using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class AssemblerMarksTests
    {
        sealed class Assembler : IMarkedAssembler
        {
            public readonly List<bool> Writes = new List<bool>();

            public Assembler(string name, AssemblerMark mark, int construct = 0, bool canCooperate = true, bool cooperative = false)
            {
                Name = name;
                Mark = mark;
                Construct = construct;
                CanCooperate = canCooperate;
                Cooperative = cooperative;
            }

            static long lastId;
            public long Id { get; } = System.Threading.Interlocked.Increment(ref lastId);
            public string Name { get; private set; }
            public bool IsOperational { get; set; } = true;
            public bool IsReceiver { get; set; }
            public int Construct { get; private set; }
            public AssemblerMark Mark { get; private set; }
            public bool CanCooperate { get; private set; }
            public bool Cooperative { get; private set; }

            public void SetCooperative(bool on, bool dry)
            {
                if (dry) return;
                Writes.Add(on);
                Cooperative = on;
            }
        }

        static MarkCheck Check(params Assembler[] assemblers)
        {
            var check = new MarkCheck();
            check.Check(new List<Assembler>(assemblers));
            return check;
        }

        [Fact]
        public void Check_OneMainWithCoopsIsValid()
        {
            var main = new Assembler("M", AssemblerMark.Main);
            var coop = new Assembler("C", AssemblerMark.Coop);
            var check = Check(main, coop, new Assembler("C2", AssemblerMark.Coop));
            Assert.True(check.IsValid(0));
            Assert.True(main.IsReceiver);
            Assert.False(coop.IsReceiver);
            Assert.Empty(check.Problems);
        }

        [Fact]
        public void Check_NoMarksIsValidWithNoReceiver()
        {
            var unmarked = new Assembler("U", AssemblerMark.None);
            var check = Check(unmarked, new Assembler("U2", AssemblerMark.None));
            Assert.True(check.IsValid(0));
            Assert.False(unmarked.IsReceiver);
            Assert.Empty(check.Problems);
        }

        [Fact]
        public void Check_CoopWithoutMainIsReported()
        {
            var check = Check(new Assembler("C1", AssemblerMark.Coop), new Assembler("C2", AssemblerMark.Coop));
            Assert.False(check.IsValid(0));
            Assert.Equal(new[] { "assemblers: no Main among Coop C1, C2" }, check.Problems);
        }

        [Fact]
        public void Check_ListsAtMostThreeNames()
        {
            var check = Check(new Assembler("M1", AssemblerMark.Main), new Assembler("M2", AssemblerMark.Main),
                new Assembler("M3", AssemblerMark.Main), new Assembler("M4", AssemblerMark.Main));
            Assert.Equal(new[] { "assemblers: several Main - M1, M2, M3…" }, check.Problems);
        }

        [Fact]
        public void Check_SeveralMainHoldsTheConstruct()
        {
            var main = new Assembler("M1", AssemblerMark.Main);
            var check = Check(main, new Assembler("M2", AssemblerMark.Main));
            Assert.False(check.IsValid(0));
            Assert.False(main.IsReceiver);
            Assert.Equal(new[] { "assemblers: several Main - M1, M2" }, check.Problems);
        }

        [Fact]
        public void Check_CoopThatCannotCooperateIsAReceiverAndNotAMain()
        {
            var main = new Assembler("M", AssemblerMark.Main);
            var kit = new Assembler("Kit", AssemblerMark.Coop, canCooperate: false);
            var check = Check(main, kit);
            Assert.True(check.IsValid(0));
            Assert.True(kit.IsReceiver);
            Assert.Empty(check.Problems);
        }

        [Fact]
        public void Check_CoopThatCannotCooperateAloneIsValid()
        {
            var kit = new Assembler("Kit", AssemblerMark.Coop, canCooperate: false);
            var check = Check(kit, new Assembler("C", AssemblerMark.Coop, canCooperate: false));
            Assert.True(check.IsValid(0));
            Assert.True(kit.IsReceiver);
        }

        [Fact]
        public void Check_OffMainStillCountsAsMain()
        {
            // Off-ness is not part of the mark: the check never reads it, so an off Main still blocks a second one.
            var check = Check(new Assembler("Off", AssemblerMark.Main), new Assembler("On", AssemblerMark.Main));
            Assert.False(check.IsValid(0));
        }

        [Fact]
        public void Check_ConstructsAreCheckedApart()
        {
            var docked = new Assembler("D", AssemblerMark.Main, construct: 1, cooperative: false);
            var check = Check(new Assembler("C", AssemblerMark.Coop), docked);
            Assert.False(check.IsValid(0));
            Assert.True(check.IsValid(1));
            Assert.True(docked.IsReceiver);
            Assert.True(check.IsValid(5));
        }

        [Fact]
        public void Check_SetsTheHomeSwitchToMatchOnce()
        {
            var main = new Assembler("M", AssemblerMark.Main, cooperative: true);
            var coop = new Assembler("C", AssemblerMark.Coop, cooperative: false);
            var matched = new Assembler("C2", AssemblerMark.Coop, cooperative: true);
            var check = new MarkCheck();
            var list = new List<Assembler> { main, coop, matched };
            check.Check(list);
            check.Check(list);
            Assert.Equal(new[] { false }, main.Writes);
            Assert.Equal(new[] { true }, coop.Writes);
            Assert.Empty(matched.Writes);
            Assert.Empty(check.Problems);
        }

        [Fact]
        public void Check_LeavesTheSwitchOfUnmarkedAndNonCooperatingModels()
        {
            var unmarked = new Assembler("U", AssemblerMark.None, cooperative: true);
            var kit = new Assembler("Kit", AssemblerMark.Coop, canCooperate: false);
            Check(new Assembler("M", AssemblerMark.Main), unmarked, kit);
            Assert.Empty(unmarked.Writes);
            Assert.Empty(kit.Writes);
        }

        [Fact]
        public void Check_LeavesTheSwitchOnAMisconfiguredHome()
        {
            var main = new Assembler("M1", AssemblerMark.Main, cooperative: true);
            Check(main, new Assembler("M2", AssemblerMark.Main));
            Assert.Empty(main.Writes);
        }

        [Fact]
        public void Check_ReportsADockedMismatchAndHoldsThatConstruct()
        {
            var main = new Assembler("DM", AssemblerMark.Main, construct: 1, cooperative: true);
            var coop = new Assembler("DC", AssemblerMark.Coop, construct: 1, cooperative: false);
            var home = new Assembler("M", AssemblerMark.Main);
            var check = Check(home, main, coop);
            Assert.True(check.IsValid(0));
            Assert.False(check.IsValid(1));
            Assert.False(main.IsReceiver);
            Assert.Empty(main.Writes);
            Assert.Empty(coop.Writes);
            Assert.Equal(new[] { "DM: cooperative switch doesn't match Main", "DC: cooperative switch doesn't match Coop" },
                check.Problems);
        }

        [Fact]
        public void Check_ADockedCoopWithoutMainIsReportedBesideItsMismatch()
        {
            var check = Check(new Assembler("DC", AssemblerMark.Coop, construct: 1, cooperative: false));
            Assert.Equal(new[] { "DC: cooperative switch doesn't match Coop", "assemblers: no Main among Coop DC" },
                check.Problems);
        }

        [Fact]
        public void Check_StartsFreshEachTime()
        {
            var check = new MarkCheck();
            check.Check(new List<Assembler> { new Assembler("M1", AssemblerMark.Main), new Assembler("M2", AssemblerMark.Main) });
            check.Check(new List<Assembler> { new Assembler("M1", AssemblerMark.Main) });
            Assert.True(check.IsValid(0));
            Assert.Empty(check.Problems);
        }

        [Fact]
        public void Check_LeavesTheDisassemblingCoopsSwitchAlone()
        {
            var main = new Assembler("M", AssemblerMark.Main);
            var coop = new Assembler("C", AssemblerMark.Coop, cooperative: false);
            var check = new MarkCheck();
            check.Check(new List<Assembler> { main, coop }, coop.Id);
            Assert.Empty(coop.Writes);
            check.Check(new List<Assembler> { main, coop });
            Assert.Equal(new[] { true }, coop.Writes);
        }

        [Fact]
        public void Check_CountsTaggedAssemblersOnValidConstructsOnly()
        {
            var check = Check(new Assembler("M", AssemblerMark.Main), new Assembler("C", AssemblerMark.Coop),
                new Assembler("U", AssemblerMark.None), new Assembler("Off", AssemblerMark.Coop) { IsOperational = false },
                new Assembler("D1", AssemblerMark.Main, construct: 1), new Assembler("D2", AssemblerMark.Main, construct: 1));
            Assert.Equal(2, check.Controlled);
        }
    }
}
