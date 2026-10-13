// R2 2026-10-09: legacy fixture authority/request wiring; existing business assertions retained.
// Physical proof: work/architecture-refactor-20261009/r2/legacy-script-contract-proof-v1/proof.json
// Each listed old mechanism: baseline build/run 0/0 -> mutant 0/1 -> exact-byte restoration 0/0.
// Runtime fixture body below equals that compiled proof epoch; these lines are comment-only.
// Actual RED: System.Exception: Winning plan: a winning planned end-turn must survive the useful-attack heuristic
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2 2026-10-01: each fault was injected into production, observed red, then restored.
// Commands and full traces: work/ancient-options-20261001/r2-*.json / r2-*.log.
// winning-end-turn -> System.Exception: Winning plan: a winning planned end-turn must survive the useful-attack heuristic
// losing-rescue -> System.Exception: Winning plan: a losing continuation must retain last-chance potion spending
// live-potion-dispatch -> System.Exception: Winning plan: live potion dispatch must leave the winning tail intact
// winning-potion -> System.Exception: Winning plan: a predicted teammate death must not override a verified team victory
// winning-fullbelt -> System.Exception: Winning plan: a full belt must not override a verified victory either
// R2 2026-10-02, production faults restored:
// System.Exception: Winning plan: full belt must dispatch Radiant natively even with a winning tail
// System.Exception: Winning plan: a predicted teammate death must not override a verified team victory
// System.Exception: Winning plan: unknown potion dispatch must clear the old script before replanning
internal static class WinningPlanScenarios
{
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Winning plan: " + why); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        new Harmony("coopbots.test.winning-plan.format").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(WinningPlanScenarios), nameof(Format)));
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, 2);
        var run = RunState.CreateForTest([p, ally], seed: "PRESERVE-WINNING-PLAN");
        var combat = new CombatState(runState: run);
        foreach (var player in new[] { p, ally })
        {
            player.ResetCombatState(); combat.AddPlayer(player);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(player == p ? 10 : 80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            player.PlayerCombatState.GainEnergy(3);
        }
        var strike = combat.CreateCard<StrikeIronclad>(p); p.PlayerCombatState!.Hand.AddInternal(strike);
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        var vulnerable = ModelDb.Potion<VulnerablePotion>().ToMutable(); p.AddPotionInternal(vulnerable, silent: true);
        var root = KernelSession.Capture(combat);
        // The terminal is deliberately supplied: this is a scheduling regression,
        // not a claim that this tiny board reproduces the native Ovicopter battle.
        var win = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [0, 30], 1, 0, RoundsSurvived: 10);
        var end = new KernelTeamSearch.Action(p, null, null, EndTurn: true);
        Check(KernelPotionOpportunity.Choose(root, [p, ally], [p, ally], win) is not null,
            "fixture must expose the profitable death-triggered bottle without a remaining plan");
        Check(KernelPotionOpportunity.Choose(root, [p, ally], [p, ally], win, [end]) is null,
            "a predicted teammate death must not override a verified team victory");
        Check(KernelPotionOpportunity.Choose(root, [p, ally], [p, ally], win with { Victory = false }, [end]) is not null,
            "a losing continuation must retain last-chance potion spending");
        // The 2026-10-02 unknown-value exception is tested below; retain this guard for modeled full-dose bottles.
        p.DiscardPotionInternal(vulnerable, silent: true);
        while (p.Potions.Count() < p.MaxPotionCount) p.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), silent: true);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(combat), [p, ally], [p, ally], win with { PostCombatHp = [10, 30] }, [end]) is null,
            "a full belt must not override a verified victory either");

        var opening = new KernelTeamSearch.Action(p, strike, enemy);
        var script = new[] { opening, end };
        var result = new TournamentResult(opening, win, [], 1, 0, "fixture") {
            Script = script, ScriptAliveStates = script.Select(_ => new[] { p.NetId, ally.NetId }).ToArray(),
            ScriptEnemyStates = script.Select(_ => new[] { enemy }).ToArray(), JointNodes = 1 };
        var planner = new KernelCombatPlanner();
        AccessTools.Method(typeof(KernelCombatPlanner), "AdoptTournamentScript").Invoke(planner, [result, combat]);
        var offered = (bool)AccessTools.Method(typeof(KernelCombatPlanner), "TryPotionOpportunity")
            .Invoke(planner, [combat, new[] { p, ally }, win, new[] { end }])!;
        Check(!offered && planner.HasPendingStep, "live potion dispatch must leave the winning tail intact");
        object?[] args = [combat, new[] { p, ally }, true, null];
        // This scheduling oracle is a supplied pure-bot tail. A real human
        // proposal requires separate actual-root replay evidence.
        var controlled=new[]{p,ally};
        var previousControl=controlled.Select(player=>AutoPilot.IsAutopiloted(player.NetId)).ToArray();
        foreach(var player in controlled) AutoPilot.Set(player.NetId,true);
        bool emitted;
        try { emitted=(bool)AccessTools.Method(typeof(KernelCombatPlanner), "TryEmitFromTournamentScript").Invoke(planner,args)!; }
        finally { for(var i=0;i<controlled.Length;i++) AutoPilot.Set(controlled[i].NetId,previousControl[i]); }
        Check(emitted && planner.ConfirmedEndTurn == p,
            "a winning planned end-turn must survive the useful-attack heuristic");
        foreach (var bottle in p.Potions.ToArray()) p.DiscardPotionInternal(bottle, silent: true);
        var radiant = ModelDb.Potion<RadiantTincture>().ToMutable();
        p.AddPotionInternal(radiant, silent: true);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(combat), [p], [p, ally], null) is null,
            "unknown full-belt value must not spend a non-full belt");
        while (p.Potions.Count() < p.MaxPotionCount) p.AddPotionInternal(ModelDb.Potion<FairyInABottle>().ToMutable(), silent: true);
        AccessTools.Method(typeof(KernelCombatPlanner), "AdoptTournamentScript").Invoke(planner, [result, combat]);
        offered = (bool)AccessTools.Method(typeof(KernelCombatPlanner), "TryPotionOpportunity")
            .Invoke(planner, [combat, new[] { p }, win, new[] { end }])!;
        Check(offered && planner.ConfirmedPotion?.Potion == radiant && planner.ConfirmedPotion.Target == p.Creature,
            "full belt must dispatch Radiant natively even with a winning tail");
        Check(!planner.HasPendingStep, "unknown potion dispatch must clear the old script before replanning");
        p.DiscardPotionInternal(radiant, silent: true);
        var chooser = ModelDb.Potion<GamblersBrew>().ToMutable(); p.AddPotionInternal(chooser, silent: true);
        Check(!KernelSession.CanModelPotion(chooser)
            && KernelPotionOpportunity.Choose(KernelSession.Capture(combat), [p], [p, ally], win, [end])?.Action.Potion == chooser,
            "full belt must allow an unmodeled chooser potion");
        p.DiscardPotionInternal(chooser, silent: true);
        p.AddPotionInternal(ModelDb.Potion<FairyInABottle>().ToMutable(), silent: true);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(combat), [p], [p, ally], win, [end]) is null,
            "automatic Fairy must never be manually consumed");
        Console.WriteLine("PASS: full belts dispatch unknown-valued/chooser bottles, clear the old live plan, preserve automatic Fairy and non-full belts.");
        Console.WriteLine("PASS: verified winning scripts survive emergency/full-belt potion and spare-attack heuristics; losing plans retain rescue spending.");
    }
}
