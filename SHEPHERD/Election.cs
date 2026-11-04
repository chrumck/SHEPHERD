using System.Collections.Generic;

namespace IngameScript
{
    // The order is total (rank, then the lower address), so each PB reaches the same leader on its own from the
    // heartbeats it hears, with no negotiation. Times are game seconds, so sim lag slows every PB's clock alike and a
    // paused game expires nobody.
    public class PeerElection
    {
        public const double LeadHeartbeatSeconds = 2.0;
        // About three beats even at the parked cadence, whose slow wakes stretch the interval, so a peer must miss
        // more than one wake-rounded beat before it is given up on.
        public const double LeadStaleSeconds = 10.0;

        class Peer
        {
            public long Rank;
            public string Label;
            public double Heard;
            public bool OnOurConstruct;
        }

        readonly Dictionary<long, Peer> peers = new Dictionary<long, Peer>();
        readonly List<long> stale = new List<long>();
        double lastSent = -LeadHeartbeatSeconds;

        // Such a leader already draws the panels we would.
        public bool LeaderOnOurConstruct;

        // Between equal priorities a station outranks a ship and a large grid a small one, picking the base over a
        // docked ship with no configuration. A halted PB does no work, so it drops below every healthy one
        // (priority × 4 stays within ±2^34); alone, it still leads.
        public static long Rank(int priority, bool isStatic, bool isLarge, bool halted = false) =>
            priority * 4L + (isStatic ? 2 : 0) + (isLarge ? 1 : 0) - (halted ? 1L << 40 : 0);

        public static bool Outranks(long rank, long address, long otherRank, long otherAddress) =>
            rank > otherRank || rank == otherRank && address < otherAddress;

        // True when the peer is new to us — never heard, or heard again after going stale.
        public bool Hear(long address, long rank, string label, double now, bool onOurConstruct)
        {
            Peer p;
            var fresh = !peers.Get(address, out p) || now - p.Heard > LeadStaleSeconds;
            if (p == null) peers[address] = p = new Peer();
            p.Rank = rank;
            p.Label = label;
            p.Heard = now;
            p.OnOurConstruct = onOurConstruct;
            return fresh;
        }

        // The label of the best live peer that outranks us, or null when we lead.
        public string Leader(long self, long selfRank, double now)
        {
            stale.Empty();
            string leader = null;
            LeaderOnOurConstruct = false;
            var bestRank = selfRank;
            var bestAddress = self;
            foreach (var pair in peers)
            {
                var p = pair.Value;
                if (now - p.Heard > LeadStaleSeconds) { stale.Push(pair.Key); continue; }
                if (!Outranks(p.Rank, pair.Key, bestRank, bestAddress)) continue;
                leader = p.Label;
                LeaderOnOurConstruct = p.OnOurConstruct;
                bestRank = p.Rank;
                bestAddress = pair.Key;
            }
            foreach (var address in stale) peers.Remove(address);
            return leader;
        }

        public bool TakeHeartbeat(double now)
        {
            if (now - lastSent < LeadHeartbeatSeconds) return false;
            lastSent = now;
            return true;
        }
    }
}
