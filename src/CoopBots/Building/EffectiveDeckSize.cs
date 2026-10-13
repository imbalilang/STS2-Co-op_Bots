using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// ONE effective deck size, shared by both size mechanisms.
///
/// Two mutually inconsistent size rules used to run side by side: <c>DeckStrength</c>'s
/// <c>0.18*max(0, n-20)^2</c> (inside a candidate's total, so it moved the ranking) and
/// <c>RewardTakeBar</c>'s <c>max(0, n-24)*1.5</c> (a threshold only, so it moved TAKE/SKIP).
/// Different onsets, different shapes, and both charged on the same decision. They now
/// read ONE count, so they cannot disagree about how big the deck is.
///
/// WHY THE COUNT IS NOT THE CARD COUNT
/// A card that leaves the deck the first time it is played -- a Power, or anything with
/// Exhaust/Ethereal -- displaces draws for only part of the fight; a vanilla card keeps
/// displacing for every cycle. Pricing both as one card is what made a 25-card Silent deck
/// refuse a Rare shiv payoff: live 2026-10-06, FAN_OF_KNIVES scored -0.4 against a 1.5 bar,
/// and 11 of that deck's final 28 cards were self-removing, so its effective size was 22.5,
/// not 28. This is the same "leaves the deck after one use" notion <see cref="ConditionalCosts"/>
/// already uses for exhaust accounting.
///
/// WHY DRAW IS A DIVISOR
/// Draw does not shrink the deck; it raises how often the deck turns over, which is what
/// makes size hurt less. A larger divisor is exactly "the same cards act smaller". Draw is
/// ALREADY priced as combat value (the <c>hands</c> term), so this is a second channel and
/// <see cref="DrawScale"/> has to stay small enough not to become "draw is always right".
///
/// WHY THE DIVISOR IS GATED ON ENERGY
/// Drawing a card you cannot pay for buys nothing. Default play is three energy, so draw
/// only relieves pressure once the deck can spend what it draws -- a cheap curve or real
/// energy supply. <see cref="DeckStructure.EnergyPressure"/> is the existing 0..1 reading of
/// that (0 = energy to spare, 1 = energy-starved). Gating on it makes the bound semantic
/// instead of an arbitrary cap: the decks that fully discount draw are the ones that really
/// can keep drawing and playing. Guarding the opposite failure is what the fixed bar was
/// for -- live 2026-10-04 drafted 22 -> 31 cards with a starting Strike still on top.
/// </summary>
internal static class EffectiveDeckSize
{
    /// <summary>Weight of a card that leaves the deck when it is first played.</summary>
    internal const double SelfRemovingWeight = 0.5;

    /// <summary>Effective count at which size pressure starts.</summary>
    internal const double Onset = 16;

    /// <summary>
    /// How hard one point of draw-per-card shortens the effective count. Two is the
    /// owner's own example: a deck with 20% net-draw cards ends up about 1.4x "smaller"
    /// once the energy to pay for them exists.
    /// </summary>
    internal const double DrawScale = 2.0;

    /// <summary>Coefficient of the penalty subtracted from a deck's total.</summary>
    internal const double TotalCoefficient = 0.10;

    /// <summary>
    /// Skip-value per effective card above the onset. Deliberately shallow: with the
    /// onset at an EFFECTIVE 16, a 1.0 slope put the bar at 4.18 on the live 25-card
    /// deck, which is HIGHER than the 1.5 the old raw-count-24 rule charged -- the
    /// earlier onset ate the whole "gentler" intent and FAN_OF_KNIVES was still refused
    /// (total 3.92 against 4.18). 0.15 keeps the general shape while leaving an
    /// effective-20 deck a ~0.6 bar, so a measured payoff clears it and filler does not.
    /// The total-side penalty is what carries the early pressure; this only decides
    /// TAKE/SKIP.
    /// </summary>
    internal const double BarCoefficient = 0.15;

    /// <summary>
    /// True when the card is expected to leave the deck after it is played once. Curses and
    /// statuses are NOT excluded: the question this answers is "does it still occupy draw
    /// slots later", and an Ethereal one does not (live A10 ascender's bane included).
    /// </summary>
    internal static bool SelfRemoving(CardModel card, CardProfile.Facts facts)
        => facts.IsPower
           || card.Keywords.Contains(CardKeyword.Exhaust)
           || card.Keywords.Contains(CardKeyword.Ethereal);

    /// <summary>
    /// The deck's effective size: self-removing cards count <see cref="SelfRemovingWeight"/>,
    /// and draw shortens the total by however much the deck's energy can actually cash in.
    /// </summary>
    internal static double Effective(IReadOnlyList<CardModel> deck, Player player, int energy)
    {
        if (deck.Count == 0) return 0;
        double weighted = 0, draw = 0;
        foreach (var card in deck)
        {
            var facts = CardProfile.Of(card, energy);
            weighted += SelfRemoving(card, facts) ? SelfRemovingWeight : 1.0;
            draw += Math.Max(0, CardProfile.NetDraw(card, facts));
        }
        var drawPerCard = draw / deck.Count;
        if (drawPerCard <= 0) return weighted;
        var convert = 1 - DeckStructure.EnergyPressure(DeckStructure.Build(deck, energy));
        if (convert <= 0) return weighted;
        return weighted / (1 + drawPerCard * DrawScale * convert);
    }

    internal static double Effective(IReadOnlyList<CardModel> deck, Player player)
        => Effective(deck, player, BuildValue.StableEnergy(player));

    /// <summary>Subtracted from a deck's potential. Smooth, quadratic, and never capped.</summary>
    internal static double TotalPenalty(IReadOnlyList<CardModel> deck, Player player, int energy)
    {
        var over = Math.Max(0, Effective(deck, player, energy) - Onset);
        return TotalCoefficient * over * over;
    }

    /// <summary>Added to what skipping a reward is worth.</summary>
    internal static double TakeBarPressure(IReadOnlyList<CardModel> deck, Player player)
    {
        var over = Math.Max(0, Effective(deck, player) - Onset);
        return BarCoefficient * over;
    }
}
