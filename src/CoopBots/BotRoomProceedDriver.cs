using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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
/// disabled and only enables it once the camp is settled, so an enabled button means
/// the room is done. The one place that reading is wrong is the shop, where the button
/// is enabled from the start and pressing it early would skip the purchases
/// <see cref="BotShopDriver"/> still wants to make — so the shop additionally waits for
/// that driver to settle every seat it shops for.
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

    internal static bool TryProceed(RunManager manager, RunState state)
    {
        if (LocalContext.NetId is not { } me || !AutoPilot.IsAutopiloted(me)) return false;
        if (Live.Count == 0) return false;
        try
        {
            // Never race the action queue: a proceed pressed while a purchase or a card
            // play is still executing would cut it off mid-flight.
            if (manager.ActionExecutor.CurrentlyRunningAction is not null || !manager.ActionQueueSet.IsEmpty) return false;

            var shop = state.CurrentRoom as MerchantRoom;
            foreach (var button in Live.ToList())
            {
                if (button is null || !GodotObject.IsInstanceValid(button)) { Live.Remove(button); continue; }
                if (!button.IsEnabled || !button.IsVisibleInTree()) continue;
                // The shop's button is enabled from the first frame; the others only
                // become enabled once the room is genuinely finished.
                if (shop is not null && !BotShopDriver.Finished(state)) continue;

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
