using System;
using System.Collections.Generic;
using System.Text;

namespace IngameScript
{
    // The production section of the status panel, composed once per plan tick from the planners' outputs. Two kinds
    // of line: an ACTION describes work happening right now (status only — a per-plan changing amount would turn the
    // log into a per-cycle stream), a STATE an obstruction or learning step that holds until something changes.
    // States go through a StateChannel, so a state's one string is both the panel line and the log entry and the two
    // surfaces cannot word the same condition differently. Wording is operation-first ("craft X: no blueprint") so a
    // glance reads as verb, subject, obstruction.
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
            // A knowledge request for the item means learning is already under way and says so in its own line;
            // the generic obstruction would only restate it less usefully.
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

    // One category's fill state for the status LCD readout: its name, whether any container accepts it, and the
    // aggregate fill fraction (0..1) across those containers. Reused across renders (the live layer refills the
    // same instances each tick). Cur/Max/Explicit are the live layer's per-render tally. Overflow marks a category
    // whose only home is the catch-all: its fill is the catch-all row's, so repeating it per category would make
    // every unhomed row read the same number and hide which categories actually have a container.
    public class CategoryFill
    {
        public readonly string Name;
        public bool HasRoute, Explicit, Overflow;
        public double Fraction, Cur, Max;
        public CategoryFill(string name) { Name = name; }
    }

    // Formats the [SHEPHERD] status LCD: the glanceable human view — a run-state health word, a liveness stamp,
    // and a per-category fill bar. Rendered as ONE text string (Unicode block chars in a fixed-width font),
    // never sprites, so the whole screen is a single cheap WriteText (a sprite DrawFrame is an atomic multi-ms
    // spike). Runtime numbers stay on [SHEPHERD-DEBUG], not here.
    public static class StatusView
    {
        const int BarWidth = 10;
        const int NamePad = 15;              // widest built-in ("Consumables", 11) + headroom for learned categories
        const char Filled = '█';
        const char Empty = '░';

        // stamp is the current wall-clock — it advances every render as a "not stuck" liveness signal and shares
        // the log lines' time source, so a log stamp can be read against it to see how recent an entry is. blockName
        // (the rendering PB's name, on its own line so a long name never pushes the health word off the panel) says
        // which of several SHEPHERD PBs leads.
        // messages is the fixed set of blocks whose contents change in place (each is a StateChannel's live set or
        // the production actions), so a render never allocates and never has to be handed a rebuilt list.
        // assemblers sits above the rule because it describes the setup, not something to act on; negative omits it.
        public static void Render(StringBuilder sb, string version, string blockName, string health, string stamp,
                                  List<CategoryFill> fills, List<CategoryFill> reserves, List<List<string>> messages,
                                  int assemblers = -1, bool disassembling = false)
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
                    // Reserve fill is quota-based (have/want) and may exceed 100% when over-stocked, unlike the
                    // volume-bounded category fills — so its percent isn't clamped. A pool with only All-mode
                    // quotas has no target to measure against, so it shows a marker instead of a bar.
                    if (!r.HasRoute) { sb.Line("──all──"); continue; }
                    AppendBar(sb, r.Fraction, true);
                    sb.Line();
                }
            }
            // The message section: current production work plus every standing state. Its rule is drawn even when
            // there is nothing to say, so the panel's layout doesn't shift under the reader as states come and go —
            // and an empty section is itself the signal that nothing needs attention.
            for (var i = 0; i < NamePad + BarWidth; i++) sb.Put('─');
            sb.Line();
            if (messages != null)
                foreach (var block in messages)
                    foreach (var line in block) sb.Line(line);
        }

        // The bar always clamps to [0, BarWidth]; allowOver lets the trailing percent run past 100 (reserve quota
        // fill can be over-stocked, category volume fill cannot).
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

    // Formats one reserve pool for a [SHEPHERD-RESERVE] levels LCD, or one category's grid-wide stock for a
    // [SHEPHERD:...] LCD. Items are grouped under their TypeId so each line carries only the SubtypeId: the type
    // alone tells Ore/Iron from Ingot/Iron, and header + "/" + line name rebuilds the exact Custom Data key. A
    // configured pool shows have/quota per quota line; an unconfigured one lists everything its containers hold, so
    // the user can read the keys to write its quota section.
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

        // stockKeys/categories are parallel; a key with nothing in stock is left off. A null category is the
        // catch-all section: whatever the catch-all containers hold, of any category.
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

            // Each type's lines together, the types in first-seen order: a counting pass, since a stock panel can
            // list hundreds of items in a single run.
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

        // The one place an item's display name is derived, so a future alias lookup replaces only this.
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
