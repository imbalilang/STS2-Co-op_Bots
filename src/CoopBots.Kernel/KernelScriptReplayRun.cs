using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

// Validate only a proposed winner, once, against its captured boundary. Doing
// this for every search node would spend the animation budget on duplicate work.
public sealed class KernelScriptReplayRun
{
    private readonly KernelSession state;
    private readonly IReadOnlyList<Player> party;
    private readonly TournamentResult candidate;
    private readonly int maxRounds;
    private readonly KernelDecisionRequest? request;
    internal Func<bool>? StopRequested { private get; init; }
    private readonly List<KernelTeamSearch.Action> actions = new();
    private readonly List<ulong[]> alive = new();
    private readonly List<Creature[]> enemies = new();
    public bool Done { get; private set; }
    public TournamentResult? Result { get; private set; }
    public string Failure { get; private set; } = "";
    public KernelScriptReplayRun(KernelSession root, IReadOnlyList<Player> party,
        TournamentResult candidate, int maxRounds, KernelDecisionRequest? request = null)
    {
        this.request = request;
        state = request?.IsStopped == true ? root : root.Fork();
        this.party = party; this.candidate = candidate; this.maxRounds = maxRounds;
        if (StopAtRequestBoundary()) return;
        state.AutoResolveEnemyChoices = true; state.AutoResolveTurnStartChoices = true;
    }
    public void Advance(TimeSpan slice)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(request);
        var clock = Stopwatch.StartNew();
        do
        {
            if (Done) return;
            if (StopAtRequestBoundary()) return;
            if (state.HasTerminal || actions.Count >= (candidate.Script?.Count ?? 0))
            {
                PublishSettledReplay();
                return;
            }
            var action = candidate.Script![actions.Count];
            alive.Add(party.Where(p => state.Hp(p.Creature) > 0).Select(p => p.NetId).Order().ToArray());
            enemies.Add(state.Enemies.ToArray());
            if (!state.ReplayPlanned(action, maxRounds, out var reason, out var resolved))
            { Failure = $"step={actions.Count} round={state.RoundsAdvanced} reason={reason} {state.PlannedActionDetail(action)}"; Done = true; return; }
            actions.Add(resolved);
            if (StopAtRequestBoundary()) return;
        } while (clock.Elapsed < slice);
    }

    private bool StopAtRequestBoundary()
    {
        if (request?.IsStopped != true && StopRequested?.Invoke() != true) return false;
        Failure = request is { IsStopped: true } stopped ? stopped.StopReason : "phase-deadline";
        if (actions.Count > 0 && state.HasTerminal) PublishSettledReplay();
        else Done = true;
        return true;
    }

    private void PublishSettledReplay()
    {
        Done = true;
        if (actions.Count == 0) { Failure = "empty-script"; return; }
        var record = TerminalRecord.Capture(state, party, state.HasTerminal, "replay-script-end") with {
            ResourcesConsumed = actions.Count(a => a.Potion is not null),
        };
        Result = candidate with { FirstAction = actions[0], Record = record, Script = actions.ToArray(),
            ScriptAliveStates = alive.ToArray(), ScriptEnemyStates = enemies.ToArray(),
            // Native replay proves this ending on the new root. It cannot
            // certify checkpoints captured by the old proposal's simulator.
            ScriptCheckpoints = request is null ? candidate.ScriptCheckpoints : null,
            RootGeneration = request?.RootGeneration ?? candidate.RootGeneration,
            RequestProvenance = request ?? candidate.RequestProvenance };
    }
}
