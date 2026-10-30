using System;
using System.Collections.Generic;

namespace IngameScript
{
    // The built-in category tokens. These strings are the contract: what a user types in a [SHEPHERD:...] tag
    // and what the routing index keys on, so a category name IS its tag token.
    public static class Categories
    {
        public const string Ores = "Ores";
        public const string Ingots = "Ingots";
        public const string Components = "Components";
        public const string Tools = "Tools";
        public const string Ammo = "Ammo";
        public const string Consumables = "Consumables";
        public const string Others = "Others";

        // In display order — the status LCD's per-category fill readout lists these first, then the custom ones.
        public static readonly string[] All = { Ores, Ingots, Components, Tools, Ammo, Consumables, Others };

        // Collects the custom categories (tokens that are not built-ins) for the status rows, each once.
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

        // The stable per-item key used by reserve quotas and (later) the persisted stores: ShortTypeId/SubtypeId,
        // e.g. Ingot/Iron, AmmoMagazine/NATO_5p56x45mm.
        public static string Key(string typeId, string subtypeId) => ShortTypeId(typeId) + "/" + subtypeId;

        // Asked for every stack the sort examines, and the type half costs a Substring, so each key is worked out
        // once. Static is safe: it holds strings, never a block, and a world has a bounded set of item types.
        static readonly Dictionary<string, string> builtIns = new Dictionary<string, string>();

        // The built-in bucket of an item key (Key form, TypeId/SubtypeId).
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

        // The routing category of an item, honoring the [Categories] overrides (learned or hand-written): a per-item
        // override wins over the coarse built-in bucket. The override token may be a built-in or a custom category —
        // routing keys on the string either way, so a custom token routes to a [SHEPHERD:token] container.
        public static string ForKey(string key, CategoryOverrides overrides)
        {
            string cat;
            return overrides != null && overrides.TryGet(key, out cat) ? cat : ForKey(key);
        }

        // The coarse built-in bucket, decided by TypeId alone. Subtype refinement is the learned override map's
        // job (the override-aware ForKey), deliberately not this — the built-in buckets stay coarse.
        // Unknown TypeId -> Others.
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

        // A [Categories] override line is TypeId/SubtypeId=Category: a key with a type before the '/' and a
        // non-empty token. The token is not checked against the built-ins — a custom category is valid.
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

    // The in-memory category-override map ([Categories], learned or hand-written): item key (Category.Key) -> category token, which
    // may be a built-in or a custom token. Seeded from the persisted [Categories] section at load and consulted
    // ahead of the coarse bucket when routing. Pure (no game types) so it is offline-tested; the source of
    // truth is memory, disk is a mirror written on change.
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
