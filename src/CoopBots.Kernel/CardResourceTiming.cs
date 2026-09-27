using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

/// <summary>
/// Resource timing read from the game's OnPlay implementations. An EnergyVar
/// describes an amount, not when it is received. Shared by rollout holding and
/// permanent-deck evaluation so they cannot give Sidestep two different meanings.
/// </summary>
public static class CardResourceTiming
{
    public readonly record struct Resources(double EnergyNow, double EnergyNextTurn,
        double DrawNextTurn, bool DrawForTeam = false);

    public static Resources Of(CardModel card, int energy = 3)
    {
        double Amount(string name) => card.DynamicVars.TryGetValue(name, out var value)
            ? (double)value.BaseValue : 0;
        var type = card.GetType().Name;
        var futureEnergy = type is "ChargeBattery" or "Convergence" or "Delay" or "Hegemony"
            or "Invoke" or "Outmaneuver" or "RefineBlade" or "Relax" or "Scavenge" or "Sidestep"
            ? Amount("Energy") : 0;
        var futureDraw = type == "Predator" ? 2
            : type is "Glow" or "GuidingStar" or "Plot" or "Relax" ? Amount("Cards") : 0;
        var immediateEnergy = type == "DoubleEnergy" ? Math.Max(0, energy)
            : type is "Adrenaline" or "Bloodletting" or "Offering" or "Turbo" or "Wisp"
                or "BelieveInYou" or "EnergySurge" or "Constellation" ? Amount("Energy") : 0;
        return new(immediateEnergy, futureEnergy, futureDraw, type == "Plot");
    }
}
