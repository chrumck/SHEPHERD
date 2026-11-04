using System;
using System.Collections.Generic;

namespace IngameScript
{
    // A gas tank as a momentary refill station: bottles are lifted in through Intake and returned out of Contents.
    public interface IBottleTank
    {
        bool HasGas { get; }
        // Whether gas was consumed: the sweep cycles a stack through the tank until none is, so a stack bigger than
        // the tank's inventory still fills in one sweep.
        bool Refill(bool dry);
        ICategoryAcceptor Intake { get; }
        ISortSource Contents { get; }
    }

    // A PB can't read a bottle's gas level, so the sweep never tells full from empty: IMyGasTank.RefillBottles()
    // fills every bottle in a tank instantly and idempotently. Each storage stack (walked high-index-first) is lifted
    // into a tank as far as volume allows, refilled and returned, again until a refill consumes no gas — so the
    // tank's stored gas bounds the sweep, not its volume. Returned bottles append above the downward cursor and are
    // never re-lifted. Drain then empties bottles a user left in a tank. One transfer per tick, find/commit split.
    public class BottleJob : IStepJob
    {
        // Must differ from -1 (walked past index 0, advance): a returned bottle re-grows the source, so resetting a
        // spent -1 cursor to the top would re-lift it forever.
        const int Unset = int.MinValue;

        enum Phase { Scan, Refill, Return, Decide, Drain }

        readonly List<ISortSource> sources;   // never a tank
        readonly List<IBottleTank> tanks;
        readonly int examineBudget;

        Phase phase;
        int srcCursor;
        int resumeIndex = Unset;   // not advanced on a lift: Decide loops or advances it
        IBottleTank activeTank;
        ISortSource activeSource;
        bool refillConsumed;

        int drainTank;
        int drainSource;
        int warmTank, warmSource;  // where SendDry resumes
        bool drainRefilled;

        bool commitPending;
        ISortSource pendingSource;
        bool committingReturn;     // a failed return strands the tank; a failed lift doesn't
        string pendingReturnTank;
        bool done;

        public readonly List<string> StrandedTanks = new List<string>();

        public BottleJob(List<ISortSource> storageSources, List<IBottleTank> refillTanks, int examineBudgetPerStep)
        {
            sources = storageSources;
            tanks = refillTanks;
            examineBudget = examineBudgetPerStep > 0 ? examineBudgetPerStep : int.MaxValue;
        }

        public bool IsDone => done;

        // A dry sweep moves and fills nothing but still sends each tank a round trip, so the sweep's code has run once
        // before a real one needs it.
        public bool Dry;
        // True past half the instruction cap: a unit's cost grows with the grid and the cap aborts the script, so a
        // step yields then too.
        public Func<bool> Full = () => false;

        public StepResult Step()
        {
            if (tanks == null || sources == null) { done = true; return StepResult.Done; }

            // The commit tick fires only the prepared transfer. A failed Drain return gives up the whole tank (all
            // its bottles route the same way), else StepDrain would re-prepare the same dead move forever.
            if (commitPending)
            {
                commitPending = false;
                var moved = pendingSource.Commit(Dry) == CommitResult.Moved;
                if (committingReturn && !moved)
                {
                    RecordStranded(pendingReturnTank);
                    if (phase == Phase.Drain) NextDrainTank();
                }
                return Dry ? StepResult.Scanned : StepResult.Moved;
            }

            while (true)
            {
                if (phase == Phase.Refill)
                {
                    phase = Phase.Return;
                    // An earlier lift this sweep may have spent the gas Scan saw.
                    if (activeTank.HasGas) { refillConsumed = activeTank.Refill(Dry); return StepResult.Refilled; }
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
                    if (!refillConsumed) resumeIndex--;
                    continue;
                }
                if (phase == Phase.Scan)
                {
                    var r = ScanForBottle();
                    if (r == StepResult.Found || r == StepResult.Scanned) return r;
                    phase = Phase.Drain; drainTank = 0; drainRefilled = false;
                    continue;
                }

                var d = StepDrain();
                if (d == StepResult.Done)
                {
                    // A dry return never lands, so the tanks it left "stranded" are not.
                    if (Dry)
                    {
                        if (!SendDry()) return StepResult.Scanned;
                        StrandedTanks.Empty();
                    }
                    done = true;
                    return StepResult.Done;
                }
                return d;
            }
        }

        StepResult ScanForBottle()
        {
            var examined = 0;
            while (srcCursor < sources.Count)
            {
                var src = sources[srcCursor];
                if (src == null) { srcCursor++; resumeIndex = Unset; continue; }

                src.Refresh();
                var n = src.Count;
                if (resumeIndex == Unset || resumeIndex >= n) resumeIndex = n - 1;

                while (resumeIndex >= 0)
                {
                    if (examined >= examineBudget || Full()) return StepResult.Scanned;
                    var i = resumeIndex; examined++;
                    // A bottle no tank takes is offered to every tank, so each offer draws the budget.
                    if (Category.IsBottle(src.KeyAt(i)))
                    foreach (var t in tanks)
                    {
                        examined++;
                        if (t == null || !src.CanPlaceAt(i, t.Intake)) continue;
                        src.Prepare(i, t.Intake);
                        pendingSource = src;
                        committingReturn = false;
                        commitPending = true;
                        activeTank = t;
                        activeSource = src;
                        phase = Phase.Refill;
                        return StepResult.Found;   // resumeIndex stays on this stack for Decide
                    }
                    resumeIndex--;
                }

                srcCursor++; resumeIndex = Unset;
                if (++examined >= examineBudget || Full()) return StepResult.Scanned;
            }
            return StepResult.Done;
        }

        // Refills a tank once before draining it, so a stray bottle leaves full.
        StepResult StepDrain()
        {
            var examined = 0;
            while (drainTank < tanks.Count)
            {
                if (examined >= examineBudget || Full()) return StepResult.Scanned;
                var t = tanks[drainTank];
                if (t == null) { NextDrainTank(); examined++; continue; }

                if (t.HasGas && !drainRefilled) { drainRefilled = true; t.Refill(Dry); return StepResult.Refilled; }

                var src = t.Contents;
                src.Refresh();
                examined++;
                var n = src.Count;
                if (n <= 0) { NextDrainTank(); continue; }

                var idx = n - 1;
                while (drainSource < sources.Count)
                {
                    if (examined >= examineBudget || Full()) return StepResult.Scanned;
                    examined++;
                    var dest = sources[drainSource++] as ICategoryAcceptor;
                    if (dest == null || !src.CanPlaceAt(idx, dest)) continue;
                    drainSource = 0;
                    src.Prepare(idx, dest);
                    pendingSource = src;
                    committingReturn = true;
                    pendingReturnTank = t.Intake.Name;
                    commitPending = true;
                    return StepResult.Found;
                }
                // All of a tank's bottles route the same way, so one with no taker strands the whole tank.
                RecordStranded(t.Intake.Name);
                NextDrainTank();
            }
            return StepResult.Done;
        }

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

        // Budgeted per source: the empty sources ahead of the first stack are walked again for every tank.
        bool SendDry()
        {
            var examined = 0;
            for (; warmTank < tanks.Count; warmTank++, warmSource = 0)
            {
                var t = tanks[warmTank];
                for (; t != null && warmSource < sources.Count; warmSource++)
                {
                    if (examined++ >= examineBudget || Full()) return false;
                    var src = sources[warmSource];
                    if (src == null) continue;
                    src.Refresh();
                    if (src.Count == 0) continue;
                    src.CanPlaceAt(0, t.Intake);
                    src.Prepare(0, t.Intake);
                    src.Commit(true);
                    t.Refill(true);
                    if (PrepareReturn(t, src)) pendingSource.Commit(true);
                    commitPending = false;
                    break;
                }
            }
            return true;
        }

        void NextDrainTank() { drainTank++; drainRefilled = false; drainSource = 0; }

        void RecordStranded(string tankName)
        {
            foreach (var t in StrandedTanks) if (t == tankName) return;
            StrandedTanks.Push(tankName);
        }
    }
}
