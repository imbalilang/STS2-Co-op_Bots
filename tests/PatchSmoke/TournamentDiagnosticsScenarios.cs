using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class TournamentDiagnosticsScenarios
{
    // Actual private-source R2, 2026-10-09: one baseline Rebuild0;
    // each named mutant Rebuild0/RED1, exact restored Rebuild0/GREEN0.
    // Physical sources, DLL/PDB, commands and raw logs:
    // work/architecture-refactor-20261009/fixed-budget-performance-v3/state-diagnostics/r2-v6/proof.json
    private static KernelDecisionRequest? expireAtPlay;
    private static int expiredPlays;

    internal static void Run(string? only = null)
    {
        TestEnvironment.Ensure();
        var hooks = new Harmony("coopbots.tournament-diagnostics-headless");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        hooks.Patch(format, prefix: new HarmonyMethod(typeof(TournamentDiagnosticsScenarios), nameof(Format)));
        try
        {
            if (only is null or "declined") Declined();
            if (only is null or "native-steps") NativeSteps();
            if (only is null or "native-deadline-tail") NativeDeadlineTail();
            if (only is null or "settled-paused-prefix") SettledPausedPrefix();
            if (only is null or "prefix-incumbent") PrefixCannotDisplaceIncumbent();
            if (only is null or "queued-terminal-prefix") QueuedTerminalPrefix();
            if (only is null or "prefix-cancel") CancelledPausedPrefix();
            if (only is null or "prefix-pending-choice") PendingChoicePrefix();
        }
        finally { hooks.Unpatch(format, HarmonyPatchType.All, hooks.Id); }
    }

    private static void Declined()
    {
        var (root, owner) = Board();
        var text = root.PartyStateText();
        var request = new KernelDecisionRequest(390301, 0);
        using var run = new TournamentRun(root, [owner], [owner],
            new TournamentOptions { Request = request }, 1000);
        var result = run.Result!;
        Check(run.Done && result.FirstAction is null && result.Record is null
            && result.StopReason == "request-deadline"
            && result.JointDiagnostics.Contains("baseline=not-run")
            && result.JointDiagnostics.Contains("policyReferences=[]")
            && result.JointDiagnostics.Contains("pausedReferences=0")
            && result.JointDiagnostics.Contains("rolloutSteps=0")
            && root.PartyStateText() == text,
            // RED: System.InvalidOperationException: Tournament diagnostics: declined search must retain baseline/policy/step diagnostics without inventing an action
            // Control: tournament-declined-diagnostics.
            "declined search must retain baseline/policy/step diagnostics without inventing an action");
        Console.WriteLine("PASS: declined tournament preserves its scheduling diagnostics and unchanged native root.");
    }

    private static void NativeSteps()
    {
        var (root, owner) = Board();
        var before = root.PartyStateText();
        using var run = new TournamentRun(root, [owner], [owner],
            new TournamentOptions(UsePolicyPortfolio: false, ReserveMs: 0,
                Rollout: new(RolloutPolicyKind.Kill, MaxSteps: 12, MaxRounds: 2),
                EnableJointSearch: true), 3000);
        while (!run.Advance(TimeSpan.MaxValue)) Thread.Sleep(1);
        Check(run.Result!.FirstAction is not null && run.Result.Record is { Verified: true, Victory: true }
            && run.RolloutStepCount > 0 && run.RolloutStepMaxMs > 0
            && run.Result.JointDiagnostics.Contains("rolloutSteps=")
            && run.Result.JointDiagnostics.Contains("rolloutStepMaxMs=")
            && run.Result.JointDiagnostics.Contains("baseline=victory")
            && !run.Result.JointDiagnostics.Contains("baselineSteps=unavailable")
            && run.RolloutStepsCrossingDeadline == 0 && root.PartyStateText() == before,
            // RED: System.InvalidOperationException: Tournament diagnostics: actual native rollout steps must populate elapsed telemetry without changing terminal evidence or frozen root
            // Control: tournament-native-step-count.
            "actual native rollout steps must populate elapsed telemetry without changing terminal evidence or frozen root");
        Console.WriteLine("PASS: completed native rollout populates step telemetry while preserving terminal and root evidence.");
    }

    private static void NativeDeadlineTail()
    {
        var (root, owner) = Board(6);
        var before = root.PartyStateText();
        var hooks = new Harmony("coopbots.tournament-tail-tests");
        var play = AccessTools.Method(typeof(CombatPredictionSimulator), "OnPlayWrapper");
        hooks.Patch(play, prefix: new HarmonyMethod(typeof(TournamentDiagnosticsScenarios), nameof(ExpireNativePlay)));
        var request = new KernelDecisionRequest(390302, 1000);
        expiredPlays = 0; expireAtPlay = request;
        try
        {
            using var run = new TournamentRun(root, [owner], [owner],
                new TournamentOptions(UsePolicyPortfolio: false, ReserveMs: 0,
                    Rollout: new(RolloutPolicyKind.Kill, MaxSteps: 12, MaxRounds: 2),
                    EnableJointSearch: true) { Request = request }, 1000);
            while (!run.Advance(TimeSpan.MaxValue)) Thread.Sleep(1);
            Check(expiredPlays == 1 && request.DeadlineReached
                && run.Result!.Record is { Verified: true, Victory: true }
                && run.Result.StopReason == "request-deadline"
                && run.RolloutStepsCrossingDeadline == 1 && run.RolloutStepDeadlineTailMs > 0
                && run.Result.JointDiagnostics.Contains("rolloutStepDeadlineCrossings=1")
                && root.PartyStateText() == before,
                // RED: System.InvalidOperationException: Tournament diagnostics: a real rollout step crossing the request deadline must finish its native victory and publish positive tail evidence
                // Control: tournament-native-deadline-crossing.
                "a real rollout step crossing the request deadline must finish its native victory and publish positive tail evidence");
            Console.WriteLine("PASS: a native rollout crossing the deadline settles its victory and records a positive elapsed tail.");
        }
        finally
        {
            expireAtPlay = null;
            hooks.Unpatch(play, HarmonyPatchType.All, hooks.Id);
        }
    }

    private static void ExpireNativePlay()
    {
        if (expireAtPlay is not { } request) return;
        expiredPlays++;
        Thread.Sleep(request.RemainingMs + 25);
    }

    // Actual private R2, 2026-10-11: eight mutant Rebuild0/RED1;
    // exact source2 + DLL/PDB6 restore, then native GREEN0 for each.
    // Raw commands, finite mutations and all byte identities:
    // work/v12-budget-reference-20261011-v1/r2/proof.json
    // The reflection controls change only scheduler shares or construct the
    // explicitly labeled pending-choice negative; paid card replay stays native.
    // 01-lost-prefix RED: System.InvalidOperationException: Tournament diagnostics: paused policies must retain only aligned settled prefixes as cutoff deployment without promoting completed evidence
    // 02-policy-checkpoint-wiring RED: System.InvalidOperationException: Tournament diagnostics: paused policies must retain only aligned settled prefixes as cutoff deployment without promoting completed evidence
    // 03-terminal-queue-closure RED: System.InvalidOperationException: Tournament diagnostics: closing the paused queue must retain its already-settled native victory instead of relabeling it cutoff
    // 04-incumbent-priority RED: System.InvalidOperationException: Tournament diagnostics: a paused cutoff must never replace a real accepted terminal/reference or alter final candidate ranking
    // 05-cancel-withdrawal RED: System.InvalidOperationException: Tournament diagnostics: cancellation must withdraw paused-prefix deployment without starting another native action
    // 06-pending-can-fork RED: System.InvalidOperationException: Tournament diagnostics: an unresolved choice transaction must never be published as a settled policy prefix or terminal
    // 07-middle-checkpoint RED: System.InvalidOperationException: Tournament diagnostics: each retained checkpoint/alive/enemy row must match its actual native pre-action boundary
    // 08-active-stop-settlement RED: System.InvalidOperationException: Tournament diagnostics: active StopRequested must retain settled Defend/Growth cutoff evidence without starting another action or inventing completion
    private static Queue<(RolloutPolicyKind Policy, RolloutRun Run)> Paused(TournamentRun run)
        => (Queue<(RolloutPolicyKind, RolloutRun)>)AccessTools.Field(typeof(TournamentRun), "pausedPolicyReferences").GetValue(run)!;
    private static void SetSchedule(TournamentRun run, string name, object value)
        => AccessTools.Field(typeof(TournamentRun), name).SetValue(run, value);
    private static void CloseSchedulingShare(TournamentRun run)
    {
        var options = (TournamentOptions)AccessTools.Field(typeof(TournamentRun), "o").GetValue(run)!;
        var budget = (int)AccessTools.Field(typeof(TournamentRun), "budgetMs").GetValue(run)!;
        // Reserve the remaining local phase share, without changing its clock
        // or the shared request deadline. The in-progress native step settles.
        SetSchedule(run, "o", options with { ReserveMs = budget });
    }
    private static void AdvancePaused(TournamentRun run)
        => AccessTools.Method(typeof(TournamentRun), "AdvancePausedPolicyReferences").Invoke(run, [TimeSpan.Zero]);

    private static (TournamentRun Run, KernelDecisionRequest Request) OpenPaused(KernelSession root,
        Player owner, RolloutPolicyKind kind, long generation)
    {
        var request = new KernelDecisionRequest(generation, 100000);
        var run = new TournamentRun(root, [owner], [owner],
            new TournamentOptions(ReserveMs: 0, IncludePotions: false, EnableJointSearch: true)
            { Request = request }, 100000);
        var method = kind == RolloutPolicyKind.Kill ? "AdvanceReference" : "AdvancePolicyReferences";
        if (kind != RolloutPolicyKind.Kill)
        {
            SetSchedule(run, "referenceFinished", true);
            SetSchedule(run, "policyReferenceIndex", kind == RolloutPolicyKind.Defend ? 0 : 1);
            SetSchedule(run, "policyReferenceDeadlineMs", 200000d);
        }
        AccessTools.Method(typeof(TournamentRun), method).Invoke(run, [TimeSpan.Zero]);
        SetSchedule(run, kind == RolloutPolicyKind.Kill ? "referenceStartedMs" : "policyReferenceStartedMs", -200000d);
        AccessTools.Method(typeof(TournamentRun), method).Invoke(run, [TimeSpan.Zero]);
        Check(Paused(run).Count == 1 && Paused(run).Peek().Policy == kind
            && Paused(run).Peek().Run.CommittedSteps == 2 && root.Energy(owner) == 3,
            "production Kill/Defend/Growth reference factories must pause their own two paid native actions without mutating the root");
        // Close the remaining share through the existing reserve check. The
        // actual phase/request clocks are never shortened or restarted.
        CloseSchedulingShare(run);
        return (run, request);
    }

    private static void SettledPausedPrefix()
    {
        foreach (var kind in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth })
        {
            var (root, owner) = Board(200, 3);
            var before = root.PlanningState([owner.NetId]);
            var (run, request) = OpenPaused(root, owner, kind, 390310 + (int)kind);
            using (run)
            {
                AdvancePaused(run);
                var result = run.Result!;
                Console.WriteLine($"DIAG prefix policy={kind} action={result?.FirstAction?.Card?.Id.Entry} verified={result?.Record?.Verified} cutoff={result?.Record?.CutoffReason} steps={result?.Script?.Count} completed={run.BestCompleted is not null} rootExact={Exact(root.PlanningState([owner.NetId]), before)}");
                Check(result is { FirstAction.Card: not null, Record: { Verified: false, Victory: false, CutoffReason: "continuation-budget" }, Script.Count: 3 }
                    && result.IsFor(request) && result.ScriptAliveStates?.Count == 3
                    && result.ScriptEnemyStates?.Count == 3 && result.ScriptCheckpoints?.Count == 3
                    && result.ScriptCheckpoints[0].SameBoundary(root.ActionCheckpoint())
                    && run.BestCompleted is null && Exact(root.PlanningState([owner.NetId]), before),
                    "paused policies must retain only aligned settled prefixes as cutoff deployment without promoting completed evidence");
                NativeReplay(root, owner, result!);
            }
        }
        SettledActivePrefix();
        StoppedActivePrefix();
        Console.WriteLine("PASS: production Kill/Defend/Growth active and paused references retain paid native prefixes, exact root metadata and native replay parity under their cutoff contracts.");
    }

    private static void SettledActivePrefix()
    {
        foreach (var kind in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth })
        {
            var (root, owner) = Board(200, 3);
            var before = root.PlanningState([owner.NetId]);
            var request = new KernelDecisionRequest(390315 + (int)kind, 100000);
            using var run = new TournamentRun(root, [owner], [owner],
                new TournamentOptions(ReserveMs: 0, IncludePotions: false, EnableJointSearch: true) { Request = request }, 100000);
            var method = kind == RolloutPolicyKind.Kill ? "AdvanceReference" : "AdvancePolicyReferences";
            if (kind != RolloutPolicyKind.Kill)
            {
                SetSchedule(run, "referenceFinished", true);
                SetSchedule(run, "policyReferenceIndex", kind == RolloutPolicyKind.Defend ? 0 : 1);
                SetSchedule(run, "policyReferenceDeadlineMs", 200000d);
            }
            for (var i = 0; i < 2; i++)
                AccessTools.Method(typeof(TournamentRun), method).Invoke(run, [TimeSpan.Zero]);
            Check(Paused(run).Count == 0, "active-prefix control must keep its own policy continuation active instead of entering the paused queue");
            CloseSchedulingShare(run);
            AccessTools.Method(typeof(TournamentRun), method).Invoke(run, [TimeSpan.Zero]);
            AccessTools.Method(typeof(TournamentRun), "Finish").Invoke(run, []);
            Check(run.Result is { FirstAction.Card: not null, Record: { Verified: false, Victory: false, CutoffReason: "continuation-budget" }, Script.Count: 3 }
                && run.Result.IsFor(request) && Exact(root.PlanningState([owner.NetId]), before)
                && (kind == RolloutPolicyKind.Kill
                    ? run.BestCompleted is { Record.Verified: false }
                    : run.BestCompleted is null),
                "active bounded policies must retain native cutoff prefixes while Kill preserves its existing nonpaused contract and no new complete result is invented");
            NativeReplay(root, owner, run.Result!);
        }
    }

    private static void StoppedActivePrefix()
    {
        foreach (var kind in new[] { RolloutPolicyKind.Defend, RolloutPolicyKind.Growth })
        {
            var (root, owner) = Board(200, 3);
            var before = root.PlanningState([owner.NetId]);
            var request = new KernelDecisionRequest(390330 + (int)kind, 100000);
            using var run = new TournamentRun(root, [owner], [owner],
                new TournamentOptions(ReserveMs: 0, IncludePotions: false, EnableJointSearch: true) { Request = request }, 100000);
            SetSchedule(run, "referenceFinished", true);
            SetSchedule(run, "policyReferenceIndex", kind == RolloutPolicyKind.Defend ? 0 : 1);
            SetSchedule(run, "policyReferenceDeadlineMs", 200000d);
            for (var i = 0; i < 2; i++)
                AccessTools.Method(typeof(TournamentRun), "AdvancePolicyReferences").Invoke(run, [TimeSpan.Zero]);
            Check(Paused(run).Count == 0, "stop control must have an active policy with two committed native actions");
            // Expire the local phase after the two real paid actions, then enter
            // the public StopRequested settlement path without starting a third.
            SetSchedule(run, "budgetMs", 0);
            var settled = run.Advance(TimeSpan.Zero);
            Console.WriteLine($"DIAG active-stop policy={kind} done={settled} action={run.Result?.FirstAction?.Card?.Id.Entry} cutoff={run.Result?.Record?.CutoffReason} steps={run.Result?.Script?.Count} stop={run.Result?.StopReason}");
            Check(settled
                && run.Result is { FirstAction.Card: not null, Record: { Verified: false, Victory: false, CutoffReason: "continuation-budget" }, Script.Count: 2 }
                && run.Result.IsFor(request) && run.Result.StopReason == "phase-deadline"
                && run.BestCompleted is null && Exact(root.PlanningState([owner.NetId]), before),
                "active StopRequested must retain settled Defend/Growth cutoff evidence without starting another action or inventing completion");
            NativeReplay(root, owner, run.Result!);
        }
    }

    private static void NativeReplay(KernelSession root, Player owner, TournamentResult result)
    {
        var combat = (CombatState)owner.Creature.CombatState!;
        using var native = (IDisposable)Activator.CreateInstance(
            AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [combat], null)!;
        var predicted = root.Fork();
        var script = result.Script!;
        for (var i = 0; i < script.Count; i++)
        {
            var actualBefore = KernelSession.Capture(combat);
            Check(result.ScriptCheckpoints![i].SameBoundary(actualBefore.ActionCheckpoint())
                && result.ScriptAliveStates![i].SequenceEqual(actualBefore.Party.Where(p => actualBefore.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(id => id))
                && result.ScriptEnemyStates![i].SequenceEqual(actualBefore.Enemies),
                "each retained checkpoint/alive/enemy row must match its actual native pre-action boundary");
            var action = script[i];
            Check(!action.EndTurn && action.Potion is null && predicted.ReplayPlanned(action, 80, out var reason),
                "settled-prefix native control must replay its own real card tokens");
            var card = KernelSession.ResolvePlannedCard(owner, action.Card, action.CardStateKey, action.CardStateOccurrence, out _)!;
            var paid = card.SpendResources().GetAwaiter().GetResult();
            Check(paid.Item1 == 1, "settled-prefix native control must pay the actual Strike cost");
            card.OnPlayWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), action.Target, false,
                new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1, StarsSpent = paid.Item2, StarValue = paid.Item2 },
                skipCardPileVisuals: true).GetAwaiter().GetResult();
            var fresh = KernelSession.Capture(combat);
            var expected = predicted.PlanningState([owner.NetId]);
            var actual = fresh.PlanningState([owner.NetId]);
            Check(Exact(expected, actual)
                && predicted.Energy(owner) == owner.PlayerCombatState!.Energy,
                "a retained cutoff action must match fresh paid native card state, resources, RNG and physical identity before the next action");
        }
    }

    private static bool Exact(KernelPlanningState expected, KernelPlanningState actual)
    {
        var field = AccessTools.Field(typeof(KernelPlanningState), "physicalCards");
        var a = (CardModel[])field.GetValue(expected)!;
        var b = (CardModel[])field.GetValue(actual)!;
        return expected.Semantics == actual.Semantics && a.Length == b.Length
            && a.Zip(b).All(pair => ReferenceEquals(pair.First, pair.Second));
    }

    private static void QueuedTerminalPrefix()
    {
        var (root, owner) = Board(12, 3);
        var before = root.PlanningState([owner.NetId]);
        var (run, request) = OpenPaused(root, owner, RolloutPolicyKind.Kill, 390319);
        using (run)
        {
            var nativeVictory = Paused(run).Dequeue();
            Check(nativeVictory.Run.State.HasWon && !nativeVictory.Run.Done,
                "queued terminal control must have actually killed the enemy before observing its ending");
            var first = new RolloutRun(root.Fork(), [owner], [owner],
                new(RolloutPolicyKind.Defend) { InvestmentLookahead = false, SequenceHandLookahead = false, CaptureActionCheckpoints = true });
            first.Step(); first.FinishAtBudget();
            Paused(run).Enqueue((RolloutPolicyKind.Defend, first));
            Paused(run).Enqueue(nativeVictory);
            AdvancePaused(run);
            Check(run.Result is { Record: { Verified: true, Victory: true }, Script.Count: 2 }
                && run.Result.IsFor(request) && run.Rollouts == 1
                && run.Result.Considered.Count == 1 && run.BestCompleted is { Record.Verified: true }
                && Exact(root.PlanningState([owner.NetId]), before),
                "closing the paused queue must retain its already-settled native victory instead of relabeling it cutoff");
        }
        Console.WriteLine("PASS: phase closure preserves a true terminal in a later paused queue entry and consumes it once.");
    }

    private static void PrefixCannotDisplaceIncumbent()
    {
        var (root, owner) = Board(12, 3);
        var request = new KernelDecisionRequest(390320, 100000);
        var complete = new RolloutRun(root.Fork(), [owner], [owner],
            new(RolloutPolicyKind.Kill) { InvestmentLookahead = false, SequenceHandLookahead = false, CaptureActionCheckpoints = true });
        while (!complete.Done) complete.Step();
        var ending = complete.Result!;
        Check(ending is { Terminal: true, Victory: true }, "prefix incumbent must come from a real native terminal rollout");
        var reference = new TournamentResult(ending.Actions[0], ending.Record, [ending.Record], 1, 0, "native-reference")
        { RootGeneration = request.RootGeneration, RequestProvenance = request, Script = ending.Actions,
            ScriptAliveStates = ending.AliveStates, ScriptEnemyStates = ending.EnemyStates, ScriptCheckpoints = ending.Checkpoints };
        using var run = new TournamentRun(root, [owner], [owner],
            new TournamentOptions(ReserveMs: 0, EnableJointSearch: true) { Request = request, ReferenceResult = reference }, 100000);
        var partial = new RolloutRun(root.Fork(), [owner], [owner],
            new(RolloutPolicyKind.Kill) { InvestmentLookahead = false, SequenceHandLookahead = false, CaptureActionCheckpoints = true });
        partial.Step(); partial.FinishAtBudget();
        Paused(run).Enqueue((RolloutPolicyKind.Kill, partial));
        CloseSchedulingShare(run); AdvancePaused(run);
        Check(run.Result!.Record is { Verified: true, Victory: true }
            && ReferenceEquals(run.Result.Record, ending.Record) && run.Result.FirstAction == reference.FirstAction
            && ReferenceEquals(run.BestCompleted!.Record, ending.Record),
            "a paused cutoff must never replace a real accepted terminal/reference or alter final candidate ranking");
        Console.WriteLine("PASS: a native completed incumbent survives paused-prefix recovery unchanged.");
    }

    private static void CancelledPausedPrefix()
    {
        var (root, owner) = Board(200, 3);
        var (run, request) = OpenPaused(root, owner, RolloutPolicyKind.Kill, 390321);
        using (run)
        {
            request.Cancel("paused-prefix-native-control");
            Check(run.Advance(TimeSpan.Zero) && run.Result is { FirstAction: null, Record: null, Script: null }
                && run.BestCompleted is null && root.Energy(owner) == 3,
                "cancellation must withdraw paused-prefix deployment without starting another native action");
        }
        Console.WriteLine("PASS: cancellation withdraws settled-prefix deployment without weakening its shared request.");
    }

    private static void PendingChoicePrefix()
    {
        var (root, owner) = Board(200, 3);
        var before = root.PlanningState([owner.NetId]);
        var (run, _) = OpenPaused(root, owner, RolloutPolicyKind.Kill, 390322);
        using (run)
        {
            var pending = Paused(run).Peek().Run;
            // Construct a genuine simulation pending-choice frame after two
            // committed native card bodies. This is a guard negative control,
            // not a claim of native EndTurn parity or a real Tools transaction.
            var combat = (SimulatedCombatState)pending.State.Simulator.State.CombatState;
            var options = pending.State.Simulator.State.GetPlayerCombatState(owner).Hand.Cards.ToArray();
            var spec = new CardChoiceSpec(PlanChoiceEffect.Discard, PileType.Hand, 1, 1, options, options, 0, "constructed-tools-tail");
            combat.BeginActionChoices(new TurnStartChoiceCursor([]));
            combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            combat.SetPendingTurnStartChoice(new("ToolsOfTheTradePower", PlanChoiceEffect.Discard, PileType.Hand, 1,
                spec, "constructed-tools-tail", PlanChoiceTiming.PlayerTurnStart));
            object?[] boundary = ["", "constructed-turn-start-tail"];
            AccessTools.Method(typeof(KernelSession), "Pending").Invoke(pending.State, boundary);
            Check(combat.PendingTurnStartChoice?.Spec == spec && !pending.State.CanFork,
                "constructed pending-choice control must enter the actual nonforkable kernel boundary");
            pending.FinishAtBudget();
            Check(pending.Result is { Terminal: false, Victory: false, StopReason: "continuation-budget", Actions.Count: 2 }
                && pending.Result.AliveStates.Count == 2 && pending.Result.EnemyStates.Count == 2
                && pending.Result.Checkpoints?.Count == 2
                && pending.Result.Checkpoints[0].SameBoundary(root.ActionCheckpoint()),
                "pending-choice guard control must otherwise be an aligned continuation-budget prefix from its own root");
            AdvancePaused(run);
            Check(run.Result is { FirstAction: null, Record: null } && run.BestCompleted is null
                && Exact(root.PlanningState([owner.NetId]), before),
                "an unresolved choice transaction must never be published as a settled policy prefix or terminal");
            Console.WriteLine("DIAG constructed-pending-prefix boundary=" + boundary[0]);
        }
        Console.WriteLine("PASS: a constructed pending choice frame cannot enter settled-prefix deployment; native EndTurn parity is not asserted.");
    }

    private static (KernelSession Root, Player Owner) Board(int enemyHp = 12, int cards = 2)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 390301);
        Player[] party = [owner];
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "TOURNAMENT-DIAGNOSTICS"));
        owner.ResetCombatState(); combat.AddPlayer(owner);
        owner.Creature.SetMaxHpInternal(80); owner.Creature.SetCurrentHpInternal(80);
        owner.PlayerCombatState!.Phase = PlayerTurnPhase.Play; owner.PlayerCombatState.GainEnergy(3);
        for (var i = 0; i < cards; i++) owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (KernelSession.Capture(combat), owner);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Tournament diagnostics: " + message);
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
}
