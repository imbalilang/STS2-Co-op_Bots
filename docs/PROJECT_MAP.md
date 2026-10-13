# CoopBots 项目资产地图

本文只依据工作区中**实际存在的文件**与其中的证据编写，不复制历史结论；历史结论请点击链接查看原文。核对日期：2026-09-17；**2026-09-20 目录整理后已同步**（见 §9.7）。

## 0. 首要规则（先读）

1. **保留未提交改动。** 本仓库是脏共享检出，包含接手前就存在的大量未提交修改与未跟踪文件。不要 `reset` / `checkout` / `clean` / `stash`，不要自动提交、合并、发布或部署。
2. **参考副本一律放在 `third_party/` 下，移动前先改引用。** 2026-09-20 起 `CombatSolver-main/`、`CombatSolver-upstream-main/`、`StS2.RandomForeseer-0.13.14/` 与两个第三方参考包都在 `third_party/`。`scripts/capture-research-baseline.py` 与 `scripts/building_repeat_probe.py` 曾以硬编码相对路径引用它们，本次已同步更新；**下次再移动必须先 grep 全仓并改完引用**。
3. **不读密钥与凭据。** 根目录 `DEEPSEEK_API_KEY` 被 `.gitignore` 忽略；环境变量全集都不要读取或打印。
4. **不要构建或测量废弃副本。** 历史 PR 克隆 `work/CombatSolver-pr104/`（pr-104 `d98f089`）已于 2026-09-20 随 `work/` 整理删除；PR104 早已并入 0.40，不要再为它保留路径。

## 1. 仓库顶层

| 路径 | 角色 |
| --- | --- |
| `src/CoopBots/` | MOD 本体源码（编译为 `CoopBots.dll`） |
| `src/CoopBots.Kernel/` | 生产内核项目（编译为 `CoopBots.Kernel.dll`），`Vendor/` 为上游导入源码 |
| `tests/PatchSmoke/` | 主回归测试线，含 `DraftSim/`、`DeckSim/` 调参工具 |
| `tests/TypeLoadCheck/` | 独立进程的类型加载保护检查 |
| `scripts/` | 构建、导入、抓取、分析、oracle 实验与验证脚本 |
| `third_party/` | **所有第三方参考副本**（0.39 游戏内 oracle、0.40 离线宿主、Random Foreseer、参考包）。Git 忽略，不随包分发 |
| `outputs/` | 历史文档、日志、发布包与发布目录（Git 忽略） |
| `work/` | 本地 SDK、构建输出、实验数据、实机日志（Git 忽略） |
| `PLAN.md` | **全局规划**：阶段、验收判据、编号 backlog（见 §10） |
| `.claude/skills/` | 可调用 Skills（S1–S8 的执行入口，见 `AGENT.md` §5） |
| `README.md` / `CHANGELOG.md` | 项目说明与版本记录 |
| `SOURCE_PACKAGE.md` / `THIRD_PARTY_NOTICES.md` / `LICENSE` | 源码包说明、第三方署名、许可 |
| `AGENT.md` | **唯一权威规范**（冲突时以它为准） |
| `TAKEOVER_REVIEW.md` / `TEAM.md` / `AGENTS.md` / `SOUL.md` / `CLAUDE.md` | 历史文件，仅供查历史，不要依据它们行动 |
| `docs/` | 本文档目录（新文档入口见 [`README.md`](README.md)） |

## 2. 编译产物（MOD 本体）

| 项目 | 路径 | 产物 | 关键事实 |
| --- | --- | --- | --- |
| MOD 主项目 | `src/CoopBots/CoopBots.csproj` | `CoopBots.dll` | `net9.0`；`InternalsVisibleTo=PatchSmoke`；编译期引用 `sts2.dll` / `GodotSharp.dll` / `0Harmony.dll`，或改用 `STS2ReferenceVersion` 引用包 |
| 生产内核 | `src/CoopBots.Kernel/CoopBots.Kernel.csproj` | `CoopBots.Kernel.dll` | `Vendor/**/*.cs` 一并编译；`Vendor/Runtime/CombatReplayOutcome.cs` 被显式排除（`CoopBots.Kernel.csproj:21`） |

- MOD 清单：`src/CoopBots/mod_manifest.json`，当前 `version = 0.36.1`，依赖 `STS2-RitsuLib >= 0.6.2`，`min_game_version = 0.111.0`。
- 内核通过 `ProjectReference`（`src/CoopBots/CoopBots.csproj:16`）被 MOD 引用；运行时 `ModEntry` 用 `KernelAssemblyLoader` 加载同目录的 `CoopBots.Kernel.dll`（`src/CoopBots.Kernel/INTEGRATION.md:30`）。
- 中间输出 `**/bin/`、`**/obj/` 与 `work/build-<版本>/` 均被忽略（`.gitignore:2,4-5`）。`scripts/build.ps1:21-23` 把构建输出写到 `work/build-<版本>`，再打包到 `outputs/CoopBots-v<版本>.zip`。

## 3. 生产内核 Vendor 与 CombatSolver 参考副本

这是最容易混淆的一组路径，务必分清"生产内核"与"参考副本"。

### 3.1 生产内核（随包发布）

- 路径：`src/CoopBots.Kernel/Vendor/`，命名空间 `CoopBots.Kernel.Vendor`。
- 来源记录：`src/CoopBots.Kernel/UPSTREAM.json:2` 写明 `"source": "CombatSolver 0.33.9"`，并逐文件记录 `upstream_sha256`。
- 规模与范围：`src/CoopBots.Kernel/INTEGRATION.md:5` 记载 267 份上游源码文件加 1 个提取的进度 DTO，保留 Engine / Prediction / Search 与必要运行时支持；不导入原 CombatSolver 的启动、界面、自动部署或联网功能。
- **这是唯一进入 `CoopBots.Kernel.dll` 的 CombatSolver 代码**。它不从磁盘上的任何 CombatSolver 参考副本编译，参考副本被移动也不会改变已导入的 Vendor 源码。

### 3.2 参考副本 A：游戏内 oracle 用

| 项 | 值 |
| --- | --- |
| 路径 | `third_party/CombatSolver-main/`（2026-09-20 前在仓库根） |
| 版本声明 | `third_party/CombatSolver-main/CombatSolver.json:6` = `0.39.0`（但其 `README.md:7` 正文仍写 `0.36.4`，内部不一致） |
| 类型 | 普通文件副本，**不是 Git 仓库**（`scripts/combatsolver-oracle-adaptations.json:11`） |
| 角色 | 曾作**游戏内 oracle** 副本，2026-09-16 打过适配补丁；该实验线已于 2026-09-20 判死（见 `PLAN.md`），副本仅作来源核对保留 |
| 启动方式 | 经 `tools/run-unattended-test.ps1` 启动隔离游戏进程；Python 侧入口 `work/oracle/run-oracle.ps1`（**`work/oracle/` 已于 2026-09-20 删除**，该入口不再存在） |
| 谁在引用 | `scripts/oracle-dual-test.py:74`、`scripts/oracle-factorial.py:118`、`scripts/oracle-factorial-v2.py:152`、`scripts/oracle-small-batch.py:41,183`（均属已废弃的 oracle 线） |

### 3.3 参考副本 B：离线搜索宿主用

| 项 | 值 |
| --- | --- |
| 路径 | `third_party/CombatSolver-upstream-main/`（2026-09-20 前在仓库根） |
| 版本声明 | `third_party/CombatSolver-upstream-main/CombatSolver.json:6` = `0.40.0`；HEAD 记录为 `1c31b9f (v0.40.0-2-g1c31b9f)`（`combatsolver-oracle-adaptations.json:98`） |
| 类型 | 含 `.git/`，是 Git 仓库 |
| 角色 | 离线搜索宿主 `tools/OfflineSearchHarness/`（`combatsolver-oracle-adaptations.json:96-101`）。**生产内核的导入源不是它**，是 `work/combatsolver-sync-latest/`（0.41.0），见 §3.6 |
| 离线宿主产物 | `third_party/CombatSolver-upstream-main/tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll`（构建产物，Git 忽略） |
| Git 状态 | 被 `.gitignore` 的 `third_party/` 忽略 |
| 谁在引用 | `scripts/capture-research-baseline.py`、`scripts/building_repeat_probe.py`、`scripts/perf-dop.py`、`scripts/perf-parallel.py`、`scripts/rerun-old-ruler.py`、`scripts/shiv-pm-interaction.py`、`scripts/silent-base-screen.py`、`scripts/soulnexus-sigma.py`（全部已在 2026-09-20 移动时同步更新路径） |

### 3.4 已删除的副本

- `work/CombatSolver-pr104/`、`work/cs-0.39.0-ref/`、`work/vendor-41-try/`、`work/vendor-41-nopatch/`：历史 PR 克隆与 0.41 导入尝试，PR104 已并入 0.40，导入尝试已被 `work/combatsolver-sync-latest/` 取代。**2026-09-20 随 `work/` 整理删除**（记录见 `work/CLEANUP-2026-09-20.md`）。

### 3.5 上游对应关系速查

| 用途 | 实际副本 | 版本 | 入口 |
| --- | --- | --- | --- |
| 生产内核（编译进 MOD） | `src/CoopBots.Kernel/Vendor/` | CombatSolver 0.33.9 + 0.41 部分回迁（`UPSTREAM.json` / `POWER_SYNC.json`） | 随 `CoopBots.Kernel.dll` 发布 |
| 内核导入源（权威） | `work/combatsolver-sync-latest/` | 0.41.0（commit `0e6cc2d…`） | `src/CoopBots.Kernel/POWER_SYNC.json` 的 `upstream_source.path` 指它 |
| 游戏内 oracle（启动游戏） | `third_party/CombatSolver-main/` | 0.39.0 | `tools/run-unattended-test.ps1`（实验线已停） |
| 离线 oracle（纯 .NET 进程） | `third_party/CombatSolver-upstream-main/` | 0.40.0 | `tools/OfflineSearchHarness`（实验线已停） |

### 3.6 内核导入源不得删除

`src/CoopBots.Kernel/POWER_SYNC.json` 以 `work/combatsolver-sync-latest` 记录了 0.41.0 回迁的来源路径与 commit。**`work/` 清理时该目录是唯一必须保留的大目录**；删除它会让 `POWER_SYNC.json` 的 `upstream_source` 失去可核对对象。

## 4. 生成的资源表（勿手改）

| 生成文件 | 生成脚本 | 内容 |
| --- | --- | --- |
| `src/CoopBots/Building/BakedArchetypes.cs` | `scripts/import-spire-codex-archetypes.py` | 真实跑局挖掘的流派簇 |
| `src/CoopBots/Building/BakedCardAffinity.cs` | `scripts/import-spire-codex-affinity.py` | 从真实选牌挖掘的卡-卡抓取亲和 |
| `src/CoopBots/Building/BakedEvents.cs` | `scripts/import-spire-codex-events.py` | 事件选项 id |
| `src/CoopBots/Building/BakedResources.cs` | `scripts/import-spire-codex-resources.py` | 资源生产/乘数/重放/消耗表（文件头注释即注明 GENERATED） |

- 数据抓取脚本：`scripts/fetch-spire-codex-cards.py`、`fetch-spire-codex-decks.py`、`fetch-spire-codex-draft-recs.py`、`fetch-spire-codex-run-details.py`。缓存默认在 `work/spire-codex/`（Git 忽略）。
- 来源与许可：`THIRD_PARTY_NOTICES.md:5-50`（Spire Codex，PolyForm Noncommercial 1.0.0）。只烘焙标识符与结构，不烘焙胜率、不复制游戏资产。
- 一致性由测试守护：`tests/PatchSmoke/CodexCrossCheckScenarios.cs` 在校验不过时会让生成的表失败，提示重新生成（`tests/PatchSmoke/Program.cs:129-132`）。

## 5. 测试入口与特性开关

| 入口 | 路径 | 作用 |
| --- | --- | --- |
| PatchSmoke | `tests/PatchSmoke/PatchSmoke.csproj` + `Program.cs` | 控制台回归：Harmony 补丁挂载、Bot ID 编解码、Genius 战术探针、各 `*Scenarios.cs` |
| TypeLoadCheck | `tests/TypeLoadCheck/TypeLoadCheck.csproj` + `Program.cs` | 独立进程验证：`CoopBots.Kernel` 不可解析时 `CoopBots.dll` 仍能枚举所有类型 |
| DraftSim | `tests/PatchSmoke/DraftSim/` | 构筑流水线：`DeckScorer.cs`、`DraftRun.cs`、`DraftSimHarness.cs`、`SimConfig.cs`、`CommunityDecks.cs` |
| DeckSim | `tests/PatchSmoke/DeckSim/` | 数据集与统计工具：`Csv.cs`、`IncrementalAuc.cs` |

**注意**：DraftSim/DeckSim 是本地调参与构筑校验工具，**不属于 MOD 包体**，构建脚本不会把它打进发布包（`SOURCE_PACKAGE.md:9`）；它也不是发布门禁（`Program.cs:159-160`）。

### 特性开关

| 开关 | 类型 | 效果 | 证据 |
| --- | --- | --- | --- |
| `EnableKernelTests` | MSBuild 属性（默认 `false`） | 定义 `KERNEL_TESTS`；未开启时从编译中移除 `KernelEngineScenarios.cs`、`KernelRoundScenarios.cs` | `tests/PatchSmoke/PatchSmoke.csproj:9-10,30-31`；`CooperativeScenarios.cs:307` 用 `#if KERNEL_TESTS` |
| `CoopBotsDll` | MSBuild 属性 | 指定已构建的 `CoopBots.dll` 做引用，而不是 `ProjectReference`；`scripts/build.ps1` 打包时使用 | `PatchSmoke.csproj:8,13-18` |
| `STS2DataDir` | MSBuild 属性 | 游戏程序集目录（必须含 `sts2.dll`） | `PatchSmoke.csproj:7`；`KernelDependencies.props` |
| `RitsuLibDir` / `RitsuLibSharedDir` | MSBuild 属性 | RitsuLib 0.6.x 的 `compat/<版本>` 与 `shared` 目录，默认由 `STS2DataDir` 推导 | `KernelDependencies.props:9-12` |
| `--deck-sim` / `--deck-sim-only` | 运行时参数 | 运行构筑扫描；`--deck-sim-only` 跳过普通回归断言 | `tests/PatchSmoke/DraftSimScenarios.cs:5-17,25` |
| `--deck-sim-validate` | 运行时参数 | 用社区 A10 牌组校验评分 | `tests/PatchSmoke/CommunityValidationScenarios.cs:11-15,35` |
| `--runs` / `--chars` / `--seed` / `--ascension` / `--players` / `--quick` / `--baseline` / `--out` | 运行时参数 | DraftSim 扫描控制 | `DraftSimScenarios.cs:53-69` |
| `--features` / `--incremental` / `--calibrate` / `--par` / `--export` | 运行时参数 | 社区校验分析控制 | `CommunityValidationScenarios.cs:74-90` |

## 6. 离线测试工具与 oracle 脚本

`scripts/` 下的 Python 脚本按用途分为几类：

- **数据抓取**：`fetch-spire-codex-*.py`，缓存写入 `work/spire-codex/`。
- **表生成**：`import-spire-codex-*.py`，写出第 4 节的 `Baked*.cs`。
- **游戏内 oracle**：`oracle-*.py`，经 `work/oracle/run-oracle.ps1` → `third_party/CombatSolver-main/tools/run-unattended-test.ps1` 启动隔离游戏进程；结果落在 `work/oracle/<实验名>/`，报告写到 `outputs/`。**该线已判死**，`work/oracle/` 已删除，脚本仅作历史保留。
- **离线 oracle**：使用 `third_party/CombatSolver-upstream-main/tools/OfflineSearchHarness`（纯 .NET 进程，不启动 Godot），如 `soulnexus-sigma.py`、`shiv-pm-interaction.py`、`silent-base-screen.py`、`soulnexus-calibration.py`。**同属已判死的离线评价线**。
- **性能实验**：`perf-dop.py`、`perf-parallel.py`、`oracle-fastmode.py`。
- **对比/复算**：`compare-feature-tables.py`、`compare-old-new-ruler.py`、`rerun-old-ruler.py`。
- **分析与报告**：`analyze-*.py`、`oracle-report.py`。
- **验证骨架**：`verify-oracle.py`（其中的 oracle 尚未存在，属于预注册检查清单，见文件头注释）。
- **适配记录**：`combatsolver-oracle-adaptations.json`（对参考副本的补丁与锚点，不放在被忽略目录，是可复核的来源记录）。
- **内核导入器**：`scripts/import-combatsolver-kernel.py`。

运行环境补充：

- 本机 PowerShell 7 未系统安装，`work/pwsh/7/` 提供便携版，由脚本调用前加入 PATH。
- 本地 .NET 9 SDK 位于 `work/dotnet-win/dotnet.exe`（Git 忽略）。**构建必须用它**，PATH 上的 dotnet 没有 SDK。
- 控制台编码：证据里出现 `????` 说明调用前没设 UTF-8。

## 7. 被忽略的工作区数据（Git 不交付）

`.gitignore` 忽略：

- `work/`（本地 SDK、构建输出、实验数据、实机日志）与 `outputs/`（历史文档、日志、发布物）。
- `third_party/`（**2026-09-20 起所有第三方参考副本**；旧的分目录条目仍保留以兼容老检出）。
- `**/bin/`、`**/obj/`、`**/releases/`、`*.dll`、`*.pdb`、`*.exe`、`*.zip`、`*.rar`、`*.dylib`、`*.so`。
- `src/CoopBots.Kernel/.nuget/`、`__pycache__/`、`*.pyc`、`local.props`、`/DEEPSEEK_API_KEY`。

`work/` 里**必须保留**的目录（清理时不要删）：`dotnet-win/`、`pwsh/`、`gh-cli/`、`steamcmd/`、`.tools/`（ilspy）、`combatsolver-sync-latest/`（§3.6）、`spire-codex/`（社区数据，§4）、`agent-state/`（实机日志证据）、`verification/`（门禁运行记录）、`build-0.36.4/`（当前版本的 `build.ps1` 输出目录）。

因此通过普通 `git clone` 得到的仓库**不包含**实验数据、交接、离线宿主和本地 SDK；它们是本工作区独有资产。参考 `TAKEOVER_REVIEW.md:55`。

## 8. 发布产物 vs 当前工作树

- **当前版本**：`src/CoopBots/mod_manifest.json` = `0.36.4`。`outputs/` 里 `CoopBots-v<版本>.zip` 是**已发布工件，不得覆盖**；带 `-<label>` 后缀的是测试包。
- `scripts/build.ps1` **必须带 `-Label`**，否则会写到已发布工件的路径上。它打印的 SHA256 只哈希 `CoopBots.dll`，**内核变更时它不变** —— 识别包请用 zip 自身的 sha256。
- **工坊上传**：见 `docs/RELEASING.md`；发布前 README 与工坊描述都要更新（`AGENTS.md` 的常设要求）。
- **历史归档**：`outputs/` 下的旧版本目录与测试包（Git 忽略）。
- **重要区分**：`README` / `CHANGELOG` 里的"已同步 GitHub / 工坊"是**历史记录**；当前工作树包含大量未提交改动，**不能等同于已发布版本**。

## 9. 陈旧描述与证据纠正

以下描述在仓库文档中已经落后于工作区，以实际文件为准：

1. **参考副本名称**。`SOURCE_PACKAGE.md:11,29` 仍写 `CombatSolver-0.33.9/`，`.gitignore:22` 也仍忽略 `CombatSolver-0.33.9/`；但该目录**已不在磁盘上**。实际存在的是 `CombatSolver-main/`（0.39.0，游戏内 oracle）与 `CombatSolver-upstream-main/`（0.40.0，离线宿主）。`TAKEOVER_REVIEW.md:55` 明确说"源码包说明中的参考副本名称也已落后于工作区"。
2. **内核重导入路径**。`scripts/import-combatsolver-kernel.py:5` 的源码指向 `CombatSolver-0.33.9/src`。该目录不存在，直接重跑会失败；若需重导入须先修正路径并核对 `UPSTREAM.json` 的哈希。
3. **`docs/releases/` 不存在**。`SOURCE_PACKAGE.md:16` 说"作为开发文档的 `docs/releases/` 会保留"，但当前工作区没有 `docs/` 下的发布目录（`docs/` 是本次任务新建的文档目录）。
4. **`CombatSolver-main` 版本自相矛盾**。其 `CombatSolver.json` 为 `0.39.0`，`README.md:7` 正文却写 `0.36.4`。以 JSON 为准，正文待更新。
5. **离线宿主版本**。`TAKEOVER_REVIEW.md:15` 称"离线研究宿主是另一份 0.40 副本"，与 `CombatSolver-upstream-main/CombatSolver.json` 的 `0.40.0` 一致；`scripts/combatsolver-oracle-adaptations.json:98` 进一步记录 HEAD `1c31b9f`。
6. **历史实验文档**。`outputs/DEVELOPMENT-AND-TESTS.md`、`outputs/*.md` 等属于早期阶段记录，不能当成本轮结果；新文档应以工作区实际文件和可重跑命令为准。
7. **2026-09-20 目录整理**（本次）：三处需要按新事实读——
   - **参考副本已移位**：`CombatSolver-main/`、`CombatSolver-upstream-main/`、`StS2.RandomForeseer-0.13.14/` 与两个第三方参考包现在都在 `third_party/`。原文中写在仓库根的路径一律加 `third_party/` 前缀。
   - **`work/` 已大幅清理**（8.0GB → 2.2GB）：离线卡组评价线（`oracle/`、`shiv-*`、`building-*`、`pick-*`、`precision-200` 等 ≈3.35GB）、旧构建目录、废弃协作设施（`superharness*`、`agent-tools`、`coord`、opencode 数据库）均已删除。逐条记录在 `work/CLEANUP-2026-09-20.md`。
   - **因此本文 §6 里指向 `work/oracle/`、`work/measurement-pilot/`、`work/superseded/`、`work/workshop/`、`work/CombatSolver-pr104/` 的路径描述都已失效**，那些目录不再存在。
   - `scripts/import-combatsolver-kernel.py:5` 指向的 `CombatSolver-0.33.9/src` 仍不存在；**当前内核导入源是 `work/combatsolver-sync-latest/`（0.41.0）**，脚本是 `scripts/import-combatsolver-kernel-41.py`。
8. **2026-09-20 通过数收敛**（含一次自我更正）：仓库里同时存在过几个不同的通过数 ——
   `AGENT.md` §7（166/94）、`tests/README.md`（转指 `TAKEOVER_REVIEW.md`）、
   `TAKEOVER_REVIEW.md`（97/173，09-17）、`VENDOR_DEVIATIONS.md`（PASS 50，09-19）、
   `work/verification/`（162/242，09-18）。**实跑复核后确认：这些大多不是"矛盾"，而是不同日期、
   不同命令的读数。** 真正的问题是两个：

   1. **权威链断了一环**：`AGENT.md` 说"与 `tests/README.md` 冲突时以它为准"，
      而 `tests/README.md` 把权威让给了一份已作废的历史文档。**已断掉该引用。**
   2. **两个不同的命令共用"门 4"这个名字**：`-- --kernel-power-only` 的**子集 = 94**，
      而 `build.ps1` / `verify-project.ps1` 跑的**完整套件 = 265**。
      门槛曾写成 94 而读数是 265 → **比较恒真，等于没有门**（R5b）。**门槛已改为 265。**

   现在的规矩：**判据（门槛）只看 `AGENT.md` §3 的表，读数（证据）只看 `work/verification/`
   最近一次运行**（`python scripts/state.py show`），且**记录通过数必须同时记录是哪条命令跑出来的**。
9. **2026-09-20 首次必读压缩**：`AGENT.md` 新增 **§0「首次接手」**（约 60 行）作为唯一入口，
   明确"读完这一节就能开工，不要顺序通读全文"；§7 的未完成项迁到 `PLAN.md` 的 B1–B11，
   §7 只留叙述性背景。历史文件（`CLAUDE.md`/`TEAM.md`/`AGENTS.md`/`SOUL.md`、
   `TAKEOVER_REVIEW.md`、`docs/building/**`）全部加上作废横幅。

## 10. 相关链接（不复制结论）

- **权威规范**：[`../AGENT.md`](../AGENT.md) —— 与任何其它文件冲突时以它为准。
- 全局规划：[`../PLAN.md`](../PLAN.md)
- 项目说明与构建：[`../README.md`](../README.md)
- 版本记录：[`../CHANGELOG.md`](../CHANGELOG.md)
- 发版流程：[`RELEASING.md`](RELEASING.md)
- 内核接入记录：[`../src/CoopBots.Kernel/INTEGRATION.md`](../src/CoopBots.Kernel/INTEGRATION.md)
- 第三方来源与许可：[`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)
- 测试命令：[`README.md`](README.md)、[`../tests/README.md`](../tests/README.md)
- 历史文件（不要依据它们行动）：[`../TAKEOVER_REVIEW.md`](../TAKEOVER_REVIEW.md)、`../TEAM.md`、`../AGENTS.md`、`../SOUL.md`、`../CLAUDE.md`


## 测量校准工具增补（2026-09-17）

- `scripts/building_eval.py`：只读审计 PM 原始产物，结局分类、输入哈希、逐种子配对。
- `scripts/building_repeat_probe.py`：默认干跑，显式 `--run` 执行固定四场、相同输入的预算探针。
- `tests/test_building_eval.py`、`tests/test_building_repeat_probe.py`：离线单元测试。
- 当前批准执行顺序见 [直接选牌规格](building/DIRECT_PICK_PILOT.md)，实际证据见 [测量进展](building/MEASUREMENT_STATUS.md)。旧析因步骤已降为可选诊断。

- `scripts/building_budget_probe.py` 与 `tests/test_building_budget_probe.py`：固定节点上限下软预算4s/20s、内部线程1/2的顺序对照；规格见 [预算校准](building/BUDGET_CALIBRATION.md)。
