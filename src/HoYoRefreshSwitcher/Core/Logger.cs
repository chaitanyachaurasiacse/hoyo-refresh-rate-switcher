using System;
using System.IO;
using System.Text;

namespace HoYoRefreshSwitcher.Core
{
    /// <summary>Tiny append-only text log in %LOCALAPPDATA%\HoYoRefreshSwitcher\logs (rolls at 1 MB).</summary>
    public static class Logger
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();

        public static string LogDirectory { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoYoRefreshSwitcher", "logs");

        public static string LogFile => Path.Combine(LogDirectory, "app.log");

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string message, Exception ex) => Write("ERROR", message + Environment.NewLine + ex);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var fi = new FileInfo(LogFile);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = Path.Combine(LogDirectory, "app.old.log");
                        if (File.Exists(old))
                            File.Delete(old);
                        File.Move(LogFile, old);
                    }

                    File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
