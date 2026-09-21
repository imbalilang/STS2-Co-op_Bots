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

// WHICH POLICY DECIDES AN ALL-BOT FIGHT — the switch shipped in 0.38.0.
//
// The user's call: hand every seat to the AI and the tournament (C) takes the fight, not the
// segmented search (B). B answers an all-bot fight by searching for three minutes (up to eight)
// BEFORE the first card, and a human who has handed the seats over still watches that silence.
// C replans from the live board on every action, so a decision costs its own budget instead.
//
// What has to stay true, and is asserted here:
//   1. an all-bot table with no override is decided by the tournament;
//   2. a table with a human in it is NOT — that human keeps the interactive bounded search;
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

        // 2. A human at the table keeps the segmented search: the tournament never drives a
        //    mixed table (its rollouts would price a seat that does not act on its own).
        var withHuman = AllBots(seed: "CHOICEPOLICY-HUMAN", humanSeat: true);
        Decide(withHuman, overrideValue: null, out var mixedReason);
        Check(!mixedReason.Contains("tournament:", StringComparison.Ordinal),
            $"a mixed table must not be decided by the tournament, got '{mixedReason}'.");

        // 3. The override still forces either policy.
        Decide(allBots, overrideValue: false, out var forcedSearch);
        Check(!forcedSearch.Contains("tournament:", StringComparison.Ordinal),
            $"forcing the segmented search must keep the tournament out, got '{forcedSearch}'.");
        Decide(allBots, overrideValue: true, out var forcedTournament);
        Check(forcedTournament.Contains("tournament:", StringComparison.Ordinal),
            $"forcing the tournament must make it decide, got '{forcedTournament}'.");

        // LEAVE THE PIN ALONE. `TestEnvironment` pins the segmented search for the whole suite
        // (every later planner fixture drives one bot in its own combat, which is an all-bot
        // table). Clearing it here instead of restoring it put the tournament under those
        // fixtures: measured 2026-09-22, the kernel suite stopped finishing — its last line was
        // a PASS and the process never returned.
        Console.WriteLine("PASS: an all-bot table is decided by the roll-out tournament by default, a "
            + "mixed table is not, and the override forces either policy — which is what keeps the "
            + "kernel suite on the segmented search it guards.");
    }

    /// <summary>Poll once with the override in place and read back what decided.</summary>
    private static TeamCombatPlanner.Decision? Decide(BoardFixture fixture, bool? overrideValue, out string reason)
    {
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = overrideValue;
        try
        {
            var root = KernelSession.Capture(fixture.Combat);
            var res = KernelTournament.Run(root!, root!.Party, fixture.Party, new TournamentOptions(6,
                Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: fixture.Combat.RoundNumber), IncludePotions: false), 1200);
            Console.WriteLine($"PROBE2 canFork={root!.CanFork} party={root.Party.Count} first={res.FirstAction?.Card?.Id.Entry ?? "null"} stop={res.StopReason} rollouts={res.Rollouts}");
            var planner = new KernelCombatPlanner();
            var status = planner.Poll(fixture.Combat, fixture.Party, 0, null, false,
                BotDifficulty.Pro, out var decision);
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
