using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// Slippery Bridge regression, from the 2026-09-25 A5 four-bot run: every seat
/// answered OVERCOME and gave up the one non-Basic card it owned (TACTICIAN,
/// MOLTEN_FIST, FLAME_BARRIER, BULLET_TIME — each picked one to six floors
/// earlier), and the party reached the act-1 boss on 11/12/13/15-card decks
/// with three upgrades between them.
///
/// Neither option id carries any wording, so the generic scorer priced both at
/// 5.0 and the index tie-break took OVERCOME. These assertions drive the same
/// private chooser the driver calls, so a missing or inert handler falls back to
/// that tie-break and the first check goes red.
/// </summary>
internal static class SlipperyBridgeScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();

        var choose = typeof(BotEventDriver)
            .GetMethod("ChooseEventOption", BindingFlags.Static | BindingFlags.NonPublic)!;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Slippery Bridge regression: " + message);
        }

        Check(EventHandlers.Handles("SLIPPERY_BRIDGE"),
            "Slippery Bridge must have a registered handler, or the tie-break gives up the deck's best card.");

        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 3, 1));
        typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(player, ModelDb.Character<Ironclad>());
        RunState.CreateForTest(new[] { player }, seed: "SLIPPERY-BRIDGE");

        var moltenFist = ModelDb.Card<MoltenFist>();
        var strike = ModelDb.Card<StrikeIronclad>();
        var decay = ModelDb.Card<Decay>();

        // Returns the TextKey of the option the chooser took, so the assertions
        // name the action rather than an index. `cost` is the damage the event has
        // published for the next hold — the real event feeds the same number into
        // its lethal flag.
        string Picked(CardModel atRisk, int cost, int hp = 80, int maxHp = 80,
            bool holdOnFirst = false, string holdOnId = "HOLD_ON_0")
        {
            player.Creature.SetMaxHpInternal(maxHp);
            player.Creature.SetCurrentHpInternal(hp);

            var bridge = ModelDb.Event<SlipperyBridge>().ToMutable();
            bridge.DynamicVars["HpLoss"].BaseValue = cost;
            // Titles are passed explicitly: the short overload looks them up in the
            // localization tables, which the headless harness does not have.
            EventOption Overcome() => new(bridge, () => Task.CompletedTask,
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                "SLIPPERY_BRIDGE.pages.INITIAL.options.OVERCOME",
                new[] { HoverTipFactory.FromCard(atRisk) });
            EventOption HoldOn() => new EventOption(bridge, () => Task.CompletedTask,
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                "SLIPPERY_BRIDGE.pages.INITIAL.options." + holdOnId, Array.Empty<IHoverTip>())
                .ThatDoesDamage(cost);
            var options = holdOnFirst
                ? new List<EventOption> { HoldOn(), Overcome() }
                : new List<EventOption> { Overcome(), HoldOn() };

            // The page is private state; the driver reads it through the same field.
            typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(bridge, options);

            var index = (int)choose.Invoke(null, new object[] { player, bridge })!;
            return index >= 0 && index < options.Count ? options[index].TextKey : "(waited)";
        }

        // The live bug: the event offers a card the deck chose, OVERCOME is listed
        // first, and the tie-break hands it over for nothing. The 3 HP reroll is
        // cheap and keeps the card.
        //
        // R2, proved by making the handler inert (the pre-fix behaviour: it defers
        // and the generic scorer's index tie-break takes OVERCOME):
        //   System.InvalidOperationException: Slippery Bridge regression:
        //   A picked card must not be handed over while a 3 HP reroll is on the table.
        //     at SlipperyBridgeScenarios.Run() ... line 48
        Check(Picked(moltenFist, 3).EndsWith("HOLD_ON_0", StringComparison.Ordinal),
            "A picked card must not be handed over while a 3 HP reroll is on the table.");

        // Filler is exactly what this event is for: taking it thins the deck, and
        // paying HP to dodge it would be strictly worse. Asserted in both option
        // orders so the choice is proved to be by id, not by index.
        Check(Picked(strike, 3).EndsWith("OVERCOME", StringComparison.Ordinal),
            "A Basic card is replaceable and must be handed over.");
        Check(Picked(strike, 3, holdOnFirst: true).EndsWith("OVERCOME", StringComparison.Ordinal),
            "A Basic must still be handed over when OVERCOME is listed second.");
        Check(Picked(decay, 3).EndsWith("OVERCOME", StringComparison.Ordinal),
            "A curse must be handed over rather than paid for.");

        // The hit is unblockable, unpowered and grows with every hold: it is never
        // worth a card. At exactly the cost the game flags the hold as lethal.
        Check(Picked(moltenFist, 3, hp: 3).EndsWith("OVERCOME", StringComparison.Ordinal),
            "A hold that would kill the player must be refused.");
        Check(Picked(moltenFist, 12, hp: 30).EndsWith("OVERCOME", StringComparison.Ordinal),
            "A hold that would leave the player under the health floor must be refused.");
        Check(Picked(moltenFist, 3, hp: 40).EndsWith("HOLD_ON_0", StringComparison.Ordinal),
            "A hold above the health floor is still worth taking.");

        // After the first hold the event rebuilds the page under a new key
        // ("pages.HOLD_ON_1.options.HOLD_ON_2"). The id is matched, so the second
        // hold is still recognised — this is the page that actually follows.
        Check(Picked(moltenFist, 7, hp: 70, holdOnId: "HOLD_ON_LOOP").EndsWith("HOLD_ON_LOOP", StringComparison.Ordinal),
            "A rebuilt page must still find its HOLD_ON option by id.");

        // Deciding only reads state: the health the page was decided against must
        // be exactly the health left behind.
        _ = Picked(moltenFist, 3, hp: 63);
        Check(player.Creature.CurrentHp == 63 && player.Creature.MaxHp == 80,
            "Deciding must not alter the player's health.");

        Console.WriteLine("PASS: Slippery Bridge holds on for a picked card and gives up only filler, "
            + "refusing rerolls that would kill or gut the player.");
    }
}
