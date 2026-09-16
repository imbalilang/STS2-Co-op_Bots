# CoopBots 源码包说明

本源码包对应 `CoopBots 0.36.1`，只包含继续开发、构建和测试所需的文本源码与配置。

## 包含内容

- `src/CoopBots/`：MOD C# 源码、项目文件和 Mod 清单。
- `src/CoopBots.Kernel/`：接入 CombatSolver 的模拟内核实验项目；`Vendor/` 为上游导入源码，来源见 `UPSTREAM.json` 与 `INTEGRATION.md`。
- `tests/PatchSmoke/`：补丁挂载与多人协作回归测试源码；`DraftSim/` 是本地调参与构筑校验用的模拟器测试线，**不属于 MOD 包体**，构建脚本也不会把它打进发布包。
- `scripts/`：macOS/Linux 与 Windows 构建脚本、CombatSolver 内核导入器、Spire Codex 数据抓取脚本。
- `CombatSolver-0.33.9/`：用于算法研究的参考项目源码、测试、文档、开发约束与第三方声明。
- `README.md`（简介、构建与许可）、`CHANGELOG.md`（完整版本变更记录）、`.gitignore` 和本说明。

## 已排除内容

- 所有 `bin/`、`obj/`、仓库根发行包目录和 `.git/` 目录；作为开发文档的 `docs/releases/` 会保留。
- `work/` 中的本地 .NET SDK、反编译工具、缓存和重复解压目录。
- `outputs/` 中的 DLL、历史安装包和其他发行物。
- 所有 DLL、PDB、EXE、ZIP、RAR、DYLIB、SO 和系统元数据文件。

## 构建环境

- .NET 9 SDK。
- 《杀戮尖塔 2》v0.111.0 的本地游戏程序集。
- RitsuLib 0.6.2+ 的 0.111.0 变体程序集，内核与测试编译期引用。0.6.x 把运行时拆成多个程序集，默认从工坊项的 `compat/0.111.0`（`STS2-RitsuLib.dll`、`STS2-RitsuLib.Runtime.dll`）与同级的 `shared/`（Shared / Ui / Settings）解析，路径由 `$(STS2DataDir)/../../../workshop/content/2868840/3747602295/compat/0.111.0` 推出，可用 `-p:RitsuLibDir=...`（以及 `-p:RitsuLibSharedDir=...`）覆盖。
- macOS/Linux：`./scripts/build.sh`。
- Windows：`scripts/build.ps1`，必要时传入 `-GameData` 和 `-Dotnet`。

游戏程序集、RitsuLib 和 .NET SDK 不随源码包分发。发布包声明 RitsuLib 为运行依赖但不内含其二进制。`CombatSolver-0.33.9` 没有覆盖整个仓库的统一软件许可证；其来源关系和 Random Foreseer 许可条件以随包保留的 `THIRD_PARTY_NOTICES.md` 为准。
