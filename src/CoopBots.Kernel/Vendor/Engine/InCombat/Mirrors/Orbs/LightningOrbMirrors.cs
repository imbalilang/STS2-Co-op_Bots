using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Orbs;

internal static class LightningOrbMirrors
{
    // Mirrors LightningOrb.BeforeTurnEndOrbTrigger by forwarding through OrbModel.TriggerPassive.
    public static void BeforeTurnEndOrbTrigger(LightningOrb orb, OrbMirrorContext context)
    {
        context.Simulator.TriggerOrbPassive(orb, target: null, context.ProcessedEnemyDeaths);
    }

    // Mirrors LightningOrb.Passive without VFX/SFX or waits.
    public static void Passive(LightningOrb orb, OrbPassiveMirrorContext context)
    {
        Damage(orb, context, OrbMirrors.ModifyValue(context.Simulator, orb, 3m), context.Target);
    }

    // Mirrors LightningOrb.Evoke without VFX/SFX or waits.
    public static IReadOnlyList<Creature> Evoke(LightningOrb orb, OrbMirrorContext context)
    {
        return Damage(orb, context, OrbMirrors.ModifyValue(context.Simulator, orb, 8m), target: null);
    }

    private static IReadOnlyList<Creature> Damage(
        LightningOrb orb,
        OrbMirrorContext context,
        decimal value,
        Creature? target)
    {
        var candidates = context.State.GetOpponentsOf(orb.Owner.Creature)
            .Where(context.State.IsHittable)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        target ??= context.Rng.CombatTargets.NextItem(candidates);
        if (target is null)
        {
            return [];
        }

        IReadOnlyList<Creature> targets = [target];
        context.Simulator.Damage(targets, value, ValueProp.Unpowered, orb.Owner.Creature);
        return targets;
    }
}
