using System.Diagnostics;
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

internal static class PlannerPerformanceScenarios
{
    internal static void Run(bool withDraw = false, bool withPlating = false)
    {
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            i == 0 ? 1UL : BotRegistry.CreateId(BotDifficulty.Pro, i, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "PERFORMANCE-OPENING"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            if (withPlating)
                ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.PlatingPower>().ToMutable().ApplyInternal(p.Creature, 5, true);
            void Add<T>() where T : CardModel => p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<T>(p));
            Add<StrikeIronclad>(); Add<TwinStrike>(); Add<DefendIronclad>(); Add<Bash>();
            if (withDraw)
            {
                Add<Offering>();
                for (var i = 0; i < 6; i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            }
            else Add<DemonicShield>();
        }
        for (var i = 0; i < 3; i++)
        {
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
            enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
            enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(8)), true);
        }
        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!;
        var choose = planner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        var bots = party.Skip(1).ToArray();
        var times = new List<double>(); var bytes = new List<long>(); string? expected = null;
        for (var i = 0; i < 6; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            var decision = choose.Invoke(null, new object?[] { bots, party, null })!;
            watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (decision is null) throw new Exception("Opening benchmark produced no legal action.");
            var move = (BotBrain.CombatMove)decision.GetType().GetProperty("Move")!.GetValue(decision)!;
            var result = $"{move.Card.Owner.NetId}/{move.Card.GetType().Name}/{move.Target?.CombatId}/{move.Score:R}/{move.Reason}";
            expected ??= result;
            if (result != expected) throw new Exception("Repeated identical opening changed its decision.");
            if (party.Any(p => p.PlayerCombatState!.Energy != 3 || p.PlayerCombatState.Hand.Cards.Count != 5 || p.Creature.CurrentHp != 80)
                || combat.HittableEnemies.Any(e => e.CurrentHp != 100))
                throw new Exception("Planning mutated the live opening state.");
            if (i > 0) { times.Add(watch.Elapsed.TotalMilliseconds); bytes.Add(allocated); }
        }
        times.Sort();
        Console.WriteLine($"PERF: 4 seats, 3 bots, 20 hand cards, 3 enemies, drawProjection={withDraw}, endPlating={withPlating}; warm n=5 median={times[2]:F1}ms max={times[^1]:F1}ms allocatedMean={bytes.Average() / 1048576:F2}MiB; {expected}");
        var stats = planner.GetProperty("LastSearchStatistics", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (stats is not null)
        {
            int Count(string name) => (int)stats.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stats)!;
            if (Count("Evaluations") > Count("ExpandedNodes") + 1 || Count("EvaluationCacheHits") <= 0)
                throw new Exception("Each immutable node must be evaluated at most once per search.");
            Console.WriteLine($"PERF: nodes={Count("ExpandedNodes")}; evaluations={Count("Evaluations")}; cacheHits={Count("EvaluationCacheHits")}; damageHooks={Count("DamageHookCalls")}; blockProjections={Count("BlockProjectionCalls")}");
        }
        foreach (var bot in bots) bot.PlayerCombatState!.LoseEnergy(3);
        if (!withDraw && choose.Invoke(null, new object?[] { bots, party, null }) is not null)
            throw new Exception("A later search reused a plan after its energy became unavailable.");
        foreach (var bot in bots) bot.PlayerCombatState!.GainEnergy(3);
        Console.WriteLine("PASS: opening search is deterministic and leaves live resources, hands and HP unchanged.");
        if (!withDraw) Run(withDraw: true);
        else if (!withPlating) Run(withDraw: true, withPlating: true);
    }
}
