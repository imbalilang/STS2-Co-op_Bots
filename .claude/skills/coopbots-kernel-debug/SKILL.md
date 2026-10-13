---
name: coopbots-kernel-debug
description: Localize defects in the CoopBots combat kernel/simulator — a script step being refused or the script dropped, a card or mechanic that never reaches a plan, a fight that mysteriously drags on, expected-vs-actual state divergence, or a metric that is suspiciously constant. Covers instrument-before-fixing, the five known lying-metric shapes, compare-like-with-like, the probe method for separating "objective missing a term" from "search tree truncated", reading search-complete, loading-DLL/version triage, production wiring assertions, and the R3 inventories of same-shaped call sites. Use when 剧本被拒/被丢、某个机制从不进剧本、战斗莫名拖长、expected/actual 分歧、某指标永远是 0、源码断言存在但实机没有按断言执行、疑似加载旧 DLL.
---

# CoopBots 内核（模拟器）缺陷定位

完整规范在 `AGENT.md` §5 **S7/S8**。本文件是入口；冲突时以 `AGENT.md` 为准。

需要区分局内策略、搜索预算与局外估值时，先读 [S3 决策归因方法](../coopbots-live-log-triage/references/decision-attribution.md)。重点检查收益模型、候选覆盖、完整续演、结果保留、运行期执行分别在哪一步失效。

## 0. 第一原则：**先让系统说话，再改逻辑**

本 session 每一次真正的突破都来自**先加一行仪器**，每一次走弯路都来自"读代码猜根因"。

| 现象 | 加什么 | 它给出的答案 |
|---|---|---|
| 商店永久卡死 | `shop: entered … stock=[…]` / `idle room=… isMerchant=…` | 驱动**走错分支**（`NetGameType.Host` 漏掉 Singleplayer） |
| 地图开着却不投票 | `map: waiting — <why>; generation=N` | `no-route`，**证伪**"陈旧票"这个首选嫌疑 |
| 拒绝只写 `no-card` / `playable` | 给**每个守卫**各自命名 | `round-drift` / `potion-not-held` … 才知道改哪一道 |
| 某场"莫名沉默 4 分钟" | `search slice overran: took=… rounds=… nodes=…` | 单次扩展超时（**不是**"没在搜"） |
| boss 战"机器人不杀、一直拖" | 指名 `PendingTurnStartChoiceDescription` | 阶段转换后留下的 pending choice |

**判据：每个 `return false` 的守卫都要能被单独指名。** 只回答"这一步是不是牌"（`no-card`）或"牌在手里且能指目标"（`playable`）是**描述**，不是**原因**。

### 0b. 先确认运行的是带修复的 DLL

源码断言存在、实机却违反断言时，先把“逻辑没跑”与“加载的是旧二进制”分开：

- 记录 `mods/CoopBots/CoopBots.dll`、`CoopBots.Kernel.dll` 的 mtime 和 sha256；不要用仓库源码时间代替。
- 在加载 DLL 里扫关键符号/参数名（例如 `requireElite`、`CanFinishWithElite`、`MaxMonsterShare`、`PotionPurchaseAllowed`），再和本地 build 的 DLL 对比。
- 本次实测：源码已有 `RoutePlanner.requireElite`，断言在 `OwnerPolicyScenarios` 全绿；但实机 DLL 里两个符号都不存在，根因是 04:31 旧包。此时修的是构建/部署，不是重写逻辑。
- 部署后再次扫同一组符号并比对包内/部署后的 sha256；部署成功但符号缺失，仍是旧包。

## 1. 恒为常数的指标不是证据（R5b 家族，已量过五个）

见到"永远 0 / 永远不变"的字段，**先证明它可达**：

1. `noScript` 曾不可达（"走完最后一步"被当成拒绝计数）→ 每场都是 0；
2. `plansWithRoute` 漏了一处每场重置 → `routes=` 分子全局累计、分母单场；
3. `routes=` 漏了两条部署路径的计数 → "以结束回合/以药水开局"的剧本**找到了解却不计**；
4. `CanModelPotion` 比它管的那道闸**更窄** → 报告与门不一致；
5. `COVERAGE BROKEN: ALL_FOR_ONE:…` **每轮都在打印**却没人读 —— 它指的正是"`case` 标签丢了，这张牌从没能进任何剧本"。

→ **看 PASS 数之前先读不是 PASS 的行**；恒常出现的红/黄字要么修掉、要么登记为"已知无害"。

## 2. 比对的两侧必须枚举同一个集合

实测 `ContinuationStamp`：实盘侧 `Creatures.SelectMany(c => c.Powers)`，模拟侧 `EffectivePowers()`（= hook listener 里的 power）→ 口径不同 → 投影分歧**每次捕获都报**、`drift` 恒亮，**把真分歧掩盖了**。

**任何 expected/actual 分歧，先问两侧是不是在数同一件东西。**

## 3. 探针法：把实机现象变成最小可判定实验

写一个**只观测、不改行为**的最小实验，让结论只能是你问的那一个。

- 例：怀疑"评分让机器人不敢赢" → 直接驱动 `KernelTeamSearch`，把**胜利**的评分人为设成灾难级（−1e6），读 `CompletedState.HasWon`。实测 `True` → **假设被否证**，注意力才转到"树被截断"。
- **探针给出"没问题"同样是答案**（本 session 两次靠它纠正自己的错误假设）。
- 探针**不要顺手留成测试**：若同一性质已被既有断言守住，新断言无法按 R2 自己变红（实测关掉 `Complete` 的终局优先时，既有 `P1-1: A complete nonlethal round must not displace an immediate lethal` 先变红）→ **撤掉冗余断言**，只留"该性质已有一道门"的结论。

**两种相反的根因会产生同一种外在表现**（"机器人不打、战斗拖着"）：目标函数缺项 vs 搜索树被截断。**不写探针分不开。**

## 4. 读 `search-complete`

字段：`wall` / `compute` / `gcPause` / `nodes` / `alloc` / `stop` / `turnPrefix` / `crossesTurn` / `route` / `actions`。

- `stop=frontier-empty` = 当前保留的前沿耗尽；检查候选、剪枝与边界，不能据此证明战斗无解。`stop=time-budget|node-budget` 表示预算触顶，还要看多少完整终局真正参与比较。
- 先核查当前版本的计时采集点和线程口径，再比较 wall/compute/GC；帧间等待、并行累计与嵌套阶段不能混算，不固定假设 wall 与 compute 的比例。
- 判"还能不能加深/加席"：看 `nodes` 与 `wall` **对预算的占用**。实测单人 A0 只用 3–4%；**4 席 A10 已到 `nodes` 90%+ / `wall` 97%**（几乎无法延长）。

## 5. "未建模 = boundary" 的代价分两种，先分清

- 跳过**一张牌/一瓶药水**：只损失那件东西的价值（可接受，登记即可）。
- 截断**时间轴**（回合边界的 pending choice、未建模的敌方招式）：搜索**看不到地平线之后**，会退化成"只求活过这几回合"。
  实测 `boundaries=end-turn:round-pending-choice×749` 就把一场 25 回合的 boss 战变成"不杀只防、一直拖"。
  **时间级优先修。**

## 6. R3：改一处前先 grep 这些同形点

- 每场必须重置的计数器：`plans / plansWithRoute / planRefusals / planTicksWithoutScript / seatStepsSkipped / planDriftDetected / planSentinelPending / planSentinelMissing`；
- **三条部署路径**（结束回合开局 / 药水开局 / 卡牌开局）都必须计数并设置 `LastExecuted`；
- `NetGameType.Host` 的**所有**在局内站点（`RunAuthority` 已把"谁是这个局的出牌方"收成一处；"要广播给远端"是**另一问**，保持 Host-only）；
- "需要选择"的药水是**九瓶**（`PotionChoiceSupport.RequiresChoice`），别只挡四瓶；
- "这个房间当前 act 造得出来吗"（`RoutePlanner.IsBuildable`）。

## 7. 写断言的夹具模式（细节见 S8）

`KernelEngineScenarios.cs` 里现成：`ContinuationBoard(seed)`、`combat.CreateCard<T>(player)`、`KernelSession.Capture(combat).Fork()` + `branch.Play/UsePotion/EndTurn`、直接用 `new KernelTeamSearch(root, actors, s => <自定评分>, new(MaxRounds:…, IncludeEndTurns:true))`。**扫描式断言**（`AuditPotionCoverage` 用反射遍历每个 potion）优于单实例。

**夹具会骗你**：断言写完必须**改坏 → 确认红 → 恢复**。本 session 有断言改坏后仍绿 —— 因为把"两个实现都满足"的性质当成了区分点；也有行为断言用了**旧谓词下也能通过**的样本（`SkillPotion`），换掉才有区分力。

**生产接线也要断言。** 直接驱动 `KernelRollout.Run` 的 green 不能证明生产 `KernelJointTurnRun` / `KernelTournament` 传了同样选项。实测 `SequenceLookahead` 的直接行为桩在把生产 wiring 改回 `jobIndex >= paths.Count` 后仍然 green；改用一个反射断言——取 `KernelJointTurnRun.continuation`，再取 `RolloutRun.o.SequenceLookahead`，确认第一个 continuation 为 true——才真正变红。行为绿 + 接线红时，别宣称“已修复生产路径”。

## 8. 完成判据

1. 现象被一个**具名守卫**解释（不是"某个地方"）；
2. 修复附 **R2 变红原文**（把变红原文记进注释）；
3. 按 `AGENT.md` §3 完成生产构建、独立类型加载和一次无过滤完整 PatchSmoke；开发中仅运行对应具名用例及必要 R2。旧普通 166 / 内核 94 为历史读数，94 是当时的聚焦子集；
4. **明确区分**哪些是实测、哪些只是构造推断。
