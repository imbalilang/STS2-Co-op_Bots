using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// The one deterministic way a set of removals is ordered. Each pick is
/// re-evaluated against the deck the previous picks would leave, so the last
/// copy of a role becomes protected exactly when removing it would strip the
/// deck of that role. Protected candidates are only taken when no unprotected
/// candidate remains (a forced screen still fills its count), and the live deck
/// and candidate list are never mutated — only local copies change.
///
/// Both the permanent deck-edit path (<see cref="CoopBots.BotBrain"/>) and the
/// rest-site Cook valuation use this, so the price a removal is valued at and
/// the card it actually removes cannot disagree.
/// </summary>
internal static class RemovalPlan
{
    internal readonly record struct Step(CardModel Card, double Value, string Reason, bool Protected);
    internal readonly record struct Plan(IReadOnlyList<Step> Steps, bool Fallback);

    internal static Plan Choose(Player player, IReadOnlyList<CardModel> candidates, int count,
        IReadOnlyList<CardModel>? deckOverride = null)
    {
        var context = (deckOverride ?? player.Deck.Cards.ToList()).ToList();
        var remaining = new List<CardModel>(candidates);
        var steps = new List<Step>();
        var fallback = false;

        for (var step = 0; step < count && remaining.Count > 0; step++)
        {
            var evaluated = remaining.Select(card =>
            {
                var valuation = BuildValue.Remove(card, player, context);
                var protection = BuildValue.RemovalProtection(card, context);
                return new Step(card, valuation.Total, valuation.Reason, protection.Protected);
            }).ToList();

            var unprotected = evaluated.Where(entry => !entry.Protected).ToList();
            if (unprotected.Count == 0) fallback = true;
            var pool = unprotected.Count > 0 ? unprotected : evaluated;
            // A stable original order for identical copies, with the card id as
            // the deterministic tie-break the rest of the deck tools use.
            var chosen = pool
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Card.Id.Entry, StringComparer.Ordinal)
                .ThenBy(entry => remaining.IndexOf(entry.Card))
                .First();

            steps.Add(chosen);
            remaining.Remove(chosen.Card);
            var contextIndex = context.FindIndex(card => ReferenceEquals(card, chosen.Card));
            if (contextIndex >= 0) context.RemoveAt(contextIndex);
        }

        return new Plan(steps, fallback);
    }
}
