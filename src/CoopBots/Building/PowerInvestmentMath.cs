namespace CoopBots.Building;

internal static class PowerInvestmentMath
{
    internal readonly record struct Power(double Cost, double Strength = 0, double StrengthPerTurn = 0,
        double BlockDamage = 0, double BlockPerCard = 0, bool KeepAvailable = false);
    internal readonly record struct Estimate(double PaidShare, double Strength, double PaidStrength,
        double BlockDamage, double BlockPerCard, double SetupEnergy, double SetupCards);

    internal static double OrdinaryDemand(double total, double component)
        => Math.Max(0, total - Math.Max(0, component));

    internal static Estimate Evaluate(int size, double draw, double energy, double ordinaryDemand,
        IReadOnlyList<Power> powers, int turns = 3)
    {
        size = Math.Max(1, size);
        draw = Math.Clamp(draw, 0, size);
        energy = Math.Max(0, energy);
        ordinaryDemand = Math.Max(0, ordinaryDemand);
        turns = Math.Clamp(turns, 1, 6);
        Span<double> active = powers.Count > 64 ? new double[powers.Count] : stackalloc double[powers.Count];
        active.Clear();
        Span<double> cycleAvailable = powers.Count > 64 ? new double[powers.Count] : stackalloc double[powers.Count];
        var cycleLength = draw > 0 ? (int)Math.Min(int.MaxValue, Math.Ceiling(size / draw)) : int.MaxValue;
        double paidShare=0, strength=0, paidStrength=0, blockDamage=0, blockPerCard=0, setupEnergy=0, setupCards=0,
            growingStrength=0;
        for (var turn = 1; turn <= turns; turn++)
        {
            var cycleTurn = (turn - 1) % cycleLength + 1;
            var exposure = Math.Min(1, cycleTurn * draw / size) - Math.Min(1, (cycleTurn - 1) * draw / size);
            if (cycleTurn == 1)
                for (var i = 0; i < powers.Count; i++) cycleAvailable[i] = Math.Max(0, 1 - active[i]);
            double demand = 0;
            for (var i = 0; i < powers.Count; i++)
            {
                var power = powers[i];
                growingStrength += active[i] * Math.Max(0, power.StrengthPerTurn);
                var available = power.KeepAvailable ? Math.Max(0, Math.Min(1, turn * draw / size) - active[i])
                    : cycleAvailable[i] * exposure;
                if (power.Cost <= energy) demand += available * Math.Max(0, power.Cost);
            }
            var setupShare = demand > 0 ? Math.Min(1, energy / demand) : 1;
            double turnSetupEnergy=0, turnSetupCards=0, turnStrength=growingStrength, turnBlockDamage=0, turnBlockPerCard=0;
            for (var i = 0; i < powers.Count; i++)
            {
                var power = powers[i];
                var available = power.KeepAvailable ? Math.Max(0, Math.Min(1, turn * draw / size) - active[i])
                    : cycleAvailable[i] * exposure;
                var played = power.Cost <= energy ? available * setupShare : 0;
                active[i] += played;
                turnSetupEnergy += played * Math.Max(0, power.Cost);
                turnSetupCards += played;
                turnStrength += active[i] * Math.Max(0, power.Strength);
                turnBlockDamage += active[i] * Math.Max(0, power.BlockDamage);
                turnBlockPerCard += active[i] * Math.Max(0, power.BlockPerCard);
            }
            var ordinaryShare = ordinaryDemand > 0 ? Math.Min(1, Math.Max(0, energy - turnSetupEnergy) / ordinaryDemand) : 1;
            paidShare += ordinaryShare;
            strength += turnStrength;
            paidStrength += turnStrength * ordinaryShare;
            blockDamage += turnBlockDamage;
            blockPerCard += turnBlockPerCard;
            setupEnergy += turnSetupEnergy;
            setupCards += turnSetupCards;
        }
        return new(paidShare/turns, strength/turns, paidStrength/turns, blockDamage/turns, blockPerCard/turns,
            setupEnergy/turns, setupCards/turns);
    }
}
