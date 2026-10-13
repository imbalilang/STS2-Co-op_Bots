using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
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
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// combat039 native-v1: Sparkling Rouge applies Strength/Dexterity in the third
// AfterBlockCleared hook. The model queued its amount-change notifications and
// then tried to fork the party-setup reference while those notifications remained.
// Physical R2 2026-10-09: all three faults actually rebuilt0, named runtime RED1,
// then restored exact source bytes and rebuilt0/named GREEN0; original native cases passed.
// Proof: work/architecture-refactor-20261009/pending-power-turn-boundary-v1/
// execution-20261009T124115372069Z/evidence/proof.json. Actual RED exception lines:
// System.InvalidOperationException: Pending power turn: Sparkling Rouge third-turn hooks must settle their power notifications before the party-setup fork: Cannot fork with pending Power amount changes: STRENGTH_POWER:1,DEXTERITY_POWER:1.
// System.InvalidOperationException: Pending power turn: completed native Sparkling Rouge grants must survive the reference fork instead of being erased or duplicated
// System.InvalidOperationException: Pending power turn: normal and extra-turn power settlement must match native party piles, resources, shared RNG, excluded seats and the frozen parent
// The native oracle below uses real Hook/PowerCmd and native SetupPlayerTurn;
// presentation/version methods alone are stubbed and all scoped globals restored.
internal static class PendingPowerTurnBoundaryScenarios
{
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.pending-power-turn.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PendingPowerTurnBoundaryScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "Exists"),
            prefix: new HarmonyMethod(typeof(PendingPowerTurnBoundaryScenarios), nameof(Exists)));
        presentation.Patch(AccessTools.Method(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerName)),
            prefix: new HarmonyMethod(typeof(PendingPowerTurnBoundaryScenarios), nameof(PlayerName)));
        presentation.Patch(AccessTools.Method(typeof(PeerVersionInfo), nameof(PeerVersionInfo.LocalDefault)),
            prefix: new HarmonyMethod(typeof(PendingPowerTurnBoundaryScenarios), nameof(Version)));
        // Skip only the first-shuffle tutorial; native shuffle/draw and RNG remain active.
        presentation.Patch(AccessTools.Method(typeof(CardPileCmd), "ShuffleFtueCheck"),
            prefix: new HarmonyMethod(typeof(PendingPowerTurnBoundaryScenarios), nameof(NoTutorial)));
        var net = AccessTools.Property(typeof(RunManager), nameof(RunManager.NetService));
        var previousNet = net.GetValue(RunManager.Instance);
        try
        {
            net.SetValue(RunManager.Instance, new NetReplayGameService(1));
            if (caseName is null or "rouge")
            { Compare(1, extra: false); Console.WriteLine("PASS: Pending power turn rouge"); }
            if (caseName is null or "party")
            {
                Compare(4, extra: false);
                Compare(4, extra: true);
                Console.WriteLine("PASS: Pending power turn party");
            }
        }
        finally
        { net.SetValue(RunManager.Instance, previousNet); presentation.UnpatchAll(presentation.Id); }
    }

    private static void Compare(int count, bool extra)
    {
        var (party, combat) = Board(count, extra);
        using var native = new NativeState(combat);
        var participants = extra ? new[] { party[^1] } : party;
        var root = KernelSession.Capture(combat).ConfigureSynchronousChoicePlayers(party.Select(p => p.NetId));
        var frozen = root.PartyStateText();
        var simulation = root.ForkForReplay();
        bool settled;
        string failure = "";
        try
        {
            settled = (bool)AccessTools.Method(typeof(KernelSession), "StartNextPlayerTurn")
                .Invoke(simulation, [participants, extra])!;
        }
        catch (TargetInvocationException error)
        { settled = false; failure = error.InnerException?.Message ?? error.Message; }
        Check(settled,
            "Sparkling Rouge third-turn hooks must settle their power notifications before the party-setup fork: " + failure);
        var child = simulation.Fork(); // exercise the real complete-state/history qualification
        StartNative(native, combat, participants, extra);
        var state = (SimulatedCombatState)simulation.Simulator.State.CombatState;
        Check(participants.All(p => simulation.Power<StrengthPower>(p.Creature) == NativePower<StrengthPower>(p.Creature)
                && simulation.Power<DexterityPower>(p.Creature) == NativePower<DexterityPower>(p.Creature)
                && child.Power<StrengthPower>(p.Creature) == simulation.Power<StrengthPower>(p.Creature)
                && child.Power<DexterityPower>(p.Creature) == simulation.Power<DexterityPower>(p.Creature))
            && NativePower<StrengthPower>(party[^1].Creature) == 1
            && NativePower<DexterityPower>(party[^1].Creature) == 1,
            "completed native Sparkling Rouge grants must survive the reference fork instead of being erased or duplicated");
        Check(party.All(p => simulation.Hp(p.Creature) == p.Creature.CurrentHp
                && simulation.Block(p.Creature) == p.Creature.Block
                && simulation.Energy(p) == p.PlayerCombatState!.Energy
                && state.GetPlayerTurnNumber(p) == p.PlayerCombatState!.TurnNumber)
            && state.RoundNumber == combat.RoundNumber && Piles(simulation, party) == Piles(party)
            && simulation.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState()
            && simulation.Simulator.Rng.CombatCardSelectionState == ((RunState)combat.RunState).Rng.CombatCardSelection.CaptureState()
            && root.PartyStateText() == frozen
            && (!extra || party.Take(party.Length - 1).All(p => simulation.Block(p.Creature) == 7
                && simulation.Power<StrengthPower>(p.Creature) == 0
                && simulation.Power<DexterityPower>(p.Creature) == 0)),
            "normal and extra-turn power settlement must match native party piles, resources, shared RNG, excluded seats and the frozen parent");
    }

    private static (Player[] Party, CombatState Combat) Board(int count, bool extra)
    {
        var party = Enumerable.Range(0, count)
            .Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, 960UL + (ulong)i)).ToArray();
        var run = RunState.CreateForTest(party, seed: $"PENDING-POWER-{count}-{extra}");
        var combat = new CombatState(runState: run) { RoundNumber = 2 };
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.Creature.GainBlockInternal(7);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(1);
            AccessTools.Property(typeof(PlayerCombatState), nameof(PlayerCombatState.TurnNumber))
                .SetValue(p.PlayerCombatState, extra && p != party[^1] ? 3 : 2);
            p.AddRelicInternal(ModelDb.Relic<SparklingRouge>().ToMutable());
            // Force a real shared native shuffle; comparing untouched RNG would
            // not exercise the draw/shuffle continuation after the amount hooks.
            for (var i = 0; i < 10; i++)
            {
                var card = i % 2 == 0 ? (CardModel)combat.CreateCard<StrikeIronclad>(p)
                    : combat.CreateCard<DefendIronclad>(p);
                p.PlayerCombatState.DiscardPile.AddInternal(card);
            }
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy);
        using var native = new NativeState(combat);
        enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(1000); enemy.SetCurrentHpInternal(1000);
        return (party, combat);
    }

    private static void StartNative(NativeState native, CombatState combat, Player[] participants, bool extra)
    {
        using var selector = CardSelectCmd.PushSelector(new MegaCrit.Sts2.Core.TestSupport.TestCardSelector());
        if (extra) native.SetExtraParticipants(participants);
        if (!extra) combat.RoundNumber++;
        foreach (var p in extra ? participants : combat.Players.ToArray()) p.PlayerCombatState!.IncrementTurnNumber();
        combat.CurrentSide = CombatSide.Player;
        foreach (var p in participants) p.Creature.BeforeTurnStart(CombatSide.Player);
        Hook.BeforeSideTurnStart(combat, CombatSide.Player, participants.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        foreach (var p in participants) p.Creature.AfterTurnStart(CombatSide.Player).GetAwaiter().GetResult();
        foreach (var p in participants) Hook.AfterBlockCleared(combat, p.Creature).GetAwaiter().GetResult();
        var tasks = new List<Task>();
        foreach (var p in participants)
        {
            var context = new HookPlayerChoiceContext(p, participants[0].NetId,
                MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly);
            tasks.Add((Task)AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn")
                .Invoke(CombatManager.Instance, [native.Turn, p, context])!);
        }
        Hook.AfterSideTurnStart(combat, CombatSide.Player, participants.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        foreach (var p in participants)
            p.PlayerCombatState!.OrbQueue.AfterTurnStart(new HookPlayerChoiceContext(p, participants[0].NetId,
                MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly)).GetAwaiter().GetResult();
        foreach (var task in tasks) task.GetAwaiter().GetResult();
        foreach (var p in participants)
        {
            p.PlayerCombatState!.Phase = PlayerTurnPhase.AutoPrePlay;
            Hook.AfterAutoPrePlayPhaseEntered(new HookPlayerChoiceContext(p, participants[0].NetId,
                MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly), combat, p).GetAwaiter().GetResult();
            p.PlayerCombatState.Phase = PlayerTurnPhase.Play;
        }
    }

    private static int NativePower<T>(Creature c) where T : PowerModel => c.Powers.OfType<T>().Sum(p => p.Amount);
    private static string Names(IEnumerable<CardModel> cards) => string.Join(',', cards.Select(c => c.Id.Entry + "+" + c.CurrentUpgradeLevel));
    private static string Piles(KernelSession s, Player[] party) => string.Join('|', party.Select(p =>
        $"{p.NetId}:H[{Names(s.Hand(p))}]D[{Names(s.DrawPile(p))}]C[{Names(s.Discard(p))}]X[{Names(s.Exhaust(p))}]"));
    private static string Piles(Player[] party) => string.Join('|', party.Select(p =>
        $"{p.NetId}:H[{Names(p.PlayerCombatState!.Hand.Cards)}]D[{Names(p.PlayerCombatState.DrawPile.Cards)}]C[{Names(p.PlayerCombatState.DiscardPile.Cards)}]X[{Names(p.PlayerCombatState.ExhaustPile.Cards)}]"));
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Pending power turn: " + message); }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool Exists(ref bool __result) { __result = false; return false; }
    private static bool PlayerName(ulong playerId, ref string __result) { __result = playerId.ToString(); return false; }
    private static bool NoTutorial(ref Task __result) { __result = Task.CompletedTask; return false; }
    private static bool Version(ref PeerVersionInfo __result)
    { __result = default; return false; }

    private sealed class NativeState : IDisposable
    {
        private readonly FieldInfo turn = AccessTools.Field(typeof(CombatManager), "_turnState");
        private readonly PropertyInfo run = AccessTools.Property(typeof(RunManager), "State");
        private readonly PropertyInfo ascension = AccessTools.Property(typeof(RunManager), nameof(RunManager.AscensionManager));
        private readonly object? previousTurn, previousRun, previousAscension;
        private readonly ulong? previousLocal = LocalContext.NetId;
        internal NativeState(CombatState combat)
        {
            previousTurn = turn.GetValue(CombatManager.Instance);
            previousRun = run.GetValue(RunManager.Instance);
            previousAscension = ascension.GetValue(RunManager.Instance);
            run.SetValue(RunManager.Instance, combat.RunState);
            ascension.SetValue(RunManager.Instance, new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(combat.RunState.AscensionLevel));
            LocalContext.NetId = combat.Players[0].NetId;
            var state = Activator.CreateInstance(turn.FieldType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
            AccessTools.Property(turn.FieldType, "IsInProgress").SetValue(state, true);
            AccessTools.Property(turn.FieldType, "IsStarting").SetValue(state, false);
            turn.SetValue(CombatManager.Instance, state);
        }
        internal object Turn => turn.GetValue(CombatManager.Instance)!;
        internal void SetExtraParticipants(Player[] participants)
        {
            var list = (List<Player>)AccessTools.Property(turn.FieldType, "PlayersTakingExtraTurn").GetValue(Turn)!;
            list.Clear(); list.AddRange(participants);
        }
        public void Dispose()
        {
            turn.SetValue(CombatManager.Instance, previousTurn);
            LocalContext.NetId = previousLocal;
            run.SetValue(RunManager.Instance, previousRun);
            ascension.SetValue(RunManager.Instance, previousAscension);
        }
    }
}
