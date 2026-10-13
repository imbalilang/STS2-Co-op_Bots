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
    internal static bool TrySelectAsync(MethodBase method, object[] args, out Task<object?>? selection)
    {
        selection = null;
        var player = args.OfType<Player>().FirstOrDefault()
            ?? args.OfType<IEnumerable<CardModel>>().FirstOrDefault()?.FirstOrDefault()?.Owner;
        if (player is null || !AutoPilot.Drives(player.NetId)
            || MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress) return false;
        // Capture native caller semantics before yielding; the event's async
        // state-machine frame no longer exists on later scoring passes.
        var purpose = SelectionPurpose(method, args);
        selection = Building.ConstructionDecision.RunAsync(player, () => {
            TrySelectCore(method, args, purpose, out var value);
            return value;
        }, () => {
            if (!AutoPilot.Drives(player.NetId)) throw new OperationCanceledException();
            return method.Name + Building.ConstructionDecision.Cards(ExtractCards(args, player, method.Name));
        });
        return true;
    }
    internal static async Task<CardModel?> SingleAsync(Task<object?> task) => await task as CardModel;
    internal static async Task<IEnumerable<CardModel>> MultipleAsync(Task<object?> task)
        => await task as IEnumerable<CardModel> ?? Array.Empty<CardModel>();

    public static bool TrySelect(MethodBase originalMethod, object[] args, out object? value)
        => TrySelectCore(originalMethod, args, SelectionPurpose(originalMethod, args), out value);

    private static bool TrySelectCore(MethodBase originalMethod, object[] args, string purpose, out object? value)
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
            if (player is not null) BotTurnStartChoiceSync.ExternalChoice(player);
            value = null;
            return false;
        }

        var returnValueType = ((MethodInfo)originalMethod).ReturnType.GenericTypeArguments[0];
        // Pael's Tooth asks for a deck REMOVAL, but it hands the card back upgraded after a
        // later combat — so it must be answered like a smith, not like a removal. The call
        // is indistinguishable from any other FromDeckForRemoval by its arguments (the
        // filter is just IsUpgradable), so classify the native caller the same way
        // BotRewardDriver.ChoiceForWait already classifies CardReward and MendRestSiteOption.
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
        var source = args.OfType<AbstractModel>().LastOrDefault()?.Id.Entry;
        // An empty native FromHand opens no FUTURE turn-start menu. An
        // immediate card action can still have a recorded implicit-all empty
        // choice (HiddenDaggers), which must advance its existing cursor.
        // Native R2 2026-10-12: restoring the old all-empty shortcut built0,
        // then Hidden(0) exited1 at the unchanged immediate-menu assertion:
        // Latest run choice repair: actual dispatcher and transported PlanSync must accept the native short HiddenDaggers menu without dropping its suffix
        // Exact 1154-source/10 DLL+PDB restore built0, native Hidden(0) GREEN0.
        // Evidence: work/v16-hidden-empty-menu-native-20261012-v1/r2-empty-immediate-v2/proof.json.
        var emptyHand = cards.Count == 0 && (originalMethod.Name == nameof(CardSelectCmd.FromHand)
            || originalMethod.Name.StartsWith("FromHandFor", StringComparison.Ordinal));
        var planned = Building.ConstructionDecision.Current is null
            ? (emptyHand ? null : BotTurnStartChoiceSync.SelectMenu(player, cards, min, max, source))
                ?? BotChoicePlanSync.SelectMenu(player, cards, min, max, source) : null;
        if (planned is null && emptyHand) planned = Array.Empty<CardModel>();
        planned ??= BotRelicChoices.Select(player, cards, min, max, purpose);

        if (returnValueType == typeof(CardModel))
        {
            value = (planned ?? BotBrain.SelectCards(player, cards, min, max, purpose, maySkip)).FirstOrDefault();
        }
        else
        {
            var bundles = args.OfType<IEnumerable<IReadOnlyList<CardModel>>>().FirstOrDefault();
            value = planned is not null ? planned : bundles is not null
                ? ChooseBundle(player, bundles, originalMethod.Name)
                : BotBrain.SelectCards(player, cards, min, max, purpose, maySkip);
        }
        return true;
    }

    /// <summary>
    /// Capture native caller semantics before asynchronous evaluation loses the stack.
    /// Generic transform prompts use transform ranking; WoodCarvings also supplies
    /// the known replacement so option scoring and target selection share a delta.
    /// </summary>
    private static string SelectionPurpose(MethodBase method, object[] args)
    {
        var prefs = args.OfType<CardSelectorPrefs>().Cast<CardSelectorPrefs?>().FirstOrDefault();
        var purpose = PurposeFor(method.Name, PaelsToothIsOnStack(), prefs);
        if (method.Name == nameof(CardSelectCmd.FromChooseACardScreen))
            purpose = SingleChoicePurpose(MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress);
        if (args.OfType<PowerModel>().FirstOrDefault() is MegaCrit.Sts2.Core.Models.Powers.ToolsOfTheTradePower
            && playerInCombat(args)) return purpose + ":power:ToolsOfTheTradePower";
        var relic = args.OfType<RelicModel>().FirstOrDefault()?.GetType();
        if (relic is null && method.Name is nameof(CardSelectCmd.FromChooseACardScreen) or nameof(CardSelectCmd.FromSimpleGrid))
            relic = new System.Diagnostics.StackTrace().GetFrames().Select(f => f.GetMethod()?.DeclaringType)
                .Select(t => t?.DeclaringType ?? t).FirstOrDefault(t => t is not null && typeof(RelicModel).IsAssignableFrom(t));
        if (relic?.Name is "GamblingChip" or "ToastyMittens" or "Toolbox" or "ChoicesParadox"
            && playerInCombat(args)) return purpose + ":relic:" + relic.Name;
        if (purpose == nameof(CardSelectCmd.FromDeckForEnchantment)
            && args.OfType<EnchantmentModel>().FirstOrDefault() is { } enchantment)
            return purpose + ":" + enchantment.GetType().Name + ":" + args.OfType<int>().FirstOrDefault();
        if (purpose != nameof(CardSelectCmd.FromDeckForTransformation)) return purpose;
        if (args.OfType<Delegate>().Any(callback => callback.Target is MegaCrit.Sts2.Core.Models.Relics.Claws))
            return purpose + ":MAUL";
        foreach (var frame in new System.Diagnostics.StackTrace().GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type?.DeclaringType == typeof(MegaCrit.Sts2.Core.Models.Relics.Astrolabe)) return purpose + ":RNG_NICHE_UPGRADE";
            if (type?.DeclaringType == typeof(MegaCrit.Sts2.Core.Models.Relics.NewLeaf)) return purpose + ":RNG_NICHE";
            if (type?.DeclaringType == typeof(MegaCrit.Sts2.Core.Models.Relics.Claws)) return purpose + ":MAUL";
            if (type?.DeclaringType != typeof(MegaCrit.Sts2.Core.Models.Events.WoodCarvings)) continue;
            if (type.Name.StartsWith("<Bird>", StringComparison.Ordinal)) return purpose + ":PECK";
            if (type.Name.StartsWith("<Torus>", StringComparison.Ordinal)) return purpose + ":TORIC_TOUGHNESS";
        }
        return purpose;

        static bool playerInCombat(object[] values) => (values.OfType<Player>().FirstOrDefault()
            ?? values.OfType<IEnumerable<CardModel>>().FirstOrDefault()?.FirstOrDefault()?.Owner)?.Creature.CombatState is not null;
    }

    internal static string SingleChoicePurpose(bool inCombat)
        => inCombat ? nameof(CardSelectCmd.FromChooseACardScreen) : BotBrain.PermanentAddPurpose;

    internal static string PurposeFor(string methodName, bool paelsToothOnStack, CardSelectorPrefs? prefs = null)
        => methodName == nameof(CardSelectCmd.FromDeckForRemoval) && paelsToothOnStack
            ? BotBrain.PaelsToothPurpose
            : methodName == nameof(CardSelectCmd.FromDeckGeneric)
                && prefs?.Prompt?.LocTable == "card_selection" && prefs?.Prompt?.LocEntryKey == "TO_TRANSFORM"
                ? nameof(CardSelectCmd.FromDeckForTransformation)
            : methodName;

    /// <summary>
    /// Whether the native call we are answering was made by <c>PaelsTooth.AfterObtained</c>.
    /// Its removal screen is argument-identical to every other one (the filter is only
    /// <c>IsUpgradable</c>), so the caller is the only thing that tells them apart — the same
    /// classification <see cref="BotRewardDriver.ChoiceForWait"/> does for CardReward and
    /// MendRestSiteOption. Only walked for FromDeckForRemoval, which is rare.
    /// </summary>
    private static bool PaelsToothIsOnStack()
    {
        try
        {
            return new System.Diagnostics.StackTrace().GetFrames().Any(frame =>
                frame.GetMethod()?.DeclaringType?.FullName?.Contains("PaelsTooth", StringComparison.Ordinal) == true);
        }
        catch
        {
            // A stack we cannot read must not change the answer.
            return false;
        }
    }

    // Declining is free for a card offer and costly for a potion: the bottle has
    // already been spent by the time its choice opens, so an empty answer throws
    // it away. Only a plain choice may come back empty.
    private static bool SkippingIsFree()
    {
        // Permanent event/relic choices are outside UsePotionAction and their
        // native canSkip flag is authoritative even without a run UI singleton.
        if (!MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress) return true;
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
                return ApplyFilter(NativeDeckPredicate(methodName, pile.Cards, args), args);
            if (arg is IEnumerable<CardCreationResult> creations)
                return ApplyFilter(NativeDeckPredicate(methodName, creations.Select(result => result.Card), args), args);
            if (arg is IEnumerable<CardModel> cards)
                return ApplyFilter(NativeDeckPredicate(methodName, cards, args), args);
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
        if (methodName is nameof(CardSelectCmd.FromDeckForUpgrade) or nameof(CardSelectCmd.FromHandForUpgrade))
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
        if (BotCardChoiceDispatcher.TrySelectAsync(__originalMethod, __args, out var task))
        { __result = BotCardChoiceDispatcher.SingleAsync(task!); return false; }
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
        if (BotCardChoiceDispatcher.TrySelectAsync(__originalMethod, __args, out var task))
        { __result = BotCardChoiceDispatcher.MultipleAsync(task!); return false; }
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
        RelicModel? Choose() => relics
            .OrderByDescending(relic => HumanCoopAdvisor.RelicValue(relic, player).Score)
            .ThenBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .FirstOrDefault();
        __result = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress ? Task.FromResult(Choose())
            : CoopBots.Building.ConstructionDecision.RunAsync(player, Choose, () =>
            {
                if (!AutoPilot.Drives(player.NetId)) throw new OperationCanceledException();
                return string.Join(';', relics.Select(r => $"{r.Id}:{r.IsUsedUp}"));
            });
        return false;
    }
}
