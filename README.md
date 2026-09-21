# Co-op Bots / 联机机器人

为《杀戮尖塔 2》**官方多人房间**加入可配置的 AI 队友。与那些"单机带 AI 陪玩"的实现不同，它把
Bot 注入真实联机大厅：每个 Bot 是真实玩家槽位，行为通过游戏自身的动作队列同步，所有真人必须使用
同一版本。

- 3 档节奏：**Flash**（0.5s/张）/ **Pro**（1.5s/张）/ **Cheated**（3× 金币）；所有档位使用同一套完整算法，差别只在节奏与思考预算
- **全 Bot 模式**：把全部席位都交给 AI 后，每次决策都从实盘重算（续演锦标赛，单次约 1.2 秒），不再有开局数分钟的沉默
- 团队联合决策：共享易伤/虚弱、逐人承伤、格挡投射共享、真人手牌潜力、Bot 主动呼叫补刀
- 战斗搜索基于**真实模拟**（复用 CombatSolver 内核），不是启发式打分
- 构筑：统一估值 + 数据挖掘的流派识别 + 真实升级差分；宁可跳过也不稀释核心
- 事件、商店、营地、路线建议均由 AI 处理

## 最新更新 / Latest：0.38.0（2026-09-22）

- **全 Bot 局改用「续演锦标赛」决策**：不再开局沉默 3–8 分钟才出第一张牌，改为**每个动作都从实盘重算**——每条候选线把战斗模拟到真实终局，取最好那条的首个动作。单次决策预算约 **1.2 秒**，日志新增 `decision-ms`。
- **桌上还有真人时完全不变**：仍是有界搜索（数百毫秒级），不会让真人等。
- **已知代价**：单次决策是同步的，极端情况会短暂卡住画面（秒级）；换来的是不再有数分钟的静默。本版不宣称强度提升。
- 上一版（0.37.1）：修复混合局里真人选牌被 Bot 顶掉。

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

**已发布版本 0.38.0**，安装包 `outputs/CoopBots-v0.38.0.zip`。运行目标为游戏 v0.111.0，
运行依赖 **RitsuLib 0.6.2 及以上**（Steam 创意工坊 id 3747602295）。

> 发布流程见 `docs/RELEASING.md`。版本号的权威是 `src/CoopBots/mod_manifest.json` 与
> `ModEntry.FallbackVersion`；工作树领先于已发布版本时，本区块要等发版才更新。

完整变更记录、每版修复内容与发布状态见 **[CHANGELOG.md](CHANGELOG.md)**。
