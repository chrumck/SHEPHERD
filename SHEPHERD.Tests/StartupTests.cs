using System.Collections.Generic;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    // A start's slow first runs: packed into fewer runs, and logged so a check can see they finished inside the
    // limiter's startup allowance.
    public class StartupTests
    {
        static ProgramHarness Base(out List<string> log)
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "");
            log = h.AddPanel("Log [SHEPHERD-LOG]");
            h.Tick(5);
            return h;
        }

        static string LastLogged(ProgramHarness h, List<string> log, string start)
        {
            h.TickUntil(() => log.Count > 0 && log[log.Count - 1].Contains(start));
            foreach (var line in log[log.Count - 1].Split('\n'))
                if (line.Contains(start)) return line;
            return null;
        }

        [Fact]
        public void ARunOverThePeakBarSoonAfterAStartIsLoggedWithItsTimeSinceStart()
        {
            List<string> log;
            var h = Base(out log);
            h.RunMs = 1.6;
            h.Tick();
            h.RunMs = 0.05;
            Assert.Matches(@"slow \(\w+ 1\.600ms\) \+\d+s$", LastLogged(h, log, "slow (").TrimEnd());
        }

        [Fact]
        public void AStartsOverBarRunsArmNoBackoff()
        {
            // Inside the limiter's startup allowance a back-off only delays the next cold path; a run over the bar
            // while working logs as slow, never as throttling.
            List<string> log;
            var h = Base(out log);
            for (var i = 0; i < 3; i++)
            {
                h.RunMs = 1.6;
                h.Tick();
                h.RunMs = 0.05;
                h.Tick(20);
            }
            Assert.DoesNotContain(log, frame => frame.Contains("throttling"));
            Assert.Contains(log, frame => frame.Contains("slow ("));
        }

        [Fact]
        public void AZeroStartupWindowBacksOffFromTheFirstRun()
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "[Settings]\nstartupSeconds=0\n");
            var log = h.AddPanel("Log [SHEPHERD-LOG]");
            h.Tick(5);
            h.RunMs = 1.6;
            h.Tick();
            h.RunMs = 0.05;
            Assert.Matches(@"throttling U\d+ \(\w+ 1\.600ms\) \+\d+s$", LastLogged(h, log, "throttling").TrimEnd());
        }

        [Fact]
        public void ASpikeLineCarriesItsTimeSinceStart()
        {
            List<string> log;
            var h = Base(out log);
            h.RunMs = 20.0;
            h.Tick();
            h.RunMs = 0.05;
            Assert.Matches(@"throttling U\d+ \(\w+ 20\.000ms\) \+\d+s$", LastLogged(h, log, "throttling").TrimEnd());
        }

        static int TicksToFirstIdle(int instructions)
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "");
            var status = h.AddPanel("Status [SHEPHERD]");
            h.AddPanel("Debug [SHEPHERD-DEBUG]");
            h.AddCargo("Ore Cargo [SHEPHERD:Ores,Ingots]", 4, 10);
            h.Instructions = instructions;
            var ticks = 0;
            while (status.Count == 0 || !status[status.Count - 1].Split('\n')[0].TrimEnd().EndsWith("· idle"))
            {
                h.Tick();
                Assert.True(++ticks < 5000, "never idle");
            }
            return ticks;
        }

        [Fact]
        public void AStartPacksItsFirstStepsIntoFewerRuns()
        {
            // Room in a run (few instructions so far) lets the first cycle's steps share runs; a run already deep in
            // the instruction budget takes one step, as every later cycle does.
            Assert.True(TicksToFirstIdle(0) < TicksToFirstIdle(10000));
        }

        [Fact]
        public void APackedStartEchoesOncePerRun()
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "");
            h.AddPanel("Status [SHEPHERD]");
            h.AddPanel("Debug [SHEPHERD-DEBUG]");
            h.AddPanel("Log [SHEPHERD-LOG]");
            h.Instructions = 0;
            h.Tick(300);
            Assert.Equal(1, h.MostEchoesInARun);
        }

        // Where the first sweep's line sits relative to the first sort lap's: the log lists the newest line first.
        static bool RefillLoggedBeforeTheLapEnded(string customData)
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", customData);
            var log = h.AddPanel("Log [SHEPHERD-LOG]");
            h.AddStock("Stock", new MyInventoryItem(MyItemType.MakeOre("Iron"), 1, 100));
            h.AddTank("Tank");
            h.TickUntil(() => log.Count > 0 && log[log.Count - 1].Contains("refilling bottles") && log[log.Count - 1].Contains("no route for"));
            var lines = new List<string>(log[log.Count - 1].Split('\n'));
            return lines.FindIndex(l => l.Contains("refilling bottles")) > lines.FindIndex(l => l.Contains("no route for"));
        }

        [Fact]
        public void AStartRefillsBottlesAheadOfItsFirstSort()
        {
            // The sweep's first runs are cold, and behind a long first sort pass they land past the startup allowance.
            Assert.True(RefillLoggedBeforeTheLapEnded(""));
        }

        [Fact]
        public void PastTheStartupWindowBottlesFollowTheSort()
        {
            Assert.False(RefillLoggedBeforeTheLapEnded("[Settings]\nstartupSeconds=0\n"));
        }

        [Fact]
        public void AGapBetweenRunsIsLoggedWithItsLength()
        {
            List<string> log;
            var h = Base(out log);
            h.Pause(12.0);
            h.Tick();
            Assert.Matches(@"no run for 12\.\d s \(wall \d+\.\d s\)$", LastLogged(h, log, "no run for").TrimEnd());
        }

        [Fact]
        public void RegularRunsLogNoGap()
        {
            List<string> log;
            var h = Base(out log);
            h.Tick(600);
            Assert.DoesNotContain(log, frame => frame.Contains("no run for"));
        }

        [Fact]
        public void ARunUnderThePeakBarIsNotLogged()
        {
            List<string> log;
            var h = Base(out log);
            h.RunMs = 0.5;
            h.Tick(200);
            Assert.DoesNotContain(log, frame => frame.Contains("slow ("));
        }
    }
}
