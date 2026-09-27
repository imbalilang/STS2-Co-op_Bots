namespace CoopBots.Building;

/// <summary>
/// The four-player counterpart of <see cref="BakedCardElo"/>: how far a card's
/// Elo should move when four seats share one enemy board.
///
/// <para>
/// WHY THIS EXISTS. <c>BakedCardElo</c> is a SINGLE-PLAYER revealed-preference
/// table. The four-player community data separates winners from losers on a
/// different axis, and the project's own analysis of 747 finished A10 decks
/// measured it per card (`outputs/mp-a10-deck-findings.md`, §4.1): Ironclad
/// winners take efficient damage-plus-draw and high single-card block, while the
/// <c>INFLAME</c> / <c>MOLTEN_FIST</c> slow-strength profile is the LOSING one —
/// <c>INFLAME</c> was taken by 0/31 winners against 13/23 losers, <c>MOLTEN_FIST</c>
/// by 11% of winners against 61% of losers. The single-player Elo table rates
/// those cards well enough that the live A5 bot kept building exactly that
/// losing profile (A5J1 took <c>MOLTEN_FIST</c> three times and <c>SHRUG_IT_OFF</c>
/// once while dying at the act-1 boss).
/// </para>
///
/// <para>
/// SCOPE. Applied only when more than one seat is in the run, so every
/// single-player number in the suite is untouched by construction. The scale is
/// <c>elo-base</c> units — the same unit as <c>(elo - skip) / 10</c> — so the
/// magnitudes here are directly comparable to the term they compete with.
/// </para>
///
/// <para>
/// TWO EVIDENCE TIERS, TWO MAGNITUDES. The offered-versus-chosen rate is the
/// strong one: it asks what players did when both cards were on the table. Deck
/// presence — the card showing up in a FINISHED deck — is weaker, because you can
/// only show up if the run lasted long enough to draw it, so it carries a survivor
/// bias. Deck-presence rows therefore get about half the magnitude, and only the
/// ones the analysis names with a split of roughly two-to-one or worse are listed
/// at all.
/// </para>
/// </summary>
internal static class MultiplayerPickLift
{
    internal const string Source = "outputs/mp-a10-deck-findings.md";
    internal const string Measured = "2026-09-16";
    /// <summary>Below this many seats the lift is not applied at all.</summary>
    internal const int MinimumSeats = 2;

    // Positive = the four-player data says winners take it; negative = losers do.
    // Tier 1 (offered to chosen): full magnitude. Tier 2 (deck presence): half.
    private static readonly Dictionary<string, double> Lift = new(StringComparer.Ordinal)
    {
        // Tier 1 — winners: efficient damage (draw attached), energy, one-card block.
        ["POMMEL_STRIKE"] = 2.5,      // W 47/84 (56%)  L 10/68 (15%)
        ["DRUM_OF_BATTLE"] = 2.5,     // W 17/34 (50%)  L  4/48 ( 8%)
        ["TANK"] = 2.0,               // W  5/17 (29%)  L  0/11 ( 0%)
        ["CASCADE"] = 2.0,            // W  4/14 (29%)  L  0/14 ( 0%)
        // Tier 2 — 0-of-N among winners is the strongest deck-presence split there is.
        ["FLAME_BARRIER"] = 2.5,      // deck presence W 10/21 L  0/22
        ["IMPERVIOUS"] = 2.5,         // deck presence W  8/21 L  0/22
        // Tier 1 — winners, smaller margins.
        ["PYRE"] = 1.5,               // W 13/13(100%)  L 13/18 (72%)
        ["BARRICADE"] = 1.5,          // W 10/14 (71%)  L  8/18 (44%)
        // Tier 2 — deck presence, winner-leaning without a number in the analysis.
        ["TAUNT"] = 1.0,
        ["THUNDERCLAP"] = 1.0,
        // Tier 1 — losers: the slow strength-growth profile.
        ["INFLAME"] = -3.0,           // W  0/31 ( 0%)  L 13/23 (57%)
        ["STAMPEDE"] = -3.0,          // W  0/39 ( 0%)  L 10/25 (40%)
        ["DISMANTLE"] = -3.0,         // W  0/35 ( 0%)  L 21/58 (36%)
        ["EXPECT_A_FIGHT"] = -3.0,    // W  0/15 ( 0%)  L 12/38 (32%)
        ["MOLTEN_FIST"] = -2.5,       // W  7/63 (11%)  L 48/79 (61%)
        // Tier 2 — deck presence, losers' low-impact filler.
        ["SHRUG_IT_OFF"] = -1.5,      // deck presence W  7/21 L 14/22
        ["COLOSSUS"] = -1.5,          // deck presence, losers only (no number given)
    };

    /// <summary>Card count with a measured four-player lift.</summary>
    internal static int CardCount => Lift.Count;

    /// <summary>Lift in <c>elo-base</c> units; 0 for a card with no four-player evidence.</summary>
    internal static double Term(string cardId) => Lift.GetValueOrDefault(cardId, 0);

    /// <summary>
    /// The lift that actually applies to a run. Kept separate from
    /// <see cref="Term"/> so the table can be asserted directly and so the
    /// seat-count rule has exactly one implementation.
    /// </summary>
    internal static double Term(string cardId, int seats)
        => seats >= MinimumSeats ? Term(cardId) : 0;
}
