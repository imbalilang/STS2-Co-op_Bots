using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

// Real native GameAction.Execute and ActionQueueSet enqueue/finish events.
// The test action supplies a controllable body instead of native UI commands;
// cancellation-signal wiring is dispatched through the native event field.
// R2 raw exceptions and receipts are appended after physical runs.
// R2 actual 2026-10-09: independent private forced Rebuild 0, named runtime RED 1,
// exact restored source and six DLL/PDB artifacts; restored run 0.
// Receipt: work/architecture-refactor-20261009/r2/root-planning-combined-physical-audit.v3.json
// Scope: supported state/control/input contracts, not full gates or live win rate.
// human-enqueue-before-settlement: System.InvalidOperationException: Mixed input: enqueued human input cannot cancel a request before it settles
// human-finished-subscription: System.InvalidOperationException: Mixed input: one actual AfterFinished event cancels pending root work exactly once
// human-authority-classification: System.InvalidOperationException: Mixed input: handed-over seat's Bot action is not external human input
// human-cancel-detaches-finish: System.InvalidOperationException: Mixed input: cancelled observer cannot survive into a later completion
// human-battle-detaches-listeners: System.InvalidOperationException: Mixed input: changing battles detaches listeners from unfinished old actions
// human-settled-cancels-request: System.InvalidOperationException: Mixed input: one actual AfterFinished event cancels pending root work exactly once
internal static class MixedInputLifecycleScenarios
{
    private sealed class ControlledEndAction(Player owner) : EndPlayerTurnAction(owner, 1)
    {
        internal readonly TaskCompletionSource Completion = new();
        protected override Task ExecuteAction() => Completion.Task;
    }

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        foreach (var test in new (string Name, Action Body)[] {
            ("settled", Settled), ("authority", Authority), ("cancelled", Cancelled), ("battle", Battle) })
            if (selected is null || selected == test.Name)
            { test.Body(); Console.WriteLine("PASS: mixed input " + test.Name); }
    }

    private static void Settled()
    {
        var (battle, owner, _) = KernelPlanningStateScenarios.Board();
        var queue = new ActionQueueSet([owner]); queue.CombatStarted();
        var planner = AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
        var requestField = AccessTools.Field(planner.GetType(), "tournamentRequest");
        var previous = requestField.GetValue(planner);
        var request = new KernelDecisionRequest(901, 10000);
        var wasDriven = AutoPilot.IsAutopiloted(owner.NetId); AutoPilot.Set(owner.NetId, false);
        requestField.SetValue(planner, request);
        try
        {
            KernelInputObserver.Observe(queue, battle);
            var action = new ControlledEndAction(owner); queue.EnqueueWithoutSynchronizing(action);
            Check(KernelInputObserver.Revision == 0 && !request.IsCancelled,
                "enqueued human input cannot cancel a request before it settles");
            var execution = action.Execute();
            Check(KernelInputObserver.Revision == 0 && !request.IsCancelled && !execution.IsCompleted,
                "executing human input cannot publish a mid-action root");
            action.Completion.SetResult(); execution.GetAwaiter().GetResult();
            Check(KernelInputObserver.Revision == 1 && request.IsCancelled,
                "one actual AfterFinished event cancels pending root work exactly once");
        }
        finally
        {
            KernelInputObserver.Observe(queue, null);
            requestField.SetValue(planner, previous); AutoPilot.Set(owner.NetId, wasDriven);
        }
    }

    private static void Authority()
    {
        var (battle, owner, _) = KernelPlanningStateScenarios.Board();
        var queue = new ActionQueueSet([owner]); queue.CombatStarted();
        var wasDriven = AutoPilot.IsAutopiloted(owner.NetId); AutoPilot.Set(owner.NetId, true);
        try
        {
            KernelInputObserver.Observe(queue, battle);
            var ownAction = new ControlledEndAction(owner); queue.EnqueueWithoutSynchronizing(ownAction);
            ownAction.Completion.SetResult(); ownAction.Execute().GetAwaiter().GetResult();
            Check(KernelInputObserver.Revision == 0, "handed-over seat's Bot action is not external human input");
            AutoPilot.Set(owner.NetId, false);
            var humanAction = new ControlledEndAction(owner); queue.EnqueueWithoutSynchronizing(humanAction);
            humanAction.Completion.SetResult(); humanAction.Execute().GetAwaiter().GetResult();
            Check(KernelInputObserver.Revision == 1, "returning control to a human changes subsequent input classification");
        }
        finally { KernelInputObserver.Observe(queue, null); AutoPilot.Set(owner.NetId, wasDriven); }
    }

    private static void Cancelled()
    {
        var (battle, owner, _) = KernelPlanningStateScenarios.Board();
        var queue = new ActionQueueSet([owner]); queue.CombatStarted();
        var wasDriven = AutoPilot.IsAutopiloted(owner.NetId); AutoPilot.Set(owner.NetId, false);
        try
        {
            KernelInputObserver.Observe(queue, battle);
            var action = new ControlledEndAction(owner); queue.EnqueueWithoutSynchronizing(action);
            var eventField = AccessTools.Field(typeof(GameAction), "BeforeCancelled");
            ((Action<GameAction>?)eventField.GetValue(action))?.Invoke(action);
            Check(KernelInputObserver.Revision == 0
                && AccessTools.Field(typeof(GameAction), "BeforeCancelled").GetValue(action) is null,
                "cancelled input removes its observer without publishing a revision");
            // The event signal has already detached the observer; an orphaned
            // completion must not turn this cancelled input into a new root.
            action.Completion.SetResult(); action.Execute().GetAwaiter().GetResult();
            Check(KernelInputObserver.Revision == 0, "cancelled observer cannot survive into a later completion");
        }
        finally { KernelInputObserver.Observe(queue, null); AutoPilot.Set(owner.NetId, wasDriven); }
    }

    private static void Battle()
    {
        var (oldBattle, owner, _) = KernelPlanningStateScenarios.Board();
        var queue = new ActionQueueSet([owner]); queue.CombatStarted();
        var (newBattle, newOwner, _) = KernelPlanningStateScenarios.Board();
        var replacement = new ActionQueueSet([newOwner]); replacement.CombatStarted();
        var wasDriven = AutoPilot.IsAutopiloted(owner.NetId); AutoPilot.Set(owner.NetId, false);
        try
        {
            KernelInputObserver.Observe(queue, oldBattle);
            var old = new ControlledEndAction(owner); queue.EnqueueWithoutSynchronizing(old);
            KernelInputObserver.Observe(replacement, newBattle);
            Check(AccessTools.Field(typeof(GameAction), "BeforeCancelled").GetValue(old) is null,
                "changing battles detaches listeners from unfinished old actions");
            old.Completion.SetResult(); old.Execute().GetAwaiter().GetResult();
            Check(KernelInputObserver.Revision == 0, "old battle completion cannot modify the new battle revision");
            var current = new ControlledEndAction(newOwner); replacement.EnqueueWithoutSynchronizing(current);
            current.Completion.SetResult(); current.Execute().GetAwaiter().GetResult();
            Check(KernelInputObserver.Revision == 1, "new battle queue remains observed after old completion");
        }
        finally { KernelInputObserver.Observe(replacement, null); AutoPilot.Set(owner.NetId, wasDriven); }
    }

    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Mixed input: " + message); }
}
