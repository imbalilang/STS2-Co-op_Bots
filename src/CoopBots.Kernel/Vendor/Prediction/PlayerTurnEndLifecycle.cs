using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal static class PlayerTurnEndLifecycle
{
    public static bool RunPhaseTwo(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> participants,
        int etherealExhaustCount = 0, IReadOnlyDictionary<Creature, int>? etherealByOwner = null)
    {
        if (!CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(
                simulator, combat, participants, etherealExhaustCount, etherealByOwner)
            || !TurnStartRelicSupport.TriggerAfterSideTurnEnd(
                simulator, combat, participants, etherealExhaustCount, etherealByOwner)
            || !EndTurnPowerSupport.TriggerLate(simulator, combat, participants))
        {
            return false;
        }
        combat.NormalizeCardAfflictions(simulator);
        return true;
    }

    public static bool RunPhaseOne(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        IReadOnlyList<Creature> participants)
    {
        EndTurnPowerSupport.TriggerVeryEarly(combat, participants);
        if (combat.HasPendingChoice)
            return false;
        TurnStartRelicSupport.TriggerBeforeSideTurnEnd(simulator, combat, participants);
        if (combat.HasPendingChoice)
            return false;
        if (!OrbLifecycleSupport.TriggerBeforeTurnEnd(simulator, combat, player)
            || combat.HasPendingChoice
            || !simulator.SimulateEndPlayerTurnAfterOrbPassives(combat.GetPlayerTurnNumber(player)))
        {
            return false;
        }
        CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(combat, participants);
        return !combat.HasPendingChoice;
    }
}
