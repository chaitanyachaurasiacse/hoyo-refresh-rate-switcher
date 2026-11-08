using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using HoYoRefreshSwitcher.Core;

namespace HoYoRefreshSwitcher.UI
{
    /// <summary>
    /// Owns the tray icon, the controller and (lazily) the main window. The app lives as long as this
    /// context, not as long as the window.
    /// </summary>
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly Control _invoker;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miHeader, _miOpen, _miAuto, _miGaming, _miDesktop, _miStartup, _miExit;
        private readonly RegisteredWaitHandle _showWait;
        private MainForm _form;
        private Icon _trayIcon;
        private string _trayIconKey;
        private bool _exiting;

        public SwitchController Controller { get; }

        public TrayApplicationContext(bool startMinimized, EventWaitHandle showEvent)
        {
            // A hidden control gives us a WindowsFormsSynchronizationContext and a way to marshal to the UI thread.
            _invoker = new Control();
            _invoker.CreateControl();
            _ = _invoker.Handle;

            var settings = AppSettings.Load();
            // The Run entry decides whether "Start with Windows" is on; the settings file just mirrors it.
            bool startup = StartupManager.Sync();
            if (settings.StartWithWindows != startup)
            {
                settings.StartWithWindows = startup;
                settings.Save();
            }

            Controller = new SwitchController(settings);
            Controller.StateChanged += (s, e) => UpdateTray();
            Controller.SwitchFailed += (s, msg) => ShowError(msg);

            _miHeader = new ToolStripMenuItem("HoYo Refresh Rate Switcher") { Enabled = false };
            _miOpen = new ToolStripMenuItem("Open", null, (s, e) => ShowMainWindow()) { Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            _miAuto = new ToolStripMenuItem("Automatic switching", null, (s, e) => Controller.SetAutoEnabled(!Controller.AutoEnabled));
            _miGaming = new ToolStripMenuItem("Switch to 60 Hz", null, (s, e) => Controller.ManualSwitch(Controller.Settings.GamingRefreshRate));
            _miDesktop = new ToolStripMenuItem("Switch to 144 Hz", null, (s, e) => Controller.ManualSwitch(Controller.Settings.DesktopRefreshRate));
            _miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            _miExit = new ToolStripMenuItem("Exit", null, (s, e) => ExitApplication());

            _menu = new ContextMenuStrip();
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miHeader, _miOpen, new ToolStripSeparator(),
                _miAuto, _miGaming, _miDesktop, new ToolStripSeparator(),
                _miStartup, new ToolStripSeparator(),
                _miExit,
            });
            _menu.Opening += (s, e) => UpdateMenu();

            _tray = new NotifyIcon
            {
                Icon = Program.AppIcon,
                Text = "HoYo Refresh Rate Switcher",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };

            Controller.Start();
            UpdateTray();

            // A second launch of the exe signals this event; bring our window forward instead.
            _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (state, timedOut) =>
            {
                try { _invoker.BeginInvoke(new Action(ShowMainWindow)); }
                catch (InvalidOperationException) { /* shutting down */ }
            }, null, Timeout.Infinite, false);

            if (!startMinimized)
                ShowMainWindow();
            else
                _invoker.BeginInvoke(new Action(TrimMemory));
        }

        public void ShowMainWindow()
        {
            if (_exiting)
                return;
            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(Controller, this);
                _form.FormClosed += (s, e) =>
                {
                    _form = null;
                    if (!_exiting)
                        _invoker.BeginInvoke(new Action(TrimMemory));
                };
            }

            Controller.Refresh();
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized)
                _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
        }

        public void ExitApplication()
        {
            if (_exiting)
                return;
            _exiting = true;
            Logger.Info("Exit requested.");

            Controller.RestoreDesktopRateIfSafe("Exit");
            Controller.Dispose();
            _showWait.Unregister(null);

            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _trayIcon?.Dispose();

            if (_form != null && !_form.IsDisposed)
                _form.Close();

            _invoker.Dispose();
            ExitThread();
        }

        public void ShowError(string message)
        {
            // The only notification the app ever shows: a failed switch.
            if (!_exiting)
                _tray.ShowBalloonTip(6000, "Refresh rate not changed", Truncate(message, 250), ToolTipIcon.Warning);
        }

        private void ToggleStartup()
        {
            try
            {
                Controller.SetStartWithWindows(!StartupManager.IsEnabled());
            }
            catch (Exception ex)
            {
                Logger.Error("Could not change startup setting.", ex);
                ShowError("Could not change the startup setting: " + ex.Message);
            }
        }

        private void UpdateMenu()
        {
            var s = Controller.Settings;
            var d = Controller.Display;
            _miHeader.Text = d != null ? $"{d.RefreshRate} Hz — {d.FriendlyName}" : "Display not found";
            _miAuto.Checked = s.AutoSwitchEnabled;
            _miGaming.Text = $"Switch to {s.GamingRefreshRate} Hz";
            _miDesktop.Text = $"Switch to {s.DesktopRefreshRate} Hz";
            _miGaming.Enabled = d != null && d.Supports(s.GamingRefreshRate);
            _miDesktop.Enabled = d != null && d.Supports(s.DesktopRefreshRate);
            _miStartup.Checked = StartupManager.IsEnabled();
        }

        private void UpdateTray()
        {
            if (_exiting)
                return;

            var c = Controller;
            var d = c.Display;
            string text = d != null ? d.RefreshRate.ToString() : "!";
            Color color;
            if (d == null || c.StatusIsError) color = TrayIconRenderer.ErrorColor;
            else if (!c.AutoEnabled) color = TrayIconRenderer.NeutralColor;
            else if (d.RefreshRate == c.Settings.GamingRefreshRate) color = TrayIconRenderer.GamingColor;
            else if (d.RefreshRate == c.Settings.DesktopRefreshRate) color = TrayIconRenderer.DesktopColor;
            else color = TrayIconRenderer.NeutralColor;

            string key = text + "|" + color.ToArgb();
            if (key != _trayIconKey)
            {
                try
                {
                    var icon = TrayIconRenderer.Create(text, color);
                    _tray.Icon = icon;
                    _trayIcon?.Dispose();
                    _trayIcon = icon;
                    _trayIconKey = key;
                }
                catch (Exception ex)
                {
                    Logger.Error("Could not draw tray icon.", ex);
                }
            }

            string tip = d != null ? $"HoYo Refresh: {d.RefreshRate} Hz" : "HoYo Refresh: display not found";
            if (c.GameRunning) tip += " · game running";
            if (!c.AutoEnabled) tip += " · auto off";
            if (c.StatusIsError) tip += " · error";
            _tray.Text = Truncate(tip, 63); // NotifyIcon.Text is limited to 63 characters
        }

        /// <summary>
        /// After the window closes, release its memory and give unused pages back to Windows.
        /// Runs once per window close, never periodically.
        /// </summary>
        private static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch (Exception ex)
            {
                Logger.Warn("Memory trim failed: " + ex.Message);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
