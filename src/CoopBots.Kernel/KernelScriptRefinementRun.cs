using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

// Upstream has no multiplayer idle-script optimizer. Owner request 2026-10-03:
// use execution/animation time for a separate, resumable job on a frozen root.
// The current turn is replayed unchanged; only the next-turn suffix is optimized.
public sealed class KernelScriptRefinementRun
{
    private readonly IReadOnlyList<Player> party, actors;
    private readonly IReadOnlyList<KernelTeamSearch.Action> script;
    private readonly TournamentOptions options;
    private readonly int budgetMs, initialRound;
    private readonly KernelDecisionRequest? request;
    private KernelSession replay;
    private int index;
    private readonly List<KernelTeamSearch.Action> actions = new();
    private readonly List<ulong[]> alive = new();
    private readonly List<Creature[]> enemies = new();
    private TournamentRun? search;
    private bool referenceRefused;
    private KernelScriptReplayRun? validation;
    private readonly HashSet<IReadOnlyList<KernelTeamSearch.Action>> examined = new(ReferenceEqualityComparer.Instance);
    public string FailureDetail { get; private set; } = "";
    public int ValidatedCandidates { get; private set; }
    public int RejectedCandidates { get; private set; }
    public KernelSession? BoundaryState { get; private set; }
    public KernelPlanningState? BoundaryEvidence { get; private set; }
    public int BoundaryIndex { get; private set; }
    public string? BoundaryText { get; private set; }
    public TournamentResult? Baseline { get; private set; }
    public TournamentResult? Result { get; private set; }
    public double ComputeMs { get; private set; }
    public bool Done { get; private set; }
    public string StopReason { get; private set; } = "pending";
    public bool Improved => Result?.Record is { } fresh && Baseline?.Record is { } old
        && TerminalComparer.Compare(fresh, old, options.Objective) > 0
        || referenceRefused && Result?.Record is { Verified: true };

    public void FinishAtBoundary()
    {
        // A completed search record is not yet a replayed candidate. Keep the
        // last validated result when animation time ends midway through replay.
        if (!Done) StopReason = Baseline is null ? "boundary-reference-incomplete"
            : Improved ? "boundary-improved" : "boundary-reference-kept";
        Done = true;
    }

    public KernelScriptRefinementRun(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, IReadOnlyList<KernelTeamSearch.Action> script,
        TournamentOptions options, int budgetMs)
    {
        request = options.Request;
        replay = root.Fork(); replay.AutoResolveTurnStartChoices = true; replay.AutoResolveEnemyChoices = true;
        initialRound = root.RoundsAdvanced;
        this.party = party; this.actors = actors; this.script = script;
        this.options = options; this.budgetMs = budgetMs;
    }

    public bool Advance(TimeSpan slice)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(request);
        if (Done) return true;
        var watch = Stopwatch.StartNew();
        try
        {
            do
            {
                if (request?.IsStopped == true)
                { FinishAtBoundary(); StopReason = request.StopReason; break; }
                if (ComputeMs + watch.Elapsed.TotalMilliseconds >= budgetMs)
                { FinishAtBoundary(); StopReason = "budget"; break; }
                if (Baseline is not null || referenceRefused)
                {
                    if (validation is not null)
                    {
                        validation.Advance(TimeSpan.FromMilliseconds(1));
                        if (!validation.Done) continue;
                        if (validation.Result is { Record: { } record } verified)
                        {
                            ValidatedCandidates++;
                            if (Result?.Record is not { } incumbent
                                || TerminalComparer.Compare(record, incumbent, options.Objective) > 0) Result = verified;
                        }
                        else { RejectedCandidates++; FailureDetail = validation.Failure; }
                        validation = null;
                        if (search!.Done) { StopReason = Improved ? "improved" : "reference-kept"; Done = true; }
                        continue;
                    }
                    search ??= new TournamentRun(BoundaryState!, party, actors, options with {
                        IncludePotions = true, PotionsOnly = false, IncludeEndTurns = true,
                        // Publish complete opening comparisons early. An initial
                        // beam would spend most animation time without producing
                        // a single replacement candidate.
                        MaxDegreeOfParallelism = 1, EnableJointSearch = false, ReferenceResult = Baseline,
                    }, Math.Max(1, (int)(budgetMs - ComputeMs - watch.Elapsed.TotalMilliseconds)));
                    var finished = search.Advance(TimeSpan.FromMilliseconds(1));
                    var candidate = search.BestCompleted;
                    if (candidate?.Script is { Count: > 0 } candidateScript && !ReferenceEquals(candidate, Baseline)
                        && candidate.Record is { } candidateRecord
                        && (Result?.Record is not { } currentRecord || TerminalComparer.Compare(candidateRecord, currentRecord, options.Objective) > 0)
                        && examined.Add(candidateScript))
                        validation = new(BoundaryState!, party, candidate, options.Rollout?.MaxRounds ?? 80, request);
                    if (finished && validation is null)
                    {
                        StopReason = Improved ? "improved" : "reference-kept";
                        Done = true;
                    }
                    continue;
                }
                if (replay.HasTerminal || index >= script.Count)
                {
                    if (BoundaryState is null || actions.Count == 0)
                    { StopReason = "no-next-turn"; Done = true; break; }
                    var record = TerminalRecord.Capture(replay, party, replay.HasTerminal, "reference-script-end") with {
                        ResourcesConsumed = actions.Count(a => a.Potion is not null),
                    };
                    Baseline = Result = new TournamentResult(actions[0], record, [record], 1,
                        record.Verified ? 0 : 1, "refinement-reference") {
                        Script = actions.ToArray(), ScriptAliveStates = alive.ToArray(), ScriptEnemyStates = enemies.ToArray(),
                        RootGeneration = request?.RootGeneration, RequestProvenance = request,
                    };
                    continue;
                }
                var action = script[index];
                if (BoundaryState is not null)
                {
                    alive.Add(party.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).Order().ToArray());
                    enemies.Add(replay.Enemies.ToArray());
                }
                if (!replay.ReplayPlanned(action, options.Rollout?.MaxRounds ?? 80, out var reason, out var resolved))
                {
                    FailureDetail = $"step={index} round={replay.RoundsAdvanced} {replay.PlannedActionDetail(action)}";
                    StopReason = "reference-refused:" + reason;
                    if (BoundaryState is not null) { referenceRefused = true; continue; }
                    Done = true; break;
                }
                if (BoundaryState is not null) actions.Add(resolved);
                index++;
                if (BoundaryState is null && replay.RoundsAdvanced > initialRound)
                {
                    BoundaryIndex = index; BoundaryState = replay.Fork();
                    BoundaryText = BoundaryState.PartyStateText();
                    // Existing prediction freezes the real adopted native prefix.
                    // The local full-state reuse contract cannot accept an
                    // unqualified boundary, so spending its suffix budget cannot
                    // make that cache adoptable. Preserve the boundary for R9.
                    // Owner 2026-10-12: F31 and F8 spent idle refinement work
                    // on environmental-risk or unavailable owned power facts.
                    BoundaryEvidence = BoundaryState.PlanningState(party.Select(p => p.NetId));
                    if (!BoundaryEvidence.Reusable)
                    {
                        StopReason = "boundary-not-reusable";
                        Done = true;
                        break;
                    }
                }
            } while (!Done && watch.Elapsed < slice);
        }
        finally { ComputeMs += watch.Elapsed.TotalMilliseconds; }
        return Done;
    }
}
