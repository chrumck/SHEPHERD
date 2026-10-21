using System.Text;

namespace IngameScript
{
    // Formats the [SHEPHERD-DEBUG] diagnostics view: a header (version / run-state / wake cadence / the rendering PB's
    // name / cycle & block counts / uptime / peak & rolling-average ms / estimated sim speed / share of time at each
    // wake rate) then the per-kind run
    // table (count / max ms / avg ms / runs over the peak bar). The runtime numbers live here, not on the glanceable
    // status LCD.
    public static class Profiler
    {
        // THROTTLED U10 is either a breach back-off or the sim park's wary stage, so both are shown side by side,
        // each as engagements and share of the window's sim-time, to tell which is holding the cadence.
        public static void Render(StringBuilder sb, string version, string blockName, string state, string cadence, int cycles,
                                  int managed, int dest, int throttles, double throttleSec, int waries, double warySec,
                                  double sim, double simAvg, RunStats stats)
        {
            sb.Clear();
            sb.Append("SHEPHERD ").Append(version).Append(" · ").Append(state).Append(" · ").AppendLine(cadence);
            sb.AppendLine(blockName);
            sb.Append("cycles ").Append(Count(cycles)).Append(" · uptime ").AppendLine(Dur(stats.WindowSec));
            sb.Append("managed ").Append(Count(managed)).Append(" · dest ").Append(Count(dest)).AppendLine();
            sb.Append("peak ").Append(Ms(stats.PeakMs)).Append(" ms · avg50 ").Append(Ms(stats.SlidingAvgMs))
              .AppendLine(" ms");
            // sim = the last instant estimate (the park trigger), then the running average (the resume trigger).
            sb.Append("thr x").Append(Count(throttles)).Append(" ").Append(Share(throttleSec, stats.WindowSec))
              .Append(" · wary x").Append(Count(waries)).Append(" ").AppendLine(Share(warySec, stats.WindowSec));
            sb.Append("sim ").Append(sim.ToString("0.00")).Append("/").Append(simAvg.ToString("0.00")).AppendLine();
            sb.Append("U1 ").Append(Share(stats.RateSec(0), stats.WindowSec))
              .Append(" · U10 ").Append(Share(stats.RateSec(1), stats.WindowSec))
              .Append(" · U100 ").AppendLine(Share(stats.RateSec(2), stats.WindowSec));
            Row(sb, stats, RunKind.Fetch);
            Row(sb, stats, RunKind.Scan);
            Row(sb, stats, RunKind.Build);
            Row(sb, stats, RunKind.Transfer);
            Row(sb, stats, RunKind.Plan);
            Row(sb, stats, RunKind.Save);
            Row(sb, stats, RunKind.Command);
            Row(sb, stats, RunKind.Other);
        }

        // Indexed by RunKind; shared with the throttle log line so it names the arming run as the table does.
        static readonly string[] Tags = { "othr", "scan", "xfer", "ftch", "plan", "save", "cmd", "bld" };

        public static string Tag(RunKind kind) => Tags[(int)kind];

        static void Row(StringBuilder sb, RunStats stats, RunKind kind)
        {
            sb.Append(Tag(kind)).Append(" x").Append(Count(stats.Runs(kind)))
              .Append(" max ").Append(Ms(stats.MaxMs(kind)))
              .Append(" avg ").Append(Ms(stats.AvgMs(kind)))
              .Append(" over ").AppendLine(Count(stats.Over(kind)));
        }

        static string Ms(double ms) => ms.ToString("0.000");

        static string Share(double sec, double window) => (window > 0.0 ? (int)(sec * 100.0 / window + 0.5) : 0) + "%";

        static string Dur(double sec) { var s = (int)sec; return s >= 60 ? (s / 60) + "m" + (s % 60) + "s" : s + "s"; }

        // A compact count — up to 4 significant digits with a k/M/G suffix — so a long-running tally stays
        // legible instead of sprawling to "othr x2534187".
        static string Count(int n)
        {
            if (n < 10000) return n.ToString();
            double v;
            string suffix;
            if (n < 1000000) { v = n / 1000.0; suffix = "k"; }
            else if (n < 1000000000) { v = n / 1000000.0; suffix = "M"; }
            else { v = n / 1000000000.0; suffix = "G"; }
            var fmt = v < 10.0 ? "0.###" : v < 100.0 ? "0.##" : "0.#";
            return v.ToString(fmt) + suffix;
        }
    }
}
