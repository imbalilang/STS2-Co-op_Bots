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

    private static string Describe(TurnStartChoiceRequest request)
        => $"{request.SourceId}/{request.Effect}/{request.SourcePile}/{request.Count}/{request.ContextId}";
}
