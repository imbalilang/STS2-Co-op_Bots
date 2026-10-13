using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
internal static partial class NightDecisionScenarios {
 internal static void Combat(){
 Initialize();
 // Controlled local reconstruction of log 14252. No claim of full combat replay.
 var ps=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(100+i))).ToArray();
 var run=RunState.CreateForTest(ps,seed:"NIGHT-WAIL");var c=new CombatState(runState:run);c.RoundNumber=3;
 for(var i=0;i<4;i++){var p=ps[i];p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(i<2?1:0);p.Creature.SetMaxHpInternal(86);p.Creature.SetCurrentHpInternal(new[]{57,62,63,62}[i]);p.Creature.GainBlockInternal(i==0?10:5);}
 for(var i=0;i<2;i++){var e=c.CreateCreature(ModelDb.Monster<PhantasmalGardener>().ToMutable(),CombatSide.Enemy,i==0?"second":"fourth");c.AddCreature(e);e.Monster!.SetUpForCombat();e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States[i==0?"FLAIL_MOVE":"LASH_MOVE"],true);e.SetMaxHpInternal(140);e.SetCurrentHpInternal(i==0?95:109);if(i==0)e.GainBlockInternal(17);if(i==1)ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,3,true);}
 var p0=ps[0];var cards=new CardModel[]{c.CreateCard<PiercingWail>(p0),c.CreateCard<DefendSilent>(p0),c.CreateCard<Footwork>(p0)};foreach(var card in cards)p0.PlayerCombatState!.Hand.AddInternal(card);
 // Match the other living Silent's held cards from the immediately prior log.
 foreach(var x in new CardModel[]{ModelDb.Card<PreciseCut>(),ModelDb.Card<Sidestep>(),ModelDb.Card<AscendersBane>()})ps[1].PlayerCombatState!.Hand.AddInternal(c.CreateCard(x,ps[1]));
 var root=KernelSession.Capture(c);
 var cheap=new RolloutRun(root.Fork(),ps,ps,new(RolloutPolicyKind.Defend,MaxRounds:1){InvestmentLookahead=false,SequenceLookahead=false});
 while(!cheap.Done)cheap.Step();
 Check(cheap.Result!.Actions.First(a=>a.Player==p0).Card is PiercingWail, "cheap defence must see non-block team mitigation");
 var remaining=16;
 var ordered=KernelSequenceOrder.Before(root,ps,ps,new(p0,cards[1],null),new[]{new KernelTeamSearch.Action(p0,cards[0],null)},ref remaining);
 Check(ordered.Card is PiercingWail, "mutually exclusive last-energy actions must compare legal hand outcomes");
 Check(!RolloutRun.ShouldHold(root,ps,p0,cards[0]), "team pressure must justify Wail");
 foreach(var policy in new[]{RolloutPolicyKind.Defend,RolloutPolicyKind.Tactical}) {
   var rr=new RolloutRun(root.Fork(),ps,ps,new(policy,MaxRounds:1));while(!rr.Done)rr.Step();
   Console.WriteLine($"NIGHT policy={policy} actions="+string.Join(',',rr.Result!.Actions.Select(a=>$"{a.Player.NetId}:{a.Card?.Id.Entry??"END"}")));
   Check(rr.Result.Actions.First(a=>a.Player==p0).Card is PiercingWail, "last energy must protect three allies in "+policy);
 }
 var schedule=KernelJointTurnRun.Schedule(8,true);
 // 8 paths × 6 policies (Kill/KillAdds/Defend/Growth/Tactical/Cleanup)：KillAdds 是
 // 2026-10-05 女王爪牙标注加入的策略，所以 40 → 48；语义断言不变——Tactical 仍然拿到
 // 第二个受资助的续演（schedule[1]）。
 Check(schedule.Length==48 && schedule.Distinct().Count()==48 && schedule[1]==(0,RolloutPolicyKind.Tactical), "Tactical must get the second funded continuation");
 Check(CoopBots.KernelCombatPlanner.OpeningTournamentBudget(3200,true,1)==6400
   && CoopBots.KernelCombatPlanner.OpeningTournamentBudget(3200,true,2)==3200
   && CoopBots.KernelCombatPlanner.OpeningTournamentBudget(3200,false,1)==3200, "only boss opening gets twice the budget");
 // owner 2026-10-05：高价值场景允许更长搜索时间。普通不变；精英/普通 Boss 后续 1.5x；
 // 普通 Boss 开幕 2x；终幕 Boss 全回合 3x。
 Check(CoopBots.KernelCombatPlanner.HighValueTournamentBudget(3200,false,false,false,1)==3200
   && CoopBots.KernelCombatPlanner.HighValueTournamentBudget(3200,false,true,false,1)==4800
   && CoopBots.KernelCombatPlanner.HighValueTournamentBudget(3200,true,false,false,1)==6400
   && CoopBots.KernelCombatPlanner.HighValueTournamentBudget(3200,true,false,false,2)==4800
   && CoopBots.KernelCombatPlanner.HighValueTournamentBudget(3200,true,false,true,2)==9600, "high-value rooms get more thinking time");
 p0.Creature.GainBlockInternal(3); foreach(var p in ps.Skip(1))p.Creature.GainBlockInternal(5);
 var minor=KernelSession.Capture(c);
 Check(ps.Sum(p=>minor.Threat(p).HpLoss)==9 && !RolloutRun.ShouldHold(minor,ps,p0,cards[0]),
   "three small hits must count as meaningful total team pressure");
 Console.WriteLine("PASS: night defeat native team defence, tactical schedule and boss budget.");
 }
}
