<#
.SYNOPSIS
  End-to-end test of HoYo Refresh Rate Switcher on the real display.

.DESCRIPTION
  Drives the real app and checks the real refresh rate of the built-in display.
  Games are simulated by harmless stand-in processes (copies of Windows' ping.exe with game names),
  so the switching logic can be tested without launching any game.

  * If none of the real games is running, the stand-ins use the real names
    (GenshinImpact.exe, StarRail.exe, ZenlessZoneZero.exe).
  * If a real game IS running, the script first tests the app against that real game, then
    temporarily configures stand-in names (HoYoTestGenshin.exe, ...) for the remaining scenarios.

  Your settings.json is backed up and restored. The display will switch several times.
  The app must not already be running.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\e2e-test.ps1
#>
param(
    [string]$AppExe,
    [string]$DiagExe,
    [int]$IdleMeasureSeconds = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $AppExe)  { $AppExe  = Join-Path $root 'src\HoYoRefreshSwitcher\bin\Release\net48\HoYoRefreshSwitcher.exe' }
if (-not $DiagExe) { $DiagExe = Join-Path $root 'src\HoYoRefreshSwitcher.Diag\bin\Release\net48\HoYoRefreshDiag.exe' }
foreach ($f in $AppExe, $DiagExe) { if (-not (Test-Path $f)) { throw "Not found: $f (build first)" } }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$UIA = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

$settingsDir  = Join-Path $env:APPDATA 'HoYoRefreshSwitcher'
$settingsFile = Join-Path $settingsDir 'settings.json'
$backupFile   = Join-Path $settingsDir 'settings.e2e-backup.json'
$logFile      = Join-Path $env:LOCALAPPDATA 'HoYoRefreshSwitcher\logs\app.log'
$fakeDir      = Join-Path $env:TEMP 'HoYoE2E'
$realNames    = 'GenshinImpact', 'StarRail', 'ZenlessZoneZero'

$script:pass = 0; $script:fail = 0; $script:results = @()
$script:fakes = @{}

function Result([bool]$ok, [string]$name, [string]$detail = '') {
    if ($ok) { $script:pass++ } else { $script:fail++ }
    $line = '{0}  {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  [$detail]" } else { '' })
    $script:results += $line
    Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}

function Get-Rate {
    $out = & $DiagExe status
    if ($out -match '\t(\d+) Hz\tsaved (\d+) Hz') { return [pscustomobject]@{ Current = [int]$Matches[1]; Saved = [int]$Matches[2] } }
    throw "Could not parse diag output: $out"
}

function Wait-Rate([int]$hz, [int]$timeoutSec = 12) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if ((Get-Rate).Current -eq $hz) { return [math]::Round($sw.Elapsed.TotalSeconds, 1) }
        Start-Sleep -Milliseconds 400
    }
    return -1
}

function Stay-Rate([int]$hz, [int]$seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) {
        if ((Get-Rate).Current -ne $hz) { return $false }
        Start-Sleep -Milliseconds 500
    }
    return $true
}

function Start-Fake([string]$name) {
    New-Item -ItemType Directory -Force $fakeDir | Out-Null
    $exe = Join-Path $fakeDir "$name.exe"
    if (-not (Test-Path $exe)) { Copy-Item "$env:WINDIR\System32\PING.EXE" $exe }
    $p = Start-Process -FilePath $exe -ArgumentList '-n', '3600', '127.0.0.1' -WindowStyle Hidden -PassThru
    $script:fakes[$name] = $p
}

function Stop-Fake([string]$name) {
    $p = $script:fakes[$name]
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit() }
    $script:fakes.Remove($name)
}

function Get-AppProcs { @(Get-Process HoYoRefreshSwitcher -ErrorAction SilentlyContinue) }

function Start-App([switch]$Minimized) {
    if ($Minimized) { Start-Process $AppExe -ArgumentList '--minimized' | Out-Null } else { Start-Process $AppExe | Out-Null }
    Start-Sleep -Seconds 2
}

function Get-Window([int]$timeoutSec = 10) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, 'HoYo Refresh Rate Switcher')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $w = $UIA::RootElement.FindFirst($Tree::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    }
    throw 'Main window not found'
}

# The managed UIA client sees WinForms buttons as plain panes, so controls are located by name via
# UIA and clicked with a real BM_CLICK message to their window handle (same as a mouse click).
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class E2EWin32 { [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l); }
'@

function Get-Control($window, [string]$name) {
    $byName = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    $all = $window.FindAll($Tree::Descendants, $byName)
    foreach ($el in $all) { if ($el.Current.NativeWindowHandle -ne 0 -and $el.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text) { return $el } }
    throw "Control '$name' not found"
}

function Click($window, [string]$name) {
    $el = Get-Control $window $name
    [E2EWin32]::SendMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # BM_CLICK
}

function Toggle($window, [string]$name) { Click $window $name; Start-Sleep -Milliseconds 500 }

function Get-SavedSettings { Get-Content $settingsFile -Raw | ConvertFrom-Json }

function Write-Settings([string[]]$games) {
    New-Item -ItemType Directory -Force $settingsDir | Out-Null
    $json = [ordered]@{
        autoSwitchEnabled = $true; gamingRefreshRate = 60; desktopRefreshRate = 144
        gameProcesses = $games; pollIntervalSeconds = 2; startWithWindows = $false; targetDisplay = 'Internal'
    } | ConvertTo-Json
    [IO.File]::WriteAllText($settingsFile, $json, (New-Object Text.UTF8Encoding($false)))
}

function Exit-App {
    $procs = Get-AppProcs
    if ($procs.Count -eq 0) { return }
    try { $w = Get-Window 3 } catch { $w = $null }
    if (-not $w) { Start-App; $w = Get-Window }
    Click $w 'Exit'
    foreach ($p in $procs) { $p.WaitForExit(10000) | Out-Null }
}

# ---------------------------------------------------------------------------------------------
if ((Get-AppProcs).Count -gt 0) { throw 'HoYoRefreshSwitcher is already running. Exit it first (tray icon > Exit).' }
if (Test-Path $settingsFile) { Copy-Item $settingsFile $backupFile -Force }
# The test turns "Start with Windows" on and off; keep the user's real entry to restore afterwards.
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$savedRun = (Get-ItemProperty $runKeyPath -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue).HoYoRefreshSwitcher
$savedApproved = (Get-ItemProperty $approvedKeyPath -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue).HoYoRefreshSwitcher
$realRunning = @(Get-Process -Name $realNames -ErrorAction SilentlyContinue)
$startRate = (Get-Rate).Current
Write-Host "Display starts at $startRate Hz. Real games running: $(if ($realRunning) { ($realRunning.Name | Sort-Object -Unique) -join ', ' } else { 'none' })"

try {
    # --- Real game (only if one is running) -------------------------------------------------
    if ($realRunning.Count -gt 0) {
        Write-Settings ($realNames | ForEach-Object { "$_.exe" })
        & $DiagExe set 144 | Out-Null
        Start-App -Minimized
        $t = Wait-Rate 60
        Result ($t -ge 0) "REAL GAME: app started at 144 Hz while $($realRunning[0].Name) runs -> switches to 60 Hz" "after ${t}s"
        Exit-App
        Result ((Get-Rate).Current -eq 60) 'REAL GAME: exiting the app while the game runs leaves 60 Hz alone'
        $names = @{ G = 'HoYoTestGenshin'; S = 'HoYoTestStarRail'; Z = 'HoYoTestZZZ' }
    } else {
        $names = @{ G = 'GenshinImpact'; S = 'StarRail'; Z = 'ZenlessZoneZero' }
    }
    Write-Settings ($names.Values | ForEach-Object { "$_.exe" })
    Write-Host "Stand-in game names: $($names.Values -join ', ')"

    # --- 1. App with no game -----------------------------------------------------------------
    & $DiagExe set 60 --temp | Out-Null
    Start-App
    $t = Wait-Rate 144
    Result ($t -ge 0) '1. Launch app with no game running -> 144 Hz' "from 60 Hz in ${t}s"
    $w = Get-Window
    Result ((Get-AppProcs).Count -eq 1) 'App runs as a single process'

    # --- 2/3. Genshin ------------------------------------------------------------------------
    Start-Fake $names.G; $t = Wait-Rate 60
    Result ($t -ge 0) "2. $($names.G) starts -> 60 Hz" "${t}s"
    $r = Get-Rate
    Result ($r.Saved -eq 144) 'Gaming rate is temporary: Windows still has 144 Hz saved (reboot-safe)' "saved=$($r.Saved)"
    Result (Stay-Rate 60 5) 'No repeated switching while the game keeps running (stable 5 s)'
    Stop-Fake $names.G; $t = Wait-Rate 144
    Result ($t -ge 0) "3. $($names.G) closes -> 144 Hz" "${t}s"

    # --- 4. Star Rail ------------------------------------------------------------------------
    Start-Fake $names.S; $t = Wait-Rate 60
    Result ($t -ge 0) "4. $($names.S) starts -> 60 Hz" "${t}s"
    Stop-Fake $names.S; $t = Wait-Rate 144
    Result ($t -ge 0) "4. $($names.S) closes -> 144 Hz" "${t}s"

    # --- 5/6/7. ZZZ + two games --------------------------------------------------------------
    Start-Fake $names.Z; $t = Wait-Rate 60
    Result ($t -ge 0) "5. $($names.Z) starts -> 60 Hz" "${t}s"
    Start-Fake $names.G
    Result (Stay-Rate 60 4) "6. Second game ($($names.G)) starts -> stays 60 Hz"
    Stop-Fake $names.Z
    Result (Stay-Rate 60 7) "6. Close $($names.Z) while $($names.G) still runs -> stays 60 Hz (7 s)"
    Stop-Fake $names.G; $t = Wait-Rate 144
    Result ($t -ge 0) '7. Last game closes -> 144 Hz' "${t}s"

    # --- crash of a game (process killed abruptly is what Stop-Fake does) is covered above ---

    # --- 8. Disable automatic switching ------------------------------------------------------
    $w = Get-Window
    Toggle $w 'Automatic switching'
    Result (-not (Get-SavedSettings).autoSwitchEnabled) '8. Automatic switching checkbox turns off (and is saved to settings.json)'
    Start-Fake $names.G
    Result (Stay-Rate 144 6) '8. With auto off, starting a game does NOT change the rate'

    # --- 9. Manual switching via GUI ---------------------------------------------------------
    Click $w 'Switch to 60 Hz'; $t = Wait-Rate 60
    Result ($t -ge 0) '9. GUI button "Switch to 60 Hz" -> 60 Hz' "${t}s"
    Click $w 'Switch to 144 Hz'; $t = Wait-Rate 144
    Result ($t -ge 0) '9. GUI button "Switch to 144 Hz" -> 144 Hz' "${t}s"

    # Re-enabling auto applies the rule immediately (game still running -> 60)
    Toggle $w 'Automatic switching'; $t = Wait-Rate 60
    Result ($t -ge 0) 'Re-enabling auto while a game runs -> 60 Hz' "${t}s"

    # Manual override while a game runs is respected until the next start/stop
    Click $w 'Switch to 144 Hz'; Wait-Rate 144 | Out-Null
    Result (Stay-Rate 144 6) 'Manual 144 Hz while a game runs is respected (not undone by polling)'
    Stop-Fake $names.G; Start-Sleep -Seconds 3

    # --- Single instance ---------------------------------------------------------------------
    Start-Process $AppExe | Out-Null; Start-Sleep -Seconds 3
    Result ((Get-AppProcs).Count -eq 1) 'Second launch does not start a second instance'

    # --- 10 (partial). Start with Windows ----------------------------------------------------
    $w = Get-Window
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Toggle $w 'Start with Windows'
    $val = (Get-ItemProperty $runKey -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue).HoYoRefreshSwitcher
    Result ($val -like "*HoYoRefreshSwitcher.exe*--minimized") '10. "Start with Windows" writes HKCU Run entry (no admin, no task)' "$val"
    $approved = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue).HoYoRefreshSwitcher
    Result ($approved -and $approved[0] -eq 2) '10. Entry is marked enabled in Startup apps (as Task Manager does)' "$(if ($approved) { '{0:X2}' -f $approved[0] } else { 'missing' })"

    # A restart of the app with a settings file saying "off" must NOT remove the entry.
    Exit-App
    Write-Settings ($names.Values | ForEach-Object { "$_.exe" })   # startWithWindows = false in the file
    Start-App; $w = Get-Window
    $val = (Get-ItemProperty $runKey -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue).HoYoRefreshSwitcher
    Result ([bool]$val) '10. App start with a stale settings file keeps the startup entry (registry is the source of truth)'
    Result ((Get-SavedSettings).startWithWindows -eq $true) '10. ...and the settings file is corrected to match'
    Toggle $w 'Start with Windows'
    $val = (Get-ItemProperty $runKey -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue).HoYoRefreshSwitcher
    Result (-not $val) '10. Unchecking removes the Run entry'
    $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like '*HoYo*' })
    Result ($tasks.Count -eq 0) 'No scheduled tasks were created'

    # --- Idle resource usage -----------------------------------------------------------------
    $w = Get-Window; Click $w 'Minimize to tray'; Start-Sleep -Seconds 2
    $p = (Get-AppProcs)[0]; $p.Refresh(); $cpu0 = $p.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds $IdleMeasureSeconds
    $p.Refresh(); $cpu1 = $p.TotalProcessorTime.TotalMilliseconds
    $cpuPct = [math]::Round(($cpu1 - $cpu0) / ($IdleMeasureSeconds * 1000 * [Environment]::ProcessorCount) * 100, 3)
    $ws = [math]::Round($p.WorkingSet64 / 1MB, 1); $priv = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    Result ($cpuPct -lt 0.5) "Idle in tray: CPU over $IdleMeasureSeconds s" "$([math]::Round($cpu1 - $cpu0)) ms CPU total = $cpuPct % of machine; working set $ws MB, private $priv MB"

    # --- Crash recovery ----------------------------------------------------------------------
    Start-Fake $names.S; Wait-Rate 60 | Out-Null
    Stop-Process -Id (Get-AppProcs)[0].Id -Force; Start-Sleep -Seconds 1
    Stop-Fake $names.S; Start-Sleep -Seconds 3
    $r = Get-Rate
    Result ($r.Current -eq 60 -and $r.Saved -eq 144) 'Crash: app killed at 60 Hz -> stays 60 Hz, but Windows still has 144 Hz saved' "current=$($r.Current) saved=$($r.Saved)"
    Start-App -Minimized; $t = Wait-Rate 144
    Result ($t -ge 0) 'Crash recovery: restarting the app with no game restores 144 Hz' "${t}s"
    Exit-App
    Result ((Get-AppProcs).Count -eq 0) 'Exit from the GUI ends the process'
}
finally {
    foreach ($n in @($script:fakes.Keys)) { Stop-Fake $n }
    Get-AppProcs | ForEach-Object { Stop-Process -Id $_.Id -Force }
    if (Test-Path $backupFile) { Move-Item $backupFile $settingsFile -Force } elseif (Test-Path $settingsFile) { Remove-Item $settingsFile }
    Remove-ItemProperty $runKeyPath -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue
    Remove-ItemProperty $approvedKeyPath -Name HoYoRefreshSwitcher -ErrorAction SilentlyContinue
    if ($savedRun) { New-ItemProperty $runKeyPath -Name HoYoRefreshSwitcher -Value $savedRun -PropertyType String -Force | Out-Null }
    if ($savedApproved) { New-ItemProperty $approvedKeyPath -Name HoYoRefreshSwitcher -Value $savedApproved -PropertyType Binary -Force | Out-Null }
    Remove-Item $fakeDir -Recurse -Force -ErrorAction SilentlyContinue
    $stillReal = @(Get-Process -Name $realNames -ErrorAction SilentlyContinue)
    $final = if ($stillReal.Count -gt 0) { 60 } else { 144 }
    & $DiagExe set $final $(if ($final -eq 60) { '--temp' }) | Out-Null
}

Write-Host ''
Write-Host "$script:pass passed, $script:fail failed. Display left at $((Get-Rate).Current) Hz." -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
Write-Host "App log: $logFile"
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
