namespace CoopBots;

// One clock for the entire bot team: three bots must not produce three cards per second.
// Re-evaluate the interval each tick so the last human ending immediately shortens the wait.
internal sealed class CombatPacing
{
    internal const int NormalIntervalMs = 1000;
    internal const int FastIntervalMs = 200;
    private object? _combat;
    private int _round;
    private long _lastActionAt;
    private long _lastPlanMs;

    internal void Observe(object combat, int round, long now)
    {
        if (ReferenceEquals(combat, _combat) && round == _round) return;
        _combat = combat; _round = round; _lastActionAt = now; _lastPlanMs = 0;
    }

    // Planning happens after the wait, so the cadence would be interval + plan.
    // Subtracting how long the previous plan took makes the wait and the search
    // overlap: the effective cadence is max(interval, plan), which keeps the
    // pacing for fast planners but stops a slow kernel search from paying for
    // both. The interval still sets the minimum spacing between actions.
    internal bool IsDue(long now, bool humansFinished, bool paused)
        => !paused && _combat is not null && now - _lastActionAt >=
            Math.Max(0, (humansFinished ? FastIntervalMs : NormalIntervalMs) - _lastPlanMs);

    internal void MarkAction(long now, long planStartedAt)
    {
        // A missing stamp must not look like an hour-long plan and zero the wait.
        _lastPlanMs = planStartedAt <= 0 ? 0 : Math.Clamp(now - planStartedAt, 0, NormalIntervalMs);
        _lastActionAt = now;
    }
    internal void Reset() { _combat = null; _round = 0; _lastActionAt = 0; _lastPlanMs = 0; }
}
