# Co-op Bots / 联机机器人

为《杀戮尖塔 2》**官方多人房间**加入可配置的 AI 队友。与那些"单机带 AI 陪玩"的实现不同，它把
Bot 注入真实联机大厅：每个 Bot 是真实玩家槽位，行为通过游戏自身的动作队列同步，所有真人必须使用
同一版本。

- 3 档节奏：**Flash**（0.5s/张）/ **Pro**（1.5s/张）/ **Cheated**（3× 金币）；所有档位使用同一套完整算法，差别只在节奏与思考预算
- **续演锦标赛决策**：只要桌上有交给 AI 的席位（全 Bot 或人机混合）就用同一套锦标赛（单次约 1.2 秒）；找到的剧本逐步执行，不再每张牌重搜
- 团队联合决策：共享易伤/虚弱、逐人承伤、格挡投射共享、真人手牌潜力、Bot 主动呼叫补刀
- 战斗搜索基于**真实模拟**（复用 CombatSolver 内核），不是启发式打分
- 构筑：统一估值 + 数据挖掘的流派识别 + 真实升级差分；宁可跳过也不稀释核心
- 事件、商店、营地、路线建议均由 AI 处理

## 最新更新 / Latest：0.39.0（2026-09-23）

- **人机混合局也用续演锦标赛决策**，并且找到的剧本**逐步执行**、不再每张牌重搜一遍——修掉了混合局里每帧约 6 毫秒的持续占用（上一版虽然把计算分片，但每步都重跑一遍完整锦标赛）。性能档位现在在任何有 Bot 的桌上都可调。
- **「不打」成为选项**：鬼火在能量换不出牌时不再白打，低威胁下的尖啸会保留；一回合增益（调制 / Fade / Coordinate / Oblivion）先于队友的攻击 / 防御打出，单回合力量 / 敏捷药水只在还有匹配牌时喝。
- **偷牌有代价**：终局比较计入未追回的偷取资源，被螳螂偷走关键牌后只顾防守不再算好结局。
- **路线认遗物与金币**：遗物决定偏向精英还是安全房间；500+ 金币的座位直奔最近商店；全 Bot 健康队伍主动打精英。
- **商店为「核心牌」多出价**；宝箱奖励每个 Bot 都补发（SPOILS_MAP 能变现）；非终局奖励组不再卡死房间。
- 修复知识恶魔诅咒计数；并行锦标赛的启动也按帧切片（Act 3 BOSS 动画不再卡）。
- 上一版（0.38.2）：战斗内不再卡动画、性能档位、商店只买永久提升。

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

**已发布版本 0.39.0**，安装包 `outputs/CoopBots-v0.39.0.zip`。运行目标为游戏 v0.111.0，
运行依赖 **RitsuLib 0.6.2 及以上**（Steam 创意工坊 id 3747602295）。

> 发布流程见 `docs/RELEASING.md`。版本号的权威是 `src/CoopBots/mod_manifest.json` 与
> `ModEntry.FallbackVersion`；工作树领先于已发布版本时，本区块要等发版才更新。

完整变更记录、每版修复内容与发布状态见 **[CHANGELOG.md](CHANGELOG.md)**。
