using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// A constructed four-seat native menu, independent of work/ saved-run assets.
// F31's archived boss root is not reproduced here. The native Tools effect and
// production selector execute; no TestCardSelector supplies their answer.
// Actual 2026-10-12 native R2, each forced build0 -> assertion RED1 -> exact
// source/DLL/PDB restore -> native GREEN0:
// Core SelectMenu withdraws its lease:
// Turn-start contract: real native Tools selector must consume the EndTurn contract instead of independently choosing Defend
// Replay replaces recorded tokens with AutomaticTurnStartChoice:
// Turn-start contract: native turn-start contract and exact replay must retain all canonical future facts including RNG/history
// The second RED uses the explicit second-Defend identity witness, not a claim
// that the original search picked Defend. Direct Automatic on the original
// Blade contract stayed GREEN; that failed control is retained. A whole-
// transition null-contract mutant exceeded 160s and is not counted as RED.
// Evidence: work/v16-turn-start-contract-native-20261012-v1/{r2-native,r2-replay-v4}/proof.json.
internal static class TurnStartChoiceContractScenarios
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Sync = typeof(BotBrain).Assembly.GetType("CoopBots.BotTurnStartChoiceSync", true)!;

    internal static void Run(string? only = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.turn-start-contract.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(TurnStartChoiceContractScenarios), nameof(Format)));
        try
        {
            if (only is null or "native") NativeContract();
            if (only is null or "replay") ReplayClock();
            if (only is null or "menu") NativeGuard("menu");
            if (only is null or "identity")
            { NativeGuard("owner"); NativeGuard("round"); NativeGuard("source"); }
            if (only is null or "unconsumed") NativeGuard("unconsumed");
            if (only is null or "duplicate") DuplicatePhysicalToken();
            if (only is null or "horizon") HorizonReplay();
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static void NativeContract()
    {
        // Independent native policy control: a real Tools menu with no lease.
        using (var control = new BoardScope())
        {
            control.AdvanceNative();
            Check(control.Menu[0].Pile?.Type == PileType.Discard
                && control.Menu[5].Pile?.Type == PileType.Hand,
                "unleased native Tools control must independently discard the first Defend and retain Blade Dance");
        }
        using var b = new BoardScope();
        var prediction = b.PredictExpired();
        var choice = prediction.Contract.Choices.Single();
        Check(choice.Cards.Single().Id == "BLADE_DANCE" && choice.Options?.Count == 6
            && choice.Timing == "PlayerTurnStart" && choice.RequestedMinimum == 1 && choice.RequestedMaximum == 1,
            "expired mandatory EndTurn must retain its actual complete Tools menu and adopted Blade Dance token");
        b.Install(prediction.Contract);
        b.AdvanceNative();
        Check(b.Menu[5].Pile?.Type == PileType.Discard && b.Menu[0].Pile?.Type == PileType.Hand,
            "real native Tools selector must consume the EndTurn contract instead of independently choosing Defend");
        Check(Get("contract") is null && Get("installed") is null,
            "real TurnStarted must retire a completely consumed turn-start lease");
        CompareNativeReplay(b, prediction.Actions, prediction.Root);
        Console.WriteLine("PASS: four-seat native Tools consumes the expired EndTurn menu through production Core.");
    }

    private static void ReplayClock()
    {
        using var b = new BoardScope(ordinaryEnergy: true);
        var prediction = b.PredictExpired();
        var replay = prediction.Root.Fork();
        using (KernelSearchBudgetScope.Enter(new KernelDecisionRequest(16200, 9600, 6)))
            foreach (var action in prediction.Actions)
                Check(replay.ReplayPlanned(action, 80, out var why),
                    "recorded EndTurn must replay its adopted menu after caller clock changes: " + why);
        Check(replay.Hand(b.Party[0]).SequenceEqual(prediction.State.Hand(b.Party[0]))
            && replay.PlanningState(b.Ids).Semantics == prediction.State.PlanningState(b.Ids).Semantics,
            "EndTurn replay under a fresh clock must retain the original Blade Dance discard and complete semantics");
        var last = prediction.Actions[^1];
        var missing = prediction.Root.Fork();
        foreach (var action in prediction.Actions.Take(3))
            Check(missing.ReplayPlanned(action, 80, out var why), "early recorded seat end settles: " + why);
        var before = missing.PlanningState(b.Ids);
        Check(!missing.ReplayPlanned(last with { TurnStartChoices = null }, 80, out var refusal)
            && refusal == "turn-start-contract:not-recorded" && missing.PlanningState(b.Ids).Semantics == before.Semantics,
            "an EndTurn missing its recorded contract must refuse before readiness or native setup changes");
        var enemy = b.Combat.Enemies.Single();
        var strike = replay.Hand(b.Party[1]).First(c => c is StrikeIronclad);
        var playWhy = "";
        Check(replay.LastTurnStartChoices is not null && replay.Play(strike, enemy, out playWhy)
            && replay.LastTurnStartChoices is null,
            "a direct ordinary Strike must retire the previous EndTurn's LastTurnStartChoices: " + playWhy);
        Check(replay.EndTurn(b.Party[0], 80, out var earlyWhy) && replay.LastTurnStartChoices is not null,
            "ordinary-potion stale contract control has a genuine new early-seat EndTurn: " + earlyWhy);
        var potion = b.Party[3].Potions.OfType<EnergyPotion>().Single();
        Check(replay.UsePotion(potion, b.Party[3].Creature, out var potionWhy) && replay.LastTurnStartChoices is null,
            "a direct ordinary Energy potion must retire the previous EndTurn's LastTurnStartChoices: " + potionWhy);
        Check(replay.EndTurn(b.Party[1], 80, out earlyWhy) && replay.LastTurnStartChoices is not null,
            "ordinary resolved-action control records another actual early-seat EndTurn: " + earlyWhy);
        strike = replay.Hand(b.Party[2]).First(c => c is StrikeIronclad);
        Check(replay.ReplayPlanned(new(b.Party[2], strike, enemy), 80, out playWhy, out var resolved)
            && resolved.TurnStartChoices is null && replay.LastTurnStartChoices is null,
            "an ordinary resolved Strike action must not inherit the preceding EndTurn contract: " + playWhy);
        Console.WriteLine("PASS: EndTurn replay consumes the recorded menu regardless of current request clock.");
    }

    private static void NativeGuard(string kind)
    {
        using var b = new BoardScope();
        var prediction = b.PredictExpired();
        var original = prediction.Contract;
        var first = original.Choices.Single();
        var altered = kind switch
        {
            "owner" => original with { Choices = [first with { Owner = b.Party[1].NetId }] },
            "round" => original with { ToRound = original.ToRound + 1 },
            "source" => original with { Choices = [first with { MenuSource = "WRONG_POWER_SOURCE" }] },
            "unconsumed" => original with { Choices = [first, first] },
            _ => original,
        };
        b.SeedOldPrediction();
        b.Install(altered);
        if (kind == "menu") b.Menu[0].UpgradeInternal();
        b.AdvanceNative();
        Check(Get("contract") is null && Get("installed") is null && b.OldPredictionCleared,
            "native turn-start " + kind + " mismatch must retire the lease and invalidate the old prediction");
        Check(b.Party[0].PlayerCombatState!.Hand.Cards.Count == 5,
            "native turn-start refusal must still settle the real mandatory Tools discard");
        Console.WriteLine("PASS: native turn-start " + kind + " guard clears its lease and stale prediction.");
    }

    // Actual native R2 2026-10-12: each production mutation built 0, asserted
    // RED1, then exact source/10 physical DLL+PDB restore built0 and GREEN0.
    // Withdraw the recorded endpoint cap:
    // Turn-start contract: a larger replay horizon must preserve the recorded enemy-only endpoint: turn-start-contract:unexpected-menu
    // Keep explicit continuation at the already-completed horizon instead:
    // Turn-start contract: explicit horizon continuation must record its adopted Tools setup without repeating the enemy phase
    // Evidence: work/v16-horizon-contract-native-20261012-v1/r2-cap/proof-accepted.json
    // and work/v16-horizon-contract-native-20261012-v1/r2-setup/proof.json.
    private static void HorizonReplay()
    {
        using var b = new BoardScope();
        var root = KernelCombatCapture.Capture(b.Combat);
        var shortState = root.Fork();
        shortState.AutoResolveTurnStartChoices = true;
        shortState.AutoResolveEnemyChoices = true;
        var actions = new List<KernelTeamSearch.Action>();
        foreach (var p in b.Party)
        {
            Check(shortState.EndTurn(p, 1, out var why, out var action), "short side settles: " + why);
            actions.Add(action);
        }
        Check(actions[^1].TurnStartChoices is { Complete: true, OwnersTurnNumbers.Count: 0, Choices.Count: 0 }
            && shortState.EnemyPhaseCompleted && shortState.HorizonClosed,
            "a short prediction must actually stop before the native Tools setup");
        var replay = root.Fork();
        foreach (var action in actions)
            Check(replay.ReplayPlanned(action, 80, out var why),
                "a larger replay horizon must preserve the recorded enemy-only endpoint: " + why);
        Check(replay.PlanningState(b.Ids).Semantics == shortState.PlanningState(b.Ids).Semantics,
            "short-horizon exact replay must retain all canonical enemy-boundary facts");
        replay.AutoResolveTurnStartChoices = true;
        replay.AutoResolveEnemyChoices = true;
        using (KernelSearchBudgetScope.Enter(new KernelDecisionRequest(16201, 0, 6)))
        {
            Check(replay.ContinueRecordedHorizon(80, out var why, out var continuation),
                "explicit continuation must start the already-settled side: " + why);
            Check(continuation is { Complete: true, FromRound: 1, ToRound: 2, OwnersTurnNumbers.Count: 4, Choices.Count: 1 }
                && !replay.HorizonClosed && !replay.EnemyPhaseCompleted && replay.RoundsAdvanced == 1,
                "explicit horizon continuation must record its adopted Tools setup without repeating the enemy phase");
            actions[^1] = actions[^1] with { TurnStartChoices = continuation };
        }
        var extendedReplay = root.Fork();
        foreach (var action in actions)
            Check(extendedReplay.ReplayPlanned(action, 80, out var why), "extended recorded prefix replays: " + why);
        Check(extendedReplay.PlanningState(b.Ids).Semantics == replay.PlanningState(b.Ids).Semantics,
            "extended-prefix replay must retain the single enemy phase and exact adopted setup facts");
        b.Install(actions[^1].TurnStartChoices!);
        b.AdvanceNative();
        CompareNativeReplay(b, actions, root);
        Console.WriteLine("PASS: short-horizon exact replay and explicit native Tools continuation preserve the recorded boundary.");
    }

    private static void DuplicatePhysicalToken()
    {
        using var b = new BoardScope();
        var prediction = b.PredictExpired();
        var first = prediction.Contract.Choices.Single();
        Check(first.Options![0].Id == "DEFEND_SILENT" && first.Options[1].Id == "DEFEND_SILENT"
            && first.Options[0].Occurrence == 0 && first.Options[1].Occurrence == 1,
            "the complete native menu must distinguish both identical Defend occurrences");
        // A legal contract-control variant, not a claim that the search chose it.
        var duplicate = prediction.Contract with { Choices = [first with { Cards = [first.Options[1]] }] };
        var actions = prediction.Actions.ToArray();
        actions[^1] = actions[^1] with { TurnStartChoices = duplicate };
        b.Install(duplicate);
        b.AdvanceNative();
        Check(b.Menu[1].Pile?.Type == PileType.Discard && b.Menu[0].Pile?.Type == PileType.Hand,
            "production native turn-start contract must discard the exact second physical Defend occurrence");
        CompareNativeReplay(b, actions, prediction.Root);
        Console.WriteLine("PASS: native Tools and EndTurn replay preserve duplicate physical token identity.");
    }

    private static void CompareNativeReplay(BoardScope b, IReadOnlyList<KernelTeamSearch.Action> actions, KernelSession root)
    {
        var replay = root.Fork();
        foreach (var action in actions)
            Check(replay.ReplayPlanned(action, 80, out var why), "native comparison's exact EndTurn replay settles: " + why);
        var actual = KernelCombatCapture.Capture(b.Combat).PlanningState(b.Ids);
        var expected = replay.PlanningState(b.Ids);
        Check(expected.Semantics == actual.Semantics,
            "native turn-start contract and exact replay must retain all canonical future facts including RNG/history");
        if (expected.Reusable && actual.Reusable)
            Check(expected.Matches(actual), "qualified native turn-start comparison preserves all physical references");
        Console.WriteLine($"DIAG turn-start native full-semantics=True reusable={expected.Reusable}/{actual.Reusable} "
            + $"qualification={expected.FirstMismatch(actual) ?? "equal"}");
    }

    private sealed record Prediction(KernelSession Root, KernelSession State,
        IReadOnlyList<KernelTeamSearch.Action> Actions, KernelTurnStartChoiceContract Contract);

    private sealed class BoardScope : IDisposable
    {
        internal readonly Player[] Party;
        internal readonly CombatState Combat;
        internal readonly CardModel[] Menu;
        internal ulong[] Ids => Party.Select(p => p.NetId).ToArray();
        private readonly IDisposable native, managers;
        private readonly object manager;
        private readonly KernelCombatPlanner planner;
        private readonly Dictionary<FieldInfo, object?> oldPlanner, oldSync;
        private readonly object? oldIdentity;
        private readonly ulong[] oldAutopilot = AutoPilot.HandedOver.ToArray();
        private readonly Action<CombatState> complete = state => Call("CompletePlayerTurn", state);
        private KernelTeamSearch.Action[]? oldPrediction;
        internal bool OldPredictionCleared => !ReferenceEquals(
            AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScript").GetValue(planner), oldPrediction);

        internal BoardScope(bool ordinaryEnergy = false)
        {
            Party = [Player.CreateForNewRun<Deprived>(UnlockState.all, 16101),
                Player.CreateForNewRun<Deprived>(UnlockState.all, 16102),
                Player.CreateForNewRun<Deprived>(UnlockState.all, 16103),
                Player.CreateForNewRun<Deprived>(UnlockState.all, 16104)];
            Combat = new CombatState(runState: RunState.CreateForTest(Party, seed: "TURN-START-CONTRACT", ascensionLevel: 10)) { RoundNumber = 1 };
            for (var i = 0; i < Party.Length; i++)
            {
                var p = Party[i]; p.ResetCombatState(); Combat.AddPlayer(p); AutoPilot.Set(p.NetId, true);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(new[] { 57, 75, 31, 42 }[i]);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3); p.MaxEnergy = 3;
                if (i != 0) for (var j = 0; j < 5; j++) p.PlayerCombatState.DrawPile.AddInternal(Combat.CreateCard<StrikeIronclad>(p));
            }
            var owner = Party[0];
            Menu = [Combat.CreateCard<DefendSilent>(owner), Combat.CreateCard<DefendSilent>(owner),
                Combat.CreateCard<PiercingWail>(owner), Combat.CreateCard<ToolsOfTheTrade>(owner),
                Combat.CreateCard<TheBall>(owner), Combat.CreateCard<BladeDance>(owner)];
            foreach (var c in Menu) owner.PlayerCombatState!.DrawPile.AddInternal(c);
            for (var j = 0; j < 5; j++) owner.PlayerCombatState!.DrawPile.AddInternal(Combat.CreateCard<StrikeSilent>(owner));
            var enemy = Combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
            Combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
            native = MakeScope("NativeFactsState", Combat);
            managers = MakeScope("NativeReadyManagerScope", Combat); manager = managers;
            planner = (KernelCombatPlanner)AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
            oldPlanner = typeof(KernelCombatPlanner).GetFields(Instance).Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(planner));
            oldSync = Sync.GetFields(Static).Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(null));
            oldIdentity = AccessTools.Field(typeof(BotRuntime), "_combatIdentity").GetValue(null);
            AccessTools.Field(typeof(BotRuntime), "_combatIdentity").SetValue(null, Combat);
            CombatManager.Instance.TurnStarted += complete;
            var tools = Combat.CreateCard<ToolsOfTheTrade>(owner); owner.PlayerCombatState!.Hand.AddInternal(tools);
            var beforeHistory = CombatManager.Instance.History.CardPlaysFinished.Count();
            var paid = tools.SpendResources().GetAwaiter().GetResult();
            tools.OnPlayWrapper(new ThrowingPlayerChoiceContext(), null, false,
                new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1, StarsSpent = paid.Item2, StarValue = paid.Item2 },
                skipCardPileVisuals: true).GetAwaiter().GetResult();
            Check(paid.Item1 == 1 && owner.PlayerCombatState.Energy == 2
                && owner.Creature.GetPower<ToolsOfTheTradePower>()?.Amount == 1
                && CombatManager.Instance.History.CardPlaysFinished.Count() == beforeHistory + 1,
                "constructed native fixture must actually pay Tools and establish its real persistent hook");
            if (ordinaryEnergy) Party[3].AddPotionInternal(ModelDb.Potion<EnergyPotion>().ToMutable(), silent: true);
        }

        internal Prediction PredictExpired()
        {
            var root = KernelCombatCapture.Capture(Combat);
            var state = root.Fork(); state.AutoResolveTurnStartChoices = true; state.AutoResolveEnemyChoices = true;
            var actions = new List<KernelTeamSearch.Action>();
            using (KernelSearchBudgetScope.Enter(new KernelDecisionRequest(16100, 0, 6)))
                foreach (var p in Party)
                {
                    Check(state.EndTurn(p, 80, out var why, out var action), "expired mandatory native setup completes: " + why);
                    actions.Add(action);
                }
            var contract = actions[^1].TurnStartChoices;
            Check(contract is { Complete: true } && contract.Choices.Count == 1
                && contract.OwnersTurnNumbers.Count == 4 && contract.FromRound == 1 && contract.ToRound == 2,
                "four-seat settled transition must record exactly its adopted Tools menu, not sampled child probes");
            return new(root, state, actions, contract!);
        }

        internal void Install(KernelTurnStartChoiceContract contract)
        { Call("InstallLocal", Combat, contract, Party[^1].NetId, (uint)161); Call("MarkEnqueued", Party[^1], (uint)161); }

        internal void AdvanceNative()
        {
            foreach (var p in Party) CombatManager.Instance.SetReadyToEndTurn(p, false);
            NativeCall("EndPlayerTurnPhaseOneInternal", Turn); NativeCall("EndPlayerTurnPhaseTwoInternal", Turn);
            NativeCall("SwitchFromPlayerToEnemySide", Turn); NativeCall("StartTurn", Turn, null); NativeCall("StartTurn", Turn, null);
        }
        private object Turn => AccessTools.Property(manager.GetType(), "Turn").GetValue(manager)!;
        private void NativeCall(string method, params object?[] args)
            => AccessTools.Method(manager.GetType(), "Call").Invoke(manager, [method, args]);
        internal void SeedOldPrediction()
        {
            planner.Reset();
            oldPrediction = [new(Party[0], Menu[0], null)];
            AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScript").SetValue(planner, oldPrediction);
            AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScriptCombat").SetValue(planner, Combat);
            AccessTools.Field(typeof(KernelCombatPlanner), "rootCombat").SetValue(planner, Combat);
        }
        public void Dispose()
        {
            Call("Cancel"); CombatManager.Instance.TurnStarted -= complete;
            planner.Reset(); foreach (var item in oldPlanner) item.Key.SetValue(planner, item.Value);
            foreach (var item in oldSync) item.Key.SetValue(null, item.Value);
            AccessTools.Field(typeof(BotRuntime), "_combatIdentity").SetValue(null, oldIdentity);
            managers.Dispose(); native.Dispose();
            AutoPilot.Clear(); foreach (var id in oldAutopilot) AutoPilot.Set(id, true);
        }
    }

    private static IDisposable MakeScope(string name, CombatState combat)
        => (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), name),
            Instance, null, [combat], null)!;
    private static object? Get(string name) => AccessTools.Field(Sync, name).GetValue(null);
    private static object? Call(string method, params object?[] arguments) => AccessTools.Method(Sync, method).Invoke(null, arguments);
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("Turn-start contract: " + message); }
}
