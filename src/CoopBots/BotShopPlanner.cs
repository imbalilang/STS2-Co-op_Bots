using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;

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
    // Price the same deck-strength gain for additions and removals over the
    // remaining investment horizon. Native shop prices already make later
    // removals more expensive; there is no second removal discount.
    // Finite encounter utility has a different scale from the old unbounded
    // throughput. Calibrate one conversion for every permanent deck purchase.
    internal const double GoldPerDeckValue = 3.6;
    private static double GoldFor(double deckValue) => Math.Max(0, deckValue) * GoldPerDeckValue;

    // Native shops enforce affordability without an investment score.
    // Owner 2026-10-04: convert spare cash into permanent strength now. The
    // first 150 gold keeps its full opportunity cost; larger purses lower that
    // cost continuously. Recompute after each purchase, including pair previews.
    internal static double PriceFactor(Player player, int gold)
        => Math.Min(RunDepth.ShopThresholdFactor(player), Math.Clamp(150d / Math.Max(150, gold), .35, 1));

    /// <summary>The reward score converted with the common permanent-investment price.</summary>
    private static double CardGold(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var value = BuildValue.AddForDecision(card, player, deckOverride);
        return GoldFor(PermanentValue(value, player));
    }

    internal static double PermanentValue(BuildValue.Valuation value, Player player)
        => value.Total * BotRestSitePatch.InvestmentHorizon(player);

    // The native MawBank only disables on a positive spend (AfterItemPurchased
    // returns early when goldSpent <= 0), so a free purchase leaves its income
    // alive. Matches the single-purchase evaluation exactly.
    private static bool ActiveMawBank(Player player)
        => player.Relics.Any(relic => relic.GetType().Name == "MawBank" && !relic.IsUsedUp);
    // Act index at which the run's remaining shops stop being worth saving for.
    // Shared with the potion policy: past this line nothing is worth holding for.
    internal const int FinalAct = 2;

    // Owner policy 2026-10-04: with two exceptions, a shop must not spend gold
    // on a potion. Fruit Juice and Fairy in a Bottle carry value beyond one
    // fight, so they stay legal everywhere; every other bottle is legal only in
    // the third-layer shop when no later shop remains.
    internal const string FruitJuiceId = "FRUIT_JUICE";
    internal const string FairyInABottleId = "FAIRY_IN_A_BOTTLE";

    internal static bool PotionPurchaseAllowed(string potionId, bool finalActLastShop)
        => finalActLastShop || potionId is FruitJuiceId or FairyInABottleId;

    internal static bool PotionPurchaseAllowed(Player player, PotionModel potion)
        => PotionPurchaseAllowed(potion.Id.Entry,
            RunDepth.Act(player) >= FinalAct && RunDepth.LastShopBeforeBoss(player));

    /// <summary>
    /// Cards whose removal is never worth it, whatever the deck-value model says. A map is the
    /// clearest case: its whole value happens outside combat, where the model cannot see it.
    /// Matched on both the model id and the type name, case-insensitively — this project has
    /// already been bitten by an id that came back empty, and a guard that silently stops
    /// matching is worse than no guard at all.
    /// </summary>
    internal static bool IsNeverRemoved(CardModel card) =>
        card.Id.Entry.Contains("SPOILS_MAP", StringComparison.OrdinalIgnoreCase)
        || card.GetType().Name.Contains("SpoilsMap", StringComparison.OrdinalIgnoreCase)
        || card.GetType().Name.Contains("TreasureMap", StringComparison.OrdinalIgnoreCase);

    // What removing this card is worth to the deck, using the same valuation as
    // reward picks and upgrades. A card that has outlived its purpose scores as
    // a removal target even when it is not a curse or a basic card, and the
    // only copy of a defensive or engine card is protected explicitly.
    internal static double RemovalValue(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        if (!card.IsRemovable) return 0;
        // NEVER REMOVE A MAP. SPOILS_MAP (藏宝图) marks a location on the run map — removing it
        // destroys that location for good, and the removal planner was happily doing it because
        // its deck-value model only knows what a card is worth IN COMBAT, where this one is
        // worth nothing at all. Reported by the user watching a live run.
        //
        // Matched on both the model id and the type name, case-insensitively: this project has
        // already been bitten once by an id that came back empty (the A/B log printed `?` for
        // cards whose Id.Entry was blank), and a guard that silently stops matching is worse
        // than no guard.
        if (IsNeverRemoved(card)) return -1000;
        if (BuildValue.RemovalProtection(card, deckOverride ?? player.Deck.Cards.ToArray()).Protected) return 0;
        return PermanentValue(BuildValue.RemoveForDecision(card, player, deckOverride), player);
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
        using var evaluation = DeckStrength.BeginEvaluation();
        var player = inventory.Player;
        var entries = inventory.AllEntries.ToList();
        var deck = player.Deck.Cards.ToList();
        var removalEntry = entries.FindIndex(e => e is MerchantCardRemovalEntry
            && e.IsStocked && e.Cost >= 0 && e.Cost <= player.Gold);
        // Native shops leave target selection to the player. Owner 2026-10-01:
        // an affordable removable curse is an immediate purchase, without a
        // deck search or a comparison against other goods. Native legality and
        // stock/price checks still apply when the driver submits the purchase.
        var curse = deck.FindIndex(c => c.IsRemovable && c.Type == CardType.Curse && !IsNeverRemoved(c));
        if (removalEntry >= 0 && curse >= 0)
        {
            var price = entries[removalEntry].Cost;
            return ShopPlan.Single(new Choice(removalEntry, curse, price, price), 0);
        }
        // The contributor's first-key-card policy follows immediate curse removal:
        // select the cheapest affordable first copy before comparing investments.
        var keyCard = entries.Select((entry, index) => (Entry: entry, Index: index))
            .Where(item => item.Entry is MerchantCardEntry { CreationResult: not null } card
                && item.Entry.IsStocked && item.Entry.Cost >= 0 && item.Entry.Cost <= player.Gold
                && KeyCardAcquisition.Preferred(card.CreationResult.Card, player, deck))
            .OrderBy(item => item.Entry.Cost).ThenBy(item => item.Index).FirstOrDefault();
        if (keyCard.Entry is not null)
        {
            Log.Info($"CoopBots shop priority: bot={player.NetId} card={Key(keyCard.Entry)} policy=first-key-card cost={keyCard.Entry.Cost} gold={player.Gold}");
            return ShopPlan.Single(new Choice(keyCard.Index, -1, keyCard.Entry.Cost, keyCard.Entry.Cost), 0);
        }
        var removal = removalEntry >= 0 ? BestRemoval(deck, player) : (index: -1, value: 0d);
        var offers = new List<PurchaseComparison.Offer>();
        var diagnostics = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!entry.IsStocked || entry.Cost < 0 || entry.Cost > player.Gold)
            {
                diagnostics.Add($"{index}:{Key(entry)}:cost={entry.Cost}:eligible=false:reason={(entry.IsStocked ? "affordability" : "stock")}");
                continue;
            }
            var value = entry switch
            {
                MerchantCardEntry card when card.CreationResult is not null =>
                    CardGold(card.CreationResult.Card, player),
                MerchantRelicEntry relic when relic.Model is not null => RelicWillingness(relic.Model, player, inventory),
                // 上游怎么做：货架上有什么就卖什么；游戏在 *购买* 那一刻用
                //            Hook.ShouldProcurePotion 把关（PotionCmd.TryToProcure）。
                // 我们怎么做：推销之前先问同一道钩子，拿不到的药水不进候选。
                // 为什么：实机 2026-09-29 第 25 层商店（读档后复现第二次）：SOZU 在身的席位
                //        空着两个药水格，规划器照卖 DEXTERITY_POTION，游戏按 Sozu 拒绝，
                //        驱动遂 Fail、整桌卡死在商店。绝不推销这一席拿不到的东西。
                MerchantPotionEntry potion when potion.Model is not null && player.HasOpenPotionSlots
                    && Hook.ShouldProcurePotion(player.RunState, player.Creature.CombatState, potion.Model, player)
                    && PotionPurchaseAllowed(player, potion.Model)
                    => PotionValue(potion.Model, player),
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
            if (entry is MerchantRelicEntry { Model: not null } speculative
                && RunDepth.LastShopBeforeBoss(player)
                && !HumanCoopAdvisor.RelicPriced(speculative.Model, player))
                value = Math.Max(value, entry.Cost);
            // Spending switches off MawBank; retain a modest allowance for lost future income.
            if (entry.Cost > 0 && ActiveMawBank(player)) value -= 24;
            // Native purchases require affordability. Our opportunity cost is
            // the shared price/remaining-shop threshold below. Owner 2026-09-30:
            // spending the final 25 gold must not impose a second discontinuous
            // threshold on removal while a 75-gold card escapes it.
            // The bar falls with the run: in the last act gold is nearly dead,
            // and at the last shop that can be reached before the boss it is dead
            // outright, so anything that does not make the deck worse is bought.
            if (value > entry.Cost * PriceFactor(player, player.Gold))
                offers.Add(new PurchaseComparison.Offer(index, entry is MerchantCardRemovalEntry ? removal.index : -1,
                    entry, value, entry.Cost, entry is MerchantPotionEntry, IsVariablePrice(entry),
                    entry is MerchantRelicEntry));
            diagnostics.Add($"{index}:{Key(entry)}:value={value:F2}:cost={entry.Cost}:net={value-entry.Cost*PriceFactor(player,player.Gold):F2}"
                + $":eligible={value>entry.Cost*PriceFactor(player,player.Gold)}");
        }

        // SHOPS ARE FOR THE DECK. A potion is a one-shot consumable whose whole value is
        // realised inside a single fight; gold is the only currency that buys PERMANENT
        // improvement, and every later fight is measured against the permanent deck. The
        // user's own play is the rule here — "商店里几乎不会买药水，只删牌和买关键牌" — and the
        // reviewed act-1 run is what the opposite looks like: 16 shop visits bought two
        // potions and zero cards, and the party then lost the act-1 boss holding a 13-15
        // card deck whose only attacks were the starter Strikes.
        //
        // So a bottle is considered only when this shop has NOTHING else that cleared the
        // bar — the case where the gold would otherwise be carried to a shop that may never
        // arrive. It is deliberately a comparison rather than a discount: any card, relic or
        // removal that is worth its price outranks every potion, in every act, at every
        // price. Dropping them from `offers` (instead of pricing them at zero) also keeps
        // them out of the pair comparison, so a card is never delayed to make room for one.
        if (offers.Any(offer => !offer.IsPotion))
            offers = offers.Where(offer => !offer.IsPotion).ToList();

        if (ConstructionDecision.AwaitingValues) return new ShopPlan(null, null, null, 0, 0, false);
        Log.Info($"CoopBots shop candidates: bot={player.NetId} gold={player.Gold} removal={removal.index}:{removal.value:F2} all=[{string.Join(';',diagnostics)}]");
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

    // Legal removal candidates in deck order, narrowed by the one priority tier
    // every removal path shares (see RemovalPlan.PriorityPool): a removable
    // curse, else unupgraded basic Strikes/Defends, else everything legal. This
    // is the probe/enumeration view; the actual choice goes through BestRemoval
    // so the role-protection pass runs too.
    internal static IReadOnlyList<int> RemovalCandidates(IReadOnlyList<CardModel> deck)
    {
        var legal = LegalRemovalIndices(deck);
        var tier = RemovalPlan.PriorityTier(legal.Select(i => deck[i]).ToList());
        return legal.Where(i => tier.Any(card => ReferenceEquals(card, deck[i]))).ToArray();
    }

    private static int[] LegalRemovalIndices(IReadOnlyList<CardModel> deck)
        => Enumerable.Range(0, deck.Count)
            .Where(i => deck[i].IsRemovable && !IsNeverRemoved(deck[i])).ToArray();

    // The best removal target and its value inside a given deck, through the
    // same priority the event and rest-site removals use. Non-removable and
    // never-removed cards are skipped outright: a zero-valued invalid candidate
    // must never beat a negative-valued valid one. Protected cards are only
    // reached when nothing unprotected is left.
    internal static (int index, double value) BestRemoval(IReadOnlyList<CardModel> deck, Player player)
    {
        var legal = LegalRemovalIndices(deck);
        if (legal.Length == 0) return (-1, 0);
        var pool = RemovalPlan.PriorityPool(legal.Select(i => deck[i]).ToList(), deck, out _);
        var bestIndex = -1;
        var bestValue = 0.0;
        foreach (var card in pool)
        {
            var legalPosition = Array.FindIndex(legal, i => ReferenceEquals(deck[i], card));
            if (legalPosition < 0) continue;
            var index = legal[legalPosition];
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
    // (affordability and item threshold) on the projected state, so a pair
    // cannot win on combined arithmetic the driver itself would refuse to
    // execute. Its value is the freshly recomputed second value (no MawBank fee).
    internal static bool SecondExecutable(PurchaseComparison.Offer second, double secondValue, Player player,
        int goldAfterFirst, int projectedPotionCount)
    {
        if (second.Cost > goldAfterFirst) return false;
        if (second.IsPotion && projectedPotionCount >= player.MaxPotionCount) return false;
        return secondValue > second.Cost * PriceFactor(player, goldAfterFirst);
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
                CardGold(card.CreationResult.Card, player, hypothetical),
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
            or "EnergyPotion" or "SwiftPotion" or "VulnerablePotion"
            // The two owner-whitelisted permanent/survival bottles must not be
            // filtered as "unknown" when the policy does allow them.
            or "FruitJuice" or "FairyInABottle";
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
                        && Building.BuildValue.AddForDecision(c.CreationResult.Card, player).Total > 0 => e.Cost,
                    MerchantCardRemovalEntry when player.Deck.Cards.Any(c => c.IsRemovable && c.Type == CardType.Curse && !IsNeverRemoved(c))
                        || BestRemoval(player.Deck.Cards, player).value > 0 => e.Cost,
                    MerchantPotionEntry p when p.Model is not null && player.HasOpenPotionSlots && PotionValue(p.Model, player) > 0 => e.Cost,
                    _ => 0,
                });
            return Math.Min(remaining, usefulStock * .5) + 35;
        }
        return Math.Max(0, HumanCoopAdvisor.RelicValue(relic, player).Score) * 7;
    }
}
