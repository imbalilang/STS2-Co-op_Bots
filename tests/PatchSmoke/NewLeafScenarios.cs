using CoopBots;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// NewLeaf.AfterObtained asks FromDeckForTransformation for a card and then
// transforms it. The bot answers that entry point with a Harmony prefix, which
// bypasses the method body — including the native `c.Type != CardType.Quest &&
// c.IsTransformable` filter. The dispatcher's fallback used to hand the whole
// deck to the brain, so an Eternal curse could be chosen and the transform threw
// "Non-removable cards cannot be transformed". These scenarios drive the real,
// Harmony-patched method with real card models, not the helper alone.
internal static class NewLeafScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();

        var bot = Player.CreateForNewRun<Deprived>(
            UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 77, 0));
        var run = RunState.CreateForTest(new[] { bot }, seed: "NEWLEAF");
        bot.ResetCombatState();
        var combat = new CombatState(runState: run);
        combat.AddPlayer(bot);

        void Clear()
        {
            foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card);
        }
        void Give<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        }
        static CardSelectorPrefs Transform(int count) =>
            new(CardSelectorPrefs.TransformSelectionPrompt, count);

        // Minimum-one pick: an Eternal curse must never be the card offered back.
        Clear();
        Give<AscendersBane>();
        Give<StrikeIronclad>(5);
        Give<DefendIronclad>(4);
        var legal = CardSelectCmd.FromDeckForTransformation(bot, Transform(1)).GetAwaiter().GetResult().ToList();
        if (legal.Count != 1)
            throw new Exception($"A minimum-one transform must return exactly one card, got {legal.Count}.");
        if (legal[0].Type == CardType.Quest || !legal[0].IsTransformable)
            throw new Exception($"A transform must never offer an un-transformable card, got {legal[0].Id.Entry}.");
        if (legal[0].Id.Entry == "ASCENDERS_BANE")
            throw new Exception("An Eternal curse must be filtered out of a transform selection.");
        Console.WriteLine($"PASS: a transform with AscendersBane in the deck offers a legal card ({legal[0].Id.Entry}).");

        // Quest cards are filtered by the same predicate, even alongside legal ones.
        Clear();
        Give<StrikeIronclad>(3);
        Give<Dowsing>();
        var withQuest = CardSelectCmd.FromDeckForTransformation(bot, Transform(1)).GetAwaiter().GetResult().ToList();
        if (withQuest.Count != 1 || withQuest[0].Type == CardType.Quest)
            throw new Exception($"A Quest card must be filtered from a transform, got "
                + $"{string.Join(",", withQuest.Select(card => card.Id.Entry))}.");
        Console.WriteLine("PASS: Quest cards are filtered from a transform selection.");

        // A deck with nothing transformable comes back empty and must not wait.
        Clear();
        Give<AscendersBane>(2);
        Give<Dowsing>();
        var none = CardSelectCmd.FromDeckForTransformation(bot, Transform(1)).GetAwaiter().GetResult().ToList();
        if (none.Count != 0)
            throw new Exception($"A deck with no transformable cards must select nothing, got "
                + $"{string.Join(",", none.Select(card => card.Id.Entry))}.");
        Console.WriteLine("PASS: an all-un-transformable deck returns empty without waiting.");

        // A required count larger than the legal pool is capped at what exists.
        Clear();
        Give<StrikeIronclad>(1);
        Give<AscendersBane>();
        var capped = CardSelectCmd.FromDeckForTransformation(bot, Transform(2)).GetAwaiter().GetResult().ToList();
        if (capped.Count != 1 || !capped[0].IsTransformable)
            throw new Exception($"A two-card request with one legal candidate must return only that candidate, got "
                + $"{string.Join(",", capped.Select(card => card.Id.Entry))}.");
        Console.WriteLine("PASS: a transform request is capped at the legal candidates available.");

        // Selection reads a copy of the deck: repeated calls agree and the live
        // deck is untouched and un-reordered before the transform is applied.
        Clear();
        Give<StrikeIronclad>(3);
        Give<DefendIronclad>(3);
        Give<AscendersBane>();
        var snapshot = bot.Deck.Cards.ToList();
        var first = CardSelectCmd.FromDeckForTransformation(bot, Transform(1)).GetAwaiter().GetResult().ToList();
        var second = CardSelectCmd.FromDeckForTransformation(bot, Transform(1)).GetAwaiter().GetResult().ToList();
        if (bot.Deck.Cards.Count != snapshot.Count)
            throw new Exception($"The live deck must keep its size during a transform choice "
                + $"({snapshot.Count} -> {bot.Deck.Cards.Count}).");
        for (var index = 0; index < snapshot.Count; index++)
            if (!ReferenceEquals(bot.Deck.Cards[index], snapshot[index]))
                throw new Exception($"The live deck must not be mutated or reordered during a transform choice (index {index}).");
        if (first.Count != second.Count || !ReferenceEquals(first.FirstOrDefault(), second.FirstOrDefault()))
            throw new Exception("Repeated transform selection must be deterministic.");
        Console.WriteLine("PASS: transform selection never touches the live deck and repeats deterministically.");

        // A human is not a bot: the dispatcher must decline the call so the real
        // screen runs instead of being answered by the bot.
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 4242);
        var humanHandled = BotCardChoiceDispatcher.TrySelect(
            typeof(CardSelectCmd).GetMethod(nameof(CardSelectCmd.FromDeckForTransformation))!,
            new object[] { human, Transform(1), null! },
            out _);
        if (humanHandled)
            throw new Exception("The dispatcher must not answer a card choice for a human player.");
        Console.WriteLine("PASS: a human player's transform screen is left to the native path.");

        // The actual relic path, end to end. Before the fix the brain chose the
        // Eternal curse and CardTransformation threw; now the transform completes
        // and swaps exactly one legal card for exactly one new reference. Every
        // pre-call deck reference is captured so a silent no-op (nothing removed,
        // nothing added) and an illegal removal (the curse, or any un-transformable
        // card) both fail, not merely a changed deck size.
        Clear();
        Give<AscendersBane>();
        Give<StrikeIronclad>(5);
        Give<DefendIronclad>(4);
        var before = bot.Deck.Cards.ToList();
        var deckSize = before.Count;
        var curseBefore = before.Single(card => card.Id.Entry == "ASCENDERS_BANE");
        var relic = ModelDb.Relic<NewLeaf>().ToMutable();
        relic.Owner = bot;
        relic.AfterObtained().GetAwaiter().GetResult();
        var after = bot.Deck.Cards.ToList();

        static bool Present(IReadOnlyList<CardModel> cards, CardModel target) =>
            cards.Any(card => ReferenceEquals(card, target));
        var removed = before.Where(card => !Present(after, card)).ToList();
        var added = after.Where(card => !Present(before, card)).ToList();

        if (bot.Deck.Cards.Count != deckSize || after.Count != before.Count)
            throw new Exception($"A transform must replace a card in place, deck size {deckSize} -> {bot.Deck.Cards.Count}.");
        if (removed.Count != 1)
            throw new Exception($"A transform must remove exactly one old card reference, removed {removed.Count}: "
                + string.Join(",", removed.Select(card => card.Id.Entry)) + ".");
        if (added.Count != 1)
            throw new Exception($"A transform must add exactly one new card reference, added {added.Count}: "
                + string.Join(",", added.Select(card => card.Id.Entry)) + ".");
        if (!removed[0].IsTransformable || removed[0].Type == CardType.Quest)
            throw new Exception($"A transform must only remove a legal card, removed {removed[0].Id.Entry}.");
        if (ReferenceEquals(removed[0], curseBefore) || ReferenceEquals(added[0], curseBefore))
            throw new Exception("An Eternal curse must never be transformed by NewLeaf.");
        if (!Present(after, curseBefore))
            throw new Exception("An Eternal curse must survive NewLeaf's transform untouched.");
        Console.WriteLine("PASS: NewLeaf.AfterObtained replaces exactly one legal card "
            + $"({removed[0].Id.Entry} -> {added[0].Id.Entry}) and leaves the Eternal curse alone.");
    }
}
