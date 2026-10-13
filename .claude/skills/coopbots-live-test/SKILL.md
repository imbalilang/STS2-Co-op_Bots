---
name: coopbots-live-test
description: Run CoopBots unattended live tests against the real Slay the Spire 2 client — package immutable verified gate artifacts under a fresh label without rebuilding, deploy to mods/CoopBots, launch through Steam, watch godot.log, decide whether each fight followed its script, classify how the run ended, and hand the machine back safely. Use when the user asks to 跑实机 / 无人值守测试 / 复现某个局内现象 / 验证一处执行侧修复 / 看护日志, or when a kernel fix needs live evidence.
---

# CoopBots 无人值守实机测试管线

完整规范在 `AGENT.md` §5 **S5**（skills 注册表）。本文件是它的可调用入口；冲突时以 `AGENT.md` 为准。

**社区当前入口**：`docs/TESTING_AND_BENCHMARKS.md` 描述可公开的准备、回归和 `scripts/a10-benchmark.py` 管线。下面的 `run-live-loop.ps1` 及 2026-09-20 批次工具是历史路径，不是默认社区启动命令；其“杀客户端”行为不能替代当前授权、PID/启动时间/路径所有权和正常关闭合同。本机 `work/` 中的旧脚本、存档与日志不随克隆分发。

## 0. 前置：两个不可跳过的动作

1. **部署前必须关闭客户端**（DLL 被占用，否则部署静默失败）。
2. **交还机器前必须删除哨兵** `mods/CoopBots/live-test.flag`。
   留着会让用户**一开游戏就被劫持**成一局自动 4BOT A10（实测）。
   `run-live-loop.ps1` 跑完**不会**自动删。

## 1. 跑一轮

先完成 `AGENT.md` §3 的默认验证：生产构建、独立 TypeLoadCheck、一次无过滤参数的 `EnableKernelTests=true` 完整 PatchSmoke。普通进程仅在 `-IncludeOrdinary` 显式诊断时另跑；默认普通记 `covered-by-full`/数值 `null`，完整总数只取完整内核日志。沿用现有门槛，不改原四门历史证据。

```powershell
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File scripts/build.ps1 `
  -GameData '<actual game data>' -VerifiedGateBinding '<immutable-epoch-directory>/gate-run-binding.json' `
  -Label '<fresh-label>'
```

绑定由本次 `verify-project.ps1` 冻结输入并核对运行时工件后生成；旧 epoch runner 绑定保持原样。打包验证实际门禁 0、当前源码/引用/包内文档与冻结输入和归档双 DLL，然后直接复用门禁 `build/` 工件，**不重构建、不重跑 smoke**；Label 必须全新，保留包与双 DLL 哈希。输入变化不能沿用旧绑定。legacy 无绑定的构建/类型加载/smoke 子集出包不算完整验证；本轮用户简化授权优先于旧重复条款。

历史 `run-live-loop.ps1` 曾自行杀客户端、部署、写哨兵并看护；执行它前必须审查这些副作用，不能据此默认结束现有游戏。当前完整爬塔使用具名冻结候选与预先固定的计划。只正常关闭自己启动且身份匹配的测试进程；失败时保留现场并按明确授权处理，不能绕过自动审批。客户端确认关闭后才清理自己拥有的旗帜/锁，不能清理另一个测试的控制文件。

- **必须经 Steam**（`steam://rungameid/2868840`）。直启 exe 进 editor 模式，在 profile 设置加载前枚举 mod，于是**每个 mod 都被跳过**。
- **哨兵文件**：`mods/CoopBots/live-test.flag`，第一行 = 种子；可加一行 `mode=single` 回到"单席直进一场战斗"。
  默认 = **多人 4 BOT / A10 / 从地图开始爬**。
  是文件不是环境变量：经 Steam 启动时游戏**不是本进程的子进程**。
  路径取**程序集自己的目录**（mod 被 `Assembly.LoadFrom` 隔离加载，`AppContext.BaseDirectory` 指向游戏 exe）。

## 2. 看护判据 = 局面指纹，不是 mtime

mod 自己每 5s 一行 `CoopBots live test: where …` 会把日志 mtime 一路刷新 → **mtime 规则永远不触发**（实测卡在商店 4 分钟不被杀）。

指纹 = `floor/room` + **最后一条进度行的原文**。
**不要用滑动窗口里的行计数**：旧行滚出 `-Tail` 窗口会让计数变小，一次缩小就是一记假"进展"。

## 3. 判读验收（`grep "combat summary"`，每场一行）

| 字段 | 读法 |
|---|---|
| `plans=n (coverage=x%)` | 部署过的剧本数；一场里应**随段数增长** |
| `routes=k/n` | n 个剧本里 k 个搜到了战场解 |
| `planRefusals=0(drift=…,pending=…,missing=…)` | 区分动作不可执行、等待事务结算与缺少重放证据。具名实机/模拟差异仍按 R9 为 P0，不能因拒绝数为 0 而忽略 |
| `noScript=0` | 内核已无"停止思考"的正常路径 → 非 0 是**报警** |
| `exception=0` / `seatSkips=` | 异常回退 / 席位退场被跳过的步数 |
| `maxRound=` | 必须是真实回合数（**曾每场都是 1**，因为采样只发生在搜索起点） |

**"没有拒绝" ≠ "没被比对"**：`missing` 高时哨兵对那一段失去校验能力，`drift=0` 只是没在比。

## 4. 终止方式必须分类（否则会把外部事件读成你的 bug）

| 现场 | 含义 |
|---|---|
| 有 `How did the game end??` | **运行结束**（团灭/胜利）→ 才可以谈强度 |
| 有 `NGame.Quit called` 且无上一行 | **外部退出**（`_Notification(1006)` = `NOTIFICATION_WM_CLOSE_REQUEST`，用户或 Steam）→ **数据被截断，不能用来判断强度**（实测被误读一轮） |
| 两者都无且进度停滞 | **未完成/疑似卡死**；按进度与进程证据分类，不能只凭看护结束就证明根因。查具名行：`CoopBots map: waiting — …` / `plan step refused: …` / `search slice overran: …` |

## 5. 先归档再分析

```bash
cp godot.log work/agent-state/data/<批次>/godot-mp-a10-NN.log
```

滚动中的 `godot.log` 会让**场次归属判错**（实测把一次拒绝错记到另一场，靠备份日志才纠正）。

## 6. 历史批次工具（不随社区克隆提供）

- `refusal-check.py <log>` —— 逐场 `refused/dropped/skipped/drift/complete` 计数 + 每次拒绝原文 + 归类 + **"每次丢弃是否都由具名拒绝引起"**（查静默杀剧本）。
- `analyze-mp-a10.py <log>` —— 逐场表 + 拒绝原文 + 搜索成本分布（wall/compute/nodes/alloc/gcPause）。
- `analyze-death.py <log>` —— 最后一战现场（血线、搜索、结尾 30 行）。

已有批次：`work/agent-state/data/mp-a10-2026-09-20/`（三轮完整日志 + 这些脚本 + `INDEX.md`）。

## 7. 完成判据

1. 每场"是否严格按剧本"的表；
2. 终止方式分类；
3. 到了第几层 / 是否通关；
4. 改了代码则按 `AGENT.md` §3 默认完整验证通过，实机包/部署双 DLL 与匹配的绑定工件一致；
5. **哨兵已删除**，机器已交还（手动模式）。

旧条款里的“普通 166 / 内核 94”保留为历史读数说明：94 对应当时聚焦内核子集，不能充当当前完整入口的门槛。当前判据见 AGENT.md §3，实际读数来自本次命令/退出码/日志；本轮不改写旧运行证据。
