using System.Collections.Generic;
using IngameScript;
using Moq;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    public class ScanTests
    {
        // A stand-in for "our" construct; blocks built by Block() report same-construct by default.
        static readonly IMyCubeGrid OwnGrid = new Mock<IMyCubeGrid>().Object;
        static readonly ManagedScope HomeScope = ManagedScope.Home(OwnGrid);

        static IMyCubeGrid GridOn(bool sameConstruct)
        {
            var g = new Mock<IMyCubeGrid>();
            g.Setup(x => x.IsSameConstructAs(It.IsAny<IMyCubeGrid>())).Returns(sameConstruct);
            return g.Object;
        }

        static ScanResult Classify(params IMyTerminalBlock[] blocks) => Scan.Classify(blocks, HomeScope);

        static IMyTerminalBlock Block(string name, bool hasInventory)
        {
            return Block(name, hasInventory, new Mock<IMyInventory>().Object);
        }

        static IMyTerminalBlock Block(string name, bool hasInventory, IMyInventory inventory)
        {
            return Block(name, hasInventory, inventory, GridOn(true));
        }

        static IMyTerminalBlock Block(string name, bool hasInventory, IMyInventory inventory, IMyCubeGrid grid)
        {
            var mock = new Mock<IMyTerminalBlock>();
            mock.SetupGet(b => b.CustomName).Returns(name);
            mock.SetupGet(b => b.HasInventory).Returns(hasInventory);
            mock.Setup(b => b.GetInventory(It.IsAny<int>())).Returns(inventory);
            mock.SetupGet(b => b.CubeGrid).Returns(grid);
            return mock.Object;
        }

        [Fact]
        public void DestinationCarriesItsAcceptSet()
        {
            var scan = Classify(new[] { Block("Ore Box [SHEPHERD:Ores]", true) });

            Assert.Single(scan.Destinations);
            Assert.Equal(new[] { Categories.Ores }, new List<string>(scan.Destinations[0].AcceptedCategories));
            Assert.False(scan.Destinations[0].IsCatchAll);
            Assert.Single(scan.Managed); // a destination is also a managed source
        }

        [Fact]
        public void CatchAllDestination()
        {
            var scan = Classify(new[] { Block("Overflow [SHEPHERD:*]", true) });
            Assert.Single(scan.Destinations);
            Assert.True(scan.Destinations[0].IsCatchAll);
        }

        [Fact]
        public void DestinationExposesTagPriority()
        {
            var scan = Classify(new[] { Block("Ore Box [SHEPHERD:Ores:#5]", true) });
            Assert.Single(scan.Destinations);
            Assert.Equal(5, scan.Destinations[0].Priority);
            Assert.Equal(new[] { Categories.Ores }, new List<string>(scan.Destinations[0].AcceptedCategories));
        }

        [Fact]
        public void PlainInventoryIsManagedButNotDestination()
        {
            var scan = Classify(new[] { Block("Cargo 3", true) });
            Assert.Single(scan.Managed);
            Assert.Empty(scan.Destinations);
        }

        [Fact]
        public void OffBlockIsExcludedEntirely()
        {
            var scan = Classify(new[] { Block("Reactor [SHEPHERD-Off]", true) });
            Assert.Empty(scan.Managed);
            Assert.Empty(scan.Destinations);
        }

        [Fact]
        public void OffBeatsDestination()
        {
            var scan = Classify(new[] { Block("Box [SHEPHERD:Ores] [SHEPHERD-Off]", true) });
            Assert.Empty(scan.Managed);
            Assert.Empty(scan.Destinations);
        }

        [Fact]
        public void StatusLcdCapturedAndNotManaged()
        {
            var scan = Classify(new[] { Block("Panel [SHEPHERD]", false) });
            Assert.NotNull(scan.StatusLcd);
            Assert.Empty(scan.Managed);
        }

        [Fact]
        public void FirstStatusLcdWins()
        {
            var a = Block("A [SHEPHERD]", false);
            var b = Block("B [SHEPHERD]", false);
            Assert.Same(a, Classify(new[] { a, b }).StatusLcd);
        }

        [Fact]
        public void DebugAndLogLcdsCapturedAndNotManaged()
        {
            var scan = Classify(new[]
            {
                Block("Profiler [SHEPHERD-DEBUG]", false),
                Block("Log [SHEPHERD-LOG]", false),
            });
            Assert.NotNull(scan.DebugLcd);
            Assert.NotNull(scan.LogLcd);
            Assert.Null(scan.StatusLcd);              // -DEBUG/-LOG are not the bare status LCD
            Assert.Empty(scan.Managed);    // display surfaces are never sources
        }

        [Fact]
        public void FirstDebugAndLogLcdWin()
        {
            var d1 = Block("D1 [SHEPHERD-DEBUG]", false);
            var d2 = Block("D2 [SHEPHERD-DEBUG]", false);
            var l1 = Block("L1 [SHEPHERD-LOG]", false);
            var l2 = Block("L2 [SHEPHERD-LOG]", false);
            var scan = Classify(new[] { d1, d2, l1, l2 });
            Assert.Same(d1, scan.DebugLcd);
            Assert.Same(l1, scan.LogLcd);
        }

        [Fact]
        public void BlockWithoutInventoryIsSkipped()
        {
            var scan = Classify(new[] { Block("Antenna", false) });
            Assert.Empty(scan.Managed);
            Assert.Empty(scan.Destinations);
        }

        [Fact]
        public void NullInventoryIsSkipped()
        {
            var scan = Classify(new[] { Block("Weird [SHEPHERD:Ores]", true, null) });
            Assert.Empty(scan.Managed);
            Assert.Empty(scan.Destinations);
        }

        [Fact]
        public void NullInputsAreSafe()
        {
            Assert.Empty(Scan.Classify(null, HomeScope).Destinations);
            Assert.Empty(Scan.Classify(new IMyTerminalBlock[] { null }, HomeScope).Destinations);
        }

        [Fact]
        public void MixedGridClassifiesEachRole()
        {
            var blocks = new[]
            {
                Block("Panel [SHEPHERD]", false),
                Block("Ore Box [SHEPHERD:Ores]", true),
                Block("Overflow [SHEPHERD:*]", true),
                Block("Cargo", true),
                Block("Reactor [SHEPHERD-Off]", true),
            };
            var scan = Classify(blocks);

            Assert.NotNull(scan.StatusLcd);
            Assert.Equal(2, scan.Destinations.Count);       // ore box + overflow
            Assert.Equal(3, scan.Managed.Count); // ore box + overflow + cargo (reactor excluded)
        }

        [Fact]
        public void ForeignConstructBlockIsSkipped()
        {
            // A grid outside the managed scope (no connector path to it) is never managed or used as a destination.
            var foreign = Block("Ally Cargo [SHEPHERD:Ores]", true, new Mock<IMyInventory>().Object, GridOn(false));
            var scan = Scan.Classify(new[] { foreign }, HomeScope);
            Assert.Empty(scan.Managed);
            Assert.Empty(scan.Destinations);
        }

        [Fact]
        public void OwnConstructBlockIsManaged()
        {
            var own = Block("My Cargo [SHEPHERD:Ores]", true); // same-construct by default
            var scan = Scan.Classify(new[] { own }, HomeScope);
            Assert.Single(scan.Destinations);
            Assert.Single(scan.Managed);
        }

        [Fact]
        public void ForeignStatusLcdIsNotAdopted()
        {
            // The scope filter runs before status-LCD capture, so an out-of-scope grid's [SHEPHERD] panel is ignored.
            var foreign = Block("Their Panel [SHEPHERD]", false, null, GridOn(false));
            var scan = Scan.Classify(new[] { foreign }, HomeScope);
            Assert.Null(scan.StatusLcd);
        }

        // --- Role classification (Step C.1) ------------------------------------------------------------------

        static IMyInventory Inv() => new Mock<IMyInventory>().Object;

        // A managed block of a specific ingame machine interface. GetInventory(0) = input, GetInventory(1) =
        // output; every machine reports HasInventory and same-construct so it reaches classification.
        static IMyTerminalBlock MachineBlock<T>(string name, IMyInventory input, IMyInventory output) where T : class, IMyTerminalBlock
        {
            var mock = new Mock<T>();
            mock.SetupGet(b => b.CustomName).Returns(name);
            mock.SetupGet(b => b.HasInventory).Returns(true);
            mock.Setup(b => b.GetInventory(0)).Returns(input);
            mock.Setup(b => b.GetInventory(1)).Returns(output);
            mock.SetupGet(b => b.CubeGrid).Returns(GridOn(true));
            return mock.Object;
        }

        static ManagedBlock Managed1<T>(IMyInventory output = null) where T : class, IMyTerminalBlock
        {
            return Scan.Classify(new[] { MachineBlock<T>("", Inv(), output) }, HomeScope).Managed[0];
        }

        [Fact] public void ReactorIsConsumer() => Assert.Equal(Role.Consumer, Managed1<IMyReactor>().Role);
        [Fact] public void GasGeneratorIsConsumer() => Assert.Equal(Role.Consumer, Managed1<IMyGasGenerator>().Role);
        [Fact] public void AssemblerIsProcessor() => Assert.Equal(Role.Processor, Managed1<IMyAssembler>().Role);
        [Fact] public void DrillIsCollector() => Assert.Equal(Role.Collector, Managed1<IMyShipDrill>().Role);
        [Fact] public void GrinderIsCollector() => Assert.Equal(Role.Collector, Managed1<IMyShipGrinder>().Role);
        [Fact] public void CollectorIsCollector() => Assert.Equal(Role.Collector, Managed1<IMyCollector>().Role);

        [Fact]
        public void PlainCargoIsStorageWithNoOutput()
        {
            var m = Scan.Classify(new[] { Block("Cargo 3", true) }, HomeScope).Managed[0];
            Assert.Equal(Role.Storage, m.Role);
            Assert.Null(m.Output); // only processors expose a harvest output
        }

        [Fact]
        public void ConsumerHasNoHarvestOutput()
        {
            // A reactor's output param is set up, but a Consumer only ever drains its primary inventory.
            Assert.Null(Managed1<IMyReactor>(Inv()).Output);
        }

        [Fact]
        public void RefineryIsProcessorAndHarvestsOutput()
        {
            var output = Inv();
            var m = Managed1<IMyRefinery>(output);
            Assert.Equal(Role.Processor, m.Role);
            Assert.Same(output, m.Output); // GetInventory(1) is picked up as the harvest source
        }

        [Fact]
        public void OffMachineIsNotManaged()
        {
            var reactor = MachineBlock<IMyReactor>("Reactor [SHEPHERD-Off]", Inv(), null);
            var scan = Scan.Classify(new[] { reactor }, HomeScope);
            Assert.Empty(scan.Managed);
        }

        // --- Reserve capture (Step D-a) ----------------------------------------------------------------------

        [Fact]
        public void BareReserveIsCapturedManagedNotDestination()
        {
            var scan = Classify(new[] { Block("Loadout [SHEPHERD-RESERVE]", true) });
            Assert.Single(scan.Reserves);
            Assert.Equal("", scan.Reserves[0].Pool);
            Assert.False(scan.Reserves[0].DualRole);
            Assert.Single(scan.Managed);          // a reserve is a source (so mis-placed items can leave)
            Assert.True(scan.Managed[0].IsReserve);
            Assert.Empty(scan.Destinations);      // a reserve is not a category destination
        }

        [Fact]
        public void NamedReserveCarriesPoolName()
        {
            var scan = Classify(new[] { Block("Ammo Locker [SHEPHERD-RESERVE:Ammo]", true) });
            Assert.Single(scan.Reserves);
            Assert.Equal("Ammo", scan.Reserves[0].Pool);
        }

        [Fact]
        public void OffReserveIsExcludedEntirely()
        {
            var scan = Classify(new[] { Block("Locker [SHEPHERD-RESERVE] [SHEPHERD-Off]", true) });
            Assert.Empty(scan.Reserves);
            Assert.Empty(scan.Managed);
            Assert.Empty(scan.Destinations);
        }

        [Fact]
        public void ReserveWithDestinationTagIsReserveOnlyAndFlaggedDualRole()
        {
            var scan = Classify(new[] { Block("Box [SHEPHERD-RESERVE:Ammo] [SHEPHERD:Ores]", true) });
            Assert.Single(scan.Reserves);
            Assert.Equal("Ammo", scan.Reserves[0].Pool);
            Assert.True(scan.Reserves[0].DualRole);   // the [SHEPHERD:Ores] tag is ignored — Program warns
            Assert.Empty(scan.Destinations);          // reserve wins: not a category destination
        }

        [Fact]
        public void ReserveTagWithoutInventoryIsALevelsLcd()
        {
            var scan = Classify(new[] { Block("Levels [SHEPHERD-RESERVE]", false), Block("Ammo [SHEPHERD-RESERVE:Ammo]", false) });
            Assert.Equal(2, scan.ReserveLcds.Count);
            Assert.Equal("", scan.ReserveLcds[0].Pool);   // bare: the default pool
            Assert.Equal("Ammo", scan.ReserveLcds[1].Pool);
            Assert.Empty(scan.Reserves);                  // a display is never a container
            Assert.Empty(scan.Managed);
        }

        [Fact]
        public void DestinationTagWithoutInventoryIsAStockLcd()
        {
            var scan = Classify(new[] { Block("Stock [SHEPHERD:Ores,Ingots]", false), Block("Levels [SHEPHERD-RESERVE] [SHEPHERD:Ores]", false) });
            Assert.Single(scan.StockLcds);   // a reserve tag wins: that one stays a levels LCD
            Assert.Equal(new[] { Categories.Ores, Categories.Ingots }, new List<string>(scan.StockLcds[0].AcceptedCategories));
            Assert.Single(scan.ReserveLcds);
            Assert.Empty(scan.Destinations); // a display is never a container
            Assert.Empty(scan.Managed);
        }

        [Fact]
        public void CatchAllContainersAreFlaggedForTheStockTally()
        {
            var scan = Classify(new[] {
                Block("Overflow [SHEPHERD:Ores,*]", true),
                Block("Ores [SHEPHERD:Ores]", true),
                Block("Reserve [SHEPHERD-RESERVE] [SHEPHERD:*]", true),   // reserve wins: not a catch-all
                Block("Screen [SHEPHERD:*]", false) });
            Assert.True(scan.Managed[0].CatchAll);
            Assert.False(scan.Managed[1].CatchAll);
            Assert.False(scan.Managed[2].CatchAll);
            Assert.True(Assert.Single(scan.StockLcds).IsCatchAll);
        }

        [Fact]
        public void OffLevelsLcdIsIgnored()
        {
            var scan = Classify(new[] { Block("Levels [SHEPHERD-RESERVE] [SHEPHERD-Off]", false) });
            Assert.Empty(scan.ReserveLcds);
        }

        // --- Feed classification (Step C.3) ------------------------------------------------------------------

        static FeedKind FeedKindOf<T>() where T : class, IMyTerminalBlock
        {
            return Roles.FeedKindOf(MachineBlock<T>("", Inv(), null));
        }

        [Fact] public void ReactorFeedKindIsReactor() => Assert.Equal(FeedKind.Reactor, FeedKindOf<IMyReactor>());
        [Fact] public void GasGeneratorFeedKindIsGasGen() => Assert.Equal(FeedKind.GasGen, FeedKindOf<IMyGasGenerator>());
        [Fact] public void RefineryFeedKindIsRefinery() => Assert.Equal(FeedKind.Refinery, FeedKindOf<IMyRefinery>());
        [Fact] public void AssemblerFeedKindIsNone() => Assert.Equal(FeedKind.None, FeedKindOf<IMyAssembler>()); // fed by Craft (Step D), not here
        [Fact] public void CollectorFeedKindIsNone() => Assert.Equal(FeedKind.None, FeedKindOf<IMyCollector>());

        [Fact]
        public void PlainCargoFeedKindIsNone()
        {
            Assert.Equal(FeedKind.None, Roles.FeedKindOf(Block("Cargo", true)));
        }

        // --- Conveyor hold (Step C.5) ------------------------------------------------------------------------

        [Fact]
        public void DisableConveyorSystemTurnsOffAReactorThatIsOn()
        {
            var r = new Mock<IMyReactor>();
            r.SetupGet(x => x.UseConveyorSystem).Returns(true);
            Roles.DisableConveyorSystem(r.Object);
            r.VerifySet(x => x.UseConveyorSystem = false, Times.Once());
        }

        [Fact]
        public void DisableConveyorSystemTurnsOffARefineryThatIsOn()
        {
            var r = new Mock<IMyRefinery>();
            r.SetupGet(x => x.UseConveyorSystem).Returns(true);
            Roles.DisableConveyorSystem(r.Object);
            r.VerifySet(x => x.UseConveyorSystem = false, Times.Once());
        }

        // Write-on-change: a terminal-property set is network-replicated, so a machine already off must not be
        // rewritten every scan (the redundant-MP-write churn the design forbids).
        [Fact]
        public void DisableConveyorSystemLeavesAnAlreadyOffMachineUnwritten()
        {
            var g = new Mock<IMyGasGenerator>();
            g.SetupGet(x => x.UseConveyorSystem).Returns(false);
            Roles.DisableConveyorSystem(g.Object);
            g.VerifySet(x => x.UseConveyorSystem = It.IsAny<bool>(), Times.Never());
        }

        // An assembler is a production block with a conveyor too, but it is fed by Craft (Step D), not here, so
        // this pass must not seize it — even when it is on.
        [Fact]
        public void DisableConveyorSystemLeavesAnAssemblerUntouched()
        {
            var a = new Mock<IMyAssembler>();
            a.SetupGet(x => x.UseConveyorSystem).Returns(true);
            Roles.DisableConveyorSystem(a.Object);
            a.VerifySet(x => x.UseConveyorSystem = It.IsAny<bool>(), Times.Never());
        }

        static IMyTerminalBlock Typed<T>(string name) where T : class, IMyTerminalBlock
        {
            var mock = new Mock<T>();
            mock.SetupGet(b => b.CustomName).Returns(name);
            mock.SetupGet(b => b.HasInventory).Returns(true);
            mock.Setup(b => b.GetInventory(It.IsAny<int>())).Returns(new Mock<IMyInventory>().Object);
            mock.SetupGet(b => b.CubeGrid).Returns(GridOn(true));
            return mock.Object;
        }

        // A gun pulls its own ammo, so draining it would only ping-pong ammo through the conveyor.
        [Fact]
        public void WeaponsAreNeverManagedEvenWhenTagged()
        {
            var scan = Classify(Typed<IMyLargeTurretBase>("Turret [SHEPHERD:Ammo]"),
                                Typed<IMyUserControllableGun>("Gatling"),
                                Block("Cargo", true));
            Assert.Single(scan.Managed);
            Assert.Equal("Cargo", scan.Managed[0].Block.CustomName);
            Assert.Empty(scan.Destinations);
        }

        // A parachute hatch pulls its own canvas, so draining it would only ping-pong canvas through the conveyor.
        [Fact]
        public void ParachuteHatchesAreNeverManagedEvenWhenTagged()
        {
            var scan = Classify(Typed<IMyParachute>("Chute [SHEPHERD:Components]"), Block("Cargo", true));
            Assert.Single(scan.Managed);
            Assert.Equal("Cargo", scan.Managed[0].Block.CustomName);
            Assert.Empty(scan.Destinations);
        }

        // A welder pulls the components it welds with, so draining it would only ping-pong them through the conveyor.
        [Fact]
        public void WeldersAreNeverManagedEvenWhenTagged()
        {
            var scan = Classify(Typed<IMyShipWelder>("Welder [SHEPHERD:Components]"), Block("Cargo", true));
            Assert.Single(scan.Managed);
            Assert.Equal("Cargo", scan.Managed[0].Block.CustomName);
            Assert.Empty(scan.Destinations);
        }
    }
}
