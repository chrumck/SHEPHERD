using System;
using System.Collections.Generic;

namespace IngameScript
{
    // The craft planner's whole output vocabulary. Append/restore, plus one clear that only ever targets the tracked
    // disassembler (taken over empty) once its queue has stalled — so a user's own queue can only grow or complete.
    // StopCooperating drains the Coop picked to disassemble; the mark check turns its switch back on once released.
    // Trim takes back only units SHEPHERD queued itself.
    public enum CraftActionKind { AppendAssembly, SetDisassembly, AppendDisassembly, RestoreAssembly, ClearQueue, StopCooperating, Trim }

    public struct CraftAction
    {
        public readonly CraftActionKind Kind;
        public readonly IAssemblerState Assembler;
        public readonly string Blueprint;
        public readonly double Amount;
        public readonly string Item;    // the item key the action serves — diagnostics only

        public CraftAction(CraftActionKind kind, IAssemblerState assembler, string blueprint, double amount, string item)
        {
            Kind = kind;
            Assembler = assembler;
            Blueprint = blueprint;
            Amount = amount;
            Item = item;
        }
    }

    // The game-free Craft planner: one pass per cycle over the filled ledger and the walk's assembler snapshots,
    // producing a bounded action list plus the blocked-demand outputs (NeedBlueprint feeds status and blueprint
    // discovery). Deficits/surpluses are already net of queued work and capped by authorization (the ledger's
    // math), so a queue that covers its demand plans no action at all — write-on-change by construction. A mode
    // change is only ever emitted for an empty assembler, and only the one tracked disassembler is ever restored.
    public sealed class CraftPlanner
    {
        readonly List<CraftAction> actions = new List<CraftAction>();
        readonly List<string> needBlueprint = new List<string>();
        readonly List<string>[] unassigned = { new List<string>(), new List<string>(), new List<string>(), new List<string>(),
            new List<string>(), new List<string>(), new List<string>(), new List<string>() };
        readonly List<string> heldAssembly = new List<string>();
        readonly List<string> heldDisassembly = new List<string>();
        readonly List<string> keyScratch = new List<string>();
        readonly List<int> modelOf = new List<int>();               // parallel to the snapshots
        readonly List<IAssemblerState> modelProbe = new List<IAssemblerState>();
        readonly List<int> modelVerdict = new List<int>();          // per model, per key: 0 unprobed, 1 no blueprint, 2 can't use, 3 usable
        readonly List<double> plannedLoad = new List<double>();     // parallel to the snapshots
        readonly List<double> weight = new List<double>();          // parallel: own speed + a Main's share of the Coops'
        readonly List<double> crewLoad = new List<double>();        // parallel: a Main's share of the Coops' queued units
        readonly List<int> candidates = new List<int>();
        const double StallSeconds = 120.0;
        const double MinShare = 5.0;
        long observedDisassembler;
        double observedQueueAmount;
        double queueProgressTime;
        IAssemblerState drained;    // the tracked Coop once it is empty with its switch off, until it gets surplus
        bool homeHasMain;
        readonly List<QueueClaim> claims = new List<QueueClaim>();
        readonly List<QueueEntry> queueScratch = new List<QueueEntry>();

        // Units SHEPHERD itself queued on one assembler, per blueprint, so a trim only ever takes back its own work.
        // Building and a Coop's batch take from the front of the queue, so each drop in the assembler's units of the
        // blueprint uses up the units queued Ahead of SHEPHERD's before its own: the player's units queued later sit
        // behind and never keep the claim alive once its own are built. Last is that count as last seen. A player's
        // append in the same plan as a drop hides the drop, which can leave one cycle's building in the claim. Not
        // persisted: after a recompile nothing is trimmed until SHEPHERD queues again, which errs toward leaving
        // work alone.
        sealed class QueueClaim
        {
            public long Id;
            public string Key, Blueprint;
            public double Units, Ahead, Last;
            public bool Seen;
        }

        // The one assembler SHEPHERD switched to Disassembly (0 = none). Persisted by Program in the managed
        // [Production] section so a recompile can distinguish our disassembler from a user's deliberate
        // Disassembly mode; re-seeded from disk at startup/configReload.
        public long TrackedDisassembler;

        public List<CraftAction> Actions => actions;
        public List<string> NeedBlueprint => needBlueprint;
        // Keys with work no assembler took, indexed by why: NoMain (no assembler takes orders), MainOff (none of those
        // is operational), MainBusy (all operational ones disassemble), NoAssembler (none of them can make it — or, for
        // a surplus, none is free to take it over). Surplus keys sit Disassembly further on.
        public List<string>[] Unassigned => unassigned;
        public const int NoMain = 0, MainOff = 1, MainBusy = 2, NoAssembler = 3, Disassembly = 4;
        public List<string> HeldAssembly => heldAssembly;
        public List<string> HeldDisassembly => heldDisassembly;
        public string StalledQueueStatus { get; private set; }

        public void Plan(ProductionLedger ledger, ProductionFacts facts, List<AssemblerSnapshot> assemblers)
        {
            Plan(ledger, facts, assemblers, 0, 0);
        }

        public void Plan(ProductionLedger ledger, ProductionFacts facts, List<AssemblerSnapshot> assemblers,
            long unavailableAssembler, double nowSeconds)
        {
            actions.Clear();
            needBlueprint.Clear();
            foreach (var u in unassigned) u.Clear();
            heldAssembly.Clear();
            heldDisassembly.Clear();
            GroupModels(assemblers);
            ReconcileClaims(ledger, facts, assemblers);
            // The game gives a cooperative assembler work only in Assembly mode, so a Coop left in Disassembly
            // (re-tagged mid-run, or set by hand) would idle for good; once its own queue is done it goes back.
            foreach (var s in assemblers)
            {
                var st = s.State;
                if (IsHomeCoop(st) && st.IsDisassembling && s.QueueEmpty
                    && st.Id != TrackedDisassembler && st.Id != unavailableAssembler)
                    actions.Add(new CraftAction(CraftActionKind.RestoreAssembly, st, null, 0, null));
            }
            var released = ReconcileTracked(assemblers, nowSeconds);
            ledger.CollectKeys(keyScratch);
            foreach (var key in keyScratch)
            {
                // Below one whole unit is the deadband: queue items are integral, so a fractional remainder is
                // "at target". Deficit and surplus are mutually exclusive per key (same floor/cap arithmetic) —
                // but a queue in the OPPOSITE direction can coexist with either (a stale order the vanished stock
                // no longer justifies, or a user's own), and planning against it feeds one order into the other.
                // Each direction holds while the opposite queue stands; held keys are surfaced, not silent.
                var deficit = ledger.AssemblyDeficit(key);
                if (deficit >= 1.0)
                {
                    if (ledger.Outgoing(key) > 0) heldAssembly.Add(key);
                    else PlanAssembly(key, Math.Floor(deficit), facts, assemblers, unavailableAssembler);
                    continue;
                }
                var surplus = ledger.DisassemblySurplus(key);
                if (surplus < 1.0) continue;
                if (ledger.Incoming(key) > 0) heldDisassembly.Add(key);
                else PlanDisassembly(key, Math.Floor(surplus), facts, assemblers, released, unavailableAssembler);
            }
            // A drained Coop no surplus wanted any more goes back to helping; the mark check turns its switch on.
            if (drained != null) TrackedDisassembler = 0;
        }

        static bool IsHomeCoop(IMarkedAssembler a) => a.Construct == 0 && a.Mark == AssemblerMark.Coop && a.CanCooperate;

        // The count is read before the append, so the new units are known to sit behind everything already queued.
        public void Append(IAssemblerState a, string key, string blueprint, double units)
        {
            double removable;
            var before = Queued(a, blueprint, out removable);
            a.Append(blueprint, units);
            var c = Find(a.Id, blueprint);
            if (c == null) claims.Add(c = new QueueClaim { Id = a.Id, Key = key, Blueprint = blueprint, Ahead = before, Last = before });
            else Settle(c, before);
            c.Units += units;
            c.Last += units;
        }

        public void Release(IAssemblerState a, string blueprint, double units)
        {
            var c = Find(a.Id, blueprint);
            if (c == null) return;
            c.Units -= units;
            c.Last -= units;
        }

        QueueClaim Find(long id, string blueprint)
        {
            foreach (var c in claims) if (c.Id == id && c.Blueprint == blueprint) return c;
            return null;
        }

        static void Settle(QueueClaim c, double held)
        {
            var drop = c.Last - held;
            if (drop > 0)
            {
                var ahead = Math.Min(drop, c.Ahead);
                c.Ahead -= ahead;
                c.Units -= drop - ahead;
            }
            c.Last = held;
            if (c.Units > held) c.Units = held;
        }

        // Excess is trimmed only once two plans in a row see it: one snapshot can catch a stack mid-move, or a Coop's
        // batch in both its queue and its Main's. An off assembler's assembly queue isn't counted as incoming, so the
        // excess says nothing about it; its disassembly queue is still counted as outgoing.
        void ReconcileClaims(ProductionLedger ledger, ProductionFacts facts, List<AssemblerSnapshot> assemblers)
        {
            // The tracked disassembler was taken over empty, so its whole queue is SHEPHERD's — after a recompile too,
            // which forgets the claims while that queue keeps pulling the item through the conveyor, quota stock included.
            long whole = 0;
            foreach (var s in assemblers)
            {
                if (s.State.Id != TrackedDisassembler || !s.State.IsDisassembling) continue;
                whole = s.State.Id;
                s.State.QueueInto(queueScratch);
                foreach (var e in queueScratch)
                    if (Find(whole, e.Blueprint) == null)
                        claims.Add(new QueueClaim { Id = whole, Key = facts.ItemOf(e.Blueprint), Blueprint = e.Blueprint });
            }
            for (var c = claims.Count - 1; c >= 0; c--)
            {
                var claim = claims[c];
                IAssemblerState st = null;
                foreach (var s in assemblers) if (s.State.Id == claim.Id) st = s.State;
                double removable = 0;
                var held = st == null ? 0 : Queued(st, claim.Blueprint, out removable);
                Settle(claim, held);
                if (claim.Id == whole) claim.Units = held;
                if (claim.Units < 1) { claims.RemoveAt(c); continue; }
                var excess = ledger.QueuedExcess(claim.Key, st.IsDisassembling);
                // Runs before anything else is planned, so every action so far is an earlier claim's trim.
                foreach (var a in actions) if (a.Item == claim.Key) excess -= a.Amount;
                var due = claim.Seen;
                claim.Seen = excess >= 1 && (st.IsDisassembling || st.IsOperational);
                var take = Math.Min(Math.Floor(excess), Math.Min(claim.Units, Math.Floor(removable)));
                if (due && claim.Seen && take >= 1) actions.Add(new CraftAction(CraftActionKind.Trim, st, claim.Blueprint, take, claim.Key));
            }
        }

        // An assembler's units of one blueprint, and how many one trim can take: the newest such entry, less the unit
        // in production. Re-read only for an assembler holding SHEPHERD's own work, since the snapshot keeps a total.
        double Queued(IAssemblerState a, string blueprint, out double removable)
        {
            a.QueueInto(queueScratch);
            var total = 0.0;
            removable = 0;
            for (var i = 0; i < queueScratch.Count; i++)
            {
                if (queueScratch[i].Blueprint != blueprint) continue;
                total += queueScratch[i].Amount;
                removable = queueScratch[i].Amount - (i == 0 ? 1 : 0);
            }
            return total;
        }

        // A release waits one cycle before re-acquisition to prevent two opposing mode changes in one plan. A queue that
        // stops moving waits on items that are gone, so it is cleared: the next plan restores the assembler and
        // re-queues whatever surplus still stands.
        // A Coop picked while still in Assembly mode is draining: with its switch off it takes no more batches from
        // the Main, so once its own is done it stays empty and can switch mode without touching anyone's work.
        bool ReconcileTracked(List<AssemblerSnapshot> assemblers, double nowSeconds)
        {
            StalledQueueStatus = null;
            drained = null;
            if (TrackedDisassembler == 0) { observedDisassembler = 0; return false; }
            foreach (var s in assemblers)
            {
                var st = s.State;
                if (st.Id != TrackedDisassembler) continue;
                // Re-marked away from taking orders: the player took it over, and Manual means no mode changes.
                if (!MarkCheck.TakesOrders(st) && !IsHomeCoop(st)) break;
                if (!st.IsDisassembling && IsHomeCoop(st))
                {
                    if (!st.IsOperational) break;
                    if (st.Cooperative) actions.Add(new CraftAction(CraftActionKind.StopCooperating, st, null, 0, null));
                    else if (s.QueueEmpty) drained = st;
                    return false;
                }
                if (!s.QueueEmpty)
                {
                    // In Assembly mode the queue is the player's assembly, not our stalled order.
                    if (!st.IsDisassembling) return false;
                    if (observedDisassembler != TrackedDisassembler || observedQueueAmount != s.QueueAmount)
                    {
                        observedDisassembler = TrackedDisassembler;
                        observedQueueAmount = s.QueueAmount;
                        queueProgressTime = nowSeconds;
                    }
                    else if (nowSeconds - queueProgressTime >= StallSeconds)
                    {
                        actions.Add(new CraftAction(CraftActionKind.ClearQueue, st, null, 0, null));
                        StalledQueueStatus = "clearing stalled disassembly queue on " + st.Name;
                        observedDisassembler = 0;
                    }
                    return false;
                }
                actions.Add(new CraftAction(CraftActionKind.RestoreAssembly, st, null, 0, null));
                TrackedDisassembler = 0;
                observedDisassembler = 0;
                return true;
            }
            TrackedDisassembler = 0;
            observedDisassembler = 0;
            return false;
        }

        // Assemblers of one model accept the same blueprints, so compatibility is probed once per model per key
        // rather than once per assembler — the probe parses a definition id, and a big base multiplies it.
        // A working Coop takes small batches from whichever reachable Main has a queue, picked at random — docked
        // ones across the connector too — so the Coops are one pool shared evenly by every Main: each Main's weight
        // and queue include its share of the pool's speed and queued units. Whether a Coop can make a given item
        // isn't probed, so a pool of mixed models is weighted roughly. The tracked disassembler helps no one.
        void GroupModels(List<AssemblerSnapshot> assemblers)
        {
            modelOf.Clear();
            modelProbe.Clear();
            plannedLoad.Clear();
            double poolSpeed = 0, poolQueue = 0;
            var mains = 0;
            homeHasMain = false;
            foreach (var s in assemblers)
            {
                var st = s.State;
                var model = st.Model;
                var m = 0;
                while (m < modelProbe.Count && modelProbe[m].Model != model) m++;
                if (m == modelProbe.Count) modelProbe.Add(st);
                modelOf.Add(m);
                plannedLoad.Add(s.QueueAmount);
                if (st.Mark == AssemblerMark.Coop && st.CanCooperate)
                {
                    if (!st.IsOperational || st.IsDisassembling || st.Id == TrackedDisassembler) continue;
                    poolSpeed += s.Speed;
                    poolQueue += s.QueueAmount;
                }
                else if (st.Mark == AssemblerMark.Main && st.IsReceiver)
                {
                    mains++;
                    homeHasMain |= st.Construct == 0;
                }
            }
            weight.Clear();
            crewLoad.Clear();
            foreach (var s in assemblers)
            {
                var share = s.State.Mark == AssemblerMark.Main && s.State.IsReceiver ? 1.0 / mains : 0;
                weight.Add(s.Speed + poolSpeed * share);
                crewLoad.Add(poolQueue * share);
            }
        }

        // A deficit is spread so every compatible assembler would finish its queue at about the same time: each
        // tops its queue up toward one common finish level, in proportion to its speed, and work planned for
        // earlier keys counts as queued. Queues are measured in units — the PB can't read a blueprint's build
        // time — so a mixed queue balances only roughly. A share under MinShare isn't worth its own queue
        // append, so the smallest share drops out until every remaining one clears it (or one is left).
        void PlanAssembly(string key, double amount, ProductionFacts facts, List<AssemblerSnapshot> assemblers, long unavailableAssembler)
        {
            string bp = null;
            var resolved = false;
            candidates.Clear();
            modelVerdict.Clear();
            foreach (var p in modelProbe) modelVerdict.Add(0);
            for (var i = 0; i < assemblers.Count; i++)
            {
                var st = assemblers[i].State;
                if (!st.IsReceiver || !st.IsOperational || st.IsDisassembling || st.Id == TrackedDisassembler || st.Id == unavailableAssembler) continue;
                var m = modelOf[i];
                if (modelVerdict[m] == 0)
                {
                    var probe = modelProbe[m];
                    modelVerdict[m] = !facts.TryResolveBlueprint(key, probe.CanUse, out bp) ? 1 : probe.CanUse(bp) ? 3 : 2;
                }
                resolved |= modelVerdict[m] > 1;
                if (modelVerdict[m] == 3) candidates.Add(i);
            }
            if (candidates.Count == 0)
            {
                string known;
                var why = Why(assemblers);
                if (why < NoAssembler || resolved || facts.TryBlueprint(key, out known)) unassigned[why].Add(key);
                else needBlueprint.Add(key);
                return;
            }

            double level;
            for (;;)
            {
                double load = amount, speed = 0;
                foreach (var i in candidates) { load += plannedLoad[i] + crewLoad[i]; speed += weight[i]; }
                level = load / speed;
                var drop = -1;
                var smallest = MinShare;
                for (var c = 0; c < candidates.Count && candidates.Count > 1; c++)
                {
                    var i = candidates[c];
                    var share = level * weight[i] - plannedLoad[i] - crewLoad[i];
                    if (share < smallest) { smallest = share; drop = c; }
                }
                if (drop < 0) break;
                candidates.RemoveAt(drop);
            }

            // Rounding the running total, not each share, keeps whole units that sum exactly to the deficit.
            double cumulative = 0, given = 0;
            for (var c = 0; c < candidates.Count; c++)
            {
                var i = candidates[c];
                cumulative += level * weight[i] - plannedLoad[i] - crewLoad[i];
                var upTo = c == candidates.Count - 1 ? amount : Math.Min(amount, Math.Round(cumulative));
                if (upTo <= given) continue;
                actions.Add(new CraftAction(CraftActionKind.AppendAssembly, assemblers[i].State, bp, upTo - given, key));
                plannedLoad[i] += upTo - given;
                given = upTo;
            }
        }

        // True surplus is disassembled on ONE assembler at a time: the tracked one if it is still ours (append
        // more work to it), else one is acquired and tracked. The slowest compatible home Coop goes first, so the Main
        // and the faster Coops keep assembling; a busy Coop is drained before its mode changes. Only with no such Coop
        // does the fastest empty home receiver no assembly was planned onto this pass take it, holding the home's
        // assembly until done. Never a docked one: a ship leaving mid-order would take the queue along and shred
        // that item from its own cargo, out of SHEPHERD's reach. An occupied assembler's mode is never touched (acquisition requires an empty queue,
        // structurally), nor is an empty one already in Disassembly: that mode is the user's, and taking it over would
        // end with SHEPHERD switching it back to Assembly.
        void PlanDisassembly(string key, double amount, ProductionFacts facts, List<AssemblerSnapshot> assemblers, bool released, long unavailableAssembler)
        {
            string bp;
            if (!facts.TryBlueprint(key, out bp))
            {
                foreach (var s in assemblers)
                    if (s.State.Id != unavailableAssembler && facts.TryResolveBlueprint(key, s.State.CanUse, out bp)) break;
            }
            if (bp == null) { needBlueprint.Add(key); return; }

            if (TrackedDisassembler != 0)
            {
                foreach (var s in assemblers)
                {
                    var st = s.State;
                    if (st.Id != TrackedDisassembler || st.Id == unavailableAssembler) continue;
                    if (st == drained && homeHasMain && st.CanUse(bp))
                    {
                        actions.Add(new CraftAction(CraftActionKind.SetDisassembly, st, null, 0, key));
                        drained = null;
                    }
                    else if (!st.IsDisassembling || !st.IsOperational || !(st.IsReceiver && st.Construct == 0 || IsHomeCoop(st) && homeHasMain)
                        || !st.CanUse(bp)) return;    // this surplus waits its turn
                    actions.Add(new CraftAction(CraftActionKind.AppendDisassembly, st, bp, amount, key));
                    return;
                }
                return;
            }
            if (released) return;

            var best = -1;
            for (var i = 0; i < assemblers.Count && homeHasMain; i++)
            {
                var s = assemblers[i];
                var st = s.State;
                if (!IsHomeCoop(st) || !st.IsOperational || st.IsDisassembling || st.Id == unavailableAssembler
                    || best >= 0 && s.Speed >= assemblers[best].Speed || !st.CanUse(bp)) continue;
                best = i;
            }
            if (best >= 0)
            {
                var coop = assemblers[best];
                TrackedDisassembler = coop.State.Id;
                if (!coop.QueueEmpty || coop.State.Cooperative)
                {
                    actions.Add(new CraftAction(CraftActionKind.StopCooperating, coop.State, null, 0, key));
                    return;
                }
                actions.Add(new CraftAction(CraftActionKind.SetDisassembly, coop.State, null, 0, key));
                actions.Add(new CraftAction(CraftActionKind.AppendDisassembly, coop.State, bp, amount, key));
                return;
            }
            for (var i = 0; i < assemblers.Count; i++)
            {
                var s = assemblers[i];
                var st = s.State;
                if (!s.QueueEmpty || !st.IsReceiver || st.Construct > 0 || !st.IsOperational || st.IsDisassembling || st.Id == unavailableAssembler || plannedLoad[i] > 0
                    || best >= 0 && s.Speed <= assemblers[best].Speed || !st.CanUse(bp)) continue;
                best = i;
            }
            if (best < 0) { unassigned[Disassembly + Why(assemblers)].Add(key); return; }
            var chosen = assemblers[best].State;
            actions.Add(new CraftAction(CraftActionKind.SetDisassembly, chosen, null, 0, key));
            actions.Add(new CraftAction(CraftActionKind.AppendDisassembly, chosen, bp, amount, key));
            TrackedDisassembler = chosen.Id;
        }

        int Why(List<AssemblerSnapshot> assemblers)
        {
            var why = NoMain;
            foreach (var s in assemblers)
            {
                var st = s.State;
                if (!st.IsReceiver) continue;
                if (!st.IsOperational) { if (why < MainOff) why = MainOff; }
                else if (st.IsDisassembling || st.Id == TrackedDisassembler) why = MainBusy;
                else return NoAssembler;
            }
            return why;
        }
    }

    // The live craft stepper: exactly one consequential machine mutation (queue append, clear or mode set) per Step,
    // returned as Moved so the scheduler attributes it like a transfer and arms the post-heavy refractory. A plan
    // holds only a few mutations, so spacing them costs little. Pooled: Rearm copies the plan. An append is claimed
    // only once it lands, so a plan dropped before running never claims the player's units of the same blueprint.
    public sealed class CraftJob : IStepJob
    {
        readonly List<CraftAction> actions = new List<CraftAction>();
        CraftPlanner planner;
        int cursor;

        public void Rearm(CraftPlanner planned)
        {
            planner = planned;
            actions.Clear();
            actions.AddRange(planned.Actions);
            cursor = 0;
        }

        public bool IsDone => cursor >= actions.Count;

        public StepResult Step()
        {
            if (IsDone) return StepResult.Done;
            var a = actions[cursor];
            cursor++;
            if (a.Kind == CraftActionKind.AppendAssembly || a.Kind == CraftActionKind.AppendDisassembly)
                planner.Append(a.Assembler, a.Item, a.Blueprint, a.Amount);
            else if (a.Kind == CraftActionKind.Trim)
                planner.Release(a.Assembler, a.Blueprint, a.Assembler.Trim(a.Blueprint, a.Amount));
            else if (a.Kind == CraftActionKind.ClearQueue)
                a.Assembler.ClearQueue();
            else if (a.Kind == CraftActionKind.StopCooperating)
                a.Assembler.SetCooperative(false);
            else
                a.Assembler.SetDisassembling(a.Kind == CraftActionKind.SetDisassembly);
            return StepResult.Moved;
        }
    }
}
