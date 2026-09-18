using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// Community-draft signals shared by the pick and shop paths: the directional
/// held-card affinity term and a pure all-runs Elo baseline kept for
/// diagnosis. The affinity here replaces the legacy additive prior inside the
/// Elo-first valuation; the pure baseline is never used to make a decision, it
/// only exists so a hybrid choice can be compared against Elo alone.
/// </summary>
internal static class CommunityDraft
{
    // Per held card, a lift of L contributes min(3, 2 * (L - 1)). Only the two
    // strongest unique held ids pay, and the whole term is capped, so a handful
    // of lucky pairs cannot carry a candidate on its own. Missing relations are
    // zero, never a negative.
    private const double PerLift = 2.0;
    private const double LiftBaseline = 1.0;
    private const double PerHeldCap = 3.0;
    internal const int MaxHeldIds = 2;
    internal const double AffinityCap = 6.0;

    internal readonly record struct EloRanking(
        string? Selected,
        IReadOnlyList<string> Ranking,
        IReadOnlyList<string> Unavailable,
        bool Available,
        bool SkipIncluded);

    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    internal static double Affinity(CardModel candidate, IReadOnlyList<CardModel> deck)
        => Affinity(candidate.Id.Entry, candidate, deck);

    /// <summary>
    /// Directional affinity: a held card's document recommends the offered
    /// candidate, never the reverse. Held card ids are unique (a deck holding
    /// three copies of one card pays once), the candidate instance and its own
    /// id are excluded, and only the two strongest positive lifts pay.
    /// </summary>
    internal static double Affinity(string candidateId, CardModel? candidateInstance, IReadOnlyList<CardModel> deck)
    {
        if (deck.Count == 0 || string.IsNullOrEmpty(candidateId)) return 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lifts = new List<double>();
        foreach (var held in deck)
        {
            if (candidateInstance is not null && ReferenceEquals(held, candidateInstance)) continue;
            var heldId = held.Id.Entry;
            // A card never recommends its own id here: the affinity feed is a
            // cross-card relation, so a self edge would be double counting.
            if (string.Equals(heldId, candidateId, StringComparison.Ordinal)) continue;
            // One held copy pays whether the deck holds one or four.
            if (!seen.Add(heldId)) continue;
            if (!BakedCardAffinity.Pairs.TryGetValue(heldId, out var partners) || partners.Length == 0) continue;
            foreach (var (partner, lift) in partners)
            {
                if (!string.Equals(partner, candidateId, StringComparison.Ordinal)) continue;
                if (IsFinite(lift) && lift > LiftBaseline)
                    lifts.Add(Math.Min(PerHeldCap, PerLift * (lift - LiftBaseline)));
                break;
            }
        }
        if (lifts.Count == 0) return 0;
        lifts.Sort((left, right) => right.CompareTo(left));
        var bonus = 0.0;
        for (var index = 0; index < Math.Min(MaxHeldIds, lifts.Count); index++) bonus += lifts[index];
        return Math.Min(bonus, AffinityCap);
    }

    /// <summary>
    /// Pure all-runs Elo ranking, deterministic and bracket-local. Cards with
    /// no Elo are excluded and listed explicitly, never treated as a silent
    /// zero. Ties break SKIP first, then card id ascending, matching the
    /// audit baseline this table came from.
    /// </summary>
    internal static EloRanking RankByElo(IReadOnlyList<CardModel> candidates, bool includeSkip)
    {
        var available = new Dictionary<string, double>(StringComparer.Ordinal);
        var unavailable = new List<string>();
        foreach (var card in candidates)
        {
            var id = card.Id.Entry;
            if (BakedCardElo.TryGet(id, out var elo) && IsFinite(elo)) available[id] = elo;
            else unavailable.Add(id);
        }
        var skipIncluded = false;
        if (includeSkip)
        {
            if (BakedCardElo.CardCount > 0 && IsFinite(BakedCardElo.SkipElo))
            {
                available["SKIP"] = BakedCardElo.SkipElo;
                skipIncluded = true;
            }
            else
            {
                unavailable.Add("SKIP");
            }
        }
        unavailable.Sort(StringComparer.Ordinal);
        if (available.Count == 0)
            return new EloRanking(null, Array.Empty<string>(), unavailable, false, skipIncluded);
        var ranking = available.Keys.ToList();
        ranking.Sort((left, right) =>
        {
            var byElo = available[right].CompareTo(available[left]);
            if (byElo != 0) return byElo;
            var leftSkip = string.Equals(left, "SKIP", StringComparison.Ordinal);
            var rightSkip = string.Equals(right, "SKIP", StringComparison.Ordinal);
            if (leftSkip != rightSkip) return leftSkip ? -1 : 1;
            return string.CompareOrdinal(left, right);
        });
        return new EloRanking(ranking[0], ranking, unavailable, true, skipIncluded);
    }
}
