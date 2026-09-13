using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyStarCostMirrorContext, decimal>;

// Mirrors Hook.ModifyStarCost while selectively replacing listeners that read card-play state.
internal static class ModifyStarCostMirrors
{
    private static readonly MirrorMethodSpec ModifyStarCost = MirrorMethodSpec.Hook(
        nameof(AbstractModel.TryModifyStarCost),
        [typeof(CardModel), typeof(decimal), typeof(decimal).MakeByRefType()]);

    private static readonly Registry Registry = CreateRegistry();

    public static decimal Invoke(AbstractModel listener, ModifyStarCostMirrorContext context)
    {
        if (Registry.TryInvokeRegistered(listener, context, out var result))
        {
            return result.Value;
        }

        listener.TryModifyStarCost(context.Card.Preview, context.Cost, out var modifiedCost);
        return modifiedCost;
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(ModifyStarCost);

        registry.Register<VoidFormPower>(HandleVoidFormPower);

        registry.Register<BrilliantScarf>(HandleBrilliantScarf);

        return registry;
    }

    private static decimal HandleVoidFormPower(VoidFormPower power, ModifyStarCostMirrorContext context)
    {
        return context.Card.Preview.Owner.Creature == power.Owner &&
            IsInPlayablePile(context) &&
            context.StateStore.Get(power, static value => new VoidFormPredictionState(value)).CardsPlayedThisTurn <
            power.Amount
                ? 0
                : context.Cost;
    }

    private static decimal HandleBrilliantScarf(BrilliantScarf relic, ModifyStarCostMirrorContext context)
    {
        return context.Card.Preview.Owner == relic.Owner &&
            IsInPlayablePile(context) &&
            context.StateStore.Get(relic, static value => new CounterPredictionState(value._cardsPlayedThisTurn)).Value ==
            relic.DynamicVars.Cards.IntValue - 1
                ? 0
                : context.Cost;
    }

    private static bool IsInPlayablePile(ModifyStarCostMirrorContext context)
    {
        return context.Card.GetPile(context.State)?.Type is PileType.Hand or PileType.Play;
    }
}

internal sealed class ModifyStarCostMirrorContext : CombatMirrorContext
{
    public required PredictedCard Card { get; init; }

    public required decimal Cost { get; set; }
}
