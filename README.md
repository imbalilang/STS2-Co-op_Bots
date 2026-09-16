# Co-op Bots / 联机机器人

为《杀戮尖塔 2》**官方多人房间**加入可配置的 AI 队友。与那些"单机带 AI 陪玩"的实现不同，它把
Bot 注入真实联机大厅：每个 Bot 是真实玩家槽位，行为通过游戏自身的动作队列同步，所有真人必须使用
同一版本。

- 3 档节奏：**Flash**（0.5s/张）/ **Pro**（1.5s/张）/ **Cheated**（3× 金币）；所有档位使用同一套完整算法，差别只在节奏与思考预算
- 团队联合决策：共享易伤/虚弱、逐人承伤、格挡投射共享、真人手牌潜力、Bot 主动呼叫补刀
- 战斗搜索基于**真实模拟**（复用 CombatSolver 内核），不是启发式打分
- 构筑：统一估值 + 数据挖掘的流派识别 + 真实升级差分；宁可跳过也不稀释核心
- 事件、商店、营地、路线建议均由 AI 处理

完整的**版本变更记录**见 [CHANGELOG.md](CHANGELOG.md)。

## 构建 / Build

需要 .NET 9 SDK、已安装的游戏、以及 RitsuLib 0.6.2+ 的程序集（编译期引用）。

```powershell
# Windows：构建 Release、跑两套回归、打包并输出哈希
powershell -ExecutionPolicy Bypass -File scripts/build.ps1

# 指定游戏目录（默认自动探测）
powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -GameData "<...>\data_sts2_windows_x86_64"
```

```bash
# macOS / Linux
./scripts/build.sh
```

产物在 `outputs/CoopBots-v<版本>.zip`，解压到游戏 `mods/CoopBots/` 即可。

`src/CoopBots.Kernel/Vendor/` 是已导入的上游模拟内核源码，仓库里直接可编译。若要从上游重新导入，
见 `src/CoopBots.Kernel/INTEGRATION.md` 与 `scripts/import-combatsolver-kernel.py`。

## 许可证 / License

本项目代码采用 **MIT**（见 `LICENSE`）。该许可**只覆盖本仓库中我们自己编写的代码**；`Vendor/`
内的上游代码、以及 `Baked*.cs` 中来自 Spire Codex 的数据各自适用其原有条款，详见
`THIRD_PARTY_NOTICES.md`（必须随任何分发保留）。

---

## 版本 / Version

当前开发版本 **0.36.1**，最新安装包 `outputs/CoopBots-v0.36.1.zip`。运行目标为游戏 v0.111.0，
运行依赖 **RitsuLib 0.6.2 及以上**（Steam 创意工坊 id 3747602295）。

完整变更记录、每版修复内容与发布状态见 **[CHANGELOG.md](CHANGELOG.md)**。
