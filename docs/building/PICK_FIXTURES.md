# 选牌 fixture 准备（build-pick-fixtures）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `tools/BuildingDecisionProbe` + `scripts/building_pick_cases.py` 这组**离线 fixture 准备工具**的目的、两个 PROXY 状态、实际命令、证据与未完成项。它不是选牌收益结论，也不批准 4s/20s 哪一档更强。

## 状态（2026-09-17，DeepSeek 实现，Astra 独立验收通过）

Astra 独立复跑：`work/pick-fixtures/20260917T134019.577341Z/verify-summary.json`，构建、评分导出、场景生成均成功。相关 128 项测试全部通过（`work/pick-fixtures/acceptance-tests.log`）。独立逐张核对见 `work/pick-fixtures/owner-acceptance.json`：后期 32 张牌含 10 张升级牌与历史输入一致，skip 与 base 字节相同，每个 add 只新增指定一张牌，原有 500 个文件哈希未变。此验收只覆盖准备工具，不覆盖求解器实际消费这些场景或选牌收益。

实际执行成功，三阶段退出码均为 0：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/BuildingDecisionProbe/verify.ps1
# verify summary: work/pick-fixtures/20260917T133950.842071Z/verify-summary.json
# exitCodes: build=0 adapter=0 builder=0   success=true

python -m unittest discover -s tests -p test_building_pick_cases.py
# Ran 31 tests ... OK

python -m unittest discover -s tests -p test_building*.py
# Ran 128 tests ... OK
```

本轮修复了首版的两个阻断项：

1. `Program.cs` 缺少 `using System.Runtime.CompilerServices;`，`InternalsVisibleToAttribute` 编译失败（第一次 build exit 1，日志见上文时间戳更早的 `verify-summary.json`）；补回后编译通过。
2. 首版把 ascension 只写进输出、没有传进运行；现在 `RunState.CreateForTest(..., ascensionLevel: spec.Ascension, ...)` 并用 `verifiedAscension`/`verifiedActIndex`/`verifiedMaxHp`/`verifiedCurrentHp` 回读校验。

## 目标与边界

- 目的：为后续“直接选牌”小试验准备两个**决策状态**：真实当前生产 `BuildValue.Add` / `BestReward(allowSkip:true)` 分数、skip、候选集，以及“同上下文、只差一张候选牌”的场景 JSON。
- 只做离线准备：不启动战斗、不部署到游戏、不写 `outputs/`、不改生产源码。
- 两个状态都显式标注 **PROXY**：它们不是观察到的真实奖励画面。
- 遗物、药水按设计清空（一次消融），并明确记录：去掉遗物会改变真实牌局语义。
- 当前实验已显示“更大预算不必然带来更多终局生命”，因此本工具不预设 4s/dop1 或 20s/dop1 谁更强，只冻结输入与分数。

## 交付物

| 路径 | 作用 |
| --- | --- |
| `tools/BuildingDecisionProbe/BuildingDecisionProbe.csproj` | 独立 .NET9 控制台，`AssemblyName=BuildingDecisionProbe`，引用当前 CoopBots 项目与游戏/RitsuLib/Harmony 程序集 |
| `tools/BuildingDecisionProbe/Program.cs` | headless 宿主、从 ModelDb 构牌、**反射**调用内部 `CoopBots.Building.BuildValue`、导出 JSON |
| `tools/BuildingDecisionProbe/fixtures.json` | 两个 fixture 的输入规格与 provenance（候选固定为 `BACKFLIP`、`BLADE_DANCE`、`DAGGER_SPRAY`） |
| `tools/BuildingDecisionProbe/verify.ps1` | 无必填参数的离线入口：构建 → 导出 adapter JSON → 运行 Python fixture 生成器，记录退出码与 SHA256 |
| `scripts/building_pick_cases.py` | 消费 adapter JSON，校验并写出每个动作的场景 JSON 与 `manifest.json` |
| `tests/test_building_pick_cases.py` | 纯 Python 离线测试，不启动战斗 |

## 两个 PROXY fixture

| | 起始 PROXY | 晚期 PROXY |
| --- | --- | --- |
| id | `silent-starter-proxy` | `silent-late-pm001-proxy` |
| stateKind | `synthetic-starter` | `historical-community-late` |
| seed | `PICKPROXY01` | `PM001` |
| 角色 | SILENT（`player.Character.Id == SILENT`） | 同左 |
| A 等级 / 生命 | 10 / 70–70 | 10 / 70–70 |
| act / 遭遇 | 0 / `FUZZY_WURM_CRAWLER_WEAK` | 2 / `SOUL_NEXUS_ELITE` |
| 牌组来源 | **实际 ModelDb 起始牌组**（工厂填充，回读校验 12 张） | `work/oracle/shiv-pm/b4000-hp1.0-dmg1.0-P0M0-PM001/input.json`，逐卡升级等级原样保留 |
| 遗物 / 药水 | 空（消融） | 空（消融） |
| 进阶之灾 | 恰好一张 | 恰好一张 |
| 实际牌组张数 | 12 + 1 = 13 | 29 + 2 + 1 = 32 |

- 起始牌组：`Player.CreateForNewRun<Silent>` 会调用私有 `PopulateStartingDeck()` 填充角色起始牌组；adapter 回读实际牌组并与 `ModelDb.Character<Silent>().StartingDeck` 逐卡比对，不一致即失败。晚期 fixture 用 `runState.RemoveCard` 同步清空后重建，保证 run 状态一致。
- 遗物：`Player.PopulateStartingRelics()` 会访问 `SaveManager.Instance.MarkRelicAsSeen`，headless 下无存档宿主。adapter 保留真实 SILENT 身份，用一条前缀补丁跳过该次遗物获取（显式消融），输出 `relics=[]`、`potions=[]`、`verifiedCharacterId=SILENT`。**不使用 `Deprived` 替换角色。**
- 已知缺失上下文：没有地图/楼层信息，`RunDepth.ShopAhead` 回退为 true，`RunDepth.Act` 只读 `actIndex`；遗物/药水为空是一次消融；两个状态都不是观察到的奖励画面。

## 真实 BuildValue 的取用方式

adapter 不重写评分，也不冒充 `PatchSmoke` 程序集身份：

1. headless Harmony 与 `ModelDb.Init` 复刻自 `tests/PatchSmoke/TestEnvironment.cs`；
2. 反射取得 `CoopBots.Building.BuildValue` 的 `Add` 与 `BestReward`（`BindingFlags.NonPublic`）；
3. 对每个候选调用 `Add(card, player)`，并调用 `BestReward(player, candidates, allowSkip:true/false)`；
4. 校验分数有限、理由非空；**任一 Add/BestReward 异常直接失败**，不会静默退化为 skip；
5. 比较“含 skip 的 argmax”与 `BestReward(allowSkip:true)`，不一致即失败。

## 实际导出结果（本轮证据）

来源：`work/pick-fixtures/20260917T133950.842071Z/adapter-output.json`（sha256 `e73d5873347c949cd92b10fbc31dec4dd7ca567a0a1f7bb4ffd57ff60b14ae06`；manifest sha256 `8e38e07365817552b288b96e01a2b0ddd9fc8cc8c8182ceb05df666623b0bddc`）。

运行身份（两个 fixture 一致）：`verifiedCharacterId=SILENT`、`verifiedAscension=10`、`characterStartingHp=70`、`verifiedMaxHp=70`、`verifiedCurrentHp=70`；起始 act=0、晚期 act=2。晚期 `sourceInputValidation={characterCards:29, colorlessCards:2, matchesSpec:true, matchesActualDeck:true}`（源文件 sha256 `7c0dfb7ba2a8d2092e47e5d87a4e56171e5816dab4cc9c0bb577d71ab2c8d133`）。

| fixture | 候选 | score（当前生产 BuildValue.Add） | reason（节选） | argmax（含 skip） | BestReward(allowSkip) |
| --- | --- | --- | --- | --- | --- |
| starter | BACKFLIP | 29.5 | `block,engine,needs-draw` | 0 | 0 |
| starter | BLADE_DANCE | 6.0 | `no role` | | |
| starter | DAGGER_SPRAY | 26.72 | `aoe,needs-aoe` | | |
| starter | skip | 0 | baseline | | |
| late | BACKFLIP | -36.4 | `block,engine,off-route:Prepared + Ricochet,size:45.0` | 2 | 2 |
| late | BLADE_DANCE | -45.0 | `off-route:Prepared + Ricochet,size:45.0` | | |
| late | DAGGER_SPRAY | 17.78 | `aoe,fits-route:Prepared + Ricochet,size:12.0` | | |
| late | skip | 0 | baseline | | |

这些是“该状态下的生产评分”，不是强度结论，也不用于比较 4s/20s。

关键 SHA256 与运行路径全部记录在 `verify-summary.json`：

- `adapterDll` `a6e4d31ac33a97cf044cba012dfd83db4aebea348271c982263ba7d65b48ec7a`
- `coopBotsDll` `e7f40ba85105f64d65160f3b3f4d6f2359c5e72b096458af44935e9018547f17`
- `sts2` `0861bfa1df347538d932f22d580e75420f08082792eb914e53b4882764acdbe9`
- `godotSharp` `0e4897ecdfb31456a97c7d8028dfb8d7dbdc632e2f73fc9b438d7b266a139289`
- `harmony` `ef1898322c9f5c86dc1b0758b272a9c440823b4a41ca9a0b82a3aa6b3d206387`
- `fixturesSpec` `be6ceedba02b12f9497f124fad94d04b7d3157c2c235c5e8ef9dc104bac8259f`
- 游戏数据目录 `B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64`

## 场景 JSON 生成规则

`scripts/building_pick_cases.py --input <adapter-output> --output <new-dir>` 写出：

```
cases/<fixture-id>/base.json
cases/<fixture-id>/skip.json          # 与 base 逐字节相同
cases/<fixture-id>/add-BACKFLIP.json
cases/<fixture-id>/add-BLADE_DANCE.json
cases/<fixture-id>/add-DAGGER_SPRAY.json
cases/manifest.json
```

规则与校验：

- 结构对齐 CombatSolver 的 `GeneratedCombatScenarioOptions`（camelCase、未列出字段用默认值）：`characterCards`/`colorlessCards` 使用 `count`+`ids`+`upgradeLevelsPerCard`，`relics`/`potions` 为 `{"count":0}`。
- **HP 不省略**：场景写入 `playerCurrentHp: 70`。harness 的最大生命来自角色（SILENT = 70），并在提供 `playerCurrentHp` 时设为当前生命，因此 70/70 被保留；`manifest.scenarioHp` 记录该语义。
- `fixedSearchBudget=false`、`mode="Deploy"`：历史 fixture 沿用其 `input.json`，起始 fixture 为对齐固定。4s/20s 属于请求级预算字段，本批不改、待 Astra 复核后决定。
- skip 与 base 逐字节相同；
- 每个 add 只在对应卡池块**追加一张未升级卡**，不删除任何牌；断言 `DEFEND_SILENT` 数量不变；
- 所有动作共享同一 seed / 角色 / A 等级 / 幕 / 遭遇 / `playerCurrentHp` / include 标志与逐卡升级数组；
- **实际牌组必须等于** `resolvedStartingDeck`（起始展开）+ `characterCards` + `colorlessCards` + 恰好一张 `ASCENDERS_BANE`；不一致即拒绝（含起始牌组展开漂移）。
- 拒绝：重复候选、未知 `canonicalId`、非 character 卡池、NaN/Inf 分数、数组长度不一致、负数/非整数升级、skip 值非 0、已把 `ASCENDERS_BANE` 写进 `characterCards`、**非法 ID（`^[A-Z0-9_]+$`，阻挡路径穿越）**、fixture id 不安全（`^[A-Za-z0-9_-]+$`）、运行期 A 等级/幕/生命与声明不一致、历史源输入与 spec/实际牌组不一致、argmax 与 `BestReward` 不一致的 adapter JSON；
- 输出目录必须不存在，否则失败（不覆盖）。

`manifest.json` 保留每个 fixture 的上下文、生产分数、逐动作 SHA256、语义检查，以及**预先冻结但不执行**的种子集合：

- 选择：`PICKSEL01`、`PICKSEL02`
- 评估：`PICKEVAL01`、`PICKEVAL02`
- 两组不相交；2 个种子不能证明估计量无偏。

## 实际命令

```powershell
# 离线构建 + 生成 adapter JSON + 生成 cases（无必填参数）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/BuildingDecisionProbe/verify.ps1

# 纯 Python 离线测试
python -m unittest discover -s tests -p test_building_pick_cases.py
```

`verify.ps1` 行为：由 `$PSScriptRoot` 定位仓库，使用 `work/dotnet-win/dotnet.exe` 与游戏目录
`B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64`，构建到新的
`work/pick-fixtures/<UTC>/build`，把 adapter JSON 写到同目录，并让 Python 生成器写到
`work/pick-fixtures/<UTC>/cases`。日志、退出码与关键 SHA256 记录在
`work/pick-fixtures/<UTC>/verify-summary.json`；任一阶段非零即停止。

## 限制（不得省略）

- 两个状态都是 PROXY，不是观察到的奖励画面；不能据此宣称覆盖真实早期选牌。
- 遗物/药水为空是消融，去掉它们改变真实牌局语义。
- 本工具不运行任何战斗、不做预算对比，也不批准 4s/20s 或任何模型。
- `act0` 起始遭遇是 fixture 代理；在昂贵采样前必须先有实际解析场景（`resolvedScenario` seed/character/encounter）校验。
- 场景 JSON 的结构对齐 CombatSolver 的 `GeneratedCombatScenarioOptions` 是静态审查结论；本任务未在本仓库启动 CombatSolver harness 实际消费这些 JSON。
- 只执行了离线构建、生产评分导出与 fixture 生成；**未启动游戏、未启动战斗、未部署**。
