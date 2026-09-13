using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors;

internal abstract class CombatMirrorContext<TBase> : IMethodMirrorContext<TBase>
    where TBase : AbstractModel
{
    public required CombatPredictionSimulator Simulator { get; init; }

    public CombatPredictionState State => Simulator.State;

    public CombatPredictionRngSet Rng => Simulator.Rng;

    public PredictionStateStore StateStore => Simulator.StateStore;

    public CombatPredictionHistory History => Simulator.History;

    public ICombatState CombatState => Simulator.State.CombatState;

    public CardMultiplayerConstraint CardMultiplayerConstraint
        => CombatState is ICombatPredictionRunSnapshot snapshot
            ? snapshot.CardMultiplayerConstraint
            : throw new InvalidOperationException("Combat prediction requires a captured run snapshot.");

    protected virtual AbstractModel GetDispatchSource(TBase receiver) => receiver;

    PredictionTrace.TraceScope IMethodMirrorContext<TBase>.PushDispatchSource(
        TBase receiver,
        MirrorMethodSpec method)
    {
        return Simulator.PushMethodSource(GetDispatchSource(receiver), method);
    }

    void IMethodMirrorContext<TBase>.RecordMethodNotMirroredRisk()
    {
        History.RecordRisk(PredictionRiskReason.MethodNotMirrored);
    }

    void IMethodMirrorContext<TBase>.RecordMethodMirrorIncompleteRisk()
    {
        History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
    }
}

internal abstract class CombatMirrorContext : CombatMirrorContext<AbstractModel>;
