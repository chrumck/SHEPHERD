namespace IngameScript
{
    // Runs of very different cost are tallied apart. A PB can't read its own in-run time, so each sample is the
    // PREVIOUS run's ms, attributed to that run's kind. Command = a non-tick run (a command or the limiter's signal).
    public enum RunKind { Other, Scan, Transfer, Fetch, Plan, Save, Command, Build, Fill }

    public class RunStats
    {
        // Matches the limiter's average window, so SlidingAvgMs compares directly. We can't see GC, so unlike the
        // limiter this mean keeps GC-coincident runs and reads a touch high.
        const int WindowRuns = 50;

        const int Kinds = 9;
        readonly int[] runs = new int[Kinds];
        // Counts GC-coincident runs too, so it over-counts offenses — yet it is the one in-script measure of how often
        // stalls land.
        readonly int[] over = new int[Kinds];
        readonly double[] maxMs = new double[Kinds];
        readonly double[] sumMs = new double[Kinds];
        readonly double[] window = new double[WindowRuns];
        int windowCount;
        int windowHead;
        double windowSum;

        public double PeakMs;
        public double WindowSec; // sim-time
        public double BarMs = Pacer.DefaultPeakMs;

        public double SlidingAvgMs => windowCount > 0 ? windowSum / windowCount : 0.0;

        // Sim-time at each wake rate (0 = Update1, 1 = Update10, 2 = Update100); the rate token shows only the current.
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
