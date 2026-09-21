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
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// P3-04 acceptance: a pair may only be treated as order-independent when BOTH orders were
/// simulated and left the same state (STS2_Bot_Solver_Implementation_Plan_v1.0 §P3-04).
///
/// The negative cases matter more than the positive one. A false "commutes" silently changes
/// what a plan does, and the plan names the exact mechanisms to be suspicious of: 共享触发、
/// RNG 消费、目标死亡和条件效果. Case (2) is one of those — Vulnerable.
/// </summary>
internal static class KernelCommutationScenarios
{
    internal static void Run()
    {
        ASharedEnemyPairCommutes();
        AVulnerableAmplifierPairDoesNotCommute();
        EndTurnNeverCommutes();
        TheWhiteListRefusesUnprovenPairs();
    }

    // (1) Two different seats hitting the same enemy, neither able to change the other's
    // damage: the two orders leave the same board, so this is the pair a joint search wants to
    // collapse. If this ever reports false the white-list can never fill, which is a real
    // (if safe) regression worth catching.
    private static void ASharedEnemyPairCommutes()
    {
        var (party, combat, enemy) = Board("COMMUTE-POSITIVE");
        var root = KernelSession.Capture(combat);
        var verdict = KernelCommutation.Check(root,
            new KernelTeamSearch.Action(party[0], Card(party[0], root, "StrikeIronclad"), enemy),
            new KernelTeamSearch.Action(party[1], Card(party[1], root, "StrikeIronclad"), enemy));
        Check(verdict.Commutes, $"two seats striking the same enemy should commute, got: {verdict.Reason} {verdict.Difference}");
        Console.WriteLine("PASS: two seats striking the same enemy commute by simulation, so the joint "
            + "search may collapse those two orderings into one.");
    }

    // (2) THE DISCRIMINATING CASE. Bash applies Vulnerable, which amplifies a following Strike,
    // so AB and BA leave the enemy at different HP. A checker that compares only "did both
    // plays succeed" would call this pair independent and silently mis-plan every turn that
    // contains a setup-then-payoff sequence.
    private static void AVulnerableAmplifierPairDoesNotCommute()
    {
        var (party, combat, enemy) = Board("COMMUTE-VULNERABLE");
        var root = KernelSession.Capture(combat);
        var verdict = KernelCommutation.Check(root,
            new KernelTeamSearch.Action(party[0], Card(party[0], root, "Bash"), enemy),
            new KernelTeamSearch.Action(party[0], Card(party[0], root, "StrikeIronclad"), enemy));
        Check(!verdict.Commutes,
            "Bash then Strike must NOT commute with Strike then Bash: Vulnerable changes the "
            + "Strike's damage, and treating the pair as independent would mis-plan the turn.");
        Check(verdict.Reason == "state-diverged",
            $"expected the pair to be rejected because the state diverged, got '{verdict.Reason}'.");
        Check(verdict.Difference.Length > 0,
            "a rejected pair must say WHERE the two orders diverged; an empty difference is the "
            + "diagnostic-that-says-nothing this project keeps getting bitten by.");
        Console.WriteLine("PASS: Bash and Strike do not commute — the simulation catches the "
            + "Vulnerable amplification and names the diverging field.");
    }

    // (3) EndTurn is structurally a round boundary, not a card. It must be refused without
    // spending two simulations on it, and it must never be collapsed by a joint search.
    private static void EndTurnNeverCommutes()
    {
        var (party, combat, enemy) = Board("COMMUTE-ENDTURN");
        var root = KernelSession.Capture(combat);
        var verdict = KernelCommutation.Check(root,
            new KernelTeamSearch.Action(party[0], null, null, EndTurn: true),
            new KernelTeamSearch.Action(party[1], Card(party[1], root, "StrikeIronclad"), enemy));
        Check(!verdict.Commutes, "EndTurn must never commute with an action.");
        Check(verdict.Reason == "end-turn-is-a-boundary",
            $"expected the structural refusal for EndTurn, got '{verdict.Reason}'.");
        Console.WriteLine("PASS: EndTurn is refused as a round boundary before any simulation is "
            + "spent, so it can never be collapsed with a card play.");
    }

    // (4) The white-list can only be widened by evidence. Handing it a pair that was never
    // proven must throw rather than quietly record an assumption about the game.
    private static void TheWhiteListRefusesUnprovenPairs()
    {
        var (party, combat, enemy) = Board("COMMUTE-WHITELIST");
        var root = KernelSession.Capture(combat);
        var bash = new KernelTeamSearch.Action(party[0], Card(party[0], root, "Bash"), enemy);
        var strike = new KernelTeamSearch.Action(party[0], Card(party[0], root, "StrikeIronclad"), enemy);
        var list = new CommutationWhiteList();
        Check(list.Count == 0, "the white-list must start empty; entries are evidence, not defaults.");
        var threw = false;
        try { list.Add(root, bash, strike); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "the white-list accepted a pair that does not commute; a search-space "
            + "optimisation just became an unverified assumption about the game.");
        Check(list.Count == 0, "the rejected pair still landed in the white-list.");
        // And a genuinely commuting pair DOES go in, so the refusal above is not just a
        // list that never accepts anything.
        list.Add(root,
            new KernelTeamSearch.Action(party[0], Card(party[0], root, "StrikeIronclad"), enemy),
            new KernelTeamSearch.Action(party[1], Card(party[1], root, "StrikeIronclad"), enemy));
        Check(list.Count == 1, $"a proven pair was not recorded (count={list.Count}).");
        Console.WriteLine("PASS: the white-list refuses an unproven pair, accepts a proven one, and "
            + "starts empty so no entry rests on intuition.");
    }

    /// <summary>
    /// The root's own card instance for a card type in a seat's hand. Matched by TYPE, not by
    /// Id.Entry: the entry string is a model-database key (the live logs show ALL_FOR_ONE-style
    /// values) and guessing one turns a fixture bug into a confusing "sequence contains no
    /// matching element".
    /// </summary>
    private static CardModel Card(Player player, KernelSession root, string typeName) =>
        root.Hand(player).First(card => card.GetType().Name == typeName);

    private static void Check(bool condition, string failure)
    { if (!condition) throw new Exception(failure); }

    /// <summary>
    /// Two seats, each with 3 energy. Seat 0 holds Bash + Strike so the Vulnerable pair is
    /// playable from one hand; the enemy is tanky enough that neither order ends the fight,
    /// which keeps the comparison about the two orders rather than about the ending.
    /// </summary>
    private static (Player[] Party, CombatState Combat, Creature Enemy) Board(string seed)
    {
        var party = Enumerable.Range(0, 2).Select(i =>
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, i, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        party[0].PlayerCombatState!.Hand.AddInternal(combat.CreateCard<Bash>(party[0]));
        for (var i = 0; i < 6; i++)
            foreach (var p in party)
                p.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(400); enemy.SetCurrentHpInternal(400);
        // From the monster's own state machine: a hand-built MoveState has no FollowUpState.
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat, enemy);
    }
}
