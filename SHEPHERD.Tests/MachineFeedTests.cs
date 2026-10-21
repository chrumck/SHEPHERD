using IngameScript;
using Moq;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    // The open-feed filter (withholding an in-demand unknown-yield ore from a refinery's greedy feed) gates
    // ROUTING only, through the key-typed RoutingAllows: a constrained feed answers to its request alone, an open
    // feed to its filter. CanTake (the planner's and the yield sampler's compatibility probe) bypasses the filter
    // entirely — that refusal-vs-compatibility split is what hands an unknown ore to the sampler instead of the
    // greedy feed. Volume/constraint paths end in GetItemInfo(), unavailable offline, so those stay Tier-2.
    public class MachineFeedTests
    {
        const string Penumbrite = "Ore/Penumbrite";
        const string Iron = "Ore/Iron";

        static MachineFeed Feed(System.Func<string, bool> filter) =>
            new MachineFeed(new Mock<IMyInventory>().Object, 1, "Refinery", "Ores", 1000.0, filter);

        [Fact]
        public void OpenFeedAsksItsFilter()
        {
            var feed = Feed(key => key == Iron);
            Assert.True(feed.RoutingAllows(Iron));
            Assert.False(feed.RoutingAllows(Penumbrite));
        }

        [Fact]
        public void NoFilterOpenFeedAllowsEverything()
        {
            var feed = Feed(null);
            Assert.True(feed.RoutingAllows(Penumbrite));
        }

        [Fact]
        public void ConstrainedFeedIgnoresTheFilterAndFollowsItsRequest()
        {
            var feed = Feed(key => false);
            feed.Constrain(new FeedRequest(Penumbrite, "Ingot/PrototechScrap", 110.0));
            Assert.True(feed.RoutingAllows(Penumbrite));
            Assert.False(feed.RoutingAllows(Iron));
        }

        static MachineFeed FeedHolding(double volume)
        {
            var inv = new Mock<IMyInventory>();
            inv.Setup(i => i.CurrentVolume).Returns((VRage.MyFixedPoint)(float)volume);
            return new MachineFeed(inv.Object, 1, "Refinery", "Ores", 1000.0, null);
        }

        [Fact]
        public void OpenFeedWaitsUntilBelowTheRefillMark()
        {
            Assert.False(FeedHolding(MachineFeed.RefillBelow * 1000.0).WantsFill);
            Assert.False(FeedHolding(900.0).WantsFill);
            Assert.True(FeedHolding(MachineFeed.RefillBelow * 1000.0 - 1.0).WantsFill);
        }

        [Fact]
        public void ConstrainedFeedFillsAboveTheRefillMark()
        {
            var feed = FeedHolding(900.0);
            feed.Constrain(new FeedRequest(Iron, "Ingot/Iron", 50.0));
            Assert.True(feed.WantsFill);
        }

        [Fact]
        public void ExhaustedRequestAllowsNothingMore()
        {
            var feed = Feed(null);
            var request = new FeedRequest(Penumbrite, "Ingot/PrototechScrap", 10.0);
            feed.Constrain(request);
            request.Claim(10.0);
            Assert.False(feed.RoutingAllows(Penumbrite));
        }
    }
}
