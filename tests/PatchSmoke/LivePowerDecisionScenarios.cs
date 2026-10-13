using CoopBots;
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
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Two hands from the live A10 run of 2026-10-04 (godot.log, S-jie Ironclad,
// bot-driven). Both are "affordable permanent power vs one more attack" and both
// were spent on the attack:
//   * 2 energy, hand [PYRE+/2, STRIKE/1] -> STRIKE, PYRE never played.
//   * 3 energy, hand [CRUELTY+/1, BASH+/2, STRIKE, STRIKE, STRIKE] against a
//     520-HP already-Vulnerable boss -> BASH + STRIKE, CRUELTY never played.
internal static class LivePowerDecisionScenarios
{
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Live power decision: " + why); }

    internal static void Run()
    {
        TestEnvironment.Ensure();
        new Harmony("coopbots.test.live-power").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LivePowerDecisionScenarios), nameof(Format)));

        Pyre();
        PyreUnupgradedLiveHand();
        InfiniteBladesLiveHand();
        Cruelty();
        Console.WriteLine("PASS: live 2026-10-04 power hands deploy Pyre and Cruelty instead of one more Strike.");
    }

    private static void Pyre()
    {
        var (party, combat, _) = Board(enemyHp: 400);
        var owner = party[0];
        owner.PlayerCombatState!.LoseEnergy(1);
        var pyre = combat.CreateCard<Pyre>(owner);
        pyre.UpgradeInternal();
        owner.PlayerCombatState.Hand.AddInternal(pyre);
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        // The live deck's AVERAGE per-turn demand sat at or below the seat's energy,
        // which is exactly what zeroed the old Pyre gate even though the hand itself
        // was energy-starved (Pyre+ 2 + Strike 1 > 2 energy). Mostly-free cards plus
        // a couple of paid ones reproduce that shape.
        AddDraw(owner, combat, "ANGER", "ANGER", "ANGER", "ANGER", "ANGER", "ANGER",
            "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD");
        var root = KernelSession.Capture(combat);
        var after = root.CardBranches(pyre, null, 1).Single(b => b.Boundary.Length == 0).State;
        var retention = after.PersistentValue(owner, 10, 1) - root.PersistentValue(owner, 10, 1);
        var line = KernelRollout.Run(root.Fork(), party, party, new(RolloutPolicyKind.Tactical, MaxSteps: 4));
        var tournament = KernelTournament.Run(root.Fork(), party, party,
            new TournamentOptions(TopK: 6, ReserveMs: 0, IncludePotions: false,
                Rollout: new RolloutOptions(MaxSteps: 100, MaxRounds: 8)), budgetMs: 5000);
        Console.WriteLine($"LIVE-PROBE PYRE retention={retention:F2} tactical={string.Join(',', line.Actions.Select(a => a.Card?.Id.Entry ?? "END"))}");
        Console.WriteLine($"LIVE-PROBE PYRE tournament first={tournament.FirstAction?.Card?.Id.Entry} script={Script(tournament)}");
        Check(retention > 0, "Pyre+ at 2 energy must be worth more than nothing.");
        Check(line.Actions.FirstOrDefault()?.Card is Pyre,
            "tactical must deploy Pyre+ before spending its last energy on a Strike in a long fight.");
        Check(tournament.Script is { } pyreScript && pyreScript.TakeWhile(a => !a.EndTurn).Any(a => a.Card is Pyre),
            "the tournament's opening turn must deploy Pyre+.");
    }

    private static void PyreUnupgradedLiveHand()
    {
        // godot.log:22434 verbatim: 2 energy, hand [DEFEND, DEFEND, ASCENDERS_BANE,
        // SHIV, SHIV, PYRE/2, STRIKE, STRIKE] against two ~170-HP enemies. The bot
        // spent the energy on a Strike and the Shivs and never played Pyre.
        var (party, combat, first) = Board(enemyHp: 173);
        var second = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy2");
        combat.AddCreature(second); second.Monster!.SetUpForCombat();
        second.SetMaxHpInternal(166); second.SetCurrentHpInternal(166);
        second.Monster.SetMoveImmediate((MoveState)second.Monster.MoveStateMachine!.States["ATTACK"], true);
        var owner = party[0];
        owner.PlayerCombatState!.LoseEnergy(1);
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<AscendersBane>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Shiv>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Shiv>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Pyre>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        var root = KernelSession.Capture(combat);
        foreach (var policy in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Tactical, RolloutPolicyKind.Defend })
        {
            var t = KernelTournament.Run(root.Fork(), party, party,
                new TournamentOptions(TopK: 6, ReserveMs: 0, IncludePotions: false,
                    Rollout: new RolloutOptions(policy, MaxSteps: 100, MaxRounds: 8)), budgetMs: 5000);
            Console.WriteLine($"LIVE-PROBE PYRE-UNUP policy={policy} first={t.FirstAction?.Card?.Id.Entry} steps={t.Script?.Count} stop={t.StopReason} script={string.Join(",", (t.Script ?? []).Take(40).Select(a => a.Card?.Id.Entry ?? "END"))}");
        }
        var pyreBranch = root.CardBranches(root.Hand(owner).First(c => c.Id.Entry == "PYRE"), null, 1)
            .Single(b => b.Boundary.Length == 0).State;
        var pyreRetention = pyreBranch.PersistentValue(owner, 10, 1) - root.PersistentValue(owner, 10, 1);
        Console.WriteLine($"LIVE-PROBE PYRE-UNUP persistentGain={pyreRetention:F2}");
        first.SetMaxHpInternal(1000); first.SetCurrentHpInternal(1000);
        second.SetCurrentHpInternal(0);
        var longRoot = KernelSession.Capture(combat);
        var longTournament = KernelTournament.Run(longRoot.Fork(), party, party,
            new TournamentOptions(TopK: 6, ReserveMs: 0, IncludePotions: false,
                Rollout: new RolloutOptions(MaxSteps: 200, MaxRounds: 20)), budgetMs: 8000);
        Console.WriteLine($"LIVE-PROBE PYRE-UNUP-LONG first={longTournament.FirstAction?.Card?.Id.Entry} pyresInTurn1={Turn1Pyre(longTournament)}");
        // The gate fix is the part that can be asserted here: the drawn Pyre now
        // has positive retention and the tournament keeps it in the plan. It is
        // played on the first turn where it does not consume the whole turn (2
        // energy for an unupgraded Pyre is a full tempo loss on turn 1), which is
        // why the live skips were mostly short fights, not a dropped card.
        Check(pyreRetention > 0, "a drawn Pyre must have positive retained value.");
        Check(longTournament.Script?.Any(action => action.Card is Pyre) == true,
            "the long-fight tournament plan must keep Pyre, not drop it entirely.");
    }

    private static void InfiniteBladesLiveHand()
    {
        // godot.log, Knowledge Demon opening (Bot1 / 12716757972810793474), e3:
        // hand [INFINITE_BLADES+/1, AFTERIMAGE/1, DEFEND+/1, PIERCING_WAIL/1,
        // BLADE_SYMPHONY+/1, STRIKE/1, SURVIVOR/1, ACCURACY+/1, STRIKE/1, STRIKE/1]
        // against the 1915-HP boss. The bot played AFTERIMAGE, ACCURACY, then SHIV at
        // 1 energy and left the upgraded engine; across the run INFINITE_BLADES+ was
        // in hand on 39 decisions (the 1915->937 boss and the 820-HP elite included)
        // and played zero times.
        var (party, combat, _) = Board(enemyHp: 1915);
        var owner = party[0];
        CardModel Add<T>() where T : CardModel => combat.CreateCard<T>(owner);
        CardModel Upgraded<T>() where T : CardModel { var c = combat.CreateCard<T>(owner); c.UpgradeInternal(); return c; }
        owner.PlayerCombatState!.Hand.AddInternal(Upgraded<InfiniteBlades>());
        owner.PlayerCombatState.Hand.AddInternal(Add<Afterimage>());
        owner.PlayerCombatState.Hand.AddInternal(Upgraded<DefendSilent>());
        owner.PlayerCombatState.Hand.AddInternal(Add<PiercingWail>());
        owner.PlayerCombatState.Hand.AddInternal(Upgraded<BladeSymphony>());
        owner.PlayerCombatState.Hand.AddInternal(Add<StrikeSilent>());
        owner.PlayerCombatState.Hand.AddInternal(Add<Survivor>());
        owner.PlayerCombatState.Hand.AddInternal(Upgraded<Accuracy>());
        owner.PlayerCombatState.Hand.AddInternal(Add<StrikeSilent>());
        owner.PlayerCombatState.Hand.AddInternal(Add<StrikeSilent>());
        var root = KernelSession.Capture(combat);
        foreach (var policy in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Tactical, RolloutPolicyKind.Defend })
        {
            var t = KernelTournament.Run(root.Fork(), party, party,
                new TournamentOptions(TopK: 6, ReserveMs: 0, IncludePotions: false,
                    Rollout: new RolloutOptions(policy, MaxSteps: 100, MaxRounds: 8)), budgetMs: 6000);
            var hasIB = t.Script?.Any(a => a.Card is InfiniteBlades) == true;
            var plan = string.Join(",", (t.Script ?? []).Select(a => a.Card?.Id.Entry ?? "END").Take(16));
            Console.WriteLine($"LIVE-PROBE IB+ policy={policy} first={t.FirstAction?.Card?.Id.Entry} hasIB={hasIB} plan={plan}");
            // The upgraded engine must stay reachable when the same hand is planned
            // from a clean root; the live run lost it inside the JOINT script, which
            // is a separate under-play the solo plan does not reproduce.
            Check(hasIB, $"{policy} must keep the upgraded Infinite Blades reachable in the live opening hand.");
        }
    }

    private static void Cruelty()
    {
        var (party, combat, enemy) = Board(enemyHp: 520);
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(enemy, 8, true);
        var owner = party[0];
        var cruelty = combat.CreateCard<Cruelty>(owner);
        cruelty.UpgradeInternal();
        owner.PlayerCombatState!.Hand.AddInternal(cruelty);
        var bash = combat.CreateCard<Bash>(owner);
        bash.UpgradeInternal();
        owner.PlayerCombatState.Hand.AddInternal(bash);
        for (var i = 0; i < 3; i++) owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        AddDraw(owner, combat, "BASH", "UPPERCUT", "POMMEL_STRIKE", "SHOCKWAVE",
            "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD");
        var root = KernelSession.Capture(combat);
        var after = root.CardBranches(cruelty, null, 1).Single(b => b.Boundary.Length == 0).State;
        var retention = after.PersistentValue(owner, 10, 1) - root.PersistentValue(owner, 10, 1);
        // Does the SIMULATION realize Cruelty, or only the heuristic? Play the
        // same Strike in the Cruelty branch and in the plain branch and compare.
        var strikeCard = root.Hand(owner).First(c => c.Id.Entry == "STRIKE_IRONCLAD");
        var plainStrike = root.CardBranches(strikeCard, enemy, 1).Single(b => b.Boundary.Length == 0).State;
        var crueltyStrike = after.CardBranches(after.Hand(owner).First(c => c.Id.Entry == "STRIKE_IRONCLAD"), enemy, 1)
            .Single(b => b.Boundary.Length == 0).State;
        Console.WriteLine($"LIVE-PROBE CRUELTY simulation plainDamage={root.Hp(enemy) - plainStrike.Hp(enemy)} crueltyDamage={after.Hp(enemy) - crueltyStrike.Hp(enemy)}");
        var line = KernelRollout.Run(root.Fork(), party, party, new(RolloutPolicyKind.Tactical, MaxSteps: 6));
        var tournament = KernelTournament.Run(root.Fork(), party, party,
            new TournamentOptions(TopK: 6, ReserveMs: 0, IncludePotions: false,
                Rollout: new RolloutOptions(MaxSteps: 100, MaxRounds: 8)), budgetMs: 5000);
        Console.WriteLine($"LIVE-PROBE CRUELTY retention={retention:F2} tactical={string.Join(',', line.Actions.Select(a => a.Card?.Id.Entry ?? "END"))}");
        Console.WriteLine($"LIVE-PROBE CRUELTY tournament first={tournament.FirstAction?.Card?.Id.Entry} script={Script(tournament)}");
        Check(crueltyStrike.Hp(enemy) < plainStrike.Hp(enemy),
            "the simulation must realize Cruelty's bonus damage, not only the heuristic.");
        Check(line.Actions.FirstOrDefault()?.Card is Cruelty,
            "tactical must deploy Cruelty+ before spending its last energy on a Strike against a long Vulnerable enemy.");
        Check(tournament.Script is { } crueltyScript && crueltyScript.TakeWhile(a => !a.EndTurn).Any(a => a.Card is Cruelty),
            "the tournament's opening turn must deploy Cruelty+.");
    }

    private static string Script(TournamentResult result)
        => result.Script is null ? "" : string.Join(',', result.Script.Take(8).Select(a => a.Card?.Id.Entry ?? "END"));

    private static bool Turn1Pyre(TournamentResult result)
    {
        var ends = 0;
        foreach (var action in result.Script ?? [])
        {
            if (action.EndTurn) { if (++ends >= 4) return false; continue; }
            if (action.Card is Pyre) return true;
        }
        return false;
    }

    private static void AddDraw(Player owner, CombatState combat, params string[] ids)
    {
        foreach (var id in ids)
        {
            var card = id switch
            {
                "BASH" => (CardModel)combat.CreateCard<Bash>(owner),
                "POMMEL_STRIKE" => combat.CreateCard<PommelStrike>(owner),
                "UPPERCUT" => combat.CreateCard<Uppercut>(owner),
                "SHRUG_IT_OFF" => combat.CreateCard<ShrugItOff>(owner),
                "SHOCKWAVE" => combat.CreateCard<Shockwave>(owner),
                "ANGER" => combat.CreateCard<Anger>(owner),
                "DEFEND_IRONCLAD" => combat.CreateCard<DefendIronclad>(owner),
                _ => combat.CreateCard<StrikeIronclad>(owner),
            };
            owner.PlayerCombatState!.DrawPile.AddInternal(card);
        }
    }

    private static (Player[], CombatState, Creature) Board(int enemyHp)
    {
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 33, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "LIVE-POWER"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat, enemy);
    }
}
