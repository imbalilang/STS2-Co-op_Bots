using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

// SZG1RJZ6XVL6 exposed GamblingChip using independent per-card values.
// Controlled native selector witnesses; this is not the historical replay.
// R2 private-source proof: work/szg-repair-20261008T124624/choice/r2-results.json.
// Five faults built0/RED1, exact source restore, rebuilt0/GREEN0.
// effect-admission: System.Exception: SZG choice repair: native replacement must replace lasting Wound while retaining harmless Ethereal cleanup
// complete-witness-diagnostic: System.Exception: SZG choice repair: GamblingChip must use completed native hands instead of independent card-value fallback
// variable-cardinality: System.Exception: SZG choice repair: GamblingChip must admit every legal cardinality including no replacement and full replacement
// known-pool-order: System.Exception: SZG choice repair: GamblingChip must compare a common known pool without reading or rewriting the hidden top order
// dependency-wiring: System.Exception: SZG choice repair: production native hand comparison must enable bounded dependency ordering without nesting whole-hand searches
// Native effect/root immutability are independent controls for the previously
// guarded effect/cursor path. Skipping that path hits existing AssertConsumed;
// it is retained as an invalid target attempt, not counted as a new assertion.
// Capture R2: capture-v3/r2-results.json; each build0 / named RED1 /
// exact private restoration / build0 / GREEN0. Raw fault output:
// System.Exception: SZG choice repair: production capture must snapshot synthetic Bot synchronous selectors
// System.Exception: SZG choice repair: production capture must snapshot handed-over human synchronous selectors
// System.Exception: SZG choice repair: ordinary human selectors must remain deferred
// System.Exception: SZG choice repair: live end-turn risk must carry the same synchronous control snapshot into its next native side
// Warm-observer R2: combat/capture-warm-observer/r2/proof.json.
// Reverting actual StartNextPlayerTurn observation to the tiny Configure setter:
// build0 / RED1, risk.Simulated=True, hpLost=3, snapshots=0; exact restore0/0.
// Removing LiveEndTurnRisk's production Configure call: build0 / same named RED1,
// actual native side snapshots=1 but seats empty; exact source restore0/0.
// Both RED: "live end-turn risk must carry the same synchronous control snapshot into its next native side".
// The latter revalidates capture-v3's existing fault; only the warm observer is new.
internal static class SzgChoiceRepairScenarios
{
    private static bool observeRollouts;
    private static readonly List<RolloutOptions> comparedOptions = new();
    private static bool observeRiskCapture;
    private static readonly List<HashSet<ulong>> riskSnapshots = new();
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        if (Environment.GetCommandLineArgs().Contains("--szg-capture-warm")) WarmRiskEvaluation();
        var presentation = new Harmony("coopbots.szg-choice.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(SzgChoiceRepairScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Constructor(typeof(RolloutRun),
            [typeof(KernelSession), typeof(IReadOnlyList<Player>), typeof(IReadOnlyList<Player>), typeof(RolloutOptions), typeof(bool)]),
            postfix: new HarmonyMethod(typeof(SzgChoiceRepairScenarios), nameof(ObserveRollout)));
        // Observe the actual large native side transition. The tiny Configure
        // setter can already be inlined into a warmed LiveEndTurnRisk caller.
        presentation.Patch(AccessTools.Method(typeof(KernelSession), "StartNextPlayerTurn", [typeof(Player[]), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(SzgChoiceRepairScenarios), nameof(ObserveActualNextSide)));
        try
        {
            foreach (var test in new (string Name, Action Run)[] {
                ("cardinalities", Cardinalities), ("native-replacement", NativeReplacement),
                ("retain-synergy", RetainSynergy), ("hidden-order", HiddenOrder),
                ("capture-control", CaptureControl) })
                if (caseName is null || caseName == test.Name)
                { test.Run(); Console.WriteLine("PASS: SZG choice repair " + test.Name); }
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static void Cardinalities()
    {
        var (p, c, _) = Board("SZG_CARDINALITIES");
        foreach (var canonical in new CardModel[] { ModelDb.Card<Accuracy>(), ModelDb.Card<Shiv>(),
            ModelDb.Card<StrikeSilent>(), ModelDb.Card<DefendSilent>(), ModelDb.Card<Backflip>(),
            ModelDb.Card<Neutralize>(), ModelDb.Card<Survivor>(), ModelDb.Card<Footwork>(),
            ModelDb.Card<Acrobatics>(), ModelDb.Card<Tactician>() })
            p.PlayerCombatState!.Hand.AddInternal(c.CreateCard(canonical, p));
        var root = KernelSession.Capture(c);
        var options = root.Simulator.State.GetPlayerCombatState(p).Hand.Cards.ToArray();
        var choices = KernelRelicChoices.ReplacementChoices(root.Simulator,
            new(PlanChoiceEffect.DiscardAndDraw, PileType.Hand, 0, 10, options, options, 0));
        Check(choices.Select(q => q.Cards.Count).Distinct().Order().SequenceEqual(Enumerable.Range(0, 11)),
            "GamblingChip must admit every legal cardinality including no replacement and full replacement");
        Check(choices.Count <= 12 && choices.All(q => q.Cards.Count <= 10),
            "variable replacement must remain bounded with actual native tokens");
    }

    private static void NativeReplacement()
    {
        var (p, c, _) = Board("SZG_NATIVE_REPLACEMENT");
        var bane = Hand<AscendersBane>(p, c); var dead = Hand<Wound>(p, c);
        var shiv = c.CreateCard<Shiv>(p); p.PlayerCombatState!.DrawPile.AddInternal(shiv);
        p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<StrikeSilent>(p));
        var root = KernelSession.Capture(c); var original = root.StateText(p);
        var selected = root.SelectRelicCards(p, root.Hand(p), "DiscardAndDraw", 0, 2, "GamblingChip");
        Check(selected.Contains(dead) && !selected.Contains(bane),
            "native replacement must replace lasting Wound while retaining harmless Ethereal cleanup");
        Check(root.LastHandChoiceComparison.Contains("selected=complete-native-hand"),
            "GamblingChip must use completed native hands instead of independent card-value fallback");
        Check(root.StateText(p) == original, "native replacement comparison must preserve its parent and RNG");

        // Actual sts2.dll effect, with selection prepared by the production
        // policy. Hand movement and draw hooks are native, not predicted helpers.
        var chip = ModelDb.Relic<GamblingChip>().ToMutable(); p.AddRelicInternal(chip);
        var selector = new TestCardSelector(); selector.PrepareToSelect(selected);
        using var turn = new NativeTurn(c);
        using (CardSelectCmd.PushSelector(selector))
            chip.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), p).GetAwaiter().GetResult();
        Check(p.PlayerCombatState.DiscardPile.Cards.Contains(dead)
            && p.PlayerCombatState.Hand.Cards.Contains(bane)
            && p.PlayerCombatState.Hand.Cards.Contains(shiv),
            "real GamblingChip must apply the production partial replacement and draw the native card");
    }

    private static void RetainSynergy()
    {
        var (p, c, e) = Board("SZG_RETAIN_ENGINE");
        e.SetCurrentHpInternal(20);
        var accuracy = Hand<Accuracy>(p, c, true);
        var a = Hand<Shiv>(p, c); var b = Hand<Shiv>(p, c);
        for (var i = 0; i < 8; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<Dazed>(p));
        comparedOptions.Clear(); observeRollouts = true;
        IReadOnlyList<CardModel> selected;
        try { selected = BotRelicChoices.Select(p, p.PlayerCombatState!.Hand.Cards.ToArray(),
            0, 999999999, "FromHand:relic:GamblingChip")!; }
        finally { observeRollouts = false; }
        Check(comparedOptions.Count > 0 && comparedOptions.All(o => o.SequenceLookahead && !o.SequenceHandLookahead),
            "production native hand comparison must enable bounded dependency ordering without nesting whole-hand searches");
        Check(!selected.Contains(accuracy) && !selected.Contains(a) && !selected.Contains(b),
            "production GamblingChip selector must retain the affordable Accuracy plus Shiv native lethal sequence");
        var root = KernelSession.Capture(c);
        var first = root.CardBranches(accuracy, null, 1).Single(q => q.Boundary.Length == 0).State;
        var second = first.CardBranches(a, e, 1).Single(q => q.Boundary.Length == 0).State;
        var last = second.CardBranches(b, e, 1).Single(q => q.Boundary.Length == 0).State;
        Check(last.HasWon, "retained native power/attack dependency must actually defeat the enemy");
    }

    private static void HiddenOrder()
    {
        var (p, c, _) = Board("SZG_HIDDEN_POOL");
        Hand<StrikeSilent>(p, c); Hand<Dazed>(p, c);
        p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<Backflip>(p));
        p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<Shiv>(p));
        p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<DefendSilent>(p));
        var first = KernelSession.Capture(c);
        var selected = first.SelectRelicCards(p, first.Hand(p), "DiscardAndDraw", 0, 2, "GamblingChip")
            .Select(q => q.Id.Entry).ToArray();
        var draw = p.PlayerCombatState.DrawPile.Cards.ToArray();
        foreach (var card in draw) p.PlayerCombatState.DrawPile.RemoveInternal(card);
        foreach (var card in draw.Reverse()) p.PlayerCombatState.DrawPile.AddInternal(card);
        var second = KernelSession.Capture(c); var before = second.StateText(p);
        var other = second.SelectRelicCards(p, second.Hand(p), "DiscardAndDraw", 0, 2, "GamblingChip")
            .Select(q => q.Id.Entry).ToArray();
        Check(selected.SequenceEqual(other) && second.StateText(p) == before,
            "GamblingChip must compare a common known pool without reading or rewriting the hidden top order");
    }

    private static void CaptureControl()
    {
        var prior = AutoPilot.HandedOver.ToArray();
        try
        {
            AutoPilot.Clear();
            var botId = BotRegistry.CreateId(BotDifficulty.Pro, 44, 1);
            const ulong handedId = 90000000000000003, humanId = 90000000000000004;
            var players = new[] { botId, handedId, humanId }
                .Select(id => Player.CreateForNewRun<Deprived>(UnlockState.all, id)).ToArray();
            var combat = new CombatState(runState: RunState.CreateForTest(players, seed: "SZG_CAPTURE_CONTROL"));
            foreach (var player in players)
            {
                player.ResetCombatState(); combat.AddPlayer(player);
                player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
                player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
                player.PlayerCombatState.GainEnergy(3);
            }
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
            enemy.SetMaxHpInternal(600); enemy.SetCurrentHpInternal(600);
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
            AutoPilot.Set(handedId, true);
            var captured = KernelCombatCapture.Capture(combat);
            var field = AccessTools.Field(typeof(KernelSession), "synchronousChoicePlayers");
            HashSet<ulong> Snapshot(KernelSession session) => (HashSet<ulong>)field.GetValue(session)!;
            Check(Snapshot(captured).Contains(botId), "production capture must snapshot synthetic Bot synchronous selectors");
            Check(Snapshot(captured).Contains(handedId), "production capture must snapshot handed-over human synchronous selectors");
            Check(!Snapshot(captured).Contains(humanId), "ordinary human selectors must remain deferred");
            riskSnapshots.Clear(); observeRiskCapture = true;
            LiveEndTurnRisk.Result risk;
            try { risk = LiveEndTurnRisk.Evaluate(combat, players, 2, KernelCombatCapture.SynchronousSeats(combat)); }
            finally { observeRiskCapture = false; }
            Console.WriteLine($"CAPTURE RAW simulated={risk.Simulated} hpLost={risk.HpLost} deaths={risk.Deaths} snapshots={riskSnapshots.Count} sets=[{string.Join(";",riskSnapshots.Select(s=>string.Join(",",s.Order())))}]");
            Check(risk.Simulated && riskSnapshots.Count == 1
                && riskSnapshots.Single().SetEquals(new[] { botId, handedId }),
                "live end-turn risk must carry the same synchronous control snapshot into its next native side");
            AutoPilot.Clear(); AutoPilot.Set(humanId, true);
            var fork = captured.Fork();
            Check(Snapshot(captured).SetEquals(new[] { botId, handedId })
                && Snapshot(fork).SetEquals(Snapshot(captured))
                && !ReferenceEquals(Snapshot(captured), Snapshot(fork)),
                "captured and forked choice timing must preserve the original control snapshot independently of live handover");
        }
        finally
        {
            AutoPilot.Clear(); foreach (var id in prior) AutoPilot.Set(id, true);
        }
    }

    private sealed class NativeTurn : IDisposable
    {
        private readonly FieldInfo field = AccessTools.Field(typeof(CombatManager), "_turnState");
        private readonly object? saved;
        internal NativeTurn(CombatState c)
        {
            saved = field.GetValue(CombatManager.Instance);
            var value = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [c], null)!;
            AccessTools.Property(field.FieldType, "IsInProgress").SetValue(value, true);
            AccessTools.Property(field.FieldType, "IsStarting").SetValue(value, false);
            field.SetValue(CombatManager.Instance, value);
        }
        public void Dispose() => field.SetValue(CombatManager.Instance, saved);
    }
    private static (Player, CombatState, Creature) Board(string seed)
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 90000000000000005);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: seed));
        p.ResetCombatState(); c.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(600); e.SetCurrentHpInternal(600);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (p, c, e);
    }
    private static T Hand<T>(Player p, CombatState c, bool upgraded = false) where T : CardModel
    { var card = c.CreateCard<T>(p); if (upgraded) card.UpgradeInternal(); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void ObserveRollout(RolloutOptions options)
    { if (observeRollouts) comparedOptions.Add(options); }
    private static void WarmRiskEvaluation()
    {
        var warmPresentation = new Harmony("coopbots.szg-choice.warm-presentation");
        var formatMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        warmPresentation.Patch(formatMethod,prefix:new HarmonyMethod(typeof(SzgChoiceRepairScenarios),nameof(Format)));
        try {
        var (player,combat,_) = Board("SZG_CAPTURE_WARM");
        var warm = LiveEndTurnRisk.Evaluate(combat,new[]{player},2,Array.Empty<ulong>());
        Console.WriteLine($"CAPTURE WARM simulated={warm.Simulated} loss={warm.HpLost}");
        Check(warm.Simulated,"warm native end-turn risk must actually run before installing capture observation");
        } finally { warmPresentation.Unpatch(formatMethod,HarmonyPatchType.All,warmPresentation.Id); }
    }
    private static void ObserveActualNextSide(KernelSession __instance)
    {
        if (observeRiskCapture)
            riskSnapshots.Add(new((HashSet<ulong>)AccessTools.Field(typeof(KernelSession), "synchronousChoicePlayers").GetValue(__instance)!));
    }
    private static void Check(bool condition, string why)
    { if (!condition) throw new Exception("SZG choice repair: " + why); }
}
