using System.Collections.Generic;

namespace IngameScript
{
    // A walkable set of item stacks the sorter drains, addressed by index into a per-step snapshot. Kept free of
    // game types so SortJob is offline-testable; the live InventorySource adapter binds it to a real inventory.
    // Refresh() re-reads the snapshot at the top of every step, so an index is only ever used against the
    // snapshot it was read from that same step — a stack that shifted or vanished between ticks is never mis-moved.
    public interface ISortSource
    {
        void Refresh();
        int Count { get; }
        string CategoryAt(int index);
        // The stack's item key (Category.Key form) and unit count — the production ledger's stock walk reads
        // these off the same snapshot the sort walks; the sort itself never needs item identity.
        string KeyAt(int index);
        double AmountAt(int index);
        // May the stack at index be drained out? True for ordinary storage; for a machine that keeps its own
        // contents it is true only for a FOREIGN item to evict, false for what the block legitimately holds.
        bool ShouldDrain(int index);
        // Stacks may leave only for a demand target; any other routed target leaves them where they are.
        bool DemandOnly { get; }
        bool IsSameAs(ICategoryAcceptor acceptor);
        // Can the stack at index actually land in target right now? Answered by CanItemsBeAdded (constraint +
        // volume), so the sort never picks a container that reports free volume but rejects the item.
        bool CanPlaceAt(int index, ICategoryAcceptor target);
        void Prepare(int index, ICategoryAcceptor target); // capture the pending move on the cheap find tick
        CommitResult Commit();                              // the one budgeted heavy (conveyor) call, fired alone
    }

    // Why a commit moved nothing. Only Refused is a conveyor/sorter problem worth warning about: a stack taken
    // between the find and commit ticks (ItemGone) and a target that filled meanwhile (TargetFull) are ordinary
    // races on a busy grid, not faults.
    public enum CommitResult { Moved, ItemGone, TargetFull, Refused }

    // A routing target that reported room yet whose transfer moved nothing — unreachable or sorter-filtered (the
    // two are indistinguishable). SortJob collects these per cycle (deduped) so Program can surface a diagnostic.
    public struct UnreachableRoute
    {
        public readonly string Target;
        public readonly string Category;
        public UnreachableRoute(string target, string category) { Target = target; Category = category; }
    }

    // The cross-tick sort pass: one lap visits every source once, starting at a rotated index, and may be stopped
    // early so a long backlog never holds the rest of the cycle; the next pass resumes at NextStart, so a busy early
    // source can't keep later ones from being reached. A placement takes two Step()s: a cheap FIND step (Refresh + choose a target +
    // Prepare, returns Found) then a bare COMMIT step (fires the one conveyor call alone, returns Moved) — so the
    // single heavy call owns its whole tick and the scheduler can space heavy ticks apart. Resumable across ticks
    // and self-healing: Refresh() re-snapshots the current source each step and stacks are walked high-index-first,
    // so our own transfers never shift a not-yet-processed lower index, and external edits between ticks at worst
    // cost a re-examine. A source or target removed mid-cycle (undock, grind) does not throw: the engine still
    // answers for its inventory and a commit involving it fails like any other, so it needs no special case.
    public sealed partial class SortJob : IStepJob
    {
        const int Unset = -1;

        readonly List<ISortSource> sources;
        readonly RoutingIndex routing;
        readonly int examineBudget;
        readonly int start;
        // Supplied by the caller and only ever appended to (deduped), so diagnostics accumulate across the passes
        // of one lap — a stopped pass saw only part of the grid, so it can't by itself say a state has cleared.
        readonly List<string> noRoute;
        public readonly List<UnreachableRoute> UnreachableRoutes;
        int sourceCursor;           // sources finished this pass; the source worked is (start + sourceCursor) % Count
        bool stopped;
        int resumeIndex = Unset;
        bool commitPending;         // a transfer was Prepared last tick and awaits its own bare commit tick
        int revisits;               // consecutive commits that moved something on the current stack
        int revisitCap;             // upper bound on those revisits = the worked stack's accepting-container count
        int skipTargets;            // ChooseTarget starts here — advanced past a target whose commit failed
        int chosenRank;             // rank (into the routed list) of the target Prepared this find tick
        string chosenTarget;        // its Name, captured cheap for the commit tick's unreachable diagnostic
        string chosenCategory;      // the Prepared stack's category, likewise captured cheap

        public SortJob(List<ISortSource> sources, RoutingIndex routing, int examineBudgetPerStep, int startSource,
                       List<string> noRouteSink, List<UnreachableRoute> unreachableSink)
        {
            this.sources = sources;
            this.routing = routing;
            // 0 or less means "no cap"; the shipping loop passes a real bound.
            examineBudget = examineBudgetPerStep > 0 ? examineBudgetPerStep : int.MaxValue;
            // The carried-over start can outlive a grid that shrank since it was taken.
            start = sources != null && startSource > 0 && startSource < sources.Count ? startSource : 0;
            noRoute = noRouteSink;
            UnreachableRoutes = unreachableSink;
        }

        // A prepared transfer still owns its commit tick, so a stop lands only once none is pending.
        public bool IsDone => sources == null || sourceCursor >= sources.Count || stopped && !commitPending;

        public void Stop() => stopped = true;

        public int SourcesFinished => sourceCursor;

        // Where the next pass should begin: the source this one was working when stopped, so it is resumed rather
        // than skipped; after a full lap, the same start again.
        public int NextStart => sources == null || sourceCursor >= sources.Count ? start : Current;

        int Current => (start + sourceCursor) % sources.Count;

        // Categories encountered this cycle whose Route() was empty — items with nowhere to go. Deduped; Program
        // logs the set when the cycle ends.
        public List<string> NoRouteCategories => noRoute;

        public StepResult Step()
        {
            if (routing == null || sources == null) return StepResult.Done;

            // A transfer prepared last tick fires now and NOTHING else runs this tick — the heavy tick stays bare
            // so all the sub-bar headroom is free to absorb a coincident GC pause. Commit moves by item id, so a
            // stack that shifted since Prepare degrades to "no move", never a mis-move.
            if (commitPending)
            {
                commitPending = false;
                var committed = sources[Current].Commit();
                if (committed == CommitResult.Moved)
                {
                    // A transfer that moved something leaves the stack worth re-examining THIS cycle — its target
                    // is now fuller, so the next find tick spills the remainder to the next container instead of
                    // waiting a whole cycle. Capped by the accepting-container count, so a surprise "returned true
                    // but drained nothing" degrades to advance, never a livelock.
                    if (++revisits < revisitCap) return StepResult.Moved;
                    Advance();
                    // Walking past index 0 means this source is finished, and that must be committed HERE: the
                    // walk loop is not re-entered before returning, and a bare cursor of -1 is indistinguishable
                    // from the fresh-source sentinel, so the next find tick would restart this same source from
                    // the top. Harmless on a static inventory (one wasted re-examine), fatal on one refilled from
                    // outside — a running disassembler, drill or collector always has another stack by then, so
                    // the pass would never reach the next source and the cycle would never end.
                    if (resumeIndex < 0) NextSource();
                    return StepResult.Moved;
                }
                // Moved nothing though CanItemsBeAdded reported room at find time. A stack taken between the two
                // ticks is nobody's fault: drop the revisit/skip state and let the next find tick re-examine
                // whatever sits at the cursor now. Otherwise retry the SAME stack against the NEXT candidate
                // (skipTargets steps past the dead one) instead of abandoning it to a dead top-ranked container
                // every cycle — recording a warning only for a genuine refusal (target still present + still
                // reporting room = unreachable/sorter-filtered), not for one that merely filled meanwhile.
                // skipTargets rises monotonically and ChooseTarget returns none once past the last candidate, so
                // an unreachable grid can't thrash: one failed attempt per candidate, then drop for this cycle.
                // Returned as Stalled (not Moved) so the scheduler counts no progress.
                revisits = 0;
                if (committed == CommitResult.ItemGone) { skipTargets = 0; return StepResult.Stalled; }
                if (committed == CommitResult.Refused) RecordUnreachable(chosenTarget, chosenCategory);
                skipTargets = chosenRank + 1;
                return StepResult.Stalled;
            }

            if (stopped) return StepResult.Done;

            // ONE budget spans the whole step, across sources. It must — otherwise a run of already-sorted
            // inventories each gets a full re-examine (and a GetItems snapshot) in a single run, so the cheap
            // scan is unbounded over grid size and trips the 50k-instruction cap. Each item examined and each
            // source finished draws it down; the checks sit AFTER progress is made, so the step always advances
            // at least one item/source before yielding — no livelock even at budget 1.
            var examined = 0;
            while (sourceCursor < sources.Count)
            {
                var src = sources[Current];
                if (src == null) { NextSource(); continue; }

                src.Refresh();
                var n = src.Count;
                // Clamp on entry, and whenever a revisit's stack drained out from under the cursor (i >= n):
                // both mean we're on a different stack now, so drop the stale revisit + skip-target state.
                if (resumeIndex < 0 || resumeIndex >= n) { resumeIndex = n - 1; revisits = 0; skipTargets = 0; }

                while (resumeIndex >= 0)
                {
                    if (examined >= examineBudget) return StepResult.Scanned; // resume here next tick, same source
                    var i = resumeIndex;                        // no pre-decrement: Advance() owns the cursor step
                    examined++;

                    if (!src.ShouldDrain(i)) { Advance(); continue; }

                    var cat = src.CategoryAt(i);
                    var targets = routing.Route(cat, Category.ForKey(src.KeyAt(i)));
                    if (!routing.HasHome(cat)) RecordNoRoute(cat);
                    if (targets.Count == 0) { Advance(); continue; }

                    var rank = ChooseTarget(src, i, targets, skipTargets);
                    if (rank < 0) { Advance(); continue; }       // nothing (more) can take it right now
                    var target = targets[rank];
                    // The demand tier ranks first, so a non-demand pick means no demand target has room.
                    if (src.DemandOnly && !target.IsDemand) { Advance(); continue; }

                    // Leave a stack already sitting in an accepting container UNLESS the chosen target expresses
                    // demand: a below-target machine pulls its fuel/ore even out of a container, but a plain
                    // container→container priority upgrade stays banned (anti-churn). Once the machine hits its
                    // fill target its demand clears (CanPlaceAt false) and this reverts to leave-in-place. A
                    // catch-all is a home only while the item's own category has no room: once the chosen target
                    // is a container naming the category, the stack moves up — and never back, since that
                    // container then holds it as a named home.
                    var home = target.IsDemand ? null : PlacedIn(src, targets);
                    if (home != null && (!routing.InOverflow(home, cat) || routing.InOverflow(target, cat))) { Advance(); continue; }

                    revisitCap = targets.Count;                 // this stack may revisit at most once per acceptor
                    chosenRank = rank;                          // remember for a failed commit's spill + diagnostic
                    chosenTarget = target.Name;                 // captured cheap so the commit tick stays bare
                    chosenCategory = cat;
                    src.Prepare(i, target);
                    commitPending = true;
                    return StepResult.Found;
                }

                NextSource();
                examined++;         // charge the snapshot itself, so sweeping many empty/sorted sources is bounded
                if (examined >= examineBudget) return StepResult.Scanned;
            }
            return StepResult.Done;
        }

        void NextSource() { sourceCursor++; resumeIndex = Unset; revisits = 0; skipTargets = 0; }

        // Step the cursor down one stack and forget the revisit + skip-target state. Walking high-index-first
        // means dropping the current stack never shifts a lower, unseen one.
        void Advance() { resumeIndex--; revisits = 0; skipTargets = 0; }

        // The single placement policy: the highest-ranked routed target with room for the item, searching from
        // `from` (advanced past a target whose commit failed this stack this cycle). Returns the target's rank, or
        // -1 if none at/after `from` will take it. CanPlaceAt is cheap and network-free, so probing several
        // candidates costs nothing near the heavy transfer. Gates on "room for ≥1 unit", so a near-full
        // high-priority container is topped off and the remainder spills to the next candidate.
        static int ChooseTarget(ISortSource src, int index, List<ICategoryAcceptor> targets, int from)
        {
            for (var t = from; t < targets.Count; t++)
                if (src.CanPlaceAt(index, targets[t])) return t;
            return -1;
        }

        void RecordUnreachable(string target, string category)
        {
            foreach (var u in UnreachableRoutes)
                if (u.Target == target && u.Category == category) return;
            UnreachableRoutes.Add(new UnreachableRoute(target, category));
        }

        void RecordNoRoute(string category)
        {
            foreach (var c in noRoute) if (c == category) return;
            noRoute.Add(category);
        }

        // A demand target is a conditional home, not a permanent one — an over-quota reserve's own intake routes
        // under its item's category yet currently refuses it, so counting the source's own intake as "placed"
        // would trap the very excess a KeepQuota drain is trying to shed. Only a non-demand destination counts.
        static ICategoryAcceptor PlacedIn(ISortSource src, List<ICategoryAcceptor> targets)
        {
            foreach (var t in targets)
                if (!t.IsDemand && src.IsSameAs(t)) return t;
            return null;
        }
    }
}
