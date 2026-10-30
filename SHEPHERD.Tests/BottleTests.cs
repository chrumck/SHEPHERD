using System;
using System.Collections.Generic;
using System.Linq;
using IngameScript;
using VRage;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    // Step C.4 — the periodic bottle-refill sweep (BottleJob), the round-trip design with the per-stack refill
    // loop (v0.28.1). The fakes model the two things that make this hard: bottles STACK (an item carries a count),
    // and a transfer into a tank moves only what fits by VOLUME (the tank has a bottle capacity). Refill flips the
    // tank's empty bottles to full and reports whether any were empty (the live FilledRatio-drop signal). This lets
    // the offline harness reproduce the real bug the round-trip fixes — a stack larger than the tank must be
    // drained through it across several lifts, not abandoned after the first tank-volume batch. A non-terminating
    // sweep surfaces as an infinite loop, caught by RunToDone's guard. Physical conveyor threading is Tier-2.
    public class BottleTests
    {
        sealed class Stack
        {
            public int Count;
            public bool Full;
            public Stack(int count, bool full) { Count = count; Full = full; }
        }

        // One inventory's bottle stacks (same-fill bottles share a stack, like SE).
        sealed class Bin
        {
            public readonly List<Stack> Stacks = new List<Stack>();
            public int Total() { var s = 0; for (var i = 0; i < Stacks.Count; i++) s += Stacks[i].Count; return s; }
            public int Count(bool full) { var s = 0; for (var i = 0; i < Stacks.Count; i++) if (Stacks[i].Full == full) s += Stacks[i].Count; return s; }
            public void Add(int count, bool full)   // merge into a same-fill stack or append (SE stacking)
            {
                for (var i = 0; i < Stacks.Count; i++) if (Stacks[i].Full == full) { Stacks[i].Count += count; return; }
                Stacks.Add(new Stack(count, full));
            }
            public void Merge()                      // collapse the tank's now-uniform stacks after a refill
            {
                var empty = 0; var full = 0;
                for (var i = 0; i < Stacks.Count; i++) { if (Stacks[i].Full) full += Stacks[i].Count; else empty += Stacks[i].Count; }
                Stacks.Clear();
                if (empty > 0) Stacks.Add(new Stack(empty, false));
                if (full > 0) Stacks.Add(new Stack(full, true));
            }
        }

        // A transfer target backed by a Bin: Room models CanItemsBeAdded (how many bottles fit); Blocked models a
        // target that reports room yet whose transfer fails (unreachable / sorter-filtered).
        interface IBinTarget { Bin Bin { get; } int Room { get; } bool Blocked { get; } }

        // A storage inventory: a source of bottles AND a return target, mirroring the live InventorySource. Commit
        // moves min(stack, target room) bottles, so a lift into a small tank is a partial (volume-capped) move.
        sealed class Store : ISortSource, ICategoryAcceptor, IBinTarget
        {
            readonly Bin bin;
            public int RoomValue = int.MaxValue;      // storage is effectively unbounded unless a test caps it
            public bool BlockedValue;                 // reports room but the transfer fails
            int pending = -1;
            IBinTarget pendingTarget;

            public Store(Bin b) { bin = b; }
            public Bin Bin => bin;
            public int Room => RoomValue;
            public bool Blocked => BlockedValue;

            public int Refreshes, Probes;
            public void Refresh() { Refreshes++; }
            public int Count => bin.Stacks.Count;
            public string CategoryAt(int i) => bin.Stacks[i].Full ? "F" : "E";
            public string KeyAt(int i) => "OxygenContainerObject/OxygenBottle";
            public double AmountAt(int i) => bin.Stacks[i].Count;
            public bool ShouldDrain(int i) => true;
            public bool DemandOnly => false;
            public bool IsSameAs(ICategoryAcceptor a) => ReferenceEquals(a, this);
            public bool CanPlaceAt(int i, ICategoryAcceptor target) { Probes++; var b = target as IBinTarget; return b != null && b.Room > 0; }
            public void Prepare(int i, ICategoryAcceptor target) { pending = i; pendingTarget = target as IBinTarget; }
            public int DryCommits;
            public CommitResult Commit(bool dry)
            {
                var t = pendingTarget; var idx = pending;
                pendingTarget = null; pending = -1;
                if (dry) { DryCommits++; return CommitResult.Refused; }
                if (t == null || t.Blocked || t.Room <= 0 || idx < 0 || idx >= bin.Stacks.Count) return CommitResult.Refused;
                var st = bin.Stacks[idx];
                var move = Math.Min(st.Count, t.Room);
                if (move <= 0) return CommitResult.Refused;
                st.Count -= move;
                if (st.Count == 0) bin.Stacks.RemoveAt(idx);
                t.Bin.Add(move, st.Full);
                return CommitResult.Moved;
            }
            public IEnumerable<string> AcceptedCategories => new string[0];
            public bool IsCatchAll => false;
            public int Priority => 0;
            public bool IsDemand => false;
            public string Name { get; set; } = "storage";
            public IMyInventory Inventory => null;
            public bool Accepts(MyItemType type) => Room > 0;
            public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
            public void CompleteTransfer(double amount, bool moved) { }
        }

        sealed class Tank : IBottleTank
        {
            public readonly Bin Inv = new Bin();
            public int Cap;
            public bool Gas;
            public int Refills;
            readonly TankIntake intake;
            readonly Store contents;

            public Tank(string name, int cap, bool gas)
            {
                Cap = cap; Gas = gas;
                intake = new TankIntake(this, name);
                contents = new Store(Inv) { Name = name };
            }
            public bool HasGas => Gas;
            public int DryRefills;
            public bool Refill(bool dry)             // fill the tank's empties; report whether any were empty (FilledRatio drop)
            {
                if (dry) { DryRefills++; return false; }
                Refills++;
                if (!Gas) return false;
                var had = false;
                for (var i = 0; i < Inv.Stacks.Count; i++) if (!Inv.Stacks[i].Full) { Inv.Stacks[i].Full = true; had = true; }
                Inv.Merge();
                return had;
            }
            public ICategoryAcceptor Intake => intake;
            public ISortSource Contents => contents;

            sealed class TankIntake : ICategoryAcceptor, IBinTarget
            {
                readonly Tank t;
                public TankIntake(Tank tank, string name) { t = tank; Name = name; }
                public Bin Bin => t.Inv;
                public int Room => t.Gas ? t.Cap - t.Inv.Total() : 0;  // gasless → accepts nothing
                public bool Blocked => false;
                public IEnumerable<string> AcceptedCategories => new string[0];
                public bool IsCatchAll => false;
                public int Priority => 0;
                public bool IsDemand => false;
                public string Name { get; set; }
                public IMyInventory Inventory => null;
                public bool Accepts(MyItemType type) => Room > 0;
                public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
                public void CompleteTransfer(double amount, bool moved) { }
            }
        }

        static List<StepResult> RunToDone(BottleJob job)
        {
            var steps = new List<StepResult>();
            for (var guard = 0; guard < 100000; guard++)
            {
                var s = job.Step();
                steps.Add(s);
                if (s == StepResult.Done) return steps;
            }
            Assert.Fail("BottleJob did not terminate — a bottle stack is being cycled forever");
            return steps;
        }

        // THE headline fix: a single stack of 50 empty bottles is fully filled through ONE gas tank whose
        // inventory only holds 10 at a time. v0.28.0 filled just the first 10 (one tank-volume) and abandoned the
        // rest; the per-stack refill loop drains the whole stack through the tank in one sweep.
        // The most of a counter any one step of the job adds, run to the end.
        static int MostPerStep(BottleJob job, Func<int> counter)
        {
            var most = 0;
            for (var guard = 0; guard < 100000; guard++)
            {
                var before = counter();
                var s = job.Step();
                most = Math.Max(most, counter() - before);
                if (s == StepResult.Done) return most;
            }
            Assert.Fail("BottleJob did not terminate");
            return most;
        }

        static List<IBottleTank> Gasless(int count)
        {
            var tanks = new List<IBottleTank>();
            for (var i = 0; i < count; i++) tanks.Add(new Tank("Tank " + i, 10, false));
            return tanks;
        }

        // Each bottle no tank takes is offered to every tank; at thirty tanks a step of a hundred stacks overran
        // the instruction cap on a large grid.
        [Fact]
        public void EachOfferToATankDrawsTheBudget()
        {
            var bin = new Bin();
            for (var i = 0; i < 50; i++) bin.Stacks.Add(new Stack(1, false));
            var storage = new Store(bin);
            Assert.InRange(MostPerStep(new BottleJob(new List<ISortSource> { storage }, Gasless(30), 10), () => storage.Probes), 1, 40);
        }

        // A tank's bottle with nowhere to go was offered to every storage source in one step.
        [Fact]
        public void ADrainSearchesStorageOnTheBudget()
        {
            var full = new List<ISortSource>();
            for (var i = 0; i < 200; i++) full.Add(new Store(new Bin()) { RoomValue = 0 });
            var tank = new Tank("O2 Tank", 10, false);
            tank.Inv.Add(1, false);
            var contents = (Store)tank.Contents;
            Assert.InRange(MostPerStep(new BottleJob(full, new List<IBottleTank> { tank }, 10), () => contents.Probes), 1, 11);
        }

        // Empty storage ahead of the first stack was walked once per tank in one step.
        [Fact]
        public void ADrySweepsRoundTripsWalkStorageOnTheBudget()
        {
            var stores = new List<Store>();
            for (var i = 0; i < 200; i++) stores.Add(new Store(new Bin()));
            var last = new Bin(); last.Add(1, false);
            stores.Add(new Store(last));
            var job = new BottleJob(new List<ISortSource>(stores), Gasless(5), 10) { Dry = true };
            Assert.InRange(MostPerStep(job, () => stores.Sum(s => s.Refreshes)), 1, 12);
        }

        [Fact]
        public void ADrySweepFillsAndMovesNothing()
        {
            var bin = new Bin(); bin.Add(5, false);
            var storage = new Store(bin);
            var tank = new Tank("O2 Tank", 10, true);

            RunToDone(new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { tank }, 0) { Dry = true });

            Assert.Equal(5, bin.Count(false));
            Assert.Empty(tank.Inv.Stacks);
            Assert.Equal(0, tank.Refills);
            Assert.True(tank.DryRefills > 0);
        }

        [Fact]
        public void ADrySweepWithNoBottlesStillSendsEachTankARoundTrip()
        {
            // Gasless tanks take no bottle, so the walk finds nothing; the round trip still runs, dry.
            var bin = new Bin(); bin.Add(1, true);
            var storage = new Store(bin);
            var a = new Tank("A", 10, false);
            var b = new Tank("B", 10, false);

            RunToDone(new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { a, b }, 0) { Dry = true });

            Assert.Equal(1, a.DryRefills);
            Assert.Equal(1, b.DryRefills);
            Assert.Equal(2, storage.DryCommits);
            Assert.Equal(1, bin.Total());
        }

        [Fact]
        public void BigStackIsFullyFilledThroughASmallTank()
        {
            var bin = new Bin(); bin.Add(50, false);
            var storage = new Store(bin);
            var tank = new Tank("O2 Tank", 10, true);

            var job = new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { tank }, 0);
            RunToDone(job);

            Assert.Equal(50, bin.Count(true));                 // all 50 ended full
            Assert.Equal(0, bin.Count(false));                 // none left empty
            Assert.Empty(tank.Inv.Stacks);                     // nothing stranded in the tank
        }

        // The user's second question: every container with bottles is served, not just the first. Three
        // containers, each with a stack bigger than the tank, all end fully filled.
        [Fact]
        public void EveryContainerWithBottlesIsFilled()
        {
            var a = new Bin(); a.Add(15, false);
            var b = new Bin(); b.Add(25, false);
            var c = new Bin(); c.Add(8, false);
            var sa = new Store(a) { Name = "A" };
            var sb = new Store(b) { Name = "B" };
            var sc = new Store(c) { Name = "C" };
            var tank = new Tank("O2 Tank", 10, true);

            var job = new BottleJob(new List<ISortSource> { sa, sb, sc }, new List<IBottleTank> { tank }, 0);
            RunToDone(job);

            Assert.Equal(15, a.Count(true)); Assert.Equal(0, a.Count(false));
            Assert.Equal(25, b.Count(true)); Assert.Equal(0, b.Count(false));
            Assert.Equal(8, c.Count(true)); Assert.Equal(0, c.Count(false));
        }

        // Refill lands strictly between the lift transfer and the return transfer (bottles are filled while in the
        // tank), and each transfer is its own tick.
        [Fact]
        public void RefillHappensBetweenLiftAndReturn()
        {
            var bin = new Bin(); bin.Add(1, false);
            var storage = new Store(bin);
            var tank = new Tank("O2 Tank", 10, true);

            var job = new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { tank }, 0);
            var steps = RunToDone(job);

            var liftAt = steps.IndexOf(StepResult.Moved);
            var refillAt = steps.IndexOf(StepResult.Refilled);
            var returnAt = steps.LastIndexOf(StepResult.Moved);
            Assert.True(liftAt < refillAt, "lift must precede the refill");
            Assert.True(refillAt < returnAt, "refill must precede the return");
            Assert.Equal(1, bin.Count(true));
        }

        // A gasless tank fills nothing and takes in no storage bottle (its intake rejects while dry), but a bottle
        // already sitting inside it is still drained back out to storage — never left stranded.
        [Fact]
        public void GaslessTankFillsNothingButStillDrains()
        {
            var storage = new Store(new Bin());               // empty storage, ready to receive the drained bottle
            var tank = new Tank("O2 Tank", 10, false);
            tank.Inv.Add(3, false);                            // three empty bottles already inside a dry tank

            var job = new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { tank }, 0);
            RunToDone(job);

            Assert.Equal(3, storage.Bin.Total());              // drained to storage
            Assert.Empty(tank.Inv.Stacks);
            Assert.Empty(job.StrandedTanks);
        }

        // Bottles a user dropped into a tank (or an older build left) are refilled and drained out to storage by
        // the Drain pass, even when no storage bottle round-trips through that tank.
        [Fact]
        public void PreExistingTankBottlesAreFilledAndDrained()
        {
            var storage = new Store(new Bin());
            var tank = new Tank("H2 Tank", 10, true);
            tank.Inv.Add(4, false);

            var job = new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { tank }, 0);
            RunToDone(job);

            Assert.Equal(4, storage.Bin.Count(true));          // all four drained out, full
            Assert.Equal(0, storage.Bin.Count(false));
            Assert.Empty(tank.Inv.Stacks);
        }

        // A bottle uses a tank that has gas; a dry tank is skipped (its intake reports no room).
        [Fact]
        public void BottleSkipsDryTankAndUsesTheOneWithGas()
        {
            var bin = new Bin(); bin.Add(5, false);
            var storage = new Store(bin);
            var dry = new Tank("Dry Tank", 10, false);
            var wet = new Tank("Wet Tank", 10, true);

            var job = new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { dry, wet }, 0);
            RunToDone(job);

            Assert.Empty(dry.Inv.Stacks);                      // dry tank never received a bottle
            Assert.Equal(5, bin.Count(true));                  // all filled via the wet tank
            Assert.Empty(wet.Inv.Stacks);
        }

        // When the Drain pass cannot return a tank's bottles anywhere (every storage source full/unreachable), the
        // tank is recorded so Program can warn (North star #5).
        [Fact]
        public void UnreturnableTankBottlesAreRecordedAsStranded()
        {
            var full = new Store(new Bin()) { RoomValue = 0 };  // storage that reports no room
            var tank = new Tank("O2 Tank", 10, true);
            tank.Inv.Add(2, false);

            var job = new BottleJob(new List<ISortSource> { full }, new List<IBottleTank> { tank }, 0);
            var steps = RunToDone(job);

            Assert.Equal(2, tank.Inv.Total());                 // bottles left in the tank — no route out
            Assert.Contains("O2 Tank", job.StrandedTanks);
            Assert.Equal(StepResult.Done, steps[steps.Count - 1]);
        }

        // A Drain source that reports room but whose transfer fails (unreachable) must not loop forever: the tank
        // is given up and recorded stranded. Without the give-up guard RunToDone's guard would fail.
        [Fact]
        public void DrainGivesUpAnUnreachableTankInsteadOfLooping()
        {
            var unreachable = new Store(new Bin()) { RoomValue = 100, BlockedValue = true };
            var tank = new Tank("O2 Tank", 10, true);
            tank.Inv.Add(2, false);

            var job = new BottleJob(new List<ISortSource> { unreachable }, new List<IBottleTank> { tank }, 0);
            var steps = RunToDone(job);

            Assert.Contains("O2 Tank", job.StrandedTanks);
            Assert.Equal(2, tank.Inv.Total());                 // left in the tank (couldn't be placed)
            Assert.Equal(StepResult.Done, steps[steps.Count - 1]);
        }

        // The scan is bounded by the per-step examine budget: with two stacks no tank will take (a dry tank), budget
        // 1 yields Scanned mid-source rather than sweeping the whole inventory in one tick.
        [Fact]
        public void ScanRespectsExamineBudget()
        {
            var bin = new Bin(); bin.Add(1, false); bin.Add(1, true);
            var storage = new Store(bin);
            var dry = new Tank("Dry Tank", 10, false);          // no gas → intake rejects, nothing is placeable

            var job = new BottleJob(new List<ISortSource> { storage }, new List<IBottleTank> { dry }, 1);
            Assert.Equal(StepResult.Scanned, job.Step());
        }

        [Theory]
        [InlineData("MyObjectBuilder_GasContainerObject", true)]
        [InlineData("MyObjectBuilder_OxygenContainerObject", true)]
        [InlineData("GasContainerObject", true)]              // prefix optional, like the categorizer
        [InlineData("MyObjectBuilder_PhysicalGunObject", false)]
        [InlineData("MyObjectBuilder_Ingot", false)]
        [InlineData("", false)]
        public void IsBottleRecognizesGasContainers(string typeId, bool expected)
        {
            Assert.Equal(expected, Category.IsBottle(typeId));
        }
    }
}
