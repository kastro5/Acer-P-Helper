using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace PredatorControlApp
{
    internal interface IFanTestHost
    {
        bool CurveActive { get; }
        (int Up, int Down, int CpuBypass, int GpuBypass) FilterSettings { get; }
        string DescribeFanSettings();
        (int CpuTemp, int GpuTemp, int CpuRpm, int GpuRpm) ReadSensors();
        void BeginFixedSpeedOverride(int cpuPercent, int gpuPercent);
        void EndFixedSpeedOverride();
    }

    internal enum TestKind { Idle, Burst, Light, Sustained, Firmware }

    internal sealed record TestStep(string Name, TestKind Kind, int Threads, int PreSec, int LoadSec, int RestSec, string Purpose)
    {
        public int TotalSec => PreSec + LoadSec + RestSec;
    }

    internal sealed class StepTiming
    {
        public TestStep Step = null!;
        public long StartMs, LoadStartMs, LoadEndMs, EndMs;
    }

    internal sealed class StepResult
    {
        public int CpuTempMax, GpuTempMax, CpuLoadAvg;
        public double SecondsAboveCpuBypass;
        public int CpuHeldStart = -1, CpuHeldMax = -1, CpuHeldEnd = -1;
        public double? RaiseAfterLoadStartSec, DropAfterLoadEndSec;
        public int BypassTicks, GpuBypassTicks, GpuHeldChanges;
        public int CurveWrites, OtherWrites;
        public int CpuRpmMin, CpuRpmMax, GpuRpmMax;
        public int SettledCpuRpm, PeakCpuRpm, EndCpuRpm, SettledGpuRpm, PeakGpuRpm;
        public bool FirmwareOverride, BypassRaised, UnexpectedRaise, IdleRaise;
        public string Verdict = "";
    }

    // Runs scripted CPU loads while logging every fan decision, then writes a summary report.
    [SupportedOSPlatform("windows")]
    internal sealed class FanTestRunner : IDisposable
    {
        public const int FixedTestSpeed = 40;
        private const int ProbeIntervalMs = 500;

        private readonly IFanTestHost _host;
        private readonly double _timeScale;
        private readonly List<TestStep> _steps;
        private readonly List<FanLogRow> _rows = new();
        private readonly List<StepTiming> _timings = new();
        private readonly LoadGenerator _load = new();
        private readonly System.Windows.Forms.Timer _probeTimer = new() { Interval = ProbeIntervalMs };
        private CancellationTokenSource? _cts;
        private long _testStartMs, _stepStartMs;
        private int _stepIndex;

        public event Action<string>? StatusChanged;

        public bool Running => _cts != null;
        public int TotalSec => (int)Math.Ceiling(_steps.Sum(s => s.TotalSec) * _timeScale);

        // timeScale < 1 shortens every step; only the CI smoke test uses it.
        public FanTestRunner(IFanTestHost host, double timeScale = 1.0)
        {
            _host = host;
            _timeScale = timeScale;
            _steps = BuildSteps(Environment.ProcessorCount);
            _probeTimer.Tick += ProbeTick;
        }

        internal static List<TestStep> BuildSteps(int cores) => new()
        {
            new("idle", TestKind.Idle, 0, 0, 0, 60,
                "No load. Fans should stay put."),
            new("burst-1core-3s", TestKind.Burst, 1, 0, 3, 50,
                "3 s single-core spike, like opening an app."),
            new("burst-allcore-3s", TestKind.Burst, cores, 0, 3, 50,
                "3 s all-core spike."),
            new("burst-allcore-10s", TestKind.Burst, cores, 0, 10, 50,
                "10 s all-core spike."),
            new("light-2core-60s", TestKind.Light, 2, 0, 60, 50,
                "Light sustained load, like browsing or video."),
            new("heavy-allcore-60s", TestKind.Sustained, cores, 0, 60, 90,
                "Gaming-like sustained load. Fans should rise after the ramp-up delay and drop after the ramp-down delay."),
            new("firmware-fixed-40", TestKind.Firmware, cores, 20, 45, 30,
                $"Curve off, fans fixed at {FixedTestSpeed}%. Checks whether the laptop overrides the app's fan speed when hot."),
        };

        public void Cancel() => _cts?.Cancel();

        // Returns the path of the report zip.
        public async Task<string> RunAsync()
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;
            lock (_rows) _rows.Clear();
            _timings.Clear();

            var settings = _host.FilterSettings;
            string settingsText = _host.DescribeFanSettings();
            string csv = FanLog.StartTestFile();
            FanLog.Recorded += OnRecorded;
            FanLog.Event("test start | " + settingsText.Replace(Environment.NewLine, " | "));

            _testStartMs = Environment.TickCount64;
            _probeTimer.Start();
            bool cancelled = false;

            try
            {
                for (_stepIndex = 0; _stepIndex < _steps.Count; _stepIndex++)
                {
                    var step = _steps[_stepIndex];
                    var t = new StepTiming { Step = step, StartMs = Environment.TickCount64 };
                    _stepStartMs = t.StartMs;
                    FanLog.Phase = step.Name;
                    FanLog.Event("step start: " + step.Purpose);

                    if (step.Kind == TestKind.Firmware)
                        _host.BeginFixedSpeedOverride(FixedTestSpeed, FixedTestSpeed);

                    try
                    {
                        await Task.Delay(Scaled(step.PreSec), ct);

                        t.LoadStartMs = Environment.TickCount64;
                        if (step.Threads > 0)
                        {
                            _load.Start(step.Threads);
                            FanLog.Event($"load on: {step.Threads} threads for {step.LoadSec} s");
                        }
                        await Task.Delay(Scaled(step.LoadSec), ct);

                        _load.Stop();
                        t.LoadEndMs = Environment.TickCount64;
                        if (step.Threads > 0) FanLog.Event("load off");

                        await Task.Delay(Scaled(step.RestSec), ct);
                    }
                    finally
                    {
                        _load.Stop();
                        long now = Environment.TickCount64;
                        if (t.LoadStartMs == 0) t.LoadStartMs = now;
                        if (t.LoadEndMs == 0) t.LoadEndMs = now;
                        t.EndMs = now;
                        _timings.Add(t);

                        if (step.Kind == TestKind.Firmware)
                            _host.EndFixedSpeedOverride();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                _load.Stop();
                _probeTimer.Stop();
                FanLog.Phase = "";
                FanLog.Event(cancelled ? "test cancelled" : "test finished");
                FanLog.Recorded -= OnRecorded;
                FanLog.EndTestFile();
                cts.Dispose();
                _cts = null;
            }

            List<FanLogRow> rows;
            lock (_rows) rows = _rows.ToList();
            string summary = BuildSummary(rows, _timings, settings, settingsText, cancelled);
            return FanLog.CreateReport(summary, csv);
        }

        private int Scaled(int seconds) => (int)(seconds * 1000 * _timeScale);

        private void OnRecorded(FanLogRow row)
        {
            lock (_rows) _rows.Add(row);
        }

        private void ProbeTick(object? sender, EventArgs e)
        {
            try
            {
                var (cpuT, gpuT, cpuRpm, gpuRpm) = _host.ReadSensors();
                FanLog.Probe(cpuT, gpuT, cpuRpm, gpuRpm);

                if (_stepIndex >= _steps.Count) return;
                var step = _steps[_stepIndex];
                long now = Environment.TickCount64;
                double inStep = (now - _stepStartMs) / 1000.0 / _timeScale;
                string state = inStep < step.PreSec ? "settling"
                             : inStep < step.PreSec + step.LoadSec && step.Threads > 0 ? "LOAD ON"
                             : "cooling";
                int left = Math.Max(0, TotalSec - (int)((now - _testStartMs) / 1000));
                StatusChanged?.Invoke(
                    $"Step {_stepIndex + 1}/{_steps.Count}: {step.Name} ({state})\n" +
                    $"CPU {cpuT}°C · GPU {gpuT}°C · fans {cpuRpm}/{gpuRpm} RPM · {left / 60}:{left % 60:00} left");
            }
            catch { }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _load.Stop();
            _probeTimer.Dispose();
        }

        #region Analysis

        internal static StepResult Analyze(IReadOnlyList<FanLogRow> rows, StepTiming t,
                                           (int Up, int Down, int CpuBypass, int GpuBypass) s)
        {
            var r = new StepResult();
            var inStep = rows.Where(x => x.Ms >= t.StartMs && x.Ms <= t.EndMs).ToList();
            var sensors = inStep.Where(x => x.Type is "probe" or "tick").ToList();
            var ticks = inStep.Where(x => x.Type == "tick").ToList();
            var probes = inStep.Where(x => x.Type == "probe").ToList();
            var writes = inStep.Where(x => x.Type == "write" && (x.CpuWritten.HasValue || x.GpuWritten.HasValue)).ToList();

            r.CpuTempMax = sensors.Select(x => x.CpuTemp ?? 0).DefaultIfEmpty(0).Max();
            r.GpuTempMax = sensors.Select(x => x.GpuTemp ?? 0).DefaultIfEmpty(0).Max();
            // Load average over the load window, or the whole step when it has no load.
            bool hasLoad = t.Step.Threads > 0 && t.LoadEndMs > t.LoadStartMs;
            r.CpuLoadAvg = (int)Math.Round(ticks
                .Where(x => x.CpuLoad.HasValue && (!hasLoad || (x.Ms >= t.LoadStartMs && x.Ms <= t.LoadEndMs)))
                .Select(x => (double)x.CpuLoad!.Value).DefaultIfEmpty(0).Average());
            if (s.CpuBypass > 0)
                r.SecondsAboveCpuBypass = probes.Count(x => x.CpuTemp >= s.CpuBypass) * ProbeIntervalMs / 1000.0;

            var held = ticks.Where(x => x.CpuHeld.HasValue).ToList();
            if (held.Count > 0)
            {
                r.CpuHeldStart = held[0].CpuHeld!.Value;
                r.CpuHeldMax = held.Max(x => x.CpuHeld!.Value);
                r.CpuHeldEnd = held[^1].CpuHeld!.Value;
                var raise = held.FirstOrDefault(x => x.CpuHeld > r.CpuHeldStart);
                if (raise != null) r.RaiseAfterLoadStartSec = (raise.Ms - t.LoadStartMs) / 1000.0;

                // First drop after the peak, which may come after the load stopped.
                int peakIdx = held.FindIndex(x => x.CpuHeld == r.CpuHeldMax);
                var drop = held.Skip(peakIdx + 1).FirstOrDefault(x => x.CpuHeld < r.CpuHeldMax);
                if (drop != null && r.CpuHeldMax > r.CpuHeldStart) r.DropAfterLoadEndSec = (drop.Ms - t.LoadEndMs) / 1000.0;
            }
            var gpuHeld = ticks.Where(x => x.GpuHeld.HasValue).Select(x => x.GpuHeld!.Value).ToList();
            r.GpuHeldChanges = gpuHeld.Zip(gpuHeld.Skip(1)).Count(p => p.First != p.Second);

            r.BypassTicks = ticks.Count(x => x.CpuBypass == 1);
            r.GpuBypassTicks = ticks.Count(x => x.GpuBypass == 1);
            r.CurveWrites = writes.Count(x => x.Note.Contains("ApplyFanCurve"));
            r.OtherWrites = writes.Count - r.CurveWrites;

            var cpuRpm = sensors.Where(x => x.CpuRpm > 0).Select(x => x.CpuRpm!.Value).ToList();
            r.CpuRpmMin = cpuRpm.DefaultIfEmpty(0).Min();
            r.CpuRpmMax = cpuRpm.DefaultIfEmpty(0).Max();
            r.GpuRpmMax = sensors.Select(x => x.GpuRpm ?? 0).DefaultIfEmpty(0).Max();

            var step = t.Step;
            switch (step.Kind)
            {
                case TestKind.Idle:
                    r.IdleRaise = r.RaiseAfterLoadStartSec.HasValue;
                    r.Verdict = r.IdleRaise
                        ? $"Fan rose with no test load. Something in the background is heating the CPU (avg load {r.CpuLoadAvg}%)."
                        : $"OK: fan steady at idle (CPU max {r.CpuTempMax}°C).";
                    break;

                case TestKind.Burst:
                {
                    bool raised = r.RaiseAfterLoadStartSec.HasValue;
                    bool expected = s.Up == 0 || step.LoadSec >= s.Up;
                    r.BypassRaised = raised && r.BypassTicks > 0;
                    r.UnexpectedRaise = raised && r.BypassTicks == 0 && !expected;
                    r.Verdict = !raised ? $"OK: spike ignored (CPU peaked at {r.CpuTempMax}°C)."
                        : r.BypassRaised ? $"Fan raised BY THE BYPASS: CPU hit {r.CpuTempMax}°C (bypass {s.CpuBypass}°C) for ~{r.SecondsAboveCpuBypass:0.#} s."
                        : expected ? "Raised: expected, since the spike was at least as long as the ramp-up delay."
                        : "Raised without the bypass. Unexpected, check the CSV.";
                    break;
                }

                case TestKind.Light:
                    r.BypassRaised = r.BypassTicks > 0;
                    r.Verdict = r.RaiseAfterLoadStartSec is double riseAt
                        ? $"Fan rose from {r.CpuHeldStart}% to {r.CpuHeldMax}% at +{riseAt:0} s{(r.BypassRaised ? " (bypass fired)" : "")}. A long light load is enough to need more fan with this curve."
                        : $"No change. Light load stayed within the curve (CPU max {r.CpuTempMax}°C).";
                    break;

                case TestKind.Sustained:
                {
                    r.BypassRaised = r.BypassTicks > 0;
                    var sb = new StringBuilder();
                    sb.Append(r.RaiseAfterLoadStartSec is double up
                        ? $"Rose {r.CpuHeldStart}%→{r.CpuHeldMax}% at +{up:0} s (ramp-up delay {s.Up} s{(r.BypassRaised ? "; bypass fired" : "")})."
                        : "Fan did not rise. Curve didn't ask for more at these temps.");
                    if (r.CpuHeldMax > r.CpuHeldStart)
                        sb.Append(r.DropAfterLoadEndSec is double down
                            ? $" Dropped +{down:0} s after load stopped (ramp-down delay {s.Down} s)."
                            : $" Still held {r.CpuHeldEnd}% {step.RestSec} s after load stopped.");
                    r.Verdict = sb.ToString();
                    break;
                }

                case TestKind.Firmware:
                {
                    long settleFrom = t.LoadStartMs - 8000;
                    var settled = probes.Where(x => x.Ms >= settleFrom && x.Ms < t.LoadStartMs && x.CpuRpm > 0).Select(x => x.CpuRpm!.Value).ToList();
                    var settledGpu = probes.Where(x => x.Ms >= settleFrom && x.Ms < t.LoadStartMs && x.GpuRpm > 0).Select(x => x.GpuRpm!.Value).ToList();
                    var after = probes.Where(x => x.Ms >= t.LoadStartMs).ToList();
                    r.SettledCpuRpm = Median(settled);
                    r.SettledGpuRpm = Median(settledGpu);
                    r.PeakCpuRpm = after.Select(x => x.CpuRpm ?? 0).DefaultIfEmpty(0).Max();
                    r.PeakGpuRpm = after.Select(x => x.GpuRpm ?? 0).DefaultIfEmpty(0).Max();
                    r.EndCpuRpm = Median(probes.Where(x => x.Ms >= t.EndMs - 5000 && x.CpuRpm > 0).Select(x => x.CpuRpm!.Value).ToList());

                    int laterWrites = writes.Count(x => x.Ms > t.StartMs + 2000);
                    bool rose(int settledRpm, int peak) => settledRpm > 0 && peak > settledRpm * 1.25 && peak - settledRpm > 400;

                    if (r.SettledCpuRpm == 0)
                        r.Verdict = "Inconclusive: no fan RPM readings.";
                    else if (laterWrites > 0)
                        r.Verdict = $"Inconclusive: {laterWrites} fan writes happened during this step (see CSV).";
                    else if (rose(r.SettledCpuRpm, r.PeakCpuRpm) || rose(r.SettledGpuRpm, r.PeakGpuRpm))
                    {
                        r.FirmwareOverride = true;
                        r.Verdict = $"FANS SPED UP ON THEIR OWN: CPU {r.SettledCpuRpm}→{r.PeakCpuRpm} RPM, GPU {r.SettledGpuRpm}→{r.PeakGpuRpm} RPM while the app held {FixedTestSpeed}%. The laptop overrides custom speeds when hot.";
                    }
                    else
                        r.Verdict = $"OK: fans held the fixed speed under load (CPU {r.SettledCpuRpm}→{r.PeakCpuRpm} RPM, {r.EndCpuRpm} RPM at the end).";
                    break;
                }
            }
            return r;
        }

        private static int Median(List<int> v)
        {
            if (v.Count == 0) return 0;
            v.Sort();
            return v[v.Count / 2];
        }

        internal static string BuildSummary(IReadOnlyList<FanLogRow> rows, IReadOnlyList<StepTiming> timings,
                                            (int Up, int Down, int CpuBypass, int GpuBypass) s,
                                            string settingsText, bool cancelled)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PREDATOR CONTROL: FAN TEST REPORT");
            sb.AppendLine($"Generated {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine(FanLog.SystemInfoText());
            sb.AppendLine();
            sb.AppendLine("Fan settings at test start:");
            sb.AppendLine(settingsText);
            sb.AppendLine();
            sb.AppendLine(cancelled ? $"Test was STOPPED early after {timings.Count} step(s)." : "Test completed.");
            sb.AppendLine();

            var results = new List<(StepTiming T, StepResult R)>();
            for (int i = 0; i < timings.Count; i++)
            {
                var t = timings[i];
                var r = Analyze(rows, t, s);
                results.Add((t, r));

                sb.AppendLine($"[{i + 1}] {t.Step.Name}: {t.Step.Purpose}");
                sb.AppendLine($"    CPU max {r.CpuTempMax}°C, load avg {r.CpuLoadAvg}%" +
                              (s.CpuBypass > 0 ? $", at/above bypass {r.SecondsAboveCpuBypass:0.#} s" : "") +
                              $" · GPU max {r.GpuTempMax}°C");
                sb.AppendLine($"    CPU fan held {Pct(r.CpuHeldStart)} → max {Pct(r.CpuHeldMax)} → end {Pct(r.CpuHeldEnd)}" +
                              $" · bypass ticks CPU {r.BypassTicks}/GPU {r.GpuBypassTicks}" +
                              $" · writes curve {r.CurveWrites}/other {r.OtherWrites}" +
                              $" · RPM CPU {r.CpuRpmMin}–{r.CpuRpmMax}, GPU max {r.GpuRpmMax}");
                sb.AppendLine($"    => {r.Verdict}");
                sb.AppendLine();
            }

            sb.AppendLine("CONCLUSIONS");
            var bypassBursts = results.Where(x => x.T.Step.Kind == TestKind.Burst && x.R.BypassRaised).ToList();
            bool any = false;
            if (bypassBursts.Count > 0)
            {
                any = true;
                sb.AppendLine($"- The bypass fired on {bypassBursts.Count} short spike(s) (CPU peaks up to {bypassBursts.Max(x => x.R.CpuTempMax)}°C). " +
                              "This is the likely cause of desktop peaks. Fix: set CPU bypass to Off or above the spike peak.");
            }
            if (results.Any(x => x.R.UnexpectedRaise))
            {
                any = true;
                sb.AppendLine("- A short spike raised the fan without the bypass. That shouldn't happen; check the CSV.");
            }
            if (results.Any(x => x.R.FirmwareOverride))
            {
                any = true;
                sb.AppendLine("- The laptop's firmware raises the fans by itself when hot, even with a fixed custom speed. " +
                              "Peaks caused by this can't be filtered by the app.");
            }
            if (results.Any(x => x.R.IdleRaise))
            {
                any = true;
                sb.AppendLine("- Fans rose at idle: background activity was heating the CPU during the test.");
            }
            var light = results.FirstOrDefault(x => x.T.Step.Kind == TestKind.Light);
            if (light.R is { RaiseAfterLoadStartSec: not null, BypassRaised: false })
            {
                any = true;
                sb.AppendLine("- A long light load raised the fans through the normal curve. If that's the desktop peak, " +
                              "lower the curve in the 50–75°C range or use a longer ramp-up delay.");
            }
            if (!any)
                sb.AppendLine("- Short spikes were ignored and no firmware override was seen. If peaks still happen in normal use, " +
                              "the session logs (fanlog-*.csv) show what the curve did at those moments.");

            sb.AppendLine();
            sb.AppendLine("Files: fantest-*.csv = this test (0.5 s probes); fanlog-*.csv = normal use; crash.log if any.");
            return sb.ToString();
        }

        private static string Pct(int v) => v < 0 ? "n/a" : $"{v}%";

        #endregion
    }

    // CI check of the whole load-test flow (timers, load threads, logging, summary, zip) with fake sensors.
    // Exit code 0 = report produced with every step; anything else = failure.
    [SupportedOSPlatform("windows")]
    internal static class FanTestSmoke
    {
        private sealed class FakeHost : IFanTestHost
        {
            public bool CurveActive => true;
            public (int Up, int Down, int CpuBypass, int GpuBypass) FilterSettings => (20, 45, 90, 85);
            public string DescribeFanSettings() => "smoke test (fake sensors)";
            public (int CpuTemp, int GpuTemp, int CpuRpm, int GpuRpm) ReadSensors() => (60, 55, 2500, 2400);
            public void BeginFixedSpeedOverride(int cpuPercent, int gpuPercent) => FanLog.FanWrite(cpuPercent, gpuPercent, true, "FakeHost");
            public void EndFixedSpeedOverride() { }
        }

        public static int Run()
        {
            int code = 1;
            var ctx = new ApplicationContext();
            var kick = new System.Windows.Forms.Timer { Interval = 1 };
            kick.Tick += async (s, e) =>
            {
                kick.Stop();
                try
                {
                    FanLog.SetEnabled(true);
                    using var runner = new FanTestRunner(new FakeHost(), timeScale: 0.02);
                    string zipPath = await runner.RunAsync();

                    using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
                    var summaryEntry = zip.GetEntry("summary.txt");
                    string summary = "";
                    if (summaryEntry != null)
                        using (var r = new StreamReader(summaryEntry.Open())) summary = r.ReadToEnd();

                    int steps = FanTestRunner.BuildSteps(Environment.ProcessorCount).Count;
                    bool ok = summary.Contains($"[{steps}] ")
                           && summary.Contains("CONCLUSIONS")
                           && zip.Entries.Any(x => x.Name.StartsWith("fantest-") && x.Length > 0)
                           && zip.Entries.Any(x => x.Name.StartsWith("fanlog-") && x.Length > 0);
                    code = ok ? 0 : 2;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(FanLog.Folder, "smoke-error.txt"), ex.ToString()); } catch { }
                    code = 3;
                }
                finally
                {
                    ctx.ExitThread();
                }
            };
            kick.Start();
            Application.Run(ctx);
            return code;
        }
    }

    // Busy-loops on N threads at below-normal priority so the UI stays responsive.
    internal sealed class LoadGenerator
    {
        private volatile bool _run;
        private readonly List<Thread> _threads = new();
        private static double _sink;

        public void Start(int threads)
        {
            Stop();
            _run = true;
            for (int i = 0; i < threads; i++)
            {
                var t = new Thread(Spin) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "FanTestLoad" };
                _threads.Add(t);
                t.Start();
            }
        }

        public void Stop()
        {
            _run = false;
            foreach (var t in _threads) t.Join(1000);
            _threads.Clear();
        }

        private void Spin()
        {
            double x = 1.0001;
            while (_run)
            {
                for (int i = 0; i < 200_000; i++) x = x * 1.0000001 + 1e-9;
                if (x > 1e6) x = 1.0001;
            }
            Interlocked.Exchange(ref _sink, x); // keeps the loop from being optimised away
        }
    }
}
