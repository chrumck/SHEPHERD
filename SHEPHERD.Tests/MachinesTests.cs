using IngameScript;
using Moq;
using Sandbox.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    public class MachinesTests
    {
        [Theory]
        [InlineData(Role.Storage, DrainMode.All)]
        [InlineData(Role.Collector, DrainMode.All)]
        [InlineData(Role.Consumer, DrainMode.EvictForeign)]
        [InlineData(Role.Processor, DrainMode.EvictForeign)]
        public void DrainModeFollowsRole(Role role, DrainMode expected)
        {
            Assert.Equal(expected, Roles.DrainModeFor(role));
        }

        // A disassembling assembler's inventories run in reverse: input (0) holds the PRODUCED materials (harvest
        // fully), output (1) holds the items being taken apart (never scatter them). Assembly direction keeps the
        // normal Processor roles.
        [Theory]
        [InlineData(false, DrainMode.EvictForeign, DrainMode.All)]
        [InlineData(true, DrainMode.All, DrainMode.KeepAll)]
        public void DisassemblyReversesProcessorDrainRoles(bool disassembling, DrainMode input, DrainMode output)
        {
            Assert.Equal(input, Roles.InputDrainFor(Role.Processor, disassembling));
            Assert.Equal(output, Roles.OutputDrainFor(disassembling));
        }

        // Step C.3: the hardcoded-vanilla feed category per machine kind (reactors burn Ingots; generators and
        // refineries take Ores). None (assemblers, non-machines) has no feed category.
        [Theory]
        [InlineData(FeedKind.Reactor, Categories.Ingots)]
        [InlineData(FeedKind.GasGen, Categories.Ores)]
        [InlineData(FeedKind.Refinery, Categories.Ores)]
        public void FeedCategoryFollowsKind(FeedKind kind, string expected)
        {
            Assert.Equal(expected, Roles.FeedCategoryFor(kind));
        }

        [Fact]
        public void NoneFeedKindHasNoCategory()
        {
            Assert.Null(Roles.FeedCategoryFor(FeedKind.None));
        }

        // Step C.3 overshoot fix: FeedUnits caps a feed to the units that hit the target fill, so a machine is
        // filled to its target not to 100%.
        [Fact]
        public void FeedUnitsCapsToRoomWhenStackExceedsIt()
        {
            // room 30 L at 2 L/unit = 15 units, though the stack has 100 — cap to 15.
            Assert.Equal(15.0, Roles.FeedUnits(30.0, 2.0, 100.0, true), 6);
        }

        [Fact]
        public void FeedUnitsMovesWholeStackWhenItFitsUnderTarget()
        {
            // room for 50 units but the stack is only 8 — move the 8.
            Assert.Equal(8.0, Roles.FeedUnits(100.0, 2.0, 8.0, true), 6);
        }

        [Fact]
        public void FeedUnitsFloorsNonFractionalItems()
        {
            // 5 L room at 2 L/unit = 2.5 units; a non-fractional item can only move whole units → 2.
            Assert.Equal(2.0, Roles.FeedUnits(5.0, 2.0, 100.0, false), 6);
        }

        [Fact]
        public void FeedUnitsKeepsFractionForFractionalItems()
        {
            Assert.Equal(2.5, Roles.FeedUnits(5.0, 2.0, 100.0, true), 6);
        }

        [Fact]
        public void FeedUnitsIsZeroWhenLessThanOneWholeNonFractionalUnitFits()
        {
            // room for 0.4 of a unit, non-fractional → nothing moves (the Accepts gate prevents this feeding).
            Assert.Equal(0.0, Roles.FeedUnits(0.8, 2.0, 100.0, false), 6);
        }

        [Theory]
        [InlineData(0.0, 2.0, 100.0)]   // no room (at/above target)
        [InlineData(-5.0, 2.0, 100.0)]  // over target
        [InlineData(30.0, 0.0, 100.0)]  // no per-unit volume (guard against divide-by-zero)
        public void FeedUnitsIsZeroWhenNothingShouldMove(double roomVol, double perUnitVol, double stackUnits)
        {
            Assert.Equal(0.0, Roles.FeedUnits(roomVol, perUnitVol, stackUnits, true), 6);
        }

        [Theory]
        [InlineData(499.999999, 500.0, true)]    // a near-whole claim takes the rest instead of stranding ~0
        [InlineData(500.0, 500.0, true)]
        [InlineData(600.0, 500.0, true)]
        [InlineData(499.5, 500.0, false)]        // a real remainder stays
        [InlineData(0.0, 3.0, false)]
        public void LeavesDustOnlyForAUselessRemnant(double units, double stackUnits, bool dust)
        {
            Assert.Equal(dust, Roles.LeavesDust(units, stackUnits));
        }

        // Step D-a: CapUnits bounds a count-based (reserve) move to the deficit, flooring non-fractional items.
        [Fact]
        public void CapUnitsTakesDeficitWhenStackExceedsIt()
        {
            Assert.Equal(30.0, Roles.CapUnits(30.0, 100.0, false), 6); // want 30 of a 100 stack → 30
        }

        [Fact]
        public void CapUnitsTakesWholeStackWhenUnderDeficit()
        {
            Assert.Equal(8.0, Roles.CapUnits(50.0, 8.0, false), 6);    // want 50 but only 8 present → 8
        }

        [Fact]
        public void CapUnitsFloorsNonFractionalDeficit()
        {
            Assert.Equal(2.0, Roles.CapUnits(2.7, 100.0, false), 6);   // ammo can't move a partial unit → 2
        }

        [Fact]
        public void CapUnitsKeepsFractionForFractionalItems()
        {
            Assert.Equal(2.7, Roles.CapUnits(2.7, 100.0, true), 6);    // ore/ingot moves the exact partial
        }

        [Fact]
        public void CapUnitsIsZeroWhenDeficitIsZeroOrNegative()
        {
            Assert.Equal(0.0, Roles.CapUnits(0.0, 100.0, true), 6);
            Assert.Equal(0.0, Roles.CapUnits(-5.0, 100.0, true), 6);
        }

        // C.2 precise eviction (v0.15.0): a present item is foreign only when a whole unit WOULD fit on volume yet
        // the constraint still refuses it; when less than a unit of room remains the "no" is volume-driven and
        // ambiguous, so keep it (honor a manual over-fill; never false-evict legitimate fuel/ore near full).
        [Fact]
        public void IsForeignWhenAUnitFitsOnVolumeButConstraintRefuses()
        {
            Assert.True(Roles.IsForeign(10.0, 2.0, false)); // room for 5 units, still refused → foreign
        }

        [Fact]
        public void NotForeignWhenConstraintAcceptsAndRoomExists()
        {
            Assert.False(Roles.IsForeign(10.0, 2.0, true)); // legitimate, kept
        }

        [Fact]
        public void NotForeignWhenLessThanOneUnitOfRoomRemains()
        {
            // The over-fill honor case: <1 unit of free volume, constraint reports "no" purely on volume → keep.
            Assert.False(Roles.IsForeign(0.8, 2.0, false));
        }

        [Fact]
        public void NotForeignWhenExactlyFull()
        {
            Assert.False(Roles.IsForeign(0.0, 2.0, false)); // 100% full → keep everything
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, true, false)]   // switched off
        [InlineData(true, false, false)]   // damaged past working
        public void AMachineIsOperationalOnlyWhenOnAndFunctional(bool enabled, bool functional, bool expected)
        {
            var machine = new Mock<IMyAssembler>();
            machine.SetupGet(m => m.Enabled).Returns(enabled);
            machine.SetupGet(m => m.IsFunctional).Returns(functional);
            Assert.Equal(expected, Roles.IsOperational(machine.Object));
        }

        [Fact]
        public void ABlockWithNoPowerSwitchIsOperationalWhileFunctional()
        {
            var cargo = new Mock<IMyTerminalBlock>();
            cargo.SetupGet(m => m.IsFunctional).Returns(true);
            Assert.True(Roles.IsOperational(cargo.Object));
        }
    }
}
