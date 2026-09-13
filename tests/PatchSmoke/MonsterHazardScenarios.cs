using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class MonsterHazardScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Genius, 1, 1));
        var other = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Genius, 2, 2));
        var party = new[] { human, bot, other }; var bots = new[] { bot, other };
        var run = RunState.CreateForTest(party, seed: "SANDPIT-19"); var combat = new CombatState(runState: run);
        foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); }
        var enemy = combat.CreateCreature(ModelDb.Monster<TheInsatiable>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200);
        var asm = typeof(BotBrain).Assembly;
        var choose = asm.GetType("CoopBots.TeamCombatPlanner")!.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        var potions = asm.GetType("CoopBots.BotPotionPlanner")!.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Plan() => choose.Invoke(null, new object?[] { bots, party, null });
        CardModel? Card(object? plan) => plan is null ? null : ((BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!).Card;
        T Hand<T>(Player? p = null) where T : CardModel { p ??= bot; var c = combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(c); return c; }
        SandpitPower Pit(Player p, int amount) { var power = (SandpitPower)ModelDb.Power<SandpitPower>().ToMutable(); power.Target = p.Creature; power.ApplyInternal(enemy, amount, true); return power; }
        void Check(bool value, string message) { if (!value) throw new Exception("Sandpit: " + message); }
        void Reset(int remaining, int hp = 200, int incoming = 0, int energy = 1)
        {
            foreach (var power in enemy.Powers.ToArray()) power.RemoveInternal();
            foreach (var p in party)
            {
                foreach (var c in p.PlayerCombatState!.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(c);
                foreach (var c in p.PlayerCombatState.DrawPile.Cards.ToArray()) p.PlayerCombatState.DrawPile.RemoveInternal(c);
                foreach (var potion in p.Potions.ToArray()) p.DiscardPotionInternal(potion, silent: true);
                p.PlayerCombatState.LoseEnergy(p.PlayerCombatState.Energy); p.PlayerCombatState.GainEnergy(p == bot ? energy : 0);
                p.Creature.LoseBlockInternal(p.Creature.Block); p.Creature.SetCurrentHpInternal(80);
            }
            enemy.SetCurrentHpInternal(hp);
            enemy.Monster.SetMoveImmediate(new MoveState("TEST_MOVE", _ => Task.CompletedTask, new SingleAttackIntent(incoming)), true);
            if (remaining > 0) Pit(bot, remaining);
        }
        Reset(1); var escape = Hand<FranticEscape>(); Hand<DefendIronclad>(); Hand<StrikeIronclad>();
        bot.Creature.GainBlockInternal(999);
        Check(Card(Plan()) == escape, "Lethal countdown ignores 999 block and prioritizes escape.");
        Check(enemy.Powers.OfType<SandpitPower>().Single().Amount == 1 && bot.PlayerCombatState!.Energy == 1, "Search must not change live countdown or energy.");
        Reset(1, 6); Hand<FranticEscape>(); var strike = Hand<StrikeIronclad>();
        Check(Card(Plan()) == strike, "Confirmed boss kill removes countdown without wasting an escape.");
        Reset(1); Hand<DefendIronclad>();
        Check(Plan() is null, "Block cannot pretend to rescue forced death.");
        bot.AddPotionInternal(ModelDb.Potion<BlockPotion>().ToMutable()); bot.AddPotionInternal(ModelDb.Potion<BloodPotion>().ToMutable());
        bot.Creature.SetCurrentHpInternal(20);
        Check(potions.Invoke(null, new object[] { bots, party }) is null, "Block/heal potion cannot prevent forced death.");
        Reset(1, 10); bot.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable());
        Check(potions.Invoke(null, new object[] { bots, party }) is not null, "Lethal damage potion can remove the sandpit source.");
        Reset(2); escape = Hand<FranticEscape>(); Hand<StrikeIronclad>();
        Check(Card(Plan()) == escape, "Invest in a bounded escape reserve before the final turn.");
        Reset(4); Hand<FranticEscape>(); strike = Hand<StrikeIronclad>();
        Check(Card(Plan()) == strike, "Healthy countdown must not cause escape spam.");
        Reset(0); Hand<FranticEscape>(); Check(Plan() is null, "No sandpit means no escape benefit.");
        Reset(1); Pit(other, 4); Hand<FranticEscape>(other); other.PlayerCombatState!.GainEnergy(1);
        Check(Plan() is null, "Another bot's escape cannot rescue the doomed bot.");
        Reset(4); Pit(other, 1); escape = Hand<FranticEscape>(other); other.PlayerCombatState!.GainEnergy(1); Hand<StrikeIronclad>();
        Check(Card(Plan()) == escape, "Each bot's instanced countdown must be evaluated separately.");
        Reset(1, energy: 2); Hand<BattleTrance>(); escape = combat.CreateCard<FranticEscape>(bot); bot.PlayerCombatState!.DrawPile.AddInternal(escape);
        Check(Card(Plan()) is BattleTrance, "Draw a known escape to prevent forced death.");
        Reset(1); escape = Hand<FranticEscape>(); escape.EnergyCost.AddThisCombat(1); Hand<DefendIronclad>();
        Check(Plan() is null, "Read the actual increased cost; do not assume every escape costs one.");
        Reset(2);
        var nativePit = enemy.Powers.OfType<SandpitPower>().Single();
        nativePit.AfterSideTurnStartLate(CombatSide.Player, party.Select(p => p.Creature).ToArray(), combat).GetAwaiter().GetResult();
        Check(nativePit.Amount == 2, "Native countdown does not tick at player turn start.");
        nativePit.AfterSideTurnStartLate(CombatSide.Enemy, party.Select(p => p.Creature).ToArray(), combat).GetAwaiter().GetResult();
        Check(nativePit.Amount == 1, "Native enemy-turn start decrements countdown, before attack resolution.");
        Console.WriteLine("PASS: real TheInsatiable/Sandpit models, forced death, escape reserves, per-player instances, boss lethal, potion correctness, draw rescue and dynamic escape costs.");
    }
}
