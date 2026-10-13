using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
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

// SZG1RJZ6XVL6 mechanism fixtures, not a complete historical run replay.
// Native command hooks remain real; only UI/version/player-name presentation is stubbed.
// R2: 24 isolated production faults + one fixture-isolation fault each built0/RED1.
// Exact source restoration rebuilt0/GREEN0; commands/hash/raw: work/szg-repair-20261008T124624/model/r2/proof.json.
// liquify-batched: System.InvalidOperationException: SZG model: native per-seat generated-card ordering must match every complete pile: TheInsatiable/2
// liquify-extra-rng: System.InvalidOperationException: SZG model: generated-card random insertion must preserve the complete shared Shuffle RNG state
// noisebot-regroup: System.InvalidOperationException: SZG model: native generated hooks must retain the exact per-seat create/insert dispatch order: Noisebot/2
// soulfysh-regroup: System.InvalidOperationException: SZG model: native generated hooks must retain the exact per-seat create/insert dispatch order: SoulFysh/2
// rampart-one-instance: System.InvalidOperationException: SZG model: native global Rampart must follow every completed seat Hourglass and grant once per power instance
// bots-all-deferred: System.InvalidOperationException: SZG model: production Bot selector tasks must complete Mittens/DarkEmbrace/Hellraiser/Sly/Hourglass before shared Rampart; only human tasks may remain paused
// humans-all-instant: System.InvalidOperationException: SZG model: production Bot selector tasks must complete Mittens/DarkEmbrace/Hellraiser/Sly/Hourglass before shared Rampart; only human tasks may remain paused
// side-too-early: System.InvalidOperationException: SZG model: native global Rampart must follow every completed seat Hourglass and grant once per power instance
// fork-misses-sync: System.Exception: SZG choice repair: captured and forked choice timing must preserve the original control snapshot independently of live handover
// fork-shares-sync: System.Exception: SZG choice repair: captured and forked choice timing must preserve the original control snapshot independently of live handover
// setup-auto-first: System.InvalidOperationException: SZG model: paused native setup relics must run after global Rampart while every first/nested menu resumes in seat order
// extra-grants-rampart: System.InvalidOperationException: SZG model: native extra-turn participant setup must fire its relic and suppress all Rampart grants
// reseal-party-history:  ---> System.InvalidOperationException: Cannot fork prediction history with unresolved deferred entries.
// auto-without-card-scope:  ---> System.InvalidOperationException: Resuming a card selector without its execution scope.
// nested-extra-selection-rng: System.InvalidOperationException: SZG model: nested draw/card continuation must preserve both shared native RNG streams and frozen parent
// party-extra-shuffle-rng: System.InvalidOperationException: SZG model: nested native setup menus must retain shared draw/Shuffle RNG
// party-extra-energy: System.InvalidOperationException: SZG model: paused party setup must match native complete piles and per-seat energy after nested selection
// side-skips-draw: System.InvalidOperationException: SZG model: global side staging must preserve every participant hand draw
// extra-clears-excluded-block: System.InvalidOperationException: SZG model: extra-turn start must preserve excluded seats hands and block
// comparison-cap-denies-progress: System.InvalidOperationException: SZG model: production native party start must finish:
// adaptable-unmirrored: System.InvalidOperationException: SZG model: verified compensated Adaptable AfterDeath must not emit an unmirrored diagnostic
// adaptable-no-revive: System.InvalidOperationException: SZG model: native Adaptable death must enter the compensated unhittable revive move without winning
// adaptable-wrong-hp: System.InvalidOperationException: SZG model: native Adaptable first and second revival must match scaled HP and final power removal
// adaptable-final-power: System.InvalidOperationException: SZG model: native Adaptable first and second revival must match scaled HP and final power removal
// fixture-unpatches-production: System.InvalidOperationException: SZG model: scoped native Bot selector fixture must restore every inherited production selector prefix
internal static class SzgModelRepairScenarios
{
    private static KernelSession? observed;
    private static readonly List<(ulong Player, IReadOnlyList<CardModel> Cards)> actualChoices = new();
    private static CombatState? generatedCombat;
    private static readonly List<string> nativeGenerated = new();
    internal static void Run(string? caseName = null)
    {
        var results = new List<string>(); Run(results, caseName);
        foreach (var result in results) Console.WriteLine(result);
    }
    internal static void Run(List<string> results, string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.szg-model.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(SzgModelRepairScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "Exists"),
            prefix: new HarmonyMethod(typeof(SzgModelRepairScenarios), nameof(Exists)));
        presentation.Patch(AccessTools.Method(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerName)),
            prefix: new HarmonyMethod(typeof(SzgModelRepairScenarios), nameof(PlayerName)));
        presentation.Patch(AccessTools.Method(typeof(PeerVersionInfo), nameof(PeerVersionInfo.LocalDefault)),
            prefix: new HarmonyMethod(typeof(SzgModelRepairScenarios), nameof(Version)));
        presentation.Patch(AccessTools.Method(typeof(KernelSession), "AutomaticTurnStartChoice"),
            postfix: new HarmonyMethod(typeof(SzgModelRepairScenarios), nameof(ObserveChoice)));
        presentation.Patch(AccessTools.Method(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat)),
            prefix: new HarmonyMethod(typeof(SzgModelRepairScenarios), nameof(ObserveGenerated)));
        var wrapper = AccessTools.Method(typeof(CardModel), "OnPlayWrapper")
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        presentation.Patch(AccessTools.Method(wrapper, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(SzgModelRepairScenarios), nameof(PresentationClock)));
        var net = AccessTools.Property(typeof(RunManager), nameof(RunManager.NetService));
        var priorNet = net.GetValue(RunManager.Instance);
        net.SetValue(RunManager.Instance, new NetReplayGameService(1));
        try
        {
            foreach (var test in new (string Name, Action Run)[] {
                ("generated-order", GeneratedOrder), ("side-timing", SideTiming),
                ("party-pauses", PartyPauses), ("extra-turn", ExtraTurn),
                ("nested-draw", NestedDraw),
                ("bot-instant", BotInstant),
                ("adaptable", Adaptable) })
                if (caseName is null || caseName == test.Name)
                { test.Run(); results.Add("PASS: SZG model " + test.Name); }
        }
        finally { observed = null; generatedCombat = null; net.SetValue(RunManager.Instance, priorNet); presentation.UnpatchAll(presentation.Id); }
    }

    private static void GeneratedOrder()
    {
        foreach (var count in new[] { 1, 2, 4 })
        foreach (var entry in new[] { (Type: typeof(TheInsatiable), Move: "LIQUIFY_GROUND_MOVE"),
            (Type: typeof(Noisebot), Move: "NOISE_MOVE"), (Type: typeof(SoulFysh), Move: "BECKON_MOVE") })
        {
            var (party, combat, enemy) = Board(count, "SZG-GENERATION-" + count + entry.Type.Name,
                monster: entry.Type, moveId: entry.Move);
            using var native = new NativeTurnState(combat);
            foreach (var p in party)
            {
                var cards = new CardModel[] { combat.CreateCard<StrikeIronclad>(p), combat.CreateCard<Bash>(p),
                    combat.CreateCard<DefendSilent>(p), combat.CreateCard<Dominate>(p), combat.CreateCard<PommelStrike>(p) };
                foreach (var card in cards.Take(3)) p.PlayerCombatState!.DrawPile.AddInternal(card);
                foreach (var card in cards.Skip(3)) p.PlayerCombatState!.DiscardPile.AddInternal(card);
            }
            var root = KernelSession.Capture(combat); var frozen = root.PartyStateText(); var sim = root.Fork();
            foreach (var p in party) Check(sim.EndTurn(p, 1, out var reason), "generated native enemy phase must settle: " + reason);
            combat.CurrentSide = CombatSide.Enemy;
            generatedCombat = combat; nativeGenerated.Clear();
            try { enemy.Monster!.NextMove.PerformMove(party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult(); }
            finally { generatedCombat = null; }
            Console.WriteLine($"GENERATED {entry.Type.Name} n={count} sim={Piles(sim, party)} native={Piles(party)}");
            Check(Piles(sim, party) == Piles(party), "native per-seat generated-card ordering must match every complete pile: " + entry.Type.Name + "/" + count);
            var created = sim.Simulator.History.OfType<CoopBots.Kernel.Vendor.Engine.InCombat.Simulation.CombatPredictionCardGeneratedEntry>()
                .Select(e => e.Card.Owner.NetId + ":" + e.Card.Id + ":" + sim.Simulator.State.FindCard(e.Card.Original)!.OwnerPile!.Type).ToArray();
            Check(created.SequenceEqual(nativeGenerated),
                "native generated hooks must retain the exact per-seat create/insert dispatch order: " + entry.Type.Name + "/" + count);
            Check(sim.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState(),
                "generated-card random insertion must preserve the complete shared Shuffle RNG state");
            Check(root.PartyStateText() == frozen, "generated-card simulation must leave its frozen parent unchanged");
        }
    }

    private static void SideTiming()
    {
        foreach (var count in new[] { 1, 2, 4 })
        foreach (var owner in Enumerable.Range(0, count))
        foreach (var copies in new[] { 1, 2 })
        {
            var (party, combat, enemy) = Board(count, $"SZG-SIDE-{count}-{owner}-{copies}", monster: typeof(TurretOperator));
            using var native = new NativeTurnState(combat);
            foreach (var p in party) DrawFive(p, combat);
            party[owner].AddRelicInternal(ModelDb.Relic<MercuryHourglass>().ToMutable());
            Pow<RampartPower>(enemy, 120);
            if (copies == 2)
            {
                var second = combat.CreateCreature(ModelDb.Monster<TurretOperator>().ToMutable(), CombatSide.Enemy, "1");
                combat.AddCreature(second); second.Monster!.SetUpForCombat();
                second.SetMaxHpInternal(2000); second.SetCurrentHpInternal(2000); Pow<RampartPower>(second, 120);
            }
            var root = KernelSession.Capture(combat); var frozen = root.PartyStateText(); var sim = root.ForkForReplay();
            StartSim(sim, party, false);
            StartNative(native, party, combat, false, null);
            Console.WriteLine($"SIDE n={count} owner={owner} copies={copies} sim={Enemies(sim, combat)} native={Enemies(combat)}");
            Check(Enemies(sim, combat) == Enemies(combat) && combat.Enemies.All(e => e.CurrentHp == 1997 && e.Block == 120 * copies),
                "native global Rampart must follow every completed seat Hourglass and grant once per power instance");
            Check(Piles(sim, party) == Piles(party), "global side staging must preserve every participant hand draw");
            Check(root.PartyStateText() == frozen, "party turn staging must leave its parent unchanged");
        }
    }

    private static void PartyPauses()
    {
        foreach (var beforeHand in new[] { false, true })
        {
            var (party, combat, enemy) = Board(4, "SZG-PAUSES-" + beforeHand, monster: typeof(TurretOperator));
            using var native = new NativeTurnState(combat);
            foreach (var p in party)
            {
                DrawFive(p, combat); p.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<AscendersBane>(p));
                Pow<ToolsOfTheTradePower>(p.Creature, 1);
            }
            if (beforeHand) Pow<ForegoneConclusionPower>(party[0].Creature, 1);
            party[0].AddRelicInternal(ModelDb.Relic<MercuryHourglass>().ToMutable());
            party[2].AddRelicInternal(ModelDb.Relic<MercuryHourglass>().ToMutable());
            Pow<RampartPower>(enemy, 120);
            var sim = KernelSession.Capture(combat).ForkForReplay();
            actualChoices.Clear(); observed = sim;
            try { StartSim(sim, party, false); } finally { observed = null; }
            Check(actualChoices.Count == (beforeHand ? 5 : 4), "all native first and nested setup menus must actually resolve");
            StartNative(native, party, combat, false, actualChoices.ToArray());
            Console.WriteLine($"PAUSES beforeHand={beforeHand} choices={string.Join('|', actualChoices.Select(q => q.Player + ":" + string.Join(',', q.Cards.Select(c => c.Id.Entry))))} sim={Enemies(sim, combat)} native={Enemies(combat)}");
            Check(Enemies(sim, combat) == Enemies(combat) && enemy.CurrentHp == 2000 && enemy.Block == 114,
                "paused native setup relics must run after global Rampart while every first/nested menu resumes in seat order");
            Check(Piles(sim, party) == Piles(party) && party.All(p => sim.Energy(p) == p.PlayerCombatState!.Energy),
                "paused party setup must match native complete piles and per-seat energy after nested selection");
            Check(sim.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState(),
                "nested native setup menus must retain shared draw/Shuffle RNG");
        }
    }

    private static void ExtraTurn()
    {
        var (party, combat, enemy) = Board(4, "SZG-EXTRA", monster: typeof(TurretOperator));
        using var native = new NativeTurnState(combat);
        foreach (var p in party) { DrawFive(p, combat); p.Creature.GainBlockInternal(7); }
        Pow<RampartPower>(enemy, 120);
        party[2].AddRelicInternal(ModelDb.Relic<MercuryHourglass>().ToMutable());
        var sim = KernelSession.Capture(combat).ForkForReplay();
        var round = State(sim).RoundNumber;
        StartSim(sim, [party[2]], true);
        native.SetExtraParticipants([party[2]]);
        StartNative(native, [party[2]], combat, true, null);
        Console.WriteLine("EXTRA sim=" + Enemies(sim, combat) + " native=" + Enemies(combat));
        Check(State(sim).RoundNumber == round && Enemies(sim, combat) == Enemies(combat)
            && enemy.CurrentHp == 1997 && enemy.Block == 0,
            "native extra-turn participant setup must fire its relic and suppress all Rampart grants");
        Check(Piles(sim, party) == Piles(party) && party.Where(p => p != party[2]).All(p => sim.Block(p.Creature) == 7),
            "extra-turn start must preserve excluded seats hands and block");
    }

    private static void NestedDraw()
    {
        var (party, combat, enemy) = Board(2, "SZG-NESTED-DRAW", monster: typeof(TurretOperator));
        using var native = new NativeTurnState(combat);
        foreach (var p in party)
        {
            Pow<HellraiserPower>(p.Creature, 1);
            foreach (var model in new CardModel[] { ModelDb.Card<SeekerStrike>(), ModelDb.Card<DefendIronclad>(),
                ModelDb.Card<DarkEmbrace>(), ModelDb.Card<DefendSilent>(), ModelDb.Card<Footwork>(),
                ModelDb.Card<ShrugItOff>(), ModelDb.Card<AscendersBane>() })
                p.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard(model, p));
        }
        party[1].AddRelicInternal(ModelDb.Relic<MercuryHourglass>().ToMutable());
        Pow<RampartPower>(enemy, 120);
        var root = KernelSession.Capture(combat); var frozen = root.PartyStateText();
        var sim = root.ForkForReplay(); actualChoices.Clear(); observed = sim;
        try { StartSim(sim, party, false); } finally { observed = null; }
        Check(actualChoices.Count == 2, "both drawn native SeekerStrike auto-plays must open their nested menus");
        StartNative(native, party, combat, false, actualChoices.ToArray());
        Console.WriteLine("NESTED-DRAW sim=" + Enemies(sim, combat) + " native=" + Enemies(combat)
            + " choices=" + string.Join('|', actualChoices.Select(q => q.Player + ":" + string.Join(',', q.Cards.Select(c => c.Id.Entry)))));
        Check(Enemies(sim, combat) == Enemies(combat) && Piles(sim, party) == Piles(party)
            && enemy.CurrentHp == 1982 && enemy.Block == 117,
            "simultaneously paused draw/card transactions must resume after side hooks without losing any native frame or card pile");
        Check(sim.Simulator.Rng.CombatCardSelectionState == ((RunState)combat.RunState).Rng.CombatCardSelection.CaptureState()
            && sim.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState()
            && root.PartyStateText() == frozen,
            "nested draw/card continuation must preserve both shared native RNG streams and frozen parent");
    }

    private static void Adaptable()
    {
        foreach (var count in new[] { 1, 2, 4 })
        {
            var (party, combat, enemy) = Board(count, "SZG-ADAPTABLE-" + count, monster: typeof(TestSubject), act: 2);
            using var native = new NativeTurnState(combat);
            Pow<AdaptablePower>(enemy, 1);
            var sim = KernelSession.Capture(combat).Fork();
            for (var form = 0; form < 2; form++)
            {
                sim.Simulator.Kill(enemy); Settle(sim);
                CorePowerSupport.ApplyEnemyDeathPowers(sim.Simulator, State(sim), State(sim).KnownEnemies, new HashSet<uint>());
                combat.CurrentSide = CombatSide.Enemy;
                CreatureCmd.Kill(enemy).GetAwaiter().GetResult();
                Console.WriteLine($"ADAPTABLE dead n={count} form={form} simHP={sim.Hp(enemy)} nativeHP={enemy.CurrentHp} simMove={State(sim).CurrentMonsterMove(enemy).Move.Id} nativeMove={enemy.Monster!.NextMove.Id}");
                Check(sim.Hp(enemy) == enemy.CurrentHp && !sim.HasWon && !sim.Simulator.State.IsHittable(enemy)
                    && !enemy.IsHittable && State(sim).CurrentMonsterMove(enemy).Move.Id == enemy.Monster.NextMove.Id,
                    "native Adaptable death must enter the compensated unhittable revive move without winning");
                Check(PredictionCoverage.Collect(sim.Simulator).All(g => g.SourceId != "ADAPTABLE_POWER" || g.Method != "AfterDeath")
                    && !sim.Simulator.History.OfType<CoopBots.Kernel.Vendor.Engine.InCombat.Simulation.CombatPredictionRiskEntry>()
                        .Any(g => g.Trace?.Source is AdaptablePower && g.Trace.Invocation.Method?.Name == "AfterDeath"),
                    "verified compensated Adaptable AfterDeath must not emit an unmirrored diagnostic");
                State(sim).ResolveReviveMove(sim.Simulator, enemy, "RESPAWN_MOVE");
                enemy.Monster.NextMove.PerformMove(party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
                Console.WriteLine($"ADAPTABLE revived n={count} form={form} simHP={sim.Hp(enemy)} nativeHP={enemy.CurrentHp}");
                Check(sim.Hp(enemy) == enemy.CurrentHp && sim.Simulator.State.IsHittable(enemy) && enemy.IsHittable
                    && sim.Power<AdaptablePower>(enemy) == (enemy.GetPower<AdaptablePower>()?.Amount ?? 0),
                    "native Adaptable first and second revival must match scaled HP and final power removal");
            }
        }
    }

    private static void BotInstant()
    {
        // Exercise the actual deployed Harmony selector Prefix, not a selector
        // stub returning a task with different native completion semantics.
        var patches = new Harmony("coopbots.szg-model.bot-selectors");
        var methods = typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Task<CardModel>) || m.ReturnType == typeof(Task<IEnumerable<CardModel>>)).ToArray();
        string Prefixes(MethodBase method) => string.Join('|', Harmony.GetPatchInfo(method)?.Prefixes
            .Select(p => p.owner + ":" + p.PatchMethod.DeclaringType!.FullName + ":" + p.PatchMethod.Name).Order().ToArray() ?? []);
        var inherited = methods.ToDictionary(m => m, m => Prefixes(m));
        foreach (var method in methods)
        {
            var prefix = AccessTools.Method(AccessTools.TypeByName(method.ReturnType == typeof(Task<CardModel>)
                ? "CoopBots.BotSingleCardSelectPatch" : "CoopBots.BotMultipleCardSelectPatch"), "Prefix");
            if (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.PatchMethod == prefix) != true)
                patches.Patch(method, prefix: new HarmonyMethod(prefix));
        }
        var handedOver = CoopBots.AutoPilot.HandedOver.ToArray();
        try
        {
            foreach (var count in new[] { 1, 2, 4 })
            foreach (var mixed in count == 4 ? new[] { false, true } : new[] { false })
            {
                var (party, combat, enemy) = Board(count, $"SZG-BOT-INSTANT-{count}-{mixed}", monster: typeof(TurretOperator));
                using var native = new NativeTurnState(combat);
                var bots = party.Where((p, index) => !mixed || index != 0).Select(p => p.NetId).ToHashSet();
                CoopBots.AutoPilot.Clear();
                foreach (var p in party)
                {
                    if (bots.Contains(p.NetId)) CoopBots.AutoPilot.Set(p.NetId, true);
                    Pow<ToolsOfTheTradePower>(p.Creature, 1);
                    for (var i = 0; i < 5; i++) p.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<Dazed>(p));
                    p.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<Ricochet>(p));
                    if (bots.Contains(p.NetId))
                    {
                        p.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
                        Pow<DarkEmbracePower>(p.Creature, 1); Pow<HellraiserPower>(p.Creature, 1);
                        p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<SeekerStrike>(p));
                        for (var i = 0; i < 3; i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<Dazed>(p));
                    }
                    p.AddRelicInternal(ModelDb.Relic<MercuryHourglass>().ToMutable());
                }
                Pow<RampartPower>(enemy, 120);
                var sim = KernelSession.Capture(combat).ConfigureSynchronousChoicePlayers(bots).ForkForReplay();
                actualChoices.Clear(); observed = sim;
                try { StartSim(sim, party, false); } finally { observed = null; }
                StartNative(native, party, combat, false, actualChoices.ToArray(), bots);
                Console.WriteLine($"BOT-INSTANT n={count} mixed={mixed} sim={Enemies(sim, combat)} native={Enemies(combat)} "
                    + "choices=" + string.Join('|', actualChoices.Select(q => q.Player + ":" + string.Join(',', q.Cards.Select(c => c.Id.Entry)))));
                Check(Enemies(sim, combat) == Enemies(combat) && enemy.CurrentHp < 1997 && enemy.Block <= 120,
                    "production Bot selector tasks must complete Mittens/DarkEmbrace/Hellraiser/Sly/Hourglass before shared Rampart; only human tasks may remain paused");
                Check(Piles(sim, party) == Piles(party),
                    "production synchronous Bot setup must preserve full native piles through exhaust/draw/nested selection and discard/Sly tails");
                Check(sim.Simulator.Rng.CombatCardSelectionState == ((RunState)combat.RunState).Rng.CombatCardSelection.CaptureState()
                    && sim.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState(),
                    "production Bot immediate tails and human deferred tails must consume the complete native shared RNG states");
            }
        }
        finally
        {
            CoopBots.AutoPilot.Clear(); foreach (var id in handedOver) CoopBots.AutoPilot.Set(id, true);
            // This Harmony version's UnpatchAll(owner) removes by MethodInfo
            // after filtering owner, deleting another owner's same Prefix too.
            // Remove only our owner; never unregister the main smoke patches.
            foreach (var method in patches.GetPatchedMethods().ToArray())
                patches.Unpatch(method, HarmonyPatchType.All, patches.Id);
            Check(methods.All(m => Prefixes(m) == inherited[m]),
                "scoped native Bot selector fixture must restore every inherited production selector prefix");
        }
    }

    private static void ObserveChoice(KernelSession __instance, TurnStartChoiceRequest request, PlanCardChoice? __result)
    {
        if (!ReferenceEquals(__instance, observed) || __result is null || request.Spec is not { } spec) return;
        var owner = spec.Options.FirstOrDefault()?.Preview.Owner;
        if (owner is null) return;
        actualChoices.Add((owner.NetId, TurnStartChoiceSupport.ResolveTokens(__result, spec.Options,
            spec.MinCount, spec.MaxCount).Select(c => c.Original).ToArray()));
    }
    private static void ObserveGenerated(ICombatState combatState, CardModel card)
    {
        if (ReferenceEquals(combatState, generatedCombat))
            nativeGenerated.Add(card.Owner.NetId + ":" + card.Id.Entry + ":" + card.Pile?.Type);
    }
    private static void StartSim(KernelSession sim, Player[] players, bool extra)
    {
        Check((bool)AccessTools.Method(typeof(KernelSession), "StartNextPlayerTurn").Invoke(sim, [players, extra])!,
            "production native party start must finish: " + sim.LastRoundFailure);
    }
    private static void DrawFive(Player p, CombatState c)
    {
        foreach (var model in new CardModel[] { ModelDb.Card<StrikeIronclad>(), ModelDb.Card<DefendIronclad>(),
            ModelDb.Card<Bash>(), ModelDb.Card<ShrugItOff>(), ModelDb.Card<PommelStrike>() })
            p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard(model, p));
    }
    private static void StartNative(NativeTurnState native, Player[] party, CombatState combat, bool extra,
        IReadOnlyList<(ulong Player, IReadOnlyList<CardModel> Cards)>? selections, ISet<ulong>? synchronous = null)
    {
        using var selector = CardSelectCmd.PushSelector(new MegaCrit.Sts2.Core.TestSupport.TestCardSelector());
        var chooser = (MegaCrit.Sts2.Core.TestSupport.TestCardSelector)CardSelectCmd.Selector!;
        if (!extra) combat.RoundNumber++;
        foreach (var p in extra ? party : combat.Players.ToArray()) p.PlayerCombatState!.IncrementTurnNumber();
        combat.CurrentSide = CombatSide.Player;
        foreach (var p in party) p.Creature.BeforeTurnStart(CombatSide.Player);
        Hook.BeforeSideTurnStart(combat, CombatSide.Player, party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        foreach (var p in party) p.Creature.AfterTurnStart(CombatSide.Player).GetAwaiter().GetResult();
        foreach (var p in party) Hook.AfterBlockCleared(combat, p.Creature).GetAwaiter().GetResult();
        var tasks = new List<(Player Player, Task Setup, TaskCompletionSource<IEnumerable<CardModel>>? First)>();
        foreach (var p in party)
        {
            var first = synchronous?.Contains(p.NetId) != true && selections?.Any(s => s.Player == p.NetId) == true
                ? chooser.SetupForAsyncCardSelection() : null;
            var context = new HookPlayerChoiceContext(p, party[0].NetId, MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly);
            var task = (Task)AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn").Invoke(CombatManager.Instance,
                [native.Turn, p, context])!;
            Check(first is null ? task.IsCompletedSuccessfully : !task.IsCompleted,
                "actual native setup must complete or pause at that participant's first card menu: "
                    + p.NetId + " " + task.Status + " " + task.Exception);
            tasks.Add((p, task, first));
        }
        Hook.AfterSideTurnStart(combat, CombatSide.Player, party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        foreach (var p in party) p.PlayerCombatState!.OrbQueue.AfterTurnStart(new HookPlayerChoiceContext(p,
            party[0].NetId, MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly)).GetAwaiter().GetResult();
        foreach (var (p, task, first) in tasks)
        {
            var choices = selections?.Where(s => s.Player == p.NetId).ToArray() ?? [];
            if (synchronous?.Contains(p.NetId) != true)
                foreach (var choice in choices.Skip(1)) chooser.PrepareToSelect(choice.Cards);
            if (first is not null) first.SetResult(choices[0].Cards);
            task.GetAwaiter().GetResult();
            p.PlayerCombatState!.Phase = PlayerTurnPhase.AutoPrePlay;
            Hook.AfterAutoPrePlayPhaseEntered(new HookPlayerChoiceContext(p, party[0].NetId,
                MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly), combat, p).GetAwaiter().GetResult();
            p.PlayerCombatState.Phase = PlayerTurnPhase.Play;
        }
    }
    private static string CardNames(IEnumerable<CardModel> c) => string.Join(',', c.Select(c => c.Id.Entry + "+" + c.CurrentUpgradeLevel));
    private static string Piles(KernelSession s, Player[] p) => string.Join('|', p.Select(x => $"{x.NetId}:H[{CardNames(s.Hand(x))}]D[{CardNames(s.DrawPile(x))}]C[{CardNames(s.Discard(x))}]E[{CardNames(s.Exhaust(x))}]"));
    private static string Piles(Player[] p) => string.Join('|', p.Select(x => $"{x.NetId}:H[{CardNames(x.PlayerCombatState!.Hand.Cards)}]D[{CardNames(x.PlayerCombatState.DrawPile.Cards)}]C[{CardNames(x.PlayerCombatState.DiscardPile.Cards)}]E[{CardNames(x.PlayerCombatState.ExhaustPile.Cards)}]"));
    private static string Enemies(KernelSession s, CombatState c) => string.Join('|', c.Enemies.Select(e => $"{e.CombatId}:{s.Hp(e)}/{s.Block(e)}"));
    private static string Enemies(CombatState c) => string.Join('|', c.Enemies.Select(e => $"{e.CombatId}:{e.CurrentHp}/{e.Block}"));
    private static (Player[] Party, CombatState Combat, Creature Enemy) Board(int count, string seed,
        int act = 0, Type? monster = null, string? moveId = null, int ascension = 0)
    {
        var party = Enumerable.Range(0, count).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, 910UL + (ulong)i)).ToArray();
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
    private static ulong NativeClock() => 0;
    private static IEnumerable<CodeInstruction> PresentationClock(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType?.FullName == "Godot.Time"
                && method.Name == "GetTicksMsec")
                instruction.operand = AccessTools.Method(typeof(SzgModelRepairScenarios), nameof(NativeClock));
            yield return instruction;
        }
    }
    private static bool Exists(ref bool __result) { __result = true; return false; }
    private static bool PlayerName(ulong playerId, ref string __result) { __result = playerId.ToString(); return false; }
    private static bool Version(ref PeerVersionInfo __result) { __result = default; return false; }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("SZG model: " + message); }

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
        internal object Turn => field.GetValue(CombatManager.Instance)!;
        internal void SetExtraParticipants(Player[] players)
        { var list = (List<Player>)AccessTools.Property(field.FieldType, "PlayersTakingExtraTurn").GetValue(Turn)!; list.Clear(); list.AddRange(players); }
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
