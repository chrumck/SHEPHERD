using System.Collections.Generic;

namespace IngameScript
{
    // The value on a quota line decodes to one of these:
    //   100  -> Exact  (keep exactly: add below target, remove excess)
    //   100M -> Min    (keep >= target; the surplus leaves only to fill another pool's shortfall)
    //   100L -> Limit  (moves as Exact; with A it only disassembles, never assembles)
    //   All  -> All    (fill with everything available, never remove; Target unused)
    // Limit pulls, unlike the usual remove-only L: a reserve container takes items only through its own pull, so a
    // remove-only quota would sit below target while the grid held plenty.
    public enum QuotaMode { Exact, Min, Limit, All }

    public struct Quota
    {
        public QuotaMode Mode;
        public double Target;
        // A trailing 'A': a flag on top of the mode, which keeps its move semantics.
        public bool AutomaticProduction;

        public static bool TryParse(string value, out Quota quota)
        {
            quota = new Quota { Mode = QuotaMode.Exact };
            if (value == null) return false;
            var s = value.Trim();
            if (s.Length == 0) return false;

            if (s.Is("All"))
            {
                quota = new Quota { Mode = QuotaMode.All };
                return true;
            }

            // Canonical suffix order is <amount>[M|L][A]: peel the trailing A first, then the mode letter, so a
            // reordered ("100AM"), repeated ("100AA"), or "AllA" value (All has no finite production target) falls
            // through to the numeric parse and is rejected.
            var auto = false;
            var last = s[s.Length - 1];
            if (last == 'A' || last == 'a') { auto = true; s = s.Cut(0, s.Length - 1); }
            if (auto && s.Is("All")) return false;

            var mode = QuotaMode.Exact;
            last = s.Length > 0 ? s[s.Length - 1] : ' ';
            if (last == 'M' || last == 'm') { mode = QuotaMode.Min; s = s.Cut(0, s.Length - 1); }
            else if (last == 'L' || last == 'l') { mode = QuotaMode.Limit; s = s.Cut(0, s.Length - 1); }

            // Plain TryParse: SE runs PB scripts under InvariantCulture, so '.' is the decimal separator whatever the
            // server locale.
            double target;
            if (!double.TryParse(s.Trim(), out target)) return false;
            if (target < 0) return false;

            quota = new Quota { Mode = mode, Target = target, AutomaticProduction = auto };
            return true;
        }

        // The display form, in the parser's canonical order <amount>[M|L][A] or All. The amount is compacted the same
        // way as the count beside it on a levels LCD, so a large target never reads as 10k/10000.
        public string Format() => Mode == QuotaMode.All ? "All" : WithLetters(LevelsView.Amount(Target));

        // The value written for a learned amount: the line keeps its mode letter and A flag and only the number
        // changes; an All line has no amount to change (null). Never the compacted display form — 10k does not parse.
        public string WithAmount(double amount) => Mode == QuotaMode.All ? null : WithLetters(amount.ToString("0.##"));

        string WithLetters(string amount) =>
            amount + (Mode == QuotaMode.Min ? "M" : Mode == QuotaMode.Limit ? "L" : "") + (AutomaticProduction ? "A" : "");
    }

    // The count is a per-cycle snapshot kept live within the cycle by TookIn/TookOut, so the intake can self-limit on
    // counts, which volume checks can't.
    public partial class ReservePool
    {
        public readonly string Name;
        // The section's docked=true: the pool's containers live on a ship, so they are expected to come and go. Item
        // keys always contain '/', so this key can't collide with one.
        public const string DockedKey = "docked";
        public bool Docked;
        readonly Dictionary<string, Quota> quotas = new Dictionary<string, Quota>();
        Dictionary<string, double> counts = new Dictionary<string, double>();
        Dictionary<string, double> tally = new Dictionary<string, double>();
        string[] categories = new string[0];

        public ReservePool(string name) { Name = name; }

        public int QuotaCount => quotas.Count;
        public bool TryGetQuota(string key, out Quota quota) => quotas.Get(key, out quota);
        // The concrete dictionary (not an interface view) so a foreach stays a struct enumerator — the production
        // ledger walks every pool's quotas when planning.
        public Dictionary<string, Quota> Quotas => quotas;

        public string[] Categories => categories;

        // Lookups use the prefix-free key, so a hand-written MyObjectBuilder_ key is normalized here or it never matches.
        public void SetQuota(string key, Quota quota) => quotas[Category.ShortTypeId(key)] = quota;

        // Honors the category overrides, else an overridden quota item would route to its override category while the
        // intake sat under the built-in one, and the reserve would never pull it.
        public void RecomputeCategories(CategoryOverrides overrides)
        {
            var set = new List<string>();
            foreach (var key in quotas.Keys)
            {
                string cat;
                if (overrides == null || !overrides.TryGet(key, out cat)) cat = Category.ForKey(key);
                if (!set.Contains(cat)) set.Push(cat);
            }
            categories = set.ToArray();
        }

        // Containers carrying this pool's tag in the current scan; zero means the pool's quotas have nowhere to land.
        public int Containers;

        // Crafting counts every pool's quotas against grid-wide stock, so a pool of only A lines works with no container.
        public bool AllAutomatic()
        {
            foreach (var q in quotas.Values) if (!q.AutomaticProduction) return false;
            return true;
        }

        // An unconfigured pool counts every item present, so its levels LCD can list the keys the user needs for quotas.
        public Dictionary<string, double> Counts => counts;

        // The recount spans several ticks, so it builds aside and swaps in whole: a render never shows a half-counted pool.
        public void BeginTally() => tally.Empty();

        public void Tally(string key, double amount)
        {
            Quota q;
            double have;
            if (quotas.Count == 0 || quotas.Get(key, out q))
                tally[key] = (tally.Get(key, out have) ? have : 0) + amount;
        }

        public void CommitTally()
        {
            var live = counts;
            counts = tally;
            tally = live;
        }

        public void TookIn(string key, double amount)
        {
            double have;
            counts[key] = (counts.Get(key, out have) ? have : 0) + amount;
        }

        public void TookOut(string key, double amount)
        {
            double have;
            var left = (counts.Get(key, out have) ? have : 0) - amount;
            counts[key] = left < 0 ? 0 : left;
        }

        public double Deficit(string key)
        {
            Quota q;
            if (!quotas.Get(key, out q)) return 0;
            if (q.Mode == QuotaMode.All) return double.MaxValue;

            var have = Count(key);
            return have < q.Target ? q.Target - have : 0;
        }

        // A Min surplus counts too, though only another pool's intake may take it: the production ledger sums every
        // pool's targets against grid-wide stock, so a surplus held back here would leave a short pool nothing crafts for.
        public double Excess(string key)
        {
            Quota q;
            if (!quotas.Get(key, out q)) return 0;
            if (q.Mode == QuotaMode.All) return 0;

            var have = Count(key);
            return have > q.Target ? have - q.Target : 0;
        }

        // fraction exceeds 1 when the pool is stocked above quota.
        public bool TryQuotaFill(out double fraction)
        {
            var have = 0.0;
            var target = 0.0;
            foreach (var pair in quotas)
            {
                if (pair.Value.Mode == QuotaMode.All) continue;
                target += pair.Value.Target;
                have += Count(pair.Key);
            }
            fraction = target > 0.0 ? have / target : 0.0;
            return target > 0.0;
        }

        public double Count(string key)
        {
            double have;
            return counts.Get(key, out have) ? have : 0;
        }

        public static string Label(string name) => name.Length == 0 ? "(default)" : name;

        // An All quota pulls every unit into its pool and never lets one go, so two pools All-ing one item can't share
        // it: whichever holds it keeps it, and the other never fills. Nothing else would ever tell the user.
        public static void SharedAll(Dictionary<string, ReservePool> pools, Dictionary<string, string> firstPool, StateChannel into)
        {
            firstPool.Empty();
            foreach (var pool in pools.Values)
                foreach (var pair in pool.quotas)
                {
                    if (pair.Value.Mode != QuotaMode.All) continue;
                    string first;
                    if (!firstPool.Get(pair.Key, out first)) firstPool[pair.Key] = pool.Name;
                    else into.Push("reserves " + Label(first) + " and " + Label(pool.Name) + " both want All of " + pair.Key + " - only one fills");
                }
        }
    }
}
