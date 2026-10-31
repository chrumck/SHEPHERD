using System.Collections.Generic;
using Xunit;

namespace SHEPHERD.Tests
{
    // [Settings] as a player sees it: the keys SHEPHERD adds with their defaults, what configReload reads back, and
    // what a bad line logs.
    public class SettingsTests
    {
        static ProgramHarness Base(string customData, out List<string> log)
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", customData);
            log = h.AddPanel("Log [SHEPHERD-LOG]");
            h.Tick(5);
            return h;
        }

        [Fact]
        public void AnEmptyCustomDataGetsEverySettingWithItsDefaultInTheGuidesOrder()
        {
            List<string> log;
            var h = Base("", out log);
            h.TickUntil(() => h.CustomData.Contains("statusWrapChars"));
            Assert.Equal("[Settings]\nutcOffsetHours=0\nreactorFillPct=15\ngasGenFillPct=50\nrefineryFillPct=30\n"
                + "bottleSweepMinutes=3\nsortPassSeconds=60\npriority=0\nfactionShare=false\ndockedProduction=false\n"
                + "dockedFuel=false\npeakMs=1.5\nparkSim=0.75\nresumeSim=0.8\nbackoffSim=1\nspikeMs=8\n"
                + "spikeWindowSeconds=10.5\nstartupSeconds=45\nstatusWrapChars=40\n",
                h.CustomData.Substring(h.CustomData.IndexOf("[Settings]")).Replace("\r", ""));
        }

        [Fact]
        public void ConfigReloadReadsBackEverySettingInEffect()
        {
            List<string> log;
            var h = Base("[Settings]\nutcOffsetHours=-3\nreactorFillPct=20\ngasGenFillPct=40\nrefineryFillPct=35\n"
                + "bottleSweepMinutes=0\nsortPassSeconds=90\npriority=-2\nfactionShare=true\ndockedProduction=False\n"
                + "dockedFuel=TRUE\npeakMs=1.2\nparkSim=0.6\nresumeSim=0.7\nbackoffSim=0.95\nspikeMs=7\n"
                + "spikeWindowSeconds=12\nstartupSeconds=30\nstatusWrapChars=32\n", out log);
            h.Command("configReload");
            Assert.EndsWith("\npriority -2, factionShare, dockedFuel"
                + "\npeakMs 1.2, parkSim 0.6, resumeSim 0.7, backoffSim 0.95"
                + "\nspikeMs 7, spikeWindowSeconds 12, startupSeconds 30"
                + "\nfill % 20/40/35, bottles 0 min, pass 90 s, UTC-3, wrap 32", h.Echo.TrimEnd());
        }

        [Fact]
        public void ABadOrUnknownLineIsLoggedAndLeavesTheDefault()
        {
            List<string> log;
            var h = Base("[Settings]\nreactorFillPct=101\npriority=high\nfactionShare=yes\nsortPass=90\npeakMs=0\n"
                + "resumeSim=0.5\n", out log);
            h.Command("configReload");
            Assert.EndsWith("\npriority 0"
                + "\npeakMs 1.5, parkSim 0.75, resumeSim 0.8, backoffSim 1"
                + "\nspikeMs 8, spikeWindowSeconds 10.5, startupSeconds 45"
                + "\nfill % 15/50/30, bottles 3 min, pass 60 s, UTC+0, wrap 40", h.Echo.TrimEnd());
            h.TickUntil(() => log.Count > 0 && log[log.Count - 1].Contains("both default"));
            var logged = log[log.Count - 1];
            Assert.Contains("bad setting 'reactorFillPct=101' in [Settings]", logged);
            Assert.Contains("bad setting 'priority=high' in [Settings]", logged);
            Assert.Contains("bad setting 'factionShare=yes' in [Settings]", logged);
            Assert.Contains("unknown setting 'sortPass' in [Settings]", logged);
            Assert.Contains("bad setting 'peakMs=0' in [Settings]", logged);
            Assert.Contains("resumeSim below parkSim in [Settings] - both default", logged);
        }
    }
}
