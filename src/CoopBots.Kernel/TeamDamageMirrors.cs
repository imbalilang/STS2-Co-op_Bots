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
    internal const decimal FlankingApplicationFactor = 2m;

    internal static void Register(MethodMirrorRegistry<AbstractModel, ModifyDamageMirrorContext, decimal> registry)
    {
        registry.Register<FlankingPower>(FlankingMultiplier);
        registry.Register<KnockdownPower>(KnockdownMultiplier);
        // Native Tank/Guarded modify powered incoming attacks only. Instances
        // from different living Tanks multiply independently, as in the game.
        registry.Register<TankPower>((p,c) => c.Target == p.Owner && c.Props.IsPoweredAttack()
            ? p.DynamicVars["DamageIncrease"].BaseValue : 1m);
        registry.Register<GuardedPower>((p,c) => c.Target == p.Owner && c.Props.IsPoweredAttack()
            ? p.DynamicVars["DamageDecrease"].BaseValue : 1m);
    }

    // FLANKING has no declared variable: each application is one stack, and the
    // card text "double damage from your allies" means 2^stacks.
    private static decimal FlankingMultiplier(FlankingPower power, ModifyDamageMirrorContext context)
        => Amplifies(power, context) ? Compound(2m, FlankingStacks(power)) : 1m;

    /// <summary>
    /// The stack count is written in two different units, and they must not be mixed.
    /// The kernel applies one stack per application; the live card applies
    /// <c>PowerCmd.Apply(..., 2m, ...)</c>, so a board captured mid-round carries 2 per
    /// application. Compounding those raw would square the multiplier (live ×2 read as
    /// 2^2 = ×4). Convert a live instance down to stacks and leave the compounding rule
    /// alone: 1 card is 2x and 3 cards are 2^3 = 8x in BOTH encodings, which is what
    /// <c>KernelEngineScenarios.AuditFlankingKnockdown</c> pins.
    /// </summary>
    private static int FlankingStacks(FlankingPower power)
        => IsLiveEncoded(power) ? Math.Max(1, power.Amount / 2) : power.Amount;

    /// <summary>
    /// Which units an instance is written in. A live application renders the applier as the
    /// platform display name (FlankingPower.AfterApplied) while every kernel application
    /// writes the applier's NetId into the same variable.
    /// </summary>
    private static bool IsLiveEncoded(PowerModel power)
        => power.Applier?.Player is { } applier
            && power.DynamicVars["Applier"] is StringVar text
            && !string.Equals(text.StringValue, applier.NetId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

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
        // The live power is applied WITH its applier creature and answers `dealer == Applier`
        // from it; the kernel only ever wrote a NetId into the display variable. Prefer the
        // creature, otherwise a captured live board (platform name in that variable) reads as
        // "somebody else" and amplifies the applier's own attack.
        if (power.Applier is { } applierCreature)
            return !ReferenceEquals(applierCreature, context.Dealer);
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
