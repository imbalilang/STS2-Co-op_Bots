using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Extensions;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2: byte-identical production seams frozen to an isolated Kernel mirror. No production faults.
// Each named RED below exited 1; exact-byte restoration then the same fixture exited 0.
// Commands, before/mutant/restored SHA256, native-runtime hash and raw logs:
// work/incombat-repair-20261007/temporary-stats/combat/temporary-combat-r2-results.json
// temporary-stats-debuff-full-fields:
// System.InvalidOperationException: Temporary stats combat: native temporary application must match complete power fields for CrushUnderPower/artifact0
// temporary-stats-absent-participant:
// System.InvalidOperationException: Temporary stats combat: extra turn must not expire an absent ally's temporary Dexterity
// temporary-stats-mixed-native-core-order:
// System.InvalidOperationException: Temporary stats combat: mixed temporary stats must expire in native hook acquisition order with one Artifact charge
// temporary-stats-mixed-future-order:
// System.InvalidOperationException: Temporary stats combat: future stat projection must preserve native mixed Artifact expiry order
// temporary-stats-persistent-temp-internal-stat:
// System.InvalidOperationException: Temporary stats combat: temporary Strength and Dexterity must not enter sustained power retention through their internal stats
// temporary-stats-future-vulnerable-temp-stat:
// System.InvalidOperationException: Temporary stats combat: temporary Strength must not amplify future Vulnerable turns after its owner ends
// temporary-stats-future-weak-permanent-stat:
// System.InvalidOperationException: Temporary stats combat: future Weak must price permanent enemy Strength in its restored native attack amount
// temporary-stats-future-weak-temp-rollback:
// System.InvalidOperationException: Temporary stats combat: future Weak must use restored enemy Strength rather than this-turn PiercingWail reduction
// temporary-stats-generic-temp-stat-capped-offset:
// System.InvalidOperationException: Temporary stats combat: temporary Dexterity must mirror native requested-offset callback even when wrapper counter clamps
// temporary-stats-anticipate-production-card:
// System.InvalidOperationException: Temporary stats combat: Anticipate card path must share native temporary-stat stacking semantics
// temporary-stats-native-self-target-kernel:
// System.InvalidOperationException: Temporary stats combat: AnyAlly candidates must include the native legal self target
// temporary-stats-native-self-target-vendor:
// System.InvalidOperationException: Temporary stats combat: vendor manual target enumeration must retain the native legal AnyAlly self target
// temporary-stats-native-living-ally-requirement:
// System.InvalidOperationException: Temporary stats combat: AnyAlly must reject manual play when no other living player exists, matching native CanPlay
// temporary-stats-plot-dead-seat:
// System.InvalidOperationException: Temporary stats combat: Plot must schedule draw for native living teammates including owner, excluding dead seats
// temporary-stats-actual-negative-card-pipeline:
// System.InvalidOperationException: Temporary stats combat: native card damage-before-stat and complete temporary fields must match: CrushUnder/False
internal static class TemporaryStatsCombatScenarios
{
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var h = new Harmony("coopbots.temporary-stats-combat");
        h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(TemporaryStatsCombatScenarios), nameof(Format)));
        try
        {
            foreach (var test in new (string Name, Action Run)[] {
                ("family", NativeFamily), ("participants", DexterityParticipants),
                ("persistent", Persistent), ("future", Future), ("artifact", ArtifactExpiry),
                ("mixed", MixedExpiry), ("cards", NativeCards), ("negative", NegativeFuture),
                ("targets", NativeAllyTargets), ("cap", DexterityStackCap), ("isolation", ForecastIsolation),
                ("plot", NativePlot) })
                if (caseName is null || test.Name == caseName)
                { test.Run(); Console.WriteLine("PASS: temporary-stats combat " + test.Name); }
        }
        finally { h.UnpatchAll(h.Id); }
    }

    private static void NativeFamily()
    {
        var types = typeof(SetupStrikePower).Assembly.GetTypes().Where(t => !t.IsAbstract
            && (typeof(TemporaryStrengthPower).IsAssignableFrom(t) || typeof(TemporaryDexterityPower).IsAssignableFrom(t)
                || typeof(TemporaryFocusPower).IsAssignableFrom(t))
            && !t.Namespace!.EndsWith(".Mocks")).OrderBy(t => t.Name).ToArray();
        foreach (var type in types)
        foreach (var artifact in new[] { 0, 1 })
        {
            var (p, c, _) = Board("STAT-" + type.Name + artifact);
            if (artifact > 0) Pow<ArtifactPower>(p[0].Creature, artifact);
            var root = KernelSession.Capture(c); var sim = root.Fork();
            var canonical = (PowerModel)typeof(ModelDb).GetMethod("Power")!.MakeGenericMethod(type).Invoke(null, null)!;
            var positive = canonical.Type == PowerType.Buff;
            var method = typeof(TemporaryDexterityPower).IsAssignableFrom(type) ? "ApplyTemporaryDexterity"
                : typeof(TemporaryFocusPower).IsAssignableFrom(type) ? positive ? "ApplyTemporaryFocus" : "ApplyTemporaryFocusLoss"
                : positive ? "ApplyTemporaryStrengthGain" : "ApplyTemporaryStrengthLoss";
            var apply = typeof(SimulatedCombatState).GetMethods().Single(m => m.Name == method && m.IsGenericMethodDefinition);
            apply.MakeGenericMethod(type).Invoke(Combat(sim), [p[0].Creature, 3, p[0].Creature]);
            PowerLifecycleSupport.ResolvePowerAmountChanges(sim.Simulator, Combat(sim));
            Check(p[0].Creature.Powers.All(x => x is not StrengthPower and not DexterityPower),
                "temporary stat simulation must leave its live source powers unchanged");
            PowerCmd.Apply(new ThrowingPlayerChoiceContext(), canonical.ToMutable(), p[0].Creature, 3,
                p[0].Creature, null).GetAwaiter().GetResult();
            Console.WriteLine("TEMP APPLY " + type.Name + "/artifact" + artifact + " sim=" + State(sim, p[0].Creature) + " native=" + State(p[0].Creature));
            Check(State(sim, p[0].Creature) == State(p[0].Creature),
                "native temporary application must match complete power fields for " + type.Name + "/artifact" + artifact);
            Combat(sim).RestoreTemporaryStats([p[0].Creature]);
            PowerLifecycleSupport.ResolvePowerAmountChanges(sim.Simulator, Combat(sim));
            var instance = p[0].Creature.Powers.SingleOrDefault(x => x.GetType() == type);
            instance?.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), CombatSide.Player, [p[0].Creature]).GetAwaiter().GetResult();
            Check(State(sim, p[0].Creature) == State(p[0].Creature),
                "native temporary expiry must match complete power fields for " + type.Name + "/artifact" + artifact);
            Console.WriteLine("TEMP FAMILY " + type.Name + " artifact=" + artifact + " final=" + State(p[0].Creature));
        }
        Check(types.Length == 21, "temporary stat family scan must cover seventeen Strength/Dexterity and four Focus native concrete wrappers");
    }

    private static void DexterityParticipants()
    {
        var (p, c, _) = Board("STAT-PARTICIPANTS");
        var fade = Hand<Fade>(p[0], c);
        Pow<AmbergrisPower>(p[0].Creature, 1);
        var root = KernelSession.Capture(c).ForkForReplay();
        Check(root.EndTurn(p[0], 4, out _) && root.EndTurn(p[1], 4, out _), "extra turn must open for its caster");
        // Native legal ally targeting does not require the recipient to participate.
        Check(root.Play(fade, p[1].Creature, out var played), "native Fade must target its ready ally during extra turn: " + played);
        Check(root.EndTurn(p[0], 4, out var why), "extra turn must settle after ally buff: " + why);
        Check(root.Power<DexterityPower>(p[1].Creature) == 6 && root.Power<FadePower>(p[1].Creature) == 6,
            "extra turn must not expire an absent ally's temporary Dexterity");
        var native = ModelDb.Power<FadePower>().ToMutable();
        PowerCmd.Apply(new ThrowingPlayerChoiceContext(), native, p[1].Creature, 6, p[0].Creature, fade).GetAwaiter().GetResult();
        native.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), CombatSide.Player, [p[0].Creature]).GetAwaiter().GetResult();
        Check(p[1].Creature.GetPowerAmount<DexterityPower>() == 6,
            "native temporary Dexterity must retain a nonparticipant's buff at the same side boundary");
    }

    private static void Persistent()
    {
        var (p, c, enemy) = Board("STAT-PERSISTENT");
        for (var i = 0; i < 5; i++) { Hand<StrikeIronclad>(p[0], c); Hand<DefendIronclad>(p[0], c); }
        var root = KernelSession.Capture(c); var baseValue = root.PersistentValue(p[0], 40, 3);
        var strength = root.Fork(); Combat(strength).ApplyTemporaryStrengthGain<SetupStrikePower>(p[0].Creature, 3, p[0].Creature);
        var dex = root.Fork(); Combat(dex).ApplyTemporaryDexterity<AnticipatePower>(p[0].Creature, 2, p[0].Creature);
        Settle(strength); Settle(dex);
        Check(strength.PersistentValue(p[0], 40, 3, root) == baseValue && dex.PersistentValue(p[0], 40, 3, root) == baseValue,
            "temporary Strength and Dexterity must not enter sustained power retention through their internal stats");
        var permanent = root.Fork(); Combat(permanent).Apply<StrengthPower>(p[0].Creature, 3, p[0].Creature);
        Settle(permanent);
        Check(permanent.PersistentValue(p[0], 40, 3, root) > baseValue,
            "permanent Strength must retain its existing sustained value");
    }

    private static void Future()
    {
        var (p, c, enemy) = Board("STAT-FUTURE");
        Pow<VulnerablePower>(enemy, 4);
        for (var i = 0; i < 8; i++) Hand<StrikeIronclad>(p[0], c);
        var root = KernelSession.Capture(c); var baseValue = root.FutureDebuffValue([p[0]], reference: root);
        var temp = root.Fork(); Combat(temp).ApplyTemporaryStrengthGain<SetupStrikePower>(p[0].Creature, 4, p[0].Creature);
        Settle(temp);
        Check(Math.Abs(temp.FutureDebuffValue([p[0]], reference: root) - baseValue) < .001,
            "temporary Strength must not amplify future Vulnerable turns after its owner ends");
        var permanent = root.Fork(); Combat(permanent).Apply<StrengthPower>(p[0].Creature, 4, p[0].Creature);
        Settle(permanent);
        Check(permanent.FutureDebuffValue([p[0]], reference: root) > baseValue,
            "permanent Strength must keep its future Vulnerable dividend");
    }

    private static void MixedExpiry()
    {
        foreach (var focusFirst in new[] { false, true })
        foreach (var reverse in new[] { false, true })
        {
            var (p, c, _) = Board("STAT-MIXED" + focusFirst + reverse);
            Pow<ArtifactPower>(p[0].Creature, 1);
            var root = KernelSession.Capture(c); var sim = root.Fork();
            var types = focusFirst ? new[] { typeof(HotfixPower), typeof(SetupStrikePower), typeof(AnticipatePower) }
                : new[] { typeof(AnticipatePower), typeof(SetupStrikePower) };
            if (reverse) Array.Reverse(types);
            foreach (var type in types)
            {
                var method = typeof(TemporaryFocusPower).IsAssignableFrom(type) ? "ApplyTemporaryFocus"
                    : typeof(TemporaryDexterityPower).IsAssignableFrom(type) ? "ApplyTemporaryDexterity" : "ApplyTemporaryStrengthGain";
                typeof(SimulatedCombatState).GetMethods().Single(m => m.Name == method && m.IsGenericMethodDefinition)
                    .MakeGenericMethod(type).Invoke(Combat(sim), [p[0].Creature, 3, p[0].Creature]);
                var canonical = (PowerModel)typeof(ModelDb).GetMethod("Power")!.MakeGenericMethod(type).Invoke(null, null)!;
                PowerCmd.Apply(new ThrowingPlayerChoiceContext(), canonical.ToMutable(), p[0].Creature, 3,
                    p[0].Creature, null).GetAwaiter().GetResult();
            }
            Settle(sim);
            var future = sim.AfterTemporaryStatExpiry();
            CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(sim.Simulator, Combat(sim), [p[0].Creature]); Settle(sim);
            var previous = LocalContext.NetId;
            try { LocalContext.NetId = p[0].NetId; Hook.AfterSideTurnEnd(c, CombatSide.Player, [p[0].Creature]).GetAwaiter().GetResult(); }
            finally { LocalContext.NetId = previous; }
            Console.WriteLine("MIXED " + string.Join(',', types.Select(t => t.Name)) + " sim=" + State(sim, p[0].Creature)
                + " future=" + State(future, p[0].Creature) + " native=" + State(p[0].Creature));
            Check(State(sim, p[0].Creature) == State(p[0].Creature),
                "mixed temporary stats must expire in native hook acquisition order with one Artifact charge");
            Check(State(future, p[0].Creature) == State(p[0].Creature),
                "future stat projection must preserve native mixed Artifact expiry order");
        }
    }

    private static void NativeCards()
    {
        foreach (var type in new[] { typeof(SetupStrike), typeof(Coordinate), typeof(Fade), typeof(Anticipate), typeof(FeedingFrenzy),
                     typeof(CrushUnder), typeof(DarkShackles), typeof(DyingStar), typeof(EnfeeblingTouch), typeof(Mangle), typeof(PiercingWail) })
        foreach (var upgraded in new[] { false, true })
        {
            var (p, c, enemy) = Board("STAT-CARD" + type.Name + upgraded);
            p[0].PlayerCombatState!.GainEnergy(7); p[0].PlayerCombatState.GainStars(10);
            var card = (CardModel)typeof(CombatState).GetMethods().Single(m => m.Name == "CreateCard" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(type).Invoke(c, [p[0]])!;
            if (upgraded) card.UpgradeInternal();
            p[0].PlayerCombatState!.Hand.AddInternal(card);
            Creature? target = card.TargetType == TargetType.AnyEnemy ? enemy : card.TargetType == TargetType.AnyAlly ? p[1].Creature : null;
            var sim = KernelSession.Capture(c).Fork();
            Check(sim.Play(card, target, out var why), "native temporary card must be modeled: " + type.Name + " " + why);
            var play = new CardPlay { Card = card, Player = p[0], Target = target, ResultPile = PileType.Discard,
                Resources = default, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            ((Task)AccessTools.Method(type, "OnPlay").Invoke(card, [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
            Check(p.All(owner => State(sim, owner.Creature) == State(owner.Creature)) && State(sim, enemy) == State(enemy)
                && sim.Hp(enemy) == enemy.CurrentHp,
                "native card damage-before-stat and complete temporary fields must match: " + type.Name + "/" + upgraded);
            if (type == typeof(SetupStrike)) Check(enemy.CurrentHp == 600 - (upgraded ? 9 : 7),
                "SetupStrike must not borrow its subsequent temporary Strength for its own hit");
        }
    }

    private static void NegativeFuture()
    {
        var (p, c, enemy) = Board("STAT-NEGATIVE");
        Pow<WeakPower>(enemy, 4); Pow<StrengthPower>(enemy, 9);
        for (var i = 0; i < 8; i++) Hand<StrikeIronclad>(p[0], c);
        var root = KernelSession.Capture(c); var temp = root.Fork();
        var expected = root.CurrentAttacks().Where(attack => attack.Enemy == enemy)
            .Sum(attack => Math.Max(0, (double)attack.Raw + 9) * attack.Repeats) * .25 * 3;
        Check(Math.Abs(root.FutureDebuffValue([p[0]], reference: root) - expected) < .001,
            "future Weak must price permanent enemy Strength in its restored native attack amount");
        Combat(temp).ApplyTemporaryStrengthLoss<PiercingWailPower>(enemy, 6, p[0].Creature); Settle(temp);
        Check(temp.Threat(p[0]).AttackDamage < root.Threat(p[0]).AttackDamage,
            "current enemy damage must retain the active temporary Strength reduction");
        Check(Math.Abs(temp.FutureDebuffValue([p[0]], reference: root) - root.FutureDebuffValue([p[0]], reference: root)) < .001,
            "future Weak must use restored enemy Strength rather than this-turn PiercingWail reduction");
        Check(temp.RestorableStrength(enemy) == 6, "temporary negative enemy Strength must return after its participant end");
        var native = ModelDb.Power<PiercingWailPower>().ToMutable();
        PowerCmd.Apply(new ThrowingPlayerChoiceContext(), native, enemy, 6, p[0].Creature, null).GetAwaiter().GetResult();
        native.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), CombatSide.Enemy, [enemy]).GetAwaiter().GetResult();
        Check(State(temp.AfterTemporaryStatExpiry(), enemy) == State(enemy),
            "future negative-stat restoration must match native full fields");
    }

    private static void NativeAllyTargets()
    {
        foreach (var type in new[] { typeof(Coordinate), typeof(Fade) })
        {
            var (p, c, _) = Board("STAT-TARGET" + type.Name);
            var card = (CardModel)typeof(CombatState).GetMethods().Single(m => m.Name == "CreateCard" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(type).Invoke(c, [p[0]])!;
            p[0].PlayerCombatState!.Hand.AddInternal(card);
            var sim = KernelSession.Capture(c);
            Check(card.CanPlayTargeting(p[0].Creature) && card.CanPlayTargeting(p[1].Creature),
                "native manual AnyAlly must permit self and living teammate in a multiplayer party");
            Check(sim.Targets(card).Contains(p[0].Creature) && sim.Targets(card).Contains(p[1].Creature),
                "AnyAlly candidates must include the native legal self target");
            Check(c.GetValidManualCardTargets(card).Contains(p[0].Creature),
                "vendor manual target enumeration must retain the native legal AnyAlly self target");
            var self = sim.Fork(); Check(self.Play(card, p[0].Creature, out var why), "native legal self buff must play: " + why);
            p[1].Creature.SetCurrentHpInternal(0); var solo = KernelSession.Capture(c);
            Check(!card.CanPlayTargeting(p[0].Creature) && !solo.CanPlay(card),
                "AnyAlly must reject manual play when no other living player exists, matching native CanPlay");
        }
    }

    private static void DexterityStackCap()
    {
        var (p, c, _) = Board("STAT-CAP");
        Pow<AnticipatePower>(p[0].Creature, 999_999_998);
        var sim = KernelSession.Capture(c).Fork();
        Combat(sim).ApplyTemporaryDexterity<AnticipatePower>(p[0].Creature, 4, p[0].Creature); Settle(sim);
        PowerCmd.Apply<AnticipatePower>(new ThrowingPlayerChoiceContext(), p[0].Creature, 4, p[0].Creature, null).GetAwaiter().GetResult();
        Check(State(sim, p[0].Creature) == State(p[0].Creature),
            "temporary Dexterity must mirror native requested-offset callback even when wrapper counter clamps");
        var (p2, c2, _) = Board("STAT-ANTICIPATE-CAP"); Pow<AnticipatePower>(p2[0].Creature, 999_999_998);
        var card = Hand<Anticipate>(p2[0], c2); var branch = KernelSession.Capture(c2).Fork();
        Check(branch.Play(card, null, out _), "Anticipate cap fixture must exercise its production card path");
        PowerCmd.Apply<AnticipatePower>(new ThrowingPlayerChoiceContext(), p2[0].Creature, 2, p2[0].Creature, card).GetAwaiter().GetResult();
        Check(State(branch, p2[0].Creature) == State(p2[0].Creature),
            "Anticipate card path must share native temporary-stat stacking semantics");
    }

    private static void ForecastIsolation()
    {
        var (p, c, _) = Board("STAT-FORECAST-ISOLATION");
        Pow<SetupStrikePower>(p[0].Creature, 3); Pow<StrengthPower>(p[0].Creature, 3);
        Pow<DemonFormPower>(p[0].Creature, 2);
        var root = KernelSession.Capture(c); var before = root.PartyStateText();
        var native = State(p[0].Creature); var projected = root.AfterTemporaryStatExpiry();
        Check(root.PartyStateText() == before && State(p[0].Creature) == native,
            "future stat projection must not mutate source branch, live powers, hand or RNG");
        Check(projected.Power<StrengthPower>(p[0].Creature) == 0 && projected.Power<DemonFormPower>(p[0].Creature) == 2,
            "temporary expiry must not prematurely grant recurring turn-start Strength");
        var unstable = root.Fork(); Combat(unstable).ApplyTemporaryStrengthGain<SetupStrikePower>(p[0].Creature, 1, p[0].Creature);
        var rejected = false; try { unstable.AfterTemporaryStatExpiry(); }
        catch (InvalidOperationException e) when (e.Message.StartsWith("Cannot fork with pending Power amount changes")) { rejected = true; }
        Check(rejected, "future stat pricing must preserve the native pending-transaction fork guard");
    }

    private static void NativePlot()
    {
        foreach (var alive in new[] { true, false })
        {
            var (p, c, _) = Board("STAT-PLOT" + alive);
            if (!alive) p[1].Creature.SetCurrentHpInternal(0);
            var card = Hand<Plot>(p[0], c); var sim = KernelSession.Capture(c).Fork();
            Check(sim.Play(card, null, out var why), "Plot must play its production draw-next-turn path: " + why);
            var play = new CardPlay { Card = card, Player = p[0], Target = null, ResultPile = PileType.Discard,
                Resources = default, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            ((Task)AccessTools.Method(typeof(Plot), "OnPlay").Invoke(card,
                [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
            Console.WriteLine("PLOT alive=" + alive + " sim=" + string.Join('|', p.Select(owner => State(sim, owner.Creature)))
                + " native=" + string.Join('|', p.Select(owner => State(owner.Creature))));
            Check(p.All(owner => State(sim, owner.Creature) == State(owner.Creature))
                && sim.Power<DrawCardsNextTurnPower>(p[0].Creature) == 2
                && sim.Power<DrawCardsNextTurnPower>(p[1].Creature) == (alive ? 2 : 0),
                "Plot must schedule draw for native living teammates including owner, excluding dead seats");
        }
    }

    private static void Settle(KernelSession s) => PowerLifecycleSupport.ResolvePowerAmountChanges(s.Simulator, Combat(s));

    private static void ArtifactExpiry()
    {
        var (p, c, _) = Board("STAT-ARTIFACT");
        Pow<ArtifactPower>(p[0].Creature, 1);
        for (var i = 0; i < 5; i++) Hand<StrikeIronclad>(p[0], c);
        var root = KernelSession.Capture(c); var temp = root.Fork();
        Combat(temp).ApplyTemporaryStrengthGain<SetupStrikePower>(p[0].Creature, 3, p[0].Creature);
        Combat(temp).RestoreTemporaryStrength([p[0].Creature]);
        Check(temp.Power<StrengthPower>(p[0].Creature) == 3 && temp.Power<ArtifactPower>(p[0].Creature) == 0,
            "Artifact may legally preserve a temporary gain by blocking its later negative stat application");
        var native = ModelDb.Power<SetupStrikePower>().ToMutable();
        PowerCmd.Apply(new ThrowingPlayerChoiceContext(), native, p[0].Creature, 3, p[0].Creature, null).GetAwaiter().GetResult();
        native.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), CombatSide.Player, [p[0].Creature]).GetAwaiter().GetResult();
        Check(State(temp, p[0].Creature) == State(p[0].Creature), "Artifact expiry must match native restored power fields");
    }

    private static SimulatedCombatState Combat(KernelSession s) => (SimulatedCombatState)s.Simulator.State.CombatState;
    private static string State(KernelSession s, Creature owner) => FormatPowers(Combat(s).EffectivePowers().Where(x => x.Owner == owner));
    private static string State(Creature owner) => FormatPowers(owner.Powers);
    private static string FormatPowers(IEnumerable<PowerModel> powers) => string.Join(';', powers.Where(p => p.Amount != 0)
        .OrderBy(p => p.Id.Entry).Select(p => $"{p.Id.Entry}:{p.Amount}:{p.TypeForCurrentAmount}:{p.SkipNextDurationTick}:{p.AmountOnTurnStart}:{p.Applier?.CombatId}"));
    private static (Player[], CombatState, Creature) Board(string seed)
    {
        var p = Enumerable.Range(0, 2).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, 700UL + (ulong)i)).ToArray();
        var c = new CombatState(runState: RunState.CreateForTest(p, seed: seed));
        foreach (var seat in p) { seat.ResetCombatState(); c.AddPlayer(seat); seat.Creature.SetMaxHpInternal(80); seat.Creature.SetCurrentHpInternal(80);
            seat.PlayerCombatState!.Phase = PlayerTurnPhase.Play; seat.PlayerCombatState.GainEnergy(3); }
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(600); e.SetCurrentHpInternal(600);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (p, c, e);
    }
    private static T Hand<T>(Player p, CombatState c) where T : CardModel
    { var card = c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static void Pow<T>(Creature owner, int amount) where T : PowerModel
    { ModelDb.Power<T>().ToMutable().ApplyInternal(owner, amount, true); }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("Temporary stats combat: " + message); }
}
