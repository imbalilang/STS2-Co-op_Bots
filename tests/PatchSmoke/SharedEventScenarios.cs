using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class SharedEventScenarios
{
    // R2 2026-09-27: restoring the old !isShared guard failed with
    // System.Exception: Shared dummy must choose only one encounter while the host animation is pending.
    // Blocking every later synchronizer page instead failed with
    // System.Exception: Fresh shared event options must remain answerable after the previous page.
    internal static void Run()
    {
        TestEnvironment.Ensure();
        using var pump = new ConstructionParallelScenarios.Pump();
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(
            UnlockState.all, i == 0 ? 1UL : BotRegistry.CreateId(BotDifficulty.Pro, 9, i))).ToArray();
        var run = RunState.CreateForTest(party, seed: "SHARED-EVENT-ANIMATION");
        var net = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        using var sync = new EventSynchronizer(new RunLocationTargetedMessageBuffer(net), net, run, run, 1, 42);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(EventSynchronizer).GetField("_canonicalEvent", flags)!.SetValue(sync, ModelDb.Event<BattlewornDummy>());
        var events = (List<EventModel>)typeof(EventSynchronizer).GetField("_events", flags)!.GetValue(sync)!;
        var votes = (List<uint?>)typeof(EventSynchronizer).GetField("_playerVotes", flags)!.GetValue(sync)!;
        var bodies = new int[4, 3];
        var animation = new TaskCompletionSource();
        for (var seat = 0; seat < party.Length; seat++)
        {
            var model = ModelDb.Event<BattlewornDummy>().ToMutable();
            var capturedSeat = seat;
            var options = Enumerable.Range(0, 3).Select(choice => new EventOption(model,
                () => { bodies[capturedSeat, choice]++; return Task.CompletedTask; },
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                $"BATTLEWORN_DUMMY.pages.INITIAL.options.SETTING_{choice + 1}", Array.Empty<IHoverTip>())).ToList();
            // The local UI awaits its confirmation animation; other seats have no UI.
            if (seat == 0) options[0].BeforeChosen += _ => animation.Task;
            typeof(EventModel).GetField("_currentOptions", flags)!.SetValue(model, options);
            events.Add(model); votes.Add(null);
        }
        AutoPilot.Set(1, true);
        BotEventDriver.Reset();
        try
        {
            BotEventDriver.TickEvents(sync, run);
            for (var tick = 0; tick < 12; tick++) BotEventDriver.TickEvents(sync, run);
            var page = (uint)typeof(EventSynchronizer).GetField("_pageIndex", flags)!.GetValue(sync)!;
            Check(page == 1 && Enumerable.Range(0, 4).All(s => bodies[s, 0] == 0 && bodies[s, 2] == 0),
                "Shared dummy must choose only one encounter (the fixed second tier) while the host animation is pending.");
            animation.SetResult();
            pump.Run(async () => { await sync.AwaitPendingOptionTasks(); return true; });
            Check(Enumerable.Range(0, 4).All(s => bodies[s, 1] == 1),
                "Every seat must execute the same chosen dummy setting (tier two) exactly once.");

            var nextPageAnswers = 0;
            foreach (var model in events)
            {
                var next = new EventOption(model, () => { nextPageAnswers++; return Task.CompletedTask; },
                    new LocString("events", "test.title"), new LocString("events", "test.description"),
                    "BATTLEWORN_DUMMY.pages.NEXT.options.CONTINUE", Array.Empty<IHoverTip>());
                typeof(EventModel).GetField("_currentOptions", flags)!.SetValue(model, new List<EventOption> { next });
            }
            BotEventDriver.TickEvents(sync, run);
            Check(nextPageAnswers == 4, "Fresh shared event options must remain answerable after the previous page.");
            Console.WriteLine("PASS: shared event native voting waits through animation, executes one setting per seat and advances fresh pages.");
        }
        finally { AutoPilot.Set(1, false); BotEventDriver.Reset(); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
