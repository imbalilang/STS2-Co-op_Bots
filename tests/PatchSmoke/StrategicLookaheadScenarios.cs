using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class StrategicLookaheadScenarios
{
    // R2, 2026-10-03: each production mutation below exited 1; all restored.
    // Evidence: work/strategic-lookahead-20261003/r2-results.json and r2-*.log.
    // Bypass native AB/BA ordering:
    // System.Exception: Strategic lookahead: Kill must apply debuffs, multiply their duration, then cash out Strength before attacking
    // Disable investment trials:
    // System.Exception: Strategic lookahead: native future triggers must determine whether Rupture earns its activation cost
    // Zero future debuff value:
    // System.Exception: Strategic lookahead: long-fight debuff duration must retain value beyond the current hand without a cash-out card
    // Discard settled investment routes:
    // System.Exception: Strategic lookahead: settled investment routes must survive leaving the expandable frontier
    // Keep only one distinct power opening:
    // System.Exception: Strategic lookahead: different powers must survive the opening shortlist independently
    // Restore candidate-first rollout scheduling:
    // System.Exception: Strategic lookahead: the first two complete rollouts must fund two different openings
    // Zero Panache's native trigger damage:
    // System.Exception: Strategic lookahead: Panache must start before the five-card chain
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Strategic lookahead: " + why); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        new Harmony("coopbots.strategic-lookahead").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(StrategicLookaheadScenarios), nameof(Format)));
        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (party, combat, enemy) = Board();
            ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(enemy, 1, true);
            Add(combat, party[0], ModelDb.Card<MoltenFist>());
            Add(combat, party[0], ModelDb.Card<Dominate>());
            Add(combat, party[0], ModelDb.Card<Anger>());
            Add(combat, party[1], ModelDb.Card<Taunt>());
            Add(combat, party[1], ModelDb.Card<Thunderclap>());
            var path = KernelRollout.Run(KernelSession.Capture(combat), party, party, new(policy, MaxSteps: 5));
            var cards = path.Actions.Select(a => a.Card).ToArray();
            Console.WriteLine($"STRATEGIC ORDER {policy}: " + string.Join(',', cards.Select(c => c?.Id.Entry)));
            var molten = Array.FindIndex(cards, c => c is MoltenFist);
            var dominate = Array.FindIndex(cards, c => c is Dominate);
            Check(molten > Array.FindIndex(cards, c => c is Taunt) && molten > Array.FindIndex(cards, c => c is Thunderclap)
                && Array.FindIndex(cards, c => c is Taunt) >= 0 && Array.FindIndex(cards, c => c is Thunderclap) >= 0
                && dominate > molten && Array.FindIndex(cards, c => c is Anger) > dominate,
                policy + " must apply debuffs, multiply their duration, then cash out Strength before attacking");
        }
        foreach (var trigger in new[] { true, false })
        {
            var (party, combat, enemy) = Board();
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 7, true);
            enemy.SetCurrentHpInternal(220);
            var p = party[0]; p.MaxEnergy = 1; p.PlayerCombatState!.LoseEnergy(2);
            Add(combat, p, ModelDb.Card<Rupture>()); Add(combat, p, ModelDb.Card<StrikeIronclad>());
            Add(combat, p, trigger ? ModelDb.Card<Bloodletting>() : ModelDb.Card<DefendIronclad>(), false);
            for (var i = 0; i < 5; i++) Add(combat, p, ModelDb.Card<Anger>(), false);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var debugRoot = KernelSession.Capture(combat);
            var activated = debugRoot.CardBranches(debugRoot.Hand(p).OfType<Rupture>().Single(), null, 1).Single().State;
            var trial = new RolloutRun(activated, party, party, new(RolloutPolicyKind.Growth, MaxRounds: 2) { InvestmentLookahead = false });
            while (!trial.Done) trial.Step();
            Console.WriteLine($"STRATEGIC TRIAL {trigger} value={KernelTacticalPolicy.ActionValue(debugRoot, trial.State, party, party):F2} strength={trial.State.Power<StrengthPower>(p.Creature)} stop={trial.Result!.StopReason} actions=" + string.Join(',',trial.Result.Actions.Select(a=>a.Card?.Id.Entry??"END")));
            var line = KernelRollout.Run(KernelSession.Capture(combat), party, party, new(RolloutPolicyKind.Tactical, MaxSteps: 1));
            Console.WriteLine($"STRATEGIC RUPTURE trigger={trigger} first={line.Actions.FirstOrDefault()?.Card?.Id.Entry} ms={clock.ElapsedMilliseconds}");
            Check(trigger ? line.Actions.FirstOrDefault()?.Card is Rupture : line.Actions.FirstOrDefault()?.Card is StrikeIronclad,
                "native future triggers must determine whether Rupture earns its activation cost");
            if (trigger)
            {
                var result = KernelTournament.Run(debugRoot.Fork(), party, party,
                    new(IncludePotions: false, ReserveMs: 0, EnableJointSearch: true), 5000);
                Console.WriteLine($"STRATEGIC TOURNAMENT first={result.FirstAction?.Card?.Id.Entry} victory={result.Record?.Victory} {result.JointDiagnostics}");
                Check(result.Record is { Verified: true, Victory: true }
                    && result.Script!.TakeWhile(a => !a.EndTurn).Any(a => a.Card is Rupture),
                    "the production joint tournament must deploy and realize the Rupture investment");
                var replay = debugRoot.Fork();
                foreach (var action in result.Script!) Check(replay.ReplayPlanned(action, 80, out _), "investment script must replay natively");
                Check(replay.HasWon, "the selected investment script must reach the predicted victory");
            }
        }
        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (durationParty, durationCombat, target) = Board();
            ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(target, 1, true);
            Add(durationCombat, durationParty[0], ModelDb.Card<MoltenFist>());
            Add(durationCombat, durationParty[1], ModelDb.Card<Taunt>());
            for (var i = 0; i < 5; i++) Add(durationCombat, durationParty[0], ModelDb.Card<StrikeIronclad>(), false);
            var duration = KernelRollout.Run(KernelSession.Capture(durationCombat), durationParty, durationParty, new(policy, MaxSteps: 2));
            Check(duration.Actions[0].Card is Taunt && duration.Actions[1].Card is MoltenFist,
                "long-fight debuff duration must retain value beyond the current hand without a cash-out card");
        }
        var covered = 0;
        foreach (var upgrade in new[] { false, true })
        {
            var (panParty, panCombat, foe) = Board(); var owner = panParty[0];
            var panache = Add(panCombat, owner, ModelDb.Card<Panache>()); if (upgrade) panache.UpgradeInternal();
            for (var i = 0; i < 5; i++) Add(panCombat, owner, ModelDb.Card<Shiv>());
            var panRoot = KernelSession.Capture(panCombat);
            var line = KernelRollout.Run(panRoot.Fork(), panParty, panParty, new(RolloutPolicyKind.Tactical, MaxSteps: 6));
            Console.WriteLine($"STRATEGIC PANACHE upgrade={upgrade}: " + string.Join(',', line.Actions.Select(a => a.Card?.Id.Entry)));
            Check(line.Actions.FirstOrDefault()?.Card is Panache, "Panache must start before the five-card chain");
            var resolved = panRoot.Fork();
            foreach (var action in line.Actions) Check(resolved.ReplayPlanned(action, 80, out _), "Panache sequence must replay");
            Check(resolved.Hp(foe) == 400 - 20 - (upgrade ? 14 : 10), "Panache must realize its native five-card damage trigger");
            var result = KernelTournament.Run(panRoot.Fork(), panParty, panParty,
                new(IncludePotions: false, ReserveMs: 0, EnableJointSearch: true), 3000);
            Check(result.FirstAction?.Card is Panache, "the production tournament must open with Panache before spending its triggers");
            foe.SetCurrentHpInternal(4);
            var lethal = KernelRollout.Run(KernelSession.Capture(panCombat), panParty, panParty,
                new(RolloutPolicyKind.Tactical, MaxSteps: 1));
            Check(lethal.Actions.FirstOrDefault()?.Card is Shiv, "an immediate lethal must still beat Panache setup");
        }
        var (threatParty, threatCombat, threatEnemy) = Board(); var threatened = threatParty[0];
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(threatEnemy, 4, true);
        threatened.MaxEnergy = 1; threatened.PlayerCombatState!.LoseEnergy(2); threatened.Creature.SetCurrentHpInternal(4);
        Add(threatCombat, threatened, ModelDb.Card<Rupture>());
        Add(threatCombat, threatened, ModelDb.Card<DefendIronclad>());
        Add(threatCombat, threatened, ModelDb.Card<Bloodletting>(), false);
        for (var i = 0; i < 5; i++) Add(threatCombat, threatened, ModelDb.Card<Anger>(), false);
        var safe = KernelRollout.Run(KernelSession.Capture(threatCombat), threatParty, threatParty,
            new(RolloutPolicyKind.Tactical, MaxSteps: 1));
        Check(safe.Actions.FirstOrDefault()?.Card is DefendIronclad, "future investment must not displace survival this turn");
        foreach (var character in new CharacterModel[] { ModelDb.Character<Ironclad>(), ModelDb.Character<Silent>() })
        foreach (var template in character.CardPool.AllCards.Where(c => c.Type == CardType.Power))
        foreach (var upgraded in new[] { false, true })
        {
            var (party, combat, _) = Board(); var p = party[0];
            var power = Add(combat, p, template); if (upgraded) power.UpgradeInternal();
            Add(combat, p, ModelDb.Card<StrikeIronclad>()); Add(combat, p, ModelDb.Card<DefendIronclad>());
            foreach (var c in new CardModel[] { ModelDb.Card<Bloodletting>(), ModelDb.Card<BladeDance>(),
                ModelDb.Card<Bash>(), ModelDb.Card<StrikeIronclad>(), ModelDb.Card<DefendIronclad>(), ModelDb.Card<Anger>() }) Add(combat, p, c, false);
            var root = KernelSession.Capture(combat); var stamp = root.StateText(p);
            var probe = new KernelInvestmentProbe(root, party, party, new());
            while (!probe.Done) probe.Step();
            Check(probe.CompletedPowerTrials == 2, $"{power.Id.Entry}{(upgraded ? "+" : "")}: both investment continuations must reach their native horizon");
            Check(root.StateText(p) == stamp, "investment trials must leave the captured root unchanged");
            covered++;
        }
        Console.WriteLine($"STRATEGIC COVERAGE {covered} normal/upgraded class powers completed both native investment trials");

        var (multiParty, multiCombat, _) = Board();
        var a = Add(multiCombat, multiParty[0], ModelDb.Card<Rupture>());
        var b = Add(multiCombat, multiParty[0], ModelDb.Card<Footwork>());
        Add(multiCombat, multiParty[0], ModelDb.Card<StrikeIronclad>());
        Add(multiCombat, multiParty[0], ModelDb.Card<DefendIronclad>());
        foreach (var card in new CardModel[] { ModelDb.Card<Bash>(), ModelDb.Card<PommelStrike>(),
            ModelDb.Card<Anger>(), ModelDb.Card<Thunderclap>(), ModelDb.Card<StrikeSilent>() }) Add(multiCombat, multiParty[1], card);
        var multi = KernelSession.Capture(multiCombat);
        var candidates = KernelTournament.FirstActions(multi, multiParty, 4, new(IncludePotions: false));
        Check(candidates.Any(x => x.Card == a) && candidates.Any(x => x.Card == b), "different powers must survive the opening shortlist independently");
        using (var beam = new KernelTeamSearch(multi, multiParty, s => -s.Enemies.Sum(s.Hp),
            new(Depth: 1, Width: 6, MaxNodes: 50) { IncludePotions = false, Deduplicate = false, PreserveSetupRoutes = true, CaptureReplayStates = false }))
        {
            while (!beam.Advance(TimeSpan.FromMilliseconds(5), () => true)) { }
            Check(beam.ContinuationPaths.Any(path => path.Any(x => x.Card == a))
                && beam.ContinuationPaths.Any(path => path.Any(x => x.Card == b)), "beam pruning must retain distinct power investments for continuation");
        }
        var (closedParty, closedCombat, _) = Board(); var closedOwner = closedParty[0];
        closedOwner.PlayerCombatState!.LoseEnergy(2);
        var closedA = Add(closedCombat, closedOwner, ModelDb.Card<Rupture>());
        var closedB = Add(closedCombat, closedOwner, ModelDb.Card<Footwork>());
        Add(closedCombat, closedOwner, ModelDb.Card<StrikeIronclad>());
        using (var beam = new KernelTeamSearch(KernelSession.Capture(closedCombat), closedParty, s => -s.Enemies.Sum(s.Hp),
            new(Depth: 8, Width: 8, MaxNodes: 1000, IncludeEndTurns: true, MaxRounds: 1)
            { IncludePotions = false, Deduplicate = false, PreserveSetupRoutes = true, CaptureReplayStates = false }))
        {
            while (!beam.Advance(TimeSpan.FromMilliseconds(5), () => true)) { }
            Check(beam.ContinuationPaths.Any(path => path.Any(x => x.Card == closedA))
                && beam.ContinuationPaths.Any(path => path.Any(x => x.Card == closedB)),
                "settled investment routes must survive leaving the expandable frontier");
        }
        var (fairParty, fairCombat, fairEnemy) = Board(); fairEnemy.SetCurrentHpInternal(6);
        Add(fairCombat, fairParty[0], ModelDb.Card<StrikeIronclad>());
        Add(fairCombat, fairParty[0], ModelDb.Card<Anger>());
        var fair = new TournamentRun(KernelSession.Capture(fairCombat), fairParty, fairParty,
            new(TopK: 2, IncludePotions: false, ReserveMs: 0), 10000);
        while (!fair.Done && fair.Rollouts < 2) fair.Advance(TimeSpan.FromTicks(1));
        Check(fair.EvaluatedOpenings == 2, "the first two complete rollouts must fund two different openings");
        Console.WriteLine("PASS: strategic investment trials and native order comparisons cover delayed triggers and debuff multipliers.");
    }

    private static CardModel Add(CombatState combat, Player p, CardModel card, bool hand = true)
    {
        var result = combat.CreateCard(card, p);
        (hand ? p.PlayerCombatState!.Hand : p.PlayerCombatState!.DrawPile).AddInternal(result);
        return result;
    }
    private static (Player[], CombatState, Creature) Board()
    {
        var party = Enumerable.Range(1, 2).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)i)).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "STRATEGIC-LOOKAHEAD"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var e = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(400); e.SetCurrentHpInternal(400);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat, e);
    }
}
