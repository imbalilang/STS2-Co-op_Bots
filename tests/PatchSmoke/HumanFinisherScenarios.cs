using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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

internal static class HumanFinisherScenarios
{
    internal static void Run()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 71);
        var other = Player.CreateForNewRun<Deprived>(UnlockState.all, 72);
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { human, other }, seed: "FINISHER"));
        foreach (var p in new[] { human, other })
        { p.ResetCombatState(); combat.AddPlayer(p); p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3); }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "finisher");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(5)), true);
        void Clear(int hp)
        {
            foreach (var p in new[] { human, other })
                foreach (var c in p.PlayerCombatState!.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(c);
            foe.SetMaxHpInternal(100); foe.SetCurrentHpInternal(hp); foe.LoseBlockInternal(foe.Block);
        }
        CardModel Give<T>(Player? p = null) where T : CardModel
        { p ??= human; var card = combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
        HumanFinisher.Plan? Find()
        {
            var stamp = KernelSession.CaptureLiveStamp(combat);
            var attempts = HumanFinisher.Search(KernelSession.Capture(combat), combat).ToList();
            if (attempts.Count > HumanFinisher.MaxBranches) throw new Exception("Finisher exceeded its branch budget.");
            if (KernelSession.CaptureLiveStamp(combat) != stamp) throw new Exception("Finisher changed the live game.");
            return attempts.LastOrDefault(p => p is not null);
        }
        Clear(6); var strike = Give<StrikeIronclad>();
        if (Find() is not { Cards.Count: 1, Energy: 1 } single || single.Cards[0] != strike)
            throw new Exception("Single Strike finisher missing.");
        Clear(12); Give<StrikeIronclad>(); Give<StrikeIronclad>();
        if (Find() is not { Cards.Count: 2, Energy: 2 }) throw new Exception("Two distinct Strikes must finish 12.");
        Clear(18); Give<StrikeIronclad>(); Give<StrikeIronclad>(); Give<StrikeIronclad>();
        if (Find() is not null) throw new Exception("Three-card/three-energy line incorrectly recommended.");
        Clear(10); Give<StrikeIronclad>(); Give<StrikeIronclad>(other);
        if (Find() is not null) throw new Exception("Different humans' damage was combined.");
        Clear(7); Give<StrikeIronclad>();
        if (Find() is not null) throw new Exception("The same card was reused.");
        Clear(6); Give<StrikeIronclad>(); foe.GainBlockInternal(2);
        if (Find() is not null) throw new Exception("Enemy block was ignored.");
        Clear(12); var trip = Give<Thunderclap>(); var hit = Give<StrikeIronclad>();
        var setup = Find();
        if (setup is null || setup.Cards.Count != 2 || setup.Cards[0] != trip || setup.Cards[1] != hit)
            throw new Exception("Vulnerable before attack must be verified in order.");
        Clear(20); Give<Bludgeon>();
        if (Find() is not null) throw new Exception("A three-energy card was accepted.");
        Clear(12); Give<StrikeIronclad>(); Give<StrikeIronclad>();
        human.PlayerCombatState!.LoseEnergy(human.PlayerCombatState.Energy);
        human.PlayerCombatState.GainEnergy(1);
        if (Find() is not null) throw new Exception("Two cards cannot spend unavailable energy.");
        human.PlayerCombatState.GainEnergy(2);
        Clear(6); Give<StrikeIronclad>();
        var ended = KernelSession.Capture(combat);
        if (!ended.EndTurn(human, out var endReason)) throw new Exception(endReason);
        if (HumanFinisher.Search(ended, combat).Any(p => p is not null))
            throw new Exception("An ended human cannot be asked to act.");
        Clear(6); Give<SwordBoomerang>();
        if (Find() is not null) throw new Exception("Seeded random targets must not be promised.");
        Clear(12); Give<PommelStrike>();
        human.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(human));
        if (Find() is not null) throw new Exception("Unknown future draws must not supply the second card.");
        Clear(6); Give<StrikeIronclad>();
        foe.Monster.SetMoveImmediate(new MoveState("WAIT", _ => Task.CompletedTask), true);
        if (Find() is not null) throw new Exception("Non-attacking enemies must not trigger a defence-sacrifice request.");
        Console.WriteLine("PASS: bounded finisher singles/pairs, ordered Vulnerable, block, gross energy, one human, distinct cards, attack intent, budget and no live mutation.");
    }
}
