using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    // Characterizes RunStats offline — the Tier-2 instrument. Pure doubles/ints, no game types, so it is
    // exercised here exactly as it accumulates in-game.
    public class RunStatsTests
    {
        [Fact]
        public void FreshStatsAreZeroAndDoNotDivideByZero()
        {
            var s = new RunStats();
            Assert.Equal(0, s.Runs(RunKind.Scan));
            Assert.Equal(0, s.Runs(RunKind.Transfer));
            Assert.Equal(0, s.Runs(RunKind.Other));
            Assert.Equal(0.0, s.PeakMs);
            Assert.Equal(0.0, s.AvgMs(RunKind.Other)); // empty bucket → no divide-by-zero
        }

        [Fact]
        public void KindsAreTalliedSeparately()
        {
            var s = new RunStats();
            s.Record(5.0, RunKind.Scan);
            s.Record(1.0, RunKind.Transfer);
            s.Record(0.1, RunKind.Other);
            s.Record(0.2, RunKind.Other);
            s.Record(0.3, RunKind.Fetch);
            s.Record(0.4, RunKind.Plan);
            s.Record(0.5, RunKind.Save);
            s.Record(9.5, RunKind.Command);
            s.Record(0.7, RunKind.Build);
            Assert.Equal(1, s.Runs(RunKind.Scan));
            Assert.Equal(1, s.Runs(RunKind.Transfer));
            Assert.Equal(2, s.Runs(RunKind.Other));
            Assert.Equal(1, s.Runs(RunKind.Fetch));
            Assert.Equal(5.0, s.MaxMs(RunKind.Scan));
            Assert.Equal(1.0, s.MaxMs(RunKind.Transfer));
            Assert.Equal(0.2, s.MaxMs(RunKind.Other));
            Assert.Equal(0.3, s.MaxMs(RunKind.Fetch));
            Assert.Equal(0.4, s.MaxMs(RunKind.Plan));
            Assert.Equal(0.5, s.MaxMs(RunKind.Save));
            Assert.Equal(9.5, s.MaxMs(RunKind.Command));
            Assert.Equal(0.7, s.MaxMs(RunKind.Build));
            Assert.Equal(5.0, s.MaxMs(RunKind.Scan));     // a build sample never lands in the classify row
        }

        [Fact]
        public void AveragesAreMeansPerKind()
        {
            var s = new RunStats();
            s.Record(0.2, RunKind.Transfer);
            s.Record(0.4, RunKind.Transfer);
            Assert.Equal(0.3, s.AvgMs(RunKind.Transfer), 6);
        }

        [Fact]
        public void PeakSpansAllKindsButPerKindMaxDoesNot()
        {
            var s = new RunStats();
            s.Record(0.3, RunKind.Transfer);
            s.Record(5.2, RunKind.Scan);    // the whole-grid scan spike (the observed 507-inventory case)
            s.Record(0.1, RunKind.Other);
            Assert.Equal(5.2, s.PeakMs);                 // the worst single run, whatever its kind
            Assert.Equal(0.3, s.MaxMs(RunKind.Transfer)); // ...transfer max is untouched by the scan spike
            Assert.Equal(0.1, s.MaxMs(RunKind.Other));
        }

        [Fact]
        public void SlidingAverageIsTheMeanOverTheWindow()
        {
            var s = new RunStats();
            Assert.Equal(0.0, s.SlidingAvgMs);       // empty window → no divide-by-zero
            s.Record(0.2, RunKind.Other);
            s.Record(0.4, RunKind.Other);
            Assert.Equal(0.3, s.SlidingAvgMs, 6);    // mean over the (still-partial) window
        }

        [Fact]
        public void SlidingAverageOnlyCountsTheLastFiftyRuns()
        {
            var s = new RunStats();
            for (var i = 0; i < 50; i++) s.Record(1.0, RunKind.Other); // fill the 50-run window
            Assert.Equal(1.0, s.SlidingAvgMs, 6);
            for (var i = 0; i < 50; i++) s.Record(0.0, RunKind.Other); // push every 1.0 out of the window
            Assert.Equal(0.0, s.SlidingAvgMs, 6);                      // old runs are no longer counted
        }

        [Fact]
        public void WindowAccumulatesElapsedAndResets()
        {
            var s = new RunStats();
            s.AddElapsed(1.5, 0);
            s.AddElapsed(0.5, 0);
            Assert.Equal(2.0, s.WindowSec, 6);
            s.Reset();
            Assert.Equal(0.0, s.WindowSec);
        }

        [Fact]
        public void CountsRunsOverTheBarPerKind()
        {
            var s = new RunStats();
            s.Record(0.8, RunKind.Other);       // at the bar is not over it
            s.Record(0.81, RunKind.Other);
            s.Record(6.0, RunKind.Other);
            s.Record(0.9, RunKind.Transfer);
            Assert.Equal(2, s.Over(RunKind.Other));
            Assert.Equal(1, s.Over(RunKind.Transfer));
            s.BarMs = 1.0;                      // a server with a looser limit
            s.Record(0.9, RunKind.Transfer);
            Assert.Equal(1, s.Over(RunKind.Transfer));
        }

        [Fact]
        public void ResetZeroesEveryBucket()
        {
            var s = new RunStats();
            s.Record(5.0, RunKind.Scan);
            s.Record(1.0, RunKind.Transfer);
            s.Record(0.5, RunKind.Other);
            s.AddElapsed(3.0, 0);
            s.Reset();
            Assert.Equal(0, s.Runs(RunKind.Scan));
            Assert.Equal(0, s.Runs(RunKind.Transfer));
            Assert.Equal(0, s.Runs(RunKind.Other));
            Assert.Equal(0.0, s.PeakMs);
            Assert.Equal(0.0, s.MaxMs(RunKind.Scan));
            Assert.Equal(0, s.Over(RunKind.Scan));
            Assert.Equal(0.0, s.SlidingAvgMs);
            Assert.Equal(0.0, s.WindowSec);
        }
    }
}
