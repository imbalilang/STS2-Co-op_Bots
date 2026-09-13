using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

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
        var min = prefs?.MinSelect ?? 1;
        var max = prefs?.MaxSelect ?? 1;
        var planned = BotChoicePlanSync.Select(player, cards, min, max);

        if (returnValueType == typeof(CardModel))
        {
            value = (planned ?? BotBrain.SelectCards(player, cards, min, max, originalMethod.Name)).FirstOrDefault();
        }
        else
        {
            var bundles = args.OfType<IEnumerable<IReadOnlyList<CardModel>>>().FirstOrDefault();
            value = planned is not null ? planned : bundles is not null
                ? ChooseBundle(player, bundles, originalMethod.Name)
                : BotBrain.SelectCards(player, cards, min, max, originalMethod.Name);
        }
        return true;
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

        var fallback = methodName.Contains("Hand", StringComparison.Ordinal) && player.PlayerCombatState is not null
            ? player.PlayerCombatState.Hand.Cards
            : player.Deck.Cards;
        return ApplyFilter(fallback, args);
    }

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
        var selected = relics.Count == 0 ? null : relics[0];
        __result = Task.FromResult(selected);
        return false;
    }
}
