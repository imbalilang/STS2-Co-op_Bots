using MegaCrit.Sts2.Core.Entities.Players;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal static class OrbLifecycleSupport
{
    public static bool TriggerBeforeTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        int historyEntryStart = simulator.History.Entries.Count;
        bool completed = simulator.State.GetPlayerCombatState(player).OrbQueue.BeforeTurnEnd(simulator);
        if (!completed)
            return false;
        TriggeredPowerSupport.CompensateHistorySince(simulator, combat, historyEntryStart);
        return !combat.HasPendingChoice;
    }
}
