using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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

internal static class DynamicTargetScenarios
{
    // R2 V17: restoring the live target list and then the live team target
    // independently failed (exit 1); raw commands/output: work/strength-20260927-v17/r2-results.json.
    // System.Exception: Predicted shiv must use a null group target immediately after its enabling power.
    // System.Exception: Team vulnerability payoff must include every enemy hit by predicted group Shivs.
    private static bool Format(ref string __result) { __result=""; return false; }
    internal static void Run()
    {
        new Harmony("coopbots.dynamic.targets.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(DynamicTargetScenarios), nameof(Format)));
        var errors=new List<string>();
        foreach(var kind in new[]{"shiv","blade"})
        {
            var p=Player.CreateForNewRun<Deprived>(UnlockState.all,1);
            var combat=new CombatState(runState:RunState.CreateForTest([p],seed:"DYNAMIC-TARGET"));
            p.ResetCombatState();combat.AddPlayer(p);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(10);
            for(var i=0;i<2;i++)
            {
                var e=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,i.ToString());
                combat.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(100);e.SetCurrentHpInternal(100);
                e.Monster.SetMoveImmediate(new MoveState("ATTACK",_=>Task.CompletedTask,new SingleAttackIntent(4)),true);
                ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(e,2,true);
            }
            CardModel setup=kind=="shiv"?combat.CreateCard<FanOfKnives>(p):combat.CreateCard<SeekingEdge>(p);
            p.PlayerCombatState.Hand.AddInternal(setup);
            CardModel held=kind=="shiv"?combat.CreateCard<Shiv>(p):combat.CreateCard<SovereignBlade>(p);
            p.PlayerCombatState.Hand.AddInternal(held);
            var root=KernelSession.Capture(combat);var branch=root.Fork();
            if(root.Targets(held).Count!=2 || root.Targets(held).Any(t=>t is null))
                errors.Add("An untransformed attack must retain its single-enemy targets.");
            if(!branch.Play(setup,null,out var why))throw new Exception("Setup failed: "+why);
            var attack=branch.Hand(p).First(c=>kind=="shiv"?c is Shiv:c is SovereignBlade);
            var targets=branch.Targets(attack);
            Console.WriteLine($"PROBE {kind} targets={string.Join(',',targets.Select(t=>t?.CombatId.ToString()??"null"))} liveType={attack.TargetType}");
            if(targets.Count!=1 || targets[0] is not null)errors.Add($"Predicted {kind} must use a null group target immediately after its enabling power.");
            var start=branch.Enemies.ToDictionary(e=>e,branch.Hp);
            var played=branch.Fork();
            if(!played.Play(attack,null,out why))errors.Add($"Predicted group {kind} must accept null and resolve: {why}.");
            else if(start.Any(e=>played.Hp(e.Key)>=e.Value))errors.Add($"Predicted group {kind} must damage both enemies.");
            if(kind=="shiv")
            {
                var expected=branch.Hand(p).Count(c=>c is Shiv)*4;
                var actual=KernelTeamCoordination.VulnerableValue(branch,[p]);
                Console.WriteLine($"PROBE group vulnerable actual={actual} expected={expected}");
                if(actual!=expected)errors.Add("Team vulnerability payoff must include every enemy hit by predicted group Shivs.");
            }
            if(p.Creature.HasPower<FanOfKnivesPower>() || p.Creature.HasPower<SeekingEdgePower>() || !root.Hand(p).Contains(setup)
                || root.Targets(held).Count!=2 || root.Targets(held).Any(t=>t is null))
                errors.Add("Predicted target changes must leave the root and live powers untouched.");
        }
        if(errors.Count>0)throw new Exception(string.Join(Environment.NewLine,errors));
        Console.WriteLine("PASS: predicted target changes propagate through legal targets, actual group damage and team vulnerability value.");
    }
}
