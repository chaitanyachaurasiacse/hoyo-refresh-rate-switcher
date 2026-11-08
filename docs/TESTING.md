# Test report

**Test machine:** ASUS TUF Gaming A17 FA706QM, Windows 11 (build 26200), AMD Radeon iGPU + NVIDIA RTX 3060 Laptop GPU (Optimus).
Built-in panel AUO 8294, 1920×1080, offers 60 Hz and 144 Hz, connected to the **AMD** adapter as `\\.\DISPLAY1`.

All results below are from the real panel. No refresh-rate change was mocked. Each one is read back from Windows, and the diagnostic tool also reads the panel's actual signal timing through CCD.

## Executed automatically

### Stage 2: switching component (`HoYoRefreshDiag test`): 10/10 passed

| Test | Result |
|---|---|
| Native struct sizes match the Windows SDK (DEVMODE 220, PATH_INFO 72, MODE_INFO 64, ...) | PASS |
| 60 Hz and 144 Hz offered at current resolution | PASS |
| 60 → 144 Hz (saved), verified: GDI reports 144, signal timing 144 Hz | PASS |
| 144 → 60 Hz (temporary), verified: GDI reports 60, signal timing 60 Hz | PASS |
| Repeated request for the current rate does nothing | PASS |
| 60 → 144 Hz again, verified | PASS |
| Unsupported 61 Hz is refused; display untouched | PASS |
| Non-existent device `\\.\DISPLAY99` fails gracefully (Win32 error 1150) | PASS |
| Invalid 0 Hz refused | PASS |

### Stages 3–5: whole app (`tests\e2e-test.ps1`): 28/28 passed, run three times (twice on the build output, once on the packaged `dist` exe)

| # | Scenario | Result |
|---|---|---|
| Real | **Real Genshin Impact running**: app started at 144 Hz → switched to 60 Hz (0.1–0.2 s) | PASS |
| Real | Exiting the app while the real game runs leaves 60 Hz | PASS |
| 1 | App launched with no game → 144 Hz | PASS |
| 2 | Game starts → 60 Hz (~2 s, one poll interval) | PASS |
| – | Gaming rate is temporary: Windows' saved mode stays 144 Hz | PASS |
| – | No repeated mode changes while the game keeps running | PASS |
| 3 | Game closes (process killed abruptly, like a crash) → 144 Hz | PASS |
| 4 | Star Rail starts → 60 Hz, closes → 144 Hz | PASS |
| 5 | ZZZ starts → 60 Hz | PASS |
| 6 | Second game starts → stays 60; first closes while second runs → **stays 60 Hz** (7 s) | PASS |
| 7 | Last game closes → 144 Hz | PASS |
| 8 | Automatic switching off (saved to settings.json) → game start changes nothing | PASS |
| 9 | GUI buttons "Switch to 60 Hz" / "Switch to 144 Hz" → verified changes | PASS |
| – | Re-enabling auto while a game runs → 60 Hz immediately | PASS |
| – | Manual 144 Hz while a game runs is respected (not undone by polling) | PASS |
| – | Second launch does not create a second instance | PASS |
| 10 | "Start with Windows" writes `HKCU\...\Run` = `"<exe>" --minimized`; unticking removes it | PASS |
| – | No scheduled tasks created | PASS |
| – | Idle in tray for 60 s: 0.03 % CPU, 6.5 MB working set | PASS |
| – | App killed at 60 Hz → display stays 60 but Windows' saved mode is 144 | PASS |
| – | Restarting the app after the crash → 144 Hz restored | PASS |
| – | Exit from the GUI ends the process | PASS |

For scenarios 1–9 the games were simulated by harmless stand-in processes (`HoYoTestGenshin.exe`, `HoYoTestStarRail.exe`, `HoYoTestZZZ.exe`, copies of `ping.exe`). Real names couldn't be used because the real Genshin Impact was running during the tests. Detection by real name was verified separately:

- `HoYoRefreshDiag info` detected the real `GenshinImpact.exe` and ignored `HYP.exe` / `HYPHelper.exe` (launcher).
- The real-game e2e step above.
- The three process-scan methods agree (see `HoYoRefreshDiag info`).

Executable names were checked against the installed games on disk:
`F:\Genshin Impact game\GenshinImpact.exe`, `E:\Games\StarRail.exe`, `...\ZenlessZoneZero Game\ZenlessZoneZero.exe`.

## Still to be checked manually on the laptop

These can't be fully automated here. Each code path exists and is logged, but hasn't been exercised for real.

| # | Test | How | Expected |
|---|---|---|---|
| A | Real Genshin **closing** | Play, then quit Genshin | Within ~2 s: 144 Hz; log shows `all supported games closed` |
| B | Real Star Rail and ZZZ | Launch each from HoYoPlay | 60 Hz while running; 144 Hz after closing; launcher alone does **not** switch |
| C | Two real games at once | Open two games, close one | Stays 60 Hz until both closed |
| D | Restart Windows | Tick *Start with Windows*, reboot | Tray icon appears after login; display 144 Hz |
| E | Sleep / hibernate | Sleep with a game running, wake, close game | Log shows `System resumed`; correct rate after wake and after closing the game |
| F | External monitor | Connect/disconnect a monitor | Log shows `Display configuration changed`; built-in panel still controlled; external monitor rate untouched |
| G | Lid closed with external monitor only | Close lid, start a game | Status says the built-in display isn't active; nothing changes |
| H | Lock screen | Win+L, close the game from another session or let it crash while locked, unlock | Switch succeeds now or is retried after unlock |

The **verification-failed → restore previous mode** path (driver accepts a mode but reports a different one) can't be triggered on demand. It is implemented in `DisplayManager.SetRefreshRate` step 7 and would show up in the log as `Restore previous mode`.
