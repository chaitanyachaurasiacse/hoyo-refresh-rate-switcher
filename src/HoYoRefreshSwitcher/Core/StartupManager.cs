using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HoYoRefreshSwitcher.Core
{
    /// <summary>
    /// "Start with Windows" via the per-user Run key (HKCU\...\CurrentVersion\Run).
    /// This is the standard mechanism Windows' own "Startup apps" page manages; it needs no admin
    /// rights and no scheduled task, and the app starts inside the user's interactive session,
    /// which is required for changing display modes.
    ///
    /// The registry entry itself is the source of truth for whether the option is on. The settings
    /// file only mirrors it, and app start-up never deletes the entry because the file disagrees.
    /// Only the user's own toggle (in the app, or in Task Manager / Settings > Startup apps) turns it off.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "HoYoRefreshSwitcher";

        public static string ExpectedCommand => $"\"{Application.ExecutablePath}\" --minimized";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                return key?.GetValue(ValueName) is string s && s.Length > 0;
        }

        /// <summary>True when the user switched the entry off in Task Manager / Settings > Startup apps.</summary>
        public static bool IsDisabledInStartupApps()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
                return key?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                    key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
                else if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName, false);
            }

            // Mark the entry as enabled in Startup apps, exactly as Task Manager does when you enable an
            // app there. The user explicitly asked for this, so it also clears an earlier "disabled" flag.
            using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            {
                if (enabled)
                    approved.SetValue(ValueName, EnabledFlag, RegistryValueKind.Binary);
                else if (approved.GetValue(ValueName) != null)
                    approved.DeleteValue(ValueName, false);
            }

            Logger.Info(enabled ? "Start with Windows enabled: " + ExpectedCommand : "Start with Windows disabled.");
        }

        /// <summary>StartupApproved value meaning "enabled": 0x02 followed by 11 zero bytes (as written by Task Manager).</summary>
        private static byte[] EnabledFlag => new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>
        /// Called at app start. Fixes the entry's path if the exe was moved and returns whether the
        /// option is on. It never creates or deletes the entry, so a stale settings file cannot undo
        /// the user's choice.
        /// </summary>
        public static bool Sync()
        {
            try
            {
                string current;
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    current = key?.GetValue(ValueName) as string;

                if (string.IsNullOrEmpty(current))
                    return false;

                if (!string.Equals(current, ExpectedCommand, StringComparison.OrdinalIgnoreCase))
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                        key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
                    Logger.Info("Start with Windows entry updated to the new location: " + ExpectedCommand);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Could not check the Start with Windows entry.", ex);
                return IsEnabledSafe();
            }
        }

        private static bool IsEnabledSafe()
        {
            try { return IsEnabled(); }
            catch { return false; }
        }
    }
}
