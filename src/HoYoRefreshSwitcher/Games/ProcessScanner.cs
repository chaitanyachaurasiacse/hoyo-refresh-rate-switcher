using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace HoYoRefreshSwitcher.Games
{
    /// <summary>
    /// Finds running processes by executable name.
    ///
    /// Reads the system process list in one call (NtQuerySystemInformation, the same call .NET's
    /// Process class uses), falling back to a Toolhelp32 snapshot and then to Process.GetProcesses.
    /// None of these need admin rights and none open a handle to any process, so scanning cannot
    /// interfere with games or their anti-cheat. The buffer is reused, and a string is only built
    /// for processes whose name length matches a configured game, so a scan allocates almost nothing.
    /// </summary>
    public static class ProcessScanner
    {
        private static readonly object BufferGate = new object();
        private static IntPtr _buffer = IntPtr.Zero;
        private static int _bufferSize;

        /// <summary>Normalises "GenshinImpact.exe", "genshinimpact" etc. to "genshinimpact".</summary>
        public static string Normalize(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return null;
            string n = processName.Trim().Trim('"');
            n = Path.GetFileName(n);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            return n.Length == 0 ? null : n.ToLowerInvariant();
        }

        /// <summary>
        /// Returns the executable names (as reported by Windows, e.g. "StarRail.exe") of running
        /// processes whose name matches one of <paramref name="normalizedNames"/>, sorted and distinct.
        /// </summary>
        public static List<string> FindRunning(ICollection<string> normalizedNames)
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (normalizedNames == null || normalizedNames.Count == 0)
                return found.ToList();

            if (!TryNtScan(normalizedNames, found) && !TryToolhelpScan(normalizedNames, found))
                FallbackScan(normalizedNames, found);

            return found.ToList();
        }

        /// <summary>Runs all three scan methods, checks they agree, and reports CPU cost (for the diagnostic tool).</summary>
        public static string SelfTest(ICollection<string> normalizedNames, int iterations = 50)
        {
            var sb = new System.Text.StringBuilder();
            var results = new List<string>();
            void Measure(string label, Func<SortedSet<string>, bool> scan)
            {
                var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                bool ok = scan(set);
                var proc = Process.GetCurrentProcess();
                var cpu0 = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                    scan(new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                sw.Stop();
                proc.Refresh();
                double cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / iterations;
                string found = string.Join(", ", set);
                results.Add(found);
                sb.AppendLine($"  {label,-24} {(ok ? "ok " : "N/A")}  {sw.Elapsed.TotalMilliseconds / iterations,6:0.00} ms/scan wall, {cpu,6:0.00} ms CPU   found: {(found.Length == 0 ? "none" : found)}");
            }

            Measure("NtQuerySystemInformation", s => TryNtScan(normalizedNames, s));
            Measure("Toolhelp32 snapshot", s => TryToolhelpScan(normalizedNames, s));
            Measure("Process.GetProcesses", s => { FallbackScan(normalizedNames, s); return true; });
            sb.AppendLine(results.Distinct().Count() == 1 ? "  All methods agree." : "  WARNING: methods disagree.");
            return sb.ToString();
        }

        private static bool TryNtScan(ICollection<string> names, SortedSet<string> found)
        {
            // Image names are compared as "<name>.exe"; only those lengths can match.
            var lengths = new HashSet<int>(names.Select(n => n.Length + 4));

            lock (BufferGate)
            {
                try
                {
                    if (_buffer == IntPtr.Zero)
                    {
                        _bufferSize = 512 * 1024;
                        _buffer = Marshal.AllocHGlobal(_bufferSize);
                    }

                    int status;
                    for (int attempt = 0; ; attempt++)
                    {
                        status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out int needed);
                        if (status != STATUS_INFO_LENGTH_MISMATCH || attempt >= 4)
                            break;
                        Marshal.FreeHGlobal(_buffer);
                        _bufferSize = Math.Max(_bufferSize * 2, needed + 64 * 1024);
                        _buffer = Marshal.AllocHGlobal(_bufferSize);
                    }

                    if (status != 0)
                        return false;

                    // SYSTEM_PROCESS_INFORMATION: NextEntryOffset @0, ImageName (UNICODE_STRING) @0x38.
                    int nameOffset = 0x38;
                    int bufferPtrOffset = IntPtr.Size == 8 ? 0x40 : 0x3C;
                    long offset = 0;
                    while (true)
                    {
                        IntPtr entry = new IntPtr(_buffer.ToInt64() + offset);
                        int next = Marshal.ReadInt32(entry, 0);
                        int byteLen = Marshal.ReadInt16(entry, nameOffset) & 0xFFFF;
                        IntPtr namePtr = Marshal.ReadIntPtr(entry, bufferPtrOffset);

                        if (byteLen > 0 && namePtr != IntPtr.Zero && lengths.Contains(byteLen / 2))
                        {
                            string exe = Marshal.PtrToStringUni(namePtr, byteLen / 2);
                            string n = Normalize(exe);
                            if (n != null && names.Contains(n))
                                found.Add(exe);
                        }

                        if (next == 0)
                            break;
                        offset += next;
                        if (offset >= _bufferSize)
                            return false;
                    }

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static bool TryToolhelpScan(ICollection<string> names, SortedSet<string> found)
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE)
                return false;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                if (!Process32FirstW(snap, ref entry))
                    return false;
                do
                {
                    string n = Normalize(entry.szExeFile);
                    if (n != null && names.Contains(n))
                        found.Add(entry.szExeFile);
                }
                while (Process32NextW(snap, ref entry));
                return true;
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        private static void FallbackScan(ICollection<string> names, SortedSet<string> found)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string n = Normalize(p.ProcessName);
                    if (n != null && names.Contains(n))
                        found.Add(p.ProcessName + ".exe");
                }
            }
        }

        private const int SystemProcessInformation = 5;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
