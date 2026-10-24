# CoopBots 测试入口

本目录包含两条自动化测试线与一条本地调参线：

- `PatchSmoke/`：主回归控制台程序。
- `TypeLoadCheck/`：独立进程的类型加载保护检查。
- `PatchSmoke/DraftSim/`、`PatchSmoke/DeckSim/`：构筑调参与校验工具，**不属于 MOD 包体**，也不是发布门禁。

以下命令均在仓库根目录执行，使用工作区自带的 `work/dotnet-win/dotnet.exe`（Git 忽略）和**显式指定的游戏程序集目录**。

日常完整验证运行 `scripts/verify-project.ps1`：每次新建 `work/verification/<运行标识>/`，顺序执行生产构建、独立类型加载和**一次无参数 `EnableKernelTests=true` 完整入口**，保存实际命令、日志、退出码及 `summary.json`，不打包或覆盖发行物。独立普通进程改为 `-IncludeOrdinary` 显式诊断；原四门历史证据保持原样。以下单项命令用于定位问题。开发中运行本次改动对应的具名用例与必要 RED/GREEN；同一批候选改动完成、准备部署时才运行一次默认完整验证，聚焦结果不能替代交付完整验证。

## 0. 前提

V2混合局新增四个聚焦入口，也全部注册在无过滤完整入口：

- `--mixed-cold-opening-only`，可加 `--mixed-cold-opening-case=constructor|units|prefix|cancel|portfolio|allbot`，检查构造不做全量开局、原子单位续枚举、真实已结算前缀截止发布、取消撤回、完整策略/联合工作继续，以及默认全Bot路径不变。
- `--mixed-stable-script-only`，可加 `--mixed-stable-script-case=stable-prefix|reservation|recost|identity|unrelated-enemy|allbot-strict|expired-script|fresh-end|expired-card|expired-potion-baseline|no-candidate|control-rebind`。驱动真实多帧Poll、原生扣费动作与AfterFinished，检查真人穿插、四步后续打、准备不消费/结算单次消费、取消重试、物理身份、费用变化和已完成首步EndTurn退休。
- `--mixed-runtime-pacing-only`，可加 `--mixed-runtime-pacing-case=team|finished|delay|invalid|pause|flash|enqueue|resume|focus`。虚拟单调时钟驱动生产TickCombat与真实队列事件，检查跨席1500ms、真人结束立即收尾、慢动画不追赶、失败不改时钟、暂停/忙队列、Flash500ms和选择续租。夹具注入已选动作，搜索质量另由planner场景覆盖，不能把虚拟时钟结果报成实机帧耗时。
- `--mixed-background-replan-only`，可加 `--mixed-background-replan-case=input-burst|full-options|stable-script|nonblocking|shared-budget|candidate|control|allbot|human-death|bot-death|pure-allbot|reset-domain`。检查真人输入合并、前后相同完整配置、后台不阻塞前台、共享预算、实际原生候选严格改进/同根/准备槽保护，以及恢复全Bot未来优化。

`--request-lifecycle-only --request-lifecycle-case=replay-completed-stop` 与 `--planning-state-only --planning-state-case=continuation-deadline` 区分计算截止、完整候选和有效执行剧本的寿命；`--turn-start-choice-only --turn-start-choice-case=transition-diagnostics` 用原生合同区分回合号、席位回合号和未消费选择三轴。历史日志中四次拒绝尚无足够细节确证具体轴，增加诊断不等于这些历史机制已经复现修好。


真人混合桌回归新增五个聚焦入口（均也注册在无过滤完整入口）：`--mixed-room-handshake-only`（可用 `--mixed-room-handshake-case=map|handshake|human-input`）、`--mixed-room-sync-only`、`--mixed-responsiveness-only`（`--mixed-responsiveness-case=progress|proposal|status|control`）、`--mixed-replay-safety-only`、`--mixed-combat-aux-only`（`--mixed-combat-aux-case=focus|focus-change|focus-safety|potion-lease|card-lease`）。它们驱动生产选房屏障/原生自动握手与未来菜单、双端原生宝箱奖励RNG/重连快照/进入与退出清理/真人事件广播、真人连续原生付费出牌后Bot在EndTurn前取得行动、交回控制权、合法历史后缀与可选原生存活开局的比较，以及默认锦标赛集火与急救续招合同。Godot屏幕渲染和物理网络仍是headless边界，不把这些结果报成双客户端实机通过。

多人回合开始卡死回归：`--party-setup-pending-only`，可加 `--party-setup-pending-case=risk|pending|automatic|warm` 分别检查实际 `LiveEndTurnRisk` 未回答菜单及时返回、保留可 fork/续演的原生帧及完整 Facts/history/RNG、自动选牌与严格合同重放、调用方已编译时观察器仍有效。观察点是实际 `ResumeExecutionContinuation`，避免较小包装方法被 JIT 内联后漏计。安全观察器触顶必须失败，不得当作成功。默认完整入口也执行这些场景。实际替换抽牌使后续预选菜单失效的正控制复用 `--szg-combat-only --szg-combat-case=chained-choice`，包含 1/2/4 席的原生 GamblingChip → ToastyMittens 对照。

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

## 1. 默认完整验证（含普通和内核场景）

```powershell
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File "$repo\scripts\verify-project.ps1" -GameData $game
```

它只运行一次无过滤参数的完整 PatchSmoke。完整默认主线保留普通入口，`CooperativeScenarios.Run` 在 `KERNEL_TESTS` 分支追加六组，`KernelEngineScenarios` 再调用对应场景；`Program.cs` 的条件分支还包含 focused flags。`EnableKernelTests=true` 定义 `KERNEL_TESTS`，并编入 `KernelEngineScenarios.cs`、`KernelRoundScenarios.cs`、`KernelPowerRouteScenarios.cs`。保留调用并不证明普通/完整隔离进程中的 presentation/Harmony 环境完全等价。

需要单独定位完整入口时：

```powershell
& $dotnet run --project "$repo\tests\PatchSmoke\PatchSmoke.csproj" -c Release `
  "-p:STS2DataDir=$game" -p:EnableKernelTests=true -p:NuGetAudit=false
```

程序不得带 `--*-only`、case 或 `--packaging-smoke` 等过滤参数。退出码 0 才是实际通过；PASS 计数来自原始日志，不从场景数量推导。ProjectReference/绑定路径见 `PatchSmoke.csproj`：壳可通过 `CoopBotsDll` 指定门禁产物，内核仍有必需的 ProjectReference，实际运行双 DLL 必须和门禁归档物理工件核对。

默认 `summary.json` 的普通阶段状态为 **`covered-by-full`**，其数值通过数 **`null`**；这表示入口覆盖，不是一份另跑过的普通结果。**`full_total` 只取完整内核运行的 PASS 行数**，不与独立普通读数相加。若完整入口失败，不能用覆盖标记宣布普通已通过。

## 2. 独立普通诊断（opt-in）

需要比较普通进程环境时才追加诊断：

```powershell
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File "$repo\scripts\verify-project.ps1" -GameData $game -IncludeOrdinary
```

单独编译运行普通配置仍可用：

```powershell
& $dotnet run --project "$repo\tests\PatchSmoke\PatchSmoke.csproj" -c Release `
  "-p:STS2DataDir=$game" -p:EnableKernelTests=false -p:NuGetAudit=false
```

两种入口都保留；独立普通运行记录其实际命令、退出码、日志和 PASS 数，不能算第二份完整总数。聚焦诊断与打包 smoke 子集均不能替代第 1 节的默认完整验证。

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
- 默认完整验证在独立进程运行这一步。带 `-VerifiedGateBinding` 的打包路径验证既有类型加载证据并复用工件，不重新执行；legacy 无绑定路径仍自行运行类型加载和 smoke 子集。

### 复用完整验证工件打包

```powershell
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File "$repo\scripts\build.ps1" `
  -GameData $game -VerifiedGateBinding '<immutable-epoch-directory>/gate-run-binding.json' `
  -Label '<fresh-label>'
```

绑定由该不可变 epoch 的 `run-bound-final-gates.ps1` 生成。打包核对门禁真实退出码 0、当前源码/构建引用/包内文档与冻结输入，以及 `gate/build/` 和运行时归档双 DLL；通过后直接复制门禁工件，**不重新构建或运行 smoke**。保存 ZIP 和包内两份 DLL 的哈希及包验证收据。Label 必须全新，已有输出目录/ZIP 不覆盖；输入变了必须重新获得匹配绑定，不能修改旧绑定掩盖变化。

未传 `-VerifiedGateBinding` 的 legacy 出包仍走构建、类型加载和 smoke 子集。它可以用于备用诊断，不能称完整验证通过，也不能用 smoke 数字替代完整日志读数。

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

## 2026-10-23 贡献包增量回归

`CommunityPackageScenarios` 纳入默认完整入口，也可通过 `--community-package-only` 聚焦运行；可选 `--community-package-case=acquisition|survival|juggernaut|team|copy` 单独定位。覆盖首张关键牌奖励／网格／商店与诅咒优先、取消传播、当前血量成本、势不可当的启动与格挡事件、可支付的队友增伤和复制投资。JUGGERNAUT 是势不可当，DOMINATE 是主宰，测试显式区分两张牌。

十个生产故障实际 RED1、精确恢复 GREEN0 的命令与原文保存在维护者本机 `work/merge-key-card-acquisition-20261023/r2/`，断言注释保留对应失败文本。聚焦通过不代表完整门禁或 Steam 实机通过；完整结果以本次 `work/verification/` 收据为准。

血量评分快照与营火新升级选择另经两个生产故障验证 RED1／精确恢复 GREEN0，证据在同一任务目录的 `r2-integration/`。首次“关闭血量风险应同时翻转营火选择”的假设未被检出，其失败尝试保留于 `r2-hp-baseline/`；不将该假设计作有效故障证明。

临时属性到期回归可用 `--expiry-only` 聚焦运行，`--expiry-record` 仅在运行目录导出128条测量值。贡献包只改变其中16条未来虚弱估值，其余112条保持逐位相等；冻结旧DLL／原包DLL／重建DLL三方对照在 `expiry-parity.json`。忽略未来格挡的生产故障另经 RED1／精确恢复 GREEN0，证据在 `r2-expiry/`。负力量回归改按所有存活队友计算虚弱收益，永久力量和临时恢复两项原守卫重新故障验证，证据在 `r2-future-stats/`。

普通永久单选夹具保留原全部断言，将不再保证正收益的 ANGER 换成非关键牌 POMMEL_STRIKE。原包与重建DLL通过更新后的整个 LatestBuildRepairScenarios，旧DLL通过原夹具；错误分流故障 RED1／精确恢复 GREEN0 证据在 `r2-single-choice/`。原包／旧基线对照命令在 `latest-policy-parity.json`，本次共16项有效故障证明。
