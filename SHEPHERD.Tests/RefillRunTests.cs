using System.Collections.Generic;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    // A gas tank filling bottles is an engine call that can cost ms on a loaded server, so its run is paced like a
    // transfer: no panel in the run, no engine call on the next, and its cost on its own profiler row.
    public class RefillRunTests
    {
        static readonly MyItemType BottleType = new MyItemType(typeof(Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_OxygenContainerObject), VRage.Utils.MyStringHash.GetOrCompute("OxygenBottle"));

        sealed class Run
        {
            public bool Drew, NextWrote;
        }

        static List<Run> RefillRuns(out List<string> debug)
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "[Settings]\nstartupSeconds=0\n");
            var status = h.AddPanel("Status [SHEPHERD]");
            debug = h.AddPanel("Debug [SHEPHERD-DEBUG]");
            h.AddTank("Tank", true);
            h.AddStock("Bottles", new MyInventoryItem(BottleType, 1, 1), new MyInventoryItem(BottleType, 2, 1));
            var runs = new List<Run>();
            Run last = null;
            for (var i = 0; i < 3000 && runs.Count < 6; i++)
            {
                int refills = h.Refills, writes = h.EngineWrites, frames = status.Count + debug.Count;
                h.Tick();
                if (last != null) { last.NextWrote = h.EngineWrites > writes; last = null; }
                if (h.Refills == refills) continue;
                last = new Run { Drew = status.Count + debug.Count > frames };
                runs.Add(last);
            }
            h.Tick(30);
            return runs;
        }

        [Fact]
        public void ARefillRunDrawsNoPanelAndTheNextRunCallsNoEngine()
        {
            List<string> debug;
            var runs = RefillRuns(out debug);
            Assert.NotEmpty(runs);
            foreach (var r in runs)
            {
                Assert.False(r.Drew);
                Assert.False(r.NextWrote);
            }
        }

        [Fact]
        public void RefillRunsHaveTheirOwnProfilerRow()
        {
            List<string> debug;
            RefillRuns(out debug);
            Assert.Matches(@"fill x[1-9]", debug[debug.Count - 1]);
        }
    }
}
