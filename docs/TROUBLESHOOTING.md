# Troubleshooting

Start with the diagnostic tool, then check the log:

```
HoYoRefreshDiag info
notepad %LOCALAPPDATA%\HoYoRefreshSwitcher\logs\app.log
```

Every switch is logged step by step: current mode, available rates, dry-run result, apply result, read-back.

---

### The rate doesn't change when I start a game

1. Is **Automatic switching** ticked? (A grey tray icon means it's off.)
2. Does *Supported game running* say **Yes** while the game is open? If not, find the game's real executable name in Task Manager → Details and add it to the game list. You can also run `HoYoRefreshDiag watch GameName.exe` to confirm it is detected.
3. Did you switch manually while the game was running? A manual choice is kept until the next game start/stop.
4. Is the display already at the gaming rate? Then nothing needs to happen ("Already at 60 Hz" in the status).

### "60 Hz is not available on …" / "144 Hz is not available on …"

The display doesn't offer that rate **at its current resolution**. `HoYoRefreshDiag info` lists the rates it does offer. The app never forces a rate Windows doesn't list. Change the rate in the settings to one that is offered, or reset the resolution to the panel's native resolution.

### "Display not available: The built-in display is not active"

With `targetDisplay` set to `Internal`, the app only controls the laptop panel. It leaves everything alone while the lid is closed or Windows uses only an external monitor. To control the primary display instead (e.g. on a desktop PC), set `"targetDisplay": "Primary"` in `settings.json`.

### The game flickers / switches back to 144 Hz in fullscreen

Some games set their own refresh rate in **exclusive fullscreen**. Use the game's *Fullscreen* / *Borderless* mode, which follows the desktop rate, or set the game's own refresh/FPS option. The app changes the Windows desktop mode only.

### The tray icon is red

The last switch failed. Hover over the icon or open the window to see the error, and check the log. Common causes:

- PC locked or on the secure desktop (UAC prompt) at that moment. The app retries automatically, and again after unlock.
- A graphics driver reset or update in progress. Retry with the buttons in the window.

### "Start with Windows" is ticked but the app doesn't start

- Check *Task Manager → Startup apps*. If **HoYoRefreshSwitcher** is *Disabled* there, Windows ignores the entry. The checkbox then reads "(disabled in Task Manager)". Re-ticking the checkbox in the app re-enables it.
- Was the app started from inside another sandboxed app (for example a terminal inside a Microsoft Store / MSIX app)? Windows redirects such programs' registry writes into that app's private storage, so the startup entry never reaches the real registry. Start the app from File Explorer or the Start menu and tick the option again.
- If you moved or renamed the folder, open the exe once from its new location; the startup entry is updated automatically.
- Windows starts Run-key apps a few seconds after the desktop appears. That's normal.

### The display stayed at 60 Hz after the app crashed or was killed

Start the app again. With no game running it restores the desktop rate within a second. Even without the app, a reboot restores it, because the gaming rate is never saved to Windows' settings.

### SmartScreen / antivirus warning

The exe isn't code-signed. You can build it yourself from source (see README) to be sure of what it contains. The app needs no admin rights, installs no service or scheduled task, and doesn't open handles to game processes.

### Settings were reset

If `settings.json` couldn't be read (e.g. a typo while editing by hand), the app starts with defaults and keeps the broken file as `settings.invalid.json` in the same folder.

### Reporting a problem

Attach:

1. The output of `HoYoRefreshDiag info > report.txt`
2. `%LOCALAPPDATA%\HoYoRefreshSwitcher\logs\app.log`
3. Laptop model, GPU, and whether a MUX switch / Advanced Optimus is enabled.
