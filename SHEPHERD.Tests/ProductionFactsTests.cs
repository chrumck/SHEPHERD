using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class ProductionFactsTests
    {
        [Fact]
        public void BakedExceptionsResolveWithoutAProbe()
        {
            var facts = new ProductionFacts();
            string bp;
            Assert.True(facts.TryBlueprint("Component/Canvas", out bp));
            Assert.Equal("Position0030_Canvas", bp);
            Assert.True(facts.TryBlueprint("ConsumableItem/Medkit", out bp));
            Assert.Equal("Position0021_Medkit", bp);
            Assert.True(facts.TryBlueprint("PhysicalGunObject/AngleGrinder2Item", out bp));
            Assert.Equal("Position0020_AngleGrinder2", bp);
            Assert.True(facts.TryBlueprint("AmmoMagazine/NATO_25x184mm", out bp));
            Assert.Equal("Position0080_NATO_25x184mmMagazine", bp);
            Assert.True(facts.TryBlueprint("AmmoMagazine/FlareClip", out bp));
            Assert.Equal("Position0051_FlareGunMagazine", bp);
            Assert.True(facts.TryBlueprint("PhysicalGunObject/ElitePistolItem", out bp));
            Assert.Equal("Position0030_EliteAutoPistol", bp);
            Assert.True(facts.TryBlueprint("OxygenContainerObject/OxygenBottle", out bp));
            Assert.Equal("Position0010_OxygenBottle", bp);
            Assert.True(facts.TryBlueprint("GasContainerObject/HydrogenBottle", out bp));
            Assert.Equal("Position0020_HydrogenBottle", bp);
            Assert.True(facts.TryBlueprint("Datapad/Datapad", out bp));
            Assert.Equal("Position0040_Datapad", bp);
            Assert.True(facts.TryBlueprint("AmmoMagazine/AutomaticRifleGun_Mag_20rd", out bp));
            Assert.Equal("Position0040_AutomaticRifleGun_Mag_20rd", bp);
        }

        [Fact]
        public void ConventionComponentsAreNotBaked()
        {
            var facts = new ProductionFacts();
            string bp;
            Assert.False(facts.TryBlueprint("Component/SteelPlate", out bp));
            Assert.False(facts.TryBlueprint("Component/Construction", out bp));
        }

        [Fact]
        public void ResolveTriesPlainSubtypeThenComponentSuffix()
        {
            var facts = new ProductionFacts();
            var probed = new List<string>();
            string bp;
            Assert.True(facts.TryResolveBlueprint("Component/Construction",
                c => { probed.Add(c); return c == "ConstructionComponent"; }, out bp));
            Assert.Equal("ConstructionComponent", bp);
            Assert.Equal(new[] { "Construction", "ConstructionComponent" }, probed);

            Assert.True(facts.TryResolveBlueprint("Component/SteelPlate", c => c == "SteelPlate", out bp));
            Assert.Equal("SteelPlate", bp);
        }

        [Fact]
        public void ResolveCachesAValidatedCandidateForTheSession()
        {
            var facts = new ProductionFacts();
            string bp;
            Assert.True(facts.TryResolveBlueprint("Component/SteelPlate", c => c == "SteelPlate", out bp));
            Assert.True(facts.TryResolveBlueprint("Component/SteelPlate", c => false, out bp));
            Assert.Equal("SteelPlate", bp);
            Assert.Equal("Component/SteelPlate", facts.ItemOf("SteelPlate"));
        }

        [Fact]
        public void ResolveDoesNotCacheAFailure()
        {
            var facts = new ProductionFacts();
            string bp;
            Assert.False(facts.TryResolveBlueprint("Component/Modded", c => false, out bp));
            Assert.Null(bp);
            Assert.True(facts.TryResolveBlueprint("Component/Modded", c => c == "Modded", out bp));
            Assert.Equal("Modded", bp);
        }

        [Fact]
        public void ItemOfUsesTheBakedReverseMap()
        {
            var facts = new ProductionFacts();
            Assert.Equal("AmmoMagazine/Missile200mm", facts.ItemOf("Position0100_Missile200mm"));
            Assert.Equal("PhysicalGunObject/WelderItem", facts.ItemOf("Position0090_Welder"));
        }

        [Fact]
        public void ItemOfFallsBackToTheComponentConvention()
        {
            var facts = new ProductionFacts();
            Assert.Equal("Component/Construction", facts.ItemOf("ConstructionComponent"));
            Assert.Equal("Component/SteelPlate", facts.ItemOf("SteelPlate"));
            Assert.Equal("Component/UnknownModThing", facts.ItemOf("UnknownModThing"));
        }

        [Fact]
        public void VanillaYieldsAreBaked()
        {
            var facts = new ProductionFacts();
            List<OreYield> outputs;
            Assert.True(facts.TryYields("Ore/Iron", out outputs));
            Assert.Single(outputs);
            Assert.Equal("Ingot/Iron", outputs[0].Output);
            Assert.Equal(0.7, outputs[0].Ratio);

            Assert.True(facts.TryYields("Ore/Scrap", out outputs));
            Assert.Equal("Ingot/Iron", outputs[0].Output);
            Assert.Equal(0.8, outputs[0].Ratio);

            Assert.True(facts.TryYields("Ore/Platinum", out outputs));
            Assert.Equal(0.005, outputs[0].Ratio);
        }

        [Fact]
        public void StoneIsMultiOutput()
        {
            var facts = new ProductionFacts();
            List<OreYield> outputs;
            Assert.True(facts.TryYields("Ore/Stone", out outputs));
            Assert.Equal(4, outputs.Count);
            Assert.Equal("Ingot/Stone", outputs[0].Output);
            Assert.Equal(0.014, outputs[0].Ratio);
            Assert.Equal("Ingot/Iron", outputs[1].Output);
            Assert.Equal(0.03, outputs[1].Ratio);
            Assert.Equal("Ingot/Nickel", outputs[2].Output);
            Assert.Equal(0.0024, outputs[2].Ratio);
            Assert.Equal("Ingot/Silicon", outputs[3].Output);
            Assert.Equal(0.004, outputs[3].Ratio);
        }

        [Fact]
        public void IceYieldsNothingAsAKnownFact()
        {
            var facts = new ProductionFacts();
            List<OreYield> outputs;
            Assert.True(facts.TryYields("Ore/Ice", out outputs));   // known, so never a yield-learning candidate
            Assert.Empty(outputs);
            Assert.True(facts.TryYields("Ore/Organic", out outputs));
            Assert.Empty(outputs);
        }

        [Fact]
        public void LearnAddsBothDirectionsAndSetsDirty()
        {
            var facts = new ProductionFacts();
            Assert.False(facts.TakeDirty());
            facts.Learn("Component/ModAlloy", "ModAlloyBlueprint");
            string bp;
            Assert.True(facts.TryBlueprint("Component/ModAlloy", out bp));
            Assert.Equal("ModAlloyBlueprint", bp);
            Assert.Equal("Component/ModAlloy", facts.ItemOf("ModAlloyBlueprint"));
            Assert.True(facts.TakeDirty());
            Assert.False(facts.TakeDirty());
        }

        [Fact]
        public void LearnYieldStoresAndSetsDirty()
        {
            var facts = new ProductionFacts();
            facts.LearnYield("Ore/Modium", new List<OreYield> { new OreYield("Ingot/Modium", 0.25) });
            List<OreYield> outputs;
            Assert.True(facts.TryYields("Ore/Modium", out outputs));
            Assert.Equal(0.25, outputs[0].Ratio);
            Assert.True(facts.TakeDirty());
        }

        [Fact]
        public void SessionCacheFromProbingIsNotDirty()
        {
            var facts = new ProductionFacts();
            string bp;
            Assert.True(facts.TryResolveBlueprint("Component/SteelPlate", c => c == "SteelPlate", out bp));
            Assert.False(facts.TakeDirty());
        }

        [Fact]
        public void LoadedFactsRoundTripWithoutDirtying()
        {
            var facts = new ProductionFacts();
            Assert.True(facts.LoadRecipe("Component/ModAlloy", "ModAlloyBlueprint"));
            Assert.True(facts.LoadYield("Ore/Modium", "Ingot/Modium:0.25,Ingot/Iron:0.1"));
            Assert.False(facts.TakeDirty());
            var entries = new List<KeyValuePair<string, string>>();
            facts.RecipeEntries(entries);
            Assert.Equal(new KeyValuePair<string, string>("Component/ModAlloy", "ModAlloyBlueprint"), Assert.Single(entries));
            facts.YieldEntries(entries);
            Assert.Equal("Ore/Modium", Assert.Single(entries).Key);
            Assert.Equal("Ingot/Modium:0.25,Ingot/Iron:0.1", entries[0].Value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("broken")]
        [InlineData("Ingot/X:0")]
        [InlineData("Ingot/X:-1")]
        [InlineData("Ingot/X:nope")]
        [InlineData("Ingot/X:1,Ingot/X:2")]
        public void InvalidYieldIsRejectedAtomically(string value)
        {
            var facts = new ProductionFacts();
            Assert.False(facts.LoadYield("Ore/Modium", value));
            List<OreYield> outputs;
            Assert.False(facts.TryYields("Ore/Modium", out outputs));
        }

        [Fact]
        public void ClearLearnedRemovesReverseAndPreservesBakedFacts()
        {
            var facts = new ProductionFacts();
            facts.LoadRecipe("Component/ModAlloy", "ModAlloyBlueprint");
            facts.LoadYield("Ore/Modium", "Ingot/Modium:0.25");
            facts.ClearLearned();
            string bp;
            List<OreYield> outputs;
            Assert.False(facts.TryBlueprint("Component/ModAlloy", out bp));
            Assert.Equal("Component/ModAlloyBlueprint", facts.ItemOf("ModAlloyBlueprint"));
            Assert.False(facts.TryYields("Ore/Modium", out outputs));
            Assert.True(facts.TryBlueprint("Component/Canvas", out bp));
            Assert.True(facts.TryYields("Ore/Iron", out outputs));
            Assert.False(facts.TakeDirty());
        }

        [Fact]
        public void IdenticalLearnedValuesDoNotDirtyAgain()
        {
            var facts = new ProductionFacts();
            facts.Learn("Component/ModAlloy", "ModAlloyBlueprint");
            Assert.True(facts.TakeDirty());
            facts.Learn("Component/ModAlloy", "ModAlloyBlueprint");
            Assert.False(facts.TakeDirty());
            facts.LearnYield("Ore/Modium", new List<OreYield> { new OreYield("Ingot/Modium", 0.25) });
            Assert.True(facts.TakeDirty());
            facts.LearnYield("Ore/Modium", new List<OreYield> { new OreYield("Ingot/Modium", 0.25) });
            Assert.False(facts.TakeDirty());
        }
    }
}
