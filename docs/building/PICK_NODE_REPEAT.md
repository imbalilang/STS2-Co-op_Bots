# 节点上限重复性检查（build-pick-node-repeat）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `scripts/building_pick_node_repeat.py`。它是已验收的
`scripts/building_pick_parallel_check.py` 之后的**窄口径重复性**仪器：把已验收基线的
**8 个 fixture/动作组合**（来自 `work/pick-ranking-screen/20260917T141513.123416Z`
的 20 秒格子）在同一 seed `PICKSEL01`、nodes 6000、`--dop 2`、120000ms 软预算、
180000ms 搜索超时下**各重复两次**，在单个 16 路进程池中并发执行，共 **16 场**。

它只回答两件事：同一输入的两个重复是否给出**完全一致的终局语义**，以及每个 fixture
内四个动作的**排序（并列结构/逐对顺序）**在两次重复间是否一致。它不是强度、方差或生产工具。

## 状态

- 交付：`scripts/building_pick_node_repeat.py`、`tests/test_building_pick_node_repeat.py`、本文件。
- 只读复用 `building_pick_parallel_check`（`prepare_run_input`、`execute_run`、
  `compute_timeout` 所在的 900 秒全局期限、`semantic_tuple`/`compare_semantics`/
  `compare_rankings`/`check_runtime_against_baseline`/`load_baseline`/`validate_baseline_source`/
  `with_dop`/`baseline_cells`/`baseline_ranking_for`）、`building_pick_ranking_screen`
  （source 校验、`write_run_inputs`、`build_ranking` 冻结并列规则、`build_command` 的其余搜索参数）、
  `building_pick_input_probe`、`building_eval`、`building_repeat_probe`。
  未修改任何既有 helper、生产代码或模块全局。
- 默认干跑已由实现者执行：`work/pick-node-repeat/20260917T151257.249890Z/`，
  16 个 `dry_run`、0 启动、`runtime.matches_baseline=true`、`baseline.validated=true`；
  计划 16 个 run 的命令除 `--budget-ms`/`--search-timeout-ms`/`--dop`/产物路径/标签外与基线 20 秒命令一致。
- 真实 16 场由 Astra 审查后显式 `--run` 执行；执行前可先看 `plan.json` 的 `runtime_before` 与
  `baseline` 块确认运行时身份未变。本任务未执行 `--run`。

## 冻结设计（运行前冻结）

| 维度 | 值 |
| --- | --- |
| 基线 | `work/pick-ranking-screen/20260917T141513.123416Z/summary.json`（16 场 dop1，PICKSEL01） |
| fixture 源 | 与基线相同的已验收目录 `work/pick-fixtures/20260917T134019.577341Z` |
| 组合 | 8 = 2 fixture × 4 动作（skip / add-BACKFLIP / add-BLADE_DANCE / add-DAGGER_SPRAY），取自基线 **20000ms** 格子 |
| 重复 | 每个组合 2 次（rep1/rep2），唯一 run 名 `<base>-b120000-r<rep>` |
| 启动总数 | **16**，单阶段 `phase=parallel`，每格观测两次，不重试 |
| 与基线命令差异 | 仅 `--budget-ms 120000`、`--search-timeout-ms 180000`、`--dop 2`，加上新产物路径/标签 |
| 种子 / profile / nodes / max-turns / milestone / play-path | PICKSEL01 / Medium / 6000 / 40 / M2 / gameaction |
| 单进程硬限 | **300 秒**（`min(300, remaining)`，在输入准备**之后**重算） |
| 整批硬限 | **900 秒**单调时钟（复用 `parallel.BATCH_DEADLINE_SECONDS`），含输入准备与进程池排队等待 |
| 并发上限 | `ThreadPoolExecutor(max_workers=16)`；所有 future 均被 join，journal 由主线程按完成顺序写 |
| 默认 | 干跑（0 启动）；必须显式 `--run`；输出目录 `work/pick-node-repeat/<UTC 微秒>`，已存在即拒绝 |

## 输入与基线映射

- 规格来源：`building_pick_ranking_screen.prepare_source` 加载的 16 个 spec 中 `budget_ms == 20000` 的 8 个
  （与基线 20 秒格子同源），再改预算为 120000 并复制成 r1/r2。
- **输出路径重建**：不走 `ranking.build_runs`（其目录为 `output_root/<name>`，两个重复会撞名）；
  改用 `parallel.run_dir_path` 生成 `output_root/parallel/<name>`，与 `parallel.prepare_run_input`/
  `parallel.execute_run` 的 `phase/name` 口径一致。
- **显式重映射基线上限格**：`remap_baseline_cells` 把 `(fixture, 动作, 120000)` 映射到基线的
  `(fixture, 动作, 20000)` 格子；缺少该格子或缺少 `raw_hashes.input_sha256` 时在任何启动前抛
  `SourceValidationError`。`parallel.prepare_run_input` 仍用该映射校验生成的 `input.json`
  sha256 必须逐字节等于基线 20 秒格子的输入哈希——因为 input 只含 seed，预算相同的两个重复输入哈希相同。
- 基线结构/原始文件哈希由 `parallel.load_baseline` 重算；source adapter/manifest/历史 request
  与基线 plan 一致由 `parallel.validate_baseline_source` 校验。
- 请求由 `ranking.write_run_inputs` 依冻结规则重生成；`outer-request.json` 因路径/预算不同不做跨批哈希断言。

## 运行与失败口径

- `parallel.execute_run` 原样复用：严格 `exit 0`、payload 无 `error`，超时/非零/缺失/畸形/不一致
  绝不变成失败或排序证据；超时记 `timeout`，无剩余预算记 `not_started`。
- 额外校验实际 `search-policy.json`：`maxDegreeOfParallelism==2`、`softTimeBudgetMilliseconds==120000`、
  `maxExpandedNodes==6000`，不匹配则 `status=mismatch` 且 `eligible=false`。
- 结局用 `building_eval.classify`；输入状态（`status`）与战斗结局（`outcome`）分开保存。
- 采集 runtime `before`/`after`：`before != after` 记 `stable=false`；与基线五项身份哈希不一致记
  `matches_baseline=false`。任一为假或干跑时，**禁用全部比较**并在 `gate_reason` 注明（本批记录全部 16 场）；
  运行时不匹配不在启动前中止，但明确禁用比较，便于复核。
- 批异常被记录为 `error`，summary 仍写盘。

## 汇总口径

- **重复一致（8 对）**：仅当同一 `(fixture, 动作)` 的两个重复都 `status=verified` 且 `eligible`
  且比较未被禁用时，才用 `semantic_tuple`+`compare_semantics` 比较六字段
  （input_status / outcome_status / player_hp / max_hp / enemy_hp / turns）。
  `repeat_agreement` 报告 `available`（可比较数）、`agreed`、`disagreed`、逐对 `changed_fields` 与两次的原始 tuple。
  缺失/无效/不完整只抑制**该对**；重复本身不一致如实报告为 `agreed=false`，不算抑制。
- **排序（区分于精确语义）**：仅当某 fixture 某次重复的四个动作全部完整有效时，才用
  `ranking.build_ranking` 的冻结并列规则排序（胜>败；胜者剩余 HP 越高越前；所有失败并列；turns 仅诊断）。
  `ranking_agreement` 比较 rep1 与 rep2 的 top 集/并列结构/逐对顺序；`agreed` 是排序层面的一致，
  与上面的精确语义一致是**两件事**。
- **历史 20 秒对照为纯描述**：`historical_20000ms_comparison` 用 `parallel.compare_rankings` 对齐基线
  20000ms 排序，`cell_comparison_vs_20000ms` 给出每格相对基线 20 秒的六字段与 `changed_fields`，
  均标 `descriptive=true`。预算、dop、重复次数同时变化，**不归因**，**不报告任何加速比**
  （`historical_speed_ratio` 恒为 `null`）。
- **性能**：并行阶段墙钟、每场 `runtime_seconds`、`peak_actual_overlap`（包裹 `execute_run` 的线程安全
  计数器的实际峰值并发）、实际 `totalCpuMilliseconds` 求和与逻辑 CPU 数；
  `average_busy_cores = 并行CPU/并行墙钟`、`estimated_child_utilization = 忙核/逻辑CPU`，
  任一并行 run 缺 CPU 则 **CPU 不完整并抑制利用率**（缺失值绝不当 0）。
- **初始搜索诊断**：从 `harness-result.json` 读取 `timeBoundaryObserved`、
  `solverMetrics.TotalExpanded/SelectedExpanded`、`pruneCounters.boundary` 与 `replans` 计数。
  `whole_fight_node_only_established` 恒为 `false`：只有初始搜索边界，`replans` 只是计数，
  **即使初始 `timeBoundaryObserved=false` 也不建立"整场纯节点执行"结论**；不采集/不臆造逐回合证据。
- **决策块**：`production_adoption=none`、无新标签、无并发安全结论、无因果、无方差、无加速、
  无整场节点确定性、无训练、未用留出种子。
- 继承环境只传给子进程，从不写入任何产物。

## 输出

`work/pick-node-repeat/<UTC 微秒>/`（已存在即拒绝，不覆盖）：

```
plan.json        # 16 个 run（8 组合 × 2 重复）、固定命令、组合映射、baseline/runtime 校验
journal.jsonl    # 主线程按完成顺序每次尝试一行（16 行）
summary.json     # 状态/结局计数、repeat_agreement、rankings/ranking_agreement、
                 # historical_20000ms_comparison、cell_comparison_vs_20000ms、
                 # initial_search、performance、decision、runs、限制
parallel/<name>/input.json  outer-request.json  stdout.bin  stderr.bin  harness-result.json …
```

## 实际命令

```powershell
# 默认干跑（即“dry only”）：只写 plan/summary/journal，0 启动
python scripts/building_pick_node_repeat.py
# 受限真实执行：需 Astra 审查后显式调用（会新建另一批，不覆盖）
python scripts/building_pick_node_repeat.py --run

# 纯离线测试（假 subprocess、线程安全假时钟、临时 fixture/baseline/runtime）
python -m unittest discover -s tests -p test_building_pick_node_repeat.py
python -m unittest discover -s tests -p test_building*.py
```

## 本轮验证（DeepSeek）

- `python -m unittest discover -s tests -p test_building_pick_node_repeat.py`：**26 项通过**。
  覆盖：固定常量、`_with_flag` 只改指定值、16 个唯一命令且 8 组合各 2 次、命令仅冻结 flag/路径与基线不同、
  干跑 0 启动、计划 8 组合与 `phase=parallel`、拒绝已存在目录、`main` 干跑退出码 0、
  生成 input 逐字节等于基线 20 秒格子且同组合两重复一致、重映射要求基线期望哈希存在、
  16/16 verified 与 policy 校验、真实峰值并发 16（barrier）、journal 16 行、截止后全 `not_started` 且无重试、
  两次一致 8/8、重复不一致如实报告且不算抑制、不完整只抑制该对、缺失/无效抑制其对应对、
  runtime 前后变化禁用比较、缺 CPU 抑制利用率、历史/逐格对照为描述性、初始搜索字段解析且不声称整场节点执行、
  模块全局与 ranking/parallel 全局不被改动、继承环境不外泄。
- `python -m unittest discover -s tests -p test_building*.py`：**271 项通过**（含既有 building 测试）。
- 全部测试注入假 subprocess、临时 fixture 副本与临时 runtime，未启动任何真实进程。
- CLI 干跑 `work/pick-node-repeat/20260917T151257.249890Z`：`attempts=16 ... executed=false`，
  16 个 `dry_run`，`runtime.matches_baseline=true`，`baseline.validated=true`，
  `peak_actual_overlap=16` 为初版错误统计（计入工作线程，已修正），`repeat_agreement.comparisons_enabled=false`（干跑），
  `historical_speed_ratio=null`；输出目录内只有 plan/journal/summary，无任何 input/进程产物。

## 限制（不得省略）

- 两个状态都是 PROXY，不是观察到的奖励画面。
- 每格两次重复无法建立跨 seed 的方差，也无法把重复间差异与并发/调度噪声分离。
- 历史基线是 20000ms dop1；本批是 120000ms dop2，任何差异都不可控，仅作描述。
- 批截止为单调墙钟；慢机会把后续 run 记为 `not_started`，如实记录不隐藏。
- 只读初始搜索边界；不采集逐回合遥测，**不主张整场纯节点执行**。
- 证据可信度上限取决于 harness 构建与其消费的验收 fixture 目录。

## Astra 审查修正与回归

初版把整个工作线程的执行区间当作子进程并发，导致干跑也显示16；现仅在实际 subprocess 调用周围计数，干跑为0。启动前 runtime 与基线不匹配时，保留失败摘要并阻止全部子进程启动；不完整/无效结果不参与历史逐格语义比较。

专项29项、全套274项测试通过。回归日志：`work/pick-fixtures/node-repeat-acceptance-tests.log`。独立干跑：`work/pick-node-repeat/20260917T151905.969082Z`，16个唯一命令的nodes6000/dop2/soft120000ms/search-timeout180000ms/maxturn40已逐项复核；六个既有辅助文件哈希未变。

## 真实批次与独立验收（2026-09-17）

产物：`work/pick-node-repeat/20260917T152011.472975Z`，独立复核：同目录 `owner-acceptance.json`。16场全部输入与实际搜索policy校验通过，14胜2负，无超时/异常/未完成。8组同输入两次重复的胜负、HP、敌人HP和回合数全部相同，2组状态的全部排序/并列关系一致，16场终局也都与历史20秒格子吻合。

| 动作 | 起始代理状态（两次相同） | 后期代理状态（两次相同） |
|---|---|---|
| skip | 胜，63HP，6回合 | 胜，41HP，8回合 |
| BACKFLIP | 胜，57HP，8回合 | 胜，55HP，4回合 |
| BLADE_DANCE | 胜，69HP，6回合 | 胜，23HP，7回合 |
| DAGGER_SPRAY | 胜，69HP，6回合 | 负，0HP，敌人113HP，4回合 |

并行阶段87.750秒，实际子进程区间独立复算峰值16，原始CPU合计775.0624秒，平均8.833逻辑核；16逻辑核下子进程估计平均利用率55.20%。该均值含早期短任务结束后的尾部，不是峰值，也不是单场提速倍率。

16场首次搜索均未观察到时间边界。起始状态首次展开2717–3831节点，后期状态均6000节点；这是节点上限，不要求每场恰好展开6000。全批有46次重规划但没有逐次搜索遥测，故仍不能证明整场每次搜索均未触及时间边界。

结论：保留N16×dop2、nodes6000、软限120秒用于下一步小批验证；本次局部重复性关通过，不等于跨种子收益通过。下一关使用尚未运行的第二开发种子PICKSEL02，同8动作各重复2次、最多16场，参数与硬限保持不变，先审查种子参数化及输入校验，再启动。PICKEVAL01/02仍留出；等两开发种子形成冻结选牌规则后才做独立收益评估。当前不扩产标签、不训练、不改生产评分。
