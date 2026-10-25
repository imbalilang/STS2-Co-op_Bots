using System.Diagnostics;
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

internal static class MixedSearchThreadingScenarios
{
    private static TournamentRun? heldRun;
    private static int caller, blocked, observedThread;
    private static readonly ManualResetEventSlim entered = new(false), release = new(false);

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.mixed-search-threading.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedSearchThreadingScenarios), nameof(Format)));
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null;
        try { foreach (var test in new (string Name, Action Body)[] {
            ("native-worker", NativeWorker), ("cursor-progress", CursorProgress), ("obsolete-cancel", ObsoleteCancel),
            ("production-options", ProductionOptions), ("settled-prefix", SettledPrefix), ("longest-prefix", LongestPrefix),
            ("failure-memo", FailureMemo), ("failure-before-phase", FailureBeforePhase), ("vigor-death", VigorDeath),
            ("finished-priority", FinishedPriority), ("input-prefix", InputPrefix), ("potion-worker", PotionWorker) })
            if (selected is null || selected == test.Name)
            { test.Body(); Console.WriteLine("PASS: mixed search threading " + test.Name); }
        } finally { KernelCombatPlanner.TournamentOverride = previous; presentation.UnpatchAll(presentation.Id); }
    }
    private static bool Format(ref string __result) { __result = "fixture"; return false; }

    private static IDisposable Native(object combat)
        => (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;

    // A barrier at an actual production rollout step proves that Poll cannot
    // execute or wait for that native computation. Card effects are unchanged.
    private static void BeforeStep(TournamentRun __instance)
    {
        if (!ReferenceEquals(__instance, heldRun) || Interlocked.Exchange(ref blocked, 1) != 0) return;
        observedThread = Environment.CurrentManagedThreadId; entered.Set();
        if (observedThread != caller && !release.Wait(5000)) throw new TimeoutException("native worker barrier was not released");
    }

    private static void NativeWorker()
    {
        var (combat, bot, enemy) = KernelPlanningStateScenarios.Board();
        using var native = Native(combat);
        var root = KernelSession.Capture(combat);
        var originalHp = enemy.CurrentHp; var originalEnergy = bot.PlayerCombatState!.Energy;
        var request = new KernelDecisionRequest(99101, 1800, 2);
        using var run = new TournamentRun(root, combat.Players.ToArray(), [bot],
            new TournamentOptions(TopK: 4, Rollout: new(MaxSteps: 80, MaxRounds: 2), MaxDegreeOfParallelism: 2,
                EnableJointSearch: true, ReserveMs: 0) {
                Request = request, MixedIncrementalOpenings = true, RunInBackground = true }, 1500);
        var harmony = new Harmony("coopbots.mixed-search-threading.worker");
        harmony.Patch(AccessTools.Method(typeof(TournamentRun), "StepRollout"),
            prefix: new HarmonyMethod(typeof(MixedSearchThreadingScenarios), nameof(BeforeStep)));
        caller = Environment.CurrentManagedThreadId; blocked = observedThread = 0;
        entered.Reset(); release.Reset(); heldRun = run;
        try
        {
            run.Advance(TimeSpan.FromMilliseconds(2));
            Check(entered.Wait(3000) && observedThread != caller && run.WorkerThreadId == observedThread,
                "full tournament native reference must run on its background owner, never the game caller");
            var maximum = 0d;
            for (var i = 0; i < 100; i++)
            {
                var clock = Stopwatch.StartNew();
                Check(!run.Advance(TimeSpan.FromMilliseconds(2)), "blocked native computation cannot publish a completed result");
                maximum = Math.Max(maximum, clock.Elapsed.TotalMilliseconds);
            }
            Check(maximum < 50, "main-thread polling must stay nonblocking while a native rollout step is held");
            release.Set();
            var timeout = Stopwatch.StartNew();
            while (!run.Advance(TimeSpan.FromMilliseconds(2)) && timeout.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            Check(run.Result is not null && bot.PlayerCombatState!.Energy == originalEnergy && enemy.CurrentHp == originalHp,
                "background simulation must publish once without spending real resources or changing the live target");
            Console.WriteLine($"THREAD-PERF: held-native-step pollMaxMs={maximum:F3} caller={caller} worker={observedThread}");
        }
        finally { release.Set(); request.Cancel("fixture finished"); heldRun = null; harmony.UnpatchAll(harmony.Id); }
    }

    private static void CursorProgress()
    {
        using var b = new MixedBackgroundReplanScenarios.Board(); b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        var job = Get(b.Planner, "mixedReplan");
        // Polling only observes work. Let Bot execution move its cursor without
        // inventing another settled human revision or refilling optional credit.
        Set(b.Planner, "tournamentScriptIndex", 1);
        for (var i = 0; i < 10; i++) b.Planner.AdvanceMixedReplan(b.Combat, 5000 + i * 2000);
        Check((int)Get(b.Planner, "mixedReplanStarts")! == 1 && !(bool)Get(b.Planner, "mixedReplanDirty")!,
            "Bot cursor progress must consume one human revision without cancelling and readmitting its search");
        b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 30000);
        Check((int)Get(b.Planner, "mixedReplanStarts")! == 2,
            "a genuinely newer settled human input must still admit its own coalesced comparison");
    }

    private static void ObsoleteCancel()
    {
        using var b = new MixedBackgroundReplanScenarios.Board(); b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        var request = (KernelDecisionRequest)Get(Get(b.Planner, "mixedReplan")!, "Request")!;
        Set(b.Planner, "tournamentScriptIndex", 1);
        b.Planner.AdvanceMixedReplan(b.Combat, 1001);
        Check(request.IsCancelled && Get(b.Planner, "mixedReplan") is null && !(bool)Get(b.Planner, "mixedReplanDirty")!,
            "an optional root made unadoptable by Bot progress must stop computing without scheduling another copy");
    }

    private static void ProductionOptions()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        foreach (var finished in new[] { false, true })
        {
            var planner = new KernelCombatPlanner();
            try
            {
                AccessTools.Method(typeof(KernelCombatPlanner), "BeginTournamentPhase").Invoke(planner, [b.Combat, new[] { b.Bot }, finished]);
                var options = (TournamentOptions)Get(planner, "tournamentOptions")!;
                Check(options.RunInBackground && options.UsePolicyPortfolio && options.EnableJointSearch,
                    "both human readiness states must wire the complete tournament to background execution");
            }
            finally { planner.Reset(newCombat: true); }
        }
        AutoPilot.Set(b.Human.NetId, true);
        var allBot = new KernelCombatPlanner();
        try
        {
            AccessTools.Method(typeof(KernelCombatPlanner), "BeginTournamentPhase").Invoke(allBot, [b.Combat, new[] { b.Bot }, true]);
            Check(!((TournamentOptions)Get(allBot, "tournamentOptions")!).RunInBackground,
                "all-Bot planning must keep its existing execution and future-round optimization path");
        }
        finally { allBot.Reset(newCombat: true); }
    }

    private static void SettledPrefix()
    {
        var (combat, bot, _) = KernelPlanningStateScenarios.Board();
        using var native = Native(combat);
        var root = KernelSession.Capture(combat);
        using var run = new TournamentRun(root, combat.Players.ToArray(), [bot],
            new TournamentOptions(IncludeEndTurns: true, EnableJointSearch: true, ReserveMs: 0) {
                MixedIncrementalOpenings = true, Request = new(99102, 3000) }, 2500);
        var timer = Stopwatch.StartNew();
        while (run.LatestSettledProposal is null && !run.Done && timer.ElapsedMilliseconds < 3000)
            run.Advance(TimeSpan.FromTicks(1));
        var saved = run.LatestSettledProposal;
        Check(saved?.Script is { Count: > 0 } && saved.Record is { Verified: false, Victory: false },
            "the first settled native policy prefix must survive as a proposal before the full search finishes");
        var after = root.Fork();
        Check(after.ReplayPlanned(saved!.Script![0], 80, out _) && after.Energy(bot) < root.Energy(bot),
            "the retained native prefix must actually pay its recorded physical card");
        run.Cancel("later input");
        while (!run.Advance(TimeSpan.FromMilliseconds(1))) Thread.Sleep(1);
        Check(run.Result?.FirstAction is null && ReferenceEquals(run.LatestSettledProposal, saved),
            "cancelled quality must be withdrawn while the immutable historical native proposal remains readable");
    }

    private static void LongestPrefix()
    {
        var (combat, bot, enemy) = KernelPlanningStateScenarios.Board();
        using var native = Native(combat);
        var root = KernelSession.Capture(combat);
        using var run = new TournamentRun(root, combat.Players.ToArray(), [bot],
            new TournamentOptions(IncludeEndTurns: true, EnableJointSearch: true, ReserveMs: 0) {
                MixedIncrementalOpenings = true, Request = new(99105, 3000) }, 2500);
        var timer = Stopwatch.StartNew();
        while ((run.LatestSettledProposal?.Script?.Count ?? 0) < 2 && !run.Done && timer.ElapsedMilliseconds < 3000)
            run.Advance(TimeSpan.FromTicks(1));
        var saved = run.LatestSettledProposal;
        Check(saved?.Script is { Count: >= 2 },
            "keep the longest settled native prefix instead of forcing a full repair after each single card");
        var replay = root.Fork();
        foreach (var action in saved!.Script!) Check(replay.ReplayPlanned(action, 80, out _), "retained prefix must replay all actual paid actions");
        Check(replay.Energy(bot) == root.Energy(bot) - 2 && replay.Hp(enemy) == root.Hp(enemy) - 12,
            "two retained native Strikes must settle once on the same original root");
    }

    private static void FailureMemo() => FailureAtRoot(true);
    private static void FailureBeforePhase() => FailureAtRoot(false);
    private static void FailureAtRoot(bool startedRun)
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        Set(b.Planner, "tournamentScript", null);
        var request = new KernelDecisionRequest(99103, 3000);
        var root = KernelCombatCapture.Capture(b.Combat);
        using var run = new TournamentRun(root, b.Combat.Players.ToArray(), [b.Bot],
            new TournamentOptions { MixedIncrementalOpenings = true, Request = request }, 1000);
        Set(b.Planner, "tournamentRun", startedRun ? run : null); Set(b.Planner, "tournamentRequest", request);
        var outcome = AccessTools.Method(typeof(KernelCombatPlanner), "ParkStoppedMixedSearch")
            .Invoke(b.Planner, ["unchanged-native-root", "phase-deadline", true]);
        Check(outcome!.ToString() == "Declined" && Get(b.Planner, "tournamentRun") is null
            && Get(b.Planner, "tournamentRequest") is null && Get(b.Planner, "mixedStoppedPoint") is null,
            "a funded no-candidate failure must retire once through the existing decline memo, never schedule a timed full restart");
    }

    private static void FinishedPriority()
    {
        using var b = new MixedBackgroundReplanScenarios.Board(); b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        var request = (KernelDecisionRequest)Get(Get(b.Planner, "mixedReplan")!, "Request")!;
        b.Planner.AdvanceMixedReplan(b.Combat, 1001, humansFinished: true);
        Check(request.IsCancelled && Get(b.Planner, "mixedReplan") is null
            && ReferenceEquals(Get(b.Planner, "tournamentScript"), b.Script),
            "human completion must retire optional compute while retaining the fast executable suffix");
    }

    private static void InputPrefix()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        Set(b.Planner, "tournamentScript", null);
        AccessTools.Method(typeof(KernelCombatPlanner), "BeginTournamentPhase").Invoke(b.Planner, [b.Combat, new[] { b.Bot }, false]);
        var run = (TournamentRun)Get(b.Planner, "tournamentRun")!;
        var clock = Stopwatch.StartNew();
        while (run.LatestSettledProposal is null && clock.ElapsedMilliseconds < 4000)
        { run.Advance(TimeSpan.FromMilliseconds(1)); Thread.Sleep(1); }
        Check(run.LatestSettledProposal is not null, "native worker must produce a settled physical prefix before input");
        b.Input();
        Check(Get(b.Planner, "mixedInterruptedBaseline") is not null,
            "settled human input must retain the worker's completed native prefix before cancelling its quality search");
        var generation = Get(b.Planner, "tournamentRootGeneration");
        var status = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out var decision);
        Check(status == KernelCombatPlanner.Status.Ready && decision?.Move.Card is not null
            && Equals(generation, Get(b.Planner, "tournamentRootGeneration")),
            "the real human input must release a legal historical prefix without restarting a full tournament");
    }

    private static void PotionWorker()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        var bottle = ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.FirePotion>().ToMutable();
        b.Bot.AddPotionInternal(bottle, silent: true);
        var root = KernelCombatCapture.Capture(b.Combat);
        var replay = new KernelScriptReplayRun(root, b.Combat.Players.ToArray(),
            new TournamentResult(b.Script[0], null, [], 0, 0, "native-reference") { Script = b.Script }, 80);
        while (!replay.Done) replay.Advance(TimeSpan.FromMilliseconds(1));
        Set(b.Planner, "tournamentRoot", root); Set(b.Planner, "tournamentParty", b.Combat.Players.ToArray());
        Set(b.Planner, "tournamentActors", new[] { b.Bot }); Set(b.Planner, "tournamentFirstPass", replay.Result);
        Set(b.Planner, "tournamentRequest", new KernelDecisionRequest(99104, 2500));
        var timer = Stopwatch.StartNew();
        while (!(bool)AccessTools.Method(typeof(KernelCombatPlanner), "AdvanceMixedPotionReplay").Invoke(b.Planner, [])!
            && timer.ElapsedMilliseconds < 5000) Thread.Sleep(1);
        var work = (KernelBackgroundValue<KernelPotionContinuationRun>)Get(b.Planner, "mixedPotionReplayWork")!;
        Check(work.Operation.IsCompleted && work.Operation.WorkerThreadId != Environment.CurrentManagedThreadId
            && Get(b.Planner, "tournamentPotionReplay") is KernelPotionContinuationRun { Done: true },
            "native potion opening construction and complete comparison must stay on the background owner");
    }

    private static void VigorDeath()
    {
        var (combat, bot, enemy) = KernelPlanningStateScenarios.Board();
        using var native = Native(combat);
        bot.Creature.SetCurrentHpInternal(1);
        ModelDb.Power<VigorPower>().ToMutable().ApplyInternal(bot.Creature, 5, true);
        ModelDb.Power<ThornsPower>().ToMutable().ApplyInternal(enemy, 3, true);
        var root = KernelSession.Capture(combat);
        var after = root.Fork();
        var card = bot.PlayerCombatState!.Hand.Cards.OfType<StrikeIronclad>().First();
        Check(after.Play(card, enemy, out var reason), "Vigor native attack must settle: " + reason);
        var fork = after.Fork();
        Check(fork.Hp(bot.Creature) == 0 && root.Hp(bot.Creature) == 1,
            "a Vigor attacker dying to thorns must leave a settled forkable transaction and an unchanged root");
    }

    private static object? Get(object obj, string field) => AccessTools.Field(obj.GetType(), field).GetValue(obj);
    private static void Set(object obj, string field, object? value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Mixed search threading: " + message); }
}

// Actual 2026-10-25 R2: mutant build0/RED1 -> exact source restore -> build0/GREEN0.
// Receipts: work/stutter-r2/receipts.json and work/stutter-r2-final/receipts.json.
// The initial Vigor build failure is retained and excluded from valid controls.
// native-worker: System.InvalidOperationException: Mixed search threading: full tournament native reference must run on its background owner, never the game caller
// production-options: System.InvalidOperationException: Mixed search threading: both human readiness states must wire the complete tournament to background execution
// cursor-progress: System.InvalidOperationException: Mixed search threading: Bot cursor progress must consume one human revision without cancelling and readmitting its search
// settled-prefix: System.InvalidOperationException: Mixed search threading: the first settled native policy prefix must survive as a proposal before the full search finishes
// failure-memo: System.InvalidOperationException: Mixed search threading: a funded no-candidate failure must retire once through the existing decline memo, never schedule a timed full restart
// vigor-death: System.InvalidOperationException: Cannot fork during Vigor attack resolution.
// finished-priority: System.InvalidOperationException: Mixed search threading: human completion must retire optional compute while retaining the fast executable suffix
// input-prefix: System.InvalidOperationException: Mixed search threading: settled human input must retain the worker's completed native prefix before cancelling its quality search
// potion-worker: System.InvalidOperationException: Mixed search threading: native potion opening construction and complete comparison must stay on the background owner

// Actual RED/GREEN, work/stutter-r2-before-phase/receipts.json:
// failure-before-phase: System.InvalidOperationException: Mixed search threading: a funded no-candidate failure must retire once through the existing decline memo, never schedule a timed full restart

// Actual RED/GREEN, work/stutter-r2-longest-prefix/receipts.json:
// longest-prefix: System.InvalidOperationException: Mixed search threading: keep the longest settled native prefix instead of forcing a full repair after each single card

// Actual RED/GREEN, work/stutter-r2-obsolete-cancel/receipts.json:
// obsolete-cancel: System.InvalidOperationException: Mixed search threading: an optional root made unadoptable by Bot progress must stop computing without scheduling another copy
