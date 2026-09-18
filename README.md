# Co-op Bots / 联机机器人

为《杀戮尖塔 2》**官方多人房间**加入可配置的 AI 队友。与那些"单机带 AI 陪玩"的实现不同，它把
Bot 注入真实联机大厅：每个 Bot 是真实玩家槽位，行为通过游戏自身的动作队列同步，所有真人必须使用
同一版本。

- 3 档节奏：**Flash**（0.5s/张）/ **Pro**（1.5s/张）/ **Cheated**（3× 金币）；所有档位使用同一套完整算法，差别只在节奏与思考预算
- 团队联合决策：共享易伤/虚弱、逐人承伤、格挡投射共享、真人手牌潜力、Bot 主动呼叫补刀
- 战斗搜索基于**真实模拟**（复用 CombatSolver 内核），不是启发式打分
- 构筑：统一估值 + 数据挖掘的流派识别 + 真实升级差分；宁可跳过也不稀释核心
- 事件、商店、营地、路线建议均由 AI 处理
- 大厅与战斗面板的标题栏均可拖动；战斗面板可收起，战斗思考保持有界——没有自动长考，也没有投机脚本回放

## 最新更新 / Latest：0.36.3（2026-09-18）

- **面板可以收起**：点击「收起 / 展开」切换，收起后只保留可拖动的标题栏；切换房间后记住状态，机器人继续正常行动。
- **修复“新叶”变牌卡住**：机器人不再选择“进阶之灾”等无法变化的牌，同类变牌事件也适用。
- **更合理地拿牌、跳过和升级**：结合当前牌组的机制配合、费用负担和攻防需求，减少拿到缺少配合的牌；升级按实际收益比较。
- **删牌与营地决策改进**：连续删牌会逐步重新评估，尽量保留关键功能；营地选择考虑恢复、升级和删牌的取舍。
- **队伍配合与购物改进**：结合队友需求和已有能力评估支援牌；商店比较一次购买与两件商品组合，购买后按实际情况重新规划。

已通过构建及离线回归；尚未完成本版实机联机验证。完整记录见 [CHANGELOG.md](CHANGELOG.md)。

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

当前开发版本 **0.36.3**，最新安装包 `outputs/CoopBots-v0.36.3.zip`。运行目标为游戏 v0.111.0，
运行依赖 **RitsuLib 0.6.2 及以上**（Steam 创意工坊 id 3747602295）。

完整变更记录、每版修复内容与发布状态见 **[CHANGELOG.md](CHANGELOG.md)**。
维护者的发布流程（清单版本、README / Steam 最新说明、打包与校验）见 **[docs/RELEASING.md](docs/RELEASING.md)**。
