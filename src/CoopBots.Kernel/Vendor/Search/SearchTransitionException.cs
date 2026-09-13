namespace CoopBots.Kernel.Vendor;

internal sealed class SearchTransitionException : Exception
{
    public PlanAction Action { get; }
    public StateFingerprint ParentState { get; }
    public int ParentActionCount { get; }

    public SearchTransitionException(
        PlanAction action,
        StateFingerprint parentState,
        int parentActionCount,
        Exception innerException)
        : base(Describe(action, parentState, parentActionCount), innerException)
    {
        Action = action;
        ParentState = parentState;
        ParentActionCount = parentActionCount;
    }

    private static string Describe(
        PlanAction action,
        StateFingerprint parentState,
        int parentActionCount)
    {
        string source = action.Kind switch
        {
            PlanActionKind.PlayCard => $"card={action.CardId} occurrence={action.CardOccurrence}",
            PlanActionKind.UsePotion => $"potion={action.PotionId} slot={action.PotionSlot}",
            PlanActionKind.EndTurn => "end_turn=true",
            _ => throw new ArgumentOutOfRangeException(nameof(action.Kind), action.Kind, null),
        };
        return $"搜索动作回放失败：turn={action.Turn} action_count={parentActionCount} " +
               $"kind={action.Kind} {source} target={action.TargetCombatId?.ToString() ?? "-"} " +
               $"parent_state={parentState}。";
    }
}
