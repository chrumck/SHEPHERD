using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class ElectionTests
    {
        const long Self = 100;
        static readonly long ShipRank = PeerElection.Rank(0, false, true);

        [Fact]
        public void AlonePbLeads()
        {
            var e = new PeerElection();
            Assert.Null(e.Leader(Self, ShipRank, 0.0));
        }

        [Fact]
        public void HigherRankedPeerLeads()
        {
            var e = new PeerElection();
            e.Hear(200, PeerElection.Rank(1, false, true), "Base PB on Base", 0.0, false);
            Assert.Equal("Base PB on Base", e.Leader(Self, ShipRank, 0.0));
        }

        [Fact]
        public void LowerRankedPeerDoesNotLead()
        {
            var e = new PeerElection();
            e.Hear(200, PeerElection.Rank(0, false, false), "Fighter PB", 0.0, false);
            Assert.Null(e.Leader(Self, ShipRank, 0.0));
        }

        [Fact]
        public void EqualRankGoesToTheLowerAddress()
        {
            var low = new PeerElection();
            low.Hear(200, ShipRank, "high", 0.0, false);
            Assert.Null(low.Leader(Self, ShipRank, 0.0));

            var high = new PeerElection();
            high.Hear(Self, ShipRank, "low", 0.0, false);
            Assert.Equal("low", high.Leader(200, ShipRank, 0.0));
        }

        [Fact]
        public void StalePeerIsForgottenAndReturnsAsNew()
        {
            var e = new PeerElection();
            var rank = PeerElection.Rank(5, true, true);
            Assert.True(e.Hear(200, rank, "leader", 0.0, false));
            Assert.False(e.Hear(200, rank, "leader", 2.0, false));

            Assert.Equal("leader", e.Leader(Self, ShipRank, 2.0 + PeerElection.LeadStaleSeconds));
            Assert.Null(e.Leader(Self, ShipRank, 2.1 + PeerElection.LeadStaleSeconds));

            Assert.True(e.Hear(200, rank, "leader", 20.0, false));
            Assert.Equal("leader", e.Leader(Self, ShipRank, 20.0));
        }

        [Fact]
        public void HearReportsAStaleButUnprunedPeerAsNew()
        {
            var e = new PeerElection();
            e.Hear(200, ShipRank, "peer", 0.0, false);
            Assert.True(e.Hear(200, ShipRank, "peer", PeerElection.LeadStaleSeconds + 1.0, false));
        }

        [Fact]
        public void LeaderThatDropsItsRankHandsBack()
        {
            var e = new PeerElection();
            e.Hear(200, PeerElection.Rank(1, false, true), "peer", 0.0, false);
            Assert.NotNull(e.Leader(Self, ShipRank, 0.0));

            e.Hear(200, PeerElection.Rank(-1, false, true), "peer", 2.0, false);
            Assert.Null(e.Leader(Self, ShipRank, 2.0));
        }

        [Fact]
        public void OnlyTheTopOfThreeLeads()
        {
            var top = PeerElection.Rank(0, true, true);
            var mid = PeerElection.Rank(0, false, true);
            var low = PeerElection.Rank(0, false, false);

            var a = new PeerElection();
            a.Hear(2, mid, "B", 0.0, false);
            a.Hear(3, low, "C", 0.0, false);
            Assert.Null(a.Leader(1, top, 0.0));

            var b = new PeerElection();
            b.Hear(1, top, "A", 0.0, false);
            b.Hear(3, low, "C", 0.0, false);
            Assert.Equal("A", b.Leader(2, mid, 0.0));

            var c = new PeerElection();
            c.Hear(2, mid, "B", 0.0, false);
            c.Hear(1, top, "A", 0.0, false);
            Assert.Equal("A", c.Leader(3, low, 0.0));
        }

        [Fact]
        public void PriorityOutweighsGridKind()
        {
            Assert.True(PeerElection.Rank(1, false, false) > PeerElection.Rank(0, true, true));
            Assert.True(PeerElection.Rank(-1, true, true) < PeerElection.Rank(0, false, false));
            Assert.True(PeerElection.Rank(0, true, false) > PeerElection.Rank(0, false, true));
            Assert.True(PeerElection.Rank(0, false, true) > PeerElection.Rank(0, false, false));
        }

        // The real script: a base PB leads over a lower-priority backup on the same grid until its crash breaker
        // opens; then it advertises a halted rank and stands by, so the backup takes over the work.
        [Fact]
        public void AHaltedLeaderHandsOverToAHealthyPeer()
        {
            var station = ProgramHarness.Grid("Base", true, true);
            var h = new ProgramHarness(station, "Base PB", "");
            h.AddPeer("Backup PB", station, PeerElection.Rank(-1, true, true), 2002);
            h.TickUntil(() => h.Echo.Contains(" running"));
            h.Tick(300);
            Assert.Contains(" running", h.Echo);
            Assert.Equal(PeerElection.Rank(0, true, true), h.LastHeartbeat.Item1);

            h.FailFetch = true;
            h.TickUntil(() => h.Echo.Contains("standing by"));
            Assert.Contains("Backup PB on Base leads", h.Echo);
            Assert.Equal(PeerElection.Rank(0, true, true, true), h.LastHeartbeat.Item1);
        }

        // A docked ship's PB, outranked by the station's, runs one plan while warming up — the plan's slow first pass
        // belongs inside the limiter's startup allowance, not at the undock that makes it lead — then stands by.
        [Fact]
        public void AnOutrankedPbPlansOnceWhileWarmingThenStandsBy()
        {
            var ship = ProgramHarness.Grid("Ship", false, false);
            var station = ProgramHarness.Grid("Base", true, true);
            var h = new ProgramHarness(ship, "Ship PB", "[Reserve:Spare]\nComponent/SteelPlate=100MA\n");
            var log = h.AddPanel("Log [SHEPHERD-LOG]");
            h.Dock(ship, station);
            h.AddPeer("Base PB", station, PeerElection.Rank(0, true, true), 2002);
            h.TickUntil(() => h.Echo.Contains("standing by"));
            h.TickUntil(() => log.Count > 0 && log[log.Count - 1].Contains("craft Component/SteelPlate"));
            h.Tick(300);
            Assert.Contains("standing by", h.Echo);
        }

        [Fact]
        public void AHaltedBlockRanksBelowEveryHealthyOne()
        {
            Assert.True(PeerElection.Rank(int.MaxValue, true, true, true) < PeerElection.Rank(int.MinValue, false, false));
            // Among halted blocks the usual order still holds, so the pick stays the same on every PB.
            Assert.True(PeerElection.Rank(1, false, false, true) > PeerElection.Rank(0, true, true, true));
        }

        [Fact]
        public void TellsWhetherTheLeaderSharesOurConstruct()
        {
            var e = new PeerElection();
            e.Hear(200, PeerElection.Rank(0, true, true), "base", 0.0, false);
            e.Hear(300, PeerElection.Rank(0, false, true), "twin", 0.0, true);
            Assert.Equal("base", e.Leader(Self, PeerElection.Rank(0, false, false), 1.0));
            Assert.False(e.LeaderOnOurConstruct);   // a docked base draws only its own panels

            var f = new PeerElection();
            f.Hear(300, PeerElection.Rank(0, false, true), "twin", 0.0, true);
            Assert.Equal("twin", f.Leader(Self, PeerElection.Rank(0, false, false), 1.0));
            Assert.True(f.LeaderOnOurConstruct);

            Assert.Null(f.Leader(Self, PeerElection.Rank(0, false, false), 1.0 + PeerElection.LeadStaleSeconds + 1.0));
            Assert.False(f.LeaderOnOurConstruct);
        }

        [Fact]
        public void HeartbeatIsDueAtOnceThenEveryInterval()
        {
            var e = new PeerElection();
            Assert.True(e.TakeHeartbeat(0.0));
            Assert.False(e.TakeHeartbeat(0.0));
            Assert.False(e.TakeHeartbeat(PeerElection.LeadHeartbeatSeconds - 0.1));
            Assert.True(e.TakeHeartbeat(PeerElection.LeadHeartbeatSeconds));
            Assert.False(e.TakeHeartbeat(PeerElection.LeadHeartbeatSeconds + 1.0));
        }
    }
}
