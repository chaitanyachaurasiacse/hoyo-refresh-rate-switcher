using System.Collections.Generic;
using System.Linq;

namespace HoYoRefreshSwitcher.Display
{
    /// <summary>Snapshot of one active display (one GDI source) at the moment it was queried.</summary>
    public sealed class DisplayInfo
    {
        /// <summary>GDI device name, e.g. \\.\DISPLAY1. This is what ChangeDisplaySettingsEx targets.</summary>
        public string GdiDeviceName { get; set; }

        /// <summary>Human-readable monitor name (EDID friendly name, or a constructed fallback).</summary>
        public string FriendlyName { get; set; }

        /// <summary>Graphics adapter that scans out to this display (on Optimus laptops: usually the iGPU).</summary>
        public string AdapterName { get; set; }

        public bool IsPrimary { get; set; }

        /// <summary>True when CCD reports an internal connection (laptop panel: eDP / LVDS / internal).</summary>
        public bool IsInternal { get; set; }

        /// <summary>CCD output technology as text (e.g. "Internal", "HDMI"), or "Unknown" if CCD failed.</summary>
        public string Connection { get; set; }

        public string MonitorDevicePath { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }
        public int BitsPerPixel { get; set; }

        /// <summary>Integer refresh rate of the current GDI mode (what Windows Settings shows).</summary>
        public int RefreshRate { get; set; }

        /// <summary>Exact vertical sync frequency of the active signal from CCD (e.g. 143.98), or 0 if unknown.</summary>
        public double SignalRefreshRate { get; set; }

        /// <summary>Refresh rates Windows offers at the current resolution and colour depth.</summary>
        public List<int> SupportedRefreshRates { get; set; } = new List<int>();

        public bool Supports(int hz) => SupportedRefreshRates.Contains(hz);

        public string SupportedRatesText =>
            SupportedRefreshRates.Count == 0 ? "none reported" : string.Join(", ", SupportedRefreshRates.Select(r => r + " Hz"));

        public override string ToString() =>
            $"{FriendlyName} [{GdiDeviceName}, {AdapterName}, {Connection}] {Width}x{Height} {BitsPerPixel}-bit @ {RefreshRate} Hz";
    }

    /// <summary>Outcome of a refresh-rate change request, including a step-by-step trace.</summary>
    public sealed class SwitchResult
    {
        public string DeviceName { get; set; }
        public int RequestedHz { get; set; }
        public int FromHz { get; set; }
        public int ActualHz { get; set; }
        public bool Success { get; set; }
        public bool NoChangeNeeded { get; set; }
        public string Message { get; set; }
        public List<string> Trace { get; } = new List<string>();

        internal SwitchResult Step(string line)
        {
            Trace.Add(line);
            return this;
        }

        internal SwitchResult Fail(string message)
        {
            Success = false;
            Message = message;
            Trace.Add("FAILED: " + message);
            return this;
        }

        internal SwitchResult Ok(string message)
        {
            Success = true;
            Message = message;
            Trace.Add("OK: " + message);
            return this;
        }
    }
}
