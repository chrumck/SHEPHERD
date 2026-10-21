using System.Collections.Generic;
using IngameScript;
using Moq;
using Sandbox.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    // The cross-tick classify seam: budget/partial/complete/reset behaviour, and equivalence to the one-shot
    // Scan.Classify (the per-block logic itself is covered by ScanTests, which stay unchanged).
    public class ClassifyCursorTests
    {
        static readonly IMyCubeGrid OwnGrid = new Mock<IMyCubeGrid>().Object;
        static readonly ManagedScope HomeScope = ManagedScope.Home(OwnGrid);

        static IMyCubeGrid GridOn(bool sameConstruct)
        {
            var g = new Mock<IMyCubeGrid>();
            g.Setup(x => x.IsSameConstructAs(It.IsAny<IMyCubeGrid>())).Returns(sameConstruct);
            return g.Object;
        }

        static IMyTerminalBlock Block(string name, bool hasInventory)
        {
            var mock = new Mock<IMyTerminalBlock>();
            mock.SetupGet(b => b.CustomName).Returns(name);
            mock.SetupGet(b => b.HasInventory).Returns(hasInventory);
            mock.Setup(b => b.GetInventory(It.IsAny<int>())).Returns(new Mock<IMyInventory>().Object);
            mock.SetupGet(b => b.CubeGrid).Returns(GridOn(true));
            return mock.Object;
        }

        static IMyTerminalBlock[] Cargos(int n)
        {
            var blocks = new IMyTerminalBlock[n];
            for (var i = 0; i < n; i++) blocks[i] = Block("Cargo " + i, true);
            return blocks;
        }

        static IMyTerminalBlock[] MixedGrid() => new[]
        {
            Block("Panel [SHEPHERD]", false),
            Block("Ore Box [SHEPHERD:Ores]", true),
            Block("Overflow [SHEPHERD:*]", true),
            Block("Cargo", true),
            Block("Ammo Locker [SHEPHERD-RESERVE:Ammo]", true),
            Block("Reactor [SHEPHERD-Off]", true),
        };

        [Fact]
        public void BudgetConsumesExactlyThatManyBlocksThenReportsMore()
        {
            var cursor = new ClassifyCursor();
            cursor.Begin(Cargos(5), HomeScope);

            Assert.True(cursor.Step(2));                 // blocks 0..1
            Assert.Equal(2, cursor.Result.Managed.Count);
            Assert.True(cursor.Step(2));                 // blocks 2..3
            Assert.Equal(4, cursor.Result.Managed.Count);
            Assert.False(cursor.Step(2));                // block 4, then done
            Assert.Equal(5, cursor.Result.Managed.Count);
        }

        [Fact]
        public void BudgetEqualToRemainingReportsDone()
        {
            var cursor = new ClassifyCursor();
            cursor.Begin(Cargos(3), HomeScope);
            Assert.False(cursor.Step(3));                // consumes all three, no "more"
            Assert.Equal(3, cursor.Result.Managed.Count);
        }

        [Fact]
        public void HasMoreReflectsProgress()
        {
            var cursor = new ClassifyCursor();
            cursor.Begin(Cargos(3), HomeScope);
            Assert.True(cursor.HasMore);
            cursor.Step(1);
            Assert.True(cursor.HasMore);
            cursor.Step(int.MaxValue);
            Assert.False(cursor.HasMore);
        }

        [Fact]
        public void FullRunReproducesOneShotClassifyExactly()
        {
            var blocks = MixedGrid();
            var oneShot = Scan.Classify(blocks, HomeScope);

            var cursor = new ClassifyCursor();
            cursor.Begin(blocks, HomeScope);
            Assert.False(cursor.Step(int.MaxValue));
            var stepped = cursor.Result;

            Assert.Equal(oneShot.Managed.Count, stepped.Managed.Count);
            Assert.Equal(oneShot.Destinations.Count, stepped.Destinations.Count);
            Assert.Equal(oneShot.Reserves.Count, stepped.Reserves.Count);
            Assert.Equal(oneShot.Tanks.Count, stepped.Tanks.Count);
            Assert.Same(blocks[0], stepped.StatusLcd);
        }

        [Fact]
        public void SmallBudgetAccumulationMatchesOneShotTotals()
        {
            var blocks = MixedGrid();
            var oneShot = Scan.Classify(blocks, HomeScope);

            var cursor = new ClassifyCursor();
            cursor.Begin(blocks, HomeScope);
            while (cursor.Step(1)) { }                    // one block per tick

            Assert.Equal(oneShot.Managed.Count, cursor.Result.Managed.Count);
            Assert.Equal(oneShot.Destinations.Count, cursor.Result.Destinations.Count);
            Assert.Equal(oneShot.Reserves.Count, cursor.Result.Reserves.Count);
        }

        [Fact]
        public void BeginStartsAFreshResultLeavingThePriorOneIntact()
        {
            // The prior result must survive Begin — the status fills render off it on the refractory ticks
            // between classify steps until finalize hands over the new one, so reusing it would blank the view.
            var cursor = new ClassifyCursor();
            cursor.Begin(Cargos(4), HomeScope);
            cursor.Step(int.MaxValue);
            var first = cursor.Result;
            Assert.Equal(4, first.Managed.Count);

            cursor.Begin(Cargos(2), HomeScope);
            Assert.NotSame(first, cursor.Result);         // a new instance
            Assert.Equal(4, first.Managed.Count);         // the old one is untouched
            Assert.Empty(cursor.Result.Managed);          // the new one starts empty
            cursor.Step(int.MaxValue);
            Assert.Equal(2, cursor.Result.Managed.Count);
        }

        [Fact]
        public void ResetDiscardsPartialPass()
        {
            var cursor = new ClassifyCursor();
            cursor.Begin(Cargos(5), HomeScope);
            cursor.Step(2);
            cursor.Reset();
            Assert.False(cursor.HasMore);
            Assert.Empty(cursor.Result.Managed);
            Assert.False(cursor.Step(int.MaxValue));      // nothing left to classify
            Assert.Empty(cursor.Result.Managed);
        }

        [Fact]
        public void NullListIsACompleteEmptyPass()
        {
            var cursor = new ClassifyCursor();
            cursor.Begin(null, HomeScope);
            Assert.False(cursor.HasMore);
            Assert.False(cursor.Step(10));
            Assert.Empty(cursor.Result.Managed);
        }
    }
}
