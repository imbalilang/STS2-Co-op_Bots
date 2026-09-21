using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor.PowerSync;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// Focused regression for the partial 0.41.0 power-route backport on the
/// production KernelTeamSearch path. Old control and new path run on the SAME
/// fixture; the new path is asserted to preserve a delayed payoff line the old
/// score-only trim would drop, and to leave no-power/dead/unsupported cases
/// unchanged. Pure lifecycle and seat policy are exercised through
/// InternalsVisibleTo (the adapter file is the only new production kernel file).
/// </summary>
internal static class KernelPowerRouteScenarios
{
    private static int passed;

    internal static void Run()
    {
        TestSeatPolicyAndLifecycle();
        TestLedgerSignatureIncludesFullState();
        TestCatalogRegistration();
        TestGenuineRuntimeEngineAndOwnership();
        TestSilentOwnershipAdmission();
        TestNoTriggerAndUnsupportedPowers();
        TestOldControlVsNewDelayedRoute();
        TestProductionEvaluationRoute();
        TestImmediateLethalPreferred();
        TestNoPowerBehaviourUnchanged();
        TestSelectionFairnessThreeOwners();
        TestDeadOwnerLosesProtection();
        TestWonBranchClearsProtection();
        TestBudgetsAndCancellation();
        Console.WriteLine($"PASS: kernel power-route backport scenarios ({passed} focused checks).");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("POWER-SYNC: " + message);
        passed++;
    }

    // ----------------------------------------------------------------- helpers

    private sealed record Fixture(CombatState Combat, Player[] Party, Creature Enemy);

    private static Fixture Build(int enemyHp, int attack, string seed, int partySize = 1, bool giveEnergy = true)
    {
        var party = new Player[partySize];
        for (var i = 0; i < partySize; i++)
            party[i] = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 7, i + 1));
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            if (giveEnergy) p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(attack))
            { FollowUpStateId = "ATTACK" }, true);
        return new Fixture(combat, party, enemy);
    }

    private static void AddHand<T>(Fixture fixture, Player p, int count = 1) where T : CardModel
    {
        for (var i = 0; i < count; i++)
            p.PlayerCombatState!.Hand.AddInternal(fixture.Combat.CreateCard<T>(p));
    }

    private static void AddDraw<T>(Fixture fixture, Player p, int count) where T : CardModel
    {
        for (var i = 0; i < count; i++)
            p.PlayerCombatState!.DrawPile.AddInternal(fixture.Combat.CreateCard<T>(p));
    }

    private static KernelTeamSearch.Result Run(
        KernelSession root,
        Player[] actors,
        Func<KernelSession, double> evaluate,
        KernelTeamSearch.Options options)
    {
        return SearchWithState(root, actors, evaluate, options).Result;
    }

    private static (KernelTeamSearch.Result Result, KernelSession? State) SearchWithState(
        KernelSession root,
        Player[] actors,
        Func<KernelSession, double> evaluate,
        KernelTeamSearch.Options options)
    {
        using var search = new KernelTeamSearch(root, actors, evaluate, options);
        while (!search.Advance(TimeSpan.FromMilliseconds(4), () => true)) { }
        return (search.CompletedResult!, search.CompletedState);
    }

    private static KernelTeamSearch.Options Options(int width, int depth, int nodes, bool includeEndTurns,
        bool powerRoutes) => new(Depth: depth, Width: width, MaxNodes: nodes, IncludeEndTurns: includeEndTurns,
            MaxRounds: 2)
        { EnablePowerRoutes = powerRoutes };

    // --------------------------------------------------------- pure policy/data

    private static void TestSeatPolicyAndLifecycle()
    {
        var inflame = KernelPowerCatalog.TryDescribe("INFLAME", out var descriptor);
        Check(inflame && descriptor.Family == PowerCommitmentFamily.StrengthGrowth
            && descriptor.Pool == PowerCardPool.Ironclad && descriptor.CardId == "INFLAME",
            "INFLAME must map to the exact upstream Ironclad StrengthGrowth route.");
        Check(!KernelPowerCatalog.TryDescribe("ROYALTIES", out _),
            "an upstream NoInCombatCommitment card must not create a descriptor.");
        Check(!KernelPowerCatalog.TryDescribe("NOT_A_REAL_CARD", out _),
            "an unknown card id must not create a descriptor.");

        // SeatQuota is the exact upstream function; ordinary floor is half.
        foreach (var width in new[] { 2, 3, 4, 6, 8, 12, 20, 24 })
        {
            var quota = PowerCommitmentSeatPolicy.SeatQuota(width, aggressive: false);
            var ordinaryFloor = (width + 1) / 2;
            Check(quota >= 0 && width - quota >= ordinaryFloor,
                $"SeatQuota({width}) must leave the upstream ordinary floor (quota={quota}).");
        }
        Check(PowerCommitmentSeatPolicy.SeatQuota(1, false) == 0
            && PowerCommitmentSeatPolicy.SeatQuota(2, false) == 1
            && PowerCommitmentSeatPolicy.SeatQuota(12, false) == 2,
            "SeatQuota must match the upstream clamp(W/12,2,12) capped by W/2.");

        var created = PowerCommitmentLifecycle.Create(descriptor, turn: 0, actionCount: 0, historyEntryCount: 0,
            investment: 8, provisionalPotential: 10);
        Check(created.PowerCardsPlayed == 1 && created.ProvisionalPotential == 10,
            "commitment creation must record the upstream investment and potential.");

        var active = PowerCommitmentLifecycle.Advance(created, parentTurn: 0, childTurn: 1,
            maximumTransitions: 2, progressEvidence: 1, realizedEvidence: 0, terminal: false);
        Check(active.Disposition == PowerCommitmentDisposition.Active && active.Commitment!.ProgressEvidence == 1,
            "evidence within one transition must keep the commitment active.");

        var stale = PowerCommitmentLifecycle.Advance(created, parentTurn: 0, childTurn: 3,
            maximumTransitions: 2, progressEvidence: 0, realizedEvidence: 0, terminal: false);
        Check(stale.Disposition == PowerCommitmentDisposition.Expired && stale.Commitment is null,
            "commitments must expire past the upstream transition cap.");

        var realized = PowerCommitmentLifecycle.Advance(created, parentTurn: 0, childTurn: 0,
            maximumTransitions: 2, progressEvidence: 0, realizedEvidence: 10, terminal: false);
        Check(realized.Disposition == PowerCommitmentDisposition.Realized && realized.Commitment is null,
            "full realization must release the commitment seat.");
    }

    private static PowerCommitment Commitment(string cardId)
    {
        Check(KernelPowerCatalog.TryDescribe(cardId, out var descriptor), cardId + " must describe a route.");
        return PowerCommitmentLifecycle.Create(descriptor, turn: 0, actionCount: 0, historyEntryCount: 0,
            investment: 8, provisionalPotential: 10);
    }

    private static void TestLedgerSignatureIncludesFullState()
    {
        var fixture = Build(enemyHp: 60, attack: 4, seed: "POWER-SIG");
        var owner = fixture.Party[0];
        var baseCommitment = Commitment("INFLAME");

        var plain = KernelPowerLedger.Empty.With(owner.NetId, baseCommitment);
        var identical = KernelPowerLedger.Empty.With(owner.NetId, baseCommitment);
        Check(plain.Signature() == identical.Signature(),
            "identical commitment states must produce an identical signature.");

        // Same owner and family, different transition/expiry clock.
        var advanced = PowerCommitmentLifecycle.Advance(baseCommitment, parentTurn: 0, childTurn: 1,
            maximumTransitions: 2, progressEvidence: 1, realizedEvidence: 0, terminal: false).Commitment!;
        var withTransitions = KernelPowerLedger.Empty.With(owner.NetId, advanced);
        Check(plain.Signature() != withTransitions.Signature(),
            "same owner+family but different transition/evidence state must not dedup together.");

        // Same owner and family, different remaining potential after partial
        // realization.
        var partlyRealized = PowerCommitmentLifecycle.Advance(baseCommitment, parentTurn: 0, childTurn: 0,
            maximumTransitions: 2, progressEvidence: 0, realizedEvidence: 4, terminal: false).Commitment!;
        var withRemaining = KernelPowerLedger.Empty.With(owner.NetId, partlyRealized);
        Check(plain.Signature() != withRemaining.Signature(),
            "same owner+family but different remaining potential must not dedup together.");
        Check(withTransitions.Signature() != withRemaining.Signature(),
            "different transition/potential commitment states must be distinguishable.");
    }

    private static void TestCatalogRegistration()
    {
        // A sample from each upstream pool must resolve; this pins the six route
        // policy files and their namespace adaptation.
        foreach (var (card, pool) in new[]
        {
            ("INFLAME", PowerCardPool.Ironclad), ("ACCURACY", PowerCardPool.Silent),
            ("ECHO_FORM", PowerCardPool.Defect), ("FURNACE", PowerCardPool.Regent),
            ("REAPER_FORM", PowerCardPool.Necrobinder), ("MAYHEM", PowerCardPool.Colorless),
        })
        {
            Check(KernelPowerCatalog.TryDescribe(card, out var d) && d.Pool == pool,
                $"{card} must resolve to upstream pool {pool}.");
        }
    }

    // ------------------------------------------- genuine engine + owner isolation

    private static void TestGenuineRuntimeEngineAndOwnership()
    {
        var fixture = Build(enemyHp: 120, attack: 4, seed: "POWER-OWNER", partySize: 2);
        var a = fixture.Party[0];
        var b = fixture.Party[1];
        AddHand<Inflame>(fixture, a);
        AddHand<StrikeIronclad>(fixture, a, 3);
        AddHand<Barricade>(fixture, b);
        AddHand<DefendIronclad>(fixture, b, 2);
        var root = KernelSession.Capture(fixture.Combat);

        var inflame = root.Hand(a).Single(c => c.Id.Entry == "INFLAME");
        var aBranch = root.Fork();
        // A Self-target power is played with NO target — the live game logs a blank
        // `targetid:` for these cards, and upstream CombatSolver's TargetsFor yields
        // (-1, null) for every type except AnyEnemy. Passing the owner's creature made
        // the real gate reject the card ("invalid-target" here, "card-not-playable" at
        // deployment in a live fight).
        Check(aBranch.Play(inflame, null, out var aReason), "Inflame play: " + aReason);
        Check(aBranch.Power<StrengthPower>(a.Creature) == 2,
            "the real engine must apply Strength before evidence is measured.");
        var ledgerA = KernelPowerRouter.Advance(KernelPowerLedger.Empty, root, aBranch,
            new KernelTeamSearch.Action(a, inflame, null), fixture.Party);
        Check(ledgerA.Count == 1 && ledgerA.TryGet(a.NetId, out var commitA)
            && commitA.Family == PowerCommitmentFamily.StrengthGrowth,
            "a registered power with own-branch trigger facts must create exactly one owner commitment.");

        var barricade = aBranch.Hand(b).Single(c => c.Id.Entry == "BARRICADE");
        var bBranch = aBranch.Fork();
        Check(bBranch.Play(barricade, null, out var bReason), "Barricade play: " + bReason);
        var ledgerAB = KernelPowerRouter.Advance(ledgerA, aBranch, bBranch,
            new KernelTeamSearch.Action(b, barricade, null), fixture.Party);
        Check(ledgerAB.Count == 2 && ledgerAB.TryGet(a.NetId, out _) && ledgerAB.TryGet(b.NetId, out _),
            "two owners must hold independent commitments keyed by NetId.");
    }

    // Silent admission requires the owner's OWN engine: a teammate's Shivs or
    // block must never justify the caster's route.
    private static void TestSilentOwnershipAdmission()
    {
        var accuracyNeg = Build(enemyHp: 120, attack: 4, seed: "POWER-SILENT-ACC-NEG", partySize: 2);
        AddHand<Accuracy>(accuracyNeg, accuracyNeg.Party[0]);
        AddHand<Shiv>(accuracyNeg, accuracyNeg.Party[1], 2);
        Check(PowerRoute(accuracyNeg, accuracyNeg.Party[0], "ACCURACY").IsEmpty,
            "Accuracy must not open a route from a teammate's Shivs.");

        var accuracyPos = Build(enemyHp: 120, attack: 4, seed: "POWER-SILENT-ACC-POS", partySize: 2);
        AddHand<Accuracy>(accuracyPos, accuracyPos.Party[0]);
        AddHand<Shiv>(accuracyPos, accuracyPos.Party[0], 2);
        Check(!PowerRoute(accuracyPos, accuracyPos.Party[0], "ACCURACY").IsEmpty,
            "Accuracy with the caster's own Shivs must open an owner route.");

        var footworkNeg = Build(enemyHp: 120, attack: 4, seed: "POWER-SILENT-FW-NEG", partySize: 2);
        AddHand<Footwork>(footworkNeg, footworkNeg.Party[0]);
        AddHand<DefendSilent>(footworkNeg, footworkNeg.Party[1], 2);
        Check(PowerRoute(footworkNeg, footworkNeg.Party[0], "FOOTWORK").IsEmpty,
            "Footwork must not open a route from a teammate's block cards.");

        var footworkPos = Build(enemyHp: 120, attack: 4, seed: "POWER-SILENT-FW-POS", partySize: 2);
        AddHand<Footwork>(footworkPos, footworkPos.Party[0]);
        AddHand<DefendSilent>(footworkPos, footworkPos.Party[0], 2);
        Check(!PowerRoute(footworkPos, footworkPos.Party[0], "FOOTWORK").IsEmpty,
            "Footwork with the caster's own block cards must open an owner route.");
    }

    private static KernelPowerLedger PowerRoute(Fixture fixture, Player owner, string cardId)
    {
        var root = KernelSession.Capture(fixture.Combat);
        var power = root.Hand(owner).Single(c => c.Id.Entry == cardId);
        var branch = root.Fork();
        Check(branch.Play(power, null, out var reason), $"{cardId} play: {reason}");
        return KernelPowerRouter.Advance(KernelPowerLedger.Empty, root, branch,
            new KernelTeamSearch.Action(owner, power, null), fixture.Party);
    }

    private static void TestNoTriggerAndUnsupportedPowers()
    {
        // Registered power, but the owner has no attack anywhere: PersistentValue
        // cannot see a trigger, so no commitment may be created (old behaviour).
        var noAttack = Build(enemyHp: 60, attack: 4, seed: "POWER-NOTRIGGER");
        var a = noAttack.Party[0];
        AddHand<Inflame>(noAttack, a);
        AddHand<DefendIronclad>(noAttack, a, 3);
        var noAttackRoot = KernelSession.Capture(noAttack.Combat);
        var noAttackCard = noAttackRoot.Hand(a).Single(c => c.Id.Entry == "INFLAME");
        var noAttackBranch = noAttackRoot.Fork();
        Check(noAttackBranch.Play(noAttackCard, null, out var reason), "Inflame no-trigger play: " + reason);
        var empty = KernelPowerRouter.Advance(KernelPowerLedger.Empty, noAttackRoot, noAttackBranch,
            new KernelTeamSearch.Action(a, noAttackCard, null), noAttack.Party);
        Check(empty.IsEmpty, "a registered power with no own-branch trigger evidence must not open a commitment.");
    }

    // ------------------------------------------------- old control vs new route

    private static void TestOldControlVsNewDelayedRoute()
    {
        // DemonForm costs 3, so it can only be the first action. The hand also has
        // attack and skill lines, so a width-2 score-only frontier drops the power
        // immediately; five Defends push the candidate count past width*4 so the
        // intermediate trim is also exercised.
        Fixture NewFixture()
        {
            var f = Build(enemyHp: 200, attack: 4, seed: "POWER-DELAY");
            var bot = f.Party[0];
            AddHand<DemonForm>(f, bot);
            AddHand<StrikeIronclad>(f, bot, 3);
            AddHand<DefendIronclad>(f, bot, 5);
            AddDraw<StrikeIronclad>(f, bot, 6);
            return f;
        }
        static Func<KernelSession, double> Eval(Player bot, Creature enemy)
            => s => 200 - s.Hp(enemy) + s.Power<StrengthPower>(bot.Creature) * 5;

        static string Path(KernelTeamSearch.Result result)
            => string.Join(",", result.Actions.Select(a => a.Card?.Id.Entry ?? (a.EndTurn ? "END" : "?")));

        var controlFixture = NewFixture();
        var controlEnemy = controlFixture.Enemy;
        var controlRoot = KernelSession.Capture(controlFixture.Combat);
        var controlRun = SearchWithState(controlRoot, controlFixture.Party, Eval(controlFixture.Party[0], controlEnemy),
            Options(width: 2, depth: 6, nodes: 500, includeEndTurns: true, powerRoutes: false));
        var control = controlRun.Result;
        Check(!control.Actions.Any(a => a.Card?.Id.Entry == "DEMON_FORM"),
            "old control must prune the delayed power line at width 2.");

        var newFixture = NewFixture();
        var newEnemy = newFixture.Enemy;
        var bot = newFixture.Party[0];
        var newRoot = KernelSession.Capture(newFixture.Combat);
        var updatedRun = SearchWithState(newRoot, newFixture.Party, Eval(newFixture.Party[0], newEnemy),
            Options(width: 2, depth: 6, nodes: 500, includeEndTurns: true, powerRoutes: true));
        var updated = updatedRun.Result;
        var controlPath = Path(control);
        var newPath = Path(updated);
        var controlDamage = controlEnemy.CurrentHp - (controlRun.State?.Hp(controlEnemy) ?? controlEnemy.CurrentHp);
        var newDamage = newEnemy.CurrentHp - (updatedRun.State?.Hp(newEnemy) ?? newEnemy.CurrentHp);
        var newStrength = updatedRun.State?.Power<StrengthPower>(bot.Creature) ?? 0;
        Console.WriteLine($"[power-sync] delayed route old=[{controlPath}] score={control.Score:F1} damage={controlDamage}; "
            + $"new=[{newPath}] score={updated.Score:F1} damage={newDamage} strength={newStrength}");

        Check(updated.Actions.Any(a => a.Card?.Id.Entry == "DEMON_FORM"),
            "the backport must retain the DemonForm route through both bounded trims. new=" + newPath
            + $" score={updated.Score:F1}; old={controlPath} score={control.Score:F1}");
        Check(updated.Actions[0].Card?.Id.Entry == "DEMON_FORM",
            "the retained route must actually deploy the power setup first.");
        Check(updated.Actions.Skip(1).Any(a => !a.EndTurn && a.Card is not null && a.Card.Type != CardType.Power),
            "after the power setup the retained route must continue with a non-power action. new=" + newPath);
        Check(newStrength > 0, "the engine must actually apply DemonForm Strength on the retained route.");
        Check(newDamage > 0, "the retained delayed route must deal real damage the old prune never reached.");
        Check(newDamage > controlDamage,
            $"the retained route's real damage must beat the pruned control: new={newDamage}, old={controlDamage}.");
        Check(updated.Score > control.Score + 0.001,
            $"the delayed route must pay off within the same budget: new={updated.Score:F1}, old={control.Score:F1}.");
        Check(updated.ExpandedNodes <= 500, "route retention must stay inside the node budget.");
    }

    private static void TestProductionEvaluationRoute()
    {
        // Production KernelCombatEvaluation (internals visible to PatchSmoke):
        // Inflame raises PersistentValue and the following Strikes inherit the
        // real Strength, so the retained route must not score worse.
        Fixture NewFixture()
        {
            var f = Build(enemyHp: 90, attack: 4, seed: "POWER-PRODEVAL");
            var bot = f.Party[0];
            AddHand<Inflame>(f, bot);
            AddHand<StrikeIronclad>(f, bot, 2);
            AddHand<DefendIronclad>(f, bot, 2);
            return f;
        }

        var controlFixture = NewFixture();
        var controlEval = new KernelCombatEvaluation(controlFixture.Combat, controlFixture.Party, null);
        var controlRoot = KernelSession.Capture(controlFixture.Combat);
        var control = Run(controlRoot, controlFixture.Party, s => controlEval.Evaluate(s).Score,
            Options(width: 2, depth: 4, nodes: 400, includeEndTurns: false, powerRoutes: false));

        var newFixture = NewFixture();
        var newEval = new KernelCombatEvaluation(newFixture.Combat, newFixture.Party, null);
        var newRoot = KernelSession.Capture(newFixture.Combat);
        var updated = Run(newRoot, newFixture.Party, s => newEval.Evaluate(s).Score,
            Options(width: 2, depth: 4, nodes: 400, includeEndTurns: false, powerRoutes: true));
        Check(updated.Actions.Any(a => a.Card?.Id.Entry == "INFLAME"),
            "production evaluation must retain the Inflame setup route.");
        Check(updated.Score >= control.Score - 0.001,
            $"the Inflame route must not score worse under the production evaluator: new={updated.Score:F1}, old={control.Score:F1}.");
    }

    private static void TestImmediateLethalPreferred()
    {
        var fixture = Build(enemyHp: 6, attack: 4, seed: "POWER-LETHAL");
        var bot = fixture.Party[0];
        AddHand<StrikeIronclad>(fixture, bot);
        AddHand<Inflame>(fixture, bot);
        AddHand<StrikeIronclad>(fixture, bot, 2);
        var root = KernelSession.Capture(fixture.Combat);
        var run = SearchWithState(root, fixture.Party,
            s => (s.HasWon ? 10000 : 0) - s.Hp(fixture.Enemy) * 10 + s.Hp(bot.Creature),
            Options(width: 2, depth: 3, nodes: 200, includeEndTurns: false, powerRoutes: true));
        Check(run.State!.HasWon, "a committed power line must not displace a confirmed kill.");
        Check(run.Result.Actions[0].Card?.Id.Entry == "STRIKE_IRONCLAD",
            "the immediate lethal attack must remain the first deployed action.");
    }

    private static void TestNoPowerBehaviourUnchanged()
    {
        Fixture NewFixture()
        {
            var f = Build(enemyHp: 60, attack: 4, seed: "POWER-NONE");
            var bot = f.Party[0];
            AddHand<StrikeIronclad>(f, bot, 3);
            AddHand<DefendIronclad>(f, bot, 3);
            AddDraw<StrikeIronclad>(f, bot, 3);
            return f;
        }

        var controlFixture = NewFixture();
        var control = Run(KernelSession.Capture(controlFixture.Combat), controlFixture.Party,
            s => 60 - s.Hp(controlFixture.Enemy) + s.Hp(controlFixture.Party[0].Creature) * 0.1,
            Options(width: 3, depth: 4, nodes: 300, includeEndTurns: true, powerRoutes: false));
        var newFixture = NewFixture();
        var updated = Run(KernelSession.Capture(newFixture.Combat), newFixture.Party,
            s => 60 - s.Hp(newFixture.Enemy) + s.Hp(newFixture.Party[0].Creature) * 0.1,
            Options(width: 3, depth: 4, nodes: 300, includeEndTurns: true, powerRoutes: true));
        var controlPath = string.Join(",", control.Actions.Select(a => a.Card?.Id.Entry ?? (a.EndTurn ? "END" : "?")));
        var newPath = string.Join(",", updated.Actions.Select(a => a.Card?.Id.Entry ?? (a.EndTurn ? "END" : "?")));
        Check(controlPath == newPath && Math.Abs(control.Score - updated.Score) < 0.001,
            $"with no committed power the chosen path and score must match on this fixture: old={controlPath}, new={newPath}.");
    }

    private static void TestDeadOwnerLosesProtection()
    {
        var fixture = Build(enemyHp: 60, attack: 50, seed: "POWER-DEATH");
        var bot = fixture.Party[0];
        bot.Creature.SetCurrentHpInternal(1);
        AddHand<Inflame>(fixture, bot);
        AddHand<StrikeIronclad>(fixture, bot, 3);
        var root = KernelSession.Capture(fixture.Combat);
        var inflame = root.Hand(bot).Single(c => c.Id.Entry == "INFLAME");
        var afterPower = root.Fork();
        Check(afterPower.Play(inflame, null, out var reason), "Inflame death setup: " + reason);
        var active = KernelPowerRouter.Advance(KernelPowerLedger.Empty, root, afterPower,
            new KernelTeamSearch.Action(bot, inflame, null), fixture.Party);
        Check(active.Count == 1, "the setup owner starts committed.");

        var ended = afterPower.Fork();
        Check(ended.EndTurn(bot, 2, out var endReason), "death end turn: " + endReason);
        Check(ended.Hp(bot.Creature) == 0, "the 50-damage attack must kill the 1-HP owner.");
        var afterDeath = KernelPowerRouter.Advance(active, afterPower, ended,
            new KernelTeamSearch.Action(bot, null, null, EndTurn: true), fixture.Party);
        Check(afterDeath.IsEmpty, "a dead owner and terminal branch must not keep protection.");
    }

    private static void TestWonBranchClearsProtection()
    {
        var fixture = Build(enemyHp: 6, attack: 4, seed: "POWER-WON");
        var bot = fixture.Party[0];
        AddHand<StrikeIronclad>(fixture, bot, 2);
        AddHand<Inflame>(fixture, bot);
        var root = KernelSession.Capture(fixture.Combat);
        var existing = KernelPowerLedger.Empty.With(bot.NetId, Commitment("INFLAME"));
        var strike = root.Hand(bot).First(c => c.Id.Entry == "STRIKE_IRONCLAD");
        var branch = root.Fork();
        Check(branch.Play(strike, fixture.Enemy, out var reason), "winning strike: " + reason);
        Check(branch.HasWon, "the 6-damage strike must win against 6 HP.");
        var after = KernelPowerRouter.Advance(existing, root, branch,
            new KernelTeamSearch.Action(bot, strike, fixture.Enemy), fixture.Party);
        Check(after.IsEmpty, "a won branch must clear every owner's protection before anything else.");
    }

    // ------------------------------------- bounded seat fairness (reflection)

    private static readonly Type NodeType = typeof(KernelTeamSearch)
        .GetNestedType("Node", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("KernelTeamSearch.Node was not found.");

    private static object MakeNode(KernelSession state, KernelPowerLedger ledger, double score)
    {
        // Anchored on the LEADING parameter types rather than a raw count. The old
        // `.Single(Length == 5)` silently stopped matching anything once Node gained a
        // parameter (Boundaries), so this threw "Sequence contains no matching element"
        // -- a reflection failure that looks like a product failure but is not.
        var ctor = NodeType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length >= 4
                    && parameters[0].ParameterType == typeof(KernelSession)
                    && parameters[1].ParameterType == typeof(KernelTeamSearch.Action[])
                    && parameters[2].ParameterType == typeof(double)
                    && parameters[3].ParameterType == typeof(bool);
            });
        var path = Array.CreateInstance(typeof(KernelTeamSearch.Action), 0);
        // Fill positionally: the seat-fairness assertion only needs a constructible
        // node, not a semantically complete one. Power is the 5th parameter and the
        // turn-boundary predictions the 6th; both default to empty/ledger here.
        var signature = ctor.GetParameters();
        var arguments = new object?[signature.Length];
        arguments[0] = state;
        arguments[1] = path;
        arguments[2] = score;
        arguments[3] = false;
        if (signature.Length > 4) arguments[4] = ledger;
        if (signature.Length > 5)
            arguments[5] = Array.CreateInstance(typeof(ValueTuple<int, string>), 0);
        return ctor.Invoke(arguments);
    }

    // A shared node that carries two owners' commitments must consume ONE seat
    // and represent both owners, so a third eligible owner still gets a seat.
    private static void TestSelectionFairnessThreeOwners()
    {
        var fixture = Build(enemyHp: 80, attack: 2, seed: "POWER-FAIR", partySize: 3);
        var a = fixture.Party[0];
        var b = fixture.Party[1];
        var c = fixture.Party[2];
        var session = KernelSession.Capture(fixture.Combat);

        var shared = KernelPowerLedger.Empty
            .With(a.NetId, Commitment("INFLAME"))
            .With(b.NetId, Commitment("INFLAME"));
        var onlyC = KernelPowerLedger.Empty.With(c.NetId, Commitment("INFLAME"));

        var listType = typeof(List<>).MakeGenericType(NodeType);
        var candidates = (System.Collections.IList)Activator.CreateInstance(listType)!;
        // Ordinary lines fill the reserved ordinary seats; the committed lines
        // compete for the two reserved power seats.
        candidates.Add(MakeNode(session, KernelPowerLedger.Empty, 100));
        candidates.Add(MakeNode(session, KernelPowerLedger.Empty, 90));
        candidates.Add(MakeNode(session, shared, 50));
        candidates.Add(MakeNode(session, onlyC, 40));

        var method = typeof(KernelTeamSearch)
            .GetMethod("SelectProtectedFrontier", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SelectProtectedFrontier was not found.");
        var selected = (System.Collections.IEnumerable)method.Invoke(null, new object[] { candidates, 4 })!;
        var powerProperty = NodeType.GetProperty("Power",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var selectedCount = 0;
        var thirdOwnerRepresented = false;
        var distinctCommitted = new HashSet<KernelPowerLedger>();
        foreach (var node in selected)
        {
            selectedCount++;
            var ledger = (KernelPowerLedger)powerProperty.GetValue(node)!;
            if (!ledger.IsEmpty) distinctCommitted.Add(ledger);
            if (ledger.OwnerIds.Contains(c.NetId)) thirdOwnerRepresented = true;
        }
        Check(selectedCount <= 4, $"protected selection must stay within width (selected={selectedCount}).");
        Check(thirdOwnerRepresented,
            "the third owner's eligible route must not be dropped when two owners share one node.");
        Check(distinctCommitted.Count >= 2,
            "the shared node and the third owner's node must both be selected as distinct representatives.");
    }

    private static void TestBudgetsAndCancellation()
    {
        Fixture NewFixture()
        {
            var f = Build(enemyHp: 120, attack: 4, seed: "POWER-BUDGET");
            var bot = f.Party[0];
            AddHand<Inflame>(f, bot);
            AddHand<DemonForm>(f, bot);
            AddHand<StrikeIronclad>(f, bot, 3);
            AddHand<DefendIronclad>(f, bot, 3);
            return f;
        }

        var capFixture = NewFixture();
        var capRoot = KernelSession.Capture(capFixture.Combat);
        var capped = Run(capRoot, capFixture.Party, s => capFixture.Enemy.CurrentHp - s.Hp(capFixture.Enemy),
            Options(width: 2, depth: 6, nodes: 3, includeEndTurns: true, powerRoutes: true));
        Check(capped.ExpandedNodes <= 3 && capped.StopReason == "node-budget",
            $"node cap must stay strict with power routes on ({capped.ExpandedNodes}/{capped.StopReason}).");

        var staleFixture = NewFixture();
        var staleRoot = KernelSession.Capture(staleFixture.Combat);
        using (var stale = new KernelTeamSearch(staleRoot, staleFixture.Party,
            s => staleFixture.Enemy.CurrentHp - s.Hp(staleFixture.Enemy),
            Options(width: 2, depth: 6, nodes: 400, includeEndTurns: true, powerRoutes: true)))
        {
            stale.Advance(TimeSpan.FromTicks(1), () => true);
            stale.Advance(TimeSpan.FromMilliseconds(2), () => false);
            Check(stale.CompletedResult is { StopReason: "stale-root", Actions.Count: 0 },
                "a stale root must still clear every planned action.");
        }
        var cancelFixture = NewFixture();
        var cancelRoot = KernelSession.Capture(cancelFixture.Combat);
        using (var cancelled = new KernelTeamSearch(cancelRoot, cancelFixture.Party,
            s => cancelFixture.Enemy.CurrentHp - s.Hp(cancelFixture.Enemy),
            Options(width: 2, depth: 6, nodes: 400, includeEndTurns: true, powerRoutes: true)))
        {
            cancelled.Advance(TimeSpan.FromMilliseconds(2), () => true, new CancellationToken(true));
            Check(cancelled.CompletedResult is { StopReason: "cancelled", Actions.Count: 0, ExpandedNodes: 0 },
                "cancellation must stay zero-cost and clear actions.");
        }

        var budgetFixture = NewFixture();
        var budgetRoot = KernelSession.Capture(budgetFixture.Combat);
        using var budget = new KernelTeamSearch(budgetRoot, budgetFixture.Party,
            s => budgetFixture.Enemy.CurrentHp - s.Hp(budgetFixture.Enemy),
            Options(width: 2, depth: 6, nodes: 400, includeEndTurns: true, powerRoutes: true));
        budget.FinishAtBudget();
        Check(budget.CompletedResult is { StopReason: "time-budget" },
            "time-budget completion must remain callable with power routes on.");
    }
}
