using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel;

public static class TeamSynergyNative
{
    public static double VulnerableFactor
        => (double)ModelDb.Power<VulnerablePower>().DynamicVars["DamageIncrease"].BaseValue;
    public static double WeakFactor
        => (double)ModelDb.Power<WeakPower>().DynamicVars["DamageDecrease"].BaseValue;

    public static TeamSynergyMath.Component Component(CardModel card, int cost)
    {
        if (card.TargetType is not (TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy)
            || card.Keywords.Contains(CardKeyword.Unplayable)) return new();
        var repeats = 1 + Math.Max(0, card.GetEnchantedReplayCount());
        var shared = card.GetType().Name is "Uppercut" or "Shockwave" ? Amount("Power") : 0;
        var malaise = card is Malaise ? Math.Max(0, cost + (card.IsUpgraded ? 1 : 0)) : 0;
        return new(card is Flanking ? Math.Pow((double)TeamDamageMirrors.FlankingApplicationFactor, repeats) : 1,
            card is Knockdown ? Amount("KnockdownPower") * repeats : 0,
            Math.Max(shared, Amount("VulnerablePower")) * repeats,
            Math.Max(malaise, Math.Max(shared, Amount("WeakPower"))) * repeats,
            Math.Max(malaise, Amount("StrengthLoss")) * repeats,
            card is Debilitate ? Amount("DebilitatePower") * repeats : 0, Math.Max(0, cost));

        double Amount(string key) => card.DynamicVars.TryGetValue(key, out var value)
            ? Math.Max(0, (double)value.BaseValue) : 0;
    }

    public static bool HasComponent(TeamSynergyMath.Component c)
        => c.Flanking > 1 || c.Knockdown > 0 || c.Vulnerable > 0
            || c.Weak > 0 || c.StrengthDown > 0 || c.Debilitate > 0;

    public static double VulnerableFactorFor(Player attacker, Creature? target = null)
    {
        var factor = (decimal)VulnerableFactor;
        if (attacker.GetRelic<PaperPhrog>() is { } relic)
            // The contribution permits a hypothetical (unspecified) target;
            // retain that argument while acknowledging the native annotation.
            factor = relic.ModifyVulnerableMultiplier(target!, factor, ValueProp.Move, attacker.Creature, null);
        return (double)factor;
    }

    public static double WeakFactorFor(Player defender, Creature? dealer = null)
    {
        var factor = (decimal)WeakFactor;
        if (defender.GetRelic<PaperKrane>() is { } relic)
            factor = relic.ModifyWeakMultiplier(defender.Creature, factor, ValueProp.Move, dealer, null);
        return (double)factor;
    }
}
