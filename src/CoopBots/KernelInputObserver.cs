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
    private static long materialVersion;
    private static readonly Dictionary<GameAction, Action<GameAction>> pending = new();
    internal static long Revision => Interlocked.Read(ref revision);
    // Unlike the native action id, this does not change when a human only
    // marks the current turn ready. It still distinguishes settled Bot cards,
    // bottles and unknown actions at an otherwise unchanged decision point.
    internal static long MaterialVersion => Interlocked.Read(ref materialVersion);
    internal static bool IsReadinessOnly(GameAction action)
        => action.GetType() == typeof(EndPlayerTurnAction);

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
        Interlocked.Exchange(ref materialVersion, 0);
        if (battle is not null) queue.ActionEnqueued += Enqueued;
    }

    private static void Enqueued(GameAction action)
    {
        // Unknown human mod actions can affect combat too. Automatic Ready is
        // a protocol acknowledgement, excluded by the shared classifier; it
        // neither invalidates a script nor starts an optional comparison.
        var battle = observedBattle;
        if (action.GetType() == typeof(ReadyToBeginEnemyTurnAction) || battle is not CombatState combat
            || !combat.Players.Any(player => player.NetId == action.OwnerId)) return;
        if (pending.ContainsKey(action)) return;
        pending.Add(action, Finished);
        action.AfterFinished += Finished;
        action.BeforeCancelled += Cancelled;
        void Finished(GameAction settled)
        {
            settled.AfterFinished -= Finished;
            settled.BeforeCancelled -= Cancelled;
            // Native callbacks are normally once, but duplicated/cancelled
            // notifications must not manufacture multiple dirty revisions.
            if (!pending.Remove(settled)) return;
            if (!ReferenceEquals(observedBattle, battle)) return;
            // Native EndPlayerTurnAction is a readiness signal; the native
            // side transition is a separate phase/round boundary. Keep an
            // already running full comparison and an executable closing tail.
            // Unknown derived/mod actions remain material input.
            if (IsReadinessOnly(settled))
            {
                Log.Info($"CoopBots external input: readiness owner={settled.OwnerId} action={settled.GetType().Name} id={settled.Id} revision={Revision} material={MaterialVersion}");
                return;
            }
            Interlocked.Increment(ref materialVersion);
            if (!ExternalCombatInput.IsHumanAction(settled)) return;
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
