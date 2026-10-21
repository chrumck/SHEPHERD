using System;

namespace IngameScript
{
    // Formats a wall-clock time as a fixed-width stamp. The caller passes the already-offset time; the game
    // exposes no local time zone, so the offset is applied upstream. Built by hand from the components rather
    // than DateTime.ToString(format) to sidestep any culture-dependent time separator.
    public static class Clock
    {
        public static string Hms(DateTime t) => P2(t.Hour) + ":" + P2(t.Minute) + ":" + P2(t.Second);

        public static string HmsCentis(DateTime t) =>
            P2(t.Hour) + ":" + P2(t.Minute) + ":" + P2(t.Second) + "." + P2(t.Millisecond / 10);

        static string P2(int n) => n < 10 ? "0" + n : n.ToString();
    }
}
