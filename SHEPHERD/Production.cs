using System;
using System.Collections.Generic;

namespace IngameScript
{
    // One refinery output of an ore: Ratio units of Output item per one unit of ore input.
    public struct OreYield
    {
        public string Output;
        public double Ratio;
    }

    // The game-free production-knowledge store: item key -> assembler blueprint subtype (with the reverse map for
    // attributing live queues back to items) and ore key -> refinery output ratios. Vanilla facts are baked in;
    // only learned modded additions persist.
    public class ProductionFacts
    {
        // Most vanilla blueprints follow a naming convention (resolved live, nothing baked): blueprint subtype ==
        // item subtype, or item subtype + "Component". The baked table holds only the exceptions, generated fresh
        // from the game's Content/Data/Blueprints.sbc. Entry: <typeCode><itemSubtype>;<positionDigits>
        // [;<blueprintName>] — every exception blueprint is "Position<digits>_<name>", and <name> defaults to the
        // item subtype minus a trailing "Item", so it is only spelled out where that derivation fails.
        const string BakedBlueprints =
            "KCanvas;0030|CMedkit;0021|CPowerkit;0022|DDatapad;0040|OOxygenBottle;0010|GHydrogenBottle;0020"
            + "|PFlareGunItem;0050|AFlareClip;0051;FlareGunMagazine|AFireworksBoxBlue;0060|AFireworksBoxGreen;0061"
            + "|AFireworksBoxRed;0062|AFireworksBoxYellow;0063|AFireworksBoxPink;0064|AFireworksBoxRainbow;0065"
            + "|PSemiAutoPistolItem;0010|PFullAutoPistolItem;0020|PElitePistolItem;0030;EliteAutoPistol"
            + "|PAutomaticRifleItem;0040|PRapidFireAutomaticRifleItem;0050|PPreciseAutomaticRifleItem;0060"
            + "|PUltimateAutomaticRifleItem;0070|PBasicHandHeldLauncherItem;0080|PAdvancedHandHeldLauncherItem;0090"
            + "|PAngleGrinderItem;0010|PAngleGrinder2Item;0020|PAngleGrinder3Item;0030|PAngleGrinder4Item;0040"
            + "|PHandDrillItem;0050|PHandDrill2Item;0060|PHandDrill3Item;0070|PHandDrill4Item;0080"
            + "|PWelderItem;0090|PWelder2Item;0100|PWelder3Item;0110|PWelder4Item;0120"
            + "|ASemiAutoPistolMagazine;0010|AFullAutoPistolMagazine;0020|AElitePistolMagazine;0030"
            + "|AAutomaticRifleGun_Mag_20rd;0040|ARapidFireAutomaticRifleGun_Mag_50rd;0050"
            + "|APreciseAutomaticRifleGun_Mag_5rd;0060|AUltimateAutomaticRifleGun_Mag_30rd;0070"
            + "|ANATO_25x184mm;0080;NATO_25x184mmMagazine|AAutocannonClip;0090|AMissile200mm;0100"
            + "|AMediumCalibreAmmo;0110|ALargeCalibreAmmo;0120|ASmallRailgunAmmo;0130|ALargeRailgunAmmo;0140";

        // Refinery ratios per one unit of ore, from the same Blueprints.sbc pass (Stone normalized from the
        // per-1000 blueprint). "Ore/" and "Ingot/" prefixes are implied. Ice and Organic are present with ZERO
        // outputs: they yield no ingots (gas / nothing), and baking that fact keeps them KNOWN — never scored,
        // never flagged for yield learning. Entry: <ore>[;<output>:<ratio>[,<output>:<ratio>...]]
        const string BakedYields =
            "Iron;Iron:0.7|Nickel;Nickel:0.4|Cobalt;Cobalt:0.3|Silicon;Silicon:0.7|Magnesium;Magnesium:0.007"
            + "|Silver;Silver:0.1|Gold;Gold:0.01|Platinum;Platinum:0.005|Uranium;Uranium:0.01|Scrap;Iron:0.8"
            + "|Stone;Stone:0.014,Iron:0.03,Nickel:0.0024,Silicon:0.004|Ice|Organic";

        readonly Dictionary<string, string> blueprintOf = new Dictionary<string, string>();
        readonly Dictionary<string, string> itemOf = new Dictionary<string, string>();
        readonly Dictionary<string, List<OreYield>> yields = new Dictionary<string, List<OreYield>>();
        readonly Dictionary<string, string> learnedRecipes = new Dictionary<string, string>();
        readonly Dictionary<string, List<OreYield>> learnedYields = new Dictionary<string, List<OreYield>>();
        readonly List<string> keyScratch = new List<string>();
        bool dirty;

        public ProductionFacts()
        {
            LoadBaked();
        }

        void LoadBaked()
        {
            foreach (var entry in BakedBlueprints.Split('|'))
            {
                var parts = entry.Split(';');
                var subtype = parts[0].Cut(1);
                var name = parts.Length > 2 ? parts[2]
                    : subtype.Ends("Item")
                        ? subtype.Cut(0, subtype.Length - 4) : subtype;
                Remember(TypeOf(parts[0][0]) + "/" + subtype, "Position" + parts[1] + "_" + name);
            }

            foreach (var entry in BakedYields.Split('|'))
            {
                var parts = entry.Split(';');
                var outputs = new List<OreYield>();
                if (parts.Length > 1)
                    foreach (var output in parts[1].Split(','))
                    {
                        var colon = output.IndexOf(':');
                        outputs.Push(new OreYield { Output = "Ingot/" + output.Cut(0, colon), Ratio = double.Parse(output.Cut(colon + 1)) });
                    }
                yields["Ore/" + parts[0]] = outputs;
            }
        }

        public bool TryBlueprint(string itemKey, out string blueprint) =>
            blueprintOf.Get(itemKey, out blueprint);

        // The known map, then the convention candidates — each validated through the caller's probe (live: an
        // assembler's CanUseBlueprint; tests: a mock) and session-cached on success. A failed probe is NOT cached:
        // the candidate may validate later against a different assembler.
        public bool TryResolveBlueprint(string itemKey, Func<string, bool> canUse, out string blueprint)
        {
            if (blueprintOf.Get(itemKey, out blueprint)) return true;
            var slash = itemKey.IndexOf('/');
            var subtype = slash >= 0 ? itemKey.Cut(slash + 1) : itemKey;
            return Probe(itemKey, subtype, canUse, out blueprint)
                || Probe(itemKey, subtype + "Component", canUse, out blueprint);
        }

        // Queue attribution: the known map, then the reverse convention — a convention blueprint always makes a
        // Component, and no vanilla item subtype itself ends in "Component", so the suffix strip is unambiguous.
        // An unknown modded blueprint thus attributes to a phantom Component key; harmless, since a key nothing
        // has a quota for never produces an action.
        public string ItemOf(string blueprint)
        {
            string itemKey;
            if (itemOf.Get(blueprint, out itemKey)) return itemKey;
            var name = blueprint.Length > 9 && blueprint.Ends("Component")
                ? blueprint.Cut(0, blueprint.Length - 9) : blueprint;
            return "Component/" + name;
        }

        public bool TryYields(string oreKey, out List<OreYield> outputs) =>
            yields.Get(oreKey, out outputs);

        public bool IsBlueprintKnown(string blueprint) => itemOf.ContainsKey(blueprint);

        public void Learn(string itemKey, string blueprint)
        {
            string current;
            if (learnedRecipes.Get(itemKey, out current) && current == blueprint) return;
            ForgetReverse(itemKey);
            Remember(itemKey, blueprint);
            learnedRecipes[itemKey] = blueprint;
            dirty = true;
        }

        // Takes ownership of the list; the caller hands over a freshly built one.
        public void LearnYield(string oreKey, List<OreYield> outputs)
        {
            List<OreYield> current;
            if (learnedYields.Get(oreKey, out current) && Same(current, outputs)) return;
            yields[oreKey] = outputs;
            learnedYields[oreKey] = outputs;
            dirty = true;
        }

        public bool LoadRecipe(string itemKey, string blueprint)
        {
            if (!ValidKey(itemKey) || string.IsNullOrWhiteSpace(blueprint)) return false;
            blueprint = blueprint.Trim();
            if (blueprint.IndexOf('/') >= 0) return false;
            ForgetReverse(itemKey);
            Remember(itemKey, blueprint);
            learnedRecipes[itemKey] = blueprint;
            return true;
        }

        public bool LoadYield(string oreKey, string value)
        {
            if (!ValidKey(oreKey) || value == null) return false;
            var parsed = new List<OreYield>();
            foreach (var part in value.Split(','))
            {
                var colon = part.LastIndexOf(':');
                double ratio;
                var output = colon > 0 ? part.Cut(0, colon).Trim() : null;
                if (!ValidKey(output) || colon == part.Length - 1
                    || !double.TryParse(part.Cut(colon + 1).Trim(), out ratio) || ratio <= 0.0)
                    return false;
                foreach (var seen in parsed)
                    if (seen.Output == output) return false;
                parsed.Push(new OreYield { Output = output, Ratio = ratio });
            }
            if (parsed.Count == 0) return false;
            yields[oreKey] = parsed;
            learnedYields[oreKey] = parsed;
            return true;
        }

        public void ClearLearned()
        {
            blueprintOf.Empty();
            itemOf.Empty();
            yields.Empty();
            learnedRecipes.Empty();
            learnedYields.Empty();
            dirty = false;
            LoadBaked();
        }

        public void RecipeEntries(List<KeyValuePair<string, string>> into)
        {
            into.Empty();
            Keys(learnedRecipes.Keys);
            foreach (var key in keyScratch) into.Push(new KeyValuePair<string, string>(key, learnedRecipes[key]));
        }

        public void YieldEntries(List<KeyValuePair<string, string>> into)
        {
            into.Empty();
            Keys(learnedYields.Keys);
            foreach (var key in keyScratch) into.Push(new KeyValuePair<string, string>(key, Format(learnedYields[key])));
        }

        public bool TakeDirty()
        {
            var was = dirty;
            dirty = false;
            return was;
        }

        void Remember(string itemKey, string blueprint)
        {
            blueprintOf[itemKey] = blueprint;
            itemOf[blueprint] = itemKey;
        }

        void ForgetReverse(string itemKey)
        {
            string old, mapped;
            if (blueprintOf.Get(itemKey, out old)
                && itemOf.Get(old, out mapped) && mapped == itemKey) itemOf.Remove(old);
        }

        void Keys(IEnumerable<string> keys)
        {
            keyScratch.Empty();
            foreach (var key in keys) keyScratch.Push(key);
            keyScratch.SortOrdinal();
        }

        static string Format(List<OreYield> outputs)
        {
            var text = new System.Text.StringBuilder();
            foreach (var output in outputs)
            {
                if (text.Length > 0) text.Put(',');
                text.Put(output.Output).Put(':').Put(output.Ratio.ToString("R"));
            }
            return text.ToString();
        }

        static bool Same(List<OreYield> a, List<OreYield> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i].Output != b[i].Output || a[i].Ratio != b[i].Ratio) return false;
            return true;
        }

        static bool ValidKey(string key) => key != null && key.IndexOf('/') > 0;

        bool Probe(string itemKey, string candidate, Func<string, bool> canUse, out string blueprint)
        {
            blueprint = null;
            if (canUse == null || !canUse(candidate)) return false;
            Remember(itemKey, candidate);
            blueprint = candidate;
            return true;
        }

        static string TypeOf(char code)
        {
            switch (code)
            {
                case 'K': return "Component";
                case 'P': return "PhysicalGunObject";
                case 'A': return "AmmoMagazine";
                case 'C': return "ConsumableItem";
                case 'O': return "OxygenContainerObject";
                case 'G': return "GasContainerObject";
                default: return "Datapad";
            }
        }
    }
}
