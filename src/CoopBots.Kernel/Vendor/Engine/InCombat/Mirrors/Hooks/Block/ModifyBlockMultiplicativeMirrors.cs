using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.ValueProps;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Block;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyBlockMultiplicativeMirrorContext, decimal>;

/// <summary>
/// Mirrors the multiplicative listener pass inside <see cref="Hook.ModifyBlock"/>
/// </summary>
internal static class ModifyBlockMultiplicativeMirrors
{
    private static readonly MirrorMethodSpec ModifyBlockMultiplicative = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyBlockMultiplicative),
        [typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(CardModel), typeof(CardPlay)]);

    private static readonly Registry Registry = CreateRegistry();

    public static decimal Invoke(AbstractModel listener, ModifyBlockMultiplicativeMirrorContext context)
    {
        if (Registry.TryInvokeRegistered(listener, context, out var result))
        {
            return result.Value;
        }

        return listener.ModifyBlockMultiplicative(
            context.Target,
            context.Amount,
            context.Props,
            context.CardSource?.Preview,
            context.CardPlay);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(ModifyBlockMultiplicative);

        registry.Register<PaelsLegion>(HandlePaelsLegion);
        registry.Register<Vambrace>(HandleVambrace);
        registry.Register<MultiplayerScalingModel>(HandleMultiplayerScaling);
        registry.Register<UnmovablePower>(HandleUnmovablePower);

        return registry;
    }

    private static decimal HandleUnmovablePower(
        UnmovablePower power,
        ModifyBlockMultiplicativeMirrorContext context)
    {
        if (context.Target.IsMonster
            || !context.Props.IsCardOrMonsterMove()
            || context.CardSource is not null
                && context.CardSource.Preview.Owner.Creature != power.Owner)
        {
            return 1m;
        }

        SimulatedCombatState combat = context.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Unmovable prediction requires SimulatedCombatState.");
        int earlierEvents = combat.GetBlockCardsPlayedThisTurn(power.Owner)
            - context.Simulator.GetPoweredBlockEvents(context.CardPlay);
        return earlierEvents < power.Amount ? 2m : 1m;
    }

    private static decimal HandleMultiplayerScaling(
        MultiplayerScalingModel _,
        ModifyBlockMultiplicativeMirrorContext context)
    {
        // CoopBots multiplayer port: the upstream guard refused playerCount != 1,
        // which made every block card throw in real multiplayer and forced the
        // kernel to skip all defense. Mirror the native scaling instead: player
        // block is unscaled, enemy block scales with the party size.
        var target = context.Target;
        if (target != null && !target.IsPrimaryEnemy && !target.IsSecondaryEnemy) return 1m;
        if (!context.Props.IsPoweredCardOrMonsterMoveBlock()) return 1m;
        int playerCount = context.State.CombatState.Players.Count;
        if (playerCount <= 2) return playerCount;
        return playerCount * MultiplayerScalingModel.GetMultiplayerScaling(
            context.State.CombatState.Encounter, context.State.CombatState.RunState.CurrentActIndex);
    }

    private static decimal HandlePaelsLegion(
        PaelsLegion relic,
        ModifyBlockMultiplicativeMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new PaelsLegionPredictionState(relic));
        return context.Props.IsCardOrMonsterMove() &&
            context.CardSource?.Preview.Owner == relic.Owner &&
            state.Cooldown <= 0
                ? 2
                : 1;
    }

    private static decimal HandleVambrace(Vambrace relic, ModifyBlockMultiplicativeMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new VambracePredictionState(relic));
        return context.Props.IsCardOrMonsterMove() &&
            context.CardSource?.Preview.Owner == relic.Owner &&
            !state.BlockGainedThisCombat &&
            (state.TriggeringCard is null || context.CardSource.Original == state.TriggeringCard)
                ? 2
                : 1;
    }
}

internal sealed class ModifyBlockMultiplicativeMirrorContext : CombatMirrorContext
{
    public required Creature Target { get; init; }

    public required decimal Amount { get; set; }

    public required ValueProp Props { get; init; }

    public required PredictedCard? CardSource { get; init; }

    public required CardPlay? CardPlay { get; init; }
}
