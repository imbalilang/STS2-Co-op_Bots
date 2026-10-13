# Co-op Bots / 联机机器人

为《杀戮尖塔 2》的官方多人房间加入可配置的 AI 队友。Bot 使用真实玩家席位，通过游戏的动作队列同步出牌、选牌和局外决策。

Configurable AI teammates for Slay the Spire 2's official multiplayer lobbies. Contributions from developers and coding agents are welcome; start with [CONTRIBUTING.md](CONTRIBUTING.md).

## 最新源码 / Latest source：0.39.1 开发快照（2026-10-13）

本次公开当前算法、回归组件与 Steam 实机测试管线，邀请其他作者共同优化。源码快照保留 0.39.1 版本号；工坊安装包与 GitHub 源码的交付身份应分别核对。

- 战斗使用 CombatSolver 0.41.0 导入内核，结合四席联合规划、有界搜索、完整状态核对和失效后重新规划。
- 选牌、药水与跨回合计划携带执行合同；单回合力量/敏捷按实际持续时间估值，升级和构筑使用卡组上下文。
- v21 修复多人初始化阶段带抽弃牌能力时的等待问题。历史完整门禁通过 **887 项**；后续同一冻结候选五局全 Bot A10 实机结果为 **0 通关、5 自然失败、0 卡死**。这五局属于开发诊断样本，尚未证明 50% 通关目标，也未完成混合桌验收。
- 公开 Agent 规范、九项项目技能、类型加载检查、内核回归、原生对照入口、种子划分及日志归档/分析工具。

完整变更见 [CHANGELOG.md](CHANGELOG.md)；当前证据、边界与优化优先级见 [docs/STATUS.md](docs/STATUS.md)。旧候选记录保存在 [历史说明](docs/LOCAL_CANDIDATE_HISTORY.md)。

## 使用

需要本地游戏 **v0.111.0** 和 **RitsuLib 0.6.2+** 的兼容变体。所有真人使用相同的 MOD 版本；RitsuLib 不随本项目二进制分发。

- 在官方多人大厅添加 Bot，可调整节奏和性能预算。
- Flash、Pro、Cheated 共用算法；档位影响节奏、预算，Cheated 另有金币加成。
- 支持全 Bot 和真人/Bot 混合桌。混合桌中的真人输入会使依赖旧盘面的计划失效，效果需单独测试。
- Bot 可处理奖励、商店、事件、营地及路线建议。当前优化基线保持路线设置和既有搜索预算，知识恶魔统一选择瓦解。

构建后将 `CoopBots.dll`、`CoopBots.Kernel.dll` 和 `mod_manifest.json` 放入游戏的 `mods/CoopBots/`，并安装匹配的 RitsuLib。构建脚本的输出与依赖路径见测试指南。

## 开发与协作

使用 .NET 9 SDK、Python 3 和本地合法安装的游戏/RitsuLib 程序集。干净克隆无需复制维护者的 `work/` 缓存。Windows 完整验证入口：

```powershell
pwsh -File scripts/verify-project.ps1 `
  -Dotnet 'C:\path\to\dotnet.exe' `
  -Python 'C:\path\to\python.exe' `
  -GameData 'D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64' `
  -RitsuLibDir 'D:\SteamLibrary\steamapps\workshop\content\2868840\3747602295\compat\0.111.0' `
  -RitsuLibSharedDir 'D:\SteamLibrary\steamapps\workshop\content\2868840\3747602295\shared'
```

门禁依次执行 Release 构建、独立类型加载检查和一次未过滤的完整内核回归。测试耗时与原生模拟规模有关；定位阶段可先使用指南中的聚焦入口，交付前仍运行完整门禁。

| 入口 | 内容 |
| --- | --- |
| [贡献指南](CONTRIBUTING.md) | 人类和 Agent 的协作、PR 证据、改动范围 |
| [算法地图](docs/ALGORITHM.md) | 战斗、构筑、执行合同及关键代码入口 |
| [测试与基准](docs/TESTING_AND_BENCHMARKS.md) | 干净克隆、原生测试、Steam 实机、种子与统计 |
| [当前状态](docs/STATUS.md) | 已知失败、性能约束、贡献优先级 |
| [AGENT.md](AGENT.md) / [PLAN.md](PLAN.md) | 项目权威规范及规划；AGENTS.md 为兼容入口 |
| [.claude/skills](.claude/skills) | 可供其他 Agent 使用的项目工作流 |
| [源码内容](SOURCE_PACKAGE.md) | 公开内容与外部依赖 |

请优先提供能复现问题的原生短场景，并在固定性能预算下比较旧版与新版。公开结果保留自然失败、卡死和异常尝试；开发种子与预先登记的留出种子分开，不能用战斗胜率替代整塔通关率。

## 许可与致谢

CoopBots 自有代码采用 [MIT](LICENSE)。导入的 CombatSolver / Random Foreseer 代码保留其 MIT 许可和署名；Spire Codex 派生数据适用 **PolyForm Noncommercial 1.0.0**，不能把整个仓库视为不受限制的 MIT 数据集。完整说明与许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 [licenses/](licenses/)。

感谢 [CombatSolver](https://github.com/Torch1230/CombatSolver)、[Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer)、[Spire Codex](https://spire-codex.com) 和 RitsuLib 作者。游戏与原版资源属于 Mega Crit Games；仓库不分发游戏程序集。