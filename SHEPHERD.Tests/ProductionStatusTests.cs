using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    // The production section's composition: one string per state, shared by the status panel and the transition log.
    public class ProductionStatusTests
    {
        const string Item = "Component/ModAlloy";
        const string Ore = "Ore/Modium";

        readonly List<string> logged = new List<string>();
        readonly ProductionStatus status;
        readonly CraftPlanner craft = new CraftPlanner();
        readonly RefinePlanner refine = new RefinePlanner();
        readonly DiscoveryCoordinator discovery = new DiscoveryCoordinator();
        readonly BlueprintProbe probe = new BlueprintProbe();
        readonly YieldSample sample = new YieldSample();
        readonly MarkCheck marks = new MarkCheck();

        public ProductionStatusTests() { status = new ProductionStatus(logged.Add); }

        void Compose() => status.Compose(craft, refine, discovery, probe, sample, marks);

        sealed class Feed : IRefineryFeed
        {
            public long Id => 7;
            public string Name => "R1";
            public bool IsConstrained { get; private set; }
            public bool CanTake(string oreKey) => true;
            public void Constrain(FeedRequest request) { IsConstrained = true; }
        }

        [Fact]
        public void AStateIsOneStringOnBothSurfaces()
        {
            craft.Unassigned[CraftPlanner.NoAssembler].Add(Item);
            Compose();
            Assert.Equal(new[] { "craft " + Item + ": no assembler" }, status.States);
            Assert.Equal(status.States, logged);
        }

        [Fact]
        public void AContinuouslyAssertedStateLogsOnceButStaysOnStatus()
        {
            craft.Unassigned[CraftPlanner.NoAssembler].Add(Item);
            Compose();
            Compose();
            Compose();
            Assert.Single(status.States);                       // still shown — the panel is a live snapshot
            Assert.Single(logged);                             // logged once — the log records the entry edge
        }

        [Fact]
        public void ResolutionClearsStatusAndRearmsTheLog()
        {
            craft.Unassigned[CraftPlanner.NoAssembler].Add(Item);
            Compose();
            craft.Unassigned[CraftPlanner.NoAssembler].Clear();
            Compose();
            Assert.Empty(status.States);
            craft.Unassigned[CraftPlanner.NoAssembler].Add(Item);
            Compose();
            Assert.Equal(2, logged.Count);                     // the returning state logs again
        }

        [Fact]
        public void ALearningRequestReplacesTheGenericMissingBlueprintLine()
        {
            craft.NeedBlueprint.Add(Item);
            discovery.Reconcile(craft.NeedBlueprint, refine.NeedYield);
            probe.Plan(Item, new List<AssemblerSnapshot>(), new List<CraftAction>());
            Compose();
            Assert.Equal(new[] { "learn " + Item + ": queue some alone on an assembler" }, status.States);
        }

        [Fact]
        public void AnUnhandledMissingBlueprintStillShowsTheGenericLine()
        {
            craft.NeedBlueprint.Add(Item);
            Compose();
            Assert.Equal(new[] { "craft " + Item + ": no blueprint" }, status.States);
        }

        [Fact]
        public void AnActiveSampleReplacesItsOresYieldUnknownLine()
        {
            refine.NeedYield.Add(Ore);
            sample.Begin(Ore, "Ingot/Modium", new List<IRefineryFeed> { new Feed() });
            Compose();
            Assert.Equal(new[] { "learn " + Ore + ": draining" }, status.States);
        }

        [Fact]
        public void AnUnsampledUnknownYieldShowsItsOwnLine()
        {
            refine.NeedYield.Add(Ore);
            Compose();
            Assert.Equal(new[] { "refine " + Ore + ": yield unknown" }, status.States);
        }

        [Fact]
        public void ActionLinesShowOnStatusAndNeverReachTheLog()
        {
            // Current work changes amount every plan, so logging it would turn the history into a per-cycle stream.
            craft.Actions.Add(new CraftAction { Kind = CraftActionKind.AppendAssembly, Blueprint = "bp", Amount = 70.0, Item = Item });
            craft.Actions.Add(new CraftAction { Kind = CraftActionKind.AppendDisassembly, Blueprint = "bp", Amount = 5.0, Item = Item });
            refine.Requests.Add(new FeedRequest(Ore, "Ingot/Modium", 100.0));
            Compose();
            Assert.Equal(new[]
            {
                "crafting " + Item + " +70",
                "disassembling " + Item + " 5",
                "refining " + Ore + " for Ingot/Modium"
            }, status.Actions);
            Assert.Empty(status.States);
            Assert.Empty(logged);
        }

        [Fact]
        public void EveryBlockedRefineLineReachesBothSurfaces()
        {
            refine.Blocked.Add(RefinePlanner.NoRefineryLine(Ore));
            refine.Blocked.Add(RefinePlanner.NeedOreLine(110.0, Ore));
            Compose();
            Assert.Equal(new[] { "refine " + Ore + ": no refinery", "learn " + Ore + ": need 110 available" },
                status.States);
            Assert.Equal(status.States, logged);
        }

        [Fact]
        public void HeldDirectionsNameTheOperationTheyHold()
        {
            craft.HeldAssembly.Add(Item);
            craft.HeldDisassembly.Add(Ore);
            Compose();
            Assert.Contains("craft " + Item + ": disassembly queued", status.States);
            Assert.Contains("disassemble " + Ore + ": assembly queued", status.States);
        }

        sealed class Marked : IMarkedAssembler
        {
            public Marked(string name, AssemblerMark mark) { Name = name; Mark = mark; }
            public long Id => 0;
            public bool IsOperational => true;
            public bool IsReceiver { get; set; }
            public string Name { get; private set; }
            public int Construct => 0;
            public AssemblerMark Mark { get; private set; }
            public bool CanCooperate => true;
            public bool Cooperative => false;
            public void SetCooperative(bool on, bool dry) { }
        }

        [Fact]
        public void EachUnassignedReasonHasItsLine()
        {
            craft.Unassigned[CraftPlanner.NoMain].Add("A");
            craft.Unassigned[CraftPlanner.MainOff].Add("B");
            craft.Unassigned[CraftPlanner.MainBusy].Add("C");
            craft.Unassigned[CraftPlanner.NoAssembler].Add("D");
            Compose();
            Assert.Equal(new[] { "craft A: no Main assembler", "craft B: Main assembler off",
                "craft C: Main assembler disassembling", "craft D: no assembler" }, status.States);
        }

        [Fact]
        public void AWaitingSurplusReadsAsDisassembly()
        {
            craft.Unassigned[CraftPlanner.Disassembly + CraftPlanner.MainOff].Add("B");
            craft.Unassigned[CraftPlanner.Disassembly + CraftPlanner.NoAssembler].Add("D");
            Compose();
            Assert.Equal(new[] { "disassemble B: Main assembler off", "disassemble D: no free assembler" }, status.States);
        }

        [Fact]
        public void AMarkProblemIsAStateAndReplacesNoMain()
        {
            marks.Check(new List<Marked> { new Marked("C1", AssemblerMark.Coop) });
            craft.Unassigned[CraftPlanner.NoMain].Add("A");
            craft.Unassigned[CraftPlanner.MainOff].Add("B");
            Compose();
            Assert.Equal(new[] { "assemblers: no Main among Coop C1", "craft B: Main assembler off" }, status.States);
            Assert.Equal(status.States, logged);
        }
    }
}
