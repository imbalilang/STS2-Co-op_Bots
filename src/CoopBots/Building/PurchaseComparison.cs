using MegaCrit.Sts2.Core.Entities.Merchant;

namespace CoopBots.Building;

/// <summary>
/// Bounded single-versus-pair shop ranking. The shop buys at most one thing per
/// visit through the existing driver, which re-plans after the real purchase, so
/// nothing here queues commitments or changes the network message. The pair is a
/// comparison only: it decides which single action to take first.
///
/// The pair is deliberately conservative. Relics are excluded from pairs: their
/// side effects on deck, prices or potion capacity cannot be modelled, and the
/// variable-price ones (MembershipCard, MawBank, The Courier) would additionally
/// assume a discount or restock. They all remain singles. Two removals are never
/// paired; potions must fit remaining slots; and the total must fit the purse.
/// Every single valuation is untouched.
/// </summary>
internal static class PurchaseComparison
{
    // The stock a pair comparison may consider, fixed so the result is
    // deterministic by the inventory's existing entry order.
    internal const int MaxConsidered = 16;
    // A pair must beat the best single by this margin to change the decision.
    internal const double PairMargin = 0.001;

    internal readonly record struct Offer(
        int Index, int RemovalIndex, MerchantEntry Entry, double Value, int Cost,
        bool IsPotion, bool VariablePrice, bool IsRelic = false)
    {
        internal double Net => Value - Cost;
        internal bool IsRemoval => Entry is MerchantCardRemovalEntry;
    }

    /// <summary>
    /// Whether two distinct offers can both be bought in one shop visit without
    /// violating the modelling contract described on the type.
    /// </summary>
    internal static bool Feasible(Offer left, Offer right, int gold, int openPotionSlots)
    {
        // A relic's side effects on the next purchase (deck, prices, capacity)
        // cannot be modelled, so relics remain singles. This also covers the
        // variable-price relics (MembershipCard, MawBank, TheCourier), whose
        // discount/restock could change the second price.
        if (left.IsRelic || right.IsRelic) return false;
        if (left.VariablePrice || right.VariablePrice) return false;
        if (left.IsRemoval && right.IsRemoval) return false;
        var potions = (left.IsPotion ? 1 : 0) + (right.IsPotion ? 1 : 0);
        if (potions > openPotionSlots) return false;
        if (left.Cost + right.Cost > gold) return false;
        return true;
    }

    internal static bool PairBeatsSingle(double pairNet, double singleNet)
        => pairNet > singleNet + PairMargin;
}
