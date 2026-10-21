namespace IngameScript
{
    // Which kind of tick a run-time sample belongs to, so the instrument can separate runs with very different
    // costs: Fetch (the GetBlocksOfType sweep), Scan (a classify batch), Build (a build step over a slice of the
    // managed set), the network-bound Transfer,
    // Plan (the production-plan tick, whose work grows with quotas and machines), Save (the Custom Data
    // rewrite), Command (a non-tick run: a terminal command or the limiter's signal), and every other cheap tick. A PB
    // can't read its own in-run time, so each sample is the PREVIOUS run's ms attributed to that run's kind (one tick
    // late).
    public enum RunKind { Other, Scan, Transfer, Fetch, Plan, Save, Command, Build }

    // A running tally of per-tick wall-clock ms, bucketed by RunKind, plus one all-tick peak and a rolling mean
    // over the last WindowRuns runs — the numbers checked against the limiter's peak and average bars.
    public sealed class RunStats
    {
        // The limiter averages over its own last-50 non-dropped runs; match that window so SlidingAvgMs is
        // directly comparable. We can't see GC, so unlike the limiter we can't drop GC-coincident runs — this
        // mean includes them and reads a touch high (an upper bound on the limiter's real average).
        const int WindowRuns = 50;

        const int Kinds = 8;
        readonly int[] runs = new int[Kinds];
        // Runs over the peak bar. GC-coincident runs are counted too (we can't see GC), so this over-counts the
        // limiter's offenses — but that makes it the one in-script measure of how often stalls land at all.
        readonly int[] over = new int[Kinds];
        readonly double[] maxMs = new double[Kinds];
        readonly double[] sumMs = new double[Kinds];
        readonly double[] window = new double[WindowRuns]; // circular buffer of the last WindowRuns run-ms
        int windowCount;                                   // filled slots (<= WindowRuns)
        int windowHead;                                    // next slot to overwrite
        double windowSum;                                  // running sum of the live window, for an O(1) mean

        public double PeakMs { get; private set; }
        public double WindowSec { get; private set; } // sim-time elapsed since the last Reset
        public double BarMs = Pacer.DefaultPeakMs;

        public double SlidingAvgMs => windowCount > 0 ? windowSum / windowCount : 0.0;

        // Sim-time spent at each wake rate (0 = Update1, 1 = Update10, 2 = Update100) — how often the loop runs
        // over a long session, which the instantaneous rate token can't show.
        readonly double[] rateSec = new double[3];

        public double RateSec(int rate) => rateSec[rate];

        public void AddElapsed(double dtSeconds, int rate)
        {
            WindowSec += dtSeconds;
            rateSec[rate] += dtSeconds;
        }

        public int Runs(RunKind kind) => runs[(int)kind];
        public double MaxMs(RunKind kind) => maxMs[(int)kind];
        public double AvgMs(RunKind kind) { var n = runs[(int)kind]; return n > 0 ? sumMs[(int)kind] / n : 0.0; }
        public int Over(RunKind kind) => over[(int)kind];

        public void Record(double ms, RunKind kind)
        {
            if (ms > PeakMs) PeakMs = ms;

            // Slide the window: once full, evict the oldest slot from the sum before overwriting it.
            if (windowCount == WindowRuns) windowSum -= window[windowHead];
            else windowCount++;
            window[windowHead] = ms;
            windowSum += ms;
            windowHead = windowHead + 1 == WindowRuns ? 0 : windowHead + 1;

            var k = (int)kind;
            runs[k]++;
            sumMs[k] += ms;
            if (ms > maxMs[k]) maxMs[k] = ms;
            if (ms > BarMs) over[k]++;
        }

        public void Reset()
        {
            for (var i = 0; i < Kinds; i++) { runs[i] = 0; over[i] = 0; maxMs[i] = 0.0; sumMs[i] = 0.0; }
            for (var i = 0; i < WindowRuns; i++) window[i] = 0.0;
            for (var i = 0; i < 3; i++) rateSec[i] = 0.0;
            windowCount = 0; windowHead = 0; windowSum = 0.0;
            PeakMs = 0.0;
            WindowSec = 0.0;
        }
    }
}
