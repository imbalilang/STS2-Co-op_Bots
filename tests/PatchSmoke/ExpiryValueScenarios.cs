// R2 private physical-mirror proof: six source faults; every force-Rebuild succeeded,
// then named RED exited 1; exact source bytes restored, Rebuild 0 / same fixture GREEN 0.
// Raw logs, sidecars, before/mutant/restored files and SHA are at:
// work/szg-repair-20261008T124624/perf-audit/expiry/r2/r2-results.json
// empty-allocation: System.InvalidOperationException: Expiry optimization: empty expiry must avoid per-call allocations after the effective-power cache is warm; bytes=2960000
// empty-source-identity: System.InvalidOperationException: Expiry optimization: empty expiry must keep the physical source session
// native-parent-isolation: System.InvalidOperationException: Expiry optimization: projection must preserve parent powers, cards, HP, RNG and counters
// native-participant-set: System.InvalidOperationException: Expiry optimization: own and party projections must expire only participating owners while all projection expires enemy wrappers
// complete-value-equality: System.InvalidOperationException: Expiry optimization: complete tactical/persistent/future values and native residual states must equal the unoptimized baseline; first=a0-m0-rFalse/action expected=40715429E379D071 actual=40714A9049E036D7
// single-party-projection: System.InvalidOperationException: Expiry optimization: party tactical value must share one native residual projection without repeating an expiry scan for every owner; calls=3
// Actual R2 compiled fixture SHA256 (before these comment-only annotations): 55c3100f74425b7b3d9ce2db474f0ef59b0d7fe2ee8aba662a8ade5c42ceb367
using System.Text.Json;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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
internal static class ExpiryValueScenarios
{
 internal static void Run(bool record)
 {
  TestEnvironment.Ensure();var h=new Harmony("coopbots.expiry-values");
  h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),prefix:new HarmonyMethod(typeof(ExpiryValueScenarios),nameof(Format)));
  try {
   var rows=new SortedDictionary<string,string>();
   foreach(var artifact in new[]{0,1}) foreach(var mode in new[]{0,1,2,3}) foreach(var reverse in new[]{false,true})
   {
    var (p,c,e)=Board();
    foreach(var owner in p) {
     owner.PlayerCombatState!.Hand.AddInternal(c.CreateCard<StrikeIronclad>(owner));owner.PlayerCombatState.Hand.AddInternal(c.CreateCard<DefendIronclad>(owner));
     owner.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<Shiv>(owner));owner.PlayerCombatState.DiscardPile.AddInternal(c.CreateCard<BladeDance>(owner));
     Pow<DemonFormPower>(owner.Creature,2);Pow<AfterimagePower>(owner.Creature,1);Pow<AccuracyPower>(owner.Creature,2);Pow<EnergyNextTurnPower>(owner.Creature,1);
     if(artifact>0)Pow<ArtifactPower>(owner.Creature,artifact);
    }
    Pow<VulnerablePower>(e,4);Pow<WeakPower>(e,3);
    var root=KernelSession.Capture(c);var s=root.Fork();var combat=(SimulatedCombatState)s.Simulator.State.CombatState;
    if(mode>0) foreach(var owner in reverse?p.Reverse():p) {
     if(mode!=2)combat.ApplyTemporaryStrengthGain<SetupStrikePower>(owner.Creature,3,owner.Creature);
     combat.ApplyTemporaryDexterity<AnticipatePower>(owner.Creature,2,owner.Creature);
     if(mode==3)combat.ApplyTemporaryFocus<HotfixPower>(owner.Creature,1,owner.Creature);
    }
    if(mode>0)combat.ApplyTemporaryStrengthLoss<PiercingWailPower>(e,4,p[0].Creature);
    PowerLifecycleSupport.ResolvePowerAmountChanges(s.Simulator,combat);
    var key=$"a{artifact}-m{mode}-r{reverse}";var before=Full(s,p,e);
    rows[key+"/value"]=Bits(KernelTacticalPolicy.Value(s,p,p));
    foreach(var owner in p)rows[key+"/persistent"+owner.NetId]=Bits(s.PersistentValue(owner,18,3,root));
    rows[key+"/future"]=Bits(s.FutureDebuffValue(p,reference:root));
    var own=s.AfterTemporaryStatExpiry([p[0].Creature]);var party=s.AfterTemporaryStatExpiry(p.Select(x=>x.Creature));var all=s.AfterTemporaryStatExpiry();
    rows[key+"/own"]=Full(own,p,e);rows[key+"/party"]=Full(party,p,e);rows[key+"/all"]=Full(all,p,e);
    Check(Full(s,p,e)==before,"projection must preserve parent powers, cards, HP, RNG and counters");
    if(mode>0)Check(own.Power<AnticipatePower>(p[1].Creature)==2 && party.Power<PiercingWailPower>(e)==4 && all.Power<PiercingWailPower>(e)==0,
     "own and party projections must expire only participating owners while all projection expires enemy wrappers");
    var played=s.Fork();Check(played.Play(p[0].PlayerCombatState!.Hand.Cards[0],e,out _),"value comparison must execute the real card pipeline");
    rows[key+"/action"]=Bits(KernelTacticalPolicy.ActionValue(s,played,p,p));
   }
   var filename=Path.Combine(AppContext.BaseDirectory,"expiry-values.baseline.json");
   if(record){File.WriteAllText(filename,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("RECORDED "+rows.Count+" complete scalar/native-state outputs");}
   else {var expected=JsonSerializer.Deserialize<SortedDictionary<string,string>>(File.ReadAllText(filename))!;
    var difference=rows.FirstOrDefault(x=>!expected.TryGetValue(x.Key,out var old)||old!=x.Value);
    Check(expected.Count==rows.Count && difference.Key is null,"complete tactical/persistent/future values and native residual states must equal the unoptimized baseline; first="+difference.Key+" expected="+(difference.Key is null?"none":expected.GetValueOrDefault(difference.Key))+" actual="+difference.Value);
    Console.WriteLine("PASS: expiry complete values/native states "+rows.Count);
    EmptyAllocation();
    SinglePartyProjection();
   }
  } finally {h.UnpatchAll(h.Id);}
 }
 static void EmptyAllocation() {
  var(p,c,e)=Board();var root=KernelSession.Capture(c);Creature[] owners=[p[0].Creature,p[1].Creature];
  for(int i=0;i<1000;i++)root.AfterTemporaryStatExpiry(owners);
  var old=GC.GetAllocatedBytesForCurrentThread();bool same=true;
  for(int i=0;i<10000;i++)same &= ReferenceEquals(root.AfterTemporaryStatExpiry(owners),root);
  var bytes=GC.GetAllocatedBytesForCurrentThread()-old;
  Check(same,"empty expiry must keep the physical source session");
  Check(bytes<=1024,"empty expiry must avoid per-call allocations after the effective-power cache is warm; bytes="+bytes);
  Console.WriteLine("PASS: expiry empty calls=10000 allocated="+bytes);
 }
 static int expiryCalls;
 static void CountExpiry()=>expiryCalls++;
 static void SinglePartyProjection() {
  var(p,c,e)=Board();var root=KernelSession.Capture(c);var h=new Harmony("coopbots.expiry-single-party");
  h.Patch(AccessTools.Method(typeof(KernelSession),"AfterTemporaryStatExpiry"),prefix:new HarmonyMethod(typeof(ExpiryValueScenarios),nameof(CountExpiry)));
  try {expiryCalls=0;var value=KernelTacticalPolicy.Value(root,p,p);var calls=expiryCalls;
   Check(calls==1,"party tactical value must share one native residual projection without repeating an expiry scan for every owner; calls="+calls);
   Console.WriteLine("PASS: expiry single party projection calls="+calls+" value="+value);
  } finally {h.UnpatchAll(h.Id);}
 }
 static string Full(KernelSession s,Player[] p,Creature e)=>s.PartyStateText()+"/"+string.Join('|',p.Select(x=>x.Creature).Append(e).Select(owner=>string.Join(';',((SimulatedCombatState)s.Simulator.State.CombatState).EffectivePowers().Where(x=>x.Owner==owner).OrderBy(x=>x.Id.Entry).Select(x=>$"{x.Id.Entry}:{x.Amount}:{x.TypeForCurrentAmount}:{x.SkipNextDurationTick}:{x.AmountOnTurnStart}:{x.Applier?.CombatId}"))));
 static string Bits(double x)=>BitConverter.DoubleToInt64Bits(x).ToString("X16");
 static (Player[],CombatState,Creature) Board(){var p=Enumerable.Range(0,2).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,710UL+(ulong)i)).ToArray();var c=new CombatState(runState:RunState.CreateForTest(p,seed:"EXPIRY-OPT-PROOF"));foreach(var x in p){x.ResetCombatState();c.AddPlayer(x);x.Creature.SetMaxHpInternal(80);x.Creature.SetCurrentHpInternal(80);x.PlayerCombatState!.Phase=PlayerTurnPhase.Play;x.PlayerCombatState.GainEnergy(3);}var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(600);e.SetCurrentHpInternal(600);e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);return(p,c,e);}
 static void Pow<T>(Creature o,int n)where T:PowerModel=>ModelDb.Power<T>().ToMutable().ApplyInternal(o,n,true);
 static bool Format(ref string __result){__result="";return false;}
 static void Check(bool ok,string text){if(!ok)throw new InvalidOperationException("Expiry optimization: "+text);}
}
