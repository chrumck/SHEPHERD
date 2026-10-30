using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class StateChannelTests
    {
        readonly List<string> emitted = new List<string>();
        readonly StateChannel channel;

        public StateChannelTests() { channel = new StateChannel(emitted.Add); }

        void Commit(params string[] current)
        {
            channel.Begin();
            foreach (var message in current) channel.Push(message);
            channel.Commit();
        }

        [Fact]
        public void EmitsOnceOnTheTransitionIn()
        {
            Commit("a");
            Commit("a");
            Commit("a");
            Assert.Equal(new[] { "a" }, emitted);
        }

        [Fact]
        public void EmitsAgainAfterTheStateResolves()
        {
            Commit("a");
            Commit();
            Commit("a");
            Assert.Equal(new[] { "a", "a" }, emitted);
        }

        [Fact]
        public void TracksEachMessageIndependently()
        {
            Commit("a");
            Commit("a", "b");
            Commit("b");
            Commit("a", "b");
            Assert.Equal(new[] { "a", "b", "a" }, emitted);
        }

        [Fact]
        public void EmptySetsStayQuiet()
        {
            Commit();
            Commit();
            Assert.Empty(emitted);
        }

        [Fact]
        public void CurrentHoldsTheAssertedSetForRendering()
        {
            // The status panel renders Current, so it must hold every asserted state for as long as it is asserted —
            // including the ones already logged, which the log will not repeat.
            Commit("a", "b");
            Assert.Equal(new[] { "a", "b" }, channel.Current);
            Commit("a", "b");
            Assert.Equal(new[] { "a", "b" }, channel.Current);   // still shown
            Assert.Equal(new[] { "a", "b" }, emitted);           // but not logged twice
            Commit();
            Assert.Empty(channel.Current);                        // resolved → off the panel
        }

        [Fact]
        public void NullIsIgnoredSoAnOptionalStatusCanBePassedStraightThrough()
        {
            Commit("a", null, "b");
            Assert.Equal(new[] { "a", "b" }, channel.Current);
            Assert.Equal(new[] { "a", "b" }, emitted);
        }

        [Fact]
        public void SeparateInstancesDoNotPruneEachOther()
        {
            // One instance per recompute cadence: a channel that stops asserting its own state must not clear
            // another channel's still-standing state.
            var otherEmitted = new List<string>();
            var other = new StateChannel(otherEmitted.Add);
            other.Begin();
            other.Push("a");
            other.Commit();

            Commit("a");           // this channel sees "a" for the first time
            Commit();              // and drops it
            other.Begin();
            other.Push("a");
            other.Commit();        // the other channel still holds it, so no second emit

            Assert.Equal(new[] { "a" }, emitted);
            Assert.Equal(new[] { "a" }, otherEmitted);
        }
    }
}
