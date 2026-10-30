using System;
using System.Collections.Generic;
using IngameScript;
using VRageMath;
using Xunit;

namespace SHEPHERD.Tests
{
    // A [SHEPHERD] light shows the status panel's verdict as a colour: green, yellow when nothing moves on purpose or
    // the panel lists a state line, red when the owner must act.
    public class StatusLightTests
    {
        // No reserve, so the panel lists no state line.
        static ProgramHarness Base(string settings = "")
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", settings);
            h.AddCargo("Ore Cargo [SHEPHERD:Ores,Ingots]", 4, 10);
            return h;
        }

        static Color Shown(List<Color> colours) => colours.Count == 0 ? Color.White : colours[colours.Count - 1];

        static bool Shows(List<string> frames, string health) =>
            frames.Count > 0 && frames[frames.Count - 1].Split('\n')[0].Trim().EndsWith("· " + health, StringComparison.Ordinal);

        [Fact]
        public void EveryLightIsGreenWhileNothingNeedsReadingAndWrittenOnlyOnChange()
        {
            var h = Base();
            var a = h.AddLight("Lamp A [SHEPHERD]");
            var b = h.AddLight("Lamp B [SHEPHERD]");
            h.TickUntil(() => a.Count > 0 && b.Count > 0);
            h.Tick(600);
            Assert.Equal(new[] { Color.Lime }, a);
            Assert.Equal(new[] { Color.Lime }, b);
        }

        [Fact]
        public void ALightListedBeforeThePanelLeavesThePanelItsSlot()
        {
            var h = Base();
            var light = h.AddLight("Lamp [SHEPHERD]");
            var status = h.AddPanel("Status [SHEPHERD]");
            h.TickUntil(() => Shows(status, "idle"));
            Assert.Equal(Color.Lime, Shown(light));
        }

        [Fact]
        public void ALightIsYellowWhileThePanelListsAStateLine()
        {
            var h = Base("[Reserve:Spare]\nComponent/SteelPlate=100MA\n");
            var status = h.AddPanel("Status [SHEPHERD]");
            var light = h.AddLight("Lamp [SHEPHERD]");
            h.TickUntil(() => Shows(status, "idle") && status[status.Count - 1].Contains("no Main assembler"));
            Assert.Equal(Color.Yellow, Shown(light));
        }

        [Fact]
        public void ALightIsYellowWhileLearning()
        {
            var h = Base();
            var status = h.AddPanel("Status [SHEPHERD]");
            var light = h.AddLight("Lamp [SHEPHERD]");
            h.TickUntil(() => Shows(status, "idle"));
            h.Command("learn");
            h.TickUntil(() => Shows(status, "learning"));
            Assert.Equal(Color.Yellow, Shown(light));
        }

        [Fact]
        public void ALightIsRedWhileHaltedThenGreenOnceTheFaultClears()
        {
            var h = Base();
            var status = h.AddPanel("Status [SHEPHERD]");
            var light = h.AddLight("Lamp [SHEPHERD]");
            h.TickUntil(() => Shows(status, "idle"));
            h.FailFetch = true;
            h.TickUntil(() => Shows(status, "HALTED"));
            Assert.Equal(Color.Red, Shown(light));
            h.FailFetch = false;
            h.TickUntil(() => Shown(light) == Color.Lime);
        }

        // The limiter's last warning before it punishes.
        [Fact]
        public void ALightIsRedWhileStandingDown()
        {
            var h = Base();
            var light = h.AddLight("Lamp [SHEPHERD]");
            h.TickUntil(() => Shown(light) == Color.Lime);
            h.Command("GracefulShutDown::-1");
            h.TickUntil(() => Shown(light) == Color.Red);
            h.TickUntil(() => Shown(light) == Color.Lime);
        }

        // Its leader covers its jobs.
        [Fact]
        public void AStandingByPBShowsGreen()
        {
            var shuttle = ProgramHarness.Grid("Shuttle", false, false);
            var station = ProgramHarness.Grid("Base", true, true);
            var h = new ProgramHarness(shuttle, "Shuttle PB", "");
            var status = h.AddPanel("Shuttle Status [SHEPHERD]");
            var light = h.AddLight("Shuttle Lamp [SHEPHERD]");
            h.Dock(shuttle, station);
            h.AddPeer("Base PB", station, PeerElection.Rank(0, true, true), 2002);
            h.TickUntil(() => Shows(status, "standing by"));
            h.Tick(60);
            Assert.Equal(Color.Lime, Shown(light));
        }
    }
}
