using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Orbs;

internal static class GlassOrbMirrors
{
    // Mirrors GlassOrb.BeforeTurnEndOrbTrigger by forwarding through OrbModel.TriggerPassive.
    public static void BeforeTurnEndOrbTrigger(GlassOrb orb, OrbMirrorContext context)
    {
        context.Simulator.TriggerOrbPassive(orb, target: null, context.ProcessedEnemyDeaths);
    }

    // Mirrors GlassOrb.Passive by mutating only the simulator's cloned orb.
    public static void Passive(GlassOrb orb, OrbPassiveMirrorContext context)
    {
        decimal passiveVal = OrbMirrors.ModifyValue(context.Simulator, orb, orb._passiveVal);
        if (passiveVal <= 0m)
        {
            return;
        }

        orb._passiveVal = Math.Max(0m, orb._passiveVal - 1m);
        Damage(orb, context, passiveVal);
    }

    // Mirrors GlassOrb.Evoke without VFX/SFX or waits.
    public static IReadOnlyList<Creature> Evoke(GlassOrb orb, OrbMirrorContext context)
    {
        decimal evokeVal = OrbMirrors.ModifyValue(context.Simulator, orb, orb._passiveVal) * 2m;
        return evokeVal <= 0m ? [] : Damage(orb, context, evokeVal);
    }

    private static IReadOnlyList<Creature> Damage(GlassOrb orb, OrbMirrorContext context, decimal value)
    {
        var targets = context.State.HittableEnemies;
        context.Simulator.Damage(targets, value, ValueProp.Unpowered, orb.Owner.Creature);
        return targets;
    }
}
