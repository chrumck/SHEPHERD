using System;
using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class RefineTests
    {
        const string IronOre = "Ore/Iron";
        const string ScrapOre = "Ore/Scrap";
        const string StoneOre = "Ore/Stone";
        const string ModdedOre = "Ore/Nexalite";
        const string Iron = "Ingot/Iron";
        const string Silicon = "Ingot/Silicon";
        const string Nickel = "Ingot/Nickel";

        sealed class Feed : IRefineryFeed
        {
            static long nextId;
            readonly HashSet<string> takes;    // null = takes every ore

            public Feed(string name, params string[] oreKeys)
            {
                Name = name;
                Id = ++nextId;
                takes = oreKeys.Length == 0 ? null : new HashSet<string>(oreKeys);
            }

            public FeedRequest Request { get; private set; }
            public long Id { get; private set; }
            public string Name { get; private set; }
            public bool IsConstrained => Request != null;
            public bool CanTake(string oreKey) => takes == null || takes.Contains(oreKey);
            public void Constrain(FeedRequest request) => Request = request;
        }

        sealed class Rig
        {
            public readonly ProductionLedger Ledger = new ProductionLedger();
            public readonly ProductionFacts Facts = new ProductionFacts();
            public readonly RefinePlanner Planner = new RefinePlanner();
            public readonly List<IRefineryFeed> Feeds = new List<IRefineryFeed>();
            public Func<string, bool> HasRoom = key => true;

            public Rig()
            {
                Ledger.Begin();
            }

            public Rig Quota(string key, string quota)
            {
                var pool = new ReservePool("P");
                IngameScript.Quota q;
                Assert.True(IngameScript.Quota.TryParse(quota, out q));
                pool.SetQuota(key, q);
                Ledger.AddQuotas(pool);
                return this;
            }

            public Rig Feed(Feed feed)
            {
                Feeds.Add(feed);
                return this;
            }

            public void Plan() => Planner.Plan(Ledger, Facts, Feeds, HasRoom);
        }

        [Fact]
        public void IngotDeficitRequestsKnownOreOnTheFirstFeed()
        {
            var feed = new Feed("R1");
            var rig = new Rig().Quota(Iron, "100MA").Feed(feed);
            rig.Ledger.AddStock(Iron, 30);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Plan();
            var request = Assert.Single(rig.Planner.Requests);
            Assert.Same(request, feed.Request);
            Assert.Equal(IronOre, request.Ore);
            Assert.Equal(Iron, request.Ingot);
            Assert.Equal(Math.Ceiling(70 / 0.7), request.Remaining);
        }

        [Fact]
        public void NonAutomaticAndLimitQuotasRequestNothing()
        {
            var feed = new Feed("R1");
            var rig = new Rig().Quota(Iron, "100").Quota(Silicon, "100LA").Feed(feed);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
            Assert.Null(feed.Request);
        }

        [Fact]
        public void ExpectedIncomingOutputSuppressesTheRequest()
        {
            var feed = new Feed("R1");
            var rig = new Rig().Quota(Iron, "100A").Feed(feed);
            rig.Ledger.AddStock(Iron, 40);
            rig.Ledger.AddIncoming(Iron, 60);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
        }

        [Fact]
        public void SubUnitDeficitIsAtTarget()
        {
            var rig = new Rig().Quota(Iron, "100A").Feed(new Feed("R1"));
            rig.Ledger.AddStock(Iron, 99.5);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
            Assert.Empty(rig.Planner.Blocked);
        }

        [Fact]
        public void RequestAmountClampsToAvailableOre()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(IronOre, 50);
            rig.Plan();
            var request = Assert.Single(rig.Planner.Requests);
            Assert.Equal(50, request.Remaining);
        }

        [Fact]
        public void AvailabilityExcludesProtectedAndRetainedOre()
        {
            var rig = new Rig().Quota(Iron, "100MA").Quota(IronOre, "30");
            rig.Feed(new Feed("R1"));
            rig.Ledger.AddStock(IronOre, 100);
            rig.Ledger.AddProtected(IronOre, 50);
            rig.Plan();
            var request = Assert.Single(rig.Planner.Requests);
            Assert.Equal(20, request.Remaining);
        }

        [Fact]
        public void HigherScoringOreWinsWhenStockBinds()
        {
            var rig = new Rig().Quota(Iron, "1000MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(IronOre, 10);   // 10 * 0.7 = 7 ingots possible
            rig.Ledger.AddStock(ScrapOre, 10);  // 10 * 0.8 = 8 ingots possible
            rig.Plan();
            var request = Assert.Single(rig.Planner.Requests);
            Assert.Equal(ScrapOre, request.Ore);
        }

        [Fact]
        public void ScoreTiesBreakByOrdinalKeyOrder()
        {
            var rig = new Rig().Quota(Iron, "10MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(ScrapOre, 1000);   // both fully cover the deficit -> tie
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Plan();
            var request = Assert.Single(rig.Planner.Requests);
            Assert.Equal(IronOre, request.Ore);
        }

        [Fact]
        public void MultiOutputOreScoresAcrossEveryLiveDeficit()
        {
            var rig = new Rig().Quota(Silicon, "10MA").Quota(Nickel, "10MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(StoneOre, 100000);
            rig.Ledger.AddStock(IronOre, 100000);  // yields neither silicon nor nickel
            rig.Plan();
            var request = Assert.Single(rig.Planner.Requests);
            Assert.Equal(StoneOre, request.Ore);
            // enough stone for the binding output: nickel at 0.0024/unit
            Assert.Equal(Math.Ceiling(10 / 0.0024), request.Remaining);
        }

        [Fact]
        public void OneRequestCoveringSeveralDeficitsConsumesThemAll()
        {
            var rig = new Rig().Quota(Silicon, "10MA").Quota(Nickel, "10MA")
                .Feed(new Feed("R1")).Feed(new Feed("R2"));
            rig.Ledger.AddStock(StoneOre, 100000);
            rig.Plan();
            Assert.Single(rig.Planner.Requests);
            Assert.Empty(rig.Planner.Blocked);
        }

        [Fact]
        public void IncompatibleFeedIsSkippedForTheNext()
        {
            var iceOnly = new Feed("R1", "Ore/Ice");
            var open = new Feed("R2");
            var rig = new Rig().Quota(Iron, "100MA").Feed(iceOnly).Feed(open);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Plan();
            Assert.Single(rig.Planner.Requests);
            Assert.Null(iceOnly.Request);
            Assert.NotNull(open.Request);
        }

        [Fact]
        public void SecondDeficitTakesTheSecondFeed()
        {
            var f1 = new Feed("R1");
            var f2 = new Feed("R2");
            var rig = new Rig().Quota(Iron, "100MA").Quota(Silicon, "100MA").Feed(f1).Feed(f2);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Ledger.AddStock("Ore/Silicon", 1000);
            rig.Plan();
            Assert.Equal(2, rig.Planner.Requests.Count);
            Assert.NotNull(f1.Request);
            Assert.NotNull(f2.Request);
            Assert.NotEqual(f1.Request.Ore, f2.Request.Ore);
        }

        [Fact]
        public void ExhaustedFeedsBlockTheRemainingOre()
        {
            var f1 = new Feed("R1");
            var rig = new Rig().Quota(Iron, "100MA").Quota(Silicon, "100MA").Feed(f1);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Ledger.AddStock("Ore/Silicon", 1000);
            rig.Plan();
            Assert.Single(rig.Planner.Requests);
            var line = Assert.Single(rig.Planner.Blocked);
            Assert.EndsWith(": no refinery", line);
            Assert.Empty(rig.Planner.NeedYield);
        }

        [Fact]
        public void NoFeedsAtAllBlocksWithoutYieldRequests()
        {
            var rig = new Rig().Quota(Iron, "100MA");
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Ledger.AddStock(ModdedOre, 1000);
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
            Assert.Contains("refine " + IronOre + ": no refinery", rig.Planner.Blocked);
            Assert.Empty(rig.Planner.NeedYield);
        }

        [Fact]
        public void BlockedOutputStopsTheRequest()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(IronOre, 1000);
            rig.HasRoom = key => false;
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
            Assert.Contains("refine " + Iron + ": output has no room", rig.Planner.Blocked);
        }

        [Fact]
        public void UncoveredDeficitWithUnknownOreAsksForItsYield()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(ModdedOre, 1000);
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
            Assert.Equal(new[] { ModdedOre }, rig.Planner.NeedYield);
            Assert.Empty(rig.Planner.Blocked);
        }

        [Fact]
        public void YieldRequestRequiresTheConfiguredSample()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(ModdedOre, 109);
            rig.Planner.Plan(rig.Ledger, rig.Facts, rig.Feeds, rig.HasRoom, 110);
            Assert.Empty(rig.Planner.NeedYield);
            rig.Ledger.AddStock(ModdedOre, 1);
            rig.Planner.Plan(rig.Ledger, rig.Facts, rig.Feeds, rig.HasRoom, 110);
            Assert.Equal(new[] { ModdedOre }, rig.Planner.NeedYield);
        }

        [Fact]
        public void ShortSampleIsReportedAndReservedWhileADeficitStands()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(ModdedOre, 50);
            rig.Planner.Plan(rig.Ledger, rig.Facts, rig.Feeds, rig.HasRoom, 110);
            Assert.Equal(new[] { ModdedOre }, rig.Planner.UnderSampled);
            Assert.Contains("learn " + ModdedOre + ": need 110 available", rig.Planner.Blocked);
            Assert.Empty(rig.Planner.NeedYield);
        }

        [Fact]
        public void ShortSampleIsNotReservedWithoutADeficit()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(Iron, 100);
            rig.Ledger.AddStock(ModdedOre, 50);
            rig.Planner.Plan(rig.Ledger, rig.Facts, rig.Feeds, rig.HasRoom, 110);
            Assert.Empty(rig.Planner.UnderSampled);
            Assert.Empty(rig.Planner.Blocked);
        }

        [Fact]
        public void ShortSampleSuppressesTheNoOreLine()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(ModdedOre, 50);
            rig.Planner.Plan(rig.Ledger, rig.Facts, rig.Feeds, rig.HasRoom, 110);
            Assert.DoesNotContain("refine " + Iron + ": no ore", rig.Planner.Blocked);
        }

        [Fact]
        public void UnknownOreNeedsACompatibleFreeRefinery()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1", IronOre));
            rig.Ledger.AddStock(ModdedOre, 1000);
            rig.Plan();
            Assert.Empty(rig.Planner.NeedYield);
            Assert.Contains("refine " + ModdedOre + ": no refinery", rig.Planner.Blocked);
        }

        [Fact]
        public void ConstrainedRefineryIsNotAssignedAgain()
        {
            var feed = new Feed("R1");
            feed.Constrain(new FeedRequest(ModdedOre, Iron, 0));
            var rig = new Rig().Quota(Iron, "100MA").Feed(feed);
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
        }

        [Fact]
        public void SufficientKnownOreSuppressesYieldRequests()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock(IronOre, 1000);
            rig.Ledger.AddStock(ModdedOre, 1000);
            rig.Plan();
            Assert.Single(rig.Planner.Requests);
            Assert.Empty(rig.Planner.NeedYield);
        }

        [Fact]
        public void GaslikeOresAreKnownNotYieldCandidates()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Ledger.AddStock("Ore/Ice", 5000);
            rig.Ledger.AddStock("Ore/Organic", 5000);
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
            Assert.Empty(rig.Planner.NeedYield);                 // ice/organic yield nothing, KNOWN fact
            Assert.Contains("refine " + Iron + ": no ore", rig.Planner.Blocked);
        }

        [Fact]
        public void NoOreAtAllReportsTheDeficit()
        {
            var rig = new Rig().Quota(Iron, "100MA").Feed(new Feed("R1"));
            rig.Plan();
            Assert.Empty(rig.Planner.Requests);
            Assert.Contains("refine " + Iron + ": no ore", rig.Planner.Blocked);
            Assert.Empty(rig.Planner.NeedYield);
        }

        [Fact]
        public void RequestBooksItsClaims()
        {
            var request = new FeedRequest(IronOre, Iron, 10);
            Assert.True(request.Allows(IronOre));
            Assert.False(request.Allows(StoneOre));
            var claim = request.Claim(15);
            Assert.Equal(10, claim);
            Assert.Equal(0, request.Remaining);
            Assert.False(request.Allows(IronOre));
            request.Complete(claim, 7);
            Assert.Equal(7, request.Delivered);
            Assert.Equal(3, request.Remaining);
            claim = request.Claim(3);
            request.Complete(claim, 0);
            Assert.Equal(3, request.Remaining);
            Assert.Equal(7, request.Delivered);
        }

        [Fact]
        public void LedgerAvailableFloorsAtZero()
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddStock(IronOre, 10);
            ledger.AddProtected(IronOre, 30);
            Assert.Equal(0, ledger.Available(IronOre));
            Assert.Equal(0, ledger.Available("Ore/Missing"));
        }

    }
}
