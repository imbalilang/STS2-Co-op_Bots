using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class KernelEngineScenarios
{
    internal static void Run()
    {
        // The console harness has no localization tables. Only presentation is
        // replaced; card/power numeric variables and simulation stay native.
        new HarmonyLib.Harmony("coopbots.test.kernel.presentation").Patch(
            HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(KernelEngineScenarios), nameof(FormatText)));
        KernelPowerRouteScenarios.Run();
        KernelRoundScenarios.Run();
        RunContinuationInvariants();
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 2));
        var party = new[] { human, a, b }; var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "KERNEL-21"));
        foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3); }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
        T Hand<T>(Player p) where T : CardModel { var c = combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(c); return c; }
        var bash = Hand<Bash>(a); var strike = Hand<StrikeIronclad>(b);
        var root = KernelSession.Capture(combat); var branch = root.Fork(); var sibling = root.Fork();
        if (!branch.Play(bash, enemy, out var reason)) throw new Exception("Kernel Bash: " + reason);
        if (!branch.Play(strike, enemy, out reason)) throw new Exception("Kernel Strike: " + reason);
        if (branch.Hp(enemy) != 183 || branch.Energy(a) != 1 || branch.Energy(b) != 2)
            throw new Exception($"Kernel shared world: hp={branch.Hp(enemy)}, a={branch.Energy(a)}, b={branch.Energy(b)}");
        if (root.Hp(enemy) != 200 || sibling.Hp(enemy) != 200 || enemy.CurrentHp != 200 || a.PlayerCombatState!.Energy != 3)
            throw new Exception("Kernel changed root, sibling or live world.");
        Console.WriteLine("PASS: full CombatSolver kernel, cross-bot Bash/Strike in one shared fork, owner resource isolation, sibling/root/live isolation.");

        void Check(bool value, string message) { if (!value) throw new Exception("Kernel: " + message); }
        var invalid = root.Fork();
        Check(!invalid.Play(bash, human.Creature, out reason) && reason == "invalid-target", "Bash must not target a human.");
        Check(invalid.Hp(enemy) == 200 && invalid.Energy(a) == 3, "Invalid target must not spend resources.");
        Hand<StrikeIronclad>(b); Hand<StrikeIronclad>(b);
        var teamRoot = KernelSession.Capture(combat);
        using (var search = new KernelTeamSearch(teamRoot, [b, a], s => 200 - s.Hp(enemy)))
        {
            while (!search.Advance(TimeSpan.FromMilliseconds(2), () => true)) { }
            var result = search.CompletedResult!;
            Check(result.Actions.Count == 4 && result.Actions[0].Card == bash && result.Score == 35,
                "Team search must reorder actors: Bash before all three teammate Strikes, independent of actor enumeration.");
            Check(result.Boundaries.Count == 0, "Basic team search must have no unresolved prediction gaps.");
        }
        using (var stale = new KernelTeamSearch(teamRoot, [b, a], s => 200 - s.Hp(enemy)))
        {
            stale.Advance(TimeSpan.FromTicks(1), () => true);
            stale.Advance(TimeSpan.FromMilliseconds(2), () => false);
            Check(stale.CompletedResult is { StopReason: "stale-root", Actions.Count: 0 }, "Discard all actions after human intervention.");
        }
        using (var cancelled = new KernelTeamSearch(teamRoot, [a, b], s => 200 - s.Hp(enemy)))
        {
            cancelled.Advance(TimeSpan.FromMilliseconds(2), () => true, new CancellationToken(true));
            Check(cancelled.CompletedResult is { StopReason: "cancelled", Actions.Count: 0, ExpandedNodes: 0 }, "Cancelled search must not simulate or deploy.");
        }
        using (var budget = new KernelTeamSearch(teamRoot, [a, b], s => 200 - s.Hp(enemy), new(MaxNodes: 1)))
        {
            while (!budget.Advance(TimeSpan.FromMilliseconds(2), () => true)) { }
            Check(budget.CompletedResult is { StopReason: "node-budget", ExpandedNodes: 1 }, "Node budget must be strict.");
        }

        void ClearHands()
        {
            foreach (var p in party)
                foreach (var c in p.PlayerCombatState!.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(c);
        }
        ClearHands();
        var fire = Hand<FiendFire>(a); var fuel1 = Hand<DefendIronclad>(a); var fuel2 = Hand<DefendIronclad>(a);
        var otherHand = Hand<StrikeIronclad>(b);
        var fireRoot = KernelSession.Capture(combat); var fireBranch = fireRoot.Fork();
        Check(fireBranch.Play(fire, enemy, out reason), "FiendFire: " + reason);
        Check(fireBranch.Hp(enemy) == 186 && fireBranch.Hand(a).Count == 0
            && fireBranch.Exhaust(a).Count == 3 && fireBranch.Hand(b).Single() == otherHand,
            "FiendFire must hit twice and exhaust only its owner's hand plus itself.");
        Check(fireRoot.Hand(a).Count == 3 && fireRoot.Exhaust(a).Count == 0
            && a.PlayerCombatState!.Hand.Cards.Count == 3, "Exhaust must not mutate sibling/root/live piles.");

        ClearHands();
        var offering = Hand<Offering>(a);
        for (var i = 0; i < 6; i++) a.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(a));
        var drawRoot = KernelSession.Capture(combat); var drawBranch = drawRoot.Fork();
        Check(drawBranch.Play(offering, null, out reason), "Offering: " + reason);
        Check(drawBranch.Hp(a.Creature) == 74 && drawBranch.Energy(a) == 5 && drawBranch.Hand(a).Count == 3,
            "Offering must lose six HP, gain two energy and draw three exact cards.");
        using (var drawSearch = new KernelTeamSearch(drawRoot, [a], s => 200 - s.Hp(enemy) - (80 - s.Hp(a.Creature))))
        {
            while (!drawSearch.Advance(TimeSpan.FromMilliseconds(2), () => true)) { }
            Check(drawSearch.CompletedResult!.Actions.Count == 4 && drawSearch.CompletedResult.Actions[0].Card == offering
                && drawSearch.CompletedResult.Score == 12, "Search must discover and play newly drawn cards, charging the HP cost.");
        }
        ClearHands();
        var battleTrance = Hand<BattleTrance>(a);
        // NoDraw is an upstream power model, not a CoopBots per-card approximation.
        var offeringAfterTrance = combat.CreateCard<Offering>(a); a.PlayerCombatState!.Hand.AddInternal(offeringAfterTrance);
        var noDraw = KernelSession.Capture(combat);
        Check(noDraw.Play(battleTrance, null, out reason), "BattleTrance repeat: " + reason);
        var handBefore = noDraw.Hand(a).Count;
        Check(noDraw.Play(offeringAfterTrance, null, out reason) && noDraw.Hand(a).Count == handBefore - 1,
            "NoDraw must suppress Offering draw.");
        Check(enemy.CurrentHp == 200 && a.Creature.CurrentHp == 80 && a.PlayerCombatState.Energy == 3,
            "All searches must leave the real combat unchanged.");
        ClearHands();
        var voidForm = Hand<VoidForm>(a);
        var endRoot = KernelSession.Capture(combat); var endBranch = endRoot.Fork();
        Check(endBranch.Play(voidForm, null, out reason) && endBranch.IsReady(a), "VoidForm must end only its caster's turn: " + reason);
        Check(endBranch.Fork().IsReady(a) && !endBranch.IsReady(b) && !endBranch.EnemyPhaseCompleted && endRoot.Energy(a) == 3,
            "Settled forced end may fork but must not flush teammates or mutate root.");
        using (var boundarySearch = new KernelTeamSearch(endRoot, [a], s => s.Block(a.Creature)))
        {
            while (!boundarySearch.Advance(TimeSpan.FromMilliseconds(2), () => true)) { }
            Check(!boundarySearch.CompletedResult!.Boundaries.Keys.Any(k => k.EndsWith(":player-turn-end")),
                "A supported forced end must not appear as an unknown boundary.");
        }
        ClearHands();
        var shuriken = ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Shuriken>().ToMutable(); a.AddRelicInternal(shuriken);
        shuriken.GetType().GetField("_attacksPlayedThisTurn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(shuriken, 2);
        var hit1 = Hand<StrikeIronclad>(a); var hit2 = Hand<StrikeIronclad>(a); var teammateHit = Hand<StrikeIronclad>(b);
        var relicRoot = KernelSession.Capture(combat); var relicBranch = relicRoot.Fork();
        Check(relicBranch.Play(hit1, enemy, out reason) && relicBranch.Play(teammateHit, enemy, out reason)
            && relicBranch.Play(hit2, enemy, out reason) && relicBranch.Hp(enemy) == 181,
            "Native Shuriken counter must buff following owner attack only (6 + teammate 6 + owner 7).");
        Check((int)shuriken.GetType().GetField("_attacksPlayedThisTurn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(shuriken)! == 2
            && a.Creature.Powers.Count == 0, "Simulation must not mutate live relic counter or powers.");

        // Soft human-finish credit: a damage line that brings an enemy the bots
        // cannot kill alone into the human's finishing range must beat blocking.
        ClearHands();
        var softEnemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "soft");
        combat.AddCreature(softEnemy); softEnemy.Monster!.SetUpForCombat();
        softEnemy.SetMaxHpInternal(24); softEnemy.SetCurrentHpInternal(24);
        softEnemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(8)), true);
        var softStrike = Hand<StrikeIronclad>(a);
        var softDefend = Hand<DefendIronclad>(a);
        for (var i = 0; i < 4; i++) Hand<StrikeIronclad>(human);
        var softRoot = KernelSession.Capture(combat);
        var softEval = new KernelCombatEvaluation(combat, [a, b], null);
        var softAttack = softRoot.Fork();
        Check(softAttack.Play(softStrike, softEnemy, out reason), "soft-finish strike: " + reason);
        var softBlock = softRoot.Fork();
        Check(softBlock.Play(softDefend, null, out reason), "soft-finish defend: " + reason);
        var attackValue = softEval.Evaluate(softAttack);
        var blockValue = softEval.Evaluate(softBlock);
        Check(attackValue.SoftFinish > 0, "Damage that reaches the human's finish range must be credited.");
        Check(attackValue.Score > blockValue.Score,
            $"Enabling the human finish must beat blocking first: attack={attackValue.Score:F1}, block={blockValue.Score:F1}");
        Console.WriteLine("PASS: kernel team score credits reaching a human finishing range and prefers it over blocking first.");

        AuditMultiplayer(combat, human, a, b);
        AuditSharedDebuffs();
        AuditEnemyScaling();
        AuditPlannerLiveness();
        AuditPartialPlanAndForecast();
        AuditInertMonsterMove();
        AuditPotionCoverage();
        AuditPotionMirrorsDoSomething();
        AuditTeamPotionMirrors();
        AuditProactivePotion();
        AuditJointPotionSequence();
        AuditPotionOnlyWhenCardsCannotFinisher();
        AuditSoloTakeover();
        AuditBoundedSearchPolicy();
        AuditIdleGating();
        AuditNoActionStampReuse();
        AuditBotStampIgnoresHumanEndTurn();
        AuditBossReactiveMechanics();
        AuditScalingThreatFocus();
        AuditCrossTurn();
        AuditMultiplayerBlockScaling();
        HumanFinisherScenarios.Run();
        AuditTurnStrengthDown();
        AuditStructuralFallback();
        AuditHeldStatusPenalty();
        AuditHeldPenaltyBlockSplit();
        AuditOutrage();
        AuditAllForOne();
        AuditMultiplayerBatchA();
        AuditFlankingKnockdown();
        AuditReplanAfterAction();
        AuditCoopBuffTiming();
        AuditCardCoverage();

        Console.WriteLine("PASS: kernel team beam reorders actors, shares Vulnerable, discovers draw chains, enforces owner-only exhaust/NoDraw, rejects invalid targets and discards stale/cancelled plans with bounded nodes.");
        Console.WriteLine("PASS: kernel forced end readies only its owner; imported Shuriken counter/owner isolation.");
    }
    // Safety audit: a multiplayer card the kernel cannot model must reach an
    // explicit boundary (so the caller falls back to the multiplayer-aware
    // planner), never be silently treated as zero value, and never throw.
    private static void AuditMultiplayer(CombatState combat, Player human, Player a, Player b)
    {
        // Vendored snapshot has no mirror/spec for these; if one ever starts
        // being "modeled" without a multiplayer differential test, fail loudly.
        // Cards that left this list once they got a kernel mirror and a
        // differential test: DemonicShield (AuditMultiplayerBatchA), Flanking and
        // Knockdown (AuditFlankingKnockdown).
        var mustBoundary = new HashSet<string>
        {
            "OneForAll", "Intercept", "Tutor",
        };
        // Concoct is the same shape as Blaze/Coordinate (Skill, one declared
        // PowerVar, one ally) so the structural fallback deliberately prices it
        // instead of dropping it. Asserted positively so a future regression to
        // "unplayable" is caught just as loudly.
        // Concoct left this bucket on 2026-09-20: it is properly modelled now
        // (CardEffectSpecRegistry: [typeof(Concoct)] = [Target<ConcoctPower>("ConcoctPower")]),
        // so it is MODELED on its own merits rather than priced from DynamicVars. Anything
        // that drifts back into this bucket is an unmodelled card, and an unmodelled card
        // must be skipped — see the loop below.
        var structurallyPriced = new HashSet<string>();
        var names = new[]
        {
            "TagTeam", "GangUp", "OneForAll", "Sneaky", "BeaconOfHope", "Rally", "Mimic", "DemonicShield",
            "BelieveInYou", "Constellation", "EnergySurge", "HuddleUp", "Flanking", "Knockdown",
            "Intercept", "Concoct", "Tutor", "FightMe", "SicEm", "Bodyguard",
        };
        var outcome = new Dictionary<string, string>();
        foreach (var name in names)
        {
            var canonical = ModelDb.AllCards.FirstOrDefault(c => c.GetType().Name == name);
            if (canonical is null) { outcome[name] = "TYPE-NOT-FOUND"; continue; }
            Clear(a);
            a.PlayerCombatState!.GainEnergy(12);
            var card = combat.CreateCard(canonical, a);
            a.PlayerCombatState.Hand.AddInternal(card);
            var root = KernelSession.Capture(combat);
            var branch = root.Fork();
            if (!branch.CanPlay(card)) { outcome[name] = "NOT-PLAYABLE"; continue; }
            var target = branch.Targets(card).FirstOrDefault(t => t is not null);
            // Play must fail closed on any mirror exception, not propagate it.
            outcome[name] = branch.Play(card, target, out var boundary) ? "MODELED" : "BOUNDARY";
            Console.WriteLine($"AUDIT {name}: {outcome[name]} {boundary}");
        }
        foreach (var error in outcome.Where(pair => pair.Value is "TYPE-NOT-FOUND" or "THREW"))
            throw new Exception($"Kernel multiplayer audit failed for {error.Key}: {error.Value}");
        foreach (var name in mustBoundary)
            if (outcome[name] != "BOUNDARY")
                throw new Exception($"Kernel must fail closed for {name}, got {outcome[name]}.");
        // POLICY REVERSAL 2026-09-20. These are the cards StructuralCardMirror used to price
        // from their own DynamicVars so they stayed playable. Live evidence ended that: a
        // structurally priced card (CONCOCT) went into a 90-action route whose hand then
        // diverged from reality, and the plan was refused at its turn boundary eighteen times.
        // An unmodelled OnPlay is a boundary again — upstream's answer — so the card is
        // skipped rather than played on an estimate. The cost is stated in KernelSession.Play.
        foreach (var name in structurallyPriced)
            if (outcome[name] != "BOUNDARY")
                throw new Exception($"An unmodelled ally buff must now be SKIPPED, not estimated: {name} was {outcome[name]}.");
        Console.WriteLine($"PASS: kernel multiplayer audit: {outcome.Count(p => p.Value == "BOUNDARY")} cards defer to the multiplayer planner, "
            + $"{outcome.Count(p => p.Value == "MODELED")} resolve in the kernel, none throw.");

        void ClearAll()
        {
            foreach (var p in new[] { human, a, b }) Clear(p);
        }

        // EnergySurge must grant energy to every living teammate.
        ClearAll();
        var surge = combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "EnergySurge"), a);
        a.PlayerCombatState!.Hand.AddInternal(surge);
        var surgeRoot = KernelSession.Capture(combat);
        var surgeBranch = surgeRoot.Fork();
        if (!surgeBranch.Play(surge, null, out var surgeReason)) throw new Exception("Kernel EnergySurge: " + surgeReason);
        // Teammates gain the full 2; the caster also paid the 1 energy cost.
        if (surgeBranch.Energy(human) != surgeRoot.Energy(human) + 2 || surgeBranch.Energy(b) != surgeRoot.Energy(b) + 2)
            throw new Exception("Kernel EnergySurge must give +2 to every teammate, got "
                + $"human {surgeRoot.Energy(human)}->{surgeBranch.Energy(human)}, b {surgeRoot.Energy(b)}->{surgeBranch.Energy(b)}");
        if (surgeBranch.Energy(a) != surgeRoot.Energy(a) + 1)
            throw new Exception($"Kernel EnergySurge caster must net +1 (cost 1, gain 2), got {surgeRoot.Energy(a)}->{surgeBranch.Energy(a)}");

        // BelieveInYou must grant energy only to the chosen ally.
        ClearAll();
        var believe = combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "BelieveInYou"), a);
        a.PlayerCombatState!.Hand.AddInternal(believe);
        var believeRoot = KernelSession.Capture(combat);
        var believeBranch = believeRoot.Fork();
        if (!believeBranch.Play(believe, b.Creature, out var believeReason)) throw new Exception("Kernel BelieveInYou: " + believeReason);
        if (believeBranch.Energy(b) != believeRoot.Energy(b) + 2 || believeBranch.Energy(human) != believeRoot.Energy(human))
            throw new Exception("Kernel BelieveInYou must give +2 only to the targeted ally.");

        // Power cards whose team hooks the snapshot already mirrors must apply it.
        ClearAll();
        var sneaky = combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "Sneaky"), a);
        a.PlayerCombatState!.Hand.AddInternal(sneaky);
        var sneakyRoot = KernelSession.Capture(combat);
        var sneakyBranch = sneakyRoot.Fork();
        if (!sneakyBranch.Play(sneaky, null, out var sneakyReason)) throw new Exception("Kernel Sneaky: " + sneakyReason);
        if (sneakyBranch.Power<SneakyPower>(a.Creature) <= 0)
            throw new Exception("Kernel Sneaky must apply SneakyPower to its owner.");

        ClearAll();
        var beacon = combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "BeaconOfHope"), a);
        a.PlayerCombatState!.Hand.AddInternal(beacon);
        var beaconRoot = KernelSession.Capture(combat);
        var beaconBranch = beaconRoot.Fork();
        if (!beaconBranch.Play(beacon, null, out var beaconReason)) throw new Exception("Kernel BeaconOfHope: " + beaconReason);
        if (beaconBranch.Power<BeaconOfHopePower>(a.Creature) <= 0)
            throw new Exception("Kernel BeaconOfHope must apply BeaconOfHopePower to its owner.");
        Console.WriteLine("PASS: kernel models EnergySurge/BelieveInYou/Sneaky/BeaconOfHope with team-correct effects.");

        // A modeled multiplayer card must actually reach teammates, not just
        // report "no gap": Rally gives block to every living ally.
        Clear(a);
        var rally = combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "Rally"), a);
        a.PlayerCombatState!.Hand.AddInternal(rally);
        var rallyRoot = KernelSession.Capture(combat);
        var rallyBranch = rallyRoot.Fork();
        if (!rallyBranch.CanPlay(rally)) throw new Exception("Kernel: Rally was not playable in the audit harness.");
        if (!rallyBranch.Play(rally, null, out var rallyReason)) throw new Exception("Kernel Rally: " + rallyReason);
        if (rallyBranch.Block(human.Creature) <= 0 || rallyBranch.Block(a.Creature) <= 0 || rallyBranch.Block(b.Creature) <= 0)
            throw new Exception("Kernel: Rally must block every living ally, got "
                + $"human={rallyBranch.Block(human.Creature)}, a={rallyBranch.Block(a.Creature)}, b={rallyBranch.Block(b.Creature)}");
        Console.WriteLine("PASS: kernel resolves a modeled multiplayer card across all teammates (Rally blocks every ally).");
    }

    // Team-awareness audit for shared debuffs: Weak and Strength-down reduce
    // incoming damage for EVERY member, so team scoring must value them by party
    // size rather than as a single-player buff.
    private static void AuditSharedDebuffs()
    {
        double DebuffValue(string cardName, int allies)
        {
            var players = new List<Player>
            {
                Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 7, allies)),
            };
            for (var i = 1; i < allies; i++)
                players.Add(Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(900 + i)));
            var run = RunState.CreateForTest(players.ToArray(), seed: $"DEBUFF-{cardName}-{allies}");
            var c = new CombatState(runState: run);
            foreach (var p in players)
            {
                p.ResetCombatState(); c.AddPlayer(p);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(6);
            }
            var foe = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "debuff");
            c.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(500); foe.SetCurrentHpInternal(500);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
            var bot = players[0];
            var card = c.CreateCard(ModelDb.AllCards.First(x => x.GetType().Name == cardName), bot);
            bot.PlayerCombatState!.Hand.AddInternal(card);
            var eval = new KernelCombatEvaluation(c, [bot], null);
            var root = KernelSession.Capture(c);
            var branch = root.Fork();
            var target = branch.Targets(card).FirstOrDefault(x => x is not null);
            if (!branch.Play(card, target, out var reason)) throw new Exception($"Kernel {cardName}: " + reason);
            return eval.Evaluate(branch).Score - eval.Evaluate(root).Score;
        }

        var weak3 = DebuffValue("Shockwave", 3);
        var weak1 = DebuffValue("Shockwave", 1);
        if (!(weak3 > weak1 + 0.001))
            throw new Exception($"Kernel must value Weak by party size: 3 allies={weak3:F2}, 1 ally={weak1:F2}");
        Console.WriteLine($"PASS: kernel values shared Weak by party size (3 allies {weak3:F1} vs 1 ally {weak1:F1}).");

        var strength3 = DebuffValue("Mangle", 3);
        var strength1 = DebuffValue("Mangle", 1);
        if (!(strength3 > strength1 + 0.001))
            throw new Exception($"Kernel must value Strength-down by party size: 3 allies={strength3:F2}, 1 ally={strength1:F2}");
        Console.WriteLine($"PASS: kernel values shared Strength-down by party size (3 allies {strength3:F1} vs 1 ally {strength1:F1}).");

        // Offensive half: Vulnerable applied by one bot must amplify every
        // teammate's follow-up attack in the same shared plan.
        var vPlayers = new List<Player>
        {
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 8, 1)),
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 8, 2)),
        };
        var vCombat = new CombatState(runState: RunState.CreateForTest(vPlayers.ToArray(), seed: "VULN-ORDER"));
        foreach (var p in vPlayers)
        {
            p.ResetCombatState(); vCombat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(6);
        }
        var vFoe = vCombat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "vuln");
        vCombat.AddCreature(vFoe); vFoe.Monster!.SetUpForCombat();
        vFoe.SetMaxHpInternal(60); vFoe.SetCurrentHpInternal(60);
        vFoe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(6)), true);
        var bash = vCombat.CreateCard<Bash>(vPlayers[0]); vPlayers[0].PlayerCombatState!.Hand.AddInternal(bash);
        var strikeA = vCombat.CreateCard<StrikeIronclad>(vPlayers[0]); vPlayers[0].PlayerCombatState!.Hand.AddInternal(strikeA);
        var strikeB = vCombat.CreateCard<StrikeIronclad>(vPlayers[1]); vPlayers[1].PlayerCombatState!.Hand.AddInternal(strikeB);

        int TotalHpAfter(params CardModel[] order)
        {
            var root = KernelSession.Capture(vCombat);
            var branch = root.Fork();
            foreach (var card in order)
                if (!branch.Play(card, vFoe, out var why)) throw new Exception("Vulnerable order: " + why);
            return branch.Hp(vFoe);
        }
        var setupFirst = TotalHpAfter(bash, strikeA, strikeB);
        var attackFirst = TotalHpAfter(strikeA, strikeB, bash);
        if (!(setupFirst < attackFirst))
            throw new Exception($"Kernel must exploit Vulnerable for every teammate: setup-first={setupFirst}, attack-first={attackFirst}");
        Console.WriteLine($"PASS: kernel amplifies every teammate's attack with shared Vulnerable (setup-first {setupFirst} < attack-first {attackFirst}).");

        // Shared Vulnerable must also credit the human's follow-up damage, which
        // the bot-only search cannot simulate. Enemy 30, human can deal 18: only
        // after a bot applies Vulnerable (18 * 1.5 = 27 >= 22) is it a soft kill.
        var hPlayers = new List<Player>
        {
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 9, 1)),
            Player.CreateForNewRun<Deprived>(UnlockState.all, 77UL),
        };
        var hCombat = new CombatState(runState: RunState.CreateForTest(hPlayers.ToArray(), seed: "HUMAN-VULN"));
        foreach (var p in hPlayers)
        {
            p.ResetCombatState(); hCombat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var hFoe = hCombat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "hvuln");
        hCombat.AddCreature(hFoe); hFoe.Monster!.SetUpForCombat();
        hFoe.SetMaxHpInternal(30); hFoe.SetCurrentHpInternal(30);
        hFoe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(6)), true);
        var humanPlayer = hPlayers[1];
        for (var i = 0; i < 3; i++)
            humanPlayer.PlayerCombatState!.Hand.AddInternal(hCombat.CreateCard<StrikeIronclad>(humanPlayer));
        var hBash = hCombat.CreateCard<Bash>(hPlayers[0]); hPlayers[0].PlayerCombatState!.Hand.AddInternal(hBash);
        var hEval = new KernelCombatEvaluation(hCombat, [hPlayers[0]], null);
        var hRoot = KernelSession.Capture(hCombat);
        var vulnBranch = hRoot.Fork();
        if (!vulnBranch.Play(hBash, hFoe, out var hReason)) throw new Exception("Human Vulnerable: " + hReason);
        // The credit is continuous in the HP the human can take, so Vulnerable
        // must strictly increase it rather than merely cross a threshold.
        var baseFinish = hEval.Evaluate(hRoot).SoftFinish;
        var vulnFinish = hEval.Evaluate(vulnBranch).SoftFinish;
        if (!(vulnFinish > baseFinish))
            throw new Exception($"Vulnerable must raise the human's credited damage: with={vulnFinish:F1}, without={baseFinish:F1}");
        Console.WriteLine("PASS: kernel credits Vulnerable's amplification of the human's finishing damage.");
    }

    // Enemy-scaling mechanics triggered by the party (e.g. TestSubject's Enrage:
    // any Skill play gives the boss Strength) must be priced across every member,
    // so the penalty for triggering them grows with the team.
    private static void AuditEnemyScaling()
    {
        double SkillPenalty(int allies, bool enraged)
        {
            var players = new List<Player>
            {
                Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 6, allies)),
            };
            for (var i = 1; i < allies; i++)
                players.Add(Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(600 + i)));
            var c = new CombatState(runState: RunState.CreateForTest(players.ToArray(), seed: $"ENRAGE-{allies}-{enraged}"));
            foreach (var p in players)
            {
                p.ResetCombatState(); c.AddPlayer(p);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            }
            var foe = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "rage");
            c.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(500); foe.SetCurrentHpInternal(500);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
            if (enraged)
            {
                var enrage = (EnragePower)ModelDb.Power<EnragePower>().ToMutable();
                enrage.ApplyInternal(foe, 2, true);
            }
            var bot = players[0];
            var skill = c.CreateCard<DefendIronclad>(bot); bot.PlayerCombatState!.Hand.AddInternal(skill);
            var eval = new KernelCombatEvaluation(c, [bot], null);
            var root = KernelSession.Capture(c);
            var branch = root.Fork();
            if (!branch.Play(skill, null, out var reason)) throw new Exception("Enrage skill: " + reason);
            // Positive means playing the Skill made the team worse off.
            return eval.Evaluate(root).Score - eval.Evaluate(branch).Score;
        }

        // Isolate the Enrage contribution by subtracting the same Skill play
        // without Enrage (its block benefit and any other terms cancel out).
        var penalty1 = SkillPenalty(1, true) - SkillPenalty(1, false);
        var penalty3 = SkillPenalty(3, true) - SkillPenalty(3, false);
        if (!(penalty1 > 0))
            throw new Exception($"Triggering Enrage with a Skill must cost the team, got {penalty1:F2}.");
        if (!(penalty3 > penalty1 + 0.001))
            throw new Exception($"Enrage's enemy Strength must be priced across the whole party: 3 allies={penalty3:F2}, 1 ally={penalty1:F2}");
        Console.WriteLine($"PASS: kernel prices Skill-triggered enemy Strength across the party (3 allies {penalty3:F1} vs 1 ally {penalty1:F1}).");
    }

    // Regression for the "bots stop playing cards" bug: a completed kernel
    // search must resolve to Ready (with a plan) or Fallback within a bounded
    // number of ticks. Returning Pending forever re-searches every frame and
    // never submits an action, leaving the bots idle for the whole fight.
    private static void AuditPlannerLiveness()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 4, 1));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)555);
        var players = new[] { bot, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(players, seed: "LIVENESS"));
        foreach (var p in players)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "live");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(9)), true);
        bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));

        var planner = new KernelCombatPlanner();
        var status = KernelCombatPlanner.Status.Pending;
        TeamCombatPlanner.Decision? decision = null;
        var ticks = 0;
        while (status == KernelCombatPlanner.Status.Pending && ticks++ < 400)
            status = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out decision);
        if (status == KernelCombatPlanner.Status.Pending)
            throw new Exception($"Kernel planner stayed Pending for {ticks} ticks; bots would never act.");
        if (status == KernelCombatPlanner.Status.Ready && decision is null)
            throw new Exception("Kernel Ready without a plan skips the legacy planner and leaves the bots idle.");
        Console.WriteLine($"PASS: kernel planner resolves to {status} within {ticks} ticks and never idles the bots.");

        // `maxRound` is a post-mortem field: it has to say how long the fight RAN.
        // It is only right if every poll samples the live round, because a solved fight
        // is ONE search replayed to the end — nothing starts a second search, so a value
        // tied to the search-start path is frozen at whatever round that search began in.
        // Every multi-turn fight in the 2026-09-20 live round printed `maxRound=1`, which
        // makes a two-turn fight indistinguishable from a twenty-turn one.
        //
        // This polls a seat with no playable card and no potion, which returns BEFORE any
        // search begins. The round still has to be recorded. Reverting the sampling to
        // ObserveCombat was CHECKED (R2) and goes red with exactly:
        //   System.Exception: maxRound did not follow a poll that started no search:
        //   0 != 4; the summary would report the round its last search began in,
        //   not the last round the fight reached.
        var sampler = new KernelCombatPlanner();
        var roundBefore = combat.RoundNumber;
        combat.RoundNumber = 4;
        sampler.Poll(combat, new[] { ally }, 0, null, false, BotDifficulty.Pro, out _);
        if (sampler.MaxRoundSeen != 4)
            throw new Exception($"maxRound did not follow a poll that started no search: "
                + $"{sampler.MaxRoundSeen} != 4; the summary would report the round its last search began in, "
                + "not the last round the fight reached.");
        Console.WriteLine("PASS: combat summary's maxRound is sampled on every poll, including ones that start no search.");
        combat.RoundNumber = roundBefore;

        // `routes=` is `plansWithRoute/plans`. `plans` is cleared per fight; `plansWithRoute`
        // was NOT — it was the fourth counter that reset block forgot (its own comment
        // already names `routes=3/2` as the symptom, and names only three of the four).
        // Measured live 2026-09-20, one round printed a run-cumulative numerator over a
        // per-fight denominator: 1/6, 2/5, 3/3, 4/6, 6/4 … 13/5, 14/2. So every `routes=`
        // in a long run mixed two different units and could not be read at all.
        // CHECKED (R2): removing the reset turns this red with exactly
        //   System.Exception: plansWithRoute survived Reset(newCombat: true) as 7; the
        //   summary's `routes=N/M` would then be a run-cumulative numerator over a
        //   per-fight denominator.
        var routeField = typeof(KernelCombatPlanner).GetField("plansWithRoute", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("plansWithRoute field was not found.");
        routeField.SetValue(planner, 7);
        planner.Reset(newCombat: true);
        if ((int)routeField.GetValue(planner)! != 0)
            throw new Exception($"plansWithRoute survived Reset(newCombat: true) as {routeField.GetValue(planner)}; "
                + "the summary's `routes=N/M` would then be a run-cumulative numerator over a per-fight denominator.");
        Console.WriteLine("PASS: plansWithRoute is cleared per fight, so `routes=N/M` is one fight's ratio.");

        // The real trigger: the live root keeps moving while the search expands
        // (human actions, sync updates). Modelled by churning the action version
        // every tick. Before the fix this returned Pending forever.
        var churning = new KernelCombatPlanner();
        var churnStatus = KernelCombatPlanner.Status.Pending;
        TeamCombatPlanner.Decision? churnDecision = null;
        uint version = 0;
        var churnTicks = 0;
        while (churnStatus == KernelCombatPlanner.Status.Pending && churnTicks++ < 400)
            churnStatus = churning.Poll(combat, new[] { bot }, version++, null, false, BotDifficulty.Pro, out churnDecision);
        if (churnStatus == KernelCombatPlanner.Status.Pending)
            throw new Exception($"A moving live root spun the kernel planner for {churnTicks} ticks; bots would never act.");
        Console.WriteLine($"PASS: kernel planner falls back to the legacy planner ({churnStatus}) after a queued action moves the root instead of spinning.");

        // Notification-only churn must NOT discard an in-flight search: the
        // revision counter fires on every combat-state notification, most of
        // which change nothing a branch depends on. The captured state stamp on
        // the completion path is the real gate, so a search that is still valid
        // must be allowed to finish instead of being thrown away mid-expansion.
        var notified = new KernelCombatPlanner();
        var notifyStatus = KernelCombatPlanner.Status.Pending;
        TeamCombatPlanner.Decision? notifyDecision = null;
        var isolation = typeof(KernelSession).Assembly.GetType("CoopBots.Kernel.KernelIsolation")
            ?? throw new Exception("KernelIsolation type was not found.");
        var notify = isolation.GetMethod("NotifyPrefix", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("KernelIsolation.NotifyPrefix was not found.");
        var notifyTicks = 0;
        while (notifyStatus == KernelCombatPlanner.Status.Pending && notifyTicks++ < 400)
        {
            notify.Invoke(null, null); // a non-state notification lands mid-search
            notifyStatus = notified.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out notifyDecision);
        }
        if (notifyStatus == KernelCombatPlanner.Status.Pending)
            throw new Exception($"Notification churn spun the kernel planner for {notifyTicks} ticks.");
        // The same board must reach the same outcome as the no-churn run above;
        // a stale fallback here means notification noise still discarded a valid
        // search, which is exactly the waste this guards against.
        if (notifyStatus != status || (notifyDecision is null) != (decision is null))
            throw new Exception($"Notification churn changed the kernel outcome ({status} -> {notifyStatus}); it must be invisible to the planner.");
        Console.WriteLine($"PASS: notification-only churn does not discard an in-flight kernel search ({notifyStatus}).");

        // Finished phase: once every human has ended their turn the planner still
        // uses the same bounded budget (end-turn branches now included) and must
        // resolve, not spin.
        var finished = new KernelCombatPlanner();
        var finishedStatus = KernelCombatPlanner.Status.Pending;
        TeamCombatPlanner.Decision? finishedDecision = null;
        var finishedTicks = 0;
        while (finishedStatus == KernelCombatPlanner.Status.Pending && finishedTicks++ < 4000)
            finishedStatus = finished.Poll(combat, new[] { bot }, 0, null, true, BotDifficulty.Pro, out finishedDecision);
        if (finishedStatus == KernelCombatPlanner.Status.Pending)
            throw new Exception("Finished phase (humans finished) must resolve, not spin.");
        Console.WriteLine($"PASS: kernel planner resolves in the finished (humans finished) phase: {finishedStatus}.");
    }

    // P0: (1) an unmodeled card must not disable the kernel for the whole fight;
    // (2) next-round enemy threat must be priced into the team score.
    private static void AuditPartialPlanAndForecast()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 3, 1));
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "PARTIAL"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "p");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(8)), true);
        bot.PlayerCombatState.Hand.AddInternal(
            combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "OneForAll"), bot));
        bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));

        var planner = new KernelCombatPlanner();
        var status = KernelCombatPlanner.Status.Pending;
        TeamCombatPlanner.Decision? decision = null;
        var ticks = 0;
        while (status == KernelCombatPlanner.Status.Pending && ticks++ < 400)
            status = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out decision);
        if (status != KernelCombatPlanner.Status.Ready || decision is null)
            throw new Exception($"An unmodeled card must not disable the kernel; got {status}.");
        Console.WriteLine("PASS: kernel keeps planning when the hand contains an unmodeled card (partial plan).");

        var evalEmpty = new KernelCombatEvaluation(combat, new[] { bot }, null,
            Array.Empty<IReadOnlyList<KernelSession.ForecastAttack>>());
        var evalThreat = new KernelCombatEvaluation(combat, new[] { bot }, null,
            new[] { new[] { new KernelSession.ForecastAttack(foe, 30m, 1) } });
        var evalDeeper = new KernelCombatEvaluation(combat, new[] { bot }, null,
            new[]
            {
                new[] { new KernelSession.ForecastAttack(foe, 30m, 1) },
                new[] { new KernelSession.ForecastAttack(foe, 30m, 1) },
            });
        var root = KernelSession.Capture(combat);
        var emptyScore = evalEmpty.Evaluate(root).Score;
        var threatScore = evalThreat.Evaluate(root).Score;
        var deeperScore = evalDeeper.Evaluate(root).Score;
        if (!(threatScore < emptyScore))
            throw new Exception($"Next-round enemy threat must lower the team score: with={threatScore:F1}, without={emptyScore:F1}");
        if (!(deeperScore < threatScore))
            throw new Exception($"A further round of threat must lower the score again: two={deeperScore:F1}, one={threatScore:F1}");
        Console.WriteLine($"PASS: kernel prices multi-round enemy threat with decay ({deeperScore:F1} < {threatScore:F1} < {emptyScore:F1}).");

        // Pin the actual first-round coefficient (0.25), not just the ordering:
        // a double discount used to make it 0.0625.
        var heavy = new KernelCombatEvaluation(combat, new[] { bot }, null,
            new[] { new[] { new KernelSession.ForecastAttack(foe, 100m, 1) } });
        var heavyDelta = heavy.Evaluate(root).Score - emptyScore;
        if (Math.Abs(heavyDelta + 25) > 1.0)
            throw new Exception($"One future round of 100 raw damage must cost 0.25*100=25, got {-heavyDelta:F2}.");
        Console.WriteLine("PASS: first future round is discounted exactly once (0.25).");
    }

    // Which potions the kernel can simulate, by name. This is the list the
    // optimization plan orders potion work by, instead of guessing which bottle
    // in the log was the missed one.
    private static void AuditPotionCoverage()
    {
        // ModelDb only exposes a generic Potion<T>(), so the coverage sweep has to
        // instantiate each potion type through it by reflection.
        var factory = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "Potion" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
        var all = typeof(PotionModel).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(PotionModel).IsAssignableFrom(t))
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var missing = all.Where(name =>
        {
            var type = typeof(PotionModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Potions." + name);
            if (type is null) return false;
            var potion = ((PotionModel)factory.MakeGenericMethod(type).Invoke(null, null)!).ToMutable();
            return !KernelSession.CanModelPotion(potion);
        }).ToArray();
        Console.WriteLine($"POTION COVERAGE: {all.Length - missing.Length}/{all.Length} simulated; missing="
            + (missing.Length == 0 ? "none" : string.Join(",", missing)));
        // The three bottles the last A10 defeat ended holding must be simulatable,
        // otherwise the search cannot even offer them.
        foreach (var required in new[] { "SpeedPotion", "GigantificationPotion", "ShipInABottle" })
            if (missing.Contains(required))
                throw new Exception($"{required} must be simulatable by the kernel.");

        // CHOOSER potions must NOT be reported as modelable. Their mirror rolls the three
        // offered cards but returns `AddsToHand: false` — it never applies the pick — so the
        // search would plan the rest of the fight as if the bottle added NOTHING while the live
        // game adds the card the chooser picked. Measured live 2026-09-20 (`SLIMES_WEAK`): live
        // hand +1 (`BLOODLETTING`), the plan's step 4 never replayed, eleven drift lines that
        // were all that one displacement, and `card-left-hand` at step 12 — the whole
        // `card-left-hand` family's root cause.
        // R4 stands: an unmodelled effect is a BOUNDARY, not something to plan across.
        // NINE, not four: `PotionChoiceSupport.RequiresChoice` also covers the five pile/pick
        // potions, and none of them can be replayed because the search records no choice
        // (`new Action(actor, null, target, potion)`) and UsePotion passes `choice: null`. The
        // guard used to cover only the four, which is how DROPLET_OF_PRECOGNITION reached a live
        // plan and cost a script (SLIMES_NORMAL, 2026-09-20).
        foreach (var chooser in new[] { "AttackPotion", "SkillPotion", "PowerPotion", "ColorlessPotion",
            "Ashwater", "DropletOfPrecognition", "GamblersBrew", "LiquidMemories", "TouchOfInsanity" })
            if (!missing.Contains(chooser))
                throw new Exception($"{chooser} is a CHOOSER potion whose mirror never applies the pick "
                    + "(AddsToHand: false); it must report unmodelable so the search treats it as a boundary "
                    + "instead of planning across a board that is wrong by one card.");
        // ...and the exclusion has to stay narrow: an ordinary potion must remain modelable.
        foreach (var plain in new[] { "BlockPotion", "EnergyPotion", "FirePotion" })
            if (missing.Contains(plain))
                throw new Exception($"{plain} must stay modelable; only the chooser potions are half-modelled.");
        Console.WriteLine("PASS: chooser potions report unmodelable (boundary) while ordinary potions stay modelable.");

        // Reporting it is not enough — the search must actually REFUSE it, or a branch still
        // expands across the same wrong board and the reporting was cosmetic.
        // DROPLET_OF_PRECOGNITION, not SkillPotion: SkillPotion was ALREADY refused by the
        // narrower four-potion guard, so using it here would pass even after a revert to that
        // guard and could never catch the bug that cost SLIMES_NORMAL its script. This one is
        // only refused once the predicate is `RequiresChoice`.
        var chooserType = typeof(PotionModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Potions.DropletOfPrecognition")!;
        var chooserBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var chooserCombat = new CombatState(runState: RunState.CreateForTest(new[] { chooserBot }, seed: "CHOOSER-REFUSE"));
        chooserBot.ResetCombatState(); chooserCombat.AddPlayer(chooserBot);
        chooserBot.Creature.SetMaxHpInternal(80); chooserBot.Creature.SetCurrentHpInternal(80);
        chooserBot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; chooserBot.PlayerCombatState.GainEnergy(3);
        chooserBot.AddPotionInternal(((PotionModel)factory.MakeGenericMethod(chooserType).Invoke(null, null)!).ToMutable());
        var chooserPotion = chooserBot.Potions.Single();
        var chooserRoot = KernelSession.Capture(chooserCombat);
        var refused = false; var refusalBoundary = "";
        foreach (var target in chooserRoot.PotionTargets(chooserPotion))
        {
            var branch = chooserRoot.Fork();
            if (branch.UsePotion(chooserPotion, target, out var why)) continue;
            refused = true; refusalBoundary = why; break;
        }
        if (!refused || refusalBoundary != "potion-choice-unmodeled")
            throw new Exception($"the search must refuse a chooser potion by name, got refused={refused} "
                + $"boundary='{refusalBoundary}'; an expanded branch plans on a board missing the card the live "
                + "game adds.");
        Console.WriteLine("PASS: the search refuses a chooser potion by name instead of planning across it.");
    }

    // A monster move that only sleeps, stuns or hides is the enemy doing nothing,
    // and must not fail the whole enemy phase closed. Rocket's RECHARGE_MOVE did
    // exactly that: thirty `enemy-move-unmodeled:ROCKET/RECHARGE_MOVE` boundaries
    // in one fight meant no end-turn branch could ever be simulated.
    private static void AuditInertMonsterMove()
    {
        var type = typeof(MonsterModel).Assembly.GetTypes()
            .Single(t => t.Name == "Rocket" && typeof(MonsterModel).IsAssignableFrom(t));
        var factory = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Monster" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "ROCKET"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
        var monster = ((MonsterModel)factory.MakeGenericMethod(type).Invoke(null, null)!).ToMutable();
        var enemy = combat.CreateCreature(monster, CombatSide.Enemy, "rocket");
        combat.AddCreature(enemy); monster.SetUpForCombat();
        enemy.SetMaxHpInternal(120); enemy.SetCurrentHpInternal(120);
        var recharge = monster.MoveStateMachine!.States.Values.OfType<MoveState>().Single(move => move.Id == "RECHARGE_MOVE");
        if (recharge.Intents.Any(intent => intent.IntentType is not IntentType.Sleep))
            throw new Exception("RECHARGE_MOVE is expected to be a sleep-intent move; the rule needs reviewing.");
        monster.SetMoveImmediate(recharge, true);
        var before = KernelSession.Capture(combat);
        var branch = before.Fork();
        if (!branch.EndTurn(bot, 1, out var boundary))
            throw new Exception("A sleep-intent monster move must not fail the enemy phase: " + boundary);
        if (branch.Power<StrengthPower>(enemy) != 0)
            throw new Exception("RECHARGE_MOVE must not change combat numbers, but Strength moved to "
                + branch.Power<StrengthPower>(enemy) + ".");
        Console.WriteLine("PASS: a sleep-intent monster move (Rocket RECHARGE) is simulated instead of bounding the round.");
    }

    // Every potion the kernel claims to model must actually do something when it
    // is used. A mirror that resolves to nothing is worse than a missing one: the
    // search sees a free action, spends the bottle and changes no state.
    private static void AuditPotionMirrorsDoSomething()
    {
        var factory = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "Potion" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
        var failed = new List<string>();
        foreach (var type in typeof(PotionModel).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(PotionModel).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var template = ((PotionModel)factory.MakeGenericMethod(type).Invoke(null, null)!).ToMutable();
            if (!KernelSession.CanModelPotion(template)) continue;

            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
            var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "MIRROR-" + type.Name));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            // Board set up so every potion has something to act on: a wounded
            // player (heals), existing block (Fortifier), an upgradable hand
            // (Blessing of the Forge), an empty slot (Entropic Brew) and a foe.
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(50);
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
            bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(bot));
            for (var i = 0; i < 3; i++)
                bot.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "mirror");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
            bot.AddPotionInternal(((PotionModel)factory.MakeGenericMethod(type).Invoke(null, null)!).ToMutable());

            var potion = bot.Potions.Single();
            var root = KernelSession.Capture(combat);
            var before = root.StateText(bot);
            var outcome = "no-target";
            foreach (var target in root.PotionTargets(potion))
            {
                var branch = root.Fork();
                if (!branch.UsePotion(potion, target, out var boundary))
                { outcome = "refused:" + boundary; continue; }
                outcome = branch.StateText(bot) == before ? "NO CHANGE"
                    : $"changed ({(boundary.Length == 0 ? "ok" : boundary)})";
                break;
            }
            Console.WriteLine($"POTION USE {type.Name}: {outcome}");
            // Only the four card-generation potions may refuse: their result is a
            // card choice the single-step model cannot take. Any other refusal
            // means a bottle that was silently unusable again.
            if (outcome.StartsWith("refused:", StringComparison.Ordinal)
                && type.Name is not ("AttackPotion" or "SkillPotion" or "PowerPotion" or "ColorlessPotion"))
                failed.Add(type.Name + ":" + outcome);
            else if (outcome is "NO CHANGE" or "no-target") failed.Add(type.Name + ":" + outcome);
        }
        if (failed.Count > 0)
            throw new Exception("Modeled potions that simulate as a no-op: " + string.Join(", ", failed));
        Console.WriteLine("PASS: every potion the kernel claims to model changes the simulated board.");
    }

    // Every potion the team planner can hold must actually resolve inside the
    // kernel. A potion with no mirror was silently simulated as a free no-op, so
    // the search never picked it and the slot stayed full for the whole run.
    private static void AuditTeamPotionMirrors()
    {
        KernelSession ForkWithPotion<T>(out Player bot, out PotionModel potion) where T : PotionModel
        {
            bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
            var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "MIRROR-" + typeof(T).Name));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "mirror");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
            bot.AddPotionInternal(ModelDb.Potion<T>().ToMutable());
            potion = bot.Potions.Single();
            return KernelSession.Capture(combat).Fork();
        }

        var speed = ForkWithPotion<SpeedPotion>(out var speedBot, out var speedPotion);
        if (!speed.UsePotion(speedPotion, speedBot.Creature, out var speedBoundary))
            throw new Exception("Speed Potion use failed: " + speedBoundary);
        // 5 Dexterity now, and the same 5 as the temporary power that is paid back
        // at end of turn (SpeedPotionPower derives from TemporaryDexterityPower).
        if (speed.Power<DexterityPower>(speedBot.Creature) != 5
            || speed.Power<SpeedPotionPower>(speedBot.Creature) != 5)
            throw new Exception($"Speed Potion must grant 5 Dexterity and 5 temporary: "
                + $"dex={speed.Power<DexterityPower>(speedBot.Creature)}, temp={speed.Power<SpeedPotionPower>(speedBot.Creature)}");
        Console.WriteLine("PASS: kernel simulates a Speed Potion as 5 Dexterity plus its end-of-turn temporary pair.");

        var giant = ForkWithPotion<GigantificationPotion>(out var giantBot, out var giantPotion);
        if (!giant.UsePotion(giantPotion, giantBot.Creature, out var giantBoundary))
            throw new Exception("Gigantification Potion use failed: " + giantBoundary);
        if (giant.Power<GigantificationPower>(giantBot.Creature) != 1)
            throw new Exception($"Gigantification Potion must apply one stack, got {giant.Power<GigantificationPower>(giantBot.Creature)}.");
        Console.WriteLine("PASS: kernel simulates a Gigantification Potion.");

        var ship = ForkWithPotion<ShipInABottle>(out var shipBot, out var shipPotion);
        if (!ship.UsePotion(shipPotion, shipBot.Creature, out var shipBoundary))
            throw new Exception("Ship in a Bottle use failed: " + shipBoundary);
        if (ship.Block(shipBot.Creature) != 10 || ship.Power<BlockNextTurnPower>(shipBot.Creature) != 10)
            throw new Exception($"Ship in a Bottle must grant 10 Block and 10 next turn: "
                + $"block={ship.Block(shipBot.Creature)}, next={ship.Power<BlockNextTurnPower>(shipBot.Creature)}");
        Console.WriteLine("PASS: kernel simulates a Ship in a Bottle as 10 block now plus 10 next turn.");
    }

    // A buff potion that turns a non-lethal turn into a confirmed kill must be
    // detected by simulating the potion, not guessed from live state.
    private static void AuditProactivePotion()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "POTION"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "potion");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        // Strike deals 6; with +2 Strength it deals 8, so 7 HP flips to a kill.
        foe.SetMaxHpInternal(7); foe.SetCurrentHpInternal(7);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
        bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
        bot.AddPotionInternal(ModelDb.Potion<StrengthPotion>().ToMutable());

        // Isolate the mirror first: using the potion must enable the kill.
        var potion = bot.Potions.Single();
        var directRoot = KernelSession.Capture(combat);
        var directFork = directRoot.Fork();
        if (!directFork.UsePotion(potion, bot.Creature, out var potionBoundary))
            throw new Exception("Strength potion use failed: " + potionBoundary);
        if (!directFork.Play(directFork.Hand(bot).Single(), foe, out var afterPotionBoundary))
            throw new Exception("Strike after potion failed: " + afterPotionBoundary);
        if (directFork.Enemies.Count != 0)
            throw new Exception($"Strength potion did not enable the kill: hp={directFork.Hp(foe)}");
        Console.WriteLine("PASS: kernel simulates a Strength potion and the enabled kill.");

        var planner = new KernelCombatPlanner();
        var status = KernelCombatPlanner.Status.Pending;
        TeamCombatPlanner.Decision? decision = null;
        var ticks = 0;
        while (status == KernelCombatPlanner.Status.Pending && ticks++ < 400)
            status = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out decision);
        if (planner.ConfirmedPotion is not { } plan || plan.Potion.GetType().Name != "StrengthPotion")
            throw new Exception($"A Strength potion that enables a kill must be confirmed, got {planner.ConfirmedPotion?.Potion.Id.Entry ?? "none"} ({status}).");
        if (plan.Target != bot.Creature)
            throw new Exception("The confirmed Strength potion must target the attacker.");
        Console.WriteLine("PASS: kernel confirms a proactive buff potion that turns a non-lethal turn into a kill.");
    }

    // Joint search: the potion and the following cards must be planned together.
    // Two Strikes deal 12; with +2 Strength the same two deal 16, so a 14 HP
    // enemy flips to a kill only through the sequence potion -> Strike -> Strike.
    private static void AuditJointPotionSequence()
    {
        KernelCombatPlanner.Status Run(int enemyHp, out KernelCombatPlanner planner)
        {
            planner = new KernelCombatPlanner();
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
            var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: $"SEQ-{enemyHp}"));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "seq");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(enemyHp); foe.SetCurrentHpInternal(enemyHp);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
            bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            bot.AddPotionInternal(ModelDb.Potion<StrengthPotion>().ToMutable());
            var status = KernelCombatPlanner.Status.Pending;
            TeamCombatPlanner.Decision? decision = null;
            var ticks = 0;
            while (status == KernelCombatPlanner.Status.Pending && ticks++ < 400)
                status = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out decision);
            return status;
        }

        Run(14, out var sequenced);
        if (sequenced.ConfirmedPotion is not { } plan || plan.Potion.GetType().Name != "StrengthPotion")
            throw new Exception("A potion that only enables the kill as part of a sequence must be planned first.");
        if (!plan.Reason.Contains("sequence:3"))
            throw new Exception($"Expected the potion-first sequence to span 3 actions, got: {plan.Reason}");
        Console.WriteLine("PASS: kernel plans a potion-then-combo sequence instead of only a single potion action.");

        // No waste: when the cards alone already kill, the potion must be kept.
        Run(12, out var noWaste);
        if (noWaste.ConfirmedPotion is not null)
            throw new Exception("The potion must be kept when the cards already secure the kill.");
        Console.WriteLine("PASS: kernel keeps the potion when the cards alone already kill.");
    }

    // A Vulnerable potion is wasted on an enemy that already carries several

    // Boss reactive mechanics (e.g. Thorns) must be simulated, so the search
    // prices the reflected damage instead of ignoring or refusing the attack.
    private static void AuditBossReactiveMechanics()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 3));
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "THORNS"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "thorns");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(50); foe.SetCurrentHpInternal(50);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
        ((ThornsPower)ModelDb.Power<ThornsPower>().ToMutable()).ApplyInternal(foe, 3, true);
        var strike = combat.CreateCard<StrikeIronclad>(bot); bot.PlayerCombatState.Hand.AddInternal(strike);

        var root = KernelSession.Capture(combat);
        var branch = root.Fork();
        if (!branch.Play(strike, foe, out var reason))
            throw new Exception($"An enemy with Thorns must still be attackable in the kernel: {reason}");
        if (branch.Hp(foe) != 44)
            throw new Exception($"Strike must deal 6 to the Thorns enemy, got hp={branch.Hp(foe)}");
        if (branch.Hp(bot.Creature) != 77)
            throw new Exception($"Thorns must reflect 3 to the attacker, got hp={branch.Hp(bot.Creature)}");
        Console.WriteLine("PASS: kernel simulates boss reactive damage (Thorns reflects onto the attacker).");
    }

    // A Ritual enemy (e.g. Calcified Cultist) grows every turn. It must be both
    // focused and valued above a lower-HP enemy that will stay harmless.
    private static void AuditScalingThreatFocus()
    {
        (CombatState Combat, Creature Cultist, Creature Mob) Build(bool ritual)
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 4));
            var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "SCALING-" + ritual));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
            var cultist = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "cultist");
            combat.AddCreature(cultist); cultist.Monster!.SetUpForCombat();
            cultist.SetMaxHpInternal(50); cultist.SetCurrentHpInternal(50);
            cultist.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(6)), true);
            var mob = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "mob");
            combat.AddCreature(mob); mob.Monster!.SetUpForCombat();
            mob.SetMaxHpInternal(20); mob.SetCurrentHpInternal(20);
            mob.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(6)), true);
            if (ritual)
                ((RitualPower)ModelDb.Power<RitualPower>().ToMutable()).ApplyInternal(cultist, 3, true);
            return (combat, cultist, mob);
        }

        var (withRitual, cultist, mob) = Build(true);
        var enemies = withRitual.HittableEnemies.Where(e => e.IsAlive).ToList();
        var focus = TeamFocus.Resolve(withRitual, enemies, null);
        if (focus != cultist)
            throw new Exception("Focus must prioritise the Ritual enemy over a lower-HP harmless one.");
        Console.WriteLine("PASS: focus prioritises a scaling (Ritual) enemy over a lower-HP target.");

        var (plain, plainCultist, plainMob) = Build(false);
        var plainEnemies = plain.HittableEnemies.Where(e => e.IsAlive).ToList();
        if (TeamFocus.Resolve(plain, plainEnemies, null) != plainMob)
            throw new Exception("Without scaling, focus must still prefer the lower-HP enemy.");
        Console.WriteLine("PASS: focus keeps the lowest-HP target when nobody scales.");

        var ritualEval = new KernelCombatEvaluation(withRitual, [Party(withRitual)], null);
        var plainEval = new KernelCombatEvaluation(plain, [Party(plain)], null);
        var ritualScore = ritualEval.Evaluate(KernelSession.Capture(withRitual)).Score;
        var plainScore = plainEval.Evaluate(KernelSession.Capture(plain)).Score;
        if (!(ritualScore < plainScore))
            throw new Exception($"A compounding enemy must lower the team score: ritual={ritualScore:F1}, plain={plainScore:F1}");
        Console.WriteLine($"PASS: team score prices compounding enemy growth ({ritualScore:F1} < {plainScore:F1}).");

        // The plan itself must commit damage to the scaler instead of spending
        // the turn blocking and letting it compound.
        var actor = withRitual.Players.First();
        actor.PlayerCombatState!.Hand.AddInternal(withRitual.CreateCard<StrikeIronclad>(actor));
        var searchEval = new KernelCombatEvaluation(withRitual, [actor], null);
        using (var search = new KernelTeamSearch(KernelSession.Capture(withRitual), [actor],
            state => searchEval.Evaluate(state).Score, new(Depth: 5, Width: 4, MaxNodes: 192)))
        {
            while (!search.Advance(TimeSpan.FromMilliseconds(3), () => true)) { }
            var plan = search.CompletedResult!;
            var first = plan.Actions.Count > 0 ? plan.Actions[0] : null;
            if (first?.Card is not { } card || card.Type != CardType.Attack || first.Target != cultist)
                throw new Exception($"The best plan must attack the scaling enemy, got {first?.Card?.Id.Entry ?? "none"}.");
        }
        Console.WriteLine("PASS: the chosen plan attacks the scaling enemy rather than only blocking.");

        static Player Party(CombatState combat) => combat.Players.First();
    }

    // P1-2: a settled enemy phase advances into the next player turn (draw,
    // energy, turn-start triggers) so the search can plan a second round.
    private static void AuditCrossTurn()
    {
        (CombatState Combat, Player Bot, Creature Foe) Build(string seed)
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 5));
            var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: seed));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
            // A real monster with a complete move state machine: the round advance
            // needs real follow-up states, which the mock monsters do not have.
            var foe = combat.CreateCreature(ModelDb.Monster<Nibbit>().ToMutable(), CombatSide.Enemy, "turn");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
            // Use the monster's own registered move so it has a valid follow-up
            // for the round advance (a hand-built move has none).
            var machine = foe.Monster.MoveStateMachine!;
            foe.Monster.SetMoveImmediate((MoveState)machine.States["BUTT_MOVE"], true);
            return (combat, bot, foe);
        }

        var (combat, bot, foe) = Build("CROSSTURN");
        var session = KernelSession.Capture(combat).Fork();
        if (!session.EndTurn(bot, 2, out var endBoundary))
            throw new Exception($"The enemy phase must resolve: {endBoundary} / {session.LastRoundFailure}");
        if (session.RoundsAdvanced != 1 || session.EnemyPhaseCompleted)
            throw new Exception($"A second round must start within budget: rounds={session.RoundsAdvanced}, settled={session.EnemyPhaseCompleted}");
        if (!session.CanAct(bot))
            throw new Exception("The bot must be able to act again in the new round.");
        if (session.Hp(bot.Creature) >= 80)
            throw new Exception($"The enemy attack must have landed: hp={session.Hp(bot.Creature)}");
        Console.WriteLine("PASS: a settled enemy phase advances into an actionable next round.");

        // Once a round has advanced, the root-built forecast must no longer be
        // counted: the branch's own monster AI decides the coming attack.
        var evalForecast = new KernelCombatEvaluation(combat, new[] { bot }, null,
            new[] { new[] { new KernelSession.ForecastAttack(foe, 50m, 1) } });
        var evalEmpty = new KernelCombatEvaluation(combat, new[] { bot }, null,
            Array.Empty<IReadOnlyList<KernelSession.ForecastAttack>>());
        if (Math.Abs(evalForecast.Evaluate(session).Score - evalEmpty.Evaluate(session).Score) > 0.001)
            throw new Exception("A root-built forecast must not be counted after the search advanced a round.");
        Console.WriteLine("PASS: forecasts are not reused from the root after a round advanced.");

        var capped = session.Fork();
        if (!capped.EndTurn(bot, 2, out var cappedBoundary) || capped.RoundsAdvanced != 1 || !capped.EnemyPhaseCompleted)
            throw new Exception($"At the round cap the enemy phase must be the settled terminal: {cappedBoundary}");
        Console.WriteLine("PASS: the round cap stops the search at a settled enemy boundary.");

        // An unmodeled non-attack move must fail this round closed, not throw:
        // a single unknown move must never disable the kernel for the fight.
        var (unmodeledCombat, unmodeledBot, unmodeledFoe) = Build("UNMODELED");
        unmodeledFoe.Monster.SetMoveImmediate(
            new MoveState("CUSTOM_BUFF", _ => Task.CompletedTask, new BuffIntent()), true);
        var unmodeled = KernelSession.Capture(unmodeledCombat).Fork();
        if (unmodeled.EndTurn(unmodeledBot, 2, out var unmodeledBoundary))
            throw new Exception("An unmodeled non-attack move must not resolve.");
        if (!unmodeledBoundary.Contains("enemy-move-unmodeled") || unmodeled.LastRoundFailure is not null)
            throw new Exception($"Expected an explicit boundary without an exception, got: {unmodeledBoundary} / {unmodeled.LastRoundFailure}");
        Console.WriteLine("PASS: an unmodeled enemy move becomes a boundary instead of an exception.");

        // A settled plan must not replace a better card line just because it
        // reached the enemy boundary.
        var (settledCombat, settledBot, _) = Build("SETTLED");
        settledBot.PlayerCombatState!.Hand.AddInternal(settledCombat.CreateCard<StrikeIronclad>(settledBot));
        var settledEval = new KernelCombatEvaluation(settledCombat, new[] { settledBot }, null);
        using (var settledSearch = new KernelTeamSearch(KernelSession.Capture(settledCombat), new[] { settledBot },
            state => settledEval.Evaluate(state).Score,
            new(Depth: 4, Width: 4, MaxNodes: 256, IncludeEndTurns: true, MaxRounds: 1)))
        {
            while (!settledSearch.Advance(TimeSpan.FromMilliseconds(3), () => true)) { }
            var settledPlan = settledSearch.CompletedResult!;
            if (settledPlan.Actions.Count == 0 || settledPlan.Actions[0].Card is null)
                throw new Exception("A better card line must not be replaced by merely settling the enemy phase.");
        }
        Console.WriteLine("PASS: settling the enemy phase does not override a better card line.");
    }

    // Real multiplayer combats carry a MultiplayerScalingModel as a block hook.
    // The upstream mirror threw for playerCount != 1, which made every block card
    // a boundary and forced the kernel to skip all defense in real games.
    private static void AuditMultiplayerBlockScaling()
    {
        var party = new[]
        {
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 6)),
            Player.CreateForNewRun<Deprived>(UnlockState.all, 41UL),
            Player.CreateForNewRun<Deprived>(UnlockState.all, 42UL),
        };
        var run = RunState.CreateForTest(party, seed: "MPSCALE");
        // The singleton is shared; initializing it here would leak into other
        // tests, and player block does not need it anyway.
        var scaling = ModelDb.Singleton<MultiplayerScalingModel>();
        var combat = new CombatState(encounter: null, runState: run, multiplayerScalingModel: scaling);
        scaling.OnCombatEntered(combat);
        foreach (var player in party)
        {
            player.ResetCombatState(); combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "scale");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(5)), true);
        var bot = party[0];
        var defend = combat.CreateCard<DefendIronclad>(bot); bot.PlayerCombatState!.Hand.AddInternal(defend);

        var root = KernelSession.Capture(combat);
        var branch = root.Fork();
        if (!branch.Play(defend, null, out var reason))
            throw new Exception($"A block card must be playable in multiplayer combat: {reason}");
        if (branch.Block(bot.Creature) != 5)
            throw new Exception($"Player block must stay unscaled: got {branch.Block(bot.Creature)}");
        scaling.OnCombatFinished();
        Console.WriteLine("PASS: multiplayer block scaling no longer rejects block cards in the kernel.");
    }

    // When the bots cannot finish an enemy alone but the human's damage covers
    // the remainder, the planner must ask the human for that hit.
    // Turn-scoped enemy Strength loss (PiercingWail / DarkShackles /
    // EnfeeblingTouch / DyingStar) must be simulated, must cut the incoming the
    // evaluation scores, and must be played before a plain block so the rest of
    // the team can size its block to the reduced hit.
    private static void AuditTurnStrengthDown()
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 12, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 12, 2));
        var party = new[] { a, b };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "STRENGTH-DOWN"));
        foreach (var player in party)
        {
            player.ResetCombatState(); combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "wail");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(120); foe.SetCurrentHpInternal(120);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(8)), true);
        var wail = combat.CreateCard<PiercingWail>(a); a.PlayerCombatState!.Hand.AddInternal(wail);

        var eval = new KernelCombatEvaluation(combat, [a], null);
        var root = KernelSession.Capture(combat);
        var scream = root.Fork();
        if (!scream.Play(wail, null, out var reason)) throw new Exception("Kernel PiercingWail: " + reason);
        if (root.IntentHit(foe, a.Creature, 8) != 8)
            throw new Exception($"Live 8-damage intent must read 8, got {root.IntentHit(foe, a.Creature, 8)}.");
        if (scream.IntentHit(foe, a.Creature, 8) != 2)
            throw new Exception($"Piercing Wail (-6 Strength) must cut an 8-damage hit to 2, got {scream.IntentHit(foe, a.Creature, 8)}.");
        if (!(eval.Evaluate(scream).Score > eval.Evaluate(root).Score))
            throw new Exception("Cutting incoming damage must raise the team score.");
        Console.WriteLine("PASS: kernel simulates turn-scoped enemy Strength loss and credits the reduced incoming.");

        // The loss is temporary: it must not survive the enemy side turn end. The
        // round advance needs a real move state machine, so this uses a real
        // monster rather than a hand-built mock move.
        var tBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 12, 3));
        var tAlly = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 12, 4));
        var tParty = new[] { tBot, tAlly };
        var tCombat = new CombatState(runState: RunState.CreateForTest(tParty, seed: "STRENGTH-DOWN-TURN"));
        foreach (var player in tParty)
        {
            player.ResetCombatState(); tCombat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        }
        var tFoe = tCombat.CreateCreature(ModelDb.Monster<Nibbit>().ToMutable(), CombatSide.Enemy, "turn-wail");
        tCombat.AddCreature(tFoe); tFoe.Monster!.SetUpForCombat();
        tFoe.SetMaxHpInternal(60); tFoe.SetCurrentHpInternal(60);
        var machine = tFoe.Monster.MoveStateMachine!;
        tFoe.Monster.SetMoveImmediate((MoveState)machine.States["BUTT_MOVE"], true);
        var turnWail = tCombat.CreateCard<PiercingWail>(tBot); tBot.PlayerCombatState!.Hand.AddInternal(turnWail);

        var turn = KernelSession.Capture(tCombat).Fork();
        if (!turn.Play(turnWail, null, out var wailReason)) throw new Exception("Kernel turn PiercingWail: " + wailReason);
        if (turn.IntentHit(tFoe, tBot.Creature, 8) != 2)
            throw new Exception("Strength loss must apply before the enemy phase.");
        // The enemy phase only runs once every living player has ended the turn.
        turn.EndTurn(tAlly, 2, out _);
        if (!turn.EndTurn(tBot, 2, out var boundary))
            throw new Exception("PiercingWail end turn: " + boundary + " :: " + turn.LastRoundFailure);
        if (turn.IntentHit(tFoe, tBot.Creature, 8) != 8)
            throw new Exception($"Temporary Strength loss must be restored next round, got {turn.IntentHit(tFoe, tBot.Creature, 8)}.");
        Console.WriteLine("PASS: turn-scoped Strength loss is restored once the enemy phase settles.");

        // Ordering: a 5-damage hit is fully covered by either a block or the
        // The team planner must also know a teammate's Strength loss is coming,
        // so it does not keep spending block on damage that is already gone. The
        // block cards are listed first in hand; only the modeled reduction can
        // make the planner leave them unplayed.
        var oBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 12, 5));
        var oAlly = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 12, 6));
        var oParty = new[] { oBot, oAlly };
        var oCombat = new CombatState(runState: RunState.CreateForTest(oParty, seed: "STRENGTH-DOWN-ORDER"));
        foreach (var player in oParty)
        {
            player.ResetCombatState(); oCombat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        }
        var oFoe = oCombat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "order");
        oCombat.AddCreature(oFoe); oFoe.Monster!.SetUpForCombat();
        oFoe.SetMaxHpInternal(120); oFoe.SetCurrentHpInternal(120);
        oFoe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(5)), true);
        var oDefend = oCombat.CreateCard<DefendIronclad>(oBot); oBot.PlayerCombatState!.Hand.AddInternal(oDefend);
        oAlly.PlayerCombatState!.Hand.AddInternal(oCombat.CreateCard<DefendIronclad>(oAlly));
        var withoutWail = TeamCombatPlanner.Choose(oParty, oParty, null);
        if (withoutWail is null || withoutWail.Move.Card?.GetType().Name != "DefendIronclad")
            throw new Exception($"Block must be spent when nothing cuts the incoming, got {withoutWail?.Move.Card?.Id.Entry ?? "none"}.");
        oBot.PlayerCombatState.Hand.AddInternal(oCombat.CreateCard<PiercingWail>(oBot));
        var withWail = TeamCombatPlanner.Choose(oParty, oParty, null);
        if (withWail is null || withWail.Move.Card?.GetType().Name != "PiercingWail")
            throw new Exception($"A teammate's Strength loss must be played before block, got {withWail?.Move.Card?.Id.Entry ?? "none"}.");
        Console.WriteLine("PASS: the team planner plays a teammate's turn-scoped Strength loss before block instead of over-blocking.");

        // Once the Strength loss has already landed, the same block is worthless:
        // the planner must stop spending it rather than over-block.
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(oFoe, -6, true);
        if (CombatAssessment.FromEnemy(oFoe, oBot.Creature) > 0)
            throw new Exception("The applied Strength loss must remove the incoming threat.");
        var covered = TeamCombatPlanner.Choose(oParty, oParty, null);
        if (covered is not null && covered.Move.Card?.GetType().Name == "DefendIronclad")
            throw new Exception("Blocking must not be spent once this turn's incoming is already cut to zero.");
        Console.WriteLine("PASS: the team planner stops spending block once a teammate's Strength loss has landed.");
    }

    // Cards the snapshot cannot mirror used to be dropped from the search
    // entirely. A multiplayer-only support card exists only in co-op, so that
    // made it unplayable by the bots at all — the reported COORDINATE/BLAZE
    // boundaries. The shapes we can describe exactly are priced from the card's
    // own declared variables instead, and stay estimates.
    private static void AuditStructuralFallback()
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 21, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 21, 2));
        var party = new[] { a, b };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "STRUCTURAL"));
        foreach (var player in party)
        {
            player.ResetCombatState(); combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "struct");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(5)), true);

        // BLAZE GRANTS A PLAIN POWER TO ONE ALLY — and it is MODELLED now (2026-09-20).
        //
        // This block used to demand the opposite (`An unmirrored ally buff must be SKIPPED now`),
        // which was right while BLAZE had no spec. The live A10 4-bot round priced that gap
        // (2026-09-20, CEREMONIAL_BEAST_BOSS):
        //   boundaries=BLAZE:prediction-risk:{SourceId=BLAZE, Method=OnPlay, Reason=MethodNotMirrored}x1272
        // — the most frequent boundary in the whole fight, i.e. the card the bot most wanted to
        // play. The registry entry is the same shape CONCOCT got, so the assertion flips to the
        // positive one the old comment invited: play it, and check the ALLY got the declared
        // Strength. `mustBoundary` above is unchanged — modelling one card is a coverage gain,
        // not a policy change, and an unmodelled card must still be skipped.
        var blaze = combat.CreateCard<Blaze>(a); a.PlayerCombatState!.Hand.AddInternal(blaze);
        var blazeBranch = KernelSession.Capture(combat).Fork();
        var allyStrengthBefore = blazeBranch.Power<StrengthPower>(b.Creature);
        if (!blazeBranch.Play(blaze, b.Creature, out var blazeReason))
            throw new Exception($"BLAZE must be playable now, not a boundary: {blazeReason}");
        var allyStrengthAfter = blazeBranch.Power<StrengthPower>(b.Creature);
        if (allyStrengthAfter != allyStrengthBefore + 5)
            throw new Exception($"BLAZE must grant its declared 5 Strength to the chosen ALLY "
                + $"(got {allyStrengthBefore} -> {allyStrengthAfter}); it is AnyAlly, so the buff "
                + "belongs to the target and never to the caster.");
        Console.WriteLine("PASS: BLAZE is modelled — it grants its declared Strength to the chosen ally.");

        // Coordinate grants a temporary Strength power: the paired real Strength
        // must be applied too, or the end-of-turn restore would subtract it.
        a.PlayerCombatState.Hand.RemoveInternal(blaze);
        var coordinate = combat.CreateCard<Coordinate>(a); a.PlayerCombatState.Hand.AddInternal(coordinate);
        var coordinateBranch = KernelSession.Capture(combat).Fork();
        // Same reversal as Blaze: refused, not estimated. The paired-Strength handling this
        // used to assert lives in StructuralCardMirror and is now unreachable from a plan, so
        // asserting it here would only pin dead behaviour.
        if (coordinateBranch.Play(coordinate, b.Creature, out var coordinateReason))
            throw new Exception("An unmirrored temporary-Strength buff must be SKIPPED now, not estimated.");
        if (!coordinateReason.StartsWith("prediction-risk:", StringComparison.Ordinal))
            throw new Exception($"Expected a prediction-risk refusal, got: {coordinateReason}");
        Console.WriteLine("PASS: an unmirrored temporary-Strength buff is skipped as well; "
            + "no unmodelled card can reach a plan on an estimate.");

        // Cards outside the describable shape must keep their boundary: an
        // unmirrored card that does anything else may not be guessed at.
        ClearHands();
        var intercept = combat.CreateCard<Intercept>(a); a.PlayerCombatState.Hand.AddInternal(intercept);
        var interceptRoot = KernelSession.Capture(combat);
        var interceptBranch = interceptRoot.Fork();
        if (interceptBranch.Play(intercept, b.Creature, out var boundary) || boundary.Length == 0)
            throw new Exception("An undescribable unmirrored card must remain a boundary.");
        Console.WriteLine("PASS: cards outside the describable shape are still refused rather than guessed.");

        void ClearHands()
        {
            foreach (var player in party)
                foreach (var card in player.PlayerCombatState!.Hand.Cards.ToArray())
                    player.PlayerCombatState.Hand.RemoveInternal(card);
        }
    }

    // A status that damages its owner for being held must be played, not
    // refused. A real act-1 boss fight lost two teammates to Beckon (6
    // unblockable at end of turn, added two at a time) because every planner
    // treated "status" as "never play".
    private static void AuditHeldStatusPenalty()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 30, 1));
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "HELD-STATUS"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "held");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(120); foe.SetCurrentHpInternal(120);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(6)), true);
        var beckon = combat.CreateCard<Beckon>(bot);
        bot.PlayerCombatState.Hand.AddInternal(beckon);

        var held = GeniusCombatStrategy.HeldPenalty(beckon);
        if (held <= 0)
            throw new Exception("Beckon must be recognised as punishing being held.");
        var evaluation = new KernelCombatEvaluation(combat, [bot], null);
        var root = KernelSession.Capture(combat);
        var before = evaluation.Evaluate(root).Score;
        var branch = root.Fork();
        if (!branch.Play(beckon, null, out var reason)) throw new Exception("Held status: " + reason);
        var after = evaluation.Evaluate(branch).Score;
        if (!(after > before))
            throw new Exception($"Playing a card that costs {held:F0} HP to hold must raise the score: {after:F1} vs {before:F1}.");
        // The individual scorer must offer it too, or the kernel's plan never
        // reaches the queue on the frames the legacy planner acts.
        var move = BotBrain.ChooseCombatMove(bot, 0);
        if (move?.Card.Id.Entry != "BECKON")
            throw new Exception($"A held-penalty status must be played, chose {move?.Card.Id.Entry ?? "nothing"}.");
        Console.WriteLine($"PASS: a status that costs {held:F0} HP to hold is played instead of refused.");

        // The same rule must cover a held card that charges DamageVar instead of
        // HpLossVar: the Aeonglass boss's Wither costs damage at end of turn, and
        // looking only for HpLoss made it invisible — a bot died in its own
        // end-turn phase holding one.
        var wither = combat.CreateCard<Wither>(bot);
        var witherHeld = GeniusCombatStrategy.HeldPenalty(wither);
        if (witherHeld <= 0)
            throw new Exception("Wither must be recognised as a held penalty, not only HpLoss-shaped cards.");
        Console.WriteLine($"PASS: a held card charging Damage ({witherHeld:F0}) is priced like an HpLoss one.");

        // Knowing what a Wither costs is not enough: the legacy scorer prices one
        // card at a time, so it also has to know that the *next* play is the one
        // that hands the player one. The reviewed run's last stand spent its whole
        // hand into the Aeonglass counter and then died to the Withers it made.
        var presence = (WitheringPresencePower)ModelDb.Power<WitheringPresencePower>().ToMutable();
        presence.ApplyInternal(foe, 1, true);
        presence.Target = bot.Creature;
        var counter = presence.DynamicVars.Values.Single(variable => variable.Name == "CardsLeft");
        counter.BaseValue = 2;
        if (KernelSession.PendingHeldStatus(bot) is not null)
            throw new Exception("Two plays left on the counter must not read as a pending status.");
        counter.BaseValue = 1;
        var pending = KernelSession.PendingHeldStatus(bot);
        if (pending is null || pending.Id.Entry != "WITHER")
            throw new Exception($"The play that trips the counter must report the status it creates, got {pending?.Id.Entry ?? "none"}.");
        presence.Target = null;
        if (KernelSession.PendingHeldStatus(bot) is not null)
            throw new Exception("A player the power does not target must not be charged for another's counter.");
        Console.WriteLine("PASS: the play that trips the Aeonglass counter is charged the status it creates.");
    }

    // A held card's end-of-turn damage must be charged against block when it is
    // ordinary damage. Beckon charges HpLoss — no amount of block stops it — while
    // the Aeonglass boss's Wither charges Damage, which block absorbs like any
    // other hit. Both used to land in the same unblockable bucket, so the fast
    // path defended against Wither harder than the game requires.
    private static void AuditHeldPenaltyBlockSplit()
    {
        double ScoreWith<T>(int block, bool upgraded) where T : CardModel
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 31, 1));
            var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "HELD-BLOCK"));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "held");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(200); foe.SetCurrentHpInternal(200);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(6)), true);
            var card = combat.CreateCard<T>(bot);
            if (upgraded) card.UpgradeInternal();
            bot.PlayerCombatState.Hand.AddInternal(card);
            if (block > 0) bot.Creature.GainBlockInternal(block);
            var evaluation = new KernelCombatEvaluation(combat, [bot], null);
            return evaluation.Evaluate(KernelSession.Capture(combat)).Score;
        }

        // How much the same 50 block is worth with an empty hand: it soaks the
        // enemy's 6. Everything below is measured against that baseline.
        var enemyDelta = ScoreWith<StrikeIronclad>(50, false) - ScoreWith<StrikeIronclad>(0, false);
        if (enemyDelta <= 0)
            throw new Exception($"Block must be worth something against the enemy attack, got {enemyDelta:F1}.");
        var witherDelta = ScoreWith<Wither>(50, false) - ScoreWith<Wither>(0, false);
        if (!(witherDelta > enemyDelta + 1))
            throw new Exception($"Block must also absorb a held Wither: {witherDelta:F1} vs enemy-only {enemyDelta:F1}.");
        var beckonDelta = ScoreWith<Beckon>(50, false) - ScoreWith<Beckon>(0, false);
        if (Math.Abs(beckonDelta - enemyDelta) > 0.01)
            throw new Exception($"Block must not absorb Beckon's HpLoss: {beckonDelta:F1} vs enemy-only {enemyDelta:F1}.");
        Console.WriteLine($"PASS: held Damage is blockable while HpLoss is not ({enemyDelta:F1} enemy, "
            + $"{witherDelta:F1} with a Wither, {beckonDelta:F1} with Beckon).");

        // The Aeonglass fight upgrades the Withers it generates (the mirror calls
        // FakeUpgrade, which is how the real card scales past its printed max), so
        // the price has to follow the upgraded damage rather than the printed one.
        var upgradeBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 32, 1));
        var upgradeCombat = new CombatState(runState: RunState.CreateForTest(new[] { upgradeBot }, seed: "HELD-UPGRADE"));
        upgradeBot.ResetCombatState(); upgradeCombat.AddPlayer(upgradeBot);
        var plainWither = upgradeCombat.CreateCard<Wither>(upgradeBot);
        var upgradedWither = upgradeCombat.CreateCard<Wither>(upgradeBot);
        upgradedWither.FakeUpgrade();
        var plainHeld = GeniusCombatStrategy.HeldPenalty(plainWither);
        var upgradedHeld = GeniusCombatStrategy.HeldPenalty(upgradedWither);
        if (!(upgradedHeld > plainHeld))
            throw new Exception($"An upgraded Wither must cost more than a base one: {upgradedHeld:F1} vs {plainHeld:F1}.");
        Console.WriteLine($"PASS: an upgraded Wither is priced above the base one ({plainHeld:F1} -> {upgradedHeld:F1}).");
    }

    // OUTRAGE: damage plus a copy into EVERY player's discard pile. The copy is
    // the part the snapshot could not express; without it the kernel values the
    // card as plain damage and the deck pollution that cost a real act-2 run is
    // invisible to the search.
    // ALL_FOR_ONE's `case` label was missing, so it fell through into ENERGY_SURGE's body and
    // threw KeyNotFoundException (`card.DynamicVars.Energy` does not exist on a card whose only
    // CanonicalVar is DamageVar). Every kernel-suite run printed that as
    //   COVERAGE BROKEN: ALL_FOR_ONE:prediction-exception:KeyNotFoundException: …
    // and it was read as background noise. This asserts the literal effect instead.
    private static void AuditAllForOne()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 43, 1));
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "ALLFORONE"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(999); bot.Creature.SetCurrentHpInternal(999);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(9);
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "a4o");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(999); foe.SetCurrentHpInternal(999);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)), true);
        var allForOne = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.AllForOne>(bot);
        bot.PlayerCombatState.Hand.AddInternal(allForOne);
        // BLOODLETTING is a 0-cost Skill — exactly what ALL_FOR_ONE is supposed to return.
        var zeroCost = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Bloodletting>(bot);
        bot.PlayerCombatState.DiscardPile.AddInternal(zeroCost);

        var root = KernelSession.Capture(combat);
        var branch = root.Fork();
        var energyBefore = branch.Energy(bot);
        if (!branch.Play(allForOne, foe, out var boundary))
            throw new Exception($"ALL_FOR_ONE must be simulatable now, not a prediction exception: {boundary}");
        if (!branch.Hand(bot).Any(c => c.Id.Entry == zeroCost.Id.Entry))
            throw new Exception("ALL_FOR_ONE must return every 0-cost Attack/Skill/Power from the discard pile to hand. "
                + "It used to fall through into ENERGY_SURGE's body (missing `case` label; the compiler's "
                + "unreachable-code warning was the only trace) and threw KeyNotFoundException instead.");
        // NOT `!= energyBefore`: ALL_FOR_ONE costs 2, so spending is correct. The
        // discriminator is the DIRECTION — ENERGY_SURGE's body GRANTS energy, so any increase
        // is the fall-through coming back.
        if (branch.Energy(bot) > energyBefore)
            throw new Exception($"ALL_FOR_ONE must not GRANT energy; that is ENERGY_SURGE's body. "
                + $"got {energyBefore} -> {branch.Energy(bot)}.");
        Console.WriteLine("PASS: ALL_FOR_ONE returns the 0-cost cards from the discard pile and grants no energy.");
    }

    private static void AuditOutrage()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 42, 1));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)442);
        var party = new[] { bot, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "OUTRAGE"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(999); p.Creature.SetCurrentHpInternal(999);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(9);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "rage");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(999); foe.SetCurrentHpInternal(999);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)), true);
        var outrage = combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Outrage>(bot);
        bot.PlayerCombatState!.Hand.AddInternal(outrage);

        var root = KernelSession.Capture(combat);
        var branch = root.Fork();
        var before = branch.Hp(foe);
        if (!branch.Play(outrage, foe, out var boundary))
            throw new Exception($"OUTRAGE must be simulatable now: {boundary}");
        // Enemy must have taken the attack.
        foe.SetCurrentHpInternal(branch.Hp(foe));
        if (branch.Hp(foe) >= before)
            throw new Exception("OUTRAGE must still deal its damage, not only generate copies.");
        // One copy per player, the caster included, in the discard pile.
        foreach (var p in party)
            if (!branch.Discard(p).Any(c => c.Id.Entry == "OUTRAGE"))
                throw new Exception($"OUTRAGE must add a copy to {p.NetId}'s discard pile.");
        Console.WriteLine("PASS: OUTRAGE deals damage and adds a copy to every player's discard pile.");
    }

    // Batch A multiplayer cards whose underlying power hook already exists, so
    // the OnPlay application was the only missing piece. Each assertion checks
    // the card's literal effect, not merely that the card became playable: a
    // boundary that is suppressed without the effect is a confident wrong plan.
    private static void AuditMultiplayerBatchA()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 43, 1));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)443);
        var party = new[] { bot, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "BATCH-A"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(999); p.Creature.SetCurrentHpInternal(999);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(9); p.PlayerCombatState.GainStars(9);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "a");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(999); foe.SetCurrentHpInternal(999);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)), true);

        KernelSession PlayCard<T>(Player owner, Creature? target) where T : CardModel
        {
            var live = combat.CreateCard<T>(owner);
            owner.PlayerCombatState!.Hand.AddInternal(live);
            try
            {
                var branch = KernelSession.Capture(combat).Fork();
                var card = branch.Hand(owner).First(c => c is T);
                var targets = branch.Targets(card);
                var chosen = targets.Contains(target) ? target : targets.FirstOrDefault(t => t is not null);
                if (!targets.Contains(chosen))
                    throw new Exception($"{typeof(T).Name} has no valid target.");
                if (!branch.Play(card, chosen, out var boundary))
                    throw new Exception($"{typeof(T).Name} must be simulatable: {boundary}");
                return branch;
            }
            finally { owner.PlayerCombatState!.Hand.RemoveInternal(live); }
        }

        // Cacophony: power carries the draw interval from its own var.
        var cacophony = PlayCard<Cacophony>(bot, null);
        if (cacophony.Power<CacophonyPower>(bot.Creature) <= 0)
            throw new Exception("CACOPHONY must apply its draw counter power.");
        // Underworld: power on the caster.
        var underworld = PlayCard<Underworld>(bot, null);
        if (underworld.Power<UnderworldPower>(bot.Creature) <= 0)
            throw new Exception("UNDERWORLD must apply its Doom-conversion power.");
        // Soulbound targets the chosen ally and records the caster as applier.
        var soulbound = PlayCard<Soulbound>(bot, ally.Creature);
        if (soulbound.Power<SoulboundPower>(ally.Creature) <= 0)
            throw new Exception("SOULBOUND must land on the chosen ally.");
        // Hibernate: the shared-block power. The Frost channel goes through the
        // same vendor-tested OrbChannel helper Chill/ColdSnap use, but this
        // fixture's Deprived caster has no orb slots to hold the result.
        var hibernate = PlayCard<Hibernate>(bot, null);
        if (hibernate.Power<HibernatePower>(bot.Creature) <= 0)
            throw new Exception("HIBERNATE must apply its Frost-sharing power.");
        // BladeSymphony: every player, caster included, gains the Shivs.
        var symphony = PlayCard<BladeSymphony>(bot, null);
        foreach (var p in party)
            if (!symphony.Hand(p).Any(c => c.Id.Entry == "SHIV"))
                throw new Exception($"BLADE_SYMPHONY must give {p.NetId} Shivs.");
        // GlimpseBeyond: every player's draw pile gains Souls.
        var glimpse = PlayCard<GlimpseBeyond>(bot, null);
        foreach (var p in party)
            if (!glimpse.DrawPile(p).Any(c => c.Id.Entry == "SOUL"))
                throw new Exception($"GLIMPSE_BEYOND must give {p.NetId} a Soul in the draw pile.");
        // LegionOfBone: every player summons an Osty.
        var legion = PlayCard<LegionOfBone>(bot, null);
        foreach (var p in party)
            if (!legion.HasOsty(p))
                throw new Exception($"LEGION_OF_BONE must summon an Osty for {p.NetId}.");
        // Plot: next-turn draw power on every player, not just the caster.
        var plot = PlayCard<Plot>(bot, null);
        foreach (var p in party)
            if (plot.Power<DrawCardsNextTurnPower>(p.Creature) <= 0)
                throw new Exception($"PLOT must grant next-turn draw to {p.NetId}.");
        // DemonicShield: caster pays HP, the chosen ally gains block equal to the
        // caster's current block, so the caster must actually hold block first.
        bot.Creature.GainBlockInternal(20);
        var hpBefore = bot.Creature.CurrentHp;
        var demonic = PlayCard<DemonicShield>(bot, ally.Creature);
        if (demonic.Hp(bot.Creature) >= hpBefore)
            throw new Exception("DEMONIC_SHIELD must cost the caster HP.");
        if (demonic.Block(ally.Creature) <= 0)
            throw new Exception("DEMONIC_SHIELD must give the ally block.");
        Console.WriteLine("PASS: batch-A multiplayer cards apply their literal team effect "
            + "(Cacophony, Underworld, Soulbound, Hibernate, BladeSymphony, GlimpseBeyond, LegionOfBone, Plot, DemonicShield).");
    }

    // FLANKING / KNOCKDOWN: enemy takes extra attack damage from the applier's
    // ALLIES this turn. They are a special Vulnerable, but their stacks compound
    // (three Flanking = 2x2x2 = 8x) instead of extending a duration like
    // Vulnerable does, so the differential has to pin the exact multiplier and
    // prove the applier's own attack is not amplified.
    private static void AuditFlankingKnockdown()
    {
        var applier = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 44, 1));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 44, 2));
        var party = new[] { applier, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "FLANK"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(999); p.Creature.SetCurrentHpInternal(999);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(20);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "flank");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(99_999); foe.SetCurrentHpInternal(99_999);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)), true);

        // Three FLANKING for the applier, three TwinStrikes for the ally, one
        // plain Strike for the applier so its own attack can be checked too.
        for (var i = 0; i < 3; i++) applier.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<Flanking>(applier));
        for (var i = 0; i < 3; i++) ally.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<TwinStrike>(ally));
        applier.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(applier));
        applier.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Knockdown>(applier));
        var upgraded = combat.CreateCard<Knockdown>(applier); upgraded.UpgradeInternal();
        applier.PlayerCombatState.Hand.AddInternal(upgraded);
        var root = KernelSession.Capture(combat);
        var baseHp = root.Hp(foe);

        int Damage(KernelSession branch, bool allyAttacks, int flankings, int knockDowns, bool upgradedKnockdown)
        {
            for (var i = 0; i < flankings; i++)
                if (!branch.Play(branch.Hand(applier).First(c => c is Flanking), foe, out var f))
                    throw new Exception("FLANKING play failed: " + f);
            for (var i = 0; i < knockDowns; i++)
                if (!branch.Play(branch.Hand(applier).First(c => c is Knockdown && c.IsUpgraded == upgradedKnockdown), foe, out var k))
                    throw new Exception("KNOCKDOWN play failed: " + k);
            // Measure only the attack: Knockdown itself deals damage, which
            // would otherwise be read as part of the multiplier.
            var before = branch.Hp(foe);
            var attacker = allyAttacks ? ally : applier;
            var attack = branch.Hand(attacker).First(c => allyAttacks ? c is TwinStrike : c is StrikeIronclad);
            if (!branch.Play(attack, foe, out var a))
                throw new Exception("attack failed: " + a);
            return before - branch.Hp(foe);
        }

        var plainTwin = Damage(root.Fork(), allyAttacks: true, 0, 0, false);
        var flanked1 = Damage(root.Fork(), allyAttacks: true, 1, 0, false);
        var flanked3 = Damage(root.Fork(), allyAttacks: true, 3, 0, false);
        if (flanked1 != plainTwin * 2)
            throw new Exception($"One FLANKING must double ally damage, got {flanked1} vs base {plainTwin}.");
        if (flanked3 != plainTwin * 8)
            throw new Exception($"Three FLANKING must compound to 8x, got {flanked3} vs base {plainTwin}.");

        // The applier's own attack is not amplified.
        var plainStrike = Damage(root.Fork(), allyAttacks: false, 0, 0, false);
        var selfStrike = Damage(root.Fork(), allyAttacks: false, 3, 0, false);
        if (selfStrike != plainStrike)
            throw new Exception($"FLANKING must not amplify the applier's own attack: {selfStrike} vs {plainStrike}.");

        // KNOCKDOWN: one application is double, the upgraded one is triple.
        var knockBase = Damage(root.Fork(), allyAttacks: true, 0, 1, false);
        if (knockBase != plainTwin * 2)
            throw new Exception($"Base KNOCKDOWN must double ally damage, got {knockBase} vs base {plainTwin}.");
        var knockUp = Damage(root.Fork(), allyAttacks: true, 0, 1, true);
        if (knockUp != plainTwin * 3)
            throw new Exception($"Upgraded KNOCKDOWN must triple ally damage, got {knockUp} vs base {plainTwin}.");
        Console.WriteLine($"PASS: FLANKING compounds (1 stack {flanked1}, 3 stacks {flanked3} on base {plainTwin}), "
            + $"excludes the applier, and KNOCKDOWN is 2x / 3x upgraded.");
    }

    // Every action is re-planned from the real board. Only the first action of a
    // search is submitted; there is no speculative tail to replay, so the action
    // after a resolved play must come from a fresh search on the current board.
    private static void AuditReplanAfterAction()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 46, 1));
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "REPLAN"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "replan");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(12); foe.SetCurrentHpInternal(12);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)), true);
        var first = combat.CreateCard<StrikeIronclad>(bot);
        var second = combat.CreateCard<StrikeIronclad>(bot);
        bot.PlayerCombatState.Hand.AddInternal(first);
        bot.PlayerCombatState.Hand.AddInternal(second);

        TeamCombatPlanner.Decision? Plan(KernelCombatPlanner planner, uint version, out int ticks)
        {
            TeamCombatPlanner.Decision? decision = null;
            var status = KernelCombatPlanner.Status.Pending;
            ticks = 0;
            while (status == KernelCombatPlanner.Status.Pending && ticks++ < 2000)
                status = planner.Poll(combat, new[] { bot }, version, null, false, BotDifficulty.Pro, out decision);
            if (status != KernelCombatPlanner.Status.Ready || decision is null)
                throw new Exception($"A two-Strike kill must produce a plan, got {status}.");
            return decision;
        }

        var planner = new KernelCombatPlanner();
        var opening = Plan(planner, 0, out _);
        if (opening.Move.Card is not { } selected)
            throw new Exception("The opening plan must select a card.");
        // No speculative tail: the kept plan's second step was computed for the board
        // AFTER its first step, and that step has not been applied here. Re-polling must
        // not deploy the tail against a board it was never computed for — and it must
        // report that as "not resolved yet", NOT as drift, or the drift counter stops
        // being usable as evidence.
        //
        // It must also WAIT rather than refuse. Upstream awaits the action's completion
        // task between steps (DeployCurrentTurn awaits `actionCompletion`); our tick model
        // cannot block, so the equivalent is Status.Pending with the script intact.
        // Refusing here dropped the script on a plain submit/resolve race — the same
        // shape as the three Reset() sites that killed it silently.
        var immediate = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out var immediateDecision);
        if (immediate != KernelCombatPlanner.Status.Pending || immediateDecision is not null
            || planner.PlanSentinelPendingForTesting != 1 || planner.PlanDriftDetectedForTesting != 0
            || !planner.HasContinuationForTesting)
            throw new Exception($"An unresolved first step must WAIT with the script intact, never "
                + $"refuse it and never call it drift; got {immediate} "
                + $"(refusals={planner.PlanRefusalsForTesting}, sentinelMissing={planner.PlanSentinelMissingForTesting}, "
                + $"drift={planner.PlanDriftDetectedForTesting}, pending={planner.PlanSentinelPendingForTesting}).");

        // The first action is resolved against the real board: its selected card
        // left the hand, its energy was spent, and the foe took its damage. A
        // queued-action version bump then invalidates the in-flight search root.
        bot.PlayerCombatState.Hand.RemoveInternal(selected);
        bot.PlayerCombatState.LoseEnergy(1);
        foe.SetCurrentHpInternal(foe.CurrentHp - 6);
        var replan = planner.Poll(combat, new[] { bot }, 1, null, false, BotDifficulty.Pro, out var replanDecision);

        // The script CONTINUES. This used to re-search: an unresolved step refused the
        // plan and threw the tail away, so every step paid for a fresh search. The board
        // now matches what the plan said step 0 would leave behind, so the retained step 1
        // IS the right next action — and finding it again is exactly the cost the whole
        // "search once, then play the script" design exists to avoid. Chaining a
        // speculative tail onto a board it was never computed for is still impossible:
        // that case is the Pending one above.
        var remaining = ReferenceEquals(selected, first) ? second : first;
        if (replan != KernelCombatPlanner.Status.Ready || replanDecision?.Move.Card is not { } card
            || !ReferenceEquals(card, remaining))
            throw new Exception($"The action after a faithfully resolved play must come from the kept script, "
                + $"got {replan} ({replanDecision?.Move.Card?.Id.Entry ?? "none"}).");
        Console.WriteLine("PASS: a faithfully resolved step continues the kept script, an unresolved one waits, and neither replays a speculative tail.");
    }

    // Handing a one-turn buff to a player who has already ended throws it away,
    // and a bot teammate spends what it is given with certainty. This is the
    // coop rule: aim timed support at the team, and only at a human while they
    // can still act on it.
    private static void AuditCoopBuffTiming()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 47, 1));
        var mate = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 47, 2));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)471);
        var party = new[] { bot, mate, human };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "COOP-TIMING"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "timing");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(5)), true);
        // Both candidates would use it; only the human has already ended.
        mate.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<TwinStrike>(mate));
        human.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<TwinStrike>(human));
        human.PlayerCombatState.Phase = PlayerTurnPhase.End;
        bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<Coordinate>(bot));

        var scored = GeniusCombatStrategy.ScoreLegalMoves(bot,
            BotBrain.PlanningCombatMoves(bot).Where(m => m.Card is Coordinate).ToList(), 0);
        var toMate = scored.FirstOrDefault(m => ReferenceEquals(m.Target, mate.Creature));
        var toHuman = scored.FirstOrDefault(m => ReferenceEquals(m.Target, human.Creature));
        if (toMate.Card is null || toHuman.Card is null)
            throw new Exception("COORDINATE must offer both teammates as targets.");
        if (!(toMate.Score > toHuman.Score))
            throw new Exception($"A one-turn buff must prefer the teammate who still acts "
                + $"({toMate.Score:F1} vs the ended human {toHuman.Score:F1}).");
        Console.WriteLine("PASS: timed support is aimed at a teammate who can still act, never at an ended player.");
    }

    // A damage potion must not be thrown at an enemy the team's own cards can
    // already kill. The reported fight: the enemy sat at 9 HP after poison, the
    // turn was ended, and the next turn opened with an explosive potion.
    private static void AuditPotionOnlyWhenCardsCannotFinisher()
    {
        BotPotionPlanner.Choice? Evaluate(bool giveAttacks)
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 48, 1));
            var mate = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 48, 2));
            var party = new[] { bot, mate };
            var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "POTION-FINISH"));
            foreach (var p in party)
            {
                p.ResetCombatState(); combat.AddPlayer(p);
                p.Creature.SetMaxHpInternal(60); p.Creature.SetCurrentHpInternal(30);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            }
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "finish");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(6); foe.SetCurrentHpInternal(6);
            // A lethal incoming attack, so the potion's rescue branch is reachable.
            foe.Monster.SetMoveImmediate(new MoveState("SMASH", _ => Task.CompletedTask, new SingleAttackIntent(200)), true);
            if (giveAttacks)
            {
                bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
                bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            }
            bot.AddPotionInternal(ModelDb.Potion<ExplosiveAmpoule>().ToMutable());
            return BotPotionPlanner.Evaluate(new[] { bot }, party);
        }

        if (Evaluate(giveAttacks: true) is { } wasted)
            throw new Exception($"A damage potion must be kept when the hand already kills the target "
                + $"(chose {wasted.Reason}).");
        if (Evaluate(giveAttacks: false) is null)
            throw new Exception("The potion must still be thrown when no card can finish the enemy.");
        Console.WriteLine("PASS: a damage potion is kept while the hand can already finish the target.");

        // Past the last shop a bottle has no future, so the final encounter may
        // spend one on a measured gain instead of waiting for a rescue. The
        // reviewed party met the act-3 boss holding six and never drank them.
        BotPotionPlanner.Choice? Final(bool noFuture, bool giveAttacks)
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 49, 1));
            var mate = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 49, 2));
            var party = new[] { bot, mate };
            var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "POTION-FINAL"));
            foreach (var p in party)
            {
                p.ResetCombatState(); combat.AddPlayer(p);
                // Nobody is in danger: the rescue branch cannot fire at all.
                p.Creature.SetMaxHpInternal(60); p.Creature.SetCurrentHpInternal(60);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            }
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "final");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(6); foe.SetCurrentHpInternal(6);
            foe.Monster.SetMoveImmediate(new MoveState("TAP", _ => Task.CompletedTask, new SingleAttackIntent(2)), true);
            if (giveAttacks)
                bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            bot.AddPotionInternal(ModelDb.Potion<ExplosiveAmpoule>().ToMutable());
            return BotPotionPlanner.EvaluateWithFuture(new[] { bot }, party, noFuture);
        }

        if (Final(noFuture: false, giveAttacks: false) is not null)
            throw new Exception("Outside the final encounter a bottle must be kept when nobody is in danger.");
        var last = Final(noFuture: true, giveAttacks: false);
        if (last is null || last.Reason != "final-encounter-no-future")
            throw new Exception($"The final encounter must spend a bottle on a kill the cards cannot get, got {last?.Reason ?? "nothing"}.");
        if (Final(noFuture: true, giveAttacks: true) is not null)
            throw new Exception("0.34.1 still holds: a kill the hand already has must not be bought with a potion.");
        // Without a map there is no final encounter to detect, which is also what
        // keeps this from firing in an ordinary act-3 fight.
        var mapBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 49, 3));
        mapBot.ResetCombatState();
        if (BotPotionPlanner.NoFutureForPotion(mapBot))
            throw new Exception("A player with no readable map position must not be treated as the final encounter.");
        Console.WriteLine("PASS: the final encounter spends a bottle on a kill the cards cannot get, and only there.");
    }

    // Once every human is dead the team owns the fight. The predicate is still
    // reported for callers that describe the state, but it no longer changes the
    // planner budget: the same bounded search runs whether or not a human lives.
    private static void AuditSoloTakeover()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 49, 1));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)491);
        var party = new[] { bot, human };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "SOLO"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(50); p.Creature.SetCurrentHpInternal(50);
        }
        if (BotCooperation.AllHumansDown(combat.RunState))
            throw new Exception("A living human means the team is not alone in the fight.");
        human.Creature.SetCurrentHpInternal(0);
        if (!BotCooperation.AllHumansDown(combat.RunState))
            throw new Exception("With every human dead the team must own the rest of the fight.");
        Console.WriteLine("PASS: the solo-takeover state is detected exactly when no human is left alive.");
    }

    // The removed escalation is the regression: humans finishing (or dying) must not widen the
    // search budget, so the scheduled wall clock is the same in every phase.
    //
    // REWRITTEN 2026-09-20 for bounded lookahead (user spec), and the two questions it was
    // conflating now have different answers:
    //   * does the PHASE change the budget? Still no — asserted on both table shapes;
    //   * is the budget SMALL? Only on a table with a human in it. The small bound exists so a
    //     human's turn is not delayed, and an all-bot table has nobody to delay: it is exactly
    //     the table the kernel plans 5-round segments for, so it gets the real segment budget.
    // The old single-fixture form passed only because a bare `new KernelCombatPlanner()` has
    // `openingSearch == false`, so it read the per-tier budget by accident.
    private static void AuditBoundedSearchPolicy()
    {
        int WallBudget(bool finished, bool withHuman)
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 51, 1));
            var party = withHuman
                ? new[] { bot, Player.CreateForNewRun<Deprived>(UnlockState.all, 1) }
                : new[] { bot };
            var combat = new CombatState(runState: RunState.CreateForTest(party, seed: $"BUDGET-{finished}-{withHuman}"));
            foreach (var p in party) { p.ResetCombatState(); combat.AddPlayer(p); }
            foreach (var p in party) { p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); }
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play; bot.PlayerCombatState.GainEnergy(3);
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "budget");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
            bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            var planner = new KernelCombatPlanner();
            var status = planner.Poll(combat, new[] { bot }, 0, null, finished, BotDifficulty.Pro, out _);
            if (status != KernelCombatPlanner.Status.Pending)
                throw new Exception($"Bounded-policy probe must start a search, got {status}.");
            var field = typeof(KernelCombatPlanner).GetField("wallBudgetMs", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("KernelCombatPlanner.wallBudgetMs was not found.");
            var budget = (int)field.GetValue(planner)!;
            planner.Reset();
            return budget;
        }

        var live = WallBudget(false, withHuman: false);
        var finished = WallBudget(true, withHuman: false);
        if (live != finished)
            throw new Exception($"Live and finished phases must share one wall budget, got {live} vs {finished}.");
        // All-bot: a real 5-round computation, i.e. the three-minute base search, not 450ms.
        if (finished <= 450 || finished > 180_000)
            throw new Exception($"An all-bot table must get the real segment budget (above the 450ms interactive "
                + $"bound and at most the 3-minute base search), got {finished}ms.");

        var humanLive = WallBudget(false, withHuman: true);
        var humanFinished = WallBudget(true, withHuman: true);
        if (humanLive != humanFinished)
            throw new Exception($"Live and finished phases must share one wall budget with a human present, "
                + $"got {humanLive} vs {humanFinished}.");
        if (humanFinished > 450)
            throw new Exception($"Pro's INTERACTIVE wall budget must stay bounded at 450ms when a human is seated, "
                + $"got {humanFinished}ms — this is the bound that keeps a human's turn from being delayed.");
        Console.WriteLine($"PASS: one search policy per phase, and the budget follows the table: "
            + $"all-bot={finished}ms (real 5-round segments), with-human={humanFinished}ms (interactive); "
            + "live==finished in both.");
    }

    // Cheap live-board gate: an idle team must answer without capturing or
    // searching, but zero energy is not idle and a potion can still be the action.
    private static void AuditIdleGating()
    {
        KernelCombatPlanner IdlePlanner(out Player bot, out CombatState combat)
        {
            bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 52, 1));
            combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "IDLE-GATE"));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
            bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            bot.PlayerCombatState.LoseEnergy(bot.PlayerCombatState.Energy); // exactly zero energy
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "idle");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
            foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
            return new KernelCombatPlanner();
        }

        // (1) Unaffordable card, no potion: immediate fallback with no search.
        var idle = IdlePlanner(out var idleBot, out var idleCombat);
        idleBot.PlayerCombatState!.Hand.AddInternal(idleCombat.CreateCard<StrikeIronclad>(idleBot));
        var idleStatus = idle.Poll(idleCombat, new[] { idleBot }, 0, null, false, BotDifficulty.Pro, out var idleDecision);
        if (idleStatus != KernelCombatPlanner.Status.Fallback || idleDecision is not null)
            throw new Exception($"An unaffordable hand with no potion must fall back immediately, got {idleStatus}.");
        if (idle.LastNoAction != KernelCombatPlanner.NoActionKind.Idle)
            throw new Exception($"An out-of-cards board must be classified Idle, got {idle.LastNoAction}.");
        if (idle.IsSearching)
            throw new Exception("An idle board must not start a kernel search.");
        idle.Poll(idleCombat, new[] { idleBot }, 0, null, true, BotDifficulty.Pro, out _);
        if (idle.LastNoAction != KernelCombatPlanner.NoActionKind.Idle)
            throw new Exception("The idle verdict must hold after the humans finish.");

        // (2) A zero-cost card is playable at zero energy and must not be skipped.
        var zero = IdlePlanner(out var zeroBot, out var zeroCombat);
        zeroBot.PlayerCombatState!.Hand.AddInternal(zeroCombat.CreateCard<BattleTrance>(zeroBot));
        var zeroStatus = zero.Poll(zeroCombat, new[] { zeroBot }, 0, null, false, BotDifficulty.Pro, out _);
        if (zeroStatus != KernelCombatPlanner.Status.Pending)
            throw new Exception($"A zero-cost card at zero energy must start a search, got {zeroStatus}.");
        zero.Reset();

        // (3) An energy potion unlocks an unaffordable hand: keep searching.
        var potion = IdlePlanner(out var potionBot, out var potionCombat);
        potionBot.PlayerCombatState!.Hand.AddInternal(potionCombat.CreateCard<StrikeIronclad>(potionBot));
        potionBot.AddPotionInternal(ModelDb.Potion<EnergyPotion>().ToMutable());
        var potionStatus = potion.Poll(potionCombat, new[] { potionBot }, 0, null, false, BotDifficulty.Pro, out _);
        if (potionStatus != KernelCombatPlanner.Status.Pending)
            throw new Exception($"An energy potion with an unaffordable card must remain searchable, got {potionStatus}.");
        potion.Reset();
        Console.WriteLine("PASS: idle boards short-circuit without capture/search; zero-cost cards and energy potions are not skipped.");
    }

    // A completed search that recommends nothing caches that verdict against the
    // bot-visible board. Finishing the humans changes nothing the team can see, so
    // the cached verdict must be reused instead of paying for another search.
    private static void AuditNoActionStampReuse()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 53, 1));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)531);
        var party = new[] { bot, human };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "NOACTION"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "noaction");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(4)), true);
        // The only playable card is unmodeled, so the search can recommend nothing
        // and the no-action verdict is what gets cached.
        bot.PlayerCombatState.Hand.AddInternal(
            combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "OneForAll"), bot));

        var planner = new KernelCombatPlanner();
        var status = KernelCombatPlanner.Status.Pending;
        var ticks = 0;
        while (status == KernelCombatPlanner.Status.Pending && ticks++ < 2000)
            status = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out _);
        if (status != KernelCombatPlanner.Status.Fallback || planner.LastNoAction != KernelCombatPlanner.NoActionKind.Boundary)
            throw new Exception($"An unmodeled-only hand must cache a boundary no-action verdict, got {status}/{planner.LastNoAction} "
                + $"(ticks={ticks}, searching={planner.IsSearching}, plans={planner.PlansForTesting}, "
                + $"planRefusals={planner.PlanRefusalsForTesting}, firstActionRefused={planner.FirstActionRefusalsForTesting}, "
                + $"noAction={planner.NoActionFallbacksForTesting}, exceptions={planner.ExceptionFallbacksForTesting}, "
                + $"stale={planner.StaleFallbacksForTesting}, noScript={planner.NoScriptTicksForTesting}).");

        human.PlayerCombatState!.Phase = PlayerTurnPhase.End;
        status = planner.Poll(combat, new[] { bot }, 0, null, true, BotDifficulty.Pro, out _);
        if (status != KernelCombatPlanner.Status.Fallback || planner.IsSearching)
            throw new Exception($"The unchanged no-action verdict must be reused after the humans finish, got {status}.");
        Console.WriteLine("PASS: an unchanged no-action board is reused across the humans-finished transition.");
    }

    // A human merely ending their turn changes nothing the team can see: same
    // hands, same energy, same intents. The "nothing to play" verdict from before
    // that therefore still holds, which is why the planner keys it on a stamp
    // built from the acting players only.
    private static void AuditBotStampIgnoresHumanEndTurn()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 50, 1));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)501);
        var party = new[] { bot, human };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "BOTSTAMP"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
        }
        var botBefore = KernelSession.CaptureBotStamp(combat, new[] { bot });
        var liveBefore = KernelSession.CaptureLiveStamp(combat);
        human.PlayerCombatState!.Phase = PlayerTurnPhase.End;
        if (KernelSession.CaptureBotStamp(combat, new[] { bot }) != botBefore)
            throw new Exception("A human ending their turn must not change the board the team can see.");
        if (KernelSession.CaptureLiveStamp(combat) == liveBefore)
            throw new Exception("The full stamp should still notice the human's phase change.");
        Console.WriteLine("PASS: a human ending their turn leaves the bot-visible stamp intact while the full stamp moves.");
    }

    // Authoritative coverage probe: ask the kernel, per card, whether it can
    // simulate the card's OnPlay at all. That is the same question the live
    // planner asks, so it cannot drift from shipped behaviour the way a static
    // supported-list can. It also catches a mirror that throws, which a static
    // list would happily call "supported".
    private static void AuditCardCoverage()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 41, 1));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)441);
        var party = new[] { bot, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "CARD-COVERAGE"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(999); p.Creature.SetCurrentHpInternal(999);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(99); p.PlayerCombatState.GainStars(99);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "cov");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(99999); foe.SetCurrentHpInternal(99999);
        foe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)), true);
        var factory = typeof(CombatState).GetMethods()
            .First(m => m.Name == "CreateCard" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

        var modeled = new List<string>();
        var unmodeled = new List<string>();
        var choice = new List<string>();
        var broken = new List<string>();
        var skipped = new List<string>();
        string? variantEntry = null;

        void Probe(CardModel probeCard, string label)
        {
            bot.PlayerCombatState!.Hand.AddInternal(probeCard);
            try
            {
                var branch = KernelSession.Capture(combat).Fork();
                if (!branch.CanPlay(probeCard)) { skipped.Add($"{label}:{probeCard.Type}"); return; }
                var target = branch.Targets(probeCard).FirstOrDefault(t => t is not null);
                if (branch.Play(probeCard, target, out var boundary)) { modeled.Add(label); return; }
                if (boundary.StartsWith("prediction-risk:", StringComparison.Ordinal)) unmodeled.Add(label);
                else if (boundary.StartsWith("prediction-exception:", StringComparison.Ordinal)) broken.Add($"{label}:{boundary}");
                else if (boundary == "pending-choice") choice.Add(label);
                else skipped.Add($"{label}:{boundary}");
            }
            catch (Exception error) { broken.Add($"{label}:{error.GetType().Name}"); }
            finally { bot.PlayerCombatState!.Hand.RemoveInternal(probeCard); }
        }

        foreach (var type in typeof(CardModel).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition
                && t.Namespace == "MegaCrit.Sts2.Core.Models.Cards" && typeof(CardModel).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            CardModel card;
            try { card = (CardModel)factory.MakeGenericMethod(type).Invoke(combat, new object[] { bot })!; }
            catch { skipped.Add(type.Name + "(create)"); continue; }

            // A card whose behaviour is picked by a runtime-assigned field cannot be probed
            // by a plain instantiation. MadScience keeps CardType.None until the TinkerTime
            // event assigns TinkerTimeType, and MadScienceOnPlay's switch then falls through
            // to its default throw — so the generic path reported "broken" for a card whose
            // three real variants are all mirrored. That is a hole in this probe, and it
            // cost more than noise: `broken` was pinned at that one false positive, so it
            // could no longer go red when a mirror really broke (AGENT.md R5c — a counter
            // that is always the same number is not evidence). Probe the variants instead.
            // Keyed on the card name on purpose: a *new* variant card should still land in
            // `broken` and make someone look at it.
            var variantProperty = type.Name == "MadScience"
                ? type.GetProperty("TinkerTimeType",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                : null;
            var variantSetter = variantProperty?.GetSetMethod(nonPublic: true);
            // Recorded outside the setter check on purpose: the guard below must still fire
            // when the *lookup* is what broke. Written inside the branch the first time, and
            // that made the guard skip exactly the mutation it exists to catch.
            if (type.Name == "MadScience") variantEntry = card.Id.Entry;
            if (variantSetter is not null)
            {
                foreach (var variant in new[] { CardType.Attack, CardType.Skill, CardType.Power })
                {
                    variantSetter.Invoke(card, new object[] { variant });
                    Probe(card, $"{card.Id.Entry}:{variant}");
                }
                continue;
            }

            Probe(card, card.Id.Entry);
        }

        // Pin the variant probe down. It is the only thing keeping `broken` honest: if the
        // setter lookup stops matching (a renamed field, a card moved out of Cards/), the
        // card silently falls back to the generic path, `broken` quietly returns to 1, and
        // nothing goes red -- which is exactly the failure mode this fix removed.
        //
        // R2 (AGENT.md): verified red by renaming the property lookup above to
        // "TinkerTimeTypeX" -- exit 1, and the coverage line never printed:
        //   System.Exception: the MadScience variant probe did not model MAD_SCIENCE:Attack;
        //   without it the coverage scan reports a false 'broken' (see AuditCardCoverage).
        //      at KernelEngineScenarios.AuditCardCoverage() ... KernelEngineScenarios.cs:line 2267
        // Reverted and re-ran: exit 0, broken=0.
        //
        // That check also caught the first version of this guard being self-defeating: it
        // read the entry id only inside the setter branch, so renaming the lookup skipped
        // the guard entirely and everything stayed green. Hence the assignment above.
        if (variantEntry is { } pinned)
            foreach (var variant in new[] { CardType.Attack, CardType.Skill, CardType.Power })
                if (!modeled.Contains($"{pinned}:{variant}"))
                    throw new Exception(
                        $"the MadScience variant probe did not model {pinned}:{variant}; "
                        + "without it the coverage scan reports a false 'broken' (see AuditCardCoverage).");
        var total = modeled.Count + unmodeled.Count + choice.Count + broken.Count + skipped.Count;
        Console.WriteLine($"CARD COVERAGE: total={total} modeled={modeled.Count} unmodeled={unmodeled.Count} "
            + $"choice={choice.Count} broken={broken.Count} skipped={skipped.Count}");
        Console.WriteLine("COVERAGE UNMODELED: " + string.Join(" ", unmodeled.OrderBy(n => n, StringComparer.Ordinal)));
        Console.WriteLine("COVERAGE CHOICE: " + string.Join(" ", choice.OrderBy(n => n, StringComparer.Ordinal)));
        Console.WriteLine("COVERAGE BROKEN: " + string.Join(" ", broken.OrderBy(n => n, StringComparer.Ordinal)));
        Console.WriteLine("COVERAGE SKIPPED: " + string.Join(" ", skipped.OrderBy(n => n, StringComparer.Ordinal)));
        if (unmodeled.Count == 0) throw new Exception("Coverage probe found nothing unmodeled; it is not probing.");
    }

    /// <summary>
    /// A board for the continuation invariants: two driven seats, an enemy that does not
    /// kill anyone, and enough energy to play.
    /// </summary>
    private static (Player Bot, Player Other, CombatState Combat) ContinuationBoard(string seed)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 9, 9));
        var other = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 10, 10));
        var party = new[] { bot, other };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            // A crossing flushes the hand and draws the next one, so the draw pile has to
            // exist or the round settles with an exception instead of a turn boundary.
            for (var i = 0; i < 5; i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "inv");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(200); foe.SetCurrentHpInternal(200);
        // The move has to come FROM the state machine, not be a detached MoveState: a
        // crossing prepares the monster's next round by walking FollowUpState, and a
        // hand-built move has none ("行动 ATTACK 没有后继状态").
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (bot, other, combat);
    }

    /// <summary>
    /// The four invariants the "one search, then play the script" feature rests on. Every
    /// one of them was found by a LIVE run rather than by this suite, and every one of
    /// them costs a manual game session to rediscover, so each gets an assertion here.
    ///
    /// <list type="number">
    /// <item>the state text renders the BRANCH's turn, never the live board's current one;</item>
    /// <item><c>TurnNumber</c> is the branch's own round arithmetic, and a crossing carries it;</item>
    /// <item><c>Reset()</c> clears the search and can never reach the committed script;</item>
    /// <item>giving a script up is a single, named, counted decision.</item>
    /// </list>
    /// </summary>
    private static void RunContinuationInvariants()
    {
        // (1) A live value must not leak into the recorded state text.
        //
        // This is the bug that cost the 2026-09-20 fight: StateText rendered
        // `player.PlayerCombatState.TurnNumber` — the LIVE number — so every recorded
        // "after" text was stamped with the turn the search started on. The first step
        // past any boundary then compared against a board that had moved on and reported
        // `field=turn expected={1} actual={2}`, forever, on every crossing.
        {
            var (bot, _, combat) = ContinuationBoard("CONTINUATION-1");
            var session = KernelSession.Capture(combat);
            var before = session.StateText(bot);
            bot.PlayerCombatState!.IncrementTurnNumber();
            var after = session.StateText(bot);
            if (before != after)
                throw new Exception("Continuation: the branch state text follows the LIVE turn number; "
                    + "an invariant that only holds while the game has not moved on cannot be a script check.\n"
                    + $"  search-time render: {before}\n  after live TurnNumber++: {after}");
            Console.WriteLine("PASS: the branch state text does not move when only the live turn number moves.");
        }

        // (2) TurnNumber must be round arithmetic on the branch, and a crossing must carry it.
        // Upstream passes the snapshot's own turn (`CombatBeamSolver.PathDiagnostics.cs:44`);
        // this pins the same property on our side.
        {
            var (bot, other, combat) = ContinuationBoard("CONTINUATION-2");
            var root = KernelSession.Capture(combat);
            var rootTurn = root.TurnNumber;
            var crossed = root.Fork();
            foreach (var seat in new[] { bot, other })
                if (!crossed.EndTurn(seat, 2, out var boundary))
                    throw new Exception($"Continuation: end-turn for {seat.NetId} did not settle: {boundary} "
                        + $"[{crossed.LastRoundFailure}]");
            if (crossed.TurnNumber != rootTurn + 1)
                throw new Exception($"Continuation: TurnNumber={crossed.TurnNumber} after one crossing, expected {rootTurn + 1}.");
            var text = crossed.StateText(bot);
            if (!text.Contains($"turn={rootTurn + 1}", StringComparison.Ordinal))
                throw new Exception($"Continuation: a crossed branch renders the wrong turn (expected turn={rootTurn + 1}): {text}");
            Console.WriteLine("PASS: a crossed branch reports its own turn number, not the root's.");
        }

        // (3) and (4): Reset() must leave the script alone, and giving it up must be a
        // named decision that is counted. Three separate reset sites in the runtime used
        // to destroy it, each at a turn boundary, each with no log line.
        {
            var (bot, _, combat) = ContinuationBoard("CONTINUATION-3");
            bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            var planner = new KernelCombatPlanner();
            var status = KernelCombatPlanner.Status.Pending;
            TeamCombatPlanner.Decision? decision = null;
            var ticks = 0;
            while (status == KernelCombatPlanner.Status.Pending && ticks++ < 400)
                status = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out decision);
            if (status != KernelCombatPlanner.Status.Ready)
                throw new Exception($"Continuation: the planner never became Ready (status={status}, ticks={ticks}).");
            if (!planner.HasContinuationForTesting)
                throw new Exception($"Continuation: no script was committed (status={status}, ticks={ticks}).");
            planner.Reset();
            if (!planner.HasContinuationForTesting)
                throw new Exception("Continuation: Reset() dropped the committed script. This is the bug that silently "
                    + "handed the tail of three live fights to the legacy planner while planRefusals stayed 0.");
            if (planner.ContinuationDropsForTesting != 0)
                throw new Exception("Continuation: Reset() counted a drop; only DropContinuation may give a script up.");
            planner.DropContinuationForTesting("regression probe");
            if (planner.HasContinuationForTesting || planner.ContinuationDropsForTesting != 1)
                throw new Exception("Continuation: dropping a script did not go through the named, counted path.");
            Console.WriteLine("PASS: Reset() clears the search and leaves the committed script alone; "
                + "giving it up is a named, counted decision.");
        }

        // (4b) EXHAUSTION IS COMPLETION, NOT A REFUSAL.
        // A script that plays its own last step and stops is the normal end of a plan that
        // did not reach the end of the fight. It used to be reported as
        //   `plan step refused: index=9/9 reason=plan-exhausted` + `plan dropped: …`
        // and counted in planRefusals. Measured live 2026-09-20, two consequences:
        //   * the acceptance metric could not tell "the script was violated" from "the
        //     script finished";
        //   * the STRICT EXECUTION branch — whose counter, noScript, is written to say how
        //     much of the fight the legacy planner covered — was UNREACHABLE, which is why
        //     noScript read 0 in every summary of every round.
        // CHECKED (R2): routing exhaustion back into the refusal path turns this red with
        //   System.Exception: Exhausting the script counted 1 refusal(s); completion must
        //   not be counted as a refusal, or planRefusals cannot measure strictness.
        {
            var (bot, _, combat) = ContinuationBoard("CONTINUATION-EXHAUST");
            bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            var planner = new KernelCombatPlanner();
            var status = KernelCombatPlanner.Status.Pending;
            TeamCombatPlanner.Decision? decision = null;
            var ticks = 0;
            while (status == KernelCombatPlanner.Status.Pending && ticks++ < 400)
                status = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out decision);
            if (!planner.HasContinuationForTesting || planner.PlanLengthForTesting <= 0)
                throw new Exception($"Exhaustion probe: no script was committed (status={status}, ticks={ticks}).");
            // The cursor is the only thing that decides exhaustion, so moving it is an exact
            // stand-in for having played every step — and it does not require replaying a fight.
            planner.SeekPlanForTesting(planner.PlanLengthForTesting);
            var after = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out _);
            if (planner.PlanRefusalsForTesting != 0)
                throw new Exception($"Exhausting the script counted {planner.PlanRefusalsForTesting} refusal(s); "
                    + "completion must not be counted as a refusal, or planRefusals cannot measure strictness.");
            if (planner.ContinuationDropsForTesting != 0)
                throw new Exception("Exhausting the script counted a drop; playing the last step is not giving the script up.");
            if (planner.HasContinuationForTesting)
                throw new Exception("Exhausting the script left it committed, so the tick would re-report it forever.");
            // BOUNDED LOOKAHEAD CONTRACT (user spec 2026-09-20): a finished script is not the
            // end of the kernel's involvement — the same tick must start planning the NEXT
            // script. Before this, completion left planCommitted set and every later tick went
            // through the STRICT EXECUTION branch into the legacy planner, which was correct
            // under "search once, then play the whole fight" and is exactly wrong once the
            // horizon is five rounds.
            if (after != KernelCombatPlanner.Status.Pending || !planner.IsSearching)
                throw new Exception($"Exhausting the script resolved to {after} (searching={planner.IsSearching}); under "
                    + "bounded lookahead it must plan the next script rather than hand the rest of the fight to the legacy planner.");
            // noScript is an ALARM now, not a coverage measure: the kernel never stops thinking.
            if (planner.NoScriptTicksForTesting != 0)
                throw new Exception($"Exhausting the script counted {planner.NoScriptTicksForTesting} noScript tick(s); under "
                    + "bounded lookahead the kernel plans the next script instead, so noScript must stay 0 as an alarm.");
            Console.WriteLine("PASS: exhausting the script is completion — no refusal, no drop, and the next script starts planning.");
        }

        // (4d) A SEAT THAT LEAVES THE FIGHT MUST NOT COST THE WHOLE SCRIPT.
        // A script is priced for the seats that were alive and waiting when the search ran. In a
        // four-seat fight one can die mid-script, and every remaining step of that seat is then
        // unplayable (a dead seat has no hand, so it reads `card-left-hand`). Treating that as a
        // refusal threw away the surviving seats' perfectly executable steps. Measured live
        // 2026-09-20 (CEREMONIAL_BEAST_BOSS, A10 4-bot): three seats died to STOMP_MOVE and the
        // survivor's script was dropped with
        //   `plan step refused: index=19/32 card=EXPECT_A_FIGHT card-left-hand seat_eligible=False`
        {
            var (bot, mate, combat) = ContinuationBoard("CONTINUATION-SEATSKIP");
            foreach (var p in new[] { bot, mate })
                for (var i = 0; i < 5; i++)
                    p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            var planner = new KernelCombatPlanner();
            var status = KernelCombatPlanner.Status.Pending;
            TeamCombatPlanner.Decision? decision = null;
            var ticks = 0;
            while (status == KernelCombatPlanner.Status.Pending && ticks++ < 4000)
                status = planner.Poll(combat, new[] { bot, mate }, 0, null, true, BotDifficulty.Pro, out decision);
            if (status != KernelCombatPlanner.Status.Ready || !planner.HasContinuationForTesting)
                throw new Exception($"Seat-skip probe: no script was committed (status={status}, ticks={ticks}).");
            // The mate leaves the fight. From here the ONLY eligible actor is the bot.
            mate.Creature.SetCurrentHpInternal(0);
            var foe = combat.HittableEnemies.First();
            for (var step = 0; step < 12 && planner.SeatSkipsForTesting == 0; step++)
            {
                var next = planner.Poll(combat, new[] { bot }, 0, null, true, BotDifficulty.Pro, out decision);
                if (planner.SeatSkipsForTesting > 0) break;
                if (next != KernelCombatPlanner.Status.Ready || decision is null) break;
                // Apply the step so the script's cursor advances toward the dead seat's steps.
                decision.Player.PlayerCombatState!.Hand.RemoveInternal(decision.Move.Card!);
                decision.Player.PlayerCombatState.LoseEnergy(1);
                foe.SetCurrentHpInternal(Math.Max(0, foe.CurrentHp - 6));
            }
            if (planner.SeatSkipsForTesting == 0)
                throw new Exception("a script step belonging to a seat that can no longer act was not skipped; "
                    + "it would have been refused as card-left-hand, throwing away the surviving seats' steps.");
            if (planner.PlanRefusalsForTesting != 0)
                throw new Exception($"a seat that left the fight cost {planner.PlanRefusalsForTesting} refusal(s); leaving the "
                    + "fight is not a plan violation — R4 allows only executability to refuse, and the other seats' steps "
                    + "are still executable.");
            Console.WriteLine($"PASS: a seat that leaves the fight is SKIPPED, not refused "
                + $"(skips={planner.SeatSkipsForTesting}, refusals={planner.PlanRefusalsForTesting}).");
        }

        // (4c) BOUNDED LOOKAHEAD: the horizon must be REACHED and must COUNT AS SUCCESS.
        // This is the pair that the first attempt at bounded lookahead got wrong, and each half
        // is load-bearing:
        //   * with nothing paying for the horizon, the evaluator prefers a stub — measured live
        //     2026-09-20 with MaxRounds=3, `best partial: actions=2, nodes=1616`, because a
        //     deeper line eats enemy turns and the position at the end of it is worth no more
        //     than the position at the start;
        //   * with HasRoute still meaning "found a line that ENDS the fight", the opening ladder
        //     fired on every fight and its cap branch disabled the kernel for the rest of combat
        //     (`every action via=legacy`).
        {
            var (bot, mate, combat) = ContinuationBoard("CONTINUATION-HORIZON");
            // Nobody can win and nobody can die: the ONLY way this search can succeed is by
            // reaching the round horizon, which is precisely the case a bounded search exists for.
            foreach (var foe in combat.HittableEnemies)
            { foe.SetMaxHpInternal(5000); foe.SetCurrentHpInternal(5000); }
            foreach (var p in new[] { bot, mate })
            {
                p.Creature.SetMaxHpInternal(999); p.Creature.SetCurrentHpInternal(999);
                for (var i = 0; i < 5; i++)
                    p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            }
            var planner = new KernelCombatPlanner();
            // The OPENING shape, i.e. unbounded depth. A fresh planner is the INTERACTIVE shape
            // (depth 9), which cannot even cross round 1 with two seats holding five cards — the
            // horizon would then be unreachable and the assertion below would fail for a reason
            // that has nothing to do with the horizon value.
            planner.Reset(newCombat: true);
            var status = KernelCombatPlanner.Status.Pending;
            TeamCombatPlanner.Decision? decision = null;
            var ticks = 0;
            while (status == KernelCombatPlanner.Status.Pending && ticks++ < 4000)
                status = planner.Poll(combat, new[] { bot, mate }, 0, null, true, BotDifficulty.Pro, out decision);
            if (status != KernelCombatPlanner.Status.Ready)
                throw new Exception($"Bounded-lookahead probe did not resolve (status={status}, ticks={ticks}).");
            // A stub is what a missing horizon value produces; a real horizon line is far longer.
            var length = planner.PlanLengthForTesting;
            // THE DISCRIMINATOR IS CROSSINGS, NOT LENGTH. A stub can be long: with nothing paying
            // for the horizon the search plays every card in hand and then stops inside round one,
            // which passed a `length >= 6` check and hid the bug. A horizon-reaching line must
            // have crossed turns.
            if (planner.PlanBoundariesForTesting == 0)
                throw new Exception($"the bounded search committed a {length}-action script with ZERO turn crossings. "
                    + "A line that reaches the horizon must cross rounds; stopping inside round one means nothing paid "
                    + "for reaching the horizon, which is the bug that made the first bounded-lookahead attempt plan a "
                    + "stub and then hand the fight to the legacy planner.");
            if (length < 6)
                throw new Exception($"the bounded search committed a {length}-action script, which is too short to be a "
                    + "horizon-reaching line.");
            // CHECKED (R2), and be honest about which half: reverting `HasRoute` to
            // `bestTerminal is not null` turns this red with
            //   System.Exception: a line that reaches the horizon must report HasRoute=true. …
            // The HorizonBonus half is NOT discriminated by this fixture, and the attempt to do so
            // was instructive: removing the bonus still produced a 54-action script here, because
            // on a synthetic board with a 5000-HP foe, striking it makes crossing rounds
            // tactically ATTRACTIVE, so nothing needs to pay for the horizon. The bonus is needed
            // when crossing is tactically EXPENSIVE, which is the real board — measured live
            // 2026-09-20 with MaxRounds=3: `best partial: actions=2, nodes=1616`, i.e. play two
            // cards and stop. So its necessity is evidenced live, not here; this assertion still
            // guards the OUTCOME (a script that crosses rounds and reports success).
            if (!planner.HasRouteForTesting)
                throw new Exception("a line that reaches the horizon must report HasRoute=true. It is read by deployment, "
                    + "the opening ladder AND the ladder's cap branch — a false there made the ladder fire on every fight "
                    + "and its cap branch disable the kernel for the whole combat.");
            Console.WriteLine($"PASS: bounded lookahead reaches its horizon and counts it as success "
                + $"(script={length} actions, HasRoute={planner.HasRouteForTesting}).");
        }

        // (5) The HANDOFF, which is where this bug actually lived. Every earlier assertion
        // here tested one producer in isolation; the boundary bug was a disagreement
        // between two producers, so no amount of per-producer testing could see it.
        {
            var (bot, other, combat) = ContinuationBoard("CONTINUATION-4");
            var root = KernelSession.Capture(combat);

            // (5a) Recorder and comparator must build byte-identical texts for the same
            // board. This already existed as a LOG LINE in production and nowhere else:
            // when the two formats drift, every boundary is silently refused and the only
            // symptom is "reuse never happens", which reads like "no benefit" rather than
            // like a bug.
            var live = KernelSession.CaptureLivePartyText(combat);
            var branch = root.PartyStateText();
            if (live != branch)
                throw new Exception("Continuation: the branch and live TABLE texts disagree at the root, "
                    + $"where they describe the same board — no boundary can ever match (liveLen={live.Length}, "
                    + $"branchLen={branch.Length}).\n  live={live}\n  branch={branch}");

            // (5b) The text must be TABLE-wide. A per-seat text is what the boundary check
            // used to record and then compare against a different seat's view, so exactly
            // one of these fired on every crossing — with no drift and no differing field
            // to report, because both texts were individually "correct".
            foreach (var seat in new[] { bot, other })
            {
                if (live.Contains($"p{seat.NetId}{{", StringComparison.Ordinal)) continue;
                throw new Exception($"Continuation: the table text is missing seat {seat.NetId}.");
            }
            foreach (var seat in new[] { bot, other })
                if (live == root.StateText(seat) || branch == root.StateText(seat))
                    throw new Exception($"Continuation: the table text IS one seat's view ({seat.NetId}); "
                        + "a text that names a viewpoint cannot be compared across seats.");
            Console.WriteLine("PASS: the recorded table text and the live table text agree byte for byte, "
                + "and neither is any single seat's view.");
        }

        // (6) Per-turn counters must RESET when the round crosses. Upstream's round
        // transition calls BeginSideTurn between AdvancePlayerTurn and
        // SnapshotPowerAmountsAtTurnStart (CombatBeamSolver.RoundTransition.cs:26-32); our
        // copy of that sequence omitted it, so nothing that "this turn" counts was ever
        // reset on the player side. The visible symptom was a boundary refusal reading
        // `field=Y expected={0/1/4/4} actual={0/0/0/0}`; the real damage was that every
        // turn-two-and-later line the search priced was evaluated against the previous
        // turn's totals.
        {
            static string TurnHistory(string text)
            {
                var at = text.IndexOf(";Y=", StringComparison.Ordinal);
                if (at < 0) return "missing";
                var end = text.IndexOf(';', at + 3);
                return text[(at + 3)..(end < 0 ? text.Length : end)];
            }

            var (bot, other, combat) = ContinuationBoard("CONTINUATION-5");
            var strike = combat.CreateCard<StrikeIronclad>(bot);
            bot.PlayerCombatState!.Hand.AddInternal(strike);
            var session = KernelSession.Capture(combat);

            // ONE branch: play, then cross. The counters are a MEMOISED CACHE
            // (GetCardPlayStartsThisTurn returns `_cardPlayStartsThisTurn[owner]` if present,
            // and only BeginSideTurn clears it), so a play recorded in one fork and a
            // crossing performed in another proves nothing at all — the inspected branch
            // never saw the play, and the assertion passes with or without the reset.
            var branch = session.Fork();
            if (!branch.Play(strike, combat.Enemies[0], out var why))
                throw new Exception($"Continuation: the probe play did not resolve: {why}");
            // Non-vacuous: a turn that counted nothing would read 0/0/0/0 here too.
            var played = TurnHistory(branch.PartyStateText());
            if (played == "0/0/0/0")
                throw new Exception("Continuation: the probe play moved no turn counter, so the reset test proves nothing.");

            // TWO crossings. With one, "the enemy attacked once this enemy phase" and "the
            // enemy never resets and has attacked once so far" are the SAME number — only a
            // second crossing separates them (1 = it reset, 2 = it did not).
            for (var round = 0; round < 2; round++)
                foreach (var seat in new[] { bot, other })
                    if (!branch.EndTurn(seat, 3, out var boundary))
                        throw new Exception($"Continuation: end-turn for {seat.NetId} did not settle: {boundary} "
                            + $"[{branch.LastRoundFailure}]");

            var afterCrossing = TurnHistory(branch.PartyStateText());
            if (afterCrossing != "0/0/0/0")
                throw new Exception($"Continuation: per-turn counters survived the round crossing "
                    + $"(Y={afterCrossing}, expected 0/0/0/0). Every later turn would be searched against "
                    + "the previous turn's totals — the simulator and the live board disagree from turn two on.");

            // The enemy side of the same reset. The state text cannot show it: enemies render
            // as combat_id/hp/max_hp/block/move and their per-turn counters appear nowhere,
            // which is why the omission there was invisible to every assertion above.
            var enemyAttacks = branch.CreatureAttacksThisTurnForTesting(combat.Enemies[0]);
            if (enemyAttacks != 1)
                throw new Exception($"Continuation: the enemy's per-turn attack count is {enemyAttacks} after two "
                    + "rounds (expected 1 — one enemy phase since the last reset). A value of 2 means the enemy "
                    + "side never resets its per-turn state, so every later round is priced against accumulated "
                    + "enemy bookkeeping the live game has already cleared.");
            Console.WriteLine("PASS: per-turn counters reset on the round crossing, on both sides.");
        }

        // (7) THE PIPELINE, not its parts. Every assertion above tests one component; the
        // live failures were all in the SEAM between them — a searched script refused at its
        // second or third step, which no component test can see. This drives the real path:
        // search -> deploy -> apply the step to the live board -> ask again, and demands that
        // every step after the first comes from the SCRIPT.
        //
        // The applier is deliberately best-effort (card leaves hand, energy spent, foe takes
        // its damage — no discard pile, no RNG bookkeeping). That is enough because the
        // per-step state comparison is now LOGGED and not refused: an imperfect live board
        // must still let the script run. If that ever becomes a refusal again, this test goes
        // red at step two — which is exactly the bug class it exists for.
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 11, 11));
            var mate = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 12, 12));
            var party = new[] { bot, mate };
            var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "PIPELINE-1"));
            foreach (var p in party)
            {
                p.ResetCombatState(); combat.AddPlayer(p);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
                for (var i = 0; i < 5; i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            }
            var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "pipe");
            combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
            // Enough damage on the table that the searched best line OPENS with cards: with
            // one Strike each the best line began by ending the turn, and the probe has to
            // exercise multi-step card reuse, which is where the live failures were.
            foe.SetMaxHpInternal(60); foe.SetCurrentHpInternal(60);
            foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
            foreach (var p in party)
                for (var i = 0; i < 3; i++) p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));

            var planner = new KernelCombatPlanner();
            TeamCombatPlanner.Decision? decision = null;
            var status = KernelCombatPlanner.Status.Pending;
            var ticks = 0;
            while (status == KernelCombatPlanner.Status.Pending && ticks++ < 600)
                status = planner.Poll(combat, party, 0, null, true, BotDifficulty.Pro, out decision);
            if (status != KernelCombatPlanner.Status.Ready || decision?.Move.Card is not { } opened)
                throw new Exception($"Continuation: the pipeline probe could not get a plan (status={status}, ticks={ticks}).");
            if (!planner.HasContinuationForTesting)
                throw new Exception("Continuation: Ready without a committed script; there is nothing to follow.");

            var scripted = 1;
            var lastStatus = KernelCombatPlanner.Status.Ready;
            var lastReason = decision.Move.Reason;
            for (var step = 0; step < 8; step++)
            {
                decision!.Player.PlayerCombatState!.Hand.RemoveInternal(opened);
                decision.Player.PlayerCombatState.LoseEnergy(1);
                foe.SetCurrentHpInternal(Math.Max(0, foe.CurrentHp - 6));
                var next = planner.Poll(combat, party, 0, null, true, BotDifficulty.Pro, out decision);
                lastStatus = next;
                lastReason = decision?.Move.Reason ?? "-";
                if (next != KernelCombatPlanner.Status.Ready) break;
                // An EndTurn step from the script arrives with decision == null and
                // ConfirmedEndTurn set (that is how BotRuntime reads it). It counts: crossing
                // the turn IS the script continuing. The harness stops here rather than
                // driving the live round, which needs the real game's turn machinery.
                if (decision is null)
                {
                    if (planner.ConfirmedEndTurn is null)
                        throw new Exception("Continuation: Ready with neither a card nor a confirmed end turn.");
                    scripted++;
                    break;
                }
                if (decision.Move.Card is not { } card) break;
                if (!decision.Move.Reason.Contains("kernel-plan:reuse", StringComparison.Ordinal)) break;
                opened = card;
                scripted++;
            }
            if (scripted < 3)
                throw new Exception($"Continuation: only {scripted} step(s) came from the kept script; the plan is not "
                    + "being followed past its first action, which is the failure every component test above missed. "
                    + $"[lastStatus={lastStatus} lastReason={lastReason} refusals={planner.PlanRefusalsForTesting} "
                    + $"drift={planner.PlanDriftDetectedForTesting} missing={planner.PlanSentinelMissingForTesting} "
                    + $"pending={planner.PlanSentinelPendingForTesting} script={planner.HasContinuationForTesting}]");
            Console.WriteLine($"PASS: the kept script is followed step by step past its first action ({scripted} steps).");
        }

        // (8) The round-drift predicate itself. The form that refused every step after a
        // crossing is one line, and one line is the cheapest thing in this file to pin.
        if (!KernelCombatPlanner.AllowsRoundDrift(liveRound: 2, planRound: 1, boundaryHere: true, validatedRoundDelta: 0)
            || !KernelCombatPlanner.AllowsRoundDrift(2, 1, boundaryHere: false, validatedRoundDelta: 1))
            throw new Exception("Continuation: a validated crossing must let the REST of that round be played.");
        if (KernelCombatPlanner.AllowsRoundDrift(2, 1, boundaryHere: false, validatedRoundDelta: 0))
            throw new Exception("Continuation: a round change with no validated crossing must still be refused.");
        Console.WriteLine("PASS: round drift is allowed for the rest of a validated round, and refused when never validated.");


        // (9) The intercepted deck-select screens. Our prefix runs BEFORE each
        // FromDeckFor* body, so the predicate that body composes has to be mirrored by hand —
        // and until 2026-09-20 only the transformation case was. A live Neow removal took
        // 永恒 / Eternal Ascender's Bane out of a deck because the removal screen handed the
        // brain the whole deck.
        {
            var deckBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 13, 13));
            var deckCombat = new CombatState(runState: RunState.CreateForTest(new[] { deckBot }, seed: "DECKFILTER-1"));
            deckBot.ResetCombatState(); deckCombat.AddPlayer(deckBot);
            var eternal = deckCombat.CreateCard<AscendersBane>(deckBot);
            var plain = deckCombat.CreateCard<StrikeIronclad>(deckBot);
            var deck = new CardModel[] { eternal, plain };

            var removable = BotCardChoiceDispatcher.NativeDeckPredicate(
                nameof(MegaCrit.Sts2.Core.Commands.CardSelectCmd.FromDeckForRemoval),
                deck, Array.Empty<object>()).ToArray();
            if (removable.Contains(eternal))
                throw new Exception("Continuation: the card-removal screen still offers an unremovable card (Eternal) — "
                    + "this is the live bug where a Neow removal took Ascender's Bane.");
            if (!removable.Contains(plain))
                throw new Exception("Continuation: the removal screen dropped a removable card.");

            var upgradable = BotCardChoiceDispatcher.NativeDeckPredicate(
                nameof(MegaCrit.Sts2.Core.Commands.CardSelectCmd.FromDeckForUpgrade),
                deck, Array.Empty<object>()).ToArray();
            if (upgradable.Contains(eternal))
                throw new Exception("Continuation: the upgrade screen offers a card that cannot be upgraded.");
            Console.WriteLine("PASS: the intercepted deck screens apply the native predicate "
                + "(an Eternal card is offered for neither removal nor upgrade).");
        }

        // (10) HP is priced by the FRACTION of a pool it is, not by the raw number. Without
        // this, ten points off an 80-HP bruiser cost the same as ten points off a 40-HP
        // squishy, so "spend your own blood for tempo" was systematically undervalued.
        {
            var tank = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 21, 21));
            var squishy = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 22, 22));
            var hpParty = new[] { tank, squishy };
            var hpCombat = new CombatState(runState: RunState.CreateForTest(hpParty, seed: "HPWEIGHT-1"));
            foreach (var p in hpParty)
            {
                p.ResetCombatState(); hpCombat.AddPlayer(p);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            }
            tank.Creature.SetMaxHpInternal(80); tank.Creature.SetCurrentHpInternal(80);
            squishy.Creature.SetMaxHpInternal(40); squishy.Creature.SetCurrentHpInternal(40);
            var hpFoe = hpCombat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "hp");
            hpCombat.AddCreature(hpFoe); hpFoe.Monster!.SetUpForCombat();
            hpFoe.SetMaxHpInternal(200); hpFoe.SetCurrentHpInternal(200);
            hpFoe.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);

            var hpMetrics = new KernelCombatEvaluation(hpCombat, hpParty, null)
                .Evaluate(KernelSession.Capture(hpCombat));
            // Both seats take 10. Pool sizes are 80 and 40, so the reference is 60 and the
            // weighted loss is 10*60/80 + 10*60/40 = 7.5 + 15 = 22.5. The raw sum was 20.0,
            // which is what this pins: the tank's point of HP must be cheaper than the
            // squishy's, or a blood-price line reads as expensive as any other.
            if (hpMetrics.HpLoss < 21.0 || hpMetrics.HpLoss > 24.0)
                throw new Exception($"Continuation: ten points on an 80-pool plus ten on a 40-pool should "
                    + $"cost ~22.5 (weighted by pool size), got {hpMetrics.HpLoss:F3}. A value of 20.0 means "
                    + "raw-HP scoring is back, and with it the discount on every blood-price line.");
            Console.WriteLine($"PASS: HP loss is weighted by pool size "
                + $"(10 on an 80-pool + 10 on a 40-pool costs {hpMetrics.HpLoss:F3}, not 20.000).");
        }


    }

    private static void Clear(Player p)
    {
        foreach (var c in p.PlayerCombatState!.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(c);
    }

    private static bool FormatText(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
    { __result = __instance.LocEntryKey; return false; }
}
