# 冻结动作留出评估（build-pick-heldout-screen）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `scripts/building_pick_heldout_screen.py`。它是已验收的
`scripts/building_pick_node_repeat.py` 之后的**窄口径留出**仪器：开发种子已经用
冻结规则为每个 fixture 选出一个动作（见 `work/pick-fixtures/heldout-protocol.json`），
本仪器只在**从未使用过的留出种子** `PICKEVAL01` / `PICKEVAL02` 上，重放这
**4 个已批准 fixture/动作格子**（每种子每动作 2 次），在单个 16 路进程池、nodes 6000、
`--dop 2`、120000ms 软预算、180000ms 搜索超时下共 **16 场**。

它只回答一个问题：**在留出种子上，冻结的 selected 动作相对冻结的 baseline 动作，
原始成对终局结果如何？** 它不是强度、显著性、训练或生产工具。

## 状态

- 交付：`scripts/building_pick_heldout_screen.py`、`tests/test_building_pick_heldout_screen.py`、本文件。
- 只读复用 `building_pick_node_repeat`（`_run_one`、`build_command`、`build_performance`、
  `_node_telemetry`、`_build_initial_search`、`_Overlap`、`_remap_cell_to_seed`）、
  `building_pick_parallel_check`（`execute_run` 的期限/杀进程/回收/实际 policy/原始输入校验、
  `load_baseline`/`validate_baseline_source`/`check_runtime_against_baseline`/`semantic_tuple`/
  `compare_semantics`/`baseline_cells`/`run_dir_path`/`_missing_record`/状态词表）、
  `building_pick_ranking_screen`（`prepare_source`、`resolve_build_value_action`、`diff_paths`、
  `create_exclusive_file`/`append_journal`、`_status_counts`/`_outcome_counts`）、
  `building_pick_input_probe`（输入证据/`verify_run_evidence`）、`building_eval`、`building_repeat_probe`
  （`sanitize_request`、`write_json_exclusive`、`build_env`）。未修改任何既有 helper、生产代码、
  开发种子 allowlist 或模块全局。
- 默认干跑已由实现者执行（`work/pick-heldout-screen/<UTC>/`，16 个 `dry_run`、0 启动、
  `runtime.matches_baseline=true`、`baseline.validated=true`、selection pin 校验通过）。
- 真实 16 场由 Astra 审查后显式 `--run` 执行；本任务**未执行** `--run`。

## 冻结协议与选择产物

来源 `work/pick-fixtures/heldout-protocol.json`（运行前冻结）。`load_protocol` 逐字段重校：

| 维度 | 值 |
| --- | --- |
| 留出种子 | `PICKEVAL01`、`PICKEVAL02`（与选择种子不相交） |
| 批准映射 | starter：selected `skip` vs baseline `BACKFLIP`；late：selected `BACKFLIP` vs baseline `DAGGER_SPRAY` |
| 重复 | 每 (fixture, 动作, 种子) 2 次；4 格子 × 2 种子 × 2 = **16** |
| 参数 | `--dop 2`、nodes 6000、soft 120000ms、search-timeout 180000ms、max-turns 40 |
| 硬限 | 单进程 300s、整批 900s（含输入准备与进程池排队，`min(300, remaining)` 准备后重算）、16 并发、不重试 |
| 禁止 | `reselection`/`training`/`production_adoption` 必须为 false |

选择产物 `work/pick-node-repeat/20260917T153844.881455Z/development-selection.json`
的 SHA256 由模块常量 `SELECTION_SHA256` 固定为
`b970a7b5eef9fdf9f9b79332b67da36482ee85e1000d538e47824a3b2d9da942`。任何真实执行前
`load_selection` 必先用该常量重算文件哈希；只有 Python 测试可通过 `selection_sha256=`
注入假选择（CLI 不暴露该参数）。随后 `verify_selection_mapping` 重新检查：

- 选择产物每个 fixture `gate_passed=true`，`selected_action` 等于批准映射；
- 选择的 `build_value_action` 等于**当前 adapter 的 `BuildValue` 动作**
  （`ranking.resolve_build_value_action`），且等于批准映射的 baseline。

任何不一致（协议字段、选择哈希、映射、BuildValue）都在任何输出目录创建/子进程启动前抛错。
**从不扫描或重选留出动作。**

## 输入与基线映射

- 规格来自 `ranking.prepare_source` 默认 `PICKSEL01` 的 16 个 spec，筛选 `budget_ms==20000`
  且属于 4 个批准 (fixture, 动作) 的格子；克隆为 2 留出种子 × 2 次。
- **专用紧凑写入器** `prepare_heldout_input`（不调用会拒绝留出种子的
  `ranking.validate_seed`/`write_run_inputs`）：
  1. 重读原始 scenario，重算 SHA256 必须等于 manifest `scenario_sha256`，且其 `seed`
     必须等于 manifest `source_seed`；
  2. 仅把 `seed` 设为计划留出种子，`diff_paths` 必须恰为 `{"seed"}`；
  3. 以 `indent=1, ensure_ascii=false` 写出 `input.json`；
  4. `outer-request.json` 经 `brp.sanitize_request` 克隆后恢复计划种子/角色，
     与 sanitize 基线的差异必须恰为 `{"seed"}`；
  5. 生成的 input 哈希必须等于**已由 `parallel.load_baseline` 原始哈希校验过的基线 20000ms
     input 仅改 seed** 后的期望哈希（`node._remap_cell_to_seed`），否则抛
     `SourceValidationError`。
  返回 `{input_sha256, request_sha256, baseline_input_sha256}` 供 `parallel.execute_run`。
- 未放宽任何校验器，未 monkeypatch 模块全局，未重贴历史证据标签。

## 运行与失败口径

- 复用的 `node._run_one` 仅用实际子进程区间计数并发峰值；复用的 `parallel.execute_run`
  保持严格 `exit 0`、payload 无 `error`，超时/非零/缺失/畸形/证据不一致绝不变成损失；
  超时记 `timeout`、无剩余预算记 `not_started`。
- 额外校验实际 `search-policy.json`：`maxDegreeOfParallelism==2`、
  `softTimeBudgetMilliseconds==120000`、`maxExpandedNodes==6000`，不匹配即 `mismatch` 且
  `eligible=false`。
- runtime 在启动前采集并与基线五项身份哈希比对；`--run` 下不匹配**直接中止**（0 启动）。
  批次后再采集一次：`before != after` 记 `stable=false`；与基线不匹配记
  `matches_baseline=false`；任一为假或干跑时**禁用全部比较**并在 `gate_reason` 注明。
- 所有 16 个 future 都被 join，journal 由主线程按完成顺序写；批异常记为 `error`，summary 仍写盘。
- 继承环境只传给子进程，从不写入任何产物。

## 汇总口径

- **8 组同 (fixture, 动作, 种子) 重复**：仅当两次重复都 `verified`/`eligible` 且比较未禁用时，
  用 `parallel.semantic_tuple`+`compare_semantics` 比较六字段（input_status/outcome_status/
  player_hp/max_hp/enemy_hp/turns）。缺失/无效/不完整只抑制该组；重复本身不一致如实报
  `agreement=false`，非抑制。
- **4 个成对 (fixture, 种子) 比较**：仅当该动作组的两次重复都可用且**完全一致**，且 selected
  与 baseline 两动作组都如此，且 runtime 固定时，才折叠重复为一个观测并比较：
  selected 胜 / baseline 负 记 `selected_better`（`selected_win_baseline_loss`）；反向记
  `selected_worse`（`selected_loss_baseline_win`）；双胜比较终局玩家 HP（高者更好，相等并列，
  `hp_delta` 计入 co-win），双负并列。敌人 HP 与回合数**绝不作为 tiebreak**。
  unavailable 只记录不推断。
- `pair_aggregation` 报告 `better`/`equal`/`worse`、selected 胜 baseline 负与其反向计数、
  `both_win`、`co_win_hp_deltas` 与 `co_win_hp_denominator`，并显式标记重复不是额外样本、
  4 对共享 2 个种子。
- **性能与初始搜索诊断**复用 `node.build_performance` 与 `node._build_initial_search`：
  并行墙钟、实际峰值并发、CPU 合计与利用率（任一并行 run 缺 CPU 即抑制利用率，绝不当 0）；
  `whole_fight_node_only_established` 恒为 false。
- **决策块**：`production_adoption=none`、无新标签、无留出收益声称、无统计显著性/置信、无因果、
  无训练、无重选、无方差、无整场节点确定性、无自动扩张。

## 输出

`work/pick-heldout-screen/<UTC 微秒>/`（已存在即拒绝，不覆盖）：

```
plan.json        # 16 个 run、冻结命令、批准映射、协议/选择/baseline/runtime 校验、期望 input 哈希
journal.jsonl    # 主线程按完成顺序每次尝试一行（16 行）
summary.json     # 状态/结局计数、repeat_agreement、pairs/pair_aggregation、
                 # initial_search、performance、decision、runs、限制
parallel/<name>/input.json  outer-request.json  stdout.bin  stderr.bin  harness-result.json …
```

## 实际命令

```powershell
# 默认干跑（即“dry only”）：只写 plan/summary/journal，0 启动
python scripts/building_pick_heldout_screen.py
python scripts/building_pick_heldout_screen.py dryonly
# 受限真实执行：需 Astra 审查后显式调用（会新建另一批，不覆盖）
python scripts/building_pick_heldout_screen.py --run

# 纯离线测试（假 subprocess、线程安全假时钟、临时 fixture/baseline/runtime/协议/选择）
python -m unittest discover -s tests -p test_building_pick_heldout_screen.py
python -m unittest discover -s tests -p test_building*.py
```

## 本轮验证（DeepSeek）

- `python -m unittest discover -s tests -p test_building_pick_heldout_screen.py`：**36 项通过**。
  覆盖：固定常量与 pin、协议字段/选择哈希/映射/BuildValue 不一致均在启动前拒绝且 0 启动、
  16 个唯一命令（4 格子 × 2 种子 × 2 次）与正确种子/动作、命令仅冻结 flag/路径与基线不同、
  默认干跑 0 启动、拒绝已存在目录、`main dryonly` 退出码 0、CLI 拒绝 `--seed/--action/--dop`、
  生成 input 与 seed-only 期望哈希一致且同 (动作,种子) 两次逐字节相同、input/outer 仅 seed 改变、
  源 seed 不匹配拒绝、16/16 verified 与 policy、真实峰值并发 16、journal 16 行、截止后全
  `not_started` 无重试、错种子/缺失/超时/不完整均非损失且抑制对应 pair、重复不一致抑制对应 pair
  且不误伤其它 pair、same-win-HP/双负/对立胜负聚合、runtime 预检中止与批次内变化禁用比较、
  缺 CPU 抑制利用率、初始搜索不声称整场节点执行、既有 node 入口仍拒绝 `PICKEVAL01`、
  模块与 ranking/parallel 全局不被改动、继承环境不外泄。
- `python -m unittest discover -s tests -p test_building*.py`：**326 项通过**（含既有 building 测试）。
- 全部测试注入假 subprocess、临时 fixture 副本、临时 runtime、临时协议/选择，未启动任何真实进程。
- CLI 干跑：`attempts=16 seeds=['PICKEVAL01','PICKEVAL02'] dop=2 nodes=6000 budget_ms=120000
  executed=false status_counts={dry_run:16}`，输出目录只有 plan/journal/summary；
  `runtime.matches_baseline=true`、`baseline.validated=true`、selection pin 校验通过。

## 限制（不得省略）

- 两个状态都是 PROXY，不是观察到的奖励画面。
- 两个种子、每格两次重复无法建立方差或显著性；同一种子的两次重复不是独立样本，
  4 对共享 2 个种子，**不构成 i.i.d. 样本**。
- 旧20000ms/dop1产物仅用于输入来源及运行版本校验。本次所选动作与BuildValue对照均使用相同的120000ms/dop2，并在相同留出种子上配对；该配对不混入旧预算战斗结果。
- 批截止为单调墙钟；慢机会把后续 run 记为 `not_started`，如实记录不隐藏。
- 只读初始搜索边界；不采集逐回合遥测，**不主张整场纯节点执行**。
- 证据可信度上限取决于 harness 构建与其消费的验收 fixture 目录。
- 本仪器只报告原始成对留出结果；**是否继续、是否扩产、是否训练由 owner 依据原始结果决定**，
  仪器自身不做任何自动扩张。

## Astra 审查补充

修正获胜HP无效时被误当作平局的边界：缺失、布尔、非有限数或字符串HP会使该配对不可比较；有效数值按差值排序。新增全计划配对是否完整的显式标志，不能用部分完成结果代表全部4对。两项针对性回归覆盖了这类计数和HP排序边界。

## 真实留出结果与独立验收（2026-09-18）

Astra最终回归328项通过，日志 `work/pick-fixtures/heldout-acceptance-tests.log`；独立干跑 `work/pick-heldout-screen/20260917T160410.629703Z`。真实批次 `work/pick-heldout-screen/20260917T160509.272646Z`，独立原始产物复核见同目录 `owner-acceptance.json`。

16场全部输入、实际搜索policy与完整结局校验通过，14胜2负，无异常/超时/未完成。8/8同动作同种子重复组终局完全一致；4/4计划配对可比较。冻结选择文件哈希未变，运行二进制与开发阶段一致，7个既有辅助文件未改动。

| 状态 | 留出种子 | 冻结选择结果 | BuildValue结果 | 配对变化 |
|---|---|---|---|---|
| 起始 | PICKEVAL01 | skip：胜70HP | BACKFLIP：胜67HP | +3HP |
| 起始 | PICKEVAL02 | skip：胜69HP | BACKFLIP：胜70HP | -1HP |
| 后期 | PICKEVAL01 | BACKFLIP：胜32HP | DAGGER_SPRAY：败，敌人167HP | 由败变胜 |
| 后期 | PICKEVAL02 | BACKFLIP：胜44HP | DAGGER_SPRAY：胜13HP | +31HP |

4对中3对更好、0对持平、1对更差；所选动作4/4获胜，BuildValue3/4获胜（描述性计数，不作为独立样本的胜率推断）。双方同胜的3对HP差值为+3、-1、+31，均值+11HP，分母只含这3对，不把由败变胜混入HP平均。起始两种子平均只多1HP，信号很弱；后期两种子方向均正，但仅一个后期牌组/遭遇。重复只用于一致性检查，不把16场说成16个独立收益样本；两状态共享2个种子。

并行阶段88.937秒，实际峰值16，CPU合计891.2501秒，平均10.021逻辑核，16逻辑核下估计子进程平均利用率62.63%。无历史速度倍率推断。16次首次搜索均无观察到的时间边界，首次展开1046–6000节点；66次后续重规划没有逐次遥测，仍不能证明每次搜索均纯节点截断。

## 结论与下一步

这批独立留出结果为直接求解器选牌路线提供了正向的局部证据，支持继续投入有限验证；还不足以宣布Bot实机构筑普遍提升或启动大量标签生产/模型训练。当前样本仅2个代理状态、单角色、固定遭遇且无遗物/药水，上下文覆盖与生产战斗策略一致性尚未通过。

下一步优先审计可用真实选牌状态与生产/研究执行策略差异，冻结一个小规模扩展评估集（目标不超过6个新决策状态，按来源整局/牌组家族分组；不足则如实记录，不用虚构代理充数）。先复用现有批处理与校验工具，不再每批复制新运行器。扩展计算预算待状态数量和覆盖明确后冻结；本阶段不自动加跑，不部署。

PICKEVAL01/02现已使用，只能作为已知回归证据；未来的最终确认必须使用新留出划分，不能继续把这两枚种子称为未见评估集，也不能根据本次结果重新选择本批动作。
