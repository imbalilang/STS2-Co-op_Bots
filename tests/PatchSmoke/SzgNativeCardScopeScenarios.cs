using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Live AR10B7M2X001 R4/R6 field H: the differing bit is DeckVersion != null,
// not IsClone (ContinuationStamp does not print that field). These are minimal
// native seam witnesses, not a claim that the later main-thread hang is solved.
// R2: 7 private production faults, actual Rebuild0/RED1/exact restore Rebuild0/GREEN0.
// Source bytes, DLL/PDB, commands, actual exits: work/architecture-refactor-20261009/native-card-scope/r2-final/proof.json.
internal static class SzgNativeCardScopeScenarios
{
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.native-card-scope");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(SzgNativeCardScopeScenarios), nameof(Format)));
        var machine = AccessTools.Method(typeof(CardModel), "OnPlayWrapper")
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        presentation.Patch(AccessTools.Method(machine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(SzgNativeCardScopeScenarios), nameof(PresentationClock)));
        try
        {
            if (caseName is null or "clone") CloneScope();
            if (caseName is null or "shuffle") AngerShuffle();
            if (caseName is null or "the-ball") BallGrowth();
            if (caseName is null or "allocation-wiring") AllocationWiring();
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static void CloneScope()
    {
        foreach (var removed in new[] { false, true })
        {
            var (combat, owner, source, _) = Board("CARD-SCOPE-CLONE");
            source.HasBeenRemovedFromState = removed;
            source.ExhaustOnNextPlay = true;
            var root = KernelSession.Capture(combat);
            var frozen = root.PartyStateText();
            var branch = root.Fork();
            var predicted = branch.Simulator.State.FindCard(source)!;
            var preview = predicted.MutablePreview; // force the real COW capture path
            Check(ReferenceEquals(preview.DeckVersion, source.DeckVersion)
                && preview.HasBeenRemovedFromState == removed,
                /* R2 RED: Native card scope: state capture and COW must preserve deck identity and removed state instead of applying gameplay clone resets */
                "state capture and COW must preserve deck identity and removed state instead of applying gameplay clone resets");
            var native = combat.CloneCard(source);
            var simulated = ((SimulatedCombatState)branch.Simulator.State.CombatState).CloneCard(preview);
            Console.WriteLine($"DIAG clone removed={removed} nativeDeck={native.DeckVersion is not null} simDeck={simulated.DeckVersion is not null} nativeRemoved={native.HasBeenRemovedFromState} simRemoved={simulated.HasBeenRemovedFromState}");
            Check(native.DeckVersion is null && !native.HasBeenRemovedFromState
                && Fields(simulated) == Fields(native)
                && ReferenceEquals(simulated.Enchantment!.Card, simulated)
                && root.PartyStateText() == frozen && source.HasBeenRemovedFromState == removed,
                /* R2 RED: Native card scope: gameplay ICardScope.CloneCard must match native AfterCloned identity resets without changing its frozen parent */
                "gameplay ICardScope.CloneCard must match native AfterCloned identity resets without changing its frozen parent");
        }
        Console.WriteLine("PASS: native gameplay cloning resets deck/removed fields while actual capture and COW retain them.");
    }

    private static void AngerShuffle()
    {
        foreach (var withPlayListener in new[] { false, true })
        {
            var (combat, owner, anger, enemy) = Board("ANGER-PERFECT-FIT");
            if (withPlayListener)
            {
                var waiting = combat.CreateCard<Bash>(owner);
                waiting.DeckVersion = ((RunState)combat.RunState).CreateCard<Bash>(owner);
                Enchant(waiting);
                owner.PlayerCombatState!.PlayPile.AddInternal(waiting);
            }
            using var native = new NativeState(combat);
            var root = KernelSession.Capture(combat);
            var frozen = root.PartyStateText();
            var sim = root.Fork();
            // R2 RED: Native card scope: mirrored Anger must complete its actual paid transaction: fault-injected-unplayable
            Check(sim.Play(anger, enemy, out var reason), "mirrored Anger must complete its actual paid transaction: " + reason);
            var paid = anger.SpendResources().GetAwaiter().GetResult();
            anger.OnPlayWrapper(new ThrowingPlayerChoiceContext(), enemy, false,
                new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1, StarsSpent = paid.Item2, StarValue = paid.Item2 },
                skipCardPileVisuals: true).GetAwaiter().GetResult();
            Check(Piles(sim, owner) == Piles(owner) && sim.Hp(enemy) == enemy.CurrentHp
                && sim.Energy(owner) == owner.PlayerCombatState!.Energy,
                /* R2 RED: Native card scope: actual native Anger must create the same attached copy and result piles before shuffle */
                "actual native Anger must create the same attached copy and result piles before shuffle");
            NativeShuffle(combat, owner);
            sim.Simulator.Shuffle(owner);
            Console.WriteLine($"DIAG shuffle play={withPlayListener} native={Piles(owner)} sim={Piles(sim,owner)}");
            Check(Piles(sim, owner) == Piles(owner)
                && sim.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState(),
                /* R2 RED: Native card scope: native PerfectFit listeners must follow current owned Hand Draw Discard Exhaust Play order after Anger generates its copy */
                "native PerfectFit listeners must follow current owned Hand Draw Discard Exhaust Play order after Anger generates its copy");
            // Change only the live order after the branch has its complete state.
            // A second shuffle must use the fork's own piles, never these lists.
            var sibling = sim.Fork();
            var nativeBefore = owner.PlayerCombatState!.DrawPile.Cards.ToArray();
            sibling.Simulator.Shuffle(owner); // control before any live mutation
            var reverse = nativeBefore.Reverse().ToArray();
            foreach (var card in reverse) { owner.PlayerCombatState.DrawPile.RemoveInternal(card); owner.PlayerCombatState.DrawPile.AddInternal(card); }
            sim.Simulator.Shuffle(owner);
            var independent = sim.PartyStateText() == sibling.PartyStateText()
                && sim.Simulator.Rng.ShuffleState == sibling.Simulator.Rng.ShuffleState;
            // Restore only the native test board, perform the second real native
            // hook/RNG dispatch, then compare what actually arrives in hand.
            foreach (var card in nativeBefore) { owner.PlayerCombatState.DrawPile.RemoveInternal(card); owner.PlayerCombatState.DrawPile.AddInternal(card); }
            NativeShuffle(combat, owner);
            CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 1m, owner).GetAwaiter().GetResult();
            sim.Simulator.Draw(owner, 1);
            Check(independent && Piles(sim, owner) == Piles(owner)
                && sim.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState()
                && root.PartyStateText() == frozen,
                /* R2 RED: Native card scope: a forked shuffle must remain independent of later live pile reordering and draw the same native card identity */
                "a forked shuffle must remain independent of later live pile reordering and draw the same native card identity");
        }
        Console.WriteLine("PASS: native Anger plus PerfectFit preserves complete pile order, deck identity, shared shuffle RNG and frozen-fork ownership.");
    }

    private static string BallGrowth()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 78001);
        var teammate = Player.CreateForNewRun<Deprived>(UnlockState.all, 78002);
        var run = RunState.CreateForTest([p, teammate], seed: "BALL-NATIVE-GROWTH");
        var combat = new CombatState(runState: run) { RoundNumber = 1 };
        foreach (var player in new[] { p, teammate })
        {
            player.ResetCombatState(); combat.AddPlayer(player); player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3); player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        }
        var ball = combat.CreateCard<TheBall>(p); ball.DeckVersion = run.CreateCard<TheBall>(p); p.PlayerCombatState!.Hand.AddInternal(ball);
        teammate.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(teammate));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        using var native = new NativeState(combat);
        var root = KernelSession.Capture(combat); var frozen = root.PlanningState([p.NetId, teammate.NetId]);
        var sim = root.Fork();
        Check(sim.Play(ball, enemy, out var reason), "mirrored TheBall must settle its real paid first transaction: " + reason);
        PaidBall(ball, enemy);
        var predicted = sim.Simulator.State.FindCard(ball)!;
        var extra = AccessTools.Field(typeof(TheBall), "_extraDamageFromPlays");
        Console.WriteLine($"DIAG TheBall first paid nativeDamage={ball.DynamicVars.Damage.BaseValue} simDamage={predicted.Preview.DynamicVars.Damage.BaseValue} extra={extra.GetValue(ball)}/{extra.GetValue(predicted.Preview)} hp={enemy.CurrentHp}/{sim.Hp(enemy)} owner={ball.Owner.NetId}/{predicted.Preview.Owner.NetId} targetsRng={run.Rng.CombatTargets.Counter()}/{sim.Simulator.Rng.CombatTargets.Counter()}");
        Check(ball.DynamicVars.Damage.BaseValue == 20m && predicted.Preview.DynamicVars.Damage.BaseValue == 20m
            && (decimal)extra.GetValue(ball)! == 10m && (decimal)extra.GetValue(predicted.Preview)! == 10m,
            /* Actual physical R2 RED: Native card scope: actual paid TheBall must grow both damage and downgrade bookkeeping before its next owner draws it
               Withdraw both post-damage growth writes: native20/10 vs branch10/0; exact source and all six DLL/PDB restored, native GREEN0. */
            "actual paid TheBall must grow both damage and downgrade bookkeeping before its next owner draws it");
        Check(enemy.CurrentHp == 90 && sim.Hp(enemy) == 90 && ball.Owner == teammate && predicted.Preview.Owner == teammate
            && Piles(sim, p) == Piles(p) && Piles(sim, teammate) == Piles(teammate)
            && sim.Simulator.Rng.CombatTargets.CaptureState() == run.Rng.CombatTargets.CaptureState()
            && root.PlanningState([p.NetId, teammate.NetId]).Matches(frozen),
            "native growing TheBall must preserve team result piles, shared target RNG, deck identity and its frozen parent");
        CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 2m, teammate).GetAwaiter().GetResult(); sim.Simulator.Draw(teammate, 2);
        Check(teammate.PlayerCombatState.Hand.Cards.Contains(ball) && sim.Hand(teammate).Contains(ball)
            && sim.PlanningState([p.NetId, teammate.NetId]).Matches(KernelSession.Capture(combat).PlanningState([p.NetId, teammate.NetId])),
            "native teammate drawing TheBall must receive its grown future fields and equivalent complete board");
        var parentPredicted = sim.Simulator.State.FindCard(ball)!;
        var parentDamage = parentPredicted.Preview.DynamicVars.Damage.BaseValue;
        var fork = sim.Fork();
        Check(fork.Play(ball, enemy, out reason), "mirrored TheBall must settle its second teammate paid transaction: " + reason);
        PaidBall(ball, enemy);
        var next = fork.Simulator.State.FindCard(ball)!.Preview;
        Console.WriteLine($"DIAG TheBall second paid nativeDamage={ball.DynamicVars.Damage.BaseValue} simDamage={next.DynamicVars.Damage.BaseValue} extra={extra.GetValue(ball)}/{extra.GetValue(next)} hp={enemy.CurrentHp}/{fork.Hp(enemy)} owner={ball.Owner.NetId}/{next.Owner.NetId}");
        Check(ball.DynamicVars.Damage.BaseValue == 30m && next.DynamicVars.Damage.BaseValue == 30m
            && (decimal)extra.GetValue(ball)! == 20m && (decimal)extra.GetValue(next)! == 20m
            && enemy.CurrentHp == 70 && fork.Hp(enemy) == 70 && ball.Owner == p && next.Owner == p
            && parentPredicted.Preview.DynamicVars.Damage.BaseValue == parentDamage
            && fork.Simulator.Rng.CombatTargets.CaptureState() == run.Rng.CombatTargets.CaptureState()
            && fork.PlanningState([p.NetId, teammate.NetId]).Matches(KernelSession.Capture(combat).PlanningState([p.NetId, teammate.NetId])),
            "native second TheBall must deal grown damage, retain further growth and keep sibling COW fields independent");
        ball.UpgradeInternal(); ball.DowngradeInternal();
        next.UpgradeInternal(); next.DowngradeInternal();
        Check(ball.DynamicVars.Damage.BaseValue == 30m && next.DynamicVars.Damage.BaseValue == 30m,
            "native TheBall downgrade must restore accumulated damage from its preserved private growth field");
        Console.WriteLine("PASS: paid native TheBall growth survives team transfer, draw, second play, downgrade, target RNG and COW forks.");
        return Piles(p) + "/" + Piles(teammate) + "/" + enemy.CurrentHp + "/" + ball.DynamicVars.Damage.BaseValue + "/" + run.Rng.CombatTargets.Counter();
    }

    // Actual B1 physical R2: caller scope held across yield, initial measurement
    // removed, shared measurement removed: each Rebuild0 / native RED1 / exact
    // six sources + six DLL/PDB restore Rebuild0 / native GREEN0.
    // Raw proof: work/architecture-refactor-20261009/fixed-budget-performance-v3/b1-caller-preview-execution-v1/r2/proof.json.
    // RED: caller measurement must be disposed and published before yielding its first native branch
    // RED: first mutable preview must measure exactly one initial clone and storage replacement
    // RED: shared mutable preview must measure exactly one detach while preserving its sibling fields
    private static void AllocationWiring()
    {
        var previous = AllocationProbe.Enabled;
        try
        {
            AllocationProbe.ForceEnabled(true); _ = AllocationProbe.ReportAndReset();
            PreviewDetachWiring();
            BranchCallerWiring();
            AllocationProbe.ForceEnabled(false);
            var disabled = BallGrowth();
            Check(AllocationProbe.ReportAndReset() == "", "disabled allocation probe must retain empty output");
            AllocationProbe.ForceEnabled(true); _ = AllocationProbe.ReportAndReset();
            var enabled = BallGrowth();
            Check(disabled == enabled,
                "enabled instrumentation must preserve actual two-paid TheBall native piles, growth, RNG and COW outcome");
            var report = AllocationProbe.ReportAndReset();
            Console.WriteLine("DIAG enabled native TheBall allocation phases: " + report);
            // Capture materializes previews before the paid route, so this
            // native route exercises shared COW; first lazy materialization
            // has its separate exact-count control in PreviewDetachWiring.
            Check(report.Contains("Play=", StringComparison.Ordinal)
                && report.Contains("PreviewSharedDetach=", StringComparison.Ordinal),
                "actual two-paid native TheBall trace must record paid Play and its shared preview detach");
            Console.WriteLine("PASS: B1 caller and preview wiring preserves native paid outcomes, ordered choices, cancellation and yield consumer boundaries.");
        }
        finally
        {
            AllocationProbe.ForceEnabled(true); _ = AllocationProbe.ReportAndReset();
            AllocationProbe.ForceEnabled(previous);
        }
    }
    private static (long Bytes, long Calls, long Enums) AllocationCounts(AllocationProbe.Phase phase)
    {
        var index = (int)phase;
        long Read(string name) => Interlocked.Read(ref ((long[])AccessTools.Field(typeof(AllocationProbe), name).GetValue(null)!)[index]);
        return (Read("Bytes"), Read("Calls"), Read("BranchEnumerations"));
    }
    private static void PreviewDetachWiring()
    {
        var (_, _, original, _) = Board("ALLOCATION-PREVIEW");
        AllocationProbe.ForceEnabled(true); _ = AllocationProbe.ReportAndReset();
        var predicted = new PredictedCard(original);
        var first = predicted.MutablePreview; first.DynamicVars.Damage.BaseValue += 1;
        Check(AllocationCounts(AllocationProbe.Phase.PreviewInitialDetach) is { Calls: 1, Bytes: > 0 }
            && AllocationCounts(AllocationProbe.Phase.PreviewSharedDetach).Calls == 0,
            "first mutable preview must measure exactly one initial clone and storage replacement");
        var parentDamage = first.DynamicVars.Damage.BaseValue;
        using var context = new PredictionForkContext();
        var child = predicted.Fork(context);
        var detached = child.MutablePreview; detached.DynamicVars.Damage.BaseValue += 2;
        var afterShared = AllocationCounts(AllocationProbe.Phase.PreviewSharedDetach);
        Check(afterShared is { Calls: 1, Bytes: > 0 }
            && predicted.Preview.DynamicVars.Damage.BaseValue == parentDamage
            && child.Preview.DynamicVars.Damage.BaseValue == parentDamage + 2,
            "shared mutable preview must measure exactly one detach while preserving its sibling fields");
        _ = child.MutablePreview;
        Check(AllocationCounts(AllocationProbe.Phase.PreviewInitialDetach).Calls == 1
            && AllocationCounts(AllocationProbe.Phase.PreviewSharedDetach) == afterShared,
            "an already owned mutable preview must not emit another detach measurement");
    }
    private static void BranchCallerWiring()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        using var native = new NativeState(combat);
        var card = owner.PlayerCombatState!.Hand.Cards[0];
        var root = KernelSession.Capture(combat);
        AllocationProbe.ForceEnabled(true); _ = AllocationProbe.ReportAndReset();
        using (var iterator = root.CardBranches(card, enemy, 2).GetEnumerator())
        {
            Check(AllocationCounts(AllocationProbe.Phase.BranchOther) == (0L, 0L, 0L),
                "creating a card-branch enumerable without MoveNext must not count an enumeration");
            Check(iterator.MoveNext(), "allocation iterator must settle a real native branch");
            var beforeConsumer = AllocationCounts(AllocationProbe.Phase.BranchOther);
            Check(beforeConsumer is { Calls: 1, Bytes: > 0, Enums: 1 },
                "caller measurement must be disposed and published before yielding its first native branch");
            var consumer = new byte[128 * 1024]; consumer[0] = 1; GC.KeepAlive(consumer);
            Check(AllocationCounts(AllocationProbe.Phase.BranchOther) == beforeConsumer,
                "yield-suspended consumer allocations must stay outside the synchronous CardBranches MoveNext measurement");
        }
        var afterDispose = AllocationCounts(AllocationProbe.Phase.BranchOther);
        Check(afterDispose.Calls == 1 && afterDispose.Enums == 1,
            "iterator disposal without another MoveNext must not fabricate a branch measurement");
        foreach (var stopped in new[] { false, true })
        {
            var request = new KernelDecisionRequest(93001, int.MaxValue);
            if (stopped) request.Cancel("allocation-cancel-control");
            using var budget = KernelSearchBudgetScope.Enter(request);
            AllocationProbe.ForceEnabled(false);
            var direct = root.CardBranches(card, enemy, 2);
            Check(direct.GetType().Name.Contains("CoreCardBranches", StringComparison.Ordinal),
                "disabled CardBranches must directly return the original core iterator");
            var off = direct.ToArray();
            AllocationProbe.ForceEnabled(true); var on = root.CardBranches(card, enemy, 2).ToArray();
            Check(off.Length == on.Length && off.Zip(on).All(pair => pair.First.Boundary == pair.Second.Boundary
                && pair.First.ChoiceOrdinals.SequenceEqual(pair.Second.ChoiceOrdinals)
                && pair.First.State.PlanningState([owner.NetId]).Matches(pair.Second.State.PlanningState([owner.NetId]))),
                "enabled and disabled native branch order, fallback, cancellation and complete state must remain equal");
        }
        // Compare a real choice vector as well as the no-choice Strike above.
        // Scoring entry avoids adding optional choice-ranking work to this control.
        var survivor = combat.CreateCard<Survivor>(owner);
        owner.PlayerCombatState.Hand.AddInternal(survivor);
        var choiceRoot = KernelSession.Capture(combat);
        using (KernelChoiceContinuation.Enter())
        {
            AllocationProbe.ForceEnabled(false); var offChoices = choiceRoot.CardBranches(survivor, null, 2).ToArray();
            AllocationProbe.ForceEnabled(true); var onChoices = choiceRoot.CardBranches(survivor, null, 2).ToArray();
            Check(offChoices.Length == 2 && onChoices.Length == 2 && offChoices.Zip(onChoices)
                .All(pair => pair.First.Boundary.Length == 0 && pair.Second.Boundary.Length == 0
                    && pair.First.ChoiceOrdinals.SequenceEqual(pair.Second.ChoiceOrdinals)
                    && pair.First.Choices.SelectMany(c => c.Cards).SequenceEqual(pair.Second.Choices.SelectMany(c => c.Cards))
                    && pair.First.State.PlanningState([owner.NetId]).Matches(pair.Second.State.PlanningState([owner.NetId]))),
                "enabled and disabled paid Survivor branches must keep their two ordered choice vectors and complete states");
        }
        owner.PlayerCombatState.Hand.RemoveInternal(survivor);
        AllocationProbe.ForceEnabled(true); _ = AllocationProbe.ReportAndReset();
        var options = new RolloutOptions(RolloutPolicyKind.Tactical, MaxSteps: 1)
            { InvestmentLookahead = false, SequenceLookahead = false, SequenceHandLookahead = false };
        AllocationProbe.ForceEnabled(false); var offPolicy = KernelRollout.Run(root.Fork(), [owner], [owner], options);
        AllocationProbe.ForceEnabled(true); var onPolicy = KernelRollout.Run(root.Fork(), [owner], [owner], options);
        Check(offPolicy.Actions.Count == onPolicy.Actions.Count && offPolicy.Actions.Zip(onPolicy.Actions)
            .All(pair => pair.First.Card == pair.Second.Card && pair.First.Target == pair.Second.Target
                && pair.First.EndTurn == pair.Second.EndTurn),
            "instrumented actual Tactical policy must preserve its same-root first settled action");
        Check(AllocationCounts(AllocationProbe.Phase.BranchTacticalScoringMode).Enums > 0
            && AllocationCounts(AllocationProbe.Phase.BranchTacticalFullChoiceMode).Enums > 0,
            "actual Tactical scoring and full-choice callers must populate their finite entry categories");
        Console.WriteLine("DIAG Tactical caller phases: " + AllocationProbe.ReportAndReset());
    }

    private static void PaidBall(TheBall ball, Creature enemy)
    {
        var paid = ball.SpendResources().GetAwaiter().GetResult(); Check(paid.Item1 == 1, "native TheBall pays one energy");
        ball.OnPlayWrapper(new ThrowingPlayerChoiceContext(), enemy, false,
            new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1, StarsSpent = paid.Item2, StarValue = paid.Item2 },
            skipCardPileVisuals: true).GetAwaiter().GetResult();
    }

    private static void NativeShuffle(CombatState combat, Player owner)
    {
        // Actual sts2 command: RNG, pile transitions and all before/after hooks.
        // Native NonInteractiveMode (TestMode) suppresses presentation waits.
        CardPileCmd.Shuffle(new ThrowingPlayerChoiceContext(), owner).GetAwaiter().GetResult();
    }
    private static string Fields(CardModel c) => $"{c.Id.Entry}/{c.CurrentUpgradeLevel}/{c.DeckVersion is not null}/{c.HasBeenRemovedFromState}/{c.ExhaustOnNextPlay}/{c.IsClone}/{c.Enchantment?.Id.Entry}/{c.Enchantment?.Amount}";
    private static string Piles(Player p) => string.Join('|', p.PlayerCombatState!.AllPiles.Select(pile => pile.Type+":"+string.Join(';',pile.Cards.Select(Fields))));
    private static string Piles(KernelSession s, Player p) => string.Join('|',s.Simulator.State.GetPlayerCombatState(p).AllPiles.Select(pile => pile.Type+":"+string.Join(';',pile.Cards.Select(c=>Fields(c.Preview)))));
    private static void Enchant(CardModel card) { card.EnchantInternal(ModelDb.Enchantment<PerfectFit>().ToMutable(),1);card.Enchantment!.ModifyCard(); }
    private static (CombatState,Player,Anger,Creature) Board(string seed)
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,78001);
        var run=RunState.CreateForTest([p],seed:seed);var combat=new CombatState(runState:run);
        p.ResetCombatState();combat.AddPlayer(p);p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
        var anger=combat.CreateCard<Anger>(p);anger.DeckVersion=run.CreateCard<Anger>(p);Enchant(anger);p.PlayerCombatState.Hand.AddInternal(anger);
        p.PlayerCombatState.DiscardPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        var enemy=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"enemy");combat.AddCreature(enemy);
        enemy.Monster!.SetUpForCombat();enemy.SetMaxHpInternal(100);enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"],true);
        return(combat,p,anger,enemy);
    }
    private sealed class NativeState : IDisposable
    {
        private readonly FieldInfo turn=AccessTools.Field(typeof(CombatManager),"_turnState");
        private readonly PropertyInfo state=AccessTools.Property(typeof(RunManager),"State"),ascension=AccessTools.Property(typeof(RunManager),nameof(RunManager.AscensionManager));
        private readonly object? oldTurn,oldState,oldAscension;private readonly ulong? oldLocal=LocalContext.NetId;
        internal NativeState(CombatState combat)
        {
            oldTurn=turn.GetValue(CombatManager.Instance);oldState=state.GetValue(RunManager.Instance);oldAscension=ascension.GetValue(RunManager.Instance);
            state.SetValue(RunManager.Instance,combat.RunState);ascension.SetValue(RunManager.Instance,new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(combat.RunState.AscensionLevel));LocalContext.NetId=combat.Players[0].NetId;
            var current=Activator.CreateInstance(turn.FieldType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[combat],null)!;
            AccessTools.Property(turn.FieldType,"IsInProgress").SetValue(current,true);AccessTools.Property(turn.FieldType,"IsStarting").SetValue(current,false);turn.SetValue(CombatManager.Instance,current);
        }
        public void Dispose(){turn.SetValue(CombatManager.Instance,oldTurn);state.SetValue(RunManager.Instance,oldState);ascension.SetValue(RunManager.Instance,oldAscension);LocalContext.NetId=oldLocal;}
    }
    private static IEnumerable<CodeInstruction> PresentationClock(IEnumerable<CodeInstruction> instructions)
    { foreach(var i in instructions){if(i.operand is MethodInfo m&&m.DeclaringType?.FullName=="Godot.Time"&&m.Name=="GetTicksMsec")i.operand=AccessTools.Method(typeof(SzgNativeCardScopeScenarios),nameof(Clock));yield return i;} }
    private static ulong Clock()=>0;
    private static bool Format(ref string __result){__result="";return false;}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Native card scope: "+message);}
}
