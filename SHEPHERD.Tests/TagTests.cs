using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class TagTests
    {
        [Fact]
        public void SingleCategory()
        {
            var t = Tags.Parse("Cargo [SHEPHERD:Ore]");
            Assert.True(t.HasTag);
            Assert.True(t.IsDestination);
            Assert.False(t.IsCatchAll);
            Assert.Equal(new[] { "Ore" }, t.Categories);
        }

        [Fact]
        public void MultipleCategories()
        {
            var t = Tags.Parse("[SHEPHERD:Ore,Ingot]");
            Assert.Equal(new[] { "Ore", "Ingot" }, t.Categories);
            Assert.False(t.IsCatchAll);
        }

        [Fact]
        public void CatchAll()
        {
            var t = Tags.Parse("Overflow [SHEPHERD:*]");
            Assert.True(t.IsDestination);
            Assert.True(t.IsCatchAll);
            Assert.Empty(t.Categories);
        }

        [Fact]
        public void MixedCategoryAndCatchAll()
        {
            var t = Tags.Parse("[SHEPHERD:Ore,*]");
            Assert.Equal(new[] { "Ore" }, t.Categories);
            Assert.True(t.IsCatchAll);
            Assert.True(t.IsDestination);
        }

        [Fact]
        public void StatusLcd()
        {
            var t = Tags.Parse("Status [SHEPHERD]");
            Assert.True(t.HasTag);
            Assert.True(t.IsStatusLcd);
            Assert.False(t.IsDestination);
            Assert.False(t.IsCatchAll);
        }

        [Fact]
        public void OffScope()
        {
            var t = Tags.Parse("Reactor [SHEPHERD-Off]");
            Assert.True(t.HasTag);
            Assert.True(t.IsOff);
            Assert.False(t.IsDestination);
            Assert.False(t.IsStatusLcd);
        }

        [Fact]
        public void SplitScope()
        {
            var t = Tags.Parse("Connector [SHEPHERD-Split]");
            Assert.True(t.HasTag);
            Assert.True(t.IsSplit);
        }

        [Fact]
        public void AssemblerTagsMatchCaseInsensitively()
        {
            var main = Tags.Parse("Assembler [shepherd-main]");
            Assert.True(main.HasTag);
            Assert.True(main.IsMain);
            Assert.False(main.IsCoop);
            Assert.True(Tags.Parse("Assembler [SHEPHERD-COOP]").IsCoop);
            Assert.False(Tags.Parse("Assembler [SHEPHERD-Mian]").HasTag);
        }

        [Fact]
        public void AssemblerTagSitsBesideOtherTags()
        {
            var t = Tags.Parse("Assembler [SHEPHERD-Main] [SHEPHERD:Components]");
            Assert.True(t.IsMain);
            Assert.True(t.IsDestination);
        }

        [Fact]
        public void DebugLcdScope()
        {
            var t = Tags.Parse("Panel [SHEPHERD-DEBUG]");
            Assert.True(t.HasTag);
            Assert.True(t.IsDebugLcd);
            Assert.False(t.IsStatusLcd);   // distinguished by the char after SHEPHERD — never the bare status LCD
            Assert.False(t.IsLogLcd);
            Assert.False(t.IsDestination);
        }

        [Fact]
        public void LogLcdScope()
        {
            var t = Tags.Parse("Panel [SHEPHERD-LOG]");
            Assert.True(t.HasTag);
            Assert.True(t.IsLogLcd);
            Assert.False(t.IsStatusLcd);
            Assert.False(t.IsDebugLcd);
        }

        [Fact]
        public void DebugAndLogScopesAreCaseInsensitive()
        {
            Assert.True(Tags.Parse("[shepherd-debug]").IsDebugLcd);
            Assert.True(Tags.Parse("[SHEPHERD-Log]").IsLogLcd);
        }

        [Fact]
        public void NoTag()
        {
            var t = Tags.Parse("Cargo Container 3");
            Assert.False(t.HasTag);
            Assert.False(t.IsDestination);
            Assert.False(t.IsStatusLcd);
            Assert.False(t.IsOff);
            Assert.False(t.IsSplit);
            Assert.Empty(t.Categories);
        }

        [Fact]
        public void SurroundingTextAndOtherBrackets()
        {
            var t = Tags.Parse("Main [A1] Cargo [SHEPHERD:Ore] (left)");
            Assert.True(t.IsDestination);
            Assert.Equal(new[] { "Ore" }, t.Categories);
        }

        [Fact]
        public void WhitespaceInPayload()
        {
            var t = Tags.Parse("[SHEPHERD: Ore , Ingot ]");
            Assert.Equal(new[] { "Ore", "Ingot" }, t.Categories);
        }

        [Fact]
        public void EmptyPayload()
        {
            var t = Tags.Parse("[SHEPHERD:]");
            Assert.True(t.IsDestination);
            Assert.Empty(t.Categories);
            Assert.False(t.IsCatchAll);
        }

        [Fact]
        public void TrailingCommaDropsEmptyToken()
        {
            var t = Tags.Parse("[SHEPHERD:Ore,]");
            Assert.Equal(new[] { "Ore" }, t.Categories);
        }

        // Case-sensitivity contract: the tag keyword and scope words match
        // case-insensitively; category tokens are preserved verbatim (case matters for later matching).
        [Fact]
        public void TagKeywordIsCaseInsensitive()
        {
            var t = Tags.Parse("[shepherd:Ore]");
            Assert.True(t.IsDestination);
            Assert.Equal(new[] { "Ore" }, t.Categories);
        }

        [Fact]
        public void ScopeKeywordsAreCaseInsensitive()
        {
            Assert.True(Tags.Parse("[Shepherd-off]").IsOff);
            Assert.True(Tags.Parse("[SHEPHERD-SPLIT]").IsSplit);
            Assert.True(Tags.Parse("[shepherd]").IsStatusLcd);
        }

        [Fact]
        public void CategoryTokensPreserveCase()
        {
            var t = Tags.Parse("[SHEPHERD:ore,INGOT]");
            Assert.Equal(new[] { "ore", "INGOT" }, t.Categories);
        }

        [Fact]
        public void PrefixCollisionIsNotATag()
        {
            Assert.False(Tags.Parse("[SHEPHERDX]").HasTag);
            Assert.False(Tags.Parse("[SHEPHERDING:Ore]").HasTag);
        }

        [Fact]
        public void UnknownScopeIsNotATag()
        {
            var t = Tags.Parse("[SHEPHERD-Foo]");
            Assert.False(t.HasTag);
            Assert.False(t.IsOff);
            Assert.False(t.IsSplit);
        }

        [Fact]
        public void DestinationAndScopeCoexist()
        {
            var t = Tags.Parse("[SHEPHERD:Ore] [SHEPHERD-Off]");
            Assert.True(t.IsDestination);
            Assert.Equal(new[] { "Ore" }, t.Categories);
            Assert.True(t.IsOff);
        }

        [Fact]
        public void MultipleDestinationTagsAccumulate()
        {
            var t = Tags.Parse("[SHEPHERD:Ore][SHEPHERD:Ingot]");
            Assert.Equal(new[] { "Ore", "Ingot" }, t.Categories);
        }

        [Fact]
        public void NullAndEmptyAreSafe()
        {
            Assert.False(Tags.Parse(null).HasTag);
            Assert.False(Tags.Parse("").HasTag);
        }

        [Fact]
        public void UnclosedBracketIsIgnored()
        {
            var t = Tags.Parse("[SHEPHERD:Ore");
            Assert.False(t.HasTag);
        }

        // Container-wide priority: an optional trailing :#N, detached from the last
        // category because tokens never contain ':'; default 0, negatives allowed.
        [Fact]
        public void PriorityDefaultsToZero()
        {
            Assert.Equal(0, Tags.Parse("[SHEPHERD:Ore]").Priority);
            Assert.Equal(0, Tags.Parse("[SHEPHERD]").Priority);
        }

        [Fact]
        public void TrailingPriorityParsed()
        {
            var t = Tags.Parse("[SHEPHERD:Ore,Ingot:#5]");
            Assert.Equal(new[] { "Ore", "Ingot" }, t.Categories);
            Assert.Equal(5, t.Priority);
        }

        [Fact]
        public void NegativePriorityParsed()
        {
            var t = Tags.Parse("[SHEPHERD:Ore:#-3]");
            Assert.Equal(new[] { "Ore" }, t.Categories);
            Assert.Equal(-3, t.Priority);
        }

        [Fact]
        public void CatchAllWithPriority()
        {
            var t = Tags.Parse("[SHEPHERD:*:#5]");
            Assert.True(t.IsCatchAll);
            Assert.Empty(t.Categories);
            Assert.Equal(5, t.Priority);
        }

        [Fact]
        public void MalformedPriorityStaysCategoryDefaultZero()
        {
            // A trailing segment that isn't #<int> is not a priority — the whole payload stays categories.
            var t = Tags.Parse("[SHEPHERD:Ore:#abc]");
            Assert.Equal(new[] { "Ore:#abc" }, t.Categories);
            Assert.Equal(0, t.Priority);
        }

        [Fact]
        public void BarePriorityMarkerIsNotAPriority()
        {
            var t = Tags.Parse("[SHEPHERD:Ore:#]");
            Assert.Equal(new[] { "Ore:#" }, t.Categories);
            Assert.Equal(0, t.Priority);
        }

        // Reserve pool tag: [SHEPHERD-RESERVE] = default pool, [SHEPHERD-RESERVE:Name] =
        // named pool. Keyword case-insensitive; pool name preserved verbatim (must match the Custom Data section).
        [Fact]
        public void BareReserveIsDefaultPool()
        {
            var t = Tags.Parse("Cargo [SHEPHERD-RESERVE]");
            Assert.True(t.HasTag);
            Assert.True(t.IsReserve);
            Assert.Equal("", t.ReservePool);
            Assert.False(t.IsDestination);
        }

        [Fact]
        public void NamedReservePreservesPoolVerbatim()
        {
            var t = Tags.Parse("Cargo [SHEPHERD-RESERVE:Ammo]");
            Assert.True(t.IsReserve);
            Assert.Equal("Ammo", t.ReservePool);
        }

        [Fact]
        public void ReserveKeywordIsCaseInsensitive()
        {
            Assert.True(Tags.Parse("[shepherd-reserve]").IsReserve);
            Assert.True(Tags.Parse("[SHEPHERD-Reserve:Ammo]").IsReserve);
        }

        [Fact]
        public void ReservePoolNamePreservesCaseAndSpacing()
        {
            var t = Tags.Parse("[SHEPHERD-RESERVE: oDD PoolName ]");
            Assert.True(t.IsReserve);
            Assert.Equal(" oDD PoolName ", t.ReservePool);
        }

        [Fact]
        public void PlainDestinationIsNotAReserve()
        {
            var t = Tags.Parse("[SHEPHERD:Ores]");
            Assert.False(t.IsReserve);
            Assert.Null(t.ReservePool);
        }

        [Fact]
        public void ReservePrefixCollisionIsNotAReserve()
        {
            Assert.False(Tags.Parse("[SHEPHERD-RESERVED]").HasTag);
            Assert.False(Tags.Parse("[SHEPHERD-RESERVEX]").IsReserve);
        }

        [Fact]
        public void ReserveAndOffCoexist()
        {
            var t = Tags.Parse("[SHEPHERD-RESERVE:Ammo] [SHEPHERD-Off]");
            Assert.True(t.IsReserve);
            Assert.Equal("Ammo", t.ReservePool);
            Assert.True(t.IsOff);
        }
    }
}
