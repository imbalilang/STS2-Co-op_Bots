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

## 最新更新 / Latest：0.36.4（2026-09-19）

- **面板默认更省地方**：进入对局时战斗面板默认收起为标题栏，玩家展开后本次对局内保持展开；大厅面板尺寸改为贴合文字，不再固定宽度留出大片空白。
- **不再挡住敌人意图**：战斗面板会自动向下避开敌人意图气泡；玩家拖动过面板后就不再自动移动。
- **补刀提示带箭头**：呼叫真人补刀时，除了文字，还从该玩家手牌画箭头指向目标敌人。
- **大厅面板跟随房间**：离开房间再开房不再残留上一个房间的席位与名单；单机界面不再出现该面板。
- **选牌改用社区数据**：以社区全跑局 Elo（玩家被提供时实际会选谁）为基底，原启发式降为有界修正，并加入成型度与关联卡牌加权；社区未覆盖的牌标注为回退并记录原因。
- **内核战斗搜索**：回移 0.41 的部分 power-route 策略到多人内核搜索。

已通过隔离源码构建、类型加载与普通 / 内核回归；尚未完成本版实机联机验证。完整记录见 [CHANGELOG.md](CHANGELOG.md)。

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
