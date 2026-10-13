using CoopBots.Building;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Map;
using System.Reflection;

namespace CoopBots;

/// <summary>
/// One unit system for events that TRADE resources, so two events cannot disagree
/// about what a hit point is worth.
///
/// The unit is DECK-VALUE POINTS: what <see cref="BuildValue"/> scores a card in,
/// what the shop converts at <see cref="BotShopPlanner.GoldPerDeckValue"/> gold per
/// point, and what <see cref="HumanCoopAdvisor.RelicValue"/> reports. HP is priced
/// 1:1 with it, which is not an invention: <see cref="RoomFullOfCheeseHandler"/>
/// already compares a wound's HP directly against a relic's score, and the combat
/// terminal comparator uses HP as its utility unit.
///
/// These are policy estimates, not measurements. Every handler using them logs both
/// sides, so the arithmetic can be audited from the live log.
/// </summary>
internal static class EventTrade
{
    /// <summary>EventTrade.Gold is worth what it can buy.</summary>
    internal static double Gold(double amount) => Math.Max(0, amount) / BotShopPlanner.GoldPerDeckValue;

    /// <summary>Current HP paid now. Full price: at 10/80 one hit point is the run.</summary>
    internal static double HealthLost(double amount) => Math.Max(0, amount);

    /// <summary>Healing actually taken — overflow is worth nothing.</summary>
    internal static double Healed(Player player, double amount)
        => Math.Clamp(amount, 0, Math.Max(0, player.Creature.MaxHp - player.Creature.CurrentHp));

    /// <summary>Permanent max HP, in the same unit as current HP.</summary>
    internal static double MaxHpChanged(double amount) => amount;

    /// <summary>A curse the option adds. Bounded and depth-scaled, never the card
    /// path's -1000 refusal: a curse the option PAYS for is a cost, not a defect.</summary>
    internal static double Curse(Player player) => -14 * RunDepth.BloatFactor(player);

    /// <summary>
    /// What the deck gains by shedding its <paramref name="count"/> worst cards,
    /// priced by the one deck-strength potential and the one removal priority:
    /// a removable curse first, then unupgraded basic Strikes/Defends while any
    /// remain, then the rest. Sequential, like the removal plan: the second
    /// removal cannot shed the card the first one already gave up. A card the
    /// deck must keep whatever a score says is skipped — the same two guards every
    /// removal path uses (never-remove ids, and a role the deck would lose its
    /// last copy of).
    /// </summary>
    internal static double BestRemoval(Player player, int count = 1,
        IReadOnlyList<CardModel>? deckOverride = null, Func<CardModel, bool>? eligible = null)
    {
        var deck = (deckOverride ?? player.Deck.Cards.ToList()).ToList();
        var candidates = deck.Where(card => !BotShopPlanner.IsNeverRemoved(card)
            && (eligible is null || eligible(card))).ToList();
        RemovalPlan.Plan plan;
        try { plan = RemovalPlan.Choose(player, candidates, count, deck); }
        catch { return 0; /* an unreadable card is not a removal target */ }
        var total = 0.0;
        foreach (var step in plan.Steps)
        {
            // A protected step is the planner refusing to strip a role, not a
            // removal the event actually gets. The old price skipped it and
            // stopped, so a forced all-protected screen still prices at zero.
            if (step.Protected || step.Value <= 0) break;
            total += step.Value;
        }
        return total;
    }

    /// <summary>Read an event var without letting a missing key throw.</summary>
    internal static double Var(EventModel model, string key)
    {
        try { return (double)model.DynamicVars[key].BaseValue; } catch { return 0; }
    }

    internal static double Card(Player player, CardModel card)
    {
        if (card.Type is CardType.Curse or CardType.Status) return EventTrade.Curse(player);
        // A Quest card is refused by the combat deck-value model as unplayable —
        // it deals no damage, blocks nothing and is never played — so
        // BuildValue.Add answers with its -1000 guard sentinel. But its payoff is
        // entirely OUT of combat: SPOILS_MAP rewrites the next act's map so every
        // path funnels through a treasure node, and completes for 600 gold. Price
        // that reward, minus the draw slot it costs while it waits. Without this
        // branch the whole card scored -1000 and the map option lost on its combat
        // value alone, before the reward was ever read (measured 2026-09-30: the
        // bare option scored -237 against the exit's -4).
        if (QuestGold(card) is { } gold) return EventTrade.Gold(gold) - DeadCard(player);
        try { return BuildValue.AddForDecision(card, player).Total; } catch { return 0; }
    }

    /// <summary>
    /// The gold a quest card pays when its quest completes, or null when this is
    /// not a quest this build knows how to price (only SPOILS_MAP). Matched on
    /// both the model id and the type name, the same belt-and-braces the removal
    /// guard uses: an id that comes back empty must not silently stop pricing.
    /// </summary>
    private static double? QuestGold(CardModel card)
    {
        if (!card.Id.Entry.Contains("SPOILS_MAP", StringComparison.OrdinalIgnoreCase)
            && !card.GetType().Name.Contains("SpoilsMap", StringComparison.OrdinalIgnoreCase))
            return null;
        try { return (double)card.DynamicVars["Gold"].BaseValue; } catch { return null; }
    }

    /// <summary>
    /// The cost of a dead card that cannot be removed and pays nothing in combat:
    /// a Quest card while its quest is open. One quarter of a curse, stated as a
    /// positive magnitude like <see cref="HealthLost"/> — it wastes a draw slot,
    /// but unlike a curse it does nothing when it is finally drawn. Bounded, and
    /// it never decides an event on its own: the map's 600 gold converts to ~200.
    /// </summary>
    internal static double DeadCard(Player player) => -EventTrade.Curse(player) * 0.25;

    internal static double Potion(Player player, PotionModel potion)
    {
        try { return BotPotionInventory.ReserveValue(player, potion) ?? 0; } catch { return 0; }
    }

    /// <summary>
    /// Deck-point value of EVERYTHING this option will actually grant: the models the
    /// game put on its hover tips, plus the ones the RandomForeseer port predicts by
    /// replaying the game's generator on a CLONED RNG. A model that appears in both is
    /// counted once (options that name their grant carry both).
    ///
    /// This is the T1+T2 core, kept in one place so the generic scorer and every
    /// trade handler price a granted card, relic or potion identically.
    /// </summary>
    internal static (double Value, string Detail) Grants(Player player, EventModel model, EventOption option)
    {
        if (RelicExchange(player, model, option) is { } exchange) return exchange;
        if (Transforms(option))
            return (TransformPlan.EventValue(player, model, option), "complete-transform-transaction");
        if (model is MegaCrit.Sts2.Core.Models.Events.Trial
            && option.TextKey == "TRIAL.pages.NONDESCRIPT.options.GUILTY")
            return (TransformPlan.TrialRewards(player, model, option), "curse-and-two-sequential-rewards");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0.0;
        var detail = new List<string>();

        void Add(string key, double value, string name)
        {
            if (!seen.Add(key)) return;
            total += value;
            detail.Add($"{name}={value:F1}");
        }

        foreach (var tip in option.HoverTips)
        {
            if (tip.CanonicalModel is CardModel named) Add("c:" + named.Id.Entry, Card(player, named), named.Id.Entry);
            else if (tip.CanonicalModel is RelicModel relic) Add("r:" + relic.Id, Relic(player, relic), relic.Id.Entry);
            else if (tip.CanonicalModel is PotionModel potion) Add("p:" + potion.Id, Potion(player, potion), potion.Id.Entry);
        }

        OutOfCombatPredictions.Effect predicted;
        try { predicted = OutOfCombatPredictions.EventOption(model, option); }
        catch { predicted = OutOfCombatPredictions.Effect.Empty; }

        foreach (var card in predicted.Cards)
            if (card is not null) Add("c:" + card.Id.Entry, Card(player, card), card.Id.Entry + "*");
        foreach (var bundle in predicted.Bundles)
        {
            // A bundle is a CHOICE, so it is worth its best member — and its members
            // must not be double counted against each other.
            var best = BestChoice(bundle.Cards.Select(card => (card, Value: Card(player, card))).ToArray(), bundle.Mandatory);
            if (best.Card is not null) Add("c:" + best.Card.Id.Entry, best.Value, best.Card.Id.Entry + "*");
        }
        foreach (var relic in predicted.Relics)
            if (relic is not null) Add("r:" + relic.Id, Relic(player, relic), relic.Id.Entry + "*");
        foreach (var potion in predicted.Potions)
            if (potion is not null) Add("p:" + potion.Id, Potion(player, potion), potion.Id.Entry + "*");

        return (total, string.Join(",", detail));
    }

    /// <summary>
    /// Native RelicTrader.Trade removes OwnedRelics[index], then obtains
    /// NewRelics[index]. Its two hover models describe that signed exchange,
    /// not two grants. Read only the lists already initialized by the visible
    /// page: invoking their lazy getters would advance the native relic RNG.
    /// </summary>
    internal static (double Value, string Detail)? RelicExchange(Player player, EventModel model, EventOption option)
    {
        if (model is RelicTrader)
        {
            var index = option.TextKey switch {
                "RELIC_TRADER.pages.INITIAL.options.TOP" => 0,
                "RELIC_TRADER.pages.INITIAL.options.MIDDLE" => 1,
                "RELIC_TRADER.pages.INITIAL.options.BOTTOM" => 2,
                _ => -1 };
            if (index < 0) return (0, "no-relic-exchange");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var old = typeof(RelicTrader).GetField("_ownedRelics", flags)?.GetValue(model) as IReadOnlyList<RelicModel>;
            var received = typeof(RelicTrader).GetField("_newRelics", flags)?.GetValue(model) as IReadOnlyList<RelicModel>;
            if (old is null || received is null || index >= old.Count || index >= received.Count)
                return (0, "relic-exchange:unavailable-initialized-pair");
            return SignedRelicExchange(player, old[index], [received[index]]);
        }
        // The other native relic-payment option names ONLY the surrendered
        // relic in its hover, and obtains two rolls. Never count that cost as a
        // grant merely because the presentation uses the same hover type.
        if (model is RanwidTheElder && option.TextKey == "RANWID_THE_ELDER.pages.INITIAL.options.RELIC")
        {
            var old = option.HoverTips.Select(t => t.CanonicalModel).OfType<RelicModel>().FirstOrDefault();
            if (old is null) return (0, "relic-exchange:unavailable-surrender");
            var received = OutOfCombatPredictions.EventOption(model, option).Relics;
            return SignedRelicExchange(player, old, received);
        }
        return null;
    }

    internal static (double Value, string Detail) SignedRelicExchange(Player player, RelicModel old, IReadOnlyList<RelicModel> received)
    {
        var cost = RemainingRelicValue(player, old, obtaining: false);
        var values = received.Select(r => (Relic: r, Value: RemainingRelicValue(player, r, obtaining: true))).ToArray();
        var gain = values.Sum(r => r.Value);
        return (gain - cost, $"remove:{old.Id.Entry}={-cost:F2},obtain:["
            + string.Join('|', values.Select(r => $"{r.Relic.Id.Entry}={r.Value:F2}"))
            + $"],net={gain-cost:F2},remaining-fights={RunDepth.RemainingCombats(player):F2}");
    }

    internal static double RemainingRelicValue(Player player, RelicModel relic, bool obtaining)
    {
        // These native effects already happened. Surrendering the object does
        // not undo the gold/upgrade it previously granted.
        if (!obtaining && relic is OldCoin or Whetstone or WarPaint or SandCastle or FragrantMushroom)
            return 0;
        if (relic is EternalFeather)
        {
            var cards = Math.Max(1, (int)relic.DynamicVars["Cards"].BaseValue);
            var healing = Math.Min((double)player.Creature.MaxHp,
                player.Deck.Cards.Count / cards * (double)relic.DynamicVars["Heal"].BaseValue);
            return healing * AncientOptionValue.RemainingRooms(player, MapPointType.RestSite, 2);
        }
        // A new pickup pays now; recurring effects lose value when only a few
        // combats remain. This is the existing Ancient eight-fight horizon,
        // with zero when no future use remains, not an invented full-run price.
        if (obtaining && relic.HasUponPickupEffect) return Relic(player, relic);
        return Relic(player, relic) * Math.Clamp(RunDepth.RemainingCombats(player) / 8, 0, 1.5);
    }

    /// <summary>
    /// The relic an option grants. Prefers a named model on the option; falls back to
    /// the RandomForeseer prediction for "a random relic"; only then to the estimate.
    /// </summary>
    internal static double GrantedRelic(Player player, EventModel model, EventOption option)
    {
        var named = BotEventDriver.GrantedRelics(option).ToList();
        if (named.Count > 0) return named.Max(relic => Relic(player, relic));
        try
        {
            var predicted = OutOfCombatPredictions.EventOption(model, option).Relics;
            if (predicted.Count > 0) return predicted.Max(relic => Relic(player, relic));
        }
        catch { /* no prediction is not a reason to invent one */ }
        return EventTrade.UnnamedRelic;
    }

    internal static double Relic(Player player, RelicModel relic)
    {
        try { return HumanCoopAdvisor.RelicValue(relic, player).Score; } catch { return 0; }
    }

    /// <summary>
    /// True when the GAME marks this option as a card transform. The game tags
    /// such an option with <c>HoverTipFactory.Static(StaticHoverTip.Transform)</c>,
    /// whose hover tip Id carries the localisation KEY (`TRANSFORM.title`), not
    /// localised text — so this reads the game's own statement rather than a
    /// per-event option-id table that rots silently when an id is renamed.
    /// </summary>
    internal static bool Transforms(EventOption option)
    {
        foreach (var tip in option.HoverTips)
        {
            try
            {
                if (tip.Id?.Contains("TRANSFORM.title", StringComparison.OrdinalIgnoreCase) == true) return true;
            }
            catch { /* a tip this build cannot read is not evidence of a transform */ }
        }
        return false;
    }

    /// <summary>
    /// Slither rewrites the card's cost to a uniform 0..3 EVERY time it is drawn
    /// (`Slither.AfterCardDrawn` → `EnergyCost.SetThisCombat(Rng.NextInt(4))`), so
    /// it is a cost RANDOMISATION, not a stat bonus: the expected cost becomes 1.5,
    /// which is a gain only for a card that cost more than that. Priced through the
    /// commitment term the deck potential already charges for expensive cards, so
    /// the "3 cost or better" threshold falls out of the model rather than being a
    /// hard-coded rule. A target the enchantment cannot take is not a candidate.
    /// </summary>
    internal static double SlitherGain(Player player, CardModel card)
    {
        // Mirrors Slither.CanEnchant: no unplayable cards, no X costs.
        if (card.Keywords.Contains(CardKeyword.Unplayable) || card.EnergyCost.CostsX)
            return double.NegativeInfinity;
        var energy = Math.Max(1, BuildValue.StableEnergy(player));
        var now = DeckStrength.CommitmentLoad(card, energy);
        var expected = 0.0;
        for (var cost = 0; cost <= 3; cost++) expected += DeckStrength.CommitmentLoadForCost(cost, energy);
        expected /= 4;
        return DeckStrength.CommitmentWeight * (now - expected);
    }

    /// <summary>Best gain any legal target in the deck would take from Slither.</summary>
    internal static double BestSlitherGain(Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var best = double.NegativeInfinity;
        foreach (var card in deckOverride ?? player.Deck.Cards.ToList())
        {
            try { best = Math.Max(best, SlitherGain(player, card)); }
            catch { /* a card this build cannot read is not a target */ }
        }
        return best;
    }

    /// <summary>The potion an option grants, predicted when possible.</summary>
    internal static double GrantedPotion(Player player, EventModel model, EventOption option)
    {
        try
        {
            var predicted = OutOfCombatPredictions.EventOption(model, option).Potions;
            foreach (var potion in predicted)
            {
                var value = BotPotionInventory.ReserveValue(player, potion);
                if (value is { } found) return found;
            }
        }
        catch { /* fall through to the estimate */ }
        return EventTrade.UnnamedPotion;
    }

    internal static (CardModel? Card, double Value) BestChoice(
        IReadOnlyList<(CardModel Card, double Value)> choices, bool mandatory)
    {
        if (choices.Count == 0) return (null, 0);
        var best = choices.MaxBy(c => c.Value);
        return mandatory || best.Value > 0 ? best : (null, 0);
    }

    /// <summary>Grants for the option whose id ends with <paramref name="suffix"/>; 0 when absent.</summary>
    internal static double GrantsOf(Player player, EventModel model, string suffix)
    {
        var index = EventHandlers.IndexOf(model, suffix);
        return index < 0 ? 0 : Grants(player, model, model.CurrentOptions[index]).Value;
    }

    /// <summary>Greedy upgrades, re-evaluated after each hypothetical replacement.</summary>
    internal static double BestUpgrades(Player player, int count)
    {
        var before = player.Deck.Cards.ToArray();
        var after = before.ToList();
        var remaining = BuildValue.EligibleUpgrades(before).ToList();
        for (var i = 0; i < count && remaining.Count > 0; i++)
        {
            var ranked = BuildValue.RankUpgrades(remaining.Select(c =>
                (Card: c, Value: BuildValue.UpgradeForDecision(c, player, after)))).First();
            if (ConstructionDecision.AwaitingValues) break;
            if (ranked.Value.Total <= 0) break;
            var best = ranked.Card;
            remaining.Remove(best);
            if (CardProfile.Upgraded(best, BuildValue.StableEnergy(player)) is { } upgrade)
                after[after.IndexOf(best)] = upgrade.Card;
        }
        return DeckStrength.Change(before, after, player).Total;
    }

    private static bool IsCurse(CardModel card) => card.Type is CardType.Curse or CardType.Status;

    /// <summary>
    /// Whether the option's own prompts already charge for a curse — its hover tips or
    /// the prediction name one. A handler that knows from the event text how many
    /// curses an option adds must not charge the same curse twice.
    /// </summary>
    internal static bool PricesCurse(EventModel model, EventOption option)
    {
        foreach (var tip in option.HoverTips)
            if (tip.CanonicalModel is CardModel card && IsCurse(card)) return true;
        try
        {
            var predicted = OutOfCombatPredictions.EventOption(model, option);
            foreach (var card in predicted.Cards)
                if (card is not null && IsCurse(card)) return true;
            foreach (var bundle in predicted.Bundles)
            foreach (var card in bundle.Cards)
                if (card is not null && IsCurse(card)) return true;
        }
        catch { /* no prediction is not a reason to invent one */ }
        return false;
    }

    /// <summary>
    /// Grants for the option, minus any curse the option did not already price. The
    /// curse COUNT comes from the event's own text (which is localised and may not be
    /// parsed), so the handler passes it explicitly.
    /// </summary>
    internal static double GrantsWithCurses(Player player, EventModel model, string suffix, int curses)
    {
        var index = EventHandlers.IndexOf(model, suffix);
        if (index < 0) return 0;
        var option = model.CurrentOptions[index];
        var value = Grants(player, model, option).Value;
        if (curses > 0 && !PricesCurse(model, option)) value += curses * EventTrade.Curse(player);
        return value;
    }

    // The per-play weights MarginalCore already uses, so an enchantment is priced in
    // the same units as every other card change rather than in a new currency.
    private const double DamagePointValue = 1.2;
    private const double BlockPointValue = 1.0;
    private const double DrawPointValue = 4.0;
    // An additive enchantment pays on every play; a first-play one pays once. Three
    // plays is the same bounded horizon the cycle estimate uses.
    private const double ExpectedPlays = 3;

    /// <summary>
    /// What an enchantment adds to ONE card, in deck-value points. Read from the
    /// game's own descriptions: SHARP "+N damage", NIMBLE "+N block", SWIFT "draw N
    /// cards the first time this is played", VIGOROUS "+N damage the first time".
    /// An unknown enchantment is worth zero rather than guessed.
    /// </summary>
    internal static double Enchantment(string id, double amount)
    {
        var n = Math.Max(0, amount);
        return id switch
        {
            "SHARP" => n * DamagePointValue * ExpectedPlays,
            "NIMBLE" => n * BlockPointValue * ExpectedPlays,
            "SWIFT" => n * DrawPointValue,
            "VIGOROUS" => n * DamagePointValue,
            _ => 0,
        };
    }

    /// <summary>The id an enchantment var names ("SHARP.title" -> "SHARP"), or "".</summary>
    internal static string EnchantmentId(EventModel model, string key)
    {
        try
        {
            var variable = model.DynamicVars[key];
            var raw = variable.GetType().GetProperty("StringValue")?.GetValue(variable)?.ToString() ?? "";
            var dot = raw.IndexOf('.');
            return (dot > 0 ? raw[..dot] : raw).ToUpperInvariant();
        }
        catch { return ""; }
    }

    internal static bool HasCardOfType(Player player, CardType type)
        => player.Deck.Cards.Any(card => card.Type == type);

    /// <summary>
    /// The average value of an upgrade this deck could still take. Used as the price of
    /// LOSING one: Wongos downgrades an already-upgraded card at random, and a card that
    /// is already upgraded reports no further delta, so the deck's remaining upgrade
    /// value is the bounded proxy.
    /// </summary>
    internal static double AverageUpgradeValue(Player player)
    {
        var values = new List<double>();
        foreach (var card in player.Deck.Cards)
        {
            try
            {
                var delta = BuildValue.UpgradeForDecision(card, player).Total;
                if (delta > 0) values.Add(delta);
            }
            catch { }
        }
        return values.Count == 0 ? 0 : values.Average();
    }

    // An outcome the option does not name and no prediction can resolve. Estimates,
    // logged whenever they are used; they exist so the choice is not page order.
    internal const double UnnamedRelic = 25;
    internal const double UnnamedPotion = 12;
}

/// <summary>A handler that can explain its two sides in the decision log.</summary>
internal interface IEventTradeExplainer
{
    string Explain(Player player, EventModel model);
}

/// <summary>
/// Sunken Statue: dive for gold and take a wound, or take the named relic. Both
/// sides are data — the wound and the gold from the event's vars, the relic from
/// the option itself.
/// </summary>
internal sealed class SunkenStatueHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["DIVE_INTO_WATER", "GRAB_SWORD"];

    internal static (double Dive, double Sword) Price(Player player, EventModel model)
    {
        var dive = EventTrade.Gold(EventTrade.Var(model, "Gold"))
            - EventTrade.HealthLost(EventTrade.Var(model, "HpLoss"));
        var sword = EventHandlers.IndexOf(model, "GRAB_SWORD") is var i and >= 0
            ? EventTrade.GrantedRelic(player, model, model.CurrentOptions[i]) : 0;
        return (dive, sword);
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        var dive = EventHandlers.IndexOf(eventModel, "DIVE_INTO_WATER");
        var sword = EventHandlers.IndexOf(eventModel, "GRAB_SWORD");
        if (dive < 0 || sword < 0) return null;
        var (diveValue, swordValue) = Price(player, eventModel);
        return swordValue > diveValue ? sword : dive;
    }

    public string Explain(Player player, EventModel model)
    {
        var (dive, sword) = Price(player, model);
        return $"dive={dive:F1} sword={sword:F1}";
    }
}

/// <summary>
/// Sunken Treasury: a small chest of gold, or a much larger one plus the Greed
/// curse. The curse is a permanent deck cost, so the bigger number is not
/// automatically the better option.
/// </summary>
internal sealed class SunkenTreasuryHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["FIRST_CHEST", "SECOND_CHEST"];

    internal static (double First, double Second) Price(Player player, EventModel model)
        => (EventTrade.Gold(EventTrade.Var(model, "SmallChestGold")),
            EventTrade.Gold(EventTrade.Var(model, "LargeChestGold")) + EventTrade.Curse(player));

    public int? Choose(Player player, EventModel eventModel)
    {
        var first = EventHandlers.IndexOf(eventModel, "FIRST_CHEST");
        var second = EventHandlers.IndexOf(eventModel, "SECOND_CHEST");
        if (first < 0 || second < 0) return null;
        var (small, large) = Price(player, eventModel);
        return large > small ? second : first;
    }

    public string Explain(Player player, EventModel model)
    {
        var (first, second) = Price(player, model);
        return $"small={first:F1} large={second:F1}";
    }
}

/// <summary>
/// Jungle Maze Adventure: share the gold, or keep more of it and take a wound.
/// </summary>
internal sealed class JungleMazeAdventureHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["JOIN_FORCES", "SOLO_QUEST"];

    internal static (double Share, double Solo) Price(Player player, EventModel model)
        => (EventTrade.Gold(EventTrade.Var(model, "JoinForcesGold")),
            EventTrade.Gold(EventTrade.Var(model, "SoloGold")) - EventTrade.HealthLost(EventTrade.Var(model, "SoloHp")));

    public int? Choose(Player player, EventModel eventModel)
    {
        var join = EventHandlers.IndexOf(eventModel, "JOIN_FORCES");
        var solo = EventHandlers.IndexOf(eventModel, "SOLO_QUEST");
        if (join < 0 || solo < 0) return null;
        var (share, solitaire) = Price(player, eventModel);
        return solitaire > share ? solo : join;
    }

    public string Explain(Player player, EventModel model)
    {
        var (share, solo) = Price(player, model);
        return $"join={share:F1} solo={solo:F1}";
    }
}

/// <summary>
/// This or That: a curse plus a random relic, or a wound plus gold. The relic is
/// unnamed, so it is priced by prediction when the port can resolve it and by the
/// logged estimate otherwise.
/// </summary>
internal sealed class ThisOrThatHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["ORNATE", "PLAIN"];

    internal static (double Ornate, double Plain) Price(Player player, EventModel model)
    {
        var ornate = EventHandlers.IndexOf(model, "ORNATE") is var i and >= 0
            ? EventTrade.GrantedRelic(player, model, model.CurrentOptions[i]) + EventTrade.Curse(player) : 0;
        var plain = EventTrade.Gold(EventTrade.Var(model, "Gold"))
            - EventTrade.HealthLost(EventTrade.Var(model, "HpLoss"));
        return (ornate, plain);
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        var ornate = EventHandlers.IndexOf(eventModel, "ORNATE");
        var plain = EventHandlers.IndexOf(eventModel, "PLAIN");
        if (ornate < 0 || plain < 0) return null;
        var (cursed, wound) = Price(player, eventModel);
        return cursed > wound ? ornate : plain;
    }

    public string Explain(Player player, EventModel model)
    {
        var (ornate, plain) = Price(player, model);
        return $"ornate={ornate:F1} plain={plain:F1}";
    }
}

/// <summary>
/// Unrest Site: pay permanent Max HP for a relic, or heal to full and take the
/// Bad Sleep curse. The heal is capped at the wound the player actually has.
/// </summary>
internal sealed class UnrestSiteHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["KILL", "REST"];

    // `Rest` is a reserved tuple element name, hence `Recover`.
    internal static (double Kill, double Recover) Price(Player player, EventModel model)
    {
        var kill = EventHandlers.IndexOf(model, "KILL") is var i and >= 0
            ? EventTrade.GrantedRelic(player, model, model.CurrentOptions[i])
                - EventTrade.MaxHpChanged(EventTrade.Var(model, "MaxHpLoss")) : 0;
        // "Heal all your HP" is the missing pool, not the Heal var the event also carries.
        var rest = EventTrade.Healed(player, player.Creature.MaxHp - player.Creature.CurrentHp)
            + EventTrade.Curse(player);
        return (kill, rest);
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        var kill = EventHandlers.IndexOf(eventModel, "KILL");
        var rest = EventHandlers.IndexOf(eventModel, "REST");
        if (kill < 0 || rest < 0) return null;
        var (trade, heal) = Price(player, eventModel);
        return trade > heal ? kill : rest;
    }

    public string Explain(Player player, EventModel model)
    {
        var (kill, rest) = Price(player, model);
        return $"kill={kill:F1} rest={rest:F1}";
    }
}

/// <summary>
/// Self Help Book: three pages, each enchanting a card of ONE type — an Attack with
/// SHARP, a Skill with NIMBLE, a Power with SWIFT. The option ids say nothing about
/// which is better, so the enchantments are read from the event's own vars and priced
/// by their printed effect; a page whose type the deck has none of is worth nothing.
/// </summary>
internal sealed class SelfHelpBookHandler : IEventSpecialHandler, IEventTradeExplainer
{
    // Option id -> (enchantment id var, amount var, card type it lands on).
    private static readonly (string Option, string IdVar, string AmountVar, CardType Type)[] Pages =
    [
        ("READ_THE_BACK", "Enchantment1", "Enchantment1Amount", CardType.Attack),
        ("READ_PASSAGE", "Enchantment2", "Enchantment2Amount", CardType.Skill),
        ("READ_ENTIRE_BOOK", "Enchantment3", "Enchantment3Amount", CardType.Power),
    ];

    public IReadOnlyList<string> OptionIds => ["NO_OPTIONS", "READ_ENTIRE_BOOK", "READ_ENTIRE_BOOK_LOCKED",
        "READ_PASSAGE", "READ_PASSAGE_LOCKED", "READ_THE_BACK", "READ_THE_BACK_LOCKED"];

    private static string Suffix(string textKey) => textKey[(textKey.LastIndexOf('.') + 1)..];

    internal static double Price(Player player, EventModel model, string idVar, string amountVar, CardType type)
        => EventTrade.HasCardOfType(player, type)
            ? EventTrade.Enchantment(EventTrade.EnchantmentId(model, idVar), EventTrade.Var(model, amountVar))
            : 0;

    public int? Choose(Player player, EventModel eventModel)
    {
        // Owner 2026-10-05: hard page preference instead of the printed-price
        // comparison. Take the Power page (third option in the game's page order)
        // whenever it can be taken, else the Attack page (first option). Only when
        // neither named page is takeable — e.g. a skill-only deck — does the
        // printed-price comparison decide, which keeps the Skill page reachable.
        // Live 2026-10-04 every printed price was zero, so the priced loop fell
        // through to page order.
        var entireBook = Find(eventModel, "READ_ENTIRE_BOOK");
        if (entireBook is { } third && CanTake(player, eventModel, third, CardType.Power))
            return third;
        var theBack = Find(eventModel, "READ_THE_BACK");
        if (theBack is { } first && CanTake(player, eventModel, first, CardType.Attack))
            return first;
        return PricedBest(player, eventModel);
    }

    private static int? Find(EventModel eventModel, string id)
    {
        for (var index = 0; index < eventModel.CurrentOptions.Count; index++)
            if (Suffix(eventModel.CurrentOptions[index].TextKey) == id) return index;
        return null;
    }

    private static bool CanTake(Player player, EventModel eventModel, int index, CardType type)
    {
        var option = eventModel.CurrentOptions[index];
        if (option.IsLocked || option.WasChosen) return false;
        if (!EventTrade.HasCardOfType(player, type)) return false;
        return !EnchantmentEventPolicy.ShouldAvoid(player, eventModel, option);
    }

    private int? PricedBest(Player player, EventModel eventModel)
    {
        int? best = null;
        var bestValue = double.NegativeInfinity;
        for (var index = 0; index < eventModel.CurrentOptions.Count; index++)
        {
            var option = eventModel.CurrentOptions[index];
            if (option.IsLocked || option.WasChosen) continue;
            var page = Pages.FirstOrDefault(p => p.Option == Suffix(option.TextKey));
            if (page.Option is null) continue;
            if (EnchantmentEventPolicy.ShouldAvoid(player, eventModel, option)) continue;
            var value = Price(player, eventModel, page.IdVar, page.AmountVar, page.Type);
            if (value > bestValue) { bestValue = value; best = index; }
        }
        return best;
    }

    public string Explain(Player player, EventModel model)
        => string.Join(" ", Pages.Select(p =>
            $"{p.Option}={Price(player, model, p.IdVar, p.AmountVar, p.Type):F1}"));
}

/// <summary>
/// Stone of All Time: drink a random potion for permanent Max HP, or take a wound to
/// enchant an Attack with Vigorous. Both sides are priced: the potion by its reserve
/// value, the wound 1:1, and Vigorous by its printed first-play damage.
/// </summary>
internal sealed class StoneOfAllTimeHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["LIFT", "LIFT_LOCKED", "PUSH", "PUSH_LOCKED"];

    internal static (double Lift, double Push) Price(Player player, EventModel model)
        => (-EventTrade.UnnamedPotion + EventTrade.MaxHpChanged(EventTrade.Var(model, "DrinkMaxHpGain")),
            EventTrade.Enchantment("VIGOROUS", EventTrade.Var(model, "PushVigorousAmount")) - EventTrade.HealthLost(EventTrade.Var(model, "PushHpLoss")));

    public int? Choose(Player player, EventModel eventModel)
    {
        var lift = EventHandlers.IndexOf(eventModel, "LIFT");
        var push = EventHandlers.IndexOf(eventModel, "PUSH");
        if (lift < 0 || push < 0) return null;
        var (drink, shove) = Price(player, eventModel);
        return shove > drink ? push : lift;
    }

    public string Explain(Player player, EventModel model)
    {
        var (lift, push) = Price(player, model);
        return $"lift={lift:F1} push={push:F1}";
    }
}

/// <summary>
/// Welcome to Wongos: a shop. Each shelf costs gold from the event's own vars, the
/// bargain bin's relic is predicted, and LEAVING is not free — the game downgrades one
/// of the upgraded cards, so walking out has a price of its own. A shelf whose contents
/// cannot be predicted is left unpriced rather than bought blind.
/// </summary>
internal sealed class WelcomeToWongosHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["BARGAIN_BIN", "BARGAIN_BIN_LOCKED", "FEATURED_ITEM",
        "FEATURED_ITEM_LOCKED", "LEAVE", "MYSTERY_BOX", "MYSTERY_BOX_LOCKED"];

    internal static (double Bin, double Featured, double Box, double Leave) Price(Player player, EventModel model)
        => (EventTrade.GrantsOf(player, model, "BARGAIN_BIN") - EventTrade.Gold(EventTrade.Var(model, "BargainBinCost")),
            EventTrade.GrantsOf(player, model, "FEATURED_ITEM") - EventTrade.Gold(EventTrade.Var(model, "FeaturedItemCost")),
            EventTrade.GrantsOf(player, model, "MYSTERY_BOX") - EventTrade.Gold(EventTrade.Var(model, "MysteryBoxCost")),
            -EventTrade.AverageUpgradeValue(player));

    public int? Choose(Player player, EventModel eventModel)
    {
        var values = new (string Option, double Value)[]
        {
            ("BARGAIN_BIN", Price(player, eventModel).Bin),
            ("FEATURED_ITEM", Price(player, eventModel).Featured),
            ("MYSTERY_BOX", Price(player, eventModel).Box),
            ("LEAVE", Price(player, eventModel).Leave),
        };
        int? best = null;
        var bestValue = double.NegativeInfinity;
        foreach (var (option, value) in values)
        {
            var index = EventHandlers.IndexOf(eventModel, option);
            if (index < 0) continue;
            if (value > bestValue) { bestValue = value; best = index; }
        }
        return best;
    }

    public string Explain(Player player, EventModel model)
    {
        var (bin, featured, box, leave) = Price(player, model);
        return $"bin={bin:F1} featured={featured:F1} box={box:F1} leave={leave:F1}";
    }
}

/// <summary>
/// Lost Wisp: take a curse with the relic, or take the gold and walk away. The curse
/// count comes from the event's own text; it is only charged here if the option's
/// prompts did not already name it, so it cannot be paid for twice.
/// </summary>
internal sealed class LostWispHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["CLAIM", "CLAIM_LOCKED", "SEARCH"];

    internal static (double Claim, double Search) Price(Player player, EventModel model)
        => (EventTrade.GrantsWithCurses(player, model, "CLAIM", 1),
            EventTrade.Gold(EventTrade.Var(model, "Gold")));

    public int? Choose(Player player, EventModel eventModel)
    {
        var claim = EventHandlers.IndexOf(eventModel, "CLAIM");
        var search = EventHandlers.IndexOf(eventModel, "SEARCH");
        if (claim < 0 || search < 0) return null;
        var (relic, gold) = Price(player, eventModel);
        return relic > gold ? claim : search;
    }

    public string Explain(Player player, EventModel model)
    {
        var (claim, search) = Price(player, model);
        return $"claim={claim:F1} search={search:F1}";
    }
}

/// <summary>
/// Spirit Grafter: let the spirit in for a heal plus Metamorphosis, or refuse it and
/// pay health for a card upgrade.
/// </summary>
internal sealed class SpiritGrafterHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["LET_IT_IN", "REJECTION"];

    internal static (double Accept, double Reject) Price(Player player, EventModel model)
        => (EventTrade.Healed(player, EventTrade.Var(model, "LetItInHealAmount"))
                + EventTrade.GrantsOf(player, model, "LET_IT_IN"),
            EventTrade.BestUpgrades(player, 1) - EventTrade.HealthLost(EventTrade.Var(model, "RejectionHpLoss")));

    public int? Choose(Player player, EventModel eventModel)
    {
        var accept = EventHandlers.IndexOf(eventModel, "LET_IT_IN");
        var reject = EventHandlers.IndexOf(eventModel, "REJECTION");
        if (accept < 0 || reject < 0) return null;
        var (letIn, refusal) = Price(player, eventModel);
        return letIn > refusal ? accept : reject;
    }

    public string Explain(Player player, EventModel model)
    {
        var (accept, reject) = Price(player, model);
        return $"let-in={accept:F1} rejection={reject:F1}";
    }
}

/// <summary>
/// Drowning Beacon: bottle a potion, or climb for the named relic at the price of
/// permanent Max HP. (This used to be an inline special case in the generic scorer
/// that only ever subtracted the health; the relic side was never priced at all.)
/// </summary>
internal sealed class DrowningBeaconHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["BOTTLE", "CLIMB"];

    internal static (double Bottle, double Climb) Price(Player player, EventModel model)
        => (EventTrade.GrantsOf(player, model, "BOTTLE"),
            EventTrade.GrantsOf(player, model, "CLIMB") - EventTrade.MaxHpChanged(EventTrade.Var(model, "HpLoss")));

    public int? Choose(Player player, EventModel eventModel)
    {
        var bottle = EventHandlers.IndexOf(eventModel, "BOTTLE");
        var climb = EventHandlers.IndexOf(eventModel, "CLIMB");
        if (bottle < 0 || climb < 0) return null;
        var (potion, relic) = Price(player, eventModel);
        return relic > potion ? climb : bottle;
    }

    public string Explain(Player player, EventModel model)
    {
        var (bottle, climb) = Price(player, model);
        return $"bottle={bottle:F1} climb={climb:F1}";
    }
}

/// <summary>
/// Battleworn Dummy: three dummies of growing HP, each paying a different random
/// reward (a potion, two upgrades, a relic). The reward is read from the game's own
/// generator on a cloned RNG, so the comparison is between what each setting will
/// ACTUALLY give — not between "SETTING_1/2/3", which carry no wording at all.
/// </summary>
internal sealed class BattlewornDummyHandler : IEventSpecialHandler, IEventTradeExplainer
{
    // The dummy is a training target, not a threat: the cost of the longer fight is
    // time and chip damage. One point per this much dummy HP keeps the longest fight
    // from being free without inventing a combat simulation.
    private const double DummyHpPerPoint = 30;

    private static readonly Dictionary<string, string> HpVars = new(StringComparer.Ordinal)
    {
        ["SETTING_1"] = "Setting1Hp",
        ["SETTING_2"] = "Setting2Hp",
        ["SETTING_3"] = "Setting3Hp",
    };

    public IReadOnlyList<string> OptionIds => ["SETTING_1", "SETTING_2", "SETTING_3"];

    private static string? Setting(string textKey)
    {
        var suffix = textKey[(textKey.LastIndexOf('.') + 1)..];
        return HpVars.ContainsKey(suffix) ? suffix : null;
    }

    internal static double Price(Player player, EventModel model, string option, string hpVar)
        => EventTrade.GrantsOf(player, model, option) - EventTrade.Var(model, hpVar) / DummyHpPerPoint;

    public int? Choose(Player player, EventModel eventModel)
    {
        // Owner policy 2026-10-04: the second tier (two upgrades) is the fixed
        // pick. The dummy deals no damage in the live fight and the generic
        // grant path does not price the random upgrades, so the old "shortest
        // fight wins" comparison could never value them correctly.
        for (var index = 0; index < eventModel.CurrentOptions.Count; index++)
        {
            var option = eventModel.CurrentOptions[index];
            if (option.IsLocked || option.WasChosen) continue;
            if (Setting(option.TextKey) == "SETTING_2") return index;
        }
        // Malformed page fallback: keep the old priced comparison so a page
        // without a legal second tier still makes a deterministic choice.
        int? best = null;
        var bestValue = double.NegativeInfinity;
        for (var index = 0; index < eventModel.CurrentOptions.Count; index++)
        {
            var option = eventModel.CurrentOptions[index];
            if (option.IsLocked || option.WasChosen) continue;
            if (Setting(option.TextKey) is not { } setting) continue;
            var value = Price(player, eventModel, setting, HpVars[setting]);
            if (value > bestValue) { bestValue = value; best = index; }
        }
        return best;
    }

    public string Explain(Player player, EventModel model)
        => string.Join(" ", HpVars.Select(pair =>
            $"{pair.Key}={Price(player, model, pair.Key, pair.Value):F1}"));
}

/// <summary>
/// Brain Leech: pay HP for a colourless card reward, or take one of five random
/// cards for free. Both sides are predicted grants, so the only real question is
/// whether the wound is worth the better card.
/// </summary>
internal sealed class BrainLeechHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["RIP", "SHARE_KNOWLEDGE"];

    internal static (double Rip, double Share) Price(Player player, EventModel model)
        => (EventTrade.GrantsOf(player, model, "RIP") - EventTrade.HealthLost(EventTrade.Var(model, "RipHpLoss")),
            EventTrade.GrantsOf(player, model, "SHARE_KNOWLEDGE"));

    public int? Choose(Player player, EventModel eventModel)
    {
        var rip = EventHandlers.IndexOf(eventModel, "RIP");
        var share = EventHandlers.IndexOf(eventModel, "SHARE_KNOWLEDGE");
        if (rip < 0 || share < 0) return null;
        var (wounded, free) = Price(player, eventModel);
        return wounded > free ? rip : share;
    }

    public string Explain(Player player, EventModel model)
    {
        var (rip, share) = Price(player, model);
        return $"rip={rip:F1} share={share:F1}";
    }
}

/// <summary>
/// Wellspring: bathe to remove a card and take Guilt, or bottle a random potion.
/// The removal is priced by the shop's own removal path; the potion by prediction.
/// </summary>
internal sealed class WellspringHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["BATHE", "BOTTLE"];

    internal static (double Bathe, double Bottle) Price(Player player, EventModel model)
    {
        var bathe = EventTrade.BestRemoval(player)
            + EventTrade.Var(model, "BatheCurses") * EventTrade.Curse(player);
        var bottle = EventHandlers.IndexOf(model, "BOTTLE") is var i and >= 0
            ? EventTrade.GrantedPotion(player, model, model.CurrentOptions[i]) : 0;
        return (bathe, bottle);
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        var bathe = EventHandlers.IndexOf(eventModel, "BATHE");
        var bottle = EventHandlers.IndexOf(eventModel, "BOTTLE");
        if (bathe < 0 || bottle < 0) return null;
        var (bath, potion) = Price(player, eventModel);
        return bath > potion ? bathe : bottle;
    }

    public string Explain(Player player, EventModel model)
    {
        var (bathe, bottle) = Price(player, model);
        return $"bathe={bathe:F1} bottle={bottle:F1}";
    }
}

/// <summary>
/// The Legends Were True: nail down the map, or slowly find an exit for an
/// 8-point unblockable, unpowered wound and a random potion.
///
/// The map is a Quest card, so the combat deck-value model refuses it as
/// unplayable and the generic scorer priced it with the -1000 guard sentinel —
/// it declined the map on its combat value without ever reading what the card is
/// for. Its payoff is entirely out of combat, and <see cref="EventTrade.Card"/>
/// prices it (the gold the quest pays, minus the draw slot it costs while it
/// waits), so both sides here are real numbers.
/// </summary>
internal sealed class LegendsWereTrueHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["NAB_THE_MAP", "SLOWLY_FIND_AN_EXIT"];

    internal static (double Map, double Exit) Price(Player player, EventModel model)
    {
        var map = EventHandlers.IndexOf(model, "NAB_THE_MAP") is var m and >= 0
            ? EventTrade.Grants(player, model, model.CurrentOptions[m]).Value : 0;
        var exit = EventHandlers.IndexOf(model, "SLOWLY_FIND_AN_EXIT") is var e and >= 0
            ? EventTrade.GrantedPotion(player, model, model.CurrentOptions[e])
                - EventTrade.HealthLost(EventTrade.Var(model, "Damage")) : 0;
        return (map, exit);
    }

    // The map is chosen by identity, never by page order: the caller passes the
    // two live indices, so a tie cannot silently fall to whichever page order.
    internal static int Decide(int mapIndex, int exitIndex, (double Map, double Exit) price)
        => price.Map >= price.Exit ? mapIndex : exitIndex;

    public int? Choose(Player player, EventModel eventModel)
    {
        var map = EventHandlers.IndexOf(eventModel, "NAB_THE_MAP");
        var exit = EventHandlers.IndexOf(eventModel, "SLOWLY_FIND_AN_EXIT");
        if (map < 0) return exit >= 0 ? exit : null;
        if (exit < 0) return map;
        // The exit's wound cannot be blocked or scaled down, so never take it if
        // it would kill the player, whatever the map pays.
        if (eventModel.CurrentOptions[exit].WillKillPlayer?.Invoke(player) == true) return map;
        return Decide(map, exit, Price(player, eventModel));
    }

    public string Explain(Player player, EventModel model)
    {
        var (map, exit) = Price(player, model);
        return $"map={map:F1} exit={exit:F1}";
    }
}
