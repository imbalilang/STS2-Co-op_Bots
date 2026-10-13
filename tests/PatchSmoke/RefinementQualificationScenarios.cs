using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Constructed four-seat native branch, not the historical F8/F31 hidden root.
// Prefix actions and future menu contracts are really replayed. Core adoption
// uses the established actual-capture/live-text seam; the bottle entry observer
// only records arguments and does not replace the production policy body.
// Actual production R2, 2026-10-12: private forced build0/test0 -> RED1 ->
// byte-exact source + ten DLL/PDB restore -> forced build0/test0/GREEN0.
// Proofs: work/v17-refinement-qualification-native-20261012-v1/candidate/
// r2-early-v2/proof.json, r2-retire/proof.json, r2-order/proof.json.
// First r2-early matcher failed before any mutation; not a RED, kept separately.
// System.InvalidOperationException: Refinement qualification: environmental known nonreusable boundary must stop before suffix reference/search
// System.InvalidOperationException: Refinement qualification: null-baseline nonreusable boundary must discard old deployed suffix and terminal evidence
// System.InvalidOperationException: Refinement qualification: bottle policy entry must retire old verified victory before pricing the current board
internal static class RefinementQualificationScenarios
{
    private static KernelSession? actual;
    private static string? actualText;
    private static readonly List<TerminalRecord?> bottleForecasts = new();
    private static readonly List<string> removals = new();
    internal static void Run(string mode="all")
    {
        TestEnvironment.Ensure();
        var previous=Enumerable.Range(1,4).Select(i=>BotRegistry.CreateId(BotDifficulty.Pro,171,i))
            .ToDictionary(id=>id,id=>AutoPilot.IsAutopiloted(id));
        var h=new Harmony("coopbots.qualification.private");
        var format=AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText");
        h.Patch(format,prefix:new HarmonyMethod(typeof(RefinementQualificationScenarios),nameof(Format)));
        try {
            if(mode=="all") { Early("environmental");Early("vigor");Adoption("retire");Adoption("order"); }
            else if(mode=="early") { Early("environmental"); Early("vigor"); }
            else if(mode is "retire" or "order") Adoption(mode);
            else if(mode=="cost") { Measure(1);Measure(2); }
            else if(mode=="cost-production") { Measure(1,true);Measure(2,true); }
            else throw new ArgumentException("finite refinement case required");
        } finally { actual=null;actualText=null;h.UnpatchAll(h.Id);foreach(var pair in previous)AutoPilot.Set(pair.Key,pair.Value); }
    }
    private static (CombatState Combat,Player[] Party,Creature Enemy,KernelSession Root,KernelTeamSearch.Action[] Script) Board(string kind,bool longSuffix=false)
    {
        var party=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro,171,i+1))).ToArray();
        var c=new CombatState(runState:RunState.CreateForTest(party,seed:"REFINEMENT-QUALIFICATION",ascensionLevel:10)){RoundNumber=1};
        foreach(var p in party) {
            p.ResetCombatState();c.AddPlayer(p);AutoPilot.Set(p.NetId,true);
            p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);p.MaxEnergy=3;
            p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
            p.PlayerCombatState.Hand.AddInternal(c.CreateCard<StrikeIronclad>(p));
            for(var j=0;j<5;j++)p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        }
        var e=c.CreateCreature(kind=="vigor"?ModelDb.Monster<BowlbugEgg>().ToMutable():ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"enemy");
        c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(longSuffix?2000:5);e.SetCurrentHpInternal(longSuffix?2000:5);
        if(kind=="vigor") {
            e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["BITE_MOVE"],true);
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,-6,true);
            ModelDb.Power<VigorPower>().ToMutable().ApplyInternal(party[2].Creature,2,true);
        } else {
            var move=new MoveState("ATTACK",_=>Task.CompletedTask,new SingleAttackIntent(1));move.FollowUpState=move;
            e.Monster.SetMoveImmediate(move,true);
        }
        var root=KernelSession.Capture(c).ConfigureSynchronousChoicePlayers(party.Select(p=>p.NetId));
        var prefix=TurnStartFixtureCapture.Record(root,party.Select(p=>new KernelTeamSearch.Action(p,null,null,EndTurn:true)).ToArray());
        var after=root.Fork();foreach(var action in prefix)Check(after.ReplayPlanned(action,80,out var why),"native prefix must execute: "+why);
        var suffix=new List<KernelTeamSearch.Action>();
        if(longSuffix) for(var r=0;r<6;r++)foreach(var p in party)suffix.Add(new(p,null,null,EndTurn:true));
        else {var card=after.Hand(party[0])[0];suffix.Add(new(party[0],card,e,CardStateKey:after.CardStateKey(card)));}
        var script=prefix.Concat(TurnStartFixtureCapture.Record(after,suffix)).ToArray();
        return(c,party,e,root,script);
    }
    private static KernelScriptRefinementRun Job(KernelSession root,Player[] party,KernelTeamSearch.Action[] script,KernelDecisionRequest request)
        =>new(root,party,party,script,new TournamentOptions(ReserveMs:0){Request=request},6400);
    private static KernelPlanningState? Evidence(KernelScriptRefinementRun job)
        =>AccessTools.Property(typeof(KernelScriptRefinementRun),"BoundaryEvidence")?.GetValue(job) as KernelPlanningState;
    private static void Reach(KernelScriptRefinementRun job)
    { for(var i=0;job.BoundaryState is null&&!job.Done&&i<64;i++)job.Advance(TimeSpan.Zero); }
    private static void Early(string kind)
    {
        var b=Board(kind);var request=new KernelDecisionRequest(17101,6400,1);var job=Job(b.Root,b.Party,b.Script,request);Reach(job);
        var evidence=Evidence(job);Console.WriteLine("EARLY "+JsonSerializer.Serialize(new{kind,job.StopReason,job.Done,job.BoundaryIndex,
            baseline=job.Baseline is not null,result=job.Result is not null,reusable=evidence?.Reusable,
            mismatch=evidence?.FirstMismatch(job.BoundaryState!.PlanningState(b.Party.Select(p=>p.NetId)))}));
        Check(job.BoundaryState is not null&&job.BoundaryIndex==4&&!job.BoundaryState.PlanningState(b.Party.Select(p=>p.NetId)).Reusable,
            kind+" must expose a genuinely nonreusable native four-seat boundary");
        Check(job.Done&&job.StopReason=="boundary-not-reusable"&&job.Baseline is null&&job.Result is null,
            kind+" known nonreusable boundary must stop before suffix reference/search");
        var parent=(KernelSession)AccessTools.Field(typeof(KernelScriptRefinementRun),"replay").GetValue(job)!;
        Check(parent.PartyStateText()==job.BoundaryText&&job.BoundaryState.PartyStateText()==job.BoundaryText,
            "qualification stop must retain the complete native boundary without executing its suffix");
        Check(!request.IsCancelled&&request.BudgetMs==6400,"qualification decline must not renew or cancel the owning deadline");
        Console.WriteLine("PASS: refinement qualification early "+kind);
    }
    private static void Adoption(string mode)
    {
        var b=Board("environmental");var speculative=new KernelDecisionRequest(17102,6400,1);var job=Job(b.Root,b.Party,b.Script,speculative);Reach(job);
        Check(job.Done&&job.Baseline is null&&Evidence(job) is {Reusable:false},"adoption fixture needs null baseline from its real declined boundary");
        // Real branch terminal supplies the stale winning evidence; it is not a
        // claim that the live game executed this previous branch.
        var winning=b.Root.Fork();Check(winning.Play(winning.Hand(b.Party[0])[0],b.Enemy,out _),"old terminal witness native Strike must execute");
        var old=TerminalRecord.Capture(winning,b.Party,winning.HasTerminal);Check(old is {Verified:true,Victory:true},"old winning metadata must originate from an actual terminal branch");
        var planner=new KernelCombatPlanner();var foreground=new KernelDecisionRequest(17103,6400,1);
        Set(planner,"tournamentRequest",foreground);Set(planner,"tournamentPoint","same-point");
        Set(planner,"tournamentScriptCombat",b.Combat);Set(planner,"tournamentScript",b.Script);Set(planner,"tournamentScriptIndex",4);
        Set(planner,"tournamentScriptRecord",old);Set(planner,"refinement",job);Set(planner,"refinementRequest",speculative);
        Set(planner,"refinementRound",2);Set(planner,"refinementScript",b.Script);b.Combat.RoundNumber=2;
        actual=job.BoundaryState!.Fork();actualText=job.BoundaryText;removals.Clear();bottleForecasts.Clear();
        var h=new Harmony("coopbots.qualification.private.core");
        h.Patch(AccessTools.Method(typeof(KernelSession),nameof(KernelSession.Capture)),prefix:new HarmonyMethod(typeof(RefinementQualificationScenarios),nameof(Capture)));
        h.Patch(AccessTools.Method(typeof(KernelSession),nameof(KernelSession.CaptureLivePartyText)),prefix:new HarmonyMethod(typeof(RefinementQualificationScenarios),nameof(LiveText)));
        h.Patch(AccessTools.Method(typeof(KernelCombatPlanner),"ClearTournamentScript"),prefix:new HarmonyMethod(typeof(RefinementQualificationScenarios),nameof(Removal)));
        h.Patch(AccessTools.Method(typeof(KernelCombatPlanner),"TryPotionOpportunity"),prefix:new HarmonyMethod(typeof(RefinementQualificationScenarios),nameof(Bottle)));
        try {
            if(mode=="retire")Invoke(planner,"TryAdoptRefinement",b.Combat);
            else Invoke(planner,"TryTournamentDecision",b.Combat,b.Party,true,"same-point",null);
            Console.WriteLine("ADOPTION "+JsonSerializer.Serialize(new{mode,removals,bottleForecasts=bottleForecasts.Select(r=>r is null?"null":r.Verified+"/"+r.Victory),
                originalRecordHeld=ReferenceEquals(Get(planner,"tournamentScriptRecord"),old),speculativeCancelled=speculative.IsCancelled,
                foregroundSame=ReferenceEquals(Get(planner,"tournamentRequest"),foreground)}));
            if(mode=="order")Check(bottleForecasts.Count>0&&bottleForecasts[0] is not {Verified:true,Victory:true},
                "bottle policy entry must retire old verified victory before pricing the current board");
            else {
                Check(Get(planner,"tournamentScript") is null&&Get(planner,"tournamentScriptRecord") is null&&speculative.IsCancelled,
                    "null-baseline nonreusable boundary must discard old deployed suffix and terminal evidence");
                var proposal=(TournamentResult?)Get(planner,"mixedProposal");
                Check(proposal is {Record.Verified:false,Record.Victory:false,Script.Count:1}&&proposal.ScriptCheckpoints is null&&!proposal.IsFor(foreground),
                    "declined suffix must remain only an untrusted proposal without old victory or ownership");
                Check(((KernelSession)Get(planner,"rootSession")!).PlanningState(b.Party.Select(p=>p.NetId)).Semantics==actual.PlanningState(b.Party.Select(p=>p.NetId)).Semantics,
                    "declined boundary must re-root to the actual captured native state");
            }
            Check(ReferenceEquals(Get(planner,"tournamentRequest"),foreground)&&!foreground.IsCancelled,
                "boundary retirement must preserve the current foreground request and deadline");
            Console.WriteLine("PASS: refinement qualification "+mode);
        } finally { h.UnpatchAll(h.Id);actual=null;actualText=null; }
    }
    private static void Measure(int repeat,bool production=false)
    {
        var b=Board("environmental",true);KernelScriptRefinementRun job;KernelDecisionRequest request;
        var oldPerf=KernelCombatPlanner.Perf;
        if(production) {
            KernelCombatPlanner.Perf=KernelCombatPlanner.TournamentPerf.High;
            var planner=new KernelCombatPlanner();Invoke(planner,"StartRefinement",b.Root,b.Combat,b.Script);
            job=(KernelScriptRefinementRun)Get(planner,"refinement")!;request=(KernelDecisionRequest)Get(planner,"refinementRequest")!;
            KernelCombatPlanner.Perf=oldPerf;
        } else {request=new KernelDecisionRequest(17110+repeat,6400,1);job=Job(b.Root,b.Party,b.Script,request);}
        var options=(TournamentOptions)AccessTools.Field(typeof(KernelScriptRefinementRun),"options").GetValue(job)!;
        var initial=b.Root.PlanningState(b.Party.Select(p=>p.NetId)).Semantics;
        var allocated=GC.GetTotalAllocatedBytes(true);var gc=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();var watch=Stopwatch.StartNew();
        while(!job.Done)job.Advance(TimeSpan.FromMilliseconds(2));
        var allocation=GC.GetTotalAllocatedBytes(true)-allocated;var boundary=job.BoundaryState!;
        Console.WriteLine("COST "+JsonSerializer.Serialize(new{repeat,wallMs=watch.Elapsed.TotalMilliseconds,job.ComputeMs,allocation,
            gc=Enumerable.Range(0,3).Select(i=>GC.CollectionCount(i)-gc[i]),job.StopReason,job.BoundaryIndex,steps=(int)AccessTools.Field(typeof(KernelScriptRefinementRun),"index").GetValue(job)!,
            suffixActions=((System.Collections.ICollection)AccessTools.Field(typeof(KernelScriptRefinementRun),"actions").GetValue(job)!).Count,
            job.ValidatedCandidates,job.RejectedCandidates,baseline=job.Baseline is not null,search=AccessTools.Field(typeof(KernelScriptRefinementRun),"search").GetValue(job) is not null,
            root=Hash(initial),boundary=Hash(boundary.PlanningState(b.Party.Select(p=>p.NetId)).Semantics),job.BoundaryText,
            budget=request.BudgetMs,requestGeneration=request.RootGeneration,dop=options.MaxDegreeOfParallelism,
            options.TopK,options.ReserveMs,productionFactory=production,sliceMs=2,Steam=false,gainClaim=false}));
    }
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static bool Capture(ref KernelSession __result){if(actual is null)return true;__result=actual.Fork();return false;}
    private static bool LiveText(ref string __result){if(actualText is null)return true;__result=actualText;return false;}
    private static void Bottle(TerminalRecord? forecast)=>bottleForecasts.Add(forecast);
    private static void Removal(string reason)=>removals.Add(reason);
    private static bool Format(ref string __result){__result="";return false;}
    private static void Set(object o,string n,object? v)=>AccessTools.Field(o.GetType(),n).SetValue(o,v);
    private static object? Get(object o,string n)=>AccessTools.Field(o.GetType(),n).GetValue(o);
    private static object? Invoke(object o,string n,params object?[] a)=>AccessTools.Method(o.GetType(),n).Invoke(o,a);
    private static void Check(bool okay,string text){if(!okay)throw new InvalidOperationException("Refinement qualification: "+text);}
}
