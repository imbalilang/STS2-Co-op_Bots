using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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
        SlicingDoesNotChangeTheAnswer();
        ABudgetBindsInsideOneAdvance();
        ParallelRolloutsMatchSequentialOnes();
        ChoiceCardsRemainPlayable();
        PortfolioStartsEachPolicyFromTheSamePosition();
        EveryPolicyReachesARealEnding();
        ACutoffIsNeverDressedAsAnEnding();
        TheSameSeedReplaysTheSameActions();
        ARolloutDoesNotTouchTheSessionItWasForkedFrom();
        TheTournamentAlwaysReturnsALegalFirstAction();
        TournamentScriptCacheKeepsTheCachedLine();
        ThePlannerKeepsTheCachedScriptWhenTheRescanCannotBeatIt();
        EndTurnStepWaitsForTheHumansInsteadOfDroppingTheScript();
        ACachedScriptIsDroppedWhenTheLivingSeatSetChanges();
        TheScriptContinuesThroughItsOwnPredictedDeath();
        TheTournamentLeavesTheRootUntouched();
        TheTournamentPicksTheBestEndingItFound();
        DeadlineCoverageKeepsNonDamageCandidates();
        ToolsOfTheTradeDoesNotTruncateRollout();
        HoldOptionAppearsForWastedEnergy();
        HoldOptionAppearsForLowThreatExhaust();
        ConcoctIsPlayedBeforeTheAlliesAttacks();
        FadeIsPlayedBeforeTheAlliesBlocks();
        TemporaryStatPotionNeedsMatchingCards();
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
        typeof(KernelCombatPlanner).GetField("rootSession", Flags)!.SetValue(planner, KernelSession.Capture(oldCombat));
        var decided = DriveTournament(planner, combat, party, out var decision);
        Check(decided && decision is not null && party.Contains(decision.Player),
            "tournament must decide from the current combat, not a cached search root");
        Console.WriteLine("PASS: tournament decisions capture the current combat instead of reusing a stale search root.");
    }

    private static readonly System.Reflection.BindingFlags Flags =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    // RECORD `==` IS NOT ENOUGH HERE. `TerminalRecord.PostCombatHp` (and Action's Choices) are
    // IReadOnlyList fields, and record equality compares those by REFERENCE — two runs that
    // reached the same ending are not `==`. An assertion that fails on identity would have said
    // nothing about slicing, so compare the fields that carry the answer.
    private static bool SameEnding(TerminalRecord? a, TerminalRecord? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null
            && a.Victory == b.Victory && a.Kind == b.Kind && a.CutoffReason == b.CutoffReason
            && a.IrreversibleLoss == b.IrreversibleLoss && a.ResourcesConsumed == b.ResourcesConsumed
            && a.PostCombatHp.SequenceEqual(b.PostCombatHp));

    /// <summary>
    /// The action as a reader needs it: WHICH seat plays WHICH card at WHAT. The card's name alone
    /// is not enough — two seats playing the same card at the same target look identical until the
    /// seat is on the line, and telling those apart is exactly the point of the comparison.
    /// </summary>
    private static string Label(KernelTeamSearch.Action? a)
        => a is null ? "none"
            : $"{a.Card?.Id.Entry ?? "end-turn"}@{a.Player.NetId}->{a.Target?.CombatId?.ToString() ?? "-"}";

    private static bool SameAction(KernelTeamSearch.Action? a, KernelTeamSearch.Action? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null
            && ReferenceEquals(a.Player, b.Player) && ReferenceEquals(a.Card, b.Card)
            && ReferenceEquals(a.Target, b.Target) && ReferenceEquals(a.Potion, b.Potion)
            && a.EndTurn == b.EndTurn && a.CardStateKey == b.CardStateKey
            && a.CardStateOccurrence == b.CardStateOccurrence
            && (a.Choices ?? []).SequenceEqual(b.Choices ?? [])
            && (a.ChoiceOrdinals ?? []).SequenceEqual(b.ChoiceOrdinals ?? []));

    /// <summary>
    /// Drive the planner's tournament the way BotRuntime does: one call per "frame", repeating
    /// while it answers Pending. The tournament used to answer inside a single call — the frame
    /// loop IS the change under test, so every scenario that reaches it goes through here.
    /// </summary>
    private static bool DriveTournament(KernelCombatPlanner planner, CombatState combat,
        IReadOnlyList<Player> party, out TeamCombatPlanner.Decision? decision, string point = "test-point")
    {
        var method = typeof(KernelCombatPlanner).GetMethod("TryTournamentDecision", Flags)!;
        decision = null;
        // A FRAME IS ~16 ms, NOT AN ITERATION. Once the tournament went parallel its workers run
        // at BelowNormal on their own threads, so a tight counting loop burns its whole budget in
        // milliseconds and then reports "never finished" while the rollouts are still running —
        // measured 2026-09-21, that failed the kernel gate. Sleep between polls, and bound the
        // wait by the WALL CLOCK rather than by a count that means nothing here.
        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            // Match TryTournamentDecision(combat, actors, humansFinished, point, out decision).
            // These fixtures are all-bot, so humansFinished=true is the honest value; the
            // all-seat-driven gate would allow end-turn either way.
            var args = new object?[] { combat, party, true, point, null };
            var outcome = method.Invoke(planner, args)!.ToString()!;
            if (outcome != "Pending")
            {
                decision = args[4] as TeamCombatPlanner.Decision;
                return outcome == "Decided";
            }
            Thread.Sleep(1);
        }
        throw new InvalidOperationException("Tournament: the sliced tournament never finished.");
    }

    // CHECKED (R2) 2026-09-21: deleting the `if (watch.Elapsed >= slice) break;` line from
    // TournamentRun.Advance turns this red, verbatim:
    //
    //   System.Exception: the sliced run must have taken more than one Advance; it is not
    //   actually sliced
    //      at KernelRolloutScenarios.SlicingDoesNotChangeTheAnswer() ... line 110
    //      at CooperativeScenarios.Run() ... line 309
    //
    // THE ASSERTION UNDER THAT MUTATION. Slicing must change WHEN the tournament computes, never
    // WHAT it computes — fixing the animation must not cost decision quality. The budget here is
    // far above what the fixture can spend, so the deadline never binds and both runs execute
    // every candidate: same count, same order, same winner. That makes it a deterministic
    // equality rather than a timing race.
    private static void SlicingDoesNotChangeTheAnswer()
    {
        var (party, combat) = Board("SLICING-EQUIVALENCE");
        var root = KernelSession.Capture(combat);
        var options = new TournamentOptions(TopK: 4,
            Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: combat.RoundNumber),
            IncludePotions: true);

        var whole = KernelTournament.Run(root, party, party, options, budgetMs: 1_000_000);

        // 2 ms is smaller than one rollout (~27 ms), so the state machine has to park and resume
        // in the MIDDLE of a candidate rather than only between candidates.
        var sliced = new TournamentRun(root, party, party, options, budgetMs: 1_000_000);
        var frames = 0;
        while (!sliced.Advance(TimeSpan.FromMilliseconds(2))) frames++;
        var result = sliced.Result!;

        Check(frames > 1, "the sliced run must have taken more than one Advance; it is not actually sliced");
        Check(result.Rollouts == whole.Rollouts,
            $"slicing must not change how many rollouts ran (whole={whole.Rollouts} sliced={result.Rollouts})");
        Check(result.Cutoffs == whole.Cutoffs,
            $"slicing must not change the cutoff count (whole={whole.Cutoffs} sliced={result.Cutoffs})");
        Check(result.StopReason == whole.StopReason,
            $"slicing must not change the stop reason (whole={whole.StopReason} sliced={result.StopReason})");
        Check(SameAction(result.FirstAction, whole.FirstAction),
            $"slicing must not change the chosen action (whole={whole.FirstAction?.Card?.Id.Entry ?? "end-turn"} "
            + $"sliced={result.FirstAction?.Card?.Id.Entry ?? "end-turn"})");
        Check(SameEnding(result.Record, whole.Record),
            $"slicing must not change the ending the tournament picked "
            + $"(whole hp={string.Join('/', whole.Record?.PostCombatHp ?? [])} "
            + $"sliced hp={string.Join('/', result.Record?.PostCombatHp ?? [])})");

        Console.WriteLine($"PASS: a sliced tournament reaches the same answer as the one-shot run "
            + $"(rollouts={whole.Rollouts}, frames={frames}, action={whole.FirstAction?.Card?.Id.Entry ?? "end-turn"}, "
            + $"stop={whole.StopReason}).");
    }

    // THE PROBE THAT HAD TO COME FIRST.
    //
    // Parallelising the tournament is only legitimate if N rollouts running at once reach the
    // same answers as N rollouts running one after another. The engine says this is not free —
    // "A parent simulator cannot be forked concurrently: prediction history seals its mutable
    // tail and several COW containers publish a shared bit during Fork" — and that warning is
    // the whole reason the shape is "fork on the caller's thread, hand out private forks".
    //
    // A budget far above what the fixture can spend keeps the deadline out of it, so both runs
    // execute every candidate and this is an equality rather than a timing race.
    //
    // CHECKED (R2) 2026-09-21: consuming the lanes in REVERSE order (completion order instead of
    // launch order, which is what a naive `foreach (lane in ...)` over a completed set gives you)
    // turns this red, verbatim:
    //
    //   System.Exception: parallel rollouts must pick the same action
    //   (sequential=STRIKE_IRONCLAD@76561198109201343->0 parallel=STRIKE_IRONCLAD@12716757972810793217->0)
    //      at KernelRolloutScenarios.ParallelRolloutsMatchSequentialOnes() ... line 188
    //
    // Note the card NAME is identical on both sides — the seat is what moved. That is why the
    // comparison is on player AND card AND target, not on the card's id.
    private static void ParallelRolloutsMatchSequentialOnes()
    {
        var (party, combat) = Board("PARALLEL-EQUIVALENCE");
        var root = KernelSession.Capture(combat);
        var options = new TournamentOptions(TopK: 4,
            Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: combat.RoundNumber),
            IncludePotions: true);

        var sequential = KernelTournament.Run(root, party, party,
            options with { MaxDegreeOfParallelism = 1 }, budgetMs: 1_000_000);
        var parallel = KernelTournament.Run(root, party, party,
            options with { MaxDegreeOfParallelism = 4 }, budgetMs: 1_000_000);

        Check(parallel.Rollouts == sequential.Rollouts,
            $"parallel rollouts must run the same work (sequential={sequential.Rollouts} parallel={parallel.Rollouts})");
        Check(parallel.Cutoffs == sequential.Cutoffs,
            $"cutoff count must not depend on parallelism (sequential={sequential.Cutoffs} parallel={parallel.Cutoffs})");
        Check(parallel.StopReason == sequential.StopReason,
            $"stop reason must not depend on parallelism (sequential={sequential.StopReason} parallel={parallel.StopReason})");
        Check(SameAction(parallel.FirstAction, sequential.FirstAction),
            $"parallel rollouts must pick the same action (sequential={Label(sequential.FirstAction)} "
            + $"parallel={Label(parallel.FirstAction)})");
        Check(SameEnding(parallel.Record, sequential.Record),
            $"parallel rollouts must reach the same ending (sequential hp={string.Join('/', sequential.Record?.PostCombatHp ?? [])} "
            + $"parallel hp={string.Join('/', parallel.Record?.PostCombatHp ?? [])})");

        Console.WriteLine($"PASS: 4 rollouts at once reach the same answer as 1 at a time "
            + $"(rollouts={sequential.Rollouts}, action={sequential.FirstAction?.Card?.Id.Entry ?? "end-turn"}, "
            + $"stop={sequential.StopReason}).");
    }

    // THE BUDGET MUST COUNT THE TIME THE CURRENT Advance IS SPENDING.
    //
    // `computeMs` only folds in an Advance when that Advance RETURNS, so on its own it is stale
    // for the whole of a call. The one-shot Run passes a single enormous slice, so a budget
    // checked against the stale value never trips at all — every non-sliced caller would quietly
    // stop honouring its deadline. `BudgetSpent` therefore adds the running stopwatch.
    //
    // CHECKED (R2) 2026-09-21: dropping `+ watch.Elapsed.TotalMilliseconds` from BudgetSpent
    // turns this red, verbatim:
    //
    //   System.Exception: a 1 ms budget with no reserve must bind
    //   (stop=top-k-exhausted, rollouts=18)
    //      at KernelRolloutScenarios.ABudgetBindsInsideOneAdvance() ... line 179
    //
    // 18 rollouts is "every candidate" — i.e. the deadline never fired at all, which is exactly
    // the bug this guards.
    private static void ABudgetBindsInsideOneAdvance()
    {
        var (party, combat) = Board("BUDGET-INSIDE-ADVANCE");
        var root = KernelSession.Capture(combat);
        // ReserveMs 0 so the very first rollout is enough to blow a 1 ms budget; a default
        // 300 ms reserve would let the whole fixture through and prove nothing.
        var options = new TournamentOptions(TopK: 6, ReserveMs: 0,
            Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: 1), IncludePotions: false);

        var capped = KernelTournament.Run(root, party, party, options, budgetMs: 1);
        var uncapped = KernelTournament.Run(root, party, party, options, budgetMs: 1_000_000);

        Check(uncapped.Rollouts > 1,
            $"the fixture must offer more than one rollout or this proves nothing (got {uncapped.Rollouts})");
        Check(capped.StopReason == "deadline",
            $"a 1 ms budget with no reserve must bind (stop={capped.StopReason}, rollouts={capped.Rollouts})");
        Check(capped.Rollouts < uncapped.Rollouts,
            $"the budget must cut the run short (capped={capped.Rollouts} uncapped={uncapped.Rollouts})");
        Console.WriteLine($"PASS: the deadline binds inside a single Advance "
            + $"(capped={capped.Rollouts} rollouts/{capped.StopReason}, uncapped={uncapped.Rollouts}).");
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

    // CHECKED (R2) 2026-09-22: removing the Block role from the coverage list
    // (`ActionRole.Damage, ActionRole.Block,` -> `ActionRole.Damage,`) turns this red:
    //   System.Exception: a deadline-shortened top-K must still contain a block card;
    //   damage-only ordering would return STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,
    //   STRIKE_IRONCLAD; hand=...,DEFEND_IRONCLAD
    // The 2026-09-22 live run stopped at `deadline` on 707/1326 decisions (median 14
    // rollouts ≈ 5 first actions), so the pre-ranking was the actual policy in those fights.
    private static void DeadlineCoverageKeepsNonDamageCandidates()
    {
        var (party, combat) = Board("DEADLINE-COVERAGE");
        foreach (var p in party)
            p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<DefendIronclad>(p));
        var root = KernelSession.Capture(combat);
        var first = KernelTournament.FirstActions(root, party, topK: 4,
            new TournamentOptions(TopK: 4, IncludePotions: false));
        Check(first.Any(action => action.Card is DefendIronclad),
            "a deadline-shortened top-K must still contain a block card; damage-only ordering "
            + $"would return {string.Join(",", first.Select(a => a.Card?.Id.Entry ?? "end-turn"))}; "
            + $"hand={string.Join(",", root.Hand(party[0]).Select(c => c.Id.Entry))}");
        Console.WriteLine("PASS: the tournament's candidate list covers block/power roles before "
            + "the deadline cuts it to the biggest attacks.");
    }

    // CHECKED (R2) 2026-09-22: reverting the rollout's automatic turn-start choice
    // (`TurnStartChoiceCursor.ForAutomaticPolicy(request => null)`) turns this red:
    //   System.Exception: Tools of the Trade must resolve its start-of-turn discard, not
    //   truncate the rollout
    //   (stop=stuck:round-pending-choice:TOOLS_OF_THE_TRADE_POWER@start-next-player-turn).
    // The same shape produced 23/56 pending-choice decisions in the live final boss.
    private static void ToolsOfTheTradeDoesNotTruncateRollout()
    {
        var (party, combat) = Board("TOOLS-OF-THE-TRADE");
        var owner = party[0];
        foreach (var card in owner.PlayerCombatState!.Hand.Cards.ToArray())
            owner.PlayerCombatState.Hand.RemoveInternal(card);
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<ToolsOfTheTrade>(owner));
        var root = KernelSession.Capture(combat);
        var outcome = KernelRollout.Run(root.Fork(), party, party,
            new RolloutOptions(RolloutPolicyKind.Kill, MaxRounds: 20, Seed: 1));
        Check(!outcome.StopReason.Contains("TOOLS_OF_THE_TRADE_POWER", StringComparison.Ordinal),
            "Tools of the Trade must resolve its start-of-turn discard, not truncate the "
            + $"rollout (stop={outcome.StopReason}).");
        Check(outcome.Terminal && outcome.Victory,
            "the other three seats must still be able to finish the fight "
            + $"(stop={outcome.StopReason}, victory={outcome.Victory}).");
        Console.WriteLine("PASS: a Tools of the Trade start-of-turn discard is answered by the "
            + "rollout's automatic policy; the line reaches the real ending.");
    }

    // CHECKED (R2) 2026-09-22: making ShouldHold return false turns this red, verbatim:
    //   System.Exception: Wisp with 1 energy and no card its +1 energy can unlock must be
    //   held, not exhausted for an energy nobody can spend.
    private static void HoldOptionAppearsForWastedEnergy()
    {
        var (party, combat) = Board("HOLD-WISP");
        var owner = party[0];
        var state = owner.PlayerCombatState!;
        foreach (var card in state.Hand.Cards.ToArray()) state.Hand.RemoveInternal(card);
        if (state.Energy > 1) state.LoseEnergy(state.Energy - 1);
        var wisp = combat.CreateCard<Wisp>(owner);
        state.Hand.AddInternal(wisp);
        state.Hand.AddInternal(combat.CreateCard<Bludgeon>(owner));
        var root = KernelSession.Capture(combat);
        Check(RolloutRun.ShouldHold(root, party, owner, wisp),
            "Wisp with 1 energy and no card its +1 energy can unlock must be held, not "
            + "exhausted for an energy nobody can spend.");
        var first = KernelTournament.FirstActions(root, party, topK: 20,
            new TournamentOptions(TopK: 20, IncludePotions: false));
        Check(first.Any(action => action.EndTurn),
            "the hold rule must put an end-turn candidate into the tournament's first actions");
        // A mixed table with a human still acting passes IncludeEndTurns=false: the runtime
        // would refuse the end-turn, and re-asking every tick would burn a tournament per frame.
        var suppressed = KernelTournament.FirstActions(root, party, topK: 20,
            new TournamentOptions(TopK: 20, IncludePotions: false, IncludeEndTurns: false));
        Check(suppressed.Count > 0,
            "suppressing end-turn must not take the held card's own candidates away.");
        Check(!suppressed.Any(action => action.EndTurn),
            "IncludeEndTurns=false must remove the hold rule's end-turn candidate; a mixed table "
            + "with a human still acting depends on it.");
        Console.WriteLine("PASS: a wasted Wisp is held, ending the turn is a real first action, and "
            + "IncludeEndTurns=false removes it without removing the cards.");
    }

    // CHECKED (R2) 2026-09-22: making the non-energy hold branch return false turns this
    // red, verbatim:
    //   System.Exception: Piercing Wail into a turn whose attack is already fully blocked
    //   must be held, not exhausted for 2 damage.
    private static void HoldOptionAppearsForLowThreatExhaust()
    {
        var (party, combat) = Board("HOLD-WAIL");
        var owner = party[0];
        var state = owner.PlayerCombatState!;
        foreach (var card in state.Hand.Cards.ToArray()) state.Hand.RemoveInternal(card);
        if (state.Energy > 1) state.LoseEnergy(state.Energy - 1);
        owner.Creature.GainBlockInternal(50);
        var wail = combat.CreateCard<PiercingWail>(owner);
        state.Hand.AddInternal(wail);
        var root = KernelSession.Capture(combat);
        Check(RolloutRun.ShouldHold(root, party, owner, wail),
            "Piercing Wail into a turn whose attack is already fully blocked must be held, "
            + "not exhausted for 2 damage.");
        var first = KernelTournament.FirstActions(root, party, topK: 20,
            new TournamentOptions(TopK: 20, IncludePotions: false));
        Check(first.Any(action => action.EndTurn),
            "the low-threat hold rule must put an end-turn candidate into the tournament's first actions");
        Console.WriteLine("PASS: a low-threat Piercing Wail is held, and ending the turn is a real first action.");
    }

    // CHECKED (R2) 2026-09-23: removing CONCOCT from KernelTemporaryBuffs.Timing turns
    // this red, verbatim:
    //   System.Exception: Concoct must be played before the ally attacks; first action was
    //   STRIKE_IRONCLAD.
    // The Kill policy attacks first and leaves Concoct for the end of the turn.
    private static void ConcoctIsPlayedBeforeTheAlliesAttacks()
    {
        var caster = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 12, 0));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 12, 1));
        var party = new[] { caster, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "CONCOCT-ORDER"));
        foreach (var player in party)
        {
            player.ResetCombatState();
            combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80);
            player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3);
            for (var draw = 0; draw < 5; draw++)
                player.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(player));
        }
        caster.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<Concoct>(caster));
        caster.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(caster));
        ally.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(ally));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy);
        enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(200);
        enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate(
            (MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);

        var root = KernelSession.Capture(combat);
        var outcome = KernelRollout.Run(root.Fork(), party, party,
            new RolloutOptions(RolloutPolicyKind.Kill, MaxRounds: 5, Seed: 1));
        var first = outcome.Actions.FirstOrDefault();
        Check(first?.Card?.Id.Entry == "CONCOCT",
            $"Concoct must be played before the ally attacks; first action was "
            + $"{first?.Card?.Id.Entry ?? "none"}.");
        Check(first!.Target?.Player is { } targetAlly && RolloutRun.RemainingAttacks(root, targetAlly) > 0,
            "Concoct must target an ally that still has attack cards left.");
        Console.WriteLine("PASS: Concoct is sequenced before the buffed ally's attacks and "
            + "targets an ally with attacks remaining.");
    }

    // CHECKED (R2) 2026-09-23: removing Fade from KernelTemporaryBuffs.Timing turns this
    // red, verbatim:
    //   System.Exception: Fade must be played before the ally's block cards; first action
    //   was STRIKE_IRONCLAD; timing=, remain=0, blocks=1, canPlay=True, branchBoundary=''.
    // The Kill policy plays the caster's Strike first and Fade is left late.
    private static void FadeIsPlayedBeforeTheAlliesBlocks()
    {
        var caster = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 13, 0));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 13, 1));
        var party = new[] { caster, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "FADE-ORDER"));
        foreach (var player in party)
        {
            player.ResetCombatState();
            combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80);
            player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3);
        }
        var fade = combat.CreateCard<Fade>(caster);
        caster.PlayerCombatState!.Hand.AddInternal(fade);
        caster.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(caster));
        ally.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<DefendIronclad>(ally));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy);
        enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(200);
        enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate(
            (MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);

        var root = KernelSession.Capture(combat);
        var outcome = KernelRollout.Run(root.Fork(), party, party,
            new RolloutOptions(RolloutPolicyKind.Kill, MaxRounds: 5, Seed: 1));
        var first = outcome.Actions.FirstOrDefault();
        var fadeBranch = root.CardBranches(fade, ally.Creature, maximumBranches: 1).FirstOrDefault();
        Check(first?.Card?.Id.Entry == "FADE",
            $"Fade must be played before the ally's block cards; first action was "
            + $"{first?.Card?.Id.Entry ?? "none"}; timing={KernelTemporaryBuffs.Timing(fade)}, "
            + $"remain={RolloutRun.RelevantRemaining(root, party, caster, fade, ally.Creature)}, "
            + $"blocks={RolloutRun.RemainingBlocks(root, ally)}, canPlay={root.CanPlay(fade)}, "
            + $"branchBoundary='{fadeBranch?.Boundary}'.");
        Check(first!.Target?.Player is { } targetAlly && RolloutRun.RemainingBlocks(root, targetAlly) > 0,
            "Fade must target an ally that still has block cards left.");
        Console.WriteLine("PASS: Fade is sequenced before the buffed ally's block cards and "
            + "targets an ally with block cards remaining.");
    }

    // CHECKED (R2) 2026-09-23: making the FlexPotion/SpeedPotion cases in
    // PotionHasImmediateValue return true turns this red, verbatim:
    //   System.Exception: Flex Potion must not be offered when no ally has an attack left.
    // A temporary stat bottle with nothing left to amplify is offered again.
    private static void TemporaryStatPotionNeedsMatchingCards()
    {
        var caster = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 14, 0));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 14, 1));
        var party = new[] { caster, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "TEMP-POTION"));
        foreach (var player in party)
        {
            player.ResetCombatState();
            combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80);
            player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3);
        }
        caster.AddPotionInternal(ModelDb.Potion<FlexPotion>().ToMutable(), silent: true);
        caster.AddPotionInternal(ModelDb.Potion<SpeedPotion>().ToMutable(), silent: true);
        var noCards = KernelSession.Capture(combat);
        var none = KernelTournament.FirstActions(noCards, party, topK: 20,
            new TournamentOptions(TopK: 20, IncludePotions: true));
        Check(!none.Any(action => action.Potion is FlexPotion),
            "Flex Potion must not be offered when no ally has an attack left.");
        Check(!none.Any(action => action.Potion is SpeedPotion),
            "Speed Potion must not be offered when no ally has a block card left.");

        ally.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(ally));
        ally.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(ally));
        var withCards = KernelSession.Capture(combat);
        var offered = KernelTournament.FirstActions(withCards, party, topK: 20,
            new TournamentOptions(TopK: 20, IncludePotions: true));
        var flex = offered.FirstOrDefault(action => action.Potion is FlexPotion);
        var speed = offered.FirstOrDefault(action => action.Potion is SpeedPotion);
        Check(flex?.Target?.Player == ally, "Flex Potion must target the ally with an attack left.");
        Check(speed?.Target?.Player == ally, "Speed Potion must target the ally with a block card left.");
        Console.WriteLine("PASS: temporary stat potions are offered only when the target still has "
            + "the matching card type, and target that player.");
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
        Check(result.Script is { Count: > 0 }, "the winning roll-out must expose its whole script, "
            + "not only its first action; without that the planner cannot keep the line.");
        // NOT SameAction: the roll-out records the RESOLVED action (a fork's card instance plus
        // its choice vector), while FirstAction is the candidate. Compare what a reader means by
        // "the same first action": seat, card identity, target, potion, end-turn.
        var scriptFirst = result.Script![0];
        Check(scriptFirst.Player.NetId == result.FirstAction!.Player.NetId
            && scriptFirst.Card?.Id.Entry == result.FirstAction.Card?.Id.Entry
            && scriptFirst.Target?.CombatId == result.FirstAction.Target?.CombatId
            && scriptFirst.Potion?.Id.Entry == result.FirstAction.Potion?.Id.Entry
            && scriptFirst.EndTurn == result.FirstAction.EndTurn,
            "the cached script must start with the first action the tournament returned.");
        Console.WriteLine("PASS: the tournament returns a legal first action with its ending record "
            + "and the full winning script for every board where an action exists.");
    }

    // (5b) THE SCRIPT CACHE. The measured final-boss failure was: first card finds VICTORY,
    // every later re-search finds WIPE hp=0/0/0/0, and the line dies after one step. Keep the
    // winning roll-out's whole action list and play its next step while the fresh tournament
    // cannot beat its ending. This asserts both halves: the comparator boundary and that a
    // cached step can actually be emitted as a decision.
    private static void TournamentScriptCacheKeepsTheCachedLine()
    {
        var (party, combat) = Board("TOURNAMENT-SCRIPT");
        var actors = party.ToArray();
        var result = KernelTournament.Run(KernelSession.Capture(combat), party, actors,
            new TournamentOptions(TopK: 8), budgetMs: 8000);
        Check(result.Script is { Count: > 1 },
            "the fixture must produce a multi-step winning line, or the cache is untestable.");
        var script = result.Script!;

        // The boundary the user asked for: only a strictly better fresh ending may displace the
        // cached script. A wipe does not displace a victory; an unverified/null fresh ending is
        // not evidence either.
        var victory = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "",
            [1, 1, 1, 1], 0, 0);
        var wipe = new TerminalRecord(false, EvidenceKind.VerifiedTerminal, "",
            [0, 0, 0, 0], 4, 0);
        Check(!KernelCombatPlanner.FreshEndingDoesNotBeat(victory, wipe),
            "a fresh victory must displace a cached wipe script.");
        Check(KernelCombatPlanner.FreshEndingDoesNotBeat(wipe, victory),
            "a fresh wipe must NOT displace a cached victory script.");
        Check(KernelCombatPlanner.FreshEndingDoesNotBeat(null, victory),
            "a null fresh ending is not evidence of a better line and must not displace the script.");

        // Emission: find a step after the first that is already legal on the opening board
        // (the winning line plays one Strike per seat), cache it on a planner, and drive the
        // private emitter the way TryTournamentDecision does. The step must also expect the
        // opening living-seat set, because that is what the private emitter validates.
        Check(result.ScriptAliveStates is { Count: > 1 } && result.ScriptAliveStates.Count == script.Count,
            "the winning script must expose one living-seat prediction per action.");
        var openingSeats = combat.Players.Where(player => player.Creature.IsAlive)
            .Select(player => player.NetId).OrderBy(id => id).ToArray();
        var step = -1;
        for (var i = 1; i < script.Count; i++)
        {
            var action = script[i];
            if (action.Card is null || !party.Contains(action.Player)) continue;
            if (!result.ScriptAliveStates![i].SequenceEqual(openingSeats)) continue;
            var live = KernelSession.FindLiveCardByKey(action.Player, action.CardStateKey, action.CardStateOccurrence);
            if (live is not null && live.CanPlayTargeting(action.Target)) { step = i; break; }
        }
        Check(step >= 0, "the winning script must contain a later step that is playable from "
            + "the opening board and expects the opening party, or the cache cannot be tested end to end.");

        var planner = new KernelCombatPlanner();
        var plannerType = typeof(KernelCombatPlanner);
        plannerType.GetField("tournamentScript", Flags)!.SetValue(planner, script);
        plannerType.GetField("tournamentScriptRecord", Flags)!.SetValue(planner, result.Record);
        plannerType.GetField("tournamentScriptCombat", Flags)!.SetValue(planner, combat);
        plannerType.GetField("tournamentScriptIndex", Flags)!.SetValue(planner, step);
        plannerType.GetField("tournamentScriptAliveStates", Flags)!.SetValue(planner, result.ScriptAliveStates);
        var emit = plannerType.GetMethod("TryEmitFromTournamentScript", Flags)!;
        // Match TryEmitFromTournamentScript(combat, actors, humansFinished, out decision).
        // This fixture is all-bot, so end-turn steps are allowed.
        var args = new object?[] { combat, party, true, null };
        var emitted = (bool)emit.Invoke(planner, args)!;
        var decision = args[3] as TeamCombatPlanner.Decision;
        Check(emitted && decision is not null,
            "a cached step that is playable on the live board must emit a decision.");
        Check(ReferenceEquals(decision!.Player, script[step].Player),
            "the cached step must be emitted for the seat that owns it.");
        Check(decision.Move.Card is not null && script[step].Card is not null
            && decision.Move.Card.Id.Entry == script[step].Card!.Id.Entry,
            "the cached step must emit the card the script named.");
        Check(decision.Move.Reason.Contains("kernel-tournament-script", StringComparison.Ordinal),
            "the emitted decision must be marked as a cached tournament script step, not a fresh search.");
        Console.WriteLine("PASS: a winning tournament line is cached, a worse fresh ending cannot "
            + "displace it, and its next executable step is emitted as the decision.");
    }

    // (5c) THE WIRING, not just the helper: feed the planner a cached two-step line whose
    // recorded ending is a perfect victory, then drive the real decision path. The fresh
    // tournament cannot find an ending that beats a zero-loss full-HP victory, so the decision
    // must come from the cached script's second step rather than from the fresh first action.
    private static void ThePlannerKeepsTheCachedScriptWhenTheRescanCannotBeatIt()
    {
        var (party, combat) = Board("TOURNAMENT-SCRIPT-WIRING");
        var enemy = combat.HittableEnemies.First();
        CardModel Strike(Player player) => player.PlayerCombatState!.Hand.Cards
            .First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var first = Strike(party[0]);
        var second = Strike(party[1]);
        KernelTeamSearch.Action Step(Player player, CardModel card) => new(player, card, enemy,
            CardStateKey: CoopBots.Kernel.Vendor.CardChoiceSupport.ChoiceCardKey(card),
            CardStateOccurrence: 0);
        var script = new[] { Step(party[0], first), Step(party[1], second) };

        var planner = new KernelCombatPlanner();
        var plannerType = typeof(KernelCombatPlanner);
        plannerType.GetField("tournamentScript", Flags)!.SetValue(planner, script);
        plannerType.GetField("tournamentScriptRecord", Flags)!.SetValue(planner,
            new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [80, 80, 80, 80], 0, 0));
        plannerType.GetField("tournamentScriptCombat", Flags)!.SetValue(planner, combat);
        plannerType.GetField("tournamentScriptIndex", Flags)!.SetValue(planner, 1);
        var aliveAtOpening = combat.Players.Where(player => player.Creature.IsAlive)
            .Select(player => player.NetId).OrderBy(id => id).ToArray();
        plannerType.GetField("tournamentScriptAliveStates", Flags)!.SetValue(planner,
            new[] { aliveAtOpening, aliveAtOpening });

        var decided = DriveTournament(planner, combat, party, out var decision, "script-wiring-point");
        Check(decided && decision is not null,
            "the cached script must decide when the fresh tournament cannot beat its ending.");
        // THE STUTTER GUARD, CHECKED FIRST so this is the assertion a regression trips.
        // Live 2026-09-23 mixed table: the planner re-ran the whole tournament (both passes,
        // ~6 ms of the main thread every frame) on every stored step just to confirm the
        // stored ending. A stored step must be replayed, not re-derived: zero fresh passes.
        //
        // CHECKED (R2) 2026-09-23: mutating the stored-line branch to
        // `if (false && HasTournamentScript)` (i.e. restoring the per-step re-search) turns
        // this red, verbatim:
        //   System.Exception: following a stored step must not run a fresh tournament pass;
        //   that per-step re-search is what made the mixed-table animation stutter.
        //      at KernelRolloutScenarios.ThePlannerKeepsTheCachedScriptWhenTheRescanCannotBeatIt()
        Check((int)plannerType.GetField("tournamentPasses", Flags)!.GetValue(planner)! == 0,
            "following a stored step must not run a fresh tournament pass; that per-step "
            + "re-search is what made the mixed-table animation stutter.");
        Check(ReferenceEquals(decision!.Player, party[1]),
            "the cached script's second step must be emitted for its own seat.");
        Check(decision.Move.Reason.Contains("kernel-tournament-script", StringComparison.Ordinal),
            "the decision must be attributed to the cached script, not to the fresh search.");
        Console.WriteLine("PASS: the planner keeps and executes the cached script when the fresh "
            + "tournament cannot beat its recorded ending, without paying for a fresh search.");
    }

    // (5c-2) THE MIXED-TABLE WAIT. A stored line may contain an end-turn step for a driven seat
    // while a real player is still acting. BotRuntime will not submit it yet, so the old code
    // dropped the whole line and re-searched; measured live 2026-09-23 that guard alone fired
    // 180 times on one fight, each one a full fresh tournament (both passes) whose only finding
    // was the same WIPE. The step is VALID — it just cannot be submitted yet — so the planner
    // must keep the line and park until the humans finish.
    private static void EndTurnStepWaitsForTheHumansInsteadOfDroppingTheScript()
    {
        var (party, combat) = MixedBoard("MIXED-END-TURN");
        Check(!KernelCombatPlanner.AllSeatsDriven(combat),
            "the fixture must have a real player's seat, or the end-turn would be legal.");
        Check(!KernelCombatPlanner.TournamentCanEndTurn(combat, humansFinished: false),
            "an unfinished human must gate the end-turn step.");

        var endTurn = new KernelTeamSearch.Action(party[1], null, null, EndTurn: true);
        var planner = new KernelCombatPlanner();
        var plannerType = typeof(KernelCombatPlanner);
        plannerType.GetField("tournamentScript", Flags)!.SetValue(planner, new[] { endTurn });
        plannerType.GetField("tournamentScriptRecord", Flags)!.SetValue(planner,
            new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [80, 80, 80, 80], 0, 0));
        plannerType.GetField("tournamentScriptCombat", Flags)!.SetValue(planner, combat);
        plannerType.GetField("tournamentScriptIndex", Flags)!.SetValue(planner, 0);
        var alive = combat.Players.Where(player => player.Creature.IsAlive)
            .Select(player => player.NetId).OrderBy(id => id).ToArray();
        plannerType.GetField("tournamentScriptAliveStates", Flags)!.SetValue(planner, new[] { alive });

        var method = plannerType.GetMethod("TryTournamentDecision", Flags)!;
        var args = new object?[] { combat, party, false, "mixed-end-turn-point", null };
        var outcome = method.Invoke(planner, args)!.ToString()!;
        Check(outcome == "Pending",
            $"an end-turn that must wait for the humans must park, not re-search (got {outcome}).");
        Check(args[4] is null, "parking must not hand the caller a decision.");
        Check(plannerType.GetField("tournamentScript", Flags)!.GetValue(planner) is not null,
            "the valid end-turn line must be kept, not dropped.");
        Check((int)plannerType.GetField("tournamentScriptIndex", Flags)!.GetValue(planner)! == 0,
            "the cursor must not advance past a step that was never submitted.");
        Check((int)plannerType.GetField("tournamentPasses", Flags)!.GetValue(planner)! == 0,
            "parking on the humans must not run a fresh tournament; that churn was the "
            + "mixed-table stutter.");
        Console.WriteLine("PASS: a stored end-turn waiting for the humans keeps the line and "
            + "parks, instead of dropping it for a fresh tournament.");
    }

    // (5d) A SCRIPT'S ROOT INCLUDES WHO IS ALIVE. The live 2026-09-23 Decimillipede wipe had a
    // cached VICTORY line, then a seat died mid-fight; the old code skipped the dead seat's
    // steps and kept using the pre-death record, so fresh WIPE results were overruled. A death
    // (or revive) must drop the script and hand the decision back to the fresh tournament.
    private static void ACachedScriptIsDroppedWhenTheLivingSeatSetChanges()
    {
        var (party, combat) = Board("TOURNAMENT-SCRIPT-DEATH");
        var enemy = combat.HittableEnemies.First();
        CardModel Strike(Player player) => player.PlayerCombatState!.Hand.Cards
            .First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        KernelTeamSearch.Action Step(Player player, CardModel card) => new(player, card, enemy,
            CardStateKey: CoopBots.Kernel.Vendor.CardChoiceSupport.ChoiceCardKey(card),
            CardStateOccurrence: 0);
        var script = new[] { Step(party[0], Strike(party[0])), Step(party[1], Strike(party[1])) };

        var planner = new KernelCombatPlanner();
        var plannerType = typeof(KernelCombatPlanner);
        plannerType.GetField("tournamentScript", Flags)!.SetValue(planner, script);
        plannerType.GetField("tournamentScriptRecord", Flags)!.SetValue(planner,
            new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [80, 80, 80, 80], 0, 0));
        plannerType.GetField("tournamentScriptCombat", Flags)!.SetValue(planner, combat);
        plannerType.GetField("tournamentScriptIndex", Flags)!.SetValue(planner, 1);
        var aliveAtAdoption = combat.Players.Where(player => player.Creature.IsAlive)
            .Select(player => player.NetId).OrderBy(id => id).ToArray();
        plannerType.GetField("tournamentScriptAliveStates", Flags)!.SetValue(planner,
            new[] { aliveAtAdoption, aliveAtAdoption });

        // Kill a seat that the script was priced with. The next step belongs to ANOTHER seat,
        // so only the living-seat check can catch this; the old skip loop would have played it.
        party[0].Creature.SetCurrentHpInternal(0);
        Check(!party[0].Creature.IsAlive, "the fixture must actually kill the first seat.");

        var emit = plannerType.GetMethod("TryEmitFromTournamentScript", Flags)!;
        var args = new object?[] { combat, party, true, null };
        var emitted = (bool)emit.Invoke(planner, args)!;
        Check(!emitted && args[3] is null,
            "a script priced for a different living seat set must not emit a step after a death.");
        Check(plannerType.GetField("tournamentScript", Flags)!.GetValue(planner) is null,
            "the stale script must be cleared, not left to override the fresh tournament.");
        Console.WriteLine("PASS: a death invalidates the cached script instead of being skipped.");
    }

    // (5e) THE OTHER HALF: a script that PREDICTED the death must still be allowed to continue.
    // The per-step living-seat prediction is what separates "the line already knows this seat is
    // gone" from "the live death diverged from the line".
    private static void TheScriptContinuesThroughItsOwnPredictedDeath()
    {
        var (party, combat) = Board("TOURNAMENT-SCRIPT-PREDICTED-DEATH");
        var enemy = combat.HittableEnemies.First();
        CardModel Strike(Player player) => player.PlayerCombatState!.Hand.Cards
            .First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        KernelTeamSearch.Action Step(Player player, CardModel card) => new(player, card, enemy,
            CardStateKey: CoopBots.Kernel.Vendor.CardChoiceSupport.ChoiceCardKey(card),
            CardStateOccurrence: 0);
        var script = new[] { Step(party[0], Strike(party[0])), Step(party[1], Strike(party[1])) };
        var allAlive = combat.Players.Where(player => player.Creature.IsAlive)
            .Select(player => player.NetId).OrderBy(id => id).ToArray();
        var withoutFirst = allAlive.Where(id => id != party[0].NetId).ToArray();

        var planner = new KernelCombatPlanner();
        var plannerType = typeof(KernelCombatPlanner);
        plannerType.GetField("tournamentScript", Flags)!.SetValue(planner, script);
        plannerType.GetField("tournamentScriptRecord", Flags)!.SetValue(planner,
            new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [0, 70, 70, 70], 1, 0));
        plannerType.GetField("tournamentScriptCombat", Flags)!.SetValue(planner, combat);
        plannerType.GetField("tournamentScriptIndex", Flags)!.SetValue(planner, 1);
        // Step 0 expected the whole party; step 1 expected the first seat to be gone.
        plannerType.GetField("tournamentScriptAliveStates", Flags)!.SetValue(planner,
            new[] { allAlive, withoutFirst });

        party[0].Creature.SetCurrentHpInternal(0);
        Check(!party[0].Creature.IsAlive, "the fixture must actually kill the first seat.");

        var emit = plannerType.GetMethod("TryEmitFromTournamentScript", Flags)!;
        var args = new object?[] { combat, party, true, null };
        var emitted = (bool)emit.Invoke(planner, args)!;
        var decision = args[3] as TeamCombatPlanner.Decision;
        Check(emitted && decision is not null && ReferenceEquals(decision.Player, party[1]),
            "a step whose own living-seat prediction matches the live board must still be emitted.");
        Check(decision!.Move.Reason.Contains("kernel-tournament-script", StringComparison.Ordinal),
            "the continued step must stay attributed to the cached script.");
        Console.WriteLine("PASS: the cached script continues through a death it predicted, "
            + "and only diverging deaths invalidate it.");
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
        => Board(seed, Enumerable.Range(0, 4)
            .Select(i => BotRegistry.CreateId(BotDifficulty.Pro, i, i)).ToArray());

    /// <summary>
    /// Four seats where the first is a REAL player's (non-bot, not handed over) and the rest are
    /// synthetic bots — the shape every mixed-table gate is about.
    /// </summary>
    private static (Player[] Party, CombatState Combat) MixedBoard(string seed)
        => Board(seed,
        [
            0x5EED_0000_0000_0001UL,
            BotRegistry.CreateId(BotDifficulty.Pro, 1, 1),
            BotRegistry.CreateId(BotDifficulty.Pro, 2, 2),
            BotRegistry.CreateId(BotDifficulty.Pro, 3, 3),
        ]);

    private static (Player[] Party, CombatState Combat) Board(string seed, ulong[] ids)
    {
        var party = ids.Select(id => Player.CreateForNewRun<Deprived>(UnlockState.all, id)).ToArray();
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
