using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Potions.OnUse;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

// CoopBots multiplayer port: the upstream snapshot mirrors only a handful of
// combat potions. These are the plain "apply a power / grant energy" ones the
// team planner needs so a proactive potion can be simulated and confirmed
// before it is spent, instead of being guessed at from live state.
internal static class TeamPotionMirrors
{
    internal static void Register(MethodMirrorRegistry<PotionModel, PotionOnUseMirrorContext> registry)
    {
        registry.Register<StrengthPotion>(StrengthOnUse);
        registry.Register<DexterityPotion>(DexterityOnUse);
        registry.Register<FocusPotion>(FocusOnUse);
        registry.Register<VulnerablePotion>(VulnerableOnUse);
        registry.Register<EnergyPotion>(EnergyOnUse);
    }

    private static void StrengthOnUse(StrengthPotion potion, PotionOnUseMirrorContext context)
        => ApplyPower(context, typeof(StrengthPower), "StrengthPower", context.TargetPlayer.Creature);

    private static void DexterityOnUse(DexterityPotion potion, PotionOnUseMirrorContext context)
        => ApplyPower(context, typeof(DexterityPower), "DexterityPower", context.TargetPlayer.Creature);

    private static void FocusOnUse(FocusPotion potion, PotionOnUseMirrorContext context)
        => ApplyPower(context, typeof(FocusPower), "FocusPower", context.TargetPlayer.Creature);

    private static void VulnerableOnUse(VulnerablePotion potion, PotionOnUseMirrorContext context)
        => ApplyPower(context, typeof(VulnerablePower), "VulnerablePower",
            context.Target ?? throw new InvalidOperationException("VulnerablePotion requires a target."));

    private static void EnergyOnUse(EnergyPotion potion, PotionOnUseMirrorContext context)
        => context.Simulator.GainEnergy(context.TargetPlayer, Amount(potion, "Energy"));

    private static void ApplyPower(PotionOnUseMirrorContext context, Type powerType, string key, Creature target)
    {
        var combat = context.Simulator.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException($"Potion {context.Potion.Id} requires simulated combat state.");
        combat.ApplyPower(powerType, target, Amount(context.Potion, key), context.Potion.Owner.Creature);
        // Applying a power leaves a pending amount change; resolve it here or the
        // potioned branch cannot be forked/searched afterwards.
        PowerLifecycleSupport.ResolvePowerAmountChanges(context.Simulator, combat);
    }

    private static int Amount(PotionModel potion, string key)
        => (int)potion.DynamicVars.Values.Where(variable => variable.Name == key)
            .Sum(variable => (double)variable.BaseValue);
}
