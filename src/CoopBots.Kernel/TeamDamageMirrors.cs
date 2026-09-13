using System.Globalization;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Damage;

namespace CoopBots.Kernel;

/// <summary>
/// Multiplayer damage amplification. FLANKING and KNOCKDOWN make an enemy take
/// extra attack damage from the applier's ALLIES for the rest of the turn, so
/// they ride the same ModifyDamageMultiplicative pass as Vulnerable — a special
/// Vulnerable gated on "a different player is attacking" instead of "any attack".
///
/// They differ from Vulnerable in how they stack. Vulnerable is a duration:
/// three stacks mean three turns at +50%. These are per-turn multipliers that
/// compound, so three Flanking stacks are 2x2x2 = 8x damage in the same turn.
/// That is why the handler exponentiates instead of reading one flat factor.
/// </summary>
internal static class TeamDamageMirrors
{
    internal static void Register(MethodMirrorRegistry<AbstractModel, ModifyDamageMirrorContext, decimal> registry)
    {
        registry.Register<FlankingPower>(FlankingMultiplier);
        registry.Register<KnockdownPower>(KnockdownMultiplier);
    }

    // FLANKING has no declared variable: each application is one stack, and the
    // card text "double damage from your allies" means 2^stacks.
    private static decimal FlankingMultiplier(FlankingPower power, ModifyDamageMirrorContext context)
        => Amplifies(power, context) ? Compound(2m, power.Amount) : 1m;

    // KNOCKDOWN declares the multiplier it applies (2, or 3 upgraded), so a
    // single application is exactly that and further applications add to it.
    private static decimal KnockdownMultiplier(KnockdownPower power, ModifyDamageMirrorContext context)
        => Amplifies(power, context) ? Math.Max(1, power.Amount) : 1m;

    // Vulnerable's gate (the target is the debuffed creature and the hit is a
    // powered attack), plus the multiplayer rule: only another player's attack
    // is amplified. The applier keeps full damage, and monsters never benefit.
    private static bool Amplifies(PowerModel power, ModifyDamageMirrorContext context)
    {
        if (context.Target != power.Owner || context.Dealer?.Player is not { } dealer)
            return false;
        if (!context.Props.IsPoweredAttack())
            return false;
        var applier = power.DynamicVars["Applier"] is StringVar text ? text.StringValue : string.Empty;
        return applier.Length == 0
            || !string.Equals(applier, dealer.NetId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static decimal Compound(decimal factor, int stacks)
    {
        var result = 1m;
        for (var i = 0; i < Math.Max(0, stacks); i++) result *= factor;
        return result;
    }
}
