using System.Collections.Generic;

namespace IngameScript
{
    // A gas tank presented to the bottle sweep as a refill station. Game-free so BottleJob is offline-testable;
    // the live RefillStation binds it to a real IMyGasTank. HasGas gates out a tank that holds no gas. Intake is
    // the tank as a move-in target (a bottle lifted IN); Contents is the tank's own inventory as a source (the
    // bottle moved back OUT). Refill is the synchronous IMyGasTank.RefillBottles().
    public interface IBottleTank
    {
        bool HasGas { get; }
        // Force-fill every bottle in the tank and return whether it consumed gas (filled at least one empty). The
        // sweep loops a bottle stack through the tank while this stays true, so a stack bigger than the tank's
        // inventory volume is fully filled in one sweep.
        bool Refill();
        ICategoryAcceptor Intake { get; }
        ISortSource Contents { get; }
    }

    // The periodic bottle-refill sweep. A PB cannot read a bottle's gas level, so full-vs-empty is undetectable.
    // The sweep sidesteps detection with IMyGasTank.RefillBottles(), which fills every bottle in a tank instantly
    // and idempotently, and uses the tank purely as a MOMENTARY refill station:
    //
    //   for each storage bottle stack (each source walked high-index-first):
    //     repeat:  lift what fits into a gas tank  ->  Refill()  ->  move the (now full) bottles back to the source
    //     until a Refill consumes no gas (the stack's bottles are all full, or the tank ran dry)
    //
    // Because only a tank-volume of bottles sits in the tank at a time and they are returned before the next lift,
    // the tank's inventory VOLUME never bounds the sweep — its stored GAS does. Bottles STACK and TransferItemTo
    // moves only what fits by volume, so a single lift can't move a big stack; the loop keeps cycling that same
    // stack until Refill reports no gas consumed, i.e. every bottle that passed through is full. The gas-consumed
    // signal is order-independent (no assumption that empties precede fulls). Returned bottles append at the END
    // of the source, above the downward cursor, so they are never re-lifted after the stack is done.
    //
    // A final Drain pass empties any bottles still sitting inside a tank (dropped there by a user) back out to
    // storage, refilling them on the way. If a tank's bottles cannot be placed anywhere (all storage full /
    // unreachable) the tank is recorded in StrandedTanks and Program logs it. One transfer per tick with a
    // find/commit split like SortJob; resumable across ticks with a per-step examine budget bounding the walks.
    public sealed class BottleJob : IStepJob
    {
        // Distinct from a decremented cursor value of -1: -1 means "walked past index 0, this source is
        // exhausted — advance"; Unset means "fresh source, start at the top". They must differ because a
        // returned bottle re-grows the source, so resetting a spent -1 cursor to n-1 would re-lift it forever.
        const int Unset = int.MinValue;

        // The round-trip of one bottle stack: Scan finds it + prepares the lift, Refill force-fills the tank,
        // Return moves the filled bottles back, Decide either loops the same stack (gas was consumed → more
        // empties may remain) or steps the cursor down (no gas consumed → this stack is done). Drain is the final
        // pass that empties any bottles left inside the tanks.
        enum Phase { Scan, Refill, Return, Decide, Drain }

        readonly List<ISortSource> sources;   // storage inventories bottles are round-tripped from (never a tank)
        readonly List<IBottleTank> tanks;
        readonly int examineBudget;

        Phase phase;               // the round-trip phases while storage has bottles, then Drain
        int srcCursor;             // Scan: source being walked
        int resumeIndex = Unset;   // Scan: bottle index within the current source (reverse walk; NOT advanced on a lift)
        IBottleTank activeTank;    // the tank the in-flight bottle was lifted into
        ISortSource activeSource;  // the storage source the in-flight bottle is returned to
        bool refillConsumed;       // did the last Refill consume gas (fill >=1 empty)? drives Decide's loop-or-advance

        int drainTank;             // Drain: tank being emptied
        bool drainRefilled;        // Drain: the current tank was already force-refilled this visit

        bool commitPending;        // a transfer was prepared last tick and awaits its own bare commit tick
        ISortSource pendingSource; // holds the prepared move (a storage source for a lift, a tank's Contents for a return)
        bool committingReturn;     // the pending commit is a return (a failed return strands the tank, not a lift)
        string pendingReturnTank;  // name of the tank a pending return is emptying (recorded stranded if it fails)
        bool done;                 // set the moment Step returns Done, so IsDone can never disagree with Step

        // Tanks whose bottles could not be returned to storage this sweep — every storage source was full or
        // unreachable, so the bottles are left sitting in the tank. Deduped; Program logs these.
        public readonly List<string> StrandedTanks = new List<string>();

        public BottleJob(List<ISortSource> storageSources, List<IBottleTank> refillTanks, int examineBudgetPerStep)
        {
            sources = storageSources;
            tanks = refillTanks;
            examineBudget = examineBudgetPerStep > 0 ? examineBudgetPerStep : int.MaxValue;
        }

        public bool IsDone => done;

        public StepResult Step()
        {
            if (tanks == null || sources == null) { done = true; return StepResult.Done; }

            // The bare heavy tick: fire the transfer prepared last tick and do NOTHING else. A return that moved
            // nothing leaves its bottle in the tank and records the tank as stranded. If a Drain return fails
            // though the source reported room (an unreachable / sorter-filtered source), give up the whole tank
            // so StepDrain doesn't re-prepare the same dead move forever (all a tank's bottles route the same
            // way). A round-trip return can't fail here in practice — the bottle just came from that source.
            if (commitPending)
            {
                commitPending = false;
                var moved = pendingSource.Commit() == CommitResult.Moved;
                if (committingReturn && !moved)
                {
                    RecordStranded(pendingReturnTank);
                    if (phase == Phase.Drain) NextDrainTank();
                }
                return StepResult.Moved;
            }

            while (true)
            {
                if (phase == Phase.Refill)
                {
                    phase = Phase.Return;
                    // The tank had gas when Scan chose it, but an earlier lift this sweep may have spent it;
                    // re-check so a drained tank isn't force-refilled for nothing. refillConsumed drives Decide:
                    // true → loop the stack (more empties may remain); false → this stack is done (or gas ran out).
                    if (activeTank.HasGas) { refillConsumed = activeTank.Refill(); return StepResult.Refilled; }
                    refillConsumed = false;
                    continue;
                }
                if (phase == Phase.Return)
                {
                    // No accept pre-check: the source just vacated these bottles, so it has room and is reachable.
                    var back = PrepareReturn(activeTank, activeSource);
                    phase = Phase.Decide;
                    if (back) return StepResult.Found;
                    continue;
                }
                if (phase == Phase.Decide)
                {
                    phase = Phase.Scan;
                    // Loop the SAME stack while the tank keeps filling it; step the cursor down once a refill
                    // fills nothing. The returned fulls sit above the cursor, so they are not re-lifted.
                    if (!refillConsumed) resumeIndex--;
                    continue;
                }
                if (phase == Phase.Scan)
                {
                    var r = ScanForBottle();
                    if (r == StepResult.Found || r == StepResult.Scanned) return r;
                    phase = Phase.Drain; drainTank = 0; drainRefilled = false; // storage exhausted → drain the tanks
                    continue;
                }

                var d = StepDrain();
                if (d == StepResult.Done) { done = true; return StepResult.Done; }
                return d;
            }
        }

        // Walk storage high-index-first for a bottle stack a gas-holding tank will take right now, and prepare its
        // lift into that tank. CanPlaceAt folds "is a bottle" + "the tank holds gas" + constraint and volume.
        // Returns Found (resumeIndex left ON that stack for Decide to loop or advance), Scanned, or Done. An index
        // is advanced here ONLY to skip one no tank will take; a liftable index is left in place for re-lifting.
        StepResult ScanForBottle()
        {
            var examined = 0;
            while (srcCursor < sources.Count)
            {
                var src = sources[srcCursor];
                if (src == null) { srcCursor++; resumeIndex = Unset; continue; }

                src.Refresh();
                var n = src.Count;
                if (resumeIndex == Unset || resumeIndex >= n) resumeIndex = n - 1; // fresh source (or it shrank) → top

                while (resumeIndex >= 0)
                {
                    if (examined >= examineBudget) return StepResult.Scanned;
                    var i = resumeIndex; examined++;
                    foreach (var t in tanks)
                    {
                        if (t == null || !src.CanPlaceAt(i, t.Intake)) continue;
                        src.Prepare(i, t.Intake);
                        pendingSource = src;
                        committingReturn = false;
                        commitPending = true;
                        activeTank = t;
                        activeSource = src;
                        phase = Phase.Refill;
                        return StepResult.Found;   // leave resumeIndex on this stack — Decide advances it
                    }
                    resumeIndex--;                 // no tank took index i → skip it
                }

                srcCursor++; resumeIndex = Unset;
                if (++examined >= examineBudget) return StepResult.Scanned; // charge the finished source too (bounded)
            }
            return StepResult.Done;
        }

        // Empty any bottles still sitting inside the tanks back out to storage. Refills each tank once so a stray
        // bottle leaves full, then routes its bottles to the first storage source with room. One tank/one bottle
        // per call.
        StepResult StepDrain()
        {
            var examined = 0;
            while (drainTank < tanks.Count)
            {
                if (examined >= examineBudget) return StepResult.Scanned;
                var t = tanks[drainTank];
                if (t == null) { NextDrainTank(); examined++; continue; }

                if (t.HasGas && !drainRefilled) { drainRefilled = true; t.Refill(); return StepResult.Refilled; }

                var src = t.Contents;
                src.Refresh();
                examined++;
                var n = src.Count;
                if (n <= 0) { NextDrainTank(); continue; }

                var idx = n - 1;
                foreach (var source in sources)
                {
                    var dest = source as ICategoryAcceptor;
                    if (dest == null || !src.CanPlaceAt(idx, dest)) continue;
                    src.Prepare(idx, dest);
                    pendingSource = src;
                    committingReturn = true;
                    pendingReturnTank = t.Intake.Name;
                    commitPending = true;
                    return StepResult.Found;
                }
                // No storage source will take this bottle → the whole tank is stranded (all its bottles route the
                // same way), so record it and move on.
                RecordStranded(t.Intake.Name);
                NextDrainTank();
            }
            return StepResult.Done;
        }

        // Prepare moving the tank's highest-index bottle back into dest (the source it came from). Returns false
        // only when the tank holds no bottle. dest is a storage source that also implements ICategoryAcceptor,
        // so a tank's Contents can transfer into it.
        bool PrepareReturn(IBottleTank t, ISortSource dest)
        {
            var acc = dest as ICategoryAcceptor;
            if (acc == null) return false;
            var src = t.Contents;
            src.Refresh();
            var n = src.Count;
            if (n <= 0) return false;
            src.Prepare(n - 1, acc);
            pendingSource = src;
            committingReturn = true;
            pendingReturnTank = t.Intake.Name;
            commitPending = true;
            return true;
        }

        void NextDrainTank() { drainTank++; drainRefilled = false; }

        void RecordStranded(string tankName)
        {
            foreach (var t in StrandedTanks) if (t == tankName) return;
            StrandedTanks.Add(tankName);
        }
    }
}
