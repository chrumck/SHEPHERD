using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // How the sort step treats a managed block. A role is derived purely from the block's ingame interface — no
    // tag, no config — so a modded machine that subtypes a vanilla one inherits the right behavior with zero
    // setup, while an untyped modded machine falls back to Storage ([SHEPHERD-Off] is the escape hatch).
    public enum Role
    {
        Storage,    // cargo/cockpit/connector/sorter/generic: drained into destinations
        Collector,  // drill/grinder/welder/collector: pure source, drained to storage (its contents are yield/loot)
        Consumer,   // reactor / O2-H2 gen / irrigation system: filled with fuel, not drained (foreign items evicted)
        Processor   // refinery / assembler: input fed & not scattered, output harvested to storage
    }

    // A managed block paired with its role and inventories. Input is GetInventory(0) — the block's sort/fuel/feed
    // inventory; Output is GetInventory(1) for a Processor (the harvested output side), null for every other role.
    // ReservePool names the pool of a [SHEPHERD-RESERVE...] container ("" = the default pool); null = not a reserve —
    // so the caller can resolve the container's pool model and assign it a keep-quota drain mode. Construct is the
    // managed-scope index; Docked = on a grid docked to the PB's own construct, whose settings must leave with it
    // unchanged. Mark is the block's assembler tag.
    // A struct, not a class: an immutable data holder stored in a List<ManagedBlock> the always-rescan rebuilds every
    // cycle, so value semantics keep that rebuild from heap-allocating one object per managed block.
    public struct ManagedBlock
    {
        public IMyTerminalBlock Block;
        public Role Role;
        public IMyInventory Input;
        public IMyInventory Output;
        public string ReservePool;
        public int Construct;
        public bool CatchAll;
        public AssemblerMark Mark;

        public bool IsReserve => ReservePool != null;
        public bool Docked => Construct > 0;
    }

    // Which machines the sort keeps fed, and with what. Finer than Role: both a reactor and an O2/H2 generator
    // are Consumers but want different feed categories, and among Processors only a refinery is fed here (an
    // assembler is fed elsewhere). None = not machine-fed.
    public enum FeedKind { None, Reactor, GasGen, Refinery }

    // How much of a managed inventory the sort may drain out. All = every stack leaves for its destination.
    // EvictForeign = keep what the block legitimately holds (a Consumer's fuel / a Processor's feed-input) and
    // drain only foreign items — anything the inventory's own constraint rejects. KeepAll = drain nothing (a
    // reserve container whose pool has no quota section yet, so tagging it never empties it). KeepQuota = a
    // configured reserve: keep each quota item up to its target, drain only the over-target excess, and drain
    // non-quota items to their category home. ToDemand = drain a stack only into a demand target (a machine feed or
    // a reserve deficit) and otherwise leave it where it is.
    public enum DrainMode { All, EvictForeign, KeepAll, KeepQuota, ToDemand }

    public static class Roles
    {
        // A Consumer keeps its fuel and a Processor keeps its feed-input, so both only surrender foreign items;
        // Storage and Collector drain fully. (A Processor's output inventory is a separate ManagedBlock.Output
        // source, assigned via OutputDrainFor by the caller — not routed through here.)
        public static DrainMode DrainModeFor(Role role) =>
            role == Role.Consumer || role == Role.Processor ? DrainMode.EvictForeign : DrainMode.All;

        // A DISASSEMBLING assembler runs its inventories in reverse: the output (1) holds the items being taken
        // apart (the block pulls them itself via conveyor — never scatter them back to storage) and the input (0)
        // holds the produced materials (harvest fully; in assembly mode that same inventory is protected feed).
        public static DrainMode InputDrainFor(Role role, bool disassembling) =>
            disassembling ? DrainMode.All : DrainModeFor(role);

        public static DrainMode OutputDrainFor(bool disassembling) =>
            disassembling ? DrainMode.KeepAll : DrainMode.All;

        // A machine switched off or damaged past working waits for the player, so it is given no feed and no
        // queue work. Power is deliberately left out: a brownout passes on its own, and work moved elsewhere
        // meanwhile would then be done twice.
        public static bool IsOperational(IMyTerminalBlock block) =>
            block.IsFunctional && (block as IMyFunctionalBlock)?.Enabled != false;

        public static bool IsDisassembling(IMyTerminalBlock block)
        {
            var assembler = block as IMyAssembler;
            return assembler != null && assembler.Mode == MyAssemblerMode.Disassembly;
        }

        // Keyed on the ingame interfaces. Everything unmatched (cargo, cockpit, connector, sorter, an untyped
        // modded machine) falls back to Storage. Most modded machines subtype a vanilla block and so match here.
        // The ice-fed Irrigation System implements IMyGasGenerator, so it lands in Consumer with no special case.
        public static Role Of(IMyTerminalBlock block)
        {
            if (block is IMyRefinery || block is IMyAssembler) return Role.Processor;
            if (block is IMyReactor || block is IMyGasGenerator) return Role.Consumer;
            if (block is IMyShipDrill || block is IMyShipGrinder || block is IMyShipWelder || block is IMyCollector) return Role.Collector;
            return Role.Storage;
        }

        // Which machines get topped up, keyed on the same ingame interfaces as Of. Assemblers are Processors but
        // return None — they are fed elsewhere, not by this basic feed.
        public static FeedKind FeedKindOf(IMyTerminalBlock block)
        {
            if (block is IMyReactor) return FeedKind.Reactor;
            if (block is IMyGasGenerator) return FeedKind.GasGen;
            if (block is IMyRefinery) return FeedKind.Refinery;
            return FeedKind.None;
        }

        // The feed category per kind: reactors burn Ingots (uranium and modded fusion fuels alike — the block's
        // own CanItemsBeAdded constraint decides which, so no per-fuel table); O2/H2 generators and refineries
        // take Ores. None → null (not fed).
        public static string FeedCategoryFor(FeedKind kind)
        {
            if (kind == FeedKind.Reactor) return Categories.Ingots;
            if (kind == FeedKind.GasGen || kind == FeedKind.Refinery) return Categories.Ores;
            return null;
        }

        // Turn off a fed machine's vanilla conveyor auto push/pull so its input stops refilling past our fill
        // target and fighting the feed. Write-on-change: a terminal-property set is network-replicated, so flip
        // only a machine currently on — after the first scan this never writes again. Keyed on the same fed
        // interfaces as FeedKindOf; a non-fed block (assembler, cargo) matches nothing and is left untouched.
        public static void DisableConveyorSystem(IMyTerminalBlock block)
        {
            var reactor = block as IMyReactor;
            if (reactor != null) { if (reactor.UseConveyorSystem) reactor.UseConveyorSystem = false; return; }
            var gen = block as IMyGasGenerator;
            if (gen != null) { if (gen.UseConveyorSystem) gen.UseConveyorSystem = false; return; }
            var refinery = block as IMyRefinery;
            if (refinery != null && refinery.UseConveyorSystem) refinery.UseConveyorSystem = false;
        }

        // How many units of a stack to move into a machine so its fill lands ON the target, not at 100%.
        // TransferItemTo moves the WHOLE stack handed to it, so an uncapped stack fills the machine to max
        // regardless of the soft target — hence the computed cap: roomVol / per-unit volume, clamped to the
        // stack size. A non-fractional item can only move whole units, so floor it; a fractional item moves the
        // exact partial. Returns 0 when there is no room, no per-unit volume, or less than one whole unit fits.
        public static double FeedUnits(double roomVol, double perUnitVol, double stackUnits, bool usesFractions)
        {
            if (roomVol <= 0.0 || perUnitVol <= 0.0) return 0.0;
            var units = roomVol / perUnitVol;
            if (units > stackUnits) units = stackUnits;
            if (!usesFractions) units = Math.Floor(units);
            return units < 0.0 ? 0.0 : units;
        }

        // Cap a count-based move: take at most `want` units of a `stackUnits` stack, flooring to whole units for a
        // non-fractional item (which can't move a partial). Used by a reserve intake to move only its deficit.
        public static double CapUnits(double want, double stackUnits, bool usesFractions)
        {
            var units = want < stackUnits ? want : stackUnits;
            if (!usesFractions) units = Math.Floor(units);
            return units < 0.0 ? 0.0 : units;
        }

        // A remnant under this many units is useless and shows as a ~0 stack, so a capped move takes it along.
        public const double Dust = 0.01;

        public static bool LeavesDust(double units, double stackUnits) => stackUnits - units < Dust;

        // Through double, never float: MyFixedPoint's float operator scales in float, landing micro-units off on a
        // large or many-digit amount — a whole-stack move then strands a ~0 remnant.
        public static MyFixedPoint Amount(double units, MyInventoryItem item) =>
            LeavesDust(units, (double)item.Amount) ? item.Amount : (MyFixedPoint)units;

        // Is a stack sitting in a constraint-limited inventory foreign (evict) or legitimate (keep)?
        // CanItemsBeAdded gives one "no" for two reasons — the constraint rejects the type, or the inventory is
        // too full for another unit — so disambiguate by per-unit volume: a "no" (canAddOne=false) means a
        // genuine constraint rejection only when a whole unit WOULD fit on volume. When less than a unit of room
        // remains the "no" is volume-driven, so keep the item — honoring a manual over-fill at any level.
        public static bool IsForeign(double freeVol, double perUnitVol, bool canAddOne) =>
            freeVol < perUnitVol ? false : !canAddOne;
    }

    // The live IAssemblerState over one IMyAssembler. GetQueue and CanUseBlueprint are local reads (no conveyor
    // call); all instances share one reused MyProductionItem buffer — safe because the ledger walk touches one
    // assembler at a time. Append and the mode set are the network-replicated terminal actions the craft stepper
    // budgets to one per tick.
    public class AssemblerState : IAssemblerState
    {
        const string BlueprintPrefix = "MyObjectBuilder_BlueprintDefinition/";

        readonly IMyAssembler assembler;
        readonly List<MyProductionItem> buffer;
        readonly List<MyInventoryItem> itemBuffer;
        readonly Dictionary<string, float> upgrades;

        public AssemblerState(IMyAssembler block, int construct, AssemblerMark mark, List<MyProductionItem> sharedBuffer,
            List<MyInventoryItem> sharedItemBuffer, Dictionary<string, float> sharedUpgrades)
        {
            assembler = block;
            Construct = construct;
            Mark = mark;
            buffer = sharedBuffer;
            itemBuffer = sharedItemBuffer;
            upgrades = sharedUpgrades;
        }

        public long Id => assembler.EntityId;
        public string Name => assembler.CustomName;
        public string Model => assembler.BlockDefinition.SubtypeName;
        public int Construct { get; }
        public AssemblerMark Mark { get; }
        public bool IsReceiver { get; set; }

        // Survival kits have no cooperative mode and ignore the switch. Known by model rather than by setting it and
        // reading it back: every set is network-replicated, and one the game ignores would be retried on every build.
        public bool CanCooperate => !Model.Contains("SurvivalKit");
        public bool Cooperative => assembler.CooperativeMode;
        public void SetCooperative(bool on, bool dry)
        {
            if (!dry) assembler.CooperativeMode = on;
        }

        // The game divides a blueprint's build time by the world speed multiplier times (the model's base speed +
        // its Productivity upgrades), so relative to other assemblers the multiplier cancels out. A PB can't read a
        // definition's base speed, hence the per-model values; an unlisted model counts as a vanilla assembler.
        public double Speed
        {
            get
            {
                upgrades.Empty();
                (assembler as IMyUpgradableBlock)?.FillUpgradesDictionary(upgrades);
                float productivity;
                upgrades.Get("Productivity", out productivity);
                var m = Model;
                return Math.Max(0.1, productivity + (m == "SX_Wade2" ? 5 : m == "BasicAssembler" ? 0.7
                    : m.Contains("SurvivalKit") ? 0.3 : 1));
            }
        }
        public bool IsWorking => assembler.IsWorking;
        public bool IsOperational => Roles.IsOperational(assembler);

        public bool IsDisassembling => assembler.Mode == MyAssemblerMode.Disassembly;

        public void QueueInto(List<QueueEntry> into)
        {
            into.Empty();
            buffer.Empty();
            assembler.GetQueue(buffer);
            foreach (var item in buffer)
                into.Push(new QueueEntry { Blueprint = item.BlueprintId.SubtypeName, Amount = (double)item.Amount });
        }

        public void OutputInto(Dictionary<string, double> into)
        {
            into.Empty();
            itemBuffer.Empty();
            assembler.GetInventory(1).GetItems(itemBuffer);
            foreach (var item in itemBuffer)
            {
                var key = ItemKey.Of(item.Type);
                double amount;
                into[key] = (into.Get(key, out amount) ? amount : 0.0) + (double)item.Amount;
            }
        }

        // Parse can throw on a malformed id and CanUseBlueprint on an unknown one — either way the candidate is
        // not a usable blueprint, which is exactly what false means here.
        public bool CanUse(string blueprint)
        {
            try { return assembler.CanUseBlueprint(MyDefinitionId.Parse(BlueprintPrefix + blueprint)); }
            catch (Exception) { return false; }
        }

        // No try/catch: the planner only appends CanUse-verified blueprints, so a throw here is a genuine fault
        // the scheduler's crash breaker should surface, not swallow.
        public void Append(string blueprint, double amount, bool dry)
        {
            if (!dry) assembler.AddQueueItem(MyDefinitionId.Parse(BlueprintPrefix + blueprint), amount);
        }

        public void ClearQueue(bool dry)
        {
            if (!dry) assembler.ClearQueue();
        }

        // One removal per call, from the newest entry of the blueprint and never the unit in production, whose
        // progress would be lost — the planner sized the amount by the same rule, but the queue may have moved since.
        public double Trim(string blueprint, double amount, bool dry)
        {
            buffer.Empty();
            assembler.GetQueue(buffer);
            for (var i = buffer.Count - 1; i >= 0; i--)
            {
                if (buffer[i].BlueprintId.SubtypeName != blueprint) continue;
                amount = Math.Min(amount, (double)buffer[i].Amount - (i == 0 ? 1 : 0));
                if (amount < 1) return 0;
                if (!dry) assembler.RemoveQueueItem(i, amount);
                return amount;
            }
            return 0;
        }

        // Write-on-change: a mode set is network-replicated, so an already-matching mode writes nothing.
        public void SetDisassembling(bool on, bool dry)
        {
            var mode = on ? MyAssemblerMode.Disassembly : MyAssemblerMode.Assembly;
            if (!dry && assembler.Mode != mode) assembler.Mode = mode;
        }
    }
}
