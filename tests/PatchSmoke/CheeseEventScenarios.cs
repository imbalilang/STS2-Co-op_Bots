using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class CheeseEventScenarios
{
    // R2 2026-09-27: 20 deliberate production faults each exited 1, then restored.
    // Commands/exits: work/strength-20260927-v8/r2-results.json. Failure text:
    // Cheese must not spend 14 HP from a 15-HP seat for distant growth.
    // Healthy early cheese choices must price remaining fights independently of option order.
    // Remaining combat estimates must include both final bosses, not alternative map branches.
    // Event relic rewards must be recognised when supplied only through hover tips.
    // The same direct and hover-tip relic must not be valued twice.
    internal static void Run()
    {
        new HarmonyLib.Harmony("coopbots.test.cheese.presentation").Patch(
            HarmonyLib.AccessTools.Method(typeof(LocString), "GetFormattedText"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(CheeseEventScenarios), nameof(FormatText)));
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var run = RunState.CreateForTest([p], seed: "CHEESE-HORIZON");
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        var model = ModelDb.Event<RoomFullOfCheese>().ToMutable();
        var choose = typeof(BotEventDriver).GetMethod("ChooseEventOption", BindingFlags.Static | BindingFlags.NonPublic)!;
        EventOption Option(string suffix) => new(model, () => Task.CompletedTask,
            new LocString("events", "test.title"), new LocString("events", "test.description"),
            "ROOM_FULL_OF_CHEESE.pages.INITIAL.options." + suffix,
            suffix == "SEARCH" ? HoverTipFactory.FromRelic<ChosenCheese>() : []);
        int Pick(bool reverse = false)
        {
            var options = new List<EventOption> { Option("GORGE"), Option("SEARCH") };
            if (reverse) options.Reverse();
            typeof(EventModel).GetField("_currentOptions", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, options);
            return (int)choose.Invoke(null, [p, model])!;
        }
        var healthy = Pick();
        if (healthy != 1 || Pick(true) != 0)
            throw new Exception("Healthy early cheese choices must price remaining fights independently of option order.");
        if (BotEventDriver.GrantedRelics(Option("SEARCH")).ToArray() is not [ChosenCheese])
            throw new Exception("Event relic rewards must be recognised when supplied only through hover tips.");
        if (BotEventDriver.GrantedRelics(Option("SEARCH").WithRelic(ModelDb.Relic<ChosenCheese>().ToMutable())).Count() != 1)
            throw new Exception("The same direct and hover-tip relic must not be valued twice.");
        var early = HumanCoopAdvisor.RelicValue(ModelDb.Relic<ChosenCheese>(), p).Score;
        p.Creature.SetCurrentHpInternal(15);
        if (Pick() != 0) throw new Exception("Cheese must not spend 14 HP from a 15-HP seat for distant growth.");
        p.Creature.SetCurrentHpInternal(80);
        run.CurrentActIndex = run.Acts.Count - 1;
        var map = new LastFights(); run.Map = map; run.AddVisitedMapCoord(map.StartingMapPoint.coord);
        if (RunDepth.RemainingCombats(p) != 2)
            throw new Exception("Remaining combat estimates must include both final bosses, not alternative map branches.");
        var late = HumanCoopAdvisor.RelicValue(ModelDb.Relic<ChosenCheese>(), p).Score;
        if (late >= early || Pick() != 0)
            throw new Exception("Late cheese must lose value when its life cost cannot repay before the bosses.");
        run.AddVisitedMapCoord(map.BossMapPoint.coord);
        if (RunDepth.RemainingCombats(p) != 1) throw new Exception("Completed bosses must not count toward cheese growth.");
        Console.WriteLine("PASS: cheese choices price remaining fights, upfront HP, alternative cards and hover-tip relics.");
    }
    private static bool FormatText(LocString __instance, ref string __result)
    { __result = __instance.LocEntryKey; return false; }

    private sealed class LastFights : ActMap
    {
        public override MapPoint StartingMapPoint { get; } = new(0, 0) { PointType = MapPointType.RestSite };
        public override MapPoint BossMapPoint { get; } = new(0, 1) { PointType = MapPointType.Boss };
        public override MapPoint SecondBossMapPoint { get; } = new(0, 2) { PointType = MapPointType.Boss };
        protected override MapPoint?[,] Grid { get; } = new MapPoint?[1, 1];
        internal LastFights() { StartingMapPoint.AddChildPoint(BossMapPoint); BossMapPoint.AddChildPoint(SecondBossMapPoint); }
    }
}
