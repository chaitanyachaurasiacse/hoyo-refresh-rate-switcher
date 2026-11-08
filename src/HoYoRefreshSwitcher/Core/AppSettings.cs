using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using HoYoRefreshSwitcher.Display;
using HoYoRefreshSwitcher.Games;

namespace HoYoRefreshSwitcher.Core
{
    /// <summary>
    /// User settings, stored as JSON in %APPDATA%\HoYoRefreshSwitcher\settings.json.
    /// Missing or invalid values fall back to defaults; a corrupt file is renamed and replaced.
    /// </summary>
    [DataContract]
    public sealed class AppSettings
    {
        public static readonly string[] DefaultGames = { "GenshinImpact.exe", "StarRail.exe", "ZenlessZoneZero.exe" };

        public const int MinInterval = 1;
        public const int MaxInterval = 30;

        [DataMember(Name = "autoSwitchEnabled", Order = 1)]
        public bool AutoSwitchEnabled { get; set; }

        [DataMember(Name = "gamingRefreshRate", Order = 2)]
        public int GamingRefreshRate { get; set; }

        [DataMember(Name = "desktopRefreshRate", Order = 3)]
        public int DesktopRefreshRate { get; set; }

        [DataMember(Name = "gameProcesses", Order = 4)]
        public List<string> GameProcesses { get; set; }

        [DataMember(Name = "pollIntervalSeconds", Order = 5)]
        public int PollIntervalSeconds { get; set; }

        [DataMember(Name = "startWithWindows", Order = 6)]
        public bool StartWithWindows { get; set; }

        /// <summary>"Internal" (built-in laptop panel, default) or "Primary".</summary>
        [DataMember(Name = "targetDisplay", Order = 7)]
        public string TargetDisplayName { get; set; }

        public DisplayTarget TargetDisplay =>
            string.Equals(TargetDisplayName, "Primary", StringComparison.OrdinalIgnoreCase) ? DisplayTarget.Primary : DisplayTarget.Internal;

        public AppSettings() => ApplyDefaults();

        [OnDeserializing]
        private void OnDeserializing(StreamingContext _) => ApplyDefaults();

        private void ApplyDefaults()
        {
            AutoSwitchEnabled = true;
            GamingRefreshRate = 60;
            DesktopRefreshRate = 144;
            GameProcesses = DefaultGames.ToList();
            PollIntervalSeconds = 2;
            StartWithWindows = false;
            TargetDisplayName = "Internal";
        }

        /// <summary>Clamps and cleans values loaded from disk or entered in the UI.</summary>
        public void Normalize()
        {
            if (GamingRefreshRate < 24 || GamingRefreshRate > 1000) GamingRefreshRate = 60;
            if (DesktopRefreshRate < 24 || DesktopRefreshRate > 1000) DesktopRefreshRate = 144;
            PollIntervalSeconds = Math.Max(MinInterval, Math.Min(MaxInterval, PollIntervalSeconds));

            GameProcesses = (GameProcesses ?? new List<string>())
                .Where(n => ProcessScanner.Normalize(n) != null)
                .Select(n => n.Trim().Trim('"'))
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe")
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (!string.Equals(TargetDisplayName, "Primary", StringComparison.OrdinalIgnoreCase))
                TargetDisplayName = "Internal";
            else
                TargetDisplayName = "Primary";
        }

        public HashSet<string> NormalizedGameNames() =>
            new HashSet<string>(GameProcesses.Select(ProcessScanner.Normalize).Where(n => n != null));

        public AppSettings Clone()
        {
            var c = (AppSettings)MemberwiseClone();
            c.GameProcesses = GameProcesses.ToList();
            return c;
        }

        // ------------------------------------------------------------------ persistence

        public static string SettingsDirectory { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HoYoRefreshSwitcher");

        public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    using (var fs = File.OpenRead(SettingsFile))
                    {
                        var s = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(fs);
                        s.Normalize();
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("settings.json could not be read; using defaults. The bad file is kept as settings.invalid.json.", ex);
                try
                {
                    string bad = Path.Combine(SettingsDirectory, "settings.invalid.json");
                    File.Copy(SettingsFile, bad, true);
                }
                catch
                {
                    // ignore
                }
            }

            var defaults = new AppSettings();
            defaults.Normalize();
            defaults.Save();
            return defaults;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                string tmp = SettingsFile + ".tmp";
                using (var fs = File.Create(tmp))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, ownsStream: false, indent: true, indentChars: "  "))
                {
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(writer, this);
                    writer.Flush();
                }

                if (File.Exists(SettingsFile))
                    File.Replace(tmp, SettingsFile, null);
                else
                    File.Move(tmp, SettingsFile);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not save settings.", ex);
            }
        }
    }
}
