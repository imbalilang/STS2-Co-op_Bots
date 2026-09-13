using MegaCrit.Sts2.Core.Combat;

namespace CoopBots.Kernel.Vendor;

/// <summary>
/// 搜索开始时的完整可见状态文本，用于拒绝已经过期的后台结果；不做摘要或哈希。
/// </summary>
internal sealed record LiveCombatStamp(string StateText)
{
    public static LiveCombatStamp Capture(CombatState state)
        => new(ContinuationStamp.CaptureLive(state).StateText);

    public static LiveCombatStamp FromContinuation(ContinuationStamp continuation)
        => new(continuation.StateText);
}
