using System;
using System.Collections.Generic;
using System.Text;

namespace IngameScript
{
    // Actions stay off the log: a per-plan changing amount would make it a per-cycle stream. States go through a
    // StateChannel, so the panel line and the log entry are one string and can't word a condition differently.
    public class ProductionStatus
    {
        readonly List<string> actions = new List<string>();
        readonly StateChannel states;
        static readonly string[] Unassigned = { ": no Main assembler", ": Main assembler off", ": Main assembler disassembling", ": no assembler" };

        public ProductionStatus(Action<string> log) { states = new StateChannel(log); }

        public List<string> Actions => actions;
        public List<string> States => states.Current;

        public void Compose(CraftPlanner craft, RefinePlanner refine, DiscoveryCoordinator discovery,
                            BlueprintProbe probe, YieldSample sample, MarkCheck marks)
        {
            actions.Empty();
            states.Begin();
            foreach (var a in craft.Actions)
            {
                if (a.Kind == CraftActionKind.AppendAssembly) actions.Push("crafting " + a.Item + " +" + a.Amount);
                else if (a.Kind == CraftActionKind.AppendDisassembly) actions.Push("disassembling " + a.Item + " " + a.Amount);
            }
            foreach (var r in refine.Requests) actions.Push("refining " + r.Ore + " for " + r.Ingot);
            // A knowledge request already says learning is under way; the generic obstruction would restate it.
            foreach (var key in craft.NeedBlueprint)
                if (!discovery.HasBlueprint(key)) states.Push("craft " + key + ": no blueprint");
            states.Push(probe.Status);
            foreach (var line in marks.Problems) states.Push(line);
            // A mark problem already says why nothing takes orders, so "no Main" would only restate it.
            for (var why = 0; why < craft.Unassigned.Length; why++)
                if (marks.Problems.Count == 0 || why % CraftPlanner.Disassembly != CraftPlanner.NoMain)
                    foreach (var key in craft.Unassigned[why])
                        states.Push(why < CraftPlanner.Disassembly ? "craft " + key + Unassigned[why]
                            : "disassemble " + key + (why == CraftPlanner.Disassembly + CraftPlanner.NoAssembler ? ": no free assembler" : Unassigned[why - CraftPlanner.Disassembly]));
            foreach (var key in craft.HeldAssembly) states.Push("craft " + key + ": disassembly queued");
            foreach (var key in craft.HeldDisassembly) states.Push("disassemble " + key + ": assembly queued");
            states.Push(craft.StalledQueueStatus);
            foreach (var key in refine.NeedYield)
                if (!sample.Active || sample.Ore != key) states.Push("refine " + key + ": yield unknown");
            states.Push(sample.Status);
            foreach (var line in refine.Blocked) states.Push(line);
            states.Commit();
        }
    }

    // Reused across renders. Overflow marks a category whose only home is the catch-all: repeating the catch-all
    // row's fill on every such row would hide which categories have a container of their own.
    public class CategoryFill
    {
        public readonly string Name;
        public bool HasRoute, Explicit, Overflow;
        public double Fraction, Cur, Max;
        public CategoryFill(string name) { Name = name; }
    }

    // One text string of block chars, never sprites: a sprite DrawFrame is an atomic multi-ms spike, a WriteText cheap.
    public static class StatusView
    {
        const int BarWidth = 10;
        const int NamePad = 15;              // widest built-in ("Consumables", 11) + headroom for learned categories
        const char Filled = '█';
        const char Empty = '░';

        // stamp shares the log lines' clock, so a log entry's age reads against it. blockName has its own line so a
        // long name never pushes the health word off the panel. messages' lists are filled in place, so a render
        // never allocates. assemblers sits above the rule because it describes the setup; negative omits it.
        public static void Render(StringBuilder sb, string version, string blockName, string health, string stamp,
                                  List<CategoryFill> fills, List<CategoryFill> reserves, List<List<string>> messages,
                                  int assemblers = -1, bool disassembling = false, int wrap = 0)
        {
            sb.Empty();
            sb.Put("SHEPHERD ").Put(version).Put(" · ").Line(health);
            sb.Line(blockName);
            sb.Line(stamp);
            if (assemblers >= 0)
                sb.Put(assemblers).Put(assemblers == 1 ? " Assembler Controlled" : " Assemblers Controlled")
                    .Line(disassembling ? ", one disassembling" : "");
            if (fills != null)
                foreach (var f in fills)
                {
                    sb.Put(Pad(f.Name));
                    if (!f.HasRoute) { sb.Line("──no route──"); continue; }
                    if (f.Overflow) { sb.Line("──catch-all──"); continue; }
                    AppendBar(sb, f.Fraction, false);
                    sb.Line();
                }
            if (reserves != null && reserves.Count > 0)
            {
                for (var i = 0; i < NamePad + BarWidth; i++) sb.Put('─');
                sb.Line();
                foreach (var r in reserves)
                {
                    sb.Put(Pad(r.Name));
                    // A pool with only All quotas has no target to measure against.
                    if (!r.HasRoute) { sb.Line("──all──"); continue; }
                    AppendBar(sb, r.Fraction, true);
                    sb.Line();
                }
            }
            // Drawn even with nothing to say, so the layout doesn't shift as states come and go.
            for (var i = 0; i < NamePad + BarWidth; i++) sb.Put('─');
            sb.Line();
            if (messages != null)
                foreach (var block in messages)
                    foreach (var line in block) Wrapped(sb, line, wrap);
        }

        // A panel's font is sized to its bars, so a long message would run off the edge; continuations are indented
        // so each still reads as one message.
        static void Wrapped(StringBuilder sb, string line, int width)
        {
            var indent = "";
            while (width > 2 && indent.Length + line.Length > width)
            {
                var room = width - indent.Length;
                var cut = line.LastIndexOf(' ', room);
                if (cut < 1) cut = room;
                sb.Put(indent).Line(line.Cut(0, cut));
                line = line.Cut(cut).TrimStart();
                indent = "  ";
            }
            sb.Put(indent).Line(line);
        }

        // Reserve quota fill can be over-stocked, category volume fill cannot.
        static void AppendBar(StringBuilder sb, double fraction, bool allowOver)
        {
            var frac = fraction < 0.0 ? 0.0 : fraction > 1.0 ? 1.0 : fraction;
            var filled = (int)(frac * BarWidth + 0.5);
            for (var i = 0; i < BarWidth; i++) sb.Put(i < filled ? Filled : Empty);
            var pctFrac = allowOver ? (fraction < 0.0 ? 0.0 : fraction) : frac;
            var pct = (int)(pctFrac * 100.0 + 0.5);
            sb.Put(' ').Put(pct).Put('%');
        }

        static string Pad(string name)
        {
            if (name == null) name = "";
            return name.Length >= NamePad ? name + " " : name.PadRight(NamePad);
        }
    }

    // Grouped under the TypeId so the type tells Ore/Iron from Ingot/Iron and header + "/" + line name rebuilds the
    // Custom Data key. An unconfigured pool lists everything its containers hold, so the user can read the keys.
    public static class LevelsView
    {
        static readonly List<string> keys = new List<string>();
        static readonly List<double> amounts = new List<double>();
        static readonly List<string> levels = new List<string>();
        static readonly Dictionary<string, int> groupOf = new Dictionary<string, int>();
        static readonly List<int> group = new List<int>(), starts = new List<int>(), order = new List<int>();

        public static void Render(StringBuilder sb, ReservePool pool)
        {
            var configured = pool.QuotaCount > 0;
            keys.Empty();
            if (configured) keys.AddRange(pool.Quotas.Keys);
            else keys.AddRange(pool.Counts.Keys);
            amounts.Empty();
            foreach (var key in keys) amounts.Push(pool.Count(key));

            double fill;
            sb.Put(pool.Name.Length == 0 ? "(default)" : pool.Name).Put(" · ").Line(
                pool.Containers == 0 ? "no container"
                : !configured ? "no quotas"
                : pool.TryQuotaFill(out fill) ? (int)(fill * 100.0 + 0.5) + "%" : "all");
            Lines(sb, configured ? pool : null);
        }

        // stockKeys/categories are parallel. A null category is the catch-all section: what the catch-all containers
        // hold, of any category.
        public static void Render(StringBuilder sb, string category, List<string> stockKeys, List<string> categories, ProductionLedger ledger)
        {
            if (sb.Length > 0) sb.Line();
            keys.Empty();
            amounts.Empty();
            for (var i = 0; i < stockKeys.Count; i++)
            {
                if (category != null && categories[i] != category) continue;
                var amount = category == null ? ledger.Caught(stockKeys[i]) : ledger.Stock(stockKeys[i]);
                if (amount > 0) { keys.Push(stockKeys[i]); amounts.Push(amount); }
            }
            sb.Put(category ?? "catch-all").Put(" · ").Line(keys.Count == 0 ? "empty" : keys.Count + " items");
            Lines(sb, null);
        }

        static void Lines(StringBuilder sb, ReservePool quotas)
        {
            var width = 0;
            var levelWidth = 0;
            levels.Empty();
            for (var i = 0; i < keys.Count; i++)
            {
                if (Name(keys[i]).Length > width) width = Name(keys[i]).Length;
                Quota q;
                var level = Amount(amounts[i]);
                if (quotas != null && quotas.TryGetQuota(keys[i], out q)) level += "/" + q.Format();
                levels.Push(level);
                if (level.Length > levelWidth) levelWidth = level.Length;
            }

            // A counting pass, not a sort: a stock panel can list hundreds of items in one run.
            groupOf.Empty();
            group.Empty();
            starts.Empty();
            order.Empty();
            foreach (var key in keys)
            {
                var type = TypeOf(key);
                int g;
                if (!groupOf.Get(type, out g)) { groupOf[type] = g = starts.Count; starts.Push(0); }
                starts[g]++;
                group.Push(g);
                order.Push(0);
            }
            for (int g = 0, at = 0; g < starts.Count; g++) { var c = starts[g]; starts[g] = at; at += c; }
            for (var i = 0; i < keys.Count; i++) order[starts[group[i]]++] = i;
            for (var n = 0; n < order.Count; n++)
            {
                var j = order[n];
                var key = keys[j];
                if (n == 0 || group[j] != group[order[n - 1]]) sb.Line(TypeOf(key));
                sb.Put(' ').Put(Name(key).PadRight(width)).Put("  ").Put(levels[j]);
                Quota q;
                // All has no amount to measure against, and a 0 target none to divide by.
                if (quotas != null && quotas.TryGetQuota(key, out q) && q.Mode != QuotaMode.All && q.Target > 0)
                    sb.Append(' ', levelWidth - levels[j].Length).Put(((int)(amounts[j] / q.Target * 100.0 + 0.5) + "%").PadLeft(6));
                sb.Line();
            }
        }

        // The one place a display name is derived, so an alias lookup would replace only this.
        static string Name(string key) => key.Cut(key.IndexOf('/') + 1);

        static string TypeOf(string key)
        {
            var slash = key.IndexOf('/');
            return slash < 0 ? "" : key.Cut(0, slash);
        }

        // Exact below ten thousand so a typed target reads back as written; ingots carry fractions. No millions
        // suffix: an M after the number is the Min quota letter, so 2,000,000 reads 2000k.
        public static string Amount(double v) =>
            v >= 1e4 ? (v / 1e3).ToString("0.#") + "k"
            : v.ToString("0.#");
    }
}
