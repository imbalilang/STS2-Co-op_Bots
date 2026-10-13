using System.Diagnostics;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private KernelPlanContinuation? mixedContinuation;
    private long mixedInputRevision;
    private string mixedControl = "";
    private int mixedCheckedCursor = -1;
    private TournamentResult? mixedProposal;
    private KernelScriptReplayRun? mixedProposalReplay;
    private Stopwatch? mixedProposalClock;

    private static ulong[] ControlledSeats(CombatState combat)
        => combat.Players.Where(p => AutoPilot.Drives(p.NetId)).Select(p => p.NetId).Order().ToArray();
    private static string ControlKey(CombatState combat) => string.Join(',', ControlledSeats(combat));
    private static bool MixedTable(CombatState combat)
        => combat.Players.Any(p => p.Creature.IsAlive && !AutoPilot.Drives(p.NetId));

    private void CaptureMixedContinuation(KernelSession? root, CombatState combat,
        IReadOnlyList<KernelTeamSearch.Action> script, int cursor = 1)
    {
        mixedContinuation = MixedTable(combat) && root?.CanFork == true
            ? new KernelPlanContinuation(root, script, ControlledSeats(combat)) : null;
        mixedInputRevision = KernelInputObserver.Revision;
        mixedControl = ControlKey(combat);
        mixedCheckedCursor = -1;
        if (mixedContinuation is not null)
            Log.Info($"CoopBots mixed plan: captured cursor={cursor} inputRevision={mixedInputRevision} committedWindow=4");
    }

    // The chosen line remains a proposal after external input. Equality can
    // renew a small action window; changed state requires native re-evaluation
    // from the settled actual root. Legality alone never renews plan quality.
    private bool ReviewMixedInput(CombatState combat)
    {
        // A handover can turn a mixed table into all-Bot between ticks. The
        // existing mixed lease still belongs to the previous control domain.
        if (tournamentScript is null || !MixedTable(combat) && mixedContinuation is null) return false;
        var changed = mixedInputRevision != KernelInputObserver.Revision || mixedControl != ControlKey(combat);
        // Inspect at most once per four deployed actions without new input.
        // This keeps a short execution commitment while retaining the searched
        // suffix as a candidate. No action is simulated on the human's behalf.
        if (!changed && mixedContinuation is not null
            && (tournamentScriptIndex % 4 != 0 || mixedCheckedCursor == tournamentScriptIndex)) return false;
        if (mixedContinuation is not null
            && !mixedContinuation.AdvanceTo(tournamentScriptIndex, TimeSpan.FromMilliseconds(TournamentSliceMs), tournamentRequest)) return true;
        var actual = KernelCombatCapture.Capture(combat);
        var controlled = ControlledSeats(combat);
        var actualEvidence = actual.PlanningState(controlled);
        if (mixedContinuation is { Failure.Length: 0 } continuation
            && mixedControl == ControlKey(combat) && continuation.Evidence.Matches(actualEvidence))
        {
            // A branch previously explored by the chosen plan becomes the new
            // exact root. Do not pay for the same search merely because an
            // external action queue ID changed without semantic consequences.
            rootSession = continuation.ForkMatchedState(actualEvidence);
            mixedInputRevision = KernelInputObserver.Revision;
            mixedCheckedCursor = tournamentScriptIndex;
            Log.Info($"CoopBots mixed plan: exact continuation reused cursor={tournamentScriptIndex} inputRevision={mixedInputRevision}");
            return false;
        }

        var suffix = tournamentScript.Skip(tournamentScriptIndex).ToArray();
        if (suffix.Length > 0 && suffix.All(a => a.Potion is null) && tournamentScriptRecord is { } old)
            mixedProposal = new(suffix[0], old with { Victory = false, Kind = EvidenceKind.EstimatedCutoff,
                CutoffReason = "external-input-awaiting-native-replay" }, [], 0, 0, "mixed-proposal") { Script = suffix };
        var cause = changed ? "settled external input or control change" : "committed action window requires a new root";
        // A failed reuse predicate can mean different physical instances,
        // unresolved selectors or planning metadata. It is not by itself a
        // native/live equivalence test. Existing projection, card identity and
        // turn-boundary discrepancy channels retain their R9 obligations.
        Log.Info($"CoopBots mixed plan: re-root reason={cause} cursor={tournamentScriptIndex} proposalSteps={mixedProposal?.Script?.Count ?? 0}");
        ClearTournamentScript(cause);
        // The decision point already discarded the preceding root's work.
        // Keep the current request: prefix review/capture are part of its wall
        // allowance, rather than granting a fresh budget after a cache miss.
        return false;
    }

    private bool ReplayMixedProposal()
    {
        if (mixedProposal is null || tournamentPhase != TournamentPhase.NoPotions) return false;
        mixedProposalReplay ??= new(tournamentRoot!, tournamentParty!, mixedProposal, 80, tournamentRequest);
        mixedProposalClock ??= Stopwatch.StartNew();
        mixedProposalReplay.Advance(TimeSpan.FromMilliseconds(TournamentSliceMs));
        if (!mixedProposalReplay.Done && mixedProposalClock.Elapsed.TotalMilliseconds < 120
            && tournamentRequest?.IsStopped != true) return true;
        if (mixedProposalReplay.Result is { Record.Verified: true } verified)
            tournamentOptions = tournamentOptions! with { ReferenceResult = verified };
        Log.Info($"CoopBots mixed proposal: replayed={mixedProposalReplay.Result?.Record?.Verified == true} "
            + $"stop={mixedProposalReplay.Failure} wall={mixedProposalClock.Elapsed.TotalMilliseconds:F1}ms "
            + $"generation={tournamentRequest?.RootGeneration}");
        mixedProposal = null; mixedProposalReplay = null; mixedProposalClock = null;
        return false;
    }

    internal void CancelPendingForExternalInput()
    {
        // AfterFinished observes a settled input; cancelling cannot interrupt a
        // native worker transaction. The next stable Poll captures the board.
        tournamentRequest?.Cancel("settled external player input");
        tournamentRun?.Cancel("settled external player input");
    }
}
