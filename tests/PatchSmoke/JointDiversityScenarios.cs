using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class JointDiversityScenarios
{
    // Production mutation failures and restoration: work/strength-20260928-v20/r2-results.json.
    // System.Exception: Unmerged frontier must keep low-immediate-score poison setup for continuation evaluation.
    // System.Exception: Joint prefixes must evaluate all five continuation policies.
    // System.Exception: Interrupted continuation must be an explicit cutoff, never a fabricated terminal.
    internal static void Run()
    {
        var harmony=new Harmony("cn.xiwa.coopbots.joint-diversity-fixture");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(JointDiversityScenarios),nameof(Format)));
        var (p,combat)=Board(12);
        p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        var root=KernelSession.Capture(combat);
        var rootText=root.StateText(p);
        var joint=new KernelJointTurnRun(root,[p],[p],new TournamentOptions(IncludePotions:false),3000);
        while(!joint.Advance(TimeSpan.FromMilliseconds(5))) { }
        Console.WriteLine("PROBE diversity "+joint.Diagnostics);
        Check(joint.Result?.Record is {Verified:true,Victory:true},"Diverse continuations must retain a verified winning prefix.");
        foreach(var policy in new[]{"Tactical:","Kill:","Growth:","Cleanup:","Defend:"})
            Check(joint.Diagnostics.Contains(policy),"Joint prefixes must evaluate all five continuation policies.");
        var result=joint.Result!;var replay=root.Fork();
        Check(result.Script!.Count==result.ScriptAliveStates!.Count
            && result.Script.Count==result.ScriptEnemyStates!.Count,"Diverse scripts must keep action-aligned evidence.");
        foreach(var action in result.Script)Check(replay.ReplayPlanned(action,80,out _),"Diverse winning script must replay natively.");
        Check(replay.HasWon && root.StateText(p)==rootText,"Continuation policies must not mutate their shared root.");
        Console.WriteLine("PASS: five joint continuation policies produce a replayable winner without mutating the root.");

        var (owner,setupCombat)=Board(200);
        for(var i=0;i<6;i++)
        {
            owner.PlayerCombatState!.Hand.AddInternal(setupCombat.CreateCard<DefendIronclad>(owner));
            owner.PlayerCombatState.Hand.AddInternal(setupCombat.CreateCard<StrikeIronclad>(owner));
        }
        var poison=setupCombat.CreateCard<DeadlyPoison>(owner);owner.PlayerCombatState.Hand.AddInternal(poison);
        var setupRoot=KernelSession.Capture(setupCombat);
        bool Retains(bool enabled)
        {
            using var search=new KernelTeamSearch(setupRoot,[owner],s=>s.Block(owner.Creature)*10-s.Enemies.Sum(e=>s.Hp(e)),
                new KernelTeamSearch.Options(Depth:1,Width:4,MaxNodes:30)
                {IncludePotions=false,Deduplicate=false,PreserveSetupRoutes=enabled,CaptureReplayStates=false});
            while(!search.Advance(TimeSpan.FromMilliseconds(5),()=>true)) { }
            return search.ContinuationPaths.Any(path=>path.Any(a=>a.Card==poison));
        }
        Check(Retains(true),"Unmerged frontier must keep low-immediate-score poison setup for continuation evaluation.");
        Check(!Retains(false),"Poison fixture must distinguish setup preservation from ordinary category retention.");
        var run=new RolloutRun(setupRoot.Fork(),[owner],[owner],new RolloutOptions());
        run.FinishAtBudget();
        Check(run.Result is {Terminal:false,Victory:false,StopReason:"continuation-budget"},
            "Interrupted continuation must be an explicit cutoff, never a fabricated terminal.");
        var tiny=new KernelJointTurnRun(setupRoot,[owner],[owner],new TournamentOptions(IncludePotions:false),1);
        var ticks=0;
        while(!tiny.Advance(TimeSpan.FromMilliseconds(1)) && ticks++<500) { }
        Check(tiny.Done,"Joint search must stop at a small total budget without requiring a completed rollout.");
        Console.WriteLine("PASS: setup candidates survive an unmerged beam and budget interruption remains explicit.");

        // V21 #1/#2: real-complexity budget stress. Four seats, eight cards
        // each and two 1500-HP attackers make the horizon produce eight
        // continuation paths, so one Tactical continuation costs more than the
        // old `budgetMs*.45/paths.Count` share. Production joint budget is half
        // of `high`, so the fixture uses 1600 ms.
        //
        // R2 red原文 (2026-09-29, old `Math.Max(15, budgetMs*.45/paths.Count)`):
        //   PROBE stress stop=node-budget paths=8/8
        //     policies=[Kill:4,Growth:5,Cleanup:3,Defend:2] budgetCutoffs=4
        //     replayFailures=0 reference=continuation-budget boundaries=[] (Tactical:0)
        //   System.Exception: Joint search must name a reference route that
        //     finished, not one the per-job share cut off.
        //     at JointDiversityScenarios.Run()
        // That matches the live defeat-v24 log, where Tactical was cut in 38 of
        // 42 searches while the cheaper policies completed.
        var (stressParty,stressCombat)=Stress(2,1500);
        foreach(var seat in stressParty)
        {
            seat.PlayerCombatState!.Hand.AddInternal(stressCombat.CreateCard<StrikeIronclad>(seat));
            seat.PlayerCombatState.Hand.AddInternal(stressCombat.CreateCard<DefendIronclad>(seat));
            seat.PlayerCombatState.Hand.AddInternal(stressCombat.CreateCard<Bash>(seat));
            seat.PlayerCombatState.Hand.AddInternal(stressCombat.CreateCard<InfiniteBlades>(seat));
            seat.PlayerCombatState.Hand.AddInternal(stressCombat.CreateCard<DeadlyPoison>(seat));
            seat.PlayerCombatState.Hand.AddInternal(stressCombat.CreateCard<StrikeIronclad>(seat));
            seat.PlayerCombatState.Hand.AddInternal(stressCombat.CreateCard<DefendIronclad>(seat));
            seat.PlayerCombatState.Hand.AddInternal(stressCombat.CreateCard<Survivor>(seat));
        }
        var stressRoot=KernelSession.Capture(stressCombat);
        var stressText=stressRoot.StateText(stressParty[0]);
        var stressJoint=new KernelJointTurnRun(stressRoot,stressParty,stressParty,
            new TournamentOptions(IncludePotions:false,Rollout:new RolloutOptions(MaxRounds:8)),1600);
        var stressTicks=0;
        while(!stressJoint.Advance(TimeSpan.FromMilliseconds(5)) && stressTicks++<60000) { }
        Console.WriteLine("PROBE stress "+stressJoint.Diagnostics);
        Check(stressJoint.Prefixes>=1,"Joint search must finish a full continuation inside the production budget.");
        Check(stressJoint.Result?.Record is not null,"Joint search must return a candidate rather than only cut-off jobs.");
        Check(stressJoint.Diagnostics.Contains("reference=")&&!stressJoint.Diagnostics.Contains("reference=pending")
            &&!stressJoint.Diagnostics.Contains("reference=continuation-budget"),
            "Joint search must name a reference route that finished, not one the per-job share cut off.");
        Check(stressJoint.Diagnostics.Contains("Tactical:")
            &&new[]{"Kill:","Growth:","Cleanup:","Defend:"}.Any(kind=>stressJoint.Diagnostics.Contains(kind)),
            "Guaranteeing the reference route must not starve every other continuation policy.");
        var stressScript=stressJoint.Result!.Script!;
        var stressReplay=stressRoot.Fork();
        foreach(var action in stressScript)
            Check(stressReplay.ReplayPlanned(action,80,out var reason),"Stress joint script must replay natively: "+reason);
        Check(stressRoot.StateText(stressParty[0])==stressText,"The stress reference must not mutate its shared root.");
        Console.WriteLine("PASS: a production-budget joint search finishes its reference route on a four-seat fight.");
    }

    private static (Player[],CombatState) Stress(int enemies,int enemyHp)
    {
        var party=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro,16,i))).ToArray();
        var combat=new CombatState(runState:RunState.CreateForTest(party,seed:"JOINT-BUDGET-STRESS"));
        foreach(var p in party)
        {
            p.ResetCombatState();combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(120);p.Creature.SetCurrentHpInternal(120);
            p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(10);
        }
        for(var i=0;i<enemies;i++)
        {
            var foe=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,i.ToString());
            combat.AddCreature(foe);foe.Monster!.SetUpForCombat();foe.SetMaxHpInternal(enemyHp);foe.SetCurrentHpInternal(enemyHp);
            foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"],true);
        }
        return(party,combat);
    }

    private static (Player,CombatState) Board(int enemyHp)
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,BotRegistry.CreateId(BotDifficulty.Pro,32,0));
        var combat=new CombatState(runState:RunState.CreateForTest([p],seed:"JOINT-DIVERSITY"));
        p.ResetCombatState();combat.AddPlayer(p);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
        p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
        var foe=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"enemy");
        combat.AddCreature(foe);foe.Monster!.SetUpForCombat();foe.SetMaxHpInternal(enemyHp);foe.SetCurrentHpInternal(enemyHp);
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"],true);
        return(p,combat);
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static bool Format(ref string __result){__result="";return false;}
}
