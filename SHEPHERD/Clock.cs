using System;

namespace IngameScript
{
    // Built from the components, not DateTime.ToString(format), to sidestep a culture-dependent time separator. The
    // caller applies the offset: the game exposes no local time zone.
    public static class Clock
    {
        public static string Hms(DateTime t) => P2(t.Hour) + ":" + P2(t.Minute) + ":" + P2(t.Second);

        public static string HmsCentis(DateTime t) =>
            P2(t.Hour) + ":" + P2(t.Minute) + ":" + P2(t.Second) + "." + P2(t.Millisecond / 10);

        static string P2(int n) => n < 10 ? "0" + n : n.ToString();
    }
}
