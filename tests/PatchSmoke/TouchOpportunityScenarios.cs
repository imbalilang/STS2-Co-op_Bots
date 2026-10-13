using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

// Constructed four-seat A10 producer-to-native mechanism, never a restored F36.
// Actual baseline05 / production-withdrawal R2 RED, 2026-10-12:
// "Touch opportunity: actual mandatory opportunity must publish complete Touch choices before Core confirmation"
// DIAG full-belt potion=TOUCH_OF_INSANITY choices=-1; same original assertion.
// R2 [0,0,1,0,0,0], 1281 source/31 DLL+PDB exact forced restore; restored default4 PASS.
internal static class TouchOpportunityScenarios
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(string? only = null)
    {
        TestEnvironment.Ensure();
        if (only is null or "full-belt") NativeOpportunity(false, false);
        if (only is null or "boss") NativeOpportunity(true, false);
        if (only is null or "empty") NativeOpportunity(false, true);
        if (only is null or "guards") Guards();
    }

    private static void NativeOpportunity(bool boss, bool empty)
    {
        var b = Board(boss, empty); using var scope = Scope(b);
        var actors = b.Combat.Players.ToArray(); var ids = actors.Select(p => p.NetId).ToArray();
        var root = KernelSession.Capture(b.Combat); var frozen = root.PlanningState(ids);
        Check(KernelPotionOpportunity.HasFullBelt(b.Owner) && root.HeldPotionCount(b.Owner) == b.Owner.MaxPotionCount,
            "constructed native owner must really have the same full two-slot belt as the archived F36 trigger");
        var chosen = KernelPotionOpportunity.Choose(root, actors, actors, bossOpening: boss);
        var contract = chosen?.Action;
        Console.WriteLine($"DIAG opportunity mode={(boss ? "boss" : empty ? "empty" : "full-belt")} potion={contract?.Potion?.Id.Entry} choices={contract?.Choices?.Count ?? -1}");
        Check(contract is { Potion: TouchOfInsanity, Choices.Count: 1, ChoiceOrdinals.Count: 1 },
            "actual mandatory opportunity must publish complete Touch choices before Core confirmation");
        Check(ReferenceEquals(contract!.Potion, b.Potion) && ReferenceEquals(contract.Target, b.Owner.Creature)
            && contract.Choices![0].Owner == b.Owner.NetId && root.PlanningState(ids).Matches(frozen),
            "producer must preserve physical bottle/recipient/root and complete original future state");
        var choices = contract.Choices!;
        Check(empty ? choices[0].Cards.Count == 0 : choices[0].Cards.Single().Id == b.Bash.Id.Entry,
            "opportunity choice must retain the native empty menu or first cost-prioritized Bash");
        var predicted = root.ForkForReplay();
        Check(predicted.ReplayPlanned(contract, 1, out var reason, out _), "complete choice must replay: " + reason);
        var planner = (KernelCombatPlanner)AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
        var offered = (bool)AccessTools.Method(typeof(KernelCombatPlanner), "TryPotionOpportunity")
            .Invoke(planner, [b.Combat, actors, null, null])!;
        var plan = planner.ConfirmedPotion;
        Check(offered && plan is { Choices.Count: 1 } && ReferenceEquals(plan.Potion, b.Potion)
            && ReferenceEquals(plan.Target, b.Owner.Creature)
            && plan.Choices![0].Cards.SequenceEqual(contract.Choices![0].Cards),
            "real Core opportunity confirmation must preserve the producer's exact Touch contract");
        Check(AutoPilot.Drives(plan!.Target!.Player!.NetId), "actual Runtime delegated-recipient guard must be satisfied");
        Check(BotChoicePlanSync.Prepare(plan, b.Combat), "actual no-peer choice synchronization must prepare");
        var slot = b.Owner.GetPotionSlotIndex(b.Potion);
        var action = (UsePotionAction)Call(scope, "Enqueue", plan.Potion, plan.Target)!;
        var selector = new FallbackSelector(b.Strike);
        using (CardSelectCmd.PushSelector(selector)) action.Execute().GetAwaiter().GetResult();
        Check(action.CompletionTask.IsCompleted && action.Exception is null
            && !(bool)Get(scope, "Diverged")! && selector.Calls == 0,
            "real native UsePotionAction must consume the production choice without fallback");
        Check(action.PotionIndex == slot && b.Owner.GetPotionAtSlotIndex(slot) is null
            && ReferenceEquals(b.Owner.GetPotionAtSlotIndex(1), b.Retained)
            && !b.Retained.HasBeenRemovedFromState,
            "full-belt same-ID bottles must consume only the selected physical slot");
        var actual = KernelSession.Capture(b.Combat).PlanningState(ids);
        var expected = predicted.PlanningState(ids);
        Console.WriteLine($"DIAG native opportunity fourSeats={actors.Length} mode={(boss ? "boss" : empty ? "empty" : "full-belt")} choiceIndex={Get(scope, "Index")} calls={selector.Calls} fullFacts={expected.Matches(actual)} mismatch={expected.FirstMismatch(actual)}");
        Check(expected.Matches(actual), "native mandatory Touch must match complete future Facts and RNG: " + expected.FirstMismatch(actual));
        if (!empty) Check(b.Bash.EnergyCost.GetWithModifiers(CostModifiers.All) == 0
            && b.Strike.EnergyCost.GetWithModifiers(CostModifiers.All) == 1,
            "native paid bottle must free the selected physical Bash and retain Strike cost");
        Console.WriteLine($"PASS: Touch opportunity {(boss ? "boss-opening" : empty ? "empty-menu" : "full-belt")} real producer/Core/native complete Facts contract.");
    }

    private static void Guards()
    {
        var b = Board(false, false); using var scope = Scope(b);
        var actors = b.Combat.Players.ToArray(); var ids = actors.Select(p => p.NetId).ToArray();
        var root = KernelSession.Capture(b.Combat); var before = root.PlanningState(ids);
        var stopped = new KernelDecisionRequest(19001, 0, 6);
        Check(KernelPotionOpportunity.Choose(root, actors, actors, request: stopped) is null
            && KernelPotionOpportunity.Choose(root, actors, actors, bossOpening: true, request: stopped) is null,
            "exhausted mandatory opportunity must return without starting native choice work");
        Check(root.PotionActions(b.Potion, b.Recipient.Creature, maximumChoices: 1, controlledActors: [b.Owner]).Count == 0,
            "an undelegated recipient must have no synthesized Touch action");
        Check(root.PlanningState(ids).Matches(before) && root.HoldsPotion(b.Potion),
            "stopped and human controls must preserve physical root, bottle and RNG");
        // Ordinary policy and all old paid menu/slot/ACK guards remain in the
        // unchanged KernelTouchPotionScenarios suite, run by the private runner.
        Console.WriteLine("PASS: Touch opportunity stopped/delegated guards leave complete root untouched.");
    }

    private sealed record State(CombatState Combat, Player Owner, Player Recipient,
        Creature Enemy, PotionModel Potion, PotionModel Retained, CardModel Strike, CardModel Bash, object Legacy);
    private static State Board(bool boss, bool empty)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var recipient = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 241, 0));
        Player[] party = [owner, recipient,
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 242, 0)),
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 243, 0))];
        var combat = new CombatState(boss ? ModelDb.Encounter<KnowledgeDemonBoss>().ToMutable() : null,
            RunState.CreateForTest(party, seed: "TOUCH-OPPORTUNITY", ascensionLevel: 10)) { RoundNumber = 1 };
        foreach (var p in party)
        { p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3; p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(3); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); }
        var strike = combat.CreateCard<StrikeIronclad>(owner); var bash = combat.CreateCard<Bash>(owner);
        if (!empty) { owner.PlayerCombatState!.Hand.AddInternal(strike); owner.PlayerCombatState.Hand.AddInternal(bash); }
        var potion = ModelDb.Potion<TouchOfInsanity>().ToMutable();
        var retained = ModelDb.Potion<TouchOfInsanity>().ToMutable();
        owner.SubtractFromMaxPotionCount(owner.MaxPotionCount - 2);
        owner.AddPotionInternal(potion, silent: true); owner.AddPotionInternal(retained, silent: true);
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        var type = AccessTools.Inner(typeof(TouchRuntimeFixtureSnippet), "BoardState");
        var legacy = Activator.CreateInstance(type, Any, null, [combat, owner, recipient, enemy, potion, strike, bash, retained], null)!;
        return new(combat, owner, recipient, enemy, potion, retained, strike, bash, legacy);
    }
    private static IDisposable Scope(State b) => (IDisposable)Activator.CreateInstance(
        AccessTools.Inner(typeof(TouchRuntimeFixtureSnippet), "Scope"), Any, null, [b.Legacy, null], null)!;
    private static object? Call(object value, string name, params object?[] args) => AccessTools.Method(value.GetType(), name).Invoke(value, args);
    private static object? Get(object value, string name) => AccessTools.Property(value.GetType(), name).GetValue(value);
    private static void Check(bool value, string detail)
    { if (!value) throw new InvalidOperationException("Touch opportunity: " + detail); }
    private sealed class FallbackSelector(CardModel selected) : ICardSelector
    {
        internal int Calls;
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> cards, int minSelect, int maxSelect)
        { Calls++; return Task.FromResult<IEnumerable<CardModel>>([selected]); }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> cards,
            IReadOnlyList<CardRewardAlternative> alternatives) => throw new NotSupportedException();
    }
}
