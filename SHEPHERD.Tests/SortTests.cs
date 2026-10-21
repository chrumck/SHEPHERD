using System.Collections.Generic;
using IngameScript;
using VRage;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    public class SortTests
    {
        // A routing target whose Accepts flag drives Source.CanPlaceAt, so tests can force containers that route
        // but won't take the item (near-full or type-filtered — the case a bare volume check would miss).
        sealed class Target : ICategoryAcceptor
        {
            readonly List<string> cats = new List<string>();
            public Target(bool accepts, bool catchAll, params string[] categories)
            {
                CanAccept = accepts;
                IsCatchAll = catchAll;
                if (categories != null) cats.AddRange(categories);
            }
            public IEnumerable<string> AcceptedCategories => cats;
            public bool IsCatchAll { get; private set; }
            public int Priority { get; set; }
            public bool IsDemand { get; set; }   // a MachineFeed-like target when set: routes first, pulls already-placed stacks
            public bool CanAccept { get; set; }
            public string Name { get; set; }
            public IMyInventory Inventory => null;
            public bool Accepts(MyItemType type) => CanAccept;
            public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
            public void CompleteTransfer(double amount, bool moved) { }
        }

        // A fake inventory of stacks (one category string each). Prepare captures the pending move and Commit
        // applies it (removing the moved stack), so a step-until-done loop converges exactly as the live engine
        // would as items drain out. Mirrors InventorySource's find/commit split.
        sealed class Source : ISortSource
        {
            public readonly List<string> Stacks = new List<string>();
            public readonly HashSet<ICategoryAcceptor> Own = new HashSet<ICategoryAcceptor>();
            // Categories this source keeps (ShouldDrain=false) — models a machine's legitimate contents. Empty by
            // default, so an ordinary source drains everything (existing behavior). Value-based, so it stays correct
            // as Commit removes drained stacks and shifts indices.
            public readonly HashSet<string> Keep = new HashSet<string>();
            public readonly List<string> Moved = new List<string>();
            public readonly List<int> MovedIndex = new List<int>();
            public readonly List<ICategoryAcceptor> MovedTo = new List<ICategoryAcceptor>();
            public int Refreshes;
            int pendingIndex = -1;
            ICategoryAcceptor pendingTarget;

            public Source(params string[] stacks) { if (stacks != null) Stacks.AddRange(stacks); }

            public void Refresh() { Refreshes++; }
            public int Count => Stacks.Count;
            public string CategoryAt(int index) => Stacks[index];
            // Item keys for stacks whose key differs from their category string (an overridden item); a stack
            // not listed uses its category string as its key.
            public readonly Dictionary<string, string> Keys = new Dictionary<string, string>();
            public string KeyAt(int index) { string key; return Keys.TryGetValue(Stacks[index], out key) ? key : Stacks[index]; }
            public double AmountAt(int index) => 1;
            public bool ShouldDrain(int index) => !Keep.Contains(Stacks[index]);
            public bool DemandOnly { get; set; }
            public bool IsSameAs(ICategoryAcceptor acceptor) => Own.Contains(acceptor);
            public bool CanPlaceAt(int index, ICategoryAcceptor target) => ((Target)target).CanAccept;
            public void Prepare(int index, ICategoryAcceptor target) { pendingIndex = index; pendingTarget = target; }
            public CommitResult Commit()
            {
                Moved.Add(Stacks[pendingIndex]);
                MovedIndex.Add(pendingIndex);
                MovedTo.Add(pendingTarget);
                pendingTarget.CompleteTransfer(1, true);
                Stacks.RemoveAt(pendingIndex);
                pendingIndex = -1;
                return CommitResult.Moved;
            }
        }

        // A routed acceptor with finite room, so a Commit can move only PART of a stack (a top-off) and leave
        // the rest — the case B-1.2's revisit-on-moved resolves within one cycle. Chunk caps how much one commit
        // moves even when Room remains (models a target that keeps accepting but drains slowly); Reachable=false
        // models an unreachable target that reports room yet moves nothing (CanItemsBeAdded yes, transfer no).
        sealed class Cap : ICategoryAcceptor
        {
            readonly List<string> cats = new List<string>();
            public int Room;
            public int Chunk = int.MaxValue;
            public bool Reachable = true;
            public Cap(int room, bool catchAll, params string[] categories)
            {
                Room = room;
                IsCatchAll = catchAll;
                if (categories != null) cats.AddRange(categories);
            }
            public IEnumerable<string> AcceptedCategories => cats;
            public bool IsCatchAll { get; private set; }
            public int Priority { get; set; }
            public bool IsDemand { get; set; }
            public string Name { get; set; }
            public IMyInventory Inventory => null;
            public bool Accepts(MyItemType type) => Room > 0;
            public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
            public void CompleteTransfer(double amount, bool moved) { }
        }

        // A fake inventory of (category, quantity) stacks whose Commit does real partial moves against a Cap's
        // room/chunk, so a step-until-done loop converges exactly as the live engine drains stacks. Records the
        // index prepared each find tick, so tests can prove revisit-on-moved (same index re-prepared) vs
        // advance-on-not-moved (a new index) directly.
        sealed class QtySource : ISortSource
        {
            readonly List<string> cats = new List<string>();
            readonly List<int> qtys = new List<int>();
            public readonly List<int> PreparedIndices = new List<int>();
            public readonly List<ICategoryAcceptor> MovedTo = new List<ICategoryAcceptor>();
            public readonly List<int> MovedQty = new List<int>();
            int pendingIndex = -1;
            Cap pendingTarget;

            public QtySource Add(string category, int quantity) { cats.Add(category); qtys.Add(quantity); return this; }

            public void Refresh() { }
            public int Count => cats.Count;
            public string CategoryAt(int index) => cats[index];
            public string KeyAt(int index) => cats[index];
            public double AmountAt(int index) => qtys[index];
            public bool DemandOnly => false;
            public bool ShouldDrain(int index) => true;
            public bool IsSameAs(ICategoryAcceptor acceptor) => false;
            public bool CanPlaceAt(int index, ICategoryAcceptor target) => ((Cap)target).Room > 0;
            public void Prepare(int index, ICategoryAcceptor target)
            {
                pendingIndex = index;
                pendingTarget = (Cap)target;
                PreparedIndices.Add(index);
            }
            public bool VanishOnCommit;      // next Commit: the stack disappears mid-flight (taken by another machine)

            public CommitResult Commit()
            {
                var i = pendingIndex;
                pendingIndex = -1;
                if (VanishOnCommit)
                {
                    VanishOnCommit = false;
                    cats.RemoveAt(i);
                    qtys.RemoveAt(i);
                    return CommitResult.ItemGone;
                }
                if (!pendingTarget.Reachable) return CommitResult.Refused;
                var fits = pendingTarget.Room < pendingTarget.Chunk ? pendingTarget.Room : pendingTarget.Chunk;
                var moved = qtys[i] < fits ? qtys[i] : fits;
                if (moved <= 0) return CommitResult.TargetFull;
                pendingTarget.Room -= moved;
                qtys[i] -= moved;
                MovedTo.Add(pendingTarget);
                MovedQty.Add(moved);
                if (qtys[i] == 0) { cats.RemoveAt(i); qtys.RemoveAt(i); }
                return CommitResult.Moved;
            }
        }

        static List<ISortSource> Sources(params ISortSource[] s) => new List<ISortSource>(s);
        static RoutingIndex Route(params ICategoryAcceptor[] acceptors) => new RoutingIndex(acceptors);

        // One placement spans two Step()s (find then commit); drive both and return the terminal step, so tests
        // that care about "one logical move" stay readable. A Found is always followed by a Moved.
        static StepResult StepOnce(SortJob job)
        {
            var step = job.Step();
            if (step == StepResult.Found)
            {
                step = job.Step();
                Assert.True(step == StepResult.Moved || step == StepResult.Stalled, "a Found must be followed by Moved or Stalled");
            }
            return step;
        }

        static int DriveToDone(SortJob job)
        {
            var moves = 0;
            for (var guard = 0; guard < 10000; guard++)
            {
                var step = job.Step();
                if (step == StepResult.Done) return moves;
                if (step == StepResult.Moved) moves++;
            }
            Assert.Fail("SortJob did not converge");
            return moves;
        }

        static int StalledCount(SortJob job)
        {
            var stalls = 0;
            for (var guard = 0; guard < 10000; guard++)
            {
                var step = job.Step();
                if (step == StepResult.Done) return stalls;
                if (step == StepResult.Stalled) stalls++;
            }
            Assert.Fail("SortJob did not converge");
            return stalls;
        }

        // A source refilled from OUTSIDE between ticks — a disassembling assembler producing into its product
        // inventory, a running drill, a collector. The walk must still finish it and move on: a pass that keeps
        // restarting the same source never completes, so the cycle never ends for as long as the machine runs.
        sealed class Producing : ISortSource
        {
            readonly List<string> stacks = new List<string>();
            readonly string category;
            int pendingIndex = -1;
            public int Moves;

            public Producing(string cat) { category = cat; stacks.Add(cat); }

            public void Refresh() { if (stacks.Count == 0) stacks.Add(category); }
            public int Count => stacks.Count;
            public string CategoryAt(int index) => stacks[index];
            public string KeyAt(int index) => stacks[index];
            public double AmountAt(int index) => 1;
            public bool DemandOnly => false;
            public bool ShouldDrain(int index) => true;
            public bool IsSameAs(ICategoryAcceptor acceptor) => false;
            public bool CanPlaceAt(int index, ICategoryAcceptor target) => ((Target)target).CanAccept;
            public void Prepare(int index, ICategoryAcceptor target) { pendingIndex = index; }
            public CommitResult Commit()
            {
                stacks.RemoveAt(pendingIndex);
                pendingIndex = -1;
                Moves++;
                return CommitResult.Moved;
            }
        }

        [Fact]
        public void AContinuouslyRefilledSourceStillFinishesThePass()
        {
            var dest = new Target(true, false, Categories.Ingots);
            var src = new Producing(Categories.Ingots);
            var job = new SortJob(Sources(src), Route(dest), 0);

            DriveToDone(job);
            Assert.True(job.IsDone);
            // One drain of what was there when the walk reached it; the machine's later output is the NEXT
            // cycle's work, not this pass's — otherwise the pass never ends.
            Assert.Equal(1, src.Moves);
        }

        [Fact]
        public void ASingleStackSourceAdvancesToTheNextSource()
        {
            var dest = new Target(true, false, Categories.Ores);
            var first = new Source(Categories.Ores);
            var second = new Source(Categories.Ores);
            var job = new SortJob(Sources(first, second), Route(dest), 0);

            Assert.Equal(2, DriveToDone(job));
            Assert.Single(first.Moved);
            Assert.Single(second.Moved);
        }

        [Fact]
        public void MovesUnplacedStackToItsTarget()
        {
            var dest = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores);
            var job = new SortJob(Sources(src), Route(dest), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Equal(new[] { Categories.Ores }, src.Moved);
            Assert.Same(dest, src.MovedTo[0]);
            Assert.Equal(StepResult.Done, StepOnce(job));
            Assert.True(job.IsDone);
        }

        [Fact]
        public void FindPreparesThenCommitFiresExactlyOneTransfer()
        {
            var dest = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores, Categories.Ores);
            var job = new SortJob(Sources(src), Route(dest), 0);

            Assert.Equal(StepResult.Found, job.Step()); Assert.Empty(src.Moved);   // find: chosen, nothing moved yet
            Assert.Equal(StepResult.Moved, job.Step()); Assert.Single(src.Moved);  // commit: exactly one transfer
            Assert.Equal(StepResult.Found, job.Step()); Assert.Single(src.Moved);  // no second transfer on find
            Assert.Equal(StepResult.Moved, job.Step()); Assert.Equal(2, src.Moved.Count);
            Assert.Equal(StepResult.Done, StepOnce(job));
        }

        [Fact]
        public void SkipsStackAlreadyInAnAcceptingContainer()
        {
            var dest = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores);
            src.Own.Add(dest); // the source itself is the ore destination

            Assert.Equal(StepResult.Done, new SortJob(Sources(src), Route(dest), 0).Step());
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void SkipsStackWithNoRoute()
        {
            var dest = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ores); // ore, but only an ingots destination exists

            Assert.Equal(StepResult.Done, new SortJob(Sources(src), Route(dest), 0).Step());
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void RecordsNoRouteCategoriesDeduped()
        {
            var dest = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ores, Categories.Ores, Categories.Ingots); // two ore stacks, no ore dest
            var job = new SortJob(Sources(src), Route(dest), 0);

            DriveToDone(job);
            Assert.Equal(new[] { Categories.Ores }, job.NoRouteCategories); // one entry despite two ore stacks
        }

        [Fact]
        public void OverriddenItemFeedsItsBuiltInMachineBeforeItsOwnContainer()
        {
            // Ore/Ice=Ice: the gas generator registers under Ores, yet must still be fed ice ahead of the Ice box.
            var gen = new Target(true, false, Categories.Ores) { IsDemand = true };
            var iceBox = new Target(true, false, "Ice");
            var src = new Source("Ice");
            src.Keys["Ice"] = "Ore/Ice";
            var job = new SortJob(Sources(src), Route(iceBox, gen), 0);

            DriveToDone(job);
            Assert.Equal(new ICategoryAcceptor[] { gen }, src.MovedTo);
            Assert.Empty(job.NoRouteCategories);
        }

        [Fact]
        public void ADemandOnlySourceKeepsStacksOnlyStorageWouldTake()
        {
            var box = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ingots, Categories.Ingots) { DemandOnly = true };
            var job = new SortJob(Sources(src), Route(box), 0);

            DriveToDone(job);
            Assert.True(job.IsDone);
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void ADemandOnlySourceStillFeedsADemandTarget()
        {
            var box = new Target(true, false, Categories.Ingots);
            var reactor = new Target(true, false, Categories.Ingots) { IsDemand = true };
            var src = new Source(Categories.Ingots) { DemandOnly = true };
            var job = new SortJob(Sources(src), Route(box, reactor), 0);

            DriveToDone(job);
            Assert.Equal(new ICategoryAcceptor[] { reactor }, src.MovedTo);
        }

        [Fact]
        public void ADemandOnlySourceWaitsWhenItsDemandTargetIsFull()
        {
            var box = new Target(true, false, Categories.Ingots);
            var reactor = new Target(false, false, Categories.Ingots) { IsDemand = true };
            var src = new Source(Categories.Ingots) { DemandOnly = true };
            var job = new SortJob(Sources(src), Route(box, reactor), 0);

            DriveToDone(job);
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void OverriddenItemDoesNotBorrowBuiltInStorage()
        {
            var oreBox = new Target(true, false, Categories.Ores);
            var src = new Source("Ice");
            src.Keys["Ice"] = "Ore/Ice";
            var job = new SortJob(Sources(src), Route(oreBox), 0);

            DriveToDone(job);
            Assert.Empty(src.Moved);
            Assert.Equal(new[] { "Ice" }, job.NoRouteCategories);
        }

        [Fact]
        public void CustomCategoryFedOnlyByBorrowedMachineStillReportsNoRoute()
        {
            // The machine takes what it burns, but once it fills the rest strands — so the missing Ice box is reported.
            var gen = new Target(true, false, Categories.Ores) { IsDemand = true };
            var src = new Source("Ice");
            src.Keys["Ice"] = "Ore/Ice";
            var job = new SortJob(Sources(src), Route(gen), 0);

            DriveToDone(job);
            Assert.Equal(new ICategoryAcceptor[] { gen }, src.MovedTo);
            Assert.Equal(new[] { "Ice" }, job.NoRouteCategories);
        }

        [Fact]
        public void NoRouteCategoriesEmptyWhenEverythingRoutes()
        {
            var dest = new Target(true, true); // catch-all takes anything
            var src = new Source(Categories.Ores, Categories.Ingots);
            var job = new SortJob(Sources(src), Route(dest), 0);

            DriveToDone(job);
            Assert.Empty(job.NoRouteCategories);
        }

        [Fact]
        public void RoutesToCatchAllWhenNoExplicitAcceptor()
        {
            var overflow = new Target(true, true);
            var src = new Source(Categories.Ores);
            var job = new SortJob(Sources(src), Route(overflow), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(overflow, src.MovedTo[0]);
        }

        [Fact]
        public void FallsToLowerTargetWhenHigherPriorityOneIsFull()
        {
            var full = new Target(false, false, Categories.Ores) { Priority = 5 }; // ranks first, no space
            var open = new Target(true, false, Categories.Ores) { Priority = 0 };
            var src = new Source(Categories.Ores);
            var job = new SortJob(Sources(src), Route(full, open), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(open, src.MovedTo[0]);
        }

        [Fact]
        public void LeavesStackWhenEveryAcceptingContainerIsFull()
        {
            var full = new Target(false, false, Categories.Ores);
            var src = new Source(Categories.Ores);

            Assert.Equal(StepResult.Done, new SortJob(Sources(src), Route(full), 0).Step());
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void ProcessesHighestIndexFirst()
        {
            var dest = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores, Categories.Ores);
            StepOnce(new SortJob(Sources(src), Route(dest), 0));

            Assert.Equal(1, src.MovedIndex[0]); // reverse walk: a full transfer never shifts a lower, unseen index
        }

        [Fact]
        public void DrainsMultipleSourcesLeavingUnroutableItems()
        {
            var dest = new Target(true, false, Categories.Ores);
            var s1 = new Source(Categories.Ores, Categories.Ingots); // ore moves, ingot has no route
            var s2 = new Source(Categories.Ores);
            var job = new SortJob(Sources(s1, s2), Route(dest), 0);

            Assert.Equal(2, DriveToDone(job));
            Assert.True(job.IsDone);
            Assert.Equal(new[] { Categories.Ores }, s1.Moved);
            Assert.Equal(new[] { Categories.Ingots }, s1.Stacks); // left in place
            Assert.Equal(new[] { Categories.Ores }, s2.Moved);
        }

        [Fact]
        public void ToleratesNullSourcesInList()
        {
            var dest = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores);
            var job = new SortJob(new List<ISortSource> { null, src, null }, Route(dest), 0);

            Assert.Equal(1, DriveToDone(job));
            Assert.Single(src.Moved);
        }

        [Fact]
        public void ExamineBudgetSpreadsScanningAcrossSteps()
        {
            var dest = new Target(true, false, Categories.Ores);
            // Reverse walk hits the two no-route ingots first; a budget of 2 yields before reaching the ore.
            var src = new Source(Categories.Ores, Categories.Ingots, Categories.Ingots);
            var job = new SortJob(Sources(src), Route(dest), 2);

            Assert.Equal(StepResult.Scanned, job.Step());
            Assert.Empty(src.Moved);
            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Equal(new[] { Categories.Ores }, src.Moved);
        }

        [Fact]
        public void ExamineBudgetBoundsSourceVisitsPerStep()
        {
            // Regression: pre-fix, one Step() Refreshed EVERY source in a single run when they were all
            // already-sorted (the vanilla 50k-instruction blow-up on a large grid). The budget must bound how
            // many source snapshots a single Step touches, even when none of them move anything.
            var dest = new Target(true, false, Categories.Ores);
            var srcs = new List<ISortSource>();
            for (var i = 0; i < 100; i++) srcs.Add(new Source()); // all empty: nothing to move, one Refresh each
            var job = new SortJob(srcs, Route(dest), 10);

            Assert.Equal(StepResult.Scanned, job.Step()); // bounded, NOT Done — cannot have swept all 100 in one step
            var refreshed = 0;
            foreach (Source s in srcs) refreshed += s.Refreshes;
            Assert.True(refreshed <= 11, "one Step Refreshed " + refreshed + " sources; must be bounded by the budget");
        }

        [Fact]
        public void ScanningManySortedSourcesConvergesOverManySteps()
        {
            var dest = new Target(true, false, Categories.Ores);
            var srcs = new List<ISortSource>();
            for (var i = 0; i < 100; i++) srcs.Add(new Source()); // all empty
            var job = new SortJob(srcs, Route(dest), 10);

            var steps = 0;
            while (job.Step() != StepResult.Done) { steps++; Assert.True(steps < 1000, "did not converge"); }
            Assert.True(steps >= 9, "100 empty sources at budget 10 must spread over many steps; took " + steps);
            var refreshed = 0;
            foreach (Source s in srcs) refreshed += s.Refreshes;
            Assert.Equal(100, refreshed); // every source visited exactly once across the whole cycle
        }

        [Fact]
        public void DoneOnEmptyOrNullInputs()
        {
            Assert.Equal(StepResult.Done, new SortJob(null, Route(), 0).Step());
            Assert.True(new SortJob(null, Route(), 0).IsDone);
            Assert.Equal(StepResult.Done, new SortJob(Sources(), Route(), 0).Step());
            Assert.Equal(StepResult.Done, new SortJob(Sources(new Source()), Route(), 0).Step());
            Assert.Equal(StepResult.Done, new SortJob(Sources(new Source(Categories.Ores)), null, 0).Step());
        }

        [Fact]
        public void PartialMoveSpillsRemainderToTheNextTargetSameCycle()
        {
            // B-1.2: an ore stack of 10 tops off A (priority 5, room 4) then must spill the remaining 6 into B
            // (priority 0, room 10) on the FOLLOWING work tick — the same cycle, not the next full sort cycle.
            var a = new Cap(4, false, Categories.Ores) { Priority = 5 };
            var b = new Cap(10, false, Categories.Ores) { Priority = 0 };
            var src = new QtySource().Add(Categories.Ores, 10);
            var job = new SortJob(Sources(src), Route(a, b), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));   // find A + commit 4 (A now full)
            Assert.Equal(StepResult.Moved, StepOnce(job));   // revisit the SAME stack → find B + commit 6
            Assert.Equal(StepResult.Done, StepOnce(job));

            Assert.Equal(new ICategoryAcceptor[] { a, b }, src.MovedTo);
            Assert.Equal(new[] { 4, 6 }, src.MovedQty);
            Assert.Equal(new[] { 0, 0 }, src.PreparedIndices); // both prepares are the one stack — a revisit, not an advance
        }

        [Fact]
        public void RevisitsAreBoundedSoASiblingStackIsNotStarved()
        {
            // The revisitCap (= a stack's accepting-container count) stops an unbounded revisit chain. A chunking
            // acceptor keeps reporting room and moves only 1 unit per commit, so without the cap revisit-on-moved
            // would chip the whole 50-unit ore stack away before ever touching the ingot sibling. With the cap
            // (ore has ONE acceptor), the sort advances after one ore move, so the sibling is serviced next.
            var chunker = new Cap(1000, false, Categories.Ores) { Chunk = 1 };
            var ingots = new Cap(1000, false, Categories.Ingots);
            var src = new QtySource().Add(Categories.Ingots, 1).Add(Categories.Ores, 50); // ingot@0, big ore@1
            var job = new SortJob(Sources(src), Route(chunker, ingots), 0);

            StepOnce(job); // reverse walk: one capped ore chunk (index 1)
            StepOnce(job); // must be the ingot sibling (index 0), NOT a second ore chunk
            Assert.Equal(new ICategoryAcceptor[] { chunker, ingots }, src.MovedTo);
        }

        [Fact]
        public void KeepsStacksShouldDrainRejectsAndEvictsTheRest()
        {
            // C.2 don't-scatter + eviction: a machine keeps its legitimate contents (Ingots = "fuel") and sheds a
            // foreign item (Ores). The kept stack is never moved; the foreign one is routed out to the catch-all.
            var overflow = new Target(true, true);
            var src = new Source(Categories.Ores, Categories.Ingots);
            src.Keep.Add(Categories.Ingots); // ShouldDrain=false for the fuel
            var job = new SortJob(Sources(src), Route(overflow), 0);

            Assert.Equal(1, DriveToDone(job));
            Assert.Equal(new[] { Categories.Ores }, src.Moved);   // only the foreign item left
            Assert.Equal(new[] { Categories.Ingots }, src.Stacks); // fuel kept in place
        }

        [Fact]
        public void KeepingEveryStackMovesNothing()
        {
            // A machine holding only legitimate contents (all kept) yields no moves — nothing is scattered.
            var overflow = new Target(true, true);
            var src = new Source(Categories.Ores, Categories.Ingots);
            src.Keep.Add(Categories.Ores);
            src.Keep.Add(Categories.Ingots);
            var job = new SortJob(Sources(src), Route(overflow), 0);

            Assert.Equal(0, DriveToDone(job));
            Assert.Empty(src.Moved);
            Assert.Equal(2, src.Stacks.Count);
        }

        // --- Demand-first machine feeding (Step C.3) ----------------------------------------------------------

        [Fact]
        public void DemandTargetRoutesAheadOfContainer()
        {
            // A below-target machine (IsDemand) is filled before a plain container that also accepts the
            // category, even though the container comes first in scan order.
            var box = new Target(true, false, Categories.Ingots);
            var feed = new Target(true, false, Categories.Ingots) { IsDemand = true };
            var src = new Source(Categories.Ingots);
            var job = new SortJob(Sources(src), Route(box, feed), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(feed, src.MovedTo[0]); // routed to the machine, not the container
        }

        [Fact]
        public void DemandTargetPullsStackOutOfAcceptingContainer()
        {
            // The demand-pull / AlreadyPlaced exception: a stack already sitting in an accepting container is
            // pulled into a below-target machine (fuel out of the Ingots box into a hungry reactor).
            var box = new Target(true, false, Categories.Ingots);
            var feed = new Target(true, false, Categories.Ingots) { IsDemand = true };
            var src = new Source(Categories.Ingots);
            src.Own.Add(box); // the source IS the accepting Ingots container
            var job = new SortJob(Sources(src), Route(box, feed), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(feed, src.MovedTo[0]); // pulled into the machine despite already being placed
        }

        [Fact]
        public void MachineAtFillTargetStopsPullingSoItemGoesToContainer()
        {
            // Self-limiting: a machine at its fill target rejects (Accepts=false), so the item routes to the
            // container instead — no overfill.
            var feed = new Target(false, false, Categories.Ingots) { IsDemand = true }; // at target: rejects
            var box = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ingots);
            var job = new SortJob(Sources(src), Route(feed, box), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(box, src.MovedTo[0]); // the full machine is skipped
        }

        [Fact]
        public void FullMachineDoesNotPullFromContainer()
        {
            // Self-limiting anti-churn: a machine at target has no demand, so a stack already in its container
            // is left in place (no pointless pull-then-nothing).
            var feed = new Target(false, false, Categories.Ingots) { IsDemand = true }; // at target
            var box = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ingots);
            src.Own.Add(box);
            var job = new SortJob(Sources(src), Route(feed, box), 0);

            Assert.Equal(StepResult.Done, job.Step());
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void StackLeavesACatchAllOnceItsCategoryHasRoom()
        {
            var catchAll = new Target(true, true);
            var oreBox = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores);
            src.Own.Add(catchAll);
            var job = new SortJob(Sources(src), Route(catchAll, oreBox), 0);

            DriveToDone(job);
            Assert.Equal(new ICategoryAcceptor[] { oreBox }, src.MovedTo);
        }

        [Fact]
        public void StackStaysInACatchAllWhileItsCategoryIsFull()
        {
            var catchAll = new Target(true, true);
            var oreBox = new Target(false, false, Categories.Ores);
            var src = new Source(Categories.Ores);
            src.Own.Add(catchAll);
            var job = new SortJob(Sources(src), Route(catchAll, oreBox), 0);

            DriveToDone(job);
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void NoChurnBetweenCatchAlls()
        {
            var first = new Target(true, true) { Priority = 0 };
            var second = new Target(true, true) { Priority = 5 };
            var src = new Source(Categories.Ores);
            src.Own.Add(first);
            var job = new SortJob(Sources(src), Route(first, second), 0);

            DriveToDone(job);
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void ACatchAllThatNamesTheCategoryIsAlreadyAHome()
        {
            var both = new Target(true, true, Categories.Ores);
            var oreBox = new Target(true, false, Categories.Ores) { Priority = 5 };
            var src = new Source(Categories.Ores);
            src.Own.Add(both);
            var job = new SortJob(Sources(src), Route(both, oreBox), 0);

            DriveToDone(job);
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void NoContainerToContainerChurnOnPriorityUpgrade()
        {
            // A plain higher-priority container never pulls a stack already sitting in a lower one — only a
            // demand target may pull an already-placed stack (AlreadyPlaced anti-churn holds for containers).
            var low = new Target(true, false, Categories.Ores) { Priority = 0 };
            var high = new Target(true, false, Categories.Ores) { Priority = 5 };
            var src = new Source(Categories.Ores);
            src.Own.Add(low); // the source is the low-priority container; the item is already accepted here
            var job = new SortJob(Sources(src), Route(low, high), 0);

            Assert.Equal(StepResult.Done, job.Step());
            Assert.Empty(src.Moved); // NOT upgraded to the higher-priority container
        }

        [Fact]
        public void AFailedCommitReturnsStalledNotMoved()
        {
            // A transfer that moves nothing (unreachable target — reports room but the conveyor call fails) is a
            // Stalled, distinct from a Moved, so the scheduler counts no progress (§11 anti-thrash: a stall-only
            // cycle parks at idle instead of hammering the dead target at the fast cadence).
            var unreachable = new Cap(1000, false, Categories.Ores) { Reachable = false, Name = "DeadBox" };
            var src = new QtySource().Add(Categories.Ores, 5);
            var job = new SortJob(Sources(src), Route(unreachable), 0);

            Assert.Equal(StepResult.Found, job.Step());
            Assert.Equal(StepResult.Stalled, job.Step()); // NOT Moved — nothing was placed
            Assert.Empty(src.MovedQty);
        }

        [Fact]
        public void AFailedCommitWithNoOtherCandidateEventuallyAdvancesToNextStack()
        {
            // With a single (dead) acceptor, a stall exhausts the candidate list for that stack, so the sort then
            // advances to the next stack — no livelock. Two ore stacks, one dead container: each stack is tried
            // once (reverse walk: index 1 then index 0), stalls, and nothing moves.
            var unreachable = new Cap(1000, false, Categories.Ores) { Reachable = false, Name = "DeadBox" };
            var src = new QtySource().Add(Categories.Ores, 5).Add(Categories.Ores, 5);
            var job = new SortJob(Sources(src), Route(unreachable), 0);

            Assert.Equal(2, StalledCount(job));                 // both stacks stall, nothing placed
            Assert.Equal(new[] { 1, 0 }, src.PreparedIndices);  // each stack tried once, in reverse-walk order
            Assert.Empty(src.MovedQty);
        }

        [Fact]
        public void AFailedCommitSpillsToTheNextCandidateTargetSameCycle()
        {
            // §11 correctness: a stack whose top-ranked acceptor is unreachable (reports room, transfer fails) is
            // spilled past to the next reachable candidate the SAME cycle, instead of being abandoned to the dead
            // container every cycle. The dead box ranks first (priority 5); the good box (priority 0) still gets it.
            var dead = new Cap(1000, false, Categories.Ores) { Priority = 5, Reachable = false, Name = "DeadBox" };
            var good = new Cap(1000, false, Categories.Ores) { Priority = 0, Name = "GoodBox" };
            var src = new QtySource().Add(Categories.Ores, 5);
            var job = new SortJob(Sources(src), Route(dead, good), 0);

            Assert.Equal(StepResult.Found, job.Step());   // find: picks the dead box (top priority)
            Assert.Equal(StepResult.Stalled, job.Step()); // commit fails → stall
            Assert.Equal(StepResult.Found, job.Step());   // retry the SAME stack, spilled past the dead box
            Assert.Equal(StepResult.Moved, job.Step());   // commit into the good box
            while (job.Step() != StepResult.Done) { }

            Assert.Single(src.MovedTo);
            Assert.Same(good, src.MovedTo[0]);          // landed in the reachable sibling, not stuck on the dead box
        }

        [Fact]
        public void RecordsUnreachableTargetsDedupedByTargetAndCategory()
        {
            // §11 diagnostic: a dead container that reports room but never accepts a transfer is recorded once
            // (deduped, though two ore stacks both hit it), carrying its name + category for Program's log line.
            var dead = new Cap(1000, false, Categories.Ores) { Reachable = false, Name = "DeadBox" };
            var src = new QtySource().Add(Categories.Ores, 5).Add(Categories.Ores, 5);
            var job = new SortJob(Sources(src), Route(dead), 0);

            DriveToDone(job);
            Assert.Single(job.UnreachableRoutes);
            Assert.Equal("DeadBox", job.UnreachableRoutes[0].Target);
            Assert.Equal(Categories.Ores, job.UnreachableRoutes[0].Category);
        }

        [Fact]
        public void AStackTakenBetweenFindAndCommitRecordsNoUnreachable()
        {
            // Commit moves by item id; a stack another machine grabbed between the find and commit ticks moves
            // nothing. That is an ordinary race, not a conveyor fault — no "can't reach" diagnostic.
            var good = new Cap(1000, false, Categories.Ores) { Name = "GoodBox" };
            var src = new QtySource().Add(Categories.Ores, 5);
            src.VanishOnCommit = true;
            var job = new SortJob(Sources(src), Route(good), 0);

            Assert.Equal(StepResult.Found, job.Step());
            Assert.Equal(StepResult.Stalled, job.Step());   // nothing placed, but nobody's fault
            Assert.Equal(StepResult.Done, job.Step());
            Assert.Empty(job.UnreachableRoutes);
            Assert.Empty(src.MovedQty);
        }

        [Fact]
        public void ATargetFilledBetweenFindAndCommitSpillsSilently()
        {
            // Room verified at find time can be gone by commit time. The stack spills to the next candidate the
            // same cycle (as any failed commit does) but a merely-full container earns no "can't reach" warning.
            var filling = new Cap(1000, false, Categories.Ores) { Priority = 5, Name = "FillingBox" };
            var good = new Cap(1000, false, Categories.Ores) { Name = "GoodBox" };
            var src = new QtySource().Add(Categories.Ores, 5);
            var job = new SortJob(Sources(src), Route(filling, good), 0);

            Assert.Equal(StepResult.Found, job.Step());     // picks the high-priority box while it has room
            filling.Room = 0;                                // another transfer fills it between the two ticks
            Assert.Equal(StepResult.Stalled, job.Step());
            Assert.Equal(StepResult.Found, job.Step());     // spills past it the same cycle
            Assert.Equal(StepResult.Moved, job.Step());
            Assert.Empty(job.UnreachableRoutes);
            Assert.Same(good, src.MovedTo[0]);
        }

        [Fact]
        public void ReachableTargetRecordsNoUnreachable()
        {
            var good = new Cap(1000, false, Categories.Ores) { Name = "GoodBox" };
            var src = new QtySource().Add(Categories.Ores, 5);
            var job = new SortJob(Sources(src), Route(good), 0);

            DriveToDone(job);
            Assert.Empty(job.UnreachableRoutes);
        }

        // --- Reserve / Move (Step D-a) ------------------------------------------------------------------------
        // A reserve intake is just a demand acceptor whose Accepts tracks the pool's per-item deficit, so at the
        // SortJob level it reuses the demand-pull / self-limit machinery. These frame the reserve contract.

        [Fact]
        public void ReservePullsQuotaItemFromCategoryContainer()
        {
            var box = new Target(true, false, Categories.Ingots);
            var reserve = new Target(true, false, Categories.Ingots) { IsDemand = true };
            var src = new Source(Categories.Ingots);
            src.Own.Add(box); // the item is already sitting in its category container
            var job = new SortJob(Sources(src), Route(box, reserve), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(reserve, src.MovedTo[0]); // pulled into the reserve to fill the loadout
        }

        [Fact]
        public void SatisfiedReserveLeavesItemInCategoryContainer()
        {
            // Deficit hit 0 → the intake rejects (Accepts=false), so the item stays put: no over-pull, no ping-pong.
            var reserve = new Target(false, false, Categories.Ingots) { IsDemand = true };
            var box = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ingots);
            src.Own.Add(box);
            var job = new SortJob(Sources(src), Route(reserve, box), 0);

            Assert.Equal(StepResult.Done, job.Step());
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void NonQuotaItemRoutesToItsNormalDestinationNotTheReserve()
        {
            // An item whose category the pool declares but which is not itself a quota item: the intake has no
            // deficit for it (Accepts=false), so it routes to its ordinary category destination.
            var reserve = new Target(false, false, Categories.Ingots) { IsDemand = true };
            var box = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ingots);
            var job = new SortJob(Sources(src), Route(reserve, box), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(box, src.MovedTo[0]);
        }

        [Fact]
        public void ReserveContainerIsNeverDrained()
        {
            // D-a.1 KeepAll: a reserve container is a pure sink — the sort never empties it.
            var box = new Target(true, false, Categories.Ingots);
            var src = new Source(Categories.Ingots, Categories.Ores);
            src.Keep.Add(Categories.Ingots);
            src.Keep.Add(Categories.Ores);
            var job = new SortJob(Sources(src), Route(box), 0);

            Assert.Equal(0, DriveToDone(job));
            Assert.Equal(2, src.Stacks.Count);
        }

        [Fact]
        public void OverQuotaReserveDrainsExcessPastItsOwnRefusingIntake()
        {
            // D-a.2 regression: an over-quota reserve sheds its excess to the category container even though its
            // OWN intake routes under the same category. The intake is a demand target that currently refuses
            // (over target → Accepts=false); AlreadyPlaced must not count that intake as a home, or the excess is
            // trapped forever (the v0.32.0 "nothing moves out of reserve" bug).
            var box = new Target(true, false, Categories.Ingots);
            var intake = new Target(false, false, Categories.Ingots) { IsDemand = true };
            var src = new Source(Categories.Ingots);
            src.Own.Add(intake); // the source IS this reserve container; its own intake routes under Ingots
            var job = new SortJob(Sources(src), Route(intake, box), 0);

            Assert.Equal(StepResult.Moved, StepOnce(job));
            Assert.Same(box, src.MovedTo[0]); // excess drained out to the category container, not trapped
        }

        [Fact]
        public void AllPoolWithTwoContainersDoesNotShuffleBetweenThem()
        {
            // The intra-pool churn guard, provided in D-a.1 by KeepAll: two containers in one All pool never
            // shuffle an item between them (a reserve is never drained), so an infinite All deficit can't ping-pong.
            var reserve = new Target(true, false, Categories.Ingots) { IsDemand = true };
            var a = new Source(Categories.Ingots);
            var b = new Source(Categories.Ingots);
            a.Keep.Add(Categories.Ingots);
            b.Keep.Add(Categories.Ingots);
            var job = new SortJob(Sources(a, b), Route(reserve), 0);

            Assert.Equal(0, DriveToDone(job));
            Assert.Empty(a.Moved);
            Assert.Empty(b.Moved);
        }

        static SortJob Rotated(List<ISortSource> sources, RoutingIndex routing, int start) =>
            new SortJob(sources, routing, 0, start, new List<string>(), new List<UnreachableRoute>());

        [Fact]
        public void RotatedStartVisitsEverySourceOnceInRotatedOrder()
        {
            var dest = new Target(true, false, Categories.Ores);
            Source s0 = new Source(Categories.Ores), s1 = new Source(Categories.Ores), s2 = new Source(Categories.Ores);
            var job = Rotated(Sources(s0, s1, s2), Route(dest), 1);

            StepOnce(job);
            Assert.Equal(new[] { 0, 1, 0 }, new[] { s0.Moved.Count, s1.Moved.Count, s2.Moved.Count });
            StepOnce(job);
            Assert.Equal(new[] { 0, 1, 1 }, new[] { s0.Moved.Count, s1.Moved.Count, s2.Moved.Count });
            StepOnce(job);
            Assert.Equal(new[] { 1, 1, 1 }, new[] { s0.Moved.Count, s1.Moved.Count, s2.Moved.Count });
            Assert.Equal(StepResult.Done, job.Step());
            Assert.True(job.IsDone);
            Assert.Equal(3, job.SourcesFinished);
            Assert.Equal(1, job.NextStart);   // a full lap hands the same rotation point on
        }

        [Fact]
        public void StartPastTheEndIsClamped()
        {
            var dest = new Target(true, false, Categories.Ores);
            Source s0 = new Source(Categories.Ores), s1 = new Source(Categories.Ores);
            var job = Rotated(Sources(s0, s1), Route(dest), 5);
            StepOnce(job);
            Assert.Single(s0.Moved);
            Assert.Empty(s1.Moved);
        }

        [Fact]
        public void StopBeforeAFindEndsThePassWithNoTransfer()
        {
            var dest = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores, Categories.Ores);
            var job = Rotated(Sources(src), Route(dest), 0);
            job.Stop();
            Assert.True(job.IsDone);
            Assert.Equal(StepResult.Done, job.Step());
            Assert.Empty(src.Moved);
        }

        [Fact]
        public void StopAfterAFindStillFiresThatCommit()
        {
            var dest = new Target(true, false, Categories.Ores);
            var src = new Source(Categories.Ores, Categories.Ores);
            var job = Rotated(Sources(src), Route(dest), 0);
            Assert.Equal(StepResult.Found, job.Step());
            job.Stop();
            Assert.False(job.IsDone);
            Assert.Equal(StepResult.Moved, job.Step());
            Assert.True(job.IsDone);
            Assert.Single(src.Moved);
        }

        [Fact]
        public void NextStartResumesTheSourceBeingWorked()
        {
            var dest = new Target(true, false, Categories.Ores);
            var s0 = new Source(Categories.Ores);
            var s1 = new Source(Categories.Ores, Categories.Ores);
            var s2 = new Source(Categories.Ores);
            var job = Rotated(Sources(s0, s1, s2), Route(dest), 0);
            StepOnce(job);           // s0 drained → finished
            StepOnce(job);           // one of s1's two stacks
            job.Stop();
            Assert.True(job.IsDone);
            Assert.Equal(1, job.SourcesFinished);
            Assert.Equal(1, job.NextStart);
        }

        [Fact]
        public void NextStartWrapsPastTheEnd()
        {
            var dest = new Target(true, false, Categories.Ores);
            var s0 = new Source(Categories.Ores, Categories.Ores);
            var s1 = new Source(Categories.Ores);
            var job = Rotated(Sources(s0, s1), Route(dest), 1);
            StepOnce(job);           // s1 drained → finished, now on s0
            StepOnce(job);           // one of s0's stacks
            job.Stop();
            Assert.Equal(1, job.SourcesFinished);
            Assert.Equal(0, job.NextStart);
        }

        [Fact]
        public void DiagnosticsAccumulateIntoTheSuppliedListsAcrossPasses()
        {
            var noRoute = new List<string>();
            var unreachable = new List<UnreachableRoute>();
            var dest = new Target(true, false, Categories.Ingots);
            var first = new SortJob(Sources(new Source(Categories.Ores)), Route(dest), 0, 0, noRoute, unreachable);
            DriveToDone(first);
            var second = new SortJob(Sources(new Source(Categories.Ores, Categories.Ammo)), Route(dest), 0, 0, noRoute, unreachable);
            DriveToDone(second);
            Assert.Equal(new[] { Categories.Ores, Categories.Ammo }, noRoute);   // deduped across both passes
            Assert.Same(noRoute, second.NoRouteCategories);
        }
    }
}
