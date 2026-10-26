using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    // Characterizes the adaptive controller (Pacer) offline — the same doubles/bools it runs on in-game, so no
    // game types are needed. Timings are asserted qualitatively (clears eventually, hot > cheap, non-consecutive)
    // rather than against the private tunables, so retuning the constants for Tier-2 never breaks these tests.
    public class PacerTests
    {
        const double Tick = 1.0 / 60.0; // one Update1 tick of sim-time
        const double HealthySim = 1.0;  // comfortably above every sim threshold
        const double LaggedSim = 0.6;   // below the park bar — the limiter's enforcement window is near
        const int WindowTicks = 3;      // Update1 ticks per sim sample window
        const double Window = WindowTicks * Tick;
        const double Breach = Pacer.DefaultPeakMs + 0.1; // a run over the peak bar at full speed

        // The tests speak sim ratios; the Pacer takes the wall time the interval took.
        static void Observe(Pacer p, double ms, double dt, bool work, double sim) =>
            p.Observe(ms, dt, work, sim > 0.0 ? dt / sim : 0.0, false);

        // Observe with a heavy's measured cost, then keep observing cheap ticks until a heavy is allowed again;
        // returns how many observations that took. Larger = a longer refractory.
        static int ObservationsToClearHeavy(double heavyMs)
        {
            var p = new Pacer();
            p.NotifyHeavyFired();
            var ms = heavyMs; // the cost arrives on the tick after the call fired
            var observations = 0;
            while (true)
            {
                Observe(p, ms, Tick, true, HealthySim);
                ms = 0.0;
                observations++;
                if (p.AllowHeavy) return observations;
                Assert.True(observations < 100000, "refractory never cleared");
            }
        }

        // Back-off mechanics tests run with the sim gate open, so a breach arms at the healthy test sim.
        static Pacer AnySim()
        {
            var p = new Pacer();
            p.SetBars(Pacer.DefaultPeakMs, Pacer.DefaultParkSim, Pacer.DefaultResumeSim, 1.0);
            return p;
        }

        static void Dips(Pacer p, int count)
        {
            for (var i = 0; i < count; i++) Observe(p, 0.0, Window, true, LaggedSim);
        }

        // A Pacer fully sim-parked by lagged samples inside the dip window — the starting point of the resume tests.
        static Pacer ParkedPacer()
        {
            var p = new Pacer();
            Dips(p, Pacer.ParkDips);
            Assert.True(p.SimParked);
            return p;
        }

        [Fact]
        public void FreshPacerWorksAndAllowsHeavy()
        {
            var p = new Pacer();
            Observe(p, 0.0, Tick, true, HealthySim);
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void IdleWhenNoWorkButStillProbes()
        {
            var p = new Pacer();
            Observe(p, 0.0, Tick, false, HealthySim);
            Assert.Equal(Cadence.Idle, p.NextCadence(false));
            Assert.True(p.AllowHeavy); // the idle sweep is still allowed to transfer — that IS work detection
        }

        [Fact]
        public void BreachBackoffOutranksAnEmptyQueue()
        {
            var p = AnySim();
            Observe(p, Breach, Tick, true, HealthySim); // one breach → Recovering-level back-off
            Assert.Equal(Cadence.Recovering, p.NextCadence(false));
            Observe(p, Breach, Tick, true, HealthySim); // second inside the window → the park wins
            Assert.Equal(Cadence.Parked, p.NextCadence(false));
        }

        [Fact]
        public void HeavyFiredBlocksTheVeryNextTick()
        {
            var p = new Pacer();
            p.NotifyHeavyFired();
            Observe(p, 0.0, Tick, true, HealthySim); // one tick later — cost unknown yet, provisional hold still holds
            Assert.False(p.AllowHeavy); // never two heavies on consecutive ticks
        }

        [Fact]
        public void HeavyFiredBlocksTheRestOfItsTick()
        {
            var p = new Pacer();
            Observe(p, 0.0, Tick, true, HealthySim);
            Assert.True(p.AllowHeavy);
            p.NotifyHeavyFired(); // the election's scope scan runs ahead of the tick's own step
            Assert.False(p.AllowHeavy);
        }

        [Fact]
        public void RefractoryClearsAfterEnoughSimTime()
        {
            var p = new Pacer();
            p.NotifyHeavyFired();
            Observe(p, 0.2, Tick, true, HealthySim);
            Assert.False(p.AllowHeavy);
            Observe(p, 0.0, 10.0, true, HealthySim); // a long gap ages the hold away
            Assert.True(p.AllowHeavy);
        }

        [Fact]
        public void HotHeavyHoldsLongerThanCheapHeavy()
        {
            Assert.True(ObservationsToClearHeavy(0.7) > ObservationsToClearHeavy(0.1));
        }

        [Fact]
        public void UnexpectedSpikeWhileWorkingRecoversAndBlocksHeavy()
        {
            var p = AnySim();
            Observe(p, Breach, Tick, true, HealthySim); // breach we did not cause → back off
            Assert.False(p.AllowHeavy);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
        }

        [Fact]
        public void SpikeBackoffsAreTalliedOncePerEngagement()
        {
            var p = AnySim();
            Assert.Equal(0, p.Backoffs);
            Observe(p, Breach, Tick, true, HealthySim);  // fresh breach → one engagement
            Observe(p, Breach, Tick, true, HealthySim);  // still inside the back-off window → not a new engagement
            Assert.Equal(1, p.Backoffs);
            Observe(p, 0.0, 10.0, true, HealthySim);  // age the back-off away
            Observe(p, Breach, Tick, true, HealthySim);  // a new breach after recovery → a second engagement
            Assert.Equal(2, p.Backoffs);
            p.ResetCounters();
            Assert.Equal(0, p.Backoffs);
        }

        [Fact]
        public void EscalationsCountSecondBreachesInsideAWindowOnly()
        {
            var p = AnySim();
            Observe(p, Breach, Tick, true, HealthySim);  // engages → Recovering, not an escalation
            Assert.Equal(0, p.Escalations);
            Observe(p, Breach, Tick, true, HealthySim);  // second breach inside the window → escalates to a park
            Assert.Equal(1, p.Escalations);
            Observe(p, Breach, Tick, true, HealthySim);  // a third in the same window is not a new escalation
            Assert.Equal(1, p.Escalations);
            Observe(p, 0.0, 10.0, true, HealthySim);  // age the window away
            Observe(p, Breach, Tick, true, HealthySim);  // a fresh engagement alone never counts
            Assert.Equal(1, p.Escalations);
            Assert.Equal(2, p.Backoffs);
            p.ResetCounters();
            Assert.Equal(0, p.Escalations);
        }

        [Fact]
        public void WaryEngagementsAreTalliedOncePerDipWindow()
        {
            var p = new Pacer();
            Dips(p, Pacer.WaryDips);
            Assert.Equal(1, p.Waries);
            Dips(p, Pacer.ParkDips - Pacer.WaryDips); // escalating to the park is not a new wary engagement
            Assert.Equal(1, p.Waries);
            p.ResetCounters();
            Assert.Equal(0, p.Waries);
        }

        [Fact]
        public void WaryRearmsAfterTheDipWindowExpires()
        {
            var p = new Pacer();
            Dips(p, Pacer.WaryDips);
            Observe(p, 0.0, 10.0, true, HealthySim); // the window expires and forgets the dips
            Dips(p, Pacer.WaryDips);
            Assert.Equal(2, p.Waries);
        }

        [Fact]
        public void WaryAndBackoffTimeAccrueSeparately()
        {
            var p = new Pacer();
            Dips(p, Pacer.WaryDips);
            Assert.Equal(0.0, p.WaryTime);           // the interval before a dip was not yet spent wary
            Observe(p, 0.0, 1.0, true, HealthySim);
            Assert.Equal(1.0, p.WaryTime, 6);
            Assert.Equal(0.0, p.BackoffTime);

            var q = AnySim();
            Observe(q, Breach, Tick, true, HealthySim);  // a breach arms the back-off
            Observe(q, 0.0, 0.5, true, HealthySim);
            Assert.Equal(0.5, q.BackoffTime, 6);
            Assert.Equal(0.0, q.WaryTime);
            q.ResetCounters();
            Assert.Equal(0.0, q.BackoffTime);
        }

        [Fact]
        public void IdleSpikeIsIgnored()
        {
            var p = AnySim();
            Observe(p, 0.6, Tick, false, HealthySim); // a pause while parked is noise, not a reason to back off
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Idle, p.NextCadence(false));
        }

        const double SpikeRun = Pacer.DefaultSpikeMs + 1.0;

        [Fact]
        public void ALoneSpikeIsCountedButHoldsNothing()
        {
            // Cold paths must be over within the limiter's startup grace, so one spike alone costs no time. On an
            // idle tick, so the breach back-off stays out of it.
            var p = new Pacer();
            Observe(p, SpikeRun, Tick, false, HealthySim);
            Assert.Equal(1, p.Spikes);
            Assert.False(p.Quiet);
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Idle, p.NextCadence(false));
        }

        [Fact]
        public void ASecondSpikeInsideTheWindowHoldsUntilTheFirstAgesOut()
        {
            var p = new Pacer();
            Observe(p, SpikeRun, Tick, true, HealthySim);
            Observe(p, SpikeRun, 1.0, true, HealthySim);      // the second spike, a second later
            Assert.True(p.Quiet);
            Assert.True(p.Throttled);
            Assert.False(p.AllowHeavy);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
            Observe(p, 0.0, Pacer.DefaultSpikeWindowSec - 1.0 - 0.01, true, HealthySim);
            Assert.True(p.Quiet);                             // the first spike is still inside the window
            Observe(p, 0.0, 0.02, true, HealthySim);
            Assert.False(p.Quiet);
            Assert.True(p.AllowHeavy);
        }

        [Fact]
        public void SpikesFartherApartThanTheWindowNeverHold()
        {
            var p = new Pacer();
            for (var i = 0; i < 5; i++)
            {
                Observe(p, SpikeRun, Pacer.DefaultSpikeWindowSec, true, HealthySim);
                Assert.False(p.Quiet);
            }
            Assert.Equal(5, p.Spikes);
        }

        [Fact]
        public void NeverThreeSpikesWithinTenSecondsEvenIfEveryAllowedRunSpikes()
        {
            // Destiny punishes three runs over 10 ms within 10 s at any sim speed, from the very first run. Worst
            // case: every run the Pacer doesn't hold is a cold path, its cost seen a tick late.
            var p = new Pacer();
            var spikes = new System.Collections.Generic.List<double>();
            var now = 0.0;
            var ms = 0.0;
            for (var i = 0; i < 60 * 60; i++)
            {
                Observe(p, ms, Tick, true, HealthySim);
                now += Tick;
                ms = p.Quiet ? 0.01 : SpikeRun;
                if (ms > Pacer.DefaultSpikeMs) spikes.Add(now);
            }
            Assert.True(spikes.Count >= 10, "pacing should let cold paths through, got " + spikes.Count);
            for (var i = 2; i < spikes.Count; i++)
                Assert.True(spikes[i] - spikes[i - 2] > 10.0, "three spikes within 10 s ending at " + spikes[i]);
        }

        [Fact]
        public void ASpikeWhileIdleCountsToo()
        {
            var p = new Pacer();
            Observe(p, SpikeRun, Tick, false, HealthySim);   // a cold render on an idle tick counts too
            Observe(p, SpikeRun, Tick, false, HealthySim);
            Assert.True(p.Quiet);
            Assert.Equal(Cadence.Recovering, p.NextCadence(false));
        }

        [Fact]
        public void BreachUnderTheSpikeBarIsNoSpike()
        {
            var p = new Pacer();
            Observe(p, Pacer.DefaultSpikeMs - 1.0, Tick, true, HealthySim);
            Observe(p, Pacer.DefaultSpikeMs - 1.0, Tick, true, HealthySim);
            Assert.False(p.Quiet);
            Assert.Equal(0, p.Spikes);
        }

        [Fact]
        public void SpikeBarAndWindowAreSettable()
        {
            var p = new Pacer();
            p.SetSpike(20.0, Pacer.DefaultSpikeWindowSec);
            Observe(p, 15.0, Tick, false, HealthySim);  // over the default bar, under the configured one
            Observe(p, 15.0, Tick, false, HealthySim);
            Assert.False(p.Quiet);
            Assert.Equal(0, p.Spikes);
            p.SetSpike(Pacer.DefaultSpikeMs, 0.0);      // no pacing: spikes are still counted
            Observe(p, SpikeRun, Tick, false, HealthySim);
            Observe(p, SpikeRun, Tick, false, HealthySim);
            Assert.False(p.Quiet);
            Assert.True(p.AllowHeavy);
            Assert.Equal(2, p.Spikes);
        }

        static Pacer NoSimPark()
        {
            var p = new Pacer();
            p.SetBars(Pacer.DefaultPeakMs, 0.0, Pacer.DefaultResumeSim, Pacer.DefaultBackoffSim);
            return p;
        }

        [Fact]
        public void UnderLagTheSpikeHoldEndsOnTheWallClock()
        {
            // At sim 0.1 the window's 10.5 s of wall time is only ~1 s of game time.
            const double sim = 0.1;
            var p = NoSimPark();
            Observe(p, SpikeRun, Window, false, sim);
            Observe(p, SpikeRun, Window, false, sim);
            Assert.True(p.Quiet);
            Observe(p, 0.0, (Pacer.DefaultSpikeWindowSec + 0.1) * sim, false, sim);
            Assert.False(p.Quiet);
        }

        [Fact]
        public void UnderLagAShorterRunIsChargedAsASpike()
        {
            // The server divides each run by the sim ratio, so at sim 0.5 a 5 ms run counts as 10 ms.
            var p = NoSimPark();
            Observe(p, 0.0, Window, false, 0.5);   // the sample that reveals the lag
            Observe(p, 5.0, Window, false, 0.5);
            Assert.Equal(1, p.Spikes);
            var healthy = NoSimPark();
            Observe(healthy, 0.0, Window, false, HealthySim);
            Observe(healthy, 5.0, Window, false, HealthySim);
            Assert.Equal(0, healthy.Spikes);
        }

        [Fact]
        public void LagWhileStartingNeverParksAndParksOnceTheStartEnds()
        {
            // The limiter charges nothing inside its startup allowance, so a park there only spends it.
            var p = new Pacer();
            for (var i = 0; i < 10 * Pacer.ParkDips; i++)
            {
                p.Observe(0.0, Window, true, Window / LaggedSim, true);
                Assert.False(p.SimParked);
                Assert.False(p.SimWary);
            }
            Assert.True(p.AllowHeavy);
            Dips(p, Pacer.ParkDips);
            Assert.True(p.SimParked);
        }

        [Fact]
        public void ACostlierSpikeDoesNotLengthenTheRefractory()
        {
            // Past the spike bar the cost is a cold path; the spike pacing spaces it, the refractory must not.
            Assert.Equal(ObservationsToClearHeavy(Pacer.DefaultSpikeMs), ObservationsToClearHeavy(60.0));
            Assert.True(ObservationsToClearHeavy(Pacer.DefaultSpikeMs) > ObservationsToClearHeavy(1.0));
        }

        [Fact]
        public void BackoffClearsAfterItsWindow()
        {
            var p = AnySim();
            Observe(p, Breach, Tick, true, HealthySim);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
            Observe(p, 0.0, 10.0, true, HealthySim); // ride out the back-off window
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void SubBreachFiredHeavyExtendsHoldButNeverBacksOff()
        {
            var p = AnySim();
            p.NotifyHeavyFired();
            Observe(p, 0.6, Tick, true, HealthySim);    // a hot cost UNDER the breach bar, and one WE caused
            Assert.False(p.AllowHeavy);                        // held...
            Assert.Equal(Cadence.Working, p.NextCadence(true)); // ...yet still fast: a sub-breach fired cost doesn't back off
        }

        [Fact]
        public void BreachWhileFiringHeavyStillBacksOff()
        {
            var p = AnySim();
            p.NotifyHeavyFired();
            Observe(p, Breach, Tick, true, HealthySim);    // the heavy WE fired breached the bar — back off regardless (B.5)
            Assert.False(p.AllowHeavy);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
        }

        [Fact]
        public void SecondBreachInsideWindowEscalatesToPark()
        {
            var p = AnySim();
            Observe(p, Breach, Tick, true, HealthySim);              // first breach → recovering
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
            Observe(p, Breach, Tick, true, HealthySim);              // second breach still inside the window → parked
            Assert.Equal(Cadence.Parked, p.NextCadence(true));
        }

        [Fact]
        public void SubBreachSpikesDoNotBackOff()
        {
            var p = AnySim();
            Observe(p, 0.6, Tick, true, HealthySim);    // a sub-breach hot run we did not cause
            Observe(p, 0.7, Tick, true, HealthySim);    // another, still under the bar
            Assert.True(p.AllowHeavy);                          // never held — sub-0.8 runs are not limiter offenses
            Assert.Equal(Cadence.Working, p.NextCadence(true)); // stays fast; only a breach (> 0.8) backs off
        }

        [Fact]
        public void BreachAfterWindowClearsDoesNotEscalate()
        {
            var p = AnySim();
            Observe(p, Breach, Tick, true, HealthySim);    // first breach → recovering
            Observe(p, 0.0, 60.0, true, HealthySim);    // ride the whole window out — the escalation count resets with it
            Assert.True(p.AllowHeavy);
            Observe(p, Breach, Tick, true, HealthySim);    // a lone breach in a fresh window → recovering again, not idle
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
        }

        [Fact]
        public void StandDownHoldsHeavyAtU10ThenClears()
        {
            var p = new Pacer();
            p.StandDown();                                   // limiter's cooperative GracefulShutDown:: signal
            Observe(p, 0.0, Tick, true, HealthySim);
            Assert.False(p.AllowHeavy);                      // no heavy call while standing down
            Assert.Equal(Cadence.Recovering, p.NextCadence(true)); // cheap runs at U10 even with work pending
            Observe(p, 0.0, 3600.0, true, HealthySim);        // a long gap ages the stand-down away
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void CrashBacksOffHeavyThenClears()
        {
            var p = new Pacer();
            p.NotifyCrash();
            Observe(p, 0.0, Tick, true, HealthySim);
            Assert.False(p.AllowHeavy);   // don't fast-loop the crashing step
            Assert.False(p.Halted);       // one crash is not a halt — the back-off alone handles it
            Observe(p, 0.0, 10.0, true, HealthySim);   // ride out the crash back-off
            Assert.True(p.AllowHeavy);
        }

        [Fact]
        public void RepeatedCrashesHaltThenCleanRunRecovers()
        {
            var p = new Pacer();
            for (var i = 0; i < 20; i++) { p.NotifyCrash(); Observe(p, 0.0, Tick, true, HealthySim); } // total, uninterrupted failure
            Assert.True(p.Halted);
            Assert.Equal(Cadence.Parked, p.NextCadence(true)); // stop hammering a persistently-crashing step
            p.NotifyCleanRun();
            Assert.False(p.Halted);                          // a clean run closes the breaker
            Observe(p, 0.0, 10.0, true, HealthySim);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void CleanRunBetweenCrashesPreventsHalt()
        {
            var p = new Pacer();
            for (var i = 0; i < 20; i++)
            {
                p.NotifyCrash();
                Observe(p, 0.0, Tick, true, HealthySim);
                p.NotifyCleanRun();          // progress between crashes: isolated failures never accumulate to a halt
            }
            Assert.False(p.Halted);
        }

        [Fact]
        public void DipsBeforeTheWaryCountAreOnlyNotedAndFullSpeedContinues()
        {
            var p = new Pacer();
            Dips(p, Pacer.WaryDips - 1);                     // a few low samples are mostly measurement noise
            Assert.False(p.SimParked);
            Assert.False(p.SimWary);
            Assert.True(p.AllowHeavy);
            Assert.False(p.Throttled);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void WaryCountInsideWindowGoesWaryNotParked()
        {
            var p = new Pacer();
            Dips(p, Pacer.WaryDips);                         // repeating: heavies held, cadence merely slowed
            Assert.False(p.SimParked);
            Assert.True(p.SimWary);
            Assert.False(p.AllowHeavy);
            Assert.True(p.Throttled);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
        }

        [Fact]
        public void TheDipBeforeTheParkCountStaysWary()
        {
            var p = new Pacer();
            Dips(p, Pacer.ParkDips - 1);
            Assert.False(p.SimParked);
            Assert.True(p.SimWary);
        }

        [Fact]
        public void ParkCountInsideWindowFullParks()
        {
            var p = new Pacer();
            Dips(p, Pacer.ParkDips);                         // confirmed: sustained lag, not noise
            Assert.True(p.SimParked);
            Assert.False(p.AllowHeavy);
            Assert.Equal(Cadence.Parked, p.NextCadence(true));
        }

        [Fact]
        public void WindowExpiryForgetsCountedDips()
        {
            var p = new Pacer();
            Dips(p, Pacer.WaryDips);                         // wary...
            Observe(p, 0.0, 10.0, true, HealthySim);          // ...healthy past the window — all dips forgotten
            Assert.False(p.SimWary);
            Assert.False(p.SimParked);
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
            Dips(p, Pacer.WaryDips);                         // later dips start the count over
            Assert.False(p.SimParked);                       // fresh dips → wary, not the park the old count would bring
            Assert.True(p.SimWary);
        }

        [Fact]
        public void DipsFartherApartThanTheWindowNeverEscalate()
        {
            var p = new Pacer();
            for (var i = 0; i < 10; i++)
            {
                Observe(p, 0.0, Window, true, LaggedSim);     // an isolated dip...
                Observe(p, 0.0, 10.0, true, HealthySim);      // ...then healthy past the window, repeatedly
            }
            Assert.False(p.SimParked);
            Assert.False(p.SimWary);
            Assert.True(p.AllowHeavy);
        }

        [Fact]
        public void SimAvgIsNotBiasedLowBySymmetricNoise()
        {
            var p = new Pacer();
            for (var i = 0; i < 2000; i++)
            {
                Observe(p, 0.0, Tick, true, 1.1);   // quantization jitter around a truly healthy sim...
                Observe(p, 0.0, Tick, true, 0.9);   // ...must cancel in the average, not drag it down
            }
            Assert.True(p.SimAvg > 0.97, "avg " + p.SimAvg + " biased low");
            Assert.True(p.SimAvg < 1.03, "avg " + p.SimAvg + " biased high");
        }

        // A game paused and later resumed leaves game time frozen while the wall clock ran on, so the resume tick
        // measures a ratio orders of magnitude below any real slowdown. That interval is absent, not slow.
        const double PausedSim = 1.0 / 600.0;   // one tick of game time across ten minutes of wall clock

        [Fact]
        public void APauseGapNeitherCountsAsADipNorMovesTheAverage()
        {
            var p = new Pacer();
            for (var i = 0; i < 200; i++) Observe(p, 0.0, Tick, true, HealthySim);
            var before = p.SimAvg;
            Observe(p, 0.0, Tick, true, PausedSim);
            Assert.Equal(before, p.SimAvg, 6);           // the average is untouched, not cratered for ~50 s
            Assert.False(p.SimWary);
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void RepeatedPauseGapsNeverEscalateToAPark()
        {
            var p = new Pacer();
            for (var i = 0; i < 10; i++) Observe(p, 0.0, Tick, true, PausedSim);
            Assert.False(p.SimParked);
            Assert.False(p.SimWary);
            Assert.True(p.AllowHeavy);
        }

        [Fact]
        public void APauseGapCannotResumeAPark()
        {
            var p = new Pacer();
            Dips(p, Pacer.ParkDips);
            Assert.True(p.SimParked);
            Observe(p, 0.0, Tick, true, PausedSim);       // no information either way — the park must stand
            Assert.True(p.SimParked);
        }

        // Park, optionally take a pause gap, then count the healthy ticks needed to resume. Comparing the two
        // answers is self-calibrating: it pins "a pause costs nothing" without hard-coding the average's tau.
        static int HealthyTicksToResume(bool pauseAfterPark)
        {
            var p = new Pacer();
            Dips(p, Pacer.ParkDips);
            Assert.True(p.SimParked);
            if (pauseAfterPark) Observe(p, 0.0, Tick, true, PausedSim);
            var ticks = 0;
            while (p.SimParked && ticks < 100000) { Observe(p, 0.0, Tick, true, HealthySim); ticks++; }
            Assert.False(p.SimParked);   // it must resume at all, not just eventually
            return ticks;
        }

        [Fact]
        public void APauseGapDoesNotDelayARealResume()
        {
            Assert.Equal(HealthyTicksToResume(false), HealthyTicksToResume(true));
        }

        [Fact]
        public void TheReadoutKeepsTheLastRealSampleAcrossAPause()
        {
            var p = new Pacer();
            Observe(p, 0.0, Window, true, HealthySim);
            Observe(p, 0.0, Tick, true, PausedSim);
            Assert.Equal(HealthySim, p.SimInstant, 6);   // a pause artifact is never shown as a measurement
        }

        [Fact]
        public void GenuineDeepLagIsStillCountedAsADip()
        {
            // The floor must sit far below any slowdown a server could actually be helped through, so real lag
            // several times worse than the park bar still escalates normally.
            var p = new Pacer();
            for (var i = 0; i < Pacer.ParkDips; i++) Observe(p, 0.0, Window, true, 0.1);
            Assert.True(p.SimParked);
        }

        [Fact]
        public void DipWindowSlidesSoACrawlOfSpacedDipsStillEscalates()
        {
            var p = new Pacer();
            Observe(p, 0.0, Window, true, LaggedSim);
            for (var i = 1; i < Pacer.ParkDips; i++)
                Observe(p, 0.0, 3.0, true, LaggedSim);        // 3 s apart — each inside the window the previous refreshed
            Assert.True(p.SimParked);                        // dips pairwise close → sustained lag
        }

        [Fact]
        public void SimDipAboveParkBarDoesNotPark()
        {
            var p = new Pacer();
            Observe(p, 0.0, Window, true, 0.77);                // between park (0.75) and resume (0.8) from healthy — no park
            Assert.False(p.SimParked);
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void OneHealthySampleDoesNotResumeAPark()
        {
            var p = ParkedPacer();
            Observe(p, 0.0, Window, true, HealthySim);        // the average was seeded to the dip — one good sample is noise
            Assert.True(p.SimParked);
            Assert.False(p.AllowHeavy);
        }

        [Fact]
        public void SustainedHealthySimResumesAPark()
        {
            var p = ParkedPacer();
            var observations = 0;
            while (p.SimParked)
            {
                Observe(p, 0.0, 1.0, true, HealthySim);       // a second of healthy sim per observation
                observations++;
                Assert.True(observations < 1000, "sim park never resumed");
            }
            Assert.True(observations > 3, "resume must take sustained recovery, not a couple of samples");
            Assert.True(p.AllowHeavy);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void SimHoveringBelowResumeBarStaysParked()
        {
            var p = ParkedPacer();
            for (var i = 0; i < 200; i++)
                Observe(p, 0.0, 1.0, true, 0.78);             // above park but the average can never clear resume (0.8)
            Assert.True(p.SimParked);
            Assert.Equal(Cadence.Parked, p.NextCadence(true));
        }

        [Fact]
        public void FreshDipDuringRecoveryReparksInstantly()
        {
            var p = ParkedPacer();
            for (var i = 0; i < 4; i++) Observe(p, 0.0, 1.0, true, HealthySim); // partway through recovery...
            Observe(p, 0.0, 1.0, true, LaggedSim);                              // ...one fresh dip parks again at once
            Assert.True(p.SimParked);
            Assert.False(p.AllowHeavy);
        }

        // A back-off confined to lag, for a limiter that enforces only there.
        static Pacer GatedBelow(double backoffSim)
        {
            var p = new Pacer();
            p.SetBars(Pacer.DefaultPeakMs, Pacer.DefaultParkSim, Pacer.DefaultResumeSim, backoffSim);
            return p;
        }

        [Fact]
        public void ByDefaultABreachAtFullSpeedBacksOff()
        {
            var p = new Pacer();                              // Destiny's limiter enforces at any sim speed
            Observe(p, Breach, Window, true, HealthySim);
            Assert.Equal(1, p.Backoffs);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
        }

        [Fact]
        public void UnderLagARunIsChargedDividedBySim()
        {
            // The limiter divides each run's ms by the sim ratio: a run under the bar at full speed is over it here.
            var p = new Pacer();
            Observe(p, Pacer.DefaultPeakMs * 0.8, Window, true, 0.7);
            Assert.Equal(1, p.Backoffs);
            var q = new Pacer();
            Observe(q, Pacer.DefaultPeakMs * 0.8, Window, true, HealthySim);
            Assert.Equal(0, q.Backoffs);
        }

        [Fact]
        public void AFastSampleNeverShrinksTheCharge()
        {
            var p = new Pacer();
            Observe(p, Pacer.DefaultPeakMs * 0.95, Window, true, 1.3);   // timer jitter over full speed
            Assert.Equal(0, p.Backoffs);
        }

        [Fact]
        public void ABreachAtAHealthySimDoesNotBackOffBelowAGatedBar()
        {
            var p = GatedBelow(0.9);
            Observe(p, Breach, Window, true, HealthySim);
            Assert.True(p.AllowHeavy);
            Assert.Equal(0, p.Backoffs);
            Assert.Equal(Cadence.Working, p.NextCadence(true));
        }

        [Fact]
        public void BreachBelowAGatedBarBacksOffEvenAboveTheResumeBar()
        {
            var p = GatedBelow(0.9);
            Observe(p, Breach, Window, true, 0.85);           // no dip and no park — only the back-off acts
            Assert.False(p.SimWary);
            Assert.Equal(1, p.Backoffs);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
        }

        [Fact]
        public void ALowSimAverageStillArmsAGatedBackoffOnAHealthySample()
        {
            var p = GatedBelow(0.9);
            for (var i = 0; i < 50; i++) Observe(p, 0.0, 1.0, true, 0.78);  // mild lag: no dip, yet the average sinks
            Observe(p, Breach, Window, true, HealthySim);     // a healthy newest sample, a not-yet-healthy average
            Assert.Equal(HealthySim, p.SimInstant, 6);
            Assert.True(p.SimAvg < 0.9, "avg " + p.SimAvg);
            Assert.Equal(1, p.Backoffs);
        }

        [Fact]
        public void ABackoffBarOfOneBacksOffAtAnySim()
        {
            var p = AnySim();
            Observe(p, Breach, Window, true, 1.05);           // timer jitter can read a full-speed sample over 1
            Assert.Equal(1, p.Backoffs);
            Assert.Equal(Cadence.Recovering, p.NextCadence(true));
        }

        [Fact]
        public void ABackoffBarOfZeroNeverBacksOff()
        {
            var p = GatedBelow(0.0);
            Observe(p, Breach, Window, true, LaggedSim);
            Assert.Equal(0, p.Backoffs);
        }

        // Healthy ticks and one that took ~1.8 ticks of wall time — one slow server tick at a healthy sim.
        const double SlowTickSim = 0.55;

        static void WindowWithOneSlowTick(Pacer p)
        {
            for (var i = 0; i < WindowTicks - 1; i++) Observe(p, 0.0, Tick, true, HealthySim);
            Observe(p, 0.0, Tick, true, SlowTickSim);
        }

        [Fact]
        public void OneSlowTickInsideAWindowIsNotADip()
        {
            var p = new Pacer();
            for (var i = 0; i < Pacer.ParkDips; i++) WindowWithOneSlowTick(p);
            Assert.False(p.SimWary);
            Assert.False(p.SimParked);
            Assert.Equal(WindowTicks / (WindowTicks - 1 + 1 / SlowTickSim), p.SimInstant, 6);
            Assert.Equal(0, p.Waries);
        }

        [Fact]
        public void ASampleWaitsForAFullWindow()
        {
            var p = new Pacer();
            for (var i = 0; i < WindowTicks - 1; i++) Observe(p, 0.0, Tick, true, LaggedSim);
            Assert.Equal(1.0, p.SimInstant, 6);              // one tick short is not a window yet
            Observe(p, 0.0, Tick, true, LaggedSim);
            Assert.Equal(LaggedSim, p.SimInstant, 6);
        }

        [Fact]
        public void SustainedLagAtUpdate1GoesWaryThenParksWithinAFewWindows()
        {
            var p = new Pacer();
            for (var i = 0; i < WindowTicks * Pacer.WaryDips; i++) Observe(p, 0.0, Tick, true, LaggedSim);
            Assert.True(p.SimWary);
            for (var i = 0; i < WindowTicks * (Pacer.ParkDips - Pacer.WaryDips); i++) Observe(p, 0.0, Tick, true, LaggedSim);
            Assert.True(p.SimParked);
        }

        [Fact]
        public void ZeroWallTicksFromTimerQuantizationAverageOutInsideAWindow()
        {
            var p = new Pacer();
            for (var i = 0; i < 200; i++)
            {
                p.Observe(0.0, Tick, true, 0.0, false);          // the clock had not ticked over...
                p.Observe(0.0, Tick, true, 2 * Tick, false);     // ...so the next interval carries both
            }
            Assert.False(p.SimWary);
            Assert.True(p.SimAvg > 0.97 && p.SimAvg < 1.03, "avg " + p.SimAvg);
        }

        [Fact]
        public void APauseInsideAWindowDropsTheWholeWindow()
        {
            var p = new Pacer();
            for (var i = 0; i < WindowTicks - 1; i++) Observe(p, 0.0, Tick, true, LaggedSim);
            Observe(p, 0.0, Tick, true, PausedSim);         // the partial window goes with it
            Observe(p, 0.0, Tick, true, LaggedSim);
            Assert.Equal(1.0, p.SimInstant, 6);              // carried over, these ticks would have closed a window
            for (var i = 0; i < WindowTicks - 1; i++) Observe(p, 0.0, Tick, true, LaggedSim);
            Assert.Equal(LaggedSim, p.SimInstant, 6);
        }

        [Fact]
        public void AWindowHoldingOurOwnSpikeIsNoDip()
        {
            // A cold first run of tens of ms stretches the wall time of the window it lands in; at the server's
            // real sim of 1.0 that reads as deep lag, but it is our own doing and says nothing about the server.
            var p = new Pacer();
            for (var i = 0; i < Pacer.ParkDips; i++) Observe(p, SpikeRun, Window, false, 0.4);
            Assert.False(p.SimWary);
            Assert.False(p.SimParked);
            Assert.Equal(1.0, p.SimInstant, 6);
            Assert.Equal(1.0, p.SimAvg, 6);
            var q = new Pacer();                                 // the same windows without our spike are dips
            for (var i = 0; i < Pacer.ParkDips; i++) Observe(q, 0.0, Window, false, 0.4);
            Assert.True(q.SimParked);
        }

        [Fact]
        public void AWindowAfterOurSpikeCountsAgain()
        {
            var p = new Pacer();
            Observe(p, SpikeRun, Window, false, 0.4);            // dropped
            Observe(p, 0.0, Window, false, LaggedSim);           // a real dip in the next window
            Assert.Equal(LaggedSim, p.SimInstant, 6);
        }

        [Fact]
        public void AClockRunningBackwardsDropsTheWindow()
        {
            var p = new Pacer();
            for (var i = 0; i < WindowTicks - 1; i++) Observe(p, 0.0, Tick, true, LaggedSim);
            p.Observe(0.0, Tick, true, -1.0, false);
            Observe(p, 0.0, Tick, true, LaggedSim);
            Assert.Equal(1.0, p.SimInstant, 6);
        }

        // --- Server-matched bars ([Settings] peakMs / parkSim / resumeSim) ------------------------------------

        [Fact]
        public void ARaisedPeakBarIgnoresRunsUnderIt()
        {
            var p = new Pacer();
            p.SetBars(2.0, Pacer.DefaultParkSim, Pacer.DefaultResumeSim, 1.0);
            Observe(p, 1.5, Tick, true, HealthySim);
            Assert.Equal(0, p.Backoffs);
            Observe(p, 2.5, Tick, true, HealthySim);
            Assert.Equal(1, p.Backoffs);
        }

        [Fact]
        public void ParkSimZeroNeverParks()
        {
            var p = new Pacer();
            p.SetBars(Pacer.DefaultPeakMs, 0.0, 0.0, Pacer.DefaultBackoffSim);
            for (var i = 0; i < 10; i++) Observe(p, 0.0, Window, true, LaggedSim);
            Assert.False(p.SimParked);
            Assert.False(p.SimWary);
            Assert.True(p.AllowHeavy);
        }

        [Fact]
        public void ALowerParkBarLetsMildLagRunAndResumesAtItsOwnBar()
        {
            var p = new Pacer();
            p.SetBars(Pacer.DefaultPeakMs, 0.5, 0.55, Pacer.DefaultBackoffSim);
            for (var i = 0; i < 10; i++) Observe(p, 0.0, Window, true, LaggedSim); // 0.6 is above the lowered bar
            Assert.False(p.SimParked);
            for (var i = 0; i < Pacer.ParkDips; i++) Observe(p, 0.0, Window, true, 0.3);
            Assert.True(p.SimParked);
            var ticks = 0;
            while (p.SimParked && ticks++ < 100000) Observe(p, 0.0, Tick, true, LaggedSim); // 0.6 clears the 0.55 resume bar
            Assert.False(p.SimParked);
        }
    }
}
