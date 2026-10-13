# 生产构筑启发式代码审查（HEURISTIC_REVIEW）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


## Astra 验收与最终优先级

以下复核结论优先于后文初审建议。当前只有静态审查证据，未运行案例，不能宣称已证明实战收益。

1. **P0：永久变更调用一致性。** `BotBrain.Permanent:94-100` 只按分数排序，`BuildValue.Remove:277` 的 protected=0 不是排除条件；当其他删牌分为负时保护卡仍可能入选。多张移除也只对原牌组算一次，可能同时删除原先各有两张的同一关键功能。应显式区分可选/被保护，并逐张在更新后的假设牌组中重评；强制选择无法避开保护时需明确兜底，不能返回不足数量造成界面卡住。变形当前使用候选列表作为整个牌组上下文（BotBrain:89），应改用完整实际牌组。
2. **P1：降低软提示的放大效应。** `BuildValue.Add:224-229` 中任意正 affinity/route/gap 都令体积惩罚倍率切换为0.4。以21张牌、actIndex=2、无后续商店为例，惩罚从30×2.025=60.75变为12，仅开关就相差48.75，远超 affinity 本身6分上限。应将真实功能缺口、机制支持与共现提示分开，按证据强度连续减免；`needs-power` 也不能把少于3张能力牌当作所有构筑的必需缺口。此为策略设计问题，先建立正反案例，不能直接猜新权重。
3. **P1：机制依赖替代共现依赖。** 保留 affinity 为小幅先验，不把它当触发条件。不能豁免所有 Resource.Producers：`BakedResources.cs:26-27` 中 RUPTURE 同时是 strength producer 和 hp-loss converter。显式依赖优先于资源产出标签，未知机制不凭共现断言“不受支持”。
4. **P2：补全升级差分。** 除上下文缺失外，`Facts` 已有 HpLoss，但 `UpgradeDiff` 没有该差值；`UpgradeDelta` 也统一按 keyword增减计±4，没有区分有益/有害关键字。先确认当前游戏中对应升级样本，再补正确方向与真实差分；不能规定“唯一角色一定比重复角色更值得升级”。
5. **P2：复核多人加分、功能画像与其他入口。** 人类建议/机器人评分尺度不同，不能直接复制1.5倍上限。单体易伤/虚弱也可能使全队受益，不能按 AllEnemies 一刀切。非 FromDeck 卡牌选择也可能是临时战斗效果，未确认调用语义前不能统一转成永久加牌。画像里的未知能力需要显式记录，不能把任意 CardsVar 当抽牌。性能缓存只在单次决策内按实例及状态保存，不能按ID+IsUpgraded合并不同属性/升级等级的牌。

建议首轮实施范围：移除保护的调用落实、多张移除逐步重评、变形完整牌组上下文。复用 `tests/PatchSmoke/BuildValueScenarios.cs` 增加入口级确定性回归；后续单独处理选牌软提示放大。无大规模战斗、训练或云资源。

初审事实修正：CookValue 只是营火选项估值，不直接执行删除；实际删除目标需继续追踪 CardSelect 回调，不能把其估值候选当作已删除目标。Analyze 已有 CalculatedHits 兜底，多段识别并非完全依赖卡名。Archetypes 对已识别强路线的不匹配牌有负分，类注释“只加不减”已过时。

- 文档性质：**只读代码审查**。不改生产代码、不跑战斗/训练/云、不做实验。
- 编制日期：2026-09-18
- 范围：`src/CoopBots/Building/*.cs`、`BotBrain`、`BotShopPlanner`、`TeamCoordinator`、`RestSitePatches`、`RunDepth`、`HumanCoopAdvisor`、`CardChoicePatches`、`RewardPatches`，以及相关测试/探针。巨型挖掘表只读结构与命中行，不整表通读。
- 标记：**【实现】**=当前工作树可直接读到的行为；**【推测】**=无法在离线代码中证实的实战后果；**【未知】**=卡面机制未在本报告核对，需另证。
- 上游：[`BASELINE.md`](BASELINE.md)、[`NEXT_STEPS.md`](NEXT_STEPS.md)。

---

## 零、结论摘要（按价值排序）

| # | 问题 | 证据锚点 | 类型 |
| - | --- | --- | --- |
| 1 | CONCOCT 修复（team 上限）只落在人类建议路径，机器人奖励/商店路径的 `TeamBonus` 仍未加上限 | `BuildValue.cs:156-161` vs `TeamCoordinator.cs:39,44-58` | 实现不一致 |
| 2 | `UnsupportedScaling` 把「共同被抽到」的 affinity 当作「触发器缺失」证据，会误伤自给型 scaling | `BuildValue.cs:411-416`、`BakedResources.cs:26`、`BakedCardAffinity.cs:247` | 语义错配 |
| 3 | `CardProfile` 的 draw/energy/multihit 语义依赖硬编码卡名白名单，与「数据自派生、模组同判」的设计声明矛盾 | `CardProfile.cs:52-53,73`、`GeniusCombatStrategy.cs:18-46` | 画像语义 |
| 4 | Cook 按 `HumanCoopAdvisor.CardValue` 排行删牌，绕过 `BuildValue.Remove` 的保底保护 | `RestSitePatches.cs:281-285`、`BuildValue.cs:277,488-497` | 多套估值 |
| 5 | `TeamBonus` 用卡名硬编码判定「全队易伤/虚弱铺垫」 | `TeamCoordinator.cs:79-80` | 作者化数据 |
| 6 | `UpgradeDelta` 与牌组/上下文无关，且升级异常被吞成「不可升级」 | `BuildValue.cs:293-295`、`CardProfile.cs:113-129` | 升级估值 |
| 7 | `CardSelectCmd` 中非 `FromDeck*` 的加牌屏走 `ScoreDeckCard`（第三套评分、无稀释/体积/资源门） | `BotBrain.cs:79,138-143,193-222` | 上下文一致性 |
| 8 | 无缓存，奖励/商店一次估值触发 O(n²) 次 `Analyze`（含动态变量预览） | `CardProfile.cs:42-107`、`BuildValue.cs:191,204,262-264` | 性能【推测】 |

---

## 一、当前已存在的启发式清单（现状，不评价）

### 1.1 BuildValue（`src/CoopBots/Building/BuildValue.cs`）

- 常量：`Base=8`、`DamageWeight=1.2`、`BlockWeight=1.0`、`DamageCap=55`、`DrawWeight=4`、`EnergyWeight=8`、`DebuffWeight=3`、`ScalingWeight=7`、`DoomWeight=3`、`DuplicatePenalty=5`（`:20-32`）。
- 稀有度加成 Rare+10 / Uncommon+4（`:384-389`）；费用项 `(3-min(3,cost))*2`（`:147`）；自伤 `HpLoss*2.5`（`:148`）；已升级 +3（`:149`）；无输出 Exhaust -6（`:150`）。
- 角色饱和 `Decay`：<4→1.0，<7→0.7，其余 0.4（`:370`）；取该项相关角色中最差者 `RoleWeight`（`:373-382`）。
- 缺口加成 `GapBonus`：needs-block 14 / needs-damage 10 / needs-power 12 / needs-aoe 8 / needs-draw 6 / needs-energy 6（`:460-482`）。
- 加牌 `Add`：`Marginal - 稀释 - 体积压力`；稀释 `max(0,avg-own)*(5/(n+1))`（`:205`）；体积 `Oversize`：`FormingSize=15`、`SizePressure=5`、`SizePressureCap=30`、`OnPlanRelief=0.4`（`:242-253,229`）。
- 删牌 `Remove`：`max(-own+relief, starter)`；`StarterRemovalBaseline=55`、`StarterPressure` 上限 2.5（`:32-43,259-286`）；保底保护 role ∈{block,aoe,energy,draw} 的最后一枚（`:488-497`）。
- 升级 `UpgradeDelta`：`CostDelta*14`、伤害/格挡/抽牌/能量/debuff/scaling/doom 正差分、`AddedRoles*6`、丢 exhaust +7、其余丢 role -6、keyword ±4（`:297-312`）。
- 星资源门：`SpendsStars && 牌组无 producer → value 0, "unplayable:no-star"`（`:62-78,100`）。
- `UnsupportedScaling`：命中 affinity 但伙伴全不在牌组 → scaling ×0.4（`:130-138,411-416`）；Dexterity 在 ≥3 张 block 时豁免（`:434-436`）。
- `Affinity`：`min(6.0, per-lift 1.2 × min(lift,2.5) × ≤2 个伙伴)`（`:401-404,438-451`）。
- `BestReward`：`allowSkip` 默认 true；阈值隐式为 0（`:331-346`）。

### 1.2 CardProfile / 资源表（`CardProfile.cs`、`BakedResources.cs`）

- `Roles` 含 damage/aoe/block/draw/energy/vulnerable/weak/strengthdown/poison/doom/scaling/power/exhaust/strength/dexterity/focus/multihit/retain/zerocost/is-shiv/produces-shiv/buffs-shiv/variant-type/minion/osty（`CardProfile.cs:21-34`）。
- 诅咒/状态/Unplayable 直接 -1000（`BuildValue.cs:92-93`）；zerocost 需非 unplayable（`CardProfile.cs:68-70`）。
- 小刀三拆（is- / produces- / buffs-shiv）由 `BakedResources` 手工校白表判据（`CardProfile.cs:84-91`；`BakedResources.cs:21`）。
- MadScience 三类变体取并集并打 `variant-type`（`CardProfile.cs:92-96`）。

### 1.3 Archetypes（`Archetypes.cs`）

- 挖掘簇优先（≥2 张 signature，`:138-172`），否则角色路线表（`:27-47`）。
- `Bonus`：signature +14×strength、role +8×strength×decay，rank1 权重 0.5 且需 `Compatible`；不合路线且 strength>0.5 时 -4-2×(hits-2)（`:99-110,116-117`）。

### 1.4 调用方启发式

- `BotShopPlanner`：`GoldPerDeckValue=2.2`（`:25`）、阈值 `RunDepth.ShopThresholdFactor`（`:90`）、保留 25 金（`:86`）、MawBank 机会成本 -24（`:82`）、末店未定价遗物地板（`:77-80`）、诅咒删牌地板 `155×BloatFactor`（`:42-44`）、MembershipCard 回本（`:110-128`）。
- `RestSitePatches`：`SmithHpFloor` 1-2 幕 0.25、3 幕 0.55/末营 0.85（`:232-233`）；`RiskFor`/`RiskForCamp`（`:220-260`）；`HealValue` NeedAtFull1→NeedAtDeath3（`:71-72,266-274`）；`UpgradeScale=2.5`、`WorthwhileUpgrade=2.5`（`:78,241`）；Mend 紧急线 0.25（`:244`）；Cook/DIG/LIFT/HATCH/KINDLE/CLONE（`:162-179,276-286`）。
- `RunDepth`：`BloatFactor=(1+0.25act)×(shopAhead?1:1.35)`、`ShopThresholdFactor` 0.35/0.8/1.0（`:54-70`）。
- `BotBrain`：`SelectCards` 按 purpose 分派；`ScoreDeckCard` 稀有度+类型+damage/block×0.25-费用（`:193-222`）；`ScoreCombatCard` 战斗分离（`:161-191`）。

---

## 二、问题详述

### 1. 【实现】team 上限只作用于人类建议，机器人奖励/商店路径仍是无上限 `TeamBonus`

- 证据：`TeamBonus` 原样返回 `(supportRole?26:10)/(1+copies) - ownSupport*5` 或 `15/(1+copies)`（`TeamCoordinator.cs:65-82`）。上限 `TeamBonusShare=1.5` 只在 `TeamCoordinator.CardValue` 内施用于 `HumanCoopAdvisor.CardValue` 基线（`:39,44-52`）。该函数的生产调用者只有 `HumanAdviceUi`（`HumanAdviceUi.cs:65`）。机器人奖励路径 `BotBrain.ChooseCardReward → BuildValue.BestReward → Add → Marginal` 是**直接** `score += team`，无任何上限（`BuildValue.cs:156-161`）；商店路径 `BotShopPlanner.Choose → BuildValue.Add`（`:63`）同样无上限。
- 【实现】`TeamBonusShare` 在仓库中除 `TeamCoordinator.cs` 外无引用（grep 仅命中定义与使用处）。
- 【推测】注释自述的「两个 CONCOCT 仅凭 team-fit 入选」事故（`TeamCoordinator.cs:47-50`）发生在机器人的奖励/商店路径上；该修复因此没有覆盖真正的出错路径。当 `Marginal` 自身贡献为负/极小时，+26 仍可把卡翻正并击败 Skip。
- 最小修复：把 `TeamBonus` 的上限抽成共享函数（或在 `BuildValue.Marginal` 内、加 `team` 前，用同一 `1.5×max(0, own)` 上限），让奖励/商店/人类建议同源。
- 确定性回归：构造 2 人队、指定设计支援位（最低 NetId bot），牌组为纯 Strike，比较 `BuildValue.Add(AnyAlly 支援牌)` 与 `TeamCoordinator.CardValue(...)` 的 team 增量应 ≤ 各自 `1.5×own`；现在前者会超出。

### 2. 【实现】affinity 被当作「触发器缺失」证据，会误伤自给型 scaling

- 证据：`UnsupportedScaling` 只要 `BakedCardAffinity.Pairs[card]` 存在且伙伴都不在牌组，就把 scaling ×0.4 并标 `scaling-unsupported`（`BuildValue.cs:411-416`）。而 `BakedCardAffinity` 的表义是「一起被抽到的 lift」，不是触发/使能关系（`BakedCardAffinity.cs:15-19`）。数据表自身把 `INFLAME` 列为 strength **producer**（`BakedResources.cs:26`），其 affinity 伙伴为 `ULTIMATE_STRIKE/TAG_TEAM/HAND_OF_GREED/ETERNAL_ARMOR`（`BakedCardAffinity.cs:247`）。
- 【实现】因此 `CardProfile` 判出 `scaling/strength` 的自给型 producer，在缺少那 4 张「共抽伙伴」时会被降权 60%，理由写作「awaiting enabler」。当前测试只锚定了 Rupture（真条件牌）与 Footwork（Dexterity 豁免）两端（`BuildValueScenarios.cs:154-168,251-262`），未覆盖自给 producer。
- 【未知】各 affinity 伙伴的具体卡面机制本报告未逐一核对；但「用共抽 lift 推断触发依赖」这一推理本身不成立，与具体卡无关。
- 最小修复：区分「producer（`BakedResources` 有该卡）跳过错配」与「条件 payoff（Rupture 型，需人手白名单或显式 trigger 字段）才降权」；在当前数据下，该 producer 全量豁免建议已被 Astra 否决，见顶部验收：Rupture 本身也在 Producers 中。
- 确定性回归：`BuildValue.Marginal(Inflame, 纯 Strike 牌组)` 的 Reason 不应含 `scaling-unsupported`；无触发源的 Rupture 保持受限；加入 Hemokinesis 后估值应提高（现有断言）。

### 3. 【实现】draw/energy/multihit 语义依赖硬编码卡名白名单，与「模组同判」声明矛盾

- 证据：`CardProfile.Of` 的 draw/energy/multihit 完全来自 `analyzed.Draw / ImmediateEnergy / Hits`（`CardProfile.cs:52-53,73`），而这些字段由卡名集合决定：`StrongDrawFirstCards`（`GeniusCombatStrategy.cs:40-46`）、`ImmediateEnergyCards`（`:34-38`）、`FixedDoubleHitCards/RepeatIsHitCountCards/EnergyXHitCards`（`:18-32`）。`Analyze` 只在卡名命中时给出非零（`:500-519,545-548`）。
- 【实现】`CardProfile` 头部声明「come from the card's own model … so modded and rebalanced cards classify the same way as shipped ones」（`CardProfile.cs:10-11`），与实际白名单相冲突。重命名/模组抽牌卡 → `Draw=0` → 无 `draw` role → 不进 `needs-draw`、不计入饱和、且不在删牌保护名单（`BuildValue.cs:491`）；同理模组多段攻击无 `multihit`，无法触发 `strength-multihit` 路线（`Archetypes.cs:29`）。
- 【推测】纯模组/改名卡组会整体退化为「伤害+格挡」二维估值，抽牌引擎与多段 payoff 被系统性低估。
- 最小修复：把这几张白名单下沉为优先，但增加从 `DynamicVars`（CardsVar/EnergyVar/Repeat/CalculatedHits）推导的兜底，或在 `CardProfile` 内对无法识别的情况显式打 `unknown-draw` 之类的标记而不是静默为 0。
- 确定性回归：新建一个不在白名单、但带 `CardsVar=2` 的测试卡，断言 `CardProfile.Of(...).Roles` 含 `draw` 且 `BuildValue.Remove` 对「牌组唯一该卡」返回 `protected:draw`。

### 4. 【实现】Cook 用另一套估值排行删牌，绕过 `BuildValue.Remove` 保护

- 证据：`CookValue` 仅过滤 `IsRemovable`，按 `HumanCoopAdvisor.CardValue` 升序取最差两张（`RestSitePatches.cs:281-284`），随后把 `BotShopPlanner.RemovalValue`（走 `BuildValue.Remove`）求和乘 0.3（`:285`）。`BuildValue.Remove` 对 role∈{block,aoe,energy,draw} 的最后一枚直接返回 0（`:277,488-497`），`BotShopPlanner.RemovalValue` 亦以 0 计价（`:35-45`）。
- 【实现】Cook 的候选排序阶段不做该保护，因此「牌组唯一的 block/aoe/energy/draw 卡」可以进入被删的两张；保护只体现在随后的分值里（若两张都被保护，removal=0，Cook 仍可能以 `10 + MaxHp` 的剩余价值胜出）。
- 【推测】在「大而缺防御」的牌组，Cook 可能删掉唯一防御来换 +5 最大生命，而商店删牌在同一牌组会明确拒绝。测试只覆盖「垃圾牌组选 Cook / 好牌组不选 Cook」（`RestSiteScenarios.cs:48-79`），未覆盖保护。
- 最小修复：Cook 选两张时复用 `BuildValue.Remove` 的返回值做初筛（`RemovalValue > 0` 才可作删牌目标），或至少排除 `Blocking` 命中的卡。
- 确定性回归：`Give<StrikeIronclad>(12); Give<DefendIronclad>(1)`，断言 `CookValue` 不会把唯一 Defend 计入两张目标（或 `BuildValue.Remove(唯一 Defend)` 为 0 时 Cook 不因该卡获利）。

### 5. 【实现】`TeamBonus` 用卡名硬编码判定全队铺垫

- 证据：`TeamBonus` 对 `card.GetType().Name is "Shockwave" or "Uppercut" or "LegSweep" or "Bash"` 返回 15（`TeamCoordinator.cs:79-80`），其余全队 debuff 一律 0。
- 【实现】这与 `CardProfile`/`Baked*` 的数据自派生口径不一致：同样的「全队易伤/虚弱」语义未从 `facts.Roles`+`TargetType.AllEnemies` 推导，改名或模组卡、以及清单外的 AoE 铺垫卡都被漏掉。
- 【推测】队里出现清单外 AoE 铺垫时，合作价值被系统性忽略；清单内卡即使牌组不支持也固定加分。
- 最小修复：改为 `card.TargetType == AllEnemies && (facts.Roles.Contains("vulnerable") || facts.Roles.Contains("weak"))`，并按 `teamCopies` 递减，保留现有 `/(1+copies)` 形状。
- 确定性回归：对一张仅 `AllEnemies+Vulnerable`（不在四名清单）的测试卡，`TeamBonus > 0`；对同名的单人目标卡 `TeamBonus == 0`。

### 6. 【实现】`UpgradeDelta` 与牌组/上下文无关，异常被吞成「不可升级」

- 证据：`UpgradeDelta(card, player)` 忽略 `player`，内部 `CardProfile.DiffUpgrade(card)` 用默认 `energy=3,enemies=1`（`BuildValue.cs:293-295`；`CardProfile.cs:136-164,42`）。`Upgraded` 以裸 `catch { return null; }` 把任何克隆/升级异常变成「不可升级」（`CardProfile.cs:113-129`），于是该卡在 `RestSitePatches.bestUpgrade` 里得 0 分（`:140-143`）。
- 【实现】铁匠的价值只按卡自身差分排序，不看牌组是否已饱和该角色；`RestSitePatches` 也只用「是否存在 ≥2.5 的可升级卡」做门槛（`:108-109`）。
- 【推测】当两张卡差分接近而其中一张角色已饱和时，铁匠会升级冗余卡而非唯一关键卡；克隆失败的 mod 卡则静默失去升级机会。
- 最小修复（不引入新架构）：至少让 `UpgradeDelta` 接受牌组并发给 `RoleWeight` 折扣；把 `catch` 改为记录原因（如 `"upgrade-failed"`），避免「不可升级」与「失败」混为一谈。
- 确定性回归：构造两张 `UpgradeDelta` 相近的卡，一张是其角色唯一、一张是第四张；断言唯一者排名更高。若真实机制无法区分，则记录为【未知】而非强行断言。

### 7. 【实现】非 `FromDeck*` 的加牌屏走第三套评分，缺稀释/体积/资源门

- 证据：`BotBrain.Permanent` 仅在 `purpose.Contains("FromDeck")` 时使用 `BuildValue`；其余加牌意图落入 `SelectCards` 的 `ScoreDeckCard`（`BotBrain.cs:79,133-143,193-222`）。`CardChoicePatches` 以 `originalMethod.Name` 传入 purpose（`CardChoicePatches.cs:39,46`），`FromChooseACardScreen` 这类屏因此走 `ScoreDeckCard`：无稀释（`BuildValue.cs:205`）、无体积压力（`:229`）、无 star 资源门（`:100`）、无 Skip 语义，且对诅咒仅 -50/-30 不等（`BotBrain.cs:201-202`）。
- 【实现】测试只锚定了该屏的 `maySkip` 行为（`BuildValueScenarios.cs:346-360`），未断言其估值来源。
- 【推测】同一「往牌组加一张牌」的决定，因入口不同而用两套价值；事件加牌不会因牌组膨胀被抑制，也不会被资源门拒绝。
- 最小修复：把「加牌」类屏（可依据 prefs/参数而非方法名）也路由到 `BuildValue.Add`，或在 `SelectCards` 内对 `min==0||maySkip` 的加牌屏统一走 `BuildValue.BestReward`。
- 确定性回归：同一候选集分别以 `FromChooseACardScreen` 与奖励路径调用，断言两者选牌一致；当前实现会因评分器不同而不一致。

### 8. 【实现/推测】无缓存导致奖励/商店一次估值触发 O(n²) 次 `Analyze`

- 证据：`CardProfile.Of` 无任何 memoization，每次调用都执行 `GeniusCombatStrategy.Analyze`（含 `card.UpdateDynamicVarPreview`，`GeniusCombatStrategy.cs:481`）（`CardProfile.cs:42-107`）。`Marginal` 内 `RoleSaturation`/`GapBonus`/`Blocking`（经 `RolesOf`）对每张 deck 卡反复调用 `CardProfile.Of`；`Add` 对每张现有卡各调 `Marginal` 一次（`BuildValue.cs:191,204`）；`Remove` 调 `Marginal` 约 2n 次（`:262-264`）。
- 【实现】奖励屏对 3 张候选各跑一次 `Add`，`BotShopPlanner.Choose` 还会对全牌组算 `RemovalValue`（`:51`）。
- 【推测】30 张牌组一次选牌约 10³ 量级 `Analyze`/预览调用，可能造成商店/奖励界面卡顿；本报告未做 profiling，故列为推测。
- 最小修复：把 `CardProfile.Of`/role 结果按牌实例及该次快照状态在单次决策内做局部字典缓存，或显式把牌组画像算一次后传入。
- 确定性回归：非行为回归（性能）；可用调用计数探针断言同一 `Add` 调用内 `CardProfile.Of` 对同一 card 实例不重复 `Analyze`。

---

## 三、已检查、未发现新增问题的点

- 诅咒/状态 -1000 且无论加入/移除对称（`BuildValue.cs:92-93,292-298`），测试已锚定（`BuildValueScenarios.cs:285-300`）。
- star 资源门、shiv 三拆、converter 分类、affinity/keyword 外键存在性均有测试（`BuildValueScenarios.cs:381-612`）。
- 体积压力上限、OnPlanRelief、starter 删牌随死抽递增、Dexterity 豁免、route bonus 只加不罚未知牌组，均已有测试与注释（`BuildValueScenarios.cs:133-262`）。
- `RemovalValue` 的 `IsRemovable` 前置检查（`BotShopPlanner.cs:37`）与 shop 阈值/末店地板逻辑自洽。
- `TeamBonus` 的分工（最低 NetId bot 为支援位）是显式设计，非缺陷；仅在问题 1 中一并讨论其上限缺失。

---

## 四、最小回归用例建议（供 Astra 决定是否落地）

1. team 上限：机器人奖励路径与人类建议路径的 team 增量必须同界（问题 1）。
2. 自给 producer 不被 `scaling-unsupported`（问题 2），同时保留 Rupture/Footwork 现有断言。
3. 模组抽牌/多段卡的 role 与删牌保护（问题 3）。
4. `CookValue` 不删唯一 block/aoe/energy/draw（问题 4）。
5. AoE debuff 卡的 `TeamBonus`（问题 5）。
6. 饱和角色时铁匠优先唯一关键卡（问题 6）。
7. `FromChooseACardScreen` 与奖励路径选牌一致（问题 7）。

## 五、未验证 / 未知

- affinity 伙伴的逐张卡面机制（问题 2 只否定推理方式，不裁定具体卡）。
- 问题 7 中 `FromChooseACardScreen` 在游戏内是否确实用于「加牌」事件（测试把它当卡牌屏，但入参语义未在源码核对）。
- 问题 1/4/8 的实战后果需实机或统计验证，本报告只给代码路径。
- 未运行任何测试；`shux`/`rg` 因本环境权限被拒，全部结论来自文件工具读取。
