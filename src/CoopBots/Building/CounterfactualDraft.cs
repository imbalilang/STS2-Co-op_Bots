using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// Paired hand experiments for permanent additions. Optimize affordable subsets
/// of the sampled hand and cards unlocked by draw; compare before/after using
/// identical local sample keys. This is a bounded structural estimate, not the
/// combat simulator or a claim about win probability. No live RNG is consumed.
/// </summary>
internal static class CounterfactualDraft
{
    internal sealed record Result(double Delta, double MeanBefore, double MeanAfter,
        double TailBefore, double TailAfter, double DeadBefore, double DeadAfter)
    {
        // Share of the decision assigned to the modeled mechanisms, not a
        // probability of correctness. Missing mechanisms retain structural value.
        internal double ModelShare { get; init; } = 1;
        internal string Coverage { get; init; } = "hand";
    }
    private sealed record Card(CardProfile.Facts Facts, int Draw, bool Known, bool Exhaust,
        double OtherDrawValue = 0, double DamagePerVulnerable = 0);
    private readonly record struct Score(double Value, bool Dead);
    internal static double OpeningEncounter(IReadOnlyList<CardModel> deck, int energy, double health, double incoming)
        => Opening(deck.Select(c => new ConstructionHand.Slot(c,CardProfile.Of(c,energy),CardProfile.Of(c,energy).Unplayable)).ToArray(),energy,1,incoming,health);
    private static readonly (int Enemies, double Incoming)[] Scenarios = [(1, 8), (1, 16), (1, 24), (3, 12)];

    internal static double Strength(IReadOnlyList<CardModel> deck, int energy)
        => ConstructionJobs.Value(() => PrepareStrength(deck, energy));

    private static Func<double> PrepareStrength(IReadOnlyList<CardModel> deck, int energy)
    {
        var finaleAvailability = GrandFinaleAvailability.Estimate(deck, energy);
        var cards = deck.Select(c =>
        {
            var f = CardProfile.Of(c, energy);
            // Conditional printed damage must obey the same availability as
            // the cycle estimate; an empty-draw-pile attack is not free damage.
            if (c is MegaCrit.Sts2.Core.Models.Cards.GrandFinale)
                f = f with { Damage = f.Damage * finaleAvailability };
            return new ConstructionHand.Slot(c,f,f.Unplayable);
        }).ToArray();
        var copies = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = deck.Select(c =>
        {
            var copy = copies.GetValueOrDefault(c.Id.Entry);
            copies[c.Id.Entry] = copy + 1;
            uint hash = 2166136261;
            foreach (var ch in c.Id.Entry) hash = unchecked((hash ^ ch) * 16777619);
            return unchecked((int)(hash ^ ((uint)copy * 0x9E3779B9u)));
        }).ToArray();
        return () => {
        var values = new List<double>();
        for (var sample = 0; sample < ConstructionBudget.Current.Samples; sample++)
        {
            var ordered = cards.Select((c, i) => (Card: c, Key: Key(keys[i], sample)))
                .OrderByDescending(c => c.Card.Model?.Keywords.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Innate) == true).ThenBy(c => c.Key).Select(c => c.Card).ToArray();
            foreach (var scenario in Scenarios)
                values.Add(Opening(ordered, energy, scenario.Enemies, scenario.Incoming,
                    scenario.Incoming <= 8 ? 18 : scenario.Incoming <= 16 ? 42 * scenario.Enemies : 100));
        }
        // Hand returns a discounted two-turn mean. Convert it to ONE opening
        // encounter before mixing with the six-turn cycle; multiplying that
        // mean by six would score several fresh targets for one large attack.
        return (1d / 6) * (.7 * values.Average() + .3 * values.Order().Take(values.Count / 4).Average());
        };
    }

    private static double Opening(ConstructionHand.Slot[] ordered,int energy,int enemies,double incoming,double health)
    {
        var first=ConstructionHand.Solve(ordered,5,new(energy,incoming,health,enemies));
        if(first.Damage>=health)return first.Utility;
        var remaining=ordered.Skip(first.Drawn).Concat(ordered.Take(first.Drawn)
            .Where((c,i)=>(first.Exhausted&(1<<i))==0 && !((first.Played&(1<<i))!=0 && c.Facts?.IsPower==true))).ToArray();
        var second=ConstructionHand.Solve(remaining,5+first.NextDraw,new(energy+first.NextEnergy,incoming,health-first.Damage,enemies,
            ExhaustBefore:first.ExhaustTriggers));
        return first.Utility+.7*second.Utility;
    }

    internal static Result? Compare(IReadOnlyList<CardModel> deck, CardModel candidate, int energy)
        => CompareChange(deck, candidate, null, energy);

    internal static Result? Remove(IReadOnlyList<CardModel> deck, CardModel card, int energy)
        => deck.Any(c => ReferenceEquals(c, card)) ? CompareChange(deck, null, card, energy) : null;

    // Convert per-hand change to one deck cycle before using the common shop
    // deck-point unit. Otherwise a larger deck would mechanically make all
    // purchases cheap while old removal floors still monopolised the budget.
    internal static double DeckPoints(Result result, int size)
        // The six-turn cycle already accounts for deck size, activation and
        // repeated draws. Multiplying by size again exaggerated both dilution
        // and upgrades. Hand-only experiments still need cycle normalization.
        => result.Delta * (result.Coverage.StartsWith("cycle:", StringComparison.Ordinal)
            ? 6 : Math.Max(1, size / 5.0) * 6);

    private static Result? CompareChange(IReadOnlyList<CardModel> deck, CardModel? candidate,
        CardModel? removed, int energy)
    {
        if (deck.Count < 8) return null;
        Card Profile(CardModel c)
        {
            var f = CardProfile.Of(c, energy);
            var known = f.Unplayable || f.Damage + f.Block + f.Draw + f.Energy + f.Poison + f.Doom
                + f.Vulnerable + f.Weak + f.StrengthDown + f.Scaling
                + f.NextTurnEnergy + f.NextTurnDraw + f.NextTurnDrawForTeam > 0;
            // This solver does not carry powers, triggered engines, temporary
            // buffs or attack replays. Nonzero printed damage is not coverage.
            known &= !f.IsPower && !f.Effects.NeedsLifecycle && f.Scaling == 0
                && c is not MegaCrit.Sts2.Core.Models.Cards.GrandFinale;
            return new Card(f, (int)Math.Clamp(CardProfile.NetDraw(c, f), 0, 4), known,
                c.Keywords.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Exhaust));
        }
        var offered = candidate is null ? null : Profile(candidate);
        if (offered is { Known: false }) return null;
        var held = deck.Select(Profile).ToArray();
        // Missing effects in HELD cards corrupt the counterfactual too: treating
        // Demon Form as a blank card made removing it appear to improve a deck.
        if (held.Any(c => !c.Known)) return null;
        var owner = (candidate ?? removed ?? deck[0]).Owner;
        var teamDraw = new Dictionary<int, double>();
        Card WithTeamDraw(Card card)
        {
            if (!card.Facts.DrawForTeam || owner is null) return card;
            var extra = (int)card.Facts.NextTurnDrawForTeam;
            if (!teamDraw.TryGetValue(extra, out var payoff))
            {
                foreach (var ally in owner.RunState.Players.Where(p => p != owner && p.Creature.IsAlive))
                {
                    var allyCards = ally.Deck.Cards.Select(Profile).ToArray();
                    if (allyCards.Length == 0 || allyCards.Count(c => c.Known) < allyCards.Length * .7) continue;
                    for (var sample = 0; sample < ConstructionBudget.Current.Samples; sample++)
                    {
                        var order = allyCards.Select((c, i) => (Card: c, Key: Key(i, sample)))
                            .OrderBy(c => c.Key).Select(c => c.Card).ToArray();
                        foreach (var scenario in Scenarios)
                            payoff += (Hand(order, ally.MaxEnergy, scenario.Enemies, scenario.Incoming,
                                Math.Min(9, 5 + extra), false).Value
                                - Hand(order, ally.MaxEnergy, scenario.Enemies, scenario.Incoming, 5, false).Value) / 64;
                    }
                }
                teamDraw[extra] = payoff;
            }
            return card with { OtherDrawValue = Math.Max(0, payoff) };
        }
        held = held.Select(WithTeamDraw).ToArray();
        if (offered is not null) offered = WithTeamDraw(offered);
        var before = new List<Score>(); var after = new List<Score>();
        for (var sample = 0; sample < ConstructionBudget.Current.Samples; sample++)
        {
            var ordered = held.Select((c, i) => (Card: c, Key: Key(i, sample))).ToArray();
            var original = ordered.OrderBy(x => x.Key).Select(x => x.Card).ToArray();
            var changed = ordered.Where((_, i) => !ReferenceEquals(deck[i], removed));
            if (offered is not null) changed = changed.Append((Card: offered, Key: Key(held.Length, sample)));
            var added = changed.OrderBy(x => x.Key).Select(x => x.Card).ToArray();
            foreach (var scenario in Scenarios)
            {
                before.Add(Hand(original, energy, scenario.Enemies, scenario.Incoming));
                after.Add(Hand(added, energy, scenario.Enemies, scenario.Incoming));
            }
        }
        double Mean(List<Score> values) => values.Average(v => v.Value);
        double Tail(List<Score> values) => values.OrderBy(v => v.Value).Take(values.Count / 4).Average(v => v.Value);
        var meanBefore = Mean(before); var meanAfter = Mean(after);
        var tailBefore = Tail(before); var tailAfter = Tail(after);
        return new Result(.7 * (meanAfter - meanBefore) + .3 * (tailAfter - tailBefore),
            meanBefore, meanAfter, tailBefore, tailAfter,
            before.Count(v => v.Dead) / (double)before.Count, after.Count(v => v.Dead) / (double)after.Count);
    }

    private static uint Key(int index, int sample)
    {
        var n = unchecked((uint)(index + 1) * 0x9E3779B9u ^ (uint)(sample + 1) * 0x85EBCA6Bu);
        n ^= n >> 16; n *= 0x7FEB352Du; n ^= n >> 15; n *= 0x846CA68Bu;
        return n ^ n >> 16;
    }

    private static Score Hand(Card[] orderedDeck, int energy, int enemies, double incoming,
        int openingDraw = 5, bool includeNextTurn = true, double health = double.PositiveInfinity)
    {
        var cards = orderedDeck.Take(Math.Min(9, openingDraw + 4)).ToArray();
        var size = 1 << cards.Length;
        var reachable = new bool[size]; reachable[0] = true;
        var energyLeft = new double[size]; energyLeft[0] = Math.Max(0, energy);
        var draws = new int[size]; var damage = new double[size]; var block = new double[size];
        var hpLoss = new double[size]; var strength = new double[size]; var dexterity = new double[size];
        var hits = new double[size]; var blocks = new int[size]; var vulnerable = new double[size];
        var vulnerablePayoff = new double[size];
        var weak = new double[size]; var strengthDown = new double[size]; var future = new double[size];
        var nextEnergy = new int[size]; var nextDraw = new int[size]; var exhausted = new int[size];
        var nextValues = new Dictionary<(int Consumed, int Energy, int Draw, int Exhausted, double Health), double>();
        var best = double.NegativeInfinity; var useful = false;
        for (var mask = 0; mask < size; mask++)
        {
            if (!reachable[mask]) continue;
            // Same-hand setup receives a half-order expectation, as in the
            // cycle estimate. Future stack carry and allied supply live there.
            var dealt = damage[mask] + strength[mask] * hits[mask] + .5 * vulnerablePayoff[mask] * vulnerable[mask];
            if (vulnerable[mask] > 0) dealt *= 1.25;
            var protectedDamage = incoming * (weak[mask] > 0 ? .25 : 0) + strengthDown[mask];
            var defence = Math.Min(incoming, block[mask] + dexterity[mask] * blocks[mask] + protectedDamage);
            var value = dealt + defence * 1.2 + future[mask] - hpLoss[mask] * 2;
            if (double.IsFinite(health))
                value = Math.Min(health, dealt) - 1.3 * (hpLoss[mask] + (dealt >= health ? 0 : Math.Max(0, incoming - defence)))
                    + (dealt >= health ? 0 : future[mask]);
            if (includeNextTurn && dealt < health)
            {
                var consumed = Math.Min(cards.Length, openingDraw + draws[mask]);
                var key = (consumed, energy + nextEnergy[mask], Math.Min(9, 5 + nextDraw[mask]), exhausted[mask], health - dealt);
                if (!nextValues.TryGetValue(key, out var nextValue))
                {
                    // The next hand comes after this hand's draw window. When
                    // the draw pile runs out, recycle the deterministic discard
                    // sample, excluding cards exhausted this turn. This is still
                    // a hand model, not a simulation of all shuffle/choice hooks.
                    var nextDeck = orderedDeck.Skip(consumed).Concat(orderedDeck.Take(consumed)
                        .Where((_, i) => (exhausted[mask] & (1 << i)) == 0)).ToArray();
                    nextValue = Hand(nextDeck, key.Item2, enemies, incoming, key.Item3, false, key.Item5).Value;
                    nextValues[key] = nextValue;
                }
                value += .7 * nextValue;
            }
            if (value > best) best = value;
            if (dealt >= 6 || defence >= incoming * .5) useful = true;
            for (var i = 0; i < Math.Min(cards.Length, openingDraw + draws[mask]); i++)
            {
                if ((mask & (1 << i)) != 0) continue;
                var c = cards[i]; var f = c.Facts;
                if (f.Unplayable || f.Cost > energyLeft[mask]) continue;
                var next = mask | (1 << i);
                if (reachable[next]) continue;
                reachable[next] = true;
                energyLeft[next] = energyLeft[mask] - Math.Max(0, f.Cost) + f.Energy;
                draws[next] = draws[mask] + c.Draw;
                damage[next] = damage[mask] + Math.Min(health / Math.Max(1, enemies), f.Damage) * (f.IsAoe ? enemies : 1) + (f.Poison + f.Doom) * 3;
                block[next] = block[mask] + f.Block; hpLoss[next] = hpLoss[mask] + f.HpLoss;
                strength[next] = strength[mask] + (f.Roles.Contains("strength") ? f.Scaling : 0)
                    + PayableHands.TemporaryStatShare*f.Effects.TemporaryStrength;
                dexterity[next] = dexterity[mask] + (f.Roles.Contains("dexterity") ? f.Scaling : 0)
                    + PayableHands.TemporaryStatShare*f.Effects.TemporaryDexterity;
                hits[next] = hits[mask] + (f.Damage > 0 ? Math.Max(1, f.Hits) : 0);
                blocks[next] = blocks[mask] + (f.Block > 0 ? 1 : 0);
                vulnerable[next] = vulnerable[mask] + f.Vulnerable;
                vulnerablePayoff[next] = vulnerablePayoff[mask] + c.DamagePerVulnerable;
                weak[next] = weak[mask] + f.Weak; strengthDown[next] = strengthDown[mask] + f.StrengthDown;
                future[next] = future[mask] + f.Scaling * 2 + (includeNextTurn ? .7 * c.OtherDrawValue : 0);
                nextEnergy[next] = nextEnergy[mask] + (int)f.NextTurnEnergy;
                nextDraw[next] = nextDraw[mask] + (int)f.NextTurnDraw;
                exhausted[next] = exhausted[mask] | (c.Exhaust ? 1 << i : 0);
            }
        }
        // Keep the original per-hand unit for reward/shop/removal consumers.
        return new Score(best / (includeNextTurn ? 1.7 : 1), !useful);
    }
}
