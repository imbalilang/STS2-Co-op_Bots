using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models.Cards;
using CoopBots.Kernel.Vendor.Engine.Common;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class PotionGenerationCardMirrors
{
    public static void AlchemizeOnPlay(Alchemize card, CardOnPlayMirrorContext context)
    {
        var potion = PotionFactory.CreateRandomPotionInCombat(
            card.Owner,
            context.Rng.CombatPotionGeneration);
        if (context.State.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("炼制药水结算缺少可写的预测状态。");
        bool procured = effects.TryProcurePotion(card.Owner, potion);
        if (procured && context.State.CombatState is SimulatedCombatState combat)
        {
            combat.RecordLongTermResource(20);
            combat.RecordGrowthReward(GrowthSource.Alchemize);
        }
        context.Simulator.History.PotionGenerated(potion);
    }
}
