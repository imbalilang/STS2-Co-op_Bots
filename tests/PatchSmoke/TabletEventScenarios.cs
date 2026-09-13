using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
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
        var reflectionsPlayer = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Genius, 7, 1));
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
    }
}
