using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.Events;
using CoopBots.Kernel.Vendor.RandomForeseer.Common.HoverTips;

namespace CoopBots.Kernel.Vendor.RandomForeseer.OutOfCombat.Events;

internal static class TheLegendsWereTruePrediction
{
    public static IReadOnlyList<IHoverTip> GetHoverTips(TheLegendsWereTrue legends, EventOption option)
    {
        return option.TextKey == "THE_LEGENDS_WERE_TRUE.pages.INITIAL.options.SLOWLY_FIND_AN_EXIT"
            ? [.. OutOfCombatPredictionUtils.PredictUniformPotions(legends.Owner!, 1).ToPredictionHoverTips()]
            : [];
    }
}
