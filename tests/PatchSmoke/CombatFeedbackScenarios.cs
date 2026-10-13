using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class CombatFeedbackScenarios
{
    // R2 2026-09-27; each old behavior/fault exits 1, then is restored.
    // Evidence: work/strength-20260927-v5/r2-*.log and r2-feedback.json / r2-endturn.json.
    // three attacks must focus the wounded enemy instead of distributing damage
    // holding a useless Wisp must not end the turn while a Strike is affordable
    // a cached EndTurn must replan when the live hand still has a useful affordable attack
    // losing rollouts must retain enemy HP progress instead of tying at zero player HP
    // the emergency benefit probe must resolve the potion's actual effect
    // a damage potion that improves a losing fight must be used even when neither rollout wins
    // a useful emergency potion must not be hoarded on a tied wipe
    // a bottle with no outcome or immediate benefit must still be kept
    // losing routes must prefer an actual enemy kill
    // equal-progress losing routes must prefer longer survival
    // end-turn recovery must not force a lethal thorns attack
    // V6 R2 r2-old-gate.log and r2-feedback.json, all exit 1 then restored:
    // a useful potion must not be hoarded merely because a longer losing rollout leaves more enemy HP
    // a worse losing rollout needs actual immediate potion benefit
    // the emergency fallback must not accept a worse route that wipes earlier
    // emergency potion fallback must never replace a verified winning baseline with a wipe
    internal static void Run()
    {
        var (party, combat) = Board("FOCUS-FIRE");
        var enemies = combat.Enemies.ToArray();
        enemies[1].SetCurrentHpInternal(18);
        foreach (var foe in enemies)
            foe.Monster!.SetMoveImmediate(new MoveState("BUFF", _ => Task.CompletedTask, new BuffIntent()), true);
        var focus = KernelRollout.Run(KernelSession.Capture(combat), party, party,
            new RolloutOptions(RolloutPolicyKind.Tactical, MaxSteps: 3, Seed: 1));
        Check(focus.Actions.Count == 3 && focus.Actions.All(a => a.Target == enemies[1]),
            "three attacks must focus the wounded enemy instead of distributing damage");
        Console.WriteLine("PASS: tactical damage concentrates on a reachable kill even during enemy buff turns.");

        var (holdingParty, holdingCombat) = Board("HOLD-ONE-NOT-ALL");
        var holder = holdingParty[0];
        foreach (var card in holder.PlayerCombatState!.Hand.Cards.ToArray()) holder.PlayerCombatState.Hand.RemoveInternal(card);
        holder.PlayerCombatState.LoseEnergy(2);
        holder.PlayerCombatState.Hand.AddInternal(holdingCombat.CreateCard<Wisp>(holder));
        holder.PlayerCombatState.Hand.AddInternal(holdingCombat.CreateCard<Bludgeon>(holder));
        holder.PlayerCombatState.Hand.AddInternal(holdingCombat.CreateCard<StrikeIronclad>(holder));
        var holdRoot = KernelSession.Capture(holdingCombat);
        var hold = KernelTournament.Run(holdRoot, holdingParty, [holder],
            new TournamentOptions(TopK: 1, IncludePotions: false, UsePolicyPortfolio: false,
                ContinuationActors: holdingParty), 30000);
        Check(hold.FirstAction?.Card is StrikeIronclad && !hold.FirstAction.EndTurn,
            "holding a useless Wisp must not end the turn while a Strike is affordable");
        Console.WriteLine("PASS: holding one card does not skip a payable Strike.");

        var endPlanner = new KernelCombatPlanner();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var ending = new KernelTeamSearch.Action(holder, null, null, EndTurn: true);
        // R8 policy change, owner 2026-10-01: only a non-winning continuation
        // can be replaced by the spare-attack heuristic. The old fixture called
        // this a verified victory and therefore guarded the reported bug.
        // WinningPlanScenarios independently requires preservation of a victory.
        // R2 2026-10-01, disabling the attack heuristic entirely exits 1:
        // System.Exception: a non-winning cached EndTurn must replan when the live hand still has a useful affordable attack
        // Evidence: work/ancient-options-20261001/r2-losing-end-turn.json / .log.
        var staleEnd = new TournamentResult(ending,
            new TerminalRecord(false, EvidenceKind.VerifiedTerminal, "", [0, 0, 0, 0], 0, 100), [], 1, 0, "fixture")
        {
            // Adoption advances to cursor 1; cursor 0 is its already-submitted
            // placeholder. Record the pending End from this fixture's live root.
            Script = [ending, TurnStartFixtureCapture.Record(KernelSession.Capture(holdingCombat), [ending])[0]],
            ScriptAliveStates = [holdingParty.Select(p => p.NetId).OrderBy(x => x).ToArray(), holdingParty.Select(p => p.NetId).OrderBy(x => x).ToArray()],
            ScriptEnemyStates = [holdingCombat.HittableEnemies.ToArray(), holdingCombat.HittableEnemies.ToArray()],
        };
        typeof(KernelCombatPlanner).GetMethod("AdoptTournamentScript", flags)!.Invoke(endPlanner, [staleEnd, holdingCombat]);
        object?[] emitArgs = [holdingCombat, holdingParty, true, null];
        // R8 2026-10-04 owner P1: reuse the compared repair, rather than
        // requiring a discarded script and a second full tournament.
        // R2: disabling recovery exited 1, "a non-winning cached EndTurn must
        // emit the useful affordable attack without a fresh tournament".
        // Evidence: work/act2-fixes-20261004/r2-extra-results.json.
        var emitted = false;
        for (var tick = 0; tick < 1000 && !emitted; tick++)
            emitted = (bool)typeof(KernelCombatPlanner).GetMethod("TryEmitFromTournamentScript", flags)!.Invoke(endPlanner, emitArgs)!;
        Check(emitted && endPlanner.ConfirmedEndTurn is null
            && emitArgs[3] is TeamCombatPlanner.Decision { Move.Card: StrikeIronclad },
            "a non-winning cached EndTurn must emit the useful affordable attack without a fresh tournament");
        Console.WriteLine("PASS: non-winning cached end-turn execution cannot silently waste a useful live attack.");

        var (doomedParty, doomedCombat) = Board("USE-POTION-WHILE-LOSING");
        foreach (var p in doomedParty) p.Creature.SetCurrentHpInternal(1);
        foreach (var foe in doomedCombat.Enemies)
        { foe.SetMaxHpInternal(200); foe.SetCurrentHpInternal(200); }
        doomedParty[0].AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), silent: true);
        var doomedRoot = KernelSession.Capture(doomedCombat);
        var noBottle = KernelTournament.Run(doomedRoot, doomedParty, doomedParty,
            new TournamentOptions(TopK: 1, IncludePotions: false, UsePolicyPortfolio: false), 30000);
        var withBottle = KernelTournament.Run(doomedRoot, doomedParty, doomedParty,
            new TournamentOptions(TopK: 20, IncludePotions: true, PotionsOnly: true, UsePolicyPortfolio: false,
                Objective: KernelPotionPolicy.Objective), 30000);
        Check(noBottle.Record is { Verified: true, Victory: false }
            && withBottle.Record is { Verified: true, Victory: false },
            "potion regression fixture must reach two actual wipes");
        Check(withBottle.Record!.RemainingEnemyHp < noBottle.Record!.RemainingEnemyHp,
            "losing rollouts must retain enemy HP progress instead of tying at zero player HP");
        Check(KernelPotionPolicy.ImmediateBenefit(doomedRoot, doomedParty, withBottle.FirstAction) > 0,
            "the emergency benefit probe must resolve the potion's actual effect");
        Check(KernelPotionPolicy.Improves(noBottle.Record, withBottle.Record),
            "a damage potion that improves a losing fight must be used even when neither rollout wins");
        // Owner 2026-10-03 explicitly replaced emergency-bottle replanning with
        // reuse of the native continuation. CombatQualityScenarios now guards
        // adoption and opportunity dispatch with replayed outcome evidence.
        var tie = noBottle.Record with { ResourcesConsumed = 1 };
        Check(KernelPotionPolicy.Improves(noBottle.Record, tie, immediateBenefit: 8),
            "a useful emergency potion must not be hoarded on a tied wipe");
        Check(!KernelPotionPolicy.Improves(noBottle.Record, tie, immediateBenefit: 0),
            "a bottle with no outcome or immediate benefit must still be kept");
        // R2: V5 live boss refused Dexterity despite benefit=32, survival=8->9,
        // enemyHp=306->327. Both endings are still wipes, not a reliable reason
        // to hoard a useful effect. Deliberate old-gate/guard mutations must fail.
        var defensive = tie with { RemainingEnemyHp = tie.RemainingEnemyHp + 21,
            RoundsSurvived = tie.RoundsSurvived + 1 };
        Check(KernelPotionPolicy.Improves(noBottle.Record, defensive, immediateBenefit: 32),
            "a useful potion must not be hoarded merely because a longer losing rollout leaves more enemy HP");
        Check(!KernelPotionPolicy.Improves(noBottle.Record, defensive, immediateBenefit: 0),
            "a worse losing rollout needs actual immediate potion benefit");
        Check(!KernelPotionPolicy.Improves(noBottle.Record,
                defensive with { RoundsSurvived = tie.RoundsSurvived - 1 }, immediateBenefit: 32),
            "the emergency fallback must not accept a worse route that wipes earlier");
        var winningBaseline = tie with { Victory = true, PostCombatHp = [1, 1, 1, 1], IrreversibleLoss = 0 };
        Check(!KernelPotionPolicy.Improves(winningBaseline, defensive, immediateBenefit: 32),
            "emergency potion fallback must never replace a verified winning baseline with a wipe");
        Console.WriteLine("PASS: useful emergency potions may trade losing damage for survival without overriding a win.");
        var kill = noBottle.Record with { RemainingEnemies = noBottle.Record.RemainingEnemies - 1 };
        Check(TerminalComparer.Compare(kill, noBottle.Record, KernelPotionPolicy.Objective) > 0,
            "losing routes must prefer an actual enemy kill");
        var longer = noBottle.Record with { RoundsSurvived = noBottle.Record.RoundsSurvived + 1 };
        Check(TerminalComparer.Compare(longer, noBottle.Record, KernelPotionPolicy.Objective) > 0,
            "equal-progress losing routes must prefer longer survival");
        Console.WriteLine("PASS: losing lines compare kills, damage and survival, and spend useful potions.");
        foreach (var foe in doomedCombat.Enemies)
            ((MegaCrit.Sts2.Core.Models.Powers.ThornsPower)ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.ThornsPower>().ToMutable()).ApplyInternal(foe, 3, true);
        Check(!KernelSession.Capture(doomedCombat).HasBeneficialAttack(doomedParty[0]),
            "end-turn recovery must not force a lethal thorns attack");
        Console.WriteLine("PASS: preserving payable attacks does not force lethal thorns damage.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static (Player[] Party, CombatState Combat) Board(string seed)
    {
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 23, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            for (var i = 0; i < 3; i++) p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        for (var i = 0; i < 2; i++)
        {
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(40); foe.SetCurrentHpInternal(40);
            foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
        }
        return (party, combat);
    }
}
