using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using System.Reflection;

// Regression coverage for the binary contribution imported on 2026-10-23.
// The reviewed ZIP matches JUGGERNAUT (势不可当), never DOMINATE (主宰).
// Actual R2: work/merge-key-card-acquisition-20261023/r2/proof.json.
// Ten production mutants each built successfully, exited 1 at these assertions,
// then exact source restoration rebuilt and passed (exit 0):
// "first Juggernaut and multiplayer Flanking must receive the contribution's acquisition priority"
// "reward must take its first key card even against a higher-valued ordinary card"
// "permanent grid must prioritize one negative-valued first key card and stop before its duplicate"
// "shop must buy the cheapest affordable first key card before ordinary value comparisons"
// "equal health loss must cost more near the reserve and most when it becomes lethal"
// "construction must activate Juggernaut before its two later block events and price two damage triggers"
// "joint construction must amplify an ally only when the source can actually pay for Flanking"
// "investment probing must include delayed copy skills and powers without treating ordinary attacks as investment"
// "Nightmare must leave the acquisition blacklist when delayed-copy planning is imported"
// "Community package: reward cancellation must propagate"
internal static class CommunityPackageScenarios
{
    private static bool cancelScores;

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        if (selected is null or "acquisition") Acquisition();
        if (selected is null or "survival") Survival();
        if (selected is null or "juggernaut") JuggernautConstruction();
        if (selected is null or "team") TeamAmplification();
        if (selected is null or "copy") CopyInvestment();
    }

    private static Player Fixture(bool multiplayer = true, bool ironclad = true)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, 90000000000000001);
        if (ironclad)
            typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(player, ModelDb.AllCharacters.First(c => c is Ironclad));
        var players = new List<Player> { player };
        if (multiplayer) players.Add(Player.CreateForNewRun<Deprived>(UnlockState.all, 90000000000000002));
        RunState.CreateForTest(players, seed: "COMMUNITY-PACKAGE-20261022");
        foreach (var p in players)
        {
            p.MaxEnergy = 3;
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            foreach (var card in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(card);
        }
        return player;
    }

    private static CardModel Card(string id, Player player)
        => ConstructionParallelScenarios.Card(id, player);

    private static bool FixedScore(CardModel card, ref BuildValue.Valuation __result)
    {
        if (cancelScores) throw new OperationCanceledException("community score cancellation");
        __result = new BuildValue.Valuation(card.Id.Entry is "JUGGERNAUT" or "FLANKING" or "KNOCKDOWN" ? -5 : 20,
            "fixture:acquisition-policy");
        return false;
    }

    private static void Acquisition()
    {
        var p = Fixture();
        var juggernaut = Card("JUGGERNAUT", p);
        var dominate = Card("DOMINATE", p);
        var flanking = Card("FLANKING", p);
        Check(KeyCardAcquisition.Preferred(juggernaut, p) && KeyCardAcquisition.Preferred(flanking, p),
            "first Juggernaut and multiplayer Flanking must receive the contribution's acquisition priority");
        Check(!KeyCardAcquisition.Preferred(dominate, p),
            "Dominate must not be confused with the imported Juggernaut policy");
        var single = Fixture(multiplayer: false);
        Check(!KeyCardAcquisition.Preferred(Card("FLANKING", single), single)
            && !KeyCardAcquisition.Preferred(Card("KNOCKDOWN", single), single),
            "ally amplification acquisition priority must require multiplayer");
        var other = Fixture(ironclad: false);
        Check(!KeyCardAcquisition.Preferred(Card("JUGGERNAUT", other), other),
            "Juggernaut acquisition priority must require Ironclad");

        var harmony = new Harmony("coopbots.community-package.acquisition");
        harmony.Patch(AccessTools.Method(typeof(BuildValue), nameof(BuildValue.AddForDecision)),
            prefix: new HarmonyMethod(typeof(CommunityPackageScenarios), nameof(FixedScore)));
        try
        {
            var strike = Card("STRIKE_IRONCLAD", p);
            Check(BuildValue.BestReward(p, new[] { strike, juggernaut }, allowSkip: true) == 1,
                "reward must take its first key card even against a higher-valued ordinary card");
            var second = Card("JUGGERNAUT", p);
            var grid = BuildValue.GreedyPick(p, new[] { juggernaut, second, strike }, 0, 3);
            Check(grid.Picked.Count == 2 && grid.Picked[0].Id.Entry == "JUGGERNAUT" && grid.Picked[1] == strike,
                "permanent grid must prioritize one negative-valued first key card and stop before its duplicate");
            p.Deck.AddInternal(juggernaut);
            Check(!KeyCardAcquisition.Preferred(second, p)
                && BuildValue.BestReward(p, new[] { second }, allowSkip: true) == -1,
                "an owned copy must restore ordinary reward and skip policy");
            p.Deck.RemoveInternal(juggernaut);

            p.Gold = 100;
            var inventory = new MerchantInventory(p);
            ShopRemovalAnchorScenarios.Offer(inventory, juggernaut, 100);
            ShopRemovalAnchorScenarios.Offer(inventory, flanking, 80);
            ShopRemovalAnchorScenarios.Offer(inventory, strike, 1);
            var purchase = BotShopPlanner.Choose(inventory);
            Check(purchase is { RemovalIndex: -1, Cost: 80 }
                && BotShopPlanner.Key(inventory.AllEntries.ElementAt(purchase.Index)) == "FLANKING",
                "shop must buy the cheapest affordable first key card before ordinary value comparisons");
            var curse = Card("CLUMSY", p); p.Deck.AddInternal(curse);
            var removal = new MerchantCardRemovalEntry(p); ShopRemovalAnchorScenarios.Cost(removal, 20);
            typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory, removal);
            var cleanse = BotShopPlanner.Choose(inventory);
            Check(cleanse is { RemovalIndex: >= 0 } && p.Deck.Cards[cleanse.RemovalIndex] == curse,
                "native affordable curse removal must still precede a first key-card purchase");

            cancelScores = true;
            try
            {
                BuildValue.BestReward(p, new[] { strike });
                throw new InvalidOperationException("Community package: reward cancellation must propagate");
            }
            catch (OperationCanceledException) { }
        }
        finally { cancelScores = false; harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("PASS: community acquisition preserves card identity, first-copy scope, reward/grid/shop priority and cancellation.");
    }

    private static void Survival()
    {
        var healthy = SurvivalValue.LossCost(80, 80, 10);
        var wounded = SurvivalValue.LossCost(20, 80, 10);
        var lethal = SurvivalValue.LossCost(10, 80, 10);
        Check(Math.Abs(healthy - 13) < 1e-8 && wounded > healthy && lethal > wounded,
            "equal health loss must cost more near the reserve and most when it becomes lethal");
        Check(SurvivalValue.ExtraLossCost(20, 80, 0) == 0,
            "no health loss must not create an additional reserve penalty");
        Console.WriteLine("PASS: community construction prices low-health and lethal exposure above healthy loss.");
    }

    private static void JuggernautConstruction()
    {
        var p = Fixture(multiplayer: false);
        var engine = Card("JUGGERNAUT", p);
        var first = Card("DEFEND_IRONCLAD", p); var second = Card("DEFEND_IRONCLAD", p);
        ConstructionHand.Slot Slot(CardModel card) => new(card, CardProfile.Of(card));
        var hand = ConstructionHand.Solve(new[] { Slot(first), Slot(engine), Slot(second) }, 3,
            new ConstructionHand.Context(4, 0, 100) { OwnerHp = 80, OwnerMaxHp = 80 });
        Check(hand.ActiveBlockTriggerDamage == 6 && hand.BlockEvents == 2 && hand.BlockTriggeredDamage == 12,
            "construction must activate Juggernaut before its two later block events and price two damage triggers");
        var big = Card("IMPERVIOUS", p);
        var oneEvent = ConstructionHand.Solve(new[] { Slot(big) }, 1,
            new ConstructionHand.Context(2, 0, 100) { BlockTriggerDamage = 6, OwnerHp = 80, OwnerMaxHp = 80 });
        Check(oneEvent.Block > hand.Block && oneEvent.BlockEvents == 1 && oneEvent.BlockTriggeredDamage == 6,
            "a larger single block gain must still trigger Juggernaut only once");
        Console.WriteLine("PASS: community Juggernaut construction counts block events and activation order rather than block amount.");
    }

    private static void TeamAmplification()
    {
        var attacker = new TeamSynergyMath.Seat(1, 1, 1, 10, 0, 0, 80, 80, []);
        var support = new TeamSynergyMath.Seat(1, 1, 1, 0, 0, 0, 80, 80,
            [new TeamSynergyMath.Component(Flanking: 2, Cost: 1)]);
        var pressure = new TeamSynergyMath.Pressure(100, 8, 1);
        var plain = TeamSynergyMath.Evaluate(new[] { support with { Components = [] }, attacker }, pressure);
        var joint = TeamSynergyMath.Evaluate(new[] { support, attacker }, pressure);
        var unpaid = TeamSynergyMath.Evaluate(new[] { support with { Energy = 0 }, attacker }, pressure);
        Check(Math.Abs(plain.Damage - 10) < 1e-8 && Math.Abs(joint.Damage - 20) < 1e-8
            && Math.Abs(unpaid.Damage - plain.Damage) < 1e-8,
            "joint construction must amplify an ally only when the source can actually pay for Flanking");
        var alone = TeamSynergyMath.Evaluate(new[] { support with { Attack = 10, FreeAttack = 10 } }, pressure);
        Check(Math.Abs(alone.Damage - 10) < 1e-8,
            "Flanking must not amplify the applier's own attack in joint construction");
        Console.WriteLine("PASS: community joint construction uses payable ally-only amplification.");
    }

    private static void CopyInvestment()
    {
        var p = Fixture();
        Check(KernelInvestmentProbe.IsInvestment(Card("NIGHTMARE", p))
            && KernelInvestmentProbe.IsInvestment(Card("DUAL_WIELD", p))
            && KernelInvestmentProbe.IsInvestment(Card("HEIRLOOM_HAMMER", p))
            && KernelInvestmentProbe.IsInvestment(Card("JUGGERNAUT", p))
            && !KernelInvestmentProbe.IsInvestment(Card("STRIKE_IRONCLAD", p)),
            "investment probing must include delayed copy skills and powers without treating ordinary attacks as investment");
        var nightmare = BuildValue.Add(Card("NIGHTMARE", p), p);
        Check(double.IsFinite(nightmare.Total) && !nightmare.Reason.Contains("blacklist:nightmare", StringComparison.Ordinal),
            "Nightmare must leave the acquisition blacklist when delayed-copy planning is imported");
        Console.WriteLine("PASS: community copy planning enables the three copy skills and restores ordinary Nightmare valuation.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Community package: " + message); }
}
