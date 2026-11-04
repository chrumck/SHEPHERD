using System.Collections.Generic;
using System.Text.RegularExpressions;
using Xunit;

namespace SHEPHERD.Tests
{
    // The production plan walks every item key the ledger saw, twice (craft, then refine). On a base holding many kinds
    // of item one run doing both passed the peak bar, so they take a run each.
    public class PlanTests
    {
        static int Read(List<string> debug, string pattern)
        {
            var m = Regex.Match(debug[debug.Count - 1], pattern, RegexOptions.Multiline);
            Assert.True(m.Success, pattern);
            return int.Parse(m.Groups[1].Value);
        }

        [Fact]
        public void ThePlanTakesTwoRunsACycle()
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "");
            var debug = h.AddPanel("Debug [SHEPHERD-DEBUG]");
            h.AddCargo("Ore Cargo [SHEPHERD:Ores,Ingots]", 4, 10);
            const string cycles = @"^cycles (\d+)";
            h.TickUntil(() => debug.Count > 0 && Read(debug, cycles) >= 3);
            h.Command("statsReset");
            var start = Read(debug, cycles);
            h.TickUntil(() => Read(debug, cycles) >= start + 6);
            var done = Read(debug, cycles) - start;
            var plans = Read(debug, @"^plan x(\d+)");
            // A floor, not a count: the row also tallies some ticks around the plan, and the window's edges cut cycles.
            // One run doing both halves came to 9 here.
            Assert.True(plans >= 2 * done - 2, plans + " plan runs in " + done + " cycles");
        }
    }
}
