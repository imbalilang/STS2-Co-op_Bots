# 直接选牌排序筛查（build-pick-ranking-screen）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `scripts/building_pick_ranking_screen.py` 这组**有界、预声明、描述性**的排序筛查。它把已验收的两个 PROXY 决策状态与四个动作，分别放进 **4000ms / 20000ms** 两个单线程完整战斗预算，最多 **16 次启动**。它只回答：候选之间是否有可观察差异、预算改变是否翻转排序、以及完整战斗的实际成本。它**不是收益结论**，不训练模型，不得用于选择训练标签。

## 状态（2026-09-17，DeepSeek 实现，Astra 独立验收通过）

- 交付：`scripts/building_pick_ranking_screen.py`、`tests/test_building_pick_ranking_screen.py`、本文件。
- 验收源：`work/pick-fixtures/20260917T134019.577341Z/adapter-output.json` 与 `cases/manifest.json`（已独立验收），只读复用 `building_pick_input_probe.load_source/build_specs/verify_run_evidence`、`building_eval.classify`、`building_repeat_probe` 的请求清洗与哈希工具，不修改任何模块全局。
- Astra 已独立执行 CLI 干跑（`work/pick-ranking-screen/20260917T141448.465428Z`），并在审查后执行受限 `--run`（`work/pick-ranking-screen/20260917T141513.123416Z`）。16/16 输入校验通过且战斗完整，12 胜 4 负，0 超时/缺失/错误，进程耗时合计 **450.391 秒（约 7.5 分钟）**，低于 900 秒上限。
- `summary.json` 保留全部原始引用与排名；同目录 `owner-acceptance.json` 独立核对原始终局、场景仅改 seed、实际牌组/升级/上下文、实际搜索预算/dop/nodes、原始产物哈希与最优动作集合。原有 500 文件及本轮依赖的 6 个辅助文件未变，运行时 DLL 与配置哈希未变。
- 执行器独立干跑曾被白名单顺序拦截；Astra 已将通配 deny 放在具体 allow 前，且独立干跑与真实执行均完成，不再是卡点。

## 实测结果与判断

以下是单个开发种子 PICKSEL01 的战后 HP；败局按预设规则全部并列，不以敌人剩余 HP 排名。

| 代理状态 | 动作 | 4 秒预算 | 20 秒预算 |
| --- | --- | --- | --- |
| 起始 | skip | 胜，63 HP | 胜，63 HP |
| 起始 | BACKFLIP（BuildValue） | 胜，57 HP | 胜，57 HP |
| 起始 | BLADE_DANCE | 胜，69 HP | 胜，69 HP |
| 起始 | DAGGER_SPRAY | 胜，69 HP | 胜，69 HP |
| 后期 | skip | 败 | 胜，41 HP |
| 后期 | BACKFLIP | 胜，13 HP | 胜，55 HP |
| 后期 | BLADE_DANCE | 败 | 胜，23 HP |
| 后期 | DAGGER_SPRAY（BuildValue） | 败 | 败 |

两组的最优动作集合在两档预算下相同：起始为 BLADE_DANCE/DAGGER_SPRAY 并列，后期为 BACKFLIP。后期三个两两关系从“败局并列”变为有序；**没有出现严格偏好从 A>B 变为 B>A，也没有首选翻转**。但 skip 与 BLADE_DANCE 的胜负随预算改变，说明标量标签仍依赖求解策略与计算预算。初始搜索的 timeBoundaryObserved 只是诊断，不能代表整场重规划都不受时间边界影响。

判断：存在值得继续核验的信号，当前两例中求解器首选都不同于 BuildValue；不构成独立测试收益，更不构成真实整局/多人提升。先重复决定性对照，确认同输入噪声，再使用 PICKSEL02；PICKEVAL01/02 保留未触碰。暂不扩产标签、不训练模型、不改生产评分。

下一小批设计：起始 BACKFLIP 与 BLADE_DANCE、后期 DAGGER_SPRAY 与 BACKFLIP，各在 4s/20s 下用相同 PICKSEL01 再跑一次，共8场，顺序执行、最多120秒/场和600秒/批、nodes6000/maxturns40/dop1；仅比较重复一致性，不据此重新挑规则。起始并列先选 BLADE_DANCE 作为复测代表，是按固定候选列表顺序取首个并列最优，不是看新数据挑选。该批尚未执行。

## 固定设计（运行前冻结）

| 维度 | 值 |
| --- | --- |
| fixture | 2（`silent-starter-proxy`、`silent-late-pm001-proxy`） |
| 动作 | 4（skip、add-BACKFLIP、add-BLADE_DANCE、add-DAGGER_SPRAY） |
| 预算 | 2（`--budget-ms 4000`、`--budget-ms 20000`） |
| 启动总数 | 2 × 4 × 2 = **16**，每格一次，不重复、不重试 |
| 开发种子 | **PICKSEL01**，写入复制的 input 与外层 request；两个来源只差这一处 |
| dop / nodes / profile | 1 / 6000 / Medium |
| search-timeout-ms / max-turns / milestone | 60000 / 40 / M2 |
| 其他开关 | `--no-card-probe --deploy-plan --play-path gameaction`（沿用成功探针） |
| 单进程硬限 | **120 秒** |
| 整批硬限 | **900 秒**单调时钟，输入准备**之后**重新计算；无剩余预算记 `not_started` |
| 默认 | 干跑（不启动任何进程）；必须显式 `--run` |

### 每 fixture 内的顺序

```
skip4s, skip20s, BACKFLIP20s, BACKFLIP4s,
BLADE_DANCE4s, BLADE_DANCE20s, DAGGER_SPRAY20s, DAGGER_SPRAY4s
```

晚期 fixture 使用**整段反向**顺序。单进程串行，无并发。

## 启动前的固定校验

1. `load_source` 校验 adapter `sha256 == manifest.source.adapterSha256`，且 `source.adapterJson` 指向同一文件；`build_specs` 重算八个场景哈希并交叉核对上下文。
2. 复制场景前重新计算源文件哈希，必须等于冻结值，防止复制期间源被改动；复制后的 input 与源 JSON 的**结构差异必须只有 `seed`**（`diff_paths == {"seed"}`）。
3. 外层 request 用 `building_repeat_probe.sanitize_request` 清洗，然后只覆盖 `seed=PICKSEL01`；相对清洗基线也只允许 `seed` 一处变化。
4. 实际运行时只读校验：runtimeconfig 的 `configProperties.Sts2DataDir` 与 `CombatSolverDll`；`CombatSolverDll` 采用**有效值**（`OFFLINE_HARNESS_COMBATSOLVER_DLL` 环境变量优先，否则用配置值）。缺 runtimeconfig / harness / dotnet / `sts2.dll` / 有效 CombatSolverDll，或 `<Sts2DataDir>/sts2.dll` 哈希与 adapter `runtime.sts2.sha256` 不一致，都在**真实启动前**失败（干跑仅记录错误，不启动）。
5. 记录 driver / input-probe helper / repeat helper / classifier / harness / dotnet / adapter / manifest / 历史 request 的路径与 SHA256，以及 adapter 声明的运行时 DLL。**继承环境只传给子进程，从不写入任何产物。**

## 每场验收

- 进程：严格 `exit 0` 且 payload 无 `error`；超时、非零退出、payload error **优先**，即使残留了看似合理的 payload 也不采用。
- 输入：复用 `building_pick_input_probe.verify_run_evidence`，要求实际 `generated-scenario.loadout.json` 与 `harness-result.json` 的 `resolvedScenario` 字段完全一致（seed=PICKSEL01、SILENT、A10、act、遭遇、70/70 HP、空遗物、空药水、牌组多重集与 add 恰好追加一张）。
- 结局：`building_eval.classify` 给出 win/loss/incomplete/…，**输入状态与结局分开保存**。只有 `status==verified` 且结局为完整 win/loss 才 `eligible`；输入不一致**不能**计入完整战斗。
- 记录：win/loss、playerHp、maxHp、enemyHp、turns、`cost_ms`、`wallSeconds`、`timeBoundaryObserved`，以及 input/request/harness-result/loadout/resolved/stdout/stderr 的原始哈希。raw 缺失时不从 summary 推断结果。

## 预声明的描述性排序规则

在任何数据之前冻结：

- 仅当某 fixture/budget 的**四个动作全部**输入保真且有完整 win/loss 结局时才排序；否则 `available=false` 并逐动作列出原因。
- 胜出优先于失败；胜出中剩余玩家 HP 越高越靠前；**所有失败并列**（不使用敌人剩余 HP 作为强弱依据）；同 HP 并列；turns 仅作诊断，不参与排序。
- 保留并列组与 top-action 集。
- BuildValue 动作由 `adapter.bestRewardAllowSkip` 映射（`-1` 为 skip，否则取候选下标），只报告其结局与是否进入 top 组，不宣称统计优势。
- 预算比较：对比两个预算的 top 集与逐对顺序，显式标记变化/并列，**不设优劣门槛**。
- 一个种子、每格一次观测，**不能**建立收益、方差或训练标签；不使用 PICKSEL02，也不使用留出种子 PICKEVAL01/02。

## 输出

`work/pick-ranking-screen/<UTC 微秒>/`（已存在即拒绝，不覆盖）：

```
plan.json        # 16 个 run、固定命令、排序规则、源/运行时 provenance
journal.jsonl    # 每次尝试一行
summary.json     # 状态计数、eligible_completed、逐 run 记录、排序、预算比较
<run>/input.json  <run>/outer-request.json
<run>/stdout.bin  <run>/stderr.bin
<run>/harness-result.json（由 harness 写）
```

## 实际命令

```powershell
# 默认干跑：只写 plan/summary，不启动进程
python scripts/building_pick_ranking_screen.py
# 受限批量（本轮已由 Astra 审查后执行；再次调用会新建另一批）
python scripts/building_pick_ranking_screen.py --run

# 纯离线测试（假 subprocess，无真实 harness）
python -m unittest discover -s tests -p test_building_pick_ranking_screen.py
python -m unittest discover -s tests -p test_building*.py
```

## 本轮实际验证

- `python -m unittest discover -s tests -p test_building_pick_ranking_screen.py`：**39 项通过**（配置、16 唯一计划与反向顺序、干跑零启动、种子替换与“只差 seed”、源哈希守卫、截止后不启动、超时/非零/缺失/畸形/无效 loadout 排除、胜负 HP 与并列、全败并列、不完整禁用排序、两组预算 top 集/并列翻转、运行时 provenance 与游戏哈希不一致、环境不外泄、不覆盖、模块全局不变、真实验收源干跑）。
- `python -m unittest discover -s tests -p test_building*.py`：**208 项通过**（含既有 building 测试）。
- 全部测试注入假 subprocess、临时 fixture 副本与临时 runtime，未启动任何真实进程。
- Astra 独立复跑全部 **208 项测试通过**，日志 `work/pick-fixtures/ranking-acceptance-tests.log`。真实16场结果见上；选择种子 PICKSEL02 与留出种子 PICKEVAL01/02 未使用。

## 限制（不得省略）

- 两个状态都是 PROXY，不是观察到的奖励画面；act0 起始遭遇是 fixture 代理。
- 遗物/药水为空是设计消融，改变真实牌局语义；这里只作 fixture 保真断言。
- 单种子、每格一次观测无法分离运行间噪声，不能建立收益、方差或训练标签。
- 4s 与 20s 单线程各只提供一个参照；本筛查不宣布任一预算更强。
- 批截止为单调墙钟；慢机会把后续 run 记为 `not_started`，如实记录不隐藏。
- 证据可信度上限取决于 harness 构建与其消费的验收 fixture 目录。
