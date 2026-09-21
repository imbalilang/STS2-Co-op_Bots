using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// B11, made deterministic.
///
/// The two-phase boss truncation was, until now, only reachable by playing a live run and
/// hoping: measured over six batches it fired in three of them and not in the other three, and
/// each attempt costs a full A10 run. This fixture builds the same board in-process so the
/// question "which site fails closed" has an answer that does not depend on a dice roll, and so
/// the answer stays pinned once it is known.
///
/// WHAT IT ASSERTS: the boundary a lethal hit on WaterfallGiant produces at the enemy phase.
/// That string is the evidence B11's protocol demands ("read the NAMED one, and only then touch
/// the settlement"), and pinning it as a regression guard means any future change to this path
/// has to say so out loud.
/// </summary>
internal static class KernelTwoPhaseBossScenarios
{
    internal static void Run()
    {
        var (combat, party, boss) = BossBoard();
        var branch = KernelSession.Capture(combat);
        // Kill the boss with a real card play, not by poking its HP. The SteamEruption
        // transition is driven by the DEATH SETTLEMENT, so the fixture has to go through the
        // same path the live game does — setting HP to zero would skip exactly the code under
        // test.
        var strike = branch.Hand(party[0]).First(c => c.GetType().Name == "StrikeIronclad");
        Check(branch.Play(strike, boss, out var playReason), "the lethal Strike was refused: " + playReason);
        var reason = "";
        foreach (var p in party) branch.EndTurn(p, out reason);
        // REGRESSION GUARD for the fix. Before it, this line read
        //   boundary='round-pending-choice@forced-move-unmodeled'
        // and every line that killed this boss became a boundary. The wind-up half of the
        // SteamEruption transition is a no-op, so a clean settle is the correct result and a
        // non-empty boundary here means the refusal has come back.
        Check(reason.Length == 0,
            $"the two-phase boss wind-up must NOT fail the round closed, but the settle reported "
            + $"'{reason}'. The forced-move handler is refusing the transition's first half again, "
            + "which is what made every killing line a boundary and stalled the fight.");
        Check(branch.Hp(boss) > 0,
            "the wind-up must not kill the boss. ABOUT_TO_BLOW_MOVE only prepares the eruption; "
            + "'MonsterMoveEffects' own case for it does that and nothing else.");
        Check(party.All(p => branch.Hp(p.Creature) == 100),
            "the wind-up must not damage the party; the explosion is the NEXT enemy phase, not "
            + "this one. Measured hp=" + string.Join('/', party.Select(p => branch.Hp(p.Creature))));
        Console.WriteLine("PASS: killing the two-phase boss no longer fails the round closed — the "
            + "SteamEruption wind-up settles as the no-op the model says it is, leaving the boss "
            + "alive to explode on the following enemy phase.");
    }

    private static void Check(bool condition, string failure)
    { if (!condition) throw new Exception(failure); }

    private static (CombatState Combat, Player[] Party, Creature Boss) BossBoard()
    {
        var party = Enumerable.Range(1, 3).Select(i =>
            Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(70 + i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "TWO-PHASE-BOSS"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(100); p.Creature.SetCurrentHpInternal(100);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            for (var i = 0; i < 5; i++)
                p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        var boss = combat.CreateCreature(ModelDb.Monster<WaterfallGiant>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(boss); boss.Monster!.SetUpForCombat();
        // Low enough that one Strike is lethal: the transition fires on death, so the fixture
        // needs a death, not a long fight.
        boss.SetMaxHpInternal(300); boss.SetCurrentHpInternal(6);
        boss.Monster.SetMoveImmediate((MoveState)boss.Monster.MoveStateMachine!.States["STOMP_MOVE"], true);
        // THE POWER IS THE WHOLE REPRODUCTION. The SteamEruption transition only fires when the
        // boss dies WITH SteamEruptionPower on it, and that power is applied by the boss's own
        // pressurise moves — which is exactly why the live truncation was intermittent: measured
        // over six batches it fired in three and not the other three, depending on whether the
        // boss had pressurised before it died. Without this line the fixture silently tests
        // nothing, which is how the first version of it passed while asserting the wrong thing.
        ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.SteamEruptionPower>()
            .ToMutable().ApplyInternal(boss, 3, true);
        return (combat, party, boss);
    }
}
