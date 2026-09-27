using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class JointSearchScenarios
{
    // R2 2026-09-27: work/strength-20260927-v6/r2-structural.json. Every
    // mutation exits 1 and is restored. Actual exception messages include:
    // joint search must coordinate two teammates' Bash setups before spending both Strikes
    // joint search must plan and charge both potions needed for a win
    // joint prefixes and continuations must preserve action-aligned snapshots
    // joint script alive snapshots must describe before-action state
    // joint script enemy snapshots must describe before-action state
    // joint script must replay: invalid-target
    // joint replay must reach the predicted victory
    // a non-winning joint continuation must search again from the next live round
    internal static void Run()
    {
        var (party, combat) = Board(2, 17);
        party[0].PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(party[0]));
        party[0].PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(party[0]));
        for (var i = 1; i < 3; i++) party[i].PlayerCombatState!.Hand.AddInternal(combat.CreateCard<Bash>(party[i]));
        var root = KernelSession.Capture(combat);
        var opts = new TournamentOptions(TopK: 40, IncludePotions: false);
        var old = KernelTournament.Run(root, party, party, opts, 30000);
        var joined = KernelTournament.Run(root, party, party, opts with { EnableJointSearch = true }, 12000);
        // V13 intentionally replaces the old fixed-seat loss expectation: both
        // the cheap continuation and joint search now coordinate all four seats.
        Check(old.Record is { Verified: true, Victory: true }, "team-wide baselines must win the two-target setup fixture");
        Check(joined.Record is { Verified: true, Victory: true },
            "joint search must coordinate two teammates' Bash setups before spending both Strikes");
        Replay(root, joined, party);
        Console.WriteLine($"PASS: global rollout and joint seat ordering win the two-target setup (nodes={joined.JointNodes}, prefixes={joined.JointPrefixes}).");
        var planner = new KernelCombatPlanner();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(KernelCombatPlanner).GetMethod("AdoptTournamentScript", flags)!.Invoke(planner,
            [joined with { Record = joined.Record! with { Victory = false } }, combat]);
        combat.RoundNumber++;
        object?[] args = [combat, party, true, null];
        var emitted = (bool)typeof(KernelCombatPlanner).GetMethod("TryEmitFromTournamentScript", flags)!.Invoke(planner, args)!;
        Check(!emitted && !planner.HasPendingStep,
            "a non-winning joint continuation must search again from the next live round");
        Console.WriteLine("PASS: non-winning joint plans reconsider each new live round.");

        var (bottlers, potionCombat) = Board(1, 40);
        bottlers[0].AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), silent: true);
        bottlers[0].AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), silent: true);
        var bottleRoot = KernelSession.Capture(potionCombat);
        var bottleOptions = new TournamentOptions(TopK: 20, IncludePotions: true, PotionsOnly: true,
            Objective: KernelPotionPolicy.Objective);
        var single = KernelTournament.Run(bottleRoot, bottlers, bottlers, bottleOptions, 30000);
        var combo = KernelTournament.Run(bottleRoot, bottlers, bottlers,
            bottleOptions with { EnableJointSearch = true }, 12000);
        Check(single.Record is { Victory: false }, "one-bottle continuation must not win the two-bottle fixture");
        Check(combo.Record is { Victory: true, Verified: true, ResourcesConsumed: 2 }
            && combo.Script!.Count(a => a.Potion is not null) == 2,
            "joint search must plan and charge both potions needed for a win");
        Replay(bottleRoot, combo, bottlers);
        Console.WriteLine("PASS: multiple potion actions form one winning, replayable, resource-priced plan.");
    }

    private static void Replay(KernelSession root, TournamentResult result, Player[] party)
    {
        var state = root.Fork(); var path = result.Script!;
        Check(path.Count == result.ScriptAliveStates!.Count && path.Count == result.ScriptEnemyStates!.Count,
            "joint prefixes and continuations must preserve action-aligned snapshots");
        for (var i = 0; i < path.Count; i++)
        {
            Check(result.ScriptAliveStates[i].SequenceEqual(party.Where(p => state.Hp(p.Creature) > 0)
                    .Select(p => p.NetId).OrderBy(n => n)), "joint script alive snapshots must describe before-action state");
            Check(result.ScriptEnemyStates[i].SequenceEqual(state.Enemies), "joint script enemy snapshots must describe before-action state");
            Check(state.ReplayPlanned(path[i], 80, out var reason), "joint script must replay: " + reason);
        }
        Check(state.HasWon, "joint replay must reach the predicted victory");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static (Player[], CombatState) Board(int count, int hp)
    {
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 24, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "JOINT-TEAM-TURN"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(1);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(2);
        }
        for (var i = 0; i < count; i++)
        {
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat(); foe.SetMaxHpInternal(hp); foe.SetCurrentHpInternal(hp);
            foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
        }
        return (party, combat);
    }
}
