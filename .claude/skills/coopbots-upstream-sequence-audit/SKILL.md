---
name: coopbots-upstream-sequence-audit
description: Audit a CoopBots method against its CombatSolver upstream counterpart line by line to find omissions, when you suspect our behavior differs from upstream, when a change touches a sequence that mirrors upstream (turn begin/end, card replay, potion replay, every prefix gate), or when something silently deviates. Covers side-by-side diffing, the equivalence/intentional-deviation/omission classification, and the S6 registration step. Use when 怀疑"我们和上游行为不一致"、改动涉及镜像上游的序列、要找遗漏、某个地方悄悄偏离上游.
---

# 上游序列审计（S1）

完整规范在 `AGENT.md` §5 **S1**。本文件是入口；冲突时以 `AGENT.md` 为准。

## 触发

- 怀疑"我们和上游行为不一致"；
- 改动涉及**镜像上游序列**的方法；
- 修完一处"不报错、只悄悄偏离"的 bug 之后，想知道同形的地方还有没有。

## R6：抄就要逐行对照，不能凭记忆

> **靠回忆审计，只能找到你记得要找的东西 —— 而"遗漏"的定义就是你想不到它存在。**

所以唯一允许的做法是**并排读**：在**同一次工具调用里**读「我们的方法」和「上游对应方法」，
然后逐行列出差量。凭记忆列出"我记得改过这几处"不算审计。

## 步骤

1. **找到对照物的两侧。** 上游源码在 `src/CoopBots.Kernel/Vendor/`（生产内核，
   来源 `UPSTREAM.json`）；参考副本在 `third_party/`。路径有疑问查 `docs/PROJECT_MAP.md` §3。
2. **并排读。** 同一个消息里读两边，不要分两次。
3. **逐行列差量表**，每一处差量判定为三者之一：

   | 判定 | 处理 |
   |---|---|
   | **等价** | 写法不同、行为相同。写清楚**为什么等价**，别只写"等价"。 |
   | **有意偏离** | 必须能在 `AGENT.md` §6 找到出处（谁要求的 / 为什么）。找不到 = 遗漏。 |
   | **遗漏** | 这就是要找的东西。补上，并登记。 |

4. **优先对照对**（这几处历史翻车最多）：
   - 回合开始 / 回合结束
   - 出牌重放
   - 用药重放
   - **所有 prefix 拦截**

## 完成判据

**差量表为空，或每一处差量都有登记（见 S6）。**

不许出现"看起来差不多"。凡是"差不多"，要么写出等价理由，要么登记为偏离。

## 已知的坑

- **比对的两侧必须枚举同一个集合。** 实测 `ContinuationStamp`：实盘侧用
  `Creatures.SelectMany(c => c.Powers)`、模拟侧用 `EffectivePowers()` → 口径不同 →
  **每次捕获都报分歧**，`drift` 恒亮，把真正的分歧掩盖了。
  任何 expected/actual 分歧，**先问两侧是不是在数同一件东西**。
- 上游**没有对照物**的方法（我们自造的），不能靠"没找到差异"过关 —— 必须登记为偏离。
