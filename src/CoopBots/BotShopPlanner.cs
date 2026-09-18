using CoopBots.Building;
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
    // The native MawBank only disables on a positive spend (AfterItemPurchased
    // returns early when goldSpent <= 0), so a free purchase leaves its income
    // alive. Matches the single-purchase evaluation exactly.
    private static bool ActiveMawBank(Player player)
        => player.Relics.Any(relic => relic.GetType().Name == "MawBank" && !relic.IsUsedUp);
    // Act index at which the run's remaining shops stop being worth saving for.
    // Shared with the potion policy: past this line nothing is worth holding for.
    internal const int FinalAct = 2;

    // What removing this card is worth to the deck, using the same valuation as
    // reward picks and upgrades. A card that has outlived its purpose scores as
    // a removal target even when it is not a curse or a basic card, and the
    // only copy of a defensive or engine card is protected explicitly.
    internal static double RemovalValue(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        if (!card.IsRemovable) return 0;
        var value = Building.BuildValue.Remove(card, player, deckOverride).Total;
        // A curse is always worth paying to remove, even in a deck so poor that
        // nothing looks below average — and more so later, when the next chance
        // to remove one may be the one being offered right now.
        if (card.Type == CardType.Curse)
            value = Math.Max(value, 155 * RunDepth.BloatFactor(player));
        return value;
    }

    // The decision plus the comparison that produced it, so the two-purchase
    // rule can be verified on the real path without a second implementation.
    internal readonly record struct ShopPlan(
        Choice? Choice, int? PairFirst, int? PairSecond, double PairNet, double SingleNet, bool FromPair)
    {
        internal static ShopPlan Single(Choice choice, double net) => new(choice, null, null, 0, net, false);
    }

    internal static Choice? Choose(MerchantInventory inventory) => ChooseDetailed(inventory).Choice;

    internal static ShopPlan ChooseDetailed(MerchantInventory inventory)
    {
        var player = inventory.Player;
        var entries = inventory.AllEntries.ToList();
        var removal = BestRemoval(player.Deck.Cards.ToList(), player);
        var offers = new List<PurchaseComparison.Offer>();
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
            // Gold that cannot reach another shop buys anything that is not a
            // loss, and a relic nobody modelled is not a loss — it is an unknown.
            // The threshold below can never express that, because a score of zero
            // fails `value > cost * factor` at every factor: the reviewed run
            // walked into its last shop with 137 gold, a Strawberry on the shelf
            // and bought nothing. Only an unpriced relic gets this floor; one with
            // a modelled downside (Brimstone, PhilosophersStone) is priced, stays
            // at zero and is still refused.
            if (value <= 0 && entry is MerchantRelicEntry { Model: not null } speculative
                && RunDepth.LastShopBeforeBoss(player)
                && !HumanCoopAdvisor.RelicPriced(speculative.Model, player))
                value = entry.Cost;
            // Spending switches off MawBank; retain a modest allowance for lost future income.
            if (entry.Cost > 0 && ActiveMawBank(player)) value -= 24;
            var finalAct = player.RunState.CurrentActIndex >= FinalAct;
            // Keeping a reserve only pays off if another shop is coming. In the
            // last act it just ends up in the save file.
            if (!finalAct && player.Gold - entry.Cost < 25 && value < entry.Cost * 1.6) continue;
            // The bar falls with the run: in the last act gold is nearly dead,
            // and at the last shop that can be reached before the boss it is dead
            // outright, so anything that does not make the deck worse is bought.
            if (value > entry.Cost * RunDepth.ShopThresholdFactor(player))
                offers.Add(new PurchaseComparison.Offer(index, entry is MerchantCardRemovalEntry ? removal.index : -1,
                    entry, value, entry.Cost, entry is MerchantPotionEntry, IsVariablePrice(entry),
                    entry is MerchantRelicEntry));
        }

        if (offers.Count == 0) return new ShopPlan(null, null, null, 0, 0, false);
        var bestSingle = offers
            .OrderByDescending(offer => offer.Net).ThenBy(offer => offer.Cost).ThenBy(offer => offer.Index).First();

        var bestPairNet = double.NegativeInfinity;
        PurchaseComparison.Offer? bestPairFirst = null;
        PurchaseComparison.Offer? bestPairSecond = null;
        // A pair is only considered among a fixed, deterministic prefix of the
        // stock, so the result depends on the inventory's existing entry order.
        var pairLimit = Math.Min(offers.Count, PurchaseComparison.MaxConsidered);
        var potionCount = player.Potions.Count();
        var openPotionSlots = Math.Max(0, player.MaxPotionCount - potionCount);

        void Consider(PurchaseComparison.Offer first, PurchaseComparison.Offer second)
        {
            // The second item is valued against the deck/gold/potion state the
            // first purchase would leave, without touching the live deck, gold
            // or stock. The first item already carries the one MawBank income
            // credit, so the fresh second value deliberately charges nothing.
            var projectedPotions = potionCount + (first.IsPotion ? 1 : 0);
            var secondValue = SecondValue(second, first, player, removal, projectedPotions);
            // A pair the existing single-purchase driver would refuse after the
            // first transaction is not a winning pair.
            if (!SecondExecutable(second, secondValue, player, player.Gold - first.Cost, projectedPotions)) return;
            var net = first.Value + secondValue - first.Cost - second.Cost;
            if (net <= bestPairNet + PurchaseComparison.PairMargin) return;
            bestPairNet = net;
            bestPairFirst = first;
            bestPairSecond = second;
        }

        for (var left = 0; left < pairLimit; left++)
        {
            for (var right = left + 1; right < pairLimit; right++)
            {
                var a = offers[left];
                var b = offers[right];
                if (!PurchaseComparison.Feasible(a, b, player.Gold, openPotionSlots)) continue;
                // Order matters: the first purchase changes the context for the
                // second, and only an executable order may win.
                Consider(a, b);
                Consider(b, a);
            }
        }

        // The pair changes only which first action is taken; the driver replans
        // after the actual purchase, so the second item is never committed here.
        if (bestPairFirst is { } first && bestPairSecond is { } second
            && PurchaseComparison.PairBeatsSingle(bestPairNet, bestSingle.Net))
            return new ShopPlan(new Choice(first.Index, first.RemovalIndex, first.Value, first.Cost),
                first.Index, second.Index, bestPairNet, bestSingle.Net, true);
        var single = new Choice(bestSingle.Index, bestSingle.RemovalIndex, bestSingle.Value, bestSingle.Cost);
        return ShopPlan.Single(single, bestSingle.Net);
    }

    // The best removal target and its value inside a given deck. Non-removable
    // cards are skipped outright: a zero-valued invalid candidate must never beat
    // a negative-valued valid one.
    private static (int index, double value) BestRemoval(IReadOnlyList<CardModel> deck, Player player)
    {
        var bestIndex = -1;
        var bestValue = 0.0;
        for (var index = 0; index < deck.Count; index++)
        {
            if (!deck[index].IsRemovable) continue;
            var value = RemovalValue(deck[index], player, deck);
            if (bestIndex < 0 || value > bestValue)
            {
                bestValue = value;
                bestIndex = index;
            }
        }
        return (bestIndex, bestIndex < 0 ? 0 : bestValue);
    }

    // Whether the existing single-purchase driver would actually take the second
    // item after paying for the first. This mirrors the single-item gates
    // (affordability, item threshold, reserve) on the projected state, so a pair
    // cannot win on combined arithmetic the driver itself would refuse to
    // execute. Its value is the freshly recomputed second value (no MawBank fee).
    internal static bool SecondExecutable(PurchaseComparison.Offer second, double secondValue, Player player,
        int goldAfterFirst, int projectedPotionCount)
    {
        if (second.Cost > goldAfterFirst) return false;
        if (second.IsPotion && projectedPotionCount >= player.MaxPotionCount) return false;
        var finalAct = player.RunState.CurrentActIndex >= FinalAct;
        if (!finalAct && goldAfterFirst - second.Cost < 25 && secondValue < second.Cost * 1.6) return false;
        return secondValue > second.Cost * RunDepth.ShopThresholdFactor(player);
    }

    // The second purchase's value against the deck the first would leave. Relics
    // never reach here (they are excluded from pairs). Cards and removals are
    // rebuilt against the hypothetical deck and a potion is priced at the
    // projected potion count; the lost MawBank income is charged here only when
    // the first item was free and the bank is still active (see below).
    private static double SecondValue(PurchaseComparison.Offer second, PurchaseComparison.Offer first,
        Player player, (int index, double value) currentRemoval, int projectedPotionCount)
    {
        List<CardModel>? hypothetical = null;
        switch (first.Entry)
        {
            case MerchantCardEntry { CreationResult: not null } firstCard:
                hypothetical = player.Deck.Cards.ToList();
                hypothetical.Add(firstCard.CreationResult.Card);
                break;
            case MerchantCardRemovalEntry when currentRemoval.index >= 0
                && currentRemoval.index < player.Deck.Cards.Count:
                hypothetical = player.Deck.Cards.ToList();
                hypothetical.RemoveAt(currentRemoval.index);
                break;
        }

        var value = second.Entry switch
        {
            MerchantCardEntry card when card.CreationResult is not null =>
                GoldFor(Building.BuildValue.Add(card.CreationResult.Card, player, hypothetical).Total),
            MerchantCardRemovalEntry =>
                GoldFor(hypothetical is not null ? BestRemoval(hypothetical, player).value : currentRemoval.value),
            MerchantPotionEntry potion when potion.Model is not null && player.HasOpenPotionSlots =>
                PotionValue(potion.Model, player, projectedPotionCount),
            _ => second.Value,
        };

        // The first item carries the lost MawBank income only if it actually
        // disabled the bank. A free first purchase leaves the bank active, so the
        // first paid purchase in the pair is the second item and is charged here;
        // if the first was paid the bank is already gone and nothing is charged.
        // Both free => nothing; both paid => still exactly one charge.
        if (first.Cost <= 0 && second.Cost > 0 && ActiveMawBank(player)) value -= 24;
        return value;
    }

    // Relics whose price or stock can change between the two purchases. A pair
    // that includes one cannot be modelled without assuming a discount/restock,
    // so it is conservatively excluded from pair comparison; the single purchase
    // is priced exactly as before.
    private static bool IsVariablePrice(MerchantEntry entry) => entry switch
    {
        MerchantRelicEntry relic when relic.Model is not null =>
            relic.Model.GetType().Name is "MembershipCard" or "MawBank" or "TheCourier",
        _ => false,
    };

    internal static double PotionValue(PotionModel potion, Player player)
        => PotionValue(potion, player, player.Potions.Count());

    // A potion's marginal value falls with how many the player already holds, so
    // the second potion of a pair is priced against the projected count (the
    // first potion occupies a slot). The caller can never pass a count below the
    // live one here except for that projection.
    internal static double PotionValue(PotionModel potion, Player player, int potionCount)
    {
        var id = potion.GetType().Name;
        var low = player.Creature.CurrentHp < player.Creature.MaxHp * .55;
        // Names must match the real models; the planner can only use potions the
        // kernel simulates (rescue, damage, buffs, energy).
        var useful = id is "BlockPotion" or "BloodPotion" or "FirePotion" or "ExplosiveAmpoule"
            or "WeakPotion" or "StrengthPotion" or "DexterityPotion" or "FocusPotion"
            or "EnergyPotion" or "SwiftPotion" or "VulnerablePotion";
        if (!useful) return 0; // Do not spend scarce gold on an unknown potion effect.
        return (low ? 160 : 65) / (1 + potionCount * .5);
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
