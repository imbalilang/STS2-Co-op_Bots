using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Upstream has no permanent-deck evaluator. Our construction policy uses one
// potential for all edits (owner request, 2026-09-30). Community preference is
// an explicit prior, never a win probability. No combat execution rules change.
internal static class DeckStrength
{
    [ThreadStatic] private static Dictionary<(Player, string), Estimate>? memo;
    // Cache only inside one read-only decision. Native state can change between
    // decisions, so no run-wide cache or live-state invalidation is required.
    internal static IDisposable BeginEvaluation()
    {
        var previous = memo;
        memo ??= new();
        return new EvaluationScope(previous, SustainedDeckValue.BeginEvaluation());
    }
    internal static IDisposable UseCache(Dictionary<(Player, string), Estimate> cache)
    {
        var previous = memo;
        memo = cache;
        return new EvaluationScope(previous, SustainedDeckValue.BeginEvaluation());
    }
    private sealed class EvaluationScope(Dictionary<(Player, string), Estimate>? previous, IDisposable allies) : IDisposable
    { public void Dispose() { allies.Dispose(); memo = previous; } }
    internal readonly record struct Estimate(double Total, double Combat, double Prior,
        double Unmodeled, double Size, double Coverage, double Boss, double Cycle, double Hands, double Commitment,
        double Growth = 0, double Robustness = 0, double Wither = 0, double Chains = 0, double RobustnessWeight = 0,
        double StartupPenalty = 0);

    // Upstream has no permanent-deck risk coefficient. Owner request 2026-09-30:
    // penalize committing most of one turn's stable energy. This explicit
    // heuristic supplements the bounded hand/cycle estimates, not combat legality.
    // Apply it to the deck potential once so addition/removal stay inverses and
    // native cost-reducing upgrades recover the same risk. Never scale a signed
    // edit delta: doing that would make a harmful expensive addition less harmful.
    // Recalibrated after finite encounter units and forecast growth changed
    // the score scale. Coarse development grid, constrained by the owner's
    // same-price shop anchor; not an independent validation or probability.
    internal const double CommitmentWeight = 31;
    internal const double CombatScale = 1.5;
    // Owner 2026-10-01: experiential baseline, one tier below the community
    // floor (-12). This is not fabricated observed pick-rate data. Upgrades
    // and enchantments retain their actual modeled effects; Bash/Survivor and
    // nonbasic cards with Strike/Defend tags do not inherit this prior.
    internal static double CommunityPrior(CardModel card)
        => card.Rarity == CardRarity.Basic
            && (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend)) ? -15
            : BakedCardElo.TryGet(card.Id.Entry, out var elo) && CommunityDraft.IsFinite(elo)
                ? Math.Clamp((elo - BakedCardElo.SkipElo) / 10, -12, 20)
                : card.Rarity == CardRarity.Basic ? -6 : 0;

    internal static double CommitmentLoad(CardModel card, int energy)
    {
        // X costs can spend the energy left after other plays; they do not
        // require committing an entire fresh turn. Fixed costs pay up front:
        // energy refunded by this card cannot finance its own initial payment.
        var f = CardProfile.Of(card, energy);
        if (f.Unplayable || card.EnergyCost.CostsX) return 0;
        return CommitmentLoadForCost(f.Cost, energy);
    }

    /// <summary>
    /// The same load for a cost that is not the card's CURRENT one, so a card
    /// whose cost is rewritten at runtime (Slither) can be priced against the
    /// distribution it will actually be played at. One definition, so the two
    /// cannot drift.
    /// </summary>
    internal static double CommitmentLoadForCost(int cost, int energy)
        => cost <= 0 ? 0 : Math.Pow(cost / (double)Math.Max(1, energy), 4);

    internal static Estimate Evaluate(IReadOnlyList<CardModel> cards, Player player)
    {
        using var evaluation = BeginEvaluation();
        // A deck is a multiset. Neither acquisition order nor current pile order
        // may change its construction score. Upgraded clones keep native facts.
        var energy = BuildValue.StableEnergy(player);
        // Hover tips and event predictions may supply canonical templates. Native
        // Owner reads require mutable cards; use owned preview copies, never mutate
        // the template or attach an unowned reward to the live player.
        var deck = cards.Select(c =>
            {
                if (c.IsMutable && c.Owner is not null) return c;
                var preview = c.IsMutable ? (CardModel)c.MutableClone() : c.ToMutable();
                preview.Owner = player;
                return preview;
            }).OrderBy(c => c.Id.Entry, StringComparer.Ordinal)
            .ThenBy(c => c.CurrentUpgradeLevel).ThenBy(c => c.Enchantment?.Id.Entry, StringComparer.Ordinal)
            .ThenBy(c => c.Enchantment?.Amount ?? 0).ThenBy(c => c.GetEnchantedReplayCount())
            .ThenBy(c => CardProfile.Of(c, energy).Cost)
            .ThenBy(c => string.Join('|', c.DynamicVars.OrderBy(v => v.Key).Select(v => $"{v.Key}:{v.Value.BaseValue}")), StringComparer.Ordinal)
            .ThenBy(c => string.Join('|', c.Keywords.Order()), StringComparer.Ordinal).ToArray();
        var memoKey = memo is null ? null : string.Join(';', deck.Select(c => $"{c.Id.Entry}/{c.CurrentUpgradeLevel}/{c.BaseStarCost}/{c.HasStarCostX}/{c.Enchantment?.Id.Entry}/{c.Enchantment?.Amount}/{c.GetEnchantedReplayCount()}/{CardProfile.Of(c, energy).Cost}/"
            + string.Join(',', c.DynamicVars.OrderBy(v => v.Key).Select(v => $"{v.Key}:{v.Value.BaseValue}")) + "/" + string.Join(',', c.Keywords.Order())));
        if (memoKey is not null) memoKey = (ShuffleStatusContext.Active(player) ? "shuffle-soot|" : "clean-shuffle|") + memoKey;
        if (memoKey is not null && memo!.TryGetValue((player, memoKey), out var cached)) return cached;
        if (ConstructionDecision.Current is { } decision)
            return decision.Request(deck, player, memoKey!);
        var resistance = DeckRobustness.Evaluate(deck,player,energy);
        var cycle = resistance.Clean;
        var hands = CounterfactualDraft.StrengthForOwner(deck, energy, player.Creature.CurrentHp, player.Creature.MaxHp);
        // Every deck uses the same mixture and the same six-turn unit. In
        // particular, neither size nor the edited card selects a different model.
        var combat = CombatScale * 6 * (.65 * cycle + .35 * hands);
        var robustness=CombatScale*6*.65*resistance.Adjustment;
        var growth = GrowthValue(deck, player, energy, combat+robustness);
        double prior = 0, unknown = 0;
        var copies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in deck)
        {
            var f = CardProfile.Of(card, energy);
            var copy = copies.GetValueOrDefault(card.Id.Entry);
            copies[card.Id.Entry] = copy + 1;
            var q = CommunityPrior(card);
            q += MultiplayerPickLift.Term(card.Id.Entry, player.RunState?.Players.Count ?? 1);
            // A positive community recommendation supports discovering the first
            // copy, not an endless supply of the same function. Further copies
            // earn their value through modeled outcomes and deck affinity.
            // Negative quality costs still apply per copy; this is one deck
            // potential shared by adding, removing and upgrading cards.
            prior += (q >= 0 ? (copy == 0 ? q : 0) : q)
                + .5 * CommunityDraft.Affinity(card, deck) / (1 + copy);
            if (f.Unplayable) { prior -= 25; continue; }
            // Unknown mechanics supply no invented ordinary-play dividend.
            // Printed effects, community prior, cost and draw occupancy already
            // have their own terms. Missing coverage is reported, not rewarded.
        }
        unknown *= 5d / Math.Max(5, deck.Length);
        // Absolute potential, not a candidate-specific size tax. The count here is the
        // EFFECTIVE size -- self-removing cards and draw both change it -- and the same
        // helper feeds RewardTakeBar, so the two can no longer disagree about size.
        var size = EffectiveDeckSize.TotalPenalty(deck, player, energy);
        var coverage = deck.Length == 0 ? 1 : deck.Count(c => BuildCoverage.Of(c) > 0 && !SustainedDeckValue.Unsupported(c)) / (double)deck.Length;
        // Robustness replaces both the old mixed pollution scenarios and the
        // separate visible-boss cleanup potential. Never charge either twice.
        var boss = 0d;
        var commitment = CommitmentWeight * (deck.Where(c => !c.Keywords.Contains(CardKeyword.Sly)).Sum(c => c is MegaCrit.Sts2.Core.Models.Cards.Midnight
            ? ConditionalCosts.Load(c,deck,energy) : CommitmentLoad(c, energy)) + SlyCommitment.Load(deck, energy));
        var supply = new ResourceStartup.Supply(deck, player);
        var missing = deck.Select(c => (Card: c, Resource: supply.Missing(c))).Where(c => c.Resource is not null).ToArray();
        var startupPenalty = missing.Length * ResourceStartup.Penalty;
        var result = new Estimate(combat + robustness + growth + prior + unknown - size + boss - commitment - startupPenalty, combat, prior, unknown, size, coverage, boss, 6 * cycle, 6 * hands, commitment, growth,
            robustness,CombatScale*6*.65*resistance.Wither,CombatScale*6*.65*resistance.Chains,resistance.Weight,
            startupPenalty);
        if (memoKey is not null) memo![(player, memoKey)] = result;
        return result;
    }

    internal static Estimate WithShuffleSoot(IReadOnlyList<CardModel> cards, Player player, bool soot)
    {
        using var preview = ShuffleStatusContext.Use(player, soot);
        return Evaluate(cards, player);
    }

    internal static BuildValue.Valuation Change(IReadOnlyList<CardModel> before,
        IReadOnlyList<CardModel> after, Player player)
    {
        var a = Evaluate(before, player);
        var b = Evaluate(after, player);
        var supply = new ResourceStartup.Supply(after, player);
        var missing = string.Join('|', after.Where(c => supply.Missing(c) is not null)
            .Select(c => c.Id.Entry + ":" + supply.Missing(c)));
        return new(b.Total - a.Total,
            $"deck-strength:{a.Total:F2}->{b.Total:F2},combat:{b.Combat-a.Combat:+0.00;-0.00;0.00},"
            + $"cycle:{b.Cycle-a.Cycle:+0.00;-0.00;0.00},hands:{b.Hands-a.Hands:+0.00;-0.00;0.00},"
            + $"prior:{b.Prior-a.Prior:+0.00;-0.00;0.00},unknown:{b.Unmodeled-a.Unmodeled:+0.00;-0.00;0.00},"
            + $"energy-risk:{a.Commitment-b.Commitment:+0.00;-0.00;0.00},"
            + $"resource-startup:{a.StartupPenalty-b.StartupPenalty:+0.00;-0.00;0.00},missing-startup:[{missing}],"
            + $"growth:{b.Growth-a.Growth:+0.00;-0.00;0.00},"
            + $"robustness:{b.Robustness-a.Robustness:+0.00;-0.00;0.00},lambda:{b.RobustnessWeight:F2},"
            + $"size:{b.Size-a.Size:+0.00;-0.00;0.00},coverage:{a.Coverage:F2}->{b.Coverage:F2}"
            + $",wither:{b.Wither-a.Wither:+0.00;-0.00;0.00},chains:{b.Chains-a.Chains:+0.00;-0.00;0.00}"
            + (BossPreparation.AgainstWither(player) ? ",boss:AEONGLASS" : ""))
            { RepeatablePerFight = b.Total - a.Total };
    }

    private static double GrowthValue(CardModel[] deck, Player player, int energy, double current)
    {
        var weight = DeckGrowth.Weight(player);
        if (weight <= 0) return 0;
        var forecasts = DeckGrowth.Forecasts(deck, player, energy);
        if (forecasts.Count == 0) return 0;
        var future = forecasts.Average(d => CombatScale * 6 * (.65 * DeckRobustness.Evaluate(d,player,energy).Total
            + .35 * CounterfactualDraft.Strength(d, energy)));
        return weight * (future - current);
    }
}
