// V2 R2 actual 2026-10-24: production build0 -> named runtime RED1 -> exact source restore -> build0/GREEN0.
// Private receipts: work/v2-validation/r2/{receipts,second-pass/receipts,final-pass/receipts}.json.
// These are fault controls, not distinct gameplay bug or live-performance counts.
// team-clock-no-plan-discount: System.InvalidOperationException: Mixed runtime pacing: pending card must stay reserved across nonidle searching ticks before its due time
// finished-removes-interval: System.InvalidOperationException: Mixed runtime pacing: human EndTurn must release an already prepared card without remaining cadence or optional search wait
// slow-card-no-catchup: System.InvalidOperationException: Mixed runtime pacing: a late submission cannot create catch-up bursts
// invalid-does-not-mark: System.InvalidOperationException: Mixed runtime pacing: invalid proposals must not restart or discount the successful card clock
// flash-team-interval: System.InvalidOperationException: Mixed runtime pacing: a different Flash seat shares the same clock
// enqueue-needs-receipt: System.InvalidOperationException: Mixed runtime pacing: silently rejected enqueue was counted as success
using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// The production TickCombat path runs at native queue boundaries, using a
// virtual monotonic clock. Only network transport/presentation are replaced.
// Real physical cards pay and execute their native wrapper; queued actions fire
// native AfterFinished. Decisions are already prepared on purpose: search
// quality/Poll reservation are exercised by the separate planner regressions.
// Production mutation RED/GREEN receipts and raw failures are recorded by the
// root run after these scenarios compile; never infer R2 from a green baseline.
internal static class MixedRuntimePacingScenarios
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static Scope? active;
    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        foreach (var test in new (string Name, Action Body)[] {
            ("team", TeamCadence), ("finished", FinishedCleanup), ("delay", DelayedCard),
            ("invalid", InvalidProposal), ("pause", PauseAndBusy), ("flash", FlashCadence),
            ("enqueue", SuccessfulQueueClock), ("resume", ResumedCardClock) })
            if (selected is null || selected == test.Name)
            { test.Body(); Console.WriteLine("PASS: mixed runtime pacing " + test.Name); }
    }

    private sealed class Scope : IDisposable
    {
        internal readonly Player[] Bots;
        internal readonly Player Human;
        internal readonly CombatState Combat;
        internal readonly RunState State;
        internal readonly Creature Enemy;
        internal readonly KernelCombatPlanner Planner;
        internal readonly ActionQueueSet Queue;
        internal readonly ActionExecutor Executor;
        internal long Now = 1000;
        internal bool Finished, RejectEnqueue;
        internal BotDifficulty Difficulty = BotDifficulty.Pro;
        internal readonly List<(ulong Owner, long At)> Submitted = [];
        private readonly Harmony harmony = new("coopbots.mixed-runtime-pacing");
        private readonly IDisposable native;
        private readonly Dictionary<PropertyInfo, object?> managerSaved;
        private readonly Dictionary<FieldInfo, object?> runtimeSaved;
        private readonly Dictionary<FieldInfo, object?> plannerSaved;
        private readonly Dictionary<FieldInfo, object?> pacingSaved;
        private readonly Dictionary<ulong, (int Turn, int Actions)> turnSaved;
        private readonly bool paused;
        private readonly TournamentRun background;
        private readonly Dictionary<uint, CardModel> oldIdCards;
        private readonly Dictionary<CardModel, uint> oldCardIds;
        private readonly object? oldNextCardId;
        internal Scope()
        {
            Check(active is null, "runtime fixture cannot nest");
            Bots = Enumerable.Range(0, 3).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
                i == 2 ? 19602UL : BotRegistry.CreateId(BotDifficulty.Pro, 96, i))).ToArray();
            AutoPilot.Set(Bots[2].NetId, true);
            Human = Player.CreateForNewRun<Deprived>(UnlockState.all, 19600);
            State = RunState.CreateForTest([.. Bots, Human], seed: "MIXED-RUNTIME-CADENCE");
            Combat = new CombatState(runState: State) { RoundNumber = 1 };
            var db = NetCombatCardDb.Instance;
            oldIdCards = new((Dictionary<uint, CardModel>)AccessTools.Field(typeof(NetCombatCardDb), "_idToCard").GetValue(db)!);
            oldCardIds = new((Dictionary<CardModel, uint>)AccessTools.Field(typeof(NetCombatCardDb), "_cardToId").GetValue(db)!);
            oldNextCardId = AccessTools.Field(typeof(NetCombatCardDb), "_nextId").GetValue(db);
            foreach (var player in State.Players)
            {
                player.ResetCombatState(); Combat.AddPlayer(player);
                player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
                player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(6);
                for (var i = 0; i < 4; i++)
                {
                    var card = Combat.CreateCard<StrikeIronclad>(player);
                    player.PlayerCombatState.Hand.AddInternal(card);
                    AccessTools.Method(typeof(NetCombatCardDb), "IdCardForTesting").Invoke(db, [card]);
                }
            }
            Enemy = Combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "cadence-enemy");
            Combat.AddCreature(Enemy); Enemy.Monster!.SetUpForCombat(); Enemy.SetMaxHpInternal(500); Enemy.SetCurrentHpInternal(500);
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
                Instance, null, [Combat], null)!;
            var manager = RunManager.Instance;
            string[] names = ["NetService", "ActionQueueSet", "ActionExecutor", "ActionQueueSynchronizer"];
            managerSaved = names.Select(n => AccessTools.Property(typeof(RunManager), n)).ToDictionary(p => p, p => p.GetValue(manager));
            turnSaved = new((Dictionary<ulong, (int Turn, int Actions)>)AccessTools.Field(typeof(BotRuntime), "TurnState").GetValue(null)!);
            runtimeSaved = typeof(BotRuntime).GetFields(Static).Where(f => !f.IsInitOnly && !f.IsLiteral).ToDictionary(f => f, f => f.GetValue(null));
            Planner = (KernelCombatPlanner)AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
            plannerSaved = typeof(KernelCombatPlanner).GetFields(Instance).Where(f => !f.IsInitOnly && !f.IsLiteral).ToDictionary(f => f, f => f.GetValue(Planner));
            var pacing = AccessTools.Field(typeof(BotRuntime), "Pacing").GetValue(null)!;
            pacingSaved = pacing.GetType().GetFields(Instance).ToDictionary(f => f, f => f.GetValue(pacing));
            Planner.Reset(newCombat: true);
            pacing.GetType().GetMethod("Reset", Instance)!.Invoke(pacing, null);
            SetRuntime("_preparedCard", null); SetRuntime("_preparedNative", null);
            SetRuntime("_combatIdentity", Combat); BotRuntime.ClockOverride = () => Now;
            paused = BotCooperation.Gate.Paused; if (paused) BotCooperation.Gate.TogglePause();
            Queue = new ActionQueueSet(State.Players); Queue.SetUpForCombat(); Queue.CombatStarted();
            Executor = (ActionExecutor)RuntimeHelpers.GetUninitializedObject(typeof(ActionExecutor));
            Set(manager, "ActionQueueSet", Queue); Set(manager, "ActionExecutor", Executor);
            Set(manager, "ActionQueueSynchronizer", RuntimeHelpers.GetUninitializedObject(typeof(ActionQueueSynchronizer)));
            Set(manager, "NetService", RuntimeHelpers.GetUninitializedObject(typeof(NetSingleplayerGameService)));
            harmony.Patch(AccessTools.Method(typeof(ActionQueueSynchronizer), "EnqueueAction", [typeof(GameAction), typeof(ulong)]),
                prefix: new HarmonyMethod(typeof(MixedRuntimePacingScenarios), nameof(Enqueue)));
            harmony.Patch(AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"),
                prefix: new HarmonyMethod(typeof(MixedRuntimePacingScenarios), nameof(CardBody)));
            harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
                prefix: new HarmonyMethod(typeof(MixedRuntimePacingScenarios), nameof(Format)));
            // A real unadvanced native tournament supplies IsSearching=true.
            // Its result is optional; Runtime must still submit prepared cards.
            background = new TournamentRun(KernelCombatCapture.Capture(Combat), State.Players, Bots,
                new TournamentOptions(IncludePotions: false, IncludeEndTurns: false), 6400);
            AccessTools.Field(typeof(KernelCombatPlanner), "tournamentRun").SetValue(Planner, background);
            AutoPilot.Set(Human.NetId, false); active = this;
        }
        internal object Prepare(int seat)
        {
            var player = Bots[seat]; var card = player.PlayerCombatState!.Hand.Cards.First();
            var decision = new TeamCombatPlanner.Decision(player,
                new BotBrain.CombatMove(card, Enemy, 10, "runtime-cadence-fixture"), 1);
            var type = AccessTools.Inner(typeof(BotRuntime), "PendingCardSubmission");
            var pending = Activator.CreateInstance(type, Instance, null,
                [Combat, decision, KernelCombatPlanner.Status.Ready, "剧本", Now], null)!;
            SetRuntime("_preparedCard", pending); return pending;
        }
        internal object? Pending => AccessTools.Field(typeof(BotRuntime), "_preparedCard").GetValue(null);
        internal void Tick(long at, bool? finished = null)
        { Now = at; if (finished.HasValue) Finished = finished.Value; BotRuntime.TickCombat(RunManager.Instance, State, Combat, Finished, Difficulty); }
        internal void Running(GameAction? action) => Set(Executor, "CurrentlyRunningAction", action);
        public void Dispose()
        {
            active = null; harmony.UnpatchAll(harmony.Id); background.Dispose();
            Planner.Reset(newCombat: true);
            foreach (var pair in plannerSaved) pair.Key.SetValue(Planner, pair.Value);
            var pacing = AccessTools.Field(typeof(BotRuntime), "Pacing").GetValue(null)!;
            foreach (var pair in pacingSaved) pair.Key.SetValue(pacing, pair.Value);
            foreach (var pair in runtimeSaved) pair.Key.SetValue(null, pair.Value);
            var turns = (Dictionary<ulong, (int Turn, int Actions)>)AccessTools.Field(typeof(BotRuntime), "TurnState").GetValue(null)!;
            turns.Clear(); foreach (var pair in turnSaved) turns.Add(pair.Key, pair.Value);
            foreach (var pair in managerSaved) pair.Key.SetValue(RunManager.Instance, pair.Value);
            if (BotCooperation.Gate.Paused != paused) BotCooperation.Gate.TogglePause();
            AutoPilot.Set(Human.NetId, false); AutoPilot.Set(Bots[2].NetId, false); native.Dispose();
            var db = NetCombatCardDb.Instance;
            var idCards = (Dictionary<uint, CardModel>)AccessTools.Field(typeof(NetCombatCardDb), "_idToCard").GetValue(db)!;
            var cardIds = (Dictionary<CardModel, uint>)AccessTools.Field(typeof(NetCombatCardDb), "_cardToId").GetValue(db)!;
            idCards.Clear(); foreach (var pair in oldIdCards) idCards.Add(pair.Key, pair.Value);
            cardIds.Clear(); foreach (var pair in oldCardIds) cardIds.Add(pair.Key, pair.Value);
            AccessTools.Field(typeof(NetCombatCardDb), "_nextId").SetValue(db, oldNextCardId);
        }
    }

    private static void TeamCadence()
    {
        using var s = new Scope(); var pending = s.Prepare(0);
        s.Tick(1000); s.Tick(1800); s.Tick(2499);
        Check(s.Submitted.Count == 0 && ReferenceEquals(s.Pending, pending) && s.Planner.IsSearching,
            "pending card must stay reserved across nonidle searching ticks before its due time");
        s.Tick(2500);
        Check(s.Submitted.Count == 1 && s.Submitted[0] == (s.Bots[0].NetId, 2500L) && s.Pending is null,
            "first prepared card must submit on the first due native Tick");
        var last = 2500L;
        foreach (var (seat, compute) in new[] { (1, 0), (2, 200), (0, 800) })
        {
            s.Now = last + compute; var stable = s.Prepare(seat);
            s.Tick(last + compute); s.Tick(last + 1499);
            Check(ReferenceEquals(s.Pending, stable), "planning/wait ticks cannot consume or replace the prepared card");
            var before = s.Submitted.Count; s.Tick(last + 1500);
            Check(s.Submitted.Count == before + 1 && s.Submitted[^1] == (s.Bots[seat].NetId, last + 1500),
                $"team cadence must stay 1500 ms with {compute} ms planning, across different Bot seats");
            last += 1500;
        }
        Check(s.Bots.Sum(p => p.PlayerCombatState!.Energy) == 14 && s.Enemy.CurrentHp == 476,
            "four scheduled cards must actually pay native energy and deal native damage");
    }
    private static void FinishedCleanup()
    {
        using var s = new Scope(); s.Prepare(0); s.Tick(1000); s.Tick(2500);
        s.Prepare(1); s.Tick(2600); Check(s.Submitted.Count == 1, "second card waits before human EndTurn");
        s.Tick(2601, true); Check(s.Submitted.Count == 2 && s.Submitted[^1].At == 2601,
            "human EndTurn must release an already prepared card without remaining cadence or optional search wait");
        s.Prepare(2); s.Tick(2602); Check(s.Submitted.Count == 3 && s.Submitted[^1].At == 2602,
            "cleanup must continue on consecutive stable ticks even with an optional tournament still pending");
    }
    private static void DelayedCard()
    {
        using var s = new Scope(); s.Prepare(0); s.Tick(1000); s.Tick(2500);
        s.Now = 6000; s.Prepare(1); s.Tick(6000);
        Check(s.Submitted.Count == 2 && s.Submitted[^1].At == 6000, "late preparation should submit immediately once stable");
        s.Prepare(2); s.Tick(6001); s.Tick(7499);
        Check(s.Submitted.Count == 2, "a late submission cannot create catch-up bursts");
        s.Tick(7500); Check(s.Submitted.Count == 3, "late submission restarts its own full team interval");
    }
    private static void InvalidProposal()
    {
        using var s = new Scope(); s.Prepare(0); s.Tick(1000);
        var card = s.Bots[0].PlayerCombatState!.Hand.Cards[0];
        s.Bots[0].PlayerCombatState!.Hand.RemoveInternal(card);
        s.Bots[0].PlayerCombatState!.DiscardPile.AddInternal(card); s.Tick(2000);
        Check(s.Submitted.Count == 0 && s.Pending is null, "a departed physical card must retire its slot without submission");
        s.Prepare(1); s.Tick(2500);
        Check(s.Submitted.Count == 1 && s.Submitted[0].At == 2500,
            "invalid proposals must not restart or discount the successful card clock");
        s.Prepare(2); AutoPilot.Set(s.Bots[2].NetId, false);
        try { s.Tick(4000); Check(s.Submitted.Count == 1 && s.Pending is null, "a handed-back seat cannot submit its reserved card"); }
        finally { AutoPilot.Set(s.Bots[2].NetId, true); }
    }
    private static void PauseAndBusy()
    {
        using var s = new Scope(); var prepared = s.Prepare(0); s.Tick(1000);
        BotCooperation.Gate.TogglePause(); s.Tick(2500);
        Check(s.Submitted.Count == 0 && ReferenceEquals(prepared, s.Pending), "pause must retain prepared work without submitting");
        BotCooperation.Gate.TogglePause();
        s.Running(new EndPlayerTurnAction(s.Human, 1)); s.Tick(4000);
        Check(s.Submitted.Count == 0 && ReferenceEquals(prepared, s.Pending), "busy native transaction must retain the prepared slot");
        s.Running(null); s.Tick(4001);
        Check(s.Submitted.Count == 1 && s.Submitted[0].At == 4001, "unpaused stable queue must submit overdue work without a fresh wait");
    }
    private static void FlashCadence()
    {
        using var s = new Scope(); s.Difficulty = BotDifficulty.Flash; s.Prepare(0); s.Tick(1000); s.Tick(1499);
        Check(s.Submitted.Count == 0, "Flash cannot bypass its shared 500 ms clock");
        s.Tick(1500); s.Prepare(1); s.Tick(1999);
        Check(s.Submitted.Count == 1, "a different Flash seat shares the same clock");
        s.Tick(2000); Check(s.Submitted.Count == 2, "Flash submits at its first shared due Tick");
    }
    private sealed class QueueOnlyEnd(Player owner) : EndPlayerTurnAction(owner, 1)
    {
        protected override Task ExecuteAction() => Task.CompletedTask;
    }
    private static void SuccessfulQueueClock()
    {
        using var s = new Scope(); s.Prepare(0); s.Tick(1000);
        s.Now = 2000;
        AccessTools.Method(typeof(BotRuntime), "QueuePlannedAction").Invoke(null, [RunManager.Instance, new QueueOnlyEnd(s.Bots[1])]);
        s.RejectEnqueue = true;
        try { s.Tick(2500); throw new InvalidOperationException("Mixed runtime pacing: silently rejected enqueue was counted as success"); }
        catch (InvalidOperationException error) when (error.Message.Contains("did not enqueue")) { }
        Check(s.Pending is null && s.Submitted.Count == 0, "failed native queue submission must release only the prepared slot");
        s.RejectEnqueue = false; s.Prepare(1); s.Tick(2501);
        Check(s.Submitted.Count == 1 && s.Submitted[0].At == 2501,
            "neither EndTurn nor a silently refused card can restart the successful card clock");
    }
    private static void ResumedCardClock()
    {
        using var s = new Scope(); s.Prepare(0); s.Tick(1000); s.Tick(2500);
        s.Prepare(1);
        var physical = s.Bots[1].PlayerCombatState!.Hand.Cards.First();
        var action = new PlayCardAction(physical, s.Enemy);
        Action resume = () => AccessTools.Method(typeof(BotRuntime), "TrySubmitPreparedCard")
            .Invoke(null, [RunManager.Instance, s.Combat, s.Finished, BotDifficulty.Pro]);
        var type = AccessTools.Inner(typeof(BotRuntime), "PendingNativeSubmission");
        SetRuntime("_preparedNative", Activator.CreateInstance(type, Instance, null, [action, resume], null));
        s.Tick(2600); Check(s.Submitted.Count == 1 && s.Pending is not null,
            "turn-start/choice resumed active cards must still pass the team submit clock");
        s.Tick(2601, true);
        Check(s.Submitted.Count == 2 && s.Submitted[^1].At == 2601,
            "human EndTurn immediately releases a resumed active card through the same submit path");
    }
    private static bool Enqueue(GameAction __0, ulong __1)
    {
        if (active is not { } s) return true;
        if (s.RejectEnqueue) return false;
        Check(__0.OwnerId == __1, "transport must preserve the submitting seat");
        s.Queue.EnqueueWithoutSynchronizing(__0);
        if (__0 is PlayCardAction) s.Submitted.Add((__1, s.Now));
        __0.Execute().GetAwaiter().GetResult(); return false;
    }
    private static bool CardBody(PlayCardAction __instance, ref Task __result)
    {
        if (active is not { } s) return true;
        var pending = s.Pending!;
        var decision = (TeamCombatPlanner.Decision)pending.GetType().GetProperty("Decision")!.GetValue(pending)!;
        Check(__instance.OwnerId == decision.Player.NetId, "the queued native body must match the prepared physical owner");
        AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [decision.Move.Card, decision.Move.Target]);
        __result = Task.CompletedTask; return false;
    }
    private static void SetRuntime(string name, object? value) => AccessTools.Field(typeof(BotRuntime), name).SetValue(null, value);
    private static void Set(object target, string name, object? value) => AccessTools.Property(target.GetType(), name).SetValue(target, value);
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException("Mixed runtime pacing: " + message); }
}
