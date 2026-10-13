using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// How much of a card's printed effect the BUILDING estimate can actually see.
///
/// This exists because "the deck-cycle model found nothing" and "the card does
/// nothing" used to be the same answer. Measured live 2026-09-29 on an Act-1 boss
/// reward: BURST (community Elo 1702.1 against a 1573.0 skip line) and
/// BLADE_OF_INK (1738.2) — the two highest-rated cards on the screen, above the
/// ones the bot actually took — were both skipped, because the cycle estimate
/// reads neither card's text and priced them as blank plays. Its -11.9 then
/// overrode the +12.9/+16.5 community prior, which the blend had clamped to +-2.
///
/// The rule this encodes: absence of evidence is not evidence of absence. A card
/// the model cannot read is scored by the community prior ALONE — a card we
/// cannot see must never be rejected on the strength of a model that pretends it
/// is empty. See <see cref="BuildValue.Add"/> for how the coverage weights the
/// two sources.
/// </summary>
internal static class BuildCoverage
{
    internal const double Full = 1.0;
    internal const double None = 0.0;

    // Cards whose PRIMARY printed quantity is a per-event count/preview that
    // CardProfile cannot evaluate out of combat. It reads a static base instead
    // (CalculatedDamage/CalculatedBlock preview, one hit, first play's damage,
    // a Repeat that the hit-count set does not know), so BuildCoverage must not
    // report Full merely because that base is non-zero.
    //
    // Audited 2026-10-02 against the local build with `--coverage-probe`:
    //   * IRONCLAD
    //     ASHEN_STRIKE     ExtraDamage per card in the Exhaust Pile
    //     DEMONIC_SHIELD   CalculatedBlock = the giver's current Block; only HpLoss is read
    //     EXPECT_A_FIGHT   CalculationExtra Block per Strength
    //     PERFECTED_STRIKE ExtraDamage per Strike card in the deck
    //     RAMPAGE          Increase per play; the base is only the first play
    //     SPITE            Repeat 2 only when HP was lost this turn
    //     TEAR_ASUNDER     CalculatedHits per time HP was lost this combat
    //   * SILENT
    //     BOUNCING_FLASK   Poison applied Repeat times (3 x 3, facts sees one application)
    //     FINISHER         CalculatedHits per Attack played this turn
    //     FLECHETTES       CalculatedHits per Skill in hand
    //     MURDER           ExtraDamage per card drawn this combat
    //     PRECISE_CUT      ExtraDamage less per other card in hand
    //
    // Deliberately NOT here:
    //   BULLY            SustainedDeckValue.DamagePerVulnerable models its real scaling.
    //   MEMENTO_MORI     SustainedDeckValue.Unsupported routes it to the structural policy.
    //   CONFLAGRATION / SWORD_BOOMERANG / RICOCHET / FIGHT_ME
    //                    Repeat is a hit count and the analyser reads it (hits > 1).
    internal static readonly HashSet<string> UnreadableMechanics = new(StringComparer.Ordinal)
    {
        "ASHEN_STRIKE", "DEMONIC_SHIELD", "EXPECT_A_FIGHT", "PERFECTED_STRIKE",
        "RAMPAGE",
        "BOUNCING_FLASK", "FINISHER", "FLECHETTES", "MURDER", "PRECISE_CUT",
    };

    /// <summary>
    /// Whether the deck-cycle estimate can price what this card prints. The
    /// families it can see are exactly the ones <see cref="CardProfile.Facts"/>
    /// and <see cref="DraftCardEffects"/> carry; anything else — a play-count
    /// modifier like Burst (the KERNEL models it, see ModifyCardPlayCountMirrors),
    /// card generation, an unknown power — is unseen.
    /// </summary>
    internal static double Of(CardModel card)
    {
        // Curses and statuses are refused by a named guard before any valuation,
        // so their coverage is irrelevant; do not report them as unreadable.
        if (card.Type is CardType.Curse or CardType.Status) return Full;
        // A power outside the known table (and the named state-dependent cards) is
        // already routed to the structural policy by the cycle model itself:
        // SustainedDeckValue.Compare returns null, so Add takes the honest
        // `estimate:structural` branch and the archetype-fit residual still decides.
        // That is absence of evidence handled correctly — this class must not
        // double-handle it, or it would also silence the fit signal those cards
        // legitimately carry.
        if (SustainedDeckValue.Unsupported(card)) return Full;
        // A non-zero base fact is not evidence that the printed per-event
        // quantity was read; these cards are the audited exceptions.
        if (UnreadableMechanics.Contains(card.Id.Entry)) return None;

        var facts = CardProfile.Of(card);
        if (facts.DrawPileDamage || facts.AlliedEnergyValue > 0) return Full;
        if (card is MegaCrit.Sts2.Core.Models.Cards.Cascade or MegaCrit.Sts2.Core.Models.Cards.Havoc) return Full;
        if (facts.Unplayable) return Full;   // unplayable is a guard, not a valuation
        if (facts.Damage != 0 || facts.Block != 0 || facts.Draw != 0 || facts.Energy != 0
            || facts.Vulnerable != 0 || facts.Weak != 0 || facts.StrengthDown != 0
            || facts.Poison != 0 || facts.Doom != 0 || facts.Scaling != 0 || facts.HpLoss != 0
            || facts.NextTurnEnergy != 0 || facts.NextTurnDraw != 0
            || facts.DrawForTeam) return Full;
        if (facts.Effects.NeedsLifecycle) return Full;
        if (SustainedDeckValue.KnownPower(card) || SustainedDeckValue.Cleanup(card)) return Full;
        // Nothing in the estimate can see this card. It is not a blank playable
        // card; it is a card we cannot read.
        return None;
    }
}
