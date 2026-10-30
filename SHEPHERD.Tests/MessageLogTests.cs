using System.Text;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class MessageLogTests
    {
        static string Render(MessageLog log)
        {
            var sb = new StringBuilder();
            log.Render(sb);
            return sb.ToString();
        }

        [Fact]
        public void RendersNewestFirst()
        {
            var log = new MessageLog(10);
            log.Push("first");
            log.Push("second");
            log.Push("third");

            var lines = Render(log).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Equal(new[] { "third", "second", "first" }, lines);
        }

        [Fact]
        public void DropsOldestPastCapacity()
        {
            var log = new MessageLog(2);
            log.Push("a");
            log.Push("b");
            log.Push("c"); // evicts "a"

            Assert.Equal(2, log.Count);
            var lines = Render(log).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Equal(new[] { "c", "b" }, lines);
        }

        [Fact]
        public void DirtySetOnAddClearedOnDemand()
        {
            var log = new MessageLog(4);
            Assert.False(log.Dirty);          // nothing added yet
            log.Push("x");
            Assert.True(log.Dirty);
            log.ClearDirty();
            Assert.False(log.Dirty);
            log.Push("y");
            Assert.True(log.Dirty);           // a new message re-dirties for the next render
        }

        [Fact]
        public void NullLineIsSafe()
        {
            var log = new MessageLog(4);
            log.Push(null);
            Assert.Equal(1, log.Count);
        }

        [Fact]
        public void CapacityFloorsAtOne()
        {
            var log = new MessageLog(0);
            log.Push("a");
            log.Push("b");
            Assert.Equal(1, log.Count);
            Assert.Contains("b", Render(log));
        }
    }
}
