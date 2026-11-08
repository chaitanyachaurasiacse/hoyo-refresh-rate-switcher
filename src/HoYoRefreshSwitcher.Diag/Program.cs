using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HoYoRefreshSwitcher.Display;
using HoYoRefreshSwitcher.Games;

namespace HoYoRefreshSwitcher.Diag
{
    /// <summary>
    /// Command-line diagnostic tool. It uses exactly the same display and process code as the tray app.
    /// </summary>
    internal static class Program
    {
        private static readonly string[] DefaultGames = { "GenshinImpact.exe", "StarRail.exe", "ZenlessZoneZero.exe" };

        private static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
            try
            {
                switch (cmd)
                {
                    case "info": return Info();
                    case "status": return Status(args);
                    case "set": return Set(args);
                    case "test": return Test(args);
                    case "watch": return Watch(args);
                    case "help":
                    case "-h":
                    case "--help":
                    case "/?":
                        Usage();
                        return 0;
                    default:
                        Console.Error.WriteLine("Unknown command: " + args[0]);
                        Usage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Unexpected error: " + ex);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.WriteLine(@"HoYoRefreshDiag - display / game detection diagnostics for HoYo Refresh Rate Switcher

  HoYoRefreshDiag info                      Full report: displays, modes, target display, games (default)
  HoYoRefreshDiag status [--primary]        One line: target display and current refresh rate
  HoYoRefreshDiag set <hz> [--temp] [--primary] [--display \\.\DISPLAYn]
                                            Change refresh rate (verified). --temp = not saved to Windows
  HoYoRefreshDiag test [gamingHz] [desktopHz] [--primary]
                                            Round-trip switching test + failure-handling tests
  HoYoRefreshDiag watch [exe names...]      Print game start/stop events every 2 s (Ctrl+C to stop)

Exit code 0 = success, 1 = failure, 2 = bad usage.");
        }

        private static DisplayTarget TargetFrom(string[] args) =>
            args.Any(a => a.Equals("--primary", StringComparison.OrdinalIgnoreCase)) ? DisplayTarget.Primary : DisplayTarget.Internal;

        // ------------------------------------------------------------------ info

        private static int Info()
        {
            Console.WriteLine("=== HoYo Refresh Rate Switcher - diagnostics ===");
            Console.WriteLine($"Time:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"OS:        {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit), .NET CLR {Environment.Version}");
            Console.WriteLine($"Session:   {System.Diagnostics.Process.GetCurrentProcess().SessionId} (0 would mean a service/non-interactive session)");
            Console.WriteLine($"Struct layout check: {DisplayManager.SelfCheck() ?? "OK"}");
            Console.WriteLine();

            var displays = DisplayManager.GetActiveDisplays(out var ccdError);
            Console.WriteLine($"Active displays: {displays.Count}" + (ccdError != null ? $"   (CCD warning: {ccdError})" : ""));
            foreach (var d in displays)
            {
                Console.WriteLine($"  {d.GdiDeviceName}");
                Console.WriteLine($"    Name:        {d.FriendlyName}");
                Console.WriteLine($"    Adapter:     {d.AdapterName}");
                Console.WriteLine($"    Connection:  {d.Connection}{(d.IsInternal ? "  <-- built-in panel" : "")}{(d.IsPrimary ? "  [primary]" : "")}");
                Console.WriteLine($"    Mode:        {d.Width}x{d.Height}, {d.BitsPerPixel}-bit, {d.RefreshRate} Hz" +
                                  (d.SignalRefreshRate > 0 ? $" (signal {d.SignalRefreshRate:0.###} Hz)" : ""));
                Console.WriteLine($"    Rates here:  {d.SupportedRatesText}");
                if (!string.IsNullOrEmpty(d.MonitorDevicePath))
                    Console.WriteLine($"    Device path: {d.MonitorDevicePath}");
            }
            Console.WriteLine();

            var target = DisplayManager.FindTarget(DisplayTarget.Internal, out var err);
            if (target == null)
            {
                Console.WriteLine("Target display (Internal mode): NOT FOUND - " + err);
            }
            else
            {
                Console.WriteLine($"Target display (Internal mode): {target.GdiDeviceName} - {target.FriendlyName}");
                Console.WriteLine($"  60 Hz supported:  {(target.Supports(60) ? "yes" : "NO")}");
                Console.WriteLine($"  144 Hz supported: {(target.Supports(144) ? "yes" : "NO")}");
            }
            Console.WriteLine();

            var names = new HashSet<string>(DefaultGames.Select(ProcessScanner.Normalize));
            var running = ProcessScanner.FindRunning(names);
            Console.WriteLine("Supported games running: " + (running.Count == 0 ? "none" : string.Join(", ", running)));

            var launcherNames = new HashSet<string>(new[] { "HYP", "HYPHelper", "HYPWorker", "launcher", "YuanShen" }.Select(ProcessScanner.Normalize));
            var launchers = ProcessScanner.FindRunning(launcherNames);
            Console.WriteLine("Launcher/other HoYo processes (ignored by the app): " + (launchers.Count == 0 ? "none" : string.Join(", ", launchers)));
            Console.WriteLine();
            Console.WriteLine($"Process scan methods ({System.Diagnostics.Process.GetProcesses().Length} processes running):");
            Console.Write(ProcessScanner.SelfTest(names));
            return 0;
        }

        // ------------------------------------------------------------------ status / set

        private static DisplayInfo ResolveDisplay(string[] args)
        {
            int idx = Array.FindIndex(args, a => a.Equals("--display", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < args.Length)
            {
                string name = args[idx + 1];
                var match = DisplayManager.GetActiveDisplays(out _).FirstOrDefault(d => d.GdiDeviceName.Equals(name, StringComparison.OrdinalIgnoreCase));
                return match ?? new DisplayInfo { GdiDeviceName = name, FriendlyName = "(not an active display)" };
            }

            var t = DisplayManager.FindTarget(TargetFrom(args), out var err);
            if (t == null)
                Console.Error.WriteLine("No target display: " + err);
            return t;
        }

        private static int Status(string[] args)
        {
            var t = ResolveDisplay(args);
            if (t == null)
                return 1;
            Console.WriteLine($"{t.GdiDeviceName}\t{t.RefreshRate} Hz\tsaved {DisplayManager.GetSavedRefreshRate(t.GdiDeviceName)} Hz\t{t.FriendlyName}");
            return 0;
        }

        private static int Set(string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[1], out int hz))
            {
                Usage();
                return 2;
            }
            bool temp = args.Any(a => a.Equals("--temp", StringComparison.OrdinalIgnoreCase));
            var t = ResolveDisplay(args);
            if (t == null)
                return 1;

            var r = DisplayManager.SetRefreshRate(t.GdiDeviceName, hz, persist: !temp);
            foreach (var line in r.Trace)
                Console.WriteLine("  " + line);
            Console.WriteLine(r.Success ? "RESULT: OK" : "RESULT: FAILED");
            return r.Success ? 0 : 1;
        }

        // ------------------------------------------------------------------ test

        private static int Test(string[] args)
        {
            var nums = args.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).ToList();
            int gaming = nums.Count > 0 ? nums[0] : 60;
            int desktop = nums.Count > 1 ? nums[1] : 144;

            var t = DisplayManager.FindTarget(TargetFrom(args), out var err);
            if (t == null)
            {
                Console.WriteLine("FAIL  no target display: " + err);
                return 1;
            }

            int original = t.RefreshRate;
            int pass = 0, fail = 0;
            void Report(bool ok, string name, SwitchResult r = null)
            {
                if (ok) pass++; else fail++;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
                if (r != null)
                    foreach (var line in r.Trace)
                        Console.WriteLine("        " + line);
            }

            Console.WriteLine($"Target: {t}");
            Console.WriteLine($"Testing gaming={gaming} Hz, desktop={desktop} Hz, original={original} Hz");
            Console.WriteLine();

            Report(DisplayManager.SelfCheck() == null, "Native struct layouts match Windows SDK");
            Report(t.Supports(gaming), $"{gaming} Hz is offered by the display ({t.SupportedRatesText})");
            Report(t.Supports(desktop), $"{desktop} Hz is offered by the display");

            if (t.Supports(gaming) && t.Supports(desktop))
            {
                var r0 = DisplayManager.SetRefreshRate(t.GdiDeviceName, desktop, persist: true);
                Report(r0.Success && DisplayManager.GetCurrentRefreshRate(t.GdiDeviceName) == desktop, $"Start from {desktop} Hz (saved) and verify", r0);
                Thread.Sleep(1500);

                var r1 = DisplayManager.SetRefreshRate(t.GdiDeviceName, gaming, persist: false);
                Report(r1.Success && DisplayManager.GetCurrentRefreshRate(t.GdiDeviceName) == gaming, $"Switch to {gaming} Hz (temporary) and verify", r1);
                Thread.Sleep(1500);

                var r2 = DisplayManager.SetRefreshRate(t.GdiDeviceName, gaming, persist: false);
                Report(r2.Success && r2.NoChangeNeeded, $"Second request for {gaming} Hz is a no-op (no repeated mode change)", r2);

                var r3 = DisplayManager.SetRefreshRate(t.GdiDeviceName, desktop, persist: true);
                Report(r3.Success && DisplayManager.GetCurrentRefreshRate(t.GdiDeviceName) == desktop, $"Switch to {desktop} Hz (saved) and verify", r3);
                Thread.Sleep(1500);
            }

            // Failure handling: an unsupported rate must be refused and must not touch the display.
            int bogus = Enumerable.Range(61, 400).First(x => !t.Supports(x) && x != desktop);
            int before = DisplayManager.GetCurrentRefreshRate(t.GdiDeviceName);
            var r4 = DisplayManager.SetRefreshRate(t.GdiDeviceName, bogus, persist: false);
            Report(!r4.Success && DisplayManager.GetCurrentRefreshRate(t.GdiDeviceName) == before, $"Unsupported {bogus} Hz is refused and display stays at {before} Hz", r4);

            var r5 = DisplayManager.SetRefreshRate(@"\\.\DISPLAY99", desktop, persist: false);
            Report(!r5.Success, "Non-existent display device fails gracefully", r5);

            var r6 = DisplayManager.SetRefreshRate(t.GdiDeviceName, 0, persist: false);
            Report(!r6.Success, "Invalid rate 0 Hz is refused", r6);

            // Leave the display as we found it.
            int now = DisplayManager.GetCurrentRefreshRate(t.GdiDeviceName);
            if (now != original && t.Supports(original))
            {
                var back = DisplayManager.SetRefreshRate(t.GdiDeviceName, original, persist: true);
                Console.WriteLine($"Restored original {original} Hz: {(back.Success ? "OK" : back.Message)}");
            }

            Console.WriteLine();
            Console.WriteLine($"{pass} passed, {fail} failed. Display now at {DisplayManager.GetCurrentRefreshRate(t.GdiDeviceName)} Hz.");
            return fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ watch

        private static int Watch(string[] args)
        {
            var list = args.Skip(1).ToList();
            if (list.Count == 0)
                list = DefaultGames.ToList();
            var names = new HashSet<string>(list.Select(ProcessScanner.Normalize).Where(n => n != null));
            Console.WriteLine("Watching for: " + string.Join(", ", list) + "   (Ctrl+C to stop)");

            var previous = new List<string>();
            bool first = true;
            while (true)
            {
                var running = ProcessScanner.FindRunning(names);
                if (first || !running.SequenceEqual(previous))
                {
                    foreach (var s in running.Except(previous)) Console.WriteLine($"{DateTime.Now:HH:mm:ss}  started  {s}");
                    foreach (var s in previous.Except(running)) Console.WriteLine($"{DateTime.Now:HH:mm:ss}  stopped  {s}");
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  => {(running.Count > 0 ? "GAME RUNNING (gaming rate)" : "no game (desktop rate)")}");
                    previous = running;
                    first = false;
                }
                Thread.Sleep(2000);
            }
        }
    }
}
