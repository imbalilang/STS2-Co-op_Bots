// V2 R2 actual 2026-10-24: production mutant build0 / named RED1 /
// exact source restore / rebuild0 / named GREEN0. Receipt:
// work/v2-validation/r2/receipts.json (only passed=true records below).
// prefix-stops-new-work:
// System.InvalidOperationException: Planning state: deadline expiration rather than only cancellation must forbid a new native prefix action
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using System.Collections;
using System.Reflection;

// Supported-state reuse contract tests, not a universal simulation proof.
// R2 mutation receipts and actual RED text are appended after physical runs.
// R2 actual 2026-10-09: independent private forced Rebuild 0, named runtime RED 1,
// exact restored source and six DLL/PDB artifacts; restored run 0.
// Receipt: work/architecture-refactor-20261009/r2/root-planning-combined-physical-audit.v3.json
// Scope: supported state/control/input contracts, not full gates or live win rate.
// planning-physical-card-identity: System.InvalidOperationException: Planning state: identical card names cannot conceal reordered physical instances
// planning-visible-semantics: System.InvalidOperationException: Planning state: shared shuffle RNG must invalidate an otherwise unchanged root
// planning-controlled-domain: System.InvalidOperationException: Planning state: control handover changes the planning domain
// planning-synchronous-domain: System.InvalidOperationException: Planning state: instant Bot selectors and deferred human selectors are different contracts
// planning-prepared-menu: System.InvalidOperationException: Planning state: a prepared native selector cannot authorize settled-root reuse
// planning-projection-difference: System.InvalidOperationException: Planning state: forking cannot erase a captured projection discrepancy
// continuation-atomic-yield: System.InvalidOperationException: Planning state: a prefix slice yields after one complete atomic action
// continuation-cursor-replay: System.InvalidOperationException: Planning state: resuming consumes only the remaining committed action
// continuation-exact-match: System.InvalidOperationException: Planning state: a changed actual root must not inherit the committed continuation
// planning-battle-identity: System.InvalidOperationException: Planning state: battle identity must distinguish equal native states even without any physical cards
// continuation-request-stop: System.InvalidOperationException: Planning state: stopped request must prevent a continuation prefix from starting another native action
internal static class KernelPlanningStateScenarios
{
    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.planning-state-presentation");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        presentation.Patch(format, prefix: new HarmonyMethod(typeof(KernelPlanningStateScenarios), nameof(Format)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("identity", Identity), ("piles-rng", PilesAndRng), ("mutable", Mutable),
                ("control", Control), ("unsettled", Unsettled), ("continuation", Continuation),
                ("continuation-deadline", ContinuationDeadline),
                ("diagnostic-qualification", DiagnosticQualification),
                ("diagnostic-battle", DiagnosticBattle), ("diagnostic-semantics", DiagnosticSemantics),
                ("diagnostic-hidden", DiagnosticHidden), ("diagnostic-count", DiagnosticCount),
                ("diagnostic-shiv", DiagnosticShiv), ("diagnostic-wiring", DiagnosticWiring) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: planning state " + test.Name); }
        }
        finally { presentation.Unpatch(format, HarmonyPatchType.All, presentation.Id); }
    }

    private static void Identity()
    {
        var (combat, owner, _) = Board();
        var root = KernelSession.Capture(combat);
        var before = root.PlanningState([owner.NetId]);
        Check(before.Matches(root.Fork().PlanningState([owner.NetId])), "unchanged fork retains supported evidence");
        var cards = owner.PlayerCombatState!.Hand.Cards.ToArray();
        foreach (var c in cards) owner.PlayerCombatState.Hand.RemoveInternal(c);
        foreach (var c in cards.Reverse()) owner.PlayerCombatState.Hand.AddInternal(c);
        var reordered = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(before.Semantics == reordered.Semantics && !before.Matches(reordered),
            "identical card names cannot conceal reordered physical instances");
        var (other, otherOwner, _) = Board();
        Check(!before.Matches(KernelSession.Capture(other).PlanningState([otherOwner.NetId])),
            "same-looking different battle cannot inherit the old root");
        var (empty, emptyOwner, _) = Board(cards: false);
        var (otherEmpty, otherEmptyOwner, _) = Board(cards: false);
        var emptyEvidence = KernelSession.Capture(empty).PlanningState([emptyOwner.NetId]);
        var otherEmptyEvidence = KernelSession.Capture(otherEmpty).PlanningState([otherEmptyOwner.NetId]);
        Check(emptyEvidence.Semantics == otherEmptyEvidence.Semantics && !emptyEvidence.Matches(otherEmptyEvidence),
            "battle identity must distinguish equal native states even without any physical cards");
    }

    private static void PilesAndRng()
    {
        var (combat, owner, _) = Board();
        var baseline = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        owner.PlayerCombatState.Hand.RemoveInternal(card); owner.PlayerCombatState.DiscardPile.AddInternal(card);
        Check(!baseline.Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "complete pile membership must invalidate equal HP/energy roots");
        owner.PlayerCombatState.DiscardPile.RemoveInternal(card); owner.PlayerCombatState.Hand.AddInternal(card);
        var rngBefore = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        combat.RunState.Rng.Shuffle.NextInt(100);
        Check(!rngBefore.Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "shared shuffle RNG must invalidate an otherwise unchanged root");
    }

    private static void Mutable()
    {
        var (combat, owner, _) = Board();
        var baseline = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        card.DynamicVars.Damage.BaseValue += 3;
        Check(!baseline.Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "mutable damage must invalidate the same original card instance");
        var next = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        card.UpgradeInternal();
        Check(!next.Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "in-combat upgrade must invalidate previous evidence");
    }

    private static void Control()
    {
        var (combat, owner, _) = Board();
        var root = KernelSession.Capture(combat);
        var baseline = root.PlanningState([owner.NetId]);
        Check(!baseline.Matches(root.PlanningState([])), "control handover changes the planning domain");
        var choiceContract = root.Fork(); choiceContract.ConfigureSynchronousChoicePlayers([owner.NetId]);
        Check(!baseline.Matches(choiceContract.PlanningState([owner.NetId])),
            "instant Bot selectors and deferred human selectors are different contracts");
    }

    private static void Unsettled()
    {
        var (combat, owner, _) = Board();
        var root = KernelSession.Capture(combat);
        AccessTools.Field(typeof(KernelSession), "requirePreparedPartyMenu").SetValue(root, true);
        Check(!root.PlanningState([owner.NetId]).Reusable,
            "a prepared native selector cannot authorize settled-root reuse");
        AccessTools.Field(typeof(KernelSession), "requirePreparedPartyMenu").SetValue(root, false);
        AccessTools.Property(typeof(KernelSession), nameof(KernelSession.ProjectionDifferences)).SetValue(root, new[] { "fixture-projection-difference" });
        Check(!root.Fork().PlanningState([owner.NetId]).Reusable,
            "forking cannot erase a captured projection discrepancy");
    }

    private static void Continuation()
    {
        var (combat, owner, enemy) = Board();
        // Distinct real keys/effects make a rewind distinguishable from the
        // remaining action. Two equal Strike keys legitimately resolve to the
        // remaining occurrence and cannot expose the cursor fault.
        var hand = owner.PlayerCombatState!.Hand;
        hand.RemoveInternal(hand.Cards[1]);
        hand.AddInternal(combat.CreateCard<Bash>(owner));
        var root = KernelSession.Capture(combat);
        var actions = root.Hand(owner).Select(card => new KernelTeamSearch.Action(owner, card, enemy,
            CardStateKey: root.CardStateKey(card))).ToArray();
        var lease = new KernelPlanContinuation(root, actions, [owner.NetId]);
        var stopped = new KernelPlanContinuation(root, actions, [owner.NetId]);
        var stoppedEvidence = stopped.Evidence;
        var request = new KernelDecisionRequest(920, 10000); request.Cancel("fixture-cancel");
        Check(stopped.AdvanceTo(1, TimeSpan.FromMilliseconds(20), request) && stopped.Cursor == 0
            && stopped.Failure.StartsWith("prefix-request-", StringComparison.Ordinal)
            && stopped.Evidence.Matches(stoppedEvidence),
            "stopped request must prevent a continuation prefix from starting another native action");
        Check(!lease.AdvanceTo(2, TimeSpan.Zero) && lease.Cursor == 1,
            "a prefix slice yields after one complete atomic action");
        Check(lease.AdvanceTo(2, TimeSpan.FromMilliseconds(20)) && lease.Cursor == 2 && lease.Failure.Length == 0,
            "resuming consumes only the remaining committed action");
        var expected = root.Fork();
        foreach (var action in actions) Check(expected.ReplayPlanned(action, 80, out _), "independent prefix fixture is executable");
        var actual = expected.PlanningState([owner.NetId]);
        Check(lease.Evidence.Matches(actual) && lease.ForkMatchedState(actual).PlanningState([owner.NetId]).Matches(actual),
            "a matched committed branch is reusable without advancing future actions");
        var refused = false;
        try { lease.ForkMatchedState(root.PlanningState([owner.NetId])); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "a changed actual root must not inherit the committed continuation");
    }

    private static void ContinuationDeadline()
    {
        var (combat, owner, enemy) = Board();
        var root = KernelSession.Capture(combat);
        var actions = root.Hand(owner).Select(card => new KernelTeamSearch.Action(owner, card, enemy,
            CardStateKey: root.CardStateKey(card))).ToArray();
        var expired = new KernelDecisionRequest(925, 0);
        var waiting = new KernelPlanContinuation(root, actions, [owner.NetId]);
        var before = waiting.Evidence;
        Check(waiting.AdvanceTo(0, TimeSpan.Zero, expired) && waiting.Cursor == 0
            && waiting.Evidence.Matches(before),
            "zero-prefix review is complete without native work even when no new computation is allowed");
        Check(waiting.AdvanceTo(1, TimeSpan.Zero, expired) && waiting.Cursor == 0
            && waiting.Failure == "prefix-request-request-deadline" && waiting.Evidence.Matches(before),
            "deadline expiration rather than only cancellation must forbid a new native prefix action");
        var complete = new KernelPlanContinuation(root, actions, [owner.NetId]);
        Check(complete.AdvanceTo(1, TimeSpan.FromSeconds(1)) && complete.Cursor == 1 && complete.Failure.Length == 0,
            "the continuation fixture must first complete exactly one native prefix action");
        var completed = complete.Evidence;
        for (var poll = 0; poll < 20; poll++)
            Check(complete.AdvanceTo(1, TimeSpan.Zero, expired) && complete.Cursor == 1
                && complete.Failure.Length == 0 && complete.Evidence.Matches(completed),
                "deadline cannot invalidate a completed prefix or make zero-work review repeat a native action");
        Check(complete.ForkMatchedState(completed).PlanningState([owner.NetId]).Matches(completed),
            "already completed exact prefix evidence remains usable after its computation allowance ends");
    }

    // Physical R2 2026-10-09: one shared actual baseline Rebuild0, named mutant
    // Rebuild0/RED1, exact-source restored Rebuild0/GREEN0. Receipt:
    // work/architecture-refactor-20261009/fixed-budget-performance-v3/state-diagnostics/r2-v6/proof.json
    // prepared-selector-reason: System.InvalidOperationException: Planning state: diagnostic: prepared selector qualification is stored and never labelled semantic drift
    // projection-reason: System.InvalidOperationException: Planning state: diagnostic: a projection discrepancy names its captured qualification guard
    // environmental-risk-reason: System.InvalidOperationException: Planning state: diagnostic: environmental risk names its captured qualification guard
    // unavailable-facts-reason: System.InvalidOperationException: Planning state: diagnostic: unavailable captured facts take precedence over their semantic marker
    // battle-reference-axis: System.InvalidOperationException: Planning state: diagnostic: equal native states from distinct battles name battle identity first
    // semantic-native-field: System.InvalidOperationException: Planning state: diagnostic: true native HP difference exposes field expected and actual values
    // semantic-window: System.InvalidOperationException: Planning state: diagnostic: long semantic payload is bounded around its actual differing value
    // hidden-semantic-field: System.InvalidOperationException: Planning state: diagnostic: hidden native cost-expiration difference names the complete semantic field
    // physical-count-axis: System.InvalidOperationException: Planning state: diagnostic: physical count edge follows semantic equality and precedes per-card identity
    // generated-shiv-axis: System.InvalidOperationException: Planning state: diagnostic: semantically equal native generated Shivs name physical identity without declaring P0 drift
    // refinement-log-wiring: System.InvalidOperationException: Planning state: diagnostic: actual refinement cache-removal reason must publish its first semantics axis
    // Qualification/identity diagnostics do not declare simulation drift.
    private static void DiagnosticQualification()
    {
        var (combat, owner, _) = Board();
        var root = KernelSession.Capture(combat);
        var baseline = root.PlanningState([owner.NetId]);
        Check(baseline.Reusable && baseline.FirstMismatch(root.Fork().PlanningState([owner.NetId])) is null,
            "diagnostic: an unchanged supported fork has no mismatch");
        AccessTools.Field(typeof(KernelSession), "requirePreparedPartyMenu").SetValue(root, true);
        var prepared = root.PlanningState([owner.NetId]);
        AccessTools.Field(typeof(KernelSession), "requirePreparedPartyMenu").SetValue(root, false);
        Check(!baseline.Matches(prepared) && baseline.FirstMismatch(prepared) is { } menu
            && menu.StartsWith("cacheMissAxis=qualification field=reusable expected=True actual=False", StringComparison.Ordinal)
            && menu.Contains("actualReason=prepared-party-selector", StringComparison.Ordinal),
            "diagnostic: prepared selector qualification is stored and never labelled semantic drift");
        AccessTools.Property(typeof(KernelSession), nameof(KernelSession.ProjectionDifferences)).SetValue(root, new[] { "fixture-difference" });
        var projection = root.PlanningState([owner.NetId]);
        AccessTools.Property(typeof(KernelSession), nameof(KernelSession.ProjectionDifferences)).SetValue(root, Array.Empty<string>());
        Check(baseline.FirstMismatch(projection)!.Contains("actualReason=projection-differences", StringComparison.Ordinal),
            "diagnostic: a projection discrepancy names its captured qualification guard");
        AccessTools.Property(typeof(KernelSession), nameof(KernelSession.LastActionHadEnvironmentalRisk)).SetValue(root, true);
        var risky = root.PlanningState([owner.NetId]);
        AccessTools.Property(typeof(KernelSession), nameof(KernelSession.LastActionHadEnvironmentalRisk)).SetValue(root, false);
        Check(baseline.FirstMismatch(risky)!.Contains("actualReason=environmental-risk", StringComparison.Ordinal),
            "diagnostic: environmental risk names its captured qualification guard");
        var simulator = AccessTools.Field(typeof(KernelSession), "simulator").GetValue(root)!;
        var store = (PredictionStateStore)simulator.GetType().GetProperty("StateStore")!.GetValue(simulator)!;
        store.Get(root.Hand(owner)[0], () => new DiagnosticUnqualifiedState());
        var unavailable = root.PlanningState([owner.NetId]);
        Check(!unavailable.Reusable && baseline.FirstMismatch(unavailable) is { } facts
            && facts.StartsWith("cacheMissAxis=qualification ", StringComparison.Ordinal)
            && facts.Contains("actualReason=facts-unavailable:unqualified-captured-state", StringComparison.Ordinal),
            "diagnostic: unavailable captured facts take precedence over their semantic marker");
    }

    private static void DiagnosticBattle()
    {
        var (combat, owner, _) = Board(cards: false);
        var (other, otherOwner, _) = Board(cards: false);
        var expected = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        var actual = KernelSession.Capture(other).PlanningState([otherOwner.NetId]);
        Check(expected.Semantics == actual.Semantics && !expected.Matches(actual)
            && expected.FirstMismatch(actual) == "cacheMissAxis=battle-reference field=battle.same-reference expected=True actual=False",
            "diagnostic: equal native states from distinct battles name battle identity first");
    }

    private static void DiagnosticSemantics()
    {
        var (combat, owner, _) = Board();
        var expected = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        owner.Creature.SetCurrentHpInternal(79);
        var actual = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(!expected.Matches(actual) && expected.FirstMismatch(actual) is { } diff
            && diff.StartsWith("cacheMissAxis=semantics field=p1.hp expected=80 actual=79 ", StringComparison.Ordinal),
            "diagnostic: true native HP difference exposes field expected and actual values");
        // Native captures above prove the real scalar path. This constructed
        // evidence-only edge verifies bounded rendering of an unusually large
        // existing field; it is not a newly introduced simulation oracle.
        var prefix = "field=" + new string('a', 10000);
        var longExpected = DiagnosticEvidence(combat, prefix + "X", []);
        var longActual = DiagnosticEvidence(combat, prefix + "Y", []);
        Check(longExpected.FirstMismatch(longActual) is { Length: < 350 } longDiff
            && longDiff.Contains("expected=...", StringComparison.Ordinal)
            && longDiff.Contains("X", StringComparison.Ordinal) && longDiff.Contains("Y", StringComparison.Ordinal),
            "diagnostic: long semantic payload is bounded around its actual differing value");
    }

    private static void DiagnosticHidden()
    {
        var (combat, owner, _) = Board();
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        card.EnergyCost.AddThisTurn(-1);
        var expected = KernelSession.Capture(combat);
        ((IList)AccessTools.Field(card.EnergyCost.GetType(), "_localModifiers").GetValue(card.EnergyCost)!).Clear();
        card.EnergyCost.AddThisCombat(-1);
        var actual = KernelSession.Capture(combat);
        var e = expected.PlanningState([owner.NetId]); var a = actual.PlanningState([owner.NetId]);
        Check(expected.PartyStateText() == actual.PartyStateText() && !e.Matches(a)
            && e.FirstMismatch(a) is { } diff
            && diff.StartsWith("cacheMissAxis=semantics field=card-facts ", StringComparison.Ordinal)
            && diff.Contains("expected=", StringComparison.Ordinal) && diff.Contains("actual=", StringComparison.Ordinal),
            "diagnostic: hidden native cost-expiration difference names the complete semantic field");
    }

    private static void DiagnosticCount()
    {
        var (combat, owner, _) = Board();
        var cards = owner.PlayerCombatState!.Hand.Cards.ToArray();
        // The production predicate compares count AFTER exact semantic text.
        // Native capture normally differs in pile text first; construct only
        // this remaining evidence edge using real native physical cards.
        var expected = DiagnosticEvidence(combat, "same-evidence", [cards[0]]);
        var actual = DiagnosticEvidence(combat, "same-evidence", cards);
        Check(!expected.Matches(actual) && expected.FirstMismatch(actual)
            == "cacheMissAxis=card-count field=physical-card-count expected=1 actual=2",
            "diagnostic: physical count edge follows semantic equality and precedes per-card identity");
    }

    private static void DiagnosticShiv()
    {
        var (combat, owner, _) = Board(cards: false);
        // Both are genuine new native Shiv instances. Create both before either
        // capture so a card-generation RNG/counter change cannot mask identity.
        // This isolates the identity mechanism; it does not claim reproduction
        // of the five live InfiniteBlades turn boundaries.
        var predictedShiv = combat.CreateCard<Shiv>(owner);
        var actualShiv = combat.CreateCard<Shiv>(owner);
        var hand = owner.PlayerCombatState!.Hand;
        hand.AddInternal(predictedShiv);
        var expected = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        hand.RemoveInternal(predictedShiv); hand.AddInternal(actualShiv);
        var actual = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(expected.Reusable && actual.Reusable && expected.Semantics == actual.Semantics
            && !expected.Matches(actual) && expected.FirstMismatch(actual)
            == "cacheMissAxis=card-reference field=card[0].physical-reference expected=SHIV actual=SHIV sameReference=False",
            "diagnostic: semantically equal native generated Shivs name physical identity without declaring P0 drift");
    }

    private static void DiagnosticWiring()
    {
        // Observe the full cache-removal body before its existing logger call.
        // The console environment can suppress the game's print callback;
        // patching this large method also avoids a warmed tiny logger wrapper.
        // Existing fixtures retain the real native adoption/re-root assertions.
        var logs = new List<string>();
        var observer = new Harmony("coopbots.planning-state-cache-miss-observer");
        var removal = AccessTools.Method(typeof(KernelCombatPlanner), "ClearTournamentScript");
        diagnosticMissReasons = logs;
        observer.Patch(removal, prefix: new HarmonyMethod(typeof(KernelPlanningStateScenarios), nameof(ObserveCacheRemoval)));
        try
        {
            foreach (var (name, axis) in new[] { ("cost-expiry", "semantics"),
                ("physical-cards", "card-reference"), ("unavailable", "qualification") })
            {
                logs.Clear();
                KernelRefinementContractScenarios.Run(name);
                Check(logs.Count == 1 && logs[0].Contains("cacheMissAxis=" + axis + " field=", StringComparison.Ordinal)
                    && logs[0].Contains("expected=", StringComparison.Ordinal)
                    && logs[0].Contains("actual=", StringComparison.Ordinal),
                    "diagnostic: actual refinement cache-removal reason must publish its first " + axis + " axis");
                Console.WriteLine("Planning miss logger argument: " + logs[0]);
            }
        }
        finally
        {
            diagnosticMissReasons = null;
            observer.Unpatch(removal, HarmonyPatchType.All, observer.Id);
        }
    }

    private static List<string>? diagnosticMissReasons;
    private static void ObserveCacheRemoval(string reason)
    {
        if (reason.Contains("refinement complete-state cache miss;", StringComparison.Ordinal))
            diagnosticMissReasons?.Add(reason);
    }

    private static KernelPlanningState DiagnosticEvidence(object battle, string semantics, CardModel[] cards)
        => (KernelPlanningState)typeof(KernelPlanningState).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single().Invoke([battle, semantics, cards, true, null]);

    private sealed class DiagnosticUnqualifiedState : IPredictionStateForkable
    { public object Fork(PredictionForkContext context) => new DiagnosticUnqualifiedState(); }

    internal static (CombatState Combat, Player Owner, Creature Enemy) Board(bool cards = true)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var combat = new CombatState(runState: RunState.CreateForTest([owner], seed: "PLANNING-CONTRACT")) { RoundNumber = 1 };
        owner.ResetCombatState(); combat.AddPlayer(owner);
        owner.Creature.SetMaxHpInternal(80); owner.Creature.SetCurrentHpInternal(80);
        owner.PlayerCombatState!.Phase = PlayerTurnPhase.Play; owner.PlayerCombatState.GainEnergy(3);
        if (cards)
        {
            owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
            owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        return (combat, owner, enemy);
    }

    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Planning state: " + message); }
    private static bool Format(ref string __result) { __result = ""; return false; }
}
