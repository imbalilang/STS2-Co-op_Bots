# 小刀模型 v0.1：社区基准与缺失的 replay 协同（SHIV_SCORE_CALIBRATION）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


> 离线、只读、标准库。生成器：`scripts/audit_shiv_scores.py`。生成时间（UTC，非数据版本）：`2026-09-17T20:52:46.731494+00:00`。
> 社区数据为缓存快照，**可能不同于当前部署数据**；快照无时间戳，故不声明数据版本。

## 1. 数据源与哈希

| 文件 | 角色 | sha256 | URL |
|---|---|---|---|
| `work/shiv-score-audit/scores-cards.json` | 所有 run 的社区 Codex Score + Elo 快照（cards） | `f548cdf1317de169…` | https://spire-codex.com/api/runs/scores/cards |
| `work/shiv-score-audit/scores-all-skip.json` | all-runs 社区 Score + Elo 快照，含 SKIP 行（最高 Elo 基准的 all-runs 唯一来源） | `9d6cf1c5b1941b5f…` | https://spire-codex.com/api/runs/scores/cards |
| `work/shiv-score-audit/scores-a10.json` | A10 bracket 的社区 Score + Elo 快照（cards，含 SKIP） | `72ec9ad79c407182…` | （快照未存 URL，不臆造） |
| `work/shiv-score-audit/run_entity_stats.py` | 定义 _compute_score 与 _compute_codex_elo 的上游源码快照 | `9dea1237f825bd55…` | （快照未存 URL，不臆造） |
| `work/shiv-score-audit/ScoreBadge.tsx` | Score -> S/A/B/C/D/F letter band 前端映射 | `0c75ee8f960ca786…` | （快照未存 URL，不臆造） |
| `work/shiv-score-audit/use-entity-scores.ts` | API 客户端类型（较旧：仅 score/picks/wins/win_rate，无 elo 字段） | `34876cc4d5bd0fd0…` | （快照未存 URL，不臆造） |
| `work/shiv-relationship-graph/graph.json` | 模型使用的 community_draft 有向层与来源溯源 | `4f4370cd7d40daad…` | （快照未存 URL，不臆造） |
| `work/spire-codex/cards/cards.json` | 关系图的卡面语义来源 | `96e5422f839d6a6a…` | （快照未存 URL，不臆造） |

- 服务元数据（取自 `graph.json` provenance）：title=`Spire Codex API`，version=`1.0.0`，openapi=`3.1.0`，captured_on=`2026-09-18`。
- 上游源码树 sha：`683e82fc6fbb73d1230ebcdd0302e38c960b2186`；URL：`https://api.github.com/repos/ptrlrd/spire-codex/git/trees/683e82fc6fbb73d1230ebcdd0302e38c960b2186`。

## 2. 实际公式与含义（来自缓存源码）

`_compute_score`（work/shiv-score-audit/run_entity_stats.py:2350-2370）：

```text
weight = prior if prior is not None else 50
shrunk = (wins + baseline * weight) / (picks + weight)
delta = shrunk - baseline
raw = (delta / 0.15 + 1) * 50
score = round(clamp(raw, 0, 100))
```

- 计数含义：picks = 在统计范围内，牌组包含该实体（至少一张）的 run 数。run 内去重：同一次 run 带 5 张 Strike 仍只计 1 次。
- wins = 这些包含该实体的 run 中胜利的 run 数。
- baseline = 同类型（cards）全部实体的『按 picks 加权的合并胜率』= Σwins/Σpicks，不是每张牌胜率的平均。
- prior = 收缩先验的虚拟 picks 数。全量默认 50；bracket 内按 run 份额缩放（_bracket_prior），下限 10。
- Score 含义：Score 是 0-100 的关联性分数：贝叶斯收缩后的卡牌胜率相对同类型基线的偏移，线性映射并截断。它是关联，不是因果效果；100 只是上边界截断，既不代表 100% 胜率，也不代表该牌本身带来胜利。

`_compute_codex_elo`（work/shiv-score-audit/run_entity_stats.py:867-1028）：Elo 是揭示偏好（玩家被提供时实际会选谁），与胜率无关；它不是独立的效果验证。本次审计的 Elo 与关系图 community_draft 层同源（都来自玩家选择），两者不是互相独立的证据。

- skip 区分：成对比较里，未被选中的牌就是『输家』；被跳过不是零证据。A10 快照中出现的 SKIP 行是奖励屏『跳过』这一伪实体的统计，本次只原样记录，不把它当作 14 张白名单之一。

已知不一致 / 警告：

- API 客户端类型（use-entity-scores.ts 的 ScoreEntry）较旧，只有 score/picks/wins/win_rate，没有 elo 字段；而实际返回的 scores-cards.json 与 scores-a10.json 每行都带 elo。
- 上游 openapi 版本为 1.0.0（graph.json provenance），描述可能把 Elo 说成 all-runs；实际返回的 A10 快照里 Elo 与 all-runs 快照的 Elo 不同。本审计保留这一事实与不一致，不声称哪份 schema 才是当前部署版本。
- 缓存快照可能不同于当前部署数据；快照内没有时间戳，故不声明数据版本。
- Score 100 是截断上界，不是 100% 胜率，也不是因果效应。
- Elo 与关系图 community_draft 同属玩家选择来源，不是独立有效性验证。
- 本次不使用 score / winrate 作为任何训练目标；不做参数拟合。

## 3. 14 张白名单全量行（社区快照原值）

| 卡 | allruns score | allruns elo | allruns picks | allruns wins | allruns win_rate | a10 score | a10 elo | a10 picks | a10 wins | a10 win_rate | elo(a10-all) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| BLADE_DANCE | 57 | 1633.3 | 224285 | 100941 | 45.0 | 51 | 1587.8 | 53990 | 18219 | 33.7 | -45.5 |
| CLOAK_AND_DAGGER | 57 | 1597.1 | 213727 | 96059 | 44.9 | 53 | 1582.2 | 56189 | 19239 | 34.2 | -14.9 |
| LEADING_STRIKE | 53 | 1555.8 | 193712 | 84503 | 43.6 | 51 | 1552.1 | 53448 | 18034 | 33.7 | -3.7 |
| INFINITE_BLADES | 74 | 1621.9 | 147939 | 74045 | 50.1 | 65 | 1589.5 | 34162 | 12935 | 37.9 | -32.4 |
| ACCURACY | 78 | 1682.6 | 170252 | 87051 | 51.1 | 72 | 1676.2 | 42789 | 17149 | 40.1 | -6.4 |
| KNIFE_TRAP | 100 | 1744.0 | 80253 | 48092 | 59.9 | 100 | 1763.4 | 18018 | 8861 | 49.2 | 19.4 |
| PHANTOM_BLADES | 74 | 1565.3 | 92948 | 46408 | 49.9 | 64 | 1550.5 | 29460 | 11087 | 37.6 | -14.8 |
| BACKFLIP | 64 | 1592.6 | 226380 | 106803 | 47.2 | 64 | 1631.0 | 69087 | 25955 | 37.6 | 38.4 |
| ACROBATICS | 77 | 1699.4 | 167843 | 85662 | 51.0 | 75 | 1763.7 | 43757 | 17884 | 40.9 | 64.3 |
| ADRENALINE | 100 | 1906.5 | 109247 | 65257 | 59.7 | 100 | 1958.5 | 26079 | 12711 | 48.7 | 52.0 |
| AFTERIMAGE | 100 | 1839.9 | 95027 | 58009 | 61.0 | 100 | 1905.2 | 23530 | 11716 | 49.8 | 65.3 |
| FOOTWORK | 69 | 1654.8 | 168153 | 81752 | 48.6 | 67 | 1760.6 | 57006 | 21990 | 38.6 | 105.8 |
| LEG_SWEEP | 67 | 1552.7 | 88826 | 42703 | 48.1 | 63 | 1626.1 | 29176 | 10901 | 37.4 | 73.4 |
| DEFLECT | 55 | 1490.0 | 153829 | 68403 | 44.5 | 53 | 1467.0 | 39240 | 13484 | 34.4 | -23.0 |

A10 快照的 SKIP 行（伪实体，不作白名单）：`{"score": null, "elo": 1597.4, "picks": 12664103, "wins": null, "win_rate": null, "pick_rate": 28.3}`。

## 4. 八场景聚合 vs 社区（描述性，非总体）

模型每张候选：八场景 `mech_delta` 均值、机制/final 排名均值（1=最好），以及社区 a10 与 allruns 的 score/elo。

| 卡 | 模型均值 mech_delta | 模型平均机制排名 | 模型平均 final 排名 | a10 score | a10 elo | allruns score | allruns elo |
|---|---|---|---|---|---|---|---|
| BLADE_DANCE | 1.002 | 5.62 | 5.75 | 51 | 1587.8 | 57 | 1633.3 |
| CLOAK_AND_DAGGER | 0.949 | 5.12 | 5.12 | 53 | 1582.2 | 57 | 1597.1 |
| LEADING_STRIKE | 0.819 | 7.12 | 7.25 | 51 | 1552.1 | 53 | 1555.8 |
| INFINITE_BLADES | 0.939 | 6.75 | 6.88 | 65 | 1589.5 | 74 | 1621.9 |
| ACCURACY | -0.064 | 10.50 | 11.12 | 72 | 1676.2 | 78 | 1682.6 |
| KNIFE_TRAP | 0.047 | 8.75 | 9.12 | 100 | 1763.4 | 100 | 1744.0 |
| PHANTOM_BLADES | 0.266 | 7.50 | 7.75 | 64 | 1550.5 | 74 | 1565.3 |
| BACKFLIP | 0.448 | 8.62 | 8.62 | 64 | 1631.0 | 64 | 1592.6 |
| ACROBATICS | -0.175 | 12.50 | 13.38 | 75 | 1763.7 | 77 | 1699.4 |
| ADRENALINE | 1.153 | 3.00 | 3.12 | 100 | 1958.5 | 100 | 1906.5 |
| AFTERIMAGE | 0.478 | 6.75 | 6.88 | 100 | 1905.2 | 100 | 1839.9 |
| FOOTWORK | -0.025 | 11.12 | 11.50 | 67 | 1760.6 | 69 | 1654.8 |
| LEG_SWEEP | 0.698 | 6.75 | 6.75 | 63 | 1626.1 | 67 | 1552.7 |
| DEFLECT | 0.769 | 4.88 | 4.88 | 53 | 1467.0 | 55 | 1490.0 |

| 对照 | n | rho(均值 mech_delta) | rho(平均机制排名) | rho(平均 final 排名) |
|---|---|---|---|---|
| community_score_allruns | 14 | -0.354 | 0.264 | 0.285 |
| community_score_a10 | 14 | -0.430 | 0.331 | 0.355 |
| community_elo_allruns | 14 | -0.160 | 0.192 | 0.227 |
| community_elo_a10 | 14 | -0.284 | 0.289 | 0.299 |

- 说明：八场景为手工示意，不是总体样本；相关系数只作描述，不作有效性或因果结论。排名并列用平均秩（tie-aware，无 scipy）。

### 需要人工复核的分歧（模型 vs 社区 a10）

| 卡 | 模型平均机制排名 | 社区 score 排名 | 社区 elo 排名 | 与 score 差 | 与 elo 差 | a10 score | a10 elo |
|---|---|---|---|---|---|---|---|
| ACROBATICS | 12.50 | 4.0 | 3.0 | 8.50 | 9.50 | 75 | 1763.7 |
| DEFLECT | 4.88 | 11.5 | 14.0 | -6.62 | -9.12 | 53 | 1467.0 |
| BLADE_DANCE | 5.62 | 13.5 | 10.0 | -7.88 | -4.38 | 51 | 1587.8 |
| KNIFE_TRAP | 8.75 | 2.0 | 4.0 | 6.75 | 4.75 | 100 | 1763.4 |
| CLOAK_AND_DAGGER | 5.12 | 11.5 | 11.0 | -6.38 | -5.88 | 53 | 1582.2 |
| LEADING_STRIKE | 7.12 | 13.5 | 12.0 | -6.38 | -4.88 | 51 | 1552.1 |
| FOOTWORK | 11.12 | 6.0 | 5.0 | 5.12 | 6.12 | 67 | 1760.6 |
| ACCURACY | 10.50 | 5.0 | 6.0 | 5.50 | 4.50 | 72 | 1676.2 |

- 上表按模型与社区 a10 排名的最大差距排序，仅用于人工复核，不强制统一排名。
- ADRENALINE 在模型示意场景中排名靠前，与其社区 a10 高分方向一致；这本身不是缺陷，本轮不做下调。
- 模型与社区分歧可能来自机制代理（供给/能量/时机）与揭示偏好/关联指标的口径差异，不能据此断言谁对谁错。
- 均值 mech_delta 与社区 score/elo 的 Spearman 为负：模型比较的是『把候选加入整副牌』的边际，生成牌在多数示意牌组中边际为正，而payoff/功能牌在缺供给牌组中边际可为负；社区 Score 是整牌出现时的胜率关联。两者口径不同，负相关只作描述，不代表模型更强或更弱。

## 5. 模型修正：Knife Trap replay 协同

新增可检查量：`knife_trap_replay_opportunity = kt_active * min(exhausted_pool, knife_trap_replay_cap)`。
Accuracy 贡献由 `accuracy_active * shiv_supply` 改为 `accuracy_active * (shiv_supply + knife_trap_replay_opportunity)`；replay 不回流到 supply/exhausted_pool，Afterimage 也不获得额外 replay 触发。其它权重/上限不变。

| 场景 | before winner | after winner | 变化 | skip rank before | skip rank after | 受影响候选数 |
|---|---|---|---|---|---|---|
| starter | LEADING_STRIKE | LEADING_STRIKE | 否 | 11 | 11 | 0 |
| producers_no_payoffs | ADRENALINE | ADRENALINE | 否 | 13 | 13 | 0 |
| payoffs_no_producers | BLADE_DANCE | BLADE_DANCE | 否 | 11 | 11 | 4 |
| defense_poor | LEG_SWEEP | LEG_SWEEP | 否 | 12 | 12 | 1 |
| energy_heavy | BLADE_DANCE | BLADE_DANCE | 否 | 13 | 13 | 1 |
| saturated | ADRENALINE | KNIFE_TRAP | 是 | 10 | 10 | 1 |
| duplicates | ADRENALINE | ADRENALINE | 否 | 11 | 12 | 1 |
| no_supported_community_edges | ADRENALINE | ADRENALINE | 否 | 13 | 13 | 0 |

改动明细（只列非零机械 delta 变化；accuracy Δ 是 ACCURACY 组件的变化）：

| 场景 | 候选 | mech before | mech after | Δ | accuracy Δ |
|---|---|---|---|---|---|
| payoffs_no_producers | BLADE_DANCE | 3.275 | 3.601 | 0.325 | 0.325 |
| payoffs_no_producers | INFINITE_BLADES | 3.235 | 3.560 | 0.325 | 0.325 |
| payoffs_no_producers | LEADING_STRIKE | 2.594 | 2.849 | 0.256 | 0.256 |
| payoffs_no_producers | CLOAK_AND_DAGGER | 1.870 | 2.003 | 0.133 | 0.133 |
| defense_poor | KNIFE_TRAP | 0.690 | 2.033 | 1.343 | 1.343 |
| energy_heavy | ACCURACY | 0.040 | 0.414 | 0.374 | 0.374 |
| saturated | KNIFE_TRAP | 0.197 | 0.704 | 0.506 | 0.506 |
| duplicates | KNIFE_TRAP | -0.076 | 0.791 | 0.867 | 0.867 |

八场景中发生机械 delta 变化的候选-场景对：8；winner 变化：1 个（saturated）。

修正只影响同时具备 ACCURACY 活跃、小刀供给、KNIFE_TRAP 活跃的局面；供给既可来自基线牌，也可来自本次被评估的生成牌候选，因此`payoffs_no_producers`（基线供给为 0）的生成牌候选仍会因新增供给而受益；而 `starter`/`producers_no_payoffs` 等不同时具备三要素的场景不受影响。

Phantom 与 replay 的时序交互仍未建模：本模型对 Phantom 只取『每活跃回合首刀』，重放是否触发 Phantom 首刀需要 runtime 验证，故此处不给 Phantom 任何 replay 信用。

## 6. 四个三选一+跳过决策案例

### payoff 重型、缺小刀供给（`missing_producer`，场景 `payoffs_no_producers`，牌组 16 张）

牌组已有 ACCURACY/KNIFE_TRAP/PHANTOM_BLADES/AFTERIMAGE，但没有生成牌；提供两个生成牌与 ADRENALINE，观察补供给与过牌/能量谁更被看好。

- 提供：BLADE_DANCE, CLOAK_AND_DAGGER, ADRENALINE
- 机制-only 排名（community off）：BLADE_DANCE > CLOAK_AND_DAGGER > ADRENALINE > SKIP
- community-on 排名：BLADE_DANCE > CLOAK_AND_DAGGER > ADRENALINE > SKIP
- skip（机制）：mech_delta=0.000；skip（community）：final=0.000，is_winner=False

| 候选 | mech before | mech after | Δ | accuracy Δ | knife_trap Δ | final before | final after | prior_applied after |
|---|---|---|---|---|---|---|---|---|
| BLADE_DANCE | 3.275 | 3.601 | 0.325 | 0.325 | 0.000 | 3.525 | 3.851 | 0.250 |
| CLOAK_AND_DAGGER | 1.870 | 2.003 | 0.133 | 0.133 | 0.000 | 1.870 | 2.003 | 0.250 |
| ADRENALINE | 1.285 | 1.285 | 0.000 | 0.000 | 0.000 | 1.285 | 1.285 | 0.000 |

> 同一副牌、同一上下文；三选一+跳过。没有真相标签，也不声明谁『正确』。before/after 只用于暴露 replay 修正带来的影响；Elo 基准分歧只作复核线索，不作胜负判定。

### 生成牌重型、缺 payoff（`lacking_payoffs`，场景 `producers_no_payoffs`，牌组 17 张）

牌组已有 BLADE_DANCE/CLOAK_AND_DAGGER/LEADING_STRIKE/INFINITE_BLADES；提供 ACCURACY、KNIFE_TRAP 与 ADRENALINE，包含本轮修正的 replay 协同。

- 提供：ACCURACY, KNIFE_TRAP, ADRENALINE
- 机制-only 排名（community off）：ADRENALINE > ACCURACY > KNIFE_TRAP > SKIP
- community-on 排名：ADRENALINE > ACCURACY > KNIFE_TRAP > SKIP
- skip（机制）：mech_delta=0.000；skip（community）：final=0.000，is_winner=False

| 候选 | mech before | mech after | Δ | accuracy Δ | knife_trap Δ | final before | final after | prior_applied after |
|---|---|---|---|---|---|---|---|---|
| ACCURACY | 0.663 | 0.663 | 0.000 | 0.000 | 0.000 | 0.663 | 0.663 | 0.250 |
| KNIFE_TRAP | 0.240 | 0.240 | 0.000 | 0.000 | 0.000 | 0.240 | 0.240 | 0.250 |
| ADRENALINE | 1.350 | 1.350 | 0.000 | 0.000 | 0.000 | 1.350 | 1.350 | 0.000 |

> 同一副牌、同一上下文；三选一+跳过。没有真相标签，也不声明谁『正确』。before/after 只用于暴露 replay 修正带来的影响；Elo 基准分歧只作复核线索，不作胜负判定。

### 防御薄弱（`defense_poor`，场景 `defense_poor`，牌组 13 张）

进攻向、几乎无格挡；提供 FOOTWORK、LEG_SWEEP 与 ADRENALINE，观察模型对防御/功能牌的处理。

- 提供：FOOTWORK, LEG_SWEEP, ADRENALINE
- 机制-only 排名（community off）：LEG_SWEEP > ADRENALINE > SKIP > FOOTWORK
- community-on 排名：LEG_SWEEP > ADRENALINE > SKIP > FOOTWORK
- skip（机制）：mech_delta=0.000；skip（community）：final=0.000，is_winner=False

| 候选 | mech before | mech after | Δ | accuracy Δ | knife_trap Δ | final before | final after | prior_applied after |
|---|---|---|---|---|---|---|---|---|
| FOOTWORK | -0.499 | -0.499 | 0.000 | 0.000 | 0.000 | -0.499 | -0.499 | 0.000 |
| LEG_SWEEP | 2.641 | 2.641 | 0.000 | 0.000 | 0.000 | 2.641 | 2.641 | 0.000 |
| ADRENALINE | 1.118 | 1.118 | 0.000 | 0.000 | 0.000 | 1.118 | 1.118 | 0.000 |

> 同一副牌、同一上下文；三选一+跳过。没有真相标签，也不声明谁『正确』。before/after 只用于暴露 replay 修正带来的影响；Elo 基准分歧只作复核线索，不作胜负判定。

### 核心已饱和、重复张数多（`saturated_duplicates`，场景 `saturated`，牌组 39 张）

已有大量重复生成牌与 payoff；提供 ADRENALINE、FOOTWORK、DEFLECT，观察稀释与低费功能牌在饱和牌组中的边际。

- 提供：ADRENALINE, FOOTWORK, DEFLECT
- 机制-only 排名（community off）：ADRENALINE > DEFLECT > FOOTWORK > SKIP
- community-on 排名：ADRENALINE > DEFLECT > FOOTWORK > SKIP
- skip（机制）：mech_delta=0.000；skip（community）：final=0.000，is_winner=False

| 候选 | mech before | mech after | Δ | accuracy Δ | knife_trap Δ | final before | final after | prior_applied after |
|---|---|---|---|---|---|---|---|---|
| ADRENALINE | 0.808 | 0.808 | 0.000 | 0.000 | 0.000 | 0.808 | 0.808 | 0.000 |
| FOOTWORK | 0.006 | 0.006 | 0.000 | 0.000 | 0.000 | 0.006 | 0.006 | 0.000 |
| DEFLECT | 0.355 | 0.355 | 0.000 | 0.000 | 0.000 | 0.355 | 0.355 | 0.000 |

> 同一副牌、同一上下文；三选一+跳过。没有真相标签，也不声明谁『正确』。before/after 只用于暴露 replay 修正带来的影响；Elo 基准分歧只作复核线索，不作胜负判定。

## 7. 最高社区 Elo 基准（新增；不改模型/权重）

对上述四个三选一+跳过案例，另算两个纯社区 Elo 基准，分 all-runs 与 A10 两个 bracket 各自独立排名，**不混用**：

- `elo_highest_card`：只在被提供的 3 张候选里选 Elo 最高者，从不跳过。
- `elo_highest_with_skip`：把该 bracket 的 SKIP 行作为伪候选一起排名。

规则：Elo 缺失的候选被排除并显式列出（绝不当作 0）；若全部缺失则该基准标记为 unavailable。并列时 SKIP 优先，然后按 ID 升序。Elo 是揭示偏好，与关系图 community_draft 同源；**这不是独立效果验证，也不用作判定模型对错的裁判**，分歧只作为复核线索。all-runs bracket 的全部 Elo 只来自 `scores-all-skip.json`（含 SKIP 行）。

| 数据源 | bracket | URL | sha256 |
|---|---|---|---|
| `work/shiv-score-audit/scores-all-skip.json` | allruns | https://spire-codex.com/api/runs/scores/cards | `9d6cf1c5b1941b5f…` |
| `work/shiv-score-audit/scores-a10.json` | a10 | （快照未存 URL，不臆造） | `72ec9ad79c407182…` |

### 7.1 四个案例的基准选择

#### payoff 重型、缺小刀供给（`missing_producer`）

- 提供：BLADE_DANCE, CLOAK_AND_DAGGER, ADRENALINE
- 模型（机制-only）胜者：`BLADE_DANCE`；模型（community-on）胜者：`BLADE_DANCE`

| bracket | 方法 | 选择 | 可用性 | 与模型(机制/community)一致 | 缺失 Elo |
|---|---|---|---|---|---|
| allruns | elo_highest_card | ADRENALINE | available | False / False | - |
| allruns | elo_highest_with_skip | ADRENALINE | available | False / False | - |
| a10 | elo_highest_card | ADRENALINE | available | False / False | - |
| a10 | elo_highest_with_skip | ADRENALINE | available | False / False | - |

候选 Elo 原值（allruns / a10）：BLADE_DANCE=1633.3/1587.8; CLOAK_AND_DAGGER=1597.1/1582.2; ADRENALINE=1906.5/1958.5

#### 生成牌重型、缺 payoff（`lacking_payoffs`）

- 提供：ACCURACY, KNIFE_TRAP, ADRENALINE
- 模型（机制-only）胜者：`ADRENALINE`；模型（community-on）胜者：`ADRENALINE`

| bracket | 方法 | 选择 | 可用性 | 与模型(机制/community)一致 | 缺失 Elo |
|---|---|---|---|---|---|
| allruns | elo_highest_card | ADRENALINE | available | True / True | - |
| allruns | elo_highest_with_skip | ADRENALINE | available | True / True | - |
| a10 | elo_highest_card | ADRENALINE | available | True / True | - |
| a10 | elo_highest_with_skip | ADRENALINE | available | True / True | - |

候选 Elo 原值（allruns / a10）：ACCURACY=1682.6/1676.2; KNIFE_TRAP=1744.0/1763.4; ADRENALINE=1906.5/1958.5

#### 防御薄弱（`defense_poor`）

- 提供：FOOTWORK, LEG_SWEEP, ADRENALINE
- 模型（机制-only）胜者：`LEG_SWEEP`；模型（community-on）胜者：`LEG_SWEEP`

| bracket | 方法 | 选择 | 可用性 | 与模型(机制/community)一致 | 缺失 Elo |
|---|---|---|---|---|---|
| allruns | elo_highest_card | ADRENALINE | available | False / False | - |
| allruns | elo_highest_with_skip | ADRENALINE | available | False / False | - |
| a10 | elo_highest_card | ADRENALINE | available | False / False | - |
| a10 | elo_highest_with_skip | ADRENALINE | available | False / False | - |

候选 Elo 原值（allruns / a10）：FOOTWORK=1654.8/1760.6; LEG_SWEEP=1552.7/1626.1; ADRENALINE=1906.5/1958.5

#### 核心已饱和、重复张数多（`saturated_duplicates`）

- 提供：ADRENALINE, FOOTWORK, DEFLECT
- 模型（机制-only）胜者：`ADRENALINE`；模型（community-on）胜者：`ADRENALINE`

| bracket | 方法 | 选择 | 可用性 | 与模型(机制/community)一致 | 缺失 Elo |
|---|---|---|---|---|---|
| allruns | elo_highest_card | ADRENALINE | available | True / True | - |
| allruns | elo_highest_with_skip | ADRENALINE | available | True / True | - |
| a10 | elo_highest_card | ADRENALINE | available | True / True | - |
| a10 | elo_highest_with_skip | ADRENALINE | available | True / True | - |

候选 Elo 原值（allruns / a10）：ADRENALINE=1906.5/1958.5; FOOTWORK=1654.8/1760.6; DEFLECT=1490.0/1467.0

### 7.2 穷举局部对照（模型 vs 两个 Elo 基准）

对 8 个固定场景各取全部 C(14,3)=364 组三选一，共 2912 个决策；模型票为**机制-only**胜者（关掉 community prior，避免与同源的 Elo 双重计数）。仅记录一致/不一致计数与跳过率，**不是性能评分**。

| bracket | 基准 | 决策 | 一致 | 不一致 | 基准缺失 | 模型跳过率 | 基准选 SKIP 率 |
|---|---|---|---|---|---|---|---|
| allruns | elo_highest_card | 2912 | 1042 | 1870 | 0 | 0.007 | 0.000 |
| allruns | elo_highest_with_skip | 2912 | 1034 | 1878 | 0 | 0.007 | 0.011 |
| a10 | elo_highest_card | 2912 | 931 | 1981 | 0 | 0.007 | 0.000 |
| a10 | elo_highest_with_skip | 2912 | 889 | 2023 | 0 | 0.007 | 0.055 |

每个场景的不一致计数（compact，完整 2912 条见 `elo-baseline.json`）：

| 场景 | a10 card 不一致 | a10 with_skip 不一致 | model skip | 样例（offer → model / elo_card / elo_with_skip） |
|---|---|---|---|---|
| starter | 308 | 316 | 4 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → LEADING_STRIKE / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → BLADE_DANCE / INFINITE_BLADES / SKIP |
| producers_no_payoffs | 194 | 194 | 0 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → CLOAK_AND_DAGGER / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → CLOAK_AND_DAGGER / INFINITE_BLADES / SKIP |
| payoffs_no_producers | 309 | 323 | 4 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → BLADE_DANCE / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → BLADE_DANCE / INFINITE_BLADES / SKIP |
| defense_poor | 255 | 258 | 1 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → CLOAK_AND_DAGGER / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → CLOAK_AND_DAGGER / INFINITE_BLADES / SKIP |
| energy_heavy | 275 | 289 | 0 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → BLADE_DANCE / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → BLADE_DANCE / INFINITE_BLADES / SKIP |
| saturated | 159 | 159 | 10 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → CLOAK_AND_DAGGER / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → CLOAK_AND_DAGGER / INFINITE_BLADES / SKIP |
| duplicates | 211 | 213 | 1 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → BLADE_DANCE / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → BLADE_DANCE / INFINITE_BLADES / SKIP |
| no_supported_community_edges | 270 | 271 | 0 | BLADE_DANCE+CLOAK_AND_DAGGER+LEADING_STRIKE → CLOAK_AND_DAGGER / BLADE_DANCE / SKIP; BLADE_DANCE+CLOAK_AND_DAGGER+INFINITE_BLADES → CLOAK_AND_DAGGER / INFINITE_BLADES / SKIP |

> 对照只对 8 个手工示意场景做局部、同上下文比较；它既不是总体样本，也不代表谁更准。基准与模型的分歧只用于人工复核，不作胜负结论。

内建自检（缺失 Elo、并列、限制性 before 重建、快照牌数一致）10/10 通过：`missing_elo_excluded_listed`, `all_missing_is_unavailable_not_zero`, `tie_breaks_by_id_ascending`, `tie_prefers_skip_first`, `missing_skip_elo_listed`, `elo_ranking_deterministic`, `before_reconstruction_uses_restricted_best`, `before_restricted_ranking_has_skip`, `snapshot_deckcounts_match_current_fixtures_before`, `snapshot_deckcounts_match_current_fixtures_after`。

## 8. 限制与已知不确定性

- 社区快照可能不同于当前部署数据；无快照时间戳，故不声明数据版本。
- Score 是截断后的关联分数，不是 100% 胜率，也不是因果效应。
- Elo 是揭示偏好，与关系图 community_draft 同源；不是独立有效性验证。
- 八场景为手工示意，不是总体；相关系数只作描述，不作强度/胜率声明。
- 模型权重/上限仍为手工、未拟合；本次只改一处机制协同，未调参。
- Knife Trap replay 是否计为『打出一张牌』需 runtime 验证；Phantom 首刀与 replay 的时序交互仍未建模。
- 抽牌/能量仍是单次有界代理；不是战斗模拟。
- 审查中不使用 score / winrate 作为训练目标，不启动大规模拟合或采样。

## 9. 下一步（仅建议，默认关闭，之后再做）

- 本修正保持默认在线下模型内，**不接入生产 C# 评分**；如日后接入，应作为可关闭的参数分支并附对拍金样，默认 off。
- 需要 runtime 确认：Knife Trap 重放是否计为『打出一张牌』（影响 Afterimage / Phantom 首刀），以及 Phantom 首刀的精确判定。
- 若要继续校准：先收集少量人工可审查的具体三选一+跳过局面，逐项核对理由；不要用 score/winrate 当训练标签，不要启动大规模拟合/采样。

## 10. 复现与耗时

```text
python -m unittest discover -s tests -p test_shiv_prior_model.py
python scripts/shiv_prior_model.py
python scripts/audit_shiv_scores.py --freeze-before   # 冻结旧模型
python scripts/audit_shiv_scores.py                   # 完整审计 + 本报告
```

| 阶段 | 秒 |
|---|---|
| aggregate_and_correlate | 0.0004 |
| build_scenarios | 0.0074 |
| decision_cases | 0.0020 |
| load_baseline_snapshots | 0.0010 |
| load_community_scores | 0.0011 |
| local_contrast_baselines | 0.0227 |
| local_contrast_model | 0.5782 |
| scenario_diffs | 0.0000 |
| selfchecks | 0.0001 |
| sources_and_formula | 0.0064 |
| total | 0.6860 |

输出：`work/shiv-score-audit/audit.json`、`model-before.json`、`model-after.json`、`elo-baseline.json`、本文件。

# Owner 最终验收：实际 community-on 模型对照

接受为离线 v0.1 及策略基线工具，不代表模型胜过 Elo，也未接入 Bot。模型54项测试通过；14张牌两组社区字段逐项匹配原始快照；基线10项自检通过。修正前后参数和8副牌组内容一致。

独立核算实际开启社区加分的模型：8副手工牌组 × 364个三选一组合 = 2912个决定。对 all-runs 最高Elo必拿一致1033次（35.5%），包含SKIP则1022次（35.1%）；对A10最高Elo必拿一致888次（30.5%），包含SKIP则839次（28.8%）。模型跳过20次，A10 Elo含跳过基线跳过160次。社区加分改变了147次模型选择。详见 `work/shiv-score-audit/owner-community-on-baseline.json`。后文全量表为机制-only消融版本，勿混用。

这些仅是选择一致率，不是正确率或胜率；均匀枚举候选也不代表实际发牌分布。最高Elo是必须保留的低成本基线。下一阶段以分歧局面验证增量价值；若模型无法胜过该基线，应优先保留更简单策略，不能让模型自己的分数或Elo充当裁判。肾上腺素高排名本身不作为错误。
