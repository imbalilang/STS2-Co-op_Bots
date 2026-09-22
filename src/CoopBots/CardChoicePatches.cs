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
        // Most CardSelectCmd entry points carry the choosing Player explicitly. One
        // does not: FromDeckForEnchantment(IReadOnlyList<CardModel>, ...), which is
        // the exact overload RoyalStamp.AfterObtained calls. The native body derives
        // the owner from cards[0].Owner, so the prefix has to do the same or the
        // call falls through to the mixed-table deadlock this dispatcher exists to
        // prevent (see tests/PatchSmoke/ChoiceSeatScenarios.cs).
        var player = args.OfType<Player>().FirstOrDefault()
            ?? args.OfType<IEnumerable<CardModel>>().FirstOrDefault()?.FirstOrDefault()?.Owner;
        if (player is null || !AutoPilot.Drives(player.NetId))
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
        // EVERY FromDeckFor* filters its own deck before any selector sees it, and
        // intercepting the call as a prefix bypasses that body. Mirror each predicate
        // exactly rather than guessing — this is not a hypothetical: the first version
        // patched only the transformation case (below) and left the other three handing the
        // brain the WHOLE deck. A live report on 2026-09-20: after picking up the Neow relic
        // that removes a card, the bot removed 永恒 / Eternal Ascender's Bane — a card the
        // game's own predicate exists to protect.
        //
        // CardSelectCmd.cs:537  FromDeckForUpgrade        c.IsUpgradable
        // CardSelectCmd.cs:589  FromDeckForTransformation c.Type != Quest && c.IsTransformable
        // CardSelectCmd.cs:657  FromDeckForEnchantment    enchantment.CanEnchant(c) && additionalFilter(c)
        // CardSelectCmd.cs:742  FromDeckForRemoval        c.IsRemovable && filter(c)
        fallback = NativeDeckPredicate(methodName, fallback, args);
        return ApplyFilter(fallback, args);
    }

    /// <summary>
    /// The predicate the intercepted <c>FromDeckFor*</c> would have applied to its own deck.
    /// </summary>
    /// <remarks>
    /// Extracted so it can be asserted directly, because the bug it fixes is a filter and a
    /// filter is the cheapest thing here to pin: our prefix runs before the native body, so
    /// every deck the brain previously saw was UNFILTERED. Measured live: after the Neow
    /// relic that removes a card, the bot removed 永恒 / Eternal Ascender's Bane.
    /// </remarks>
    internal static IEnumerable<CardModel> NativeDeckPredicate(
        string methodName, IEnumerable<CardModel> deck, IEnumerable<object> args)
    {
        if (methodName == nameof(CardSelectCmd.FromDeckForTransformation))
            return deck.Where(NativeTransformable);
        if (methodName == nameof(CardSelectCmd.FromDeckForUpgrade))
            return deck.Where(card => card.IsUpgradable);
        if (methodName == nameof(CardSelectCmd.FromDeckForRemoval))
            return deck.Where(card => card.IsRemovable);
        if (methodName == nameof(CardSelectCmd.FromDeckForEnchantment)
            && args.OfType<EnchantmentModel>().FirstOrDefault() is { } enchantment)
            return deck.Where(card => enchantment.CanEnchant(card));
        return deck;
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
        if (!AutoPilot.Drives(player.NetId))
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
