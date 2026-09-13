using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.Common;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks;

internal sealed class RupturePredictionState : IPredictionStateForkable
{
    public Dictionary<CardModel, int> StrengthByCard { get; private set; } = [];

    public object Fork(PredictionForkContext context)
        => new RupturePredictionState
        {
            StrengthByCard = new Dictionary<CardModel, int>(StrengthByCard),
        };
}
