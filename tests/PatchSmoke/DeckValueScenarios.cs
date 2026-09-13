using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Deck-building valuation: rewards, the shop, upgrades and removals all share
// HumanCoopAdvisor.CardValue, so these checks cover the whole drafting pipeline.
internal static class DeckValueScenarios
{
    internal static void Run()
    {
        var attackHeavy = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var blockHeavy = Player.CreateForNewRun<Deprived>(UnlockState.all, 2);
        var bloated = Player.CreateForNewRun<Deprived>(UnlockState.all, 3);
        var party = new[] { attackHeavy, blockHeavy, bloated };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "DECKVALUE"));
        foreach (var player in party)
        {
            player.ResetCombatState(); combat.AddPlayer(player);
        }

        for (var i = 0; i < 10; i++)
        {
            attackHeavy.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(attackHeavy));
            blockHeavy.Deck.AddInternal(combat.CreateCard<DefendIronclad>(blockHeavy));
        }
        for (var i = 0; i < 26; i++)
            bloated.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(bloated));

        double Value(CardModel card, Player player) => HumanCoopAdvisor.CardValue(card, player).Score;

        var inflame = combat.CreateCard<Inflame>(attackHeavy);
        var strike = combat.CreateCard<StrikeIronclad>(attackHeavy);
        if (!(Value(inflame, attackHeavy) > Value(strike, attackHeavy)))
            throw new Exception($"A scaling power must outvalue a basic attack: {Value(inflame, attackHeavy):F1} vs {Value(strike, attackHeavy):F1}");
        Console.WriteLine("PASS: card value prefers scaling powers over basic attacks.");

        var defend = combat.CreateCard<DefendIronclad>(attackHeavy);
        var defendForBlockDeck = combat.CreateCard<DefendIronclad>(blockHeavy);
        if (!(Value(defend, attackHeavy) > Value(defendForBlockDeck, blockHeavy)))
            throw new Exception($"A block card must be worth more to a deck without block: {Value(defend, attackHeavy):F1} vs {Value(defendForBlockDeck, blockHeavy):F1}");
        Console.WriteLine("PASS: card value reacts to the deck's block need.");

        var strikeForBloated = combat.CreateCard<StrikeIronclad>(bloated);
        if (!(Value(strikeForBloated, bloated) < Value(strike, attackHeavy)))
            throw new Exception($"Filler must be worth less in an oversized deck: {Value(strikeForBloated, bloated):F1} vs {Value(strike, attackHeavy):F1}");
        Console.WriteLine("PASS: card value penalises dilution in an oversized deck.");

        // The shop's potion whitelist must use the real model names.
        if (BotShopPlanner.PotionValue(ModelDb.Potion<ExplosiveAmpoule>().ToMutable(), attackHeavy) <= 0
            || BotShopPlanner.PotionValue(ModelDb.Potion<VulnerablePotion>().ToMutable(), attackHeavy) <= 0)
            throw new Exception("Shop potion valuation must recognise ExplosiveAmpoule and VulnerablePotion.");
        Console.WriteLine("PASS: shop potion valuation recognises the real combat potions.");
    }
}
