namespace CoopBots;

// One clock for the entire bot team: three bots must not produce three cards per second.
// Re-evaluate the interval each tick so the last human ending immediately shortens the wait.
// The interval is supplied by the caller from the team's difficulty tier.
internal sealed class CombatPacing
{
    // Used to bound the plan-time discount; must be the largest possible interval
    // so a slow plan can never shorten the wait below zero on any tier.
    private const int MaxIntervalMs = 1500;
    // Fraction of the base interval that remains once every human has finished.
    // Zero: from that point the team is the only thing still acting, so it must
    // play out its remaining cards as fast as it can think, not trickle them out
    // at the normal human-facing cadence.
    private const double FinishedSpeedup = 0.0;
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
    internal bool IsDue(long now, bool humansFinished, bool paused, int intervalMs)
        => !paused && _combat is not null && now - _lastActionAt >=
            Math.Max(0, Effective(intervalMs, humansFinished) - _lastPlanMs);

    internal static int Effective(int intervalMs, bool humansFinished)
        => humansFinished ? (int)(intervalMs * FinishedSpeedup) : intervalMs;

    internal void MarkAction(long now, long planStartedAt)
    {
        // A missing stamp must not look like an hour-long plan and zero the wait.
        _lastPlanMs = planStartedAt <= 0 ? 0 : Math.Clamp(now - planStartedAt, 0, MaxIntervalMs);
        _lastActionAt = now;
    }
    internal void Reset() { _combat = null; _round = 0; _lastActionAt = 0; _lastPlanMs = 0; }
}
