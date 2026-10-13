// R2 2026-10-09: legacy fixture authority/request wiring; existing business assertions retained.
// Physical proof: work/architecture-refactor-20261009/r2/legacy-script-contract-proof-v1/proof.json
// Each listed old mechanism: baseline build/run 0/0 -> mutant 0/1 -> exact-byte restoration 0/0.
// Runtime fixture body below equals that compiled proof epoch; these lines are comment-only.
// Actual RED: System.Exception: Act2 priority: runtime must replace a long winning script with the better verified lethal
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
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
// R2 2026-10-04: 13 decision faults and a canonical-preview fault failed, then restored.
// MegaCrit.Sts2.Core.Models.Exceptions.CanonicalModelException: Canonical model of type MegaCrit.Sts2.Core.Models.Cards.BelieveInYou used in incorrect place.
// Commands and raw failures: work/act2-priority-20261004/r2-{results,extra-results}.json
// System.Exception: Act2 priority: minions must not count as primary victory progress
// System.Exception: Act2 priority: main damage must beat non-disabling minion chip damage
// System.Exception: Act2 priority: short comparison must recover Obscura R8 lethal
// System.Exception: Act2 priority: MindBlast must deal remaining draw-pile damage on play
// System.Exception: Act2 priority: ally energy must never refund caster energy
// System.Exception: Act2 priority: ally energy must fund additional affordable ally plays
// System.Exception: Act2 priority: forced choice must take best negative card while optional reward may skip
// System.Exception: Act2 priority: brain leech alternatives must become one mandatory choice
// System.Exception: Act2 priority: spawn identity must resolve by unique combat id and model
// System.Exception: Act2 priority: dead seat must not reset next-turn energy
// System.Exception: Act2 priority: invalid suffix must still permit verified search from the frozen boundary
// System.Exception: Act2 priority: runtime must replace a long winning script with the better verified lethal
// System.Exception: Act2 priority: defence policy must compare native investment continuations
internal static class ActTwoPriorityScenarios {
 static void Check(bool ok,string message) { if(!ok) throw new Exception("Act2 priority: "+message); }
 internal static void Run(){
 NightDecisionScenarios.Initialize();
 Construction();
 SurvivalAndReplay();
 var p=Player.CreateForNewRun<Deprived>(UnlockState.all,1);
 var c=new CombatState(runState:RunState.CreateForTest([p],seed:"OBSCURA-R8"));p.ResetCombatState();c.AddPlayer(p);
 p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
 p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(6);ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(p.Creature,2,true);
 var boss=c.CreateCreature(ModelDb.Monster<TheObscura>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(boss);boss.Monster!.SetUpForCombat();boss.Monster.SetMoveImmediate((MoveState)boss.Monster.MoveStateMachine!.States["PIERCING_GAZE_MOVE"],true);boss.SetMaxHpInternal(610);boss.SetCurrentHpInternal(18);boss.GainBlockInternal(33);
 ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(boss,1,true);
 var minion=c.CreateCreature(ModelDb.Monster<Parafright>().ToMutable(),CombatSide.Enemy,"illusion");c.AddCreature(minion);minion.Monster!.SetUpForCombat();minion.Monster.SetMoveImmediate((MoveState)minion.Monster.MoveStateMachine!.States["SLAM_MOVE"],true);minion.SetMaxHpInternal(100);minion.SetCurrentHpInternal(100);
 ModelDb.Power<IllusionPower>().ToMutable().ApplyInternal(minion,1,true);ModelDb.Power<MinionPower>().ToMutable().ApplyInternal(minion,1,true);
 var bash=c.CreateCard<Bash>(p);bash.UpgradeInternal();var a=c.CreateCard<Anger>(p);var b=c.CreateCard<Anger>(p);var strike=c.CreateCard<StrikeIronclad>(p);
 foreach(var card in new CardModel[]{bash,a,b,strike,c.CreateCard<Tremble>(p)})p.PlayerCombatState.Hand.AddInternal(card);
 var root=KernelSession.Capture(c);
 Check(root.RemainingOpposition().Count==1 && root.RemainingOpposition().Hp==18,"minions must not count as primary victory progress");
 var main=root.CardBranches(bash,boss,1).Single(x=>x.Boundary.Length==0).State;
 var decoy=root.CardBranches(bash,minion,1).Single(x=>x.Boundary.Length==0).State;
 Check(KernelTacticalPolicy.Value(main,new[]{p})>KernelTacticalPolicy.Value(decoy,new[]{p}),"main damage must beat non-disabling minion chip damage");
 Check(KernelShortVictoryRun.WorthChecking(root,new[]{p}),"visible Obscura lethal must trigger the bounded comparison");
 var search=new KernelShortVictoryRun(root,new[]{p});while(!search.Done)search.Advance(TimeSpan.FromMilliseconds(2));
 Check(search.Result?.Record is {Victory:true},"short comparison must recover Obscura R8 lethal");
 Check(search.Result!.Script!.All(a=>a.Target==boss),"Obscura lethal must concentrate all attacks on the primary enemy");
 var planner=new CoopBots.KernelCombatPlanner();
 void Set(string name,object value)=>HarmonyLib.AccessTools.Field(planner.GetType(),name).SetValue(planner,value);
 var old=new KernelTeamSearch.Action[]{new(p,bash,minion,CardStateKey:root.CardStateKey(bash))};
 Set("tournamentScript",old);Set("tournamentScriptCombat",c);Set("tournamentScriptIndex",0);Set("tournamentScriptRound",c.RoundNumber);
 Set("tournamentScriptAliveStates",new[]{new[]{p.NetId}});Set("tournamentScriptEnemyStates",new[]{root.Enemies.ToArray()});
 Set("tournamentScriptRecord",TerminalRecord.Capture(root,new[]{p},true) with { Victory=true,PostCombatHp=new[]{1} });
 // Match production adoption in this same round. Cross-round withdrawal is
 // separately exercised by DecisionConsistencyScenarios.Victory.
 Set("victoryCheckedRound",c.RoundNumber);
 object?[] emitArgs={c,new[]{p},true,null};bool emitted=false;
 // This supplied script tests the pure-bot runtime, not a human proposal.
 var wasDriven=CoopBots.AutoPilot.IsAutopiloted(p.NetId);CoopBots.AutoPilot.Set(p.NetId,true);
 try {
 for(var n=0;n<1000 && !emitted;n++) emitted=(bool)HarmonyLib.AccessTools.Method(planner.GetType(),"TryEmitFromTournamentScript").Invoke(planner,emitArgs)!;
 } finally { CoopBots.AutoPilot.Set(p.NetId,wasDriven); }
 Check(emitted && ((CoopBots.TeamCombatPlanner.Decision)emitArgs[3]!).Move.Target==boss,"runtime must replace a long winning script with the better verified lethal");
 Console.WriteLine("PASS: primary-enemy progress and replayed execution-time Obscura lethal.");
 }
 static void Construction(){
 var p=Player.CreateForNewRun<Deprived>(UnlockState.all,11);
 var q=Player.CreateForNewRun<Deprived>(UnlockState.all,12);
 RunState.CreateForTest([p,q],seed:"ALLY-ENERGY");
 p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);q.Creature.SetMaxHpInternal(80);q.Creature.SetCurrentHpInternal(80);p.MaxEnergy=q.MaxEnergy=3;
 var canonicalGift=CoopBots.Building.CardProfile.Of(ModelDb.Card<BelieveInYou>());
 Check(canonicalGift.Energy==0 && canonicalGift.AlliedEnergyValue==0,"canonical ally preview must not require a live owner");
 var gift=ModelDb.Card<BelieveInYou>().ToMutable();gift.Owner=p;
 Check(CoopBots.Building.CardProfile.Of(gift).Energy==0,"ally energy must never refund caster energy");
 var blast=ModelDb.Card<MindBlast>().ToMutable();blast.Owner=p;
 var fact=CoopBots.Building.CardProfile.Of(blast);
 Check(fact.DrawPileDamage && fact.Roles.Contains("damage"),"MindBlast must expose native draw-pile damage role");
 var slots=Enumerable.Range(0,9).Select(i=>new CoopBots.Building.ConstructionHand.Slot(i==0?blast:null,i==0?fact:null)).ToArray();
 var hit=CoopBots.Building.ConstructionHand.Solve(slots,5,new(3,0,100,DeckSize:21));
 Check(hit.Damage==16,"MindBlast must deal remaining draw-pile damage on play");
 var empty=CoopBots.Building.ConstructionHand.Solve(slots,9,new(3,0,100,DeckSize:9));
 Check(empty.Damage==0,"MindBlast must lose damage when draw pile empties");
 var bash=ModelDb.Card<Bash>().ToMutable();bash.Owner=q;
 foreach(var n in Enumerable.Range(0,5)){var card=ModelDb.Card<Bash>().ToMutable();card.Owner=q;q.Deck.AddInternal(card);}
 Check(CoopBots.Building.AlliedEnergy.Value(p,2)>0,"ally energy must fund additional affordable ally plays");
 Check(CoopBots.Building.AlliedEnergy.Value(p,0)==0,"zero allied energy must not invent payoff");
 var forced=CoopBots.EventTrade.BestChoice(new[]{((CardModel)blast,-7d),((CardModel)gift,-2d)},true);
 var optional=CoopBots.EventTrade.BestChoice(new[]{((CardModel)blast,-7d),((CardModel)gift,-2d)},false);
 Check(forced.Card==gift && forced.Value==-2 && optional.Card is null && optional.Value==0,"forced choice must take best negative card while optional reward may skip");
 var eventModel=ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.BrainLeech>().ToMutable();
 var effect=OutOfCombatPredictions.NormalizeEventChoice(eventModel,"BRAIN_LEECH.pages.INITIAL.options.SHARE_KNOWLEDGE",new(new CardModel[]{blast,gift},[],[],[]));
 Check(effect.Cards.Count==0 && effect.Bundles.Single() is {Mandatory:true,Cards.Count:2},"brain leech alternatives must become one mandatory choice");
 Console.WriteLine("PASS: MindBlast draw-pile damage and allied-energy ownership.");
 }
 static void SurvivalAndReplay(){
 var board=(ValueTuple<CombatState,Player,Creature>)HarmonyLib.AccessTools.Method(typeof(ActTwoDecisionScenarios),"Board").Invoke(null,new object[]{7,12})!;
 var(c,p,e)=board;
 foreach(var card in new CardModel[]{c.CreateCard<Footwork>(p),c.CreateCard<DefendSilent>(p),c.CreateCard<DefendSilent>(p),c.CreateCard<DefendSilent>(p)})p.PlayerCombatState!.Hand.AddInternal(card);
 var root=KernelSession.Capture(c);
 var defence=new RolloutRun(root.Fork(),new[]{p},new[]{p},new(RolloutPolicyKind.Defend,MaxRounds:1));
 while(!defence.Done)defence.Step();
 var plan=defence.Result!;
 Check(defence.InvestmentProbes>0,"defence policy must compare native investment continuations");
 Check(plan.Actions.Any(a=>a.Card is Footwork),"defensive continuation must invest before spending all energy on block");
 var clone=c.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Mocks.MockAttackMonster>().ToMutable(),CombatSide.Enemy,"clone");
 var otherId=clone.CombatId;
 HarmonyLib.AccessTools.Property(typeof(Creature),"CombatId").SetValue(clone,e.CombatId);
 Check(KernelSession.SameCreature(e,clone) && KernelSession.ResolveCreature(clone,c.Creatures)==e,"spawn identity must resolve by unique combat id and model");
 HarmonyLib.AccessTools.Property(typeof(Creature),"CombatId").SetValue(clone,otherId);
 Check(!KernelSession.SameCreature(e,clone),"same-model different spawn must not alias");
 var death=(ValueTuple<CombatState,Player,Creature>)HarmonyLib.AccessTools.Method(typeof(ActTwoDecisionScenarios),"Board").Invoke(null,new object[]{1,9})!;
 var ally=Player.CreateForNewRun<Deprived>(UnlockState.all,199);ally.ResetCombatState();death.Item1.AddPlayer(ally);ally.MaxEnergy=3;ally.Creature.SetMaxHpInternal(80);ally.Creature.SetCurrentHpInternal(80);ally.PlayerCombatState!.Phase=PlayerTurnPhase.Play;ally.PlayerCombatState.GainEnergy(3);
 death.Item2.PlayerCombatState!.LoseEnergy(3);
 var dying=KernelSession.Capture(death.Item1);Check(dying.EndTurn(death.Item2,80,out _) && dying.EndTurn(ally,80,out _),"death fixture must settle the enemy phase");
 Check(dying.Hp(death.Item2.Creature)==0 && dying.Energy(death.Item2)==0,"dead seat must not reset next-turn energy");
 // A valid frozen boundary can be salvaged even when its old suffix is invalid.
 RefinementRecoveryOnly();
 Console.WriteLine("PASS: defensive investment, summoned identity, dead-seat energy and invalid-reference recovery.");
 }

 internal static void RefinementRecoveryOnly()
 {
 TestEnvironment.Ensure();
 var recovery=(ValueTuple<CombatState,Player,Creature>)HarmonyLib.AccessTools.Method(typeof(ActTwoDecisionScenarios),"Board").Invoke(null,new object[]{80,1})!;
 var(rc,rp,re)=recovery;rc.RemoveCreature(re);
 re=rc.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.BowlbugEgg>().ToMutable(),CombatSide.Enemy,"egg");
 rc.AddCreature(re);re.Monster!.SetUpForCombat();re.SetMaxHpInternal(24);re.SetCurrentHpInternal(24);
 re.Monster.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)re.Monster.MoveStateMachine!.States["BITE_MOVE"],true);
 ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(re,-6,true);
 for(var n=0;n<5;n++)rp.PlayerCombatState!.DrawPile.AddInternal(rc.CreateCard<Bludgeon>(rp));
 var rr=KernelSession.Capture(rc);var missing=rc.CreateCard<StrikeIronclad>(rp);
 var broken=TurnStartFixtureCapture.Record(rr,new KernelTeamSearch.Action[]{new(rp,null,null,EndTurn:true),new(rp,missing,re,CardStateKey:"missing")},allowInvalidSuffix:true);
 var job=new KernelScriptRefinementRun(rr,new[]{rp},new[]{rp},broken,new(ReserveMs:0),5000);
 while(!job.Done && !job.Improved)job.Advance(TimeSpan.FromMilliseconds(2));
 Check(job.Baseline is null && job.Result?.Record is {Verified:true,Victory:true} && job.ValidatedCandidates>0,"invalid suffix must still permit verified search from the frozen boundary");

 Console.WriteLine("PASS: supported native invalid-reference recovery.");
 }

}
