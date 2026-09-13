using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using CoopBots.Kernel.Vendor.Engine.Common;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

internal sealed class CombatPredictionRngSet
{
    public required Rng Shuffle { get; init; }
    public required Rng CombatCardGeneration { get; init; }
    public required Rng CombatPotionGeneration { get; init; }
    public required Rng CombatCardSelection { get; init; }
    public required Rng CombatEnergyCosts { get; init; }
    public required Rng CombatTargets { get; init; }
    public required Rng CombatOrbGeneration { get; init; }
    public required Rng MonsterAi { get; init; }
    public required Rng Niche { get; init; }

    public static CombatPredictionRngSet From(RunRngSet rng)
    {
        return new CombatPredictionRngSet
        {
            Shuffle = rng.Shuffle.Clone(),
            CombatCardGeneration = rng.CombatCardGeneration.Clone(),
            CombatPotionGeneration = rng.CombatPotionGeneration.Clone(),
            CombatCardSelection = rng.CombatCardSelection.Clone(),
            CombatEnergyCosts = rng.CombatEnergyCosts.Clone(),
            CombatTargets = rng.CombatTargets.Clone(),
            CombatOrbGeneration = rng.CombatOrbGeneration.Clone(),
            MonsterAi = rng.MonsterAi.Clone(),
            Niche = rng.Niche.Clone()
        };
    }

    internal CombatPredictionRngSet Fork()
    {
        return new CombatPredictionRngSet
        {
            Shuffle = Shuffle.Clone(),
            CombatCardGeneration = CombatCardGeneration.Clone(),
            CombatPotionGeneration = CombatPotionGeneration.Clone(),
            CombatCardSelection = CombatCardSelection.Clone(),
            CombatEnergyCosts = CombatEnergyCosts.Clone(),
            CombatTargets = CombatTargets.Clone(),
            CombatOrbGeneration = CombatOrbGeneration.Clone(),
            MonsterAi = MonsterAi.Clone(),
            Niche = Niche.Clone()
        };
    }
}
