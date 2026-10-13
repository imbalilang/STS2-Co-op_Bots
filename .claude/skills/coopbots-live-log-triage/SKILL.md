---
name: coopbots-live-log-triage
description: 分析 CoopBots 实机日志、同场伤害统计和可用 replay/save，定位团灭、卡顿、能力牌少用、错序、消耗浪费、战斗选牌及拿牌升级问题。区分执行、模拟覆盖、局内策略、搜索预算和局外构筑估值；核对加载 DLL 与源码断言是否一致，按战斗+回合跨席位扫同形失误，给出有证据的优化建议。适用于查看最新日志、判断 Bot 哪一部分决策有问题、用户只报一个席位但要检查全部席位；分析本身不授权改代码或部署。
---

# 实机日志与决策归因（S3）

权威规范见 `AGENT.md` §5 S3/S5。先遵守用户本轮范围：分析、修复、打包分别按已有授权执行。

## 先固定证据，再运行工具

1. `python scripts/analyze-live-run.py --list-logs` 找到最新候选，确认用户指的是最近结束的爬塔、最后团灭还是正在进行的战斗。
2. 将相关 `godot.log`、存在的 DamageMeter 文件、同场 replay/save/history 复制到新的 `work/<批次>/`。记录源路径、修改时间、大小、DLL/包版本与种子/难度/席位；滚动日志必须先归档。可用 `Get-FileHash` 记录摘要。
   - **DLL 以游戏目录实际加载的为准**，不是源码时间戳。记录 `mods/CoopBots/*.dll` 的 mtime 与 sha256；源码/断言与实机行为矛盾时，先扫加载 DLL 里有没有对应符号（如 `requireElite`、`MaxMonsterShare`），再讨论逻辑。
   - 同一份滚动日志可能包含多个 run：用 `Embarking on a multiplayer run` 切出当前 run，避免把旧局的动作、事件和 deck 混进同一报告。
3. 对归档运行合并分析器，再用 `rg -n` 提取关键行及上下文。摘要是索引；具体结论要回到原始动作和分数。
   - 工具报 `METER IS FROM ANOTHER RUN` 不一定是真的：先按 `run_seed` 数值和碰撞序列对齐。事件名后缀差异（如 `BATTLEWORN_DUMMY_EVENT_V` vs `..._V1_ENCOUNTER`）会误报。

```powershell
python scripts/analyze-live-run.py --log work/<批次>/godot.log --meter work/<批次>/DamageMeter_combat_state.json
rg -n 'combat summary|search-complete|falling back for this tick|plan step refused|plan dropped|How did the game end|NGame.Quit' work/<批次>/godot.log
```

缺少伤害统计时继续分析现有证据并说明缺口；显式传入缺失的归档路径，避免工具默认混入另一局的最新 meter。不要伪造数据或要求用户为了开始分析重打一局。

| 证据 | 用途与边界 |
|---|---|
| godot.log | 动作、选择、搜索状态、异常和估值理由；未记录的状态不能凭空补全 |
| DamageMeter_combat_state.json | 逐席伤害/承伤/出牌数、能量、按牌伤害；用战斗序列、席位和回合对齐，字符串种子与数值哈希不可直接比较 |
| 同场 replay/save/history | 重建关键回合、入场牌组和选择；确认保存时点及版本，终局牌组不等于拿牌当时状态 |
| 当前代码/原生反编译 | 在日志指向的层检查模型、原生规则和字段定义；当前代码不一定是该日志使用的 DLL |

合并工具输出 run/planner/combats/damage/decisions/decks 六节。DamageMeter 的 `segments` 是已结束场次，顶层 `players` 是当前/最后场；`dbc` 才是按牌伤害，UITrace 不是统计数值。

## 选择分析深度

- **团灭、决策质量、局内还是局外、优化建议**：读取 [决策归因方法](references/decision-attribution.md)。按时间线和因果层定位，再选最小反事实；其中包括能力投资/牌序、消耗、遗物/药水、升级估值、搜索调度和结论强度。
- **用户提供具体失误，要求检查其他席位，或评分与直觉相反**：继续读取 [现场动作与评分对照](references/action-counterfactuals.md)。从同一现场验证合法替代动作，区分未搜索、评分淘汰和执行绕过，并检查预测选择是否与实盘一致。
- **拒绝、丢剧本、预测分歧、时间轴边界**：在日志定位后使用 [内核缺陷定位](../coopbots-kernel-debug/SKILL.md)。
- **要改代码并补回归**：使用 [原生夹具](../coopbots-kernel-fixture-authoring/SKILL.md)、[不变量补测](../coopbots-invariant-regression-test/SKILL.md) 和 [验证门禁](../coopbots-verify-gates/SKILL.md)。

## 先查控制路径与战斗结束方式

逐场区分：是否按剧本执行、首次偏离、主动跳过退场席位、等待选择/真人、真正卡死。

- `falling back for this tick` 与 `via=legacy`：证明该决策未由内核驱动；检查完整异常堆栈和重复范围，不把 legacy 的表现归给锦标赛评分。
- `plan step refused` / `plan dropped`：追溯第一条具名原因；`playable`、`no-card` 等描述不能替代守卫名。
- `plan step drift (logged, not refused)` 与 `plan boundary drift`：只记录的偏差不等于拒绝。
- `plan step skipped`：可能是预测内的席位退场；看原生状态和剧本证据。
- `How did the game end??`：配合结算判胜负；`NGame.Quit called` 但未结算通常是外部退出；两者皆无应结合进程/看护记录判断，不能仅凭日志结尾定团灭。

逐场保留 `plans/coverage`、`routes`、`planRefusals`、`noScript`、`maxRound`，但先核查口径：完整剧本正常复用时 plans 可以不增长；noScript=0 不能独立证明全部覆盖；routes 不保证实际执行成功。药水、结束回合与卡牌开局的计数应都覆盖。

## 常见误判

- 严格执行了剧本不等于决策正确；零拒绝仍可能错序、短视或只完成失败路线。
- 伤害/出牌数低、基础牌占比高、打击伤害占比高、浪费能量少，都只是调查线索。能力/防御/支援不直接记伤害，错误牌序也可能花光能量；必须查可用牌与替代路线才能归因构筑。
- **总伤害最高不等于决策最好**。按牌聚合 `dbc` 后，若最高伤害牌是 `STRIKE_*` 之类起始牌，先判为构筑结果；若最高伤害牌是单一 payoff（如 `BULLY`），要单独检查它的 setup 牌序、强化与附魔，而不是拿总伤给席位背书。
- `damage-vs-prior`、prior/clamp/Elo 的历史观察不代表当前代码。读当时日志的分数分解，再确认对应版本公式。
- 历史 `tournament cost compute≈0` 曾在构造时取值，导致 GC 比率失真。合并工具的 NOTE 也可能沿用历史口径；先查对应版本字段更新点，不永久忽略所有 compute，也不固定假设 wall=2×compute。
- 节点多不等于有效比较多；完整终局、预算截断与模拟边界分开计数。某机制“已注册”不等于能进候选、正确续演、被选择并执行。
- 恒为零/常数的指标先证明可达；`COVERAGE BROKEN` 有内容就调查，不能因每次出现而忽略。

## 交付

围绕用户所问场次给出：终止方式、关键时间线、按层归因的问题与原始证据、可执行反事实或缺失证据、优化优先级与验证方法。区分已证实、嫌疑和合理取舍，不把“只差 14 血”写成“一定能赢”。全局统计可做背景，不能替代这场的因果证据。

只做分析时交付到证据和建议；获准修复后再落实代码、R2 和门禁。实机复测前不宣布胜率已提升。
