using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
// Aliased: a bare `using Godot;` makes Environment ambiguous with System.Environment.
using GodotObject = Godot.GodotObject;

namespace CoopBots;

/// <summary>
/// Presses a room's "continue" for a handed-over seat.
///
/// Room-level proceeds are local UI, not synchronizer choices: nothing in this mod's
/// driver set can reach them, because every other driver talks to a synchronizer. A
/// table where the human seats have been handed over therefore stops at the first
/// room that needs a click — which, on the run this was found on, was the very first
/// event.
///
/// The gate is the game's own: <c>NRestSiteRoom</c> starts with its proceed button
/// disabled and only enables it once the camp is settled, so for most rooms an enabled
/// button means the room is done.
///
/// <b>Two rooms break that reading</b> — their button is enabled from the first frame, so
/// "enabled" says nothing about whether the room's work is finished:
///
///   * the **shop**, where pressing early skips the purchases <see cref="BotShopDriver"/>
///     still wants to make;
///   * the **terminal reward screen**, where pressing early exits the room with the reward
///     set still open and the following act transition then skips the act-start NPC event.
///
/// Both therefore wait for their own driver to settle first. See
/// <see cref="MustWaitForDriver"/> for the policy and the measured evidence.
/// </summary>
internal static class BotRoomProceedDriver
{
    // Proceed buttons register themselves on _Ready and leave on _ExitTree. Only one
    // room node is live at a time, but the reward overlay carries its own button, so
    // the driver walks whatever is registered rather than assuming a single one.
    private static readonly List<NProceedButton> Live = new();
    private static long _nextWarnAt;

    internal static void Register(NProceedButton button)
    {
        if (!Live.Contains(button)) Live.Add(button);
    }

    internal static void Unregister(NProceedButton button) => Live.Remove(button);

    /// <summary>
    /// Must the continue be held because this room's own driver has not settled yet?
    ///
    /// Pulled out of <see cref="TryProceed"/> as a pure predicate so the policy can be
    /// pinned by a test that needs no Godot scene — the wiring around it is UI-bound and
    /// can only be read off a live log, but the policy itself is the part that regressed.
    ///
    /// It regressed once already: the reward-screen term was missing, so the bot pressed
    /// continue on a reward screen whose button is enabled from frame one. Measured live
    /// 2026-09-22 — a potion and a card reward were dropped, and the act-2 start event
    /// (with its 80 % heal) never fired.
    /// </summary>
    internal static bool MustWaitForDriver(bool shopPending, bool rewardsPending)
        => shopPending || rewardsPending;

    internal static bool TryProceed(RunManager manager, RunState state)
    {
        if (LocalContext.NetId is not { } me || !AutoPilot.IsAutopiloted(me)) return false;
        if (Live.Count == 0) return false;
        try
        {
            // Never race the action queue: a proceed pressed while a purchase or a card
            // play is still executing would cut it off mid-flight.
            if (manager.ActionExecutor.CurrentlyRunningAction is not null || !manager.ActionQueueSet.IsEmpty) return false;

            // TWO rooms ship a continue button that is enabled before the room is done, so
            // "enabled" does NOT mean "finished" in either of them:
            //
            //   * the shop — pressing early skips the purchases BotShopDriver still wants;
            //   * the terminal REWARD SCREEN — pressing early exits the room with the reward
            //     set still open, and the act transition that follows then runs WITHOUT the
            //     act-start NPC event (the one that restores 80 % of lost HP).
            //
            // Measured live 2026-09-22 (godot.log:12851-12857, all-bot act 1 → act 2):
            //   CoopBots room: pressed the continue for the handed-over seat (Boss)
            //   [RewardsSetSynchronizer] Skipping remaining rewards ... because we're exiting the room
            //   Reward set Id: 6 ... Rewards: GoldReward,PotionReward,CardReward   ← two dropped
            //   Run location changed to act 1 coord (null) room                    ← no event room
            // `Creating NEventRoom` never appeared anywhere in that run.
            //
            // The reward screen needs the same treatment the shop already had. The 2 s
            // non-combat throttle does not cover it: that is a rate limiter measured from the
            // previous tick, not a settle measured from the screen appearing.
            var shop = state.CurrentRoom as MerchantRoom;
            var rewardScreenUp = NOverlayStack.Instance?.Peek() is NRewardsScreen;
            var shopPending = shop is not null && !BotShopDriver.Finished(state);
            var rewardsPending = rewardScreenUp && !BotRewardsScreenDriver.Finished(state);
            foreach (var button in Live.ToList())
            {
                if (button is null || !GodotObject.IsInstanceValid(button)) { Live.Remove(button); continue; }
                if (!button.IsEnabled || !button.IsVisibleInTree()) continue;
                if (MustWaitForDriver(shopPending, rewardsPending))
                {
                    // Say why the continue is being held: a room held open with no log line
                    // is indistinguishable from a driver that stopped running (R5).
                    if (Environment.TickCount64 >= _nextWarnAt)
                    {
                        _nextWarnAt = Environment.TickCount64 + 2000;
                        Log.Info("CoopBots room: holding the continue — "
                            + (shopPending ? "the shop has not settled" : "the reward screen still has rewards"));
                    }
                    return false;
                }

                button.ForceClick();
                Log.Info($"CoopBots room: pressed the continue for the handed-over seat "
                    + $"({state.CurrentRoom?.RoomType.ToString() ?? "?"})");
                return true;
            }
        }
        catch (Exception error)
        {
            if (Environment.TickCount64 >= _nextWarnAt)
            {
                _nextWarnAt = Environment.TickCount64 + 1000;
                Log.Warn($"CoopBots could not press a room continue: {error.GetBaseException().Message}");
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(NProceedButton), nameof(NProceedButton._Ready))]
internal static class ProceedButtonReadyPatch
{
    private static void Postfix(NProceedButton __instance) => BotRoomProceedDriver.Register(__instance);
}

[HarmonyPatch(typeof(NProceedButton), nameof(NProceedButton._ExitTree))]
internal static class ProceedButtonExitPatch
{
    private static void Postfix(NProceedButton __instance) => BotRoomProceedDriver.Unregister(__instance);
}
