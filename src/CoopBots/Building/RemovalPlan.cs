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
/// The same call also carries the one removal PRIORITY: an unprotected
/// removable curse first (the shop's immediate-removal rule), then unupgraded
/// basic Strikes/Defends while any remain, then the rest of the deck. The shop's
/// removal purchase, every FromDeckForRemoval screen (events, Neow, Cook) and
/// the event option price all narrow their candidates through
/// <see cref="PriorityPool"/>, so the price a removal is valued at and the card
/// it actually removes cannot disagree across paths.
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
            // ONE priority for every removal path: the shop's removal purchase,
            // every FromDeckForRemoval screen (events, Neow, Cook) and the event
            // option price all narrow to the same candidates here. Protected
            // cards are held back unless nothing unprotected is left, so a forced
            // screen still fills its count without stripping the deck's last role.
            var pool = PriorityPool(remaining, context, out var protectedFallback);
            if (protectedFallback) fallback = true;

            var evaluated = pool.Select(card =>
            {
                var valuation = BuildValue.RemoveForDecision(card, player, context);
                var protection = BuildValue.RemovalProtection(card, context);
                return new Step(card, valuation.Total, valuation.Reason, protection.Protected);
            }).ToList();

            if (ConstructionDecision.AwaitingValues) break;

            // A stable original order for identical copies, with the card id as
            // the deterministic tie-break the rest of the deck tools use.
            //
            // AN ENCHANTMENT BREAKS THE TIE BEFORE POSITION DOES. Two otherwise equal copies
            // must never have the enchanted one removed because it happened to sit earlier in
            // the deck (community report 2026-09-23: a Replay Strike went before plain
            // Strikes). This is the last line of defence; BuildValue.Remove should already
            // score the enchanted copy lower, but a tie is exactly when the copy order must
            // not decide.
            var chosen = evaluated
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Card.Enchantment is not null ? 1 : 0)
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

    /// <summary>
    /// The one removal priority. <paramref name="candidates"/> is the set the
    /// caller may remove (native legality already applied); protected cards are
    /// only considered when every candidate is protected, and
    /// <paramref name="protectedFallback"/> reports that case. Within the
    /// unprotected pool a removable curse comes first, then unupgraded basic
    /// Strikes/Defends while any remain, then the rest of the deck.
    /// </summary>
    internal static IReadOnlyList<CardModel> PriorityPool(
        IReadOnlyList<CardModel> candidates, IReadOnlyList<CardModel> deck, out bool protectedFallback)
    {
        var unprotected = candidates
            .Where(card => !BuildValue.RemovalProtection(card, deck).Protected)
            .ToList();
        protectedFallback = unprotected.Count == 0;
        return PriorityTier(protectedFallback ? candidates : unprotected);
    }

    /// <summary>
    /// The tier order on its own, for callers that enumerate candidates before
    /// scoring them. Curse first (the shop's immediate-removal rule), then
    /// unupgraded basic Strikes/Defends, then everything passed in.
    /// </summary>
    internal static IReadOnlyList<CardModel> PriorityTier(IReadOnlyList<CardModel> pool)
    {
        var curses = pool.Where(card => card.Type == CardType.Curse
            && card.IsRemovable && !CoopBots.BotShopPlanner.IsNeverRemoved(card)).ToList();
        if (curses.Count > 0) return curses;
        var basics = pool.Where(IsUnupgradedBasicStrikeOrDefend).ToList();
        return basics.Count > 0 ? basics : pool;
    }

    private static bool IsUnupgradedBasicStrikeOrDefend(CardModel card)
        => card.IsRemovable && card.Rarity == CardRarity.Basic && !card.IsUpgraded
           && (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend));
}
