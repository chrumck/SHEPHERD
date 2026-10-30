using System.Collections.Generic;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // Routing and transfer share one contract so every indexed acceptor is a usable placement target.
    public interface ICategoryAcceptor
    {
        IEnumerable<string> AcceptedCategories { get; }
        bool IsCatchAll { get; }
        int Priority { get; }
        // The backing block's display name — diagnostics only, never consulted on the routing hot path.
        string Name { get; }
        // A target expressing active demand (a below-target machine to be fed) rather than passive storage.
        // Demand acceptors route ahead of every container and are the only targets allowed to pull a stack
        // already sitting in an accepting container. A plain container is always false; a MachineFeed is true.
        bool IsDemand { get; }
        IMyInventory Inventory { get; }
        bool Accepts(MyItemType type);
        // A CLAIM, not a query: a demand target may book the amount against its live model, so call it exactly
        // once per prepared placement and never speculatively.
        MyFixedPoint? ClaimTransferAmount(MyInventoryItem item);
        void CompleteTransfer(double amount, bool moved);
    }

    // Built once per scan from the scan-ordered acceptor list; turns the naive per-item O(all containers)
    // routing into O(matching containers). Route() ranks the demand tier above the explicit-acceptor tier
    // above the catch-all tier, ordering within each tier by Priority descending then scan order.
    public class RoutingIndex
    {
        readonly Dictionary<string, List<ICategoryAcceptor>> byCategory = new Dictionary<string, List<ICategoryAcceptor>>();
        readonly List<ICategoryAcceptor> catchAll = new List<ICategoryAcceptor>();
        readonly Dictionary<ICategoryAcceptor, int> scanIndex = new Dictionary<ICategoryAcceptor, int>();
        // Route() is asked once per stack examined, and building a route allocates and sorts three tiers, so each
        // (category, built-in) pair is built once. Tiers and priorities hold for the index's life except a
        // constrained refinery feed's priority, which the plan tick raises — so it calls Forget() afterwards.
        readonly Dictionary<string, Dictionary<string, List<ICategoryAcceptor>>> routes = new Dictionary<string, Dictionary<string, List<ICategoryAcceptor>>>();
        readonly TierOrder order;

        public RoutingIndex(IEnumerable<ICategoryAcceptor> acceptorsInScanOrder)
        {
            order = new TierOrder(scanIndex);
            if (acceptorsInScanOrder == null) return;
            var next = 0;
            foreach (var acceptor in acceptorsInScanOrder)
            {
                if (acceptor == null) continue;
                if (!scanIndex.ContainsKey(acceptor)) scanIndex[acceptor] = next++;

                var cats = acceptor.AcceptedCategories;
                if (cats != null)
                {
                    foreach (var cat in cats)
                    {
                        if (string.IsNullOrEmpty(cat)) continue;
                        List<ICategoryAcceptor> list;
                        if (!byCategory.Get(cat, out list))
                        {
                            list = new List<ICategoryAcceptor>();
                            byCategory[cat] = list;
                        }
                        if (!list.Contains(acceptor)) list.Push(acceptor);
                    }
                }

                if (acceptor.IsCatchAll && !catchAll.Contains(acceptor))
                    catchAll.Push(acceptor);
            }
        }

        // builtIn is the item's built-in category. A machine registers under its kind's built-in feed category, so
        // when an override moves an item elsewhere (Ore/Ice=Ice) its demand acceptors are borrowed from there —
        // else recategorizing a fuel would starve every machine that burns it. Only the demand tier borrows: the
        // item's storage stays its own category's. The returned list is shared by every caller: read it, never change it.
        public List<ICategoryAcceptor> Route(string category, string builtIn = null)
        {
            Dictionary<string, List<ICategoryAcceptor>> byBuiltIn;
            List<ICategoryAcceptor> route;
            category = category ?? "";
            builtIn = builtIn ?? "";
            if (!routes.Get(category, out byBuiltIn)) routes[category] = byBuiltIn = new Dictionary<string, List<ICategoryAcceptor>>();
            if (!byBuiltIn.Get(builtIn, out route)) byBuiltIn[builtIn] = route = Build(category, builtIn);
            return route;
        }

        public void Forget() => routes.Empty();

        List<ICategoryAcceptor> Build(string category, string builtIn)
        {
            var demandTier = new List<ICategoryAcceptor>();
            var explicitTier = new List<ICategoryAcceptor>();
            List<ICategoryAcceptor> categoryAcceptors;
            if (!string.IsNullOrEmpty(category) && byCategory.Get(category, out categoryAcceptors))
                foreach (var a in categoryAcceptors)
                    (a.IsDemand ? demandTier : explicitTier).Push(a);
            if (builtIn != category && !string.IsNullOrEmpty(builtIn) && byCategory.Get(builtIn, out categoryAcceptors))
                foreach (var a in categoryAcceptors)
                    if (a.IsDemand && !demandTier.Contains(a)) demandTier.Push(a);
            demandTier.Sort(order);
            explicitTier.Sort(order);

            var overflowTier = new List<ICategoryAcceptor>();
            foreach (var c in catchAll)
                if (!demandTier.Contains(c) && !explicitTier.Contains(c)) overflowTier.Push(c);
            overflowTier.Sort(order);

            var result = new List<ICategoryAcceptor>(demandTier.Count + explicitTier.Count + overflowTier.Count);
            result.AddRange(demandTier);
            result.AddRange(explicitTier);
            result.AddRange(overflowTier);
            return result;
        }

        // A catch-all holding the category only as overflow — it doesn't name the category itself.
        public bool InOverflow(ICategoryAcceptor acceptor, string category)
        {
            List<ICategoryAcceptor> named;
            return acceptor.IsCatchAll
                && !(!string.IsNullOrEmpty(category) && byCategory.Get(category, out named) && named.Contains(acceptor));
        }

        // Whether the category has a destination of its own (or a catch-all) — borrowed machine demand is not one:
        // once the machines fill, the rest would strand, so the sort still reports the category as having no route.
        public bool HasHome(string category) =>
            catchAll.Count > 0 || !string.IsNullOrEmpty(category) && byCategory.ContainsKey(category);

        // Orders one tier by Priority desc, then scan index asc. The scan-index tiebreaker makes the ordering
        // total, so equal-priority acceptors keep scan order despite List.Sort being unstable. IComparer<T>,
        // never Comparison<T> — the latter is off the PB whitelist.
        class TierOrder : IComparer<ICategoryAcceptor>
        {
            readonly Dictionary<ICategoryAcceptor, int> scanIndex;
            public TierOrder(Dictionary<ICategoryAcceptor, int> scanIndex) { this.scanIndex = scanIndex; }

            public int Compare(ICategoryAcceptor x, ICategoryAcceptor y)
            {
                var byPriority = y.Priority.CompareTo(x.Priority);
                if (byPriority != 0) return byPriority;
                return scanIndex[x].CompareTo(scanIndex[y]);
            }
        }
    }
}
