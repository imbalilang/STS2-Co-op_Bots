using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Constructed native four-seat A10 control, not the archived F31 full state.
// No private counter, selected menu, HP or RNG is changed after initial setup.
// The real Curse executes all four production choices and real OnChosen;
// all three curses are reached through normal native enemy-phase transitions.
// Actual 2026-10-12 native R2: withdraw the production Knowledge conditional
// resolver rule; forced kernel0/test0 -> original full-semantics assertion RED1:
// Knowledge conditional: third native Curse and its successors must preserve complete effective AI Facts physical identities history and RNG
// Exact 1280 source and 31 DLL/PDB restore -> forced kernel0/test0 -> GREEN0.
// Evidence: work/v18-knowledge-conditional-private-20261012-v1/r2-resolver-v3/proof.json.
// Native actions/counters through the third Curse matched before the fix
// (actual baseline phases 0..8). The
// third Curse's sole full-semantics mismatch was the root conditional choice,
// encoded CURSE length23 versus live SLAP length9, not the selected next move.
// Pure attacks retain their existing environmental-risk qualification; only
// supported Curse/PONDER cutoffs claim complete qualified physical reuse.
// Default Run() emits no files. Optional evidence belongs to a private caller.
internal static class KnowledgeConditionalScenarios
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(string? evidenceDirectory = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.knowledge-conditional.native-presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(KnowledgeConditionalScenarios), nameof(Format)));
        // TalkCmd dereferences console SaveManager only to render speech bubbles.
        // Do not patch Curse, choice selection, OnChosen, counter or state machine.
        presentation.Patch(AccessTools.Method(typeof(TalkCmd), "Play"),
            prefix: new HarmonyMethod(typeof(KnowledgeConditionalScenarios), nameof(NoSpeech)));
        var oldAutopilot = AutoPilot.HandedOver.ToArray();
        try { NativeBoundary(evidenceDirectory); }
        finally
        {
            presentation.UnpatchAll(presentation.Id);
            AutoPilot.Clear(); foreach (var id in oldAutopilot) AutoPilot.Set(id, true);
        }
    }

    private static void NativeBoundary(string? evidenceDirectory)
    {
        var party = Enumerable.Range(1, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(18100+i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "KNOWLEDGE-CONDITIONAL-A10", ascensionLevel: 10)) { RoundNumber = 1 };
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); AutoPilot.Set(p.NetId, true);
            // Explicit constructed survivability, not a claimed saved F31 health.
            p.Creature.SetMaxHpInternal(1000); p.Creature.SetCurrentHpInternal(1000);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3); p.MaxEnergy = 3;
            for (var i=0;i<80;i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        var model = (KnowledgeDemon)ModelDb.Monster<KnowledgeDemon>().ToMutable();
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "knowledge"); combat.AddCreature(enemy);
        // Ascension/host/CombatManager must be bound before intent values exist.
        using var native = Scope("NativeFactsState", combat);
        using var managers = Scope("NativeReadyManagerScope", combat);
        model.SetUpForCombat(); model.RollMove(party.Select(p => p.Creature));
        enemy.SetMaxHpInternal(399); enemy.SetCurrentHpInternal(399);
        var ids = party.Select(p=>p.NetId).ToArray();
        var root = KernelCombatCapture.Capture(combat); var initial = root.PlanningState(ids);
        Check(initial.Reusable, "constructed initial native root must support complete future facts");
        var sibling = root.Fork();
        var originalAiRng = combat.RunState.Rng.MonsterAi.CaptureState();
        var counterField = AccessTools.Field(typeof(KnowledgeDemon), "_curseOfKnowledgeCounter");
        for(var phase=0;phase<12;phase++)
        {
            // Existing pure-attack support can mark a predicted attack boundary
            // unqualified. Re-capture its genuinely settled native successor;
            // do not carry that environmental-risk flag into the next Curse.
            var phaseRoot = KernelCombatCapture.Capture(combat);
            var phaseInitial = phaseRoot.PlanningState(ids);
            var phaseSibling = phaseRoot.Fork();
            var predicted = phaseRoot.Fork(); predicted.AutoResolveEnemyChoices = true; predicted.AutoResolveTurnStartChoices = true;
            var nativeBefore = model.NextMove.Id;
            var oldCounter = (int)counterField.GetValue(model)!;
            foreach(var p in party)
                Check(predicted.EndTurn(p, 80, out var why), "normal four-seat predicted enemy phase must settle: " + why);
            AdvanceNative(managers);
            var actual = KernelCombatCapture.Capture(combat).PlanningState(ids);
            var expected = predicted.PlanningState(ids);
            var newCounter = (int)counterField.GetValue(model)!;
            var conditional = (ConditionalBranchState)model.MoveStateMachine!.States["CurseOfKnowledgeBranch"];
            var nativeChoice = conditional.GetNextState(enemy, new MegaCrit.Sts2.Core.Random.Rng(0));
            var predictedCombat = (SimulatedCombatState)AccessTools.Property(typeof(KernelSession), "Combat").GetValue(predicted)!;
            var predictedMove = predictedCombat.GetPredictedMoveId(enemy);
            Console.WriteLine($"KNOWLEDGE phase={phase} round={combat.RoundNumber} performed={nativeBefore} counter={oldCounter}->{newCounter} predictedCounter={predicted.KnowledgeDemonCurseCounter(enemy)} nativeMove={model.NextMove.Id} predictedMove={predictedMove} conditional={nativeChoice} reusable={expected.Reusable}/{actual.Reusable} mismatch={expected.FirstMismatch(actual) ?? "equal"}");
            if(evidenceDirectory is not null)
            {
                Directory.CreateDirectory(evidenceDirectory);
                File.WriteAllText(Path.Combine(evidenceDirectory, $"phase-{phase:00}-expected.txt"), expected.Semantics);
                File.WriteAllText(Path.Combine(evidenceDirectory, $"phase-{phase:00}-actual.txt"), actual.Semantics);
            }
            Check(newCounter == oldCounter + (nativeBefore == "CURSE_OF_KNOWLEDGE_MOVE" ? 1 : 0)
                && predicted.KnowledgeDemonCurseCounter(enemy) == newCounter,
                "real four-seat Curse advances its native and branch counters exactly once per move");
            Check(predictedMove == model.NextMove.Id && nativeChoice == (newCounter < 3 ? "CURSE_OF_KNOWLEDGE_MOVE" : "SLAP_MOVE"),
                "counter threshold must preserve the real native conditional and next selected move");
            if(nativeBefore == "CURSE_OF_KNOWLEDGE_MOVE")
            {
                var amount = newCounter switch { 1 => 6, 2 => 13, _ => 21 };
                Check(party.All(p => p.Creature.GetPower<DisintegrationPower>()?.Amount == amount
                    && predicted.Power<DisintegrationPower>(p.Creature) == amount
                    && p.Creature.GetPower<MindRotPower>() is null && p.Creature.GetPower<SlothPower>() is null
                    && p.Creature.GetPower<WasteAwayPower>() is null),
                    "all four actual native curse menus must pay their real fixed Disintegration OnChosen effects");
            }
            Check(root.PlanningState(ids).Matches(initial) && sibling.PlanningState(ids).Matches(initial)
                && phaseRoot.PlanningState(ids).Matches(phaseInitial) && phaseSibling.PlanningState(ids).Matches(phaseInitial)
                && combat.RunState.Rng.MonsterAi.CaptureState() == originalAiRng,
                "native deterministic Knowledge transitions preserve captured parent sibling and AI RNG");
            Check(expected.Semantics == actual.Semantics,
                "third native Curse and its successors must preserve complete effective AI Facts physical identities history and RNG");
            if(nativeBefore is "CURSE_OF_KNOWLEDGE_MOVE" or "PONDER_MOVE")
                Check(expected.Reusable && actual.Reusable && expected.Matches(actual),
                    "supported native Curse or PONDER boundary must retain strict reusable full Facts and physical identity");
            else
                Check(!expected.Reusable && actual.Reusable && expected.FirstMismatch(actual)!.Contains("expectedReason=environmental-risk", StringComparison.Ordinal),
                    "existing unsupported pure-attack qualification must remain explicit and cannot be promoted by conditional normalization");
            if(phase == 4) Console.WriteLine("PASS: native second Curse retains the below-three conditional and complete future state.");
            if(phase == 8) Console.WriteLine("PASS: native third Curse switches its effective conditional and preserves complete future state.");
            if(phase == 11) Check(model.NextMove.Id == "SLAP_MOVE" && newCounter == 3,
                "native PONDER after three paid curses must select SLAP without a fourth curse");
        }
        Console.WriteLine("PASS: native PONDER after the third Curse selects SLAP through the shared owned-counter rule.");
    }

    private static IDisposable Scope(string name, CombatState combat)
        => (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), name), Fields, null, [combat], null)!;
    private static void AdvanceNative(IDisposable managers)
    {
        var type = managers.GetType(); var turn = AccessTools.Property(type, "Turn").GetValue(managers)!;
        foreach(var name in new[]{"EndPlayerTurnPhaseOneInternal","EndPlayerTurnPhaseTwoInternal","SwitchFromPlayerToEnemySide","StartTurn","StartTurn"})
            AccessTools.Method(type, "Call").Invoke(managers, [name, name == "StartTurn" ? new object?[]{turn,null} : new object?[]{turn}]);
    }
    private static bool NoSpeech() => false;
    private static bool Format(ref string __result) { __result=""; return false; }
    private static void Check(bool value, string message)
    { if(!value) throw new InvalidOperationException("Knowledge conditional: " + message); }
}
