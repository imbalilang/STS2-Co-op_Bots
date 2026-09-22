using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

/// <summary>What a one-turn setup card is waiting for before it does anything.</summary>
internal enum TemporaryBuffTiming
{
    /// <summary>Subsequent attacks are what consume it (Concoct, Coordinate, …).</summary>
    Attack,
    /// <summary>Subsequent block cards are what consume it (Fade, Anticipate).</summary>
    Block,
    /// <summary>Subsequent cards of any type are what consume it (Oblivion).</summary>
    Cards,
}

/// <summary>
/// Curated one-turn setup cards and what has to follow them for the turn.
///
/// These are not unmodeled cards; the issue is TIMING. The simulator already knows
/// ConcoctPower / FadePower / CoordinatePower / OblivionPower and removes them at
/// the right side-turn end, but the cheap roll-out policy used to value them as
/// generic setup: it could play Concoct after the ally's attacks, Fade after the
/// ally's block cards, or a temporary potion after the last card. A card that is
/// only worth something BEFORE a subset of this turn's actions must be scored by
/// how many of those actions are still left.
///
/// Deliberately a small curated list. Extend it when a new one-turn multiplayer
/// setup card appears; a missing card remains on the generic path, it does not
/// become unplayable.
/// </summary>
internal static class KernelTemporaryBuffs
{
    internal static TemporaryBuffTiming? Timing(CardModel card) => card.Id.Entry switch
    {
        "CONCOCT" or "COORDINATE" or "SETUP_STRIKE" or "FLANKING" or "KNOCKDOWN" or "TAG_TEAM"
            => TemporaryBuffTiming.Attack,
        "FADE" or "ANTICIPATE" => TemporaryBuffTiming.Block,
        "OBLIVION" => TemporaryBuffTiming.Cards,
        _ => null,
    };

    /// <summary>
    /// A card with no immediate output of its own; if nothing it enables is left,
    /// holding it is strictly better than spending it now.
    /// </summary>
    internal static bool IsPureSetup(CardModel card) => card.Id.Entry is
        "CONCOCT" or "COORDINATE" or "FADE" or "ANTICIPATE" or "OBLIVION";

    /// <summary>Whether the card can be aimed at a teammate (so target choice matters).</summary>
    internal static bool IsAllyTarget(CardModel card) => card.TargetType == TargetType.AnyAlly;
}
