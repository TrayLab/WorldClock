using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WorldClock
{
    class SettingsForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        class ZoneItem
        {
            public TimeZoneInfo Zone;
            public override string ToString() { return Zone.DisplayName; }
        }

        static readonly string[] ThemeKeys = { "light", "dark", "system" };

        readonly AppSettings work;
        readonly List<CityInfo> cities;
        Palette pal;
        public AppSettings Result { get; private set; }

        ComboBox cbTheme, cbStyle, cbUnit, cbClose, cbZone;
        CheckBox chk24, chkTop, chkStartup;
        ListBox lbCities, lbResults;
        TextBox tbSearch, tbName;
        Button btnUp, btnDown, btnRemove, btnSearch, btnAdd, btnUpdate, btnSave, btnCancel;
        Label lblStatus;

        public SettingsForm(AppSettings current)
        {
            work = Store.Clone(current);
            cities = work.Cities;
            pal = Palette.For(work.Theme);

            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            Text = "World Clock – Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ClientSize = new Size(560, 712);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---------------- appearance
            Header("Appearance", 16);
            Muted("Theme", 16, 52);
            cbTheme = Combo(170, 48, 200, "Light", "Dark", "Same as Windows");
            Muted("Clock style", 16, 84);
            cbStyle = Combo(170, 80, 200, "Analog", "Digital");
            Muted("Temperature", 16, 116);
            cbUnit = Combo(170, 112, 200, "Celsius (°C)", "Fahrenheit (°F)");
            Muted("Close button (×)", 16, 148);
            cbClose = Combo(170, 144, 200, "Minimize to tray", "Exit the app");
            chk24 = Check("24-hour time", 16, 180);
            chkTop = Check("Always on top", 170, 180);
            chkStartup = Check("Start with Windows", 330, 180);

            cbTheme.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeKeys, work.Theme));
            cbStyle.SelectedIndex = work.Digital ? 1 : 0;
            cbUnit.SelectedIndex = work.Unit == "F" ? 1 : 0;
            cbClose.SelectedIndex = work.CloseToTray ? 0 : 1;
            chk24.Checked = work.Use24h;
            chkTop.Checked = work.AlwaysOnTop;
            chkStartup.Checked = Store.StartsWithWindows;
            cbTheme.SelectedIndexChanged += delegate { pal = Palette.For(ThemeKeys[cbTheme.SelectedIndex]); ApplyTheme(); };

            Separator(214);

            // ---------------- cities
            Header("Your cities", 226);
            lbCities = new ListBox();
            lbCities.SetBounds(16, 258, 382, 136);
            lbCities.IntegralHeight = false;
            lbCities.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(lbCities);
            btnUp = Btn("Move up", 410, 258, 134, delegate { MoveCity(-1); });
            btnDown = Btn("Move down", 410, 292, 134, delegate { MoveCity(1); });
            btnRemove = Btn("Remove", 410, 326, 134, delegate { RemoveCity(); });
            lbCities.SelectedIndexChanged += delegate { CitySelected(); };

            Separator(410);

            // ---------------- add / edit
            Header("Add or edit a city", 422);
            tbSearch = new TextBox();
            tbSearch.SetBounds(16, 456, 382, 26);
            Controls.Add(tbSearch);
            tbSearch.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); }
            };
            btnSearch = Btn("Search", 410, 455, 134, delegate { DoSearch(); });

            lbResults = new ListBox();
            lbResults.SetBounds(16, 490, 528, 92);
            lbResults.IntegralHeight = false;
            lbResults.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(lbResults);
            lbResults.SelectedIndexChanged += delegate { ResultSelected(); };

            Muted("Name", 16, 596);
            tbName = new TextBox();
            tbName.SetBounds(100, 592, 298, 26);
            Controls.Add(tbName);
            btnAdd = Btn("Add as new city", 410, 591, 134, delegate { AddCity(); });

            Muted("Time zone", 16, 628);
            cbZone = new ComboBox();
            cbZone.DropDownStyle = ComboBoxStyle.DropDownList;
            cbZone.FlatStyle = FlatStyle.Flat;
            cbZone.SetBounds(100, 624, 298, 26);
            cbZone.DropDownWidth = 470;
            cbZone.MaxDropDownItems = 20;
            foreach (TimeZoneInfo z in TimeZoneInfo.GetSystemTimeZones())
            {
                var zi = new ZoneItem();
                zi.Zone = z;
                cbZone.Items.Add(zi);
            }
            Controls.Add(cbZone);
            btnUpdate = Btn("Update selected", 410, 623, 134, delegate { UpdateCity(); });

            lblStatus = Muted("Type a city and press Search. Weather needs a city picked from the search results.", 16, 658);
            lblStatus.AutoSize = false;
            lblStatus.SetBounds(16, 658, 528, 18);

            btnSave = Btn("Save", 352, 680, 92, delegate { Save(); });
            btnSave.Tag = "primary";
            btnCancel = Btn("Cancel", 452, 680, 92, delegate { DialogResult = DialogResult.Cancel; Close(); });
            CancelButton = btnCancel;
            ClientSize = new Size(560, 716);

            RefreshCities(cities.Count > 0 ? 0 : -1);
            ApplyTheme();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { SendMessage(tbSearch.Handle, 0x1501, (IntPtr)1, "Search a city, e.g. Kyiv, Tehran, Miami…"); } catch { }
        }

        // ------------------------------------------------------------ builders
        Label Header(string text, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI Semibold", 11f);
            l.Location = new Point(14, y);
            Controls.Add(l);
            return l;
        }

        Label Muted(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            l.Tag = "muted";
            Controls.Add(l);
            return l;
        }

        void Separator(int y)
        {
            var l = new Label();
            l.AutoSize = false;
            l.SetBounds(16, y, 528, 1);
            l.Tag = "sep";
            Controls.Add(l);
        }

        ComboBox Combo(int x, int y, int w, params string[] items)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.SetBounds(x, y, w, 26);
            c.Items.AddRange(items);
            Controls.Add(c);
            return c;
        }

        CheckBox Check(string text, int x, int y)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Location = new Point(x, y);
            Controls.Add(c);
            return c;
        }

        Button Btn(string text, int x, int y, int w, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.Click += click;
            b.FlatStyle = FlatStyle.Flat;
            Controls.Add(b);
            return b;
        }

        void ApplyTheme()
        {
            BackColor = pal.Bg;
            ForeColor = pal.Ink;
            foreach (Control c in Controls)
            {
                string tag = c.Tag as string;
                if (c is Label)
                {
                    c.BackColor = tag == "sep" ? pal.Border : pal.Bg;
                    c.ForeColor = tag == "muted" ? pal.Gray : pal.Ink;
                }
                else if (c is Button)
                {
                    var b = (Button)c;
                    bool primary = tag == "primary";
                    b.BackColor = primary ? Palette.Accent : pal.Card;
                    b.ForeColor = primary ? Color.White : pal.Ink;
                    b.FlatAppearance.BorderColor = primary ? Palette.Accent : pal.Border;
                    b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 90, 170) : pal.CardHover;
                }
                else if (c is CheckBox)
                {
                    c.BackColor = pal.Bg;
                    c.ForeColor = pal.Ink;
                    ((CheckBox)c).FlatStyle = pal.Dark ? FlatStyle.Flat : FlatStyle.Standard;
                }
                else
                {
                    c.BackColor = pal.Card;
                    c.ForeColor = pal.Ink;
                }
            }
            if (IsHandleCreated) DarkTitle();
            Invalidate(true);
        }

        void DarkTitle()
        {
            try
            {
                int v = pal.Dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref v, 4);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTitle();
        }

        // -------------------------------------------------------------- cities
        string Describe(CityInfo c)
        {
            string s = c.Name + "      " + c.GetZone().DisplayName;
            if (!c.HasCoords) s += "   (no weather)";
            return s;
        }

        void RefreshCities(int select)
        {
            lbCities.BeginUpdate();
            lbCities.Items.Clear();
            foreach (CityInfo c in cities) lbCities.Items.Add(Describe(c));
            lbCities.EndUpdate();
            if (select >= 0 && select < cities.Count) lbCities.SelectedIndex = select;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            int i = lbCities.SelectedIndex;
            btnUp.Enabled = i > 0;
            btnDown.Enabled = i >= 0 && i < cities.Count - 1;
            btnRemove.Enabled = i >= 0;
            btnUpdate.Enabled = i >= 0;
        }

        void CitySelected()
        {
            UpdateButtons();
            int i = lbCities.SelectedIndex;
            if (i < 0) return;
            lbResults.ClearSelected();
            tbName.Text = cities[i].Name;
            SelectZone(cities[i].ZoneId);
        }

        void SelectZone(string id)
        {
            for (int k = 0; k < cbZone.Items.Count; k++)
                if (((ZoneItem)cbZone.Items[k]).Zone.Id == id) { cbZone.SelectedIndex = k; return; }
            cbZone.SelectedIndex = -1;
        }

        void MoveCity(int dir)
        {
            int i = lbCities.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= cities.Count) return;
            CityInfo t = cities[i];
            cities[i] = cities[j];
            cities[j] = t;
            RefreshCities(j);
        }

        void RemoveCity()
        {
            int i = lbCities.SelectedIndex;
            if (i < 0) return;
            cities.RemoveAt(i);
            RefreshCities(Math.Min(i, cities.Count - 1));
        }

        bool ReadEditor(out string name, out string zoneId)
        {
            name = tbName.Text.Trim();
            zoneId = cbZone.SelectedItem != null ? ((ZoneItem)cbZone.SelectedItem).Zone.Id : null;
            if (name.Length == 0) { Status("Type a name for the city."); return false; }
            if (zoneId == null) { Status("Choose a time zone."); return false; }
            return true;
        }

        void AddCity()
        {
            string name, zoneId;
            if (!ReadEditor(out name, out zoneId)) return;
            var c = new CityInfo();
            c.Name = name;
            c.ZoneId = zoneId;
            var r = lbResults.SelectedItem as GeoResult;
            if (r != null) { c.HasCoords = true; c.Lat = r.Lat; c.Lon = r.Lon; c.Country = r.Country; }
            cities.Add(c);
            RefreshCities(cities.Count - 1);
            Status("Added " + name + (r == null ? " (pick it from search results to get weather)." : "."));
        }

        void UpdateCity()
        {
            int i = lbCities.SelectedIndex;
            if (i < 0) return;
            string name, zoneId;
            if (!ReadEditor(out name, out zoneId)) return;
            CityInfo c = cities[i];
            c.Name = name;
            c.ZoneId = zoneId;
            var r = lbResults.SelectedItem as GeoResult;
            if (r != null) { c.HasCoords = true; c.Lat = r.Lat; c.Lon = r.Lon; c.Country = r.Country; }
            RefreshCities(i);
            Status("Updated " + name + ".");
        }

        // -------------------------------------------------------------- search
        void DoSearch()
        {
            string q = tbSearch.Text.Trim();
            if (q.Length < 2) { Status("Type at least 2 letters."); return; }
            Status("Searching…");
            btnSearch.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<GeoResult> res = null;
                string err = null;
                try { res = WeatherService.Geocode(q); }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke(new MethodInvoker(delegate { ShowResults(res, err); })); } catch { }
            });
        }

        void ShowResults(List<GeoResult> res, string err)
        {
            btnSearch.Enabled = true;
            lbResults.BeginUpdate();
            lbResults.Items.Clear();
            if (res != null) foreach (GeoResult r in res) lbResults.Items.Add(r);
            lbResults.EndUpdate();
            if (err != null) Status("Search failed (no internet?). You can still type a name and choose the time zone yourself.");
            else if (res.Count == 0) Status("No places found – try another spelling.");
            else Status("Pick the right place, then press “Add as new city”.");
        }

        void ResultSelected()
        {
            var r = lbResults.SelectedItem as GeoResult;
            if (r == null) return;
            if (lbCities.SelectedIndex >= 0 && lbCities.Focused == false) { /* keep list selection for Update */ }
            tbName.Text = r.Name;
            string win = TzMap.ToWindows(r.Timezone);
            if (win != null)
            {
                SelectZone(win);
                if (cbZone.SelectedIndex >= 0) { Status("Time zone set automatically. Press “Add as new city” (or “Update selected”)."); return; }
            }
            cbZone.SelectedIndex = -1;
            Status("Couldn't match the time zone (" + r.Timezone + ") – please choose it in the list.");
        }

        void Status(string s) { lblStatus.Text = s; }

        // ---------------------------------------------------------------- save
        void Save()
        {
            if (cities.Count == 0) { Status("Add at least one city."); return; }
            work.Theme = ThemeKeys[Math.Max(0, cbTheme.SelectedIndex)];
            work.Style = cbStyle.SelectedIndex == 1 ? "digital" : "analog";
            work.Unit = cbUnit.SelectedIndex == 1 ? "F" : "C";
            work.CloseToTray = cbClose.SelectedIndex != 1;
            work.Use24h = chk24.Checked;
            work.AlwaysOnTop = chkTop.Checked;
            try { if (Store.StartsWithWindows != chkStartup.Checked) Store.StartsWithWindows = chkStartup.Checked; }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't change the startup setting:\n" + ex.Message, "World Clock"); }
            Result = work;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
