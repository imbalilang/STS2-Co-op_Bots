namespace CoopBots.Kernel;

/// <summary>
/// Bounded, reusable BelowNormal workers. No blocking enqueue on the game thread.
/// A rejected enqueue leaves the caller's owned rollout available for retry.
/// </summary>
internal sealed class KernelSearchWorkPool
{
    // Upstream owns cancellation at the request and drains parallel expansion.
    // This facade previously created a thread for each rollout. We retain its
    // private forks and launch-order collection, but bound workers and queued
    // jobs across root generations (owner's staged refactor, 2026-10-09).
    internal static KernelSearchWorkPool Shared { get; } = new(
        Math.Clamp(Environment.ProcessorCount - 2, 1, 16), 16);
    private readonly object gate = new();
    private readonly Queue<WorkItem> queue = new();
    private readonly int maxWorkers, capacity;
    private int workers, active, peakActive;

    internal KernelSearchWorkPool(int maxWorkers, int capacity)
    {
        if (maxWorkers < 1) throw new ArgumentOutOfRangeException(nameof(maxWorkers));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.maxWorkers = maxWorkers; this.capacity = capacity;
    }

    internal int ActiveJobs { get { lock (gate) return active; } }
    internal int QueuedJobs { get { lock (gate) return queue.Count; } }
    internal int PeakActiveJobs { get { lock (gate) return peakActive; } }
    internal int WorkerLimit => maxWorkers;

    internal sealed class WorkItem(KernelDecisionRequest? request, Action work)
    {
        internal KernelDecisionRequest? Request { get; } = request;
        internal Action Work { get; } = work;
        internal Exception? Error { get; set; }
        internal volatile bool Finished;
    }

    internal bool TryQueue(KernelDecisionRequest? request, Action work, out WorkItem? item)
    {
        ArgumentNullException.ThrowIfNull(work);
        item = null;
        lock (gate)
        {
            if (queue.Count >= capacity) return false;
            // Start before admitting work. If the runtime cannot create a
            // worker, no request reservation or stranded queue entry exists.
            if (workers < maxWorkers && workers < active + queue.Count + 1)
            {
                var worker = new Thread(Drain) {
                    IsBackground = true, Priority = ThreadPriority.BelowNormal,
                    Name = "CoopBots search worker",
                };
                // Long-lived workers must not retain the first creator's
                // decision context. Each queued delegate installs its own.
                if (ExecutionContext.IsFlowSuppressed()) worker.Start();
                else { using var flow = ExecutionContext.SuppressFlow(); worker.Start(); }
                workers++;
            }
            if (request is not null && !request.TryReserveJob()) return false;
            item = new(request, work);
            queue.Enqueue(item);
            Monitor.Pulse(gate);
            return true;
        }
    }

    private void Drain()
    {
        while (true)
        {
            WorkItem item;
            lock (gate)
            {
                while (queue.Count == 0) Monitor.Wait(gate);
                item = queue.Dequeue(); active++;
                peakActive = Math.Max(peakActive, active);
            }
            item.Request?.StartJob();
            try
            {
                using var budgetScope = KernelSearchBudgetScope.Enter(item.Request);
                item.Work();
            }
            catch (Exception error) { item.Error = error; }
            finally
            {
                item.Request?.FinishJob();
                lock (gate) active--;
                // Publish the exception and finished telemetry only after the
                // worker has relinquished its slot. Collection never blocks.
                item.Finished = true;
            }
        }
    }
}
