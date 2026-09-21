#if KERNEL_TESTS
using System.Diagnostics;
using System.Reflection;
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

/// <summary>
/// MEASUREMENT ONLY — not a regression test, deliberately not wired into any suite.
///
/// Answers one question with a number instead of an opinion: what does ONE joint
/// full-bot search of the production opening shape actually cost on THIS machine,
/// in wall time, total allocation and PEAK memory?
///
/// Why the peak matters more than the total: the live logs report `alloc=` as
/// GC.GetTotalAllocatedBytes delta, which is cumulative and says nothing about how
/// much has to be resident at once. The development machine here has 8 GB of
/// physical memory in total, so "17-28 GB allocated" is survivable only if the
/// live peak is a small fraction of it. This probe measures the peak directly by
/// sampling the managed heap after every advance slice.
///
/// Budgets are deliberately small and swept rather than run at the production
/// 120 000 nodes: the point is the SLOPE (bytes and seconds per node), and the
/// slope is what extrapolates. Running the production budget here would risk
/// taking the developer's machine down, which is the very failure being measured.
/// </summary>
internal static class SearchCostProbe
{
    // The production opening-search shape, minus the budget. See
    // KernelCombatPlanner.OpeningSearchWidth / LookaheadRounds / SearchSliceMs.
    private const int ProductionWidth = 45;
    private const int ProductionRounds = 5;
    private const int ProductionSliceMs = 14;
    // Seconds. A probe that cannot be stopped is worse than no probe on a box this small.
    private const int WallCapSeconds = 90;

    /// <summary>
    /// List the encounter models this build knows, filtered by a substring. Ground truth for
    /// naming a specific encounter in the live-test sentinel — guessed type names do not compile
    /// and guessed ids do not resolve.
    /// </summary>
    internal static void ListEncounters(string filter)
    { 
        var byId = typeof(MegaCrit.Sts2.Core.Models.ModelDb)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(prop => prop.PropertyType.IsGenericType
                && prop.PropertyType.GetGenericArguments()[0].Name.Contains("Encounter"))
            .ToList();
        foreach (var prop in byId)
        {
            if (prop.GetValue(null) is not System.Collections.IEnumerable items) continue;
            foreach (var item in items)
            {
                if (item is null) continue;
                var id = item.GetType().GetProperty("Id")?.GetValue(item)?.ToString() ?? "?";
                var text = $"{prop.Name} {item.GetType().Name} {id}";
                if (filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("ENCOUNTER: " + text);
            }
        }
    }

    internal static void Run(string[] args)
    {
        // The console harness has no localization tables, so PowerModel.DynamicVars
        // throws inside KernelSession.Capture. Same stub KernelEngineScenarios
        // installs at its own entry (KernelEngineScenarios.cs:28); presentation
        // only, card and power numeric variables stay native.
        new HarmonyLib.Harmony("coopbots.test.kernel.presentation.probe").Patch(
            HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(SearchCostProbe), nameof(FormatText)));

        var budgets = new[] { 1000, 10000, 30000 };
        if (args.Contains("--perf-probe-short")) budgets = new[] { 200, 600 };

        Console.WriteLine("PROBE: search cost, 4 driven seats, production opening shape (width=45 depth=MaxValue rounds=5)");
        // Warm-up first and discarded: the first measurement otherwise pays for JIT of
        // the whole card-effect path, which made two different budgets print the same
        // wall time and hid the slope this probe exists to measure.
        Measure(200, quiet: true);
        Console.WriteLine("PROBE: node-budget  wall(ms)  nodes   route  stop            alloc(MB)  alloc/node  peakHeap(MB)  peak/node  ws(MB)");
        foreach (var budget in budgets) Measure(budget, quiet: false);

        // The plan's central bet, measured rather than argued: a rollout advances ONE
        // state forward instead of forking one per branch, so it should not be in the
        // same cost class as the search it is meant to replace.
        Console.WriteLine();
        Console.WriteLine("PROBE: ROLLOUT (single in-place session, aggro-greedy, no fork per node)");
        Console.WriteLine("PROBE: run  policy   terminal      steps  wall(ms)  alloc(MB)  alloc/step  peakHeap(MB)  ws(MB)");
        for (var run = 0; run < 5; run++) MeasureRollout(run);
        // The payoff measurement: what does a whole deployable decision cost when it is built
        // from rollouts instead of from a search — and what does it decide?
        Console.WriteLine();
        Console.WriteLine("PROBE: TOURNAMENT (first actions rolled out to a real ending)");
        Console.WriteLine("PROBE: topK  wall(ms)  rollouts  cutoffs  stop            alloc(MB)  peakHeap(MB)  chosen ending");
        foreach (var topK in new[] { 4, 8, 12 }) MeasureTournament(topK);
        Console.WriteLine();
        MeasureCommutationRate();
        Console.WriteLine("PROBE: done");
    }

    private static bool FormatText(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
    { __result = __instance.LocEntryKey; return false; }

    // How many rounds one rollout may advance before it is called a cutoff. Passed to
    // EndTurn, whose own contract is "start the next player turn while RoundsAdvanced+1
    // < maxRounds" — so a large number here is what makes a rollout run to a real
    // terminal instead of stopping at the search's five-round horizon.
    private const int RolloutRounds = 80;
    // A rollout that cannot finish in this many actions is a cutoff, exactly as the plan
    // requires (EstimatedCutoff, never a fabricated defeat).
    private const int RolloutStepCap = 600;

    /// <summary>
    /// One rollout through the production <see cref="KernelRollout"/> — a single session
    /// advanced in place, no fork anywhere. What is being measured is the per-step COST of
    /// the rollout shape, which is the number the plan's P1-02 rests on.
    /// </summary>
    private static void MeasureRollout(int run)
    {
        var (party, combat) = BuildBoard($"SEARCH-COST-ROLLOUT-{run}");
        var actors = party.ToArray();
        // A fresh capture per run: the rollout mutates its session in place, so reusing one
        // across policies would measure policy 2 and 3 starting from an already-advanced
        // board — and would quietly agree with itself.
        foreach (var kind in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth })
        {
            var session = KernelSession.Capture(combat);
            if (session is null) { Console.WriteLine("PROBE: capture returned null; aborting rollout"); return; }
            var process = Process.GetCurrentProcess();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var peakHeap = GC.GetTotalMemory(false);
            var watch = Stopwatch.StartNew();
            var outcome = KernelRollout.Run(session, party, actors,
                new RolloutOptions(kind, MaxSteps: RolloutStepCap, MaxRounds: RolloutRounds, Seed: run));
            watch.Stop();
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
            var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
            var allocMb = allocated / 1048576.0;
            var terminal = outcome.Terminal ? (outcome.Victory ? "VICTORY" : "wipe") : outcome.StopReason;
            Console.WriteLine($"PROBE: {run,-4} {kind,-8} {terminal,-13} {outcome.Steps,-6} "
                + $"{watch.Elapsed.TotalMilliseconds,-9:F0} {allocMb,-10:F1} "
                + $"{(outcome.Steps > 0 ? allocMb / outcome.Steps : 0),-11:F3} "
                + $"{peakHeap / 1048576.0,-12:F1} {process.WorkingSet64 / 1048576.0,-6:F0} rounds={outcome.RoundsAdvanced}");
        }
    }

    /// <summary>
    /// One deployable decision, priced end to end: fork the root per candidate, roll each to a
    /// real ending, pick the best. The budget is the plan's own ceiling for a mixed table, so a
    /// row that overruns it is a real miss and not a synthetic one.
    /// </summary>
    private static void MeasureTournament(int topK)
    {
        var (party, combat) = BuildBoard($"TOURNAMENT-COST-{topK}");
        var actors = party.ToArray();
        var root = KernelSession.Capture(combat);
        var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var peakHeap = GC.GetTotalMemory(false);
        var watch = Stopwatch.StartNew();
        var result = KernelTournament.Run(root, party, actors,
            new TournamentOptions(TopK: topK, Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: 1)),
            budgetMs: 5000);
        watch.Stop();
        peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
        var allocMb = (GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore) / 1048576.0;
        var chosen = result.Record is null ? "none"
            : result.Record.Victory ? "VICTORY" : result.Record.CutoffReason;
        Console.WriteLine($"PROBE: {topK,-5} {watch.Elapsed.TotalMilliseconds,-9:F0} {result.Rollouts,-9} "
            + $"{result.Cutoffs,-8} {result.StopReason,-15} {allocMb,-10:F1} "
            + $"{peakHeap / 1048576.0,-13:F1} {chosen}"
            + (result.Record?.Verified == true ? $" hp={string.Join('/', result.Record.PostCombatHp)}" : "")
            + $" ws={process.WorkingSet64 / 1048576.0:F0}");
    }

    /// <summary>
    /// How much the P3-04 white-list could actually buy: of the action pairs a first turn
    /// really offers, how many commute? Each pair costs two forks and two plays, and the answer
    /// decides whether collapsing orderings is worth building against a real deck — a
    /// theoretical saving that never materialises is the usual outcome for this kind of
    /// optimisation, so measure it before investing.
    /// </summary>
    private static void MeasureCommutationRate()
    {
        var (party, combat) = BuildBoard("COMMUTE-RATE");
        var actors = party.ToArray();
        var root = KernelSession.Capture(combat);
        var actions = new List<KernelTeamSearch.Action>();
        foreach (var player in actors)
        {
            if (!root.CanAct(player)) continue;
            foreach (var card in root.Hand(player))
            {
                if (!root.CanPlay(card)) continue;
                var target = root.Targets(card).FirstOrDefault(t => t is not null);
                actions.Add(new KernelTeamSearch.Action(player, card, target));
            }
        }
        var commuting = 0;
        var tested = 0;
        var reasons = new Dictionary<string, int>();
        for (var i = 0; i < actions.Count; i++)
        for (var j = i + 1; j < actions.Count; j++)
        {
            tested++;
            var verdict = KernelCommutation.Check(root, actions[i], actions[j]);
            if (verdict.Commutes) commuting++;
            else reasons[verdict.Reason.Split(':')[0]] = reasons.GetValueOrDefault(verdict.Reason.Split(':')[0]) + 1;
        }
        var rate = tested == 0 ? 0 : 100.0 * commuting / tested;
        Console.WriteLine($"PROBE: commutation: {commuting}/{tested} pairs commute ({rate:F1}%) "
            + $"over {actions.Count} playable actions; refusals: "
            + string.Join(", ", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
    }

    private static (Player[] Party, CombatState Combat) BuildBoard(string seed)
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
            Add<StrikeIronclad>(); Add<StrikeIronclad>(); Add<DefendIronclad>(); Add<Bash>(); Add<TwinStrike>();
            // A crossing flushes the hand and draws the next one, so the draw pile has to
            // exist or the round settles with an exception instead of a turn boundary.
            // Sized like a real act-2 deck rather than a stub: allocation per node scales
            // with how much state one fork has to copy, and a 15-card board understates it.
            for (var i = 0; i < 30; i++)
            {
                p.PlayerCombatState.DrawPile.AddInternal(i % 3 == 2
                    ? combat.CreateCard<DefendIronclad>(p) : combat.CreateCard<StrikeIronclad>(p));
            }
            // Powers are cloned per fork (PredictionUtils.CloneModelForSimulation), so
            // their number is a first-order term in the per-node cost, not decoration.
            MegaCrit.Sts2.Core.Models.ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.PlatingPower>()
                .ToMutable().ApplyInternal(p.Creature, 5, true);
        }
        // Three enemies with enough HP that the fight cannot end inside the horizon:
        // the measurement wants the search tree at its natural size, not a truncated one.
        for (var i = 0; i < 3; i++)
        {
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
            enemy.SetMaxHpInternal(300); enemy.SetCurrentHpInternal(300);
            // The move has to come FROM the state machine, not be a detached MoveState: a
            // crossing prepares the monster's next round by walking FollowUpState, and a
            // hand-built move has none ("行动 ATTACK 没有后继状态"). Learned the hard way
            // in this very probe — the first version threw round-exception at step 11 and
            // made every round transition look like a truncated branch.
            // Same construction as KernelEngineScenarios.ContinuationBoard.
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        }

        return (party, combat);
    }

    private static void Measure(int maxNodes, bool quiet)
    {
        var (party, combat) = BuildBoard($"SEARCH-COST-{maxNodes}");
        var actors = party.ToArray();
        // The production evaluator lambda, verbatim from KernelCombatPlanner: the engine
        // score when every seat is driven, the local evaluator otherwise. Using anything
        // cheaper here would measure a search nobody runs.
        // (The rollout deliberately uses none of this: its whole claim is that it needs no
        // evaluator per node, and pricing it with one would erase the difference.)
        var evaluation = new KernelCombatEvaluation(combat, actors, null, null, ProductionRounds);
        Func<KernelSession, double> evaluate = s =>
            CombatSolverEvaluator.TeamScore(s, combat, s.RoundsAdvanced + 1, new HashSet<uint>(),
                actors.Select(p => p.NetId)) is { } engineScore
                ? engineScore
                : evaluation.Evaluate(s).Score;

        var root = KernelSession.Capture(combat);
        if (root is null) { Console.WriteLine("PROBE: capture returned null; aborting"); return; }
        using var search = new KernelTeamSearch(root, actors, evaluate, new KernelTeamSearch.Options(
            Depth: int.MaxValue, Width: ProductionWidth, MaxNodes: maxNodes,
            IncludeEndTurns: true, MaxRounds: ProductionRounds));

        var process = Process.GetCurrentProcess();
        var gen0Before = GC.CollectionCount(0);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var peakHeap = GC.GetTotalMemory(false);
        var peakWorkingSet = process.WorkingSet64;
        var watch = Stopwatch.StartNew();
        var watchdog = Stopwatch.StartNew();
        var timedOut = false;
        while (!search.IsComplete)
        {
            search.Advance(TimeSpan.FromMilliseconds(ProductionSliceMs), () => true);
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            if (watchdog.Elapsed.TotalSeconds <= WallCapSeconds) continue;
            timedOut = true;
            search.FinishAtBudget();
            break;
        }
        watch.Stop();
        peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        var result = search.CompletedResult;

        var nodes = result?.ExpandedNodes ?? 0;
        var allocMb = allocated / 1048576.0;
        var peakMb = peakHeap / 1048576.0;
        if (quiet) return;
        Console.WriteLine($"PROBE: {maxNodes,-12} {watch.Elapsed.TotalMilliseconds,-9:F0} {nodes,-7} "
            + $"{(search.HasRoute ? "True " : "False")}  {result?.StopReason ?? "none",-15} "
            + $"{allocMb,-10:F0} {(nodes > 0 ? allocMb / nodes : 0),-11:F3} "
            + $"{peakMb,-12:F1} {(nodes > 0 ? peakMb / nodes : 0),-10:F4} {peakWorkingSet / 1048576.0,-6:F0} "
            + $"gen0={GC.CollectionCount(0) - gen0Before}"
            + (timedOut ? "   (WALL CAP HIT)" : ""));
    }
}
#endif
