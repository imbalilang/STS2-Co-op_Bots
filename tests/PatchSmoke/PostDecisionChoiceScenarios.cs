using System.Diagnostics;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Local controlled native hands from 5NPNPX62Q6PK; not a historical replay.
// R2: all 16 faults were injected only in a byte-identical private source mirror.
// Each mutant built (0), hit the named assertion (RED 1), restored exact bytes,
// rebuilt (0), and passed that native production-path case (GREEN 0).
// Exact commands, source/fault hashes and raw logs: work/incombat-repair-20261007/choice/r2-results.json.
// runtime-native:
// System.Exception: Post decision choice: native exhaust must retain Cascade rather than the naturally expiring curse
// runtime-source:
// System.Exception: Post decision choice: actual runtime selector must carry the native source into its effect-tail comparison
// card-native-ranking:
// System.Exception: Post decision choice: production CardBranches must publish the native continuation's retained Cut choice
// prediction-wiring:
// System.Exception: Post decision choice: production turn-start prediction must apply the same native exhaust continuation as runtime
// native-effect-hooks:
// System.Exception: Post decision choice: runtime native exhaust must execute FeelNoPain, DarkEmbrace draw and the Mittens Strength tail
// native-mittens-tail:
// System.Exception: Post decision choice: runtime native exhaust must execute FeelNoPain, DarkEmbrace draw and the Mittens Strength tail
// single-menu-coverage:
// System.Exception: Post decision choice: production single cleanup must preserve all nine legal post-play choices from a ten-card native hand
// cutoff-is-not-complete:
// System.Exception: Post decision choice: a truncated affordable hand must keep a legal fallback and report absence of a complete native witness
// returned-hidden-order:
// System.Exception: Post decision choice: common hidden-pool ordering must never replace the returned native card branch or its RNG
// multi-source-coverage:
// System.Exception: Post decision choice: ten-choice first source must leave a nondefault Mittens sacrifice reachable while keeping native Tactician energy and Rage defence
// future-exhaust:
// System.Exception: Post decision choice: minor immediate output must retain an unpayable future power instead of exhausting it for this hand
// early-side-wiring:
// System.Exception: Post decision choice: scored native branches must inherit an already-completed side start without repeating DemonForm or Plating
// early-side-idempotence:
// System.Exception: Post decision choice: scored native branches must inherit an already-completed side start without repeating DemonForm or Plating
// single-physical-tokens:
// System.Exception: Post decision choice: single discard must preserve all nine physical equal-key hand occurrences instead of semantic deduplication
// prediction-cutoff:
// System.Exception: Post decision choice: prediction without a complete affordable hand must leave prepared choices unset and use the existing legal fallback
// future-stat-wiring:
// System.Exception: Post decision choice: future exhaust opportunity must exclude native SetupStrike temporary Strength while preserving its current-hand benefit
internal static class PostDecisionChoiceScenarios
{
    private static string? observedSource;
    private static bool observePrepared;
    private static readonly List<(int Strength, int Plating)> preparedPowers = new();
    private static bool observeMenus;
    private static readonly List<int[]> observedMenus = new();
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.post-decision-choice");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PostDecisionChoiceScenarios), nameof(Format)));
        harmony.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.SelectRelicCards),
            [typeof(Player), typeof(IReadOnlyList<CardModel>), typeof(string), typeof(int), typeof(int), typeof(string)]),
            prefix: new HarmonyMethod(typeof(PostDecisionChoiceScenarios), nameof(ObserveSource)));
        harmony.Patch(AccessTools.Method(typeof(KernelChoiceContinuation), nameof(KernelChoiceContinuation.Evaluate)),
            prefix: new HarmonyMethod(typeof(PostDecisionChoiceScenarios), nameof(ObservePrepared)));
        harmony.Patch(AccessTools.Method(typeof(KernelChoiceContinuation), nameof(KernelChoiceContinuation.EnqueueAlternatives)),
            prefix: new HarmonyMethod(typeof(PostDecisionChoiceScenarios), nameof(ObserveMenus)));
        try
        {
            var tests = new (string Name, Action Run)[] {
                ("cascade", CascadeChoice), ("rage", RageChoice), ("precise", PreciseChoice),
                ("native-card", NativeCardChoice), ("triggers", DiscardTriggers),
                ("turn-start", TurnStartWiring), ("ethereal", EtherealChoice),
                ("native-effects", NativeEffects), ("ten-menu", FullSingleMenu),
                ("cutoff", IncompleteHandFallback), ("hidden-order", HiddenOrderIsolation),
                ("linked-extra", LinkedExtraTurn), ("two-source", TwoSourceCoverage),
                ("performance", ChoicePerformance), ("future-exhaust", FutureExhaust),
                ("early-side", EarlySideStart), ("duplicate-menu", DuplicatePhysicalMenu),
                ("future-temp-stat", FutureTemporaryStat), ("prediction-cutoff", PredictionIncompleteFallback) };
            Check(caseName is null || tests.Any(t => t.Name == caseName), "unknown case filter: " + caseName);
            foreach (var test in tests)
                if (caseName is null || caseName == test.Name)
                { test.Run(); Console.WriteLine("PASS: post-decision choice " + test.Name); }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void CascadeChoice()
    {
        var (p, c, e) = Board("POST_CASCADE", 3);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        var cascade = Hand<Cascade>(p, c, true); var bane = Hand<AscendersBane>(p, c);
        Hand<StrikeIronclad>(p, c); Hand<Rage>(p, c, true); Hand<Offering>(p, c);
        for (var i = 0; i < 8; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c); var before = root.StateText(p);
        var clock = Stopwatch.StartNew();
        observedSource = null;
        var selected = BotRelicChoices.Select(p, p.PlayerCombatState!.Hand.Cards.ToArray(), 1, 1,
            "FromHand:relic:ToastyMittens")!;
        Console.WriteLine($"CHOICE cascade={selected.Single().Id.Entry} ms={clock.Elapsed.TotalMilliseconds:F1}");
        Check(selected.Single() == bane, "native exhaust must retain Cascade rather than the naturally expiring curse");
        Check(observedSource == "ToastyMittens", "actual runtime selector must carry the native source into its effect-tail comparison");
        Check(root.StateText(p) == before, "choice comparison must leave the captured parent unchanged");
        var play = root.CardBranches(cascade, null, 1).Single(b => b.Boundary.Length == 0);
        Check(play.State.Energy(p) == 0 && play.State.Hp(e) < root.Hp(e),
            "Cascade continuation must pay its native X cost and realize native autoplay");
    }

    private static void RageChoice()
    {
        var (p, c, e) = Board("POST_RAGE", 3);
        Pow<StrengthPower>(e, 16);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        var rage = Hand<Rage>(p, c, true); Hand<ShrugItOff>(p, c);
        var defend = Hand<DefendIronclad>(p, c); Hand<Taunt>(p, c); Hand<StrikeIronclad>(p, c);
        p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<DefendIronclad>(p));
        var root = KernelSession.Capture(c);
        var selected = root.SelectRelicCards(p, root.Hand(p), "Exhaust", 1, 1, "ToastyMittens");
        Console.WriteLine("CHOICE rage=" + selected.Single().Id.Entry);
        Check(selected.Single() != rage, "mandatory exhaust must realize Rage attack-trigger block before choosing its sacrifice");
        Check(selected.Count == 1 && root.Hand(p).Contains(selected.Single()),
            "native selection must obey min/count and return the actual hand instance");
    }

    private static void PreciseChoice()
    {
        var (p, c, e) = Board("POST_PRECISE", 3, 10);
        Pow<VulnerablePower>(e, 8); Pow<AccuracyPower>(p.Creature, 6); Pow<FanOfKnivesPower>(p.Creature, 1);
        var cut = Hand<PreciseCut>(p, c); Hand<DefendSilent>(p, c);
        Hand<StrikeSilent>(p, c); Hand<StrikeSilent>(p, c); Hand<Shiv>(p, c); Hand<Shiv>(p, c);
        var root = KernelSession.Capture(c);
        var selected = root.SelectRelicCards(p, root.Hand(p), "Discard", 1, 1, "ToolsOfTheTradePower");
        Console.WriteLine("CHOICE precise=" + selected.Single().Id.Entry);
        Check(selected.Single() != cut, "native Tools discard must retain Precise Cut's affordable last-card payoff");
    }

    private static void NativeCardChoice()
    {
        var (p, c, e) = Board("POST_CARD_CHOICE", 4, 10);
        Pow<VulnerablePower>(e, 8); Pow<AccuracyPower>(p.Creature, 6);
        var survivor = Hand<Survivor>(p, c); var cut = Hand<PreciseCut>(p, c);
        Hand<StrikeSilent>(p, c); Hand<StrikeSilent>(p, c); Hand<DefendSilent>(p, c);
        Hand<Shiv>(p, c); Hand<Shiv>(p, c);
        var root = KernelSession.Capture(c);
        var branch = root.CardBranches(survivor, null, 1).Single(b => b.Boundary.Length == 0);
        Console.WriteLine("CHOICE card=" + branch.Choices.Single().Cards.Single().Id);
        Check(branch.Choices.Single().Cards.Single().Id != cut.Id.Entry,
            "production CardBranches must publish the native continuation's retained Cut choice");
        var action = new KernelTeamSearch.Action(p, survivor, null, Choices: branch.Choices,
            ChoiceOrdinals: branch.ChoiceOrdinals, CardStateKey: root.CardStateKey(survivor));
        var replay = root.ForkForReplay();
        Check(replay.ReplayPlanned(action, 1, out _) && replay.StateText(p) == branch.State.StateText(p),
            "chosen native ordinals and physical choice tokens must replay the same completed card branch");
    }

    private static void DiscardTriggers()
    {
        var (p, c, _) = Board("POST_DISCARD_TRIGGER", 0);
        var tactician = Hand<Tactician>(p, c); Hand<StrikeSilent>(p, c); Hand<DefendSilent>(p, c);
        var root = KernelSession.Capture(c);
        var selected = root.SelectRelicCards(p, root.Hand(p), "Discard", 1, 1, "ToolsOfTheTradePower");
        Check(selected.Single() == tactician,
            "native discard must realize Tactician energy before judging an otherwise unaffordable hand");
    }

    private static void TurnStartWiring()
    {
        var (p, c, _) = Board("POST_START_WIRING", 0);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        // Native next hand: an X engine, junk and three payable bodies.
        foreach (var canonical in new CardModel[] { ModelDb.Card<Cascade>(), ModelDb.Card<AscendersBane>(),
            ModelDb.Card<StrikeIronclad>(), ModelDb.Card<Rage>(), ModelDb.Card<Offering>() })
            p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard(canonical, p));
        for (var i = 0; i < 6; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c).ForkForReplay();
        Check(root.EndTurn(p, 2, out var reason), "native turn-start choice must settle without an incomplete transaction: " + reason);
        Console.WriteLine("CHOICE turn-start exhaust=" + string.Join(',', root.Exhaust(p).Select(q => q.Id.Entry)));
        Check(root.Exhaust(p).Any(q => q is AscendersBane) && root.Hand(p).Any(q => q is Cascade),
            "production turn-start prediction must apply the same native exhaust continuation as runtime");
        Check(root.Power<StrengthPower>(p.Creature) == 1,
            "native turn-start comparison must preserve the post-exhaust Mittens Strength tail exactly once");
    }

    private static void EtherealChoice()
    {
        var (p, c, _) = Board("POST_ETHEREAL", 1);
        var bane = Hand<AscendersBane>(p, c); var expensive = Hand<DemonForm>(p, c); Hand<StrikeSilent>(p, c);
        var root = KernelSession.Capture(c);
        var selected = root.SelectRelicCards(p, root.Hand(p), "Discard", 1, 1, "ToolsOfTheTradePower");
        Check(selected.Single() != bane,
            "native discard must preserve harmless Ethereal cleanup instead of polluting a future draw");
    }

    private static void NativeEffects()
    {
        var (p, c, _) = Board("POST_NATIVE_EFFECTS", 0);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        Pow<FeelNoPainPower>(p.Creature, 4); Pow<DarkEmbracePower>(p.Creature, 1);
        var bane = Hand<AscendersBane>(p, c);
        p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c);
        var options = root.Simulator.State.GetPlayerCombatState(p).Hand.Cards.ToArray();
        var spec = new CoopBots.Kernel.Vendor.CardChoiceSpec(CoopBots.Kernel.Vendor.PlanChoiceEffect.Exhaust,
            PileType.Hand, 1, 1, options, options, 0);
        var choice = CoopBots.Kernel.Vendor.CardChoiceSupport.BuildChoices(spec, null, 10, 10).Single();
        var child = root.Fork();
        Check(child.ApplyChoiceForAnalysis(p, spec, choice, "ToastyMittens")
            && child.Exhaust(p).Single() == bane && child.Block(p.Creature) == 4
            && child.Hand(p).Single() is StrikeIronclad && child.Power<StrengthPower>(p.Creature) == 1,
            "runtime native exhaust must execute FeelNoPain, DarkEmbrace draw and the Mittens Strength tail");
        var (d, dc, _) = Board("POST_NATIVE_DISCARD", 0);
        var reflex = Hand<Reflex>(d, dc);
        for (var i = 0; i < 3; i++) d.PlayerCombatState!.DrawPile.AddInternal(dc.CreateCard<StrikeSilent>(d));
        var dr = KernelSession.Capture(dc); var opts = dr.Simulator.State.GetPlayerCombatState(d).Hand.Cards.ToArray();
        var ds = new CoopBots.Kernel.Vendor.CardChoiceSpec(CoopBots.Kernel.Vendor.PlanChoiceEffect.Discard,
            PileType.Hand, 1, 1, opts, opts, 0);
        var dd = dr.Fork();
        Check(dd.ApplyChoiceForAnalysis(d, ds, CoopBots.Kernel.Vendor.CardChoiceSupport.BuildChoices(ds, null, 10, 10).Single(), "ToolsOfTheTradePower")
            && dd.Discard(d).Contains(reflex) && dd.Hand(d).Count == 2,
            "native discard comparison must execute Reflex draw instead of merely moving a card between piles");
    }

    private static void FullSingleMenu()
    {
        var (p, c, _) = Board("POST_FULL_SINGLE_MENU", 3);
        var survivor = Hand<Survivor>(p, c);
        Hand<StrikeSilent>(p, c); Hand<DefendSilent>(p, c); Hand<Rage>(p, c);
        Hand<Bash>(p, c); Hand<Offering>(p, c); Hand<Anger>(p, c);
        Hand<Taunt>(p, c); Hand<ShrugItOff>(p, c); Hand<PreciseCut>(p, c);
        var root = KernelSession.Capture(c);
        var branches = root.CardBranches(survivor, null, 16).Where(b => b.Boundary.Length == 0).ToArray();
        Check(branches.Select(b => b.Choices.Single().Cards.Single().Id).Distinct().Count() == 9,
            "production single cleanup must preserve all nine legal post-play choices from a ten-card native hand");
    }

    private static void IncompleteHandFallback()
    {
        var (p, c, _) = Board("POST_CHOICE_CUTOFF", 0);
        p.AddRelicInternal(ModelDb.Relic<UnceasingTop>().ToMutable());
        Hand<Shiv>(p, c); Hand<Shiv>(p, c);
        for (var i = 0; i < 40; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<Shiv>(p));
        var root = KernelSession.Capture(c);
        var selected = root.SelectRelicCards(p, root.Hand(p), "Exhaust", 1, 1, "ToastyMittens");
        Console.WriteLine("CHOICE cutoff=" + root.LastHandChoiceComparison);
        Check(selected.Count == 1 && root.LastHandChoiceComparison.Contains("complete=0")
            && root.LastHandChoiceComparison.Contains("fallback=no-complete-native-hand"),
            "a truncated affordable hand must keep a legal fallback and report absence of a complete native witness");
    }

    private static void HiddenOrderIsolation()
    {
        var (p, c, _) = Board("POST_HIDDEN_ORDER", 3);
        var survivor = Hand<Survivor>(p, c); Hand<StrikeSilent>(p, c); Hand<PreciseCut>(p, c);
        p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeSilent>(p));
        p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<Anger>(p));
        p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<DefendSilent>(p));
        var root = KernelSession.Capture(c);
        var before = root.StateText(p); var shuffle = root.Simulator.Rng.ShuffleState;
        var branch = root.CardBranches(survivor, null, 1).Single();
        Check(root.StateText(p) == before && root.Simulator.Rng.ShuffleState == shuffle,
            "hidden-pool scoring must leave the captured parent and its RNG untouched");
        Check(branch.State.DrawPile(p).SequenceEqual(root.DrawPile(p))
            && branch.State.Simulator.Rng.ShuffleState == shuffle,
            "common hidden-pool ordering must never replace the returned native card branch or its RNG");
        var replay = root.ForkForReplay();
        var action = new KernelTeamSearch.Action(p, survivor, null, Choices: branch.Choices,
            ChoiceOrdinals: branch.ChoiceOrdinals, CardStateKey: root.CardStateKey(survivor));
        Check(replay.ReplayPlanned(action, 1, out _) && replay.StateText(p) == branch.State.StateText(p),
            "actual choice tokens must replay the unsampled native hidden draw order");
    }

    private static void LinkedExtraTurn()
    {
        var (p, c, _) = Board("POST_LINKED_EXTRA", 0);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        Pow<ToolsOfTheTradePower>(p.Creature, 1); Pow<AmbergrisPower>(p.Creature, 1);
        foreach (var canonical in new CardModel[] { ModelDb.Card<Cascade>(), ModelDb.Card<AscendersBane>(),
            ModelDb.Card<StrikeIronclad>(), ModelDb.Card<Rage>(), ModelDb.Card<Offering>(), ModelDb.Card<DefendIronclad>() })
            p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard(canonical, p));
        for (var i = 0; i < 6; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c).ForkForReplay();
        Check(root.EndTurn(p, 2, out var reason), "linked Tools/Mittens extra turn must settle: " + reason);
        Console.WriteLine("CHOICE linked-extra exhaust=" + string.Join(',', root.Exhaust(p).Select(q => q.Id.Entry))
            + " hand=" + string.Join(',', root.Hand(p).Select(q => q.Id.Entry)));
        Check(root.Hp(p.Creature) == 80 && root.Power<StrengthPower>(p.Creature) == 1
            && root.Exhaust(p).Count == 1 && root.Hand(p).Count == 4,
            "bounded linked choices must preserve extra-turn skip, exact Mittens tail and both legal menu counts");
    }

    private static void ChoicePerformance()
    {
        var (p, c, _) = Board("POST_CHOICE_PERFORMANCE", 3);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        Hand<Cascade>(p, c); Hand<AscendersBane>(p, c); Hand<StrikeIronclad>(p, c);
        Hand<Rage>(p, c); Hand<Offering>(p, c); Hand<DefendIronclad>(p, c);
        Hand<Bash>(p, c); Hand<Taunt>(p, c); Hand<Anger>(p, c); Hand<ShrugItOff>(p, c);
        for (var i = 0; i < 8; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        for (var warm = 0; warm < 4; warm++)
        {
            var root = KernelSession.Capture(c); var allocated = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            var selected = root.SelectRelicCards(p, root.Hand(p), "Exhaust", 1, 1, "ToastyMittens");
            Console.WriteLine($"CHOICE PERF sample={warm} ms={clock.Elapsed.TotalMilliseconds:F1} bytes={GC.GetAllocatedBytesForCurrentThread() - allocated} {root.LastHandChoiceComparison}");
            Check(selected.Count == 1 && root.LastHandChoiceComparison.Contains("candidates=10"),
                "native ten-card cleanup must remain bounded while admitting all single-card options");
            var steps = int.Parse(root.LastHandChoiceComparison.Split("steps=")[1].Split(' ')[0]);
            Check(steps <= 384, "one native hand comparison must never exceed its fixed aggregate step budget");
        }
    }

    private static void TwoSourceCoverage()
    {
        var (p, c, e) = Board("POST_TWO_SOURCE", 0);
        Pow<StrengthPower>(e, 34);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        Pow<ToolsOfTheTradePower>(p.Creature, 1); Pow<DrawCardsNextTurnPower>(p.Creature, 5);
        foreach (var canonical in new CardModel[] { ModelDb.Card<Tactician>(), ModelDb.Card<Rage>(), ModelDb.Card<DefendIronclad>() })
        {
            var card = c.CreateCard(canonical, p); if (card is Rage) card.UpgradeInternal();
            p.PlayerCombatState!.DrawPile.AddInternal(card);
        }
        for (var i = 0; i < 7; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c).ForkForReplay();
        observedMenus.Clear(); observeMenus = true;
        try { Check(root.EndTurn(p, 2, out var reason), "two-source ten-card native start must settle: " + reason); }
        finally { observeMenus = false; }
        Console.WriteLine("CHOICE two-source exhaust=" + string.Join(',', root.Exhaust(p).Select(q => q.Id.Entry))
            + " discard=" + string.Join(',', root.Discard(p).Select(q => q.Id.Entry))
            + " hand=" + string.Join(',', root.Hand(p).Select(q => q.Id.Entry)));
        Check(observedMenus.Any(m => m.Length == 2 && m[0] == 10 && m[1] == 9),
            "two-source production fixture must actually compare ten first-menu and nine second-menu physical candidates");
        Check(root.Hand(p).Count == 8 && root.Exhaust(p).Count == 1 && root.Discard(p).Count == 1,
            "native ten-card start must resolve both distinct source menus with their physical instances");
        Check(root.Discard(p).Single() is Tactician && root.Exhaust(p).Single() is not Rage,
            "ten-choice first source must leave a nondefault Mittens sacrifice reachable while keeping native Tactician energy and Rage defence");
    }

    private static void FutureExhaust()
    {
        var (p, c, _) = Board("POST_FUTURE_EXHAUST", 1);
        var core = Hand<DarkEmbrace>(p, c); Hand<StrikeIronclad>(p, c); Hand<DefendIronclad>(p, c);
        var root = KernelSession.Capture(c);
        var selected = root.SelectRelicCards(p, root.Hand(p), "Exhaust", 1, 1, "ToastyMittens");
        Console.WriteLine("CHOICE future-exhaust=" + selected.Single().Id.Entry);
        Check(selected.Single() != core,
            "minor immediate output must retain an unpayable future power instead of exhausting it for this hand");
        var (s, sc, foe) = Board("POST_EXHAUST_SURVIVAL", 1, 2);
        Pow<StrengthPower>(foe, 4); s.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        var sacrifice = Hand<DarkEmbrace>(s, sc); Hand<DefendIronclad>(s, sc);
        var sr = KernelSession.Capture(sc);
        Check(sr.SelectRelicCards(s, sr.Hand(s), "Exhaust", 1, 1, "ToastyMittens").Single() == sacrifice,
            "native lethal prevention must outrank the future opportunity cost of a mandatory exhaust");
        var (v, vc, enemy) = Board("POST_EXHAUST_VICTORY", 1, 2);
        enemy.SetCurrentHpInternal(7); v.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        var vcCore = Hand<DarkEmbrace>(v, vc); Hand<StrikeIronclad>(v, vc);
        var vr = KernelSession.Capture(vc);
        Check(vr.SelectRelicCards(v, vr.Hand(v), "Exhaust", 1, 1, "ToastyMittens").Single() == vcCore,
            "native same-hand victory must outrank conserving an unusable future power");
    }

    private static void EarlySideStart()
    {
        var (p, c, _) = Board("POST_EARLY_SIDE", 0);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        Pow<ForegoneConclusionPower>(p.Creature, 1); Pow<ToolsOfTheTradePower>(p.Creature, 1);
        Pow<DemonFormPower>(p.Creature, 2); Pow<PlatingPower>(p.Creature, 3);
        p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<AscendersBane>(p));
        for (var i = 0; i < 8; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c).ForkForReplay();
        var parent = KernelSession.CaptureLiveStateText(c, p); preparedPowers.Clear(); observePrepared = true;
        try { Check(root.EndTurn(p, 2, out var reason), "early native menu must settle: " + reason); }
        finally { observePrepared = false; }
        Console.WriteLine("CHOICE early-side probes=" + string.Join(',', preparedPowers));
        Check(preparedPowers.Count > 0 && preparedPowers.All(x => x.Strength == 3 && x.Plating == 2),
            "scored native branches must inherit an already-completed side start without repeating DemonForm or Plating");
        Check(root.Power<StrengthPower>(p.Creature) == 3 && root.Power<PlatingPower>(p.Creature) == 2
            && KernelSession.CaptureLiveStateText(c, p) == parent,
            "early-choice prediction must preserve the real final power amounts and leave live state unchanged");
    }

    private static void DuplicatePhysicalMenu()
    {
        var (p, c, _) = Board("POST_DUPLICATE_MENU", 3);
        var survivor = Hand<Survivor>(p, c);
        for (var i = 0; i < 9; i++) Hand<StrikeSilent>(p, c);
        var root = KernelSession.Capture(c);
        var branches = root.CardBranches(survivor, null, 16).ToArray();
        Check(branches.Length == 9 && branches.Select(b => b.Choices.Single().Cards.Single().Occurrence).Distinct().Count() == 9,
            "single discard must preserve all nine physical equal-key hand occurrences instead of semantic deduplication");
        foreach (var branch in branches)
        {
            var action = new KernelTeamSearch.Action(p, survivor, null, Choices: branch.Choices,
                ChoiceOrdinals: branch.ChoiceOrdinals, CardStateKey: root.CardStateKey(survivor));
            var replay = root.ForkForReplay();
            Check(replay.ReplayPlanned(action, 1, out _) && replay.StateText(p) == branch.State.StateText(p),
                "each equal-key physical occurrence must replay its exact native token and ordinal");
        }
    }

    private static void PredictionIncompleteFallback()
    {
        var (p, c, _) = Board("POST_PREDICTION_CUTOFF", 0);
        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
        p.AddRelicInternal(ModelDb.Relic<UnceasingTop>().ToMutable());
        for (var i = 0; i < 40; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<Shiv>(p));
        var root = KernelSession.Capture(c).ForkForReplay();
        Check(root.EndTurn(p, 2, out var reason), "native prediction-cutoff start must retain a legal fallback: " + reason);
        Console.WriteLine("CHOICE prediction-cutoff=" + root.LastHandChoiceComparison);
        Check(root.LastHandChoiceComparison.Contains("fallback=no-complete-native-hand")
            && root.Exhaust(p).Count == 1 && root.Hand(p).Count == 4,
            "prediction without a complete affordable hand must leave prepared choices unset and use the existing legal fallback");
    }

    private static void FutureTemporaryStat()
    {
        var (p, c, e) = Board("POST_FUTURE_TEMP_STAT", 2);
        var setup = Hand<SetupStrike>(p, c); var strike = Hand<StrikeSilent>(p, c); Hand<DefendSilent>(p, c);
        var root = KernelSession.Capture(c).CardBranches(setup, e, 1).Single(b => b.Boundary.Length == 0).State;
        var before = root.StateText(p);
        var opts = root.Simulator.State.GetPlayerCombatState(p).Hand.Cards.ToArray();
        var spec = new CoopBots.Kernel.Vendor.CardChoiceSpec(CoopBots.Kernel.Vendor.PlanChoiceEffect.Exhaust,
            PileType.Hand, 1, 1, opts, opts, 0);
        var choice = KernelRelicChoices.CleanupChoices(spec, 10).Single(q => q.Cards.Single().CardId == strike.Id.Entry);
        var child = root.Fork();
        Check(child.ApplyChoiceForAnalysis(p, spec, choice, "analysis"), "native future-stat exhaust must settle");
        var cost = root.ExhaustOpportunityCost(child, p);
        Console.WriteLine("CHOICE future-temp-stat cost=" + cost + " currentStrength=" + root.Power<StrengthPower>(p.Creature));
        Check(cost == 6 && root.Power<StrengthPower>(p.Creature) > 0 && root.StateText(p) == before,
            "future exhaust opportunity must exclude native SetupStrike temporary Strength while preserving its current-hand benefit");
    }

    private static (Player, CombatState, Creature) Board(string seed, int energy, int hp = 80)
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 87, 1));
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: seed));
        p.ResetCombatState(); c.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(hp);
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(energy);
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(600); e.SetCurrentHpInternal(600);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (p, c, e);
    }
    private static T Hand<T>(Player p, CombatState c, bool upgraded = false) where T : CardModel
    { var card = c.CreateCard<T>(p); if (upgraded) card.UpgradeInternal(); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static void Pow<T>(Creature c, decimal amount) where T : PowerModel
    { ModelDb.Power<T>().ToMutable().ApplyInternal(c, amount, true); }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void ObserveSource(string source) { observedSource = source; }
    private static void ObservePrepared(KernelSession selected, Player owner)
    { if (observePrepared) preparedPowers.Add((selected.Power<StrengthPower>(owner.Creature), selected.Power<PlatingPower>(owner.Creature))); }
    private static void ObserveMenus(IReadOnlyList<int> counts)
    { if (observeMenus) observedMenus.Add(counts.ToArray()); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Post decision choice: " + message); }
}
