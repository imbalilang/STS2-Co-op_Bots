#if KERNEL_TESTS
// R2 actual: High6 -> legacy16 source mapping, actual CPU16: cap6 -> cap14 -> cap6.
// Baseline Rebuild0/run0 -> compiled mutant Rebuild0/named RED1 -> exact source
// and six DLL/PDB restoration Rebuild0/named GREEN0. CoopBots.dll changed in RED.
// Actual RED: System.InvalidOperationException: High DOP mapping: High must map its cap into both real planner requests, shared potion options and effective rollout DOP without changing the 6400ms normal request
// Proof: work/architecture-refactor-20261009/fixed-budget-performance-v3/high-dop6-preparation/execution-v6-20261010/r2-v2/proof.json
// This scheduling witness does not claim measured frame time, CPU use or live performance.
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class HighDopMappingScenarios
{
    internal const string Assertion = "High must map its cap into both real planner requests, shared potion options and effective rollout DOP without changing the 6400ms normal request";

    internal static void Run()
    {
        TestEnvironment.Ensure();
        var previous = KernelCombatPlanner.Perf;
        KernelCombatPlanner.Perf = KernelCombatPlanner.TournamentPerf.High;
        var players = Enumerable.Range(0, 4).Select(i =>
            Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(84001 + i))).ToArray();
        var oldControl = players.Select(p => AutoPilot.IsAutopiloted(p.NetId)).ToArray();
        var battle = new CombatState(runState: RunState.CreateForTest(players, seed: "HIGH-DOP6-MAPPING")) { RoundNumber = 1 };
        foreach (var player in players)
        {
            player.ResetCombatState(); battle.AddPlayer(player); player.MaxEnergy = 3;
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3);
            AutoPilot.Set(player.NetId, true);
        }
        var enemy = battle.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        battle.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        var normal = new KernelCombatPlanner();
        var decision = new KernelCombatPlanner();
        var probe = new Harmony("coopbots.high-dop6-mapping");
        var review = AccessTools.Method(typeof(KernelCombatPlanner), "ReviewMixedInput");
        try
        {
            var root = KernelCombatCapture.Capture(battle);
            if (root is null || !root.CanFork) throw new InvalidOperationException("High DOP fixture cannot capture its settled native root");
            var frozen = root.PartyStateText();
            // Actual idle fallback construction: request, options and run are
            // made by the production entry. Never fabricate them in the test.
            if (!(bool)Invoke(normal, "BeginTournamentPhase", battle, players, true)!)
                throw new InvalidOperationException("High DOP fixture did not enter the production no-potion phase");
            var request = (KernelDecisionRequest)Get(normal, "tournamentRequest")!;
            var baseOptions = (TournamentOptions)Get(normal, "tournamentOptions")!;
            var baseRun = (TournamentRun)Get(normal, "tournamentRun")!;
            var baseActualOptions = (TournamentOptions)AccessTools.Field(typeof(TournamentRun), "o").GetValue(baseRun)!;
            var baseDop = (int)AccessTools.Field(typeof(TournamentRun), "dop").GetValue(baseRun)!;
            baseRun.Dispose(); Set(normal, "tournamentRun", null);

            // Drive the same production options-with block used for bottles;
            // no Advance call is needed and no pooled rollout is submitted.
            var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
            phase.SetValue(normal, Enum.Parse(phase.FieldType, "Potions"));
            if (!(bool)Invoke(normal, "BeginTournamentPhase", battle, players, true)!)
                throw new InvalidOperationException("High DOP fixture did not enter the production potion phase");
            var potionRun = (TournamentRun)Get(normal, "tournamentRun")!;
            var potionOptions = (TournamentOptions)AccessTools.Field(typeof(TournamentRun), "o").GetValue(potionRun)!;
            var potionDop = (int)AccessTools.Field(typeof(TournamentRun), "dop").GetValue(potionRun)!;

            // The normal live decision constructs its request BEFORE the mixed
            // input review. Stop only after that real constructor; this is a
            // scheduling probe, not a mock request or a native-card oracle.
            probe.Patch(review, prefix: new HarmonyMethod(typeof(HighDopMappingScenarios), nameof(StopAfterRequest)));
            var point = KernelCombatPlanner.TournamentDecisionPoint(battle, 1);
            var outcome = Invoke(decision, "TryTournamentDecision", battle, players, true, point, null);
            probe.Unpatch(review, HarmonyPatchType.All, probe.Id);
            var liveRequest = (KernelDecisionRequest?)Get(decision, "tournamentRequest");
            var expected = Math.Clamp(Environment.ProcessorCount - 2, 1, 6);
            var legacy = Math.Clamp(Environment.ProcessorCount - 2, 1, 16);
            Console.WriteLine($"DIAG high-dop-mapping cpu={Environment.ProcessorCount} expected={expected} legacy={legacy} directRequest={request.MaxConcurrency} directBudget={request.BudgetMs} options={baseOptions.MaxDegreeOfParallelism} runDop={baseDop} potionRequest={potionOptions.Request?.MaxConcurrency} potionDop={potionDop} liveRequest={liveRequest?.MaxConcurrency} liveBudget={liveRequest?.BudgetMs} globalWorkerLimit={KernelSearchWorkPool.Shared.WorkerLimit} seats={players.Length}");
            if (request.MaxConcurrency != expected || request.BudgetMs != 6400
                || baseOptions.MaxDegreeOfParallelism != expected || baseDop != expected
                || !ReferenceEquals(baseOptions.Request, request)
                || !ReferenceEquals(baseActualOptions.Request, request) || baseActualOptions.IncludePotions
                || !ReferenceEquals(potionOptions.Request, request) || !potionOptions.IncludePotions || !potionOptions.PotionsOnly
                || potionOptions.MaxDegreeOfParallelism != expected || potionDop != expected
                || liveRequest is null || liveRequest.MaxConcurrency != expected || liveRequest.BudgetMs != 6400
                || outcome?.ToString() != "Pending" || liveRequest.IsCancelled
                || !ReferenceEquals(Get(normal, "tournamentRequest"), request)
                || ((IReadOnlyList<Player>)Get(normal, "tournamentParty")!).Count != 4
                || ((IReadOnlyList<Player>)Get(normal, "tournamentActors")!).Count != 4
                || root.PartyStateText() != frozen)
                throw new InvalidOperationException("High DOP mapping: " + Assertion);
            Console.WriteLine("PASS: High production requests and both tournament option paths share cap6 and the unchanged normal 6400ms allowance.");
        }
        finally
        {
            probe.Unpatch(review, HarmonyPatchType.All, probe.Id);
            normal.Reset(newCombat: true); decision.Reset(newCombat: true);
            for (var i = 0; i < players.Length; i++) AutoPilot.Set(players[i].NetId, oldControl[i]);
            KernelCombatPlanner.Perf = previous;
        }
    }

    private static bool StopAfterRequest(ref bool __result) { __result = true; return false; }
    private static object? Invoke(KernelCombatPlanner planner, string name, params object?[] args)
        => AccessTools.Method(typeof(KernelCombatPlanner), name).Invoke(planner, args);
    private static object? Get(KernelCombatPlanner planner, string name)
        => AccessTools.Field(typeof(KernelCombatPlanner), name).GetValue(planner);
    private static void Set(KernelCombatPlanner planner, string name, object? value)
        => AccessTools.Field(typeof(KernelCombatPlanner), name).SetValue(planner, value);
}

#endif
