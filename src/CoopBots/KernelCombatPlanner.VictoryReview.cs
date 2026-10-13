using System.Diagnostics;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private int victoryCheckedRound;
    private KernelScriptReplayRun? victoryReview;
    private KernelSession? victoryReviewRoot;
    private string? victoryReviewStamp;
    private double victoryReviewMs;

    // Upstream trusts a stored ending. Owner 2026-10-04: a win predicted before
    // an enemy choice cannot permanently exempt a changed live state from end
    // comparison. Replay once at each unchecked round, in slices; no per-card
    // tournament and no rejection merely because a text field differs.
    private bool ReviewVictoryAtRound(CombatState combat)
    {
        if (victoryReview is null && (victoryCheckedRound == combat.RoundNumber
            || tournamentScriptRecord is not { Verified: true, Victory: true })) return false;
        var stamp = KernelSession.CaptureLivePartyText(combat);
        if (victoryReview is null || victoryReviewStamp != stamp)
        {
            var suffix = tournamentScript!.Skip(tournamentScriptIndex).ToArray();
            var old = tournamentScriptRecord!;
            tournamentScriptRecord = old with { Victory = false, Kind = EvidenceKind.EstimatedCutoff,
                CutoffReason = "live-round-awaiting-replay" };
            victoryCheckedRound = combat.RoundNumber;
            victoryReviewRoot = KernelCombatCapture.Capture(combat);
            if (!victoryReviewRoot.CanFork || suffix.Length == 0)
            {
                victoryReview = null; tournamentReplanNextRound = true; tournamentScriptRound = combat.RoundNumber;
                Log.Info("CoopBots victory review: unavailable; terminal guarantee withdrawn");
                return false;
            }
            victoryReview = new(victoryReviewRoot, combat.Players,
                new(suffix[0], old, [old], 0, 0, "live-round-review") { Script = suffix }, 80, tournamentRequest);
            victoryReviewMs = 0; victoryReviewStamp = stamp;
            ClearRefinement("live victory review");
            Log.Info($"CoopBots victory review: started round={combat.RoundNumber} steps={suffix.Length}");
        }
        var clock = Stopwatch.StartNew();
        victoryReview.Advance(TimeSpan.FromMilliseconds(TournamentSliceMs));
        victoryReviewMs += clock.Elapsed.TotalMilliseconds;
        if (!victoryReview.Done && victoryReviewMs < 120) return true;
        var result = victoryReview.Result;
        var failure = victoryReview.Failure;
        var done = victoryReview.Done;
        victoryReview = null;
        if (result is { Record: { Verified: true, Victory: true } }
            && (tournamentRequest is null || !tournamentRequest.IsCancelled && result.IsFor(tournamentRequest)))
        {
            CaptureMixedContinuation(victoryReviewRoot, combat, result.Script!, cursor: 0);
            tournamentScript = result.Script; tournamentScriptIndex = 0;
            tournamentScriptRecord = result.Record;
            tournamentScriptAliveStates = result.ScriptAliveStates; tournamentScriptEnemyStates = result.ScriptEnemyStates;
            tournamentScriptRound = combat.RoundNumber; tournamentReplanNextRound = false;
            Log.Info($"CoopBots victory review: confirmed round={combat.RoundNumber} compute={victoryReviewMs:F1}ms");
            StartRefinement(victoryReviewRoot, combat, result.Script!);
            return false;
        }
        Log.Info($"CoopBots victory review: withdrawn round={combat.RoundNumber} done={done} "
            + $"ending={Describe(result?.Record)} failure={failure} compute={victoryReviewMs:F1}ms");
        if (done && !failure.StartsWith("request-", StringComparison.Ordinal))
        {
            // 2026-10-05：剧本走完但没到终局（ending=replay-script-end）时 failure 为空，
            // 丢剧本的日志会印出空原因，现场无法归因。用终局记录的原因兜底，保证每个
            // 丢弃都带名字。
            var reason = string.IsNullOrWhiteSpace(failure) ? Describe(result?.Record) : failure;
            ClearTournamentScript("live replay no longer confirms the stored victory: " + reason);
            return true;
        }
        // A soft timeout is not proof of defeat. Keep executable actions but
        // allow the bounded end comparison and next-round foreground fallback.
        tournamentReplanNextRound = true; tournamentScriptRound = combat.RoundNumber;
        return false;
    }
}
