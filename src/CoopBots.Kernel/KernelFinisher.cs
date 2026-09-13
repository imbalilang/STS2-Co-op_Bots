using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    // Gross cost before an action, not net energy lost after energy/refund hooks.
    public int FinisherEnergyCost(CardModel source)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var card = simulator.State.FindCard(source);
        return card is null || card.Preview.EnergyCost.CostsX || card.Preview.HasStarCostX
            ? int.MaxValue : card.GetEnergyCostWithModifiers(simulator, simulator.State.GetPlayerCombatState(source.Owner));
    }

    // A seeded lucky outcome is not a reliable recommendation to a human.
    public string FinisherRandomStamp()
    {
        var r = simulator.Rng;
        return string.Join(',', new[] { r.Shuffle, r.CombatCardGeneration, r.CombatPotionGeneration,
            r.CombatCardSelection, r.CombatEnergyCosts, r.CombatTargets, r.CombatOrbGeneration,
            r.MonsterAi, r.Niche }.Select(rng => rng.ToSerializable().counter));
    }
}
