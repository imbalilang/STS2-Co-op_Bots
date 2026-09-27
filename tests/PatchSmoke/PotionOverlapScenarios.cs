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
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class PotionOverlapScenarios
{
    // R2 2026-09-28: deliberate production faults exited 1; restored focused suites exited 0.
    // Commands and raw output: work/strength-20260928-v19/r2-results.json.
    // System.Exception: SHACKLING_POTION must receive zero immediate mitigation credit after planned Whistle; got 9.100000000000023.
    // System.Exception: A redundant one-turn Shackling Potion must be removed by replaying the same Whistle continuation.
    // System.Exception: A healthy party's modeled potion plan must survive adoption instead of replanning into stacked control.
    // System.Exception: The last-chance fallback must not spend mitigation already replaced by a planned stun.
    private static bool Format(ref string __result){__result="";return false;}
    internal static void Run()
    {
        new Harmony("coopbots.potion.overlap.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(PotionOverlapScenarios),nameof(Format)));
        foreach(var shrink in new[]{false,true})
        {
            var p=Player.CreateForNewRun<Deprived>(UnlockState.all,1);
            var run=RunState.CreateForTest([p],seed:"CONTROL-OVERLAP");var c=new CombatState(runState:run);
            p.ResetCombatState();c.AddPlayer(p);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
            var whistle=c.CreateCard<Whistle>(p);p.PlayerCombatState.Hand.AddInternal(whistle);
            var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");
            c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(500);e.SetCurrentHpInternal(500);
            e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,29,true);
            PotionModel potion=shrink?ModelDb.Potion<BeetleJuice>().ToMutable():ModelDb.Potion<ShacklingPotion>().ToMutable();
            p.AddPotionInternal(potion,silent:true);
            var root=KernelSession.Capture(c);
            var opening=new KernelTeamSearch.Action(p,null,shrink?e:null,potion);
            var before=root.Fork();if(!before.UsePotion(potion,opening.Target,out var why))throw new Exception(why);
            var played=before.CardBranches(whistle,e,1).First(b=>b.Boundary.Length==0);
            var card=new KernelTeamSearch.Action(p,whistle,e,Choices:played.Choices,ChoiceOrdinals:played.ChoiceOrdinals,
                CardStateKey:before.CardStateKey(whistle));
            var tail=KernelRollout.Run(played.State,[p],[p],new(MaxRounds:20));
            var script=new[]{opening,card}.Concat(tail.Actions).ToArray();
            var result=new TournamentResult(opening,tail.Record with {ResourcesConsumed=1},[],1,0,"fixture")
            {Script=script,ScriptAliveStates=script.Select(_=>new[]{p.NetId}).ToArray(),
                ScriptEnemyStates=script.Select(_=>new[]{e}).ToArray(),JointNodes=1};
            if(!tail.Terminal)throw new Exception("Control audit fixture must reach a native terminal: "+tail.StopReason);
            if(KernelPotionPolicy.ImmediateBenefit(root,[p],opening)<=0)
                throw new Exception("The control bottle must have raw immediate value before Whistle.");
            var marginal=KernelPotionPolicy.ImmediateBenefit(root,[p],opening,script);
            if(Math.Abs(marginal)>.01)throw new Exception($"{potion.Id.Entry} must receive zero immediate mitigation credit after planned Whistle; got {marginal}.");
            var audit=new KernelPotionPlanAudit(root,[p],result);
            while(!audit.Advance(TimeSpan.FromMilliseconds(1))){}
            if(!shrink && (!audit.Removed || audit.Result!.FirstAction?.Card is not Whistle || audit.Result.Script!.Any(a=>a.Potion==potion)))
                throw new Exception("A redundant one-turn Shackling Potion must be removed by replaying the same Whistle continuation.");
            if(shrink && audit.Removed)
                throw new Exception("Four-turn Shrink must be kept when its later turns improve the native ending despite immediate overlap.");
            var healthyForecast=result.Record! with {RoundsSurvived=8};
            var planner=new KernelCombatPlanner();
            AccessTools.Method(typeof(KernelCombatPlanner),"AdoptTournamentScript").Invoke(planner,[result with {Record=healthyForecast},c]);
            if(!planner.HasPendingStep)throw new Exception("A healthy party's modeled potion plan must survive adoption instead of replanning into stacked control.");
            p.Creature.SetCurrentHpInternal(1);var emergencyRoot=KernelSession.Capture(c);
            var emergency=KernelPotionOpportunity.Choose(emergencyRoot,[p],[p],healthyForecast,script.Skip(1).ToArray());
            if(emergency is not null)throw new Exception("The last-chance fallback must not spend mitigation already replaced by a planned stun.");
            if(root.Hp(p.Creature)!=80 || root.HeldPotionCount(p)!=1 || root.Energy(p)!=3)
                throw new Exception("Potion counterfactuals must not consume root HP, potions or energy.");
        }
        Console.WriteLine("PASS: control potions use marginal plan value, preserve useful later turns and keep healthy joint plans intact.");
    }
}
