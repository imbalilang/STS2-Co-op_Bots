// R2 physical refinement proof: work/architecture-refactor-20261009/r2/root-refinement-proof7-v3/proof.json
// refinement-atomic-stop: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.Exception: Night decisions: cancelled refinement must stop before replaying its frozen prefix or publishing a suffix
// refinement-result-ownership: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.Exception: Night decisions: a completed suffix from another request must not replace the current script
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static partial class NightDecisionScenarios
{
    private static string? predictedLive;
    private static bool LiveText(ref string __result) { __result = predictedLive!; return false; }
    private static KernelSession? refinementCaptureFixture;
    private static bool RefinementCapture(ref KernelSession __result)
    {
        if (refinementCaptureFixture is null) return true;
        __result = refinementCaptureFixture.Fork(); return false;
    }
    internal static void Refinement()
    {
        Initialize();
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 126);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: "NIGHT-REFINEMENT"));
        p.ResetCombatState(); c.AddPlayer(p); c.RoundNumber = 1; p.MaxEnergy = 3;
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        // Mock ATTACK is deliberately unmodeled and marks environmental risk.
        // A real supported BITE crosses the native round and grants 7 block;
        // Bludgeon still has to produce the actual winning continuation.
        var e = c.CreateCreature(ModelDb.Monster<BowlbugEgg>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(24); e.SetCurrentHpInternal(24);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["BITE_MOVE"], true);
        var defend = c.CreateCard<DefendIronclad>(p); p.PlayerCombatState.Hand.AddInternal(defend);
        for (var i = 0; i < 5; i++) p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<Bludgeon>(p));
        var root = KernelSession.Capture(c); var text = root.PartyStateText();
        var prefix = new KernelTeamSearch.Action[] {
            new(p, defend, null, CardStateKey: root.CardStateKey(defend)), new(p, null, null, EndTurn: true),
        };
        var old = TurnStartFixtureCapture.Record(root, prefix.Concat(Enumerable.Range(0, 10).Select(_ => new KernelTeamSearch.Action(p, null, null, EndTurn: true))));
        var request = new KernelDecisionRequest(912, 10000, 1);
        var cancelledRequest = new KernelDecisionRequest(911, 10000, 1);
        var cancelledTrial = new KernelScriptRefinementRun(root, [p], [p], old,
            new(ReserveMs: 0) { Request = cancelledRequest }, 5000);
        cancelledRequest.Cancel("fixture-refinement-cancel");
        cancelledTrial.Advance(TimeSpan.FromMilliseconds(1));
        Check(cancelledTrial.Done && cancelledTrial.BoundaryState is null
            && cancelledTrial.Baseline is null && cancelledTrial.Result is null
            && cancelledTrial.StopReason == cancelledRequest.StopReason,
            "cancelled refinement must stop before replaying its frozen prefix or publishing a suffix");
        var job = new KernelScriptRefinementRun(root, [p], [p], old, new(ReserveMs: 0) { Request = request }, 5000);
        var ticks = 0;
        // Interrupt as soon as a complete improved continuation is available,
        // before the entire tournament/compute budget has to finish.
        while (!job.Done && !job.Improved && ticks++ < 50000) job.Advance(TimeSpan.FromMilliseconds(1));
        job.FinishAtBoundary();
        Check(job.BoundaryIndex == prefix.Length && job.BoundaryState?.RoundsAdvanced == 1,
            "refinement must freeze the entire current turn before searching");
        Check(job.Improved && job.Result?.Record is { Verified: true, Victory: true },
            "idle slices must find and publish a better complete next-turn line");
        Check(root.PartyStateText() == text && p.PlayerCombatState.Hand.Cards.Contains(defend),
            "refinement must not execute or mutate the live turn");
        var replay = job.BoundaryState!.Fork();
        foreach (var action in job.Result!.Script!) Check(replay.ReplayPlanned(action, 80, out _), "improved suffix must replay natively");
        Check(replay.HasWon, "replacement ending must be a native victory");
        var completed = job.Result;
        typeof(KernelScriptRefinementRun).GetProperty(nameof(job.Result))!.SetValue(job, job.Baseline);
        Check(!job.Improved, "equal suffixes must retain the incumbent");
        typeof(KernelScriptRefinementRun).GetProperty(nameof(job.Result))!.SetValue(job, completed);
        Check(KernelCombatPlanner.RefinementMatches(job.BoundaryText, job.BoundaryText!)
            && !KernelCombatPlanner.RefinementMatches(job.BoundaryText, job.BoundaryText + "changed"),
            "precomputed suffix must require the same actual boundary state");

        // Exercise the production splice separately from native replay. Only
        // the live text is supplied by the fixture; cursor/arrays/rounds use
        // the real planner. This is not a live game or replay verification.
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var planner = new KernelCombatPlanner(); var type = typeof(KernelCombatPlanner);
        void Set(string field, object value) => type.GetField(field, flags)!.SetValue(planner, value);
        Set("refinement", job); Set("refinementRound", 2); Set("refinementScript", old);
        Set("refinementRequest", request);
        Set("tournamentScript", old); Set("tournamentScriptIndex", prefix.Length); Set("tournamentScriptCombat", c);
        c.RoundNumber = 2; predictedLive = job.BoundaryText;
        var harmony = new Harmony("coopbots.refinement-splice");
        harmony.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.CaptureLivePartyText)),
            prefix: new HarmonyMethod(typeof(NightDecisionScenarios), nameof(LiveText)));
        // Patch the actual large capture entry, which remains observable after
        // full-suite JIT warmup; the tiny factory may legitimately be inlined.
        harmony.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.Capture)),
            prefix: new HarmonyMethod(typeof(NightDecisionScenarios), nameof(RefinementCapture)));
        try
        {
            var incomplete = new KernelScriptRefinementRun(root, [p], [p], old, new(ReserveMs: 0), 5000);
            var boundaryTicks = 0;
            // A zero slice advances one native prefix action: stop immediately
            // after the boundary exists, before evaluating either suffix.
            while (incomplete.BoundaryState is null && !incomplete.Done && boundaryTicks++ < 20)
                incomplete.Advance(TimeSpan.Zero);
            Check(incomplete.BoundaryState is not null && incomplete.Result is null && incomplete.Baseline is null,
                "incomplete refinement fixture must reach a native boundary before producing a suffix");
            Set("refinement", incomplete); Set("tournamentScriptIndex", incomplete.BoundaryIndex);
            predictedLive = incomplete.BoundaryText + "different RNG";
            type.GetMethod("TryAdoptRefinement", flags)!.Invoke(planner, [c]);
            // R2: require Result before comparing the frozen boundary ->
            // System.Exception: Night decisions: an unfinished refinement must still discard a continuation from a mismatching native boundary
            // RED exit 1 / byte-restored GREEN exit 0:
            // work/decision-repair-20261007/refinement-incomplete-r2-results.json.
            Check(type.GetField("tournamentScript", flags)!.GetValue(planner) is null,
                "an unfinished refinement must still discard a continuation from a mismatching native boundary");
            Set("tournamentScript", old); Set("tournamentScriptIndex", incomplete.BoundaryIndex);
            Set("tournamentScriptCombat", c); Set("refinement", incomplete);
            predictedLive = incomplete.BoundaryText;
            type.GetMethod("TryAdoptRefinement", flags)!.Invoke(planner, [c]);
            Check(ReferenceEquals(type.GetField("tournamentScript", flags)!.GetValue(planner), old),
                "an unfinished but matching boundary must keep its executable incumbent");
            Set("refinement", incomplete); Set("tournamentScriptIndex", incomplete.BoundaryIndex + 1);
            predictedLive = incomplete.BoundaryText + "different cursor";
            type.GetMethod("TryAdoptRefinement", flags)!.Invoke(planner, [c]);
            Check(ReferenceEquals(type.GetField("tournamentScript", flags)!.GetValue(planner), old),
                "a boundary from a different cursor must not invalidate the current continuation");
            Set("refinement", job); Set("tournamentScriptIndex", prefix.Length);
            predictedLive = job.BoundaryText + "different RNG";
            type.GetMethod("TryAdoptRefinement", flags)!.Invoke(planner, [c]);
            // R9: the incumbent was replayed from the same incorrect next-turn boundary.
            // R2: remove ClearTournamentScript -> "mismatching precomputation must discard the stale continuation".
            // Evidence: work/decision-repair-20261007/root-r2-results.json.
            Check(type.GetField("tournamentScript", flags)!.GetValue(planner) is null
                && (int)type.GetField("tournamentScriptIndex", flags)!.GetValue(planner)! == 0,
                "mismatching precomputation must discard the stale continuation");
            Set("tournamentScript", old); Set("tournamentScriptIndex", prefix.Length); Set("tournamentScriptCombat", c);
            // Earlier mismatching-boundary disposal cancels its request. A
            // completed native suffix can only belong to the same active
            // request; this splice fixture must provide that real provenance.
            var spliceRequest = new KernelDecisionRequest(913, 10000, 1);
            var spliceJob = new KernelScriptRefinementRun(root, [p], [p], old,
                new(ReserveMs: 0) { Request = spliceRequest }, 5000);
            var spliceTicks = 0;
            while (!spliceJob.Done && !spliceJob.Improved && spliceTicks++ < 50000)
                spliceJob.Advance(TimeSpan.FromMilliseconds(1));
            spliceJob.FinishAtBoundary();
            Check(spliceJob.Result is { } spliceResult && spliceResult.IsFor(spliceRequest),
                "splice fixture must hold a completed result from its active request");
            Set("refinement", spliceJob); Set("refinementRequest", new KernelDecisionRequest(914, 10000, 1));
            predictedLive = spliceJob.BoundaryText;
            type.GetMethod("TryAdoptRefinement", flags)!.Invoke(planner, [c]);
            Check(ReferenceEquals(type.GetField("tournamentScript", flags)!.GetValue(planner), old)
                && (int)type.GetField("tournamentScriptIndex", flags)!.GetValue(planner)! == prefix.Length,
                "a completed suffix from another request must not replace the current script");
            Set("refinement", spliceJob); Set("refinementRequest", spliceRequest); predictedLive = spliceJob.BoundaryText;
            // This test supplies the production splice's actual-capture seam.
            // Its board has not executed the prefix; it does not prove a full
            // native/live boundary. Separate contract cases drive mismatches.
            refinementCaptureFixture = spliceJob.BoundaryState;
            type.GetMethod("TryAdoptRefinement", flags)!.Invoke(planner, [c]);
            Check((int)type.GetField("tournamentScriptIndex", flags)!.GetValue(planner)! == 0
                && ReferenceEquals(type.GetField("tournamentScript", flags)!.GetValue(planner), spliceJob.Result!.Script),
                "live splice must start the improved suffix at its first action");
        }
        finally { harmony.UnpatchAll(harmony.Id); predictedLive = null; refinementCaptureFixture = null; }
        Check(PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(BotRuntime), nameof(BotRuntime.Tick)))
            .Any(i => i.operand is MethodInfo called && called.Name == "AdvanceRefinement"),
            "runtime ticks must fund refinement before pacing and busy returns");
        Console.WriteLine($"PASS: idle refinement freezes the current turn and replaces only a completed better suffix ({ticks} slices, {job.ComputeMs:F1}ms).");
    }
}
