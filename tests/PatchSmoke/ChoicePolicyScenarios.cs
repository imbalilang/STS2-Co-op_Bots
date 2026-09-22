using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// WHICH POLICY DECIDES A FIGHT — the switch shipped in 0.38.0 and widened to mixed tables on
// 2026-09-23.
//
// The user's call: one decision algorithm for all-bot and mixed tables. The tournament (C)
// takes the fight, not the segmented search (B). B answers an all-bot fight by searching for
// three minutes (up to eight) BEFORE the first card; C replans from the live board on every
// action. A mixed table uses the same path, with every non-driven human seat simulated as
// "end their turn" (KernelRollout) instead of being fed an invented action.
//
// What has to stay true, and is asserted here:
//   1. an all-bot table with no override is decided by the tournament;
//   2. a mixed table with no override is decided by the tournament too;
//   3. the override forces either policy, because the kernel suite pins the segmented search
//      (TestEnvironment does it) and the live-test sentinel needs both directions.
internal static class ChoicePolicyScenarios
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ChoicePolicy: " + message);
    }

    internal static void Run()
    {
        TestEnvironment.Ensure();
        // The console harness has no localization tables, and `KernelSession.Capture` warms
        // every power's DynamicVars — one of which formats a LocString. The kernel scenarios
        // stub the same method; this scenario runs earlier than they do, so it must not rely
        // on their patch being in place already.
        new HarmonyLib.Harmony("coopbots.test.choicepolicy.presentation").Patch(
            HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(ChoicePolicyScenarios), nameof(FormatText)));

        // R2 (2026-09-22, mutated the default to "the tournament only when forced":
        // `TournamentOverride == true`) — the first assertion below goes red, quoted:
        //   System.InvalidOperationException: ChoicePolicy: an all-bot table must still
        //   produce a decision (no-decision/Pending).
        //     at ChoicePolicyScenarios.Run()
        // `Pending` is the segmented search taking the tick, which is exactly what the
        // default is supposed to prevent.

        // 1. Shipped default on an all-bot table: the tournament decides. The decision says so
        //    in its own reason string (`tournament:rollouts=…`), which is also what the live log
        //    prints, so this asserts the same fact the operator reads.
        var allBots = AllBots(seed: "CHOICEPOLICY-BOTS");
        var decision = Decide(allBots, overrideValue: null, out var reason);
        Check(decision is not null, $"an all-bot table must still produce a decision ({reason}).");
        Check(reason.Contains("tournament:", StringComparison.Ordinal),
            $"the default for an all-bot table is the tournament, got '{reason}'.");

        // 2. A human at the table is now decided by the same tournament. The human seat is not
        //    an actor: it is simulated as "end their turn" during the roll-out.
        var withHuman = AllBots(seed: "CHOICEPOLICY-HUMAN", humanSeat: true);
        Decide(withHuman, overrideValue: null, out var mixedReason);
        Check(mixedReason.Contains("tournament:", StringComparison.Ordinal),
            $"the default for a mixed table is the tournament, got '{mixedReason}'.");

        // 3. The override still forces either policy, on both table shapes.
        Decide(allBots, overrideValue: false, out var forcedSearch);
        Check(!forcedSearch.Contains("tournament:", StringComparison.Ordinal),
            $"forcing the segmented search must keep the tournament out, got '{forcedSearch}'.");
        Decide(allBots, overrideValue: true, out var forcedTournament);
        Check(forcedTournament.Contains("tournament:", StringComparison.Ordinal),
            $"forcing the tournament must make it decide, got '{forcedTournament}'.");
        Decide(withHuman, overrideValue: false, out var mixedForcedSearch);
        Check(!mixedForcedSearch.Contains("tournament:", StringComparison.Ordinal),
            $"forcing the segmented search must also keep the tournament out of a mixed table, "
            + $"got '{mixedForcedSearch}'.");

        // 4. While a human is acting, end-turn is not a tournament candidate: BotRuntime would
        //    refuse it and the same decision point would recompute every tick. The all-bot gate
        //    stays true either way.
        Check(!KernelCombatPlanner.TournamentCanEndTurn(withHuman.Combat, humansFinished: false),
            "a mixed table with a human still acting must not offer end-turn.");
        Check(KernelCombatPlanner.TournamentCanEndTurn(withHuman.Combat, humansFinished: true),
            "a mixed table may offer end-turn once every human has finished.");
        Check(KernelCombatPlanner.TournamentCanEndTurn(allBots.Combat, humansFinished: false),
            "an all-bot table must keep offering end-turn even when the caller passes false; "
            + "there is no human to wait for.");

        // LEAVE THE PIN ALONE. `TestEnvironment` pins the segmented search for the whole suite
        // (every later planner fixture drives one bot in its own combat, which is an all-bot
        // table). Clearing it here instead of restoring it put the tournament under those
        // fixtures: measured 2026-09-22, the kernel suite stopped finishing — its last line was
        // a PASS and the process never returned.
        Console.WriteLine("PASS: the roll-out tournament decides both all-bot and mixed tables by "
            + "default, a human still acting suppresses end-turn candidates, and the override "
            + "forces either policy — which is what keeps the kernel suite on the segmented search "
            + "it guards.");
    }

    /// <summary>
    /// Poll with the override in place and read back what decided.
    ///
    /// THE TOURNAMENT IS SLICED NOW, so one Poll is one FRAME and its answer arrives across
    /// several. Drive it the way BotRuntime does — but ONLY while the tournament is what is
    /// running: the segmented search also answers Pending, and its opening budget is minutes,
    /// which this scenario must not spend (the override-false assertions are satisfied by
    /// "the tournament did not decide").
    ///
    /// The actors are the driven seats, not the whole party: a mixed table's human seat is not
    /// an actor and must never produce a candidate.
    /// </summary>
    private static TeamCombatPlanner.Decision? Decide(BoardFixture fixture, bool? overrideValue, out string reason)
    {
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = overrideValue;
        try
        {
            var actors = fixture.Party.Where(p => AutoPilot.Drives(p.NetId)).ToArray();
            var root = KernelSession.Capture(fixture.Combat);
            var res = KernelTournament.Run(root!, root!.Party, actors, new TournamentOptions(6,
                Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: fixture.Combat.RoundNumber), IncludePotions: false), 1200);
            Console.WriteLine($"PROBE2 canFork={root!.CanFork} party={root.Party.Count} actors={actors.Length} first={res.FirstAction?.Card?.Id.Entry ?? "null"} stop={res.StopReason} rollouts={res.Rollouts}");
            var planner = new KernelCombatPlanner();
            var drives = KernelCombatPlanner.TournamentDrives(fixture.Combat, actors);
            var status = planner.Poll(fixture.Combat, actors, 0, null, false,
                BotDifficulty.Pro, out var decision);
            // A FRAME IS ~16 ms, NOT AN ITERATION — the parallel tournament's workers run at
            // BelowNormal on their own threads, so polling in a tight counting loop would exhaust
            // it before a single rollout finished. Sleep, and bound by the wall clock.
            var deadline = Environment.TickCount64 + 60_000;
            while (status == KernelCombatPlanner.Status.Pending && drives
                   && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(1);
                status = planner.Poll(fixture.Combat, actors, 0, null, false,
                    BotDifficulty.Pro, out decision);
            }
            if (status == KernelCombatPlanner.Status.Pending && drives)
                throw new InvalidOperationException("ChoicePolicy: the sliced tournament never finished.");
            reason = decision?.Move.Reason ?? $"no-decision/{status}";
            return decision;
        }
        finally { KernelCombatPlanner.TournamentOverride = previous; }
    }

    private readonly record struct BoardFixture(Player[] Party, CombatState Combat);

    /// <summary>
    /// Four seats in a playable combat. <paramref name="humanSeat"/> swaps the last seat for a
    /// real player id that nothing drives, which is what makes the table mixed.
    /// </summary>
    private static BoardFixture AllBots(string seed, bool humanSeat = false)
    {
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(
            UnlockState.all, i == 3 && humanSeat
                ? 76561198109201343UL
                : BotRegistry.CreateId(BotDifficulty.Pro, i, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState();
            combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80);
            p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(3);
            void Add<T>() where T : CardModel => p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<T>(p));
            Add<StrikeIronclad>();
            Add<StrikeIronclad>();
            Add<StrikeIronclad>();
            Add<StrikeIronclad>();
            for (var draw = 0; draw < 6; draw++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        // Two enemies, exactly like the rollout fixture the tournament's own suite uses: one
        // enemy made the tournament decline in the first cut of this scenario, and a fixture
        // that differs from the working one is a fixture that measures the difference.
        for (var i = 0; i < 2; i++)
        {
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(enemy);
            enemy.Monster!.SetUpForCombat();
            enemy.SetMaxHpInternal(40);
            enemy.SetCurrentHpInternal(40);
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        }
        return new BoardFixture(party, combat);
    }

    private static bool FormatText(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
    { __result = __instance.LocEntryKey; return false; }
}
