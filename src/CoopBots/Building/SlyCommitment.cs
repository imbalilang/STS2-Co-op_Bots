using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Printed cost is a real risk when Sly has to be played from hand. A discard
// that the existing bounded hand solver can actually pay supplies a free play.
// One deck-level receipt pass, not one search per Sly card or a named-card lift.
internal static class SlyCommitment
{
    internal static double Load(IReadOnlyList<CardModel> deck, int energy)
    {
        if (!deck.Any(c => c.Keywords.Contains(CardKeyword.Sly))) return 0;
        return ConstructionJobs.Value(() =>
        {
            var cards = deck.Select(c => new PayableHands.Card(c, CardProfile.Of(c, energy), 1)).ToArray();
            return () => FromReceipts(cards, energy);
        });
    }

    internal static double FromReceipts(PayableHands.Card[] cards, int energy)
    {
        var plays = new double[cards.Length]; var automatic = new double[cards.Length];
        foreach (var incoming in new[] { 12d, 26d })
        foreach (var enemies in new[] { 1, 3 })
        {
            var outcome = PayableHands.Evaluate(cards, cards.Length, 5, energy, incoming,
                SustainedDeckValue.EnduranceHealth, enemies, 1, 0);
            for (var i = 0; i < cards.Length; i++)
            { plays[i] += outcome.Plays[i]; automatic[i] += outcome.AutomaticPlays[i]; }
        }
        double load = 0;
        for (var i = 0; i < cards.Length; i++)
        {
            if (!cards[i].Model.Keywords.Contains(CardKeyword.Sly) || cards[i].Facts.Unplayable) continue;
            // Unreached/no-benefit paths provide no proof of a free payment.
            var freeShare = plays[i] <= 0 ? 0 : Math.Clamp(automatic[i] / plays[i], 0, 1);
            load += DeckStrength.CommitmentLoadForCost(cards[i].Facts.Cost, energy) * (1 - freeShare);
        }
        return load;
    }
}
