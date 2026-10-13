using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

// Upstream optimizes card paths. For the owner's 2026-10-03 request, reuse the
// completed card reference to measure bottles BEFORE buying another beam.
// Native replay rebuilds every step's evidence. A changed target or choice
// menu resumes a formal policy from the state before the failed attempt.
// Only an actual native terminal boundary publishes a completed comparison.
public sealed class KernelPotionContinuationRun
{
    private readonly KernelSession root;
    private readonly IReadOnlyList<Player> party;
    private readonly IReadOnlyList<Player> actors;
    private readonly TournamentResult baseline;
    private readonly RolloutOptions continuationOptions;
    private readonly List<TournamentResult> completedResults = new();
    private readonly List<KernelSession> completedNativeStates = new();
    private readonly List<IReadOnlyList<KernelTeamSearch.Action>> jobs = new();
    private readonly int budgetMs;
    private readonly KernelDecisionRequest? request;
    private readonly Stopwatch? phaseClock;
    private string? cancelled;
    internal Func<bool>? StopRequested { private get; init; }
    private bool Stopped => Volatile.Read(ref cancelled) is not null || request?.IsStopped == true || StopRequested?.Invoke() == true
        || phaseClock is not null && phaseClock.Elapsed.TotalMilliseconds >= budgetMs;
    public long? RootGeneration => request?.RootGeneration;
    public double PhaseWallMs => phaseClock?.Elapsed.TotalMilliseconds ?? 0;
    private double BudgetElapsed(Stopwatch scope) => phaseClock?.Elapsed.TotalMilliseconds
        ?? ComputeMs + scope.Elapsed.TotalMilliseconds;
    private readonly List<KernelTeamSearch.Action> actions = new();
    private readonly List<ulong[]> alive = new();
    private readonly List<Creature[]> enemies = new();
    private KernelSession? replay;
    private RolloutRun? continuation;
    private KernelActionCheckpoint? repairStart;
    private readonly HashSet<(int Round, int Segment)> repairedRounds = new();
    private int formalStepsSpent;
    internal int RepairedActions { get; private set; }
    internal int ResumedScripts { get; private set; }
    internal int FormalActions { get; private set; }
    internal int MenuRepairs { get; private set; }
    internal int UnplayableRepairs { get; private set; }
    internal string RepairDiagnostics { get; private set; } = "";
    private int job, index;
    public double ComputeMs { get; private set; }
    public int Completed { get; private set; }
    public bool Done { get; private set; }
    public TournamentResult Result { get; private set; }
    public IReadOnlyList<TournamentResult> CompletedResults => completedResults;
    internal IReadOnlyList<KernelSession> CompletedNativeStates => completedNativeStates;

    public KernelPotionContinuationRun(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, TournamentResult baseline, int budgetMs,
        RolloutOptions? continuationOptions = null, KernelDecisionRequest? request = null)
    {
        var clock = Stopwatch.StartNew();
        this.root = root; this.party = party; this.actors = actors; this.baseline = baseline; this.request = request;
        phaseClock = request is null ? null : Stopwatch.StartNew();
        this.continuationOptions = continuationOptions ?? new(RolloutPolicyKind.Kill) {
            InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false };
        this.budgetMs = budgetMs; Result = baseline;
        if (Stopped) { FinishStopped(); return; }
        if (baseline.Script is not { Count: > 0 }) { Done = true; return; }
        var options = new TournamentOptions(IncludePotions: true, PotionsOnly: true, IncludeEndTurns: false) { Request = request };
        var openings = KernelTournament.FirstActions(root, actors, 8, options, () => Stopped);
        if (Stopped) { FinishStopped(); return; }
        foreach (var action in openings) jobs.Add(new[] { action });
        // Complete the existing single-bottle comparisons before funding the
        // emergency combination. Retain that combination on the same root;
        // an expensive changed-phase tail must not consume every comparison.
        if (baseline.Record is { Verified: true, Victory: false } && openings.Count > 1)
            jobs.Add( openings.Where(a => KernelPotionPolicy.ImmediateBenefit(root, party, a, baseline.Script) > .01)
                .DistinctBy(a => a.Potion).Take(4).ToArray());
        ComputeMs = clock.Elapsed.TotalMilliseconds;
    }

    public bool Advance(TimeSpan slice)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(request);
        if (Done)
        {
            if (Volatile.Read(ref cancelled) is not null || request?.IsCancelled == true) FinishStopped();
            return true;
        }
        var watch = Stopwatch.StartNew();
        try
        {
            do
            {
                if (Stopped) { FinishStopped(); break; }
                if (job >= jobs.Count || BudgetElapsed(watch) >= budgetMs)
                { Done = true; break; }
                if (replay is null)
                {
                    replay = root.Fork(); replay.AutoResolveTurnStartChoices = true;
                    replay.AutoResolveEnemyChoices = true;
                    index = 0; actions.Clear(); alive.Clear(); enemies.Clear();
                    repairStart = null; repairedRounds.Clear(); formalStepsSpent = 0;
                }
                if (continuation is not null)
                {
                    var beforeSteps = continuation.CommittedSteps;
                    continuation.Step();
                    FormalActions += continuation.CommittedSteps - beforeSteps;
                    if (!continuation.Done && repairStart is { } start
                        && continuation.State.CanFork && !continuation.State.EnemyPhaseCompleted
                        && !continuation.State.HorizonClosed
                        && continuation.State.RoundsAdvanced > start.SideSegments)
                    {
                        var prefixResult = continuation.SettledPrefix();
                        actions.AddRange(prefixResult.Actions); alive.AddRange(prefixResult.AliveStates);
                        enemies.AddRange(prefixResult.EnemyStates);
                        RepairedActions += prefixResult.Steps; formalStepsSpent += prefixResult.Steps;
                        replay = continuation.State; continuation = null; repairStart = null;
                        if (TryResumeReference(start, prefixResult, replay, out var resume))
                        { index = jobs[job].Count + resume; ResumedScripts++; }
                        else continuation = NewContinuation(replay);
                        continue;
                    }
                    if (!continuation.Done) continue;
                    var tail = continuation.Result!;
                    if (!tail.Terminal || !tail.Record.Verified)
                    { continuation = null; replay = null; job++; continue; }
                    actions.AddRange(tail.Actions); alive.AddRange(tail.AliveStates); enemies.AddRange(tail.EnemyStates);
                    replay = continuation.State; continuation = null;
                    repairStart = null;
                }
                if (replay.HasTerminal)
                {
                    Completed++;
                    var record = TerminalRecord.Capture(replay, party, true) with
                        { ResourcesConsumed = actions.Count(a => a.Potion is not null) };
                    var completed = baseline with { FirstAction = actions[0], Record = record,
                            RootGeneration = request?.RootGeneration ?? baseline.RootGeneration,
                            RequestProvenance = request ?? baseline.RequestProvenance,
                            Script = actions.ToArray(), ScriptAliveStates = alive.ToArray(),
                            ScriptEnemyStates = enemies.ToArray(), ScriptCheckpoints = null,
                            StopReason = "potion-reference-replay" };
                    completedResults.Add(completed);
                    completedNativeStates.Add(replay);
                    RepairDiagnostics += $"[completed={Completed} potionMs={ComputeMs + watch.Elapsed.TotalMilliseconds:F1} actions={actions.Count} bottles={record.ResourcesConsumed}]";
                    if (actions.Count > 0 && (Result.Record is null
                        || TerminalComparer.Compare(record, Result.Record, KernelPotionPolicy.Objective) > 0)) Result = completed;
                    replay = null; job++; continue;
                }
                var prefix = jobs[job];
                if (index >= prefix.Count + baseline.Script!.Count)
                { continuation = NewContinuation(replay); continue; }
                var action = index < prefix.Count ? prefix[index] : baseline.Script[index - prefix.Count];
                // Failed native actions can leave partial effects. Keep the
                // state before the attempt and re-plan from that owned state.
                var attempt = replay.Fork();
                var replayed = attempt.ReplayPlanned(action, this.continuationOptions.MaxRounds, out var replayBoundary, out var resolved);
                var sameMenus = replayed && SameChoiceMenus(action.Choices, resolved.Choices);
                if (!replayed || !sameMenus)
                {
                    var targetGap = !replayed && replayBoundary == "invalid-target"
                        && action.Card is not null && action.Target is { } failedTarget
                        && !replay.Enemies.Any(e => KernelSession.SameCreature(e, failedTarget));
                    // Play returns this before beginning a card transaction.
                    // Missing hand membership, resources or an ended owner require
                    // a fresh legal hand decision, independent of any stored target.
                    var cleanCardUnplayable = !replayed && replayBoundary == "unplayable"
                        && action.Card is not null && replay.CanFork;
                    RepairDiagnostics += $"[index{index} {action.Card?.Id.Entry}:{replayBoundary} targetGone={targetGap} menuChanged={replayed && !sameMenus} cleanUnplayable={cleanCardUnplayable} meta={CompleteCheckpoints()}]";
                    // An invalid target, changed actual menu or pre-transaction
                    // unplayable card funds one real side repair per native round.
                    // Replay resumes only at its complete native checkpoint.
                    // Missing metadata, extra turns, a repeated same-side gap,
                    // unmatched checkpoints and every other failure retain the
                    // original complete formal tail from the pre-attempt state.
                    if ((targetGap || cleanCardUnplayable || replayed && !sameMenus) && CompleteCheckpoints())
                    {
                        var checkpoint = replay.ActionCheckpoint();
                        if (!checkpoint.ExtraTurn && repairedRounds.Add((checkpoint.NativeRound, checkpoint.SideSegments)))
                        {
                            repairStart = checkpoint;
                            if (replayed && !sameMenus) MenuRepairs++;
                            if (cleanCardUnplayable) UnplayableRepairs++;
                        }
                    }
                    continuation = NewContinuation(replay); continue;
                }
                alive.Add(party.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(id => id).ToArray());
                enemies.Add(replay.Enemies.ToArray()); replay = attempt;
                actions.Add(resolved); index++;
            } while (watch.Elapsed < slice);
        }
        finally { ComputeMs += watch.Elapsed.TotalMilliseconds; }
        return Done;
    }

    private RolloutRun NewContinuation(KernelSession state)
    {
        if (actions.Count > 0 && state.HorizonClosed && !state.HasTerminal
            && state.RoundsAdvanced + 1 < continuationOptions.MaxRounds)
        {
            if (state.ContinueRecordedHorizon(continuationOptions.MaxRounds, out var why, out var setup))
                actions[^1] = actions[^1] with { TurnStartChoices = setup };
            else RepairDiagnostics += "[continuation-start-failed=" + why + "]";
        }
        return new(state, party, actors,
            continuationOptions with { MaxSteps = Math.Max(0, continuationOptions.MaxSteps - formalStepsSpent),
                CaptureActionCheckpoints = repairStart is not null }) { StopRequested = () => Stopped };
    }

    public void Cancel(string reason = "cancelled")
        => Interlocked.CompareExchange(ref cancelled, string.IsNullOrEmpty(reason) ? "cancelled" : reason, null);

    private void FinishStopped()
    {
        Done = true;
        if (Volatile.Read(ref cancelled) is not null || request?.IsCancelled == true)
            Result = Result with { FirstAction = null, StopReason = "request-cancelled:" + (Volatile.Read(ref cancelled) ?? "external-request") };
        else Result = Result with { StopReason = request is { IsStopped: true } stopped ? stopped.StopReason : "phase-deadline" };
    }

    private bool CompleteCheckpoints() => baseline.ScriptCheckpoints is { } checkpoints
        && checkpoints.Count == baseline.Script!.Count && checkpoints.All(c => !c.ExtraTurn);

    private bool TryResumeReference(KernelActionCheckpoint start, RolloutOutcome prefix,
        KernelSession state, out int resume)
    {
        resume = -1;
        if (!CompleteCheckpoints() || prefix.Checkpoints is not { } actual
            || actual.Count != prefix.Actions.Count || actual.Any(c => c.ExtraTurn)) return false;
        var after = state.ActionCheckpoint();
        if (after.ExtraTurn || after.NativeRound != start.NativeRound + 1
            || after.SideSegments != start.SideSegments + 1 || after.Seats.Count != start.Seats.Count)
            return false;
        for (var i = 0; i < after.Seats.Count; i++)
            if (after.Seats[i].Player != start.Seats[i].Player
                || start.Seats[i].Alive && after.Seats[i].Alive && after.Seats[i].Turn != start.Seats[i].Turn + 1
                || after.Seats[i].Turn < start.Seats[i].Turn || after.Seats[i].Turn > start.Seats[i].Turn + 1) return false;
        for (var i = Math.Max(0, index - jobs[job].Count); i < baseline.ScriptCheckpoints!.Count; i++)
            if (baseline.ScriptCheckpoints[i].SameBoundary(after)) { resume = i; return true; }
        RepairDiagnostics += $"[checkpoint-miss after={after.NativeRound}/{after.SideSegments}:{string.Join(';', after.Seats)}]";
        return false;
    }

    private static bool SameChoiceMenus(IReadOnlyList<KernelChoice>? expected, IReadOnlyList<KernelChoice>? actual)
    {
        if ((expected?.Count ?? 0) != (actual?.Count ?? 0)) return false;
        for (var i = 0; i < (expected?.Count ?? 0); i++)
        {
            var before = expected![i]; var after = actual![i];
            if (before.Owner != after.Owner || before.Source != after.Source || before.Effect != after.Effect
                || before.Pile != after.Pile || before.Context != after.Context
                || before.Minimum != after.Minimum || before.Maximum != after.Maximum
                || before.RequestedMinimum != after.RequestedMinimum || before.RequestedMaximum != after.RequestedMaximum
                || before.MenuSource != after.MenuSource || before.Options is null || after.Options is null
                || !before.Options.SequenceEqual(after.Options) || !before.Cards.SequenceEqual(after.Cards)) return false;
        }
        return true;
    }
}
