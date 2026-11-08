using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static HoYoRefreshSwitcher.Display.NativeMethods;

namespace HoYoRefreshSwitcher.Display
{
    /// <summary>Which display the app controls.</summary>
    public enum DisplayTarget
    {
        /// <summary>The built-in laptop panel (identified via CCD output technology). Default.</summary>
        Internal,

        /// <summary>Whatever display is the Windows primary display (useful for desktops).</summary>
        Primary,
    }

    /// <summary>
    /// Display enumeration and refresh-rate switching.
    ///
    /// Design:
    ///  * Identification (read-only) uses the CCD API, because it is the only API that reports the
    ///    physical connection type (internal eDP panel vs HDMI/DP) and the precise signal timing,
    ///    independent of which GPU renders (NVIDIA Optimus / AMD hybrid).
    ///  * The change itself uses ChangeDisplaySettingsEx on that one GDI device: it changes only the
    ///    mode of that source, Windows validates it (CDS_TEST) before anything is applied, and the
    ///    resolution / colour depth / position are copied from the current mode so only the
    ///    refresh rate changes.
    ///  * Every change is verified by reading the mode back; if verification fails the previous mode
    ///    is restored.
    /// </summary>
    public static class DisplayManager
    {
        private const int VerifyAttempts = 10;
        private const int VerifyDelayMs = 150;

        // ------------------------------------------------------------------ queries

        /// <summary>Returns all displays attached to the desktop, enriched with CCD information when available.</summary>
        public static List<DisplayInfo> GetActiveDisplays(out string ccdError)
        {
            var ccd = QueryCcdTargets(out ccdError);
            var list = new List<DisplayInfo>();

            for (uint i = 0; ; i++)
            {
                var dd = NewDisplayDevice();
                if (!EnumDisplayDevices(null, i, ref dd, 0))
                    break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                    continue;

                var info = new DisplayInfo
                {
                    GdiDeviceName = dd.DeviceName,
                    AdapterName = dd.DeviceString,
                    IsPrimary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                    Connection = "Unknown",
                };

                var cur = NewDevMode();
                if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur))
                {
                    info.Width = cur.dmPelsWidth;
                    info.Height = cur.dmPelsHeight;
                    info.BitsPerPixel = cur.dmBitsPerPel;
                    info.RefreshRate = cur.dmDisplayFrequency;
                    info.SupportedRefreshRates = GetSupportedRates(dd.DeviceName, cur);
                }

                if (ccd.TryGetValue(dd.DeviceName, out var t))
                {
                    info.IsInternal = IsInternalConnection(t.Technology);
                    info.Connection = DescribeTechnology(t.Technology);
                    info.MonitorDevicePath = t.DevicePath;
                    info.SignalRefreshRate = t.SignalHz;
                }

                info.FriendlyName = BuildFriendlyName(info, t);
                list.Add(info);
            }

            return list;
        }

        /// <summary>
        /// Finds the display the app should control. For <see cref="DisplayTarget.Internal"/> there is
        /// deliberately no fallback: if the built-in panel is not active (lid closed, external only),
        /// nothing is touched.
        /// </summary>
        public static DisplayInfo FindTarget(DisplayTarget target, out string error)
        {
            var displays = GetActiveDisplays(out var ccdError);
            return SelectTarget(displays, target, ccdError, out error);
        }

        /// <summary>Same as <see cref="FindTarget"/> but works on an already-enumerated display list.</summary>
        public static DisplayInfo SelectTarget(List<DisplayInfo> displays, DisplayTarget target, string ccdError, out string error)
        {
            error = null;

            if (displays.Count == 0)
            {
                error = "No active displays were reported by Windows.";
                return null;
            }

            if (target == DisplayTarget.Primary)
            {
                var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
                return primary;
            }

            var internalPanel = displays.FirstOrDefault(d => d.IsInternal);
            if (internalPanel != null)
                return internalPanel;

            error = ccdError != null
                ? "Could not identify the built-in display: " + ccdError
                : "The built-in display is not active (lid closed or external monitor only).";
            return null;
        }

        /// <summary>Reads the current integer refresh rate of a GDI device, or 0 if it cannot be read.</summary>
        public static int GetCurrentRefreshRate(string gdiDeviceName)
        {
            var dm = NewDevMode();
            return EnumDisplaySettings(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
        }

        /// <summary>Refresh rate stored in the Windows display registry (what Windows restores after a reboot), or 0.</summary>
        public static int GetSavedRefreshRate(string gdiDeviceName)
        {
            var dm = NewDevMode();
            return EnumDisplaySettings(gdiDeviceName, ENUM_REGISTRY_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
        }

        /// <summary>A short string that changes when displays are added, removed or re-routed.</summary>
        public static string GetTopologySignature(List<DisplayInfo> displays)
        {
            return string.Join("|", displays
                .OrderBy(d => d.GdiDeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(d => $"{d.GdiDeviceName}:{d.MonitorDevicePath}:{d.IsInternal}:{d.Width}x{d.Height}"));
        }

        // ------------------------------------------------------------------ switching

        /// <summary>
        /// Changes only the refresh rate of <paramref name="gdiDeviceName"/>, keeping resolution and colour depth.
        /// </summary>
        /// <param name="persist">
        /// true  = also store the mode in the Windows display registry (survives reboot),
        /// false = temporary change; Windows returns to the stored mode after a reboot. The app uses a
        ///         temporary change for the gaming rate so that a crash can never leave 60 Hz "stuck".
        /// </param>
        public static SwitchResult SetRefreshRate(string gdiDeviceName, int hz, bool persist)
        {
            var r = new SwitchResult { DeviceName = gdiDeviceName, RequestedHz = hz };

            if (string.IsNullOrWhiteSpace(gdiDeviceName))
                return r.Fail("No display device was specified.");
            if (hz <= 1)
                return r.Fail($"{hz} Hz is not a valid refresh rate.");

            // 1. Read the current mode.
            var current = NewDevMode();
            if (!EnumDisplaySettings(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref current))
            {
                int err = Marshal.GetLastWin32Error();
                return r.Fail($"Windows could not read the current mode of {gdiDeviceName} (Win32 error {err}). " +
                              "The display may have been disconnected, or the program is not running on the interactive desktop.");
            }

            r.FromHz = current.dmDisplayFrequency;
            r.ActualHz = current.dmDisplayFrequency;
            r.Step($"Current mode of {gdiDeviceName}: {current.dmPelsWidth}x{current.dmPelsHeight}, {current.dmBitsPerPel}-bit, {current.dmDisplayFrequency} Hz");

            if (current.dmDisplayFrequency == hz)
            {
                r.NoChangeNeeded = true;
                return r.Ok($"Already at {hz} Hz; nothing to do.");
            }

            // 2. Confirm the requested rate exists at the current resolution.
            var rates = GetSupportedRates(gdiDeviceName, current);
            r.Step("Rates available at this resolution: " + (rates.Count == 0 ? "none" : string.Join(", ", rates) + " Hz"));
            if (!rates.Contains(hz))
                return r.Fail($"{hz} Hz is not offered by {gdiDeviceName} at {current.dmPelsWidth}x{current.dmPelsHeight}. Display left unchanged.");

            // 3. Build the target mode from the current mode (only the frequency differs).
            var target = current;
            target.dmDriverExtra = 0;
            target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
            target.dmDisplayFrequency = hz;

            // 4. Dry run: Windows/driver validate the mode without applying it.
            int test = ChangeDisplaySettingsEx(gdiDeviceName, ref target, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            r.Step($"Dry run (CDS_TEST): {DescribeResult(test)}");
            if (test != DISP_CHANGE_SUCCESSFUL)
                return r.Fail($"Windows rejected {hz} Hz in a dry run ({DescribeResult(test)}). Display left unchanged.");

            // 5. Apply.
            uint flags = persist ? CDS_UPDATEREGISTRY : 0;
            int apply = ChangeDisplaySettingsEx(gdiDeviceName, ref target, IntPtr.Zero, flags, IntPtr.Zero);
            r.Step($"Apply ({(persist ? "saved to Windows settings" : "temporary")}): {DescribeResult(apply)}");
            if (apply != DISP_CHANGE_SUCCESSFUL)
            {
                r.ActualHz = GetCurrentRefreshRate(gdiDeviceName);
                return r.Fail($"Windows could not apply {hz} Hz ({DescribeResult(apply)}). Display is at {r.ActualHz} Hz.");
            }

            // 6. Verify by reading the mode back (the driver may take a moment).
            int actual = 0;
            for (int attempt = 0; attempt < VerifyAttempts; attempt++)
            {
                actual = GetCurrentRefreshRate(gdiDeviceName);
                if (actual == hz)
                    break;
                Thread.Sleep(VerifyDelayMs);
            }

            r.ActualHz = actual;
            double signal = ReadSignalRefresh(gdiDeviceName);
            r.Step($"Read-back: GDI mode reports {actual} Hz" + (signal > 0 ? $", display signal timing {signal:0.###} Hz" : ""));

            if (actual == hz)
                return r.Ok($"{gdiDeviceName} changed from {r.FromHz} Hz to {hz} Hz (verified).");

            // 7. Verification failed: put the original mode back.
            var restore = current;
            restore.dmDriverExtra = 0;
            restore.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
            int back = ChangeDisplaySettingsEx(gdiDeviceName, ref restore, IntPtr.Zero, flags, IntPtr.Zero);
            r.ActualHz = GetCurrentRefreshRate(gdiDeviceName);
            r.Step($"Restore previous mode: {DescribeResult(back)}, now {r.ActualHz} Hz");
            return r.Fail($"Display reported {actual} Hz instead of {hz} Hz after the change; restored {r.ActualHz} Hz.");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Distinct refresh rates offered at the same resolution, colour depth and scan type as <paramref name="current"/>.</summary>
        internal static List<int> GetSupportedRates(string device, DEVMODE current)
        {
            var rates = new SortedSet<int>();
            bool interlaced = (current.dmDisplayFlags & DM_INTERLACED) != 0;

            for (int i = 0; ; i++)
            {
                var m = NewDevMode();
                if (!EnumDisplaySettings(device, i, ref m))
                    break;
                if (m.dmPelsWidth != current.dmPelsWidth || m.dmPelsHeight != current.dmPelsHeight)
                    continue;
                if (m.dmBitsPerPel != current.dmBitsPerPel)
                    continue;
                if (((m.dmDisplayFlags & DM_INTERLACED) != 0) != interlaced)
                    continue;
                if (m.dmDisplayFrequency > 1) // 0 and 1 mean "hardware default"
                    rates.Add(m.dmDisplayFrequency);
            }

            return rates.ToList();
        }

        private static double ReadSignalRefresh(string gdiDeviceName)
        {
            var ccd = QueryCcdTargets(out _);
            return ccd.TryGetValue(gdiDeviceName, out var t) ? t.SignalHz : 0;
        }

        private sealed class CcdTarget
        {
            public OutputTechnology Technology;
            public string FriendlyName;
            public string DevicePath;
            public ushort EdidManufacturer;
            public ushort EdidProduct;
            public double SignalHz;
        }

        /// <summary>Maps GDI source name (\\.\DISPLAYn) to the target (monitor) it drives, via QueryDisplayConfig.</summary>
        private static Dictionary<string, CcdTarget> QueryCcdTargets(out string error)
        {
            var result = new Dictionary<string, CcdTarget>(StringComparer.OrdinalIgnoreCase);
            error = null;

            DISPLAYCONFIG_PATH_INFO[] paths = null;
            DISPLAYCONFIG_MODE_INFO[] modes = null;
            uint pathCount = 0, modeCount = 0;
            int rc = ERROR_INSUFFICIENT_BUFFER;

            // The topology can change between the two calls, so retry on ERROR_INSUFFICIENT_BUFFER.
            for (int attempt = 0; attempt < 5 && rc == ERROR_INSUFFICIENT_BUFFER; attempt++)
            {
                rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                if (rc != ERROR_SUCCESS)
                    break;
                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            }

            if (rc != ERROR_SUCCESS)
            {
                error = $"QueryDisplayConfig failed: {new Win32Exception(rc).Message} ({rc})";
                return result;
            }

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                if ((path.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0)
                    continue;

                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_SOURCE_NAME;
                src.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                src.header.adapterId = path.sourceInfo.adapterId;
                src.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref src) != ERROR_SUCCESS || string.IsNullOrEmpty(src.viewGdiDeviceName))
                    continue;

                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_TARGET_NAME;
                tgt.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
                tgt.header.adapterId = path.targetInfo.adapterId;
                tgt.header.id = path.targetInfo.id;
                bool haveTarget = DisplayConfigGetDeviceInfo(ref tgt) == ERROR_SUCCESS;

                double hz = 0;
                uint mi = path.targetInfo.modeInfoIdx;
                if (mi != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && mi < modeCount &&
                    modes[mi].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.TARGET)
                {
                    hz = Rational(modes[mi].targetVideoSignalInfo.vSyncFreq);
                }
                if (hz <= 0)
                    hz = Rational(path.targetInfo.refreshRate);

                var entry = new CcdTarget
                {
                    Technology = haveTarget ? tgt.outputTechnology : path.targetInfo.outputTechnology,
                    FriendlyName = haveTarget ? tgt.monitorFriendlyDeviceName : null,
                    DevicePath = haveTarget ? tgt.monitorDevicePath : null,
                    EdidManufacturer = haveTarget ? tgt.edidManufactureId : (ushort)0,
                    EdidProduct = haveTarget ? tgt.edidProductCodeId : (ushort)0,
                    SignalHz = hz,
                };

                // In clone (duplicate) mode one source drives several targets; prefer the internal one.
                if (!result.TryGetValue(src.viewGdiDeviceName, out var existing) ||
                    (!IsInternalConnection(existing.Technology) && IsInternalConnection(entry.Technology)))
                {
                    result[src.viewGdiDeviceName] = entry;
                }
            }

            return result;
        }

        private static double Rational(DISPLAYCONFIG_RATIONAL r) =>
            r.Denominator == 0 ? 0 : (double)r.Numerator / r.Denominator;

        internal static bool IsInternalConnection(OutputTechnology t) =>
            t == OutputTechnology.Internal ||
            t == OutputTechnology.DisplayPortEmbedded ||
            t == OutputTechnology.UdiEmbedded ||
            t == OutputTechnology.LVDS;

        private static string DescribeTechnology(OutputTechnology t)
        {
            switch (t)
            {
                case OutputTechnology.Internal: return "Internal";
                case OutputTechnology.DisplayPortEmbedded: return "Embedded DisplayPort";
                case OutputTechnology.UdiEmbedded: return "Embedded UDI";
                case OutputTechnology.LVDS: return "LVDS (internal)";
                case OutputTechnology.HDMI: return "HDMI";
                case OutputTechnology.DisplayPortExternal: return "DisplayPort";
                case OutputTechnology.DisplayPortUsbTunnel: return "USB-C DisplayPort";
                case OutputTechnology.DVI: return "DVI";
                case OutputTechnology.HD15: return "VGA";
                case OutputTechnology.Miracast: return "Miracast";
                case OutputTechnology.IndirectWired: return "Indirect (wired)";
                case OutputTechnology.IndirectVirtual: return "Indirect (virtual)";
                default: return t.ToString();
            }
        }

        private static string BuildFriendlyName(DisplayInfo info, CcdTarget t)
        {
            if (t != null && !string.IsNullOrWhiteSpace(t.FriendlyName))
                return t.FriendlyName.Trim();

            string edid = t != null && t.EdidManufacturer != 0 ? $"{DecodePnpId(t.EdidManufacturer)} {t.EdidProduct:X4}" : null;
            string kind = info.IsInternal ? "Built-in display" : "Display";
            return edid != null ? $"{kind} ({edid})" : $"{kind} {info.GdiDeviceName.Replace(@"\\.\", "")}";
        }

        /// <summary>Decodes the 3-letter EDID manufacturer ID (CCD returns it byte-swapped).</summary>
        private static string DecodePnpId(ushort raw)
        {
            int v = ((raw & 0xFF) << 8) | (raw >> 8);
            char a = (char)('@' + ((v >> 10) & 0x1F));
            char b = (char)('@' + ((v >> 5) & 0x1F));
            char c = (char)('@' + (v & 0x1F));
            return new string(new[] { a, b, c });
        }

        public static string DescribeResult(int code)
        {
            switch (code)
            {
                case DISP_CHANGE_SUCCESSFUL: return "success";
                case DISP_CHANGE_RESTART: return "restart required";
                case DISP_CHANGE_FAILED: return "driver failed the mode change";
                case DISP_CHANGE_BADMODE: return "mode not supported";
                case DISP_CHANGE_NOTUPDATED: return "could not write settings to the registry";
                case DISP_CHANGE_BADFLAGS: return "invalid flags";
                case DISP_CHANGE_BADPARAM: return "invalid parameter";
                case DISP_CHANGE_BADDUALVIEW: return "DualView-capable system";
                default: return "error " + code;
            }
        }

        internal static DEVMODE NewDevMode()
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            dm.dmDriverExtra = 0;
            return dm;
        }

        private static DISPLAY_DEVICE NewDisplayDevice()
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            return dd;
        }

        /// <summary>Verifies the managed struct sizes match the Windows SDK. Returns null when all match.</summary>
        public static string SelfCheck()
        {
            var problems = new List<string>();
            void Check(Type t, int expected)
            {
                int actual = Marshal.SizeOf(t);
                if (actual != expected)
                    problems.Add($"{t.Name}: {actual} bytes (expected {expected})");
            }

            Check(typeof(DEVMODE), 220);
            Check(typeof(DISPLAY_DEVICE), 840);
            Check(typeof(DISPLAYCONFIG_PATH_INFO), 72);
            Check(typeof(DISPLAYCONFIG_MODE_INFO), 64);
            Check(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME), 84);
            Check(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME), 420);
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
    }
}
