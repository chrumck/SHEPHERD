using System.Text;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class ProfilerTests
    {
        static string Render(RunStats stats, int throttles = 0, string cadence = "U1", double throttleSec = 0.0,
                             int waries = 0, double warySec = 0.0)
        {
            var sb = new StringBuilder();
            Profiler.Render(sb, "9.9.9", "Base PB", "working", cadence, 3, 507, 7, throttles, throttleSec, waries, warySec,
                            0.98, 0.95, stats);
            return sb.ToString();
        }

        [Fact]
        public void HeaderSetsBackoffBesideWaryWithTheirShareOfTheWindow()
        {
            var stats = new RunStats();
            stats.AddElapsed(4.0, 0);
            Assert.Contains("thr x2 25% · wary x3 50%", Render(stats, 2, "U10", 1.0, 3, 2.0));
        }

        [Fact]
        public void SharesReadZeroBeforeAnyTimeHasElapsed()
        {
            Assert.Contains("thr x0 0% · wary x0 0%", Render(new RunStats()));
        }

        [Fact]
        public void HeaderNamesTheRenderingBlockOnItsOwnLine()
        {
            var lines = Render(new RunStats()).Replace("\r", "").Split('\n');
            Assert.Equal("SHEPHERD 9.9.9 · working · U1", lines[0]);
            Assert.Equal("Base PB", lines[1]);
            Assert.StartsWith("cycles ", lines[2]);
        }

        [Fact]
        public void HeaderCarriesThrottleCount()
        {
            var text = Render(new RunStats(), 4);
            Assert.Contains("thr x4", text);
        }

        [Fact]
        public void HeaderCarriesSimInstantAndAverage()
        {
            Assert.Contains("sim 0.98/0.95", Render(new RunStats()));
        }

        [Fact]
        public void LargeCountsUseSuffixesWithUpToFourSigFigs()
        {
            // Counts flow through the same Count() formatter, so exercising it via the throttle count is enough.
            Assert.Contains("thr x9999", Render(new RunStats(), 9999));    // < 10000 → plain
            Assert.Contains("thr x10k", Render(new RunStats(), 10000));    // trailing zeros trimmed
            Assert.Contains("thr x25.5k", Render(new RunStats(), 25500));  // [10,100) mantissa → 2 dp
            Assert.Contains("thr x125k", Render(new RunStats(), 125000));  // [100,1000) mantissa → 1 dp, trimmed
            Assert.Contains("thr x1.5M", Render(new RunStats(), 1500000)); // M suffix
        }

        [Fact]
        public void HeaderCarriesVersionStateAndCounts()
        {
            var text = Render(new RunStats());
            Assert.Contains("SHEPHERD 9.9.9", text);
            Assert.Contains("working", text);
            Assert.Contains("cycles 3", text);
            Assert.Contains("managed 507", text);
            Assert.Contains("dest 7", text);
            Assert.Contains("uptime", text);        // moved off line 1 onto the cycles line (v0.9.5)
            Assert.DoesNotContain("mv", text);       // the "last N mv" field was dropped (v0.9.3)
        }

        [Fact]
        public void HeaderCarriesWakeCadence()
        {
            // Line 0 only: the rate-share line below names every rate too.
            Assert.EndsWith("· U100", FirstLine(Render(new RunStats(), 0, "U100"))); // the escalated-back-off cue (B.5)
            Assert.EndsWith("· U1", FirstLine(Render(new RunStats(), 0, "U1")));
        }

        static string FirstLine(string text) => text.Replace("\r", "").Split('\n')[0];

        [Fact]
        public void HeaderSharesTheWindowAcrossWakeRates()
        {
            var stats = new RunStats();
            stats.AddElapsed(3.0, 0);
            stats.AddElapsed(6.0, 1);
            stats.AddElapsed(3.0, 2);
            Assert.Contains("U1 25% · U10 50% · U100 25%", Render(stats));
        }

        [Fact]
        public void ResetClearsTheRateShares()
        {
            var stats = new RunStats();
            stats.AddElapsed(3.0, 1);
            stats.Reset();
            stats.AddElapsed(1.0, 0);
            Assert.Contains("U1 100% · U10 0% · U100 0%", Render(stats));
        }

        [Fact]
        public void TableReportsPerKindRunsMaxAvgAndPeakAndRollingAvg()
        {
            var stats = new RunStats();
            stats.Record(0.20, RunKind.Transfer);
            stats.Record(0.60, RunKind.Transfer); // avg 0.40, max 0.60
            stats.Record(1.50, RunKind.Other);    // the all-tick peak
            stats.Record(0.30, RunKind.Plan);
            stats.Record(0.00, RunKind.Command);

            var text = Render(stats);
            Assert.Contains("peak 1.500 ms", text);
            Assert.Contains("avg50 0.520 ms", text);          // rolling mean over the 5 runs so far: 2.60/5
            Assert.Contains("xfer x2 max 0.600 avg 0.400 over 0", text);
            Assert.Contains("othr x1 max 1.500 avg 1.500 over 1", text);
            Assert.Contains("plan x1 max 0.300 avg 0.300 over 0", text);
            Assert.Contains("cmd x1 max 0.000 avg 0.000 over 0", text);
            Assert.Contains("ftch x0", text);                 // untouched kinds still print a zero row
            Assert.Contains("save x0", text);
            Assert.Contains("bld x0", text);
        }
    }
}
