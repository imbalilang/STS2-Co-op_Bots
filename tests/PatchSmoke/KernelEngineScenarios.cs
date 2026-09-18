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
        Check(drawBranch.Play(offering, a.Creature, out reason), "Offering: " + reason);
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
        Check(noDraw.Play(battleTrance, a.Creature, out reason), "BattleTrance repeat: " + reason);
        var handBefore = noDraw.Hand(a).Count;
        Check(noDraw.Play(offeringAfterTrance, a.Creature, out reason) && noDraw.Hand(a).Count == handBefore - 1,
            "NoDraw must suppress Offering draw.");
        Check(enemy.CurrentHp == 200 && a.Creature.CurrentHp == 80 && a.PlayerCombatState.Energy == 3,
            "All searches must leave the real combat unchanged.");
        ClearHands();
        var voidForm = Hand<VoidForm>(a);
        var endRoot = KernelSession.Capture(combat); var endBranch = endRoot.Fork();
        Check(endBranch.Play(voidForm, a.Creature, out reason) && endBranch.IsReady(a), "VoidForm must end only its caster's turn: " + reason);
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
        Check(softBlock.Play(softDefend, a.Creature, out reason), "soft-finish defend: " + reason);
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
        AuditRedundantPotionDeclined();
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
        var structurallyPriced = new HashSet<string> { "Concoct" };
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
        foreach (var name in structurallyPriced)
            if (outcome[name] != "MODELED")
                throw new Exception($"A structurally priced ally buff must stay playable: {name} was {outcome[name]}.");
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
        if (!sneakyBranch.Play(sneaky, a.Creature, out var sneakyReason)) throw new Exception("Kernel Sneaky: " + sneakyReason);
        if (sneakyBranch.Power<SneakyPower>(a.Creature) <= 0)
            throw new Exception("Kernel Sneaky must apply SneakyPower to its owner.");

        ClearAll();
        var beacon = combat.CreateCard(ModelDb.AllCards.First(c => c.GetType().Name == "BeaconOfHope"), a);
        a.PlayerCombatState!.Hand.AddInternal(beacon);
        var beaconRoot = KernelSession.Capture(combat);
        var beaconBranch = beaconRoot.Fork();
        if (!beaconBranch.Play(beacon, a.Creature, out var beaconReason)) throw new Exception("Kernel BeaconOfHope: " + beaconReason);
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
            if (!branch.Play(skill, bot.Creature, out var reason)) throw new Exception("Enrage skill: " + reason);
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
    // stacks unless the line turns it into a kill this turn. Real run: a boss at
    // 15% HP with four Vulnerable stacks, and a bot drank the potion, then died
    // with the slot empty. The search cannot see this because it prices the
    // resulting board, not the potion slot it consumes.
    private static void AuditRedundantPotionDeclined()
    {
        if (!KernelCombatPlanner.RedundantDebuff("VulnerablePotion", livingEnemy: true, diesThisPlan: false, existingPower: 4))
            throw new Exception("A Vulnerable potion on a target that already has stacks and survives must be declined.");
        if (!KernelCombatPlanner.RedundantDebuff("WeakPotion", true, false, 2))
            throw new Exception("A Weak potion must get the same redundancy check as Vulnerable.");
        // A kill this turn is exactly when the extra Vulnerable does real work.
        if (KernelCombatPlanner.RedundantDebuff("VulnerablePotion", true, true, 4))
            throw new Exception("A potion that turns the line into a kill must not be declined as redundant.");
        // No existing stacks, or a single expiring one, must stay usable.
        if (KernelCombatPlanner.RedundantDebuff("VulnerablePotion", true, false, 0)
            || KernelCombatPlanner.RedundantDebuff("VulnerablePotion", true, false, 1))
            throw new Exception("A refresh with no or one stack must not be treated as redundant.");
        if (KernelCombatPlanner.RedundantDebuff("StrengthPotion", true, false, 4))
            throw new Exception("Only debuff potions are subject to the redundancy rule.");
        if (KernelCombatPlanner.RedundantDebuff("VulnerablePotion", livingEnemy: false, diesThisPlan: false, existingPower: 4))
            throw new Exception("A non-enemy target must not be declined by the enemy-debuff rule.");
        Console.WriteLine("PASS: redundant debuff potions are declined unless they convert the line into a kill.");
    }

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
        if (!branch.Play(defend, bot.Creature, out var reason))
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

        // Blaze grants a plain power to one ally.
        var blaze = combat.CreateCard<Blaze>(a); a.PlayerCombatState!.Hand.AddInternal(blaze);
        var blazeBranch = KernelSession.Capture(combat).Fork();
        if (!blazeBranch.Play(blaze, b.Creature, out var reason))
            throw new Exception($"An unmirrored ally buff must stay playable: {reason}");
        if (blazeBranch.Power<StrengthPower>(b.Creature) != 5)
            throw new Exception($"Blaze must grant its declared 5 Strength, got {blazeBranch.Power<StrengthPower>(b.Creature)}.");
        if (blazeBranch.Power<StrengthPower>(a.Creature) != 0)
            throw new Exception("Blaze must only buff the chosen ally.");
        if (!blazeBranch.LastActionHadEnvironmentalRisk)
            throw new Exception("A structurally priced card must stay an estimate, never a confirmed line.");
        Console.WriteLine("PASS: an unmirrored ally buff is priced from its own variables instead of being dropped.");

        // Coordinate grants a temporary Strength power: the paired real Strength
        // must be applied too, or the end-of-turn restore would subtract it.
        a.PlayerCombatState.Hand.RemoveInternal(blaze);
        var coordinate = combat.CreateCard<Coordinate>(a); a.PlayerCombatState.Hand.AddInternal(coordinate);
        var coordinateBranch = KernelSession.Capture(combat).Fork();
        if (!coordinateBranch.Play(coordinate, b.Creature, out reason))
            throw new Exception($"An unmirrored temporary-Strength buff must stay playable: {reason}");
        if (coordinateBranch.Power<CoordinatePower>(b.Creature) != 5)
            throw new Exception($"Coordinate must apply its declared power, got {coordinateBranch.Power<CoordinatePower>(b.Creature)}.");
        if (coordinateBranch.Power<StrengthPower>(b.Creature) != 5)
            throw new Exception($"A temporary Strength buff must also raise Strength, got {coordinateBranch.Power<StrengthPower>(b.Creature)}.");
        Console.WriteLine("PASS: a temporary Strength buff carries its paired Strength so the restore cannot invert it.");

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
        // No speculative tail: an immediate re-poll on the same planner and board
        // must begin a fresh search instead of handing back the rest of the old
        // plan for free.
        var immediate = planner.Poll(combat, new[] { bot }, 0, null, false, BotDifficulty.Pro, out var immediateDecision);
        if (immediate != KernelCombatPlanner.Status.Pending || immediateDecision is not null || !planner.IsSearching)
            throw new Exception($"The next action must start a fresh search, not replay a tail; got {immediate}.");

        // The first action is resolved against the real board: its selected card
        // left the hand, its energy was spent, and the foe took its damage. A
        // queued-action version bump then invalidates the in-flight search root.
        bot.PlayerCombatState.Hand.RemoveInternal(selected);
        bot.PlayerCombatState.LoseEnergy(1);
        foe.SetCurrentHpInternal(foe.CurrentHp - 6);
        var stale = planner.Poll(combat, new[] { bot }, 1, null, false, BotDifficulty.Pro, out var staleDecision);
        if (stale != KernelCombatPlanner.Status.Fallback || staleDecision is not null || planner.IsSearching)
            throw new Exception($"A stale root must be discarded without a decision, got {stale}.");

        // The same planner now re-plans from the actual board and must pick the
        // card that is still in hand.
        var remaining = ReferenceEquals(selected, first) ? second : first;
        var next = Plan(planner, 1, out _);
        if (next.Move.Card is not { } card || !ReferenceEquals(card, remaining))
            throw new Exception($"The action after a resolved play must be re-planned from the current board, got {next.Move.Card?.Id.Entry ?? "none"}.");
        Console.WriteLine("PASS: the next action is re-planned from the current board; no speculative tail is replayed.");
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

    // The removed escalation is the regression: humans finishing (or dying) must
    // not widen the search budget, so the scheduled wall clock is the same small
    // bound in every phase.
    private static void AuditBoundedSearchPolicy()
    {
        int WallBudget(bool finished)
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 51, 1));
            var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: "BUDGET-" + finished));
            bot.ResetCombatState(); combat.AddPlayer(bot);
            bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
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

        var live = WallBudget(false);
        var finished = WallBudget(true);
        if (live != finished)
            throw new Exception($"Live and finished phases must share one bounded wall budget, got {live} vs {finished}.");
        if (finished > 450)
            throw new Exception($"Pro's scheduled wall budget must stay bounded at 450ms, got {finished}.");
        Console.WriteLine($"PASS: one bounded search policy in every phase (Pro wall={finished}ms; live==finished).");
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
            throw new Exception($"An unmodeled-only hand must cache a boundary no-action verdict, got {status}/{planner.LastNoAction}.");

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
        foreach (var type in typeof(CardModel).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition
                && t.Namespace == "MegaCrit.Sts2.Core.Models.Cards" && typeof(CardModel).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            CardModel card;
            try { card = (CardModel)factory.MakeGenericMethod(type).Invoke(combat, new object[] { bot })!; }
            catch { skipped.Add(type.Name + "(create)"); continue; }
            bot.PlayerCombatState!.Hand.AddInternal(card);
            try
            {
                var branch = KernelSession.Capture(combat).Fork();
                if (!branch.CanPlay(card)) { skipped.Add($"{card.Id.Entry}:{card.Type}"); continue; }
                var target = branch.Targets(card).FirstOrDefault(t => t is not null);
                if (branch.Play(card, target, out var boundary)) { modeled.Add(card.Id.Entry); continue; }
                if (boundary.StartsWith("prediction-risk:", StringComparison.Ordinal)) unmodeled.Add(card.Id.Entry);
                else if (boundary.StartsWith("prediction-exception:", StringComparison.Ordinal)) broken.Add($"{card.Id.Entry}:{boundary}");
                else if (boundary == "pending-choice") choice.Add(card.Id.Entry);
                else skipped.Add($"{card.Id.Entry}:{boundary}");
            }
            catch (Exception error) { broken.Add($"{card.Id.Entry}:{error.GetType().Name}"); }
            finally { bot.PlayerCombatState!.Hand.RemoveInternal(card); }
        }
        var total = modeled.Count + unmodeled.Count + choice.Count + broken.Count + skipped.Count;
        Console.WriteLine($"CARD COVERAGE: total={total} modeled={modeled.Count} unmodeled={unmodeled.Count} "
            + $"choice={choice.Count} broken={broken.Count} skipped={skipped.Count}");
        Console.WriteLine("COVERAGE UNMODELED: " + string.Join(" ", unmodeled.OrderBy(n => n, StringComparer.Ordinal)));
        Console.WriteLine("COVERAGE CHOICE: " + string.Join(" ", choice.OrderBy(n => n, StringComparer.Ordinal)));
        Console.WriteLine("COVERAGE BROKEN: " + string.Join(" ", broken.OrderBy(n => n, StringComparer.Ordinal)));
        Console.WriteLine("COVERAGE SKIPPED: " + string.Join(" ", skipped.OrderBy(n => n, StringComparer.Ordinal)));
        if (unmodeled.Count == 0) throw new Exception("Coverage probe found nothing unmodeled; it is not probing.");
    }

    private static void Clear(Player p)
    {
        foreach (var c in p.PlayerCombatState!.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(c);
    }

    private static bool FormatText(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
    { __result = __instance.LocEntryKey; return false; }
}
