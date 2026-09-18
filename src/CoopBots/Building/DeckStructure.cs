using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// Per-decision deck facts and the bounded contextual terms that fall out of
/// them: an expensive hand the energy cannot pay for, draw without affordable
/// plays, setup that outnumbers output, or a payoff duplicated past its
/// triggers.
///
/// Everything here is a cheap O(n) summary of the actual cards (cost, roles and
/// the same <see cref="CardProfile.Facts"/> the rest of the valuation uses).
/// Nothing is cached between decisions and nothing is keyed by card id alone:
/// two copies of the same id can be upgraded or rebalanced differently, so the
/// summary is rebuilt from the deck an entry point actually holds. Adjustments
/// are capped so a bottleneck can tip a close call but never hard-ban a card or
/// force an archetype.
/// </summary>
internal static class DeckStructure
{
    // A smaller deck has no stable curve or setup/output balance to speak of;
    // below this the summary stays silent rather than inventing a bottleneck.
    private const int MinimumSize = 8;
    private const double MaximumAdjustment = 6;
    private const int SetupHeavyThreshold = 8;

    // The stable per-turn energy a player starts a combat with when the caller
    // cannot read a character's persistent <c>Player.MaxEnergy</c>. This is a
    // conservative baseline, not a solvability model: conditional relic effects
    // beyond that field stay unknown. It is never the transient remaining energy
    // of one combat turn, which would make a permanent building decision depend
    // on the current fight.
    internal const int DefaultMaxEnergy = 3;
    // A typical hand the curve is normalised against, so cost is compared to the
    // energy that actually arrives per drawn card rather than per turn.
    private const double HandSample = 5;
    // Credit one energy-role card for 0.6 of a point of recurring supply, capped.
    // At a hand of five that is 0.12 energy per drawn card, at least as much as a
    // cost-2 generator adds to the average in a normal-size deck, so adding an
    // energy card can only lower pressure. It softens a heavy curve without
    // erasing it: a single generator cannot cover it, and four can cover a
    // starter curve.
    private const double EnergySourceCredit = 0.6;
    private const int MaxCountedEnergySources = 6;
    // Pressure reaches 1 once the shortfall is this many energy per drawn card.
    private const double EnergyPressureScale = 1.5;

    internal readonly record struct Summary(
        int Size, int Attacks, int Blocks, int Aoes, int Draws, int Energies,
        int Powers, int Setup, int Immediate, int Expensive, int Cheap, int Unplayable,
        int Unknown, double AverageCost, IReadOnlyDictionary<string, int> IdCounts,
        IReadOnlyDictionary<string, int> RoleCounts, int StableMaxEnergy);

    /// <summary>
    /// One pass over the deck. Curses, statuses and unplayable cards are counted
    /// only in <c>Unplayable</c>, and cards whose profile cannot be read are
    /// counted only in <c>Unknown</c>: neither is part of the useful supply a new
    /// card is measured against, and neither lowers the average cost. Id counts
    /// exclude both, so a mechanism never mistakes an unplayable or unreadable
    /// card for a producer. Unknown is neutral evidence, never a defect.
    /// </summary>
    internal static Summary Build(IReadOnlyList<CardModel> deck, int stableMaxEnergy = DefaultMaxEnergy)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        int attacks = 0, blocks = 0, aoes = 0, draws = 0, energies = 0, powers = 0;
        int setup = 0, immediate = 0, expensive = 0, cheap = 0, unplayable = 0, unknown = 0;
        var totalCost = 0.0;

        foreach (var card in deck)
        {
            CardProfile.Facts facts;
            try { facts = CardProfile.Of(card); }
            catch { unknown++; continue; }
            if (facts.Unplayable || card.Type is CardType.Curse or CardType.Status)
            {
                unplayable++;
                continue;
            }
            var entry = card.Id.Entry;
            ids[entry] = ids.GetValueOrDefault(entry) + 1;
            foreach (var role in facts.Roles) roles[role] = roles.GetValueOrDefault(role) + 1;
            if (facts.Roles.Contains("damage")) attacks++;
            if (facts.Roles.Contains("aoe")) aoes++;
            if (facts.Roles.Contains("block")) blocks++;
            if (facts.Roles.Contains("draw")) draws++;
            if (facts.Roles.Contains("energy")) energies++;
            if (facts.Roles.Contains("power")) powers++;
            if (IsSetup(facts)) setup++;
            if (IsImmediate(facts)) immediate++;
            totalCost += Math.Max(0, facts.Cost);
            if (facts.Cost >= 2) expensive++;
            if (facts.Cost <= 0) cheap++;
        }

        var useful = Math.Max(1, deck.Count - unplayable - unknown);
        return new Summary(deck.Count, attacks, blocks, aoes, draws, energies, powers,
            setup, immediate, expensive, cheap, unplayable, unknown, totalCost / useful, ids, roles,
            stableMaxEnergy);
    }

    /// <summary>
    /// Energy pressure as the share of the deck's curve one drawn card's energy
    /// cannot cover, normalised over the useful deck. Supply is the stable
    /// per-combat energy (<see cref="Summary.StableMaxEnergy"/>, a caller-read
    /// <c>Player.MaxEnergy</c> when available) plus a small bounded credit per
    /// energy-role card. More supply can only lower pressure; a single generator
    /// cannot erase an otherwise expensive curve. No exact solvability is
    /// claimed, and no transient combat energy is consulted.
    /// </summary>
    internal static double EnergyPressure(Summary deck)
    {
        if (deck.Size == 0) return 0;
        var supplyPerTurn = Math.Max(1.0, deck.StableMaxEnergy)
            + Math.Min(deck.Energies, MaxCountedEnergySources) * EnergySourceCredit;
        var supplyPerCard = supplyPerTurn / HandSample;
        if (deck.AverageCost <= supplyPerCard) return 0;
        return Math.Clamp((deck.AverageCost - supplyPerCard) / EnergyPressureScale, 0.0, 1.0);
    }

    internal static bool IsSetup(CardProfile.Facts facts)
        => facts.Vulnerable > 0 || facts.Weak > 0 || facts.StrengthDown > 0
           || facts.Scaling > 0 || facts.Poison > 0 || facts.Doom > 0
           || facts.Roles.Contains("power");

    internal static bool IsImmediate(CardProfile.Facts facts)
        => facts.Damage > 0 || facts.Block > 0;

    /// <summary>
    /// Bounded contextual adjustment for one candidate against the deck's
    /// current curve and balance. Capped to ±<see cref="MaximumAdjustment"/>: a
    /// bounded heuristic that can move a ranking or cross the skip threshold,
    /// with no claim to a precise combat strength or win rate. It is not a ban
    /// and it does not force an archetype.
    /// </summary>
    internal static double Adjust(CardModel card, CardProfile.Facts facts, Summary deck, List<string> reasons)
    {
        if (deck.Size < MinimumSize) return 0;
        if (facts.Unplayable || card.Type is CardType.Curse or CardType.Status) return 0;

        var adjust = 0.0;
        var pressure = EnergyPressure(deck);
        if (facts.Cost >= 2 && pressure >= 0.6)
        {
            var penalty = Math.Min(4.0, (facts.Cost - 1) * 1.5 * pressure);
            adjust -= penalty;
            reasons.Add($"bottleneck:energy-{penalty:F1}");
        }
        if (facts.Draw > 0 && deck.Draws >= 2 && pressure >= 0.6)
        {
            adjust -= 2.0;
            reasons.Add("bottleneck:draw-unaffordable");
        }
        if (deck.Setup >= SetupHeavyThreshold && deck.Setup > deck.Immediate * 1.5)
        {
            if (IsSetup(facts))
            {
                adjust -= 2.0;
                reasons.Add("bottleneck:too-much-setup");
            }
            else if (IsImmediate(facts))
            {
                adjust += 1.5;
                reasons.Add("bottleneck:needs-output");
            }
        }
        return Math.Clamp(adjust, -MaximumAdjustment, MaximumAdjustment);
    }
}
