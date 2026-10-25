using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace CoopBots.Kernel;

/// <summary>
/// A frozen simulator has one owner for its entire lifetime. Poll never runs
/// simulation or waits for a worker. The controller pool is separate from the
/// rollout pool: a controller waiting for its lanes cannot occupy their slots.
/// </summary>
public sealed class KernelBackgroundOperation(Action work, KernelDecisionRequest? request = null)
{
    private static readonly KernelSearchWorkPool controllers = new(1, 4);
    public static bool IsIdle => controllers.ActiveJobs == 0 && controllers.QueuedJobs == 0;
    private Action? work = work;
    private KernelSearchWorkPool.WorkItem? item;
    private ExceptionDispatchInfo? error;
    private int completed;
    public bool IsCompleted => Volatile.Read(ref completed) != 0;
    public int WorkerThreadId { get; private set; }
    public double WorkerMs { get; private set; }
    public long AllocatedBytes { get; private set; }

    public bool Poll()
    {
        if (item is null && !controllers.TryQueue(null, Execute, out item)) return false;
        if (!IsCompleted) return false;
        error?.Throw();
        return true;
    }

    private void Execute()
    {
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        WorkerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            using var scope = KernelSearchBudgetScope.Enter(request);
            work!();
        }
        catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
        finally
        {
            work = null; // Do not retain an abandoned combat through the queue item.
            WorkerMs = clock.Elapsed.TotalMilliseconds;
            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Volatile.Write(ref completed, 1);
        }
    }

    // Yield only between settled native transactions. This also prevents a
    // coordinator with no ready lanes from spinning and stealing an entire CPU.
    public static void Yield() => Thread.Sleep(1);
}

public sealed class KernelBackgroundValue<T>
{
    private T value = default!;
    private readonly KernelBackgroundOperation operation;
    public KernelBackgroundValue(Func<T> work, KernelDecisionRequest? request = null)
        => operation = new(() => value = work(), request);
    public bool Poll() => operation.Poll();
    public T Value => operation.IsCompleted ? value : throw new InvalidOperationException("Background result is not published.");
    public KernelBackgroundOperation Operation => operation;
}
