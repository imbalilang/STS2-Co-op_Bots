# 预算 / 内部 dop 对照校准（build-budget-calibration）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


> **路径提示（2026-09-20）**：本文提到的 `CombatSolver-main/`、`CombatSolver-upstream-main/`、
> `StS2.RandomForeseer-0.13.14/` 已在本次目录整理中移入 `third_party/` 下。
> 原文保留不改写；引用路径请自行加 `third_party/` 前缀。相关脚本已同步更新。


本文件记录 `scripts/building_budget_probe.py` 的固定设计、它实际测量的是宿主里哪几个强制点，以及在什么条件下才允许进入下一步排名试验。它同时是 `work/budget-calibration/<UTC 微秒>/` 产物的判读口径。

## 目的

重复性探针（`docs/building/MEASUREMENT_STATUS.md` 的「重复性探针」一节）已经用同一 PM001 输入跑了四场，结果不一致，但它只有一个软预算、一个 dop，无法把「软时间边界」「节点上限」「内部并行度」「进程调度」分开。本校准用固定 2×2 设计做**有界**分离，并明确：

- 工具是否完整跑完（tool completion）；
- 同一格内的两次重复是否终局语义一致（repeatability finding）；
- 两者是不同的问题，本工具不设置研究 gate，也不批准模型。

本工具**不**做因果或显著性结论：每格只有 2 次重复，且固定牌组没有候选集合，做不了排序。

## 固定 2×2 设计

| 因子 | 水平 |
| --- | --- |
| 软预算 `--budget-ms` | 4000 / 20000 |
| 内部并行度 `--dop` | 1 / 2 |

- 格 = `b<budget>-d<dop>`，共 4 格；每格 2 次重复，最多 **8 次启动**。
- 全部子进程**顺序执行**，没有跨进程并发。
- 顺序采用区组设计以限制漂移：
  - block 1（replicate 1）：`(4000,1) → (20000,2) → (4000,2) → (20000,1)`
  - block 2（replicate 2）：block 1 的**逆序**
- 固定的其余参数（8 条命令完全一致）：`--profile Medium`、`--nodes 6000`、`--search-timeout-ms 60000`、`--max-turns 40`、`--play-path gameaction`、`--no-card-probe`、`--deploy-plan`、`--milestone M2`。
- 输入固定为 `work/oracle/shiv-pm/b4000-hp1.0-dmg1.0-P0M0-PM001`（SILENT、seed PM001、`SOUL_NEXUS_ELITE`），每次复制**逐字节相同**的 `input.json`。
- 只有两个固定缩放环境变量：`OFFLINE_HARNESS_SOULNEXUS_HP_SCALE=1.0`、`OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE=1.0`。完整子进程环境只传给子进程，绝不写进任何产物。

上限：

- 单进程 `min(120, 剩余)` 秒，且在**输入准备完成后重新计算**，准备耗时可占用批次预算；
- 整批 720 秒单调时钟；
- 最多 8 次启动；到期的用例记为 `not_started`，不是战斗结果。

## 工具运行方式

```powershell
python scripts/building_budget_probe.py          # 默认 dry run：只写 plan，不启动任何进程
python scripts/building_budget_probe.py --run    # 真正执行 8 次
```

产物目录 `work/budget-calibration/<UTC 微秒 Z>/`：

- `plan.json`：任何启动之前写好，含 8 条命令的完整 argv、来源 fixture 与二进制指纹；
- `journal.jsonl`：每次尝试后立即追加并 flush，批处理中途失败也保留已完成的尝试；
- `summary.json`：即使批次失败也会写出；含每格汇总、逐行记录、跨格配置表；
- 每个用例子目录：`input.json`、`outer-request.json`、`stdout.bin`、`stderr.bin`、`harness-result.json`。

目录时间戳唯一；已存在的目录或被占用的输出文件会被拒绝（`FileExistsError`）。

CLI 退出码：

| 情形 | 退出码 |
| --- | --- |
| dry run | 0 |
| `--run`，8 次全部终局完整（win/loss） | 0（即使格间/格内不同） |
| `--run`，存在未完整/超时/错误/`not_started` | 1 |

## 宿主里实际的预算强制点

先纠正一个常见误解：`fixedSearchBudget = true` **不等于**时间预算被忽略。离线宿主在 Evaluate 模式下把 `--budget-ms` 直接写成求解器 profile 的软时间预算，求解器同时按墙钟和节点数停止。

### 软时间预算（`--budget-ms`）

- `CombatSolver-upstream-main/tools/OfflineSearchHarness/ModRuntime.cs` `SolveEvaluate`（`ModRuntime.cs:282-328`）：
  - `ModRuntime.cs:299-303` 把 `Profile.SoftTimeBudgetMilliseconds` 替换为 `--budget-ms` 的毫秒值（其它 profile 维度照抄）。
  - `ModRuntime.cs:314-315` 用该 profile 建立 `CombatBeamSolver`。
- `CombatSolver-upstream-main/src/Search/CombatBeamSolver.Phases.cs`：
  - `SolveCore` 的主循环条件（`CombatBeamSolver.Phases.cs:1346-1350`）同时要求 `_run.Expanded < _profile.MaxExpandedNodes` **且** `!timeBudgetReached`。
  - `CombatBeamSolver.Phases.cs:1494-1517`：一旦 `stopwatch.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds`，就置 `timeBudgetReached = true`、打 `SEARCH_TIME_BUDGET`、强制结束当前回合层。
  - `CombatBeamSolver.Phases.cs:1365-1370`：每个回合层还按 `剩余软预算 / 剩余保留层数` 分配本地时间片，`CombatBeamSolver.Phases.cs:1418-1445` 在本地时间片或层节点配额用尽时打 `TURN_LAYER_BUDGET reason=time|nodes` 并切层。

所以：**同一次搜索里，软时间预算可能在节点上限之前先触发**，20 000 ms 也不会变成逐位确定。

### 节点上限（`--nodes 6000`）

- `CombatBeamSolver.Phases.cs:1382-1387` 按层分配节点预算；`CombatBeamSolver.Phases.cs:1861-1871` 在 `_run.Expanded >= _profile.MaxExpandedNodes` 时释放并停止。
- 最终 `SolverResult.BoundaryReason` 的判定在 `CombatBeamSolver.Phases.cs:474-485`：时间触发 → `TimeLimit`；节点触发 → `NodeLimit`。

### 内部并行度（`--dop`）

- `ModRuntime.cs:135-144`（`ApplyUnattendedOverrides`）把 `--dop` 写入 `SearchMaxDegreeOfParallelism`。
- `src/Runtime/SolverController.cs:480-499`（`CaptureSearchPolicy`）把它接进 `SearchPolicySnapshot.MaxDegreeOfParallelism`（`SearchPolicySnapshot.cs:11`）。
- `CombatBeamSolver.Phases.cs:137-150` 在 `SolveCore` 里 `Math.Clamp(policy.MaxDegreeOfParallelism, 1, ProcessorCount)` 得到 `expansionParallelism`；未建立 NoGC 区域时再压到 2。并行度影响 worker lane 的调度，是待检验的波动来源；不能只凭这段实现断言它必然导致结果不确定。

### 单次搜索硬闸（`--search-timeout-ms`）

- `ModRuntime.cs:295-299` 明确注释：`--budget-ms` 是求解器**软**预算，真正的硬闸是 `--search-timeout-ms`（这里取 `max(1000, timeoutMilliseconds)`）。
- `ModRuntime.cs:317-319` 用 `loop.RunUntilCompleted(solve, searchTimeout, ...)` 在硬闸内等待。本校准固定 60 000 ms，高于两个软预算，因此单次搜索也可能先达到节点上限或其他终止条件；硬闸是超长尾的兜底。
- `Program.cs:328-347` 的 `DescribeBudget` 会把 `MaxExpandedNodes`、`MaxDegreeOfParallelism`、`BudgetMilliseconds`、`fixedSearchBudget=true` 一起写进产物，可用于逐行核对。

### `FixedBudget` 到底约束什么

`SearchPolicySnapshot.FixedBudget`（`SearchPolicySnapshot.cs:9`）在 Evaluate 模式下只表示“固定预算口径”。在协调器模式里它决定 `CombatSearchCoordinator.cs:342-343` 是否在主搜索后返回、不再执行 `EscalateSearchWhenNoVictory` 的升级重搜。本校准用 `--search-mode` 默认的 Evaluate，因此协调器不参与，更不会解除软时间预算。

### `timeBoundaryObserved` 的含义

- `Program.cs:192` 写出 `payload["timeBoundaryObserved"] = outcome.TimeBoundaryObserved`。
- `ModRuntime.cs:305-313` 在诊断里匹配 `TURN_LAYER_BUDGET reason=time`；`ModRuntime.cs:326` 再或上 `result.BoundaryReason == SearchBoundaryReason.TimeLimit`。
- 因此它是**初始搜索**触发时间边界的指示，**不是**整局所有重搜都触发时间边界的证明：M3.1 部署阶段每次重搜都会重新求解（`Program.cs:217-242` 的 `Replan`）。

### `replans` 的实际形状

- `Program.cs:219` 初始化 `int replans = 0;`，`Program.cs:237-242` 在每次部署重搜时自增，`Program.cs:230` 在 M3.1 结束后写 `payload["replans"] = replans;`。它是整数计数（历史 fixture 中为 17），不是逐次明细。

## 每行记录

每个用例在 `summary.json` 的 `runs` 中保留：

- `budget_ms`、`dop`、`replicate`、`cell`；
- `provenance.input_sha256`、`provenance.request_sha256`（复制件），以及来源 fixture 的 `source_input_sha256`、`source_request_sha256`；
- `provenance.driver` / `classifier` / `harness` / `runtime` 的路径与 SHA256（driving 脚本、`building_eval.py`、`OfflineSearchHarness.dll`、`dotnet.exe`）；
- `args`（实际 argv）、`timeout_seconds`、`timeout_before_preparation_seconds`、`elapsed_before_launch`、`elapsed_before_subprocess`、`runtime_seconds`、`exit`、`timed_out`；
- `stdout_path` / `stderr_path` / `raw_result_path`、`harness_result_sha256`；
- 共享分类器 `building_eval.classify` 的 `status` / `reason` / `player_hp` / `max_hp` / `final_enemy_hp` / `turns` / `fight_wall_ms`；
- `time_boundary_observed`、`solver_metrics`、`prune_counters`、`replans`、`search_policy`、`budget_payload`。

结果产生后会对照复制后的 `input.json` 校验 `resolvedScenario` 的 seed / character / encounter（`Program.cs:88`）。不一致直接把该行标为 `invalid`；这是场景没有真正按请求解析的信号。

## 汇总口径

`summary.json`：

- `batch.attempts_accounted`：8 次是否都有记录（含 `not_started`）；
- `status_counts`、`complete`、`terminal_complete`；
- `tool_completed`：工具是否完整跑完本批（与逐位重复无关）；
- `repeatability_finding`：终局是否完整、格内语义是否一致的文字结论；
- `study_gate_passed`：**恒为 `null`**，本工具不判定研究 gate，也不批准模型；
- `cells`：每格完整 status 计数、`semantic_equal`（**仅当两重复都完整时才比较**，否则 `null`）、`player_hp`/`turns` 范围、`time_boundary.observed/known`、逐行紧凑指标；
- `cross_cell`：四格并列的配置与结果，**不做显著性/因果声明**；
- `notes` / `limitations`。

判读规则：

1. 先看 `tool_completed` 与 `terminal_complete`，再看 `repeatability_finding`。工具跑完 ≠ 可重复。
2. 格内终局语义相等只在两重复都为 win/loss 时报值；出现 `incomplete`/`timeout`/`error`/`not_started` 时该格不可比。
3. 两次重复不足以可靠估计方差或判断候选排序；不得从 `cross_cell` 读出预算或 dop 的“效果”。
4. `timeBoundaryObserved` 只作初始搜索的提示，不能证明部署重搜的时间情况。
5. 本工具不重建求解器：它启动的是 `plan.json` 记录的既有 Release DLL，其 SHA256 写入 provenance。

## 限制

- 两重复只能筛查，不能给出置信区间或因果。
- 每行 `solver_metrics` 只在记录层面保留；除 boundary / expanded 之外不用于“变好”比较。
- 批次截止是墙钟；慢机器会把后面的用例记成 `not_started`，这是记录而非隐藏。
- 真实战斗的结论仅与该次 harness 构建同等级可信。

## 下一阶段排名试验的最低要求

在通过本校准、且 `repeatability_finding` 未显示格内终局语义不一致之前，不进入直接选牌排名。排名试验至少要：

1. **跳牌 + 真加牌两种动作都覆盖**：候选必须包含“跳过当前奖励”和“实际加入一张牌”两类，不能只比较加牌。
2. **留出种子（heldout seeds）**：调参与验收种子分离，且种子集合先冻结再跑。
3. **`currentBuildValue` 基线**：同时评估“当前构筑的直接价值”作为对照基线，而不是只比较候选之间的相对分。
4. 复用本工具的 provenance（输入哈希、driver/classifier/harness/runtime SHA）与分类器，避免自造胜负判定。

## 可能的预算重调

若长预算格（20 000 ms）的**超时率偏高**（`terminal_complete` 因 `timeout`/`not_started` 失败，或时间边界计数接近全格），说明 20 000 ms 在本机不合适，应先下调长预算或延长批次/单进程上限后重跑本校准，再谈排名。反之，若 4 000 ms 与 20 000 ms 的结果差异只来自噪声（两重复不一致），也不得据此声称“增加预算无用”。

## 本次实测与验收（2026-09-17）

DeepSeek 实现工具和测试；Astra 审查修正汇总字段、失败分类优先级与文档表述，并执行真实验收。97 项离线测试通过。八场全部完整获胜、退出0，无超时或缺失，子进程耗时合计313.656秒；总上限720秒未触及。

| 软预算 / 内部线程 | 两次剩余生命（最大70） | 两次回合 | 两次耗时（秒） | 初次展开节点 | 本例终局相同 |
| --- | --- | --- | --- | --- | --- |
| 4秒 / 1 | 46 / 46 | 9 / 9 | 39.000 / 39.063 | 2730 / 2831 | 是 |
| 4秒 / 2 | 5 / 41 | 10 / 8 | 60.891 / 40.547 | 3127 / 3227 | 否 |
| 20秒 / 1 | 31 / 31 | 6 / 6 | 38.422 / 37.875 | 6000 / 6000 | 是 |
| 20秒 / 2 | 31 / 31 | 6 / 6 | 28.687 / 29.171 | 6000 / 6000 | 是 |

四份4秒读数的初始时间边界标记均为true，四份20秒读数均为false。八份实际牌组输入哈希和初始root摘要哈希一致；逐份核对实际searchPolicy的预算/线程/节点上限与计划一致，原始产物哈希验证通过。20秒格的初始搜索达到节点上限，不等于已证明所有后续重搜都不受墙钟影响。

**判读：** 4秒双线程配置在本例不稳定；本次4秒单线程终局相同，但展开节点数不同，不能称作搜索逐位确定。20秒两种线程配置在本例终局相同，是下一步可比较的候选配置，仍未证明跨种子/牌组稳定。

**预算更大也没有在本例取得更高剩余生命：** 4秒单线程剩46，20秒剩31，且后者结束更早。两个指标方向不同，不能偷换目标宣称20秒更强。搜索启发式、重搜路径及多指标取舍需进一步检查；本小样本不归因具体原因。此结果再次说明不能把求解器配置当作客观强度真值。

**下一步：** 比较4秒单线程与20秒单线程对真实候选添加/skip的排序敏感性，先准备两个有明确来源和上下文的决策状态以及当前BuildValue对照。20秒双线程只作为后续吞吐优化候选。没有候选集合的本批试验不回答选牌收益，也不批准大批标签或模型训练。

证据：

- `work/budget-calibration/20260917T130752.487892Z/summary.json`，以及该目录plan/journal/原始输入与结果。
- `work/budget-calibration/acceptance-tests.log`：97项测试。
- `work/budget-calibration/acceptance.json`：独立验收摘要。
- `work/budget-calibration/runtime-provenance-before.json`：实际加载求解器DLL、游戏DLL及runtimeconfig哈希；验收时核对未变。
- `work/budget-calibration/prior-diagnostics.json`：此前四场的初始状态和搜索量对照。

这些work目录不随普通Git克隆交付，交接时需另行保留。
