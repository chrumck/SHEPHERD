using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class ReserveTests
    {
        [Fact]
        public void ParsesExact()
        {
            Quota q;
            Assert.True(Quota.TryParse("100", out q));
            Assert.Equal(QuotaMode.Exact, q.Mode);
            Assert.Equal(100, q.Target);
        }

        [Fact]
        public void ParsesMinCaseInsensitive()
        {
            Quota q;
            Assert.True(Quota.TryParse("100M", out q));
            Assert.Equal(QuotaMode.Min, q.Mode);
            Assert.Equal(100, q.Target);
            Assert.True(Quota.TryParse("250m", out q));
            Assert.Equal(QuotaMode.Min, q.Mode);
            Assert.Equal(250, q.Target);
        }

        [Fact]
        public void ParsesLimitCaseInsensitive()
        {
            Quota q;
            Assert.True(Quota.TryParse("100L", out q));
            Assert.Equal(QuotaMode.Limit, q.Mode);
            Assert.Equal(100, q.Target);
            Assert.True(Quota.TryParse("50l", out q));
            Assert.Equal(QuotaMode.Limit, q.Mode);
        }

        [Fact]
        public void ParsesAllCaseInsensitive()
        {
            Quota q;
            Assert.True(Quota.TryParse("All", out q));
            Assert.Equal(QuotaMode.All, q.Mode);
            Assert.True(Quota.TryParse("all", out q));
            Assert.Equal(QuotaMode.All, q.Mode);
            Assert.True(Quota.TryParse("ALL", out q));
            Assert.Equal(QuotaMode.All, q.Mode);
        }

        [Fact]
        public void ParsesDecimalAndTrims()
        {
            Quota q;
            Assert.True(Quota.TryParse(" 1.5 ", out q));
            Assert.Equal(QuotaMode.Exact, q.Mode);
            Assert.Equal(1.5, q.Target);
            Assert.True(Quota.TryParse("2.5M", out q));
            Assert.Equal(2.5, q.Target);
        }

        [Fact]
        public void RejectsGarbage()
        {
            Quota q;
            Assert.False(Quota.TryParse(null, out q));
            Assert.False(Quota.TryParse("", out q));
            Assert.False(Quota.TryParse("  ", out q));
            Assert.False(Quota.TryParse("abc", out q));
            Assert.False(Quota.TryParse("M", out q));
            Assert.False(Quota.TryParse("L", out q));
            Assert.False(Quota.TryParse("10X", out q));
            Assert.False(Quota.TryParse("-5", out q));
        }

        [Fact]
        public void ParsesAutomaticProductionSuffix()
        {
            Quota q;
            Assert.True(Quota.TryParse("100A", out q));
            Assert.Equal(QuotaMode.Exact, q.Mode);
            Assert.Equal(100, q.Target);
            Assert.True(q.AutomaticProduction);
            Assert.True(Quota.TryParse("100MA", out q));
            Assert.Equal(QuotaMode.Min, q.Mode);
            Assert.True(q.AutomaticProduction);
            Assert.True(Quota.TryParse("100LA", out q));
            Assert.Equal(QuotaMode.Limit, q.Mode);
            Assert.True(q.AutomaticProduction);
            Assert.True(Quota.TryParse("250ma", out q));
            Assert.Equal(QuotaMode.Min, q.Mode);
            Assert.Equal(250, q.Target);
            Assert.True(q.AutomaticProduction);
        }

        [Fact]
        public void ExistingValuesStayNonAutomatic()
        {
            Quota q;
            Assert.True(Quota.TryParse("100", out q));
            Assert.False(q.AutomaticProduction);
            Assert.True(Quota.TryParse("100M", out q));
            Assert.False(q.AutomaticProduction);
            Assert.True(Quota.TryParse("100L", out q));
            Assert.False(q.AutomaticProduction);
            Assert.True(Quota.TryParse("All", out q));
            Assert.False(q.AutomaticProduction);
        }

        [Fact]
        public void RejectsBadAutomaticSuffixes()
        {
            Quota q;
            Assert.False(Quota.TryParse("AllA", out q));   // All has no finite production target
            Assert.False(Quota.TryParse("alla", out q));
            Assert.False(Quota.TryParse("100AM", out q));  // reordered
            Assert.False(Quota.TryParse("100AA", out q));  // repeated
            Assert.False(Quota.TryParse("100MM", out q));
            Assert.False(Quota.TryParse("100AB", out q));
            Assert.False(Quota.TryParse("A", out q));
            Assert.False(Quota.TryParse("MA", out q));
        }

        [Fact]
        public void FormatsCanonically()
        {
            Quota q;
            Quota.TryParse("100", out q);
            Assert.Equal("100", q.Format());
            Quota.TryParse("100m", out q);
            Assert.Equal("100M", q.Format());
            Quota.TryParse("100la", out q);
            Assert.Equal("100LA", q.Format());
            Quota.TryParse("100a", out q);
            Assert.Equal("100A", q.Format());
            Quota.TryParse("100ma", out q);
            Assert.Equal("100MA", q.Format());
            Quota.TryParse("all", out q);
            Assert.Equal("All", q.Format());
            Quota.TryParse("10000MA", out q);
            Assert.Equal("10kMA", q.Format());      // compacted like the have count beside it, so 10k/10kMA
        }

        [Fact]
        public void KeyStripsBuilderPrefix()
        {
            Assert.Equal("Ingot/Iron", Category.Key("MyObjectBuilder_Ingot", "Iron"));
            Assert.Equal("AmmoMagazine/NATO_5p56x45mm", Category.Key("AmmoMagazine", "NATO_5p56x45mm"));
        }

        [Fact]
        public void DeficitAddModesBelowTarget()
        {
            var pool = new ReservePool("Ammo");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Exact, 100));
            pool.SetQuota("Ingot/Gold", new Quota(QuotaMode.Min, 50));
            pool.SetCount("Ingot/Iron", 30);
            pool.SetCount("Ingot/Gold", 10);
            Assert.Equal(70, pool.Deficit("Ingot/Iron"));
            Assert.Equal(40, pool.Deficit("Ingot/Gold"));
        }

        [Fact]
        public void DeficitZeroAtOrAboveTarget()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Exact, 100));
            pool.SetQuota("Ingot/Gold", new Quota(QuotaMode.Limit, 50));
            pool.SetCount("Ingot/Iron", 100);
            pool.SetCount("Ingot/Gold", 60);
            Assert.Equal(0, pool.Deficit("Ingot/Iron"));
            Assert.Equal(0, pool.Deficit("Ingot/Gold"));
        }

        [Fact]
        public void LimitPullsUpToTargetLikeExact()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Gold", new Quota(QuotaMode.Limit, 50));
            pool.SetCount("Ingot/Gold", 10);
            Assert.Equal(40, pool.Deficit("Ingot/Gold"));
        }

        [Theory]
        [InlineData("Ingot/Iron")]
        [InlineData("MyObjectBuilder_Ingot/Iron")]
        public void QuotaKeyMatchesWithOrWithoutBuilderPrefix(string written)
        {
            var pool = new ReservePool("P");
            pool.SetQuota(written, new Quota(QuotaMode.Exact, 100));
            pool.SetCount(Category.Key("MyObjectBuilder_Ingot", "Iron"), 30);
            Assert.Equal(70, pool.Deficit("Ingot/Iron"));
        }

        [Fact]
        public void DeficitZeroForNoQuota()
        {
            var pool = new ReservePool("P");
            Assert.Equal(0, pool.Deficit("Ingot/Iron"));
        }

        [Fact]
        public void ExcessRemoveModesAboveTarget()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Exact, 100));
            pool.SetQuota("Ingot/Gold", new Quota(QuotaMode.Limit, 50));
            pool.SetCount("Ingot/Iron", 130);
            pool.SetCount("Ingot/Gold", 80);
            Assert.Equal(30, pool.Excess("Ingot/Iron"));
            Assert.Equal(30, pool.Excess("Ingot/Gold"));
        }

        [Fact]
        public void ExcessZeroForMinAndNoQuota()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Min, 100));
            pool.SetCount("Ingot/Iron", 300);
            Assert.Equal(0, pool.Excess("Ingot/Iron"));   // Min never removes
            Assert.Equal(0, pool.Excess("Ingot/Gold"));   // no quota
        }

        [Fact]
        public void TookInReducesDeficit()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Min, 100));
            pool.SetCount("Ingot/Iron", 30);
            Assert.Equal(70, pool.Deficit("Ingot/Iron"));
            pool.TookIn("Ingot/Iron", 40);
            Assert.Equal(30, pool.Deficit("Ingot/Iron"));
        }

        [Fact]
        public void AllAlwaysHasDeficitNeverExcess()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.All, 0));
            pool.SetCount("Ingot/Iron", 1000000);
            Assert.True(pool.Deficit("Ingot/Iron") > 0);
            Assert.Equal(0, pool.Excess("Ingot/Iron"));
            pool.TookIn("Ingot/Iron", 5000);
            Assert.True(pool.Deficit("Ingot/Iron") > 0);   // still wants more
        }

        static ReservePool PoolWith(Dictionary<string, ReservePool> pools, string name, string key, string quota)
        {
            ReservePool pool;
            if (!pools.TryGetValue(name, out pool)) { pool = new ReservePool(name); pools[name] = pool; }
            Quota q;
            Assert.True(Quota.TryParse(quota, out q));
            pool.SetQuota(key, q);
            return pool;
        }

        static List<string> SharedAllLines(Dictionary<string, ReservePool> pools)
        {
            var channel = new StateChannel(_ => { });
            channel.Begin();
            ReservePool.SharedAll(pools, new Dictionary<string, string>(), channel);
            return channel.Current;
        }

        [Fact]
        public void TwoPoolsAllingOneItemAreReported()
        {
            var pools = new Dictionary<string, ReservePool>();
            PoolWith(pools, "", "Ingot/Iron", "All");
            PoolWith(pools, "Ammo", "Ingot/Iron", "All");
            Assert.Equal(new[] { "reserves (default) and Ammo both want All of Ingot/Iron - only one fills" }, SharedAllLines(pools));
        }

        [Fact]
        public void AllBesideAFiniteQuotaElsewhereIsNotReported()
        {
            var pools = new Dictionary<string, ReservePool>();
            PoolWith(pools, "A", "Ingot/Iron", "All");
            PoolWith(pools, "B", "Ingot/Iron", "500");
            PoolWith(pools, "B", "Ore/Ice", "All");
            Assert.Empty(SharedAllLines(pools));
        }

        [Fact]
        public void TallyIsInvisibleUntilCommitted()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Exact, 100));
            pool.SetCount("Ingot/Iron", 100);
            pool.BeginTally();
            pool.Tally("Ingot/Iron", 30);
            Assert.Equal(100, pool.Count("Ingot/Iron"));   // a render mid-walk still sees the whole previous count
            pool.CommitTally();
            Assert.Equal(30, pool.Count("Ingot/Iron"));
            Assert.Equal(70, pool.Deficit("Ingot/Iron"));
        }

        [Fact]
        public void TookInAfterCommitAddsToTheNewCount()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Exact, 100));
            pool.SetCount("Ingot/Iron", 90);
            pool.BeginTally();
            pool.Tally("Ingot/Iron", 20);
            pool.CommitTally();
            pool.TookIn("Ingot/Iron", 5);
            Assert.Equal(25, pool.Count("Ingot/Iron"));
        }

        [Fact]
        public void BeginTallyDropsAnAbandonedWalk()
        {
            var pool = new ReservePool("P");
            pool.BeginTally();
            pool.Tally("Ore/Ice", 10);                    // a walk cut short by a reset never commits
            pool.BeginTally();
            pool.Tally("Ore/Ice", 4);
            pool.CommitTally();
            Assert.Equal(4, pool.Count("Ore/Ice"));
        }

        [Fact]
        public void ConfiguredPoolTalliesOnlyQuotaItems()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Exact, 100));
            pool.BeginTally();
            pool.Tally("Ingot/Iron", 10);
            pool.Tally("Ore/Stone", 500);
            pool.Tally("Ingot/Iron", 5);
            pool.CommitTally();
            Assert.Equal(15, pool.Count("Ingot/Iron"));
            Assert.Single(pool.Counts);
        }

        [Fact]
        public void UnconfiguredPoolTalliesEverything()
        {
            var pool = new ReservePool("P");               // its levels LCD lists the keys a user needs for quotas
            pool.BeginTally();
            pool.Tally("Ingot/Iron", 10);
            pool.Tally("Ore/Stone", 500);
            pool.CommitTally();
            Assert.Equal(2, pool.Counts.Count);
        }

        [Fact]
        public void TookOutReducesExcess()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Exact, 100));
            pool.SetCount("Ingot/Iron", 130);
            Assert.Equal(30, pool.Excess("Ingot/Iron"));
            pool.TookOut("Ingot/Iron", 30);
            Assert.Equal(0, pool.Excess("Ingot/Iron"));   // drained to target, deadband closed
            Assert.Equal(0, pool.Deficit("Ingot/Iron"));  // neither adds nor removes now
        }

        [Fact]
        public void TookOutClampsAtZero()
        {
            var pool = new ReservePool("P");
            pool.SetQuota("Ingot/Iron", new Quota(QuotaMode.Limit, 50));
            pool.SetCount("Ingot/Iron", 60);
            pool.TookOut("Ingot/Iron", 999);              // a short commit can over-request; count never goes negative
            Assert.Equal(0, pool.Excess("Ingot/Iron"));
        }

        [Fact]
        public void WithAmountKeepsModeLettersAndAutomaticFlag()
        {
            Quota q;
            Assert.True(Quota.TryParse("500MA", out q));
            Assert.Equal("800MA", q.WithAmount(800));
            Assert.True(Quota.TryParse("100L", out q));
            Assert.Equal("12.5L", q.WithAmount(12.5));
            Assert.Equal("12345.68", new Quota(QuotaMode.Exact, 0).WithAmount(12345.678));
            Assert.True(Quota.TryParse("All", out q));
            Assert.Null(q.WithAmount(5));
        }
    }
}
