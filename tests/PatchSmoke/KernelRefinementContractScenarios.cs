using System.Collections;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

// Function-level refinement contract tests. The prefix and winning suffix are
// really replayed by the native simulation; no fixture supplies a terminal
// verdict. Only the production adoption entry's actual-capture/live-text seam
// is supplied: this does not claim that a live game executed the prefix.
// Actual production R2 (2026-10-09), forced private Rebuild and native focused entry.
// Proof: work/architecture-refactor-20261009/r2/model-refinement-contract-proof/proof.json
// Completion: work/architecture-refactor-20261009/r2/model-refinement-contract-proof-v2/proof.json
// Initial metadata mutation could not compile; kept invalid, then public record-copy fault proved it.
// Six mechanisms / nine physical records; four missed state axes share one predicate.
// Every record: baseline build/run 0/0, mutant 0/1, exact-byte restored 0/0.
// Named RED text below is copied from those actual mutant logs; not a full-gate claim.
// refinement-complete-contract-cost-expiry:
// System.InvalidOperationException: Refinement contract: cost-expiry mismatch must discard the deployed old suffix and its terminal evidence
// refinement-complete-contract-hidden-counter:
// System.InvalidOperationException: Refinement contract: hidden-counter mismatch must discard the deployed old suffix and its terminal evidence
// refinement-complete-contract-physical-cards:
// System.InvalidOperationException: Refinement contract: physical-cards mismatch must discard the deployed old suffix and its terminal evidence
// refinement-complete-contract-unavailable:
// System.InvalidOperationException: Refinement contract: unavailable mismatch must discard the deployed old suffix and its terminal evidence
// refinement-matched-native-reuse:
// System.InvalidOperationException: Refinement contract: a matching complete refinement contract must reuse its genuine native suffix evidence
// refinement-demote-terminal:
// System.InvalidOperationException: Refinement contract: a refinement cache miss must demote old terminal checkpoints and request ownership to an untrusted proposal
// refinement-discard-old-checkpoints:
// System.InvalidOperationException: Refinement contract: a refinement cache miss must demote old terminal checkpoints and request ownership to an untrusted proposal
// refinement-actual-root:
// System.InvalidOperationException: Refinement contract: refinement cache miss must install the actual captured root instead of the predicted boundary
// refinement-replayed-current-ownership:
// System.InvalidOperationException: Refinement contract: only actual-root completed native replay may renew refinement proposal provenance for competition
internal static class KernelRefinementContractScenarios
{
    private static KernelSession? capturedActual;
    private static string? capturedText;
    private static int captureCalls;

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var patches = new Harmony("coopbots.refinement-contract");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        // The production facade still configures its synchronous seat snapshot.
        // Observe the large capture body, not the small factory that a warmed
        // TryAdoptRefinement callsite may already have inlined.
        var capture = AccessTools.Method(typeof(KernelSession), nameof(KernelSession.Capture));
        var liveText = AccessTools.Method(typeof(KernelSession), nameof(KernelSession.CaptureLivePartyText));
        patches.Patch(format, prefix: new HarmonyMethod(typeof(KernelRefinementContractScenarios), nameof(Format)));
        patches.Patch(capture, prefix: new HarmonyMethod(typeof(KernelRefinementContractScenarios), nameof(Capture)));
        patches.Patch(liveText, prefix: new HarmonyMethod(typeof(KernelRefinementContractScenarios), nameof(LiveText)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("same-contract", () => Exercise("same-contract")),
                ("cost-expiry", () => Exercise("cost-expiry")),
                ("hidden-counter", () => Exercise("hidden-counter")),
                ("physical-cards", () => Exercise("physical-cards")),
                ("unavailable", () => Exercise("unavailable")),
                ("actual-replay", () => Exercise("actual-replay")) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: refinement contract " + test.Name); }
        }
        finally
        {
            capturedActual = null; capturedText = null;
            // Owner-specific removal preserves a pre-existing production/test
            // patch even when Harmony shares its PatchMethod with another owner.
            foreach (var method in new[] { format, capture, liveText })
                patches.Unpatch(method, HarmonyPatchType.All, patches.Id);
        }
    }

    private static void Exercise(string name)
    {
        var (combat, owner, mock) = KernelPlanningStateScenarios.Board();
        owner.MaxEnergy = 3;
        // MockAttack's generic attack is intentionally an environmental-risk
        // approximation. A reusable contract needs an audited actual move.
        combat.RemoveCreature(mock);
        var enemy = combat.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.BowlbugEgg>().ToMutable(),
            CombatSide.Enemy, "egg");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        // The native Bite leaves 7 block: the first Strike removes 6, and the
        // second really kills its remaining 5HP instead of a fixture verdict.
        enemy.SetMaxHpInternal(5); enemy.SetCurrentHpInternal(5);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["BITE_MOVE"], true);
        var previousControl = AutoPilot.IsAutopiloted(owner.NetId);
        AutoPilot.Set(owner.NetId, true);
        var planner = new KernelCombatPlanner();
        try
        {
            var costWitness = name is "cost-expiry" or "actual-replay";
            if (costWitness)
                foreach (var card in owner.PlayerCombatState!.Hand.Cards) card.EnergyCost.AddThisCombat(-1);
            if (name == "hidden-counter")
                ModelDb.Power<JugglingPower>().ToMutable().ApplyInternal(owner.Creature, 1, true);
            var root = KernelSession.Capture(combat).ConfigureSynchronousChoicePlayers([owner.NetId]);
            var script = new[] { new KernelTeamSearch.Action(owner, null, null, EndTurn: true) }
                .Concat(root.Hand(owner).Select(card => new KernelTeamSearch.Action(owner, card, enemy,
                    CardStateKey: root.CardStateKey(card)))).ToArray();
            script = TurnStartFixtureCapture.Record(root, script);
            var refinementRequest = new KernelDecisionRequest(940, 10000, 1);
            var job = new KernelScriptRefinementRun(root, [owner], [owner], script,
                new TournamentOptions(ReserveMs: 0) { Request = refinementRequest }, 10000);
            // Stop on the real baseline before funding any suffix tournament.
            for (var i = 0; job.Baseline is null && !job.Done && i < 16; i++) job.Advance(TimeSpan.Zero);
            job.FinishAtBoundary();
            Check(job.BoundaryIndex == 1 && job.BoundaryState is not null
                && job.Baseline is { Record.Verified: true, Record.Victory: true, Script.Count: 2 }
                && job.Baseline.IsFor(refinementRequest),
                "fixture must produce a real native next-turn terminal from its refinement request");

            var boundary = job.BoundaryState!;
            var old = WithNativeCheckpoints(boundary, owner, job.Baseline!);
            // Enrich the genuine baseline with independently native-captured,
            // action-aligned checkpoints, without changing its actual record.
            AccessTools.Property(typeof(KernelScriptRefinementRun), nameof(job.Baseline)).SetValue(job, old);
            AccessTools.Property(typeof(KernelScriptRefinementRun), nameof(job.Result)).SetValue(job, old);
            var actual = boundary.Fork();
            if (costWitness)
            {
                foreach (var card in actual.Simulator.State.GetPlayerCombatState(owner).Hand.Cards)
                {
                    var preview = card.MutablePreview;
                    ((IList)AccessTools.Field(preview.EnergyCost.GetType(), "_localModifiers")
                        .GetValue(preview.EnergyCost)!).Clear();
                    preview.EnergyCost.AddThisTurn(-1);
                }
            }
            else if (name == "hidden-counter")
            {
                var store = actual.Simulator.StateStore;
                var states = (IDictionary)AccessTools.Field(typeof(PredictionStateStore), "_states").GetValue(store)!;
                states.Values.Cast<object>().OfType<JugglingPredictionState>().Single().AttacksPlayedThisTurn = 2;
            }
            else if (name == "physical-cards")
            {
                var hand = actual.Simulator.State.GetPlayerCombatState(owner).Hand;
                var cards = hand.Cards.ToArray();
                foreach (var card in cards) hand.Remove(card);
                foreach (var card in cards.Reverse()) hand.Add(card);
            }
            else if (name == "unavailable")
                actual.Simulator.StateStore.Get(actual.Hand(owner)[0], () => new UnqualifiedState());

            Check(boundary.PartyStateText() == actual.PartyStateText(),
                name + " witness must alias the old visible refinement boundary");
            var before = boundary.PlanningState([owner.NetId]);
            var after = actual.PlanningState([owner.NetId]);
            var unavailable = string.Join(',', before.Semantics.Split(';').Where(s => s.Contains("unavailable", StringComparison.Ordinal)));
            Console.WriteLine($"Refinement contract witness: {name} reusable={before.Reusable}/{after.Reusable} "
                + $"equalText={before.Semantics == after.Semantics} fork={boundary.CanFork}/{actual.CanFork} "
                + $"risk={boundary.LastActionHadEnvironmentalRisk}/{actual.LastActionHadEnvironmentalRisk} "
                + $"projection={boundary.ProjectionDifferences.Count}/{actual.ProjectionDifferences.Count} "
                + $"unavailable={unavailable} gaps={string.Join(',', PredictionCoverage.Collect(boundary.Simulator).Where(g => !g.Compensated))}");
            if (name == "physical-cards")
                Check(before.Semantics == after.Semantics && !before.Matches(after),
                    "physical occurrence witness must differ only in supported physical identities");
            if (name == "unavailable")
                Check(before.Reusable && !after.Reusable,
                    "unqualified actual facts must make reuse unavailable without rejecting a native action");
            Check(before.Matches(after) == (name == "same-contract"),
                name + " witness must exercise the intended complete-state contract");

            // Warm only an independently owned replay before creating the
            // foreground deadline. Cold JIT may legitimately consume its
            // fixed 120ms proposal phase; that is not this contract's subject.
            var oracle = Replay(actual, owner, old);
            Check(oracle.Result is { Record.Verified: true, Record.Victory: true },
                "changed-root witness must still permit a complete native winning suffix");
            var foreground = new KernelDecisionRequest(941, 10000);
            Set(planner, "tournamentRequest", foreground);
            Set(planner, "refinement", job); Set(planner, "refinementRequest", refinementRequest);
            Set(planner, "refinementRound", 2); Set(planner, "refinementScript", script);
            Set(planner, "tournamentScript", script); Set(planner, "tournamentScriptIndex", 1);
            Set(planner, "tournamentScriptCombat", combat); Set(planner, "tournamentScriptRecord", old.Record);
            Set(planner, "tournamentScriptAliveStates", old.ScriptAliveStates);
            Set(planner, "tournamentScriptEnemyStates", old.ScriptEnemyStates);
            combat.RoundNumber = 2;
            capturedActual = actual; capturedText = job.BoundaryText; captureCalls = 0;
            Invoke(planner, "TryAdoptRefinement", combat);
            Check(captureCalls > 0, "production complete refinement comparison must consult its explicit actual-capture seam");
            Check(ReferenceEquals(Get(planner, "tournamentRequest"), foreground) && !foreground.IsCancelled,
                "refinement adoption or re-root must preserve the current foreground request and absolute deadline");

            if (name == "same-contract")
            {
                Check(ReferenceEquals(Get(planner, "tournamentScript"), old.Script)
                    && ReferenceEquals(Get(planner, "tournamentScriptRecord"), old.Record)
                    && (int)Get(planner, "tournamentScriptIndex")! == 0 && Get(planner, "mixedProposal") is null,
                    "a matching complete refinement contract must reuse its genuine native suffix evidence");
                return;
            }

            var proposal = (TournamentResult?)Get(planner, "mixedProposal");
            Check(Get(planner, "tournamentScript") is null && Get(planner, "tournamentScriptRecord") is null
                && Get(planner, "tournamentScriptAliveStates") is null && Get(planner, "tournamentScriptEnemyStates") is null
                && (int)Get(planner, "tournamentScriptIndex")! == 0,
                name + " mismatch must discard the deployed old suffix and its terminal evidence");
            Check(proposal is { Record.Verified: false, Record.Victory: false, Script.Count: 2 }
                && proposal.ScriptCheckpoints is null && proposal.ScriptAliveStates is null && proposal.ScriptEnemyStates is null
                && proposal.RootGeneration is null && proposal.RequestProvenance is null && !proposal.IsFor(foreground),
                "a refinement cache miss must demote old terminal checkpoints and request ownership to an untrusted proposal");
            var rerooted = (KernelSession?)Get(planner, "rootSession");
            Check(rerooted is not null && rerooted.PlanningState([owner.NetId]).Semantics == after.Semantics
                && rerooted.Hand(owner).SequenceEqual(actual.Hand(owner), ReferenceEqualityComparer.Instance),
                "refinement cache miss must install the actual captured root instead of the predicted boundary");
            Check(refinementRequest.IsCancelled && Get(planner, "refinement") is null,
                "refinement cache miss must retire the old speculative request");
            if (name != "actual-replay") return;

            Set(planner, "tournamentRoot", rerooted); Set(planner, "tournamentParty", new[] { owner });
            Set(planner, "tournamentActors", new[] { owner });
            var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
            phase.SetValue(planner, Enum.Parse(phase.FieldType, "NoPotions"));
            Set(planner, "tournamentOptions", new TournamentOptions { Request = foreground });
            for (var i = 0; (bool)Invoke(planner, "ReplayMixedProposal")! && i < 32; i++) { }
            var renewed = ((TournamentOptions)Get(planner, "tournamentOptions")!).ReferenceResult;
            Check(renewed is { Record.Verified: true, Record.Victory: true, Script.Count: 2 }
                && renewed.IsFor(foreground) && ReferenceEquals(renewed.RequestProvenance, foreground)
                && renewed.RootGeneration == foreground.RootGeneration && renewed.ScriptCheckpoints is null
                && Get(planner, "mixedProposal") is null && Get(planner, "mixedProposalReplay") is null,
                "only actual-root completed native replay may renew refinement proposal provenance for competition");
            var freshOracle = Replay(actual, owner, renewed!);
            var expectedState = (KernelSession)AccessTools.Field(typeof(KernelScriptReplayRun), "state").GetValue(oracle)!;
            var actualState = (KernelSession)AccessTools.Field(typeof(KernelScriptReplayRun), "state").GetValue(freshOracle)!;
            Check(freshOracle.Result is { Record.Verified: true, Record.Victory: true }
                && expectedState.PlanningState([owner.NetId]).Semantics == actualState.PlanningState([owner.NetId]).Semantics
                && renewed!.Record!.PostCombatHp.SequenceEqual(freshOracle.Result.Record!.PostCombatHp)
                && renewed.ScriptAliveStates!.Count == renewed.Script!.Count
                && renewed.ScriptEnemyStates!.Count == renewed.Script.Count,
                "renewed refinement script must reproduce the actual native ending and complete action-aligned evidence");
        }
        finally
        {
            capturedActual = null; capturedText = null;
            planner.Reset(newCombat: true); AutoPilot.Set(owner.NetId, previousControl);
        }
    }

    private static TournamentResult WithNativeCheckpoints(KernelSession boundary, Player owner, TournamentResult candidate)
    {
        var check = boundary.Fork(); var checkpoints = new List<KernelActionCheckpoint>();
        foreach (var action in candidate.Script!)
        {
            checkpoints.Add(check.ActionCheckpoint());
            Check(check.ReplayPlanned(action, 80, out _), "native baseline checkpoint witness must replay each actual suffix action");
        }
        Check(check.HasWon && checkpoints.Count == candidate.Script!.Count,
            "checkpoint-enriched refinement baseline must retain a real native victory");
        return candidate with { ScriptCheckpoints = checkpoints.ToArray() };
    }

    private static KernelScriptReplayRun Replay(KernelSession root, Player owner, TournamentResult candidate)
    {
        var replay = new KernelScriptReplayRun(root, [owner], candidate, 80);
        for (var i = 0; !replay.Done && i < 16; i++) replay.Advance(TimeSpan.Zero);
        return replay;
    }
    private sealed class UnqualifiedState : IPredictionStateForkable
    { public object Fork(PredictionForkContext context) => new UnqualifiedState(); }
    private static bool Capture(ref KernelSession __result)
    {
        if (capturedActual is null) return true;
        captureCalls++; __result = capturedActual.Fork(); return false;
    }
    private static bool LiveText(ref string __result)
    { if (capturedText is null) return true; __result = capturedText; return false; }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Set(KernelCombatPlanner planner, string field, object? value)
        => AccessTools.Field(typeof(KernelCombatPlanner), field).SetValue(planner, value);
    private static object? Get(KernelCombatPlanner planner, string field)
        => AccessTools.Field(typeof(KernelCombatPlanner), field).GetValue(planner);
    private static object? Invoke(KernelCombatPlanner planner, string method, params object?[] args)
        => AccessTools.Method(typeof(KernelCombatPlanner), method).Invoke(planner, args);
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Refinement contract: " + message); }
}
