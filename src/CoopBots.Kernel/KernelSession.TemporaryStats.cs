using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    // Native stat wrappers expire at the owner's participating side end.
    // Long-term pricing must use that residual stat, not the visible current
    // total. Resolve the ordinary mirror on a stable private branch so Artifact
    // and opposite temporary debuffs retain their actual native outcomes.
    // This projection does not advance a turn or grant delayed/recurring powers.
    internal KernelSession AfterTemporaryStatExpiry(IEnumerable<Creature>? participants = null)
    {
        // Most branches have no temporary wrapper. Do not allocate participant
        // sets or LINQ pipelines until a relevant positive wrapper exists.
        HashSet<Creature>? owners = null;
        var readOwners = participants is null;
        List<Creature>? affected = null;
        var powers = Combat.EffectivePowers();
        for (var index = 0; index < powers.Count; index++)
        {
            var power = powers[index];
            if (power.Amount <= 0 || power is not (TemporaryStrengthPower
                or TemporaryDexterityPower or TemporaryFocusPower)) continue;
            if (!readOwners)
            {
                owners = participants!.ToHashSet();
                readOwners = true;
            }
            if (owners is not null && !owners.Contains(power.Owner)) continue;
            affected ??= new List<Creature>();
            // Keep native first-owner acquisition order, as the former Distinct did.
            if (!affected.Contains(power.Owner)) affected.Add(power.Owner);
        }
        if (affected is null) return this;
        using var isolation = SimulationNotificationIsolation.Enter();
        var copy = Fork();
        copy.Combat.RestoreTemporaryStats(affected);
        PowerLifecycleSupport.ResolvePowerAmountChanges(copy.simulator, copy.Combat);
        return copy;
    }
}
