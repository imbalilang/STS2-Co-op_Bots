using CoopBots;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Full-act route search: the planner must look past the next room, not just
// take the highest immediate node.
internal static class RouteScenarios
{
    internal static void Run()
    {
        var start = new MapPoint(0, 0) { PointType = MapPointType.Monster };
        var elite = new MapPoint(0, 1) { PointType = MapPointType.Elite };
        var rest = new MapPoint(1, 1) { PointType = MapPointType.RestSite };
        var boss = new MapPoint(0, 2) { PointType = MapPointType.Boss };
        start.AddChildPoint(elite);
        start.AddChildPoint(rest);
        elite.AddChildPoint(boss);
        rest.AddChildPoint(boss);

        double Choice(MapPoint point) => point.PointType switch
        {
            MapPointType.Elite => 100,
            MapPointType.RestSite => 1,
            _ => 0,
        };
        var pick = RoutePlanner.Plan(start, boss, Choice);
        if (pick is null || pick.Path.Count != 3 || pick.Path[1] != elite || pick.Path[2] != boss)
            throw new Exception("Route planner must pick the higher-value branch and end at the boss.");
        Console.WriteLine("PASS: route planner chooses the highest-value branch to the boss.");

        // Lookahead: a modest room now that leads to a great one must beat a
        // better room now that dead-ends.
        var richStart = new MapPoint(0, 0) { PointType = MapPointType.Monster };
        var modestNow = new MapPoint(0, 1) { PointType = MapPointType.RestSite };
        var greatLater = new MapPoint(0, 2) { PointType = MapPointType.Elite };
        var goodNow = new MapPoint(1, 1) { PointType = MapPointType.Elite };
        var richBoss = new MapPoint(0, 3) { PointType = MapPointType.Boss };
        richStart.AddChildPoint(modestNow);
        richStart.AddChildPoint(goodNow);
        modestNow.AddChildPoint(greatLater);
        greatLater.AddChildPoint(richBoss);
        goodNow.AddChildPoint(richBoss);
        double Rich(MapPoint point) => point.PointType switch
        {
            MapPointType.RestSite => 10,
            MapPointType.Elite => 50,
            _ => 0,
        };
        var lookahead = RoutePlanner.Plan(richStart, richBoss, Rich);
        if (lookahead is null || lookahead.Path[1] != modestNow)
            throw new Exception("Route planner must prefer a modest room that enables a much better follow-up.");
        Console.WriteLine("PASS: route planner looks past the next room (discounted lookahead).");

        if (RoutePlanner.Plan(boss, boss, Choice)?.Path.Count != 1)
            throw new Exception("Starting on the boss must be a single-point route.");
        Console.WriteLine("PASS: route planner handles the already-at-boss case.");

        // Act 1, healthy, deck still small: unknown rooms must outrank plain
        // fights, so 3 unknown + 2 fights beats 5 fights.
        if (!(RoutePlanner.NodeValue(MapPointType.Unknown, .9, 120, 0, 20)
            > RoutePlanner.NodeValue(MapPointType.Monster, .9, 120, 0, 20)))
            throw new Exception("Unknown rooms must be worth more than a plain fight in the opening act.");
        if (!(RoutePlanner.NodeValue(MapPointType.Monster, .3, 120, 0, 20)
            < RoutePlanner.NodeValue(MapPointType.Monster, .9, 120, 0, 20)))
            throw new Exception("A fight must be worth less when the team is low on health.");
        if (!(RoutePlanner.NodeValue(MapPointType.Unknown, .9, 120, 2, 20)
            >= RoutePlanner.NodeValue(MapPointType.Monster, .9, 120, 2, 20)))
            throw new Exception("Unknown rooms must not fall behind fights even in later acts.");

        var crossStart = new MapPoint(0, 0) { PointType = MapPointType.Monster };
        var crossBoss = new MapPoint(0, 6) { PointType = MapPointType.Boss };
        MapPoint Chain(int col, MapPointType type)
        {
            var first = new MapPoint(col, 1) { PointType = type };
            var previous = first;
            for (var row = 2; row <= 5; row++)
            {
                var next = new MapPoint(col, row) { PointType = type };
                previous.AddChildPoint(next);
                previous = next;
            }
            previous.AddChildPoint(crossBoss);
            return first;
        }
        var unknowns = new MapPoint(0, 1) { PointType = MapPointType.Unknown };
        var unknownTail = unknowns;
        for (var row = 2; row <= 3; row++)
        {
            var next = new MapPoint(0, row) { PointType = MapPointType.Unknown };
            unknownTail.AddChildPoint(next);
            unknownTail = next;
        }
        for (var row = 4; row <= 5; row++)
        {
            var next = new MapPoint(0, row) { PointType = MapPointType.Monster };
            unknownTail.AddChildPoint(next);
            unknownTail = next;
        }
        unknownTail.AddChildPoint(crossBoss);
        var fights = Chain(1, MapPointType.Monster);
        crossStart.AddChildPoint(unknowns);
        crossStart.AddChildPoint(fights);
        double Act1(MapPoint point) => RoutePlanner.NodeValue(point.PointType, .9, 120, 0, 20);
        var mixed = RoutePlanner.Plan(crossStart, crossBoss, Act1);
        if (mixed is null || mixed.Path[1] != unknowns)
            throw new Exception("3 unknown + 2 fights must beat a pure 5-fight route in Act 1.");
        Console.WriteLine("PASS: route planner prefers 3 unknown + 2 fights over 5 fights in Act 1.");

        // Resource state must propagate: the second shop is valued with the gold
        // left after the first, not with the starting purse.
        var shopStart = new MapPoint(0, 0) { PointType = MapPointType.Monster };
        var shopOne = new MapPoint(0, 1) { PointType = MapPointType.Shop };
        var shopTwo = new MapPoint(0, 2) { PointType = MapPointType.Shop };
        var shopBoss = new MapPoint(0, 3) { PointType = MapPointType.Boss };
        shopStart.AddChildPoint(shopOne);
        shopOne.AddChildPoint(shopTwo);
        shopTwo.AddChildPoint(shopBoss);
        double Purse(MapPointType type, int health, int gold) => type switch
        {
            MapPointType.Shop => gold switch { 0 => -10, 1 => 15, _ => 35 },
            _ => 0,
        };
        var shops = RoutePlanner.Plan(shopStart, shopBoss, Purse, healthTier: 2, goldTier: 2);
        var expected = 0.92 * 35 + 0.92 * 0.92 * 15;
        if (shops is null || Math.Abs(shops.Score - expected) > 0.05 || shops.Path.Count != 4)
            throw new Exception($"The second shop must be valued with the remaining purse: {shops?.Score:F2} vs {expected:F2}.");
        Console.WriteLine("PASS: route planner propagates gold state between rooms.");

        // Elites scale with the act and demand a finished deck: a healthy Act 2
        // elite with an unfinished deck must be worth far less than an Act 1 one.
        var strongAct1 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 0, 20, .8);
        var weakAct2 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 1, 20, .3);
        if (!(weakAct2 < strongAct1 - 20))
            throw new Exception($"Act 2 elites with an unfinished deck must be discounted: act1={strongAct1:F1}, act2={weakAct2:F1}");
        if (!(RoutePlanner.NodeValue(MapPointType.Elite, .5, 120, 0, 20, .9) < -40))
            throw new Exception("An elite at low health must stay strongly negative regardless of act.");
        Console.WriteLine("PASS: elite value falls with later acts and an unfinished deck.");

        // Deck maturity: many non-curse cards plus scaling powers must read far
        // higher than an empty deck.
        var deckBot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var maturityLow = RoutePlanner.DeckMaturity(deckBot);
        var runForCards = RunState.CreateForTest(new[] { deckBot }, seed: "MATURITY");
        for (var i = 0; i < 14; i++) deckBot.Deck.AddInternal(runForCards.CreateCard<StrikeIronclad>(deckBot));
        for (var i = 0; i < 3; i++) deckBot.Deck.AddInternal(runForCards.CreateCard<Inflame>(deckBot));
        var maturityHigh = RoutePlanner.DeckMaturity(deckBot);
        if (!(maturityHigh > maturityLow + 0.5))
            throw new Exception($"Deck maturity must react to deck size and powers: {maturityLow:F2} -> {maturityHigh:F2}");
        Console.WriteLine("PASS: deck maturity reacts to size and scaling powers.");
    }
}
