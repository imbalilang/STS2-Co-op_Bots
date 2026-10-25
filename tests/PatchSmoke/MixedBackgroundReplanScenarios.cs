// V2 R2 ownership guards: build0/RED1 -> exact restore -> build0/GREEN0.
// Receipt: work/v2-validation/r2/ownership-pass/receipts.json
// System.InvalidOperationException: Mixed background: a pure all-Bot script must ignore unowned mixed caches instead of demoting quality, rebuilding its array or restarting refinement
// System.InvalidOperationException: Mixed background: a new combat must not inherit the old fight's human/control/living execution ownership
// V2 R2 actual 2026-10-24: production build0 -> named runtime RED1 -> exact source restore -> build0/GREEN0.
// Private receipts: work/v2-validation/r2/{receipts,second-pass/receipts,final-pass/receipts}.json.
// These are fault controls, not distinct gameplay bug or live-performance counts.
// human-input-keeps-proposal: System.InvalidOperationException: Mixed background: ten settled human actions must coalesce without starting ten searches or clearing the foreground script
// full-tournament-both-phases: System.InvalidOperationException: Mixed background: human readiness must preserve the same full portfolio, joint search, horizon and budget
// no-four-action-renewal: System.InvalidOperationException: Mixed background: four successful Bot steps must not expire a still-executable mixed continuation
// candidate-keeps-reservation: System.InvalidOperationException: Mixed background: background improvement cannot steal a card already reserved for paced submission
// candidate-same-root-only: System.InvalidOperationException: Mixed background: unobserved actual-root drift cannot be relabeled as a current background candidate
// candidate-strictly-better: System.InvalidOperationException: Mixed background: equal-quality background ending must keep the old foreground line
// control-retirement: System.InvalidOperationException: Mixed background: returning a seat must retire its actions while retaining another Bot's physical proposal
// control-reservation-rebind: System.InvalidOperationException: Mixed background: retiring another seat must rebind the unaffected Bot reservation to the retained suffix
// allbot-future-optimizer: System.InvalidOperationException: Mixed background: delegating the last human must start the original future-round optimizer on the actual remaining suffix
// background-does-not-block: System.InvalidOperationException: Mixed background: a running mixed background comparison cannot block the foreground's executable next card
// shared-budget-no-renew: System.InvalidOperationException: Mixed background: repeated human revisions must not replenish the round's shared allowance or consume execution repair reserve
// human-death-mode: System.InvalidOperationException: Mixed background: last-human death must retire mixed background work and start original future-round refinement
// bot-death-retirement: System.InvalidOperationException: Mixed background: a Bot dying on a mixed table must retire only its own steps while preserving a surviving seat's proposal
// cold-foreground-wiring: System.InvalidOperationException: Mixed background: human readiness must preserve the same full portfolio, joint search, horizon and budget
// cold-background-wiring: System.InvalidOperationException: Mixed background: one stable boundary must admit exactly one incrementally prepared full-tournament job for the latest human revision
// historical-death-execution: System.InvalidOperationException: Mixed background: historical mixed predictions cannot become strict all-Bot evidence merely because their human died
// historical-death-receipt: System.InvalidOperationException: Mixed background: last-human death must keep an executable old Bot card despite its obsolete all-seats-alive prediction
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Unlocks;

// Production settled-input notifications, foreground Poll, and the actual-root
// optional planner are exercised together. Native card bodies are not replaced.
// Physical mutation RED text is appended by the serialized integration runner.
internal static class MixedBackgroundReplanScenarios
{
    private sealed class HumanInput(Player owner) : EndPlayerTurnAction(owner, 1)
    { protected override Task ExecuteAction() => Task.CompletedTask; }

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.mixed-background.presentation");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedBackgroundReplanScenarios), nameof(Format)));
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null;
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("input-burst", InputBurst), ("full-options", FullOptions),
                ("stable-script", StableScript), ("nonblocking", Nonblocking),
                ("shared-budget", SharedBudget), ("candidate", Candidate),
                ("control", Control), ("allbot", AllBot), ("human-death", HumanDeath), ("bot-death", BotDeath), ("pure-allbot", PureAllBot), ("reset-domain", ResetDomain) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: mixed background " + test.Name); }
        }
        finally { KernelCombatPlanner.TournamentOverride = previous; harmony.UnpatchAll(harmony.Id); }
    }

    internal sealed class Board : IDisposable
    {
        internal readonly CombatState Combat;
        internal readonly Player Bot, Human;
        internal readonly KernelCombatPlanner Planner;
        internal readonly ActionQueueSet Queue;
        internal readonly KernelTeamSearch.Action[] Script;
        private readonly IDisposable native;
        private readonly bool wasBot, wasHuman;
        internal Board()
        {
            var (combat, bot, enemy) = KernelPlanningStateScenarios.Board();
            Combat = combat; Bot = bot;
            Human = Player.CreateForNewRun<Deprived>(UnlockState.all, 38132);
            Human.ResetCombatState(); Combat.AddPlayer(Human);
            Human.Creature.SetMaxHpInternal(80); Human.Creature.SetCurrentHpInternal(80);
            Human.PlayerCombatState!.Phase = PlayerTurnPhase.Play; Human.PlayerCombatState.GainEnergy(3);
            wasBot = AutoPilot.IsAutopiloted(Bot.NetId); wasHuman = AutoPilot.IsAutopiloted(Human.NetId);
            AutoPilot.Set(Bot.NetId, true); AutoPilot.Set(Human.NetId, false);
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [Combat], null)!;
            Planner = (KernelCombatPlanner)GetStatic(typeof(BotRuntime), "KernelPlanner")!;
            Planner.Reset(newCombat: true);
            Queue = new ActionQueueSet([Bot, Human]); Queue.CombatStarted();
            KernelInputObserver.Observe(Queue, Combat);
            var root = KernelCombatCapture.Capture(Combat);
            Script = root.Hand(Bot).Select(card => new KernelTeamSearch.Action(Bot, card, enemy,
                CardStateKey: root.CardStateKey(card))).ToArray();
            Set(Planner, "tournamentScript", Script); Set(Planner, "tournamentScriptIndex", 0);
            Set(Planner, "tournamentScriptCombat", Combat); Set(Planner, "tournamentScriptRound", Combat.RoundNumber);
            Set(Planner, "tournamentScriptRecord", TerminalRecord.Capture(root, Combat.Players.ToArray(), false) with {
                Victory = true, Kind = EvidenceKind.VerifiedTerminal });
            Set(Planner, "tournamentScriptAliveStates", Script.Select(_ => Combat.Players.Select(p => p.NetId).Order().ToArray()).ToArray());
            Set(Planner, "tournamentScriptEnemyStates", Script.Select(_ => new[] { enemy }).ToArray());
            Invoke(Planner, "CaptureMixedContinuation", root, Combat, Script, 0);
        }
        internal void Input()
        {
            var action = new HumanInput(Human);
            Queue.EnqueueWithoutSynchronizing(action); action.Execute().GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            Planner.Reset(newCombat: true);
            var drained = System.Diagnostics.Stopwatch.StartNew();
            while (!KernelBackgroundOperation.IsIdle && drained.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            Check(KernelBackgroundOperation.IsIdle, "cancelled frozen worker must drain before native fixture globals are restored");
            KernelInputObserver.Observe(Queue, null);
            AutoPilot.Set(Bot.NetId, wasBot); AutoPilot.Set(Human.NetId, wasHuman); native.Dispose();
        }
    }

    private static void InputBurst()
    {
        using var b = new Board();
        for (var i = 0; i < 10; i++) b.Input();
        Check(KernelInputObserver.Revision == 10 && ReferenceEquals(Get(b.Planner, "tournamentScript"), b.Script)
            && (int)Get(b.Planner, "tournamentScriptIndex")! == 0
            && (int)Get(b.Planner, "mixedReplanStarts")! == 0
            && (int)Get(b.Planner, "mixedReplanCoalesced")! >= 9,
            "ten settled human actions must coalesce without starting ten searches or clearing the foreground script");
        b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        Check((int)Get(b.Planner, "mixedReplanStarts")! == 1
            && ((TournamentOptions)Get(Get(b.Planner, "mixedReplan")!, "Options")!).MixedIncrementalOpenings,
            "one stable boundary must admit exactly one incrementally prepared full-tournament job for the latest human revision");
        for (var i = 0; i < 10; i++) b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 1100);
        Check((int)Get(b.Planner, "mixedReplanStarts")! == 1 && (bool)Get(b.Planner, "mixedReplanDirty")!
            && ReferenceEquals(Get(b.Planner, "tournamentScript"), b.Script),
            "newer burst must retire obsolete work but cannot readmit another job in the same team card window");
    }

    private static void FullOptions()
    {
        using var b = new Board();
        var before = new KernelCombatPlanner(); var after = new KernelCombatPlanner();
        try
        {
            Check((bool)Invoke(before, "BeginTournamentPhase", b.Combat, new[] { b.Bot }, false)!, "open-human phase must start");
            Check((bool)Invoke(after, "BeginTournamentPhase", b.Combat, new[] { b.Bot }, true)!, "finished-human phase must start");
            var a = (TournamentOptions)Get(before, "tournamentOptions")!;
            var z = (TournamentOptions)Get(after, "tournamentOptions")!;
            var ar = (KernelDecisionRequest)Get(before, "tournamentRequest")!;
            var zr = (KernelDecisionRequest)Get(after, "tournamentRequest")!;
            Check(a.MixedIncrementalOpenings && z.MixedIncrementalOpenings && a.UsePolicyPortfolio && a.EnableJointSearch && a.Rollout!.MaxRounds == 80 && a.Rollout.MaxSteps == 600
                && a.TopK == z.TopK && a.UsePolicyPortfolio == z.UsePolicyPortfolio && a.EnableJointSearch == z.EnableJointSearch
                && a.MaxDegreeOfParallelism == z.MaxDegreeOfParallelism && a.Rollout == z.Rollout
                && ar.BudgetMs == zr.BudgetMs && ar.BudgetMs > 400 && ar.MaxConcurrency == zr.MaxConcurrency,
                "human readiness must preserve the same full portfolio, joint search, horizon and budget");
        }
        finally { before.Reset(newCombat: true); after.Reset(newCombat: true); }
    }

    private static void StableScript()
    {
        using var b = new Board();
        // A cursor at four used to force replay/re-root even without any input.
        var longScript = Enumerable.Repeat(b.Script[0], 8).ToArray();
        Set(b.Planner, "tournamentScript", longScript); Set(b.Planner, "tournamentScriptIndex", 4);
        Check(!(bool)Invoke(b.Planner, "ReviewMixedInput", b.Combat)!
            && ReferenceEquals(Get(b.Planner, "tournamentScript"), longScript)
            && (int)Get(b.Planner, "tournamentScriptIndex")! == 4,
            "four successful Bot steps must not expire a still-executable mixed continuation");
        b.Input();
        Check(!(bool)Invoke(b.Planner, "ReviewMixedInput", b.Combat)!
            && ReferenceEquals(Get(b.Planner, "tournamentScript"), longScript)
            && Get(b.Planner, "tournamentScriptRecord") is TerminalRecord { Victory: false, Verified: false }
            && Get(b.Planner, "mixedProposalReplay") is null,
            "human input must demote old victory while retaining legal proposals without foreground native replay");
    }

    private static void Nonblocking()
    {
        using var b = new Board(); b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        Check(Get(b.Planner, "mixedReplan") is not null, "fixture must have an active optional actual-root job");
        var state = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out var decision);
        Check(state == KernelCombatPlanner.Status.Ready && decision?.Move.Card == b.Script[0].Card,
            "a running mixed background comparison cannot block the foreground's executable next card");
        b.Planner.AdvanceMixedReplan(b.Combat, 1100);
        Check(ReferenceEquals(Get(b.Planner, "tournamentScript"), b.Script)
            && (int)Get(b.Planner, "mixedReplanStarts")! == 1,
            "foreground progress must discard stale background provenance without clearing its own continuation");
    }

    private static void SharedBudget()
    {
        using var b = new Board();
        var original = (int)Invoke(b.Planner, "MixedRemainingReplanBudget", b.Combat)!;
        Set(b.Planner, "mixedEpisodeSpentMs", (double)original - 1);
        for (var i = 0; i < 10; i++) b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        Check((int)Invoke(b.Planner, "MixedRemainingReplanBudget", b.Combat)! == 1
            && (int)Get(b.Planner, "mixedReplanStarts")! == 0,
            "repeated human revisions must not replenish optional round credit");
        Check((int)Invoke(b.Planner, "MixedRemainingDecisionBudget", b.Combat)! == original,
            "exhausted optional improvement must leave the full necessary repair request available");
    }

    private static void Control()
    {
        using var b = new Board();
        var delegated = new KernelTeamSearch.Action(b.Human, null, null, EndTurn: true);
        Set(b.Planner, "tournamentScript", new[] { b.Script[0], delegated });
        Set(b.Planner, "mixedControl", string.Join(',', new[] { b.Bot.NetId, b.Human.NetId }.Order()));
        Invoke(b.Planner, "PrepareTournamentSubmission", (object?)null);
        AutoPilot.Set(b.Human.NetId, false);
        Invoke(b.Planner, "ReviewMixedInput", b.Combat);
        var remaining = (IReadOnlyList<KernelTeamSearch.Action>)Get(b.Planner, "tournamentScript")!;
        Check(remaining.Count == 1 && remaining[0].Player == b.Bot,
            "returning a seat must retire its actions while retaining another Bot's physical proposal");
        Check(b.Planner.HasPreparedSubmission
            && ReferenceEquals(Get(Get(b.Planner, "preparedSubmission")!, "Script"), remaining),
            "retiring another seat must rebind the unaffected Bot reservation to the retained suffix");
    }

    private static void Candidate()
    {
        using var b = new Board(); b.Input();
        b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        var job = Get(b.Planner, "mixedReplan")!;
        var root = (KernelSession)Get(job, "Root")!;
        var request = (KernelDecisionRequest)Get(job, "Request")!;
        TournamentResult Replay(KernelTeamSearch.Action[] script)
        {
            var proposal = new TournamentResult(script[0], null, [], 0, 0, "native-test-proposal") { Script = script };
            var replay = new KernelScriptReplayRun(root, b.Combat.Players.ToArray(), proposal, 80, request);
            while (!replay.Done) replay.Advance(TimeSpan.FromMilliseconds(2));
            Check(replay.Result?.Record is not null, "candidate witness must complete its actual-root native replay");
            return replay.Result!;
        }
        var reference = Replay([b.Script[0]]);
        var better = Replay(b.Script);
        Check(TerminalComparer.Compare(better.Record!, reference.Record!) > 0,
            "two paid native Strikes must improve this nonterminal same-root reference over one");
        Set(job, "Baseline", better);
        Check(!(bool)Invoke(b.Planner, "TryAdoptMixedCandidate", b.Combat, job, better)!,
            "equal-quality background ending must keep the old foreground line");
        Set(job, "Baseline", reference);
        Invoke(b.Planner, "PrepareTournamentSubmission", (object?)null);
        Check(!(bool)Invoke(b.Planner, "TryAdoptMixedCandidate", b.Combat, job, better)!
            && ReferenceEquals(Get(b.Planner, "tournamentScript"), b.Script),
            "background improvement cannot steal a card already reserved for paced submission");
        b.Planner.AbandonPreparedSubmission("native fixture clears reservation");
        var enemy = b.Script[0].Target!;
        var hp = enemy.CurrentHp; enemy.SetCurrentHpInternal(hp - 1);
        Check(!(bool)Invoke(b.Planner, "TryAdoptMixedCandidate", b.Combat, job, better)!,
            "unobserved actual-root drift cannot be relabeled as a current background candidate");
        enemy.SetCurrentHpInternal(hp);
        Check((bool)Invoke(b.Planner, "TryAdoptMixedCandidate", b.Combat, job, better)!
            && (int)Get(b.Planner, "tournamentScriptIndex")! == 0
            && (int)Get(b.Planner, "mixedReplanAdoptions")! == 1,
            "strictly better same-root replay must replace only the unsubmitted foreground suffix");
    }

    private static void AllBot()
    {
        using var b = new Board();
        AutoPilot.Set(b.Human.NetId, true);
        Invoke(b.Planner, "ReviewMixedInput", b.Combat);
        var original = Get(b.Planner, "refinement");
        Check(original is not null && ReferenceEquals(Get(b.Planner, "refinementScript"), Get(b.Planner, "tournamentScript"))
            && (int)Get(b.Planner, "tournamentScriptIndex")! == 0,
            "delegating the last human must start the original future-round optimizer on the actual remaining suffix");
        b.Planner.NoteMixedInput(); b.Planner.AdvanceMixedReplan(b.Combat, 1000);
        Check(ReferenceEquals(Get(b.Planner, "refinement"), original) && Get(b.Planner, "mixedReplan") is null,
            "mixed current-root scheduling must not replace or cancel the all-Bot future-round optimizer");
    }

    private static void HumanDeath()
    {
        using var b = new Board();
        b.Input();
        Invoke(b.Planner, "ReviewMixedInput", b.Combat);
        // No AutoPilot change: this is a native life-domain transition, unlike
        // the existing explicit human-to-Bot delegation regression.
        b.Human.Creature.SetCurrentHpInternal(0);
        Invoke(b.Planner, "ReviewMixedInput", b.Combat);
        Check(Get(b.Planner, "refinement") is not null
            && ReferenceEquals(Get(b.Planner, "refinementScript"), Get(b.Planner, "tournamentScript"))
            && Get(b.Planner, "mixedReplan") is null,
            "last-human death must retire mixed background work and start original future-round refinement");
        Check((bool)Invoke(b.Planner, "UsesMixedExecution", b.Combat)!,
            "historical mixed predictions cannot become strict all-Bot evidence merely because their human died");
        var status = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, true, BotDifficulty.Pro, out var decision);
        Check(status == KernelCombatPlanner.Status.Ready && decision?.Move.Card == b.Script[0].Card
            && b.Planner.HasPreparedSubmission,
            "last-human death must keep an executable old Bot card despite its obsolete all-seats-alive prediction");
        // Replace the source by a genuinely new all-Bot line: provenance is
        // reference-bound and cannot permanently weaken all-Bot execution.
        Set(b.Planner, "tournamentScript", b.Script.ToArray());
        Check(!(bool)Invoke(b.Planner, "UsesMixedExecution", b.Combat)!,
            "a new all-Bot plan must recover original strict prediction checks after historical suffix replacement");
    }

    private static void BotDeath()
    {
        using var b = new Board();
        var survivor = Player.CreateForNewRun<Deprived>(UnlockState.all, 38133);
        survivor.ResetCombatState(); b.Combat.AddPlayer(survivor);
        survivor.Creature.SetMaxHpInternal(80); survivor.Creature.SetCurrentHpInternal(80);
        survivor.PlayerCombatState!.Phase = PlayerTurnPhase.Play; survivor.PlayerCombatState.GainEnergy(3);
        survivor.PlayerCombatState.Hand.AddInternal(b.Combat.CreateCard<StrikeIronclad>(survivor));
        AutoPilot.Set(survivor.NetId, true);
        try
        {
            var action = new KernelTeamSearch.Action(survivor, survivor.PlayerCombatState.Hand.Cards[0], b.Script[0].Target);
            var source = new[] { b.Script[0], action };
            var root = KernelCombatCapture.Capture(b.Combat);
            Set(b.Planner, "tournamentScript", source);
            Invoke(b.Planner, "CaptureMixedContinuation", root, b.Combat, source, 0);
            b.Bot.Creature.SetCurrentHpInternal(0);
            Invoke(b.Planner, "ReviewMixedInput", b.Combat);
            var retained = (IReadOnlyList<KernelTeamSearch.Action>)Get(b.Planner, "tournamentScript")!;
            Check(retained.Count == 1 && ReferenceEquals(retained[0], action),
                "a Bot dying on a mixed table must retire only its own steps while preserving a surviving seat's proposal");
        }
        finally { AutoPilot.Set(survivor.NetId, false); }
    }

    private static void PureAllBot()
    {
        using var b = new Board(); AutoPilot.Set(b.Human.NetId, true);
        var root = KernelCombatCapture.Capture(b.Combat);
        Invoke(b.Planner, "CaptureMixedContinuation", root, b.Combat, b.Script, 0);
        Set(b.Planner, "mixedControl", "stale-unowned-cache");
        var record = Get(b.Planner, "tournamentScriptRecord");
        Check(!(bool)Invoke(b.Planner, "ReviewMixedInput", b.Combat)!
            && ReferenceEquals(Get(b.Planner, "tournamentScript"), b.Script)
            && ReferenceEquals(Get(b.Planner, "tournamentScriptRecord"), record)
            && Get(b.Planner, "refinement") is null && !(bool)Get(b.Planner, "mixedReplanDirty")!,
            "a pure all-Bot script must ignore unowned mixed caches instead of demoting quality, rebuilding its array or restarting refinement");
    }

    private static void ResetDomain()
    {
        using var b = new Board();
        Check((bool)Get(b.Planner, "mixedCapturedLivingHuman")!, "fixture must own a real mixed-domain cache");
        b.Planner.Reset(newCombat: true);
        Check(!(bool)Get(b.Planner, "mixedCapturedLivingHuman")! && (string)Get(b.Planner, "mixedControl")! == ""
            && (string)Get(b.Planner, "mixedLivingDomain")! == "" && Get(b.Planner, "mixedHistoricalScript") is null,
            "a new combat must not inherit the old fight's human/control/living execution ownership");
    }

    private static object? GetStatic(Type type, string name) => AccessTools.Field(type, name).GetValue(null);
    private static object? Get(object obj, string name) => AccessTools.Field(obj.GetType(), name).GetValue(obj);
    private static void Set(object obj, string name, object? value) => AccessTools.Field(obj.GetType(), name).SetValue(obj, value);
    private static object? Invoke(object obj, string name, params object?[] args) => AccessTools.Method(obj.GetType(), name).Invoke(obj, args);
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException("Mixed background: " + text); }
    private static bool Format(ref string __result) { __result = ""; return false; }
}

// 2026-10-25 coral local repair: actual production mutation build0/RED1 -> exact bytes restored -> build0/GREEN0.
// coral-budget-isolation: System.InvalidOperationException: Mixed background: exhausted optional improvement must leave the full necessary repair request available
