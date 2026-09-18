using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

internal static class BotCardChoiceDispatcher
{
    public static bool TrySelect(MethodBase originalMethod, object[] args, out object? value)
    {
        var player = args.OfType<Player>().FirstOrDefault();
        if (player is null || !BotRegistry.IsBot(player.NetId))
        {
            value = null;
            return false;
        }

        var returnValueType = ((MethodInfo)originalMethod).ReturnType.GenericTypeArguments[0];
        var cards = ExtractCards(args, player, originalMethod.Name).ToList();
        var prefs = args.OfType<CardSelectorPrefs>().Cast<CardSelectorPrefs?>().FirstOrDefault();
        // A declinable screen says so with a plain bool argument, not with
        // CardSelectorPrefs. Reading only the prefs made every one of them
        // mandatory, so an offer the bot had no use for — a group of another
        // character's cards, say — still had to be picked from. Only
        // FromChooseACardScreen carries this flag, so the single bool is it.
        var maySkip = args.OfType<bool>().FirstOrDefault() && SkippingIsFree();
        var min = prefs?.MinSelect ?? (maySkip ? 0 : 1);
        var max = prefs?.MaxSelect ?? 1;
        var planned = BotChoicePlanSync.Select(player, cards, min, max);

        if (returnValueType == typeof(CardModel))
        {
            value = (planned ?? BotBrain.SelectCards(player, cards, min, max, originalMethod.Name, maySkip)).FirstOrDefault();
        }
        else
        {
            var bundles = args.OfType<IEnumerable<IReadOnlyList<CardModel>>>().FirstOrDefault();
            value = planned is not null ? planned : bundles is not null
                ? ChooseBundle(player, bundles, originalMethod.Name)
                : BotBrain.SelectCards(player, cards, min, max, originalMethod.Name, maySkip);
        }
        return true;
    }

    // Declining is free for a card offer and costly for a potion: the bottle has
    // already been spent by the time its choice opens, so an empty answer throws
    // it away. Only a plain choice may come back empty.
    private static bool SkippingIsFree()
    {
        try
        {
            return RunManager.Instance?.ActionExecutor.CurrentlyRunningAction
                is not MegaCrit.Sts2.Core.GameActions.UsePotionAction;
        }
        catch
        {
            // A run we cannot inspect must keep the old, non-skipping behaviour
            // rather than risk turning a real choice into a no-op.
            return false;
        }
    }

    private static IEnumerable<CardModel> ExtractCards(IEnumerable<object> args, Player player, string methodName)
    {
        foreach (var arg in args)
        {
            if (arg is CardPile pile)
                return ApplyFilter(pile.Cards, args);
            if (arg is IEnumerable<CardCreationResult> creations)
                return ApplyFilter(creations.Select(result => result.Card), args);
            if (arg is IEnumerable<CardModel> cards)
                return ApplyFilter(cards, args);
        }

        IEnumerable<CardModel> fallback = methodName.Contains("Hand", StringComparison.Ordinal) && player.PlayerCombatState is not null
            ? player.PlayerCombatState.Hand.Cards
            : player.Deck.Cards;
        // FromDeckForTransformation filters its own deck to cards that can really
        // be transformed before it ever reaches a selector. Intercepting the call
        // as a prefix bypasses that body, so the old fallback handed the brain the
        // whole deck and it could pick an Eternal curse; the transform then threw
        // "Non-removable cards cannot be transformed". Mirror the native predicate
        // exactly rather than guessing at curse ids.
        if (methodName == nameof(CardSelectCmd.FromDeckForTransformation))
            fallback = fallback.Where(NativeTransformable);
        return ApplyFilter(fallback, args);
    }

    private static bool NativeTransformable(CardModel card) =>
        card.Type != CardType.Quest && card.IsTransformable;

    private static IEnumerable<CardModel> ApplyFilter(IEnumerable<CardModel> cards, IEnumerable<object> args)
    {
        var filter = args.OfType<Func<CardModel, bool>>().FirstOrDefault();
        return filter is null ? cards : cards.Where(filter);
    }

    private static IReadOnlyList<CardModel> ChooseBundle(
        Player player,
        IEnumerable<IReadOnlyList<CardModel>> bundles,
        string purpose)
    {
        var options = bundles.Where(bundle => bundle.Count > 0).ToList();
        if (options.Count == 0)
            return Array.Empty<CardModel>();

        return options
            .OrderByDescending(bundle => BotBrain.SelectCards(player, bundle, 1, 1, purpose).Count)
            .ThenBy(bundle => bundle[0].Id.Entry, StringComparer.Ordinal)
            .First();
    }

}

[HarmonyPatch]
internal static class BotSingleCardSelectPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(CardSelectCmd)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.ReturnType == typeof(Task<CardModel>));

    private static bool Prefix(MethodBase __originalMethod, object[] __args, ref Task<CardModel?> __result)
    {
        if (!BotCardChoiceDispatcher.TrySelect(__originalMethod, __args, out var value))
            return true;
        __result = Task.FromResult(value as CardModel);
        return false;
    }
}

[HarmonyPatch]
internal static class BotMultipleCardSelectPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(CardSelectCmd)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.ReturnType == typeof(Task<IEnumerable<CardModel>>));

    private static bool Prefix(MethodBase __originalMethod, object[] __args, ref Task<IEnumerable<CardModel>> __result)
    {
        if (!BotCardChoiceDispatcher.TrySelect(__originalMethod, __args, out var value))
            return true;
        __result = Task.FromResult((IEnumerable<CardModel>)(value as IEnumerable<CardModel> ?? Array.Empty<CardModel>()));
        return false;
    }
}

[HarmonyPatch(typeof(RelicSelectCmd), nameof(RelicSelectCmd.FromChooseARelicScreen))]
internal static class BotRelicSelectPatch
{
    private static bool Prefix(Player player, IReadOnlyList<RelicModel> relics, ref Task<RelicModel?> __result)
    {
        if (!BotRegistry.IsBot(player.NetId))
            return true;
        // Boss relics are not interchangeable: the chain in RelicValue prices a
        // Brimstone by its team-wide downside, and a capsule or Astrolabe by what
        // it will hand over. Taking relics[0] made the choice a formality.
        var selected = relics
            .OrderByDescending(relic => HumanCoopAdvisor.RelicValue(relic, player).Score)
            .ThenBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .FirstOrDefault();
        __result = Task.FromResult(selected);
        return false;
    }
}
