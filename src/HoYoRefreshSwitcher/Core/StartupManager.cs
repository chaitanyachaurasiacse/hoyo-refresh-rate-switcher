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

            // Windows 11 only launches Run entries that are approved in Startup apps. A missing approval
            // value is NOT treated as enabled (observed on build 26200: the entry was skipped at login),
            // so write the same "enabled" value Task Manager writes. The user explicitly asked for this,
            // so it also replaces an earlier "disabled" flag.
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
        /// Adds the "enabled" approval if there is none. An existing value is left alone, so a "disabled"
        /// choice made in Task Manager / Settings is respected.
        /// </summary>
        private static void EnsureApproved()
        {
            using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            {
                if (approved.GetValue(ValueName) == null)
                {
                    approved.SetValue(ValueName, EnabledFlag, RegistryValueKind.Binary);
                    Logger.Info("Start with Windows: added missing Startup apps approval.");
                }
            }
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

                // Repairs installs made by v1.0.0, which deleted the approval value.
                EnsureApproved();
            }
            catch (Exception ex)
            {
                Logger.Error("Could not update the Start with Windows entry.", ex);
            }
        }
    }
}
