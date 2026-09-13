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

internal static class ResourceScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var party = new[] { human, bot }; var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "RESOURCES-17"));
        foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200);
        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!;
        var choose = planner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Plan() => choose.Invoke(null, new object?[] { new[] { bot }, party, null });
        BotBrain.CombatMove Move(object plan) => (BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!;
        int Count(object plan) => (int)plan.GetType().GetProperty("PlannedCards")!.GetValue(plan)!;
        T Hand<T>() where T : CardModel { var card = combat.CreateCard<T>(bot); bot.PlayerCombatState!.Hand.AddInternal(card); return card; }
        T Draw<T>() where T : CardModel { var card = combat.CreateCard<T>(bot); bot.PlayerCombatState!.DrawPile.AddInternal(card); return card; }
        void Check(bool ok, string message) { if (!ok) throw new Exception("Resources: " + message); }
        void Reset(int energy, int enemyHp = 100)
        {
            foreach (var pile in new[] { bot.PlayerCombatState!.Hand, bot.PlayerCombatState.DrawPile, bot.PlayerCombatState.DiscardPile, bot.PlayerCombatState.ExhaustPile })
                foreach (var card in pile.Cards.ToList()) pile.RemoveInternal(card);
            foreach (var power in bot.Creature.Powers.ToList()) power.RemoveInternal();
            bot.Creature.SetCurrentHpInternal(80); bot.Creature.LoseBlockInternal(bot.Creature.Block);
            bot.PlayerCombatState.LoseEnergy(bot.PlayerCombatState.Energy); bot.PlayerCombatState.GainEnergy(energy);
            enemy.SetCurrentHpInternal(enemyHp); enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(0)), true);
        }
        Reset(1, 6); var trance = Hand<BattleTrance>(); var strike = Draw<StrikeIronclad>();
        var plan = Plan()!;
        Check(Move(plan).Card == trance && Count(plan) == 2 && Move(plan).Reason.Contains("confirmed-team-lethal"), "Draw must unlock the actual attack rather than act as a flat setup bonus.");
        Check(bot.PlayerCombatState!.DrawPile.Cards.Single() == strike && bot.PlayerCombatState.Hand.Cards.Single() == trance && bot.PlayerCombatState.Energy == 1,
            "Search must not draw or spend resources in the real battle.");
        var noDraw = ModelDb.Power<NoDrawPower>().ToMutable(); noDraw.ApplyInternal(bot.Creature, 1, true);
        Check(Plan() is null, "Existing NoDraw must not invent a draw-assisted lethal.");
        Reset(0, 12); var offering = Hand<Offering>(); Draw<StrikeIronclad>(); Draw<StrikeIronclad>();
        plan = Plan()!;
        Check(Move(plan).Card == offering && Count(plan) == 3 && Move(plan).Reason.Contains("confirmed-team-lethal"), "Offering must pay HP, generate energy and unlock two drawn attacks.");
        bot.Creature.SetCurrentHpInternal(6);
        Check(Plan() is null, "Offering must not execute after its owner pays lethal HP.");
        Reset(3); var corruption = Hand<Corruption>(); Hand<ShrugItOff>(); Hand<ShrugItOff>(); Hand<ShrugItOff>(); Hand<DefendIronclad>();
        enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(29)), true);
        human.Creature.GainBlockInternal(100); bot.Creature.SetCurrentHpInternal(29);
        plan = Plan()!;
        Check(Move(plan).Card == corruption && Count(plan) == 5, "Corruption must make the four remaining skills free and rescue the caster.");
        Check(bot.PlayerCombatState.Energy == 3 && !bot.Creature.Powers.Any(p => p is CorruptionPower), "Projected Corruption must not mutate live costs or powers.");
        Reset(1); Hand<BattleTrance>(); Draw<BattleTrance>(); Draw<StrikeIronclad>(); Draw<StrikeIronclad>(); Draw<Bludgeon>();
        Check(Count(Plan()!) <= 3, "A drawn second BattleTrance cannot bypass the first one's NoDraw.");
        Reset(1); Hand<BattleTrance>(); bot.PlayerCombatState.DiscardPile.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
        Check(Plan() is null, "An empty draw pile must stop at shuffle rather than invent a favorable reshuffle.");
        Reset(1); Hand<BattleTrance>(); Draw<Burn>(); Draw<StrikeIronclad>();
        Check(Plan() is null, "An unknown drawn-card effect must be a boundary, not a silently skipped card.");
        Reset(1);
        var ordered = new CardModel[] { Draw<StrikeIronclad>(), Draw<DefendIronclad>(), Draw<Bash>() };
        var projection = typeof(BotBrain).Assembly.GetType("CoopBots.CombatResourceProjection")!;
        const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var snapshot = projection.GetMethod("Capture", methods)!.Invoke(null, new object[] { new[] { bot }, ordered })!;
        var root = snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        var fork = root.GetType().GetMethod("Fork", fields)!;
        var left = fork.Invoke(root, null)!; var right = fork.Invoke(root, null)!;
        var drawMethod = projection.GetMethod("Draw", methods)!;
        Check((int)drawMethod.Invoke(null, new object[] { left, snapshot, 0, 2 })! == 2, "Resource branch must draw two cards.");
        ulong Available(object state) => (ulong)state.GetType().GetField("Available", fields)!.GetValue(state)!;
        Check(Available(left) == 3 && Available(right) == 0 && Available(root) == 0, "Sibling draw branches must not share mutations.");
        var actual = MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(
            new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), 2, bot).GetAwaiter().GetResult().ToArray();
        Check(actual.SequenceEqual(ordered.Take(2)) && bot.PlayerCombatState.Hand.Cards.SequenceEqual(ordered.Take(2))
            && bot.PlayerCombatState.DrawPile.Cards.SequenceEqual(ordered.Skip(2)), "Original draw command and projection must agree on ordered card instances.");
        Console.WriteLine("PASS: native CardPileCmd.Draw matches projected order/count; root and sibling branch remain isolated.");
        Reset(1); var capTrance = Hand<BattleTrance>();
        for (var i = 0; i < 9; i++) Hand<StrikeIronclad>();
        Draw<StrikeIronclad>(); Draw<DefendIronclad>(); Draw<Bash>();
        var capCards = bot.PlayerCombatState.Hand.Cards.Concat(bot.PlayerCombatState.DrawPile.Cards).ToArray();
        snapshot = projection.GetMethod("Capture", methods)!.Invoke(null, new object[] { new[] { bot }, capCards })!;
        root = snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        var specs = (Array)snapshot.GetType().GetProperty("Cards")!.GetValue(snapshot)!;
        projection.GetMethod("BeginPlay", methods)!.Invoke(null, new[] { root, (object)0, specs.GetValue(0)! });
        Check((int)drawMethod.Invoke(null, new object[] { root, snapshot, 0, 3 })! == 1, "Playing from a full hand opens only one draw slot.");
        bot.PlayerCombatState.Hand.RemoveInternal(capTrance);
        actual = MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(
            new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), 3, bot).GetAwaiter().GetResult().ToArray();
        Check(actual.Length == 1 && bot.PlayerCombatState.Hand.Cards.Count == 10, "Native full-hand draw must match projected cap.");
        Reset(3); var skill = Hand<ShrugItOff>();
        snapshot = projection.GetMethod("Capture", methods)!.Invoke(null, new object[] { new[] { bot }, new CardModel[] { skill } })!;
        root = snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        ((bool[])root.GetType().GetField("Corruption", fields)!.GetValue(root)!)[0] = true;
        specs = (Array)snapshot.GetType().GetProperty("Cards")!.GetValue(snapshot)!;
        var projectedCost = (int)projection.GetMethod("Cost", methods)!.Invoke(null, new[] { root, specs.GetValue(0)!, (object)1 })!;
        var liveCorruption = ModelDb.Power<CorruptionPower>().ToMutable(); liveCorruption.ApplyInternal(bot.Creature, 1, true);
        Check(projectedCost == 0 && skill.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.All) == projectedCost,
            "Projected Corruption skill cost must match the native cost hook.");
        projection.GetMethod("BeginPlay", methods)!.Invoke(null, new[] { root, (object)0, specs.GetValue(0)! });
        Check((ulong)root.GetType().GetField("Exhausted", fields)!.GetValue(root)! == 1
            && (ulong)root.GetType().GetField("Discarded", fields)!.GetValue(root)! == 0, "A Corruption skill must enter the projected exhaust set, not discard.");
        Reset(1); var fallbackTrance = Hand<BattleTrance>(); Draw<Burn>(); Draw<StrikeIronclad>();
        var fallback = typeof(BotBrain).GetMethod("ChooseEffectFallback", methods)!;
        Check(((BotBrain.CombatMove?)fallback.Invoke(null, new object[] { bot, 0 }))?.Card == fallbackTrance,
            "Unknown draw outcomes may use a safe live one-action fallback instead of becoming permanently unusable.");
        Console.WriteLine("PASS: full-hand native draw cap, native Corruption cost, projected exhaustion and unknown-draw fallback.");
        Console.WriteLine("PASS: draw-to-lethal, NoDraw, Offering resources/HP, Corruption free-skill rescue, sequential NoDraw, shuffle/unknown boundaries and live-state isolation.");
    }
}
