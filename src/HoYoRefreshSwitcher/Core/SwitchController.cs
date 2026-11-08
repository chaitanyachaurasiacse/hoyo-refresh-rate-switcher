using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HoYoRefreshSwitcher.Display;
using HoYoRefreshSwitcher.Games;
using Microsoft.Win32;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace HoYoRefreshSwitcher.Core
{
    /// <summary>
    /// The automatic-switching logic. Runs entirely on the UI thread.
    ///
    /// Rules:
    ///  * Acts only on EDGES: "no game" -> "at least one game" (switch to the gaming rate) and
    ///    "at least one game" -> "no game" (switch to the desktop rate). Opening or closing a second
    ///    game while another is still running is not an edge, so 60 Hz is held until ALL games exit.
    ///  * Between edges nothing is executed, so a manual change made while a game runs is respected.
    ///  * The rule is also re-applied ("reconcile") at start-up, after resume from sleep, when the
    ///    display topology changes, when auto switching is re-enabled, and when settings change.
    ///  * A failed automatic switch is retried a few times, then left alone until the next edge.
    /// </summary>
    public sealed class SwitchController : IDisposable
    {
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 5000;

        private readonly WinFormsTimer _timer = new WinFormsTimer();
        private SynchronizationContext _ui;
        private AppSettings _settings;
        private HashSet<string> _gameNames;

        private bool? _gameRunning;                 // null until the first scan
        private List<string> _runningGames = new List<string>();
        private DateTime? _reconcileAtUtc;
        private string _reconcileReason;
        private int _retryCount;
        private bool _lastSwitchFailed;
        private bool _busy;
        private bool _gamingRateSetByApp;
        private string _topology;
        private bool _disposed;

        public SwitchController(AppSettings settings)
        {
            _settings = settings;
            _gameNames = settings.NormalizedGameNames();
            _timer.Tick += (s, e) => Tick();
        }

        // ------------------------------------------------------------------ state for the UI

        public AppSettings Settings => _settings;
        public DisplayInfo Display { get; private set; }
        public string DisplayError { get; private set; }
        public IReadOnlyList<string> RunningGames => _runningGames;
        public bool GameRunning => _runningGames.Count > 0;
        public bool AutoEnabled => _settings.AutoSwitchEnabled;

        /// <summary>The rate automatic switching wants right now, or null when auto switching is off.</summary>
        public int? AutoTargetRate => _settings.AutoSwitchEnabled
            ? (GameRunning ? _settings.GamingRefreshRate : _settings.DesktopRefreshRate)
            : (int?)null;

        public string Status { get; private set; } = "Starting...";
        public bool StatusIsError { get; private set; }
        public string LastChange { get; private set; } = "None yet";

        /// <summary>Raised (on the UI thread) whenever anything shown in the UI may have changed.</summary>
        public event EventHandler StateChanged;

        /// <summary>Raised (on the UI thread) when a switch fails; argument is the error message.</summary>
        public event EventHandler<string> SwitchFailed;

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Must be called on the UI thread after a WinForms control exists (so a sync context is installed).</summary>
        public void Start()
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            RefreshDisplay();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            Logger.Info($"Controller started. Auto={_settings.AutoSwitchEnabled}, gaming={_settings.GamingRefreshRate} Hz, desktop={_settings.DesktopRefreshRate} Hz, " +
                        $"interval={_settings.PollIntervalSeconds} s, games=[{string.Join(", ", _settings.GameProcesses)}], target={_settings.TargetDisplay}");
            Logger.Info("Display: " + (Display != null ? Display.ToString() + " | rates: " + Display.SupportedRatesText : "not found - " + DisplayError));

            _timer.Interval = _settings.PollIntervalSeconds * 1000;
            Tick();
            _timer.Start();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }

        // ------------------------------------------------------------------ public actions

        public void ManualSwitch(int hz)
        {
            _reconcileAtUtc = null;
            _retryCount = 0;
            ApplyRate(hz, automatic: false, reason: "manual");
        }

        public void SetAutoEnabled(bool enabled)
        {
            if (_settings.AutoSwitchEnabled == enabled)
                return;
            _settings.AutoSwitchEnabled = enabled;
            _settings.Save();
            Logger.Info("Automatic switching " + (enabled ? "enabled." : "disabled."));

            if (enabled)
            {
                _retryCount = 0;
                Reconcile("automatic switching enabled");
            }
            else
            {
                _reconcileAtUtc = null;
                SetStatus("Automatic switching is off. The display will not be changed automatically.", false);
                RaiseStateChanged();
            }
        }

        public void SetStartWithWindows(bool enabled)
        {
            _settings.StartWithWindows = enabled;
            _settings.Save();
            StartupManager.SetEnabled(enabled);
            RaiseStateChanged();
        }

        /// <summary>Applies new settings from the UI (already validated) and re-evaluates the rule.</summary>
        public void UpdateSettings(AppSettings updated)
        {
            updated.Normalize();
            _settings = updated;
            _settings.Save();
            _gameNames = _settings.NormalizedGameNames();
            _timer.Interval = _settings.PollIntervalSeconds * 1000;
            StartupManager.Sync(_settings.StartWithWindows);
            Logger.Info($"Settings saved. gaming={_settings.GamingRefreshRate} Hz, desktop={_settings.DesktopRefreshRate} Hz, " +
                        $"interval={_settings.PollIntervalSeconds} s, games=[{string.Join(", ", _settings.GameProcesses)}]");

            // Re-scan with the new game list, then apply the rule once.
            ScanGames(out _, out _);
            _retryCount = 0;
            Reconcile("settings changed");
        }

        /// <summary>Re-reads display information (e.g. when the window is opened).</summary>
        public void Refresh()
        {
            RefreshDisplay();
            RaiseStateChanged();
        }

        /// <summary>
        /// Called on exit and on an unexpected crash. If the app itself lowered the display to the gaming
        /// rate and no game is running any more, put the desktop rate back. If a game is still running
        /// the gaming rate is left alone (it is a temporary mode, so a reboot also restores the saved rate).
        /// </summary>
        public void RestoreDesktopRateIfSafe(string reason)
        {
            try
            {
                if (!_gamingRateSetByApp || !_settings.AutoSwitchEnabled)
                    return;

                var games = ProcessScanner.FindRunning(_gameNames);
                var target = DisplayManager.FindTarget(_settings.TargetDisplay, out _);
                if (target == null || target.RefreshRate != _settings.GamingRefreshRate)
                    return;

                if (games.Count > 0)
                {
                    Logger.Info($"{reason}: leaving {target.RefreshRate} Hz because {string.Join(", ", games)} is still running.");
                    return;
                }

                if (!target.Supports(_settings.DesktopRefreshRate))
                    return;

                var r = DisplayManager.SetRefreshRate(target.GdiDeviceName, _settings.DesktopRefreshRate, persist: true);
                Logger.Info($"{reason}: restore {_settings.DesktopRefreshRate} Hz -> {(r.Success ? "OK" : r.Message)}");
            }
            catch (Exception ex)
            {
                Logger.Error(reason + ": restore failed.", ex);
            }
        }

        // ------------------------------------------------------------------ core loop

        private void Tick()
        {
            if (_disposed || _busy)
                return;

            try
            {
                bool first = !_gameRunning.HasValue;
                bool wasRunning = _gameRunning ?? false;
                ScanGames(out var started, out var stopped);
                bool listChanged = first || started.Count > 0 || stopped.Count > 0;

                foreach (var g in started) Logger.Info("Game detected: " + g);
                foreach (var g in stopped) Logger.Info("Game exited: " + g);

                if (first)
                {
                    Reconcile(GameRunning ? $"started while {string.Join(", ", _runningGames)} is running" : "application started");
                }
                else if (wasRunning != GameRunning)
                {
                    _retryCount = 0;
                    Reconcile(GameRunning ? $"{string.Join(", ", started)} started" : "all supported games closed");
                }
                else if (_reconcileAtUtc.HasValue && DateTime.UtcNow >= _reconcileAtUtc.Value)
                {
                    Reconcile(_reconcileReason);
                }
                else if (listChanged)
                {
                    RaiseStateChanged();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error in monitoring loop.", ex);
                SetStatus("Internal error: " + ex.Message, true);
                RaiseStateChanged();
            }
        }

        private void ScanGames(out List<string> started, out List<string> stopped)
        {
            var now = ProcessScanner.FindRunning(_gameNames);
            started = now.Except(_runningGames, StringComparer.OrdinalIgnoreCase).ToList();
            stopped = _runningGames.Except(now, StringComparer.OrdinalIgnoreCase).ToList();
            _runningGames = now;
            _gameRunning = now.Count > 0;
        }

        private void Reconcile(string reason)
        {
            _reconcileAtUtc = null;
            if (!_settings.AutoSwitchEnabled)
            {
                RefreshDisplay();
                SetStatus("Automatic switching is off. The display will not be changed automatically.", false);
                RaiseStateChanged();
                return;
            }

            int desired = GameRunning ? _settings.GamingRefreshRate : _settings.DesktopRefreshRate;
            ApplyRate(desired, automatic: true, reason: reason);
        }

        private void ApplyRate(int hz, bool automatic, string reason)
        {
            if (_busy)
                return;
            _busy = true;
            try
            {
                RefreshDisplay();
                if (Display == null)
                {
                    Fail($"Display not available: {DisplayError}", automatic, reason, notify: false);
                    return;
                }

                if (Display.RefreshRate == hz)
                {
                    _lastSwitchFailed = false;
                    _retryCount = 0;
                    SetStatus($"Already at {hz} Hz ({reason}); no change needed.", false);
                    Logger.Info($"{reason}: display already at {hz} Hz, nothing to do.");
                    return;
                }

                if (!Display.Supports(hz))
                {
                    // Never attempt a mode Windows does not offer. Not retried: it will not appear by itself.
                    string msg = $"{hz} Hz is not available on {Display.FriendlyName} (offers {Display.SupportedRatesText}). Display left at {Display.RefreshRate} Hz.";
                    _lastSwitchFailed = true;
                    SetStatus(msg, true);
                    Logger.Warn($"{reason}: {msg}");
                    SwitchFailed?.Invoke(this, msg);
                    return;
                }

                Logger.Info($"{reason}: switching {Display.GdiDeviceName} {Display.RefreshRate} -> {hz} Hz");
                bool persist = hz == _settings.DesktopRefreshRate;
                var r = DisplayManager.SetRefreshRate(Display.GdiDeviceName, hz, persist);
                foreach (var line in r.Trace)
                    Logger.Info("    " + line);
                RefreshDisplay();

                if (r.Success)
                {
                    _lastSwitchFailed = false;
                    _retryCount = 0;
                    _gamingRateSetByApp = automatic && hz == _settings.GamingRefreshRate;
                    LastChange = $"{DateTime.Now:HH:mm:ss}  {r.FromHz} Hz → {r.ActualHz} Hz  ({reason})";
                    SetStatus($"Switched to {r.ActualHz} Hz ({reason}) and verified.", false);
                }
                else
                {
                    Fail("Switch failed: " + r.Message, automatic, reason, notify: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error while switching.", ex);
                Fail("Switch failed: " + ex.Message, automatic, reason, notify: true);
            }
            finally
            {
                _busy = false;
                RaiseStateChanged();
            }
        }

        private void Fail(string message, bool automatic, string reason, bool notify)
        {
            _lastSwitchFailed = true;
            SetStatus(message, true);
            Logger.Error($"{reason}: {message}");

            if (automatic)
            {
                if (_retryCount < MaxRetries)
                {
                    _retryCount++;
                    string baseReason = reason.Contains(" (retry ") ? reason.Substring(0, reason.IndexOf(" (retry ", StringComparison.Ordinal)) : reason;
                    RequestReconcile($"{baseReason} (retry {_retryCount}/{MaxRetries})", RetryDelayMs);
                    Logger.Info($"Will retry in {RetryDelayMs / 1000} s.");
                    notify = notify && _retryCount == 1;
                }
                else
                {
                    Logger.Warn("Giving up until the next game start/stop or display change.");
                }
            }

            if (notify)
                SwitchFailed?.Invoke(this, message);
        }

        private void RequestReconcile(string reason, int delayMs)
        {
            _reconcileReason = reason;
            _reconcileAtUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
        }

        private void RefreshDisplay()
        {
            try
            {
                var displays = DisplayManager.GetActiveDisplays(out var ccdError);
                Display = DisplayManager.SelectTarget(displays, _settings.TargetDisplay, ccdError, out var error);
                DisplayError = error;
                _topology = _topology ?? DisplayManager.GetTopologySignature(displays);
            }
            catch (Exception ex)
            {
                Display = null;
                DisplayError = ex.Message;
                Logger.Error("Could not enumerate displays.", ex);
            }
        }

        private void SetStatus(string text, bool isError)
        {
            Status = text;
            StatusIsError = isError;
        }

        private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        // ------------------------------------------------------------------ system events (arrive on a background thread)

        private void Post(Action action)
        {
            _ui?.Post(_ =>
            {
                if (_disposed)
                    return;
                try { action(); }
                catch (Exception ex) { Logger.Error("Error handling system event.", ex); }
            }, null);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                Post(() => Logger.Info("System is going to sleep."));
            }
            else if (e.Mode == PowerModes.Resume)
            {
                Post(() =>
                {
                    Logger.Info("System resumed; re-checking in 3 s.");
                    RequestReconcile("resumed from sleep", 3000);
                });
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Post(() =>
            {
                if (_busy)
                    return; // our own change; ApplyRate refreshes afterwards
                var displays = DisplayManager.GetActiveDisplays(out var ccdError);
                Display = DisplayManager.SelectTarget(displays, _settings.TargetDisplay, ccdError, out var error);
                DisplayError = error;

                string topo = DisplayManager.GetTopologySignature(displays);
                if (!string.Equals(topo, _topology, StringComparison.Ordinal))
                {
                    _topology = topo;
                    Logger.Info($"Display configuration changed ({displays.Count} active). Target: " +
                                (Display != null ? Display.ToString() : "not found - " + DisplayError));
                    _retryCount = 0;
                    RequestReconcile("display configuration changed", 2000);
                }

                RaiseStateChanged();
            });
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect)
            {
                Post(() =>
                {
                    if (_lastSwitchFailed && _settings.AutoSwitchEnabled)
                    {
                        Logger.Info("Session unlocked after a failed switch; re-checking.");
                        _retryCount = 0;
                        RequestReconcile("session unlocked", 1000);
                    }
                });
            }
        }
    }
}
