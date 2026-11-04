using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    public enum Role
    {
        Storage,
        Collector,
        Consumer,
        Processor
    }

    // Output is a Processor's GetInventory(1), else null. ReservePool: "" = the default pool, null = not a reserve.
    // A struct: the always-rescan rebuilds this list every cycle, and value semantics keep that from allocating per block.
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

    public enum FeedKind { None, Reactor, GasGen, Refinery }

    // EvictForeign drains only what the block's own constraint rejects. KeepAll drains nothing (an unconfigured
    // reserve, so tagging it never empties it). KeepQuota drains a quota item's over-target excess and every non-quota
    // item. ToDemand drains a stack only into a demand target (a machine feed or a reserve deficit).
    public enum DrainMode { All, EvictForeign, KeepAll, KeepQuota, ToDemand }

    public static class Roles
    {
        public static DrainMode DrainModeFor(Role role) =>
            role == Role.Consumer || role == Role.Processor ? DrainMode.EvictForeign : DrainMode.All;

        // A disassembling assembler runs its inventories in reverse: the output holds the items being taken apart
        // (it pulls them itself — never scatter them) and the input holds the produced materials.
        public static DrainMode InputDrainFor(Role role, bool disassembling) =>
            disassembling ? DrainMode.All : DrainModeFor(role);

        public static DrainMode OutputDrainFor(bool disassembling) =>
            disassembling ? DrainMode.KeepAll : DrainMode.All;

        // Power is deliberately left out: a brownout passes on its own, and work moved elsewhere meanwhile would then
        // be done twice.
        public static bool IsOperational(IMyTerminalBlock block) =>
            block.IsFunctional && (block as IMyFunctionalBlock)?.Enabled != false;

        public static bool IsDisassembling(IMyTerminalBlock block)
        {
            var assembler = block as IMyAssembler;
            return assembler != null && assembler.Mode == MyAssemblerMode.Disassembly;
        }

        // Keyed on ingame interfaces, so a modded machine subtyping a vanilla one needs no setup; an untyped one falls
        // back to Storage. The ice-fed Irrigation System is an IMyGasGenerator.
        public static Role Of(IMyTerminalBlock block)
        {
            if (block is IMyRefinery || block is IMyAssembler) return Role.Processor;
            if (block is IMyReactor || block is IMyGasGenerator) return Role.Consumer;
            if (block is IMyShipDrill || block is IMyShipGrinder || block is IMyCollector) return Role.Collector;
            return Role.Storage;
        }

        // Assemblers return None: they pull their own ingredients.
        public static FeedKind FeedKindOf(IMyTerminalBlock block)
        {
            if (block is IMyReactor) return FeedKind.Reactor;
            if (block is IMyGasGenerator) return FeedKind.GasGen;
            if (block is IMyRefinery) return FeedKind.Refinery;
            return FeedKind.None;
        }

        // The block's own constraint picks the fuel within the category, so modded fuels need no table.
        public static string FeedCategoryFor(FeedKind kind)
        {
            if (kind == FeedKind.Reactor) return Categories.Ingots;
            if (kind == FeedKind.GasGen || kind == FeedKind.Refinery) return Categories.Ores;
            return null;
        }

        // A fed machine's conveyor auto-pull would refill past the fill target and fight the feed. Write-on-change: a
        // terminal-property set is network-replicated.
        public static void DisableConveyorSystem(IMyTerminalBlock block)
        {
            var reactor = block as IMyReactor;
            if (reactor != null) { if (reactor.UseConveyorSystem) reactor.UseConveyorSystem = false; return; }
            var gen = block as IMyGasGenerator;
            if (gen != null) { if (gen.UseConveyorSystem) gen.UseConveyorSystem = false; return; }
            var refinery = block as IMyRefinery;
            if (refinery != null && refinery.UseConveyorSystem) refinery.UseConveyorSystem = false;
        }

        // An uncapped TransferItemTo fills the machine to max, so the move is capped to land the fill on its target.
        public static double FeedUnits(double roomVol, double perUnitVol, double stackUnits, bool usesFractions)
        {
            if (roomVol <= 0.0 || perUnitVol <= 0.0) return 0.0;
            var units = roomVol / perUnitVol;
            if (units > stackUnits) units = stackUnits;
            if (!usesFractions) units = Math.Floor(units);
            return units < 0.0 ? 0.0 : units;
        }

        // A member whose share of a split would be under this fraction of its target drops out of the split, so a
        // small stack goes whole to the emptiest machines rather than costing a transfer per machine.
        public const double MinShare = 0.1;

        // Water-fill: pouring a stack into machines raises the emptiest to one common fill (a fraction of each one's
        // target), so a short supply leaves none empty while another sits full. Pouring in parts reaches the level
        // pouring the total would, so each placement needs only the stack in hand. fills ascend, paired with targets.
        public static double LevelShare(List<double> fills, List<double> targets, double volume, double fill, double target)
        {
            var n = fills.Count;
            if (n == 0) return 0.0;
            double sumT = 0, sumFT = 0, level = 0;
            var k = 0;
            for (; ; k++)
            {
                sumT += targets[k];
                sumFT += fills[k] * targets[k];
                level = (volume + sumFT) / sumT;
                if (k == n - 1 || level <= fills[k + 1]) break;
            }
            level = Math.Min(level, 1.0);
            while (k > 0 && level - fills[k] < MinShare)
            {
                sumT -= targets[k];
                sumFT -= fills[k] * targets[k];
                k--;
                level = Math.Min((volume + sumFT) / sumT, 1.0);
            }
            return Math.Max(0.0, level - fill) * target;
        }

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

        // CanItemsBeAdded's "no" means a constraint rejection or no room for a unit, so it counts as foreign only when
        // a whole unit would fit on volume — honoring a manual over-fill at any level.
        public static bool IsForeign(double freeVol, double perUnitVol, bool canAddOne) =>
            freeVol < perUnitVol ? false : !canAddOne;
    }

    // The instances share one queue buffer, safe because the ledger walk reads one assembler at a time.
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
