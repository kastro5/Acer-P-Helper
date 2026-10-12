using System.Globalization;
using System.IO.Compression;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PredatorControlApp
{
    internal sealed class FanLogRow
    {
        public DateTime Time;
        public long Ms;
        public string Type = "";
        public string Phase = "";
        public string? FanMode;
        public int? Curve, Ac;
        public string? Power;
        public int? CpuTemp, GpuTemp, CpuLoad;
        public int? CpuWant, CpuHeld, CpuBypass;
        public int? GpuWant, GpuHeld, GpuBypass;
        public int? CpuWritten, GpuWritten;
        public int? CpuRpm, GpuRpm;
        public string Note = "";
    }

    // CSV log of fan-control decisions, for diagnosing fan behaviour on machines we can't access.
    // tick  = one telemetry cycle (every 2 s): live temps, what the curve wanted, what the filter held
    // probe = extra sensor read during a load test (every 0.5 s)
    // write = any fan speed/behaviour command sent to the firmware, with the calling method
    // event = mode/settings/power changes and test phase markers
    [SupportedOSPlatform("windows")]
    internal static class FanLog
    {
        private const string Header =
            "time,type,phase,fan_mode,curve,ac,power,cpu_temp,gpu_temp,cpu_load," +
            "cpu_want,cpu_held,cpu_bypass,gpu_want,gpu_held,gpu_bypass," +
            "cpu_written,gpu_written,cpu_rpm,gpu_rpm,note";

        private const long MaxSessionBytes = 20L * 1024 * 1024;
        private const int KeepDays = 14;

        private static readonly object _lock = new();
        private static StreamWriter? _session;
        private static string? _sessionPath;
        private static StreamWriter? _test;
        private static int? _pendingCpuWrite, _pendingGpuWrite;

        public static bool Enabled { get; private set; }
        public static string Phase { get; set; } = "";

        // Raised after every row, on the thread that wrote it.
        public static event Action<FanLogRow>? Recorded;

        public static string Folder
        {
            get
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                return Path.Combine(dir, "PredatorControl", "logs");
            }
        }

        public static void SetEnabled(bool on)
        {
            lock (_lock)
            {
                Enabled = on;
                if (!on)
                {
                    _session?.Dispose();
                    _session = null;
                    _sessionPath = null;
                }
            }
            Event(on ? "logging enabled" : "logging disabled");

            // The WMI queries take up to a second, so keep them off the UI thread.
            if (on) Task.Run(() => Event("system: " + SystemInfoText().Replace(Environment.NewLine, " | ")));
        }

        public static void Event(string note) => Write(new FanLogRow { Type = "event", Note = note });

        public static void FanWrite(int? cpu, int? gpu, bool ok, string caller, string what = "speed")
        {
            lock (_lock)
            {
                if (cpu.HasValue) _pendingCpuWrite = cpu;
                if (gpu.HasValue) _pendingGpuWrite = gpu;
            }
            Write(new FanLogRow
            {
                Type = "write",
                CpuWritten = cpu,
                GpuWritten = gpu,
                Note = $"{what} {(ok ? "ok" : "FAILED")} from {caller}"
            });
        }

        public static void Tick(FanLogRow row)
        {
            row.Type = "tick";
            lock (_lock)
            {
                row.CpuWritten = _pendingCpuWrite;
                row.GpuWritten = _pendingGpuWrite;
                _pendingCpuWrite = _pendingGpuWrite = null;
            }
            Write(row);
        }

        public static void Probe(int cpuTemp, int gpuTemp, int cpuRpm, int gpuRpm) =>
            Write(new FanLogRow { Type = "probe", CpuTemp = cpuTemp, GpuTemp = gpuTemp, CpuRpm = cpuRpm, GpuRpm = gpuRpm });

        public static string StartTestFile()
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"fantest-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            lock (_lock)
            {
                _test?.Dispose();
                _test = OpenWriter(path);
                _test.WriteLine(Header);
            }
            Event("system: " + SystemInfoText().Replace(Environment.NewLine, " | "));
            return path;
        }

        public static void EndTestFile()
        {
            lock (_lock)
            {
                _test?.Dispose();
                _test = null;
            }
        }

        private static void Write(FanLogRow r)
        {
            r.Time = DateTime.Now;
            r.Ms = Environment.TickCount64;
            r.Phase = Phase;

            try
            {
                lock (_lock)
                {
                    if (Enabled || _test != null)
                    {
                        string line = Format(r);
                        if (Enabled)
                        {
                            EnsureSession();
                            _session?.WriteLine(line);
                        }
                        _test?.WriteLine(line);
                    }
                }
            }
            catch { }

            try { Recorded?.Invoke(r); }
            catch { }
        }

        private static void EnsureSession()
        {
            if (_session != null && _session.BaseStream.Length < MaxSessionBytes) return;

            _session?.Dispose();
            Directory.CreateDirectory(Folder);
            DeleteOldLogs();
            _sessionPath = Path.Combine(Folder, $"fanlog-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _session = OpenWriter(_sessionPath);
            _session.WriteLine(Header);
            if (_systemInfo != null)
                _session.WriteLine(Format(new FanLogRow
                {
                    Time = DateTime.Now,
                    Type = "event",
                    Note = "system: " + _systemInfo.Replace(Environment.NewLine, " | ")
                }));
        }

        private static StreamWriter OpenWriter(string path)
        {
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }

        private static void DeleteOldLogs()
        {
            try
            {
                foreach (var f in new DirectoryInfo(Folder).GetFiles("*.csv"))
                    if (f.LastWriteTime < DateTime.Now.AddDays(-KeepDays)) f.Delete();
            }
            catch { }
        }

        internal static string Format(FanLogRow r)
        {
            static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
            static string Q(string? s) =>
                string.IsNullOrEmpty(s) ? "" :
                s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

            return string.Join(",",
                r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                r.Type, Q(r.Phase), Q(r.FanMode), N(r.Curve), N(r.Ac), Q(r.Power),
                N(r.CpuTemp), N(r.GpuTemp), N(r.CpuLoad),
                N(r.CpuWant), N(r.CpuHeld), N(r.CpuBypass),
                N(r.GpuWant), N(r.GpuHeld), N(r.GpuBypass),
                N(r.CpuWritten), N(r.GpuWritten), N(r.CpuRpm), N(r.GpuRpm),
                Q(r.Note));
        }

        #region Report

        // Zips the summary, the test log (if any), the most recent session logs and the crash log.
        public static string CreateReport(string summary, string? testCsvPath)
        {
            Directory.CreateDirectory(Folder);
            string zipPath = Path.Combine(Folder, $"PredatorFanReport-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            var summaryEntry = zip.CreateEntry("summary.txt");
            using (var w = new StreamWriter(summaryEntry.Open(), new UTF8Encoding(false)))
                w.Write(summary);

            if (testCsvPath != null) AddFile(zip, testCsvPath);

            var sessions = new DirectoryInfo(Folder).GetFiles("fanlog-*.csv")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(3);
            foreach (var f in sessions) AddFile(zip, f.FullName);

            string crash = Path.Combine(Path.GetDirectoryName(Folder)!, "crash.log");
            if (File.Exists(crash)) AddFile(zip, crash);

            return zipPath;
        }

        private static void AddFile(ZipArchive zip, string path)
        {
            try
            {
                using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal).Open();
                src.CopyTo(dst);
            }
            catch { }
        }

        #endregion

        #region System info

        private static volatile string? _systemInfo;

        public static string SystemInfoText()
        {
            if (_systemInfo != null) return _systemInfo;

            var sb = new StringBuilder();
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            sb.AppendLine($"App: Predator Control {version}");
            sb.AppendLine($"Laptop: {Wmi("Win32_ComputerSystem", "Manufacturer")} {Wmi("Win32_ComputerSystem", "Model")}");
            sb.AppendLine($"BIOS: {Wmi("Win32_BIOS", "SMBIOSBIOSVersion")}");
            sb.AppendLine($"CPU: {Wmi("Win32_Processor", "Name")} ({Environment.ProcessorCount} threads)");
            sb.AppendLine($"GPU: {Wmi("Win32_VideoController", "Name")}");
            sb.Append($"Windows: {Wmi("Win32_OperatingSystem", "Caption")} build {Environment.OSVersion.Version.Build}");
            return _systemInfo = sb.ToString();
        }

        private static string Wmi(string cls, string prop)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
                using var results = searcher.Get();
                var values = results.Cast<ManagementBaseObject>()
                    .Select(o => o[prop]?.ToString()?.Trim())
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Distinct();
                string joined = string.Join(" + ", values);
                return joined.Length > 0 ? joined : "?";
            }
            catch { return "?"; }
        }

        #endregion
    }

    // Overall CPU utilisation between successive calls, from GetSystemTimes.
    internal sealed class CpuUsage
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private long _idle, _total;

        public int? Sample()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
            long total = kernel + user; // kernel time includes idle time
            long dIdle = idle - _idle, dTotal = total - _total;
            bool first = _total == 0;
            _idle = idle;
            _total = total;
            if (first || dTotal <= 0) return null;
            return (int)Math.Round(100.0 * (dTotal - dIdle) / dTotal);
        }
    }
}
