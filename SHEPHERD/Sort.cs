using System;
using System.Collections.Generic;

namespace IngameScript
{
    // Game-free so SortJob is offline-testable. Indices address a per-step snapshot that Refresh() re-reads at the
    // top of every step, so a stack that shifted or vanished between ticks is never mis-moved.
    public interface ISortSource
    {
        void Refresh();
        int Count { get; }
        string CategoryAt(int index);
        // Category.Key form; the production ledger's stock walk also reads these off the sort's snapshot.
        string KeyAt(int index);
        double AmountAt(int index);
        // A machine that keeps its own contents drains only a FOREIGN item, never what it legitimately holds.
        bool ShouldDrain(int index);
        // Stacks may leave only for a demand target.
        bool DemandOnly { get; }
        bool IsSameAs(ICategoryAcceptor acceptor);
        // CanItemsBeAdded (constraint + volume), so the sort never picks a container that reports free volume yet
        // rejects the item.
        bool CanPlaceAt(int index, ICategoryAcceptor target);
        void Prepare(int index, ICategoryAcceptor target); // on the cheap find tick
        CommitResult Commit(bool dry);                      // the one heavy (conveyor) call, fired alone; dry skips it
    }

    // Only Refused is a conveyor/sorter fault worth a warning: a stack taken between find and commit (ItemGone) or a
    // target that filled meanwhile (TargetFull) is an ordinary race on a busy grid.
    public enum CommitResult { Moved, ItemGone, TargetFull, Refused }

    // A target that reported room yet whose transfer moved nothing: unreachable or sorter-filtered, which the sandbox
    // can't tell apart.
    public struct UnreachableRoute
    {
        public string Target;
        public string Category;
    }

    // The cross-tick sort pass. One lap visits every source once from a rotated start; a pass may be stopped early so a
    // long backlog never holds the rest of the cycle, and the next resumes at NextStart, so a busy early source can't
    // starve later ones. A placement takes a cheap FIND step (Prepare, returns Found) then a bare COMMIT step (the one
    // conveyor call alone), so the heavy call owns its tick and the scheduler can space heavy ticks apart. Stacks are
    // walked high-index-first, so our own transfers never shift an unprocessed lower index. A source or target removed
    // mid-cycle (undock, grind) needs no special case: the engine still answers for it and its commit simply fails.
    public partial class SortJob : IStepJob
    {
        const int Unset = -1;

        readonly List<ISortSource> sources;
        readonly RoutingIndex routing;
        readonly int examineBudget;
        readonly int start;
        // Caller-owned and append-only, so diagnostics accumulate across a lap's passes: a stopped pass saw only part
        // of the grid and can't say a state has cleared.
        readonly List<string> noRoute;
        public readonly List<UnreachableRoute> UnreachableRoutes;
        int sourceCursor;           // sources finished this pass
        bool stopped;
        int resumeIndex = Unset;
        bool commitPending;
        int revisits;               // consecutive moving commits on the current stack
        int revisitCap;
        int skipTargets;            // ChooseTarget starts here, past any target whose commit failed
        int chosenRank;
        string chosenTarget;
        string chosenCategory;
        List<ICategoryAcceptor> warmed; // dry: the targets already sent a move

        // A dry pass moves nothing: every routed stack sends each target not yet reached a move whose engine call is
        // skipped, whether it needs to move or not, so each target's code has run once.
        public bool Dry;
        // Set by Program: the run has passed half the instruction cap. A unit's cost grows with the grid and the cap
        // stops the script outright, so a step yields then too.
        public Func<bool> Full = () => false;

        public SortJob(List<ISortSource> sources, RoutingIndex routing, int examineBudgetPerStep, int startSource,
                       List<string> noRouteSink, List<UnreachableRoute> unreachableSink)
        {
            this.sources = sources;
            this.routing = routing;
            // 0 or less = no cap.
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

        // The source being worked when stopped, so it is resumed rather than skipped; after a full lap, the same start.
        public int NextStart => sources == null || sourceCursor >= sources.Count ? start : Current;

        int Current => (start + sourceCursor) % sources.Count;

        public List<string> NoRouteCategories => noRoute;

        public StepResult Step()
        {
            if (routing == null || sources == null) return StepResult.Done;

            // NOTHING else runs on the commit tick, so all the sub-bar headroom is free to absorb a coincident GC
            // pause. Commit moves by item id, so a stack that shifted since Prepare degrades to no move, never a
            // mis-move.
            if (commitPending)
            {
                commitPending = false;
                var committed = sources[Current].Commit(false);
                if (committed == CommitResult.Moved)
                {
                    // Re-examine the stack this cycle: its target is fuller now, so the next find spills the remainder
                    // to the next container. Capped by the accepting-container count, so a commit that drained
                    // nothing degrades to advance, never a livelock.
                    if (++revisits < revisitCap) return StepResult.Moved;
                    Advance();
                    // Past index 0 the source must be finished HERE: a bare cursor of -1 reads as the fresh-source
                    // sentinel, so the next find would restart this source — forever on one refilled from outside
                    // (a running disassembler, drill or collector), and the cycle would never end.
                    if (resumeIndex < 0) NextSource();
                    return StepResult.Moved;
                }
                // A stack taken meanwhile is nobody's fault: re-examine whatever sits at the cursor now. Otherwise
                // retry the same stack against the next candidate rather than abandon it to a dead top-ranked
                // container every cycle, warning only on a genuine refusal. skipTargets only rises, so an unreachable
                // grid costs one attempt per candidate per cycle. Stalled, not Moved, so the scheduler counts no
                // progress.
                revisits = 0;
                if (committed == CommitResult.ItemGone) { skipTargets = 0; return StepResult.Stalled; }
                if (committed == CommitResult.Refused) RecordUnreachable(chosenTarget, chosenCategory);
                skipTargets = chosenRank + 1;
                return StepResult.Stalled;
            }

            if (stopped) return StepResult.Done;

            // ONE budget spans the step across sources, else a run of already-sorted inventories each gets a full
            // re-examine in one run, unbounded over grid size, and trips the instruction cap. The checks sit AFTER
            // progress, so a step always advances at least one item or source — no livelock even at budget 1.
            var examined = 0;
            while (sourceCursor < sources.Count)
            {
                var src = sources[Current];
                if (src == null) { NextSource(); continue; }

                src.Refresh();
                var n = src.Count;
                // On entry, or when a revisit's stack drained out from under the cursor, we're on a different stack
                // now, so the revisit + skip-target state is stale.
                if (resumeIndex < 0 || resumeIndex >= n) { resumeIndex = n - 1; revisits = 0; skipTargets = 0; }

                while (resumeIndex >= 0)
                {
                    if (examined >= examineBudget || Full()) return StepResult.Scanned; // resume here next tick, same source
                    var i = resumeIndex;                        // no pre-decrement: Advance() owns the cursor step
                    examined++;

                    if (!src.ShouldDrain(i) && !Dry) { Advance(); continue; }

                    var cat = src.CategoryAt(i);
                    var targets = routing.Route(cat, Category.ForKey(src.KeyAt(i)));
                    if (!routing.HasHome(cat)) RecordNoRoute(cat);
                    if (targets.Count == 0) { Advance(); continue; }

                    var rank = ChooseTarget(src, i, targets, skipTargets);
                    // A dry stack walks its whole route three times, and a quoted item's route holds every reserve
                    // container of its pool, so the route's length draws the budget.
                    if (Dry)
                    {
                        var placed = PlacedIn(src, targets);
                        if (placed != null) routing.InOverflow(placed, cat);
                        SendDry(src, i, targets);
                        examined += targets.Count;
                        Advance();
                        continue;
                    }
                    if (rank < 0) { Advance(); continue; }
                    var target = targets[rank];
                    // The demand tier ranks first, so a non-demand pick means no demand target has room.
                    if (src.DemandOnly && !target.IsDemand) { Advance(); continue; }

                    // Anti-churn: a stack already in an accepting container stays, unless a demand target (a
                    // below-target machine) wants it — no container→container priority upgrade. A catch-all is a home
                    // only while the item's own category has no room: the stack then moves up to a container naming
                    // it, and never back.
                    var home = target.IsDemand ? null : PlacedIn(src, targets);
                    if (home != null && (!routing.InOverflow(home, cat) || routing.InOverflow(target, cat))) { Advance(); continue; }

                    revisitCap = targets.Count;                 // at most one revisit per acceptor
                    chosenRank = rank;
                    chosenTarget = target.Name;                 // captured cheap so the commit tick stays bare
                    chosenCategory = cat;
                    src.Prepare(i, target);
                    commitPending = true;
                    return StepResult.Found;
                }

                NextSource();
                examined++;         // charge the snapshot, so sweeping many empty or sorted sources is bounded
                if (examined >= examineBudget || Full()) return StepResult.Scanned;
            }
            return StepResult.Done;
        }

        void SendDry(ISortSource src, int index, List<ICategoryAcceptor> targets)
        {
            if (warmed == null) warmed = new List<ICategoryAcceptor>();
            foreach (var t in targets)
            {
                if (warmed.Contains(t)) continue;
                warmed.Push(t);
                src.Prepare(index, t);
                src.Commit(true);
            }
        }

        void NextSource() { sourceCursor++; resumeIndex = Unset; revisits = 0; skipTargets = 0; }

        // Walking high-index-first, dropping the current stack never shifts a lower, unseen one.
        void Advance() { resumeIndex--; revisits = 0; skipTargets = 0; }

        // CanPlaceAt is cheap and network-free, so probing several candidates costs nothing beside the transfer. It
        // gates on room for one unit, so a near-full high-priority container is topped off and the rest spills on.
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
            UnreachableRoutes.Push(new UnreachableRoute { Target = target, Category = category });
        }

        void RecordNoRoute(string category)
        {
            foreach (var c in noRoute) if (c == category) return;
            noRoute.Push(category);
        }

        // Only a non-demand destination counts: an over-quota reserve's own intake routes the item yet refuses it, so
        // counting it as placed would trap the very excess a KeepQuota drain is shedding.
        static ICategoryAcceptor PlacedIn(ISortSource src, List<ICategoryAcceptor> targets)
        {
            foreach (var t in targets)
                if (!t.IsDemand && src.IsSameAs(t)) return t;
            return null;
        }
    }
}
