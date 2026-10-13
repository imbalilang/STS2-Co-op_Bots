# 六份社区历史的重建可行性测试（2026-09-18）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


DeepSeek 完成离线审计器与初始20项测试，Astra验收时修正来源约束、计数和结论，并新增5项回归测试。**25项测试通过；6份原始历史通过来源与目标条件校验，0个选牌前状态完成可用性验证。** 这是格式、来源和信息缺口审计，不是完整的状态重建算法；没有证明所有受限重建路径都不可能。

## 实测结果

|指标|数量|
|---|---:|
|v0.111.0、单人Silent、A10、standard、无修改且未放弃的跑局|6（3胜3负）|
|玩家楼层记录|292|
|含非空选牌记录的楼层|156|
|选项条目|564（97选中、467未选中）|
|没有选中项的选牌楼层|61|
|含两个选中项的楼层|2|
|选项数量不是3的楼层|31|
|逐选牌楼层的直接deck/relics/potions列表|均为0|
|已验证可用于收益评测的完整选牌前状态|0|

楼层记录、选项条目均不等于独立奖励页面。没有把这些数据送入战斗求解器，也没有扩产标签或训练。

## 为什么尚不能直接导出

以下路径相对于 `work/decompiled-v0.111.0/MegaCrit/sts2/Core`：

- `Runs/History/CardChoiceHistoryEntry.cs:10`只有卡牌与was_picked，没有页面编号或跨事件时间。`Rewards/CardReward.cs:295`写选中项，`:301`写未选中项；`:316`跳过、`:326`重抽也追加到同一列表。商店、事件等还有其他写入来源，因此不能按每3条、每个picked或全false推断页面与skip。
- `Runs/PlayerMapPointHistoryEntry.cs:62`开始的获得、移除、变形、升级、附魔、物品等是独立列表，未保存跨列表顺序。`Commands/CardCmd.cs:282`升级只记录ModelId；重复同名卡的具体归属可能不唯一。
- `Runs/RunManager.cs:1713`更新HP/maxHP/gold，调用发生在进入下一地图点`:886`或跑局结束`:1625`。这些是楼层末的采样，不能自动作为选牌前数值。
- 终局players[].deck/relics/potions不能直接充当前序状态。列表缺少逐次快照，并不单独构成“数学上无法重建”的证明；仍需状态初始化、事件顺序和唯一性证明。
- 缺失、null与显式空列表在工具中保持区别。但游戏某些事件列表由 `SaveIfNotCollectionEmptyOrNull` 省略空值，`SerializableCard.current_upgrade_level`也省略默认0。已知序列化默认值可以依版本解释；没有定义过的pre-reward快照不能凭默认值补造。

## 验收纠正与测试

- 默认执行固定source-validation.json的SHA256；清单变化时，在读取任何跑局前拒绝。每份原始文件另验SHA256。
- 版本、单人、A10等必须符合固定目标，不允许清单与输入同时偏离后互相“验证”。非standard、放弃、modifiers及非布尔win均拒绝。
- 快照字段数量从实际输入计算；显式空列表可计为字段存在，null不会冒充空快照。字段存在也不代表其时点正确。
- 重建可行性保留unknown/not_verified，已验证状态数量为0；不会把脚本尚未实现重建误写成“不可能”。
- 原有异常测试覆盖SHA破坏、错误目标、缺失/空列表、多选/全未选、同层获得与变更、重复卡牌身份、无选牌记录；新增测试覆盖联合篡改、固定清单读取边界、动态快照计数、CLI失败退出码。

执行：`python -m unittest discover -s tests -p test_building_history_audit.py`，25项通过；`python scripts/building_history_audit.py`，6份通过、计数与独立核验一致。

## 下一步：最小选择前捕获

建议下一任务实现默认关闭的研究记录器，不改选牌策略，先做合成输入与序列化验证，再采少量真实记录。Bot路径可在 `src/CoopBots/BotBrain.cs:146` 的ChooseCardReward入口、BestReward调用前，以 `RewardPatches.cs:64` ChoiceForWait传入的真实候选记录当刻状态；选定结果用相同decision_id另记。候选Populate/奖励对象创建时点可能过早，不能保证选择时牌组与物品未变化；其他入口需要单独确认覆盖。

最小字段：schema/game/MOD/policy版本、匿名run/player/decision标识、act/floor/room/source、升阶/角色/队伍、带稳定序号与完整升级/附魔/props的候选、skip和其他合法替代选项、完整牌组（保留副本）、HP/maxHP/gold、遗物及其状态、药水及槽位、已选行动与是否成功应用。多选与重抽每个决策单独记录。RNG/队伍状态若不能完整捕获，明确标缺失并限制评测用途；根seed不冒充当刻RNG。禁止写密钥或社区用户名。

本轮未实现或启用上述生产采集，也未宣称生产收益。采集数据应有独立schema验收器，不能直接交给这份仅针对六份冻结历史的审计器。
