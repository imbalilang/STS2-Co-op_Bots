using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// Explicit mechanism dependencies — whether a payoff actually has a trigger in
/// this deck.
///
/// This replaces the old <c>UnsupportedScaling</c> that read the community
/// affinity table as if "these cards were drafted together" meant "this card's
/// enabler is missing". That inference was wrong on its own terms: the feed is
/// a co-draft prior, not a trigger map. Every judgement here instead comes from
/// <see cref="BakedResources"/> (the single source of truth for producers and
/// amplifiers) and <see cref="CardProfile"/> roles, both derived from the real
/// card models.
///
/// The cases are deliberately narrow and reliable. A card whose mechanism this
/// table does not classify is Unknown and is never penalised: no mining record
/// is not evidence of a missing trigger.
/// </summary>
internal static class DeckMechanisms
{
    internal enum Support
    {
        /// <summary>Every card the mechanism needs is already in the deck.</summary>
        Supported,

        /// <summary>The mechanism is recognised and its trigger is absent.</summary>
        Unsupported,

        /// <summary>No evidence either way; callers must not penalise it.</summary>
        Unknown,
    }

    /// <summary>
    /// <paramref name="Payoff"/> is true when the card is the amplifier of a
    /// resource rather than its producer, which is what makes a duplicate copy
    /// a redundancy question rather than a plain value one.
    /// </summary>
    internal readonly record struct Assessment(
        Support Support, string Reason, string Resource, int Triggers, bool Payoff);

    private static readonly BakedResources.Resource? Shiv = Find("shiv");
    private static readonly BakedResources.Resource? Poison = Find("poison");
    private static readonly BakedResources.Resource? Doom = Find("doom");
    private static readonly BakedResources.Resource? Orb = Find("orb");
    private static readonly BakedResources.Resource? Strength = Find("strength");
    private static readonly BakedResources.Resource? HpLoss = Find("hp-loss");

    private static BakedResources.Resource? Find(string name)
        => BakedResources.All.FirstOrDefault(resource => string.Equals(resource.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Whether the card's mechanism is supported by the deck. Uses the deck as
    /// it would be after the decision (producers already present), never a live
    /// community query and never <c>player.Deck</c> directly, so a hypothetical
    /// deck is judged on its own contents.
    /// </summary>
    /// <param name="candidateInDeck">
    /// Whether the candidate is the actual instance already present in
    /// <paramref name="deck"/> (instance identity, not an id match). A new,
    /// external self-contained producer+payoff is not yet counted by the deck's
    /// producer total, so it must add itself; an already-held one is, so it must
    /// not. Defaults to false for external candidates.
    /// </param>
    internal static Assessment Assess(CardModel card, CardProfile.Facts facts, DeckStructure.Summary deck,
        bool candidateInDeck = false)
    {
        var entry = card.Id.Entry;

        // Rupture is both a Strength producer and a self-damage payoff, and the
        // explicit HP-loss dependency wins: Strength only arrives when the deck
        // loses HP, so attacks alone do not make it supported. Rupture is filed
        // as a converter of the hp-loss resource (it reads the life spent, not
        // the life itself), so both converter and spender buckets are checked.
        if (HpLoss is not null && (Contains(HpLoss.Converters, entry) || Contains(HpLoss.Spends, entry)))
        {
            var losses = Count(deck, HpLoss.Producers);
            return losses > 0
                ? new(Support.Supported, "mech-supported:hp-loss", "hp-loss", losses, true)
                : new(Support.Unsupported, "mech-unsupported:hp-loss", "hp-loss", 0, true);
        }

        // An amplifier or replayer is only worth its printed numbers when the
        // resource it multiplies is produced. A self-contained card that is both
        // producer and amplifier pays for itself.
        foreach (var resource in Amplified(entry))
        {
            var selfProduced = Contains(resource.Producers, entry);
            var producers = Count(deck, resource.Producers);
            // A self-contained producer/payoff counts itself exactly once. When
            // the candidate instance is already held it is inside `producers`;
            // when it is a new, external candidate it is not yet, so it adds
            // itself. Subtracting every count of the candidate's id
            // unconditionally read a non-producer payoff (Accuracy, Focus) as if
            // it were the producer and discounted real Blade Dance / orb supply;
            // adding self back unconditionally undercounted a new producer by
            // one. Only the candidate instance, never a same-id sibling, is its
            // own trigger.
            var triggers = selfProduced ? producers + (candidateInDeck ? 0 : 1) : producers;
            if (selfProduced || triggers > 0)
                return new(Support.Supported, "mech-supported:" + resource.Name, resource.Name,
                    triggers, true);
            return new(Support.Unsupported, "mech-unsupported:" + resource.Name, resource.Name, 0, true);
        }

        // Focus does nothing without orbs to channel. Only reliable when the
        // orb table classifies the deck, so an unclassified deck stays Unknown.
        if (facts.Roles.Contains("focus") && Orb is not null)
        {
            var selfProduced = Contains(Orb.Producers, entry);
            var producers = Count(deck, Orb.Producers);
            var orbs = selfProduced ? producers + (candidateInDeck ? 0 : 1) : producers;
            if (selfProduced || orbs > 0)
                return new(Support.Supported, "mech-supported:orb", "orb", orbs, false);
            return new(Support.Unsupported, "mech-unsupported:orb", "orb", 0, false);
        }

        // Ordinary Strength multiplies the attacks the deck already has.
        if (facts.Roles.Contains("strength") && Strength is not null && Contains(Strength.Producers, entry))
        {
            if (deck.Attacks > 0)
                return new(Support.Supported, "mech-supported:strength", "strength", deck.Attacks, false);
            return new(Support.Unsupported, "mech-unsupported:strength", "strength", 0, false);
        }

        // Dexterity multiplies the block the deck already has. A lone block card
        // does not make it a plan: three is the previous calibration, and below
        // it the multiplier has too little to work with.
        if (facts.Roles.Contains("dexterity"))
        {
            if (deck.Blocks >= 3)
                return new(Support.Supported, "mech-supported:block", "block", deck.Blocks, false);
            return new(Support.Unsupported, "mech-unsupported:block", "block", 0, false);
        }

        return new(Support.Unknown, "mech-unknown", string.Empty, -1, false);
    }

    private static IEnumerable<BakedResources.Resource> Amplified(string entry)
    {
        foreach (var resource in BakedResources.All)
            if (Contains(resource.Multipliers, entry) || Contains(resource.Replayers, entry))
                yield return resource;
    }

    /// <summary>
    /// Resource name when this card produces a resource whose payoff is a separate
    /// multiplier/replayer the deck does not hold yet.
    ///
    /// <see cref="Assess"/> only prices multipliers without producers; a producer is
    /// Unknown and therefore free to draft. Measured 2026-09-22: BLADE_DANCE and
    /// HIDDEN_DAGGERS were taken with no ACCURACY, and the final Silent deck's route was
    /// left as a producer pile. A bounded penalty makes the incomplete package visible to
    /// the same valuation without pretending the producer is worthless by itself.
    /// </summary>
    internal static string? ProducerWithoutPayoff(CardModel card, DeckStructure.Summary deck)
    {
        var entry = card.Id.Entry;
        foreach (var resource in BakedResources.All)
        {
            if (!Contains(resource.Producers, entry)) continue;
            if (resource.Multipliers.Length == 0 && resource.Replayers.Length == 0) continue;
            if (Count(deck, resource.Multipliers) > 0 || Count(deck, resource.Replayers) > 0) continue;
            return resource.Name;
        }
        return null;
    }

    private static int Count(DeckStructure.Summary deck, string[] ids)
    {
        var total = 0;
        foreach (var id in ids) total += deck.IdCounts.GetValueOrDefault(id);
        return total;
    }

    private static bool Contains(string[] ids, string entry)
        => ids.Any(id => string.Equals(id, entry, StringComparison.Ordinal));
}
