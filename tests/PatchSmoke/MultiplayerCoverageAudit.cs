using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Coverage observations plus regression assertions for repaired team projections.
internal static class MultiplayerCoverageAudit
{
    internal static void Run()
    {
        var support = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var attacker = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 2));
        var party = new[] { support, attacker };
        var run = RunState.CreateForTest(party, seed: "SHARED-BENEFIT-AUDIT");
        var combat = new CombatState(runState: run);
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetCurrentHpInternal(50);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate(new MoveState("ZERO", _ => Task.CompletedTask, new SingleAttackIntent(0)), true);
        var strategy = typeof(BotBrain).Assembly.GetType("CoopBots.GeniusCombatStrategy")!;
        var analyze = strategy.GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var card in new CardModel[] { combat.CreateCard<Flanking>(support), combat.CreateCard<Knockdown>(support) })
        {
            support.PlayerCombatState!.Hand.AddInternal(card);
            var facts = analyze.Invoke(null, new object[] { card, enemy, 3, 1 })!;
            Console.WriteLine($"AUDIT {card.GetType().Name}: " + string.Join(", ", facts.GetType().GetProperties()
                .Select(p => $"{p.Name}={p.GetValue(facts)}")));
            support.PlayerCombatState.Hand.RemoveInternal(card);
        }
        var bash = combat.CreateCard<Bash>(support); support.PlayerCombatState!.Hand.AddInternal(bash);
        var without = BotBrain.ChooseCombatMove(support, 0);
        var twin = combat.CreateCard<TwinStrike>(attacker); attacker.PlayerCombatState!.Hand.AddInternal(twin);
        var with = BotBrain.ChooseCombatMove(support, 0);
        Console.WriteLine($"AUDIT Bash individual score: no ally attacks={without?.Score}; ally TwinStrike={with?.Score}");
        var choose = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        var plan = choose.Invoke(null, new object?[] { party, party, null })!;
        var move = (BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!;
        Console.WriteLine($"AUDIT team order with Bash+TwinStrike, enemy HP100, incoming0: first={move.Card.GetType().Name}; {move.Reason}");
        support.PlayerCombatState.Hand.RemoveInternal(bash);
        var flanking = combat.CreateCard<Flanking>(support); support.PlayerCombatState.Hand.AddInternal(flanking);
        Console.WriteLine($"AUDIT Flanking individual choice while ally has TwinStrike: {BotBrain.ChooseCombatMove(support, 0)?.Card.GetType().Name ?? "none"}");
        void Clear()
        {
            foreach (var p in party)
                foreach (var c in p.PlayerCombatState!.Hand.Cards.ToList()) p.PlayerCombatState.Hand.RemoveInternal(c);
        }
        var names = "BeaconOfHope BelieveInYou Blaze BladeSymphony Cacophony Concoct Constellation Coordinate DemonicShield EnergySurge Fade Flanking GangUp GlimpseBeyond HammerTime Hibernate HuddleUp Ignition ImitationLearning Intercept Knockdown Largesse LegionOfBone Lift Midnight Mimic OneForAll Outrage Plot Rally Sneaky Soulbound TagTeam Tank TheBall Tutor Underworld".Split(' ');
        var factory = typeof(CombatState).GetMethods().Single(m => m.Name == "CreateCard" && m.IsGenericMethodDefinition);
        foreach (var name in names)
        {
            Clear();
            var type = typeof(CardModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Cards." + name)!;
            var card = (CardModel)factory.MakeGenericMethod(type).Invoke(combat, new object[] { support })!;
            support.PlayerCombatState.Hand.AddInternal(card);
            var target = card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy ? enemy
                : card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyAlly ? attacker.Creature : null;
            var facts = analyze.Invoke(null, new object?[] { card, target, 3, 1 })!;
            var values = facts.GetType().GetProperties().Where(p => Convert.ToDouble(p.GetValue(facts)) != 0)
                .Select(p => $"{p.Name}={p.GetValue(facts)}");
            Console.WriteLine($"AUDIT37 {name}: " + string.Join(", ", values));
        }
        object? PlanNow() => choose.Invoke(null, new object?[] { party, party, null });
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Multiplayer regression: " + message);
        }
        double Metric(object? selected, string name) => selected is null ? 0
            : Convert.ToDouble(selected.GetType().GetProperty(name)!.GetValue(selected));
        string First(object? selected) => selected is null ? "none"
            : ((BotBrain.CombatMove)selected.GetType().GetProperty("Move")!.GetValue(selected)!).Card.GetType().Name;
        bool Lethal(object? selected) => selected is not null
            && ((BotBrain.CombatMove)selected.GetType().GetProperty("Move")!.GetValue(selected)!).Reason.Contains("confirmed-team-lethal");
        string Describe(object? selected)
        {
            if (selected is null) return "none";
            var selectedMove = (BotBrain.CombatMove)selected.GetType().GetProperty("Move")!.GetValue(selected)!;
            return $"{selectedMove.Card.GetType().Name}, cards={selected.GetType().GetProperty("PlannedCards")!.GetValue(selected)}, savedHP={selected.GetType().GetProperty("HpSaved")!.GetValue(selected)}, preventedDeaths={selected.GetType().GetProperty("DeathsPrevented")!.GetValue(selected)}";
        }
        Clear();
        enemy.Monster.SetMoveImmediate(new MoveState("DANGER", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
        support.Creature.GainBlockInternal(20); attacker.Creature.SetCurrentHpInternal(5);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Rally>(support));
        Console.WriteLine("AUDIT SCENARIO Rally: caster block20, ally HP5/block0, incoming10 each; plan=" + Describe(PlanNow()));
        Check(Metric(PlanNow(), "DeathsPrevented") == 1, "Rally must save the uncovered ally.");
        Clear(); support.Creature.LoseBlockInternal(20); support.Creature.SetCurrentHpInternal(5);
        attacker.Creature.SetCurrentHpInternal(50); attacker.Creature.GainBlockInternal(20);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Mimic>(support));
        Console.WriteLine("AUDIT SCENARIO Mimic: caster HP5/block0, ally block20, incoming10 each; plan=" + Describe(PlanNow()));
        Check(Metric(PlanNow(), "DeathsPrevented") == 1, "Mimic must credit the caster's survival.");
        Clear(); support.Creature.SetCurrentHpInternal(50); attacker.Creature.LoseBlockInternal(20);
        enemy.Monster.SetMoveImmediate(new MoveState("ZERO", _ => Task.CompletedTask, new SingleAttackIntent(0)), true);
        attacker.PlayerCombatState.LoseEnergy(3);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<BelieveInYou>(support));
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Bash>(attacker));
        Console.WriteLine("AUDIT SCENARIO BelieveInYou: ally energy0 with Bash, safe board; plan=" + Describe(PlanNow()));
        Check(First(PlanNow()) == "BelieveInYou" && Metric(PlanNow(), "PlannedCards") == 2,
            "Transfer energy before the currently unaffordable attack.");
        Check(attacker.PlayerCombatState.Energy == 0, "Search must not mutate live resources.");
        Clear(); support.PlayerCombatState.GainStars(2); attacker.Creature.SetCurrentHpInternal(5);
        enemy.Monster.SetMoveImmediate(new MoveState("TWENTY", _ => Task.CompletedTask, new SingleAttackIntent(20)), true);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Constellation>(support));
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Constellation>(support));
        Console.WriteLine("AUDIT SCENARIO Constellation: caster stars2, two cards cost2 stars each, ally HP5 incoming20; plan=" + Describe(PlanNow()));
        Check(Metric(PlanNow(), "PlannedCards") == 1 && Metric(PlanNow(), "DeathsPrevented") == 0,
            "Never promise rescue using more stars than available.");
        Check(support.PlayerCombatState.Stars == 2, "Search must not spend live stars.");

        Clear(); attacker.Creature.SetCurrentHpInternal(50); attacker.PlayerCombatState.GainEnergy(3);
        enemy.Monster.SetMoveImmediate(new MoveState("ZERO", _ => Task.CompletedTask, new SingleAttackIntent(0)), true);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Flanking>(support));
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<TwinStrike>(attacker));
        enemy.SetCurrentHpInternal(20);
        Check(First(PlanNow()) == "Flanking" && Lethal(PlanNow()), "Flanking must double the other player's two hits.");
        var artifact = ModelDb.Power<ArtifactPower>().ToMutable(); artifact.ApplyInternal(enemy, 1, true);
        Check(!Lethal(PlanNow()), "Artifact must block Flanking's projected multiplier.");
        artifact.RemoveInternal();
        Clear(); support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Flanking>(support));
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<TwinStrike>(support));
        Check(!Lethal(PlanNow()), "Flanking must not amplify its own applier.");

        Clear(); support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Knockdown>(support));
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<TwinStrike>(attacker));
        enemy.SetCurrentHpInternal(30);
        Check(First(PlanNow()) == "Knockdown" && Lethal(PlanNow()), "Knockdown must amplify the following ally attack.");
        Clear(); var upgradedKnockdown = combat.CreateCard<Knockdown>(support); upgradedKnockdown.UpgradeInternal();
        support.PlayerCombatState.Hand.AddInternal(upgradedKnockdown);
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<TwinStrike>(attacker)); enemy.SetCurrentHpInternal(44);
        Check(Lethal(PlanNow()), "Upgraded Knockdown must use its actual multiplier of three.");

        Clear(); support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Bash>(support));
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<TwinStrike>(attacker));
        enemy.SetCurrentHpInternal(22);
        Check(First(PlanNow()) == "Bash" && Lethal(PlanNow()), "Vulnerable before ally hits must reach 22 damage.");
        enemy.SetCurrentHpInternal(23);
        Check(!Lethal(PlanNow()), "Round each Vulnerable hit down: 8 + 7 + 7, not 8 + 15.");
        artifact = ModelDb.Power<ArtifactPower>().ToMutable(); artifact.ApplyInternal(enemy, 1, true);
        enemy.SetCurrentHpInternal(22);
        Check(!Lethal(PlanNow()), "Artifact must block planned Vulnerable."); artifact.RemoveInternal();

        Clear(); support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Blaze>(support));
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<TwinStrike>(attacker)); enemy.SetCurrentHpInternal(20);
        Check(First(PlanNow()) == "Blaze" && Lethal(PlanNow()), "Planned ally Strength must affect both subsequent hits.");
        Check(!attacker.Creature.Powers.Any(p => p is StrengthPower), "Projected Strength must not leak into the game.");

        Clear(); enemy.SetCurrentHpInternal(100); attacker.Creature.SetCurrentHpInternal(5);
        enemy.Monster.SetMoveImmediate(new MoveState("TEN", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Fade>(support));
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(attacker));
        Check(First(PlanNow()) == "Fade" && Metric(PlanNow(), "DeathsPrevented") == 1,
            "Planned Dexterity must improve the recipient's subsequent Defend.");
        var noBlock = ModelDb.Power<NoBlockPower>().ToMutable(); noBlock.ApplyInternal(attacker.Creature, 1, true);
        Check(Metric(PlanNow(), "DeathsPrevented") == 0, "Dexterity cannot bypass NoBlock."); noBlock.RemoveInternal();

        Clear();
        enemy.Monster.SetMoveImmediate(new MoveState("NINE", _ => Task.CompletedTask, new SingleAttackIntent(9)), true);
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(support));
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DemonicShield>(support));
        Check(First(PlanNow()) == "DefendIronclad" && Metric(PlanNow(), "DeathsPrevented") == 1,
            "DemonicShield must copy block generated earlier in the plan.");

        Clear(); attacker.Creature.SetCurrentHpInternal(50);
        enemy.Monster.SetMoveImmediate(new MoveState("ZERO", _ => Task.CompletedTask, new SingleAttackIntent(0)), true);
        attacker.PlayerCombatState.LoseEnergy(3);
        attacker.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Bash>(attacker));
        Check(PlanNow() is null, "An unaffordable future candidate must never be returned as the first action.");
        support.PlayerCombatState.Hand.AddInternal(combat.CreateCard<EnergySurge>(support));
        Check(First(PlanNow()) == "EnergySurge" && Metric(PlanNow(), "PlannedCards") == 2,
            "Party energy gain must unlock the other player's hand.");
        Console.WriteLine("PASS: multiplayer resources, group/copy block, shared debuffs, per-hit rounding, Artifact, Strength/Dexterity chains and no live-state mutation.");
    }
}
