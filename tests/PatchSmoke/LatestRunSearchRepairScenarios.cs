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

// Controlled native hands exercising the failures observed in 1JJ0XFWWNLTC.
// This is a mechanism regression, not a reconstruction of all missing RNG.
// R2 2026-10-08: ten isolated source mutants compiled successfully, produced
// RED1, restored exact bytes, rebuilt and passed GREEN0. The private kernel also
// compiles the independently imported 418 sources plus retained support files.
// Commands, source hashes and original output: work/latest-run-repair-20261008/search-r2/r2-results.json.
// System.Exception: Latest-run search: immediate energy must remain useful for an already payable X-cost card
// System.Exception: Latest-run search: a zero-energy X play without a realized benefit must prefer passing in cheap continuations
// System.Exception: Latest-run search: energy gain must fund several individually payable cards instead of checking only one price
// System.Exception: Latest-run search: temporary skill repeat cannot spend the last energy when no payable skill remains
// System.Exception: Latest-run search: temporary skill-repeat valuation must stop after its native counter is consumed
// System.Exception: Latest-run search: the same native finite-counter timing must cover temporary attack repeat
// System.Exception: Latest-run search: Defend and Growth must start before beam creation consumes their budget
// System.Exception: Latest-run search: Tactical must share its budget even when the outer reference is unfinished
// System.Exception: Latest-run search: the anytime reference must retain native pair ordering without nested whole-hand searches
// System.Exception: Latest-run search: an independent native defence reference must buy the block that keeps the low-HP Tank alive
// Energy/Sloth extension: three new isolated production faults, build0 ->
// named RED1 -> exact byte restore -> build0/GREEN0. Prior search10
// evidence above is retained under its original source hash.
// Commands, exact-source manifest and hashes:
// work/latest-run-repair-20261008/energy-limits-r2/r2-results.json.
// supply-native-limits (supply-native-limits-red.log):
// System.Exception: Latest-run search: supply must not invent payable demand after Sloth consumes the remaining native play allowance
// supply-x-payment (supply-x-payment-red.log):
// System.Exception: Latest-run search: immediate energy must remain useful for an already payable X-cost card
// supply-aggregate-payment (supply-aggregate-payment-red.log):
// System.Exception: Latest-run search: energy gain must fund several individually payable cards instead of checking only one price
// Final native-history receipts: four isolated production faults, build0 ->
// named RED1 -> exact byte restore -> build0/GREEN0. Prior search10
// evidence above is retained under its original source hash.
// Commands, exact-source manifest and hashes:
// work/latest-run-repair-20261008/energy-receipts-final-r2/r2-results.json.
// supply-native-limits (supply-native-limits-red.log):
// System.Exception: Latest-run search: supply must not invent payable demand after Sloth consumes the remaining native play allowance
// supply-x-payment (supply-x-payment-red.log):
// System.Exception: Latest-run search: immediate energy must remain useful for an already payable X-cost card
// supply-aggregate-payment (supply-aggregate-payment-red.log):
// System.Exception: Latest-run search: energy gain must fund several individually payable cards instead of checking only one price
// native-energy-receipt (native-energy-receipt-red.log):
// System.Exception: Latest-run search: payment history must record actual energy after native star substitution, not its forecast cost (actual=0, expected=4)
// Cheap Tactical candidate/commit separation: two actual production faults,
// build0 -> named RED1 -> exact byte restore -> build0/GREEN0.
// Commands, source manifest, hashes and original output:
// work/latest-run-repair-20261008/stress-repair/r2-results.json.
// deep-candidate-choice (existing JointDiversityScenarios stress):
// System.Exception: Guaranteeing the reference route must not starve every other continuation policy.
// commit-scoring-choice:
// System.Exception: Latest-run search: cheap Tactical must finalize its discard choice with the full native comparison and retain PreciseCut
internal static class LatestRunSearchRepairScenarios
{
    internal static void Run(string? caseName = null)
    {
        if (caseName is not (null or "energy" or "energy-limits" or "energy-stars" or "burst" or "priority" or "reference" or "survival" or "cheap-choice"))
            throw new ArgumentException("Unknown latest-run search case: " + caseName);
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.latest-run-search");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LatestRunSearchRepairScenarios), nameof(Format)));
        try
        {
            if (caseName is null or "energy") Energy();
            if (caseName is null or "energy-limits") EnergyLimits();
            if (caseName is "energy-stars") EnergyStarPayment();
            if (caseName is null or "burst") BurstTiming();
            if (caseName is null or "priority") Priority();
            if (caseName is null or "reference") Reference();
            if (caseName is null or "survival") Survival();
            if (caseName is null or "cheap-choice") CheapChoice();
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void Energy()
    {
        var (party, combat, enemy) = Board(4, 2000, 2);
        var player = party[3];
        var cascade = Hand<Cascade>(player, combat);
        var turbo = Hand<Turbo>(player, combat, true);
        for (var i = 0; i < 7; i++) player.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(player));
        var root = KernelSession.Capture(combat);
        Check(!RolloutRun.ShouldHold(root, party, player, turbo),
            "immediate energy must remain useful for an already payable X-cost card");
        var run = new RolloutRun(root.Fork(), party, party,
            new(MaxSteps: 2) { InvestmentLookahead = false });
        while (!run.Done) run.Step();
        Console.WriteLine("PROBE latest-run energy " + string.Join(',', run.Result!.Actions.Select(a => a.Card?.Id.Entry ?? "END"))
            + " damage=" + (root.Hp(enemy) - run.State.Hp(enemy)));
        Check(run.Result.Actions[0].Card == turbo && run.Result.Actions[1].Card == cascade
            && root.Hp(enemy) - run.State.Hp(enemy) == 30,
            "fourth seat must realize Turbo before Cascade and all five native automatic attacks");
        Check(root.Energy(player) == 2 && root.Hand(player).Contains(turbo), "energy/X comparison must preserve its root");

        var (zeroParty, zeroCombat, _) = Board(1, 2000, 0);
        var zeroCascade = Hand<Cascade>(zeroParty[0], zeroCombat);
        var zero = KernelSession.Capture(zeroCombat);
        Check(RolloutRun.ShouldHold(zero, zeroParty, zeroParty[0], zeroCascade),
            "a zero-energy X play without a realized benefit must prefer passing in cheap continuations");
        zeroCascade.UpgradeInternal();
        zeroParty[0].PlayerCombatState!.DrawPile.AddInternal(zeroCombat.CreateCard<StrikeIronclad>(zeroParty[0]));
        var upgraded = KernelSession.Capture(zeroCombat);
        Check(!RolloutRun.ShouldHold(upgraded, zeroParty, zeroParty[0], zeroCascade),
            "native X-plus-one output must remain useful at zero energy");
        var (comboParty, comboCombat, _) = Board(1, 2000, 2);
        var comboTurbo = Hand<Turbo>(comboParty[0], comboCombat, true);
        for (var i = 0; i < 3; i++) Hand<StrikeIronclad>(comboParty[0], comboCombat);
        Check(!RolloutRun.ShouldHold(KernelSession.Capture(comboCombat), comboParty, comboParty[0], comboTurbo),
            "energy gain must fund several individually payable cards instead of checking only one price");
        Console.WriteLine("PASS: native X payoffs, zero-energy triggers and combined spending determine energy timing.");
    }

    private static void EnergyLimits()
    {
        foreach (var allowance in new[] { 1, 2, 4 })
        {
            var (party, combat, enemy) = Board(1, 2000, 1);
            var player = party[0];
            var turbo = Hand<Turbo>(player, combat, true);
            var attacks = Enumerable.Range(0, 3).Select(_ => Hand<StrikeIronclad>(player, combat)).ToArray();
            ModelDb.Power<SlothPower>().ToMutable().ApplyInternal(player.Creature, allowance, true);
            var root = KernelSession.Capture(combat);
            Check(RolloutRun.ShouldHold(root, party, player, turbo) == (allowance < 4),
                "supply must not invent payable demand after Sloth consumes the remaining native play allowance");
            var supplied = root.ForkForReplay().CardBranches(turbo, null, 1).Single(b => b.Boundary.Length == 0).State;
            var payable = 0;
            foreach (var attack in attacks)
            {
                if (!supplied.CanPlay(attack)) break;
                Check(supplied.Play(attack, enemy, out var why), "resolved payable expense must execute: " + why);
                payable++;
            }
            Check(payable == Math.Min(3, allowance - 1) && supplied.Energy(player) == 4 - payable
                && supplied.Hp(enemy) == 2000 - 6 * payable,
                "resolved supply line must pay actual energy and stop at the captured Sloth limit");

            // Independently call the original game's limit hook; do not use the
            // new payment helper as the oracle for the remaining play count.
            var (nativeParty, nativeCombat, _) = Board(1, 2000, 1);
            var owner = nativeParty[0]; var nativeTurbo = Hand<Turbo>(owner, nativeCombat, true);
            var nativeStrike = Hand<StrikeIronclad>(owner, nativeCombat);
            var sloth = ModelDb.Power<SlothPower>().ToMutable(); sloth.ApplyInternal(owner.Creature, allowance, true);
            void Started(CardModel played) => sloth.BeforeCardPlayed(new CardPlay {
                Card = played, Player = owner, Target = null, ResultPile = PileType.Discard,
                Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 },
                IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 }).GetAwaiter().GetResult();
            Started(nativeTurbo);
            var nativePayable = 0;
            while (nativePayable < 3 && sloth.ShouldPlay(nativeStrike, AutoPlayType.None))
            { Started(nativeStrike); nativePayable++; }
            Check(nativePayable == payable, "original Sloth hooks and resolved payment branch must agree on follow-up count");
            Check(root.Energy(player) == 1 && root.Hand(player).Count == 4 && root.Hp(enemy) == 2000,
                "bounded supply witnesses must preserve their captured root and live hand");
            Console.WriteLine($"PROBE energy-limits Sloth={allowance} payable={payable} native={nativePayable} energy={supplied.Energy(player)}");
        }
        EnergyStarPayment();
        Console.WriteLine("PASS: bounded native supply payments respect consumed Sloth allowances.");
    }

    private static Player? starPaymentOwner;
    private static bool PermitStarPayment(Player player, ref bool __result)
    {
        if (player != starPaymentOwner) return true;
        __result = true; return false;
    }

    private static void EnergyStarPayment()
    {
        // The native API supports this hook, but v0.111's shipped model set has
        // no positive listener. Supply only that external permission input;
        // original payment, affordability and resource history remain intact.
        var hooks = new Harmony("coopbots.latest-run-search.star-payment-contract");
        hooks.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "ShouldPayExcessEnergyCostWithStars"),
            prefix: new HarmonyMethod(typeof(LatestRunSearchRepairScenarios), nameof(PermitStarPayment)));
        try
        {
            var (party, combat, enemy) = Board(1, 2000, 1); var player = party[0];
            player.PlayerCombatState!.GainStars(4);
            var turbo = Hand<Turbo>(player, combat, true);
            var bludgeon = Hand<Bludgeon>(player, combat); bludgeon.EnergyCost.SetThisCombat(6);
            var root = KernelSession.Capture(combat);
            Check(!root.CanPlay(bludgeon), "stars must not bypass native affordability without an enabled permission hook");
            starPaymentOwner = player;
            Check(!root.CanPlay(bludgeon), "native stars must cover the full excess cost even when the hook permits payment");
            var supplied = root.CardBranches(turbo, null, 1).Single(b => b.Boundary.Length == 0).State;
            Check(supplied.Energy(player) == 4 && supplied.CanPlay(bludgeon) && supplied.AttackCosts(bludgeon).Energy == 6,
                "native excess-cost payment must be enabled only once actual energy and stars cover its cost");
            var before = supplied.EnergySpentThisTurn(player);
            Check(supplied.Play(bludgeon, enemy, out var why), "star-funded payment must resolve natively: " + why);
            var payment = supplied.EnergySpentThisTurn(player) - before;
            Check(payment == 4 && supplied.Energy(player) == 0 && supplied.Stars(player) == 0,
                $"payment history must record actual energy after native star substitution, not its forecast cost (actual={payment}, expected=4)");
            Check(!RolloutRun.ShouldHold(root, party, player, turbo), "actual star-funded energy payment must still realize useful supply");

            var (nativeParty, nativeCombat, _) = Board(1, 2000, 4); var owner = nativeParty[0];
            owner.PlayerCombatState!.GainStars(4);
            var nativeCard = Hand<Bludgeon>(owner, nativeCombat); nativeCard.EnergyCost.SetThisCombat(6);
            starPaymentOwner = owner;
            Check(nativeCard.EnergyCost.GetAmountToSpend() == 6 && owner.PlayerCombatState.HasEnoughResourcesFor(nativeCard, out _),
                "original affordability must allow exactly the enabled six-cost/four-energy/four-star contract");
            var resources = nativeCard.SpendResources().GetAwaiter().GetResult();
            Check(resources == (4, 4) && payment == resources.Item1
                && owner.PlayerCombatState.Energy == 0 && owner.PlayerCombatState.Stars == 0,
                "simulator payment receipt must equal unpatched original SpendResources rather than a guessed cost");
            Check(root.Energy(player) == 1 && root.Stars(player) == 4 && root.Hand(player).Contains(turbo),
                "actual payment witnesses must preserve their captured root resources");
            Console.WriteLine($"PROBE energy-stars forecast=6 actual-energy={payment} actual-stars={resources.Item2} native={resources}");
        }
        finally { starPaymentOwner = null; hooks.UnpatchAll(hooks.Id); }
    }

    private static void BurstTiming()
    {
        var (party, combat, _) = Board(1, 2000, 1);
        var player = party[0];
        var burst = Hand<Burst>(player, combat);
        var survivor = Hand<Survivor>(player, combat, true);
        Hand<Tracking>(player, combat); Hand<BouncingFlask>(player, combat);
        var root = KernelSession.Capture(combat);
        Check(RolloutRun.ShouldHold(root, party, player, burst),
            "temporary skill repeat cannot spend the last energy when no payable skill remains");
        var run = new RolloutRun(root.Fork(), party, party,
            new(MaxSteps: 1) { InvestmentLookahead = false });
        run.Step();
        Check(run.State.Hand(player).Contains(burst) && !run.State.Hand(player).Contains(survivor)
            && run.State.Block(player.Creature) == 11,
            "last energy must realize Survivor defence instead of expiring Burst without a follow-up");
        var (fundedParty, fundedCombat, _) = Board(1, 2000, 2);
        var fundedBurst = Hand<Burst>(fundedParty[0], fundedCombat);
        Hand<DefendSilent>(fundedParty[0], fundedCombat);
        var funded = KernelSession.Capture(fundedCombat);
        Check(!RolloutRun.ShouldHold(funded, fundedParty, fundedParty[0], fundedBurst),
            "a genuinely payable native skill follow-up must retain the repeat setup");
        var branch = funded.CardBranches(fundedBurst, null, 1).Single();
        var defend = branch.State.Hand(fundedParty[0]).Single();
        Check(branch.State.Play(defend, null, out var why) && branch.State.Block(fundedParty[0].Creature) == 10,
            "the retained repeat setup must actually double native defence: " + why);
        var (counterParty, counterCombat, _) = Board(1, 2000, 4);
        var countedBurst = Hand<Burst>(counterParty[0], counterCombat);
        for (var i = 0; i < 3; i++) Hand<DefendSilent>(counterParty[0], counterCombat);
        Check(RolloutRun.RelevantRemaining(KernelSession.Capture(counterCombat), counterParty, counterParty[0], countedBurst, null) == 1,
            "temporary skill-repeat valuation must stop after its native counter is consumed");
        countedBurst.UpgradeInternal();
        Check(RolloutRun.RelevantRemaining(KernelSession.Capture(counterCombat), counterParty, counterParty[0], countedBurst, null) == 2,
            "upgraded temporary skill repeat must amplify exactly two payable native follow-ups");
        var (punchParty, punchCombat, _) = Board(1, 2000, 3);
        var punch = Hand<OneTwoPunch>(punchParty[0], punchCombat);
        Hand<StrikeIronclad>(punchParty[0], punchCombat); Hand<StrikeIronclad>(punchParty[0], punchCombat);
        Check(RolloutRun.RelevantRemaining(KernelSession.Capture(punchCombat), punchParty, punchParty[0], punch, null) == 1,
            "the same native finite-counter timing must cover temporary attack repeat");
        var (unfundedParty, unfundedCombat, _) = Board(1, 2000, 1);
        var unfundedPunch = Hand<OneTwoPunch>(unfundedParty[0], unfundedCombat);
        Hand<StrikeIronclad>(unfundedParty[0], unfundedCombat);
        Check(RolloutRun.ShouldHold(KernelSession.Capture(unfundedCombat), unfundedParty, unfundedParty[0], unfundedPunch),
            "temporary attack repeat must also pay setup before counting a follow-up");
        Console.WriteLine("PASS: Burst timing pays setup first and preserves last-energy defence.");
    }

    private static void Priority()
    {
        var (party, combat, _) = Board(1, 300, 3);
        Hand<StrikeSilent>(party[0], combat); Hand<DefendSilent>(party[0], combat);
        var root = KernelSession.Capture(combat);
        var run = new KernelJointTurnRun(root, party, party,
            new(IncludePotions: false, Rollout: new(MaxSteps: 30, MaxRounds: 1)), 1200);
        var cap = AccessTools.Method(typeof(KernelJointTurnRun), "JobCapMs");
        AccessTools.Field(typeof(KernelJointTurnRun), "jobIndex").SetValue(run, 1);
        Check((double)cap.Invoke(run, [200d])! == 600,
            "Tactical must share its budget even when the outer reference is unfinished");
        AccessTools.Field(typeof(KernelJointTurnRun), "jobIndex").SetValue(run, 0);
        var started = AccessTools.Field(typeof(KernelJointTurnRun), "startedPolicies");
        var search = AccessTools.Field(typeof(KernelJointTurnRun), "search");
        for (var i = 0; i < 2000 && !run.Done; i++)
        {
            var policies = (Dictionary<RolloutPolicyKind, int>)started.GetValue(run)!;
            if (policies.ContainsKey(RolloutPolicyKind.Growth)) break;
            run.Advance(TimeSpan.Zero);
            Check(search.GetValue(run) is null, "Defend and Growth must start before beam creation consumes their budget");
        }
        var opened = (Dictionary<RolloutPolicyKind, int>)started.GetValue(run)!;
        Check(opened.ContainsKey(RolloutPolicyKind.Defend) && opened.ContainsKey(RolloutPolicyKind.Growth),
            "all distinct priority continuations must be opened before the beam");
        while (!run.Advance(TimeSpan.FromMilliseconds(2))) { }
        Console.WriteLine("PROBE latest-run priority " + run.Diagnostics);
        Console.WriteLine("PASS: unfinished references still fund all priority policies before the beam.");
    }

    private static void Reference()
    {
        var (party, combat, _) = Board(4, 500, 3);
        foreach (var player in party)
        {
            Hand<StrikeSilent>(player, combat); Hand<DefendSilent>(player, combat);
            for (var i = 0; i < 14; i++) player.PlayerCombatState!.DrawPile.AddInternal(
                i % 2 == 0 ? combat.CreateCard<StrikeSilent>(player) : combat.CreateCard<DefendSilent>(player));
        }
        var root = KernelSession.Capture(combat);
        var tournament = new TournamentRun(root, party, party,
            new(IncludePotions: false, ReserveMs: 0, EnableJointSearch: true,
                Rollout: new(MaxSteps: 160, MaxRounds: 8) { InvestmentLookahead = false }), 3000);
        tournament.Advance(TimeSpan.Zero);
        var reference = (RolloutRun)AccessTools.Field(typeof(TournamentRun), "referenceRun").GetValue(tournament)!;
        var options = (RolloutOptions)AccessTools.Field(typeof(RolloutRun), "o").GetValue(reference)!;
        Check(options.SequenceLookahead && !options.SequenceHandLookahead,
            "the anytime reference must retain native pair ordering without nested whole-hand searches");
        while (!tournament.Advance(TimeSpan.FromMilliseconds(2))) { }
        Console.WriteLine("PROBE latest-run reference compute=" + tournament.ComputeMs + " " + tournament.Result!.JointDiagnostics);
        Check(tournament.Result.JointDiagnostics.Contains("policyReferences=[Defend:")
            && !tournament.Result.JointDiagnostics.Contains("Defend:not-funded"),
            "complex party reference work must leave time to open independent defence");
        Console.WriteLine("PASS: cheap complete references reserve native defence comparisons under the total budget.");
    }

    private static void Survival()
    {
        var (party, combat, _) = Board(4, 2000, 3, ascension: 10);
        var activeRun = AccessTools.Property(typeof(RunManager), "State");
        var previousRun = activeRun.GetValue(RunManager.Instance);
        var activeAscension = AccessTools.Property(typeof(RunManager), nameof(RunManager.AscensionManager));
        var previousAscension = activeAscension.GetValue(RunManager.Instance);
        activeRun.SetValue(RunManager.Instance, combat.RunState);
        activeAscension.SetValue(RunManager.Instance, new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(10));
        try
        {
        // The native TestSubject multihit shape observed at owner R3. Hidden
        // historical draws are intentionally absent; this isolates the hand's
        // immediate survival trade with the same energy budget.
        var oldEnemy = combat.Enemies.Single(); combat.RemoveCreature(oldEnemy);
        var enemy = combat.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TestSubject>().ToMutable(), CombatSide.Enemy, "1");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(2000); enemy.SetCurrentHpInternal(2000);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["MULTI_CLAW_MOVE"], true);
        var player = party[0]; player.Creature.SetCurrentHpInternal(28);
        ModelDb.Power<TankPower>().ToMutable().ApplyInternal(player.Creature, 1, true);
        ModelDb.Power<PlatingPower>().ToMutable().ApplyInternal(player.Creature, 7, true);
        var pommel = Hand<PommelStrike>(player, combat);
        Hand<Maul>(player, combat); Hand<Taunt>(player, combat, true);
        var defend = Hand<DefendIronclad>(player, combat, true);
        var root = KernelSession.Capture(combat);
        var run = new RolloutRun(root.Fork(), party, party,
            new(RolloutPolicyKind.Defend, MaxSteps: 25, MaxRounds: 1)
            { InvestmentLookahead = false, SequenceHandLookahead = false });
        while (!run.Done) run.Step();
        Console.WriteLine("PROBE latest-run survival actions=" + string.Join(',', run.Result!.Actions.Where(a => a.Player == player)
            .Select(a => a.Card?.Id.Entry ?? "END")) + " hp=" + run.State.Hp(player.Creature));
        Check(run.Result.Actions.Any(a => a.Card == defend) && run.State.Hp(player.Creature) == 2,
            "an independent native defence reference must buy the block that keeps the low-HP Tank alive");
        Check(run.Result.Actions.Count(a => a.Player == player && a.Card is not null) <= 3
            && root.Hand(player).Contains(pommel) && root.Hp(player.Creature) == 28,
            "the defence reference must pay the original three energy and preserve its captured root");
        Console.WriteLine("PASS: independent native defence retains the low-HP Tank through the immediate enemy phase.");
        }
        finally
        { activeRun.SetValue(RunManager.Instance, previousRun); activeAscension.SetValue(RunManager.Instance, previousAscension); }
    }

    private static void CheapChoice()
    {
        // A scoring-only candidate takes one native choice branch. That is a
        // bounded ranking estimate, not evidence that the first choice is best.
        // The committed card must still perform the normal complete comparison.
        var (party, combat, enemy) = Board(1, 2000, 2);
        var player = party[0]; player.Creature.SetCurrentHpInternal(8);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 10, true);
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(enemy, 8, true);
        ModelDb.Power<AccuracyPower>().ToMutable().ApplyInternal(player.Creature, 6, true);
        var survivor = Hand<Survivor>(player, combat);
        var cut = Hand<PreciseCut>(player, combat);
        Hand<StrikeSilent>(player, combat); Hand<StrikeSilent>(player, combat);
        Hand<DefendSilent>(player, combat); Hand<Shiv>(player, combat); Hand<Shiv>(player, combat);
        var root = KernelSession.Capture(combat); var rootText = root.StateText(player);
        KernelSession.CardBranch scoring;
        using (KernelChoiceContinuation.Enter()) scoring = root.CardBranches(survivor, null, 1).Single();
        var complete = root.CardBranches(survivor, null, 1).Single();
        Check(scoring.Choices.Single().Cards.Single().Id == cut.Id.Entry
            && complete.Choices.Single().Cards.Single().Id == "STRIKE_SILENT",
            "cheap choice fixture must distinguish the first native branch from the complete discard comparison");
        var run = new RolloutRun(root.Fork(), party, party,
            new(RolloutPolicyKind.Tactical, MaxSteps: 1, MaxRounds: 1)
            { InvestmentLookahead = false, SequenceHandLookahead = false });
        while (!run.Done) run.Step();
        var action = run.Result!.Actions.Single();
        Console.WriteLine("PROBE latest-run cheap-choice candidate=" + scoring.Choices.Single().Cards.Single().Id
            + " committed=" + string.Join(',', action.Choices!.SelectMany(c => c.Cards).Select(c => c.Id)));
        Check(action.Card == survivor && action.Choices!.Single().Cards.SequenceEqual(complete.Choices.Single().Cards)
            && action.ChoiceOrdinals!.SequenceEqual(complete.ChoiceOrdinals)
            && run.State.Hand(player).Contains(cut) && run.State.Discard(player).Any(c => c is StrikeSilent),
            "cheap Tactical must finalize its discard choice with the full native comparison and retain PreciseCut");
        var replay = root.ForkForReplay();
        Check(replay.ReplayPlanned(action, 1, out var why)
            && replay.StateText(player) == run.State.StateText(player)
            && run.State.StateText(player) == complete.State.StateText(player)
            && root.StateText(player) == rootText,
            "finalized cheap Tactical choices must replay the complete native after-state without mutating the root: " + why);
        Console.WriteLine("PASS: cheap Tactical scoring branches never replace the complete committed discard choice.");
    }

    private static (Player[] Party, CombatState Combat, Creature Enemy) Board(int count, int enemyHp, int energy, int ascension = 0)
    {
        var party = Enumerable.Range(0, count).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            (ulong)(25000 + i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "LATEST-RUN-SEARCH", ascensionLevel: ascension)) { RoundNumber = 1 };
        foreach (var player in party)
        {
            player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 3;
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(energy);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(40);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat, enemy);
    }
    private static T Hand<T>(Player player, CombatState combat, bool upgraded = false) where T : CardModel
    {
        var card = combat.CreateCard<T>(player); if (upgraded) card.UpgradeInternal();
        player.PlayerCombatState!.Hand.AddInternal(card); return card;
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Latest-run search: " + why); }
}
