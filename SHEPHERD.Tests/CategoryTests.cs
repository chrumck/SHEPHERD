using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class CategoryTests
    {
        [Theory]
        [InlineData("MyObjectBuilder_Ore", Categories.Ores)]
        [InlineData("MyObjectBuilder_Ingot", Categories.Ingots)]
        [InlineData("MyObjectBuilder_Component", Categories.Components)]
        [InlineData("MyObjectBuilder_PhysicalGunObject", Categories.Tools)]
        [InlineData("MyObjectBuilder_AmmoMagazine", Categories.Ammo)]
        [InlineData("MyObjectBuilder_ConsumableItem", Categories.Consumables)]
        // Gas/oxygen bottles roll into Tools in v1 (no separate bottle bucket).
        [InlineData("MyObjectBuilder_GasContainerObject", Categories.Tools)]
        [InlineData("MyObjectBuilder_OxygenContainerObject", Categories.Tools)]
        public void BuiltInTypeIdsMapToBuckets(string typeId, string expected)
        {
            Assert.Equal(expected, Category.For(typeId));
        }

        [Theory]
        [InlineData("MyObjectBuilder_Datapad")]
        [InlineData("MyObjectBuilder_PhysicalObject")]
        [InlineData("MyObjectBuilder_TreeObject")]
        [InlineData("Totally_Unknown")]
        public void UnknownTypeIdsAreOthers(string typeId)
        {
            Assert.Equal(Categories.Others, Category.For(typeId));
        }

        [Fact]
        public void BuilderPrefixIsOptional()
        {
            Assert.Equal(Categories.Ores, Category.For("Ore"));
            Assert.Equal(Categories.Ingots, Category.For("Ingot"));
        }

        [Fact]
        public void NullOrEmptyTypeIdIsOthers()
        {
            Assert.Equal(Categories.Others, Category.For(null));
            Assert.Equal(Categories.Others, Category.For(""));
        }

        [Fact]
        public void OverrideWinsOverCoarseBucket()
        {
            var overrides = new CategoryOverrides();
            overrides.Set("Ore/Stone", Categories.Ingots); // key is the ShortTypeId form (builder prefix stripped)
            Assert.Equal(Categories.Ingots, Category.ForKey(Category.Key("MyObjectBuilder_Ore", "Stone"), overrides));
        }

        [Theory]
        [InlineData("Ore/Stone")]
        [InlineData("MyObjectBuilder_Ore/Stone")]
        public void OverrideKeyMatchesWithOrWithoutBuilderPrefix(string written)
        {
            var overrides = new CategoryOverrides();
            overrides.Set(written, Categories.Ingots);
            Assert.Equal(Categories.Ingots, Category.ForKey(Category.Key("MyObjectBuilder_Ore", "Stone"), overrides));
        }

        [Fact]
        public void UnmatchedKeyFallsBackToCoarse()
        {
            var overrides = new CategoryOverrides();
            overrides.Set("Ore/Ice", Categories.Others);
            Assert.Equal(Categories.Ores, Category.ForKey(Category.Key("MyObjectBuilder_Ore", "Stone"), overrides));
        }

        [Fact]
        public void NullOverridesIsCoarse()
        {
            Assert.Equal(Categories.Ores, Category.ForKey(Category.Key("MyObjectBuilder_Ore", "Stone"), null));
        }

        [Fact]
        public void CustomTokenReturnedVerbatim()
        {
            var overrides = new CategoryOverrides();
            overrides.Set("Ore/Stone", "Explosives");
            Assert.Equal("Explosives", Category.ForKey(Category.Key("MyObjectBuilder_Ore", "Stone"), overrides));
        }

        [Theory]
        [InlineData("Ore/Stone", "Ingots", true)]
        [InlineData("Component/SteelPlate", "Explosives", true)] // custom token accepted
        [InlineData("NoSlash", "Ingots", false)]
        [InlineData("/Leading", "Ingots", false)]               // no type before the slash
        [InlineData("Ore/Stone", "", false)]
        [InlineData("Ore/Stone", "   ", false)]                 // whitespace-only token
        [InlineData(null, "Ingots", false)]
        public void IsValidOverrideChecksKeyAndToken(string key, string value, bool expected)
        {
            Assert.Equal(expected, Category.IsValidOverride(key, value));
        }

        [Fact]
        public void OverridesModelSetRemoveClear()
        {
            var overrides = new CategoryOverrides();
            overrides.Set("Ore/Stone", Categories.Ingots);
            overrides.Set("Ore/Ice", "Water");
            Assert.Equal(2, overrides.Count);

            string cat;
            Assert.True(overrides.TryGet("Ore/Ice", out cat));
            Assert.Equal("Water", cat);
            Assert.False(overrides.TryGet("Ore/Nope", out cat));

            Assert.True(overrides.Remove("Ore/Ice"));
            Assert.False(overrides.Remove("Ore/Ice"));
            Assert.Equal(1, overrides.Count);

            overrides.Clear();
            Assert.Equal(0, overrides.Count);
        }

        [Fact]
        public void AddCustomCollectsEachNonBuiltInTokenOnce()
        {
            var into = new List<string>();
            foreach (var token in new[] { "Ice", Categories.Ores, "Explosives", "Ice", "", null, Categories.Others })
                Categories.AddCustom(into, token);
            Assert.Equal(new[] { "Ice", "Explosives" }, into);
        }

        [Fact]
        public void ForKeyIsTheBuiltInBucketOfTheTypeHalf()
        {
            Assert.Equal(Categories.Ores, Category.ForKey("Ore/Ice"));
            Assert.Equal(Categories.Ingots, Category.ForKey("MyObjectBuilder_Ingot/Uranium"));
            Assert.Equal(Categories.Tools, Category.ForKey("GasContainerObject/HydrogenBottle"));
            Assert.Equal(Categories.Others, Category.ForKey("Modded/Thing"));
        }

        [Fact]
        public void ForKeyWithOverridesPrefersTheOverride()
        {
            var overrides = new CategoryOverrides();
            overrides.Set("Ore/Ice", "Ice");
            Assert.Equal("Ice", Category.ForKey("Ore/Ice", overrides));
            Assert.Equal(Categories.Ores, Category.ForKey("Ore/Iron", overrides));
        }

        // Learn mode changes the map without a reload, so an override must apply to a key already looked up.
        [Fact]
        public void AnOverrideAddedAfterALookupApplies()
        {
            var overrides = new CategoryOverrides();
            Assert.Equal(Categories.Ores, Category.ForKey("Ore/Gravel", overrides));
            overrides.Set("Ore/Gravel", "Rubble");
            Assert.Equal("Rubble", Category.ForKey("Ore/Gravel", overrides));
            overrides.Remove("Ore/Gravel");
            Assert.Equal(Categories.Ores, Category.ForKey("Ore/Gravel", overrides));
        }
    }
}
