using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ImportedCardRelicScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var party = new[] { human, bot }; var bots = new[] { bot };
        var run = RunState.CreateForTest(party, seed: "PORT-20"); var combat = new CombatState(runState: run);
        foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(300);
        var asm = typeof(BotBrain).Assembly;
        var choose = asm.GetType("CoopBots.TeamCombatPlanner")!.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Plan() => choose.Invoke(null, new object?[] { bots, party, null });
        BotBrain.CombatMove Move(object plan) => (BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!;
        bool Lethal(object? plan) => plan is not null && Move(plan).Reason.Contains("confirmed-team-lethal");
        int Count(object plan) => (int)plan.GetType().GetProperty("PlannedCards")!.GetValue(plan)!;
        T Hand<T>(Player? p = null) where T : CardModel { p ??= bot; var c = combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(c); return c; }
        T Power<T>(Player p, int amount) where T : PowerModel { var power = ModelDb.Power<T>().ToMutable(); power.ApplyInternal(p.Creature, amount, true); return (T)power; }
        T Relic<T>(Player p, int count) where T : RelicModel
        {
            var r = ModelDb.Relic<T>().ToMutable(); p.AddRelicInternal(r);
            var field = r.GetType().GetField(r is Nunchaku ? "_attacksPlayed" : "_attacksPlayedThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(r, count); return (T)r;
        }
        void Check(bool value, string message) { if (!value) throw new Exception("Imported: " + message); }
        void Reset(int hp = 200, int incoming = 0, int energy = 3)
        {
            foreach (var p in party)
            {
                foreach (var c in p.PlayerCombatState!.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(c);
                foreach (var c in p.PlayerCombatState.DrawPile.Cards.ToArray()) p.PlayerCombatState.DrawPile.RemoveInternal(c);
                foreach (var power in p.Creature.Powers.ToArray()) power.RemoveInternal();
                foreach (var relic in p.Relics.ToArray()) p.RemoveRelicInternal(relic);
                p.Creature.LoseBlockInternal(p.Creature.Block); p.Creature.SetCurrentHpInternal(80);
                p.PlayerCombatState.LoseEnergy(p.PlayerCombatState.Energy); p.PlayerCombatState.GainEnergy(p == bot ? energy : 0);
            }
            human.Creature.GainBlockInternal(999); enemy.SetCurrentHpInternal(hp);
            enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(incoming)), true);
        }

        Reset(20); bot.Creature.GainBlockInternal(10); var entrench = Hand<Entrench>(); Hand<BodySlam>();
        var plan = Plan(); Check(Lethal(plan) && Move(plan!).Card == entrench && Count(plan!) == 2, "Entrench into BodySlam must use branch block for a confirmed lethal.");
        Check(bot.Creature.Block == 10 && bot.PlayerCombatState!.Energy == 3, "Search must not double real block or spend energy.");
        Reset(5, energy: 1); var defend = Hand<DefendIronclad>(); var slam = Hand<BodySlam>(); slam.UpgradeInternal();
        Check(Lethal(Plan()) && Move(Plan()!).Card == defend, "A zero-root-damage BodySlam becomes lethal after Defend.");
        Reset(incoming: 10, energy: 1); bot.Creature.SetCurrentHpInternal(5); Power<CorruptionPower>(bot, 1);
        var fnp = Hand<FeelNoPain>(); Hand<DefendIronclad>();
        plan = Plan(); Check(plan is not null && Move(plan).Card == fnp && Count(plan) == 2, "Install FeelNoPain before the free exhausted Defend to survive ten incoming.");
        Reset(incoming: 10, energy: 0); bot.Creature.SetCurrentHpInternal(5); Power<CorruptionPower>(bot, 1); Power<FeelNoPainPower>(human, 3); Hand<DefendIronclad>();
        plan = Plan(); Check(plan is null || (int)plan.GetType().GetProperty("DeathsPrevented")!.GetValue(plan)! == 0, "A human's FeelNoPain must not trigger for the bot's exhausted card.");
        Reset(6, energy: 3); Power<CorruptionPower>(bot, 1); var dark = Hand<DarkEmbrace>(); Hand<DefendIronclad>();
        bot.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
        plan = Plan(); Check(Lethal(plan) && Move(plan!).Card == dark && Count(plan!) == 3, "DarkEmbrace -> exhausted Defend -> drawn Strike must be searched.");
        Reset(6, energy: 1); Power<CorruptionPower>(bot, 1); Power<DarkEmbracePower>(bot, 1); Power<NoDrawPower>(bot, 1); Hand<DefendIronclad>();
        bot.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
        Check(!Lethal(Plan()), "NoDraw also blocks DarkEmbrace's draw.");
        Reset(12, energy: 3); var backflip = Hand<Backflip>();
        bot.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
        bot.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
        Check(Lethal(Plan()) && Move(Plan()!).Card == backflip, "Backflip must expose both drawn Strikes to the team search.");
        Reset(4, energy: 0); var finesse = Hand<Finesse>(); var drawnSlam = combat.CreateCard<BodySlam>(bot); drawnSlam.UpgradeInternal();
        bot.PlayerCombatState!.DrawPile.AddInternal(drawnSlam);
        Check(Lethal(Plan()) && Move(Plan()!).Card == finesse, "Finesse grants block before the drawn zero-cost BodySlam.");
        Reset(9, energy: 1); var flash = Hand<FlashOfSteel>(); bot.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
        Check(Lethal(Plan()) && Move(Plan()!).Card == flash, "FlashOfSteel's damage and draw both contribute to lethal.");
        Reset(incoming: 36, energy: 2); bot.Creature.SetCurrentHpInternal(1); Power<FeelNoPainPower>(bot, 6); Hand<Impervious>();
        plan = Plan(); Check(plan is not null && (int)plan.GetType().GetProperty("DeathsPrevented")!.GetValue(plan)! == 1,
            "Impervious exhausts after its 30 block and gains six FeelNoPain block.");

        Reset(13, energy: 2); var shuriken = Relic<Shuriken>(bot, 2); Hand<StrikeIronclad>(); Hand<StrikeIronclad>();
        Check(Lethal(Plan()), "Third attack grants Strength only to the following attack (6+7).");
        Check((int)typeof(Shuriken).GetField("_attacksPlayedThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shuriken)! == 2,
            "Search must not change the real relic counter.");
        Reset(13, energy: 2); Relic<Shuriken>(bot, 1); Hand<StrikeIronclad>(); Hand<StrikeIronclad>();
        Check(!Lethal(Plan()), "The threshold attack must not retroactively gain its own Strength (6+6).");
        Reset(incoming: 11, energy: 2); bot.Creature.SetCurrentHpInternal(6); Relic<Kunai>(bot, 2); var strike = Hand<StrikeIronclad>(); Hand<DefendIronclad>();
        plan = Plan(); Check(plan is not null && Move(plan).Card == strike && (int)plan.GetType().GetProperty("DeathsPrevented")!.GetValue(plan)! == 1,
            "Trigger Kunai before Defend to save the bot.");
        Reset(incoming: 7, energy: 1); bot.Creature.SetCurrentHpInternal(4); Relic<OrnamentalFan>(bot, 2); Hand<StrikeIronclad>();
        plan = Plan(); Check(plan is not null && (int)plan.GetType().GetProperty("DeathsPrevented")!.GetValue(plan)! == 1, "Fan's fourth block point prevents death.");
        Reset(12, energy: 1); Relic<Nunchaku>(bot, 9); Hand<StrikeIronclad>(); Hand<StrikeIronclad>();
        Check(Lethal(Plan()), "Tenth attack returns energy for the next Strike.");
        Reset(16, energy: 1); Relic<Nunchaku>(bot, 8); Hand<TwinStrike>(); Hand<StrikeIronclad>();
        Check(!Lethal(Plan()), "TwinStrike's two hits advance Nunchaku only once.");
        Reset(12, energy: 1); Relic<Nunchaku>(human, 9); Hand<StrikeIronclad>(); Hand<StrikeIronclad>();
        Check(!Lethal(Plan()), "Human Nunchaku does not grant the bot energy.");

        // Native exhaust hook differential: these callbacks execute without the
        // visual action queue; compare owner-only block and exact drawn identity.
        Reset(energy: 1); var nativeFnp = Power<FeelNoPainPower>(bot, 3); var nativeDark = Power<DarkEmbracePower>(bot, 1);
        var exhausted = Hand<DefendIronclad>(); var drawn = combat.CreateCard<StrikeIronclad>(bot); bot.PlayerCombatState!.DrawPile.AddInternal(drawn);
        var choice = new ThrowingPlayerChoiceContext();
        nativeFnp.AfterCardExhausted(choice, exhausted, false).GetAwaiter().GetResult();
        nativeDark.AfterCardExhausted(choice, exhausted, false).GetAwaiter().GetResult();
        Check(bot.Creature.Block == 3 && bot.PlayerCombatState.Hand.Cards.Contains(drawn), "Native exhaust hooks must grant three unpowered block and draw the exact top card.");
        Reset(energy: 0);
        var nativeFan = Relic<OrnamentalFan>(bot, 2); var nativeKunai = Relic<Kunai>(bot, 2);
        var nativeShuriken = Relic<Shuriken>(bot, 2); var nativeNunchaku = Relic<Nunchaku>(bot, 9);
        var played = Hand<TwinStrike>();
        var cardPlay = new CardPlay { Card = played, Player = bot, Target = enemy, ResultPile = PileType.Discard,
            Resources = default, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        foreach (var relic in new RelicModel[] { nativeFan, nativeKunai, nativeShuriken, nativeNunchaku })
            relic.AfterCardPlayed(choice, cardPlay).GetAwaiter().GetResult();
        Check(bot.Creature.Block == 4 && bot.PlayerCombatState!.Energy == 1
            && bot.Creature.Powers.OfType<StrengthPower>().Single().Amount == 1
            && bot.Creature.Powers.OfType<DexterityPower>().Single().Amount == 1,
            "Native four relic callbacks match the imported threshold effects, once for the multi-hit card.");
        Console.WriteLine("PASS: imported Entrench/BodySlam, FeelNoPain/DarkEmbrace, Shuriken/Kunai/Fan/Nunchaku; ordering, owner isolation, real counters, no live mutation and native exhaust differential.");
    }
}
