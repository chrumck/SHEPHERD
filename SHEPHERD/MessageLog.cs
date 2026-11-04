using System;
using System.Collections.Generic;
using System.Text;

namespace IngameScript
{
    // The panel shows a state for as long as it holds; the log gets only its entry edge, again if it returns. One
    // channel per recompute cadence: Commit prunes whatever its own set no longer asserts, so mixed cadences would
    // prune each other. A channel whose Current isn't handed to the renderer stays log-only.
    public class StateChannel
    {
        readonly Action<string> log;
        readonly List<string> current = new List<string>();
        readonly List<string> active = new List<string>();

        public StateChannel(Action<string> log) { this.log = log; }

        public List<string> Current => current;

        public void Begin() => current.Empty();
        // Null tolerated so a caller can pass an optional status straight through.
        public void Push(string message) { if (message != null) current.Push(message); }

        public void Commit()
        {
            foreach (var message in current)
                if (!active.Contains(message)) { active.Push(message); log(message); }
            for (var i = active.Count - 1; i >= 0; i--)
                if (!current.Contains(active[i])) active.RemoveAt(i);
        }
    }

    // Dirty lets the renderer rewrite the surface only when the content changed.
    public partial class MessageLog
    {
        readonly List<string> lines = new List<string>();
        readonly int capacity;

        public MessageLog(int capacity) { this.capacity = capacity > 0 ? capacity : 1; }

        public bool Dirty;
        public void Push(string line)
        {
            lines.Push(line ?? "");
            if (lines.Count > capacity) lines.RemoveAt(0);
            Dirty = true;
        }

        // Newest first, so the latest message stays visible at the top of a fixed-height panel.
        public void Render(StringBuilder sb)
        {
            sb.Empty();
            for (var i = lines.Count - 1; i >= 0; i--) sb.Line(lines[i]);
        }

        public void ClearDirty() => Dirty = false;
    }
}
