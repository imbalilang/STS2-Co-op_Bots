# 直接选牌输入保真探针（build-pick-input-probe）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `scripts/building_pick_input_probe.py` 这组**离线输入保真探针**的目的、固定预算、证据字段与限制。它只回答一个问题：离线求解器实际解析出的角色、牌组、升级、生命、种子、幕与遭遇，是否与已验收 fixture 一致。它**不是选牌收益结论**，也不比较预算，不批准任何排序或强度。

## 状态（2026-09-17，DeepSeek 实现，Astra 验收）

- 实现：`scripts/building_pick_input_probe.py`、`tests/test_building_pick_input_probe.py`、本文件。
- 验收源：`work/pick-fixtures/20260917T134019.577341Z/adapter-output.json` 与 `cases/manifest.json`（Astra 已独立验收）。
- 验收条件：八个**受限单回合**输入检查；牌组及升级按多重集合精确比较、上下文字段一致；不宣称强度。
- Astra 已审查并执行 `--run`，8/8 通过。结果：`work/pick-input-probe/20260917T135849.382179Z/summary.json`；独立复核：同目录 `owner-acceptance.json`。

实际进程耗时合计 62.47 秒，0 超时、0 缺失、0 不一致；8 场均为单回合后仍未完成，未计为败局。起始牌组 skip 为 13 张、加牌为 14 张；后期分别为 32/33 张，原有升级完整保留。实际角色、种子、幕、遭遇、A10、70/70 HP、空遗物与空药水均通过。运行前后宿主/求解器/游戏 DLL 与 runtimeconfig 哈希一致，游戏 DLL 与评分适配器一致，记录在 `work/pick-fixtures/input-probe-runtime-before.json`；原有 500 文件哈希未变。

## 目标与边界

- 目的：把两个决策 fixture × 四个动作（skip + 三个候选新增）共 **8 个冻结场景**交给真实离线 harness，回读 **实际 loadout 证据**，证明求解器解析的牌组/升级/上下文与 adapter 导出一致。
- 只做输入保真：不排名、不算收益、不比较 4s/20s、不做重复性结论。
- `--max-turns 1` 是**故意限制回合数**：若战斗未结束则记 incomplete，不能当作败局；即使某场一回合结束，也不能据此说预算、重复性或模型强弱。
- 失败闭合：缺证据/证据畸形/上下文不一致一律显式失败，不静默降级。

## 验收源与校验（启动前）

探针在启动任何进程前：

1. 读取 `adapter-output.json` 与 `cases/manifest.json`；
2. 校验 adapter `sha256 == manifest.source.adapterSha256`，且 `manifest.source.adapterJson` 指向同一文件；
3. 逐个动作重算场景 JSON 的 `sha256`，必须等于 manifest 记录的哈希，且文件必须位于验收源目录内（阻止路径穿越）；
4. 交叉校验 adapter fixture 与 manifest `context` 的 `seed/characterId/ascension/actIndex/encounterId`；
5. 记录 adapter 声明的运行时 DLL 哈希（`BuildingDecisionProbe/CoopBots/sts2/GodotSharp/Harmony`），存在的构建产物会再算实际哈希并标记 `ok`。

任一步失败：抛 `SourceValidationError`，**不创建输出目录、不启动任何进程**。

## 八个动作与固定命令

从 manifest 展开，恰好 8 个动作（顺序固定）：

```
silent-starter-proxy-skip / add-BACKFLIP / add-BLADE_DANCE / add-DAGGER_SPRAY
silent-late-pm001-proxy-skip / add-BACKFLIP / add-BLADE_DANCE / add-DAGGER_SPRAY
```

每个动作：

- 把场景 JSON 逐字节复制到 `work/pick-input-probe/<UTC>/<action>/input.json`（冻结种子原样保留）；
- 用 `building_repeat_probe.sanitize_request` 克隆历史 `outer-request.json`，再**覆盖 `seed`/`characterId` 为场景值**（该 helper 硬编码 PM001，不覆盖会把起始种子的 `PICKPROXY01` 悄悄写回 PM001）；
- 命令沿用成功预算探针，但 `--max-turns 1`、`--budget-ms 4000`、`--dop 1`、`--nodes 6000`、`--profile Medium`、`--search-timeout-ms 60000`、`--play-path gameaction`、`--milestone M2`、`--no-card-probe`、`--deploy-plan`。

启动规则：**单进程串行**，最多 8 次，单次进程硬限 **60 秒**，整批 **240 秒**单调时钟；每次启动前在输入准备**之后**重算 `min(60, remaining)`；超期记为 `not_started`，不自动重试。

## 证据来源与判定

实际证据**只**来自 harness 写出的文件，绝不从 input/request 推断：

- `evidence/generated-scenario.loadout.json`：实际牌组，逐卡键为 `id` + `currentUpgradeLevel`；
- `harness-result.json` 的 `resolvedScenario`（PascalCase）；
- 若存在，另核对 `evidence/generated-scenario.resolved.json`（小写键）。

对每个动作校验（缺失/畸形即失败）：

| 字段 | 期望 |
| --- | --- |
| loadout `seed` / `characterId` / `ascension` / `actIndex` / `encounterId` | 与 fixture 场景一致 |
| loadout `playerHp` / `playerMaxHp` | 70 / 70 |
| loadout `relics` | 空 `[]`（一次消融） |
| loadout `potionSlots` | 全为 `null`（允许空槽） |
| loadout `deck` 多重集 | adapter `fixture.deck`；add 动作再**恰好追加一张未升级候选**；skip 不变 |
| `resolvedScenario` | `Seed/CharacterId/Ascension/ActIndex/EncounterId` 全部一致 |
| `evidence resolved`（若在） | `seed/characterId/ascension/actIndex/encounterId` 一致 |

牌组比较按**多重集**（`id` + 升级等级）进行，顺序无关，并记录 `expected/actual` 两侧 SHA256 与差异集合。

### 状态词汇

| 状态 | 含义 |
| --- | --- |
| `verified` | 进程正常且全部证据一致，输入保真通过 |
| `mismatch` | 证据存在但不一致，或证据/结果畸形 |
| `missing` | harness-result 或 loadout 证据缺失 |
| `error` | 非零退出、payload error 或启动失败 |
| `timeout` | 进程超时（保留部分输出） |
| `not_started` | 批截止前未启动 |
| `dry_run` | 默认干跑，未启动 |

`building_eval.classify` 的结果只作为 `outcome` 诊断字段保存；**验收基于输入保真与进程健康**，不是战斗结局。单回合 incomplete 不会被当成完整结果或败局。

## 输出

`work/pick-input-probe/<UTC>/` 为**微秒时间戳新目录**，已存在即拒绝，不覆盖：

```
plan.json        # 8 个动作、固定命令、场景哈希、源哈希
journal.jsonl    # 每次尝试一行，崩溃也保留
summary.json     # 状态计数、verified、tool_completed、outcome 诊断、逐条 checks/差异原因
<action>/input.json
<action>/outer-request.json
<action>/stdout.bin  <action>/stderr.bin
<action>/harness-result.json（由 harness 写）
```

记录内容：命令 argv、源/运行时 DLL 哈希、input/request/raw/loadout/resolved 哈希、耗时、返回码、逐字段 `expected/actual/ok` 与精确不一致原因。**继承环境只传给子进程，从不写入任何产物，也不记录任何凭据。**

## 实际命令

```powershell
# 默认干跑：只写 plan，不启动进程
python scripts/building_pick_input_probe.py
# 受限批量（由所有者审查后手动执行；本任务未执行）
python scripts/building_pick_input_probe.py --run

# 纯离线测试（无真实 harness）
python -m unittest discover -s tests -p test_building_pick_input_probe.py
python -m unittest discover -s tests -p test_building*.py
```

## 本轮实际验证

- `python -m unittest discover -s tests -p test_building_pick_input_probe.py`：**37 项通过**。
- `python -m unittest discover -s tests -p test_building*.py`：**165 项通过**。
- 干跑产物：`work/pick-input-probe/20260917T135504.596844Z/`（`plan.json` / `summary.json` / `journal.jsonl`），`executed=false`、`dry_run=8`、`tool_completed=true`。
  - 源校验：adapter `72abd6a83648171db7b0eb755397f30fa97c41c3e2e62c0c2fe48f98ac490397`，manifest `910c685b56d706913e58094ea72912de4dfca62ae904961437f430a58f2faed9`，历史请求 `2e82369b8ff6c0fb9511b47a931eda512923980f3409001d474a3ef12b8916ab`。
  - 运行时 DLL 五个哈希（probe/CoopBots/sts2/GodotSharp/Harmony）实际与声明一致，全部 `ok=true`。
  - 8 个场景哈希与 manifest 记录逐一相符（含 skip 与 add）。
- 测试全部使用假进程：覆盖全通过、逐上下文变异、错误牌/升级/数量、缺失/畸形证据、非零退出/超时、截止阻止启动、干跑不启动、不覆盖、源哈希不匹配与不同外层种子。
- Astra 补充了缺失解析产物、缺失进程退出码、缺少场景哈希、重复动作四项回归检查，并修正失败处理；最终相关测试 **169 项通过**，见 `work/pick-fixtures/input-probe-acceptance-tests.log`。
- 受限 `python scripts/building_pick_input_probe.py --run` 已执行并通过上述独立复核。选择种子 PICKSEL01/02 与评估种子 PICKEVAL01/02 均未使用。

## 限制（不得省略）

- 两个状态都是 PROXY，不是观察到的奖励画面；act0 起始遭遇是 fixture 代理。
- 遗物/药水为空是设计消融，改变真实牌局语义；这里只作 fixture 保真断言。
- 单回合截断**不能**建立预算行为、重复性或候选排序；截断的战斗不是完整战斗证据。
- 每个动作只跑一次，无法分离运行间噪声；探针不设研究闸门、不给模型结论。
- 批截止为单调墙钟；慢机会把后续动作记为 `not_started`，如实记录不隐藏。
- 证据可信度上限取决于 harness 构建与所消费的验收 fixture 目录。
