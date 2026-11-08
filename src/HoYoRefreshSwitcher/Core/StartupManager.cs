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

            // The user explicitly asked for this, so clear a previous "disabled" flag from Startup apps.
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
            {
                if (approved?.GetValue(ValueName) != null)
                    approved.DeleteValue(ValueName, false);
            }

            Logger.Info(enabled ? "Start with Windows enabled: " + ExpectedCommand : "Start with Windows disabled.");
        }

        /// <summary>Keeps the registry in line with the setting, and fixes the path if the exe was moved.</summary>
        public static void Sync(bool shouldBeEnabled)
        {
            try
            {
                if (!shouldBeEnabled)
                {
                    if (IsEnabled())
                        SetEnabled(false);
                    return;
                }

                string current;
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    current = key?.GetValue(ValueName) as string;

                if (!string.Equals(current, ExpectedCommand, StringComparison.OrdinalIgnoreCase))
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                        key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
                    Logger.Info("Start with Windows entry updated: " + ExpectedCommand);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Could not update the Start with Windows entry.", ex);
            }
        }
    }
}
