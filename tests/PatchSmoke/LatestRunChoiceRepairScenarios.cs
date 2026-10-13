using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

// Live 1JJ0XFWWNLTC had five HiddenDaggers menus with one remaining card.
// These are controlled native protocol witnesses, not a historical replay.
// R2 2026-10-08: thirteen isolated production faults; successful build, RED 1, exact restore, build, GREEN 0.
// Actual commands/hashes/raw: work/latest-run-repair-20261008/choice/r2-results.json.
// effective-short-menu-bounds: System.Exception: Latest run choice repair: HiddenDaggers captured bounds must use the native effective cardinality instead of an unavailable second card
// full-menu-capture: System.Exception: Latest run choice repair: HiddenDaggers must stamp the complete menu and executing source after removing the played card
// active-source-capture: System.Exception: Latest run choice repair: nested card menus must stamp the executing HiddenDaggers separately from its outer Decisions source
// empty-source-capture: System.Exception: Latest run choice repair: HiddenDaggers must stamp the complete menu and executing source after removing the played card
// changed-unselected-option: System.Exception: Latest run choice repair: a changed unselected option must revoke the suffix even when the selected card remains available
// changed-effective-bounds: System.Exception: Latest run choice repair: changed effective native bounds must revoke the suffix instead of trusting the same selected token
// changed-executing-source: System.Exception: Latest run choice repair: a different nested executing source must revoke the suffix despite identical options and bounds
// dispatcher-source-wiring: System.Exception: Latest run choice repair: the production native dispatcher must call source-aware planned-menu matching
// dispatcher-source-value: System.Exception: Latest run choice repair: actual dispatcher and transported PlanSync must accept the native short HiddenDaggers menu without dropping its suffix
// cursor-effective-bounds: System.Exception: Latest run choice repair: an explicit cursor must reject a selected-token count below the native effective minimum
// cursor-nested-context: System.Exception: Latest run choice repair: an explicit cursor must reject a different nested context even when tokens and counts coincide
// room-initialization-barrier: System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
// planned-source-wiring: System.Exception: Latest run choice repair: a different nested executing source must revoke the suffix despite identical options and bounds
// Room native bounds fault also contains the actual CoopBots.RoomWork.Waiting stack.
// Native setup/compile/presentation failures in initial-green-attempt* are not R2 results.
// R2 plans/raw results: work/latest-run-repair-20261008/choice/.
// Expanded empty-menu/upgrade-filter R2: six additional faults, successful build -> named RED1 -> exact restore -> build -> GREEN0.
// Raw stage: work/latest-run-repair-20261008/choice/expanded-final-r2/r2-results.json.
// native-empty-hidden-spec: System.Exception: Latest run choice repair: an empty nested HiddenDaggers menu must retain its native requested two-card bounds separately from effective zero
// native-empty-survivor-spec: System.Exception: Latest run choice repair: all three real empty Survivor discard menus must remain aligned with their nested native source
// native-empty-seeker-spec: System.Exception: Latest run choice repair: native empty Seeker draw-pile menu must retain its implicit slot and requested one-card bounds
// native-dredge-capacity-control: System.Exception: Latest run choice repair: native full-hand Dredge and NeowsFury must not invent a menu when their capacity gate skips selection
// native-neow-capacity-control: System.Exception: Latest run choice repair: native full-hand Dredge and NeowsFury must not invent a menu when their capacity gate skips selection
// native-armaments-menu-filter: System.Exception: Latest run choice repair: real Armaments native upgrade filtering must match the transported complete menu without false invalidation
// Test-isolation R2 2026-10-08: successful private build -> named RED1 -> exact restore -> build0/GREEN0.
// Raw results: work/latest-run-repair-20261008/choice/fixture-isolation/r2-results.json.
// borrow-existing-prefix: System.Exception: Latest run choice repair: each native choice fixture must preserve every pre-existing selection prefix and its owner
// nested-local-context-restore: System.Exception: Latest run choice repair: each native choice fixture must restore local, network and selector context
internal static class LatestRunChoiceRepairScenarios
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool condition, string why)
    { if (!condition) throw new Exception("Latest run choice repair: " + why); }

    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var cases = new (string Name, Action Run)[] {
            ("hidden-zero", () => Hidden(0)), ("hidden-one", () => Hidden(1)),
            ("hidden-two", () => Hidden(2)), ("nested-source", NestedSource), ("nested-empty", NestedEmpty),
            ("nested-empty-survivor", NestedEmptySurvivor), ("menu-identity", MenuIdentity),
            ("armaments-mixed", () => ArmamentsMenu(false)), ("armaments-empty", () => ArmamentsMenu(true)),
            ("seeker-empty", SeekerEmpty), ("capacity-no-menu", CapacityNoMenu),
            ("nested-cursor", NestedCursor), ("room-start", RoomStart), ("wiring", Wiring) };
        Check(caseName is null || cases.Any(c => c.Name == caseName), "unknown case filter: " + caseName);
        var nativePrefixes = NativePrefixSnapshot();
        var savedLocal = MegaCrit.Sts2.Core.Context.LocalContext.NetId;
        var savedNetwork = RunManager.Instance.NetService;
        var savedSelector = CardSelectCmd.Selector;
        var harmony = new Harmony("coopbots.latest-run-choice-repair");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LatestRunChoiceRepairScenarios), nameof(Format)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(CardModel), "SelectionScreenPrompt"),
            prefix: new HarmonyMethod(typeof(LatestRunChoiceRepairScenarios), nameof(NativeSelectionPrompt)));
        // Native autoplay's wrapper uses Godot's clock only to time its play
        // animation. Replace that clock call, not the wrapper/card/hooks.
        var nativeWrapper = AccessTools.Method(typeof(CardModel), "OnPlayWrapper")
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        harmony.Patch(AccessTools.Method(nativeWrapper, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(LatestRunChoiceRepairScenarios), nameof(PresentationClock)));
        try
        {
            foreach (var item in cases)
                if (caseName is null || caseName == item.Name)
                {
                    item.Run();
                    Check(NativePrefixSnapshot() == nativePrefixes,
                        "each native choice fixture must preserve every pre-existing selection prefix and its owner");
                    Check(MegaCrit.Sts2.Core.Context.LocalContext.NetId == savedLocal
                        && ReferenceEquals(RunManager.Instance.NetService, savedNetwork)
                        && ReferenceEquals(CardSelectCmd.Selector, savedSelector),
                        "each native choice fixture must restore local, network and selector context");
                    Console.WriteLine("PASS: latest-run choice repair " + item.Name);
                }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    // Harmony removes duplicate registrations of the same PatchMethod together.
    // Borrow an existing production registration; install only when absent.
    private sealed class NativePrefixScope(string id) : IDisposable
    {
        private readonly Harmony harmony = new(id);
        private bool installed;
        public void Add(MethodBase original, MethodInfo prefix)
        {
            if (Harmony.GetPatchInfo(original)?.Prefixes.Any(p => p.PatchMethod == prefix) == true) return;
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            installed = true;
        }
        public void Dispose()
        {
            if (!installed) return;
            harmony.UnpatchAll(harmony.Id);
            installed = false;
        }
    }

    private static string NativePrefixSnapshot() => string.Join("|", typeof(CardSelectCmd).GetMethods()
        .SelectMany(method => Harmony.GetPatchInfo(method)?.Prefixes
            .Select(patch => method.ToString() + ":" + patch.owner + ":" + patch.PatchMethod) ?? Enumerable.Empty<string>())
        .OrderBy(text => text, StringComparer.Ordinal));

    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool NativeSelectionPrompt(ref MegaCrit.Sts2.Core.Localization.LocString __result)
    { __result = new("card_selection", "TO_DISCARD"); return false; }
    private static ulong NativeClock() => 0;
    private static IEnumerable<CodeInstruction> PresentationClock(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType?.FullName == "Godot.Time"
                && method.Name == "GetTicksMsec")
                instruction.operand = AccessTools.Method(typeof(LatestRunChoiceRepairScenarios), nameof(NativeClock));
            yield return instruction;
        }
    }

    private static void Hidden(int remaining)
    {
        // First drive the UNPATCHED native selector with a human seat. Its
        // implicit-all result establishes the cardinality independently of the
        // projected choice bounds and the bot's ranking policy.
        var (human, hc, _) = Board(90000000000000002, "LATEST-HIDDEN-NATIVE-" + remaining);
        var nativeSource = hc.CreateCard<HiddenDaggers>(human);
        var nativeOptions = Enumerable.Range(0, remaining).Select(_ => hc.CreateCard<StrikeSilent>(human)).ToArray();
        foreach (var card in nativeOptions) human.PlayerCombatState!.Hand.AddInternal(card);
        using (CardSelectCmd.PushSelector(new TestCardSelector()))
        {
            var implicitCards = CardSelectCmd.FromHandForDiscard(new ThrowingPlayerChoiceContext(), human,
                new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 2), null, nativeSource)
                .GetAwaiter().GetResult().ToArray();
            Check(implicitCards.SequenceEqual(nativeOptions), "native HiddenDaggers must select all zero/one/two remaining cards");
        }

        var (p, combat, _) = Board(BotRegistry.CreateId(BotDifficulty.Pro, 231, remaining), "LATEST-HIDDEN-" + remaining);
        var hidden = combat.CreateCard<HiddenDaggers>(p); p.PlayerCombatState!.Hand.AddInternal(hidden);
        var options = Enumerable.Range(0, remaining).Select(_ => combat.CreateCard<StrikeSilent>(p)).ToArray();
        foreach (var card in options) p.PlayerCombatState.Hand.AddInternal(card);
        var root = KernelSession.Capture(combat); var before = root.StateText(p);
        KernelSession.CardBranch branch;
        using (KernelChoiceContinuation.Enter()) branch = root.CardBranches(hidden, null, 1).Single();
        Check(branch.Boundary.Length == 0 && branch.Choices.Count == 1,
            "HiddenDaggers production CardBranches must complete and record its zero/one/two-card menu");
        var choice = branch.Choices.Single();
        Check(choice.Cards.Count == remaining && choice.Minimum == remaining && choice.Maximum == remaining,
            "HiddenDaggers captured bounds must use the native effective cardinality instead of an unavailable second card");
        Check(choice.Options?.Count == remaining && choice.MenuSource == hidden.Id.Entry
            && choice.Options.SequenceEqual(KernelChoice.MenuTokens(options)),
            "HiddenDaggers must stamp the complete menu and executing source after removing the played card");
        var json = JsonSerializer.Serialize(choice);
        var transported = JsonSerializer.Deserialize<KernelChoice>(json)!;
        using var transaction = new Transaction(p, combat, hidden, [transported]);
        // The actual OnPlay has already removed HiddenDaggers from Hand.
        p.PlayerCombatState.Hand.RemoveInternal(hidden); p.PlayerCombatState.PlayPile.AddInternal(hidden);
        var method = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard));
        Check(BotCardChoiceDispatcher.TrySelect(method,
                [new ThrowingPlayerChoiceContext(), p, new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 2), null!, hidden],
                out var answer)
            && ((IEnumerable<CardModel>)answer!).SequenceEqual(options)
            && transaction.Index == 1 && !transaction.Diverged && transaction.CacheIntact,
            "actual dispatcher and transported PlanSync must accept the native short HiddenDaggers menu without dropping its suffix");
        // Exercise the same production Harmony prefix from real HiddenDaggers.OnPlay.
        transaction.Reinstall([transported]);
        using var nativePrefix = new NativePrefixScope("coopbots.latest-hidden-native-" + remaining);
        nativePrefix.Add(method, AccessTools.Method(typeof(BotMultipleCardSelectPatch), "Prefix"));
        try
        {
            var spent = hidden.SpendResources().GetAwaiter().GetResult();
            Check(spent.Item1 == 0, "native HiddenDaggers must be a legal zero-energy play");
            var play = new CardPlay { Card = hidden, Player = p, Target = null, ResultPile = PileType.Discard,
                Resources = new ResourceInfo { EnergySpent = spent.Item1, EnergyValue = spent.Item1,
                    StarsSpent = spent.Item2, StarValue = spent.Item2 }, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            ((Task)AccessTools.Method(typeof(HiddenDaggers), "OnPlay").Invoke(hidden,
                [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
            p.PlayerCombatState.PlayPile.RemoveInternal(hidden); p.PlayerCombatState.DiscardPile.AddInternal(hidden);
            Check(transaction.Index == 1 && !transaction.Diverged && transaction.CacheIntact
                && p.PlayerCombatState.Hand.Cards.Count == 2 && p.PlayerCombatState.Hand.Cards.All(c => c is Shiv)
                && p.PlayerCombatState.DiscardPile.Cards.SequenceEqual(branch.State.Discard(p))
                && p.PlayerCombatState.Energy == branch.State.Energy(p),
                "native HiddenDaggers discard and two-Shiv tail must match the projected short-menu branch without cache invalidation");
            Check(root.StateText(p) == before, "native short-menu execution must not mutate the captured parent");
            Console.WriteLine($"HIDDEN remaining={remaining} raw={choice.RequestedMinimum}..{choice.RequestedMaximum} "
                + $"effective={choice.Minimum}..{choice.Maximum} source={choice.MenuSource} options={choice.Options!.Count} "
                + $"nativeDiscard={string.Join(',', p.PlayerCombatState.DiscardPile.Cards.Select(c => c.Id.Entry))}");
        }
        finally { nativePrefix.Dispose(); }
    }

    private static void MenuIdentity()
    {
        var (p, c, _) = Board(BotRegistry.CreateId(BotDifficulty.Pro, 232, 0), "LATEST-MENU-IDENTITY");
        var source = c.CreateCard<HiddenDaggers>(p);
        var strike = c.CreateCard<StrikeSilent>(p); var defend = c.CreateCard<DefendSilent>(p);
        p.PlayerCombatState!.Hand.AddInternal(strike); p.PlayerCombatState.Hand.AddInternal(defend);
        var choice = new KernelChoice(p.NetId, "outer", "Discard", PileType.Hand, "nested-1", 1, 1,
            [KernelChoice.MenuTokens([strike]).Single()]) {
            Options = KernelChoice.MenuTokens([strike, defend]), RequestedMinimum = 1, RequestedMaximum = 1,
            MenuSource = source.Id.Entry,
        };
        using var transaction = new Transaction(p, c, source, [choice]);
        Check(BotChoicePlanSync.SelectMenu(p, [strike, defend], 1, 1, source.Id.Entry)?.Single() == strike
            && transaction.CacheIntact, "the exact full native menu must retain its transported selected token and suffix");
        transaction.Reinstall([choice]);
        Check(BotChoicePlanSync.SelectMenu(p, [strike], 1, 1, source.Id.Entry) is null
            && transaction.Diverged && !transaction.CacheIntact,
            "a changed unselected option must revoke the suffix even when the selected card remains available");
        transaction.Reinstall([choice]);
        Check(BotChoicePlanSync.SelectMenu(p, [strike, defend], 1, 2, source.Id.Entry) is null
            && transaction.Diverged && !transaction.CacheIntact,
            "changed effective native bounds must revoke the suffix instead of trusting the same selected token");
        transaction.Reinstall([choice]);
        Check(BotChoicePlanSync.SelectMenu(p, [strike, defend], 1, 1, "SURVIVOR") is null
            && transaction.Diverged && !transaction.CacheIntact,
            "a different nested executing source must revoke the suffix despite identical options and bounds");
        var details = choice.MenuDifference(p.NetId, [strike], 1, 1, "SURVIVOR");
        Check(details.Contains("plannedOptions=") && details.Contains("actualOptions=")
            && details.Contains("plannedBounds=1..1") && details.Contains("actualBounds=1..1")
            && details.Contains("context=nested-1") && details.Contains("selected="),
            "native choice diagnostics must preserve both menus, bounds, context and selected tokens");
        Console.WriteLine("MENU_DIFFERENCE " + details);
    }

    private static void NestedSource() => NativeNested(emptyFirstMenu: false);
    private static void NestedEmpty() => NativeNested(emptyFirstMenu: true);
    private static void NestedEmptySurvivor() => NativeNested(emptyFirstMenu: true, survivor: true);

    private static void NativeNested(bool emptyFirstMenu, bool survivor = false)
    {
        var (p, c, _) = Board(BotRegistry.CreateId(BotDifficulty.Pro, 235, 0),
            survivor ? "LATEST-NATIVE-NESTED-EMPTY-SURVIVOR"
                : emptyFirstMenu ? "LATEST-NATIVE-NESTED-EMPTY" : "LATEST-NATIVE-NESTED-SOURCE");
        p.PlayerCombatState!.GainStars(6);
        var outer = c.CreateCard<DecisionsDecisions>(p);
        CardModel hidden = survivor ? c.CreateCard<Survivor>(p) : c.CreateCard<HiddenDaggers>(p);
        var strike = c.CreateCard<StrikeSilent>(p);
        foreach (var card in emptyFirstMenu ? new CardModel[] { outer, hidden }
            : new CardModel[] { outer, hidden, strike }) p.PlayerCombatState.Hand.AddInternal(card);
        if (!emptyFirstMenu)
            for (var i = 0; i < 3; i++) p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<Dazed>(p));
        var root = KernelSession.Capture(c); var before = root.StateText(p);
        KernelSession.CardBranch branch;
        using (KernelChoiceContinuation.Enter()) branch = root.CardBranches(outer, null, 1).Single();
        if (survivor)
            Check(branch.Boundary.Length == 0 && branch.Choices.Count == 4
                && branch.Choices.Skip(1).All(choice => choice.Cards.Count == 0 && choice.Options?.Count == 0
                    && choice.Minimum == 0 && choice.Maximum == 0
                    && choice.RequestedMinimum == 1 && choice.RequestedMaximum == 1
                    && choice.Source == outer.Id.Entry && choice.MenuSource == hidden.Id.Entry),
                "all three real empty Survivor discard menus must remain aligned with their nested native source");
        else if (emptyFirstMenu)
            Check(branch.Boundary.Length == 0 && branch.Choices.Count == 4
                && branch.Choices[1].Cards.Count == 0 && branch.Choices[1].Options?.Count == 0
                && branch.Choices[1].Minimum == 0 && branch.Choices[1].Maximum == 0
                && branch.Choices[1].Source == outer.Id.Entry && branch.Choices[1].MenuSource == hidden.Id.Entry
                && branch.Choices.Skip(2).All(choice => choice.Cards.Count == 2 && choice.Options?.Count == 2),
                "nested empty native menu must occupy its own source-stamped slot before the two generated-Shiv menus");
        if (emptyFirstMenu && !survivor)
            Check(branch.Choices[1].RequestedMinimum == 2 && branch.Choices[1].RequestedMaximum == 2,
                "an empty nested HiddenDaggers menu must retain its native requested two-card bounds separately from effective zero");
        Check(branch.Boundary.Length == 0 && branch.Choices.Count == 4
            && branch.Choices[0].MenuSource == outer.Id.Entry
            && branch.Choices.Skip(1).All(choice => choice.Source == outer.Id.Entry && choice.MenuSource == hidden.Id.Entry),
            "nested card menus must stamp the executing HiddenDaggers separately from its outer Decisions source");
        using var transaction = new Transaction(p, c, outer, JsonSerializer.Deserialize<KernelChoice[]>(
            JsonSerializer.Serialize(branch.Choices))!);
        using var nativePrefix = new NativePrefixScope("coopbots.latest-native-nested-source");
        var savedLocalId = MegaCrit.Sts2.Core.Context.LocalContext.NetId;
        MegaCrit.Sts2.Core.Context.LocalContext.NetId = p.NetId;
        foreach (var name in new[] { nameof(CardSelectCmd.FromHand), nameof(CardSelectCmd.FromHandForDiscard) })
            nativePrefix.Add(AccessTools.Method(typeof(CardSelectCmd), name),
                AccessTools.Method(typeof(BotMultipleCardSelectPatch), "Prefix"));
        try
        {
            Check(outer.CanPlay() && outer.IsValidTarget(null), "native nested source must start from a payable legal outer card");
            var spent = outer.SpendResources().GetAwaiter().GetResult();
            p.PlayerCombatState.Hand.RemoveInternal(outer); p.PlayerCombatState.PlayPile.AddInternal(outer);
            var play = new CardPlay { Card = outer, Player = p, Target = null, ResultPile = PileType.Exhaust,
                Resources = new ResourceInfo { EnergySpent = spent.Item1, EnergyValue = spent.Item1,
                    StarsSpent = spent.Item2, StarValue = spent.Item2 }, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            ((Task)AccessTools.Method(typeof(DecisionsDecisions), "OnPlay").Invoke(outer,
                [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
            p.PlayerCombatState.PlayPile.RemoveInternal(outer); p.PlayerCombatState.ExhaustPile.AddInternal(outer);
            Check(transaction.Index == 4 && !transaction.Diverged && transaction.CacheIntact,
                "real Decisions autoplay must consume its outer menu and all three executing HiddenDaggers menus without false divergence");
            Check(KernelChoice.MenuTokens(p.PlayerCombatState.Hand.Cards.ToArray()).SequenceEqual(
                    KernelChoice.MenuTokens(branch.State.Hand(p)))
                && KernelChoice.MenuTokens(p.PlayerCombatState.DiscardPile.Cards.ToArray()).SequenceEqual(
                    KernelChoice.MenuTokens(branch.State.Discard(p)))
                && p.PlayerCombatState.DrawPile.Cards.SequenceEqual(branch.State.DrawPile(p))
                && p.PlayerCombatState.Energy == branch.State.Energy(p)
                && p.PlayerCombatState.Stars == branch.State.Simulator.State.GetPlayerCombatState(p).Stars
                && p.Creature.Block == branch.State.Block(p.Creature) && p.Creature.CurrentHp == branch.State.Hp(p.Creature),
                "nested native autoplay and projection must preserve ordered card identities and paid resources after every menu tail");
            Check(root.StateText(p) == before, "nested real menu execution must preserve its captured parent");
            Console.WriteLine("NESTED_MENUS " + JsonSerializer.Serialize(branch.Choices.Select(choice => new {
                choice.Source, choice.MenuSource, choice.Context, choice.Minimum, choice.Maximum, choice.Options, choice.Cards } )));
        }
        finally { nativePrefix.Dispose(); MegaCrit.Sts2.Core.Context.LocalContext.NetId = savedLocalId; }
    }

    private static void ArmamentsMenu(bool noUpgradable)
    {
        var (p, c, _) = Board(BotRegistry.CreateId(BotDifficulty.Pro, 236, noUpgradable ? 1 : 0),
            "LATEST-ARMAMENTS-" + noUpgradable);
        using var turn = new NativeTurnState(c);
        var source = c.CreateCard<Armaments>(p); var first = c.CreateCard<StrikeIronclad>(p);
        var second = c.CreateCard<StrikeIronclad>(p); second.UpgradeInternal();
        if (noUpgradable) first.UpgradeInternal();
        foreach (var card in new CardModel[] { source, first, second }) p.PlayerCombatState!.Hand.AddInternal(card);
        var root = KernelSession.Capture(c); var before = root.StateText(p);
        KernelSession.CardBranch branch;
        using (KernelChoiceContinuation.Enter()) branch = root.CardBranches(source, null, 1).Single();
        Check(branch.Boundary.Length == 0 && branch.Choices.Count == 1
            && branch.Choices[0].Options?.Count == (noUpgradable ? 0 : 1),
            "Armaments projection must capture exactly the native upgradable subset, including an empty subset");
        using var transaction = new Transaction(p, c, source, JsonSerializer.Deserialize<KernelChoice[]>(
            JsonSerializer.Serialize(branch.Choices))!);
        using var nativePrefix = new NativePrefixScope("coopbots.latest-native-armaments-" + noUpgradable);
        nativePrefix.Add(AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade)),
            AccessTools.Method(typeof(BotSingleCardSelectPatch), "Prefix"));
        try
        {
            PlayNative(source, null, PileType.Discard);
            Check(transaction.Index == 1 && !transaction.Diverged && transaction.CacheIntact,
                "real Armaments native upgrade filtering must match the transported complete menu without false invalidation");
            Check(first.CurrentUpgradeLevel == 1 && second.CurrentUpgradeLevel == 1
                && p.Creature.Block == branch.State.Block(p.Creature)
                && p.PlayerCombatState!.Energy == branch.State.Energy(p)
                && KernelChoice.MenuTokens(p.PlayerCombatState.Hand.Cards.ToArray()).SequenceEqual(
                    KernelChoice.MenuTokens(branch.State.Hand(p)))
                && p.PlayerCombatState.DiscardPile.Cards.SequenceEqual(branch.State.Discard(p)),
                "native Armaments upgrade, block, resources and physical piles must match projection for mixed and empty menus");
            Check(root.StateText(p) == before, "Armaments native upgrade must preserve its captured parent");
            Console.WriteLine("ARMAMENTS " + noUpgradable + " " + JsonSerializer.Serialize(branch.Choices));
        }
        finally { nativePrefix.Dispose(); }
    }

    private static void SeekerEmpty()
    {
        var (p, c, enemy) = Board(BotRegistry.CreateId(BotDifficulty.Pro, 237, 0), "LATEST-SEEKER-EMPTY");
        using var turn = new NativeTurnState(c);
        var source = c.CreateCard<SeekerStrike>(p); p.PlayerCombatState!.Hand.AddInternal(source);
        var root = KernelSession.Capture(c); var before = root.StateText(p);
        var selectionRngBefore = root.Simulator.Rng.CombatCardSelection.Counter();
        KernelSession.CardBranch branch;
        using (KernelChoiceContinuation.Enter()) branch = root.CardBranches(source, enemy, 1).Single();
        Check(branch.Boundary.Length == 0 && branch.Choices.Count == 1
            && branch.Choices[0].Options?.Count == 0 && branch.Choices[0].Cards.Count == 0
            && branch.Choices[0].Minimum == 0 && branch.Choices[0].RequestedMinimum == 1,
            "native empty Seeker draw-pile menu must retain its implicit slot and requested one-card bounds");
        using var transaction = new Transaction(p, c, source, branch.Choices.ToArray());
        using var nativePrefix = new NativePrefixScope("coopbots.latest-native-seeker-empty");
        foreach (var method in typeof(CardSelectCmd).GetMethods().Where(m => m.Name == nameof(CardSelectCmd.FromCombatPile)))
            nativePrefix.Add(method, AccessTools.Method(typeof(BotMultipleCardSelectPatch), "Prefix"));
        try
        {
            PlayNative(source, enemy, PileType.Discard);
            Check(transaction.Index == 1 && !transaction.Diverged && transaction.CacheIntact
                && enemy.CurrentHp == branch.State.Hp(enemy)
                && p.PlayerCombatState.Energy == branch.State.Energy(p)
                && p.PlayerCombatState.DiscardPile.Cards.SequenceEqual(branch.State.Discard(p))
                && c.RunState.Rng.CombatCardSelection.Counter() == selectionRngBefore
                && branch.State.Simulator.Rng.CombatCardSelection.Counter() == selectionRngBefore,
                "real Seeker empty selection must match native attack, resources and zero shuffle consumption");
            Check(root.StateText(p) == before, "empty Seeker must preserve its captured parent");
        }
        finally { nativePrefix.Dispose(); }
    }

    private static int observedPileMenus;
    private static void ObservePileMenu() => observedPileMenus++;
    private static void CapacityNoMenu()
    {
        foreach (var neow in new[] { false, true })
        {
            var (p, c, enemy) = Board(BotRegistry.CreateId(BotDifficulty.Pro, 238, neow ? 1 : 0),
                "LATEST-CAPACITY-NO-MENU-" + neow);
            using var turn = new NativeTurnState(c);
            // A native autoplay can resolve from PlayPile while the hand is full.
            CardModel source = neow ? c.CreateCard<NeowsFury>(p) : c.CreateCard<Dredge>(p);
            for (var i = 0; i < 10; i++) p.PlayerCombatState!.Hand.AddInternal(c.CreateCard<Dazed>(p));
            p.PlayerCombatState!.PlayPile.AddInternal(source);
            var root = KernelSession.Capture(c);
            var predictedCard = root.Simulator.State.FindCard(source)!;
            Check(CardChoiceSupport.GetSpec(root.Simulator, predictedCard) is null,
                "native full-hand Dredge and NeowsFury must not invent a menu when their capacity gate skips selection");
            observedPileMenus = 0;
            var harmony = new Harmony("coopbots.latest-native-capacity-" + neow);
            foreach (var method in typeof(CardSelectCmd).GetMethods().Where(m => m.Name == nameof(CardSelectCmd.FromCombatPile)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(LatestRunChoiceRepairScenarios), nameof(ObservePileMenu)));
            try
            {
                var play = new CardPlay { Card = source, Player = p, Target = neow ? enemy : null,
                    ResultPile = PileType.Exhaust, Resources = default, IsAutoPlay = true, PlayIndex = 0, PlayCount = 1 };
                ((Task)AccessTools.Method(source.GetType(), "OnPlay").Invoke(source,
                    [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
                Check(observedPileMenus == 0 && p.PlayerCombatState.Hand.Cards.Count == 10,
                    "real full-hand native capacity gates must skip every combat-pile selection call");
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        }
    }

    private static void PlayNative(CardModel source, Creature? target, PileType result)
    {
        var p = source.Owner; Check(source.CanPlay() && source.IsValidTarget(target), "native menu card must be payable and legal");
        var spent = source.SpendResources().GetAwaiter().GetResult();
        p.PlayerCombatState!.Hand.RemoveInternal(source); p.PlayerCombatState.PlayPile.AddInternal(source);
        var play = new CardPlay { Card = source, Player = p, Target = target, ResultPile = result,
            Resources = new ResourceInfo { EnergySpent = spent.Item1, EnergyValue = spent.Item1,
                StarsSpent = spent.Item2, StarValue = spent.Item2 }, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        ((Task)AccessTools.Method(source.GetType(), "OnPlay").Invoke(source,
            [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
        p.PlayerCombatState.PlayPile.RemoveInternal(source);
        if (result == PileType.Exhaust) p.PlayerCombatState.ExhaustPile.AddInternal(source);
        else p.PlayerCombatState.DiscardPile.AddInternal(source);
    }

    private sealed class NativeTurnState : IDisposable
    {
        private readonly FieldInfo turnField = AccessTools.Field(typeof(CombatManager), "_turnState");
        private readonly PropertyInfo state = AccessTools.Property(typeof(RunManager), "State");
        private readonly PropertyInfo ascension = AccessTools.Property(typeof(RunManager), nameof(RunManager.AscensionManager));
        private readonly object? priorTurn, priorState, priorAscension;
        private readonly ulong? localId = MegaCrit.Sts2.Core.Context.LocalContext.NetId;
        internal NativeTurnState(CombatState c)
        {
            priorTurn = turnField.GetValue(CombatManager.Instance); priorState = state.GetValue(RunManager.Instance);
            priorAscension = ascension.GetValue(RunManager.Instance);
            state.SetValue(RunManager.Instance, c.RunState);
            ascension.SetValue(RunManager.Instance, new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(c.RunState.AscensionLevel));
            MegaCrit.Sts2.Core.Context.LocalContext.NetId = c.Players[0].NetId;
            var turn = Activator.CreateInstance(turnField.FieldType, Instance, null, [c], null)!;
            AccessTools.Property(turnField.FieldType, "IsInProgress").SetValue(turn, true);
            AccessTools.Property(turnField.FieldType, "IsStarting").SetValue(turn, false);
            turnField.SetValue(CombatManager.Instance, turn);
        }
        public void Dispose()
        {
            turnField.SetValue(CombatManager.Instance, priorTurn); state.SetValue(RunManager.Instance, priorState);
            ascension.SetValue(RunManager.Instance, priorAscension); MegaCrit.Sts2.Core.Context.LocalContext.NetId = localId;
        }
    }

    private static void NestedCursor()
    {
        var (p, c, _) = Board(BotRegistry.CreateId(BotDifficulty.Pro, 233, 0), "LATEST-NESTED-CURSOR");
        var strike = c.CreateCard<StrikeSilent>(p); p.PlayerCombatState!.Hand.AddInternal(strike);
        var root = KernelSession.Capture(c); var options = root.Simulator.State.GetPlayerCombatState(p).Hand.Cards.ToArray();
        var spec = new CardChoiceSpec(PlanChoiceEffect.Discard, PileType.Hand, 2, 2, options, options, 0,
            IsImplicitAllSelection: true);
        var token = CardChoiceSupport.BuildChoices(spec, null, 8, 8).Single();
        var inner = token with { SourceId = "inner", ContextId = "inner-menu" };
        var outer = token with { SourceId = "outer", ContextId = "outer-menu" };
        var innerRequest = new TurnStartChoiceRequest("inner", PlanChoiceEffect.Discard, PileType.Hand, 2,
            spec, "inner-menu", MenuSource: "HIDDEN_DAGGERS");
        var outerRequest = innerRequest with { SourceId = "outer", ContextId = "outer-menu" };
        var cursor = new TurnStartChoiceCursor([inner, outer]);
        using (cursor.BeforeNextTake(() => cursor.TryTake(innerRequest, out _)))
            Check(cursor.TryTake(outerRequest, out var selected) && ReferenceEquals(selected, outer),
                "nested before-take must consume its own short menu before matching the outer context");
        cursor.AssertConsumed();
        Check(new TurnStartChoiceCursor([inner]).TryTake(innerRequest, out _),
            "cursor cardinality must clamp a two-card request to its one-card implicit native menu");
        void Reject(PlanCardChoice planned, TurnStartChoiceRequest actual, string why)
        {
            try { new TurnStartChoiceCursor([planned]).TryTake(actual, out _); }
            catch (InvalidPlannedChoiceBranchException) { return; }
            Check(false, why);
        }
        Reject(inner, innerRequest with { ContextId = "another-menu" },
            "an explicit cursor must reject a different nested context even when tokens and counts coincide");
        Reject(inner with { Cards = [] }, innerRequest,
            "an explicit cursor must reject a selected-token count below the native effective minimum");
    }

    private static void RoomStart()
    {
        var players = Enumerable.Range(0, 2).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 234, i))).ToArray();
        var state = RunState.CreateForTest(players, seed: "LATEST-EVENT-START");
        var manager = (RunManager)RuntimeHelpers.GetUninitializedObject(typeof(RunManager));
        var net = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        using var sync = new EventSynchronizer(new RunLocationTargetedMessageBuffer(net), net, state, state, players[0].NetId, 42);
        Set(manager, nameof(RunManager.EventSynchronizer), sync);
        var canonical = ModelDb.Event<BattlewornDummy>(); state.PushRoom(new EventRoom(canonical));
        var events = (List<EventModel>)AccessTools.Field(typeof(EventSynchronizer), "_events").GetValue(sync)!;
        Check(RoomWork.Waiting(manager, state) == "event choices/effects pending",
            "an event room installed before BeginEvent must wait without indexing an empty seat list");
        events.Add(canonical.ToMutable());
        Check(RoomWork.Waiting(manager, state) == "event choices/effects pending",
            "a partially initialized multiplayer event must wait before indexing its second seat");
        events.Add(canonical.ToMutable());
        foreach (var e in events) Set(e, nameof(EventModel.IsFinished), true);
        var effects = (List<Task>)AccessTools.Field(typeof(EventSynchronizer), "_pendingOptionTasks").GetValue(sync)!;
        var pending = new TaskCompletionSource(); effects.Add(pending.Task);
        Check(RoomWork.Waiting(manager, state) == "event choices/effects pending",
            "finished event menus must still wait for actual pending effects");
        pending.SetResult();
        Check(RoomWork.Waiting(manager, state) is null, "fully initialized completed event effects must release departure");
    }

    private static void Wiring()
    {
        var call = AccessTools.Method(typeof(BotCardChoiceDispatcher), "TrySelectCore");
        Check(PatchProcessor.GetOriginalInstructions(call).Any(i => i.operand is MethodInfo m
            && m.DeclaringType == typeof(BotChoicePlanSync) && m.Name == "SelectMenu"),
            "the production native dispatcher must call source-aware planned-menu matching");
        var cursor = AccessTools.Method(typeof(TurnStartChoiceCursor), "Matches");
        Check(PatchProcessor.GetOriginalInstructions(cursor).Any(i => i.operand is MethodInfo m
            && m.DeclaringType == typeof(Math) && m.Name == nameof(Math.Min)),
            "the production explicit cursor must validate native effective choice bounds");
    }

    private sealed class Transaction : IDisposable
    {
        private readonly KernelCombatPlanner planner = (KernelCombatPlanner)AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
        private readonly Dictionary<FieldInfo, object?> savedPlanner;
        private readonly Dictionary<FieldInfo, object?> savedSync;
        private readonly object? savedIdentity;
        private readonly Player player;
        private readonly CombatState combat;
        private readonly CardModel source;
        private readonly PlayCardAction action;
        private readonly KernelTeamSearch.Action[] script;
        internal int Index => (int)AccessTools.Field(typeof(BotChoicePlanSync), "index").GetValue(null)!;
        internal bool Diverged => (bool)AccessTools.Field(typeof(BotChoicePlanSync), "choiceDiverged").GetValue(null)!;
        internal bool CacheIntact => ReferenceEquals(AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScript").GetValue(planner), script);
        internal Transaction(Player p, CombatState c, CardModel card, KernelChoice[] choices)
        {
            player = p; combat = c; source = card;
            savedPlanner = typeof(KernelCombatPlanner).GetFields(Instance).Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(planner));
            savedSync = typeof(BotChoicePlanSync).GetFields(Static).Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(null));
            savedIdentity = AccessTools.Field(typeof(BotRuntime), "_combatIdentity").GetValue(null);
            var executor = (ActionExecutor)RuntimeHelpers.GetUninitializedObject(typeof(ActionExecutor));
            var manager = (RunManager)RuntimeHelpers.GetUninitializedObject(typeof(RunManager));
            Set(manager, nameof(RunManager.ActionExecutor), executor);
            AccessTools.Field(typeof(BotChoicePlanSync), "manager").SetValue(null, manager);
            AccessTools.Field(typeof(BotRuntime), "_combatIdentity").SetValue(null, c);
            action = new PlayCardAction(p, NetCombatCard.ForTesting(0), card.Id, null);
            Set(action, nameof(GameAction.Id), (uint?)41); Set(executor, nameof(ActionExecutor.CurrentlyRunningAction), action);
            script = [new(p, card, null), new(p, card, null)]; Reinstall(choices);
        }
        internal void Reinstall(KernelChoice[] choices)
        {
            planner.Reset(); Set(planner, "tournamentScript", script); Set(planner, "tournamentScriptCombat", combat);
            Set(planner, "rootCombat", combat);
            AccessTools.Field(typeof(BotChoicePlanSync), "installed").SetValue(null,
                new BotChoicePlanMessage { Action = 41, Bot = player.NetId, Card = source.Id.Entry });
            AccessTools.Field(typeof(BotChoicePlanSync), "choices").SetValue(null, choices);
            AccessTools.Field(typeof(BotChoicePlanSync), "index").SetValue(null, 0);
            AccessTools.Field(typeof(BotChoicePlanSync), "choiceDiverged").SetValue(null, false);
        }
        public void Dispose()
        {
            planner.Reset(); foreach (var saved in savedPlanner) saved.Key.SetValue(planner, saved.Value);
            foreach (var saved in savedSync) saved.Key.SetValue(null, saved.Value);
            AccessTools.Field(typeof(BotRuntime), "_combatIdentity").SetValue(null, savedIdentity);
        }
    }

    private static (Player, CombatState, Creature) Board(ulong id, string seed)
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, id);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: seed));
        p.ResetCombatState(); c.AddPlayer(p); c.RoundNumber = 1; p.MaxEnergy = 3;
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        var enemy = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (p, c, enemy);
    }
    private static void Set(object obj, string member, object? value)
    {
        for (var type = obj.GetType(); type is not null; type = type.BaseType)
        {
            var flags = Instance | BindingFlags.DeclaredOnly;
            if (type.GetField(member, flags) is { } field) { field.SetValue(obj, value); return; }
            if (type.GetProperty(member, flags) is { SetMethod: not null } property) { property.SetValue(obj, value); return; }
            if (type.GetField("<" + member + ">k__BackingField", flags) is { } backing) { backing.SetValue(obj, value); return; }
        }
        throw new MissingMemberException(obj.GetType().Name, member);
    }
}
