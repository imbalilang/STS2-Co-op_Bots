using System.Diagnostics;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

// Only the already-completed tournament candidate is injected. Its paid card
// prefix comes from native replay. The real production result/opportunity/rescue
// scheduler then runs across frames; the barrier changes timing, not outcomes.
internal static class MixedPotionResultConsumptionScenarios
{
    private static readonly ManualResetEventSlim entered = new(false), release = new(false);
    private static int opportunityCalls;
    private static bool holdEmergency, holdBenefit;
    private static int benefitCaller, benefitThread, benefitCalls, stoppedRootForks;
    private static KernelSession? stoppedRoot;
    private static void CountStoppedFork(KernelSession __instance)
    { if (ReferenceEquals(stoppedRoot, __instance)) Interlocked.Increment(ref stoppedRootForks); }
    private static void CountOpportunity() => Interlocked.Increment(ref opportunityCalls);
    private static void BeforeEmergency()
    {
        if (!holdEmergency) return;
        entered.Set();
        if (!release.Wait(5000)) throw new TimeoutException("emergency result barrier was not released");
    }
    private static void BeforeBenefit()
    {
        if (!holdBenefit) return;
        Interlocked.Increment(ref benefitCalls);
        benefitThread = Environment.CurrentManagedThreadId; entered.Set();
        if (benefitThread != benefitCaller && !release.Wait(5000))
            throw new TimeoutException("native potion benefit barrier was not released");
    }
    private static bool Format(ref string __result) { __result = "fixture"; return false; }

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null;
        var patches = new Harmony("coopbots.mixed-potion-result-consumption");
        patches.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedPotionResultConsumptionScenarios), nameof(Format)));
        patches.Patch(AccessTools.Method(typeof(KernelPotionOpportunity), nameof(KernelPotionOpportunity.Choose)),
            prefix: new HarmonyMethod(typeof(MixedPotionResultConsumptionScenarios), nameof(CountOpportunity)));
        patches.Patch(AccessTools.Method(typeof(KernelCombatPlanner), "EmergencyPotionComparisons"),
            prefix: new HarmonyMethod(typeof(MixedPotionResultConsumptionScenarios), nameof(BeforeEmergency)));
        patches.Patch(AccessTools.Method(typeof(KernelPotionPolicy), nameof(KernelPotionPolicy.ImmediateBenefit)),
            prefix: new HarmonyMethod(typeof(MixedPotionResultConsumptionScenarios), nameof(BeforeBenefit)));
        patches.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.Fork)),
            prefix: new HarmonyMethod(typeof(MixedPotionResultConsumptionScenarios), nameof(CountStoppedFork)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("no-rescue-once", NoRescueOnce), ("reset-consumption", ResetConsumption), ("final-benefit-worker", FinalBenefitWorker), ("benefit-deadline", BenefitDeadline) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: mixed potion result consumption " + test.Name); }
        }
        finally
        {
            stoppedRoot = null; holdEmergency = holdBenefit = false; release.Set();
            var drain = Stopwatch.StartNew();
            while (!KernelBackgroundOperation.IsIdle && drain.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            patches.UnpatchAll(patches.Id); KernelCombatPlanner.TournamentOverride = previous;
        }
    }

    private static void NoRescueOnce() => ConsumeResults(1);
    private static void ResetConsumption() => ConsumeResults(2);
    private static void ConsumeResults(int repeats)
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        b.Bot.Creature.SetCurrentHpInternal(1);
        for (var i = b.Bot.Potions.Count(); i < b.Bot.MaxPotionCount; i++)
            b.Bot.AddPotionInternal(ModelDb.Potion<EnergyPotion>().ToMutable(), silent: true);
        Check(KernelPotionOpportunity.HasUnmodeledFullBelt([b.Bot]),
            "fixture must enter the real full-belt opportunity route after a compared card ending");
        opportunityCalls = 0;
        for (var generation = 0; generation < repeats; generation++)
        {
            if (generation > 0)
            {
                b.Planner.Reset(newCombat: true);
                b.Bot.Creature.SetCurrentHpInternal(2); // A genuinely newer settled root.
                Check(Get(b.Planner, "tournamentCompletedResult") is null
                    && !(bool)Get(b.Planner, "tournamentCompletedResultPrepared")!
                    && !(bool)Get(b.Planner, "tournamentCompletedOpportunityReviewed")!,
                    "a retired result must release every single-consumption marker before a new root");
            }
            Set(b.Planner, "tournamentScript", null);
            var root = KernelCombatCapture.Capture(b.Combat);
            var replay = new KernelScriptReplayRun(root, b.Combat.Players.ToArray(),
                new TournamentResult(b.Script[0], null, [], 0, 0, "native-result-fixture") { Script = b.Script }, 80);
            while (!replay.Done) replay.Advance(TimeSpan.FromMilliseconds(1));
            Check(replay.Result?.FirstAction?.Card is not null && replay.Result.Record is { Victory: false },
                "fixture must provide a completed native non-winning paid-card proposal");
            var request = new KernelDecisionRequest(99400 + generation, 0);
            var result = replay.Result! with { RootGeneration = request.RootGeneration };
            AccessTools.Property(typeof(TournamentResult), "RequestProvenance").SetValue(result, request);
            var options = new TournamentOptions(IncludePotions: false, IncludeEndTurns: false) { Request = request };
            using var completed = new TournamentRun(root, b.Combat.Players.ToArray(), [b.Bot], options, 1);
            AccessTools.Property(typeof(TournamentRun), nameof(TournamentRun.Result)).SetValue(completed, result);
            Set(b.Planner, "tournamentRun", completed); Set(b.Planner, "tournamentRequest", request);
            Set(b.Planner, "tournamentRoot", root); Set(b.Planner, "tournamentParty", b.Combat.Players.ToArray());
            Set(b.Planner, "tournamentActors", new[] { b.Bot }); Set(b.Planner, "tournamentOptions", options);
            var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
            phase.SetValue(b.Planner, Enum.Parse(phase.FieldType, "NoPotions"));
            Set(b.Planner, "tournamentOpeningReviewed", true);
            var point = "completed-potion-result-" + generation;
            Set(b.Planner, "tournamentPoint", point);
            entered.Reset(); release.Reset(); holdEmergency = true;
            var waiting = false; string outcome = ""; TeamCombatPlanner.Decision? decision = null;
            var timer = Stopwatch.StartNew();
            try
            {
                while (timer.ElapsedMilliseconds < 5000)
                {
                    object?[] args = [b.Combat, new[] { b.Bot }, false, point, null];
                    outcome = AccessTools.Method(typeof(KernelCombatPlanner), "TryTournamentDecision")
                        .Invoke(b.Planner, args)!.ToString()!;
                    decision = args[4] as TeamCombatPlanner.Decision;
                    if (entered.IsSet && !waiting)
                    {
                        waiting = true;
                        Check((bool)Get(b.Planner, "emergencyPotionPending")!,
                            "the actual emergency worker must remain pending while its no-rescue result is held");
                        holdEmergency = false; release.Set();
                    }
                    if (opportunityCalls > generation + 1 || outcome == "Decided") break;
                    Thread.Sleep(1);
                }
                Check(waiting && opportunityCalls == generation + 1 && outcome == "Decided"
                    && ReferenceEquals(decision?.Move.Card, result.FirstAction!.Card)
                    && Get(b.Planner, "tournamentRun") is null && Get(b.Planner, "tournamentRequest") is null,
                    $"a completed opportunity followed by an asynchronous no-rescue result must deploy the native card once without restarting the potion comparison; generation={generation} waiting={waiting} calls={opportunityCalls} outcome={outcome} pending={Get(b.Planner, "emergencyPotionPending")} card={decision?.Move.Card?.Id.Entry}");
                Check(b.Bot.PlayerCombatState!.Energy == root.Energy(b.Bot),
                    "result scheduling cannot spend the real card before executor submission");
            }
            finally { holdEmergency = false; release.Set(); }
        }
    }
    private static void FinalBenefitWorker()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        Set(b.Planner, "tournamentScript", null);
        var bottle = ModelDb.Potion<FirePotion>().ToMutable(); b.Bot.AddPotionInternal(bottle, silent: true);
        var root = KernelCombatCapture.Capture(b.Combat);
        var actions = new[] { new KernelTeamSearch.Action(b.Bot, null, b.Script[0].Target, bottle), b.Script[0] };
        var replay = new KernelScriptReplayRun(root, b.Combat.Players.ToArray(),
            new TournamentResult(actions[0], null, [], 0, 0, "native-potion-result-fixture") { Script = actions }, 80);
        while (!replay.Done) replay.Advance(TimeSpan.FromMilliseconds(1));
        Check(replay.Result?.FirstAction?.Potion == bottle && replay.Result.Script?.Count == 2,
            "final benefit fixture must really replay the bottle and a paid native card");
        var request = new KernelDecisionRequest(99410, 5000);
        var result = replay.Result! with { RootGeneration = request.RootGeneration };
        AccessTools.Property(typeof(TournamentResult), "RequestProvenance").SetValue(result, request);
        // This is an existing cached WIPE forecast supplied to the scheduling
        // branch, as in PostDecisionPotionScenarios. It is not a new search
        // proof. The candidate bottle/card computation above remains native.
        var baseline = new TournamentResult(b.Script[0],
            new(false, EvidenceKind.VerifiedTerminal, "", [0, 0], 2, 0), [], 0, 0, "cached-loss-forecast") { Script = b.Script };
        var options = new TournamentOptions { Request = request, MixedIncrementalOpenings = true };
        using var completed = new TournamentRun(root, b.Combat.Players.ToArray(), [b.Bot], options, 5000);
        AccessTools.Property(typeof(TournamentRun), nameof(TournamentRun.Result)).SetValue(completed, result);
        Set(b.Planner, "tournamentRun", completed); Set(b.Planner, "tournamentRequest", request);
        Set(b.Planner, "tournamentRoot", root); Set(b.Planner, "tournamentParty", b.Combat.Players.ToArray());
        Set(b.Planner, "tournamentActors", new[] { b.Bot }); Set(b.Planner, "tournamentOptions", options);
        Set(b.Planner, "tournamentFirstPass", baseline);
        var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
        phase.SetValue(b.Planner, Enum.Parse(phase.FieldType, "Potions"));
        Set(b.Planner, "tournamentOpeningReviewed", true);
        const string point = "native-final-benefit"; Set(b.Planner, "tournamentPoint", point);
        entered.Reset(); release.Reset(); benefitCaller = Environment.CurrentManagedThreadId;
        benefitThread = benefitCalls = 0; holdBenefit = true;
        string Poll()
        {
            object?[] args = [b.Combat, new[] { b.Bot }, false, point, null];
            return AccessTools.Method(typeof(KernelCombatPlanner), "TryTournamentDecision").Invoke(b.Planner, args)!.ToString()!;
        }
        try
        {
            var timer = Stopwatch.StartNew();
            while (!entered.IsSet && timer.ElapsedMilliseconds < 4000) { Poll(); Thread.Sleep(1); }
            Check(entered.IsSet && benefitThread != benefitCaller && benefitCalls == 1,
                "the production final potion comparison must evaluate native ImmediateBenefit on its background owner");
            var maximum = 0d;
            for (var i = 0; i < 100; i++)
            {
                var watch = Stopwatch.StartNew();
                Check(Poll() == "Pending" && b.Planner.IsSearching,
                    "an unfinished final benefit remains searchable without reopening the completed tournament");
                maximum = Math.Max(maximum, watch.Elapsed.TotalMilliseconds);
            }
            Check(maximum < 50 && benefitCalls == 1,
                "game-thread polls must not execute, wait for or duplicate the held native potion benefit");
            holdBenefit = false; release.Set(); timer.Restart();
            var outcome = "Pending";
            while (outcome == "Pending" && timer.ElapsedMilliseconds < 5000) { outcome = Poll(); Thread.Sleep(1); }
            Check(outcome == "Decided" && Get(b.Planner, "mixedPotionBenefitWork") is null
                && b.Bot.Potions.Contains(bottle) && b.Bot.PlayerCombatState!.Energy == root.Energy(b.Bot),
                "the final benefit must publish once, retire its handle and leave native execution to the caller");
            Console.WriteLine($"POTION-BENEFIT-PERF: pollMaxMs={maximum:F3} caller={benefitCaller} worker={benefitThread}");
        }
        finally { holdBenefit = false; release.Set(); }
    }

    private static void BenefitDeadline()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        var bottle = ModelDb.Potion<FirePotion>().ToMutable(); b.Bot.AddPotionInternal(bottle, silent: true);
        var root = KernelCombatCapture.Capture(b.Combat);
        var action = new KernelTeamSearch.Action(b.Bot, null, b.Script[0].Target, bottle);
        stoppedRoot = root; stoppedRootForks = 0;
        try
        {
            var expired = new KernelDecisionRequest(99411, 0);
            var cancelled = new KernelDecisionRequest(99412, 5000); cancelled.Cancel("new native root");
            foreach (var request in new[] { expired, cancelled })
                Check(KernelPotionPolicy.ImmediateBenefit(root, b.Combat.Players.ToArray(), action,
                    [action, b.Script[0]], request) == 0 && stoppedRootForks == 0,
                    "an expired or cancelled final benefit must publish zero without forking or starting another native action");
        }
        finally { stoppedRoot = null; }
    }

    private static object? Get(object obj, string field) => AccessTools.Field(obj.GetType(), field).GetValue(obj);
    private static void Set(object obj, string field, object? value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Mixed potion result consumption: " + message); }
}

// Actual 2026-10-25 production fault controls: build0/RED1 -> exact source restore -> build0/GREEN0.
// Raw commands and logs: work/stutter-potion-result-consumption/r2/receipts.json.
// single-opportunity: System.InvalidOperationException: Mixed potion result consumption: a completed opportunity followed by an asynchronous no-rescue result must deploy the native card once without restarting the potion comparison; generation=0 waiting=True calls=2 outcome=Decided pending=False card=STRIKE_IRONCLAD
// reset-markers: System.InvalidOperationException: Mixed potion result consumption: a retired result must release every single-consumption marker before a new root
// background-benefit: System.InvalidOperationException: Mixed potion result consumption: the production final potion comparison must evaluate native ImmediateBenefit on its background owner
// benefit-deadline: System.InvalidOperationException: Mixed potion result consumption: an expired or cancelled final benefit must publish zero without forking or starting another native action
