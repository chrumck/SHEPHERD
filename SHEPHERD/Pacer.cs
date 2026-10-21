namespace IngameScript
{
    // How often the scheduler should wake next — the game-free intent the Pacer emits; Program maps each to a
    // concrete UpdateFrequency. Working = a backlog of moves is draining (wake fast: the many cheap ticks both
    // dispatch the spaced heavy calls and dilute the rolling-average bar). Idle = caught up, or only a trickle of
    // moves — wake moderately, not rarely: idle runs cost ~nothing against the limiter's bars and sparse transfers
    // leave the average far below its bar, while a rare-wake idle made reacting to new work (a
    // ship docking mid-idle-cycle) take minutes, since a whole cycle must step through before the first move is
    // even found. Recovering = a breach was just seen, so wake less often and fire no heavy call until the
    // window passes. Parked = a safety hold (sim park, stand-down, open breaker, escalated back-off) — wake
    // rarely; minimal presence is the point.
    public enum Cadence { Parked, Idle, Working, Recovering }

    // The one adaptive controller. A PB cannot read its own in-run time, so every decision is driven by the
    // PREVIOUS run's wall-clock ms (Runtime.LastRunTimeMs) — one tick late by necessity. It unifies the limiter
    // defenses over sim-time timers (never a tick count — sim-time tracks sim-speed, as the limiter does):
    //   * an adaptive refractory after each heavy (conveyor) call — spacing that grows with the measured cost,
    //     so a hot transfer self-paces and two heavies never land on consecutive ticks;
    //   * a sim-speed park — the PRIMARY throttle: the limiter enforces its rules only while server sim is below
    //     a threshold, and there it also charges each run divided by the sim ratio with no server-side deferral.
    //     A sample spans a few ticks of game time, never one: a single tick's wall interval is near the OS
    //     timer resolution, and one slow server tick at a healthy sim reads as deep lag, so per-tick samples
    //     faked dips most of the time. Three-stage on top, because sustained lag is what actually matters and a
    //     lone low sample can still be noise. Low samples are counted within a sliding window (each dip
    //     refreshes it; expiry resets the count): the FIRST dip is only noted — full speed continues; the SECOND
    //     holds heavy calls and slows the cadence, still ready to resume clean if the window expires; the THIRD
    //     confirms real lag and parks fully at idle. Resuming a full park needs the sim AVERAGE to clear a higher
    //     bar (slow to relax — the server gets time to recover, and one good sample in a dip never resumes us).
    //     Escalation stays fast when it matters: under genuine lag every sample window is a dip, so a full park
    //     lands in a fraction of a second, and only one refractory-spaced heavy can fire before stage two holds
    //     them;
    //   * a proactive back-off on a breach while working — a run over the peak bar (whether WE fired it or an
    //     external stall landed on a cheap tick) opens a short de-cluster window, and a second breach inside it
    //     escalates to a park; sub-breach runs are left alone (they are never offenses). Armed only while the
    //     sim is below its own bar, where the limiter may enforce: at a healthy sim it enforces nothing, and
    //     the breaches there are mostly external stalls on cheap ticks, so backing off only cost throughput. A
    //     bar of 1 arms it at any sim, for a limiter that enforces regardless of sim speed. Reducing exposure is
    //     the only lever, since we can't stop the first breach (a tick late) or see GC to match the limiter's
    //     GC-drop from the sandbox;
    //   * dynamic cadence — fast only while a backlog drains, moderate otherwise, rare only under a safety park.
    public sealed class Pacer
    {
        // The server-matched bars: Destiny's limiter, unless [Settings] overrides them for another server.
        public const double DefaultPeakMs = 0.8;    // the limiter's per-run peak bar — a run over this is an offense candidate
        public const double DefaultParkSim = 0.75;  // margin above the limiter's 0.7 enforcement gate
        public const double DefaultResumeSim = 0.8;
        // Above the resume bar: a sim that is recovering, not yet healthy, still backs off. 1 = at any sim.
        public const double DefaultBackoffSim = 0.9;
        const double BaseHoldSec = 0.15;     // min spacing after any heavy call (keeps heavies non-consecutive at Update1)
        const double HoldScalePerMs = 0.3;   // extra refractory per ms of the heavy call's measured cost
        // A short de-cluster pulse, deliberately far shorter than the limiter's 15 s offence grace: the limiter
        // already drops GC-coincident runs and debounces offenses, and a pulse near the observed breach-recurrence
        // interval resonated into a permanent U10 lock-in. Return to U1 fast; re-arm if a real cluster recurs.
        const double BreachBackoffSec = 2.0;
        double breachMs = DefaultPeakMs;
        double parkSim = DefaultParkSim;     // sim samples below this trigger the park stages; 0 = no sim park
        double resumeSim = DefaultResumeSim; // a full park resumes only once the sim AVERAGE clears this — sustained recovery, not one good sample
        double backoffSim = DefaultBackoffSim; // the breach back-off arms only while the sim is below this; 1 = at any sim
        // A tick whose game/wall ratio is below this was not slow, it was ABSENT: the game was paused, alt-tabbed,
        // or saving, so game time froze while the wall clock ran on. Such an interval carries no sim-speed
        // information at all — and taken as a measurement it reads as a ~50x-or-worse slowdown, which would
        // crater the average for tens of seconds (holding a park that should have resumed) and could chain into
        // a park on its own. So the sample window it falls in is dropped whole. Checked per tick, not per window,
        // so a freeze is caught at the same length whatever the window. A slowdown this deep is also a dead
        // server rather than one we could help by pacing.
        const double SimFloor = 0.02;
        // Five Update1 ticks, set a hair under so the float sum of five lands on it. Long enough that one slow
        // server tick no longer reads as a dip; short enough that real lag still parks within a fraction of a
        // second. At Update10 and slower every tick spans more than this, so each tick is its own sample.
        const double SimWindowSec = 0.08;
        const double SimAvgTauSec = 12.0;    // the average's time constant: resume takes roughly this long of healthy sim
        // Deliberately much shorter than the observed recurrence interval of isolated dips, so unrelated dips
        // never chain toward a park — only genuinely sustained lag (low samples fractions of a second apart)
        // escalates. The same resonance lesson as the breach back-off window above. Sliding: each dip refreshes
        // it, and its expiry forgets all counted dips.
        const double SimWaryWindowSec = 5.0;
        public const int WaryDips = 2;       // dips within the window before heavies are held and the cadence slows
        public const int ParkDips = 3;       // dips within the window before the full park
        const double StandDownSec = 30.0;    // cooperative hold after a GracefulShutDown:: signal — let the offense window age out
        const double CrashHoldSec = 2.0;     // hold after a crashing step so it can never fast-loop
        const int MaxConsecutiveCrashes = 5; // this many crashes with no clean run between → open the breaker (park at idle)

        double holdSec;         // heavy-call refractory remaining (sim-time)
        double backoffSec;      // cadence back-off remaining (sim-time)
        double standDownSec;    // GracefulShutDown stand-down remaining (sim-time)
        int windowBreaches;     // breaches recorded in the current back-off window; 1 → Recovering, 2+ → Parked (escalation)
        int consecutiveCrashes; // crashing steps since the last clean run; a run of them opens the breaker
        bool firedLastTick;     // a heavy fired last tick — its cost arrives as THIS tick's lastRunMs
        bool simParked;         // repeated low sim samples confirmed real lag — hold every heavy until the average recovers
        double simWarySec;      // sliding dip window remaining (sim-time); expiry resets the dip count
        int simDipCount;        // low sim samples counted within the current window — the escalation stage
        double windowGame;      // game time accumulated toward the next sim sample
        double windowWall;      // wall time over the same ticks
        // The resume signal, as a ratio of two EWMAs: game time and wall time smoothed SEPARATELY, then divided.
        // Averaging the per-sample ratios instead would be biased — the wall interval sits near the OS timer
        // resolution at the fast cadence, and quantization noise that cancels in a summed denominator does not
        // cancel in a mean of ratios (and clamping the over-1 samples would skew it further low).
        double simGameAvg = 1.0;
        double simWallAvg = 1.0;
        double simInstant = 1.0; // the newest USABLE sample — dropped intervals leave it alone, so the readout
                                 // shows the last real measurement instead of flashing a pause artifact

        public bool AllowHeavy { get; private set; }

        public void SetBars(double peakMs, double parkBelowSim, double resumeAboveSim, double backoffBelowSim)
        {
            breachMs = peakMs;
            parkSim = parkBelowSim;
            resumeSim = resumeAboveSim;
            backoffSim = backoffBelowSim;
        }

        public bool SimParked => simParked;

        public bool SimWary => simDipCount >= WaryDips && !simParked;

        public double SimInstant => simInstant;

        public double SimAvg => simGameAvg / simWallAvg;

        // How many times a cadence back-off has ENGAGED since the last reset (each fresh engagement, not each
        // throttled tick). Surfaced as "thr xN" on [SHEPHERD-DEBUG]. It over-states real offenses (it can't see
        // the limiter's GC-drop and isn't debounced), so read it as an upper bound, not the offense count.
        public int Backoffs { get; private set; }

        // How many times a back-off window escalated to a park (a second breach inside it). Rare by construction:
        // the first breach already holds every heavy call, so only an external stall can land the second.
        public int Escalations { get; private set; }

        // How many times the sim park's wary stage engaged, and the sim-time spent wary or in a breach back-off since
        // the last reset. Both hold the cadence at the same rate, so only these tell the two apart; wary edges are
        // too frequent to log. An interval counts whole toward the state the previous tick left: the wake rate
        // holds for all of it, even if the timer ran out partway. The two may overlap.
        public int Waries { get; private set; }
        public double WaryTime { get; private set; }
        public double BackoffTime { get; private set; }

        // The crash breaker is open — too many steps threw with no clean run between. Park so a persistently-
        // crashing step is retried at most once per rare wake, never hammered. Closes on a clean run.
        public bool Halted => consecutiveCrashes >= MaxConsecutiveCrashes;

        // Deliberately slowing down beyond the routine per-heavy refractory — a spike back-off, a crash hold, a
        // stand-down, or the sim park is active. Surfaced as THROTTLED on [SHEPHERD-DEBUG] only, not the glanceable status LCD
        // (throttling is common/expected, so it would be noise there). The transient per-heavy holdSec is normal
        // working, so it is excluded; Halted is reported separately.
        public bool Throttled => backoffSec > 0.0 || standDownSec > 0.0 || simParked || simDipCount >= WaryDips;

        // Top of every tick: age the timers by the elapsed sim-time and learn from the previous run's cost.
        // wallSeconds is the wall-clock time the same interval took; game ÷ wall over a sample window is the sim
        // estimate — the ratio the limiter divides each run's ms by, a window late. A tick that closes no window
        // leaves every sim-derived decision as it was.
        public void Observe(double lastRunMs, double dtSeconds, bool workPending, double wallSeconds)
        {
            if (SimWary) WaryTime += dtSeconds;
            if (backoffSec > 0.0) BackoffTime += dtSeconds;
            holdSec = Age(holdSec, dtSeconds);
            backoffSec = Age(backoffSec, dtSeconds);
            standDownSec = Age(standDownSec, dtSeconds);
            simWarySec = Age(simWarySec, dtSeconds);
            if (simWarySec <= 0.0) simDipCount = 0;

            // A dropped window (see SimFloor) must not move the average, must not count as a dip, and must not
            // resume a park either. A clock running backwards is no measurement either.
            var game = 0.0;
            var wall = 0.0;
            if (wallSeconds < 0.0 || wallSeconds > 0.0 && dtSeconds / wallSeconds < SimFloor) windowGame = windowWall = 0.0;
            else if ((windowGame += dtSeconds) >= SimWindowSec)
            {
                game = windowGame;
                wall = windowWall + wallSeconds;
                windowGame = windowWall = 0.0;
            }
            else windowWall += wallSeconds;
            if (wall > 0.0)
            {
                var simEstimate = game / wall;
                simInstant = simEstimate;
                var blend = game / (SimAvgTauSec + game);
                simGameAvg += (game - simGameAvg) * blend;
                simWallAvg += (wall - simWallAvg) * blend;
                if (simEstimate < parkSim)
                {
                    if (++simDipCount == WaryDips && !simParked) Waries++;
                    simWarySec = SimWaryWindowSec;
                    if (simParked || simDipCount >= ParkDips)
                    {
                        // Seed the averages so their ratio equals the dip on the parking transition: the pre-dip
                        // average is stale "healthy" history that would resume us immediately — recovery must be
                        // demonstrated from the dip onward.
                        if (!simParked) { simGameAvg = simEstimate; simWallAvg = 1.0; }
                        simParked = true;
                    }
                }
                else if (SimAvg > resumeSim) simParked = false;
            }

            // A heavy WE fired last tick extends the refractory in proportion to its measured cost, so an
            // expensive transfer self-paces (this only spaces the next heavy — it never shrinks the cadence).
            if (firedLastTick) holdSec = Higher(holdSec, BaseHoldSec + lastRunMs * HoldScalePerMs);
            // A breach on a working tick arms the de-cluster back-off — whether WE fired the heavy or an external
            // stall landed on a cheap tick (indistinguishable from the sandbox). Sub-breach runs are left alone:
            // they are never offenses, so backing off on them only throws away throughput. Idle spikes are
            // excluded too — a pause while parked is noise, not deferrable work. Either sim reading below the
            // back-off bar counts: the newest sample sees a lag onset first, the average a recovery not yet
            // complete. A bar of 1 is checked outright: an unclamped sample can read over 1 at full speed.
            if (workPending && lastRunMs > breachMs
                && (backoffSim >= 1.0 || simInstant < backoffSim || SimAvg < backoffSim)) ArmBackoff();

            firedLastTick = false;
            AllowHeavy = !simParked && simDipCount < WaryDips && holdSec <= 0.0 && backoffSec <= 0.0 && standDownSec <= 0.0;
        }

        // Arm (or escalate) the cadence back-off after a breach. A fresh engagement opens a new window (and
        // resets the escalation count); a second breach inside the still-open window escalates it. One breach →
        // Recovering (~10x fewer runs); a second inside the window → Parked (~100x fewer).
        void ArmBackoff()
        {
            if (backoffSec <= 0.0) { Backoffs++; windowBreaches = 0; }
            if (++windowBreaches == 2) Escalations++;
            backoffSec = Higher(backoffSec, BreachBackoffSec);
        }

        // End of the tick, with the now-current backlog state: pick the next wake cadence. Only safety states park;
        // no backlog is Idle, still frequent enough that a trickle of moves and background discovery work advance
        // in seconds and need no cadence floating of their own. The safety checks outrank the backlog check so an
        // escalated back-off parks even when a backlog is draining.
        public Cadence NextCadence(bool backlog)
        {
            if (standDownSec > 0.0 || Halted || simParked) return Cadence.Parked;
            if (backoffSec > 0.0) return windowBreaches >= 2 ? Cadence.Parked : Cadence.Recovering;
            if (simDipCount >= WaryDips) return Cadence.Recovering;
            return backlog ? Cadence.Working : Cadence.Idle;
        }

        // Arm the refractory the instant a heavy call is dispatched (before its cost is known), so the very next
        // tick cannot fire a second one; Observe then lengthens it once the measured cost arrives.
        public void NotifyHeavyFired()
        {
            firedLastTick = true;
            holdSec = Higher(holdSec, BaseHoldSec);
        }

        // The limiter fired the cooperative GracefulShutDown:: argument — it is about to punish us. Hold every
        // heavy call and park long enough for the offense count to age out. The immediate
        // AllowHeavy=false covers the gap until the next Observe.
        public void StandDown()
        {
            standDownSec = Higher(standDownSec, StandDownSec);
            AllowHeavy = false;
        }

        // A step threw. Arm a back-off so the scheduler cannot fast-loop the crashing step; enough of these with no
        // clean run between opens the breaker (Halted → parked). NotifyCleanRun closes it again.
        public void NotifyCrash()
        {
            consecutiveCrashes++;
            backoffSec = Higher(backoffSec, CrashHoldSec);
            AllowHeavy = false;
        }

        // A tick completed without throwing — the crashing step (if any) has recovered, so close the breaker. Only
        // total, uninterrupted failure accumulates to Halted; an isolated crash is handled by the back-off alone.
        public void NotifyCleanRun() => consecutiveCrashes = 0;

        // Measurement counters, not live control state, so the refractory/back-off/stand-down timers are untouched.
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
