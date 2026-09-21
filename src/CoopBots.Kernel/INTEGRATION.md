# CombatSolver 内核接入进度（开发中，2026-09-13）

## 当前落地范围

- 267 份上游完整源码文件，加 1 个提取的进度 DTO。保留 Engine / Prediction / Search 与必要运行时支持，命名空间统一改为 `CoopBots.Kernel.Vendor`。逐文件来源记录见 `UPSTREAM.json`。
- 不加载原 CombatSolver 的 ModInitializer、界面、自动部署或联网功能。`CombatReplayOutcome.cs` 保留来源记录但不参与编译，它依赖原求解器控制器并含单人假设。
- `KernelSession` 在游戏线程捕获全队共享战场，分支复用原引擎的卡牌、遗物、Power、牌堆和资源结算。出牌候选从分支手牌重新枚举，因此包含抽到/生成后进入手牌的牌。
- `KernelTeamSearch` 在一个共享战场中交错搜索所有指定 Bot 的动作；由调用者提供团队评分，不再复制一套逐卡结算。不会自动把人类玩家加入出牌搜索。
- 可以在动作之间让出执行权；宽度、深度和节点数都有边界。每个片段必须在捕获线程执行，调用者必须验证根仍然有效。取消或过期会清空可部署动作。单次原生结算和捕获不可抢占，时间片不是单帧耗时保证。
- 不合法目标在执行前拒绝；未完成的选牌、回合结束、未补偿风险均作为明确边界返回。已经部分执行的失败分支不能继续出牌或 Fork。未知异常向调用者抛出。
- 捕获、Fork、出牌处于线程局部通知隔离作用域，专用 Harmony 补丁抑制模拟通知进入真实战场 UI。尚未证明整个第三方 Mod 栈的隔离性。
- 出牌镜像抛出的异常按 `prediction-exception` 边界失败关闭；调用方在边界命中后整场停用内核，避免重复执行注定失败的搜索。
- 多人卡牌审计（preview.23）：内核引擎层具备队伍感知结构（`GetTeammatesOf`、`AnyAlly`/`AllAllies`、按生物攻击历史、按所有者资源）。20 张协同卡中 8 张内核可解（TagTeam/GangUp/Rally/Mimic/HuddleUp/FightMe/SicEm/Bodyguard），11 张返回边界并由多人规划器接手，未见静默按 0 计价。`MODELED` 仅表示上游未报缺口，不等于多人语义已逐卡差分验证。
- 多人卡分级与建模（preview.24）：分级依据"队友语义是否已由现有钩子承载"。A 级已建模：EnergySurge、BelieveInYou（即时回能，`CardEffectSpecRegistry` 资源结算）、Sneaky、BeaconOfHope（`AfterCardPlayed`/`AfterBlockGained` 钩子已存在，仅补 OnPlay Power 施加）。放弃：Flanking、OneForAll、Intercept（缺伤害加成/重定向钩子）、Concoct、Tutor（需选牌）、Knockdown（加成伤害无钩子，仅施加 Power 会低估）、DemonicShield（需 CalculatedBlock + 队友格挡结算）。被放弃的卡保持边界→整场回退，不静默计价。相关适配已写入导入器并在锚点变化时报错。
- 共享减益的团队计价（preview.25）：`KernelCombatEvaluation` 按每名存活成员分别估算来袭伤害再求和，故虚弱/减力量按人数放大（实测 3 人 vs 1 人为 3:1）；`SoftFinish` 在敌人有易伤时放大真人收尾射程，把易伤对真人攻击的增益计入团队评分。来袭伤害按"每个敌人攻击命中每名存活成员"建模，与既有规划器一致。
- 遗物的团队计价（preview.26）：战斗内敌方力量走与虚弱/减力量相同的按成员伤害路径，Brimstone 在 `SimulatedCombatState.RelicTurnStart` 已正确建模（持有者+2、存活敌人各+1）。局外 `HumanCoopAdvisor.RelicValue` 新增 Brimstone/PhilosophersStone 的净团队估值：个人收益封顶、敌方力量按全队人数计负面，商店/宝箱/分配与真人建议共用，避免 Bot 无脑拿全队负面遗物。
- 敌方强化机制的团队计价（preview.27）：实验体 Enrage（任一角色打技能牌→Boss 加力量）在 `AfterCardPlayedMirrors.HandleEnragePower` 已镜像，实测技能牌触发代价 3 人 6.0 vs 1 人 2.0，按人数线性；FightMe 的敌方力量走同一路径。单回合搜索不推演力量未来累积；敌人本回合非攻击意图时，Enrage 叠加的力量在本回合评分中无直接代价。
- 边界判定收窄（preview.36，P1）：`KernelSession.Play/UsePotion` 只在"**被使用对象自身**的 OnPlay/OnUse 未建模"（`gap.Method` 与 `gap.SourceId` 匹配该牌/药水）时判为边界；敌方 Power/遗物等环境类未建模钩子降级为估算，不再让该动作从搜索中消失。此前任何未补偿缺口都会导致该出牌被跳过，敌人带未建模被动时 Bot 会拒绝攻击。Boss 反应机制（尖刺反伤等）已由 `BeforeDamageReceivedMirrors` 等镜像覆盖并有回归锁定。
- 多人格挡缩放修复（preview.47）：上游 `ModifyBlockMultiplicativeMirrors.HandleMultiplayerScaling` 对 `playerCount != 1` 抛 `NotSupportedException`，导致真实多人下**所有格挡牌**成为边界、内核无法模拟防御（日志实证：大量 `DEFEND_*:prediction-exception:NotSupportedException`）。现改为镜像原生公式（玩家格挡不缩放；敌人格挡 `players<=2 ? players : players * GetMultiplayerScaling(Encounter, act)`）。导入器已加锚点守卫，重新导入可复现。
- P1-2 跨回合可执行搜索（preview.45）：`KernelTurns.EndTurn(player, maxRounds)` 在结算敌方阶段后按预算调用 `StartNextPlayerTurn`（回合数、回合开始遗物/能力、清格挡、能量重置、抽牌、自动前置出牌、`TriggerAfterPlayerTurnStart`），随后 `ready.Clear()` 并 `EnemyPhaseCompleted=false`，搜索继续第二回合；`KernelTeamSearch.Options.MaxRounds`（默认 2）为上限，达上限则以 `EnemyPhaseCompleted=true` 作为终止结算节点。四项修正：`nextIncoming` 仅在未结算且 `RoundsAdvanced==0` 时计入；未建模非攻击怪招写 `roundBoundary`（`enemy-move-unmodeled:...`）返回边界而非抛异常；`bestResolved` 仅在分数不劣时采用；开启 EndTurn 时 frontier 保留一个结束回合的探索位。第一回合仍用实况意图，`RoundsAdvanced>0` 才改用分支 `CurrentAttacks()`。
- 双阶段调度与多回合投影（preview.35）：`KernelCombatPlanner.Poll` 接收 `humansFinished`：未结束时 Depth9/Width8/768、200ms，全结束后 Depth16/Width12/3072、1200ms。`KernelSession.ForecastRounds(live, n)` 取代单回合 `ForecastNextRound`，`KernelCombatEvaluation` 按 0.5 衰减对多回合敌方攻击加权（未结束 2 回合、结束后 4 回合）。**完整敌方回合动作搜索仍被阻塞**：`MonsterMoveSemantics.ApplyForecastMove` 只接受单个玩家目标，多人下同一意图命中全体；需在 Kernel/Vendor 层实现多目标结算（攻击逐成员、非攻击效果一次）与团队 `EndRound`，才能把 EndTurn 作为搜索动作。
- 药水并入联合搜索（preview.34，S07）：`KernelTeamSearch.Action` 增加可选 `Potion`，`Expand` 在卡牌之后枚举 `UsablePotions`×`PotionTargets` 并 fork→UsePotion；规划器若判定最优计划首动作为药水，则以 `PotionPlan` 作为免费动作返回，运行时执行后重规划。旧的外层单点药水探测（`EvaluateConfirmedPotion`）已被联合搜索取代并移除。救援药水仍由 `BotPotionPlanner` 优先处理。
- 主动药水确认（preview.33）：`TeamPotionMirrors`（非 Vendor，随源码保留）为 Strength/Dexterity/Focus/Vulnerable/Energy 药水补 OnUse 镜像并注册进 `PotionOnUseMirrors`；`SimulatedCombatState.FindPotion` 把 live 药水映射到模拟克隆（导入器有守卫）。`KernelSession.UsePotion` 在分支内用药并结算待处理 Power 数值变更。`KernelCombatPlanner` 对候选 fork→用药→短搜索，仅在"新达成击杀/减少死亡"时给出药水方案，作为免费动作交运行时执行。药水与出牌序列的联合求解（S07）仍未做。
- 部件化 P0 强度优化（preview.32）：未建模卡只从搜索中剔除，不再整场停用内核（`KernelCombatPlanner` 边界分支只报告，不禁用）。跨回合方面，`KernelSession.ForecastNextRound` 用上游 `IntentForecaster.Build(live, 2)` 取下一回合的敌方攻击基础伤害，`KernelCombatEvaluation` 以 0.25 权重计入团队战损（折价：下回合全队会重新抽牌/格挡）。**完整敌方回合动作搜索仍未接入**：上游 `CombatBeamSolver.AdvanceRound` 为私有且强绑定单人求解器的 `_run`/`_player` 状态，直接复用会把多人语义带错，后续需在 Kernel 层单独实现回合推进（对应 S03）。
- 规划器活性不变量（preview.28）：`KernelCombatPlanner.Poll` 在一次搜索完成后只返回 Ready（必带决策）或 Fallback，绝不返回 Pending；无推荐动作时交回旧规划器；实况根连续两次失效则本场停用内核。`BotRuntime` 只要内核未产出可用计划就回退旧规划器。
- 在途搜索不得被瞬时忙碌打断（preview.29，根因修复）：内核搜索跨帧展开，`BotRuntime` 原在"动作队列非空/有动作执行"的帧上 `KernelPlanner.Reset()`，导致搜索永远推进不完、每帧 Pending、Bot 整场不出牌。现在这类瞬时忙碌只跳过本帧，搜索存续，由 `Poll` 的实况根校验决定是否回退；另修复选中牌提交前失效时的静默 `return`（改为记录、重规划、必要时结束回合）。diagnostics: `CoopBots idle: kernel=..., legacy=..., humansFinished=..., paused=...`。

## 与游戏运行入口的关系

**已切换 BotRuntime，preview.21 起随包发布。** `BotRuntime` 每帧调用 `KernelCombatPlanner.Poll` 作为主战斗决策；内核返回 `Fallback` 或遇到边界时，改走原 `TeamCombatPlanner` + `BotBrain` 回退路径。主项目通过 `ProjectReference` 引用内核，`ModEntry` 启动时用 `KernelAssemblyLoader` 加载同目录的 `CoopBots.Kernel.dll`，`build.ps1` 会把该 DLL 一并打包。

由于内核编译期静态引用了 `STS2-RitsuLib`，运行时必须存在 RitsuLib；`mod_manifest.json` 已声明 `STS2-RitsuLib >= 0.5.20` 依赖。不随包分发 RitsuLib 或其他第三方二进制。

新增测试通过证明引擎分支和团队动作搜索已运行，不代表卡牌/遗物已全部在实战中覆盖，也不代表 A10 胜率提高。已导入原跨回合求解源码，但新的团队搜索尚未接入敌方回合、多角色结束回合或长期战略评分。

## 本轮验证

在游戏 0.111.0 引用与现有 PatchSmoke 控制台环境中运行：

- 多 Bot 顺序：B 有三张打击、A 有重击，即使先枚举 B，也选 A 重击后 B 三张打击，共 35 伤害，而非先各自出手。
- FiendFire：消耗自己的两张手牌后造成两段 7 伤害，自身一并消耗；队友手牌、原始战场、兄弟分支不变。
- Offering：损失 6 HP、增加 2 能量、抽 3 张；搜索发现抽到的后续攻击，并在测试团队评分中扣除生命代价。
- BattleTrance 后 Offering：抽牌堆仍有牌，NoDraw 确实阻止后续抽牌。
- Shuriken：继承真实的两次攻击计数，第一次攻击后增益只作用于持有者后续攻击，队友不误享受力量；真实计数和 Power 不改变。
- VoidForm：明确返回 player-turn-end；失败分支不可 Fork；搜索结果包含边界诊断，不能把它当作成功路线部署。
- 无效友方攻击目标、节点预算、取消、过期根结果清空。
- 同轮运行现有重连、多玩家协作、节奏、团队集火、事件/商店等回归，以及现有 47 个补丁入口检查。日志：`outputs/kernel-team-tests.log`。

控制台仅替换本地化文本格式化，不替换卡牌数值、伤害或遗物效果。本轮没有真实游戏动画、多机测试或 A10 胜率测试。

```powershell
.\work\dotnet-win\dotnet.exe run --project tests/PatchSmoke/PatchSmoke.csproj -c Release `
  '-p:STS2DataDir=B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64' `
  -p:EnableKernelTests=true -p:NuGetAudit=false
```

RitsuLib 默认从相应 SteamLibrary 的 workshop/content/2868840/3747602295/lib/0.111.0 解析，其他安装位置通过 `-p:RitsuLibDir=...` 指定。无二进制再分发。实验项目目前以本地游戏 DLL + Publicizer 构建，未验证 macOS 路径及引用包构建。

## 接入默认游戏决策前剩余工作

1. ~~把现有的人类生存优先、有限 Bot 死亡代价、集火记忆、沙虫即死保护和药水救援评估迁入 KernelSession 的团队评分~~ **已完成**：`KernelCombatEvaluation` 计真人死亡 ×1e9、Bot 死亡 40+上限25%、`TeamFocus` 集火、`SandpitDeath`/`SandpitReserve`；救援药水仍由 `BotPotionPlanner` 在搜索之外评估。**另加"真人可收尾"软项**（preview.22）：用 `TeamCombatPlanner.AttackPotential` 估算真人剩余伤害，仅对 Bot 无法独立斩杀的敌人、在进入真人打击范围时加分，纯加法且不产生确认斩杀。
2. 把多人原生选牌与动作身份映射接入模拟选择分支和真正的部署选择处理；结束回合必须按角色处理，不能沿用全局单人请求直接推进团队回合。**当前遇到 `pending-choice` / `player-turn-end` 边界即整场回退旧规划器，尚未在内核内解决。**
3. 接入运行时分片调度与真实状态版本校验，覆盖人类/其他 Bot 行动、牌堆及资源变化、暂停、回合边界、重连和战斗切换；`KernelCombatPlanner` 已校验战斗对象/回合/动作队列版本/集火/实况版本号并按 4ms 分片让出，但单元测试只验证调用者发出失效信号后的契约。
4. 明确失败/未知语义的旧规划器回退条件，记录边界和耗时；避免新旧规划器每帧重复求解。**已完成基础回退**：边界命中或异常时标记本场 `Fallback`/禁用内核，回退旧规划器，并限频记录耗时与边界。
5. 对 Ritsu 免费费用状态、克隆扩展和其他已加载 Mod 的订阅回调执行隔离验证。**依赖声明与打包 DLL 已完成**（`STS2-RitsuLib >= 0.5.20`），但隔离性仍未在完整第三方 Mod 栈上证明。

## 导入适配维护

`scripts/import-combatsolver-kernel.py` 保留上述来源记录。除命名空间替换外，已将诊断用玩家名字改为 NetId，避免模拟捕获依赖平台服务。该适配已写入导入器并检查原始表达式恰好出现一次；上游变化会要求人工检查。

其他本地适配位于 Vendor 外部。授权/署名沿用根目录 `THIRD_PARTY_NOTICES.md`；原始 notice 保持不变。

## 能力路线部分同步（solver-power-sync，2026-09-18，**已被取代**）

- **取代说明（2026-09-19）**：本节讲的是"把 0.41.0 的策略层部分回移到 0.33.9 引擎"的过渡做法。
  现在基础引擎本身就是 `UPSTREAM.json` 记录的 **0.41.0**（整体重导入，见 `VENDOR_DEVIATIONS.md` 的 D1），
  "回移"不再必要。`Vendor/PowerSync/**` 与 `POWER_SYNC.json` 保留，作为该阶段的历史记录。
- 范围：把 CombatSolver 0.41.0 的**纯策略层**部分回移到 0.33.9 引擎上的生产路径（`KernelTeamSearch`），不是整体替换引擎，也不包含 0.41.0 的逐卡投影与单开能力前缀组合反事实。来源与文件清单见 `POWER_SYNC.json`。（当时的基础引擎是 0.33.9。）
- `Vendor/PowerSync/**` 逐文件命名空间适配（`CombatSolver` → `CoopBots.Kernel.Vendor.PowerSync`，正文不变）：合同、准入、承诺记录/生命周期/席位配额/启动投资，以及六卡池逐卡路线政策（共 104 张，Ironclad 19 / Silent 17 / Defect 20 / Regent 18 / Necrobinder 18 / Colorless 12）。未知卡或上游 `NoInCombatCommitment` 卡不产生描述符。
- `KernelPower.cs` 适配当前按玩家划分的 `KernelSession`：承诺按 `Player.NetId` 归属，随出牌/药水/结束回合沿分支传递。准入只认“本主自己的分支上可观测到的 `PersistentValue` 增量或即时格挡增益”，因此未建模/无触发能力不会凭空开路；Silent 的 Shiv/Block 触发只看该玩家自己的牌堆与 Power，不看队友。
- `KernelTeamSearch` 在宽度内用上游 normal 席位配额保留承诺代表（至少一半普通席位、最高分普通线优先保护，每个 owner 先占一席避免独占；共享节点只占一席并代表其上所有 owner，其次才给同一 owner 第二个席位；`after.HasWon` 分支先清空承诺），中间内存裁剪与最终 frontier 使用同一套保护；带承诺节点的去重键包含完整决策相关承诺状态（owner、family、cards、priority、opened turn/action/history、transition、last evidence、investment、remaining potential、progress/realized、power count）。承诺不进入 `Score`，`Complete` 排序、取消/过期、药水/选择/结束回合语义均不变。
- 未移植（诚实边界）：逐卡 `Projection/*`、完整单人开局能力前缀组合（上游每路线 ≥25000 节点 / 10 秒）、0.41.0 Engine/SimulatedCombatState 增量、自动出牌历史检测。需要这些的能力（如要求正投影的卡）保持旧行为，不创建承诺。
- 验证入口：`scripts/verify-solver-power-sync.ps1`（构建 `EnableKernelTests=true`，运行 `--kernel-power-only`，不部署）；生产行为回归在 `tests/PatchSmoke/KernelPowerRouteScenarios.cs`。
