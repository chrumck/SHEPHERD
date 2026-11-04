using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    public class Destination : ICategoryAcceptor
    {
        public readonly IMyTerminalBlock Block;
        readonly IMyInventory inv;
        readonly TagInfo tag;

        public Destination(IMyTerminalBlock block, IMyInventory inventory, TagInfo tag)
        {
            Block = block;
            inv = inventory;
            this.tag = tag;
        }

        public IEnumerable<string> AcceptedCategories => tag.Categories;
        public bool IsCatchAll => tag.IsCatchAll;
        public int Priority => tag.Priority;
        public bool IsDemand => false; // never pulls an already-placed stack
        public string Name => Block.CustomName;

        public IMyInventory Inventory => inv;
        // Constraint + volume in one network-free call: a bare CurrentVolume < MaxVolume test would pass a near-full
        // or type-filtered container.
        public bool Accepts(MyItemType type) => inv.CanItemsBeAdded(1, type);
        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
        public void CompleteTransfer(double amount, bool moved) { }
    }

    // A machine's input as a demand target: a below-target machine pulls its fuel or ore ahead of storage, even out
    // of an accepting container. Self-limiting: at the fill target Accepts goes false and anti-churn resumes, so
    // there is no ping-pong.
    public class MachineFeed : ICategoryAcceptor, IRefineryFeed
    {
        // An open feed refills only below this share of its target, then fills it in one transfer: topping up each
        // cycle's burn would cost a move per machine per cycle and hold the loop at the fast cadence. A constrained
        // feed fills at once — it carries a live ingot deficit.
        public const double RefillBelow = 0.75;
        readonly IMyInventory inv;
        readonly string name;
        readonly string[] categories;
        public readonly double Target;
        readonly long id;
        readonly Func<string, bool> openAccepts;
        readonly LevelFill group;
        FeedRequest request;            // null = open greedy feed
        double pendingClaim;
        // A failed commit leaves the machine the group's emptiest, so it would refuse its siblings every stack: it sits
        // out until the next build.
        public bool Blocked;

        public MachineFeed(IMyInventory input, long machineId, string machineName, string feedCategory,
            double targetVolume, Func<string, bool> openFilter = null, LevelFill levelFill = null)
        {
            inv = input;
            id = machineId;
            name = machineName;
            categories = new[] { feedCategory };
            Target = targetVolume;
            openAccepts = openFilter;
            group = levelFill;
            levelFill?.Members.Push(this);
        }

        public IEnumerable<string> AcceptedCategories => categories;
        public bool IsCatchAll => false;
        // A constrained feed outranks open ones in the demand tier, so contested ore reaches the demand-first refinery
        // before backfill. The index was built earlier in the cycle, so the plan tick makes it forget its routes once
        // the feeds are constrained.
        public int Priority => request != null ? 1 : 0;
        public bool IsDemand => true;
        public string Name => name;
        public long Id => id;
        public bool IsConstrained => request != null;

        // Bypasses the open filter on purpose: compatibility must stay visible for an ore the open feed refuses, since
        // that refusal is what hands an unknown ore to the yield sampler.
        public bool CanTake(string oreKey) => Fits(ItemKey.ToType(oreKey));
        public void Constrain(FeedRequest r)
        {
            request = r;
            if (group != null) group.Gen++;
        }

        public IMyInventory Inventory => inv;
        // In a group only the emptiest open feed accepts, so the order the route lists the machines in never decides
        // which of them goes short.
        public bool Accepts(MyItemType type) =>
            Eligible(ItemKey.Of(type), type) && (request != null || group == null || group.Lowest(type) == this);

        public bool Eligible(string key, MyItemType type) => WantsFill && RoutingAllows(key) && Fits(type);

        double Held => (double)(float)inv.CurrentVolume;
        public double Fill => Held / Target;
        public bool WantsFill => request != null || Held < RefillBelow * Target;

        // Key-typed so it is offline-testable (a MyItemType needs the game registry). A refinery's open filter takes
        // known-yield ores only: an unknown ore must stay in storage, where the yield sampler can claim a full batch.
        public bool RoutingAllows(string key) =>
            request != null ? request.Allows(key) : openAccepts == null || openAccepts(key);

        // No fuel table: the block's own constraint admits the fuel, so a modded fuel just works. A non-fractional item
        // also needs room for one WHOLE unit, else ClaimTransferAmount floors to 0 and the commit moves nothing.
        bool Fits(MyItemType type)
        {
            var room = Target - Held;
            if (room <= 0.0 || !inv.CanItemsBeAdded(1, type)) return false;
            var info = type.GetItemInfo();
            return info.UsesFractions || room >= info.Volume;
        }

        // The fill lands ON the target, not at 100%, and a constrained one at the request's remaining amount (reserved
        // here, confirmed in CompleteTransfer). Computed on the find tick so the commit tick stays bare.
        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item)
        {
            var info = item.Type.GetItemInfo();
            var room = Target - Held;
            // At least one unit, so a non-fractional share never floors to a move of nothing.
            if (request == null && group != null)
                room = Math.Min(room, Math.Max(group.Share(this, item.Type, info.Volume * (double)item.Amount), info.Volume));
            var units = Roles.FeedUnits(room, info.Volume, (double)item.Amount, info.UsesFractions);
            if (request != null)
            {
                units = request.Claim(units);
                pendingClaim = units;
            }
            return Roles.Amount(units, item);
        }

        public void CompleteTransfer(double amount, bool moved)
        {
            if (group != null)
            {
                group.Gen++;
                if (!moved && request == null) Blocked = true;
            }
            if (request != null && pendingClaim > 0.0)
            {
                request.Complete(pendingClaim, moved ? amount : 0.0);
                pendingClaim = 0.0;
            }
        }
    }

    // One per feed kind per build. Its open feeds share a short supply so they end at one common fill; with enough,
    // each still fills to its target.
    public class LevelFill
    {
        public readonly List<MachineFeed> Members = new List<MachineFeed>();
        // Bumped by every transfer into a member and every constraint: the memoized scan below holds only until a
        // fill changes, or the member it found stops qualifying (a hand top-off moves no transfer through here).
        public int Gen;
        int seenGen = -1;
        string seenKey;
        MachineFeed lowest;
        readonly List<double> fills = new List<double>(), targets = new List<double>();

        public MachineFeed Lowest(MyItemType type)
        {
            var key = ItemKey.Of(type);
            if (key == seenKey && Gen == seenGen && lowest?.Eligible(key, type) == true) return lowest;
            seenKey = key;
            seenGen = Gen;
            lowest = null;
            fills.Empty();
            targets.Empty();
            foreach (var m in Members)
            {
                if (m.IsConstrained || m.Blocked || !m.Eligible(key, type)) continue;
                var f = m.Fill;
                var at = 0;
                while (at < fills.Count && fills[at] <= f) at++;
                fills.Insert(at, f);
                targets.Insert(at, m.Target);
                if (at == 0) lowest = m;
            }
            return lowest;
        }

        // A live claim follows the Accepts that just scanned, so only a new key rescans. The dry pass claims every
        // member in one run with a transfer between each: a rescan or a full level per claim would make that run
        // quadratic in the group's size. Only the lowest computes a level; the others pass 0, which ends at once.
        public double Share(MachineFeed m, MyItemType type, double volume)
        {
            if (ItemKey.Of(type) != seenKey) Lowest(type);
            return Roles.LevelShare(fills, targets, m == lowest ? volume : 0.0, m.Fill, m.Target);
        }
    }

    // A reserve container as a demand target sharing its pool's model. Like MachineFeed it may pull a stack out of a
    // category container, but it self-limits on COUNTS (the pool's per-item deficit), which volume can't bound.
    // ClaimTransferAmount decrements the deficit so the pool doesn't over-pull within a cycle; the next recount
    // corrects any drift.
    public class ReserveIntake : ICategoryAcceptor
    {
        readonly IMyInventory inv;
        readonly ReservePool pool;
        readonly string name;

        public ReserveIntake(IMyInventory inventory, ReservePool reservePool, string blockName)
        {
            inv = inventory;
            pool = reservePool;
            name = blockName;
        }

        public IEnumerable<string> AcceptedCategories => pool.Categories;
        public bool IsCatchAll => false;
        public int Priority => 0;      // rank comes from the demand tier, not a number
        public bool IsDemand => true;
        public string Name => name;

        public IMyInventory Inventory => inv;
        public bool Accepts(MyItemType type) => pool.Deficit(ItemKey.Of(type)) > 0.0 && inv.CanItemsBeAdded(1, type);

        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item)
        {
            var key = ItemKey.Of(item.Type);
            var units = Roles.CapUnits(pool.Deficit(key), (double)item.Amount, item.Type.GetItemInfo().UsesFractions);
            pool.TookIn(key, units);
            return Roles.Amount(units, item);
        }
        public void CompleteTransfer(double amount, bool moved) { }
    }

    // AcceptedCategories is empty: BottleJob targets the intake directly rather than routing it, so Accepts is the
    // only gate.
    public class RefillStation : ICategoryAcceptor, IBottleTank
    {
        static readonly string[] noCategories = new string[0];
        readonly IMyGasTank tank;
        readonly IMyInventory inv;
        readonly InventorySource contents;

        public RefillStation(IMyGasTank gasTank, IMyInventory tankInventory, List<MyInventoryItem> sharedBuffer)
        {
            tank = gasTank;
            inv = tankInventory;
            contents = new InventorySource(inv, sharedBuffer, DrainMode.All, null, null); // the return trip isn't routed by category
        }

        public bool HasGas => (double)tank.FilledRatio > 0.0;
        // Whether gas was consumed, i.e. an empty bottle filled — the sweep cycles a stack through until a refill fills
        // nothing. RefillBottles is synchronous, so FilledRatio reflects it at once.
        public bool Refill(bool dry)
        {
            var before = (double)tank.FilledRatio;
            if (!dry) tank.RefillBottles();
            return (double)tank.FilledRatio < before;
        }
        public ISortSource Contents => contents;
        public ICategoryAcceptor Intake => this;

        public IEnumerable<string> AcceptedCategories => noCategories;
        public bool IsCatchAll => false;
        public int Priority => 0;      // rank comes from the demand tier, not a number
        public bool IsDemand => true;
        public string Name => tank.CustomName;

        public IMyInventory Inventory => inv;
        public bool Accepts(MyItemType type) => HasGas && Category.IsBottle(type.TypeId) && inv.CanItemsBeAdded(1, type);
        // RefillBottles fills every bottle in the tank at once, so the whole stack moves.
        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
        public void CompleteTransfer(double amount, bool moved) { }
    }

    // Pool "" = the default pool. DualRole: the block also carried a [SHEPHERD:...] tag, ignored (reserve wins) and
    // logged. A struct so the per-scan rebuild allocates no per-container object.
    public struct ReserveContainer
    {
        public IMyTerminalBlock Block;
        public IMyInventory Inventory;
        public string Pool;
        public bool DualRole;
        public bool Docked;
    }

    public class ScanResult
    {
        public IMyTerminalBlock StatusLcd;
        public IMyTerminalBlock DebugLcd;
        public IMyTerminalBlock LogLcd;
        public readonly List<Destination> Destinations = new List<Destination>();
        public readonly List<ManagedBlock> Managed = new List<ManagedBlock>();
        public readonly List<ReserveContainer> Reserves = new List<ReserveContainer>();
        public readonly List<ReserveContainer> ReserveLcds = new List<ReserveContainer>(); // levels LCDs, no inventory
        public readonly List<Destination> StockLcds = new List<Destination>(); // [SHEPHERD:...] LCDs, no inventory
        public readonly List<IMyLightingBlock> Lights = new List<IMyLightingBlock>(); // [SHEPHERD] lights
        public readonly List<IMyGasTank> Tanks = new List<IMyGasTank>(); // bottle refill stations
    }

    // A classify pass spread across ticks, so a scan run can't grow past the peak bar with grid size. Indexed, not
    // foreach, because the pass resumes mid-list.
    public partial class ClassifyCursor
    {
        // Set by Program: the run has passed half the instruction cap. A unit's cost grows with the grid and the cap
        // stops the script outright, so a step yields then too.
        public Func<bool> Full = () => false;
        ScanResult result = new ScanResult();
        IList<IMyTerminalBlock> blocks;
        ManagedScope scope;
        int index;

        public ScanResult Result => result;
        // A new result, not reused: renders between classify steps still read the previous one until finalize hands
        // over the fully built set.
        public void Begin(IList<IMyTerminalBlock> source, ManagedScope managedScope)
        {
            result = new ScanResult();
            blocks = source;
            scope = managedScope;
            index = 0;
        }

        // After a mid-classify crash or stand-down, so a partial pass can't leak into the next cycle. The result being
        // rendered is untouched: finalize never ran for this pass.
        public void Reset()
        {
            result = new ScanResult();
            blocks = null;
            index = 0;
        }

        // budget <= 0 = no cap. True while blocks remain; false once Result is final.
        public bool Step(int budget)
        {
            if (blocks == null) return false;
            var count = blocks.Count;
            var processed = 0;
            while (index < count)
            {
                if (budget > 0 && processed >= budget || Full()) return true;
                Scan.ClassifyOne(blocks[index], scope, result);
                index++;
                processed++;
            }
            return false;
        }
    }

    public static partial class Scan
    {
        // Surfaces are captured before the exclude and inventory checks, since a display has no inventory. Every
        // [SHEPHERD] light is a status light, so one listed first can't take the panel. A docked grid is resupply, not
        // storage: its destination tags are inert (else docking siphons the base's stock into the ship), while its
        // reserve containers still fill to quota.
        public static void ClassifyOne(IMyTerminalBlock block, ManagedScope scope, ScanResult result)
        {
            if (block == null) return;
            var at = scope.IndexOf(block.CubeGrid);
            if (at < 0 || !scope.Owns(block)) return;
            var home = at == 0;

            var tag = Tags.Parse(block.CustomName);

            // A docked ship's panels belong to its own SHEPHERD, standing by while docked.
            if (home)
            {
                if (tag.IsStatusLcd)
                {
                    var light = block as IMyLightingBlock;
                    if (light != null) result.Lights.Push(light);
                    else if (result.StatusLcd == null) result.StatusLcd = block;
                }
                if (tag.IsDebugLcd && result.DebugLcd == null)
                    result.DebugLcd = block;
                if (tag.IsLogLcd && result.LogLcd == null)
                    result.LogLcd = block;
            }

            if (tag.IsOff) return;
            if (!block.HasInventory)
            {
                if (tag.IsReserve) result.ReserveLcds.Push(new ReserveContainer { Block = block, Pool = tag.ReservePool ?? "", Docked = !home });
                else if (tag.IsDestination && home) result.StockLcds.Push(new Destination(block, null, tag));
                return;
            }

            var inv = block.GetInventory(0);
            if (inv == null) return;

            // A gun pulls its own ammo, a parachute hatch its canvas and a welder its components, so draining any of
            // them only ping-pongs the item at a transfer each way.
            if (block is IMyUserControllableGun || block is IMyParachute || block is IMyShipWelder) return;

            // A gas tank holds bottles being refilled: a refill station, never drained by the sort.
            if (block is IMyGasTank) { if (home) result.Tanks.Push((IMyGasTank)block); return; }

            var role = Roles.Of(block);
            // A docked ship's fuel and production stay the pilot's unless a setting opts in: whatever SHEPHERD drained,
            // fed or switched would leave with the ship.
            if (!home && (role == Role.Consumer && !scope.DockedFuel || role == Role.Processor && !scope.DockedProduction)) return;
            var output = role == Role.Processor ? block.GetInventory(1) : null;
            result.Managed.Push(new ManagedBlock
            {
                Block = block, Role = role, Input = inv, Output = output, ReservePool = tag.IsReserve ? (tag.ReservePool ?? "") : null, Construct = at,
                CatchAll = tag.IsCatchAll && home && !tag.IsReserve, Mark = tag.IsMain ? AssemblerMark.Main : tag.IsCoop ? AssemblerMark.Coop : AssemblerMark.None
            });
            if (tag.IsReserve)
                result.Reserves.Push(new ReserveContainer
                    { Block = block, Inventory = inv, Pool = tag.ReservePool ?? "", DualRole = tag.IsDestination, Docked = !home });
            else if (tag.IsDestination && home)
                result.Destinations.Push(new Destination(block, inv, tag));
        }
    }
}
