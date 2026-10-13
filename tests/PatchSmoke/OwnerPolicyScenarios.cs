using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class OwnerPolicyScenarios
{
    // R2 2026-10-04: seven production mutations exited 1; restored run exited 0.
    // work/live-triage-20261004-noon/policy-r2.json holds commands and raw logs.
    // System.Exception: Owner policy: first act must route through an elite even at low health.
    // System.Exception: Owner policy: a completed elite must release the first-act obligation.
    // System.Exception: Owner policy: an old save beyond all elites must still reach the boss.
    // System.Exception: Owner policy: unmodelled relic needs a positive prior without claiming mechanism coverage.
    // System.Exception: Owner policy: 445 gold must buy a beneficial 150-gold removal valued at 100.
    // System.Exception: Owner policy: projected cash must restore opportunity cost after spending.
    // System.Exception: Owner policy: positive prior must not override native affordability.
    // R2 modeled-zero branch removed, exit 1; restored exit 0 (policy-red-known-zero.log):
    // System.Exception: Owner policy: a modeled relic with no legal upgrade targets must stay zero and priced.
    private static bool Removal(ref double __result) { __result = 100 / BotShopPlanner.GoldPerDeckValue; return false; }
    internal static void Run()
    {
        var p = ConstructionParallelScenarios.Fixture();
        var run = (RunState)p.RunState;
        var map = new EliteMap(); run.Map = map;
        p.Creature.SetCurrentHpInternal(1);
        var route = RoutePlanner.Plan(run, p, true);
        if (route is null || !route.Path.Contains(map.Elite))
            throw new Exception("Owner policy: first act must route through an elite even at low health.");
        run.AppendToMapPointHistory(MapPointType.Elite, RoomType.Elite, null);
        route = RoutePlanner.Plan(run, p, true);
        if (route is null || route.Path.Contains(map.Elite))
            throw new Exception("Owner policy: a completed elite must release the first-act obligation.");
        var orphan = new MapPoint(3, 0) { PointType = MapPointType.Unknown };
        orphan.AddChildPoint(map.BossMapPoint);
        if (RoutePlanner.Plan(orphan, map.BossMapPoint, (_, _, _) => 0, 0, 0, requireElite: true)?.Path.Count != 2)
            throw new Exception("Owner policy: an old save beyond all elites must still reach the boss.");
        Console.WriteLine("PASS: first-act elite obligation survives low health and releases after an elite or beyond all remaining elites.");

        foreach (var card in p.Deck.Cards.Where(c => c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack).ToArray())
            p.Deck.RemoveInternal(card);
        if (HumanCoopAdvisor.RelicValue(ModelDb.Relic<Whetstone>(), p).Score != 0
            || !HumanCoopAdvisor.RelicPriced(ModelDb.Relic<Whetstone>(), p))
            throw new Exception("Owner policy: a modeled relic with no legal upgrade targets must stay zero and priced.");
        var unpriced = ModelDb.Relic<Planisphere>();
        if (HumanCoopAdvisor.RelicValue(unpriced, p).Score <= 0 || HumanCoopAdvisor.RelicPriced(unpriced, p))
            throw new Exception("Owner policy: unmodelled relic needs a positive prior without claiming mechanism coverage.");
        var stock = new MerchantInventory(p);
        var removal = new MerchantCardRemovalEntry(p); ShopRemovalAnchorScenarios.Cost(removal, 150);
        typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(stock, removal);
        var harmony = new Harmony("coopbots.test.owner-shop-policy");
        harmony.Patch(AccessTools.Method(typeof(BotShopPlanner), nameof(BotShopPlanner.RemovalValue)),
            prefix: new HarmonyMethod(typeof(OwnerPolicyScenarios), nameof(Removal)));
        try
        {
            p.Gold = 445;
            if (BotShopPlanner.Choose(stock)?.RemovalIndex is not >= 0)
                throw new Exception("Owner policy: 445 gold must buy a beneficial 150-gold removal valued at 100.");
            p.Gold = 150;
            if (BotShopPlanner.Choose(stock) is not null)
                throw new Exception("Owner policy: projected cash must restore opportunity cost after spending.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        stock = new MerchantInventory(p); p.Gold = 445;
        var relic = (MerchantRelicEntry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MerchantRelicEntry));
        AccessTools.Field(typeof(MerchantEntry), "_player").SetValue(relic, p);
        typeof(MerchantRelicEntry).GetProperty("Model")!.SetValue(relic, unpriced.ToMutable());
        ShopRemovalAnchorScenarios.Cost(relic, 197); stock.AddRelicEntry(relic);
        if (BotShopPlanner.Choose(stock) is null)
            throw new Exception("Owner policy: spare cash must purchase an affordable unmodelled relic.");
        ShopRemovalAnchorScenarios.Cost(relic, 50); p.Gold = 49;
        if (BotShopPlanner.Choose(stock) is not null)
            throw new Exception("Owner policy: positive prior must not override native affordability.");
        Console.WriteLine("PASS: positive unmodelled relic prior and diminishing cash opportunity cost enable real purchases.");

        // Owner policy 2026-10-04: outside the third-layer last shop, only
        // Fruit Juice and Fairy in a Bottle may be bought. The truth table is
        // pinned first; then the real Choose path proves a banned bottle with an
        // open slot and enough gold is not offered at all.
        if (BotShopPlanner.PotionPurchaseAllowed("BLOCK_POTION", false)
            || !BotShopPlanner.PotionPurchaseAllowed(BotShopPlanner.FruitJuiceId, false)
            || !BotShopPlanner.PotionPurchaseAllowed(BotShopPlanner.FairyInABottleId, false)
            || !BotShopPlanner.PotionPurchaseAllowed("BLOCK_POTION", true))
            throw new Exception("Owner policy: potion whitelist must be Fruit Juice/Fairy everywhere plus any bottle in the final-act last shop.");
        var potionBot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var potionRun = RunState.CreateForTest(new[] { potionBot }, seed: "OWNER-POTION-POLICY");
        _ = new MegaCrit.Sts2.Core.Combat.CombatState(runState: potionRun);
        var potionRoom = new MerchantRoom(); potionRun.PushRoom(potionRoom);
        var potionInventory = new MerchantInventory(potionBot); potionRoom.Inventories.Add(potionInventory);
        potionBot.Gold = 300;
        potionBot.Creature.SetMaxHpInternal(80); potionBot.Creature.SetCurrentHpInternal(20);
        MerchantPotionEntry PotionEntry(PotionModel model)
        {
            var item = (MerchantPotionEntry)RuntimeHelpers.GetUninitializedObject(typeof(MerchantPotionEntry));
            typeof(MerchantEntry).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, potionBot);
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, 30);
            typeof(MerchantPotionEntry).GetProperty("Model")!.SetValue(item, model.ToMutable());
            return item;
        }
        var bannedPotion = PotionEntry(ModelDb.Potion<BlockPotion>());
        ((List<MerchantPotionEntry>)potionInventory.PotionEntries).Add(bannedPotion);
        if (BotShopPlanner.Choose(potionInventory) is not null)
            throw new Exception("Owner policy: a shop must not buy a Block Potion outside the final-act last-shop exception.");
        var allowedPotion = PotionEntry(ModelDb.Potion<FruitJuice>());
        ((List<MerchantPotionEntry>)potionInventory.PotionEntries).Add(allowedPotion);
        var potionStock = potionInventory.AllEntries.ToList();
        var potionChoice = BotShopPlanner.Choose(potionInventory);
        if (potionChoice is null || BotShopPlanner.Key(potionStock[potionChoice.Index]) != BotShopPlanner.FruitJuiceId)
            throw new Exception($"Owner policy: with a banned bottle on the shelf, the shop must still pick the allowed Fruit Juice; got "
                + $"{potionChoice?.Index.ToString() ?? "save"}.");
        Console.WriteLine("PASS: shop potions are restricted to Fruit Juice/Fairy outside the final-act last shop.");

        // Owner policy 2026-10-04: an act-1 boss rare menu cannot be skipped.
        // THE_GAMBIT is an all-rare, acquisition-vetoed offer: the unforced
        // scorer skips it, so the forced boss-menu path is the only way this
        // assertion can pass. The live 2026-10-04 boss menu is the same shape
        // (all Rare), even though its individual scores depend on the live map.
        var bossBot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bossRun = RunState.CreateForTest(new[] { bossBot }, seed: "OWNER-BOSS-RARE");
        var bossOffers = new CardModel[] { bossRun.CreateCard<TheGambit>(bossBot) };
        if (bossOffers[0].Rarity != CardRarity.Rare)
            throw new Exception("Owner policy: the boss-rare fixture needs an all-rare offer.");
        if (BuildValue.BestReward(bossBot, bossOffers, allowSkip: true) != -1)
            throw new Exception("Owner policy: the boss-rare fixture must be an offer the unforced scorer skips.");
        if (BotBrain.ChooseCardReward(bossBot, bossOffers) < 0)
            throw new Exception("Owner policy: an act-1 boss rare menu must never be skipped.");
        Console.WriteLine("PASS: act-1 boss rare menu is forced to take a card.");

        // Owner policy 2026-10-04: routes must keep plain fights at or below
        // 60% of the chosen rooms. The first graph has a lower-value, fight-free
        // branch that must beat the high-value monster branch. The second graph
        // has no feasible branch, so the fewest-monsters branch wins.
        var shareStart = new MapPoint(0, 0) { PointType = MapPointType.Unknown };
        var shareA1 = new MapPoint(0, 1) { PointType = MapPointType.Monster };
        var shareA2 = new MapPoint(0, 2) { PointType = MapPointType.Monster };
        var shareB1 = new MapPoint(1, 1) { PointType = MapPointType.Unknown };
        var shareB2 = new MapPoint(1, 2) { PointType = MapPointType.RestSite };
        var shareBoss = new MapPoint(0, 3) { PointType = MapPointType.Boss };
        shareStart.AddChildPoint(shareA1); shareStart.AddChildPoint(shareB1);
        shareA1.AddChildPoint(shareA2); shareB1.AddChildPoint(shareB2);
        shareA2.AddChildPoint(shareBoss); shareB2.AddChildPoint(shareBoss);
        double MonsterHeavy(MapPointType type, int hp, int gold) => type switch
        {
            MapPointType.Monster => 100,
            MapPointType.Unknown or MapPointType.RestSite => 1,
            _ => 0,
        };
        var unconstrainedRoute = RoutePlanner.Plan(shareStart, shareBoss, MonsterHeavy, 2, 1);
        if (unconstrainedRoute is null || unconstrainedRoute.Path[1] != shareA1)
            throw new Exception("Owner policy: the route-share fixture needs the unconstrained planner to prefer the monster branch.");
        var balancedRoute = RoutePlanner.Plan(shareStart, shareBoss, MonsterHeavy, 2, 1, enforceMonsterShare: true);
        if (balancedRoute is null || balancedRoute.Path[1] != shareB1
            || RoutePlanner.MonsterShare(balancedRoute.Path) > RoutePlanner.MaxMonsterShare)
            throw new Exception("Owner policy: a route with 100% plain fights must lose to a 0% branch under the 60% cap.");

        var hardStart = new MapPoint(0, 0) { PointType = MapPointType.Unknown };
        var hardA1 = new MapPoint(0, 1) { PointType = MapPointType.Monster };
        var hardA2 = new MapPoint(0, 2) { PointType = MapPointType.Monster };
        var hardA3 = new MapPoint(0, 3) { PointType = MapPointType.Monster };
        var hardB1 = new MapPoint(1, 1) { PointType = MapPointType.Monster };
        var hardB2 = new MapPoint(1, 2) { PointType = MapPointType.Monster };
        var hardB3 = new MapPoint(1, 3) { PointType = MapPointType.Unknown };
        var hardBoss = new MapPoint(0, 4) { PointType = MapPointType.Boss };
        hardStart.AddChildPoint(hardA1); hardStart.AddChildPoint(hardB1);
        hardA1.AddChildPoint(hardA2); hardB1.AddChildPoint(hardB2);
        hardA2.AddChildPoint(hardA3); hardB2.AddChildPoint(hardB3);
        hardA3.AddChildPoint(hardBoss); hardB3.AddChildPoint(hardBoss);
        double HardValue(MapPointType type, int hp, int gold) => type switch
        {
            MapPointType.Monster => 100,
            MapPointType.Unknown => 1,
            _ => 0,
        };
        var fewest = RoutePlanner.Plan(hardStart, hardBoss, HardValue, 2, 1, enforceMonsterShare: true);
        if (fewest is null || fewest.Path.Count(point => point.PointType == MapPointType.Monster) != 2)
            throw new Exception("Owner policy: when every route is over 60% monsters, the route with the fewest monsters must win.");
        Console.WriteLine("PASS: route monster share is capped at 60%, with a fewest-monsters fallback.");

        // Owner policy 2026-10-04: the reward screen needs a bounded large-deck
        // bar. Live Bot 2 drafted from 22 to 31 cards and still had a starting
        // Strike as its top damage card; this bar must make the 31st marginal
        // filler harder to take while an engine/payoff can still clear it.
        var sizeBot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var sizeRun = RunState.CreateForTest(new[] { sizeBot }, seed: "OWNER-BOSS-RARE");
        sizeRun.CurrentActIndex = 1;
        foreach (var card in sizeBot.Deck.Cards.ToArray()) sizeBot.Deck.RemoveInternal(card);
        for (var i = 0; i < 30; i++) sizeBot.Deck.AddInternal(sizeRun.CreateCard<StrikeIronclad>(sizeBot));
        var largeDeckBar = BuildValue.RewardTakeBar(sizeBot);
        // POLICY CHANGE 2026-10-07: the bar is now a shallow 0.15-per-effective-card
        // slope from an EFFECTIVE 16, not 1.5-per-raw-card from 24. A 30-card vanilla
        // deck reads 2.1 here instead of 9.0. The bar's job is unchanged -- it still has
        // to separate an engine from filler -- but the early pressure now lives in the
        // total-side penalty, which is what the earlier raw-count onset was double-charging.
        if (largeDeckBar < 2)
            throw new Exception($"Owner policy: a 30-card act-2 deck must raise the reward bar, got {largeDeckBar:F1}.");
        Console.WriteLine("PASS: large act-2 decks get a bounded reward-size bar.");
    }
    private sealed class EliteMap : ActMap
    {
        public override MapPoint StartingMapPoint { get; } = new(0, 0) { PointType = MapPointType.Unknown };
        public override MapPoint BossMapPoint { get; } = new(0, 3) { PointType = MapPointType.Boss };
        internal MapPoint Elite { get; } = new(0, 1) { PointType = MapPointType.Elite };
        protected override MapPoint?[,] Grid { get; } = new MapPoint?[1, 1];
        internal EliteMap()
        {
            var rest = new MapPoint(1, 1) { PointType = MapPointType.RestSite };
            var merge = new MapPoint(0, 2) { PointType = MapPointType.Unknown };
            StartingMapPoint.AddChildPoint(rest); StartingMapPoint.AddChildPoint(Elite);
            rest.AddChildPoint(merge); Elite.AddChildPoint(merge); merge.AddChildPoint(BossMapPoint);
        }
    }
}
