using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Unlocks;

internal static class PostDecisionProjectionScenarios
{
    // R2 2026-10-07: nine faults in a byte-exact frozen copy of the production
    // PersistentPowerSupport source each produced the named RED1 below; exact
    // byte restoration produced GREEN0. Production files were never mutated.
    // Every build runs the original game sts2.dll, not Publicizer metadata.
    // Logs/commands/native/source hashes: projection/projection-r2-results.json.
    // Raw System.InvalidOperationException suffixes:
    // Post-decision projection: frozen Ring of the Snake future draw must remain five after LIVE turn advances
    // Post-decision projection: frozen BREAD max energy must not double its bonus after LIVE turn advances
    // Post-decision projection: frozen PAELS_FLESH max energy must not double its bonus after LIVE turn advances
    // Post-decision projection: Pendulum must use its incremented branch counter instead of its captured native field
    // Post-decision projection: Pollinous Core must use its fourth branch draw counter before consuming the bonus
    // Post-decision projection: Pocketwatch must price the completed four-card turn from branch history without a draw bonus
    // Post-decision projection: frozen future draw must consume the same complete native Shuffle RNG state
    // Post-decision projection: EndTurn must draw five and perform the required shuffle after its frozen live owner advances
    // Post-decision projection: frozen BAG_OF_PREPARATION draw at turn 1 must remain equal after LIVE turn advances again
    // Import adapter mirror R2 (projection-import-r2-results.json), likewise
    // RED1 followed by byte-exact restoration GREEN0; raw RuntimeError text:
    // Frozen hook source hash changed: PersistentPowerSupport; review native listener order and captured relic semantics.
    // Frozen draw/energy hook seam changed; review before importing.
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.post-decision.projection.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PostDecisionProjectionScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Method(typeof(CardPileCmd), "ShuffleFtueCheck"),
            prefix: new HarmonyMethod(typeof(PostDecisionProjectionScenarios), nameof(NoTutorial)));
        try
        {
            caseName ??= Environment.GetCommandLineArgs().FirstOrDefault(a =>
                a.StartsWith("--post-decision-projection=", StringComparison.Ordinal))?.Split('=')[1];
            if (caseName == "all") caseName = null;
            if (caseName is not (null or "draw" or "energy" or "energy-bread" or "energy-paels"
                or "stateful" or "family" or "lifecycle" or "shuffle"))
                throw new InvalidOperationException("Unknown post-decision projection case: " + caseName);
            if (caseName is null or "draw") FrozenDrawIgnoresLiveTurn();
            if (caseName is null or "energy" or "energy-bread" or "energy-paels") FrozenEnergyIgnoresLiveTurn(caseName);
            if (caseName is null or "stateful") StatefulDrawUsesTheBranchCounter();
            if (caseName is null or "family") NativeRelicFamilyMatches();
            if (caseName is null or "lifecycle") FrozenDrawThroughTheSharedLifecycle();
            if (caseName is null or "shuffle") MissingDrawDoesNotSkipNativeShuffle();
            Console.WriteLine("PASS: post-decision projection " + (caseName ?? "all"));
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool NoTutorial(ref Task __result) { __result = Task.CompletedTask; return false; }
    private static SimulatedCombatState Combat(KernelSession session)
        => (SimulatedCombatState)AccessTools.Property(typeof(KernelSession), "Combat").GetValue(session)!;
    private static CombatPredictionSimulator Simulator(KernelSession session)
        => (CombatPredictionSimulator)AccessTools.Field(typeof(KernelSession), "simulator").GetValue(session)!;

    private static void FrozenDrawIgnoresLiveTurn()
    {
        var (live, player) = Board("POST-FROZEN-DRAW");
        player.AddRelicInternal(ModelDb.Relic<RingOfTheSnake>().ToMutable());
        var root = KernelSession.Capture(live); var before = root.PartyStateText();
        int FutureDraw()
        {
            var branch = root.Fork(); var combat = Combat(branch); combat.AdvancePlayerTurn(player);
            return PersistentPowerSupport.ConsumeModifiedHandDraw(combat, player, 5);
        }
        Check(FutureDraw() == 5, "captured turn one must predict five cards on turn two");
        player.PlayerCombatState!.IncrementTurnNumber();
        Check(FutureDraw() == 5,
            "frozen Ring of the Snake future draw must remain five after LIVE turn advances");
        Check(root.PartyStateText() == before && player.PlayerCombatState.Hand.IsEmpty,
            "frozen draw evaluation must preserve captured root and live piles");
        var native = Hook.ModifyHandDraw(live, player, 5, out _);
        Check(native == 5, "native turn two Ring of the Snake must draw five");
    }

    private static void FrozenEnergyIgnoresLiveTurn(string? caseName)
    {
        foreach (RelicModel relic in new RelicModel[]
                 { ModelDb.Relic<Bread>().ToMutable(), ModelDb.Relic<PaelsFlesh>().ToMutable() })
        {
            if (caseName == "energy-bread" && relic is not Bread) continue;
            if (caseName == "energy-paels" && relic is not PaelsFlesh) continue;
            var (live, player) = Board("POST-FROZEN-ENERGY-" + relic.Id.Entry);
            player.AddRelicInternal(relic);
            var root = KernelSession.Capture(live); var before = root.PartyStateText();
            int FutureEnergy()
            {
                var branch = root.Fork(); var combat = Combat(branch);
                combat.AdvancePlayerTurn(player); combat.AdvancePlayerTurn(player);
                return PersistentPowerSupport.GetModifiedMaxEnergy(combat, player);
            }
            int expected = player.MaxEnergy + 1;
            Check(FutureEnergy() == expected, "captured energy relic must grant its native turn-three bonus");
            player.PlayerCombatState!.IncrementTurnNumber(); player.PlayerCombatState.IncrementTurnNumber();
            Check(FutureEnergy() == expected,
                "frozen " + relic.Id.Entry + " max energy must not double its bonus after LIVE turn advances");
            Check(Hook.ModifyMaxEnergy(live, player, player.MaxEnergy) == expected,
                "native turn-three energy reference must match the frozen branch");
            Check(root.PartyStateText() == before, "energy evaluation must preserve captured root");
        }
    }

    private static void StatefulDrawUsesTheBranchCounter()
    {
        var (live, player) = Board("POST-STATEFUL-DRAW");
        var pendulum = (Pendulum)ModelDb.Relic<Pendulum>().ToMutable(); pendulum.TurnsSeen = 2;
        player.AddRelicInternal(pendulum);
        var root = KernelSession.Capture(live); var before = root.PartyStateText();
        var branch = root.Fork(); var combat = Combat(branch); var simulator = Simulator(branch);
        Check(!combat.PrepareRelicsBeforeHandDraw(simulator, player, new TurnStartChoiceCursor([])),
            "Pendulum draw preparation must settle without a choice");
        Check(PersistentPowerSupport.ConsumeModifiedHandDraw(combat, player, 5) == 6,
            "Pendulum must use its incremented branch counter instead of its captured native field");
        pendulum.TurnsSeen = 1;
        Check(PersistentPowerSupport.GetModifiedHandDraw(combat, player, 5) == 6,
            "a LIVE stateful relic counter change must not contaminate a prepared branch draw");
        Check(root.PartyStateText() == before, "stateful draw must preserve its root counter");

        var (pollLive, pollPlayer) = Board("POST-POLLINOUS-DRAW");
        var poll = (PollinousCore)ModelDb.Relic<PollinousCore>().ToMutable(); poll.TurnsSeen = 3;
        pollPlayer.AddRelicInternal(poll);
        var pollRoot = KernelSession.Capture(pollLive); var pollBranch = pollRoot.Fork();
        Check(!Combat(pollBranch).PrepareRelicsBeforeHandDraw(Simulator(pollBranch), pollPlayer, new TurnStartChoiceCursor([])),
            "Pollinous Core draw preparation must settle");
        Check(PersistentPowerSupport.ConsumeModifiedHandDraw(Combat(pollBranch), pollPlayer, 5) == 7,
            "Pollinous Core must use its fourth branch draw counter before consuming the bonus");
        poll.BeforeHandDraw(pollPlayer, new ThrowingPlayerChoiceContext(), pollLive).GetAwaiter().GetResult();
        Check(Hook.ModifyHandDraw(pollLive, pollPlayer, 5, out _) == 7,
            "native fourth Pollinous Core draw must match the branch bonus");
        poll.AfterModifyingHandDraw().GetAwaiter().GetResult();
        Check(PersistentPowerSupport.GetModifiedHandDraw(Combat(pollBranch), pollPlayer, 5) == 5 && poll.TurnsSeen == 0,
            "native and projected Pollinous Core must both consume the draw bonus once");

        var (watchLive, watchPlayer) = Board("POST-POCKETWATCH-DRAW");
        var watch = (Pocketwatch)ModelDb.Relic<Pocketwatch>().ToMutable(); watchPlayer.AddRelicInternal(watch);
        AccessTools.Field(typeof(Pocketwatch), "_cardsPlayedThisTurn").SetValue(watch, 4);
        var watchBranch = KernelSession.Capture(watchLive).Fork(); var watchCombat = Combat(watchBranch);
        watchCombat.AdvancePlayerTurn(watchPlayer);
        Check(watchCombat.PrepareRelicsBeforeSideTurnStart(Simulator(watchBranch), [watchPlayer.Creature]),
            "Pocketwatch next-side counter preparation must settle");
        watchPlayer.PlayerCombatState!.IncrementTurnNumber();
        watch.BeforeSideTurnStart(new ThrowingPlayerChoiceContext(), CombatSide.Player, [watchPlayer.Creature], watchLive)
            .GetAwaiter().GetResult();
        Check(PersistentPowerSupport.ConsumeModifiedHandDraw(watchCombat, watchPlayer, 5) == 5
              && Hook.ModifyHandDraw(watchLive, watchPlayer, 5, out _) == 5,
            "Pocketwatch must price the completed four-card turn from branch history without a draw bonus");
    }

    private static void NativeRelicFamilyMatches()
    {
        Func<RelicModel>[] factories =
        [() => ModelDb.Relic<BagOfPreparation>().ToMutable(), () => ModelDb.Relic<BigMushroom>().ToMutable(),
         () => ModelDb.Relic<RingOfTheDrake>().ToMutable(), () => ModelDb.Relic<RingOfTheSnake>().ToMutable(),
         () => ModelDb.Relic<Pendulum>().ToMutable(), () => ModelDb.Relic<Pocketwatch>().ToMutable(),
         () => ModelDb.Relic<PollinousCore>().ToMutable(), () => ModelDb.Relic<PaelsBlood>().ToMutable(),
         () => ModelDb.Relic<Fiddle>().ToMutable(), () => ModelDb.Relic<SneckoEye>().ToMutable(),
         () => ModelDb.Relic<BoomingConch>().ToMutable()];
        foreach (var make in factories)
        foreach (int targetTurn in new[] { 1, 2, 4 })
        {
            var (live, player) = Board("POST-DRAW-FAMILY"); var relic = make(); player.AddRelicInternal(relic);
            if (relic is BoomingConch)
                ((RunState)live.RunState).PushRoom(new CombatRoom(ModelDb.Encounter<InfestedPrismsElite>().ToMutable(), live.RunState));
            var root = KernelSession.Capture(live); var branch = root.Fork(); var combat = Combat(branch);
            for (int turn = 1; turn < targetTurn; turn++) combat.AdvancePlayerTurn(player);
            // The unchanged native counter is intentional here: this is the modifier
            // stage; the separate stateful case tests BeforeHandDraw progression.
            for (int turn = 1; turn < targetTurn; turn++) player.PlayerCombatState!.IncrementTurnNumber();
            int nativeCount = (int)Hook.ModifyHandDraw(live, player, 5, out _);
            Check(PersistentPowerSupport.GetModifiedHandDraw(combat, player, 5) == nativeCount,
                $"captured {relic.Id.Entry} draw at turn {targetTurn} must match the native modifier");
            for (int turn = 0; turn < 4; turn++) player.PlayerCombatState!.IncrementTurnNumber();
            if (relic is BoomingConch) ((RunState)live.RunState).PopCurrentRoom();
            Check(PersistentPowerSupport.GetModifiedHandDraw(combat, player, 5) == nativeCount,
                $"frozen {relic.Id.Entry} draw at turn {targetTurn} must remain equal after LIVE turn advances again");
            var stranger = Player.CreateForNewRun<Deprived>(UnlockState.all, 84512);
            Check(PersistentPowerSupport.GetModifiedHandDraw(combat, stranger, 5) == 5,
                "another seat must not receive the relic owner's draw contribution");
        }
    }

    private static void FrozenDrawThroughTheSharedLifecycle()
    {
        var (live, player) = Board("POST-FROZEN-SHARED-LIFECYCLE");
        player.AddRelicInternal(ModelDb.Relic<RingOfTheSnake>().ToMutable());
        for (int i = 0; i < 3; i++) player.PlayerCombatState!.DrawPile.AddInternal(live.CreateCard<StrikeSilent>(player));
        for (int i = 0; i < 10; i++) player.PlayerCombatState!.DiscardPile.AddInternal(live.CreateCard<DefendSilent>(player));
        var root = KernelSession.Capture(live); var before = root.PartyStateText();
        player.PlayerCombatState!.IncrementTurnNumber();
        var branch = root.ForkForReplay();
        // This is the production side-ending entry, not a direct modifier probe.
        Check(branch.EndTurn(player, 2, out var boundary), "frozen shared lifecycle must settle: " + boundary + branch.LastRoundFailure);
        Check(branch.Hand(player).Count == 5 && Simulator(branch).ShuffleEventCount == 1,
            "EndTurn must draw five and perform the required shuffle after its frozen live owner advances");
        Check(root.PartyStateText() == before && player.PlayerCombatState.Hand.IsEmpty,
            "shared lifecycle must preserve root and live hand while the live owner has advanced");
    }

    // Native shuffle consumes N-1 Shuffle calls, with one further random insertion
    // for Biiig Hug's Soot. The historical deltas 9/12/13 can therefore result from
    // missing two draws at this threshold. Matching the signature is NOT replaying
    // those four historical states: their full piles/boundary dumps were not saved.
    private static void MissingDrawDoesNotSkipNativeShuffle()
    {
        foreach (var scenario in new[] { (Count: 10, Hug: false), (Count: 13, Hug: false), (Count: 13, Hug: true) })
        {
            var (live, player) = Board("POST-SHUFFLE-THRESHOLD-" + scenario.Count + scenario.Hug);
            player.AddRelicInternal(ModelDb.Relic<RingOfTheSnake>().ToMutable());
            if (scenario.Hug) player.AddRelicInternal(ModelDb.Relic<BiiigHug>().ToMutable());
            for (int i = 0; i < 3; i++)
            {
                var card = live.CreateCard<StrikeSilent>(player); if (i == 1) card.UpgradeInternal();
                player.PlayerCombatState!.DrawPile.AddInternal(card);
            }
            for (int i = 0; i < scenario.Count; i++)
            {
                var card = live.CreateCard<DefendSilent>(player); if (i % 2 == 0) card.UpgradeInternal();
                player.PlayerCombatState!.DiscardPile.AddInternal(card);
            }
            var root = KernelSession.Capture(live); var before = root.PartyStateText();
            player.PlayerCombatState!.IncrementTurnNumber();
            var branch = root.Fork(); var combat = Combat(branch); var simulator = Simulator(branch);
            combat.AdvancePlayerTurn(player);
            int count = PersistentPowerSupport.ConsumeModifiedHandDraw(combat, player, 5);
            simulator.Draw(player, count, fromHandDraw: true);
            CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), Hook.ModifyHandDraw(live, player, 5, out _),
                player, fromHandDraw: true).GetAwaiter().GetResult();
            Check(Shuffle(branch.StateText(player)) == Shuffle(KernelSession.CaptureLiveStateText(live, player)),
                "frozen future draw must consume the same complete native Shuffle RNG state");
            Check(branch.Hand(player).Count == 5 && player.PlayerCombatState.Hand.Cards.Count == 5,
                "frozen future draw must reach the native shuffle threshold with all five cards");
            Check(Keys(branch.Hand(player), branch) == Keys(player.PlayerCombatState.Hand.Cards)
                  && Keys(branch.DrawPile(player), branch) == Keys(player.PlayerCombatState.DrawPile.Cards),
                "native threshold draw and Biiig Hug insertion must preserve ordered card keys");
            Check(root.PartyStateText() == before, "native shuffle comparison must not mutate its captured root");
            Console.WriteLine($"Native threshold: discard={scenario.Count} BiiigHug={scenario.Hug} shuffle={Shuffle(branch.StateText(player))}");
        }
    }

    private static string Keys(IEnumerable<CardModel> cards, KernelSession? branch = null)
        => string.Join(',', cards.Select(c => branch?.CardStateKey(c) ?? CardChoiceSupport.ChoiceCardKey(c)));
    private static string Shuffle(string text) => text.Split(';').First(f => f.StartsWith("R=", StringComparison.Ordinal)).Split('/')[0];
    private static (CombatState Combat, Player Player) Board(string seed)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, 84511);
        player.ResetCombatState();
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: seed)); combat.AddPlayer(player);
        player.Creature.SetMaxHpInternal(100); player.Creature.SetCurrentHpInternal(100);
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "attack");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(2000); enemy.SetCurrentHpInternal(2000);
        var move = new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)); move.FollowUpState = move;
        enemy.Monster.SetMoveImmediate(move, true);
        return (combat, player);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Post-decision projection: " + message); }
}
