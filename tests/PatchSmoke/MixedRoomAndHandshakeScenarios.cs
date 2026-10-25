using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Constructed multiplayer regression. Map rendering and screen inspection are
// headless seams; RoomWork.Waiting, BotRuntime.TryVoteOnMap, native map votes,
// native ActionQueueSet enqueue/execute events, installed future-choice leases,
// and the real Tools turn-start menu all follow their production paths.
// Native ReadyToBeginEnemyTurnAction explicitly documents that it is
// automatically enqueued without player intervention; CombatManager's
// SetReadyToBeginEnemyTurn has the same native contract. Other action types,
// including unknown mod actions, remain conservative external input.
// R2 raw failing assertions are recorded here after the serial physical runs.
// R2 actual 2026-10-24: production mutant build0 / RED1 / exact restore / GREEN0.
// Receipt: work/mixed-fixes-20261023T164339Z/r2-valid-proofs.json
// human-reward-map: System.InvalidOperationException: Mixed room/handshake: a human's retained reward overlay cannot become unattended Bot reward work
// automatic-ready: System.InvalidOperationException: Mixed room/handshake: native automatic enemy-turn readiness must preserve the installed future menu and cached script
// actual-human-input: System.InvalidOperationException: Mixed room/handshake: a real human PlayCardAction enqueue must retire the future choice and cached script
internal static class MixedRoomAndHandshakeScenarios
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type Sync = typeof(BotBrain).Assembly.GetType("CoopBots.BotTurnStartChoiceSync", true)!;
    private static readonly Type NativeFixture = AccessTools.Inner(typeof(TurnStartChoiceContractScenarios), "BoardScope");
    private static NOverlayStack? overlay;
    private static NRewardsScreen? rewardScreen;
    private static NMapScreen? mapScreen;
    private static bool rewardFinished;
    private static int rewardInspections;
    private static GameAction? submitted;

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.mixed-room-handshake.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedRoomAndHandshakeScenarios), nameof(Format)));
        try
        {
            if (selected is null or "map") MapAfterHumanRewards();
            if (selected is null or "handshake") AutomaticHandshakeRetainsNativeChoice();
            if (selected is null or "human-input") HumanActionsRetireFutureChoice();
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static void MapAfterHumanRewards()
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 17101);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 96, 0));
        var state = RunState.CreateForTest([human, bot], seed: "MIXED-REWARD-MAP");
        var manager = (RunManager)RuntimeHelpers.GetUninitializedObject(typeof(RunManager));
        var queueSync = (ActionQueueSynchronizer)RuntimeHelpers.GetUninitializedObject(typeof(ActionQueueSynchronizer));
        var net = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        var map = new MapSelectionSynchronizer(net, queueSync, state);
        AccessTools.Property(typeof(RunManager), "MapSelectionSynchronizer").SetValue(manager, map);
        AccessTools.Property(typeof(RunManager), "ActionQueueSynchronizer").SetValue(manager, queueSync);
        var chosen = new MapVote { coord = new MapCoord(6, 2), mapGenerationCount = map.MapGenerationCount };
        map.PlayerVotedForMapCoord(human, state.MapLocation, chosen);
        overlay = UninitializedNode<NOverlayStack>();
        rewardScreen = UninitializedNode<NRewardsScreen>();
        // The complete suite can JIT Waiting before this UI seam is patched.
        // Native Peek is a tiny _overlays.LastOrDefault getter and can already
        // be inlined there; populate its real data as well as patching the UI.
        AccessTools.Field(typeof(NOverlayStack), "_overlays")
            .SetValue(overlay, new List<IOverlayScreen> { rewardScreen });
        mapScreen = UninitializedNode<NMapScreen>();
        var ui = new Harmony("coopbots.mixed-room-map.headless-ui");
        var oldLocal = LocalContext.NetId;
        var oldHandedOver = AutoPilot.HandedOver.ToArray();
        var nextAction = AccessTools.Field(typeof(BotRuntime), "_nextActionAt");
        var oldNextAction = nextAction.GetValue(null);
        try
        {
            ui.Patch(AccessTools.PropertyGetter(typeof(NOverlayStack), "Instance"), prefix: Prefix(nameof(Overlay)));
            ui.Patch(AccessTools.Method(typeof(NOverlayStack), "Peek"), prefix: Prefix(nameof(RewardOverlay)));
            ui.Patch(AccessTools.Method(typeof(BotRewardsScreenDriver), "Finished"), prefix: Prefix(nameof(RewardsFinished)));
            ui.Patch(AccessTools.PropertyGetter(typeof(NMapScreen), "Instance"), prefix: Prefix(nameof(MapScreen)));
            ui.Patch(AccessTools.PropertyGetter(typeof(NMapScreen), "IsOpen"), prefix: Prefix(nameof(True)));
            ui.Patch(AccessTools.PropertyGetter(typeof(NMapScreen), "IsTravelEnabled"), prefix: Prefix(nameof(True)));
            ui.Patch(AccessTools.PropertyGetter(typeof(NMapScreen), "IsTraveling"), prefix: Prefix(nameof(False)));
            ui.Patch(AccessTools.Method(typeof(ActionQueueSynchronizer), "EnqueueAction", [typeof(GameAction), typeof(ulong)]),
                prefix: Prefix(nameof(Submit)));
            LocalContext.NetId = human.NetId;
            AutoPilot.Set(human.NetId, false);
            rewardFinished = false; rewardInspections = 0; submitted = null;
            Check(RoomWork.Waiting(manager, state) is null && rewardInspections == 0,
                "a human's retained reward overlay cannot become unattended Bot reward work");
            Check((bool)AccessTools.Method(typeof(BotRuntime), "TryVoteOnMap").Invoke(null, [manager, state])!
                && submitted is VoteForMapCoordAction && submitted.OwnerId == bot.NetId,
                "the production map path must submit a Bot vote while the human reward overlay remains");
            var netVote = (NetVoteForMapCoordAction)submitted!.ToNetAction();
            Check(netVote.destination?.coord == chosen.coord && netVote.destination?.mapGenerationCount == chosen.mapGenerationCount,
                "the submitted Bot map vote must follow the human's current-generation coordinate");
            AutoPilot.Set(human.NetId, true);
            submitted = null;
            Check(RoomWork.Waiting(manager, state) == "rewards pending" && rewardInspections > 0,
                "an actual local autopilot reward screen must retain its pending reward barrier");
            Check(!(bool)AccessTools.Method(typeof(BotRuntime), "TryVoteOnMap").Invoke(null, [manager, state])!
                && submitted is null,
                "the production map path must not submit before local autopilot rewards finish");
            rewardFinished = true;
            Check(RoomWork.Waiting(manager, state) is null,
                "finishing local autopilot rewards must release the same map barrier");
        }
        finally
        {
            ui.UnpatchAll(ui.Id); LocalContext.NetId = oldLocal;
            AutoPilot.Clear(); foreach (var id in oldHandedOver) AutoPilot.Set(id, true);
            nextAction.SetValue(null, oldNextAction);
            overlay = null; rewardScreen = null; mapScreen = null; submitted = null;
        }
        Console.WriteLine("PASS: mixed human reward overlay allows a real Bot map vote; local autopilot rewards still hold it.");
    }

    private static void AutomaticHandshakeRetainsNativeChoice()
    {
        using var fixture = CreateNativeFixture();
        var players = (Player[])AccessTools.Field(NativeFixture, "Party").GetValue(fixture)!;
        var combat = (CombatState)AccessTools.Field(NativeFixture, "Combat").GetValue(fixture)!;
        var menu = (CardModel[])AccessTools.Field(NativeFixture, "Menu").GetValue(fixture)!;
        var prediction = AccessTools.Method(NativeFixture, "PredictExpired").Invoke(fixture, null)!;
        var contract = (KernelTurnStartChoiceContract)AccessTools.Property(prediction.GetType(), "Contract").GetValue(prediction)!;
        Check(contract.Choices.Single().Cards.Single().Id == "BLADE_DANCE",
            "the native fixture must retain its distinguishing planned Blade Dance discard");
        AutoPilot.Set(players[1].NetId, false);
        AccessTools.Method(NativeFixture, "SeedOldPrediction").Invoke(fixture, null);
        AccessTools.Method(NativeFixture, "Install").Invoke(fixture, [contract]);
        var queue = new ActionQueueSet(players); queue.CombatStarted();
        var observe = AccessTools.Method(Sync, "ActionEnqueued").CreateDelegate<Action<GameAction>>();
        queue.ActionEnqueued += observe;
        KernelInputObserver.Observe(queue, combat);
        try
        {
            var automatic = new ReadyToBeginEnemyTurnAction(players[1]);
            queue.EnqueueWithoutSynchronizing(automatic);
            Check(ReferenceEquals(GetSync("contract"), contract) && !OldPredictionCleared(fixture),
                "native automatic enemy-turn readiness must preserve the installed future menu and cached script");
            automatic.Execute().GetAwaiter().GetResult();
            Check(KernelInputObserver.Revision == 0 && ReferenceEquals(GetSync("contract"), contract),
                "settling native automatic readiness cannot publish a human input revision or cancel the menu");
            AccessTools.Method(NativeFixture, "AdvanceNative").Invoke(fixture, null);
            Check(menu[5].Pile?.Type == MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard
                && menu[0].Pile?.Type == MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                "after the native handshake the real Tools selector must still consume planned Blade Dance instead of fallback Defend");
            Check(GetSync("contract") is null && GetSync("installed") is null,
                "the surviving future menu must retire normally after native turn-start consumption");
        }
        finally { queue.ActionEnqueued -= observe; KernelInputObserver.Observe(queue, null); }
        Console.WriteLine("PASS: native automatic handshake preserves and consumes the installed Tools future-choice contract.");
    }

    private static void HumanActionsRetireFutureChoice()
    {
        using var fixture = CreateNativeFixture();
        var players = (Player[])AccessTools.Field(NativeFixture, "Party").GetValue(fixture)!;
        var prediction = AccessTools.Method(NativeFixture, "PredictExpired").Invoke(fixture, null)!;
        var contract = (KernelTurnStartChoiceContract)AccessTools.Property(prediction.GetType(), "Contract").GetValue(prediction)!;
        var human = players[1]; AutoPilot.Set(human.NetId, false);
        var queue = new ActionQueueSet(players); queue.CombatStarted();
        var observe = AccessTools.Method(Sync, "ActionEnqueued").CreateDelegate<Action<GameAction>>();
        queue.ActionEnqueued += observe;
        try
        {
            GameAction[] actions = [new PlayCardAction(human, NetCombatCard.ForTesting(0), ModelDb.Card<StrikeIronclad>().Id, null),
                new UsePotionAction(human, 0, null, null, true),
                new UndoEndPlayerTurnAction(human, 1), new DiscardPotionGameAction(human, 0, true), new UnknownStateAction(human)];
            foreach (var action in actions)
            {
                AccessTools.Method(NativeFixture, "SeedOldPrediction").Invoke(fixture, null);
                AccessTools.Method(NativeFixture, "Install").Invoke(fixture, [contract]);
                queue.EnqueueWithoutSynchronizing(action);
                Check(GetSync("contract") is null && GetSync("installed") is null && OldPredictionCleared(fixture),
                    "a real human " + action.GetType().Name + " enqueue must retire the future choice and cached root prediction");
            }
            AccessTools.Method(NativeFixture, "SeedOldPrediction").Invoke(fixture, null);
            AccessTools.Method(NativeFixture, "Install").Invoke(fixture, [contract]);
            queue.EnqueueWithoutSynchronizing(new EndPlayerTurnAction(human, 1));
            Check(ReferenceEquals(GetSync("contract"), contract) && !OldPredictionCleared(fixture),
                "a pure native human readiness signal must retain the unconsumed future menu and physical script");
            AutoPilot.Set(human.NetId, true);
            AccessTools.Method(NativeFixture, "SeedOldPrediction").Invoke(fixture, null);
            AccessTools.Method(NativeFixture, "Install").Invoke(fixture, [contract]);
            queue.EnqueueWithoutSynchronizing(new PlayCardAction(human, NetCombatCard.ForTesting(0), ModelDb.Card<StrikeIronclad>().Id, null));
            Check(ReferenceEquals(GetSync("contract"), contract) && !OldPredictionCleared(fixture),
                "a handed-over seat's delegated card action must retain the future contract");
        }
        finally { queue.ActionEnqueued -= observe; }
        Console.WriteLine("PASS: material human actions retire future choices; native readiness and delegated cards retain them.");
    }

    private sealed class UnknownStateAction(Player owner) : GameAction
    {
        public override ulong OwnerId => owner.NetId;
        public override GameActionType ActionType => GameActionType.Combat;
        protected override Task ExecuteAction() => Task.CompletedTask;
        public override INetAction ToNetAction() => default(NetReadyToBeginEnemyTurnAction);
    }

    private static IDisposable CreateNativeFixture()
        => (IDisposable)Activator.CreateInstance(NativeFixture, Instance, null, [false], null)!;
    private static bool OldPredictionCleared(object fixture)
        => (bool)AccessTools.Property(NativeFixture, "OldPredictionCleared").GetValue(fixture)!;
    private static object? GetSync(string field) => AccessTools.Field(Sync, field).GetValue(null);
    private static T UninitializedNode<T>() where T : class
    { var node = (T)RuntimeHelpers.GetUninitializedObject(typeof(T)); GC.SuppressFinalize(node); return node; }
    private static HarmonyMethod Prefix(string method) => new(typeof(MixedRoomAndHandshakeScenarios), method);
    private static bool Overlay(ref NOverlayStack? __result) { __result = overlay; return false; }
    private static bool RewardOverlay(ref IOverlayScreen? __result) { __result = rewardScreen; return false; }
    private static bool RewardsFinished(ref bool __result) { rewardInspections++; __result = rewardFinished; return false; }
    private static bool MapScreen(ref NMapScreen? __result) { __result = mapScreen; return false; }
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool Submit(GameAction action) { submitted = action; return false; }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("Mixed room/handshake: " + message); }
}
