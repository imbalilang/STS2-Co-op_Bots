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
/// The card-reward seam for a handed-over seat.
///
/// A synthetic bot never touches this: <c>CardReward.OnSelect</c> only reads
/// <see cref="CardSelectCmd.Selector"/> when the reward's owner is the LOCAL player
/// and no screen was opened, and a bot's reward resolves remotely instead. A real
/// player's seat handed over from the room panel is exactly that case — local owner,
/// and we want no screen — so the game's own test hatch is the right seam rather than
/// a reimplementation of the reward body (which also writes the run history and moves
/// the card into the deck).
///
/// <see cref="CardRewardScreenSuppression"/> supplies the other half: with the screen
/// suppressed the selection falls through to this selector; with the screen shown it
/// would wait for a click that a watching player has handed away.
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
    /// WHAT THIS COSTS ON A MIXED TABLE: a handed-over seat's CARD REWARD falls back to
    /// the pre-0.37.0 behaviour (`CardReward` finds no screen and no selector, throws
    /// "Card selector unset during test!", and that seat skips the card). Its card
    /// *choices* are unaffected: those go through the per-seat CardSelectCmd prefixes.
    /// Card rewards for synthetic bots are unaffected too — they never use this seam.
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

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        // `options` is an IEnumerable, not a list, and the caller may hand back a
        // lazy filter over a live pile: materialise once so the brain and the answer
        // see the same cards.
        var cards = options.ToList();
        var player = cards.FirstOrDefault()?.Owner;
        if (player is null || cards.Count == 0)
            return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

        // minSelect == 0 is how a declinable screen says so; the brain treats an
        // empty answer as a real option only then.
        var maySkip = minSelect == 0;
        var chosen = BotBrain.SelectCards(player, cards, minSelect, maxSelect, "CardSelector", maySkip).ToList();
        return Task.FromResult<IEnumerable<CardModel>>(chosen);
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
        // times per run).
        var index = BotBrain.ChooseCardReward(player, cards);
        if (index < 0 || index >= cards.Count)
            return default;
        return new CardRewardSelection { card = cards[index] };
    }
}

/// <summary>
/// Keeps the reward screen from opening for a seat that has been handed over. The
/// screen is the only thing standing between <c>CardReward.OnSelect</c> and the
/// selector path, and it is created only for the local player — so suppressing it
/// here is what makes the local seat behave like the remote one.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.ShowScreen))]
internal static class CardRewardScreenSuppression
{
    private static bool Prefix(ref NCardRewardSelectionScreen? __result)
    {
        if (LocalContext.NetId is not { } me || !AutoPilot.IsAutopiloted(me))
            return true;
        __result = null;
        return false;
    }
}
