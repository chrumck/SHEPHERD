using System.Collections.Generic;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // All instances share one item buffer, so only one source may be walked at a time: a Refresh silently overwrites
    // the previous walker's snapshot. Program's own reads borrow it only on ticks where no job walks a source.
    public class InventorySource : ISortSource, ICategoryAcceptor
    {
        static readonly string[] noCategories = new string[0];
        IMyInventory inv;
        readonly List<MyInventoryItem> buffer;
        readonly CategoryOverrides overrides;    // stable for the PB's lifetime, so not rebound
        DrainMode mode;
        ReservePool pool;
        MyInventoryItem pendingItem;
        IMyInventory pendingTarget;
        ICategoryAcceptor pendingAcceptor;
        MyFixedPoint? pendingAmount;             // null = whole stack
        bool pendingToFront;

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
            buffer.Empty();
            inv.GetItems(buffer);
        }

        public int Count => buffer.Count;

        public string CategoryAt(int index) => Category.ForKey(KeyAt(index), overrides);

        public string KeyAt(int index) => ItemKey.Of(buffer[index].Type);

        public double AmountAt(int index) => (double)buffer[index].Amount;

        // Eviction is a rare safety net: SE input constraints reject foreign items on every insertion path, so it only
        // bites if a mod or game update drops a type already inside from the constraint. SortJob limits where a
        // ToDemand stack may go (DemandOnly).
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

        // Room for one unit, not the whole stack, so a near-full higher-priority target still qualifies: Commit tops it
        // off and the rest spills on. A Min quota's stock stays on reserve duty, so its surplus may go only to another
        // pool's intake. A refill tank only borrows a bottle.
        public bool CanPlaceAt(int index, ICategoryAcceptor target)
        {
            Quota q;
            if (!(target is ReserveIntake || target is RefillStation) && pool != null && pool.TryGetQuota(KeyAt(index), out q) && q.Mode == QuotaMode.Min) return false;
            return target.Accepts(buffer[index].Type);
        }

        // Captures the item by value so Commit can move it by id. A refill tank's lift skips the source cap and pool
        // count: the sweep returns the bottles here before any other job runs, and capped to the excess, a reserve's
        // bottles at or under target would never be refilled.
        public void Prepare(int index, ICategoryAcceptor target)
        {
            pendingItem = buffer[index];
            pendingTarget = target.Inventory;
            pendingAcceptor = target;
            var amount = target.ClaimTransferAmount(pendingItem);
            pendingAmount = target is RefillStation ? amount : ClaimExcess(pendingItem, amount);
            // A machine consumes its input front-first and greedy top-ups merge into the front stack, so a demand
            // delivery behind one would never be processed while its in-flight cover suppresses re-requesting.
            var feed = target as MachineFeed;
            pendingToFront = feed != null && feed.IsConstrained;
        }

        // Decremented at Prepare so a sibling container sees the reduced excess this cycle; a short commit only
        // under-drains until the next recount.
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

        // The one conveyor-bound call, alone on its tick so the headroom under the bar absorbs a coincident GC pause.
        // Moving by item id makes the cross-tick split safe: a stack that shifted or vanished since Prepare moves nothing.
        public CommitResult Commit(bool dry)
        {
            if (pendingTarget == null) return CommitResult.ItemGone;
            var moved = pendingToFront
                ? CommitToFront(dry)
                : !dry && inv.TransferItemTo(pendingTarget, pendingItem, pendingAmount);
            var result = moved ? CommitResult.Moved : ClassifyFailure();
            var amount = pendingAmount.HasValue ? (double)pendingAmount.Value : (double)pendingItem.Amount;
            pendingAcceptor.CompleteTransfer(amount, moved);
            pendingAcceptor = null;
            return result;
        }

        // Only after a failed move, so a successful commit tick stays bare. One local re-read tells a stack taken or a
        // target filled meanwhile from a genuine refusal — only the last deserves "can't reach".
        CommitResult ClassifyFailure()
        {
            buffer.Empty();
            inv.GetItems(buffer);
            foreach (var item in buffer)
                if (item.ItemId == pendingItem.ItemId)
                    return pendingAcceptor.Accepts(pendingItem.Type) ? CommitResult.Refused : CommitResult.TargetFull;
            return CommitResult.ItemGone;
        }

        // Only the index overload names a target slot, so the prepared item is re-located by id in a fresh local read.
        // stackIfPossible=false keeps the arrival its own front stack rather than trusting merge semantics near an index.
        bool CommitToFront(bool dry)
        {
            buffer.Empty();
            inv.GetItems(buffer);
            for (var i = 0; i < buffer.Count; i++)
                if (buffer[i].ItemId == pendingItem.ItemId)
                    return !dry && inv.TransferItemTo(pendingTarget, i, 0, false, pendingAmount);
            return false;
        }

        // The bottle sweep's return-target face: never in the routing index, so room for one unit is its only gate.
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
