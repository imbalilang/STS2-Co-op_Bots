namespace CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    internal sealed record PartySetupPause(TurnStartChoiceRequest Request,
        PredictionExecutionContinuation Continuation);
    // CombatManager allows every participant's setup to reach its first pause,
    // then fires the shared side hook before resuming those setup tasks. These
    // continuations remain on this SAME synchronous branch; they are never sent
    // to another branch and cannot escape the party turn-start driver.
    internal int PreparePartySetupCapture() => History.PrepareManualCardChoice();

    internal void BeginPartySetupCapture(int historyStart)
    {
        GuardOrdinaryExecutionContinuationFork();
        if (CurrentFrame != null || _damageSource != null || _activeDrawDepth != 0 || ActionRelicTriggers != null)
            throw new InvalidOperationException("Party setup still owns active CLR scopes.");
        // Another seat can own a qualified draw/card continuation in the common
        // history suffix. Do not seal that suffix or treat it as ordinary work.
        StateStore.AssertForkable();
        _executionContinuationHistoryStart = historyStart;
        _executionContinuationRejected = false;
        _pendingExecutionSteps = null;
        _captureExecutionContinuation = true;
    }

    internal PartySetupPause? DetachPartySetupPause(IReadOnlyList<PartySetupPause> earlier)
    {
        var combat = (SimulatedCombatState)State.CombatState;
        var request = combat.PendingTurnStartChoice;
        if (_captureExecutionContinuation || _executionContinuationRejected || request is null
            || _pendingExecutionSteps is not { Count: > 0 } steps
            || combat is not ICombatPredictionExecutionContinuationState { CanCaptureExecutionContinuation: true }) return null;
        var all = earlier.SelectMany(p => p.Continuation.Steps).Concat(steps).ToArray();
        if (!History.SupportsExecutionContinuation(_executionContinuationHistoryStart,
                all.SelectMany(s => s.Frame.DeferredEntries), all.SelectMany(s => s.Frame.ActiveCardPlays))) return null;
        var continuation = new PredictionExecutionContinuation(_executionContinuationHistoryStart, steps);
        _pendingExecutionSteps = null;
        combat.ClearPendingTurnStartChoice();
        return new(request, continuation);
    }

    internal bool ResumePartySetupPause(PartySetupPause pause)
    {
        GuardOrdinaryExecutionContinuationFork();
        _preparedExecutionContinuation = pause.Continuation;
        return ResumeExecutionContinuation(pause.Continuation);
    }

    internal void DetachPreparedPartySetupProbe(PredictionExecutionContinuation continuation)
    {
        if (!ReferenceEquals(_preparedExecutionContinuation, continuation))
            throw new InvalidOperationException("Party setup probe does not own its prepared continuation.");
        _preparedExecutionContinuation = null;
    }

    internal CombatPredictionSimulator ForkPartySetupPause(PartySetupPause pause,
        out PredictionExecutionContinuation continuation)
    {
        GuardOrdinaryExecutionContinuationFork();
        var combat = (SimulatedCombatState)State.CombatState;
        if (combat.HasPendingChoice || combat.HasActiveActionChoices)
            throw new InvalidOperationException("Party setup probe requires a settled shared side prefix.");
        combat.SetPendingTurnStartChoice(pause.Request);
        _ownedExecutionContinuation = pause.Continuation;
        try { return ForkExecutionContinuation(pause.Continuation, out continuation); }
        finally { combat.ClearPendingTurnStartChoice(); _ownedExecutionContinuation = null; }
    }

    internal PartySetupPause CombinePartySetupPauses(IReadOnlyList<PartySetupPause> pauses,
        IReadOnlyList<ICombatPredictionExecutionFrame> tails)
    {
        if (pauses.Count == 0) throw new InvalidOperationException("No paused party setup.");
        var steps = new List<PredictionExecutionStep>();
        for (var i = 0; i < pauses.Count; i++)
        {
            steps.AddRange(pauses[i].Continuation.Steps);
            if (i < tails.Count) steps.Add(new(null, 0, null, tails[i]));
        }
        var first = pauses[0];
        if (!History.SupportsExecutionContinuation(first.Continuation.HistoryStart,
                steps.SelectMany(s => s.Frame.DeferredEntries), steps.SelectMany(s => s.Frame.ActiveCardPlays)))
            throw new InvalidOperationException("Combined party setup history is not qualified.");
        return first with { Continuation = new(first.Continuation.HistoryStart, steps) };
    }
}
