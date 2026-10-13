"""Preserve the relic choice adapter's token resolver on vendor reimport."""
from kernel_power_fork_adaptations import apply_power_fork_adaptations


def apply_relic_choice_adaptations(relative, text):
    text = apply_power_fork_adaptations(relative, text)
    if relative == 'Search/SimulatedCombatState.ReactiveRelics.cs':
        anchor = 'internal sealed partial class SimulatedCombatState\n{'
        addition = '''
    // Native PaelsEye.AfterSideTurnStart sees the complete participant roster,
    // including the negative case for an owner excluded from an extra turn.
    // The upstream owner-scoped hook cannot observe that negative case.
    public void SetPlayerTurnParticipation(IReadOnlyList<Player> participants)
    {
        foreach (Player seat in Players.Where(seat =>
                     _predictionState?.GetCreature(seat.Creature).IsAlive ?? seat.Creature.IsAlive))
        foreach (PaelsEye relic in RelicsOf(seat).OfType<PaelsEye>().Where(static r => !r.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            SetStatefulRelicState(relic, state with { Previous = participants.Contains(seat) ? 1 : 0 });
        }
    }
'''
        if text.count(anchor) != 1:
            raise RuntimeError('Player turn participation seam changed; review Paels Eye lifecycle.')
        return text.replace(anchor, anchor + addition)
    if relative == 'Engine/InCombat/Simulation/CombatPredictionSimulator.EndTurn.cs':
        # The host's multiplayer extra-turn lifecycle ends only the participating
        # seats. Preserve the optional roster while single-player callers retain
        # the upstream default.
        anchor = 'internal bool SimulateEndPlayerTurnBeforeOrbPassives(int playerTurn)'
        replacement = ('internal bool SimulateEndPlayerTurnBeforeOrbPassives(int playerTurn, '
                       'IReadOnlyList<Player>? participants = null)')
        roster = 'var playersEndingTurn = State.CombatState.Players;'
        if text.count(anchor) != 1 or text.count(roster) != 1:
            raise RuntimeError('Player end-turn participant seam changed; review extra-turn lifecycle.')
        return text.replace(anchor, replacement).replace(roster,
            'var playersEndingTurn = participants ?? State.CombatState.Players;')
    if relative == 'Search/CardChoiceSupport.cs':
        # Preserve the existing production null-safe implicit selection: the
        # Kernel cursor intentionally has no display-name service. This is a
        # retained support repair, not a new selection policy.
        anchor = 'ToTokens(spec.Options, spec.Options, spec.SourceCards, displayNames.Card), ContextId: spec.ContextId)'
        replacement = '''// displayNames is optional here — KernelSession.ChoiceCursor passes null, and
                // every other path in this file tolerates that (see the identical
                // `displayNames?.Card(card) ?? card.Id.Entry` below). Passing the METHOD GROUP
                // `displayNames.Card` created a delegate bound to a null instance, so the
                // implicit-all-selection branch threw
                // "Delegate to an instance method cannot have null 'this'" the moment it ran —
                // which is what surfaced as the SURVIVOR:prediction-exception boundary.
                ToTokens(spec.Options, spec.Options, spec.SourceCards,
                    card => displayNames?.Card(card) ?? card.Id.Entry), ContextId: spec.ContextId)'''
        if text.count(anchor) != 1:
            raise RuntimeError('Implicit-all null display-name seam changed; review the retained choice support repair.')
        return text.replace(anchor, replacement)
    if relative != 'Prediction/TurnStartChoiceSupport.cs':
        return text
    anchor = '    private static IReadOnlyList<PredictedCard> ResolveTokens('
    replacement = ('    // Adapter visibility: the shared live/rollout relic policy resolves the\n'
                   '    // same tokens with these native cardinality and occurrence checks.\n'
                   '    internal static IReadOnlyList<PredictedCard> ResolveTokens(')
    if text.count(anchor) != 1:
        raise RuntimeError('Relic token resolver changed; review the adapter visibility.')
    return text.replace(anchor, replacement)
