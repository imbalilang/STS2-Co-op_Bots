using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class CoordinatorScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var support = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var attacker = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 2));
        var party = new[] { human, support, attacker };
        var run = RunState.CreateForTest(party, seed: "COORDINATOR");
        var combat = new CombatState(runState: run);
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetCurrentHpInternal(50);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
        support.Creature.GainBlockInternal(12);
        var shield = combat.CreateCard<DemonicShield>(support); support.PlayerCombatState!.Hand.AddInternal(shield);
        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!;
        var choose = planner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Plan() => choose.Invoke(null, new object?[] { new[] { support, attacker }, party, null });
        var team = Plan();
        Check(team is not null && (double)team.GetType().GetProperty("HpSaved")!.GetValue(team)! >= 8.9,
            "Normal bot must accept one HP of personal cost to save ten team HP even without a lethal threat.");
        Check((int)team!.GetType().GetProperty("DeathsPrevented")!.GetValue(team)! == 0,
            "Nonfatal sacrifice regression must measure total attrition, not merely emergency rescue.");
        var coordinator = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCoordinator")!;
        var select = coordinator.GetMethod("Select", BindingFlags.Static | BindingFlags.NonPublic)!;
        var strike = combat.CreateCard<StrikeIronclad>(attacker);
        var selfish = new List<(Player, BotBrain.CombatMove)> { (attacker, new(strike, enemy, 1e12)) };
        var decision = select.Invoke(null, new object?[] { team, null, selfish, false })!;
        Check(ReferenceEquals(decision.GetType().GetProperty("Player")!.GetValue(decision), support),
            "An arbitrarily high individual score must not override the selected joint plan.");
        support.Creature.SetCurrentHpInternal(1);
        Check(Plan() is null, "Do not kill the support merely to reduce nonfatal team damage.");
        var wait = select.Invoke(null, new object?[] { null, null, selfish, false })!;
        Check(wait.GetType().GetProperty("Move")!.GetValue(wait) is null,
            "A deliberate team wait must not fall back to a supported card's selfish score.");
        support.Creature.SetCurrentHpInternal(5);
        for (var i = 0; i < 20; i++) attacker.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(attacker));
        var relics = new RelicModel[] { ModelDb.Relic<Anchor>().ToMutable(), ModelDb.Relic<Vajra>().ToMutable() };
        var assign = coordinator.GetMethod("AssignRelics", BindingFlags.Static | BindingFlags.NonPublic)!;
        Dictionary<ulong, int?> Assign(HashSet<int> reserved) => (Dictionary<ulong, int?>)assign.Invoke(null,
            new object[] { new[] { support, attacker }, relics, reserved })!;
        var allocation = Assign(new());
        Check(allocation[support.NetId] == 0 && allocation[attacker.NetId] == 1,
            "Global relic assignment must send Anchor to fragile support and Vajra to attack-heavy teammate.");
        var reservedAllocation = Assign(new() { 0 });
        Check(!reservedAllocation.Values.Contains(0) && reservedAllocation.Values.Count(v => v == 1) == 1,
            "Human/previous votes must be reserved; remaining bots must not contest the same relic.");
        Check(Assign(new() { 0, 1 }).Values.All(v => v is null), "No remaining relics must yield safe skips.");
        var value = coordinator.GetMethod("CardValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Value(CardModel c) => ((ValueTuple<double, string>)value.Invoke(null, new object[] { c, support })!).Item1;
        var before = Value(shield);
        Check(before > Value(combat.CreateCard<StrikeIronclad>(support)), "Designated support should draft team protection before redundant basic offense.");
        support.Deck.AddInternal(shield);
        Check(Value(shield) < before, "Repeated team cards must have diminishing draft value.");
        Console.WriteLine("PASS: Normal-bot nonfatal sacrifice saves 9 total HP, suicidal sacrifice rejected, joint plan authority, deliberate wait, global relic fit/reservations, support drafting and diminishing duplicates.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
