using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

// A bounded execution-time comparison, independent of whether the cached line
// eventually wins. Only a replayed victory may replace that line.
public sealed class KernelShortVictoryRun
{
    private readonly KernelSession root;
    private readonly IReadOnlyList<Player> party;
    private readonly KernelTeamSearch? search;
    private readonly KernelDecisionRequest? request;
    private readonly Stopwatch? phaseClock;
    private bool Stopped => request?.IsStopped == true
        || phaseClock is not null && phaseClock.Elapsed.TotalMilliseconds >= 60;
    private string RequestStop => request is { IsStopped: true } stopped ? stopped.StopReason : "phase-deadline";
    private KernelScriptReplayRun? replay;
    private RolloutRun? direct;
    private double compute;
    public bool Done { get; private set; }
    public TournamentResult? Result { get; private set; }
    public string StopReason { get; private set; } = "pending";
    public KernelShortVictoryRun(KernelSession root, IReadOnlyList<Player> actors,
        KernelDecisionRequest? request = null)
    {
        this.root = root; party = root.Party; this.request = request;
        phaseClock = request is null ? null : Stopwatch.StartNew();
        if (StopAtRequestBoundary()) return;
        direct = new(root.Fork(), party, actors, new(RolloutPolicyKind.Kill, MaxSteps: 12, MaxRounds: root.RoundsAdvanced + 1) { InvestmentLookahead = false, SequenceLookahead = false })
            { StopRequested = () => Stopped };
        search = new(root, actors, s => KernelTacticalPolicy.Value(s, party, actors),
            new(Depth: 8, Width: 12, MaxNodes: 384, IncludeEndTurns: false) {
                IncludePotions = false, CaptureReplayStates = false, Deduplicate = false,
                PreserveSetupRoutes = true });
    }
    public static bool WorthChecking(KernelSession root, IReadOnlyList<Player> actors)
    {
        var main = root.Enemies.Where(root.IsPrimaryEnemy).ToArray();
        if (main.Length == 0) return false;
        // Attack damage alone missed the decimillipede kill: a 7-damage Strike
        // into 8 HP left 1, but the 4 poison the enemy already carried killed it
        // at the start of its turn. A short-victory comparison is worth running
        // when either the hand or the already-applied poison can finish the job.
        var attack = actors.Where(root.CanAct).SelectMany(root.Hand).Where(root.CanPlay)
            .Sum(c => main.Max(e => root.AttackDamageHint(c, e)));
        var poison = main.Sum(e => Math.Min(root.Hp(e),
            CardPoisonEffects.TwoTicks(root.Power<PoisonPower>(e))));
        return root.RemainingOpposition().Hp + main.Sum(root.Block) <= attack + poison;
    }
    public bool Advance(TimeSpan slice)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(request);
        if (Done) return true;
        var watch = Stopwatch.StartNew();
        try
        {
            if (StopAtRequestBoundary()) return true;
            if (replay is not null)
            {
                replay.Advance(slice);
                if (replay.Done) { search?.Dispose(); Result = replay.Result is { Record: { Verified: true, Victory: true } } r ? r : null; Done = true; StopReason = replay.Failure.Length > 0 ? replay.Failure : "complete"; }
                return Done;
            }
            if (direct is not null)
            {
                direct.Step();
                if (StopAtRequestBoundary()) return true;
                if (!direct.Done) return false;
                if (direct.Result is { Victory: true } win)
                    replay = new(root, party, new(win.Actions[0], win.Record, [win.Record], 1, 0, "short-direct") { Script = win.Actions }, 80, request)
                        { StopRequested = () => Stopped };
                direct = null;
                if (replay is not null) return false;
            }
            search!.Advance(slice, () => !Stopped);
            if (StopAtRequestBoundary()) return true;
            if (compute + watch.Elapsed.TotalMilliseconds >= 60) search.FinishAtBudget();
            if (!search.IsComplete) return false;
            if (search.CompletedState is not { HasWon: true } state || search.CompletedResult is not { Actions.Count: > 0 } result)
            { Done = true; search.Dispose(); return true; }
            var record = TerminalRecord.Capture(state, party, true);
            replay = new(root, party, new(result.Actions[0], record, [record], 1, 0, "short-victory") { Script = result.Actions }, 80, request)
                { StopRequested = () => Stopped };
            search.Dispose();
            return false;
        }
        finally { compute += watch.Elapsed.TotalMilliseconds; }
    }

    private bool StopAtRequestBoundary()
    {
        if (!Stopped) return false;
        // Only an already completed native replay is an adoptable incumbent.
        // A direct prefix or a beam estimate never becomes one on expiry.
        if (replay?.Result is { Record: { Verified: true, Victory: true } } completed) Result = completed;
        StopReason = RequestStop; Done = true; search?.Dispose();
        return true;
    }
}
