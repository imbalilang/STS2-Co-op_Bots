namespace CoopBots.Kernel.Vendor;

internal sealed partial class SimulatedCombatState
{
    public TurnStartChoiceRequest? PendingTurnStartChoice { get; private set; }

    public void SetPendingTurnStartChoice(TurnStartChoiceRequest request)
    {
        if (PendingTurnStartChoice is { } pending)
        {
            throw new InvalidOperationException(
                $"模拟状态已经存在待处理的选牌：" +
                $"pending={Describe(pending)} new={Describe(request)}。");
        }
        PendingTurnStartChoice = request;
    }

    public void ClearPendingTurnStartChoice()
        => PendingTurnStartChoice = null;

    /// <summary>
    /// Names the pending turn-start choice for diagnostics (uses the same fields as the
    /// exception message above). Needed because a SteamEruption phase transition leaves one
    /// behind and the death settlement then fails closed — see CorePowerSupport's
    /// `TryTriggerSteamEruptionDeath` call site — so a line that KILLS the boss becomes a
    /// boundary and the search can never see a line that resolves the fight.
    /// </summary>
    public string PendingTurnStartChoiceDescription
        => PendingTurnStartChoice is { } pending ? Describe(pending) : "none";

    private static string Describe(TurnStartChoiceRequest request)
        => $"{request.SourceId}/{request.Effect}/{request.SourcePile}/{request.Count}/{request.ContextId}";
}
