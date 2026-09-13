using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor.Engine.Common;

internal sealed class CombatPredictionRisk(IReadOnlyList<CombatPredictionRiskEntry> entries)
    : PredictionRisk(entries.Count > 0)
{
    public IReadOnlyList<CombatPredictionRiskEntry> Entries { get; } = entries;
}
