using System;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class ClockTests
    {
        [Fact]
        public void HmsFormatsTimeOfDayZeroPadded()
        {
            Assert.Equal("14:05:03", Clock.Hms(new DateTime(2026, 7, 10, 14, 5, 3)));
            Assert.Equal("00:00:00", Clock.Hms(new DateTime(2026, 7, 10, 0, 0, 0)));
            Assert.Equal("23:59:59", Clock.Hms(new DateTime(2026, 7, 10, 23, 59, 59)));
        }

        [Fact]
        public void HmsCentisAddsHundredthsFromMilliseconds()
        {
            Assert.Equal("14:05:03.00", Clock.HmsCentis(new DateTime(2026, 7, 10, 14, 5, 3)));
            Assert.Equal("14:05:03.25", Clock.HmsCentis(new DateTime(2026, 7, 10, 14, 5, 3, 250))); // 250 ms → .25
            Assert.Equal("14:05:03.07", Clock.HmsCentis(new DateTime(2026, 7, 10, 14, 5, 3, 70)));  // 70 ms → .07
        }
    }
}
