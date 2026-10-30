using System.Collections.Generic;

namespace IngameScript
{
    public enum KnowledgeKind { Blueprint, Yield }

    public struct KnowledgeRequest
    {
        public KnowledgeKind Kind;
        public string Key;
        public string Reason;
    }

    public class DiscoveryCoordinator
    {
        KnowledgeRequest active;
        KnowledgeRequest parked;

        public bool HasActive => active.Key != null;
        public KnowledgeRequest Active => active;
        public bool HasParkedBlueprint => parked.Key != null;
        public KnowledgeRequest ParkedBlueprint => parked;

        public void Reconcile(List<string> blueprints, List<string> yields)
        {
            if (active.Key != null && !Contains(active.Kind == KnowledgeKind.Blueprint ? blueprints : yields, active.Key))
                active = default(KnowledgeRequest);
            if (parked.Key != null && !Contains(blueprints, parked.Key)) parked = default(KnowledgeRequest);
            if (active.Key != null) return;
            var key = parked.Key == null ? First(blueprints, null) : null;
            if (key != null) active = new KnowledgeRequest { Kind = KnowledgeKind.Blueprint, Key = key, Reason = key };
            else
            {
                key = First(yields, null);
                if (key != null) active = new KnowledgeRequest { Kind = KnowledgeKind.Yield, Key = key, Reason = key };
            }
        }

        public void ParkBlueprint()
        {
            if (active.Kind != KnowledgeKind.Blueprint || active.Key == null) return;
            parked = active;
            active = default(KnowledgeRequest);
        }

        public void Resolve(KnowledgeKind kind, string key)
        {
            if (active.Kind == kind && active.Key == key) active = default(KnowledgeRequest);
            if (parked.Kind == kind && parked.Key == key) parked = default(KnowledgeRequest);
        }

        public void Reset()
        {
            active = default(KnowledgeRequest);
            parked = default(KnowledgeRequest);
        }

        public bool HasBlueprint(string key) =>
            (active.Kind == KnowledgeKind.Blueprint && active.Key == key)
            || (parked.Kind == KnowledgeKind.Blueprint && parked.Key == key);

        static bool Contains(List<string> keys, string key)
        {
            if (keys == null) return false;
            foreach (var candidate in keys)
                if (candidate == key) return true;
            return false;
        }

        static string First(List<string> keys, string except)
        {
            string first = null;
            if (keys == null) return null;
            foreach (var key in keys)
                if (key != except && (first == null || string.CompareOrdinal(key, first) < 0)) first = key;
            return first;
        }
    }

    public enum BlueprintPhase { Idle, Protecting, Queued, Observing, Manual }
    public enum BlueprintObservation { None, Learned, Manual }

    public class BlueprintProbe
    {
        readonly Dictionary<string, double> baseline = new Dictionary<string, double>();
        readonly Dictionary<string, double> output = new Dictionary<string, double>();
        readonly List<QueueEntry> queue = new List<QueueEntry>();
        int manualCursor;
        long manualAssembler;
        string manualBlueprint;

        public BlueprintPhase Phase;
        public string Item;
        public long AssemblerId;
        public long ReservedAssemblerId => AssemblerId != 0 ? AssemblerId : manualAssembler;
        public string Blueprint;
        public bool NeedsObservation => Phase == BlueprintPhase.Observing || Phase == BlueprintPhase.Manual;
        // The watch needs the item to be the assembler's ONLY queue entry (a second entry makes the output
        // unattributable) and needs that entry to still be there when the next observation tick looks — so ask for
        // a batch, not a single unit a fast assembler would finish first.
        public string Status => Phase == BlueprintPhase.Manual ? "learn " + Item + ": queue some alone on an assembler"
            : Phase == BlueprintPhase.Queued || Phase == BlueprintPhase.Observing ? "learn " + Item + ": probing"
            : Phase == BlueprintPhase.Protecting ? "learn " + Item + ": preparing" : null;

        public void Plan(string item, List<AssemblerSnapshot> assemblers, List<CraftAction> actions)
        {
            if (Item != item || Phase == BlueprintPhase.Idle) Begin(item);
            if (Phase != BlueprintPhase.Protecting) return;
            if (AssemblerId == 0)
            {
                foreach (var snapshot in assemblers)
                {
                    var assembler = snapshot.State;
                    if (!snapshot.QueueEmpty || !assembler.IsReceiver || !assembler.IsWorking || assembler.IsDisassembling
                        || HasAction(actions, assembler.Id)) continue;
                    var candidate = Candidate(item, assembler);
                    if (candidate == null) continue;
                    AssemblerId = assembler.Id;
                    Blueprint = candidate;
                    return;
                }
                Phase = BlueprintPhase.Manual;
                return;
            }

            var selected = Find(assemblers, AssemblerId);
            if (selected.State == null || !selected.QueueEmpty || !selected.State.IsReceiver || !selected.State.IsWorking
                || selected.State.IsDisassembling || HasAction(actions, AssemblerId))
            {
                AssemblerId = 0;
                Blueprint = null;
                return;
            }
            selected.State.OutputInto(baseline);
            actions.Push(new CraftAction { Kind = CraftActionKind.AppendAssembly, Assembler = selected.State, Blueprint = Blueprint, Amount = 1.0, Item = item });
            Phase = BlueprintPhase.Queued;
        }

        public void ProbeExecuted()
        {
            if (Phase == BlueprintPhase.Queued) Phase = BlueprintPhase.Observing;
        }

        public BlueprintObservation Observe(List<IAssemblerState> assemblers, ProductionFacts facts)
        {
            if (Phase == BlueprintPhase.Observing)
            {
                var assembler = Find(assemblers, AssemblerId);
                if (assembler == null) return BlueprintObservation.Manual;
                assembler.OutputInto(output);
                bool ambiguous;
                if (ProducedItem(out ambiguous)) return BlueprintObservation.Learned;
                if (ambiguous) return BlueprintObservation.Manual;
                assembler.QueueInto(queue);
                if (!HasBlueprint(queue, Blueprint)) return BlueprintObservation.Manual;
                return BlueprintObservation.None;
            }
            if (Phase != BlueprintPhase.Manual || assemblers.Count == 0) return BlueprintObservation.None;
            if (manualAssembler != 0)
            {
                var assembler = Find(assemblers, manualAssembler);
                if (assembler == null) { ClearManualWatch(); return BlueprintObservation.None; }
                assembler.OutputInto(output);
                bool ambiguous;
                if (ProducedItem(out ambiguous)) { Blueprint = manualBlueprint; return BlueprintObservation.Learned; }
                if (ambiguous) { ClearManualWatch(); return BlueprintObservation.None; }
                assembler.QueueInto(queue);
                if (!HasBlueprint(queue, manualBlueprint)) ClearManualWatch();
                return BlueprintObservation.None;
            }

            if (manualCursor >= assemblers.Count) manualCursor = 0;
            var next = assemblers[manualCursor++];
            if (next.IsDisassembling) return BlueprintObservation.None;
            next.QueueInto(queue);
            if (queue.Count != 1 || facts.IsBlueprintKnown(queue[0].Blueprint)) return BlueprintObservation.None;
            manualAssembler = next.Id;
            manualBlueprint = queue[0].Blueprint;
            next.OutputInto(baseline);
            return BlueprintObservation.None;
        }

        public bool OwnsOutput(long id) => ReservedAssemblerId != 0 && ReservedAssemblerId == id;

        public void MakeManual()
        {
            AssemblerId = 0;
            Blueprint = null;
            Phase = BlueprintPhase.Manual;
        }

        public void Reset()
        {
            Item = null;
            Blueprint = null;
            AssemblerId = 0;
            Phase = BlueprintPhase.Idle;
            baseline.Empty();
            output.Empty();
            ClearManualWatch();
        }

        void Begin(string item)
        {
            Reset();
            Item = item;
            Phase = BlueprintPhase.Protecting;
        }

        bool ProducedItem(out bool ambiguous)
        {
            ambiguous = false;
            var found = false;
            foreach (var pair in output)
            {
                double before;
                baseline.Get(pair.Key, out before);
                if (pair.Value <= before) continue;
                if (pair.Key != Item) { ambiguous = true; continue; }
                found = true;
            }
            return found && !ambiguous;
        }

        void ClearManualWatch()
        {
            manualAssembler = 0;
            manualBlueprint = null;
            baseline.Empty();
        }

        static string Candidate(string item, IAssemblerState assembler)
        {
            var slash = item.IndexOf('/');
            var subtype = slash >= 0 ? item.Cut(slash + 1) : item;
            if (subtype.Ends("Item"))
            {
                var stripped = subtype.Cut(0, subtype.Length - 4);
                if (assembler.CanUse(stripped)) return stripped;
            }
            var magazine = subtype + "Magazine";
            return assembler.CanUse(magazine) ? magazine : null;
        }

        static bool HasAction(List<CraftAction> actions, long id)
        {
            foreach (var action in actions)
                if (action.Assembler != null && action.Assembler.Id == id) return true;
            return false;
        }

        static bool HasBlueprint(List<QueueEntry> entries, string blueprint)
        {
            foreach (var entry in entries)
                if (entry.Blueprint == blueprint) return true;
            return false;
        }

        static AssemblerSnapshot Find(List<AssemblerSnapshot> assemblers, long id)
        {
            foreach (var assembler in assemblers)
                if (assembler.State.Id == id) return assembler;
            return default(AssemblerSnapshot);
        }

        static IAssemblerState Find(List<IAssemblerState> assemblers, long id)
        {
            foreach (var assembler in assemblers)
                if (assembler.Id == id) return assembler;
            return null;
        }
    }

    public enum YieldPhase { Idle, Draining, Ready, Feeding, Measuring }

    public class YieldSample
    {
        const double AmountEpsilon = 0.1;       // absorbs MyFixedPoint→double drift in the inventory reads

        readonly FeedRequest blocker = new FeedRequest("", "", 0.0);
        readonly List<string> keyScratch = new List<string>();
        FeedRequest request;
        bool stopped;
        double lastConsumed;
        double lastOutputTotal;
        double lastSeconds;
        double maxRate;
        int rateSamples;

        public YieldPhase Phase;
        public string Ore;
        public string Ingot;
        public long RefineryId;
        public bool Active => Phase != YieldPhase.Idle;
        public bool Drains => Phase == YieldPhase.Draining;
        public bool Holds => Phase == YieldPhase.Ready || Phase == YieldPhase.Feeding || Phase == YieldPhase.Measuring;
        public string Status => !Active ? null : Phase == YieldPhase.Draining ? "learn " + Ore + ": draining"
            : Phase == YieldPhase.Ready ? "learn " + Ore + ": preparing"
            : stopped ? "learn " + Ore + ": refinery stopped"
            : Phase == YieldPhase.Feeding ? "learn " + Ore + ": feeding"
            : "learn " + Ore + ": measuring";

        public bool Begin(string ore, string ingot, List<IRefineryFeed> feeds)
        {
            foreach (var feed in feeds)
            {
                if (feed.IsConstrained || !feed.CanTake(ore)) continue;
                Ore = ore;
                Ingot = ingot;
                RefineryId = feed.Id;
                Phase = YieldPhase.Draining;
                feed.Constrain(blocker);
                return true;
            }
            return false;
        }

        public void Bind(IRefineryFeed feed)
        {
            if (!Active || feed.Id != RefineryId) return;
            feed.Constrain(request ?? blocker);
        }

        public List<OreYield> Advance(bool working, double inputItems, double oreInput,
            Dictionary<string, double> outputs, double sampleAmount, double floor, double nowSeconds)
        {
            if (Phase == YieldPhase.Draining)
            {
                if (inputItems <= 0.0 && outputs.Count == 0) Phase = YieldPhase.Ready;
                return null;
            }
            if (Phase == YieldPhase.Ready)
            {
                request = new FeedRequest(Ore, Ingot, sampleAmount);
                Phase = YieldPhase.Feeding;
                return null;
            }
            if (Phase == YieldPhase.Feeding && request.Delivered >= sampleAmount) Phase = YieldPhase.Measuring;
            var consumed = request.Delivered - oreInput;
            Watch(working, consumed, outputs, nowSeconds);
            if (Phase != YieldPhase.Measuring) return null;
            if (consumed < floor || outputs.Count == 0) return null;
            var learned = new List<OreYield>();
            keyScratch.Empty();
            foreach (var key in outputs.Keys) keyScratch.Push(key);
            keyScratch.SortOrdinal();
            foreach (var key in keyScratch)
                if (outputs[key] > 0.0) learned.Push(new OreYield { Output = key, Ratio = outputs[key] / consumed });
            return learned.Count > 0 ? learned : null;
        }

        public double Delivered => request == null ? 0.0 : request.Delivered;

        // The measurement is only trustworthy if the owned refinery went untouched, so every fed/measuring
        // observation is checked against three tamper signals before the numbers are used: consumed
        // (= Delivered − ore left) may never FALL (a fall means ore was inserted); it may not RISE across an
        // interval the refinery was off at both ends (an off refinery consumes nothing — a rise is theft); and it
        // may not rise faster than a generous multiple of the fastest pace this sample has shown (a refinery's
        // pace is near-constant, a manual/automated grab is an instantaneous step). Output may never fall (it is
        // harvest-protected — only an external hand can lower it). Any trip poisons the sample: the caller
        // abandons it unrecorded and a fresh learn re-triggers while the deficit stands. Honest limit: the first
        // two pace observations establish the baseline, so startup ramp-up cannot read as a grab. A grab before
        // that baseline is established slips under the rate guard (the exact checks still apply).
        void Watch(bool working, double consumed, Dictionary<string, double> outputs, double nowSeconds)
        {
            var outputTotal = 0.0;
            foreach (var amount in outputs.Values) outputTotal += amount;
            if (lastSeconds > 0.0)
            {
                var gained = consumed - lastConsumed;
                var seconds = nowSeconds - lastSeconds;
                var disturbed = gained < -AmountEpsilon || outputTotal < lastOutputTotal - AmountEpsilon
                    || gained > AmountEpsilon && !working && stopped;
                if (gained > AmountEpsilon && seconds > 0.0)
                {
                    var rate = gained / seconds;
                    if (rateSamples++ > 1 && rate > maxRate * 3.0) disturbed = true;
                    if (rate > maxRate) maxRate = rate;
                }
                if (disturbed) { Phase = YieldPhase.Idle; return; }
            }
            stopped = !working;
            lastConsumed = consumed;
            lastOutputTotal = outputTotal;
            lastSeconds = nowSeconds;
        }

        public void Reset()
        {
            Phase = YieldPhase.Idle;
            Ore = null;
            Ingot = null;
            RefineryId = 0;
            request = null;
            // Status reads it before the next sample's first observation refreshes it — stale true would flash
            // "refinery stopped" on the Ready→Feeding transition tick.
            stopped = false;
            lastSeconds = 0.0;
            maxRate = 0.0;
            rateSamples = 0;
        }
    }
}
