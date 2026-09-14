using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.Events;
using CoopBots.Kernel.Vendor.RandomForeseer.Common.HoverTips;

namespace CoopBots.Kernel.Vendor.RandomForeseer.OutOfCombat.Events;

internal static class WellspringPrediction
{
    public static IReadOnlyList<IHoverTip> GetHoverTips(Wellspring wellspring, EventOption option)
    {
        return option.TextKey == "WELLSPRING.pages.INITIAL.options.BOTTLE"
            ? [.. OutOfCombatPredictionUtils.PredictUniformPotions(wellspring.Owner!, 1).ToPredictionHoverTips()]
            : [];
    }
}
