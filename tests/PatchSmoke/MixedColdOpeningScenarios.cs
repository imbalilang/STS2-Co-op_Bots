// V2 R2 actual 2026-10-24: production build0 -> named runtime RED1 -> exact source restore -> build0/GREEN0.
// Private receipts: work/v2-validation/r2/{receipts,second-pass/receipts,final-pass/receipts}.json.
// These are fault controls, not distinct gameplay bug or live-performance counts.
// cold-constructor-deferred: System.InvalidOperationException: Mixed cold opening: mixed construction must leave all opening enumeration for the resumable native cursor
// cold-unit-yield: System.InvalidOperationException: Mixed cold opening: a zero-sized opening slice must finish exactly one target/card unit and yield
// cold-settled-prefix: System.InvalidOperationException: Mixed cold opening: a mixed deadline must retain the same-root settled native prefix as estimated deployment, never invent terminal evidence or another action
// cold-no-first-kill-stop: System.InvalidOperationException: Mixed cold opening: a cheap Kill result cannot stop the mixed tournament before actual Defend/Growth, joint work and opening completion
// cold-allbot-default: System.InvalidOperationException: Mixed cold opening: the default all-Bot path must retain eager opening order and its original optimizer scheduling
// cold-cancel-withdraw: System.InvalidOperationException: Mixed cold opening: a cancelled mixed root cannot deploy even a previously settled prefix
using System.Diagnostics;
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

// Native root/rollout transactions are unchanged. Only the request Stopwatch
// is advanced deterministically after an atomic action has actually settled.
// Mutation evidence above was recorded after the actual native-focused runs.
internal static class MixedColdOpeningScenarios
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.mixed-cold-opening");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedColdOpeningScenarios), nameof(Format)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("constructor", ConstructorDefersEnumeration), ("units", OneOpeningUnit),
                ("prefix", DeadlineRetainsSettledPrefix), ("cancel", CancellationWithdrawsPrefix),
                ("portfolio", CompletePortfolioStillRuns), ("allbot", DefaultPathIsUnchanged) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: mixed cold opening " + test.Name); }
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }
    private sealed class Board : IDisposable
    {
        internal readonly CombatState Combat;
        internal readonly Player Owner;
        internal readonly KernelSession Root;
        private readonly IDisposable native;
        internal Board(int hp = 100)
        {
            var board = KernelPlanningStateScenarios.Board(); Combat = board.Combat; Owner = board.Owner;
            board.Enemy.SetCurrentHpInternal(hp);
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
                Instance, null, [Combat], null)!;
            Root = KernelCombatCapture.Capture(Combat);
        }
        public void Dispose() => native.Dispose();
    }
    private static TournamentOptions Options(KernelDecisionRequest? request = null) => new(
        TopK: 20, Rollout: new RolloutOptions(MaxRounds: 80, MaxSteps: 600), ReserveMs: 0,
        IncludePotions: false, IncludeEndTurns: false, UsePolicyPortfolio: true, EnableJointSearch: true)
        { Request = request, MixedIncrementalOpenings = true };
    private static void ConstructorDefersEnumeration()
    {
        using var b = new Board();
        using var run = new TournamentRun(b.Root, [b.Owner], [b.Owner], Options(), 6400);
        Check(!run.Done && Get(run, "openingRun") is not null
            && ((IReadOnlyCollection<KernelTeamSearch.Action>)Get(run, "candidates")!).Count == 0,
            "mixed construction must leave all opening enumeration for the resumable native cursor");
        run.Advance(TimeSpan.Zero);
        Check(!run.Done && ((RolloutRun?)Get(run, "referenceRun"))?.CommittedSteps > 0
            && ((KernelTournament.OpeningRun)Get(run, "openingRun")!).Units == 0,
            "the first mixed slice must fund a settled real policy action before cold candidate enumeration");
    }
    private static void OneOpeningUnit()
    {
        using var b = new Board(); var options = Options();
        var eager = KernelTournament.FirstActions(b.Root, [b.Owner], options.TopK, options);
        var cursor = new KernelTournament.OpeningRun(b.Root, [b.Owner], options, () => false);
        Check(!cursor.Advance(TimeSpan.Zero) && cursor.Units == 1,
            "a zero-sized opening slice must finish exactly one target/card unit and yield");
        var calls = 1;
        while (!cursor.Advance(TimeSpan.Zero) && ++calls < 100) { }
        Check(cursor.Done && calls < 100 && eager.SequenceEqual(cursor.Result),
            "resuming opening units must preserve the exact default role/seat/target TopK order without duplicate work");
    }
    private static void DeadlineRetainsSettledPrefix()
    {
        using var b = new Board(); var before = b.Root.PartyStateText();
        var request = new KernelDecisionRequest(95101, 30000);
        using var run = new TournamentRun(b.Root, [b.Owner], [b.Owner], Options(request), 30000);
        run.Advance(TimeSpan.Zero);
        var reference = (RolloutRun)Get(run, "referenceRun")!;
        var committed = reference.CommittedSteps;
        Check(committed > 0 && !reference.State.HasTerminal && reference.State.Energy(b.Owner) == 2,
            "the deadline fixture must first settle an actual paid nonterminal native card");
        Expire(request); run.Advance(TimeSpan.Zero);
        Check(run.Done && run.Result is { FirstAction: { Card: not null }, Record: { Verified: false }, Script: { Count: > 0 } }
            && run.Result.IsFor(request) && run.Result.Script.Count == committed
            && run.Result.ScriptCheckpoints?.Count == committed && run.BestCompleted is null
            && run.Result.StopReason == "request-deadline" && b.Root.PartyStateText() == before,
            "a mixed deadline must retain the same-root settled native prefix as estimated deployment, never invent terminal evidence or another action");
    }
    private static void CancellationWithdrawsPrefix()
    {
        using var b = new Board(); var request = new KernelDecisionRequest(95102, 30000);
        using var run = new TournamentRun(b.Root, [b.Owner], [b.Owner], Options(request), 30000);
        run.Advance(TimeSpan.Zero); request.Cancel("new-human-root"); run.Advance(TimeSpan.Zero);
        Check(run.Done && run.Result?.FirstAction is null && run.Result.Record is null && run.Result.Script is null,
            "a cancelled mixed root cannot deploy even a previously settled prefix");
    }
    private static void CompletePortfolioStillRuns()
    {
        using var b = new Board(12);
        using var run = new TournamentRun(b.Root, [b.Owner], [b.Owner], Options(), 6400);
        var watch = Stopwatch.StartNew();
        do { run.Advance(TimeSpan.Zero); } while (!run.Done && watch.ElapsedMilliseconds < 15000);
        var policies = (IReadOnlyCollection<string>)Get(run, "policyReferenceStops")!;
        Check(run.Done && policies.Any(s => s.StartsWith("Defend:")) && policies.Any(s => s.StartsWith("Growth:"))
            && Get(run, "joint") is not null && ((KernelTournament.OpeningRun)Get(run, "openingRun")!).Done,
            "a cheap Kill result cannot stop the mixed tournament before actual Defend/Growth, joint work and opening completion");
    }
    private static void DefaultPathIsUnchanged()
    {
        using var b = new Board();
        using var run = new TournamentRun(b.Root, [b.Owner], [b.Owner], Options() with { MixedIncrementalOpenings = false }, 6400);
        Check(Get(run, "openingRun") is null
            && ((IReadOnlyCollection<KernelTeamSearch.Action>)Get(run, "candidates")!).Count > 0,
            "the default all-Bot path must retain eager opening order and its original optimizer scheduling");
    }
    private static object? Get(object target, string name) => AccessTools.Field(target.GetType(), name).GetValue(target);
    private static void Expire(KernelDecisionRequest request)
    {
        var clock = (Stopwatch)AccessTools.Field(typeof(KernelDecisionRequest), "clock").GetValue(request)!;
        clock.Stop(); AccessTools.Field(typeof(Stopwatch), "_elapsed").SetValue(clock,
            (request.BudgetMs + 1L) * Stopwatch.Frequency / 1000);
        Check(request.DeadlineReached, "the controlled request clock must actually cross its absolute deadline");
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Mixed cold opening: " + message); }
}
