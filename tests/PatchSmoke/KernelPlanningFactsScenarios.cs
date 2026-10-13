using System.Collections;
using System.Reflection;
using System.Text;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

// R2: actual isolated production mutations, compile 0 / named RED 1 /
// exact source-byte restore compile 0 / GREEN 0. Raw logs, immediate
// exits, physical DLL/PDB and source bytes: work/architecture-refactor-
// 20261009/construction-calibration/r2/proof-facts.json.
// planning-cost-expiration/cost-expiry: System.InvalidOperationException: Planning facts: energy modifier expiration must invalidate equal-cost continuation roots
// planning-star-expiration/star-expiry: System.InvalidOperationException: Planning facts: star modifier expiration must invalidate equal-cost continuation roots
// planning-captured-power-payload/hidden-counter: System.InvalidOperationException: Planning facts: captured hidden power counters must invalidate equal visible roots
// planning-captured-power-payload/captured-fork: System.InvalidOperationException: Planning facts: fork-owned hidden counter mutation cannot contaminate its parent
// planning-moving-live-read/captured-fork: System.InvalidOperationException: Planning facts: captured mechanism evidence cannot read moving live hidden values
// planning-returning-card-membership/lifecycle: System.InvalidOperationException: Planning facts: captured future returning-card membership must invalidate continuation roots
// planning-unknown-state-qualification/unavailable: System.InvalidOperationException: Planning facts: unqualified captured payload prevents continuation reuse
// planning-production-wiring/cost-expiry: System.InvalidOperationException: Planning facts: energy modifier expiration must invalidate equal-cost continuation roots
// planning-production-unavailable-wiring/unavailable: System.InvalidOperationException: Planning facts: unqualified captured payload prevents continuation reuse
// Native cleanup and native internal-data controls establish that these states
// have different gameplay meanings despite equal current visible fields.
// Actual 2026-10-09 route-role/native-capture physical R2: 13 distinct
// mutants forced Rebuild0 / named runtime RED1 / exact restored Rebuild0 /
// named GREEN0. Six immediate DLL/PDB snapshots and raw source/commands:
// state-diagnostics/p0-route-ledger-v1/r2-final/proof.json. This is focused
// native-mechanism evidence; it is not a full gate or A10 win-rate claim.
// route-ledger-mixed-into-semantics: System.InvalidOperationException: Planning facts: settled paid Strike must reuse equivalent future state despite different route scoring origins
// route-metadata-wiring: System.InvalidOperationException: Planning facts: all nine route receipts must remain exact diagnostic metadata rather than future gameplay semantics
// route-metadata-fork-ownership: System.InvalidOperationException: Planning facts: fork-owned route metadata must retain its scoring values without mutating the parent
// native-hp-semantics: System.InvalidOperationException: Planning facts: real native HP differences must still invalidate future state after scoring metadata separation
// native-history-semantics: System.InvalidOperationException: Planning facts: real native damage history must still invalidate equal-HP future state after scoring metadata separation
// current-native-attack-capture: System.InvalidOperationException: Planning facts: captured finished native attack must retain its physical identity and frozen fields even after exhaust or removal
// removed-native-attack-identity: System.InvalidOperationException: Planning facts: captured finished native attack must retain its physical identity and frozen fields even after exhaust or removal
// removed-native-attack-fields: System.InvalidOperationException: Planning facts: historical native attack damage must remain a frozen future replay fact and invalidate a changed live source
// vital-spark-first-amount-change: System.InvalidOperationException: Planning facts: the first captured VitalSpark amount change must normalize native Tainted identically without mutating its root
// smoggy-captured-skill-membership: System.InvalidOperationException: Planning facts: a generated skill after a captured paid native skill must receive the same Smog affliction
// native-max-hp-semantics: System.InvalidOperationException: Planning facts: real native max-hp must invalidate equal-HP future state after scoring metadata separation
// native-gold-semantics: System.InvalidOperationException: Planning facts: real native gold must invalidate equal-HP future state after scoring metadata separation
// native-used-relic-evidence: System.InvalidOperationException: Planning facts: real native relic-used must invalidate equal-HP future state after scoring metadata separation
// Actual 2026-10-09 native power-origin/intent physical R2: nine distinct
// mutants forced Rebuild0 / named RED1 / exact source restore Rebuild0 /
// GREEN0. Six immediate DLL/PDB plus commands, raw logs and frozen source:
// state-diagnostics/p0-power-origin-v1/r2/proof.json. The native Stock
// death/spawn, later BOOT_UP/paid Strike and stateful power re-acquisition
// are in-process native controls, not Steam/performance/win-rate results.
// raw-root-retirement-in-matching: System.InvalidOperationException: Planning facts: settled native Stock death replacement must match effective future state across root origins
// completed-departed-projection: System.InvalidOperationException: Planning facts: settled native Stock death replacement must match effective future state across root origins
// retired-required-payload-projection: System.InvalidOperationException: Planning facts: retired native power payloads must not conceal the complete fresh effective hidden state
// unknown-retired-payload-not-audited: System.InvalidOperationException: Planning facts: unknown retired-instance payloads must not inherit an audited power-state retirement exemption
// effective-native-hook-order: System.InvalidOperationException: Planning facts: real native acquisition hook order differences must still invalidate equal visible power roots
// effective-reacquired-hidden-payload: System.InvalidOperationException: Planning facts: real reacquired native hidden counter differences must still reject equal visible power roots
// active-native-ai-static-state: System.InvalidOperationException: Planning facts: real active native AI static state must reject equal visible roots after retired-owner normalization
// pending-native-power-callback: System.InvalidOperationException: Planning facts: unsettled native power callbacks must prevent reuse while root origin encoding is canonicalized
// force-native-intent-cache-refresh: System.InvalidOperationException: Planning facts: native forced nonattack move must refresh the effective branch intent consumer before the next card
// Actual 2026-10-10 native history-window R2: six distinct physical
// source faults, forced Rebuild0 / named RED1 / exact source9 + six
// DLL/PDB restore / named GREEN0. Two preserved epochs (UTF8 comment
// normalization between epochs) are in state-diagnostics/
// p0-history-domain-v12/r2/proof.json and r2-part2/proof.json.
// 01-dead-advance: System.InvalidOperationException: Planning facts: actual native dead-seat history window must expire removed prior attacks when all player turn numbers advance
// 02-pending-capture: System.InvalidOperationException: Planning facts: actual native extra turn must retain full HistoryCourse card domain for nonparticipants
// 03-effective-current: System.InvalidOperationException: Planning facts: settled enemy horizon must close player-side current without prematurely promoting or losing pending history
// 04-origin-fork-ownership: System.InvalidOperationException: Planning facts: diagnostic extra transition must not mutate its captured parent
// 05-pending-semantic-domain: System.InvalidOperationException: Planning facts: changed native pending removed attack must invalidate its frozen future semantic fields
// 06-pending-physical-domain: System.InvalidOperationException: Planning facts: native extra pending removed attack must enter the physical identity contract
// Native paid exhausted/removed attacks, real SludgeSpinner death,
// Ambergris extra and later normal advance, side-only settled horizon
// and real enemy-side auto-play exercise current/previous/pending.
// Environmental-risk qualification remains strict: the R3 SLAM origin
// can be unqualified even when exact native semantics/physical refs match.
// These controls do not prove the Steam scene's exact missing card ID,
// whole-game outcome, search quality or allocation improvement.
internal static class KernelPlanningFactsScenarios
{
    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.planning-facts-presentation");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        presentation.Patch(format, prefix: new HarmonyMethod(typeof(KernelPlanningFactsScenarios), nameof(Format)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("cost-expiry", CostExpiry), ("star-expiry", StarExpiry),
                ("hidden-counter", HiddenCounter), ("captured-fork", CapturedFork),
                ("lifecycle", Lifecycle), ("unavailable", Unavailable),
                ("orichalcum-lazy", OrichalcumLazy), ("orichalcum-flag", OrichalcumFlag),
                ("orichalcum-order", OrichalcumOrder), ("orichalcum-fingerprint", OrichalcumFingerprint), ("route-ledger-native", RouteLedgerNative), ("route-ledger-metadata", RouteLedgerMetadata),
                ("route-native-hp", RouteNativeHp), ("route-native-history", RouteNativeHistory),
                ("history-course-native", HistoryCourseNative), ("vital-spark-native", VitalSparkNative),
                ("smoggy-skill-native", SmoggySkillNative), ("route-potion-native", RoutePotionNative),
                ("route-native-assets", RouteNativeAssets), ("power-origin-axebot", PowerOriginAxebot), ("power-origin-reapply", PowerOriginReapply), ("power-origin-hidden", PowerOriginHidden), ("power-origin-order", PowerOriginOrder), ("power-origin-revive", PowerOriginRevive), ("power-origin-ai", PowerOriginAi), ("power-origin-intent", PowerOriginIntent), ("monster-int-retirement", MonsterIntRetirement), ("rat-dynamic-weight", RatDynamicWeight), ("seat-death-ready", SeatDeathReady), ("history-dead-domain", HistoryDeadDomain), ("history-extra-domain", HistoryExtraDomain), ("history-enemy-window", HistoryEnemyWindow), ("afterimage-native", AfterimageNative), ("bowlbug-stunned-conditional", BowlbugStunnedConditional), ("conditional-neighbor-strict", ConditionalNeighborStrict) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: planning mechanism facts " + test.Name); }
        }
        finally { presentation.Unpatch(format, HarmonyPatchType.All, presentation.Id); }
    }

    private static void CostExpiry()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        card.EnergyCost.AddThisTurn(-1);
        var thisTurn = KernelSession.Capture(combat);
        var nativeTurn = PredictionUtils.CloneCardStateForSimulation(card);
        ((IList)Field(card.EnergyCost, "_localModifiers")!).Clear();
        card.EnergyCost.AddThisCombat(-1);
        var thisCombat = KernelSession.Capture(combat);
        var nativeCombat = PredictionUtils.CloneCardStateForSimulation(card);
        Check(nativeTurn.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0
            && nativeCombat.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0,
            "cost-expiry native controls have the same current cost");
        nativeTurn.EnergyCost.EndOfTurnCleanup(); nativeCombat.EnergyCost.EndOfTurnCleanup();
        Check(nativeTurn.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1
            && nativeCombat.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0,
            "native cost cleanup proves different future affordability");
        Check(thisTurn.PartyStateText() == thisCombat.PartyStateText(),
            "cost-expiry witness aliases the previous visible-state text");
        Check(!thisTurn.PlanningState([owner.NetId]).Matches(thisCombat.PlanningState([owner.NetId])),
            "energy modifier expiration must invalidate equal-cost continuation roots");
    }

    private static void StarExpiry()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        AccessTools.Field(typeof(CardModel), "_baseStarCost").SetValue(card, 3);
        AccessTools.Field(typeof(CardModel), "_starCostSet").SetValue(card, true);
        card.SetStarCostThisTurn(1);
        var thisTurn = KernelSession.Capture(combat);
        var nativeTurn = PredictionUtils.CloneCardStateForSimulation(card);
        ((IList)Field(card, "_temporaryStarCosts")!).Clear(); card.SetStarCostThisCombat(1);
        var thisCombat = KernelSession.Capture(combat);
        var nativeCombat = PredictionUtils.CloneCardStateForSimulation(card);
        Check(nativeTurn.CurrentStarCost == 1 && nativeCombat.CurrentStarCost == 1,
            "star-expiry native controls have the same current cost");
        nativeTurn.EndOfTurnCleanup(); nativeCombat.EndOfTurnCleanup();
        Check(nativeTurn.CurrentStarCost == 3 && nativeCombat.CurrentStarCost == 1,
            "native star cleanup proves different future affordability");
        Check(thisTurn.PartyStateText() == thisCombat.PartyStateText(),
            "star-expiry witness aliases the previous visible-state text");
        Check(!thisTurn.PlanningState([owner.NetId]).Matches(thisCombat.PlanningState([owner.NetId])),
            "star modifier expiration must invalidate equal-cost continuation roots");
    }

    private static void HiddenCounter()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        var power = ModelDb.Power<JugglingPower>().ToMutable(); power.ApplyInternal(owner.Creature, 1, true);
        SetNativeAttacks(power, 1);
        var first = KernelSession.Capture(combat);
        SetNativeAttacks(power, 2);
        var second = KernelSession.Capture(combat);
        Check(power.Amount == 1 && NativeAttacks(power) == 2,
            "native hidden counter changes without changing power amount");
        Check(first.PartyStateText() == second.PartyStateText(),
            "hidden-counter witness aliases the previous visible-state text");
        Check(!first.PlanningState([owner.NetId]).Matches(second.PlanningState([owner.NetId])),
            "captured hidden power counters must invalidate equal visible roots");
        Check(first.PlanningState([owner.NetId]).Reusable && second.PlanningState([owner.NetId]).Reusable,
            "audited captured counters qualify reusable evidence");
        var a = first.Fork(); var b = second.Fork();
        Check(a.Play(a.Hand(owner)[0], enemy, out _) && b.Play(b.Hand(owner)[0], enemy, out _),
            "both hidden-counter native prediction branches are executable");
        Check(a.Hand(owner).Count == 1 && b.Hand(owner).Count == 2,
            "captured Juggling counter controls the real third-attack clone trigger");
    }

    private static void CapturedFork()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        var card = owner.PlayerCombatState!.Hand.Cards[0]; card.EnergyCost.AddThisTurn(-1);
        var power = ModelDb.Power<JugglingPower>().ToMutable(); power.ApplyInternal(owner.Creature, 1, true);
        SetNativeAttacks(power, 1);
        var root = KernelSession.Capture(combat); var child = root.Fork();
        var before = Facts(root); var inherited = Facts(child);
        Check(before.Available && inherited.Available && before.Text == inherited.Text,
            "an unchanged native-captured fork preserves all mechanism evidence");
        card.EnergyCost.SetThisCombat(5); SetNativeAttacks(power, 7);
        Check(Facts(root) == before && Facts(child) == inherited,
            "captured mechanism evidence cannot read moving live hidden values");
        var simulator = AccessTools.Field(typeof(KernelSession), "simulator").GetValue(child)!;
        var store = simulator.GetType().GetProperty("StateStore")!.GetValue(simulator)!;
        var states = (IDictionary)Field(store, "_states")!;
        var payload = states.Values.Cast<object>().Single(v => v is JugglingPredictionState);
        ((JugglingPredictionState)payload).AttacksPlayedThisTurn = 2;
        Check(Facts(root) == before && Facts(child).Text != inherited.Text,
            "fork-owned hidden counter mutation cannot contaminate its parent");
    }

    private static void Lifecycle()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        var root = KernelSession.Capture(combat); var before = root.PlanningState([owner.NetId]);
        var child = root.Fork();
        var simulated = AccessTools.Property(typeof(KernelSession), "Combat").GetValue(child)!;
        // Native eligibility is captured into a branch-owned set. This control
        // changes future returning-card behavior without touching current piles.
        var returning = new HashSet<PredictedCard>();
        var simulator = AccessTools.Field(typeof(KernelSession), "simulator").GetValue(child)!;
        var state = simulator.GetType().GetProperty("State")!.GetValue(simulator)!;
        var playerState = state.GetType().GetMethod("GetPlayerCombatState")!.Invoke(state, [owner])!;
        var hand = playerState.GetType().GetProperty("Hand")!.GetValue(playerState)!;
        var cards = (IReadOnlyList<PredictedCard>)hand.GetType().GetProperty("Cards")!.GetValue(hand)!;
        returning.Add(cards[0]); AccessTools.Field(simulated.GetType(), "_returnToHandNextTurn").SetValue(simulated, returning);
        Check(root.PartyStateText() == child.PartyStateText(), "returning eligibility preserves current visible fields");
        Check(!before.Matches(child.PlanningState([owner.NetId])),
            "captured future returning-card membership must invalidate continuation roots");
    }

    private static void Unavailable()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        var root = KernelSession.Capture(combat);
        var simulator = AccessTools.Field(typeof(KernelSession), "simulator").GetValue(root)!;
        var store = (PredictionStateStore)simulator.GetType().GetProperty("StateStore")!.GetValue(simulator)!;
        store.Get(root.Hand(owner)[0], () => new UnqualifiedState());
        var facts = Facts(root);
        Check(!facts.Available && facts.Text.Contains("!unavailable[unqualified-captured-state]", StringComparison.Ordinal)
            && !root.PlanningState([owner.NetId]).Reusable,
            "unqualified captured payload prevents continuation reuse");
        Check(root.Play(root.Hand(owner)[0], enemy, out _),
            "unavailable reuse evidence must not refuse a legal native action");
    }

    // Live v3 P0: combat039-v3-20261009T154700Z godot.log:2471. Native
    // lazy witness reproduces captured-state Orichalcum -> LizardTail with
    // unchanged ordered relics. Actual isolated source mutations below each
    // compiled0 / named RED1 / exact source restore compiled0 / GREEN0.
    // Physical commands/exits, native outputs, before/mutant/restored bytes,
    // 6 DLL/PDB: state-diagnostics/p0-native-orichalcum-v1/physical-audit-8.json.
    // Two driver classifications were corrected in derivative metadata only:
    // true-flag fails at the stronger native effect guard; the small Order
    // callee was JIT-inlined. Original failed drivers/logs remain archived.
    // capture-orichalcum: Planning facts: settled native Orichalcum must match after lazy mirror payload allocation
    // capture-fakeorichalcum: Planning facts: settled native FakeOrichalcum must match after lazy mirror payload allocation
    // flag-orichalcum: Planning facts: captured native Orichalcum true trigger must gain block even after an early block effect
    // flag-fakeorichalcum: Planning facts: captured native FakeOrichalcum true trigger must gain block even after an early block effect
    // turn-start-disarm: Planning facts: native Orichalcum turn-start reset must disarm the captured mirror trigger
    // owned-relic-fork: Planning facts: consuming a fork-owned relic flag cannot mutate the armed parent
    // normal-hook-consume: Planning facts: normal mirror hook must consume captured Orichalcum true trigger
    // ordered-native-relics: Planning facts: native relic hook order must remain part of complete-state evidence
    private static void OrichalcumLazy()
    {
        foreach (var fake in new[] { false, true })
        {
            var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
            var native = AddOrichalcum(owner, fake);
            owner.AddRelicInternal(ModelDb.Relic<LizardTail>().ToMutable());
            var actual = KernelSession.Capture(combat);
            var predicted = actual.Fork();
            var simulator = Simulator(predicted);
            var relic = PredictedRelic(predicted, owner);
            // Native normal hook is a no-op while ShouldTrigger=false. Its
            // mirror used to allocate an extra false payload nevertheless.
            native.BeforeSideTurnEnd(null!, CombatSide.Enemy, [enemy]).GetAwaiter().GetResult();
            OrichalcumMirrors.BeforeSideTurnEnd(relic, new BeforeSideTurnEndMirrorContext()
                { Simulator = simulator, Side = CombatSide.Enemy, Participants = [enemy] });
            var e = predicted.PlanningState([owner.NetId]);
            var a = KernelSession.Capture(combat).PlanningState([owner.NetId]);
            Console.WriteLine("Native lazy witness " + native.GetType().Name + ": " + (e.FirstMismatch(a) ?? "equal"));
            Check(e.Reusable && a.Reusable && e.Matches(a),
                "settled native " + native.GetType().Name + " must match after lazy mirror payload allocation");
            Check(Facts(actual).Text == Facts(actual.Fork()).Text,
                "native relic capture is stable under an unchanged owned fork");
        }
    }

    private static void OrichalcumFlag()
    {
        foreach (var fake in new[] { false, true })
        {
            var (combat, owner, _) = KernelPlanningStateScenarios.Board();
            var native = AddOrichalcum(owner, fake);
            var before = KernelSession.Capture(combat).PlanningState([owner.NetId]);
            native.BeforeSideTurnEndVeryEarly(null!, CombatSide.Player, [owner.Creature]).GetAwaiter().GetResult();
            Check((bool)Field(native, "_shouldTrigger")!, "native very-early hook arms " + native.GetType().Name);
            var armed = KernelSession.Capture(combat);
            Check(!before.Matches(armed.PlanningState([owner.NetId])),
                "true native " + native.GetType().Name + " trigger must differ from false despite equal visible board");
            // A later early effect can add block without cancelling the
            // already armed native very-early flag (the Plating ordering).
            owner.Creature.GainBlockInternal(2);
            var parent = KernelSession.Capture(combat);
            var parentFacts = Facts(parent);
            var predicted = parent.Fork();
            var relic = PredictedRelic(predicted, owner);
            OrichalcumMirrors.BeforeSideTurnEnd(relic, new BeforeSideTurnEndMirrorContext()
                { Simulator = Simulator(predicted), Side = CombatSide.Player, Participants = [owner.Creature] });
            var expectedBlock = 2 + (int)native.DynamicVars.Block.BaseValue;
            Check(predicted.Block(owner.Creature) == expectedBlock,
                "captured native " + native.GetType().Name + " true trigger must gain block even after an early block effect");
            Check(!CapturedRelicFlag(predicted, relic),
                "normal mirror hook must consume captured " + native.GetType().Name + " true trigger");
            Check(parent.Block(owner.Creature) == 2 && Facts(parent) == parentFacts
                && Facts(parent).Text != Facts(predicted).Text,
                "consuming a fork-owned relic flag cannot mutate the armed parent");
            native.BeforeSideTurnEnd(null!, CombatSide.Player, [owner.Creature]).GetAwaiter().GetResult();
            Check(owner.Creature.Block == expectedBlock && !(bool)Field(native, "_shouldTrigger")!,
                "real native normal hook consumes the armed flag and agrees on block for " + native.GetType().Name);
            Console.WriteLine("Native flag witness " + native.GetType().Name + ": armed=True nativeBlock="
                + owner.Creature.Block + " predictedBlock=" + predicted.Block(owner.Creature) + " consumed=True parentOwned=True");
            // Native start hook clears this flag, and the production mirror
            // must clear the same owned typed state before the next turn.
            var reset = parent.Fork();
            native.BeforeSideTurnStart(null!, CombatSide.Player, [owner.Creature], combat).GetAwaiter().GetResult();
            RelicPredictionStateSupport.ResetBeforeSideTurnStart(Simulator(reset), PredictedRelic(reset, owner));
            OrichalcumMirrors.BeforeSideTurnEnd(PredictedRelic(reset, owner), new BeforeSideTurnEndMirrorContext()
                { Simulator = Simulator(reset), Side = CombatSide.Player, Participants = [owner.Creature] });
            Check(!(bool)Field(native, "_shouldTrigger")! && reset.Block(owner.Creature) == 2,
                "native " + native.GetType().Name + " turn-start reset must disarm the captured mirror trigger");
        }
    }

    private static void OrichalcumOrder()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        var relic = AddOrichalcum(owner, false);
        var tail = ModelDb.Relic<LizardTail>().ToMutable(); owner.AddRelicInternal(tail);
        var before = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        // Relics is the genuine native ordered hook-list backing field.
        var relics = (IList)Field(owner, "_relics")!;
        relics[0] = tail; relics[1] = relic;
        var after = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(!before.Matches(after), "native relic hook order must remain part of complete-state evidence");
    }

    // Added fingerprint-only actual R2 (old 8 proofs reused): replacing the
    // production fingerprint Bool with false compiled0, RED1:
    // Planning facts: native Orichalcum true/false trigger must have distinct complete search fingerprints
    // Exact source-byte restore compiled0 / GREEN0. Raw logs and 6 physical
    // artifacts: p0-native-orichalcum-v1/fingerprint-addition/r2/proof.json.
    // Native false/true captures for both relics preserve the same visible key;
    // the unchanged armed fork retains its full search fingerprint.
    private static void OrichalcumFingerprint()
    {
        foreach (var fake in new[] { false, true })
        {
            var (combat, owner, _) = KernelPlanningStateScenarios.Board();
            var native = AddOrichalcum(owner, fake);
            var disarmed = KernelSession.Capture(combat);
            native.BeforeSideTurnEndVeryEarly(null!, CombatSide.Player, [owner.Creature]).GetAwaiter().GetResult();
            var armed = KernelSession.Capture(combat);
            Check(disarmed.CompactStateKey() == armed.CompactStateKey(),
                "native trigger witness preserves every visible compact grouping field");
            Check(CombatFingerprint(disarmed) != CombatFingerprint(armed),
                "native " + native.GetType().Name + " true/false trigger must have distinct complete search fingerprints");
            Check(CombatFingerprint(armed) == CombatFingerprint(armed.Fork()),
                "unchanged captured relic fork preserves its complete search fingerprint");
        }
    }

    private static StateFingerprint CombatFingerprint(KernelSession session)
    {
        var combat = AccessTools.Property(typeof(KernelSession), "Combat").GetValue(session)!;
        object[] arguments = [new StateFingerprintBuilder(), Simulator(session)];
        AccessTools.Method(combat.GetType(), "AppendFingerprint").Invoke(combat, arguments);
        return ((StateFingerprintBuilder)arguments[0]).Finish();
    }

    private static RelicModel AddOrichalcum(MegaCrit.Sts2.Core.Entities.Players.Player owner, bool fake)
    {
        RelicModel relic = fake ? ModelDb.Relic<FakeOrichalcum>().ToMutable() : ModelDb.Relic<Orichalcum>().ToMutable();
        owner.AddRelicInternal(relic); return relic;
    }

    private static void RouteLedgerNative()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        var nativeState = AccessTools.Inner(typeof(SzgNativeCardScopeScenarios), "NativeState");
        using var active = (IDisposable)Activator.CreateInstance(nativeState,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
        var presentation = new Harmony("coopbots.route-ledger-clock");
        var machine = AccessTools.Method(typeof(CardModel), "OnPlayWrapper")
            .GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
        presentation.Patch(AccessTools.Method(machine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(SzgNativeCardScopeScenarios), "PresentationClock"));
        try
        {
            Check(AccessTools.Field(typeof(CombatState), "_cumulativeHpLost") is null
                && AccessTools.Field(typeof(CombatState), "_recoveredHp") is null,
                "native CombatState does not own route-relative search scoring ledgers");
            var root = KernelSession.Capture(combat);
            var card = owner.PlayerCombatState!.Hand.Cards[0];
            var predicted = root.Fork();
            Check(predicted.Play(card, enemy, out var reason), "ledger witness mirror paid Strike must settle: " + reason);
            var paid = card.SpendResources().GetAwaiter().GetResult();
            card.OnPlayWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), enemy, false,
                new MegaCrit.Sts2.Core.Entities.Cards.ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                    StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
            var actual = KernelSession.Capture(combat);
            var simCombat = (SimulatedCombatState)Simulator(predicted).State.CombatState;
            var actualCombat = (SimulatedCombatState)Simulator(actual).State.CombatState;
            var nativeDamage = CombatManager.Instance.History.Entries
                .OfType<MegaCrit.Sts2.Core.Combat.History.Entries.DamageReceivedEntry>()
                .Where(e => ReferenceEquals(e.Receiver, enemy)).Sum(e => e.Result.UnblockedDamage);
            Console.WriteLine($"Native paid ledger witness: paidEnergy={paid.Item1} nativeDamage={nativeDamage} "
                + $"predLost={simCombat.GetCumulativeHpLost(enemy)} rootLost={actualCombat.GetCumulativeHpLost(enemy)} "
                + $"nativeEnemyHp={enemy.CurrentHp} simEnemyHp={predicted.Hp(enemy)} "
                + $"mismatch={predicted.PlanningState([owner.NetId]).FirstMismatch(actual.PlanningState([owner.NetId])) ?? "equal"}");
            Check(paid.Item1 == 1 && nativeDamage == 6 && enemy.CurrentHp == 94
                && predicted.Hp(enemy) == enemy.CurrentHp && predicted.Energy(owner) == owner.PlayerCombatState.Energy
                && simCombat.GetCumulativeHpLost(enemy) == 6 && actualCombat.GetCumulativeHpLost(enemy) == 0,
                "real paid native damage settles identically while a fresh search root resets its scoring ledger");
            Check(predicted.PlanningState([owner.NetId]).Matches(actual.PlanningState([owner.NetId])),
                "settled paid Strike must reuse equivalent future state despite different route scoring origins");
            Check(predicted.PlanningState([owner.NetId]).SearchMetadata.Contains("_cumulativeHpLost=[", StringComparison.Ordinal)
                && actual.PlanningState([owner.NetId]).SearchMetadata.Contains("_cumulativeHpLost=null", StringComparison.Ordinal),
                "diagnostic search metadata must retain the paid native route ledger values and different origins");
            var parentMetadata = predicted.PlanningState([owner.NetId]).SearchMetadata;
            var child = predicted.Fork();
            child.Simulator.Heal(enemy, 4m);
            MegaCrit.Sts2.Core.Commands.CreatureCmd.Heal(enemy, 4m, playAnim: false).GetAwaiter().GetResult();
            var healedActual = KernelSession.Capture(combat);
            var healedCombat = (SimulatedCombatState)Simulator(child).State.CombatState;
            Check(enemy.CurrentHp == 98 && child.Hp(enemy) == 98
                && healedCombat.GetRecoveredHp(enemy) == 4
                && ((SimulatedCombatState)Simulator(healedActual).State.CombatState).GetRecoveredHp(enemy) == 0
                && predicted.PlanningState([owner.NetId]).SearchMetadata == parentMetadata
                && child.PlanningState([owner.NetId]).SearchMetadata.Contains("_recoveredHp=[", StringComparison.Ordinal),
                "native healing must preserve recovery ledger values and fork isolation without changing its fresh-root origin");
            Check(child.PlanningState([owner.NetId]).Matches(healedActual.PlanningState([owner.NetId])),
                "settled native healing must reuse equal future state while preserving route-relative recovery metadata");
            var secondCard = owner.PlayerCombatState.Hand.Cards[0];
            Check(child.Play(secondCard, enemy, out reason), "second paid mirror Strike must settle: " + reason);
            var paidAgain = secondCard.SpendResources().GetAwaiter().GetResult();
            secondCard.OnPlayWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), enemy, false,
                new MegaCrit.Sts2.Core.Entities.Cards.ResourceInfo { EnergySpent = paidAgain.Item1, EnergyValue = paidAgain.Item1,
                    StarsSpent = paidAgain.Item2, StarValue = paidAgain.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
            var twiceActual = KernelSession.Capture(combat);
            Check(paidAgain.Item1 == 1 && enemy.CurrentHp == 92 && child.Hp(enemy) == 92
                && child.Energy(owner) == owner.PlayerCombatState.Energy
                && healedCombat.GetCumulativeHpLost(enemy) == 12 && healedCombat.GetRecoveredHp(enemy) == 4
                && child.PlanningState([owner.NetId]).Matches(twiceActual.PlanningState([owner.NetId])),
                "multiple paid native actions must preserve exact future equality without rebasing search scoring ledgers");
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }


    private sealed class NativeFactsState : IDisposable
    {
        private readonly IDisposable native;
        private readonly Harmony clock = new("coopbots.facts-native-clock");
        internal NativeFactsState(CombatState combat)
        {
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(SzgNativeCardScopeScenarios), "NativeState"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
            var machine = AccessTools.Method(typeof(CardModel), "OnPlayWrapper")
                .GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
            clock.Patch(AccessTools.Method(machine, "MoveNext"),
                transpiler: new HarmonyMethod(typeof(SzgNativeCardScopeScenarios), "PresentationClock"));
        }
        public void Dispose() { clock.UnpatchAll(clock.Id); native.Dispose(); }
    }
    private static void NativePaidCard(CardModel card, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
    {
        var paid = card.SpendResources().GetAwaiter().GetResult();
        Check(paid.Item1 == 1, "native control pays the real one-energy card cost");
        card.OnPlayWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), target, false,
            new MegaCrit.Sts2.Core.Entities.Cards.ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
    }
    private static SimulatedCombatState PredictedCombat(KernelSession session)
        => (SimulatedCombatState)Simulator(session).State.CombatState;
    private static readonly string[] RouteReceiptNames = ["_cumulativeHpLost", "_recoveredHp", "_potionUses",
        "_longTermResourceValue", "_angerCopiesGenerated", "_growthRewards", "_deathSaveRelicHpRestored",
        "_deathSavePotionHpRestored", "_deathSaveUseCount"];
    private static void RouteLedgerMetadata()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        using var native = new NativeFactsState(combat);
        var root = KernelSession.Capture(combat);
        var before = root.PlanningState([owner.NetId]);
        var child = root.Fork();
        var state = PredictedCombat(child);
        // Explicit branch-event unit control, not a claim of native rewards:
        // metadata serialization must preserve the existing solver values.
        state.RecordHpRecovered(owner.Creature, 4);
        state.RecordLongTermResource(3); state.RecordAngerCopyGenerated();
        state.RecordGrowthReward(GrowthSource.TheScythe);
        state.RecordDeathSaveRelicHpRestored(5); state.RecordDeathSavePotionHpRestored(6);
        var evidence = child.PlanningState([owner.NetId]);
        Check(RouteReceiptNames.All(name => evidence.SearchMetadata.Contains(";" + name + "=", StringComparison.Ordinal)
                && !evidence.Semantics.Contains(name, StringComparison.Ordinal))
            && evidence.SearchMetadata.Contains("_longTermResourceValue=3", StringComparison.Ordinal)
            && evidence.SearchMetadata.Contains("_angerCopiesGenerated=1", StringComparison.Ordinal)
            && evidence.SearchMetadata.Contains("_deathSaveRelicHpRestored=5", StringComparison.Ordinal)
            && evidence.SearchMetadata.Contains("_deathSavePotionHpRestored=6", StringComparison.Ordinal)
            && evidence.SearchMetadata.Contains("_deathSaveUseCount=2", StringComparison.Ordinal),
            "all nine route receipts must remain exact diagnostic metadata rather than future gameplay semantics");
        Check(before.Matches(evidence) && root.PlanningState([owner.NetId]).SearchMetadata == before.SearchMetadata
            && state.GetRecoveredHp(owner.Creature) == 4 && state.LongTermResourceValue == 3
            && state.AngerCopiesGenerated == 1 && state.DeathSaveUseCount == 2,
            "branch scoring values must remain unchanged and cannot contaminate native reuse or their captured parent");
        var grandchild = child.Fork();
        PredictedCombat(grandchild).RecordHpRecovered(owner.Creature, 3);
        Check(PredictedCombat(child).GetRecoveredHp(owner.Creature) == 4
            && PredictedCombat(grandchild).GetRecoveredHp(owner.Creature) == 7
            && child.PlanningState([owner.NetId]).SearchMetadata == evidence.SearchMetadata,
            "fork-owned route metadata must retain its scoring values without mutating the parent");
    }
    private static void RouteNativeHp()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        using var native = new NativeFactsState(combat);
        var before = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        MegaCrit.Sts2.Core.Commands.CreatureCmd.SetCurrentHp(owner.Creature, 79m).GetAwaiter().GetResult();
        var after = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(owner.Creature.CurrentHp == 79 && !before.Matches(after),
            "real native HP differences must still invalidate future state after scoring metadata separation");
    }
    private static void RouteNativeHistory()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        using var native = new NativeFactsState(combat);
        var before = KernelSession.Capture(combat);
        MegaCrit.Sts2.Core.Commands.CreatureCmd.Damage(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
            owner.Creature, 3m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unblockable | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered,
            null, null).GetAwaiter().GetResult();
        MegaCrit.Sts2.Core.Commands.CreatureCmd.Heal(owner.Creature, 3m, playAnim: false).GetAwaiter().GetResult();
        var after = KernelSession.Capture(combat);
        Check(owner.Creature.CurrentHp == 80 && before.PartyStateText() == after.PartyStateText()
            && !PredictedCombat(before).HasLostHpThisTurn(owner.Creature)
            && PredictedCombat(after).HasLostHpThisTurn(owner.Creature)
            && !before.PlanningState([owner.NetId]).Matches(after.PlanningState([owner.NetId])),
            "real native damage history must still invalidate equal-HP future state after scoring metadata separation");
    }
    private static void HistoryCourseNative()
    {
        foreach (var remove in new[] { false, true })
        {
            var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
            using var native = new NativeFactsState(combat);
            var relic = ModelDb.Relic<HistoryCourse>().ToMutable(); owner.AddRelicInternal(relic);
            var card = owner.PlayerCombatState!.Hand.Cards[0]; card.ExhaustOnNextPlay = true;
            NativePaidCard(card, enemy);
            Check(owner.PlayerCombatState.ExhaustPile.Cards.Contains(card), "native paid attack actually exhausts");
            if (remove) MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(card, skipVisuals: true).GetAwaiter().GetResult();
            var root = KernelSession.Capture(combat);
            var current = root.PlanningHistoryCourseAttacks().Single().Current;
            Check(current is not null && ReferenceEquals(current.Original, card)
                && current.Preview.DynamicVars.Damage.BaseValue == 6
                && root.PlanningState([owner.NetId]).Reusable,
                "captured finished native attack must retain its physical identity and frozen fields even after exhaust or removal");
            var frozen = root.PlanningState([owner.NetId]);
            card.DynamicVars.Damage.BaseValue += 3;
            var changedHistory = KernelSession.Capture(combat);
            var changedEvidence = changedHistory.PlanningState([owner.NetId]);
            Console.WriteLine($"Native HistoryCourse frozen remove={remove} old={current!.Preview.DynamicVars.Damage.BaseValue} "
                + $"new={changedHistory.PlanningHistoryCourseAttacks().Single().Current?.Preview.DynamicVars.Damage.BaseValue} "
                + $"mismatch={frozen.FirstMismatch(changedEvidence) ?? "equal"}");
            Check(current!.Preview.DynamicVars.Damage.BaseValue == 6
                && !frozen.Matches(changedEvidence),
                "historical native attack damage must remain a frozen future replay fact and invalidate a changed live source");
            card.DynamicVars.Damage.BaseValue -= 3;
            var branch = root.Fork();
            var state = PredictedCombat(branch);
            state.CommitHistoryCourseTurn(owner); state.AdvancePlayerTurn(owner);
            owner.PlayerCombatState.IncrementTurnNumber();
            relic.AfterAutoPrePlayPhaseEntered(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), owner)
                .GetAwaiter().GetResult();
            var choices = TurnStartChoiceCursor.ForAutomaticPolicy(_ => null);
            state.BeginActionChoices(choices);
            try
            {
                using var dispatch = branch.Simulator.BeginExecutionDispatch();
                state.TriggerScheduledAutoPlays(branch.Simulator, owner, 2, choices, new HashSet<uint>());
            }
            finally { state.EndActionChoices(); }
            Console.WriteLine($"Native HistoryCourse remove={remove} nativeHp={enemy.CurrentHp} simulatedHp={branch.Hp(enemy)}");
            Check(enemy.CurrentHp == 88 && branch.Hp(enemy) == 88,
                "a recaptured paid native attack must drive the same next-turn HistoryCourse replay after exhaust or removal");
        }
    }
    private static void VitalSparkNative()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board(cards: false);
        using var native = new NativeFactsState(combat);
        var card = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(owner);
        owner.PlayerCombatState!.Hand.AddInternal(card);
        var power = ModelDb.Power<VitalSparkPower>().ToMutable(); power.ApplyInternal(enemy, 2, true);
        power.BeforeCombatStart().GetAwaiter().GetResult();
        Check(card.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Tainted { Amount: 2 }, "native initial VitalSpark affliction is preserved");
        var root = KernelSession.Capture(combat); var branch = root.Fork();
        PredictedCombat(branch).Apply<VitalSparkPower>(enemy, 1);
        PowerLifecycleSupport.ResolvePowerAmountChanges(branch.Simulator, PredictedCombat(branch));
        PredictedCombat(branch).NormalizeCardAfflictions(branch.Simulator);
        MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VitalSparkPower>(
            new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), enemy, 1m, null, null).GetAwaiter().GetResult();
        var preview = branch.Simulator.State.FindCard(card)!.Preview;
        Console.WriteLine($"Native VitalSpark first change: native={card.Affliction?.Amount} simulated={preview.Affliction?.Amount}");
        Check(card.Affliction?.Amount == 3 && preview.Affliction?.Amount == 3
            && root.Simulator.State.FindCard(card)!.Preview.Affliction?.Amount == 2,
            "the first captured VitalSpark amount change must normalize native Tainted identically without mutating its root");
    }
    private static void SmoggySkillNative()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board(cards: false);
        using var native = new NativeFactsState(combat);
        var card = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(owner);
        owner.PlayerCombatState!.Hand.AddInternal(card);
        var smog = ModelDb.Power<SmoggyPower>().ToMutable(); smog.ApplyInternal(owner.Creature, 1, true);
        NativePaidCard(card, null);
        var root = KernelSession.Capture(combat); var branch = root.Fork();
        var generated = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(owner);
        MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(generated, MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand).GetAwaiter().GetResult();
        branch.Simulator.AddToCombat<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(owner.Creature, MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, 1, owner, MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom);
        var preview = branch.Simulator.State.GetPlayerCombatState(owner).Hand.Cards.Last().Preview;
        Console.WriteLine($"Native Smoggy post-paid-skill: native={generated.Affliction?.GetType().Name} simulated={preview.Affliction?.GetType().Name}");
        Check(generated.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Smog
            && preview.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Smog,
            "a generated skill after a captured paid native skill must receive the same Smog affliction");
    }
    private static void RoutePotionNative()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        using var native = new NativeFactsState(combat);
        var potion = ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.StrengthPotion>().ToMutable();
        owner.AddPotionInternal(potion, silent: true);
        var root = KernelSession.Capture(combat); var before = root.PlanningState([owner.NetId]);
        var branch = root.Fork();
        Check(branch.UsePotion(potion, owner.Creature, out var reason), "native potion mirror must settle: " + reason);
        potion.OnUseWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), owner.Creature)
            .GetAwaiter().GetResult();
        var after = KernelSession.Capture(combat);
        Check(owner.Creature.GetPower<StrengthPower>()?.Amount == 2 && branch.Power<StrengthPower>(owner.Creature) == 2
            && !owner.Potions.Any() && PredictedCombat(branch).PotionUses.Count == 1
            && PredictedCombat(after).PotionUses.Count == 0
            && branch.PlanningState([owner.NetId]).Matches(after.PlanningState([owner.NetId])),
            "native potion inventory and power state must match while preserving different route receipt origins");
        Check(!before.Matches(after.PlanningState([owner.NetId])),
            "real native potion inventory consumption must remain part of future-state evidence");
    }

    private static void RouteNativeAssets()
    {
        foreach (var axis in new[] { "max-hp", "gold", "relic-used" })
        {
            var (combat, owner, _) = KernelPlanningStateScenarios.Board();
            using var native = new NativeFactsState(combat);
            var tail = (LizardTail)ModelDb.Relic<LizardTail>().ToMutable(); owner.AddRelicInternal(tail);
            var oldGold = owner.Gold;
            var before = KernelSession.Capture(combat).PlanningState([owner.NetId]);
            if (axis == "max-hp")
                MegaCrit.Sts2.Core.Commands.CreatureCmd.SetMaxHp(owner.Creature, 85m).GetAwaiter().GetResult();
            else if (axis == "gold")
                MegaCrit.Sts2.Core.Commands.PlayerCmd.GainGold(7m, owner).GetAwaiter().GetResult();
            else
            {
                Check(!tail.ShouldDieLate(owner.Creature), "native unused LizardTail prevents its next death");
                tail.AfterPreventingDeath(owner.Creature).GetAwaiter().GetResult();
                Check(tail.WasUsed && tail.ShouldDieLate(owner.Creature), "native used LizardTail cannot prevent the next death");
            }
            var after = KernelSession.Capture(combat).PlanningState([owner.NetId]);
            Check(owner.Creature.CurrentHp == 80
                && (axis != "max-hp" || owner.Creature.MaxHp == 85)
                && (axis != "gold" || owner.Gold == oldGold + 7)
                && !before.Matches(after),
                "real native " + axis + " must invalidate equal-HP future state after scoring metadata separation");
        }
    }


    private static void PowerOriginAxebot()
    {
        var (combat, owner, oldMock) = KernelPlanningStateScenarios.Board();
        combat.RemoveCreature(oldMock);
        var model = (MegaCrit.Sts2.Core.Models.Monsters.Axebot)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Axebot>().ToMutable();
        // Leave the native initial StockAmount override unset: HAMMER_UPPERCUT.
        var old = combat.CreateCreature(model, CombatSide.Enemy, "enemy");
        combat.AddCreature(old); model.SetUpForCombat();
        var stock = ModelDb.Power<StockPower>().ToMutable(); stock.ApplyInternal(old, 2, true);
        var weak = ModelDb.Power<WeakPower>().ToMutable(); weak.ApplyInternal(old, 1, true);
        var vulnerable = ModelDb.Power<VulnerablePower>().ToMutable(); vulnerable.ApplyInternal(old, 1, true);
        old.SetMaxHpInternal(6); old.SetCurrentHpInternal(6);
        model.RollMove([owner.Creature]);
        using var native = new NativeFactsState(combat);
        var root = KernelSession.Capture(combat); var branch = root.Fork();
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        Check(branch.Play(card, old, out var reason), "paid Axebot death mirror must settle: " + reason);
        NativePaidCard(card, old);
        var next = combat.Enemies.Single(); var simulated = branch.Enemies.Single();
        var fresh = KernelSession.Capture(combat);
        Console.WriteLine($"Native Stock replacement: old={old.CombatId} new={next.CombatId}/{simulated.CombatId} "
            + $"nativeHp={next.CurrentHp} simulatedHp={branch.Hp(simulated)} stock={next.GetPower<StockPower>()?.Amount}/{branch.Power<StockPower>(simulated)} "
            + $"move={next.Monster!.NextMove.Id} mismatch={branch.PlanningState([owner.NetId]).FirstMismatch(fresh.PlanningState([owner.NetId])) ?? "equal"}");
        Check(!combat.ContainsCreature(old) && old.IsDead && old.Powers.Count == 0
            && next.Monster is MegaCrit.Sts2.Core.Models.Monsters.Axebot { StockAmount: 1 }
            && next.CombatId == simulated.CombatId && next.CombatId != old.CombatId
            && next.CurrentHp == branch.Hp(simulated) && next.GetPower<StockPower>()?.Amount == 1
            && branch.Power<StockPower>(simulated) == 1 && next.Monster.NextMove.Id == "BOOT_UP_MOVE",
            "paid native lethal Strike must remove the old Axebot and create the same new native stock stage");
        Check(branch.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId])),
            "settled native Stock death replacement must match effective future state across root origins");
        var simState = PredictedCombat(branch);
        Check(MonsterMoveEffects.Apply(branch.Simulator, simState, simState.CurrentMonsterMove(simulated), owner.Creature, out _),
            "replacement Axebot BOOT_UP mirror must settle");
        PowerLifecycleSupport.ResolvePowerAmountChanges(branch.Simulator, simState);
        next.Monster.NextMove.PerformMove([owner.Creature]).GetAwaiter().GetResult();
        Console.WriteLine($"Native Stock next BOOT_UP: block={next.Block}/{branch.Block(simulated)} strength={next.GetPower<StrengthPower>()?.Amount}/{branch.Power<StrengthPower>(simulated)}");
        Check(next.Block == branch.Block(simulated) && next.Block == 10
            && next.GetPower<StrengthPower>()?.Amount == 3 && branch.Power<StrengthPower>(simulated) == 3
            && branch.PlanningState([owner.NetId]).Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "native replacement BOOT_UP must retain identical future block, Strength and ordered captured state");
        var second = owner.PlayerCombatState.Hand.Cards[0];
        Check(branch.Play(second, simulated, out reason), "second-stage paid Strike mirror settles: " + reason);
        NativePaidCard(second, next);
        Check(next.Block == 4 && branch.Block(simulated) == 4 && next.CurrentHp == branch.Hp(simulated)
            && branch.PlanningState([owner.NetId]).Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "native paid next-stage Strike must keep the completed old owner irrelevant while matching future state");

    }
    private static void PowerOriginReapply()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        using var native = new NativeFactsState(combat);
        var weak = ModelDb.Power<WeakPower>().ToMutable(); weak.ApplyInternal(owner.Creature, 1, true);
        var strength = ModelDb.Power<StrengthPower>().ToMutable(); strength.ApplyInternal(owner.Creature, 2, true);
        var root = KernelSession.Capture(combat); var branch = root.Fork();
        var state = PredictedCombat(branch); state.SetAmount<WeakPower>(owner.Creature, 0);
        MegaCrit.Sts2.Core.Commands.PowerCmd.Remove(weak).GetAwaiter().GetResult();
        PowerLifecycleSupport.ResolvePowerAmountChanges(branch.Simulator, state);
        state.Apply<WeakPower>(owner.Creature, 1);
        Check(!branch.PlanningState([owner.NetId]).Reusable,
            "unsettled native power callbacks must prevent reuse while root origin encoding is canonicalized");
        PowerLifecycleSupport.ResolvePowerAmountChanges(branch.Simulator, state);
        MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<WeakPower>(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
            owner.Creature, 1m, null, null).GetAwaiter().GetResult();
        var fresh = KernelSession.Capture(combat);
        var nativeOrder = owner.Creature.Powers.Select(p => p.GetType().Name).ToArray();
        var simOrder = state.EffectivePowers().Where(p => p.Owner == owner.Creature).Select(p => p.GetType().Name).ToArray();
        Console.WriteLine($"Native power reapply: nativeOrder={string.Join(',', nativeOrder)} simulatedOrder={string.Join(',', simOrder)} "
            + $"mismatch={branch.PlanningState([owner.NetId]).FirstMismatch(fresh.PlanningState([owner.NetId])) ?? "equal"}");
        Check(nativeOrder.SequenceEqual(new[] { "StrengthPower", "WeakPower" }) && nativeOrder.SequenceEqual(simOrder)
            && !ReferenceEquals(owner.Creature.GetPower<WeakPower>(), weak),
            "native removed power must rejoin at its new acquisition hook position as a fresh instance");
        Check(branch.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId])),
            "native remove and reapply must match effective ordered powers across root origins");
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        Check(branch.Play(card, enemy, out var reason), "reapplied weak mirror paid Strike settles: " + reason);
        NativePaidCard(card, enemy);
        Check(enemy.CurrentHp == 94 && branch.Hp(enemy) == 94
            && branch.PlanningState([owner.NetId]).Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "reacquired native Weak and Strength must drive the same next paid damage and complete future state");
    }


    private static void PowerOriginHidden()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        using var native = new NativeFactsState(combat);
        var juggling = ModelDb.Power<JugglingPower>().ToMutable(); juggling.ApplyInternal(owner.Creature, 1, true);
        SetNativeAttacks(juggling, 7);
        var root = KernelSession.Capture(combat); var original = root.PlanningState([owner.NetId]);
        var branch = root.Fork(); var state = PredictedCombat(branch);
        state.SetAmount<JugglingPower>(owner.Creature, 0);
        MegaCrit.Sts2.Core.Commands.PowerCmd.Remove(juggling).GetAwaiter().GetResult();
        state.Apply<JugglingPower>(owner.Creature, 1);
        state.InitializeJugglingAfterApplied(branch.Simulator, owner.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(branch.Simulator, state);
        MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<JugglingPower>(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
            owner.Creature, 1m, null, null).GetAwaiter().GetResult();
        var replacement = owner.Creature.GetPower<JugglingPower>()!;
        var fresh = KernelSession.Capture(combat);
        Console.WriteLine($"Native hidden replacement: counter={NativeAttacks(replacement)} originalCounter={NativeAttacks(juggling)} "
            + $"mismatch={branch.PlanningState([owner.NetId]).FirstMismatch(fresh.PlanningState([owner.NetId])) ?? "equal"}");
        Check(!ReferenceEquals(replacement, juggling) && NativeAttacks(replacement) == 0
            && NativeAttacks(juggling) == 7 && root.PlanningState([owner.NetId]).Matches(original),
            "native removed Juggling must initialize a fresh payload without mutating its captured parent");
        Check(branch.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId])),
            "retired native power payloads must not conceal the complete fresh effective hidden state");
        var unknown = branch.Fork();
        var stored = (IDictionary)Field(unknown.Simulator.StateStore, "_states")!;
        var retiredModel = stored.Keys.Cast<ValueTuple<AbstractModel, Type>>()
            .First(key => key.Item1 is JugglingPower { Amount: 0 }).Item1;
        unknown.Simulator.StateStore.Get(retiredModel, () => new UnqualifiedState());
        Check(!unknown.PlanningState([owner.NetId]).Reusable,
            "unknown retired-instance payloads must not inherit an audited power-state retirement exemption");
        var settled = branch.PlanningState([owner.NetId]);
        SetNativeAttacks(replacement, 2);
        var changed = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(!settled.Matches(changed),
            "real reacquired native hidden counter differences must still reject equal visible power roots");
        SetNativeAttacks(replacement, 0);
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        Check(branch.Play(card, enemy, out var reason), "fresh Juggling paid attack mirror settles: " + reason);
        NativePaidCard(card, enemy);
        Check(NativeAttacks(replacement) == 1 && owner.PlayerCombatState.Hand.Cards.Count == 1
            && branch.Hand(owner).Count == 1 && enemy.CurrentHp == 94 && branch.Hp(enemy) == 94
            && branch.PlanningState([owner.NetId]).Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "fresh native Juggling payload must drive the same next paid attack rather than a retired counter");
    }
    private static void PowerOriginOrder()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        using var native = new NativeFactsState(combat);
        var strength = ModelDb.Power<StrengthPower>().ToMutable(); strength.ApplyInternal(owner.Creature, 2, true);
        var weak = ModelDb.Power<WeakPower>().ToMutable(); weak.ApplyInternal(owner.Creature, 1, true);
        var before = KernelSession.Capture(combat);
        MegaCrit.Sts2.Core.Commands.PowerCmd.Remove(strength).GetAwaiter().GetResult();
        MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<StrengthPower>(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
            owner.Creature, 2m, null, null).GetAwaiter().GetResult();
        var after = KernelSession.Capture(combat);
        Check(owner.Creature.Powers.Select(p => p.GetType().Name).SequenceEqual(new[] { "WeakPower", "StrengthPower" })
            && before.PartyStateText() == after.PartyStateText()
            && !before.PlanningState([owner.NetId]).Matches(after.PlanningState([owner.NetId])),
            "real native acquisition hook order differences must still invalidate equal visible power roots");
    }
    private static void PowerOriginRevive()
    {
        var (combat, owner, oldMock) = KernelPlanningStateScenarios.Board(); combat.RemoveCreature(oldMock);
        var model = (MegaCrit.Sts2.Core.Models.Monsters.TestSubject)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TestSubject>().ToMutable();
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "enemy"); combat.AddCreature(enemy); model.SetUpForCombat();
        var adaptable = ModelDb.Power<AdaptablePower>().ToMutable(); adaptable.ApplyInternal(enemy, 1, true);
        enemy.SetMaxHpInternal(6); enemy.SetCurrentHpInternal(6); model.RollMove([owner.Creature]);
        using var native = new NativeFactsState(combat);
        var root = KernelSession.Capture(combat); var branch = root.Fork();
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        Check(branch.Play(card, enemy, out var reason), "paid native Adaptable mirror must settle: " + reason);
        NativePaidCard(card, enemy);
        var fresh = KernelSession.Capture(combat);
        Console.WriteLine($"Native retained revive: hp={enemy.CurrentHp}/{branch.Hp(enemy)} move={model.NextMove.Id} "
            + $"mismatch={branch.PlanningState([owner.NetId]).FirstMismatch(fresh.PlanningState([owner.NetId])) ?? "equal"}");

        Check(combat.ContainsCreature(enemy) && enemy.IsDead && !enemy.IsHittable && model.NextMove.Id == "RESPAWN_MOVE"
            && branch.PlanningState([owner.NetId]).Semantics == fresh.PlanningState([owner.NetId]).Semantics
            && !branch.PlanningState([owner.NetId]).Reusable && !fresh.PlanningState([owner.NetId]).Reusable
            && branch.PlanningState([owner.NetId]).Semantics.Contains("unqualified-internal-power-state:AdaptablePower", StringComparison.Ordinal),
            "native retained reviving enemy must keep its complete future lifecycle and existing unknown-payload refusal");
        var changed = branch.Fork();
        var setter = AccessTools.Method(typeof(SimulatedCombatState), "SetDeathPhase");
        setter.Invoke(PredictedCombat(changed), [enemy, Enum.Parse(setter.GetParameters()[1].ParameterType, "PermanentlyDead")]);
        Check(branch.PlanningState([owner.NetId]).Semantics != changed.PlanningState([owner.NetId]).Semantics,
            "reviving and permanently dead native lifecycle meanings must remain distinct");
        var state = PredictedCombat(branch); state.ResolveReviveMove(branch.Simulator, enemy, "RESPAWN_MOVE");
        model.NextMove.PerformMove([owner.Creature]).GetAwaiter().GetResult();
        Check(enemy.CurrentHp > 0 && enemy.CurrentHp == branch.Hp(enemy) && enemy.IsHittable
            && ((ICombatPredictionCreatureSemantics)state).IsHittable(enemy),
            "retained native revive lifecycle must drive the same next form and hittability");
    }

    private static void PowerOriginAi()
    {
        var (combat, owner, oldMock) = KernelPlanningStateScenarios.Board(); combat.RemoveCreature(oldMock);
        var model = (MegaCrit.Sts2.Core.Models.Monsters.Axebot)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Axebot>().ToMutable();
        model.StockAmount = 1;
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "enemy"); combat.AddCreature(enemy); model.SetUpForCombat();
        var stock = ModelDb.Power<StockPower>().ToMutable(); stock.ApplyInternal(enemy, 1, true);
        model.RollMove([owner.Creature]);
        using var native = new NativeFactsState(combat);
        var first = KernelSession.Capture(combat); var frozen = first.PlanningState([owner.NetId]);
        model.StockAmount = 0;
        var changed = KernelSession.Capture(combat);
        Check(first.PartyStateText() == changed.PartyStateText()
            && !frozen.Matches(changed.PlanningState([owner.NetId])),
            "real active native AI static state must reject equal visible roots after retired-owner normalization");
        var a = first.Fork(); var b = changed.Fork();
        foreach (var candidate in new[] { a, b })
        {
            var state = PredictedCombat(candidate);
            Check(MonsterMoveEffects.Apply(candidate.Simulator, state, state.CurrentMonsterMove(enemy), owner.Creature, out _),
                "active AI static mirror BOOT_UP settles");
            PowerLifecycleSupport.ResolvePowerAmountChanges(candidate.Simulator, state);
        }
        model.NextMove.PerformMove([owner.Creature]).GetAwaiter().GetResult();
        Console.WriteLine($"Native active AI static BOOT_UP: nativeStr={enemy.GetPower<StrengthPower>()?.Amount} old={a.Power<StrengthPower>(enemy)} new={b.Power<StrengthPower>(enemy)}");
        Check(a.Power<StrengthPower>(enemy) == 3 && b.Power<StrengthPower>(enemy) == 6
            && enemy.GetPower<StrengthPower>()?.Amount == 6,
            "different active native AI static state must produce the witnessed different next native move");
    }

    private static void PowerOriginIntent()
    {
        var (combat, owner, oldMock) = KernelPlanningStateScenarios.Board(cards: false); combat.RemoveCreature(oldMock);
        var model = (MegaCrit.Sts2.Core.Models.Monsters.Axebot)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Axebot>().ToMutable();
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "enemy"); combat.AddCreature(enemy); model.SetUpForCombat();
        model.RollMove([owner.Creature]); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        var card = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.GoForTheEyes>(owner); owner.PlayerCombatState!.Hand.AddInternal(card);
        using var native = new NativeFactsState(combat);
        var root = KernelSession.Capture(combat); var branch = root.Fork(); var state = PredictedCombat(branch);
        Check(model.IntendsToAttack && state.IsEnemyIntendingToAttack(enemy), "intent witness starts with real attacking Axebot move");
        state.ForceMonsterMove(enemy, "BOOT_UP_MOVE");
        model.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)model.MoveStateMachine!.States["BOOT_UP_MOVE"], true);
        Check(!model.IntendsToAttack && !state.IsEnemyIntendingToAttack(enemy),
            "native forced nonattack move must refresh the effective branch intent consumer before the next card");
        Check(branch.Play(card, enemy, out var reason), "intent-conditioned paid card mirror settles: " + reason);
        var paid = card.SpendResources().GetAwaiter().GetResult();
        card.OnPlayWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), enemy, false,
            new MegaCrit.Sts2.Core.Entities.Cards.ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
        Console.WriteLine($"Native forced-intent card: paid={paid.Item1} hp={enemy.CurrentHp}/{branch.Hp(enemy)} weak={enemy.GetPower<WeakPower>()?.Amount ?? 0}/{branch.Power<WeakPower>(enemy)}");
        Check(paid.Item1 == 0 && enemy.CurrentHp == 97 && branch.Hp(enemy) == 97
            && enemy.GetPower<WeakPower>() is null && branch.Power<WeakPower>(enemy) == 0,
            "native GoForTheEyes after a forced nonattack move must not apply Weak from stale root intent");
    }

    // Actual 2026-10-09 R2: completed-departed-monster-int-projection,
    // forced Rebuild0 / native RED1 / exact source and six-artifact restored
    // Rebuild0 / native GREEN0. state-diagnostics/p0-monster-int-origin-v1/r2/proof.json.
    // System.InvalidOperationException: Planning facts: settled paid Rat death must match effective native monster fields across root origins
    // Native controls retain the active backup limit, next SCREECH/paid Strike,
    // and a removed reviving owner's fields. This is not a Steam win-rate test.
    private static void MonsterIntRetirement()
    {
        var (combat, owner, mock) = KernelPlanningStateScenarios.Board(); combat.RemoveCreature(mock);
        AccessTools.Field(typeof(CombatState), "_encounter").SetValue(combat,
            ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.TwoTailedRatsNormal>().ToMutable());
        var rats = new List<MegaCrit.Sts2.Core.Entities.Creatures.Creature>();
        foreach (var slot in new[] { "fourth", "fifth" })
        {
            var model = (MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat>().ToMutable();
            model.StarterMoveIndex = 2;
            var creature = combat.CreateCreature(model, CombatSide.Enemy, slot); combat.AddCreature(creature);
            model.SetUpForCombat(); model.RollMove([owner.Creature]);
            model.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)model.MoveStateMachine!.States["SCREECH_MOVE"], true);
            creature.SetMaxHpInternal(100); creature.SetCurrentHpInternal(100); rats.Add(creature);
        }
        var old = rats[0]; var survivor = rats[1]; old.SetMaxHpInternal(6); old.SetCurrentHpInternal(6);
        using var native = new NativeFactsState(combat);
        var root = KernelSession.Capture(combat); var branch = root.Fork(); var state = PredictedCombat(branch);
        var first = owner.PlayerCombatState!.Hand.Cards[0];
        Check(branch.Play(first, old, out var reason), "paid Rat removal mirror settles: " + reason);
        NativePaidCard(first, old);
        var fresh = KernelSession.Capture(combat); var freshState = PredictedCombat(fresh);
        Console.WriteLine($"Native Rat paid removal: old={old.CombatId} survivor={survivor.CombatId} oldContained={combat.ContainsCreature(old)} oldHp={old.CurrentHp} oldPowers={old.Powers.Count} oldCount={state.GetMonsterInt(old, "_callForBackupCount")} survivorCount={state.GetMonsterInt(survivor, "_callForBackupCount")}/{freshState.GetMonsterInt(survivor, "_callForBackupCount")} mismatch={branch.PlanningState([owner.NetId]).FirstMismatch(fresh.PlanningState([owner.NetId])) ?? "equal"}");
        Check(!combat.ContainsCreature(old) && old.IsDead && old.Powers.Count == 0 && combat.Enemies.Single() == survivor,
            "paid native Strike permanently removes one original Rat without summoning or replacing its surviving teammate");
        Check(branch.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId])),
            "settled paid Rat death must match effective native monster fields across root origins");
        Check(MonsterMoveEffects.Apply(branch.Simulator, state, state.CurrentMonsterMove(survivor), owner.Creature, out _),
            "surviving Rat SCREECH mirror settles");
        PowerLifecycleSupport.ResolvePowerAmountChanges(branch.Simulator, state);
        survivor.Monster!.NextMove.PerformMove([owner.Creature]).GetAwaiter().GetResult();
        fresh = KernelSession.Capture(combat); freshState = PredictedCombat(fresh);
        Console.WriteLine($"Native Rat next SCREECH: frail={owner.Creature.GetPower<FrailPower>()?.Amount}/{branch.Power<FrailPower>(owner.Creature)} turns={state.GetMonsterInt(survivor, "_turnsUntilSummonable")}/{freshState.GetMonsterInt(survivor, "_turnsUntilSummonable")} count={state.GetMonsterInt(survivor, "_callForBackupCount")}/{freshState.GetMonsterInt(survivor, "_callForBackupCount")}");
        Check(owner.Creature.GetPower<FrailPower>()?.Amount == 1 && branch.Power<FrailPower>(owner.Creature) == 1
            && state.GetMonsterInt(survivor, "_turnsUntilSummonable") == 1
            && freshState.GetMonsterInt(survivor, "_turnsUntilSummonable") == 1
            && branch.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId])),
            "surviving native Rat private countdown must drive the same next SCREECH and captured future state");
        var survivorModel = (MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat)survivor.Monster;
        var countdown = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat), "_turnsUntilSummonable");
        var canSummon = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat), "CanSummon");
        countdown.SetValue(survivorModel, 0);
        Check((bool)canSummon.Invoke(survivorModel, null)!, "native surviving Rat below backup limit can summon in a free encounter slot");
        var beforeNativeChange = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        survivorModel.CallForBackupCount = 3;
        var changed = KernelSession.Capture(combat);
        Check(!beforeNativeChange.Matches(changed.PlanningState([owner.NetId]))
            && !(bool)AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat), "CanSummon").Invoke(survivorModel, null)!,
            "real active native Rat backup limit must retain its private field and reject equal visible roots");
        survivorModel.CallForBackupCount = 0;
        countdown.SetValue(survivorModel, 1);
        Console.WriteLine("Native Rat backup-limit predicate: count0=True count3=False; active private change rejected");
        var retained = branch.Fork();
        var phaseSetter = AccessTools.Method(typeof(SimulatedCombatState), "SetDeathPhase");
        phaseSetter.Invoke(PredictedCombat(retained), [old, Enum.Parse(phaseSetter.GetParameters()[1].ParameterType, "Reviving")]);
        var retainedFacts = retained.PlanningState([owner.NetId]).Semantics;
        Check(retainedFacts.Contains("Item1=Enemy/" + old.CombatId + "/ValueTuple`2.Item2=19:_callForBackupCount", StringComparison.Ordinal),
            "removed reviving Rat must retain its future monster fields rather than inherit permanent-departure eligibility");
        var second = owner.PlayerCombatState.Hand.Cards[0];
        Check(branch.Play(second, survivor, out reason), "next paid surviving Rat Strike mirror settles: " + reason);
        NativePaidCard(second, survivor);
        Check(survivor.CurrentHp == 94 && branch.Hp(survivor) == 94
            && branch.PlanningState([owner.NetId]).Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "next paid native surviving Rat action must preserve complete effective state after old-owner retirement");
    }


    // Actual 2026-10-09 R2 rat-effective-random-weight-projection:
    // forced Rebuild0 / native RED1 / exact source and six-artifact restored
    // Rebuild0 / GREEN0. state-diagnostics/p0-rat-dynamic-weight-v1/r2/proof.json.
    // System.InvalidOperationException: Planning facts: native Rat effective weights after countdown and RollMove must match across capture origins
    // The native selected CALL is witnessed; its spawn action is not executed.
    private static void RatDynamicWeight()
    {
        var (combat, owner, mock) = KernelPlanningStateScenarios.Board(); combat.RemoveCreature(mock);
        AccessTools.Field(typeof(CombatState), "_encounter").SetValue(combat,
            ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.TwoTailedRatsNormal>().ToMutable());
        var model = (MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat>().ToMutable();
        model.StarterMoveIndex = 2;
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "fifth"); combat.AddCreature(enemy);
        model.SetUpForCombat(); model.RollMove([owner.Creature]);
        enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        AccessTools.Field(model.GetType(), "_turnsUntilSummonable").SetValue(model, 1);
        using var native = new NativeFactsState(combat);
        var root = KernelSession.Capture(combat); var original = root.PlanningState([owner.NetId]);
        var branch = root.Fork(); var state = PredictedCombat(branch);
        var random = (MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.RandomBranchState)model.MoveStateMachine!.States["RAND"];
        var scratch = random.States.Single(row => row.stateId == "SCRATCH_MOVE");
        Check(scratch.GetWeight() == 1f, "native Rat root before summon countdown completion uses scratch weight one");
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        Check(branch.Play(card, enemy, out var reason), "paid pre-countdown Rat Strike mirror settles: " + reason);
        NativePaidCard(card, enemy);
        Check(enemy.CurrentHp == 94 && branch.Hp(enemy) == 94
            && branch.PlanningState([owner.NetId]).Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "native paid Rat action before eligibility transition has equivalent complete state");
        Check(MonsterMoveEffects.Apply(branch.Simulator, state, state.CurrentMonsterMove(enemy), owner.Creature, out _),
            "native Rat eligibility-crossing SCREECH mirror settles");
        PowerLifecycleSupport.ResolvePowerAmountChanges(branch.Simulator, state);
        model.PerformMove().GetAwaiter().GetResult();
        state.AdvanceMonsterAi(enemy, branch.Simulator); model.RollMove([owner.Creature]);
        var fresh = KernelSession.Capture(combat);
        Console.WriteLine($"Native Rat dynamic RAND: scratch={scratch.GetWeight()} bits={BitConverter.SingleToInt32Bits(scratch.GetWeight())} turns={state.GetMonsterInt(enemy, "_turnsUntilSummonable")}/{PredictedCombat(fresh).GetMonsterInt(enemy, "_turnsUntilSummonable")} move={state.GetPredictedMoveId(enemy)}/{model.NextMove.Id} hp={branch.Hp(enemy)}/{enemy.CurrentHp} mismatch={branch.PlanningState([owner.NetId]).FirstMismatch(fresh.PlanningState([owner.NetId])) ?? "equal"}");
        Check(scratch.GetWeight() == 1f / 12f && state.GetMonsterInt(enemy, "_turnsUntilSummonable") == 0
            && state.GetPredictedMoveId(enemy) == model.NextMove.Id
            && root.PlanningState([owner.NetId]).Matches(original),
            "native eligibility crossing must change dynamic Rat weights and select the same next move without moving captured parents");
        Check(branch.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId])),
            "native Rat effective weights after countdown and RollMove must match across capture origins");
        var next = owner.PlayerCombatState.Hand.Cards[0];
        Check(branch.Play(next, enemy, out reason), "next paid eligible Rat Strike mirror settles: " + reason);
        NativePaidCard(next, enemy);
        Check(enemy.CurrentHp == 88 && branch.Hp(enemy) == 88
            && branch.PlanningState([owner.NetId]).Matches(KernelSession.Capture(combat).PlanningState([owner.NetId])),
            "next paid native Rat action must retain complete future state after the dynamic weight transition");
        var beforeCounter = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        model.CallForBackupCount = 3;
        var changed = KernelSession.Capture(combat);
        Check(scratch.GetWeight() == 1f && !beforeCounter.Matches(changed.PlanningState([owner.NetId])),
            "real active native Rat backup limit and its future weights must still reject an equal visible board");
    }


    // R2 actual 2026-10-10: withdraw only the dead-seat ready return,
    // forced Rebuild0 -> native RED1 -> exact source and six DLL/PDB
    // restored Rebuild0/GREEN0. Receipt: state-diagnostics/p0-seat-death-v11/
    // r2-ready-return/proof.json. Native first mismatch: seat expected
    // 2/False/False/2/0/0 actual 2/True/False/2/0/0 (offset5000).
    // System.InvalidOperationException: Planning facts: real native dead-seat next player turn must match the complete planning state including readiness
    // Three native controls: shared turn with three paid deaths; Ambergris
    // extra turn retaining an alive nonparticipant; LizardTail's immediate
    // death prevention and healing. No delayed player revival is claimed.
    private static void SeatDeathReady()
    {
        SeatDeathReadyCase(extra: false, preventDeath: false);
        SeatDeathReadyCase(extra: true, preventDeath: false);
        SeatDeathReadyCase(extra: false, preventDeath: true);
    }
    private static void SeatDeathReadyCase(bool extra, bool preventDeath)
    {
        var party = Enumerable.Range(1, 4).Select(id =>
            MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun<MegaCrit.Sts2.Core.Models.Characters.Deprived>(
                MegaCrit.Sts2.Core.Unlocks.UnlockState.all, (ulong)id)).ToArray();
        var combat = new CombatState(runState: MegaCrit.Sts2.Core.Runs.RunState.CreateForTest(party, seed: "SEAT-DEATH-READY")) { RoundNumber = 1 };
        foreach (var seat in party)
        {
            seat.ResetCombatState(); combat.AddPlayer(seat);
            seat.Creature.SetMaxHpInternal(80); seat.Creature.SetCurrentHpInternal(80);
            seat.PlayerCombatState!.Phase = PlayerTurnPhase.Play; seat.PlayerCombatState.GainEnergy(3);
            for (var i = 0; i < 5; i++) seat.PlayerCombatState.DrawPile.AddInternal(
                combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(seat));
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.SludgeSpinner>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)enemy.Monster.MoveStateMachine!.States["RAGE_MOVE"], true);
        using var native = new NativeFactsState(combat);
        using var managers = new NativeReadyManagerScope(combat);
        if (extra)
        {
            var ambergris = ModelDb.Power<AmbergrisPower>().ToMutable();
            ambergris.ApplyInternal(party[0].Creature, 1, true);
        }
        if (preventDeath)
        {
            var tail = (LizardTail)ModelDb.Relic<LizardTail>().ToMutable(); party[1].AddRelicInternal(tail);
            MegaCrit.Sts2.Core.Commands.CreatureCmd.Damage(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
                party[1].Creature, 80m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unblockable | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered,
                null, null).GetAwaiter().GetResult();
            Check(tail.WasUsed && party[1].Creature.CurrentHp == 40 && !party[1].Creature.IsDead
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(party[1]),
                "real native death prevention heals before completion and must not ready the surviving participant");
        }
        var deadSeats = party.Skip(extra || preventDeath ? 2 : 1).ToArray();
        foreach (var dead in deadSeats)
        {
            MegaCrit.Sts2.Core.Commands.CreatureCmd.Damage(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
                dead.Creature, 80m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unblockable | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered,
                null, null).GetAwaiter().GetResult();
            Check(dead.Creature.IsDead && CombatManager.Instance.IsPlayerReadyToEndTurn(dead),
                "actual paid native HP loss and player death hook ready the dead seat during its current player turn");
        }
        var ids = party.Select(p => p.NetId).ToArray();
        var root = KernelSession.Capture(combat); var frozen = root.PlanningState(ids);
        var branch = root.Fork(); var shadow = root.Fork();
        Check(deadSeats.All(p => root.IsReady(p) && !root.CanAct(p)) && root.CanAct(party[0]),
            "native capture retains current dead readiness without waking an alive seat");
        Check(branch.EndTurn(party[0], 2, out var reason), "death-ready normal continuation settles: " + reason);
        if (extra || preventDeath)
            Check(branch.EndTurn(party[1], 2, out reason), "second living seat ends before shared continuation: " + reason);
        managers.Call("EndPlayerTurnPhaseOneInternal", managers.Turn);
        managers.Call("EndPlayerTurnPhaseTwoInternal", managers.Turn);
        managers.Call("SwitchFromPlayerToEnemySide", managers.Turn);
        if (!extra) managers.Call("StartTurn", managers.Turn, null);
        Check(combat.CurrentSide == CombatSide.Player && combat.RoundNumber == (extra ? 1 : 2),
            "real native side switching respects shared versus extra-player turns");
        managers.Call("StartTurn", managers.Turn, null);
        var fresh = KernelSession.Capture(combat);
        Console.WriteLine($"Native dead-seat turn extra={extra} preventDeath={preventDeath}: round={combat.RoundNumber}/{PredictedCombat(branch).RoundNumber} "
            + $"hp={party[0].Creature.CurrentHp}/{branch.Hp(party[0].Creature)} "
            + $"ready={string.Join(',', party.Select(p => CombatManager.Instance.IsPlayerReadyToEndTurn(p)))}/"
            + $"{string.Join(',', party.Select(branch.IsReady))} mismatch={branch.PlanningState(ids).FirstMismatch(fresh.PlanningState(ids)) ?? "equal"}");
        Check(branch.PlanningState(ids).Matches(fresh.PlanningState(ids)),
            "real native dead-seat next player turn must match the complete planning state including readiness");
        Check(deadSeats.All(p => branch.IsReady(p) && !branch.CanAct(p)) && branch.CanAct(party[0])
            && root.PlanningState(ids).Matches(frozen) && shadow.PlanningState(ids).Matches(frozen),
            "dead readiness reset must preserve the captured parent and shadow fork while only the living seat can act");
        if (extra || preventDeath)
            Check(branch.IsReady(party[1]) == extra && branch.CanAct(party[1]) == !extra
                && fresh.IsReady(party[1]) == extra && fresh.CanAct(party[1]) == !extra,
                "alive extra-turn nonparticipants stay ready, but a native healed participant starts awake");
        var card = party[0].PlayerCombatState!.Hand.Cards[0];
        Check(branch.Play(card, enemy, out reason), "living seat can pay a successor after dead-seat setup: " + reason);
        NativePaidCard(card, enemy);
        Check(branch.PlanningState(ids).Matches(KernelSession.Capture(combat).PlanningState(ids)),
            "paid native successor after death-ready setup must retain complete future state");
        var beforeReady = KernelSession.Capture(combat).PlanningState(ids);
        CombatManager.Instance.SetReadyToEndTurn(party[0], canBackOut: false);
        Check(!beforeReady.Matches(KernelSession.Capture(combat).PlanningState(ids)) && branch.CanAct(party[0]),
            "a real alive-seat readiness change must remain strict and cannot contaminate a captured branch");
    }

    // Initialize the actual game's documented headless host test services. Only
    // network transport/presentation are stubbed; death, turn setup, ready marks,
    // hooks, card resources and side switching execute current sts2.dll methods.
    private static void HistoryEnemyWindow()
    {
        var (combat, owner, oldEnemy) = KernelPlanningStateScenarios.Board();
        combat.RemoveCreature(oldEnemy);
        var enemy = combat.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.SludgeSpinner>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)enemy.Monster.MoveStateMachine!.States["RAGE_MOVE"], true);
        using var native = new NativeFactsState(combat);
        using var managers = new NativeReadyManagerScope(combat);
        var card = owner.PlayerCombatState!.Hand.Cards[0]; card.ExhaustOnNextPlay = true;
        NativePaidCard(card, enemy);
        MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(card, skipVisuals: true).GetAwaiter().GetResult();
        var root = KernelSession.Capture(combat); var branch = root.Fork();
        Check(ReferenceEquals(root.PlanningHistoryCourseAttacks().Single().Current?.Original,card),
            "paid native player-side attack begins in its real shared current window");
        Check(branch.EndTurn(owner, 1, out var reason), "native player-to-enemy horizon settles: " + reason + " " + branch.LastRoundFailure);
        managers.Call("EndPlayerTurnPhaseOneInternal",managers.Turn);
        managers.Call("EndPlayerTurnPhaseTwoInternal",managers.Turn);
        managers.Call("SwitchFromPlayerToEnemySide",managers.Turn);
        var fresh = KernelSession.Capture(combat);
        Check(combat.CurrentSide == CombatSide.Enemy && owner.PlayerCombatState.TurnNumber == 1,
            "native side switch changes side without advancing the player's own turn number");
        var predictedSlots = branch.PlanningHistoryCourseAttacks().Single(); var freshSlots = fresh.PlanningHistoryCourseAttacks().Single();
        Console.WriteLine($"Native enemy window current={predictedSlots.Current?.Original.Id.Entry ?? "null"}/{freshSlots.Current?.Original.Id.Entry ?? "null"} previous={predictedSlots.Previous?.Original.Id.Entry ?? "null"}/{freshSlots.Previous?.Original.Id.Entry ?? "null"}");
        Check(predictedSlots.Current is null && freshSlots.Current is null && predictedSlots.Previous is null && freshSlots.Previous is null
            && ReferenceEquals(branch.PlanningHistoryCoursePendingAttacks().Single().Pending?.Original,card)
            && ReferenceEquals(fresh.PlanningHistoryCoursePendingAttacks().Single().Pending?.Original,card),
            "settled enemy horizon must close player-side current without prematurely promoting or losing pending history");
        // Real native auto-play on the enemy side is still a valid current
        // window. The getter must compare origin side, not assume Enemy=null.
        var enemySideCard = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(owner);
        owner.PlayerCombatState.Hand.AddInternal(enemySideCard);
        MegaCrit.Sts2.Core.Commands.CardCmd.AutoPlay(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
            enemySideCard, enemy).GetAwaiter().GetResult();
        var enemyFresh = KernelSession.Capture(combat);
        var nativeCurrent = CombatManager.Instance.History.CardPlaysFinished.LastOrDefault(entry =>
            entry.CardPlay.Player == owner && entry.CardPlay.Card.Type == CardType.Attack && !entry.CardPlay.Card.IsDupe
            && entry.HappenedThisTurn(combat))?.CardPlay.Card;
        Console.WriteLine($"Native enemy-side autoplay current={nativeCurrent?.Id.Entry ?? "null"} captured={enemyFresh.PlanningHistoryCourseAttacks().Single().Current?.Original.Id.Entry ?? "null"}");
        Check(ReferenceEquals(nativeCurrent,enemySideCard) && ReferenceEquals(enemyFresh.PlanningHistoryCourseAttacks().Single().Current?.Original,enemySideCard),
            "real native enemy-side finished auto attack must remain effective current history");
    }

    private static void HistoryDeadDomain() => HistoryTurnDomain(extra: false);
    private static void HistoryExtraDomain() => HistoryTurnDomain(extra: true);
    private static void HistoryTurnDomain(bool extra)
    {
        var party = Enumerable.Range(1, 4).Select(id =>
            MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun<MegaCrit.Sts2.Core.Models.Characters.Deprived>(
                MegaCrit.Sts2.Core.Unlocks.UnlockState.all, (ulong)id)).ToArray();
        var combat = new CombatState(runState: MegaCrit.Sts2.Core.Runs.RunState.CreateForTest(party, seed: "HISTORY-DOMAIN")) { RoundNumber = 1 };
        foreach (var seat in party)
        {
            seat.ResetCombatState(); combat.AddPlayer(seat);
            seat.Creature.SetMaxHpInternal(80); seat.Creature.SetCurrentHpInternal(extra || seat == party[0] ? 80 : 4);
            seat.PlayerCombatState!.Phase = PlayerTurnPhase.Play; seat.PlayerCombatState.GainEnergy(3);
            for (var i = 0; i < 20; i++) seat.PlayerCombatState.DrawPile.AddInternal(
                combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(seat));
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.SludgeSpinner>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)enemy.Monster.MoveStateMachine!.States["RAGE_MOVE"], true);
        using var native = new NativeFactsState(combat);
        using var managers = new NativeReadyManagerScope(combat);
        MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<DexterityPower>(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
            party[0].Creature, 2m, party[0].Creature, null).GetAwaiter().GetResult();
        var attacks = new List<CardModel>();
        foreach (var seat in party)
        {
            var attack = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(seat);
            seat.PlayerCombatState!.Hand.AddInternal(attack); attack.ExhaustOnNextPlay = true;
            NativePaidCard(attack, enemy);
            Check(seat.PlayerCombatState.ExhaustPile.Cards.Contains(attack) && seat.PlayerCombatState.Energy == 2,
                "history domain witness must pay each physical native attack and exhaust it");
            MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(attack, skipVisuals: true).GetAwaiter().GetResult();
            Check(attack.Pile is null, "history domain paid attack is actually removed from all native piles");
            attacks.Add(attack);
        }
        var previousAttacks = attacks.ToArray();
        if (extra)
        {
            AdvanceNativeHistoryTurn(managers, extra: false);
            Check(combat.RoundNumber == 2 && party.All(p => p.PlayerCombatState!.TurnNumber == 2),
                "native extra witness begins after one actual shared enemy turn");
            attacks.Clear();
            foreach (var seat in party)
            {
                var attack = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(seat);
                seat.PlayerCombatState!.Hand.AddInternal(attack); attack.ExhaustOnNextPlay = true;
                NativePaidCard(attack, enemy);
                MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(attack, skipVisuals: true).GetAwaiter().GetResult();
                attacks.Add(attack);
            }
            var ambergris = ModelDb.Power<AmbergrisPower>().ToMutable(); ambergris.ApplyInternal(party[0].Creature, 1, true);
        }
        var ids = party.Select(p => p.NetId).ToArray();
        var root = KernelSession.Capture(combat); var frozen = root.PlanningState(ids); var branch = root.Fork();
        DumpHistoryDomain(extra ? "extra-before" : "dead-before", root, combat, ids);
        foreach (var seat in party) Check(branch.EndTurn(seat, 4, out var reason), "history domain first end settles: " + reason);
        AdvanceNativeHistoryTurn(managers, extra);
        var firstFresh = KernelSession.Capture(combat);
        DumpHistoryPair(extra ? "extra-after" : "dead-after-first", branch, firstFresh, combat, ids);
        if (extra)
        {
            Check(party[0].PlayerCombatState!.TurnNumber == 3 && party.Skip(1).All(p => p.PlayerCombatState!.TurnNumber == 2)
                && party.All(p => !p.Creature.IsDead), "native Ambergris actually advances only its surviving participant");
            Check(root.PlanningState(ids).Matches(frozen), "diagnostic extra transition must not mutate its captured parent");
            CheckSameHistoryFuture(branch, firstFresh, ids,
                "actual native extra turn must retain full HistoryCourse card domain for nonparticipants");
            var nativePrevious = firstFresh.PlanningHistoryCourseAttacks();
            Check(party.Skip(1).All(p => ReferenceEquals(nativePrevious.Single(row => row.Player == p).Previous?.Original,
                previousAttacks[Array.IndexOf(party,p)])) && nativePrevious.All(row => row.Current is null),
                "native nonparticipants preserve their older previous attack while the shared current window closes");
            // Re-capture the real extra entry, rather than carrying only a predicted
            // pending dictionary. B/C/D's R2 paid cards must become R3 previous.
            var extraRoot = firstFresh; var extraFrozen = extraRoot.PlanningState(ids);
            var physical = (CardModel[])AccessTools.Field(typeof(KernelPlanningState), "physicalCards").GetValue(extraFrozen)!;
            Check(physical.Contains(attacks[1]), "native extra pending removed attack must enter the physical identity contract");
            attacks[1].DynamicVars.Damage.BaseValue += 3;
            var changedPending = KernelSession.Capture(combat).PlanningState(ids);
            Check(extraFrozen.Semantics != changedPending.Semantics,
                "changed native pending removed attack must invalidate its frozen future semantic fields");
            attacks[1].DynamicVars.Damage.BaseValue -= 3;
            var successor = extraRoot.Fork();
            Check(successor.EndTurn(party[0], 4, out var successorReason), "native extra successor settles: " + successorReason);
            AdvanceNativeHistoryTurn(managers, extra: false);
            var successorFresh = KernelSession.Capture(combat);
            DumpHistoryPair("extra-next-normal", successor, successorFresh, combat, ids);
            Check(party[0].PlayerCombatState!.TurnNumber == 4 && party.Skip(1).All(p => p.PlayerCombatState!.TurnNumber == 3)
                && party.Skip(1).All(p => ReferenceEquals(successorFresh.PlanningHistoryCourseAttacks().Single(row => row.Player == p).Previous?.Original,
                    attacks[Array.IndexOf(party,p)])),
                "native normal turn after an extra turn promotes each nonparticipant's pending physical attack");
            CheckSameHistoryFuture(successor, successorFresh, ids,
                "fresh native extra entry must retain pending attacks for the later shared normal turn");
            Check(extraRoot.PlanningState(ids).Matches(extraFrozen), "extra successor must not mutate captured pending origins");
            return;
        }
        Check(combat.RoundNumber == 2 && party.Skip(1).All(p => p.Creature.IsDead)
            && party.All(p => p.PlayerCombatState!.TurnNumber == 2), "native enemy attack kills three seats and the normal turn advances every seat");
        // Re-root the ACTUAL R2 state: all four R1 removed attacks are previous.
        // The only living seat performs no attack in R2. On R3 the native
        // HappenedLastPlayerTurn window must contain no R1 cards for any seat.
        root = firstFresh; frozen = root.PlanningState(ids); branch = root.Fork();
        var horizon = root.Fork();
        Check(horizon.EndTurn(party[0], 1, out var horizonReason), "native history horizon settles: " + horizonReason);
        DumpHistoryDomain("dead-enemy-horizon", horizon, combat, ids);
        Check(horizon.EnemyPhaseCompleted && PredictedCombat(horizon).CurrentSide == CombatSide.Enemy
            && horizon.PlanningHistoryCourseAttacks().All(row => row.Current is null),
            "settled enemy horizon must expire the old player-side effective current window");
        Check(branch.EndTurn(party[0], 4, out var secondReason), "history domain second end settles: " + secondReason);
        AdvanceNativeHistoryTurn(managers, extra: false);
        var fresh = KernelSession.Capture(combat);
        DumpHistoryPair("dead-after-second", branch, fresh, combat, ids);
        Check(combat.RoundNumber == 3 && party.All(p => p.PlayerCombatState!.TurnNumber == 3)
            && fresh.PlanningHistoryCourseAttacks().All(row => row.Current is null && row.Previous is null),
            "fresh native R3 history has no attack after the attack-free R2, including dead seats");
        Check(root.PlanningState(ids).Matches(frozen), "diagnostic death transition must not mutate its captured parent");
        CheckSameHistoryFuture(branch, fresh, ids,
            "actual native dead-seat history window must expire removed prior attacks when all player turn numbers advance");
    }
    private static void AdvanceNativeHistoryTurn(NativeReadyManagerScope managers, bool extra)
    {
        managers.Call("EndPlayerTurnPhaseOneInternal", managers.Turn);
        managers.Call("EndPlayerTurnPhaseTwoInternal", managers.Turn);
        managers.Call("SwitchFromPlayerToEnemySide", managers.Turn);
        if (!extra) managers.Call("StartTurn", managers.Turn, null);
        managers.Call("StartTurn", managers.Turn, null);
    }
    private static void CheckSameHistoryFuture(KernelSession predicted, KernelSession fresh, ulong[] ids, string message)
    {
        var a = predicted.PlanningState(ids); var b = fresh.PlanningState(ids);
        Check(a.Semantics == b.Semantics, message);
        var field = AccessTools.Field(typeof(KernelPlanningState), "physicalCards");
        var expected = (CardModel[])field.GetValue(a)!; var actual = (CardModel[])field.GetValue(b)!;
        Check(expected.Length == actual.Length && expected.Zip(actual).All(row => ReferenceEquals(row.First,row.Second)),
            "native historical domain retains every physical card identity");
        if (a.Reusable && b.Reusable) Check(a.Matches(b), "qualified native historical states match all contract axes");
    }
    private static void DumpHistoryPair(string label, KernelSession predicted, KernelSession fresh, CombatState combat, ulong[] ids)
    {
        DumpHistoryDomain(label + "-predicted", predicted, combat, ids);
        DumpHistoryDomain(label + "-fresh", fresh, combat, ids);
        var a = predicted.PlanningState(ids); var b = fresh.PlanningState(ids);
        var offset = 0; while (offset < Math.Min(a.Semantics.Length,b.Semantics.Length) && a.Semantics[offset] == b.Semantics[offset]) offset++;
        var begin = Math.Max(0, offset - 160);
        var rawMismatch = (string)AccessTools.Method(typeof(KernelPlanningState), "FirstSemanticMismatch").Invoke(null,[a.Semantics,b.Semantics])!;
        Console.WriteLine("DOMAIN semantic-only=" + (a.Semantics == b.Semantics ? "equal" : rawMismatch));
        Console.WriteLine($"DOMAIN {label} mismatch={a.FirstMismatch(b) ?? "equal"} offset={offset}");
        Console.WriteLine("DOMAIN predicted fragment=" + a.Semantics.Substring(begin,Math.Min(800,a.Semantics.Length-begin)));
        Console.WriteLine("DOMAIN fresh fragment=" + b.Semantics.Substring(begin,Math.Min(800,b.Semantics.Length-begin)));
    }
    private static void DumpHistoryDomain(string label, KernelSession session, CombatState combat, ulong[] ids)
    {
        var rows = session.PlanningHistoryCourseAttacks();
        if (label.EndsWith("fresh", StringComparison.Ordinal) || label.EndsWith("before", StringComparison.Ordinal))
            foreach (var row in rows)
            {
                var actualCurrent = CombatManager.Instance.History.CardPlaysFinished.LastOrDefault(entry =>
                    entry.CardPlay.Player == row.Player && entry.CardPlay.Card.Type == CardType.Attack && !entry.CardPlay.Card.IsDupe
                    && entry.HappenedThisTurn(combat))?.CardPlay.Card;
                var actualPrevious = CombatManager.Instance.History.CardPlaysFinished.LastOrDefault(entry =>
                    entry.CardPlay.Player == row.Player && entry.CardPlay.Card.Type == CardType.Attack && !entry.CardPlay.Card.IsDupe
                    && entry.HappenedLastPlayerTurn(row.Player))?.CardPlay.Card;
                Check(ReferenceEquals(actualCurrent,row.Current?.Original) && ReferenceEquals(actualPrevious,row.Previous?.Original),
                    "native captured effective history slots must equal the real public history predicates");
            }
        Console.WriteLine($"DOMAIN {label} round={PredictedCombat(session).RoundNumber} nativeRound={combat.RoundNumber} risk={session.LastActionHadEnvironmentalRisk} riskDetail={session.LastActionRiskDetail}");
        foreach (var gap in PredictionCoverage.Collect(Simulator(session)))
            if (!gap.Compensated) Console.WriteLine($"DOMAIN {label} uncompensated={gap}");
        Console.WriteLine($"DOMAIN native MVID={typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry).Module.ModuleVersionId:D}");
        foreach (var enemy in combat.Enemies)
            Console.WriteLine($"DOMAIN native-origin-move={enemy.Monster?.Id.Entry}/{enemy.Monster?.NextMove.Id} supported={MonsterMoveEffects.Supports(enemy.Monster!,enemy.Monster!.NextMove.Id)}");
        foreach (var row in rows)
            Console.WriteLine($"DOMAIN {label} seat={row.Player.NetId} turn={PredictedCombat(session).GetPlayerTurnNumber(row.Player)} hp={session.Hp(row.Player.Creature)} current={row.Current?.Original.Id.Entry ?? "null"} previous={row.Previous?.Original.Id.Entry ?? "null"}");
        var sem = session.PlanningState(ids).Semantics;
        File.WriteAllText("diagnostic-" + label + ".semantics.txt", sem);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sem, @";card-facts=([^{}]+)"))
            Console.WriteLine($"DOMAIN {label} cardAddress={match.Groups[1].Value}");
    }

    private sealed class NativeReadyManagerScope : IDisposable
    {
        private readonly MegaCrit.Sts2.Core.Runs.RunManager run = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
        private readonly Dictionary<FieldInfo, object?> previous;
        internal object Turn => AccessTools.Field(typeof(CombatManager), "_turnState").GetValue(CombatManager.Instance)!;
        internal NativeReadyManagerScope(CombatState combat)
        {
            previous = typeof(MegaCrit.Sts2.Core.Runs.RunManager).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .ToDictionary(f => f, f => f.GetValue(run));
            AccessTools.Property(run.GetType(), "State").SetValue(run, null);
            var host = DispatchProxy.Create<MegaCrit.Sts2.Core.Multiplayer.Game.INetHostGameService, ReconnectNetProxy>();
            run.SetUpTest((MegaCrit.Sts2.Core.Runs.RunState)combat.RunState, host, disableCombatStateSync: true, shouldSave: false);
            Check(!run.IsSingleplayerOrFakeMultiplayer, "native ready witness uses true multiplayer host semantics");
        }
        internal void Call(string name, params object?[] args)
        {
            var method = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == name && m.GetParameters().Length == args.Length);
            if (method.Invoke(CombatManager.Instance, args) is Task task)
                task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            run.ActionExecutor.Pause();
            foreach (var item in previous.Where(row => !ReferenceEquals(row.Value, row.Key.GetValue(run)))
                .Select(row => row.Key.GetValue(run)).OfType<IDisposable>().Distinct()) item.Dispose();
            foreach (var row in previous.Where(row => !ReferenceEquals(row.Value, row.Key.GetValue(run)))) row.Key.SetValue(run, row.Value);
        }
    }

    // Actual 2026-10-11 native Afterimage R2: three distinct private
    // forced Rebuild0 -> named native RED1 -> exact source4/DLL+PDB6
    // restore -> native GREEN0. Evidence work/v12-afterimage-native-20261011-v1/r2/proof.json.
    // 01-empty-root-capture: System.InvalidOperationException: Planning facts: native settled Afterimage empty pairs must qualify and match the full paid successor state
    // 02-nonempty-native-refusal: System.InvalidOperationException: Planning facts: native nonempty Afterimage Before/After data must remain unqualified without reading moving live data into captured parents
    // 03-required-native-payload: System.InvalidOperationException: Planning facts: native settled Afterimage empty pairs must qualify and match the full paid successor state
    // Native normal lifecycle: canonical Data is null; ToMutable initializes
    // a nonnull empty Data before real Apply/paid play. GetInternalData has
    // no lazy-empty path. No null or nonempty native payload is fabricated.
    // Native in-process focused entry: --planning-facts-only --planning-facts-case=afterimage-native.
    // The real paid wrapper supplies Before -> OnPlay -> After, rather than
    // replacing those hooks with a mock or injecting a made-up empty payload.
    private static void AfterimageNative()
    {
        var canonical = ModelDb.Power<AfterimagePower>();
        var detached = (AfterimagePower)canonical.ToMutable();
        Console.WriteLine($"Native Afterimage Data lifecycle: canonicalNull={Field(canonical, "_internalData") is null} "
            + $"mutableNull={Field(detached, "_internalData") is null} mutablePairs={NativeAfterimageAmounts(detached).Count}");
        {
            var (combat, owner, enemy) = KernelPlanningStateScenarios.Board(cards: false);
            var first = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Afterimage>(owner);
            var second = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Afterimage>(owner);
            var strike = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(owner);
            owner.PlayerCombatState!.Hand.AddInternal(first);
            owner.PlayerCombatState.Hand.AddInternal(second);
            owner.PlayerCombatState.Hand.AddInternal(strike);
            using var native = new NativeFactsState(combat);
            var root = KernelSession.Capture(combat); var original = root.PlanningState([owner.NetId]);
            var branch = root.Fork();
            Check(branch.Play(first, null, out var reason), "first paid Afterimage mirror settles: " + reason);
            NativePaidCard(first, null);
            var power = owner.Creature.GetPower<AfterimagePower>()!;
            var fresh = KernelSession.Capture(combat);
            Check(power.Amount == 1 && NativeAfterimageAmounts(power).Count == 0
                && owner.Creature.Block == 0 && branch.Block(owner.Creature) == 0
                && branch.Power<AfterimagePower>(owner.Creature) == 1,
                "native newly applied Afterimage must not fabricate a Before snapshot for its own applying card");
            Check(fresh.PlanningState([owner.NetId]).Reusable
                && branch.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId])),
                "native settled Afterimage empty pairs must qualify and match the full paid successor state");
            Console.WriteLine($"Native Afterimage first paid: amount={power.Amount} block={owner.Creature.Block}/{branch.Block(owner.Creature)} "
                + $"energy={owner.PlayerCombatState.Energy}/{branch.Energy(owner)} pairs={NativeAfterimageAmounts(power).Count} "
                + $"historyFinished={CombatManager.Instance.History.CardPlaysFinished.Count()} freshReusable={fresh.PlanningState([owner.NetId]).Reusable} fullMatch=True");
            var firstBoundary = branch.PlanningState([owner.NetId]);
            var child = branch.Fork();
            Check(child.PlanningState([owner.NetId]).Matches(firstBoundary),
                "an owned empty Afterimage pair must preserve full facts when forked");
            Check(child.Play(second, null, out reason), "second paid Afterimage mirror settles: " + reason);
            NativePaidCard(second, null);
            fresh = KernelSession.Capture(combat);
            Check(power.Amount == 2 && NativeAfterimageAmounts(power).Count == 0
                && owner.Creature.Block == 1 && child.Block(owner.Creature) == 1
                && child.Power<AfterimagePower>(owner.Creature) == 2
                && child.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId]))
                && branch.PlanningState([owner.NetId]).Matches(firstBoundary),
                "native stacking Afterimage pays only its old Amount snapshot and cannot mutate the fork parent");
            Console.WriteLine($"Native Afterimage second paid: amount={power.Amount} block={owner.Creature.Block}/{child.Block(owner.Creature)} "
                + $"energy={owner.PlayerCombatState.Energy}/{child.Energy(owner)} pairs={NativeAfterimageAmounts(power).Count} "
                + $"historyFinished={CombatManager.Instance.History.CardPlaysFinished.Count()} fullMatch=True parentMatch=True");
            Check(child.Play(strike, enemy, out reason), "paid post-stack Strike mirror settles: " + reason);
            NativePaidCard(strike, enemy);
            fresh = KernelSession.Capture(combat);
            Check(enemy.CurrentHp == 94 && child.Hp(enemy) == 94
                && owner.Creature.Block == 3 && child.Block(owner.Creature) == 3
                && child.Energy(owner) == owner.PlayerCombatState.Energy
                && NativeAfterimageAmounts(power).Count == 0
                && child.PlanningState([owner.NetId]).Matches(fresh.PlanningState([owner.NetId]))
                && root.PlanningState([owner.NetId]).Matches(original),
                "the next paid native card must consume the new Amount while preserving complete facts and frozen parents");
            Console.WriteLine($"Native Afterimage Strike paid: enemyHp={enemy.CurrentHp}/{child.Hp(enemy)} ownerHp={owner.Creature.CurrentHp}/{child.Hp(owner.Creature)} "
                + $"block={owner.Creature.Block}/{child.Block(owner.Creature)} energy={owner.PlayerCombatState.Energy}/{child.Energy(owner)} "
                + $"pairs={NativeAfterimageAmounts(power).Count} historyFinished={CombatManager.Instance.History.CardPlaysFinished.Count()} "
                + $"sharedShuffleCounter={Field(combat.RunState.Rng.Shuffle, "_counter")} fullFactsPhysicalRngMatch=True parentsMatch=True");
        }
        {
            var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
            using var native = new NativeFactsState(combat);
            var context = new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext();
            var power = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<AfterimagePower>(context,
                owner.Creature, 1m, owner.Creature, null).GetAwaiter().GetResult()!;
            var root = KernelSession.Capture(combat); var frozen = root.PlanningState([owner.NetId]);
            Check(frozen.Reusable && NativeAfterimageAmounts(power).Count == 0,
                "native actually empty Afterimage root must carry owned qualification evidence");
            var card = owner.PlayerCombatState!.Hand.Cards[0];
            var paid = card.SpendResources().GetAwaiter().GetResult();
            Check(paid.Item1 == 1, "pending native hook control pays the real card cost");
            var play = new CardPlay { Card = card, Player = owner, Target = enemy, ResultPile = PileType.Discard,
                Resources = new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                    StarsSpent = paid.Item2, StarValue = paid.Item2 },
                IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            // This is an explicit native hook-boundary control, not a full
            // OnPlayWrapper action or a claim about live queue quiescence.
            power.BeforeCardPlayed(play).GetAwaiter().GetResult();
            var pending = KernelSession.Capture(combat).PlanningState([owner.NetId]);
            Check(NativeAfterimageAmounts(power).Count == 1 && !pending.Reusable
                && pending.Semantics.Contains("missing-captured-power-state:AfterimagePower", StringComparison.Ordinal)
                && root.PlanningState([owner.NetId]).Matches(frozen),
                "native nonempty Afterimage Before/After data must remain unqualified without reading moving live data into captured parents");
            Console.WriteLine($"Native Afterimage genuine Before: pairs={NativeAfterimageAmounts(power).Count} reusable={pending.Reusable} "
                + $"energyPaid={paid.Item1} capturedParentMatch=True missingCapturedPower=True");
            power.AfterCardPlayed(context, play).GetAwaiter().GetResult();
            Check(NativeAfterimageAmounts(power).Count == 0 && owner.Creature.Block == 1
                && KernelSession.Capture(combat).PlanningState([owner.NetId]).Reusable,
                "native After must consume its real pending pair before empty-pair qualification can return");
            Console.WriteLine($"Native Afterimage genuine After: pairs={NativeAfterimageAmounts(power).Count} block={owner.Creature.Block} "
                + $"reusable={KernelSession.Capture(combat).PlanningState([owner.NetId]).Reusable}");
        }
    }

    private static IDictionary NativeAfterimageAmounts(AfterimagePower power)
    {
        var data = AccessTools.Method(typeof(PowerModel), "GetInternalData")
            .MakeGenericMethod(AccessTools.Inner(typeof(AfterimagePower), "Data")).Invoke(power, null)!;
        return (IDictionary)Field(data, "amountsForPlayedCards")!;
    }

    private static CombatPredictionSimulator Simulator(KernelSession session)
        => (CombatPredictionSimulator)AccessTools.Field(typeof(KernelSession), "simulator").GetValue(session)!;
    private static RelicModel PredictedRelic(KernelSession session, MegaCrit.Sts2.Core.Entities.Players.Player owner)
    {
        var combat = AccessTools.Property(typeof(KernelSession), "Combat").GetValue(session)!;
        return ((IReadOnlyList<RelicModel>)AccessTools.Method(combat.GetType(), "RelicsOf").Invoke(combat, [owner])!)[0];
    }
    private static bool CapturedRelicFlag(KernelSession session, RelicModel relic)
    {
        var states = (IDictionary)Field(Simulator(session).StateStore, "_states")!;
        foreach (DictionaryEntry entry in states)
        {
            var model = entry.Key.GetType().GetField("Item1")!.GetValue(entry.Key);
            if (ReferenceEquals(model, relic) && AccessTools.Property(entry.Value!.GetType(), "ShouldTrigger") is { } flag)
                return (bool)flag.GetValue(entry.Value)!;
        }
        throw new InvalidOperationException("Planning facts: native relic flag was not captured");
    }

    private sealed class UnqualifiedState : IPredictionStateForkable
    { public object Fork(PredictionForkContext context) => new UnqualifiedState(); }
    private static (bool Available, string Text) Facts(KernelSession session)
    { var text = new StringBuilder(); return (session.AppendPlanningMechanismFacts(text), text.ToString()); }
    private static object? Field(object value, string name)
        => AccessTools.Field(value.GetType(), name).GetValue(value);
    private static object NativeJugglingData(PowerModel power)
        => AccessTools.Method(typeof(PowerModel), "GetInternalData")
            .MakeGenericMethod(AccessTools.Inner(typeof(JugglingPower), "Data")).Invoke(power, null)!;
    private static int NativeAttacks(PowerModel power)
        => (int)Field(NativeJugglingData(power), "attacksPlayedThisTurn")!;
    private static void SetNativeAttacks(PowerModel power, int count)
        => AccessTools.Field(NativeJugglingData(power).GetType(), "attacksPlayedThisTurn")
            .SetValue(NativeJugglingData(power), count);
    private static bool Format(ref string __result) { __result = ""; return false; }
    // Actual 2026-10-11 private native R2, three distinct source faults:
    // Rebuild0 -> named RED1 -> exact source2 and DLL/PDB6 restore,
    // Rebuild0 -> named GREEN0. work/v13-f18-ai-drift-native-20261011-v1/r2/proof.json.
    // 01: System.InvalidOperationException: Planning facts: native Bowlbug STUNNED and later HEADBUTT must match complete effective AI facts across capture origins
    // 02: System.InvalidOperationException: Planning facts: native neighbor paid threshold crossing must expose the effective BEETLE conditional and complete future match
    // 03: System.InvalidOperationException: Planning facts: native unknown conditional fallback must retain its captured choice reject a changed selection and preserve its parent
    // Scope: actual native STUNNED entry -> wake is reusable/full-match twice.
    // Fully blocked/unblocked HEADBUTT controls remain unqualified through the
    // existing unsupported-attack risk and skip ledger. Neither is exempted.
    // This is not historical F18 replay, A10 qualification or a win-rate claim.
    private static void BowlbugStunnedConditional()
    {
        var party = Enumerable.Range(1, 4).Select(id =>
            MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun<MegaCrit.Sts2.Core.Models.Characters.Deprived>(
                MegaCrit.Sts2.Core.Unlocks.UnlockState.all, (ulong)id)).ToArray();
        var combat = new CombatState(runState: MegaCrit.Sts2.Core.Runs.RunState.CreateForTest(party, seed: "BOWLBUG-STUNNED-FACTS")) { RoundNumber = 1 };
        foreach (var seat in party)
        {
            seat.ResetCombatState(); combat.AddPlayer(seat);
            seat.Creature.SetMaxHpInternal(200); seat.Creature.SetCurrentHpInternal(200);
            seat.Creature.GainBlockInternal(40);
            seat.PlayerCombatState!.Phase = PlayerTurnPhase.Play; seat.PlayerCombatState.GainEnergy(3);
            seat.PlayerCombatState.Hand.AddInternal(combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(seat));
            for (var i = 0; i < 30; i++) seat.PlayerCombatState.DrawPile.AddInternal(
                combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(seat));
        }
        var model = (MegaCrit.Sts2.Core.Models.Monsters.BowlbugRock)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.BowlbugRock>().ToMutable();
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "bowlbug"); combat.AddCreature(enemy);
        model.SetUpForCombat(); model.RollMove(party.Select(p => p.Creature).ToArray());
        enemy.SetMaxHpInternal(400); enemy.SetCurrentHpInternal(400);
        using var native = new NativeFactsState(combat);
        using var managers = new NativeReadyManagerScope(combat);
        ModelDb.Power<ImbalancedPower>().ToMutable().ApplyInternal(enemy, 1, true);
        var ids = party.Select(p => p.NetId).ToArray();
        Check(model.NextMove.Id == "HEADBUTT_MOVE" && !model.IsOffBalance, "native Bowlbug root starts on a balanced HEADBUTT");
        var root = KernelSession.Capture(combat); var frozen = root.PlanningState(ids);
        var branch = root.Fork(); var shadow = root.Fork();
        var beforeRng = combat.RunState.Rng.MonsterAi.CaptureState();
        for (var phase = 0; phase < 5; phase++)
        {
            if (phase == 3)
            {
                // A fresh real native input: only the nonfirst seat two fully blocks the
                // next actual four-seat HEADBUTT; the other seats lose HP.
                party[1].Creature.GainBlockInternal(40);
                root = KernelSession.Capture(combat); frozen = root.PlanningState(ids);
                branch = root.Fork(); shadow = root.Fork();
            }
            var card = party[0].PlayerCombatState!.Hand.Cards.First(c => c is MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad);
            Check(branch.Play(card, enemy, out var reason), "paid native Bowlbug successor mirror settles: " + reason);
            NativePaidCard(card, enemy);
            Check(branch.PlanningState(ids).Matches(KernelSession.Capture(combat).PlanningState(ids)),
                "paid native Bowlbug action must preserve full history energy HP RNG and physical card facts");
            var phaseHp = party.Select(p => p.Creature.CurrentHp).ToArray();
            foreach (var seat in party)
                Check(branch.EndTurn(seat, combat.RoundNumber + 1, out reason), "four-seat Bowlbug continuation settles: " + reason);
            AdvanceNativeHistoryTurn(managers, extra: false);
            var fresh = KernelSession.Capture(combat); var a = branch.PlanningState(ids); var b = fresh.PlanningState(ids);
            DumpHistoryPair("bowlbug-phase-" + phase, branch, fresh, combat, ids);
            var conditional = (MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.ConditionalBranchState)model.MoveStateMachine!.States["POST_HEADBUTT"];
            Console.WriteLine($"BOWLBUG phase={phase} nativeMove={model.NextMove.Id} predictedMove={PredictedCombat(branch).GetPredictedMoveId(enemy)} offBalance={model.IsOffBalance} capturedConditional={conditional.GetNextState(enemy,new MegaCrit.Sts2.Core.Random.Rng(0))} stateLog={string.Join(',',model.MoveStateMachine.StateLog.Select(s => s.Id))} hp={string.Join(',',party.Select(p => p.Creature.CurrentHp))}/{string.Join(',',party.Select(p => branch.Hp(p.Creature)))} block={string.Join(',',party.Select(p => p.Creature.Block))} energy={string.Join(',',party.Select(p => p.PlayerCombatState!.Energy))} rng={combat.RunState.Rng.MonsterAi.CaptureState()}");
            Check(party.All(p => branch.Hp(p.Creature) == p.Creature.CurrentHp && branch.Block(p.Creature) == p.Creature.Block && branch.Energy(p) == p.PlayerCombatState!.Energy)
                && root.PlanningState(ids).Matches(frozen) && shadow.PlanningState(ids).Matches(frozen),
                "native Bowlbug paid continuation preserves all seats and immutable parent forks");
            Check(combat.RunState.Rng.MonsterAi.CaptureState() == beforeRng,
                "actual native Bowlbug deterministic conditional and STUNNED continuation consume no AI RNG");
            if (phase is 0 or 3)
                Check(model.NextMove.Id == "STUNNED" && model.IsOffBalance,
                    "real fully blocked native HEADBUTT installs STUNNED and sets off balance");
            else
                Check(model.NextMove.Id == "HEADBUTT_MOVE" && !model.IsOffBalance,
                    "real native STUNNED callback resets off balance and its unblocked successor remains HEADBUTT");
            if (phase is 0 or 1 or 4) Check(party.Select(p => p.Creature.CurrentHp).SequenceEqual(phaseHp),
                "native full block and actual STUNNED callback do not lose HP");
            if (phase == 2) Check(party.Select((p,i) => p.Creature.CurrentHp < phaseHp[i]).All(v => v),
                "real unblocked native successor damages every party target");
            if (phase == 3) Check(party[1].Creature.CurrentHp == phaseHp[1] && party.Select((p,i) => i == 1 || p.Creature.CurrentHp < phaseHp[i]).All(v => v),
                "one fully blocked real target stuns Bowlbug while the other native targets take damage");
            if (phase is 0 or 3)
            {
                // Preserve the observed full-block bridge ledger discrepancy:
                // this is an actual native control, not qualified reuse proof.
                // Re-capture the genuinely native STUNNED entry to isolate the
                // live F18 callback-to-HEADBUTT static conditional drift.
                Console.WriteLine("BOWLBUG blocked-control fullFacts=" + (a.FirstMismatch(b) ?? "equal"));
                root = fresh; frozen = root.PlanningState(ids);
                branch = root.Fork(); shadow = root.Fork();
            }
            else if (phase == 2)
            {
                // HEADBUTT is still an unsupported pure attack in the existing
                // Supports table. Its native damage/history control is retained;
                // no qualification is removed and no safe full-plan claim made.
                CheckSameHistoryFuture(branch, fresh, ids,
                    "actual unblocked Bowlbug damage control preserves all native semantic and physical facts");
                Console.WriteLine("BOWLBUG unblocked-control fullFacts=" + (a.FirstMismatch(b) ?? "equal"));
            }
            else Check(a.Reusable && b.Reusable && a.Matches(b),
                "native Bowlbug STUNNED and later HEADBUTT must match complete effective AI facts across capture origins");
        }
        var settled = KernelSession.Capture(combat); var settledFacts = settled.PlanningState(ids);
        enemy.SetCurrentHpInternal(enemy.CurrentHp - 1);
        Check(!settledFacts.Matches(KernelSession.Capture(combat).PlanningState(ids)),
            "real native changed HP remains strict after effective conditional normalization");
        enemy.SetCurrentHpInternal(enemy.CurrentHp + 1);
        combat.RunState.Rng.MonsterAi.NextFloat();
        Check(!settledFacts.Matches(KernelSession.Capture(combat).PlanningState(ids)),
            "real native changed AI RNG remains strict after effective conditional normalization");
    }
    private static void ConditionalNeighborStrict()
    {
        var (combat, owner, mock) = KernelPlanningStateScenarios.Board(); combat.RemoveCreature(mock);
        var model = (MegaCrit.Sts2.Core.Models.Monsters.FrogKnight)ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.FrogKnight>().ToMutable();
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "frog"); combat.AddCreature(enemy);
        model.SetUpForCombat(); model.RollMove([owner.Creature]); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(60);
        using var native = new NativeFactsState(combat);
        var card = owner.PlayerCombatState!.Hand.Cards[0]; card.DynamicVars.Damage.BaseValue = 21;
        var root = KernelSession.Capture(combat); var initial = root.PlanningState([owner.NetId]);
        var conditional = (MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.ConditionalBranchState)model.MoveStateMachine!.States["HALF_HEALTH"];
        var paidBranch = root.Fork();
        Check(paidBranch.Play(card, enemy, out var reason), "native neighbor paid threshold crossing settles: " + reason);
        NativePaidCard(card, enemy);
        var paidFacts = paidBranch.PlanningState([owner.NetId]); var paidFresh = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(enemy.CurrentHp == 39 && paidFacts.Semantics.Contains("11:HALF_HEALTH=13:BEETLE_CHARGE", StringComparison.Ordinal)
            && paidFacts.Reusable && paidFresh.Reusable && paidFacts.Matches(paidFresh) && root.PlanningState([owner.NetId]).Matches(initial),
            "native neighbor paid threshold crossing must expose the effective BEETLE conditional and complete future match");
        enemy.SetCurrentHpInternal(60);
        var before = conditional.GetNextState(enemy,new MegaCrit.Sts2.Core.Random.Rng(0));
        enemy.SetCurrentHpInternal(40);
        var after = conditional.GetNextState(enemy,new MegaCrit.Sts2.Core.Random.Rng(0));
        Check(before == "TONGUE_LASH" && after == "BEETLE_CHARGE" && !initial.Matches(KernelSession.Capture(combat).PlanningState([owner.NetId]))
            && root.PlanningState([owner.NetId]).Matches(initial),
            "native neighbor dynamic branch threshold remains strict and cannot read moving live state into its parent");
        var below = KernelSession.Capture(combat); var belowFacts = below.PlanningState([owner.NetId]);
        AccessTools.Field(model.GetType(), "_hasBeetleCharged").SetValue(model, true);
        var charged = conditional.GetNextState(enemy,new MegaCrit.Sts2.Core.Random.Rng(0));
        Check(enemy.CurrentHp == 40 && charged == "TONGUE_LASH" && !belowFacts.Matches(KernelSession.Capture(combat).PlanningState([owner.NetId]))
            && below.PlanningState([owner.NetId]).Matches(belowFacts),
            "native neighbor real hidden charged predicate must invalidate equal visible roots after effective conditional projection");
        Check(belowFacts.Semantics.Contains("11:HALF_HEALTH=13:BEETLE_CHARGE", StringComparison.Ordinal)
            && KernelSession.Capture(combat).PlanningState([owner.NetId]).Semantics.Contains("11:HALF_HEALTH=11:TONGUE_LASH", StringComparison.Ordinal),
            "native neighbor hidden charged change must update the exact effective conditional selection");
        // An explicit native machine condition outside the resolver's overrides:
        // its captured fallback must stay strict and never re-read live lambdas.
        var unknownSelect = false;
        var unknown = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.ConditionalBranchState("UNKNOWN_FACTS");
        unknown.AddState(model.MoveStateMachine.States["BEETLE_CHARGE"], () => !unknownSelect);
        unknown.AddState(model.MoveStateMachine.States["TONGUE_LASH"], () => unknownSelect);
        model.MoveStateMachine.States.Add(unknown.Id,unknown);
        var unknownRoot = KernelSession.Capture(combat); var unknownBefore = unknownRoot.PlanningState([owner.NetId]);
        unknownSelect = true;
        var unknownAfter = KernelSession.Capture(combat).PlanningState([owner.NetId]);
        Check(unknownBefore.Semantics.Contains("13:UNKNOWN_FACTS=13:BEETLE_CHARGE", StringComparison.Ordinal)
            && unknownAfter.Semantics.Contains("13:UNKNOWN_FACTS=11:TONGUE_LASH", StringComparison.Ordinal)
            && !unknownBefore.Matches(unknownAfter) && unknownRoot.PlanningState([owner.NetId]).Matches(unknownBefore),
            "native unknown conditional fallback must retain its captured choice reject a changed selection and preserve its parent");
    }

    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Planning facts: " + message); }
}
