using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

// Read on the preparation thread, like the existing allied draw/debuff models.
// Compare the SAME sampled hand with and without energy; give it to one ally,
// never to the caster and never to all allies at once. These are construction
// samples, not a prediction of the live shuffle.
internal static class AlliedEnergy
{
    internal static double Value(Player? owner, int amount)
    {
        if (amount <= 0 || owner?.RunState is not { } run) return 0;
        var best = 0d;
        foreach (var ally in run.Players.Where(p => p != owner && p.Creature.IsAlive))
        {
            var energy = BuildValue.StableEnergy(ally);
            // Other copies cannot recursively finance a hypothetical cycle.
            var cards = ally.Deck.Cards.Where(c => c is not BelieveInYou)
                .OrderBy(c => c.Id.Entry, StringComparer.Ordinal)
                .Select(c => new ConstructionHand.Slot(c, CardProfile.Of(c, energy))).ToArray();
            if (cards.Length == 0) continue;
            double value = 0;
            for (var sample = 0; sample < 4; sample++)
            {
                var offset = sample * cards.Length / 4;
                var hand = cards.Skip(offset).Concat(cards.Take(offset)).ToArray();
                var baseline = ConstructionHand.Solve(hand, 5, new(energy, 16, 100));
                var boosted = ConstructionHand.Solve(hand, 5, new(energy + amount, 16, 100));
                value += Math.Max(0, boosted.Utility - baseline.Utility) / 4;
            }
            best = Math.Max(best, value);
        }
        return best;
    }
}
