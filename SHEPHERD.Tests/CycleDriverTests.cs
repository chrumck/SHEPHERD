using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class CycleDriverTests
    {
        // Yields a scripted result sequence; IsDone once the script is exhausted, so a job whose last unit ran
        // this tick reports IsDone alongside that unit's (non-Done) result — the SortJob "Moved on the final
        // item" shape. An empty script is a job that is done before ever being stepped.
        sealed class FakeJob : IStepJob
        {
            readonly List<StepResult> script = new List<StepResult>();
            int cursor;
            public int Steps;

            public FakeJob(params StepResult[] results) { if (results != null) script.AddRange(results); }

            public bool IsDone => cursor >= script.Count;

            public StepResult Step()
            {
                Steps++;
                return cursor < script.Count ? script[cursor++] : StepResult.Done;
            }
        }

        [Fact]
        public void ScopeToleratesSmallLossesAndGrowth()
        {
            Assert.False(Program.ScopeShrank(526, 526));
            Assert.False(Program.ScopeShrank(526, 501));      // 25 of 526 — under the 5% bar, skip/spill absorbs it
            Assert.False(Program.ScopeShrank(526, 900));      // growth strands nothing
            Assert.False(Program.ScopeShrank(20, 16));        // 4 of 20 — the floor, not the fraction, governs here
        }

        [Fact]
        public void ScopeTripsOnAMaterialLoss()
        {
            Assert.True(Program.ScopeShrank(526, 499));       // 27 of 526 — just past the 26-block bar
            Assert.True(Program.ScopeShrank(526, 76));        // a deleted 450-container grid
            Assert.True(Program.ScopeShrank(20, 15));         // small grid: the floor of 4 still catches it
            Assert.True(Program.ScopeShrank(526, 0));
        }

        [Fact]
        public void EmptyDriverHasNoWorkAndStepsHarmlessly()
        {
            var driver = new CycleDriver();

            Assert.False(driver.HasWork);
            Assert.Equal(StepResult.Done, driver.Step());
            Assert.Null(driver.LastCompleted);
            Assert.Null(driver.Current);
        }

        [Fact]
        public void NullEnqueueIsIgnored()
        {
            var driver = new CycleDriver();
            driver.Enqueue(null);

            Assert.False(driver.HasWork);
        }

        [Fact]
        public void SingleJobStepsToCompletionAndSurfacesLastCompleted()
        {
            var job = new FakeJob(StepResult.Scanned, StepResult.Moved);
            var driver = new CycleDriver();
            driver.Enqueue(job);

            Assert.True(driver.HasWork);
            Assert.Equal(StepResult.Scanned, driver.Step());
            Assert.Null(driver.LastCompleted);              // mid-job: nothing completed yet
            Assert.Same(job, driver.Current);

            Assert.Equal(StepResult.Moved, driver.Step());  // final unit: IsDone leads the non-Done result
            Assert.Same(job, driver.LastCompleted);
            Assert.Null(driver.Current);
            Assert.False(driver.HasWork);
        }

        [Fact]
        public void ExplicitDoneReturnAlsoCompletesTheJob()
        {
            var job = new FakeJob(StepResult.Done);
            var driver = new CycleDriver();
            driver.Enqueue(job);

            Assert.Equal(StepResult.Done, driver.Step());
            Assert.Same(job, driver.LastCompleted);
            Assert.False(driver.HasWork);
        }

        [Fact]
        public void JobsHandOffInOrderAndNeverShareATick()
        {
            var a = new FakeJob(StepResult.Moved);
            var b = new FakeJob(StepResult.Moved);
            var driver = new CycleDriver();
            driver.Enqueue(a);
            driver.Enqueue(b);

            Assert.Equal(StepResult.Moved, driver.Step()); // a's only unit — b must NOT also run this tick
            Assert.Same(a, driver.LastCompleted);
            Assert.Equal(0, b.Steps);
            Assert.True(driver.HasWork);

            Assert.Equal(StepResult.Moved, driver.Step());
            Assert.Same(b, driver.LastCompleted);
            Assert.False(driver.HasWork);
        }

        [Fact]
        public void LastCompletedIsClearedByTheNextStep()
        {
            var job = new FakeJob(StepResult.Moved);
            var driver = new CycleDriver();
            driver.Enqueue(job);
            driver.Step();
            Assert.Same(job, driver.LastCompleted);

            driver.Step();                                  // empty tick
            Assert.Null(driver.LastCompleted);
        }

        [Fact]
        public void CompletionHandlerEnqueueRunsOnTheNextTick()
        {
            // The scheduler's shape: step, see LastCompleted, enqueue the next phase — which runs next tick.
            var a = new FakeJob(StepResult.Classified);
            var b = new FakeJob(StepResult.Found);
            var driver = new CycleDriver();
            driver.Enqueue(a);

            Assert.Equal(StepResult.Classified, driver.Step());
            Assert.Same(a, driver.LastCompleted);
            driver.Enqueue(b);
            Assert.Equal(0, b.Steps);

            Assert.Equal(StepResult.Found, driver.Step());
            Assert.Same(b, driver.LastCompleted);
        }

        [Fact]
        public void ClearDropsCurrentQueueAndLastCompleted()
        {
            var a = new FakeJob(StepResult.Scanned, StepResult.Moved);
            var b = new FakeJob(StepResult.Moved);
            var driver = new CycleDriver();
            driver.Enqueue(a);
            driver.Enqueue(b);
            driver.Step();                                  // a is mid-flight

            driver.Clear();
            Assert.False(driver.HasWork);
            Assert.Null(driver.Current);
            Assert.Null(driver.LastCompleted);
            Assert.Equal(StepResult.Done, driver.Step());
            Assert.Equal(1, a.Steps);                       // never resumed
            Assert.Equal(0, b.Steps);                       // never started
        }

        [Fact]
        public void ClearAfterCompletionForgetsLastCompleted()
        {
            var job = new FakeJob(StepResult.Moved);
            var driver = new CycleDriver();
            driver.Enqueue(job);
            driver.Step();
            Assert.Same(job, driver.LastCompleted);

            driver.Clear();
            Assert.Null(driver.LastCompleted);
        }

        [Fact]
        public void AlreadyDoneJobCompletesWithoutBeingStepped()
        {
            var done = new FakeJob();                       // IsDone before ever running
            var driver = new CycleDriver();
            driver.Enqueue(done);

            Assert.Equal(StepResult.Done, driver.Step());
            Assert.Same(done, driver.LastCompleted);        // its completion handler still fires
            Assert.Equal(0, done.Steps);                    // but it was never stepped
            Assert.False(driver.HasWork);
        }

        [Fact]
        public void SortAndBottleJobsImplementTheSeam()
        {
            // The retrofit contract: the real cross-tick jobs are steppable through the driver unchanged.
            var driver = new CycleDriver();
            driver.Enqueue(new SortJob(null, null, 0));
            driver.Enqueue(new BottleJob(null, null, 0));

            Assert.Equal(StepResult.Done, driver.Step());   // SortJob with null inputs is done immediately
            Assert.IsType<SortJob>(driver.LastCompleted);
            Assert.Equal(StepResult.Done, driver.Step());
            Assert.IsType<BottleJob>(driver.LastCompleted);
            Assert.False(driver.HasWork);
        }
    }
}
