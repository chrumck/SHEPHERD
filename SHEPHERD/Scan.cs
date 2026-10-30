using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // A live routing target: a block's primary inventory paired with its parsed accept-set.
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
        public bool IsDemand => false; // passive storage, never pulls an already-placed stack
        public string Name => Block.CustomName;

        public IMyInventory Inventory => inv;
        // "Room for at least one unit?" — constraint + volume in one network-free call, so a near-full or
        // type-filtered container is rejected where a bare CurrentVolume<MaxVolume test would pass it.
        public bool Accepts(MyItemType type) => inv.CanItemsBeAdded(1, type);
        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
        public void CompleteTransfer(double amount, bool moved) { }
    }

    // A machine's input inventory presented as a demand-first routing target: a below-target reactor / O2-H2
    // generator / refinery pulls its fuel or ore ahead of any storage container, and — because IsDemand is true —
    // even out of an accepting container. Self-limiting: once CurrentVolume reaches the fill target Accepts goes
    // false, its demand clears, and container anti-churn resumes, so there is no ping-pong.
    public class MachineFeed : ICategoryAcceptor, IRefineryFeed
    {
        // An open feed waits until its machine has burnt below this share of the target, then refills it in one
        // transfer: topping up whatever a running machine consumed since the last cycle was a move per machine per
        // cycle forever, and it kept the whole loop at the fast cadence. A constrained feed fills at once — it
        // carries a live ingot deficit.
        public const double RefillBelow = 0.75;
        readonly IMyInventory inv;
        readonly string name;           // the machine's display name — diagnostics only
        readonly string[] categories;   // one element: the kind's feed category
        readonly double targetVolume;   // fill ceiling = fill% × input MaxVolume; Accepts is false at/above it
        readonly long id;
        readonly Func<string, bool> openAccepts; // extra gate on the OPEN feed's routing (null = any acceptable item)
        FeedRequest request;            // this cycle's demand-first assignment; null = open greedy feed
        double pendingClaim;

        public MachineFeed(IMyInventory input, long machineId, string machineName, string feedCategory,
            double targetVolume, Func<string, bool> openFilter = null)
        {
            inv = input;
            id = machineId;
            name = machineName;
            categories = new[] { feedCategory };
            this.targetVolume = targetVolume;
            openAccepts = openFilter;
        }

        public IEnumerable<string> AcceptedCategories => categories;
        public bool IsCatchAll => false;
        // A constrained feed outranks the open greedy feeds inside the demand tier, so contested ore reaches the
        // demand-first refinery before backfill takes it. The routing index was built earlier in the cycle, so the
        // plan tick makes it forget the routes it has already built once the feeds are constrained.
        public int Priority => request != null ? 1 : 0;
        public bool IsDemand => true;
        public string Name => name;
        public long Id => id;
        public bool IsConstrained => request != null;

        // The planner's compatibility-and-room probe, and its assignment handle. CanTake deliberately bypasses
        // the open filter: compatibility must stay visible for an ore the open feed refuses to route (that
        // refusal is what hands an unknown ore to the yield sampler instead of the greedy feed).
        public bool CanTake(string oreKey) => Fits(ItemKey.ToType(oreKey));
        public void Constrain(FeedRequest r) => request = r;

        public IMyInventory Inventory => inv;
        public bool Accepts(MyItemType type) => WantsFill && RoutingAllows(ItemKey.Of(type)) && Fits(type);

        public bool WantsFill => request != null || (double)(float)inv.CurrentVolume < RefillBelow * targetVolume;

        // The routing gate alone, key-typed (offline-testable — MyItemType needs the game registry): a constrained
        // feed admits only its requested ore while the request has amount left; an open feed asks its filter (a
        // refinery's open feed takes known-yield ores only — an unknown ore must stay in storage where the yield
        // sampler can claim a full measurement batch).
        public bool RoutingAllows(string key) =>
            request != null ? request.Allows(key) : openAccepts == null || openAccepts(key);

        // Below the fill target AND the inventory's own constraint admits the fuel (no "reactors want Uranium"
        // table — a modded fuel the block accepts just works). For a non-fractional feed the room must also fit
        // one WHOLE unit, else ClaimTransferAmount floors to 0 and the commit moves nothing (a false stall);
        // fractional feeds always have room for a partial, so the GetItemInfo() call is a rare, cheap lookup.
        bool Fits(MyItemType type)
        {
            var room = targetVolume - (double)(float)inv.CurrentVolume;
            if (room <= 0.0 || !inv.CanItemsBeAdded(1, type)) return false;
            var info = type.GetItemInfo();
            return info.UsesFractions || room >= info.Volume;
        }

        // Cap the feed so the fill lands ON the target, not at 100% — and, when constrained, at the request's
        // remaining amount (reserved here, then confirmed by the transfer completion callback). Computed on the cheap
        // find tick so the bare commit tick still does nothing but the one TransferItemTo.
        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item)
        {
            var info = item.Type.GetItemInfo();
            var room = targetVolume - (double)(float)inv.CurrentVolume;
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
            if (request != null && pendingClaim > 0.0)
            {
                request.Complete(pendingClaim, moved ? amount : 0.0);
                pendingClaim = 0.0;
            }
        }
    }

    // One reserve container's inventory presented as a demand-first routing target, sharing its pool's model. Like
    // MachineFeed it out-ranks plain containers and may demand-pull a stack out of a category container to fill the
    // loadout; unlike MachineFeed it self-limits on COUNTS (the pool's per-item deficit), which volume can't bound.
    // Accepts only an item the pool has an unmet quota for; ClaimTransferAmount caps the move to that deficit (and
    // decrements it, so the pool doesn't over-pull within a cycle — a fresh recount next cycle corrects any drift).
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

    // The live IBottleTank: Intake is itself (a demand acceptor that admits only bottles the tank can physically
    // hold), Contents is the tank's own inventory as a full-drain source, Refill is the synchronous
    // IMyGasTank.RefillBottles(), HasGas gates a gasless tank. Built by Program (it owns the shared item buffer).
    // AcceptedCategories is empty because the feed targets the intake directly (BottleJob does not route it) —
    // Accepts is the only gate.
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
            contents = new InventorySource(inv, sharedBuffer, DrainMode.All, null, null); // bottles aren't routed by category on the return trip → no overrides needed
        }

        public bool HasGas => (double)tank.FilledRatio > 0.0;
        // Force-fill every bottle in the tank and report whether it consumed gas (FilledRatio dropped) — i.e.
        // whether at least one empty bottle was actually filled. RefillBottles is synchronous, so FilledRatio
        // reflects it immediately. The sweep keeps cycling a big bottle stack through the tank until a refill
        // fills nothing (all cycled bottles full, or the tank ran dry).
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
        // Only a bottle the tank can physically hold right now, and only while the tank has gas to fill it.
        public bool Accepts(MyItemType type) => HasGas && Category.IsBottle(type.TypeId) && inv.CanItemsBeAdded(1, type);
        // RefillBottles fills every bottle in the tank at once, so there is no per-unit cap — move the whole stack.
        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
        public void CompleteTransfer(double amount, bool moved) { }
    }

    // A [SHEPHERD-RESERVE...] container captured for its pool: block + inventory + pool name (empty = default pool).
    // DualRole marks a block that ALSO carried a [SHEPHERD:...] destination tag — the destination tag is ignored
    // (reserve wins), and Program logs it so the user knows.
    // A struct, like ManagedBlock: stored inline in its per-scan List, so the rebuild allocates no per-container object.
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
        public IMyTerminalBlock StatusLcd; // [SHEPHERD]        — glanceable health view
        public IMyTerminalBlock DebugLcd;  // [SHEPHERD-DEBUG]  — profiler
        public IMyTerminalBlock LogLcd;    // [SHEPHERD-LOG]    — message log
        public readonly List<Destination> Destinations = new List<Destination>();
        public readonly List<ManagedBlock> Managed = new List<ManagedBlock>();
        public readonly List<ReserveContainer> Reserves = new List<ReserveContainer>();
        public readonly List<ReserveContainer> ReserveLcds = new List<ReserveContainer>(); // levels LCDs, no inventory
        public readonly List<Destination> StockLcds = new List<Destination>(); // [SHEPHERD:...] LCDs, no inventory
        public readonly List<IMyLightingBlock> Lights = new List<IMyLightingBlock>(); // [SHEPHERD] lights
        public readonly List<IMyGasTank> Tanks = new List<IMyGasTank>(); // bottle refill stations
    }

    // The cross-tick form of a classify pass. Begin() over a fetched block list, then Step(budget) classifies up
    // to `budget` blocks per call and returns whether more remain — so one scan run can no longer grow past the
    // limiter's peak bar with grid size. Result is the accumulated ScanResult, complete once Step returns false.
    // Indexed (not foreach) because the pass resumes mid-list across ticks; hand-rolled rather than a coroutine to
    // keep the one-heavy-op-per-tick invariant explicit, like SortJob.
    public partial class ClassifyCursor
    {
        // Set by Program: the run has passed half the instruction cap. A unit's cost grows with the grid, and the cap
        // stops the script outright, so a step yields then too.
        public Func<bool> Full = () => false;
        ScanResult result = new ScanResult();
        IList<IMyTerminalBlock> blocks;
        ManagedScope scope;
        int index;

        public ScanResult Result => result;
        // Start a fresh pass. The result is allocated new (not reused): the previous one stays valid for the
        // status-fill renders that run on the refractory ticks between classify steps, so finalize can hand over
        // the fully-built set without blanking the live view mid-classify. Reuse is a measure-gated later step.
        public void Begin(IList<IMyTerminalBlock> source, ManagedScope managedScope)
        {
            result = new ScanResult();
            blocks = source;
            scope = managedScope;
            index = 0;
        }

        // Abandon the current pass so a partial classify can't leak into the next cycle after a mid-classify
        // crash or stand-down. The still-rendering previous result is untouched (finalize never ran for this
        // pass); the next Begin starts clean regardless.
        public void Reset()
        {
            result = new ScanResult();
            blocks = null;
            index = 0;
        }

        // Classify up to `budget` blocks (<= 0 = no cap), advancing the cursor. Returns true while blocks
        // remain — call again next tick; false once the pass is complete and Result is final.
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
        // Classify a single block into result — the per-block body shared by Classify and ClassifyCursor. A
        // block is managed if it is in scope, owned, has an inventory and is not excluded; each carries its Role and
        // inventories, and a home block is additionally a routing destination if it carries a [SHEPHERD:...] tag.
        // [SHEPHERD-Off] beats a destination tag. The first home [SHEPHERD]/[SHEPHERD-DEBUG]/[SHEPHERD-LOG] block is
        // the status/profiler/log LCD (captured before the exclude/inventory checks — a display has no inventory),
        // except that a [SHEPHERD] light is a status light, all of them — one listed first must not take the panel.
        //
        // A docked grid is resupply, not storage: its destination tags are inert (else docking siphons the base's
        // stock into the ship), so its containers just drain home, while its reserve containers still fill to quota.
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

            // A gun pulls its own ammo and a parachute hatch its canvas through the conveyor, so draining either only
            // ping-pongs the item back and forth at a transfer each way.
            if (block is IMyUserControllableGun || block is IMyParachute) return;

            // A gas tank is a bottle refill station, not drain-storage: its inventory holds bottles being
            // maintained, so it is captured here and NOT added to Managed (never drained by the sort).
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
