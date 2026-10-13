using CoopBots.Building;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CoopBots;

/// <summary>
/// Values the complete native ancient OPTION, including costs and deferred
/// effects. This is deliberately separate from chest/shop relic allocation.
/// v0.111.0 ancient options obtain a configured relic whose AfterObtained/hooks
/// implement the option; option.Relic is the payload, hover tips are not grants.
/// Upstream CombatSolver does not choose out-of-combat events. Owner request
/// 2026-10-01: price each complete native option and audit the old grant scorer.
/// Native effects define the targets and costs; future encounters, rewards and
/// combat utility are bounded estimates in the existing event/deck-value unit.
/// </summary>
internal static partial class AncientOptionValue
{
    internal sealed record Estimate(double Value, string Detail, bool Modeled = true);
    internal sealed record Ranked(int Index, string Option, Estimate Estimate);

    internal static IReadOnlyList<Ranked> Rank(Player player, AncientEventModel model)
    {
        using var evaluation = DeckStrength.BeginEvaluation();
        var context = new Context(player);
        return model.CurrentOptions.Select((o, i) => (o, i))
            .Where(x => !x.o.IsLocked && !x.o.WasChosen && !EnchantmentEventPolicy.ShouldAvoid(player, model, x.o))
            .Select(x => new Ranked(x.i, x.o.TextKey.Split('.').Last(), Evaluate(context, model, x.o)))
            .OrderByDescending(x => x.Estimate.Value).ThenBy(x => x.Option, StringComparer.Ordinal).ToArray();
    }

    internal static Estimate Evaluate(Player player, AncientEventModel model, EventOption option)
        => Evaluate(new Context(player), model, option);

    private static Estimate Evaluate(Context c, AncientEventModel model, EventOption option)
    {
        if (option.IsProceed) return new(0, "proceed:no-additional-effect");
        if (option.WillKillPlayer?.Invoke(c.Player) == true) return new(-10000, "native-lethal-option");
        if (option.Relic is not { } relic)
        {
            // Custom-run Neow modifiers are mandatory sequential pages, not a
            // choice between blessings. Do not interpret their hover tips as gifts.
            if (model.CurrentOptions.Count == 1) return new(0, "mandatory-modifier-page");
            return new(-1000, "unmodeled-ancient-option:" + option.TextKey, false);
        }
        if (!Catalog.TryGetValue(model.GetType().Name, out var ids) || !ids.Contains(relic.GetType().Name))
            return new(-1000, "unmodeled-ancient-option:" + option.TextKey, false);
        return Price(c, relic);
    }

    internal static string Explain(Player p, AncientEventModel model)
        => "ancient-options " + string.Join(" | ", Rank(p, model).Select(x =>
            $"{x.Option}={x.Estimate.Value:F2}[{x.Estimate.Detail}]"));

    internal sealed record HugPackage(IReadOnlyList<CardModel> Targets, double Removals, double Soot)
    { internal double Total => Removals + Soot; }

    internal static HugPackage BiiigHugPackage(Player player, int count)
    {
        using var evaluation = DeckStrength.BeginEvaluation();
        var deck = player.Deck.Cards.ToArray();
        var currentSoot = ShuffleStatusContext.Active(player);
        RemovalPlan.Plan plan;
        // The acquired relic exists when the actual removal screen is shown.
        // Rank targets with the same shuffle cost that screen will see, while
        // retaining the shared basic/curse removal priority.
        using (ShuffleStatusContext.Use(player, true))
            plan = RemovalPlan.Choose(player, deck.Where(c => c.IsRemovable).ToArray(), count, deck);
        if (ConstructionDecision.AwaitingValues) return new([], 0, 0);
        var targets = plan.Steps.Select(s => s.Card).ToArray();
        var after = deck.Except(targets).ToArray();
        var beforeValue = DeckStrength.WithShuffleSoot(deck, player, currentSoot).Total;
        var removalValue = DeckStrength.WithShuffleSoot(after, player, currentSoot).Total;
        var completeValue = DeckStrength.WithShuffleSoot(after, player, true).Total;
        // These are differences of one potential, not four isolated priors.
        // In particular the Soot cost uses the deck AFTER all four removals.
        return new(targets, removalValue - beforeValue, completeValue - removalValue);
    }

    internal static double RemainingRooms(Player player, MapPointType type, double futurePerAct)
    {
        var state = player.RunState;
        var future = Math.Max(0, state.Acts.Count - state.CurrentActIndex - 1) * futurePerAct;
        var from = state.CurrentMapPoint;
        if (from is null) return future + futurePerAct;
        var memo = new Dictionary<MapPoint,double>();
        double Walk(MapPoint p)
        {
            if (memo.TryGetValue(p, out var known)) return known;
            if (memo.Count > 256) return 0;
            memo[p] = 0;
            return memo[p] = (p.PointType == type ? 1 : 0) + p.Children.Select(Walk).DefaultIfEmpty(0).Average();
        }
        return future + from.Children.Select(Walk).DefaultIfEmpty(0).Average();
    }

    private sealed class Parts
    {
        private readonly List<(string Label, double Value)> values = [];
        internal Parts Add(string label, double value) { values.Add((label, value)); return this; }
        internal Estimate Finish() => new(values.Sum(v => v.Value), string.Join(",", values.Select(v => $"{v.Label}:{v.Value:F2}")));
    }

    private sealed class Context
    {
        internal readonly Player Player;
        internal readonly CardModel[] Deck;
        internal readonly CardProfile.Facts[] Facts;
        internal readonly int Energy;
        internal readonly double Fights, Elites, Camps, Shops, Horizon;
        internal Context(Player player)
        {
            Player = player; Deck = player.Deck.Cards.ToArray(); Energy = BuildValue.StableEnergy(player);
            Facts = Deck.Select(c => CardProfile.Of(c, Energy)).ToArray();
            Fights = Math.Max(0, RunDepth.RemainingCombats(player));
            Elites = Rooms(MapPointType.Elite, 1.5); Camps = Rooms(MapPointType.RestSite, 2); Shops = Rooms(MapPointType.Shop, 1);
            Horizon = Math.Clamp(Fights / 8, .1, 1.5);
        }
        internal bool Has(string name) => Player.Relics.Any(r => r.GetType().Name == name);
        internal double Wound => 1 - (double)Player.Creature.CurrentHp / Math.Max(1, Player.Creature.MaxHp);
        internal double Count => Math.Max(1, Deck.Length);
        internal double AttackPlays => Math.Min(4, 5 * Facts.Count(f => f.Damage > 0) / Count);
        internal double DrawCards => Facts.Sum(f => f.Draw) / Count * 5;
        internal double Plays => Math.Min(12, Energy / Math.Max(.35, Facts.Where(f => !f.Unplayable).Select(f => (double)f.Cost).DefaultIfEmpty(1).Average())
            + Facts.Count(f => f.Roles.Contains("produces-shiv")) * 8 / Count);
        internal double Combat(double value) => value * Horizon;
        internal double EnergyGain(double amount, double turns = 4)
        {
            var demand = Facts.Where(f => !f.Unplayable).Sum(f => Math.Max(0, f.Cost)) / Count * (5 + Math.Min(2, DrawCards));
            return Combat(Math.Min(amount, Math.Max(0, demand - Energy)) * 6 * turns);
        }
        internal double Draw(double amount, double turns = 4)
            => Combat(amount * turns * 2.5 * Math.Clamp(Energy / Math.Max(1, Facts.Where(f => !f.Unplayable).Sum(f => f.Cost) / Count * 5), .3, 1.25));
        internal double Money(double amount)
        {
            if (amount > 0 && Has("Ectoplasm")) return 0;
            // Coins beyond the remaining spending capacity have diminishing value.
            var capacity = Math.Max(0, Shops * 350 - Player.Gold);
            var useful = amount > 0 ? Math.Min(amount, capacity) + Math.Max(0, amount - capacity) * .1 : amount;
            return Math.Sign(useful) * EventTrade.Gold(Math.Abs(useful));
        }
        internal double Hp(double amount)
            => amount >= 0 ? amount * 2 : amount - Math.Max(0, Player.Creature.CurrentHp - (Player.Creature.MaxHp + amount));
        internal double Damage(double amount) => amount >= Player.Creature.CurrentHp ? -10000 : -amount * (1 + Wound);
        internal double Curse(int count = 1) => count * EventTrade.Curse(Player);
        internal double Potions(int count, int addedSlots = 0)
        {
            if (Has("Sozu")) return 0;
            var room = Math.Max(0, Player.MaxPotionCount + addedSlots - Player.Potions.Count());
            return Math.Min(count, room) * 10;
        }
        internal double PotionSlots(int added) => Has("Sozu") ? 0 : 3 * added * Horizon;
        internal double Relics(int count, RelicModel? source = null, bool wax = false)
        {
            var predicted = source is null ? [] : OutOfCombatPredictions.RelicPickup(Player, source).Relics;
            var sum = 0d;
            for (var i = 0; i < count; i++)
            {
                // An unknown future relic is an explicit expectation, not zero.
                var v = i < predicted.Count ? EventTrade.Relic(Player, predicted[i]) : 0;
                if (i >= predicted.Count || !HumanCoopAdvisor.RelicPriced(predicted[i], Player)) v = EventTrade.UnnamedRelic;
                sum += v * (wax ? Math.Min(1, (i + 1) * 3 / Math.Max(1, Fights)) : 1);
            }
            return sum;
        }
        internal CardModel Copy(CardModel card)
        { var clone = card.IsMutable ? (CardModel)card.MutableClone() : card.ToMutable(); if (clone.Owner is null) clone.Owner = Player; return clone; }
        internal CardModel Card(string name, bool upgrade = false)
        {
            var result = Copy(ModelDb.AllCards.First(c => c.GetType().Name == name));
            if (upgrade && result.IsUpgradable) { result.UpgradeInternal(); result.FinalizeUpgradeInternal(); }
            return result;
        }
        internal double Delta(IEnumerable<CardModel> deck) => BuildValue.Deck(deck.ToArray(), Player) - BuildValue.Deck(Deck, Player);
        internal double Added(params CardModel[] cards) => Delta(Deck.Concat(cards.Select(Copy)));
        internal double Add(string name, int count = 1, bool upgrade = false)
            => Added(Enumerable.Range(0, count).Select(_ => Card(name, upgrade)).ToArray());
        internal double Removal(int count)
        {
            var plan = RemovalPlan.Choose(Player, Deck.Where(c => c.IsRemovable).ToArray(), count);
            return plan.Steps.Sum(s => s.Value);
        }
        internal (CardModel[] Targets, double Value) UpgradePlan(int count, bool includePlainBasics = false)
        {
            var remaining = (includePlainBasics ? Deck.Where(c => c.IsUpgradable)
                : BuildValue.EligibleUpgrades(Deck)).ToList();
            var after = Deck.ToList(); var selected = new List<CardModel>();
            while (selected.Count < count && remaining.Count > 0)
            {
                var best = BuildValue.RankUpgrades(remaining.Select(c =>
                    (Card: c, Value: BuildValue.UpgradeForDecision(c, Player, after)))).First().Card;
                if (ConstructionDecision.AwaitingValues) break;
                selected.Add(best); remaining.Remove(best);
                after[after.IndexOf(best)] = CardProfile.Upgraded(best, Energy)!.Value.Card;
            }
            return (selected.ToArray(), Delta(after));
        }
        internal double RandomUpgrades(double count)
        {
            var candidates = Deck.Where(c => c.IsUpgradable).ToArray();
            return candidates.Length == 0 ? 0 : candidates.Average(c => BuildValue.UpgradeForDecision(c, Player).Total) * Math.Min(count, candidates.Length);
        }
        internal CardModel[] Pool(CardRarity? rarity = null, bool colorless = false)
        {
            var pool = colorless ? ModelDb.AllCards.Where(c => c.Pool.IsColorless)
                : Player.Character.CardPool.GetUnlockedCards(Player.UnlockState, Player.RunState.CardMultiplayerConstraint);
            return pool.Where(c => (rarity is null ? c.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare : c.Rarity == rarity)
                && (rarity == CardRarity.Ancient || c.CanBeGeneratedByModifiers)).OrderBy(c => c.Id.Entry, StringComparer.Ordinal).ToArray();
        }
        // Stratified, deterministic expectation when a native preview is unavailable.
        // Never draws from a live RNG or registers preview cards in the run.
        private static T[] Sample<T>(IReadOnlyList<T> pool, int n = 8) => pool.Count <= n ? pool.ToArray()
            : Enumerable.Range(0, n).Select(i => pool[i * pool.Count / n]).ToArray();
        internal double Offer(RelicModel relic, int options = 3, CardRarity? rarity = null, bool colorless = false, int choices = 1,
            bool otherCharacters = false, bool multiplayer = false)
        {
            var effect = OutOfCombatPredictions.RelicPickup(Player, relic);
            if (effect.Bundles.Count > 0)
                return effect.Bundles.Sum(b => Math.Max(0, b.Cards.Select(card => Added(card)).DefaultIfEmpty(0).Max()));
            if (effect.Cards.Count > 0) return Math.Max(0, effect.Cards.Select(card => Added(card)).DefaultIfEmpty(0).Max());
            IEnumerable<CardModel> available = otherCharacters
                ? ModelDb.AllCharacters.Where(ch => ch.Id != Player.Character.Id).SelectMany(ch => ch.CardPool.GetUnlockedCards(Player.UnlockState, Player.RunState.CardMultiplayerConstraint))
                    .Where(card => card.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare)
                : Pool(rarity, colorless);
            if (multiplayer) available = Pool().Concat(Pool(colorless: true)).Where(card => card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly);
            var candidates = Sample(available.OrderBy(card => card.Id.Entry, StringComparer.Ordinal).ToArray());
            if (candidates.Length == 0) return 0;
            var sorted = candidates.Select(card => Math.Max(0, Added(card))).Order().ToArray();
            // Expected maximum of k uniform draws from the sampled value CDF.
            return choices * sorted.Select((v, i) => v * (Math.Pow((i + 1d) / sorted.Length, options) - Math.Pow((double)i / sorted.Length, options))).Sum();
        }
        internal double ForcedRare(RelicModel relic)
        {
            var cards = OutOfCombatPredictions.RelicPickup(Player, relic).Cards;
            return cards.Count > 0 ? Added(cards[0]) : Sample(Pool(CardRarity.Rare)).Select(c => Added(c)).DefaultIfEmpty(0).Average();
        }
        internal double Transform(int count, bool upgrade = false, IEnumerable<CardModel>? forced = null,
            MegaCrit.Sts2.Core.Random.Rng? rng = null)
        {
            return TransformPlan.Choose(Player, forced ?? Deck.Where(c => c.IsTransformable), count,
                rng: rng ?? Player.RunState.Rng.Niche, before: Deck, upgrade: upgrade, forced: forced is not null).Value;
        }

        internal double Enchant<T>(int count, decimal amount = 1, bool random = false, Func<CardModel, bool>? filter = null) where T : EnchantmentModel
        {
            var enchantment = ModelDb.Enchantment<T>();
            var candidates = Deck.Where(c => enchantment.CanEnchant(c) && (filter?.Invoke(c) ?? true)).ToArray();
            if (candidates.Length == 0) return 0;
            if (random) return candidates.Average(card => EnchantmentDelta(Player, card, enchantment, amount)) * Math.Min(count, candidates.Length);
            var selected = EnchantmentPriorities.Choose(Player, candidates, enchantment, amount, 0, count, allowForcedFallback: false);
            return selected.Cards.Sum(card => EnchantmentDelta(Player, card, enchantment, amount));
        }
        internal double StarterUpgrade()
        {
            if (Has("BurningBlood")) return Combat(6 * Math.Min(Fights, 8));
            if (Has("RingOfTheSnake")) return Draw(2, 2); // turns 2 and 3; turn 1 was already +2
            if (Has("DivineRight")) return Combat(4 * 3);
            if (Has("BoundPhylactery")) return Combat(5 + 4); // +5 opening summon, +1/turn
            if (Has("CrackedCore")) return Combat(2 * 3 * 4 + 3 * 4); // +2 lightning and +1 passive/evoke
            return 0;
        }
        internal double Transcend()
        {
            var replacements = new Dictionary<string,string> { ["Bash"]="Break", ["Neutralize"]="Suppress", ["Unleash"]="Protector", ["FallingStar"]="MeteorShower", ["Dualcast"]="Quadcast" };
            var old = Deck.FirstOrDefault(c => replacements.ContainsKey(c.GetType().Name));
            if (old is null) return 0;
            var replacement = Card(replacements[old.GetType().Name], old.IsUpgraded);
            if (old.Enchantment is { } enchantment)
            { var e = (EnchantmentModel)enchantment.MutableClone(); replacement.EnchantInternal(e, e.Amount); e.ModifyCard(); }
            return Delta(Deck.Select(c => ReferenceEquals(c, old) ? replacement : c));
        }
        internal double Rooms(MapPointType type, double futurePerAct)
            => RemainingRooms(Player, type, futurePerAct);
    }

    internal static double EnchantmentDelta(Player player, CardModel card, EnchantmentModel enchantment, decimal amount)
    {
        if (!enchantment.CanEnchant(card)) return 0;
        var clone = (CardModel)card.MutableClone(); var e = enchantment.ToMutable();
        clone.EnchantInternal(e, amount); e.ModifyCard();
        var deck = player.Deck.Cards.ToArray();
        var delta = BuildValue.Deck(deck.Select(c => ReferenceEquals(c, card) ? clone : c).ToArray(), player) - BuildValue.Deck(deck, player);
        // These native hooks do not change DynamicVars read by the deck scorer.
        var facts = CardProfile.Of(card, BuildValue.StableEnergy(player));
        var horizon = Math.Clamp(RunDepth.RemainingCombats(player) / 8, .1, 1.5);
        return delta + (enchantment.GetType().Name switch
        {
            "Swift" => (double)amount * 4 * horizon,
            "Imbued" => Math.Max(0, facts.Cost * 6 + facts.Block * .3 + facts.Draw * 3 - facts.HpLoss * 2) * horizon,
            "Goopy" => Math.Min(8, RunDepth.RemainingCombats(player) * .4) * horizon,
            "Clone" => Math.Max(0, BuildValue.AddForDecision(card, player).Total), // native camp duplicates each marked card once, independent of Amount
            _ => 0,
        });
    }
}
