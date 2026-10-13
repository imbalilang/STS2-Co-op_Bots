using CoopBots;
using CoopBots.Kernel;
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
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

// Test-isolation R2 2026-10-08: successful private build -> named RED1 -> exact restore -> build0/GREEN0.
// Raw results: work/latest-run-repair-20261008/choice/fixture-isolation/r2-results.json.
// native-tools-scoped-input: System.InvalidOperationException: Latest projection: independent Tools native witness must own its scoped selector input
// ringing-complete-menu-rejection: System.InvalidOperationException: Latest projection: published Ringing menu must reject a missing unselected native option
// ringing-native-ordered-pile-input: System.InvalidOperationException: Latest projection: published Ringing menu must match native ordered draw and shuffle candidates
// Ringing native pile reconstruction checks menu identity, not a complete historical/combat replay.
internal static class LatestProjectionRepairScenarios
{
    // R2 2026-10-07: eleven production faults each exited 1 at the named
    // assertion below; exact source-byte restoration followed by exit0.
    // Commands, raw output and red/green logs: projection-r2-results.json.
    // Raw red exception suffixes (all System.InvalidOperationException):
    // Latest projection: dead seat must reset its this-turn card history at the shared round boundary
    // Latest projection: enemy cached this-turn attack history must expire when the shared player side starts
    // Latest projection: native SwitchSides also advances the dead seat's player turn number
    // Latest projection: Ambergris extra turn must skip enemy attacks for the whole party
    // Latest projection: a fork must not apply a nonparticipant's turn-end damage during the extra turn
    //   (independently: Paels participation, extra roster fork, turn-start policy fork)
    // Latest projection: captured extra turn must end only its participant then resume the native shared turn
    // Latest projection: CardBranches must retain automatic enemy choice when Void Form forces the turn to end: round-pending-choice:knowledge-demon/KnowledgeDemonChoiceRequest@party-move
    // Latest projection: Tools predicted discard must preserve the combat engine and protection; expected STRIKE_SILENT, got PIERCING_WAIL
    // Latest projection: native Tools source must use the same combat discard policy as the projection
    // Supplement R2 2026-10-07: ten deliberate C# faults exited1 at these
    // assertions, followed by exact source-byte restoration and exit0. Two
    // importer faults also RED1/GREEN0: changed base hash and retained null-safe
    // display service support. Logs: projection-supplement-r2-results.json and
    // projection-supplement-import-support-r2-results.json.
    // Raw System.InvalidOperationException suffixes:
    // Latest projection: replayed Ringing suffix must publish the branch's pre-play affliction key and second-card occurrence
    //   (independently stale key and wrong duplicate occurrence)
    // Latest projection: replayed Ringing Survivor must publish current discard tokens without earlier action choices
    //   (independently stale choice key and cumulative previous-action tokens)
    // Latest projection: refinement boundary must remain frozen when its replay parent plays another Inflame
    //   (independently missing parent sharing marker and bypassed write detach)
    // Latest projection: refinement boundary must retain Plating2 when the later side start reduces its parent's Plating
    // Latest projection: native source alias must not bypass power ownership and mutate its frozen sibling
    // Latest projection: parallel read-only forks must all retain the parent's original Strength after its later write
    // Latest projection: dead Paels Eye must keep its native previous participation during normal and extra side starts
    // Latest projection: first real Survivor must supply one pre-Ringing discard token
    //   (fresh import deliberately bound displayNames.Card to its null receiver)
    // Tools native-boundary update R2 2026-10-07 (two new pairs, not the
    // historical eleven/ten groups above). The intent-only Mock9x3 and implicit
    // energy boundary were replaced with actual native Mock1/Strength8 and
    // explicit MaxEnergy4. Wrong predicted choice and bypassed native selector
    // exited1 at these raw assertions, each exact production-mirror restoration
    // rebuilt successfully and exited0. Logs: choice/tools-native-review/r2-results.json.
    // System.InvalidOperationException: Latest projection: Tools native chosen discard must preserve HP and both engines while enabling six more damage than discarding Strike
    // System.InvalidOperationException: Latest projection: native Tools source must use the same combat discard policy as the projection
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.latest-projection.presentation");
        presentation.Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LatestProjectionRepairScenarios), nameof(Format)));
        // This callback only opens/records the first-shuffle tutorial. Native
        // shuffle ordering, piles and RNG execute unchanged below it.
        presentation.Patch(AccessTools.Method(typeof(CardPileCmd), "ShuffleFtueCheck"),
            prefix: new HarmonyMethod(typeof(LatestProjectionRepairScenarios), nameof(NoTutorial)));
        try
        {
        var supplement = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--projection-supplement=", StringComparison.Ordinal));
        if (supplement is not null)
        {
            switch (supplement.Split('=')[1])
            {
                case "ringing": ReplayRingingPublishesExecutableKeys(); break;
                case "ringing-choices": ReplayRingingPublishesCurrentChoiceTokens(); break;
                case "cow": RefinementBoundaryPowersStayFrozen(); break;
                case "cow-parallel": ParallelForkPowersStayFrozen(); break;
                case "cow-plating": RefinementBoundaryPlatingStaysFrozen(); break;
                case "dead-paels": DeadPaelsEyeKeepsNativeInactiveState(); break;
                default: throw new InvalidOperationException("Unknown projection supplement: " + supplement);
            }
            Console.WriteLine("PASS: latest projection supplement " + supplement);
            return;
        }
        DeadSeatHistoryAdvances();
        ExtraTurnKeepsPartyLifecycle();
        CaptureAnActiveExtraTurn();
        ForcedEndKeepsAutomaticChoices();
        ToolsOfTradeUsesSharedDiscard(false);
        ToolsOfTradeUsesSharedDiscard(true);
        ToolsOfTradeUsesSharedDiscard(true, shuffle: true);
        BrandWithNoRemainingHand();
        ReplayRingingPublishesExecutableKeys();
        ReplayRingingPublishesCurrentChoiceTokens();
        RefinementBoundaryPowersStayFrozen();
        ParallelForkPowersStayFrozen();
        RefinementBoundaryPlatingStaysFrozen();
        DeadPaelsEyeKeepsNativeInactiveState();
        Console.WriteLine("PASS: latest projection repair covers dead-seat history, extra turns, native Tools/Brand, Ringing replay and frozen Power boundaries.");
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool NoTutorial(ref Task __result) { __result = Task.CompletedTask; return false; }
    private static IReadOnlyList<Player> nativeExtraSeats = [];
    private static bool CapturedExtraSeats(ref IReadOnlyList<Player> __result)
    { __result = nativeExtraSeats; return false; }
    private static bool CapturedReady(Player __0, ref bool __result)
    { __result = !nativeExtraSeats.Contains(__0); return false; }

    private static void CaptureAnActiveExtraTurn()
    {
        var extra = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 198, 2));
        var teammate = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 198, 3));
        var combat = Board([extra, teammate], "LATEST-CAPTURE-EXTRA"); Enemy(combat, 1);
        extra.PlayerCombatState!.IncrementTurnNumber();
        ModelDb.Power<DisintegrationPower>().ToMutable().ApplyInternal(teammate.Creature, 2, true);
        for (var i = 0; i < 5; i++)
        {
            extra.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeSilent>(extra));
            teammate.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<DefendIronclad>(teammate));
        }
        // Supply only native boundary metadata to the headless host. Neither
        // hook changes a combat rule or the projected driver under test.
        var metadata = new Harmony("coopbots.latest-projection.extra-capture");
        nativeExtraSeats = [extra];
        KernelSession root;
        try
        {
            metadata.Patch(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.PlayersTakingExtraTurn)),
                prefix: new HarmonyMethod(typeof(LatestProjectionRepairScenarios), nameof(CapturedExtraSeats)));
            metadata.Patch(AccessTools.Method(typeof(CombatManager), nameof(CombatManager.IsPlayerReadyToEndTurn)),
                prefix: new HarmonyMethod(typeof(LatestProjectionRepairScenarios), nameof(CapturedReady)));
            root = KernelSession.Capture(combat);
        }
        finally { metadata.UnpatchAll(metadata.Id); nativeExtraSeats = []; }
        var branch = root.ForkForReplay();
        Check(branch.CanAct(extra) && !branch.CanAct(teammate),
            "capturing an active native extra turn must retain readiness");
        Check(branch.EndTurn(extra, 2, out var boundary), "captured extra turn failed: " + boundary);
        Check(branch.Hp(teammate.Creature) == 79 && branch.StateText(extra).StartsWith("turn=3;", StringComparison.Ordinal)
            && branch.StateText(teammate).StartsWith("turn=2;", StringComparison.Ordinal),
            "captured extra turn must end only its participant then resume the native shared turn");
    }

    private static void ForcedEndKeepsAutomaticChoices()
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 198, 4));
        var combat = Board([player], "LATEST-VOID-FORM-KNOWLEDGE");
        var enemy = combat.CreateCreature(ModelDb.Monster<KnowledgeDemon>().ToMutable(), CombatSide.Enemy, "knowledge");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(2000); enemy.SetCurrentHpInternal(2000);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["CURSE_OF_KNOWLEDGE_MOVE"], true);
        var voidForm = combat.CreateCard<VoidForm>(player); player.PlayerCombatState!.Hand.AddInternal(voidForm);
        var root = KernelSession.Capture(combat).ForkForReplay();
        var branch = root.CardBranches(voidForm, null, 1).Single();
        Check(branch.Boundary.Length == 0 && branch.State.Power<DisintegrationPower>(player.Creature) > 0,
            "CardBranches must retain automatic enemy choice when Void Form forces the turn to end: " + branch.Boundary);
        Check(branch.State.AutoResolveEnemyChoices && branch.State.AutoResolveTurnStartChoices,
            "plain forks must retain both automatic boundary-choice policies");
        Check(player.PlayerCombatState.Hand.Cards.SequenceEqual([voidForm])
            && !player.Creature.Powers.OfType<DisintegrationPower>().Any(),
            "forced-end choice simulation must leave the live hand and curse untouched");
    }

    // Native FromHand returns all available cards when count <= MinSelect.
    // Once Brand itself left the hand there are zero cards to exhaust; its
    // actual remaining effects are still one unblockable HP loss and Strength.
    private static void BrandWithNoRemainingHand()
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 199, 0));
        var combat = Board([player], "LATEST-BRAND-LAST-CARD"); Enemy(combat, 1);
        var brand = combat.CreateCard<Brand>(player); player.PlayerCombatState!.Hand.AddInternal(brand);
        var root = KernelSession.Capture(combat);
        var branch = root.CardBranches(brand, null, 8).Single(b => b.Boundary.Length == 0);
        Check(branch.Choices is { Count: 1 } && branch.Choices[0].Cards.Count == 0,
            "last-card Brand must record an explicit empty exhaust selection");
        var choice = branch.Choices[0];
        Check(choice.Resolve(player.NetId, [], 1, 1) is { Count: 0 },
            "empty native Brand options must accept the planned empty selection");
        player.PlayerCombatState.Hand.RemoveInternal(brand);
        var play = new CardPlay { Card = brand, Player = player, Target = null,
            ResultPile = PileType.Discard, Resources = default, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        ((Task)AccessTools.Method(typeof(Brand), "OnPlay").Invoke(brand,
            [new ThrowingPlayerChoiceContext(), play])!).GetAwaiter().GetResult();
        Check(player.Creature.CurrentHp == branch.State.Hp(player.Creature)
            && player.Creature.CurrentHp == 79
            && player.Creature.Powers.OfType<StrengthPower>().Single().Amount == branch.State.Power<StrengthPower>(player.Creature)
            && player.PlayerCombatState.ExhaustPile.Cards.Count == 0 && branch.State.Exhaust(player).Count == 0,
            "last-card Brand native and projected HP, Strength and exhaust must match");
    }

    // Ambergris' healing is only half of its native effect. The extra turn
    // skips enemies, advances/draws only the selected seat, and then rejoins
    // the shared party turn. This is also the boundary family used by Pael's Eye.
    private static void ExtraTurnKeepsPartyLifecycle()
    {
        var extra = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 198, 0));
        var teammate = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 198, 1));
        var combat = Board([extra, teammate], "LATEST-EXTRA-SUBSET");
        var enemy = Enemy(combat, 1);
        ModelDb.Power<ToolsOfTheTradePower>().ToMutable().ApplyInternal(extra.Creature, 1, true);
        // The teammate has already played a card on the normal turn. Once the
        // other seat's extra turn resets shared history, the excluded seat must
        // still NOT trigger its unused Pael's Eye from the now-empty history.
        teammate.AddRelicInternal(ModelDb.Relic<PaelsEye>().ToMutable());
        ModelDb.Power<DisintegrationPower>().ToMutable().ApplyInternal(teammate.Creature, 2, true);
        var teammateStrike = combat.CreateCard<StrikeSilent>(teammate);
        teammate.PlayerCombatState!.Hand.AddInternal(teammateStrike);
        for (var i = 0; i < 10; i++)
        {
            extra.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeSilent>(extra));
            teammate.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<DefendIronclad>(teammate));
        }
        var ambergris = ModelDb.Potion<Ambergris>().ToMutable(); extra.AddPotionInternal(ambergris);
        var root = KernelSession.Capture(combat);
        var before = root.PartyStateText();
        var branch = root.ForkForReplay();
        Check(branch.Play(teammateStrike, enemy, out var played), "extra-turn teammate setup: " + played);
        Check(branch.UsePotion(ambergris, extra.Creature, out var boundary), "Ambergris play: " + boundary);
        Check(branch.EndTurn(extra, 3, out boundary) && branch.EndTurn(teammate, 3, out boundary),
            "Ambergris must reach the actual extra turn: " + boundary + " " + branch.LastRoundFailure);
        Check(branch.Hp(extra.Creature) == 80 && branch.Hp(teammate.Creature) == 78
            && branch.CreatureAttacksThisTurnForTesting(enemy) == 0,
            "Ambergris extra turn must skip enemy attacks for the whole party");
        Check(branch.Hand(extra).Count == 5 && branch.Hand(teammate).Count == 0
            && branch.DrawPile(teammate).Count == 10,
            "Ambergris must draw only for the extra-turn participant");
        Check(branch.StateText(extra).StartsWith("turn=2;", StringComparison.Ordinal)
            && branch.StateText(teammate).StartsWith("turn=1;", StringComparison.Ordinal),
            "an extra turn must retain per-seat native turn numbers");
        Check(branch.CanAct(extra) && !branch.CanAct(teammate) && branch.IsReady(teammate)
            && branch.Power<AmbergrisPower>(extra.Creature) == 0,
            "extra-turn sources must be consumed and nonparticipants must remain ready");
        var copied = branch.Fork();
        Check(!copied.CanAct(teammate) && copied.StateText(teammate) == branch.StateText(teammate),
            "forking an extra turn must preserve its participation roster");
        Check(copied.EndTurn(extra, 3, out boundary) && copied.Hp(teammate.Creature) == 77,
            "a fork must not apply a nonparticipant's turn-end damage during the extra turn");
        Check(branch.EndTurn(extra, 3, out boundary),
            "ending the extra turn must return to the shared round: " + boundary + " " + branch.LastRoundFailure);
        Check(branch.Hp(extra.Creature) == 79 && branch.Hp(teammate.Creature) == 77,
            "enemies must attack all living party members after the extra turn ends");
        Check(branch.CreatureAttacksThisTurnForTesting(enemy) == 0,
            "enemy cached this-turn attack history must expire when the shared player side starts");
        Check(branch.StateText(extra).StartsWith("turn=3;", StringComparison.Ordinal)
            && branch.StateText(teammate).StartsWith("turn=2;", StringComparison.Ordinal)
            && branch.Hand(teammate).Count == 5 && branch.CanAct(teammate),
            "normal turn resumption must advance and reactivate the whole party");
        Check(root.PartyStateText() == before && extra.Potions.Contains(ambergris),
            "extra-turn simulation must preserve the live potion and root");
    }

    // Live 2026-10-07: after deaths, R7/R8/R12 retained the dead seat's
    // Y=0/0/3/3 or 1/0/2/2 while native history read Y=0/0/0/0.
    // Native CombatManager.SwitchSides increments ALL PlayerCombatStates.
    // Its history's HappenedThisTurn changes with the shared side/round.
    // Dead-history and dead-turn-number mutation red text is recorded above.
    private static void DeadSeatHistoryAdvances()
    {
        var dead = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 196, 0));
        var survivor = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 196, 1));
        var combat = Board([dead, survivor], "LATEST-DEAD-SEAT");
        dead.Creature.SetCurrentHpInternal(1);
        var strike = combat.CreateCard<StrikeIronclad>(dead);
        dead.PlayerCombatState!.Hand.AddInternal(strike);
        var enemy = Enemy(combat, 1);
        var root = KernelSession.Capture(combat);
        var before = root.PartyStateText();
        var branch = root.ForkForReplay();
        Check(branch.Play(strike, enemy, out var refusal), "dead-seat first card: " + refusal);
        Check(branch.StateText(dead).Contains(";Y=0/0/1/1;", StringComparison.Ordinal),
            "dead-seat fixture must record a card before the enemy kills it");
        Check(branch.EndTurn(dead, 2, out refusal) && branch.EndTurn(survivor, 2, out refusal),
            "dead-seat round did not settle: " + refusal + " " + branch.LastRoundFailure);
        Check(branch.Hp(dead.Creature) == 0 && branch.Hp(survivor.Creature) > 0,
            "dead-seat fixture must cross a shared turn with exactly one survivor");
        Check(branch.StateText(dead).Contains(";Y=0/0/0/0;", StringComparison.Ordinal),
            "dead seat must reset its this-turn card history at the shared round boundary");
        Check(branch.StateText(dead).StartsWith("turn=2;", StringComparison.Ordinal),
            "native SwitchSides also advances the dead seat's player turn number");
        Check(root.PartyStateText() == before && dead.Creature.CurrentHp == 1
            && dead.PlayerCombatState.TurnNumber == 1,
            "advancing dead-seat history must leave the live board and root unchanged");
    }

    // Latest Knowledge Demon H mismatches have five cards on both sides.
    // The old simulator used CardValue for Tools discards; native dispatch used
    // BotBrain.CombatDiscardScore. A six-card setup makes that divergence explicit.
    // This exercises the real power callback and host dispatcher, then compares
    // the ordered hand/draw/discard and Shuffle RNG with the projected next turn.
    // Both predicted choice and real native source dispatch were independently
    // mutated red and restored green, with raw exception text recorded above.
    private static void ToolsOfTradeUsesSharedDiscard(bool junk, bool shuffle = false)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 197, junk ? 1 : 0));
        var combat = Board([player], junk ? "LATEST-TOOLS-JUNK" : "LATEST-TOOLS-COMBAT");
        // A choice scored after the next hand must have the same native
        // energy and enemy action on the projected and live sides. The old
        // intent-only replacement had no native attack callback; its Mock
        // ATTACK id was separately simulated as a one-point hit.
        player.MaxEnergy = 4;
        var enemy = ToolsEnemy(combat, junk);
        var tools = (ToolsOfTheTradePower)ModelDb.Power<ToolsOfTheTradePower>().ToMutable();
        tools.ApplyInternal(player.Creature, 1, true);
        CardModel Make<T>(bool upgrade = false) where T : CardModel
        {
            var card = combat.CreateCard<T>(player);
            if (upgrade) card.UpgradeInternal();
            player.PlayerCombatState!.DrawPile.AddInternal(card);
            return card;
        }
        var cards = junk
            ? new CardModel[] { Make<Dazed>(), Make<Burn>(), Make<Doubt>(),
                Make<Footwork>(), Make<PiercingWail>(true), Make<DefendSilent>(true) }
            : new CardModel[] { Make<Footwork>(), Make<NoxiousFumes>(), Make<PiercingWail>(true),
                Make<DefendSilent>(true), Make<Survivor>(true), Make<StrikeSilent>() };
        if (shuffle)
            foreach (var card in cards.Skip(2))
            {
                player.PlayerCombatState!.DrawPile.RemoveInternal(card);
                player.PlayerCombatState.DiscardPile.AddInternal(card);
            }
        var initialDraw = player.PlayerCombatState!.DrawPile.Cards.ToArray();
        var initialDiscard = player.PlayerCombatState.DiscardPile.Cards.ToArray();
        var root = KernelSession.Capture(combat);
        var before = root.PartyStateText();
        var predicted = root.ForkForReplay();
        Check(predicted.EndTurn(player, 2, out var boundary),
            "Tools setup round did not settle: " + boundary + " " + predicted.LastRoundFailure);
        Check(predicted.Discard(player).Count == 1,
            "Tools prediction must discard exactly one physical card");
        var expectedDiscard = predicted.Discard(player).Single();
        if (junk)
        {
            Check(ReferenceEquals(expectedDiscard, cards[1]),
                "Tools junk hand must discard Burn under the native attack pressure");
            var kept = ToolsNativeCounterfactual(true, expectedDiscard.Id.Entry);
            var lost = ToolsNativeCounterfactual(true, "PIERCING_WAIL");
            Check(kept.StartHp == 71 && kept.Hp == 71 && lost.Hp == 70
                && kept.Dexterity == 2 && lost.Dexterity == 2
                && kept.Block == 10 && lost.Block == 10 && lost.AfterHeldBlock == 8,
                "Tools native junk witness must prevent the one HP lost after Burn consumes block");
        }
        else
        {
            var selected = ToolsNativeCounterfactual(false, expectedDiscard.Id.Entry);
            var oldStrike = ToolsNativeCounterfactual(false, "STRIKE_SILENT");
            Check(selected.StartHp == 79 && selected.Hp == oldStrike.Hp && selected.Hp == 79
                && selected.EnemyHp == oldStrike.EnemyHp - 6
                && selected.Dexterity == 2 && selected.NoxiousFumes == 2
                && oldStrike.Dexterity == 2 && oldStrike.NoxiousFumes == 2,
                "Tools native chosen discard must preserve HP and both engines while enabling six more damage than discarding Strike");
        }
        Check(root.PartyStateText() == before && player.PlayerCombatState!.DrawPile.Cards.SequenceEqual(initialDraw)
            && player.PlayerCombatState.DiscardPile.Cards.SequenceEqual(initialDiscard),
            "Tools projection must not draw or discard on the live board");

        // Execute the preceding empty-hand attack, rather than copying the
        // predicted HP. Then advance the same native round, energy and hand.
        enemy.Monster!.NextMove.PerformMove([player.Creature]).GetAwaiter().GetResult();
        combat.RoundNumber++;
        player.PlayerCombatState!.IncrementTurnNumber();
        player.PlayerCombatState.Phase = PlayerTurnPhase.Start;
        player.PlayerCombatState.LoseEnergy(player.PlayerCombatState.Energy);
        player.PlayerCombatState.GainEnergy(player.MaxEnergy);
        Check(player.Creature.CurrentHp == predicted.Hp(player.Creature)
            && player.PlayerCombatState.Energy == predicted.Energy(player)
            && enemy.CurrentHp == predicted.Hp(enemy)
            && (enemy.GetPower<StrengthPower>()?.Amount ?? 0) == predicted.Power<StrengthPower>(enemy)
            && tools.Amount == predicted.Power<ToolsOfTheTradePower>(player.Creature)
            && enemy.Monster.NextMove.Id == "ATTACK",
            "Tools live and predicted native menu boundaries must have equal HP, energy, powers and enemy move");
        var context = new ThrowingPlayerChoiceContext();
        var drawn = CardPileCmd.Draw(context, tools.ModifyHandDraw(player, 5), player, fromHandDraw: true)
            .GetAwaiter().GetResult().ToArray();
        Check(drawn.Length == 6 && drawn.Take(initialDraw.Length).SequenceEqual(initialDraw)
            && cards.All(drawn.Contains), "native Tools must draw all six physical cards and preserve the initial draw order");
        // Exercise the production source classifier before executing the native
        // callback. A generic discard without Tools as source stays on its own path.
        var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 1);
        var command = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard));
        Check(BotCardChoiceDispatcher.TrySelect(command,
            [context, player, prefs, null!, tools], out var routed)
            && ((IEnumerable<CardModel>)routed!).SequenceEqual([expectedDiscard]),
            "native Tools source must use the same combat discard policy as the projection");
        Check(BotRelicChoices.Select(player, cards, 1, 1, nameof(CardSelectCmd.FromHandForDiscard)) is null,
            "a generic hand discard must not be misclassified as a Tools turn-start choice");
        tools.AfterPlayerTurnStart(context, player).GetAwaiter().GetResult();
        Check(player.PlayerCombatState.Hand.Cards.SequenceEqual(predicted.Hand(player))
            && player.PlayerCombatState.DiscardPile.Cards.SequenceEqual(predicted.Discard(player))
            && player.PlayerCombatState.DrawPile.Cards.SequenceEqual(predicted.DrawPile(player)),
            "native Tools callback and next-turn projection must leave identical ordered piles");
        static string Shuffle(string text) => text.Split(';').First(f => f.StartsWith("R=", StringComparison.Ordinal)).Split('/')[0];
        Check(Shuffle(KernelSession.CaptureLiveStateText(combat, player)) == Shuffle(predicted.StateText(player)),
            "native Tools discard and projection must consume identical Shuffle RNG");
        if (shuffle)
            Check(Shuffle(predicted.StateText(player)) != Shuffle(before),
                "the short-draw Tools variant must exercise a real discard-pile shuffle");
    }

    private static Creature ToolsEnemy(CombatState combat, bool junk)
    {
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "attack");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(2000); enemy.SetCurrentHpInternal(2000);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        // Real native permanent Strength changes both the actual attack and
        // its projection. No artificial intent replaces the monster's move.
        if (junk) ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 8, true);
        return enemy;
    }

    private sealed record ToolsWitness(decimal StartHp, decimal Hp, decimal EnemyHp,
        decimal Block, decimal AfterHeldBlock, decimal Dexterity, decimal NoxiousFumes);

    // Independent native witness, without ActionValue or the cleanup scorer.
    // These six ordinary unenchanted cards have no play-count/after-play hooks:
    // validate and pay native resources, execute their native OnPlay effects,
    // then actual held-card damage and MockAttackMonster.PerformMove. Survivor
    // sees at most one remaining candidate when it is affordable in this order.
    private static ToolsWitness ToolsNativeCounterfactual(bool junk, string discarded)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all,
            junk ? 76561198999999198UL : 76561198999999197UL);
        var combat = Board([player], junk ? "LATEST-TOOLS-JUNK-WITNESS" : "LATEST-TOOLS-COMBAT-WITNESS");
        player.MaxEnergy = 4;
        var enemy = ToolsEnemy(combat, junk);
        // Human native witness owns its selector; no ambient bot patch or synchronizer.
        var selector = new TestCardSelector();
        using var nativeSelector = CardSelectCmd.PushSelector(selector);
        Check(ReferenceEquals(CardSelectCmd.Selector, selector),
            "independent Tools native witness must own its scoped selector input");
        var tools = ModelDb.Power<ToolsOfTheTradePower>().ToMutable();
        tools.ApplyInternal(player.Creature, 1, true);
        CardModel Make<T>(bool upgrade = false) where T : CardModel
        {
            var card = combat.CreateCard<T>(player);
            if (upgrade) card.UpgradeInternal();
            player.PlayerCombatState!.DrawPile.AddInternal(card);
            return card;
        }
        var cards = junk
            ? new CardModel[] { Make<Dazed>(), Make<Burn>(), Make<Doubt>(), Make<Footwork>(),
                Make<PiercingWail>(true), Make<DefendSilent>(true) }
            : new CardModel[] { Make<Footwork>(), Make<NoxiousFumes>(), Make<PiercingWail>(true),
                Make<DefendSilent>(true), Make<Survivor>(true), Make<StrikeSilent>() };
        enemy.Monster!.NextMove.PerformMove([player.Creature]).GetAwaiter().GetResult();
        var startHp = player.Creature.CurrentHp;
        combat.RoundNumber++; player.PlayerCombatState!.IncrementTurnNumber();
        player.PlayerCombatState.Phase = PlayerTurnPhase.Start;
        player.PlayerCombatState.LoseEnergy(player.PlayerCombatState.Energy);
        player.PlayerCombatState.GainEnergy(player.MaxEnergy);
        var context = new ThrowingPlayerChoiceContext();
        CardPileCmd.Draw(context, tools.ModifyHandDraw(player, 5), player, fromHandDraw: true).GetAwaiter().GetResult();
        CardCmd.Discard(context, cards.Single(c => c.Id.Entry == discarded)).GetAwaiter().GetResult();
        player.PlayerCombatState.Phase = PlayerTurnPhase.Play;
        var sequence = new List<string>();
        foreach (var id in new[] { "FOOTWORK", "NOXIOUS_FUMES", "STRIKE_SILENT", "DEFEND_SILENT", "SURVIVOR", "PIERCING_WAIL" })
        {
            var card = player.PlayerCombatState.Hand.Cards.SingleOrDefault(c => c.Id.Entry == id);
            if (card is null || !card.CanPlay()) continue;
            Creature? target = card.TargetType == TargetType.AnyEnemy ? enemy : null;
            Check(card.IsValidTarget(target), "Tools native witness must use a legal target: " + id);
            var energy = player.PlayerCombatState.Energy;
            var spent = card.SpendResources().GetAwaiter().GetResult();
            Check(spent.Item1 == 1 && player.PlayerCombatState.Energy == energy - 1,
                "Tools native witness must pay each card's actual one energy: " + id);
            player.PlayerCombatState.Hand.RemoveInternal(card);
            player.PlayerCombatState.PlayPile.AddInternal(card);
            var result = card.Type == CardType.Power ? PileType.None
                : card.Keywords.Contains(CardKeyword.Exhaust) ? PileType.Exhaust : PileType.Discard;
            var play = new CardPlay { Card = card, Player = player, Target = target, ResultPile = result,
                Resources = new ResourceInfo { EnergySpent = spent.Item1, EnergyValue = spent.Item1,
                    StarsSpent = spent.Item2, StarValue = spent.Item2 },
                IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            if (card is Survivor)
                Check(player.PlayerCombatState.Hand.Cards.Count <= 1,
                    "Tools native Survivor witness must use only its real implicit-all remaining menu");
            ((Task)AccessTools.Method(card.GetType(), "OnPlay").Invoke(card, [context, play])!).GetAwaiter().GetResult();
            player.PlayerCombatState.PlayPile.RemoveInternal(card);
            if (result == PileType.Discard) player.PlayerCombatState.DiscardPile.AddInternal(card);
            else if (result == PileType.Exhaust) player.PlayerCombatState.ExhaustPile.AddInternal(card);
            sequence.Add(id + ":" + energy + "->" + player.PlayerCombatState.Energy);
        }
        var block = player.Creature.Block;
        foreach (var held in player.PlayerCombatState.Hand.Cards.ToArray())
            if (held.HasTurnEndInHandEffect) held.OnTurnEndInHandWrapper(context).GetAwaiter().GetResult();
        var afterHeld = player.Creature.Block;
        enemy.Monster.NextMove.PerformMove([player.Creature]).GetAwaiter().GetResult();
        var witness = new ToolsWitness(startHp, player.Creature.CurrentHp, enemy.CurrentHp, block, afterHeld,
            player.Creature.GetPower<DexterityPower>()?.Amount ?? 0,
            player.Creature.GetPower<NoxiousFumesPower>()?.Amount ?? 0);
        Console.WriteLine($"Tools native witness junk={junk} discard={discarded} result={witness} costs={string.Join('|', sequence)}");
        return witness;
    }

    private static void ReplayRingingPublishesExecutableKeys()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 199, 1));
        var c = Board([p], "LATEST-RINGING-REPLAY-SUFFIX");
        var beast = c.CreateCreature(ModelDb.Monster<CeremonialBeast>().ToMutable(), CombatSide.Enemy, "beast");
        c.AddCreature(beast); beast.Monster!.SetUpForCombat();
        beast.SetMaxHpInternal(2000); beast.SetCurrentHpInternal(8);
        beast.Monster.SetMoveImmediate((MoveState)beast.Monster.MoveStateMachine!.States["BEAST_CRY_MOVE"], true);
        var first = c.CreateCard<Bash>(p); var second = c.CreateCard<Bash>(p);
        p.PlayerCombatState!.DrawPile.AddInternal(first); p.PlayerCombatState.DrawPile.AddInternal(second);
        var root = KernelSession.Capture(c);
        var attack = new KernelTeamSearch.Action(p, second, beast,
            CardStateKey: root.CardStateKey(second), CardStateOccurrence: 1);
        var script = TurnStartFixtureCapture.Record(root, new KernelTeamSearch.Action[] { new(p, null, null, EndTurn: true), attack }, maxRounds: 3);
        var candidate = new TournamentResult(script[0], null, [], 0, 0, "ringing-original-key") { Script = script };
        var validation = new KernelScriptReplayRun(root, [p], candidate, 3);
        while (!validation.Done) validation.Advance(TimeSpan.FromMilliseconds(1));
        Check(validation.Result?.Record is { Verified: true }, "Ringing suffix must replay to a real terminal: " + validation.Failure);
        var verified = validation.Result!.Script![1];
        Check(verified.CardStateKey.Contains("RINGING", StringComparison.Ordinal)
            && verified.CardStateOccurrence == 1,
            "replayed Ringing suffix must publish the branch's pre-play affliction key and second-card occurrence");

        var refinement = new KernelScriptRefinementRun(root, [p], [p], script, new(ReserveMs: 0), 5000);
        for (var i = 0; refinement.Baseline is null && i < 20; i++) refinement.Advance(TimeSpan.Zero);
        refinement.FinishAtBoundary();
        Check(refinement.Result?.Script is { Count: 1 }, "Ringing refinement must publish the next-turn suffix");
        var adopted = refinement.Result!.Script![0];
        Check(adopted.CardStateKey == verified.CardStateKey && adopted.CardStateOccurrence == 1,
            "refinement suffix must retain the replay's execution-time Ringing identity");

        // Execute the real native affliction callback and real draw. The live
        // resolver is checked BEFORE the selected card leaves this hand.
        var ringing = (RingingPower)ModelDb.Power<RingingPower>().ToMutable();
        ringing.ApplyInternal(p.Creature, 1, true);
        ringing.AfterApplied(beast, null).GetAwaiter().GetResult();
        CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 2, p).GetAwaiter().GetResult();
        Check(first.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Ringing
            && second.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Ringing,
            "native Ringing must afflict both same-state Bash cards before deployment");
        var live = KernelSession.ResolvePlannedCard(p, adopted.Card, adopted.CardStateKey,
            adopted.CardStateOccurrence, out var refusal);
        Check(ReferenceEquals(live, second) && refusal.Length == 0,
            "published Ringing occurrence must deploy the second native card before execution: " + refusal);
        var liveBranch = KernelSession.Capture(c).ForkForReplay();
        Check(liveBranch.ReplayPlanned(adopted, 3, out var reason), "published Ringing action failed: " + reason);
        Check(liveBranch.Hand(p).SequenceEqual([first]),
            "exactly the selected second Bash must leave the hand in the legal first Ringing play");
    }

    private static void RefinementBoundaryPowersStayFrozen()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 199, 2));
        var c = Board([p], "LATEST-PARENT-WRITES-AFTER-FORK"); var e = QualifiedRefinementEnemy(c);
        var first = c.CreateCard<Inflame>(p); var second = c.CreateCard<Inflame>(p);
        p.PlayerCombatState!.Hand.AddInternal(first); p.PlayerCombatState.DrawPile.AddInternal(second);
        for (var i = 0; i < 4; i++) p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<DefendIronclad>(p));
        var root = KernelSession.Capture(c);
        var script = new KernelTeamSearch.Action[] {
            new(p, first, null, CardStateKey: root.CardStateKey(first)),
            new(p, null, null, EndTurn: true),
            new(p, second, null, CardStateKey: root.CardStateKey(second)),
        };
        script = TurnStartFixtureCapture.Record(root, script);
        var refinement = new KernelScriptRefinementRun(root, [p], [p], script, new(ReserveMs: 0), 5000);
        for (var i = 0; refinement.BoundaryState is null && i < 20; i++) refinement.Advance(TimeSpan.Zero);
        Check(refinement.BoundaryState is not null, "Inflame fixture must cross a real round boundary");
        var frozen = refinement.BoundaryState!; var text = frozen.PartyStateText();
        Check(frozen.Power<StrengthPower>(p.Creature) == 2, "frozen boundary must start with first Inflame's Strength2");
        for (var i = 0; refinement.Baseline is null && i < 20; i++) refinement.Advance(TimeSpan.Zero);
        refinement.FinishAtBoundary();
        var parent = (KernelSession)AccessTools.Field(typeof(KernelScriptRefinementRun), "replay").GetValue(refinement)!;
        Check(parent.Power<StrengthPower>(p.Creature) == 4, "baseline must execute the actual second Inflame on its replay parent");
        Check(frozen.Power<StrengthPower>(p.Creature) == 2 && frozen.PartyStateText() == text,
            "refinement boundary must remain frozen when its replay parent plays another Inflame");

    }

    private static void ParallelForkPowersStayFrozen()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 199, 8));
        var c = Board([p], "LATEST-PARALLEL-READONLY-POWER-FORKS"); Enemy(c, 1);
        var first = c.CreateCard<Inflame>(p); var second = c.CreateCard<Inflame>(p);
        p.PlayerCombatState!.Hand.AddInternal(first); p.PlayerCombatState.DrawPile.AddInternal(second);
        for (var i = 0; i < 4; i++) p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<DefendIronclad>(p));
        var mutable = KernelSession.Capture(c).ForkForReplay();
        Check(mutable.Play(first, null, out var reason), "parallel fork parent first Inflame failed: " + reason);
        // Read-only Fork can be called in parallel by tournament workers. Sharing
        // registration must not mutate a non-thread-safe marker set on this source.
        var forks = new KernelSession[64];
        Parallel.For(0, forks.Length, i => forks[i] = mutable.Fork());
        Check(mutable.EndTurn(p, 3, out reason) && mutable.Play(second, null, out reason),
            "parallel fork parent second Inflame failed: " + reason);
        Check(forks.All(f => f.Power<StrengthPower>(p.Creature) == 2),
            "parallel read-only forks must all retain the parent's original Strength after its later write");
    }

    private static void ReplayRingingPublishesCurrentChoiceTokens()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 199, 6));
        var c = Board([p], "LATEST-RINGING-REPLAY-CHOICES");
        var beast = c.CreateCreature(ModelDb.Monster<CeremonialBeast>().ToMutable(), CombatSide.Enemy, "beast");
        c.AddCreature(beast); beast.Monster!.SetUpForCombat();
        beast.SetMaxHpInternal(2000); beast.SetCurrentHpInternal(2000);
        beast.Monster.SetMoveImmediate((MoveState)beast.Monster.MoveStateMachine!.States["BEAST_CRY_MOVE"], true);
        var first = c.CreateCard<Survivor>(p); var firstDiscard = c.CreateCard<StrikeSilent>(p);
        var second = c.CreateCard<Survivor>(p); var secondDiscard = c.CreateCard<StrikeSilent>(p);
        p.PlayerCombatState!.Hand.AddInternal(first); p.PlayerCombatState.Hand.AddInternal(firstDiscard);
        p.PlayerCombatState.DrawPile.AddInternal(second); p.PlayerCombatState.DrawPile.AddInternal(secondDiscard);
        var root = KernelSession.Capture(c);
        var originalChoice = root.CardBranches(first, null, 1).Single();
        Check(originalChoice.Choices is { Count: 1 }, "first real Survivor must supply one pre-Ringing discard token");
        var firstAction = new KernelTeamSearch.Action(p, first, null, Choices: originalChoice.Choices,
            ChoiceOrdinals: originalChoice.ChoiceOrdinals, CardStateKey: root.CardStateKey(first));
        var secondAction = firstAction with { Card = second, CardStateKey = root.CardStateKey(second) };
        var script = TurnStartFixtureCapture.Record(root, new KernelTeamSearch.Action[] { firstAction, new(p, null, null, EndTurn: true), secondAction }, maxRounds: 3);
        var candidate = new TournamentResult(firstAction, null, [], 0, 0, "ringing-old-choice-tokens") { Script = script };
        var validation = new KernelScriptReplayRun(root, [p], candidate, 3);
        while (!validation.Done) validation.Advance(TimeSpan.FromMilliseconds(1));
        Check(validation.Result?.Script is { Count: 3 }, "Ringing choice script must replay: " + validation.Failure);
        var final = validation.Result!.Script![2];
        Check(final.Choices is { Count: 1 } && final.Choices[0].Cards is { Count: 1 }
            && final.Choices[0].Cards[0].StateKey.Contains("RINGING", StringComparison.Ordinal),
            "replayed Ringing Survivor must publish current discard tokens without earlier action choices");
        var refinement = new KernelScriptRefinementRun(root, [p], [p], script, new(ReserveMs: 0), 5000);
        for (var i = 0; refinement.Baseline is null && i < 20; i++) refinement.Advance(TimeSpan.Zero);
        refinement.FinishAtBoundary();
        var suffix = refinement.Result!.Script!.Single();
        Check(suffix.Choices is { Count: 1 } && suffix.Choices[0].Cards[0].StateKey == final.Choices![0].Cards[0].StateKey,
            "refinement must publish only its current Survivor choice tokens");
        var ringing = (RingingPower)ModelDb.Power<RingingPower>().ToMutable();
        ringing.ApplyInternal(p.Creature, 1, true); ringing.AfterApplied(beast, null).GetAwaiter().GetResult();
        // Reconstruct the actual menu using native pile commands. The first
        // Survivor selected firstDiscard, then itself entered Discard; native
        // draw includes both next-turn cards and the real shuffled discard tail.
        // This tests the published card-menu identity, not a full combat replay.
        using var nativeSelector = CardSelectCmd.PushSelector(new TestCardSelector());
        var nativeContext = new ThrowingPlayerChoiceContext();
        CardCmd.Discard(nativeContext, firstDiscard).GetAwaiter().GetResult();
        CardCmd.Discard(nativeContext, first).GetAwaiter().GetResult();
        CardPileCmd.Draw(nativeContext, 5, p, fromHandDraw: true).GetAwaiter().GetResult();
        Check(p.PlayerCombatState.Hand.Cards.Contains(second),
            "native Ringing draw must retain the actual next Survivor instance");
        p.PlayerCombatState.Hand.RemoveInternal(second); p.PlayerCombatState.PlayPile.AddInternal(second);
        var actualMenu = p.PlayerCombatState.Hand.Cards.ToArray();
        Check(suffix.Choices![0].Options!.SequenceEqual(KernelChoice.MenuTokens(actualMenu)),
            "published Ringing menu must match native ordered draw and shuffle candidates");
        Check(ReferenceEquals(suffix.Choices[0].ResolveMenu(p.NetId, actualMenu, 1, 1, second.Id.Entry)?.Single(), secondDiscard),
            "the published Ringing discard token must resolve the actual native-afflicted option");
        Check(suffix.Choices[0].ResolveMenu(p.NetId, actualMenu.Where(card => !ReferenceEquals(card, first)).ToArray(),
                1, 1, second.Id.Entry) is null,
            "published Ringing menu must reject a missing unselected native option");
    }

    private static void DeadPaelsEyeKeepsNativeInactiveState()
    {
        foreach (var extra in new[] { false, true })
        {
            var actor = Player.CreateForNewRun<Deprived>(UnlockState.all,
                BotRegistry.CreateId(BotDifficulty.Pro, 199, 3));
            var dead = Player.CreateForNewRun<Deprived>(UnlockState.all,
                BotRegistry.CreateId(BotDifficulty.Pro, 199, 4));
            var c = Board([actor, dead], "LATEST-DEAD-PAELS-" + extra); Enemy(c, 1);
            var eye = (PaelsEye)ModelDb.Relic<PaelsEye>().ToMutable(); dead.AddRelicInternal(eye);
            AccessTools.Field(typeof(PaelsEye), "_wasOwnerPartOfLastPlayerTurn").SetValue(eye, extra);
            dead.Creature.SetCurrentHpInternal(0); dead.DeactivateHooks();
            if (extra) ModelDb.Power<AmbergrisPower>().ToMutable().ApplyInternal(actor.Creature, 1, true);
            var root = KernelSession.Capture(c).ForkForReplay();
            Check(!c.IterateHookListeners().Contains(eye), "native dead Paels Eye must be absent from active hook listeners");
            var nativeParticipants = extra ? new[] { actor.Creature } : c.Allies.ToArray();
            foreach (var listener in c.IterateHookListeners().ToArray())
                listener.AfterSideTurnStart(CombatSide.Player, nativeParticipants, c).GetAwaiter().GetResult();
            Check((bool)AccessTools.Field(typeof(PaelsEye), "_wasOwnerPartOfLastPlayerTurn").GetValue(eye)! == extra,
                "native side start must preserve an inactive dead Paels Eye's previous participation");
            Check(root.EndTurn(actor, 3, out var reason), "dead Paels transition failed: " + reason);
            var marker = "PAELS_EYE/0/" + (extra ? 1 : 0);
            Check(root.StateText(dead).Contains(marker, StringComparison.Ordinal),
                "dead Paels Eye must keep its native previous participation during normal and extra side starts");
        }
    }

    private static void RefinementBoundaryPlatingStaysFrozen()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 199, 5));
        var c = Board([p], "LATEST-PARENT-PLATING-AFTER-FORK"); QualifiedRefinementEnemy(c);
        ModelDb.Power<PlatingPower>().ToMutable().ApplyInternal(p.Creature, 3, true);
        var script = new KernelTeamSearch.Action[] {
            new(p, null, null, EndTurn: true), new(p, null, null, EndTurn: true),
        };
        var root = KernelSession.Capture(c);
        script = TurnStartFixtureCapture.Record(root, script);
        var refinement = new KernelScriptRefinementRun(root, [p], [p], script,
            new(ReserveMs: 0), 5000);
        for (var i = 0; refinement.BoundaryState is null && i < 20; i++) refinement.Advance(TimeSpan.Zero);
        var boundary = refinement.BoundaryState!;
        Check(boundary is not null && boundary.Hp(p.Creature) == 80
            && boundary.Power<PlatingPower>(p.Creature) == 2,
            "first actual side start must leave the frozen boundary with HP80 and Plating2");
        var text = boundary!.PartyStateText();
        for (var i = 0; refinement.Baseline is null && i < 20; i++) refinement.Advance(TimeSpan.Zero);
        refinement.FinishAtBoundary();
        var parent = (KernelSession)AccessTools.Field(typeof(KernelScriptRefinementRun), "replay").GetValue(refinement)!;
        Check(parent.Hp(p.Creature) == 80 && parent.Power<PlatingPower>(p.Creature) == 1,
            "second actual side start must mutate the baseline parent to HP80 and Plating1");
        Check(boundary.Power<PlatingPower>(p.Creature) == 2 && boundary.PartyStateText() == text,
            "refinement boundary must retain Plating2 when the later side start reduces its parent's Plating");

        // The ownership seam accepts the original native source reference as
        // well as the current mirrored instance. Once the parent has its own
        // mutable clone, a lookup by that native alias must still detach it.
        p = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 199, 7));
        c = Board([p], "LATEST-PARENT-PLATING-NATIVE-ALIAS"); Enemy(c, 1);
        var native = (PlatingPower)ModelDb.Power<PlatingPower>().ToMutable();
        native.ApplyInternal(p.Creature, 3, true);
        parent = KernelSession.Capture(c).Fork();
        Check(parent.EndTurn(p, 3, out var reason) && parent.Power<PlatingPower>(p.Creature) == 2,
            "native alias parent must first own a Plating2 mirror: " + reason);
        boundary = parent.Fork();
        var state = AccessTools.Property(typeof(KernelSession), "Combat").GetValue(parent)!;
        AccessTools.Method(state.GetType(), "SetPowerAmount").Invoke(state, [native, 1]);
        Check(parent.Power<PlatingPower>(p.Creature) == 1 && native.Amount == 3,
            "source alias write must affect only the simulated parent");
        Check(boundary.Power<PlatingPower>(p.Creature) == 2,
            "native source alias must not bypass power ownership and mutate its frozen sibling");
    }

    private static CombatState Board(IReadOnlyList<Player> players, string seed)
    {
        var combat = new CombatState(runState: RunState.CreateForTest(players, seed: seed));
        foreach (var player in players)
        {
            player.ResetCombatState(); combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3);
        }
        return combat;
    }

    // A reusable frozen-boundary fixture uses an audited native Bite, not
    // the deliberately unqualified generic Mock attack. Existing COW assertions
    // and the actual one-damage/Plating arithmetic remain unchanged.
    private static Creature QualifiedRefinementEnemy(CombatState combat)
    {
        var e= combat.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.BowlbugEgg>().ToMutable(), CombatSide.Enemy, "egg");
        combat.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(2000);e.SetCurrentHpInternal(2000);
        e.Monster.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)e.Monster.MoveStateMachine!.States["BITE_MOVE"],true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,-6,true);
        return e;
    }

    private static Creature Enemy(CombatState combat, int damage, int repeats = 1)
    {
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "attack");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(2000); enemy.SetCurrentHpInternal(2000);
        var move = new MoveState("ATTACK", _ => Task.CompletedTask,
            repeats == 1 ? new SingleAttackIntent(damage) : new MultiAttackIntent(damage, repeats));
        move.FollowUpState = move;
        enemy.Monster.SetMoveImmediate(move, true);
        return enemy;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Latest projection: " + message);
    }
}
