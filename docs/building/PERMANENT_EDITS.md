# 永久牌组变更启发式修复（build-permanent-edits）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录首轮生产启发式修复：`HEURISTIC_REVIEW.md` 顶部 P0 的三项落地——删牌保护的结构化查询、多张移除的逐步重评、变形使用完整牌组上下文。**不改任何权重**，不改升级路径，不扩大可选屏语义，也不触碰真实牌组。

## 状态（2026-09-18，DeepSeek 实现，Astra 已验收）

- 可写文件：`src/CoopBots/BotBrain.cs`、`src/CoopBots/Building/BuildValue.cs`、`tests/PatchSmoke/BuildValueScenarios.cs`、`tests/PatchSmoke/Program.cs`、`scripts/verify-building-edits.ps1`、本文件。
- 快照：`work/building-edits/before/`（编辑前原文件，脚本只在首次运行时建立，后续运行不覆盖）。
- 校验脚本：`scripts/verify-building-edits.ps1`，运行 `PatchSmoke.dll --building-edits-only`，输出隔离在 `work/building-edits/<UTC>/`。
- 预算：离线构建与合成模型测试，无战斗、无训练、无云、无部署、无提交。

## 行为变化

1. **结构化删牌保护**（`BuildValue.RemovalProtection`）。原先私有的 `Blocking` 改为返回 `RemovalGuard(Protected, Role)` 的 `internal static` 查询（role ∈ `{block, aoe, energy, draw}`）。`BuildValue.Remove` 仍对受保护卡返回 `Valuation(0, "protected:<role>")`，商店调用方（`BotShopPlanner.RemovalValue`、`RestSitePatches`）语义不变；调用方不再需要解析 reason 字符串。

2. **移除优先不受保护候选**（`BotBrain.RemoveFromDeck`）。删牌入口不再按单一分数排序取前 N。每一步：以**完整实际牌组副本**为上下文，对候选分别计算 `BuildValue.Remove` 与 `RemovalProtection`；先在**未受保护**候选中按降序分数选择，只有当未受保护候选耗尽时才退回受保护池（记录 `purpose:fallback-protected` 显式日志）。因此即使未受保护候选的删牌分为负，也不会去删受保护卡。

3. **多张移除逐步重评**。每选走一张，只从本地的**假设牌组副本**中按引用移除该实例，然后对剩余候选重新评估。真实牌组从不被修改（测试逐索引断言引用与顺序不变）。当某功能只剩一张时，它会在后续步骤中变为受保护，从而不会在还有其它选择时被删掉。并列时使用 `Id.Entry` 升序，再以候选原始顺序兜底；相同副本因此确定性选择。

4. **强制数量兜底**。`min/max/maySkip` 的数量语义沿用 `SelectCards` 现有计算，不改动。当强制数量无法在未受保护候选中凑齐时，仍返回**恰好 count 张互不相同**的卡（受保护池内确定性取分最高者），不出现不足数量、重复或卡屏。

5. **变形使用完整牌组上下文**（`BotBrain.Permanent`，`ForTransformation` 分支）。评分上下文由「候选列表」改为 `player.Deck.Cards` 的副本，其余语义（取边际最低者、`worst=true`、升级路径）不变，且不为变形引入新保护。

## 入口级回归（`BuildValueScenarios.Run`，真实游戏模型）

使用 `BotBrain.SelectCards`，全部在 `--building-edits-only` 下运行，并保留既有全部场景：

1. 受保护 card0 对比**负分**未受保护卡（`Inflame`）：必须选未受保护卡。同时断言受保护卡 `Remove` 总分为 0、未受保护卡 `< 0`，确保用例真的命中失败条件。
2. 两张移除 + 两张 Defend：不得把最后一张 block 一起删掉，且返回两张不同实例（旧实现取前二会删两张 Defend）。
3. 全受保护强制屏：断言三张候选 `Remove` 总分为 0，`SelectCards(..., 2, 2, ...)` 仍返回 2 张不同卡。
4. 变形子集反例：`SevenStars` 在仅候选上下文里因无 Star 生产者被判 0，在含 `Glow` 的完整牌组里可支付；断言完整牌组排序与子集排序确实不同，且 `SelectCards` 选完整牌组的边际最低者（旧实现选子集最低者）。
5. 真实牌组不变 + 重复选择确定性：删牌前后逐索引引用/顺序一致，同参数两次调用返回相同实例序列。

## 实测证据

失败前（红）：`work/building-edits/20260917T194044.333355Z/`，`--building-edits-only` 退出码 `-1`，`logs/building-edits-run.log`：

```
System.Exception: An unprotected candidate must win over a protected card even when its score is negative, got DEFEND_IRONCLAD.
```

修复后（绿）：`work/building-edits/20260917T194104.415600Z/`，restore/build/run 退出码均为 `0`，`success: true`。`logs/building-edits-run.log` 共 37 行 PASS（含既有场景与新增 5 组）：既有 32 行全部保留，新增：

```
PASS: removal prefers an unprotected negative target over a protected card.
PASS: two removals keep a block card back (DEFEND_IRONCLAD,STRIKE_IRONCLAD).
PASS: an all-protected forced removal still returns 2 distinct cards (BREAKTHROUGH,DEFEND_IRONCLAD).
PASS: transformation scores candidates against the full deck, so the subset counterexample is avoided.
PASS: removal never touches the live deck and repeats deterministically.
```

产物哈希（同上 `verify-summary.json`）：`PatchSmoke.dll 4c0f4353…`、`CoopBots.dll 27152dea…`、`sts2.dll 0861bfa1…`。源文件前后哈希（`beforeHashes` → `sourceHashes`）均记录在案。

复现命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-building-edits.ps1
```

## 限制（不得省略）

- 仅**离线合成**构建与真实模型入口测试；未在真实跑局或多人联机中验证。
- 只覆盖 `BotBrain.SelectCards` 的 `FromDeckForRemoval` / `FromDeckForTransformation` 入口；未覆盖实机事件/商店回调差异。
- 强制兜底后的选择仍可能删除受保护卡，这是屏幕强制数量下的既定行为，日志标记为 `:fallback-protected`；本任务不改变 `min/max/maySkip` 语义。
- 未部署、未调用战斗服务、未复制到 `outputs/`/`releases/`，未运行战斗批。
- 权重、软提示放大（`HEURISTIC_REVIEW` P1）与其余 P1/P2 项不在本任务范围。
