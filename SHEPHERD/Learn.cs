using System;
using System.Collections.Generic;

namespace IngameScript
{
    // One learned reserve line, ready to write into the user's [Reserve] / [Reserve:Name] section.
    public struct QuotaLine
    {
        public string Section, Key, Value;
    }

    // Infers category overrides and reserve quotas from where the user has put items (learn mode). One sweep is Begin,
    // a Teach call per stack read from a teacher container, then Finish against the current config, which leaves the
    // pending changes and their preview lines. Each sweep starts from nothing, so an item moved away between sweeps
    // simply drops out of the preview. Absence never removes anything: only a placement teaches.
    public partial class PlacementLearner
    {
        // Item key -> the distinct tokens it was found under, its own built-in category included.
        readonly Dictionary<string, List<string>> placed = new Dictionary<string, List<string>>();
        // Item keys found in a container of their own built-in category (that token is also in placed).
        readonly HashSet<string> atBuiltIn = new HashSet<string>();
        // Pool -> item key -> units across the pool's containers.
        readonly Dictionary<string, Dictionary<string, double>> stocked = new Dictionary<string, Dictionary<string, double>>();
        // Pools taught by at least one container on our own construct — the rest live on docked ships.
        readonly HashSet<string> taughtAtHome = new HashSet<string>();
        // Inner collections handed back by Begin, so a sweep every few seconds reuses rather than reallocates them.
        readonly List<List<string>> spareTokens = new List<List<string>>();
        readonly List<Dictionary<string, double>> spareCounts = new List<Dictionary<string, double>>();
        readonly List<string> keys = new List<string>();
        readonly List<string> pools = new List<string>();
        readonly List<string> conflicts = new List<string>();

        public readonly List<KeyValuePair<string, string>> CategorySets = new List<KeyValuePair<string, string>>();
        public readonly List<string> CategoryRemovals = new List<string>();
        public readonly List<QuotaLine> QuotaLines = new List<QuotaLine>();
        // The preview, in display order: category sets, removals, reserve lines, then conflicts.
        public readonly List<string> Lines = new List<string>();

        public bool HasChanges => CategorySets.Count + CategoryRemovals.Count + QuotaLines.Count > 0;

        public void Begin()
        {
            foreach (var list in placed.Values) { list.Empty(); spareTokens.Push(list); }
            placed.Empty();
            atBuiltIn.Empty();
            foreach (var counts in stocked.Values) { counts.Empty(); spareCounts.Push(counts); }
            stocked.Empty();
            taughtAtHome.Empty();
        }

        // A stack of item `key` sits in a single-category container tagged `token`; builtIn is the item's own bucket.
        public void TeachCategory(string token, string key, string builtIn)
        {
            if (token == builtIn) atBuiltIn.Push(key);
            List<string> tokens;
            if (!placed.Get(key, out tokens))
            {
                tokens = Take(spareTokens) ?? new List<string>();
                placed[key] = tokens;
            }
            if (!tokens.Contains(token)) tokens.Push(token);
        }

        public void TeachReserve(string pool, string key, double amount, bool docked = false)
        {
            if (!docked) taughtAtHome.Push(pool);
            Dictionary<string, double> counts;
            if (!stocked.Get(pool, out counts))
            {
                counts = Take(spareCounts) ?? new Dictionary<string, double>();
                stocked[pool] = counts;
            }
            double have;
            counts[key] = (counts.Get(key, out have) ? have : 0.0) + amount;
        }

        public void Finish(CategoryOverrides current, Dictionary<string, ReservePool> config)
        {
            CategorySets.Empty();
            CategoryRemovals.Empty();
            QuotaLines.Empty();
            conflicts.Empty();
            Lines.Empty();
            string was;

            Sorted(placed.Keys, keys);
            foreach (var key in keys)
            {
                var tokens = placed[key];
                // An item in two categories — its own built-in included — is ambiguous, so it teaches nothing until
                // every stack of it sits in one.
                if (tokens.Count > 1) { conflicts.Push("conflict " + key + ": " + string.Join(", ", tokens)); continue; }
                if (atBuiltIn.Contains(key) || current.TryGet(key, out was) && was == tokens[0]) continue;
                CategorySets.Push(new KeyValuePair<string, string>(key, tokens[0]));
                Lines.Push("learn " + key + " -> " + tokens[0]);
            }

            Sorted(atBuiltIn, keys);
            foreach (var key in keys)
            {
                if (placed[key].Count > 1 || !current.TryGet(key, out was)) continue;
                CategoryRemovals.Push(key);
                Lines.Push("learn " + key + " -> built-in");
            }

            Sorted(stocked.Keys, pools);
            foreach (var pool in pools)
            {
                var section = pool.Length == 0 ? "Reserve" : "Reserve:" + pool;
                ReservePool known;
                config.Get(pool, out known);
                // A pool taught only from docked ships is a ship's loadout, so its section says so.
                if (!taughtAtHome.Contains(pool) && (known == null || !known.Docked))
                {
                    QuotaLines.Push(new QuotaLine { Section = section, Key = ReservePool.DockedKey, Value = "true" });
                    Lines.Push("learn " + section + " " + ReservePool.DockedKey + "=true");
                }
                Sorted(stocked[pool].Keys, keys);
                foreach (var key in keys)
                {
                    var amount = stocked[pool][key];
                    // A new key is a plain (exact) line; its stand-in target of -1 can never equal a learned amount.
                    var q = new Quota { Mode = QuotaMode.Exact, Target = -1 };
                    if (known != null && !known.TryGetQuota(key, out q)) q = new Quota { Mode = QuotaMode.Exact, Target = -1 };
                    var value = q.WithAmount(amount);
                    // An amount the line already allows teaches nothing: M is a floor, so more is fine, and L a
                    // ceiling, so less is.
                    if (value == null || value == q.WithAmount(q.Target)
                        || q.Mode == QuotaMode.Min && amount > q.Target || q.Mode == QuotaMode.Limit && amount < q.Target) continue;
                    QuotaLines.Push(new QuotaLine { Section = section, Key = key, Value = value });
                    Lines.Push("learn " + section + " " + key + "=" + value);
                }
            }

            Lines.AddRange(conflicts);
            if (Lines.Count == 0) Lines.Push("learning - nothing to save yet");
        }

        public void Apply(CategoryOverrides overrides)
        {
            foreach (var pair in CategorySets) overrides.Set(pair.Key, pair.Value);
            foreach (var key in CategoryRemovals) overrides.Remove(key);
        }

        static T Take<T>(List<T> spare) where T : class
        {
            if (spare.Count == 0) return null;
            var last = spare[spare.Count - 1];
            spare.RemoveAt(spare.Count - 1);
            return last;
        }

        // Sorted, so the preview doesn't reorder itself from sweep to sweep.
        static void Sorted(IEnumerable<string> from, List<string> into)
        {
            into.Empty();
            into.AddRange(from);
            into.SortOrdinal();
        }
    }
}
