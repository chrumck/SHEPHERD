using System.Collections.Generic;

namespace IngameScript
{
    // One vocabulary for every cross-tick job, so the scheduler maps results to run kinds and pacing in one place.
    // Fetched and Classified ran a heavy step; Found prepared a transfer the NEXT step commits; Moved is that bare
    // heavy commit; Stalled fired it but moved nothing (unreachable / sorter-filtered) — a transfer's cost but NOT
    // progress, so a stall-only cycle parks at idle instead of hammering a dead target; Scanned hit the examine budget
    // with no transfer.
    public enum StepResult { Fetched, Classified, Found, Moved, Stalled, Refilled, Scanned, Planned, Done }

    // At most one heavy engine call per Step. IsDone may lead Step's Done return: a job can complete on the same tick
    // its last unit ran.
    public interface IStepJob
    {
        StepResult Step();
        bool IsDone { get; }
    }

    // A job finishing on a tick does NOT start the next one that tick (one concern per tick).
    public class CycleDriver
    {
        readonly List<IStepJob> queue = new List<IStepJob>();
        IStepJob current;

        public IStepJob Current => current;

        // Cleared by the next Step, so completion handling runs exactly once, on the tick the job finished.
        public IStepJob LastCompleted;

        public bool HasWork => current != null || queue.Count > 0;

        public void Enqueue(IStepJob job)
        {
            if (job != null) queue.Push(job);
        }

        public void Clear()
        {
            current = null;
            queue.Empty();
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
