using Xunit;

namespace SHEPHERD.Tests
{
    // Scaffold smoke test: proves the harness compiles the linked PB source (..\SHEPHERD\*.cs) and the
    // xunit runner is wired. Real routing/scan/sort tests replace this as the v1 core lands.
    public class SmokeTests
    {
        [Fact]
        public void Harness_Is_Wired()
        {
            Assert.True(true);
        }
    }
}
