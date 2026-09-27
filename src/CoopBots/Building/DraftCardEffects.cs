using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Native amounts and timing, separate from OUR bounded deck-cycle estimates.
// Audit: work/strength-20260927-v9/semantic-audit.md. These are not combat rules.
internal static class DraftCardEffects
{
    internal readonly record struct Effects(double TemporaryStrength = 0, double TemporaryDexterity = 0,
        double StrengthPerTurn = 0, double ReplayAttacks = 0, double ConditionalDamage = 0,
        int ExhaustRequired = 0, double GeneratedShivs = 0, double ExhaustDraw = 0,
        double ExhaustBlock = 0, bool FreeExhaustSkills = false, double ConditionalPoison = 0)
    {
        internal bool NeedsLifecycle => this != default;
    }

    internal static Effects Of(CardModel card)
    {
        double Amount(string key) => card.DynamicVars.TryGetValue(key, out var v) ? (double)v.BaseValue : 0;
        return card.GetType().Name switch
        {
            "Fade" or "Anticipate" => new(TemporaryDexterity: Amount("DexterityPower")),
            "Coordinate" or "SetupStrike" => new(TemporaryStrength: Amount("StrengthPower")),
            "DemonForm" => new(StrengthPerTurn: Amount("StrengthPower")),
            "OneTwoPunch" => new(ReplayAttacks: Amount("Attacks")),
            "PactsEnd" => new(ConditionalDamage: Amount("Damage"), ExhaustRequired: (int)Amount("Cards")),
            "BladeDance" or "CloakAndDagger" => new(GeneratedShivs: Amount("Cards")),
            "DarkEmbrace" => new(ExhaustDraw: 1),
            "FeelNoPain" => new(ExhaustBlock: Amount("Power")),
            "Corruption" => new(FreeExhaustSkills: true),
            "BubbleBubble" => new(ConditionalPoison: CoopBots.Kernel.CardPoisonEffects.DirectAmount(card)),
            _ => default,
        };
    }

    // A deck-cycle trigger estimate, not the current fight's exhaust pile. The
    // hypothetical deck is authoritative during repeated removals and additions.
    internal static double ExhaustSupply(IReadOnlyList<CardModel> deck)
    {
        var corruption = deck.Any(c => Of(c).FreeExhaustSkills);
        return deck.Sum(c => (c.Keywords.Contains(CardKeyword.Exhaust) || c.Keywords.Contains(CardKeyword.Ethereal)
                || corruption && c.Type == CardType.Skill ? 1.0 : 0)
            + (c.GetType().Name is "TrueGrit" or "BurningPact" ? 1 : 0)
            + Of(c).GeneratedShivs);
    }

    internal static double ConditionalAvailability(Effects effect, IReadOnlyList<CardModel> deck)
        => effect.ExhaustRequired <= 0 ? 1 : Math.Clamp(ExhaustSupply(deck) / effect.ExhaustRequired, 0, 1) * .7;

    internal static double Value(Effects effect, IReadOnlyList<CardModel> deck, int energy)
    {
        if (!effect.NeedsLifecycle || deck.Count == 0) return 0;
        var profiles = deck.Select(c => (Card: c, Facts: CardProfile.Of(c, energy))).ToArray();
        var attacks = profiles.Where(c => c.Card.Type == CardType.Attack && c.Facts.Damage > 0).ToArray();
        var hitsPerHand = Math.Min(Math.Max(1, energy), attacks.Sum(c => Math.Max(1, c.Facts.Hits)) * 5.0 / deck.Count);
        var blocksPerHand = Math.Min(Math.Max(1, energy), profiles.Count(c => c.Facts.Block > 0) * 5.0 / deck.Count);
        // Three future turns: growth is received at turn start (1+2+3), while
        // temporary buffs apply only to the affordable remainder of this hand.
        var value = effect.TemporaryStrength * hitsPerHand * .7 * 1.2
            + effect.TemporaryDexterity * blocksPerHand * .7
            + effect.StrengthPerTurn * 6 * hitsPerHand * 1.2;
        if (attacks.Length > 0)
        {
            var replayOutput = attacks.OrderByDescending(c => c.Facts.Damage).Take(Math.Max(1, (attacks.Length + 2) / 3))
                .Average(c => c.Facts.Damage);
            var availableAttacks = Math.Min(Math.Max(0, energy - 1), attacks.Length * 4.0 / deck.Count);
            value += Math.Min(effect.ReplayAttacks, availableAttacks) * replayOutput * 1.2;
        }
        value += effect.ConditionalDamage * ConditionalAvailability(effect, deck) * 1.2;
        var poisonSources = deck.Count(CoopBots.Kernel.CardPoisonEffects.CanSeedPoison);
        var poisonAvailability = 1 - Math.Pow(1 - poisonSources / (double)deck.Count, Math.Min(10, deck.Count));
        value += effect.ConditionalPoison * poisonAvailability * 3;
        value += effect.GeneratedShivs * 4 * 1.2;
        var triggers = ExhaustSupply(deck);
        value += Math.Min(8, triggers) * (effect.ExhaustDraw * 4 + effect.ExhaustBlock);
        if (effect.FreeExhaustSkills)
        {
            // Savings are one use per skill because Corruption also exhausts it.
            var saved = profiles.Where(c => c.Card.Type == CardType.Skill).Sum(c => Math.Max(0, c.Facts.Cost));
            value += Math.Min(8, saved) * 4;
        }
        return value;
    }
}
