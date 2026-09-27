using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class TemporalResourceScenarios
{
    // R2 2026-09-27: 20 deliberate production faults each exited 1, then restored.
    // Commands/exits: work/strength-20260927-v8/r2-results.json. Failure text:
    // Resource timing must not hold Sidestep for lacking a current payoff.
    // Resource timing must not hold ChargeBattery for lacking a current payoff.
    // Resource timing must not hold Relax for lacking a current payoff.
    // Resource timing must not hold Outmaneuver for lacking a current payoff.
    // Resource timing must not hold Adrenaline for lacking a current payoff.
    // Resource timing must not hold Apotheosis for lacking a current payoff.
    // Kill must play a lone payable Sidestep at zero energy.
    // Defend must play a lone payable Sidestep at zero energy.
    // Growth must play a lone payable Sidestep at zero energy.
    // Tactical must play a lone payable Sidestep at zero energy.
    // Predator must draw seven cards on the next turn.
    // System.NullReferenceException: Object reference not set to an instance of an object.
    // Scavenge must not finance cards in the current hand.
    // Tactical evaluation must credit scheduled resources.
    // Two-hand drafting must value Sidestep when the next hand needs its energy.
    // Next-turn team draw must be worth more when allies can use the additional cards.
    // Sidestep upgrades must value the extra next-turn energy.
    // Sidestep must pay its printed amount at the next energy reset.
    // Scheduled energy must be consumed once, not become permanent energy.
    // Next-turn draw must expire after one hand.
    internal static void Run()
    {
        new HarmonyLib.Harmony("coopbots.test.temporal.presentation").Patch(
            HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(TemporalResourceScenarios), nameof(FormatText)));
        var failures = new List<string>();
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var combat = new CombatState(runState: RunState.CreateForTest([p], seed: "FUTURE-RESOURCES"));
        p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat(); foe.SetMaxHpInternal(500); foe.SetCurrentHpInternal(500);
        foe.Monster.SetMoveImmediate(new MoveState("BUFF", _ => Task.CompletedTask, new BuffIntent()), true);
        void Hand(CardModel card)
        {
            foreach (var old in p.PlayerCombatState.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(old);
            p.PlayerCombatState.Hand.AddInternal(card);
        }
        var side = combat.CreateCard<Sidestep>(p);
        var scavenge = combat.CreateCard<Scavenge>(p);
        var glow = combat.CreateCard<Glow>(p);
        var predator = combat.CreateCard<Predator>(p);
        Check(CardProfile.Of(side) is { Energy: 0, NextTurnEnergy: 1 }, "Sidestep must grant future energy, never current energy.");
        Check(CardProfile.Of(scavenge) is { Energy: 0, NextTurnEnergy: 2 }, "Scavenge must not finance cards in the current hand.");
        Check(CardProfile.Of(glow) is { Draw: 1, NextTurnDraw: 1 }, "Glow must retain both its immediate and next-turn draw.");
        Check(CardProfile.Of(predator) is { Draw: 0, NextTurnDraw: 2 }, "Predator's two cards belong to the next hand.");
        Check(BuildValue.UpgradeDelta(side, p) is { Total: > 0, Reason: var why } && why.Contains("next-energy+1"),
            "Sidestep upgrades must value the extra next-turn energy.");
        foreach (var card in new CardModel[] { side, combat.CreateCard<ChargeBattery>(p),
            combat.CreateCard<Relax>(p), combat.CreateCard<Outmaneuver>(p), combat.CreateCard<Adrenaline>(p), combat.CreateCard<Apotheosis>(p) })
        {
            Hand(card);
            Check(!RolloutRun.ShouldHold(KernelSession.Capture(combat), [p], p, card),
                $"Resource timing must not hold {card.GetType().Name} for lacking a current payoff.");
        }
        Hand(side);
        p.PlayerCombatState.LoseEnergy(3);
        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var rollout = KernelRollout.Run(KernelSession.Capture(combat), [p], [p], new RolloutOptions(policy, MaxSteps: 1));
            Check(rollout.Actions.FirstOrDefault()?.Card is Sidestep, $"{policy} must play a lone payable Sidestep at zero energy.");
        }
        p.PlayerCombatState.GainEnergy(3);
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
        for (var i = 0; i < 25; i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        foreach (var upgraded in new[] { false, true })
        {
            var scheduled = combat.CreateCard<Sidestep>(p); if (upgraded) scheduled.UpgradeInternal(); Hand(scheduled);
            var root = KernelSession.Capture(combat); var before = root.StateText(p);
            var branch = root.Fork();
            if (!branch.Play(scheduled, null, out var error)) throw new Exception("Sidestep fixture play: " + error);
            Check(branch.Energy(p) == 3, "Scheduled energy must not be spendable before the turn ends.");
            Check(branch.PersistentValue(p, 0, 0) > 0, "Tactical evaluation must credit scheduled resources.");
            if (!branch.EndTurn(p, 10, out error)) throw new Exception("Sidestep fixture boundary: " + error);
            Check(branch.Energy(p) == (upgraded ? 5 : 4), "Sidestep must pay its printed amount at the next energy reset.");
            if (!branch.EndTurn(p, 10, out error)) throw new Exception("Sidestep second boundary: " + error);
            Check(branch.Energy(p) == 3, "Scheduled energy must be consumed once, not become permanent energy.");
            Check(root.StateText(p) == before, "Scheduled-resource rollouts must leave the source board untouched.");
        }
        Hand(predator);
        var drawBranch = KernelSession.Capture(combat);
        if (!drawBranch.Play(predator, foe, out var boundary)) throw new Exception("Predator fixture: " + boundary);
        Check(drawBranch.Hand(p).Count == 0, "Predator must not draw its next-turn cards immediately.");
        if (!drawBranch.EndTurn(p, 10, out boundary)) throw new Exception("Predator boundary: " + boundary);
        Console.WriteLine($"PROBE predator next hand={drawBranch.Hand(p).Count}");
        Check(drawBranch.Hand(p).Count == 7, "Predator must draw seven cards on the next turn.");
        if (!drawBranch.EndTurn(p, 10, out boundary)) throw new Exception("Predator second boundary: " + boundary);
        Check(drawBranch.Hand(p).Count == 5, "Next-turn draw must expire after one hand.");

        foreach (var c in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(c);
        for (var i = 0; i < 12; i++) p.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var original = p.Deck.Cards.ToArray();
        var tight = CounterfactualDraft.Compare(original, side, 2)!;
        var rich = CounterfactualDraft.Compare(original, side, 9)!;
        var repeat = CounterfactualDraft.Compare(original, side, 2)!;
        Check(tight.Delta > 0 && tight.Delta > rich.Delta,
            "Two-hand drafting must value Sidestep when the next hand needs its energy.");
        Check(tight == repeat && p.Deck.Cards.SequenceEqual(original),
            "Two-hand drafting must remain deterministic without changing the live deck.");
        var team = Enumerable.Range(10, 2).Select(id => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)id)).ToArray();
        var teamRun = RunState.CreateForTest(team, seed: "TEAM-FUTURE-DRAW");
        foreach (var member in team) { member.MaxEnergy = 3; member.Creature.SetCurrentHpInternal(member.Creature.MaxHp); }
        for (var i = 0; i < 12; i++)
        {
            team[0].Deck.AddInternal(teamRun.CreateCard<StrikeIronclad>(team[0]));
            team[1].Deck.AddInternal(teamRun.CreateCard<Wound>(team[1]));
        }
        var plot = teamRun.CreateCard<Plot>(team[0]);
        var noPayoff = CounterfactualDraft.Compare(team[0].Deck.Cards.ToArray(), plot, 3)!;
        foreach (var c in team[1].Deck.Cards.ToArray()) team[1].Deck.RemoveInternal(c);
        for (var i = 0; i < 12; i++) team[1].Deck.AddInternal(teamRun.CreateCard<Anger>(team[1]));
        var allyPayoff = CounterfactualDraft.Compare(team[0].Deck.Cards.ToArray(), plot, 3)!;
        Check(allyPayoff.Delta > noPayoff.Delta,
            "Next-turn team draw must be worth more when allies can use the additional cards.");
        Console.WriteLine($"PROBE future draft tight={tight.Delta:F3} rich={rich.Delta:F3} threeComparisonsMs={timer.ElapsedMilliseconds}");
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
        Console.WriteLine("PASS: resource timing drives all rollout policies, two real turn boundaries, drafting and upgrades.");
    }
    private static bool FormatText(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
    { __result = __instance.LocEntryKey; return false; }
}
