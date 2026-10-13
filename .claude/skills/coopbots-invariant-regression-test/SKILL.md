---
name: coopbots-invariant-regression-test
description: Add a regression assertion for a bug that failed silently, and prove the assertion is alive by deliberately breaking the code, confirming it goes red, restoring, then confirming green. Covers the R2 mutate-and-verify discipline, the RunContinuationInvariants home, choosing a discriminating sample, production wiring assertions via reflection, dropping redundant assertions, and treating a mutation that stays green as a disproved hypothesis. Use when 修完一个"不报错、只悄悄偏离"的 bug、要给一处静默失败加断言、写新断言、断言改坏后仍然绿色、生产入口没接线.
---

# 不变量补测（S2）

完整规范在 `AGENT.md` §5 **S2** 与 §2 **R2**。本文件是入口；冲突时以 `AGENT.md` 为准。

## 触发

修完一个**"不报错、只悄悄偏离"**的 bug —— 也就是那种没有异常、没有拒绝、
只是行为慢慢不对的缺陷。这类 bug 唯一能被长期守住的办法是补一条不变量断言。

## R2 是硬要求：先证明断言**会**变红

> **全绿的断言比没有断言更坏。**
>
> 血泪：本 session 有三条断言写错了（分支错配、语义理解错、断言点本来就是对的），
> **全是靠改坏检查逼出来的。**

所以顺序固定，不许跳：

1. 写出断言；
2. **故意改坏**被测代码；
3. 跑一次，**确认它变红**；
4. 恢复代码；
5. 再跑一次，确认变绿；
6. **把变红的原文记进断言注释**。

没有第 3 步的证据，这条断言就不算写完。

**改坏后仍然 green，先否证假设，不要改样本硬凑。** 本次复盘先怀疑 `KernelShortVictoryRun` 的队伍-only stamp，写了一条“敌方 HP 变化必须让 stamp 失效”的断言；把 stamp 改回 party-only 后它仍然 green。原因是 `CaptureLivePartyText` 本来就随敌人 HP 变。结论是：stamp 不是根因，真正的缺项是 `WorthChecking` 没把已施加的毒伤算进斩杀门槛。于是撤掉 stamp 断言，改成 poison gate 断言（把 `TwoTicks` 改成 0 即变红）。**绿掉的错误假设留在测试里，比没有测试更坏。**

## 放在哪

- 续跑/剧本不变量 → `RunContinuationInvariants`（见 `tests/PatchSmoke/KernelEngineScenarios.cs`）；
- 机制级断言 → 见 **S8**（夹具模式）。

## 选样本：断言要有区分力

**"两个实现都满足"的性质不是区分点。** 实测翻车：

- 拿"脚本 ≥6 动作"当区分点 —— 短桩也可能很长；真正的区分点是**是否跨回合**。
- 一条"行为断言"用 `SkillPotion` 做样本 —— 退回窄谓词也能通过，**抓不到目标 bug**；
  换成 `DropletOfPrecognition` 才有区分力。

**判据**：把 bug 改回去，这条断言**必须**失败。做不到就换样本。

### 接线断言：直接驱动不够，要断言生产入口真的传了选项

机制 helper 的 R2 绿，不等于生产路径按同一选项运行。实测把 `KernelJointTurnRun` 的 `SequenceLookahead` 改回关闭：

- 直接跑 `KernelRollout.Run` 的顺序断言仍然 green；
- 要反射取 `KernelJointTurnRun.continuation`（`private` 字段）→ `RolloutRun.o.SequenceLookahead`，断言第一个 continuation 为 true，才会真正变红。

当“行为正常但生产没接线”时，反射接线断言是允许的，不是作弊：被测对象就是生产 wiring 本身。

## 别留冗余断言

若同一性质**已被既有断言守住**，新断言无法按 R2 自己变红 —— 此时**撤掉冗余断言**，
只留下"该性质已有一道门"的结论。

> 实测：把 `Complete` 的终局优先关掉时，既有断言
> `P1-1: A complete nonlethal round must not displace an immediate lethal` 先变红。
> 结论是"不用再加一条"。

## 完成判据

**断言在改坏后确实变红，变红原文记进注释。** 并且门 3 + 门 4 全绿。

## 外溢检查（R8）

改完先看有没有把别的行为带走。换成"更正确"的实现会翻掉旧断言 ——
那些断言是**旧政策的守卫**，翻转它们是决策，不是修 bug。
