using MegaCrit.Sts2.Core.Combat;
using CoopBots.Kernel;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    // A native choice fallback is legal, but its result was not the choice priced
    // by this search. Cancel only our dependent work: the running card must finish
    // normally before BotRuntime's existing busy-action guard permits a new capture.
    internal void InvalidateChoiceDependentState(CombatState combat, string detail)
        => ClearChoiceDependentState(combat, detail, externalInput: false);

    internal void InvalidateExternalTurnChoiceState(CombatState combat, string detail)
    {
        if (RetireMixedChoiceContracts(combat, "future turn-start plan superseded by external input: " + detail))
        {
            NoteMixedInput();
            return;
        }
        ClearChoiceDependentState(combat, detail, externalInput: true);
    }

    // Future turn-start contracts and terminal evidence depend on the old
    // root. Keep immediate card-choice contracts: their complete native menu
    // identity is still checked when opened, preserving the searched answer.
    // A queued action owns its existing receipt and choice transaction.
    internal bool RetireMixedChoiceContracts(CombatState combat, string reason)
    {
        if (!ReferenceEquals(tournamentScriptCombat, combat) || !HasTournamentScript || !UsesMixedExecution(combat))
            return false;
        var old = tournamentScript!;
        var first = tournamentScriptIndex + (preparedSubmission?.Queued is not null ? 1 : 0);
        var retained = old.ToArray();
        for (var i = first; i < retained.Length; i++)
            retained[i] = retained[i] with { TurnStartChoices = null };
        tournamentScript = retained;
        mixedHistoricalScript = retained;
        if (preparedSubmission is { Queued: not null } queued)
        { queued.Script = retained; queued.Cursor = tournamentScriptIndex; }
        else AbandonPreparedSubmission(reason);
        if (tournamentScriptRecord is { } record)
            tournamentScriptRecord = record with { Victory = false, Kind = EvidenceKind.EstimatedCutoff,
                CutoffReason = "mixed-choice-contract-retired-quality-unverified", IrreversibleLoss = 0 };
        mixedContinuation = null;
        mixedProposal = null; mixedProposalReplay = null; mixedProposalClock = null;
        ClearMixedReplan(reason);
        ClearRefinement(reason);
        ClearTournament(reason);
        victoryReview = null; victoryReviewRoot = null; victoryReviewStamp = null; victoryCheckedRound = 0;
        shortVictory = null; shortVictoryStamp = null;
        endTurnComparison = null; endTurnComparisonStamp = null; endTurnComparisonIndex = -1;
        ConfirmedEndTurnChoices = null;
        ReportSearchLine(reason + $"; retired future turn-start contracts, retained physical suffix and validated card menus cursor={tournamentScriptIndex}/{retained.Length}");
        return true;
    }

    private void ClearChoiceDependentState(CombatState combat, string detail, bool externalInput)
    {
        var ownsCombat = ReferenceEquals(tournamentScriptCombat, combat)
            || ReferenceEquals(rootCombat, combat) || ReferenceEquals(planCombat, combat)
            || tournamentParty?.Any(p => ReferenceEquals(p.Creature.CombatState, combat)) == true
            || ReferenceEquals(ConfirmedEndTurn?.Creature.CombatState, combat)
            || ReferenceEquals(ConfirmedPotion?.Potion.Owner?.Creature.CombatState, combat);
        if (!ownsCombat) return;
        var reason = (externalInput ? "future turn-start plan superseded by external input: "
            : "planned choice differs from prediction: ") + detail;
        ReportSearchLine(reason + "; clearing dependent search and continuation caches");
        ClearTournamentScript(reason);
        DropContinuation(reason);
        ClearRefinement(reason);
        ClearTournament();
        Reset();
        DiscardConfirmation();
        // ClearTournamentScript normally clears these, but it intentionally does
        // nothing when a legacy continuation or an unfinished search owns the turn.
        tournamentScriptRecord = null; tournamentScriptAliveStates = null; tournamentScriptEnemyStates = null;
        tournamentScriptCombat = null; tournamentScriptIndex = 0;
        victoryReview = null; victoryReviewRoot = null; victoryReviewStamp = null; victoryCheckedRound = 0;
        shortVictory = null; shortVictoryStamp = null;
        endTurnComparison = null; endTurnComparisonStamp = null; endTurnComparisonIndex = -1;
        tournamentDeclined = null; tournamentPoint = null;
        planCommitted = false; planSentinelPendingTicks = 0; noActionStamp = "";
    }
}
