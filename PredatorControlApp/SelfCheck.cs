using System.Diagnostics;

namespace PredatorControlApp
{
    internal static class SelfCheck
    {
        [Conditional("DEBUG")]
        public static void Run()
        {
            var messy = new List<Point> { new(500, 500), new(10, -20), new(60, 60), new(60, 61) };
            var n = FanCurveGraph.Normalize(messy);

            Debug.Assert(n.Count == messy.Count, "Normalize dropped points");
            Debug.Assert(n[0].X == 30 && n[^1].X == 100, "Normalize must pin first/last temp");
            for (int i = 0; i < n.Count; i++)
            {
                Debug.Assert(n[i].Y is >= 0 and <= 100, "speed out of range");
                Debug.Assert(n[i].X is >= 30 and <= 100, "temp out of range");
                Debug.Assert(i == 0 || n[i - 1].X <= n[i].X, "Normalize must sort by temp");
            }

            Debug.Assert(FanCurveGraph.Normalize(null).Count >= 2, "null must yield default curve");
            Debug.Assert(FanCurveGraph.Normalize(new List<Point> { new(50, 50) }).Count >= 2,
                "single point must yield default curve");

            Debug.Assert(Form1.BatteryProfileValues.Length == 4, "battery table size");
            Debug.Assert(Form1.BatteryProfileValues[3] == 0x06, "battery Eco must map to 0x06");
            Debug.Assert(Form1.AcProfileValues.Length == 5, "AC table size");
            Debug.Assert(Form1.AcProfileValues[4] == 0x05, "AC Turbo must map to 0x05");

            CheckPowerLineDebounce();
            CheckFanSpeedFilter();
        }

        private static void CheckPowerLineDebounce()
        {
            bool? pending = null;
            int ticks = 0;
            bool? state = false;

            state = Form1.DebouncePowerLine(PowerLineStatus.Online, state, ref pending, ref ticks);
            Debug.Assert(state == false, "one Online sample must not flip state");

            state = Form1.DebouncePowerLine(PowerLineStatus.Offline, state, ref pending, ref ticks);
            Debug.Assert(state == false, "a contradicting sample must restart the count");

            state = Form1.DebouncePowerLine(PowerLineStatus.Online, state, ref pending, ref ticks);
            state = Form1.DebouncePowerLine(PowerLineStatus.Online, state, ref pending, ref ticks);
            Debug.Assert(state == true, "two agreeing samples must flip state");

            state = Form1.DebouncePowerLine(PowerLineStatus.Unknown, state, ref pending, ref ticks);
            Debug.Assert(state == true, "Unknown must not change state");
            Debug.Assert(ticks == 0 && pending == null, "Unknown must reset the count");

            bool? unknownStart = null;
            pending = null;
            ticks = 0;
            unknownStart = Form1.DebouncePowerLine(PowerLineStatus.Offline, unknownStart, ref pending, ref ticks);
            Debug.Assert(unknownStart == null, "unresolved state must stay unresolved after one sample");
            unknownStart = Form1.DebouncePowerLine(PowerLineStatus.Offline, unknownStart, ref pending, ref ticks);
            Debug.Assert(unknownStart == false, "unresolved state must resolve after two agreeing samples");

            CheckUpdater();
        }

        [Conditional("DEBUG")]
        private static void CheckFanSpeedFilter()
        {
            const int up = 20, down = 45, bypass = 90;
            Func<int, int> linear = t => t;
            long now = 0;
            var f = new FanSpeedFilter();
            int Feed(int temp, Func<int, int>? curve = null)
            {
                int r = f.Update(now, temp, curve ?? linear, up, down, bypass);
                now += 2000;
                return r;
            }

            Debug.Assert(Feed(50) == 50, "first sample must apply immediately");
            for (int i = 0; i < 10; i++) Feed(50);

            for (int i = 0; i < 3; i++)
                Debug.Assert(Feed(80) == 50, "a 6s spike must not raise the fan");
            for (int i = 0; i < 30; i++) Feed(50);

            for (int i = 0; i < 10; i++)
                Debug.Assert(Feed(80) == 50, "sustained heat must wait out the ramp-up delay");
            Debug.Assert(Feed(80) == 80, "20s of sustained heat must raise the fan");
            for (int i = 0; i < 20; i++) Feed(80);

            for (int i = 0; i < 23; i++)
                Debug.Assert(Feed(50) == 80, "fan must hold through the ramp-down delay");
            Debug.Assert(Feed(50) == 50, "fan must drop once the ramp-down delay has passed");

            Debug.Assert(Feed(92) == 92, "bypass temp must raise the fan immediately");
            Debug.Assert(Feed(50) == 92, "bypass must not skip the ramp-down delay");

            int before = f.HeldSpeed;
            Debug.Assert(Feed(0) == before, "a failed sensor read must not change the speed");

            f.Reset();
            Debug.Assert(Feed(70) == 70, "the first sample after Reset must apply immediately");
            Debug.Assert(Feed(85) == 70, "after Reset, ramp-up still needs a full window of history");

            var fresh = new FanSpeedFilter();
            Debug.Assert(fresh.Update(0, 0, linear, up, down, bypass) == -1 && fresh.HeldSpeed == -1,
                "no valid sample means no speed");

            var instant = new FanSpeedFilter();
            long t0 = 0;
            foreach (int temp in new[] { 50, 80, 60, 95, 40 })
            {
                Debug.Assert(instant.Update(t0, temp, linear, 0, 0, 0) == temp, "zero delays must follow the curve exactly");
                t0 += 2000;
            }

            // Hotter means slower here, so a temp rise is a speed drop and must wait the ramp-down delay.
            Func<int, int> inverted = t => t < 70 ? 60 : 30;
            f.Reset();
            Debug.Assert(Feed(50, inverted) == 60, "non-monotonic curve: initial speed");
            for (int i = 0; i < 3; i++)
                Debug.Assert(Feed(80, inverted) == 60, "non-monotonic curve: filter on speed, not temp");
            for (int i = 0; i < 30; i++) Feed(80, inverted);
            Debug.Assert(f.HeldSpeed == 30, "non-monotonic curve: sustained drop must apply after the delay");

            Debug.Assert(FanSpeedFilter.Snap(FanSpeedFilter.DelayPresets, 25) is 20 or 30, "snap must pick a neighbour");
            Debug.Assert(FanSpeedFilter.Snap(FanSpeedFilter.DelayPresets, 500) == 120, "snap must clamp high");
            Debug.Assert(FanSpeedFilter.Snap(FanSpeedFilter.BypassPresets, 87) == 85, "snap must pick nearest");
            Debug.Assert(FanSpeedFilter.Snap(FanSpeedFilter.BypassPresets, -5) == 0, "snap must clamp low");
        }

        [Conditional("DEBUG")]
        private static void CheckUpdater()
        {
            Debug.Assert(Updater.TryParseTag("v1.3.1", out var tag) && tag == new Version(1, 3, 1), "tag must parse without the v");
            Debug.Assert(!Updater.TryParseTag("nightly", out _), "junk tags must be rejected");
            Debug.Assert(Updater.Current.Revision == -1, "Current must be Major.Minor.Build so tags compare cleanly");

            using var doc = System.Text.Json.JsonDocument.Parse("""
                {"assets":[
                    {"name":"PredatorControl-standalone-win-x64.exe","browser_download_url":"http://x/big.exe"},
                    {"name":"PredatorControl-win-x64.exe","browser_download_url":"http://x/light.exe"}]}
                """);
            var rel = doc.RootElement;
            Debug.Assert(Updater.PickAsset(rel, true) == "http://x/big.exe", "self-contained build must take the standalone asset");
            Debug.Assert(Updater.PickAsset(rel, false) == "http://x/light.exe", "framework-dependent build must take the light asset");

            using var noMatch = System.Text.Json.JsonDocument.Parse("""
                {"assets":[{"name":"only-light.exe","browser_download_url":"http://x/only.exe"}]}
                """);
            Debug.Assert(Updater.PickAsset(noMatch.RootElement, true) == "http://x/only.exe", "must fall back to any .exe");

            using var none = System.Text.Json.JsonDocument.Parse("""{"assets":[{"name":"notes.txt","browser_download_url":"http://x/n"}]}""");
            Debug.Assert(Updater.PickAsset(none.RootElement, true) == null, "non-exe assets must be ignored");

            string plain = Updater.Plain("## What's new\r\n\r\n**Bold.** text\r\n\r\n\r\n- one\r\n* two\r\n\r\n`code` and [link](http://x)");
            Debug.Assert(!plain.Contains('#') && !plain.Contains('*') && !plain.Contains('`'), "markdown syntax must be stripped");
            Debug.Assert(plain.StartsWith("What's new"), "headings must lose their hashes");
            Debug.Assert(plain.Contains("\u2022 one") && plain.Contains("\u2022 two"), "list markers must become bullets");
            Debug.Assert(plain.Contains("link") && !plain.Contains("http://x"), "link text must survive, url must not");
            string nl = Environment.NewLine;
            Debug.Assert(!plain.Contains(nl + nl + nl) && !plain.EndsWith(nl), "blank runs must collapse and not trail");
        }
    }
}
