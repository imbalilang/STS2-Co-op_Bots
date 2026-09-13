namespace CoopBots.Kernel.Vendor.Engine.Common;

internal enum PredictionRiskReason
{
    MethodNotMirrored,
    MethodMirrorIncomplete,
    UnresolvedPlayerChoice,
    CardDrawLimitExceeded,
    OrbChannelLimitExceeded,
}

internal class PredictionRisk(bool hasRisk)
{
    public static PredictionRisk None { get; } = new(false);

    public virtual bool HasRisk { get; } = hasRisk;
}
