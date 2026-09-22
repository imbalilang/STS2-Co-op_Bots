using CoopBots;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Community report (2026-09-21): "every in-run effect that makes a BOT pick a card
// hangs — Survivor, Attack Potion, Power Potion". Both named cases funnel into a
// CardSelectCmd entry point, and the bot answers those with a Harmony prefix:
//
//   Survivor            -> CardSelectCmd.FromHandForDiscard   (Models/Cards/Survivor.cs)
//   Attack/Skill/Power  -> CardSelectCmd.FromChooseACardScreen (Models/Potions/*.cs)
//
// WHY IT HANGS WHEN NOBODY ANSWERS. The native body has exactly two branches and
// both park forever when the choosing seat is one nobody plays by hand: for the
// LOCAL player it shows NChooseACardSelectionScreen and awaits a click, and for any
// other player it awaits PlayerChoiceSynchronizer.WaitForRemoteChoice — a card
// choice message that a synthetic bot has no client to send. So "is the answering
// prefix firing for this seat" is the whole question, and that guard is the thing
// this file pins.
//
// The assertions below drive the real dispatch seam with real card models, and they
// check the seat question from both sides: answered for a seat the bot drives
// (synthetic bot, or a real player's seat handed over), declined for a seat it does
// not (an ordinary human, whose own screen must keep working).
internal static class ChoiceSeatScenarios
{
    private static void Fail(string message) => throw new InvalidOperationException("ChoiceSeat: " + message);

    internal static void Run()
    {
        TestEnvironment.Ensure();
        AutoPilot.Clear();

        var bot = Player.CreateForNewRun<Deprived>(
            UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 91, 0));
        var seat = Player.CreateForNewRun<Deprived>(UnlockState.all, 4242);
        var run = RunState.CreateForTest(new[] { bot, seat }, seed: "CHOICESEAT");
        bot.ResetCombatState();
        seat.ResetCombatState();
        var combat = new CombatState(runState: run);
        combat.AddPlayer(bot);
        combat.AddPlayer(seat);

        // The two entry points the report names, resolved by the same reflection the
        // patch uses (so a renamed or re-signatured method fails here, not silently).
        var fromHand = typeof(CardSelectCmd).GetMethod(nameof(CardSelectCmd.FromHandForDiscard))
            ?? throw new InvalidOperationException("ChoiceSeat: CardSelectCmd.FromHandForDiscard is gone.");
        var fromScreen = typeof(CardSelectCmd).GetMethod(nameof(CardSelectCmd.FromChooseACardScreen))
            ?? throw new InvalidOperationException("ChoiceSeat: CardSelectCmd.FromChooseACardScreen is gone.");

        // Survivor's own call: FromHandForDiscard(context, player, prefs(1), filter: null, source: this).
        // The context is null on purpose — the prefix answers before the body could read it,
        // which is the property under test (the body would dereference it).
        var discardPrefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 1);
        var hand = new List<CardModel>
        {
            combat.CreateCard<StrikeIronclad>(bot),
            combat.CreateCard<DefendIronclad>(bot),
            combat.CreateCard<Bash>(bot),
        };
        foreach (var card in hand) bot.PlayerCombatState!.Hand.AddInternal(card);

        // The potions' own call: three generated cards, pick one (canSkip: true).
        var offered = new List<CardModel>
        {
            combat.CreateCard<StrikeIronclad>(bot),
            combat.CreateCard<Bash>(bot),
            combat.CreateCard<Anger>(bot),
        };

        object[] DiscardArgs(Player chooser) => [null!, chooser, discardPrefs, null!, null!];
        object[] ScreenArgs(Player chooser) => [null!, offered, chooser, true];

        // 1. A synthetic bot: both reported entry points are answered, and the answer
        //    is a card from the offered set. The empty answer is what the native path
        //    produces instead — after waiting forever.
        if (!BotCardChoiceDispatcher.TrySelect(fromScreen, ScreenArgs(bot), out var screenPick))
            Fail("a synthetic bot's potion choice (FromChooseACardScreen) must be answered; "
                + "otherwise the native body waits for a remote choice no client will ever send.");
        if (screenPick is not CardModel picked || !offered.Contains(picked))
            Fail($"the potion choice must return one of the three offered cards, got {screenPick ?? "null"}.");

        if (!BotCardChoiceDispatcher.TrySelect(fromHand, DiscardArgs(bot), out var discardPick))
            Fail("Survivor's discard (FromHandForDiscard) must be answered for a synthetic bot.");
        var discarded = ((IEnumerable<CardModel>?)discardPick ?? []).ToList();
        if (discarded.Count != 1 || !hand.Contains(discarded[0]))
            Fail("Survivor's discard must name exactly one card that is actually in the hand.");

        // 2. A real player's seat handed over to the bot ("全 Bot" mode) is driven too,
        //    and that is the seat the 0.37.0 guard widening exists for: with the older
        //    BotRegistry.IsBot guard this seat fell through to the native body, which is
        //    the reported stall (the screen opens on a player who handed the seat away).
        //
        //    R2 (2026-09-22, mutated `Drives` back to `BotRegistry.IsBot` in
        //    BotCardChoiceDispatcher.TrySelect) — the assertions below go red, quoted:
        //      System.InvalidOperationException: ChoiceSeat: a handed-over seat's potion
        //      choice must be answered by the bot that drives it.
        //        at ChoiceSeatScenarios.Run()
        AutoPilot.Set(seat.NetId, true);
        var seatHand = new List<CardModel> { combat.CreateCard<StrikeIronclad>(seat) };
        foreach (var card in seatHand) seat.PlayerCombatState!.Hand.AddInternal(card);
        if (!BotCardChoiceDispatcher.TrySelect(fromScreen, ScreenArgs(seat), out _))
            Fail("a handed-over seat's potion choice must be answered by the bot that drives it.");
        if (!BotCardChoiceDispatcher.TrySelect(fromHand, DiscardArgs(seat), out _))
            Fail("a handed-over seat's Survivor discard must be answered by the bot that drives it.");
        AutoPilot.Set(seat.NetId, false);

        // 3. Taking the seat back must restore the native path, or a real human would
        //    lose the screen that is theirs to answer.
        if (BotCardChoiceDispatcher.TrySelect(fromScreen, ScreenArgs(seat), out _)
            || BotCardChoiceDispatcher.TrySelect(fromHand, DiscardArgs(seat), out _))
            Fail("an ordinary human's card choice must be left to the native screen.");

        // 4. Survivor resolving onto an EMPTY hand is a real live case (the card can
        //    resolve more times than the hand has cards: upstream hit exactly this and
        //    the native body answers it with a 0-option request). The dispatcher must
        //    return "nothing to discard" instead of throwing inside the prefix — an
        //    exception there escapes into the game's action and wedges the queue.
        foreach (var card in hand.ToArray()) bot.PlayerCombatState!.Hand.RemoveInternal(card);
        if (!BotCardChoiceDispatcher.TrySelect(fromHand, DiscardArgs(bot), out var emptyPick))
            Fail("an empty hand must still be answered, not left to the native 0-option request.");
        if (((IEnumerable<CardModel>?)emptyPick ?? []).Any())
            Fail("an empty hand cannot produce a discarded card.");

        // 5. The selector hatch is MACHINE-wide, not seat-wide: the native CardSelectCmd
        //    bodies read `Selector` before they ask who is choosing, so while it is
        //    installed the game answers every player's choice with the bot's brain — a
        //    human at the table loses their own screen and their choice message is never
        //    sent, which is what the other peers wait on. It may therefore only be
        //    installed while EVERY seat is driven.
        //
        //    R2 (2026-09-22, mutated `SyncForRun` to install whenever a run exists — the
        //    shape the bug had) — the first assertion below goes red, quoted:
        //      System.InvalidOperationException: ChoiceSeat: a table with an ordinary human
        //      must not have the machine-wide selector installed — it would answer that
        //      human's own choice and never sync it to the other peers.
        //        at ChoiceSeatScenarios.Run()
        AutoPilot.Clear();
        var table = new List<Player> { bot, seat };

        BotCardSelector.SyncForRun(table);
        if (CardSelectCmd.Selector is not null)
            Fail("a table with an ordinary human must not have the machine-wide selector installed — "
                + "it would answer that human's own choice and never sync it to the other peers.");

        AutoPilot.Set(seat.NetId, true);
        BotCardSelector.SyncForRun(table);
        if (CardSelectCmd.Selector is null)
            Fail("a table where every seat is driven still needs the selector for a handed-over seat's "
                + "card reward (the reward body has no other seam).");

        AutoPilot.Set(seat.NetId, false);
        BotCardSelector.SyncForRun(table);
        if (CardSelectCmd.Selector is not null)
            Fail("taking the seat back must remove the selector again, not leave it for the rest of the run.");
        AutoPilot.Clear();

        // 6. RoyalStamp.AfterObtained calls the ONE CardSelectCmd overload that carries
        //    no Player argument: FromDeckForEnchantment(IReadOnlyList<CardModel>, ...).
        //    The prefix therefore has to recover the owner from cards[0].Owner, exactly
        //    as the native body does. Without that, a mixed table never installs the
        //    machine-wide selector, the synthetic path falls through to the remote
        //    fallback, which returns an Index PlayerChoiceResult, and the native body
        //    throws on AsDeckCards(); a handed-over local seat instead opens
        //    NDeckEnchantSelectScreen and waits for a click nobody makes. Either way
        //    the shop purchase awaits forever.
        //
        //    CHECKED (R2, 2026-09-23): restoring the old `args.OfType<Player>()`-only
        //    lookup turns the first assertion below red, verbatim:
        //      System.InvalidOperationException: ChoiceSeat: RoyalStamp's
        //      FromDeckForEnchantment(cards, ...) must be answered for a synthetic bot;
        //      the remote fallback returns an Index result and the native AsDeckCards()
        //      throws.
        //        at ChoiceSeatScenarios.Fail(...) ChoiceSeatScenarios.cs:line 35
        //        at ChoiceSeatScenarios.Run()      ChoiceSeatScenarios.cs:line 204
        var fromDeckEnchant = typeof(CardSelectCmd).GetMethods().Single(method =>
            method.Name == nameof(CardSelectCmd.FromDeckForEnchantment)
            && method.GetParameters().Length == 4
            && method.GetParameters()[0].ParameterType == typeof(IReadOnlyList<CardModel>));
        var royalStamp = ModelDb.Enchantment<RoyallyApproved>();
        var enchantPrefs = new CardSelectorPrefs(CardSelectorPrefs.EnchantSelectionPrompt, 1);
        object[] EnchantArgs(Player owner) =>
        [
            new List<CardModel>
            {
                combat.CreateCard<StrikeIronclad>(owner),
                combat.CreateCard<Bash>(owner),
            },
            royalStamp,
            1,
            enchantPrefs,
        ];

        if (!BotCardChoiceDispatcher.TrySelect(fromDeckEnchant, EnchantArgs(bot), out var botEnchantPick))
            Fail("RoyalStamp's FromDeckForEnchantment(cards, ...) must be answered for a synthetic bot; "
                + "the remote fallback returns an Index result and the native AsDeckCards() throws.");
        if (botEnchantPick is not IEnumerable<CardModel> botEnchantCards || botEnchantCards.Count() != 1)
            Fail($"RoyalStamp's enchant choice must return exactly one card, got {botEnchantPick ?? "null"}.");

        AutoPilot.Set(seat.NetId, true);
        if (!BotCardChoiceDispatcher.TrySelect(fromDeckEnchant, EnchantArgs(seat), out _))
            Fail("RoyalStamp's no-Player overload must also be answered for a handed-over seat.");
        AutoPilot.Set(seat.NetId, false);
        if (BotCardChoiceDispatcher.TrySelect(fromDeckEnchant, EnchantArgs(seat), out _))
            Fail("an ordinary human's RoyalStamp enchant pick must stay on the native screen.");
        AutoPilot.Clear();

        Console.WriteLine("PASS: the reported card-choice stalls are answered for every seat the bot "
            + "drives — a synthetic bot's potion pick and Survivor discard, and the same two on a "
            + "handed-over real seat plus RoyalStamp's owner-less deck-enchant overload — while an "
            + "ordinary human keeps the native screen and the machine-wide selector hatch stays off "
            + "any table with a human at it, and an empty hand returns nothing instead of throwing.");
    }
}
