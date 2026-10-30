using System;
using System.Collections.Generic;

namespace IngameScript
{
    // One demand-first refining assignment: feed Ore, up to the remaining amount, to fill the Ingot deficit that
    // motivated it. Doubles as the constrained feed's claim ledger on the sort path — Allows filters the feed to
    // the selected ore, Cap/Took book each moved amount so the request can never over-feed within its cycle.
    // Dies with the cycle (the feeds it constrains are rebuilt every scan), so no recovery is ever needed.
    public class FeedRequest
    {
        public readonly string Ore;
        public readonly string Ingot;
        double remaining;
        double delivered;

        public FeedRequest(string ore, string ingot, double amount)
        {
            Ore = ore;
            Ingot = ingot;
            remaining = amount;
        }

        public double Remaining => remaining;
        public double Delivered => delivered;
        public bool Allows(string key) => remaining > 0.0 && key == Ore;
        public double Claim(double want)
        {
            var units = want < remaining ? want : remaining;
            remaining -= units;
            return units;
        }

        public void Complete(double claimed, double moved)
        {
            delivered += moved;
            remaining += claimed - moved;
        }
    }

    // The refinery view the refine planner commands (live: the refinery's MachineFeed; tests: a mock). CanTake is
    // the compatibility-and-room probe; Constrain hands the feed its assignment for this cycle. A constrained feed
    // takes ONLY the requested ore, capped — so demand-first work and greedy backfill never command the same
    // refinery, and the planner assigning each feed at most once per plan is the whole ownership rule.
    public interface IRefineryFeed
    {
        long Id { get; }
        string Name { get; }
        bool IsConstrained { get; }
        bool CanTake(string oreKey);
        void Constrain(FeedRequest request);
    }

    // The game-free Refine planner: one pass per cycle over the filled ledger and the cycle's refinery feeds,
    // choosing which known ore each free refinery should refine to fill the A-enabled ingot deficits. Deficits are
    // already net of stock, queued work, and the expected output of ore sitting in refinery inputs (the ledger's
    // math), so a request in flight suppresses re-requesting — write-on-change by construction. Ores are scored
    // across every live deficit (multi-output ores count each), highest score first, ties by ordinal key order.
    public class RefinePlanner
    {
        readonly List<FeedRequest> requests = new List<FeedRequest>();
        readonly List<string> needYield = new List<string>();
        readonly List<string> blocked = new List<string>();
        readonly List<string> keyScratch = new List<string>();
        readonly List<string> deficitKeys = new List<string>();
        readonly List<double> deficits = new List<double>();        // parallel to deficitKeys
        readonly List<string> oreKeys = new List<string>();
        readonly List<double> oreAvail = new List<double>();        // parallel to oreKeys
        readonly List<string> unknownOres = new List<string>();
        readonly List<string> underSampled = new List<string>();
        readonly List<IRefineryFeed> freeFeeds = new List<IRefineryFeed>();

        public static string NoRefineryLine(string oreKey) => "refine " + oreKey + ": no refinery";
        public static string NeedOreLine(double sample, string oreKey) => "learn " + oreKey + ": need " + sample + " available";

        public List<FeedRequest> Requests => requests;
        public List<string> NeedYield => needYield;
        // Unknown-yield ores a live deficit wants sampled but whose available stock is below the sample target —
        // the caller withholds these from open greedy feeds so the stock can accumulate instead of trickling
        // into refinery inputs (ledger-protected there, so the sample bar would never be reached).
        public List<string> UnderSampled => underSampled;
        // Pre-formatted diagnostic lines (no room / no refinery / no ore) — recomputed per plan, so they persist
        // on the status LCD while blocked and the transition log emits each only on entry.
        public List<string> Blocked => blocked;
        public string YieldReason;

        public void Plan(ProductionLedger ledger, ProductionFacts facts, List<IRefineryFeed> feeds, Func<string, bool> outputHasRoom)
        {
            Plan(ledger, facts, feeds, outputHasRoom, 0.0);
        }

        public void Plan(ProductionLedger ledger, ProductionFacts facts, List<IRefineryFeed> feeds,
            Func<string, bool> outputHasRoom, double yieldSample)
        {
            requests.Empty();
            needYield.Empty();
            blocked.Empty();
            underSampled.Empty();
            freeFeeds.Empty();
            YieldReason = null;
            if (feeds != null)
                foreach (var feed in feeds)
                    if (!feed.IsConstrained) freeFeeds.Push(feed);
            Collect(ledger, facts, outputHasRoom, yieldSample);

            // Each round either consumes a feed (a request was issued) or zeroes an ore (no refinery can take
            // it), so the bound is exact, never a spin.
            for (var guard = freeFeeds.Count + oreKeys.Count; guard > 0; guard--)
            {
                var o = BestOre(facts);
                if (o < 0) break;
                var ore = oreKeys[o];
                List<OreYield> outputs;
                facts.TryYields(ore, out outputs);

                // Amount covers the largest per-output need (a multi-output ore's incidental extra ingots are
                // physically unavoidable and just get stored); the reason is the biggest deficit it serves.
                var amount = 0.0;
                var reasonDeficit = 0.0;
                string reason = null;
                foreach (var y in outputs)
                {
                    var d = DeficitOf(y.Output);
                    if (d <= 0.0) continue;
                    var need = Math.Ceiling(d / y.Ratio);
                    if (need > amount) amount = need;
                    if (d > reasonDeficit) { reasonDeficit = d; reason = y.Output; }
                }
                if (amount > oreAvail[o]) amount = oreAvail[o];

                var feed = TakeFeed(ore);
                if (feed == null) { blocked.Push(NoRefineryLine(ore)); oreAvail[o] = 0.0; continue; }
                var request = new FeedRequest(ore, reason, amount);
                feed.Constrain(request);
                requests.Push(request);
                oreAvail[o] -= amount;
                foreach (var y in outputs) ReduceDeficit(y.Output, amount * y.Ratio);
            }
            FinishBlocked(yieldSample);
        }

        // Split the ledger's keys into the active ingot deficits (dropping any whose output has nowhere to land —
        // refining into a jammed grid just strands ingots in outputs) and the consumable ore supply, known-yield
        // and unknown apart. Below one whole ingot is the deadband: at target, not a work order.
        void Collect(ProductionLedger ledger, ProductionFacts facts, Func<string, bool> outputHasRoom, double yieldSample)
        {
            deficitKeys.Empty();
            deficits.Empty();
            oreKeys.Empty();
            oreAvail.Empty();
            unknownOres.Empty();
            ledger.CollectKeys(keyScratch);
            foreach (var key in keyScratch)
            {
                var d = ledger.RefineDeficit(key);
                if (d >= 1.0)
                {
                    if (outputHasRoom != null && !outputHasRoom(key))
                    {
                        blocked.Push("refine " + key + ": output has no room");
                        continue;
                    }
                    deficitKeys.Push(key);
                    deficits.Push(d);
                    continue;
                }
                if (!key.Starts("Ore/")) continue;
                var avail = ledger.Available(key);
                if (avail <= 0.0) continue;
                List<OreYield> outputs;
                if (facts.TryYields(key, out outputs)) { oreKeys.Push(key); oreAvail.Push(avail); }
                else if (avail >= yieldSample) unknownOres.Push(key);
                else if (yieldSample > 0.0) underSampled.Push(key);
            }
        }

        // The highest-scoring consumable known ore: score = over its outputs, the deficit actually closed by
        // feeding everything available (min of possible gain and remaining deficit). Ties break by ordinal key
        // order so the choice is stable across cycles, never a churn between equal ores.
        int BestOre(ProductionFacts facts)
        {
            var best = -1;
            var bestScore = 0.0;
            for (var o = 0; o < oreKeys.Count; o++)
            {
                if (oreAvail[o] <= 0.0) continue;
                List<OreYield> outputs;
                facts.TryYields(oreKeys[o], out outputs);
                var score = 0.0;
                foreach (var y in outputs)
                {
                    var d = DeficitOf(y.Output);
                    if (d <= 0.0) continue;
                    var gain = y.Ratio * oreAvail[o];
                    score += gain < d ? gain : d;
                }
                if (score <= 0.0) continue;
                if (score > bestScore
                    || (score == bestScore && string.CompareOrdinal(oreKeys[o], oreKeys[best]) < 0))
                {
                    best = o;
                    bestScore = score;
                }
            }
            return best;
        }

        IRefineryFeed TakeFeed(string oreKey)
        {
            for (var f = 0; f < freeFeeds.Count; f++)
            {
                var feed = freeFeeds[f];
                if (!feed.CanTake(oreKey)) continue;
                freeFeeds.RemoveAt(f);
                return feed;
            }
            return null;
        }

        // A deficit left uncovered is a blocked state worth telling the user about — but only when a refinery is
        // still free to act: with none free the no-refinery line already names the real bottleneck, and asking for
        // yield learning would be noise (learning needs a refinery too). Unknown ore with a full sample means the
        // answer is a yield; a short sample says how much to add; otherwise there is simply no ore. UnderSampled
        // holds its protection only while a deficit stands — without demand, unknown ores feed freely.
        void FinishBlocked(double sampleNeed)
        {
            var remaining = false;
            for (var i = 0; i < deficits.Count && !remaining; i++) remaining = deficits[i] >= 1.0;
            if (!remaining) { underSampled.Empty(); return; }
            if (freeFeeds.Count == 0) return;
            var noCandidates = unknownOres.Count == 0 && underSampled.Count == 0;
            for (var i = 0; i < deficits.Count; i++)
            {
                if (deficits[i] < 1.0) continue;
                if (YieldReason == null) YieldReason = deficitKeys[i];
                if (noCandidates) blocked.Push("refine " + deficitKeys[i] + ": no ore");
            }
            foreach (var ore in underSampled) blocked.Push(NeedOreLine(sampleNeed, ore));
            foreach (var ore in unknownOres)
            {
                var compatible = false;
                foreach (var feed in freeFeeds)
                    if (feed.CanTake(ore)) { compatible = true; break; }
                if (compatible) needYield.Push(ore);
                else blocked.Push(NoRefineryLine(ore));
            }
        }

        double DeficitOf(string key)
        {
            for (var i = 0; i < deficitKeys.Count; i++)
                if (deficitKeys[i] == key) return deficits[i];
            return 0.0;
        }

        void ReduceDeficit(string key, double by)
        {
            for (var i = 0; i < deficitKeys.Count; i++)
                if (deficitKeys[i] == key) { deficits[i] -= by; return; }
        }
    }
}
