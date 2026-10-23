namespace CoopBots.Building;

internal static class SurvivalValue
{
    internal const double HpWeight = 1.3;
    internal const double ReserveFraction = .3;
    internal const double ReserveBudget = 16;
    internal const double LethalBudget = 32;

    private static double Risk(double hp, double maxHp)
    {
        var reserve = Math.Clamp((maxHp * ReserveFraction - hp) / Math.Max(1, maxHp * ReserveFraction), 0, 1);
        return ReserveBudget * reserve * reserve + (hp <= 0 ? LethalBudget : 0);
    }

    internal static double ExtraLossCost(double hp, double maxHp, double loss)
        => maxHp <= 0 || loss <= 0 ? 0 : Math.Max(0, Risk(hp - loss, maxHp) - Risk(hp, maxHp));

    internal static double LossCost(double hp, double maxHp, double loss)
        => HpWeight * Math.Max(0, loss) + ExtraLossCost(hp, maxHp, loss);

    internal static double ForecastCost(double hp, double maxHp, double incoming, double meanBlock,
        double missChance, double selfLossPerTurn, double horizon, bool includeIncomingCost = true)
    {
        var turns = Math.Clamp(horizon, 1, 6);
        var miss = Math.Clamp(missChance, 0, 1);
        var pressure = Math.Max(0, incoming);
        var blockedLoss = Math.Max(0, pressure - Math.Max(0, meanBlock) / Math.Max(.001, 1 - miss));
        var meanLoss = miss * pressure + (1 - miss) * blockedLoss;
        var otherLoss = (turns - 1) * meanLoss + turns * Math.Max(0, selfLossPerTurn);
        return miss * LossCost(hp, maxHp, otherLoss + pressure)
            + (1 - miss) * LossCost(hp, maxHp, otherLoss + blockedLoss)
            - (includeIncomingCost ? 0 : HpWeight * turns * meanLoss);
    }
}
