using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class RoomWorkScenarios
{
    // R2 2026-10-01: nine production faults exit 1, restored exit 0.
    // Commands/raw output: work/prior15-live-stall/r2/results.json and *.log.
    // "Room work: unfinished camp must block map votes"
    // "Room work: unsettled shop must block map votes"
    // "Room work: unfinished event must block map votes"
    // "Room work: finished event with pending effects must block departure"
    // "Room work: camp entry must hold a lease before the first await"
    // "Room work: construction must register its pending room"
    // "Room work: departure entry must call shared barrier: BotRuntime"
    // "Room work: departure entry must call shared barrier: BotRoomProceedDriver"
    // "Room work: departure entry must call shared barrier: BotEventProceedDriver"
    // R2 r2-shop/shop-identity.log, exit 1: "Room work: old shop failure must not release a new shop".
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Room work: " + message); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        foreach(var method in new[]{AccessTools.Method(typeof(BotRuntime),"TryVoteOnMap"),
            AccessTools.Method(typeof(BotRoomProceedDriver),"TryProceed"),AccessTools.Method(typeof(BotEventProceedDriver),"TryProceed")})
            Check(PatchProcessor.GetOriginalInstructions(method).Any(i=>i.operand is MethodInfo called
                && called.DeclaringType==typeof(RoomWork) && called.Name==nameof(RoomWork.Waiting)),
                "departure entry must call shared barrier: "+method.DeclaringType!.Name);
        using var pump = new ConstructionParallelScenarios.Pump();
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 95, 0));
        var state = RunState.CreateForTest(new[]{p}, seed:"ROOM-WORK");
        var manager = (RunManager)RuntimeHelpers.GetUninitializedObject(typeof(RunManager));
        var net = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        using var camp = new RestSiteSynchronizer(new RunLocationTargetedMessageBuffer(net),net,state,p.NetId,null);
        typeof(RunManager).GetProperty("RestSiteSynchronizer")!.SetValue(manager,camp);
        var room = new RestSiteRoom(); state.PushRoom(room); camp.BeginRestSite();
        Check(RoomWork.Waiting(manager,state)=="camp choices pending", "unfinished camp must block map votes");
        var options=(System.Collections.IList)camp.GetOptionsForPlayer(p); options.Clear();
        var entry=new TaskCompletionSource(); var operation=new TaskCompletionSource();
        var tracked=BotRestSitePatch.CompleteTracked(state,entry.Task,()=>operation.Task);
        Check(RoomWork.Pending(room),"camp entry must hold a lease before the first await");
        pump.Run(async()=>{entry.SetResult();await Task.Yield();return true;});
        Check(RoomWork.Waiting(manager,state)!=null && !tracked.IsCompleted,"camp operation gap must block departure after options clear");
        pump.Run(async()=>{operation.SetResult();await tracked;return true;});
        Check(RoomWork.Waiting(manager,state)==null,"completed camp must release departure");
        var first=RoomWork.Begin(room);var second=RoomWork.Begin(room);first.Dispose();first.Dispose();
        Check(RoomWork.Pending(room),"nested and repeated disposal must preserve remaining work");second.Dispose();
        Check(!RoomWork.Pending(room),"last lease must release");
        p.MaxEnergy=3;
        for(var i=0;i<5;i++)p.Deck.AddInternal(ConstructionParallelScenarios.Card("STRIKE_IRONCLAD",p));
        var asyncTask=ConstructionDecision.RunAsync(p,()=>DeckStrength.Evaluate(p.Deck.Cards,p),()=>"room-work");
        Check(!asyncTask.IsCompleted && RoomWork.Pending(room),"construction must register its pending room");
        pump.Run(async()=>{await asyncTask;return true;});
        Check(!RoomWork.Pending(room),"construction completion must release its room");
        using(var cancellation=new CancellationTokenSource())
        {
            var cancelled=ConstructionDecision.RunAsync(p,()=>DeckStrength.Evaluate(p.Deck.Cards,p),()=>"cancel",cancellation.Token);
            cancellation.Cancel();
            try { pump.Run(async()=>{await cancelled;return true;});throw new Exception("Room work: expected cancellation"); }
            catch(OperationCanceledException) { }
            Check(!RoomWork.Pending(room),"cancelled construction must release its room");
        }
        state.PopCurrentRoom();
        using(var stale=RoomWork.Begin(room))
        {
            var shop=new MerchantRoom();state.PushRoom(shop);
            Check(!RoomWork.Pending(shop),"old rooms must not block new rooms");
            Check(RoomWork.Waiting(manager,state)=="shop transactions pending","unsettled shop must block map votes");
            var faultField=AccessTools.Field(typeof(BotShopDriver),"faulted");
            var shopField=AccessTools.Field(typeof(BotShopDriver),"room");
            var oldFault=faultField.GetValue(null);var oldShop=shopField.GetValue(null);
            try
            {
                faultField.SetValue(null,true);shopField.SetValue(null,new MerchantRoom());
                Check(RoomWork.Waiting(manager,state)!=null,"old shop failure must not release a new shop");
                shopField.SetValue(null,shop);
                Check(RoomWork.Waiting(manager,state)==null,"stopped current shop must remain escapable");
            }
            finally { faultField.SetValue(null,oldFault);shopField.SetValue(null,oldShop); }
            state.PopCurrentRoom();
        }
        using var events = new EventSynchronizer(new RunLocationTargetedMessageBuffer(net),net,state,state,p.NetId,42);
        typeof(RunManager).GetProperty("EventSynchronizer")!.SetValue(manager,events);
        var canonical=ModelDb.Event<BattlewornDummy>();
        var model=canonical.ToMutable();
        AccessTools.Field(typeof(EventSynchronizer),"_canonicalEvent").SetValue(events,canonical);
        ((List<EventModel>)AccessTools.Field(typeof(EventSynchronizer),"_events").GetValue(events)!).Add(model);
        state.PushRoom(new EventRoom(canonical));
        Check(RoomWork.Waiting(manager,state)=="event choices/effects pending","unfinished event must block map votes");
        typeof(EventModel).GetProperty("IsFinished")!.SetValue(model,true);
        var effect=new TaskCompletionSource();
        ((List<Task>)AccessTools.Field(typeof(EventSynchronizer),"_pendingOptionTasks").GetValue(events)!).Add(effect.Task);
        Check(RoomWork.Waiting(manager,state)=="event choices/effects pending","finished event with pending effects must block departure");
        effect.SetResult();
        Check(RoomWork.Waiting(manager,state)==null,"finished event effects must release departure");
        state.PopCurrentRoom();
        Console.WriteLine("PASS: room departure waits for camp entry/operations, shop settlement, event effects and concurrent construction; old rooms release independently.");
    }
}
