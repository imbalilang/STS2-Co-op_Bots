using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class StrengthScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var party = new[] { human, bot };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "STRENGTH-15"));
        foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100);
        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!;
        var choose = planner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Plan() => choose.Invoke(null, new object?[] { new[] { bot }, party, null });
        BotBrain.CombatMove Move(object plan) => (BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!;
        int Count(object plan) => (int)plan.GetType().GetProperty("PlannedCards")!.GetValue(plan)!;
        T Hand<T>() where T : CardModel { var card = combat.CreateCard<T>(bot); bot.PlayerCombatState!.Hand.AddInternal(card); return card; }
        void Check(bool ok, string failure) { if (!ok) throw new Exception("Strength regression: " + failure); }
        void Reset(int incoming, int energy = 1)
        {
            foreach (var p in party)
            {
                foreach (var card in p.PlayerCombatState!.Hand.Cards.ToList()) p.PlayerCombatState.Hand.RemoveInternal(card);
                foreach (var power in p.Creature.Powers.ToList()) power.RemoveInternal();
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
                p.Creature.LoseBlockInternal(p.Creature.Block);
                p.PlayerCombatState.Phase = PlayerTurnPhase.Play;
                p.PlayerCombatState.LoseEnergy(p.PlayerCombatState.Energy); p.PlayerCombatState.GainEnergy(p == bot ? energy : 0);
            }
            human.Creature.GainBlockInternal(100);
            enemy.SetCurrentHpInternal(100);
            enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(incoming)), true);
        }
        Reset(1); var strike = Hand<StrikeIronclad>(); var defendAtTwo = Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == strike, "At healthy HP, accept one damage to deal six instead of blocking one.");
        bot.Creature.SetCurrentHpInternal(2);
        // THE SAFETY PROPERTY, not a record of what shipped. This used to assert Strike, with the
        // note "a safety preference that is only asserted is not a safety property". The source
        // note on EnemyHpWeight names the remedy and this is it: the health-deficit weighting
        // lives in TriageCost, and its coefficient must clear the enemy-HP credit — 1 + 4*0.975^2
        // = 4.80 for one chip point. A 2-HP bot must not trade its life for six damage.
        // The value is MEASURED against this assertion: 3 was tried first and still chose the
        // attack, so the enemy-HP credit is more than the hard term's 6*0.5 = 3.00 alone.
        // CHECKED (R2): setting HealthDeficitWeight back to 2 turns this red, verbatim:
        //   System.Exception: At 2 HP a bot must block the chip damage it can stop rather than
        //   trade six damage for its life; got StrikeIronclad.
        Check(Move(Plan()!).Card == defendAtTwo,
            "At 2 HP a bot must block the chip damage it can stop rather than trade six damage "
            + $"for its life; got {Move(Plan()!).Card.GetType().Name}.");
        Reset(8); Hand<StrikeIronclad>(); var defend = Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == defend, "Five useful block should still beat six ordinary damage under pressure.");
        Reset(5); bot.Creature.SetCurrentHpInternal(5); Hand<StrikeIronclad>(); defend = Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == defend, "A cheap bot rescue must still beat avoidable death for damage.");
        Reset(1); var inflame = Hand<Inflame>(); Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == inflame, "Healthy bot may invest in known growth instead of preventing one HP loss.");
        Reset(0, 2); Hand<StrikeIronclad>(); Hand<StrikeIronclad>();
        var rage = ModelDb.Power<RagePower>().ToMutable(); rage.ApplyInternal(bot.Creature, 3, true);
        enemy.SetCurrentHpInternal(12);
        var uncertain = Plan()!;
        Check(Count(uncertain) == 2, "An unmodeled power must not truncate all subsequent attacks.");
        Check(!Move(uncertain).Reason.Contains("confirmed-team-lethal"), "Speculative multi-card damage is not a confirmed kill.");
        Reset(0, 2); Hand<StrikeIronclad>(); Hand<StrikeIronclad>();
        var ritual = ModelDb.Power<RitualPower>().ToMutable(); ritual.ApplyInternal(bot.Creature, 3, true);
        enemy.SetCurrentHpInternal(12);
        Check(Count(Plan()!) == 2 && Move(Plan()!).Reason.Contains("confirmed-team-lethal"),
            "Verified turn-end growth must preserve a real two-strike lethal.");
        Reset(0, 3); var corruption = Hand<MachineLearning>();
        Check(Plan() is null, "Opaque powers must not receive fabricated team growth credit.");
        var fallback = typeof(BotBrain).GetMethod("ChooseEffectFallback", BindingFlags.Static | BindingFlags.NonPublic)!;
        BotBrain.CombatMove? Fallback() => (BotBrain.CombatMove?)fallback.Invoke(null, new object[] { bot, 0 });
        Check(Fallback()?.Card == corruption, "A useful opaque power should remain available through a safe fallback.");
        Hand<StrikeIronclad>();
        Check(Fallback()?.Card == corruption, "A modeled personal best must not hide an unmodeled fallback.");
        human.Creature.LoseBlockInternal(100); human.Creature.SetCurrentHpInternal(1);
        enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
        Check(Fallback() is null, "No speculative power spending when a teammate faces lethal damage.");
        Console.WriteLine("PASS: offense versus chip damage, useful defense, cheap rescue, growth, multi-card unknown-power projection, hard-lethal boundaries and partial-effect fallback.");
    }
}
