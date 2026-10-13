using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using CoopBots.Kernel;
using CoopBots;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Private constructed native control; actual baseline command17 reproduced the
// production GeneratedFollowupValue -> AttackCosts null lookup after TheBall.
// Uses current native card bodies and existing accepted PatchSmoke host scopes.
// It does not reconstruct historical F14/F26/F31 full Facts, physical tokens or RNG.
// R2 lookup: restore old single-owner FindCard -> actual native RED:
// "System.NullReferenceException" at GetEnergyCostWithModifiers -> AttackCosts -> GeneratedFollowupValue.
// R2 acting-owner: use source.Owner before play -> actual native RED:
// "System.InvalidOperationException: Post-play win check must retain preplay acting owner after TheBall transfers again".
// Both restored source1128/artifact12 exactly, including forced rebuild; native GREEN.
internal static class GeneratedAttackCostScenarios
{
    internal static void Run(string mode = "generators")
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.generated-cost-existing-presentation");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        presentation.Patch(format, prefix: new HarmonyMethod(typeof(KernelPlanningStateScenarios), "Format"));
        try
        {
            if (mode == "high6") { High6(); return; }
            if (mode == "the-ball") { TheBallTransfer(); return; }
            if (mode == "high6-batch")
            {
                for (var repeat = 0; repeat < 2; repeat++)
                { High6(fieldPowers: false, repeat); High6(fieldPowers: true, repeat); }
                return;
            }
            Probe<CloakAndDagger>(upgrade: true, expectedShivs: 2);
            Probe<BladeDance>(upgrade: true, expectedShivs: 4);
            Probe<Anger>(upgrade: false, expectedShivs: 0);
            TheBallTransfer();
        }
        finally { presentation.Unpatch(format, HarmonyPatchType.All, presentation.Id); }
    }

    private static void Probe<T>(bool upgrade, int expectedShivs) where T : CardModel
    {
        var name = typeof(T).Name + (upgrade ? "+" : "");
        var (combat, party, enemy, generator) = Board<T>(upgrade);
        using (Native(combat))
        {
            var before = KernelSession.Capture(combat);
            var after = before.CardBranches(generator, generator.Type == CardType.Attack ? enemy : null, 1)
                .FirstOrDefault(b => b.Boundary.Length == 0)?.State
                ?? throw new InvalidOperationException("SETUP_FAILURE generator did not settle: " + name);
            var owner = party[1];
            if (expectedShivs > 0)
                Require(after.Hand(owner).OfType<Shiv>().Count() == expectedShivs,
                    "SETUP_FAILURE expected generated Shiv count: " + name);
            else
                Require(after.Discard(owner).OfType<Anger>().Count() == 2,
                    "SETUP_FAILURE Anger original and generated copy must enter discard");

            Inspect(name + "/settled", before, after, party);
            var child = after.Fork();
            var sibling = after.Fork();
            var grandchild = child.Fork();
            Inspect(name + "/child", before, child, party);
            Inspect(name + "/sibling", before, sibling, party);
            Inspect(name + "/grandchild", before, grandchild, party);

            // Resolve another actual hand card on one fork, leaving the siblings alone.
            var attack = grandchild.Hand(owner).FirstOrDefault(c => c.Type == CardType.Attack && grandchild.CanPlay(c));
            if (attack is not null)
            {
                var target = grandchild.Targets(attack).FirstOrDefault();
                var next = grandchild.CardBranches(attack, target, 1).FirstOrDefault(b => b.Boundary.Length == 0)?.State
                    ?? throw new InvalidOperationException("SETUP_FAILURE generated attack did not settle: " + name);
                Inspect(name + "/second-hand-branch", grandchild, next, party);
                Inspect(name + "/sibling-after-other-play", before, sibling, party);
            }
            if (typeof(T) == typeof(Anger))
            {
                var next = grandchild.Fork();
                foreach (var player in party)
                    Require(next.EndTurn(player, 80, out var boundary), "SETUP_FAILURE next turn: " + boundary + " " + next.LastRoundFailure);
                Require(next.Hand(owner).OfType<Anger>().Count() == 2,
                    "SETUP_FAILURE actual simulation draw must reach both Anger instances");
                Inspect(name + "/next-draw", before, next, party);
                var copied = next.Hand(owner).OfType<Anger>().First(c => !ReferenceEquals(c, generator));
                var branch = next.CardBranches(copied, enemy, 1).FirstOrDefault(b => b.Boundary.Length == 0)?.State
                    ?? throw new InvalidOperationException("SETUP_FAILURE redrawn generated Anger did not settle");
                Inspect(name + "/copied-anger-play", next, branch, party);
            }
        }

        // Independent native paid-card control; no simulation result is copied to it.
        var (nativeCombat, nativeParty, nativeEnemy, nativeCard) = Board<T>(upgrade);
        using (Native(nativeCombat))
        {
            var paid = nativeCard.SpendResources().GetAwaiter().GetResult();
            nativeCard.OnPlayWrapper(new ThrowingPlayerChoiceContext(),
                nativeCard.Type == CardType.Attack ? nativeEnemy : null, false,
                new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                    StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
            var state = nativeParty[1].PlayerCombatState!;
            Console.WriteLine("GENERATED_COST_NATIVE " + JsonSerializer.Serialize(new {
                name, paidEnergy = paid.Item1, paidStars = paid.Item2,
                shivs = state.Hand.Cards.OfType<Shiv>().Count(),
                angerDiscard = state.DiscardPile.Cards.OfType<Anger>().Count(),
                energy = state.Energy, enemyHp = nativeEnemy.CurrentHp
            }));
            Require(expectedShivs > 0 ? state.Hand.Cards.OfType<Shiv>().Count() == expectedShivs
                : state.DiscardPile.Cards.OfType<Anger>().Count() == 2,
                "SETUP_FAILURE independent native generator result: " + name);
        }
        Console.WriteLine("PASS generated-attack-cost-probe " + name);
    }

    private static void Inspect(string label, KernelSession before, KernelSession after, Player[] party)
    {
        var sim = (CombatPredictionSimulator)AccessTools.Field(typeof(KernelSession), "simulator").GetValue(after)!;
        var held = party.SelectMany(before.Hand).ToHashSet();
        foreach (var actor in party)
        foreach (var source in after.Hand(actor).Where(c => c.Type == CardType.Attack))
        {
            var seat = sim.State.GetPlayerCombatState(actor);
            var hand = seat.Hand.Cards.FirstOrDefault(c => c.References(source));
            var all = seat.AllCards.FirstOrDefault(c => c.References(source));
            var found = sim.State.FindCard(source);
            Console.WriteLine("GENERATED_COST_LOOKUP " + JsonSerializer.Serialize(new {
                label, card = source.Id.ToString(), actor = actor.NetId, originalOwner = source.Owner.NetId,
                originalOwnerIsActor = ReferenceEquals(source.Owner, actor), generatedSinceBefore = !held.Contains(source),
                handReferenceFound = hand is not null, allCardsReferenceFound = all is not null,
                ownerFindCardFound = found is not null, handMatchesAll = ReferenceEquals(hand, all),
                handMatchesFound = ReferenceEquals(hand, found), previewOwner = hand?.Preview.Owner.NetId,
                previewOwnerIsActor = hand is not null && ReferenceEquals(hand.Preview.Owner, actor),
                previewEnergyCostPresent = hand?.Preview.EnergyCost is not null
            }));
            // Invoke the unmodified production lookup and cost path after diagnostic output.
            // Missing lookup / EnergyCost produces the real baseline exception, not a catch or skip.
        }
        // This is the exact failing production entry, including its first generated LINQ filter.
        var payoff = RolloutRun.GeneratedFollowupValue(before, after, party, party);
        Console.WriteLine($"GENERATED_COST_PAYOFF {label} value={payoff:R}");
        foreach (var source in party.SelectMany(after.Hand).Where(c => c.Type == CardType.Attack))
        {
            var cost = after.AttackCosts(source);
            Console.WriteLine($"GENERATED_COST_RESULT {label} {source.Id} energy={cost.Energy} stars={cost.Stars}");
        }
    }

    private static (CombatState Combat, Player[] Party, Creature Enemy, T Card) Board<T>(bool upgrade) where T : CardModel
    {
        var party = Enumerable.Range(1, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)i)).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "GENERATED-COST-" + typeof(T).Name)) { RoundNumber = 1 };
        foreach (var player in party)
        {
            player.ResetCombatState(); combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        }
        var card = combat.CreateCard<T>(party[1]);
        if (upgrade) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
        var ownerState = party[1].PlayerCombatState!;
        ownerState.Hand.AddInternal(card);
        for (var n = 0; n < 3; n++)
            ownerState.DrawPile.AddInternal(combat.CreateCard<DefendIronclad>(party[1]));
        var model = ModelDb.Monster<Axebot>().ToMutable();
        var enemy = combat.CreateCreature(model, CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        model.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)
            model.MoveStateMachine!.States["BOOT_UP_MOVE"], true);
        enemy.SetMaxHpInternal(1000); enemy.SetCurrentHpInternal(1000);
        return (combat, party, enemy, card);
    }

    private static IDisposable Native(CombatState combat)
        => (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static int missingLookup;
    private static int missingEnergyCost;
    private static void LookupObserved(PredictedCard? predicted, KernelSession session, CardModel source)
    {
        if (predicted is not null && predicted.Preview.EnergyCost is not null) return;
        if (predicted is null) Interlocked.Increment(ref missingLookup);
        else Interlocked.Increment(ref missingEnergyCost);
        var state = session.Simulator.State;
        Console.WriteLine("GENERATED_COST_ACTUAL_NULL " + JsonSerializer.Serialize(new {
            thread = Environment.CurrentManagedThreadId, card = source.Id.ToString(), sourceOwner = source.Owner.NetId,
            lookupMissing = predicted is null, energyCostMissing = predicted is not null && predicted.Preview.EnergyCost is null,
            directMembership = session.Party.Select(player => new {
                player = player.NetId, hand = state.GetPlayerCombatState(player).Hand.Cards.Count(c => c.References(source)),
                all = state.GetPlayerCombatState(player).AllCards.Count(c => c.References(source))
            }).ToArray()
        }));
    }

    private static IEnumerable<CodeInstruction> ObserveFindCard(IEnumerable<CodeInstruction> instructions)
    {
        var find = AccessTools.Method(typeof(CombatPredictionState), "FindCard");
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.Calls(find))
            {
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GeneratedAttackCostScenarios), nameof(LookupObserved)));
            }
        }
    }

    private static void TheBallTransfer()
    {
        NativeTheBallControl();
        var (combat, party, enemy, ball) = Board<TheBall>(upgrade: false);
        using (Native(combat))
        {
            MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(),
                party[3].Creature, 7, party[3].Creature, null).GetAwaiter().GetResult();
            var before = KernelSession.Capture(combat);
            var after = before.CardBranches(ball, enemy, 1).FirstOrDefault(b => b.Boundary.Length == 0)?.State
                ?? throw new InvalidOperationException("SETUP_FAILURE TheBall native simulation did not settle");
            var destination = party.Single(p => after.DrawPile(p).Contains(ball));
            Console.WriteLine("GENERATED_COST_TRANSFER " + JsonSerializer.Serialize(new {
                destination = destination.NetId, originalOwner = ball.Owner.NetId,
                sourceIsOriginalPhysicalCard = after.DrawPile(destination).Any(c => ReferenceEquals(c, ball))
            }));
            var pending = after.Fork();
            foreach (var player in party)
                Require(after.EndTurn(player, 80, out var boundary), "SETUP_FAILURE TheBall next turn: " + boundary + " " + after.LastRoundFailure);
            Require(after.Hand(destination).Contains(ball), "SETUP_FAILURE TheBall must be drawn by simulated recipient");
            Inspect("TheBall/recipient-hand", pending, after, party);
            Require(after.CardOwner(ball) == destination && ball.Owner == party[1],
                "Transferred card must retain Original identity while resolving its simulated recipient");
            Require(after.CanPlay(ball) && after.AttackCosts(ball) == (1, 0) && after.FinisherEnergyCost(ball) == 1,
                "Recipient must legally play transferred card at its native cost");
            var baseDamage = ball.DynamicVars.Values.OfType<MegaCrit.Sts2.Core.Localization.DynamicVars.DamageVar>()
                .Sum(v => v.BaseValue);
            Console.WriteLine("GENERATED_COST_DAMAGE_GUARD " + JsonSerializer.Serialize(new {
                baseDamage, hint = after.AttackDamageHint(ball, enemy),
                recipientStrength = after.Power<StrengthPower>(destination.Creature),
                originalStrength = after.Power<StrengthPower>(party[1].Creature),
                variables = ball.DynamicVars.Values.Select(v => new { name = v.Name, kind = v.GetType().Name, value = v.BaseValue }).ToArray()
            }));
            Require(after.AttackDamageHint(ball, enemy) == baseDamage + ball.DynamicVars["Increase"].BaseValue + 7
                && destination == party[3],
                "Transferred attack damage hint must use recipient Strength, not Original owner");
            ActingOwnerTiming(after, party, ball, enemy, destination);
            var wrong = new KernelTeamSearch.Action(ball.Owner, ball, enemy, ChoiceOrdinals: []);
            Require(!after.ForkForReplay().ReplayPlanned(wrong, 80, out var wrongBoundary)
                && wrongBoundary == "planned-card-owner-changed", "Wrong-owner action must remain rejected");
            var parentEnergy = party.Select(after.Energy).ToArray();
            var parentRng = after.FinisherRandomStamp();
            var sibling = after.Fork();
            var child = after.Fork();
            var run = new RolloutRun(child, party, [destination], new RolloutOptions(
                RolloutPolicyKind.Tactical, MaxSteps: 1) { InvestmentLookahead = false, SequenceLookahead = false });
            while (!run.Done) run.Step();
            child = run.State;
            var action = run.Result!.Actions.Single();
            Require(action.Player == destination && ReferenceEquals(action.Card, ball),
                "Production Tactical action must publish the actual recipient and physical Original card");
            Require(child.Energy(destination) == parentEnergy[Array.IndexOf(party, destination)] - 1
                && child.Energy(party[1]) == parentEnergy[1], "Simulated transfer play must debit recipient only");
            Require(party.Select(after.Energy).SequenceEqual(parentEnergy)
                && party.Select(sibling.Energy).SequenceEqual(parentEnergy)
                && after.Hand(destination).Contains(ball) && sibling.Hand(destination).Contains(ball)
                && after.CardOwner(ball) == destination && sibling.CardOwner(ball) == destination
                && after.FinisherRandomStamp() == parentRng && sibling.FinisherRandomStamp() == parentRng,
                "Transferred-card play must preserve parent/sibling ownership, physical identity, resources and RNG");
            var replay = after.ForkForReplay();
            Require(replay.ReplayPlanned(action, 80, out var replayBoundary, out var resolved)
                && resolved.Player == destination && ReferenceEquals(resolved.Card, ball)
                && replay.Energy(destination) == child.Energy(destination),
                "Recipient action must strictly replay with the same physical identity and payment: " + replayBoundary);
            Console.WriteLine("GENERATED_COST_TRANSFER_GUARDS " + JsonSerializer.Serialize(new {
                recipient = destination.NetId, originalOwner = ball.Owner.NetId, actionOwner = action.Player.NetId,
                cost = after.AttackCosts(ball), recipientDamageHint = after.AttackDamageHint(ball, enemy),
                recipientEnergyBefore = parentEnergy[Array.IndexOf(party, destination)], recipientEnergyAfter = child.Energy(destination),
                originalEnergyBefore = parentEnergy[1], originalEnergyAfter = child.Energy(party[1]),
                wrongBoundary, replayBoundary, parentSiblingUnchanged = true
            }));
        }
        Console.WriteLine("PASS generated-attack-cost-probe TheBall-native-transfer");
    }

    private static int observedPlayerTurn;
    private static void ObserveCheckedTurn(int playerTurn) => observedPlayerTurn = playerTurn;
    private static void ActingOwnerTiming(KernelSession after, Player[] party, CardModel ball,
        Creature enemy, Player actor)
    {
        // Constructed timing control only: use the simulator's supported turn
        // advance to distinguish the acting seat from both original and new owner.
        var timing = after.Fork();
        var timingCombat = (CoopBots.Kernel.Vendor.SimulatedCombatState)
            AccessTools.Property(typeof(KernelSession), "Combat").GetValue(timing)!;
        timingCombat.AdvancePlayerTurn(actor);
        var expectedTurn = timingCombat.GetPlayerTurnNumber(actor);
        observedPlayerTurn = -1;
        var observe = new Harmony("coopbots.transfer-acting-owner-timing");
        var checkedMethod = AccessTools.Method(typeof(CombatPredictionSimulator), "CheckWinCondition");
        observe.Patch(checkedMethod, prefix: new HarmonyMethod(typeof(GeneratedAttackCostScenarios), nameof(ObserveCheckedTurn)));
        try
        {
            var played = timing.CardBranches(ball, enemy, 1).FirstOrDefault(b => b.Boundary.Length == 0)?.State
                ?? throw new InvalidOperationException("SETUP_FAILURE distinct-turn transferred play did not settle");
            var newOwner = played.CardOwner(ball);
            var playedCombat = (CoopBots.Kernel.Vendor.SimulatedCombatState)
                AccessTools.Property(typeof(KernelSession), "Combat").GetValue(played)!;
            Require(newOwner != actor,
                "SETUP_FAILURE TheBall must transfer away from acting recipient");
            Require(observedPlayerTurn == expectedTurn && expectedTurn != playedCombat.GetPlayerTurnNumber(newOwner)
                && expectedTurn != playedCombat.GetPlayerTurnNumber(ball.Owner),
                "Post-play win check must retain preplay acting owner after TheBall transfers again");
            Console.WriteLine("GENERATED_COST_ACTING_OWNER_TIMING " + JsonSerializer.Serialize(new {
                actor = actor.NetId, expectedTurn, observedPlayerTurn, newOwner = newOwner.NetId,
                newOwnerTurn = playedCombat.GetPlayerTurnNumber(newOwner), originalOwnerTurn = playedCombat.GetPlayerTurnNumber(ball.Owner)
            }));
        }
        finally { observe.Unpatch(checkedMethod, HarmonyPatchType.All, observe.Id); }
    }

    private static void NativeTheBallControl()
    {
        var (nativeCombat, nativeParty, nativeEnemy, nativeBall) = Board<TheBall>(upgrade: false);
        using (Native(nativeCombat))
        {
            var paid = nativeBall.SpendResources().GetAwaiter().GetResult();
            nativeBall.OnPlayWrapper(new ThrowingPlayerChoiceContext(), nativeEnemy, false,
                new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                    StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
            var destination = nativeParty.Single(p => p.PlayerCombatState!.DrawPile.Cards.Contains(nativeBall));
            Console.WriteLine("GENERATED_COST_NATIVE_TRANSFER " + JsonSerializer.Serialize(new {
                paidEnergy = paid.Item1, destination = destination.NetId, owner = nativeBall.Owner.NetId,
                physicalIdentityKept = destination.PlayerCombatState!.DrawPile.Cards.Any(c => ReferenceEquals(c, nativeBall))
            }));
            MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 1, destination).GetAwaiter().GetResult();
            Require(destination.PlayerCombatState!.Hand.Cards.Contains(nativeBall), "SETUP_FAILURE native recipient actually draws TheBall");
            var fresh = KernelSession.Capture(nativeCombat);
            Inspect("TheBall/fresh-native-recipient-hand", fresh, fresh, nativeParty);
            var oldEnergy = nativeParty[1].PlayerCombatState!.Energy;
            var recipientEnergy = destination.PlayerCombatState!.Energy;
            var recipientPaid = nativeBall.SpendResources().GetAwaiter().GetResult();
            nativeBall.OnPlayWrapper(new ThrowingPlayerChoiceContext(), nativeEnemy, false,
                new ResourceInfo { EnergySpent = recipientPaid.Item1, EnergyValue = recipientPaid.Item1,
                    StarsSpent = recipientPaid.Item2, StarValue = recipientPaid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
            Require(recipientPaid.Item1 == 1 && destination.PlayerCombatState!.Energy == recipientEnergy - 1
                && nativeParty[1].PlayerCombatState!.Energy == oldEnergy,
                "Actual native second transfer must debit its recipient, leaving previous owner resources unchanged");
            Console.WriteLine("GENERATED_COST_NATIVE_RECIPIENT_PAYMENT " + JsonSerializer.Serialize(new {
                actor = destination.NetId, recipientEnergyBefore = recipientEnergy,
                recipientEnergyAfter = destination.PlayerCombatState!.Energy, originalEnergyBefore = oldEnergy,
                originalEnergyAfter = nativeParty[1].PlayerCombatState!.Energy, paid = recipientPaid.Item1
            }));
        }
    }

    private static void High6(bool fieldPowers = false, int repeat = 0)
    {
        var (combat, party, enemy, _) = Board<CloakAndDagger>(upgrade: true);
        foreach (var player in party)
        {
            var dance = combat.CreateCard<BladeDance>(player); dance.UpgradeInternal(); dance.FinalizeUpgradeInternal();
            player.PlayerCombatState!.Hand.AddInternal(dance);
            player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Anger>(player));
            player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(player));
            player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(player));
        }
        using var native = Native(combat);
        if (fieldPowers)
        {
            var choice = new ThrowingPlayerChoiceContext();
            var creature = party[1].Creature;
            MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<PhantomBladesPower>(choice, creature, 1, creature, null).GetAwaiter().GetResult();
            MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<AfterimagePower>(choice, creature, 1, creature, null).GetAwaiter().GetResult();
            MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<DexterityPower>(choice, creature, 4, creature, null).GetAwaiter().GetResult();
            MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<WeakPower>(choice, creature, 3, creature, null).GetAwaiter().GetResult();
        }
        var observe = new Harmony("coopbots.generated-cost-observe-original-find");
        var costs = AccessTools.Method(typeof(KernelSession), "AttackCosts");
        observe.Patch(costs, transpiler: new HarmonyMethod(typeof(GeneratedAttackCostScenarios), nameof(ObserveFindCard)));
        var savedPerf = KernelCombatPlanner.Perf;
        KernelCombatPlanner.Perf = KernelCombatPlanner.TournamentPerf.High;
        var planner = new KernelCombatPlanner();
        try
        {
            var decisionMethod = AccessTools.Method(typeof(KernelCombatPlanner), "TryTournamentDecision");
            var stop = Environment.TickCount64 + 20_000;
            string? result = null;
            while (Environment.TickCount64 < stop)
            {
                var parameters = new object?[] { combat, party, true, "constructed-generated-cost-high6", null };
                result = decisionMethod.Invoke(planner, parameters)!.ToString();
                if (result != "Pending") break;
                Thread.Sleep(1);
            }
            Console.WriteLine("GENERATED_COST_PRODUCTION " + JsonSerializer.Serialize(new {
                result, repeat, fieldPowers, exceptions = planner.ExceptionFallbacksForTesting,
                missingLookup, missingEnergyCost, enemyHp = enemy.CurrentHp,
                perf = KernelCombatPlanner.Perf.ToString(), allocationProbe = AllocationProbe.Enabled,
                cpu = Environment.ProcessorCount
            }));
            Require(result != "Pending", "SETUP_FAILURE production diagnostic did not close within finite window");
            Require(missingLookup == 0 && missingEnergyCost == 0,
                "BASELINE_TARGET_REPRODUCTION: original AttackCosts found null object (see actual diagnostics)");
        }
        finally
        {
            planner.Reset(newCombat: true);
            KernelCombatPlanner.Perf = savedPerf;
            observe.Unpatch(costs, HarmonyPatchType.All, observe.Id);
        }
    }
}
