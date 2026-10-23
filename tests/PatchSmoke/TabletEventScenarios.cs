using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class TabletEventScenarios
{
    internal static void Run()
    {
        CheeseEventScenarios.Run();
        var choose = typeof(BotEventDriver).GetMethod("ChooseEventOption", BindingFlags.Static | BindingFlags.NonPublic)!;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Tablet regression: " + message);
        }
        foreach (var difficulty in Enum.GetValues<BotDifficulty>())
        {
            // Headless fixtures lack the save services required by starting relics.
            var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(difficulty, 1, 1));
            typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(player, ModelDb.Character<Ironclad>());
            var run = RunState.CreateForTest(new[] { player }, seed: "TABLET-SAFETY");
            player.MaxEnergy = 3; // Deprived's 100-energy mock is not a native run budget.
            var combat = new CombatState(runState: run);
            foreach (var card in player.Deck.Cards.ToList()) player.Deck.RemoveInternal(card);
            player.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(player));
            var tablet = ModelDb.Event<TabletOfTruth>().ToMutable();
            int Choice(int cost, int maxHp, int hp, bool initial = false, bool reversed = false, bool lockedExit = false)
            {
                player.Creature.SetMaxHpInternal(maxHp); player.Creature.SetCurrentHpInternal(hp);
                tablet.DynamicVars["DecipherMaxHpLoss"].BaseValue = cost;
                EventOption Option(string suffix, bool locked = false) => new(tablet,
                    locked ? null : () => Task.CompletedTask, new LocString("events", "test.title"),
                    new LocString("events", "test.description"), "TABLET_OF_TRUTH.pages.TEST.options." + suffix, Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>());
                var options = new List<EventOption> { Option(initial ? "DECIPHER_1" : "DECIPHER"), Option(initial ? "SMASH" : "GIVE_UP", lockedExit) };
                if (reversed) options.Reverse();
                typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tablet, options);
                return (int)choose.Invoke(null, new object[] { player, tablet })!;
            }
            Check(Choice(3, 80, 80, initial: true) == 0, "Healthy Ironclad may buy the first cheap upgrade.");
            Check(Choice(6, 77, 77) == 0, "Second cheap upgrade remains affordable with a healthy basic-card pool.");
            Check(Choice(12, 71, 71) == 1, "Stop at 71 MaxHp before the escalating third purchase.");
            Check(Choice(24, 59, 59) == 1, "Never repeat the reported 59-to-35 sacrifice.");
            Check(Choice(34, 35, 35) == 1, "Never reduce the saved 35-MaxHp bot to one.");
            Check(Choice(3, 80, 60, initial: true) == 1, "Prefer the available 20 healing when injured.");
            Check(Choice(6, 77, 30) == 1, "Low current HP must stop further permanent sacrifices.");
            Check(Choice(3, 58, 58, initial: true) == 1, "Respect the character-relative MaxHp floor.");
            Check(Choice(6, 56, 56) == 1, "Reject excessive cumulative spending on an already reduced character.");
            Check(Choice(12, 71, 71, reversed: true) == 0, "Choose by action identity, not option order.");
            Check(Choice(12, 71, 71, lockedExit: true) == -1, "Wait rather than choose an unsafe action when exit is locked.");
            Check(Choice(7, 80, 80) == 1, "Unknown price schedules must fail safely.");
            BotEventDriver.Reset();
            Check(Choice(12, 71, 71) == 1, "Reload/reset cannot erase the cumulative-spending safeguard.");
            foreach (var card in player.Deck.Cards) card.UpgradeInternal();
            Check(Choice(3, 80, 80, initial: true) == 1, "Never pay when all cards are already upgraded.");
            player.Deck.RemoveInternal(player.Deck.Cards.Single());
            Check(Choice(3, 80, 80, initial: true) == 1, "Never pay with an empty deck.");
            Check(player.Creature.MaxHp == 80 && player.Creature.CurrentHp == 80,
                "Decision evaluation must not alter real HP.");
        }
        Console.WriteLine("PASS: TabletOfTruth escalating cost, cumulative budget, 35-to-1 prevention, healing, deck exhaustion, option order, reload and all bot difficulties.");

        // A single-answer page must be answered exactly once. The chosen option
        // is marked before its animation finishes, and while that runs the page
        // still shows both options; picking the other one takes a mutually
        // exclusive option (Reflections.Shatter duplicated a whole deck and
        // added a curse on every bot).
        var reflections = ModelDb.Event<Reflections>().ToMutable();
        var page = new List<EventOption>
        {
            new(reflections, () => Task.CompletedTask, new LocString("events", "test.title"),
                new LocString("events", "test.description"),
                "REFLECTIONS.pages.INITIAL.options.TOUCH_A_MIRROR", Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>()),
            new(reflections, () => Task.CompletedTask, new LocString("events", "test.title"),
                new LocString("events", "test.description"),
                "REFLECTIONS.pages.INITIAL.options.SHATTER", Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>()),
        };
        typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(reflections, page);
        var taken = new HashSet<EventOption>(ReferenceEqualityComparer.Instance);
        Check(BotEventDriver.ShouldAnswer(reflections, page[1], taken),
            "A fresh event page must be answerable.");
        typeof(EventOption).GetField("<WasChosen>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page[0], true);
        Check(!BotEventDriver.ShouldAnswer(reflections, page[1], taken),
            "While the chosen option resolves, the mutually exclusive option must not be taken.");
        Console.WriteLine("PASS: an answered event page is not answered again (Reflections cannot duplicate the deck twice).");

        // Reflections gets a handler: cloning the whole deck for a curse is not a
        // trade any option data expresses, so no generic rule can refuse it.
        // Asserted in both orders to prove it is chosen by identity, not index.
        var reflectionsPlayer = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 7, 1));
        RunState.CreateForTest(new[] { reflectionsPlayer }, seed: "REFLECTIONS-HANDLER");
        foreach (var reversed in new[] { false, true })
        {
            var eventModel = ModelDb.Event<Reflections>().ToMutable();
            EventOption Mirror() => new(eventModel, () => Task.CompletedTask, new LocString("events", "test.title"),
                new LocString("events", "test.description"),
                "REFLECTIONS.pages.INITIAL.options.TOUCH_A_MIRROR", Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>());
            EventOption Shatter() => new(eventModel, () => Task.CompletedTask, new LocString("events", "test.title"),
                new LocString("events", "test.description"),
                "REFLECTIONS.pages.INITIAL.options.SHATTER", Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>());
            var options = reversed
                ? new List<EventOption> { Shatter(), Mirror() }
                : new List<EventOption> { Mirror(), Shatter() };
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(eventModel, options);
            var picked = (int)choose.Invoke(null, new object[] { reflectionsPlayer, eventModel })!;
            Check(options[picked].TextKey.EndsWith("TOUCH_A_MIRROR", StringComparison.Ordinal),
                "Reflections must take the mirror, never clone the deck for a curse.");
        }
        Console.WriteLine("PASS: Reflections takes the guaranteed mirror in either option order.");

        // The generic scorer must price a curse even when the option's key never
        // mentions one (LostWisp's CLAIM reads as a plain gain and adds Decay).
        foreach (var reversed in new[] { false, true })
        {
            var eventModel = ModelDb.Event<Reflections>().ToMutable();
            EventOption Plain(string key) => new(eventModel, () => Task.CompletedTask, new LocString("events", "test.title"),
                new LocString("events", "test.description"), "EVENT.pages.INITIAL.options." + key,
                Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>());
            EventOption WithCurse(string key) => new(eventModel, () => Task.CompletedTask, new LocString("events", "test.title"),
                new LocString("events", "test.description"), "EVENT.pages.INITIAL.options." + key,
                new[] { MegaCrit.Sts2.Core.HoverTips.HoverTipFactory.FromCard<Decay>() });
            var cursed = WithCurse("TAKE_IT");
            var plain = Plain("LEAVE_IT");
            var options = reversed
                ? new List<EventOption> { plain, cursed }
                : new List<EventOption> { cursed, plain };
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(eventModel, options);
            var picked = (int)choose.Invoke(null, new object[] { reflectionsPlayer, eventModel })!;
            Check(ReferenceEquals(options[picked], plain),
                "An option that hands over a curse must lose to one that does not.");
        }
        Console.WriteLine("PASS: the generic event scorer prices a curse from the option's own data, not its key.");

        // Divination: the board only opens for the local player, so the entry
        // choice is "spend gold" against "take a permanent Debt curse". The bot
        // must never trade a lasting curse for a few extra reveals.
        // The real CrystalSphere model cannot be built headlessly (its canonical
        // vars need card titles), so the handler is exercised directly over a
        // carrier event; the registry entry is asserted separately.
        Check(EventHandlers.Handles("CRYSTAL_SPHERE"), "The divination event must have a registered handler.");
        foreach (var reversed in new[] { false, true })
        {
            var sphere = ModelDb.Event<Reflections>().ToMutable();
            EventOption Option(string suffix) => new(sphere, () => Task.CompletedTask,
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                "CRYSTAL_SPHERE.pages.INITIAL.options." + suffix,
                Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>());
            var payGold = Option("UNCOVER_FUTURE");
            var payCurse = Option("PAYMENT_PLAN");
            var options = reversed
                ? new List<EventOption> { payCurse, payGold }
                : new List<EventOption> { payGold, payCurse };
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sphere, options);
            var picked = new CrystalSphereHandler().Choose(reflectionsPlayer, sphere);
            Check(picked is { } index && ReferenceEquals(options[index], payGold),
                "Divination must be paid with gold, never with a permanent Debt curse.");
        }
        Console.WriteLine("PASS: divination pays gold and never takes the Debt curse, in either option order.");

        // The fallback sweep must stay inside the board and never ask for more
        // reveals than the player has: it runs when the host's script is missing.
        var sweep = CrystalSphereSync.Sweep(11, 11, 3);
        Check(sweep.Count == 3, $"The sweep must use exactly the divinations available, got {sweep.Count}.");
        Check(sweep.All(step => step.X >= 0 && step.X < 11 && step.Y >= 0 && step.Y < 11),
            "The sweep must stay inside the board.");
        Check(sweep.All(step => step.Tool == CrystalSphereMinigame.CrystalSphereToolType.Big),
            "The sweep should use the big tool to cover the board.");
        Check(CrystalSphereSync.Sweep(11, 11, 6).Count == 6, "The sweep must scale with the divinations available.");
        Console.WriteLine("PASS: the divination fallback sweep is bounded by the board and the divinations available.");

        // Drift guard: a handler matches option *ids*, and a patch that renames
        // one would leave the handler silently dead. Every id a handler names
        // must exist in the baked event table for that event.
        Check(EventHandlers.Registered.Count >= 6, "The event handler registry looks truncated.");
        foreach (var entry in EventHandlers.Registered)
        {
            if (!BakedEvents.OptionIds.TryGetValue(entry.Key, out var known))
                throw new InvalidOperationException($"Regression: no baked option ids for handled event {entry.Key}.");
            foreach (var id in entry.Value.OptionIds)
                if (!known.Contains(id, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Regression: handler for {entry.Key} matches option id {id}, which this build's event data does not have.");
        }
        Console.WriteLine($"PASS: all {EventHandlers.Registered.Count} event handlers match option ids that exist in this build.");

        // Three option ids were added to the table by hand after diffing it against
        // the INSTALLED build's localisation keys (2026-09-29). A missing id is not
        // cosmetic — the guard above rejects a handler that names one, so the event
        // could never be modelled. Pin them so a regeneration that drops them fails.
        Check(BakedEvents.OptionIds["COLORFUL_PHILOSOPHERS"].Contains("EQUALITY")
            && BakedEvents.OptionIds["LOST_WISP"].Contains("CLAIM_LOCKED")
            && BakedEvents.OptionIds["SPIRALING_WHIRLPOOL"].Contains("REACH_IN"),
            "The option ids verified against the installed build must stay in the table.");
        // Pool-based events have no static ids: their options are synthesised from a
        // relic pool at runtime. Named here so "which events are unguarded" has an
        // answer, and kept handler-free so the guard never has to skip one silently.
        Check(BakedEvents.PoolBasedEvents.Count > 0,
            "Pool-based events must be named rather than silently absent from the table.");
        Check(!BakedEvents.PoolBasedEvents.Any(EventHandlers.Registered.ContainsKey),
            "A pool-based event cannot carry a handler: the id guard has nothing to check it against.");
        Console.WriteLine($"PASS: {BakedEvents.PoolBasedEvents.Count} pool-based events are named, and the "
            + "three build-verified option ids are pinned.");

        // Carrier event: the real event models build canonical vars that need card
        // titles, which the headless harness does not have.
        EventModel Carrier(params string[] suffixes)
        {
            var model = ModelDb.Event<Reflections>().ToMutable();
            var list = new List<EventOption>();
            foreach (var suffix in suffixes)
                list.Add(new EventOption(model, () => Task.CompletedTask, new LocString("events", "test.title"),
                    new LocString("events", "test.description"), "EVENT.pages.INITIAL.options." + suffix,
                    Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>()));
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, list);
            return model;
        }
        string Picked(IEventSpecialHandler handler, EventModel model)
        {
            var index = handler.Choose(reflectionsPlayer, model);
            return index is { } value && value >= 0 && value < model.CurrentOptions.Count
                ? model.CurrentOptions[value].TextKey : "(none)";
        }

        // Potion Courier: the Foul Potions it hands out damage their own drinker.
        var courier = Picked(new PotionCourierHandler(), Carrier("RANSACK", "GRAB_POTIONS"));
        Check(courier.EndsWith("RANSACK", StringComparison.Ordinal), $"Potion Courier must ransack, got {courier}.");
        Check(Picked(new PotionCourierHandler(), Carrier("GRAB_POTIONS", "RANSACK")).EndsWith("RANSACK", StringComparison.Ordinal),
            "Potion Courier must ransack in either option order.");
        Console.WriteLine("PASS: the potion courier ransacks instead of taking potions that hurt the drinker.");

        // Morphic Grove: "Group" spends every coin the player owns.
        reflectionsPlayer.Gold = 400;
        Check(Picked(new MorphicGroveHandler(), Carrier("GROUP", "LONER")).EndsWith("LONER", StringComparison.Ordinal),
            "Morphic Grove must keep the gold when there is gold worth keeping.");
        reflectionsPlayer.Gold = 60;
        Check(Picked(new MorphicGroveHandler(), Carrier("GROUP", "LONER")).EndsWith("GROUP", StringComparison.Ordinal),
            "Morphic Grove should take the transforms when the gold buys nothing anyway.");
        reflectionsPlayer.Gold = 0;
        Console.WriteLine("PASS: the morphic grove keeps gold that is worth keeping.");

        // Colossal Flower: the generic scorer takes the first prize because the
        // option ids carry no wording, forgoing the escalating one. Going deeper
        // is right while the player can still take a hit.
        reflectionsPlayer.Creature.SetMaxHpInternal(80);
        reflectionsPlayer.Creature.SetCurrentHpInternal(80);
        var deep = Picked(new ColossalFlowerHandler(), Carrier("EXTRACT_CURRENT_PRIZE_1", "REACH_DEEPER_1"));
        Check(deep.EndsWith("REACH_DEEPER_1", StringComparison.Ordinal),
            $"A healthy player should reach deeper for the bigger prize, got {deep}.");
        reflectionsPlayer.Creature.SetCurrentHpInternal(12);
        var shallow = Picked(new ColossalFlowerHandler(), Carrier("EXTRACT_CURRENT_PRIZE_1", "REACH_DEEPER_1"));
        Check(shallow.EndsWith("EXTRACT_CURRENT_PRIZE_1", StringComparison.Ordinal),
            $"A hurt player should take the prize and stop digging, got {shallow}.");
        Console.WriteLine("PASS: the colossal flower digs deeper while healthy and takes the prize when hurt.");

        // Abyssal Baths trades health for permanent Max HP with growing damage.
        // A live event is needed for the exact soak damage, so this exercises the
        // health-floor rule the handler falls back to when the damage is unknown.
        reflectionsPlayer.Creature.SetMaxHpInternal(80);
        reflectionsPlayer.Creature.SetCurrentHpInternal(80);
        Check(Picked(new AbyssalBathsHandler(), Carrier("IMMERSE", "ABSTAIN")).EndsWith("IMMERSE", StringComparison.Ordinal),
            "A healthy player should soak for the permanent Max HP.");
        reflectionsPlayer.Creature.SetCurrentHpInternal(10);
        Check(Picked(new AbyssalBathsHandler(), Carrier("IMMERSE", "ABSTAIN")).EndsWith("ABSTAIN", StringComparison.Ordinal),
            "A hurt player should stop soaking rather than risk the damage.");
        Console.WriteLine("PASS: the abyssal baths soak while healthy and stop once the health is needed.");

        // Doors of Light and Dark: the light door upgrades {Cards} cards AT RANDOM,
        // the dark door removes one card the player CHOOSES. Both option ids carry no
        // wording, so the generic scorer rated them equal and the page order decided —
        // measured live 2026-09-29 (seed 9F4Q3R2BV8HQ), all four seats spent the event
        // on two random upgrades. The random upgrade IS knowable: the event's own
        // generator is replayed on a cloned RNG. Price those two, not the deck's best.
        var doors = Player.CreateForNewRun<Deprived>(UnlockState.all, 9);
        doors.MaxEnergy = 3; // Deprived's 100-energy default is not an ordinary event deck.
        var doorsRun = RunState.CreateForTest(new[] { doors }, seed: "DOORS-LIGHT-DARK");
        var doorsCombat = new CombatState(runState: doorsRun);
        foreach (var card in doors.Deck.Cards.ToList()) doors.Deck.RemoveInternal(card);
        void AddDoor<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++) doors.Deck.AddInternal(doorsCombat.CreateCard<T>(doors));
        }
        AddDoor<StrikeIronclad>(6); AddDoor<DefendIronclad>(5);
        AddDoor<Bash>(1); AddDoor<Uppercut>(1);
        CardModel ByEntry(string id) => doors.Deck.Cards.First(card => card.Id.Entry == id);
        double SumOf(IEnumerable<CardModel> cards)
        {
            var selected = cards.ToHashSet();
            var after = doors.Deck.Cards.Select(card => selected.Contains(card)
                ? CardProfile.Upgraded(card, BuildValue.StableEnergy(doors))!.Value.Card : card).ToArray();
            return BuildValue.Deck(after, doors) - BuildValue.Deck(doors.Deck.Cards.ToArray(), doors);
        }
        // The worst hand the generator can deal: two basics.
        var handed = new[] { ByEntry("STRIKE_IRONCLAD"), ByEntry("DEFEND_IRONCLAD") };
        var bestTwo = doors.Deck.Cards.Where(card => card.IsUpgradable)
            .OrderByDescending(card => BuildValue.UpgradeDelta(card, doors).Total).Take(2).ToList();
        var doorsValue = DoorsOfLightAndDarkHandler.PriceWith(doors, handed);
        Check(Math.Abs(doorsValue.Light - SumOf(handed)) < 0.01,
            $"The light door must be priced from the cards it will ACTUALLY take: got {doorsValue.Light:F1}, "
            + $"expected {SumOf(handed):F1} for [{string.Join(",", handed.Select(c => c.Id.Entry))}].");
        Check(SumOf(bestTwo) > SumOf(handed) + 0.1,
            $"Fixture must distinguish the prediction from the best case ({SumOf(bestTwo):F1} vs {SumOf(handed):F1}).");
        // With the owner's -15 starter prior, these two BASIC upgrades (12.8)
        // lose to removing a basic (16.0). Preserve the actual-upgrade assertion
        // above and check the chosen value, rather than forcing the old policy.
        // R2 prior15-r2/door.log: reversing Decide's comparison exits 1:
        // "Tablet regression: The higher-valued door must win whichever order the page lists the two options in."
        var lightWins = doorsValue.Light > doorsValue.Dark;
        Check(DoorsOfLightAndDarkHandler.Decide(light: 0, dark: 1, doorsValue) == (lightWins ? 0 : 1)
            && DoorsOfLightAndDarkHandler.Decide(light: 1, dark: 0, doorsValue) == (lightWins ? 1 : 0),
            "The higher-valued door must win whichever order the page lists the two options in.");
        Console.WriteLine($"PASS: the light door is priced from the cards it will really upgrade "
            + $"({doorsValue.Light:F1}) versus the chosen removal ({doorsValue.Dark:F1}).");

        // T1: an option that grants a NAMED card must be priced by the reward-pick
        // model. The generic scorer gave every non-power card 0, so a card-granting
        // option could not be told from an empty one.
        var tipper = Player.CreateForNewRun<Deprived>(UnlockState.all, 11);
        var tipRun = RunState.CreateForTest(new[] { tipper }, seed: "EVENT-T1");
        var tipCombat = new CombatState(runState: tipRun);
        foreach (var card in tipper.Deck.Cards.ToList()) tipper.Deck.RemoveInternal(card);
        for (var i = 0; i < 5; i++) tipper.Deck.AddInternal(tipCombat.CreateCard<StrikeIronclad>(tipper));
        for (var i = 0; i < 4; i++) tipper.Deck.AddInternal(tipCombat.CreateCard<DefendIronclad>(tipper));
        var weakCard = tipCombat.CreateCard<DefendIronclad>(tipper);
        var strongCard = tipCombat.CreateCard<Bash>(tipper);
        var weakValue = BuildValue.Add(weakCard, tipper).Total;
        var strongValue = BuildValue.Add(strongCard, tipper).Total;
        Check(strongValue > weakValue + 0.01,
            $"Fixture must give the two cards different values ({strongValue:F1} vs {weakValue:F1}).");
        EventModel Tips(params (string Suffix, IHoverTip[] Tips)[] options)
        {
            var model = ModelDb.Event<Reflections>().ToMutable();
            var list = new List<EventOption>();
            foreach (var (suffix, tips) in options)
                list.Add(new EventOption(model, () => Task.CompletedTask, new LocString("events", "test.title"),
                    new LocString("events", "test.description"), "EVENT.pages.INITIAL.options." + suffix, tips));
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, list);
            return model;
        }
        // The BETTER card is listed second on purpose: with the old zero-for-cards
        // scorer the tie broke to index 0 and the worse card won.
        var giveaway = Tips(("WEAK", [new CardHoverTip(weakCard)]), ("STRONG", [new CardHoverTip(strongCard)]));
        var giveawayIndex = (int)choose.Invoke(null, new object[] { tipper, giveaway })!;
        Check(giveaway.CurrentOptions[giveawayIndex].TextKey.EndsWith("STRONG", StringComparison.Ordinal),
            $"A named card must be priced by the reward model: got "
            + $"{giveaway.CurrentOptions[giveawayIndex].TextKey} ({weakValue:F1} vs {strongValue:F1}).");
        Console.WriteLine($"PASS: a granted card is priced by the reward model "
            + $"({weakValue:F1} vs {strongValue:F1}), not scored zero.");

        // T3: the resource-trade events. The generic scorer has no signal for any of
        // these (no option id carries a keyword), so before this they were decided by
        // page order. Each is pinned on both sides of its own break-even.
        EventModel Trade<T>(params string[] suffixes) where T : EventModel
        {
            var model = ModelDb.Event<T>().ToMutable();
            var list = new List<EventOption>();
            foreach (var suffix in suffixes)
                list.Add(new EventOption(model, () => Task.CompletedTask, new LocString("events", "test.title"),
                    new LocString("events", "test.description"),
                    typeof(T).Name.ToUpperInvariant() + ".pages.INITIAL.options." + suffix,
                    Array.Empty<IHoverTip>()));
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, list);
            return model;
        }
        string PickedBy(IEventSpecialHandler handler, EventModel model, Player who)
        {
            var index = handler.Choose(who, model);
            return index is { } value && value >= 0 && value < model.CurrentOptions.Count
                ? model.CurrentOptions[value].TextKey : "(none)";
        }

        var statue = Trade<SunkenStatue>("DIVE_INTO_WATER", "GRAB_SWORD");
        // Preserve the fixture's stated surplus after the shared gold scale
        // changes: forty value points clearly cover the wound and the relic.
        statue.DynamicVars["Gold"].BaseValue = (decimal)(40 * BotShopPlanner.GoldPerDeckValue);
        statue.DynamicVars["HpLoss"].BaseValue = 7;
        Check(PickedBy(new SunkenStatueHandler(), statue, doors).EndsWith("DIVE_INTO_WATER", StringComparison.Ordinal),
            "Sunken Statue must dive when the gold clearly outweighs a seven-point wound: "
            + new SunkenStatueHandler().Explain(doors, statue) + ".");
        statue.DynamicVars["Gold"].BaseValue = 0;
        statue.DynamicVars["HpLoss"].BaseValue = 60;
        Check(PickedBy(new SunkenStatueHandler(), statue, doors).EndsWith("GRAB_SWORD", StringComparison.Ordinal),
            "Sunken Statue must take the relic rather than a fatal dive: "
            + new SunkenStatueHandler().Explain(doors, statue) + ".");

        var treasury = Trade<SunkenTreasury>("FIRST_CHEST", "SECOND_CHEST");
        treasury.DynamicVars["SmallChestGold"].BaseValue = 60;
        treasury.DynamicVars["LargeChestGold"].BaseValue = 333;
        Check(PickedBy(new SunkenTreasuryHandler(), treasury, doors).EndsWith("SECOND_CHEST", StringComparison.Ordinal),
            "Sunken Treasury must take the large chest when it pays for the Greed curse.");
        treasury.DynamicVars["LargeChestGold"].BaseValue = 66;
        Check(PickedBy(new SunkenTreasuryHandler(), treasury, doors).EndsWith("FIRST_CHEST", StringComparison.Ordinal),
            "Sunken Treasury must refuse a large chest that only just covers its curse.");

        var maze = Trade<JungleMazeAdventure>("JOIN_FORCES", "SOLO_QUEST");
        maze.DynamicVars["JoinForcesGold"].BaseValue = 50;
        maze.DynamicVars["SoloGold"].BaseValue = 150;
        maze.DynamicVars["SoloHp"].BaseValue = 18;
        Check(PickedBy(new JungleMazeAdventureHandler(), maze, doors).EndsWith("SOLO_QUEST", StringComparison.Ordinal),
            "Jungle Maze must take the larger purse when the wound is cheap.");
        maze.DynamicVars["SoloHp"].BaseValue = 140;
        Check(PickedBy(new JungleMazeAdventureHandler(), maze, doors).EndsWith("JOIN_FORCES", StringComparison.Ordinal),
            "Jungle Maze must share when the solo wound is unaffordable.");

        var thisOrThat = Trade<ThisOrThat>("ORNATE", "PLAIN");
        thisOrThat.DynamicVars["Gold"].BaseValue = 0;
        thisOrThat.DynamicVars["HpLoss"].BaseValue = 6;
        Check(PickedBy(new ThisOrThatHandler(), thisOrThat, doors).EndsWith("ORNATE", StringComparison.Ordinal),
            "This or That must take the relic when the plain side offers nothing.");
        thisOrThat.DynamicVars["Gold"].BaseValue = 90;
        Check(PickedBy(new ThisOrThatHandler(), thisOrThat, doors).EndsWith("PLAIN", StringComparison.Ordinal),
            "This or That must prefer the gold once it outweighs a relic minus the curse.");

        var unrest = Trade<UnrestSite>("KILL", "REST");
        unrest.DynamicVars["MaxHpLoss"].BaseValue = 8;
        doors.Creature.SetMaxHpInternal(80); doors.Creature.SetCurrentHpInternal(80);
        Check(PickedBy(new UnrestSiteHandler(), unrest, doors).EndsWith("KILL", StringComparison.Ordinal),
            "Unrest Site must trade Max HP for the relic when there is nothing to heal.");
        doors.Creature.SetCurrentHpInternal(20);
        Check(PickedBy(new UnrestSiteHandler(), unrest, doors).EndsWith("REST", StringComparison.Ordinal),
            "Unrest Site must heal a badly hurt player, curse and all.");

        var well = Trade<Wellspring>("BATHE", "BOTTLE");
        well.DynamicVars["BatheCurses"].BaseValue = 1;
        AddDoor<Injury>(); // Chosen removal now has a genuine curse to clean up.
        Check(PickedBy(new WellspringHandler(), well, doors).EndsWith("BATHE", StringComparison.Ordinal),
            "Wellspring must take the removal when a starter-heavy deck still has dead draws.");
        Console.WriteLine("PASS: the resource-trade events are decided on both sides of their break-even.");

        // T2: the RANDOM outcome is read from the game's own generator on a cloned
        // RNG, so a "random relic/potion/card" option is priced by what it will give
        // instead of being unknown. Probed here rather than assumed: the prediction
        // needs the event's owner, and a harness that cannot supply one must make the
        // handler fall back rather than decide on nothing.
        // EventModel.Owner is get-only; the prediction reads it, so the harness
        // must supply one explicitly (the live game sets it when the event starts).
        void SetOwner(EventModel model, Player? owner) => typeof(EventModel)
            .GetField("<Owner>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, owner);

        var automaton = ModelDb.Event<InfestedAutomaton>().ToMutable();
        SetOwner(automaton, doors);
        var automatonOptions = new List<EventOption>();
        foreach (var suffix in new[] { "STUDY", "TOUCH_CORE" })
            automatonOptions.Add(new EventOption(automaton, () => Task.CompletedTask,
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                "INFESTED_AUTOMATON.pages.INITIAL.options." + suffix, Array.Empty<IHoverTip>()));
        typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(automaton, automatonOptions);
        var study = EventTrade.Grants(doors, automaton, automaton.CurrentOptions[0]);
        var touch = EventTrade.Grants(doors, automaton, automaton.CurrentOptions[1]);
        // The harness draws from a mock card pool: STUDY's random Power resolves, while
        // TOUCH_CORE's zero-cost filter finds nothing there. So this pins the plumbing
        // (a prediction really is produced and priced) and the comparison (the option
        // whose predicted grant loses value is rejected), not the live card values.
        // 2026-10-03: an unknown MockPowerCard no longer gets a fabricated positive dividend.
        Check(study.Value < 0 && study.Detail.Length > 0,
            $"The RandomForeseer prediction must be priced: study={study.Value:F1}({study.Detail}) "
            + $"touch={touch.Value:F1}({touch.Detail})");
        var automatonPick = (int)choose.Invoke(null, new object[] { doors, automaton })!;
        Check(automaton.CurrentOptions[automatonPick].TextKey.EndsWith("TOUCH_CORE", StringComparison.Ordinal),
            $"A predicted harmful mock grant must not beat the neutral alternative: {study.Value:F1} vs {touch.Value:F1}.");

        // Brain Leech: RIP pays HP for a colourless card reward; SHARE takes a random
        // card for free. With both sides predicted the handler must compare the card
        // against the wound, not the option order.
        var leech = ModelDb.Event<BrainLeech>().ToMutable();
        SetOwner(leech, doors);
        leech.DynamicVars["RipHpLoss"].BaseValue = 5;
        var leechOptions = new List<EventOption>();
        foreach (var suffix in new[] { "RIP", "SHARE_KNOWLEDGE" })
            leechOptions.Add(new EventOption(leech, () => Task.CompletedTask,
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                "BRAIN_LEECH.pages.INITIAL.options." + suffix, Array.Empty<IHoverTip>()));
        typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(leech, leechOptions);
        var leechIndex = new BrainLeechHandler().Choose(doors, leech);
        Check(leechIndex is { } leechPick && leechPick >= 0,
            "Brain Leech must make a choice rather than defer.");
        Console.WriteLine($"PASS: T2 predicted grants (study={study.Value:F1}, touch={touch.Value:F1}); "
            + $"Brain Leech picked {leech.CurrentOptions[leechIndex!.Value].TextKey}.");

        // Owner policy 2026-10-04: Battleworn Dummy always picks the second
        // tier (two random upgrades). Listed shuffled on purpose: the choice must
        // be by setting identity, not page order or the old shortest-fight price.
        var dummy = Trade<BattlewornDummy>("SETTING_3", "SETTING_2", "SETTING_1");
        SetOwner(dummy, null);
        dummy.DynamicVars["Setting1Hp"].BaseValue = 75;
        dummy.DynamicVars["Setting2Hp"].BaseValue = 150;
        dummy.DynamicVars["Setting3Hp"].BaseValue = 300;
        var dummyPick = PickedBy(new BattlewornDummyHandler(), dummy, doors);
        Check(dummyPick.EndsWith("SETTING_2", StringComparison.Ordinal),
            $"Battleworn Dummy must always choose the second tier (two upgrades); got {dummyPick} "
            + $"({new BattlewornDummyHandler().Explain(doors, dummy)}).");
        Console.WriteLine("PASS: the dummy event always chooses the second tier.");

        // T4: trading a curse or a permanent cost for a benefit.
        //
        // LOST_WISP — the curse is the price. It must be charged EXACTLY ONCE whether
        // the game names it on the option or not: charging it twice rejects a relic
        // the deck should take, charging it never takes a curse for free.
        var wisp = Trade<LostWisp>("CLAIM", "SEARCH");
        wisp.DynamicVars["Gold"].BaseValue = 60;
        var curseCard = doorsCombat.CreateCard<Decay>(doors);
        var claimOption = wisp.CurrentOptions[0];
        var unnamedCurse = EventTrade.GrantsWithCurses(doors, wisp, "CLAIM", 1);
        Check(EventTrade.PricesCurse(wisp, claimOption) == false && unnamedCurse < 0,
            $"An unnamed curse must be charged by the handler: {unnamedCurse:F1}.");
        var namedOption = new EventOption(wisp, () => Task.CompletedTask, new LocString("events", "t"),
            new LocString("events", "d"), "LOST_WISP.pages.INITIAL.options.CLAIM",
            [new CardHoverTip(curseCard)]);
        typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(wisp, new List<EventOption> { namedOption, wisp.CurrentOptions[1] });
        Check(EventTrade.PricesCurse(wisp, wisp.CurrentOptions[0]),
            "A curse the option names must be recognised as already priced.");
        var onceOnly = EventTrade.GrantsWithCurses(doors, wisp, "CLAIM", 1);
        Check(Math.Abs(onceOnly - unnamedCurse) < 0.01,
            $"A named curse must not be charged a second time: {onceOnly:F1} vs {unnamedCurse:F1}.");
        // And the choice follows: 60 gold beats a curse when no relic is offered.
        var wispBack = Trade<LostWisp>("CLAIM", "SEARCH");
        wispBack.DynamicVars["Gold"].BaseValue = 60;
        Check(PickedBy(new LostWispHandler(), wispBack, doors).EndsWith("SEARCH", StringComparison.Ordinal),
            "Lost Wisp must walk away when the relic side has no value and the curse does.");

        // SPIRIT_GRAFTER — the heal is capped at what the pool can take, so the same
        // option is worth 25 more at 20/80 than at full health.
        var grafter = Trade<SpiritGrafter>("LET_IT_IN", "REJECTION");
        grafter.DynamicVars["LetItInHealAmount"].BaseValue = 25;
        doors.Creature.SetMaxHpInternal(80); doors.Creature.SetCurrentHpInternal(80);
        var fedFull = SpiritGrafterHandler.Price(doors, grafter).Accept;
        doors.Creature.SetCurrentHpInternal(20);
        var fedHurt = SpiritGrafterHandler.Price(doors, grafter).Accept;
        Check(Math.Abs((fedHurt - fedFull) - 25) < 0.01,
            $"The graft's heal must be worth the missing pool, not its face value: {fedFull:F1} -> {fedHurt:F1}.");
        doors.Creature.SetCurrentHpInternal(80);

        // DROWNING_BEACON — climbing costs permanent Max HP and that price must be
        // charged even when the relic is not named on the option.
        var beacon = Trade<DrowningBeacon>("BOTTLE", "CLIMB");
        beacon.DynamicVars["HpLoss"].BaseValue = 13;
        var (bottle, climb) = DrowningBeaconHandler.Price(doors, beacon);
        Check(climb < bottle && Math.Abs(climb - (-13)) < 0.01,
            $"Climbing must be charged its Max HP cost: bottle={bottle:F1} climb={climb:F1}.");
        Check(PickedBy(new DrowningBeaconHandler(), beacon, doors).EndsWith("BOTTLE", StringComparison.Ordinal),
            "Drowning Beacon must take the potion when the relic side is unpriced and the climb costs 13 Max HP.");
        Console.WriteLine($"PASS: T4 charges the curse once ({onceOnly:F1} vs {unnamedCurse:F1}), caps the "
            + $"graft's heal at the missing pool (+25), and prices the climb's Max HP ({climb:F1}).");

        // The three events the live logs met most often among the unmodelled.
        void ClearDeck(Player player)
        {
            foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        }
        void AddDeck<T>(Player player, CombatState combat, int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) player.Deck.AddInternal(combat.CreateCard<T>(player));
        }

        // SELF_HELP_BOOK — three pages, each enchanting ONE card type: an Attack with
        // SHARP, a Skill with NIMBLE, a Power with SWIFT. The ids say nothing, so the
        // page must be chosen from the enchantment's printed effect and whether the
        // deck can take it. Listed POWER FIRST so page order would pick the wrong one.
        var book = Trade<SelfHelpBook>("READ_ENTIRE_BOOK", "READ_PASSAGE", "READ_THE_BACK");
        book.DynamicVars["Enchantment1"].GetType().GetProperty("StringValue")!.SetValue(book.DynamicVars["Enchantment1"], "SHARP.title");
        book.DynamicVars["Enchantment2"].GetType().GetProperty("StringValue")!.SetValue(book.DynamicVars["Enchantment2"], "NIMBLE.title");
        book.DynamicVars["Enchantment3"].GetType().GetProperty("StringValue")!.SetValue(book.DynamicVars["Enchantment3"], "SWIFT.title");
        foreach (var key in new[] { "Enchantment1Amount", "Enchantment2Amount", "Enchantment3Amount" })
            book.DynamicVars[key].BaseValue = 2;
        ClearDeck(doors); AddDeck<StrikeIronclad>(doors, doorsCombat, 8);
        Check(PickedBy(new SelfHelpBookHandler(), book, doors).EndsWith("READ_THE_BACK", StringComparison.Ordinal),
            $"An attack-only deck must take the Attack page: {new SelfHelpBookHandler().Explain(doors, book)}.");
        ClearDeck(doors); AddDeck<DefendIronclad>(doors, doorsCombat, 8);
        Check(PickedBy(new SelfHelpBookHandler(), book, doors).EndsWith("READ_PASSAGE", StringComparison.Ordinal),
            $"A skill-only deck must take the Skill page: {new SelfHelpBookHandler().Explain(doors, book)}.");
        ClearDeck(doors); AddDeck<InfiniteBlades>(doors, doorsCombat, 8);
        Check(PickedBy(new SelfHelpBookHandler(), book, doors).EndsWith("READ_ENTIRE_BOOK", StringComparison.Ordinal),
            $"A power-only deck must take the Power page: {new SelfHelpBookHandler().Explain(doors, book)}.");

        // Owner 2026-10-05: when every page is takeable the Power page is a HARD
        // preference, not a price tie. Listed in the game's real page order
        // (Back=first, Entire=third) so page order alone would pick the Attack page.
        var orderedBook = Trade<SelfHelpBook>("READ_THE_BACK", "READ_PASSAGE", "READ_ENTIRE_BOOK");
        foreach (var key in new[] { "Enchantment1", "Enchantment2", "Enchantment3" })
            orderedBook.DynamicVars[key].GetType().GetProperty("StringValue")!.SetValue(orderedBook.DynamicVars[key], "SHARP.title");
        // The Power page is deliberately the CHEAPEST so the old printed-price
        // comparison (and page order) would take the Attack page instead.
        orderedBook.DynamicVars["Enchantment1Amount"].BaseValue = 5;
        orderedBook.DynamicVars["Enchantment2Amount"].BaseValue = 4;
        orderedBook.DynamicVars["Enchantment3Amount"].BaseValue = 1;
        ClearDeck(doors); AddDeck<StrikeIronclad>(doors, doorsCombat, 4);
        AddDeck<DefendIronclad>(doors, doorsCombat, 4); AddDeck<InfiniteBlades>(doors, doorsCombat, 4);
        Check(PickedBy(new SelfHelpBookHandler(), orderedBook, doors).EndsWith("READ_ENTIRE_BOOK", StringComparison.Ordinal),
            $"A deck with every type must hard-prefer the Power page: "
            + $"{new SelfHelpBookHandler().Explain(doors, orderedBook)}.");
        Console.WriteLine($"PASS: the book follows the deck's card types "
            + $"({new SelfHelpBookHandler().Explain(doors, book)}).");

        // STONE_OF_ALL_TIME — drink a random potion for Max HP, or take a wound for a
        // Vigorous attack. Both sides priced: the potion at its reserve value, the
        // wound 1:1, Vigorous at its printed first-play damage.
        var stone = Trade<StoneOfAllTime>("LIFT", "PUSH");
        stone.DynamicVars["DrinkMaxHpGain"].BaseValue = 10;
        stone.DynamicVars["PushHpLoss"].BaseValue = 6;
        stone.DynamicVars["PushVigorousAmount"].BaseValue = 8;
        Check(PickedBy(new StoneOfAllTimeHandler(), stone, doors).EndsWith("PUSH", StringComparison.Ordinal),
            $"A cheap Vigorous attack must beat ten Max HP for a potion: {new StoneOfAllTimeHandler().Explain(doors, stone)}.");
        stone.DynamicVars["DrinkMaxHpGain"].BaseValue = 30;
        Check(PickedBy(new StoneOfAllTimeHandler(), stone, doors).EndsWith("LIFT", StringComparison.Ordinal),
            $"Thirty Max HP must beat the Vigorous attack: {new StoneOfAllTimeHandler().Explain(doors, stone)}.");

        // WELCOME_TO_WONGOS — a shop, and LEAVING is not free: the game downgrades one
        // of the upgraded cards. A shelf nobody can price must not be bought blind.
        var wongos = Trade<WelcomeToWongos>("BARGAIN_BIN", "FEATURED_ITEM", "MYSTERY_BOX", "LEAVE");
        wongos.DynamicVars["BargainBinCost"].BaseValue = 100;
        wongos.DynamicVars["FeaturedItemCost"].BaseValue = 200;
        wongos.DynamicVars["MysteryBoxCost"].BaseValue = 300;
        ClearDeck(doors); AddDeck<StrikeIronclad>(doors, doorsCombat, 5); AddDeck<DefendIronclad>(doors, doorsCombat, 4);
        AddDeck<Bash>(doors, doorsCombat, 1);
        Check(PickedBy(new WelcomeToWongosHandler(), wongos, doors).EndsWith("LEAVE", StringComparison.Ordinal),
            $"An unpriced shelf must not be bought blind: {new WelcomeToWongosHandler().Explain(doors, wongos)}.");
        Console.WriteLine("PASS: Wongos prices its shelves against the cost of leaving.");

        // THE_LEGENDS_WERE_TRUE — nail down the map, or slowly find an exit for an
        // unblockable 8-point wound and a random potion. The map is a Quest card:
        // unplayable in combat, so the combat deck-value model refuses it with its
        // -1000 guard sentinel and the generic scorer started from -237 (measured
        // 2026-09-30), declining the map without ever reading the 600 gold the quest
        // pays or the treasure node it guarantees. Both sides must now be priced.
        var spoilsMap = doorsCombat.CreateCard<SpoilsMap>(doors);
        var questGold = (double)spoilsMap.DynamicVars["Gold"].BaseValue;
        Check(questGold > 0, "The fixture must expose SPOILS_MAP's quest gold.");
        EventModel LegendsPage(bool withReward)
        {
            var model = ModelDb.Event<TheLegendsWereTrue>().ToMutable();
            SetOwner(model, doors);
            EventOption Map() => new(model, () => Task.CompletedTask, new LocString("events", "t"),
                new LocString("events", "d"),
                "THE_LEGENDS_WERE_TRUE.pages.INITIAL.options.NAB_THE_MAP",
                withReward ? new IHoverTip[] { new CardHoverTip(spoilsMap) } : Array.Empty<IHoverTip>());
            EventOption Exit() => new(model, () => Task.CompletedTask, new LocString("events", "t"),
                new LocString("events", "d"),
                "THE_LEGENDS_WERE_TRUE.pages.INITIAL.options.SLOWLY_FIND_AN_EXIT", Array.Empty<IHoverTip>());
            var options = new List<EventOption> { Map(), Exit() };
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, options);
            return model;
        }
        int Slot(EventModel model, string suffix) => model.CurrentOptions
            .Select((option, index) => (option, index))
            .First(pair => pair.option.TextKey.EndsWith(suffix, StringComparison.Ordinal)).index;

        var legends = LegendsPage(withReward: true);
        var mapIndex = Slot(legends, "NAB_THE_MAP");
        var exitIndex = Slot(legends, "SLOWLY_FIND_AN_EXIT");
        // The combat model still refuses the card as unplayable; only the event layer
        // prices the quest. That layering is the point: BuildValue stays combat-only.
        Check(BuildValue.Add(spoilsMap, doors).Total < 0,
            "SPOILS_MAP must stay combat-dead in the deck-value model.");
        var mapGrant = EventTrade.Grants(doors, legends, legends.CurrentOptions[mapIndex]);
        var pricedQuestGold = questGold / BotShopPlanner.GoldPerDeckValue;
        Check(mapGrant.Value > pricedQuestGold - 10 && mapGrant.Detail.Contains("SPOILS_MAP"),
            $"The map's quest reward must be priced, not refused as unplayable: "
            + $"{mapGrant.Value:F1} ({mapGrant.Detail}).");
        Check(mapGrant.Value < pricedQuestGold + 0.01,
            $"Holding the Quest card must still cost its draw slot: {mapGrant.Value:F1} vs {pricedQuestGold:F1}.");
        var legendsPrice = LegendsWereTrueHandler.Price(doors, legends);
        Check(legendsPrice.Map > legendsPrice.Exit,
            $"The map must beat the wound-plus-potion exit once its reward is priced: "
            + $"map={legendsPrice.Map:F1} exit={legendsPrice.Exit:F1}.");
        Check(LegendsWereTrueHandler.Decide(mapIndex, exitIndex, legendsPrice) == mapIndex
            && LegendsWereTrueHandler.Decide(exitIndex, mapIndex, legendsPrice) == exitIndex,
            "The map must be chosen by identity, not by page order.");
        var legendsPick = (int)choose.Invoke(null, new object[] { doors, legends })!;
        Check(legends.CurrentOptions[legendsPick].TextKey.EndsWith("NAB_THE_MAP", StringComparison.Ordinal),
            $"The Legends Were True must nail down the map: got {legends.CurrentOptions[legendsPick].TextKey} "
            + $"({new LegendsWereTrueHandler().Explain(doors, legends)}).");

        // Negative control: strip the reward and the SAME handler must take the exit,
        // so the map win above comes from the priced quest and not from its id.
        var bare = LegendsPage(withReward: false);
        var bareMapIndex = Slot(bare, "NAB_THE_MAP");
        var bareExitIndex = Slot(bare, "SLOWLY_FIND_AN_EXIT");
        var barePrice = LegendsWereTrueHandler.Price(doors, bare);
        Check(barePrice.Exit > barePrice.Map && new LegendsWereTrueHandler().Choose(doors, bare) == bareExitIndex,
            $"With no reward attached the exit must win: map={barePrice.Map:F1} exit={barePrice.Exit:F1}.");
        Console.WriteLine($"PASS: The Legends Were True prices the map's quest ({mapGrant.Value:F1} of "
            + $"{pricedQuestGold:F1} value from gold) against the wound-plus-potion exit ({legendsPrice.Exit:F1}).");
    }
}
