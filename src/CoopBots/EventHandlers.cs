using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace CoopBots;

/// <summary>
/// Per-event overrides, consulted before the generic scorer.
///
/// Most events are now decided from the game's own data (an option's relic and
/// the models behind its hover tips), which is language independent and carries
/// magnitude. A few still need card-specific reasoning that no option data
/// expresses: Reflections can duplicate the entire deck, and nothing in the
/// option says so. Those get a handler here. Returning null defers to the
/// generic scorer, so an uncovered or misbehaving event can never stall a run.
/// </summary>
internal interface IEventSpecialHandler
{
    /// <summary>
    /// The option ids this handler matches. Checked against the baked event table
    /// so a patch that renames an id fails a test instead of silently leaving the
    /// handler dead.
    /// </summary>
    IReadOnlyList<string> OptionIds { get; }

    /// <summary>Index into <see cref="EventModel.CurrentOptions"/>, or null to defer.</summary>
    int? Choose(Player player, EventModel eventModel);
}

internal static class EventHandlers
{
    private static readonly Dictionary<string, IEventSpecialHandler> Handlers = new(StringComparer.Ordinal)
    {
        ["REFLECTIONS"] = new ReflectionsHandler(),
        ["TABLET_OF_TRUTH"] = new TabletOfTruthHandler(),
        ["CRYSTAL_SPHERE"] = new CrystalSphereHandler(),
        ["POTION_COURIER"] = new PotionCourierHandler(),
        ["MORPHIC_GROVE"] = new MorphicGroveHandler(),
        ["ABYSSAL_BATHS"] = new AbyssalBathsHandler(),
        ["COLOSSAL_FLOWER"] = new ColossalFlowerHandler(),
        ["SLIPPERY_BRIDGE"] = new SlipperyBridgeHandler(),
        ["ROOM_FULL_OF_CHEESE"] = new RoomFullOfCheeseHandler(),
        ["PUNCH_OFF"] = new PunchOffHandler(),
        ["DOORS_OF_LIGHT_AND_DARK"] = new DoorsOfLightAndDarkHandler(),
        // T3: events whose options are a resource trade. These use EventTrade's one
        // unit system instead of the option ids' (absent) wording.
        ["SUNKEN_STATUE"] = new SunkenStatueHandler(),
        ["SUNKEN_TREASURY"] = new SunkenTreasuryHandler(),
        ["JUNGLE_MAZE_ADVENTURE"] = new JungleMazeAdventureHandler(),
        ["THIS_OR_THAT"] = new ThisOrThatHandler(),
        ["UNREST_SITE"] = new UnrestSiteHandler(),
        ["WELLSPRING"] = new WellspringHandler(),
        // T2: the random reward is read from the game's own generator on a cloned
        // RNG, so the choice is between what each setting will actually give.
        ["BATTLEWORN_DUMMY"] = new BattlewornDummyHandler(),
        ["BRAIN_LEECH"] = new BrainLeechHandler(),
        // T4: paying a curse or a permanent cost for a benefit.
        ["LOST_WISP"] = new LostWispHandler(),
        ["SPIRIT_GRAFTER"] = new SpiritGrafterHandler(),
        ["DROWNING_BEACON"] = new DrowningBeaconHandler(),
        // The three events the live logs actually met most often among the unmodelled.
        ["SELF_HELP_BOOK"] = new SelfHelpBookHandler(),
        ["WELCOME_TO_WONGOS"] = new WelcomeToWongosHandler(),
        ["STONE_OF_ALL_TIME"] = new StoneOfAllTimeHandler(),
        // A gold-for-deck-edits menu whose removal options carry no hover tip.
        ["ZEN_WEAVER"] = new ZenWeaverHandler(),
        // A fixed net-negative relic on one option, priced flat by owner decision.
        ["ROUND_TEA_PARTY"] = new RoundTeaPartyHandler(),
        // Three deck edits whose options carry no usable tag or tip.
        ["WOOD_CARVINGS"] = new WoodCarvingsHandler(),
        // The map is a Quest card: unplayable in combat, and its whole payoff is
        // the gold and the guaranteed treasure node it buys out of combat.
        ["THE_LEGENDS_WERE_TRUE"] = new LegendsWereTrueHandler(),
    };

    /// <summary>Registered handlers, for the drift check against the baked ids.</summary>
    internal static IReadOnlyDictionary<string, IEventSpecialHandler> Registered => Handlers;

    internal static bool Handles(string eventId) => Handlers.ContainsKey(eventId);

    /// <summary>
    /// A handler's index, -1 when it deliberately waits for a later page, or null
    /// to defer to the generic scorer.
    /// </summary>
    internal static int? TryChoose(Player player, EventModel eventModel)
    {
        if (!Handlers.TryGetValue(eventModel.Id.Entry, out var handler)) return null;
        try
        {
            var choice = handler.Choose(player, eventModel);
            if (choice is not { } index) return null;
            if (index < 0) return -1;
            if (index >= eventModel.CurrentOptions.Count) return null;
            var option = eventModel.CurrentOptions[index];
            // A handler may never commit a locked or already-resolved option.
            return option.IsLocked || option.WasChosen ? null : index;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A handler's one-line account of the decision, or "" when it has none. Kept
    /// here so a new handler can be explained without editing the log call site.
    /// </summary>
    internal static string Explain(Player player, EventModel model)
    {
        if (model is AncientEventModel ancient) return AncientOptionValue.Explain(player, ancient);
        if (Handlers.TryGetValue(model.Id.Entry, out var handler) && handler is IEventTradeExplainer explainer)
        {
            try { return explainer.Explain(player, model); } catch { return ""; }
        }
        return model switch
        {
            RoomFullOfCheese => RoomFullOfCheeseHandler.Explain(player, model),
            RelicTrader => RelicTraderHandler.Explain(player, model),
            PunchOff => $"team-min-hp={PunchOffHandler.WeakestHealth(player):P0} challenge-reserve=80%",
            DoorsOfLightAndDark => DoorsOfLightAndDarkHandler.Explain(player, model),
            _ => "",
        };
    }

    // Only selectable options: a locked exit must read as "no such option", not
    // as a choice the handler can make.
    internal static int IndexOf(EventModel eventModel, string textSuffix)
    {
        for (var index = 0; index < eventModel.CurrentOptions.Count; index++)
        {
            var option = eventModel.CurrentOptions[index];
            if (!option.IsLocked && !option.WasChosen
                && option.TextKey.EndsWith(textSuffix, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }
}

internal static class RelicTraderHandler
{
    internal static string Explain(Player player, EventModel model) => string.Join(";", model.CurrentOptions
        .Where(o => !o.IsLocked && !o.WasChosen)
        .Select(o => o.TextKey.Split('.').Last() + ":" + EventTrade.Grants(player, model, o).Detail));
}

/// <summary>
/// Reflections offers one page: touch the mirror (downgrade two random cards,
/// upgrade four) or shatter it (clone the whole deck, then add a curse).
/// </summary>
internal sealed class ReflectionsHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["TOUCH_A_MIRROR", "SHATTER"];

    public int? Choose(Player player, EventModel eventModel)
    {
        // Cloning every card doubles the junk along with the payoff, adds a
        // permanent curse and halves the odds of drawing anything the deck was
        // built around. That is not a trade the option data can express, and a
        // real run was lost to it: the bots duplicated 28/31/30-card decks into
        // 57/63/61 right before the act-3 elite and both bosses. Always take the
        // guaranteed mirror instead.
        var mirror = EventHandlers.IndexOf(eventModel, "TOUCH_A_MIRROR");
        return mirror >= 0 ? mirror : null;
    }
}

/// <summary>
/// The divination board opens its screen only for the local player, so a bot
/// never plays it: the event would finish with nothing collected. The bot's
/// board is driven separately (<see cref="CrystalSphereSync"/>), which leaves the
/// entry choice as "spend gold" versus "take a permanent Debt curse". The gold
/// option is taken, because a lasting curse costs the deck every remaining
/// fight while the extra reveals only buy a few more grid items.
/// </summary>
internal sealed class CrystalSphereHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["UNCOVER_FUTURE", "PAYMENT_PLAN"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var gold = EventHandlers.IndexOf(eventModel, "UNCOVER_FUTURE");
        return gold >= 0 ? gold : null;
    }
}

/// <summary>
/// Tablet of Truth's price doubles each page and can reduce Max HP. Verifying
/// the accumulated spend is spread across pages, so it stays a handler.
/// </summary>
internal sealed class TabletOfTruthHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["DECIPHER", "DECIPHER_1", "SMASH", "GIVE_UP"];

    public int? Choose(Player player, EventModel eventModel)
    {
        if (eventModel is not TabletOfTruth tablet) return null;
        var decipher = EventHandlers.IndexOf(eventModel, "DECIPHER");
        var decipherFirst = EventHandlers.IndexOf(eventModel, "DECIPHER_1");
        var smash = EventHandlers.IndexOf(eventModel, "SMASH");
        var giveUp = EventHandlers.IndexOf(eventModel, "GIVE_UP");
        var decipherIndex = decipher >= 0 ? decipher : decipherFirst;
        var stopIndex = smash >= 0 ? smash : giveUp;
        if (decipherIndex < 0) return stopIndex >= 0 ? stopIndex : null;

        var cost = tablet.DynamicVars["DecipherMaxHpLoss"].IntValue;
        var healing = smash >= 0 ? tablet.DynamicVars["SmashHPGain"].IntValue : 0;
        if (eventModel.CurrentOptions[decipherIndex].WillKillPlayer?.Invoke(player) != true
            && BotEventDriver.ShouldDecipherTablet(player, cost, healing)) return decipherIndex;
        // A temporarily unavailable exit is a reason to wait, never to sacrifice
        // HP. Waiting is -1, not "defer": the generic scorer would happily take
        // the bad deal this handler exists to refuse.
        return stopIndex >= 0 ? stopIndex : -1;
    }
}

/// <summary>
/// Potion Courier offers three Foul Potions or one real potion. A Foul Potion
/// damages every non-pet creature including its own drinker, so "grab potions"
/// is a trap: it fills slots with something that hurts the team. Always ransack.
/// </summary>
internal sealed class PotionCourierHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["GRAB_POTIONS", "RANSACK"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var ransack = EventHandlers.IndexOf(eventModel, "RANSACK");
        return ransack >= 0 ? ransack : null;
    }
}

/// <summary>
/// Morphic Grove asks for free +5 Max HP or for every coin the player owns in
/// exchange for two random transforms. Gold buys removals and relics, which is
/// worth more than two random cards, so the free option is taken unless the
/// player has almost nothing to lose.
/// </summary>
internal sealed class MorphicGroveHandler : IEventSpecialHandler
{
    // Below this the gold is not buying anything anyway, so the transforms are
    // the better half of the trade.
    private const int SpareGold = 120;

    public IReadOnlyList<string> OptionIds => ["GROUP", "LONER"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var loner = EventHandlers.IndexOf(eventModel, "LONER");
        var group = EventHandlers.IndexOf(eventModel, "GROUP");
        if (eventModel is MorphicGrove && group >= 0 && loner >= 0)
        {
            var value = Building.TransformPlan.EventValue(player, eventModel, eventModel.CurrentOptions[group]);
            var gain = (double)eventModel.DynamicVars.MaxHp.BaseValue;
            return value > EventTrade.MaxHpChanged(gain) + EventTrade.Healed(player, gain) ? group : loner;
        }
        if (player.Gold <= SpareGold) return group >= 0 ? group : loner >= 0 ? loner : null;
        return loner >= 0 ? loner : null;
    }
}

/// <summary>
/// ROUND_TEA_PARTY offers two relics: ENJOY_TEA hands over RoyalPoison (FIXED,
/// not random — EnjoyTea runs RelicCmd.Obtain<RoyalPoison>) plus a full heal, and
/// PICK_FIGHT costs one hit for a random relic. RoyalPoison damages its owner for
/// 4 unblockable on every combat's first turn, so over a run it is a large net
/// loss, and before this both options priced at the bare 5.0 — the tie fell to
/// page order and every seat took the poison (measured live 2026-10-01).
///
/// OWNER DECISION 2026-10-01: do not model it. Price the poison option flat and
/// always take the fight while it is survivable; the only reason to accept the
/// poison is a fight that would kill, which is the same rule the other traps
/// follow. The ranking line therefore shows ENJOY_TEA at 5.0-100 = -95.
/// </summary>
internal sealed class RoundTeaPartyHandler : IEventSpecialHandler, IEventTradeExplainer
{
    /// Flat price of the RoyalPoison option: a deliberate placeholder, not a
    /// measurement. Its job is to lose to a survivable PICK_FIGHT every time.
    internal const double PoisonOptionPenalty = -100;

    public IReadOnlyList<string> OptionIds => ["CONTINUE_FIGHT", "ENJOY_TEA", "PICK_FIGHT"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var fight = EventHandlers.IndexOf(eventModel, "PICK_FIGHT");
        var tea = EventHandlers.IndexOf(eventModel, "ENJOY_TEA");
        // The follow-up page after PICK_FIGHT has only CONTINUE_FIGHT.
        if (fight < 0)
        {
            var cont = EventHandlers.IndexOf(eventModel, "CONTINUE_FIGHT");
            if (cont >= 0) return cont;
            return tea >= 0 ? tea : null;
        }
        // Take the hit unless it would actually kill. This is the whole rule: the
        // poison option is priced flat, so the fight wins whenever it is legal.
        if (eventModel.CurrentOptions[fight].WillKillPlayer?.Invoke(player) == true)
            return tea >= 0 ? tea : null;
        return fight;
    }

    public string Explain(Player player, EventModel model)
        => $"enjoy_tea={PoisonOptionPenalty:F0}(固有RoyalPoison:每战首回合自伤4) pick_fight=可承受则取";
}

/// <summary>
/// WOOD_CARVINGS offers three deck edits and the generic scorer priced NONE of
/// them correctly. BIRD and TORUS transform one BASIC starter into a fixed named
/// card, but the game tags them with the RESULT's hover tip rather than
/// StaticHoverTip.Transform, so the effect-blind transform detector misses them
/// and they were priced as free additions of a card that is often a NEGATIVE
/// addition. SNAKE enchants any card with Slither, whose hover tip carries an
/// EnchantmentModel and is therefore invisible to EventTrade.Grants.
///
/// Measured live 2026-10-01: all four seats chose SNAKE at exactly 5.0 (the bare
/// base), beating BIRD 3.8/3.4/3.5/-1.5 and TORUS 2.7/2.2/1.8/-1.4 — SNAKE won by
/// default, not by merit, and the enchant was then applied to a 1-cost card, which
/// Slither makes strictly WORSE (cost 1 → expected 1.5).
/// </summary>
internal sealed class WoodCarvingsHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds => ["BIRD", "SNAKE", "SNAKE_LOCKED", "TORUS"];

    internal static (double Bird, double Snake, double Torus) Price(Player player, EventModel model)
    {
        return (Building.FixedTransform.WoodValue(player, "PECK"), EventTrade.BestSlitherGain(player),
            Building.FixedTransform.WoodValue(player, "TORIC_TOUGHNESS"));
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        var bird = EventHandlers.IndexOf(eventModel, "BIRD");
        var snake = EventHandlers.IndexOf(eventModel, "SNAKE");
        var torus = EventHandlers.IndexOf(eventModel, "TORUS");
        var (birdValue, snakeValue, torusValue) = Price(player, eventModel);
        int? best = null;
        var bestValue = double.NegativeInfinity;
        // Walk the LIVE page so a tie cannot be settled by this method's order.
        foreach (var (index, value) in new[] { (bird, birdValue), (snake, snakeValue), (torus, torusValue) })
            if (index >= 0 && !EnchantmentEventPolicy.ShouldAvoid(player, eventModel, eventModel.CurrentOptions[index])
                && value > bestValue) { bestValue = value; best = index; }
        return best;
    }

    public string Explain(Player player, EventModel model)
    {
        var (bird, snake, torus) = Price(player, model);
        return $"bird={bird:F1} snake={snake:F1} torus={torus:F1}";
    }
}

/// <summary>
/// ZEN_WEAVER is a pure gold-for-deck-edits menu: pay for two Enlightenment
/// (BREATHING_TECHNIQUES), for one card removed (EMOTIONAL_AWARENESS), or for two
/// removed (ARACHNID_ACUPUNCTURE). Only the Enlightenment option carries a hover
/// tip; the two removal options carry NONE, so the generic scorer gave all three
/// the same base 5.0 and the page order decided which edit the party bought —
/// measured live 2026-09-30, all four seats took EMOTIONAL_AWARENESS with no
/// number behind the choice. Both sides are now priced in the one unit.
/// </summary>
internal sealed class ZenWeaverHandler : IEventSpecialHandler, IEventTradeExplainer
{
    public IReadOnlyList<string> OptionIds =>
        ["ARACHNID_ACUPUNCTURE", "BREATHING_TECHNIQUES", "EMOTIONAL_AWARENESS", "LOCKED"];

    // Option id -> (cost DynamicVar, cards the deck sheds).
    private static readonly (string Option, string CostVar, int Removed)[] Choices =
    [
        ("BREATHING_TECHNIQUES", "BreathingTechniquesCost", 0),
        ("EMOTIONAL_AWARENESS", "EmotionalAwarenessCost", 1),
        ("ARACHNID_ACUPUNCTURE", "ArachnidAcupunctureCost", 2),
    ];

    internal static double Price(Player player, EventModel model, string option, string costVar, int removed)
    {
        var index = EventHandlers.IndexOf(model, option);
        if (index < 0) return double.NegativeInfinity;
        // What the option hands over (Enlightenment is on the hover tips) plus the
        // cards it lets the deck shed, minus the gold it costs.
        var granted = EventTrade.Grants(player, model, model.CurrentOptions[index]).Value;
        var shed = removed > 0 ? EventTrade.BestRemoval(player, removed) : 0;
        return granted + shed - EventTrade.Gold(EventTrade.Var(model, costVar));
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        int? best = null;
        var bestValue = double.NegativeInfinity;
        // Walk the LIVE page so the result cannot depend on the order this method
        // happens to list the three choices in.
        foreach (var (option, costVar, removed) in Choices)
        {
            var index = EventHandlers.IndexOf(eventModel, option);
            if (index < 0) continue;
            var value = Price(player, eventModel, option, costVar, removed);
            if (value > bestValue) { bestValue = value; best = index; }
        }
        return best;
    }

    public string Explain(Player player, EventModel model)
        => string.Join(" ", Choices.Select(c => $"{c.Option}={Price(player, model, c.Option, c.CostVar, c.Removed):F1}"));
}

/// <summary>
/// Abyssal Baths trades health for permanent Max HP, and the damage grows with
/// every soak. Soaking is right while the hit is comfortably survivable and
/// wrong once it would leave the player in danger, so the exit is taken on a
/// health floor rather than on the option's wording.
/// </summary>
internal sealed class AbyssalBathsHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["IMMERSE", "LINGER", "ABSTAIN", "EXIT_BATHS"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var soak = -1;
        foreach (var suffix in new[] { "IMMERSE", "LINGER" })
        {
            soak = EventHandlers.IndexOf(eventModel, suffix);
            if (soak >= 0) break;
        }
        var exit = -1;
        foreach (var suffix in new[] { "ABSTAIN", "EXIT_BATHS" })
        {
            exit = EventHandlers.IndexOf(eventModel, suffix);
            if (exit >= 0) break;
        }
        if (soak < 0) return null;
        if (eventModel.CurrentOptions[soak].WillKillPlayer?.Invoke(player) == true) return exit >= 0 ? exit : null;
        var maxHp = Math.Max(1, player.Creature.MaxHp);
        var damage = Damage(eventModel);
        // A soak only ever costs a few hit points and grants permanent Max HP, so
        // when the damage cannot be read the healthy keep going and the hurt stop.
        var survivable = damage > 0
            ? player.Creature.CurrentHp - damage >= maxHp * 0.35
            : player.Creature.CurrentHp >= maxHp * 0.5;
        return survivable ? soak : exit >= 0 ? exit : null;
    }

    // The event publishes the current soak damage; zero means it could not be
    // read, which the caller handles conservatively rather than by refusing.
    private static int Damage(EventModel eventModel)
    {
        try { return eventModel.DynamicVars["Damage"].IntValue; }
        catch { return 0; }
    }
}

/// <summary>
/// Colossal Flower pays 35, 75 or 135 gold (or a relic) for reaching deeper, and
/// each reach costs a small amount of unblockable damage. Taking the prize
/// immediately is the safe line the generic scorer falls into, because the
/// option ids carry no wording to score. Going deeper is worth it while the
/// player can absorb the next hit and still have health left to fight with.
/// </summary>
internal sealed class ColossalFlowerHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds =>
        ["EXTRACT_CURRENT_PRIZE_1", "EXTRACT_CURRENT_PRIZE_2", "EXTRACT_INSTEAD", "POLLINOUS_CORE",
         "REACH_DEEPER_1", "REACH_DEEPER_2"];

    public int? Choose(Player player, EventModel eventModel)
    {
        // The relic at the end of the dig beats the last gold step, so it is
        // taken whenever the player can still afford the hit.
        var relic = EventHandlers.IndexOf(eventModel, "POLLINOUS_CORE");
        var deeper = -1;
        for (var step = 1; step <= 3 && deeper < 0; step++)
            deeper = EventHandlers.IndexOf(eventModel, "REACH_DEEPER_" + step);
        var prize = -1;
        for (var step = 1; step <= 3 && prize < 0; step++)
            prize = EventHandlers.IndexOf(eventModel, "EXTRACT_CURRENT_PRIZE_" + step);
        if (prize < 0) prize = EventHandlers.IndexOf(eventModel, "EXTRACT_INSTEAD");

        var dive = relic >= 0 ? relic : deeper;
        if (dive < 0) return null;
        var floor = Math.Max(1, player.Creature.MaxHp * 0.4);
        return player.Creature.CurrentHp >= floor ? dive : prize >= 0 ? prize : null;
    }
}

/// <summary>
/// Bounded native-qualified candidate: value the actual physical card removed at each
/// already-supported, finite reroll endpoint and pay every preceding hold.
/// This is a bounded policy estimate, not a complete event/win-rate solver.
/// </summary>
internal sealed class SlipperyBridgeHandler : IEventSpecialHandler, IEventTradeExplainer
{
    private const double HoldOnHealthFloor = 0.35;

    public IReadOnlyList<string> OptionIds =>
        ["OVERCOME", "HOLD_ON_0", "HOLD_ON_1", "HOLD_ON_2", "HOLD_ON_3", "HOLD_ON_4",
         "HOLD_ON_5", "HOLD_ON_6", "HOLD_ON_LOOP"];

    public int? Choose(Player player, EventModel eventModel)
    {
        if (eventModel is not SlipperyBridge bridge) return null;
        var overcome = EventHandlers.IndexOf(eventModel, "OVERCOME");
        if (overcome < 0) return null;
        if (AtRisk(bridge.CurrentOptions[overcome]) is not { } atRisk) return null;

        var deck = player.Deck.Cards.ToArray();
        var physical = deck.Any(card => ReferenceEquals(card, atRisk));
        var protectedRisk = physical && Protected(atRisk, deck);
        if (IsFiller(atRisk) && !protectedRisk) return overcome;

        var holdOn = HoldOnIndex(bridge);
        if (holdOn < 0) return overcome;
        var option = bridge.CurrentOptions[holdOn];
        if (option.WillKillPlayer?.Invoke(player) == true) return overcome;
        var cost = HoldOnCost(bridge);
        if (cost <= 0 || player.Creature.CurrentHp - cost < player.Creature.MaxHp * HoldOnHealthFloor)
            return overcome;

        // A missing physical instance is not a zero-valued removal. Preserve
        // the prior bounded safe-hold fallback; never substitute a same-ID copy.
        if (!physical || !ReferenceEquals(bridge.Owner, player)) return holdOn;
        var outcomes = PricedOutcomes(player, bridge, option, atRisk, deck, cost);
        if (outcomes.Count <= 1) return holdOn;
        if (CoopBots.Building.ConstructionDecision.AwaitingValues) return -1;

        // The existing removal planner protects the last block/aoe/energy/draw
        // role while any unprotected endpoint is available. The HP floor and
        // growing cumulative charge still bound how far this handler may hold.
        var best = outcomes.OrderBy(outcome => outcome.Protected)
            .ThenByDescending(outcome => outcome.Value)
            .ThenBy(outcome => outcome.Holds)
            .First();
        return best.Holds == 0 ? overcome : holdOn;
    }

    public string Explain(Player player, EventModel model)
    {
        if (model is not SlipperyBridge bridge) return "";
        var overcome = EventHandlers.IndexOf(bridge, "OVERCOME");
        var holdOn = HoldOnIndex(bridge);
        if (overcome < 0 || AtRisk(bridge.CurrentOptions[overcome]) is not { } atRisk) return "bridge unreadable-risk";
        var deck = player.Deck.Cards.ToArray();
        if (!deck.Any(card => ReferenceEquals(card, atRisk)) || !ReferenceEquals(bridge.Owner, player))
            return "bridge physical-reference-unavailable bounded-legacy-fallback";
        // Choose and Explain run in the same construction delegate. A cheap
        // terminal decision must not schedule deck simulations just to log it.
        if (IsFiller(atRisk) && !Protected(atRisk, deck)) return $"bridge risk={atRisk.Id.Entry} unprotected-filler";
        if (holdOn < 0) return $"bridge risk={atRisk.Id.Entry} no-hold";
        var option = bridge.CurrentOptions[holdOn];
        if (option.WillKillPlayer?.Invoke(player) == true) return $"bridge risk={atRisk.Id.Entry} lethal-hold";
        var cost = HoldOnCost(bridge);
        if (cost <= 0 || player.Creature.CurrentHp - cost < player.Creature.MaxHp * HoldOnHealthFloor)
            return $"bridge risk={atRisk.Id.Entry} health-floor";
        var outcomes = PricedOutcomes(player, bridge, option, atRisk, deck, cost);
        return "bridge bounded-endpoints=" + string.Join(";", outcomes.Select(outcome =>
            $"{outcome.Card.Id.Entry}+{outcome.Card.CurrentUpgradeLevel}:holds={outcome.Holds},hp={outcome.HealthCost},"
            + $"remove={outcome.RemovalValue:F2},net={outcome.Value:F2},protected={outcome.Protected}"));
    }

    private readonly record struct RemovalOutcome(CardModel Card, int Holds, int HealthCost,
        double RemovalValue, double Value, bool Protected);

    private static IReadOnlyList<RemovalOutcome> PricedOutcomes(Player player, SlipperyBridge bridge,
        EventOption holdOn, CardModel atRisk, IReadOnlyList<CardModel> deck, int firstCost)
    {
        var outcomes = new List<RemovalOutcome>();
        void Add(CardModel card, int holds, int hp)
        {
            var removal = CoopBots.Building.BuildValue.RemoveForDecision(card, player, deck).Total;
            if (!double.IsFinite(removal)) throw new InvalidOperationException("Bridge removal price is not finite");
            outcomes.Add(new(card, holds, hp, removal, removal - EventTrade.HealthLost(hp), Protected(card, deck)));
        }
        Add(atRisk, 0, 0);
        if (firstCost <= 0) return outcomes;
        // Existing Foreseer facade preserves actual deck references, uses a
        // cloned event RNG, and applies native skipped/type/filter/fallback rules.
        // Its already-configured preview count is the sole horizon; no new search
        // pool or construction precision is introduced here.
        var preview = CoopBots.Kernel.OutOfCombatPredictions.EventOption(bridge, holdOn).Cards;
        var paid = 0;
        for (var index = 0; index < preview.Count; index++)
        {
            // Native HoldOn increases the next HpLoss by one after every paid
            // choice. Every candidate is a removal AFTER all these payments.
            paid = checked(paid + firstCost + index);
            if (player.Creature.CurrentHp - paid < player.Creature.MaxHp * HoldOnHealthFloor
                || player.Creature.CurrentHp <= paid) break;
            var card = preview[index];
            if (!card.IsRemovable || !deck.Any(held => ReferenceEquals(held, card))) break;
            Add(card, index + 1, paid);
        }
        return outcomes;
    }

    private static bool Protected(CardModel card, IReadOnlyList<CardModel> deck)
        => BotShopPlanner.IsNeverRemoved(card) || CoopBots.Building.BuildValue.RemovalProtection(card, deck).Protected;

    internal static bool IsFiller(CardModel card)
        => (card.IsBasicStrikeOrDefend && card.CurrentUpgradeLevel == 0
            && card.Enchantment is null && card.GetEnchantedReplayCount() <= 0)
            || card.Type is CardType.Curse or CardType.Status;

    // The real hover tip retains its mutable card; CanonicalModel discards the
    // upgrade, enchantment, physical duplicate and exact Deck reference.
    private static CardModel? AtRisk(EventOption overcome)
        => overcome.HoverTips.OfType<MegaCrit.Sts2.Core.HoverTips.CardHoverTip>()
            .Select(tip => tip.Card).FirstOrDefault();

    private static int HoldOnIndex(EventModel eventModel)
    {
        for (var index = 0; index < eventModel.CurrentOptions.Count; index++)
        {
            var option = eventModel.CurrentOptions[index];
            if (!option.IsLocked && !option.WasChosen
                && option.TextKey.Contains("options.HOLD_ON_", StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static int HoldOnCost(EventModel eventModel)
    {
        try { return eventModel.DynamicVars["HpLoss"].IntValue; }
        catch { return 0; }
    }
}
