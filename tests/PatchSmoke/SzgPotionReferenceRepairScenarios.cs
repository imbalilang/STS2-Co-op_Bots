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
        // ReplayPlanned is a one-line wrapper which warm callers can inline.
        // Patch the real large causal transaction entry (contains isolation and
        // try/finally), so prior native suites cannot bypass this fault control.
        hooks.Patch(AccessTools.Method(typeof(KernelSession), "ApplyCausal",
            [typeof(KernelTeamSearch.Action), typeof(int), typeof(bool), typeof(string).MakeByRefType(), typeof(KernelTeamSearch.Action).MakeByRefType()]),
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
        if (name == "failed-transaction") WarmFaultControl(root, player, first);
        observedPlayer = player; attemptedFailures = formalAfterPotion = contaminatedFormals = tailStarts = contaminatedTailStarts = 0;
        // Candidate discovery may itself evaluate private potion forks. Arm
        // the failure only after construction so it belongs to the retained
        // reference's actual ReplayPlanned attempt inside Advance.
        failAttempt = false;
        var run = new KernelPotionContinuationRun(root, party, party, baseline,
            name == "remaining-deadline" ? 0 : 2000);
        failAttempt = name == "failed-transaction";
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

    private static void WarmFaultControl(KernelSession root, Player owner, KernelTeamSearch.Action first)
    {
        // Exercise the public replay call once before arming the mutation. This
        // is a positive control for the same warmed entry used by production,
        // not a substitute for the actual failed-transaction run below.
        observedPlayer = null; failAttempt = false;
        var warm = root.ForkForReplay();
        Check(warm.ReplayPlanned(first, 80, out var warmWhy),
            "the warm replay fault-control fixture must execute its ordinary native card: " + warmWhy);
        var partial = root.ForkForReplay();
        var bottle = partial.UsablePotions(owner).Single();
        Check(partial.UsePotion(bottle, partial.PotionTargets(bottle).FirstOrDefault(), out var potionWhy),
            "the partial-effect fault control must actually consume and draw its native bottle: " + potionWhy);
        observedPlayer = owner; attemptedFailures = 0; failAttempt = true;
        Check(!partial.ReplayPlanned(first, 80, out var failure, out var resolved)
            && failure == "partial-native-effect-control" && attemptedFailures == 1
            && partial.Hp(owner.Creature) == 17 && ReferenceEquals(resolved, first)
            && root.Hp(owner.Creature) == 1 && root.UsablePotions(owner).Count == 1,
            "a warmed public replay call must hit exactly one real causal-entry fault, change only its private HP to 17 and return false");
        Console.WriteLine("FAULT-CONTROL potion-reference warmed causal entry injected=1 partialHp=17 rootHp=1");
        observedPlayer = null; failAttempt = false;
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
        bool exactTurnStartContract, ref string boundary, ref KernelTeamSearch.Action resolvedAction, ref bool __result)
    {
        if (!exactTurnStartContract || !failAttempt || attemptedFailures > 0 || action.Card is not Survivor
            || observedPlayer is not { } owner || !ReferenceEquals(action.Player, owner)
            || __instance.UsablePotions(owner).Count > 0) return true;
        // Supply the engine's failure-after-partial-effects contract. The actual
        // native card/menu and subsequent continuation remain unmodified.
        attemptedFailures++; __instance.Simulator.State.GetCreature(owner.Creature).CurrentHp = 17;
        boundary = "partial-native-effect-control"; resolvedAction = action; __result = false; return false;
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

// R2 production mutation: KernelPotionContinuationRun var attempt = replay.Fork() -> replay.
// RED (build 0, test 1): System.Exception: a failed native attempt must resume from the pre-action state, without its partial side effects
// Restored exact production bytes; build 0, focused failed-transaction GREEN 0.
