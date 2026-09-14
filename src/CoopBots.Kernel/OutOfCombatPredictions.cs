using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.RandomForeseer.Common.HoverTips;
using CoopBots.Kernel.Vendor.RandomForeseer.OutOfCombat;

namespace CoopBots.Kernel;

/// <summary>
/// Out-of-combat RNG predictions, ported from Random Foreseer 0.13.14
/// (MIT, copyright (c) 2026 hotwords123; see THIRD_PARTY_NOTICES.md).
///
/// This is a host facade in the same shape as <see cref="KernelSession"/>: it
/// exposes game models and nothing else. Upstream surfaces predictions as
/// HoverTips because it is a UI mod; the plan's consumers need to *reason* about
/// what was predicted, so the tips are unpacked back into models here.
///
/// Every call clones the run's RNG and replays the game's own generator. Real
/// RNG, decks and relic lists are never touched — a prediction must not consume
/// the very roll it is predicting.
/// </summary>
public static class OutOfCombatPredictions
{
    /// <summary>
    /// What a bundle means, so a caller can value it without knowing which relic
    /// produced it. A choice is worth its best member; a transform result is a
    /// replacement, not a gain, and needs the card it replaced to be priced.
    /// </summary>
    public enum BundleKind
    {
        Choice,
        Transform,
        ScrollBoxes,
    }

    public sealed record Bundle(IReadOnlyList<CardModel> Cards, BundleKind Kind);

    /// <summary>
    /// What a prediction resolved to. The shapes are kept apart because they mean
    /// different things to the caller: <see cref="Cards"/> are upgraded or
    /// transformed deck cards, while <see cref="Bundles"/> are sets offered
    /// together (a card reward, a choice of transform results).
    /// </summary>
    public sealed record Effect(
        IReadOnlyList<CardModel> Cards,
        IReadOnlyList<Bundle> Bundles,
        IReadOnlyList<RelicModel> Relics,
        IReadOnlyList<PotionModel> Potions)
    {
        public static Effect Empty { get; } = new([], [], [], []);

        public bool IsEmpty => Cards.Count == 0 && Bundles.Count == 0
            && Relics.Count == 0 && Potions.Count == 0;
    }

    /// <summary>
    /// What picking up this relic will do beyond its printed text — the cards a
    /// Whetstone upgrades, the relic a capsule contains, the cards an Orrery
    /// offers. Empty when the relic has no random component, or when it is one
    /// this port cannot describe.
    /// </summary>
    public static Effect RelicPickup(Player player, RelicModel relic)
    {
        try
        {
            return Unpack(RelicPickupPrediction.GetHoverTips(player, relic));
        }
        catch
        {
            // A prediction that cannot be made is not information; the caller
            // must fall back to whatever it knew before.
            return Effect.Empty;
        }
    }

    /// <summary>
    /// What this event option will actually grant. Upstream reaches the event
    /// model through a Harmony patch on the option button; the caller already
    /// holds the event it is deciding inside, so it is passed in directly.
    /// </summary>
    public static Effect EventOption(EventModel eventModel, EventOption option)
    {
        try
        {
            return Unpack(EventOptionPrediction.GetHoverTips(eventModel, option));
        }
        catch
        {
            return Effect.Empty;
        }
    }

    private static Effect Unpack(IReadOnlyList<IHoverTip> tips)
    {
        var cards = new List<CardModel>();
        var bundles = new List<Bundle>();
        var relics = new List<RelicModel>();
        var potions = new List<PotionModel>();
        foreach (var tip in tips)
        {
            switch (tip)
            {
                case PredictionCardBundleHoverTip bundle:
                    bundles.Add(new Bundle(bundle.Cards, bundle.Kind switch
                    {
                        PredictionCardBundleKind.Transform => BundleKind.Transform,
                        PredictionCardBundleKind.ScrollBoxes => BundleKind.ScrollBoxes,
                        _ => BundleKind.Choice,
                    }));
                    break;
                case PredictionCardHoverTip card:
                    cards.Add(card.PredictedCard);
                    break;
                default:
                    if (!PredictionHoverTipFactory.TryGetModel(tip, out var model)) break;
                    if (model is RelicModel relic) relics.Add(relic);
                    else if (model is PotionModel potion) potions.Add(potion);
                    break;
            }
        }
        return new Effect(cards, bundles, relics, potions);
    }
}
