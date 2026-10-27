using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class LearnTests
    {
        readonly PlacementLearner learner = new PlacementLearner();
        readonly CategoryOverrides overrides = new CategoryOverrides();
        readonly Dictionary<string, ReservePool> pools = new Dictionary<string, ReservePool>();

        void Finish() => learner.Finish(overrides, pools);

        ReservePool Pool(string name, string key, string quota)
        {
            ReservePool pool;
            if (!pools.TryGetValue(name, out pool)) { pool = new ReservePool(name); pools[name] = pool; }
            Quota q;
            Assert.True(Quota.TryParse(quota, out q));
            pool.SetQuota(key, q);
            return pool;
        }

        // --- categories ---

        [Fact]
        public void NewTokenTeachesAnOverride()
        {
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.Equal(new[] { new KeyValuePair<string, string>("Ore/Ice", "Ice") }, learner.CategorySets);
            Assert.Equal(new[] { "learn Ore/Ice -> Ice" }, learner.Lines);
            Assert.True(learner.HasChanges);
        }

        [Fact]
        public void AnotherBuiltInTokenTeachesAnOverride()
        {
            learner.TeachCategory(Categories.Ingots, "Ore/Stone", Categories.Ores);
            Finish();
            Assert.Equal(new[] { new KeyValuePair<string, string>("Ore/Stone", Categories.Ingots) }, learner.CategorySets);
        }

        [Fact]
        public void SameTokenInTwoContainersTeachesOnce()
        {
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.Single(learner.CategorySets);
        }

        [Fact]
        public void OverrideAlreadyInForceIsNotAChange()
        {
            overrides.Set("Ore/Ice", "Ice");
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.Empty(learner.CategorySets);
            Assert.False(learner.HasChanges);
            Assert.Equal(new[] { "learning - nothing to save yet" }, learner.Lines);
        }

        [Fact]
        public void TwoDifferentTokensAreAConflictAndTeachNothing()
        {
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            learner.TeachCategory("Water", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.Empty(learner.CategorySets);
            Assert.False(learner.HasChanges);
            Assert.Equal(new[] { "conflict Ore/Ice: Ice, Water" }, learner.Lines);
        }

        [Fact]
        public void StockLeftInItsBuiltInContainerIsAConflict()
        {
            learner.TeachCategory(Categories.Ores, "Ore/Ice", Categories.Ores);
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.False(learner.HasChanges);
            Assert.Equal(new[] { "conflict Ore/Ice: Ores, Ice" }, learner.Lines);
        }

        [Fact]
        public void StockLeftUnderItsSavedCategoryIsAConflict()
        {
            overrides.Set("Ore/Ice", "Water");
            learner.TeachCategory("Water", "Ore/Ice", Categories.Ores);
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.False(learner.HasChanges);
            Assert.Equal(new[] { "conflict Ore/Ice: Water, Ice" }, learner.Lines);
        }

        [Fact]
        public void APlacementReplacesTheSavedCategory()
        {
            overrides.Set("Ore/Ice", "Water");
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.Equal(new[] { new KeyValuePair<string, string>("Ore/Ice", "Ice") }, learner.CategorySets);
        }

        [Fact]
        public void BuiltInStockBesideASavedOverrideIsAConflictNotARemoval()
        {
            overrides.Set("Ore/Ice", "Ice");
            learner.TeachCategory(Categories.Ores, "Ore/Ice", Categories.Ores);
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            Finish();
            Assert.Empty(learner.CategoryRemovals);
            Assert.Equal(new[] { "conflict Ore/Ice: Ores, Ice" }, learner.Lines);
        }

        [Fact]
        public void PlacementOnlyInItsBuiltInContainerRemovesTheOverride()
        {
            overrides.Set("Ore/Ice", "Ice");
            learner.TeachCategory(Categories.Ores, "Ore/Ice", Categories.Ores);
            Finish();
            Assert.Equal(new[] { "Ore/Ice" }, learner.CategoryRemovals);
            Assert.Equal(new[] { "learn Ore/Ice -> built-in" }, learner.Lines);
        }

        [Fact]
        public void PlacementInItsBuiltInContainerWithoutAnOverrideTeachesNothing()
        {
            learner.TeachCategory(Categories.Ores, "Ore/Iron", Categories.Ores);
            Finish();
            Assert.False(learner.HasChanges);
            Assert.Equal(new[] { "learning - nothing to save yet" }, learner.Lines);
        }

        [Fact]
        public void AbsenceNeverRemovesAnOverride()
        {
            overrides.Set("Ore/Ice", "Ice");
            Finish();
            Assert.Empty(learner.CategoryRemovals);
            Assert.False(learner.HasChanges);
        }

        [Fact]
        public void ApplySetsAndRemoves()
        {
            overrides.Set("Ore/Stone", Categories.Ingots);
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            learner.TeachCategory(Categories.Ores, "Ore/Stone", Categories.Ores);
            Finish();
            learner.Apply(overrides);
            string cat;
            Assert.True(overrides.TryGet("Ore/Ice", out cat));
            Assert.Equal("Ice", cat);
            Assert.False(overrides.TryGet("Ore/Stone", out cat));
        }

        // --- reserves ---

        [Fact]
        public void NewReserveKeyIsAPlainLineInTheDefaultSection()
        {
            learner.TeachReserve("", "Component/SteelPlate", 2000);
            Finish();
            var line = Assert.Single(learner.QuotaLines);
            Assert.Equal("Reserve", line.Section);
            Assert.Equal("Component/SteelPlate", line.Key);
            Assert.Equal("2000", line.Value);
            Assert.Equal(new[] { "learn Reserve Component/SteelPlate=2000" }, learner.Lines);
        }

        [Fact]
        public void NamedPoolWritesItsOwnSection()
        {
            learner.TeachReserve("Ship", "Component/SteelPlate", 10);
            Finish();
            Assert.Equal("Reserve:Ship", Assert.Single(learner.QuotaLines).Section);
        }

        [Fact]
        public void APoolsContainersAreSummed()
        {
            learner.TeachReserve("Ship", "Ingot/Uranium", 12.5);
            learner.TeachReserve("Ship", "Ingot/Uranium", 0.25);
            Finish();
            Assert.Equal("12.75", Assert.Single(learner.QuotaLines).Value);
        }

        [Fact]
        public void FractionalAmountsKeepTwoDecimalsNeverTheCompactForm()
        {
            learner.TeachReserve("", "Ingot/Iron", 12345.678);
            Finish();
            Assert.Equal("12345.68", Assert.Single(learner.QuotaLines).Value);
        }

        [Fact]
        public void RelearningOverwritesTheAmountAndKeepsTheLetters()
        {
            Pool("", "Component/SteelPlate", "500MA");
            Pool("", "Component/Girder", "100L");
            learner.TeachReserve("", "Component/SteelPlate", 300);
            learner.TeachReserve("", "Component/Girder", 140);
            Finish();
            Assert.Equal(new[] { "learn Reserve Component/Girder=140L", "learn Reserve Component/SteelPlate=300MA" },
                learner.Lines);
        }

        [Fact]
        public void AnAmountTheLineAlreadyAllowsTeachesNothing()
        {
            Pool("", "Component/SteelPlate", "500MA");
            Pool("", "Component/Girder", "100L");
            Pool("", "Component/Motor", "50");
            learner.TeachReserve("", "Component/SteelPlate", 800);   // above an M floor
            learner.TeachReserve("", "Component/Girder", 40);        // below an L ceiling
            learner.TeachReserve("", "Component/Motor", 80);
            Finish();
            Assert.Equal(new[] { "learn Reserve Component/Motor=80" }, learner.Lines);
            Assert.Single(learner.QuotaLines);
        }

        [Fact]
        public void AnAllLineIsLeftAlone()
        {
            Pool("", "Ore/Ice", "All");
            learner.TeachReserve("", "Ore/Ice", 5000);
            Finish();
            Assert.Empty(learner.QuotaLines);
        }

        [Fact]
        public void TheSameAmountIsNotAChange()
        {
            Pool("Ship", "Component/SteelPlate", "2000M");
            learner.TeachReserve("Ship", "Component/SteelPlate", 2000);
            Finish();
            Assert.Empty(learner.QuotaLines);
        }

        [Fact]
        public void AQuotaKeyNotInTheContainersIsLeftAlone()
        {
            Pool("Ship", "Component/SteelPlate", "2000");
            learner.TeachReserve("Ship", "Component/Girder", 10);
            Finish();
            Assert.Equal("Component/Girder", Assert.Single(learner.QuotaLines).Key);
        }

        [Fact]
        public void APoolTaughtOnlyFromDockedShipsIsFlaggedDocked()
        {
            learner.TeachReserve("Ship", "Component/SteelPlate", 2000, docked: true);
            Finish();
            Assert.Equal(new[] { "learn Reserve:Ship docked=true", "learn Reserve:Ship Component/SteelPlate=2000" },
                learner.Lines);
            Assert.Equal("docked", learner.QuotaLines[0].Key);
            Assert.Equal("true", learner.QuotaLines[0].Value);
        }

        [Fact]
        public void APoolWithAHomeContainerIsNotFlagged()
        {
            learner.TeachReserve("Ship", "Component/SteelPlate", 1000, docked: true);
            learner.TeachReserve("Ship", "Component/SteelPlate", 1000);
            Finish();
            Assert.Equal(new[] { "learn Reserve:Ship Component/SteelPlate=2000" }, learner.Lines);
        }

        [Fact]
        public void AnAlreadyFlaggedPoolIsNotFlaggedAgain()
        {
            Pool("Ship", "Component/SteelPlate", "2000").Docked = true;
            learner.TeachReserve("Ship", "Component/SteelPlate", 2000, docked: true);
            Finish();
            Assert.False(learner.HasChanges);
        }

        // --- sweeps and order ---

        [Fact]
        public void PreviewOrderIsSetsRemovalsReservesThenConflictsEachSorted()
        {
            overrides.Set("Ore/Stone", Categories.Ingots);
            learner.TeachCategory("Water", "Ore/Ice", Categories.Ores);
            learner.TeachCategory("Fuel", "Ore/Ice", Categories.Ores);
            learner.TeachReserve("Ship", "Ingot/Iron", 1);
            learner.TeachCategory(Categories.Ores, "Ore/Stone", Categories.Ores);
            learner.TeachCategory("Explosives", "Component/Explosives", Categories.Components);
            learner.TeachCategory("Fuel", "Ingot/Uranium", Categories.Ingots);
            learner.TeachReserve("", "Ingot/Iron", 2);
            Finish();
            Assert.Equal(new[]
            {
                "learn Component/Explosives -> Explosives",
                "learn Ingot/Uranium -> Fuel",
                "learn Ore/Stone -> built-in",
                "learn Reserve Ingot/Iron=2",
                "learn Reserve:Ship Ingot/Iron=1",
                "conflict Ore/Ice: Water, Fuel",
            }, learner.Lines);
        }

        [Fact]
        public void EachSweepStartsFromNothing()
        {
            learner.TeachCategory("Ice", "Ore/Ice", Categories.Ores);
            learner.TeachReserve("", "Ingot/Iron", 5);
            Finish();
            learner.Begin();
            learner.TeachReserve("", "Ingot/Iron", 3);
            Finish();
            Assert.Empty(learner.CategorySets);
            Assert.Equal("3", Assert.Single(learner.QuotaLines).Value);
            Assert.Equal(new[] { "learn Reserve Ingot/Iron=3" }, learner.Lines);
        }
    }
}
