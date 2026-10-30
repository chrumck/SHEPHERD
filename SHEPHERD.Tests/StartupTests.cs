using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
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
            Assert.Matches(@"slow \(\w+ " + Did + @" 1\.600ms \d+i\) \+\d+s$", LastLogged(h, log, "slow (").TrimEnd());
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
            Assert.Matches(@"throttling U\d+ \(\w+ " + Did + @" 1\.600ms \d+i\) \+\d+s$", LastLogged(h, log, "throttling").TrimEnd());
        }

        [Fact]
        public void ASpikeLineCarriesItsTimeSinceStart()
        {
            List<string> log;
            var h = Base(out log);
            h.RunMs = 20.0;
            h.Tick();
            h.RunMs = 0.05;
            Assert.Matches(@"throttling U\d+ \(\w+ " + Did + @" 20\.000ms \d+i\) \+\d+s$", LastLogged(h, log, "throttling").TrimEnd());
        }

        // What a run did, as the slow and throttling lines name it: its job, the panel it drew and the heartbeat.
        const string Did = @"(none|((sort|bottle|craft|ledger|fetch|scan|build|plan|learn|probe|scope|standby)(\+(status|debug|log|levels|stock))?|status|debug|log|levels|stock)(\+beat)?|beat)";

        [Fact]
        public void ASlowCommandIsNamed()
        {
            List<string> log;
            var h = Base(out log);
            h.Command("statsReset");
            h.RunMs = 1.6;
            h.Tick();
            h.RunMs = 0.05;
            Assert.Matches(@"slow \(cmd statsReset 1\.600ms \d+i\) \+\d+s$", LastLogged(h, log, "slow (").TrimEnd());
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
            Assert.True(TicksToFirstIdle(0) < TicksToFirstIdle(20000));
        }

        // Every long run of a live start stopped at a tenth of the cap, short of its time budget.
        [Fact]
        public void AStartPacksUpToAQuarterOfTheInstructionCap()
        {
            Assert.True(TicksToFirstIdle(12000) < TicksToFirstIdle(13000));
        }

        static readonly MyItemType BottleType = new MyItemType(typeof(Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_OxygenContainerObject), VRage.Utils.MyStringHash.GetOrCompute("OxygenBottle"));

        static int TicksToWarm(int tanks)
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "");
            var log = h.AddPanel("Log [SHEPHERD-LOG]");
            for (var i = 0; i < tanks; i++)
            {
                h.AddTank("Tank " + i, true);
                h.AddStock("Bottles " + i, new MyInventoryItem(BottleType, (uint)i, 1));
            }
            h.Instructions = 0;
            var ticks = 0;
            while (log.Count == 0 || !log[log.Count - 1].Contains("warmed up +"))
            {
                h.Tick();
                Assert.True(++ticks < 5000, "never warmed up");
            }
            return ticks;
        }

        [Fact]
        public void AStartLogsWhenItHasWarmedUp()
        {
            List<string> log;
            var h = Base(out log);
            Assert.Matches(@"warmed up \+\d+s$", LastLogged(h, log, "warmed up").TrimEnd());
        }

        // The scope scan, the scope probe and the save each used to end a start's run: 23 runs to warm up here.
        [Fact]
        public void AStartWithRoomWarmsUpInAFewRuns()
        {
            TicksToWarm(0);
            Assert.InRange(TicksToWarm(1), 1, 3);
        }

        // The dry sweep's lifts and refills skip the engine call, so they share a start's packed runs.
        [Fact]
        public void MoreTanksAddNoRunsToTheWarmUp()
        {
            // The first start in a test process compiles the harness and the script too, which fills its runs.
            TicksToWarm(0);
            // Unpacked, each tank's lift, refill and return took runs of their own: 49 runs with one tank, 70 with eight.
            Assert.InRange(TicksToWarm(8), 0, TicksToWarm(1) + 4);
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
        public void TheDryCycleWritesNothing()
        {
            // The target is full, so no real transfer is attempted either; the dry cycle still sends it a move.
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "");
            h.AddCargo("Ores [SHEPHERD:Ores]", 10, 10);
            h.AddStock("Stock", new MyInventoryItem(MyItemType.MakeOre("Iron"), 1, 100));
            h.AddTank("Tank");
            h.Tick(600);
            Assert.Equal(0, h.EngineWrites);
        }

        [Fact]
        public void TheSweepFollowsTheSort()
        {
            // The dry cycle has run the sweep's code, so a start's real sweep needs no place ahead of a long first sort.
            Assert.False(RefillLoggedBeforeTheLapEnded(""));
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

        // v1.0.30–v1.0.34 parked after a gap past the startup window; the slow runs only came later.
        [Fact]
        public void PastTheStartupWindowAGapHoldsNothing()
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "[Settings]\nstartupSeconds=0\n");
            h.Tick(5);
            var before = h.Frequency;
            h.Pause(12.0);
            h.Tick(4);
            Assert.Equal(before, h.Frequency);
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
