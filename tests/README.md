# CoopBots 测试入口

本目录包含两条自动化测试线与一条本地调参线：

- `PatchSmoke/`：主回归控制台程序。
- `TypeLoadCheck/`：独立进程的类型加载保护检查。
- `PatchSmoke/DraftSim/`、`PatchSmoke/DeckSim/`：构筑调参与校验工具，**不属于 MOD 包体**，也不是发布门禁。

以下命令均在仓库根目录执行，使用工作区自带的 `work/dotnet-win/dotnet.exe`（Git 忽略）和**显式指定的游戏程序集目录**。

日常完整验证优先运行 `scripts/verify-project.ps1`：每次新建 `work/verification/<运行标识>/`，保存构建、类型加载、普通回归与内核回归日志及 `summary.json`，不打包或覆盖发行物。以下单项命令用于定位问题。

## 0. 前提

- 游戏目录必须包含 `sts2.dll`，默认示例为
  `B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64`（`scripts/build.ps1:2,17`）。
- RitsuLib 0.6.x 默认由游戏目录推导：`$(STS2DataDir)/../../../workshop/content/2868840/3747602295/compat/0.111.0`，同级 `shared/` 存放 Shared/Ui/Settings。安装位置不同时用 `-p:RitsuLibDir=...` 覆盖（`src/CoopBots.Kernel/KernelDependencies.props:9-12`）。
- 普通测试不需要读取根目录 `DEEPSEEK_API_KEY`，也不要读取或打印该文件与任何凭据。

下面命令里的路径先统一设好：

```powershell
$repo   = (Resolve-Path .).Path
$dotnet = Join-Path $repo 'work\dotnet-win\dotnet.exe'
$game   = 'B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64'
if (!(Test-Path -LiteralPath (Join-Path $game 'sts2.dll'))) { throw 'STS2DataDir 必须包含 sts2.dll' }
```

## 1. 普通 PatchSmoke（默认回归）

不启用内核套件，编译并运行 Harmony 补丁挂载、Bot ID 编解码、Genius 战术探针以及各普通场景。

```powershell
& $dotnet run --project "$repo\tests\PatchSmoke\PatchSmoke.csproj" -c Release `
  "-p:STS2DataDir=$game" -p:NuGetAudit=false
```

- 退出码 `0` 为通过；`Program.cs:164-168` 在异常时设置 `Environment.ExitCode = 1`。
- 该程序会将 `CoopBots.dll` 的构建输出放到项目输出目录，并 ProjectReference `src/CoopBots` 与 `src/CoopBots.Kernel`（`tests/PatchSmoke/PatchSmoke.csproj:13-22`）。

## 2. EnableKernelTests（含内核场景）

在普通回归之上定义 `KERNEL_TESTS`、编入 `KernelEngineScenarios.cs` 与 `KernelRoundScenarios.cs`（`tests/PatchSmoke/PatchSmoke.csproj:9-10,30-31`）。

```powershell
& $dotnet run --project "$repo\tests\PatchSmoke\PatchSmoke.csproj" -c Release `
  "-p:STS2DataDir=$game" -p:EnableKernelTests=true -p:NuGetAudit=false
```

**通过数不在本文档里。** 判据（门槛）见 [`../AGENT.md`](../AGENT.md) §3 的表；
读数（证据）只认 [`../work/verification/`](../work/verification) 里最近一次运行的
`summary.json` —— 用 `python scripts/state.py gate` 记录、`python scripts/state.py show` 查看。

> ⚠ **不要引用任何文档里写死的 PASS 数**，包括本文档的历史版本、`TAKEOVER_REVIEW.md`
> 和 `VENDOR_DEVIATIONS.md` 里的数字 —— 它们各自是写作当天的读数，
> 本仓库的通过数已经漂过至少四轮（详见 [`../AGENT.md`](../AGENT.md) §3.1）。

### 续接不变量（`RunContinuationInvariants`）

「搜一次、按剧本打完」这条链路有四个不变量，每一个都曾经只能靠**跑一次实机**才发现，所以每一个都在这里钉住（`KernelEngineScenarios.cs` 的 `RunContinuationInvariants`）：

1. **状态文本渲染的是分支自己的回合**，不是实机当前回合。实机值一旦混进去，跨回合比对必然报 `field=turn expected={1} actual={2}`。
2. **`KernelSession.TurnNumber` 是分支自己的回合算术**（`rootTurnNumber + RoundsAdvanced`），跨一次回合就 +1。
3. **`Reset()` 只清搜索，碰不到已提交的剧本**。它曾经是静默杀剧本的元凶：`BotRuntime` 里三处「这 tick 不行动」的早退路径都会顺手删掉剧本，而日志里只剩 `noScript` 在涨、`planRefusals` 是 0。
4. **丢剧本只能走 `DropContinuation(reason)`** —— 具名、计数、且日志不可节流。静默丢掉的剧本和「剧本跑完了」在日志里长得一模一样。

这四个断言都用「故意改坏再跑」验证过会变红（2026-09-20）：把 `StateText` 换回实机回合号 → 第 1、2 条红；在 `Reset()` 里加回 `ContinuationSource = null` → 第 3 条红。新增同类修复时，**先确认断言在改坏后确实变红**，再宣布它修好了。

## 3. TypeLoadCheck（类型加载保护）

游戏 Mod 加载器会在调用本 MOD 初始化器**之前**枚举 `CoopBots.dll` 的全部类型，而只有初始化器才加载 `CoopBots.Kernel`。任何在加载期就需要内核的类型（例如携带内核类型字段的值类型/迭代器状态机）会让整个 MOD 以 `ReflectionTypeLoadException` 失败。该检查在独立进程中故意让 `CoopBots.Kernel` 不可解析，再枚举类型（`tests/TypeLoadCheck/Program.cs:4-17,44-75`）。

先构建一个含 `CoopBots.dll` 的目录（也可复用 `scripts/build.ps1` 生成的 `work\build-<版本>`）：

```powershell
$build = Join-Path $repo ('work\type-load-check-' + [Guid]::NewGuid().ToString('N'))
& $dotnet build "$repo\src\CoopBots\CoopBots.csproj" -c Release `
  "-p:STS2DataDir=$game" "-p:OutputPath=$build\" -p:NuGetAudit=false

& $dotnet run --project "$repo\tests\TypeLoadCheck\TypeLoadCheck.csproj" -c Release -- `
  $build $game
```

- 参数：`TypeLoadCheck <含 CoopBots.dll 的目录> [额外探测目录...]`（`Program.cs:20`）。
- 退出码 `0` 表示 `CoopBots.dll` 在无内核时仍能枚举全部类型；`1` 表示类型加载会失败；`2` 表示用法错误或隔离不可靠。
- `scripts/build.ps1:27-30` 在打包前会自动跑这一步，失败即中止。

## 4. 构筑调参（⚠ 所属线已废弃，非发布门禁）

> **2026-09-19 判死**：离线卡组评价 / DraftSim 调参线已废弃，本节与文末的 Python 工具
> 只作历史保留。它们的输出目录（`work/oracle/`、`work/measurement-pilot/` 等）
> 已于 2026-09-20 删除，**直接跑会失败**。见 [`../docs/building/README.md`](../docs/building/README.md)。

DraftSim 默认不参与回归，只在显式传参时运行（`tests/PatchSmoke/DraftSimScenarios.cs:5-17`）：

```powershell
# 只跑构筑扫描，跳过普通回归断言
& $dotnet run --project "$repo\tests\PatchSmoke\PatchSmoke.csproj" -c Release `
  "-p:STS2DataDir=$game" -p:NuGetAudit=false `
  -- --deck-sim-only --runs=3 --quick

# 用社区 A10 牌组校验评分（需先准备缓存）
& $dotnet run --project "$repo\tests\PatchSmoke\PatchSmoke.csproj" -c Release `
  "-p:STS2DataDir=$game" -p:NuGetAudit=false `
  -- --deck-sim-validate
```

- 社区数据由 `scripts/fetch-spire-codex-decks.py` 抓取（`CommunityValidationScenarios.cs:19,38-39`）。
- 可用参数：`--deck-sim`、`--deck-sim-only`、`--runs`、`--chars`、`--seed`、`--ascension`、`--players`、`--quick`、`--baseline`、`--out`，以及校验侧的 `--features`、`--incremental`、`--calibrate`、`--par`、`--export`（`DraftSimScenarios.cs:53-69`、`CommunityValidationScenarios.cs:74-90`）。
- 这是**调参与校验工具**，不会打开发布包，也不能替代实机验收。

## 5. 自动化检查证明了什么、没证明什么

测试使用真实的游戏 C# 模型、伤害/格挡 Hook 与 MOD 决策逻辑，夹具只 stub 原生 Godot 打印、编辑器探测、存档入口和"战斗进行中"标志（`tests/PatchSmoke/TestEnvironment.cs:6-18`）。

因此：

- **能证明**：代码可编译并加载、Harmony 补丁按预期挂载、决策逻辑在构造的固定状态下按断言运行、内核分支与类型加载契约成立。
- **不能证明**：
  - 多客户端之间的**动作队列往返、票选同步、断线重连**；
  - 真人+Bot 混编下的**整局流程、UI 显示、原生选牌/用药交互**的真实表现；
  - 实际多人帧时序、延迟、卡顿；
  - 整局胜率或 A10 通关率等统计结论。

这些必须通过两名真人 + Bot 的实机验收（同步、重连、选牌/用药、Boss 结束回合、协作提示）并保留日志来验证；接手检查同样明确"以上不替代多客户端同步、断线重连、UI 显示和整局胜率的实机验收"（[`../TAKEOVER_REVIEW.md`](../TAKEOVER_REVIEW.md) 的"本次验证"一节）。

## 6. 常见失败

- `STS2DataDir` 未指向含 `sts2.dll` 的目录 → 构建/运行报错。
- 缺少 `STS2-RitsuLib.Runtime.dll` → 说明 RitsuLib 不是 0.6.x 的 `compat/<版本>` 目录，用 `-p:RitsuLibDir=...` 指到正确目录（`KernelDependencies.props:15-18`）。
- TypeLoadCheck 报"guard is not isolated" → 隔离不可靠（退出码 2），需检查探测目录与 `Resolving` 处理。
- 生成表（`Baked*.cs`）与当前游戏版本不一致 → `CodexCrossCheckScenarios` 会按预期失败，按第 4 节脚本重新生成。

## 构筑测量工具（纯 Python，⚠ 所属线已废弃）

> 同 §4：这条线 2026-09-19 判死，输入目录 2026-09-20 已删除，下列命令**直接跑会失败**。
> 保留只为查历史与复用其中可迁移的统计方法（配对差、重复性检查）。


`python -m unittest discover -s tests -p test_building*.py` 验证原始终局分类、配对缺失处理、四场重复探针的输入/超时/失败汇总。测试使用离线 fixture 与模拟子进程，不执行战斗。

`python scripts/building_eval.py --audit-pm --output work/measurement-pilot/<新的文件名>.json` 重新审计现有 PM 原始结果，不运行实验、不覆盖已有报告。

`python scripts/building_repeat_probe.py` 默认仅生成计划与干跑汇总；`--run` 才启动固定四场 PM001 重复探针，最多两个进程并发，单场 120 秒、批次 360 秒。四次都完整且终局语义相同时退出 0，否则退出非零。语义一致只覆盖一个种子，不证明所有种子确定或策略更强。

`python scripts/building_budget_probe.py` 默认干跑；`--run` 执行 4s/20s 软预算 × dop1/2 的八场顺序对照，每格两次、单进程120秒、总720秒。它以完整终局是否齐全决定退出码，语义重复性单独报告，不自动批准模型。测试入口已包含于 `test_building*.py`。
