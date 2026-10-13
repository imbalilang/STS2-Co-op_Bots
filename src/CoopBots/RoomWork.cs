using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// Owner-requested room completion barrier. Track the whole async operation,
// including gaps between evaluations, without holding the game thread.
internal static class RoomWork
{
    private static readonly Dictionary<object, int> active = new(ReferenceEqualityComparer.Instance);
    private static readonly System.Reflection.FieldInfo EventTasks =
        AccessTools.Field(typeof(EventSynchronizer), "_pendingOptionTasks");

    internal static IDisposable Begin(object room)
    {
        lock (active) active[room] = active.GetValueOrDefault(room) + 1;
        return new Lease(room);
    }
    internal static bool Pending(object? room)
    {
        lock (active) return room is not null && active.ContainsKey(room);
    }
    private sealed class Lease(object room) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (active)
            {
                if (disposed) return;
                disposed = true;
                if (--active[room] == 0) active.Remove(room);
            }
        }
    }

    internal static string? Reason(bool calculation, bool camp, bool shop, bool events, bool rewards)
        => calculation ? "room calculation/operation pending"
        : camp ? "camp choices pending" : shop ? "shop transactions pending"
        : events ? "event choices/effects pending" : rewards ? "rewards pending" : null;

    internal static string? Waiting(RunManager manager, RunState state)
    {
        var room = state.CurrentRoom;
        if (Pending(room)) return Reason(true, false, false, false, false);
        var driven = state.Players.Where(p => AutoPilot.Drives(p.NetId) && p.Creature.IsAlive).ToArray();
        var camp = room is RestSiteRoom && driven.Any(p => manager.RestSiteSynchronizer.GetOptionsForPlayer(p).Count > 0);
        var shop = room is MerchantRoom && !BotShopDriver.Finished(state) && !BotShopDriver.FaultedIn(state);
        // EventRoom is installed before its asset preload completes. BeginEvent
        // then fills Events one seat at a time; GetEventForPlayer indexes that
        // list without an initialization guard. Departure must wait through
        // both gaps instead of reading a missing seat or the preceding event.
        var events = room is EventRoom eventRoom &&
            (manager.EventSynchronizer.Events.Count < state.Players.Count
             || manager.EventSynchronizer.Events.Any(e => e.Id != eventRoom.CanonicalEvent.Id)
             || driven.Any(p => !manager.EventSynchronizer.GetEventForPlayer(p).IsFinished)
             || ((IEnumerable<Task>)EventTasks.GetValue(manager.EventSynchronizer)!).Any(t => !t.IsCompleted));
        var rewards = MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Peek()
            is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen && !BotRewardsScreenDriver.Finished(state);
        return Reason(Pending(room), camp, shop, events, rewards);
    }
}
