---
name: coopbots-kernel-fixture-authoring
description: Build a minimal in-process fixture (ContinuationBoard, CreateCard, KernelSession.Capture().Fork(), direct KernelTeamSearch driving) so a mechanic that only ever appeared in a live game can be asserted on. Covers the reflection-scan style assertions that beat single instances, and why a fixture can lie to you. Use when 要为一个只在实机出现过的机制写断言、要建内核夹具、要驱动搜索验证一个假设、写扫描式断言.
---

# 把局内机制做成最小夹具（S8）

完整规范在 `AGENT.md` §5 **S8**。本文件是入口；冲突时以 `AGENT.md` 为准。

## 触发

要为一个**只在实机出现过**的机制写断言 —— 手边没有现成的构造入口。

## 现成夹具模式

都在 `tests/PatchSmoke/KernelEngineScenarios.cs`：

| 要什么 | 用什么 |
|---|---|
| 两席 + 一个 `MockAttackMonster`，手牌/抽牌堆已铺好 | `ContinuationBoard(seed)` → `(bot, other, combat)` |
| 造牌与摆位 | `combat.CreateCard<T>(player)`、`p.PlayerCombatState.Hand/DrawPile/DiscardPile.AddInternal(...)` |
| 单步推演 | `KernelSession.Capture(combat).Fork()` → `branch.Play(card, target, out reason)` / `branch.UsePotion(...)` / `branch.EndTurn(...)`；再用 `branch.Hp/Hand/Discard/Power<T>` 读结果 |
| 直接驱动搜索 | `new KernelTeamSearch(root, actors, s => <自定评分>, new(MaxRounds:…, IncludeEndTurns:true, MaxNodes:…))` + `Advance(...)`；读 `CompletedResult` / `CompletedState` / `HasRoute` |

## 扫描式断言优于单实例

用反射实例化**每一个**同类对象再问它，比钉死一个实例覆盖面大得多：

> `AuditPotionCoverage` 用反射遍历**每一个** potion 并问 `CanModelPotion`。
> 新机制优先加成这种扫描 —— 本 session 正是靠它发现"`BLAZE` 一直不可用"、
> "**九瓶**药水只挡了四瓶"。

## 夹具会骗你（R2 的价值）

**断言写完必须：改坏 → 确认红 → 恢复。** 本 session 两次翻车：

1. 有断言在改坏后**仍然绿** —— 因为把"两个实现都满足"的性质当成了区分点
   （"脚本 ≥6 动作"—— 短桩也可能很长；真正的区分点是**是否跨回合**）。
2. 一条"行为断言"一开始用 `SkillPotion`，**退回窄谓词也能通过** → 抓不到目标 bug；
   换成 `DropletOfPrecognition` 才有区分力。

## 探针不要顺手留成测试

探针（只观测、不改行为的最小实验）给出结论后，**若同一性质已被既有断言守住**，
新断言无法按 R2 自己变红 —— 此时**撤掉冗余断言**，只保留"该性质已有一道门"的结论。

## 完成判据

- 断言在**改坏后确实变红**，变红原文记进注释；
- 若既有断言已覆盖同一性质 → **撤掉冗余断言**，而不是留两份；
- 开发中完成本次对应具名用例及必要 R2；候选准备部署时按 `AGENT.md` §3 完成一次默认完整验证，不重复先跑普通进程。

## 相关

- 判断"该不该修这个机制" → `.claude/skills/coopbots-kernel-debug/SKILL.md`（S7）
- 断言写在哪、怎么补 → `.claude/skills/coopbots-invariant-regression-test/SKILL.md`（S2）
