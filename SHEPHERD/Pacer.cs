namespace IngameScript
{
    // When the scheduler wakes next; Program maps each to an UpdateFrequency. Working = a backlog drains: wake fast,
    // since the many cheap ticks both dispatch the spaced heavy calls and dilute the rolling average. Idle = caught up
    // or a trickle: moderate, not rare — idle runs cost ~nothing against the bars, while a rare idle wake would make
    // reacting to new work (a ship docking) take minutes, as a whole cycle steps through before the first move is
    // found. Recovering = a breach, spike hold, stand-down or wary sim: wake less, fire no heavy call. Parked = a
    // safety hold (sim park, open breaker, escalated back-off): wake rarely.
    public enum Cadence { Parked, Idle, Working, Recovering }

    // The one adaptive controller. A PB can't read its own in-run time, so every decision uses the PREVIOUS run's ms
    // (Runtime.LastRunTimeMs), one tick late. Timers run on sim-time, which tracks sim speed as the limiter does;
    // spike pacing alone runs on the wall clock, as the rule it keeps under does.
    //   * a refractory after each heavy (conveyor) call, growing with its measured cost, so a hot transfer
    //     self-paces and two heavies never land on consecutive ticks;
    //   * a three-stage sim park — the limiter charges each run ÷ sim with no server-side deferral, so under lag the
    //     one lever is fewer runs. Dips count within a sliding window, since a lone dip can be noise: the first is
    //     only noted, the second holds heavies and slows the cadence, the third parks. Resuming needs the sim
    //     AVERAGE over a higher bar, so one good sample never resumes. Under real lag every window is a dip, so a
    //     park lands in about three windows;
    //   * a back-off on a breach while working — a run charged over the peak bar (ours or an external stall; the
    //     sandbox can't tell, nor see GC) opens a short de-cluster window, and a second breach inside it parks.
    //     Sub-breach runs are never offenses and are left alone. Its sim bar can confine it to lag; at 1 it is armed
    //     at any sim;
    //   * spike pacing — a run far over the peak bar is almost always a cold path (a method compiles on its first
    //     call), which no split brings under the bar and which must finish inside the limiter's startup allowance,
    //     yet a server may punish a few long runs close together from the first run. So two may land back to back,
    //     and a second inside the window holds heavies, at any sim, until the first has aged out;
    //   * dynamic cadence — fast only while a backlog drains, moderate otherwise, rare only under a safety park.
    public class Pacer
    {
        // Defaults match Destiny's limiter; [Settings] overrides them for another server.
        public const double DefaultPeakMs = 1.5;    // the limiter's per-run peak bar
        public const double DefaultParkSim = 0.75;
        public const double DefaultResumeSim = 0.8;
        public const double DefaultBackoffSim = 1.0; // Destiny's limiter enforces at any sim speed
        const double BaseHoldSec = 0.15;     // min spacing after any heavy call (keeps heavies non-consecutive at Update1)
        const double HoldScalePerMs = 0.3;   // extra refractory per ms of the heavy call's measured cost
        // Far shorter than the limiter's offense grace: it already drops GC runs and debounces offenses, and a pulse
        // near the breach-recurrence interval resonates into a permanent U10 lock-in. Re-arms if a cluster recurs.
        const double BreachBackoffSec = 2.0;
        double breachMs = DefaultPeakMs;
        double parkSim = DefaultParkSim;     // 0 = no sim park
        double resumeSim = DefaultResumeSim; // a park resumes once the sim AVERAGE clears this
        double backoffSim = DefaultBackoffSim; // the breach back-off arms only below this sim; 1 = at any sim
        double spikeMs = DefaultSpikeMs;
        double spikeWindowSec = DefaultSpikeWindowSec; // at most two spikes within this; 0 = no pacing
        // A tick with game/wall below this was absent, not slow (paused, alt-tabbed, saving): it carries no sim
        // information, and taken as one it would crater the average for tens of seconds or park on its own. Its
        // sample window is dropped whole; checked per tick so a freeze is caught at the same length whatever the window.
        const double SimFloor = 0.02;
        // Wall time, as the limiter's sim (physics steps in the last second) counts it. A tenth of its second, so a
        // short hitch is seen while it lasts. The cost: at a healthy sim one server tick ~30 ms late already reads as a
        // dip, and two in the sliding window go wary. From Update10 each tick is a sample.
        const double SimWindowSec = 0.1;
        const double SimAvgTauSec = 12.0;    // the average's time constant: resume takes roughly this long of healthy sim
        // Much shorter than the recurrence interval of isolated dips, so unrelated dips never chain toward a park.
        // Sliding: each dip refreshes it, and its expiry forgets all counted dips.
        const double SimWaryWindowSec = 5.0;
        public const int WaryDips = 2;       // dips within the window before heavies are held and the cadence slows
        public const int ParkDips = 3;       // dips within the window before the full park
        // Destiny punishes 3 runs over 10 ms within 10 s at any sim, from the first runs after a grid loads. Both
        // defaults keep a margin under that rule; the bar sits well above any steady-state run.
        public const double DefaultSpikeMs = 8.0;
        public const double DefaultSpikeWindowSec = 10.5;
        const double StandDownSec = 30.0;    // lets the limiter's offense window age out
        const double CrashHoldSec = 2.0;     // so a crashing step can never fast-loop
        const int MaxConsecutiveCrashes = 5; // with no clean run between, opens the breaker

        double holdSec;         // sim-time
        double backoffSec;      // sim-time
        double standDownSec;    // sim-time
        double spikeSec;        // wall time
        double clock;           // wall time, for the spike window
        double lastSpikeAt = -1e9;
        int windowBreaches;     // in the current back-off window; 2+ escalates to Parked
        int consecutiveCrashes;
        bool firedLastTick;     // its cost arrives as THIS tick's lastRunMs
        bool simParked;
        double simWarySec;      // sim-time; expiry resets the dip count
        int simDipCount;
        double windowGame;      // game time toward the next sim sample
        double windowWall;      // wall time over the same ticks
        bool windowSpiked;      // one of our own spikes ran inside the current window
        // The resume signal is a ratio of two EWMAs, game and wall smoothed separately: a mean of per-sample ratios
        // is biased, since quantization noise in a wall interval near the timer resolution cancels in a summed
        // denominator but not in a mean of ratios.
        double simGameAvg = 1.0;
        double simWallAvg = 1.0;
        double simInstant = 1.0; // the newest usable sample; dropped windows leave it alone

        public bool AllowHeavy;

        public void SetBars(double peakMs, double parkBelowSim, double resumeAboveSim, double backoffBelowSim)
        {
            breachMs = peakMs;
            parkSim = parkBelowSim;
            resumeSim = resumeAboveSim;
            backoffSim = backoffBelowSim;
        }

        public void SetSpike(double overMs, double windowSeconds)
        {
            spikeMs = overMs;
            spikeWindowSec = windowSeconds;
        }

        public bool SimParked => simParked;

        // The limiter's last warning came in: the next offense punishes the PB.
        public bool StandingDown => standDownSec > 0.0;

        public bool SimWary => simDipCount >= WaryDips && !simParked;

        public double SimInstant => simInstant;

        public double SimAvg => simGameAvg / simWallAvg;

        // A spike hold is running: no heavy call and no panel render, which can be a cold path too.
        public bool Quiet => spikeSec > 0.0;

        // Runs over the spike bar; each one is worth a log line.
        public int Spikes;

        // Back-off engagements since the last reset, not throttled ticks: an upper bound on offenses, since it can't
        // see the limiter's GC-drop and isn't debounced.
        public int Backoffs;

        // Rare by construction: the first breach already holds every heavy, so only an external stall lands a second.
        public int Escalations;

        // Wary engagements, and the sim-time spent wary or backing off: both hold the same cadence, so only these tell
        // them apart (wary edges are too frequent to log). An interval counts whole toward the state the previous tick
        // left, since the wake rate held for all of it. The two may overlap.
        public int Waries;
        public double WaryTime;
        public double BackoffTime;

        // Parked, so a persistently crashing step is retried once per rare wake, never hammered.
        public bool Halted => consecutiveCrashes >= MaxConsecutiveCrashes;

        // The routine per-heavy refractory is normal working, so it is excluded. Debug panel only: throttling is
        // common, so on the status LCD it would be noise.
        public bool Throttled => backoffSec > 0.0 || standDownSec > 0.0 || spikeSec > 0.0 || simParked || simDipCount >= WaryDips;

        // Top of every tick. game ÷ wall over a sample window estimates the sim the limiter divides each run by; a tick
        // that closes no window leaves every sim decision as it was. While starting no dip counts: the limiter charges
        // nothing in its wall-clock startup allowance, so a park there only pushes the cold runs out of it.
        public void Observe(double lastRunMs, double dtSeconds, bool workPending, double wallSeconds, bool starting)
        {
            if (SimWary) WaryTime += dtSeconds;
            if (backoffSec > 0.0) BackoffTime += dtSeconds;
            holdSec = Age(holdSec, dtSeconds);
            backoffSec = Age(backoffSec, dtSeconds);
            standDownSec = Age(standDownSec, dtSeconds);
            // The server's rule counts on the wall clock; in game time, lag would stretch one window into minutes,
            // holding the cold runs past the startup allowance.
            var wallStep = wallSeconds > 0.0 ? wallSeconds : 0.0;
            spikeSec = Age(spikeSec, wallStep);
            simWarySec = Age(simWarySec, dtSeconds);
            if (simWarySec <= 0.0) simDipCount = 0;

            // A dropped window (see SimFloor) or a backwards clock moves no average, counts no dip and resumes no park.
            // Nor does a window holding one of our own spikes: a cold path's tens of ms stretch its wall time into a
            // dip of our own making.
            var game = 0.0;
            var wall = 0.0;
            if (lastRunMs > spikeMs) windowSpiked = true;
            if (wallSeconds < 0.0 || wallSeconds > 0.0 && dtSeconds / wallSeconds < SimFloor)
            {
                windowGame = windowWall = 0.0;
                windowSpiked = false;
            }
            else
            {
                windowGame += dtSeconds;
                if ((windowWall += wallSeconds) >= SimWindowSec)
                {
                    if (!windowSpiked)
                    {
                        game = windowGame;
                        wall = windowWall;
                    }
                    windowGame = windowWall = 0.0;
                    windowSpiked = false;
                }
            }
            if (wall > 0.0)
            {
                var simEstimate = game / wall;
                simInstant = simEstimate;
                var blend = game / (SimAvgTauSec + game);
                simGameAvg += (game - simGameAvg) * blend;
                simWallAvg += (wall - simWallAvg) * blend;
                if (simEstimate < parkSim && !starting)
                {
                    if (++simDipCount == WaryDips && !simParked) Waries++;
                    simWarySec = SimWaryWindowSec;
                    if (simParked || simDipCount >= ParkDips)
                    {
                        // Seed the averages at the dip: the pre-dip history would resume us at once, and recovery
                        // must be shown from the dip onward.
                        if (!simParked) { simGameAvg = simEstimate; simWallAvg = 1.0; }
                        simParked = true;
                    }
                }
                else if (SimAvg > resumeSim) simParked = false;
            }

            // Past the spike bar the cost is a cold path, not the network, and spike pacing spaces it; scaling the
            // refractory by it would push the next cold path past the limiter's startup allowance.
            if (firedLastTick) holdSec = Higher(holdSec, BaseHoldSec + (lastRunMs < spikeMs ? lastRunMs : spikeMs) * HoldScalePerMs);
            // Idle breaches are excluded: a pause while parked is noise, not deferrable work. Either sim reading below
            // the back-off bar counts — the instant sees a lag onset first, the average an unfinished recovery. A bar
            // of 1 is checked outright, since an unclamped sample can read over 1 at full speed. The run is charged
            // ÷ the lower reading, capped at 1, as the limiter divides by sim.
            var sim = simInstant < SimAvg ? simInstant : SimAvg;
            if (workPending && lastRunMs > breachMs * (sim < 1.0 ? sim : 1.0)
                && (backoffSim >= 1.0 || simInstant < backoffSim || SimAvg < backoffSim)) ArmBackoff();

            clock += wallStep;
            // Charged like the breach: a server dividing by sim counts a short run under deep lag as a long one.
            if (lastRunMs > spikeMs * (sim < 1.0 ? sim : 1.0))
            {
                Spikes++;
                // Two spikes inside the window: hold until the older is out of it, so a third can't join them.
                if (clock - lastSpikeAt < spikeWindowSec) spikeSec = lastSpikeAt + spikeWindowSec - clock;
                lastSpikeAt = clock;
            }

            firedLastTick = false;
            AllowHeavy = !simParked && simDipCount < WaryDips && holdSec <= 0.0 && backoffSec <= 0.0 && standDownSec <= 0.0 && spikeSec <= 0.0;
        }

        // A fresh engagement opens a new window; a second breach inside it escalates from Recovering to Parked.
        void ArmBackoff()
        {
            if (backoffSec <= 0.0) { Backoffs++; windowBreaches = 0; }
            if (++windowBreaches == 2) Escalations++;
            backoffSec = Higher(backoffSec, BreachBackoffSec);
        }

        // Idle is frequent enough that a trickle of moves and background discovery advance in seconds with no cadence
        // of their own. Safety checks come first, so an escalated back-off parks even while a backlog drains.
        public Cadence NextCadence(bool backlog)
        {
            if (Halted || simParked) return Cadence.Parked;
            // These holds only keep heavies back, so they wake at U10: cheap runs flush a slow run out of the limiter's
            // rolling average in seconds, where at U100 it lingers over a minute and can draw an average offense.
            if (standDownSec > 0.0 || spikeSec > 0.0) return Cadence.Recovering;
            if (backoffSec > 0.0) return windowBreaches >= 2 ? Cadence.Parked : Cadence.Recovering;
            if (simDipCount >= WaryDips) return Cadence.Recovering;
            return backlog ? Cadence.Working : Cadence.Idle;
        }

        // Armed before the cost is known, so neither the rest of this tick nor the next can fire a second; Observe
        // lengthens it once the cost arrives.
        public void NotifyHeavyFired()
        {
            firedLastTick = true;
            holdSec = Higher(holdSec, BaseHoldSec);
            AllowHeavy = false;
        }

        // The limiter's cooperative GracefulShutDown:: signal: the next offense punishes. AllowHeavy=false covers the
        // gap until the next Observe.
        public void StandDown()
        {
            standDownSec = Higher(standDownSec, StandDownSec);
            AllowHeavy = false;
        }

        public void NotifyCrash()
        {
            consecutiveCrashes++;
            backoffSec = Higher(backoffSec, CrashHoldSec);
            AllowHeavy = false;
        }

        // Only uninterrupted failure accumulates to Halted; an isolated crash is handled by the back-off alone.
        public void NotifyCleanRun() => consecutiveCrashes = 0;

        // Measurement counters only; the control timers are untouched.
        public void ResetCounters()
        {
            Backoffs = 0;
            Escalations = 0;
            Waries = 0;
            WaryTime = 0.0;
            BackoffTime = 0.0;
        }

        static double Age(double remaining, double dt) => remaining > dt ? remaining - dt : 0.0;
        static double Higher(double a, double b) => a > b ? a : b;
    }
}
