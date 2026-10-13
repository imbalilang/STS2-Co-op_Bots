using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class SzgPotionReferenceRepairScenarios
{
    // R2, isolated source: bypass full menu identity -> changed-menu fails;
    // replay on the retained state -> failed-transaction detects native partial
    // effects; remove the remaining deadline -> remaining-deadline fails;
    // discard the real suffix -> changed-menu fails. Each mutation compiled,
    // then exact source bytes were restored and the same case passed.
    // Original unmodified CombatQualityScenarios.LateFightBudget separately
    // rejects pausing the primary card reference before a potion comparison,
    // and rejects hiding completed potion results from the tournament. Both
    // controls retain budget3200, two trials, native script replay and the
    // required two different openings; no expected outcome was loosened.
    // Warm-suite R2: after original native party setup has JIT-compiled rollout
    // callers, observing the small CardBranches iterator factory misses actual
    // calls. Reverting this observer to that factory builds0/RED1; the restored
    // large formal Evaluate entry builds0/GREEN0 on the same preceding setup.
    // Four existing production faults were re-proved with this stronger actual
    // four-option Survivor menu. Exact physical input files, exits and raw logs:
    // work/szg-repair-20261008T124624/model/late-diagnostic/r2-warm-final/proof.json.
    private static Player? observedPlayer;
    private static bool failAttempt;
    private static int attemptedFailures, formalAfterPotion, contaminatedFormals, tailStarts, contaminatedTailStarts;

    internal static void Run(string? only = null)
    {
        TestEnvironment.Ensure();
        var hooks = new Harmony("coopbots.szg-potion-reference-tests");
        hooks.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(SzgPotionReferenceRepairScenarios), nameof(Format)));
        hooks.Patch(AccessTools.Method(typeof(KernelChoiceContinuation), nameof(KernelChoiceContinuation.Evaluate)),
            prefix: new HarmonyMethod(typeof(SzgPotionReferenceRepairScenarios), nameof(ObserveFormal)));
        hooks.Patch(AccessTools.GetDeclaredConstructors(typeof(RolloutRun)).Single(c => c.GetParameters()[0].ParameterType == typeof(KernelSession)),
            prefix: new HarmonyMethod(typeof(SzgPotionReferenceRepairScenarios), nameof(ObserveTailStart)));
        hooks.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.ReplayPlanned),
            [typeof(KernelTeamSearch.Action), typeof(int), typeof(string).MakeByRefType(), typeof(KernelTeamSearch.Action).MakeByRefType()]),
            prefix: new HarmonyMethod(typeof(SzgPotionReferenceRepairScenarios), nameof(FailPartially)));
        try
        {
            foreach (var name in new[] { "changed-menu", "failed-transaction", "remaining-deadline" })
            {
                if (only is not null && only != name) continue;
                Case(name);
            }
        }
        finally
        {
            observedPlayer = null; failAttempt = false;
            foreach (var method in hooks.GetPatchedMethods().ToArray()) hooks.Unpatch(method, HarmonyPatchType.All, hooks.Id);
        }
    }

    private static void Case(string name)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, 29991);
        Player[] party = [player];
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "SZG-POTION-REFERENCE")) { RoundNumber = 1 };
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 1;
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(1);
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(1);
        var survivor = combat.CreateCard<Survivor>(player); player.PlayerCombatState.Hand.AddInternal(survivor);
        player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendSilent>(player));
        // Drawing attacks could legally replace Survivor with a damage opening.
        // Here native Survivor gives 8 block against each same-cost Defend's 5,
        // so the changed four-card discard menu is exercised by the real suffix.
        player.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<DefendSilent>(player));
        player.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<DefendSilent>(player));
        player.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<DefendSilent>(player));
        player.AddPotionInternal(ModelDb.Potion<SwiftPotion>().ToMutable(), silent: true);
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(300); enemy.SetCurrentHpInternal(300);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 30, true);
        var root = KernelSession.Capture(combat); var before = root.PartyStateText();
        var native = root.CardBranches(survivor, null, 1).Single(branch => branch.Boundary.Length == 0);
        var first = new KernelTeamSearch.Action(player, survivor, null, CardStateKey: root.CardStateKey(survivor),
            Choices: native.Choices, ChoiceOrdinals: native.ChoiceOrdinals);
        var tail = KernelRollout.Run(native.State, party, party, new(RolloutPolicyKind.Kill) {
            InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false });
        Check(tail.Terminal && tail.Record.Verified, "the independent card reference must reach an actual ending");
        var baseline = new TournamentResult(first, tail.Record, [tail.Record], 1, 0, "native-reference") {
            Script = new[] { first }.Concat(tail.Actions).ToArray(),
            ScriptAliveStates = new[] { party.Select(p => p.NetId).ToArray() }.Concat(tail.AliveStates).ToArray(),
            ScriptEnemyStates = new[] { root.Enemies.ToArray() }.Concat(tail.EnemyStates).ToArray(),
        };
        observedPlayer = player; attemptedFailures = formalAfterPotion = contaminatedFormals = tailStarts = contaminatedTailStarts = 0;
        failAttempt = name == "failed-transaction";
        var run = new KernelPotionContinuationRun(root, party, party, baseline,
            name == "remaining-deadline" ? 0 : 2000);
        while (!run.Advance(TimeSpan.FromMilliseconds(2))) { }
        Console.WriteLine($"PROBE potion-reference {name} completed={run.Completed} formal={formalAfterPotion} partialFailures={attemptedFailures} contaminated={contaminatedFormals} tailStarts={tailStarts} contaminatedStarts={contaminatedTailStarts} ms={run.ComputeMs:F1} baselineMenus={string.Join(',', first.Choices!.Select(c => c.Options!.Count))} replayMenus={string.Join(',', run.CompletedResults.SelectMany(c => c.Script!).Where(a => a.Card is Survivor).SelectMany(a => a.Choices!).Select(c => c.Options!.Count))}");
        if (name == "remaining-deadline")
            Check(run.Completed == 0 && run.CompletedResults.Count == 0 && run.Result == baseline,
                "an exhausted remaining deadline must publish no bottle comparison or ending");
        else
        {
            if (name == "failed-transaction")
                Check(attemptedFailures == 1 && tailStarts > 0 && contaminatedTailStarts == 0 && contaminatedFormals == 0,
                    "a failed native attempt must resume from the pre-action state, without its partial side effects");
            Check(run.Completed > 0 && run.Completed == run.CompletedResults.Count && formalAfterPotion > 0,
                "a changed actual native hand must receive a fresh formal comparison before its ending is counted");
            foreach (var candidate in run.CompletedResults)
            {
                Check(candidate.FirstAction?.Potion is SwiftPotion && candidate.Record is { Verified: true },
                    "only a real bottle opening and completed native ending may be published");
                var replay = root.ForkForReplay();
                foreach (var action in candidate.Script!)
                {
                    Check(replay.ReplayPlanned(action, 80, out var why), "completed bottle script must replay: " + why);
                    if (action.Card is Survivor)
                        Check(action.Choices is { Count: 1 } && action.Choices[0].Options is { Count: 4 }
                            && action.Choices[0].Cards.Single().Id == "DEFEND_SILENT"
                            && replay.Block(player.Creature) == 8 && replay.Hand(player).Count == 3,
                            "the actual drawn four-option Survivor menu must publish its native discard and eight-block after-state");
                }
                Check(candidate.Script!.Any(action => action.Card is Survivor),
                    "the defensive control must exercise the actual changed Survivor menu");
                Check(replay.HasTerminal && replay.HasWon == candidate.Record!.Victory,
                    "published potion comparison must reproduce the actual terminal boundary");
            }
        }
        Check(root.PartyStateText() == before && player.Creature.CurrentHp == 1 && player.Potions.Count() == 1,
            "potion comparisons must preserve the captured root and live native board");
        observedPlayer = null; failAttempt = false;
        Console.WriteLine("PASS: potion-reference " + name);
    }

    private static void ObserveFormal(KernelSession root)
    {
        if (observedPlayer is not { } owner || root.UsablePotions(owner).Count > 0) return;
        formalAfterPotion++;
        if (root.Hp(owner.Creature) != 1) contaminatedFormals++;
    }
    private static void ObserveTailStart(KernelSession __0)
    {
        if (observedPlayer is not { } owner || KernelChoiceContinuation.Active || __0.UsablePotions(owner).Count > 0) return;
        tailStarts++;
        if (__0.Hp(owner.Creature) != 1) contaminatedTailStarts++;
    }

    private static bool FailPartially(KernelSession __instance, KernelTeamSearch.Action action,
        ref string boundary, ref KernelTeamSearch.Action resolvedAction, ref bool __result)
    {
        if (!failAttempt || attemptedFailures > 0 || action.Card is not Survivor
            || observedPlayer is not { } owner || __instance.UsablePotions(owner).Count > 0) return true;
        // Supply the engine's failure-after-partial-effects contract. The actual
        // native card/menu and subsequent continuation remain unmodified.
        attemptedFailures++; __instance.Simulator.State.GetCreature(owner.Creature).CurrentHp = 17;
        boundary = "partial-native-effect-control"; resolvedAction = action; __result = false; return false;
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
