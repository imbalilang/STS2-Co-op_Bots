using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

// Upstream invalidates a single-player request on input. In multiplayer we
// observe only settled external player actions, then coalesce them until the
// next stable Poll. Owner's architecture plan, 2026-10-09. This does not read
// a mid-action board or treat a human's input as a simulation discrepancy.
internal static class KernelInputObserver
{
    private static ActionQueueSet? observedQueue;
    private static ICombatState? observedBattle;
    private static long revision;
    private static readonly Dictionary<GameAction, Action<GameAction>> pending = new();
    internal static long Revision => Interlocked.Read(ref revision);

    internal static void Observe(ActionQueueSet queue, ICombatState? battle)
    {
        if (ReferenceEquals(observedQueue, queue) && ReferenceEquals(observedBattle, battle)) return;
        if (observedQueue is not null) observedQueue.ActionEnqueued -= Enqueued;
        foreach (var pair in pending)
        {
            pair.Key.AfterFinished -= pair.Value;
            pair.Key.BeforeCancelled -= Cancelled;
        }
        pending.Clear();
        observedQueue = queue; observedBattle = battle;
        Interlocked.Exchange(ref revision, 0);
        if (battle is not null) queue.ActionEnqueued += Enqueued;
    }

    private static void Enqueued(GameAction action)
    {
        if (AutoPilot.Drives(action.OwnerId) || action is not
            (PlayCardAction or UsePotionAction or EndPlayerTurnAction or UndoEndPlayerTurnAction or DiscardPotionGameAction)) return;
        var battle = observedBattle;
        if (pending.ContainsKey(action)) return;
        pending.Add(action, Finished);
        action.AfterFinished += Finished;
        action.BeforeCancelled += Cancelled;
        void Finished(GameAction settled)
        {
            settled.AfterFinished -= Finished;
            settled.BeforeCancelled -= Cancelled;
            pending.Remove(settled);
            if (!ReferenceEquals(observedBattle, battle)) return;
            var next = Interlocked.Increment(ref revision);
            BotRuntime.ExternalInputSettled();
            Log.Info($"CoopBots external input: settled owner={settled.OwnerId} action={settled.GetType().Name} id={settled.Id} revision={next}");
        }
    }

    private static void Cancelled(GameAction action)
    {
        if (pending.Remove(action, out var finished)) action.AfterFinished -= finished;
        action.BeforeCancelled -= Cancelled;
    }
}
