using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class TurnBoundaryScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Genius, 1, 1));
        var party = new[] { human, bot }; var run = RunState.CreateForTest(party, seed: "BOUNDARY-18");
        var combat = new CombatState(runState: run);
        foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200);
        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!;
        var choose = planner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Plan() => choose.Invoke(null, new object?[] { new[] { bot }, party, null });
        BotBrain.CombatMove Move(object plan) => (BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!;
        T Hand<T>() where T : CardModel { var card = combat.CreateCard<T>(bot); bot.PlayerCombatState!.Hand.AddInternal(card); return card; }
        T Power<T>(Player owner, int amount) where T : PowerModel { var power = ModelDb.Power<T>().ToMutable(); power.ApplyInternal(owner.Creature, amount, true); return (T)power; }
        void Check(bool condition, string text) { if (!condition) throw new Exception("Boundary: " + text); }
        void Reset(int incoming, int energy = 3)
        {
            foreach (var p in party)
            {
                foreach (var power in p.Creature.Powers.ToList()) power.RemoveInternal();
                foreach (var card in p.PlayerCombatState!.Hand.Cards.ToList()) p.PlayerCombatState.Hand.RemoveInternal(card);
                p.Creature.SetCurrentHpInternal(80); p.Creature.LoseBlockInternal(p.Creature.Block);
                p.PlayerCombatState.LoseEnergy(p.PlayerCombatState.Energy); p.PlayerCombatState.GainEnergy(p == bot ? energy : 0);
            }
            human.Creature.GainBlockInternal(100);
            enemy.SetCurrentHpInternal(200); enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(incoming)), true);
        }
        Reset(0); bot.Creature.GainBlockInternal(20); var barricade = Hand<Barricade>();
        Check(Move(Plan()!).Card == barricade, "Save current surplus block for a continuing fight.");
        Check(bot.Creature.Block == 20 && !bot.Creature.Powers.Any(p => p is BarricadePower), "Future block projection must not modify live powers.");
        Reset(30); bot.Creature.GainBlockInternal(20); bot.Creature.SetCurrentHpInternal(5); Hand<Barricade>(); var defend = Hand<DefendIronclad>(); Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == defend || Move(Plan()!).Card is DefendIronclad, "Do not spend a rescue turn retaining block that incoming damage will consume.");
        Reset(1); for (var i = 0; i < 3; i++) bot.Deck.AddInternal(run.CreateCard<StrikeIronclad>(bot));
        var demon = Hand<DemonForm>(); Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == demon, "Healthy low-pressure turns may invest in actual next-turn Strength.");
        Reset(8); bot.Creature.SetCurrentHpInternal(5); Hand<DemonForm>(); defend = Hand<DefendIronclad>(); Hand<DefendIronclad>();
        Check(Move(Plan()!).Card is DefendIronclad, "Future Strength cannot justify dying this turn.");
        Reset(4, 1); bot.Creature.SetCurrentHpInternal(4); Power<PlatingPower>(bot, 4); var strike = Hand<StrikeIronclad>(); Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == strike, "Existing end-turn Plating already covers incoming damage.");
        Reset(4, 1); bot.Creature.SetCurrentHpInternal(4); Power<PlatingPower>(human, 8); Power<BeaconOfHopePower>(human, 1);
        strike = Hand<StrikeIronclad>(); Hand<DefendIronclad>();
        Check(Move(Plan()!).Card == strike, "End-turn Plating shared through Beacon can protect a teammate.");
        bot.AddPotionInternal(ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.BlockPotion>().ToMutable());
        var potionPlanner = typeof(BotBrain).Assembly.GetType("CoopBots.BotPotionPlanner")!.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(potionPlanner.Invoke(null, new object[] { new[] { bot }, party }) is null, "Do not spend a rescue potion when end-turn shared Plating already prevents death.");
        foreach (var potion in bot.Potions.ToArray()) bot.DiscardPotionInternal(potion, silent: true);
        Reset(10, 1); human.Creature.LoseBlockInternal(100); human.Creature.SetCurrentHpInternal(5);
        Power<PlatingPower>(bot, 20); Hand<DemonicShield>();
        Check(Plan() is null, "End-turn block must not become present-time block that DemonicShield can copy.");

        Reset(0); var plating = Power<PlatingPower>(bot, 5); var ritual = Power<RitualPower>(bot, 2);
        var form = Power<DemonFormPower>(bot, 3); var wall = Power<BarricadePower>(bot, 1);
        var boundary = typeof(BotBrain).Assembly.GetType("CoopBots.TurnBoundaryProjection")!;
        var advance = boundary.GetMethod("Advance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = advance.Invoke(null, new object[] { 20d, 5d, 3d, 0d, 2d, 3d, 5d, true })!;
        double Value(string name) => (double)result.GetType().GetProperty(name)!.GetValue(result)!;
        var choice = new ThrowingPlayerChoiceContext();
        plating.BeforeSideTurnEndEarly(choice, CombatSide.Player, party.Select(p => p.Creature)).GetAwaiter().GetResult();
        Check(bot.Creature.Block == 5, "Native Plating must grant the projected five end-turn block.");
        ritual.AfterSideTurnEnd(choice, CombatSide.Player, party.Select(p => p.Creature)).GetAwaiter().GetResult();
        bot.Creature.LoseBlockInternal(3); // Controlled incoming hit; native power callbacks are the differential subject.
        if (Hook.ShouldClearBlock(combat, bot.Creature, out _)) bot.Creature.LoseBlockInternal(bot.Creature.Block);
        bot.PlayerCombatState!.IncrementTurnNumber();
        form.AfterSideTurnStart(CombatSide.Player, party.Select(p => p.Creature).ToArray(), combat).GetAwaiter().GetResult();
        plating.AfterSideTurnStart(CombatSide.Player, party.Select(p => p.Creature).ToArray(), combat).GetAwaiter().GetResult();
        Check(bot.Creature.Block == (decimal)Value("Block") && bot.Creature.Powers.OfType<StrengthPower>().Single().Amount == (decimal)Value("Strength")
            && plating.Amount == (decimal)Value("Plating"), "Native Ritual/DemonForm/Plating/Barricade boundary results must match projection.");
        result = advance.Invoke(null, new object[] { 2d, 0d, 3d, 0d, 2d, 3d, 5d, true })!;
        Check(Value("Hp") == 0 && Value("Strength") == 0 && Value("Block") == 0, "Dead actors have no next-turn resources.");
        Console.WriteLine("PASS: retained block, safe growth investment, current-turn rescue, Plating/Beacon defense, native boundary power callbacks and no post-death resources.");
    }
}
