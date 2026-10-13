using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private KernelScriptRefinementRun? refinement;
    private IReadOnlyList<KernelTeamSearch.Action>? refinementScript;
    private int refinementRound;
    private bool refinementReported;
    private KernelDecisionRequest? refinementRequest;

    private void StartRefinement(KernelSession? root, CombatState combat, IReadOnlyList<KernelTeamSearch.Action> script)
    {
        ClearRefinement("new script");
        // A human can change the unplayed prefix. Use actual authority, including
        // handed-over seats, instead of treating synthetic bot IDs specially.
        if (root is null || !root.CanFork || combat.Players.Any(p => p.Creature.IsAlive && !AutoPilot.Drives(p.NetId))) return;
        var actors = combat.Players.Where(p => p.Creature.IsAlive && AutoPilot.Drives(p.NetId)).ToArray();
        refinementRound = combat.RoundNumber + 1;
        refinementScript = script;
        // One separately accounted speculative future-root request. Its wall
        // allowance starts here and cannot restart at baseline/search/replay.
        refinementRequest = new(++tournamentRootGeneration, 2 * TournamentDriveBudgetMs, 1);
        refinement = new KernelScriptRefinementRun(root, combat.Players.ToArray(), actors, script,
            new TournamentOptions(TournamentDriveTopK, Objective: KernelPotionPolicy.Objective,
                Rollout: new RolloutOptions(Seed: refinementRound)) { Request = refinementRequest }, 2 * TournamentDriveBudgetMs);
        Log.Info($"CoopBots refinement: started targetRound={refinementRound} frozenSteps={script.Count} slice=2ms budget={2 * TournamentDriveBudgetMs}ms");
    }

    // Called before pacing/animation early returns. Work stays on the game
    // thread and runs only against the captured branch, never a mid-action capture.
    internal void AdvanceRefinement(CombatState combat)
    {
        if (refinement is null) return;
        if (!ReferenceEquals(tournamentScriptCombat, combat) || !ReferenceEquals(refinementScript, tournamentScript)
            || combat.Players.Any(p => p.Creature.IsAlive && !AutoPilot.Drives(p.NetId)))
        { ClearRefinement("script or control changed"); return; }
        if (combat.RoundNumber >= refinementRound) return;
        // Foreground work owns the decision. Do not consume an extra replay
        // slice while that request is being prepared or searched.
        if (tournamentRequest is { IsStopped: false }) return;
        try
        {
            refinement.Advance(TimeSpan.FromMilliseconds(2));
            if (refinement.Done && !refinementReported)
            {
                refinementReported = true;
                Log.Info($"CoopBots refinement: ready targetRound={refinementRound} stop={refinement.StopReason} compute={refinement.ComputeMs:F1}ms "
                    + $"old={Describe(refinement.Baseline?.Record)} new={Describe(refinement.Result?.Record)} "
                    + $"validated={refinement.ValidatedCandidates} rejected={refinement.RejectedCandidates} failure={refinement.FailureDetail}");
            }
        }
        catch (Exception error)
        { Log.Warn($"CoopBots refinement: failed {error}"); ClearRefinement("exception"); }
    }

    private void TryAdoptRefinement(CombatState combat)
    {
        var job = refinement;
        if (job is null || combat.RoundNumber < refinementRound) return;
        job.FinishAtBoundary();
        // The frozen prefix already produced a boundary even when its suffix
        // comparison did not finish. Readiness cannot excuse retaining a
        // continuation from a boundary known to differ from the live one.
        // Compare only the same round/cursor, including on extra-turn tables.
        if (combat.RoundNumber == refinementRound && tournamentScriptIndex == job.BoundaryIndex
            && job.BoundaryState is not null && job.BoundaryText is not null
            && !RefinementMatches(job.BoundaryText, KernelSession.CaptureLivePartyText(combat)))
        {
            ClearTournamentScript("next-turn state differs from prediction: "
                + KernelSession.FirstLiveDifference(combat, job.BoundaryText));
            return;
        }
        // A known-unqualified prefix stops before Baseline/Result exist.
        // That must still retire the old terminal claim at the actual boundary:
        // legality guards alone do not renew its full-state or quality evidence.
        // The native visible-boundary R9 check above remains first. The original
        // tail is only an untrusted proposal for the existing actual-root replay.
        if (job.BoundaryEvidence is { Reusable: false } declined)
        {
            var declinedActual = KernelCombatCapture.Capture(combat);
            var declinedActualEvidence = declinedActual.PlanningState(ControlledSeats(combat));
            var declinedSuffix = tournamentScript?.Skip(tournamentScriptIndex).ToArray();
            var declinedProposal = tournamentScriptRecord is { } declinedPrevious && declinedSuffix is { Length: > 0 }
                ? new TournamentResult(declinedSuffix[0], declinedPrevious with { Victory = false,
                    Kind = EvidenceKind.EstimatedCutoff, CutoffReason = "refinement-awaiting-actual-root-replay" },
                    [], 0, 0, "refinement-proposal") { Script = declinedSuffix } : null;
            ClearTournamentScript("refinement complete-state cache miss; re-root actual board; "
                + declined.FirstMismatch(declinedActualEvidence));
            rootSession = declinedActual;
            mixedProposal = declinedProposal;
            return;
        }
        // No waiting: when the turn arrives the current script or ordinary
        // foreground search owns the action, whether this trial finished or not.
        var eligible = combat.RoundNumber == refinementRound
            && refinementRequest is { IsCancelled: false } request
            && job.Result is { } result && result.IsFor(request)
            && job.Result?.Script is { Count: > 0 } && job.BoundaryState is not null
            && (job.Baseline is not null || job.Result.Record is { Verified: true })
            && tournamentScriptIndex == job.BoundaryIndex;
        if (!eligible)
        {
            ClearRefinement($"not ready at boundary liveRound={combat.RoundNumber} cursor={tournamentScriptIndex}/{job.BoundaryIndex} "
                + $"baseline={job.Baseline is not null} result={job.Result?.Script?.Count ?? 0}");
            return;
        }
        // Both suffixes were evaluated from the same predicted state. Preserve
        // the original on ties; a cutoff never displaces a verified win.
        var chosen = job.Improved ? job.Result! : job.Baseline!;
        var improved = job.Improved;
        var boundary = job.BoundaryState;
        // P0-SIM-2（2026-10-05 伪 VICTORY）：胜利记录是唯一"错了就是全灭"的终局。文本比较
        // 已经覆盖逐席定向 Power（P0-SIM-1 的 PT 段）与手牌/HP，但这里再按名字断言两件终局事实：
        //   ① 复验后的终局至少有一名预测存活席位；
        //   ② 验证边界与实机的"定向 Power 逐席身份"一致（独立于文本渲染器的第二道防线）。
        // 任何一条不成立都拒绝采用，而不是带着这份胜利继续打。
        if (chosen.Record is { Verified: true, Victory: true })
        {
            var party = combat.Players;
            if (!chosen.Record.PostCombatHp.Any(hp => hp > 0))
            {
                ClearRefinement("victory refused: the validated replay leaves no surviving seat "
                    + $"postHp=[{string.Join(",", chosen.Record.PostCombatHp)}]");
                return;
            }
            var boundaryTargeted = boundary!.TargetedPowerState();
            var liveTargeted = KernelSession.TargetedPowerState(combat);
            if (!string.Equals(boundaryTargeted, liveTargeted, StringComparison.Ordinal))
            {
                ClearRefinement("victory refused: targeted power seats differ between the validated boundary and live "
                    + $"boundary=[{boundaryTargeted}] live=[{liveTargeted}]");
                return;
            }
            Log.Info($"CoopBots victory guard: round={combat.RoundNumber} "
                + $"survivors={chosen.Record.PostCombatHp.Count(hp => hp > 0)} postHp=[{string.Join(",", chosen.Record.PostCombatHp)}] "
                + $"sandpits-boundary=[{boundary.SandpitCountdowns(party)}] sandpits-live=[{KernelSession.SandpitCountdowns(combat, party)}]");
        }
        // Upstream's stamp alone does not describe captured multiplayer hidden
        // counters, cost expiry or physical card occurrences. The same exact
        // supported-state contract used by mixed input also qualifies this
        // future-root cache (owner's staged refactor, 2026-10-09).
        var actual = KernelCombatCapture.Capture(combat);
        var controlled = ControlledSeats(combat);
        var predictedEvidence = boundary!.PlanningState(controlled);
        var actualEvidence = actual.PlanningState(controlled);
        if (!predictedEvidence.Matches(actualEvidence))
        {
            // Generated native cards can be semantically equal yet be different
            // physical instances; unknown facts can also make reuse unavailable.
            // This is a cache miss, not proof of simulation drift or illegality.
            // Drop old checkpoints/terminal ownership; bounded actual-root replay
            // must create fresh evidence before this suffix can compete again.
            var proposal = chosen.Record is { } previous && chosen.Script is { Count: > 0 } suffix
                ? new TournamentResult(suffix[0], previous with { Victory = false,
                    Kind = EvidenceKind.EstimatedCutoff, CutoffReason = "refinement-awaiting-actual-root-replay" },
                    [], 0, 0, "refinement-proposal") { Script = suffix } : null;
            ClearTournamentScript("refinement complete-state cache miss; re-root actual board; "
                + predictedEvidence.FirstMismatch(actualEvidence));
            rootSession = actual;
            mixedProposal = proposal;
            return;
        }
        tournamentScript = chosen.Script;
        tournamentScriptRecord = chosen.Record;
        tournamentScriptAliveStates = chosen.ScriptAliveStates;
        tournamentScriptEnemyStates = chosen.ScriptEnemyStates;
        tournamentScriptIndex = 0;
        endTurnComparison = null; endTurnComparisonStamp = null;
        tournamentScriptRound = combat.RoundNumber;
        victoryCheckedRound = combat.RoundNumber;
        // A matching suffix funds THIS turn. A failed/missing next trial must
        // still fall back to foreground comparison on a non-winning line.
        tournamentReplanNextRound = chosen.Record is not { Verified: true, Victory: true };
        Log.Info($"CoopBots refinement: adopted round={combat.RoundNumber} improved={improved} steps={chosen.Script!.Count} "
            + $"old={Describe(job.Baseline?.Record)} new={Describe(chosen.Record)} "
            + $"enemyHp={job.Baseline?.Record?.RemainingEnemyHp}->{chosen.Record?.RemainingEnemyHp} "
            + $"rounds={job.Baseline?.Record?.RoundsSurvived}->{chosen.Record?.RoundsSurvived} validated={job.ValidatedCandidates}");
        ClearRefinement(improved ? "adopted-improvement" : "kept-baseline");
        StartRefinement(boundary, combat, chosen.Script);
    }

    internal static bool RefinementMatches(string? expected, string actual)
        => expected is not null && string.Equals(expected, actual, StringComparison.Ordinal);

    private void ClearRefinement(string reason)
    {
        if (refinement is not null)
            Log.Info($"CoopBots refinement: disposed targetRound={refinementRound} reason={reason} "
                + $"ready={refinementReported} stop={refinement.StopReason} compute={refinement.ComputeMs:F1}ms "
                + $"failure={refinement.FailureDetail}");
        if (refinementRequest is { } request)
            Log.Info($"CoopBots refinement request: generation={request.RootGeneration} reason={reason} "
                + $"budget={request.BudgetMs} wall={request.ElapsedMs:F1}ms stop={request.StopReason}");
        refinementRequest?.Cancel(reason); refinementRequest = null;
        refinement = null; refinementScript = null; refinementReported = false;
    }
}
