using System.Collections.Generic;
using System.Text;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class StatusViewTests
    {
        static CategoryFill Fill(string name, bool hasRoute, double fraction)
        {
            return new CategoryFill(name) { HasRoute = hasRoute, Fraction = fraction };
        }

        static string Render(string health, params CategoryFill[] fills)
        {
            return Render(health, "00:00:00", fills);
        }

        static readonly List<CategoryFill> NoReserves = new List<CategoryFill>();

        static string Render(string health, string stamp, params CategoryFill[] fills)
        {
            var sb = new StringBuilder();
            StatusView.Render(sb, "1.0.0", "Base PB", health, stamp, new List<CategoryFill>(fills), NoReserves, null);
            return sb.ToString();
        }

        static string RenderWithReserves(CategoryFill[] cats, params CategoryFill[] reserves)
        {
            var sb = new StringBuilder();
            StatusView.Render(sb, "1.0.0", "Base PB", "working", "00:00:00", new List<CategoryFill>(cats), new List<CategoryFill>(reserves), null);
            return sb.ToString();
        }

        static string RenderWithMessages(params List<string>[] blocks)
        {
            var sb = new StringBuilder();
            StatusView.Render(sb, "1.0.0", "Base PB", "working", "00:00:00", new List<CategoryFill>(), NoReserves,
                new List<List<string>>(blocks));
            return sb.ToString();
        }

        static List<string> Block(params string[] lines) => new List<string>(lines);

        [Fact]
        public void MessageSectionRendersBelowASeparator()
        {
            var text = RenderWithMessages(Block("crafting Component/SteelPlate +70", "craft X: no blueprint"));
            Assert.Contains("─────", text);
            Assert.Contains("crafting Component/SteelPlate +70", text);
            Assert.Contains("craft X: no blueprint", text);
        }

        [Fact]
        public void EveryMessageBlockRendersInOrderUnderTheOneSeparator()
        {
            var text = RenderWithMessages(Block("crafting X +1"), Block(), Block("can't reach Y for Ores"))
                .Replace("\r", "");
            var rule = text.IndexOf(new string('─', 25));
            Assert.True(rule >= 0);
            Assert.True(text.IndexOf("crafting X +1") > rule);
            Assert.True(text.IndexOf("can't reach Y for Ores") > text.IndexOf("crafting X +1"));
            Assert.Equal(1, CountOccurrences(text, new string('─', 25)));   // one rule, not one per block
        }

        [Fact]
        public void TheMessageSeparatorHoldsItsPlaceWhenThereIsNothingToSay()
        {
            // The rule is a fixed landmark: lines appearing and resolving must not shift the rest of the panel.
            var empty = RenderWithMessages(Block()).Replace("\r", "");
            var filled = RenderWithMessages(Block("craft X: no blueprint")).Replace("\r", "");
            Assert.Contains(new string('─', 25), empty);
            Assert.Equal(empty.IndexOf(new string('─', 25)), filled.IndexOf(new string('─', 25)));
            Assert.EndsWith("─\n", empty);   // nothing follows the rule when nothing needs attention
        }

        static int CountOccurrences(string text, string value)
        {
            var count = 0;
            for (var i = text.IndexOf(value); i >= 0; i = text.IndexOf(value, i + value.Length)) count++;
            return count;
        }

        [Fact]
        public void HeaderCarriesVersionAndHealthThenBlockNameThenClock()
        {
            // The block name tells which of several SHEPHERD PBs drew the panel. The clock shares the log lines' time
            // source, so a log stamp reads against it.
            var lines = Render("OK", "00:05:12", Fill(Categories.Ores, true, 0.5)).Replace("\r", "").Split('\n');
            Assert.Equal("SHEPHERD 1.0.0 · OK", lines[0]);
            Assert.Equal("Base PB", lines[1]);
            Assert.Equal("00:05:12", lines[2]);
        }

        [Fact]
        public void ClockStampIsOnItsOwnLineVerbatim()
        {
            var lines = Render("working", "99:59:59", Fill(Categories.Ores, true, 0.5)).Replace("\r", "").Split('\n');
            Assert.Equal("SHEPHERD 1.0.0 · working", lines[0]);
            Assert.Equal("99:59:59", lines[2]);
        }

        [Fact]
        public void CategoryNamesPadToAFixedWidthSoBarsAlign()
        {
            // The widest built-in ("Consumables", 11) must not push its bar past the short names' column.
            var text = Render("OK",
                Fill(Categories.Ores, true, 1.0),
                Fill(Categories.Consumables, true, 1.0)).Replace("\r", "");
            var lines = text.Split('\n');
            var ores = System.Array.Find(lines, l => l.StartsWith("Ores"));
            var cons = System.Array.Find(lines, l => l.StartsWith("Consumables"));
            Assert.Equal(ores.IndexOf('█'), cons.IndexOf('█'));
        }

        [Fact]
        public void HalfFullBarIsHalfBlocks()
        {
            var text = Render("OK", Fill(Categories.Ores, true, 0.5));
            // BarWidth 10 → 5 filled, 5 empty, "50%"
            Assert.Contains("█████░░░░░ 50%", text);
            Assert.DoesNotContain("50%!", text); // below the 90% cue — no bang
        }

        [Fact]
        public void NoBangCueBesideThePercent()
        {
            // v0.9.2: dropped the "!" almost-full cue — 100% is distinct enough on its own.
            var text = Render("OK", Fill(Categories.Ingots, true, 0.95));
            Assert.Contains("95%", text);
            Assert.DoesNotContain("!", text);
        }

        [Fact]
        public void EmptyAndFullExtremes()
        {
            var text = Render("OK",
                Fill(Categories.Ores, true, 0.0),
                Fill(Categories.Ingots, true, 1.0));
            Assert.Contains("░░░░░░░░░░ 0%", text);
            Assert.Contains("██████████ 100%", text);
            Assert.DoesNotContain("100%!", text);
        }

        [Fact]
        public void StatusNeverRedirectsToAnotherSurface()
        {
            // Every state the panel needs to convey is said outright; it never points the reader at the log.
            var text = Render("OK", "00:05:12", Fill(Categories.Ores, true, 0.5)).Replace("\r", "");
            var lines = text.Split('\n');
            Assert.Equal("00:05:12", lines[2]);
            Assert.StartsWith("Ores", lines[3]);   // fills follow the clock directly
            Assert.DoesNotContain("[SHEPHERD-LOG]", text);
        }

        [Fact]
        public void NoRouteCategoryIsFlaggedNotBarred()
        {
            var text = Render("OK", Fill(Categories.Ammo, false, 0.0));
            Assert.Contains("──no route──", text);
            Assert.DoesNotContain("0%", text); // a no-route row shows the signal, not a fake empty bar
        }

        [Fact]
        public void CatchAllOnlyCategoryShowsTheMarkerNotTheCatchAllFill()
        {
            var overflow = Fill(Categories.Ammo, true, 0.4);
            overflow.Overflow = true;
            var text = Render("OK", overflow);
            Assert.Contains("──catch-all──", text);
            Assert.DoesNotContain("40%", text);
            Assert.DoesNotContain("no route", text);
        }

        [Fact]
        public void FractionIsClampedNotOverflowed()
        {
            // A defensive clamp: a stray >1 category fraction must not print more than BarWidth blocks or >100%
            // (category fill is volume-bounded — reserve fill, which can exceed 100%, is a separate path).
            var text = Render("working", Fill(Categories.Ores, true, 1.5));
            Assert.Contains("██████████ 100%", text);
            Assert.DoesNotContain("███████████", text); // never an 11th block
        }

        [Fact]
        public void ReserveFillsRenderBelowASeparatorRule()
        {
            var text = RenderWithReserves(
                new[] { Fill(Categories.Ores, true, 0.5) },
                Fill("Ammo", true, 0.8)).Replace("\r", "");
            var sep = text.IndexOf(new string('─', 25)); // NamePad(15)+BarWidth(10) rule between the two groups
            Assert.True(sep >= 0);                        // the separator is present
            Assert.True(text.IndexOf("Ores") < sep);      // categories above the rule
            Assert.True(text.IndexOf("Ammo") > sep);      // reserves below it
            Assert.Contains("████████░░ 80%", text);       // reserve bar rendered like a category bar
        }

        [Fact]
        public void ReservePercentExceedsOneHundredWhenOverStocked()
        {
            // Quota fill above target: the bar still clamps to a full BarWidth, but the percent runs past 100.
            var text = RenderWithReserves(new CategoryFill[0], Fill("Ammo", true, 1.5)).Replace("\r", "");
            Assert.Contains("██████████ 150%", text);
            Assert.DoesNotContain("███████████", text); // never an 11th block
        }

        [Fact]
        public void AllModeReserveShowsMarkerNotABar()
        {
            // A pool with only All-mode quotas has no target to fill against (TryQuotaFill → false).
            var text = RenderWithReserves(new CategoryFill[0], Fill("Bulk", false, 0.0));
            Assert.Contains("──all──", text);
            Assert.DoesNotContain("%", text); // a marker row, not a fake percent
        }

        [Fact]
        public void NoReserveSectionWhenNoPools()
        {
            var text = RenderWithReserves(new[] { Fill(Categories.Ores, true, 0.5) }).Replace("\r", "");
            // Only the message rule remains — the reserve rule is drawn solely when there are pools to separate.
            Assert.Equal(1, CountOccurrences(text, new string('─', 25)));
            Assert.Contains("Ores", text);
            Assert.True(text.IndexOf("Ores") < text.IndexOf(new string('─', 25)));
        }

        [Fact]
        public void AssemblerCountRendersAboveTheRule()
        {
            var sb = new StringBuilder();
            StatusView.Render(sb, "1.0.0", "Base PB", "working", "00:00:00", new List<CategoryFill>(), NoReserves, null, 3);
            var lines = sb.ToString().Replace("\r", "").Split('\n');
            Assert.Equal("3 Assemblers Controlled", lines[3]);
            Assert.StartsWith("─", lines[4]);
            StatusView.Render(sb, "1.0.0", "Base PB", "working", "00:00:00", new List<CategoryFill>(), NoReserves, null, 1);
            Assert.Contains("1 Assembler Controlled\n", sb.ToString().Replace("\r", ""));
            StatusView.Render(sb, "1.0.0", "Base PB", "working", "00:00:00", new List<CategoryFill>(), NoReserves, null, 2, true);
            Assert.Contains("2 Assemblers Controlled, one disassembling", sb.ToString());
            StatusView.Render(sb, "1.0.0", "Base PB", "working", "00:00:00", new List<CategoryFill>(), NoReserves, null);
            Assert.DoesNotContain("Controlled", sb.ToString());
        }
    }
}
