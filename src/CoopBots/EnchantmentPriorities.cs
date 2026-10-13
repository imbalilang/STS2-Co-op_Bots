using System.Collections.Frozen;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

/// <summary>
/// Compiled translation of EnchantmentsTable.csv, owner policy 2026-10-04.
/// Upstream offers legal cards for a human to choose. We veto listed traits,
/// compare preference tiers, then preserve the previous scoring within a tier.
/// The owner permits minimum-required fallback only after a forced choice starts.
/// No CSV parsing, localized card text or arbitrary score bonuses at runtime.
/// </summary>
internal static class EnchantmentPriorities
{
    [Flags]
    internal enum Trait
    {
        None = 0, SelfReplay = 1, Zero = 2, One = 4, Two = 8, Three = 16,
        FourPlus = 32, X = 64, Damage = 128, TwoHits = 256, ThreeHits = 512,
        FourPlusHits = 1024, Bolas = 2048, Exhaust = 4096, Power = 8192,
        Innate = 16384, UpgradeRemovesExhaust = 32768, Upgraded = 65536,
    }
    internal sealed record Rule(Trait Extreme = Trait.None, Trait High = Trait.None,
        Trait Slight = Trait.None, Trait Veto = Trait.None, double? OptionScore = null,
        bool RankDamage = false);

    // A cell's alternatives are OR; veto is evaluated before all preferences.
    // CSV display names with spaces/apostrophes map to native CLR model names.
    internal static readonly FrozenDictionary<string, Rule> Table = new Dictionary<string, Rule>
    {
        ["Adroit"] = new(Trait.SelfReplay, Trait.Zero, Trait.One, Trait.Exhaust | Trait.Power),
        ["Clone"] = new(OptionScore: -100),
        ["Corrupted"] = new(High: Trait.X, Slight: Trait.Damage, Veto: Trait.Exhaust, RankDamage: true),
        ["Glam"] = new(),
        ["Goopy"] = new(),
        ["Imbued"] = new(High: Trait.X, Slight: Trait.Three | Trait.Two),
        ["Inky"] = new(),
        ["Instinct"] = new(High: Trait.X, Slight: Trait.Damage, Veto: Trait.Exhaust, RankDamage: true),
        ["Momentum"] = new(Trait.Bolas | Trait.FourPlusHits, Trait.Zero | Trait.X | Trait.ThreeHits, Trait.TwoHits, Trait.Exhaust),
        ["Nimble"] = new(High: Trait.Zero, Slight: Trait.One, Veto: Trait.Exhaust),
        ["PerfectFit"] = new(Veto: Trait.Exhaust | Trait.Power),
        ["RoyallyApproved"] = new(Veto: Trait.Innate),
        ["Sharp"] = new(Trait.FourPlusHits, Trait.Zero | Trait.X | Trait.ThreeHits, Trait.TwoHits, Trait.Exhaust),
        ["Slither"] = new(Trait.FourPlus, Trait.Three, Trait.Two, Trait.Zero | Trait.One),
        ["SlumberingEssence"] = new(Veto: Trait.Zero),
        ["SoulsPower"] = new(Veto: Trait.UpgradeRemovesExhaust),
        ["Sown"] = new(Slight: Trait.Innate),
        ["Spiral"] = new(Slight: Trait.Upgraded),
        ["Steady"] = new(Veto: Trait.Zero),
        ["Swift"] = new(High: Trait.Zero, Slight: Trait.One),
        ["TezcatarasEmber"] = new(),
        ["Vigorous"] = new(Trait.FourPlusHits, Trait.Zero | Trait.X | Trait.ThreeHits, Trait.TwoHits),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    internal readonly record struct Preference(int Tier, double DamageOrder, string Reason);
    internal sealed record Candidate(CardModel Card, Preference Preference, double Score);
    internal sealed record Plan(IReadOnlyList<CardModel> Cards, IReadOnlyList<Candidate> Ranked, bool ForcedFallback);

    internal static EnchantmentModel? Resolve(string name)
        => ModelDb.DebugEnchantments.FirstOrDefault(e => e.GetType().Name == name);

    internal static Preference Classify(CardModel card, EnchantmentModel enchantment)
    {
        if (!Table.TryGetValue(enchantment.GetType().Name, out var rule)) return new(0, 0, "ordinary:unlisted");
        var traits = Traits(card);
        if ((traits & rule.Veto) is { } rejected && rejected != Trait.None)
            return new(-1, 0, "veto:" + rejected);
        var tier = (traits & rule.Extreme) != 0 ? 3 : (traits & rule.High) != 0 ? 2 : (traits & rule.Slight) != 0 ? 1 : 0;
        // Owner clarification: "single play" means total damage across hits,
        // ranked highest first, not a threshold on one hit. X remains higher.
        var damage = rule.RankDamage && tier == 1 ? CardProfile.Of(card).Damage : 0;
        return new(tier, damage, $"tier:{tier},traits:{traits}" + (damage > 0 ? $",play-damage:{damage:F1}" : ""));
    }

    internal static Trait Traits(CardModel card)
    {
        var traits = Trait.None;
        if (card.EnergyCost.CostsX) traits |= Trait.X;
        else traits |= card.EnergyCost.GetWithModifiers(CostModifiers.Local) switch
        { 0 => Trait.Zero, 1 => Trait.One, 2 => Trait.Two, 3 => Trait.Three, >= 4 => Trait.FourPlus, _ => Trait.None };
        if (card.Keywords.Contains(CardKeyword.Exhaust)) traits |= Trait.Exhaust;
        if (card.Type == CardType.Power) traits |= Trait.Power;
        if (card.Keywords.Contains(CardKeyword.Innate)) traits |= Trait.Innate;
        if (card.IsUpgraded) traits |= Trait.Upgraded;
        if (card.GetType().Name == "Bolas") traits |= Trait.Bolas;
        // Native self-auto-play hooks, plus Replay already granted to the card.
        // Returning to hand (Bolas/ThrummingHatchet) is not itself a replay.
        if (card.GetType().Name is "Bombardment" or "HowlFromBeyond" || card.GetEnchantedReplayCount() > 0)
            traits |= Trait.SelfReplay;
        var facts = CardProfile.Of(card);
        if (card.Type == CardType.Attack && facts.Damage > 0)
        {
            traits |= Trait.Damage;
            traits |= facts.Hits switch { 2 => Trait.TwoHits, 3 => Trait.ThreeHits, >= 4 => Trait.FourPlusHits, _ => Trait.None };
        }
        if ((traits & Trait.Exhaust) != 0 && CardProfile.Upgraded(card) is { } upgraded
            && !upgraded.Card.Keywords.Contains(CardKeyword.Exhaust)) traits |= Trait.UpgradeRemovesExhaust;
        return traits;
    }

    internal static double OriginalScore(Player player, CardModel card, EnchantmentModel enchantment, decimal amount)
        => enchantment.GetType().Name is "Instinct" or "Imbued" or "Clone"
            ? AncientOptionValue.EnchantmentDelta(player, card, enchantment, amount)
            : BotBrain.ScoreDeckCard(card);

    internal static Plan Choose(Player player, IEnumerable<CardModel> cards, EnchantmentModel enchantment,
        decimal amount, int min, int max, bool allowForcedFallback = true)
    {
        var ranked = cards.Distinct().Where(enchantment.CanEnchant)
            .Select(card => new Candidate(card, Classify(card, enchantment), OriginalScore(player, card, enchantment, amount)))
            .OrderByDescending(c => c.Preference.Tier).ThenByDescending(c => c.Preference.DamageOrder)
            .ThenByDescending(c => c.Score).ThenBy(c => c.Card.Id.Entry, StringComparer.Ordinal).ToArray();
        var cap = Math.Clamp(max, 0, ranked.Length);
        var floor = Math.Clamp(min, 0, cap);
        var selected = ranked.Where(c => c.Preference.Tier >= 0).Take(cap).Select(c => c.Card).ToList();
        var fallback = allowForcedFallback && selected.Count < floor;
        if (fallback)
            selected.AddRange(ranked.Where(c => c.Preference.Tier < 0).OrderByDescending(c => c.Score)
                .ThenBy(c => c.Card.Id.Entry, StringComparer.Ordinal).Take(floor - selected.Count).Select(c => c.Card));
        return new(selected, ranked, fallback);
    }

    internal static IReadOnlyList<CardModel> Select(Player player, IEnumerable<CardModel> cards,
        EnchantmentModel enchantment, decimal amount, int min, int max)
    {
        var plan = Choose(player, cards, enchantment, amount, min, max);
        BuildTrace.LogDeckEdit(player, "enchant:" + enchantment.GetType().Name + (plan.ForcedFallback ? ":forced-fallback" : ""),
            plan.Cards, plan.Ranked.Select(c => (c.Card, c.Score, c.Preference.Reason)).ToArray());
        return plan.Cards;
    }
}
