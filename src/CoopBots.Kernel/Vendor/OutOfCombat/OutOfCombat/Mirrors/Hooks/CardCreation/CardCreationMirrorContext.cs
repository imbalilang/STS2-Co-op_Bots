using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.RandomForeseer.Common;
using CoopBots.Kernel.Vendor.RandomForeseer.Common.Mirrors;

namespace CoopBots.Kernel.Vendor.RandomForeseer.OutOfCombat.Mirrors.Hooks.CardCreation;

internal abstract class CardCreationMirrorContext : IMethodMirrorContext<AbstractModel>
{
    private readonly PredictionTrace _trace = new();

    public required RunPredictionContext RunContext { get; init; }

    public required List<CardCreationResult> Results { get; init; }

    public Player Player => RunContext.Player;

    public bool HasRisk { get; private set; }

    IDisposable IMethodMirrorContext<AbstractModel>.PushDispatchSource(
        AbstractModel model,
        MirrorMethodSpec method)
    {
        return _trace.Push(model, PredictionInvocation.ForMethod(method.BaseMethod));
    }

    void IMethodMirrorContext<AbstractModel>.RecordMethodNotMirroredRisk()
    {
        HasRisk = true;
    }

    void IMethodMirrorContext<AbstractModel>.RecordMethodMirrorIncompleteRisk()
    {
        HasRisk = true;
    }
}
