# 卡牌构筑基线（BASELINE）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


> **路径提示（2026-09-20）**：本文提到的 `CombatSolver-main/`、`CombatSolver-upstream-main/`、
> `StS2.RandomForeseer-0.13.14/` 已在本次目录整理中移入 `third_party/` 下。
> 原文保留不改写；引用路径请自行加 `third_party/` 前缀。相关脚本已同步更新。


- 文档性质：**现状与证据分级**，不是结论、不是计划、不是验收
- 编制日期：2026-09-17
- 编制方式：仅用文件工具读取仓库内实际文件；未运行 shell、未启动实验、未修改算法/历史记录/原始数据
- 上游入口：[`../../TAKEOVER_REVIEW.md`](../../TAKEOVER_REVIEW.md)、[`../PROJECT_MAP.md`](../PROJECT_MAP.md)
- 配套计划：[`NEXT_STEPS.md`](NEXT_STEPS.md)

本文把所有陈述分为三类，**三类不得混用**：

| 标记 | 含义 | 使用规则 |
| --- | --- | --- |
| **【实现事实】** | 当前工作树代码/数据可直接读到 | 可作为实现与设计的依据 |
| **【历史主张】** | 早期文档或回执里的结论 | 必须连同版本、配置、样本量一起引用；与当前工作树冲突时以工作树为准 |
| **【未验证假设】** | 尚未被当前证据支持的解释 | 不得当成结论；只能作为待检验项进入实验设计 |

历史结论一律**链接原文**，不在本文复制；`outputs/` 与 `work/` 被 Git 忽略，普通 clone 不包含它们（`docs/PROJECT_MAP.md:148-157`）。

---

## 一、活跃的生产决策路径【实现事实】

卡牌构筑的核心估值在 `src/CoopBots/Building/`，编译进 `CoopBots.dll`；实际调用与上下文还涉及 `BotBrain`、`BotShopPlanner`、`HumanCoopAdvisor` 和 `TeamCoordinator`。下面的研究代码不在生产路径上。

### 1.1 决策入口与评分

| 路径 | 位置 | 作用 |
| --- | --- | --- |
| 奖励选牌 | `BuildValue.BestReward`（`src/CoopBots/Building/BuildValue.cs:331`） | 在所有候选中取 `Add(...).Total` 最大者；`allowSkip=true` 时允许跳过（返回 -1） |
| 加牌估值 | `BuildValue.Add`（`BuildValue.cs:200`） | 自身贡献 `Marginal` 减去**稀释**（`HandSize/(deck.Count+1)`，`:205`）与**体积压力** `Oversize`（`:242-253`，上限 `SizePressureCap=30`） |
| 删牌估值 | `BuildValue.Remove`（`BuildValue.cs:259`） | 被删卡的贡献取反 + 返还抽牌；起始牌有 `StarterRemovalBaseline=55` 与 `StarterPressure`（`:32-43`） |
| 升级估值 | `BuildValue.UpgradeDelta`（`BuildValue.cs:293`） | 用真实升级差异计价，成本下降权重最高（`CostDelta*14.0`，`:297`） |
| 单卡画像 | `CardProfile.Of`（`src/CoopBots/Building/CardProfile.cs:42`） | 从卡模型动态变量/关键字导出数值与角色 |
| 流派加成 | `Archetypes.Bonus` / `Detect`（`src/CoopBots/Building/Archetypes.cs:81,58-67`） | 数据挖掘簇优先，角色路线兜底；只加不减（`:17-20`） |

关键常量（当前值，改动即改变生产行为）：`Base=8`、`DamageWeight=1.2`、`BlockWeight=1.0`、`DrawWeight=4`、`EnergyWeight=8`、`ScalingWeight=7`、`DuplicatePenalty=5`（`BuildValue.cs:20-32`）。

### 1.2 角色与资源表【实现事实】

- `CardProfile.Roles`（`CardProfile.cs:21-34`）已包含 **`strength` / `dexterity` / `focus`** 等细化角色；`"strength"` 在 `CardProfile.cs:60` 由 `analyzed.Strength > 0` 直接产出。
- 小刀**已拆成三种语义**，不再用单一 `shiv` 标签：`is-shiv`（资源）、`produces-shiv`（enabler）、`buffs-shiv`（payoff/乘数器）（`CardProfile.cs:74-91`）。
- 资源烘焙表 `BakedResources.All`（`src/CoopBots/Building/BakedResources.cs:17-28`）由 `scripts/import-spire-codex-resources.py` 生成；其中 `shiv` 的乘数器为 `ACCURACY / FAN_OF_KNIVES / PHANTOM_BLADES`（`BakedResources.cs:21`），`strength` 为独立资源条目（`BakedResources.cs:26`）。
- 因此「先补 strength 的 M/R」这类交接说法**与当前代码不符**：`strength` 角色与资源条目都已存在。以代码为准（见 §五 历史主张第 4 条）。

### 1.3 其他生产候选项（同属决策链，但不在本任务范围）

`TeamCoordinator.TeamBonus`（`BuildValue.cs:156`）、`BakedCardAffinity` 亲和（`BuildValue.cs:438-451`）也在选牌评分中，但其权重被刻意压低（`AffinityCap=6.0`），不会压过卡自身贡献。

---

## 二、研究代码与数据位置【实现事实】

这些**不进入发布包**，也不改变生产算法；它们产出的是测量结果。

| 类别 | 路径 | 说明 |
| --- | --- | --- |
| 2×2 驱动 | `scripts/shiv-pm-interaction.py` | P×M（生成器×乘数器）因子实验；移除池 `REMOVAL_POOL = ["DEFEND_SILENT"]*4`（`:42`），张数恒定（`:70-106`） |
| 遭遇标定 | `scripts/soulnexus-sigma.py` | SoulNexusElite 的 σ/通关率/回合/配对 ρ；工作点读取 `afterTurns` 后解析 `final_enemy_hp`（`:107-119`） |
| 底座筛选 | `scripts/silent-base-screen.py` | 10 副猎手候选的裸测生存性筛选（见派单66回执 §1） |
| 实验数据 | `work/oracle/shiv-pm/pm.json` | 最新一轮逐场读数 + 交互项（`:1501-1506`） |
| 社区底座 | `work/spire-codex/decks/runs/` | 历史筛选记录为 832 副真实牌组缓存（带 relics 字段；当前数量未重数），小刀实验底座 `9f0f93ff71` 出自此处 |
| 离线求解宿主 | `CombatSolver-upstream-main/`（0.40，HEAD `1c31b9f`） | 纯 .NET 进程，不启动 Godot；由 `soulnexus-sigma.py:30` 等引用 |
| 游戏内 oracle | `CombatSolver-main/`（0.39.0） | 另一条 oracle 路径，本任务不涉及 |
| 适配记录 | `scripts/combatsolver-oracle-adaptations.json` | ORACLE-01/02 补丁与锚点，以及三份副本的用途（`:91-113`） |

副本权威登记以 `scripts/combatsolver-oracle-adaptations.json` 为准；生产内核 Vendor 为 CombatSolver 0.33.9（`src/CoopBots.Kernel/UPSTREAM.json`），与 0.40 离线宿主**不是同一版本**（见 §六）。

---

## 三、最新 P×M 实验（派单66 后续）【实现事实】

数据：`work/oracle/shiv-pm/pm.json`。**这是一次独立实验，不得与早期的 50 种子实验混同。**

配置：

| 项 | 值 | 位置 |
| --- | --- | --- |
| 底座 | 社区缓存 `9f0f93ff71`（SILENT、A10、通关、单人、无小刀、无毒） | `pm.json:7` |
| 每格种子数 | **24** | `pm.json:1505` |
| 软预算 | **`budget_ms=4000`** | `pm.json:8` |
| 数值 | **hp 1.0 / dmg 1.0** | 点键 `hp1.0_dmg1.0`（`pm.json:10`） |
| 遭遇 | SoulNexusElite | `scripts/soulnexus-sigma.py:33` |
| 主指标 | `final_enemy_hp`（**越低越好**，通关 = 0） | `scripts/shiv-pm-interaction.py:151-156` |

四格结果（`pm.json:12-1500`，汇总见 `pm.json:1501-1506`）：

```
P0M0  mean  94.8   sd 89.9   clear 0.333   turns 10.5   n=24
P0M1  mean 146.2   sd 82.1   clear 0.042   turns  7.5   n=24
P3M0  mean 148.8   sd 79.4   clear 0.083   turns  7.7   n=24
P3M1  mean 126.4   sd 94.3   clear 0.292   turns  7.6   n=24

delta_P3 = -22.4    （P3M1 − P3M0）
delta_P0 = +51.4    （P0M1 − P0M0）
interaction = -73.8  SE≈35.3（脚本用 sd_avg=86.4、2σ/√n 近似）
sd_avg = 86.425，n_per_cell = 24
```

**符号必须显式统一**：主指标是「剩余敌人血量」，**越低越好**，所以原始方向的交互项是 **−73.8**。若换算成「收益方向」（越高越好），同一结果是 **+73.8**（`outputs/project-handoff.md:97` 用的是后者）。两者是同一个数的两种符号约定，**不是两次结果**。脚本预注册里「明显大于即乘法成立」（`scripts/shiv-pm-interaction.py:44-48`）判定方向与当前原始指标相反；需明确约定，不能据此推断作者意图。

**与早期 50 种子实验的区别**（`work/coord/outbox/decksim/20260917-派单66-回执-PM交互.md:33-46`）：

| | 早期实验 | 最新实验 |
| --- | --- | --- |
| 每格种子 | 50 | 24 |
| 预算 | 历史运行参数待原始请求核实；当前脚本默认值不能证明旧运行预算 | `4000` |
| 四格均值 | 49.3 / 17.9 / 42.2 / 60.5 | 94.8 / 146.2 / 148.8 / 126.4 |
| 原始方向交互项 | **+49.7**（CI [+14.8,+84.6]） | **−73.8** |
| 原始方向含义 | M 在 P 高时更不值钱 | M 在 P 高时更值钱（收益方向 +73.8） |

两轮**方向相反**，且样本量不同、历史预算尚需核实。`TAKEOVER_REVIEW.md:35` 明确要求二者不得合并。最新一轮「值得继续验证，尚不足以指导拟合」（`TAKEOVER_REVIEW.md:35`）。

---

## 四、当前指标与仪器的已知边界【实现事实 + 待检验】

1. **主指标已从「通关三层 Boss 的血耗」漂移为「单人裸牌组打 SoulNexus 后的剩余敌人血量」**。原协议目标是 Boss 血耗（`outputs/verification-protocol.md:42-43`），当前主统计是剩余敌人血量。
2. **胜利地板效应（victory floor）**：通关时 `final_enemy_hp = 0`（`shiv-pm-interaction.py:152`），于是**所有通关样本被压成同一个值**，无法再区分「低损通关」与「高损通关」。`TAKEOVER_REVIEW.md:39` 已记录。
3. **失败/截断过滤缺失**：汇总只按 `final_enemy_hp is not None` 纳入（`shiv-pm-interaction.py:161`），**没有**显式排除截断与错误；`describe` 只把「无读数」计入 `failed`（`soulnexus-sigma.py:123-144`）。宿主已有 `fightTruncated` / `fightWallMs` 字段，但当前调用未传 `--max-fight-ms`，因此**不能断言现有数据已被截断污染，也不能断言没有**（`TAKEOVER_REVIEW.md:49`）。完整结束、失败、超时、截断必须在扩产标签前统一分类。
4. **CI 近似未利用配对结构**：固定种子在四格间有配对结构，但汇总用「各格 sd 的均值」近似独立样本标准误（`shiv-pm-interaction.py:218-220`），未直接统计每种子的四格交互差。`TAKEOVER_REVIEW.md:43` 要求改为配对逐种子差，并区分效应、随机方差与预算偏差。
5. **预算差异**：此前纯节点预算下的重复性证明（`outputs/project-handoff.md:63-71`，如 `--nodes 200` 并行逐位相同）**不能直接覆盖当前 4000ms 软墙钟配置**；需按当前配置重验单独/并发重复性（`TAKEOVER_REVIEW.md:41`）。
6. **成本模型已改**：早期交接的「1.9 秒/场」来自 D6 标定（`outputs/project-handoff.md:114`），派单66 实测单场中位 152s、摊销 11.1s/场（`派单66回执:77-98`）。整批训练成本不可用旧标定外推（`TAKEOVER_REVIEW.md:45-47`）。

---

## 五、历史主张（引用时必须带版本/配置）【历史主张】

| # | 主张 | 原文 | 与当前工作树的关系 |
| --- | --- | --- | --- |
| 1 | 「重复噪声 = 0」「节点预算让并行安全」 | `outputs/project-handoff.md:63-73` | 在**纯节点预算**下成立；当前 4000ms 软预算需重验（§四.5） |
| 2 | 「单卡 delta ≈ +1 HP，逐卡模型在结构上不可测」 | `outputs/project-handoff.md:67,158` | **不能读成「小效应数学上不可测」**，见 §七.6 |
| 3 | 「配对无用（ρ=0.431，只降 10%）」 | `outputs/project-handoff.md:73` | 一次实测；不排除换遭遇/底座后配对结构不同 |
| 4 | 「需先补 strength 的 M/R」 | `outputs/project-handoff.md:178` | **已过时**：`strength` 角色与资源条目都已存在（§1.2） |
| 5 | 「通关用 Boss 血耗、无法通关视为最差」 | `outputs/verification-protocol.md:42-43` | 目标尚未对齐；当前统计指标是剩余敌人血量（§四.1） |
| 6 | 派单66 回执「交互 +49.7，方向相反」 | `派单66回执:9-12,42-46` | 属**早期 50 种子**实验，与最新 24 种子方向相反，不得混用（§三） |
| 7 | 交接文档「每格 24 次 / 交互 +73.8」 | `outputs/project-handoff.md:88-98` | 数值与最新 `pm.json` 一致，但符号是收益方向；回执 §2 是另一轮每格 50 种子的实验；各自保留，不以 24 覆盖历史样本量 |
| 8 | 「200 场 6.6 分钟」 | `outputs/project-handoff.md:126` | 旧配置的成本不能外推；11.1s/场也只适用于其运行条件，当前配置需重新测量（§四.6） |

---

## 六、版本与交付边界【实现事实】

1. **生产内核与离线求解器版本不同**：生产 Vendor 为 CombatSolver 0.33.9（`src/CoopBots.Kernel/UPSTREAM.json`），游戏内 oracle 副本为 0.39.0，离线研究宿主为 0.40.0（`scripts/combatsolver-oracle-adaptations.json:96-101`、`docs/PROJECT_MAP.md:85-88`）。**离线研究结果不能直接当作生产内核的实测结果**（`TAKEOVER_REVIEW.md:15`）。
2. **工作树 ≠ 已发布 0.36.1**：工作树含大量未提交改动（`TAKEOVER_REVIEW.md:12-14`、`docs/PROJECT_MAP.md:159-164`）。
3. **研究资产不进 Git**：`work/`、`outputs/`、`CombatSolver-upstream-main/` 被忽略；实验数据、交接、离线宿主不随普通 clone 交付（`TAKEOVER_REVIEW.md:55`）。

---

## 七、未验证假设（只作待检验项）【未验证假设】

1. **「剩余敌人血量对真实构筑决策与多人环境有迁移价值」** —— 尚未证明（`TAKEOVER_REVIEW.md:39`）。
2. **「乘法包件」形状** —— 最新一轮收益方向 +73.8，但 CI 勉强且 P 轴与防御共线（§八.1），不能当形状结论。
3. **「早期 50 种子与最新 24 种子的差异来自预算还是底座/样本」** —— 未归因。
4. **「截断/失败对当前读数无影响」** —— 没有传 `--max-fight-ms`，既不能证实也不能证伪（§四.3）。
5. **「当前 4000ms 配置可重复」** —— 待按当前配置复验（§四.5）。
6. **「小效应不可测」** —— **没有证据支持**。单次小效应小于种子标准差，只说明按当前精度与采样成本判定所需样本量（`TAKEOVER_REVIEW.md:43`），**不等于数学上不可测**；不得据此排除对小效应的测量。

---

## 八、已知混淆与设计约束清单（进入重设计前必须处理）

1. **DEFEND 移除混杂【实现事实】**：底座普通牌层只有 `DEFEND_SILENT×5`，而「张数恒定」通过移除 `REMOVAL_POOL` 实现（`shiv-pm-interaction.py:42,70-86`）。于是加 3 张生成器 = 拿掉 3 张 DEFEND，P 轴与格挡能力共线；`P3M1` 的 DEFEND 只剩 1 张。原文见 `派单66回执:59-75`。**所谓交互项实际是「1 张 DEFEND 的牌组 vs 4 张 DEFEND 的牌组」上 ACCURACY 的价值差**，不是纯 P×M。
2. **张数恒定不等于正交化**：恒定张数同时把「加入」与「移除」绑定；需显式定义**加牌 vs 替换**的估计量，并把「移除」作为独立因子或换用非防御冗余层（`TAKEOVER_REVIEW.md:31`、`派单66回执:74`）。
3. **牌组体积稀释**：`BuildValue.Add` 已有稀释项（`BuildValue.cs:205`），但实验底座与格子的 DEFEND 数不同，总张数固定，但防御构成与处理因子混杂；未来加牌设计还需处理体积稀释。
4. **历史 ACCURACY 负对照异常**：无 Shiv 时它没有直接增伤效果（`派单66回执:52-57`），但替换 DEFEND 会改变防御、抽牌顺序与搜索路径。因此早期 `P0M1` 最好不能直接判作不可能或噪声；需对照实验区分这些来源。
5. **符号约定不统一**：原始指标越低越好、脚本预注册的判定不等式与原始方向相反（§三），交接与回执两处符号表达不同。

---

## 九、证据索引（不复制结论，只给出处）

| 想核对 | 位置 |
| --- | --- |
| 接手现状、卡点、建议顺序 | `TAKEOVER_REVIEW.md` |
| 资产地图、副本/版本/测试入口、被忽略数据 | `docs/PROJECT_MAP.md` |
| 测量协议与历次裁定 | `outputs/verification-protocol.md` |
| 交接总结（含形状第一结果） | `outputs/project-handoff.md` |
| 派单66 原始回执（混杂、成本、截断） | `work/coord/outbox/decksim/20260917-派单66-回执-PM交互.md` |
| 最新逐场读数与交互项 | `work/oracle/shiv-pm/pm.json` |
| 副本适配与锚点 | `scripts/combatsolver-oracle-adaptations.json` |
| 生产决策实现 | `src/CoopBots/Building/{BuildValue,CardProfile,Archetypes,BakedResources}.cs` |
| 验证与归档工具 | `scripts/verify-project.ps1`、`scripts/capture-research-baseline.py` |
| 计划 | `docs/building/NEXT_STEPS.md` |
