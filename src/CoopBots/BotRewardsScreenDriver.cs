using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
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
/// Shape follows the game's own AutoSlay handler (RewardsScreenHandler), including its
/// potion rule: a potion reward is only clickable with an open potion slot, and
/// clicking one with full slots would open a discard prompt instead.
/// </summary>
internal static class BotRewardsScreenDriver
{
    private static NRewardsScreen? _screen;
    private static readonly HashSet<NRewardButton> Attempted = new();
    private static long _nextWarnAt;

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
            }

            var hasPotionSlots = state.Players.FirstOrDefault(p => p.NetId == me)?.HasOpenPotionSlots ?? false;
            var button = Descendants<NRewardButton>(screen)
                .FirstOrDefault(candidate => candidate.IsEnabled && !Attempted.Contains(candidate)
                    && (candidate.Reward is not PotionReward || hasPotionSlots));
            if (button is null) return false;

            Attempted.Add(button);
            button.ForceClick();
            Log.Info($"CoopBots rewards: took {button.Reward?.GetType().Name ?? "a reward"} "
                + "for the handed-over seat");
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

    // Godot's own FindChildren needs a type name string; walking the tree keeps this
    // typed and independent of how the screen names its nodes.
    private static IEnumerable<T> Descendants<T>(Node node) where T : Node
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
