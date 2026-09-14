using System.Reflection;
using CoopBots;
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

// The full-value rule: a bottle is spent once its whole dose would land, not only
// when a death is on the line. Each case below pins one half of that sentence.
// The failure being guarded against is not a crash — it is a belt that stayed
// full all the way through a wipe, which is what two reviewed runs did.
internal static class PotionValueScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var first = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var second = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 2));
        var party = new[] { human, first, second };
        var bots = new[] { first, second };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "POTION-VALUE"));
        foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();

        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.BotPotionPlanner")!;
        var full = planner.GetMethod("EvaluateFullValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        var rescueOnly = planner.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic)!;

        object? Value(bool latePhase, bool block, bool lethal, bool noFuture = false)
            => full.Invoke(null, new object[] { bots, party, noFuture, latePhase, block, lethal });
        object? Rescue() => rescueOnly.Invoke(null, new object[] { bots, party });
        static string? Reason(object? choice)
            => choice?.GetType().GetProperty("Reason")!.GetValue(choice) as string;
        static bool Preemptive(object? choice)
            => choice is not null && (bool)choice.GetType().GetProperty("Preemptive")!.GetValue(choice)!;
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Potion value: " + message); }
        T Hand<T>(Player p) where T : CardModel
        { var card = combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
        PotionModel Add<T>(Player p) where T : PotionModel
        { var potion = ModelDb.Potion<T>().ToMutable(); p.AddPotionInternal(potion, silent: true); return potion; }
        void Reset(int incoming, int enemyHp = 500)
        {
            foreach (var p in party)
            {
                foreach (var card in p.PlayerCombatState!.Hand.Cards.ToList()) p.PlayerCombatState.Hand.RemoveInternal(card);
                foreach (var potion in p.Potions.ToList()) p.DiscardPotionInternal(potion, silent: true);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
                p.Creature.LoseBlockInternal(p.Creature.Block);
                p.PlayerCombatState.Phase = PlayerTurnPhase.Play;
                p.PlayerCombatState.LoseEnergy(p.PlayerCombatState.Energy);
            }
            enemy.SetCurrentHpInternal(enemyHp);
            enemy.Monster!.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(incoming)), true);
        }

        // The headline case: nobody is dying, but the 12-point bottle would soak
        // every point of a 14-point hit, and the team has no card left to play.
        // That is the bottle at its ceiling, so waiting can only lose value.
        Reset(14);
        var block = Add<BlockPotion>(first);
        Check(Value(latePhase: true, block: true, lethal: false) is not null,
            "14 uncovered with a 12-point bottle must spend it once the team is out of cards.");
        Check(Reason(Value(true, true, false)) == "full-value-block",
            "the verdict must be the full-value one, not a rescue.");
        Check(first.Potions.Contains(block) && first.Creature.Block == 0,
            "the projection must not consume the real bottle or grant real block.");

        // Timing is the other half of the rule: mid-turn a card may still cover
        // the hit, which would turn the "full" dose into a partly wasted one.
        Check(Value(latePhase: false, block: true, lethal: false) is null,
            "the same board mid-turn must keep the bottle: a card may still cover the hit.");
        Check(Rescue() is null, "nobody is dying, so the rescue-only verdict must stay silent.");

        // Partial soak is not full value: 9 damage cannot use a 12-point bottle.
        Reset(9);
        Add<BlockPotion>(first);
        Check(Value(latePhase: true, block: true, lethal: false) is null,
            "9 incoming cannot spend a 12-point bottle in full.");

        // The rescue rule must survive the rewrite untouched.
        Reset(14);
        Add<BlockPotion>(first);
        first.Creature.SetCurrentHpInternal(5);
        Check(Rescue() is not null && Reason(Rescue()) == "prevent-death",
            "potion-plus-defense rescue must keep working.");

        // Worthless for the ally it cannot save — and the same bottle is still
        // worth drinking for the two allies it does save, which is the point of
        // the rule rather than a regression.
        Reset(40);
        first.Creature.SetCurrentHpInternal(5);
        var hopeless = Add<BlockPotion>(first);
        hopeless.DynamicVars["Block"].BaseValue = 5;
        Check(Rescue() is null,
            "a 5-point bottle against 40 damage that still kills must stay corked.");
        Check(Value(latePhase: true, block: true, lethal: false) is not null,
            "the same bottle is worth drinking for an ally that survives it.");

        // A heal only counts once it stops being an overheal.
        Reset(4);
        Add<BloodPotion>(first);
        first.Creature.SetCurrentHpInternal(74);
        Check(Value(latePhase: true, block: true, lethal: false) is null,
            "a bottle that would overheal must stay corked.");
        Reset(4);
        Add<BloodPotion>(first);
        first.Creature.SetCurrentHpInternal(50);
        Check(Reason(Value(true, true, false)) == "full-value-heal",
            "a heal with room to land in full is worth drinking.");

        // Lethal, front-loaded: the throw is the only thing on the board that
        // kills, so the cards keep their damage for something else.
        Reset(4, enemyHp: 18);
        Add<FirePotion>(second);
        Check(Reason(Value(latePhase: false, block: false, lethal: true)) == "full-value-lethal",
            "a 20-point bottle that kills an 18 HP enemy the cards cannot must be thrown.");
        Check(Preemptive(Value(false, false, true)),
            "the lethal throw must outrank a card plan rather than wait behind it.");

        Reset(4, enemyHp: 18);
        Add<FirePotion>(second);
        Hand<StrikeIronclad>(first); Hand<StrikeIronclad>(first); Hand<StrikeIronclad>(first);
        first.PlayerCombatState!.GainEnergy(3);
        Check(Value(false, false, true) is null,
            "a kill the hand already has must keep the bottle.");

        // The three bottles both reviewed runs died holding. A card-generation
        // potion cannot save anyone, so there is no rescue value to hoard for and
        // the slot is dead weight — but it is still only worth a tempo throw in a
        // fight worth spending on, not every trash mob.
        Reset(4);
        Add<AttackPotion>(second);
        Check(Reason(Value(false, false, true, noFuture: true)) == "card-generation-tempo",
            "a card-generation bottle must be spent once there is nothing left to save it for.");
        Check(Value(false, false, true) is null,
            "an ordinary fight must not burn it.");

        // With the rule off, every case above collapses back to rescue-only.
        Reset(14);
        Add<BlockPotion>(first);
        Check(Rescue() is null && Value(false, false, false) is null,
            "the full-value rule must be opt-in; the old contract cannot drift.");

        Console.WriteLine("PASS: potion full-value timing, partial soak, overheal, preserved rescue, "
            + "lethal throw, tempo bottle and hand-already-kills conservation.");
    }
}
