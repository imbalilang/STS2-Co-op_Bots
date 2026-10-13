---
name: coopbots-verify-gates
description: Prove a change is actually verified before claiming it is. Run production build, independent TypeLoadCheck, and one unfiltered EnableKernelTests=true PatchSmoke; optional -IncludeOrdinary is a separate diagnostic. Reuse immutable verified gate artifacts for packaging without rebuilding or rerunning smoke. Record commands, exit codes and actual log lines; preserve R1, R2 and historical four-gate evidence. Use when 要声明修好了、交付前、改完生产代码要跑回归、要证明读数没变、要给出验证证据、通过数对不上了.
---

# 验证门禁（R1 / R2 / 一次完整运行）

完整规范在 `AGENT.md` §2 铁律与 §3 验证机制。本文件是入口；冲突时以 `AGENT.md` 为准。

## 触发

- 你要说**"修好了"**；
- 改完任何生产代码；
- 交付前 / 交接前；
- 有人问"你怎么知道这是对的"。

## R1：不许声称未验证的事

> 「应该修好了」「大概是这个原因」**一律不算**。
> 要么给出证据（**命令 + 退出码 + 日志行**），要么明说**"未验证"**。

证据的最小形态是三件套：**跑的命令、退出码、日志里的原文**。
缺任何一件，就把它标成"未验证"，不要含糊过去。

## 当前默认完整验证

用户本轮已授权简化重复执行，优先于本 skill 的旧“四次物理执行”要求。`scripts/verify-project.ps1` 顺序执行生产构建、独立 TypeLoadCheck、一次 `EnableKernelTests=true` **无程序参数**的完整 PatchSmoke；原四门历史记录不能改写。

| 阶段 | 判据 |
|---|---|
| 生产构建 | 退出码 0、0 error，警告不超过冻结输入已核定的基线 |
| 独立 TypeLoadCheck | 内核不可解析时仍枚举壳全部类型，退出码 0 |
| 一次完整 PatchSmoke | `EnableKernelTests=true`，无 focused/case/packaging 过滤参数，退出码 0；完整最低计数线沿用 AGENT.md §3 |
| 可选独立普通诊断 | 只有显式 `-IncludeOrdinary` 才另外运行、记录实际结果；不作为默认重复步骤 |

完整主线保留普通场景，`CooperativeScenarios.Run` 的 `KERNEL_TESTS` 分支追加六入口，`KernelEngineScenarios` 内部继续调用场景；Program 的相关分支也有 focused flags。它们不是“普通与完整进程的 Harmony/presentation 隔离环境完全等价”的证明。

默认普通阶段记 **`covered-by-full`**，数值 PASS **`null`**，不得虚构独立普通通过数。**`full_total` 只取完整内核运行的实际 PASS 行数**。启用诊断时普通读数单独保存，不相加；完整失败也不能用覆盖标记宣布普通通过。`--kernel-power-only`、其它 focused flags 与 `--packaging-smoke` 均是子集。

计数的当前判据见 [`AGENT.md`](../../../AGENT.md) §3；具体命令与证据边界见 [`tests/README.md`](../../../tests/README.md)。读数只认 `work/verification/` 的实际日志/summary，必须同时给出命令和退出码，历史数值不保证当前工作树。

## 验证与打包入口

| 入口 | 用途 | 副作用 |
|---|---|---|
| `scripts/verify-project.ps1` | 默认完整验证一次；`-IncludeOrdinary` 可选独立诊断 | 新建 `work/verification/<运行标识>/`，保存真实日志和 summary，不覆盖旧 run、不出包 |
| `scripts/build.ps1 -VerifiedGateBinding <immutable binding> -Label <fresh label>` | 核对不可变验证绑定后直接打包门禁产物 | 核对实际门禁 0、当前源码/引用/包内文档与冻结输入、gate/build 与归档双 DLL；**不重构建、不重跑 smoke**；已有目录/ZIP 拒绝覆盖 |
| `scripts/build.ps1` 无绑定的 legacy 路径 | 备用构建/类型加载/smoke 子集出包 | 不提供完整验证证据；新本地包仍须全新 Label，不能覆盖发行物 |

```powershell
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File scripts/verify-project.ps1
python scripts/state.py gate
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File scripts/build.ps1 `
  -GameData '<actual game data>' -VerifiedGateBinding '<immutable-epoch-directory>/gate-run-binding.json' `
  -Label '<fresh-label>'
```

绑定由本次 `verify-project.ps1` 冻结输入并核对运行时工件后生成；旧 epoch runner 的绑定保持原样。复制后保存 ZIP 与包内双 DLL 哈希/验证收据；输入变化必须有匹配的新绑定，不得改写旧证据或靠单壳 DLL 的哈希认证内核。

**执行频率**：开发中运行本次改动对应的具名用例及必要实际 RED/GREEN；同一批候选改动完成、准备部署时才运行一次默认完整验证，聚焦结果不能冒充交付完整验证，验证成功且输入未变时同次交付复用该工件。独立普通环境需要诊断时再显式 opt-in。

## R2：新断言必须先证明它**会**变红

**故意改坏 → 确认红 → 恢复 → 再宣布修好。**

> 血泪：本 session 有三条断言写错了（分支错配、语义理解错、断言点本来就是对的），
> **全是靠改坏检查逼出来的。全绿的断言比没有断言更坏。**

细节与选样本的方法见 `.claude/skills/coopbots-invariant-regression-test/SKILL.md`（S2）。

## R5b / R5c：先证明你的指标活着

两类假证据，都量过：

**R5b · 恒为常数的指标不是证据。** 见到"永远 0 / 永远不变"的字段，先证明它**可达**。
`noScript` 曾不可达（"走完最后一步"被当成拒绝计数）→ 每场都是 0，读起来像"全程覆盖"。
同族已量过**五个**实例（详见 `coopbots-kernel-debug`）。

**R5c · 每一轮都出现的那行诊断，等于没人读的诊断。**
`COVERAGE BROKEN: ALL_FOR_ONE:…` 在**每一次**内核套件运行里都打印，连文件与行号都指对了，
却被当成背景噪音读过去了一整个 session —— 真相是那张牌**从来没能进过任何剧本**。

> **看 PASS 数之前，先读那些不是 PASS 的行**；恒常出现的红/黄字要么修掉、要么登记为"已知无害"。

## 不许自造"门"（R4）

上游没有的拒绝逻辑不要加；**只有「可执行性」可以拒绝**。
"我不想让这种情况发生"不是加拒绝的理由 —— 见 `.claude/skills/coopbots-deviation-register/SKILL.md`。

## 完成判据

1. 当前默认完整验证通过且绑定匹配（或明确列出哪个阶段没过、原文是什么）；
2. 改动涉及的断言有 **R2 变红原文**；
3. 证据已落盘：`python scripts/state.py gate` 记录门禁、`python scripts/state.py log "…"` 记录结论；
4. **明确区分**哪些是**实测**、哪些只是**构造推断**。
