using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
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
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.ValueProps;

// Latest A10 1JJ0XFWWNLTC mechanisms, not a historical full-state replay.
// R2: 17 native C# guards and 2 importer guards: RED1 -> exact restore GREEN0.
// Raw commands/source/native SHA: work/latest-run-repair-20261008/model/*r2-results.json.
// R2 guarded-target: System.InvalidOperationException: Latest run model: native Tank card must create Guarded with null Target and the Tank owner as Applier
// R2 guarded-applier: System.InvalidOperationException: Latest run model: native Tank card must create Guarded with null Target and the Tank owner as Applier
// R2 guarded-applier-metadata: System.InvalidOperationException: Latest run model: native Tank card must create Guarded with null Target and the Tank owner as Applier
// R2 enemy-first-scale: System.InvalidOperationException: Latest run model: first multiplayer enemy application must match native complete fields for ArtifactPower/2
// R2 enemy-restack-scale: System.InvalidOperationException: Latest run model: existing multiplayer power stacking must use native unscaled offset for ArtifactPower/2
// R2 plating-party-decrement: System.InvalidOperationException: Latest run model: first multiplayer enemy application must match native complete fields for PlatingPower/2
// R2 enemy-native-scale-override: System.InvalidOperationException: Latest run model: first multiplayer enemy application must match native complete fields for ArtifactPower/2
// R2 enemy-captured-act: System.InvalidOperationException: Latest run model: first enemy power scaling must use the captured act after live RunState advances
// R2 party-hit-death-order: System.InvalidOperationException: Latest run model: same native hit must preserve Guarded until every target is damaged when its Tank dies
// R2 party-hit-retaliation-order: System.InvalidOperationException: Latest run model: same native hit must reach later targets before earlier target thorns kill the attacker
// R2 player-scale-control: System.InvalidOperationException: Latest run model: player powers and unflagged Buffer must remain native unscaled controls
// R2 unflagged-scale-control: System.InvalidOperationException: Latest run model: player powers and unflagged Buffer must remain native unscaled controls
// R2 explicit-target-control: System.InvalidOperationException: Latest run model: repairing ordinary null Target must preserve explicit targeted-power identity
// R2 flutter-blocked-control: System.InvalidOperationException: Latest run model: native fully blocked powered hit must preserve scaled Flutter layers
// R2 flutter-zero-control: System.InvalidOperationException: Latest run model: native zero powered hit must preserve scaled Flutter layers
// R2 flutter-unpowered-control: System.InvalidOperationException: Latest run model: native nonzero unpowered HP damage must preserve scaled Flutter layers
// R2 flutter-decrement-control: System.InvalidOperationException: Latest run model: Flutter must retain native single powered-hit decrement after scaled application
// R2 import-whole-source-guard: AssertionError: Latest model import: changed predecessor must be rejected before import
// R2 import-unique-seam-guard: AssertionError: Latest model import: missing exact seam must be rejected after a reviewed predecessor hash
internal static class LatestRunModelRepairScenarios
{
    internal static void Run(string? caseName = null)
    {
        var results = new List<string>(); Run(results, caseName);
        foreach (var result in results) Console.WriteLine(result);
    }

    internal static void Run(List<string> results, string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.latest-run-model.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LatestRunModelRepairScenarios), nameof(Format)));
        // Player name is display metadata. Use the same stable identity as the
        // simulator; native owner, applier, target and all combat hooks stay real.
        presentation.Patch(AccessTools.Method(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerName)),
            prefix: new HarmonyMethod(typeof(LatestRunModelRepairScenarios), nameof(PlayerName)));
        // Replay transport's version is presentation/handshake metadata. Its
        // normal factory loads Godot OS paths, unavailable in a headless runner.
        presentation.Patch(AccessTools.Method(typeof(PeerVersionInfo), nameof(PeerVersionInfo.LocalDefault)),
            prefix: new HarmonyMethod(typeof(LatestRunModelRepairScenarios), nameof(Version)));
        var net = AccessTools.Property(typeof(RunManager), nameof(RunManager.NetService));
        var priorNet = net.GetValue(RunManager.Instance);
        net.SetValue(RunManager.Instance, new NetReplayGameService(1));
        try
        {
            foreach (var test in new (string Name, Action Run)[] {
                ("guarded", NativeGuardedFields), ("scaling", NativeScalingFamily),
                ("scaling-controls", NativeScalingControls), ("flutter", NativeFlutterMove),
                ("tank-batch", NativeTankHitBatch), ("batch-reflection", NativeBatchReflection) })
                if (caseName is null || caseName == test.Name)
                { test.Run(); results.Add("PASS: latest-run model " + test.Name); }
        }
        finally { net.SetValue(RunManager.Instance, priorNet); presentation.UnpatchAll(presentation.Id); }
    }

    private static void NativeGuardedFields()
    {
        var (party, combat, _) = Board(4, "LATEST-GUARDED");
        using var nativeTurn = new NativeTurnState(combat);
        var tank = combat.CreateCard<Tank>(party[0]); party[0].PlayerCombatState!.Hand.AddInternal(tank);
        var root = KernelSession.Capture(combat); var frozen = root.PartyStateText();
        var after = root.CardBranches(tank, null, 1).Single(x => x.Boundary.Length == 0).State;
        Check(root.PartyStateText() == frozen && party.All(p => p.Creature.Powers.Count == 0),
            "Tank projection must leave its root and native powers unchanged");
        NativePlayEffect(tank, null);
        Console.WriteLine("GUARDED sim=" + PartyPowers(after, party) + " native=" + PartyPowers(party));
        Check(PartyPowers(after, party) == PartyPowers(party)
            && State(after).EffectivePowers().OfType<GuardedPower>().All(q => q.Target is null && q.Applier == party[0].Creature),
            "native Tank card must create Guarded with null Target and the Tank owner as Applier");

        // A second Tank gives the same ally two independently owned instances.
        var second = combat.CreateCard<Tank>(party[1]); party[1].PlayerCombatState!.Hand.AddInternal(second);
        var two = KernelSession.Capture(combat).CardBranches(second, null, 1).Single(x => x.Boundary.Length == 0).State;
        NativePlayEffect(second, null);
        Check(PartyPowers(two, party) == PartyPowers(party),
            "multiple native Tanks must preserve independent Guarded appliers and null targets");
        two.Simulator.Kill(party[0].Creature, force: true);
        combat.CurrentSide = CombatSide.Enemy;
        CreatureCmd.Kill(party[0].Creature, force: true).GetAwaiter().GetResult();
        Console.WriteLine("GUARDED death sim=" + PartyPowers(two, party) + " native=" + PartyPowers(party));
        Check(PartyPowers(two, party) == PartyPowers(party)
            && State(two).EffectivePowers().OfType<GuardedPower>().Where(q => q.Amount > 0).All(q => q.Applier == party[1].Creature),
            "native Tank death must remove only that applier's Guarded instances");

        // Distinct Target is still required for actual targeted powers.
        var targetRoot = KernelSession.Capture(combat).Fork();
        State(targetRoot).ApplyTargeted<SandpitPower>(party[1].Creature, party[2].Creature, 2, party[1].Creature);
        Check(State(targetRoot).EffectivePowers().OfType<SandpitPower>().Single().Target == party[2].Creature,
            "repairing ordinary null Target must preserve explicit targeted-power identity");
    }

    private static void NativeScalingFamily()
    {
        var family = typeof(FlutterPower).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(PowerModel))
            && t.Namespace == typeof(FlutterPower).Namespace)
            .Select(t => (Type: t, Canonical: CanonicalPower(t)))
            .Where(x => x.Canonical.ShouldScaleInMultiplayer).OrderBy(x => x.Type.Name).ToArray();
        Check(family.Length == 12, "multiplayer scaling scan must cover all twelve native concrete flagged powers");
        foreach (var count in new[] { 1, 2, 4 })
        foreach (var entry in family)
        {
            var (party, combat, enemy) = Board(count, "LATEST-SCALE-" + count + entry.Type.Name, act: 1);
            using var nativeTurn = new NativeTurnState(combat);
            var root = KernelSession.Capture(combat); var sim = root.Fork();
            State(sim).ApplyPower(entry.Type, enemy, 5, enemy); Settle(sim);
            NativeApply(entry.Type, enemy, 5, enemy);
            Console.WriteLine($"SCALE first n={count} {entry.Type.Name} sim={Powers(sim, enemy)} native={Powers(enemy)}");
            Check(Powers(sim, enemy) == Powers(enemy),
                "first multiplayer enemy application must match native complete fields for " + entry.Type.Name + "/" + count);
            Check(root.Hp(enemy) == 2000 && State(root).EffectivePowers().All(p => p.Owner != enemy),
                "multiplayer power application must leave its frozen parent unchanged");
            State(sim).ApplyPower(entry.Type, enemy, 5, enemy); Settle(sim);
            NativeApply(entry.Type, enemy, 5, enemy);
            Check(Powers(sim, enemy) == Powers(enemy),
                "existing multiplayer power stacking must use native unscaled offset for " + entry.Type.Name + "/" + count);
            var simulated = State(sim).EffectivePowers().Single(p => p.Owner == enemy && p.GetType() == entry.Type && p.Amount != 0);
            State(sim).SetPowerAmount(simulated, 0); Settle(sim);
            PowerCmd.Remove(enemy.Powers.Single(p => p.GetType() == entry.Type)).GetAwaiter().GetResult();
            State(sim).ApplyPower(entry.Type, enemy, 5, enemy); Settle(sim);
            NativeApply(entry.Type, enemy, 5, enemy);
            Check(Powers(sim, enemy) == Powers(enemy),
                "removed multiplayer power must acquire native first-application scaling again for " + entry.Type.Name + "/" + count);
        }
    }

    private static void NativeScalingControls()
    {
        var (party, combat, enemy) = Board(4, "LATEST-SCALE-CONTROLS", act: 1);
        using var nativeTurn = new NativeTurnState(combat);
        var sim = KernelSession.Capture(combat).Fork();
        foreach (var type in new[] { typeof(FlutterPower), typeof(ArtifactPower), typeof(PlatingPower), typeof(BufferPower), typeof(StrengthPower) })
        {
            State(sim).ApplyPower(type, party[0].Creature, 5, party[0].Creature); Settle(sim);
            NativeApply(type, party[0].Creature, 5, party[0].Creature);
        }
        State(sim).Apply<BufferPower>(enemy, 5, enemy); Settle(sim); NativeApply(typeof(BufferPower), enemy, 5, enemy);
        Check(Powers(sim, party[0].Creature) == Powers(party[0].Creature) && Powers(sim, enemy) == Powers(enemy),
            "player powers and unflagged Buffer must remain native unscaled controls");
        // Freeze the act before changing the live RunState. The default scaling
        // helper must not read a later live act from the shared owner object.
        ((RunState)combat.RunState).CurrentActIndex = 0;
        State(sim).Apply<FlutterPower>(enemy, 5, enemy); Settle(sim);
        Check(sim.Power<FlutterPower>(enemy) == 24,
            "first enemy power scaling must use the captured act after live RunState advances");
    }

    private static void NativeFlutterMove()
    {
        var (party, combat, _) = Board(4, "LATEST-FLUTTER-MOVE", act: 1, monster: typeof(ThievingHopper), moveId: "FLUTTER_MOVE");
        using var nativeTurn = new NativeTurnState(combat);
        var enemy = combat.Enemies.Single(); var root = KernelSession.Capture(combat); var after = root.Fork();
        foreach (var p in party) Check(after.EndTurn(p, 1, out var why), "native Flutter production boundary must finish: " + why);
        combat.CurrentSide = CombatSide.Enemy;
        enemy.Monster!.NextMove.PerformMove(party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        Console.WriteLine("FLUTTER sim=" + Powers(after, enemy) + " native=" + Powers(enemy));
        Check(after.Power<FlutterPower>(enemy) == 24 && Powers(after, enemy) == Powers(enemy),
            "production Hopper Flutter move must apply native four-player twenty-four layers");
        Console.WriteLine($"FLUTTER before hits simBlock={after.Block(enemy)} nativeBlock={enemy.Block}");
        after.Simulator.GainBlock(enemy, 10, ValueProp.Unpowered);
        enemy.GainBlockInternal(10);
        Hit(2, ValueProp.Move);
        Check(after.Power<FlutterPower>(enemy) == 24 && Powers(after, enemy) == Powers(enemy),
            "native fully blocked powered hit must preserve scaled Flutter layers");
        Hit(0, ValueProp.Move);
        Check(after.Power<FlutterPower>(enemy) == 24 && Powers(after, enemy) == Powers(enemy),
            "native zero powered hit must preserve scaled Flutter layers");
        var hpBeforeUnpowered = after.Hp(enemy);
        Hit(30, ValueProp.Unpowered);
        Check(after.Hp(enemy) < hpBeforeUnpowered && after.Power<FlutterPower>(enemy) == 24
            && Powers(after, enemy) == Powers(enemy),
            "native nonzero unpowered HP damage must preserve scaled Flutter layers");
        Hit(2, ValueProp.Move);
        Console.WriteLine("FLUTTER damaged sim=" + Powers(after, enemy) + " native=" + Powers(enemy));
        Check(after.Power<FlutterPower>(enemy) == 23 && Powers(after, enemy) == Powers(enemy),
            "Flutter must retain native single powered-hit decrement after scaled application");

        void Hit(int amount, ValueProp props)
        {
            var simResults = after.Simulator.Damage(enemy, amount, props, party[0].Creature); Settle(after);
            var nativeResults = CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), enemy, amount, props,
                party[0].Creature).GetAwaiter().GetResult();
            Console.WriteLine($"FLUTTER hit {amount}/{props} simHP={after.Hp(enemy)} nativeHP={enemy.CurrentHp} simBlock={after.Block(enemy)} nativeBlock={enemy.Block} simLoss={simResults.Sum(r => r.UnblockedDamage)} nativeLoss={nativeResults.Sum(r => r.UnblockedDamage)}");
            Check(after.Hp(enemy) == enemy.CurrentHp && after.Block(enemy) == enemy.Block,
                "native Flutter damage controls must match HP and block on every hit");
        }
    }

    private static void NativeTankHitBatch()
    {
        // R3 mechanism: Tank HP28/block14, protected Bot1 HP51/block19.
        // Each of three native 11-damage hits batches the whole party before
        // death; the Tank dies on hit three, after all allies received 5 damage.
        foreach (var tankFirst in new[] { true, false })
        {
            var (party, combat, enemy) = Board(4, "LATEST-TANK-BATCH" + tankFirst,
                monster: typeof(TestSubject), moveId: "MULTI_CLAW_MOVE", ascension: 10);
            using var nativeTurn = new NativeTurnState(combat);
            var tankSeat = tankFirst ? 0 : 3; var protectedSeat = tankFirst ? 1 : 0;
            var tank = party[tankSeat]; tank.Creature.SetCurrentHpInternal(28); tank.Creature.GainBlockInternal(14);
            party[protectedSeat].Creature.SetCurrentHpInternal(51); party[protectedSeat].Creature.GainBlockInternal(19);
            PowerCmd.Apply<TankPower>(new ThrowingPlayerChoiceContext(), tank.Creature, 1, tank.Creature, null).GetAwaiter().GetResult();
            // Other seats exercise an HP-loss modifier and independent guards.
            party[2].AddRelicInternal(ModelDb.Relic<TungstenRod>().ToMutable());
            var root = KernelSession.Capture(combat); var frozen = root.PartyStateText(); var sim = root.Fork();
            Check(root.CurrentAttacks().Sum(a => a.Raw * a.Repeats) == 33 && root.Threat(tank).AttackDamage == 48,
                "native A10 TestSubject three eleven-damage hits with Tank must forecast forty-eight raw damage");
            foreach (var p in party) Check(sim.EndTurn(p, 1, out var why), "Tank production enemy boundary must finish: " + why);
            Check(root.PartyStateText() == frozen && tank.Creature.CurrentHp == 28,
                "group-hit simulation must preserve its root and the live Tank before native execution");
            combat.CurrentSide = CombatSide.Enemy;
            enemy.Monster!.NextMove.PerformMove(party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
            Console.WriteLine("TANK BATCH " + tankFirst + " sim=" + PartyHitState(sim, party) + " native=" + PartyHitState(party));
            Check(sim.Hp(tank.Creature) == 0 && sim.Hp(party[protectedSeat].Creature) == 51,
                "same native hit must preserve Guarded until every target is damaged when its Tank dies");
            Check(PartyHitState(sim, party) == PartyHitState(party),
                "native TestSubject multihit must match every seat HP/block/powers regardless of target order");
        }
    }

    private static void NativeBatchReflection()
    {
        // Native computes the whole hit before Thorn retaliation. Even when
        // seat0's thorns kill the attacker, seat1 must receive this same hit.
        var (party, combat, enemy) = Board(2, "LATEST-BATCH-THORNS", moveId: "ATTACK");
        using var nativeTurn = new NativeTurnState(combat);
        enemy.SetCurrentHpInternal(2); Pow<ThornsPower>(party[0].Creature, 3);
        var sim = KernelSession.Capture(combat).Fork();
        foreach (var p in party) Check(sim.EndTurn(p, 1, out var why), "native reflected group hit must finish: " + why + " " + sim.LastRoundFailure);
        combat.CurrentSide = CombatSide.Enemy;
        enemy.Monster!.NextMove.PerformMove(party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        Check(sim.Hp(party[0].Creature) == 79 && sim.Hp(party[1].Creature) == 79
            && party.All(p => p.Creature.CurrentHp == 79),
            "same native hit must reach later targets before earlier target thorns kill the attacker");
    }

    private static (Player[] Party, CombatState Combat, Creature Enemy) Board(int count, string seed,
        int act = 0, Type? monster = null, string? moveId = null, int ascension = 0)
    {
        var party = Enumerable.Range(0, count).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, 810UL + (ulong)i)).ToArray();
        var run = RunState.CreateForTest(party, seed: seed, ascensionLevel: ascension); run.CurrentActIndex = act;
        var combat = new CombatState(runState: run);
        foreach (var p in party)
        { p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
          p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3); }
        var canonical = (MonsterModel)typeof(ModelDb).GetMethod(nameof(ModelDb.Monster))!.MakeGenericMethod(monster ?? typeof(MockAttackMonster)).Invoke(null, null)!;
        var enemy = combat.CreateCreature(canonical.ToMutable(), CombatSide.Enemy, "0"); combat.AddCreature(enemy);
        using var nativeRun = new NativeRunState(run);
        enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(2000); enemy.SetCurrentHpInternal(2000);
        if (moveId is not null) enemy.Monster.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)
            enemy.Monster.MoveStateMachine!.States[moveId], true);
        return (party, combat, enemy);
    }

    private static PowerModel CanonicalPower(Type type) => (PowerModel)typeof(ModelDb).GetMethod(nameof(ModelDb.Power))!
        .MakeGenericMethod(type).Invoke(null, null)!;
    private static void NativeApply(Type type, Creature target, int amount, Creature applier) =>
        PowerCmd.Apply(new ThrowingPlayerChoiceContext(), CanonicalPower(type).ToMutable(), target, amount, applier, null).GetAwaiter().GetResult();
    private static void NativePlayEffect(CardModel card, Creature? target)
    {
        var play = new CardPlay { Card = card, Player = card.Owner, Target = target, ResultPile = PileType.Discard,
            Resources = default, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        ((Task)AccessTools.Method(card.GetType(), "OnPlay").Invoke(card, [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
    }
    private static void Pow<T>(Creature owner, int amount) where T : PowerModel => ModelDb.Power<T>().ToMutable().ApplyInternal(owner, amount, true);
    private static void Settle(KernelSession s)
    { s.Simulator.SynchronizePowerAmountPredictionStates(); PowerLifecycleSupport.ResolvePowerAmountChanges(s.Simulator, State(s)); }
    private static SimulatedCombatState State(KernelSession s) => (SimulatedCombatState)s.Simulator.State.CombatState;
    private static string Powers(KernelSession s, Creature owner) => PowerFields(State(s).EffectivePowers().Where(p => p.Owner == owner));
    private static string Powers(Creature owner) => PowerFields(owner.Powers);
    private static string PowerFields(IEnumerable<PowerModel> powers) => string.Join(';', powers.Where(p => p.Amount != 0)
        .OrderBy(p => p.Id.Entry).ThenBy(p => p.Applier?.CombatId).Select(p =>
        $"{p.Id.Entry}:{p.Amount}:{p.TypeForCurrentAmount}:{p.SkipNextDurationTick}:{p.AmountOnTurnStart}:owner{p.Owner.CombatId}:applier{p.Applier?.CombatId}:target{p.Target?.CombatId}:" +
        string.Join(',', p.DynamicVars.OrderBy(v => v.Key).Select(v => v.Key + "=" + v.Value.BaseValue
            + (v.Value is MegaCrit.Sts2.Core.Localization.DynamicVars.StringVar text ? ":" + text.StringValue : "")))));
    private static string PartyPowers(KernelSession s, Player[] p) => string.Join('|', p.Select(x => Powers(s, x.Creature)));
    private static string PartyPowers(Player[] p) => string.Join('|', p.Select(x => Powers(x.Creature)));
    private static string PartyHitState(KernelSession s, Player[] p) => string.Join('|', p.Select(x => $"{s.Hp(x.Creature)}/{s.Block(x.Creature)}/{Powers(s, x.Creature)}"));
    private static string PartyHitState(Player[] p) => string.Join('|', p.Select(x => $"{x.Creature.CurrentHp}/{x.Creature.Block}/{Powers(x.Creature)}"));
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool PlayerName(ulong playerId, ref string __result) { __result = playerId.ToString(); return false; }
    private static bool Version(ref PeerVersionInfo __result) { __result = default; return false; }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("Latest run model: " + message); }

    private sealed class NativeTurnState : IDisposable
    {
        private readonly FieldInfo field = AccessTools.Field(typeof(CombatManager), "_turnState");
        private readonly object? previous;
        private readonly NativeRunState run;
        private readonly ulong? localId = LocalContext.NetId;
        private readonly CombatState combat;
        private readonly CombatSide side;
        internal NativeTurnState(CombatState combat)
        {
            this.combat = combat; side = combat.CurrentSide; run = new NativeRunState((RunState)combat.RunState);
            LocalContext.NetId = combat.Players[0].NetId;
            previous = field.GetValue(CombatManager.Instance);
            var turn = Activator.CreateInstance(field.FieldType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
            AccessTools.Property(field.FieldType, "IsInProgress").SetValue(turn, true);
            AccessTools.Property(field.FieldType, "IsStarting").SetValue(turn, false);
            field.SetValue(CombatManager.Instance, turn);
        }
        public void Dispose()
        { field.SetValue(CombatManager.Instance, previous); LocalContext.NetId = localId; combat.CurrentSide = side; run.Dispose(); }
    }

    private sealed class NativeRunState : IDisposable
    {
        private readonly PropertyInfo state = AccessTools.Property(typeof(RunManager), "State");
        private readonly PropertyInfo ascension = AccessTools.Property(typeof(RunManager), nameof(RunManager.AscensionManager));
        private readonly object? priorState;
        private readonly object? priorAscension;
        internal NativeRunState(RunState run)
        {
            priorState = state.GetValue(RunManager.Instance); priorAscension = ascension.GetValue(RunManager.Instance);
            state.SetValue(RunManager.Instance, run);
            ascension.SetValue(RunManager.Instance, new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(run.AscensionLevel));
        }
        public void Dispose()
        { state.SetValue(RunManager.Instance, priorState); ascension.SetValue(RunManager.Instance, priorAscension); }
    }
}
