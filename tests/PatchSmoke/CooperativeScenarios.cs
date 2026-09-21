using System.Reflection;
using CoopBots;
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
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

internal static class CooperativeScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var attacker = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var support = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 2));
        var party = new[] { human, attacker, support };
        var run = RunState.CreateForTest(party, seed: "COOP-REGRESSION");
        var combat = new CombatState(runState: run);
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy);
        enemy.Monster!.SetUpForCombat();
        enemy.Monster.SetMoveImmediate(new MoveState("DANGER", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
        human.Creature.SetCurrentHpInternal(5);
        attacker.Creature.SetCurrentHpInternal(50);
        support.Creature.SetCurrentHpInternal(50);
        support.Creature.GainBlockInternal(12);
        var shield = combat.CreateCard<DemonicShield>(support);
        support.PlayerCombatState!.Hand.AddInternal(shield);
        var shieldMove = BotBrain.ChooseCombatMove(support, 0);
        Check(shieldMove?.Target == human.Creature, "Shield must protect endangered human even when caster is safe.");
        support.PlayerCombatState.Hand.RemoveInternal(shield);

        var potion = ModelDb.Potion<BlockPotion>().ToMutable();
        support.AddPotionInternal(potion);
        var planner = typeof(BotBrain).Assembly.GetType("CoopBots.BotPotionPlanner")!;
        var evaluate = planner.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic)!;
        var choice = evaluate.Invoke(null, new object[] { new[] { support }, party });
        Check(choice is not null && ReferenceEquals(choice.GetType().GetProperty("Target")!.GetValue(choice), human.Creature),
            "Bot block potion must rescue human rather than healthy owner.");
        human.Creature.GainBlockInternal(10);
        Check(evaluate.Invoke(null, new object[] { new[] { support }, party }) is null,
            "Do not consume rescue potion once lethal damage is covered.");

        var noBlock = ModelDb.Power<NoBlockPower>().ToMutable();
        noBlock.ApplyInternal(human.Creature, 1, true);
        var lift = combat.CreateCard<Lift>(support);
        support.PlayerCombatState.Hand.AddInternal(lift);
        var blockFor = typeof(BotBrain).Assembly.GetType("CoopBots.CombatAssessment")!
            .GetMethod("BlockFor", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((double)blockFor.Invoke(null, new object?[] { lift, human.Creature, null })! == 0,
            "Ally-targeted block must honor the recipient's NoBlock, not the caster's preview.");
        noBlock.RemoveInternal();
        support.PlayerCombatState.Hand.RemoveInternal(lift);
        human.Creature.LoseBlockInternal(10);
        support.Creature.LoseBlockInternal(12);
        support.Creature.SetCurrentHpInternal(5);
        var intercept = combat.CreateCard<Intercept>(support);
        support.PlayerCombatState.Hand.AddInternal(intercept);
        Check(BotBrain.ChooseCombatMove(support, 0) is null, "Intercept must not kill its caster to grant fake ally block.");
        support.PlayerCombatState.Hand.RemoveInternal(intercept);
        support.Creature.SetCurrentHpInternal(50); support.Creature.GainBlockInternal(12);
        human.Creature.GainBlockInternal(10);

        var eventPatch = typeof(BotBrain).Assembly.GetType("CoopBots.HumanEventVotePatch");
        if (eventPatch is not null)
        {
            var invalidate = eventPatch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
            var votes = new List<uint?> { 0, 0, 0 };
            invalidate.Invoke(null, new object[] { human, 1u, 1u, votes });
            Check(votes[0] == 0 && votes[1] is null && votes[2] is null,
                "Human vote update must invalidate stale bot votes before resolution.");
            votes[1] = 1;
            invalidate.Invoke(null, new object[] { human, 0u, 1u, votes });
            Check(votes[1] == 1, "Stale event pages must not invalidate current votes.");
        }

        // The support bot has no follow-up attacks; its Bash must still value another bot's hand.
        var bash = combat.CreateCard<Bash>(support);
        support.PlayerCombatState.Hand.AddInternal(bash);
        var before = BotBrain.ChooseCombatMove(support, 0)!.Value.Score;
        for (var i = 0; i < 3; i++) attacker.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(attacker));
        var after = BotBrain.ChooseCombatMove(support, 0)!.Value.Score;
        Check(after > before, "Vulnerable setup must value other bots' affordable attacks.");
        support.PlayerCombatState.Hand.RemoveInternal(bash);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(support));
        attacker.PlayerCombatState!.LoseEnergy(attacker.PlayerCombatState.Energy - 1);
        support.PlayerCombatState.LoseEnergy(support.PlayerCombatState.Energy - 1);
        enemy.SetCurrentHpInternal(10);
        var teamPlanner = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner");
        if (teamPlanner is not null)
        {
            var choose = teamPlanner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
            string DecisionReason(object decision)
            {
                var move = decision.GetType().GetProperty("Move")!.GetValue(decision)!;
                return (string)move.GetType().GetProperty("Reason")!.GetValue(move)!;
            }
            double DecisionScore(object decision)
            {
                var move = decision.GetType().GetProperty("Move")!.GetValue(decision)!;
                return (double)move.GetType().GetProperty("Score")!.GetValue(move)!;
            }
            bool IsConfirmedLethal(object? decision) => decision is not null
                && DecisionReason(decision).Contains("confirmed-team-lethal", StringComparison.Ordinal);
            void ClearHand(Player player)
            {
                foreach (var card in player.PlayerCombatState!.Hand.Cards.ToList())
                    player.PlayerCombatState.Hand.RemoveInternal(card);
            }
            void SetEnergy(Player player, int amount)
            {
                var state = player.PlayerCombatState!;
                if (state.Energy < amount) state.GainEnergy(amount - state.Energy);
                else if (state.Energy > amount) state.LoseEnergy(state.Energy - amount);
            }

            var joint = choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null });
            Check(joint is not null && IsConfirmedLethal(joint)
                && (int)joint.GetType().GetProperty("PlannedCards")!.GetValue(joint)! == 2,
                "Joint lethal requires one attack from each bot and respects each bot's energy budget.");
            support.PlayerCombatState.LoseEnergy(1);
            Check(!IsConfirmedLethal(choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })),
                "Replanning must discard a confirmed lethal when another action consumes reserved energy.");
            attacker.PlayerCombatState.GainEnergy(1);
            enemy.SetCurrentHpInternal(15);
            var vigor = ModelDb.Power<VigorPower>().ToMutable();
            vigor.ApplyInternal(attacker.Creature, 2, true);
            Check(!IsConfirmedLethal(choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })),
                "One-use Vigor must not produce an unsafe confirmed lethal claim.");
            vigor.RemoveInternal();
            attacker.PlayerCombatState.LoseEnergy(2);
            enemy.SetCurrentHpInternal(10);
            var free = ModelDb.Power<FreeAttackPower>().ToMutable();
            free.ApplyInternal(attacker.Creature, 1, true);
            Check(!IsConfirmedLethal(choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })),
                "One free attack must not finance an unsafe confirmed lethal claim.");
            free.RemoveInternal();
            attacker.PlayerCombatState.GainEnergy(2);
            enemy.SetCurrentHpInternal(9);
            var weak = ModelDb.Power<WeakPower>().ToMutable();
            weak.ApplyInternal(attacker.Creature, 1, true);
            Check(!IsConfirmedLethal(choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })),
                "Two weakened Strikes deal 4+4, not 4.5+4.5; nine HP is not lethal.");
            weak.RemoveInternal();

            // Human cards affect setup and target allocation, but are never
            // inserted into the Bot's guaranteed damage or survival ledger.
            ClearHand(attacker); ClearHand(support); ClearHand(human);
            SetEnergy(attacker, 0); SetEnergy(support, 2); SetEnergy(human, 3);
            enemy.SetCurrentHpInternal(40);
            var setupBash = combat.CreateCard<Bash>(support);
            support.PlayerCombatState.Hand.AddInternal(setupBash);
            var beforeHumanHand = choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })!;
            for (var i = 0; i < 3; i++)
                human.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(human));
            var afterHumanHand = choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })!;
            Check(DecisionReason(afterHumanHand).Contains("human-hand-synergy", StringComparison.Ordinal)
                && DecisionScore(afterHumanHand) > DecisionScore(beforeHumanHand),
                "Visible human attacks must raise the value of a Bot Vulnerable setup without becoming guaranteed actions.");

            ClearHand(attacker); ClearHand(support); ClearHand(human);
            SetEnergy(attacker, 0); SetEnergy(support, 1); SetEnergy(human, 1);
            enemy.SetCurrentHpInternal(10);
            support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(support));
            human.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(human));
            Check(!IsConfirmedLethal(choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })),
                "Human attack potential must never turn an uncommitted 6+6 line into a confirmed Bot lethal.");

            ClearHand(attacker); ClearHand(support); ClearHand(human);
            SetEnergy(attacker, 0); SetEnergy(support, 3); SetEnergy(human, 0);
            human.Creature.LoseBlockInternal(human.Creature.Block);
            human.Creature.SetCurrentHpInternal(5);
            enemy.SetCurrentHpInternal(50);
            var teamShield = combat.CreateCard<DemonicShield>(support);
            support.PlayerCombatState.Hand.AddInternal(teamShield);
            var rescue = choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null })!;
            var rescueMove = rescue.GetType().GetProperty("Move")!.GetValue(rescue)!;
            Check(ReferenceEquals(rescueMove.GetType().GetProperty("Target")!.GetValue(rescueMove), human.Creature)
                && DecisionReason(rescue).Contains("team-survival", StringComparison.Ordinal),
                "Team final ordering must prevent a human death before optimizing damage or setup.");

            ClearHand(attacker); ClearHand(support); ClearHand(human);
            SetEnergy(attacker, 3); SetEnergy(support, 3); SetEnergy(human, 3);
            human.Creature.SetCurrentHpInternal(50);
            human.Creature.GainBlockInternal(10);
            enemy.SetCurrentHpInternal(120);
            foreach (var bot in new[] { attacker, support })
            {
                for (var i = 0; i < 5; i++)
                    bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
                for (var i = 0; i < 3; i++)
                    bot.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<DefendIronclad>(bot));
            }
            for (var i = 0; i < 5; i++)
                human.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(human));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var bounded = choose.Invoke(null, new object?[] { new[] { attacker, support }, party, null });
            timer.Stop();
            Check(bounded is not null && timer.ElapsedMilliseconds < 2000,
                $"Bounded team search exceeded the regression budget: {timer.ElapsedMilliseconds} ms.");

            Console.WriteLine($"PASS: team Beam survival, joint lethal, human-hand soft potential, hard-claim boundaries, bounded search ({timer.ElapsedMilliseconds} ms).");

            var advisor = typeof(BotBrain).Assembly.GetType("CoopBots.HumanCoopAdvisor")!;
            var openingMethod = advisor.GetMethod("Opening", BindingFlags.Static | BindingFlags.NonPublic)!;
            BotBrain.CombatMove? Opening() => (BotBrain.CombatMove?)openingMethod.Invoke(null, new object[] { support, party });
            ClearHand(attacker); ClearHand(support); ClearHand(human);
            SetEnergy(support, 3); SetEnergy(human, 3);
            support.Creature.SetCurrentHpInternal(50);
            enemy.SetCurrentHpInternal(100);
            var openingBash = combat.CreateCard<Bash>(support);
            support.PlayerCombatState!.Hand.AddInternal(openingBash);
            Check(Opening() is null, "Vulnerable must wait if no teammate has an affordable follow-up.");
            human.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(human));
            Check(Opening()?.Card == openingBash, "Bot Bash may open before human release when human can exploit Vulnerable.");
            var vulnerability = ModelDb.Power<VulnerablePower>().ToMutable();
            vulnerability.ApplyInternal(enemy, 2, true);
            Check(Opening() is null, "Replan must stop repeating setup after Vulnerable is applied.");
            vulnerability.RemoveInternal();
            SetEnergy(human, 0);
            Check(Opening() is null, "Human energy consumption must invalidate stale follow-up potential.");
            SetEnergy(human, 3);
            var artifact = ModelDb.Power<ArtifactPower>().ToMutable();
            artifact.ApplyInternal(enemy, 1, true);
            Check(Opening() is null, "Artifact prevents an assumed immediate status benefit.");
            artifact.RemoveInternal();
            enemy.SetCurrentHpInternal(1);
            Check(Opening() is null, "Setup exception must not steal a lethal attack.");
            enemy.SetCurrentHpInternal(100);
            ClearHand(support); ClearHand(human);
            support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(support));
            Check(Opening() is null, "Ordinary attacks must never bypass human release.");
            ClearHand(support);
            var neutralize = combat.CreateCard<Neutralize>(support);
            support.PlayerCombatState.Hand.AddInternal(neutralize);
            human.Creature.LoseBlockInternal(human.Creature.Block);
            Check(Opening()?.Card == neutralize, "Weak opener must reduce current team incoming damage without human attacks.");
            var weakness = ModelDb.Power<WeakPower>().ToMutable();
            weakness.ApplyInternal(enemy, 2, true);
            Check(Opening() is null, "Already-weakened target must not trigger another early Weak card.");
            weakness.RemoveInternal();
            ClearHand(support);
            var shockwave = combat.CreateCard<Shockwave>(support);
            support.PlayerCombatState.Hand.AddInternal(shockwave);
            Check(Opening()?.Card == shockwave, "AOE Shockwave generic Power variable must count as cooperative setup.");
            var adviceMethod = advisor.GetMethod("CombatAdvice", BindingFlags.Static | BindingFlags.NonPublic)!;
            human.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Bash>(human));
            support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(support));
            var advice = (BotBrain.CombatMove?)adviceMethod.Invoke(null, new object[] { human, party });
            Check(advice?.Card.Owner == human && advice?.Target == enemy, "Human advice must name a legal human card and target.");
            SetEnergy(human, 0);
            Check(adviceMethod.Invoke(null, new object[] { human, party }) is null, "Advice must be recomputed when human cannot afford the suggested card.");
            var routeMethod = advisor.GetMethod("RouteValue", BindingFlags.Static | BindingFlags.NonPublic)!;
            double Route(MegaCrit.Sts2.Core.Map.MapPointType type) => (double)routeMethod.Invoke(null, new object[] { type, .3, 200 })!;
            Check(Route(MegaCrit.Sts2.Core.Map.MapPointType.RestSite) > Route(MegaCrit.Sts2.Core.Map.MapPointType.Elite),
                "Route advice must favor recovery over an elite at low team health.");
            var cardValue = advisor.GetMethod("CardValue", BindingFlags.Static | BindingFlags.NonPublic)!;
            var defendValue = ((double Score, string Reason))cardValue.Invoke(null, new object[] { combat.CreateCard<DefendIronclad>(human), human })!;
            var strikeValue = ((double Score, string Reason))cardValue.Invoke(null, new object[] { combat.CreateCard<StrikeIronclad>(human), human })!;
            Check(defendValue.Score > strikeValue.Score, "An empty defense package should prioritize a basic block option over another basic attack.");
            var relicValue = advisor.GetMethod("RelicValue", BindingFlags.Static | BindingFlags.NonPublic)!;
            var anchor = ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Anchor>().ToMutable();
            human.Creature.SetCurrentHpInternal(5);
            var injured = ((double Score, string Reason))relicValue.Invoke(null, new object[] { anchor, human })!;
            human.Creature.SetCurrentHpInternal(human.Creature.MaxHp);
            var healthy = ((double Score, string Reason))relicValue.Invoke(null, new object[] { anchor, human })!;
            Check(injured.Score > healthy.Score, "Relic advice must react to the human's current survival pressure.");
            Console.WriteLine("PASS: cooperative opening, no duplicate debuffs, human hand/energy replanning, Artifact, lethal boundary, ordinary-attack exclusion, Weak, AOE setup, human advice and recovery route.");
        }
        Console.WriteLine("PASS: real game-model scenarios: human shield, rescue potion, potion conservation, cross-bot Vulnerable.");
        AuditTeamRelicValue();
        DeckValueScenarios.Run();
        RouteScenarios.Run();
        RestSiteScenarios.Run();
        ReconnectScenarios.Run(run);
        MultiHumanScenarios.Run();
        CoordinatorScenarios.Run();
        MultiplayerCoverageAudit.Run();
        MultiplayerTriggerScenarios.Run();
        TabletEventScenarios.Run();
        ShopScenarios.Run();
        LastStandScenarios.Run();
        PotionValueScenarios.Run();
        StrengthScenarios.Run();
        FocusScenarios.Run();
        ResourceScenarios.Run();
        TurnBoundaryScenarios.Run();
        MonsterHazardScenarios.Run();
        ImportedCardRelicScenarios.Run();
#if KERNEL_TESTS
        KernelEngineScenarios.Run();
        KernelRolloutScenarios.Run();
        KernelTerminalScenarios.Run();
        KernelCommutationScenarios.Run();
        KernelTwoPhaseBossScenarios.Run();
        ForeseerScenarios.Run();
#endif
        PlannerPerformanceScenarios.Run();
    }
    private static void Check(bool condition, string failure) { if (!condition) throw new Exception(failure); }

    // Enemy-buff relics trade a personal upside for a team-wide downside, so
    // their value must drop as the party grows; team-wide buffs must rise.
    private static void AuditTeamRelicValue()
    {
        double Score(RelicModel relic, int allies)
        {
            var players = new List<Player>
            {
                Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 5, allies)),
            };
            for (var i = 1; i < allies; i++)
                players.Add(Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(700 + i)));
            RunState.CreateForTest(players.ToArray(), seed: $"RELIC-{relic.Id.Entry}-{allies}");
            return HumanCoopAdvisor.RelicValue(relic.ToMutable(), players[0]).Score;
        }
        var brimstone = ModelDb.Relic<Brimstone>();
        var stone = ModelDb.Relic<PhilosophersStone>();
        var redMask = ModelDb.Relic<RedMask>();
        Check(Score(brimstone, 3) < Score(brimstone, 1),
            "Brimstone's enemy Strength hurts every member, so more allies must lower its value.");
        Check(Score(stone, 3) < Score(stone, 1),
            "PhilosophersStone's enemy Strength hurts every member, so more allies must lower its value.");
        Check(Score(redMask, 3) > Score(redMask, 1),
            "A team-wide relic (RedMask) must gain value with more members.");
        Console.WriteLine("PASS: relic valuation is team-aware: enemy-buff relics lose value as the party grows, team-wide relics gain.");
    }
}



