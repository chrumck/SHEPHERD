using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class YieldDiscoveryTests
    {
        sealed class Feed : IRefineryFeed
        {
            readonly string ore;

            public Feed(long id, string accepted)
            {
                Id = id;
                ore = accepted;
            }

            public long Id { get; private set; }
            public string Name => "R" + Id;
            public bool IsConstrained => Request != null;
            public FeedRequest Request { get; private set; }
            public bool CanTake(string key) => key == ore;
            public void Constrain(FeedRequest request) => Request = request;
        }

        const string Ore = "Ore/Modium";
        const string Ingot = "Ingot/Modium";

        [Fact]
        public void SelectionClosesOnlyACompatibleFreeRefinery()
        {
            var wrong = new Feed(1, "Ore/Other");
            var right = new Feed(2, Ore);
            var sample = new YieldSample();
            Assert.True(sample.Begin(Ore, Ingot, new List<IRefineryFeed> { wrong, right }));
            Assert.Equal(2, sample.RefineryId);
            Assert.Null(wrong.Request);
            Assert.NotNull(right.Request);
            Assert.False(right.Request.Allows(Ore));
            Assert.True(sample.Drains);
        }

        [Fact]
        public void CleaningRequiresAnEmptyScanAndAProtectedRebuildBarrier()
        {
            var feed = new Feed(2, Ore);
            var sample = new YieldSample();
            sample.Begin(Ore, Ingot, new List<IRefineryFeed> { feed });
            Assert.Null(sample.Advance(true, 1, 1, new Dictionary<string, double>(), 110, 100, 10));
            Assert.Equal(YieldPhase.Draining, sample.Phase);
            Assert.Null(sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 20));
            Assert.Equal(YieldPhase.Ready, sample.Phase);
            Assert.True(sample.Holds);
            Assert.Null(sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 30));
            Assert.Equal(YieldPhase.Feeding, sample.Phase);
            sample.Bind(feed);
            Assert.Equal(110, feed.Request.Remaining);
        }

        [Fact]
        public void ConfirmedDeliveryAndConsumptionProduceMultiOutputRatios()
        {
            var feed = new Feed(2, Ore);
            var sample = new YieldSample();
            sample.Begin(Ore, Ingot, new List<IRefineryFeed> { feed });
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 40);
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 50);
            sample.Bind(feed);
            var claim = feed.Request.Claim(110);
            feed.Request.Complete(claim, 110);
            var outputs = new Dictionary<string, double>
            {
                { Ingot, 25 },
                { "Ingot/Side", 5 }
            };
            var learned = sample.Advance(true, 10, 10, outputs, 110, 100, 60);
            Assert.Equal(YieldPhase.Measuring, sample.Phase);
            Assert.Equal(2, learned.Count);
            Assert.Equal(0.25, learned[0].Ratio);
            Assert.Equal(0.05, learned[1].Ratio);
        }

        [Fact]
        public void FloorAndWorkingStateGateRecording()
        {
            var feed = new Feed(2, Ore);
            var sample = new YieldSample();
            sample.Begin(Ore, Ingot, new List<IRefineryFeed> { feed });
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 60);
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 70);
            sample.Bind(feed);
            var claim = feed.Request.Claim(110);
            feed.Request.Complete(claim, 110);
            var output = new Dictionary<string, double> { { Ingot, 20 } };
            Assert.Null(sample.Advance(true, 11, 11, output, 110, 100, 80));
            Assert.NotNull(sample.Advance(false, 10, 10, output, 110, 100, 90));
        }

        [Fact]
        public void ResetReleasesAllOperationState()
        {
            var sample = new YieldSample();
            sample.Begin(Ore, Ingot, new List<IRefineryFeed> { new Feed(2, Ore) });
            sample.Reset();
            Assert.False(sample.Active);
            Assert.Equal(0, sample.RefineryId);
            Assert.Null(sample.Ore);
        }

        // A sample in Feeding with `delivered` units already confirmed into the owned refinery — the state every
        // tamper check watches from.
        static YieldSample Fed(Feed feed, double delivered)
        {
            var sample = new YieldSample();
            sample.Begin(Ore, Ingot, new List<IRefineryFeed> { feed });
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 0);
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 10);
            sample.Bind(feed);
            var claim = feed.Request.Claim(delivered);
            feed.Request.Complete(claim, delivered);
            return sample;
        }

        static Dictionary<string, double> Out(double amount) => new Dictionary<string, double> { { Ingot, amount } };

        [Fact]
        public void InsertedOreAbortsTheSample()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            Assert.Null(sample.Advance(true, 110, 110, Out(0.5), 110, 100, 20));
            Assert.True(sample.Active);
            Assert.Null(sample.Advance(true, 120, 120, Out(0.5), 110, 100, 30));   // consumed fell -> foreign ore
            Assert.False(sample.Active);
            sample.Reset();
            Assert.False(sample.Active);
        }

        [Fact]
        public void RemovedOutputAbortsTheSample()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            Assert.Null(sample.Advance(true, 60, 60, Out(20), 110, 100, 20));
            Assert.Null(sample.Advance(true, 50, 50, Out(5), 110, 100, 30));       // output can only fall externally
            Assert.False(sample.Active);
        }

        [Fact]
        public void GrabBeyondTheEstablishedPaceAbortsTheSample()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            sample.Advance(true, 108, 108, Out(0.5), 110, 100, 20);                // baseline observation
            sample.Advance(true, 106, 106, Out(1.0), 110, 100, 30);                // pace 0.2/s
            Assert.True(sample.Active);
            sample.Advance(true, 104, 104, Out(1.5), 110, 100, 40);                // confirm the pace
            sample.Advance(true, 94, 94, Out(1.7), 110, 100, 50);                  // 1.0/s > 3x pace -> a grab
            Assert.False(sample.Active);
        }

        [Fact]
        public void PaceVariationWithinTheFactorNeverTrips()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            sample.Advance(true, 108, 108, Out(0.5), 110, 100, 20);
            sample.Advance(true, 106, 106, Out(1.0), 110, 100, 30);                // 0.2/s
            sample.Advance(true, 101, 101, Out(2.0), 110, 100, 40);                // 0.5/s (2.5x) -> new pace
            sample.Advance(true, 91, 91, Out(4.0), 110, 100, 50);                  // 1.0/s (2x new pace)
            Assert.True(sample.Active);
        }

        [Fact]
        public void PartialFirstIntervalDoesNotSetThePaceLimit()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            sample.Advance(true, 110, 110, Out(0), 110, 100, 20);
            sample.Advance(true, 109.5, 109.5, Out(0.1), 110, 100, 30);
            sample.Advance(true, 107.5, 107.5, Out(0.5), 110, 100, 40);
            Assert.True(sample.Active);
            sample.Advance(true, 97.5, 97.5, Out(0.7), 110, 100, 50);
            Assert.False(sample.Active);
        }

        [Fact]
        public void ConsumptionWhileStoppedIsTheft()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            Assert.Null(sample.Advance(false, 60, 60, Out(20), 110, 100, 20));
            Assert.Null(sample.Advance(false, 40, 40, Out(20), 110, 100, 30));     // off refineries consume nothing
            Assert.False(sample.Active);
        }

        [Fact]
        public void StoppingMidIntervalIsNotTheft()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            Assert.Null(sample.Advance(true, 60, 60, Out(20), 110, 100, 20));
            Assert.Null(sample.Advance(false, 55, 55, Out(22), 110, 100, 30));     // consumed before the power-off
            Assert.True(sample.Active);
        }

        [Fact]
        public void AStoppedObservationNeverLeaksIntoTheNextSample()
        {
            var sample = Fed(new Feed(2, Ore), 110);
            sample.Advance(false, 60, 60, Out(20), 110, 100, 20);
            Assert.Contains("stopped", sample.Status);
            sample.Reset();
            sample.Begin(Ore, Ingot, new List<IRefineryFeed> { new Feed(3, Ore) });
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 30);
            sample.Advance(true, 0, 0, new Dictionary<string, double>(), 110, 100, 40);
            Assert.Equal("learn " + Ore + ": feeding", sample.Status);
        }
    }
}
