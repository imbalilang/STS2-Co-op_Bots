using System.Diagnostics;
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// These drive the shipped Poll entry with an ordinary living human, not the
// historical segmented planner. Native paid card bodies use the existing
// presentation-only native fixture.
// R2 actual 2026-10-24: production mutant build0 / RED1 / exact restore / GREEN0.
// Receipt: work/mixed-fixes-20261023T164339Z/r2-valid-proofs.json
// mixed-deadline: System.InvalidOperationException: Mixed responsiveness: live human planning must have a short interaction deadline rather than the all-Bot 6400ms allowance
// mixed-current-root-proposal: System.InvalidOperationException: Mixed responsiveness: a suffix replayed from the actual changed root must supply its action without starting another tournament
// tournament-status: System.InvalidOperationException: Mixed responsiveness: actual mixed tournament work must be visible and use a bounded native one-round comparison
// mixed-round-horizon: System.InvalidOperationException: Mixed responsiveness: actual mixed tournament work must be visible and use a bounded native one-round comparison
// mixed-control-handover: System.InvalidOperationException: Mixed responsiveness: returning control must retire old delegated scripts and in-flight proposals before replay
// mixed-control-gain (existing planner-request regression): System.InvalidOperationException: Planner request: handing the last human seat to a Bot must renew the old mixed plan's control domain
internal static class MixedResponsivenessScenarios
{
    private static readonly MethodInfo Paid = AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard");
    private static PlayCardAction? inputAction;
    private static CardModel? inputCard;
    private static Creature? inputTarget;
    // Replace only the input/network lookup of our selected native action;
    // its body still pays and executes the real native card wrapper. Native
    // GameAction.Execute and AfterFinished remain installed and unmodified.
    private static bool HumanBody(PlayCardAction __instance, ref Task __result)
    {
        if (!ReferenceEquals(inputAction, __instance)) return true;
        Paid.Invoke(null, [inputCard, inputTarget]);
        __result = Task.CompletedTask;
        return false;
    }

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var previous = KernelCombatPlanner.TournamentOverride;
        var presentation = new Harmony("coopbots.mixed-responsiveness.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedResponsivenessScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"),
            prefix: new HarmonyMethod(typeof(MixedResponsivenessScenarios), nameof(HumanBody)));
        KernelCombatPlanner.TournamentOverride = null;
        try
        {
            if (selected is null or "progress") ProgressBeforeHumanEnd();
            if (selected is null or "proposal") RenewedProposalProvidesAction();
            if (selected is null or "status") CurrentSearchIsVisible();
            if (selected is null or "control") ReturnedHumanOwnsTheirCards();
        }
        finally { KernelCombatPlanner.TournamentOverride = previous; presentation.UnpatchAll(presentation.Id); }
    }

    private sealed class Board : IDisposable
    {
        internal readonly Player Bot, Human;
        internal readonly CombatState Combat;
        internal readonly Creature Enemy;
        internal readonly KernelCombatPlanner Planner;
        internal readonly ActionQueueSet Queue;
        private readonly IDisposable native;
        internal Board()
        {
            Bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 98, 0));
            Human = Player.CreateForNewRun<Deprived>(UnlockState.all, 19801);
            Combat = new CombatState(runState: RunState.CreateForTest([Bot, Human], seed: "MIXED-INTERLEAVING")) { RoundNumber = 1 };
            foreach (var p in new[] { Bot, Human })
            {
                p.ResetCombatState(); Combat.AddPlayer(p);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
                p.PlayerCombatState.GainEnergy(6);
                for (var i = 0; i < 6; i++) p.PlayerCombatState.Hand.AddInternal(Combat.CreateCard<StrikeIronclad>(p));
            }
            Enemy = Combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
            Combat.AddCreature(Enemy); Enemy.Monster!.SetUpForCombat(); Enemy.SetMaxHpInternal(200); Enemy.SetCurrentHpInternal(200);
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [Combat], null)!;
            // Isolated case processes also JIT the native transaction/rollout
            // stack. Warm on forks before measuring the interaction request;
            // cold startup is outside the scheduling contract, as in the other
            // native deadline fixtures. The live board remains untouched.
            var warm = KernelCombatCapture.Capture(Combat);
            _ = KernelTournament.Run(warm, Combat.Players.ToArray(), [Bot],
                new TournamentOptions(ReserveMs: 0, IncludePotions: false, IncludeEndTurns: false,
                    UsePolicyPortfolio: false, Rollout: new(MaxRounds: 1, MaxSteps: 32)), 5000);
            Planner = (KernelCombatPlanner)AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
            Planner.Reset(newCombat: true);
            Queue = new ActionQueueSet([Bot, Human]); Queue.CombatStarted();
            AutoPilot.Set(Human.NetId, false);
            KernelInputObserver.Observe(Queue, Combat);
        }
        internal void HumanCard()
        {
            var card = Human.PlayerCombatState!.Hand.Cards.First(c => c is StrikeIronclad);
            var action = new PlayCardAction(Human, NetCombatCard.ForTesting(0), card.Id, Enemy.CombatId);
            inputAction = action; inputCard = card; inputTarget = Enemy;
            try
            {
                Queue.EnqueueWithoutSynchronizing(action);
                action.Execute().GetAwaiter().GetResult();
            }
            finally { inputAction = null; inputCard = null; inputTarget = null; }
        }
        public void Dispose()
        {
            AutoPilot.Set(Human.NetId, false);
            KernelInputObserver.Observe(Queue, null);
            Planner.Reset(newCombat: true);
            native.Dispose();
        }
    }

    private static void ProgressBeforeHumanEnd()
    {
        using var b = new Board();
        var first = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out _);
        var before = Get(b.Planner, "tournamentRequest") as KernelDecisionRequest;
        Check(before is null || before.BudgetMs <= 400,
            "live human planning must have a short interaction deadline rather than the all-Bot 6400ms allowance");
        for (var i = 0; i < 3; i++)
        {
            b.HumanCard();
            if (before is not null) Check(before.IsCancelled, "settled human card must still cancel stale-root work");
            b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out _);
            before = Get(b.Planner, "tournamentRequest") as KernelDecisionRequest;
        }
        var decision = Decision(b);
        Check(decision.Player == b.Bot && decision.Move.Card.Owner == b.Bot
            && b.Human.PlayerCombatState!.Energy > 0 && b.Human.PlayerCombatState.Phase == PlayerTurnPhase.Play
            && KernelInputObserver.Revision == 3,
            "Bot must produce a native current-root card while the human still has energy and has never ended turn");
        Paid.Invoke(null, [decision.Move.Card, decision.Move.Target]);
        Check(b.Bot.PlayerCombatState!.Energy == 5 && b.Enemy.CurrentHp < 182,
            "the interleaved Bot card must actually execute through the paid native body");
        Console.WriteLine("PASS: mixed continuous native human cards cancel stale work and Bot plays before the human ends turn.");
    }

    private static void RenewedProposalProvidesAction()
    {
        using var b = new Board();
        var initial = Decision(b);
        Paid.Invoke(null, [initial.Move.Card, initial.Move.Target]);
        var passes = (int)Get(b.Planner, "tournamentPasses")!;
        b.HumanCard();
        var next = Decision(b);
        Check((int)Get(b.Planner, "tournamentPasses")! == passes,
            "a suffix replayed from the actual changed root must supply its action without starting another tournament");
        Check(next.Player == b.Bot && b.Human.PlayerCombatState!.Energy > 0,
            "renewed proposal action belongs only to a controlled seat before human end-turn");
        Console.WriteLine("PASS: mixed current-root native suffix replay supplies the next Bot action without a fresh tournament.");
    }

    private static void CurrentSearchIsVisible()
    {
        using var b = new Board();
        var begin = AccessTools.Method(typeof(KernelCombatPlanner), "BeginTournamentPhase");
        Check((bool)begin.Invoke(b.Planner, [b.Combat, new[] { b.Bot }, false])!, "production phase must start");
        var options = (TournamentOptions)Get(b.Planner, "tournamentOptions")!;
        Check(b.Planner.IsSearching && b.Planner.SearchingWallBudgetMs <= 400
            && options.Rollout?.MaxRounds == 1 && !options.EnableJointSearch,
            "actual mixed tournament work must be visible and use a bounded native one-round comparison");
        Console.WriteLine("PASS: mixed tournament pending work is visible with its real interaction deadline.");
    }

    private static void ReturnedHumanOwnsTheirCards()
    {
        using var b = new Board();
        // Initialize through the production entry before seeding the boundary
        // between an old delegated script and the next settled control domain.
        Decision(b);
        AutoPilot.Set(b.Human.NetId, true);
        var root = KernelCombatCapture.Capture(b.Combat);
        KernelTeamSearch.Action[] script = [
            new(b.Human, b.Human.PlayerCombatState!.Hand.Cards[0], b.Enemy),
            new(b.Bot, b.Bot.PlayerCombatState!.Hand.Cards[0], b.Enemy) ];
        var record = TerminalRecord.Capture(root, b.Combat.Players, false, "old-controlled-root");
        Set(b.Planner, "tournamentScript", script);
        Set(b.Planner, "tournamentScriptIndex", 0);
        Set(b.Planner, "tournamentScriptCombat", b.Combat);
        Set(b.Planner, "tournamentScriptRecord", record);
        AccessTools.Method(typeof(KernelCombatPlanner), "CaptureMixedContinuation")
            .Invoke(b.Planner, [root, b.Combat, script, 0]);
        Set(b.Planner, "mixedProposal", new TournamentResult(script[0], record, [], 0, 0, "old-delegation") { Script = script });
        AutoPilot.Set(b.Human.NetId, false);
        AccessTools.Method(typeof(KernelCombatPlanner), "ReviewMixedInput").Invoke(b.Planner, [b.Combat]);
        Check(Get(b.Planner, "mixedProposal") is null && Get(b.Planner, "mixedReadyProposal") is null
            && Get(b.Planner, "tournamentScript") is null,
            "returning control must retire old delegated scripts and in-flight proposals before replay");
        var next = Decision(b);
        Check(next.Player == b.Bot && next.Move.Card.Owner == b.Bot && b.Human.PlayerCombatState.Hand.Cards.Count == 6,
            "fresh production Poll after handback must leave every human card under human control");
        Console.WriteLine("PASS: mixed control handback retires delegated proposals and only the remaining Bot acts.");
    }

    private static TeamCombatPlanner.Decision Decision(Board b)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            var status = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out var decision);
            if (status == KernelCombatPlanner.Status.Ready && decision is not null) return decision;
            Check(status != KernelCombatPlanner.Status.Fallback, "supported mixed root must provide a native tournament action");
            Thread.Sleep(1);
        }
        throw new InvalidOperationException("Mixed responsiveness: no Bot progress while human turn remains open");
    }
    private static object? Get(KernelCombatPlanner planner, string name) => AccessTools.Field(typeof(KernelCombatPlanner), name).GetValue(planner);
    private static void Set(KernelCombatPlanner planner, string name, object? value) => AccessTools.Field(typeof(KernelCombatPlanner), name).SetValue(planner, value);
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Mixed responsiveness: " + message); }
    private static bool Format(ref string __result) { __result = ""; return false; }
}
