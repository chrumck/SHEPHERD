using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class BlueprintDiscoveryTests
    {
        sealed class Assembler : IAssemblerState
        {
            readonly HashSet<string> usable = new HashSet<string>();
            public readonly List<QueueEntry> Queue = new List<QueueEntry>();
            public readonly Dictionary<string, double> Output = new Dictionary<string, double>();

            public Assembler(long id, params string[] blueprints)
            {
                Id = id;
                foreach (var blueprint in blueprints) usable.Add(blueprint);
            }

            public long Id { get; private set; }
            public string Name => "A" + Id;
            public int Construct { get; set; }
            public bool IsReceiver { get; set; } = true;
            public AssemblerMark Mark { get; set; } = AssemblerMark.Main;
            public bool CanCooperate => true;
            public bool Cooperative { get; set; }
            public void SetCooperative(bool on, bool dry) { if (!dry) Cooperative = on; }
            public string Model => "LargeAssembler";
            public double Speed => 1;
            public bool IsWorking { get; set; } = true;
            public bool IsOperational => true;
            public bool IsDisassembling { get; set; }
            public bool CanUse(string blueprint) => usable.Contains(blueprint);

            public void QueueInto(List<QueueEntry> into)
            {
                into.Clear();
                into.AddRange(Queue);
            }

            public void OutputInto(Dictionary<string, double> into)
            {
                into.Clear();
                foreach (var pair in Output) into[pair.Key] = pair.Value;
            }

            public void Append(string blueprint, double amount, bool dry) { if (!dry) Queue.Add(new QueueEntry { Blueprint = blueprint, Amount = amount }); }
            public double Trim(string blueprint, double amount, bool dry) => 0;
            public void SetDisassembling(bool on, bool dry) { if (!dry) IsDisassembling = on; }
            public void ClearQueue(bool dry) { }
        }

        static AssemblerSnapshot Snapshot(Assembler assembler) =>
            new AssemblerSnapshot { State = assembler, QueueEmpty = assembler.Queue.Count == 0, QueueAmount = QueueAmount(assembler), Speed = 1 };

        static double QueueAmount(Assembler assembler)
        {
            var amount = 0.0;
            foreach (var entry in assembler.Queue) amount += entry.Amount;
            return amount;
        }

        [Fact]
        public void ProbeWaitsForProtectedRebuildThenQueuesOneUnit()
        {
            var assembler = new Assembler(7, "ModTool");
            var snapshots = new List<AssemblerSnapshot> { Snapshot(assembler) };
            var actions = new List<CraftAction>();
            var probe = new BlueprintProbe();
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            Assert.Equal(BlueprintPhase.Protecting, probe.Phase);
            Assert.Equal(7, probe.AssemblerId);
            Assert.True(probe.OwnsOutput(7));
            Assert.Empty(actions);
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            var action = Assert.Single(actions);
            Assert.Equal(CraftActionKind.AppendAssembly, action.Kind);
            Assert.Equal("ModTool", action.Blueprint);
            Assert.Equal(1, action.Amount);
            Assert.Equal(BlueprintPhase.Queued, probe.Phase);
            probe.ProbeExecuted();
            Assert.Equal(BlueprintPhase.Observing, probe.Phase);
        }

        [Fact]
        public void PlannedCraftActionExcludesAnAssembler()
        {
            var busy = new Assembler(1, "ModTool");
            var free = new Assembler(2, "ModTool");
            var actions = new List<CraftAction>
            {
                new CraftAction { Kind = CraftActionKind.AppendAssembly, Assembler = busy, Blueprint = "Known", Amount = 1, Item = "Component/Known" }
            };
            var probe = new BlueprintProbe();
            probe.Plan("PhysicalGunObject/ModToolItem",
                new List<AssemblerSnapshot> { Snapshot(busy), Snapshot(free) }, actions);
            Assert.Equal(2, probe.AssemblerId);
        }

        [Fact]
        public void FastCompletedProbeLearnsFromHeldOutput()
        {
            var assembler = new Assembler(1, "ModTool");
            var snapshots = new List<AssemblerSnapshot> { Snapshot(assembler) };
            var actions = new List<CraftAction>();
            var probe = new BlueprintProbe();
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            probe.ProbeExecuted();
            assembler.Output["PhysicalGunObject/ModToolItem"] = 1;
            Assert.Equal(BlueprintObservation.Learned,
                probe.Observe(new List<IAssemblerState> { assembler }, new ProductionFacts()));
            Assert.Equal("ModTool", probe.Blueprint);
        }

        [Fact]
        public void AmbiguousProbeOutputFallsBackWithoutLearning()
        {
            var assembler = new Assembler(1, "ModTool");
            var snapshots = new List<AssemblerSnapshot> { Snapshot(assembler) };
            var actions = new List<CraftAction>();
            var probe = new BlueprintProbe();
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            probe.ProbeExecuted();
            assembler.Output["PhysicalGunObject/ModToolItem"] = 1;
            assembler.Output["Component/Interference"] = 1;
            Assert.Equal(BlueprintObservation.Manual,
                probe.Observe(new List<IAssemblerState> { assembler }, new ProductionFacts()));
        }

        [Fact]
        public void ManualTeachingRecordsOnlyRequestedOutput()
        {
            var assembler = new Assembler(1);
            assembler.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 1 });
            var probe = new BlueprintProbe();
            probe.Plan("Component/Requested", new List<AssemblerSnapshot>(), new List<CraftAction>());
            Assert.Equal(BlueprintPhase.Manual, probe.Phase);
            var states = new List<IAssemblerState> { assembler };
            Assert.Equal(BlueprintObservation.None, probe.Observe(states, new ProductionFacts()));
            assembler.Output["Component/Requested"] = 1;
            Assert.Equal(BlueprintObservation.Learned, probe.Observe(states, new ProductionFacts()));
            Assert.Equal("UnknownBlueprint", probe.Blueprint);
        }

        [Fact]
        public void ManualTeachingRejectsAnUnrelatedOutput()
        {
            var assembler = new Assembler(1);
            assembler.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 1 });
            var probe = new BlueprintProbe();
            probe.Plan("Component/Requested", new List<AssemblerSnapshot>(), new List<CraftAction>());
            var states = new List<IAssemblerState> { assembler };
            probe.Observe(states, new ProductionFacts());
            assembler.Queue.Clear();
            assembler.Output["Component/Other"] = 1;
            Assert.Equal(BlueprintObservation.None, probe.Observe(states, new ProductionFacts()));
            Assert.Null(probe.Blueprint);
        }

        static BlueprintProbe ManualProbe()
        {
            var probe = new BlueprintProbe();
            probe.Plan("Component/Requested", new List<AssemblerSnapshot>(), new List<CraftAction>());
            return probe;
        }

        [Fact]
        public void ManualTeachingLearnsAUnitMadeAfterTheOutputWasEmptied()
        {
            var assembler = new Assembler(1);
            assembler.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 10 });
            assembler.Output["Component/Requested"] = 1;
            var probe = ManualProbe();
            var states = new List<IAssemblerState> { assembler };
            var facts = new ProductionFacts();
            probe.Observe(states, facts);
            assembler.Output.Clear();
            Assert.Equal(BlueprintObservation.None, probe.Observe(states, facts));
            assembler.Output["Component/Requested"] = 1;
            Assert.Equal(BlueprintObservation.Learned, probe.Observe(states, facts));
            Assert.Equal("UnknownBlueprint", probe.Blueprint);
        }

        [Fact]
        public void ManualTeachingWatchesOnlyTheHomeMain()
        {
            var kit = new Assembler(1) { Mark = AssemblerMark.None, IsReceiver = false };
            kit.Queue.Add(new QueueEntry { Blueprint = "Stuck", Amount = 1 });
            var docked = new Assembler(2) { Construct = 1 };
            docked.Queue.Add(new QueueEntry { Blueprint = "Docked", Amount = 1 });
            var main = new Assembler(3);
            var probe = ManualProbe();
            var states = new List<IAssemblerState> { kit, docked, main };
            var facts = new ProductionFacts();
            probe.Observe(states, facts);
            Assert.Equal(0, probe.ReservedAssemblerId);
            Assert.Equal("learn Component/Requested: batch some on A3", probe.Status);
            main.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 10 });
            probe.Observe(states, facts);
            Assert.Equal(3, probe.ReservedAssemblerId);
            Assert.Equal("learn Component/Requested: watching UnknownBlueprint on A3", probe.Status);
        }

        [Fact]
        public void ManualTeachingWithoutAHomeMainSaysSo()
        {
            var kit = new Assembler(1) { Mark = AssemblerMark.None, IsReceiver = false };
            kit.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 10 });
            var probe = ManualProbe();
            probe.Observe(new List<IAssemblerState> { kit, new Assembler(2) { Construct = 1 } }, new ProductionFacts());
            Assert.Equal(0, probe.ReservedAssemblerId);
            Assert.Equal("learn Component/Requested: no Main assembler", probe.Status);
        }

        [Fact]
        public void ManualTeachingLetsGoOfAMainThatStopsWorking()
        {
            var main = new Assembler(1);
            main.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 10 });
            var probe = ManualProbe();
            var states = new List<IAssemblerState> { main };
            var facts = new ProductionFacts();
            probe.Observe(states, facts);
            Assert.Equal(1, probe.ReservedAssemblerId);
            main.IsWorking = false;
            probe.Observe(states, facts);
            Assert.Equal(0, probe.ReservedAssemblerId);
            probe.Observe(states, facts);
            Assert.Equal(0, probe.ReservedAssemblerId);
        }

        [Fact]
        public void ManualTeachingIgnoresKnownWorkQueuedBesideTheItem()
        {
            var main = new Assembler(1);
            main.Queue.Add(new QueueEntry { Blueprint = "SteelPlate", Amount = 5 });
            main.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 10 });
            var facts = new ProductionFacts();
            facts.Learn("Component/SteelPlate", "SteelPlate");
            var probe = ManualProbe();
            var states = new List<IAssemblerState> { main };
            probe.Observe(states, facts);
            Assert.Equal(1, probe.ReservedAssemblerId);
            main.Output["Component/SteelPlate"] = 5;
            Assert.Equal(BlueprintObservation.None, probe.Observe(states, facts));
            Assert.Equal(1, probe.ReservedAssemblerId);
            main.Output["Component/Requested"] = 1;
            Assert.Equal(BlueprintObservation.Learned, probe.Observe(states, facts));
            Assert.Equal("UnknownBlueprint", probe.Blueprint);
        }

        [Fact]
        public void ManualTeachingWaitsWhileTwoUnknownBlueprintsAreQueued()
        {
            var main = new Assembler(1);
            main.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 10 });
            main.Queue.Add(new QueueEntry { Blueprint = "OtherUnknown", Amount = 10 });
            var probe = ManualProbe();
            probe.Observe(new List<IAssemblerState> { main }, new ProductionFacts());
            Assert.Equal(0, probe.ReservedAssemblerId);
        }

        [Fact]
        public void ALeftoverUnknownEntryIsNotedOnceItShowsWhatItMakes()
        {
            var main = new Assembler(1);
            main.Queue.Add(new QueueEntry { Blueprint = "Leftover", Amount = 5 });
            var facts = new ProductionFacts();
            var probe = ManualProbe();
            var states = new List<IAssemblerState> { main };
            probe.Observe(states, facts);
            Assert.Equal("learn Component/Requested: watching Leftover on A1", probe.Status);
            main.Output["Component/Other"] = 1;
            Assert.Equal(BlueprintObservation.None, probe.Observe(states, facts));
            Assert.Equal(0, probe.ReservedAssemblerId);
            Assert.True(facts.IsBlueprintKnown("Leftover"));
            main.Queue.Add(new QueueEntry { Blueprint = "UnknownBlueprint", Amount = 10 });
            probe.Observe(states, facts);
            Assert.Equal("learn Component/Requested: watching UnknownBlueprint on A1", probe.Status);
            main.Output["Component/Other"] = 2;
            main.Output["Component/Requested"] = 1;
            Assert.Equal(BlueprintObservation.Learned, probe.Observe(states, facts));
            Assert.Equal("UnknownBlueprint", probe.Blueprint);
        }

        [Fact]
        public void ProbeUsesOnlyAnAssemblerThatTakesOrders()
        {
            var unmarked = new Assembler(1, "ModTool") { IsReceiver = false };
            var main = new Assembler(2, "ModTool");
            var probe = new BlueprintProbe();
            probe.Plan("PhysicalGunObject/ModToolItem",
                new List<AssemblerSnapshot> { Snapshot(unmarked), Snapshot(main) }, new List<CraftAction>());
            Assert.Equal(2, probe.AssemblerId);
        }

        [Fact]
        public void ProbeWithNoAssemblerTakingOrdersFallsBackToManualTeaching()
        {
            var probe = new BlueprintProbe();
            probe.Plan("PhysicalGunObject/ModToolItem",
                new List<AssemblerSnapshot> { Snapshot(new Assembler(1, "ModTool") { IsReceiver = false }) }, new List<CraftAction>());
            Assert.Equal(BlueprintPhase.Manual, probe.Phase);
        }

        [Fact]
        public void ProbeLetsGoOfAnAssemblerThatStopsTakingOrders()
        {
            var main = new Assembler(7, "ModTool");
            var snapshots = new List<AssemblerSnapshot> { Snapshot(main) };
            var actions = new List<CraftAction>();
            var probe = new BlueprintProbe();
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            main.IsReceiver = false;
            probe.Plan("PhysicalGunObject/ModToolItem", snapshots, actions);
            Assert.Equal(0, probe.AssemblerId);
            Assert.Empty(actions);
        }
    }
}
