using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel.Vendor;

/// <summary>
/// 第三方牌的移除估值偏置登记表。
/// </summary>
/// <remarks>
/// <para>
/// 消耗、转变这类<b>移除</b>选择按 <c>CardChoiceSupport.RemovalPriority</c> 从低到高排序，估值低的
/// 先被移除；<c>ChoicePriority</c> 对消耗返回 <c>-Σ RemovalPriority</c> 并按降序取分支，所以估值
/// <b>为负</b>的牌会让「选它」这条分支排在「一张都不选」之前——也就是从「少亏一点」变成「值得烧」。
/// </para>
/// <para>
/// 通用估值 <c>CardValue</c> 只读 <c>Damage</c>、<c>Block</c>、<c>Cards</c> 三个变量加一个 Power
/// 加成。价值落在「它让你能做什么」上的牌，牌面上这三个变量往往是空的，于是估值为 <c>0</c>，
/// 排在最前面先被烧；而一张 6 伤害的起手打击是 <c>6.0</c>，反而留了下来。原版靠
/// <c>BasicCardRemovalValue</c> 里一张按类型写死的表把十张起手牌压回正确位置，那张表只列原版，
/// 注释里写明理由是「其他来源的打击、防御强弱取决于各自的机制，这里没有依据替它们排序」。
/// </para>
/// <para>
/// 这个判断对求解器成立，对 Mod 作者不成立——<b>他知道自己那张牌在自己这套体系里值多少</b>。所以
/// 这里开一个登记点，让他给一个<b>相对通用估值的偏置</b>：
/// </para>
/// <code>
/// CardRemovalValueMirrors.Register&lt;YourStrike&gt;(-10d);
/// CardRemovalValueMirrors.Register&lt;YourDefend&gt;(-10d);
/// </code>
/// <para>
/// 登记的是偏置而不是绝对值，是为了保住牌自身的梯度：升级过的起手打击伤害更高，加同一个偏置之后
/// 仍然比未升级的那张更靠后被烧。角色之间的相对顺序也由此自然落下来——通用估值把格挡打了八折，
/// 所以同样偏置下防御排在打击前面被烧；原版五个角色相反，那是那张写死的表在起作用，与本入口无关。
/// </para>
/// <para>
/// <b>这个入口不怕被滥用。</b>把自己的牌估低等于让求解器优先烧掉它，估高等于让它留在牌库里堵手，
/// 两个方向的代价都由登记方自己承担，没有可以占的便宜。
/// </para>
/// <para>
/// <b>负偏置还有第二个作用：那张牌按牌库杂质计。</b>状态牌和诅咒本来就进 <c>liveDeckClutter</c>，
/// 只要还占着牌堆就扣分，消耗掉它们因此是正收益。别的牌不进那一项，于是消耗一张非状态非诅咒的牌
/// 在打分里的收益<b>正好是零</b>（<c>retainedAttackValue</c> 有上限，攻击牌多的时候早就顶满，
/// 少一张也不掉），「打出净化消耗两张废牌」严格劣于「不打净化」。排序偏置排不出一个本来就没有的
/// 收益，所以负偏置同时表示「这张牌占着牌堆就是负担」。
/// </para>
/// <para>
/// 原版那张写死的表优先：已经列进去的类型不会被登记表改写。登记表为空时下游一行都不多走，排序与
/// 开这个口子之前逐位相同。登记在初始化期间完成，任何搜索开始后保持登记表不变。
/// </para>
/// </remarks>
internal static class CardRemovalValueMirrors
{
    /// <summary>偏置的绝对值上限。够表达「这张牌白占位置」，又不至于一次手滑让求解器烧光牌库。</summary>
    private const double MaximumOffsetMagnitude = 100d;

    private static readonly Dictionary<Type, double> Registry = [];

    /// <summary>登记表是否为空。空表时下游可以整段跳过。</summary>
    public static bool IsEmpty => Registry.Count == 0;

    /// <summary>
    /// 给一张牌登记移除估值偏置。最终估值是<b>通用估值加这个偏置</b>。
    /// </summary>
    /// <param name="removalValueOffset">
    /// 负数表示「比通用估值更该先烧」，取到负值之后这张牌会让「烧它」这条分支排在「一张都不选」
    /// 之前。正数表示「更该留」。绝对值不得超过 <c>100</c>。
    /// </param>
    public static void Register<TCard>(double removalValueOffset)
        where TCard : CardModel
    {
        if (!double.IsFinite(removalValueOffset)
            || Math.Abs(removalValueOffset) > MaximumOffsetMagnitude)
        {
            throw new ArgumentOutOfRangeException(
                nameof(removalValueOffset),
                removalValueOffset,
                $"移除估值偏置必须是绝对值不超过 {MaximumOffsetMagnitude} 的有限数。");
        }
        // 和别的镜像登记表同一口径：重复登记是错误，不静默覆盖。
        Registry.Add(typeof(TCard), removalValueOffset);
    }

    /// <summary>
    /// 撤销一次登记。<b>只给无人测试用</b>：测试要自己登记再清干净，否则同一个进程里后面的
    /// 用例都会被影响。真实 Mod 不要调用，更不要在搜索进行中调用。
    /// </summary>
    internal static void UnregisterForTesting<TCard>()
        where TCard : CardModel
        => Registry.Remove(typeof(TCard));

    /// <summary>取这张牌登记的偏置；没登记过时返回 <c>null</c>。</summary>
    public static double? Offset(CardModel card)
        => Registry.Count == 0
            ? null
            : Registry.TryGetValue(card.GetType(), out double offset)
                ? offset
                : null;
}
