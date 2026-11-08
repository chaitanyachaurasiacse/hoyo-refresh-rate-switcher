using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using HoYoRefreshSwitcher.Core;
using HoYoRefreshSwitcher.UI;

namespace HoYoRefreshSwitcher
{
    internal static class Program
    {
        private const string MutexName = @"Local\HoYoRefreshSwitcher.SingleInstance";
        private const string ShowEventName = @"Local\HoYoRefreshSwitcher.ShowWindow";

        private static TrayApplicationContext _context;
        private static Icon _appIcon;

        /// <summary>The icon embedded in the exe.</summary>
        public static Icon AppIcon =>
            _appIcon ?? (_appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application);

        [STAThread]
        private static void Main(string[] args)
        {
            bool minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

            // Single instance: a second launch just asks the running instance to show its window.
            using (var mutex = new Mutex(true, MutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(ShowEventName))
                            ev.Set();
                    }
                    catch (Exception)
                    {
                        // The other instance is starting or exiting; nothing else to do.
                    }
                    return;
                }

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += (s, e) => OnUnhandled(e.Exception, fatal: false);
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => OnUnhandled(e.ExceptionObject as Exception, fatal: true);

                    Logger.Info($"=== Started v{Application.ProductVersion} (pid {System.Diagnostics.Process.GetCurrentProcess().Id}, args: {string.Join(" ", args)}) ===");

                    _context = new TrayApplicationContext(minimized, showEvent);
                    Application.Run(_context);

                    Logger.Info("=== Exited ===");
                }

                GC.KeepAlive(mutex);
            }
        }

        private static void OnUnhandled(Exception ex, bool fatal)
        {
            Logger.Error(fatal ? "Fatal unhandled exception." : "Unhandled UI exception.", ex ?? new Exception("(unknown)"));

            if (fatal)
            {
                // Crash recovery: do not leave the gaming rate behind if no game is running.
                _context?.Controller?.RestoreDesktopRateIfSafe("Crash");
                return;
            }

            _context?.ShowError("An unexpected error occurred and was logged: " + ex?.Message);
        }
    }
}
