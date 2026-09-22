using CoopBots;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
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

        // Rich purse (>= 500): reaching a shop becomes the route's first job, even when
        // the shop is far and a near elite is the better immediate room.
        // CHECKED (R2) 2026-09-22: removing the RichGoldThreshold branch turns this red,
        // verbatim:
        //   System.Exception: A 500+ gold shop must be worth far more than a spent purse's
        //   shop (40.0 vs 40.0).
        if (RoutePlanner.GoldTier(500) != 3 || RoutePlanner.RepresentativeGold(3) < 500)
            throw new Exception("500 gold must have its own tier so a shop can spend it.");
        var richShop = RoutePlanner.ValueForNode(MapPointType.Shop, 2, 3, 0, 20, .5, 1);
        var spentShop = RoutePlanner.ValueForNode(MapPointType.Shop, 2, 2, 0, 20, .5, 1);
        if (!(richShop > spentShop + 300))
            throw new Exception($"A 500+ gold shop must be worth far more than a spent purse's shop "
                + $"({richShop:F1} vs {spentShop:F1}).");
        var richRouteStart = new MapPoint(0, 0) { PointType = MapPointType.Monster };
        var nearElite = new MapPoint(0, 1) { PointType = MapPointType.Elite };
        var far1 = new MapPoint(1, 1) { PointType = MapPointType.Monster };
        var cursor = far1;
        for (var row = 2; row <= 5; row++)
        {
            var next = new MapPoint(1, row) { PointType = MapPointType.Monster };
            cursor.AddChildPoint(next);
            cursor = next;
        }
        var farShop = new MapPoint(1, 6) { PointType = MapPointType.Shop };
        cursor.AddChildPoint(farShop);
        var purseBoss = new MapPoint(0, 7) { PointType = MapPointType.Boss };
        richRouteStart.AddChildPoint(nearElite);
        richRouteStart.AddChildPoint(far1);
        nearElite.AddChildPoint(purseBoss);
        farShop.AddChildPoint(purseBoss);
        double RichRouteValue(MapPointType type, int hp, int gold) =>
            RoutePlanner.ValueForNode(type, hp, gold, 0, 20, .5, 1);
        var richRoute = RoutePlanner.Plan(richRouteStart, purseBoss, RichRouteValue,
            healthTier: 2, goldTier: RoutePlanner.GoldTier(600));
        if (richRoute is null || !richRoute.Path.Any(point => point.PointType == MapPointType.Shop))
            throw new Exception("500+ gold must route to a far shop instead of a near elite "
                + $"(path={string.Join(">", richRoute?.Path.Select(p => p.PointType) ?? Enumerable.Empty<MapPointType>())}).");
        Console.WriteLine("PASS: 500+ gold routes to a far shop instead of taking a better near room.");

        // Elites scale with the act and demand a finished deck: a healthy Act 2
        // elite with an unfinished deck must be worth far less than an Act 1 one.
        var strongAct1 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 0, 20, .8);
        var weakAct2 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 1, 20, .3);
        if (!(weakAct2 < strongAct1 - 20))
            throw new Exception($"Act 2 elites with an unfinished deck must be discounted: act1={strongAct1:F1}, act2={weakAct2:F1}");
        if (!(RoutePlanner.NodeValue(MapPointType.Elite, .5, 120, 0, 20, .9) < -40))
            throw new Exception("An elite at low health must stay strongly negative regardless of act.");
        Console.WriteLine("PASS: elite value falls with later acts and an unfinished deck.");

        // ALL-BOT ROUTE: a healthy team must proactively take the elite in acts 1/2,
        // where the relic/gold/card is what the boss check needs. Act 3 stays cautious
        // until the deck is actually mature.
        // CHECKED (R2) 2026-09-23: zeroing the healthy/team bonuses and restoring the old
        // act bases turns this red, verbatim:
        //   System.Exception: A healthy all-bot Act 1 must prefer the elite over a safe
        //   unknown (19.0 vs 22.0).
        var teamEliteAct1 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 0, 20, .5, teamRoute: true);
        var safeUnknownAct1 = RoutePlanner.NodeValue(MapPointType.Unknown, .9, 120, 0, 20, .5);
        if (!(teamEliteAct1 > safeUnknownAct1))
            throw new Exception($"A healthy all-bot Act 1 must prefer the elite over a safe unknown "
                + $"({teamEliteAct1:F1} vs {safeUnknownAct1:F1}).");
        var teamEliteAct2 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 1, 20, .5, teamRoute: true);
        var safeUnknownAct2 = RoutePlanner.NodeValue(MapPointType.Unknown, .9, 120, 1, 20, .5);
        if (!(teamEliteAct2 > safeUnknownAct2))
            throw new Exception($"A healthy all-bot Act 2 must prefer the elite over a safe unknown "
                + $"({teamEliteAct2:F1} vs {safeUnknownAct2:F1}).");
        var teamEliteAct3 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 2, 20, .5, teamRoute: true);
        var safeUnknownAct3 = RoutePlanner.NodeValue(MapPointType.Unknown, .9, 120, 2, 20, .5);
        if (!(teamEliteAct3 < safeUnknownAct3))
            throw new Exception($"Act 3 with an unfinished deck must stay cautious about elites "
                + $"({teamEliteAct3:F1} vs {safeUnknownAct3:F1}).");
        var matureEliteAct3 = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 2, 20, .8, teamRoute: true);
        if (!(matureEliteAct3 > safeUnknownAct3))
            throw new Exception($"A genuinely mature all-bot Act 3 deck may take the elite "
                + $"({matureEliteAct3:F1} vs {safeUnknownAct3:F1}).");
        Console.WriteLine("PASS: a healthy all-bot party proactively takes act-1/2 elites and "
            + "only takes act-3 elites with a mature deck.");

        // RELIC-AWARE ROOM VALUE. The scan's three batches must move the right room type, and
        // the no-relic profile must leave the old numbers untouched.
        var noRelics = RouteRelicProfile.None;
        var lowUnknown = RoutePlanner.NodeValue(MapPointType.Unknown, .3, 120, 2, 20, .5);
        var lowUnknownNoRelics = RoutePlanner.NodeValue(MapPointType.Unknown, .3, 120, 2, 20, .5, relics: noRelics);
        if (Math.Abs(lowUnknown - lowUnknownNoRelics) > 1e-9)
            throw new Exception("an empty relic profile must not change route value.");
        var juzu = RouteRelicProfile.ForTest(4, "JuzuBracelet");
        var lowUnknownJuzu = RoutePlanner.NodeValue(MapPointType.Unknown, .3, 120, 2, 20, .5, relics: juzu);
        if (!(lowUnknownJuzu > lowUnknown + 10))
            throw new Exception("Juzu Bracelet must make a low-health ? room safer; the shared-room "
                + $"relief is missing ({lowUnknown:F1} -> {lowUnknownJuzu:F1}).");
        Console.WriteLine("PASS: Juzu Bracelet raises shared ? room value, especially at low health.");

        var tent = RouteRelicProfile.ForTest(4, "MiniatureTent");
        var restBase = RoutePlanner.NodeValue(MapPointType.RestSite, .8, 120, 1, 20, .5);
        var restTent = RoutePlanner.NodeValue(MapPointType.RestSite, .8, 120, 1, 20, .5, relics: tent);
        if (!(restTent > restBase + 10))
            throw new Exception("Miniature Tent must raise the shared Rest Site value "
                + $"({restBase:F1} -> {restTent:F1}).");
        var feather = RouteRelicProfile.ForTest(4, "EternalFeather");
        var restFeather = RoutePlanner.NodeValue(MapPointType.RestSite, .8, 120, 1, 20, .5, relics: feather);
        if (!(restFeather > restBase && restFeather < restTent))
            throw new Exception("Eternal Feather is a self-only Rest Site bonus and must be smaller "
                + $"than the shared Miniature Tent bonus ({restBase:F1}/{restFeather:F1}/{restTent:F1}).");
        Console.WriteLine("PASS: shared Miniature Tent outranks the self-only Eternal Feather at a Rest Site.");

        var eliteBase = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 1, 20, .6);
        var stars = RouteRelicProfile.ForTest(4, "BlackStar", "WhiteStar");
        var eliteStars = RoutePlanner.NodeValue(MapPointType.Elite, .9, 120, 1, 20, .6, relics: stars);
        if (!(eliteStars > eliteBase + 15))
            throw new Exception("Black Star + White Star must raise elite value only after the team "
                + $"can still take the fight ({eliteBase:F1} -> {eliteStars:F1}).");
        var hurtStars = RoutePlanner.NodeValue(MapPointType.Elite, .5, 120, 1, 20, .6, relics: stars);
        if (hurtStars >= 0)
            throw new Exception("reward relics must not override the low-health elite refusal.");
        Console.WriteLine("PASS: elite reward relics raise elite value without overriding the survival gate.");

        var shopBase = RoutePlanner.ValueForNode(MapPointType.Shop, 2, 1, 1, 20, .5, 1);
        var discount = RouteRelicProfile.ForTest(4, "MembershipCard", "TheCourier");
        var shopDiscount = RoutePlanner.ValueForNode(MapPointType.Shop, 2, 1, 1, 20, .5, 1, relics: discount);
        if (!(shopDiscount > shopBase))
            throw new Exception("Membership Card + The Courier must raise a funded shop "
                + $"({shopBase:F1} -> {shopDiscount:F1}).");
        var drains = RouteRelicProfile.ForTest(4, "MawBank", "Ectoplasm", "Sozu", "SealOfGold");
        var shopDrained = RoutePlanner.ValueForNode(MapPointType.Shop, 2, 1, 1, 20, .5, 1, relics: drains);
        if (!(shopDrained < shopBase))
            throw new Exception("Maw Bank / Ectoplasm / Sozu / Seal of Gold must lower shop value "
                + $"({shopBase:F1} -> {shopDrained:F1}).");
        Console.WriteLine("PASS: funded-shop and drain relics move shop value in opposite directions.");

        var treasureBase = RoutePlanner.NodeValue(MapPointType.Treasure, .9, 120, 1, 20, .5);
        var crucible = RouteRelicProfile.ForTest(4, "SilverCrucible");
        var treasureCrucible = RoutePlanner.NodeValue(MapPointType.Treasure, .9, 120, 1, 20, .5, relics: crucible);
        if (!(treasureCrucible < treasureBase))
            throw new Exception("Silver Crucible empties the first chest and must lower treasure value.");
        Console.WriteLine("PASS: conditional chest/reward relics lower the room they tax.");

        var relicSeat = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        relicSeat.AddRelicInternal(ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.JuzuBracelet>().ToMutable());
        relicSeat.AddRelicInternal(ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.EternalFeather>().ToMutable());
        var fromSeats = RouteRelicProfile.FromPlayers(new[] { relicSeat });
        if (!fromSeats.Any("JuzuBracelet") || fromSeats.HolderCount("EternalFeather") != 1)
            throw new Exception("RouteRelicProfile.FromPlayers must count living holders by type.");
        Console.WriteLine("PASS: route relic profile reads the living seats' relic counts.");

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

        // A room the GAME cannot build must never be a route candidate.
        // Vanilla `RunManager.CreateRoom` builds a treasure room with
        // `new TreasureRoom(State.CurrentActIndex)` and does NOT range-check the act, while
        // `TreasureRoom(int actIndex)` throws unless 0 <= actIndex <= 2 (TreasureRoom.cs:25).
        // `Glory` is the fourth act — index 3 — so a treasure node on its map throws
        // ArgumentOutOfRangeException the moment it is entered, down the game's own click
        // chain (NMapScreen.TravelToMapCoord <- MoveToMapCoordAction), leaving the run at
        // room=null roomStack=0 with no way forward.
        // Measured live 2026-09-20: it ended a 25-fight unattended run on floor 54.
        // CHECKED (R2): making IsBuildable() always true turns this red with
        //   System.Exception: act 4 (index 3) treasure must be excluded: vanilla
        //   TreasureRoom throws for actIndex > 2.
        if (RoutePlanner.IsBuildable(MapPointType.Treasure, 3))
            throw new Exception("act 4 (index 3) treasure must be excluded: vanilla TreasureRoom throws for actIndex > 2.");
        if (!RoutePlanner.IsBuildable(MapPointType.Treasure, 0) || !RoutePlanner.IsBuildable(MapPointType.Treasure, 1)
            || !RoutePlanner.IsBuildable(MapPointType.Treasure, 2))
            throw new Exception("treasure in acts 0-2 is buildable and must stay selectable; the exclusion has to stay narrow.");
        foreach (var type in new[] { MapPointType.Monster, MapPointType.Elite, MapPointType.Boss, MapPointType.Shop,
            MapPointType.RestSite, MapPointType.Unknown, MapPointType.Ancient })
            if (!RoutePlanner.IsBuildable(type, 3))
                throw new Exception($"{type} in act 4 must stay selectable; only treasure is unbuildable there.");
        if (RoutePlanner.UnbuildableReason(MapPointType.Treasure, 3).Length == 0)
            throw new Exception("an unbuildable room must carry a reason — every skip is logged verbatim.");
        Console.WriteLine("PASS: only act-4 treasure is refused as unbuildable, and it carries the reason that gets logged.");

        // The exclusion rides on the DP's existing reading of a non-finite value as
        // UNREACHABLE (it skips such children). Pin that reading, not just the predicate: if
        // the DP ever started treating non-finite as merely a bad score, the act-4 treasure
        // exclusion would silently stop excluding anything.
        var unreachableStart = new MapPoint(0, 0) { PointType = MapPointType.Monster };
        var notBuildable = new MapPoint(0, 1) { PointType = MapPointType.Treasure };
        var buildable = new MapPoint(1, 1) { PointType = MapPointType.RestSite };
        var unreachableBoss = new MapPoint(0, 2) { PointType = MapPointType.Boss };
        unreachableStart.AddChildPoint(notBuildable);
        unreachableStart.AddChildPoint(buildable);
        notBuildable.AddChildPoint(unreachableBoss);
        buildable.AddChildPoint(unreachableBoss);
        double Unreachable(MapPoint point) => point.PointType switch
        {
            // exactly what Plan(state, human) returns for a room IsBuildable() rejects
            MapPointType.Treasure => RoutePlanner.UnreachableValue,
            MapPointType.RestSite => 1,
            _ => 0,
        };
        var avoided = RoutePlanner.Plan(unreachableStart, unreachableBoss, Unreachable);
        if (avoided is null || avoided.Path.Count != 3 || avoided.Path[1] != buildable)
            throw new Exception("a room the game cannot build has to be DETOURED around when another branch exists.");
        // The discriminating half. If the sentinel is ever swapped for a finite "very bad"
        // number, the detour above still passes (the other branch is simply better) while
        // THIS one routes straight into the unbuildable room — which is the failure that
        // would put a live run back into the act-4 crash.
        // CHECKED (R2): RoutePlanner.UnreachableValue = -1000.0 turns this red with
        //   System.Exception: the only route to the boss runs through a room the game
        //   cannot build, so there is no route — got a 3-node path instead.
        var trapStart = new MapPoint(0, 0) { PointType = MapPointType.Monster };
        var trap = new MapPoint(0, 1) { PointType = MapPointType.Treasure };
        var trapBoss = new MapPoint(0, 2) { PointType = MapPointType.Boss };
        trapStart.AddChildPoint(trap);
        trap.AddChildPoint(trapBoss);
        var trapped = RoutePlanner.Plan(trapStart, trapBoss, Unreachable);
        if (trapped is not null)
            throw new Exception("the only route to the boss runs through a room the game cannot build, so there is no route — "
                + $"got a {trapped.Path.Count}-node path instead. A finite \"very bad\" sentinel would route straight into it.");
        Console.WriteLine("PASS: unbuildable rooms are detoured around, and a map whose only path leads through one has no route.");
    }
}
