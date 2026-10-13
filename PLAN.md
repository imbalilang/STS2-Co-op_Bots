# PLAN.md — CoopBots 全局规划

> **这是全局规划，不是权威规范。** 铁律、验证门、Skills 注册表、偏离登记表在
> [`AGENT.md`](AGENT.md)；冲突时以 `AGENT.md` 为准。
>
> **这份文件回答三个问题**：目标是什么、现在走到哪、下一步做什么。
> 每一项都有**编号**、**验收判据**和**证据位置**，让任何一次 session 都能接着往下做。

状态以两个地方为准，不要在本文件里手抄进度：

| 机器可读 | 人读 |
|---|---|
| `work/agent-state/state/project-state.json` | `AGENT.md` §7（活文档，每轮更新） |

```bash
python scripts/state.py show          # 一屏：阶段、最近门禁、活跃 backlog、最近实机
```

**社区克隆的证据边界（2026-10-13）**：上表与 `state.py` 读取维护者/使用者的本地生成状态，`work/` 不随 Git 分发，不能用本机状态替新克隆认证。公共当前状态与精简身份摘要见 [`docs/STATUS.md`](docs/STATUS.md) 和 [`docs/evidence/v21-20261012.json`](docs/evidence/v21-20261012.json)。贡献者和 Agent 的入口见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。该次发布为源码开发快照，不是 50% 胜率验收或新的 Steam 二进制版本。

---

## 1. 目标

**当前目标（用户 2026-10-09）：固定性能预算下，全 Bot A10 通关率达到 50%，并改善真人与 Bot 协作质量。**

已授权按分阶段架构重构执行，路线选择保持现状，保留已验证原生模拟及多人适配，不整包替换新版单人 CombatSolver。验收同时关注完整爬塔结果、合法执行与模拟一致性、等待时间/分配/帧影响；回归全绿或完成更多终局不能替代胜率。

本轮实施顺序：

1. 冻结验证过的版本、双 DLL、包、游戏与路线输入，预留开发与留出种子，建立可恢复的完整 A10 配对测试及局内/构筑 2×2 采证合同；卡死、异常和外部停止保留在计划尝试分母。
2. 统一稳定根代次、搜索请求截止时间、任务取消及有界工作池；参考路线、联合搜索、药水与选牌续搜使用请求剩余额度，不新增独立预算池。
3. 建立含牌堆、随机数、临时状态、选牌与控制方式的状态证据合同。全 Bot 保留整战联合计划；混合桌根据已结算真人输入更新实际根，有限复用匹配后继，真实模拟分歧仍按 P0 修复。
4. 共享可证明机制事实，用有限原生战斗采证校准构筑代理模型；先采证和消融再改默认权重，不为每次奖励运行完整求解。

本轮证据目录：`work/architecture-refactor-20261009/`。冻结基线 `baseline/receipt.json` 已确认 1231 个源码输入、完整四门与已安装双 DLL。开发三种子与留出十种子由 `benchmark/seed-reservation.json` 固定；扫描范围内未发现历史使用记录，不推断删除过的或范围外历史。上述是实施与验收目标，尚未证明达到 50%。

2026-10-09 最新旧基线实机 `NPYVL1JKFQG2` 的复核见 [可行性报告](work/architecture-feasibility-20261009T041107Z/feasibility-review.md)。11 次胜利预测实际获胜，末战团灭预测实际团灭；本局未记录具名漂移，不能评价尚未部署的重构，也不能估计总体通关率。后续在保留上述四阶段的同时，按以下顺序补齐收益验收：

1. 完成当前请求、状态合同的功能验证与完整四门，保留原生完整参考。
2. 先在保存的完整原生入口做同根成本与质量配对，实测原子动作尾耗时、帧影响、并发、分配/GC、重放和精确缓存命中。6ms 软切片、统一截止和共享线程池不单独构成卡顿解决证据；并发与可选 refinement 调整须兼顾实际完整候选质量。
3. 将固定预算的有效候选覆盖与低战损作为局内强度交付，优先采证本局20/23/26层的启动、防御、多人顺序和药水。最新 run history 缺少完整入口，不能伪装成已完成同根反事实。
4. 将构筑机制采证扩展到真实奖励/事件上下文与毒、小刀持续组合，证据支持后改代理、先验尺度或合法性政策，再做局内/局外消融。采证接口不自动改变选择，不能单独声称提高胜率；路线保持现状。
5. 性能与同根质量过关后做完整 A10 固定种子配对和独立混合桌测试，记录胜率、分幕生存、累计战损与卡死。现有十种子留出是筛查规模，不把5/10当作总体50%已获证明。

近期先将已有正确性改动验证到可出测试包，再优先复现卡顿及真实高战损节点；非现场、非高频机制的通用接口扩充后移。新发现的真实模拟分歧仍按 P0 处理，新增优化用固定预算下的成本或生存收益验收。

用户已明确授权持续实机测试、自动分析与优化直到胜率达标。循环状态在 `work/a10-optimization-loop-20261009/loop-state.json`，当前任务接续 `coopbots-a10` 已启用。每轮使用具名本地测试包和不可变版本/双 DLL/种子身份；完整门禁后部署、经 Steam 测试、归档复盘，再实施有证据的优化。不同候选版本不混合统计胜率；已用于优化的留出种子转为诊断证据，新候选重新预留确认批次。筛查与确认分开，预先固定确认样本和停止规则，报告置信区间与异常/卡死，未达标继续推进，不自动发布工坊。

以下 V20–V31 记录是历史实施证据，不能替代本轮冻结版本的配对结果。

0.39.1 已发布；稳定通关尚未验证。当前进度、证据边界、实施成本与下一批测试设计见
[`docs/OPTIMIZATION_PLAN_0.39.1.md`](docs/OPTIMIZATION_PLAN_0.39.1.md)。下一步优先补齐构筑机制估值与联合搜索候选质量，通过分别改构筑、改出牌的对照判断收益。

V20 已完成首轮结构改动和自检，见 [`docs/OPTIMIZATION_V20_SELF_CHECK.md`](docs/OPTIMIZATION_V20_SELF_CHECK.md)。V21 已完成 8 场沙漏对照与 2 个原生房间；调整卡组四场均击杀，原卡组基线两败、候选版一胜一负。新联合搜索 44 次只有 1 次产出完整候选，下一步先修复预算调度并加入真实复杂度回归，再决定六局完整 A10。详见 [`docs/OPTIMIZATION_V21_TEST_RESULTS.md`](docs/OPTIMIZATION_V21_TEST_RESULTS.md)。本批自动测试已结束。

V31（2026-09-29）按**实机出场次数**补三个事件（T4 那批实机 0 次出现）：`SELF_HELP_BOOK`(8)——附魔 id 藏在 StringVar 里（SHARP/NIMBLE/SWIFT），效果从游戏文本读出后按 `MarginalCore` 权重定价，**牌组没有该牌型的一页记 0**；`STONE_OF_ALL_TIME`(8)——药水换最大生命 vs 掉血附魔 Vigorous，两侧都定价；`WELCOME_TO_WONGOS`(8)——商店，价格取自事件变量，**`LEAVE` 会把一张已强化的牌降级所以走人也有成本**，没有预测器的货架不盲买。四道门 267/428，见 [`docs/OPTIMIZATION_V31_TOP_EVENTS.md`](docs/OPTIMIZATION_V31_TOP_EVENTS.md)。**尚无实机复验**；T6 代价侧 / `TRIAL` / T5 其余未做。

V30（2026-09-29）事件 T4：`EventTrade` 加 `BestUpgrades`/`PricesCurse`/`GrantsWithCurses`（诅咒只在选项没自己计价时才补收，避免收两次），新增 `LOST_WISP`/`SPIRIT_GRAFTER`/`DROWNING_BEACON` 三个 handler，删掉内联的 DROWNING_BEACON 特判。**`TRIAL` 明确不做**：选项 id 跨页重复、本地化文本与预测器对其 INNOCENT 的描述互相矛盾。四道门 265/426，见 [`docs/OPTIMIZATION_V30_EVENT_T4.md`](docs/OPTIMIZATION_V30_EVENT_T4.md)。**尚无实机复验**；按出场频率，仍未被建模且实际出现过的是 `SELF_HELP_BOOK`(8)、`WELCOME_TO_WONGOS`(8)、`STONE_OF_ALL_TIME`(8)（T5/T2/T6）。

V29（2026-09-29）事件 T2：内核注册了 **31 个**事件预测器而当时只有 11 个 handler——"随机结果"一直读得出来。新增 `EventTrade.Grants`（具名 hover tip + RandomForeseer 预测合并、按 Id 去重），**通用打分器与全部 handler 共用**，于是"随机遗物/药水/卡牌"的选项按实际会给的模型比较；`THIS_OR_THAT`/`UNREST_SITE`/`WELLSPRING` 的既有预测器自动生效。另加 `BATTLEWORN_DUMMY`、`BRAIN_LEECH` 两个代价侧 handler。四道门 264/425，见 [`docs/OPTIMIZATION_V29_EVENT_T2_PREDICTION.md`](docs/OPTIMIZATION_V29_EVENT_T2_PREDICTION.md)。**尚无实机复验**；T4 剩余 / T5 / T6 未做。

V28（2026-09-29）事件覆盖率：全事件扫描为 56 事件 / 11 handler / **41 个零信号**（选项 id 不含任何关键词，靠页面顺序决定）。① `BakedEvents` 按本机安装版 `.pck` 补齐 3 个缺失选项 id，并把 `NEOW`/`OROBAS`/`PAEL` 具名登记为**池基事件**（选项是运行期遗物 id，静态表列不出）。② **T1** 通用打分器对具名卡牌改用 `BuildValue.Add`（×0.25），不再"非 Power 一律 0"。③ **T3** 新增统一单位 `EventTrade` 与 6 个资源交换 handler（`SUNKEN_STATUE`/`SUNKEN_TREASURY`/`JUNGLE_MAZE_ADVENTURE`/`THIS_OR_THAT`/`UNREST_SITE`/`WELLSPRING`），数值取自事件 `DynamicVars`。四道门 262/423，见 [`docs/OPTIMIZATION_V28_EVENT_COVERAGE.md`](docs/OPTIMIZATION_V28_EVENT_COVERAGE.md)。**尚无实机复验**；T2/T4/T5/T6 未做。

V27（2026-09-29）给「光与暗之门」补上建模：该事件原本没有 handler，两个选项 id 不含通用打分器的任何关键词，双双得基础分 5.0，**并列后按页面顺序**选中了"随机升级两张"而不是"自选删一张"。新 `DoorsOfLightAndDarkHandler` 用 RandomForeseer 预测（克隆 RNG）读出**这局实际会被升级的两张**逐张计价，与 `BuildValue.Remove` 的最优删牌目标比大小；夹具实测 6.6 vs 75.0。四道门 259/420，见 [`docs/OPTIMIZATION_V27_DOORS_EVENT.md`](docs/OPTIMIZATION_V27_DOORS_EVENT.md)。**尚无实机复验**。

V26（2026-09-29）修构筑选牌估值：实测第一幕 Boss 后跳过稀有卡，核对发现被跳的牌社区 Elo **高于**实际拿下的牌；根因是"读不懂文本的牌被 cycle 估计当空白牌"+ 稀释税按张数收且与牌无关。新增 `BuildCoverage`（覆盖率加权、缺证据不得变成否决）、**成型度取代张数**（未成型不收张数税，`N>32` 兜底）、撤销机会按整局算；重写了「20 张起手牌组里的大路货必须低于 skip」为「**成型**后的大路货需要 Skip」。四道门 258/419，见 [`docs/OPTIMIZATION_V26_BUILD_DECISION.md`](docs/OPTIMIZATION_V26_BUILD_DECISION.md)。**尚无实机复验**。

V25（2026-09-29）已落地 V21 的前置条件 #1：联合续演的参照路线（最佳前缀 × Tactical）不再被单任务时间片切掉，其余任务按"打算比较的打法数"分摊；触发证据是 V24 实机（种子 `9F4Q3R2BV8HQ`，A10，第三幕团灭）中 42 次联合搜索有 38 次缺少 `Tactical:`。四道门 256/417，见 [`docs/OPTIMIZATION_V25_JOINT_BUDGET.md`](docs/OPTIMIZATION_V25_JOINT_BUDGET.md)。**尚无实机复验**；V21 的四场最小复测与六局 A10 仍未启动。

**以下为早期执行管线目标及历史验收背景。** 当前已实现多人联合搜索与必要的重新规划；不能仅凭剧本覆盖率或存活回合数判断强度。

早期目标：局内开局做一次长搜索，然后严格按搜到的剧本打完本局剩下的战斗。

对标 CombatSolver 0.41.0。衡量方式不是"Bot 看起来聪明"，而是三条可判定的读数：

1. **执行率**：每场 `planRefusals=0 / drift=0 / missing=0 / noScript=0 / exception=0`；
2. **结束方式**：整局以 `How did the game end??` 收尾（而不是被看护器杀 = 卡死、或外部退出 = 数据截断）；
3. **爬升**：到第几层、是否通关。

**取巧的形态**是"不杀只防、战斗无限拖"——它能让 1 达标却让 2 失败。所以三条必须一起看。

---

## 2. 阶段

> 下表及其性能读数保留为早期阶段记录；最新阶段安排以第 1 节链接的 0.39.1 计划为准。B1–B11 编号不变，逐项复核后再更新状态。

| 阶段 | 内容 | 状态 | 判据 |
|---|---|---|---|
| **P0 管线** | 搜索 → 部署 → 跨回合 → 继续执行 的完整通路 | ✅ 完成 | live12 的 22/22 场执行率 100% |
| **P1 限步前瞻** | `LookaheadRounds=5`，预算 3 分钟 + 5×1 分钟；一局 = 一串连续剧本 | ✅ 完成 | 实机 `plans` 每场 2–9（段链在跑） |
| **P2 多人强度与成本** | 4 席 A10 下把成本压下来，让强度成为唯一瓶颈 | 🔵 **当前** | 见 B1；成本不降则地平线无法延长 |
| **P3 建模覆盖率** | 未建模的牌/药水不进剧本，把"边界"从时间轴级降到单件级 | ⬜ 未开始 | 见 B7 / B10 |

**P2 为什么是当前**：4 席 A10 已经**打满预算**——`wall` p90 174s（基础预算 180s）、
`nodes` max 110k/120k、`alloc` max 29GB、`gcPause` p90 31.6s。**在成本降下来之前延长地平线不可行**，
所以 B1（分配/GC）是 P2 的闸门，不是"顺手优化"。

---

## 3. Backlog

编号稳定，不要重排。`⚠` = 有未验证的假设或未修的已知缺陷。

### B1 · 分配 / GC（P2 闸门）🔴

- **现象**：单次搜索 `alloc` 最高 13GB（4 席下 29GB）；`gcPause` 占 `compute` 的 15%–57%。
  实测 live6 第 5 轮在 floor 6 只打 4 场就卡死 —— 主线程被超长 GC 卡住时**连 45s 心跳都发不出来**，
  看护器正确判死。
- **判据**：`gcPause / compute` 降到个位数百分比，且 4 席 A10 的 `wall` p90 明显低于预算。
- **注意**：先分清"静默"与"没在搜"是两件事。`search slice overran` 已经在打，先读它。
- **证据**：`search-complete … gcPause=… (…%), gen0=…, alloc=…MB`

### B2 · `occurrence` 回退 ⚠

- **现象**：`card-left-hand` 的疑似根因 —— 同状态键的**序号错位**。
- **状态**：本轮 18 场已建模战斗里**一次都没复现**。**先别急着改**，等再次复现拿到现场。
- **判据**：要么拿到可复现的现场，要么明确降级为"无法复现"并撤案。

### B3 · `plan step drift` 只打第一个字段

- **现象**：诊断只输出 `field=hp`，后面的差异被掩盖。
- **判据**：一次漂移能列出全部差异字段。
- **属于 R5c 家族**：把信息量不足的诊断补足，否则它等于没人读。

### B4 · `LIVING_FOG_NORMAL` 的 `missing=7` ⚠

- **现象**：`missing=7` 而 `refusals=0`。疑似"剧本比战斗长"。
- **判据**：判定为良性（写进 §6 偏离登记）或又一个静默跳过（按 R5 修）。
- **注意**：`missing` = 搜索没能重放出预测，不是拒绝。**别拿 `refusals=0` 给它脱罪。**

### B5 · 重放 `invalid-target` ⚠

- **现象**：某步在重放里多打死一个敌人。
- **判据**：找到具名守卫并给 R2 变红原文。

### B6 · `build.ps1` 的哈希只覆盖 `CoopBots.dll`

- **现象**：内核变更时 `build.ps1` 打印的 SHA256 **不变**，无法识别包。
- **判据**：识别包改用 zip 自身的 sha256，并在 `build.ps1` 里打印它。
- **捷径**：现在就可以先用 zip sha256 人工识别，不必等修。

### B7 · 未建模的多人牌 10 张

- **现象**：显式跳过中，`COVERAGE UNMODELED` 每轮都在打。
- **判据**：unmodeled 归零，或每一张都有登记（§6）+ 一行"为什么可以跳过"。
- **注意 R5c**：`COVERAGE BROKEN` / `UNMODELED` 这类**每轮都出现**的行，
  要么修掉、要么登记为"已知无害"。见 `all-for-one` 的教训（一个 `case` 标签丢了，
  这张牌从来没能进过任何剧本，而被当背景噪音读了一整个 session）。

### B8 · 搜索跑空时仍会从头重搜

- **现象**：`stop=frontier-empty` 后仍从头重来 —— 确定性重复，纯浪费。
- **判据**：同局面同预算下不重复搜出同一个空结果。

### B9 · act3→act4 之后不再投票 ⚠（仪器已加，等实机分辨）

- **现场**：`room=Map roomStack=1 floor=48 mapOpen=True mapTravel=True`，守卫全过却 4 分钟不投票；
  `generation 4` 在整份日志里出现 **0 次**。
- **两个嫌疑**：① `TryVoteOnMap` 的 `if (GetVote(player).HasValue) continue;` 只看"有没有票"不比对
  `generation`，act 切换若不清票则旧票让席位永远被跳过；② `RoutePlanner.Plan` 返回 null。
- **判据**：实机读到 `CoopBots map: waiting — <why>; generation=N` 后按结论修（两者修法都很小）。
- **仪器是零行为改动**，只用一轮实机即可分辨。

### B10 · 药水"选择"通路

- **现状**：九瓶"需要选择"的药水（`PotionChoiceSupport.RequiresChoice`）一律判 boundary，不进剧本。
  这是**正确**的，因为搜索建药水动作时 `Choices` 恒为 null，计划无法说明该拿哪张牌。
- **真正的修法**是打通四段：① 搜索侧捕获选择 → ② `UsePotion` 应用 → ③ 部署侧携带 →
  ④ **运行期用它回答实盘选择**。**缺的是①**，因为 `BotChoicePlanSync.Select` 的守卫只认 `PlayCardAction`。
- **判据**：`DropletOfPrecognition` 这类药水能进剧本且实机不再产生 `card-left-hand` 拒绝。

### B11 · 两阶段 boss 的 pending choice 截断 `fail-closed` ⚠（仪器已加，未修）

- **根因链（已端到端验证）**：`WaterfallGiant` 打到 0 血不会赢（`SteamEruptionPower` 回满血 + 切
  `ABOUT_TO_BLOW_MOVE`）→ 内核**建模了**这一转换 → 但转换后那句 fail-closed
  （`CorePowerSupport.cs:661` `if (simulator.HasPendingChoice) return false;`）**把整条线截断**。
- **后果**：任何"打死这个 boss"的线都走不到底 → 搜索最好的线只剩"活过 5 回合" →
  **每段重搜都得出同一结论 → 战斗无限拖**（实测 51 张 DEFEND vs 36 张 STRIKE、25 回合 64 个剧本）。
- **同族**：`KNOWLEDGE_DEMON_BOSS` 走同一个 `HasPendingChoice` 路径（两个来源都要覆盖）。
- **判据**：实机读到 `PendingTurnStartChoiceDescription` 指名的那一个，然后**才**动那一行 ——
  改的是"回合边界的结算"，本 session 已在同类地方被罚过两次。
- **优先修的理由**：这是**时间轴级**边界（不是单张牌），代价是整段地平线。

---

## 4. 执行协议

**一次做完，不要中途回来问。** 只有四类算阻碍性问题：产品/政策决定、凭据/账号、
不可逆操作的许可、信息缺失到无法开始。其余一律自己决定并往下走。
（完整展开见 `AGENT.md` §2 R0。）

**每条规则的病根是同一个 —— 用规则的文字代替规则的目的。** 所以 R0–R8 一起读，别只挑一条。

**session 轮次循环**：

1. `python scripts/state.py show` —— 拿阶段与活跃 backlog；
2. 按"手上的东西/现象"选一个 **Skill**（`AGENT.md` §5 的选表），不要按目录选；
3. 做完按该 Skill 的**完成判据**自检；
4. `python scripts/state.py gate` —— 记录门禁结果；
5. `python scripts/state.py log "…"` —— 把结论与证据落盘，供下一个 session 接手。

**探索只为写实现服务**：每 3 次只读必须换 1 次写入实现。

---

## 5. 验证

当前验证合同以 `AGENT.md` §3 为准：`scripts/verify-project.ps1` 顺序运行生产构建、独立 TypeLoadCheck 和**一次无过滤参数的完整内核 PatchSmoke**。完整入口覆盖普通场景，默认 ordinary 标为 `covered-by-full`，独立 PASS 数为 `null`；`-IncludeOrdinary` 才另外运行普通诊断。历史四门记录保持原样，不改为三门成功。

`--kernel-power-only`、case flags 与 `--packaging-smoke` 都是子集，不能报成完整回归。当前最低计数线在 `AGENT.md` §3，实际 PASS 数从本次原始日志读取，不从文档复制。具体社区路径参数及复现步骤见 [`docs/TESTING_AND_BENCHMARKS.md`](docs/TESTING_AND_BENCHMARKS.md)。

验证结果新建在 `work/verification/<运行标识>/`，包含实际命令、退出码、日志、`summary.json` 和输入/工件绑定；不打包或覆盖旧 run。记录自己的本地状态用 `python scripts/state.py gate`。交付在输入未变时使用 `scripts/build.ps1 -VerifiedGateBinding <binding> -Label <fresh-label>` 复用已验证的双 DLL，不再重构建或重跑 smoke；无绑定的 legacy 打包只提供子集证据。

开发中运行相关聚焦用例及必要 RED/GREEN；同一批生产改动完成、准备交付时运行一次默认完整验证。

**R1/R2 是硬要求**：不许声称未验证的事；新断言必须先**故意改坏确认变红**再宣布修好。
全绿的断言比没有断言更坏 —— 先证明它活着（R5b / R5c）。

---

## 6. 本次整理（2026-09-20）

| 动作 | 结果 |
|---|---|
| `work/` 中间构筑清理 | **8.0GB → 2.2GB**，删除 289 个路径；逐条记录 `work/CLEANUP-2026-09-20.md` |
| 参考副本归位 | `CombatSolver-main/`、`CombatSolver-upstream-main/`、`StS2.RandomForeseer-0.13.14/`、两个参考包 → `third_party/`；**8 个引用脚本已同步更新路径** |
| 废弃协作设施 | `superharness*`、`agent-tools`、`coord`、opencode 数据库、`.superharness/` 删除 |
| 全局规划 | 本文件 |
| Skills | `.claude/skills/` 下 S1–S9（见 `AGENT.md` §5 注册表） |
| 持久化状态 | `work/agent-state/state/project-state.json` + `scripts/state.py` |
| **首次必读压缩** | `AGENT.md` 新增 **§0**（约 60 行入口）；未完成项从 §7 迁到本文件 §3 |
| **通过数收敛** | 五个互相矛盾的读数收敛为「门槛看 `AGENT.md` §3、证据看 `work/verification/`」 |
| **废弃横幅** | **39** 个历史/废弃文档加上作废横幅：根目录 6 个（`CLAUDE.md` / `TEAM.md` / `AGENTS.md` / `SOUL.md` / `TAKEOVER_REVIEW.md` / `DECK_DELTA_MODEL_PLAN.md`）+ `docs/PREPARATION_CHECK.md` + `docs/building/` 32 个 |

**判据与边界**：清理只删 `work/`（Git 忽略的中间产物）与第三方参考副本的**位置**；
`outputs/`（已发布工件）、`src/`、`tests/`、`scripts/`、`docs/` 一律未删。

### 6.1 冲突与解决（2026-09-20 第二轮）

| # | 冲突 | 证据 | 解决 |
|---|---|---|---|
| 1 | **权威链断了 + "门 4"有两个命令** | `AGENT.md` 说"冲突时以 `tests/README.md` 为准"，而它把权威让给了历史文档 `TAKEOVER_REVIEW.md`；同时 §3 门 4 记的是 `--kernel-power-only` 子集（94），而 `build.ps1`/`verify-project.ps1` 跑的是完整套件（**265**） | 断掉该引用；§3 列出两条命令的对照表；门槛从 94 改为 265；§3.1 记录复核结论 |
| 2 | **权威链断裂** | `AGENT.md` §3 说"冲突时以 `tests/README.md` 为准"，而 `tests/README.md` 又把权威让给历史文档 | 断掉该引用，改为指向 `AGENT.md` §3 + `state.py gate` |
| 3 | **协作协议两套** | `TEAM.md`/`AGENTS.md`/`SOUL.md`/`CLAUDE.md` 与 `AGENT.md` R0 直接矛盾（"只执行被分配的任务" vs "一次做完"） | 四份文件已有作废横幅；`docs/README.md` 与 `PROJECT_MAP.md` 不再把它们列为必读 |
| 4 | **偏离表两份无互链** | `AGENT.md` §6（运行期行为）与 `VENDOR_DEVIATIONS.md`（源码层 D1–D4） | 两边加互链 + 二分标准（运行期行为 vs `Vendor/**` 源码；后者必须由导入管线产生） |
| 5 | **版本号三处不一致** | `mod_manifest.json`/`FallbackVersion` = 0.36.4；`README.md`/`SOURCE_PACKAGE.md` = 0.36.1 | 确认 0.36.2–0.36.4 **从未发布**（`outputs/` 无无标签包），因此 README 描述的是**已发布版本**，措辞改为"已发布版本 0.36.1 / 工作树领先"，消除歧义 |
| 6 | **§7 有 4 行空的 `**未完成**：`** | 编辑残留 | 删除，未完成项统一指向本文件 §3 |
| 7 | **死线段落无标记** | `tests/README.md` §4 与文末 Python 工具节、`docs/building/**` 32 个文件 | 全部加"所属线已废弃"横幅 |

**复核结果（2026-09-20 实跑，已解决）**：`AGENT.md` §7 的 **166 是对的**；**94 也对，但那是子集口径**。
实跑 `scripts/verify-project.ps1` 得到 `work/verification/20260920T152605040Z/summary.json`：

- 门 1 构建 exit 0 · 门 2 TypeLoadCheck exit 0（406 类型）· 门 3 **166 PASS** · 门 4 **265 PASS**（完整套件）
- 另跑 `-- --kernel-power-only` 得 **94 PASS** —— 与 §7 记录的第二个数完全吻合

> **更正上一轮的判断（R1）**：我先写成"§7 的数没有证据、不可核查"，那是**错的** ——
> 数是对的，我只是没跑。真正的问题是**两个不同的命令共用了"门 4"这个名字**，
> 于是门槛 94 对上读数 265，**这个比较恒真**（R5b：恒为真的检查不是证据）。
