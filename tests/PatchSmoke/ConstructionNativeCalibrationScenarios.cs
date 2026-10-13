using System.Reflection;
using CoopBots.Building;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using Calibration = CoopBots.Building.ConstructionNativeCalibration;
using Mechanisms = CoopBots.Building.ConstructionMechanismFacts;

// R2: actual isolated production mutations, compile 0 / named RED 1 /
// exact source-byte restore compile 0 / GREEN 0. Raw logs, immediate
// exits, physical DLL/PDB and source bytes: work/architecture-refactor-
// 20261009/construction-calibration/r2/proof-construction.json.
// profile-fact-wiring/stats: System.InvalidOperationException: Construction native calibration: production profiles must expose the common mechanism facts
// temporary-lifetime/stats: System.InvalidOperationException: Construction native calibration: temporary native Strength Dexterity and Focus must declare owner-side expiry
// grant-amount/stats: System.InvalidOperationException: Construction native calibration: printed base and upgraded grants must match actual native stat gain: SetupStrikeFalse
// grant-after-own-hit/stats: System.InvalidOperationException: Construction native calibration: temporary Strength received after native damage must not be attributed to its own first hit
// draw-amount/flow: System.InvalidOperationException: Construction native calibration: reviewed draw amounts must match actual native base and upgrade draw: PommelStrikeFalse
// selected-exhaust/flow: System.InvalidOperationException: Construction native calibration: selected exhaust and self-exhaust must remain separate native quantities
// self-exhaust/flow: System.InvalidOperationException: Construction native calibration: selected exhaust and self-exhaust must remain separate native quantities
// hand-payment-wiring/flow: System.InvalidOperationException: Construction native calibration: production net draw must use the common hand-payment facts
// paired-attribution/paired-round: System.InvalidOperationException: Construction native calibration: paired attribution must preserve native quantities and proxy quantities in separate units
// important-funding/funding: System.InvalidOperationException: Construction native calibration: finite evidence funding must prioritize important candidates without duplicate samples or extra native calls
// duplicate-identity/funding: System.InvalidOperationException: Construction native calibration: finite evidence funding must prioritize important candidates without duplicate samples or extra native calls
// action-budget/budget: System.InvalidOperationException: Construction native calibration: an unfinished native witness must not exceed its action budget or become complete evidence
// original-deadline/budget: System.InvalidOperationException: Construction native calibration: atomic native overrun may finish its witness but cannot fund commands after the original soft deadline
// scenario-identity/identity: System.InvalidOperationException: Construction native calibration: native observations from another scenario must not certify the current candidate
// partial-observation/identity: System.InvalidOperationException: Construction native calibration: partial native observations cannot produce a completed calibration delta
// game-source-identity/identity: System.InvalidOperationException: Construction native calibration: mechanism facts and native calibration must share the actual game source identity
// Native effects use actual game commands/OnPlay and actual side-end hooks.
// Resource/pile dispatch is explicit; this is a bounded mechanism/one-round
// witness, not a complete native CardPlayWrapper or multi-fight win-rate study.
internal static class ConstructionNativeCalibrationScenarios
{
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.construction-native-calibration");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        var version = AccessTools.Method(typeof(PeerVersionInfo), nameof(PeerVersionInfo.LocalDefault));
        harmony.Patch(format, prefix: new HarmonyMethod(typeof(ConstructionNativeCalibrationScenarios), nameof(Format)));
        harmony.Patch(version, prefix: new HarmonyMethod(typeof(ConstructionNativeCalibrationScenarios), nameof(Version)));
        try
        {
            foreach (var test in new (string Name, Action Run)[] {
                ("stats", NativeStats), ("flow", NativeFlow), ("paired-round", PairedRound),
                ("funding", Funding), ("budget", AtomicBudget), ("identity", EvidenceIdentity) })
                if (caseName is null || caseName == test.Name)
                { test.Run(); Console.WriteLine("PASS: construction native calibration " + test.Name); }
        }
        finally
        {
            harmony.Unpatch(format, HarmonyPatchType.All, harmony.Id);
            harmony.Unpatch(version, HarmonyPatchType.All, harmony.Id);
        }
    }

    private static void NativeStats()
    {
        foreach (var type in new[] { typeof(SetupStrike), typeof(Anticipate), typeof(Hotfix) })
        foreach (var upgrade in new[] { false, true })
        {
            var (player, combat, enemy) = Board("CALIBRATE-STAT-" + type.Name + upgrade);
            using var run = new NativeRun(combat, player.NetId);
            var card = combat.CreateCard(ModelDb.AllCards.Single(c => c.GetType() == type), player);
            if (upgrade) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
            player.PlayerCombatState!.Hand.AddInternal(card);
            var profile = CardProfile.Of(card);
            Check(profile.Mechanisms is not null, "production profiles must expose the common mechanism facts");
            var grant = profile.Mechanisms!.StatGrant;
            Check(grant is { Lifetime: Mechanisms.Lifetime.OwnerSideEnd },
                "temporary native Strength Dexterity and Focus must declare owner-side expiry");
            var expected = grant!.Value;
            NativePlay(card, card.TargetType == TargetType.AnyEnemy ? enemy : null);
            var active = ReadStat(player.Creature, expected.Stat);
            Check(active == expected.Amount, "printed base and upgraded grants must match actual native stat gain: " + type.Name + upgrade);
            if (card is SetupStrike)
                Check(expected.AfterOwnDamage && 600 - enemy.CurrentHp == (upgrade ? 9 : 7),
                    "temporary Strength received after native damage must not be attributed to its own first hit");
            Hook.AfterSideTurnEnd(combat, CombatSide.Player, [player.Creature]).GetAwaiter().GetResult();
            Check(ReadStat(player.Creature, expected.Stat) == 0,
                "declared temporary stat must expire at the actual native owner-side boundary");
            Console.WriteLine($"CALIBRATION-NATIVE stat card={card.Id.Entry} upgrade={card.CurrentUpgradeLevel} during={active} after=0 source={expected.Source}");
        }
        Check(Mechanisms.PowerLifetime(typeof(StrengthPower)) == Mechanisms.Lifetime.Persistent
            && Mechanisms.PowerLifetime(typeof(DexterityPower)) == Mechanisms.Lifetime.Persistent
            && Mechanisms.PowerLifetime(typeof(FocusPower)) == Mechanisms.Lifetime.Persistent,
            "ordinary permanent stat powers must retain distinct lifetime facts");
        var unreviewed = Mechanisms.Read(ModelDb.Card<Bash>().ToMutable());
        Check(unreviewed.StatGrant is null && !unreviewed.DrawReviewed && !unreviewed.HandSelectionReviewed,
            "unreviewed mechanism quantities must remain unknown instead of certified zero effects");
    }

    private static void NativeFlow()
    {
        // Native FromHand reserves/sends a human choice before its implicit-all
        // shortcut unless a selector is present. Own a scoped selector so this
        // offline witness uses the native shortcut without a live network/UI.
        using var selector = CardSelectCmd.PushSelector(new MegaCrit.Sts2.Core.TestSupport.TestCardSelector());
        foreach (var type in new[] { typeof(PommelStrike), typeof(BurningPact), typeof(Prepared), typeof(Adrenaline) })
        foreach (var upgrade in new[] { false, true })
        {
            var (player, combat, enemy) = Board("CALIBRATE-FLOW-" + type.Name + upgrade);
            using var run = new NativeRun(combat, player.NetId);
            var state = player.PlayerCombatState!;
            var card = combat.CreateCard(ModelDb.AllCards.Single(c => c.GetType() == type), player);
            if (upgrade) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
            state.Hand.AddInternal(card);
            if (card is BurningPact) state.Hand.AddInternal(combat.CreateCard<DefendIronclad>(player));
            for (var i = 0; i < 8; i++) state.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(player));
            var facts = Mechanisms.Read(card);
            var drawBefore = state.DrawPile.Cards.Count;
            var exhaustBefore = state.ExhaustPile.Cards.Count;
            var discardBefore = state.DiscardPile.Cards.Count;
            NativePlay(card, card.TargetType == TargetType.AnyEnemy ? enemy : null);
            Check(facts.GrossDraw == drawBefore - state.DrawPile.Cards.Count,
                "reviewed draw amounts must match actual native base and upgrade draw: " + type.Name + upgrade);
            Check(state.ExhaustPile.Cards.Count - exhaustBefore
                == facts.RequiredExhaust + (facts.ExhaustsSelf ? 1 : 0),
                "selected exhaust and self-exhaust must remain separate native quantities");
            if (card is Prepared)
                Check(state.DiscardPile.Cards.Count - discardBefore == facts.RequiredDiscard + 1,
                    "Prepared must pay its actual draw count beside its own original hand slot");
            var profile = CardProfile.Of(card);
            var actualDraw = drawBefore - state.DrawPile.Cards.Count;
            var actualExhaustPayment = state.ExhaustPile.Cards.Count - exhaustBefore - (facts.ExhaustsSelf ? 1 : 0);
            var actualDiscardPayment = state.DiscardPile.Cards.Count - discardBefore - (facts.ExhaustsSelf ? 0 : 1);
            Check(CardProfile.NetDraw(card, profile)
                == Math.Max(0, actualDraw - 1 - actualExhaustPayment - actualDiscardPayment),
                "production net draw must use the common hand-payment facts");
            Console.WriteLine($"CALIBRATION-NATIVE flow card={card.Id.Entry} upgrade={card.CurrentUpgradeLevel} draw={actualDraw} discardPayment={actualDiscardPayment} exhaustPayment={actualExhaustPayment} selfExhaust={facts.ExhaustsSelf}");
        }
    }

    private static void PairedRound()
    {
        var (player, combat, enemy) = Board("CALIBRATE-PAIRED-ROUND");
        using var run = new NativeRun(combat, player.NetId);
        var cards = new CardModel[] { combat.CreateCard<SetupStrike>(player), combat.CreateCard<StrikeIronclad>(player),
            combat.CreateCard<DefendIronclad>(player) };
        foreach (var card in cards) player.PlayerCombatState!.Hand.AddInternal(card);
        // The proxy optimizes this complete affordable three-card hand. The
        // native witness explicitly plays those legal cards in native timing.
        var cheap = ConstructionHand.Solve(cards.Select(c => new ConstructionHand.Slot(c, CardProfile.Of(c))).ToArray(),
            cards.Length, new(3, 1, 600));
        var mechanisms = Mechanisms.Read(cards[0]);
        var scenario = Scenario("paired-round", "CALIBRATE-PAIRED-ROUND", 3, 1, 600);
        var request = new Calibration.Request("setup-line", true, 0, scenario,
            new(cheap.Damage, cheap.Block, 0, 0, CardProfile.Of(cards[0]).Effects.TemporaryStrength, 0, 0,
                cheap.Utility, "construction-hand utility; not probability"), mechanisms);
        var evidence = new Calibration.Session(new(1, 1, 5, 10_000)).Observe(request, scope => {
            var rngBefore = Rng((RunState)combat.RunState);
            var hpBefore = player.Creature.CurrentHp;
            foreach (var card in cards) scope.Execute(() => NativePlay(card, card.Type == CardType.Attack ? enemy : null));
            var damage = 600 - enemy.CurrentHp; var block = player.Creature.Block;
            var strength = ReadStat(player.Creature, Mechanisms.Stat.Strength);
            scope.Execute(() => Hook.AfterSideTurnEnd(combat, CombatSide.Player, [player.Creature]).GetAwaiter().GetResult());
            scope.Execute(() => enemy.Monster!.NextMove.PerformMove([player.Creature]).GetAwaiter().GetResult());
            return new(scenario, damage, block, hpBefore - player.Creature.CurrentHp, 0, 0, strength, 0, 0,
                ReadStat(player.Creature, Mechanisms.Stat.Strength), 0, 0, true, scenario.Boundary,
                rngBefore, Rng((RunState)combat.RunState));
        });
        Check(evidence.Complete && evidence.NativeActions == 5 && evidence.Native!.Damage == 16
            && evidence.Native.StrengthDuring == 3 && evidence.Native.StrengthAfter == 0,
            "paired evidence must contain actual native damage stat expiry and the completed enemy boundary");
        var witness = evidence.Native!;
        var delta = evidence.NativeMinusProxy!;
        Check(delta.Damage == witness.Damage - cheap.Damage
            && delta.Block == witness.Block - cheap.Block,
            "paired attribution must preserve native quantities and proxy quantities in separate units");
        Check(evidence.JsonLine().Contains("construction-hand utility; not probability", StringComparison.Ordinal)
            && witness.RngBefore.Length > 0 && witness.RngAfter.Length > 0,
            "calibration output must retain score units and actual native RNG identities");
        Console.WriteLine("CALIBRATION-PAIR " + evidence.JsonLine());
    }

    private static void Funding()
    {
        var ordinary = Request("ordinary", false); var important = Request("important", true);
        var calls = new List<string>();
        // Keep room for a second sample/action so a duplicate-identity regression
        // really invokes the oracle twice instead of hiding behind quota exhaustion.
        var result = Calibration.Collect([ordinary, important, important], new(1, 2, 3, 10_000), (request, scope) => {
            scope.Execute(() => calls.Add(request.CandidateKey)); return EmptyNative(request);
        });
        Check(calls.SequenceEqual(["important"]) && result.Count(r => r.Complete) == 1
            && result[1].Status == "duplicate" && result[2].Status == "not-funded",
            "finite evidence funding must prioritize important candidates without duplicate samples or extra native calls");
    }

    private static void AtomicBudget()
    {
        var request = Request("atomic", true); var actual = 0;
        var limited = new Calibration.Session(new(1, 1, 1, 10_000)).Observe(request, scope => {
            scope.Execute(() => actual++); scope.Execute(() => actual++); return EmptyNative(request);
        });
        Check(actual == 1 && !limited.Complete && limited.Native is null && limited.Status == "budget-exhausted",
            "an unfinished native witness must not exceed its action budget or become complete evidence");
        var clock = new ManualClock(); var session = new Calibration.Session(new(2, 1, 4, 10), clock);
        var first = session.Observe(request, scope => { scope.Execute(() => clock.Advance(20)); return EmptyNative(request); });
        var second = session.Observe(Request("late", true), scope => { scope.Execute(() => actual++); return EmptyNative(request); });
        Check(first.Complete && !second.Complete && second.NativeActions == 0 && actual == 1,
            "atomic native overrun may finish its witness but cannot fund commands after the original soft deadline");
    }

    private static void EvidenceIdentity()
    {
        var request = Request("identity", true);
        var wrong = new Calibration.Session(new(1, 1, 1, 10_000)).Observe(request, scope => {
            scope.Execute(() => { }); return EmptyNative(request) with { Scenario = request.Scenario with { Seed = "another-seed" } };
        });
        var partial = new Calibration.Session(new(1, 1, 1, 10_000)).Observe(request, scope => {
            scope.Execute(() => { }); return EmptyNative(request) with { Completed = false };
        });
        var invoked = false;
        var stale = new Calibration.Session(new(1, 1, 1, 10_000)).Observe(request with {
            Scenario = request.Scenario with { GameMvid = "old-game" } }, scope => { invoked = true; return EmptyNative(request); });
        Check(!wrong.Complete && wrong.Status == "scenario-mismatch" && wrong.NativeMinusProxy is null,
            "native observations from another scenario must not certify the current candidate");
        Check(!partial.Complete && partial.NativeMinusProxy is null,
            "partial native observations cannot produce a completed calibration delta");
        Check(!invoked && stale.Status == "source-mismatch",
            "mechanism facts and native calibration must share the actual game source identity");
    }

    private static Calibration.Request Request(string key, bool important)
    {
        var card = ModelDb.Card<SetupStrike>().ToMutable();
        return new(key, important, 0, Scenario(key, "CALIBRATE-BUDGET", 3, 1, 600),
            new(0, 0, 0, 0, 0, 0, 0, 0, "test quantities; not probability"), Mechanisms.Read(card));
    }
    private static Calibration.Scenario Scenario(string key, string seed, double energy, double incoming, double hp)
        => new(key, typeof(CardModel).Module.ModuleVersionId.ToString("D"), seed, 1, energy, incoming, hp, "native-owner-side-ended");
    private static Calibration.Native EmptyNative(Calibration.Request request)
        => new(request.Scenario, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true,
            request.Scenario.Boundary, "fixture-before", "fixture-after");
    private static double ReadStat(Creature creature, Mechanisms.Stat stat) => stat switch {
        Mechanisms.Stat.Strength => creature.GetPower<StrengthPower>()?.Amount ?? 0,
        Mechanisms.Stat.Dexterity => creature.GetPower<DexterityPower>()?.Amount ?? 0,
        _ => creature.GetPower<FocusPower>()?.Amount ?? 0 };
    private static string Rng(RunState run) => string.Join("|", run.Rng.Shuffle.CaptureState(),
        run.Rng.CombatCardGeneration.CaptureState(), run.Rng.CombatPotionGeneration.CaptureState(),
        run.Rng.CombatCardSelection.CaptureState(), run.Rng.CombatEnergyCosts.CaptureState(),
        run.Rng.CombatTargets.CaptureState(), run.Rng.CombatOrbGeneration.CaptureState(),
        run.Rng.MonsterAi.CaptureState(), run.Rng.Niche.CaptureState());
    private static (Player, CombatState, Creature) Board(string seed)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, 76561198000100990UL);
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: seed));
        player.ResetCombatState(); combat.AddPlayer(player); player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "calibration");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(600); enemy.SetCurrentHpInternal(600);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (player, combat, enemy);
    }
    private static void NativePlay(CardModel card, Creature? target)
    {
        var state = card.Owner.PlayerCombatState!;
        Check(card.CanPlay() && card.IsValidTarget(target), "native calibration card must be actually playable: " + card.Id.Entry);
        var spent = card.SpendResources().GetAwaiter().GetResult();
        state.Hand.RemoveInternal(card); state.PlayPile.AddInternal(card);
        var result = card.Type == CardType.Power ? PileType.None
            : card.Keywords.Contains(CardKeyword.Exhaust) ? PileType.Exhaust : PileType.Discard;
        var play = new CardPlay { Card = card, Player = card.Owner, Target = target, ResultPile = result,
            Resources = new ResourceInfo { EnergySpent = spent.Item1, EnergyValue = spent.Item1, StarsSpent = spent.Item2, StarValue = spent.Item2 },
            IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        ((Task)AccessTools.Method(card.GetType(), "OnPlay").Invoke(card, [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
        state.PlayPile.RemoveInternal(card);
        if (result == PileType.Exhaust) state.ExhaustPile.AddInternal(card);
        else if (result == PileType.Discard) state.DiscardPile.AddInternal(card);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(long milliseconds) => timestamp += milliseconds;
    }
    private sealed class NativeRun : IDisposable
    {
        private readonly PropertyInfo state = AccessTools.Property(typeof(RunManager), "State");
        private readonly PropertyInfo ascension = AccessTools.Property(typeof(RunManager), nameof(RunManager.AscensionManager));
        private readonly PropertyInfo net = AccessTools.Property(typeof(RunManager), nameof(RunManager.NetService));
        private readonly FieldInfo turn = AccessTools.Field(typeof(CombatManager), "_turnState");
        private readonly object? previous;
        private readonly object? previousAscension;
        private readonly object? previousNet;
        private readonly object? previousTurn;
        private readonly ulong? previousId = LocalContext.NetId;
        internal NativeRun(CombatState combat, ulong netId)
        {
            var run = (RunState)combat.RunState;
            previous = state.GetValue(RunManager.Instance); previousAscension = ascension.GetValue(RunManager.Instance);
            previousNet = net.GetValue(RunManager.Instance); previousTurn = turn.GetValue(CombatManager.Instance);
            state.SetValue(RunManager.Instance, run);
            ascension.SetValue(RunManager.Instance, new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(run.AscensionLevel));
            net.SetValue(RunManager.Instance, new NetReplayGameService(1)); LocalContext.NetId = netId;
            var current = Activator.CreateInstance(turn.FieldType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
            AccessTools.Property(turn.FieldType, "IsInProgress").SetValue(current, true);
            AccessTools.Property(turn.FieldType, "IsStarting").SetValue(current, false);
            turn.SetValue(CombatManager.Instance, current);
        }
        public void Dispose()
        {
            turn.SetValue(CombatManager.Instance, previousTurn); net.SetValue(RunManager.Instance, previousNet);
            state.SetValue(RunManager.Instance, previous); ascension.SetValue(RunManager.Instance, previousAscension);
            LocalContext.NetId = previousId;
        }
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool Version(ref PeerVersionInfo __result)
    { __result = new PeerVersionInfo { version = "construction-calibration", gameplayAffectingMods = [], otherMods = [] }; return false; }
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException("Construction native calibration: " + message); }
}
