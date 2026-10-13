using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    // Native SpendResources records what was actually paid, including the
    // excess-cost-with-stars clamp. Forecast costs are only ordering hints.
    internal int EnergySpentThisTurn(Player player) => Combat.GetEnergySpentThisTurn(player);
}
