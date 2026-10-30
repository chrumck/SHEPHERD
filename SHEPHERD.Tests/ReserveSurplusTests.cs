using System;
using System.Collections.Generic;
using IngameScript;
using Moq;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    // Where a reserve container's stock may go, read off the live InventorySource the sort and bottle sweep walk.
    public class ReserveSurplusTests
    {
        const string Plate = "Component/SteelPlate";
        const string Bottle = "OxygenContainerObject/OxygenBottle";
        static readonly MyItemType PlateType = MyItemType.MakeComponent("SteelPlate");
        static readonly MyItemType BottleType = new MyItemType(typeof(Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_OxygenContainerObject), VRage.Utils.MyStringHash.GetOrCompute("OxygenBottle"));

        static Mock<IMyInventory> Holding(MyItemType type, double amount)
        {
            var inv = new Mock<IMyInventory>();
            var item = new MyInventoryItem(type, 1, (MyFixedPoint)amount);
            inv.Setup(i => i.GetItems(It.IsAny<List<MyInventoryItem>>(), It.IsAny<Func<MyInventoryItem, bool>>()))
                .Callback<List<MyInventoryItem>, Func<MyInventoryItem, bool>>((list, keep) => list.Add(item));
            inv.Setup(i => i.CanItemsBeAdded(It.IsAny<MyFixedPoint>(), It.IsAny<MyItemType>())).Returns(true);
            return inv;
        }

        static ReservePool Pool(string name, string key, string quota, double count)
        {
            var pool = new ReservePool(name);
            Quota q;
            Assert.True(Quota.TryParse(quota, out q));
            pool.SetQuota(key, q);
            pool.SetCount(key, count);
            return pool;
        }

        static InventorySource Reserve(IMyInventory inv, ReservePool pool)
        {
            var src = new InventorySource(inv, new List<MyInventoryItem>(), DrainMode.KeepQuota, pool, null);
            src.Refresh();
            return src;
        }

        static InventorySource ReserveHolding(ReservePool pool, double amount) => Reserve(Holding(PlateType, amount).Object, pool);

        static ICategoryAcceptor Storage()
        {
            var target = new Mock<ICategoryAcceptor>();
            target.Setup(t => t.Accepts(It.IsAny<MyItemType>())).Returns(true);
            return target.Object;
        }

        static RefillStation TankWithGas()
        {
            var tank = new Mock<IMyGasTank>();
            tank.SetupGet(t => t.FilledRatio).Returns(0.5);
            return new RefillStation(tank.Object, Holding(BottleType, 0).Object, new List<MyInventoryItem>());
        }

        [Fact]
        public void AMinSurplusTopsUpAShortPool()
        {
            var src = ReserveHolding(Pool("B", Plate, "100M", 140), 140);
            var shortPool = new ReserveIntake(Holding(PlateType, 0).Object, Pool("A", Plate, "100", 60), "A");
            Assert.True(src.ShouldDrain(0));
            Assert.True(src.CanPlaceAt(0, shortPool));
        }

        [Fact]
        public void AMinSurplusNeverGoesBackToStorage()
        {
            var src = ReserveHolding(Pool("B", Plate, "100M", 140), 140);
            Assert.False(src.CanPlaceAt(0, Storage()));
        }

        [Fact]
        public void AMinPoolAtTargetKeepsItsStock()
        {
            var src = ReserveHolding(Pool("B", Plate, "100M", 100), 100);
            Assert.False(src.ShouldDrain(0));
        }

        [Fact]
        public void AnExactSurplusStillTrimsToStorage()
        {
            var src = ReserveHolding(Pool("B", Plate, "100", 140), 140);
            Assert.True(src.ShouldDrain(0));
            Assert.True(src.CanPlaceAt(0, Storage()));
        }

        [Fact]
        public void AFullPoolTakesNoSurplus()
        {
            var src = ReserveHolding(Pool("B", Plate, "100M", 140), 140);
            var fullPool = new ReserveIntake(Holding(PlateType, 100).Object, Pool("A", Plate, "100", 100), "A");
            Assert.False(src.CanPlaceAt(0, fullPool));
        }

        [Theory]
        [InlineData("4")]
        [InlineData("4M")]
        [InlineData("4L")]
        public void AReserveAtTargetLendsItsWholeBottleStackToARefillTank(string quota)
        {
            var inv = Holding(BottleType, 4);
            MyFixedPoint? sent = -1;
            inv.Setup(i => i.TransferItemTo(It.IsAny<IMyInventory>(), It.IsAny<MyInventoryItem>(), It.IsAny<MyFixedPoint?>()))
                .Callback<IMyInventory, MyInventoryItem, MyFixedPoint?>((dst, item, amount) => sent = amount).Returns(true);
            var pool = Pool("B", Bottle, quota, 4);
            var src = Reserve(inv.Object, pool);
            var tank = TankWithGas();

            Assert.True(src.CanPlaceAt(0, tank));
            src.Prepare(0, tank);
            Assert.Equal(CommitResult.Moved, src.Commit(false));
            Assert.Null(sent);                       // the whole stack, not the pool's excess of 0
            Assert.Equal(4, pool.Count(Bottle));     // the bottles come straight back, so the pool isn't charged
        }
    }
}
