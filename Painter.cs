using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WorldClock
{
    class Palette
    {
        public bool Dark;
        public Color Bg, Card, CardHover, Ink, Gray, Border;
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);

        public static Palette For(string theme)
        {
            bool dark = theme == "dark" || (theme == "system" && WindowsUsesDark());
            var p = new Palette();
            p.Dark = dark;
            if (dark)
            {
                p.Bg = Color.FromArgb(30, 30, 32);
                p.Card = Color.FromArgb(44, 44, 46);
                p.CardHover = Color.FromArgb(56, 56, 59);
                p.Ink = Color.FromArgb(245, 245, 247);
                p.Gray = Color.FromArgb(152, 152, 157);
                p.Border = Color.FromArgb(62, 62, 66);
            }
            else
            {
                p.Bg = Color.White;
                p.Card = Color.FromArgb(242, 242, 247);
                p.CardHover = Color.FromArgb(232, 232, 238);
                p.Ink = Color.FromArgb(17, 17, 17);
                p.Gray = Color.FromArgb(142, 142, 147);
                p.Border = Color.FromArgb(225, 225, 230);
            }
            return p;
        }

        static bool WindowsUsesDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }
    }

    static class Painter
    {
        public static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly Color Red = Color.FromArgb(255, 59, 48);
        public static readonly string UiFamily = PickFamily(new[] { "Segoe UI" }, FontFamily.GenericSansSerif.Name);
        public static readonly string MonoFamily = PickFamily(new[] { "Cascadia Mono", "Consolas", "DejaVu Sans Mono" }, FontFamily.GenericMonospace.Name);
        static readonly StringFormat Typo = MakeTypo();

        static string PickFamily(string[] names, string fallback)
        {
            using (var fc = new InstalledFontCollection())
            {
                foreach (string n in names)
                    foreach (FontFamily f in fc.Families)
                        if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return f.Name;
            }
            return fallback;
        }

        static StringFormat MakeTypo()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        public static void Prep(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        public static Font Ui(float px) { return new Font(UiFamily, px, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Ui(float px, FontStyle st) { return new Font(UiFamily, px, st, GraphicsUnit.Pixel); }

        public static Font Fit(Graphics g, string text, FontStyle st, float startPx, float minPx, float maxW)
        {
            float sz = startPx;
            Font f = Ui(sz, st);
            while (sz > minPx && g.MeasureString(text, f, PointF.Empty, Typo).Width > maxW)
            {
                f.Dispose();
                sz -= 1;
                f = Ui(sz, st);
            }
            return f;
        }

        public static float Width(Graphics g, string s, Font f) { return g.MeasureString(s, f, PointF.Empty, Typo).Width; }

        public static void Text(Graphics g, string s, Font f, Color c, float x, float y, float maxW)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat(Typo))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(s, f, b, new RectangleF(x, y, Math.Max(1, maxW), f.Size * 1.6f), sf);
            }
        }

        public static void Text(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y, Typo);
        }

        // ------------------------------------------------------------ text helpers
        public static string Gmt(TimeSpan off)
        {
            string sign = off < TimeSpan.Zero ? "−" : "+";
            TimeSpan a = off.Duration();
            return "GMT " + sign + a.Hours + (a.Minutes != 0 ? ":" + a.Minutes.ToString("00") : "");
        }

        public static string DayWord(DateTime there)
        {
            int diff = (there.Date - DateTime.Now.Date).Days;
            if (diff == 0) return "Today";
            if (diff == 1) return "Tomorrow";
            if (diff == -1) return "Yesterday";
            return there.ToString("ddd", Inv);
        }

        static TimeSpan DiffFromHere(TimeSpan off, DateTime utc) { return off - TimeZoneInfo.Local.GetUtcOffset(utc); }

        public static string DiffShort(TimeSpan off, DateTime utc)
        {
            TimeSpan d = DiffFromHere(off, utc);
            if (d == TimeSpan.Zero) return "same time";
            TimeSpan a = d.Duration();
            string s = (d < TimeSpan.Zero ? "−" : "+") + (int)a.TotalHours + "h";
            if (a.Minutes != 0) s += " " + a.Minutes + "m";
            return s;
        }

        public static string DiffLong(TimeSpan off, DateTime utc)
        {
            TimeSpan d = DiffFromHere(off, utc);
            if (d == TimeSpan.Zero) return "same time as you";
            TimeSpan a = d.Duration();
            int h = (int)a.TotalHours;
            string amount = a.Minutes != 0 ? h + "h " + a.Minutes + "m" : h + (h == 1 ? " hour" : " hours");
            return amount + (d < TimeSpan.Zero ? " behind you" : " ahead of you");
        }

        public static string Temp(double t, string unit, bool withUnit)
        {
            return Math.Round(t).ToString("0", Inv) + "°" + (withUnit ? unit : "");
        }

        // ------------------------------------------------------------- shapes
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color c)
        {
            using (GraphicsPath p = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        // little "you are here" arrow shown next to the city in your own time zone
        public static void LocationArrow(Graphics g, float x, float y, float size, Color c)
        {
            PointF[] pts =
            {
                new PointF(x + size * 0.95f, y + size * 0.05f),
                new PointF(x + size * 0.55f, y + size * 0.95f),
                new PointF(x + size * 0.45f, y + size * 0.55f),
                new PointF(x + size * 0.05f, y + size * 0.45f)
            };
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        // ------------------------------------------------------- analog clock
        public static void DrawClock(Graphics g, RectangleF r, DateTime t, bool big, float s, Palette pal)
        {
            bool night = t.Hour < 6 || t.Hour >= 18;
            Color face = night ? Color.FromArgb(22, 22, 24) : Color.White;
            Color ink = night ? Color.White : Color.FromArgb(17, 17, 17);
            Color tickC = night ? Color.FromArgb(160, 160, 165) : Color.FromArgb(120, 120, 125);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, R = r.Width / 2f;

            if (!pal.Dark)
                for (int i = 3; i >= 1; i--)
                {
                    float grow = i * 1.0f * s;
                    using (var b = new SolidBrush(Color.FromArgb(night ? 12 : 7, 0, 0, 0)))
                        g.FillEllipse(b, r.X - grow, r.Y - grow + 1.5f * s, r.Width + 2 * grow, r.Height + 2 * grow);
                }
            using (var b = new SolidBrush(face)) g.FillEllipse(b, r);
            Color edge = pal.Dark ? (night ? Color.FromArgb(70, 70, 74) : Color.FromArgb(0, 0, 0, 0)) : (night ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(229, 229, 234));
            if (edge.A > 0)
                using (var p = new Pen(edge, Math.Max(1f, s))) g.DrawEllipse(p, r);

            using (var tickPen = new Pen(tickC, Math.Max(1.2f, (big ? 0.02f : 0.035f) * R)))
            using (var numFont = new Font(UiFamily, Math.Max(6f, R * 0.2f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var numBrush = new SolidBrush(tickC))
            using (var sf = new StringFormat())
            {
                tickPen.StartCap = tickPen.EndCap = LineCap.Round;
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                for (int h = 0; h < 12; h++)
                {
                    double a = h * Math.PI / 6.0;
                    float sx = (float)Math.Sin(a), sy = (float)-Math.Cos(a);
                    if (big && h % 3 == 0)
                    {
                        float nr = R * 0.75f;
                        g.DrawString((h == 0 ? 12 : h).ToString(Inv), numFont, numBrush, cx + sx * nr, cy + sy * nr + R * 0.01f, sf);
                    }
                    else
                    {
                        float r1 = R * (big ? 0.87f : 0.80f), r2 = R * (big ? 0.78f : 0.66f);
                        g.DrawLine(tickPen, cx + sx * r1, cy + sy * r1, cx + sx * r2, cy + sy * r2);
                    }
                }
            }

            Hand(g, cx, cy, ((t.Hour % 12) + t.Minute / 60.0) * 30.0, R * 0.50f, 0, R * 0.085f, ink);
            Hand(g, cx, cy, (t.Minute + t.Second / 60.0) * 6.0, R * 0.76f, 0, R * 0.06f, ink);
            Hand(g, cx, cy, t.Second * 6.0, R * 0.86f, R * 0.18f, Math.Max(1f, R * 0.026f), Red);
            float c1 = R * 0.085f, c2 = R * 0.035f;
            using (var b = new SolidBrush(ink)) g.FillEllipse(b, cx - c1, cy - c1, 2 * c1, 2 * c1);
            using (var b = new SolidBrush(face)) g.FillEllipse(b, cx - c2, cy - c2, 2 * c2, 2 * c2);
        }

        static void Hand(Graphics g, float cx, float cy, double deg, float len, float tail, float width, Color c)
        {
            double a = deg * Math.PI / 180.0;
            float dx = (float)Math.Sin(a), dy = (float)-Math.Cos(a);
            using (var p = new Pen(c, width))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLine(p, cx - dx * tail, cy - dy * tail, cx + dx * len, cy + dy * len);
            }
        }

        // ------------------------------------------------------ digital clock
        static float Ascent(Font f)
        {
            FontFamily ff = f.FontFamily;
            return f.Size * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
        }

        public static float DigitalWidth(Graphics g, string main, string suffix, float px)
        {
            using (var fb = new Font(MonoFamily, px, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fs = new Font(MonoFamily, px * 0.4f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                float w = Width(g, main, fb);
                if (suffix.Length > 0) w += px * 0.08f + Width(g, suffix, fs);
                return w;
            }
        }

        // "10:35" big with a small "am" sitting on the same baseline, centred on cx
        public static void DrawDigital(Graphics g, float cx, float top, string main, string suffix, float px, Color c)
        {
            using (var fb = new Font(MonoFamily, px, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fs = new Font(MonoFamily, px * 0.4f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(c))
            {
                float wm = Width(g, main, fb);
                float gap = suffix.Length > 0 ? px * 0.08f : 0;
                float ws = suffix.Length > 0 ? Width(g, suffix, fs) : 0;
                float x = cx - (wm + gap + ws) / 2f;
                g.DrawString(main, fb, br, x, top, Typo);
                if (suffix.Length > 0)
                {
                    float baseline = top + Ascent(fb);
                    g.DrawString(suffix, fs, br, x + wm + gap, baseline - Ascent(fs), Typo);
                }
            }
        }

        // ------------------------------------------------- animated weather icon
        static double Frac(double x) { return x - Math.Floor(x); }

        public static void DrawWeather(Graphics g, RectangleF r, WeatherInfo w, Palette pal, Color bg)
        {
            double ph = Clock.Elapsed.TotalSeconds;
            GraphicsState st = g.Save();
            g.TranslateTransform(r.X, r.Y);
            float u = r.Width / 24f;
            g.ScaleTransform(u, u);
            switch (WeatherService.KindOf(w.Code))
            {
                case WxKind.Clear:
                    if (w.IsDay) Sun(g, 12, 12, 1f, ph); else Moon(g, 12, 12, 1f, ph, pal, bg);
                    break;
                case WxKind.Partly:
                    if (w.IsDay) Sun(g, 8.5f, 8f, 0.66f, ph); else Moon(g, 9f, 8f, 0.68f, ph, pal, bg);
                    Cloud(g, 2.5f, 3.2f, 0.82f, ph, pal, bg);
                    break;
                case WxKind.Cloudy:
                    Cloud(g, -2f, -1.5f, 0.62f, ph * 0.7, pal, bg, Color.Empty);
                    Cloud(g, 1.2f, 1.5f, 0.92f, ph, pal, bg);
                    break;
                case WxKind.Fog:
                    Cloud(g, 0, -2.5f, 0.88f, ph, pal, bg);
                    FogLines(g, ph, pal);
                    break;
                case WxKind.Drizzle:
                    Cloud(g, 0, -3f, 0.9f, ph, pal, bg);
                    Drops(g, ph, new float[] { 9.5f, 14.5f }, 0.8, 1.3f);
                    break;
                case WxKind.Rain:
                    Cloud(g, 0, -3f, 0.9f, ph, pal, bg);
                    Drops(g, ph, new float[] { 8f, 12f, 16f }, 1.25, 1.6f);
                    break;
                case WxKind.Snow:
                    Cloud(g, 0, -3f, 0.9f, ph, pal, bg);
                    Flakes(g, ph, pal);
                    break;
                case WxKind.Thunder:
                    Cloud(g, 0, -3f, 0.9f, ph, pal, bg);
                    Drops(g, ph, new float[] { 7.5f, 16.5f }, 1.25, 1.5f);
                    Bolt(g, ph);
                    break;
            }
            g.Restore(st);
        }

        static void Sun(Graphics g, float cx, float cy, float sc, double ph)
        {
            Color c = Color.FromArgb(255, 183, 0);
            float rot = (float)((ph * 20) % 360);
            using (var p = new Pen(c, 1.7f * sc))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double a = (rot + i * 45) * Math.PI / 180;
                    float r1 = 6.4f * sc, r2 = (8.7f + 0.6f * (float)Math.Sin(ph * 3 + i)) * sc;
                    float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
                    g.DrawLine(p, cx + ca * r1, cy + sa * r1, cx + ca * r2, cy + sa * r2);
                }
            }
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 4.4f * sc, cy - 4.4f * sc, 8.8f * sc, 8.8f * sc);
        }

        static void Moon(Graphics g, float cx, float cy, float sc, double ph, Palette pal, Color bg)
        {
            Color mc = pal.Dark ? Color.FromArgb(232, 232, 238) : Color.FromArgb(110, 118, 140);
            float r = 6.6f * sc, r2 = 5.6f * sc;
            using (var b = new SolidBrush(mc)) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            float bx = cx + 3.4f * sc, by = cy - 2.6f * sc;
            using (var b = new SolidBrush(bg)) g.FillEllipse(b, bx - r2, by - r2, 2 * r2, 2 * r2);
            int a = (int)(60 + 195 * (0.5 + 0.5 * Math.Sin(ph * 2.4)));
            float sr = 1.0f * sc;
            using (var b = new SolidBrush(Color.FromArgb(a, mc)))
                g.FillEllipse(b, cx + 6.2f * sc - sr, cy + 4.2f * sc - sr, 2 * sr, 2 * sr);
        }

        static void Cloud(Graphics g, float dx, float dy, float sc, double ph, Palette pal, Color bg)
        {
            Cloud(g, dx, dy, sc, ph, pal, bg, pal.Ink);
        }

        // Outline cloud: stroke every lump, then fill them all with the background -> only the outer outline stays.
        static void Cloud(Graphics g, float dx, float dy, float sc, double ph, Palette pal, Color bg, Color ink)
        {
            if (ink.IsEmpty) ink = pal.Gray;
            GraphicsState st = g.Save();
            float drift = (float)Math.Sin(ph * 1.3) * 0.7f;
            g.TranslateTransform(12 + dx + drift, 12 + dy);
            g.ScaleTransform(sc, sc);
            g.TranslateTransform(-12, -12);
            var e1 = new RectangleF(2, 11, 8, 7.5f);
            var e2 = new RectangleF(6.5f, 5.5f, 11, 11);
            var e3 = new RectangleF(13, 9.5f, 9, 9);
            var rb = new RectangleF(6, 13, 12, 5.5f);
            using (var p = new Pen(ink, 2.9f))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawEllipse(p, e1); g.DrawEllipse(p, e2); g.DrawEllipse(p, e3);
                g.DrawRectangle(p, rb.X, rb.Y, rb.Width, rb.Height);
            }
            using (var b = new SolidBrush(bg))
            {
                g.FillEllipse(b, e1); g.FillEllipse(b, e2); g.FillEllipse(b, e3); g.FillRectangle(b, rb);
            }
            g.Restore(st);
        }

        static void Drops(Graphics g, double ph, float[] xs, double speed, float width)
        {
            for (int i = 0; i < xs.Length; i++)
            {
                double f = Frac(ph * speed + i * 0.37);
                float y = 15.5f + (float)f * 6f;
                int a = (int)(255 * Math.Min(1.0, (1 - f) * 2.2));
                using (var p = new Pen(Color.FromArgb(a, 64, 156, 255), width))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawLine(p, xs[i], y, xs[i] - 0.9f, y + 2.6f);
                }
            }
        }

        static void Flakes(Graphics g, double ph, Palette pal)
        {
            float[] xs = { 8f, 12f, 16f };
            Color c = pal.Dark ? Color.FromArgb(235, 240, 250) : Color.FromArgb(120, 150, 200);
            for (int i = 0; i < xs.Length; i++)
            {
                double f = Frac(ph * 0.45 + i * 0.33);
                float y = 16f + (float)f * 6.5f;
                float x = xs[i] + (float)Math.Sin(ph * 2 + i * 2) * 0.9f;
                int a = (int)(255 * Math.Min(1.0, (1 - f) * 2.5));
                using (var b = new SolidBrush(Color.FromArgb(a, c))) g.FillEllipse(b, x - 1.1f, y - 1.1f, 2.2f, 2.2f);
            }
        }

        static void Bolt(Graphics g, double ph)
        {
            double f = Frac(ph * 0.55);
            int a = (f < 0.06 || (f > 0.12 && f < 0.17)) ? 255 : 175;
            PointF[] pts =
            {
                new PointF(13.5f, 13.5f), new PointF(9.8f, 19.2f), new PointF(12.2f, 19.2f),
                new PointF(10.8f, 23.4f), new PointF(15.6f, 17f), new PointF(13.1f, 17f), new PointF(14.8f, 13.5f)
            };
            using (var b = new SolidBrush(Color.FromArgb(a, 255, 204, 0))) g.FillPolygon(b, pts);
        }

        static void FogLines(Graphics g, double ph, Palette pal)
        {
            float s1 = (float)Math.Sin(ph * 1.4) * 1.6f;
            using (var p = new Pen(pal.Gray, 1.6f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLine(p, 4 + s1, 19.5f, 18 + s1, 19.5f);
                g.DrawLine(p, 6 - s1, 22.5f, 20 - s1, 22.5f);
            }
        }

        // ------------------------------------------------------------ tray icon
        public static Bitmap TrayBitmap(int size, DateTime t)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size / 16f;
                var r = new RectangleF(0.75f * s, 0.75f * s, size - 1.5f * s, size - 1.5f * s);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, r);
                using (var p = new Pen(Color.FromArgb(40, 40, 44), 1.4f * s)) g.DrawEllipse(p, r);
                float cx = size / 2f, cy = size / 2f, R = r.Width / 2f;
                Color ink = Color.FromArgb(17, 17, 17);
                Hand(g, cx, cy, ((t.Hour % 12) + t.Minute / 60.0) * 30.0, R * 0.48f, 0, 1.6f * s, ink);
                Hand(g, cx, cy, t.Minute * 6.0, R * 0.72f, 0, 1.2f * s, ink);
                using (var b = new SolidBrush(Red)) g.FillEllipse(b, cx - 1.1f * s, cy - 1.1f * s, 2.2f * s, 2.2f * s);
            }
            return bmp;
        }
    }
}
