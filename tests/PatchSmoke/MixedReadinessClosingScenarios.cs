using System.Diagnostics;
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

// Readiness notifications are injected at their settled callback boundary;
// no native card, potion or side transition is replaced. Separate rollout
// cases below execute native captured end-turn transitions and assert the
// distinction between readiness-only and an unowned advancing side.
// R2 runtime RED text is appended by the serialized integration runner.
internal static class MixedReadinessClosingScenarios
{
    private sealed class MaterialInput(Player owner) : EndPlayerTurnAction(owner, 1)
    { protected override Task ExecuteAction() => Task.CompletedTask; }

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.mixed-readiness.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedReadinessClosingScenarios), nameof(Format)));
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null;
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("ready-keeps-search", ReadyKeepsSearch), ("material-version", MaterialVersion),
                ("passive-closing-prefix", PassiveClosingPrefix), ("closing-only-prefix", ClosingOnlyPrefix),
                ("unowned-transition", UnownedTransition), ("closing-gate", ClosingGate),
                ("compute-options", ComputeOptions), ("turn-domain", TurnDomain) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: mixed readiness closing " + test.Name); }
        }
        finally { KernelCombatPlanner.TournamentOverride = previous; presentation.UnpatchAll(presentation.Id); }
    }

    private static bool Format(ref string __result) { __result = "fixture"; return false; }

    private static void NotifySettled(MixedBackgroundReplanScenarios.Board b, GameAction action)
    {
        // A scheduler contract test: deliver the same completion callback
        // the native action queue invokes, without calling presentation UI.
        b.Queue.EnqueueWithoutSynchronizing(action);
        var pending = (Dictionary<GameAction, Action<GameAction>>)AccessTools.Field(typeof(KernelInputObserver), "pending").GetValue(null)!;
        Check(pending.TryGetValue(action, out var callback), "a real native owned action must register its settled receipt");
        callback!(action);
        callback(action); // duplicated receipt must remain harmless
    }

    private static void ReadyKeepsSearch()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        Set(b.Planner, "tournamentScript", null);
        Check((bool)Invoke(b.Planner, "BeginTournamentPhase", b.Combat, new[] { b.Bot }, false)!, "full foreground search must start");
        var request = (KernelDecisionRequest)Get(b.Planner, "tournamentRequest")!;
        var run = Get(b.Planner, "tournamentRun");
        var before = Invoke(b.Planner, "TournamentDecisionPoint", b.Combat, 10u);
        NotifySettled(b, new EndPlayerTurnAction(b.Human, b.Human.PlayerCombatState!.TurnNumber));
        var after = Invoke(b.Planner, "TournamentDecisionPoint", b.Combat, 11u);
        Check(KernelInputObserver.Revision == 0 && KernelInputObserver.MaterialVersion == 0
            && !request.IsCancelled && ReferenceEquals(Get(b.Planner, "tournamentRun"), run)
            && !(bool)Get(b.Planner, "mixedReplanDirty")! && Equals(before, after),
            "native human readiness must retain the same running full search and material decision point");
    }

    private static void MaterialVersion()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        NotifySettled(b, new MaterialInput(b.Bot));
        Check(KernelInputObserver.MaterialVersion == 1 && KernelInputObserver.Revision == 0
            && !(bool)Get(b.Planner, "mixedReplanDirty")!,
            "a settled controlled material action must advance the root exactly once without becoming human input");
        Set(b.Planner, "tournamentScript", null);
        Check((bool)Invoke(b.Planner, "BeginTournamentPhase", b.Combat, new[] { b.Bot }, false)!, "full search must start before material input");
        var request = (KernelDecisionRequest)Get(b.Planner, "tournamentRequest")!;
        var before = Invoke(b.Planner, "TournamentDecisionPoint", b.Combat, 11u);
        NotifySettled(b, new MaterialInput(b.Human));
        Check(KernelInputObserver.MaterialVersion == 2 && KernelInputObserver.Revision == 1
            && request.IsCancelled && (bool)Get(b.Planner, "mixedReplanDirty")!
            && !Equals(before, Invoke(b.Planner, "TournamentDecisionPoint", b.Combat, 11u)),
            "unknown derived human actions must still invalidate material roots and cancel obsolete initial search");
    }

    private static RolloutRun NativeRun(MixedBackgroundReplanScenarios.Board b, bool humanFirst)
    {
        // The generic planning fixture leaves this native test monster at
        // UNSET_MOVE; a complete native round needs its real looping attack.
        var monster = b.Combat.Enemies.Single().Monster!;
        monster.SetMoveImmediate((MoveState)monster.MoveStateMachine!.States["ATTACK"], true);
        return new(KernelCombatCapture.Capture(b.Combat).Fork(), humanFirst ? [b.Human, b.Bot] : [b.Bot, b.Human], [b.Bot],
            new RolloutOptions(MaxSteps: 40, MaxRounds: 2) { CaptureActionCheckpoints = true,
                InvestmentLookahead = false, SequenceLookahead = false });
    }

    private static void PassiveClosingPrefix()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        var run = NativeRun(b, humanFirst: true);
        var root = KernelCombatCapture.Capture(b.Combat);
        for (var i = 0; i < 30 && !run.Done && !run.SettledPrefix().Actions.Any(a => a.EndTurn); i++) run.Step();
        var prefix = run.SettledPrefix();
        Console.WriteLine($"READINESS-PREFIX: steps={run.CommittedSteps} actions={run.RecordedActionCount} accounted={run.HasAccountedProposalSteps} "
            + $"lastEnd={prefix.Actions.LastOrDefault()?.EndTurn} checkpoints={prefix.Checkpoints?.Count} startsAtRoot={run.SettledPrefixStartsAt(root.ActionCheckpoint())} "
            + $"verified={prefix.Record.Verified} victory={prefix.Record.Victory} done={run.Done} stop={run.Result?.StopReason}");
        if (run.State.LastRoundFailure is { } failure) Console.WriteLine(failure);
        Check(run.CommittedSteps == run.RecordedActionCount + 1 && run.HasAccountedProposalSteps
            && prefix.Actions.Any(a => a.Card is not null) && prefix.Actions.Last().EndTurn
            && prefix.Checkpoints?.Count == prefix.Actions.Count && run.SettledPrefixStartsAt(root.ActionCheckpoint())
            && !prefix.Record.Verified && !prefix.Record.Victory,
            "a native passive human ready gap must retain the paid card prefix through the Bot closing action without a victory claim");
        using var tournament = new TournamentRun(root, [b.Human, b.Bot], [b.Bot],
            new TournamentOptions(IncludeEndTurns: true, ReserveMs: 0) { MixedIncrementalOpenings = true }, 2000);
        Invoke(tournament, "RememberSettledProposal", run);
        Check(tournament.LatestSettledProposal?.Script?.Last().EndTurn == true,
            "the production tournament must publish the accounted native closing suffix after passive readiness");
    }

    private static void EmptyBotHand(MixedBackgroundReplanScenarios.Board b)
    {
        var state = b.Bot.PlayerCombatState!;
        foreach (var card in state.Hand.Cards.ToArray())
        { state.Hand.RemoveInternal(card); state.DiscardPile.AddInternal(card); }
    }

    private static void ClosingOnlyPrefix()
    {
        using var b = new MixedBackgroundReplanScenarios.Board(); EmptyBotHand(b);
        var run = NativeRun(b, humanFirst: true);
        var root = KernelCombatCapture.Capture(b.Combat);
        run.Step(); run.Step();
        Check(run.RecordedActionCount == 1 && run.CommittedSteps == 2 && run.SettledPrefix().Actions[0].EndTurn
            && run.HasAccountedProposalSteps && run.SettledPrefixStartsAt(root.ActionCheckpoint()),
            "a closing-only proposal must prove its first boundary differs solely by native passive readiness");
        using var tournament = new TournamentRun(root, [b.Human, b.Bot], [b.Bot],
            new TournamentOptions(IncludeEndTurns: true) { MixedIncrementalOpenings = true }, 1000);
        Invoke(tournament, "RememberSettledProposal", run);
        Check(tournament.LatestSettledProposal?.FirstAction?.EndTurn == true,
            "a native EndTurn-first answer must survive before the human submits readiness");
    }

    private static void UnownedTransition()
    {
        using var b = new MixedBackgroundReplanScenarios.Board(); EmptyBotHand(b);
        var run = NativeRun(b, humanFirst: false);
        run.Step(); run.Step();
        Check(!run.HasAccountedProposalSteps && run.SettledPrefix().Checkpoints is null,
            "an unowned side transition must never be relabeled as an accounted readiness-only gap");
    }

    private static void ClosingGate()
    {
        using var b = new MixedBackgroundReplanScenarios.Board(); EmptyBotHand(b);
        var root = KernelCombatCapture.Capture(b.Combat); var run = NativeRun(b, humanFirst: true);
        run.Step(); run.Step(); var prefix = run.SettledPrefix();
        var proposal = new TournamentResult(prefix.Actions[0], prefix.Record, [], 0, 1, "native-closing-proposal") {
            Script = prefix.Actions, ScriptAliveStates = prefix.AliveStates,
            ScriptEnemyStates = prefix.EnemyStates, ScriptCheckpoints = prefix.Checkpoints };
        Invoke(b.Planner, "AdoptTournamentScript", proposal, b.Combat);
        var generation = Get(b.Planner, "tournamentRootGeneration");
        var waiting = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out _);
        Check(waiting == KernelCombatPlanner.Status.Pending && b.Planner.ConfirmedEndTurn is null
            && Get(b.Planner, "tournamentRequest") is null,
            "a stored closing proposal must wait for human ready without submitting EndTurn or starting another search");
        var ready = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, true, BotDifficulty.Pro, out _);
        Check(ready == KernelCombatPlanner.Status.Ready && ReferenceEquals(b.Planner.ConfirmedEndTurn, b.Bot)
            && Equals(generation, Get(b.Planner, "tournamentRootGeneration")) && Get(b.Planner, "tournamentRequest") is null,
            "human completion must release the existing closing proposal without a replacement full tournament");
    }

    private static void ComputeOptions()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        var before = new KernelCombatPlanner(); var after = new KernelCombatPlanner();
        try
        {
            Invoke(before, "BeginTournamentPhase", b.Combat, new[] { b.Bot }, false);
            Invoke(after, "BeginTournamentPhase", b.Combat, new[] { b.Bot }, true);
            var a = (TournamentOptions)Get(before, "tournamentOptions")!;
            var z = (TournamentOptions)Get(after, "tournamentOptions")!;
            b.Input(); b.Planner.AdvanceMixedReplan(b.Combat, 1000);
            var optional = (TournamentOptions)Get(Get(b.Planner, "mixedReplan")!, "Options")!;
            Check(a.IncludeEndTurns && z.IncludeEndTurns && optional.IncludeEndTurns
                && a.UsePolicyPortfolio && z.UsePolicyPortfolio && optional.UsePolicyPortfolio
                && a.EnableJointSearch && z.EnableJointSearch && optional.EnableJointSearch,
                "foreground and human-driven background full tournaments must compute closing actions before and after human readiness");
        }
        finally { before.Reset(newCombat: true); after.Reset(newCombat: true); }
    }

    private static void TurnDomain()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        // A production scheduling-key contract, not another extra-turn
        // simulation oracle: mutate the actual native seat/side metadata.
        // Existing native extra-turn suites prove that owner turn numbers
        // can advance while RoundNumber stays unchanged.
        var round = b.Combat.RoundNumber;
        var material = KernelInputObserver.MaterialVersion;
        var initial = KernelCombatPlanner.TournamentDecisionPoint(b.Combat, 10);
        b.Human.PlayerCombatState!.Phase = PlayerTurnPhase.End;
        Check(initial == KernelCombatPlanner.TournamentDecisionPoint(b.Combat, 11),
            "readiness phase changes must not masquerade as a new material turn domain");
        b.Bot.PlayerCombatState!.IncrementTurnNumber();
        var extra = KernelCombatPlanner.TournamentDecisionPoint(b.Combat, 11);
        Check(b.Combat.RoundNumber == round && KernelInputObserver.MaterialVersion == material && initial != extra,
            "an actual native owner turn-number change must retire a mixed root even in the same round without another material action");
        b.Combat.CurrentSide = CombatSide.Enemy;
        Check(extra != KernelCombatPlanner.TournamentDecisionPoint(b.Combat, 11),
            "an actual native side change must distinguish otherwise identical mixed roots");
        AutoPilot.Set(b.Human.NetId, true);
        var allBot = KernelCombatPlanner.TournamentDecisionPoint(b.Combat, 11);
        b.Bot.PlayerCombatState.IncrementTurnNumber();
        b.Combat.CurrentSide = CombatSide.Player;
        Check(allBot == KernelCombatPlanner.TournamentDecisionPoint(b.Combat, 11)
            && allBot != KernelCombatPlanner.TournamentDecisionPoint(b.Combat, 12),
            "all-Bot root scheduling must retain its existing native action-version contract");
    }

    private static object? Get(object target, string name) => AccessTools.Field(target.GetType(), name).GetValue(target);
    private static void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] arguments)
        => AccessTools.Method(target.GetType(), name).Invoke(target, arguments);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Mixed readiness closing: " + message); }
}

// R2 actual 2026-10-25: readiness-no-cancel build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: native human readiness must retain the same running full search and material decision point

// R2 actual 2026-10-25: controlled-material-version build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: a settled controlled material action must advance the root exactly once without becoming human input

// R2 actual 2026-10-25: passive-closing-accounting build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: the production tournament must publish the accounted native closing suffix after passive readiness

// R2 actual 2026-10-25: closing-first-boundary build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: a closing-only proposal must prove its first boundary differs solely by native passive readiness

// R2 actual 2026-10-25: unowned-side-boundary build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: an unowned side transition must never be relabeled as an accounted readiness-only gap

// R2 actual 2026-10-25: closing-submission-gate build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: a stored closing proposal must wait for human ready without submitting EndTurn or starting another search

// R2 actual 2026-10-25: foreground-closing-compute build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: foreground and human-driven background full tournaments must compute closing actions before and after human readiness

// R2 actual 2026-10-25: optional-closing-compute build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: foreground and human-driven background full tournaments must compute closing actions before and after human readiness

// R2 actual 2026-10-25: same-round-turn-domain build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/readiness-r2/receipts.json
// System.InvalidOperationException: Mixed readiness closing: an actual native owner turn-number change must retire a mixed root even in the same round without another material action
