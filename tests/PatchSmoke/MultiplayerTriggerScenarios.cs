using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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

internal static class MultiplayerTriggerScenarios
{
    internal static void Run()
    {
        var support = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var attacker = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 2));
        var party = new[] { support, attacker };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "TEAM-TRIGGERS"));
        foreach (var player in party) { player.ResetCombatState(); combat.AddPlayer(player); }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        var choose = typeof(BotBrain).Assembly.GetType("CoopBots.TeamCombatPlanner")!
            .GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Plan() => choose.Invoke(null, new object?[] { party, party, null });
        BotBrain.CombatMove? Move(object? plan) => plan is null ? null
            : (BotBrain.CombatMove)plan.GetType().GetProperty("Move")!.GetValue(plan)!;
        bool Lethal() => Move(Plan())?.Reason.Contains("confirmed-team-lethal") == true;
        double Metric(string key) { var plan = Plan(); return plan is null ? 0 : Convert.ToDouble(plan.GetType().GetProperty(key)!.GetValue(plan)); }
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Trigger regression: " + message); }
        void Reset(int hp = 100, int incoming = 0)
        {
            foreach (var player in party)
            {
                foreach (var card in player.PlayerCombatState!.Hand.Cards.ToList()) player.PlayerCombatState.Hand.RemoveInternal(card);
                foreach (var power in player.Creature.Powers.ToList()) power.RemoveInternal();
                player.Creature.LoseBlockInternal(player.Creature.Block);
                player.Creature.SetCurrentHpInternal(50);
                player.PlayerCombatState.Phase = PlayerTurnPhase.Play;
                player.PlayerCombatState.LoseEnergy(player.PlayerCombatState.Energy);
                player.PlayerCombatState.GainEnergy(3);
            }
            foreach (var power in enemy.Powers.ToList()) power.RemoveInternal();
            enemy.SetCurrentHpInternal(hp);
            enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(incoming)), true);
        }
        T Hand<T>(Player player) where T : CardModel
        {
            var card = combat.CreateCard<T>(player); player.PlayerCombatState!.Hand.AddInternal(card); return card;
        }
        Reset(31); Hand<TagTeam>(support); Hand<TwinStrike>(attacker);
        Check(Lethal() && Move(Plan())?.Card is TagTeam, "TagTeam must repeat the whole TwinStrike: 11 + 10 + 10.");
        Check(Metric("PlannedCards") == 2, "Replay pays for two hand cards, not three.");
        enemy.SetCurrentHpInternal(32); Check(!Lethal(), "Repeated TwinStrike must not exceed 31 combined damage.");
        Hand<TwinStrike>(attacker); enemy.SetCurrentHpInternal(42);
        Check(!Lethal(), "TagTeam is consumed once; it must not repeat both later cards.");
        Reset(31); Hand<TagTeam>(support); Hand<TwinStrike>(support);
        Check(!Lethal(), "TagTeam must exclude its applier.");
        Reset(31); Hand<TagTeam>(support); Hand<TwinStrike>(attacker);
        var artifact = ModelDb.Power<ArtifactPower>().ToMutable(); artifact.ApplyInternal(enemy, 1, true);
        Check(!Lethal(), "Artifact must prevent the new TagTeam instance.");

        Reset(20); Hand<TwinStrike>(attacker);
        var tag = ModelDb.Power<TagTeamPower>().ToMutable(); tag.Applier = support.Creature; tag.ApplyInternal(enemy, 1, true);
        Check(Lethal(), "A live TagTeam instance must be reflected in the plan.");
        Check(tag.Amount == 1 && enemy.Powers.Contains(tag), "Search cannot consume the live TagTeam instance.");

        Reset(24); Hand<DaggerSpray>(attacker);
        var secondEnemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "1");
        combat.AddCreature(secondEnemy); secondEnemy.Monster!.SetUpForCombat(); secondEnemy.SetCurrentHpInternal(24);
        secondEnemy.Monster.SetMoveImmediate(new MoveState("ZERO", _ => Task.CompletedTask, new SingleAttackIntent(0)), true);
        foreach (var target in new[] { enemy, secondEnemy })
        {
            var mark = ModelDb.Power<TagTeamPower>().ToMutable(); mark.Applier = support.Creature; mark.ApplyInternal(target, 1, true);
        }
        Check(Lethal(), "Two enemy TagTeam instances add two whole AOE replays: three times eight to both enemies.");
        secondEnemy.SetCurrentHpInternal(25); Check(!Lethal(), "AOE replay damage must not exceed 24 per target.");
        secondEnemy.SetCurrentHpInternal(0);

        Reset(31); Hand<TagTeam>(support); Hand<Bash>(attacker);
        Check(Lethal(), "Repeated Bash applies Vulnerable before its second play: 11 + 8 + 12.");
        enemy.SetCurrentHpInternal(32); Check(!Lethal(), "Repeated Bash must not apply its own Vulnerable before the first hit.");

        Reset(25); Hand<GangUp>(support); Hand<TwinStrike>(attacker);
        Check(Lethal() && Move(Plan())?.Card is TwinStrike, "GangUp counts both ally hits: 10 + 5 + 2*5.");
        enemy.SetCurrentHpInternal(26); Check(!Lethal(), "GangUp hit-history boundary is 25.");
        Reset(25); Hand<GangUp>(support); Hand<TwinStrike>(support);
        Check(!Lethal(), "GangUp excludes its owner's attacks.");
        Reset(29); Hand<GangUp>(support).UpgradeInternal(); Hand<TwinStrike>(attacker);
        Check(Lethal(), "Upgraded GangUp uses seven per allied hit.");

        Reset(16); Hand<OneForAll>(support); var zeroTwin = Hand<TwinStrike>(attacker); zeroTwin.SetToFreeThisTurn();
        Check(Lethal() && Move(Plan())?.Card is OneForAll, "OneForAll grants three damage to each zero-cost hit.");
        enemy.SetCurrentHpInternal(17); Check(!Lethal(), "OneForAll cannot inflate damage beyond 16.");
        Reset(16); Hand<OneForAll>(support); Hand<TwinStrike>(attacker);
        Check(!Lethal(), "OneForAll must not amplify a paid attack.");
        Reset(18); Hand<OneForAll>(support).UpgradeInternal(); Hand<TwinStrike>(attacker).SetToFreeThisTurn();
        Check(Lethal(), "Upgraded OneForAll grants four per zero-cost hit.");

        Reset(incoming: 5); support.Creature.SetCurrentHpInternal(5); Hand<Sneaky>(support); Hand<TwinStrike>(attacker);
        Check(Move(Plan())?.Card is Sneaky && Metric("DeathsPrevented") == 1,
            "Sneaky before an ally attack must provide the one block needed to survive.");
        Reset(incoming: 6); support.Creature.SetCurrentHpInternal(5); Hand<Sneaky>(support); Hand<TwinStrike>(attacker);
        Check(Metric("DeathsPrevented") == 0, "Sneaky triggers once per card, not twice for TwinStrike.");
        Reset(incoming: 5); support.Creature.SetCurrentHpInternal(5); Hand<Sneaky>(support); Hand<TwinStrike>(support);
        Check(Metric("DeathsPrevented") == 0, "Sneaky excludes its owner's card plays.");
        Reset(incoming: 6); support.Creature.SetCurrentHpInternal(5); Hand<Sneaky>(support); Hand<TwinStrike>(attacker);
        tag = ModelDb.Power<TagTeamPower>().ToMutable(); tag.Applier = support.Creature; tag.ApplyInternal(enemy, 1, true);
        Check(Metric("DeathsPrevented") == 1, "TagTeam's second card play triggers Sneaky again.");

        Reset(incoming: 6); attacker.Creature.SetCurrentHpInternal(5); Hand<BeaconOfHope>(support); Hand<DefendIronclad>(support);
        Check(Move(Plan())?.Card is BeaconOfHope && Metric("DeathsPrevented") == 1,
            "Beacon shares half of the later five block, saving the ally with two block.");
        var noBlock = ModelDb.Power<NoBlockPower>().ToMutable(); noBlock.ApplyInternal(attacker.Creature, 1, true);
        Check(Metric("DeathsPrevented") == 1, "NoBlock blocks card block, but permits Beacon's unpowered share.");
        Reset(incoming: 8); attacker.Creature.SetCurrentHpInternal(5); Hand<DefendIronclad>(support);
        foreach (var player in party) ModelDb.Power<BeaconOfHopePower>().ToMutable().ApplyInternal(player.Creature, 1, true);
        Check(Metric("DeathsPrevented") == 0, "Two Beacons must stop recursive sharing instead of inventing infinite block.");
        Check(party.All(player => player.Creature.Block == 0 && player.PlayerCombatState!.Energy == 3),
            "Projected triggers cannot change live block or energy.");
        Console.WriteLine("PASS: TagTeam repeat/consume/Artifact, GangUp allied hit history, OneForAll zero-cost checks, Sneaky card-play triggers, Beacon sharing and recursion guard.");
    }
}
