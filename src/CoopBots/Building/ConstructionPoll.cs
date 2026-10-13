namespace CoopBots.Building;

// Tick-driven screens must poll, never wait synchronously for worker results.
// A changed screen/state cancels the old calculation and discards its result.
internal sealed class ConstructionPoll<T>
{
    private string? stamp;
    private CancellationTokenSource? cancellation;
    private Task<T>? pending;
    internal void Clear()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        // Observe a displaced fault without running native/game code here.
        if (pending is { } old) _ = old.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        pending = null; stamp = null;
    }
    internal bool TryGet(string state, Func<CancellationToken, Task<T>> start, out T result)
    {
        if (stamp != state || pending is null)
        {
            Clear(); stamp = state; cancellation = new(); pending = start(cancellation.Token);
        }
        result = default!;
        if (!pending.IsCompleted) return false;
        if (pending.IsCanceled) { Clear(); return false; }
        result = pending.GetAwaiter().GetResult(); // Only after IsCompleted; never blocks.
        return true;
    }
}
