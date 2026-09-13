using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class FocusScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bots = Enumerable.Range(1, 2).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Genius, i, i))).ToArray();
        var party = new[] { human }.Concat(bots).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "FOCUS-ALTERNATING"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(100); p.Creature.SetCurrentHpInternal(100);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
        }
        var enemies = Enumerable.Range(0, 2).Select(i =>
        {
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(36);
            return enemy;
        }).ToArray();
        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!;
        var choose = planner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        var focus = typeof(BotBrain).Assembly.GetType("CoopBots.TeamFocus");
        var submitted = focus?.GetMethod("ObserveSubmitted", BindingFlags.Static | BindingFlags.NonPublic);
        var resolve = focus?.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic);
        object? Plan(uint? manual = null) => choose.Invoke(null, new object?[] { bots, party, manual });
        BotBrain.CombatMove Move(object plan) => (BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!;
        Creature? Preferred() => (Creature?)resolve!.Invoke(null, new object?[] { combat, enemies.Where(e => e.IsAlive).ToList(), null });
        void Check(bool condition, string message) { if (!condition) throw new Exception("Focus regression: " + message); }
        void Ready()
        {
            foreach (var bot in bots)
            {
                foreach (var card in bot.PlayerCombatState!.Hand.Cards.ToList()) bot.PlayerCombatState.Hand.RemoveInternal(card);
                bot.PlayerCombatState.LoseEnergy(bot.PlayerCombatState.Energy); bot.PlayerCombatState.GainEnergy(1);
                bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            }
        }
        void Intent(Creature enemy, bool attack, int damage = 4) => enemy.Monster!.SetMoveImmediate(
            new MoveState(attack ? "ATTACK" : "DEBUFF", _ => Task.CompletedTask,
                attack ? new SingleAttackIntent(damage) : new DebuffIntent()), true);
        var firstKillRound = 0;
        var focused = true;
        var trace = new List<string>();
        for (var round = 1; round <= 6; round++)
        {
            Ready(); Intent(enemies[0], round % 2 == 1); Intent(enemies[1], round % 2 == 0);
            for (var action = 0; action < 2; action++)
            {
                var move = Move(Plan()!);
                focused &= move.Target == enemies[round <= 3 ? 0 : 1];
                trace.Add($"{round}:{Array.IndexOf(enemies, move.Target)}");
                submitted?.Invoke(null, new object?[] { combat, move, null });
                // A controlled replay of fixed six-damage Strikes; not a full engine run.
                move.Target!.SetCurrentHpInternal(move.Target.CurrentHp - 6);
                move.Card.Owner.PlayerCombatState!.LoseEnergy(1);
                move.Card.Owner.PlayerCombatState.Hand.RemoveInternal(move.Card);
                if (firstKillRound == 0 && enemies.Any(e => !e.IsAlive)) firstKillRound = round;
            }
        }
        Console.WriteLine($"FOCUS-REPLAY: firstKillRound={firstKillRound}; actions={string.Join(',', trace)}");
        Check(focused && firstKillRound == 3 && enemies.All(e => !e.IsAlive), "First removal must be round three, then finish the second by round six without intent-driven switching.");

        Ready(); enemies[0].SetCurrentHpInternal(30); enemies[1].SetCurrentHpInternal(36);
        Intent(enemies[0], false); Intent(enemies[1], true);
        var preferred = Preferred();
        var manualTarget = enemies.First(e => e != preferred);
        Check(Move(Plan(manualTarget.CombatId)!).Target == manualTarget, "Manual focus must influence an otherwise equivalent attack.");
        Check(Preferred() == preferred, "A speculative manual/advice search must not mutate committed focus.");
        var restoredCombat = new CombatState(runState: combat.RunState);
        Check(ReferenceEquals(resolve!.Invoke(null, new object?[] { restoredCombat, enemies, null }), enemies[0]),
            "A fresh combat identity must reconstruct focus from remaining HP, without inheriting an old lock.");
        preferred!.GainBlockInternal(100);
        Check(Preferred() != preferred, "A large block wall must allow reconsidering the old focus.");
        preferred.LoseBlockInternal(100);
        Check(ReferenceEquals(resolve.Invoke(null, new object?[] { combat, new[] { manualTarget }, null }), manualTarget),
            "An unhittable target must be dropped even if it is alive.");
        // Establish A, then ensure safety can override an explicit manual A focus.
        var opening = Move(Plan(enemies[0].CombatId)!);
        submitted!.Invoke(null, new object?[] { combat, opening, enemies[0].CombatId });
        enemies[1].SetCurrentHpInternal(6); human.Creature.SetCurrentHpInternal(1);
        Intent(enemies[1], true, 20);
        Check(Move(Plan(enemies[0].CombatId)!).Target == enemies[1], "Kill the lethal attacker even when the manual focus is elsewhere.");
        human.Creature.SetCurrentHpInternal(100); enemies[0].SetCurrentHpInternal(0);
        Check(Preferred() == enemies[1], "A dead target must be dropped immediately.");
        Console.WriteLine("PASS: alternating attack/debuff six-round focus replay, first kill on round three, manual focus, pure probes, emergency lethal override and dead-target release.");
    }
}
