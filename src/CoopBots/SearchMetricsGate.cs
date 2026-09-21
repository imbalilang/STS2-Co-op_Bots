namespace CoopBots;

/// <summary>
/// Decides which of the planner's search-complete reports is the real one.
///
/// The planner reports a finished search from four places. Three of them (card, potion,
/// end-turn(plan)) fire while the COMMITTED SCRIPT is being played, long after the search
/// ended — and because computeMs / planningStartMs / the GC baselines are fields, those
/// reports reprint the same search's numbers, only staler.
///
/// Measured live 2026-09-18, 4-seat A10, 6 logs: 738 `search-complete` lines for ~116 real
/// searches. One line repeated `compute=61121.7ms` byte-identically 21 times; another showed
/// `wall=217344ms` beside `compute=88083ms`. The wall, GC and alloc columns of a repeated
/// line are the script's runtime charged to the search — which is why PLAN.md B1's cost
/// figures could not be read at face value.
///
/// This is a separate type only so the invariant is testable. It is not a policy: exactly one
/// report per search carries the metrics, and the rest are plan steps.
/// </summary>
internal sealed class SearchMetricsGate
{
    private bool reported;

    /// <summary>Call when a new search starts, where its start stamp is taken.</summary>
    internal void BeginSearch() => reported = false;

    /// <summary>True for the first report of the current search, false for every later one.</summary>
    internal bool ClaimSearchLine()
    {
        if (reported) return false;
        reported = true;
        return true;
    }
}
