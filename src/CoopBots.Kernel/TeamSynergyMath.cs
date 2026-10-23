using System.Numerics;

namespace CoopBots.Kernel;

// Bounded joint output/prevention estimate. Components consume their owner's
// energy; native combat simulation remains responsible for actual outcomes.
public static class TeamSynergyMath
{
    public sealed record Component(double Flanking = 1, double Knockdown = 0,
        double Vulnerable = 0, double Weak = 0, double StrengthDown = 0,
        double Debilitate = 0, int Cost = 0, double Block = 0);

    public sealed record Seat(int Size, double Draw, double Energy, double Attack,
        double OtherDamage, double Block, double Hp, double MaxHp,
        IReadOnlyList<Component> Components, double VulnerableFactor = 1.5,
        double WeakFactor = .75, double FreeAttack = 0, double PaidBlock = 0);

    public sealed record Pressure(double Hp, double Hit, int Repeats,
        double Artifact = 0, int CoverageTurns = 1);

    public readonly record struct Exposure(double AfterDebuffs, double Unblocked)
    {
        public double AvailableBlock { get; init; } = double.NaN;
    }

    public sealed record Estimate(double Damage, double Prevention, double UnamplifiedDamage)
    {
        public Exposure[]? Exposures { get; init; }
    }

    private sealed record Window(double Flanking, double Knockdown, double Vulnerable,
        double Weak, double StrengthDown, double Debilitate, double EnergyLeft,
        double Probability, double Block = 0);

    public const int MaximumComponents = 8;

    public static double ConstructionDamageResidual(IReadOnlyList<Seat> party,
        Pressure pressure, double fullDamage, double soloDamage)
    {
        if (party.Count == 0 || !party.Any(s => s.Components.Any(c =>
            c.Flanking != 1 || c.Knockdown > 0 || c.Debilitate > 0))) return 0;
        var plain = party.Select(s => s with
        {
            Components = s.Components.Select(c => c with
                { Flanking = 1, Knockdown = 0, Debilitate = 0 }).ToArray()
        }).ToArray();
        var ordinaryJointGain = Evaluate(plain, pressure).Damage
            - Evaluate([plain[0]], pressure).Damage;
        return fullDamage - soloDamage - ordinaryJointGain;
    }

    public static Estimate Evaluate(IReadOnlyList<Seat> party, Pressure pressure,
        int excludedDefender = -1, bool includeExposure = false)
    {
        if (pressure.Hp <= 0 || party.Count == 0) return new(0, 0, 0);
        var windows = party.Select(Windows).ToArray();
        double damage = 0, unamplified = 0, prevention = 0;
        var exposures = includeExposure ? new Exposure[party.Count] : null;
        for (var i = 0; i < party.Count; i++)
        {
            var seat = party[i];
            if (seat.Hp <= 0) continue;
            var freeShare = seat.Attack > 0 ? Math.Clamp(seat.FreeAttack / seat.Attack, 0, 1) : 0;
            var baseline = seat.Attack * windows[i].Sum(w => w.Probability
                * (freeShare + (1 - freeShare) * EnergyShare(w, seat)));
            double amplified = 1, withoutVulnerable = 1, withoutDebilitate = 1, withoutBoth = 1;
            for (var source = 0; source < party.Count; source++)
            {
                double all = 0, noVulnerable = 0, noDebilitate = 0, neither = 0;
                foreach (var window in windows[source])
                {
                    var multiplier = source == i
                        ? freeShare + (1 - freeShare) * EnergyShare(window, seat)
                        : window.Flanking * Math.Max(1, window.Knockdown);
                    var probability = window.Probability * multiplier;
                    all += probability;
                    noVulnerable += probability * (1 - Coverage(window.Vulnerable));
                    noDebilitate += probability * (1 - Coverage(window.Debilitate));
                    neither += probability * (1 - Coverage(window.Vulnerable))
                        * (1 - Coverage(window.Debilitate));
                }
                amplified *= all;
                withoutVulnerable *= noVulnerable;
                withoutDebilitate *= noDebilitate;
                withoutBoth *= neither;
                for (var age = 1; age < Math.Clamp(pressure.CoverageTurns, 1, 3); age++)
                {
                    withoutVulnerable *= windows[source].Sum(w => w.Probability
                        * (1 - Coverage(w.Vulnerable - age)));
                    withoutDebilitate *= windows[source].Sum(w => w.Probability
                        * (1 - Coverage(w.Debilitate - age)));
                    withoutBoth *= windows[source].Sum(w => w.Probability
                        * (1 - Coverage(w.Vulnerable - age)) * (1 - Coverage(w.Debilitate - age)));
                }
            }
            var jointCoverage = Math.Max(0, amplified - withoutVulnerable - withoutDebilitate + withoutBoth);
            var vulnerableGain = (amplified - withoutVulnerable + jointCoverage) * (seat.VulnerableFactor - 1);
            var artifactShare = 1 - Math.Clamp(pressure.Artifact, 0, 1);
            var full = seat.Attack * (amplified + vulnerableGain);
            damage += baseline + (full - baseline) * artifactShare + seat.OtherDamage;
            unamplified += seat.Attack + seat.OtherDamage;
        }

        double noWeak = 1, noDebilitateTeam = 1, neitherTeam = 1, strengthDown = 0;
        foreach (var source in windows)
        {
            noWeak *= 1 - source.Sum(w => w.Probability * Coverage(w.Weak));
            noDebilitateTeam *= 1 - source.Sum(w => w.Probability * Coverage(w.Debilitate));
            neitherTeam *= source.Sum(w => w.Probability
                * (1 - Coverage(w.Weak)) * (1 - Coverage(w.Debilitate)));
            for (var age = 1; age < Math.Clamp(pressure.CoverageTurns, 1, 3); age++)
            {
                noWeak *= source.Sum(w => w.Probability * (1 - Coverage(w.Weak - age)));
                noDebilitateTeam *= source.Sum(w => w.Probability * (1 - Coverage(w.Debilitate - age)));
                neitherTeam *= source.Sum(w => w.Probability
                    * (1 - Coverage(w.Weak - age)) * (1 - Coverage(w.Debilitate - age)));
            }
            strengthDown += source.Sum(w => w.Probability * w.StrengthDown);
        }
        for (var defender = 0; defender < party.Count; defender++)
        {
            var seat = party[defender];
            if (seat.Hp <= 0 || defender == excludedDefender) continue;
            var paidBlock = Math.Clamp(seat.PaidBlock, 0, Math.Max(0, seat.Block));
            double energyShare = 0, componentBlock = 0;
            foreach (var window in windows[defender])
            {
                energyShare += window.Probability * EnergyShare(window, seat);
                componentBlock += window.Probability * window.Block;
            }
            var block = seat.Block - paidBlock + paidBlock * energyShare + componentBlock;
            var hit = Math.Max(0, pressure.Hit);
            var jointCoverage = Math.Max(0, 1 - noWeak - noDebilitateTeam + neitherTeam);
            var weakenedHit = Math.Max(0, hit - strengthDown)
                * Math.Max(0, 1 - (1 - noWeak + jointCoverage) * (1 - seat.WeakFactor));
            var before = Math.Max(0, hit * pressure.Repeats - block);
            var after = Math.Max(0, weakenedHit * pressure.Repeats - block);
            if (exposures is not null)
            {
                var artifact = Math.Clamp(pressure.Artifact, 0, 1);
                var afterDebuffs = artifact * hit * pressure.Repeats
                    + (1 - artifact) * weakenedHit * pressure.Repeats;
                exposures[defender] = new(afterDebuffs, Math.Max(0, afterDebuffs - block))
                    { AvailableBlock = block };
            }
            var saved = Math.Min(seat.Hp, before) - Math.Min(seat.Hp, after);
            var survivalWeight = 1 + 2 * Math.Clamp(1 - seat.Hp / Math.Max(1, seat.MaxHp), 0, 1);
            if (before >= seat.Hp && after < seat.Hp) survivalWeight += 2;
            prevention += saved * survivalWeight * (1 - Math.Clamp(pressure.Artifact, 0, 1));
        }
        return new(Math.Min(pressure.Hp, damage), prevention, Math.Min(pressure.Hp, unamplified))
            { Exposures = exposures };
    }

    public static double BoundedMarginal(double marginal, double ceiling = 40)
        => ceiling * Math.Tanh(marginal / Math.Max(1, ceiling));

    public static double DelayedMarginal(double marginal, double remainingHp,
        double ordinaryOutput, double arrivalTurns, double availability,
        double survival, double dilution = 0)
    {
        if (remainingHp <= ordinaryOutput * Math.Max(0, arrivalTurns) || survival <= 0) return 0;
        var turns = Math.Clamp(remainingHp / Math.Max(1, ordinaryOutput) - arrivalTurns, 0, 3);
        return Math.Max(0, Math.Min(remainingHp, marginal * turns)
            * Math.Clamp(availability, 0, 1) * Math.Clamp(survival, 0, 1)
            * Math.Pow(.7, Math.Max(0, arrivalTurns)) - dilution);
    }

    private static double Coverage(double duration) => Math.Clamp(duration, 0, 1);
    private static double EnergyShare(Window window, Seat seat)
        => Math.Clamp(window.EnergyLeft / Math.Max(1, seat.Energy), 0, 1);

    private static Window[] Windows(Seat seat)
    {
        if (seat.Hp <= 0) return [new(1, 0, 0, 0, 0, 0, 0, 1)];
        var components = seat.Components.Take(MaximumComponents).ToArray();
        if (components.Length == 0) return [new(1, 0, 0, 0, 0, 0, seat.Energy, 1)];
        var size = Math.Max(components.Length, seat.Size);
        var draw = Math.Clamp((int)Math.Round(seat.Draw), 0, size);
        var windows = new List<Window>(1 << components.Length);
        for (var mask = 0; mask < 1 << components.Length; mask++)
        {
            var drawn = BitOperations.PopCount((uint)mask);
            var probability = Choose(size - components.Length, draw - drawn) / Choose(size, draw);
            if (probability <= 0) continue;
            (double Flanking, double Knockdown, double Vulnerable, double Weak,
                double StrengthDown, double Debilitate, double EnergyLeft, double Block) best = default;
            var bestValue = double.NegativeInfinity;
            var subset = mask;
            while (true)
            {
                double flanking = 1, knockdown = 0, vulnerable = 0, weak = 0,
                    strengthDown = 0, debilitate = 0, cost = 0, block = 0;
                for (var i = 0; i < components.Length; i++)
                {
                    if ((subset & (1 << i)) == 0) continue;
                    var c = components[i];
                    cost += c.Cost;
                    flanking *= Math.Max(1, c.Flanking);
                    knockdown += c.Knockdown;
                    vulnerable += c.Vulnerable;
                    weak += c.Weak;
                    strengthDown += c.StrengthDown;
                    debilitate += c.Debilitate;
                    block += Math.Max(0, c.Block);
                }
                if (cost <= seat.Energy)
                {
                    var value = flanking * Math.Max(1, knockdown)
                        * (1 + .5 * Coverage(vulnerable) * (1 + Coverage(debilitate)))
                        + .25 * Coverage(weak) * (1 + Coverage(debilitate))
                        + .05 * strengthDown + .01 * block - .01 * cost;
                    if (value > bestValue)
                    {
                        bestValue = value;
                        best = (flanking, knockdown, vulnerable, weak, strengthDown,
                            debilitate, seat.Energy - cost, block);
                    }
                }
                if (subset == 0) break;
                subset = (subset - 1) & mask;
            }
            if (bestValue > double.NegativeInfinity)
                windows.Add(new(best.Flanking, best.Knockdown, best.Vulnerable, best.Weak,
                    best.StrengthDown, best.Debilitate, best.EnergyLeft, probability, best.Block));
        }
        return windows.ToArray();
    }

    private static double Choose(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        k = Math.Min(k, n - k);
        double combinations = 1;
        for (var i = 1; i <= k; i++) combinations *= (double)(n - k + i) / i;
        return combinations;
    }
}
