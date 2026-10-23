using CoopBots.Kernel;

namespace CoopBots.Building;

internal static class EncounterValueMath
{
    internal readonly record struct Defense(double MissChance, double SelfLossPerTurn, double ExhaustSelfLoss,
        double DrawFraction, double ExhaustBlockPerDraw = 0);
    internal readonly record struct Estimate(double Total, double EffectiveDamage, double HealthCost,
        int PlayerRounds, int EnemyWindows);

    internal static Estimate Evaluate(IReadOnlyList<TeamSynergyMath.Seat> party, TeamSynergyMath.Pressure pressure,
        TeamSynergyMath.Estimate output, IReadOnlyList<Defense> defense, int maxRounds = 6)
    {
        if (pressure.Hp <= 0) return new(0, 0, 0, 0, 0);
        var exposures = output.Exposures;
        if (exposures is null || exposures.Length != party.Count || defense.Count != party.Count)
            throw new ArgumentException("Encounter pricing requires one exposure and defense forecast per seat");
        maxRounds = Math.Clamp(maxRounds, 1, 6);
        var damage = Math.Max(0, output.Damage);
        var lethal = damage > 0 && damage * maxRounds >= pressure.Hp;
        var playerRounds = lethal ? (int)Math.Clamp(Math.Ceiling(pressure.Hp / damage), 1, maxRounds) : maxRounds;
        var enemyWindows = lethal ? playerRounds - 1 : playerRounds;
        double healthCost = 0;
        for (var i = 0; i < party.Count; i++)
        {
            var seat = party[i];
            if (seat.Hp <= 0) continue;
            var forecast = defense[i];
            var drawFraction = Math.Clamp(forecast.DrawFraction, 0, 1);
            var selfLoss = playerRounds * Math.Max(0, forecast.SelfLossPerTurn)
                + Math.Max(0, forecast.ExhaustSelfLoss) * Math.Min(1, playerRounds * drawFraction);
            if (enemyWindows == 0)
            {
                healthCost += SurvivalValue.LossCost(seat.Hp, seat.MaxHp, selfLoss);
                continue;
            }
            var exhaustAvailability = Math.Min(1, enemyWindows * drawFraction) / Math.Max(.001, enemyWindows * drawFraction);
            var availableBlock = double.IsNaN(exposures[i].AvailableBlock) ? seat.Block : exposures[i].AvailableBlock;
            var meanBlock = Math.Max(0, availableBlock - Math.Max(0, forecast.ExhaustBlockPerDraw) * (1 - exhaustAvailability));
            healthCost += SurvivalValue.ForecastCost(seat.Hp, seat.MaxHp, exposures[i].AfterDebuffs, meanBlock,
                forecast.MissChance, selfLoss / enemyWindows, enemyWindows);
        }
        var effectiveDamage = Math.Min(pressure.Hp, damage * maxRounds);
        return new(effectiveDamage - healthCost, effectiveDamage, healthCost, playerRounds, enemyWindows);
    }
}
