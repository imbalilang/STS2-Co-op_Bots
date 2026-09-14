using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.Events;
using CoopBots.Kernel.Vendor.RandomForeseer.Common;
using CoopBots.Kernel.Vendor.RandomForeseer.Common.HoverTips;

namespace CoopBots.Kernel.Vendor.RandomForeseer.OutOfCombat.Events;

internal static class DoorsOfLightAndDarkPrediction
{
    public static IReadOnlyList<IHoverTip> GetHoverTips(DoorsOfLightAndDark doors, EventOption option)
    {
        return option.TextKey == "DOORS_OF_LIGHT_AND_DARK.pages.INITIAL.options.LIGHT"
            ? [.. OutOfCombatPredictionUtils.PredictUpgradedDeckCards(
                doors.Owner!,
                2,
                card => card.IsUpgradable,
                doors.Rng.Clone()).ToPredictionHoverTips()]
            : [];
    }
}
