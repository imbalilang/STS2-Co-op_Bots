using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

internal static class FixedTransform
{
    internal sealed record Candidate(CardModel Card, double Value, string Reason);

    // Native TransformTo creates a fresh, unupgraded result. Preview only:
    // no RNG, deck mutation, native command or ownership registration.
    internal static IReadOnlyList<Candidate> Rank(Player player, IEnumerable<CardModel> candidates, string replacementId)
    {
        var before = player.Deck.Cards.ToArray();
        var replacement = ModelDb.AllCards.First(c => c.Id.Entry == replacementId).ToMutable();
        replacement.Owner = player;
        var baseline = BuildValue.Deck(before, player);
        return candidates.Where(c => c.IsTransformable && before.Contains(c)).Distinct().Select(card =>
        {
            var after = TransformPlan.Replace(player, before, card, replacement);
            var value = BuildValue.Deck(after, player) - baseline;
            return new Candidate(card, value, $"fixed-transform:{card.Id.Entry}{(card.IsUpgraded ? "+" : "")}->{replacementId},deck-delta:{value:F2}");
        }).OrderByDescending(c => c.Value).ThenBy(c => c.Card.Id.Entry, StringComparer.Ordinal).ToArray();
    }

    internal static double WoodValue(Player player, string replacementId)
        => Rank(player, player.Deck.Cards.Where(c => c.Rarity == CardRarity.Basic && c.IsTransformable), replacementId)
            .FirstOrDefault()?.Value ?? double.NegativeInfinity;
}
