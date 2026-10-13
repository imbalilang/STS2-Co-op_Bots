using System.Diagnostics;

namespace CoopBots.Kernel;

/// <summary>
/// One settled live root and one absolute deadline, shared by every search pass.
/// Cancellation is a scheduling signal; native actions are allowed to settle.
/// </summary>
public sealed class KernelDecisionRequest
{
    // Upstream SearchBudgetLedger binds its request clock to one request limit.
    // We bind the same wall-clock allowance to a multiplayer root generation,
    // also observed by pooled rollout jobs. Owner's staged refactor, 2026-10-09:
    // a new live root must not leave the abandoned root's workers computing.
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationToken cancellation;
    private string? cancelled;
    private int outstandingJobs, activeJobs, peakActiveJobs, completedJobs;

    public KernelDecisionRequest(long rootGeneration, int budgetMs, int maxConcurrency = 16,
        CancellationToken cancellation = default)
    {
        if (budgetMs < 0) throw new ArgumentOutOfRangeException(nameof(budgetMs));
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        RootGeneration = rootGeneration;
        BudgetMs = budgetMs;
        MaxConcurrency = maxConcurrency;
        this.cancellation = cancellation;
    }

    public long RootGeneration { get; }
    public int BudgetMs { get; }
    public int MaxConcurrency { get; }
    public double ElapsedMs => clock.Elapsed.TotalMilliseconds;
    public int RemainingMs => (int)Math.Max(0, Math.Ceiling(BudgetMs - ElapsedMs));
    public bool IsCancelled => Volatile.Read(ref cancelled) is not null || cancellation.IsCancellationRequested;
    public bool DeadlineReached => ElapsedMs >= BudgetMs;
    public bool IsStopped => IsCancelled || DeadlineReached;
    public string StopReason => IsCancelled
        ? "request-cancelled:" + (Volatile.Read(ref cancelled) ?? "external-token")
        : DeadlineReached ? "request-deadline" : "pending";
    public int ActiveJobs => Volatile.Read(ref activeJobs);
    public int QueuedJobs => Math.Max(0, Volatile.Read(ref outstandingJobs) - ActiveJobs);
    public int PeakActiveJobs => Volatile.Read(ref peakActiveJobs);
    public int CompletedJobs => Volatile.Read(ref completedJobs);

    public void Cancel(string reason = "cancelled")
        => Interlocked.CompareExchange(ref cancelled, string.IsNullOrEmpty(reason) ? "cancelled" : reason, null);

    // Reservation counts queued + executing jobs. It does not reserve time or
    // start a new deadline, and is never used as evidence of a native ending.
    internal bool TryReserveJob()
    {
        while (!IsStopped)
        {
            var current = Volatile.Read(ref outstandingJobs);
            if (current >= MaxConcurrency) return false;
            if (Interlocked.CompareExchange(ref outstandingJobs, current + 1, current) == current) return true;
        }
        return false;
    }

    internal void StartJob()
    {
        var active = Interlocked.Increment(ref activeJobs);
        var peak = Volatile.Read(ref peakActiveJobs);
        while (active > peak)
        {
            var previous = Interlocked.CompareExchange(ref peakActiveJobs, active, peak);
            if (previous == peak) break;
            peak = previous;
        }
    }

    internal void FinishJob()
    {
        Interlocked.Decrement(ref activeJobs);
        Interlocked.Decrement(ref outstandingJobs);
        Interlocked.Increment(ref completedJobs);
    }
}
