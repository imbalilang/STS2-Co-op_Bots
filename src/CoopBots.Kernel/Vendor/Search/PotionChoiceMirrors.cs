using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

/// <summary>
/// 第三方药水的玩家选择登记表。
/// </summary>
/// <remarks>
/// <see cref="PotionChoiceSupport"/> 的 <c>RequiresChoice</c>、<c>GetSpec</c> 和 <c>Apply</c>
/// 是按原版药水类型写死的开关：<c>RequiresChoice</c> 对第三方类型恒为 <c>false</c>，另外两个的
/// 默认分支直接抛。于是第三方药水的玩家选择永远不会被展开成搜索分支——求解器要么当它没有选择，
/// 要么根本不为它开分支。药水自己的 <c>OnUse</c> 镜像补不了这个：等那个钩子触发的时候，
/// "要不要开分支"早就已经被否决了。
///
/// 登记之后，第三方药水和原版带选择的药水走同一条通道：搜索按登记的
/// <see cref="CardChoiceSpec"/> 展开分支，选中的结果记进计划，部署时照常应答原生选牌页面，
/// 而效果由登记方自己的 <c>apply</c> 施加——求解器不需要认识任何第三方效果。
///
/// 选项牌用 <see cref="PlanChoiceEffect.ModDefined"/>：部署侧按卡牌令牌在原生页面上定位，
/// 本来就与效果无关；这个值只是明确表示"结算由登记方负责"，别的效果分支不会误接手。
/// </remarks>
internal static class PotionChoiceMirrors
{
    private readonly record struct Entry(
        Func<CombatPredictionSimulator, PotionModel, CardChoiceSpec> Spec,
        Func<CombatPredictionSimulator, PotionModel, PlanCardChoice, bool> Apply);

    private static readonly Dictionary<Type, Entry> Registry = [];

    /// <summary>
    /// 为一个药水类型登记玩家选择。
    /// </summary>
    /// <param name="spec">
    /// 给出这次选择的候选与上下界。候选必须是玩家在原生页面上真正看到的那几张，顺序也要一致，
    /// 否则部署时按卡牌令牌定位会错位。下界给 0 表示"可以一张都不选"。
    /// </param>
    /// <param name="apply">
    /// 按选中的结果在模拟里施加效果，返回是否已经结算完（还有嵌套选择挂起时返回 <c>false</c>，
    /// 和原版同一口径）。
    /// </param>
    public static void Register<TPotion>(
        Func<CombatPredictionSimulator, TPotion, CardChoiceSpec> spec,
        Func<CombatPredictionSimulator, TPotion, PlanCardChoice, bool> apply)
        where TPotion : PotionModel
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(apply);
        // 和镜像注册表同一口径：重复登记是错误，不静默覆盖。
        Registry.Add(
            typeof(TPotion),
            new Entry(
                (simulator, potion) => spec(simulator, (TPotion)potion),
                (simulator, potion, choice) => apply(simulator, (TPotion)potion, choice)));
    }

    /// <summary>这个药水类型登记过选择没有。</summary>
    public static bool RequiresChoice(PotionModel potion)
        => Registry.Count > 0 && Registry.ContainsKey(potion.GetType());

    public static bool TryGetSpec(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        out CardChoiceSpec spec)
    {
        if (Registry.Count > 0 && Registry.TryGetValue(potion.GetType(), out Entry entry))
        {
            spec = entry.Spec(simulator, potion);
            return true;
        }
        spec = null!;
        return false;
    }

    public static bool TryApply(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        PlanCardChoice choice,
        out bool completed)
    {
        if (Registry.Count > 0 && Registry.TryGetValue(potion.GetType(), out Entry entry))
        {
            completed = entry.Apply(simulator, potion, choice);
            return true;
        }
        completed = false;
        return false;
    }
}
