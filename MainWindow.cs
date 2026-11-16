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
    // Compact strip: one row of cities (up to 3 visible, small arrows to scroll the rest).
    // Clicking a city grows its card to fill the strip; the others slide away.
    class MainWindow : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        readonly TrayContext app;
        AppSettings settings;
        Palette pal;
        protected readonly float S;

        // ------------------------------------------------------------ layout
        struct Metrics { public float W, H, MinCW, R; }
        Metrics m;
        const float HeaderH = 26, Pad = 8, Gap = 8, GutterW = 18;
        float gutter, cw, ch, rowX;
        int visible;
        bool Paging { get { return settings.Cities.Count > visible; } }
        int MaxScroll { get { return Math.Max(0, settings.Cities.Count - visible); } }
        string Size3 { get { return settings.Size; } }

        // ------------------------------------------------------------ state
        int view;                 // 0 = row, 1 = one city enlarged
        int selected = -1;
        int hoverCard = -1, hoverBtn = -1, hoverArrow = 0, pressBtn = -1;
        Point downPt;
        bool mouseDown;

        float scroll;             // index of first visible city (animated)
        float scrollFrom;
        int scrollTo;
        bool scrolling;
        readonly Stopwatch scrollSw = new Stopwatch();
        const double ScrollMs = 260;

        bool animating, expanding;
        readonly Stopwatch animSw = new Stopwatch();
        Bitmap snapOthers, snapCard, snapLarge;
        RectangleF fromRect;
        const double AnimMs = 330;

        readonly List<RectangleF> iconRects = new List<RectangleF>();
        bool recording;
        readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer wxTimer = new System.Windows.Forms.Timer();
        int lastSecond = -1;
        bool fast, hiRes;

        bool dwmRounded, positioned;
        public bool AllowClose;
        public int LastDeactivated = -100000;

        const int BtnBack = 0, BtnGear = 1, BtnMin = 2, BtnClose = 3;
        readonly RectangleF[] btn = new RectangleF[4];
        RectangleF arrowL, arrowR;

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
                cp.Style |= 0x00020000 | 0x00080000;  // WS_MINIMIZEBOX | WS_SYSMENU
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
                dwmRounded = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0;
                int border = pal.Border.R | (pal.Border.G << 8) | (pal.Border.B << 16);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { dwmRounded = false; }
        }

        // ============================================================ settings
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
            scrolling = false;
            view = 0;
            selected = -1;
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

        static Metrics Mk(float w, float h, float minCw, float r)
        {
            var x = new Metrics();
            x.W = w; x.H = h; x.MinCW = minCw; x.R = r;
            return x;
        }

        void ComputeLayout()
        {
            if (settings.Digital)
                m = Size3 == "mini" ? Mk(104, 60, 210, 12) : Size3 == "normal" ? Mk(138, 106, 270, 16) : Mk(116, 82, 236, 14);
            else
                m = Size3 == "mini" ? Mk(84, 106, 236, 12) : Size3 == "normal" ? Mk(122, 182, 310, 16) : Mk(98, 140, 260, 14);

            int n = settings.Cities.Count;
            visible = Math.Min(Math.Max(1, settings.Visible), n);
            gutter = Paging ? GutterW : 0;
            float rowW = visible * m.W + (visible - 1) * Gap;
            cw = Math.Max(rowW, m.MinCW);
            ch = m.H;
            rowX = (cw - rowW) / 2f;
            scrollTo = Math.Min(Math.Max(0, scrollTo), MaxScroll);
            scroll = scrollTo;

            var size = new Size((int)Math.Ceiling((cw + 2 * (Pad + gutter)) * S), (int)Math.Ceiling((HeaderH + ch + Pad) * S));
            if (Size != size) Size = size;

            float bw = 26 * S, bh = 20 * S, by = 3 * S;
            btn[BtnClose] = new RectangleF(Width - 4 * S - bw, by, bw, bh);
            btn[BtnMin] = new RectangleF(btn[BtnClose].X - bw, by, bw, bh);
            btn[BtnGear] = new RectangleF(btn[BtnMin].X - bw, by, bw, bh);
            btn[BtnBack] = new RectangleF(4 * S, by, 24 * S, bh);
            arrowL = new RectangleF(Pad * S - 4 * S, HeaderH * S, gutter * S + 2 * S, ch * S);
            arrowR = new RectangleF(Width - (Pad + gutter) * S + 2 * S, HeaderH * S, gutter * S + 2 * S, ch * S);
            if (positioned) ClampToScreen();
        }

        float OX { get { return (Pad + gutter) * S; } }
        float OY { get { return HeaderH * S; } }
        RectangleF Full { get { return new RectangleF(0, 0, cw * S, ch * S); } }

        RectangleF CardRect(int i)
        {
            return new RectangleF((rowX + (i - scroll) * (m.W + Gap)) * S, 0, m.W * S, m.H * S);
        }

        // ========================================================= show / hide
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
            SetFast(false);
        }

        void PlaceInitial()
        {
            if (settings.HasPos)
            {
                foreach (Screen sc in Screen.AllScreens)
                    if (sc.WorkingArea.Contains(new Point(settings.WinX + 40, settings.WinY + 10)))
                    {
                        Location = new Point(settings.WinX, settings.WinY);
                        ClampToScreen();
                        return;
                    }
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int mg = (int)(16 * S);
            Location = new Point(wa.Right - Width - mg, wa.Bottom - Height - mg);
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
            if (WindowState == FormWindowState.Minimized)
                BeginInvoke(new MethodInvoker(delegate { Hide(); frame.Stop(); SetFast(false); WindowState = FormWindowState.Normal; }));
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            LastDeactivated = Environment.TickCount;
            ClearHover();
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
            SetFast(false);
            base.OnFormClosing(e);
        }

        void CloseButton()
        {
            if (settings.CloseToTray) HideToTray();
            else if (app != null) app.ExitApp();
        }

        // ============================================================== frames
        // ~60 fps while something moves, otherwise once a second (+ weather icons at 25 fps)
        void SetFast(bool on)
        {
            if (on == fast) return;
            fast = on;
            frame.Interval = on ? 15 : 40;
            try
            {
                if (on && !hiRes) { timeBeginPeriod(1); hiRes = true; }
                if (!on && hiRes) { timeEndPeriod(1); hiRes = false; }
            }
            catch { }
        }

        void OnFrame()
        {
            if (!Visible) return;
            bool moving = false;
            if (scrolling)
            {
                double t = Math.Min(1.0, scrollSw.Elapsed.TotalMilliseconds / ScrollMs);
                scroll = scrollFrom + (scrollTo - scrollFrom) * EaseOut(t);
                if (t >= 1) { scroll = scrollTo; scrolling = false; }
                moving = true;
            }
            if (animating)
            {
                if (animSw.Elapsed.TotalMilliseconds >= AnimMs) FinishAnim();
                moving = true;
            }
            if (moving || fast)
            {
                Invalidate();
                Update();
                if (!scrolling && !animating) SetFast(false);
                return;
            }
            int sec = DateTime.Now.Second;
            if (sec != lastSecond) { lastSecond = sec; Invalidate(); return; }
            foreach (RectangleF r in iconRects)
                Invalidate(Rectangle.Round(RectangleF.Inflate(r, 3 * S, 3 * S)));
        }

        static float EaseOut(double p) { return (float)(1 - Math.Pow(1 - p, 3)); }
        static float EaseInOut(double p) { return (float)(p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2); }
        static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        static RectangleF Lerp(RectangleF a, RectangleF b, float k)
        {
            return new RectangleF(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k,
                                  a.Width + (b.Width - a.Width) * k, a.Height + (b.Height - a.Height) * k);
        }

        // ============================================================= scroll
        void ScrollBy(int dir)
        {
            if (animating || view != 0) return;
            int target = Math.Min(MaxScroll, Math.Max(0, scrollTo + dir));
            if (target == scrollTo) return;
            scrollFrom = scroll;
            scrollTo = target;
            scrollSw.Reset();
            scrollSw.Start();
            scrolling = true;
            hoverCard = -1;
            SetFast(true);
            if (!frame.Enabled) frame.Start();
        }

        // ========================================================= transitions
        void Expand(int idx)
        {
            if (animating || scrolling) return;
            hoverCard = -1;
            selected = idx;
            fromRect = CardRect(idx);
            snapOthers = SnapRow(idx);
            snapCard = SnapCard(idx);
            snapLarge = SnapLarge(idx);
            expanding = true;
            view = 1;
            BeginAnim();
        }

        void Collapse()
        {
            if (animating || view != 1 || selected < 0) return;
            // make sure the city we return to is on screen
            if (selected < scrollTo || selected >= scrollTo + visible)
                scrollTo = Math.Min(MaxScroll, Math.Max(0, selected - (visible - 1) / 2));
            scroll = scrollTo;
            fromRect = CardRect(selected);
            snapOthers = SnapRow(selected);
            snapCard = SnapCard(selected);
            snapLarge = SnapLarge(selected);
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
            SetFast(true);
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
            if (snapOthers != null) { snapOthers.Dispose(); snapOthers = null; }
            if (snapCard != null) { snapCard.Dispose(); snapCard = null; }
            if (snapLarge != null) { snapLarge.Dispose(); snapLarge = null; }
        }

        Bitmap NewSnap(float w, float h, out Graphics g)
        {
            var b = new Bitmap(Math.Max(1, (int)Math.Ceiling(w)), Math.Max(1, (int)Math.Ceiling(h)), PixelFormat.Format32bppPArgb);
            g = Graphics.FromImage(b);
            Painter.Prep(g);
            g.Clear(Color.Transparent);
            return b;
        }

        Bitmap SnapRow(int skip)
        {
            Graphics g;
            Bitmap b = NewSnap(cw * S, ch * S, out g);
            using (g) RenderRow(g, false, Full, skip);
            return b;
        }

        Bitmap SnapCard(int idx)
        {
            Graphics g;
            Bitmap b = NewSnap(m.W * S, m.H * S, out g);
            using (g) CardContent(g, settings.Cities[idx], new RectangleF(0, 0, m.W * S, m.H * S), pal.Card, DateTime.UtcNow);
            return b;
        }

        Bitmap SnapLarge(int idx)
        {
            Graphics g;
            Bitmap b = NewSnap(cw * S, ch * S, out g);
            using (g) LargeContent(g, idx);
            return b;
        }

        // The chosen card's background grows to fill the strip, its small content fades out,
        // the enlarged content fades in, and the other cards fade while sliding outward.
        void DrawTransition(Graphics g, float ox, float oy)
        {
            if (snapOthers == null || snapCard == null || snapLarge == null) return;
            double p = Math.Min(1.0, animSw.Elapsed.TotalMilliseconds / AnimMs);
            float e = EaseInOut(p);
            float k = expanding ? e : 1 - e;
            RectangleF full = Full;
            RectangleF morph = Lerp(fromRect, full, k);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // other cards: split at the chosen card and push each side outward
            float aO = Clamp01(1 - k * 1.6f);
            if (aO > 0)
            {
                float split = fromRect.X + fromRect.Width / 2;
                float shift = 34 * S * k;
                DrawPart(g, snapOthers, new RectangleF(0, 0, split, full.Height), ox - shift, oy, aO);
                DrawPart(g, snapOthers, new RectangleF(split, 0, full.Width - split, full.Height), ox + split + shift, oy, aO);
            }

            var morphW = new RectangleF(morph.X + ox, morph.Y + oy, morph.Width, morph.Height);
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Painter.FillRound(g, morphW, m.R * S, pal.Card);
            g.PixelOffsetMode = PixelOffsetMode.Half;

            GraphicsState st = g.Save();
            g.SetClip(morphW, CombineMode.Intersect);
            float aC = Clamp01(1 - k * 2.4f);
            if (aC > 0)
            {
                float frac = full.Width - fromRect.Width > 1 ? Clamp01(fromRect.X / (full.Width - fromRect.Width)) : 0;
                float cx = morphW.X + (morphW.Width - snapCard.Width) * frac;   // small content stays put while the card grows
                DrawPart(g, snapCard, new RectangleF(0, 0, snapCard.Width, snapCard.Height), cx, morphW.Y, aC);
            }
            float aL = Clamp01((k - 0.32f) / 0.68f);
            if (aL > 0)
                DrawPart(g, snapLarge, new RectangleF(0, 0, snapLarge.Width, snapLarge.Height), ox, oy, aL);
            g.Restore(st);
        }

        static void DrawPart(Graphics g, Image img, RectangleF src, float x, float y, float alpha)
        {
            if (alpha <= 0.01f || src.Width <= 0) return;
            using (var ia = new ImageAttributes())
            {
                var cm = new ColorMatrix();
                cm.Matrix33 = Math.Min(1f, alpha);
                ia.SetColorMatrix(cm);
                PointF[] pts = { new PointF(x, y), new PointF(x + src.Width, y), new PointF(x, y + src.Height) };
                g.DrawImage(img, pts, src, GraphicsUnit.Pixel, ia);
            }
        }

        // =============================================================== paint
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Painter.Prep(g);
            g.Clear(pal.Bg);
            bool full = e.ClipRectangle.Contains(ClientRectangle);
            float ox = OX, oy = OY;

            DrawHeader(g);
            if (Paging && view == 0 && !animating) DrawArrows(g);

            if (full) iconRects.Clear();
            GraphicsState st = g.Save();
            g.SetClip(new RectangleF(ox, oy, cw * S, ch * S), CombineMode.Intersect);
            if (animating)
            {
                DrawTransition(g, ox, oy);
            }
            else
            {
                recording = full;
                g.TranslateTransform(ox, oy);
                var clip = new RectangleF(e.ClipRectangle.X - ox, e.ClipRectangle.Y - oy, e.ClipRectangle.Width, e.ClipRectangle.Height);
                if (view == 1 && selected >= 0)
                {
                    Painter.FillRound(g, Full, m.R * S, pal.Card);
                    LargeContent(g, selected);
                }
                else
                {
                    RenderRow(g, true, clip, -1);
                    if (scrolling) EdgeFade(g);
                }
                recording = false;
            }
            g.Restore(st);

            if (!dwmRounded)
                using (var p = new Pen(pal.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        void AddIcon(RectangleF r)
        {
            if (recording) iconRects.Add(new RectangleF(r.X + OX, r.Y + OY, r.Width, r.Height));
        }

        void DrawHeader(Graphics g)
        {
            float tx = 10 * S;
            if (view == 1)
            {
                DrawButton(g, BtnBack);
                tx = btn[BtnBack].Right;
            }
            string title = view == 1 && selected >= 0 ? settings.Cities[selected].Name : "World Clock";
            using (Font f = Painter.Ui(11 * S, FontStyle.Bold))
                Painter.Text(g, title, f, pal.Gray, tx, (HeaderH * S - f.Size * 1.25f) / 2f + 1 * S, btn[BtnGear].X - tx - 4 * S);
            DrawButton(g, BtnGear);
            DrawButton(g, BtnMin);
            DrawButton(g, BtnClose);
        }

        void DrawButton(Graphics g, int which)
        {
            RectangleF r = btn[which];
            Color ink = pal.Gray;
            if (hoverBtn == which)
            {
                if (which == BtnClose) { Painter.FillRound(g, r, 5 * S, Color.FromArgb(232, 17, 35)); ink = Color.White; }
                else { Painter.FillRound(g, r, 5 * S, pal.CardHover); ink = pal.Ink; }
            }
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var p = new Pen(ink, 1.2f * S))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                switch (which)
                {
                    case BtnBack:
                        g.DrawLines(p, new[] { new PointF(cx + 2.5f * S, cy - 4.5f * S), new PointF(cx - 2f * S, cy), new PointF(cx + 2.5f * S, cy + 4.5f * S) });
                        break;
                    case BtnMin:
                        g.DrawLine(p, cx - 4.5f * S, cy, cx + 4.5f * S, cy);
                        break;
                    case BtnClose:
                        g.DrawLine(p, cx - 4 * S, cy - 4 * S, cx + 4 * S, cy + 4 * S);
                        g.DrawLine(p, cx + 4 * S, cy - 4 * S, cx - 4 * S, cy + 4 * S);
                        break;
                    case BtnGear:
                        using (var tp = new Pen(ink, 1.9f * S))
                            for (int i = 0; i < 8; i++)
                            {
                                double a = i * Math.PI / 4;
                                float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
                                g.DrawLine(tp, cx + ca * 3.6f * S, cy + sa * 3.6f * S, cx + ca * 5.6f * S, cy + sa * 5.6f * S);
                            }
                        Color hole = hoverBtn == which ? pal.CardHover : pal.Bg;
                        using (var b = new SolidBrush(ink)) g.FillEllipse(b, cx - 4.2f * S, cy - 4.2f * S, 8.4f * S, 8.4f * S);
                        using (var b = new SolidBrush(hole)) g.FillEllipse(b, cx - 1.8f * S, cy - 1.8f * S, 3.6f * S, 3.6f * S);
                        break;
                }
            }
        }

        void DrawArrows(Graphics g)
        {
            DrawArrow(g, arrowL, -1, scrollTo > 0);
            DrawArrow(g, arrowR, 1, scrollTo < MaxScroll);
        }

        void DrawArrow(Graphics g, RectangleF r, int dir, bool enabled)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            bool hot = enabled && hoverArrow == dir;
            if (hot) Painter.FillRound(g, new RectangleF(cx - 8 * S, cy - 13 * S, 16 * S, 26 * S), 6 * S, pal.CardHover);
            Color c = !enabled ? Color.FromArgb(60, pal.Gray) : hot ? pal.Ink : pal.Gray;
            using (var p = new Pen(c, 1.5f * S))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float d = 2.5f * S * dir;
                g.DrawLines(p, new[] { new PointF(cx - d, cy - 5 * S), new PointF(cx + d, cy), new PointF(cx - d, cy + 5 * S) });
            }
        }

        // ================================================================ row
        void RenderRow(Graphics g, bool live, RectangleF clip, int skip)
        {
            DateTime utc = DateTime.UtcNow;
            for (int i = 0; i < settings.Cities.Count; i++)
            {
                if (i == skip) continue;
                RectangleF r = CardRect(i);
                if (r.Right < 0 || r.X > cw * S || !clip.IntersectsWith(r)) continue;
                Color bg = (live && i == hoverCard) ? pal.CardHover : pal.Card;
                Painter.FillRound(g, r, m.R * S, bg);
                CardContent(g, settings.Cities[i], r, bg, utc);
            }
        }

        // soft fade at both ends of the row while it slides
        void EdgeFade(Graphics g)
        {
            float w = 14 * S, H = ch * S, W = cw * S;
            using (var lb = new LinearGradientBrush(new RectangleF(-1, 0, w + 1, H), pal.Bg, Color.FromArgb(0, pal.Bg), LinearGradientMode.Horizontal))
                g.FillRectangle(lb, 0, 0, w, H);
            using (var rb = new LinearGradientBrush(new RectangleF(W - w, 0, w + 1, H), Color.FromArgb(0, pal.Bg), pal.Bg, LinearGradientMode.Horizontal))
                g.FillRectangle(rb, W - w, 0, w, H);
        }

        string TimeShort(DateTime t) { return t.ToString(settings.Use24h ? "HH:mm" : "h:mm tt", Painter.Inv); }
        string TimeLong(DateTime t) { return t.ToString(settings.Use24h ? "HH:mm:ss" : "h:mm:ss tt", Painter.Inv); }

        void DigitalParts(DateTime t, bool seconds, out string main, out string suffix)
        {
            main = t.ToString(settings.Use24h ? (seconds ? "HH:mm:ss" : "HH:mm") : (seconds ? "hh:mm:ss" : "hh:mm"), Painter.Inv);
            suffix = settings.Use24h ? "" : t.ToString("tt", Painter.Inv).ToLowerInvariant();
        }

        static string DayShort(DateTime t)
        {
            string d = Painter.DayWord(t);
            return d == "Today" ? "" : d == "Tomorrow" ? "Tmrw" : d == "Yesterday" ? "Yday" : d;
        }

        void CardContent(Graphics g, CityInfo c, RectangleF r, Color bg, DateTime utc)
        {
            if (settings.Digital) DigitalCard(g, c, r, bg, utc);
            else AnalogCard(g, c, r, bg, utc);
        }

        // centred "[icon] 22°  extra" row
        void WeatherRow(Graphics g, CityInfo c, Color bg, float cx, float y, float iconPx, float tempPx, string extra, float extraPx)
        {
            WeatherInfo w = WeatherService.Get(c, settings.Unit);
            using (Font ft = Painter.Ui(tempPx, FontStyle.Bold))
            using (Font fx = Painter.Ui(extraPx))
            {
                string temp = w != null ? Painter.Temp(w.Temp, settings.Unit, false) : "";
                if (w == null && string.IsNullOrEmpty(extra)) return;
                string ex = string.IsNullOrEmpty(extra) ? "" : (w != null ? "  ·  " : "") + extra;
                float total = (w != null ? iconPx + 3 * S + Painter.Width(g, temp, ft) : 0) + (ex.Length > 0 ? Painter.Width(g, ex, fx) : 0);
                float x = cx - total / 2;
                if (w != null)
                {
                    var ir = new RectangleF(x, y - iconPx * 0.18f, iconPx, iconPx);
                    Painter.DrawWeather(g, ir, w, pal, bg);
                    AddIcon(ir);
                    x += iconPx + 3 * S;
                    Painter.Text(g, temp, ft, pal.Ink, x, y);
                    x += Painter.Width(g, temp, ft);
                }
                if (ex.Length > 0) Painter.Text(g, ex, fx, pal.Gray, x, y + (tempPx - extraPx) * 0.55f);
            }
        }

        void CenteredText(Graphics g, string s, float px, FontStyle st, Color c, float cx, float y, float maxW)
        {
            using (Font f = Painter.Fit(g, s, st, px, px * 0.75f, maxW))
            {
                float w = Math.Min(maxW, Painter.Width(g, s, f));
                Painter.Text(g, s, f, c, cx - w / 2, y, maxW);
            }
        }

        void CityName(Graphics g, CityInfo c, float px, FontStyle st, Color col, float cx, float y, float maxW)
        {
            bool local = c.IsLocal();
            using (Font f = Painter.Fit(g, c.Name, st, px, px * 0.75f, maxW - (local ? px : 0)))
            {
                float w = Math.Min(maxW, Painter.Width(g, c.Name, f));
                float arrow = local ? f.Size * 0.6f : 0, gap = local ? f.Size * 0.3f : 0;
                float x = cx - (w + arrow + gap) / 2;
                if (local) Painter.LocationArrow(g, x, y + f.Size * 0.33f, arrow, pal.Gray);
                Painter.Text(g, c.Name, f, col, x + arrow + gap, y, maxW);
            }
        }

        void AnalogCard(Graphics g, CityInfo c, RectangleF r, Color bg, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            float cx = r.X + r.Width / 2, inner = r.Width - 10 * S;
            string sz = Size3;
            float D = (sz == "mini" ? 56 : sz == "normal" ? 80 : 62) * S;
            float y = r.Y + (sz == "normal" ? 12 : 10) * S;
            Painter.DrawClock(g, new RectangleF(cx - D / 2, y, D, D), t, false, S, pal);
            y += D + (sz == "normal" ? 9 : 7) * S;

            float namePx = (sz == "normal" ? 14 : sz == "small" ? 12.5f : 12) * S;
            CityName(g, c, namePx, FontStyle.Bold, pal.Ink, cx, y, inner);
            y += namePx * 1.35f;
            string time = TimeShort(t);
            string day = DayShort(t);
            if (sz != "normal" && day.Length > 0) time += " " + day;
            CenteredText(g, time, (sz == "normal" ? 12.5f : 11) * S, FontStyle.Regular, sz == "normal" ? pal.Ink : pal.Gray, cx, y, inner);

            if (sz == "small")
                WeatherRow(g, c, bg, cx, y + 19 * S, 16 * S, 11.5f * S, null, 10 * S);
            else if (sz == "normal")
            {
                y += 18 * S;
                string sub = (day.Length > 0 ? day + ", " : "") + Painter.DiffShort(off, utc);
                CenteredText(g, sub, 10.5f * S, FontStyle.Regular, pal.Gray, cx, y, inner);
                WeatherRow(g, c, bg, cx, y + 20 * S, 19 * S, 13 * S, null, 10 * S);
            }
        }

        void DigitalCard(Graphics g, CityInfo c, RectangleF r, Color bg, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            float cx = r.X + r.Width / 2, inner = r.Width - 12 * S;
            string sz = Size3;
            string main, suf;
            DigitalParts(t, false, out main, out suf);

            float namePx = (sz == "normal" ? 12.5f : 11) * S;
            CityName(g, c, namePx, FontStyle.Regular, pal.Gray, cx, r.Y + (sz == "normal" ? 9 : 7) * S, inner);
            float px = (sz == "mini" ? 25 : sz == "normal" ? 32 : 27) * S;
            while (px > 12 * S && Painter.DigitalWidth(g, main, suf, px) > inner) px -= 1 * S;
            Painter.DrawDigital(g, cx, r.Y + (sz == "mini" ? 23 : sz == "normal" ? 28 : 24) * S, main, suf, px, pal.Ink);

            string day = DayShort(t);
            if (sz == "small")
                WeatherRow(g, c, bg, cx, r.Y + 60 * S, 15 * S, 11 * S, day.Length > 0 ? day : null, 10 * S);
            else if (sz == "normal")
                WeatherRow(g, c, bg, cx, r.Y + 78 * S, 17 * S, 12 * S, (day.Length > 0 ? day + " " : "") + Painter.DiffShort(off, utc), 10.5f * S);
            else if (day.Length > 0)
            {
                // mini: mark a different day with a tiny tag in the corner
                using (Font f = Painter.Ui(8.5f * S)) Painter.Text(g, day, f, pal.Gray, r.Right - 6 * S - Painter.Width(g, day, f), r.Y + 5 * S);
            }
        }

        // ============================================================== large
        void LargeContent(Graphics g, int idx)
        {
            if (idx < 0 || idx >= settings.Cities.Count) return;
            CityInfo c = settings.Cities[idx];
            DateTime utc = DateTime.UtcNow;
            if (settings.Digital) LargeDigital(g, c, cw * S, ch * S, utc);
            else LargeAnalog(g, c, cw * S, ch * S, utc);
        }

        void LargeAnalog(Graphics g, CityInfo c, float W, float H, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            string sz = Size3;
            float pad = 9 * S;
            float D = H - 2 * pad;
            Painter.DrawClock(g, new RectangleF(pad + 4 * S, pad, D, D), t, sz != "mini", S, pal);
            float tx = pad + 4 * S + D + 16 * S, tw = W - tx - pad;
            WeatherInfo w = sz == "mini" ? null : WeatherService.Get(c, settings.Unit);

            float f = sz == "normal" ? 1f : sz == "small" ? 0.84f : 0.74f;
            float gmtPx = 11 * S * f, namePx = 22 * S * f, timePx = 17 * S * f, infoPx = Math.Max(10 * S, 12 * S * f);
            string day = Painter.DayWord(t);
            string date = day == "Today" ? t.ToString(sz == "normal" ? "dddd, d MMMM" : "ddd, d MMM", Painter.Inv) : day + ", " + t.ToString("ddd d MMM", Painter.Inv);

            float block = 0;
            if (sz == "normal") block += gmtPx * 1.5f;
            block += namePx * 1.3f + timePx * 1.45f + infoPx * 1.45f;
            if (sz != "mini") block += infoPx * 1.45f;
            if (w != null) block += 6 * S + infoPx * 2.2f;
            float y = (H - block) / 2f;

            if (sz == "normal")
            {
                using (Font fo = Painter.Ui(gmtPx)) Painter.Text(g, Painter.Gmt(off), fo, pal.Gray, tx, y, tw);
                y += gmtPx * 1.5f;
            }
            using (Font fo = Painter.Fit(g, c.Name, FontStyle.Bold, namePx, namePx * 0.6f, tw)) Painter.Text(g, c.Name, fo, pal.Ink, tx - 1 * S, y, tw);
            y += namePx * 1.3f;
            using (Font fo = Painter.Ui(timePx)) Painter.Text(g, TimeLong(t), fo, pal.Ink, tx, y, tw);
            y += timePx * 1.45f;
            using (Font fo = Painter.Ui(infoPx))
            {
                if (sz == "mini")
                {
                    Painter.Text(g, date + "  ·  " + Painter.DiffShort(off, utc), fo, pal.Gray, tx, y, tw);
                    y += infoPx * 1.45f;
                }
                else
                {
                    Painter.Text(g, date, fo, pal.Gray, tx, y, tw);
                    y += infoPx * 1.45f;
                    Painter.Text(g, sz == "normal" ? Painter.DiffLong(off, utc) : Painter.Gmt(off) + "  ·  " + Painter.DiffShort(off, utc), fo, pal.Gray, tx, y, tw);
                    y += infoPx * 1.45f;
                }
            }
            if (w != null)
            {
                y += 6 * S;
                float ip = infoPx * 2.0f;
                var ir = new RectangleF(tx - 2 * S, y - ip * 0.12f, ip, ip);
                Painter.DrawWeather(g, ir, w, pal, pal.Card);
                AddIcon(ir);
                float x = ir.Right + 5 * S;
                using (Font ft = Painter.Ui(infoPx * 1.3f, FontStyle.Bold))
                {
                    string temp = Painter.Temp(w.Temp, settings.Unit, true);
                    Painter.Text(g, temp, ft, pal.Ink, x, y);
                    x += Painter.Width(g, temp, ft) + 6 * S;
                }
                using (Font fo = Painter.Ui(infoPx)) Painter.Text(g, WeatherService.Describe(w.Code, w.IsDay), fo, pal.Gray, x, y + infoPx * 0.3f, W - x - pad);
            }
        }

        void LargeDigital(Graphics g, CityInfo c, float W, float H, DateTime utc)
        {
            DateTime t = c.TimeAt(utc);
            TimeSpan off = c.OffsetAt(utc);
            string sz = Size3;
            float cx = W / 2, inner = W - 24 * S;
            string main, suf;
            DigitalParts(t, true, out main, out suf);
            string date = t.ToString("ddd, d MMM", Painter.Inv);

            float namePx = (sz == "normal" ? 13 : 11) * S;
            string head = sz == "mini" ? c.Name + "   ·   " + date + "   ·   " + Painter.DiffShort(off, utc) : c.Name;
            if (sz == "mini") CenteredText(g, head, namePx, FontStyle.Regular, pal.Gray, cx, 7 * S, inner);
            else CityName(g, c, namePx, FontStyle.Regular, pal.Gray, cx, 8 * S, inner);

            float maxPx = (sz == "mini" ? 29 : sz == "normal" ? 40 : 32) * S;
            float px = maxPx;
            while (px > 12 * S && Painter.DigitalWidth(g, main, suf, px) > inner) px -= 1 * S;
            float top = (sz == "mini" ? 22 : sz == "normal" ? 26 : 23) * S + (maxPx - px) * 0.5f;
            Painter.DrawDigital(g, cx, top, main, suf, px, pal.Ink);

            if (sz == "small")
                WeatherRow(g, c, pal.Card, cx, 60 * S, 15 * S, 11 * S, date + "  ·  " + Painter.Gmt(off) + "  ·  " + Painter.DiffShort(off, utc), 10 * S);
            else if (sz == "normal")
            {
                WeatherInfo w = WeatherService.Get(c, settings.Unit);
                string ex = (w != null ? WeatherService.Describe(w.Code, w.IsDay) + "  ·  " : "") + date + "  ·  " + Painter.Gmt(off) + "  ·  " + Painter.DiffLong(off, utc);
                WeatherRow(g, c, pal.Card, cx, 78 * S, 18 * S, 13 * S, ex, 10.5f * S);
            }
        }

        // ============================================================== mouse
        int HitBtn(Point p)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i == BtnBack && view != 1) continue;
                if (btn[i].Contains(p)) return i;
            }
            return -1;
        }

        int HitArrow(Point p)
        {
            if (!Paging || view != 0 || animating) return 0;
            if (arrowL.Contains(p)) return -1;
            if (arrowR.Contains(p)) return 1;
            return 0;
        }

        bool InContent(Point p)
        {
            return new RectangleF(OX, OY, cw * S, ch * S).Contains(p);
        }

        int HitCard(Point p)
        {
            if (view != 0 || animating || scrolling || !InContent(p)) return -1;
            var q = new PointF(p.X - OX, p.Y - OY);
            for (int i = 0; i < settings.Cities.Count; i++) if (CardRect(i).Contains(q)) return i;
            return -1;
        }

        void ClearHover()
        {
            if (hoverBtn != -1 || hoverCard != -1 || hoverArrow != 0)
            {
                hoverBtn = hoverCard = -1;
                hoverArrow = 0;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mouseDown && pressBtn < 0 && (Math.Abs(e.X - downPt.X) > 4 || Math.Abs(e.Y - downPt.Y) > 4))
            {
                mouseDown = false;     // start dragging the window from anywhere
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                return;
            }
            int b = HitBtn(e.Location);
            int a = b >= 0 ? 0 : HitArrow(e.Location);
            int c = (b >= 0 || a != 0) ? -1 : HitCard(e.Location);
            bool overLarge = view == 1 && !animating && InContent(e.Location);
            Cursor = (b >= 0 || a != 0 || c >= 0 || overLarge) ? Cursors.Hand : Cursors.Default;
            if (b != hoverBtn || c != hoverCard || a != hoverArrow)
            {
                hoverBtn = b;
                hoverCard = c;
                hoverArrow = a;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            ClearHover();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int b = HitBtn(e.Location);
            if (b >= 0) { pressBtn = b; return; }
            int a = HitArrow(e.Location);
            if (a != 0) { ScrollBy(a); return; }
            mouseDown = true;
            downPt = e.Location;
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
            if (!mouseDown) return;
            mouseDown = false;
            if (animating || !InContent(e.Location)) return;
            if (view == 0)
            {
                int c = HitCard(e.Location);
                if (c >= 0) Expand(c);
            }
            else Collapse();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (view == 0) ScrollBy(e.Delta > 0 ? -1 : 1);
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
            else if (e.KeyCode == Keys.Left) ScrollBy(-1);
            else if (e.KeyCode == Keys.Right) ScrollBy(1);
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SetFast(false);
                frame.Dispose();
                wxTimer.Dispose();
                DisposeSnaps();
            }
            base.Dispose(disposing);
        }
    }
}
