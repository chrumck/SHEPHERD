using System.Collections.Generic;
using IngameScript;
using VRage;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    public class RoutingTests
    {
        sealed class FakeAcceptor : ICategoryAcceptor
        {
            readonly string name;
            readonly List<string> cats = new List<string>();
            readonly bool catchAll;

            public FakeAcceptor(string name, bool catchAll, params string[] categories)
            {
                this.name = name;
                this.catchAll = catchAll;
                if (categories != null) cats.AddRange(categories);
            }

            public IEnumerable<string> AcceptedCategories => cats;
            public bool IsCatchAll => catchAll;
            public int Priority { get; set; }
            public bool IsDemand { get; set; }
            public string Name => name;
            public IMyInventory Inventory => null;
            public bool Accepts(MyItemType type) => true;
            public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
            public void CompleteTransfer(double amount, bool moved) { }
            public override string ToString() => name;
        }

        static RoutingIndex Index(params ICategoryAcceptor[] acceptors) => new RoutingIndex(acceptors);

        [Fact]
        public void SingleAcceptor()
        {
            var a = new FakeAcceptor("A", false, Categories.Ores);
            Assert.Equal(new[] { a }, Index(a).Route(Categories.Ores));
        }

        [Fact]
        public void MultipleAcceptorsKeepScanOrder()
        {
            var a = new FakeAcceptor("A", false, Categories.Ores);
            var b = new FakeAcceptor("B", false, Categories.Ores);

            Assert.Equal(new[] { a, b }, Index(a, b).Route(Categories.Ores));
            Assert.Equal(new[] { b, a }, Index(b, a).Route(Categories.Ores));
        }

        // A learnCategories override to a custom token routes the stack to a [SHEPHERD:token] container: the
        // category the sort routes by (Category.For with the override) keys the index the container registered under.
        [Fact]
        public void OverriddenCustomTokenRoutesToTaggedContainer()
        {
            var overrides = new CategoryOverrides();
            overrides.Set("Ore/Stone", "Explosives");
            var box = new FakeAcceptor("box", false, "Explosives");

            var category = Category.ForKey(Category.Key("MyObjectBuilder_Ore", "Stone"), overrides);
            Assert.Equal(new[] { box }, Index(box).Route(category));
        }

        [Fact]
        public void ExplicitBeatsCatchAll()
        {
            var explicitOre = new FakeAcceptor("E", false, Categories.Ores);
            var overflow = new FakeAcceptor("C", true);

            // Catch-all listed first in scan order still ranks last.
            Assert.Equal(new[] { explicitOre, overflow }, Index(overflow, explicitOre).Route(Categories.Ores));
        }

        [Fact]
        public void CategoryWithNoExplicitFallsToCatchAllOnly()
        {
            var overflow = new FakeAcceptor("C", true);
            Assert.Equal(new[] { overflow }, Index(overflow).Route(Categories.Ingots));
        }

        [Fact]
        public void NeitherExplicitNorCatchAllIsEmpty()
        {
            var oreOnly = new FakeAcceptor("A", false, Categories.Ores);
            Assert.Empty(Index(oreOnly).Route(Categories.Ammo));
        }

        [Fact]
        public void EmptyIndexRoutesToNothing()
        {
            Assert.Empty(Index().Route(Categories.Ores));
        }

        [Fact]
        public void CatchAllsKeepScanOrder()
        {
            var c1 = new FakeAcceptor("C1", true);
            var c2 = new FakeAcceptor("C2", true);
            Assert.Equal(new[] { c1, c2 }, Index(c1, c2).Route(Categories.Ores));
        }

        [Fact]
        public void ExplicitThenAllCatchAlls()
        {
            var e = new FakeAcceptor("E", false, Categories.Ores);
            var c1 = new FakeAcceptor("C1", true);
            var c2 = new FakeAcceptor("C2", true);
            Assert.Equal(new[] { e, c1, c2 }, Index(e, c1, c2).Route(Categories.Ores));
        }

        [Fact]
        public void AcceptorThatIsBothExplicitAndCatchAllNotDuplicated()
        {
            // [SHEPHERD:Ores,*] — explicit acceptor of Ores AND overflow for everything else.
            var x = new FakeAcceptor("X", true, Categories.Ores);

            Assert.Equal(new[] { x }, Index(x).Route(Categories.Ores));     // once, not twice
            Assert.Equal(new[] { x }, Index(x).Route(Categories.Ingots));   // as catch-all
        }

        [Fact]
        public void NullCategoryRoutesToCatchAll()
        {
            var overflow = new FakeAcceptor("C", true);
            Assert.Equal(new[] { overflow }, Index(overflow).Route(null));
            Assert.Empty(Index(new FakeAcceptor("A", false, Categories.Ores)).Route(null));
        }

        [Fact]
        public void NullAcceptorsInInputAreSkipped()
        {
            var a = new FakeAcceptor("A", false, Categories.Ores);
            var idx = new RoutingIndex(new ICategoryAcceptor[] { null, a, null });
            Assert.Equal(new[] { a }, idx.Route(Categories.Ores));
        }

        [Fact]
        public void CatchAllWithNullCategoriesCollectionIsSafe()
        {
            var idx = new RoutingIndex(new ICategoryAcceptor[] { new NullCatsCatchAll() });
            Assert.Single(idx.Route(Categories.Ores));
        }

        sealed class NullCatsCatchAll : ICategoryAcceptor
        {
            public IEnumerable<string> AcceptedCategories => null;
            public bool IsCatchAll => true;
            public int Priority => 0;
            public bool IsDemand => false;
            public string Name => "nullcats";
            public IMyInventory Inventory => null;
            public bool Accepts(MyItemType type) => true;
            public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
            public void CompleteTransfer(double amount, bool moved) { }
        }

        [Fact]
        public void EmptyCategoryTokenIsNotIndexed()
        {
            var a = new FakeAcceptor("A", false, "", Categories.Ores);
            Assert.Empty(Index(a).Route(""));
            Assert.Equal(new[] { a }, Index(a).Route(Categories.Ores));
        }

        [Fact]
        public void HigherPriorityExplicitFillsFirst()
        {
            var low = new FakeAcceptor("low", false, Categories.Ores) { Priority = 0 };
            var high = new FakeAcceptor("high", false, Categories.Ores) { Priority = 5 };

            // Scan order (low, high) is overridden by priority; the result is independent of scan order.
            Assert.Equal(new[] { high, low }, Index(low, high).Route(Categories.Ores));
            Assert.Equal(new[] { high, low }, Index(high, low).Route(Categories.Ores));
        }

        [Fact]
        public void EqualPriorityKeepsScanOrder()
        {
            var a = new FakeAcceptor("A", false, Categories.Ores) { Priority = 3 };
            var b = new FakeAcceptor("B", false, Categories.Ores) { Priority = 3 };
            Assert.Equal(new[] { a, b }, Index(a, b).Route(Categories.Ores));
            Assert.Equal(new[] { b, a }, Index(b, a).Route(Categories.Ores));
        }

        [Fact]
        public void NegativePriorityFillsLast()
        {
            var normal = new FakeAcceptor("normal", false, Categories.Ores) { Priority = 0 };
            var last = new FakeAcceptor("last", false, Categories.Ores) { Priority = -1 };
            Assert.Equal(new[] { normal, last }, Index(last, normal).Route(Categories.Ores));
        }

        [Fact]
        public void HighPriorityCatchAllStillRanksBelowExplicit()
        {
            // The explicit tier is unconditionally above the catch-all tier — priority only orders within a tier.
            var explicitOre = new FakeAcceptor("E", false, Categories.Ores) { Priority = 0 };
            var overflow = new FakeAcceptor("C", true) { Priority = 99 };
            Assert.Equal(new[] { explicitOre, overflow }, Index(overflow, explicitOre).Route(Categories.Ores));
        }

        [Fact]
        public void CatchAllTierOrdersByPriority()
        {
            var lowCatch = new FakeAcceptor("lc", true) { Priority = 0 };
            var highCatch = new FakeAcceptor("hc", true) { Priority = 2 };
            Assert.Equal(new[] { highCatch, lowCatch }, Index(lowCatch, highCatch).Route(Categories.Ingots));
        }

        [Fact]
        public void DemandAcceptorRoutesAheadOfExplicitAndCatchAll()
        {
            // C.3: a demand target (a hungry machine feed) ranks above every container, regardless of scan order.
            var feed = new FakeAcceptor("feed", false, Categories.Ores) { IsDemand = true };
            var box = new FakeAcceptor("box", false, Categories.Ores);
            var overflow = new FakeAcceptor("C", true);
            Assert.Equal(new[] { feed, box, overflow }, Index(box, overflow, feed).Route(Categories.Ores));
        }

        [Fact]
        public void OverriddenItemBorrowsBuiltInDemandOnly()
        {
            var gen = new FakeAcceptor("gen", false, Categories.Ores) { IsDemand = true };
            var oreBox = new FakeAcceptor("ore", false, Categories.Ores);
            var iceBox = new FakeAcceptor("ice", false, "Ice");
            Assert.Equal(new[] { gen, iceBox }, Index(oreBox, iceBox, gen).Route("Ice", Categories.Ores));
        }

        [Fact]
        public void BorrowedDemandJoinsTheTierOnceInPriorityOrder()
        {
            var both = new FakeAcceptor("both", false, Categories.Ores, "Ice") { IsDemand = true };
            var high = new FakeAcceptor("high", false, Categories.Ores) { IsDemand = true, Priority = 3 };
            Assert.Equal(new[] { high, both }, Index(both, high).Route("Ice", Categories.Ores));
        }

        [Fact]
        public void NoBorrowingWithoutAnOverride()
        {
            var gen = new FakeAcceptor("gen", false, Categories.Ores) { IsDemand = true };
            Assert.Equal(new[] { gen }, Index(gen).Route(Categories.Ores, Categories.Ores));
            Assert.Empty(Index(gen).Route(Categories.Ingots, null));
        }

        [Fact]
        public void InOverflowIsACatchAllThatDoesNotNameTheCategory()
        {
            var plain = new FakeAcceptor("C", true);
            var naming = new FakeAcceptor("CO", true, Categories.Ores);
            var box = new FakeAcceptor("O", false, Categories.Ores);
            var idx = Index(plain, naming, box);
            Assert.True(idx.InOverflow(plain, Categories.Ores));
            Assert.False(idx.InOverflow(naming, Categories.Ores));
            Assert.True(idx.InOverflow(naming, Categories.Ingots));
            Assert.False(idx.InOverflow(box, Categories.Ores));
            Assert.True(idx.InOverflow(plain, null));
        }

        [Fact]
        public void HasHomeIgnoresBorrowedDemand()
        {
            var gen = new FakeAcceptor("gen", false, Categories.Ores) { IsDemand = true };
            Assert.False(Index(gen).HasHome("Ice"));
            Assert.True(Index(gen, new FakeAcceptor("ice", false, "Ice")).HasHome("Ice"));
            Assert.True(Index(gen, new FakeAcceptor("C", true)).HasHome("Ice"));
            Assert.False(Index(gen).HasHome(null));
        }

        [Fact]
        public void ARouteIsBuiltOncePerCategoryAndBuiltIn()
        {
            var feed = new FakeAcceptor("feed", false, Categories.Ores) { IsDemand = true };
            var box = new FakeAcceptor("box", false, "Ice");
            var idx = Index(feed, box);
            Assert.Same(idx.Route(Categories.Ores, Categories.Ores), idx.Route(Categories.Ores, Categories.Ores));
            Assert.Equal(new[] { box }, idx.Route("Ice", "Ice"));
            Assert.Equal(new[] { feed, box }, idx.Route("Ice", Categories.Ores));
            Assert.Same(idx.Route(null), idx.Route(""));
        }

        // A constrained refinery feed's priority rises on the plan tick, after routes may have been built.
        [Fact]
        public void ForgetPicksUpAPriorityChange()
        {
            var a = new FakeAcceptor("a", false, Categories.Ores) { IsDemand = true };
            var b = new FakeAcceptor("b", false, Categories.Ores) { IsDemand = true };
            var idx = Index(a, b);
            Assert.Equal(new[] { a, b }, idx.Route(Categories.Ores));
            b.Priority = 1;
            Assert.Equal(new[] { a, b }, idx.Route(Categories.Ores));
            idx.Forget();
            Assert.Equal(new[] { b, a }, idx.Route(Categories.Ores));
        }

        [Fact]
        public void DemandTierOrdersByPriorityThenScan()
        {
            var lowFeed = new FakeAcceptor("lf", false, Categories.Ores) { IsDemand = true, Priority = 0 };
            var highFeed = new FakeAcceptor("hf", false, Categories.Ores) { IsDemand = true, Priority = 5 };
            Assert.Equal(new[] { highFeed, lowFeed }, Index(lowFeed, highFeed).Route(Categories.Ores));
        }
    }
}
