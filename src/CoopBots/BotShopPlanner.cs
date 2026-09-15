using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

internal static class BotShopPlanner
{
    internal sealed record Choice(int Index, int RemovalIndex, double Value, int Cost);
    internal static string Key(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry c => c.CreationResult?.Card.Id.Entry ?? "sold",
        MerchantRelicEntry r => r.Model?.Id.Entry ?? "sold",
        MerchantPotionEntry p => p.Model?.Id.Entry ?? "sold",
        MerchantCardRemovalEntry => "remove",
        _ => "unknown",
    };
    // Gold a permanent deck improvement is worth per point of deck value. Stated
    // once here so every shop entry is compared in the same unit — including
    // removals, which previously compared raw deck value against a gold price and
    // so could never beat the 75 gold a first removal costs.
    // Read by the draft pipeline (tests/PatchSmoke/DraftSim) so the simulated shop
    // and the live shop cannot disagree about what a deck improvement is worth in gold.
    internal const double GoldPerDeckValue = 2.2;
    private static double GoldFor(double deckValue) => Math.Max(0, deckValue) * GoldPerDeckValue;
    // Act index at which the run's remaining shops stop being worth saving for.
    // Shared with the potion policy: past this line nothing is worth holding for.
    internal const int FinalAct = 2;

    // What removing this card is worth to the deck, using the same valuation as
    // reward picks and upgrades. A card that has outlived its purpose scores as
    // a removal target even when it is not a curse or a basic card, and the
    // only copy of a defensive or engine card is protected explicitly.
    internal static double RemovalValue(CardModel card, Player player)
    {
        if (!card.IsRemovable) return 0;
        var value = Building.BuildValue.Remove(card, player).Total;
        // A curse is always worth paying to remove, even in a deck so poor that
        // nothing looks below average — and more so later, when the next chance
        // to remove one may be the one being offered right now.
        if (card.Type == CardType.Curse)
            value = Math.Max(value, 155 * RunDepth.BloatFactor(player));
        return value;
    }

    internal static Choice? Choose(MerchantInventory inventory)
    {
        var player = inventory.Player;
        var entries = inventory.AllEntries.ToList();
        var removal = player.Deck.Cards.Select((card, index) => (index, value: RemovalValue(card, player)))
            .OrderByDescending(x => x.value).ThenBy(x => x.index).FirstOrDefault();
        var candidates = new List<Choice>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!entry.IsStocked || entry.Cost < 0 || entry.Cost > player.Gold) continue;
            var value = entry switch
            {
                // Cards must clearly improve the deck, not just be playable filler:
                // the same improvement the reward screen would see, priced in gold.
                MerchantCardEntry card when card.CreationResult is not null =>
                    GoldFor(Building.BuildValue.Add(card.CreationResult.Card, player).Total),
                MerchantRelicEntry relic when relic.Model is not null => RelicWillingness(relic.Model, player, inventory),
                MerchantPotionEntry potion when potion.Model is not null && player.HasOpenPotionSlots => PotionValue(potion.Model, player),
                MerchantCardRemovalEntry => GoldFor(removal.value),
                _ => 0,
            };
            // Spending switches off MawBank; retain a modest allowance for lost future income.
            if (entry.Cost > 0 && player.Relics.Any(r => r.GetType().Name == "MawBank" && !r.IsUsedUp)) value -= 24;
            var finalAct = player.RunState.CurrentActIndex >= FinalAct;
            // Keeping a reserve only pays off if another shop is coming. In the
            // last act it just ends up in the save file.
            if (!finalAct && player.Gold - entry.Cost < 25 && value < entry.Cost * 1.6) continue;
            // The bar falls with the run: in the last act gold is nearly dead,
            // and at the last shop that can be reached before the boss it is dead
            // outright, so anything that does not make the deck worse is bought.
            if (value > entry.Cost * RunDepth.ShopThresholdFactor(player))
                candidates.Add(new(index, entry is MerchantCardRemovalEntry ? removal.index : -1, value, entry.Cost));
        }
        // Compare net benefit in gold-equivalent units, rather than buying the first affordable item.
        return candidates.OrderByDescending(c => c.Value - c.Cost).ThenBy(c => c.Cost).ThenBy(c => c.Index).FirstOrDefault();
    }

    internal static double PotionValue(PotionModel potion, Player player)
    {
        var id = potion.GetType().Name;
        var low = player.Creature.CurrentHp < player.Creature.MaxHp * .55;
        // Names must match the real models; the planner can only use potions the
        // kernel simulates (rescue, damage, buffs, energy).
        var useful = id is "BlockPotion" or "BloodPotion" or "FirePotion" or "ExplosiveAmpoule"
            or "WeakPotion" or "StrengthPotion" or "DexterityPotion" or "FocusPotion"
            or "EnergyPotion" or "SwiftPotion" or "VulnerablePotion";
        if (!useful) return 0; // Do not spend scarce gold on an unknown potion effect.
        return (low ? 160 : 65) / (1 + player.Potions.Count() * .5);
    }

    private static double RelicWillingness(RelicModel relic, Player player, MerchantInventory inventory)
    {
        if (relic.GetType().Name == "MembershipCard")
        {
            var remaining = Math.Max(0, player.Gold - inventory.RelicEntries.Where(e => e.Model == relic).Select(e => e.Cost).FirstOrDefault());
            // Savings are limited by useful stocked goods and the buyer's actual remaining budget.
            var usefulStock = inventory.AllEntries.Where(e => e.IsStocked && e is not MerchantRelicEntry)
                .Sum(e => e switch
                {
                    MerchantCardEntry c when c.CreationResult is not null
                        && Building.BuildValue.Add(c.CreationResult.Card, player).Total > 0 => e.Cost,
                    MerchantCardRemovalEntry when player.Deck.Cards.Any(c => RemovalValue(c, player) > 0) => e.Cost,
                    MerchantPotionEntry p when p.Model is not null && player.HasOpenPotionSlots && PotionValue(p.Model, player) > 0 => e.Cost,
                    _ => 0,
                });
            return Math.Min(remaining, usefulStock * .5) + 35;
        }
        return Math.Max(0, HumanCoopAdvisor.RelicValue(relic, player).Score) * 7;
    }
}
