using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

// Native OnPlay conditions, shared by combat ordering and permanent drafting.
// BubbleBubble adds poison only to an already poisoned target. Merely having
// PoisonPower in DynamicVars does not make it an independent poison source.
public static class CardPoisonEffects
{
    public static bool RequiresPoison(CardModel card) => card.GetType().Name == "BubbleBubble";

    public static double DirectAmount(CardModel card)
    {
        if (card.GetType().Name is not ("BubbleBubble" or "Snakebite" or "DeadlyPoison" or "PoisonedStab" or "Outbreak")) return 0;
        return card.DynamicVars.TryGetValue("PoisonPower", out var amount) ? (double)amount.BaseValue : 0;
    }

    public static bool CanSeedPoison(CardModel card)
        => !RequiresPoison(card) && (DirectAmount(card) > 0
            || card.GetType().Name is "NoxiousFumes" or "Envenom" or "Concoct" or "BouncingFlask");

    // OUR ordering estimate: two future poison ticks, capped by the target HP.
    // The actual simulator remains authoritative, including Artifact and hooks.
    public static double TwoTicks(double amount) => amount + Math.Max(0, amount - 1);
}
