using System.Collections.Generic;

namespace IngameScript
{
    // Decides whether a live peer PB outranks this one. Every PB advertises its rank on a heartbeat and remembers
    // what it hears; because the order is total (rank, then the lower address), each PB reaches the same answer on
    // its own — the top-ranked live PB works and every other one stands by — with no negotiation and no PB having
    // to know the whole group. Times are game seconds, so sim lag slows every PB's clock alike and a paused game
    // expires nobody.
    public sealed class PeerElection
    {
        public const double LeadHeartbeatSeconds = 2.0;
        // Several heartbeats, and still about three at the parked cadence, whose ~1.67 s wakes stretch the heartbeat
        // interval: a peer must miss more than one wake-rounded beat before it is given up on.
        public const double LeadStaleSeconds = 10.0;

        sealed class Peer
        {
            public long Rank;
            public string Label;
            public double Heard;
            public bool OnOurConstruct;
        }

        readonly Dictionary<long, Peer> peers = new Dictionary<long, Peer>();
        readonly List<long> stale = new List<long>();
        double lastSent = -LeadHeartbeatSeconds;

        // Whether the current leader sits on our own construct, so it already draws the panels we would.
        public bool LeaderOnOurConstruct { get; private set; }

        // Priority dominates; between equal priorities a station outranks a ship and a large grid a small one, which
        // picks the base over a docked ship without any configuration.
        public static long Rank(int priority, bool isStatic, bool isLarge) =>
            priority * 4L + (isStatic ? 2 : 0) + (isLarge ? 1 : 0);

        public static bool Outranks(long rank, long address, long otherRank, long otherAddress) =>
            rank > otherRank || rank == otherRank && address < otherAddress;

        // True when the peer is new to us — never heard, or heard again after going stale.
        public bool Hear(long address, long rank, string label, double now, bool onOurConstruct)
        {
            Peer p;
            var fresh = !peers.TryGetValue(address, out p) || now - p.Heard > LeadStaleSeconds;
            if (p == null) peers[address] = p = new Peer();
            p.Rank = rank;
            p.Label = label;
            p.Heard = now;
            p.OnOurConstruct = onOurConstruct;
            return fresh;
        }

        // The label of the best live peer that outranks us, or null when we lead. Forgets stale peers first.
        public string Leader(long self, long selfRank, double now)
        {
            stale.Clear();
            string leader = null;
            LeaderOnOurConstruct = false;
            var bestRank = selfRank;
            var bestAddress = self;
            foreach (var pair in peers)
            {
                var p = pair.Value;
                if (now - p.Heard > LeadStaleSeconds) { stale.Add(pair.Key); continue; }
                if (!Outranks(p.Rank, pair.Key, bestRank, bestAddress)) continue;
                leader = p.Label;
                LeaderOnOurConstruct = p.OnOurConstruct;
                bestRank = p.Rank;
                bestAddress = pair.Key;
            }
            foreach (var address in stale) peers.Remove(address);
            return leader;
        }

        // True when a heartbeat is due, which it then counts as sent.
        public bool TakeHeartbeat(double now)
        {
            if (now - lastSent < LeadHeartbeatSeconds) return false;
            lastSent = now;
            return true;
        }
    }
}
