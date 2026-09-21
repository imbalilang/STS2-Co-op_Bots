using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

/// <summary>
/// Clicks the event room's "continue" for a handed-over seat.
///
/// This one is not a synchronizer choice, which is why every other driver in this
/// mod misses it. <c>NEventRoom.OptionButtonClicked</c> short-circuits the proceed
/// option before it ever reaches <c>EventSynchronizer.ChooseLocalOption</c>:
///
///     if (option.IsProceed) { TaskHelper.RunSafely(option.Chosen()); return; }
///
/// and the option it acts on is synthesised in <c>NEventRoom.SetOptions</c> only for
/// the UI — it is never in <c>eventModel.CurrentOptions</c>. So a bot's event options
/// can all be answered through the synchronizer while the room still waits for a
/// click that only the local human could make. That is what stalled a handed-over
/// table on the very first event of the run.
///
/// <c>Proceed</c> itself is two lines of local UI — enable travel and open the map —
/// so calling it is safe and lands the run in the state the map-vote driver expects.
/// </summary>
internal static class BotEventProceedDriver
{
    private static readonly MethodInfo? Proceed = AccessTools.Method(
        typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom), "Proceed");

    private static long _nextWarnAt;
    private static bool _loggedMissing;

    internal static bool TryProceed(RunManager manager, RunState state)
    {
        // Only ever for a handed-over local seat: a synthetic bot has no event room of
        // its own here, and an ordinary human wants their own click.
        if (LocalContext.NetId is not { } me || !AutoPilot.IsAutopiloted(me)) return false;
        if (Proceed is null)
        {
            if (!_loggedMissing)
            {
                _loggedMissing = true;
                Log.Warn("CoopBots could not find NEventRoom.Proceed; a handed-over seat will stall on events.");
            }
            return false;
        }
        try
        {
            if (state.CurrentRoom?.RoomType != RoomType.Event) return false;
            // Already moved on: Proceed opens the map, so an open map means this event
            // is behind us and re-opening it would fight whatever the player is doing.
            if (NMapScreen.Instance is { IsOpen: true }) return false;
            var player = state.Players.FirstOrDefault(candidate => candidate.NetId == me);
            if (player is null) return false;
            if (manager.EventSynchronizer.GetEventForPlayer(player) is not { IsFinished: true }) return false;

            Proceed.Invoke(null, null);
            Log.Info("CoopBots event: continued past the event for the handed-over seat");
            return true;
        }
        catch (Exception error)
        {
            if (Environment.TickCount64 >= _nextWarnAt)
            {
                _nextWarnAt = Environment.TickCount64 + 1000;
                Log.Warn($"CoopBots could not continue past an event: {error.GetBaseException().Message}");
            }
        }
        return false;
    }
}
