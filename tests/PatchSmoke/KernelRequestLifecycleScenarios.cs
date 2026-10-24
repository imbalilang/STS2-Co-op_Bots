// V2 R2 actual 2026-10-24: production mutant build0 / named RED1 /
// exact source restore / rebuild0 / named GREEN0. Receipt:
// work/v2-validation/r2/receipts.json (only passed=true records below).
// complete-replay-keeps-estimate:
// System.InvalidOperationException: Request lifecycle: a complete nonterminal replay crossing its computation stop must retain its completed same-root candidate
using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class KernelRequestLifecycleScenarios
{
    // R2: core14 + limits20 + stopped-reference5 validation records are closed.
    // Each actual private-source mutation built0/RED1/exact restore built0/GREEN0.
    // Source bytes, DLL/PDB, named logs and exit sidecars: core-r2-proof.json and
    // runtime/r2-limits-v1/proof.json; runtime/r2-stopped-reference-v1/proof.json
    // under work/architecture-refactor-20261009/request-lifecycle/.
    // Records are fault controls, not a count of distinct gameplay defects.
    // Initial GREEN mutation hypotheses remain preserved and are not counted.
    // Observe the actual production mirror after payment, before the remainder of
    // the same card transaction. sts2.CardModel.OnPlayWrapper is not invoked by
    // mirrored Strike; this point is deliberately not an independent native oracle.
    private static readonly MethodInfo nativePlay = AccessTools.Method(typeof(CombatPredictionSimulator), "OnPlayWrapper");
    private static readonly MethodInfo launch = AccessTools.Method(typeof(TournamentRun), "Launch");
    private static readonly MethodInfo openRollout = AccessTools.Method(typeof(TournamentRun), "TryOpenNextRollout");
    private static readonly MethodInfo advanceReference = AccessTools.Method(typeof(TournamentRun), "AdvanceReference");
    private static KernelDecisionRequest? cancelAtPlay;
    private static KernelDecisionRequest? expireAtPlay;
    private static bool blockWorker;
    private static bool observeNative;
    private static int captureThread, nativeCalls;
    private static readonly ManualResetEventSlim entered = new(false), release = new(false);

    internal static void Run(string? only = null)
    {
        TestEnvironment.Ensure();
        var hooks = new Harmony("coopbots.request-lifecycle-tests");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        hooks.Patch(format, prefix: new HarmonyMethod(typeof(KernelRequestLifecycleScenarios), nameof(Format)));
        hooks.Patch(nativePlay, prefix: new HarmonyMethod(typeof(KernelRequestLifecycleScenarios), nameof(ObserveNativePlay)));
        try
        {
            if (only is null or "deadline") SharedDeadline();
            if (only is null or "pool") BoundedPool();
            if (only is null or "settlement") NativeCancellationBoundary();
            if (only is null or "terminal-opening") SettledTerminalOpening();
            if (only is null or "stopped-reference") StoppedNativeReferences();
            if (only is null or "worker-cancel") CancelRunningWorker();
            if (only is null or "queued-cancel") CancelQueuedWorker();
            if (only is null or "generation") GenerationsAndIncumbents();
            if (only is null or "replay-audit") ReplayAndAuditStops();
            if (only is null or "foreground") ForegroundStops();
            if (only is null or "replay-completed-stop") ReplayCompletedAtStop();
        }
        finally
        {
            cancelAtPlay = null; expireAtPlay = null; blockWorker = false; observeNative = false; release.Set();
            hooks.Unpatch(nativePlay, HarmonyPatchType.All, hooks.Id);
            hooks.Unpatch(format, HarmonyPatchType.All, hooks.Id);
        }
    }

    private static void SharedDeadline()
    {
        var (root, owner, _) = Board(30, 2);
        var text = root.PartyStateText();
        var request = new KernelDecisionRequest(7001, 20, 2);
        var remaining = request.RemainingMs;
        // Waiting between frames belongs to an absolute request deadline.
        // Neither constructor below is allowed to restart that clock.
        Thread.Sleep(40);
        var first = new TournamentRun(root, [owner], [owner], Options(request), 10000);
        var second = new TournamentRun(root, [owner], [owner], Options(request), 10000);
        Check(remaining <= request.BudgetMs && request.RemainingMs == 0 && request.DeadlineReached
            && first.Done && second.Done && first.Rollouts == 0 && second.Rollouts == 0
            && first.Result!.StopReason == "request-deadline" && second.Result!.StopReason == "request-deadline"
            && first.Result.RootGeneration == 7001 && second.Result.RootGeneration == 7001
            && first.Result.FirstAction is null && second.Result.FirstAction is null
            && root.PartyStateText() == text,
            // R2 RED: System.InvalidOperationException: Request lifecycle: both passes must stop at the same absolute request deadline before native work
            // Physical controls: absolute-deadline.
            "both passes must stop at the same absolute request deadline before native work");
        Console.WriteLine("PASS: one wall-clock request covers frame waits and both tournament passes without renewing its deadline.");
    }

    private static void BoundedPool()
    {
        var pool = new KernelSearchWorkPool(2, 1);
        var one = new KernelDecisionRequest(7002, 10000, 1);
        var other = new KernelDecisionRequest(7003, 10000, 2);
        using var a = new ManualResetEventSlim(false);
        using var b = new ManualResetEventSlim(false);
        using var unblock = new ManualResetEventSlim(false);
        var threads = new HashSet<int>();
        var seen = new object();
        void Work(ManualResetEventSlim signal)
        {
            lock (seen) threads.Add(Environment.CurrentManagedThreadId);
            signal.Set();
            if (!unblock.Wait(5000)) throw new InvalidOperationException("request pool fixture did not release its workers");
        }
        KernelSearchWorkPool.WorkItem? first = null, second = null, queued = null;
        try
        {
            Check(pool.TryQueue(one, () => Work(a), out first) && a.Wait(5000),
                // R2 RED: System.InvalidOperationException: Request lifecycle: first pooled job must actually enter its worker
                // Physical controls: pool-admission.
                "first pooled job must actually enter its worker");
            Check(!pool.TryQueue(one, () => { }, out _),
                // R2 RED: System.InvalidOperationException: Request lifecycle: a request must bound queued plus running jobs by its own concurrency allowance
                // Physical controls: request-outstanding-limit.
                "a request must bound queued plus running jobs by its own concurrency allowance");
            Check(pool.TryQueue(other, () => Work(b), out second) && b.Wait(5000),
                // R2 RED: System.InvalidOperationException: Request lifecycle: a second request must use the remaining global worker
                // Physical controls: configured-worker-capacity.
                "a second request must use the remaining global worker");
            Check(pool.TryQueue(other, () => { lock (seen) threads.Add(Environment.CurrentManagedThreadId); }, out queued)
                && !pool.TryQueue(null, () => { }, out _)
                && pool.ActiveJobs == 2 && pool.QueuedJobs == 1 && pool.PeakActiveJobs == 2
                && one.ActiveJobs == 1 && other.ActiveJobs == 1 && other.QueuedJobs == 1,
                // R2 RED: System.InvalidOperationException: Request lifecycle: the shared worker and queue limits must remain bounded across request generations
                // Physical controls: global-queue-limit, global-worker-limit.
                "the shared worker and queue limits must remain bounded across request generations");
        }
        finally { unblock.Set(); }
        Check(SpinWait.SpinUntil(() => first?.Finished == true && second?.Finished == true && queued?.Finished == true, 5000)
            && first!.Error is null && second!.Error is null && queued!.Error is null
            && pool.ActiveJobs == 0 && pool.QueuedJobs == 0
            && one.ActiveJobs == 0 && one.QueuedJobs == 0 && one.CompletedJobs == 1
            && other.CompletedJobs == 2 && threads.Count == 2
            && (int)AccessTools.Field(typeof(KernelSearchWorkPool), "workers").GetValue(pool)! == 2,
            // R2 RED: System.InvalidOperationException: Request lifecycle: finished jobs must release reservations and reuse bounded worker threads
            // Physical controls: release-job-reservation.
            "finished jobs must release reservations and reuse bounded worker threads");
        Console.WriteLine("PASS: pooled jobs obey global and per-request bounds, publish completion, and reuse workers across generations.");
    }

    private static void NativeCancellationBoundary()
    {
        foreach (var hp in new[] { 18, 6 })
        {
            var (root, owner, enemy) = Board(hp, 1);
            var original = root.PartyStateText();
            var options = Cheap();
            var control = new RolloutRun(root.Fork(), [owner], [owner], options);
            control.Step(); control.FinishAtBudget();
            var expected = control.State.PartyStateText();
            var request = new KernelDecisionRequest(7100 + hp, 10000);
            var run = new RolloutRun(root.Fork(), [owner], [owner], options) { StopRequested = () => request.IsStopped };
            nativeCalls = 0; cancelAtPlay = request;
            try { run.Step(); }
            finally { cancelAtPlay = null; }
            var observed = nativeCalls;
            // Observe completion before re-entry: a PRE-Step check alone must
            // not mask a missing POST-settlement response to cancellation.
            var settledAfterFirst = run.Done;
            run.Step();
            Console.WriteLine($"DIAG settlement hp={hp} observed={observed} cancelled={request.IsCancelled} done={run.Done} steps={run.CommittedSteps} actions={run.Result?.Actions.Count} energy={run.State.Energy(owner)} hand={run.State.Hand(owner).Count} enemyHP={run.State.Hp(enemy)} equal={run.State.PartyStateText()==expected} stop={run.Result?.StopReason}");
            Check(observed == 1 && request.IsCancelled && settledAfterFirst && run.Done && run.CommittedSteps == 1
                && run.Result!.Actions.Count == 1 && run.State.Energy(owner) == 2
                && run.State.Hand(owner).Count == 0 && run.State.Hp(enemy) == Math.Max(0, hp - 6)
                && run.State.PartyStateText() == expected && root.PartyStateText() == original,
                // R2 RED: System.InvalidOperationException: Request lifecycle: cancellation inside native play must settle the same paid action and stop before another action
            "cancellation inside native play must settle the same paid action and stop before another action");
            Check(hp == 6 ? run.Result!.Terminal && run.Result.Record.Verified && run.Result.Victory
                    : run.Result!.Cutoff && !run.Result.Record.Verified && run.Result.StopReason == "continuation-budget",
                // R2 RED: System.InvalidOperationException: Request lifecycle: a last-action native victory must remain complete while an interrupted live fight remains a cutoff
                // Physical controls: genuine-terminal-at-stop, partial-live-not-terminal.
                "a last-action native victory must remain complete while an interrupted live fight remains a cutoff");
        }
        Console.WriteLine("PASS: native cancellation settles payment and damage, preserves the root, and keeps real terminal evidence distinct from cutoff.");
    }

    private static void SettledTerminalOpening()
    {
        // Warm the actual production opening/continuation on an independent
        // board before opening the measured wall request. No request is widened.
        var (warmRoot, warmOwner, _) = Board(6, 1);
        using (var warm = new TournamentRun(warmRoot, [warmOwner], [warmOwner],
            Options(new KernelDecisionRequest(7150, 10000)), 10000)) Finish(warm);

        foreach (var duringOpening in new[] { true, false })
        foreach (var cancel in new[] { true, false })
        foreach (var hp in new[] { 6, 18 })
        {
            var (root, owner, _) = Board(hp, 1);
            var before = root.PartyStateText();
            var card = root.Hand(owner).Single();
            var request = new KernelDecisionRequest(7151, cancel ? 10000 : 200);
            using var run = new TournamentRun(root, [owner], [owner], Options(request), 10000);
            nativeCalls = 0; observeNative = true;
            try
            {
                if (duringOpening)
                {
                    if (cancel) cancelAtPlay = request; else expireAtPlay = request;
                }
                else
                {
                    // Execute the real native opener through the production
                    // factory, then stop before any worker can be admitted.
                    var opened = openRollout.Invoke(run, [System.Diagnostics.Stopwatch.StartNew()]);
                    Check(opened?.ToString() == "Opened",
                        "terminal-opening fixture must execute its native opener before worker admission");
                    if (cancel) request.Cancel("between-opening-and-worker");
                    else Thread.Sleep(request.RemainingMs + 5);
                }
                run.Advance(TimeSpan.FromSeconds(1));
            }
            finally { cancelAtPlay = null; expireAtPlay = null; observeNative = false; }
            var evidence = run.BestCompleted;
            Console.WriteLine($"DIAG terminal-opening during={duringOpening} cancel={cancel} hp={hp} native={nativeCalls} done={run.Done} rollouts={run.Rollouts} verified={evidence?.Record?.Verified} resultAction={run.Result?.FirstAction is not null}");
            Check(nativeCalls == 1 && run.Done && request.IsStopped && root.PartyStateText() == before,
                // R2 RED: System.InvalidOperationException: Request lifecycle: a stopped atomic opening must settle one paid action and preserve its frozen root
                // Physical controls: opening-native-provider.
                "a stopped atomic opening must settle one paid action and preserve its frozen root");
            Check(hp == 6
                    ? evidence is { Record: { Verified: true, Victory: true, RemainingEnemyHp: 0 } ending, Script: { Count: 1 } }
                        && evidence.IsFor(request)
                        && ReferenceEquals(evidence.FirstAction!.Card, card) && ending.PostCombatHp.SequenceEqual([80])
                    : evidence is null && run.Result?.Record is null,
                // R2 RED: System.InvalidOperationException: Request lifecycle: a settled terminal opening must retain genuine completed evidence while an unfinished opening remains unverified
                // Physical controls: opening-terminal-evidence, unlaunched-terminal-evidence, unfinished-opening-not-complete.
                "a settled terminal opening must retain genuine completed evidence while an unfinished opening remains unverified");
            var published = run.Result!;
            Check(cancel
                    ? published.FirstAction is null && published.Record is null
                    : hp == 6 ? published.Record is { Verified: true, Victory: true }
                        && ReferenceEquals(published.Record, evidence!.Record) && published.IsFor(request)
                        : published.FirstAction is null,
                // R2 RED: System.InvalidOperationException: Request lifecycle: terminal opening evidence may survive a deadline but cancellation must still withdraw deployment
                // Physical controls: cancelled-opening-not-deployed.
                "terminal opening evidence may survive a deadline but cancellation must still withdraw deployment");
        }
        Console.WriteLine("PASS: fully settled native terminal openings survive request stops before continuation admission without promoting unfinished openings or cancelled deployment.");
    }

    private static void StoppedNativeReferences()
    {
        var (warmRoot, warmOwner, _) = Board(6, 1);
        using (var warm = new TournamentRun(warmRoot, [warmOwner], [warmOwner],
            Options(new KernelDecisionRequest(7170, 10000)) with { EnableJointSearch = true }, 10000))
            advanceReference.Invoke(warm, [TimeSpan.FromSeconds(1)]);

        foreach (var cancel in new[] { true, false })
        foreach (var hp in new[] { 6, 18 })
        {
            var (root, owner, _) = Board(hp, 1);
            var before = root.PartyStateText();
            var request = new KernelDecisionRequest(7171, cancel ? 10000 : 200);
            using var run = new TournamentRun(root, [owner], [owner],
                Options(request) with { EnableJointSearch = true }, 10000);
            nativeCalls = 0; observeNative = true;
            try { advanceReference.Invoke(run, [TimeSpan.Zero]); }
            finally { observeNative = false; }
            var pending = (RolloutRun?)AccessTools.Field(typeof(TournamentRun), "referenceRun").GetValue(run);
            Check(nativeCalls == 1 && pending is { Result: null, CommittedSteps: 1 }
                && pending.State.HasWon == (hp == 6) && pending.State.Energy(owner) == 2
                && pending.State.Hand(owner).Count == 0,
                // R2 RED: System.InvalidOperationException: Request lifecycle: the production sliced reference must settle its paid action before its next terminal observation
                // Physical controls: reference-native-provider.
                "the production sliced reference must settle its paid action before its next terminal observation");
            if (cancel) request.Cancel("between-reference-slices"); else Thread.Sleep(request.RemainingMs + 5);
            run.Advance(TimeSpan.FromSeconds(1));
            var evidence = run.BestCompleted;
            Check(run.Done && nativeCalls == 1 && root.PartyStateText() == before
                && (hp == 6 ? evidence is { Record: { Verified: true, Victory: true, RemainingEnemyHp: 0 }, Script: { Count: 1 } }
                    && evidence.IsFor(request) && run.Rollouts == 1
                    : evidence is null && run.Result?.Record is null && run.Rollouts == 0),
                // R2 RED: System.InvalidOperationException: Request lifecycle: stopping a sliced reference must retain only its genuine settled terminal without another native action
                // Physical controls: stopped-reference-evidence, unfinished-reference-not-terminal.
                "stopping a sliced reference must retain only its genuine settled terminal without another native action");
            Check(cancel ? run.Result?.FirstAction is null && run.Result?.Record is null
                    : hp == 6 ? ReferenceEquals(run.Result?.Record, evidence!.Record) : run.Result?.Record is null,
                // R2 RED: System.InvalidOperationException: Request lifecycle: sliced-reference deadlines may retain completed deployment while cancelled references remain evidence only
                // Physical controls: cancelled-reference-not-deployed.
                "sliced-reference deadlines may retain completed deployment while cancelled references remain evidence only");
        }

        var (completedRoot, completedOwner, _) = Board(6, 1);
        var current = new KernelDecisionRequest(7172, 10000);
        using var already = new TournamentRun(completedRoot, [completedOwner], [completedOwner],
            Options(current) with { EnableJointSearch = true }, 10000);
        advanceReference.Invoke(already, [TimeSpan.FromSeconds(1)]);
        var old = already.BestCompleted!;
        current.Cancel("after-reference-consumption");
        already.Advance(TimeSpan.FromSeconds(1)); already.Advance(TimeSpan.FromSeconds(1));
        Check(old.Record is { Verified: true, Victory: true } && already.Rollouts == 1
            && already.Result?.Considered.Count == 1 && ReferenceEquals(old.Record, already.BestCompleted!.Record)
            && already.Result.FirstAction is null,
            // R2 RED: System.InvalidOperationException: Request lifecycle: completed reference evidence must be consumed once without losing a previously accepted incumbent
            // Physical controls: completed-reference-once.
            "completed reference evidence must be consumed once without losing a previously accepted incumbent");
        Console.WriteLine("PASS: native sliced references retain real endings across request stops, exclude unfinished states, withdraw cancelled deployment and avoid duplicate consumption.");
    }

    private static void CancelRunningWorker()
    {
        var (root, owner, _) = Board(30, 2);
        var request = new KernelDecisionRequest(7201, 10000, 2);
        using var tournament = new TournamentRun(root, [owner], [owner], Options(request) with { MaxDegreeOfParallelism = 2 }, 10000);
        var card = root.Hand(owner)[0]; var target = root.Enemies[0];
        var opened = root.CardBranches(card, target, 1).Single(b => b.Boundary.Length == 0);
        var action = new KernelTeamSearch.Action(owner, card, target, Choices: opened.Choices,
            ChoiceOrdinals: opened.ChoiceOrdinals, CardStateKey: root.CardStateKey(card));
        var run = new RolloutRun(opened.State, [owner], [owner], Cheap());
        entered.Reset(); release.Reset(); captureThread = Environment.CurrentManagedThreadId;
        blockWorker = true;
        try
        {
            Check((bool)launch.Invoke(tournament, [run, action, 10000d])! && entered.Wait(5000)
                && request.ActiveJobs == 1 && tournament.ActiveWorkerJobs == 1,
                "production tournament launch must publish its actual running native job");
            tournament.Cancel("superseded-root");
        }
        finally { release.Set(); }
        try
        {
            Check(SpinWait.SpinUntil(() => tournament.ActiveWorkerJobs == 0, 5000),
                "an abandoned tournament worker must drain after its current native action");
            tournament.Advance(TimeSpan.FromTicks(1));
            Check(run.Done && run.CommittedSteps == 1 && run.State.Energy(owner) == 1
                && request.ActiveJobs == 0 && request.QueuedJobs == 0 && request.CompletedJobs == 1
                && !request.IsCancelled && tournament.Result!.FirstAction is null
                && tournament.Result.StopReason == "request-cancelled:superseded-root",
                // R2 RED: System.InvalidOperationException: Request lifecycle: local cancellation must stop its native worker without killing the shared next-pass request or deploying stale work
            // R2 RED: System.InvalidOperationException: Request lifecycle: local cancellation must stop its native worker without killing the shared next-pass request or deploying stale work
            // Physical controls: local-not-shared-cancel.
            "local cancellation must stop its native worker without killing the shared next-pass request or deploying stale work");
        }
        finally { blockWorker = false; }
        Console.WriteLine("PASS: cancelling the production tournament drains its real pooled native job without another Poll or shared-request cancellation.");
    }

    private static void GenerationsAndIncumbents()
    {
        var (root, owner, _) = Board(6, 1);
        var request = new KernelDecisionRequest(7301, 10000);
        using var first = new TournamentRun(root, [owner], [owner], Options(request) with {
            Rollout = Cheap() with { CaptureActionCheckpoints = true } }, 10000);
        Finish(first);
        var completed = first.Result!;
        Check(completed.Record is { Verified: true, Victory: true } && completed.RootGeneration == 7301
            && completed.ScriptCheckpoints?.Count == completed.Script!.Count,
            // R2 RED: System.InvalidOperationException: Request lifecycle: production completed evidence must carry its actual request generation
            // Physical controls: completed-result-root-stamp.
            "production completed evidence must carry its actual request generation");
        using var capped = new TournamentRun(root, [owner], [owner],
            Options(request) with { ReferenceResult = completed }, 0);
        Check(capped.Done && capped.Result?.StopReason == "phase-deadline"
            && ReferenceEquals(capped.Result.Record, completed.Record) && capped.Result.FirstAction == completed.FirstAction
            && capped.Result.IsFor(request) && !request.IsCancelled,
            // R2 RED: System.InvalidOperationException: Request lifecycle: a phase deadline on the current root must retain its complete incumbent instead of cancelling the shared request
            // Physical controls: deadline-retains-complete-incumbent.
            "a phase deadline on the current root must retain its complete incumbent instead of cancelling the shared request");
        var fresh = new KernelDecisionRequest(7302, 10000);
        using var second = new TournamentRun(root, [owner], [owner], Options(fresh) with { ReferenceResult = completed }, 10000);
        Check(second.BestCompleted is null,
            // R2 RED: System.InvalidOperationException: Request lifecycle: a completed reference from a different request generation must not become the new incumbent
            "a completed reference from a different request generation must not become the new incumbent");
        using var relabelled = new TournamentRun(root, [owner], [owner],
            Options(fresh) with { ReferenceResult = completed with { RootGeneration = fresh.RootGeneration } }, 10000);
        Check(relabelled.BestCompleted is null,
            // R2 RED: System.InvalidOperationException: Request lifecycle: changing an old record generation number must not forge new-request provenance
            "changing an old record generation number must not forge new-request provenance");
        var replay = new KernelScriptReplayRun(root, [owner], completed, 80, fresh);
        for (var i = 0; i < 1000 && !replay.Done; i++) replay.Advance(TimeSpan.FromMilliseconds(1));
        Check(replay.Result?.Record is { Verified: true, Victory: true }
            && replay.Result.IsFor(fresh) && !ReferenceEquals(replay.Result.Record, completed.Record)
            && replay.Result.ScriptCheckpoints is null,
            // R2 RED: System.InvalidOperationException: Request lifecycle: actual new-root native replay must issue new-request provenance only for its freshly verified ending
            "actual new-root native replay must issue new-request provenance only for its freshly verified ending");
        using var revalidated = new TournamentRun(root, [owner], [owner], Options(fresh) with { ReferenceResult = replay.Result }, 10000);
        Check(ReferenceEquals(revalidated.BestCompleted?.Record, replay.Result!.Record),
            // R2 RED: System.InvalidOperationException: Request lifecycle: a genuinely revalidated ending must remain eligible as the new-request incumbent
            // Physical controls: revalidated-reference-eligible.
            "a genuinely revalidated ending must remain eligible as the new-request incumbent");
        Finish(second);
        Check(second.Result?.Record is { Verified: true, Victory: true } && second.Result.RootGeneration == 7302,
            "rejecting stale evidence must still allow a fresh native ending on the new root generation");
        first.Cancel("root-moved"); first.Advance(TimeSpan.FromTicks(1));
        Check(first.Result?.FirstAction is null && ReferenceEquals(first.BestCompleted?.Record, completed.Record)
            && first.BestCompleted!.RootGeneration == 7301,
            // R2 RED: System.InvalidOperationException: Request lifecycle: cancellation must withdraw deployment while retaining the original completed evidence for its own generation
            // Physical controls: cancel-withdraws-complete-deployment.
            "cancellation must withdraw deployment while retaining the original completed evidence for its own generation");
        Console.WriteLine("PASS: actual native incumbents retain generation provenance and survive cancellation as evidence, without stale deployment or cross-generation adoption.");
    }

    private static void CancelQueuedWorker()
    {
        var pool = KernelSearchWorkPool.Shared;
        using var unblock = new ManualResetEventSlim(false);
        var items = new List<KernelSearchWorkPool.WorkItem>();
        var enteredCount = 0;
        var (root, owner, _) = Board(30, 2);
        var request = new KernelDecisionRequest(7251, 10000, 2);
        using var tournament = new TournamentRun(root, [owner], [owner], Options(request) with { MaxDegreeOfParallelism = 2 }, 10000);
        var card = root.Hand(owner)[0]; var target = root.Enemies[0];
        var opened = root.CardBranches(card, target, 1).Single(b => b.Boundary.Length == 0);
        var action = new KernelTeamSearch.Action(owner, card, target, Choices: opened.Choices,
            ChoiceOrdinals: opened.ChoiceOrdinals, CardStateKey: root.CardStateKey(card));
        var run = new RolloutRun(opened.State, [owner], [owner], Cheap());
        var before = run.State.PartyStateText();
        try
        {
            for (var i = 0; i < pool.WorkerLimit; i++)
            {
                Check(pool.TryQueue(null, () => {
                    Interlocked.Increment(ref enteredCount);
                    if (!unblock.Wait(5000)) throw new InvalidOperationException("queued native fixture did not release its workers");
                }, out var item), "the native queued-cancel fixture must occupy the actual bounded pool");
                items.Add(item!);
            }
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref enteredCount) == pool.WorkerLimit, 5000),
                "all production worker slots must be occupied before queueing the native rollout");
            Check((bool)launch.Invoke(tournament, [run, action, 10000d])!
                && request.ActiveJobs == 0 && request.QueuedJobs == 1,
                "production launch must keep a queued native rollout without starting another worker");
            tournament.Cancel("queued-root-moved");
            nativeCalls = 0; observeNative = true;
        }
        finally { unblock.Set(); }
        try
        {
            Check(SpinWait.SpinUntil(() => tournament.ActiveWorkerJobs == 0 && items.All(item => item.Finished), 5000),
                "cancelled queued production work must publish completion without a new planner Poll");
            tournament.Advance(TimeSpan.FromTicks(1));
            Check(nativeCalls == 0 && run.CommittedSteps == 0 && run.State.PartyStateText() == before
                && request.ActiveJobs == 0 && request.QueuedJobs == 0 && request.CompletedJobs == 1
                && tournament.Result?.FirstAction is null,
                // R2 RED: System.InvalidOperationException: Request lifecycle: a cancelled queued native job must stop before its first action and release its reservation
            "a cancelled queued native job must stop before its first action and release its reservation");
        }
        finally { observeNative = false; }
        Console.WriteLine("PASS: cancellation before pooled admission performs no native action and releases the queued reservation.");
    }

    private static void ReplayAndAuditStops()
    {
        var (root, owner, _) = Board(12, 2);
        using var source = new TournamentRun(root, [owner], [owner], Options(new KernelDecisionRequest(7401, 10000)), 10000);
        Finish(source);
        var candidate = source.Result!;
        var request = new KernelDecisionRequest(7402, 10000);
        var replay = new KernelScriptReplayRun(root, [owner], candidate, 80, request);
        nativeCalls = 0; cancelAtPlay = request;
        try { replay.Advance(TimeSpan.FromSeconds(1)); }
        finally { cancelAtPlay = null; }
        Check(nativeCalls == 1 && replay.Done && replay.Result is null && replay.Failure == "request-cancelled:native-hook",
            // R2 RED: System.InvalidOperationException: Request lifecycle: partial native replay must stop after the settled action without publishing the old winning record
            "partial native replay must stop after the settled action without publishing the old winning record");

        var (potionRoot, potionOwner, target) = Board(24, 1, withPotion: true);
        var potion = potionRoot.UsablePotions(potionOwner).Single();
        var actual = potionRoot.Fork();
        Check(actual.UsePotion(potion, target, out var potionReason), "audit fixture must apply a real native bottle: " + potionReason);
        var card = actual.Hand(potionOwner).Single();
        var branch = actual.CardBranches(card, target, 1).Single(b => b.Boundary.Length == 0);
        Check(branch.State.HasTerminal && branch.State.HasWon, "audit fixture must reach a real native victory");
        var bottle = new KernelTeamSearch.Action(potionOwner, null, target, potion);
        var strike = new KernelTeamSearch.Action(potionOwner, card, target, Choices: branch.Choices,
            ChoiceOrdinals: branch.ChoiceOrdinals, CardStateKey: actual.CardStateKey(card));
        var record = TerminalRecord.Capture(branch.State, [potionOwner], true) with { ResourcesConsumed = 1 };
        var original = new TournamentResult(bottle, record, [record], 1, 0, "native-audit-fixture") {
            Script = [bottle, strike], ScriptAliveStates = [[potionOwner.NetId], [potionOwner.NetId]],
            ScriptEnemyStates = [[target], [target]],
        };
        var expired = new KernelDecisionRequest(7403, 0);
        nativeCalls = 0; observeNative = true;
        KernelPotionPlanAudit audit;
        try { audit = new KernelPotionPlanAudit(potionRoot, [potionOwner], original, expired); audit.Advance(TimeSpan.FromSeconds(1)); }
        finally { observeNative = false; }
        Check(nativeCalls == 0 && !audit.Removed && !audit.Audited
            && audit.StopReason == "request-deadline:audited=false"
            && ReferenceEquals(audit.Result!.Record, original.Record)
            && ReferenceEquals(audit.Result.Script, original.Script)
            && ReferenceEquals(audit.Result.FirstAction, original.FirstAction),
            // R2 RED: System.InvalidOperationException: Request lifecycle: an expired bottle audit must retain the complete native original and explicitly remain unaudited
            "an expired bottle audit must retain the complete native original and explicitly remain unaudited");
        Console.WriteLine("PASS: request stops withdraw partial replay proposals and retain complete original potion candidates without claiming an audit.");
    }

    private static TournamentOptions Options(KernelDecisionRequest request) => new(TopK: 2,
        Rollout: Cheap(), IncludePotions: false, UsePolicyPortfolio: false, ReserveMs: 0,
        EnableJointSearch: false) { Request = request };
    private static RolloutOptions Cheap() => new(RolloutPolicyKind.Kill) {
        InvestmentLookahead = false, SequenceLookahead = false, SequenceHandLookahead = false };

    private static void ReplayCompletedAtStop()
    {
        var (root, owner, enemy) = Board(30, 1);
        var original = root.PartyStateText();
        var card = root.Hand(owner).Single();
        var action = new KernelTeamSearch.Action(owner, card, enemy, CardStateKey: root.CardStateKey(card));
        var candidate = new TournamentResult(action, TerminalRecord.Capture(root, [owner], false, "fixture-reference"),
            [], 0, 0, "fixture-reference") { Script = [action] };
        var expired = new KernelDecisionRequest(7461, 0);
        nativeCalls = 0; observeNative = true;
        try
        {
            var neverStarted = new KernelScriptReplayRun(root, [owner], candidate, 80, expired);
            for (var i = 0; i < 20; i++) neverStarted.Advance(TimeSpan.Zero);
            Check(neverStarted.Done && neverStarted.Result is null && nativeCalls == 0
                && neverStarted.Failure == "request-deadline" && root.PartyStateText() == original,
                "zero-step replay must stop once at an expired deadline without native work or a Pending loop");
            var request = new KernelDecisionRequest(7462, 10000);
            // A deterministic phase cutoff becomes true inside the paid native
            // wrapper. This is the same post-transaction stop boundary as an
            // elapsed request, without wall-clock sleeps in this regression.
            var completed = new KernelScriptReplayRun(root, [owner], candidate, 80, request) {
                StopRequested = () => nativeCalls > 0 };
            completed.Advance(TimeSpan.Zero);
            var result = completed.Result;
            Check(completed.Done && nativeCalls == 1 && result is { Record.Verified: false, Record.RemainingEnemyHp: 24 }
                && result.Script?.Count == 1 && result.IsFor(request) && root.PartyStateText() == original,
                "a complete nonterminal replay crossing its computation stop must retain its completed same-root candidate");
            for (var i = 0; i < 20; i++) completed.Advance(TimeSpan.FromMilliseconds(1));
            Check(ReferenceEquals(completed.Result, result) && nativeCalls == 1,
                "polling a completed stopped replay must not repeat its native action or republish another result");
        }
        finally { observeNative = false; }
        Console.WriteLine("PASS: replay deadlines stop zero-step work and retain exactly one fully settled nonterminal result.");
    }

    private static void ForegroundStops()
    {
        var (root, owner, _) = Board(30, 2, withPotion: true);
        var original = root.PartyStateText();
        var expired = new KernelDecisionRequest(7501, 0);
        var end = new KernelEndTurnRun(root, owner, 10000, expired);
        Check(end.Done && end.Advance(TimeSpan.FromSeconds(1)) && end.Attempted == 0
            && end.Compared == 0 && end.Actions is null && end.StopReason == "request-deadline",
            // R2 RED: System.InvalidOperationException: Request lifecycle: an expired foreground request must not start an end-turn comparison
            "an expired foreground request must not start an end-turn comparison");
        var shortRun = new KernelShortVictoryRun(root, [owner], expired);
        Check(shortRun.Done && shortRun.Advance(TimeSpan.FromSeconds(1)) && shortRun.Result is null
            && shortRun.StopReason == "request-deadline",
            // R2 RED: System.InvalidOperationException: Request lifecycle: an expired foreground request must not start a short-victory comparison
            "an expired foreground request must not start a short-victory comparison");
        var choice = KernelPotionOpportunity.Choose(root, [owner], [owner], bossOpening: true, request: expired);
        Check(choice is null,
            // R2 RED: System.InvalidOperationException: Request lifecycle: an expired foreground request must not begin even a legal boss-opening potion candidate
            "an expired foreground request must not begin even a legal boss-opening potion candidate");
        var witness = new KernelPotionOpportunity.CurrentTurnWitness(root, [owner], [owner], RolloutPolicyKind.Kill, expired);
        Check(witness.Done && witness.Result is null,
            // R2 RED: System.InvalidOperationException: Request lifecycle: an expired emergency native witness must be done before starting its first action
            "an expired emergency native witness must be done before starting its first action");
        nativeCalls = 0; observeNative = true;
        try { witness.Step(); }
        finally { observeNative = false; }
        Check(nativeCalls == 0 && witness.Result is { Cutoff: true, Record.Verified: false }
            && root.PartyStateText() == original,
            // R2 RED: System.InvalidOperationException: Request lifecycle: stopping the emergency native witness must produce only cutoff evidence and preserve the root
            "stopping the emergency native witness must produce only cutoff evidence and preserve the root");
        var active = new KernelDecisionRequest(7502, 10000);
        var phase = new KernelEndTurnRun(root, owner, 0, active);
        Check(phase.Done && phase.StopReason == "phase-deadline" && !active.IsStopped
            && phase.Attempted == 0 && phase.Actions is null,
            // R2 RED: System.InvalidOperationException: Request lifecycle: an end-turn phase wall cap must stop its own work without cancelling the shared request
            // Physical controls: endturn-phase-wall-cap.
            "an end-turn phase wall cap must stop its own work without cancelling the shared request");
        Console.WriteLine("PASS: foreground end-turn, short-victory, potion choices and emergency witnesses share the request deadline without fabricated complete results.");
    }

    private static (KernelSession Root, Player Owner, Creature Enemy) Board(int enemyHp, int strikes, bool withPotion = false)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 70001);
        Player[] party = [owner];
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "REQUEST-LIFECYCLE"));
        owner.ResetCombatState(); combat.AddPlayer(owner);
        owner.Creature.SetMaxHpInternal(80); owner.Creature.SetCurrentHpInternal(80);
        owner.PlayerCombatState!.Phase = PlayerTurnPhase.Play; owner.PlayerCombatState.GainEnergy(3);
        for (var i = 0; i < strikes; i++) owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        if (withPotion) owner.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), silent: true);
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (KernelSession.Capture(combat), owner, enemy);
    }

    private static void Finish(TournamentRun run)
    {
        for (var i = 0; i < 1000 && !run.Done; i++) run.Advance(TimeSpan.FromMilliseconds(1));
        Check(run.Done, "native lifecycle fixture must reach its own bounded completion");
    }
    private static void ObserveNativePlay()
    {
        if (observeNative || cancelAtPlay is not null || expireAtPlay is not null) Interlocked.Increment(ref nativeCalls);
        if (cancelAtPlay is { } request) request.Cancel("native-hook");
        if (expireAtPlay is { } expiring) Thread.Sleep(expiring.RemainingMs + 5);
        if (blockWorker && Environment.CurrentManagedThreadId != captureThread)
        {
            entered.Set();
            if (!release.Wait(5000)) throw new InvalidOperationException("request native worker fixture was not released");
        }
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Request lifecycle: " + message); }
}
