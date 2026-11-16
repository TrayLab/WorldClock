// World Clock 2 - Windows tray world clock with analog/digital faces, themes and live weather.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("World Clock")]
[assembly: System.Reflection.AssemblyProduct("World Clock")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

namespace WorldClock
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            var mutex = new Mutex(true, "Local\\WorldClockTrayWidget", out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WorldClockTrayWidget.Show");
            if (!created)
            {
                showEvent.Set();   // already running: ask that copy to show its window
                return;
            }
            bool startHidden = Array.IndexOf(args, "--tray") >= 0;
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(startHidden, showEvent));
            GC.KeepAlive(mutex);
        }
    }

    class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        AppSettings settings;
        readonly MainWindow win;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        IntPtr iconHandle = IntPtr.Zero;
        int lastMinute = -1;
        SettingsForm settingsForm;

        public TrayContext(bool startHidden, EventWaitHandle showEvent)
        {
            settings = Store.Load();

            menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open World Clock", null, delegate { win.ShowFromTray(); });
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("Settings…", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });

            win = new MainWindow(settings, this);
            IntPtr force = win.Handle;   // create the handle so BeginInvoke works while hidden

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Text = "World Clock";
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleWindow();
            };
            UpdateTray(true);
            tray.Visible = true;

            timer.Interval = 1000;
            timer.Tick += delegate { UpdateTray(false); };
            timer.Start();

            ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
            {
                try { win.BeginInvoke(new MethodInvoker(delegate { win.ShowFromTray(); })); } catch { }
            }, null, -1, false);

            if (!startHidden) win.ShowFromTray();
        }

        void ToggleWindow()
        {
            bool recentlyActive = win.ContainsFocus || unchecked(Environment.TickCount - win.LastDeactivated) < 400;
            if (win.Visible && win.WindowState != FormWindowState.Minimized && recentlyActive) win.HideToTray();
            else win.ShowFromTray();
        }

        public void ShowMenu(Control owner, Point p) { menu.Show(owner, p); }

        public void OpenSettings()
        {
            if (settingsForm != null) { settingsForm.Activate(); return; }
            using (settingsForm = new SettingsForm(win.Settings))
            {
                DialogResult r = settingsForm.ShowDialog(win.Visible ? win : null);
                if (r == DialogResult.OK && settingsForm.Result != null)
                {
                    AppSettings old = win.Settings;
                    settings = settingsForm.Result;
                    settings.HasPos = old.HasPos;
                    settings.WinX = win.Visible ? win.Left : old.WinX;
                    settings.WinY = win.Visible ? win.Top : old.WinY;
                    Store.Save(settings);
                    win.ApplySettings(settings);
                    UpdateTray(true);
                }
            }
            settingsForm = null;
        }

        public void ExitApp() { ExitThread(); }

        void UpdateTray(bool force)
        {
            DateTime now = DateTime.Now;
            if (!force && now.Minute == lastMinute) return;
            lastMinute = now.Minute;

            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (Bitmap bmp = Painter.TrayBitmap(size, now))
            {
                IntPtr h = bmp.GetHicon();
                Icon old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (old != null) old.Dispose();
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }

            // hover tooltip with every city's time (Windows allows 63 characters)
            DateTime utc = DateTime.UtcNow;
            string tip = BuildTip(utc, false);
            if (tip.Length > 63) tip = BuildTip(utc, true);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        string BuildTip(DateTime utc, bool shortNames)
        {
            var sb = new StringBuilder();
            foreach (CityInfo c in win.Settings.Cities)
            {
                if (sb.Length > 0) sb.Append(shortNames ? " " : "\n");
                string n = c.Name;
                if (shortNames && n.Length > 3) n = n.Substring(0, 3);
                sb.Append(n).Append(' ').Append(c.TimeAt(utc).ToString("HH:mm"));
            }
            return sb.ToString();
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            win.AllowClose = true;
            win.Close();
            win.Dispose();
            if (iconHandle != IntPtr.Zero) { DestroyIcon(iconHandle); iconHandle = IntPtr.Zero; }
            base.ExitThreadCore();
        }
    }
}
