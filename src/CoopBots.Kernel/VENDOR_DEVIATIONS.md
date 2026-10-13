# Vendor/ 的有意偏离

> **这是两张偏离表里的"源码层"那一张。** 另一张是 [`AGENT.md`](../../AGENT.md) §6
> 「偏离登记表」，登记的是**运行期行为**偏离。二分标准与关键区别写在那里，登记前先读。
>
> 本表的关键约束：**`Vendor/**` 的每一项偏离都必须由导入管线本身产生** ——
> 手改再补登记是不够的，重导入会静默覆盖。

`Vendor/**` 是从 CombatSolver 导入的上游源码。**当前来源是 0.41.0**(`UPSTREAM.json` 记录来源),
由 `scripts/import-combatsolver-kernel-41.py` 从 `work/combatsolver-sync-latest/src`
(该目录是 CombatSolver 仓库 `0e6cc2d release: prepare 0.41.0` 的检出) 生成。

## 2026-10-09：山铜原生触发标志的捕获、生命周期与搜索指纹

v3 实机 `combat039-v3-20261009T154700Z` 的首个完整状态失配是预测侧多出山铜的 `ShouldTrigger=false` 捕获条目；实机新根从蜥蜴尾巴条目开始。原生遗物顺序没有交换，旧镜像只在执行回合末时创建私有临时状态，而根捕获没有对应条目。独立原生山铜/蜥蜴尾巴盘面重现相同的 `field=captured-state` 失配；这不豁免真实状态差异。

山铜和假山铜现在在原生根捕获时保存 `_shouldTrigger`，回合末两阶段镜像、正常消费和本席回合开始清除使用同一分支所属的 `OrichalcumPredictionState`。现有续演文本与搜索指纹也记录该值。完整规划合同仍保留真实遗物钩子顺序、捕获条目和 true/false 语义；没有忽略、清空或只按可见数值比较。真实 very-early/normal/start 钩子与镜像的触发、格挡和分叉隔离由 `KernelPlanningFactsScenarios` 驱动。

`scripts/kernel_orichalcum_capture_adaptations.py` 与 `kernel-orichalcum-capture-adaptations-41.json` 在现有 0.41 导入适配之后生成两份 Vendor 修改。整文件前驱哈希、唯一具名接缝和最终文本哈希不符均中止；生产文件从独立重导入产物原子落盘。0.33 历史入口未变、未验证。原生夹具、实际 C# 故障注入、导入守卫故障及版本身份保存在 `work/architecture-refactor-20261009/fixed-budget-performance-v3/state-diagnostics/p0-native-orichalcum-v1/`。这是原生机制证据，完整门禁与再次实机测试由根任务单独执行，不宣称该关或 A10 已通过。

## 2026-10-08：共享随机生成与多席回合开始屏障

原生 `TheInsatiable.LiquifyMove` 对每席逐卡创建并随机插入：前三张 Draw、后三张 Discard。镜像原先先全部席位 Draw、再全部 Discard，并批量创建；共享 Shuffle 的调用次序因此不相同。同形 `Noisebot`/`SoulFysh` 也改成逐席、逐卡原生序列。生成策略、张数、目标和 RNG 本身保持原生。

单席上游的连续 setup 不能直接重复为多人 side-start。主机 `KernelPartySetup` 对照原生 `CombatManager.StartTurn/SetupPlayerTurn`：Bot 和被接管真人的生产选牌补丁同步返回，完成其 setup 后才到 Side；普通真人暂停的任务在全队 Side/Orb 钩子后恢复。`KernelCombatCapture` 的十二个捕获入口及一个 `LiveEndTurnRisk` 风险评估入口冻结控制席位，所有分支继承快照。新增 `CombatPredictionSimulator.PartySetup` 封存整个 party 的共同历史前缀及数据帧；完整尾帧按既有 fork 资格检查恢复。直接 hook 驱动 autoplay 也建立普通出牌的 execution scope，使抽牌/弃牌引发的 nested selection 有正确死亡集和帧归属。没有放松 pending、history、菜单或 RNG 校验。

选牌比较仅在独立分支使用共同已知 multiset，后续菜单不能沿用不同抽牌结果生成的 token。准备队列逐菜单核对来源、上下文、有效数量和完整有序牌状态；实际菜单变化就重新捕获并比较。昂贵重评估额度耗尽时使用实际菜单的合法选择完成事务，不按菜单数拒绝原生回合。384 是每次菜单比较的 committed steps，整段 side-start 可以累计更多，有限牌序工作另计；外层总 deadline 不延长，但同步原生事务内不能任意中断。

`AdaptablePower.AfterDeath` 已由 `DeathPowerSupport`/`DeathLifecycleSupport` 原生死亡复活路径补偿，诊断登记为已补偿监听者，保留机制断言。不是删掉死后变形。控制夹具覆盖一、二、四席位两次复活，以及生成有序牌堆、所有沙漏席位、多个城垛实例、选牌暂停和额外回合；具体读数、R2、四门以本轮报告为准。这些是机制对照，未解码重放 SZG1RJZ6XVL6 整局隐藏 RNG，不宣称该种子必胜。

来源：用户2026-10-08“修复上述问题并重新部署”。`scripts/kernel_szg_model_adaptations.py` 与 JSON 在旧 retained adapter 后校验整文件 predecessor、精确接缝、最终 target，并生成冻结的 party support。重导入419份生成源码与生产文本一致，9项隔离 importer 故障红/恢复绿；后续修改须重新冻结、对照。证据：`work/szg-repair-20261008T124624/import-final-proof-v2.json` 与 `model/`。

## 2026-10-07：临时属性的原生到期与手动队友目标

临时 Strength／Dexterity／Focus 按原生 `AfterSideTurnEnd` 监听顺序逐 Power 归还，仅处理本侧参与者。不能按属性分组或合并同属性，否则一层 Artifact 会保住错误的属性；额外回合也不能移除未参与队友的 Fade。独立旧 API 保留，正常玩家／敌方结算统一使用 `RestoreTemporaryStats`。临时 Dexterity／Focus 叠加读取原生 modified request，而非截断后的 counter delta；Anticipate 牌路径使用同一入口。首次玩家 Debuff 的 `SkipNextDurationTick` 字段按原生声明 Type 设置，真正消费该字段的 duration 家族不变。

原生手动 `AnyAlly` 允许在另一玩家存活时对自己使用，人数不足时 `CanPlay` 拒绝；候选枚举与模拟合法性同步补齐。自动出牌的随机队友仍按原生排除施法者。Plot 原生 `GetTeammatesOf` **包含自己**；本次仅补其死亡玩家过滤，不修改施法者收益或全局 AllAllies 规则。

用户来源：2026-10-07 单回合力量／敏捷审计与局内修复授权。`scripts/kernel_temporary_stat_adaptations.py`、`kernel-temporary-stat-adaptations-41.json` 校验五份适配前整文件哈希及唯一接缝，两个导入入口都接入；只执行验证 0.41。独立导入 416 源，SimulatedCombatState／Registry／Targets／Simulator.Card 与生产 text exact；CorePowerSupport 新接缝相同，仍有既存 GoldGain／Steam 诊断导入差量。本次五份导入源实际编译并通过十二组原生夹具，其余冻结本地支持件逐项列明，不宣称无补件的全 Vendor 重建。

证据：`work/incombat-repair-20261007/temporary-stats/combat/temporary-combat-reimport-proof.json`、`reimport-exact-check.json`、`temporary-combat-r2-results.json`、`temporary-combat-import-r2-results.json`。未来估值使用稳定分支上的同一原生到期投影，当前手牌和 Threat 仍使用当前属性；这部分非 Vendor 策略见 AGENT.md。

## 2026-10-07：冻结盘面的遗物抽牌和能量 Hook

上游 `PersistentPowerSupport` 先调用原生 Hook，再减捕获根回合的遗物贡献、加模拟回合贡献。原生 RingOfTheSnake 等方法读取 live Owner 的回合计数，因此同一个冻结 turn1 根会在实盘推进到 turn2 后把未来抽牌从5变3。
本地按照原生 Hook 的监听顺序逐项分派，对五种依赖回合的抽牌遗物和 Pendulum/Pocketwatch/PollinousCore 使用模拟回合与捕获计数；Bread/PaelsFlesh 的能量同理。普通修改和晚修改的两遍顺序保持，其他原生监听者仍调用捕获模型的方法。

用户来源：2026-10-07 修复第二层团灭后的局内算法，路线策略保持。`scripts/kernel_frozen_hook_adaptations.py` 与 `kernel-frozen-hook-adaptations-41.json` 校验修复前整文件哈希和唯一接缝，两个导入入口均接入；0.33 入口只保留接线，本次执行验证的是0.41。
独立0.41导入416源，修复文件与生产 byte/text exact，并从导入文件编译原生抽牌/能量/洗牌夹具；其他冻结本地支持件逐项列出，不宣称无补件的完整 Vendor 编译。证据 `work/incombat-repair-20261007/projection/reimport-proof.json`、`projection-r2-results.json`、`projection-import-r2-results.json`。
原生9/12/13次洗牌调用与完整 RNG/有序牌堆一致；历史四处 RNG 均是同一流多推进9/12/13/13次，具体历史洗牌来源尚未逐场完整还原。

## 2026-10-07：多人额外回合的原生参与席位

`CombatPredictionSimulator.SimulateEndPlayerTurnBeforeOrbPassives` 接受可选参与玩家列表，使 Ambergris/PaelsEye 的额外回合只对实际参与者运行回合结束效果；普通回合仍默认全部玩家。
`SimulatedCombatState.ReactiveRelics` 在玩家侧开始时清除未参与且存活的 PaelsEye 所有者的参与状态，对照原生 `PaelsEye.AfterSideTurnStart` 的否定分支；死席已退出原生 hook listener 集合，其旧参与状态保持不变。额外回合跳过敌方侧、消耗来源、逐席回合数和就绪状态的编排位于非 Vendor 的 `KernelTurns`/`KernelSession`。
两处修改由 `scripts/kernel_relic_choice_adaptations.py` 的锚定适配生成，上下文漂移直接报错。0.41 管线独立重导入 416 文件退出 0，两文件与生产文本完全一致，同时固定瓦解文件也一致；证据 `work/decision-repair-20261007/projection-reimport-check.json`。原生对照和故障注入见 `LatestProjectionRepairScenarios`，不是改写游戏额外回合规则。

## 2026-10-07：续演边界的 Power 写入隔离

上游 0.41 `Fork` 逐个克隆 Power；本地既有分配优化改为写时克隆。本次用户要求修复模拟与执行分叉，实际 `KernelScriptRefinementRun` 保存边界分支后继续写父分支，证明仅标记子分支会让后续 Inflame、Plating 写入污染边界。
现在七类 Power 引用在 Fork 时同时标记父子，写入前克隆并重新绑定镜像容器和 `PredictionStateStore`。同步标记采用小容量单条并发锁带的 `ConcurrentDictionary`，允许同一只读来源被多个搜索 worker 并发 Fork。

`scripts/kernel_power_fork_adaptations.py`、`kernel-power-fork-cow-adaptations-41.json` 与 `templates/kernel-power-fork-cow-support.cs` 生成完整 COW 支持及 Fork、可写 Power 和 StateStore 接线；三个适配源的 SHA256 不符直接报错。两个导入入口都接入，当前只验证 0.41。独立重导入的本次语义、逐项 exact/既存文本缺口、保留本地支持件哈希见 `work/decision-repair-20261007/projection-supplement-reimport-proof.json`。
独立编译显式使用重导入的修复源，并补入已有 PowerSync、OutOfCombat 等本地服务；它不是无补件的全 Kernel 重导入证明。Inflame/Plating 原生续演、64 个只读并发 Fork 及实际故障注入见 `LatestProjectionRepairScenarios`。这证明共享写入家族缺陷，不等于已还原历史 Exoskeleton 敌阵漂移或知识恶魔 HP34/36 分叉。

## 2026-10-07：保留隐式全选的空显示服务修正

生产既有 `CardChoiceSupport.BuildChoices` 修正允许 `displayNames` 为 null：用 null-safe lambda 生成 token，而不是绑定 null 对象的 `displayNames.Card` 实例委托。当前 Kernel choice cursor 本来就不传显示服务；独立导入 Ringing/Survivor 镜像因此暴露旧管线遗漏。
本次仅把这条已有修正以唯一上下文锚定适配加入 `kernel_relic_choice_adaptations.py`，不修改选牌政策或复制整文件。导入镜像的原生场景和故意恢复错误委托的 RED/GREEN 证据见 `work/decision-repair-20261007/projection-supplement-import-support-r2-results.json`。

## 2026-10-07：知识恶魔固定瓦解策略

用户明确放弃 Debuff 估值：自动续演和待决搜索候选统一只选 Disintegration，实盘使用同一固定策略。原生四种惩罚的效果与显式效果模拟均保留。
`scripts/kernel_decision_consistency_adaptations.py` 在既有哈希校验适配后持久化这两处修改，上游上下文变化会报错。独立目录重导入 416 文件退出 0，KnowledgeDemonChoiceSupport 文本与生产完全一致；证据 `work/decision-repair-20261007/reimport-check.json`。
下面 2026-10-04 的选择估计已由本次用户策略替代。

## 2026-10-04：知识恶魔实盘与预测选择统一

`KnowledgeDemonChoiceSupport` 的默认续演调用 `KernelKnowledgeChoice`，与实机选牌入口共用同一后果估计。
`MonsterMoveEffects` 传入分支实际HP；不读取根实盘HP代替未来状态。保留显式计划选择的优先级。
原生崩解可格挡，去掉旧注释的不可格挡假设。自动续演不再固定选择非崩解惩罚。
来源：用户本轮复盘发现预测Sloth限三张、实盘崩解13层后仍执行旧胜利剧本。
导入由 `scripts/kernel_decision_consistency_adaptations.py` 在既有位置补丁后应用，两个导入入口均已接入。
同时补入旧管线遗漏的自动选择续演与每招仅递增一次计数的既有修改。0.41独立目录重导入416文件成功，两份修改文件文本与生产源码一致；0.33旧入口未验证。

## 2026-10-03：战斗遗物选牌的适配入口

`TurnStartChoiceSupport.ResolveTokens` 由 private 改为 internal，便于实机选择器
与续演策略共用原生的卡牌身份、重复项与数量验证。方法体不变。
重导入由 `scripts/kernel_relic_choice_adaptations.py` 自动应用，两个导入入口均已接入。
用户来源：2026-10-03 赌博筹码不换牌的日志反馈；策略登记见 `AGENT.md` §6。

## 2026-10-01：Tank／Guarded 原生多人能力补齐

用户要求审计战士、猎手全部能力牌。当前游戏 v0.111.0 卡池共 38 张，
升级前后 76 个版本；旧核只有 Tank 的 OnPlay 被 `MethodNotMirrored` 截断。

- `CardEffectSpecRegistry` 注册 Tank → TankPower 1。
- `SimulatedCombatState.ApplyWithBeforeApplied` 对照原生 `TankPower.AfterApplied`，
  首次生效时给每个存活玩家队友单独的 Guarded 实例，保留施加者。
- `TeamDamageMirrors` 对照原生两个 `ModifyDamageMultiplicative`：Tank 承受
  1.5 倍攻击，Guarded 承受 0.5 倍；仅 powered attack，多施加者分别相乘。
- `AfterDeathMirrors` 对照原生 `GuardedPower.AfterDeath`：实际施加者死亡移除
  其保护，被阻止的死亡保留；不同施加者与分支互不污染。

预测分支保留 Guarded 的 `Applier` 身份，但省略 `AfterApplied` 中仅用于卡面显示的
平台玩家名查询；离线模拟没有 Steam 显示名服务，伤害与死亡判断都直接使用身份。

这是补齐原生语义，没有改变游戏规则。导入器在位置补丁完成后调用
`scripts/kernel_class_power_adaptations.py`；锚点变化会具名报错。
0.41 重导入验证落在 `work/power-coverage-20261001/reimport/`，没有覆盖工作检出。
其余能力牌的估值扩展位于非 Vendor 层，记录在 `AGENT.md` §6。

定向回归：`--class-powers-only`；原生出牌／跨回合覆盖、能力触发正反例、
薪火之源实际首步选择、触发路线保留、双 Tank 与死亡清理。
故障注入的命令、退出码及原文见 `work/power-coverage-20261001/r2.json` 和对应日志。

## 先纠正一个常见误读:`upstream_sha256` 不是完整性校验

导入器会重写命名空间:

```python
adapted = text.replace('CombatSolver.', 'CoopBots.Kernel.Vendor.').replace(
    'namespace CombatSolver;', 'namespace CoopBots.Kernel.Vendor;')
```

所以 `UPSTREAM.json` 里那个字段记的是**来源**(这批代码取自上游哪个文件),不是"这个文件没被动过"。

真正的风险是下面这条。

## 真正的风险:重导入会覆盖改动

重跑导入器会从上游重新生成 `Vendor/**`,**覆盖**这里的任何手工编辑。
因此:凡是没有写进导入器(内联钩子)或偏移表(P1 表)的改动,重导入后必然静默消失 ——
表现为"搜索行为突然变了",而没有任何编译错误能解释它。

**所以每一项偏离都必须在下面登记,并且必须由导入管线本身产生。**

## 管线:三层,各自可重放

| 层 | 位置 | 作用 |
|---|---|---|
| 内联钩子 | `scripts/import-combatsolver-kernel-41.py`(函数 `add_*`) | 逐条锚定的小改动;锚点漂移即 `raise`,不会静默丢失 |
| P1 偏移表 | `scripts/kernel-p1-adaptations-41.json` | 按位置记录的多人适配(全体目标、每玩家虚数等) |
| 重锚工具 | `scripts/reanchor-p1-adaptations-41.py` | 把**唯一的作者版**表(0.33.9 的 `kernel-p1-adaptations.json`)重锚到新快照,生成上一行那张表 |

重锚工具的两条不变量,各自都对应一次已经发生过的错误:

1. **顺序是真正的锚点,偏移不是。** 按升序在"上一处结尾之后"搜索,短片段(如 `        };\n`)
   才不会撞到后文里的同名片段。
2. **偏移写在原始文本坐标系里。** 导入器按偏移**倒序**作用于未打补丁的文本,所以每个偏移
   必须在原始文本上成立。曾经按"边打边算"的坐标写过一次,结果把一句局部变量声明插进了参数表中间。

`old == ""` 的纯插入没有可搜索的文本,以它原位置**前一行**为锚。

`base_sha256` 取的是**上游文本**(命名空间重写后、任何 CoopBots 钩子之前)的哈希 ——
否则记录值会依赖我们自己的补丁,重锚工具永远算不出来。

## 偏离清单

### D9 — 续用戳的字段位置稳定性（2026-10-06）

**上游：** `AppendPowers` 直接 `foreach (powers.Where(power => power.Amount != 0))` 渲染 `;P=`，
**没有** `PT=` 这一段；两处 `onplay_configuration` 也只在非空时输出。而
`ContinuationStamp.DescribeDifferences` 把**整份队伍文本**按 `;` 平铺后**按位置**逐个比较。

**我们：** ①`;onplay_configuration=` 与 `;PT=` **恒输出**（含空值）；②`P=` 的渲染按
`(Owner.CombatId, Id.Entry)` 规范排序，而不是按枚举顺序。

**为什么：** 字段数一旦依赖状态，两侧的形状就可能不同，位置比较随即整体错位。实机 2026-10-06
（seed `8W0NNPPSPFTX`，`FABRICATOR_NORMAL`）：

```
field_order[35] expected={PT=0:GUARDED_POWER=1@0,2:GUARDED_POWER=1@2,3:GUARDED_POWER=1@3,}
                actual={R=3008:2132…
```

预测侧有定向能力、实机侧没有 → `PT=` 只在一侧出现 → 之后每个字段都错开一格，**真正的分歧
（实机缺 GUARDED）反而没被命名**。`P=` 同理：实机渲染 `Creatures.SelectMany(c => c.Powers)`，
分支渲染 `EffectivePowers()`（hook listener 顺序），两侧枚举顺序不同却按索引比，`P[n]`
可能只是顺序差异（或反过来，两个不同的盘面渲染成同一段文本）。

**顺带补的旧债：** 本次钩子同时把 **P0-SIM-1 的 `active` / `targeted` / `PT=` 机制**（2026-10-05
引入生产、但从未加入管线）纳入 `method_new`。在此之前 `Runtime/ContinuationStamp.cs` 的重导入
结果与生产文件不一致，重导入会**整段删掉**这套机制。

**施加方式：** 导入器钩子 `make_stamp_fields_positionally_stable`（三处锚点漂移即 `raise`；
若目标文本已带该 port 也 `raise`，避免重复施加）。`AppendPowers` 的整段替换文本由生产文件
直接生成，避免手抄。重导入到临时目录后 `Runtime/ContinuationStamp.cs` 与生产文件**逐字符一致**。

**回归：** `KernelEngineScenarios.StampFieldsArePositionallyStable`——① 带不带定向能力，队伍戳的
`;` 字段数必须相同；② 同一对能力以相反顺序挂上，`P=` 段必须相同。

### D8 — Rampart 的每回合一次授予（2026-10-06）

**上游：** `PersistentPowerSupport.TriggerRampart` 只在 `TriggerAfterSideTurnStart` 里以
`side == CombatSide.Player && !isExtraTurn` 为条件触发，没有"本回合第一个席位"的门，用
`simulator.GainBlock(enemy, rampart.Amount, …)` 累加。上游求解器是**单席位**模型
（`CombatBeamSolver.TurnExecutionContinuation` 传 `[player.Creature]` 且每回合只走一圈），
所以那里它天然每回合只授予一次。

**我们：** 多人层 `KernelTurns.StartNextPlayerTurn` 按**席位**循环驱动同一条玩家侧回合开始序列
（`foreach (var player in players…)` → `TriggerSideTurnStart(Player, [player.Creature])`），
于是同一条 side-level 钩子每回合被触发席位数次。`SimulatedCombatState` 新增
`TryClaimRampartGrant(owner, round)`，由 `TriggerRampart` 在用 `(rampart.Owner, RoundNumber)`
认领一次后才授予。认领键用**能力持有者 + 回合号**，因此不需要任何重置钩子；容器用
`ForkableDictionary`（写时复制），所以某个分支授予过不会污染它的兄弟分支。

**为什么：** 实机 2026-10-06（seed `8W0NNPPSPFTX`，A10 四人，`TURRET_OPERATOR_WEAK`）
refinement 边界检查连报三次：

```
CoopBots refinement: disposed targetRound=2 reason=next-turn state differs from prediction:
  field=E1.block expected={480} actual={120} …
```

480 = 4 席 × `RAMPART_POWER(120)`。日志三次报告的期望值**恒为 480** 而不是 240/360/480，
这一条把成因钉在"同一回合内按席位重复"而不是"跨回合累加"——模拟在**敌方**回合开始会清空
敌方格挡（`CombatBeamSolver.Expansion.cs` 的敌方阶段），跨回合累加会读出递增序列。
原生语义由 coverage 记录背书：`RampartPower.AfterSideTurnStart(side, participants, state)`
的执行体是 `CreatureCmd.GainBlock`（`third_party/CombatSolver-main/coverage/state-mutations.json`，
`runtimeVerified: true`），即"每阵营回合一次、带整份参赛者列表"的普通钩子。

**施加方式：** 导入器钩子 `add_rampart_once_per_round`（两个文件各自的锚点漂移即 `raise`）。
重导入到临时目录后，两处新增与生产文件逐字符一致。

**回归：** `KernelEngineScenarios.RampartGrantsOncePerPlayerTurnNotOncePerSeat`——四席桌上
跨一个回合后 Turret 的格挡必须等于 120。R2 变红原文（把认领那一行注释掉后）：

```
System.Exception: Kernel: RampartPower is a side-turn-start hook and must grant 120 block
once per player turn, not once per seat; a 4-seat table got 480 (= 4x).
Live 2026-10-06 reported `E1.block expected={480} actual={120}`.
```

### D7 — Music Box 的预测状态改成可 fork，不再拒绝（2026-09-30）

**上游：** `BeforeCardPlayedMirrors.cs` 的 `MusicBoxPredictionState` 同时实现
`IPredictionStateForkable` 与 `IPredictionForkBoundary`，`Fork()` 先 `AssertForkable()`，
只要 `CardBeingPlayed` 非空就
`throw new InvalidOperationException("Cannot fork Music Box during card-play resolution.")`。
上游是单机：分叉点由它自己控制，拒绝分叉只是让那一次预测保守一点，没有别的后果。

**我们：** `Fork()` 直接 `return MemberwiseClone();`，`AssertForkable()` 留成空实现。
**接口本身保留**——`IPredictionForkBoundary` 还有第二个用途：
`PredictionStateStore.SupportsManualCardChoiceContinuation` 用它把这类状态挡在
"手牌选择续演"之外，那条限制仍然成立，只有"拒绝分叉"这一半被拿掉。

**为什么：** 联机四人局里这个异常不再是"保守"，而是**离线整个决策**。
`KernelCombatPlanner.Choose` 的 catch 把它变成 `TournamentOutcome.Declined`，
真正的规划器（锦标赛）整轮退出、退回 legacy 规划器。实机 2026-09-30（A10 四人，
第三层 Boss `AEONGLASS_BOSS`，即团灭那一场）量到：

```
CoopBots tournament: falling back for this tick —
InvalidOperationException: Cannot fork Music Box during card-play resolution.
```

连续 **6 次**，且**恰好是持有 Music Box 的两个席位**（VAKUU 事件的遗物奖励，HOST 与 B2）：
`via=legacy` 的 7 次决策里有 6 次落在这两人身上。也就是最强的规划器在**整局唯一
打不过的那场战斗**里把自己关掉了。`CardBeingPlayed` 只是一个跨"单次出牌 before/after
钩子"的标记，持有的是两个分支共享的 live `CardModel`，浅拷贝对该分支是合法状态；
分支若不接着演同一张牌，代价有界——这一回合不再给虚无复制品，`WasUsedThisTurn`
在下个回合边界自己重置。丢一张复制品 < 丢整次搜索。

**施加方式：** 导入器 `import-combatsolver-kernel-41.py::make_music_box_forkable`
（锚点漂移即 `raise`）。生成文件与生产文件除行尾外逐字符一致（已核对）。

### D6 — FLANKING / KNOCKDOWN 的队友增伤必须在回合末摘掉（2026-09-29）

**上游：** `EndTurnPowerSupport.TriggerRegular` 只翻转上游自己需要的那批 power；单机没有"队友"概念，
`FlankingPower` / `KnockdownPower` 对应的多人牌在上游不存在，"owner 阵营回合末 Remove"这一步自然也没有。

**我们：** 这两张牌的**增伤那一半**早就在 `TeamDamageMirrors` 里镜像了（骑
`ModifyDamageMultiplicative`），但 `AfterSideTurnEnd` 里那一半一直没人管。于是在内核里，一次 FLANKING 会
**跟到整场战斗结束**，把之后每一回合的输出都乘 2。

**为什么：** 实机证据（A10 四人，第 25 层 `ENTOMANCER_ELITE`）：缓存剧本 `ending=VICTORY`
（VerifiedTerminal），81 步**一步不差**全部按剧本执行，实机却团灭、精英还剩 **218/789**；而仿真在 R7 的
`DEFILE` 那一步就"打死"了它。R2→R6 实机出牌序列与剧本逐步一致（16/14/13/13/6 张，顺序都相同），
唯一差别就是仿真把 R2 打出的夹击一路带到了 R7 —— 正好是那 ~210 点。实机侧 `FLANKING_POWER:2` 只在 R2 出现过
（`FlankingPower.AfterSideTurnEnd` 在敌方阵营回合末摘除）。

**改法：** 在 `TriggerRegular` 里按 `ownerParticipates`（= 上游 `participants.Contains(Owner)`）加
`FlankingPower` / `KnockdownPower` 两个 case，用 `SetPowerAmount(power, 0)` **逐个实例**清零
（`SetAmount<T>` 对 Instanced power 只清第一个实例）。
由导入器钩子 `add_team_multiplier_lifetime` 生成；`tests/PatchSmoke/FlankingLifetimeScenarios.cs`
同时钉住三件事：施加者本人不吃加成、其他席位吃 ×2、**过回合后必须消失**。
重导入复现：`python scripts/import-combatsolver-kernel-41.py --output /tmp/vendor-check` 后
该文件与生产文件逐字符一致。

**同族的单位错误（同轮修掉，不在 `Vendor/**`，所以不需要导入钩子）：** 摘除闸门修好后，**同一个玩家回合内**
从实机捕获到的实例仍按实机的单位被读——实机一次施法写 `Amount 2` 且把 `Applier` 渲染成平台名，内核自己写
"一次施法 = 1 层"和 NetId。实测（`FlankingLifetimeScenarios` 的 CAPTURED 用例，修前）：

```
CAPTURED: a live FLANKING must not amplify the applier's own attack, got 24.   (应为 6)
CAPTURED: a live FLANKING ('Applier' is a platform name, an amount of 2 per card)
          must read as one stack for the teammates, got 24.                     (应为 12)
CAPTURED: three live FLANKING applications must still compound to 8x (48 ...), got 384.  (64×!)
```

修法在 `src/CoopBots.Kernel/TeamDamageMirrors.cs`（我们自己的文件）：① `FlankingStacks()` 把实机单位的实例
折算成内核层数（每张 2 点 = 1 层）**之后**才交给原来那条 `Compound(2, stacks)` —— 复利规则一行没动，
3 张夹击在两种单位下都还是 8×；② `Amplifies()` 优先用施加者 **Creature**（实机 `ModifyDamageMultiplicative`
自己就用它），平台名/NetId 的字符串判据降为兜底。既有
`KernelEngineScenarios.AuditFlankingKnockdown`（1 张 20 / 3 张 80 / 排除施加者）在改后仍然绿。

### D5 — 暴怒复制与现有多人修复的重导入保持（2026-09-27）

游戏 `Outrage.OnPlay` 为每个存活玩家（包括施法者）调用 `CreateCloneForPlayer`。
模拟原先生成基础版，丢失升级和修饰；现改为从 `playedCard` 克隆，排除阵亡玩家，保留生成者与接收者语义。
0.33.9 和 0.41 导入器均包含此补全。

重导入检查同时发现 0.41 管线会重新引入 AllForOne 分支错位，并丢失已经存在的
Concoct / Blaze / Coordinate / Fade 应用与临时属性派发。新增精确锚定的
`preserve_verified_registry_fixes` 在位置补丁之后恢复这些既有修复；未改变其生产行为。
完整生成的 `CardEffectSpecRegistry.cs` 与生产文件逐字符一致。证据与差量表见
`work/strength-20260927-v3/implementation.md`。

### D1 — 多人适配整体移植到 0.41(2026-09-19)

0.41 不只是加常量:它把若干机制换了形状,所以旧补丁不能原样套用。逐条核对后:

**保留**(重锚后照旧生效):`MonsterMoveEffects` 的全体目标(69 处)、`PlayerTurnEndLifecycle` /
`TurnStartRelicSupport` / `ReactiveRelics` / `RelicTurnStart` 的 `etherealByOwner`、
`EndTurn.cs` 的 `participants` / `beforeHandEffects`、`CardChoiceSupport` 的 `displayNames` 可空、
`MonsterMoveSemantics` 改 `partial`。

**改锚(`RETARGET`)**:
- `EndTurn.cs` 的 `playersEndingTurn`:0.33.9 从 `CombatManager.Instance.PlayersTakingExtraTurn`
  取队伍,0.41 直接读 `State.CombatState.Players`。改成
  `participants ?? State.CombatState.Players`,多人补丁仍然要点名"这一回合结束的是谁"。
- `CardEffectSpecRegistry` 的 `case Adrenaline:`:0.41 把 Adrenaline 移到了
  `CardDrawCardMirrors.AdrenalineOnPlay`,那个 case 不复存在。`ENERGY_SURGE` / `BELIEVE_IN_YOU`
  (给队友/目标补能量)改成挂在 `switch (card)` 的第一个 case 前。

**丢弃(0.41 已自己做掉,或改补丁会与上游打架)**:
- `SimulatedCombatState.ApplyTargeted` 的 Instanced Power 插入 —— 0.41 自己按
  `PowerInstanceType` 分支并绕开 `(owner,type)` 合并缓存。
- `EndTurnPowerSupport` / `CorePowerSupport` 的 `etherealByOwner` 管道 —— 0.41 把 JossPaper 的
  虚数逻辑搬到了 `SimulatedCombatState.ReactiveRelics`(我们的补丁已在那里接住),
  `TriggerRegular` 不再读这个计数,那条管道没有下游了。
- `PlayerTurnEndLifecycle` → `CorePowerSupport` 的那一路转发(同一原因;另一路去
  `TurnStartRelicSupport` 的转发保留,它才是 JossPaper 的路径)。

判定依据是**读 0.41 的对应实现**,不是锚点匹配失败。

另:0.41 删掉了 0.33.9 的 history-sensitive 抑制窗口
(`SuppressHistorySensitiveCardModifiers` / `BeginHistorySensitiveCardModifierScope`),
因为它把"本回合已出的牌"折算进了 PhantomBlades / Lethality / Unmovable 三个镜像本身
(`HandleLethalityPower` 减去正在打出的这张、`HandleUnmovablePower` 减去
`GetPoweredBlockEvents(CardPlay)`)。所以 `KernelSession.Play` 不再需要那圈
`try/finally` 包裹 —— 删掉即可,多人语义没有丢失。

### D2 — 从 `Snapshot` 抽出派生量为可调用入口(2026-09-19,仍在)

**动机**:BFWS novelty search 的判据是 `CaptureNoveltyFacts` 读的一批派生量
(`ReachableHandValue` / `StrategicEffects` 五维 / `ReplayPotentialValue` / …)。
这些值上游已经算好,但算在 `CombatBeamSolver.StateEvaluation.cs` 的**私有实例方法**
`Snapshot(...)` 里,没有独立入口。

**改动**:把该方法的若干计算段抽成 `internal static`,参数化其依赖;原处改为调用。
不改变任何计算,只改变可达性。

### D3 — 打分走反射桥,不走 0.41 的装配(仍在)

`CombatSolverEvaluator` 通过反射调用 vendored `CombatBeamSolver` 的私有
`Snapshot(...)`,按席位求和。**不能**改用 `work/combatsolver-sync-latest` 自己构建出的
`CombatSolver.dll`:两个程序集里的 `CombatPredictionSimulator` 是**同名不同类型**,
引擎只认自己那一个,把我们的联合状态递进去会抛 —— 于是分数静默退回降级评估器,
比不接更糟。`CombatSolver41.cs` 是"0.41 端到端能跑"的探针,但它的展开只走一个玩家的手牌。

### D4 — `ContinuationStamp.CaptureLive(CombatState, Player?)`(仍在)

0.41 只有单参数版本,永远盖本地玩家的戳。四人计划要从每个席位自己的视角校验,所以
视角必须可选。由导入器内联钩子施加。

## 当前基线(2026-09-19,0.41.0 引擎)

构建(三个项目都 0 错误):

```
work/dotnet-win/dotnet.exe build src/CoopBots/CoopBots.csproj -c Release \
  "-p:STS2DataDir=<游戏 data 目录>" "-p:OutputPath=<临时目录>" -p:NuGetAudit=false
```

- `PatchSmoke`(默认与 `-p:EnableKernelTests=true` 两种):**当时读数为 PASS 50**,唯一失败
  `LastStandScenarios.cs:36` 的 Beacon 断言
- 该失败**先于** 0.41 导入存在,由同日 `EnemyHpWeight 0.25→0.5` 引入,与本次导入无关
- `TypeLoadCheck` exit 0(`CoopBots.dll enumerates 390 types with CoopBots.Kernel unresolvable`)

> ⚠ **"PASS 50" 是 2026-09-19 当天的读数,不是当前门槛。**
> 当前判据见 [`AGENT.md`](../../AGENT.md) §3 的表,读数只认 `work/verification/` 的最近一次运行
> (`python scripts/state.py show`)。不要拿这里的数字去比对现在的套件。

**导入方法本身不变**(这条才是本节要守的):重导入前后 **PASS 数必须完全相同,且失败集合不变**。
任何差异都说明导入改变了行为 —— 用**同一次运行前后的两个数**比,不要用历史数字比。

## 可证伪的版本标记

`KernelTeamSearch.EngineMarker` 打印在 `search-complete:` 日志行的 `engine=` 字段。
它的两个数字取自 vendored `SolverWeights.MaximumNoVictoryEscalations` /
`DeathSavePremiumPercent` —— 这两个成员只在 0.41.0 存在。所以:回退引擎会**编译失败**,
而旧 DLL 根本打不出这个字段。日志里有 `engine=0.41.0(nve=2,dsp=900)`,才是 0.41 真的在跑。
## 2026-10-08：1JJ0XFWWNLTC 原生联机机制与选牌协议修复

本轮直接对照游戏 v0.111.0 与导入来源 0.41.0，保留来源主体。
上游 README 明确只支持单人，并说明实机变化时重新搜索；不能把上游模拟终局视为原生重放证明。

- `AddPowerInstance` 默认 Target 改为 null，Owner/Applier 各自保留；显式定向能力仍由原定向接口填写 Target。原生 Guarded 无 Target，上游默认 owner 的假设在我方 Tank 多人适配中暴露。
- 新获得的敌人能力按原生 `ShouldScaleInMultiplayer` 和原生 override 缩放；已有实例增量不再缩放。默认倍率读取捕获的幕号，Plating 保留原生 Decrement 初始化。上游该入口没有多人首次倍率。
- `KernelMonsterMoves` 每一击使用既有全目标 Damage 事务，先计算本击所有玩家承伤，再结算死亡及反击；替代我方逐玩家即时死亡路径。保留伤害源、Osty/DieForYou 抑制与多击间状态推进。
- HiddenDaggers 保留上游 implicit-all 的要求数量表达；我方 `KernelChoice` 在捕获真实菜单时存有效数量，另存原始数量、执行源和完整有序候选。原生联机菜单匹配校验这些可见元数据，真实失配仍撤销后续缓存。预测 cursor 额外校验其可见嵌套 context 与有效选中数量。
- 零候选仍调用原生选牌接口的普通 Spec 与 Seeker 保留空菜单和原始请求数量；嵌套自动打牌经同一个动作选择入口记录。31 个卡牌分支已核原生调用条件，Dredge/NeowsFury 保留容量大于零的条件。运行时 Armaments 手牌升级复用原生 IsUpgradable 过滤，17 个原生选择重载已核局内候选过滤；没有把未知回合开始或药水通道冒称已验证。

0.41 导入入口在既有 temporary-stat adapter 之后依次调用独立 latest-model/latest-choice adapter。
历史 0.33 入口保持任务前字节，不能把 0.41 的严格基准错误套到旧快照。
只允许准确基准与唯一上下文，导入变化具名报错。原生夹具、故障注入及独立重导入证据统一落在
`work/latest-run-repair-20261008/`；是否完成四门以该任务最终 `work/verification/*/summary.json` 为准。
普通运行时没有任意 predictor ContextId，未声称所有原生菜单都暴露该字段；未证明历史整局可赢。

全生成源审计另发现八个**任务前已有**支持源没有被旧导入证明覆盖：分配计量、StateStore/卡牌预览支持、作弊金币一致性与具名边界诊断。
`kernel_retained_support_adaptations.py` 按修复前捕获源码逐段保留这些实现；没有因此改动生产机制或把旧字段差量忽略掉。
0.41 导入覆盖 418 个生成 C# 源（416 上游源加支持件），还有 331 个未被该入口重写的既存辅助源，逐文件与任务前基线哈希相同。
这是生成集合与既存支持的明确边界，不能宣称 418 文件可以独立代替整个当前 Vendor 目录。

## 2026-10-09：原生生成牌身份与当前牌堆洗牌监听

来源仍为 0.41.0，本轮不以 0.50.1 替换内核。原生 0.111.0 的 `CombatState.CloneCard` 保留 `AfterCloned` 对 `DeckVersion` / `HasBeenRemovedFromState` 的重置；本地 snapshot/COW 帮助方法为捕获特意恢复这两字段。因此仅 gameplay `SimulatedCombatState.CloneCard` 清除两字段，捕获和父子分支保留政策不变，`cloneOf` / `IsClone` 没有修改。

`ModifyShuffleOrder` 的卡与附着监听按当前分支五牌堆顺序排列，保留非卡槽位、同牌附件相对次序及每项 pending-choice 停止边界。实际原生付费 Anger、Shuffle、Draw 对照捕获了 PerfectFit 在原牌/生成牌相对次序上的差异；未知第三方跨类型监听交错未被证明。这两接缝的修复不证明后续 AXEBOTS 主线程挂起的具体原因。

适配追加在既有 `kernel-szg-model-adaptations-41.json`，原模块和原支持源保持。实际重生成 419 C# 与共享 Vendor 规范化差异为0；base/seam/target三个物理坏输入 RED1，精确恢复 GREEN0。证据 `work/architecture-refactor-20261009/native-card-scope/import-verification-v2/proof.json`。最终7个物理C#故障控制、原生证人和DLL/PDB见 `native-card-scope/r2-final/proof.json`，重复早期 epoch 不累计为独立故障；完整门禁与实机状态以本轮最终记录为准。


## 2026-10-09：原生历史攻击与首次能力变化捕获

来源仍为 CombatSolver 0.41.0。仅 `Search/SimulatedCombatState.cs` 与 `.AutoPlay.cs` 由新增 `kernel_history_capture_adaptations.py` / `kernel-history-capture-adaptations-41.json` 生成：MaterializeRoot 捕获已完成且非 dupe 的当前攻击、已打技能 membership 与 VitalSpark 基线；被移出牌堆的历史攻击保留原生 original 身份和冻结 preview，前后回合共用同一捕获函数。实际原生 paid Strike、消耗/移除后的 HistoryCourse 回放、Spark 2→3 与 paid skill 后生成 Smog 卡建立对照。9 个 route-relative 评分账仍保留原分支计算与 Fork，主机 Facts 单独记录 SearchMetadata；没有归零或按全战历史重算。

新适配的 8 个 predecessor/seam/target guard 均实际 withdraw RED1 / exact restore GREEN0；原13个 Orichalcum导入控制复用。419 个生成 C# 仅上述两文件有规范化差异，既存 ContinuationStamp 完全未改；主机 Facts 复用其冻结卡渲染覆盖已移除历史牌。证据 `work/architecture-refactor-20261009/fixed-budget-performance-v3/state-diagnostics/p0-route-ledger-v1/sealed/`，13 项新的物理 C# R2 及六 DLL/PDB 在相邻 `r2-final/proof.json`；12项旧有效物理收据复用，未能令第13项变红的payload-only mutant及恢复保留在原 `r2/`，不计为通过。 这是本批聚焦原生证据，完整门禁与实机结果由根任务另行验收。


## 2026-10-09：根能力槽位与原生行动意图缓存

CombatSolver 0.41.0 来源不变。仅 `Search/SimulatedCombatState.cs` 与 `.MonsterAi.cs` 经 `kernel_power_origin_adaptations.py` / `kernel-power-origin-adaptations-41.json` 生成：分支 Force/Advance/Register/Pending/initial roll 后按已捕获 Current.Intents 的 Attack/DeathBlow 类型同步该 owner 的攻击意图，未完成初始 roll 的占位不发布；离场 owner 从意图缓存移除。没有读取 live NextMove、清空 captured flag、改变 AI 行动、弱化未知 payload 或更改评分/Fork/fingerprint。

主机 Facts 以有效有序能力及全部必需捕获 payload 比较未来状态，root-relative `_retiredRootPowerSlots` 仍保留 Vendor 与分叉/搜索键。只有 HP0、已移出 roster、PermanentlyDead 且无有效能力的旧 owner 从 known/death/AI 规范投影退役；尚在场、Reviving 或未完成死亡保留。已移除零 amount 实例仅允许其具名 RequiredPowerStates payload 消退，未知 payload 仍拒绝；新实例隐藏状态、真实 hook 顺序及 pending callbacks 保持完整。

原生 paid lethal Strike 实际调用 StockPower→CreatureCmd.Add：旧 Axebot 移除，新 stock1 stage HP85/编号/BOOT_UP 一致；随后原生 BOOT_UP(block10/Strength3)与 paid Strike 一致。Juggling 移除 counter7→新 counter0→下一 paid counter1，真实有序重获/隐藏 counter 与 active AI static 差异仍拒绝。原生 Adaptable retained revive 两侧仍以原未知 payload 不能复用；已由原生 paid witness 发现并修复其攻击意图 cache 陈旧，强制非攻击行动后的原生 GoForTheEyes 支付0、伤害3、不附加 Weak，与模拟一致。

证据 `work/architecture-refactor-20261009/fixed-budget-performance-v3/state-diagnostics/p0-power-origin-v1/sealed/`；9 项独立物理 C# 故障及六 DLL/PDB 在相邻 `r2/proof.json`，6 新 predecessor/seam/target Python guard实际0→1→0，原13/8导入控制和既有原生/R2收据复用。419生成源仅上述2文件改变且与私有源字节完全一致。本项为聚焦原生/合同证据，完整门禁、Steam性能与胜率由根任务另行验收。

## 2026-10-10：滚球付费出牌后的两字段增长

CombatSolver 0.41.0 的 CardEffectSpecRegistry 已维护 Rampage 的当前伤害与降级恢复账，却遗漏多人限定 TheBall 的同形增长；既有本地 HandleTheBall 已维护队友选择、共享 CombatTargets RNG 与 Draw/Random 结果位置。当前 0.111.0 DLL 的真实付费 OnPlayWrapper 首次造成10伤害后原生 Damage20/ExtraDamageFromPlays10，而旧分支仍10/0；本轮只在 generic damage 后补这两次增长，写 COW MutablePreview，不修改传牌、身份、预算、路线或评分合同。

仅 `Prediction/CardEffectSpecRegistry.cs` 经 `scripts/import-combatsolver-kernel-41.py` 的具名 predecessor/seam/target 适配生成；实际 0.41 输入与当前/原生的逐行 S1 差量留在 `state-diagnostics/p0-the-ball-growth-v1/s1-side-by-side-v2/`。第二次独立实际导入419个C#，与 frozen-v9 仅该文件有规范化差异，目标两次生成及最终私有源码字节相等；不声称其它生成文件全部与既有 LF 基线字节相等。

当前 DLL 原生 paid play→队友 Draw→第二 paid play 验证伤害10再20、Damage20再30、私有增长账10再20、所有权/RNG/完整盘面与 Fork 隔离；实际 UpgradeInternal→DowngradeInternal 仍保留30伤害。一个物理 C# 故障撤去两增长写入后 RED1，精确源码与六 DLL/PDB 恢复 GREEN0；一个实际 Python target-checksum guard 撤去后接受坏 +1m receipt 而 RED1，精确恢复 GREEN0。原有矩阵不重复，完整门禁与新 combat016 Steam 保存入口复测由根任务接受；未证明总体胜率。


## 2026-10-10：B1 有限调用归属与 Preview 克隆采证

原生/上游 `PredictedCard.MutablePreview` 的初始克隆和共享写时克隆顺序保持：
克隆模型、替换 PreviewStorage、原牌堆缓存失效、观察者配置、变更通知。
仅在已启用 AllocationProbe 时记录这两处克隆与存储替换的同步当前线程分配，
与 CardFork、调用方 CardBranches 的包含关系明确区分；不可相加解释为独立总量。
非 Vendor 的 CardBranches 关闭探针时直接返回原 Core 迭代器，启用时每次 MoveNext 的
scope 在 yield 前释放，避免把消费者的分配计入调用归属。有限编译期 caller 分类不
输出文件路径、不按物理分支指针声称同根重复，没有新增机制状态或搜索政策。

授权来源：用户 2026-10-09 固定性能预算的 A10 实机优化；root 本批 B1 独占原生采证与物理 R2 验证。
启用的探针会增加计时成本，可能影响 wall-clock 截止，不预先宣称性能收益。
两处 Vendor span 由实际 0.41 导入管线的既存 retained support 适配生成；整份前驱、
唯一位置、目标 SHA256 均 fail closed。0.41 首次/独立导入均实际0，新增 target
guard 撤除的 Python R2 为0→1→0；CP936仅 stdout，JSON读取和生成源均 UTF8。
具名原生夹具实际验证 probe off/on 两 paid TheBall、Tactical 入口、取消/有序 choice 与
yield consumer 边界；caller/initial/shared 三接线各实际 RED1、六源与六 DLL/PDB
精确 restore GREEN0。不预先宣称实机性能收益，也不替代完整交付门禁。
证据入口：work/architecture-refactor-20261009/fixed-budget-performance-v3/b1-caller-preview-execution-v1/r2/proof.json；
导入/S1父证据：b1-caller-preview-preparation-v1/manifest.json。
原始RED：caller measurement must be disposed and published before yielding its first native branch；
first mutable preview must measure exactly one initial clone and storage replacement；
shared mutable preview must measure exactly one detach while preserving its sibling fields。


## 2026-10-10：多人原生历史攻击的 own-turn 与共享窗口

来源仍为 CombatSolver 0.41.0。真实当前 DLL 的 `HappenedLastPlayerTurn`
只以该席 own TurnNumber-1 判断，`HappenedThisTurn` 还要求 round/side
及整个席位 turn 向量相等。原 phase-one 对存活席提前 Commit 的本地
偏离在死亡席第二轮留下陈旧 removed attack，也在 Ambergris 额外回合
提前替换未推进席的 previous。本轮仅把 Commit 移至实际 Advance 集合：
normal 含死亡席，extra 仅参与席；未知模型/风险及其它回合顺序保持。

`.AutoPlay.cs` 以每席有限 pending/current/previous 保留原生已完成且非
dupe 攻击。冻结 origin round/side/turn 字典只在捕获时读取原生声明基类
私有 `_playerTurnNumbers` 与属性，之后 getter 只读分支 own turn。
非参与席的 pending 不因共享窗口关闭而丢失，真实 Enemy-side auto-play
仍可 current。窗口值不可变，`.Fork.cs` 单独拥有窗口字典并沿既有
ForkCard remap；fingerprint 与 Facts v4 保留 pending 卡、窗口及完整
physical identity。没有反射写原生数据或把不合格事实当成可复用。

AutoPlay 由既有 `kernel-history-capture-adaptations-41.json` 生成，Fork
由既有 `kernel-retained-support-adaptations-41.json` 生成；实际管线仍
先 retained 后 history，各自 exact predecessor/base/target 校验保留。
错误的 tiny positional history span 实际导入1已保存，改用该协议的
唯一全输入 old→new 后两个独立导入实际0；419个生成 C# raw bytes
完全相同，全 Vendor 相较 frozen-v11 仅上述两文件规范化改变。

实际原生 paid Strike→exhaust→RemoveFromCombat、SludgeSpinner RAGE
死亡与两次 normal advance、Ambergris extra（未参与席保留较早 previous）
及下一 normal、Enemy-side 完成窗口和原生 auto-play均有具名证人。
6个不同物理 C# 故障实际 Rebuild0/RED1/exact source9+six artifact
restore/GREEN0；旧 HistoryCourse removed replay 控制复用并再次实际0。
证据 `work/architecture-refactor-20261009/fixed-budget-performance-v3/
state-diagnostics/p0-history-domain-v12/sealed/manifest.json`。部分死亡后
随机 SLAM 模型仍严格不合格；native 语义和实体引用直接对照证明窗口，
未由此宣称全域 qualified、实机 P0 已消失、胜率或性能收益。


### 2026-10-11：Afterimage根内部状态与有界策略前缀

- `PowerPredictionStateSupport` 从原生 `AfterimagePower.Data.amountsForPlayedCards` 实际 Count=0 捕获独立空 `CardPlayPairPredictionState`；非空不制造 CardPlay 对象或身份。`KernelPlanningState.Facts` 登记所需状态，既有 Before/After 触发顺序及非空 Fork 拒绝保持。仅新增既有 retained 适配键；真实导入419×2与原生R2证据见 v13 root integration。
- `KernelTournament` 的本地多人暂停参考在截止时分开保存已结算前缀；检查点、逐动作席位/敌数组及结算边界必须完整。完整终局与已接受结果不被截断前缀替换，取消请求没有可部署结果。总预算和路线保持，不能以截断前缀宣称胜利；原生/R2及实机收益单独留档。


### 2026-10-11：怪物动态条件的完整状态合同

上游的分支ResolveConditional已按捕获的当前HP、Power、怪物字段或队友状态处理动态条件；本地完整Facts先前仍序列化旧根原生lambda求值。P0-SIM-26当前游戏四席原生STUNNED回调后，有效行动和RNG一致但该影子值失配。仅在本地Facts调用现有resolver形成有效ConditionalSelections，保留未知严格fallback、原有Rat有效权重、所有其他Static、当前行动、日志、生命周期与RNG；未修改Vendor或怪物预测，不增加接受豁免。付费原生、分支隔离和实际RED/GREEN封存；原有未覆盖HEADBUTT控制仍未qualified，完整回归/Steam结果另记。

### 2026-10-12：多人回合开始未回答菜单的恢复循环

本地 `ResumePreparedPartySetup` 仅在恢复前存在实际预选菜单时重新捕获并比较后续菜单。没有预选答案的普通/合同策略恢复一次仍 pending，则返回既有未完成结果，保留待选帧；原来的 32 次昂贵比较限制、自动策略和同步/延迟席位两处调用保持。根因是实际 `LiveEndTurnRisk` 使用默认关闭自动选牌的 session，旧循环在停止 prepare 后仍无限恢复同一 Tools 菜单。未改 Vendor、未提供虚构答案、未放宽完整状态或预算合同。

`PartySetupPendingNativeScenarios` 使用实际 sts2.dll、三次付费原生出牌、四席中三席初始 HP0 的构造根；不是历史 F45 隐藏根或 Kill 历史。基线实际 RED1 为 `resumes=8 stopped=True`；恢复后实际 GREEN0 为 `resumes=1 stopped=False`。清除 pending、拒绝真实 complete、禁止陈旧菜单重新准备三个故障均构建0/RED1；精确恢复后新场景及既有 1/2/4 席 GamblingChip → ToastyMittens 原生完整效果对照 GREEN0。命令、源码快照、退出码与原文见 `work/v21-party-setup-repair-20261012/commands/`；无断言的错误 case 名旧运行不计证据。完整门禁及新 Steam 保存战斗复测结果另外留档，构造回归不等于实机通过。

首轮完整门禁的新增观察器因小包装方法已被 JIT 内联而漏计（883 PASS/exit1），保留失败记录。实际预编译调用方可使旧观察点 RED1/resumes0；改观测非内联的实际 `ResumeExecutionContinuation` 后 GREEN0/resumes1，撤除生产 guard 又实际 RED1/resumes8。修正后重新完整验证，不用聚焦通过覆盖第一次失败。
