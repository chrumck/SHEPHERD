using System;
using System.Text;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class LevelsViewTests
    {
        static string Render(ReservePool pool)
        {
            var sb = new StringBuilder();
            LevelsView.Render(sb, pool);
            return sb.ToString();
        }

        static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

        static Quota Q(string value)
        {
            Quota q;
            Assert.True(Quota.TryParse(value, out q));
            return q;
        }

        [Fact]
        public void ConfiguredPoolGroupsByTypeInFirstAppearanceOrder()
        {
            var pool = new ReservePool("Foo");
            pool.SetQuota("Component/SteelPlate", Q("500M"));
            pool.SetQuota("Ingot/Iron", Q("1000"));
            pool.SetQuota("Component/Motor", Q("100LA"));
            pool.SetQuota("Ore/Iron", Q("All"));
            pool.Containers = 1;
            pool.TookIn("Component/SteelPlate", 340);
            pool.TookIn("Ingot/Iron", 1200);
            pool.TookIn("Ore/Iron", 12345);

            Assert.Equal(Lines(
                "Foo · 96%",              // (340 + 1200) / (500 + 1000 + 100); All carries no target
                "Component",
                " SteelPlate  340/500M",
                " Motor       0/100LA",
                "Ingot",
                " Iron        1200/1000",  // same subtype as the ore, kept apart by its type header
                "Ore",
                " Iron        12.3k/All"), Render(pool));
        }

        [Fact]
        public void UnconfiguredPoolListsItsContentsGrouped()
        {
            var pool = new ReservePool("Missiles");
            pool.Containers = 1;
            pool.TookIn("AmmoMagazine/NATO_5p56x45mm", 40);
            pool.TookIn("Component/SteelPlate", 250);
            pool.TookIn("AmmoMagazine/Missile200mm", 2.5);

            Assert.Equal(Lines(
                "Missiles · no quotas",
                "AmmoMagazine",
                " NATO_5p56x45mm  40",
                " Missile200mm    2.5",
                "Component",
                " SteelPlate      250"), Render(pool));
        }

        [Fact]
        public void HeaderStates()
        {
            var noContainer = new ReservePool("A");
            noContainer.SetQuota("Ingot/Iron", Q("100"));
            Assert.StartsWith("A · no container", Render(noContainer));

            var allOnly = new ReservePool("");
            allOnly.SetQuota("Ore/Ice", Q("All"));
            allOnly.Containers = 2;
            Assert.StartsWith("(default) · all", Render(allOnly));
        }

        static string RenderStock(string category, params object[] rows)
        {
            var keys = new System.Collections.Generic.List<string>();
            var categories = new System.Collections.Generic.List<string>();
            var ledger = new ProductionLedger();
            for (var i = 0; i < rows.Length; i += 3)
            {
                keys.Add((string)rows[i]);
                categories.Add((string)rows[i + 1]);
                ledger.AddStock((string)rows[i], System.Convert.ToDouble(rows[i + 2]));
            }
            var sb = new StringBuilder();
            LevelsView.Render(sb, category, keys, categories, ledger);
            return sb.ToString();
        }

        [Fact]
        public void StockListsOnlyTheCategorysItemsInStockGrouped()
        {
            Assert.Equal(Lines(
                "Tools · 3 items",
                "GasContainerObject",
                " HydrogenBottle  4",
                "PhysicalGunObject",
                " AngleGrinder    1",
                " Welder          2"), RenderStock(Categories.Tools,
                    "GasContainerObject/HydrogenBottle", Categories.Tools, 4,
                    "Ore/Iron", Categories.Ores, 500,
                    "PhysicalGunObject/AngleGrinder", Categories.Tools, 1,
                    "PhysicalGunObject/Drill", Categories.Tools, 0,   // queued, none held
                    "PhysicalGunObject/Welder", Categories.Tools, 2));
        }

        [Fact]
        public void CatchAllSectionListsTheCatchAllContentsOfAnyCategory()
        {
            var keys = new System.Collections.Generic.List<string> { "Component/Motor", "Ore/Iron", "Ore/Stone" };
            var categories = new System.Collections.Generic.List<string> { Categories.Components, Categories.Ores, Categories.Ores };
            var ledger = new ProductionLedger();
            ledger.AddStock("Component/Motor", 30);
            ledger.AddCaught("Component/Motor", 12);
            ledger.AddStock("Ore/Iron", 500);
            ledger.AddCaught("Ore/Iron", 100);
            ledger.AddStock("Ore/Stone", 40);            // held, but not in a catch-all
            var sb = new StringBuilder();
            LevelsView.Render(sb, Categories.Ores, keys, categories, ledger);
            LevelsView.Render(sb, null, keys, categories, ledger);
            Assert.Equal(Lines(
                "Ores · 2 items",
                "Ore",
                " Iron   500",
                " Stone  40",
                "",
                "catch-all · 2 items",
                "Component",
                " Motor  12",
                "Ore",
                " Iron   100"), sb.ToString());
        }

        [Fact]
        public void StockOfAnEmptyCategory()
        {
            Assert.Equal(Lines("Ice · empty"), RenderStock("Ice", "Ore/Iron", Categories.Ores, 500));
        }

        [Fact]
        public void LargeAmountsAreCompacted()
        {
            var pool = new ReservePool("B");
            pool.Containers = 1;
            pool.TookIn("Ingot/Iron", 5000);
            pool.TookIn("Ore/Stone", 1500000);
            var text = Render(pool);
            Assert.Contains(" Iron   5000", text);  // below ten thousand reads back exactly
            Assert.Contains(" Stone  1500k", text);   // never M: that is the Min quota letter
        }
    }
}
