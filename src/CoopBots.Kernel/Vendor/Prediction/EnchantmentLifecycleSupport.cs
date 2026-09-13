using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal static class EnchantmentLifecycleSupport
{
    public static void BeforeFlush(CombatPredictionSimulator simulator, Player player)
    {
        foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).Hand)
        {
            if (card.Preview.Enchantment is SlumberingEssence)
                card.MutablePreview.EnergyCost.AddUntilPlayed(-1);
        }
    }

    public static bool TriggerAutoPrePlay(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        int turnNumber,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        if (turnNumber > 1)
            return false;

        PredictedCard[] imbuedCards = simulator.State.GetPlayerCombatState(player).AllCards
            .Where(card => card.Preview.Enchantment is Imbued)
            .ToArray();
        for (int index = 0; index < imbuedCards.Length; index++)
        {
            PredictedCard card = imbuedCards[index];
            if (!combat.AutoPlayWithChoice(
                    simulator,
                    card,
                    card.Preview.Enchantment!.Id.Entry,
                    $"{card.Preview.Id.Entry}+{card.Preview.CurrentUpgradeLevel}#{index}",
                    choices,
                    processedEnemyDeaths))
            {
                return true;
            }
        }
        return false;
    }

    public static void TriggerAfterTurnStartOrbs(CombatPredictionSimulator simulator, Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        foreach (PlasmaOrb orb in state.OrbQueue.Orbs.OfType<PlasmaOrb>())
            simulator.GainEnergy(player, orb.PassiveVal);
    }
}
