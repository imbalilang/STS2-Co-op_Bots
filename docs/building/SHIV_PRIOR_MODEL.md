# 小刀先验边际模型（SHIV_PRIOR_MODEL）v0

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


> 只读离线原型：`scripts/shiv_prior_model.py`（标准库、确定性、无网络/战斗/生产改动）。
> 单位是**启发式效用 utility**，不是 HP、伤害、胜率或因果强度。所有参数为**手工、未拟合**。
> 输出：`work/shiv-prior-model/results.json`、`work/shiv-prior-model/params.json`、`work/shiv-prior-model/scenarios/<name>.json`。

## 1. 范围与契约

- 白名单固定 14 张；基础牌（ASCENDERS_BANE / DEFEND_SILENT / NEUTRALIZE / STRIKE_SILENT / SURVIVOR）仅作固定上下文；PANACHE 在白名单外，输入即拒绝。
- 输入整副牌 `deck`（列表，重复张数有意义）+ 候选 `candidates` + 显式上下文 `context`（能量/抽牌/视界/能力延迟）。
- 未知 ID、升级后缀（含 `+`）、把基础牌当候选，一律 `ValueError`，不静默忽略。
- 上下文键未知、负数、非有限值、非整数视界，一律 `ValueError`；`turn_horizon=0` 合法且整副牌效用为 0。
- `params["assumptions"]` 在 `context` 缺省时提供默认上下文（不再被忽略）；传入 context 覆盖对应键。
- `evaluate_deck` 与 `rank_choices` 均为纯函数：不修改入参、确定性、可复算。
- 候选分 = `evaluate(deck+candidate) - evaluate(deck)`；SKIP 为同牌组 delta 0；候选 ID 去重；`winner == ranking[0]`（并列也成立）。
- 不做绝对 HP/胜率声明；不把 winrate / co / NPMI 作为强度。

## 2. 近似方程（紧凑版）

```text
N            = |deck|
turns        = turn_horizon                         # <=0 则整副牌全 0
base_seen    = initial_hand + (turns-1)*draw_per_turn
access_base  = min(1, base_seen/N)
extra_draw   = Σ_c count_c * access_base * draw_c    # 单次有界；抽到的抽牌不再回代
seen         = min(N, base_seen + extra_draw)
access       = seen/N
edpt         = draw_per_turn + extra_draw/turns
cycles       = clamp(turns / max(1, N/edpt), 1, cycle_cap)   # 仅可重复牌

opp_c        = count_c * access * (1 若 exhaust/power；否则 cycles)
energy_total = energy_per_turn*turns + opp_ADRENALINE * energy_ADRENALINE
paid_demand  = Σ_{cost_c>0} opp_c * cost_c
capacity     = clamp(energy_total/paid_demand, 0, 1)  (paid_demand=0 时为 1)

plays_c      = 0                    若不可打出
             = opp_c                若 cost_c == 0     # 零费不受付费容量压缩
             = opp_c * capacity     若 cost_c > 0

active_turns = max(0, turns - power_setup_delay_turns)
power_frac   = active_turns / turns
active(c)    = saturate(plays_c, active_payoff_cap) * power_frac   # 全部 payoff Power
saturate(x,cap) = cap * (1 - exp(-x/cap))

shiv_raw = Σ_{BLADE_DANCE,CLOAK,LEADING} shivs_c*plays_c + plays_IB*active_turns
S        = saturate(shiv_raw, shiv_supply_cap)

shiv_play        = w_shiv * S
accuracy         = w_acc  * active(ACCURACY) * S
phantom          = w_phan * active(PHANTOM_BLADES) * min(active_turns, S)     # 每活跃回合首刀
knife_trap       = w_trap * saturate(plays_KT,cap) * min(S, trap_replay_cap)  # 一次性重放，不递归
afterimage       = w_after* active(AFTERIMAGE)
                   * min(S + (Σ_c plays_c - plays_AFTERIMAGE), trigger_cap)
footwork         = w_dex  * active(FOOTWORK) * min(normal_block_plays, dex_cap)
direct_block     = w_block* saturate(Σ plays*block, block_cap)
weak             = w_weak * saturate(Σ plays*weak, weak_cap)
attack_damage    = w_atk  * saturate(Σ plays*damage, attack_cap)
draw_support     = w_draw * saturate(Σ plays*draw, draw_cap)
energy_support   = w_energy* saturate(Σ plays*energy, energy_cap)
power_setup      = -w_power * saturate(power_copies, power_penalty_cap)
deck_dilution    = -w_dilute * N
total = Σ 上述 components
```

设计要点与显式近似：

- **抽牌单次有界**：`extra_draw` 只按 `base_seen` 的可见比例估算一次，抽到的抽牌不再展开，避免自持抽牌递归；`extra_draw` 同时进入 `seen` 与 `cycles`，所以额外抽牌改变**卡牌可达性/循环**，不只是 `draw_support` 常量。
- **视界抽牌**：`initial_hand + (turns-1)*draw_per_turn`；`turns=0` ⇒ 0 次出牌、效用 0。
- **能量**：单一标量预算统一压缩**付费**出牌机会；`cost=0` 的牌不被压缩（零费不被付费容量压制）。这是粗略机会上限，不是精确战斗结算。
- **能力时机**：所有 payoff Power 共用同一 `setup_delay`，生效占比 `active_turns/turns`；`delay >= turns` ⇒ `power_frac=0` ⇒ 该能力无任何 payoff（不只 InfiniteBlades）。这是统一延迟近似。
- **区分一次性与可重复**：Blade Dance / Adrenaline 带 Exhaust，每张一次；其余可重复牌乘 `cycles`。
- **饱和与稀释**：复制到一定程度后 `saturate` 边际递减；`deck_dilution`、`access`、`capacity` 共同让无限复制最终负边际。
- **Phantom**：每活跃回合仅首张小刀，上限 `min(active_turns, S)`，不随小刀数线性叠加。
- **Afterimage**：每打出一张牌触发一次，**包含生成牌自身的打出**与生成的小刀；排除自身生效、排除未验证的 KnifeTrap 重放；不声明精确结算顺序（可用触发数上界）。
- **Knife Trap**：只消耗已消耗小刀池的一次性重放，重放不产生新供给、不假设 Afterimage 重放，上限 `trap_replay_cap`。
- **Footwork**：只放大普通直伤 Block，含白名单 `CLOAK_AND_DAGGER/BACKFLIP/LEG_SWEEP/DEFLECT` 与基础牌 `DEFEND_SILENT/SURVIVOR`；**不放大 Afterimage**。

## 3. 社区先验（有向、有界、仅用于同分/小幅微调）

```text
held          = dedup(deck ∩ 14 whitelist)          # 持有张数不放大先验
per_edge      = clamp(ln(lift), 0, edge_log_clamp) * offers/(offers+1000)
prior_raw     = mean(最大的至多 top_n 个正值 per_edge)   # 无正观测边则为 0
prior         = clamp(prior_raw, 0, prior_cap)           # 默认上限 0.25 utility
known_edges   = 观测到的有向边数（包含 lift<=1）
positive_edges= 其中 lift>1 的边数
```

应用规则（**不改变 pick vs skip 符号，不逆转大于 cap 的机制差距**）：

```text
best_positive = max(mech_delta) if 存在 >0 else None
if best_positive is None:  winner = ranking[0] 且必为 SKIP   # 所有机制 delta<=0
else:
  c 用先验 当且仅当  mech_delta(c) > 0 且 best_positive - mech_delta(c) <= prior_cap
  final(c) = mech_delta(c) + prior(c)   (否则等于 mech_delta(c))
```

- 仅用观测到的 `持有一被提供` draft 方向；缺失边记为 unknown（中性），返回 `unknown_edges` 计数，不记负证据；`lift<=1` 记入 `known_edges` 但不产生正先验、也不记负证据。
- `reliability = offers/(offers+1000)` 是显式启发式，**不是独立跑局置信度**。
- `--community off` 可做消融；`prior_raw` 与 `prior_applied` 分别记录，`prior_used` 仅在正先验实际生效时为真。

## 4. 参数（全部位于 `params.json`，标签 `manual_unfitted_heuristic`）

上下文默认由 `params.assumptions` 提供：`initial_hand=5`、`cards_drawn_per_turn=5`、`energy_per_turn=3`、`turn_horizon=4`、`power_setup_delay_turns=1`。

| 类型 | 名称 | 值 |
|---|---|---|
| 权重 | w_shiv_play / w_accuracy_per_shiv / w_phantom_first_shiv | 1.00 / 0.60 / 1.10 |
| 权重 | w_knife_trap_replay / w_afterimage_block / w_dex_block | 0.50 / 0.18 / 0.55 |
| 权重 | w_direct_block / w_weak / w_attack_damage | 0.35 / 0.40 / 0.12 |
| 权重 | w_draw_support / w_energy_support | 0.22 / 0.30 |
| 权重 | w_power_setup_penalty / w_deck_dilution | 0.10 / 0.02 |
| 上限 | shiv_supply_cap / active_payoff_cap / cycle_cap | 9.0 / 1.0 / 2.0 |
| 上限 | knife_trap_replay_cap / afterimage_trigger_cap / dex_block_cap | 6.0 / 24.0 / 3.0 |
| 上限 | draw/energy/block/weak/attack/power 相关上限 | 6 / 3 / 36 / 4 / 36 / 5 |
| 社区 | prior_cap / edge_log_clamp / reliability_denominator / top_n | 0.25 / 1.0 / 1000 / 2 |

## 5. 八个示意场景结果（community on；**场景非有效性证据**）

| 场景 | 牌组张数 | winner | top3 |
|---|---|---|---|
| starter | 11 | LEADING_STRIKE | LEADING_STRIKE, BLADE_DANCE, INFINITE_BLADES |
| producers_no_payoffs | 17 | ADRENALINE | ADRENALINE, ACCURACY, PHANTOM_BLADES |
| payoffs_no_producers | 19 | BLADE_DANCE | BLADE_DANCE, INFINITE_BLADES, LEADING_STRIKE |
| defense_poor | 14 | LEG_SWEEP | LEG_SWEEP, CLOAK_AND_DAGGER, DEFLECT |
| energy_heavy | 21 | BLADE_DANCE | BLADE_DANCE, INFINITE_BLADES, LEADING_STRIKE |
| saturated | 40 | ADRENALINE | ADRENALINE, AFTERIMAGE, DEFLECT |
| duplicates | 20 | ADRENALINE | ADRENALINE, DEFLECT, PHANTOM_BLADES |
| no_supported_community_edges | 13 | ADRENALINE | ADRENALINE, DEFLECT, CLOAK_AND_DAGGER |

社区先验示例（`payoffs_no_producers`，`prior_cap=0.25`，实际输出）：BLADE_DANCE 机制 3.275、`prior_raw≈0.491`、`prior=0.25` 且为最高 ⇒ 用先验后 3.525；INFINITE_BLADES 机制 3.235，与最高差距 0.040 ≤ cap ⇒ 用先验后 3.485；LEADING_STRIKE 机制 2.594，差距 0.681 > cap ⇒ 不用先验；ACCURACY 机制 −0.493（非正）⇒ 先验 `prior_applied=0.25` 但 `prior_used=false`，不救非正。说明先验只微调正机制候选、不逆转大差距、不救非正。

每个场景文件含全部 14+SKIP 排序、机制 delta、组件 delta、社区变化；`results.json` 聚合同一份数据。

## 6. 测试与复现

```text
python -m unittest discover -s tests -p test_shiv_prior_model.py
python scripts/shiv_prior_model.py
python scripts/shiv_prior_model.py --input in.json --output out.json [--community off]
```

本机实际观测：47 个测试全部通过（`Ran 47 tests in 0.044s`）；默认生成器即时导出 8 个场景，远低于 1 秒。测试覆盖（含本轮 owner 修正的针对性回归）：入参不被修改、顺序无关、SKIP 为 0、重复张数计入、未知/升级拒绝、上下文未知键/负数/非整数/非有限拒绝、零视界全 0、`params.assumptions` 缺省生效、视界抽牌公式、额外抽牌改变可达性与循环（非仅 draw_support）、零费不受付费容量压制、`delay>=horizon` 全 payoff 为 0、`power_frac` 缩放、Afterimage 计入生成牌自身打出且排除自身生效与 KnifeTrap 重放、Footwork 放大基础 DEFEND_SILENT/SURVIVOR、Phantom 用活跃回合封顶、无生成牌时 ACCURACY 协同为 0、Knife Trap 无递归供给、大复制扫描最终负边际、ACCURACY(持有)→BLADE_DANCE(候选) 有向可得而反向缺失、`known_edges` 计入 `lift<=1` 且 `positive_edges` 分开、先验不救非正/不逆转大于 cap 的差距、候选去重、`winner==ranking[0]`、community off 一致、`--input/--output --community off` CLI 往返。

## 7. 限制（必须先解决再谈接入 C#）

1. **未拟合、未验证**：权重/上限为手工设定，单位仅是相对 utility；不能在 UI 上表述为伤害或胜率。
2. **抽牌/能量代理仍然粗糙**：抽牌是单次有界估算；能量是单一标量，忽略每回合预算分配、保留手牌、弃牌/消耗回收、遗物与能力叠加产生的能量差；固定短视界不等于真实战斗长度。
3. **不是战斗模拟**：忽略敌人、目标选择、升级、状态层数时序等运行时细节；Afterimage 触发数不声明精确顺序。
4. **运行时规则待确认**：Exhaust/Retain、Knife Trap 重放是否计为打出、Phantom 首刀判定等需以运行时为准（见 `SHIV_RELATIONSHIP_GRAPH.md`）。
5. **社区数据边界**（见 `RELATIONSHIP_DATA_QUALITY.md`）：观测型、跨版本/角色/人数混合、方向与样本量需保留，本模型只取有向 draft lift 且加 cap。
6. **接入前验收**：需在独立样本或战斗采样上校准；需给出角色/版本分层；需在 C# 端复刻同一方程并以金样（同一 deck/candidate 的 JSON 对拍）验证一致；需确认界面上下文（能量、手牌、回合预期）的来源与展示口径。

## 8. 文件与溯源

- 模型：`scripts/shiv_prior_model.py`
- 测试：`tests/test_shiv_prior_model.py`
- 参数/结果：`work/shiv-prior-model/params.json`、`results.json`、`scenarios/*.json`
- 只读输入：`work/shiv-relationship-graph/graph.json`（community_draft 层）、`docs/building/SHIV_RELATIONSHIP_GRAPH.md`、`docs/building/RELATIONSHIP_DATA_QUALITY.md`
- 生成器标签：`manual_unfitted_heuristic`；无生产代码改动。
# Owner 验收补充

已接受为离线、人工参数的 v0 原型；不代表实战收益通过验收，不接入生产评分。

- 最终修正：额外抽牌先按初步能量预算折减，避免零能量时 Acrobatics 凭空改善可达性；Context 对象和字典接受相同合法性检查。抽牌与能量仍是单次粗略估算，不是逐回合模拟。
- 最终局部测试：47 项通过，0.047 秒；另核对零能量付费抽牌为零、非有限 Context 对象被拒绝。
- 社区关闭对照：8 个场景首选均未变化。
- 单个权重分别乘 0.8 / 1.2，共 208 次场景比较，仅 1 次首选变化。结果见 `work/shiv-prior-model/owner-sensitivity.json`。稳定性不等于正确性。
- 肾上腺素在 4/8 个示意场景首选，应优先核对是否高估抽牌/能量；当前样例不能证明模型学会小刀流，也不是训练集或独立效果验证。
- 下一步优先收集少量人工可审查的具体三选一加跳过局面，逐项核对理由和模型适用边界。不要因此重新启动大规模战斗标注；社区胜率不能当因果标签。
