namespace CoopBots.Building;

internal static class ConstructionCombatMath
{
    internal static double BlockTriggerDamage(double amount, double events, double health)
        => Math.Min(Math.Max(0, health), Math.Max(0, amount) * Math.Max(0, events));
}
