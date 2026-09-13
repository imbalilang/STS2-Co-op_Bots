using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CoopBots.Kernel.Vendor;

internal static class PowerDynamicVarWarmup
{
    private static bool _canonicalPowersMaterialized;

    public static void EnsureMaterialized(CombatState state)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Power dynamic variables must be materialized on the main thread.");

        if (!_canonicalPowersMaterialized)
        {
            foreach (PowerModel power in ModelDb.AllPowers)
                _ = power.DynamicVars;
            _canonicalPowersMaterialized = true;
        }

        foreach (PowerModel power in state.Creatures.SelectMany(creature => creature.Powers))
            _ = power.DynamicVars;
    }
}
