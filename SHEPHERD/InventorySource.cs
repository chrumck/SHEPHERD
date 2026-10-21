using System.Collections.Generic;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // Live ISortSource: binds SortJob's game-free contract to one real inventory. All instances share one reusable
    // item buffer — a hard invariant, not a convenience: at most ONE source may be walked per tick, since a second
    // Refresh in the same tick silently corrupts the first walker's snapshot (SortJob/BottleJob honor this by
    // touching one source per step; Program's own reads, such as the learn sweep's, borrow the buffer only on ticks
    // where no job walks a source). Instances are pooled and rebound across cycles (Rebind) rather than reallocated.
    //
    // Also a return target for the bottle sweep: a tank's Contents transfers a bottle back INTO an InventorySource.
    // The acceptor face carries no categories (bottles aren't routed on
    // the way back — they go to a known source) and just answers "room for one unit?".
    public sealed class InventorySource : ISortSource, ICategoryAcceptor
    {
        static readonly string[] noCategories = new string[0];
        IMyInventory inv;
        readonly List<MyInventoryItem> buffer;
        readonly CategoryOverrides overrides;    // shared, stable for the PB's lifetime → set once in the ctor
        DrainMode mode;
        ReservePool pool;                        // the reserve pool model when mode is KeepQuota; null otherwise
        MyInventoryItem pendingItem;             // captured by VALUE on the find tick; committed by item, not index
        IMyInventory pendingTarget;
        ICategoryAcceptor pendingAcceptor;
        MyFixedPoint? pendingAmount;             // null = whole stack; a MachineFeed / reserve-drain caps it
        bool pendingToFront;                     // deliver at the target's slot 0 (a constrained machine feed)

        public InventorySource(IMyInventory inventory, List<MyInventoryItem> sharedBuffer, DrainMode drainMode, ReservePool reservePool, CategoryOverrides categoryOverrides)
        {
            inv = inventory;
            buffer = sharedBuffer;
            mode = drainMode;
            pool = reservePool;
            overrides = categoryOverrides;
        }

        public void Rebind(IMyInventory inventory, DrainMode drainMode, ReservePool reservePool)
        {
            inv = inventory;
            mode = drainMode;
            pool = reservePool;
        }

        public void Refresh()
        {
            buffer.Clear();
            inv.GetItems(buffer);
        }

        public int Count => buffer.Count;

        public string CategoryAt(int index) => Category.ForKey(KeyAt(index), overrides);

        public string KeyAt(int index) => ItemKey.Of(buffer[index].Type);

        public double AmountAt(int index) => (double)buffer[index].Amount;

        // A DrainMode.All inventory drains every stack. An EvictForeign inventory keeps what its own constraint
        // accepts and sheds only foreign items, disambiguated by per-unit volume so a manual over-fill at any
        // level is honored and legitimate fuel/ore near 100% is never false-evicted. Eviction is a rare safety
        // net — SE input constraints reject foreign items on every insertion path, so a present item is almost
        // always kept; it only bites if a mod/game update drops a previously-valid type from the constraint.
        // A KeepQuota reserve drains a quota item only when it is over target (the over-target excess routes out,
        // capped source-side in Prepare) and drains a non-quota item fully to its category home; a quota item
        // at/under target — and every Min/All item — is kept. A ToDemand stack is drainable; SortJob restricts
        // where it may go (DemandOnly).
        public bool ShouldDrain(int index)
        {
            if (mode == DrainMode.KeepAll) return false;
            if (mode == DrainMode.All || mode == DrainMode.ToDemand) return true;
            var type = buffer[index].Type;
            if (mode == DrainMode.KeepQuota)
            {
                if (pool == null) return true;
                var key = ItemKey.Of(type);
                Quota q;
                if (!pool.TryGetQuota(key, out q)) return true;
                return pool.Excess(key) > 0.0;
            }
            var freeVol = (double)(float)inv.MaxVolume - (double)(float)inv.CurrentVolume;
            return Roles.IsForeign(freeVol, type.GetItemInfo().Volume, inv.CanItemsBeAdded(1, type));
        }

        public bool DemandOnly => mode == DrainMode.ToDemand;

        public bool IsSameAs(ICategoryAcceptor acceptor) => acceptor.Inventory == inv;

        // Ask the target itself whether it will take one unit right now. Deliberately "room for ≥1 unit", not the
        // whole stack, so a near-full higher-priority container still qualifies — Commit tops it off with what
        // fits and the remainder spills to the next target. Cheap and network-free; reachability isn't checked.
        public bool CanPlaceAt(int index, ICategoryAcceptor target) => target.Accepts(buffer[index].Type);

        // Runs on the cheap find tick and captures the item by VALUE (its id travels in the struct), not an
        // index — so Commit can move by id on the next tick. The move amount is the tighter of the target's cap
        // (a MachineFeed / reserve intake stopping ON its fill target) and this source's cap (a KeepQuota reserve
        // shedding only its over-target excess).
        public void Prepare(int index, ICategoryAcceptor target)
        {
            pendingItem = buffer[index];
            pendingTarget = target.Inventory;
            pendingAcceptor = target;
            pendingAmount = ClaimExcess(pendingItem, target.ClaimTransferAmount(pendingItem));
            // A machine consumes its input queue front-first and greedy top-ups merge into the front stack, so a
            // demand delivery landing behind one would never be processed while its in-flight cover suppresses
            // re-requesting. Delivering a constrained feed's ore at slot 0 closes that live-lock inside the one
            // conveyor call already being paid for. A concrete cast, not a seam — front placement is intrinsic to
            // a constrained MachineFeed and has no second caller.
            var feed = target as MachineFeed;
            pendingToFront = feed != null && feed.IsConstrained;
        }

        // A KeepQuota reserve moves only the over-target excess of a quota item — capped to the stack, floored to
        // whole units, and no larger than any target-side cap. Non-quota items (and non-reserve sources) drain
        // whole, governed by the target cap alone. Decrements the pool count so a sibling pool container sees the
        // reduced excess this cycle (decrement-at-Prepare: a rare short commit only ever under-drains, corrected
        // next recount).
        MyFixedPoint? ClaimExcess(MyInventoryItem item, MyFixedPoint? targetAmount)
        {
            if (mode != DrainMode.KeepQuota || pool == null) return targetAmount;
            var key = ItemKey.Of(item.Type);
            Quota q;
            if (!pool.TryGetQuota(key, out q)) return targetAmount;
            var units = Roles.CapUnits(pool.Excess(key), (double)item.Amount, item.Type.GetItemInfo().UsesFractions);
            if (targetAmount.HasValue)
            {
                var cap = (double)targetAmount.Value;
                if (cap < units) units = cap;
            }
            pool.TookOut(key, units);
            return Roles.Amount(units, item);
        }

        // The one conveyor-bound call, alone on its tick so all the sub-bar headroom is free to absorb a
        // coincident GC pause. Moving by item id makes the cross-tick split safe: a stack that shifted, drained,
        // or vanished since Prepare returns false and moves nothing, never a stale-index mis-move. A null amount
        // moves the whole stack (SE moves what fits, remainder routes onward); a MachineFeed passes a computed
        // cap so the fill stops ON its target. An unreachable target likewise just returns false.
        public CommitResult Commit()
        {
            if (pendingTarget == null) return CommitResult.ItemGone;
            var moved = pendingToFront
                ? CommitToFront()
                : inv.TransferItemTo(pendingTarget, pendingItem, pendingAmount);
            var result = moved ? CommitResult.Moved : ClassifyFailure();
            var amount = pendingAmount.HasValue ? (double)pendingAmount.Value : (double)pendingItem.Amount;
            pendingAcceptor.CompleteTransfer(amount, moved);
            pendingAcceptor = null;
            return result;
        }

        // Runs only when the transfer moved nothing, so the successful commit tick stays bare. One local re-read
        // (no conveyor) tells the three causes of a false transfer apart: the stack was taken between the find
        // and commit ticks, the target filled meanwhile, or a genuine conveyor/sorter refusal — only the last
        // deserves the "can't reach" warning.
        CommitResult ClassifyFailure()
        {
            buffer.Clear();
            inv.GetItems(buffer);
            foreach (var item in buffer)
                if (item.ItemId == pendingItem.ItemId)
                    return pendingAcceptor.Accepts(pendingItem.Type) ? CommitResult.Refused : CommitResult.TargetFull;
            return CommitResult.ItemGone;
        }

        // The index overload is the only one that can name a target slot, and a slot index must come from the
        // live inventory — so re-locate the prepared item by its id in a fresh snapshot (a local read; the tick's
        // one conveyor call stays singular). A shifted or vanished stack degrades to "no move", preserving the
        // commit-by-id safety. stackIfPossible=false keeps the arrival its own front stack rather than trusting
        // merge semantics near an explicit index; a second stack of the same ore is harmless.
        bool CommitToFront()
        {
            buffer.Clear();
            inv.GetItems(buffer);
            for (var i = 0; i < buffer.Count; i++)
                if (buffer[i].ItemId == pendingItem.ItemId)
                    return inv.TransferItemTo(pendingTarget, i, 0, false, pendingAmount);
            return false;
        }

        // The return-target face (bottle sweep): room for one unit is the only gate. No categories/priority/
        // demand — it is never placed in the routing index, only handed to a tank's Contents as a return target.
        public IMyInventory Inventory => inv;
        public bool Accepts(MyItemType type) => inv.CanItemsBeAdded(1, type);
        public MyFixedPoint? ClaimTransferAmount(MyInventoryItem item) => null;
        public void CompleteTransfer(double amount, bool moved) { }
        public IEnumerable<string> AcceptedCategories => noCategories;
        public bool IsCatchAll => false;
        public int Priority => 0;
        public bool IsDemand => false;
        public string Name => "";
    }
}
