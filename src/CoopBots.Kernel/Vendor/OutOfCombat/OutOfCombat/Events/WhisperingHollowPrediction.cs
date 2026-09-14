using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using CoopBots.Kernel.Vendor.RandomForeseer.Common;
using CoopBots.Kernel.Vendor.RandomForeseer.Common.HoverTips;

namespace CoopBots.Kernel.Vendor.RandomForeseer.OutOfCombat.Events;

internal static class WhisperingHollowPrediction
{
    public static IReadOnlyList<IHoverTip> GetHoverTips(WhisperingHollow whisperingHollow, EventOption option)
    {
        return option.TextKey switch
        {
            "WHISPERING_HOLLOW.pages.INITIAL.options.GOLD" =>
                [.. PredictionUtils.PredictPotionRewards(
                    whisperingHollow.Owner!,
                    2,
                    whisperingHollow.Owner!.PlayerRng.Rewards.Clone()).ToPredictionHoverTips()],
            "WHISPERING_HOLLOW.pages.INITIAL.options.HUG" =>
                [.. PredictHug(whisperingHollow).ToPredictionHoverTips()],
            _ => []
        };
    }

    private static IReadOnlyList<CardModel> PredictHug(WhisperingHollow whisperingHollow)
    {
        return OutOfCombatPredictionUtils.PredictDistinctDeckTransformResults(whisperingHollow.Owner!, whisperingHollow.Rng);
    }
}
