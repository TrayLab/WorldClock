// Weather + city search from Open-Meteo (free, no API key): https://open-meteo.com
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WorldClock
{
    public class WeatherInfo
    {
        public double Temp;
        public int Code;
        public bool IsDay;
        public DateTime FetchedUtc;
    }

    public class GeoResult
    {
        public string Name, Admin, Country, Timezone;
        public double Lat, Lon;

        public override string ToString()
        {
            var sb = new StringBuilder(Name);
            if (!string.IsNullOrEmpty(Admin) && Admin != Name) sb.Append(", ").Append(Admin);
            if (!string.IsNullOrEmpty(Country)) sb.Append(", ").Append(Country);
            if (!string.IsNullOrEmpty(Timezone)) sb.Append("    (").Append(Timezone).Append(")");
            return sb.ToString();
        }
    }

    public enum WxKind { Clear, Partly, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

    static class WeatherService
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly object sync = new object();
        static readonly Dictionary<string, WeatherInfo> cache = new Dictionary<string, WeatherInfo>();
        static readonly HashSet<string> inFlight = new HashSet<string>();
        static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

        static WeatherService()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // TLS 1.2
        }

        static string Key(CityInfo c, string unit)
        {
            string k = c.CoordKey();
            return k == null ? null : k + "|" + unit;
        }

        public static WeatherInfo Get(CityInfo c, string unit)
        {
            string key = Key(c, unit);
            if (key == null) return null;
            lock (sync)
            {
                WeatherInfo w;
                return cache.TryGetValue(key, out w) ? w : null;
            }
        }

        public static void Put(CityInfo c, string unit, WeatherInfo w) // used by the preview harness
        {
            string key = Key(c, unit);
            if (key != null) lock (sync) cache[key] = w;
        }

        public static void Refresh(IEnumerable<CityInfo> cities, string unit, Action changed)
        {
            foreach (CityInfo c0 in cities)
            {
                CityInfo c = c0;
                string key = Key(c, unit);
                if (key == null) continue;
                lock (sync)
                {
                    WeatherInfo old;
                    if (cache.TryGetValue(key, out old) && DateTime.UtcNow - old.FetchedUtc < MaxAge) continue;
                    if (!inFlight.Add(key)) continue;
                }
                double lat = c.Lat, lon = c.Lon;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    WeatherInfo w = null;
                    try { w = Fetch(lat, lon, unit); } catch { }
                    lock (sync)
                    {
                        inFlight.Remove(key);
                        if (w != null) cache[key] = w;
                    }
                    if (w != null && changed != null) { try { changed(); } catch { } }
                });
            }
        }

        static WeatherInfo Fetch(double lat, double lon, string unit)
        {
            string url = string.Format(Inv,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current=temperature_2m,weather_code,is_day&timezone=auto{2}",
                lat, lon, unit == "F" ? "&temperature_unit=fahrenheit" : "");
            return ParseCurrent(Download(url));
        }

        public static WeatherInfo ParseCurrent(string json)
        {
            var root = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var cur = (IDictionary<string, object>)root["current"];
            var w = new WeatherInfo();
            w.Temp = Convert.ToDouble(cur["temperature_2m"], Inv);
            w.Code = Convert.ToInt32(cur["weather_code"], Inv);
            w.IsDay = Convert.ToInt32(cur["is_day"], Inv) == 1;
            w.FetchedUtc = DateTime.UtcNow;
            return w;
        }

        public static List<GeoResult> Geocode(string query)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=12&language=en&format=json&name=" + Uri.EscapeDataString(query);
            return ParseGeo(Download(url));
        }

        public static List<GeoResult> ParseGeo(string json)
        {
            var root = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var list = new List<GeoResult>();
            object results;
            if (root == null || !root.TryGetValue("results", out results) || !(results is IEnumerable)) return list;
            foreach (object o in (IEnumerable)results)
            {
                var d = o as IDictionary<string, object>;
                if (d == null) continue;
                var g = new GeoResult();
                g.Name = Str(d, "name");
                g.Admin = Str(d, "admin1");
                g.Country = Str(d, "country");
                g.Timezone = Str(d, "timezone");
                g.Lat = Convert.ToDouble(d["latitude"], Inv);
                g.Lon = Convert.ToDouble(d["longitude"], Inv);
                list.Add(g);
            }
            return list;
        }

        static string Str(IDictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o != null ? o.ToString() : "";
        }

        static string Download(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "WorldClock/2.0 (Windows tray widget)";
            req.Timeout = 12000;
            req.ReadWriteTimeout = 12000;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static WxKind KindOf(int code)
        {
            if (code == 0) return WxKind.Clear;
            if (code == 1 || code == 2) return WxKind.Partly;
            if (code == 45 || code == 48) return WxKind.Fog;
            if (code >= 51 && code <= 57) return WxKind.Drizzle;
            if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return WxKind.Rain;
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return WxKind.Snow;
            if (code >= 95) return WxKind.Thunder;
            return WxKind.Cloudy;
        }

        public static string Describe(int code, bool isDay)
        {
            switch (code)
            {
                case 0: return isDay ? "Sunny" : "Clear";
                case 1: return "Mainly clear";
                case 2: return "Partly cloudy";
                case 3: return "Overcast";
                case 45: case 48: return "Fog";
                case 51: case 53: case 55: return "Drizzle";
                case 56: case 57: return "Freezing drizzle";
                case 61: return "Light rain";
                case 63: return "Rain";
                case 65: return "Heavy rain";
                case 66: case 67: return "Freezing rain";
                case 71: return "Light snow";
                case 73: return "Snow";
                case 75: return "Heavy snow";
                case 77: return "Snow grains";
                case 80: return "Light showers";
                case 81: return "Showers";
                case 82: return "Heavy showers";
                case 85: case 86: return "Snow showers";
                case 95: return "Thunderstorm";
                case 96: case 99: return "Thunderstorm, hail";
            }
            return "Cloudy";
        }
    }
}
