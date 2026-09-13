using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal static class PersistentRelicSupport
{
    public static bool ShouldPlayerResetEnergy(SimulatedCombatState combat, Player player)
    {
        int simulatedTurn = combat.GetPlayerTurnNumber(player);
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            if (listener is IceCream iceCream && ReferenceEquals(iceCream.Owner, player))
            {
                if (simulatedTurn > 1)
                    return false;
                continue;
            }
            if (!listener.ShouldPlayerResetEnergy(player))
                return false;
        }
        return true;
    }

    public static bool ShouldFlush(SimulatedCombatState combat, Player player)
    {
        int simulatedTurn = combat.GetPlayerTurnNumber(player);
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            if (listener is RingingTriangle triangle && ReferenceEquals(triangle.Owner, player))
            {
                if (simulatedTurn <= 1)
                    return false;
                continue;
            }
            if (!listener.ShouldFlush(player))
                return false;
        }
        return true;
    }

    public static int ModifyPowerAmountGiven(
        SimulatedCombatState combat,
        PowerModel power,
        Creature giver,
        Creature target,
        int amount,
        CardModel? cardSource)
    {
        if (power is not PoisonPower)
            return amount;

        decimal modified = amount;
        foreach (SneckoSkull skull in combat.IterateHookListeners().OfType<SneckoSkull>())
            modified += skull.ModifyPowerAmountGivenAdditive(power, giver, modified, target, cardSource);
        return (int)modified;
    }

    public static void TriggerAfterPreventingBlockClear(
        CombatPredictionSimulator simulator,
        AbstractModel? preventer,
        Creature creature)
    {
        if (preventer is not SturdyClamp clamp
            || !ReferenceEquals(clamp.Owner.Creature, creature))
        {
            return;
        }

        SimCreatureState state = simulator.State.GetCreature(creature);
        if (state.Block > clamp.DynamicVars.Block.IntValue)
            state.DamageBlock(state.Block - clamp.DynamicVars.Block.IntValue, ValueProp.Unpowered);
    }
}
