using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class CraftTests
    {
        const string Plate = "Component/SteelPlate";
        const string PlateBp = "SteelPlate";
        const string Missile = "AmmoMagazine/Missile200mm";
        const string MissileBp = "Position0100_Missile200mm";
        const string Iron = "Ingot/Iron";

        // A live-like assembler: a real queue the planner's snapshots and the job's mutations act against, plus a
        // compatibility whitelist (null = accepts everything).
        sealed class Assembler : IAssemblerState
        {
            public readonly List<QueueEntry> Queue = new List<QueueEntry>();
            public readonly List<string> Mutations = new List<string>();
            HashSet<string> usable;

            public Assembler(long id) { Id = id; }

            public long Id { get; private set; }
            public string Name => "A" + Id;
            public int Construct { get; set; }
            public bool IsReceiver { get; set; } = true;
            public AssemblerMark Mark { get; set; } = AssemblerMark.Main;
            public bool CanCooperate { get; set; } = true;
            public bool Cooperative { get; set; }
            public void SetCooperative(bool on, bool dry)
            {
                if (dry) { DryCalls++; return; }
                Cooperative = on;
            }
            string model;
            public string Model { get { return model ?? "M" + Id; } set { model = value; } }
            public double Speed { get; set; } = 1;
            public int CanUseCalls;
            public bool IsWorking => true;
            public bool IsOperational { get; set; } = true;
            public bool IsDisassembling { get; set; }

            public Assembler Usable(params string[] blueprints)
            {
                usable = new HashSet<string>(blueprints);
                return this;
            }

            public Assembler Enqueue(string blueprint, double amount)
            {
                Queue.Add(new QueueEntry { Blueprint = blueprint, Amount = amount });
                return this;
            }

            public void QueueInto(List<QueueEntry> into)
            {
                into.Clear();
                into.AddRange(Queue);
            }

            public void OutputInto(Dictionary<string, double> into) => into.Clear();

            public bool CanUse(string blueprint)
            {
                CanUseCalls++;
                return usable == null || usable.Contains(blueprint);
            }

            public int DryCalls;

            public void Append(string blueprint, double amount, bool dry)
            {
                if (dry) { DryCalls++; return; }
                Queue.Add(new QueueEntry { Blueprint = blueprint, Amount = amount });
                Mutations.Add("append " + blueprint + " " + amount);
            }

            public void SetDisassembling(bool on, bool dry)
            {
                if (dry) { DryCalls++; return; }
                if (IsDisassembling != on) Mutations.Add(on ? "disassembly" : "assembly");
                IsDisassembling = on;
            }

            public void ClearQueue(bool dry)
            {
                if (dry) { DryCalls++; return; }
                Queue.Clear();
                Mutations.Add("clear");
            }

            public double Trim(string blueprint, double amount, bool dry)
            {
                if (dry) { DryCalls++; return 0; }
                for (var i = Queue.Count - 1; i >= 0; i--)
                {
                    if (Queue[i].Blueprint != blueprint) continue;
                    amount = System.Math.Min(amount, Queue[i].Amount - (i == 0 ? 1 : 0));
                    if (amount < 1) return 0;
                    Queue[i] = new QueueEntry { Blueprint = blueprint, Amount = Queue[i].Amount - amount };
                    if (Queue[i].Amount == 0) Queue.RemoveAt(i);
                    Mutations.Add("trim " + blueprint + " " + amount);
                    return amount;
                }
                return 0;
            }
        }

        sealed class Rig
        {
            public readonly ProductionLedger Ledger = new ProductionLedger();
            public readonly ProductionFacts Facts = new ProductionFacts();
            public readonly CraftPlanner Planner = new CraftPlanner();
            public readonly List<AssemblerSnapshot> Snapshots = new List<AssemblerSnapshot>();
            readonly List<ReservePool> pools = new List<ReservePool>();

            public Rig()
            {
                Ledger.Begin();
            }

            public Rig Quota(string key, string quota)
            {
                var pool = new ReservePool("P");
                IngameScript.Quota q;
                Assert.True(IngameScript.Quota.TryParse(quota, out q));
                pool.SetQuota(key, q);
                Ledger.AddQuotas(pool);
                pools.Add(pool);
                return this;
            }

            public Rig Snapshot(Assembler a)
            {
                Snapshots.Add(SnapshotOf(a));
                return this;
            }

            public void Plan() => Planner.Plan(Ledger, Facts, Snapshots);

            // A fresh cycle: the ledger refilled from the quotas and the snapshots retaken from the live queues.
            public Rig Recount(double stock, string key = Plate)
            {
                Ledger.Begin();
                foreach (var p in pools) Ledger.AddQuotas(p);
                Ledger.AddStock(key, stock);
                for (var i = 0; i < Snapshots.Count; i++)
                {
                    var a = (Assembler)Snapshots[i].State;
                    Snapshots[i] = SnapshotOf(a);
                    foreach (var e in a.Queue)
                        if (Facts.ItemOf(e.Blueprint) != key) continue;
                        else if (a.IsDisassembling) Ledger.AddOutgoing(key, e.Amount);
                        else if (a.IsOperational) Ledger.AddIncoming(key, e.Amount);
                }
                return this;
            }


            public void Plan(double nowSeconds) => Planner.Plan(Ledger, Facts, Snapshots, 0, nowSeconds);

            public void Execute()
            {
                var job = new CraftJob();
                job.Rearm(Planner, Planner.Actions);
                var guard = 0;
                while (job.Step() != StepResult.Done) Assert.True(++guard < 100);
            }
        }

        [Fact]
        public void ADryJobMakesNoneOfItsActions()
        {
            var rig = new Rig();
            var a = new Assembler(1).Enqueue(PlateBp, 5);
            var actions = new List<CraftAction>();
            for (var k = CraftActionKind.AppendAssembly; k <= CraftActionKind.Trim; k++)
                actions.Add(new CraftAction { Kind = k, Assembler = a, Blueprint = PlateBp, Amount = 2, Item = "" });
            var job = new CraftJob { Dry = true };
            job.Rearm(rig.Planner, actions);
            while (job.Step() != StepResult.Done) { }

            Assert.Empty(a.Mutations);
            Assert.Equal(7, a.DryCalls);
            Assert.Equal(5, a.Queue[0].Amount);
        }

        [Fact]
        public void DeficitSkipsAnIncompatibleAssembler()
        {
            var a1 = new Assembler(1).Usable("Nothing");
            var a2 = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(a1).Snapshot(a2);
            rig.Ledger.AddStock(Plate, 30);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(CraftActionKind.AppendAssembly, action.Kind);
            Assert.Same(a2, action.Assembler);
            Assert.Equal(PlateBp, action.Blueprint);
            Assert.Equal(70, action.Amount);
            Assert.Equal(Plate, action.Item);
        }

        [Fact]
        public void TwoPoolsWantingOneItemQueueOneCombinedOrder()
        {
            var a = new Assembler(1);
            var rig = new Rig().Quota(Plate, "100MA").Quota(Plate, "150MA").Snapshot(a);
            rig.Ledger.AddStock(Plate, 60);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(190, action.Amount);        // 250 combined target against stock counted once
            rig.Execute();
            Assert.Single(a.Mutations);
        }

        [Fact]
        public void ExceptionBlueprintItemsCraftThroughTheBakedMap()
        {
            var a = new Assembler(1);
            var rig = new Rig().Quota(Missile, "10A").Snapshot(a);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(MissileBp, action.Blueprint);
            Assert.Equal(10, action.Amount);
        }

        [Fact]
        public void QueueAlreadyCoveringTheDeficitPlansNothing()
        {
            var a = new Assembler(1).Enqueue(PlateBp, 70);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(a);
            rig.Ledger.AddStock(Plate, 30);
            rig.Ledger.AddIncoming(Plate, 70);       // what LedgerJob attributes from the live queue
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
        }

        [Fact]
        public void SubUnitDeficitIsADeadband()
        {
            var a = new Assembler(1);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(a);
            rig.Ledger.AddStock(Plate, 99.4);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
        }

        [Fact]
        public void UnknownBlueprintReportsNeedBlueprint()
        {
            var a = new Assembler(1).Usable(PlateBp);   // rejects every convention candidate for the modded item
            var rig = new Rig().Quota("Component/ModAlloy", "10A").Snapshot(a);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Contains("Component/ModAlloy", rig.Planner.NeedBlueprint);
        }

        [Fact]
        public void KnownBlueprintWithNoCompatibleAssemblerReportsNoAssembler()
        {
            var a = new Assembler(1).Usable("SomethingElse");
            var rig = new Rig().Quota(Missile, "10A").Snapshot(a);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Contains(Missile, rig.Planner.Unassigned[CraftPlanner.NoAssembler]);
            Assert.Empty(rig.Planner.NeedBlueprint);
        }

        [Fact]
        public void DisassemblingAssemblerNeverReceivesAssemblyWork()
        {
            var a = new Assembler(1) { IsDisassembling = true };
            var rig = new Rig().Quota(Missile, "10A").Snapshot(a);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Contains(Missile, rig.Planner.Unassigned[CraftPlanner.MainBusy]);
        }

        [Fact]
        public void ReservedAssemblerReceivesNoNormalCraftWork()
        {
            var reserved = new Assembler(1);
            var available = new Assembler(2);
            var rig = new Rig().Quota(Plate, "10A").Snapshot(reserved).Snapshot(available);
            rig.Planner.Plan(rig.Ledger, rig.Facts, rig.Snapshots, 1, 0);
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Same(available, action.Assembler);
        }

        [Fact]
        public void SurplusAcquiresAnEmptyAssemblerSwitchesAndTracksIt()
        {
            var busy = new Assembler(1).Enqueue(PlateBp, 5);
            var idle = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(busy).Snapshot(idle);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(2, rig.Planner.Actions.Count);
            Assert.Equal(CraftActionKind.SetDisassembly, rig.Planner.Actions[0].Kind);
            Assert.Same(idle, rig.Planner.Actions[0].Assembler);
            Assert.Equal(CraftActionKind.AppendDisassembly, rig.Planner.Actions[1].Kind);
            Assert.Equal(400, rig.Planner.Actions[1].Amount);
            Assert.Equal(2, rig.Planner.TrackedDisassembler);

            rig.Execute();
            Assert.True(idle.IsDisassembling);
            Assert.Equal(new[] { "disassembly", "append " + PlateBp + " 400" }, idle.Mutations);
            Assert.Empty(busy.Mutations);
        }

        [Fact]
        public void AnEmptyAssemblerTheUserLeftInDisassemblyIsNeverAcquired()
        {
            var users = new Assembler(2) { IsDisassembling = true };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(users);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
            Assert.Contains(Plate, rig.Planner.Unassigned[CraftPlanner.Disassembly + CraftPlanner.MainBusy]);
        }

        [Fact]
        public void DisassemblyPassesOverTheUsersDisassemblerForAnAssemblyModeOne()
        {
            var users = new Assembler(1) { IsDisassembling = true };
            var idle = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(users).Snapshot(idle);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(2, rig.Planner.Actions.Count);
            Assert.All(rig.Planner.Actions, a => Assert.Same(idle, a.Assembler));
            Assert.Equal(2, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void MoreSurplusAppendsToTheTrackedDisassemblerWithoutAnotherSwitch()
        {
            var mine = new Assembler(7) { IsDisassembling = true }.Enqueue(PlateBp, 100);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 500);
            rig.Ledger.AddOutgoing(Plate, 100);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(CraftActionKind.AppendDisassembly, action.Kind);
            Assert.Equal(300, action.Amount);                    // 500 - 100 retained - 100 already outgoing
            Assert.Equal(7, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void TrackedDisassemblerRestoresWhenItsQueueEmpties()
        {
            var mine = new Assembler(7) { IsDisassembling = true };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 100);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(CraftActionKind.RestoreAssembly, action.Kind);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);

            rig.Execute();
            Assert.False(mine.IsDisassembling);
        }

        [Fact]
        public void ReleaseAndReacquireNeverShareAPlan()
        {
            var mine = new Assembler(7) { IsDisassembling = true };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 500);                     // surplus present while the tracked one empties
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);     // restore only; acquisition waits a cycle
            Assert.Equal(CraftActionKind.RestoreAssembly, action.Kind);
        }

        [Fact]
        public void AVanishedTrackedDisassemblerIsForgottenWithoutActions()
        {
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1));
            rig.Planner.TrackedDisassembler = 42;
            rig.Ledger.AddStock(Plate, 100);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void AnOccupiedTrackedDisassemblerIsLeftAlone()
        {
            var mine = new Assembler(7) { IsDisassembling = true }.Enqueue(PlateBp, 50);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 100);
            rig.Ledger.AddOutgoing(Plate, 50);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(7, rig.Planner.TrackedDisassembler);    // still ours; restore comes when it empties
        }

        [Fact]
        public void SwitchedOffAssemblerGetsNoAssemblyWork()
        {
            var off = new Assembler(1) { IsOperational = false };
            var on = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(off).Snapshot(on);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Same(on, action.Assembler);
        }

        [Fact]
        public void OnlySwitchedOffAssemblersReportsMainOff()
        {
            var off = new Assembler(1) { IsOperational = false };
            var rig = new Rig().Quota(Missile, "10A").Snapshot(off);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Contains(Missile, rig.Planner.Unassigned[CraftPlanner.MainOff]);
        }

        [Fact]
        public void SwitchedOffAssemblerIsNotAcquiredForDisassembly()
        {
            var off = new Assembler(1) { IsOperational = false };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(off);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void SwitchedOffTrackedDisassemblerGetsNoMoreWork()
        {
            var mine = new Assembler(7) { IsDisassembling = true, IsOperational = false }.Enqueue(PlateBp, 100);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 500);
            rig.Ledger.AddOutgoing(Plate, 100);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(7, rig.Planner.TrackedDisassembler);   // still ours, resumed once switched back on
        }

        [Fact]
        public void NoEmptyAssemblerMeansSurplusWaits()
        {
            var busy = new Assembler(1).Enqueue(PlateBp, 5);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(busy);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);                   // never switch an occupied assembler's mode
            Assert.Contains(Plate, rig.Planner.Unassigned[CraftPlanner.Disassembly + CraftPlanner.NoAssembler]);
        }

        [Fact]
        public void IngotSurplusNeverEntersAnAssembler()
        {
            var idle = new Assembler(1);
            var rig = new Rig().Quota(Iron, "100A").Snapshot(idle);
            rig.Ledger.AddStock(Iron, 5000);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
        }

        [Fact]
        public void TrackedDisassemblerNeverReceivesAssemblyWork()
        {
            var mine = new Assembler(7) { IsDisassembling = false }.Enqueue(PlateBp, 1);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;                 // e.g. re-seeded after recompile, user flipped mode
            rig.Ledger.AddStock(Plate, 30);
            rig.Ledger.AddIncoming(Plate, 1);
            rig.Plan();
            foreach (var a in rig.Planner.Actions)
                Assert.NotEqual(CraftActionKind.AppendAssembly, a.Kind);
        }

        [Fact]
        public void CraftJobExecutesExactlyOneMutationPerStep()
        {
            var idle = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(idle);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            var job = new CraftJob();
            job.Rearm(rig.Planner, rig.Planner.Actions);
            Assert.Equal(StepResult.Moved, job.Step());
            Assert.Equal(new[] { "disassembly" }, idle.Mutations);
            Assert.Equal(StepResult.Moved, job.Step());
            Assert.Equal(2, idle.Mutations.Count);
            Assert.True(job.IsDone);
            Assert.Equal(StepResult.Done, job.Step());
        }

        [Fact]
        public void AssemblyHoldsWhileADisassemblyQueueStandsAndResumesWhenItClears()
        {
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1));
            rig.Ledger.AddOutgoing(Plate, 40);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Contains(Plate, rig.Planner.HeldAssembly);
            Assert.Empty(rig.Planner.NeedBlueprint);
            Assert.Empty(rig.Planner.Unassigned[CraftPlanner.NoAssembler]);

            rig.Ledger.Begin();
            rig.Quota(Plate, "100A");
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(CraftActionKind.AppendAssembly, action.Kind);
            Assert.Empty(rig.Planner.HeldAssembly);
        }

        [Fact]
        public void PreexistingOpposingQueuesAreLeftUntouched()
        {
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1));
            rig.Ledger.AddIncoming(Plate, 100);
            rig.Ledger.AddOutgoing(Plate, 10);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Empty(rig.Planner.HeldAssembly);
            Assert.Empty(rig.Planner.HeldDisassembly);
        }

        [Fact]
        public void DisassemblyHoldsWhileAnAssemblyQueueStands()
        {
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1));
            rig.Ledger.AddStock(Plate, 150);
            rig.Ledger.AddIncoming(Plate, 10);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Contains(Plate, rig.Planner.HeldDisassembly);
        }

        [Fact]
        public void LegitimateDisassemblyInProgressRaisesNoHold()
        {
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1));
            rig.Ledger.AddStock(Plate, 140);
            rig.Ledger.AddOutgoing(Plate, 40);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Empty(rig.Planner.HeldAssembly);
            Assert.Empty(rig.Planner.HeldDisassembly);
        }

        [Fact]
        public void DeficitSplitsInProportionToAssemblerSpeed()
        {
            var basic = new Assembler(1) { Speed = 0.7 };
            var advanced = new Assembler(2) { Speed = 5 };
            var upgraded = new Assembler(3) { Speed = 1 + 2 * 0.5 };
            var rig = new Rig().Quota(Plate, "770A").Snapshot(basic).Snapshot(advanced).Snapshot(upgraded);
            rig.Plan();
            Assert.Equal(3, rig.Planner.Actions.Count);
            Assert.Equal(70, AmountFor(rig, basic));
            Assert.Equal(500, AmountFor(rig, advanced));
            Assert.Equal(200, AmountFor(rig, upgraded));
        }

        [Fact]
        public void SplitShareTopsQueuesUpToACommonFinish()
        {
            var busy = new Assembler(1).Enqueue("Motor", 60);
            var idle = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(busy).Snapshot(idle);
            rig.Plan();
            Assert.Equal(20, AmountFor(rig, busy));        // both end at 80 queued
            Assert.Equal(80, AmountFor(rig, idle));
        }

        [Fact]
        public void AnAssemblerAlreadyPastTheFinishLevelGetsNothing()
        {
            var busy = new Assembler(1).Enqueue("Motor", 500);
            var idle = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(busy).Snapshot(idle);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Same(idle, action.Assembler);
            Assert.Equal(100, action.Amount);
        }

        [Fact]
        public void ASmallDeficitIsNotFragmentedBelowTheMinimumShare()
        {
            var slow = new Assembler(1);
            var fast = new Assembler(2) { Speed = 2 };
            var rig = new Rig().Quota(Plate, "6A").Snapshot(slow).Snapshot(fast);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Same(fast, action.Assembler);
            Assert.Equal(6, action.Amount);
        }

        [Fact]
        public void WholeUnitSharesSumExactlyToTheDeficit()
        {
            var rig = new Rig().Quota(Plate, "100A")
                .Snapshot(new Assembler(1)).Snapshot(new Assembler(2)).Snapshot(new Assembler(3));
            rig.Plan();
            var total = 0.0;
            foreach (var a in rig.Planner.Actions)
            {
                Assert.Equal(System.Math.Floor(a.Amount), a.Amount);
                Assert.InRange(a.Amount, 33, 34);
                total += a.Amount;
            }
            Assert.Equal(3, rig.Planner.Actions.Count);
            Assert.Equal(100, total);
        }

        [Fact]
        public void WorkPlannedForAnEarlierItemCountsAsQueued()
        {
            var both = new Assembler(1).Usable(MissileBp, PlateBp);
            var platesOnly = new Assembler(2).Usable(PlateBp);
            var rig = new Rig().Quota(Missile, "50A").Quota(Plate, "50A").Snapshot(both).Snapshot(platesOnly);
            rig.Plan();
            Assert.Equal(2, rig.Planner.Actions.Count);
            foreach (var a in rig.Planner.Actions)
                Assert.Same(a.Item == Missile ? both : platesOnly, a.Assembler);
        }

        [Fact]
        public void CompatibilityIsProbedOncePerModel()
        {
            var a1 = new Assembler(1) { Model = "LargeAssembler" };
            var a2 = new Assembler(2) { Model = "LargeAssembler" };
            var a3 = new Assembler(3) { Model = "LargeAssembler" };
            var rig = new Rig().Quota(Missile, "90A").Snapshot(a1).Snapshot(a2).Snapshot(a3);
            rig.Plan();
            Assert.Equal(3, rig.Planner.Actions.Count);
            Assert.Equal(1, a1.CanUseCalls + a2.CanUseCalls + a3.CanUseCalls);
        }

        [Fact]
        public void SurplusAcquiresTheFastestEmptyAssembler()
        {
            var slow = new Assembler(1) { Speed = 0.7 };
            var fast = new Assembler(2) { Speed = 5 };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(slow).Snapshot(fast);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(2, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void AnAssemblerGivenAssemblyWorkIsNotAcquiredForDisassembly()
        {
            var a = new Assembler(1);
            var rig = new Rig().Quota(Missile, "10A").Quota(Plate, "100A").Snapshot(a);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(CraftActionKind.AppendAssembly, action.Kind);
            Assert.Contains(Plate, rig.Planner.Unassigned[CraftPlanner.Disassembly + CraftPlanner.NoAssembler]);
        }

        static double AmountFor(Rig rig, Assembler a)
        {
            var amount = 0.0;
            foreach (var action in rig.Planner.Actions)
                if (action.Assembler == a) amount += action.Amount;
            return amount;
        }

        static AssemblerSnapshot SnapshotOf(Assembler a)
        {
            var amount = 0.0;
            foreach (var entry in a.Queue) amount += entry.Amount;
            return new AssemblerSnapshot { State = a, QueueEmpty = a.Queue.Count == 0, QueueAmount = amount, Speed = a.Speed };
        }

        static bool Clears(CraftPlanner planner, long id)
        {
            foreach (var a in planner.Actions)
                if (a.Kind == CraftActionKind.ClearQueue && a.Assembler.Id == id) return true;
            return false;
        }

        [Fact]
        public void PlannerClearsAnUnchangedTrackedQueueAfterTheWindow()
        {
            var a = new Assembler(7) { IsDisassembling = true }.Enqueue(PlateBp, 10);
            var rig = new Rig().Snapshot(a);
            rig.Planner.TrackedDisassembler = 7;
            rig.Plan(0.0);
            Assert.Null(rig.Planner.StalledQueueStatus);
            rig.Plan(119.0);
            Assert.Null(rig.Planner.StalledQueueStatus);
            Assert.False(Clears(rig.Planner, 7));
            rig.Plan(120.0);
            Assert.Equal("clearing stalled disassembly queue on A7", rig.Planner.StalledQueueStatus);
            Assert.True(Clears(rig.Planner, 7));
            var job = new CraftJob();
            job.Rearm(rig.Planner, rig.Planner.Actions);
            while (job.Step() != StepResult.Done) { }
            Assert.Empty(a.Queue);
            Assert.Equal(new[] { "clear" }, a.Mutations);
            Assert.Equal(7, rig.Planner.TrackedDisassembler);    // the next plan sees it empty and restores it
        }

        [Fact]
        public void EmptiedTrackedQueueIsRestoredOnTheNextPlan()
        {
            var a = new Assembler(7).Enqueue(PlateBp, 10);
            a.IsDisassembling = true;
            var rig = new Rig().Snapshot(a);
            rig.Planner.TrackedDisassembler = 7;
            rig.Plan(0.0);
            rig.Plan(120.0);
            a.ClearQueue(false);
            rig.Snapshots[0] = SnapshotOf(a);
            rig.Plan(121.0);
            Assert.Null(rig.Planner.StalledQueueStatus);
            Assert.Contains(rig.Planner.Actions, x => x.Kind == CraftActionKind.RestoreAssembly && x.Assembler.Id == 7);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void AClearThatDidNotLandWaitsAnotherWindow()
        {
            var a = new Assembler(7) { IsDisassembling = true }.Enqueue(PlateBp, 10);
            var rig = new Rig().Snapshot(a);
            rig.Planner.TrackedDisassembler = 7;
            rig.Plan(0.0);
            rig.Plan(120.0);
            Assert.True(Clears(rig.Planner, 7));
            rig.Plan(121.0);                                      // the queue is unchanged: the clear never ran
            Assert.False(Clears(rig.Planner, 7));
            rig.Plan(240.0);
            Assert.False(Clears(rig.Planner, 7));
            rig.Plan(241.0);
            Assert.True(Clears(rig.Planner, 7));
        }

        [Fact]
        public void PlannerResetsTheStallWindowWhenTheTrackedQueueProgresses()
        {
            var a = new Assembler(7) { IsDisassembling = true }.Enqueue(PlateBp, 10);
            var rig = new Rig().Snapshot(a);
            rig.Planner.TrackedDisassembler = 7;
            rig.Plan(0.0);
            a.Queue[0] = new QueueEntry { Blueprint = PlateBp, Amount = 9 };
            rig.Snapshots[0] = SnapshotOf(a);
            rig.Plan(121.0);
            Assert.Null(rig.Planner.StalledQueueStatus);
            rig.Plan(240.0);
            Assert.Null(rig.Planner.StalledQueueStatus);
            rig.Plan(241.0);
            Assert.Equal("clearing stalled disassembly queue on A7", rig.Planner.StalledQueueStatus);
        }

        [Fact]
        public void PlannerNeverClearsAnUntrackedQueue()
        {
            var a = new Assembler(7).Enqueue(PlateBp, 10);
            a.IsDisassembling = true;                             // the user's own disassembly
            var rig = new Rig().Snapshot(a);
            rig.Plan(0.0);
            rig.Plan(500.0);
            Assert.False(Clears(rig.Planner, 7));
            Assert.Null(rig.Planner.StalledQueueStatus);
        }

        [Fact]
        public void PlannerDropsTheStallWhenTheTrackedAssemblerDisappears()
        {
            var a = new Assembler(7).Enqueue(PlateBp, 10);
            var rig = new Rig().Snapshot(a);
            rig.Planner.TrackedDisassembler = 7;
            rig.Plan(0.0);
            rig.Snapshots.Clear();
            rig.Plan(500.0);
            Assert.Null(rig.Planner.StalledQueueStatus);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void ACoopLeftInDisassemblyGoesBackOnceEmpty()
        {
            var coop = new Assembler(3) { Mark = AssemblerMark.Coop, IsReceiver = false, IsDisassembling = true };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Ledger.AddStock(Plate, 100);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(CraftActionKind.RestoreAssembly, action.Kind);
            Assert.Same(coop, action.Assembler);
            rig.Execute();
            Assert.False(coop.IsDisassembling);
        }

        [Fact]
        public void ACoopStillDisassemblingKeepsItsMode()
        {
            var coop = new Assembler(3) { Mark = AssemblerMark.Coop, IsReceiver = false, IsDisassembling = true }.Enqueue(PlateBp, 10);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Ledger.AddStock(Plate, 100);
            rig.Ledger.AddOutgoing(Plate, 10);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
        }

        [Fact]
        public void OnlyAHomeCoopThatCanCooperateIsSwitchedBack()
        {
            var docked = new Assembler(3) { Mark = AssemblerMark.Coop, IsReceiver = false, IsDisassembling = true, Construct = 1 };
            var untagged = new Assembler(4) { Mark = AssemblerMark.None, IsReceiver = false, IsDisassembling = true };
            var kit = new Assembler(5) { Mark = AssemblerMark.Coop, CanCooperate = false, IsReceiver = false, IsDisassembling = true };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(docked).Snapshot(untagged).Snapshot(kit);
            rig.Ledger.AddStock(Plate, 100);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
        }

        [Fact]
        public void WorkingCoopsAreSharedEvenlyByEveryMain()
        {
            var home = new Assembler(1);
            var docked = new Assembler(2) { Construct = 1, Speed = 3 };
            var c1 = new Assembler(3) { Mark = AssemblerMark.Coop, IsReceiver = false };
            var c2 = new Assembler(4) { Mark = AssemblerMark.Coop, IsReceiver = false };
            var rig = new Rig().Quota(Plate, "90A").Snapshot(home).Snapshot(docked).Snapshot(c1).Snapshot(c2);
            rig.Plan();
            Assert.Equal(2, rig.Planner.Actions.Count);
            Assert.Same(home, rig.Planner.Actions[0].Assembler);
            Assert.Equal(30, rig.Planner.Actions[0].Amount);    // weights 1+1 and 3+1
            Assert.Same(docked, rig.Planner.Actions[1].Assembler);
            Assert.Equal(60, rig.Planner.Actions[1].Amount);
        }

        [Fact]
        public void ACoopOnTheShipHelpsTheBaseMainToo()
        {
            var home = new Assembler(1);
            var docked = new Assembler(2) { Construct = 1, Speed = 3 };
            var shipHelper = new Assembler(3) { Mark = AssemblerMark.Coop, IsReceiver = false, Construct = 1, Speed = 4 };
            var rig = new Rig().Quota(Plate, "80A").Snapshot(home).Snapshot(docked).Snapshot(shipHelper);
            rig.Plan();
            Assert.Equal(30, rig.Planner.Actions[0].Amount);    // weights 1+2 and 3+2
            Assert.Equal(50, rig.Planner.Actions[1].Amount);
        }

        [Fact]
        public void CoopQueuesCountAsSharedWork()
        {
            var home = new Assembler(1);
            var docked = new Assembler(2) { Construct = 1 };
            var helper = new Assembler(3) { Mark = AssemblerMark.Coop, IsReceiver = false }.Enqueue(PlateBp, 30);
            var rig = new Rig().Quota(Plate, "90A").Snapshot(home).Snapshot(docked).Snapshot(helper);
            rig.Ledger.AddIncoming(Plate, 30);
            rig.Plan();
            Assert.Equal(30, rig.Planner.Actions[0].Amount);    // 60 deficit: level (60+30)/3 = 30, each 45-15
            Assert.Equal(30, rig.Planner.Actions[1].Amount);
        }

        [Fact]
        public void AnOffOrDisassemblingCoopAddsNoWeight()
        {
            var home = new Assembler(1);
            var docked = new Assembler(2) { Construct = 1, Speed = 3 };
            var off = new Assembler(3) { Mark = AssemblerMark.Coop, IsReceiver = false, IsOperational = false, Speed = 4 };
            var reversed = new Assembler(4) { Mark = AssemblerMark.Coop, IsReceiver = false, IsDisassembling = true, Speed = 4 }.Enqueue("Motor", 5);
            var rig = new Rig().Quota(Plate, "80A").Snapshot(home).Snapshot(docked).Snapshot(off).Snapshot(reversed);
            rig.Plan();
            Assert.Equal(20, rig.Planner.Actions[0].Amount);
            Assert.Equal(60, rig.Planner.Actions[1].Amount);
        }

        [Fact]
        public void OrdersGoOnlyToAnAssemblerThatTakesOrders()
        {
            var unmarked = new Assembler(1) { IsReceiver = false, Speed = 5 };
            var main = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(unmarked).Snapshot(main);
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Same(main, action.Assembler);
            Assert.Equal(100, action.Amount);
        }

        [Fact]
        public void NoAssemblerTakingOrdersReportsNoMainEvenForAnUnknownBlueprint()
        {
            var rig = new Rig().Quota(Plate, "10A").Quota("Component/ModAlloy", "10A")
                .Snapshot(new Assembler(1) { IsReceiver = false });
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(new[] { Plate, "Component/ModAlloy" }, rig.Planner.Unassigned[CraftPlanner.NoMain]);
            Assert.Empty(rig.Planner.NeedBlueprint);
        }

        [Fact]
        public void AReceiverThatCannotMakeTheItemReportsNoAssembler()
        {
            var rig = new Rig().Quota(Missile, "10A").Snapshot(new Assembler(1).Usable(PlateBp));
            rig.Plan();
            Assert.Equal(new[] { Missile }, rig.Planner.Unassigned[CraftPlanner.NoAssembler]);
        }

        [Fact]
        public void DisassemblyAcquiresOnlyAnAssemblerThatTakesOrders()
        {
            var unmarked = new Assembler(1) { IsReceiver = false, Speed = 5 };
            var main = new Assembler(2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(unmarked).Snapshot(main);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(CraftActionKind.SetDisassembly, rig.Planner.Actions[0].Kind);
            Assert.Same(main, rig.Planner.Actions[0].Assembler);
            Assert.Equal(2, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void DisassemblyWithNoAssemblerTakingOrdersReportsNoMain()
        {
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1) { IsReceiver = false });
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(new[] { Plate }, rig.Planner.Unassigned[CraftPlanner.Disassembly + CraftPlanner.NoMain]);
        }

        [Fact]
        public void ATrackedDisassemblerUntaggedIsDroppedWithoutARestore()
        {
            var mine = new Assembler(7) { IsDisassembling = true, IsReceiver = false, Mark = AssemblerMark.None };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 100);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void ATrackedDisassemblerOnAMisconfiguredConstructIsStillRestored()
        {
            var mine = new Assembler(7) { IsDisassembling = true, IsReceiver = false };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 100);
            rig.Plan();
            Assert.Equal(CraftActionKind.RestoreAssembly, Assert.Single(rig.Planner.Actions).Kind);
        }

        [Fact]
        public void ATrackedDisassemblerOnAMisconfiguredConstructGetsNoMoreWork()
        {
            var mine = new Assembler(7) { IsDisassembling = true, IsReceiver = false }.Enqueue(PlateBp, 100);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Ledger.AddStock(Plate, 500);
            rig.Ledger.AddOutgoing(Plate, 100);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(7, rig.Planner.TrackedDisassembler);
        }

        static Assembler Coop(long id, double speed = 1) =>
            new Assembler(id) { Mark = AssemblerMark.Coop, IsReceiver = false, Cooperative = true, Speed = speed };

        [Fact]
        public void SurplusDrainsTheSlowestCoopWhileTheMainKeepsAssembling()
        {
            var main = new Assembler(1);
            var fast = Coop(3, 5).Enqueue(PlateBp, 2);
            var slow = Coop(4).Enqueue(PlateBp, 2);
            var rig = new Rig().Quota(Plate, "100A").Quota(Missile, "10A").Snapshot(main).Snapshot(fast).Snapshot(slow);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(2, rig.Planner.Actions.Count);
            Assert.Contains(rig.Planner.Actions, x => x.Kind == CraftActionKind.AppendAssembly && x.Assembler == main);
            Assert.Contains(rig.Planner.Actions, x => x.Kind == CraftActionKind.StopCooperating && x.Assembler == slow);
            Assert.Equal(4, rig.Planner.TrackedDisassembler);
            rig.Execute();
            Assert.False(slow.Cooperative);
            Assert.False(slow.IsDisassembling);
            Assert.True(fast.Cooperative);
        }

        [Fact]
        public void AnEmptyCoopIsStillDrainedFirst()
        {
            var coop = Coop(3);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(CraftActionKind.StopCooperating, Assert.Single(rig.Planner.Actions).Kind);
        }

        [Fact]
        public void ADrainingCoopHoldsTheSurplusAndIsNeverCleared()
        {
            var coop = Coop(3).Enqueue(PlateBp, 2);
            coop.Cooperative = false;
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Planner.TrackedDisassembler = 3;
            rig.Ledger.AddStock(Plate, 500);
            rig.Ledger.AddIncoming(Plate, 2);
            rig.Plan(0.0);
            rig.Plan(500.0);
            Assert.Empty(rig.Planner.Actions);
            Assert.Null(rig.Planner.StalledQueueStatus);
            Assert.Equal(3, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void ADrainedCoopSwitchesToDisassemblyAndTakesTheSurplus()
        {
            var coop = Coop(3);
            coop.Cooperative = false;
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Planner.TrackedDisassembler = 3;
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(2, rig.Planner.Actions.Count);
            Assert.Equal(CraftActionKind.SetDisassembly, rig.Planner.Actions[0].Kind);
            Assert.Equal(CraftActionKind.AppendDisassembly, rig.Planner.Actions[1].Kind);
            Assert.Equal(400, rig.Planner.Actions[1].Amount);
            Assert.Equal(3, rig.Planner.TrackedDisassembler);
            rig.Execute();
            Assert.True(coop.IsDisassembling);
        }

        [Fact]
        public void ACoopWhoseSwitchCameBackOnIsSwitchedOffAgain()
        {
            var coop = Coop(3).Enqueue(PlateBp, 2);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Planner.TrackedDisassembler = 3;
            rig.Ledger.AddStock(Plate, 500);
            rig.Ledger.AddIncoming(Plate, 2);
            rig.Plan();
            Assert.Equal(CraftActionKind.StopCooperating, Assert.Single(rig.Planner.Actions).Kind);
        }

        [Fact]
        public void ADrainedCoopNoSurplusWantsGoesBackToHelping()
        {
            var coop = Coop(3);
            coop.Cooperative = false;
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Planner.TrackedDisassembler = 3;
            rig.Ledger.AddStock(Plate, 100);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void ADrainingCoopSwitchedOffIsReleased()
        {
            var coop = Coop(3).Enqueue(PlateBp, 2);
            coop.Cooperative = false;
            coop.IsOperational = false;
            var rig = new Rig().Quota(Plate, "100A").Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Planner.TrackedDisassembler = 3;
            rig.Ledger.AddStock(Plate, 500);
            rig.Ledger.AddIncoming(Plate, 2);
            rig.Plan();
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void AssemblyGoesOnWhileACoopDisassembles()
        {
            var main = new Assembler(1);
            var coop = Coop(3).Enqueue(PlateBp, 50);
            coop.Cooperative = false;
            coop.IsDisassembling = true;
            var rig = new Rig().Quota(Missile, "10A").Snapshot(main).Snapshot(coop);
            rig.Planner.TrackedDisassembler = 3;
            rig.Plan();
            var action = Assert.Single(rig.Planner.Actions);
            Assert.Equal(CraftActionKind.AppendAssembly, action.Kind);
            Assert.Same(main, action.Assembler);
            Assert.Empty(rig.Planner.Unassigned[CraftPlanner.MainBusy]);
        }

        [Fact]
        public void TheCoopPickedToDisassembleLeavesTheSharedPool()
        {
            var home = new Assembler(1);
            var docked = new Assembler(2) { Construct = 1, Speed = 3 };
            var draining = Coop(3, 4).Enqueue(PlateBp, 2);
            draining.Cooperative = false;
            var helper = Coop(4);
            var rig = new Rig().Quota(Missile, "50A").Snapshot(home).Snapshot(docked).Snapshot(draining).Snapshot(helper);
            rig.Planner.TrackedDisassembler = 3;
            rig.Plan();
            Assert.Equal(15, rig.Planner.Actions[0].Amount);    // weights 1+0.5 and 3+0.5
            Assert.Equal(35, rig.Planner.Actions[1].Amount);
        }

        [Fact]
        public void TheMainDisassemblesOnlyWithoutACompatibleCoop()
        {
            var main = new Assembler(1);
            var unable = Coop(3).Usable("Nothing");
            var docked = Coop(4);
            docked.Construct = 1;
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main).Snapshot(unable).Snapshot(docked);
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Equal(CraftActionKind.SetDisassembly, rig.Planner.Actions[0].Kind);
            Assert.Same(main, rig.Planner.Actions[0].Assembler);
            Assert.Equal(1, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void NoCoopIsPickedOnAHomeWithoutAValidMain()
        {
            var main = new Assembler(1) { IsReceiver = false };
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main).Snapshot(Coop(3));
            rig.Ledger.AddStock(Plate, 500);
            rig.Plan();
            Assert.Empty(rig.Planner.Actions);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void AFinishedCoopDisassemblerIsRestored()
        {
            var coop = Coop(3);
            coop.Cooperative = false;
            coop.IsDisassembling = true;
            var rig = new Rig().Snapshot(new Assembler(1)).Snapshot(coop);
            rig.Planner.TrackedDisassembler = 3;
            rig.Plan();
            Assert.Equal(CraftActionKind.RestoreAssembly, Assert.Single(rig.Planner.Actions).Kind);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        static CraftAction TrimOf(CraftPlanner planner)
        {
            foreach (var a in planner.Actions) if (a.Kind == CraftActionKind.Trim) return a;
            return default(CraftAction);
        }

        [Fact]
        public void ACoveredShortfallTakesBackSHEPHERDsUnitsOnTheSecondPlan()
        {
            var main = new Assembler(1).Enqueue(PlateBp, 20);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(10).Plan();
            Assert.Equal(70, Assert.Single(rig.Planner.Actions).Amount);
            rig.Execute();
            rig.Recount(100).Plan();
            Assert.Null(TrimOf(rig.Planner).Assembler);
            rig.Recount(100).Plan();
            var trim = TrimOf(rig.Planner);
            Assert.Same(main, trim.Assembler);
            Assert.Equal(70, trim.Amount);
            rig.Execute();
            Assert.Equal(new[] { new QueueEntry { Blueprint = PlateBp, Amount = 20 } }, main.Queue);
            rig.Recount(100).Plan();
            Assert.Empty(rig.Planner.Actions);
        }

        [Fact]
        public void ThePlayersOwnUnitsAreNeverTrimmed()
        {
            var main = new Assembler(1).Enqueue(PlateBp, 20).Enqueue(PlateBp, 50);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(100).Plan();
            rig.Recount(100).Plan();
            Assert.Empty(rig.Planner.Actions);
        }

        [Fact]
        public void TheUnitInProductionStays()
        {
            var main = new Assembler(1);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(30).Plan();
            rig.Execute();
            rig.Recount(100).Plan();
            rig.Recount(100).Plan();
            Assert.Equal(69, TrimOf(rig.Planner).Amount);
            rig.Execute();
            Assert.Equal(new[] { new QueueEntry { Blueprint = PlateBp, Amount = 1 } }, main.Queue);
        }

        [Fact]
        public void AOneCycleExcessIsLeftAlone()
        {
            var main = new Assembler(1).Enqueue(PlateBp, 20);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(10).Plan();
            rig.Execute();
            rig.Recount(100).Plan();
            rig.Recount(10).Plan();
            rig.Recount(100).Plan();
            Assert.Null(TrimOf(rig.Planner).Assembler);
        }

        [Fact]
        public void AClaimShrinksAsItsUnitsAreBuilt()
        {
            var main = new Assembler(1).Enqueue(PlateBp, 20);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(10).Plan();
            rig.Execute();
            main.Queue.Clear();
            main.Enqueue(PlateBp, 30);                       // the player's 20 and 40 of SHEPHERD's 70 are built
            rig.Recount(100).Plan();
            rig.Recount(100).Plan();
            Assert.Equal(29, TrimOf(rig.Planner).Amount);    // the claim is capped by what is still queued
        }

        [Fact]
        public void BuildingUsesUpSHEPHERDsUnitsBeforeThePlayersQueuedBehindThem()
        {
            var main = new Assembler(1);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(30).Plan();
            rig.Execute();
            main.Enqueue(PlateBp, 500);                      // the player's order, queued after SHEPHERD's 70
            rig.Recount(30).Plan();
            main.Queue[0] = new QueueEntry { Blueprint = PlateBp, Amount = 10 };     // 60 of SHEPHERD's built
            rig.Recount(100).Plan();
            Assert.Equal(10, TrimOf(rig.Planner).Amount);
            rig.Execute();
            Assert.Equal(500, main.Queue[0].Amount + main.Queue[1].Amount);
        }

        [Fact]
        public void BuildingUsesUpThePlayersUnitsQueuedAheadFirst()
        {
            var main = new Assembler(1).Enqueue(PlateBp, 50);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(0).Plan();
            rig.Execute();                                   // SHEPHERD's 50, behind the player's 50
            main.Queue[0] = new QueueEntry { Blueprint = PlateBp, Amount = 10 };     // 40 of the player's built
            rig.Recount(100).Plan();
            rig.Recount(100).Plan();
            Assert.Equal(50, TrimOf(rig.Planner).Amount);
        }

        [Fact]
        public void AnOffAssemblersQueueIsNotTrimmed()
        {
            var main = new Assembler(1).Enqueue(PlateBp, 20);
            var rig = new Rig().Quota(Plate, "100A").Snapshot(main);
            rig.Recount(10).Plan();
            rig.Execute();
            main.IsOperational = false;
            rig.Recount(200).Plan();
            rig.Recount(200).Plan();
            Assert.Null(TrimOf(rig.Planner).Assembler);
        }

        [Fact]
        public void AShrunkSurplusTrimsTheTrackedDisassembler()
        {
            var mine = new Assembler(7) { IsDisassembling = true }.Enqueue(PlateBp, 100);
            var rig = new Rig().Quota(Plate, "100LA").Snapshot(new Assembler(1)).Snapshot(mine);
            rig.Planner.TrackedDisassembler = 7;
            rig.Recount(150).Plan();
            Assert.Null(TrimOf(rig.Planner).Assembler);
            rig.Recount(150).Plan();
            var trim = TrimOf(rig.Planner);
            Assert.Same(mine, trim.Assembler);
            Assert.Equal(50, trim.Amount);
            rig.Execute();
            Assert.Equal(new[] { new QueueEntry { Blueprint = PlateBp, Amount = 50 } }, mine.Queue);
        }

        [Fact]
        public void AUsersDisassemblerIsNeverTrimmed()
        {
            var users = new Assembler(7) { IsDisassembling = true }.Enqueue(PlateBp, 100);
            var rig = new Rig().Quota(Plate, "100LA").Snapshot(new Assembler(1)).Snapshot(users);
            rig.Recount(150).Plan();
            rig.Recount(150).Plan();
            Assert.Null(TrimOf(rig.Planner).Assembler);
        }

        [Fact]
        public void ADisassemblyIsTrimmedEndToEnd()
        {
            var main = new Assembler(1);
            var off = new Assembler(2) { Mark = AssemblerMark.Coop, IsReceiver = false, Cooperative = true, IsOperational = false };
            var rig = new Rig().Quota(Plate, "2800LA").Snapshot(main).Snapshot(off);
            rig.Recount(6628).Plan();
            rig.Execute();
            Assert.True(main.IsDisassembling);
            rig.Recount(3459).Plan();
            rig.Recount(3459).Plan();
            var trim = TrimOf(rig.Planner);
            Assert.Same(main, trim.Assembler);
            Assert.Equal(3169, trim.Amount);
        }

        [Fact]
        public void ADockedAssemblerNeverDisassembles()
        {
            var main = new Assembler(1).Enqueue("GirderComponent", 425);
            var ship = new Assembler(3) { Construct = 1, Speed = 5 };
            var rig = new Rig().Quota(Plate, "2800LA").Snapshot(main).Snapshot(ship);
            rig.Recount(6628).Plan();
            Assert.DoesNotContain(rig.Planner.Actions, x => x.Assembler == ship);
            Assert.Equal(0, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void ADockedTrackedDisassemblerGetsNoMoreWork()
        {
            var ship = new Assembler(3) { Construct = 1, IsDisassembling = true }.Enqueue(PlateBp, 100);
            var rig = new Rig().Quota(Plate, "2800LA").Snapshot(new Assembler(1)).Snapshot(ship);
            rig.Planner.TrackedDisassembler = 3;
            rig.Recount(6628).Plan();
            Assert.DoesNotContain(rig.Planner.Actions, x => x.Kind == CraftActionKind.AppendDisassembly);
            Assert.Equal(3, rig.Planner.TrackedDisassembler);
        }

        [Fact]
        public void TheTrackedDisassemblersQueueIsTrimmedAfterARecompile()
        {
            var ship = new Assembler(3) { Construct = 1, IsDisassembling = true }.Enqueue(PlateBp, 3828);
            var rig = new Rig().Quota(Plate, "2800LA").Snapshot(new Assembler(1)).Snapshot(ship);
            rig.Planner.TrackedDisassembler = 3;                 // re-seeded from [Production]; no claims survive
            rig.Recount(3459).Plan();
            rig.Recount(3459).Plan();
            Assert.Equal(3169, TrimOf(rig.Planner).Amount);
            rig.Execute();
            Assert.Equal(new[] { new QueueEntry { Blueprint = PlateBp, Amount = 659 } }, ship.Queue);
        }
    }
}
