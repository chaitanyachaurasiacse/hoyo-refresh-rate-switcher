using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HoYoRefreshSwitcher.Core;

namespace HoYoRefreshSwitcher.UI
{
    /// <summary>The main window: status, controls and settings. Closing it hides it to the tray.</summary>
    internal sealed class MainForm : Form
    {
        private readonly SwitchController _controller;
        private readonly TrayApplicationContext _app;
        private bool _closing;
        private bool _updating;

        // status
        private readonly Label _valDisplay = Value();
        private readonly Label _valRate = Value(bold: true);
        private readonly Label _valSupported = Value();
        private readonly Label _valGame = Value();
        private readonly Label _valTarget = Value();
        private readonly Label _valAuto = Value();
        private readonly Label _valStatus = Value();
        private readonly Label _valLast = Value();

        // controls
        private readonly CheckBox _chkAuto = new CheckBox { Text = "Automatic switching", AutoSize = true };
        private readonly CheckBox _chkStartup = new CheckBox { Text = "Start with Windows", AutoSize = true };
        private readonly Button _btnGaming = NewButton("Switch to 60 Hz");
        private readonly Button _btnDesktop = NewButton("Switch to 144 Hz");
        private readonly Button _btnTray = NewButton("Minimize to tray");
        private readonly Button _btnExit = NewButton("Exit");

        // settings
        private readonly ComboBox _cmbGaming = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        private readonly ComboBox _cmbDesktop = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        private readonly NumericUpDown _numInterval = new NumericUpDown { Minimum = AppSettings.MinInterval, Maximum = AppSettings.MaxInterval, Width = 60 };
        private readonly TextBox _txtGames = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 80, Width = 300, AcceptsReturn = true };
        private readonly Button _btnSave = NewButton("Save settings");
        private readonly Button _btnDefaults = NewButton("Default games");
        private readonly Button _btnLogs = NewButton("Open log");
        private readonly Button _btnConfig = NewButton("Open settings folder");

        public MainForm(SwitchController controller, TrayApplicationContext app)
        {
            _controller = controller;
            _app = app;

            Text = "HoYo Refresh Rate Switcher";
            Icon = Program.AppIcon;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = true;

            BuildLayout();
            WireEvents();
            LoadSettingsIntoEditor();
            UpdateView();

            _controller.StateChanged += OnControllerStateChanged;
        }

        // ------------------------------------------------------------------ layout

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10),
                Dock = DockStyle.Fill,
            };

            // Status
            var status = Grid();
            AddRow(status, "Display:", _valDisplay);
            AddRow(status, "Current refresh rate:", _valRate);
            AddRow(status, "Available rates:", _valSupported);
            AddRow(status, "Supported game running:", _valGame);
            AddRow(status, "Target refresh rate:", _valTarget);
            AddRow(status, "Automatic switching:", _valAuto);
            AddRow(status, "Status:", _valStatus);
            AddRow(status, "Last change:", _valLast);
            root.Controls.Add(Group("Status", status));

            // Controls
            var controls = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            var checks = Flow();
            checks.Controls.Add(_chkAuto);
            checks.Controls.Add(_chkStartup);
            var buttons = Flow();
            buttons.Controls.AddRange(new Control[] { _btnGaming, _btnDesktop, _btnTray, _btnExit });
            controls.Controls.Add(checks);
            controls.Controls.Add(buttons);
            root.Controls.Add(Group("Controls", controls));

            // Settings
            var settings = Grid();
            AddRow(settings, "Gaming refresh rate:", _cmbGaming);
            AddRow(settings, "Desktop refresh rate:", _cmbDesktop);
            var intervalRow = Flow();
            intervalRow.Controls.Add(_numInterval);
            intervalRow.Controls.Add(new Label { Text = "seconds", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            AddRow(settings, "Check for games every:", intervalRow);
            AddRow(settings, "Game executables\r\n(one per line):", _txtGames);
            var settingButtons = Flow();
            settingButtons.Controls.AddRange(new Control[] { _btnSave, _btnDefaults, _btnLogs, _btnConfig });
            settings.Controls.Add(settingButtons, 0, settings.RowCount);
            settings.SetColumnSpan(settingButtons, 2);
            settings.RowCount++;
            root.Controls.Add(Group("Settings", settings));

            root.Controls.Add(new Label
            {
                Text = "Closing this window keeps the app running in the system tray. Use Exit to quit.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 4, 3, 0),
            });

            Controls.Add(root);
        }

        private void WireEvents()
        {
            _chkAuto.CheckedChanged += (s, e) => { if (!_updating) _controller.SetAutoEnabled(_chkAuto.Checked); };
            _chkStartup.CheckedChanged += (s, e) =>
            {
                if (_updating) return;
                try { _controller.SetStartWithWindows(_chkStartup.Checked); }
                catch (Exception ex) { ShowError("Could not change the startup setting: " + ex.Message); UpdateView(); }
            };
            _btnGaming.Click += (s, e) => _controller.ManualSwitch(_controller.Settings.GamingRefreshRate);
            _btnDesktop.Click += (s, e) => _controller.ManualSwitch(_controller.Settings.DesktopRefreshRate);
            _btnTray.Click += (s, e) => Close();
            _btnExit.Click += (s, e) => _app.ExitApplication();
            _btnSave.Click += (s, e) => SaveSettings();
            _btnDefaults.Click += (s, e) => _txtGames.Lines = AppSettings.DefaultGames;
            _btnLogs.Click += (s, e) => OpenPath(File.Exists(Logger.LogFile) ? Logger.LogFile : Logger.LogDirectory);
            _btnConfig.Click += (s, e) => OpenPath(AppSettings.SettingsDirectory);
        }

        // ------------------------------------------------------------------ view

        private void OnControllerStateChanged(object sender, EventArgs e)
        {
            if (!IsDisposed && Visible)
                UpdateView();
        }

        public void UpdateView()
        {
            _updating = true;
            try
            {
                var c = _controller;
                var s = c.Settings;
                var d = c.Display;

                if (d != null)
                {
                    _valDisplay.Text = $"{d.FriendlyName}  ({d.GdiDeviceName}, {d.Connection}, via {d.AdapterName})";
                    _valRate.Text = $"{d.RefreshRate} Hz   ({d.Width} × {d.Height})";
                    _valSupported.Text = d.SupportedRatesText;
                }
                else
                {
                    _valDisplay.Text = "Not found: " + (c.DisplayError ?? "unknown error");
                    _valRate.Text = "—";
                    _valSupported.Text = "—";
                }

                _valGame.Text = c.GameRunning ? "Yes — " + string.Join(", ", c.RunningGames) : "No";
                _valTarget.Text = c.AutoTargetRate.HasValue
                    ? $"{c.AutoTargetRate} Hz ({(c.GameRunning ? "gaming" : "desktop")})"
                    : "— (automatic switching is off)";
                _valAuto.Text = s.AutoSwitchEnabled ? "Enabled" : "Disabled";
                _valStatus.Text = c.Status;
                _valStatus.ForeColor = c.StatusIsError ? Color.Firebrick : SystemColors.ControlText;
                _valLast.Text = c.LastChange;

                _chkAuto.Checked = s.AutoSwitchEnabled;
                _chkStartup.Checked = StartupManager.IsEnabled();
                _chkStartup.Text = StartupManager.IsEnabled() && StartupManager.IsDisabledInStartupApps()
                    ? "Start with Windows (disabled in Task Manager)"
                    : "Start with Windows";

                _btnGaming.Text = $"Switch to {s.GamingRefreshRate} Hz";
                _btnDesktop.Text = $"Switch to {s.DesktopRefreshRate} Hz";
                _btnGaming.Enabled = d != null && d.Supports(s.GamingRefreshRate);
                _btnDesktop.Enabled = d != null && d.Supports(s.DesktopRefreshRate);
            }
            finally
            {
                _updating = false;
            }
        }

        private void LoadSettingsIntoEditor()
        {
            var s = _controller.Settings;
            var rates = new SortedSet<int>(_controller.Display?.SupportedRefreshRates ?? new List<int> { 60, 144 })
            {
                s.GamingRefreshRate,
                s.DesktopRefreshRate,
            };

            FillCombo(_cmbGaming, rates, s.GamingRefreshRate);
            FillCombo(_cmbDesktop, rates, s.DesktopRefreshRate);
            _numInterval.Value = Math.Max(_numInterval.Minimum, Math.Min(_numInterval.Maximum, s.PollIntervalSeconds));
            _txtGames.Lines = s.GameProcesses.ToArray();
        }

        private static void FillCombo(ComboBox combo, IEnumerable<int> rates, int selected)
        {
            combo.Items.Clear();
            foreach (int r in rates)
                combo.Items.Add(new RateItem(r));
            combo.SelectedItem = combo.Items.Cast<RateItem>().FirstOrDefault(i => i.Hz == selected);
        }

        private void SaveSettings()
        {
            int gaming = (_cmbGaming.SelectedItem as RateItem)?.Hz ?? _controller.Settings.GamingRefreshRate;
            int desktop = (_cmbDesktop.SelectedItem as RateItem)?.Hz ?? _controller.Settings.DesktopRefreshRate;

            if (gaming == desktop)
            {
                ShowError("The gaming and desktop refresh rates must be different.");
                return;
            }

            var names = _txtGames.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var invalid = names.Where(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0).ToList();
            if (invalid.Count > 0)
            {
                ShowError("These entries are not valid executable names:\r\n" + string.Join("\r\n", invalid) +
                          "\r\n\r\nEnter only the file name, e.g. GenshinImpact.exe");
                return;
            }

            if (names.Count == 0 &&
                MessageBox.Show(this, "The game list is empty, so the display will never switch to the gaming rate. Save anyway?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            var updated = _controller.Settings.Clone();
            updated.GamingRefreshRate = gaming;
            updated.DesktopRefreshRate = desktop;
            updated.PollIntervalSeconds = (int)_numInterval.Value;
            updated.GameProcesses = names;
            _controller.UpdateSettings(updated);
            LoadSettingsIntoEditor();
            UpdateView();
        }

        // ------------------------------------------------------------------ window behaviour

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadSettingsIntoEditor();
            UpdateView();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Minimising goes to the tray. The window is closed (and freed) rather than hidden, so the
            // app uses as little memory as possible while in the tray; it is re-created when opened.
            if (WindowState == FormWindowState.Minimized && !_closing)
            {
                _closing = true;
                BeginInvoke(new Action(Close));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _controller.StateChanged -= OnControllerStateChanged;
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ helpers

        private void ShowError(string message) =>
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private static void OpenPath(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.HasExtension(path) ? Path.GetDirectoryName(path) : path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Could not open " + path, ex);
            }
        }

        private static Label Value(bool bold = false) => new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Margin = new Padding(3, 3, 3, 3),
            Font = bold ? new Font("Segoe UI", 9f, FontStyle.Bold) : null,
        };

        private static Button NewButton(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 2, 6, 2),
            UseVisualStyleBackColor = true,
        };

        private static TableLayoutPanel Grid() => new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        private static FlowLayoutPanel Flow() => new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };

        private static GroupBox Group(string title, Control content)
        {
            var g = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 4, 8, 6),
                Margin = new Padding(0, 0, 0, 8),
            };
            content.Dock = DockStyle.Fill;
            g.Controls.Add(content);
            return g;
        }

        private static void AddRow(TableLayoutPanel grid, string caption, Control value)
        {
            int row = grid.RowCount;
            grid.Controls.Add(new Label
            {
                Text = caption,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 3, 12, 3),
            }, 0, row);
            grid.Controls.Add(value, 1, row);
            grid.RowCount = row + 1;
        }

        private sealed class RateItem
        {
            public RateItem(int hz) => Hz = hz;
            public int Hz { get; }
            public override string ToString() => Hz + " Hz";
        }
    }
}
