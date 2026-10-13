using MegaCrit.Sts2.Core.Combat;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    // A native choice fallback is legal, but its result was not the choice priced
    // by this search. Cancel only our dependent work: the running card must finish
    // normally before BotRuntime's existing busy-action guard permits a new capture.
    internal void InvalidateChoiceDependentState(CombatState combat, string detail)
        => ClearChoiceDependentState(combat, detail, externalInput: false);

    internal void InvalidateExternalTurnChoiceState(CombatState combat, string detail)
        => ClearChoiceDependentState(combat, detail, externalInput: true);

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
