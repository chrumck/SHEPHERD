using System.Collections.Generic;
using System.Text;

namespace IngameScript
{
    // The assembler tags [SHEPHERD-Main] and [SHEPHERD-Coop]. None = untagged: SHEPHERD never queues on it.
    public enum AssemblerMark { None, Main, Coop }

    // What the mark check reads per assembler. Construct is the managed-scope index (0 = home, >0 = docked).
    // CanCooperate = the model has the game's cooperative switch. IsReceiver is the check's verdict: whether
    // SHEPHERD may queue on this assembler.
    public interface IMarkedAssembler
    {
        long Id { get; }
        bool IsOperational { get; }
        bool IsReceiver { get; set; }
        string Name { get; }
        int Construct { get; }
        AssemblerMark Mark { get; }
        bool CanCooperate { get; }
        bool Cooperative { get; }
        void SetCooperative(bool on, bool dry);
    }

    // Checks the marks per construct and decides which assemblers take orders. A construct is valid with exactly one
    // Main (any number of Coop), or with neither; a Coop without a Main or several Mains is the player's to fix, so
    // the construct gets no new work until then. A Coop whose model can't cooperate would sit idle, so it takes
    // orders like a Main and doesn't count as one. The cooperative switch is set to match the mark only at home: a
    // docked block's settings leave with its pilot, so a docked mismatch is reported instead and holds the construct.
    // The disassembling Coop is left as the craft planner set it: its switch is off while it drains.
    public class MarkCheck
    {
        const int ListedNames = 3;
        readonly List<int> mains = new List<int>();
        readonly List<int> coops = new List<int>();
        readonly List<int> mismatches = new List<int>();
        readonly List<string> problems = new List<string>();
        readonly StringBuilder sb = new StringBuilder();

        public List<string> Problems => problems;
        // Tagged assemblers on valid constructs that are on and intact: the ones SHEPHERD queues on or that help them.
        public int Controlled;

        public bool IsValid(int construct) =>
            construct >= mains.Count || mismatches[construct] == 0
            && (mains[construct] == 1 || mains[construct] == 0 && coops[construct] == 0);

        public static bool TakesOrders(IMarkedAssembler a) =>
            a.Mark == AssemblerMark.Main || a.Mark == AssemblerMark.Coop && !a.CanCooperate;

        public void Check<T>(List<T> assemblers, long disassembler = 0) where T : IMarkedAssembler
        {
            mains.Empty();
            coops.Empty();
            mismatches.Empty();
            problems.Empty();
            foreach (var a in assemblers)
            {
                var c = a.Construct;
                while (mains.Count <= c) { mains.Push(0); coops.Push(0); mismatches.Push(0); }
                var main = a.Mark == AssemblerMark.Main;
                if (main) mains[c]++;
                else if (a.Mark == AssemblerMark.Coop && a.CanCooperate) coops[c]++;
                if (c > 0 && Mismatched(a))
                {
                    mismatches[c]++;
                    problems.Push(a.Name + ": cooperative switch doesn't match " + (main ? "Main" : "Coop"));
                }
            }
            for (var c = 0; c < mains.Count; c++)
            {
                if (mains[c] > 1) problems.Push(Names(assemblers, c, AssemblerMark.Main, "assemblers: several Main - "));
                else if (mains[c] == 0 && coops[c] > 0) problems.Push(Names(assemblers, c, AssemblerMark.Coop, "assemblers: no Main among Coop "));
            }
            Controlled = 0;
            foreach (var a in assemblers)
            {
                if (a.Mark != AssemblerMark.None && a.IsOperational && IsValid(a.Construct)) Controlled++;
                a.IsReceiver = IsValid(a.Construct) && TakesOrders(a);
                if (a.Construct == 0 && IsValid(0) && a.Id != disassembler && Mismatched(a)) a.SetCooperative(a.Mark == AssemblerMark.Coop, false);
            }
        }

        static bool Mismatched(IMarkedAssembler a) =>
            a.CanCooperate && (a.Mark == AssemblerMark.Main ? a.Cooperative : a.Mark == AssemblerMark.Coop && !a.Cooperative);

        string Names<T>(List<T> assemblers, int construct, AssemblerMark mark, string head) where T : IMarkedAssembler
        {
            sb.Empty().Put(head);
            var n = 0;
            foreach (var a in assemblers)
            {
                if (a.Construct != construct || a.Mark != mark || mark == AssemblerMark.Coop && !a.CanCooperate) continue;
                if (n == ListedNames) { sb.Put('…'); break; }
                sb.Put(n++ > 0 ? ", " : "").Put(a.Name);
            }
            return sb.ToString();
        }
    }
}
