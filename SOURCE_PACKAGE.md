# CoopBots 源码与发布包说明

本次公开 **0.40.0 源码与安装包（2026-10-25）**，用于共同优化算法。GitHub Release与Steam工坊安装包使用同一版本；历史冻结v21的实机结果见 [docs/STATUS.md](docs/STATUS.md)，不作为本版新增实机或胜率证明。

## 包含

- `src/CoopBots/`：联机 MOD、战斗执行、局外构筑和事件决策。
- `src/CoopBots.Kernel/`：CombatSolver 0.41.0 导入内核、四席搜索与执行合同；上游身份和本地偏离记录随源码保留。
- `tests/PatchSmoke/`、`tests/TypeLoadCheck/`：完整回归、原生对照和独立类型加载守卫；另含 Python 管线自测及匿名化样本。
- `scripts/`：构建、验证、内核导入/适配、Steam 实机测试、归档与分析，以及公开基准入口。
- `AGENT.md`、`AGENTS.md`、`PLAN.md`、九项 `.claude/skills/`：供参与作者及其 Agent 使用的协作规范。技能内的历史 `work/` 路径指维护者的私有证据，不要求其他作者拥有该目录。
- 文档、第三方署名和许可证。历史候选记录只作为背景，当前规范以 `AGENT.md` 为准。

## 外部依赖与本地内容

需要 .NET 9 SDK、Python 3、本地游戏 v0.111.0 程序集，以及 RitsuLib 0.6.2+ 对应的 compat 和 shared 程序集。构建路径可通过命令参数指定，详见 [测试指南](docs/TESTING_AND_BENCHMARKS.md)。

游戏/RitsuLib DLL、SDK、缓存、发行 ZIP、`bin/`、`obj/`、个人原始日志/存档、账号标识与凭据不随源码上传。`work/`、`outputs/` 和 `third_party/` 参考副本仍属于本地内容；必要脚本和可复现匿名化测试已提取到公开目录。公开实机证据只包含汇总、种子、版本和哈希。

构筑测试中的独立模拟器不是游戏原生模拟，不应单独用来证明实际通关收益。正常 MOD 构建不包含这些测试组件。CombatSolver、Random Foreseer 和 Spire Codex 各自的许可条件见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
