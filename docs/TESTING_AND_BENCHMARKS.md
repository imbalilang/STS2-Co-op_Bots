# 测试、原生复现与固定版本基准

本页面向社区作者与 Agent。公共历史摘要在 [STATUS.md](STATUS.md) 和 [v21 实测身份](evidence/v21-20261012.json)；原始 `work/` 记录留在维护者本机，摘要不是克隆后已重跑的结果。

## 依赖与本机路径

当前完整回归与实机管线以 Windows 为维护和验证环境。需要自己安装的 .NET 9 SDK、Python 3.10+（本机验证使用 3.11）、PowerShell 7、Steam《杀戮尖塔 2》及与目标游戏版本兼容的 RitsuLib 0.6.x。RitsuLib 的版本相关 DLL 在 `compat/<game-version>/`，Shared/Ui/Settings 在 `shared/`。依赖不随源码分发；没有游戏依赖的公共脚本检查不能代替原生 C# 回归。

在仓库根目录设定自己的路径，以下盘符只是示例：

```powershell
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$python = 'C:\Python311\python.exe'
$gameRoot = 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
$gameData = Join-Path $gameRoot 'data_sts2_windows_x86_64'
$ritsu = 'D:\SteamLibrary\steamapps\workshop\content\2868840\3747602295\compat\0.111.0'
$ritsuShared = 'D:\SteamLibrary\steamapps\workshop\content\2868840\3747602295\shared'
```

先确认实际游戏版本、`sts2.dll`、`STS2-RitsuLib.dll` 和 `STS2-RitsuLib.Runtime.dll`。示例中的 `0.111.0` 是当前源码默认引用，不保证未来游戏更新仍兼容；更新后先验证依赖和原生行为。SDK 可以装在任意位置，仓库不要求下载维护者的 `work/dotnet-win/`。

## 一次默认完整验证

```powershell
pwsh -NoProfile -NonInteractive -File scripts/verify-project.ps1 `
  -GameData $gameData -Dotnet $dotnet -Python $python `
  -RitsuLibDir $ritsu -RitsuLibSharedDir $ritsuShared
```

该命令新建 `work/verification/<运行标识>/`，顺序执行：

1. 生产 Release 构建，使用实际游戏和 RitsuLib 引用。
2. 独立 TypeLoadCheck：故意不解析内核，确认壳仍可加载全部类型。
3. 一次 `EnableKernelTests=true`、无程序过滤参数的完整 PatchSmoke，覆盖普通场景和内核场景。

保存实际命令、退出码、原始日志、`summary.json`、冻结输入与 `gate-run-binding.json`。判据由 [AGENT.md §3](../AGENT.md) 定义；必须读非 PASS 诊断及失败原文，不只数绿行。

默认 ordinary 记录为 `covered-by-full`，独立通过数为 `null`。若要诊断独立普通进程的钩子/环境差别，显式加 `-IncludeOrdinary`；它的读数单列，不与完整套件相加。聚焦 case 和 `--packaging-smoke` 均不能冒充完整回归。

`python scripts/state.py gate` 和 `python scripts/state.py log "本次结论及证据"` 可记录自己的本地进度。没有当前运行时就写“未验证”，不要从公开历史摘要给自己的检出补一份通过状态。

## 开发中的聚焦原生回归

不要每次改一行都跑完整套件。先运行本次改动对应的具名入口；候选完成后才执行一次完整门禁。例如多人回合开始 pending 与菜单失效：

```powershell
& $dotnet run --project tests/PatchSmoke/PatchSmoke.csproj -c Release `
  "-p:STS2DataDir=$gameData" "-p:RitsuLibDir=$ritsu" `
  "-p:RitsuLibSharedDir=$ritsuShared" -p:EnableKernelTests=true -p:NuGetAudit=false `
  -- --party-setup-pending-only

& $dotnet run --project tests/PatchSmoke/PatchSmoke.csproj -c Release `
  "-p:STS2DataDir=$gameData" "-p:RitsuLibDir=$ritsu" `
  "-p:RitsuLibSharedDir=$ritsuShared" -p:EnableKernelTests=true -p:NuGetAudit=false `
  -- --szg-combat-only --szg-combat-case=chained-choice
```

前者驱动实际 `LiveEndTurnRisk`、保留待决原生帧/Fork/续演和严格重放；后者驱动抽牌使后续菜单变化的 1/2/4 席正控制。全部聚焦入口见 [tests/README.md](../tests/README.md)，不要猜 flag 名称。

新正确性断言的 R2 证据在隔离检出完成：

```text
GREEN：原候选编译成功，具名原生断言通过。
RED：仅撤销被测修复/注入对应故障，编译成功，同一断言因预期机制失败。
RESTORE：精确恢复源文件与相关工件，再跑同一断言通过。
记录：源码差异、各实际命令/退出码、失败原文、恢复哈希。
```

测试看护器触顶、编译失败、错误参数或缺失依赖不构成有效 RED。一个构造夹具证明该机制，不自动证明历史整局隐藏 RNG 或所有前序动作已重现。

## 复用已验证工件打测试包

所有源码、引用和包内文档完成后再获取最终绑定。使用新标签：

```powershell
pwsh -NoProfile -NonInteractive -File scripts/build.ps1 `
  -GameData $gameData -Dotnet $dotnet -Python $python `
  -RitsuLibDir $ritsu -RitsuLibSharedDir $ritsuShared `
  -VerifiedGateBinding '<本次运行目录>\gate-run-binding.json' `
  -Label 'my-algorithm-experiment-01'
```

绑定路径核对输入、门禁、原始工件和两份 DLL 后直接复制门禁产物，不重构建或重跑 smoke。任何输入变化必须重新验证；旧包和标签不能覆盖。记录 ZIP、`CoopBots.dll`、`CoopBots.Kernel.dll` 三份 SHA256，版本字符串不足以识别候选。

这是本地测试包，不会自动部署或启动游戏。正式 Steam 发版遵循 [RELEASING.md](RELEASING.md)。

## 社区完整 A10 管线

[`scripts/community-benchmark.py`](../scripts/community-benchmark.py) 提供标准库准备步骤，不依赖维护者的忽略目录脚本或私人存档。先用上一节生成的新包及相邻 `<zipstem>.verification.json` 冻结候选：

```powershell
& $python scripts/community-benchmark.py freeze `
  --package 'outputs/CoopBots-v0.39.1-my-algorithm-experiment-01.zip' `
  --out work/community/candidate

$dataRoot = Join-Path $env:APPDATA 'SlayTheSpire2'
& $python scripts/community-benchmark.py reserve `
  --history-root $dataRoot --out work/community/seeds `
  --development 20 --holdout 30
```

`freeze` 核对实际三阶段完整门禁、当前冻结输入、包与双 DLL，生成本机 `receipt.json`，不部署。输出目录必须全新。`reserve` 生成互不重叠的随机开发/留出 seed 并扫描明确指定的本地历史目录；目录必须已存在，输出不能在扫描目录内。可重复加 `--history-root` 纳入此前实验结果。它只证明该局部扫描未发现字面 seed，不证明全球从未用过或统计独立；`20/30` 是示例规模，不是 50% 达标判据。

创建本机计划并检查首个开发尝试：

```powershell
$pwsh = (Get-Command pwsh).Source
& $python scripts/a10-benchmark.py plan `
  --baseline-receipt work/community/candidate/receipt.json `
  --candidate-receipt work/community/candidate/receipt.json `
  --reservation work/community/seeds/reservation.json `
  --game-root $gameRoot --data-root $dataRoot --power-shell $pwsh `
  --out work/community/plan.json

& $python scripts/a10-benchmark.py dry-run work/community/plan.json `
  --results work/community/results --arm candidate --split development --limit 1
```

这里给两臂同一收据只是满足既有计划格式，**仅执行 candidate 臂，不能称为旧新 A/B 对照**。真正配对时传各自独立冻结的 baseline/candidate 收据，保持相同游戏、路线、席位和预算。`dry-run` 不部署、不启动、不写触发文件或获取游戏锁。

核对计划配置、机器空闲与授权后运行：

```powershell
& $python scripts/a10-benchmark.py run work/community/plan.json `
  --results work/community/results --arm candidate --split development --limit 1

& $python scripts/a10-benchmark.py analyze work/community/plan.json `
  --results work/community/results --out work/community/report.json
```

`run` 备份当前安装文件、部署冻结候选、写自己拥有的触发文件、经 Steam 开局，并归档进程与实际日志/历史。它拒绝现有游戏、既存触发文件或被占用的锁；具名 P0 在完整日志扫描中优先停止，单场战斗胜利不触发整塔成功。关闭只对匹配 PID、创建时间和路径的自有测试进程请求正常关窗，**没有强杀回退**。失败关闭时保存触发文件、锁与尝试，等待操作者处理；不能删掉失败尝试后重跑成一场成功。成功结束会归档自己的触发文件并释放锁，安装目录保留所选测试候选。

开发与性能资格完成、候选保持固定且确认方案已预先确定时，留出运行需显式 `--split holdout --allow-holdout`；默认分析隐藏留出结果，只有 `analyze ... --include-holdout` 才公开它们。查看后用于优化的留出结果转为诊断证据。要继续下一次开发尝试可重复 `run ... --limit 1`；使用新的候选、种子集和实验输出目录来开始新一轮，保留旧结果。

## 实机测试的身份与边界

实机测试需要当前机器操作者明确授权，并串行占用机器。默认经 Steam 启动；部署前确认客户端已关闭，备份旧 `/mods/CoopBots`，核对安装双 DLL 与包一致。不得在现有用户游戏上直接覆盖、热加载或结束进程。

一次尝试应归档：固定候选/双 DLL/ZIP、游戏与依赖身份、路线输入、席位/难度/预算、seed、实际启动命令、日志、原生历史和战斗入口、搜索成本、终局，以及 PID/启动时间/路径与关闭证据。个人账号和完整原生存档默认仅保留本地；公开时用经过审查的摘要、哈希和代码夹具。

正常关闭自己启动且身份匹配的进程；失败关闭不是成功结束。保留卡死/异常现场，按既有授权处理，不绕过审批。客户端确认关闭后清理自己的 `live-test.flag` 和锁；在给下一场写触发文件前重新核对没有其他测试占用。历史 `run-live-loop.ps1` 有杀客户端行为，不应未经审查直接作为社区默认入口。

保存单场战斗、弱化 Boss 诊断、构造原生夹具和完整爬塔分开统计。只有正确难度、固定预算、完整入口和可核验终局的全塔测试可以进入完整 A10 胜率主结果；其余仍保留为诊断或无效协议尝试，不静默删除。

## 对照与胜率口径

- 在开始前固定候选版本、席位、预算、开发/留出 seeds、样本规模和停止规则；路线保持当前对照。
- 同 seed 的旧新版本配对，先用保存入口比较战损、存活、药水消耗、实际候选覆盖和成本，再跑完整塔。
- 多轮优化使用开发 seeds。查看或用于调参的留出 seed 转为开发证据；新候选预留新确认 seeds。
- 每个计划尝试保留，单列胜利、自然失败、卡死、异常、外部停止、协议无效、未开始及重试。主要操作成功率按预先计划分母；自然终局条件胜率另列，不能用它隐藏失败尝试。
- 不混合不同双 DLL 的胜率，不把四席、每场战斗或每次搜索视作独立完整爬塔样本。
- 报告经验胜率、样本量、置信区间和假设。单局胜利或 5/10 只足以做筛查，不能宣称总体已达到 50%。边看边延长批次需要预先定义序贯规则，不能挑一个有利时点结束。
- 真人混合桌单列验证：已结算输入后的重新规划、取消/过时工作、响应时间、状态一致性和协作战损。

性能至少记录实际搜索 `wall/compute/alloc/gcPause`、硬件、并发、预算、过时/被拒候选、原子动作尾耗时和样本数。未采到的指标记未知，不补零；日志里的 GC 或搜索时间不等于 FPS、总进程内存或卡顿已经解决。提高速度的同时必须检查固定预算下的有效候选质量。
