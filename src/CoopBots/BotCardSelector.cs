using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.TestSupport;
// Two different ICardSelector types exist: the screen-side one (in CardSelection,
// which this file also needs for NCardRewardSelectionScreen) and the automation one
// the card reward reads. Alias the automation one so the name is never ambiguous.
using ICardSelector = MegaCrit.Sts2.Core.TestSupport.ICardSelector;

namespace CoopBots;

/// <summary>
/// Native test-selector integration for driven seats. Permanent grid choices
/// await construction workers. Local card rewards normally retain the native
/// screen and its async completion (BotLocalRewardChoicePatch); the synchronous
/// reward method remains for native test callers that have no screen.
/// </summary>
internal sealed class BotCardSelector : ICardSelector
{
    private static IDisposable? _scope;

    /// <summary>
    /// The one entry point. Called EVERY TICK with the run's players, because a seat can
    /// be handed over or taken back at any moment, and the answer is a property of the
    /// whole table rather than of any one seat.
    ///
    /// INSTALL ONLY WHEN EVERY SEAT ON THIS MACHINE IS DRIVEN. The game consults
    /// <c>CardSelectCmd.Selector</c> BEFORE it asks who is choosing — see the decompiled
    /// `FromChooseACardScreen`: `if (Selector != null) { … } else { … ShouldSelectLocalCard(player) …
    /// WaitForRemoteChoice … }`. So while a selector is installed it answers for EVERY
    /// player this machine resolves a choice for, not only the driven seats. On a mixed
    /// table (a human still playing, another seat handed over) that silently takes a
    /// human's potion / Survivor / event choice with the bot's brain — their screen never
    /// opens — and skips `PlayerChoiceSynchronizer.SyncLocalChoice` for it, so the other
    /// peers keep waiting for a choice message that is never sent. Every other seam in
    /// this mod is per seat (`AutoPilot.Drives` in the CardSelectCmd prefixes, the relic
    /// patch and the remote-choice patch); this is the only one that cannot decline per
    /// seat, so it has to be scoped at install time instead.
    ///
    /// Card rewards use their native asynchronous screen/remote choice seams,
    /// including handed-over seats on a mixed human/bot table.
    /// </summary>
    public static void SyncForRun(IReadOnlyList<Player>? players)
    {
        var everySeatDriven = players is { Count: > 0 } && players.All(player => AutoPilot.Drives(player.NetId));
        if (everySeatDriven) EnsureInstalled();
        else Uninstall();
    }

    /// <summary>Idempotent, and safe to call every frame. Pushed rather than used so
    /// the game's own test selector (if one is ever active) keeps working underneath
    /// instead of tripping <c>UseSelector</c>'s "already active" guard.
    ///
    /// Not localOnly: <c>CardReward</c> reads <c>CardSelectCmd.Selector</c>, which is the
    /// top of the NON-local stack. Installing on the local stack put it where only
    /// <c>LocalSelector</c> looks, so the reward found nothing and threw "Card selector
    /// unset during test!" — the seat's card was skipped, and the log's
    /// "clearing 0/1 leaked selector(s)" showed one entry on each side of that mix-up.</summary>
    private static void EnsureInstalled()
    {
        if (_scope is not null) return;
        try { _scope = CardSelectCmd.PushSelector(new BotCardSelector()); }
        catch (Exception error) { Log.Warn($"CoopBots could not install the card selector: {error.GetBaseException().Message}"); }
    }

    private static void Uninstall()
    {
        _scope?.Dispose();
        _scope = null;
    }

    public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        // `options` is an IEnumerable, not a list, and the caller may hand back a
        // lazy filter over a live pile: materialise once so the brain and the answer
        // see the same cards.
        var cards = options.ToList();
        var player = cards.FirstOrDefault()?.Owner;
        if (player is null || cards.Count == 0)
            return Array.Empty<CardModel>();

        // minSelect == 0 is how a declinable screen says so; the brain treats an
        // empty answer as a real option only then.
        var maySkip = minSelect == 0;
        if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
            return BotBrain.SelectCards(player, cards, minSelect, maxSelect, "CardSelector", maySkip).ToList();
        return await Building.ConstructionDecision.RunAsync(player,
            () => BotBrain.SelectCards(player, cards, minSelect, maxSelect, "CardSelector", maySkip).ToList(),
            () => { if (!AutoPilot.Drives(player.NetId)) throw new OperationCanceledException(); return Building.ConstructionDecision.Cards(cards); });
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        var player = options.FirstOrDefault()?.Card.Owner;
        if (player is null || options.Count == 0)
            return default;

        var cards = options.Select(option => option.Card).ToList();
        // -1 is the brain's skip. The game reads "no card and no alternative" as
        // "the player skipped", so a skip is a null card rather than an out-of-range
        // index — a negative index reaches the reward's own list indexer unguarded
        // and throws (see RewardPatches, which is where that bit the live log 17
        // times per run). With Pael's Wing the skip becomes the SACRIFICE
        // alternative; that is the only case the alternative is returned.
        var decision = BotBrain.ChooseCardRewardDecision(player, cards, alternatives.Select(candidate => candidate.OptionId).ToList());
        if (decision.AlternativeId is { } alternativeId
            && alternatives.FirstOrDefault(candidate => string.Equals(
                candidate.OptionId, alternativeId, StringComparison.OrdinalIgnoreCase)) is { } alternative)
            return new CardRewardSelection { alternative = alternative };
        if (decision.Index < 0 || decision.Index >= cards.Count)
            return default;
        return new CardRewardSelection { card = cards[decision.Index] };
    }
}

// Keep the native reward screen and its completion source. The local selector
// interface is synchronous; using the screen's existing asynchronous wait keeps
// animation, manual selection and native choice synchronization intact.
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.OptionSelected))]
internal static class BotLocalRewardChoicePatch
{
    private static readonly System.Reflection.FieldInfo Completion = AccessTools.Field(
        typeof(NCardRewardSelectionScreen), "_completionSource");
    private static readonly System.Reflection.FieldInfo ExtraOptions = AccessTools.Field(
        typeof(NCardRewardSelectionScreen), "_extraOptions");
    private static void Postfix(NCardRewardSelectionScreen __instance, Task<int?> __result)
    {
        if (LocalContext.NetId is not { } me || !AutoPilot.Drives(me)
            || BotRewardDriver.ActiveReward(me) is not { } reward) return;
        _ = Complete(__instance, __result, reward);
    }
    private static async Task Complete(NCardRewardSelectionScreen screen, Task<int?> native,
        MegaCrit.Sts2.Core.Rewards.CardReward reward)
    {
        using var cancellation = new CancellationTokenSource();
        async Task StopWhenAnswered()
        {
            try { await native; }
            catch { /* Native screen owns its own completion failure. */ }
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        _ = StopWhenAnswered();
        try
        {
            var choice = await BotBrain.ChooseCardRewardAsync(reward.Player, reward.Cards.ToArray(), cancellation.Token);
            if (native.IsCompleted || !Godot.GodotObject.IsInstanceValid(screen)
                || !AutoPilot.Drives(reward.Player.NetId)
                || !ReferenceEquals(BotRewardDriver.ActiveReward(reward.Player.NetId), reward)) return;
            // The screen's completion index counts the alternatives AFTER the cards
            // (`_options.Count + altIndex`). With Pael's Wing a skipped reward becomes
            // the SACRIFICE alternative rather than a plain skip.
            var index = choice;
            if (index < 0
                && ExtraOptions.GetValue(screen) is IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> extras)
            {
                var decision = BotBrain.ApplySkipPolicy(reward.Player, index,
                    extras.Select(candidate => candidate.OptionId).ToList());
                if (decision.AlternativeId is { } alternativeId
                    && extras.ToList().FindIndex(candidate => string.Equals(
                        candidate.OptionId, alternativeId, StringComparison.OrdinalIgnoreCase)) is var sacrifice
                    && sacrifice >= 0)
                    index = reward.Cards.Count() + sacrifice;
            }
            ((TaskCompletionSource<int?>)Completion.GetValue(screen)!).TrySetResult(index < 0 ? null : index);
        }
        catch (OperationCanceledException) { /* Native screen remains available to its human owner. */ }
        catch (Exception error) { Log.Error($"CoopBots local reward computation failed: {error}"); }
    }
}
