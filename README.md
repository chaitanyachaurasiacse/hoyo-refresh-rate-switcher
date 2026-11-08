# HoYo Refresh Rate Switcher

A tiny Windows tray app that switches your **laptop display to 60 Hz while a HoYoverse game is running** and **back to 144 Hz when the last one closes**.

It was made for laptops whose panel flickers with some HoYoverse games at high refresh rates (e.g. ASUS TUF with NVIDIA Optimus), but the rates and game list are configurable.

- Detects **Genshin Impact**, **Honkai: Star Rail** and **Zenless Zone Zero** (game executables, not launchers). Add more in the settings.
- Holds 60 Hz until **all** supported games are closed.
- Changes **only the refresh rate** of the **built-in panel**: never the resolution, never an external monitor.
- Every change is checked by Windows before it is applied and verified afterwards. If verification fails, the previous mode is restored.
- No administrator rights, no scheduled tasks, no services, no runtime to install. The exe is about 90 KB.
- Idle in the tray: about 7 MB of RAM and about 0.02 % CPU (one process-list read every 2 s).

## Requirements

- Windows 10 (1903 or newer) or Windows 11
- Nothing else. The app targets .NET Framework 4.8, which ships with Windows.

## Install

1. Download `HoYoRefreshSwitcher-vX.Y.Z.zip` from the Releases page, or [build it yourself](#build-from-source).
2. Extract it to a permanent folder, for example `%LOCALAPPDATA%\Programs\HoYoRefreshSwitcher`.
   Don't run it from inside the zip or from Downloads if you plan to enable *Start with Windows*.
3. Run `HoYoRefreshSwitcher.exe`.
   - Windows SmartScreen may warn because the exe isn't code-signed. Choose **More info → Run anyway**.
4. A tray icon showing the current refresh rate appears (blue = desktop rate, amber = gaming rate, grey = automatic switching off, red = error).

**Uninstall:** untick *Start with Windows*, choose *Exit*, then delete the folder. To remove settings and logs too, delete `%APPDATA%\HoYoRefreshSwitcher` and `%LOCALAPPDATA%\HoYoRefreshSwitcher`.

## Use

| Where | What |
|---|---|
| Tray icon, left-click | Opens the window |
| Tray icon, right-click | Automatic switching on/off, switch to 60/144 Hz, Start with Windows, Exit |
| Window | Status (display, current rate, game running, target rate, last change), the same controls, and settings |
| Window close button / Minimize to tray | Closes the window. The app keeps running in the tray. |

How automatic switching behaves:

- **A supported game starts** (none was running before) → switch to the gaming rate.
- **The last supported game closes or crashes** → switch back to the desktop rate.
- Opening or closing a second game while another is still running changes nothing.
- Between those events the app does nothing. If you switch manually while a game runs, your choice stays until the next game start/stop.
- The rule is re-applied when the app starts, after sleep/hibernation, when a monitor is connected or disconnected, and when you re-enable automatic switching.
- If the display is already at the target rate, nothing is executed.

## Start with Windows

Tick **Start with Windows** in the window or the tray menu. This adds a per-user entry under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` that starts the app minimized to the tray.
It shows up in *Settings → Apps → Startup* and *Task Manager → Startup apps*, where it can also be turned off.
If you move the exe, open it once from the new location and the entry is updated automatically.

## Configuration

Settings can be changed in the window (Settings section) or in `%APPDATA%\HoYoRefreshSwitcher\settings.json`
(exit the app before editing the file by hand):

```json
{
  "autoSwitchEnabled": true,
  "gamingRefreshRate": 60,
  "desktopRefreshRate": 144,
  "gameProcesses": [ "GenshinImpact.exe", "StarRail.exe", "ZenlessZoneZero.exe" ],
  "pollIntervalSeconds": 2,
  "startWithWindows": false,
  "targetDisplay": "Internal"
}
```

| Key | Meaning |
|---|---|
| `gamingRefreshRate` / `desktopRefreshRate` | Must be rates your display offers at its current resolution (the window lists them). |
| `gameProcesses` | Executable names of the **games themselves**. Use Task Manager → Details while the game runs to find a name. The China-region Genshin client is `YuanShen.exe`. |
| `pollIntervalSeconds` | 1–30. How often the process list is checked. |
| `targetDisplay` | `Internal` (built-in laptop panel, default) or `Primary` (the Windows primary display, for desktops). With `Internal`, nothing is changed while the lid is closed or only an external monitor is active. |

Launchers and helpers such as `HYP.exe`, `launcher.exe`, `HYPHelper.exe` and `HYPWorker.exe` are deliberately **not** in the list.

## Diagnostics

`HoYoRefreshDiag.exe` (in the same folder) is a command-line tool that uses the same code as the app:

```
HoYoRefreshDiag info          Displays, connection type, available rates, target display, running games
HoYoRefreshDiag status        Current and saved refresh rate of the target display
HoYoRefreshDiag set 144       Change the rate (verified); add --temp to not save it to Windows
HoYoRefreshDiag test          Round-trip 144 -> 60 -> 144 test plus failure-handling tests
HoYoRefreshDiag watch         Prints game start/stop events live (Ctrl+C to stop)
```

Run `HoYoRefreshDiag info > report.txt` and attach the file when reporting a problem. The app log is at `%LOCALAPPDATA%\HoYoRefreshSwitcher\logs\app.log`.

## Build from source

You need the .NET SDK 8 or newer to build (`winget install Microsoft.DotNet.SDK.8`). Users don't need it to run the app.

```
git clone <this repo>
cd HoYoRefreshSwitcher
powershell -ExecutionPolicy Bypass -File build.ps1
```

The output goes to `dist\HoYoRefreshSwitcher\` and `dist\HoYoRefreshSwitcher-vX.Y.Z.zip`.
For a quick build without packaging, run `dotnet build -c Release`.

End-to-end test on real hardware (the display will switch a number of times):

```
powershell -ExecutionPolicy Bypass -File tests\e2e-test.ps1
```

## More documentation

- [How the display switching works](docs/HOW-IT-WORKS.md), including why earlier PowerShell/scheduled-task approaches failed
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Test report](docs/TESTING.md)

## Project layout

```
src/HoYoRefreshSwitcher/          Tray app (WinForms, .NET Framework 4.8)
  Display/                        Display enumeration + refresh-rate switching (CCD + GDI APIs)
  Games/                          Process scanning
  Core/                           Controller (switching rules), settings, startup entry, logging
  UI/                             Main window, tray icon, application context
src/HoYoRefreshSwitcher.Diag/     Console diagnostic tool (shares Display/ and Games/ code)
tests/e2e-test.ps1                End-to-end test against the real display
tools/make-icon.ps1               Regenerates assets/app.ico
build.ps1                         Build + package
```

## License

MIT. Not affiliated with HoYoverse/COGNOSPHERE, ASUS or NVIDIA. Game names are trademarks of their owners.
