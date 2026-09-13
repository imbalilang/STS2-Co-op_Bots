using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CoopBots.Kernel.Vendor.Engine.Common;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    public TargetType GetTargetType(PredictedCard card)
    {
        if (State.CombatState is not SimulatedCombatState combat)
            return card.Preview.TargetType;

        return card.Preview switch
        {
            Shiv when combat.GetAmount<FanOfKnivesPower>(card.Preview.Owner.Creature) > 0
                => TargetType.AllEnemies,
            SovereignBlade when combat.GetAmount<SeekingEdgePower>(card.Preview.Owner.Creature) > 0
                => TargetType.AllEnemies,
            _ => card.Preview.TargetType,
        };
    }
}
