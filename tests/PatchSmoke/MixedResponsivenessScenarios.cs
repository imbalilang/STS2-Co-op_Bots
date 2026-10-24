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

// The shipped Poll entry, real living human and queued native paid cards.
// V2 keeps the complete tournament in both human phases. Stable mixed scripts
// are proposals whose execution does not wait for optional quality work.
// Historical R2 receipts apply only to superseded policy. V2 R2 evidence is
// recorded by the serial validation owner after production mutations.
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
            Execute(Human, card, Enemy);
        }
        internal void BotCard(TeamCombatPlanner.Decision decision)
            => Execute(decision.Player, decision.Move.Card, decision.Move.Target);
        private void Execute(Player owner, CardModel card, Creature? target)
        {
            var action = new PlayCardAction(owner, NetCombatCard.ForTesting(0), card.Id, target?.CombatId);
            if (owner == Bot) Planner.ConfirmActionQueued(action);
            inputAction = action; inputCard = card; inputTarget = target;
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
        _ = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out _);
        var before = Get(b.Planner, "tournamentRequest") as KernelDecisionRequest;
        Check(before is null || before.BudgetMs >= 6400,
            "a living human must not replace the complete tournament with the old 400ms request");
        for (var i = 0; i < 3; i++)
        {
            b.HumanCard();
            // A root-search result may be retired, but a human action cannot
            // consume a prepared foreground action or require human EndTurn.
            b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null, false, BotDifficulty.Pro, out _);
        }
        var decision = Decision(b);
        Check(decision.Player == b.Bot && decision.Move.Card.Owner == b.Bot
            && b.Human.PlayerCombatState!.Energy > 0 && b.Human.PlayerCombatState.Phase == PlayerTurnPhase.Play
            && KernelInputObserver.Revision == 3,
            "Bot must produce a native current-root card while the human still has energy and has never ended turn");
        b.BotCard(decision);
        Check(b.Bot.PlayerCombatState!.Energy == 5 && b.Enemy.CurrentHp < 182,
            "the interleaved Bot card must actually execute through the paid native body");
        Console.WriteLine("PASS: complete mixed tournament produces a native Bot card while the human turn is still open.");
    }

    private static void RenewedProposalProvidesAction()
    {
        using var b = new Board();
        var initial = Decision(b);
        b.BotCard(initial);
        var passes = (int)Get(b.Planner, "tournamentPasses")!;
        b.HumanCard();
        var next = Decision(b);
        Check((int)Get(b.Planner, "tournamentPasses")! == passes,
            "a legal old script must continue without a foreground tournament after a settled human card");
        Check(next.Player == b.Bot && b.Human.PlayerCombatState!.Energy > 0,
            "stable proposal action belongs only to a controlled seat before human end-turn");
        Console.WriteLine("PASS: mixed stable script supplies the next controlled Bot action without a fresh foreground tournament.");
    }

    private static void CurrentSearchIsVisible()
    {
        using var b = new Board();
        var begin = AccessTools.Method(typeof(KernelCombatPlanner), "BeginTournamentPhase");
        Check((bool)begin.Invoke(b.Planner, [b.Combat, new[] { b.Bot }, false])!, "production phase must start");
        var before = (TournamentOptions)Get(b.Planner, "tournamentOptions")!;
        var request = (KernelDecisionRequest)Get(b.Planner, "tournamentRequest")!;
        var other = new KernelCombatPlanner();
        try
        {
            Check((bool)begin.Invoke(other, [b.Combat, new[] { b.Bot }, true])!, "post-human production phase must start");
            var after = (TournamentOptions)Get(other, "tournamentOptions")!;
            var afterRequest = (KernelDecisionRequest)Get(other, "tournamentRequest")!;
            Check(b.Planner.IsSearching && request.BudgetMs >= 6400
                && before.UsePolicyPortfolio && before.EnableJointSearch && before.TopK >= 20
                && before.Rollout is { MaxRounds: >= 80, MaxSteps: >= 600 }
                && before.TopK == after.TopK && before.Rollout == after.Rollout
                && before.UsePolicyPortfolio == after.UsePolicyPortfolio
                && before.EnableJointSearch == after.EnableJointSearch
                && before.MaxDegreeOfParallelism == after.MaxDegreeOfParallelism
                && request.BudgetMs == afterRequest.BudgetMs && request.MaxConcurrency == afterRequest.MaxConcurrency,
                "human EndTurn may change end permission but must preserve complete strategies, joint search, horizon and request budget");
            Console.WriteLine("PASS: mixed tournament keeps the same complete search capabilities and budget before and after human EndTurn.");
        }
        finally { other.Reset(newCombat: true); }
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
            && Get(b.Planner, "tournamentScript") is not null,
            "returning control retires delegated quality proposals while preserving unaffected controlled card proposals");
        var next = Decision(b);
        Check(next.Player == b.Bot && next.Move.Card.Owner == b.Bot && b.Human.PlayerCombatState.Hand.Cards.Count == 6,
            "fresh production Poll after handback must leave every human card under human control");
        Console.WriteLine("PASS: mixed control handback retires delegated proposals and only the remaining Bot acts.");
    }

    private static TeamCombatPlanner.Decision Decision(Board b)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
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
