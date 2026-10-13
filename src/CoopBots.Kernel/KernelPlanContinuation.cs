using System.Diagnostics;

namespace CoopBots.Kernel;

/// <summary>
/// A selected plan's private, incrementally replayed prefix. It retains no
/// search frontier and never searches or reads a live object from a worker.
/// </summary>
public sealed class KernelPlanContinuation
{
    private readonly KernelSession state;
    private readonly IReadOnlyList<KernelTeamSearch.Action> script;
    private readonly ulong[] controlled;
    public int Cursor { get; private set; }
    public string Failure { get; private set; } = "";
    public KernelPlanningState Evidence => state.PlanningState(controlled);

    public KernelPlanContinuation(KernelSession root, IReadOnlyList<KernelTeamSearch.Action> script,
        IEnumerable<ulong> controlled)
    {
        state = root.Fork(); this.script = script;
        this.controlled = controlled.Order().ToArray();
    }

    // Upstream retains an exact predicted continuation stamp. Multiplayer
    // consumes only already-deployed Bot actions, in bounded slices; future
    // human actions are never claimed to match this prefix. Owner 2026-10-09.
    public bool AdvanceTo(int cursor, TimeSpan slice, KernelDecisionRequest? request = null)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(request);
        if (cursor < Cursor || cursor > script.Count)
            throw new ArgumentOutOfRangeException(nameof(cursor));
        if (Failure.Length > 0) return true;
        var watch = Stopwatch.StartNew();
        while (Cursor < cursor)
        {
            if (request?.IsStopped == true)
            { Failure = "prefix-request-" + request.StopReason; return true; }
            if (!state.ReplayPlanned(script[Cursor], 80, out var failure))
            { Failure = $"prefix-step={Cursor}:{failure}"; return true; }
            Cursor++;
            if (watch.Elapsed >= slice) return Cursor == cursor;
        }
        return true;
    }

    public KernelSession ForkMatchedState(KernelPlanningState actual)
    {
        if (Failure.Length > 0 || !Evidence.Matches(actual))
            throw new InvalidOperationException("Continuation reuse requires exact supported-state evidence.");
        return state.Fork();
    }
}
