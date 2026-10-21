using System.Collections.Generic;
using System.Threading;
using IngameScript;
using Moq;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    public class DockTests
    {
        const long Me = 7;
        static int nextGrid;

        // A grid on construct `construct`. IsSameConstructAs compares construct numbers, carried in the EntityId's
        // thousands so every grid stays distinct.
        static IMyCubeGrid Grid(int construct)
        {
            var g = new Mock<IMyCubeGrid>();
            g.SetupGet(x => x.EntityId).Returns(construct * 1000L + Interlocked.Increment(ref nextGrid) % 1000);
            g.Setup(x => x.IsSameConstructAs(It.IsAny<IMyCubeGrid>()))
             .Returns((IMyCubeGrid other) => other != null && other.EntityId / 1000 == construct);
            return g.Object;
        }

        static Mock<IMyShipConnector> Connector(IMyCubeGrid grid, string name = "Connector")
        {
            var c = new Mock<IMyShipConnector>();
            c.SetupGet(x => x.CubeGrid).Returns(grid);
            c.SetupGet(x => x.CustomName).Returns(name);
            return c;
        }

        static void Link(Mock<IMyShipConnector> a, Mock<IMyShipConnector> b)
        {
            a.SetupGet(x => x.Status).Returns(MyShipConnectorStatus.Connected);
            b.SetupGet(x => x.Status).Returns(MyShipConnectorStatus.Connected);
            a.SetupGet(x => x.OtherConnector).Returns(b.Object);
            b.SetupGet(x => x.OtherConnector).Returns(a.Object);
        }

        // Both ends of a docking, as GridTerminalSystem lists them: each connector of the pair.
        static List<Mock<IMyShipConnector>> Dock(IMyCubeGrid a, IMyCubeGrid b, string nameA = "Connector", string nameB = "Connector")
        {
            var ca = Connector(a, nameA);
            var cb = Connector(b, nameB);
            Link(ca, cb);
            return new List<Mock<IMyShipConnector>> { ca, cb };
        }

        static ManagedScope ScopeOf(IMyCubeGrid home, IEnumerable<Mock<IMyShipConnector>> connectors,
            bool factionShare = false, bool dockedProduction = false, bool dockedFuel = false)
        {
            var list = new List<IMyShipConnector>();
            foreach (var c in connectors) list.Add(c.Object);
            var scope = new ManagedScope();
            scope.Build(list, home, Me, factionShare, dockedProduction, dockedFuel);
            return scope;
        }

        static ManagedScope Scope(IMyCubeGrid home, params List<Mock<IMyShipConnector>>[] dockings)
        {
            var all = new List<Mock<IMyShipConnector>>();
            foreach (var d in dockings) all.AddRange(d);
            return ScopeOf(home, all);
        }

        static Mock<T> On<T>(IMyCubeGrid grid, string name, long owner = 0, bool hasInventory = true) where T : class, IMyTerminalBlock
        {
            var b = new Mock<T>();
            b.SetupGet(x => x.CustomName).Returns(name);
            b.SetupGet(x => x.HasInventory).Returns(hasInventory);
            b.Setup(x => x.GetInventory(It.IsAny<int>())).Returns(hasInventory ? new Mock<IMyInventory>().Object : null);
            b.SetupGet(x => x.CubeGrid).Returns(grid);
            b.SetupGet(x => x.OwnerId).Returns(owner);
            return b;
        }

        static IMyTerminalBlock Cargo(IMyCubeGrid grid, string name, long owner = 0) => On<IMyCargoContainer>(grid, name, owner).Object;

        // --- The construct graph ---------------------------------------------------------------------------------

        [Fact]
        public void NoConnectorsMeansHomeAlone()
        {
            var home = Grid(1);
            var scope = Scope(home);
            Assert.Equal(1, scope.Count);
            Assert.Equal(0, scope.IndexOf(home));
            Assert.Equal(0, scope.IndexOf(Grid(1)));   // a rotor subgrid of the same construct
            Assert.Equal(-1, scope.IndexOf(Grid(2)));
        }

        [Fact]
        public void NothingIsInScopeBeforeTheFirstBuild() => Assert.Equal(-1, new ManagedScope().IndexOf(Grid(1)));

        [Fact]
        public void DockedShipJoinsTheScope()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scope = Scope(home, Dock(home, ship));
            Assert.Equal(2, scope.Count);
            Assert.Equal(1, scope.IndexOf(ship));
        }

        [Fact]
        public void AConnectorThatIsNotLockedAddsNothing()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var a = Connector(home);
            var b = Connector(ship);
            a.SetupGet(x => x.Status).Returns(MyShipConnectorStatus.Connectable);
            a.SetupGet(x => x.OtherConnector).Returns(b.Object);
            Assert.Equal(-1, ScopeOf(home, new[] { a, b }).IndexOf(ship));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ASplitTagOnEitherEndCutsTheDock(bool onHomeSide)
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scope = Scope(home, Dock(home, ship, onHomeSide ? "Port [SHEPHERD-Split]" : "Port", onHomeSide ? "Port" : "Port [shepherd-split]"));
            Assert.Equal(1, scope.Count);
            Assert.Equal(-1, scope.IndexOf(ship));
        }

        [Fact]
        public void ChainedDockingIsWalkedWhateverTheConnectorOrder()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var shuttle = Grid(3);
            var scope = Scope(home, Dock(ship, shuttle), Dock(home, ship));   // the far dock listed first
            Assert.Equal(3, scope.Count);
            Assert.Equal(1, scope.IndexOf(ship));
            Assert.Equal(2, scope.IndexOf(shuttle));
        }

        [Fact]
        public void ASplitMidChainCutsOnlyTheFarSide()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var shuttle = Grid(3);
            var scope = Scope(home, Dock(home, ship), Dock(ship, shuttle, "Aft [SHEPHERD-Split]"));
            Assert.Equal(1, scope.IndexOf(ship));
            Assert.Equal(-1, scope.IndexOf(shuttle));
        }

        [Fact]
        public void AShipStillReachableThroughAnUncutConnectorStays()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scope = Scope(home, Dock(home, ship, "Port [SHEPHERD-Split]"), Dock(home, ship));
            Assert.Equal(1, scope.IndexOf(ship));
        }

        // --- Ownership -------------------------------------------------------------------------------------------

        static Mock<IMyCargoContainer> Owned(long owner, MyRelationsBetweenPlayerAndBlock relation)
        {
            var b = On<IMyCargoContainer>(Grid(1), "Box", owner);
            b.Setup(x => x.GetUserRelationToOwner(It.IsAny<long>(), It.IsAny<MyRelationsBetweenPlayerAndBlock>())).Returns(relation);
            return b;
        }

        [Fact]
        public void OwnsOurOwnAndUnownedBlocks()
        {
            var scope = Scope(Grid(1));
            Assert.True(scope.Owns(Owned(Me, MyRelationsBetweenPlayerAndBlock.Owner).Object));
            Assert.True(scope.Owns(Owned(0, MyRelationsBetweenPlayerAndBlock.NoOwnership).Object));
            Assert.False(scope.Owns(Owned(99, MyRelationsBetweenPlayerAndBlock.Neutral).Object));
        }

        [Fact]
        public void FactionSharedBlocksOnlyWithTheToggle()
        {
            var shared = Owned(99, MyRelationsBetweenPlayerAndBlock.FactionShare).Object;
            Assert.False(Scope(Grid(1)).Owns(shared));
            Assert.True(ScopeOf(Grid(1), new Mock<IMyShipConnector>[0], factionShare: true).Owns(shared));
        }

        [Fact]
        public void TheRelationIsNotAskedWhileFactionShareIsOff()
        {
            var b = Owned(99, MyRelationsBetweenPlayerAndBlock.FactionShare);
            Scope(Grid(1)).Owns(b.Object);
            b.Verify(x => x.GetUserRelationToOwner(It.IsAny<long>(), It.IsAny<MyRelationsBetweenPlayerAndBlock>()), Times.Never());
        }

        // --- What the classify does with a docked block ----------------------------------------------------------

        [Fact]
        public void DockedContainersDrainHomeAndTheirDestinationTagsAreInert()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new[]
            {
                Cargo(home, "Ores [SHEPHERD:Ores]"),
                Cargo(ship, "Ship Ores [SHEPHERD:Ores]"),
                Cargo(ship, "Ship Hold"),
            }, Scope(home, Dock(home, ship)));
            Assert.Single(scan.Destinations);
            Assert.Equal("Ores [SHEPHERD:Ores]", scan.Destinations[0].Name);
            Assert.Equal(3, scan.Managed.Count);
            Assert.False(scan.Managed[0].Docked);
            Assert.True(scan.Managed[1].Docked);
            Assert.True(scan.Managed[2].Docked);
            Assert.Equal(0, scan.Managed[0].Construct);
            Assert.Equal(1, scan.Managed[1].Construct);
        }

        [Fact]
        public void DockedReserveIsStillAReserve()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new[] { Cargo(ship, "Locker [SHEPHERD-RESERVE:Ship]") }, Scope(home, Dock(home, ship)));
            Assert.Single(scan.Reserves);
            Assert.Equal("Ship", scan.Reserves[0].Pool);
            Assert.True(scan.Managed[0].IsReserve);
            Assert.True(scan.Reserves[0].Docked);
        }

        [Fact]
        public void HomeReserveIsNotDocked()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new[] { Cargo(home, "Locker [SHEPHERD-RESERVE:Base]") }, Scope(home, Dock(home, ship)));
            Assert.False(Assert.Single(scan.Reserves).Docked);
        }

        [Fact]
        public void DockedFuelProductionAndTanksAreLeftAlone()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new IMyTerminalBlock[]
            {
                On<IMyReactor>(ship, "Reactor").Object,
                On<IMyGasGenerator>(ship, "O2/H2").Object,
                On<IMyRefinery>(ship, "Refinery").Object,
                On<IMyAssembler>(ship, "Assembler").Object,
                On<IMyGasTank>(ship, "Tank").Object,
                On<IMyShipDrill>(ship, "Drill").Object,
            }, Scope(home, Dock(home, ship)));
            Assert.Single(scan.Managed);
            Assert.Equal(Role.Collector, scan.Managed[0].Role);
            Assert.Empty(scan.Tanks);
        }

        [Fact]
        public void DockedProductionToggleManagesDockedProcessorsOnly()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new IMyTerminalBlock[]
            {
                On<IMyRefinery>(ship, "Refinery").Object,
                On<IMyAssembler>(ship, "Assembler").Object,
                On<IMyReactor>(ship, "Reactor").Object,
            }, ScopeOf(home, Dock(home, ship), dockedProduction: true));
            Assert.Equal(2, scan.Managed.Count);
            Assert.All(scan.Managed, m => Assert.Equal(Role.Processor, m.Role));
            Assert.All(scan.Managed, m => Assert.True(m.Docked));
        }

        [Fact]
        public void AnAssemblerCarriesItsTagOnHomeAndDockedGrids()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new IMyTerminalBlock[]
            {
                On<IMyAssembler>(home, "Assembler [SHEPHERD-Main]").Object,
                On<IMyAssembler>(home, "Helper [SHEPHERD-Coop]").Object,
                On<IMyAssembler>(home, "Mine").Object,
                On<IMyAssembler>(ship, "Ship [SHEPHERD-Main] [SHEPHERD-Coop]").Object,
            }, ScopeOf(home, Dock(home, ship), dockedProduction: true));
            Assert.Equal(new[] { AssemblerMark.Main, AssemblerMark.Coop, AssemblerMark.None, AssemblerMark.Main },
                scan.Managed.ConvertAll(m => m.Mark));
        }

        [Fact]
        public void DockedFuelToggleManagesDockedConsumersOnly()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new IMyTerminalBlock[]
            {
                On<IMyRefinery>(ship, "Refinery").Object,
                On<IMyReactor>(ship, "Reactor").Object,
                On<IMyGasGenerator>(ship, "O2/H2").Object,
            }, ScopeOf(home, Dock(home, ship), dockedFuel: true));
            Assert.Equal(2, scan.Managed.Count);
            Assert.All(scan.Managed, m => Assert.Equal(Role.Consumer, m.Role));
            Assert.All(scan.Managed, m => Assert.True(m.Docked));
        }

        [Fact]
        public void DockedStatusPanelsAreIgnoredButLevelsLcdsAreHonored()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var shipPanel = On<IMyTextPanel>(ship, "Ship [SHEPHERD]", hasInventory: false).Object;
            var homePanel = On<IMyTextPanel>(home, "Base [SHEPHERD]", hasInventory: false).Object;
            var scan = Scan.Classify(new[]
            {
                shipPanel,
                homePanel,
                On<IMyTextPanel>(ship, "Ship [SHEPHERD-LOG]", hasInventory: false).Object,
                On<IMyTextPanel>(ship, "Loadout [SHEPHERD-RESERVE:Ship]", hasInventory: false).Object,
            }, Scope(home, Dock(home, ship)));
            Assert.Same(homePanel, scan.StatusLcd);
            Assert.Null(scan.LogLcd);
            Assert.Single(scan.ReserveLcds);
            Assert.Equal("Ship", scan.ReserveLcds[0].Pool);
        }

        [Fact]
        public void ABlockSomeoneElseOwnsIsNeverTouched()
        {
            var home = Grid(1);
            var guest = Grid(2);
            var scan = Scan.Classify(new[]
            {
                Cargo(home, "Their Box [SHEPHERD:Ores]", 99),
                Cargo(guest, "Guest Hold", 99),
                On<IMyTextPanel>(home, "Their Panel [SHEPHERD]", 99, false).Object,
                Cargo(guest, "Unowned Hold"),
            }, Scope(home, Dock(home, guest)));
            Assert.Single(scan.Managed);
            Assert.Equal("Unowned Hold", scan.Managed[0].Block.CustomName);
            Assert.Empty(scan.Destinations);
            Assert.Null(scan.StatusLcd);
        }

        [Fact]
        public void NothingBehindASplitConnectorIsManaged()
        {
            var home = Grid(1);
            var ship = Grid(2);
            var scan = Scan.Classify(new[] { Cargo(ship, "Hold"), Cargo(ship, "Ores [SHEPHERD:Ores]") },
                Scope(home, Dock(home, ship, "Port [SHEPHERD-Split]")));
            Assert.Empty(scan.Managed);
        }
    }
}
