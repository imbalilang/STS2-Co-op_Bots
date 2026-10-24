// V2 R2 actual 2026-10-24: production build0 -> named runtime RED1 -> exact source restore -> build0/GREEN0.
// Private receipts: work/v2-validation/r2/{receipts,second-pass/receipts,final-pass/receipts}.json.
// These are fault controls, not distinct gameplay bug or live-performance counts.
// cursor-only-after-finished: System.InvalidOperationException: Mixed stable script: only the replacement real action receipt may advance to the second physical card
// cancel-does-not-consume: System.InvalidOperationException: Mixed stable script: cancelling before settlement releases the reservation without consuming the card
// recost-same-physical: System.InvalidOperationException: Mixed stable script: a changed state key may not reject the same legal physical card in a mixed historical proposal
// physical-identity: System.InvalidOperationException: Mixed stable script: a different new same-name card cannot impersonate a missing historical physical card
// mixed-no-whole-enemy-gate: System.InvalidOperationException: Mixed stable script: a human kill outside this card's target cannot turn old whole-enemy predictions into an execution gate
// first-action-unconsumed: System.InvalidOperationException: Mixed stable script: a fresh first EndTurn must park once at cursor zero while humans act, without installation or consumption loops
// completed-result-retired: System.InvalidOperationException: Mixed stable script: a completed result must retire its readable run after single installation, with no replacement search while parked
// expired-script-stays-executable: System.InvalidOperationException: Mixed stable script: an expired computation clock cannot expire a legal paced execution plan or create a Pending loop
// expired-card-one-consumption: System.InvalidOperationException: Mixed stable script: a completed card candidate must remain one prepared action after its request expires, without fallback or repeated adoption
// expired-baseline-no-potion-work: System.InvalidOperationException: Mixed stable script: an expired no-potion result must be deployed directly instead of starting bottle replay, another phase or legacy fallback
// no-candidate-no-legacy: System.InvalidOperationException: Mixed stable script: a mixed deadline with no completed candidate must park its point once, without legacy, worker polling or a renewed allowance
// control-native-receipt: System.InvalidOperationException: Mixed stable script: the rebound Bot action's real native AfterFinished must consume one step of the new array and leave human cards untouched
// V2 R2 actual 2026-10-24: production mutant build0 / named RED1 /
// exact source restore / rebuild0 / named GREEN0. Receipt:
// work/v2-validation/r2/receipts.json (only passed=true records below).
// no-four-action-renewal is independently exercised by the production
// mixed-background stable-script case, not counted as an extra local case.
// Missing completed-result-retired / expired-script-stays-executable
// entries are not mutation proofs and are not claimed here.
// cursor-only-after-finished:
// System.InvalidOperationException: Mixed stable script: only the replacement real action receipt may advance to the second physical card
// cancel-does-not-consume:
// System.InvalidOperationException: Mixed stable script: cancelling before settlement releases the reservation without consuming the card
// recost-same-physical:
// System.InvalidOperationException: Mixed stable script: a changed state key may not reject the same legal physical card in a mixed historical proposal
// physical-identity:
// System.InvalidOperationException: Mixed stable script: a different new same-name card cannot impersonate a missing historical physical card
// mixed-no-whole-enemy-gate:
// System.InvalidOperationException: Mixed stable script: a human kill outside this card's target cannot turn old whole-enemy predictions into an execution gate
// first-action-unconsumed:
// System.InvalidOperationException: Mixed stable script: a fresh first EndTurn must park once at cursor zero while humans act, without installation or consumption loops
// no-four-action-renewal:
// System.InvalidOperationException: Mixed background: four successful Bot steps must not expire a still-executable mixed continuation
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
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// These are multi-frame production Poll/reservation/queue/AfterFinished cases.
// Search-result injection in fresh-end is deliberately limited to supplying an
// already completed native-root candidate; adoption and all later Poll calls
// are unmodified production behavior. No sleep-based pacing claims are made.
// V2 production fault injection RED/GREEN is recorded by the validation owner.
internal static class MixedStableScriptScenarios
{
    private static GameAction? executing;
    private static CardModel? executingCard;
    private static Creature? executingTarget;
    private static int observedPotionReplays;
    private static void ObservePotionReplay() => observedPotionReplays++;
    private static bool ActionBody(PlayCardAction __instance, ref Task __result)
    {
        if (!ReferenceEquals(executing, __instance)) return true;
        var card = executingCard!;
        var paid = card.SpendResources().GetAwaiter().GetResult();
        card.OnPlayWrapper(new ThrowingPlayerChoiceContext(), executingTarget, false,
            new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
        __result = Task.CompletedTask; return false;
    }
    private static bool EndBody(EndPlayerTurnAction __instance, ref Task __result)
    {
        if (!ReferenceEquals(executing, __instance)) return true;
        __result = Task.CompletedTask; return false;
    }
    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null;
        var bridge = new Harmony("coopbots.mixed-stable-script.native-body");
        bridge.Patch(AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"),
            prefix: new HarmonyMethod(typeof(MixedStableScriptScenarios), nameof(ActionBody)));
        bridge.Patch(AccessTools.Method(typeof(EndPlayerTurnAction), "ExecuteAction"),
            prefix: new HarmonyMethod(typeof(MixedStableScriptScenarios), nameof(EndBody)));
        bridge.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedStableScriptScenarios), nameof(Format)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("stable-prefix", StablePrefix), ("reservation", ReservationAndCancellation),
                ("recost", SamePhysicalRecost), ("identity", MissingPhysicalCard),
                ("unrelated-enemy", UnrelatedEnemyChange), ("allbot-strict", AllBotStateKeyStaysStrict),
                ("expired-script", ExpiredSearchKeepsScript),
                ("fresh-end", FreshEndSingleConsumption),
                ("expired-card", ExpiredCardSingleConsumption),
                ("expired-potion-baseline", ExpiredPotionBaseline),
                ("no-candidate", NoCandidateStopsOnce),
                ("control-rebind", ControlRebindNativeReceipt) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: mixed stable script " + test.Name); }
        }
        finally { bridge.UnpatchAll(bridge.Id); KernelCombatPlanner.TournamentOverride = previous; }
    }

    private sealed class Board : IDisposable
    {
        internal readonly Player Bot, Human;
        internal readonly CombatState Combat;
        internal readonly Creature Enemy;
        internal readonly KernelCombatPlanner Planner = new();
        internal readonly ActionQueueSet Queue;
        private readonly IDisposable native;
        internal Board(int cards = 6)
        {
            Bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 86, 4));
            Human = Player.CreateForNewRun<Deprived>(UnlockState.all, 18641);
            Combat = new CombatState(runState: RunState.CreateForTest([Bot, Human], seed: "MIXED-STABLE-V2")) { RoundNumber = 1 };
            foreach (var p in new[] { Bot, Human })
            {
                p.MaxEnergy = 10; p.ResetCombatState(); Combat.AddPlayer(p);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(10);
                for (var i = 0; i < cards; i++) p.PlayerCombatState.Hand.AddInternal(Combat.CreateCard<StrikeIronclad>(p));
            }
            Enemy = AddEnemy("main");
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [Combat], null)!;
            Queue = new ActionQueueSet([Bot, Human]); Queue.CombatStarted();
            AutoPilot.Set(Human.NetId, false); KernelInputObserver.Observe(Queue, Combat);
        }
        internal Creature AddEnemy(string label)
        {
            var enemy = Combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, label);
            Combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
            return enemy;
        }
        internal KernelTeamSearch.Action[] Seed()
        {
            var root = KernelCombatCapture.Capture(Combat);
            var script = Bot.PlayerCombatState!.Hand.Cards.Select(c =>
                new KernelTeamSearch.Action(Bot, c, Enemy, CardStateKey: root.CardStateKey(c))).ToArray();
            var old = TerminalRecord.Capture(root, Combat.Players, false, "historical-root")
                with { Victory = true, Kind = EvidenceKind.VerifiedTerminal };
            Set(Planner, "tournamentScript", script); Set(Planner, "tournamentScriptIndex", 0);
            Set(Planner, "tournamentScriptCombat", Combat); Set(Planner, "tournamentScriptRound", Combat.RoundNumber);
            Set(Planner, "victoryCheckedRound", Combat.RoundNumber); Set(Planner, "tournamentScriptRecord", old);
            Set(Planner, "tournamentScriptAliveStates", script.Select(_ => Combat.Players.Select(p => p.NetId).Order().ToArray()).ToArray());
            Set(Planner, "tournamentScriptEnemyStates", script.Select(_ => Combat.HittableEnemies.ToArray()).ToArray());
            Invoke(Planner, "CaptureMixedContinuation", root, Combat, script, 0);
            return script;
        }
        internal KernelCombatPlanner.Status Poll(out TeamCombatPlanner.Decision? decision, bool humansFinished = false)
            => Planner.Poll(Combat, [Bot], Queue.NextActionId, null, humansFinished, BotDifficulty.Pro, out decision);
        internal PlayCardAction Action(TeamCombatPlanner.Decision d)
            => new(d.Player, NetCombatCard.ForTesting(0), d.Move.Card.Id, d.Move.Target?.CombatId);
        internal void Execute(PlayCardAction action, CardModel card, Creature? target)
        {
            executing = action; executingCard = card; executingTarget = target;
            try { Queue.EnqueueWithoutSynchronizing(action); action.Execute().GetAwaiter().GetResult(); }
            finally { executing = null; executingCard = null; executingTarget = null; }
        }
        internal void Commit(TeamCombatPlanner.Decision d)
        {
            var action = Action(d); Planner.ConfirmActionQueued(action); Execute(action, d.Move.Card, d.Move.Target);
        }
        internal void HumanCard()
        {
            var card = Human.PlayerCombatState!.Hand.Cards[0];
            Execute(new PlayCardAction(Human, NetCombatCard.ForTesting(0), card.Id, Enemy.CombatId), card, Enemy);
            // Independent planner instance does not own Runtime's global observer.
            // The action above still proves the real AfterFinished revision path.
            Planner.NoteMixedInput();
        }
        public void Dispose()
        {
            KernelInputObserver.Observe(Queue, null); Planner.Reset(newCombat: true);
            AutoPilot.Set(Human.NetId, false); native.Dispose();
        }
    }

    private static void StablePrefix()
    {
        using var b = new Board(); var script = b.Seed();
        for (var i = 0; i < script.Length; i++)
        {
            if (i is 1 or 3) b.HumanCard();
            Check(b.Poll(out var decision) == KernelCombatPlanner.Status.Ready
                && ReferenceEquals(decision?.Move.Card, script[i].Card),
                "settled human damage and the fourth-card boundary cannot interrupt a legal old suffix");
            Check((int)Get(b.Planner, "tournamentScriptIndex")! == i && b.Planner.HasPreparedSubmission,
                "preparing the next step cannot consume its cursor before a real queue receipt");
            for (var tick = 0; tick < 8; tick++)
            {
                var again = b.Poll(out var held);
                Check(again == KernelCombatPlanner.Status.Ready && ReferenceEquals(held?.Move.Card, script[i].Card)
                    && (int)Get(b.Planner, "tournamentScriptIndex")! == i,
                    "polling a paced card across frames must return the same prepared step without skipping cards");
            }
            b.Commit(decision!);
            Check((int)Get(b.Planner, "tournamentScriptIndex")! == i + 1 && !b.Planner.HasPreparedSubmission,
                "the real native action AfterFinished must advance exactly the one reserved step");
            Check((int)Get(b.Planner, "tournamentPasses")! == 0,
                "normal committed Bot cards and settled human cards must not start foreground tournaments");
        }
        Check(b.Bot.PlayerCombatState!.Hand.Cards.Count == 0 && b.Bot.PlayerCombatState.Energy == 4
            && b.Enemy.CurrentHp == 152 && KernelInputObserver.Revision == 2,
            "all six physical Bot cards and both human cards must actually pay and land through native action bodies");
    }

    private static void ReservationAndCancellation()
    {
        using var b = new Board(2); var script = b.Seed();
        Check(b.Poll(out var first) == KernelCombatPlanner.Status.Ready, "a live proposal must prepare");
        var action = b.Action(first!); b.Planner.ConfirmActionQueued(action);
        for (var i = 0; i < 5; i++)
        {
            Check(b.Poll(out var ignored) == KernelCombatPlanner.Status.Pending && ignored is null
                && (int)Get(b.Planner, "tournamentScriptIndex")! == 0,
                "an enqueued but unresolved reservation must not emit or consume a second action");
        }
        var cancelled = (Action<GameAction>?)AccessTools.Field(typeof(GameAction), "BeforeCancelled").GetValue(action);
        Check(cancelled is not null, "the real action cancellation event must be subscribed");
        cancelled!(action);
        Check((int)Get(b.Planner, "tournamentScriptIndex")! == 0 && !b.Planner.HasPreparedSubmission,
            "cancelling before settlement releases the reservation without consuming the card");
        Check(b.Poll(out var retry) == KernelCombatPlanner.Status.Ready && ReferenceEquals(retry?.Move.Card, script[0].Card),
            "the cancelled physical card must remain the next proposal");
        b.Commit(retry!);
        Check((int)Get(b.Planner, "tournamentScriptIndex")! == 1
            && b.Poll(out var second) == KernelCombatPlanner.Status.Ready && ReferenceEquals(second?.Move.Card, script[1].Card),
            "only the replacement real action receipt may advance to the second physical card");
    }

    private static void SamePhysicalRecost()
    {
        using var b = new Board(2); var script = b.Seed(); var card = script[0].Card!;
        card.EnergyCost.AddThisTurn(-1); b.Planner.NoteMixedInput();
        Check(b.Poll(out var decision) == KernelCombatPlanner.Status.Ready && ReferenceEquals(decision?.Move.Card, card),
            "a changed state key may not reject the same legal physical card in a mixed historical proposal");
        var energy = b.Bot.PlayerCombatState!.Energy; b.Commit(decision!);
        Check(b.Bot.PlayerCombatState.Energy == energy && !b.Bot.PlayerCombatState.Hand.Cards.Contains(card),
            "the retained physical card must execute at its actual current zero cost");
        Check(Get(b.Planner, "tournamentScriptRecord") is TerminalRecord { Victory: false, Verified: false },
            "a legal recosted proposal must not claim its historical victory was reverified");
    }

    private static void MissingPhysicalCard()
    {
        using var b = new Board(1); var script = b.Seed();
        var card = script[0].Card!; b.Bot.PlayerCombatState!.Hand.RemoveInternal(card);
        var alias = b.Combat.CreateCard<StrikeIronclad>(b.Bot); b.Bot.PlayerCombatState.Hand.AddInternal(alias);
        b.Planner.NoteMixedInput();
        var stopSearch = new Harmony("coopbots.mixed-stable-script.no-alias-search");
        var begin = AccessTools.Method(typeof(KernelCombatPlanner), "BeginTournamentPhase");
        stopSearch.Patch(begin, prefix: new HarmonyMethod(typeof(MixedStableScriptScenarios), nameof(RejectSearch)));
        try
        {
            b.Poll(out var decision);
            Check(decision is null && !b.Planner.HasPreparedSubmission && Get(b.Planner, "tournamentScript") is null,
                "a different new same-name card cannot impersonate a missing historical physical card");
        }
        finally { stopSearch.UnpatchAll(stopSearch.Id); }
    }

    private static void UnrelatedEnemyChange()
    {
        using var b = new Board(2); var unrelated = b.AddEnemy("unrelated"); var script = b.Seed();
        unrelated.SetCurrentHpInternal(0); b.Planner.NoteMixedInput();
        Check(b.Poll(out var decision) == KernelCombatPlanner.Status.Ready
            && ReferenceEquals(decision?.Move.Card, script[0].Card) && decision.Move.Target == b.Enemy,
            "a human kill outside this card's target cannot turn old whole-enemy predictions into an execution gate");
        b.Commit(decision!);
        Check(b.Enemy.CurrentHp == 194 && (int)Get(b.Planner, "tournamentPasses")! == 0,
            "the unaffected target's real native card must land without a foreground search");
    }

    private static void AllBotStateKeyStaysStrict()
    {
        // One card removes the valid same-state duplicate fallback from this
        // strict-state-key control; two identical Strikes can legally resolve
        // occurrence zero to the still-unmodified second Strike.
        using var b = new Board(1); AutoPilot.Set(b.Human.NetId, true);
        var script = b.Seed(); script[0].Card!.EnergyCost.AddThisTurn(-1);
        var stopSearch = new Harmony("coopbots.mixed-stable-script.allbot-state-key");
        stopSearch.Patch(AccessTools.Method(typeof(KernelCombatPlanner), "BeginTournamentPhase"),
            prefix: new HarmonyMethod(typeof(MixedStableScriptScenarios), nameof(RejectSearch)));
        try
        {
            b.Poll(out var decision, humansFinished: true);
            Check(decision is null && Get(b.Planner, "tournamentScript") is null,
                "the mixed historical-card exception cannot loosen all-Bot verified CardStateKey execution");
        }
        finally { stopSearch.UnpatchAll(stopSearch.Id); }
    }

    private static void ExpiredSearchKeepsScript()
    {
        using var b = new Board(2); var script = b.Seed();
        var expired = new KernelDecisionRequest(18642, 0);
        Set(b.Planner, "tournamentRequest", expired);
        Set(b.Planner, "tournamentPoint", KernelCombatPlanner.TournamentDecisionPoint(b.Combat, b.Queue.NextActionId) + "/focus:");
        for (var tick = 0; tick < 20; tick++)
            Check(b.Poll(out var decision) == KernelCombatPlanner.Status.Ready
                && ReferenceEquals(decision?.Move.Card, script[0].Card)
                && (int)Get(b.Planner, "tournamentScriptIndex")! == 0,
                "an expired computation clock cannot expire a legal paced execution plan or create a Pending loop");
        Check((int)Get(b.Planner, "tournamentPasses")! == 0,
            "waiting across an expired search cannot grant another full tournament budget");
    }

    private static void FreshEndSingleConsumption()
    {
        using var b = new Board(1);
        var root = KernelCombatCapture.Capture(b.Combat);
        var ending = TurnStartFixtureCapture.Record(root, [new KernelTeamSearch.Action(b.Bot, null, null, EndTurn: true)])[0];
        // The result completed before its request deadline. Expiration may
        // stop new work, but may not erase this already complete candidate.
        var request = new KernelDecisionRequest(18643, 0);
        var result = new TournamentResult(ending,
            TerminalRecord.Capture(root, b.Combat.Players, false, "completed-native-root"), [], 1, 0, "fixture-complete") {
            Script = [ending], ScriptAliveStates = [b.Combat.Players.Select(p => p.NetId).Order().ToArray()],
            ScriptEnemyStates = [new[] { b.Enemy }], RootGeneration = request.RootGeneration };
        AccessTools.Property(typeof(TournamentResult), "RequestProvenance").SetValue(result, request);
        using var completed = new TournamentRun(root, b.Combat.Players.ToArray(), [b.Bot],
            new TournamentOptions(IncludePotions: false, IncludeEndTurns: true, ReserveMs: 0) { Request = request }, 10000);
        AccessTools.Property(typeof(TournamentRun), nameof(TournamentRun.Result)).SetValue(completed, result);
        Set(b.Planner, "tournamentRequest", request); Set(b.Planner, "tournamentRun", completed);
        Set(b.Planner, "tournamentRoot", root); Set(b.Planner, "tournamentParty", b.Combat.Players.ToArray());
        Set(b.Planner, "tournamentActors", new[] { b.Bot });
        Set(b.Planner, "tournamentOptions", new TournamentOptions { Request = request });
        var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
        phase.SetValue(b.Planner, Enum.Parse(phase.FieldType, "NoPotions"));
        Set(b.Planner, "tournamentOpeningReviewed", true);
        Set(b.Planner, "tournamentPoint", KernelCombatPlanner.TournamentDecisionPoint(b.Combat, b.Queue.NextActionId) + "/focus:");
        for (var tick = 0; tick < 40; tick++)
        {
            Check(b.Poll(out var decision) == KernelCombatPlanner.Status.Pending && decision is null
                && b.Planner.ConfirmedEndTurn is null && (int)Get(b.Planner, "tournamentScriptIndex")! == 0,
                "a fresh first EndTurn must park once at cursor zero while humans act, without installation or consumption loops");
        }
        Check(Get(b.Planner, "tournamentRun") is null && Get(b.Planner, "tournamentScript") is not null
            && (int)Get(b.Planner, "tournamentPasses")! == 0,
            "a completed result must retire its readable run after single installation, with no replacement search while parked");
        Check(b.Poll(out _, humansFinished: true) == KernelCombatPlanner.Status.Ready
            && b.Planner.ConfirmedEndTurn == b.Bot && b.Planner.HasPreparedSubmission
            && (int)Get(b.Planner, "tournamentScriptIndex")! == 0,
            "human completion must immediately release the stored first EndTurn without fresh quality replay or a new budget");
        var end = new EndPlayerTurnAction(b.Bot, 1); b.Planner.ConfirmActionQueued(end); executing = end;
        try { b.Queue.EnqueueWithoutSynchronizing(end); end.Execute().GetAwaiter().GetResult(); }
        finally { executing = null; }
        Check((int)Get(b.Planner, "tournamentScriptIndex")! == 1 && !b.Planner.HasPreparedSubmission
            && Get(b.Planner, "tournamentRun") is null,
            "one native EndTurn AfterFinished consumes the first step once and cannot revive the old completed run");
    }

    private static (KernelSession Root, TournamentResult Result) CompletedCards(Board b)
    {
        var root = KernelCombatCapture.Capture(b.Combat);
        var actions = b.Bot.PlayerCombatState!.Hand.Cards.Select(card =>
            new KernelTeamSearch.Action(b.Bot, card, b.Enemy, CardStateKey: root.CardStateKey(card))).ToArray();
        var replay = new KernelScriptReplayRun(root, b.Combat.Players.ToArray(),
            new TournamentResult(actions[0], null, [], 0, 0, "native-completed-fixture") { Script = actions }, 80);
        while (!replay.Done) replay.Advance(TimeSpan.FromMilliseconds(1));
        Check(replay.Result?.Record is not null && replay.Result.Script?.Count == actions.Length,
            "the completed-card fixture must really settle every native paid card against its own captured root");
        return (root, replay.Result!);
    }

    // Only completed search input is injected. All installation, receipt and
    // subsequent Poll paths remain production code. Ownership is explicitly
    // tied to the same request, as in fresh-end; this is a lifecycle fixture,
    // not a claim that a zero-budget request performed these earlier actions.
    private static TournamentRun InjectCompleted(Board b, KernelSession root,
        TournamentResult result, KernelDecisionRequest request)
    {
        result = result with { RootGeneration = request.RootGeneration };
        AccessTools.Property(typeof(TournamentResult), "RequestProvenance").SetValue(result, request);
        var options = new TournamentOptions(IncludePotions: false, IncludeEndTurns: false, ReserveMs: 0) { Request = request };
        var completed = new TournamentRun(root, b.Combat.Players.ToArray(), [b.Bot], options, 10000);
        AccessTools.Property(typeof(TournamentRun), nameof(TournamentRun.Result)).SetValue(completed, result);
        Set(b.Planner, "tournamentRequest", request); Set(b.Planner, "tournamentRun", completed);
        Set(b.Planner, "tournamentRoot", root); Set(b.Planner, "tournamentParty", b.Combat.Players.ToArray());
        Set(b.Planner, "tournamentActors", new[] { b.Bot }); Set(b.Planner, "tournamentOptions", options);
        var phase = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPhase");
        phase.SetValue(b.Planner, Enum.Parse(phase.FieldType, "NoPotions"));
        Set(b.Planner, "tournamentOpeningReviewed", true);
        Set(b.Planner, "tournamentPoint", KernelCombatPlanner.TournamentDecisionPoint(b.Combat, b.Queue.NextActionId) + "/focus:");
        return completed;
    }

    private static void ExpiredCardSingleConsumption()
    {
        using var b = new Board(2);
        var (root, result) = CompletedCards(b);
        var first = result.Script![0].Card; var second = result.Script[1].Card;
        var expired = new KernelDecisionRequest(18644, 0);
        using var run = InjectCompleted(b, root, result, expired);
        TeamCombatPlanner.Decision? prepared = null;
        for (var tick = 0; tick < 20; tick++)
        {
            Check(b.Poll(out prepared) == KernelCombatPlanner.Status.Ready
                && ReferenceEquals(prepared?.Move.Card, first)
                && (int)Get(b.Planner, "tournamentScriptIndex")! == 0
                && Get(b.Planner, "tournamentRun") is null && Get(b.Planner, "tournamentRequest") is null,
                "a completed card candidate must remain one prepared action after its request expires, without fallback or repeated adoption");
        }
        b.Commit(prepared!);
        var afterFirst = b.Poll(out var next);
        Check((int)Get(b.Planner, "tournamentScriptIndex")! == 1
            && afterFirst == KernelCombatPlanner.Status.Ready && ReferenceEquals(next?.Move.Card, second),
            "the expired completed card's real receipt must advance to its second physical card exactly once");
        b.Commit(next!);
        Check((int)Get(b.Planner, "tournamentScriptIndex")! == 2
            && (int)Get(b.Planner, "tournamentPasses")! == 0 && b.Bot.PlayerCombatState!.Energy == 8
            && b.Enemy.CurrentHp == 188 && Get(b.Planner, "tournamentRun") is null,
            "both native expired-result cards must settle once without resurrecting the consumed completed run");
    }

    private static void ExpiredPotionBaseline()
    {
        using var b = new Board(2);
        var bottle = ModelDb.Potion<FirePotion>().ToMutable(); b.Bot.AddPotionInternal(bottle, silent: true);
        var (root, result) = CompletedCards(b);
        Check(KernelPotionPolicy.WorthComparing(root, [b.Bot], b.Combat.Players.ToArray(), result.Record),
            "the expired baseline fixture must offer a real modeled potion whose normal second pass is worth comparing");
        var expired = new KernelDecisionRequest(18645, 0);
        using var run = InjectCompleted(b, root, result, expired);
        var observer = new Harmony("coopbots.mixed-stable-script.expired-bottle-replay");
        observer.Patch(AccessTools.Method(typeof(KernelPotionContinuationRun), nameof(KernelPotionContinuationRun.Advance)),
            prefix: new HarmonyMethod(typeof(MixedStableScriptScenarios), nameof(ObservePotionReplay)));
        observedPotionReplays = 0;
        try
        {
            Check(b.Poll(out var prepared) == KernelCombatPlanner.Status.Ready
                && ReferenceEquals(prepared?.Move.Card, result.FirstAction!.Card)
                && b.Planner.ConfirmedPotion is null && observedPotionReplays == 0
                && Get(b.Planner, "tournamentPotionReplay") is null
                && (int)Get(b.Planner, "tournamentPasses")! == 0,
                "an expired no-potion result must be deployed directly instead of starting bottle replay, another phase or legacy fallback");
            b.Commit(prepared!);
            Check(b.Bot.Potions.Contains(bottle) && !bottle.IsQueued && b.Enemy.CurrentHp == 194
                && b.Bot.PlayerCombatState!.Energy == 9 && (int)Get(b.Planner, "tournamentScriptIndex")! == 1,
                "the retained baseline must pay its native card while keeping the un-compared bottle unspent");
        }
        finally { observer.UnpatchAll(observer.Id); }
    }

    private static void NoCandidateStopsOnce()
    {
        using var b = new Board(2);
        Invoke(b.Planner, "EnsureMixedBudget", b.Combat);
        var allowance = (int)Invoke(b.Planner, "MixedRemainingDecisionBudget", b.Combat)!;
        Set(b.Planner, "mixedEpisodeSpentMs", (double)allowance);
        var point = KernelCombatPlanner.TournamentDecisionPoint(b.Combat, b.Queue.NextActionId) + "/focus:";
        Set(b.Planner, "tournamentPoint", point);
        Set(b.Planner, "tournamentOpeningReviewed", true);
        Set(b.Planner, "tournamentRequest", new KernelDecisionRequest(18646, 0));
        var generation = Get(b.Planner, "tournamentRootGeneration");
        for (var tick = 0; tick < 20; tick++)
        {
            Check(b.Poll(out var none, humansFinished: tick >= 10) == KernelCombatPlanner.Status.Pending
                && none is null && !b.Planner.HasPreparedSubmission
                && Get(b.Planner, "tournamentRun") is null && Get(b.Planner, "tournamentRequest") is null
                && (int)Get(b.Planner, "tournamentPasses")! == 0
                && Equals(Get(b.Planner, "tournamentRootGeneration"), generation)
                && (string?)Get(b.Planner, "mixedStoppedPoint") == point
                && (int)Invoke(b.Planner, "MixedRemainingDecisionBudget", b.Combat)! == 0,
                "a mixed deadline with no completed candidate must park its point once, without legacy, worker polling or a renewed allowance");
        }
        b.HumanCard();
        Check(b.Poll(out var stillNone) == KernelCombatPlanner.Status.Pending && stillNone is null
            && Get(b.Planner, "tournamentRun") is null && (int)Get(b.Planner, "tournamentPasses")! == 0
            && (int)Invoke(b.Planner, "MixedRemainingDecisionBudget", b.Combat)! == 0,
            "a changed human root in the same exhausted round/control episode cannot grant a fresh search budget");
    }

    private static void ControlRebindNativeReceipt()
    {
        using var b = new Board(2);
        // Keep one other living human so this is a mixed-to-mixed handback,
        // independently of the all-Bot future-refinement mode switch.
        var witness = Player.CreateForNewRun<Deprived>(UnlockState.all, 18647);
        witness.ResetCombatState(); b.Combat.AddPlayer(witness);
        witness.Creature.SetMaxHpInternal(80); witness.Creature.SetCurrentHpInternal(80);
        witness.PlayerCombatState!.Phase = PlayerTurnPhase.Play; witness.PlayerCombatState.GainEnergy(3);
        AutoPilot.Set(b.Human.NetId, true);
        var original = b.Seed();
        var delegated = new KernelTeamSearch.Action(b.Human, b.Human.PlayerCombatState!.Hand.Cards[0], b.Enemy);
        var source = new[] { original[0], delegated, original[1] };
        Set(b.Planner, "tournamentScript", source);
        Invoke(b.Planner, "CaptureMixedContinuation", KernelCombatCapture.Capture(b.Combat), b.Combat, source, 0);
        Check(b.Poll(out var prepared) == KernelCombatPlanner.Status.Ready && prepared?.Player == b.Bot
            && ReferenceEquals(Get(b.Planner, "tournamentScript"), source),
            "the unaffected Bot card must first be prepared from the old control array through production Poll");
        AutoPilot.Set(b.Human.NetId, false);
        Invoke(b.Planner, "ReviewMixedInput", b.Combat);
        var retained = (IReadOnlyList<KernelTeamSearch.Action>)Get(b.Planner, "tournamentScript")!;
        Check(!ReferenceEquals(retained, source) && retained.Count == 2
            && ReferenceEquals(retained[0], source[0]) && ReferenceEquals(retained[1], source[2])
            && b.Planner.HasPreparedSubmission,
            "control filtering must replace the source array while preserving the exact unaffected Bot reservation");
        b.Commit(prepared!);
        Check((int)Get(b.Planner, "tournamentScriptIndex")! == 1 && !b.Planner.HasPreparedSubmission
            && b.Bot.PlayerCombatState!.Energy == 9 && b.Enemy.CurrentHp == 194
            && b.Human.PlayerCombatState.Hand.Cards.Count == 2,
            "the rebound Bot action's real native AfterFinished must consume one step of the new array and leave human cards untouched");
        Check(b.Poll(out var next) == KernelCombatPlanner.Status.Ready && ReferenceEquals(next?.Move.Card, original[1].Card),
            "after a rebound native receipt Poll must propose the next physical card rather than repeat the first card");
        b.Commit(next!);
        Check((int)Get(b.Planner, "tournamentScriptIndex")! == 2 && b.Bot.PlayerCombatState.Energy == 8 && b.Enemy.CurrentHp == 188,
            "two rebound-suffix cards must land exactly once after a control-domain array replacement");
    }

    private static bool RejectSearch(ref bool __result) { __result = false; return false; }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static object? Get(KernelCombatPlanner p, string name) => AccessTools.Field(typeof(KernelCombatPlanner), name).GetValue(p);
    private static void Set(KernelCombatPlanner p, string name, object? value) => AccessTools.Field(typeof(KernelCombatPlanner), name).SetValue(p, value);
    private static object? Invoke(KernelCombatPlanner p, string name, params object?[] args)
        => AccessTools.Method(typeof(KernelCombatPlanner), name).Invoke(p, args);
    private static void Check(bool okay, string reason)
    { if (!okay) throw new InvalidOperationException("Mixed stable script: " + reason); }
}
