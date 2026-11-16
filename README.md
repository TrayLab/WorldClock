# 🕒 World Clock

A tiny, clean world clock for the Windows system tray, with analog or digital faces, light/dark themes and live animated weather for every city.

Built for people who work across time zones and want to see at a glance what time it is for their team, clients or partners.

<img width="1438" height="613" alt="shot" src="https://github.com/user-attachments/assets/9b96b454-260b-45d0-86ec-46a6013fb222" />


---

## ✨ Features

- **Compact row of cities**: up to 3 visible at once; small **‹ ›** arrows (or mouse wheel / ← → keys) slide through the rest.
- **Click to enlarge**: a city's card grows smoothly to fill the window with seconds, date, GMT offset and time difference. Click again to go back.
- **Analog or digital faces**: analog clocks turn dark at night (18:00–06:00) so you can see who is outside working hours.
- **Three sizes**:
  | Size | Shows |
  |---|---|
  | Very small | City name + time |
  | Small | City name + time + weather icon & temperature |
  | Normal | Everything: time, day, time difference, weather |
- **Live animated weather**: sun, moon, clouds, rain, snow, fog and thunder icons with the current temperature (°C or °F).
- **City search**: type a city name, pick it from the results, and the time zone and location are filled in automatically. You can also pick any Windows time zone manually.
- **Light, Dark or "Same as Windows" theme**
- **Lives in the tray**:
  - Left-click the tray icon to show or hide the window.
  - Hover over the icon to see every city's time without opening it.
  - Right-click it for the menu.
- **Minimize / close to tray**: the window stays open until you minimize or close it. You can choose whether **×** hides to the tray or exits.
- **Extras**: 24-hour time, Always on top, Start with Windows, remembers the window position.
- **No install, no dependencies**: one small `.exe`, built with the C# compiler that already ships with Windows.

---

## 📦 Download & run

1. Download the latest `WorldClock.exe` from **[Releases](../../releases)**, or build it yourself (below).
2. Run `WorldClock.exe`. The window opens and a clock icon appears in the tray (click **^** next to the tray if it's hidden).

> Windows SmartScreen may warn about an unsigned app. Click **More info → Run anyway**, or build it from source yourself.

### Requirements

- Windows 10 or 11
- .NET Framework 4.5 or newer (already installed on Windows 10/11)
- Internet connection for weather and city search (clocks work offline)

---

## 🔨 Build from source

No Visual Studio needed. Windows already includes the .NET Framework C# compiler.

```bat
git clone https://github.com/<your-user>/world-clock.git
cd world-clock
build.bat
```

`build.bat` compiles every `.cs` file into `WorldClock.exe` and starts it.

Manual build:

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
  /win32icon:WorldClock.ico /out:WorldClock.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll *.cs
```

> The code sticks to **C# 5** features so it compiles with this built-in compiler.

---

## 🖱️ Usage

| Action | How |
|---|---|
| Show / hide window | Left-click the tray icon |
| See all times quickly | Hover over the tray icon |
| Enlarge a city | Click its card |
| Back to all cities | Click the enlarged card, **‹** in the top bar, or `Esc` |
| Scroll cities | **‹ ›** arrows, mouse wheel, or `←` `→` |
| Move the window | Drag it from anywhere |
| Settings | ⚙ in the top bar, or right-click the tray icon → *Settings…* |
| Quit | Right-click the tray icon → *Exit* |

### Adding a city

1. Open **Settings → Add or edit a city**.
2. Type a city name and press **Search**.
3. Pick the right place from the results. The time zone is selected automatically.
4. Press **Add as new city**.

Cities added without a search result (time zone picked by hand) work fine but don't show weather, because the app doesn't know their location.

---

## ⚙️ Settings

| Setting | Options |
|---|---|
| Theme | Light · Dark · Same as Windows |
| Clock style | Analog · Digital |
| Size | Very small · Small · Normal |
| Cities shown | 1 · 2 · 3 at a time |
| Temperature | Celsius · Fahrenheit |
| Close (×) | Minimize to tray · Exit the app |
| Toggles | 24-hour time · Always on top · Start with Windows |

Settings are stored in:

```
%APPDATA%\WorldClock\settings.json
```

**Start with Windows** adds an entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` that launches the app with `--tray`, so it starts hidden in the tray.

Only one copy runs at a time. Starting it again brings the existing window to the front.

---

## 🗂️ Project structure

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, single-instance handling, tray icon & menu |
| `MainWindow.cs` | The clock window: layout, row scrolling, enlarge animation, drawing |
| `Painter.cs` | Themes, analog/digital clock drawing, animated weather icons, tray icon |
| `SettingsForm.cs` | Settings dialog and city search |
| `Settings.cs` | Settings model, JSON load/save, startup registry entry |
| `Weather.cs` | Open-Meteo weather and geocoding client |
| `TzMap.cs` | IANA → Windows time zone table (from Unicode CLDR) |
| `WorldClock.ico` | App icon |
| `build.bat` | One-click build script |

---

## 🌦️ Data sources

- **Weather & city search**: [Open-Meteo](https://open-meteo.com/), free and needs no API key. Weather data by Open-Meteo.com, licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
- **Time zones**: Windows' own time zone database (stays correct with Windows Update). City-search results are mapped to Windows zones using the [Unicode CLDR](https://cldr.unicode.org/) `windowsZones` table.

Weather refreshes every 10 minutes while the app is running.

---

## 🛣️ Roadmap ideas

- [ ] Per-city working hours highlight
- [ ] Drag to reorder cities in the window
- [ ] Meeting planner (compare hours across cities)
- [ ] Per-monitor DPI awareness

---

## 📄 License

Released under the MIT License. See [LICENSE](LICENSE).
