using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
// Aliased: a bare `using Godot;` makes Environment ambiguous with System.Environment.
using Node = Godot.Node;
using GodotObject = Godot.GodotObject;

namespace CoopBots;

/// <summary>
/// Takes the post-combat rewards on the reward SCREEN for a handed-over seat.
///
/// A synthetic bot never needs this: <c>RewardPatches</c> replaces <c>RewardsSet.Offer</c>
/// for it and selects every reward through the synchronizer, so no screen exists. A
/// handed-over seat is the local player, so the original <c>Offer</c> runs and creates
/// <c>NRewardsScreen</c> — and a screen full of reward buttons is a screen that waits
/// for clicks nobody is going to make. The card reward is already answered by
/// <see cref="BotCardSelector"/>; gold, potions and relics are buttons here.
///
/// Shape follows the game's own AutoSlay handler (RewardsScreenHandler). For
/// an improved potion reward, first finish a native discard action, then click
/// with an open slot; do not open the unattended full-belt discard prompt.
/// </summary>
internal static class BotRewardsScreenDriver
{
    private static NRewardsScreen? _screen;
    private static readonly HashSet<NRewardButton> Attempted = new();
    private static long _nextWarnAt;
    private static bool _beltFullReported;
    private static Task? _discardPending;
    private static readonly HashSet<NRewardButton> ReplacementAttempted = new();
    private static readonly HashSet<Reward> ReportedMissing = new();

    internal static bool TryDrive(RunManager manager, RunState state)
    {
        if (LocalContext.NetId is not { } me || !AutoPilot.IsAutopiloted(me)) return false;
        if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen) return false;
        try
        {
            if (!GodotObject.IsInstanceValid(screen)) return false;
            // Attempted is per screen instance: a new fight builds a new screen, and a
            // button from the previous one must not suppress this one's.
            if (!ReferenceEquals(_screen, screen))
            {
                _screen = screen;
                Attempted.Clear();
                _beltFullReported = false;
                ReplacementAttempted.Clear();
                ReportedMissing.Clear();
            }

            if (_discardPending is { IsCompleted: false }) return true;
            if (_discardPending is { } finished)
            {
                _discardPending = null;
                if (finished.IsFaulted) Log.Warn($"CoopBots reward discard failed: {finished.Exception}");
            }
            var player = state.Players.FirstOrDefault(p => p.NetId == me);
            var hasPotionSlots = player?.HasOpenPotionSlots ?? false;
            var button = NextRewardButton(screen, player);
            if (button is null)
            {
                // The driver has taken everything it can from this screen, but the
                // reward set is not necessarily complete: a potion with a full belt,
                // or a card reward whose nested selection is unavailable on a mixed
                // table, stays in the list. The native `RewardsSet.Offer` task is
                // awaiting exactly that completion, so leaving the screen up holds
                // the room forever — measured as the shop deadlock (Orrery/Cauldron).
                // Non-terminal screens expose their proceed button as "Skip", and
                // pressing it runs SkipLocalRewardsSet, which completes the task.
                ReportMissingRewards(screen);
                if (TryPressSkip(screen)) return true;

                // A full belt is final only after considering a better reward.
                if (!hasPotionSlots && !_beltFullReported
                    && Descendants<NRewardButton>(screen).Any(candidate =>
                        candidate.IsEnabled && !Attempted.Contains(candidate)
                        && candidate.Reward is PotionReward))
                {
                    _beltFullReported = true;
                    Log.Info("CoopBots rewards: leaving the potion reward — full belt and no eligible "
                        + "replacement for the handed-over seat.");
                }
                return false;
            }

            if (player is not null && !hasPotionSlots && button.Reward is PotionReward { Potion: { } incoming }
                && BotPotionInventory.Replacement(player, incoming) is { } old)
            {
                ReplacementAttempted.Add(button);
                var action = new DiscardPotionGameAction(player, (uint)player.GetPotionSlotIndex(old), false);
                Log.Info(BotPotionInventory.Describe(player, old, incoming));
                manager.ActionQueueSynchronizer.RequestEnqueue(action);
                _discardPending = action.CompletionTask;
                return true;
            }
            Attempted.Add(button);
            button.ForceClick();
            // "took" is only true for a reward this click actually CLAIMS. A CardReward
            // click merely opens the nested choice, which BotCardSelector may answer
            // with a skip — measured live 2026-09-29, the Act-1 boss card reward was
            // answered with a skip while this line read "took CardReward", the exact
            // opposite of what happened.
            var name = button.Reward?.GetType().Name ?? "a reward";
            Log.Info(button.Reward is CardReward
                ? $"CoopBots rewards: answered {name} for the handed-over seat "
                    + "(the nested choice decides whether a card was taken)"
                : $"CoopBots rewards: took {name} for the handed-over seat");
            return true;
        }
        catch (Exception error)
        {
            if (Environment.TickCount64 >= _nextWarnAt)
            {
                _nextWarnAt = Environment.TickCount64 + 1000;
                Log.Warn($"CoopBots could not take a reward from the screen: {error.GetBaseException().Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// Has this driver nothing left to take off the current reward screen?
    ///
    /// This exists because the reward screen's own continue button is enabled from the
    /// FIRST frame — the same trap the shop has. <see cref="BotRoomProceedDriver"/> used
    /// to read "enabled" as "the room is done" and pressed it while the reward set was
    /// still open. Measured live 2026-09-22 (godot.log:12851-12857): the press exited the
    /// room, `Skipping remaining rewards ... because we're exiting the room` dropped a
    /// potion and a card reward, and the act transition then ran without the act-start
    /// NPC event — `Creating NEventRoom` never appeared, so the 80 % heal was lost too.
    ///
    /// Deliberately built on <see cref="NextRewardButton"/>, the same predicate
    /// <see cref="TryDrive"/> clicks with, so the two can never disagree. If they did,
    /// one direction holds the room open forever and the other still leaves early.
    ///
    /// A screen this driver has not seen yet is NOT finished: `Attempted` still describes
    /// the previous screen, so the first pass has to happen before we can judge.
    /// </summary>
    internal static bool Finished(RunState state)
    {
        try
        {
            if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen
                || !GodotObject.IsInstanceValid(screen)) return true;
            if (!ReferenceEquals(_screen, screen)) return false;
            if (_discardPending is { IsCompleted: false }) return false;
            var player = state.Players.FirstOrDefault(p => p.NetId == LocalContext.NetId);
            return NextRewardButton(screen, player) is null;
        }
        catch
        {
            // Not finished: a room held open is recoverable (and says so in the log),
            // skipping the reward is not. Mirrors BotShopDriver.Finished.
            return false;
        }
    }

    /// <summary>The button <see cref="TryDrive"/> would click next, or null when there is
    /// nothing left to take. A better potion remains pending while the native
    /// discard is queued; equal, weaker and unknown rewards remain skippable.</summary>
    private static NRewardButton? NextRewardButton(NRewardsScreen screen, Player? player)
        => Descendants<NRewardButton>(screen)
            .FirstOrDefault(candidate => candidate.IsEnabled && !Attempted.Contains(candidate)
                && CanTakeReward(candidate.Reward, player, ReplacementAttempted.Contains(candidate)));

    internal static bool CanTakeReward(Reward? reward, Player? player, bool replacementAttempted)
        => reward is not PotionReward potion || player?.HasOpenPotionSlots == true
            || player is not null && potion.Potion is { } incoming && !replacementAttempted
                && BotPotionInventory.Replacement(player, incoming) is not null;

    /// <summary>
    /// Every reward this screen left unclaimed. The synthetic-bot path has printed
    /// `reward not taken` since 2026-09-21, but the handed-over seat had no such line —
    /// measured live 2026-09-29, its skipped Act-1 boss CardReward was reported only as
    /// "took CardReward". Printed once per reward per screen; potions already report
    /// themselves on the full-belt path.
    /// </summary>
    private static void ReportMissingRewards(NRewardsScreen screen)
    {
        foreach (var candidate in Descendants<NRewardButton>(screen))
        {
            if (!GodotObject.IsInstanceValid(candidate) || candidate.Reward is not { } reward) continue;
            if (reward.SuccessfullySelected || reward is PotionReward) continue;
            if (!ReportedMissing.Add(reward)) continue;
            Log.Info($"CoopBots reward not taken: {reward.GetType().Name} for the handed-over seat "
                + $"(taken={reward.SuccessfullySelected}).");
        }
    }

    /// <summary>
    /// Completes a non-terminal reward set whose remaining rewards this driver cannot
    /// take. Kept as a pure predicate so the policy — skip only while a reward button
    /// remains and a usable skip button exists — is pinned by a headless test; the
    /// Godot lookup itself is the untestable wiring.
    /// </summary>
    internal static bool ShouldPressSkip(bool hasRemainingRewardButton, bool hasEnabledSkipButton, bool isTerminal)
        => !isTerminal && hasRemainingRewardButton && hasEnabledSkipButton;

    private static bool TryPressSkip(NRewardsScreen screen)
    {
        // Terminal reward screens (end of combat) use the same button object, and
        // their "Skip" text is set before the reward buttons exist; BotRoomProceedDriver
        // owns that transition and this driver must not race it. Only custom,
        // non-terminal reward sets (Orrery/Cauldron and friends) are ours to finish.
        var terminal = IsTerminal(screen);
        var remaining = Descendants<NRewardButton>(screen).Any(GodotObject.IsInstanceValid);
        var skip = Descendants<NProceedButton>(screen).FirstOrDefault(candidate =>
            GodotObject.IsInstanceValid(candidate) && candidate.IsSkip
            && candidate.IsEnabled && candidate.IsVisibleInTree());
        if (!ShouldPressSkip(remaining, skip is not null, terminal)) return false;
        skip!.ForceClick();
        Log.Info("CoopBots rewards: pressed skip — the remaining rewards cannot be taken "
            + "from this screen, and holding the completed task would freeze the room/shop.");
        return true;
    }

    private static bool IsTerminal(NRewardsScreen screen)
    {
        var value = AccessTools.Field(typeof(NRewardsScreen), "_isTerminal")?.GetValue(screen);
        return value is bool terminal && terminal;
    }

    // Godot's own FindChildren needs a type name string; walking the tree keeps this
    // typed and independent of how the screen names its nodes.
    private static IEnumerable<T> Descendants<T>(Node node) where T : Node
    {
        for (var i = 0; i < node.GetChildCount(); i++)
        {
            var child = node.GetChild(i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
