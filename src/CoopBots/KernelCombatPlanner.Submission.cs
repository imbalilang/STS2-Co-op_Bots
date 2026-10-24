using CoopBots.Kernel;
using MegaCrit.Sts2.Core.GameActions;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    // A paced decision is a reservation, not an executed script prefix. Only
    // the native action receipt can consume it. This is deliberately per planner.
    private sealed class PreparedSubmission
    {
        internal required IReadOnlyList<KernelTeamSearch.Action> Script;
        internal required int Cursor;
        internal TeamCombatPlanner.Decision? Decision;
        internal PotionPlan? Potion;
        internal MegaCrit.Sts2.Core.Entities.Players.Player? EndTurn;
        internal KernelTurnStartChoiceContract? EndChoices;
        internal GameAction? Queued;
    }
    private PreparedSubmission? preparedSubmission;
    private string? mixedStoppedPoint;
    internal bool HasPreparedSubmission => preparedSubmission is not null;

    private TournamentOutcome InstallCompletedMixedResult(TournamentResult result,
        MegaCrit.Sts2.Core.Combat.CombatState combat,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player> actors, bool humansFinished,
        out TeamCombatPlanner.Decision? decision)
    {
        AdoptTournamentScript(result, combat);
        FinishTournament(TournamentOutcome.Pending);
        if (TryEmitFromTournamentScript(combat, actors, humansFinished, out decision))
            return TournamentOutcome.Decided;
        return tournamentScript is not null ? TournamentOutcome.Pending : TournamentOutcome.Declined;
    }

    private TournamentOutcome ParkStoppedMixedSearch(string point, string reason)
    {
        mixedStoppedPoint = point;
        MegaCrit.Sts2.Core.Logging.Log.Warn($"CoopBots mixed search: no settled candidate at {reason}; "
            + "waiting for a changed actual root; no legacy action or renewed allowance");
        return FinishTournament(TournamentOutcome.Pending);
    }
    private readonly HashSet<MegaCrit.Sts2.Core.Models.CardModel> mixedPhysicalCards = new(ReferenceEqualityComparer.Instance);

    private void RememberMixedPhysicalCards(MegaCrit.Sts2.Core.Combat.CombatState combat)
    {
        mixedPhysicalCards.Clear();
        foreach (var player in combat.Players)
            if (player.PlayerCombatState is { } state)
                foreach (var card in state.Hand.Cards.Concat(state.DrawPile.Cards)
                    .Concat(state.DiscardPile.Cards).Concat(state.ExhaustPile.Cards)) mixedPhysicalCards.Add(card);
    }

    private MegaCrit.Sts2.Core.Models.CardModel? ResolveMixedPhysicalCard(KernelTeamSearch.Action next, out string refusal)
    {
        if (next.Card is { } original && mixedPhysicalCards.Contains(original))
        {
            var present = ReferenceEquals(original.Owner, next.Player)
                && next.Player.PlayerCombatState?.Hand.Cards.Contains(original) == true;
            refusal = present ? "" : "physical-card-left-hand";
            return present ? original : null;
        }
        // Generated future cards have no native object at the captured root.
        return KernelSession.ResolvePlannedCard(next.Player, next.Card,
            next.CardStateKey, next.CardStateOccurrence, out refusal);
    }

    private void PrepareTournamentSubmission(TeamCombatPlanner.Decision? decision = null)
    {
        if (tournamentScriptCombat is not { } combat || !UsesMixedExecution(combat))
        { tournamentScriptIndex++; return; }
        preparedSubmission = new() { Script = tournamentScript!, Cursor = tournamentScriptIndex,
            Decision = decision, Potion = ConfirmedPotion, EndTurn = ConfirmedEndTurn,
            EndChoices = ConfirmedEndTurnChoices };
    }

    private Status PollPreparedSubmission(bool humansFinished, out TeamCombatPlanner.Decision? decision)
    {
        var pending = preparedSubmission!;
        decision = null;
        if (pending.Queued is not null || pending.EndTurn is not null && !humansFinished) return Status.Pending;
        decision = pending.Decision;
        ConfirmedPotion = pending.Potion;
        ConfirmedEndTurn = pending.EndTurn;
        ConfirmedEndTurnChoices = pending.EndChoices;
        return Status.Ready;
    }

    private bool PreparedSubmissionStillExecutable(MegaCrit.Sts2.Core.Combat.CombatState combat)
    {
        if (preparedSubmission is not { } pending) return false;
        // An enqueued native transaction already owns its receipt. Do not
        // reinterpret a mid-action hand as a failed unsubmitted proposal.
        if (pending.Queued is not null) return true;
        if (!ReferenceEquals(tournamentScriptCombat, combat)) return false;
        var step = pending.Script[pending.Cursor];
        if (!AutoPilot.Drives(step.Player.NetId) || !step.Player.Creature.IsAlive) return false;
        if (pending.Decision is { } decision)
            return ReferenceEquals(decision.Move.Card.Owner, step.Player)
                && step.Player.PlayerCombatState?.Hand.Cards.Contains(decision.Move.Card) == true
                && decision.Move.Card.CanPlayTargeting(decision.Move.Target);
        return true;
    }

    // A control handback may remove other seats' steps. Preserve this exact
    // reserved action when filtering the suffix, without matching by card name.
    private void RebindPreparedSubmission()
    {
        if (preparedSubmission is not { } pending) return;
        if (tournamentScript is { } current && tournamentScriptIndex < current.Count
            && pending.Cursor < pending.Script.Count
            && ReferenceEquals(current[tournamentScriptIndex], pending.Script[pending.Cursor]))
        { pending.Script = current; pending.Cursor = tournamentScriptIndex; }
        else AbandonPreparedSubmission("control change retired the reserved action");
    }

    internal void ConfirmActionQueued(GameAction action)
    {
        if (preparedSubmission is not { } pending || pending.Queued is not null) return;
        var step = pending.Script[pending.Cursor];
        var matches = action.OwnerId == step.Player.NetId && (pending.Decision is { } cardDecision
            ? action is PlayCardAction card && card.CardModelId == cardDecision.Move.Card.Id
            : pending.Potion is { } potionPlan
                ? action is UsePotionAction potion && potion.PotionIndex == potionPlan.Potion.Owner.GetPotionSlotIndex(potionPlan.Potion)
                : pending.EndTurn is not null && action is EndPlayerTurnAction);
        if (!matches)
        {
            AbandonPreparedSubmission("native action did not match the reserved step");
            throw new InvalidOperationException("The native action does not match the reserved tournament step.");
        }
        pending.Queued = action;
        action.AfterFinished += PreparedActionFinished;
        action.BeforeCancelled += PreparedActionCancelled;
    }

    private void PreparedActionFinished(GameAction action)
    {
        if (preparedSubmission is not { } pending || !ReferenceEquals(pending.Queued, action)) return;
        var consume = ReferenceEquals(tournamentScript, pending.Script) && tournamentScriptIndex == pending.Cursor;
        AbandonPreparedSubmission("native action settled");
        if (consume) tournamentScriptIndex++;
    }

    private void PreparedActionCancelled(GameAction action)
    {
        if (ReferenceEquals(preparedSubmission?.Queued, action))
            AbandonPreparedSubmission("native action cancelled");
    }

    internal void AbandonPreparedSubmission(string reason)
    {
        if (preparedSubmission?.Queued is { } action)
        {
            action.AfterFinished -= PreparedActionFinished;
            action.BeforeCancelled -= PreparedActionCancelled;
        }
        preparedSubmission = null;
    }
}
