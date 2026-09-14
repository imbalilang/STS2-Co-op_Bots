using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using CoopBots.Kernel.Vendor.RandomForeseer.Common.HoverTips;

namespace CoopBots.Kernel.Vendor.RandomForeseer.OutOfCombat.Events;

internal static class AromaOfChaosPrediction
{
    public static IReadOnlyList<IHoverTip> GetHoverTips(AromaOfChaos aromaOfChaos, EventOption option)
    {
        return option.TextKey == "AROMA_OF_CHAOS.pages.INITIAL.options.LET_GO"
            ? [.. PredictLetGo(aromaOfChaos).ToPredictionHoverTips()]
            : [];
    }

    private static IReadOnlyList<CardModel> PredictLetGo(AromaOfChaos aromaOfChaos)
    {
        return OutOfCombatPredictionUtils.PredictDistinctDeckTransformResults(aromaOfChaos.Owner!, aromaOfChaos.Rng);
    }
}
