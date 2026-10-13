using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace CoopBots.Kernel;

/// <summary>
/// 「可以优先打」的爪牙标注。
///
/// 上游/改前现状：内核的局部估值与裁剪只读主要敌人（<c>KernelSession.IsPrimaryEnemy</c>，
/// 胜利用的 <c>RemainingOpposition</c> 也只统计主要敌人）。爪牙的血量对搜索一文不值，
/// 于是攻击它的分支在 beam 里严格劣于攻击主要敌人、永远进不了候选。
/// 2026-10-05 的四人 A10 女王战（seed PQWEBGY4ZTGQ，日志
/// <c>work/live-triage-20261005-final/godot.log</c>）里，全队 11 回合没有一次以
/// 火炬头聚合体为目标，四席的致命伤全部来自它（女王每回合 BURN_BRIGHT_FOR_ME
/// 给它 +1 力量）；最终女王还剩 372 血 + 37 格挡，全灭。
///
/// 我方行为（owner 2026-10-05 指示「给 Queen 的爪牙标注一下，允许 Bot 先打爪牙」）：
/// 把这个爪牙标成一条**被保护、可选的策略线**——搜索会保留一条「打爪牙」的路线，
/// 由 <c>RolloutPolicyKind.KillAdds</c> 政策把它打完，终局比较再决定它是否优于对拼
/// 主要敌人。标注本身不改变默认目标优先级、不改变胜利条件（RemainingOpposition 仍然
/// 只认主要敌人），所以「不先打爪牙」的行为与改前一致。
///
/// 为什么是火炬头聚合体：女王每回合给它 +1 力量，它是这场战斗的持续伤害来源；
/// 击杀它会让女王切换到 OFF_WITH_YOUR_HEAD_MOVE（见
/// <c>Vendor/Prediction/BranchMonsterAi.cs</c> 与 <c>AfterDeathMirrors.HandleQueen</c>）。
/// </summary>
public static class KernelPriorityAdds
{
    /// <summary>被标注为「可以先打」的爪牙。</summary>
    public static bool IsPriorityAdd(Creature? enemy) => enemy?.Monster is TorchHeadAmalgam;

    /// <summary>
    /// 标注爪牙在「战斗进度」估值里的权重：半价。胜利条件仍只认主要敌人
    /// （<c>RemainingOpposition</c> 不动），所以打爪牙永远不能替代打主怪；
    /// 半价只是让「先打爪牙」这条线在搜索里可被比较。
    /// </summary>
    public const double SecondaryProgressWeight = 0.5;

    /// <summary>
    /// 爪牙当前是否在滚雪球：它每回合从在场女王的 BURN_BRIGHT_FOR_ME_MOVE 拿 +1 力量
    /// （镜像见 <c>Vendor/Prediction/MonsterMoveEffects.cs</c>；女王死后该招式切换为
    /// OFF_WITH_YOUR_HEAD，聚合体不再成长）。没有女王时不能凭空计入成长。
    /// </summary>
    public static bool ScalesEachRound(IEnumerable<Creature> enemies, Creature claw)
        => IsPriorityAdd(claw) && enemies.Any(e => e is { Monster: Queen, IsAlive: true });

    /// <summary>
    /// 路线对爪牙的推进度量：爪牙剩余总血量，越小越接近击杀。
    /// 爪牙不在场（已死/未生成）时返回 0，表示这条线已经完成。
    /// </summary>
    internal static int RemainingAddHp(KernelSession state)
    {
        var total = 0;
        foreach (var enemy in state.Enemies)
            if (IsPriorityAdd(enemy))
                total += Math.Max(0, state.Hp(enemy));
        return total;
    }
}
