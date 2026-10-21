using System;
using System.Collections.Generic;
using System.Text;

namespace IngameScript
{
    // The set of states currently asserted by one recompute event, plus entry-edge logging. Begin/Add rebuild the
    // set; Commit emits to the log sink only the newly asserted entries and forgets any no longer asserted (so a
    // returning state emits again). `Current` is that live set — the status panel renders it, so a state shows for
    // exactly as long as it holds while the log keeps only its edges. One instance per recompute cadence: Commit
    // prunes whatever its own set no longer asserts, so mixing cadences in one channel would prune each other.
    // Several channels may share a cadence (they prune independently); a channel is status-visible only if its
    // Current is handed to the renderer, which is how a log-only diagnostic stays log-only.
    public sealed class StateChannel
    {
        readonly Action<string> log;
        readonly List<string> current = new List<string>();
        readonly List<string> active = new List<string>();

        public StateChannel(Action<string> log) { this.log = log; }

        public List<string> Current => current;

        public void Begin() => current.Clear();
        // Null tolerated so a caller can pass an optional status straight through.
        public void Add(string message) { if (message != null) current.Add(message); }

        public void Commit()
        {
            foreach (var message in current)
                if (!active.Contains(message)) { active.Add(message); log(message); }
            for (var i = active.Count - 1; i >= 0; i--)
                if (!current.Contains(active[i])) active.RemoveAt(i);
        }
    }

    // A bounded rolling buffer of the most recent diagnostic lines behind the [SHEPHERD-LOG] surface. Add drops
    // the oldest line past capacity. The Dirty flag lets the renderer rewrite the surface only when the content
    // changed, not every tick.
    public sealed partial class MessageLog
    {
        readonly List<string> lines = new List<string>();
        readonly int capacity;

        public MessageLog(int capacity) { this.capacity = capacity > 0 ? capacity : 1; }

        public bool Dirty { get; private set; }
        public void Add(string line)
        {
            lines.Add(line ?? "");
            if (lines.Count > capacity) lines.RemoveAt(0);
            Dirty = true;
        }

        // Newest first, so the latest message stays visible at the top of a fixed-height panel.
        public void Render(StringBuilder sb)
        {
            sb.Clear();
            for (var i = lines.Count - 1; i >= 0; i--) sb.AppendLine(lines[i]);
        }

        public void ClearDirty() => Dirty = false;
    }
}
