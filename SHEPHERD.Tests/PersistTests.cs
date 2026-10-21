using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class PersistTests
    {
        // A game-free stand-in for MyIni: ordered sections of ordered key->value pairs. It proves the merge
        // algorithm touches only managed sections (an unmanaged section is literally never addressed). It can NOT
        // prove MyIni's real ToString round-trip preserves user comments — that is the Tier-2 gate's job.
        class FakeIni : IIniDocument
        {
            readonly List<string> order = new List<string>();
            readonly Dictionary<string, Dictionary<string, string>> sections =
                new Dictionary<string, Dictionary<string, string>>();

            public bool TryParse(string text, out string error) { error = null; return true; }

            Dictionary<string, string> SectionOf(string section, bool create)
            {
                Dictionary<string, string> d;
                if (!sections.TryGetValue(section, out d))
                {
                    if (!create) return null;
                    d = new Dictionary<string, string>();
                    sections[section] = d;
                    order.Add(section);
                }
                return d;
            }

            public void SectionNames(List<string> into) { foreach (var s in order) into.Add(s); }

            public void KeyNames(string section, List<string> into)
            {
                var d = SectionOf(section, false);
                if (d == null) return;
                foreach (var k in d.Keys) into.Add(k);
            }

            public string Get(string section, string key)
            {
                var d = SectionOf(section, false);
                string v;
                return d != null && d.TryGetValue(key, out v) ? v : null;
            }

            public void Set(string section, string key, string value) => SectionOf(section, true)[key] = value;

            public void DeleteKey(string section, string key)
            {
                var d = SectionOf(section, false);
                if (d != null) d.Remove(key);
            }

            public string ToText()
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in order)
                {
                    sb.Append('[').Append(s).Append("]\n");
                    foreach (var kv in sections[s]) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                }
                return sb.ToString();
            }

            public bool HasSection(string section) => sections.ContainsKey(section);
        }

        static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);

        static ConfigStore StoreOver(FakeIni ini) => new ConfigStore(ini, "Categories");

        [Fact]
        public void SetItemAddsALineAndCreatesTheSection()
        {
            var ini = new FakeIni();
            StoreOver(ini).SetItem("Reserve:Ship", "Component/SteelPlate", "2000");
            Assert.Equal("2000", ini.Get("Reserve:Ship", "Component/SteelPlate"));
        }

        [Fact]
        public void SetItemOverwritesThePrefixedLineForTheSameItem()
        {
            var ini = new FakeIni();
            ini.Set("Reserve", "MyObjectBuilder_Component/SteelPlate", "500M");
            ini.Set("Reserve", "Component/Girder", "10");
            StoreOver(ini).SetItem("Reserve", "Component/SteelPlate", "800M");
            Assert.Equal("800M", ini.Get("Reserve", "MyObjectBuilder_Component/SteelPlate"));
            Assert.Null(ini.Get("Reserve", "Component/SteelPlate"));
            Assert.Equal("10", ini.Get("Reserve", "Component/Girder"));
        }

        static List<KeyValuePair<string, string>> Settings(params string[] pairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (var i = 0; i < pairs.Length; i += 2) list.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
            return list;
        }

        [Fact]
        public void AddMissingWritesEveryKeyIntoAnAbsentSection()
        {
            var ini = new FakeIni();
            Assert.True(StoreOver(ini).AddMissing("Settings", Settings("peakMs", "0.8", "dockedFuel", "false")));
            Assert.Equal("0.8", ini.Get("Settings", "peakMs"));
            Assert.Equal("false", ini.Get("Settings", "dockedFuel"));
        }

        [Fact]
        public void AddMissingLeavesTheUsersLinesAsWritten()
        {
            var ini = new FakeIni();
            ini.Set("Settings", "peakMs", "2.0");
            ini.Set("Settings", "parkSim", "abc");          // a bad value is logged on load, never corrected
            Assert.True(StoreOver(ini).AddMissing("Settings", Settings("peakMs", "0.8", "parkSim", "0.75", "resumeSim", "0.8")));
            Assert.Equal("2.0", ini.Get("Settings", "peakMs"));
            Assert.Equal("abc", ini.Get("Settings", "parkSim"));
            Assert.Equal("0.8", ini.Get("Settings", "resumeSim"));
        }

        [Fact]
        public void AddMissingReportsNothingWhenEveryKeyIsPresent()
        {
            var ini = new FakeIni();
            ini.Set("Settings", "peakMs", "2.0");
            Assert.False(StoreOver(ini).AddMissing("Settings", Settings("peakMs", "0.8")));
            Assert.Equal("2.0", ini.Get("Settings", "peakMs"));
        }

        [Fact]
        public void AddMissingTreatsADifferentlyCasedKeyAsPresent()
        {
            var ini = new FakeIni();
            ini.Set("Settings", "Priority", "5");
            Assert.False(StoreOver(ini).AddMissing("Settings", Settings("priority", "0")));
            Assert.Equal("5", ini.Get("Settings", "Priority"));
            Assert.Null(ini.Get("Settings", "priority"));
        }

        [Fact]
        public void AddMissingTouchesNoOtherSection()
        {
            var ini = new FakeIni();
            ini.Set("Reserve", "peakMs", "7");
            StoreOver(ini).AddMissing("Settings", Settings("peakMs", "0.8"));
            Assert.Equal("7", ini.Get("Reserve", "peakMs"));
            Assert.Equal("0.8", ini.Get("Settings", "peakMs"));
        }

        [Fact]
        public void MergeAddsNewKeys()
        {
            var ini = new FakeIni();
            var store = StoreOver(ini);

            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ingots"), Kv("Ore/Bar", "Ores") });

            var read = store.ReadSection("Categories");
            Assert.Equal(2, read.Count);
            Assert.Equal("Ingots", ini.Get("Categories", "Ore/Foo"));
            Assert.Equal("Ores", ini.Get("Categories", "Ore/Bar"));
        }

        [Fact]
        public void MergeUpdatesExistingValue()
        {
            var ini = new FakeIni();
            ini.Set("Categories", "Ore/Foo", "Ores");
            var store = StoreOver(ini);

            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ingots") });

            Assert.Equal("Ingots", ini.Get("Categories", "Ore/Foo"));
        }

        [Fact]
        public void MergeDeletesKeyDroppedFromModel()
        {
            var ini = new FakeIni();
            ini.Set("Categories", "Ore/Keep", "Ores");
            ini.Set("Categories", "Ore/Gone", "Ores");
            var store = StoreOver(ini);

            store.MergeSection("Categories", new[] { Kv("Ore/Keep", "Ores") });

            Assert.Equal("Ores", ini.Get("Categories", "Ore/Keep"));
            Assert.Null(ini.Get("Categories", "Ore/Gone"));
        }

        [Fact]
        public void MergeLeavesUnmanagedSectionUntouched()
        {
            var ini = new FakeIni();
            ini.Set("Reserve:Ammo", "AmmoMagazine/NATO_5p56x45mm", "500M");
            ini.Set("Reserve:Ammo", "Ingot/Iron", "1000");
            var store = StoreOver(ini);

            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ingots") });

            Assert.Equal("500M", ini.Get("Reserve:Ammo", "AmmoMagazine/NATO_5p56x45mm"));
            Assert.Equal("1000", ini.Get("Reserve:Ammo", "Ingot/Iron"));
        }

        // Decision 6 (in-memory authoritative): a managed section is rewritten wholesale, so a user key added inside
        // a managed section is removed on flush — the merge-not-clobber guarantee protects other sections, not edits
        // inside a managed one (which the user makes via edit -> configReload, re-seeding the model).
        [Fact]
        public void MergeRemovesUnmanagedKeyInsideManagedSection()
        {
            var ini = new FakeIni();
            ini.Set("Categories", "Ore/UserAdded", "Ores");
            var store = StoreOver(ini);

            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ingots") });

            Assert.Null(ini.Get("Categories", "Ore/UserAdded"));
            Assert.Equal("Ingots", ini.Get("Categories", "Ore/Foo"));
        }

        [Fact]
        public void ReadSectionEmptyWhenAbsent()
        {
            var store = StoreOver(new FakeIni());
            Assert.Empty(store.ReadSection("Categories"));
        }

        [Fact]
        public void SignatureUnchangedSkipsReflush()
        {
            var ini = new FakeIni();
            var store = StoreOver(ini);
            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ingots"), Kv("Ore/Bar", "Ores") });

            var sig = store.Signature();
            store.MergeSection("Categories", new[] { Kv("Ore/Bar", "Ores"), Kv("Ore/Foo", "Ingots") });
            Assert.False(store.DirtySince(sig));
        }

        [Fact]
        public void SignatureDetectsChange()
        {
            var ini = new FakeIni();
            var store = StoreOver(ini);
            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ingots") });

            var sig = store.Signature();
            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ores") });
            Assert.True(store.DirtySince(sig));
        }

        [Fact]
        public void SignatureIgnoresUnmanagedSections()
        {
            var ini = new FakeIni();
            var store = StoreOver(ini);
            store.MergeSection("Categories", new[] { Kv("Ore/Foo", "Ingots") });
            var sig = store.Signature();

            ini.Set("Reserve:Ammo", "Ingot/Iron", "1000");
            Assert.False(store.DirtySince(sig));
        }

        // MyIni returns a trailing inline comment (key=value ; note) as part of the value; ReadSection must hand the
        // reader a clean value so quota/category parsing doesn't choke on the comment text.
        [Fact]
        public void ReadSectionStripsInlineComment()
        {
            var ini = new FakeIni();
            ini.Set("Reserve:Ammo", "AmmoMagazine/NATO_5p56x45mm", "2000 ; some random comment");
            var store = new ConfigStore(ini, "Reserve:Ammo");

            var read = store.ReadSection("Reserve:Ammo");
            Assert.Equal("2000", read[0].Value);
        }

        [Theory]
        [InlineData("2000 ; some random comment", "2000")]
        [InlineData("500M;no-space-comment", "500M")]
        [InlineData("All", "All")]
        [InlineData("  Ingots  ", "Ingots")]
        [InlineData(null, null)]
        public void StripInlineCommentCases(string value, string expected) =>
            Assert.Equal(expected, ConfigStore.StripInlineComment(value));
    }
}
