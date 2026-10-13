using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class PartySetupPendingNativeScenarios
{
    private const BindingFlags Members = BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Run(string? only=null)
    {
        TestEnvironment.Ensure();using var presentation=new Presentation();
        var assembly=typeof(CardModel).Assembly;
        Console.WriteLine($"LOADED Game={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
        if(only=="warm") System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(AccessTools.Method(typeof(KernelSession),"ResumePreparedPartySetup").MethodHandle);
        if(only is null or "risk" or "warm") RiskCaller();
        if(only is null or "pending") PendingFrame();
        if(only is null or "automatic") AutomaticAndExact();
    }
    private static void PendingFrame()
    {
        // R2 clear-pending-red exited 1: Party pending: pending native menu must retain its full two-of-eight identity.
        using var board=new Board();using var observer=new ResumeObserver();
        var root=KernelCombatCapture.Capture(board.Combat).ConfigureSynchronousChoicePlayers([board.Party[0].NetId]);
        var rootFacts=root.PlanningState(board.Ids);var branch=root.Fork();
        Check(!branch.EndTurn(board.Party[0],80,out var why),"unanswered branch must retain pending boundary");
        var sim=(CombatPredictionSimulator)AccessTools.Field(typeof(KernelSession),"simulator").GetValue(branch)!;
        var combat=(SimulatedCombatState)sim.State.CombatState;
        Check(combat.PendingTurnStartChoice is {SourceId:"TOOLS_OF_THE_TRADE_POWER",Count:2,Spec.Options.Count:8},
            "pending native menu must retain its full two-of-eight identity");
        var pause=sim.DetachPartySetupPause([]);
        Check(pause is not null&&pause.Continuation.Steps.Count>0,"unanswered branch must retain originalEntry qualified original continuation");
        var fork=sim.ForkPartySetupPause(pause!,out var continuation);
        bool Resume(CombatPredictionSimulator target,Func<bool> action)
        {
            var state=(SimulatedCombatState)target.State.CombatState;
            var cursor=TurnStartChoiceCursor.ForAutomaticPolicy(request=>KernelRelicChoices.Choose(target,board.Party[0],request.Spec!) with
                {SourceId=request.SourceId,ContextId=request.ContextId,Timing=request.Timing});
            state.BeginActionChoices(cursor);state.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            try{return action();}finally{state.EndActionChoices();}
        }
        using(KernelSearchBudgetScope.Enter(new KernelDecisionRequest(211,0,6)))
        {
            Check(Resume(fork,()=>fork.ResumeExecutionContinuation(continuation)),"native fork consumes its original mapped Tools continuation");
            Check(Resume(sim,()=>sim.ResumePartySetupPause(pause!)),"original pending continuation remains resumable after originalEntry separate fork");
        }
        Check(!combat.HasPendingChoice&&!((SimulatedCombatState)fork.State.CombatState).HasPendingChoice,
            "real answers clear only the corresponding native pending menus");
        Check(sim.State.GetPlayerCombatState(board.Party[0]).Hand.Cards.Count==6
            &&fork.State.GetPlayerCombatState(board.Party[0]).Hand.Cards.Count==6,
            "fork and original each naturally consume exactly two native cards");
        var originalFacts=branch.PlanningState(board.Ids);
        var forkView=ContinuationObservation(branch,fork);
        var forkFacts=forkView.PlanningState(board.Ids);
        var originalMechanisms=new System.Text.StringBuilder();
        var forkMechanisms=new System.Text.StringBuilder();
        Check(branch.AppendPlanningMechanismFacts(originalMechanisms)
            &&forkView.AppendPlanningMechanismFacts(forkMechanisms)
            &&string.Equals(originalMechanisms.ToString(),forkMechanisms.ToString(),StringComparison.Ordinal),
            "original and mapped fork complete mechanism Facts must be available and equal");
        Check(string.Equals(originalFacts.Semantics,forkFacts.Semantics,StringComparison.Ordinal)
            &&originalFacts.Reusable==forkFacts.Reusable
            &&string.Equals(originalFacts.SearchMetadata,forkFacts.SearchMetadata,StringComparison.Ordinal),
            "original and mapped fork resume retain complete PlanningState/Facts and qualification metadata");
        var physical=AccessTools.Field(typeof(KernelPlanningState),"physicalCards");
        var originalCards=(CardModel[])physical.GetValue(originalFacts)!;
        var forkCards=(CardModel[])physical.GetValue(forkFacts)!;
        Check(originalCards.Length==forkCards.Length
            &&originalCards.Zip(forkCards).All(pair=>ReferenceEquals(pair.First,pair.Second)),
            "original and mapped fork resume retain every physical card reference");
        SameHistory(sim,fork);
        Check(RngStates(sim).SequenceEqual(RngStates(fork)),
            "original and mapped fork resume retain all nine full RNG stream states");
        Check(root.PlanningState(board.Ids).Matches(rootFacts),"pending/fork/native resume must leave its legal search parent unchanged");
        Check(KernelCombatCapture.Capture(board.Combat).ConfigureSynchronousChoicePlayers([board.Party[0].NetId])
            .PlanningState(board.Ids).Matches(rootFacts),"prediction resume must not mutate the actual native root/history/RNG");
        Check(CombatManager.Instance.History.CardPlaysFinished.Count()==board.NativeSetupHistoryCount
            &&board.NativeSetupHistoryCount-board.NativeSetupHistoryStart==3,
            "prediction resume must preserve the exact native history baseline plus three actual setup plays");
        Check(!ResumeObserver.Stopped,"bounded observer exhaustion is originalEntry failing test, never originalEntry native PASS");
        Console.WriteLine($"PARTY RESUMED fullSemantics=True physicalCards={originalCards.Length} history={sim.History.Entries.Count} rngStreams=9 reusable={originalFacts.Reusable}/{forkFacts.Reusable}");
        Console.WriteLine($"PARTY PENDING boundary={why} steps={pause!.Continuation.Steps.Count} forkHand=6 parentUnchanged=True");
        Console.WriteLine("PASS: unanswered native Tools retains its legal pending frame and forkable/resumable continuation.");
    }
    private static void RiskCaller()
    {
        // R2 baseline-risk-red exited 1: PARTY RISK simulated=False resumes=8 stopped=True;
        // Party pending: ordinary unanswered synchronous Tools menu must return pending after one native resume.
        using var board=new Board();using var observer=new ResumeObserver();
        var watch=Stopwatch.StartNew();
        var result=LiveEndTurnRisk.Evaluate(board.Combat,board.Party,2,[board.Party[0].NetId]);
        Console.WriteLine($"PARTY RISK simulated={result.Simulated} resumes={ResumeObserver.Calls} stopped={ResumeObserver.Stopped} elapsed={watch.Elapsed.TotalMilliseconds:F3}ms menu={ResumeObserver.Last}");
        Check(!result.Simulated,"unanswered native Tools menu is not evidence of no risk");
        Check(ResumeObserver.Calls==1&&!ResumeObserver.Stopped,
            "ordinary unanswered synchronous Tools menu must return pending after one native resume");
        Console.WriteLine("PASS: actual LiveEndTurnRisk returns pending after one unanswered synchronous native Tools menu.");
    }
    private static void AutomaticAndExact()
    {
        // R2 reject-complete-red exited 1: Party pending: automatic native Tools remains executable: round-pending-choice@start-next-player-turn.
        using var board=new Board();
        var root=KernelCombatCapture.Capture(board.Combat).ConfigureSynchronousChoicePlayers([board.Party[0].NetId]);
        var branch=root.ForkForReplay();
        using var observer=new ResumeObserver(maximum:2*KernelChoiceContinuation.MaximumChoices+8);
        using var deadline=KernelSearchBudgetScope.Enter(new KernelDecisionRequest(212,0,6));
        Check(branch.EndTurn(board.Party[0],80,out var why,out var action),"automatic native Tools remains executable: "+why);
        var contract=action.TurnStartChoices;
        Check(contract is {Complete:true}&&contract.Choices.Count==1&&contract.Choices[0].Cards.Count==2
            &&contract.Choices[0].Options?.Count==8,"automatic menu records exactly the native eight-card/two-discard contract");
        var replay=root.Fork();
        Check(replay.ReplayPlanned(action,80,out why),"exact recorded menu remains replayable: "+why);
        Check(replay.PlanningState(board.Ids).Matches(branch.PlanningState(board.Ids)),
            "recorded native menu retains complete branch Facts/RNG and physical references");
        var bad=contract! with {Choices=[]};
        var rejected=root.Fork();
        Check(!rejected.ReplayPlanned(action with {TurnStartChoices=bad},80,out why)
            &&why.StartsWith("turn-start-contract:"),"missing recorded menu must fail without retry: "+why);
        Check(!ResumeObserver.Stopped,"automatic/replay must not hide bounded observer exhaustion as originalEntry contract rejection");
        Console.WriteLine($"PARTY AUTOMATIC recordedOptions={contract!.Choices[0].Options!.Count} selected={contract.Choices[0].Cards.Count} replayMismatch={replay.PlanningState(board.Ids).FirstMismatch(branch.PlanningState(board.Ids))??"equal"} rejection={why}");
        Console.WriteLine("PASS: automatic Tools menu and exact replay remain complete while missing contracts fail promptly.");
    }
    private sealed class Board:IDisposable
    {
        internal readonly Player[] Party;internal readonly CombatState Combat;
        internal readonly int NativeSetupHistoryStart,NativeSetupHistoryCount;
        internal ulong[] Ids=>Party.Select(player=>player.NetId).ToArray();
        private readonly IDisposable native,managers;
        internal Board()
        {
            Party=Enumerable.Range(1,4).Select(index=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)index)).ToArray();
            Combat=new CombatState(runState:RunState.CreateForTest(Party,seed:"PARTY-PENDING-TOOLS2",ascensionLevel:10)){RoundNumber=1};
            foreach(var player in Party)
            {
                AccessTools.Field(typeof(Player),"<Character>k__BackingField").SetValue(player,ModelDb.Character<Ironclad>());
                player.ResetCombatState();Combat.AddPlayer(player);player.MaxEnergy=3;player.Creature.SetMaxHpInternal(80);
                player.Creature.SetCurrentHpInternal(player==Party[0]?69:0);player.PlayerCombatState!.Phase=PlayerTurnPhase.Play;player.PlayerCombatState.GainEnergy(3);
                for(var index=0;index<10;index++)player.PlayerCombatState.DrawPile.AddInternal(Combat.CreateCard<StrikeIronclad>(player));
            }
            var enemy=Combat.CreateCreature(ModelDb.Monster<Axebot>().ToMutable(),CombatSide.Enemy,"axebot");Combat.AddCreature(enemy);
            native=Scope("NativeFactsState",Combat);managers=Scope("NativeReadyManagerScope",Combat);
            enemy.Monster!.SetUpForCombat();enemy.Monster.RollMove(Party.Select(player=>player.Creature));
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["BOOT_UP_MOVE"],true);
            var owner=Party[0];var history=CombatManager.Instance.History.CardPlaysFinished.Count();
            NativeSetupHistoryStart=history;
            foreach(var card in new CardModel[]{Combat.CreateCard<ToolsOfTheTrade>(owner),Combat.CreateCard<ToolsOfTheTrade>(owner),Combat.CreateCard<MachineLearning>(owner)})
            {
                owner.PlayerCombatState!.Hand.AddInternal(card);var paid=card.SpendResources().GetAwaiter().GetResult();
                Check(paid.Item1==1,"each actual native setup card must pay one energy");
                card.OnPlayWrapper(new ThrowingPlayerChoiceContext(),null,false,
                    new ResourceInfo{EnergySpent=paid.Item1,EnergyValue=paid.Item1,StarsSpent=paid.Item2,StarValue=paid.Item2},skipCardPileVisuals:true).GetAwaiter().GetResult();
            }
            Check(owner.PlayerCombatState!.Energy==0&&owner.Creature.GetPower<ToolsOfTheTradePower>()?.Amount==2
                &&owner.Creature.GetPower<MachineLearningPower>()?.Amount==1
                &&CombatManager.Instance.History.CardPlaysFinished.Count()==history+3,"native paid root must own real Tools2/MachineLearning1/history3");
            NativeSetupHistoryCount=CombatManager.Instance.History.CardPlaysFinished.Count();
            Console.WriteLine($"PARTY ROOT hp={string.Join(',',Party.Select(player=>player.Creature.CurrentHp))} energy={owner.PlayerCombatState.Energy} tools={owner.Creature.GetPower<ToolsOfTheTradePower>()!.Amount} historyDelta=3");
        }
        public void Dispose(){managers.Dispose();native.Dispose();}
    }
    private sealed class ResumeObserver:IDisposable
    {
        internal static int Calls;internal static bool Stopped;internal static string Last="";
        private static int maximumResumeObservations;
        private readonly Harmony harmony=new("coopbots.party-pending-observer");
        internal ResumeObserver(int maximum=8)
        {
            // R2 warmed-small-observer-red exited 1: PARTY RISK simulated=False resumes=0 stopped=False.
            // The tiny wrapper was already inlined; warmed-large-observer-green restores resumes=1 at the execution body.
            Check(maximum>0,"observer safety limit must be finite and positive");
            Calls=0;Stopped=false;Last="";maximumResumeObservations=maximum;
            harmony.Patch(AccessTools.Method(typeof(CombatPredictionSimulator),"ResumeExecutionContinuation"),
                prefix:new HarmonyMethod(typeof(ResumeObserver),nameof(Before)));
        }
        private static void Before(CombatPredictionSimulator __instance)
        {
            Calls++;Last=((SimulatedCombatState)__instance.State.CombatState).PendingTurnStartChoice?.ToString()??"native continuation";
            if(Calls>=maximumResumeObservations){Stopped=true;throw new InvalidOperationException($"bounded test observation: resume limit {maximumResumeObservations} reached; test must fail");}
        }
        public void Dispose()=>harmony.UnpatchAll(harmony.Id);
    }
    private static KernelSession ContinuationObservation(KernelSession original,CombatPredictionSimulator fork)
    {
        var view=(KernelSession)typeof(object).GetMethod("MemberwiseClone",Members)!.Invoke(original,null)!;
        AccessTools.Field(typeof(KernelSession),"simulator").SetValue(view,fork);
        return view;
    }
    private static PredictionRngState[] RngStates(CombatPredictionSimulator sim)=>
        [sim.Rng.ShuffleState,sim.Rng.CombatCardGenerationState,sim.Rng.CombatPotionGenerationState,
         sim.Rng.CombatCardSelectionState,sim.Rng.CombatEnergyCostsState,sim.Rng.CombatTargetsState,
         sim.Rng.CombatOrbGenerationState,sim.Rng.MonsterAiState,sim.Rng.NicheState];
    private static void SameHistory(CombatPredictionSimulator original,CombatPredictionSimulator fork)
    {
        var left=original.History.Entries;var right=fork.History.Entries;
        Check(left.Count==right.Count,"resumed original/fork history lengths agree");
        for(var index=0;index<left.Count;index++)
        {
            var originalEntry=left[index];var forkEntry=right[index];
            Check(originalEntry.Index==forkEntry.Index&&originalEntry.GetType()==forkEntry.GetType(),$"resumed history order/type at {index}");
            var equal=(originalEntry,forkEntry) switch
            {
                (CombatPredictionCardDrawnEntry originalPayload,CombatPredictionCardDrawnEntry forkPayload)=>
                    originalPayload.Card==forkPayload.Card&&originalPayload.FromHandDraw==forkPayload.FromHandDraw,
                (CombatPredictionCardDrawResolvedEntry originalPayload,CombatPredictionCardDrawResolvedEntry forkPayload)=>
                    originalPayload.Card==forkPayload.Card&&originalPayload.OriginalEntry.Index==forkPayload.OriginalEntry.Index
                    &&originalPayload.OriginalEntry.Card==forkPayload.OriginalEntry.Card
                    &&originalPayload.OriginalEntry.FromHandDraw==forkPayload.OriginalEntry.FromHandDraw,
                (CombatPredictionCardsSelectedEntry originalPayload,CombatPredictionCardsSelectedEntry forkPayload)=>originalPayload.Cards.SequenceEqual(forkPayload.Cards),
                _=>throw new InvalidOperationException($"Party pending: unreviewed native fixture history kind {originalEntry.GetType().FullName} at {index}")
            };
            Check(equal,$"resumed original/fork full history payload at {index} ({originalEntry.GetType().Name})");
        }
    }
    private static IDisposable Scope(string name,CombatState combat)=>(IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios),name),Members,null,[combat],null)!;
    private static void Check(bool yes,string text){if(!yes)throw new InvalidOperationException("Party pending: "+text);}
    private sealed class Presentation:IDisposable
    {
        private readonly Harmony harmony=new("coopbots.party-pending.presentation");
        internal Presentation()=>harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),prefix:new HarmonyMethod(typeof(Presentation),nameof(Format)));
        private static bool Format(ref string __result){__result="";return false;}
        public void Dispose()=>harmony.UnpatchAll(harmony.Id);
    }
}
