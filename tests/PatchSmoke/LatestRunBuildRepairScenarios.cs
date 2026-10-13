using System.Reflection;
using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Latest run 1JJ0XFWWNLTC: RelicTrader L53108-53121; Tactician L16323,
// L45191/L52958; Prepared L30950; TrueGrit burns Turbo+ L55688.
// R2: isolated production-source faults, each build0 -> named RED1 ->
// exact byte restore -> build0/GREEN0. Full commands, SHA256 and raw logs:
// work/latest-run-repair-20261008/build/r2-all-results.json.
// exchange-sign (exchange-sign-red.log):
// System.InvalidOperationException: Latest run build: each native trade index must price obtain minus remove, never two gains (10.00000000 vs -2.50000000)
// exchange-index (exchange-index-red.log):
// System.InvalidOperationException: Latest run build: each native trade index must price obtain minus remove, never two gains (1.25000000 vs -2.50000000)
// exchange-wiring (exchange-wiring-red.log):
// System.InvalidOperationException: Latest run build: each native trade index must price obtain minus remove, never two gains (40.00000000 vs -2.50000000)
// exchange-log (exchange-log-red.log):
// System.InvalidOperationException: Latest run build: exchange logging must read initialized pairs without advancing the native reward RNG
// ranwid-cost (ranwid-cost-red.log):
// System.InvalidOperationException: Latest run build: the other native relic-payment hover must be charged as surrender rather than an extra acquisition (25.00000000 vs -6.25000000)
// feather-window (feather-window-red.log):
// System.InvalidOperationException: Latest run build: EternalFeather must use native floor(deck/5)*3 at the one remaining camp (36.00000000 vs 18.00000000)
// spent-pickup (spent-pickup-red.log):
// System.InvalidOperationException: Latest run build: surrendering a pickup-only relic must not charge already received gold again (10.00000000 vs 0.00000000)
// recurring-window (recurring-window-red.log):
// System.InvalidOperationException: Latest run build: recurring relic loss must price only the two remaining fights (25.00000000 vs 6.25000000)
// automatic-receipt (automatic-receipt-red.log):
// System.InvalidOperationException: Latest run build: construction receipt must distinguish an actual free Sly execution from printed three-cost payment
// receipt-aggregation (receipt-aggregation-red.log):
// System.InvalidOperationException: Latest run build: reachable payable discard/Sly receipts must reduce the shared deck commitment term
// printed-risk (printed-risk-red.log):
// System.InvalidOperationException: Latest run build: reachable payable discard/Sly receipts must reduce the shared deck commitment term
// free-without-path (free-without-path-red.log):
// System.InvalidOperationException: Latest run build: without a reachable discard path Sly must retain its full printed-cost commitment risk (0.00000000 vs 1.00000000)
// commitment-wiring (commitment-wiring-red.log):
// System.InvalidOperationException: Latest run build: actual DeckStrength must use the same Sly payment receipts as construction hands
// ordinary-risk (ordinary-risk-red.log):
// System.InvalidOperationException: Latest run build: ordinary three-cost cards must retain the owner's entire-turn commitment policy (0.00000000 vs 1.00000000)
// exhaust-control (exhaust-control-red.log):
// System.InvalidOperationException: Latest run build: controlled exhaust must preserve the actual later-payable core instead of tying it with harmless Wound
// retention-wiring (retention-wiring-red.log):
// System.InvalidOperationException: Latest run build: controlled exhaust must preserve the actual later-payable core instead of tying it with harmless Wound
// planning-utility (planning-utility-red.log):
// System.InvalidOperationException: Latest run build: exhaust retention is target planning only and must not be charged again as immediate utility (-2.30000000 vs -1.30000000)
// prepared-order (prepared-order-red.log):
// System.InvalidOperationException: Latest run build: actual construction pipeline must reproduce gross draw before the mandatory discard without inventing net hand advantage
// prepared-double-prior (prepared-double-prior-red.log):
// System.InvalidOperationException: Latest run build: another filtering copy must not receive the original first-copy community support again
// pool-nested-reuse (pool-nested-reuse-red.log):
// System.InvalidOperationException: Latest run build: bounded nested exhaust continuations must reuse scratch within the existing two-MiB allocation budget
// pool-thread-isolation (pool-thread-isolation-red.log):
// System.InvalidOperationException: Latest run build: parallel pure workers must not share mutable exhaust search scratch
// Commands/manifests/logs belong to work/latest-run-repair-20261008/build.
internal static class LatestRunBuildRepairScenarios
{
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.latest-run-build.presentation");
        presentation.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText)),
            prefix: new HarmonyMethod(typeof(LatestRunBuildRepairScenarios), nameof(Format)));
        // Preserve the original power/relic HoverTip constructors and models;
        // only their texture lookup is unavailable in this console host.
        presentation.Patch(AccessTools.PropertyGetter(typeof(PowerModel), "Icon"),
            prefix: new HarmonyMethod(typeof(LatestRunBuildRepairScenarios), nameof(NoPowerIcon)));
        try
        {
            var cases = new (string Name, Action Run)[] {
                ("relic-exchange", RelicExchange), ("relic-window", RelicWindow),
                ("sly-payment", SlyPayment), ("sly-deck", SlyDeck),
                ("exhaust-control", ExhaustControl), ("exhaust-upgrade", ExhaustUpgrade), ("exhaust-reuse", ExhaustReuse), ("upgrade-policy", UpgradePolicy),
                ("prepared-native", PreparedNative), ("prepared-live", PreparedLive) };
            if (caseName is not null && !cases.Any(c => c.Name == caseName))
                throw new InvalidOperationException("Unknown latest run build case: " + caseName);
            foreach (var test in cases.Where(c => caseName is null || c.Name == caseName))
            { test.Run(); Console.WriteLine("PASS: latest run build " + test.Name); }
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }
    private static bool Format(LocString __instance, ref string __result) { __result = __instance.LocEntryKey; return false; }
    private static bool NoPowerIcon(ref Godot.Texture2D? __result) { __result = null; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException("Latest run build: " + why); }
    private static void Near(double actual, double expected, string why)
        => Check(Math.Abs(actual - expected) < 1e-8, why + $" ({actual:F8} vs {expected:F8})");
    private static (Player Player, CombatState Combat, Creature Enemy) Board(string seed = "LATEST-RUN-BUILD", int energy = 3, int count = 1)
    {
        var party = Enumerable.Range(0, count).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(1940+i))).ToArray();
        var run = RunState.CreateForTest(party, ascensionLevel: 10, seed: seed);
        var combat = new CombatState(runState: run);
        foreach (var p in party)
        {
            p.MaxEnergy = energy; p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(energy);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "build-payment-oracle");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(1000); enemy.SetCurrentHpInternal(1000);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party[0], combat, enemy);
    }
    private static CardModel Card(string id, Player p, bool upgraded = false)
    {
        var c = ModelDb.AllCards.Single(x => x.Id.Entry == id).ToMutable(); c.Owner = p;
        if (upgraded) { c.UpgradeInternal(); c.FinalizeUpgradeInternal(); }
        return c;
    }
    private static T Hand<T>(CombatState c, Player p, bool upgraded = false) where T : CardModel
    {
        var card = c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card);
        if (upgraded) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
        return card;
    }
    private static ConstructionHand.Slot Slot(CardModel c) => new(c, CardProfile.Of(c), CardProfile.Of(c).Unplayable);
    private sealed class LastCamp : ActMap
    {
        public override MapPoint StartingMapPoint { get; } = new(0,0) { PointType = MapPointType.Unknown };
        internal MapPoint Camp { get; } = new(0,1) { PointType = MapPointType.RestSite };
        public override MapPoint BossMapPoint { get; } = new(0,2) { PointType = MapPointType.Boss };
        public override MapPoint SecondBossMapPoint { get; } = new(0,3) { PointType = MapPointType.Boss };
        protected override MapPoint?[,] Grid { get; } = new MapPoint?[1,1];
        internal LastCamp() { StartingMapPoint.AddChildPoint(Camp); Camp.AddChildPoint(BossMapPoint); BossMapPoint.AddChildPoint(SecondBossMapPoint); }
    }
    private static LastCamp EndMap(Player p)
    {
        var run = (RunState)p.RunState; run.CurrentActIndex = run.Acts.Count - 1;
        var map = new LastCamp(); run.Map = map; run.AddVisitedMapCoord(map.StartingMapPoint.coord); return map;
    }
    private static void RelicExchange()
    {
        var (p, _, _) = Board(); EndMap(p);
        var model = ModelDb.Event<RelicTrader>().ToMutable();
        typeof(EventModel).GetProperty(nameof(EventModel.Owner))!.SetValue(model, p);
        RelicModel[] old = [ModelDb.Relic<BagOfPreparation>().ToMutable(), ModelDb.Relic<Vajra>().ToMutable(), ModelDb.Relic<BloodVial>().ToMutable()];
        RelicModel[] received = [ModelDb.Relic<ParryingShield>(), ModelDb.Relic<OddlySmoothStone>(), ModelDb.Relic<Anchor>()];
        foreach (var relic in old) p.AddRelicInternal(relic);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RelicTrader).GetField("_ownedRelics", flags)!.SetValue(model, old);
        typeof(RelicTrader).GetField("_newRelics", flags)!.SetValue(model, received);
        var options = new[] { "TOP", "MIDDLE", "BOTTOM" }.Select((key, i) => new EventOption(model, () => Task.CompletedTask,
            new LocString("events", "test.title"), new LocString("events", "test.description"),
            "RELIC_TRADER.pages.INITIAL.options." + key, old[i].HoverTips.Concat(received[i].HoverTips))).ToList();
        typeof(EventModel).GetField("_currentOptions", flags)!.SetValue(model, options);
        var rng = p.RunState.Rng.Niche.ToSerializable();
        var details = new List<string>();
        for (var i = 0; i < options.Count; i++)
        {
            var expected = EventTrade.RemainingRelicValue(p, received[i], true) - EventTrade.RemainingRelicValue(p, old[i], false);
            var value = EventTrade.Grants(p, model, options[i]);
            details.Add(value.Detail);
            Near(value.Value, expected, "each native trade index must price obtain minus remove, never two gains");
            Check(value.Detail.Contains("remove:" + old[i].Id.Entry) && value.Detail.Contains(received[i].Id.Entry)
                && value.Detail.Contains("net="), "exchange explanation must identify both exact indexed relics and signed net");
        }
        var pick = (int)typeof(BotEventDriver).GetMethod("ChooseEventOption", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [p, model])!;
        Check(pick == 2, "actual generic event chooser must prefer the profitable exchange rather than the largest old-plus-new hover total");
        Check(EventHandlers.Explain(p, model).Contains("BAG_OF_PREPARATION") && p.RunState.Rng.Niche.ToSerializable().counter == rng.counter,
            "exchange logging must read initialized pairs without advancing the native reward RNG");
        var ranwid = ModelDb.Event<RanwidTheElder>().ToMutable();
        typeof(EventModel).GetProperty(nameof(EventModel.Owner))!.SetValue(ranwid, p);
        var payment = new EventOption(ranwid, () => Task.CompletedTask, new LocString("events", "test.title"),
            new LocString("events", "test.description"), "RANWID_THE_ELDER.pages.INITIAL.options.RELIC", old[0].HoverTips);
        var rolls = OutOfCombatPredictions.EventOption(ranwid, payment).Relics;
        Near(EventTrade.Grants(p, ranwid, payment).Value,
            rolls.Sum(r => EventTrade.RemainingRelicValue(p, r, true)) - EventTrade.RemainingRelicValue(p, old[0], false),
            "the other native relic-payment hover must be charged as surrender rather than an extra acquisition");
        // Run the original native Trade(index), including real Remove/Obtain.
        // Neither its transaction nor relic effects are patched by this fixture.
        ((Task)typeof(RelicTrader).GetMethod("Trade", flags)!.Invoke(model, [1])!).GetAwaiter().GetResult();
        Check(!p.Relics.Contains(old[1]) && p.Relics.Any(r => r.Id == received[1].Id) && p.Relics.Contains(old[0]) && p.Relics.Contains(old[2]),
            "native oracle must surrender only the same-index old relic and obtain its new partner");
        Console.WriteLine("RELIC exchange " + string.Join(";", details));
    }
    private static void RelicWindow()
    {
        var (p, _, _) = Board(); var map = EndMap(p);
        for (var i = 0; i < 30; i++) p.Deck.AddInternal(Card("STRIKE_SILENT", p));
        var feather = ModelDb.Relic<EternalFeather>().ToMutable(); p.AddRelicInternal(feather);
        p.Creature.SetCurrentHpInternal(40);
        Near(EventTrade.RemainingRelicValue(p, feather, false), 18,
            "EternalFeather must use native floor(deck/5)*3 at the one remaining camp");
        feather.AfterRoomEntered(new RestSiteRoom()).GetAwaiter().GetResult();
        Near((double)p.Creature.CurrentHp, 58, "native camp oracle must actually realize the same eighteen healing");
        Near(EventTrade.RemainingRelicValue(p, ModelDb.Relic<OldCoin>(), false), 0,
            "surrendering a pickup-only relic must not charge already received gold again");
        var bag = EventTrade.RemainingRelicValue(p, ModelDb.Relic<BagOfPreparation>(), false);
        Near(bag, EventTrade.Relic(p, ModelDb.Relic<BagOfPreparation>()) * 2 / 8,
            "recurring relic loss must price only the two remaining fights");
        ((RunState)p.RunState).AddVisitedMapCoord(map.SecondBossMapPoint!.coord);
        Near(EventTrade.RemainingRelicValue(p, feather, true), 0, "a relic with no remaining camp cannot receive full-run healing credit");
        Near(EventTrade.RemainingRelicValue(p, ModelDb.Relic<BagOfPreparation>(), false), 0,
            "no future opening means no future BagOfPreparation surrender cost");
    }
    private static void SlyPayment()
    {
        var (p, c, e) = Board(); var prep = Hand<Prepared>(c, p); var tact = Hand<Tactician>(c, p);
        var bludgeon = Hand<Bludgeon>(c, p); var strike = Hand<StrikeIronclad>(c, p);
        var root = KernelSession.Capture(c); var native = root.ForkForReplay();
        var branch = native.CardBranches(prep, null, 8).First(b => b.Boundary.Length == 0 && b.Choices.Any(q => q.Cards.Any(t => t.Id == tact.Id.Entry)));
        Check(branch.State.Energy(p) == 4 && branch.State.Play(bludgeon, e, out var why) && branch.State.Play(strike, e, out why)
            && branch.State.Hp(e) == 962 && branch.State.Energy(p) == 0,
            "native discard must autoplay three-cost Sly for zero payment and fund the additional paid attack");
        var result = ConstructionHand.Solve([Slot(prep), Slot(tact), Slot(bludgeon), Slot(strike)], 4, new(3, 26, 1000));
        Check(result.Damage == 38 && result.EnergySpent == 4 && (result.AutomaticPlayed & 2) != 0,
            "construction receipt must distinguish an actual free Sly execution from printed three-cost payment");
        Check(root.Energy(p) == 3 && root.Hp(e) == 1000 && root.Hand(p).Count == 4, "native payment witnesses must preserve frozen root and live hand");
    }
    private static void SlyDeck()
    {
        var (p, _, _) = Board(); var tact = Card("TACTICIAN", p); var prep = Card("PREPARED", p);
        var bludgeon = Card("BLUDGEON", p); var strike = Card("STRIKE_IRONCLAD", p);
        var unsupported = new[] { tact, bludgeon, strike };
        var supported = new[] { prep, tact, bludgeon, strike };
        var full = DeckStrength.CommitmentLoad(tact, 3);
        Near(SlyCommitment.Load(unsupported, 3), full, "without a reachable discard path Sly must retain its full printed-cost commitment risk");
        var paid = SlyCommitment.Load(supported, 3);
        Check(paid < full, "reachable payable discard/Sly receipts must reduce the shared deck commitment term");
        var before = DeckStrength.Evaluate(unsupported, p); var after = DeckStrength.Evaluate(supported, p);
        Check(after.Commitment < before.Commitment, "actual DeckStrength must use the same Sly payment receipts as construction hands");
        Near(DeckStrength.CommitmentLoad(bludgeon, 3), 1, "ordinary three-cost cards must retain the owner's entire-turn commitment policy");
        Near(DeckStrength.CommitmentLoad(tact, 3), 1, "the raw printed-cost helper must remain honest for manual Sly payment");
        Console.WriteLine($"SLY supported-load={paid:F6} raw={full:F6} commitment={before.Commitment:F6}->{after.Commitment:F6}");
    }
    private static void ExhaustControl()
    {
        var (p, c, _) = Board(); var grit = Hand<TrueGrit>(c, p, true); var wound = Hand<Wound>(c, p);
        var core = Hand<Bludgeon>(c, p); var a = Hand<DefendIronclad>(c, p, true); var b = Hand<DefendIronclad>(c, p, true);
        var root = KernelSession.Capture(c); var native = root.ForkForReplay();
        var clean = native.CardBranches(grit, null, 8).First(x => x.Boundary.Length == 0 && x.State.Exhaust(p).Any(t => t is Wound));
        Check(clean.State.Energy(p) == 2 && clean.State.Block(p.Creature) == 9 && clean.State.Hand(p).Any(t => t.Id == core.Id),
            "native upgraded TrueGrit must pay one, gain nine block and legally preserve the chosen core");
        var input = new[] { Slot(grit), Slot(wound), Slot(core), Slot(a), Slot(b) };
        var selected = ConstructionHand.Solve(input, 5, new(3, 26, 1000));
        Check((selected.Exhausted & 2) != 0 && (selected.Exhausted & 4) == 0 && selected.Block == 25,
            "controlled exhaust must preserve the actual later-payable core instead of tying it with harmless Wound");
        Near(selected.Utility, -1.3, "exhaust retention is target planning only and must not be charged again as immediate utility");
        var randomCore = false;
        for (var i = 0; i < 16 && !randomCore; i++)
        {
            var (q, d, _) = Board("NATIVE-RANDOM-GRIT-" + i); var plain = Hand<TrueGrit>(d, q); Hand<Wound>(d, q); Hand<Bludgeon>(d, q);
            var oracle = KernelSession.Capture(d).ForkForReplay();
            Check(oracle.Play(plain, null, out _), "native random TrueGrit must execute on the same legal hand family");
            randomCore = oracle.Exhaust(q).Any(x => x is Bludgeon);
            Check(oracle.Block(q.Creature) == 7 && oracle.Energy(q) == 2, "random-to-control upgrade must retain the native seven-to-nine block delta and same cost");
        }
        Check(randomCore, "the original native random exhaust must have an actual reachable core-loss outcome");
        Check(root.Exhaust(p).Count == 0 && root.Hand(p).Count == 5, "exhaust target experiments must preserve the root");
    }
    private static void ExhaustUpgrade()
    {
        var (p, _, _) = Board(); var grit = Card("TRUE_GRIT", p);
        var deck = new[] { grit, Card("WOUND", p), Card("BLUDGEON", p), Card("DEFEND_IRONCLAD", p, true), Card("DEFEND_IRONCLAD", p, true) };
        foreach (var card in deck) p.Deck.AddInternal(card);
        var before = DeckStrength.Evaluate(deck, p); var clone = CardProfile.Upgraded(grit)!.Value.Card;
        var after = DeckStrength.Evaluate(deck.Select(c => ReferenceEquals(c, grit) ? clone : c).ToArray(), p);
        var delta = BuildValue.UpgradeDelta(grit, p); var decision = BuildValue.UpgradeForDecision(grit, p);
        Near(delta.Total, after.Total - before.Total, "TrueGrit upgrade consumers must price the native changed deck and shared exhaust-control pipeline");
        Near(decision.Total, delta.Total, "exhaust control must not be a hidden special upgrade bonus outside the deck potential");
        Check(!grit.IsUpgraded && grit.DynamicVars.Block.BaseValue == 7 && clone.IsUpgraded && clone.DynamicVars.Block.BaseValue == 9,
            "upgraded control must use the isolated native seven-to-nine clone without changing the held random card");
        Console.WriteLine($"TRUE_GRIT native-upgrade={delta.Total:F6} cycle={after.Cycle-before.Cycle:F6} hands={after.Hands-before.Hands:F6} reason={delta.Reason}");
    }
    private static void ExhaustReuse()
    {
        var (p, _, _) = Board();
        var input = new[] { Slot(Card("TRUE_GRIT", p, true)), Slot(Card("WOUND", p)), Slot(Card("BLUDGEON", p)),
            Slot(Card("DEFEND_IRONCLAD", p, true)), Slot(Card("DEFEND_IRONCLAD", p, true)) };
        var context = new ConstructionHand.Context(3, 26, 1000);
        using var budget = ConstructionBudget.Use(ConstructionBudget.For(KernelCombatPlanner.TournamentPerf.High));
        for (var i = 0; i < 8; i++) ConstructionHand.Solve(input, 5, context);
        var expected = ConstructionHand.Solve(input, 5, context);
        var start = GC.GetAllocatedBytesForCurrentThread();
        // Same repeated-call count and limit as ConstructionPerformanceScenarios.
        for (var i = 0; i < 64; i++)
        {
            var actual = ConstructionHand.Solve(input, 5, context);
            Check(actual.Exhausted == expected.Exhausted && actual.Block == expected.Block && actual.Utility == expected.Utility,
                "reused nested scratch must preserve actual controlled-exhaust results across repeated calls");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"EXHAUST allocation-measurement calls=64 allocated={allocated}");
        Check(allocated < 2 * 1024 * 1024,
            "bounded nested exhaust continuations must reuse scratch within the existing two-MiB allocation budget");
        using var barrier = new Barrier(4);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() => {
            using var local = ConstructionBudget.Use(ConstructionBudget.For(KernelCombatPlanner.TournamentPerf.High));
            barrier.SignalAndWait();
            for (var i = 0; i < 16; i++)
            {
                var actual = ConstructionHand.Solve(input, 5, context);
                Check(actual.Exhausted == expected.Exhausted && actual.Block == expected.Block && actual.Utility == expected.Utility,
                    "parallel pure workers must not share mutable exhaust search scratch");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
        catch (Exception error) { throw new InvalidOperationException("Latest run build: parallel pure workers must not share mutable exhaust search scratch", error); }
        Console.WriteLine($"EXHAUST nested reuse calls=64 allocated={allocated} thread-results=4x16 equivalent");
    }
    private static void UpgradePolicy()
    {
        var (p, _, _) = Board();
        foreach (var id in Enumerable.Repeat("STRIKE_IRONCLAD", 5).Concat(Enumerable.Repeat("DEFEND_IRONCLAD", 4))
            .Concat(new[] { "BASH", "SETUP_STRIKE", "POMMEL_STRIKE" })) p.Deck.AddInternal(Card(id, p));
        var values = BuildValue.EligibleUpgrades(p.Deck.Cards).Select(c => (Card: c, Value: BuildValue.UpgradeForDecision(c, p))).ToArray();
        var ranked = BuildValue.RankUpgrades(values).ToArray();
        Check(ranked[0].Card is PommelStrike && values.Where(v => v.Card is Bash or SetupStrike).All(v => v.Value.UpgradePriority == 0),
            "the owner's Pommel before Bash/Setup policy tier must survive the exhaust and payment mechanism repairs");
        foreach (var pair in values)
            Near(pair.Value.Total, BuildValue.UpgradeDelta(pair.Card, p).Total, "policy priority must not falsify permanent upgrade model prices");
    }
    private static void PreparedNative()
    {
        foreach (var upgraded in new[] { false, true })
        {
            var (p, c, _) = Board(); var prep = Hand<Prepared>(c, p, upgraded);
            for (var i = 0; i < 4; i++) Hand<StrikeSilent>(c, p);
            for (var i = 0; i < 2; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<DefendSilent>(p));
            var root = KernelSession.Capture(c); var branch = root.ForkForReplay().CardBranches(prep, null, 8).First(b => b.Boundary.Length == 0);
            Check(branch.State.Hand(p).Count == 4 && branch.Choices.Single().Cards.Count == (upgraded ? 2 : 1),
                "native Prepared must draw then pay its equally sized mandatory discard, retaining minus one net hand card");
            Near(CardProfile.NetDraw(prep, CardProfile.Of(prep)), 0,
                "Prepared and its upgrade must not claim extra net draw merely because the gross draw increased");
            var (q, d, e) = Board(energy: 1); var filter = Hand<Prepared>(d, q, upgraded); var wound = Hand<Wound>(d, q);
            var drawn = new List<CardModel>();
            if (upgraded) drawn.Add(d.CreateCard<Wound>(q));
            drawn.Add(d.CreateCard<StrikeIronclad>(q));
            foreach (var card in drawn) q.PlayerCombatState!.DrawPile.AddInternal(card);
            var oracle = KernelSession.Capture(d).ForkForReplay().CardBranches(filter, null, 8)
                .First(b => b.Boundary.Length == 0 && b.State.Hand(q).Any(x => x is StrikeIronclad));
            Check(oracle.State.Play(oracle.State.Hand(q).OfType<StrikeIronclad>().Single(), e, out _) && oracle.State.Hp(e) == 994,
                "native Prepared must reveal the payable attack before selecting the existing Wounds for mandatory discard");
            var sampled = new[] { Slot(filter), Slot(wound) }.Concat(drawn.Select(Slot)).ToArray();
            var hand = ConstructionHand.Solve(sampled, 2, new(1, 26, 1000));
            Check(hand.Damage == 6 && hand.EnergySpent == 1 && hand.Removed != 0,
                "actual construction pipeline must reproduce gross draw before the mandatory discard without inventing net hand advantage");
        }
        foreach (var skill in new[] { false, true })
        {
            var (p, c, _) = Board(); var escape = Hand<EscapePlan>(c, p);
            for (var i = 0; i < 4; i++) Hand<StrikeSilent>(c, p);
            p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard(skill ? ModelDb.Card<DefendSilent>() : ModelDb.Card<StrikeSilent>(), p));
            var native = KernelSession.Capture(c).ForkForReplay();
            Check(native.Play(escape, null, out _) && native.Hand(p).Count == 5 && native.Block(p.Creature) == (skill ? 3 : 0),
                "native EscapePlan comparison must have neutral hand count and block only when the actual draw is a Skill");
        }
    }
    private static void PreparedLive()
    {
        var (p, _, _) = Board("1JJ0XFWWNLTC", count: 4);
        typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(p, ModelDb.Character<Silent>());
        ((RunState)p.RunState).CurrentActIndex = 1; p.Creature.SetMaxHpInternal(84); p.Creature.SetCurrentHpInternal(41);
        // The actual BEFORE-floor25 deck; CalculatedGamble is acquired later.
        string[] ids = ["STRIKE_SILENT", "STRIKE_SILENT", "STRIKE_SILENT", "STRIKE_SILENT", "AFTERIMAGE",
            "DEFEND_SILENT", "DEFEND_SILENT", "DEFEND_SILENT", "NEUTRALIZE", "SURVIVOR", "ASCENDERS_BANE", "SIDESTEP",
            "DEADLY_POISON+", "PIERCING_WAIL", "SNAKEBITE+", "HIDDEN_DAGGERS", "ASSASSINATE+", "BLADE_SYMPHONY+", "EXPOSE",
            "CLOAK_AND_DAGGER", "ACCURACY", "BLUR", "SLICE", "DAGGER_THROW"];
        foreach (var id in ids) p.Deck.AddInternal(Card(id.TrimEnd('+'), p, id.EndsWith('+')));
        using var budget = ConstructionBudget.Use(ConstructionBudget.For(KernelCombatPlanner.TournamentPerf.High));
        using var cache = DeckStrength.BeginEvaluation();
        var before = DeckStrength.Evaluate(p.Deck.Cards, p); var prepared = Card("PREPARED", p); var escape = Card("ESCAPE_PLAN", p);
        var after = DeckStrength.Evaluate(p.Deck.Cards.Append(prepared).ToArray(), p);
        var value = BuildValue.AddForDecision(prepared, p); var alternative = BuildValue.AddForDecision(escape, p);
        Near(value.Total, after.Total - before.Total, "Prepared reward must be the actual shared absolute potential delta, without a separate acquisition bonus");
        Check(after.Prior > before.Prior && !p.Deck.Cards.Any(c => c is CalculatedGamble),
            "historical Prepared attribution must expose the existing first-copy prior and exclude the later Gamble");
        var duplicate = DeckStrength.Evaluate(p.Deck.Cards.Append(prepared).Append(Card("PREPARED", p)).ToArray(), p);
        Check(duplicate.Prior - after.Prior < after.Prior - before.Prior,
            "another filtering copy must not receive the original first-copy community support again");
        Console.WriteLine($"PREPARED reconstructed delta={value.Total:F6} combat={after.Combat-before.Combat:F6} prior={after.Prior-before.Prior:F6} escape={alternative.Total:F6} reason={value.Reason}");
        Console.WriteLine("PREPARED limits: exact prior deck/upgrade/HP/A10/act/count; current map, relics and live RNG are not available, so this is attribution rather than a complete historical decision replay.");
    }
}
