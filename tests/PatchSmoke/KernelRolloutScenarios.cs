using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// P1-02 acceptance: the cheap baseline policies must always come back with either a REAL
/// ending or an explicitly named cutoff, and must never dress one up as the other.
///
/// The plan's wording is exact about this and so is this file: "每次返回真实终局或明确截断"
/// (STS2_Bot_Solver_Implementation_Plan_v1.0 §P1-02), and "截断不算团灭" (§02). The failure
/// this guards against is not a crash — it is a truncated simulation being read as a loss
/// or a win, which is unfalsifiable in a log and would corrupt every number downstream.
/// </summary>
internal static class KernelRolloutScenarios
{
    private static readonly RolloutPolicyKind[] AllPolicies =
        [RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth];

    internal static void Run()
    {
        TournamentDecisionReadsTheCurrentBoard();
        ChoiceCardsRemainPlayable();
        PortfolioStartsEachPolicyFromTheSamePosition();
        EveryPolicyReachesARealEnding();
        ACutoffIsNeverDressedAsAnEnding();
        TheSameSeedReplaysTheSameActions();
        ARolloutDoesNotTouchTheSessionItWasForkedFrom();
        TheTournamentAlwaysReturnsALegalFirstAction();
        TheTournamentLeavesTheRootUntouched();
        TheTournamentPicksTheBestEndingItFound();
        TheBoundaryNamesThePendingChoice();
        AnUnresolvedBoardDeclinesInsteadOfThrowing();
    }

    // R2 2026-09-21: cached rootSession ?? Capture -> exit 1:
    // "tournament must decide from the current combat, not a cached search root"
    // Evidence: work/solver-c-stale-red.log; fresh capture passes in solver-c-stale-green.log.
    private static void TournamentDecisionReadsTheCurrentBoard()
    {
        var (oldParty, oldCombat) = Board("TOURNAMENT-STALE");
        var (party, combat) = Board("TOURNAMENT-CURRENT");
        var planner = new KernelCombatPlanner();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(KernelCombatPlanner).GetField("rootSession", flags)!.SetValue(planner, KernelSession.Capture(oldCombat));
        var args = new object?[] { combat, party, null };
        var accepted = (bool)typeof(KernelCombatPlanner).GetMethod("TryTournamentDecision", flags)!.Invoke(planner, args)!;
        Check(accepted && args[2] is TeamCombatPlanner.Decision decision && party.Contains(decision.Player),
            "tournament must decide from the current combat, not a cached search root");
        Console.WriteLine("PASS: tournament decisions capture the current combat instead of reusing a stale search root.");
    }

    // R2 2026-09-21: disable the portfolio -> exit 1:
    // "portfolio must match the best independently evaluated policy from the same opening"
    // Probe HP: Kill=316, Defend=320, Growth=316; disabled portfolio=316.
    // Removing only branch.Fork after the choice fix stays green: the rollout's card forks
    // mask that mutation on this fixture. This test proves policy selection, not all isolation.
    private static void PortfolioStartsEachPolicyFromTheSamePosition()
    {
        var (party, combat) = Board("PORTFOLIO-ISOLATION");
        foreach (var p in party)
            p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<DefendIronclad>(p));
        var root = KernelSession.Capture(combat);
        var evaluated = AllPolicies.Select(policy => KernelTournament.Run(root, party, party,
            new TournamentOptions(TopK: 1, Policy: policy,
                Rollout: new RolloutOptions(policy), IncludePotions: false, UsePolicyPortfolio: false), 30000).Record!).ToArray();
        var best = evaluated.Aggregate((a, b) => TerminalComparer.Compare(a, b) >= 0 ? a : b);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = KernelTournament.Run(root, party, party,
            new TournamentOptions(TopK: 1, IncludePotions: false), 30000);
        Console.WriteLine($"PORTFOLIO PROBE: single-policy HP={string.Join(",", evaluated.Select(r => r.PostCombatHp.Sum()))}; portfolio HP={result.Record!.PostCombatHp.Sum()}; ms={timer.Elapsed.TotalMilliseconds:F1}; rollouts={result.Rollouts}");
        Check(TerminalComparer.Compare(result.Record!, best) == 0,
            "portfolio must match the best independently evaluated policy from the same opening");
        Check(result.Rollouts == 3, "portfolio must count all three actual rollouts");
        Console.WriteLine("PASS: portfolio evaluates independent policy states and counts every rollout.");
    }

    // R2 2026-09-21: restore Play-before-CardBranches -> exit 1:
    // "choice rollout must resolve Headbutt and reach victory without poisoning the parent"
    private static void ChoiceCardsRemainPlayable()
    {
        var (party, combat) = Board("ROLLOUT-CHOICE-TRANSACTION");
        var headbutt = combat.CreateCard<Headbutt>(party[0]);
        party[0].PlayerCombatState!.Hand.AddInternal(headbutt);
        party[0].PlayerCombatState!.DiscardPile.AddInternal(combat.CreateCard<StrikeIronclad>(party[0]));
        var root = KernelSession.Capture(combat);
        var result = KernelRollout.Run(root.Fork(), party, party);
        Check(result.Victory && result.Actions.Any(a => a.Card == headbutt && a.Choices is { Count: > 0 }),
            "choice rollout must resolve Headbutt and reach victory without poisoning the parent");
        var tournament = KernelTournament.Run(root, party, party,
            new TournamentOptions(TopK: 1, IncludePotions: false), 30000);
        Check(tournament.FirstAction?.Card == headbutt && tournament.FirstAction.Choices is { Count: > 0 },
            "tournament must carry the opening Headbutt choice for deployment");
        Console.WriteLine("PASS: rollout and tournament resolve and retain Headbutt choices.");
    }

    // (9) THE PREVIEW BUG, pinned. Measured live 2026-09-21: on a board with an unresolved
    // choice the tournament could not fork it, threw, and the caller fell through to the
    // ordinary path — which was still holding the OPENING budget, so one declined decision cost
    // up to eight minutes of searching (heartbeats at 45 s / 90 s / 135 s, 21 occurrences in one
    // run). The board is reproducible in-process, so the condition does not have to be caught in
    // a live game again.
    private static void AnUnresolvedBoardDeclinesInsteadOfThrowing()
    {
        var (party, combat) = Board("UNRESOLVED-DECLINE");
        // CARDS FIRST, CAPTURE SECOND. Doing it the other way round made `Play` fail for a
        // different reason (the session had never seen the card) and `failedBoundary` stayed
        // null, so the fixture tested nothing while looking like it did.
        var headbutt = combat.CreateCard<Headbutt>(party[0]);
        party[0].PlayerCombatState!.Hand.AddInternal(headbutt);
        party[0].PlayerCombatState!.DiscardPile.AddInternal(combat.CreateCard<StrikeIronclad>(party[0]));
        var root = KernelSession.Capture(combat);
        var enemy = root.Enemies.First();
        // Headbutt resolves by CHOOSING a card from the discard pile. Playing it with no choice
        // vector leaves the transaction half-done, which is exactly the state the live tournament
        // hit. (Same construction as KernelRoundScenarios' fork guard.)
        var unresolved = root.Fork();
        Check(!unresolved.Play(headbutt, enemy, out _),
            "an unresolved choice transaction must report itself as unfinished.");
        Check(!unresolved.CanFork,
            "CanFork must be false on a half-resolved action; the tournament asks BEFORE forking, "
            + "and without a cheap predicate it can only learn this by throwing every tick.");
        // And the tournament must DECLINE on it rather than throw: a decline is a cheap fallback,
        // an exception per tick is what produced the minutes-long stalls.
        var outcome = KernelTournament.Run(unresolved, party, party,
            new TournamentOptions(TopK: 2), budgetMs: 1000);
        Check(outcome.FirstAction is null,
            "the tournament must return no action for a board it cannot fork, not a fabricated one.");
        Console.WriteLine("PASS: a half-resolved board reports CanFork=false and the tournament "
            + "declines on it instead of throwing, so the fallback can stay cheap.");
    }

    // (8) A turn-boundary truncation must say WHICH pending choice stopped it. The bare
    // `round-pending-choice` literal says only that something did, and measured live
    // 2026-09-21 a lost run carried four of them with nothing to attribute them to — which is
    // what blocks B11, whose protocol is "read the NAMED choice, and only then touch the
    // settlement". The fallback branch is asserted too: renaming it would silently split the
    // `boundaries=` histogram every earlier log is grouped by.
    private static void TheBoundaryNamesThePendingChoice()
    {
        Check(KernelSession.NamePendingChoice("BLAZE", null) == "round-pending-choice:BLAZE",
            "a turn-start choice must be named by its SourceId.");
        Check(KernelSession.NamePendingChoice(null, "KnowledgeDemonChoice") == "round-pending-choice:knowledge-demon/KnowledgeDemonChoice",
            "a knowledge-demon choice must name its own type.");
        Check(KernelSession.NamePendingChoice(null, null) == "round-pending-choice",
            "with nothing pending the boundary must keep its ORIGINAL name; a renamed fallback "
            + "would split the boundaries= histogram that every earlier live log groups by.");
        Check(KernelSession.NamePendingChoice("", null) == "round-pending-choice",
            "an empty SourceId is not a name.");
        Check(KernelSession.NamePendingChoice("BLAZE", "KnowledgeDemonChoice") == "round-pending-choice:BLAZE",
            "when both are pending the turn-start choice wins, so the name stays stable.");
        // The composed name must keep the prefix (every existing grep and `boundaries=` reading
        // keys off it) AND say which SITE failed closed — ten sites share this boundary, and a
        // bare name left a lost run's two truncations unattributable.
        var composed = KernelSession.PendingBoundary(null, null, "settle-deaths");
        Check(composed == "round-pending-choice@settle-deaths",
            $"the boundary must name its site; got '{composed}'.");
        Check(composed.StartsWith("round-pending-choice", StringComparison.Ordinal),
            "the boundary must keep the original prefix so existing greps still match.");
        Check(KernelSession.PendingBoundary("BLAZE", null, "enemy-phase")
                == "round-pending-choice:BLAZE@enemy-phase",
            "the boundary must carry BOTH what was pending and where it stopped.");
        Console.WriteLine("PASS: a turn-boundary truncation names the pending choice that caused it, "
            + "and keeps the original name when nothing is pending.");
    }

    // (5) The plan's "始终保留可执行方案" (§06), made structural: as long as any action is
    // legal, the tournament returns one. The failure this guards is the one the whole plan
    // exists to remove — a planner that has nothing to submit.
    private static void TheTournamentAlwaysReturnsALegalFirstAction()
    {
        var (party, combat) = Board("TOURNAMENT-ALWAYS");
        var actors = party.ToArray();
        var result = KernelTournament.Run(KernelSession.Capture(combat), party, actors,
            new TournamentOptions(TopK: 3), budgetMs: 2000);
        Check(result.FirstAction is not null, $"no first action (stop={result.StopReason}).");
        Check(result.Record is not null, "a returned first action must carry its ending record.");
        Check(result.StopReason.Length > 0, "the tournament must say how it stopped.");
        Check(result.Rollouts > 0, "it returned an action without rolling anything out.");
        Console.WriteLine("PASS: the tournament returns a legal first action with its ending record "
            + "for every board where an action exists.");
    }

    // (6) The tournament forks per candidate, so the session it was handed must be untouched —
    // including a live one. Without this, planning moves the real board.
    private static void TheTournamentLeavesTheRootUntouched()
    {
        var (party, combat) = Board("TOURNAMENT-ISOLATION");
        var actors = party.ToArray();
        var root = KernelSession.Capture(combat);
        var before = root.CompactStateKey() + "\n" + root.PartyStateText();
        KernelTournament.Run(root, party, actors, new TournamentOptions(TopK: 3), budgetMs: 2000);
        Check(root.CompactStateKey() + "\n" + root.PartyStateText() == before,
            "the tournament changed the root session it was handed; a live board would move.");
        Console.WriteLine("PASS: the tournament forks each candidate and leaves the root it was "
            + "handed byte-identical, so it can be run against the live board.");
    }

    // (7) The actual decision quality: the action it returns must be the one whose ENDING was
    // best, not merely the first or the biggest hit.
    //
    // THE BOARD HAS TO BE KNIFE-EDGED, and getting that wrong is how this assertion was first
    // written. The first version used the ordinary Board(), where every candidate led to an
    // identical victory — inverting the comparison left it green, so it proved nothing. The
    // guard below therefore FAILS LOUDLY when the candidates all end the same way, instead of
    // passing and looking like coverage (R2, R5b).
    //
    // KnifeEdgeBoard gives each seat exactly 1 energy and one Strike + one Defend, against a
    // 20-HP enemy that four Strikes (24) kill but any Defend does not (18). So the FIRST action
    // alone decides whether the party is hit this round: all-Strike ends at full HP, one Defend
    // ends with the party damaged. Same tier, different resource utility — exactly the key the
    // comparator has to get right.
    private static void TheTournamentPicksTheBestEndingItFound()
    {
        var (party, combat) = KnifeEdgeBoard("TOURNAMENT-BEST");
        var actors = party.ToArray();
        var result = KernelTournament.Run(KernelSession.Capture(combat), party, actors,
            new TournamentOptions(TopK: 8), budgetMs: 8000);
        Check(result.Record is not null, "no chosen record.");
        Check(result.Considered.Count > 1,
            $"only {result.Considered.Count} ending(s) considered; the comparison is untested.");
        var distinct = result.Considered
            .Select(r => TerminalComparer.ResourceUtility(r))
            .Distinct().Count();
        Check(distinct > 1,
            "INCONCLUSIVE: all " + result.Considered.Count + " candidates produced an identical "
            + "ending, so this assertion cannot tell a correct selection from an inverted one. "
            + "The board stopped being discriminating.");
        var best = result.Considered.Aggregate((a, b) => TerminalComparer.Compare(a, b) > 0 ? a : b);
        Check(TerminalComparer.Compare(result.Record, best) == 0,
            "the tournament returned an action whose ending is NOT the best among the endings it "
            + $"simulated: chose {TerminalComparer.ResourceUtility(result.Record)} "
            + $"against a best of {TerminalComparer.ResourceUtility(best)} "
            + $"over {result.Considered.Count} candidates.");
        Console.WriteLine("PASS: the tournament returns the first action whose simulated ENDING is "
            + "best under the shared objective, not the first or the largest hit.");
    }

    /// <summary>
    /// Each seat has 1 energy and exactly one Strike + one Defend. Four Strikes (24 damage) kill
    /// the 20-HP enemy; replacing any one with a Defend (18) leaves it alive for its attack. The
    /// first action therefore decides the ending, which is what makes the selection testable.
    /// </summary>
    private static (Player[] Party, CombatState Combat) KnifeEdgeBoard(string seed)
    {
        var party = Enumerable.Range(0, 4).Select(i =>
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, i, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(1);
            p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(p));
            for (var i = 0; i < 6; i++)
                p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(20); enemy.SetCurrentHpInternal(20);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat);
    }

    // (1) On a board that can be won, all three policies win it — not "return something",
    // but reach a terminal the simulator actually produced.
    private static void EveryPolicyReachesARealEnding()
    {
        foreach (var kind in AllPolicies)
        {
            var (party, combat) = Board($"ROLLOUT-{kind}");
            var session = KernelSession.Capture(combat);
            var outcome = KernelRollout.Run(session, party, party, new RolloutOptions(kind, Seed: 1));
            Check(outcome.StopReason.Length > 0, $"{kind}: stopped without saying why.");
            Check(outcome.Terminal, $"{kind}: did not reach a real ending (stop={outcome.StopReason}).");
            Check(outcome.Victory, $"{kind}: reached a terminal but it was not a victory (stop={outcome.StopReason}).");
            Check(outcome.Steps > 0 && outcome.Actions.Count == outcome.Steps,
                $"{kind}: {outcome.Steps} steps but {outcome.Actions.Count} recorded actions.");
            Check(!outcome.Cutoff, $"{kind}: a terminal must not also report itself as a cutoff.");
        }
        Console.WriteLine("PASS: every baseline rollout policy (kill/defend/growth) plays a winnable fight "
            + "through to a real victory and records one action per step.");
    }

    // (2) The other half of the same rule, and the half that is easy to get wrong: when the
    // simulation is CUT OFF it must say so, and must not report a victory or a defeat.
    private static void ACutoffIsNeverDressedAsAnEnding()
    {
        var (party, combat) = Board("ROLLOUT-CUTOFF");
        var session = KernelSession.Capture(combat);
        var outcome = KernelRollout.Run(session, party, party,
            new RolloutOptions(RolloutPolicyKind.Kill, MaxSteps: 3, Seed: 1));
        Check(!outcome.Terminal, "three steps cannot finish this fight, yet it reported a terminal.");
        Check(!outcome.Victory, "a cut-off rollout must never report a victory.");
        Check(outcome.StopReason == "step-cap",
            $"expected the cutoff to name step-cap, got '{outcome.StopReason}'.");
        Check(outcome.Cutoff, "a step-cap stop must report itself as a cutoff.");
        Console.WriteLine("PASS: a rollout stopped by its step cap reports step-cap and is never "
            + "counted as a victory or as a party wipe.");
    }

    // (3) The tie-break stream is the planner's own. Determinism is the observable half of
    // that: the same board rolled out twice with the same seed must produce the same line.
    private static void TheSameSeedReplaysTheSameActions()
    {
        var lines = new List<string>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (party, combat) = Board("ROLLOUT-DETERMINISM");
            var session = KernelSession.Capture(combat);
            var outcome = KernelRollout.Run(session, party, party,
                new RolloutOptions(RolloutPolicyKind.Kill, Seed: 7));
            lines.Add(outcome.Steps + "|" + string.Join(",", outcome.Actions.Select(ActionText)));
        }
        Check(lines[0] == lines[1],
            "the same board and seed produced two different lines:\n  " + lines[0] + "\n  " + lines[1]);
        Console.WriteLine("PASS: a rollout is deterministic in its seed: same board, same seed, "
            + "same action line, so its tie-breaks come from the planner stream and not from the fight.");
    }

    // (4) The rollout mutates the session it is handed in place; the session it was FORKED
    // from must be untouched. This is the live-safety half: if a rollout can write through
    // to the root, then planning can move the real board.
    private static void ARolloutDoesNotTouchTheSessionItWasForkedFrom()
    {
        var (party, combat) = Board("ROLLOUT-ISOLATION");
        var root = KernelSession.Capture(combat);
        var before = root.CompactStateKey() + "\n" + root.PartyStateText();
        var outcome = KernelRollout.Run(root.Fork(), party, party, new RolloutOptions(RolloutPolicyKind.Kill, Seed: 3));
        Check(outcome.Terminal, "the isolation fixture must actually play the fight out.");
        Check(root.CompactStateKey() + "\n" + root.PartyStateText() == before,
            "a rollout on a fork changed the root session it was forked from.");
        Console.WriteLine("PASS: a rollout advances only the session it is handed; the root it was "
            + "forked from is byte-identical afterwards, so planning cannot move the live board.");
    }

    private static string ActionText(KernelTeamSearch.Action action) =>
        action.EndTurn ? "end:" + action.Player.NetId
        : action.Card is null ? "?"
        : $"{action.Player.NetId}:{action.Card.Id.Entry}->{action.Target?.CombatId}";

    private static void Check(bool condition, string failure)
    { if (!condition) throw new Exception(failure); }

    /// <summary>
    /// A small, genuinely winnable four-seat fight. The monster's move comes FROM its own
    /// state machine: a hand-built MoveState has no FollowUpState and throws on the first
    /// round crossing ("行动 ATTACK 没有后继状态"), which turns every round boundary into a
    /// fake truncation. Same construction as KernelEngineScenarios.ContinuationBoard.
    /// </summary>
    private static (Player[] Party, CombatState Combat) Board(string seed)
    {
        var party = Enumerable.Range(0, 4).Select(i =>
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, i, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            void Add<T>() where T : CardModel => p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<T>(p));
            Add<StrikeIronclad>(); Add<StrikeIronclad>(); Add<StrikeIronclad>(); Add<StrikeIronclad>();
            for (var i = 0; i < 6; i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        for (var i = 0; i < 2; i++)
        {
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
            enemy.SetMaxHpInternal(40); enemy.SetCurrentHpInternal(40);
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        }
        return (party, combat);
    }
}
