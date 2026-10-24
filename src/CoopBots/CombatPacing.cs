namespace CoopBots;

// One clock belongs to the whole team, regardless of which seat submits next.
// Planning overlaps this clock; only a successfully queued active card restarts it.
internal sealed class CombatPacing
{
    private object? _combat;
    private int _round;
    private long _lastCardAt;

    internal void Observe(object combat, int round, long now)
    {
        if (ReferenceEquals(combat, _combat) && round == _round) return;
        _combat = combat; _round = round; _lastCardAt = now;
    }

    internal bool IsDue(long now, bool humansFinished, bool paused, int intervalMs)
        => !paused && _combat is not null && now - _lastCardAt >= Effective(intervalMs, humansFinished);

    internal long EligibleAt(bool humansFinished, int intervalMs)
        => _lastCardAt + Effective(intervalMs, humansFinished);

    internal static int Effective(int intervalMs, bool humansFinished)
        => humansFinished ? 0 : intervalMs;

    internal void MarkCardSubmitted(long now) => _lastCardAt = now;
    internal void Reset() { _combat = null; _round = 0; _lastCardAt = 0; }
}
