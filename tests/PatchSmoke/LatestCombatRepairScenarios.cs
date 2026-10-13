using System.Diagnostics;
using System.Reflection;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Controlled native hands drawn from RLAE8Y8MA4YH, not a historical replay.
// R2 2026-10-07: 16 production faults exited 1, each restored run exited 0.
// Commands, raw logs and mutations: work/decision-repair-20261007/card-play-r2-results.json.
// System.Exception: Latest combat repairs: held Tracking must precede its native damage payoff in a production rollout
// System.Exception: Latest combat repairs: Cruelty-first must retain the PaperPhrog-adjusted native payoff
// System.Exception: Latest combat repairs: Rage-first must generate native attack-trigger block
// System.Exception: Latest combat repairs: Footwork-first must realize the native two-block amplification without claiming HP saved
// System.Exception: Latest combat repairs: finite reserved setup must cash Offering-drawn Cruelty before Strike with PaperPhrog
// System.Exception: Latest combat repairs: production anytime reference must enable ordering and play held Tracking first
// System.Exception: Latest combat repairs: production first wave must fund distinct Tactical, Growth, Defend and Kill continuations
// System.Exception: Latest combat repairs: every production first-wave continuation must enable bounded ordering
// System.Exception: Latest combat repairs: an earlier seat's native play must preserve the fourth seat's own pair budgets
// System.Exception: Latest combat repairs: joint production schedule must open defence and growth before cheap path permutations
// System.Exception: Latest combat repairs: Tactical must reserve fixed-budget continuation time for both Defend and Growth
// System.Exception: Latest combat repairs: production joint funding must recognize its completed verified tournament reference
// System.Exception: Latest combat repairs: production tournament must preserve Ambergris when 16 healing ends in the same-turn kill
// System.Exception: Latest combat repairs: tournament enemy evidence must describe the state before each action
// System.Exception: Latest combat repairs: tournament living-seat evidence must describe the state before each action
// System.Exception: Latest combat repairs: a catching-up extra turn must refresh its finite pair budget by side epoch
// Additional R2 2026-10-07: explicit-policy-r2.py -> RED exit 1 / restored GREEN exit 0.
// Raw command/output: work/decision-repair-20261007/explicit-policy-r2-results.json.
// System.Exception: Latest combat repairs: a configured single-policy continuation must preserve its explicit investment lookahead
// Additional LongValue/order/reference R2 (2026-10-07): eight production faults
// exited 1 at the assertions below; exact three-file restoration exited 0.
// Commands, mutations, hashes and raw traces: work/decision-repair-20261007/long-order-r2-results.json.
// The stale-branch fault triggers the production Tracking-reference guard, not
// the reserved-engine assertion; the first expected-message mismatch is retained.
// System.Exception: Latest combat repairs: a replaced reserved engine must still compare allied native setup with the correct proposed after-state
// System.Exception: Latest combat repairs: production anytime reference must enable ordering and play held Tracking first
// System.Exception: Strategic lookahead: Kill must apply debuffs, multiply their duration, then cash out Strength before attacking
// System.Exception: Combat quality: exhaust must realize payable defence before burning the hand
// System.Exception: Combat quality: Growth must preserve the paid block and all five Shivs before discarding
// System.Exception: Combat quality: late-game joint reference must complete
// System.Exception: Latest combat repairs: protected joint reference itself must retain a complete native script and aligned evidence
// System.Exception: Latest combat repairs: protected joint reference evidence must describe each native pre-action roster
internal static class LatestCombatRepairScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.latest-combat-repair");
        harmony.Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LatestCombatRepairScenarios), nameof(Format)));
        try
        {
            Ordering(); ReservedEngineDependency(); DrawnEngine(); ReferenceAndCheapWiring(); FairSeatBudget(); JointFunding(); AmbergrisValue(); ScriptEvidence(); ExtraTurnBudget();
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void Ordering()
    {
        var (party, c, e) = Board(1, 2000, 4); var p = party[0];
        Pow<StrengthPower>(p.Creature, 1); Pow<VulnerablePower>(e, 5); Pow<WeakPower>(e, 2);
        var slice = Hand<Slice>(p, c); var tracking = Hand<Tracking>(p, c);
        var root = KernelSession.Capture(c);
        var run = new RolloutRun(root.Fork(), party, party, new(MaxSteps: 2)
            { InvestmentLookahead = false, SequenceLookahead = true });
        while (!run.Done) run.Step();
        Console.WriteLine("PROBE tracking " + string.Join(",", run.Result!.Actions.Select(a => a.Card?.Id.Entry ?? "END"))
            + " stop=" + run.Result.StopReason + " damage=" + (root.Hp(e) - run.State.Hp(e)));
        Check(run.Result!.Actions.First().Card == tracking,
            "held Tracking must precede its native damage payoff in a production rollout");
        Check(root.Hp(e) - run.State.Hp(e) == 15 && root.Hp(p.Creature) == run.State.Hp(p.Creature),
            "Tracking-first must realize 15 native damage without changing its HP cost");

        var (ironParty, ironCombat, ironEnemy) = Board(1, 2000, 2); var iron = ironParty[0];
        iron.AddRelicInternal(ModelDb.Relic<PaperPhrog>().ToMutable());
        Pow<StrengthPower>(iron.Creature, 1); Pow<VulnerablePower>(ironEnemy, 7);
        Hand<StrikeIronclad>(iron, ironCombat); var cruelty = Hand<Cruelty>(iron, ironCombat, true);
        var ironRun = new RolloutRun(KernelSession.Capture(ironCombat), ironParty, ironParty,
            new(MaxSteps: 2) { InvestmentLookahead = false });
        while (!ironRun.Done) ironRun.Step();
        Check(ironRun.Result!.Actions.First().Card == cruelty && 2000 - ironRun.State.Hp(ironEnemy) == 15,
            "Cruelty-first must retain the PaperPhrog-adjusted native payoff");

        var (rageParty, rageCombat, _) = Board(1, 2000, 3); var owner = rageParty[0];
        Hand<StrikeSilent>(owner, rageCombat); var rage = Hand<Rage>(owner, rageCombat);
        var rageRun = new RolloutRun(KernelSession.Capture(rageCombat), rageParty, rageParty,
            new(MaxSteps: 2) { InvestmentLookahead = false });
        while (!rageRun.Done) rageRun.Step();
        Check(rageRun.Result!.Actions.First().Card == rage && rageRun.State.Block(owner.Creature) == 3,
            "Rage-first must generate native attack-trigger block");
        var (dexParty, dexCombat, dexEnemy) = Board(1, 2000, 2); var silent = dexParty[0];
        Pow<StrengthPower>(dexEnemy, 15);
        Hand<CloakAndDagger>(silent, dexCombat); var footwork = Hand<Footwork>(silent, dexCombat);
        var dexRun = new RolloutRun(KernelSession.Capture(dexCombat), dexParty, dexParty,
            new(MaxSteps: 2) { InvestmentLookahead = false });
        while (!dexRun.Done) dexRun.Step();
        Check(dexRun.Result!.Actions.First().Card == footwork && dexRun.State.Block(silent.Creature) == 8,
            "Footwork-first must realize the native two-block amplification without claiming HP saved");
        Console.WriteLine("PASS: production rollout realizes Tracking, Cruelty, Rage and Footwork ordering payoffs.");
    }

    private static void ReservedEngineDependency()
    {
        // A native duration multiplier exposes the dependency after an own
        // engine wins the reserved comparison. It has no printed PowerVar,
        // so the broad production comparison must still examine that ally.
        var (party, c, e) = Board(2, 2000, 3); var p = party[0];
        Pow<VulnerablePower>(e, 3);
        var anger = Hand<Anger>(p, c); anger.DynamicVars["Damage"].BaseValue = 80;
        var dominate = Hand<Dominate>(p, c); var molten = Hand<MoltenFist>(party[1], c);
        for (var i = 0; i < 5; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c);
        var proposed = new KernelTeamSearch.Action(p, anger, e, CardStateKey: root.CardStateKey(anger));
        var first = root.CardBranches(anger, e, 1).Single(b => b.Boundary.Length == 0);
        var caller = new RolloutRun(root.Fork(), party, party,
            new(RolloutPolicyKind.Tactical, MaxSteps: 3) { InvestmentLookahead = false });
        var alternatives = Field<List<(CardModel Card, Creature? Target, string Key, int Occurrence, double Score)>>(caller, "candidates");
        foreach (var card in new[] { anger, dominate, molten })
            alternatives.Add((card, e, root.CardStateKey(card), 0, card == anger ? 100 : 0));
        var ordered = (KernelTeamSearch.Action)Invoke(caller, "Order", proposed, first)!;
        Check(ordered.Card == molten,
            "a replaced reserved engine must still compare allied native setup with the correct proposed after-state");
        var line = KernelRollout.Run(root.Fork(), party, party,
            new(RolloutPolicyKind.Kill, MaxSteps: 3) { InvestmentLookahead = false });
        Console.WriteLine("PROBE reserved engine " + string.Join(",", line.Actions.Select(a => a.Card?.Id.Entry ?? "END")));
        Check(line.Actions.Select(a => a.Card).SequenceEqual(new CardModel[] { molten, dominate, anger }),
            "production rollout must realize native allied setup before the reserved engine and attack");
        Console.WriteLine("PASS: a reserved engine replacement retains native allied setup and clears the prior action branch.");
    }

    private static void DrawnEngine()
    {
        var (party, c, e) = Board(4, 2000, 3); var p = party[0];
        p.AddRelicInternal(ModelDb.Relic<PaperPhrog>().ToMutable());
        Pow<StrengthPower>(p.Creature, 1); Pow<VulnerablePower>(e, 7);
        var initialStrike = Hand<StrikeIronclad>(p, c); var offering = Hand<Offering>(p, c);
        var cruelty = c.CreateCard<Cruelty>(p); cruelty.UpgradeInternal();
        var drawnStrike = c.CreateCard<StrikeIronclad>(p);
        p.PlayerCombatState!.DrawPile.AddInternal(cruelty);
        p.PlayerCombatState.DrawPile.AddInternal(drawnStrike);
        p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<Impervious>(p));
        var run = new RolloutRun(KernelSession.Capture(c), party, party,
            new(MaxSteps: 4) { InvestmentLookahead = false });
        run.Step();
        Check(!run.State.Hand(p).Contains(initialStrike),
            "drawn-engine fixture must first execute the pre-Offering native attack");
        // Isolate the already-spent ordinary allowance; reserved probes remain
        // finite. This is a controlled mid-turn budget, not a historical replay.
        Field<Dictionary<Player, int>>(run, "orderPairs")[p] = 0;
        run.Step();
        Check(!run.State.Hand(p).Contains(offering) && run.State.Hand(p).Contains(cruelty)
                && run.State.Hand(p).Contains(drawnStrike),
            "native Offering must draw both Cruelty and the later attack into the same turn");
        while (!run.Done) run.Step();
        var cards = run.Result!.Actions.Where(a => a.Card is not null).Select(a => a.Card).ToArray();
        Check(cards.Length == 4 && cards[2] == cruelty && cards[3] == drawnStrike
                && 2000 - run.State.Hp(e) == 27 && run.State.Hp(p.Creature) == 74,
            "finite reserved setup must cash Offering-drawn Cruelty before Strike with PaperPhrog");
        Console.WriteLine("PASS: Offering-drawn Cruelty retains a finite late-turn pair allowance and its native payoff.");
    }

    private static void ReferenceAndCheapWiring()
    {
        var (party, c, e) = Board(1, 2000, 4); var p = party[0]; Pow<WeakPower>(e, 2);
        Hand<Slice>(p, c); var tracking = Hand<Tracking>(p, c);
        Hand<DefendSilent>(p, c); Hand<StrikeSilent>(p, c);
        var root = KernelSession.Capture(c);
        var tournament = new TournamentRun(root, party, party,
            new(TopK: 8, IncludePotions: false, ReserveMs: 0, EnableJointSearch: true,
                Rollout: new(MaxSteps: 12) { InvestmentLookahead = false, SequenceLookahead = false }), 10000);
        // Invoke the production reference path for exactly one native action.
        Invoke(tournament, "AdvanceReference", TimeSpan.FromTicks(1));
        var reference = Field<RolloutRun>(tournament, "referenceRun");
        Check(Field<RolloutOptions>(reference, "o").SequenceLookahead
                && !reference.State.Hand(p).Contains(tracking),
            "production anytime reference must enable ordering and play held Tracking first");

        var policies = new HashSet<RolloutPolicyKind>();
        var candidates = Field<List<KernelTeamSearch.Action>>(tournament, "candidates");
        for (var i = 0; i < candidates.Count; i++)
        {
            Invoke(tournament, "TryOpenNextRollout", Stopwatch.StartNew());
            var continuation = Field<RolloutRun>(tournament, "rollout");
            var options = Field<RolloutOptions>(continuation, "o");
            Check(options.SequenceLookahead,
                "every production first-wave continuation must enable bounded ordering");
            policies.Add(options.Policy);
        }
        Check(policies.Contains(RolloutPolicyKind.Tactical) && policies.Contains(RolloutPolicyKind.Growth)
                && policies.Contains(RolloutPolicyKind.Defend) && policies.Contains(RolloutPolicyKind.Kill),
            "production first wave must fund distinct Tactical, Growth, Defend and Kill continuations");
        foreach (var policy in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.KillAdds })
        foreach (var investment in new[] { true, false })
        {
            var explicitRun = new TournamentRun(root, party, party,
                new(IncludePotions: false, ReserveMs: 0, UsePolicyPortfolio: false,
                    Rollout: new(policy, MaxSteps: 12) { InvestmentLookahead = investment }), 10000);
            Invoke(explicitRun, "TryOpenNextRollout", Stopwatch.StartNew());
            var configured = Field<RolloutOptions>(Field<RolloutRun>(explicitRun, "rollout"), "o");
            Check(configured.Policy == policy && configured.InvestmentLookahead == investment,
                "a configured single-policy continuation must preserve its explicit investment lookahead");
        }
        Console.WriteLine("PASS: anytime reference and first-wave production continuations retain ordering and policy diversity.");
    }

    private static void FairSeatBudget()
    {
        var (party, c, e) = Board(4, 2000, 10);
        Pow<WeakPower>(e, 2);
        foreach (var p in party)
        {
            Hand<StrikeSilent>(p, c); Hand<DefendSilent>(p, c); Hand<DefendSilent>(p, c);
            Hand<DefendSilent>(p, c); Hand<DefendSilent>(p, c);
        }
        var big = Hand<Bludgeon>(party[0], c); var tracking = Hand<Tracking>(party[3], c);
        var run = new RolloutRun(KernelSession.Capture(c), party, party,
            new(MaxSteps: 50) { InvestmentLookahead = false });
        run.Step();
        var ordinary = Field<Dictionary<Player, int>>(run, "orderPairs");
        var setup = Field<Dictionary<Player, int>>(run, "orderSetupPairs");
        Check(run.State.Hand(party[0]).Contains(big) == false
                && ordinary[party[3]] == 2 && setup[party[3]] == 2,
            "an earlier seat's native play must preserve the fourth seat's own pair budgets");
        var assigned = Enumerable.Range(0, 4).Sum(i =>
        {
            var q = KernelSequenceOrder.SeatAllowance(4, i); return q.Ordinary + q.Setup;
        });
        Check(assigned == 16 && KernelSequenceOrder.PairLimit == 16,
            "four-seat allocation must preserve the total sixteen-pair broad budget");
        while (!run.Done) run.Step();
        var fourth = run.Result!.Actions.Where(a => a.Player == party[3] && a.Card is not null).ToArray();
        Check(fourth.Length > 0 && fourth[0].Card == tracking,
            "the fourth seat must realize held setup before its attack after earlier seats act");
        Check(run.State.Hp(e) < 2000, "fair-seat fixture must execute real native attacks");
        Console.WriteLine("PASS: native four-seat rollout preserves the fixed budget and late-seat setup.");
    }

    private static void JointFunding()
    {
        var (winningParty, winningCombat, _) = Board(1, 6, 1);
        Hand<StrikeSilent>(winningParty[0], winningCombat);
        var production = new TournamentRun(KernelSession.Capture(winningCombat), winningParty, winningParty,
            new(ReserveMs: 0, IncludePotions: false, EnableJointSearch: true), 3000);
        while (!production.Advance(TimeSpan.FromMilliseconds(5))) { }
        Check(production.Result?.Record is { Verified: true, Victory: true }
                && Field<bool>(Field<KernelJointTurnRun>(production, "joint"), "hasVerifiedReference"),
            "production joint funding must recognize its completed verified tournament reference");
        var protectedReference = Field<TournamentResult>(Field<KernelJointTurnRun>(production, "joint"), "referenceResult");
        Check(protectedReference?.Record is { Verified: true, Victory: true }
                && protectedReference.Script!.Count == protectedReference.ScriptAliveStates!.Count
                && protectedReference.Script.Count == protectedReference.ScriptEnemyStates!.Count,
            "protected joint reference itself must retain a complete native script and aligned evidence");
        var protectedReplay = KernelSession.Capture(winningCombat).Fork();
        for (var i = 0; i < protectedReference!.Script!.Count; i++)
        {
            Check(protectedReference.ScriptAliveStates![i].SequenceEqual(winningParty
                    .Where(p => protectedReplay.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(n => n))
                    && protectedReference.ScriptEnemyStates![i].Select(e => e.CombatId)
                        .SequenceEqual(protectedReplay.Enemies.Select(e => e.CombatId)),
                "protected joint reference evidence must describe each native pre-action roster");
            Check(protectedReplay.ReplayPlanned(protectedReference.Script[i], 80, out _),
                "protected joint reference itself must replay every native action from root");
        }
        Check(protectedReplay.HasTerminal && protectedReplay.HasWon == protectedReference.Record!.Victory,
            "protected joint reference itself must reproduce its recorded native ending");
        var jobs = KernelJointTurnRun.Schedule(8, true);
        Check(jobs.Length == 48 && jobs.Take(4).Select(x => x.Policy).SequenceEqual(new[]
            { RolloutPolicyKind.Kill, RolloutPolicyKind.Tactical, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth }),
            "joint production schedule must open defence and growth before cheap path permutations");
        var (party, c, _) = Board(1, 2000, 3); Hand<StrikeSilent>(party[0], c);
        var joint = new KernelJointTurnRun(KernelSession.Capture(c), party, party,
            new TournamentOptions(IncludePotions: false), 1200, hasVerifiedReference: true);
        // The deterministic native reference is protected only by the fixed
        // total deadline; subsequent comparisons share the remainder 3:1:1. The
        // existing CombatQuality late-fight guard checks completion, not a label.
        Set(joint, "paths", Enumerable.Range(0, 8)
            .Select(_ => (IReadOnlyList<KernelTeamSearch.Action>)Array.Empty<KernelTeamSearch.Action>()).ToArray());
        var killCap = (double)Invoke(joint, "JobCapMs", 0d)!;
        Set(joint, "jobIndex", 1);
        var tacticalCap = (double)Invoke(joint, "JobCapMs", 200d)!;
        Set(joint, "jobIndex", 2);
        var defendCap = (double)Invoke(joint, "JobCapMs", 800d)!;
        Set(joint, "jobIndex", 3);
        var growthCap = (double)Invoke(joint, "JobCapMs", 1000d)!;
        Check(double.IsPositiveInfinity(killCap) && tacticalCap == 600 && defendCap == 200 && growthCap == 200,
            "Tactical must reserve fixed-budget continuation time for both Defend and Growth");
        var (nativeParty, nativeCombat, _) = Board(4, 2000, 4);
        foreach (var seat in nativeParty)
        {
            Hand<StrikeSilent>(seat, nativeCombat); Hand<DefendSilent>(seat, nativeCombat);
            Hand<DeadlyPoison>(seat, nativeCombat); Hand<InfiniteBlades>(seat, nativeCombat);
        }
        var nativeJoint = new KernelJointTurnRun(KernelSession.Capture(nativeCombat), nativeParty, nativeParty,
            new(IncludePotions: false, Rollout: new(MaxSteps: 100, MaxRounds: 2)
                { InvestmentLookahead = false }), 1800, hasVerifiedReference: true);
        while (!nativeJoint.Advance(TimeSpan.FromMilliseconds(5))) { }
        Console.WriteLine("PROBE priority " + nativeJoint.Diagnostics);
        Check(Field<IReadOnlyList<KernelTeamSearch.Action>>(nativeJoint, "referencePath").Count == 1
                && Field<bool>(nativeJoint, "referenceReady")
                && !nativeJoint.Diagnostics.Contains("reference=continuation-budget")
                && nativeJoint.Diagnostics.Contains("referenceActions=1 referenceSteps="),
            "joint must finish a resolved single-action native reference before timed beam alternatives");
        var started = Field<Dictionary<RolloutPolicyKind, int>>(nativeJoint, "startedPolicies");
        Check(new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Tactical, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth }
                .All(policy => started.GetValueOrDefault(policy) > 0),
            "native joint budget must actually begin all four priority continuations with a verified reference");
        var nativeResult = nativeJoint.Result!;
        var nativeReplay = KernelSession.Capture(nativeCombat).Fork();
        foreach (var action in nativeResult.Script!)
            Check(nativeReplay.ReplayPlanned(action, 80, out _), "deterministic joint reference choices must replay natively");
        Check(nativeResult.Script.Count == nativeResult.ScriptAliveStates!.Count
                && nativeResult.Script.Count == nativeResult.ScriptEnemyStates!.Count,
            "deterministic joint reference must retain action-aligned native evidence");
        Console.WriteLine("PASS: a deterministic native reference precedes beam work and reserves distinct continuation shares under the fixed deadline.");
    }

    private static void AmbergrisValue()
    {
        // First live fight: 54/70 heals 16, then the same turn kills. The
        // unused extra-turn power must earn nothing beyond that native HP.
        var (party, c, _) = Board(1, 6, 1); var p = party[0];
        p.Creature.SetMaxHpInternal(70); p.Creature.SetCurrentHpInternal(54);
        Hand<StrikeSilent>(p, c); var potion = Bottle(p);
        var root = KernelSession.Capture(c); var text = root.StateText(p);
        var result = Tournament(root, party, potionsOnly: false);
        Check(result.Record is { Verified: true, Victory: true } && result.FirstAction?.Potion is null
                && result.Record.ResourcesConsumed == 0,
            "production tournament must preserve Ambergris when 16 healing ends in the same-turn kill");
        var drink = Tournament(root, party, potionsOnly: true);
        Check(drink.Record is { Verified: true, Victory: true } && drink.Record.PostCombatHp[0] == 70
                && drink.Record.ResourcesConsumed == 1 && drink.Record.ConsumablePreservationPremium == 20.25,
            "consumed Ambergris must carry its native reserve premium through tournament terminal capture");
        Check(!KernelPotionPolicy.Improves(result.Record, drink.Record) && root.StateText(p) == text
                && root.HoldsPotion(potion),
            "Ambergris opportunity cost must reach adoption without consuming the shared root bottle");

        foreach (var hp in new[] { 1, 80 })
        {
            var (future, futureCombat, foe) = Board(1, 12, 1); var owner = future[0];
            owner.Creature.SetCurrentHpInternal(hp); Pow<StrengthPower>(foe, 39);
            Hand<StrikeSilent>(owner, futureCombat);
            owner.PlayerCombatState!.DrawPile.AddInternal(futureCombat.CreateCard<StrikeSilent>(owner));
            Bottle(owner);
            var futureRoot = KernelSession.Capture(futureCombat);
            var baseline = KernelRollout.Run(futureRoot.Fork(), future, future,
                new(MaxSteps: 30, MaxRounds: 4) { InvestmentLookahead = false });
            var used = Tournament(futureRoot, future, potionsOnly: true);
            Console.WriteLine($"PROBE ambergris hp={hp} baseline={baseline.StopReason}/{baseline.Record.PostCombatHp[0]} used={used.Record?.Victory}/{used.Record?.CutoffReason}/{used.Record?.PostCombatHp[0]} premium={used.Record?.ConsumablePreservationPremium} resources={used.Record?.ResourcesConsumed} script="
                + string.Join(",", used.Script?.Select(a => a.Potion?.Id.Entry ?? a.Card?.Id.Entry ?? "END") ?? []));
            Check(used.Record is { Verified: true, Victory: true } && used.FirstAction?.Potion is Ambergris
                    && used.Record.ResourcesConsumed == 1 && used.Record.ConsumablePreservationPremium == 24,
                "native Ambergris extra-turn continuation must finish a charged, replayable victory");
            var usedRecord = used.Record!;
            Check(KernelPotionPolicy.Improves(baseline.Record, used.Record),
                "Ambergris reserve must still allow an actual extra turn to save a life or forty HP");
            var healed = hp == 1 ? 41 : 80;
            var replay = ReplayEvidence(futureRoot, future, used, [hp, healed, healed, healed], [12, 12, 6, 6]);
            Check(replay.HasWon && replay.Hp(owner.Creature) == usedRecord.PostCombatHp[0]
                    && replay.Power<AmbergrisPower>(owner.Creature) == 0,
                "charged Ambergris win must consume the actual extra turn before killing");
            if (hp == 1) Check(baseline is { Terminal: true, Victory: false },
                "saving-life fixture must really wipe without the potion");
            else Check(baseline is { Terminal: true, Victory: true }
                    && usedRecord.PostCombatHp[0] - baseline.Record.PostCombatHp[0] == 40,
                "extra-turn fixture must measure forty native HP saved beyond the healing cap");
        }
        Console.WriteLine("PASS: Ambergris is reserved after trivial healing and spent for native extra-turn survival payoffs.");
    }

    private static void ScriptEvidence()
    {
        var (party, c, e) = Board(2, 12, 1); party[0].Creature.SetCurrentHpInternal(1);
        Pow<StrengthPower>(e, 4);
        Hand<StrikeSilent>(party[1], c);
        party[1].PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeSilent>(party[1]));
        var root = KernelSession.Capture(c);
        var result = KernelTournament.Run(root, party, party,
            new(IncludePotions: false, ReserveMs: 0, UsePolicyPortfolio: false,
                Rollout: new(MaxSteps: 30, MaxRounds: 4) { InvestmentLookahead = false }), 10000);
        Check(result.Record is { Verified: true, Victory: true, IrreversibleLoss: 1 },
            "script-evidence fixture must cross a real native seat death before victory");
        var replay = ReplayEvidence(root, party, result);
        Check(replay.HasWon && replay.Hp(party[0].Creature) == 0,
            "tournament prefix and suffix evidence must replay through the native seat death");
        Console.WriteLine("PASS: tournament prefix/suffix evidence matches every action before extra turns and seat death.");
    }

    private static KernelSession ReplayEvidence(KernelSession root, Player[] party, TournamentResult result,
        int[]? beforeHp = null, int[]? beforeEnemyHp = null)
    {
        var script = result.Script!; var living = result.ScriptAliveStates!; var opposition = result.ScriptEnemyStates!;
        Check(script.Count == living.Count && script.Count == opposition.Count,
            "tournament script evidence arrays must stay action aligned");
        var replay = root.Fork();
        for (var i = 0; i < script.Count; i++)
        {
            var action = script[i];
            Check(living[i].SequenceEqual(party.Where(p => replay.Hp(p.Creature) > 0)
                    .Select(p => p.NetId).OrderBy(id => id)),
                "tournament living-seat evidence must describe the state before each action");
            Check(opposition[i].SequenceEqual(replay.Enemies),
                "tournament enemy evidence must describe the state before each action");
            if (action.Card is { } card && action.CardStateKey.Length > 0)
                Check(replay.CardStateKey(card) == action.CardStateKey,
                    "tournament action card key must describe the state before its native play");
            if (beforeHp is not null)
                Check(i < beforeHp.Length && replay.Hp(party[0].Creature) == beforeHp[i]
                        && replay.Enemies.Sum(replay.Hp) == beforeEnemyHp![i],
                    "Ambergris continuation must match the native HP state before every action");
            Check(replay.ReplayPlanned(action, 80, out var why), "tournament evidence script must replay: " + why);
        }
        return replay;
    }

    private static void ExtraTurnBudget()
    {
        var (party, c, _) = Board(2, 2000, 3);
        foreach (var owner in party)
        {
            Hand<StrikeSilent>(owner, c);
            for (var i = 0; i < 3; i++)
                owner.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeSilent>(owner));
        }
        var aBottle = Bottle(party[0]); var bBottle = Bottle(party[1]);
        var root = KernelSession.Capture(c);
        Check(root.UsePotion(aBottle, party[0].Creature, out _), "first extra-turn budget potion must be natively usable");
        var run = new RolloutRun(root.Fork(), party, party,
            new(MaxSteps: 100, MaxRounds: 10) { InvestmentLookahead = false });
        var ticks = 0;
        while (!run.Done && !(run.State.CanAct(party[0]) && !run.State.CanAct(party[1])) && ticks++ < 80)
            run.Step();
        Check(!run.Done && run.State.CanAct(party[0]) && !run.State.CanAct(party[1]),
            "native first extra turn must belong only to the first seat");
        var firstExtraEpoch = run.State.RoundsAdvanced;
        while (!run.Done && !(run.State.RoundsAdvanced > firstExtraEpoch
                && run.State.CanAct(party[0]) && run.State.CanAct(party[1])) && ticks++ < 80)
            run.Step();
        Check(!run.Done && run.State.CanAct(party[0]) && run.State.CanAct(party[1])
                && run.State.UsePotion(bBottle, party[1].Creature, out _),
            "native shared side after first extra turn must allow the second seat's potion");
        run.Step(); // Initialize the normal side's finite allocation by a native action.
        var normalMax = run.State.TurnNumber; var normalEpoch = run.State.RoundsAdvanced;
        Field<Dictionary<Player, int>>(run, "orderPairs")[party[1]] = 0;
        Field<Dictionary<Player, int>>(run, "orderSetupPairs")[party[1]] = 0;
        while (!run.Done && !(run.State.RoundsAdvanced > normalEpoch
                && !run.State.CanAct(party[0]) && run.State.CanAct(party[1])) && ticks++ < 80)
            run.Step();
        Check(!run.Done && run.State.TurnNumber == normalMax && run.State.RoundsAdvanced > normalEpoch,
            "second native extra turn must catch the unchanged party maximum while its side epoch advances");
        run.Step();
        Check(Field<Dictionary<Player, int>>(run, "orderPairs")[party[1]] == 4
                && Field<Dictionary<Player, int>>(run, "orderSetupPairs")[party[1]] == 4,
            "a catching-up extra turn must refresh its finite pair budget by side epoch");
        Console.WriteLine("PASS: alternating native extra turns refresh the finite budget even when party max turn is unchanged.");
    }

    private static TournamentResult Tournament(KernelSession root, Player[] party, bool potionsOnly)
        => KernelTournament.Run(root, party, party,
            new(TopK: 8, ReserveMs: 0, IncludePotions: true, UsePolicyPortfolio: false,
                IncludeEndTurns: false, Objective: KernelPotionPolicy.Objective, PotionsOnly: potionsOnly,
                Rollout: new(MaxSteps: 30, MaxRounds: 4) { InvestmentLookahead = false }), 10000);
    private static PotionModel Bottle(Player p)
    { var potion = ModelDb.Potion<Ambergris>().ToMutable(); p.AddPotionInternal(potion, silent: true); return potion; }
    private static (Player[], CombatState, Creature) Board(int count, int enemyHp, int energy)
    {
        var party = Enumerable.Range(0, count)
            .Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(10400 + i))).ToArray();
        var c = new CombatState(runState: RunState.CreateForTest(party, seed: "LATEST-COMBAT-REPAIR")) { RoundNumber = 1 };
        foreach (var p in party)
        {
            p.ResetCombatState(); c.AddPlayer(p); p.MaxEnergy = energy;
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(energy);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        }
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(enemyHp); e.SetCurrentHpInternal(enemyHp);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, c, e);
    }
    private static CardModel Hand<T>(Player p, CombatState c, bool upgraded = false) where T : CardModel
    { var card = c.CreateCard<T>(p); if (upgraded) card.UpgradeInternal(); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static void Pow<T>(Creature creature, int amount) where T : PowerModel
        => ModelDb.Power<T>().ToMutable().ApplyInternal(creature, amount, true);
    private static T Field<T>(object o, string name)
        => (T)AccessTools.Field(o.GetType(), name).GetValue(o)!;
    private static void Set(object o, string name, object value)
        => AccessTools.Field(o.GetType(), name).SetValue(o, value);
    private static object? Invoke(object o, string name, params object[] args)
        => AccessTools.Method(o.GetType(), name).Invoke(o, args);
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why)
    { if (!ok) throw new Exception("Latest combat repairs: " + why); }
}
