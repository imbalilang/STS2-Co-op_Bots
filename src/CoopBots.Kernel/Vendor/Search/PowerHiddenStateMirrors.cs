using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

/// <summary>
/// 第三方 Power 隐藏状态的登记表：把不在 <c>DynamicVars</c> 里、但影响后续结算的整数状态送进
/// 搜索的状态指纹，并在根捕获时从实机实例把它带进模拟。
/// </summary>
/// <remarks>
/// <para>
/// 状态指纹里 Power 的通用部分（<c>SimulatedCombatState.AddPower</c>）只收 <c>DynamicVars</c>。
/// 原版有一批 Power 把语义状态放在 <c>_internalData</c> 或普通私有字段里，它们走的是另一条路：
/// <c>AddTurnStartStates</c> 按类型 <c>switch</c>，从 <c>StateStore</c> 里的预测状态取一个计数
/// 塞进指纹（虚空形态、硬化外壳、自动机、束缚锁链……）。那个 <c>switch</c> 没有第三方入口。
/// </para>
/// <para>
/// 续用核对尚未覆盖此隐藏状态；通用 Power 字段一致不能证明隐藏状态一致。
/// <b>对搜索去重有害</b>：只在这个状态上不同的两条分支指纹相同，会被当成同一个状态
/// <b>去掉一条</b>。登记方自己读得到那个字段，算出来的数值是对的，但搜索可能把算得对的那条丢了
/// ——所以记一个 <c>Unmirrored</c> 风险标记解决不了问题，红字只是显示，不会让被去重掉的分支回来。
/// </para>
/// <para>
/// 续用核对需要分别读取实机状态和捕获后的预测状态，本入口尚未提供这两侧的追加方法。
/// 登记在初始化期间完成，任何根捕获或后台搜索开始后保持登记表不变。
/// </para>
/// <para>
/// 也正因为克隆会重置 <c>_internalData</c>，靠它保存状态的 Power 必须用
/// <see cref="RegisterRootCapture{TPower}"/> 在根捕获时把实机实例的值搬进
/// <c>simulator.StateStore</c>，此后一律读预测状态，不要再读克隆上的
/// <c>GetInternalData</c>。这正是原版 <c>PowerPredictionStateSupport.CaptureRootState</c> 在做的
/// 事。状态放在普通私有字段里的 Power 不受影响（<c>MemberwiseClone</c> 会带过去），只登记读取
/// 函数就够了。
/// </para>
/// <para>
/// 两个登记表都为空时，两处下游一行都不走，指纹与登记前逐位相同。
/// </para>
/// </remarks>
internal static class PowerHiddenStateMirrors
{
    /// <summary>一个登记槽：名字加读取函数。名字进指纹，所以同一类型内必须唯一。</summary>
    internal readonly record struct Slot(
        string Name,
        Func<CombatPredictionSimulator, PowerModel, long> Read);

    private static readonly Dictionary<Type, Slot[]> Registry = [];

    private static readonly Dictionary<Type, Action<CombatPredictionSimulator, PowerModel, PowerModel>>
        RootCaptures = [];

    public static bool HasAny => Registry.Count > 0;

    public static bool HasAnyRootCapture => RootCaptures.Count > 0;

    /// <summary>
    /// 为一个 Power 类型登记一个进指纹的隐藏状态。同一类型可以登记多个，按名字排序后依次进指纹。
    /// </summary>
    /// <param name="name">
    /// 状态名，同一类型内不得重名。它进指纹，所以改名等于改指纹口径。
    /// </param>
    /// <param name="read">
    /// 读取这个状态。必须是纯读取：会在搜索热路径上被调用很多次，不得有副作用，也不要在里面
    /// 分配。返回值参与状态等价判断，所以只能取决于这个 Power 自己的状态（含它在
    /// <c>StateStore</c> 里的预测状态），不要读别处。
    /// </param>
    public static void Register<TPower>(string name, Func<CombatPredictionSimulator, TPower, long> read)
        where TPower : PowerModel
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(read);
        Slot slot = new(name, (simulator, power) => read(simulator, (TPower)power));
        if (!Registry.TryGetValue(typeof(TPower), out Slot[]? existing))
        {
            Registry[typeof(TPower)] = [slot];
            return;
        }
        foreach (Slot other in existing)
        {
            // 和镜像注册表同一口径：重复登记是错误，不静默覆盖。
            if (string.Equals(other.Name, name, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{typeof(TPower).FullName} 的隐藏状态 {name} 已经登记过。",
                    nameof(name));
            }
        }
        Slot[] merged = [.. existing, slot];
        // 排序放在登记时做一次，下游按数组顺序走，不在热路径上排序。
        Array.Sort(merged, static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        Registry[typeof(TPower)] = merged;
    }

    /// <summary>
    /// 为一个 Power 类型登记根捕获：把实机实例的隐藏状态搬进 <c>simulator.StateStore</c>。
    /// 靠 <c>_internalData</c> 保存状态的 Power 必须登记这一条，因为克隆会把它重置成初值。
    /// </summary>
    /// <param name="capture">
    /// 参数依次是模拟用的可变克隆和实机实例。照原版的形状写成
    /// <c>simulator.StateStore.GetReadOnly(clone, () =&gt; new MyState(original))</c>：键是克隆，
    /// 值从实机实例读。搜索途中新施加的实例不会走这里，它们的 <c>_internalData</c> 本来就是初值，
    /// 预测状态首次取用时按初值起算即可。
    /// </param>
    public static void RegisterRootCapture<TPower>(
        Action<CombatPredictionSimulator, TPower, TPower> capture)
        where TPower : PowerModel
    {
        ArgumentNullException.ThrowIfNull(capture);
        RootCaptures.Add(
            typeof(TPower),
            (simulator, clone, original) => capture(simulator, (TPower)clone, (TPower)original));
    }

    /// <summary>取出这个 Power 的登记槽；没登记过时返回空。</summary>
    public static ReadOnlySpan<Slot> Slots(PowerModel power)
        => Registry.Count == 0
            ? default
            : Registry.TryGetValue(power.GetType(), out Slot[]? slots)
                ? slots
                : default;

    /// <summary>根捕获。<paramref name="clone"/> 与 <paramref name="original"/> 必须同类型。</summary>
    public static void CaptureRootState(
        CombatPredictionSimulator simulator,
        PowerModel clone,
        PowerModel original)
    {
        if (RootCaptures.Count == 0)
            return;
        if (RootCaptures.TryGetValue(clone.GetType(), out var capture)
            && clone.GetType() == original.GetType())
        {
            capture(simulator, clone, original);
        }
    }
}
