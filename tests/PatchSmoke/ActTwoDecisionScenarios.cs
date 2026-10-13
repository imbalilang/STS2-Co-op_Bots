// R2 2026-10-09: legacy fixture authority/request wiring; existing business assertions retained.
// Physical proof: work/architecture-refactor-20261009/r2/legacy-script-contract-proof-v1/proof.json
// Each listed old mechanism: baseline build/run 0/0 -> mutant 0/1 -> exact-byte restoration 0/0.
// Runtime fixture body below equals that compiled proof epoch; these lines are comment-only.
// Actual RED: System.Exception: Act2 decisions: adopting a tied WIPE must retain next-round fallback
// Actual RED: System.Exception: Act2 decisions: ready jobs must log their final disposition and failure
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ActTwoDecisionScenarios
{
    // R2 2026-10-04: 15 deliberate production faults, each exited 1; restored.
    // Commands/full text: work/act2-fixes-20261004/r2-{results,extra-results}.json.
    // "blockable disintegration must precede the enemy attack in threat"
    // "end comparison must spend one energy on the exhausted-card EvilEye payoff"
    // "end comparison must recover both paid defensive actions"
    // "one point of prevention must not automatically spend an exhaust option"
    // "runtime must splice a defensive repair before emitting the stored end"
    // "recorded targeted attacks must canonicalize their current group target"
    // "replay rejection must explain the failing step and legal target kind"
    // "turn boundary must not publish a candidate before native replay validation"
    // "adopting a tied WIPE must retain next-round fallback"
    // "ready jobs must log their final disposition and failure"
    // "baseline startup cost must not starve every actionable comparison"
    // "live deployment must discard the obsolete explicit group target"
    // "extra block must absorb late damage as well as the attack"
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Act2 decisions: " + message); }
    private static (CombatState C, Player P, Creature E) Board(int hp = 80, int incoming = 27)
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 138);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: "ACT2-DECISIONS")) { RoundNumber = 1 };
        p.ResetCombatState(); c.AddPlayer(p); p.MaxEnergy = 3;
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(hp);
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(1000); e.SetCurrentHpInternal(1000);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        if (incoming != 1) ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e, incoming - 1, true);
        return (c, p, e);
    }
    private static T Hand<T>(CombatState c, Player p) where T : CardModel
    { var card = c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static KernelEndTurnRun Compare(KernelSession root, Player p)
    {
        var run = new KernelEndTurnRun(root, p, 5000);
        while (!run.Done) run.Advance(TimeSpan.FromMilliseconds(2));
        return run;
    }
    internal static void Run()
    {
        NightDecisionScenarios.Initialize();
        ThreatAndEnd(); Replay(); Runtime(); RefinementFallback();
    }
    private static void ThreatAndEnd()
    {
        var (c,p,e) = Board(51);
        ModelDb.Power<DexterityPower>().ToMutable().ApplyInternal(p.Creature, 4, true);
        ModelDb.Power<DisintegrationPower>().ToMutable().ApplyInternal(p.Creature, 13, true);
        var grit = Hand<TrueGrit>(c,p); grit.UpgradeInternal(); Hand<DefendIronclad>(c,p); Hand<EvilEye>(c,p);
        var root = KernelSession.Capture(c);
        var after = root.CardBranches(grit,null,8).First(b => b.Boundary.Length == 0
            && b.State.Hand(p).Count == 1 && b.State.Hand(p)[0] is EvilEye).State;
        Check(after.Threat(p).LateDamage == 13 && after.Threat(p).HpLoss == 27,
            "blockable disintegration must precede the enemy attack in threat");
        var protectedState=after.Fork();
        Check(protectedState.Play(protectedState.Hand(p).Single(),null,out _)
            && protectedState.Threat(p).HpLoss == 3,
            "extra block must absorb late damage as well as the attack");
        var check = Compare(after,p);
        Check(check.Actions?.Single().Card is EvilEye && Math.Abs(check.Gain - 24) < .001,
            "end comparison must spend one energy on the exhausted-card EvilEye payoff");
        Check(after.Energy(p) == 2 && after.Block(p.Creature) == 13 && after.Hand(p).Single() is EvilEye,
            "end comparison must preserve its frozen root");
        var expired = new KernelEndTurnRun(after,p,0);
        while(!expired.Done)expired.Advance(TimeSpan.FromMilliseconds(2));
        Check(expired.Compared == 1 && expired.Actions?.Single().Card is EvilEye,
            "baseline startup cost must not starve every actionable comparison");
        var (d,q,_) = Board(32,14);
        ModelDb.Power<DisintegrationPower>().ToMutable().ApplyInternal(q.Creature,13,true);
        ModelDb.Power<DexterityPower>().ToMutable().ApplyInternal(q.Creature,3,true);
        q.PlayerCombatState!.LoseEnergy(1); Hand<DefendSilent>(d,q); Hand<DefendSilent>(d,q);
        var defence = Compare(KernelSession.Capture(d),q);
        Check(defence.Actions?.Count == 2 && Math.Abs(defence.Gain - 16) < .001,
            "end comparison must recover both paid defensive actions");
        var bounded = new KernelEndTurnRun(KernelSession.Capture(d),q);
        while(!bounded.Done)bounded.Advance(TimeSpan.FromMilliseconds(2));
        Check(bounded.Actions?.Count == 2,"normal execution budget must complete the small defensive comparison");
        var (quiet,r,foe) = Board(80,0); Hand<PiercingWail>(quiet,r);
        Check(Compare(KernelSession.Capture(quiet),r).Actions is null,
            "zero-payoff exhaust must remain available for a future turn");
        var (minor,u,_) = Board(80,1); Hand<PiercingWail>(minor,u);
        Check(Compare(KernelSession.Capture(minor),u).Actions is null,
            "one point of prevention must not automatically spend an exhaust option");
        // A blockable late hit is relevant even when the attack intent is zero.
        ModelDb.Power<DisintegrationPower>().ToMutable().ApplyInternal(r.Creature,13,true);
        r.Creature.SetCurrentHpInternal(9); Hand<DefendSilent>(quiet,r);
        Check(KernelSession.Capture(quiet).Threat(r).HpLoss == 13
            && Compare(KernelSession.Capture(quiet),r).Actions?.Any(a=>a.Card is DefendSilent) == true,
            "non-attacking turns must still defend against lethal late damage");
        var (hit,s,target) = Board(2,14); s.PlayerCombatState!.GainEnergy(1);
        ModelDb.Power<DisintegrationPower>().ToMutable().ApplyInternal(s.Creature,13,true);
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(target,30,true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(s.Creature,-2,true);
        Hand<StrikeSilent>(hit,s); Hand<StrikeSilent>(hit,s);
        var strikes = Compare(KernelSession.Capture(hit),s);
        Check(strikes.Actions?.Count == 2 && strikes.Gain > 9,
            "unused energy must not outweigh safe damage before an unavoidable death");
        ModelDb.Power<ThornsPower>().ToMutable().ApplyInternal(target,100,true);
        // With 80HP and no incoming, a hit into lethal thorns cannot be an improvement.
        var (thorn,t,thorns) = Board(80,0); Hand<StrikeSilent>(thorn,t);
        ModelDb.Power<ThornsPower>().ToMutable().ApplyInternal(thorns,100,true);
        Check(Compare(KernelSession.Capture(thorn),t).Actions is null,"end repair must not force lethal thorns");
        Console.WriteLine("PASS: Act2 end comparison recovers EvilEye, two defends and doomed-seat output while preserving exhaust and thorns safety.");
    }
    private static void Replay()
    {
        foreach(var blade in new[]{false,true})
        {
            var(c,p,e)=Board();
            CardModel card = blade ? Hand<SovereignBlade>(c,p) : Hand<Shiv>(c,p);
            PowerModel power = blade ? ModelDb.Power<SeekingEdgePower>().ToMutable() : ModelDb.Power<FanOfKnivesPower>().ToMutable();
            power.ApplyInternal(p.Creature,1,true);
            var root=KernelSession.Capture(c); var action=new KernelTeamSearch.Action(p,card,e,CardStateKey:root.CardStateKey(card));
            var branch=root.Fork();
            Check(branch.ReplayPlanned(action,80,out _,out var normalized) && normalized.Target is null && branch.Hp(e)<root.Hp(e),
                "recorded targeted attacks must canonicalize their current group target");
        }
        var (board,owner,enemy)=Board(); var strike=Hand<StrikeSilent>(board,owner);var start=KernelSession.Capture(board);
        var illegal=new KernelTeamSearch.Action(owner,strike,null,CardStateKey:start.CardStateKey(strike));
        Check(!start.Fork().ReplayPlanned(illegal,80,out var why) && why=="invalid-target",
            "replay must never invent an explicit enemy target");
        var record=TerminalRecord.Capture(start,[owner],false,"fixture");
        var trial=new KernelScriptReplayRun(start,[owner],new(illegal,record,[record],1,1,"fixture"){Script=[illegal]},80);
        while(!trial.Done)trial.Advance(TimeSpan.FromMilliseconds(1));
        Check(trial.Result is null && trial.Failure.Contains("step=0") && trial.Failure.Contains("type=AnyEnemy")
            && trial.Failure.Contains("energy=3"),"replay rejection must explain the failing step and legal target kind");
        Console.WriteLine("PASS: Act2 replay normalizes implicit targets and rejects explicit-target errors with actionable evidence.");
    }
    private static void Set(object obj,string name,object? value) => AccessTools.Field(obj.GetType(),name).SetValue(obj,value);
    private static object? Get(object obj,string name) => AccessTools.Field(obj.GetType(),name).GetValue(obj);
    private static bool Emit(KernelCombatPlanner planner,CombatState c,Player p,out TeamCombatPlanner.Decision? decision)
    {
        // Injected scripts here exercise a bot-controlled execution policy.
        // Restore authority even if the reflected production guard throws.
        var wasDriven = AutoPilot.IsAutopiloted(p.NetId);
        AutoPilot.Set(p.NetId, true);
        try
        {
            object?[] args=[c,new[]{p},true,null];
            var ok=(bool)AccessTools.Method(typeof(KernelCombatPlanner),"TryEmitFromTournamentScript").Invoke(planner,args)!;
            decision=args[3] as TeamCombatPlanner.Decision; return ok;
        }
        finally { AutoPilot.Set(p.NetId, wasDriven); }
    }
    private static void Install(KernelCombatPlanner planner,CombatState c,Player p,params KernelTeamSearch.Action[] script)
    {
        var root=KernelSession.Capture(c);var record=TerminalRecord.Capture(root,[p],false,"fixture");
        if(script.Any(action=>action.EndTurn))script=TurnStartFixtureCapture.Record(root,script);
        Set(planner,"tournamentScript",script);Set(planner,"tournamentScriptCombat",c);Set(planner,"tournamentScriptRound",c.RoundNumber);
        Set(planner,"tournamentScriptRecord",record);Set(planner,"tournamentScriptAliveStates",script.Select(_=>new[]{p.NetId}).ToArray());
        Set(planner,"tournamentScriptEnemyStates",script.Select(_=>c.Enemies.ToArray()).ToArray());Set(planner,"tournamentScriptIndex",0);
    }
    private static void Runtime()
    {
        var(c,p,e)=Board();var defend=Hand<DefendIronclad>(c,p);var planner=new KernelCombatPlanner();
        Install(planner,c,p,new KernelTeamSearch.Action(p,null,null,EndTurn:true));
        TeamCombatPlanner.Decision? decision=null;
        for(var n=0;n<1000 && !Emit(planner,c,p,out decision);n++){}
        Check(decision?.Move.Card==defend && planner.ConfirmedEndTurn is null,
            "runtime must splice a defensive repair before emitting the stored end");
        Check((bool)Get(planner,"tournamentReplanNextRound")!,"repaired losing turn must reconsider the next round");
        // Exercise live target deployment, not only simulator normalization.
        var(group,q,foe)=Board();var shiv=Hand<Shiv>(group,q);
        ModelDb.Power<FanOfKnivesPower>().ToMutable().ApplyInternal(q.Creature,1,true);
        var livePlanner=new KernelCombatPlanner();Install(livePlanner,group,q,new KernelTeamSearch.Action(q,shiv,foe));
        Check(Emit(livePlanner,group,q,out var played) && played?.Move.Target is null,
            "live deployment must discard the obsolete explicit group target");
        Console.WriteLine("PASS: Act2 runtime emits defensive repairs without a full tournament and canonicalizes live group targets.");
    }
    private static string? boundaryText;
    private static KernelSession? boundaryCapture;
    private static bool CaptureBoundary(ref KernelSession __result)
    { if (boundaryCapture is null) return true; __result = boundaryCapture.Fork(); return false; }
    private static readonly List<string> messages = new();
    private static void CaptureInfo(MegaCrit.Sts2.Core.Logging.LogLevel level, string text, int skipFrames) => messages.Add(text);
    private static bool LiveText(ref string __result){__result=boundaryText!;return false;}
    private static void RefinementFallback()
    {
        var(c,p,mock)=Board(3,1);
        // The generic Mock ATTACK marks environmental risk. Use an audited
        // native Bite with the same one-point incoming damage for this reusable
        // next-turn contract; unrelated threat fixtures retain their Mock.
        c.RemoveCreature(mock);
        var e=c.CreateCreature(ModelDb.Monster<BowlbugEgg>().ToMutable(),CombatSide.Enemy,"0");
        c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(1000);e.SetCurrentHpInternal(1000);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["BITE_MOVE"],true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,-6,true);
        var wasDriven=AutoPilot.IsAutopiloted(p.NetId);AutoPilot.Set(p.NetId,true);
        var planner=new KernelCombatPlanner();
        try
        {
        var root=KernelSession.Capture(c).ConfigureSynchronousChoicePlayers([p.NetId]);
        var request=new KernelDecisionRequest(938,5000,1);
        var script=TurnStartFixtureCapture.Record(root,Enumerable.Range(0,4).Select(_=>new KernelTeamSearch.Action(p,null,null,EndTurn:true)));
        var job=new KernelScriptRefinementRun(root,[p],[p],script,new(ReserveMs:0){Request=request},5000);
        while(!job.Done && job.Baseline is null)job.Advance(TimeSpan.FromTicks(1));
        job.FinishAtBoundary(); Check(job.Baseline?.Record is {Verified:true,Victory:false},"fixture must offer a replayed losing baseline");
        var unfinished=new TournamentRun(job.BoundaryState!,[p],[p],new(),5000);
        Set(unfinished,"bestAction",script[0]);Set(unfinished,"bestScript",script);
        Set(unfinished,"bestRecord",job.Baseline!.Record! with {Victory=true});Set(job,"search",unfinished);
        job.FinishAtBoundary();
        Check(ReferenceEquals(job.Result,job.Baseline),"turn boundary must not publish a candidate before native replay validation");
        Install(planner,c,p,script);
        Set(planner,"refinementRequest",request);Set(planner,"tournamentRootGeneration",request.RootGeneration);
        Set(planner,"refinement",job);Set(planner,"refinementRound",2);Set(planner,"refinementScript",script);
        Set(planner,"tournamentScriptIndex",job.BoundaryIndex);c.RoundNumber=2;boundaryText=job.BoundaryText;
        var h=new Harmony("act2.refinement-boundary");h.Patch(AccessTools.Method(typeof(KernelSession),nameof(KernelSession.CaptureLivePartyText)),
            prefix:new HarmonyMethod(typeof(ActTwoDecisionScenarios),nameof(LiveText)));
        // Only the adoption seam is supplied: the live board has not played
        // this prefix. The losing baseline itself is genuinely replayed.
        boundaryCapture=job.BoundaryState;
        var capture=AccessTools.Method(typeof(KernelSession),nameof(KernelSession.Capture));
        h.Patch(capture,prefix:new HarmonyMethod(typeof(ActTwoDecisionScenarios),nameof(CaptureBoundary)));
        try{AccessTools.Method(typeof(KernelCombatPlanner),"TryAdoptRefinement").Invoke(planner,[c]);}
        finally
        {
            h.Unpatch(capture,HarmonyPatchType.All,h.Id);
            h.Unpatch(AccessTools.Method(typeof(KernelSession),nameof(KernelSession.CaptureLivePartyText)),HarmonyPatchType.All,h.Id);
            boundaryText=null;boundaryCapture=null;
        }
        Check((bool)Get(planner,"tournamentReplanNextRound")!,"adopting a tied WIPE must retain next-round fallback");
        // A real failed next job, after adoption, must not suppress fallback.
        var badRequest=new KernelDecisionRequest(940,5000,1);
        var attack=Hand<StrikeSilent>(c,p);var bad=new KernelScriptRefinementRun(
            KernelSession.Capture(c).ConfigureSynchronousChoicePlayers([p.NetId]),[p],[p],
            [new(p,attack,null)],new(){Request=badRequest},5000);
        while(!bad.Done)bad.Advance(TimeSpan.FromMilliseconds(1));
        Check(bad.StopReason=="reference-refused:invalid-target" && bad.FailureDetail.Contains("step=0"),"failed job must preserve its replay reason");
        Set(planner,"refinement",bad);Set(planner,"refinementRequest",badRequest);Set(planner,"refinementRound",3);Set(planner,"refinementReported",true);
        c.RoundNumber=3;
        messages.Clear(); MegaCrit.Sts2.Core.Logging.Log.LogCallback += CaptureInfo;
        try
        {
            Check(!Emit(planner,c,p,out _) && Get(planner,"tournamentScript") is null,
                "failed next refinement must return a losing script to foreground search");
            Check(messages.Any(m=>m.Contains("refinement: disposed") && m.Contains("ready=True")
                && m.Contains("reference-refused:invalid-target")),"ready jobs must log their final disposition and failure");
        }
        finally{MegaCrit.Sts2.Core.Logging.Log.LogCallback -= CaptureInfo;messages.Clear();}
        Console.WriteLine("PASS: Act2 adopted-WIPE then invalid-target pipeline falls back on the next round, including already-reported jobs.");
        }
        finally
        {
            boundaryText=null;boundaryCapture=null;
            planner.Reset(newCombat:true);AutoPilot.Set(p.NetId,wasDriven);
        }
    }
}
