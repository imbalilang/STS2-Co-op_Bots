# 社区关系数据质量审计（RELATIONSHIP_DATA_QUALITY）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


## Astra 复核：直接可用的结论

结论：足以作为受限、可解释的关系先验，不足以直接拟合当前版本小刀构筑的收益函数。未改生产代码。

2026-09-18 实际请求14张白名单牌和PANACHE，两类端点共30份关系响应，另取在线OpenAPI与公开源码。14张牌的draft built_at全部为2026-08-18T18:45:19.635000。白名单182条可能的非自环有向边中，接口返回20条draft内部边；14张牌中10张没有返回任何白名单内draft候选。20条边最低offers3283、最低picks1839；这部分样本并不稀少。pairings两方向合并去重后为52/91条无向内部边。计数见work/relationship-audit/coverage-summary.json；未返回的边为未知，不能记作0或负关系。

精准共现：刀刃之舞co43529/winrate50.19%；LEADING_STRIKE co38118/48.75%；KNIFE_TRAP co17377/63.54%。精准→PANACHE抓取：offers1022、picks172、pref16.57%、pref_base7.84%、lift2.1143，即相对抓取倾向+111.43%，不是胜率加成。draft该边winrate61.63%按选中事件累计，不能当作1022场独立胜率。

BLADE_DANCE的12条draft推荐里包含PILLAR_OF_CREATION(180 offers)、SUPERMASSIVE(109)、HYPERBEAM(146)，且没有ACCURACY。这体现全局Top12和跨角色混合的局限，不能据此认定这些跨角色牌比精准更适合小刀。

本地消费方向另有实质问题：接口context=B推荐A，含义是已持有B时选择A；BuildValue.Affinity却查询候选A的表再找已有B，读取了反方向。此为有向抓取数据，不能直接转成对称图。导入器还裁成每牌4条；后续若采用新关系网必须保留方向、样本和时间信息。

证据边界：当前上游源码不等于8月18日生产构建版本；deck字段的历史删除缺失是当前算法结构风险，尚未独立核对上游所有入库规范。官方ID过滤也不能证明跑局本身无MOD。未按角色分层的NPMI会混入角色卡池共同出现效应。去重、上传用户代表性、独立玩家数未获验证。不能将原文有关偏差的方向性推断当作已测事实，也不能把有方向lift的排序描述成丢失方向信息。

建议：先用机制关系约束核心边、draft抓取倾向作有上限的辅助分，pairings主要用于发现组件与检查合理性；不直接以原始胜率排序，不从Top12缺失构造负样本，不依据本数据学习重复张数或升级收益。无需启动战斗采样即可做小型规则验证。

- 文档性质：**只读分析**。不改生产代码、不跑战斗/训练/云、不做实验。仅写本文件。
- 编制日期：2026-09-18
- 审计对象：`work/relationship-audit/` 内的上游后端源码（`backend_app_services_draft_recs.py`、`backend_app_services_item_pairings.py`、两个 `backend_scripts_build_*.py`）、live 快照（`draft-accuracy.json`、`pair-accuracy.json`、`draft-recs-*.json`、`pairings-*.json`）、本地缓存 `work/spire-codex/draft-recs/*.json`、本地导入器 `scripts/import-spire-codex-affinity.py` 与产物 `src/CoopBots/Building/BakedCardAffinity.cs`。
- 证据标记：**【码】**=审计源码可直接读到；**【数】**=样本 JSON 直接读到；**【推】**=由源码/数据推导，非直接证实；**【未知】**=源码未建立。

---

## 0. 结论摘要

1. **可用的只是弱先验，不能支撑因果结论。** 两个 feed 都是观测型数据，没有随机化、没有对玩家/角色/版本/难度的控制；`draft_recs` 条件化“手上已有 B”后仍受同型偏好（好玩家、同一流派）混杂，属于对“往期选择结果”的条件化。**【推】**
2. **`item_pairings` 是终局牌组的一次性共现**，天然带生存者偏差与选择偏差；`draft_recs` 虽做了时间回放，但**上下文仍只取终局牌组**，中途被移除的卡整段缺失。**【码】**
3. **回放存在三处确定的口径问题**：同层 `floor_added < floor` 严格小于（同层不入上下文）、缺失 `floor_added` 落成 0（视为最早持有）、牌组是**终局快照**（被删/被转化卡完全不可见）。**【码】**
4. **样本单元是“每次报价 × 每件已持有物品”，不是“局”**，同一局内同一 `(A,B)` 可贡献多份，且同一局多个 B 共享同一 pick；没有去相关/聚类/置信区间。**【码】**
5. **有基于全局抓率的收缩**：α=30 是固定先验强度；pref 向基准抓率收缩，对应 lift 向 1 收缩。**【码】**
6. **Top-K 按 lift 降序截断，负向/被回避关系（lift<1、NPMI<0）基本不可能出现**；`score_offer` 只看到每件已持有物品的前 12 条，截断会传播到在线打分。**【码】**
7. **时间戳/版本**：本地烘表快照 `built_at=2026-08-18T18:45:19.635000`（**【码】`BakedCardAffinity.cs:23`**）。live 抓取的同名卡在**数值上与 8/18 缓存逐字段一致**，但卡面描述已更新（例：Pillar of Creation 2→3 Block），说明推荐数值是旧快照、描述在读取时按当前卡表重解析。**【数】**
8. **上游代码是“当前公开源”**（`source-tree.json` sha `683e82fc6fbb73d1230ebcdd0302e38c960b2186`），不一定等于 8/18 出数时的那版；本审计把当前源当代理。**【码】**
9. 本地消费端目前只在 `Affinity`/`UnsupportedScaling` 上使用，权重上限 +6，属于“接近取舍时轻推”。把共现 lift 当“触发器缺失”证据是语义错配，已有独立审查记录。**【码】**

---

## 1. 种群与筛选（population / filters）

### 1.1 item_pairings

- 查询：`ascension 0..10`、`hidden != True`，仅投影 `deck.id, relics.id, potions, win`。**【码】`item_pairings.py:131-135`**
  - 未按 `game_mode`、角色、party size、`build_id`/版本、是否 abandon 过滤 → **跨版本、跨模式、跨人数混池**。**【码】**
- 官方集合：卡取 `rarity != Basic`、`type not in (Quest, Curse)`、排除 `ASCENDERS_BANE`；遗物排除 `Starter Relic`；药水全收。**【码】`item_pairings.py:77-93`**
- 局内物品：牌组只取终局 `run["deck"]`，遗物取 `run["relics"]`，药水取 `was_picked or was_used`（把“捡起”和“使用”混为一类）。多重卡以集合去重。**【码】`item_pairings.py:101-122`**（药水行 114-121）
- `n_runs` 只统计**官方物品数 ≥2 的局**（`len(item_set) < 2` 跳过后再 `n_runs += 1`）。**【码】`item_pairings.py:161-175`** → 分母不是全部局。
- 共现用 `XᵀX` / 胜局子集 `XwᵀXw` 一次算出，`count/wins` 取对角。**【码】`item_pairings.py:182-203`**

### 1.2 draft_recs

- 查询同样仅 `ascension 0..10`、`hidden != True`，投影 `deck.id/floor_added`、`relics.id/floor_added`、`card_choices`、`win`。**【码】`draft_recs.py:80-91`**
- 官方集合复用 pairings 的 `_official_sets()`。**【码】`draft_recs.py:47-52`**
- `n_runs` 只统计**至少有一次官方报价的局**。**【码】`draft_recs.py:112-115`**

> 结论：两个 feed 都没有版本/模式/人数/角色分层；若游戏在采样窗口内改过卡（8/18 后确实改过描述），同一池子会把不同卡面口径混在一起，`built_at` 也无法表达“池子的时间跨度”。**【推】**

---

## 2. 采样单元与 run 内依赖（sample unit / within-run dependence）

- `draft_recs` 的计数键是 `(A, (kind,B))`，在一次报价里遍历**所有当时持有的 B**，每个 B 各记一次 `offers/picks`。**【码】`draft_recs.py:117-131`**
  - 同一次 pick 会同时计入所有 B 的分子 → 不同 `(A,B)` 样本高度重叠。**【码】**
- 同一局可以有多次报价，`(A,B)` 可重复贡献；文件只在 `compute_recs` 末尾记录 `n_runs`，**没有任何 run 级去重、聚类稳健标准误或置信区间**。**【码】`draft_recs.py:139,186`**
- `item_pairings` 以“局”为单位算共现（同一局只算一次），样本单元相对干净；但**它把整局的终局牌组当作一个 bag，丢弃了时间顺序**。**【码】`item_pairings.py:161-175`**

> 结论：两 feed 报告的都是点估计；`offers`/`co` 是“非独立观测数”，不能当作有效样本量。当前源码没有可据以计算显著性/CI 的量。**【推】**

---

## 3. 终局牌组 vs 时间回放：选择偏差与生存者偏差

### 3.1 item_pairings：纯终局共现

- 只读 `run["deck"]`（终局）。**【码】`item_pairings.py:106-109`**
- 因此：**中途被删/被转化/被吞噬的卡完全不在样本里**；一副“曾经有 A 和 B，后来删掉 B”的局只以“有 A”出现。共现度量的是“最终留在成品牌组里的卡”，混杂了“能否活到终局/是否值得保留”。**【推】**

### 3.2 draft_recs：条件化，但上下文仍是终局牌组

- 报价侧是时间的：`card_choices[].floor`、`was_picked`。**【码】`draft_recs.py:68-72`**
- 上下文侧却是终局的：`run["deck"]` 建 `ctx`，用每张卡的 `floor_added`。**【码】`draft_recs.py:59-67`**
- 持有判定：`held = {(k,i) | fa < floor, (k,i) != ("cards", a_id)}`。**【码】`draft_recs.py:122-124`**
- **生存者偏差**：`ctx` 只含活到终局的卡。若 B 在某报价时在手、之后被删除，则 B **完全不进 ctx**，这次报价不会被条件化到 B 上；反之只有“始终保留的 B”才贡献样本。于是 lift 系统性偏向“被留下”的卡。**【码】+【推】**
- **同层问题**：`fa < floor` 是严格小于 → 同一层由事件/商店/另一张奖励获得的卡，对同层后续报价**不算在手**；`card_choices` 若同层多次报价，先拿的卡不计入后一次。**【码】`draft_recs.py:122-124`**
- **缺失 floor 问题**：`c.get("floor_added") or 0` 把 `None`/缺失落成 0，等于“从第 0 层就持有”，会为所有报价加样本。官方集合排除了 Basic/Starter，能缓解但不能证明所有在局内获得的卡都有正确 `floor_added`。**【码】`draft_recs.py:62-67`**
- `floor_added` / `floor` 是绝对层还是按幕计数，源码未定义 → 严格小于的“同层”边界不可验证。**【未知】**

> 结论：文档自述“temporal replay”只对了一半：报价是时间序，**上下文是终局幸存集合**。这会遗漏历史上下文并造成选择偏差，但偏差的方向和幅度不能仅凭源码确定。**【推】**

---

## 4. 收缩公式与方向性

### 4.1 收缩

```python
pref_base = picks_base[a] / offers_base[a]                      # draft_recs.py:143-145
pref      = (picks + _SHRINK_ALPHA * pref_base) / (offers + _SHRINK_ALPHA)   # :153
lift      = pref / pref_base if pref_base > 0 else 0.0          # :154
```

- α = 30 为固定伪计数。**【码】`draft_recs.py:40`**
- 代数：`lift = (picks + 30·pb) / ((offers+30)·pb)`；`offers→0` 时 `lift→1`，`offers→∞` 时 `lift→picks/(offers·pb)`。**即把 lift 拉向 1，而不是拉向基线比 1。** 先验均值来自数据、强度固定；不应仅因 α 固定就否定经验贝叶斯解释。**【码】+【推】**
- 硬门槛 `_MIN_OFFERS = 100`。**【码】`draft_recs.py:37`**

### 4.2 方向性

- `item_pairings` 同时存 `conf`=P(partner|this) 与 `conf_rev`=P(this|partner)，明确说明“两个方向都留”。**【码】`item_pairings.py:18-21,249-265`**
- 但**排序是 NPMI（对称）**，`conf`/`conf_rev` 只随文档返回，不参与 top-N 选择。**【码】`item_pairings.py:276-282`**
- `draft_recs` 的方向是“有 B → 被报 A”，它本身不对称；反向要再查 B 的 context 文档。**【码】`draft_recs.py:248-263`**
- 在线 `score_offer` 用“手里每件东西的 context 文档 × 被报价卡”连乘。**【码】`draft_recs.py:293-306`**

> 结论：方向性数据有，但**方向信息没有进入排序与截断**；真正决定露出的是对称 NPMI 或单向 lift + Top-K。**【推】**

---

## 5. Top-K 截断与负向关系

- `item_pairings` 排序：药水按 `(-co,-conf,id)`，其余按 `(-npmi,-co,id)`，取 `[:_TOP_N]`（N=12）。**【码】`item_pairings.py:276-282`**
  - `npmi` 可以为负（`pmi=log(N·co/(ci·cj))`）。**【码】`item_pairings.py:233-236`**
  - 但降序取前 12 后，**负/被回避关系永远排在末尾被截掉**，除非该物品不足 12 个支持对；`_MIN_SUPPORT`（卡/遗物 250、药水 500）又会先删掉低支持对。**【码】`item_pairings.py:46,245-266`**
- `draft_recs` 排序：`recs.sort(key=(lift, offers), reverse=True)`, 取 `[:12]`。**【码】`draft_recs.py:171,177`**
  - `lift ∈ [0,∞)`，`lift<1`（被回避）会被排到尾部截掉；没有显式负阈值，也没有保留“反向推荐”。**【码】**
- `_TOP_N` 截断**传播到在线打分**：`score_offer` 只遍历已持有物品 context 文档里的 `recommends`，前 12 名之外的同伴无从贡献；且 `_LIFT_CLAMP=(0.5,3.0)` 的下界 0.5 实际上很难被触发，因为 <1 的项通常已被截掉。**【码】`draft_recs.py:42,44,281-299`**

> 结论：文档提到“<0 = actively avoided together”，但**现有排序+截断使负关系几乎不可见**；这是一个实现层面的限制，不是数据里没有。**【推】**

---

## 6. 时间戳与版本适用性

- 本地烘表：`BakedCardAffinity.Snapshot = "2026-08-18T18:45:19.635000"`，**无时区后缀**。**【码】`BakedCardAffinity.cs:4,23`**
- 导入器从每个缓存 JSON 的 `built_at` 取首个非空值写进产物。**【码】`import-spire-codex-affinity.py:81,102`**
- 抓取脚本从卡表导出的 `cards.json` 里筛 `Attack/Skill/Power` 且有 rarity 的 id，按卡单文件缓存。**【码】`fetch-spire-codex-draft-recs.py:35-43,71-88`**
- **live 与 8/18 缓存对比（抽样 BLADE_DANCE / ACROBATICS）**：数值字段（`pref/pref_base/lift/offers/picks`）逐字段一致，但卡面描述不同：
  - 8/18 缓存：Pillar of Creation `desc = "…gain 2 [gold]Block[/gold]."`（`work/spire-codex/draft-recs/BLADE_DANCE.json`）
  - live：Pillar of Creation `desc = "…gain 3 [gold]Block[/gold]."`（`draft-recs-BLADE_DANCE.json`）
  - 读取时按**当前卡表**重解析描述，见 `_enrich`/`_name_maps`。**【码】`draft_recs.py:220-245`、`item_pairings.py:337-394`**
  - 结论：live 推荐数值与 8/18 一致（样本内），**卡面语义已变但 lift 未重算**；若卡真的被改数值/机制，推荐仍按旧行为给出。**【数】+【推】**
- 源码版本：`source-tree.json` 给出公开仓库 sha `683e82fc…`；该树是本次抓取的“当前源”，不保证等于 8/18 出数版本。**把当前源当代理，不做逐字节等同断言。**【码】**
- 本地烘焙还被 `local-card-ids.txt`（本机 596 张）二次过滤未知引用；文件缺失时导入器会退化为“不过滤”并警告。**【码】`import-spire-codex-affinity.py:46-58,87,138`**

---

## 7. 对“弱启发式 vs 训练因果模型”的评估

- **能支撑**：一个低权重、方向模糊的“同现/报价连带”先验。当前生产用法正是如此：`Affinity` = `min(6.0, 1.2 × min(lift,2.5) × ≤2 个同伴)`；`AffinityCap=6.0` 明确低于强力角色契合的 8~14。**【码】`BuildValue.cs:404-411,445-458`**
- **不能支撑**：
  - “B 让玩家更想要 A”的因果表述（`draft_recs` docstring）。条件化“已拥有 B”是对既往选择结果的条件化，会因共同原因（角色、流派、玩家水平、版本、幕数）产生关联；报价条件化只消除了 A 侧的稀有度/出现概率混杂，**并未消除 B 侧混杂**。**【码】`draft_recs.py:1-17` +【推】**
  - 用共现推断“触发器/使能缺失”。这正是 `UnsupportedScaling` 的错配：只要 `Pairs[card]` 存在且伙伴都不在牌组就 ×0.4，而表的语义是“共同被抽到的 lift”。**【码】`BuildValue.cs:411-423`；同类结论见 `HEURISTIC_REVIEW.md` 问题 2**
  - 把 `winrate` 当强度：两 feed 都算了 `winrate`，但排序不用；本地导入器也明确丢弃它（受“谁在什么局势才拿”混淆）。**【码】`item_pairings.py:237-238`、`draft_recs.py:155,164`、`import-spire-codex-affinity.py:19,82-92`**
- **“训练因果模型能否改善”**：仅凭这份观测 feed 不足以建立可外推的因果/反事实模型——缺少随机化、缺少对玩家/角色/难度/版本/幕的混杂控制，且数据分布是“当前策略下”的（off-policy 不成立）。要谈因果改进，至少需要：版本/角色/人数/幕分层、报价集合与手牌快照的完整时间线（含被删卡）、run 级聚类或分层模型、以及独立验证集。以上均**超出本 feed 能力**。**【推】**

---

## 8. 现存代码限制（按影响排序，均为上游只读判断）

| # | 限制 | 证据 | 性质 |
|---|------|------|------|
| 1 | 回放上下文取终局牌组，中途移除卡整段缺失（生存者偏差） | `draft_recs.py:59-67,122-124` | 口径问题 |
| 2 | `fa < floor` 严格小于导致同层不入上下文 | `draft_recs.py:122-124` | 口径问题 |
| 3 | 缺失 `floor_added` 落 0，等于最早持有 | `draft_recs.py:62-67` | 口径问题 |
| 4 | 样本单元非独立、无 run 级去重/聚类/CI | `draft_recs.py:117-131,186` | 统计问题 |
| 5 | 固定 α 伪装成经验贝叶斯；lift 拉向 1 | `draft_recs.py:40,153-154` | 表述/统计 |
| 6 | 排序+Top-12 使负关系与多数 lift<1 不可见；截断传入在线打分 | `draft_recs.py:171,177,281-299`；`item_pairings.py:276-282` | 截断问题 |
| 7 | 无版本/模式/人数/角色分层，跨补丁混池 | `draft_recs.py:80-91`；`item_pairings.py:131-135` | 种群问题 |
| 8 | `winrate` 无控制且未进入排序 | `item_pairings.py:237-238`；`draft_recs.py:155` | 混淆 |
| 9 | 药水把 `was_picked` 与 `was_used` 混一类 | `item_pairings.py:114-121` | 口径问题 |
| 10 | 描述按当前卡表重解析，推荐数值不随卡改版重算 | `draft_recs.py:220-245`；live vs 8/18 抽样 | 版本问题 |

> 说明：NPMI 的 `p_ij==1` 除零保护、`_MIN_SUPPORT` 按伙伴类型分向应用等实现是自洽的，不算缺陷；`n_runs` 只计有效局是设计选择，但需在文档中说明。**【码】**

---

## 9. 给后续决策的最小提示（不改本任务范围）

1. 在文档/界面文案上把“synergy / makes players want X”降级为“同现关联”，避免因果措辞。**【推】**
2. 本地消费端若要更稳，优先修 `UnsupportedScaling` 的语义错配（用显式 trigger/机制字段，而非共现），而不是调权重。**【码】`BuildValue.cs:411-423`**
3. 若未来重建 feed，最小必要补充是：保留牌组时间线（含删除事件）、按版本/角色/人数分层、run 级聚类，并在导出中保留 CI。这是数据侧改造，不是本任务可执行项。**【推】**

---

## 附：样本与文件对照

- `draft-accuracy.json`、`draft-recs-*.json`：draft_recs 的 live 响应；`item_id` 命名（ACCURACY 等）里的 “accuracy” 是卡名，不是统计准确率。
- `pair-accuracy.json`、`pairings-*.json`：item_pairings 的 live 响应；`count` 为该卡的局数（如 ACCURACY `count=69931`，`winrate=0.4465`）。
- `work/spire-codex/draft-recs/*.json`：8/18 本地缓存（540 个文件），`BakedCardAffinity.cs` 由 `import-spire-codex-affinity.py` 从此生成。
- 本审计未运行任何脚本/测试；`shux`、`rg` 因环境权限被拒，结论全部来自文件读取。上游代码可能非 8/18 出数版本，已在 §6 标注。
