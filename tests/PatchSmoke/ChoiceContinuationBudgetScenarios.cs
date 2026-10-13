using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

// The shared request controls optional comparisons, never cancellation of a
// partially settled native card/choice. Actual private physical R2 (forced
// Rebuild0/named runtime RED1/exact source and DLL restore/GREEN0):
// work/.../choice-budget/execution-20261009T141856410090Z/evidence/proof.json
// contains nine valid scope/pool/deadline/branch/settlement faults, reused here.
// The two new entry faults are in execution-20261009T151009504264Z/evidence/proof.json.
// Exhaust fallback and unchanged Discard policy have two actual physical R2
// faults in execution-20261009T161137806015Z/evidence/proof.json (old eleven reused).
internal static class ChoiceContinuationBudgetScenarios
{
    private static readonly MethodInfo mirroredPlay = AccessTools.Method(typeof(CombatPredictionSimulator), "OnPlayWrapper");
    [ThreadStatic] private static KernelDecisionRequest? cancelAtPlay;
    [ThreadStatic] private static int observedPlays;
    [ThreadStatic] private static bool observeEntryForks;
    [ThreadStatic] private static int entryForks;

    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var hooks = new Harmony("coopbots.choice-continuation-budget");
        hooks.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(ChoiceContinuationBudgetScenarios), nameof(Format)));
        hooks.Patch(mirroredPlay, prefix: new HarmonyMethod(typeof(ChoiceContinuationBudgetScenarios), nameof(ObservePlay)));
        hooks.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.Fork)),
            prefix: new HarmonyMethod(typeof(ChoiceContinuationBudgetScenarios), nameof(ObserveFork)));
        hooks.Patch(AccessTools.Method(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerName)),
            prefix: new HarmonyMethod(typeof(ChoiceContinuationBudgetScenarios), nameof(PlayerName)));
        hooks.Patch(AccessTools.Method(typeof(PeerVersionInfo), nameof(PeerVersionInfo.LocalDefault)),
            prefix: new HarmonyMethod(typeof(ChoiceContinuationBudgetScenarios), nameof(Version)));
        var net = AccessTools.Property(typeof(RunManager), nameof(RunManager.NetService));
        var previousNet = net.GetValue(RunManager.Instance);
        try
        {
            net.SetValue(RunManager.Instance, new NetReplayGameService(1));
            if (caseName is null or "scope") ScopeIsolation();
            if (caseName is null or "pool") WorkerRequestRotation();
            if (caseName is null or "expired") ExpiredRequestFallback();
            if (caseName is null or "atomic") CancellationInsideDiscard();
            if (caseName is null or "completed") CompletedPolicyAtStop();
            if (caseName is null or "terminal-entry") CompletedTerminalAtEntry();
            if (caseName is null or "stale-phase") StaleEnemyPhaseAtEntry();
            if (caseName is null or "exhaust-fallback") ExhaustFallbackAtDeadline();
            if (caseName is null or "discard-fallback") DiscardFallbackAtDeadline();
        }
        finally
        {
            cancelAtPlay = null; observedPlays = 0; observeEntryForks = false; entryForks = 0;
            net.SetValue(RunManager.Instance, previousNet);
            foreach (var method in hooks.GetPatchedMethods().ToArray())
                hooks.Unpatch(method, HarmonyPatchType.All, hooks.Id);
        }
    }

    private static void ScopeIsolation()
    {
        // Actual R2 nested-scope-restore and async-request-flow RED:
        // System.InvalidOperationException: Choice continuation budget: nested and async choice scopes must restore the exact outer request without replacing its clock
        var prior = KernelSearchBudgetScope.Current;
        var outer = new KernelDecisionRequest(81001, int.MaxValue);
        var inner = new KernelDecisionRequest(81002, 0);
        var restored = false;
        var child = false;
        using (KernelSearchBudgetScope.Enter(outer))
        {
            using (KernelSearchBudgetScope.Enter(inner))
                Check(ReferenceEquals(KernelSearchBudgetScope.Current, inner) && KernelSearchBudgetScope.IsStopped,
                    "nested scope must expose its own stopped request");
            restored = ReferenceEquals(KernelSearchBudgetScope.Current, outer) && !KernelSearchBudgetScope.IsStopped;
            using (KernelSearchBudgetScope.Enter(null))
                restored &= ReferenceEquals(KernelSearchBudgetScope.Current, outer);
            child = Task.Run(() =>
            {
                var inherited = ReferenceEquals(KernelSearchBudgetScope.Current, outer);
                using (KernelSearchBudgetScope.Enter(inner))
                    inherited &= ReferenceEquals(KernelSearchBudgetScope.Current, inner);
                return inherited && ReferenceEquals(KernelSearchBudgetScope.Current, outer);
            }).GetAwaiter().GetResult();
            restored &= ReferenceEquals(KernelSearchBudgetScope.Current, outer);
        }
        Check(restored && child && ReferenceEquals(KernelSearchBudgetScope.Current, prior),
            "nested and async choice scopes must restore the exact outer request without replacing its clock");

        // Two simultaneous logical contexts must neither replace nor stop one
        // another. Exceptions are observed before disposing their barrier.
        using var barrier = new Barrier(2);
        var a = Task.Run(() => ParallelScope(outer, barrier));
        var b = Task.Run(() => ParallelScope(inner, barrier));
        Task.WaitAll(a, b);
        Check(a.Result && b.Result && ReferenceEquals(KernelSearchBudgetScope.Current, prior),
            "concurrent choice scopes must retain their own request and leave the caller unchanged");
        Console.WriteLine("PASS: choice continuation scopes restore nesting, inherit native async tails, and isolate concurrent requests.");
    }

    private static bool ParallelScope(KernelDecisionRequest request, Barrier barrier)
    {
        using var scope = KernelSearchBudgetScope.Enter(request);
        if (!barrier.SignalAndWait(5000)) throw new InvalidOperationException("choice budget barrier did not rendezvous");
        return ReferenceEquals(KernelSearchBudgetScope.Current, request)
            && KernelSearchBudgetScope.IsStopped == request.IsStopped;
    }

    private static void WorkerRequestRotation()
    {
        // Actual R2 worker-creator-context and worker-item-context RED:
        // System.InvalidOperationException: Choice continuation budget: the same real worker must rotate requests and legacy null without inheriting its stopped creator or previous job
        var prior = KernelSearchBudgetScope.Current;
        var creator = new KernelDecisionRequest(81010, 0);
        var first = new KernelDecisionRequest(81011, int.MaxValue);
        var second = new KernelDecisionRequest(81012, int.MaxValue);
        var pool = new KernelSearchWorkPool(1, 4);
        var contexts = new List<(KernelDecisionRequest? Request, bool Stopped, int Thread)>();
        using (KernelSearchBudgetScope.Enter(creator))
        {
            foreach (var request in new KernelDecisionRequest?[] { first, second, null })
            {
                Check(pool.TryQueue(request, () => contexts.Add((KernelSearchBudgetScope.Current,
                    KernelSearchBudgetScope.IsStopped, Environment.CurrentManagedThreadId)), out var work),
                    "worker rotation fixture must admit its real sequential work item");
                Check(SpinWait.SpinUntil(() => work!.Finished, 5000) && work!.Error is null,
                    "worker rotation fixture must finish its actual work item");
                if (ReferenceEquals(request, first)) first.Cancel("first-item-complete");
            }
            Check(contexts.Count == 3 && ReferenceEquals(contexts[0].Request, first)
                && ReferenceEquals(contexts[1].Request, second) && contexts[2].Request is null
                && contexts.All(c => !c.Stopped) && contexts.Select(c => c.Thread).Distinct().Count() == 1
                && pool.ActiveJobs == 0 && pool.QueuedJobs == 0
                && ReferenceEquals(KernelSearchBudgetScope.Current, creator),
                "the same real worker must rotate requests and legacy null without inheriting its stopped creator or previous job");
        }
        Check(ReferenceEquals(KernelSearchBudgetScope.Current, prior),
            "worker creation must not alter the caller's choice budget context");
        Console.WriteLine("PASS: one reused production worker binds each request and leaves legacy null free of old deadlines.");
    }

    private static void ExpiredRequestFallback()
    {
        // Actual R2 evaluate-request-deadline RED:
        // System.InvalidOperationException: Choice continuation budget: an expired choice request must start no optional continuation step and must not claim a completed hand
        // Actual R2 mandatory-first-choice RED:
        // System.InvalidOperationException: Choice continuation budget: an expired request must retain exactly the first fully settled legal native discard branch
        var (combat, owner, enemy, survivor) = Board("CHOICE-EXPIRED", 200, survivor: true);
        var root = KernelSession.Capture(combat);
        var frozen = root.PartyStateText();
        var request = new KernelDecisionRequest(81100, 0);
        KernelSession.CardBranch[] branches;
        KernelChoiceContinuation.Evaluation evaluated;
        using (KernelSearchBudgetScope.Enter(request))
        {
            evaluated = KernelChoiceContinuation.Evaluate(root, root.Fork(), owner, 3);
            branches = root.CardBranches(survivor!, null, 10).ToArray();
        }
        Check(evaluated.Steps == 0 && evaluated.CompleteHands == 0
            && double.IsNegativeInfinity(evaluated.Value) && evaluated.StopReason == "request-deadline",
            "an expired choice request must start no optional continuation step and must not claim a completed hand");
        Check(branches.Length == 1 && branches[0].Boundary.Length == 0 && branches[0].State.CanFork
            && branches[0].Choices.Count == 1 && branches[0].Choices[0].Effect == "Discard"
            && root.PartyStateText() == frozen,
            "an expired request must retain exactly the first fully settled legal native discard branch");
        CheckDiscardOracle(branches[0], owner, enemy, "CHOICE-EXPIRED", frozen, root);

        // The actual menu must remain legal even when its optional comparison
        // is already unavailable. No completed analysis branch is required.
        IReadOnlyList<CardModel> selected;
        using (KernelSearchBudgetScope.Enter(request))
            selected = root.SelectRelicCards(owner, root.Hand(owner), "Discard", 1, 1, "TOOLS_OF_THE_TRADE");
        Check(selected.Count == 1 && root.Hand(owner).Contains(selected[0])
            && root.LastHandChoiceComparison == "legacy-effect-or-nested-choice"
            && root.PartyStateText() == frozen,
            "an expired standalone native menu must use its existing legal fallback without optional hand comparison");
        Console.WriteLine("PASS: expired optional choice work stops, while native Survivor and standalone menus retain a paid legal fallback.");
    }

    private static void CancellationInsideDiscard()
    {
        // Actual R2 optional-choice-branches RED:
        // System.InvalidOperationException: Choice continuation budget: cancellation during native Survivor must finish payment block discard and tokens before stopping later branches
        var (combat, owner, enemy, survivor) = Board("CHOICE-ATOMIC", 200, survivor: true);
        var root = KernelSession.Capture(combat);
        var frozen = root.PartyStateText();
        var request = new KernelDecisionRequest(81101, int.MaxValue);
        observedPlays = 0; cancelAtPlay = request;
        KernelSession.CardBranch[] branches;
        try
        {
            using var scope = KernelSearchBudgetScope.Enter(request);
            branches = root.CardBranches(survivor!, null, 10).ToArray();
        }
        finally { cancelAtPlay = null; }
        Check(request.IsCancelled && observedPlays == 1 && branches.Length == 1
            && branches[0].Boundary.Length == 0 && branches[0].State.CanFork
            && branches[0].State.Energy(owner) == 2 && branches[0].State.Block(owner.Creature) == 8
            && branches[0].State.Hand(owner).Count == 1 && branches[0].Choices.Count == 1
            && root.PartyStateText() == frozen,
            "cancellation during native Survivor must finish payment block discard and tokens before stopping later branches");
        CheckDiscardOracle(branches[0], owner, enemy, "CHOICE-ATOMIC", frozen, root);
        Console.WriteLine("PASS: cancellation inside an actual Survivor transaction preserves its settled native menu result and starts no extra branch.");
    }

    private static void CompletedPolicyAtStop()
    {
        // Actual R2 policy-cancellation RED:
        // System.InvalidOperationException: Choice continuation budget: a stopped choice policy must finish its one native action and start no later policy or step
        // Actual R2 settled-completion-retention RED:
        // System.InvalidOperationException: Choice continuation budget: a genuinely completed choice policy must survive the stop while an unfinished policy remains unscored
        foreach (var enemyHp in new[] { 6, 200 })
        {
            var (combat, owner, enemy, _) = Board("CHOICE-COMPLETE", enemyHp, survivor: false);
            var root = KernelSession.Capture(combat);
            var frozen = root.PartyStateText();
            var request = new KernelDecisionRequest(81200 + enemyHp, int.MaxValue);
            observedPlays = 0; cancelAtPlay = request;
            KernelChoiceContinuation.Evaluation evaluated;
            try
            {
                using var scope = KernelSearchBudgetScope.Enter(request);
                evaluated = KernelChoiceContinuation.Evaluate(root, root.Fork(), owner, 1);
            }
            finally { cancelAtPlay = null; }
            Console.WriteLine($"DIAG choice-budget enemyHp={enemyHp} plays={observedPlays} steps={evaluated.Steps} complete={evaluated.CompleteHands} value={evaluated.Value} stop={evaluated.StopReason}");
            Check(request.IsCancelled && observedPlays == 1 && evaluated.Steps == 1
                && evaluated.StopReason == "request-cancelled:native-choice-hook" && root.PartyStateText() == frozen,
                "a stopped choice policy must finish its one native action and start no later policy or step");
            Check(enemyHp == 6
                    ? evaluated.CompleteHands == 1 && double.IsFinite(evaluated.Value)
                    : evaluated.CompleteHands == 0 && double.IsNegativeInfinity(evaluated.Value),
                "a genuinely completed choice policy must survive the stop while an unfinished policy remains unscored");
            // Independent original sts2 method, resource payment and hooks.
            var (nativeCombat, nativeOwner, nativeEnemy, _) = Board("CHOICE-COMPLETE", enemyHp, survivor: false);
            using (new NativeTurn(nativeCombat)) NativePlay(nativeOwner.PlayerCombatState!.Hand.Cards.Single(), nativeEnemy);
            Check(nativeOwner.PlayerCombatState!.Energy == 2 && nativeEnemy.CurrentHp == Math.Max(0, enemyHp - 6),
                "the stopped policy's terminal distinction must correspond to actual paid native Strike damage");
        }
        Console.WriteLine("PASS: completed native choice-policy victory remains comparable after cancellation and live cutoff never becomes completion.");
    }

    private static void CompletedTerminalAtEntry()
    {
        // Actual R2 completed-victory-entry (disable terminal-entry branch) RED:
        // System.InvalidOperationException: Choice continuation budget: a completed native victory at stopped entry must retain its original comparison value without fork or step
        var (combat, owner, enemy, _) = Board("CHOICE-TERMINAL-ENTRY", 6, survivor: false);
        var root = KernelSession.Capture(combat);
        var rootFrozen = root.PartyStateText();
        var selected = root.Fork();
        Check(selected.Play(selected.Hand(owner).Single(), enemy, out var boundary)
            && boundary.Length == 0 && selected.CanFork && selected.HasTerminal && selected.HasWon,
            "terminal entry fixture must first finish a real paid Strike victory");
        var frozen = selected.PartyStateText();
        // The existing unbudgeted continuation gives the original comparison
        // value; the stopped entry must retain it without an extra simulation.
        var originalLegacyValue = KernelChoiceContinuation.Evaluate(root, selected, owner, 1).Value;
        var request = new KernelDecisionRequest(81301, 0);
        KernelChoiceContinuation.Evaluation evaluated;
        entryForks = 0; observeEntryForks = true;
        try
        {
            using var scope = KernelSearchBudgetScope.Enter(request);
            evaluated = KernelChoiceContinuation.Evaluate(root, selected, owner, 1);
        }
        finally { observeEntryForks = false; }
        // This clear-enemy/no-debuff physical witness has zero valuation forks.
        // The budget contract forbids new rollout policy/Step, while existing
        // terminal value projections may still fork for other board states.
        Check(evaluated.Steps == 0 && evaluated.CompleteHands == 1 && entryForks == 0
            && evaluated.Value == originalLegacyValue && double.IsFinite(evaluated.Value)
            && evaluated.StopReason == "request-deadline"
            && root.PartyStateText() == rootFrozen && selected.PartyStateText() == frozen,
            "a completed native victory at stopped entry must retain its original comparison value without fork or step");
        var (nativeCombat, nativeOwner, nativeEnemy, _) = Board("CHOICE-TERMINAL-ENTRY", 6, survivor: false);
        using (new NativeTurn(nativeCombat)) NativePlay(nativeOwner.PlayerCombatState!.Hand.Cards.Single(), nativeEnemy);
        Check(nativeOwner.PlayerCombatState!.Energy == selected.Energy(owner)
            && nativeEnemy.CurrentHp == selected.Hp(enemy) && nativeEnemy.CurrentHp == 0
            && nativeOwner.Creature.CurrentHp == selected.Hp(owner.Creature),
            "completed victory entry must match independent original sts2 Strike payment damage and player HP");
        Console.WriteLine($"DIAG choice-budget terminal-entry originalLegacyValue={originalLegacyValue} retainedValue={evaluated.Value} steps={evaluated.Steps} complete={evaluated.CompleteHands} entryForks={entryForks}");
        Console.WriteLine("PASS: completed native victory entry retains the original value at deadline without fork or step.");
    }

    private static void StaleEnemyPhaseAtEntry()
    {
        // Actual R2 stale-enemy-phase-entry (accept old phase as terminal) RED:
        // System.InvalidOperationException: Choice continuation budget: an earlier settled enemy phase at stopped entry must not become a completed victory comparison
        var (combat, owner, enemy, _) = Board("CHOICE-STALE-PHASE", 200, survivor: false);
        var root = KernelSession.Capture(combat);
        var rootFrozen = root.PartyStateText();
        var selected = root.Fork();
        Check(selected.EndTurn(owner, out var boundary) && boundary.Length == 0
            && selected.CanFork && selected.EnemyPhaseCompleted && !selected.HasTerminal && selected.Hp(enemy) > 0,
            "stale phase fixture must finish a real nonterminal native enemy phase");
        var frozen = selected.PartyStateText();
        var request = new KernelDecisionRequest(81302, 0);
        KernelChoiceContinuation.Evaluation evaluated;
        entryForks = 0; observeEntryForks = true;
        try
        {
            using var scope = KernelSearchBudgetScope.Enter(request);
            evaluated = KernelChoiceContinuation.Evaluate(root, selected, owner, 1);
        }
        finally { observeEntryForks = false; }
        Check(evaluated.Steps == 0 && evaluated.CompleteHands == 0 && entryForks == 0
            && double.IsNegativeInfinity(evaluated.Value) && evaluated.StopReason == "request-deadline"
            && root.PartyStateText() == rootFrozen && selected.PartyStateText() == frozen,
            "an earlier settled enemy phase at stopped entry must not become a completed victory comparison");
        Console.WriteLine("PASS: stopped entry never upgrades an earlier settled enemy phase into a completed terminal witness.");
    }

    private static void ExhaustFallbackAtDeadline()
    {
        // Actual R2 exhaust-future-value-fallback: removing the Exhaust guard
        // made both paid native BurningPact and TrueGrit+ consume BLUDGEON.
        // Rebuild0 / runtime RED1 / exact source+DLL restore Rebuild0 / GREEN0:
        // System.InvalidOperationException: Choice continuation budget: deadline exhaust fallback must remove the low-value starter and preserve the unaffordable core with native payment piles and RNG
        var mismatches = new List<string>();
        foreach (var kind in new[] { "BurningPact", "TrueGrit+" })
        {
            var seed = "CHOICE-EXHAUST-FALLBACK-" + kind;
            var (combat, owner, enemy, source) = ExhaustBoard(seed, kind);
            var root = KernelSession.Capture(combat);
            var frozen = root.PartyStateText();
            var request = new KernelDecisionRequest(81303, 0);
            KernelSession.CardBranch[] branches;
            using (KernelSearchBudgetScope.Enter(request))
                branches = root.CardBranches(source, null, 10).ToArray();
            Check(branches.Length == 1 && branches[0].Boundary.Length == 0 && branches[0].State.CanFork,
                "expired exhaust fallback must finish exactly one legal native transaction");
            var branch = branches[0];
            // Independent original sts2 selector: request removal of the basic
            // Strike, rather than copying the kernel's proposed token.
            var (nativeCombat, nativeOwner, nativeEnemy, nativeSource) = ExhaustBoard(seed, kind);
            var nativeStrike = nativeOwner.PlayerCombatState!.Hand.Cards.OfType<StrikeIronclad>().Single();
            var nativeCore = nativeOwner.PlayerCombatState.Hand.Cards.OfType<Bludgeon>().Single();
            var chooser = new TestCardSelector();
            using (CardSelectCmd.PushSelector(chooser))
            using (new NativeTurn(nativeCombat))
            {
                chooser.PrepareToSelect(new[] { nativeStrike });
                NativePlay(nativeSource, null);
            }
            Check(nativeOwner.PlayerCombatState.Energy == 1
                && !nativeOwner.PlayerCombatState.HasEnoughResourcesFor(nativeCore, out _)
                && nativeOwner.PlayerCombatState.HasEnoughResourcesFor(nativeStrike, out _)
                && nativeOwner.PlayerCombatState.ExhaustPile.Cards.Single() == nativeStrike
                && nativeOwner.PlayerCombatState.Hand.Cards.Contains(nativeCore),
                "independent native exhaust oracle must pay to one energy and retain its now unaffordable core");
            Console.WriteLine($"DIAG choice-budget exhaust-fallback source={kind} selected=[{string.Join(',', branch.Choices.SelectMany(c => c.Cards).Select(c => c.Id))}] energy={branch.State.Energy(owner)} nativeEnergy={nativeOwner.PlayerCombatState.Energy}");
            var matches = branch.Choices.Count == 1 && branch.Choices.Single().Effect == "Exhaust"
                && branch.Choices.Single().Cards.Single().Id == nativeStrike.Id.Entry
                && MatchesNativeMenu(branch, owner, enemy, nativeCombat, nativeOwner, nativeEnemy, root, frozen);
            if (!matches) mismatches.Add(kind);
        }
        Check(mismatches.Count == 0,
            "deadline exhaust fallback must remove the low-value starter and preserve the unaffordable core with native payment piles and RNG");
        Console.WriteLine("PASS: expired BurningPact and upgraded TrueGrit preserve the unaffordable core while native exhaust payment piles and both RNG states match.");
    }

    private static void DiscardFallbackAtDeadline()
    {
        // Actual R2 discard-policy-preserved: extending the Exhaust guard to
        // Discard changed the paid Survivor policy from BLUDGEON to DEFEND.
        // Rebuild0 / runtime RED1 / exact source+DLL restore Rebuild0 / GREEN0:
        // System.InvalidOperationException: Choice continuation budget: exhaust fallback repair must preserve the existing unaffordable-card discard policy and its native piles and RNG
        const string seed = "CHOICE-DISCARD-FALLBACK";
        var (combat, owner, enemy, source) = ExhaustBoard(seed, "Survivor");
        var root = KernelSession.Capture(combat); var frozen = root.PartyStateText();
        var request = new KernelDecisionRequest(81304, 0);
        KernelSession.CardBranch[] branches;
        using (KernelSearchBudgetScope.Enter(request)) branches = root.CardBranches(source, null, 10).ToArray();
        Check(branches.Length == 1 && branches[0].Boundary.Length == 0 && branches[0].State.CanFork,
            "expired discard policy fixture must complete its one paid native branch");
        var branch = branches[0];
        var (nativeCombat, nativeOwner, nativeEnemy, nativeSource) = ExhaustBoard(seed, "Survivor");
        var nativeCore = nativeOwner.PlayerCombatState!.Hand.Cards.OfType<Bludgeon>().Single();
        var chooser = new TestCardSelector();
        using (CardSelectCmd.PushSelector(chooser))
        using (new NativeTurn(nativeCombat))
        {
            chooser.PrepareToSelect(new[] { nativeCore });
            NativePlay(nativeSource, null);
        }
        Console.WriteLine($"DIAG choice-budget discard-fallback selected=[{string.Join(',', branch.Choices.SelectMany(c => c.Cards).Select(c => c.Id))}] energy={branch.State.Energy(owner)} nativeEnergy={nativeOwner.PlayerCombatState.Energy}");
        Check(branch.Choices.Count == 1 && branch.Choices.Single().Effect == "Discard"
            && branch.Choices.Single().Cards.Single().Id == nativeCore.Id.Entry
            && nativeOwner.PlayerCombatState.Energy == 1
            && nativeOwner.PlayerCombatState.ExhaustPile.Cards.Count == 0
            && nativeOwner.PlayerCombatState.DiscardPile.Cards.Contains(nativeCore)
            && MatchesNativeMenu(branch, owner, enemy, nativeCombat, nativeOwner, nativeEnemy, root, frozen),
            "exhaust fallback repair must preserve the existing unaffordable-card discard policy and its native piles and RNG");
        Console.WriteLine("PASS: exhaust fallback repair preserves the existing paid Survivor discard policy without permanently losing the core.");
    }

    private static bool MatchesNativeMenu(KernelSession.CardBranch branch, Player owner, Creature enemy,
        CombatState nativeCombat, Player nativeOwner, Creature nativeEnemy, KernelSession root, string frozen)
        => branch.State.Energy(owner) == nativeOwner.PlayerCombatState!.Energy
            && branch.State.Block(owner.Creature) == nativeOwner.Creature.Block
            && branch.State.Hp(owner.Creature) == nativeOwner.Creature.CurrentHp
            && branch.State.Hp(enemy) == nativeEnemy.CurrentHp
            && Cards(branch.State.Hand(owner)) == Cards(nativeOwner.PlayerCombatState.Hand.Cards)
            && Cards(branch.State.Discard(owner)) == Cards(nativeOwner.PlayerCombatState.DiscardPile.Cards)
            && Cards(branch.State.DrawPile(owner)) == Cards(nativeOwner.PlayerCombatState.DrawPile.Cards)
            && Cards(branch.State.Exhaust(owner)) == Cards(nativeOwner.PlayerCombatState.ExhaustPile.Cards)
            && branch.State.Simulator.Rng.ShuffleState == ((RunState)nativeCombat.RunState).Rng.Shuffle.CaptureState()
            && branch.State.Simulator.Rng.CombatCardSelectionState == ((RunState)nativeCombat.RunState).Rng.CombatCardSelection.CaptureState()
            && root.PartyStateText() == frozen;

    private static (CombatState Combat, Player Owner, Creature Enemy, CardModel Source) ExhaustBoard(string seed, string kind)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 81202);
        var combat = new CombatState(runState: RunState.CreateForTest([owner], seed: seed)) { RoundNumber = 1 };
        owner.ResetCombatState(); combat.AddPlayer(owner); owner.MaxEnergy = 3;
        owner.PlayerCombatState!.Phase = PlayerTurnPhase.Play; owner.PlayerCombatState.GainEnergy(2);
        owner.Creature.SetMaxHpInternal(80); owner.Creature.SetCurrentHpInternal(80);
        CardModel source = kind switch { "BurningPact" => combat.CreateCard<BurningPact>(owner),
            "TrueGrit+" => combat.CreateCard<TrueGrit>(owner), _ => combat.CreateCard<Survivor>(owner) };
        if (source is TrueGrit) source.UpgradeInternal();
        owner.PlayerCombatState.Hand.AddInternal(source);
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Bludgeon>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(owner));
        owner.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<DefendIronclad>(owner));
        owner.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (combat, owner, enemy, source);
    }

    private static void CheckDiscardOracle(KernelSession.CardBranch branch, Player owner, Creature enemy,
        string seed, string frozen, KernelSession root)
    {
        var (nativeCombat, nativeOwner, nativeEnemy, nativeSurvivor) = Board(seed, 200, survivor: true);
        var token = branch.Choices.Single().Cards.Single();
        var selected = nativeOwner.PlayerCombatState!.Hand.Cards.Single(c => c.Id.Entry == token.Id);
        var chooser = new TestCardSelector();
        using (CardSelectCmd.PushSelector(chooser))
        using (new NativeTurn(nativeCombat))
        {
            chooser.PrepareToSelect(new[] { selected });
            NativePlay(nativeSurvivor!, null);
        }
        Check(branch.State.Energy(owner) == nativeOwner.PlayerCombatState!.Energy
            && branch.State.Block(owner.Creature) == nativeOwner.Creature.Block
            && branch.State.Hp(owner.Creature) == nativeOwner.Creature.CurrentHp
            && branch.State.Hp(enemy) == nativeEnemy.CurrentHp
            && Cards(branch.State.Hand(owner)) == Cards(nativeOwner.PlayerCombatState.Hand.Cards)
            && Cards(branch.State.Discard(owner)) == Cards(nativeOwner.PlayerCombatState.DiscardPile.Cards)
            && Cards(branch.State.DrawPile(owner)) == Cards(nativeOwner.PlayerCombatState.DrawPile.Cards)
            && Cards(branch.State.Exhaust(owner)) == Cards(nativeOwner.PlayerCombatState.ExhaustPile.Cards)
            && branch.State.Simulator.Rng.ShuffleState == ((RunState)nativeCombat.RunState).Rng.Shuffle.CaptureState()
            && branch.State.Simulator.Rng.CombatCardSelectionState == ((RunState)nativeCombat.RunState).Rng.CombatCardSelection.CaptureState()
            && root.PartyStateText() == frozen,
            "the retained discard transaction must match independent native hooks payment piles and both RNG states without mutating its root");
    }

    private static string Cards(IEnumerable<CardModel> cards)
        => string.Join(',', cards.Select(c => c.Id.Entry + "+" + c.CurrentUpgradeLevel));

    private static (CombatState Combat, Player Owner, Creature Enemy, Survivor? Survivor) Board(string seed,
        int enemyHp, bool survivor)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 81201);
        var combat = new CombatState(runState: RunState.CreateForTest([owner], seed: seed)) { RoundNumber = 1 };
        owner.ResetCombatState(); combat.AddPlayer(owner); owner.MaxEnergy = 3;
        owner.PlayerCombatState!.Phase = PlayerTurnPhase.Play; owner.PlayerCombatState.GainEnergy(3);
        owner.Creature.SetMaxHpInternal(80); owner.Creature.SetCurrentHpInternal(80);
        Survivor? card = survivor ? combat.CreateCard<Survivor>(owner) : null;
        if (card is not null) owner.PlayerCombatState.Hand.AddInternal(card);
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(owner));
        if (survivor) owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendSilent>(owner));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (combat, owner, enemy, card);
    }

    private static void NativePlay(CardModel card, Creature? target)
    {
        var context = new ThrowingPlayerChoiceContext();
        var player = card.Owner;
        Check(player.PlayerCombatState!.HasEnoughResourcesFor(card, out _) && card.IsValidTarget(target),
            "native choice oracle must pay and target a legal card");
        var spent = card.SpendResources().GetAwaiter().GetResult();
        player.PlayerCombatState.Hand.RemoveInternal(card); player.PlayerCombatState.PlayPile.AddInternal(card);
        var play = new CardPlay { Card = card, Player = player, Target = target, ResultPile = PileType.Discard,
            Resources = new ResourceInfo { EnergySpent = spent.Item1, EnergyValue = spent.Item1,
                StarsSpent = spent.Item2, StarValue = spent.Item2 }, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        Hook.BeforeCardPlayed(card.CombatState!, play).GetAwaiter().GetResult();
        ((Task)AccessTools.Method(card.GetType(), "OnPlay").Invoke(card, [context, play])!).GetAwaiter().GetResult();
        Hook.AfterCardPlayed(card.CombatState!, context, play).GetAwaiter().GetResult();
        player.PlayerCombatState.PlayPile.RemoveInternal(card); player.PlayerCombatState.DiscardPile.AddInternal(card);
    }

    private sealed class NativeTurn : IDisposable
    {
        private readonly FieldInfo turnField = AccessTools.Field(typeof(CombatManager), "_turnState");
        private readonly PropertyInfo runField = AccessTools.Property(typeof(RunManager), "State");
        private readonly PropertyInfo ascensionField = AccessTools.Property(typeof(RunManager), nameof(RunManager.AscensionManager));
        private readonly object? previousTurn, previousRun, previousAscension;
        private readonly ulong? previousLocal = LocalContext.NetId;
        internal NativeTurn(CombatState combat)
        {
            previousTurn = turnField.GetValue(CombatManager.Instance);
            previousRun = runField.GetValue(RunManager.Instance); previousAscension = ascensionField.GetValue(RunManager.Instance);
            runField.SetValue(RunManager.Instance, combat.RunState);
            ascensionField.SetValue(RunManager.Instance, new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(0));
            LocalContext.NetId = combat.Players[0].NetId;
            var turn = Activator.CreateInstance(turnField.FieldType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
            AccessTools.Property(turnField.FieldType, "IsInProgress").SetValue(turn, true);
            AccessTools.Property(turnField.FieldType, "IsStarting").SetValue(turn, false);
            turnField.SetValue(CombatManager.Instance, turn);
        }
        public void Dispose()
        {
            turnField.SetValue(CombatManager.Instance, previousTurn); LocalContext.NetId = previousLocal;
            runField.SetValue(RunManager.Instance, previousRun); ascensionField.SetValue(RunManager.Instance, previousAscension);
        }
    }

    private static void ObservePlay()
    {
        if (cancelAtPlay is not { } request) return;
        observedPlays++;
        request.Cancel("native-choice-hook");
    }
    private static void ObserveFork() { if (observeEntryForks) entryForks++; }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool PlayerName(ulong playerId, ref string __result) { __result = playerId.ToString(); return false; }
    private static bool Version(ref PeerVersionInfo __result) { __result = default; return false; }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Choice continuation budget: " + message); }
}
