using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// What a deck is trying to become.
///
/// Two sources, in order of preference:
///
/// 1. Clusters mined from real runs (<see cref="BakedArchetypes"/>), keyed by
///    character and identified by the cards that most distinguish them. Precise
///    for shipped cards, but silent about anything the mining never saw.
/// 2. A short table of role routes, written here, used when no cluster matches:
///    a modded deck, an off-meta deck, or a character the feed does not cover.
///
/// Both only ever add. A deck neither source recognises gets no bonus at all,
/// which is the guarantee that an unknown deck cannot be valued worse than it
/// was before archetypes existed.
/// </summary>
internal static class Archetypes
{
    internal sealed record Route(string Name, string Payoff, string[] Enablers, int PayoffThreshold,
        int EnablerThreshold = 1);

    // Role routes, expressed in tags derived from the cards themselves.
    private static readonly Route[] Routes =
    [
        new("strength-multihit", "multihit", ["strength"], 2),
        new("strength-damage", "damage", ["strength"], 4),
        new("block-retain", "block", ["retain", "dexterity"], 4),
        new("poison", "poison", ["poison", "weak"], 2),
        new("doom", "doom", ["doom", "strengthdown"], 2),
        new("force-focus", "focus", ["focus"], 2),
        new("orb-block", "block", ["focus", "dexterity"], 4),
        new("shiv", "shiv", ["zerocost", "strength"], 2),
        new("minion", "minion", ["osty"], 2),
        new("exhaust-engine", "exhaust", ["draw", "energy"], 3),
        new("draw-engine", "draw", ["energy", "zerocost"], 4),
    ];

    /// <summary>
    /// A route the deck is on. <paramref name="Signature"/> is the mined cluster's
    /// defining cards (empty for a role route); <paramref name="Roles"/> are the
    /// roles the route is built from, used to recognise cards that are not part of
    /// a known signature.
    /// </summary>
    internal sealed record Match(string Name, double Strength,
        IReadOnlyCollection<string> Signature, IReadOnlyCollection<string> Roles);

    internal static IReadOnlyList<Match> Detect(IReadOnlyList<CardModel> deck) => Detect(deck, null);

    internal static IReadOnlyList<Match> Detect(IReadOnlyList<CardModel> deck, Player? player)
    {
        if (deck.Count == 0) return [];
        var clusters = MatchClusters(deck, player);
        // A mined cluster is the stronger statement: it names the actual cards.
        // Role routes only speak up when nothing that specific was recognised.
        return clusters.Count > 0 ? clusters : MatchRoutes(deck);
    }

    /// <summary>
    /// Bonus for a card in this deck: worth more when it advances the signature the
    /// deck already holds, or feeds the roles that signature is built from.
    /// </summary>
    internal static double Bonus(CardProfile.Facts facts, CardModel card, IReadOnlyList<CardModel> deck, Player? player)
    {
        var matches = Detect(deck, player);
        if (matches.Count == 0) return 0;
        var counts = Counts(deck);
        var bonus = 0.0;
        for (var rank = 0; rank < Math.Min(2, matches.Count); rank++)
        {
            var match = matches[rank];
            var rankWeight = rank == 0 ? 1.0 : 0.5;
            var signatureHit = match.Signature.Contains(card.Id.Entry);
            if (signatureHit)
                bonus += 14 * match.Strength * rankWeight;
            foreach (var role in match.Roles)
                if (facts.Roles.Contains(role))
                    bonus += 8 * match.Strength * rankWeight * Decay(counts.GetValueOrDefault(role));
        }
        // A deck that is committed to a route is also paying for cards that serve
        // no part of it.
        if (bonus <= 0 && matches[0].Strength > 0.5) bonus -= 4;
        return bonus;
    }

    // A cluster is recognised by its signature cards: at least two of them in the
    // deck. The defining relics confirm it but never substitute for the cards,
    // because a relic can be picked up without committing to the plan.
    private static List<Match> MatchClusters(IReadOnlyList<CardModel> deck, Player? player)
    {
        var character = CharacterId(player);
        if (character.Length == 0) return [];
        var deckIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in deck) deckIds.Add(card.Id.Entry);
        var relicIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (player is not null)
                foreach (var relic in player.Relics) relicIds.Add(relic.Id.Entry);
        }
        catch { /* relics are a confirmation signal only */ }

        var density = Math.Clamp(20.0 / Math.Max(10, deck.Count), 0.7, 1.3);
        var matches = new List<Match>();
        foreach (var cluster in BakedArchetypes.All)
        {
            if (!string.Equals(cluster.Character, character, StringComparison.OrdinalIgnoreCase)) continue;
            var hits = cluster.Cards.Count(deckIds.Contains);
            if (hits < 2) continue;
            var relicHits = cluster.Relics.Count(relicIds.Contains);
            var strength = Math.Min(1.0, (hits / (2.0 * 2.0) + 0.2 * Math.Min(1, relicHits)) * density);
            if (strength <= 0) continue;
            matches.Add(new Match(cluster.Name, strength, cluster.Cards, RolesOf(cluster.Cards, deck)));
        }
        return matches
            .OrderByDescending(match => match.Strength)
            .ThenByDescending(match => match.Signature.Count)
            .ThenBy(match => match.Name, StringComparer.Ordinal)
            .ToList();
    }

    // The roles a signature actually contributes in this deck, read from the copies
    // the deck holds so a partially assembled cluster is judged on what is there.
    private static IReadOnlyCollection<string> RolesOf(IReadOnlyList<string> signature, IReadOnlyList<CardModel> deck)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in deck)
        {
            if (!signature.Contains(card.Id.Entry)) continue;
            foreach (var role in RolesOf(card)) roles.Add(role);
        }
        return roles;
    }

    private static List<Match> MatchRoutes(IReadOnlyList<CardModel> deck)
    {
        var counts = Counts(deck);
        var matches = new List<Match>();
        foreach (var route in Routes)
        {
            var payoffs = counts.GetValueOrDefault(route.Payoff);
            if (payoffs < route.PayoffThreshold) continue;
            var enablers = route.Enablers.Sum(tag => counts.GetValueOrDefault(tag));
            if (enablers < route.EnablerThreshold) continue;
            var density = Math.Clamp(20.0 / Math.Max(10, deck.Count), 0.7, 1.3);
            var strength = Math.Min(1.0, (payoffs / (route.PayoffThreshold * 2.0) + 0.2 * Math.Min(1, enablers))
                * density);
            if (strength <= 0) continue;
            var roles = new List<string> { route.Payoff };
            roles.AddRange(route.Enablers);
            matches.Add(new Match(route.Name, strength, [], roles));
        }
        return matches.OrderByDescending(match => match.Strength).ThenBy(match => match.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string CharacterId(Player? player)
    {
        try { return player?.Character?.Id.Entry ?? ""; }
        catch { return ""; }
    }

    private static Dictionary<string, int> Counts(IReadOnlyList<CardModel> deck)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in deck)
            foreach (var role in RolesOf(card))
                counts[role] = counts.GetValueOrDefault(role) + 1;
        return counts;
    }

    private static IReadOnlySet<string> RolesOf(CardModel card)
    {
        try { return CardProfile.Of(card).Roles; }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    private static double Decay(int existing) => existing switch { < 2 => 1.0, < 4 => 0.7, < 7 => 0.45, _ => 0.25 };
}
