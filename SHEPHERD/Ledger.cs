using System;
using System.Collections.Generic;

namespace IngameScript
{
    public class ProductionLedger
    {
        class Entry
        {
            public double Stock, Caught, Incoming, Outgoing, Protected, ProductionFloor, ProductionCap, Retained;
            public bool Disassemble, HasAll;

            public void Reset()
            {
                Stock = Caught = Incoming = Outgoing = Protected = ProductionFloor = ProductionCap = Retained = 0;
                Disassemble = HasAll = false;
            }
        }

        readonly Dictionary<string, Entry> items = new Dictionary<string, Entry>();
        readonly Stack<Entry> spare = new Stack<Entry>();

        public void Begin()
        {
            foreach (var e in items.Values) spare.Push(e);
            items.Empty();
        }

        public void AddStock(string key, double amount) => At(key).Stock += amount;

        public void AddCaught(string key, double amount) => At(key).Caught += amount;

        public void AddIncoming(string key, double amount) => At(key).Incoming += amount;

        public void AddOutgoing(string key, double amount) => At(key).Outgoing += amount;

        public void AddProtected(string key, double amount) => At(key).Protected += amount;

        public void AddQuotas(ReservePool pool)
        {
            foreach (var pair in pool.Quotas)
            {
                var e = At(pair.Key);
                var q = pair.Value;
                if (q.Mode == QuotaMode.All) { e.HasAll = true; continue; }
                e.Retained += q.Target;
                if (q.Mode != QuotaMode.Limit) e.ProductionFloor += q.Target;
                if (!q.AutomaticProduction) continue;
                if (q.Mode != QuotaMode.Limit) e.ProductionCap += q.Target;
                if (q.Mode != QuotaMode.Min) e.Disassemble = true;
            }
        }

        public double AssemblyDeficit(string key)
        {
            Entry e;
            if (!items.Get(key, out e) || IsType(key, "Ingot") || IsType(key, "Ore")) return 0;
            return Deficit(e);
        }

        public double RefineDeficit(string key)
        {
            Entry e;
            if (!items.Get(key, out e) || !IsType(key, "Ingot")) return 0;
            return Deficit(e);
        }

        public double DisassemblySurplus(string key)
        {
            Entry e;
            if (!items.Get(key, out e)) return 0;
            var s = Surplus(e, key) - e.Outgoing;
            return s > 0 ? s : 0;
        }

        // Queued work the targets no longer need, as a docked ship's unload or a hand top-off leaves behind.
        public double QueuedExcess(string key, bool disassembly)
        {
            Entry e;
            if (!items.Get(key, out e)) return 0;
            var need = disassembly ? Surplus(e, key) : Need(e);
            return (disassembly ? e.Outgoing : e.Incoming) - (need > 0 ? need : 0);
        }

        // Machine inputs (the drain keeps them) and reserve targets (reserve demand outranks production, and a
        // keep-quota drain refuses them) can't be moved into a machine.
        public double Available(string key)
        {
            Entry e;
            if (!items.Get(key, out e)) return 0;
            var a = e.Stock - e.Protected - e.Retained;
            return a > 0 ? a : 0;
        }

        public double Stock(string key)
        {
            Entry e;
            return items.Get(key, out e) ? e.Stock : 0;
        }

        public double Caught(string key)
        {
            Entry e;
            return items.Get(key, out e) ? e.Caught : 0;
        }

        public double Incoming(string key)
        {
            Entry e;
            return items.Get(key, out e) ? e.Incoming : 0;
        }

        public double Outgoing(string key)
        {
            Entry e;
            return items.Get(key, out e) ? e.Outgoing : 0;
        }

        public void CollectKeys(List<string> into)
        {
            into.Empty();
            foreach (var key in items.Keys) into.Push(key);
        }

        Entry At(string key)
        {
            Entry e;
            if (items.Get(key, out e)) return e;
            e = spare.Count > 0 ? spare.Pop() : new Entry();
            e.Reset();
            items[key] = e;
            return e;
        }

        // Each direction nets only its own queue: counting Outgoing here would hold an item being disassembled at
        // deficit 0, where any transient undercount (a unit consumed before its queue entry drops, a stack in
        // conveyor transit) triggers a spurious counter-assembly. Machine-held stock never nets the floor: the
        // drain protects it, so it can never be carried to a reserve.
        static double Deficit(Entry e)
        {
            var d = Need(e) - e.Incoming;
            return d > 0 ? d : 0;
        }

        static double Need(Entry e) => Math.Min(e.ProductionFloor - (e.Stock - e.Protected), e.ProductionCap);

        static double Surplus(Entry e, string key) =>
            !e.Disassemble || e.HasAll || IsType(key, "Ingot") || IsType(key, "Ore") ? 0 : e.Stock - e.Retained - e.Protected;

        static bool IsType(string key, string type) =>
            key.Length > type.Length && key[type.Length] == '/'
            && key.Starts(type);
    }

    // CatchAll is plain stock also tallied as Caught for the stock LCDs; a Refining input's ore also counts as
    // incoming ingots. Ordered: everything below Protected is plain stock.
    public enum LedgerKind { Plain, CatchAll, Protected, Refining }

    public struct QueueEntry
    {
        public string Blueprint;
        public double Amount;
    }

    // An interface so the ledger walk and the craft planner stay game-free. The craft stepper fires at most one
    // mutation per tick. Assemblers of one Model accept the same blueprints; Speed is relative to a vanilla
    // assembler's 1.
    public interface IAssemblerState : IMarkedAssembler
    {
        string Model { get; }
        double Speed { get; }
        bool IsWorking { get; }
        bool IsDisassembling { get; }
        void QueueInto(List<QueueEntry> into);
        void OutputInto(Dictionary<string, double> into);
        bool CanUse(string blueprint);
        void Append(string blueprint, double amount, bool dry);
        double Trim(string blueprint, double amount, bool dry);
        void SetDisassembling(bool on, bool dry);
        void ClearQueue(bool dry);
    }

    // Queue state and speed are captured during attribution so later planning never re-reads an assembler.
    public struct AssemblerSnapshot
    {
        public IAssemblerState State;
        public bool QueueEmpty;
        public double QueueAmount;
        public double Speed;
    }

    // A reserve container's stacks are tallied into its pool here, so the pool's count costs no read of its own. All
    // reads are local (no conveyor call), so the scheduler treats its ticks as cheap.
    public class LedgerJob : IStepJob
    {
        readonly ProductionLedger ledger;
        readonly ProductionFacts facts;
        readonly Dictionary<string, ReservePool> pools;
        readonly int examineBudget;
        // True once the run passes half the instruction cap: a unit's cost grows with the grid, and the cap aborts
        // the script outright.
        public Func<bool> Full = () => false;
        readonly List<QueueEntry> queueScratch = new List<QueueEntry>();
        public readonly List<AssemblerSnapshot> Snapshots = new List<AssemblerSnapshot>();
        List<ISortSource> sources;
        List<LedgerKind> kinds;                  // parallel to sources
        List<ReservePool> countPools;            // parallel to sources; null = not a reserve container
        List<IAssemblerState> assemblers;
        int sourceCursor;
        int itemCursor;
        int assemblerCursor;
        bool done = true;

        public LedgerJob(ProductionLedger ledger, ProductionFacts facts, Dictionary<string, ReservePool> pools, int examineBudgetPerStep)
        {
            this.ledger = ledger;
            this.facts = facts;
            this.pools = pools;
            examineBudget = examineBudgetPerStep > 0 ? examineBudgetPerStep : int.MaxValue;
        }

        public void Rearm(List<ISortSource> sortSources, List<LedgerKind> sourceKinds, List<ReservePool> sourcePools,
            List<IAssemblerState> assemblerStates)
        {
            sources = sortSources;
            kinds = sourceKinds;
            countPools = sourcePools;
            assemblers = assemblerStates;
            sourceCursor = 0;
            itemCursor = 0;
            assemblerCursor = 0;
            Snapshots.Empty();
            done = false;
            ledger.Begin();
            foreach (var pool in pools.Values) pool.BeginTally();
        }

        public bool IsDone => done;

        public StepResult Step()
        {
            if (done) return StepResult.Done;
            var examined = 0;
            while (sourceCursor < sources.Count)
            {
                var src = sources[sourceCursor];
                if (src == null) { NextSource(); continue; }

                // Re-snapshot each step: the shared buffer belongs to whichever source was walked last tick. A stack
                // that shifted between ticks miscounts one cycle at worst.
                src.Refresh();
                var kind = kinds[sourceCursor];
                var pool = countPools[sourceCursor];
                var n = src.Count;
                while (itemCursor < n)
                {
                    if (examined >= examineBudget || Full()) return StepResult.Scanned;
                    Tally(src, itemCursor, kind);
                    if (pool != null) pool.Tally(src.KeyAt(itemCursor), src.AmountAt(itemCursor));
                    itemCursor++;
                    examined++;
                }
                NextSource();
                examined++;         // charge the snapshot itself
                if (examined >= examineBudget || Full()) return StepResult.Scanned;
            }

            while (assemblerCursor < assemblers.Count)
            {
                if (examined >= examineBudget || Full()) return StepResult.Scanned;
                TallyAssembler(assemblers[assemblerCursor]);
                assemblerCursor++;
                examined++;
            }

            foreach (var pool in pools.Values)
            {
                ledger.AddQuotas(pool);
                pool.CommitTally();
            }
            done = true;
            return StepResult.Done;
        }

        void NextSource()
        {
            sourceCursor++;
            itemCursor = 0;
        }

        void Tally(ISortSource src, int index, LedgerKind kind)
        {
            var key = src.KeyAt(index);
            var amount = src.AmountAt(index);
            ledger.AddStock(key, amount);
            if (kind == LedgerKind.CatchAll) ledger.AddCaught(key, amount);
            if (kind < LedgerKind.Protected) return;
            ledger.AddProtected(key, amount);
            if (kind != LedgerKind.Refining) return;
            List<OreYield> outputs;
            if (!facts.TryYields(key, out outputs)) return;
            foreach (var y in outputs) ledger.AddIncoming(y.Output, amount * y.Ratio);
        }

        // An unknown modded blueprint lands on a phantom key no quota references. A switched-off assembler's assembly
        // queue isn't incoming (it would hold the deficit at zero while nothing gets made), but its disassembly queue
        // still counts, so a surplus is never queued twice.
        void TallyAssembler(IAssemblerState assembler)
        {
            assembler.QueueInto(queueScratch);
            var outgoing = assembler.IsDisassembling;
            var queueAmount = 0.0;
            foreach (var entry in queueScratch)
            {
                queueAmount += entry.Amount;
                var key = facts.ItemOf(entry.Blueprint);
                if (outgoing) ledger.AddOutgoing(key, entry.Amount);
                else if (assembler.IsOperational) ledger.AddIncoming(key, entry.Amount);
            }
            Snapshots.Push(new AssemblerSnapshot { State = assembler, QueueEmpty = queueScratch.Count == 0, QueueAmount = queueAmount, Speed = assembler.Speed });
        }
    }
}
