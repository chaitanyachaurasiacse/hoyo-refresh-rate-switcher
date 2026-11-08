# How the display switching works

## Summary

| Step | API | Why |
|---|---|---|
| Find the built-in panel | `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` (CCD) | Only CCD reports the physical **connection type** (internal eDP panel vs HDMI/DP) and maps it to the GDI device name, whichever GPU renders. |
| List available modes | `EnumDisplaySettings` | Enumerates exactly the modes the driver offers for that GDI device. |
| Validate | `ChangeDisplaySettingsEx(..., CDS_TEST)` | The driver checks the mode **without applying it**. |
| Apply | `ChangeDisplaySettingsEx(device, mode, ...)` | Changes the mode of **one** display source. The mode is a copy of the current one with only the frequency changed, so resolution, colour depth and position stay as they are. |
| Verify | `EnumDisplaySettings(ENUM_CURRENT_SETTINGS)` + CCD signal timing | Reads back what is actually active. On a mismatch the previous mode is put back. |

## Why two APIs, and why not `SetDisplayConfig`?

**CCD (`QueryDisplayConfig`)** is the modern topology API. It knows which *target* (monitor) is internal, its EDID name and its exact signal timing (e.g. 143.98 Hz). The app uses it **read-only**, to answer "which `\\.\DISPLAYn` is the laptop panel?"

**`SetDisplayConfig`** can also change refresh rates, but you must supply a complete, consistent path and mode set, and a refresh rate as an exact rational number that matches the driver's timing. With `SDC_ALLOW_CHANGES` Windows may silently choose something else. It is the right tool for changing *topology* (extend/duplicate) but needlessly fragile for "same mode, different Hz".

**`ChangeDisplaySettingsEx`** on a single named device, with a mode taken from that device's own mode list and validated with `CDS_TEST`, is the simplest reliable way to change only the refresh rate. It is what most refresh-rate tools use.

So each API does what it is best at: CCD identifies, GDI changes. They are not mixed for the same job.

## NVIDIA Optimus / hybrid graphics

On this ASUS TUF A17 (FA706QM) the internal panel is wired to the **AMD Radeon iGPU**. The RTX 3060 renders games and hands frames to the iGPU. `EnumDisplayDevices` therefore lists `\\.\DISPLAY1` under *AMD Radeon(TM) Graphics*, and the NVIDIA outputs show no attached display.

That's why the app never assumes the gaming GPU drives the panel. It asks CCD which source is connected to the internal target and switches that source, whatever adapter it is on. Laptops with a MUX switch in dGPU mode, where the panel hangs off NVIDIA, work the same way.

## Temporary vs saved changes (crash safety)

- **Desktop rate (144 Hz)**: applied with `CDS_UPDATEREGISTRY`, i.e. saved like a change in Windows Settings.
- **Gaming rate (60 Hz)**: applied **without** saving. Windows keeps 144 Hz as the stored mode.

If the app is killed or crashes while at 60 Hz:

1. Restarting the app (or logging in, if *Start with Windows* is on) re-applies the rule: no game, so 144 Hz.
2. Even without the app, the next reboot brings back the saved 144 Hz.
3. On a clean *Exit* or an unhandled exception, the app restores 144 Hz itself if it had lowered the rate and no game is running. If a game is still running, 60 Hz is left alone to avoid flicker.

## Why the earlier PowerShell/scheduled-task attempts behaved differently

Likely causes, based on how these APIs behave (the original scripts weren't available to reproduce):

1. **"Windows could not read the current display settings"** is the message you get when `EnumDisplaySettings` returns `FALSE`. Typical reasons:
   - The code ran in a **non-interactive session**. A scheduled task set to *Run whether user is logged on or not*, or running as SYSTEM, runs in session 0, which has **no displays at all**. Display modes can only be read and changed from the logged-in user's interactive desktop. The diagnostic tool prints the session ID for this reason.
   - `DEVMODE.dmSize` wasn't set, or the P/Invoke struct layout was wrong (it must be exactly 220 bytes). `HoYoRefreshDiag info` checks every struct size against the Windows SDK.
   - Passing a `dmDriverExtra` value from a previous call without allocating those extra bytes.
2. **Scheduled task registration failed with 0x80070005 (Access denied).** Creating a task with *Run with highest privileges*, or for another user, requires administrator rights. Changing the refresh rate needs **no** elevation, so a task was never necessary. A per-user `Run` key entry starts the app in the right session without admin.
3. **Targeting "the primary display"** works on a single-screen laptop, but on hybrid systems and with external monitors the primary display isn't necessarily the panel. Using `NULL` as the device name with `ChangeDisplaySettingsEx` changes the primary display (and its global mode), which is also why behaviour varied.
4. A script that **polled and re-applied** the mode every few seconds caused repeated mode sets (black flashes). This app acts only on game start/stop transitions.

## Game detection

Every *n* seconds (default 2) the app reads the system process list in a single call (`NtQuerySystemInformation`, the same call .NET's `Process` class uses) and compares executable names against the configured list. It never opens a handle to any process, so it cannot interfere with games or their anti-cheat.

Event-driven alternatives were rejected:

- WMI `Win32_ProcessStartTrace` requires administrator rights.
- WMI `__InstanceCreationEvent ... WITHIN n` is polling inside the WMI service and costs more than doing it directly.
- Window-creation hooks plus process handles would mean touching the game process.

Measured cost on the development laptop (308 processes): about 8 ms CPU per scan, about 0.02 % of the machine at a 2 s interval.

Verified process names (global clients):

| Game | Game process (detected) | Not detected (by design) |
|---|---|---|
| Genshin Impact | `GenshinImpact.exe` (CN: `YuanShen.exe`) | `HYP.exe`, `HYPHelper.exe`, `launcher.exe`, `ZFGameBrowser.exe` |
| Honkai: Star Rail | `StarRail.exe` | `launcher.exe`, `ZFGameBrowser.exe` |
| Zenless Zone Zero | `ZenlessZoneZero.exe` | `hyp_questr_app.exe`, `ZFGameBrowser.exe` |

## Sleep, resume and display changes

The app listens to `SystemEvents`:

- **PowerModeChanged / Resume**: re-applies the rule 3 s after resume, once the display stack is back.
- **DisplaySettingsChanged**: refreshes the shown state. If the set of displays changed (monitor plugged or unplugged, lid opened), it re-identifies the panel and re-applies the rule after 2 s. The app's own mode changes don't count as topology changes.
- **SessionSwitch / Unlock**: if a switch failed while the PC was locked, it is retried.

Failed automatic switches are retried up to 3 times, 5 s apart, then left alone until the next game start/stop. A rate the display doesn't offer is never attempted.
