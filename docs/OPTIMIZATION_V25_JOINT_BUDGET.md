# V25：联合搜索预算调度与上一版实机归档

2026-09-29。承接 `docs/OPTIMIZATION_V21_TEST_RESULTS.md` 的「后续修复重点 #1（重做后续推演的预算调度）」。
本轮改动源码、补真实复杂度回归，并跑了一次实机确认调度生效（见 §5）。

## 1. 上一版实机结果归档（补 V24 之后的败局）

`docs/OPTIMIZATION_V24_DECISION_QUALITY.md` 之后还跑过一局，此前只落在 `work/` 里，本轮补进文档。

- 包：`outputs/CoopBots-v0.39.1-manual-v24-20260929-000657.zip`（`work/package-v24-current.json`）。
  部署后 `mods/CoopBots/` 两个 DLL 的 SHA256 与包内一致（`CoopBots.dll=328996EF…`、`CoopBots.Kernel.dll=A63BA4FA…`），
  因此这一局**确为 V24 算法**。
- 局面：种子 **`9F4Q3R2BV8HQ`**，A10，`was_abandoned=false`，`killed_by_encounter=ENCOUNTER.DECIMILLIPEDE_ELITE`，
  第三幕 `ACT.GLORY`，`run_time=891s`。**失败**。
- 证据：`work/defeat-v24-20260929-003058/`（`godot.log`、原生 `1790611982.run`、`analysis.json`、`final-fight.json`）。
  **注意口径**：这份日志没有 `How did the game end??` 也没有旧式 `combat summary`（V23 已记录该格式差异），
  终局判定取自原生 `.run` 记录，不是日志行。
- 执行侧：13 场战斗，`refusals=0`、`script step refused=0`、`plan step drift=0`、`prediction-exception=0`、
  `COVERAGE BROKEN` 为空。**执行不是这一局的败因**。
- 营火：把同一批 24 个营火输入分别喂给已发布 0.39.1 算法与 V24 算法（`comparison-provenance.json`，离线、不部署游戏），
  实机 24/24 与 V24 一致；V24 与发布版有 **12 处选择不同**。详细差异见 `camp-comparison.json`。

### 这一局暴露的真实症状：联合搜索里 Tactical 续演被系统性切掉

42 次联合搜索里：

- `selected=True` 10 次；有候选（`prefixes>0`）36/37 次 —— 与 V21 当时「几乎不产候选」已经不是同一状态。
- 但 **`Tactical:` 只出现 4 次**：其余 38 次的完成策略是 `Kill/Growth/Cleanup/Defend`，`budgetCutoffs=4~5`。

Tactical 是最贵也最贴位置的续演策略（每步至多模拟 8 个牌态/目标），单任务时间片对它永远不够，于是**每次都被切掉**。

## 2. 改动：参照路线优先的预算调度

文件：`src/CoopBots.Kernel/KernelJointTurnRun.cs`。

旧行为：前缀搜索拿 55% 后，剩下 45% 按 `paths.Count` 平摊成单任务上限
（`Math.Max(15, budgetMs*.45/paths.Count)`）。8 条路径 × 5 种策略 = 40 个任务，实际只能试到十来个，
且最贵的 Tactical 必然超时。日志诊断里 Tactical 缺失就是这个原因。

新行为：

1. **第 0 个任务 = 参照路线**（最佳前缀 × Tactical），只用**总预算**封顶，不受单任务上限约束；
   只要总预算还够，就必然产出一条被完整评估的路线。
2. 其余任务：按**打算比较的打法数**（五种策略，`Policies.Length`）分摊启动时剩余的预算，
   而不是按 `paths × policies` 的四十份。分母变了，每条才有能跑完的份额。
3. 单项超时仍是具名 `continuation-budget` cutoff，**不当终局**（`Accept` 不收，`budgetCutoffs` 计数）。
4. 诊断新增 `reference=<stop|pending>`，让"参照路线是被跑完还是被切掉"可被读出（S7：先让系统说话）。

上游 CombatSolver 没有联机联合搜索，这一条按 S6 登记在 `AGENT.md` §6。

## 3. 回归（V21 后续修复重点 #2）

新增夹具：`tests/PatchSmoke/JointDiversityScenarios.cs` 的 stress 段。
四席、每席 8 张牌、两个 1500 HP 的厚血敌人，使地平线产出 **8 条续演路径**，与生产「high」半个预算（1600 ms）一致。

新代码：

```
PROBE stress stop=node-budget paths=7/8
  policies=[Tactical:1,Kill:3,Growth:3,Cleanup:1,Defend:1] budgetCutoffs=9
  replayFailures=0 reference=no-actor:enemy-phase-complete boundaries=[]
```

断言：参照路线必须跑完（`reference` 既非 `pending` 也非 `continuation-budget`）、至少产出一个候选，
且**保证参照路线不得饿死其它打法**（至少还有一种策略完成）。脚本逐动作原生重放，且不扰动共享 root。

**R2 红原文**（把 `JobCapMs` 还原成旧式 `Math.Max(15, budgetMs*.45/paths.Count)` 后实跑）：

```
PROBE stress stop=node-budget paths=8/8
  policies=[Kill:4,Growth:5,Cleanup:3,Defend:2] budgetCutoffs=4
  replayFailures=0 reference=continuation-budget boundaries=[]   (Tactical:0)
System.Exception: Joint search must name a reference route that finished,
  not one the per-job share cut off.
   at JointDiversityScenarios.Run()
```

即旧式摊分下 **Tactical:0**，与实机 38/42 次的形状完全一致；恢复后转绿。

## 4. 四道门

命令：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出 **0**。
证据：`work/verification/20260928T165831112Z/`。

| 门 | 实测 |
|---|---|
| Release 构建 | 0 error、**6 warnings（原基线，无新增）** |
| TypeLoadCheck | 退出 0 |
| 普通 PatchSmoke | **256 PASS / 0 FAIL** |
| 完整内核套件 | **417 PASS / 0 FAIL** |

覆盖扫描未变：`total=598 modeled=553 unmodeled=7 choice=10 broken=0 skipped=28`；`COVERAGE BROKEN:` 后为空。

## 5. 实机确认（本轮唯一一次实机，仅验调度）

包：`outputs/CoopBots-v0.39.1-v25-joint-budget.zip`（`CoopBots.dll` SHA256 `636E19CC…`，zip `A278A6A2…`）。
经 `run-live-loop.ps1 -Seed 9F4Q3R2BV8HQ -Perf high -Ascension 10` 部署到 `mods/CoopBots` 并经 Steam 启动。
证据：`work/v25-joint-budget-20260929/`。开局搜索与战斗运行正常，四个席位从未卡死。

跑完整局并在 `How did the game end??` 正常收尾（非外部退出、非看护器杀）。
`CoopBots live test: run outcome → victory=false act=1 floor=29`：**第二幕第 29 层精英团灭**。
同种子、`high` 档，对照上一轮 defeat-v24 的日志：

| 读数 | defeat-v24（旧摊分） | V25（参照路线优先） |
|---|---:|---:|
| 联合搜索结果行 | 42 | 47 |
| **含 `Tactical:` 的次数** | **4** | **47** |
| `Tactical:0`（被切掉） | 38 | **0** |
| `reference=` | 无此字段 | `victory`/`wipe`，**0 次 `continuation-budget`** |
| `prefixes` 中位数 | — | 7 |

即：最贵也最贴位置的 Tactical 续演从"38/42 次被切"变成"**47/47 次跑完**"，且参照路线**没有一次**是被预算截断的。
其余较便宜的策略仍会被切（`budgetCutoffs` 中位数 8）——那是设计内的：先保证一条完整参照路线，再用剩余预算比较其它打法。

**强度上不作结论。** 同一颗种子，v24 打到第三幕、v25 死在第二幕第 29 层，但两者的决策不同 →
路线、房间、抽牌全部分叉，这不是受控对照；单局也不是样本。执行侧好的那一面是：
`plan step refused=0`、`script step refused=0`、`plan step drift=0`、`prediction-exception=0`、`COVERAGE BROKEN` 为空。

## 6. 尚未验证与下一步

- 本节第 5 条的实机**只证明调度生效**（`Tactical` 不再被切、参照路线跑完）。
  **不能**据此声称 A10 强度提升；该局的胜负另行记录，且单局不是样本。
- 待验：按 V21 的四场最小复测（原/调整卡组各两场，`A10NEW92701` 沙漏战前存档）判断收益，
  再决定六局配对 A10。
- 本轮**没有**动 V21 后续重点 #3/#4（重放 B1 第 9 回合结束决策、构筑因果边界），
  也没有动 B10（药水选择通路）与 B11（两阶段 boss 的 pending choice 截断）。
- V21 的其余未完成项（B1–B11）仍按 `PLAN.md` §3 跟踪。
