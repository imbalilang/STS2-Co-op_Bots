using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Potions.OnUse;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
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
        // Three more that were invisible to the kernel: a potion with no mirror is
        // never simulated, so the team could not even consider it and the slot
        // stayed full all run (the last defeat ended holding five of these).
        registry.Register<SpeedPotion>(SpeedOnUse);
        registry.Register<GigantificationPotion>(GigantificationOnUse);
        registry.Register<ShipInABottle>(ShipInABottleOnUse);

        // The rest of the combat potions already have exact implementations in the
        // upstream deterministic-search table, but that table is only reached from
        // the vendored beam solver, which this kernel does not drive. Routing them
        // through the mirror registry keeps one implementation instead of a second
        // copy that silently drifts; two dozen bottles were otherwise invisible.
        RegisterTable(registry);
    }

    // Every potion the upstream table models with a self-contained effect. The
    // five choice-driven potions (Ashwater, DropletOfPrecognition, GamblersBrew,
    // LiquidMemories, TouchOfInsanity) are deliberately absent: their table entry
    // is a `break`, so routing them here would simulate them as a free no-op —
    // exactly the silent-waste failure this file exists to remove.
    private static void RegisterTable(MethodMirrorRegistry<PotionModel, PotionOnUseMirrorContext> registry)
    {
        registry.Register<Ambergris>(FromTable);
        registry.Register<BeetleJuice>(FromTable);
        registry.Register<BlessingOfTheForge>(FromTable);
        registry.Register<BlockPotion>(FromTable);
        registry.Register<BloodPotion>(FromTable);
        registry.Register<BoneBrew>(FromTable);
        registry.Register<CunningPotion>(FromTable);
        registry.Register<Duplicator>(FromTable);
        registry.Register<ExplosiveAmpoule>(FromTable);
        registry.Register<FirePotion>(FromTable);
        registry.Register<FlexPotion>(FromTable);
        registry.Register<Fortifier>(FromTable);
        registry.Register<FoulPotion>(FromTable);
        registry.Register<FruitJuice>(FromTable);
        registry.Register<FyshOil>(FromTable);
        registry.Register<GhostInAJar>(FromTable);
        registry.Register<HeartOfIron>(FromTable);
        registry.Register<KingsCourage>(FromTable);
        registry.Register<LiquidBronze>(FromTable);
        registry.Register<LuckyTonic>(FromTable);
        registry.Register<MazalethsGift>(FromTable);
        registry.Register<PoisonPotion>(FromTable);
        registry.Register<PotOfGhouls>(FromTable);
        registry.Register<PotionOfBinding>(FromTable);
        registry.Register<PotionOfCapacity>(FromTable);
        registry.Register<PotionOfDoom>(FromTable);
        registry.Register<PotionShapedRock>(FromTable);
        registry.Register<PowderedDemise>(FromTable);
        registry.Register<RadiantTincture>(FromTable);
        registry.Register<RegenPotion>(FromTable);
        registry.Register<ShacklingPotion>(FromTable);
        registry.Register<SoldiersStew>(FromTable);
        registry.Register<StableSerum>(FromTable);
        registry.Register<StarPotion>(FromTable);
        registry.Register<WeakPotion>(FromTable);
    }

    // A potion whose switch case is guarded on a target must have one: the manual
    // use path already refused an invalid target. Without this check a target-less
    // call would fall through to the table's `default`, which dispatches straight
    // back into this registry and recurses until the stack dies.
    private static void FromTable(PotionModel potion, PotionOnUseMirrorContext context)
    {
        if (delegating)
            throw new InvalidOperationException(
                $"Potion {potion.Id.Entry} reached the upstream table without a matching case.");
        if (RequiresTarget(potion) && context.Target is null)
            throw new InvalidOperationException($"Potion {potion.Id.Entry} requires a target.");
        var combat = context.Simulator.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException($"Potion {potion.Id.Entry} requires simulated combat state.");
        delegating = true;
        try
        {
            // A false result means the use opened a choice; the caller's pending-choice
            // check turns that into an unusable branch, which is what it means here too.
            _ = PotionOnUseSupport.Use(context.Simulator, combat, potion, context.Target);
        }
        finally { delegating = false; }
        PowerLifecycleSupport.ResolvePowerAmountChanges(context.Simulator, combat);
    }

    [ThreadStatic] private static bool delegating;

    private static bool RequiresTarget(PotionModel potion)
        => potion.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly;

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

    // "Gain X Dexterity. At the end of your turn, lose X." The temporary power
    // is SpeedPotionPower, not the generic TemporaryDexterityPower: it derives
    // from it, so the end-of-turn restore still finds it, and the branch then
    // carries the same power the live game does.
    private static void SpeedOnUse(SpeedPotion potion, PotionOnUseMirrorContext context)
    {
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("Speed Potion needs writable prediction state.");
        var creature = context.TargetPlayer.Creature;
        effects.ApplyTemporaryDexterity(typeof(SpeedPotionPower), creature, Amount(potion, "DexterityPower"), creature);
        var combat = context.Simulator.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Speed Potion requires simulated combat state.");
        PowerLifecycleSupport.ResolvePowerAmountChanges(context.Simulator, combat);
    }

    // "Your next attack deals triple damage." The damage hook for this power is
    // already mirrored; only the potion that grants it was missing.
    private static void GigantificationOnUse(GigantificationPotion potion, PotionOnUseMirrorContext context)
        => ApplyPower(context, typeof(GigantificationPower), "GigantificationPower", context.TargetPlayer.Creature);

    // "Gain X Block. Next turn, gain X Block."
    private static void ShipInABottleOnUse(ShipInABottle potion, PotionOnUseMirrorContext context)
    {
        var creature = context.TargetPlayer.Creature;
        context.Simulator.GainBlock(creature, potion.DynamicVars.Block);
        ApplyPower(context, typeof(BlockNextTurnPower), Amount(potion, "Block"), creature);
    }

    private static void ApplyPower(PotionOnUseMirrorContext context, Type powerType, string key, Creature target)
        => ApplyPower(context, powerType, Amount(context.Potion, key), target);

    private static void ApplyPower(PotionOnUseMirrorContext context, Type powerType, int amount, Creature target)
    {
        var combat = context.Simulator.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException($"Potion {context.Potion.Id} requires simulated combat state.");
        combat.ApplyPower(powerType, target, amount, context.Potion.Owner.Creature);
        // Applying a power leaves a pending amount change; resolve it here or the
        // potioned branch cannot be forked/searched afterwards.
        PowerLifecycleSupport.ResolvePowerAmountChanges(context.Simulator, combat);
    }

    // A potion whose dynamic variable was renamed upstream must fail loudly here.
    // Summing an empty match set returned 0, which simulated the potion as a free
    // no-op: the search then never chose it and the slot stayed full all run.
    private static int Amount(PotionModel potion, string key)
        => potion.DynamicVars.Values.Where(variable => variable.Name == key).ToArray() is { Length: > 0 } matches
            ? (int)matches.Sum(variable => (double)variable.BaseValue)
            : throw new InvalidOperationException(
                $"Potion {potion.Id.Entry} has no '{key}' variable (has: "
                + string.Join(", ", potion.DynamicVars.Values.Select(variable => variable.Name)) + ").");
}
