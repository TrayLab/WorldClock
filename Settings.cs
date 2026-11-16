using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WorldClock
{
    public class CityInfo
    {
        public string Name { get; set; }
        public string ZoneId { get; set; }      // Windows time zone id, e.g. "FLE Standard Time"
        public bool HasCoords { get; set; }     // needed for weather
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string Country { get; set; }

        TimeZoneInfo zone;
        string zoneFor;

        public TimeZoneInfo GetZone()
        {
            if (zone == null || zoneFor != ZoneId)
            {
                zoneFor = ZoneId;
                try { zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId); }
                catch { zone = TimeZoneInfo.Local; }
            }
            return zone;
        }

        public DateTime TimeAt(DateTime utc) { return TimeZoneInfo.ConvertTimeFromUtc(utc, GetZone()); }
        public TimeSpan OffsetAt(DateTime utc) { return GetZone().GetUtcOffset(utc); }
        public bool IsLocal() { return GetZone().Id == TimeZoneInfo.Local.Id; }

        public string CoordKey()
        {
            if (!HasCoords) return null;
            return Lat.ToString("0.###", CultureInfo.InvariantCulture) + "," + Lon.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public CityInfo Copy() { return (CityInfo)MemberwiseClone(); }
    }

    public class AppSettings
    {
        public string Theme { get; set; }       // light | dark | system
        public string Style { get; set; }       // analog | digital
        public string Unit { get; set; }        // C | F
        public string Size { get; set; }        // mini | small | normal
        public int Visible { get; set; }        // cities shown at once (1-3)
        public bool Use24h { get; set; }
        public bool CloseToTray { get; set; }   // what the X button does
        public bool AlwaysOnTop { get; set; }
        public bool Minimal { get; set; }       // no top bar: control everything from the tray menu
        public bool HasPos { get; set; }
        public int WinX { get; set; }
        public int WinY { get; set; }
        public List<CityInfo> Cities { get; set; }

        public bool Digital { get { return Style == "digital"; } }
    }

    static class Store
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorldClock");
        public static readonly string FilePath = Path.Combine(Folder, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null) return Normalize(s);
                }
            }
            catch { }
            return Defaults();
        }

        public static void Save(AppSettings s)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(s), Encoding.UTF8);
            }
            catch { }
        }

        public static AppSettings Clone(AppSettings s)
        {
            var js = new JavaScriptSerializer();
            return Normalize(js.Deserialize<AppSettings>(js.Serialize(s)));
        }

        static AppSettings Normalize(AppSettings s)
        {
            if (s.Theme != "dark" && s.Theme != "system") s.Theme = "light";
            if (s.Style != "digital") s.Style = "analog";
            if (s.Unit != "F") s.Unit = "C";
            if (s.Size != "mini" && s.Size != "normal") s.Size = "small";
            if (s.Visible < 1 || s.Visible > 3) s.Visible = 3;
            if (s.Cities == null) s.Cities = new List<CityInfo>();
            s.Cities.RemoveAll(c => c == null || string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.ZoneId));
            return s;
        }

        public static AppSettings Defaults()
        {
            var s = new AppSettings();
            s.Theme = "light";
            s.Style = "analog";
            s.Unit = "C";
            s.Size = "small";
            s.Visible = 3;
            s.CloseToTray = true;
            s.Cities = new List<CityInfo>();
            s.Cities.Add(City("Kyiv", "FLE Standard Time", 50.4501, 30.5234, "Ukraine"));
            s.Cities.Add(City("New York", "Eastern Standard Time", 40.7128, -74.0060, "United States"));
            s.Cities.Add(City("Florida", "Eastern Standard Time", 25.7617, -80.1918, "United States"));
            s.Cities.Add(City("Dubai", "Arabian Standard Time", 25.2048, 55.2708, "United Arab Emirates"));
            return s;
        }

        public static CityInfo City(string name, string zone, double lat, double lon, string country)
        {
            var c = new CityInfo();
            c.Name = name; c.ZoneId = zone; c.Lat = lat; c.Lon = lon; c.Country = country; c.HasCoords = true;
            return c;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "WorldClock";

        public static bool StartsWithWindows
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue(RunName) != null;
                }
                catch { return false; }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(RunName, false);
                }
            }
        }
    }
}
