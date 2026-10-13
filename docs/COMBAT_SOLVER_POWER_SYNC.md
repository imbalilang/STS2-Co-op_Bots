# CombatSolver 能力牌策略部分同步（solver-power-sync）

状态：**已通过源码集成验收，未部署**（2026-09-18）。

Owner 最终验收：`work/solver-power-sync/owner-acceptance.json`。75 项专项检查通过；在可用的 PowerShell 环境执行 `scripts/verify-project.ps1`，构建、类型加载、普通回归、内核回归四个阶段均退出 0，完整摘要见 `work/verification/20260918T121810223Z/summary.json`。普通/内核两轮分别记录 102/182 条 PASS（不是互不重复的测试总数）。此前 worker 环境的末尾哈希摘要问题不再阻塞本次验收。已核对最终代码与通过的专项测试哈希一致，12 份来源/移植哈希一致，旧 Vendor 引擎文件没有改动。

本文件记录本轮把 CombatSolver 0.41.0 的能力牌路线策略**部分回移**到本项目生产内核路径的实际范围、证据与边界。升级回归的固定流程见 [SOLVER_UPGRADE_REGRESSION.md](building/SOLVER_UPGRADE_REGRESSION.md)。

## 一句话结论

这是 **0.41.0 策略层在 0.33.9 引擎上的部分回移（partial strategy backport）**，不是整体升级：承诺记录/生命周期/席位配额/准入与六个卡池的逐卡路线政策按上游原文命名空间适配后落在 `Vendor/PowerSync/**`，由新的 `KernelPower.cs` 适配生产 `KernelTeamSearch`。它不改变终局评分。

## 实际落地内容

- `src/CoopBots.Kernel/Vendor/PowerSync/**`：合同、`PowerRouteAdmission`、`PowerCommitment` + 生命周期 + 席位配额 + 启动投资，以及 Ironclad / Silent / Defect / Regent / Necrobinder / Colorless 六份 `<Pool>PowerRoutePolicy.cs`（合计 104 张，逐池 19/17/20/18/18/12）。上游正文逐字保留，仅改命名空间。
- `src/CoopBots.Kernel/KernelPower.cs`：数据目录 + 按 `Player.NetId` 归属的不可变 `KernelPowerLedger` + 适配 `KernelSession` 的路线推进。准入只使用**该 owner 自己分支**上的可观测 `PersistentValue` 增量或即时格挡增益；未登记、上游 `NoInCombatCommitment`、或无可观测触发的能力不会创建承诺。
- `src/CoopBots.Kernel/KernelTeamSearch.cs`：承诺随出牌/药水/结束回合沿分支传递；frontier 在既有宽度内用上游 normal 席位配额保留代表，最高分普通线先受保护、至少一半普通席位保留、每个 owner 先占一席避免独占；共享节点只占一个席位并同时代表其上所有 owner，剩余席位再给其他有资格 owner；中间内存裁剪与最终 `SelectFrontier` 使用同一套保护；带承诺节点的去重键包含完整决策相关承诺状态（owner、family、cards、priority、opened turn/action/history、transition、last evidence、investment、remaining potential、progress/realized、power count）。`Score`、`Complete` 排序、取消/过期、药水/选择/结束回合语义不变。

## 必须明确的缺失（此前审计措辞“nothing production-relevant lost”不正确）

以下 0.41.0 生产内容**确实没有移植**，不能声称完整覆盖：

- 逐卡 `PowerCardValuation/Projection/*`（开牌投影、触发证据、Silent 伤害/防御/毒/刀投影）。
- `CombatSearchCoordinator.PowerRoutes.cs` 的完整单人开局能力前缀组合（每条路线 ≥25000 节点 / ≥10 秒）与真实结局比较；本轮没有 D4 反事实搜索。
- 0.41.0 的 Engine / `SimulatedCombatState` 增量与 `RootCombatTransformationPoolSnapshot`。
- 自动出牌历史检测（`PowerCardPlayOccurrence` 的 auto-play 语义）；生产搜索只能看到自己刚模拟的那一个动作。

因此，需要正投影的卡（如 `RequirePositiveProjection` 政策）在当前 vendor 下**保持旧行为**——不创建承诺、不保路；这是刻意选择，不是静默降级。通用战术进展只用于**释放**保护席位，是非因果信号，绝不进入评分。

## 验证与证据

- 聚焦验证：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-solver-power-sync.ps1`
  - 构建 `EnableKernelTests=true` 到 `work/solver-power-sync/<UTC>/build`，不部署、不打包。
  - 运行 `--kernel-power-only`（先装展示 shim，再跑 `KernelPowerRouteScenarios` 与既有内核回归）。
  - 记录 exit code、源码/上游游戏 DLL/输出哈希与耗时到 `verify-summary.json`。
- 生产行为回归：`tests/PatchSmoke/KernelPowerRouteScenarios.cs`
  - 同一 fixture 上 old control（`Options.EnablePowerRoutes=false`）与本轮路径对比：`DemonForm` 这类延迟兑现路线在旧路径于宽度内被剪掉、在新路径被保留并在同一预算内取得更高普通团队评分。日志同时给出 old/new 行动、分数与真实敌方掉血：old=`STRIKE,STRIKE,STRIKE,END,STRIKE,STRIKE` score=30 damage=30；new=`DEMON_FORM,END,STRIKE,STRIKE,STRIKE,STRIKE` score=51 damage=36 strength=3，即引擎真的把力量加到后续非能力动作上并提高了实际伤害，而不是只加一个合成分数。
  - 生产 `KernelCombatEvaluation` 案例：`Inflame` 铺垫 → 后续打击继承真实力量，评分不劣于旧路径。
  - 立即斩杀优先、无能力行为在同一 fixture 上选择路径与评分一致（这不是对全部代码“逐字节不变”的声明，只覆盖该 fixture 的观测结果）、无触发/未知能力不建承诺、两 bot 归属独立、纯生命周期/席位配额、死亡/消灭安全、中间裁剪保护、节点上限、取消/过期、`FinishAtBudget`。
  - 承诺去重签名覆盖完整决策相关状态：同 owner+family 但 transition/expiry 或 remaining potential 不同必须产生不同 key，完全相同的状态必须产生相同 key。
  - 席位公平：一个共享节点只占一席并代表其上所有 owner；3 owner / 2 保留席位 fixture 下第三 owner 的合格路线不会被漏掉。
  - 所有权准入负例：Accuracy 只有队友有 Shiv、Footwork 只有队友有格挡时不建路线；同一卡牌在 caster 自己拥有触发来源时建立路线（正例）。
  - 胜利终局：`after.HasWon` 分支在创建/推进前清空所有承诺，立即击杀不会新建或保留保护。
- 来源与哈希清单：`src/CoopBots.Kernel/POWER_SYNC.json`；基础引擎仍由 `src/CoopBots.Kernel/UPSTREAM.json` 诚实记录为 0.33.9。
- 本次通过证据：`work/solver-power-sync/20260918T121813.604554Z/verify-summary.json`（`success=true`，restore/build/run exit 0/0/0，`kernel-power-run.log` 含 `PASS: kernel power-route backport scenarios (75 focused checks).` 与既有内核回归全 PASS）。
- 全量集成最终结果以上方 Owner 验收为准；此前 worker 环境的摘要生成失败保留在 work/solver-power-sync/phase2-report.md 供追溯。


## 未执行 / 限制

- 未跑任何真实战斗、模型拟合、离线批量或可见 Steam 会话。
- 整体回归已通过；仍未做真实多人联机场景验收。
- 逐卡 `Projection/*`、完整前缀组合与自动出牌检测属后续独立任务范围。
