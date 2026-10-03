namespace PredatorControlApp
{
    // Delays fan-curve changes so brief temperature spikes don't move the fans.
    // Ramp up only once the curve has asked for more for the whole up-window;
    // ramp down only once it has asked for less for the whole down-window.
    internal sealed class FanSpeedFilter
    {
        public static readonly int[] DelayPresets = { 0, 5, 10, 20, 30, 45, 60, 90, 120 };
        public static readonly int[] BypassPresets = { 0, 80, 85, 90, 95, 100 };

        public const int DefaultUpDelay = 20;
        public const int DefaultDownDelay = 45;
        public const int DefaultCpuBypass = 90;
        public const int DefaultGpuBypass = 85;

        // Tolerance for timer jitter around window edges.
        private const long SlackMs = 1000;

        private readonly List<(long Ms, int Temp)> _samples = new();

        public int HeldSpeed { get; private set; } = -1;

        public void Reset()
        {
            _samples.Clear();
            HeldSpeed = -1;
        }

        public int Update(long nowMs, int temp, Func<int, int> curve,
                          int upDelaySec, int downDelaySec, int bypassTemp)
        {
            if (temp <= 0) return HeldSpeed;

            _samples.Add((nowMs, temp));
            long keepMs = Math.Max(upDelaySec, downDelaySec) * 1000L + 2 * SlackMs;
            _samples.RemoveAll(s => s.Ms < nowMs - keepMs);

            int liveSpeed = curve(temp);

            if (HeldSpeed < 0)
            {
                HeldSpeed = liveSpeed;
                return HeldSpeed;
            }

            // Temps are stored and mapped here so a new curve applies immediately,
            // and min/max are taken over speeds since curves needn't be monotonic.
            bool upValid = TryWindow(nowMs, upDelaySec, liveSpeed, curve, useMin: true, out int upSpeed);
            bool downValid = TryWindow(nowMs, downDelaySec, liveSpeed, curve, useMin: false, out int downSpeed);

            if (bypassTemp > 0 && temp >= bypassTemp)
            {
                upSpeed = upValid ? Math.Max(upSpeed, liveSpeed) : liveSpeed;
                upValid = true;
            }

            if (upValid && upSpeed > HeldSpeed)
                HeldSpeed = upSpeed;
            else if (downValid && downSpeed < HeldSpeed)
                HeldSpeed = downSpeed;

            return HeldSpeed;
        }

        private bool TryWindow(long nowMs, int delaySec, int liveSpeed, Func<int, int> curve,
                               bool useMin, out int result)
        {
            result = liveSpeed;
            if (delaySec <= 0) return true;

            long windowStart = nowMs - delaySec * 1000L;
            if (_samples[0].Ms > windowStart + SlackMs) return false;

            foreach (var s in _samples)
            {
                if (s.Ms < windowStart - SlackMs) continue;
                int speed = curve(s.Temp);
                result = useMin ? Math.Min(result, speed) : Math.Max(result, speed);
            }
            return true;
        }

        public static int NearestIndex(int[] presets, int value)
        {
            int best = 0;
            for (int i = 1; i < presets.Length; i++)
                if (Math.Abs(presets[i] - value) < Math.Abs(presets[best] - value))
                    best = i;
            return best;
        }

        public static int Snap(int[] presets, int value) => presets[NearestIndex(presets, value)];

        public static string DelayLabel(int sec) => sec <= 0 ? "Off" : $"{sec}s";

        public static string BypassLabel(int temp) => temp <= 0 ? "Off" : $"{temp}°C";
    }
}
