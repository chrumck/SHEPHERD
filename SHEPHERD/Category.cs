using System;
using System.Collections.Generic;

namespace IngameScript
{
    // These strings are the contract: a category name is both its tag token and its routing key.
    public static class Categories
    {
        public const string Ores = "Ores";
        public const string Ingots = "Ingots";
        public const string Components = "Components";
        public const string Tools = "Tools";
        public const string Ammo = "Ammo";
        public const string Consumables = "Consumables";
        public const string Others = "Others";

        // Display order: the status LCD lists these first, then the custom ones.
        public static readonly string[] All = { Ores, Ingots, Components, Tools, Ammo, Consumables, Others };

        public static void AddCustom(List<string> into, string token)
        {
            if (string.IsNullOrEmpty(token) || into.Contains(token)) return;
            foreach (var builtIn in All) if (builtIn == token) return;
            into.Push(token);
        }
    }

    public static class Category
    {
        public const string BuilderPrefix = "MyObjectBuilder_";

        public static string ShortTypeId(string typeId) =>
            string.IsNullOrEmpty(typeId) ?
                "" :
                typeId.Starts(BuilderPrefix) ?
                typeId.Cut(BuilderPrefix.Length) :
                typeId;

        // The per-item key of the quotas and the persisted sections, so its form must stay stable.
        public static string Key(string typeId, string subtypeId) => ShortTypeId(typeId) + "/" + subtypeId;

        // Asked for every stack the sort examines, and the type half costs a Substring, so each key is worked out
        // once. Static is safe: it holds strings, never a block, and a world has a bounded set of item types.
        static readonly Dictionary<string, string> builtIns = new Dictionary<string, string>();

        public static string ForKey(string key)
        {
            string cat;
            if (!builtIns.Get(key, out cat))
            {
                var slash = key.IndexOf('/');
                builtIns[key] = cat = For(slash >= 0 ? key.Cut(0, slash) : key);
            }
            return cat;
        }

        // Routing keys on the string, so a custom override token routes to a [SHEPHERD:token] container.
        public static string ForKey(string key, CategoryOverrides overrides)
        {
            string cat;
            return overrides != null && overrides.TryGet(key, out cat) ? cat : ForKey(key);
        }

        // Deliberately coarse, by TypeId alone: subtype refinement is the override map's job.
        public static string For(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return Categories.Others;

            var t = ShortTypeId(typeId);

            switch (t)
            {
                case "Ore": return Categories.Ores;
                case "Ingot": return Categories.Ingots;
                case "Component": return Categories.Components;
                case "PhysicalGunObject": return Categories.Tools;
                case "AmmoMagazine": return Categories.Ammo;
                case "ConsumableItem": return Categories.Consumables;
                // Gas/oxygen bottles group with hand equipment; no separate bottle bucket.
                case "GasContainerObject": return Categories.Tools;
                case "OxygenContainerObject": return Categories.Tools;
                default: return Categories.Others;
            }
        }

        // The token is not checked against the built-ins: a custom category is valid.
        public static bool IsValidOverride(string key, string value) =>
            key != null && key.IndexOf('/') > 0
            && !string.IsNullOrEmpty(value) && value.Trim().Length > 0;

        // A cheap prefilter for the bottle sweep, so it doesn't offer every tank each unrelated item just to find
        // bottles. Takes a TypeId or an item key.
        public static bool IsBottle(string typeId)
        {
            var t = ShortTypeId(typeId);
            return t.Starts("GasContainerObject") || t.Starts("OxygenContainerObject");
        }
    }

    // Memory is the source of truth; the [Categories] section is a mirror written on change.
    public partial class CategoryOverrides
    {
        readonly Dictionary<string, string> map = new Dictionary<string, string>();

        public IEnumerable<KeyValuePair<string, string>> Entries => map;

        // Lookups use the prefix-free key, so a hand-written MyObjectBuilder_ key is normalized here or it never matches.
        public void Set(string key, string category) => map[Category.ShortTypeId(key)] = category;
        public bool Remove(string key) => map.Remove(key);
        public void Clear() => map.Empty();
        public bool TryGet(string key, out string category) => map.Get(key, out category);
    }
}
