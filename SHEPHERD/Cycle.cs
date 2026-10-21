using System.Collections.Generic;

namespace IngameScript
{
    // The outcome of one stepper tick — one vocabulary for every cross-tick job, so the scheduler maps results
    // to run kinds and pacing through a single seam instead of per-job enums. Fetched = the block sweep ran
    // (heavy); Classified = a classify batch or the cycle build ran (heavy); Found = a transfer was prepared on
    // the cheap find tick (the NEXT step commits it); Moved = the prepared transfer fired and moved something
    // (the one bare heavy tick); Stalled = the transfer fired but moved nothing — the target reported room yet
    // the conveyor call failed (unreachable / sorter-filtered), so it cost a transfer but is NOT progress (a
    // stall-only cycle parks at idle instead of hammering a dead target at the fast cadence); Refilled = a tank
    // was force-filled (cheap, synchronous); Scanned = the examine budget was hit mid-walk with no transfer;
    // Planned = the production-plan tick ran; Done = nothing left.
    public enum StepResult { Fetched, Classified, Found, Moved, Stalled, Refilled, Scanned, Planned, Done }

    // A resumable cross-tick job the scheduler steps one bounded unit at a time (at most one heavy engine call
    // per Step). IsDone may lead Step's Done return: a job can complete on the same tick its last unit ran.
    public interface IStepJob
    {
        StepResult Step();
        bool IsDone { get; }
    }

    // The scheduler's job queue: cross-tick jobs run one at a time, in enqueue order, one bounded Step per tick.
    // The driver never touches pacing or game state — the scheduler maps each StepResult to run kinds and Pacer
    // calls, and inspects LastCompleted to run a finished job's completion handling (diagnostics, enqueueing the
    // next phase). A job finishing on a tick does NOT start the next job that same tick (one concern per tick).
    public sealed class CycleDriver
    {
        readonly List<IStepJob> queue = new List<IStepJob>();
        IStepJob current;

        public IStepJob Current => current;

        // The job that finished on the most recent Step; cleared by the next Step, so completion handling runs
        // exactly once, on the tick the job finished.
        public IStepJob LastCompleted { get; private set; }

        public bool HasWork => current != null || queue.Count > 0;

        public void Enqueue(IStepJob job)
        {
            if (job != null) queue.Add(job);
        }

        // Drop everything mid-cycle (crash / stand-down); the next cycle starts from a fresh enqueue.
        public void Clear()
        {
            current = null;
            queue.Clear();
            LastCompleted = null;
        }

        public StepResult Step()
        {
            LastCompleted = null;
            if (current == null)
            {
                if (queue.Count == 0) return StepResult.Done;
                current = queue[0];
                queue.RemoveAt(0);
                // An already-done job never runs, but it still surfaces as completed so its handler fires.
                if (current.IsDone) { Complete(); return StepResult.Done; }
            }
            var r = current.Step();
            if (r == StepResult.Done || current.IsDone) Complete();
            return r;
        }

        void Complete()
        {
            LastCompleted = current;
            current = null;
        }
    }
}
