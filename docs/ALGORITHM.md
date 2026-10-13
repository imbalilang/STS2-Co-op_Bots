# CoopBots 算法与代码入口

这是当前开发快照的导航，不是完整覆盖或最优打法保证。[当前实测状态](STATUS.md)区分原生正确性、固定预算下的打法质量与完整爬塔胜率。项目采用 Combat Solver 的模拟基础及本地多人适配；具体来源与修改见 [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) 和 [VENDOR_DEVIATIONS.md](../src/CoopBots.Kernel/VENDOR_DEVIATIONS.md)。

## 从实盘到动作

```mermaid
flowchart LR
    L[已结算的实际盘面] --> C[捕获原生状态与控制席位]
    C --> R[一份搜索请求与绝对截止]
    R --> S[原生模拟上的有界候选比较]
    S --> V[原生重放与状态证据]
    V --> A[出牌 / 药水 / 选牌 / 结束回合]
    A --> L
    H[真人已结算输入] --> L
```

宿主的 [`BotRuntime.cs`](../src/CoopBots/BotRuntime.cs) 和 [`KernelCombatPlanner.cs`](../src/CoopBots/KernelCombatPlanner.cs) 调度决策，[`KernelCombatCapture.cs`](../src/CoopBots/KernelCombatCapture.cs) 把实盘交给内核。游戏线程负责读实盘和部署动作；后台工作处理捕获后的模拟状态。

全 Bot 模式可搜索并部署跨回合的联合剧本。每步仍要对应原生可执行动作与实际状态，不能把一份预测胜利当成实盘已经获胜。搜索可以得到真实终局，也可以在预算或覆盖边界处截断；两者的证据类型不同。

## 模拟与状态一致性

| 入口 | 职责 |
|---|---|
| [`KernelSession.cs`](../src/CoopBots.Kernel/KernelSession.cs) 及 partial 文件 | 模拟根、Fork、行动事务与续演 |
| [`Vendor/`](../src/CoopBots.Kernel/Vendor/) | Combat Solver 来源的模拟、预测与搜索实现；不应手工混入不明版本 |
| [`KernelPlanningState.cs`](../src/CoopBots.Kernel/KernelPlanningState.cs)、[`KernelPlanningState.Facts.cs`](../src/CoopBots.Kernel/KernelPlanningState.Facts.cs) | 用于计划续用的完整已支持状态和机制 Facts |
| [`KernelTurns.cs`](../src/CoopBots.Kernel/KernelTurns.cs)、[`KernelPartySetup.cs`](../src/CoopBots.Kernel/KernelPartySetup.cs) | 多人回合、同步选牌、共享回合开始屏障和待决事务 |
| [`LiveEndTurnRisk.cs`](../src/CoopBots.Kernel/LiveEndTurnRisk.cs) | 从实际根模拟结束回合后的风险，不等于整个战斗求解 |

多人正确性还包括席位控制、同步选择的 owner、死亡席位、实体顺序、共享生成/洗牌 RNG、原生历史和跨回合事务。复制每个人的手牌和 HP 不足以构造等价根。

计划复用的失败可能来自未知事实、物理实例或合同不一致；它本身不证明游戏规则漂移。实机与原生预测的具名差异则按 `AGENT.md` R9 处理：保留现场、最小原生复现、修复、RED/GREEN。不要把全局忽略字段、只比 HP 或继续执行漂移剧本当成优化。

`v21` 的回合开始卡死修复在 `KernelPartySetup.ResumePreparedPartySetup`：没有实际预选菜单可重捕获时，未回答的原生请求返回既有 pending 路径并保留可续演帧，避免不断清掉请求后重跑同一未回答菜单。已有预选菜单因抽牌而失效的重捕获路径和严格重放仍保留。对应具名测试见 `tests/PatchSmoke/PartySetupPendingNativeScenarios.cs` 与 [tests/README.md](../tests/README.md)。这项修复不证明所有卡死均已消除。

## 搜索、牌序和预算

| 入口 | 适合改什么 |
|---|---|
| [`KernelTournament.cs`](../src/CoopBots.Kernel/KernelTournament.cs)、[`KernelRollout.cs`](../src/CoopBots.Kernel/KernelRollout.cs) | 有限策略候选、原生续演和终局比较 |
| [`KernelTeamSearch.cs`](../src/CoopBots.Kernel/KernelTeamSearch.cs)、[`KernelJointTurnRun.cs`](../src/CoopBots.Kernel/KernelJointTurnRun.cs) | 四席行动顺序、联合前缀和后续策略 |
| [`KernelTacticalPolicy.cs`](../src/CoopBots.Kernel/KernelTacticalPolicy.cs)、[`KernelSequenceOrder.cs`](../src/CoopBots.Kernel/KernelSequenceOrder.cs) | 可支付的牌序、资源机会成本、启动与防御的局部排序 |
| [`KernelDecisionRequest.cs`](../src/CoopBots.Kernel/KernelDecisionRequest.cs) | 已结算根的代次、绝对截止、取消和并发计数 |
| [`KernelSearchBudgetScope.cs`](../src/CoopBots.Kernel/KernelSearchBudgetScope.cs)、[`KernelSearchWorkPool.cs`](../src/CoopBots.Kernel/KernelSearchWorkPool.cs) | 子搜索/原生异步尾部继承同一请求及有界工作池 |
| [`KernelCombatPlanner.Refinement.cs`](../src/CoopBots/KernelCombatPlanner.Refinement.cs)、[`KernelCombatPlanner.VictoryReview.cs`](../src/CoopBots/KernelCombatPlanner.VictoryReview.cs) | 已有路线的再评估、边界检查和胜利证据 |

核心限制是**总请求的绝对截止**。参考路线、牌序、选牌、联合搜索和药水候选使用该请求余额，不应为每个候选新增完整预算。取消使旧根停止调度；已开始的原生事务仍需安全结算。软切片不保证每次原子行动或 GC 都在该时间内返回，因此必须实测墙钟、尾耗时和分配。

最新基准使用 High 档及局内并发上限 6；其他档位、构筑任务预算和 Boss 加预算机制各有配置。对照时要记录实际配置及请求上限，不能只写“High”。提高并发、延长时间、切换硬件后的胜率不能直接归因为算法改善。

一个有用的牌序改进示例是“可支付的免费启动牌是否在其触发动作之前被找到”；看起来晚打的牌也可能由抽牌、限费、目标变化或原生副作用造成。用同一入口重放旧新路线才能量化可避免的战损。

## 消耗、弃牌和药水的选牌

[`KernelChoices.cs`](../src/CoopBots.Kernel/KernelChoices.cs)、[`KernelChoiceContinuation.cs`](../src/CoopBots.Kernel/KernelChoiceContinuation.cs)、[`KernelRelicChoices.cs`](../src/CoopBots.Kernel/KernelRelicChoices.cs) 与 [`KernelTurnStartChoices.cs`](../src/CoopBots.Kernel/KernelTurnStartChoices.cs) 保存菜单、分支和续演合同；[`BotChoicePlanSync.cs`](../src/CoopBots/BotChoicePlanSync.cs) 在实际选择出现时部署合同。

选择的价值不能只看当前面板伤害：

- 消耗会永久移出本场循环，应考虑核心牌后续价值与实际消耗收益。当前回合付不起费用，不应因此先烧核心牌而保留普通打击/防御。
- 弃牌通常只影响本轮与重洗前的可用窗口，还要算弃牌触发、后续抽牌、能量和格挡。
- 嵌套选择和“弃牌再抽牌”会改变下一张实际菜单。不能将采样菜单的手牌 token 原样套用到不同盘面。
- 有限预算下的廉价回退要合法、确定，并保留正确机会成本；它不是完整分支比较已完成的证据。

[`KernelPotionContinuationRun.cs`](../src/CoopBots.Kernel/KernelPotionContinuationRun.cs) 处理药水前缀与后续原生续演。药水可能让目标消失、Boss 转阶段或菜单改变；必须验证实际后态，不能默认旧后缀仍有效。需要选择的药水必须贯通搜索捕获、模拟应用、计划携带及实盘回答，覆盖边界须具名记录。

## 真人与 Bot 的混合桌

[`KernelCombatPlanner.Mixed.cs`](../src/CoopBots/KernelCombatPlanner.Mixed.cs) 根据已结算真人输入和控制席位变化更新根。匹配已探索后继时有限复用；不匹配时从实际根原生重评旧后缀。当前代码以四个部署动作窗口检查续用，没有新输入时也不能无限信任旧路线。

不要提前模拟替真人出牌，也不要把仍合法的旧牌序当成新盘面上仍然优质。混合桌的取消延迟、输入后正确性、响应时间和协作战损需要单独测试；全 Bot 胜率不包含这些结果。

## 局外构筑

| 入口 | 职责 |
|---|---|
| [`Building/BuildValue.cs`](../src/CoopBots/Building/BuildValue.cs)、[`Building/DeckStrength.cs`](../src/CoopBots/Building/DeckStrength.cs) | 拿牌、删牌、升级的共同牌组差量与跳过比较 |
| [`Building/CardProfile.cs`](../src/CoopBots/Building/CardProfile.cs)、[`Building/DraftCardEffects.cs`](../src/CoopBots/Building/DraftCardEffects.cs) | 原生数值、条件效果、单回合属性和生命周期描述 |
| [`Building/DeckMechanisms.cs`](../src/CoopBots/Building/DeckMechanisms.cs)、[`Building/ConstructionMechanismFacts.cs`](../src/CoopBots/Building/ConstructionMechanismFacts.cs) | 引擎、触发来源、收益端和共享机制事实 |
| [`Building/ConstructionDecision.cs`](../src/CoopBots/Building/ConstructionDecision.cs)、[`Building/ConstructionBudget.cs`](../src/CoopBots/Building/ConstructionBudget.cs) | 有限构筑实验与档位预算 |
| [`Building/UpgradePreference.cs`](../src/CoopBots/Building/UpgradePreference.cs)、[`Building/OpeningUpgradeValue.cs`](../src/CoopBots/Building/OpeningUpgradeValue.cs) | 抽牌/启动升级的情境优先级 |
| [`EventHandlers.cs`](../src/CoopBots/EventHandlers.cs)、[`BotShopPlanner.cs`](../src/CoopBots/BotShopPlanner.cs) | 事件实际收益/成本、商店候选与资源比较 |
| [`BuildDecisionCapture.cs`](../src/CoopBots/BuildDecisionCapture.cs)、[`LiveCombatCapture.cs`](../src/CoopBots/LiveCombatCapture.cs) | 可见决策及原生战斗入口的本地采证 |

构筑是有界代理估值，不在每次奖励都解完整爬塔。原生效果是事实；抽到的频率、可支付窗口、组合成型度、先验与长期压力是估计，需要对照校准。预备打击等 `TemporaryStrength` 与单回合敏捷只对当回合有效，不能当永久成长。剑柄打击等抽牌升级应在实际基础牌组和能量窗口内比较，不能只按伤害增加排序。

缺少建模不能偷偷变成零价值或一票否决。事件应比较实际带升级/附魔的卡、累计资源成本及可见收益；不要凭隐藏未来 Boss 或假定奖励做“最优”决定。构筑改进要做局内不变/局外变化的消融，并与纯出牌改进分开。

## 读数与可复现性

搜索预测、具名原生夹具、保存战斗和完整爬塔回答不同问题。用 [测试与基准](TESTING_AND_BENCHMARKS.md) 的身份和分母规则提交结果；历史 PASS 数、执行覆盖率或一次预测胜利都不能替代通关率。源码与公共摘要可在仓库中复核，维护者本机 `work/` 下的原始存档与日志不随 Git 分发。
