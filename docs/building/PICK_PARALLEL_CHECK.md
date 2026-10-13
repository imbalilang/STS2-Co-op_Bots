# 串行 vs 16 路并行选牌复跑（build-pick-parallel-check）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `scripts/building_pick_parallel_check.py`。它把已验收的排序筛查基线
`work/pick-ranking-screen/20260917T141513.123416Z`（16 场 `--dop 1`，种子 `PICKSEL01`，
nodes 6000，4000/20000ms，`--max-turns 40`）用 `--dop 2` 重放：先串行跑 4 场"新鲜参照"，
再用 16 路进程池并发重放全部 16 个基线格子。它只回答：输入是否仍逐字节一致、实际 harness
是否接受 `dop=2`/budget/nodes、以及并发重放的结果与描述性排序相对串行基线发生了什么变化。
它报告本批实际吞吐与结果差异，不构成普遍收益或并发确定性结论，也不产生训练标签。

## 状态（2026-09-17，DeepSeek 实现，Astra 修订并独立验收）

- 交付：`scripts/building_pick_parallel_check.py`、`tests/test_building_pick_parallel_check.py`、本文件。
- 只读复用 `building_pick_ranking_screen`（source 校验、`build_runs`/`write_run_inputs`、
  记录/结局/排序函数）、`building_pick_input_probe`、`building_eval`、`building_repeat_probe`。
  未修改任何既有 helper 或生产代码，未改动任何模块全局。
- 默认干跑，已由实现者执行：`work/pick-parallel-check/20260917T144644.981758Z/`
  （另一次干跑 `…144634.293693Z` 同源）。干跑 0 启动，20 个 `dry_run` 记录，
  `runtime.matches_baseline=true`，`baseline.validated=true`，计划与基线命令仅在
  `--dop` 和产物路径/标签上不同。
- Astra 已执行受限批次：`work/pick-parallel-check/20260917T144951.379500Z/summary.json`；同目录 `owner-acceptance.json` 独立核对20场原始结局、输入哈希、实际牌组/升级/HP、实际搜索策略、CPU统计、峰值16个重叠进程与排名。20场全部完整、输入与策略均通过，无超时/缺失；运行时及既有辅助文件哈希未变。

## 本次实测

| 指标 | 结果 |
| --- | --- |
| 历史 N1×dop1 的16场进程耗时合计 | 450.391秒 |
| N16×dop2 同16格并行阶段墙钟 | **79.063秒** |
| 历史吞吐比 | **5.697倍**（不是受控或单场加速） |
| 额外4场 N1×dop2 参照墙钟 | 162.156秒；不计入16场吞吐比分母 |
| 并行阶段累计子进程 CPU | 481.6564秒 |
| 平均忙核 / 16逻辑核心估算利用率 | **6.092核 / 38.08%**；旧串行约0.99核/6.2% |
| 与旧串行终局语义一致 | **12/16**：4秒4/8；20秒8/8 |

CPU 利用率仅使用并行16场的 CPU/并行阶段墙钟。两阶段总 CPU 723.2658秒另列；不得用该总数除以79.063秒。平均利用率包含尾部少数长场次，不能等同于并发启动时的峰值利用率。

起始牌组在两档预算下全部保持旧结果。后期20秒也全部保持原有胜负、生命、敌方剩余生命与回合：BACKFLIP胜55、skip胜41、BLADE_DANCE胜23、DAGGER_SPRAY败，排名相同。后期4秒的4场终局语义均变化，其中 BACKFLIP 从旧 N1dop1 的胜13HP，经新 N1dop2 的胜33HP，变为 N16dop2 的败局；其余三动作仍败，原来的 BACKFLIP 唯一首选变成四个败局并列。不是出现了可靠的新优胜动作，而是短时限下丢失了区分能力。

判定：**N16×dop2 的吞吐优势成立于本次工作负载；4秒软时限不能不经校验直接搬到并行标签生产。** 20秒本批8/8一致，只支持继续小规模验证，不证明跨牌组/种子稳定。新4场参照仍是单次观测，不能把变化单独归因于并发或 dop。

后续批量调度保留 N16×dop2，不退回全批串行。下一关固定nodes6000、放宽搜索软时限作为保护上限，重复当前8个动作各两次（N16共16场），检查重复性和可见时间边界；保留进程/整批硬限，并明确不能仅凭初次搜索达到节点上限宣称整场纯节点确定性。PICKSEL02和PICKEVAL01/02仍未使用，暂不训练模型或改生产评分。旧8场串行复测安排由本并行路线替代。

## 固定设计（运行前冻结）

| 维度 | 值 |
| --- | --- |
| 基线 | `work/pick-ranking-screen/20260917T141513.123416Z/summary.json`（16 场 dop1，PICKSEL01） |
| fixture 源 | 与基线相同的已验收目录 `work/pick-fixtures/20260917T134019.577341Z` |
| 第一阶段 | 4 场串行"新鲜参照"，`--dop 2`：晚期 fixture 的 BACKFLIP、DAGGER_SPRAY × 4000/20000ms |
| 第二阶段 | 16 场并发 `--dop 2`，复现基线全部 16 格（fixture × 动作 × 预算） |
| 启动总数 | 4 + 16 = **20**，每格观测一次，不重试 |
| 与基线命令差异 | 仅 `--dop`（1→2），加上 `--request`/`--out`/`--label` 指向新产物路径 |
| 种子 / profile / nodes | PICKSEL01 / Medium / 6000 |
| search-timeout-ms / max-turns / milestone / play-path | 60000 / 40 / M2 / gameaction |
| 单进程硬限 | 参照 **120 秒**；并行 **300 秒**（允许争用） |
| 整批硬限 | **900 秒**单调时钟，跨两阶段全局；每次启动取 `min(phaseCap, remaining)`，在输入准备**之后**重算 |
| 并发上限 | `ThreadPoolExecutor(max_workers=16)` |
| 默认 | 干跑（0 启动）；必须显式 `--run` |

参照顺序沿用基线晚期 fixture 的反向顺序（DAGGER_SPRAY 4s/20s、BACKFLIP 4s/20s）。参照阶段
一次只跑一场；并行阶段最多 16 场同时存在，所有 future 都会被 join。

## 基线校验（把历史结果当作参照之前）

1. 基线 summary 结构校验：工具、`executed`、seed、batch（16 场、dop1、nodes6000、maxturns40）、
   16 个 run 且输入状态均为 `verified`。
2. **逐文件重算原始哈希**：每场的 `input.json`、`outer-request.json`、`stdout.bin`、
   `stderr.bin`、`harness-result.json`、`evidence/generated-scenario.loadout.json`、
   `evidence/generated-scenario.resolved.json` 必须与 summary 中对应哈希一致；缺失或不一致
   在**任何启动之前**抛出 `BaselineValidationError`。
3. 基线 plan 的 adapter/manifest/history request 哈希必须与当前已验收 fixture 源一致；
   不一致抛 `SourceValidationError`。
4. 当前 runtime 的五项身份哈希（runtimeconfig / harness / dotnet / `sts2.dll` /
   有效 CombatSolverDll）必须与基线 `runtime` 一致；真实执行前不一致直接中止；干跑记录不中止。
5. **运行时前后一致**：批前后各采集一次 runtime；`before != after` 时标记 `stable=false`，
   并抑制历史吞吐比与对比结论。

## 输入与每场验收

- 复用 `building_pick_ranking_screen.write_run_inputs` 从已验收源生成 input/request；生成后的
  `input.json` sha256 必须**等于**基线同格子 `input.json` 的 sha256，否则在启动前抛
  `SourceValidationError`。request 因路径不同只做重生成，不做跨批哈希相等断言。
- 每场继续复用 `building_pick_input_probe.verify_run_evidence` 校验实际 loadout/resolved
  （seed=PICKSEL01、SILENT、A10、act/遭遇、70/70 HP、空遗物药水、牌组多重集）。
- 结局用 `building_eval.classify`；**输入状态（`status`）与战斗结局（`outcome`）分开保存**。
  超时/非零退出/缺失/畸形/不一致/incomplete **绝不**变成失败或排序证据。
- 额外校验实际 `search-policy.json`：`maxDegreeOfParallelism==2`、`softTimeBudgetMilliseconds`
  等于该格预算、`maxExpandedNodes==6000`；不一致计 `policy ok=false`。

## 对比与判定

- 仅当某 fixture/budget 的四个动作全部 `verified` 且结局完整（win/loss）时，才用冻结规则排序
  （胜>败；胜者剩余 HP 越高越前；所有失败并列；turns 仅诊断）。任一格不完整只抑制其**自身**
  的 fixture/budget 对比。
- 逐格对比：基线 vs 并行，分别给出输入状态、结局状态、playerHp、maxHp、enemyHp、turns，
  并区分**精确语义匹配**（六字段全等）与 **top 集合匹配**/并列结构/逐对顺序。
- 4 场新鲜参照：分别与历史 `dop1` 同格、与并发 `dop2` 同格并列展示差异；只展示单次观测差异，
  **不归因**。并发与 dop 同时改变，任何差异都不能归因于并发。
- 决策块显式声明：`production_adoption=none`、无自动采用、无新标签、无并发安全结论、
  无因果结论。top 集合相同也**不**等于并发安全。
- BuildValue 动作仍由 `adapter.bestRewardAllowSkip` 映射；本工具不宣布更强。

## 性能口径

- 记录两阶段墙钟、每场 `runtime_seconds`、启动数与进程完成数。
- 汇总实际 harness `totalCpuMilliseconds`（缺失值**排除**并计数，不当作 0）、
  `os.cpu_count()` 逻辑 CPU、CPU 秒/并行墙钟（平均忙核）、以及 /逻辑 CPU（仅表示子进程估计占用）。
- `historical_throughput_ratio = 450.391 / 并行阶段墙钟`，仅在**16 场全部完整有效**且 runtime
  前后一致、且与基线一致时计算；明确标注为"历史吞吐比"，**不是**受控加速，也**不是**单场加速。
- 不采集/不打印整体环境；继承环境只传给子进程，从不写入产物。

## 输出

`work/pick-parallel-check/<UTC 微秒>/`（已存在即拒绝，不覆盖）：

```
plan.json        # 20 个 run（4 参照 + 16 并行）、固定命令、baseline 与 runtime 校验
journal.jsonl    # 主线程按完成顺序每次尝试一行（20 行）
summary.json     # 状态/结局计数、policy 校验、逐 run、对比、性能、决策、限制
reference/<name>/…   # 4 场串行参照
parallel/<name>/…    # 16 场并发重放
```

## 实际命令

```powershell
# 默认干跑：只写 plan/summary/journal，不启动进程
python scripts/building_pick_parallel_check.py
# 受限真实执行：需 Astra 审查后显式调用（会新建另一批，不覆盖）
python scripts/building_pick_parallel_check.py --run

# 纯离线测试（假 subprocess、线程安全假时钟、临时 fixture/baseline/runtime）
python -m unittest discover -s tests -p test_building_pick_parallel_check.py
python -m unittest discover -s tests -p test_building*.py
```

## 本轮实际验证（DeepSeek）

- `python -m unittest discover -s tests -p test_building_pick_parallel_check.py`：
  **35 项通过**。覆盖：固定常量、`compute_timeout` 相位上限/截止、`with_dop` 只改 dop 且不改原列表、
  20 个唯一 `(phase,name)` 计划与冻结顺序、并行命令相对基线命令仅 dop/路径不同、干跑 0 启动、
  拒绝已存在输出目录、生成 input 与基线逐字节同哈希、非匹配基线输入被拒、20/20 verified 与
  policy 20/20、journal 20 行、环境不外泄、并行 16 路真实重叠（barrier）、参照串行（并发峰值 1）、
  截止后 `not_started`、准备后重算 timeout、超时/非零/缺失/畸形/无效 loadout 排除、
  缺 policy 计数、基线原始文件损坏事前拒绝、基线源哈希不一致拒绝、
  精确语义差异 vs top 集合稳定、不完整只抑制本组、CPU 缺失不记 0、runtime 前后变化抑制吞吐比、
  runtime 与基线不一致中止、模块与 ranking 全局不变。
- `python -m unittest discover -s tests -p test_building*.py`：**243 项通过**（含既有 building 测试）。
- 全部测试注入假 subprocess、临时 fixture 副本与临时 runtime，未启动任何真实进程。
- 实现者已执行 CLI 干跑 `work/pick-parallel-check/20260917T144644.981758Z`：
  `attempts=20 reference=4 parallel=16 dop=2 executed=false`，20 个 `dry_run`，
  `runtime.matches_baseline=true`，`baseline.validated=true`，`comparison_available=false`（干跑），
  历史吞吐比为空。

## 剩余运行状态

- Astra 补充CPU分阶段回归测试，并将实际搜索策略缺失/不匹配变为不可排名场次；最终 **245项相关测试通过**，日志 `work/pick-fixtures/parallel-acceptance-tests.log`。独立干跑 `20260917T144921.778972Z` 后完成上述真实批次。

## 限制（不得省略）

- 两个状态都是 PROXY，不是观察到的奖励画面。
- 单种子、每格一次观测无法分离运行间噪声，也无法把并发影响与 `dop1→dop2` 改变分开。
- 基线为历史结果；其原始哈希可从磁盘复核，但其运行时无法再次观测。
- 450.391 秒吞吐比是描述性历史比值，不是受控或单场加速。
- 批截止为单调墙钟；慢机会把后续 run 记为 `not_started`，如实记录不隐藏。
- 证据可信度上限取决于 harness 构建与其消费的验收 fixture 目录。
