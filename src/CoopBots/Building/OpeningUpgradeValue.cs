using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Native upgrades change keywords; OUR construction model estimates the
// additional turns an opening engine can operate. User report 2026-09-27:
// Afterimage stayed unupgraded while camps repeatedly chose heal/dig.
internal static class OpeningUpgradeValue
{
    internal static double? Innate(CardModel card, Player player, IReadOnlyList<CardModel> deck, int energy)
    {
        if (card.GetType().Name is not ("Afterimage" or "InfiniteBlades" or "MachineLearning")) return null;
        if (deck.Count == 0) return 0;
        var facts = deck.Select(c => (Card: c, Facts: CardProfile.Of(c, energy))).ToArray();
        var usable = facts.Where(c => !c.Facts.Unplayable).ToArray();
        var draw = 5 + facts.Sum(c => c.Facts.Draw) * 5 / deck.Count;
        var free = usable.Count(c => c.Facts.Cost == 0) * draw / deck.Count;
        var paid = usable.Where(c => c.Facts.Cost > 0).ToArray();
        var paidCost = paid.Length == 0 ? 1 : paid.Average(c => c.Facts.Cost);
        var plays = free + Math.Min(draw - free, Math.Max(0, energy) / paidCost);
        plays += facts.Sum(c => c.Facts.Effects.GeneratedShivs) * 5 / deck.Count;
        // Existing persistent Shiv generation and the party's Outrage copies
        // also trigger Afterimage. Discount setup rather than assuming active.
        plays += deck.Count(c => c.GetType().Name == "InfiniteBlades") * .5;
        plays += player.RunState.Players.Where(p => p != player && p.Deck.Cards.Count > 0)
            .Sum(p => p.Deck.Cards.Count(c => c.GetType().Name == "Outrage") * 5d / p.Deck.Cards.Count);
        plays = Math.Clamp(plays, 1, 12);
        var opening = 5 + (player.Relics.Any(r => r.GetType().Name == "RingOfTheSnake") ? 2 : 0);
        var otherInnate = deck.Count(c => !ReferenceEquals(c, card) && c.Keywords.Contains(CardKeyword.Innate));
        // Probability the unupgraded engine has not appeared yet, over the
        // first four turns. This values reliability without claiming an exact
        // combat forecast or reading a shuffled order the bot cannot know.
        var missedTurns = Enumerable.Range(0, 4).Sum(turn =>
            Math.Max(0, 1 - Math.Min(deck.Count, opening + turn * draw) / deck.Count));
        var amount = card.DynamicVars.TryGetValue("AfterimagePower", out var power) ? (double)power.BaseValue : 1;
        var perTurn = card.GetType().Name switch
        {
            "Afterimage" => amount * plays * 1.25, // early prevention is available before the first hit
            "InfiniteBlades" => 4 * 1.2,
            "MachineLearning" => (double)card.DynamicVars.Cards.BaseValue * 4,
            _ => 0,
        };
        var crowding = Math.Clamp((opening - otherInnate) / (double)opening, 0, 1);
        return Math.Max(0, missedTurns * perTurn * crowding);
    }
}
