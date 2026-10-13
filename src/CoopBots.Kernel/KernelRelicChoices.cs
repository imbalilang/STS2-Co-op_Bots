using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    public string LastHandChoiceComparison { get; private set; } = "not-applicable";
    // Same policy for the native selector and simulated turn-start hooks. It
    // prices the resulting affordable hand, not a permanent deck acquisition.
    public IReadOnlyList<CardModel> SelectRelicCards(Player player, IReadOnlyList<CardModel> cards,
        string effect, int minimum, int maximum)
        => SelectRelicCards(player, cards, effect, minimum, maximum, "");

    public IReadOnlyList<CardModel> SelectRelicCards(Player player, IReadOnlyList<CardModel> cards,
        string effect, int minimum, int maximum, string source)
    {
        if (cards.Count == 0) return [];
        var kind = Enum.Parse<PlanChoiceEffect>(effect);
        var options = cards.Select(c => simulator.State.FindCard(c) ?? new PredictedCard(c)).ToArray();
        var pile = kind == PlanChoiceEffect.GenerateToHand ? PileType.None : PileType.Hand;
        var spec = new CardChoiceSpec(kind, pile, Math.Min(minimum, cards.Count), Math.Min(maximum, cards.Count),
            options, options, 0);
        var choice = ChooseSettledHand(player, spec, source)
            ?? KernelRelicChoices.Choose(simulator, player, spec);
        return TurnStartChoiceSupport.ResolveTokens(choice, options, spec.MinCount, spec.MaxCount)
            .Select(c => c.Original).ToArray();
    }

    private PlanCardChoice? ChooseSettledHand(Player player, CardChoiceSpec spec, string source)
    {
        LastHandChoiceComparison = "legacy-effect-or-nested-choice";
        if (KernelSearchBudgetScope.IsStopped || KernelChoiceContinuation.Active || spec.SourcePile != PileType.Hand
            || spec.Effect is not (PlanChoiceEffect.Exhaust or PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw)
            || spec.IsImplicitAllSelection) return null;
        var choices = spec.Effect == PlanChoiceEffect.DiscardAndDraw
            ? KernelRelicChoices.ReplacementChoices(simulator, spec).ToArray()
            : KernelRelicChoices.OrderHandCleanupChoices(simulator, spec,
                KernelRelicChoices.CleanupChoices(spec, 256))
                .Take(KernelChoiceContinuation.MaximumChoices).ToArray();
        PlanCardChoice? best = null;
        var value = double.NegativeInfinity;
        var complete = 0;
        var steps = 0;
        using var guard = KernelChoiceContinuation.Enter();
        foreach (var choice in choices)
        {
            if (KernelSearchBudgetScope.IsStopped) break;
            var child = Fork();
            child.SampleChoiceDrawPool(player);
            if (!child.ApplyChoiceForAnalysis(player, spec, choice, source)) continue;
            var result = KernelChoiceContinuation.Evaluate(this, child, player, choices.Length);
            var score = result.Value;
            steps += result.Steps;
            complete += result.CompleteHands;
            if (score <= value) continue;
            value = score; best = choice;
        }
        LastHandChoiceComparison = $"native-hand source={source} candidates={choices.Length} complete={complete} steps={steps} "
            + (best is null ? "fallback=no-complete-native-hand" : "selected=complete-native-hand");
        return best;
    }
}

// OUR bounded hand policy, used only for choices whose native effect is known.
// Enumerate legal cardinalities including zero; expected replacements are
// sampled from the remaining pool, without looking at the hidden top card.
internal static class KernelRelicChoices
{
    // A variable replacement menu must compare retaining everything and
    // replacing everything as well as partial replacements. Taking the best
    // ten subsets globally used to let one cardinality fill the whole queue.
    // Ten-card hands have eleven cardinalities; the aggregate native rollout
    // step budget is still KernelChoiceContinuation.MaximumSteps.
    internal static IReadOnlyList<PlanCardChoice> ReplacementChoices(
        CombatPredictionSimulator simulator, CardChoiceSpec spec)
    {
        var ordered = new List<PlanCardChoice>();
        var additional = new List<PlanCardChoice>();
        for (var count = spec.MinCount; count <= Math.Min(spec.MaxCount, spec.Options.Count); count++)
        {
            var exact = spec with { MinCount = count, MaxCount = count };
            var ranked = OrderHandCleanupChoices(simulator, exact,
                CardChoiceSupport.BuildChoices(exact, null, 256, 256)).ToArray();
            if (ranked.Length == 0) continue;
            ordered.Add(ranked[0]);
            additional.AddRange(ranked.Skip(1));
        }
        var room = Math.Max(0, 12 - ordered.Count);
        ordered.AddRange(OrderHandCleanupChoices(simulator, spec, additional).Take(room));
        return ordered;
    }

    // Existing junk option cost, now also used for a harmless Ethereal card
    // whose native cleanup was deferred by a discard. It is an HP-scale policy
    // estimate of a future wasted draw, not a measured native slot value.
    internal const double FutureJunkDrawCost = 8;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> noDrawHook = new();
    internal static bool IsHarmlessEtherealJunk(CardModel card)
        => card.Type is CardType.Curse or CardType.Status
            && card.Keywords.Contains(CardKeyword.Unplayable)
            && card.Keywords.Contains(CardKeyword.Ethereal)
            && !card.HasTurnEndInHandEffect
            && noDrawHook.GetOrAdd(card.GetType(), type =>
                type.GetMethod(nameof(AbstractModel.AfterCardDrawn))?.DeclaringType == typeof(AbstractModel));

    internal static IReadOnlyList<PlanCardChoice> CleanupChoices(CardChoiceSpec spec, int limit)
    {
        var representatives = CardChoiceSupport.BuildChoices(spec, null, limit, limit);
        if (spec.SourcePile != PileType.Hand || spec.MinCount != 1 || spec.MaxCount != 1
            || spec.Effect is not (PlanChoiceEffect.Exhaust or PlanChoiceEffect.Discard)
            || spec.IsImplicitAllSelection) return representatives;
        // The vendor collapses equal semantic combinations. A bounded native
        // single-hand menu can instead retain every physical occurrence.
        // Preserve its old semantic representative order so default ordinals
        // remain stable; the extra occurrences still carry actual native tokens.
        var ranks = representatives.Select(c => c.Cards.Single().StateKey).Distinct()
            .Select((key, rank) => (key, rank)).ToDictionary(x => x.key, x => x.rank);
        int Occurrence(IReadOnlyList<PredictedCard> cards, PredictedCard selected)
            => cards.TakeWhile(c => !ReferenceEquals(c, selected)).Count(c => c.Preview.Id == selected.Preview.Id
                && c.Preview.CurrentUpgradeLevel == selected.Preview.CurrentUpgradeLevel);
        return spec.Options.Select(card => new PlanCardChoice(spec.Effect, spec.SourcePile,
            [new(card.Preview.Id.Entry, card.Preview.CurrentUpgradeLevel, CardChoiceSupport.ChoiceCardKey(card),
                Occurrence(spec.SourceCards, card), Occurrence(spec.Options, card), card.Preview.Id.Entry)],
            ContextId: spec.ContextId))
            .OrderBy(c => ranks.GetValueOrDefault(c.Cards.Single().StateKey, int.MaxValue))
            .ThenBy(c => c.Cards.Single().OptionOccurrence).Take(limit).ToArray();
    }

    internal static PlanCardChoice Choose(CombatPredictionSimulator simulator, Player player, CardChoiceSpec spec)
    {
        if (spec.Effect == PlanChoiceEffect.Discard && spec.SourcePile == PileType.Hand)
        {
            if (spec.IsImplicitAllSelection) return CardChoiceSupport.BuildAutomaticPolicyChoice(spec);
            // A native ten-card hand has at most C(10,5)=252 fixed-count
            // discard sets. Let the shared builder preserve physical token
            // occurrences, then rank its legal choices by the combat policy.
            return OrderHandCleanupChoices(simulator, spec,
                CardChoiceSupport.BuildChoices(spec, null, 256, 256)).First();
        }
        if (spec.Effect is not (PlanChoiceEffect.DiscardAndDraw or PlanChoiceEffect.Exhaust or PlanChoiceEffect.GenerateToHand))
            return CardChoiceSupport.BuildAutomaticPolicyChoice(spec);
        var state = simulator.State.GetPlayerCombatState(player);
        var hand = state.Hand.Cards.ToArray();
        var pool = state.DrawPile.Cards.Concat(state.DiscardPile.Cards)
            .OrderBy(CardChoiceSupport.ChoiceCardKey, StringComparer.Ordinal).ToArray();
        var options = CardChoiceSupport.BuildChoices(spec, null, 24, 24);
        var energy = state.Energy;
        // BeforeHandDraw (Toolbox) precedes energy/hand availability in some
        // native hooks. The upcoming turn still has its persistent supply.
        if (hand.Length == 0) energy = Math.Max(energy, player.MaxEnergy);
        using var isolation = SimulationNotificationIsolation.Enter();
        var cardValues = new Dictionary<(PredictedCard Card, int Energy), double>();
        var futureValues = new Dictionary<PredictedCard, double>();
        double CardValue(PredictedCard card, int available)
        {
            var key = (card, available);
            if (cardValues.TryGetValue(key, out var cached)) return cached;
            return cardValues[key] = ResolvedCardValue(simulator, player, card, available);
        }
        double FutureValue(PredictedCard card)
        {
            if (futureValues.TryGetValue(card, out var cached)) return cached;
            return futureValues[card] = FutureExhaustValue(simulator, player, card,
                Math.Max(energy, player.MaxEnergy));
        }
        double Score(PlanCardChoice choice)
        {
            var selected = TurnStartChoiceSupport.ResolveTokens(choice, spec.Options, spec.MinCount, spec.MaxCount);
            var retained = hand.Where(c => !selected.Contains(c)).ToArray();
            if (spec.Effect == PlanChoiceEffect.GenerateToHand)
                return HandValue(hand.Concat(selected).ToArray(), energy);
            if (spec.Effect == PlanChoiceEffect.Exhaust)
                return HandValue(retained, energy) - selected.Sum(FutureValue);
            if (selected.Count == 0 || pool.Length == 0) return HandValue(retained, energy);
            var value = 0d;
            for (var sample = 0; sample < 4; sample++)
            {
                var drawn = Enumerable.Range(0, Math.Min(selected.Count, pool.Length))
                    .Select(i => pool[(sample * pool.Length / 4 + i) % pool.Length]);
                value += HandValue(retained.Concat(drawn).ToArray(), energy) / 4;
            }
            // Retaining a harmless Ethereal status lets it exhaust naturally.
            return value - selected.Count(c => IsHarmlessEtherealJunk(c.Preview)) * 1.5;
        }
        return options.OrderByDescending(Score).ThenBy(c => c.Cards.Count).First();

        double HandValue(IReadOnlyList<PredictedCard> cards, int available)
        {
            var count = Math.Min(10, cards.Count);
            var values = new double[1 << count]; Array.Fill(values, double.NegativeInfinity); values[0] = 0;
            var energies = new double[1 << count]; energies[0] = available;
            var best = 0d;
            var hints = cards.Take(count).Select(c => {
                var model = c.Preview;
                var cost = c.GetEnergyCostWithModifiers(simulator, state);
                var resources = CardResourceTiming.Of(model, available);
                var playable = model.Type is not (CardType.Curse or CardType.Status)
                    && !model.Keywords.Contains(CardKeyword.Unplayable);
                return (Cost: Math.Max(0, cost), Energy: resources.EnergyNow,
                    X: model.EnergyCost.CostsX, Playable: playable);
            }).ToArray();
            for (var mask = 0; mask < values.Length; mask++)
            {
                if (double.IsNegativeInfinity(values[mask])) continue;
                best = Math.Max(best, values[mask]);
                for (var i = 0; i < count; i++)
                {
                    var h = hints[i]; var next = mask | 1 << i;
                    var cost = h.X ? energies[mask] : h.Cost;
                    if (next == mask || !h.Playable || cost > energies[mask]) continue;
                    var value = values[mask] + CardValue(cards[i], (int)energies[mask]);
                    var left = energies[mask] - cost + h.Energy;
                    if (value > values[next] || value == values[next] && left > energies[next])
                    { values[next] = value; energies[next] = left; }
                }
            }
            // Small retained value prevents ties from throwing away an engine;
            // junk occupying a draw is useful only as discard/exhaust fodder.
            return best + cards.Sum(FutureValue) * .1;
        }
    }

    // Shared with the native hand continuation. Exhaust loses an option for
    // the remainder of this combat; temporary unpayability is not its value.
    // Keep the existing estimate for that opportunity, while actual native
    // continuation supplies immediate draw, energy, HP cost and autoplay.
    internal static double FutureExhaustValue(CombatPredictionSimulator simulator, Player player,
        PredictedCard card, int available)
        => card.Preview.Type is CardType.Curse or CardType.Status
            ? (IsHarmlessEtherealJunk(card.Preview) ? 0 : -FutureJunkDrawCost)
            : ResolvedCardValue(simulator, player, card, available);

    private static double ResolvedCardValue(CombatPredictionSimulator simulator, Player player,
        PredictedCard card, int available)
    {
        var model = card.Preview;
        var printedDamage = model.DynamicVars.Values.OfType<DamageVar>().Sum(v => v.BaseValue);
        var value = CardChoiceSupport.CardValue(model);
        if (model.Type == CardType.Attack)
        {
            var hits = AttackHits(model, available);
            var amounts = simulator.State.HittableEnemies.Select(target =>
            {
                var damage = printedDamage;
                foreach (var variable in model.DynamicVars.Values.OfType<CalculatedDamageVar>())
                    if (CalculatedVarSpecRegistry.TryCalculate(variable, simulator, card, target, out var amount))
                        damage += amount;
                return Math.Max(0, Math.Floor((double)HookMirrors.ModifyDamage(simulator, target,
                    player.Creature, damage, ValueProp.Move, card, null))) * hits;
            }).ToArray();
            value += (model.TargetType == TargetType.AllEnemies ? amounts.Sum() : amounts.DefaultIfEmpty(0).Max())
                - (double)printedDamage;
        }
        return Math.Max(0, value);
    }

    // Card-action branches and automatic start-of-turn discards must use the
    // same combat ranking. Native Tools of the Trade calls this facade too;
    // separate live deck scores formerly discarded a different sixth card.
    internal static IReadOnlyList<PlanCardChoice> OrderHandCleanupChoices(
        CombatPredictionSimulator simulator, CardChoiceSpec spec, IEnumerable<PlanCardChoice> choices)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var attacks = combat.CurrentMonsterMoves().SelectMany(m => m.AttackHits).ToArray();
        var incomingDamage = attacks.Sum(hit => (double)hit.BaseDamage);
        var incomingStrength = combat.Enemies.Where(e => simulator.State.GetCreature(e).IsAlive)
            .Sum(e => (double)combat.GetAmount<StrengthPower>(e));
        var priorities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var card in spec.Options)
        {
            var key = CardChoiceSupport.ChoiceCardKey(card);
            if (!priorities.ContainsKey(key)) priorities[key] = CleanupPriority(simulator, combat, card,
                spec.Effect, attacks.Length, incomingDamage, incomingStrength);
        }
        return choices.OrderByDescending(choice => choice.Cards.Sum(token =>
            priorities.GetValueOrDefault(token.StateKey))).ToArray();
    }

    private static double CleanupPriority(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        PredictedCard selected, PlanChoiceEffect effect, int incomingHits, double incomingDamage, double incomingStrength)
    {
        var card = selected.Preview;
        if (card.Type is CardType.Status or CardType.Curse)
        {
            if (IsHarmlessEtherealJunk(card)) return -1;
            var damage = card.HasTurnEndInHandEffect && card.DynamicVars.TryGetValue("Damage", out var value)
                ? (double)value.BaseValue : 0;
            return 2 + damage;
        }
        // Exhaust loses an option for the rest of combat. Keep the existing
        // vendor removal order (including starter weights) for ordinary cards;
        // temporary unpayability must not put an expensive core before them.
        // OrderBy is stable, so equal cleanup priorities retain that order.
        // Junk and harmless-Ethereal policy above remains unchanged. Discard
        // still uses this turn's affordability and incoming damage below.
        if (effect == PlanChoiceEffect.Exhaust) return 0;
        if (!combat.CanPlayCard(simulator, selected)) return 1;
        double keep = 0;
        if (card.Type == CardType.Power) keep -= 2;
        if (card.GainsBlock && incomingDamage > 0) keep -= 1;
        if (card.DynamicVars.TryGetValue("StrengthLoss", out _)
            || string.Equals(card.Id.Entry, "PIERCING_WAIL", StringComparison.OrdinalIgnoreCase))
            keep -= incomingHits >= 2 || incomingStrength > 0 ? 5 : 2;
        if (card.DynamicVars.TryGetValue("PoisonPerTurn", out _)
            || string.Equals(card.Id.Entry, "NOXIOUS_FUMES", StringComparison.OrdinalIgnoreCase)) keep -= 2;
        return keep;
    }

    // Bounded choice hint, not a simulation of a new combat. Strength applies
    // to each hit and X attacks use the energy left at that point in the hand.
    private static double AttackHits(CardModel card, int energy)
    {
        double Named(string name) => Math.Max(1, card.DynamicVars.TryGetValue(name, out var v) ? (double)v.BaseValue : 1);
        return card.GetType().Name switch
        {
        "AstralPulse" or "DaggerSpray" or "Maul" or "Refract" or "RipAndTear" or "Thrash" or "TwinStrike" or "Uproar" => 2,
        "CelestialMight" or "Conflagration" or "Exterminate" or "FightMe" or "GunkUp" or "Peck" or "Ricochet"
            or "SevenStars" or "SovereignBlade" or "SwordBoomerang" => Named("Repeat"),
        "Eradicate" or "Skewer" or "Volley" or "Whirlwind" => Math.Max(0, energy),
            _ => Named("CalculatedHits")
        };
    }
}
