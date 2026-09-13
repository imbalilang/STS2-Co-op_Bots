using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

/// <summary>
/// 第三方卡牌的玩家选择登记表。
/// </summary>
/// <remarks>
/// <see cref="CardChoiceSupport.GetSpec"/> 是按原版卡牌类型写死的 <c>switch</c>，默认分支返回
/// <c>null</c>——也就是"这张牌没有选择"。第三方卡牌落到那里就是这个答案，于是它的选牌效果
/// 永远不会被展开成搜索分支：牌照样打得出去，效果在模拟里静默变成空操作。卡牌自己的
/// <c>CardOnPlayMirrors</c> 补不了这个，因为选择的展开发生在出牌路径上、不在效果镜像里。
///
/// 登记之后，第三方卡牌和原版带选牌的卡牌走同一条通道：出牌时求解器照常调
/// <c>ResolveManualCardChoice</c>，拿到登记的 <see cref="CardChoiceSpec"/> 展开分支，选中的结果
/// 记进计划，部署时照常应答原生选牌页面，而效果由登记方自己的 <c>apply</c> 施加——求解器不需要
/// 认识任何第三方效果。
///
/// 选项牌用 <see cref="PlanChoiceEffect.ModDefined"/>，和药水那条
/// （<see cref="PotionChoiceMirrors"/>）同一个约定。
/// </remarks>
internal static class CardChoiceMirrors
{
    private readonly record struct Entry(
        Func<CombatPredictionSimulator, PredictedCard, CardChoiceSpec> Spec,
        Func<CombatPredictionSimulator, SimulatedCombatState, PredictedCard, PlanCardChoice, bool> Apply);

    private static readonly Dictionary<Type, Entry> Registry = [];

    /// <summary>
    /// 为一个卡牌类型登记玩家选择。
    /// </summary>
    /// <param name="spec">
    /// 给出这次选择的候选与上下界。候选必须是玩家在原生页面上真正看到的那几张，顺序也要一致，
    /// 而且**升级等级要对**——部署时按 CardId 加升级等级在页面上定位，选项牌跟着源牌一起升级的
    /// 效果（例如三选一的三张选项都随本牌升级）必须在这里如实反映出来，否则定位不到。
    /// 下界给 0 表示"可以一张都不选"。
    /// </param>
    /// <param name="apply">
    /// 按选中的结果在模拟里施加效果，返回是否已经结算完（还有嵌套选择挂起时返回 <c>false</c>，
    /// 和原版同一口径）。选项牌不在任何模拟牌堆里，所以传进来的是计划里的令牌本身，
    /// 由登记方按 CardId 自己认；求解器不会替你把令牌解析成牌。
    /// </param>
    public static void Register<TCard>(
        Func<CombatPredictionSimulator, PredictedCard, TCard, CardChoiceSpec> spec,
        Func<CombatPredictionSimulator, SimulatedCombatState, PredictedCard, TCard, PlanCardChoice, bool> apply)
        where TCard : CardModel
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(apply);
        // 和镜像注册表同一口径：重复登记是错误，不静默覆盖。
        Registry.Add(
            typeof(TCard),
            new Entry(
                (simulator, playedCard) => spec(simulator, playedCard, (TCard)playedCard.Preview),
                (simulator, combat, playedCard, choice) =>
                    apply(simulator, combat, playedCard, (TCard)playedCard.Preview, choice)));
    }

    public static bool TryGetSpec(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        out CardChoiceSpec spec)
    {
        if (Registry.Count > 0
            && Registry.TryGetValue(playedCard.Preview.GetType(), out Entry entry))
        {
            spec = entry.Spec(simulator, playedCard);
            return true;
        }
        spec = null!;
        return false;
    }

    public static bool TryApply(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        PlanCardChoice choice,
        out bool completed)
    {
        if (Registry.Count > 0
            && Registry.TryGetValue(playedCard.Preview.GetType(), out Entry entry))
        {
            completed = entry.Apply(simulator, combat, playedCard, choice);
            return true;
        }
        completed = false;
        return false;
    }
}
