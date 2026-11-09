using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    // Panel writes are capped in game time, so a fast wake rate no longer means a panel write on nearly every run.
    public class RenderCapTests
    {
        static readonly MyItemType BottleType = new MyItemType(typeof(Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_OxygenContainerObject), VRage.Utils.MyStringHash.GetOrCompute("OxygenBottle"));

        sealed class Window
        {
            public int StatusFrames, FastTicks, FastFrames;
            public double Seconds, FastSeconds;
        }

        // Three panels; the tank and bottles bring the bottle sweeps that wake at Update1.
        static Window Measure()
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "[Settings]\nstartupSeconds=0\n");
            var status = h.AddPanel("Status [SHEPHERD]");
            var debug = h.AddPanel("Debug [SHEPHERD-DEBUG]");
            var log = h.AddPanel("Log [SHEPHERD-LOG]");
            h.AddTank("Tank", true);
            h.AddStock("Bottles", new MyInventoryItem(BottleType, 1, 1), new MyInventoryItem(BottleType, 2, 1));
            h.Tick(600);
            var w = new Window();
            var statusBefore = status.Count;
            while (w.Seconds < 400.0)
            {
                var f = h.Frequency;
                var fast = (f & UpdateFrequency.Update1) != 0;
                var frames = status.Count + debug.Count + log.Count;
                w.Seconds += fast ? 1 / 60.0 : (f & UpdateFrequency.Update10) != 0 ? 10 / 60.0 : 100 / 60.0;
                h.Tick();
                if (!fast) continue;
                w.FastTicks++;
                w.FastSeconds += 1 / 60.0;
                w.FastFrames += status.Count + debug.Count + log.Count - frames;
            }
            w.StatusFrames = status.Count - statusBefore;
            return w;
        }

        [Fact]
        public void PanelsRedrawAboutOnceASecondWhateverTheWakeRate()
        {
            var w = Measure();
            // Each sweep wakes at Update1 for a second or two: three panels may draw three times a second there.
            Assert.True(w.FastTicks > 60, w.FastTicks + " Update1 ticks");
            Assert.True(w.FastFrames <= w.FastSeconds * 3 + 6, w.FastFrames + " frames in " + w.FastTicks + " Update1 ticks");
        }

        [Fact]
        public void TheStatusPanelStillRedrawsEverySecondOrTwo()
        {
            var w = Measure();
            Assert.True(w.StatusFrames >= w.Seconds / 2, w.StatusFrames + " status frames in " + w.Seconds + " s");
        }
    }
}
