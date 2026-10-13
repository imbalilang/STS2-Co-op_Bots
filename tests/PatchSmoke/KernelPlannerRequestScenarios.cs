// R2 physical refinement proof: work/architecture-refactor-20261009/r2/root-refinement-proof7-v3/proof.json
// refinement-human-start: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.InvalidOperationException: Planner request: a living human seat must prevent speculative next-turn refinement
// refinement-request-wiring: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.InvalidOperationException: Planner request: handed-over all-Bot table must receive its own bounded refinement request
// refinement-foreground-priority: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.InvalidOperationException: Planner request: an active foreground request must prevent extra speculative native work
// refinement-idle-progress: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.InvalidOperationException: Planner request: stopped foreground request must allow the independently funded refinement to progress
// refinement-human-handover: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.InvalidOperationException: Planner request: returning a living seat to a human must cancel the old speculative control domain
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Production planner entry and native simulated card replay. Only the order
// test intercepts potion submission, so it observes planner scheduling without
// executing a live potion. This is not a full live-game outcome oracle.
// R2 physical RED text and receipts are appended after the mutation runs.
// R2 actual 2026-10-09: independent private forced Rebuild 0, named runtime RED 1,
// exact restored source and six DLL/PDB artifacts; restored run 0.
// Receipt: work/architecture-refactor-20261009/r2/root-planning-combined-physical-audit.v3.json
// Scope: supported state/control/input contracts, not full gates or live win rate.
// planner-settled-request-key: System.InvalidOperationException: Planner request: settled human input changes the request key even with the same action version
// mixed-capture-continuation: System.InvalidOperationException: Planner request: planner must reuse the matched committed branch without dropping its script
// mixed-proposal-demotion: System.InvalidOperationException: Planner request: old victory evidence must be demoted to an untrusted suffix proposal
// mixed-miss-budget-retained: System.InvalidOperationException: Planner request: a cache miss must discard the old script while preserving the current absolute deadline
// mixed-replay-result-installed: System.InvalidOperationException: Planner request: only a complete native replay from the changed actual root can renew proposal provenance
// mixed-review-before-potion-forecast: System.InvalidOperationException: Planner request: new decision must review settled mixed input before using a cached potion forecast
// mixed-to-allbot-control-handover: System.InvalidOperationException: Planner request: handing the last human seat to a Bot must renew the old mixed plan's control domain
internal static class KernelPlannerRequestScenarios
{
    private sealed class SettledEnd(Player owner) : EndPlayerTurnAction(owner, 1)
    { protected override Task ExecuteAction() => Task.CompletedTask; }
    private static bool potionSawRenewedRoot;
    private static string replayObservation = "not-observed";

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.planner-request-presentation");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        presentation.Patch(format, prefix: new HarmonyMethod(typeof(KernelPlannerRequestScenarios), nameof(Format)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("settled-key", SettledKey), ("reuse", Reuse), ("reroot-replay", RerootReplay),
                ("handover", Handover), ("order", Order), ("refinement", Refinement),
#if KERNEL_TESTS
                ("high-dop", HighDopMappingScenarios.Run),
#endif
                ("parallelism-telemetry", ParallelismTelemetry) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: planner request " + test.Name); }
        }
        finally { presentation.Unpatch(format, HarmonyPatchType.All, presentation.Id); }
    }

    private static void SettledKey()
    {
        var (battle, owner, _) = KernelPlanningStateScenarios.Board();
        var queue = new ActionQueueSet([owner]); queue.CombatStarted();
        var driven = AutoPilot.IsAutopiloted(owner.NetId); AutoPilot.Set(owner.NetId, false);
        try
        {
            KernelInputObserver.Observe(queue, battle);
            var before = KernelCombatPlanner.TournamentDecisionPoint(battle, 7);
            var action = new SettledEnd(owner); queue.EnqueueWithoutSynchronizing(action);
            Check(before == KernelCombatPlanner.TournamentDecisionPoint(battle, 7),
                "enqueued input keeps the settled decision point");
            action.Execute().GetAwaiter().GetResult();
            Check(before != KernelCombatPlanner.TournamentDecisionPoint(battle, 7),
                "settled human input changes the request key even with the same action version");
        }
        finally { KernelInputObserver.Observe(queue, null); AutoPilot.Set(owner.NetId, driven); }
    }

    // R2 actual 2026-10-09: production-parallelism-observability removed all four
    // diagnostic reads: baseline Rebuild0/run0 -> mutant Rebuild0/named RED1
    // -> exact source and six DLL/PDB restored Rebuild0/named GREEN0.
    // Actual RED: System.InvalidOperationException: Planner request: published tournament diagnostics must report actual runtime CPU, request cap, configured options DOP and effective run DOP on both production phases
    // Evidence: work/architecture-refactor-20261009/fixed-budget-performance-v3/parallelism-telemetry/execution-20261009T174602612143Z/evidence/proof.json
    // Runtime scheduling/observability witness. Both runs and their shared
    // request/options are created by the production planner entry; cancellation
    // publishes its actual result without submitting pooled rollout work.
    // The focused CPU belongs to this .NET process, never a guessed worker peak.
    private static void ParallelismTelemetry()
    {
        var previousPerf = KernelCombatPlanner.Perf;
        KernelCombatPlanner.Perf = KernelCombatPlanner.TournamentPerf.High;
        var players = Enumerable.Range(0, 4).Select(i =>
            Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(85001 + i))).ToArray();
        var previousControl = players.Select(p => AutoPilot.IsAutopiloted(p.NetId)).ToArray();
        var battle = new CombatState(runState: RunState.CreateForTest(players, seed: "DOP-TELEMETRY")) { RoundNumber = 1 };
        foreach (var player in players)
        {
            player.ResetCombatState(); battle.AddPlayer(player); player.MaxEnergy = 3;
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3);
            AutoPilot.Set(player.NetId, true);
        }
        var enemy = battle.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        battle.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        var planner = new KernelCombatPlanner();
        var livePlanner = new KernelCombatPlanner();
        var probe = new Harmony("coopbots.parallelism-telemetry");
        var review = AccessTools.Method(typeof(KernelCombatPlanner), "ReviewMixedInput");
        try
        {
            var root = KernelCombatCapture.Capture(battle)
                ?? throw new InvalidOperationException("Parallelism telemetry fixture cannot capture its native root");
            var original = root.PartyStateText();
            if (!(bool)Invoke(planner, "BeginTournamentPhase", battle, players, true)!)
                throw new InvalidOperationException("Parallelism telemetry fixture cannot enter the production phase");
            var request = (KernelDecisionRequest)Get(planner, "tournamentRequest")!;
            var baseOptions = (TournamentOptions)Get(planner, "tournamentOptions")!;
            var firstRun = (TournamentRun)Get(planner, "tournamentRun")!;
            var first = PublishedTelemetry(firstRun);
            firstRun.Dispose(); Set(planner, "tournamentRun", null!);
            var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
            phase.SetValue(planner, Enum.Parse(phase.FieldType, "Potions"));
            if (!(bool)Invoke(planner, "BeginTournamentPhase", battle, players, true)!)
                throw new InvalidOperationException("Parallelism telemetry fixture cannot enter the production potion phase");
            var potionRun = (TournamentRun)Get(planner, "tournamentRun")!;
            var potion = PublishedTelemetry(potionRun);
            // The second production request entry precedes ReviewMixedInput.
            // Observe that real constructor, then stop before any live search.
            probe.Patch(review, prefix: new HarmonyMethod(typeof(KernelPlannerRequestScenarios), nameof(TelemetryStopAfterRequest)));
            var outcome = Invoke(livePlanner, "TryTournamentDecision", battle, players, true,
                KernelCombatPlanner.TournamentDecisionPoint(battle, 1), null);
            probe.Unpatch(review, HarmonyPatchType.All, probe.Id);
            var liveRequest = (KernelDecisionRequest?)Get(livePlanner, "tournamentRequest");
            var actual = KernelCombatCapture.Capture(battle);
            Check(TelemetryMatches(firstRun, first, request, false)
                && TelemetryMatches(potionRun, potion, request, true)
                && request.BudgetMs == 6400 && request.ActiveJobs == 0 && request.QueuedJobs == 0 && request.CompletedJobs == 0
                && ReferenceEquals(baseOptions.Request, request)
                && ReferenceEquals(Get(planner, "tournamentRequest"), request)
                && liveRequest is not null && liveRequest.BudgetMs == 6400
                && liveRequest.MaxConcurrency == request.MaxConcurrency && outcome?.ToString() == "Pending"
                && ((IReadOnlyList<Player>)Get(planner, "tournamentParty")!).Count == 4
                && ((IReadOnlyList<Player>)Get(planner, "tournamentActors")!).Count == 4
                && actual is not null && actual.PartyStateText() == original,
                "published tournament diagnostics must report actual runtime CPU, request cap, configured options DOP and effective run DOP on both production phases");
            Console.WriteLine("DIAG parallelism-telemetry noPotions=" + first + " potions=" + potion);
            Console.WriteLine("PASS: production tournament results expose actual CPU/request/options/effective DOP on both phases with the unchanged normal 6400ms request.");
        }
        finally
        {
            probe.Unpatch(review, HarmonyPatchType.All, probe.Id);
            planner.Reset(newCombat: true); livePlanner.Reset(newCombat: true);
            for (var i = 0; i < players.Length; i++) AutoPilot.Set(players[i].NetId, previousControl[i]);
            KernelCombatPlanner.Perf = previousPerf;
        }
    }

    private static string PublishedTelemetry(TournamentRun run)
    {
        if (!run.Done)
        {
            run.Cancel("parallelism-telemetry-focus");
            if (!run.Advance(TimeSpan.Zero))
                throw new InvalidOperationException("Parallelism telemetry fixture unexpectedly submitted unfinished work");
        }
        return run.Result?.JointDiagnostics
            ?? throw new InvalidOperationException("Parallelism telemetry fixture has no production-published result");
    }

    private static bool TelemetryMatches(TournamentRun run, string text, KernelDecisionRequest request, bool potions)
    {
        var options = (TournamentOptions)AccessTools.Field(typeof(TournamentRun), "o").GetValue(run)!;
        var effective = (int)AccessTools.Field(typeof(TournamentRun), "dop").GetValue(run)!;
        return TelemetryInt(text, "cpu") == Environment.ProcessorCount
            && TelemetryInt(text, "configuredDop") == options.MaxDegreeOfParallelism
            && TelemetryInt(text, "effectiveDop") == effective
            && TelemetryInt(text, "requestCap") == request.MaxConcurrency
            && effective == Math.Max(1, Math.Min(options.MaxDegreeOfParallelism, request.MaxConcurrency))
            && options.MaxDegreeOfParallelism == request.MaxConcurrency
            && ReferenceEquals(options.Request, request)
            && options.IncludePotions == potions && options.PotionsOnly == potions;
    }

    private static int TelemetryInt(string text, string key)
    {
        var values = System.Text.RegularExpressions.Regex.Matches(text,
            @"(?<!\w)" + System.Text.RegularExpressions.Regex.Escape(key) + @"=(-?\d+)(?!\w)");
        return values.Count == 1 && int.TryParse(values[0].Groups[1].Value, out var value) ? value : int.MinValue;
    }

    private static bool TelemetryStopAfterRequest(ref bool __result) { __result = true; return false; }

    private static void Reuse()
    {
        WithPlan((planner, battle, _, _, root) => {
            var request = new KernelDecisionRequest(921, 10000);
            Set(planner, "tournamentRequest", request);
            Check(!(bool)Invoke(planner, "ReviewMixedInput", battle)!, "an equal actual root needs no prefix wait");
            var restored = (KernelSession?)Get(planner, "rootSession");
            Check(Get(planner, "tournamentScript") is not null && restored is not null
                && restored.PlanningState([1]).Matches(root.PlanningState([1])),
                "planner must reuse the matched committed branch without dropping its script");
            Check(ReferenceEquals(Get(planner, "tournamentRequest"), request) && !request.IsCancelled,
                "a successful reuse cannot replace or cancel the current deadline");
        });
    }

    private static void RerootReplay()
    {
        WithPlan((planner, battle, owner, enemy, root) => {
            // This case tests completed replay evidence. Warm the native
            // two-seat card path on an isolated branch before opening its
            // request; a cold atomic step may legitimately consume the 120ms
            // proposal phase and leave no complete result to adopt.
            var oldScript = (IReadOnlyList<KernelTeamSearch.Action>)Get(planner, "tournamentScript")!;
            var warm = new KernelScriptReplayRun(root, battle.Players.ToArray(),
                new(oldScript[0], (TerminalRecord)Get(planner, "tournamentScriptRecord")!, [], 0, 0, "fixture-warmup")
                { Script = oldScript }, 80);
            for (var i = 0; !warm.Done && i < 10; i++) warm.Advance(TimeSpan.FromMilliseconds(6));
            var request = new KernelDecisionRequest(922, 10000);
            Set(planner, "tournamentRequest", request);
            enemy.SetCurrentHpInternal(12);
            Check(!(bool)Invoke(planner, "ReviewMixedInput", battle)!, "changed actual root completes a zero-prefix review");
            Check(Get(planner, "tournamentScript") is null
                && ReferenceEquals(Get(planner, "tournamentRequest"), request) && !request.IsCancelled,
                "a cache miss must discard the old script while preserving the current absolute deadline");
            var proposal = (TournamentResult?)Get(planner, "mixedProposal");
            Check(proposal is { Record.Victory: false, Record.Verified: false }
                && !proposal.IsFor(request) && proposal.Script is { Count: 2 },
                "old victory evidence must be demoted to an untrusted suffix proposal");
            var actual = KernelCombatCapture.Capture(battle);
            Set(planner, "tournamentRoot", actual); Set(planner, "tournamentParty", battle.Players.ToArray());
            Set(planner, "tournamentActors", new[] { owner });
            var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
            phase.SetValue(planner, Enum.Parse(phase.FieldType, "NoPotions"));
            Set(planner, "tournamentOptions", new TournamentOptions { Request = request });
            var replayObserver = new Harmony("coopbots.planner-proposal-observer");
            var advance = AccessTools.Method(typeof(KernelScriptReplayRun), nameof(KernelScriptReplayRun.Advance));
            replayObservation = "not-observed";
            replayObserver.Patch(advance, postfix: new HarmonyMethod(typeof(KernelPlannerRequestScenarios), nameof(ReplayObserved)));
            var ticks = 0;
            try { while ((bool)Invoke(planner, "ReplayMixedProposal")! && ticks++ < 1000) { } }
            finally { replayObserver.Unpatch(advance, HarmonyPatchType.All, replayObserver.Id); }
            var renewed = ((TournamentOptions)Get(planner, "tournamentOptions")!).ReferenceResult;
            Console.WriteLine($"Planner proposal observation: ticks={ticks} {replayObservation} requestStop={request.StopReason} renewed={renewed?.Record}");
            Check(renewed is { Record.Verified: true, Record.Victory: true } && renewed.IsFor(request)
                && Get(planner, "mixedProposal") is null,
                "only a complete native replay from the changed actual root can renew proposal provenance");
        });
    }

    private static void ReplayObserved(KernelScriptReplayRun __instance)
    {
        var state = (KernelSession)AccessTools.Field(typeof(KernelScriptReplayRun), "state").GetValue(__instance)!;
        var actions = (System.Collections.ICollection)AccessTools.Field(typeof(KernelScriptReplayRun), "actions").GetValue(__instance)!;
        replayObservation = $"done={__instance.Done} failure={__instance.Failure} actions={actions.Count} "
            + $"enemies=[{string.Join(',', state.Enemies.Select(e => state.Hp(e)))}] result={__instance.Result?.Record}";
    }

    private static void Order()
    {
        WithPlan((planner, battle, owner, enemy, _) => {
            enemy.SetCurrentHpInternal(12);
            potionSawRenewedRoot = false;
            var ordering = new Harmony("coopbots.planner-request-order");
            var method = AccessTools.Method(typeof(KernelCombatPlanner), "TryPotionOpportunity");
            ordering.Patch(method, prefix: new HarmonyMethod(typeof(KernelPlannerRequestScenarios), nameof(PotionAfterReview)));
            try
            {
                Invoke(planner, "TryTournamentDecision", battle, new[] { owner }, false,
                    KernelCombatPlanner.TournamentDecisionPoint(battle, 1), null);
                Check(potionSawRenewedRoot,
                    "new decision must review settled mixed input before using a cached potion forecast");
            }
            finally { ordering.Unpatch(method, HarmonyPatchType.All, ordering.Id); }
        });
    }

    private static void Handover()
    {
        WithPlan((planner, battle, _, enemy, _) => {
            var request = new KernelDecisionRequest(923, 10000);
            Set(planner, "tournamentRequest", request);
            enemy.SetCurrentHpInternal(12); AutoPilot.Set(battle.Players[1].NetId, true);
            Check(!(bool)Invoke(planner, "ReviewMixedInput", battle)!
                && Get(planner, "tournamentScript") is null
                && Get(planner, "mixedProposal") is TournamentResult { Record.Verified: false }
                && ReferenceEquals(Get(planner, "tournamentRequest"), request),
                "handing the last human seat to a Bot must renew the old mixed plan's control domain");
        });
    }

    private static void Refinement()
    {
        WithPlan((planner, battle, _, _, root) => {
            var script = (IReadOnlyList<KernelTeamSearch.Action>)Get(planner, "tournamentScript")!;
            Invoke(planner, "StartRefinement", root, battle, script);
            Check(Get(planner, "refinement") is null,
                "a living human seat must prevent speculative next-turn refinement");
            var human = battle.Players[1]; AutoPilot.Set(human.NetId, true);
            Invoke(planner, "StartRefinement", KernelCombatCapture.Capture(battle), battle, script);
            var job = (KernelScriptRefinementRun?)Get(planner, "refinement");
            var request = (KernelDecisionRequest?)Get(planner, "refinementRequest");
            Check(job is not null && request is not null
                && ReferenceEquals(AccessTools.Field(typeof(KernelScriptRefinementRun), "request").GetValue(job), request),
                "handed-over all-Bot table must receive its own bounded refinement request");
            var foreground = new KernelDecisionRequest(924, 10000);
            Set(planner, "tournamentRequest", foreground);
            planner.AdvanceRefinement(battle);
            Check(job!.ComputeMs == 0 && !job.Done,
                "an active foreground request must prevent extra speculative native work");
            foreground.Cancel("fixture-foreground-finished");
            planner.AdvanceRefinement(battle);
            Check(job.ComputeMs > 0,
                "stopped foreground request must allow the independently funded refinement to progress");
            AutoPilot.Set(human.NetId, false);
            planner.AdvanceRefinement(battle);
            Check(Get(planner, "refinement") is null && request!.IsCancelled,
                "returning a living seat to a human must cancel the old speculative control domain");
        });
    }

    private static bool PotionAfterReview(KernelCombatPlanner __instance, ref bool __result)
    {
        potionSawRenewedRoot = Get(__instance, "tournamentScript") is null
            && Get(__instance, "mixedProposal") is TournamentResult { Record.Verified: false }
            && Get(__instance, "tournamentRequest") is KernelDecisionRequest { IsCancelled: false };
        __result = true; return false;
    }

    private static void WithPlan(Action<KernelCombatPlanner, CombatState, Player,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature, KernelSession> body)
    {
        var (battle, owner, enemy) = KernelPlanningStateScenarios.Board();
        enemy.SetCurrentHpInternal(24);
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 2);
        human.ResetCombatState(); battle.AddPlayer(human);
        human.Creature.SetMaxHpInternal(80); human.Creature.SetCurrentHpInternal(80);
        human.PlayerCombatState!.Phase = PlayerTurnPhase.Play; human.PlayerCombatState.GainEnergy(3);
        var ownerDriven = AutoPilot.IsAutopiloted(owner.NetId); var humanDriven = AutoPilot.IsAutopiloted(human.NetId);
        AutoPilot.Set(owner.NetId, true); AutoPilot.Set(human.NetId, false);
        var planner = new KernelCombatPlanner();
        try
        {
            var root = KernelCombatCapture.Capture(battle);
            var script = root.Hand(owner).Select(card => new KernelTeamSearch.Action(owner, card, enemy,
                CardStateKey: root.CardStateKey(card))).ToArray();
            // The input is deliberately old victory metadata; it is not an
            // oracle assertion. The reroot must remove this claim before reuse.
            var old = TerminalRecord.Capture(root, battle.Players.ToArray(), false) with {
                Victory = true, Kind = EvidenceKind.VerifiedTerminal, CutoffReason = "" };
            Set(planner, "tournamentScript", script); Set(planner, "tournamentScriptRecord", old);
            Set(planner, "tournamentScriptCombat", battle); Set(planner, "tournamentScriptIndex", 0);
            Invoke(planner, "CaptureMixedContinuation", root, battle, script, 0);
            body(planner, battle, owner, enemy, root);
        }
        finally
        {
            planner.Reset(newCombat: true);
            AutoPilot.Set(owner.NetId, ownerDriven); AutoPilot.Set(human.NetId, humanDriven);
        }
    }

    private static object? Invoke(KernelCombatPlanner planner, string name, params object?[] args)
        => AccessTools.Method(typeof(KernelCombatPlanner), name).Invoke(planner, args);
    private static object? Get(KernelCombatPlanner planner, string name)
        => AccessTools.Field(typeof(KernelCombatPlanner), name).GetValue(planner);
    private static void Set(KernelCombatPlanner planner, string name, object value)
        => AccessTools.Field(typeof(KernelCombatPlanner), name).SetValue(planner, value);
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Planner request: " + message); }
    private static bool Format(ref string __result) { __result = ""; return false; }
}
