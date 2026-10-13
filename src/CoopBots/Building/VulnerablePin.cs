namespace CoopBots.Building;

/// <summary>
/// Enemy-pinned payoffs on the Vulnerable axis: cards that READ the target's
/// Vulnerable stack instead of only contributing to it.
///
/// 上游怎么做：CombatSolver 把 Bully/Dominate/MoltenFist 的行为放在单场战斗的
/// 效果表里（CorePowerSupport），没有"这张牌需要队伍供应"的概念。
/// 我们怎么做：把"读易伤"的牌集中在这一张手工表里，供 <see cref="CardProfile"/>
/// 打 `vulnerable-payoff` 角色、供 <see cref="CoopBots.TeamCoordinator"/> 的
/// payoff 通道按全队供应计价。
/// 为什么手工表：Vulnerable 的供应侧是**机械可读**的（任何带 VulnerablePower
/// 变量的牌 = 施加者，CardProfile 已把它记为 `vulnerable` 角色），所以这里只需要
/// 列供应侧读不出来的读者；<see cref="BakedResources"/> 是脚本生成的
/// （import-spire-codex-resources.py），在该脚本学会这个轴之前不往里手改。
///
/// 判据（卡片模型，非卡面推测）：
///   DOMINATE   先施加自身易伤，然后把目标当前易伤层数换成力量
///              （tests/PatchSmoke/DecisionFixScenarios.cs: "Dominate must cash
///              out all native Vulnerable stacks without a duration cap"）。
///   MOLTEN_FIST 对已有易伤的目标把层数翻倍
///              （Vendor/Prediction/CardEffectSpecRegistry.cs: `Apply<VulnerablePower>(target, vulnerable)`）。
/// 为什么 BULLY 不在这张表里：Bully 已经在牌组模型里读了全队供应
/// （SustainedDeckValue 的 allyVulnerable 只在牌组持有 Bully 时启用，把每回合
/// DamagePerVulnerable 乘上队友施加量）。实测夹具：同一副牌，队友 0 张易伤牌时
/// Add(Bully)=20.8，队友 3 张时 37.3 —— 把 Bully 再放进本通道就是同一份供应
/// 计价两次。本通道只覆盖"牌组模型读不到读者项"的那两张；
/// outputs/party-composition-build-design.md §4.2 点名的三张里，Bully 的机制
/// 已存在（回归见 TeamEconomyScenarios.VulnerablePayoffFollowsPartySupply）。
/// </summary>
internal static class VulnerablePin
{
    internal static readonly string[] Payoffs = ["DOMINATE", "MOLTEN_FIST"];

    internal static bool IsPayoff(string entry)
        => Array.IndexOf(Payoffs, entry) >= 0;
}
