using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>
/// Search several actions jointly across seats, targets, card choices and bottles,
/// then judge retained prefixes by complete continuations. The beam's local score
/// never masquerades as a terminal result. All work is resumable between actions.
/// </summary>
internal sealed class KernelJointTurnRun
{
    private readonly KernelSession root;
    private readonly IReadOnlyList<Player> party;
    private readonly IReadOnlyList<Player> actors;
    private readonly TournamentOptions options;
    private readonly int budgetMs;
    private readonly Stopwatch? phaseClock;
    private readonly bool hasVerifiedReference;
    private readonly bool reusedOwnedReference;
    private KernelTeamSearch? search;
    private bool referenceReady, searchReady;
    private IReadOnlyList<KernelTeamSearch.Action> referencePath = [];
    private int referenceSteps;
    private IReadOnlyList<IReadOnlyList<KernelTeamSearch.Action>> paths = [];
    private int pathIndex;
    private int jobIndex;
    private (int Path, RolloutPolicyKind Policy)[] jobs = [];
    private double jobStartedMs;
    private readonly HashSet<int> visitedPaths = new();
    private readonly Dictionary<RolloutPolicyKind, int> completedPolicies = new();
    private readonly Dictionary<RolloutPolicyKind, int> startedPolicies = new();
    private int budgetCutoffs;
    private int replayFailures;
    private string searchStop = "";
    private string searchBoundaries = "";
    private string referenceStop = "pending";
    private double searchComputeMs, referenceComputeMs, searchStartedMs, referenceComputeStartedMs, searchComputeStartedMs;
    private RolloutPolicyKind ReferencePolicy => options.DiverseJointContinuations
        ? RolloutPolicyKind.Kill : RolloutPolicyKind.Tactical;
    private static readonly RolloutPolicyKind[] Policies = [RolloutPolicyKind.Kill,
        RolloutPolicyKind.KillAdds, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth,
        RolloutPolicyKind.Tactical, RolloutPolicyKind.Cleanup];
    // Start with an inexpensive reference, then fund the distinct Tactical,
    // Defend and Growth comparisons before broadening to cheap permutations.
    private (int Path, RolloutPolicyKind Policy) Job
        => jobs[jobIndex];
    private RolloutPolicyKind Policy => Job.Policy;
    internal static (int Path, RolloutPolicyKind Policy)[] Schedule(int count, bool diverse)
    {
        if (count <= 0) return [];
        if (!diverse) return Enumerable.Range(0, count).Select(i => (i, RolloutPolicyKind.Tactical)).ToArray();
        var jobs = new List<(int, RolloutPolicyKind)> { (0, RolloutPolicyKind.Kill), (0, RolloutPolicyKind.Tactical),
            (0, RolloutPolicyKind.Defend), (0, RolloutPolicyKind.Growth) };
        for (var wave = 0; wave < Policies.Length; wave++)
        for (var path = 0; path < count; path++)
        {
            var job = (path, Policies[(wave + path) % Policies.Length]);
            if (!jobs.Contains(job)) jobs.Add(job);
        }
        return jobs.ToArray();
    }
    private int replayIndex;
    private KernelSession? replay;
    private RolloutRun? continuation;
    private RolloutRun? referenceOpening;
    private readonly List<ulong[]> alive = new();
    private readonly List<Creature[]> enemies = new();
    private readonly List<KernelTeamSearch.Action> resolvedPrefix = new();
    private double computeMs;
    private readonly List<TerminalRecord> considered = new();
    private TournamentResult? best;
    private TournamentResult? referenceResult;
    internal bool Done { get; private set; }
    internal Func<bool>? StopRequested { private get; init; }
    private bool Stopped => StopRequested?.Invoke() == true || options.Request?.IsStopped == true
        || phaseClock is not null && phaseClock.Elapsed.TotalMilliseconds >= budgetMs;
    private double BudgetElapsed(Stopwatch scope) => phaseClock?.Elapsed.TotalMilliseconds
        ?? computeMs + scope.Elapsed.TotalMilliseconds;
    internal TournamentResult? Result => best;
    internal int Nodes { get; private set; }
    internal int Prefixes { get; private set; }
    internal int MaxPrefixActions { get; private set; }
    internal string Diagnostics => $"stop={searchStop} paths={visitedPaths.Count}/{paths.Count} "
        + $"policies=[{string.Join(',',completedPolicies.Select(p=>$"{p.Key}:{p.Value}"))}] "
        + $"started=[{string.Join(',',startedPolicies.Select(p=>$"{p.Key}={p.Value}"))}] "
        + $"budgetCutoffs={budgetCutoffs} replayFailures={replayFailures} reference={referenceStop} "
        + $"outerReference={(hasVerifiedReference ? "verified" : "unfinished")} "
        + $"reusedOwnedReference={reusedOwnedReference} "
        + $"searchMs={searchComputeMs:F0} referenceMs={referenceComputeMs:F0} "
        + $"referenceActions={referencePath.Count} referenceSteps={referenceSteps} "
        + $"referencePrefix=[{string.Join(';', referencePath.Select(ReferenceIdentity))}] "
        + $"boundaries=[{searchBoundaries}]";

    private int JobCount => paths.Count * (options.DiverseJointContinuations ? Policies.Length : 1);

    // Spend comparison time before the optional beam. This uses a fresh replay
    // of the resolved native opening, never the reference's finished state.
    private bool ComparingBeforeBeam => options.DiverseJointContinuations
        && !searchReady && referencePath.Count > 0 && jobIndex is >= 1 and <= 3;

    /// <summary>
    /// Per-job compute cap. The deterministic native reference retains the
    /// total-budget cap. After it finishes, divide the remaining comparison
    /// allowance among Tactical/Defend/Growth even when the outer reference
    /// is unfinished. An uncapped Tactical job starved the other policies.
    ///
    /// The divisor is the number of priority comparisons still to fund, not
    /// every (path, policy) combination left. Dividing by nearly fifty jobs
    /// gave each too little to finish; making Tactical uncapped then prevented
    /// Defend/Growth from being opened at all. Distinct comparisons now share
    /// the post-reference remainder. A capped job stays an explicit
    /// cutoff and is never scored as a terminal.
    /// </summary>
    // First realize one deterministic native opening to a real outcome, under
    // the unchanged total deadline. A wall-clock-limited beam must not choose
    // that protected reference prefix. Only then spend bounded work on beam
    // alternatives, with Tactical/Defend/Growth sharing the remainder 3:1:1.
    private const int TacticalWeight = 3;
    private double JobCapMs(double jobStartMs)
    {
        if (jobIndex == 0) return double.PositiveInfinity;
        var weight = options.DiverseJointContinuations
            ? jobIndex switch { 1 => (double)TacticalWeight, _ => 1d } : 1d;
        return Math.Max(15, (budgetMs - jobStartMs) * weight / AttemptsLeft);
    }

    private int AttemptsLeft
    {
        get
        {
            var remaining = Math.Max(1, JobCount - jobIndex);
            // Tactical used to have no cap and could spend every remaining
            // millisecond before Defend/Growth were even opened. Reserve a
            // weighted share for the distinct priority comparisons. The
            // complete tournament reference remains outside this joint budget.
            if (options.DiverseJointContinuations && jobIndex is >= 0 and <= 3)
                return jobIndex switch { 0 => 1, 1 => TacticalWeight + 2, 2 => 2, _ => 1 };
            return options.DiverseJointContinuations ? Math.Min(remaining, 2) : remaining;
        }
    }

    internal KernelJointTurnRun(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, TournamentOptions options, int budgetMs, bool hasVerifiedReference = false,
        TournamentResult? ownedCompletedCardReference = null)
    {
        this.root = root; this.party = party; this.actors = actors;
        this.options = options; this.budgetMs = Math.Max(1, budgetMs);
        phaseClock = options.Request is null ? null : Stopwatch.StartNew();
        this.hasVerifiedReference = hasVerifiedReference;
        // The caller supplies only its own real Kill outcome from this same root,
        // party and continuation options. Tactical-only and potion-only joints
        // still choose their own native reference opening. Reuse only the ending:
        // every new policy starts with a fresh root replay of its actual first card.
        if (options.DiverseJointContinuations && !options.PotionsOnly
            && ownedCompletedCardReference is { Record: { Verified: true }, Script: { Count: > 0 } script } owned
            && (options.Request is null || owned.IsFor(options.Request))
            && script[0] is { Potion: null } first && actors.Contains(first.Player)
            && (!first.EndTurn || options.IncludeEndTurns))
        {
            reusedOwnedReference = true;
            referenceReady = true;
            referenceStop = owned.StopReason;
            referencePath = [first]; paths = [referencePath]; pathIndex = 0;
            jobIndex = 1; jobs = Schedule(paths.Count, true);
            referenceResult = owned;
            // This ending has already been counted by the outer tournament.
            // Keep its full native replay evidence without inventing a new job,
            // prefix, visited path, completed-policy count or computation time.
            best = owned with { Considered = considered, Rollouts = 0, Cutoffs = 0,
                StopReason = "joint-turn" };
        }
    }

    internal bool Advance(TimeSpan slice)
    {
        if (Done) return true;
        var clock = Stopwatch.StartNew();
        try
        {
            do
            {
                if (Stopped)
                {
                    continuation?.FinishAtBudget();
                    search?.Dispose(); search = null;
                    searchStop = options.Request is { IsStopped: true } request ? request.StopReason : "phase-deadline";
                    Done = true; break;
                }
                if (!referenceReady)
                {
                    if (continuation is null)
                    {
                        jobStartedMs = BudgetElapsed(clock);
                        referenceComputeStartedMs = computeMs + clock.Elapsed.TotalMilliseconds;
                        // Exactly one deterministic, resolved legal first action,
                        // including its native choice tokens. Its actual after-state
                        // seeds a whole continuation; no outer record is assumed.
                        var opening = NativeReferenceOpening();
                        if (opening is null)
                        { referenceReady = true; referenceStop = "no-reference-opening"; continue; }
                        referencePath = [opening.Value.Action];
                        resolvedPrefix.Clear(); resolvedPrefix.Add(opening.Value.Action);
                        paths = [referencePath]; pathIndex = 0;
                        alive.Clear(); alive.Add(party.Where(p => root.Hp(p.Creature) > 0)
                            .Select(p => p.NetId).OrderBy(n => n).ToArray());
                        enemies.Clear(); enemies.Add(root.Enemies.ToArray());
                        startedPolicies[ReferencePolicy] = 1;
                        continuation = new RolloutRun(opening.Value.State, party, options.ContinuationActors ?? actors,
                            (options.Rollout ?? new()) with { Policy = ReferencePolicy,
                                InvestmentLookahead = false, SequenceLookahead = true,
                                SequenceHandLookahead = false }) { StopRequested = () => Stopped };
                        if (referenceOpening is not null) continuation.InheritSequenceBudget(referenceOpening);
                    }
                    continuation.Step();
                    var referenceUsed = BudgetElapsed(clock);
                    if (!continuation.Done && referenceUsed >= budgetMs) continuation.FinishAtBudget();
                    if (!continuation.Done) continue;
                    referenceStop = continuation.Result!.StopReason;
                    referenceComputeMs = computeMs + clock.Elapsed.TotalMilliseconds - referenceComputeStartedMs;
                    referenceSteps = continuation.Result.Steps;
                    if (referenceStop == "continuation-budget") budgetCutoffs++;
                    else { Accept(continuation.Result, ReferencePolicy); referenceResult = best; }
                    continuation = null; referenceReady = true; jobIndex = 1;
                    if (referenceUsed >= budgetMs) { Done = true; break; }
                    if (options.DiverseJointContinuations) jobs = Schedule(paths.Count, true);
                    continue;
                }
                if (search is null && !searchReady && !ComparingBeforeBeam)
                {
                    searchStartedMs = BudgetElapsed(clock);
                    searchComputeStartedMs = computeMs + clock.Elapsed.TotalMilliseconds;
                    search = new KernelTeamSearch(root, actors,
                        s => KernelTacticalPolicy.ActionValue(root, s, party, actors),
                        new KernelTeamSearch.Options(Depth: 28, Width: 12, MaxNodes: 4096,
                            IncludeEndTurns: options.IncludeEndTurns, MaxRounds: root.RoundsAdvanced + 1)
                        {
                            IncludePotions = options.IncludePotions,
                            RequirePotionOpening = options.PotionsOnly,
                            TerminalObjective = options.Objective,
                            CaptureReplayStates = false,
                            // CompactStateKey omits piles/RNG/potions. Do not merge
                            // different choice or consumable histories using that key.
                            Deduplicate = false,
                            PreserveSetupRoutes = options.DiverseJointContinuations,
                        });
                }
                if (search is not null)
                {
                    search.Advance(TimeSpan.FromMilliseconds(1), () => !Stopped);
                    // Leave most of the fixed budget for realizing the retained
                    // investments, rather than producing unevaluated prefixes.
                    if (BudgetElapsed(clock) - searchStartedMs >= (budgetMs - searchStartedMs) * .40)
                        search.FinishAtBudget();
                    if (!search.IsComplete) continue;
                    Nodes = search.ExpandedNodesSoFar;
                    searchComputeMs = computeMs + clock.Elapsed.TotalMilliseconds - searchComputeStartedMs;
                    paths = (referencePath.Count > 0 ? new[] { referencePath } : [])
                        .Concat(search.ContinuationPaths).ToArray();
                    searchReady = true;
                    if (referencePath.Count == 0) jobIndex = 0;
                    jobs = paths.Count == 0 ? [] : Schedule(paths.Count, options.DiverseJointContinuations);
                    searchStop = search.CompletedResult?.StopReason ?? "";
                    searchBoundaries = string.Join(',', search.CompletedResult?.Boundaries
                        .OrderByDescending(p=>p.Value).Take(5).Select(p=>$"{p.Key}:{p.Value}") ?? []);
                    search.Dispose(); search = null;
                    if (paths.Count == 0) { referenceStop = "no-paths"; Done = true; break; }
                }
                if (continuation is not null)
                {
                    continuation.Step();
                    var used = BudgetElapsed(clock);
                    // Timeouts stay explicit cutoffs. They never masquerade as
                    // terminal evidence or replace the tournament's reference.
                    if (!continuation.Done && (used >= budgetMs
                        || used - jobStartedMs >= JobCapMs(jobStartedMs)))
                        continuation.FinishAtBudget();
                    if (!continuation.Done) continue;
                    var stop = continuation.Result!.StopReason;
                    if (stop == "continuation-budget") budgetCutoffs++;
                    else Accept(continuation.Result!);
                    continuation = null; replay = null; jobIndex++;
                    if (!searchReady)
                    {
                        if (used >= budgetMs) { Done = true; break; }
                        // Rebuild the full schedule with the same reference at
                        // path zero, after each priority ending/cutoff.
                        continue;
                    }
                }
                if (jobIndex >= JobCount
                    || BudgetElapsed(clock) >= budgetMs)
                { Done = true; break; }
                if (replay is null)
                {
                    pathIndex = Job.Path;
                    jobStartedMs = BudgetElapsed(clock);
                    replay = root.Fork(); replayIndex = 0; alive.Clear(); enemies.Clear(); resolvedPrefix.Clear();
                    replay.AutoResolveTurnStartChoices = true;
                    replay.AutoResolveEnemyChoices = true;
                }
                var path = paths[pathIndex];
                if (replayIndex < path.Count)
                {
                    alive.Add(party.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(n => n).ToArray());
                    enemies.Add(replay.Enemies.ToArray());
                    if (!replay.ReplayPlanned(path[replayIndex], 80, out _, out var resolvedAction))
                    { replayFailures++; if (jobIndex == 0) referenceStop = "replay-failed"; replay = null; jobIndex++; continue; }
                    resolvedPrefix.Add(resolvedAction);
                    replayIndex++;
                    continue;
                }
                var continuationRounds = options.Rollout?.MaxRounds ?? 80;
                if (replay.HorizonClosed && !replay.HasTerminal && replay.RoundsAdvanced + 1 < continuationRounds)
                {
                    if (!replay.ContinueRecordedHorizon(continuationRounds, out var startBoundary, out var nextSetup))
                    {
                        replayFailures++;
                        if (jobIndex == 0) referenceStop = "continuation-start-failed:" + startBoundary;
                        replay = null; jobIndex++; continue;
                    }
                    // The original short End's answer contract contained no
                    // setup. Publish the newly predicted setup on that same
                    // action, so final native replay consumes its exact menus.
                    resolvedPrefix[^1] = resolvedPrefix[^1] with { TurnStartChoices = nextSetup };
                }
                startedPolicies[Policy] = startedPolicies.GetValueOrDefault(Policy) + 1;
                continuation = new RolloutRun(replay, party, options.ContinuationActors ?? actors,
                    (options.Rollout ?? new RolloutOptions()) with
                    {
                        Policy = Policy,
                        // A bounded allowance includes powers drawn AFTER the
                        // prefix. The reference must not be blind to new engines.
                        // Like the native reference, the early comparison
                        // realizes a whole line without nested investment jobs.
                        // Later beam jobs retain the existing investment work.
                        InvestmentLookahead = !ComparingBeforeBeam && jobIndex >= paths.Count,
                        // Ordering is a correctness property, not a late-search
                        // refinement: the adopted reference line was replaying
                        // Bully before its own Vulnerable setup because only the
                        // last jobs ever ordered. Every job may be the one that
                        // wins, so every job gets the bounded pair allowance.
                        SequenceLookahead = true,
                        SequenceHandLookahead = !ComparingBeforeBeam,
                        InvestmentStepBudget = 128,
                    }) { StopRequested = () => Stopped };
            } while (clock.Elapsed < slice);
        }
        finally { computeMs += clock.Elapsed.TotalMilliseconds; }
        return Done;
    }

    private (KernelTeamSearch.Action Action, KernelSession State)? NativeReferenceOpening()
    {
        if (!options.PotionsOnly)
        {
            // Selecting a scored first card then ordering only its suffix
            // cannot repair a dependency already consumed by that prefix.
            // Resolve the reference's own first native policy decision with
            // exactly the same finite AB/BA allowance used on later steps.
            var opening = referenceOpening = new RolloutRun(root.Fork(), party, actors,
                (options.Rollout ?? new()) with { Policy = ReferencePolicy,
                    InvestmentLookahead = false, SequenceLookahead = true,
                    SequenceHandLookahead = false }) { StopRequested = () => Stopped };
            opening.Step(); opening.FinishAtBudget();
            if (opening.Result!.Actions.FirstOrDefault() is { } resolved
                && (!resolved.EndTurn || options.IncludeEndTurns))
                return (resolved, opening.State);
        }
        // Reuse the production candidate rules, including potion-only and
        // mixed-table end-turn requests. No extra availability gate is added.
        foreach (var action in KernelTournament.FirstActions(root, actors, options.TopK, options))
        {
            if (action.Card is { } card)
            {
                var branch = root.CardBranches(card, action.Target, 1)
                    .FirstOrDefault(b => b.Boundary.Length == 0);
                if (branch is not null) return (action with { Choices = branch.Choices,
                    ChoiceOrdinals = branch.ChoiceOrdinals, TurnStartChoices = branch.TurnStartChoices }, branch.State);
            }
            else
            {
                var branch = root.Fork();
                branch.AutoResolveTurnStartChoices = true; branch.AutoResolveEnemyChoices = true;
                if (action.EndTurn)
                {
                    if (branch.EndTurn(action.Player, 80, out _, out var resolvedEnd)) return (resolvedEnd, branch);
                }
                else if (branch.ReplayPlanned(action, 80, out _, out var resolved)) return (resolved, branch);
            }
        }
        return null;
    }

    private static string ReferenceIdentity(KernelTeamSearch.Action action)
        => $"{action.Player.NetId}:{(action.EndTurn ? "EndTurn" : action.Potion is not null ? "Potion:" + action.Potion.Id.Entry : "Card:" + action.CardStateKey)}"
            + $"#{action.CardStateOccurrence}@{action.Target?.CombatId?.ToString() ?? "none"}:ordinals={string.Join(',',action.ChoiceOrdinals ?? [])}"
            + $":tokens={string.Join('/',(action.Choices ?? []).Select(c => $"{c.Source}:{c.Pile}:{c.Context}:{string.Join(',',c.Cards.Select(t => $"{t.StateKey}#{t.Occurrence}"))}"))}";

    private void Accept(RolloutOutcome outcome, RolloutPolicyKind? policy = null)
    {
        Prefixes++;
        visitedPaths.Add(pathIndex);
        var actualPolicy = policy ?? Policy;
        completedPolicies[actualPolicy] = completedPolicies.GetValueOrDefault(actualPolicy) + 1;
        IReadOnlyList<KernelTeamSearch.Action> prefix = resolvedPrefix.ToArray();
        MaxPrefixActions = Math.Max(MaxPrefixActions, prefix.Count);
        var record = outcome.Record with { ResourcesConsumed = outcome.Record.ResourcesConsumed + prefix.Count(a => a.Potion is not null) };
        considered.Add(record);
        if (best?.Record is not null && TerminalComparer.Compare(record, best.Record, options.Objective) <= 0) return;
        best = new TournamentResult(prefix[0], record, considered, Prefixes,
            considered.Count(r => !r.Verified), "joint-turn")
        {
            Script = prefix.Concat(outcome.Actions).ToArray(),
            ScriptAliveStates = alive.Concat(outcome.AliveStates).ToArray(),
            ScriptEnemyStates = enemies.Concat(outcome.EnemyStates).ToArray(),
        };
    }
}
