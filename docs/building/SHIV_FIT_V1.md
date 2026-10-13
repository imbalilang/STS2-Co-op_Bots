# SHIV_FIT_V1 — 首次有界战斗数据参数优化

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


生成时间: 2026-09-18T05:19:40.370356Z
工具: `work/shiv-fit-v1/run_training.py`
运行目录: `work/shiv-fit-v1/20260918T044417.789623Z`
状态: `ok`
训练效用: `U = 1[win] + 0.5*effective_playerHP/70 - 0.5*min(final_enemyHP/200, 1); effective_playerHP=0 on loss; final_enemyHP=0 on win`
分裂: 3 训练场景 (producers/payoffs/defense, 200 seed 共享) + 1 heldout (`duplicates`, 唯一 200 seed)；4 场景 x 3 选择 x 200 = 2400 场上限。
上限: N16 dop2 nodes6000 soft120000ms searchTimeout180000ms maxturn40；每场 300s，每场景 1800s，全局战斗上限 3600s；无重试。
实际发起 2400/2400 场；eligible 2400；墙钟 2016.5s；CPU 29979.5s；峰值并行 16。
运行时前后一致: True。

## 参数拟合（仅训练数据）

- 防卫组权重: w_direct_block, w_weak, w_dex_block, w_afterimage_block；引擎组权重: w_draw_support, w_energy_support；网格 6x6=36，惩罚系数 0.002。
- 选定乘子: defense x0.5 · engine x0.5；目标 0.2277 = 原始均值 0.2296 − 惩罚 0.0019；训练 regret 0.0000。
- 基线 (1,1) 目标 0.1815；是否优于基线: True；选定即基线: False。
- 冻结参数 sha256: `a0a63060c0f18fa5837d309da010ceb17ac25c472a99ceaa26908d3ffffe454c`（heldout 前冻结）。
- 训练各案例匹配 seed 数: {'train-producers': 200, 'train-payoffs': 200, 'train-defense': 200}。

| 场景 | 选定动作 | 实测 U | 最佳动作 | 最佳 U | regret |
| --- | --- | --- | --- | --- | --- |
| train-producers | ACCURACY | -0.2280 | ACCURACY | -0.2280 | 0.0000 |
| train-payoffs | BLADE_DANCE | -0.2632 | BLADE_DANCE | -0.2632 | 0.0000 |
| train-defense | ADRENALINE | 1.1801 | ADRENALINE | 1.1801 | 0.0000 |

注意：选定乘子位于网格边界（0.5 或 2.0），真实最优可能在网格之外；本次按预先固定的网格不扩展搜索。

## 各场景选择与配对效用差（200 seed）

tuned 为冻结拟合参数；baseline 为默认参数；elo 为 A10 快照含 skip 的最高 Elo。配对差仅用三动作都 eligible 的匹配 seed；误差/缺失/超时从不算作败场。

| 场景 | split | tuned | baseline | elo | 匹配 seed | U(tuned−baseline) 均值 [95% CI] | U(tuned−elo) 均值 [95% CI] |
| --- | --- | --- | --- | --- | --- | --- | --- |
| train-producers | train | ACCURACY | ADRENALINE | ADRENALINE | 200 | 0.1442 [0.1345, 0.1539] | 0.1442 [0.1345, 0.1539] |
| train-payoffs | train | BLADE_DANCE | BLADE_DANCE | LEG_SWEEP | 200 | 0.0000 [0.0000, 0.0000] | 0.2075 [0.1940, 0.2210] |
| train-defense | train | ADRENALINE | ADRENALINE | ADRENALINE | 200 | 0.0000 [0.0000, 0.0000] | 0.0000 [0.0000, 0.0000] |
| holdout-duplicates | holdout | KNIFE_TRAP | ADRENALINE | ADRENALINE | 200 | 1.1995 [1.1366, 1.2623] | 1.1995 [1.1366, 1.2623] |

## 原始胜率 / 玩家 HP / 敌人 HP（暴露权衡）

| 场景 | 动作 | n | 胜 | 胜率 [Wilson95] | 有效玩家 HP 均值 | 敌人 HP 均值 |
| --- | --- | --- | --- | --- | --- | --- |
| train-producers | ACCURACY | 200 | 0 | 0.0000 [0.0000, 0.0188] | 0.0000 | 91.2050 |
| train-producers | ADRENALINE | 200 | 0 | 0.0000 [0.0000, 0.0188] | 0.0000 | 148.8800 |
| train-producers | skip | 200 | 0 | 0.0000 [0.0000, 0.0188] | 0.0000 | 153.4400 |
| train-payoffs | BLADE_DANCE | 200 | 0 | 0.0000 [0.0000, 0.0188] | 0.0000 | 105.2950 |
| train-payoffs | LEG_SWEEP | 200 | 0 | 0.0000 [0.0000, 0.0188] | 0.0000 | 188.2950 |
| train-payoffs | skip | 200 | 0 | 0.0000 [0.0000, 0.0188] | 0.0000 | 192.4900 |
| train-defense | LEG_SWEEP | 200 | 199 | 0.9950 [0.9722, 0.9991] | 23.4700 | 0.0800 |
| train-defense | ADRENALINE | 200 | 199 | 0.9950 [0.9722, 0.9991] | 25.9250 | 0.0500 |
| train-defense | skip | 200 | 195 | 0.9750 [0.9428, 0.9893] | 20.8550 | 0.6650 |
| holdout-duplicates | KNIFE_TRAP | 200 | 186 | 0.9300 [0.8859, 0.9578] | 27.4700 | 3.2100 |
| holdout-duplicates | ADRENALINE | 200 | 11 | 0.0550 [0.0310, 0.0958] | 0.4250 | 55.7200 |
| holdout-duplicates | skip | 200 | 4 | 0.0200 [0.0078, 0.0503] | 0.1000 | 63.6850 |

## 成本 / 计数

| 场景 | 墙钟 s | 峰值 | 发起 | 完整 | eligible | timeout | error | not_started | abort |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| train-producers | N/A | N/A | 600 | 600 | 600 | 0 | 0 | 0 | False |
| train-payoffs | N/A | N/A | 600 | 600 | 600 | 0 | 0 | 0 | False |
| train-defense | N/A | N/A | 600 | 600 | 600 | 0 | 0 | 0 | False |
| holdout-duplicates | 469.7 | 16 | 600 | 600 | 600 | 0 | 0 | 0 | False |

## 诚实结论

拟合选中参数在训练目标上优于默认基线；heldout paired U(tuned−baseline) 均值 1.1995，CI [1.1366, 1.2623] → heldout 上相对基线为正。单一 heldout 场景不足以外推。

## 局限

- 全部为合成 PROXY：状态由固定场景重建，非观测奖励屏；遗物/药水/序列化词条被显式剥离。
- train-defense 的 CLOAK_AND_DAGGER x2 与 BACKFLIP x2 是固定加入，非真实 run 牌组。
- 只有 3 个基础训练场景；200 个 seed 不是 3x200 个独立参数样本，复杂度受限于 2 个乘子。
- 训练选择复用同一批标签，训练集上的参数不确定性有偏；heldout 只做一次固定评估。
- 效用 U 是有界启发式目标，不是胜率/HP/真实收益；胜场权重更高，失败与低血胜场仍可区分。
- 缺失/错误/超时从不算败场，也不重试；固定 200 seed 不保证精度，CI 跨 0 即不确定。
- 单一 heldout 场景不能外推为广泛泛化；未做生产集成、未改全局默认参数。

## 原始证据

- 启动前 manifest: `work/shiv-fit-v1/20260918T044417.789623Z/start-manifest.json`
- 最终 manifest: `work/shiv-fit-v1/20260918T044417.789623Z/manifest.json`
- 战斗 journal: `work/shiv-fit-v1/20260918T044417.789623Z/journal.jsonl`
- params-before: `work/shiv-fit-v1/params-before.json`
- params-trained: `work/shiv-fit-v1/params-trained.json`
- grid: `work/shiv-fit-v1/grid.json`
- train-summary: `work/shiv-fit-v1/train-summary.json`
- summary: `work/shiv-fit-v1/20260918T044417.789623Z/summary.json`
- 每场原始输出位于运行目录 `runs/<case>/<name>/`。
