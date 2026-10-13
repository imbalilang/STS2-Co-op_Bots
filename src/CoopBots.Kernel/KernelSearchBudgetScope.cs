namespace CoopBots.Kernel;

/// <summary>
/// The current decision's existing absolute deadline, including native async
/// choice tails. This carries no simulated state or numerical evaluation cache.
/// </summary>
public static class KernelSearchBudgetScope
{
    // A native task can finish its hook/choice tail on a continuation thread.
    // ExecutionContext keeps that same request while parallel decisions retain
    // their own frames; disposing the caller restores only its logical context.
    private sealed record Frame(KernelDecisionRequest Request);
    private static readonly AsyncLocal<Frame?> active = new();
    internal static KernelDecisionRequest? Current => active.Value?.Request;
    internal static bool IsStopped => Current?.IsStopped == true;

    // Legacy children with no explicit request inherit their enclosing search;
    // standalone legacy calls still have no clock and retain the old step caps.
    public static IDisposable Enter(KernelDecisionRequest? request)
    {
        if (request is null) return EmptyScope.Instance;
        var previous = active.Value;
        active.Value = new(request);
        return new Scope(previous);
    }

    private sealed class Scope(Frame? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            active.Value = previous;
        }
    }
    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
