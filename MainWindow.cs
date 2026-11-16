using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WorldClock
{
    class MainWindow : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        readonly TrayContext app;
        AppSettings settings;
        Palette pal;
        protected readonly float S;

        // layout in unscaled pixels
        const float HeaderH = 46, Pad = 14, CardW = 240, CardH = 150, Gap = 12, MinCW = 492, MinCH = 312;
        float cw, ch;
        readonly List<RectangleF> cards = new List<RectangleF>();   // content coordinates, scaled

        // view state
        int view;            // 0 = all clocks, 1 = one clock large
        int selected = -1;
        int hoverCard = -1, hoverBtn = -1, pressBtn = -1;

        // transition
        bool animating, expanding;
        readonly Stopwatch animSw = new Stopwatch();
        Bitmap snapGrid, snapLarge;
        RectangleF fromRect;
        const double AnimMs = 380;

        // repaint bookkeeping
        readonly List<RectangleF> iconRects = new List<RectangleF>();
        bool recording;
        readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer wxTimer = new System.Windows.Forms.Timer();
        int lastSecond = -1;

        bool dwmRounded, positioned;
        public bool AllowClose;
        public int LastDeactivated = -100000;

        const int BtnBack = 0, BtnGear = 1, BtnMin = 2, BtnClose = 3;
        readonly RectangleF[] btn = new RectangleF[4];

        public MainWindow(AppSettings s, TrayContext app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "World Clock";
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var g = CreateGraphics()) S = g.DpiX / 96f;

            frame.Interval = 40;
            frame.Tick += delegate { OnFrame(); };
            wxTimer.Interval = 10 * 60 * 1000;
            wxTimer.Tick += delegate { RefreshWeather(); };
            wxTimer.Start();

            ApplySettings(s);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;          // CS_DROPSHADOW
                cp.Style |= 0x00020000 | 0x00080000;  // WS_MINIMIZEBOX | WS_SYSMENU (taskbar click minimizes)
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwm();
        }

        void ApplyDwm()
        {
            try
            {
                int round = 2;
                dwmRounded = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0;   // Windows 11 rounded corners
                int border = pal.Border.R | (pal.Border.G << 8) | (pal.Border.B << 16);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);                     // border colour
            }
            catch { dwmRounded = false; }
        }

        // ================================================================ settings
        public AppSettings Settings { get { return settings; } }

        public void ApplySettings(AppSettings s)
        {
            settings = s;
            if (settings.Cities.Count == 0)
            {
                var local = new CityInfo();
                local.Name = "Local time";
                local.ZoneId = TimeZoneInfo.Local.Id;
                settings.Cities.Add(local);
            }
            pal = Palette.For(s.Theme);
            BackColor = pal.Bg;
            TopMost = s.AlwaysOnTop;
            if (animating) { animating = false; DisposeSnaps(); }
            if (view == 1 && selected >= settings.Cities.Count) view = 0;
            if (view == 0) selected = -1;
            hoverCard = -1;
            ComputeLayout();
            if (IsHandleCreated) ApplyDwm();
            RefreshWeather();
            Invalidate();
        }

        void RefreshWeather()
        {
            WeatherService.Refresh(settings.Cities, settings.Unit, delegate
            {
                try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(delegate { Invalidate(); })); } catch { }
            });
        }

        void ComputeLayout()
        {
            int n = settings.Cities.Count;
            int cols = n <= 2 ? Math.Max(1, n) : n <= 4 ? 2 : n <= 9 ? 3 : 4;
            int rows = (n + cols - 1) / cols;
            float gw = cols * CardW + (cols - 1) * Gap, gh = rows * CardH + (rows - 1) * Gap;
            cw = Math.Max(gw, MinCW);
            ch = Math.Max(gh, MinCH);
            cards.Clear();
            float y0 = (ch - gh) / 2f;
            for (int i = 0; i < n; i++)
            {
                int r = i / cols, c = i % cols;
                int inRow = Math.Min(cols, n - r * cols);
                float rowW = inRow * CardW + (inRow - 1) * Gap;
                float x0 = (cw - rowW) / 2f;
                cards.Add(new RectangleF((x0 + c * (CardW + Gap)) * S, (y0 + r * (CardH + Gap)) * S, CardW * S, CardH * S));
            }
            var size = new Size((int)Math.Ceiling((cw + 2 * Pad) * S), (int)Math.Ceiling((HeaderH + ch + Pad) * S));
            if (Size != size) Size = size;

            float bw = 42 * S, bh = 32 * S, by = 7 * S;
            btn[BtnClose] = new RectangleF(Width - 8 * S - bw, by, bw, bh);
            btn[BtnMin] = new RectangleF(btn[BtnClose].X - bw, by, bw, bh);
            btn[BtnGear] = new RectangleF(btn[BtnMin].X - bw, by, bw, bh);
            btn[BtnBack] = new RectangleF(8 * S, by, 36 * S, bh);
            if (positioned) ClampToScreen();
        }

        // ============================================================ show / hide
        public void ShowFromTray()
        {
            if (!positioned) { PlaceInitial(); positioned = true; }
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            try { SetForegroundWindow(Handle); } catch { }
            frame.Start();
            RefreshWeather();
            Invalidate();
        }

        public void HideToTray()
        {
            SavePosition();
            Hide();
            frame.Stop();
        }

        void PlaceInitial()
        {
            if (settings.HasPos)
            {
                var r = new Rectangle(settings.WinX, settings.WinY, Width, Height);
                foreach (Screen sc in Screen.AllScreens)
                    if (sc.WorkingArea.Contains(new Point(r.X + 40, r.Y + 20))) { Location = r.Location; ClampToScreen(); return; }
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(16 * S);
            Location = new Point(wa.Right - Width - m, wa.Bottom - Height - m);
        }

        void ClampToScreen()
        {
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        void SavePosition()
        {
            if (!Visible || WindowState != FormWindowState.Normal) return;
            settings.HasPos = true;
            settings.WinX = Left;
            settings.WinY = Top;
            Store.Save(settings);
        }

        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SavePosition(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)   // minimized from the taskbar -> go to tray
                BeginInvoke(new MethodInvoker(delegate { Hide(); frame.Stop(); WindowState = FormWindowState.Normal; }));
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            LastDeactivated = Environment.TickCount;
            if (hoverBtn != -1 || hoverCard != -1) { hoverBtn = hoverCard = -1; Invalidate(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                CloseButton();
                return;
            }
            SavePosition();
            base.OnFormClosing(e);
        }

        void CloseButton()
        {
            if (settings.CloseToTray) HideToTray();
            else if (app != null) app.ExitApp();
        }

        // ================================================================= frame
        void OnFrame()
        {
            if (!Visible) return;
            if (animating)
            {
                if (animSw.Elapsed.TotalMilliseconds >= AnimMs) FinishAnim();
                Invalidate();
                return;
            }
            int sec = DateTime.Now.Second;
            if (sec != lastSecond) { lastSecond = sec; Invalidate(); return; }
            foreach (RectangleF r in iconRects)
                Invalidate(Rectangle.Round(RectangleF.Inflate(r, 4 * S, 4 * S)));
        }

        // =========================================================== transitions
        void Expand(int idx)
        {
            if (animating) return;
            hoverCard = -1;
            snapGrid = Snap(false);
            selected = idx;
            snapLarge = Snap(true);
            fromRect = cards[idx];
            expanding = true;
            view = 1;
            BeginAnim();
        }

        void Collapse()
        {
            if (animating || view != 1) return;
            snapLarge = Snap(true);
            snapGrid = Snap(false);
            fromRect = cards[selected];
            expanding = false;
            view = 0;
            BeginAnim();
        }

        void BeginAnim()
        {
            animating = true;
            hoverBtn = -1;
            Cursor = Cursors.Default;
            animSw.Reset();
            animSw.Start();
            if (!frame.Enabled) frame.Start();
            Invalidate();
        }

        void FinishAnim()
        {
            animating = false;
            if (!expanding) selected = -1;
            DisposeSnaps();
            Invalidate();
        }

        void DisposeSnaps()
        {
            if (snapGrid != null) { snapGrid.Dispose(); snapGrid = null; }
            if (snapLarge != null) { snapLarge.Dispose(); snapLarge = null; }
        }

        Bitmap Snap(bool large)
        {
            var b = new Bitmap(Math.Max(1, (int)Math.Ceiling(cw * S)), Math.Max(1, (int)Math.Ceiling(ch * S)), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                Painter.Prep(g);
                g.Clear(pal.Bg);
                bool rec = recording;
                recording = false;
                if (large) RenderLarge(g, selected);
                else RenderGrid(g, false, new RectangleF(0, 0, b.Width, b.Height));
                recording = rec;
            }
            return b;
        }

        static float Ease(double p) { return (float)(p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2); }

        static RectangleF Lerp(RectangleF a, RectangleF b, float k)
        {
            return new RectangleF(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k,
                                  a.Width + (b.Width - a.Width) * k, a.Height + (b.Height - a.Height) * k);
        }

        void DrawTransition(Graphics g, float ox, float oy)
        {
            if (snapGrid == null || snapLarge == null) return;
            GraphicsState st = g.Save();
            g.SetClip(new RectangleF(ox, oy, cw * S, ch * S));
            double p = Math.Min(1.0, animSw.Elapsed.TotalMilliseconds / AnimMs);
            float k = Ease(p);
            if (!expanding) k = 1 - k;   // k: 0 = grid, 1 = large
            var full = new RectangleF(0, 0, cw * S, ch * S);

            // the other clocks fade out and drift away from the chosen one
            float grow = 1 + 0.08f * k;
            float fcx = fromRect.X + fromRect.Width / 2, fcy = fromRect.Y + fromRect.Height / 2;
            var gridDst = new RectangleF(fcx - fcx * grow, fcy - fcy * grow, full.Width * grow, full.Height * grow);
            g.InterpolationMode = InterpolationMode.Bilinear;
            gridDst.Offset(ox, oy);
            DrawImageAlpha(g, snapGrid, gridDst, 1 - k);

            // the chosen clock grows from its card to fill the window
            RectangleF largeDst = Lerp(fromRect, full, k);
            largeDst.Offset(ox, oy);
            DrawImageAlpha(g, snapLarge, largeDst, Math.Min(1f, k * 2.4f));
            g.Restore(st);
        }

        static void DrawImageAlpha(Graphics g, Image img, RectangleF dst, float alpha)
        {
            if (alpha <= 0.01f) return;
            using (var ia = new ImageAttributes())
            {
                var cm = new ColorMatrix();
                cm.Matrix33 = Math.Min(1f, alpha);
                ia.SetColorMatrix(cm);
                PointF[] pts = { dst.Location, new PointF(dst.Right, dst.Top), new PointF(dst.Left, dst.Bottom) };
                g.DrawImage(img, pts, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
        }

        // =================================================================== paint
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Painter.Prep(g);
            g.Clear(pal.Bg);
            bool full = e.ClipRectangle.Contains(ClientRectangle);
            float ox = Pad * S, oy = HeaderH * S;

            DrawHeader(g);

            if (full) iconRects.Clear();
            recording = full;
            GraphicsState st = g.Save();
            g.TranslateTransform(ox, oy);
            var clip = new RectangleF(e.ClipRectangle.X - ox, e.ClipRectangle.Y - oy, e.ClipRectangle.Width, e.ClipRectangle.Height);
            if (view == 1 && selected >= 0 && !animating) RenderLarge(g, selected);
            else if (!animating) RenderGrid(g, true, clip);
            g.Restore(st);
            if (animating) DrawTransition(g, ox, oy);
            recording = false;

            if (!dwmRounded)
                using (var p = new Pen(pal.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        void AddIcon(RectangleF r)
        {
            if (recording) iconRects.Add(new RectangleF(r.X + Pad * S, r.Y + HeaderH * S, r.Width, r.Height));
        }

        void DrawHeader(Graphics g)
        {
            bool back = view == 1;
            float tx = 18 * S;
            if (back)
            {
                DrawButton(g, BtnBack);
                tx = btn[BtnBack].Right + 4 * S;
            }
            string title = back && selected >= 0 ? "All clocks" : "World Clock";
            using (Font f = Painter.Ui(14 * S, FontStyle.Bold))
                Painter.Text(g, title, f, back ? pal.Gray : pal.Ink, tx, (HeaderH * S - f.Size * 1.25f) / 2f + 1 * S);
            DrawButton(g, BtnGear);
            DrawButton(g, BtnMin);
            DrawButton(g, BtnClose);
        }

        void DrawButton(Graphics g, int which)
        {
            RectangleF r = btn[which];
            Color ink = pal.Ink;
            if (hoverBtn == which)
            {
                if (which == BtnClose) { Painter.FillRound(g, r, 6 * S, Color.FromArgb(232, 17, 35)); ink = Color.White; }
                else Painter.FillRound(g, r, 6 * S, pal.CardHover);
            }
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var p = new Pen(ink, 1.3f * S))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                switch (which)
                {
                    case BtnBack:
                        g.DrawLines(p, new[] { new PointF(cx + 3 * S, cy - 6 * S), new PointF(cx - 3 * S, cy), new PointF(cx + 3 * S, cy + 6 * S) });
                        break;
                    case BtnMin:
                        g.DrawLine(p, cx - 5.5f * S, cy, cx + 5.5f * S, cy);
                        break;
                    case BtnClose:
                        g.DrawLine(p, cx - 5 * S, cy - 5 * S, cx + 5 * S, cy + 5 * S);
                        g.DrawLine(p, cx + 5 * S, cy - 5 * S, cx - 5 * S, cy + 5 * S);
                        break;
                    case BtnGear:
                        using (var tp = new Pen(ink, 2.4f * S))
                            for (int i = 0; i < 8; i++)
                            {
                                double a = i * Math.PI / 4;
                                float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
                                g.DrawLine(tp, cx + ca * 4.5f * S, cy + sa * 4.5f * S, cx + ca * 7.2f * S, cy + sa * 7.2f * S);
                            }
                        g.DrawEllipse(p, cx - 5 * S, cy - 5 * S, 10 * S, 10 * S);
                        Color hole = hoverBtn == which ? pal.CardHover : pal.Bg;
                        using (var b = new SolidBrush(hole)) g.FillEllipse(b, cx - 4.3f * S, cy - 4.3f * S, 8.6f * S, 8.6f * S);
                        g.DrawEllipse(p, cx - 2.2f * S, cy - 2.2f * S, 4.4f * S, 4.4f * S);
                        break;
                }
            }
        }

        string TimeShort(DateTime t) { return t.ToString(settings.Use24h ? "HH:mm" : "h:mm tt", Painter.Inv); }
        string TimeLong(DateTime t) { return t.ToString(settings.Use24h ? "HH:mm:ss" : "h:mm:ss tt", Painter.Inv); }

        void DigitalParts(DateTime t, bool seconds, out string main, out string suffix)
        {
            main = t.ToString(settings.Use24h ? (seconds ? "HH:mm:ss" : "HH:mm") : (seconds ? "hh:mm:ss" : "hh:mm"), Painter.Inv);
            suffix = settings.Use24h ? "" : t.ToString("tt", Painter.Inv).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- grid
        void RenderGrid(Graphics g, bool live, RectangleF clip)
        {
            DateTime utc = DateTime.UtcNow;
            for (int i = 0; i < cards.Count && i < settings.Cities.Count; i++)
            {
                RectangleF r = cards[i];
                if (!clip.IntersectsWith(r)) continue;
                Color bg = (live && i == hoverCard) ? pal.CardHover : pal.Card;
                Painter.FillRound(g, r, 20 * S, bg);
                if (settings.Digital) DigitalCard(g, settings.Cities[i], r, bg, utc);
                else AnalogCard(g, settings.Cities[i], r, bg, utc);
            }
        }

        void AnalogCard(Graphics g, CityInfo c, RectangleF r, Color bg, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            float D = 100 * S;
            var face = new RectangleF(r.X + 18 * S, r.Y + (r.Height - D) / 2f, D, D);
            Painter.DrawClock(g, face, t, false, S, pal);

            float tx = face.Right + 16 * S, tw = r.Right - tx - 10 * S, y = r.Y + 20 * S;
            float nameX = tx;
            if (c.IsLocal())
            {
                Painter.LocationArrow(g, tx, y + 6 * S, 10 * S, pal.Gray);
                nameX += 14 * S;
            }
            using (Font f = Painter.Fit(g, c.Name, FontStyle.Bold, 19 * S, 12 * S, r.Right - nameX - 10 * S))
                Painter.Text(g, c.Name, f, pal.Ink, nameX, y, r.Right - nameX - 10 * S);
            using (Font f = Painter.Ui(15 * S))
                Painter.Text(g, TimeShort(t), f, pal.Ink, tx, y + 31 * S, tw);
            using (Font f = Painter.Ui(12 * S))
                Painter.Text(g, Painter.DayWord(t) + ", " + Painter.DiffShort(off, utc), f, pal.Gray, tx, y + 53 * S, tw);

            WeatherInfo w = WeatherService.Get(c, settings.Unit);
            if (w != null)
            {
                var ir = new RectangleF(tx - 3 * S, y + 76 * S, 30 * S, 30 * S);
                Painter.DrawWeather(g, ir, w, pal, bg);
                AddIcon(ir);
                using (Font f = Painter.Ui(16 * S, FontStyle.Bold))
                    Painter.Text(g, Painter.Temp(w.Temp, settings.Unit, false), f, pal.Ink, ir.Right + 4 * S, ir.Y + 6 * S);
            }
        }

        void DigitalCard(Graphics g, CityInfo c, RectangleF r, Color bg, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            float cx = r.X + r.Width / 2;

            using (Font f = Painter.Fit(g, c.Name, FontStyle.Regular, 16 * S, 11 * S, r.Width - 40 * S))
                CenteredName(g, c, f, cx, r.Y + 16 * S);

            string main, suf;
            DigitalParts(t, false, out main, out suf);
            Painter.DrawDigital(g, cx, r.Y + 42 * S, main, suf, 44 * S, pal.Ink);

            // bottom row: [icon] 22°  ·  Today, -2h
            WeatherInfo w = WeatherService.Get(c, settings.Unit);
            string sub = Painter.DayWord(t) + ", " + Painter.DiffShort(off, utc);
            using (Font ft = Painter.Ui(14 * S, FontStyle.Bold))
            using (Font fs = Painter.Ui(12 * S))
            {
                float iconW = 24 * S;
                string temp = w != null ? Painter.Temp(w.Temp, settings.Unit, false) : "";
                string sep = w != null ? "   ·   " : "";
                float total = (w != null ? iconW + 4 * S + Painter.Width(g, temp, ft) : 0) + Painter.Width(g, sep + sub, fs);
                float x = cx - total / 2, y = r.Y + 108 * S;
                if (w != null)
                {
                    var ir = new RectangleF(x, y - 3 * S, iconW, iconW);
                    Painter.DrawWeather(g, ir, w, pal, bg);
                    AddIcon(ir);
                    x += iconW + 4 * S;
                    Painter.Text(g, temp, ft, pal.Ink, x, y);
                    x += Painter.Width(g, temp, ft);
                }
                Painter.Text(g, sep + sub, fs, pal.Gray, x, y + 2 * S);
            }
        }

        void CenteredName(Graphics g, CityInfo c, Font f, float cx, float y)
        {
            float w = Painter.Width(g, c.Name, f);
            bool local = c.IsLocal();
            float arrow = f.Size * 0.62f, gap = f.Size * 0.3f;
            float total = w + (local ? arrow + gap : 0);
            float x = cx - total / 2;
            if (local)
            {
                Painter.LocationArrow(g, x, y + f.Size * 0.32f, arrow, pal.Gray);
                x += arrow + gap;
            }
            Painter.Text(g, c.Name, f, pal.Gray, x, y);
        }

        // ---------------------------------------------------------------- large
        void RenderLarge(Graphics g, int idx)
        {
            if (idx < 0 || idx >= settings.Cities.Count) return;
            float W = cw * S, H = ch * S;
            Painter.FillRound(g, new RectangleF(0, 0, W, H), 24 * S, pal.Card);
            CityInfo c = settings.Cities[idx];
            DateTime utc = DateTime.UtcNow;
            if (settings.Digital) LargeDigital(g, c, W, H, utc);
            else LargeAnalog(g, c, W, H, utc);
        }

        void LargeAnalog(Graphics g, CityInfo c, float W, float H, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            float D = Math.Min(H - 56 * S, 236 * S);
            var face = new RectangleF(30 * S, (H - D) / 2f, D, D);
            Painter.DrawClock(g, face, t, true, S, pal);

            float tx = face.Right + 34 * S, tw = W - tx - 22 * S;
            WeatherInfo w = WeatherService.Get(c, settings.Unit);
            float y = (H - (w != null ? 226 : 172) * S) / 2f;

            using (Font f = Painter.Ui(16 * S)) Painter.Text(g, Painter.Gmt(off), f, pal.Gray, tx, y, tw);
            y += 26 * S;
            float nameX = tx;
            if (c.IsLocal()) { Painter.LocationArrow(g, tx, y + 13 * S, 15 * S, pal.Gray); nameX += 21 * S; }
            using (Font f = Painter.Fit(g, c.Name, FontStyle.Bold, 38 * S, 18 * S, W - nameX - 22 * S))
                Painter.Text(g, c.Name, f, pal.Ink, nameX - 2 * S, y, W - nameX - 22 * S);
            y += 52 * S;
            using (Font f = Painter.Ui(25 * S)) Painter.Text(g, TimeLong(t), f, pal.Ink, tx, y, tw);
            y += 38 * S;
            using (Font f = Painter.Ui(15 * S))
            {
                string day = Painter.DayWord(t);
                string date = day == "Today" ? t.ToString("dddd, d MMMM", Painter.Inv) : day + ", " + t.ToString("ddd d MMM", Painter.Inv);
                Painter.Text(g, date, f, pal.Gray, tx, y, tw);
                y += 22 * S;
                Painter.Text(g, Painter.DiffLong(off, utc), f, pal.Gray, tx, y, tw);
            }
            y += 36 * S;
            if (w != null)
            {
                var ir = new RectangleF(tx - 4 * S, y, 48 * S, 48 * S);
                Painter.DrawWeather(g, ir, w, pal, pal.Card);
                AddIcon(ir);
                using (Font f = Painter.Ui(26 * S, FontStyle.Bold))
                    Painter.Text(g, Painter.Temp(w.Temp, settings.Unit, true), f, pal.Ink, ir.Right + 8 * S, ir.Y - 1 * S);
                using (Font f = Painter.Ui(14 * S))
                    Painter.Text(g, WeatherService.Describe(w.Code, w.IsDay), f, pal.Gray, ir.Right + 9 * S, ir.Y + 31 * S, W - ir.Right - 30 * S);
            }
        }

        void LargeDigital(Graphics g, CityInfo c, float W, float H, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            WeatherInfo w = WeatherService.Get(c, settings.Unit);
            float cx = W / 2;
            float y = (H - (w != null ? 214 : 160) * S) / 2f;

            using (Font f = Painter.Fit(g, c.Name, FontStyle.Regular, 24 * S, 14 * S, W - 60 * S))
                CenteredName(g, c, f, cx, y);
            y += 42 * S;

            string main, suf;
            DigitalParts(t, true, out main, out suf);
            float px = 86 * S;
            while (px > 30 * S && Painter.DigitalWidth(g, main, suf, px) > W - 56 * S) px -= 2 * S;
            Painter.DrawDigital(g, cx, y + (86 * S - px) / 2f, main, suf, px, pal.Ink);
            y += 102 * S;

            string line = t.ToString("ddd, d MMM", Painter.Inv) + "   ·   " + Painter.Gmt(off) + "   ·   " + Painter.DiffLong(off, utc);
            using (Font f = Painter.Ui(15 * S))
                Painter.Text(g, line, f, pal.Gray, cx - Painter.Width(g, line, f) / 2, y);
            y += 34 * S;

            if (w != null)
            {
                using (Font ft = Painter.Ui(24 * S, FontStyle.Bold))
                using (Font fc = Painter.Ui(15 * S))
                {
                    string temp = Painter.Temp(w.Temp, settings.Unit, true);
                    string cond = "   " + WeatherService.Describe(w.Code, w.IsDay);
                    float iw = 40 * S;
                    float total = iw + 8 * S + Painter.Width(g, temp, ft) + Painter.Width(g, cond, fc);
                    float x = cx - total / 2;
                    var ir = new RectangleF(x, y - 4 * S, iw, iw);
                    Painter.DrawWeather(g, ir, w, pal, pal.Card);
                    AddIcon(ir);
                    x += iw + 8 * S;
                    Painter.Text(g, temp, ft, pal.Ink, x, y);
                    x += Painter.Width(g, temp, ft);
                    Painter.Text(g, cond, fc, pal.Gray, x, y + 8 * S);
                }
            }
        }

        // ================================================================= mouse
        int HitBtn(Point p)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i == BtnBack && view != 1) continue;
                if (btn[i].Contains(p)) return i;
            }
            return -1;
        }

        int HitCard(Point p)
        {
            if (view != 0 || animating) return -1;
            var q = new PointF(p.X - Pad * S, p.Y - HeaderH * S);
            for (int i = 0; i < cards.Count; i++) if (cards[i].Contains(q)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int b = HitBtn(e.Location);
            int c = b >= 0 ? -1 : HitCard(e.Location);
            bool overLarge = view == 1 && !animating && e.Y > HeaderH * S;
            Cursor = (c >= 0 || overLarge || b >= 0) ? Cursors.Hand : Cursors.Default;
            if (b != hoverBtn || c != hoverCard)
            {
                hoverBtn = b;
                hoverCard = c;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverBtn != -1 || hoverCard != -1) { hoverBtn = hoverCard = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int b = HitBtn(e.Location);
            if (b >= 0) { pressBtn = b; return; }
            if (e.Y < HeaderH * S)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // drag the window by its header
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                if (app != null) app.ShowMenu(this, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (pressBtn >= 0)
            {
                int b = HitBtn(e.Location);
                int pressed = pressBtn;
                pressBtn = -1;
                if (b == pressed) DoButton(b);
                return;
            }
            if (e.Y < HeaderH * S || animating) return;
            if (view == 0)
            {
                int c = HitCard(e.Location);
                if (c >= 0) Expand(c);
            }
            else Collapse();
        }

        void DoButton(int b)
        {
            hoverBtn = -1;
            switch (b)
            {
                case BtnBack: Collapse(); break;
                case BtnGear: if (app != null) app.OpenSettings(); break;
                case BtnMin: HideToTray(); break;
                case BtnClose: CloseButton(); break;
            }
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back) Collapse();
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                frame.Dispose();
                wxTimer.Dispose();
                DisposeSnaps();
            }
            base.Dispose(disposing);
        }
    }
}
