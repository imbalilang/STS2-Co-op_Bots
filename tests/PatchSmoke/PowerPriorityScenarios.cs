using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
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

internal static class PowerPriorityScenarios
{
    // R2 2026-09-28: independently reverted each production mechanism; all
    // exited 1, then source bytes were restored. work/power-investigation-20260928/r2-*.log:
    // System.Exception: Recurring Shivs in a long fight must retain more value than a single Strike.
    // System.Exception: A late-seat power must survive crowded tactical candidate pruning.
    // System.Exception: Fan of Knives must realize four native Shivs before preferring two Strikes.
    // System.Exception: Kill must apply Accuracy before either of two Shivs.
    // System.Exception: Sneaky upgrade must enter the mechanism comparison, not the unknown-power fallback.
    // System.Exception: Sneaky doubling three allies' attack-triggered block must beat a basic block upgrade.
    // System.Exception: Sneaky must not invent allied attack triggers from its owner's attacks in solo play.
    // System.Exception: Expose payoff must count only the twenty-seven actual HP damage freed for three allied Strikes.
    // Kill must strip 168 block and two Artifact before allied attacks.
    // Expose was tested with both its classification removed and its baseline
    // block overwritten by the boosted block; the latter catches false zero gain.
    internal static void Run()
    {
        new Harmony("coopbots.test.power-priority").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PowerPriorityScenarios), nameof(Format)));
        var failures = new List<string>();
        void Check(bool ok, string why) { if (!ok) failures.Add(why); }
        var (party, combat, enemy) = Board();
        var owner = party[3];
        var blades = combat.CreateCard<InfiniteBlades>(owner);
        owner.PlayerCombatState!.Hand.AddInternal(blades);
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(owner));
        // A real deck cycle makes delaying this engine cost several triggers.
        // The old two-card/empty-draw fixture redraws both next turn; playing
        // Strike first there earns 6 now and loses only one 4-damage Shiv.
        for (var i = 0; i < 8; i++)
            owner.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeSilent>(owner));
        owner.PlayerCombatState.LoseEnergy(2);
        var root = KernelSession.Capture(combat);
        var after = root.CardBranches(blades, null, 1).Single(b => b.Boundary.Length == 0).State;
        var retention = after.PersistentValue(owner, 10, 1) - root.PersistentValue(owner, 10, 1);
        var line = KernelRollout.Run(root.Fork(), party, party, new(RolloutPolicyKind.Tactical, MaxSteps: 1));
        Console.WriteLine($"PROBE infinite retention={retention:F2} tactical={line.Actions.FirstOrDefault()?.Card?.Id.Entry}");
        Check(retention > 6, "Recurring Shivs in a long fight must retain more value than a single Strike.");
        Check(line.Actions.FirstOrDefault()?.Card is InfiniteBlades,
            "Tactical continuation must deploy Infinite Blades before spending its last energy on Strike in a long fight.");

        // A correct state evaluator is useless if the card never reaches its eight probes.
        var (wide, wideCombat, wideEnemy) = Board();
        ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>().ToMutable().ApplyInternal(wideEnemy,9,true);
        var image = wideCombat.CreateCard<Afterimage>(wide[3]);
        image.UpgradeInternal();
        for (var i = 0; i < 8; i++)
            wide[3].PlayerCombatState!.DrawPile.AddInternal(wideCombat.CreateCard<StrikeSilent>(wide[3]));
        wide[3].PlayerCombatState!.Hand.AddInternal(image);
        foreach (var p in wide)
        {
            p.PlayerCombatState!.Hand.AddInternal(wideCombat.CreateCard<StrikeSilent>(p));
            p.PlayerCombatState.Hand.AddInternal(wideCombat.CreateCard<Anger>(p));
            p.PlayerCombatState.Hand.AddInternal(wideCombat.CreateCard<Shiv>(p));
        }
        var wideRoot = KernelSession.Capture(wideCombat);
        foreach (var candidate in new CardModel[]{ image, wide[0].PlayerCombatState!.Hand.Cards.OfType<Anger>().First() })
        {
            var target = wideRoot.Targets(candidate).First();
            var resolved = wideRoot.CardBranches(candidate,target,1).First(b=>b.Boundary.Length==0).State;
            Console.WriteLine($"PROBE wide value {candidate.Id.Entry}={KernelTacticalPolicy.ActionValue(wideRoot,resolved,wide,wide):F2} persistent={resolved.PersistentValue(wide[3],10,1):F2}");
        }
        var wideLine = KernelRollout.Run(wideRoot, wide, wide, new(RolloutPolicyKind.Tactical, MaxSteps: 1));
        Console.WriteLine($"PROBE wide tactical={wideLine.Actions.FirstOrDefault()?.Card?.Id.Entry}");
        Check(wideLine.Actions.FirstOrDefault()?.Card is Afterimage,
            "A late-seat power must survive crowded tactical candidate pruning.");

        var (fanParty, fanCombat, _) = Board();
        var fanOwner = fanParty[0];
        var fan = fanCombat.CreateCard<FanOfKnives>(fanOwner);
        fanOwner.PlayerCombatState!.Hand.AddInternal(fan);
        fanOwner.PlayerCombatState.Hand.AddInternal(fanCombat.CreateCard<StrikeSilent>(fanOwner));
        fanOwner.PlayerCombatState.Hand.AddInternal(fanCombat.CreateCard<StrikeSilent>(fanOwner));
        fanOwner.PlayerCombatState.LoseEnergy(1);
        var fanRoot = KernelSession.Capture(fanCombat);
        var fanLine = KernelRollout.Run(fanRoot, fanParty, fanParty, new(RolloutPolicyKind.Tactical, MaxSteps: 5));
        Console.WriteLine($"PROBE fan sequence={string.Join(',',fanLine.Actions.Select(a=>a.Card?.Id.Entry))}");
        Check(fanLine.Actions.FirstOrDefault()?.Card is FanOfKnives
            && fanLine.Actions.Count(a=>a.Card is Shiv)==4,
            "Fan of Knives must realize four native Shivs before preferring two Strikes.");
        fanCombat.Creatures.First(c=>c.Side==CombatSide.Enemy).SetCurrentHpInternal(14);
        var fanDecision = KernelTournament.Run(KernelSession.Capture(fanCombat), fanParty, fanParty,
            new TournamentOptions(IncludePotions:false, Rollout:new(MaxRounds:3)), 2000);
        Check(fanDecision.FirstAction?.Card is FanOfKnives && fanDecision.Record is {Verified:true,Victory:true},
            "Full tournament must select the native Fan of Knives lethal over a twelve-damage Strike opening.");

        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (team, board, foe) = Board();
            var p = team[0]; p.PlayerCombatState!.LoseEnergy(2);
            p.PlayerCombatState.Hand.AddInternal(board.CreateCard<Accuracy>(p));
            p.PlayerCombatState.Hand.AddInternal(board.CreateCard<Shiv>(p));
            p.PlayerCombatState.Hand.AddInternal(board.CreateCard<Shiv>(p));
            var state = KernelSession.Capture(board);
            var ordered = KernelRollout.Run(state.Fork(), team, team, new(policy, MaxSteps: 3));
            Console.WriteLine($"PROBE accuracy {policy}={string.Join(',',ordered.Actions.Select(a=>a.Card?.Id.Entry))}");
            Check(ordered.Actions.FirstOrDefault()?.Card is Accuracy, $"{policy} must apply Accuracy before either of two Shivs.");
            var replay = state.Fork();
            foreach (var action in ordered.Actions) Check(replay.ReplayPlanned(action, 80, out _), "Accuracy sequence must replay.");
            Check(replay.Hp(foe)==484, $"{policy} must realize sixteen damage from both boosted Shivs.");
            foe.SetCurrentHpInternal(4);
            var lethal = KernelRollout.Run(KernelSession.Capture(board), team, team, new(policy, MaxSteps: 1));
            Check(lethal.Actions.FirstOrDefault()?.Card is Shiv, $"{policy} must retain immediate lethal ahead of Accuracy.");
        }

        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (team, board, foe) = Board();
            foe.GainBlockInternal(168);
            ModelDb.Power<ArtifactPower>().ToMutable().ApplyInternal(foe, 2, true);
            var expose = board.CreateCard<Expose>(team[3]);
            team[3].PlayerCombatState!.Hand.AddInternal(expose);
            for (var seat = 0; seat < 3; seat++)
                team[seat].PlayerCombatState!.Hand.AddInternal(board.CreateCard<StrikeSilent>(team[seat]));
            var state = KernelSession.Capture(board);
            var stripped = state.CardBranches(expose, foe, 1).Single(b => b.Boundary.Length == 0).State;
            Check(stripped.Block(foe) == 0 && stripped.Power<ArtifactPower>(foe) == 0
                && stripped.Power<VulnerablePower>(foe) == 2,
                "Expose must remove all 168 block and both Artifact stacks before applying Vulnerable.");
            Check(KernelTeamCoordination.SetupPayoff(state, team, expose, foe) == 27,
                "Expose payoff must count only the twenty-seven actual HP damage freed for three allied Strikes.");
            var lineExpose = KernelRollout.Run(state.Fork(), team, team, new(policy, MaxSteps: 4));
            Console.WriteLine($"PROBE expose {policy}={string.Join(',', lineExpose.Actions.Select(a => a.Card?.Id.Entry))}");
            Check(lineExpose.Actions.FirstOrDefault()?.Card is Expose,
                $"{policy} must strip 168 block and two Artifact before allied attacks.");
            var replay = state.Fork();
            foreach (var action in lineExpose.Actions)
                Check(replay.ReplayPlanned(action, 80, out _), "Expose team sequence must replay.");
            Check(replay.Hp(foe) == 473, $"{policy} must deal all twenty-seven damage after Expose.");
            Check(KernelTournament.FirstActions(state, team, 1, new(IncludePotions:false))[0].Card is Expose,
                "Opening pruning must retain Expose ahead of damage into a shield.");
        }
        var (bareTeam, bareBoard, bareFoe) = Board();
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(bareFoe, 2, true);
        var redundantExpose = bareBoard.CreateCard<Expose>(bareTeam[3]);
        bareTeam[3].PlayerCombatState!.Hand.AddInternal(redundantExpose);
        bareTeam[0].PlayerCombatState!.Hand.AddInternal(bareBoard.CreateCard<StrikeSilent>(bareTeam[0]));
        Check(KernelTeamCoordination.SetupPayoff(KernelSession.Capture(bareBoard), bareTeam, redundantExpose, bareFoe) == 0,
            "Already exposed enemies must not produce duplicate shield-removal value.");

        var run = (RunState)owner.RunState!;
        foreach (var p in party)
            for (var i = 0; i < 5; i++)
            {
                p.Deck.AddInternal(run.CreateCard<StrikeSilent>(p));
                p.Deck.AddInternal(run.CreateCard<DefendSilent>(p));
            }
        var sneaky = run.CreateCard<Sneaky>(owner); owner.Deck.AddInternal(sneaky);
        var survivor = run.CreateCard<Survivor>(owner); owner.Deck.AddInternal(survivor);
        owner.Deck.AddInternal(run.CreateCard<InfiniteBlades>(owner));
        var sneakValue = BuildValue.UpgradeDelta(sneaky, owner);
        var defendValue = BuildValue.UpgradeDelta(owner.Deck.Cards.OfType<DefendSilent>().First(), owner);
        var survivorValue = BuildValue.UpgradeDelta(survivor, owner);
        Console.WriteLine($"PROBE camp sneaky={sneakValue.Total:F2} ({sneakValue.Reason}) defend={defendValue.Total:F2} survivor={survivorValue.Total:F2}");
        Check(sneakValue.Reason.Contains("cycle:") && !sneakValue.Reason.Contains("unknown:SNEAKY"),
            "Sneaky upgrade must enter the mechanism comparison, not the unknown-power fallback.");
        Check(sneakValue.Total > Math.Max(defendValue.Total, survivorValue.Total),
            "Sneaky doubling three allies' attack-triggered block must beat a basic block upgrade.");
        Check(BotBrain.SelectCards(owner, [survivor, sneaky, owner.Deck.Cards.OfType<DefendSilent>().First()],
            1, 1, "FromDeckForUpgrade").Single() == sneaky,
            "The actual camp upgrade chooser must select Sneaky in the four-player attack deck.");
        var noAllies = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro,34,0));
        var soloRun = RunState.CreateForTest([noAllies],seed:"SNEAKY-SOLO");
        var soloDeck = Enumerable.Range(0,10).Select(_=>(CardModel)soloRun.CreateCard<StrikeSilent>(noAllies)).ToArray();
        foreach (var card in soloDeck) noAllies.Deck.AddInternal(card);
        var soloSneaky = soloRun.CreateCard<Sneaky>(noAllies);
        var soloUpgraded = CardProfile.Upgraded(soloSneaky,3)!.Value.Card;
        var soloDelta = SustainedDeckValue.Compare(soloDeck.Append(soloSneaky).ToArray(),soloUpgraded,soloSneaky,3);
        Check(soloDelta is not null && Math.Abs(soloDelta.Delta)<.001,
            "Sneaky must not invent allied attack triggers from its owner's attacks in solo play.");
        // OWNER RULE 2026-10-05: when the enemy is ALREADY Vulnerable and the fight outlasts the
        // three-turn setup horizon (i.e. no win-in-three script was found), Cruelty's +25% applies
        // to every remaining attack and must outrank the ordinary plays instead of being priced as
        // a four-turn engine. The three cases below isolate BOTH gates — the Vulnerable gate and
        // the horizon gate — because dropping either one is the regression this rule exists for.
        double CrueltyRetention(int enemyHp, bool vulnerable)
        {
            var (cp, cc, enemy) = Board(enemyHp);
            if (vulnerable) ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(enemy, 5, true);
            foreach (var p in cp)
            {
                p.PlayerCombatState!.Hand.AddInternal(cc.CreateCard<Bash>(p));
                for (var i = 0; i < 5; i++) p.PlayerCombatState.Hand.AddInternal(cc.CreateCard<StrikeIronclad>(p));
                p.PlayerCombatState.Hand.AddInternal(cc.CreateCard<DefendIronclad>(p));
            }
            var cOwner = cp[3];
            var cruelty = cc.CreateCard<Cruelty>(cOwner);
            cOwner.PlayerCombatState!.Hand.AddInternal(cruelty);
            var cRoot = KernelSession.Capture(cc);
            var cAfter = cRoot.CardBranches(cruelty, null, 1).Single(b => b.Boundary.Length == 0).State;
            return cAfter.PersistentValue(cOwner, 10, 1) - cRoot.PersistentValue(cOwner, 10, 1);
        }
        var longVulnerable = CrueltyRetention(enemyHp: 1200, vulnerable: true);
        var longPlain = CrueltyRetention(enemyHp: 1200, vulnerable: false);
        var shortVulnerable = CrueltyRetention(enemyHp: 20, vulnerable: true);
        Console.WriteLine($"PROBE cruelty retention long+vuln={longVulnerable:F1} long-plain={longPlain:F1} short+vuln={shortVulnerable:F1}");
        Check(longVulnerable > longPlain * 3,
            $"Cruelty must dominate a long fight only while the enemy is ALREADY Vulnerable "
            + $"(vuln={longVulnerable:F1} plain={longPlain:F1}).");
        Check(longVulnerable > shortVulnerable * 3,
            $"Cruelty must be worth far more once the fight outlasts the setup horizon "
            + $"(long={longVulnerable:F1} short={shortVulnerable:F1}).");
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
        Console.WriteLine("PASS: recurring power value, late-seat candidate coverage and multiplayer Sneaky upgrades reach actual decisions.");
    }

    private static (Player[], CombatState, Creature) Board(int enemyHp = 500)
    {
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 33, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "POWER-PRIORITY"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(enemyHp); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat, enemy);
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
}
