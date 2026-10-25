using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

// The four-seat fixture pays native Tools and executes native turn setup/menu
// consumption. Only the MOD's final completion callback is delayed, reproducing
// the native PlayPhase -> TurnStarted scheduling window from the live log.
// No card body, native hook, selector answer or native transition is replaced.
// Physical R2 RED evidence is appended after the serialized mutation runs.
internal static class MixedChoiceLeaseRetentionScenarios
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Sync = typeof(BotBrain).Assembly.GetType("CoopBots.BotTurnStartChoiceSync", true)!;
    private static readonly Type NativeFixture = AccessTools.Inner(typeof(TurnStartChoiceContractScenarios), "BoardScope");
    private static bool delayCompletion;

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var previousTournament = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null;
        var presentation = new Harmony("coopbots.mixed-choice-lease.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedChoiceLeaseRetentionScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Method(Sync, "CompletePlayerTurn"),
            prefix: new HarmonyMethod(typeof(MixedChoiceLeaseRetentionScenarios), nameof(CompletionDue)));
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("completed-zero", () => CompletedNative(zeroChoices: true)),
                ("completed-menu", () => CompletedNative(zeroChoices: false)),
                ("external-retains", ExternalRetains), ("queued-receipt", QueuedReceipt),
                ("readiness", ReadinessRetains),
                ("material-retire-uninstalled-contracts", MaterialRetiresUninstalledContracts) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: mixed choice lease " + test.Name); }
        }
        finally
        { delayCompletion = false; KernelCombatPlanner.TournamentOverride = previousTournament; presentation.UnpatchAll(presentation.Id); }
    }

    private static void CompletedNative(bool zeroChoices)
    {
        using var b = CreateFixture();
        var players = Party(b);
        var combat = Combat(b);
        if (zeroChoices) players[0].Creature.GetPower<ToolsOfTheTradePower>()!.RemoveInternal();
        var contract = Predict(combat, players);
        Check(contract.Complete && contract.OwnersTurnNumbers.Count == players.Length
            && contract.Choices.Count == (zeroChoices ? 0 : 1),
            "native prediction must establish the intended complete zero/menu turn-start contract");
        AutoPilot.Set(players[1].NetId, false);
        NativeCall(b, "SeedOldPrediction");
        NativeCall(b, "Install", contract);
        delayCompletion = true;
        try { NativeCall(b, "AdvanceNative"); }
        finally { delayCompletion = false; }
        Check(GetSync("active") is true && ReferenceEquals(GetSync("contract"), contract)
            && (int)GetSync("index")! == contract.Choices.Count
            && players.All(player => player.PlayerCombatState!.Phase == PlayerTurnPhase.Play),
            "native setup must finish every menu and enter Play while only the MOD completion callback is delayed");
        var before = GetPlanner("tournamentScript");
        WithQueue(players, queue => queue.EnqueueWithoutSynchronizing(HumanCard(players[1])));
        Check(GetSync("contract") is null && GetSync("installed") is null
            && ReferenceEquals(GetPlanner("tournamentScript"), before),
            "a human card after settled native setup must locally retire the completed lease without destroying or rewriting the Bot suffix");
        Check(((System.Collections.ICollection)GetSync("retired")!).Count > 0,
            "local early retirement must retain a lease receipt for a slower peer's later rejection");
    }

    private static void ExternalRetains()
    {
        using var b = CreateFixture();
        var players = Party(b); var combat = Combat(b);
        var contract = Predict(combat, players);
        AutoPilot.Set(players[1].NetId, false);
        NativeCall(b, "SeedOldPrediction");
        var original = ((IReadOnlyList<KernelTeamSearch.Action>)GetPlanner("tournamentScript")!)[0];
        var withChoices = original with { Choices = [], ChoiceOrdinals = [0], TurnStartChoices = contract };
        SetPlanner("tournamentScript", new[] { withChoices });
        SetPlanner("tournamentScriptRecord", TerminalRecord.Capture(KernelCombatCapture.Capture(combat), players, false) with {
            Victory = true, Kind = EvidenceKind.VerifiedTerminal });
        NativeCall(b, "Install", contract);
        WithQueue(players, queue => queue.EnqueueWithoutSynchronizing(HumanCard(players[1])));
        var retained = GetPlanner("tournamentScript") as IReadOnlyList<KernelTeamSearch.Action>;
        Check(GetSync("contract") is null && GetSync("installed") is null
            && retained is { Count: 1 } && ReferenceEquals(retained[0].Card, original.Card)
            && ReferenceEquals(retained[0].Player, original.Player) && ReferenceEquals(retained[0].Target, original.Target)
            && ReferenceEquals(retained[0].Choices, withChoices.Choices)
            && ReferenceEquals(retained[0].ChoiceOrdinals, withChoices.ChoiceOrdinals)
            && retained[0].TurnStartChoices is null
            && GetPlanner("tournamentScriptRecord") is TerminalRecord { Victory: false, Verified: false },
            "material human input before setup must retire stale future contracts and quality while retaining the same physical Bot card proposal and immediate menu choices");
    }

    private static void QueuedReceipt()
    {
        using var b = CreateFixture();
        var players = Party(b); var combat = Combat(b); var planner = Planner;
        AutoPilot.Set(players[1].NetId, false);
        NativeCall(b, "SeedOldPrediction");
        var card = ((IReadOnlyList<KernelTeamSearch.Action>)GetPlanner("tournamentScript")!)[0];
        var contract = Predict(combat, players);
        SetPlanner("tournamentScript", new KernelTeamSearch.Action[] {
            new(players[0], null, null, EndTurn: true), card with { TurnStartChoices = contract } });
        SetPlanner("ConfirmedEndTurn", players[0]);
        InvokePlanner("PrepareTournamentSubmission", new object?[] { null });
        var action = new EndPlayerTurnAction(players[0], players[0].PlayerCombatState!.TurnNumber);
        var queue = new ActionQueueSet(players); queue.CombatStarted(); queue.EnqueueWithoutSynchronizing(action);
        planner.ConfirmActionQueued(action);
        Check(planner.RetireMixedChoiceContracts(combat, "native receipt fixture"), "mixed lease retirement must accept the owning suffix");
        action.Execute().GetAwaiter().GetResult();
        Check((int)GetPlanner("tournamentScriptIndex")! == 1 && !planner.HasPreparedSubmission
            && ((IReadOnlyList<KernelTeamSearch.Action>)GetPlanner("tournamentScript")!)[1].TurnStartChoices is null,
            "retiring future choices must preserve a queued native receipt so it consumes the reserved step exactly once");
    }

    private static void ReadinessRetains()
    {
        using var b = CreateFixture(); var players = Party(b);
        var contract = Predict(Combat(b), players);
        AutoPilot.Set(players[1].NetId, false); NativeCall(b, "SeedOldPrediction"); NativeCall(b, "Install", contract);
        var before = GetPlanner("tournamentScript");
        WithQueue(players, queue => queue.EnqueueWithoutSynchronizing(
            new EndPlayerTurnAction(players[1], players[1].PlayerCombatState!.TurnNumber)));
        Check(ReferenceEquals(GetSync("contract"), contract) && ReferenceEquals(GetPlanner("tournamentScript"), before),
            "a pure native human readiness signal must preserve an unconsumed future menu lease and Bot suffix");
    }

    private static void MaterialRetiresUninstalledContracts()
    {
        // Reuse the native paid-card bridge already used by the stable suffix
        // fixtures. It skips presentation, but still pays and executes every
        // native card effect and the actual GameAction.AfterFinished observer.
        using var b = CreateFixture();
        var bridge = new Harmony("coopbots.mixed-choice-lease.native-paid-card");
        bridge.Patch(AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MixedStableScriptScenarios), "ActionBody")));
        var players = Party(b); var combat = Combat(b); var bot = players[0]; var human = players[1];
        var queue = new ActionQueueSet(players); queue.CombatStarted();
        KernelInputObserver.Observe(queue, combat);
        try
        {
            var planner = Planner;
            AutoPilot.Set(human.NetId, false);
            var enemy = combat.HittableEnemies.Single();
            var botCards = new[] { combat.CreateCard<StrikeIronclad>(bot), combat.CreateCard<StrikeIronclad>(bot) };
            foreach (var card in botCards) bot.PlayerCombatState!.Hand.AddInternal(card);
            var humanCard = combat.CreateCard<StrikeIronclad>(human); human.PlayerCombatState!.Hand.AddInternal(humanCard);
            var botEnergy = bot.PlayerCombatState!.Energy; var humanEnergy = human.PlayerCombatState.Energy;
            NativeCall(b, "SeedOldPrediction");
            var root = KernelCombatCapture.Capture(combat);
            var script = botCards.Select(card => new KernelTeamSearch.Action(bot, card, enemy,
                CardStateKey: root.CardStateKey(card))).ToArray();
            var contract = Predict(combat, players);
            var expanded = script.Append(new(bot, null, null, EndTurn: true) { TurnStartChoices = contract }).ToArray();
            SetPlanner("tournamentScript", expanded); SetPlanner("tournamentScriptRound", combat.RoundNumber);
            SetPlanner("tournamentScriptRecord", TerminalRecord.Capture(root, players, false) with {
                Victory = true, Kind = EvidenceKind.VerifiedTerminal });
            AccessTools.Method(typeof(KernelCombatPlanner), "CaptureMixedContinuation").Invoke(planner, [root, combat, expanded, 0]);
            Check(GetSync("installed") is null, "material revision fixture must have no installed future choice lease");
            var beforeHp = enemy.CurrentHp;
            PaidAction(queue, humanCard, enemy);
            Check(KernelInputObserver.Revision == 1 && human.PlayerCombatState!.Energy == humanEnergy - 1
                && combat.HittableEnemies.Single().CurrentHp == beforeHp - 6,
                "human material revision must come from a paid native Strike and its real settled observer");
            var status = planner.Poll(combat, [bot], queue.NextActionId, null, false, BotDifficulty.Pro, out var decision);
            var retained = AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScript").GetValue(planner)
                as IReadOnlyList<KernelTeamSearch.Action>;
            Check(status == KernelCombatPlanner.Status.Ready && decision is not null
                && ReferenceEquals(decision.Move.Card, script[0].Card)
                && retained is { Count: 3 } && retained[^1].EndTurn && retained[^1].TurnStartChoices is null
                && AccessTools.Field(typeof(KernelCombatPlanner), "tournamentRequest").GetValue(planner) is null
                && GetSync("installed") is null,
                "a settled native human card must retire an uninstalled old EndTurn contract while the same legal Bot suffix stays immediately playable without foreground search");
            PaidAction(queue, decision!.Move.Card, enemy, planner);
            Check(bot.PlayerCombatState!.Energy == botEnergy - 1
                && (int)AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScriptIndex").GetValue(planner)! == 1
                && combat.HittableEnemies.Single().CurrentHp == beforeHp - 12,
                "the retained physical Bot Strike must really pay, damage and consume one native receipt after material contract retirement");
        }
        finally { KernelInputObserver.Observe(queue, null); bridge.UnpatchAll(bridge.Id); }
    }

    private static void PaidAction(ActionQueueSet queue, CardModel card,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature target, KernelCombatPlanner? planner = null)
    {
        var action = new PlayCardAction(card.Owner, NetCombatCard.ForTesting(0), card.Id, target.CombatId);
        var type = typeof(MixedStableScriptScenarios);
        AccessTools.Field(type, "executing").SetValue(null, action);
        AccessTools.Field(type, "executingCard").SetValue(null, card);
        AccessTools.Field(type, "executingTarget").SetValue(null, target);
        try
        {
            planner?.ConfirmActionQueued(action);
            queue.EnqueueWithoutSynchronizing(action); action.Execute().GetAwaiter().GetResult();
        }
        finally
        {
            AccessTools.Field(type, "executing").SetValue(null, null);
            AccessTools.Field(type, "executingCard").SetValue(null, null);
            AccessTools.Field(type, "executingTarget").SetValue(null, null);
        }
    }

    private static KernelTurnStartChoiceContract Predict(CombatState combat, Player[] players)
    {
        var branch = KernelCombatCapture.Capture(combat).Fork();
        branch.AutoResolveEnemyChoices = true; branch.AutoResolveTurnStartChoices = true;
        KernelTeamSearch.Action? last = null;
        using var budget = KernelSearchBudgetScope.Enter(new KernelDecisionRequest(39400, 0, 6));
        foreach (var player in players)
        {
            Check(branch.EndTurn(player, 80, out var why, out var action), "native predicted turn transition must settle: " + why);
            last = action;
        }
        return last!.TurnStartChoices!;
    }

    private static void WithQueue(Player[] players, Action<ActionQueueSet> body)
    {
        var queue = new ActionQueueSet(players); queue.CombatStarted();
        var observe = AccessTools.Method(Sync, "ActionEnqueued").CreateDelegate<Action<GameAction>>();
        queue.ActionEnqueued += observe;
        try { body(queue); }
        finally { queue.ActionEnqueued -= observe; }
    }
    private static PlayCardAction HumanCard(Player player) => new(player,
        NetCombatCard.ForTesting(0), ModelDb.Card<StrikeIronclad>().Id, null);
    private static IDisposable CreateFixture() => (IDisposable)Activator.CreateInstance(NativeFixture, Instance, null, [false], null)!;
    private static Player[] Party(object b) => (Player[])AccessTools.Field(NativeFixture, "Party").GetValue(b)!;
    private static CombatState Combat(object b) => (CombatState)AccessTools.Field(NativeFixture, "Combat").GetValue(b)!;
    private static void NativeCall(object b, string method, params object?[] args) => AccessTools.Method(NativeFixture, method).Invoke(b, args);
    private static KernelCombatPlanner Planner => (KernelCombatPlanner)AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
    private static object? GetPlanner(string name) => AccessTools.Field(typeof(KernelCombatPlanner), name)?.GetValue(Planner)
        ?? AccessTools.Property(typeof(KernelCombatPlanner), name)?.GetValue(Planner);
    private static void SetPlanner(string name, object? value)
    {
        if (AccessTools.Field(typeof(KernelCombatPlanner), name) is { } field) field.SetValue(Planner, value);
        else AccessTools.Property(typeof(KernelCombatPlanner), name).SetValue(Planner, value);
    }
    private static object? InvokePlanner(string method, object?[] args) => AccessTools.Method(typeof(KernelCombatPlanner), method).Invoke(Planner, args);
    private static object? GetSync(string name) => AccessTools.Field(Sync, name).GetValue(null);
    private static bool CompletionDue() => !delayCompletion;
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("Mixed choice lease: " + message); }
}

// R2 actual 2026-10-25: completed-zero build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/choice-lease-r2/receipts.json
// System.InvalidOperationException: Mixed choice lease: a human card after settled native setup must locally retire the completed lease without destroying or rewriting the Bot suffix

// R2 actual 2026-10-25: completed-menu build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/choice-lease-r2/receipts.json
// System.InvalidOperationException: Mixed choice lease: a human card after settled native setup must locally retire the completed lease without destroying or rewriting the Bot suffix

// R2 actual 2026-10-25: external-retains build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/choice-lease-r2/receipts.json
// System.InvalidOperationException: Mixed choice lease: material human input before setup must retire stale future contracts and quality while retaining the same physical Bot card proposal and immediate menu choices

// R2 actual 2026-10-25: queued-receipt build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/choice-lease-r2/receipts.json
// System.InvalidOperationException: Mixed choice lease: retiring future choices must preserve a queued native receipt so it consumes the reserved step exactly once

// R2 actual 2026-10-25: readiness build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/choice-lease-r2/receipts.json
// System.InvalidOperationException: Mixed choice lease: a pure native human readiness signal must preserve an unconsumed future menu lease and Bot suffix

// R2 actual 2026-10-25: material-retire-uninstalled-contracts build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/choice-lease-r2/receipts.json
// System.InvalidOperationException: Mixed choice lease: a settled native human card must retire an uninstalled old EndTurn contract while the same legal Bot suffix stays immediately playable without foreground search
