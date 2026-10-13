using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    // Only called on a branch already projected for this owner (or the whole
    // party). This is a per-evaluation path, not a cached residual state: current
    // powers and the fixed opportunity reference are still read on each call.
    internal double PersistentValueAfterTemporaryStatExpiry(Player player, int incoming,
        int hits, KernelSession reference)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        // These are scheduled one-time resources, not permanent scaling.
        // Use the same energy/card units as the tactical state value, discounted
        // until the next round; the lifecycle consumes each effect exactly once.
        var scheduled = Power<EnergyNextTurnPower>(player.Creature) * 3 * .9
            + Power<DrawCardsNextTurnPower>(player.Creature) * .7 * .9;
        var powers = Combat.EffectivePowers().Where(p => p.Owner == player.Creature
            && p is not EnergyNextTurnPower and not DrawCardsNextTurnPower
            && (StrategicEffectMirrors.Contributes(p, player.Creature) || p is WraithFormPower)).ToArray();
        if (powers.Length == 0) return scheduled;
        var requirements = powers.Aggregate(StrategicEffectRequirements.None,
            (requirements, power) => requirements | StrategicEffectModel.Requirements(power)
                | EngineRequirements(power));
        var pcs = reference.simulator.State.GetPlayerCombatState(player);
        var cards = pcs.DiscardPile.Cards.Concat(pcs.DrawPile.Cards).Concat(pcs.Hand.Cards).ToArray();
        var context = StrategicEffectContext.Build(cards, reference.RemainingOpposition().Hp, incoming, hits, requirements);
        var opportunities = powers.Any(ExtendedPowerValueSupported) ? reference.ReadPowerOpportunities(player,cards) : default;
        return scheduled + powers.Sum(p => EngineValue(p, player, reference, context, opportunities)
            ?? StrategicEffectModel.Evaluate(p, context).RetentionValue);
    }
}
