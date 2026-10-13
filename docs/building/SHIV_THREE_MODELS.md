# SHIV_THREE_MODELS — 确认结果

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


**版本边界（2026-09-18）：** 下述结论依赖当时的 0.40.0 离线求解器与固定预算。上游 0.41.0 已改变能力牌搜索策略，旧模型对新版的兼容性尚未确认；本轮多人局内策略部分回移不等于这些实验已重跑。求解器是有策略偏差的近似优化器，增加种子只能减少随机误差。采用新版本时按 [求解器升级回归流程](SOLVER_UPGRADE_REGRESSION.md) 检查排序和参数漂移，再决定是否重校准。

## Owner 最终验收

三种模型均已完成训练。A（机制权重调参）与 C（战斗收益岭回归）在同一个冻结留出牌组、同一普通遭遇的两批独立200seed测试中，均稳定优于默认机制模型与A10最高Elo基线；B（社区混合）选择与基线相同，未产生可区分的增益。**确认2种，不是3种全都确认。**

| 模型 | 训练结果 | 留出选择 | 两批确认 |
|---|---|---|---|
| A 机制调参 | 防御权重组×0.5，抽牌/能量权重组×0.5；其他参数不变 | KNIFE_TRAP | 通过 |
| B 社区混合 | 最优lambda=0，未采用Elo混合 | ADRENALINE | 与基线同动作，无新增收益 |
| C 战斗回归 | 三个标准化特征组、固定岭惩罚；预测相对跳过的战斗效用差 | KNIFE_TRAP | 通过 |

| 选择 | 第一批胜场 | 独立复验胜场 |
|---|---:|---:|
| 刀刃陷阱（A/C） | 186/200，93% | 183/200，91.5% |
| 肾上腺素（B/原模型/Elo） | 11/200，5.5% | 8/200，4% |
| 跳过 | 4/200，2% | 2/200，1% |

两批A/C相对两种基线的效用差，经12项比较Bonferroni校正后区间均大于零（H：[1.1070,1.2919]；R：[1.0995,1.2758]）。这是单一牌组/敌人上的求解器内稳定改善，A/C同动作共享同一证据，不能视为两份独立证据或说明A优于C。合成无遗物牌组、无升级、仅3个基础训练局面，不能外推完整跑局或多人场景。训练前两局面各动作全败，训练目标包含敌人剩余HP，因此不能只把拟合称为胜率优化。A的最优乘子落在搜索下界；C的引擎系数略负，均保留为待扩展数据检验的模型局限。

共3000场（训练1800+两批留出各600），N16×dop2，战斗计时累计约41.6分钟；另有实现与汇总修复时间。Owner独立核对9000个输入/请求/结果哈希、记录唯一性、训练快照排除留出数据、协议哈希和R配对统计，运行时一致。原训练执行器在1800场后发生汇总异常，修复后只运行未启动的600场留出，未重复战斗；恢复过程保留在SHIV_FIT_V1报告及summary.resume中。

模型文件：`work/shiv-three-models/models.json`（A/B/C、标准化尺度和冻结选择）；可直接用于现有离线评分入口的A参数：`work/shiv-fit-v1/params-trained.json`。保持全局默认参数和Bot生产代码不变。验收证据：`work/shiv-three-models/20260918T052053.177537Z/owner-acceptance.json`。

生成时间: 2026-09-18T05:28:51.699944Z
报告模式（无新战斗）: `False`
H 匹配 seed 数: 200
R 匹配 seed 数: 200
家族比较数: 12，每项 α=0.004167，调整临界 z=2.8653。

## 三个模型合计 12 项比较

| 模型 | 基线 | 复制 | 模型动作 | 基线动作 | n | 均值差 | 普通95%CI | 校正CI | 状态 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | default_model | H | KNIFE_TRAP | ADRENALINE | 200 | 1.1995 | [1.1366, 1.2623] | [1.1070, 1.2919] | positive_adjusted |
| A | default_model | R | KNIFE_TRAP | ADRENALINE | 200 | 1.1877 | [1.1277, 1.2476] | [1.0995, 1.2758] | positive_adjusted |
| A | elo_with_skip | H | KNIFE_TRAP | ADRENALINE | 200 | 1.1995 | [1.1366, 1.2623] | [1.1070, 1.2919] | positive_adjusted |
| A | elo_with_skip | R | KNIFE_TRAP | ADRENALINE | 200 | 1.1877 | [1.1277, 1.2476] | [1.0995, 1.2758] | positive_adjusted |
| B | default_model | H | ADRENALINE | ADRENALINE | 200 | 0.0000 | [0.0000, 0.0000] | [0.0000, 0.0000] | indistinguishable_same_action |
| B | default_model | R | ADRENALINE | ADRENALINE | 200 | 0.0000 | [0.0000, 0.0000] | [0.0000, 0.0000] | indistinguishable_same_action |
| B | elo_with_skip | H | ADRENALINE | ADRENALINE | 200 | 0.0000 | [0.0000, 0.0000] | [0.0000, 0.0000] | indistinguishable_same_action |
| B | elo_with_skip | R | ADRENALINE | ADRENALINE | 200 | 0.0000 | [0.0000, 0.0000] | [0.0000, 0.0000] | indistinguishable_same_action |
| C | default_model | H | KNIFE_TRAP | ADRENALINE | 200 | 1.1995 | [1.1366, 1.2623] | [1.1070, 1.2919] | positive_adjusted |
| C | default_model | R | KNIFE_TRAP | ADRENALINE | 200 | 1.1877 | [1.1277, 1.2476] | [1.0995, 1.2758] | positive_adjusted |
| C | elo_with_skip | H | KNIFE_TRAP | ADRENALINE | 200 | 1.1995 | [1.1366, 1.2623] | [1.1070, 1.2919] | positive_adjusted |
| C | elo_with_skip | R | KNIFE_TRAP | ADRENALINE | 200 | 1.1877 | [1.1277, 1.2476] | [1.0995, 1.2758] | positive_adjusted |

## 模型裁决

| 模型 | 确认 | 正 | 不可区分 | 不确定 | 回归 |
| --- | --- | --- | --- | --- | --- |
| A | True | 4 | 0 | 0 | 0 |
| B | False | 0 | 4 | 0 | 0 |
| C | True | 4 | 0 | 0 | 0 |

三种模型是否全部稳定确认: **False**（A、C通过，B与基线同动作）。

## 合并 H+R（次要，400 seed，不单独构成确认）

| 模型 | 基线 | n | 均值差 | 校正CI |
| --- | --- | --- | --- | --- |
| A | default_model | 400 | 1.1936 | [1.1301, 1.2570] |
| A | elo_with_skip | 400 | 1.1936 | [1.1301, 1.2570] |
| B | default_model | 400 | 0.0000 | [0.0000, 0.0000] |
| B | elo_with_skip | 400 | 0.0000 | [0.0000, 0.0000] |
| C | default_model | 400 | 1.1936 | [1.1301, 1.2570] |
| C | elo_with_skip | 400 | 1.1936 | [1.1301, 1.2570] |

## 诚实结论与局限

- 仅在一个 heldout 牌组、一个遭遇上进行两批 200 seed；只能说明该处随机可重复性，不代表广泛牌组泛化。
- 同动作的配对差恒为 0，记为不可区分，既非失败也非确认；区间跨 0 为不确定，为负为回归。
- 合成 PROXY、单 solver 策略，无真相标签；本确认不决定模型保留，只服务优化与确认。
- 无选择性加赛、无结果后再拟合、无第二套超参数。

原始证据: `work\shiv-three-models\confirm-summary.json`, `work\shiv-three-models\models.json`。
