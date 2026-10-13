# 第二开发种子参数化（build-pick-second-dev-seed）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `scripts/building_pick_node_repeat.py` 的**窄口径参数化**：在保持全部冻结限值与既定
运行/校验路径不变的前提下，新增 `--seed {PICKSEL01,PICKSEL02}`（默认 `PICKSEL01`），复用已审查的
`building_pick_node_repeat` 仪器，在第二开发种子上再做 8 动作 × 2 重复 = **16 场**。这是同一评测仪器的
第二个观测面，不是新的 runner，也不是收益/训练工具。

## 交付与范围

- 交付：`scripts/building_pick_node_repeat.py`、`scripts/building_pick_ranking_screen.py`、
  `tests/test_building_pick_node_repeat.py`、本文件。
- 未新增 runner；`ranking.write_run_inputs` 仅从硬编码 `SEED` 改为“使用已验证的 run seed，缺省仍为
  `SEED`”，既有调用者（ranking screen、parallel check）行为不变。
- 未修改 profile / nodes / dop / 预算 / 超时 / max-turns / 进程拓扑 / batch deadline / runtime 预检 /
  实际子进程并发计数；未改生产代码、未提交、未运行 `--run`。

## 种子参数化（运行前冻结）

| 维度 | 值 |
| --- | --- |
| CLI | `--seed PICKSEL01`（默认）或 `--seed PICKSEL02` |
| 允许集合 | `SUPPORTED_SEEDS = ("PICKSEL01", "PICKSEL02")`（`ranking_screen` 定义，node-repeat 复用） |
| 拒绝口径 | 留出种子（`PICKEVAL01/02`）与任意种子在**创建输出目录或启动任何进程之前**抛 `SourceValidationError`；CLI 层由 argparse `choices` 直接拒绝（退出码 2） |
| 传递 | seed 显式经过 `run_node_repeat(seed=...)` → source specs → 生成的 `input.json` → `outer-request.json` → 证据校验 → records → plan → summary；不使用也不会修改模块全局 |
| 默认回归 | 不传 seed 时 `plan["seed"]` / `summary["seed"]` 均为 `PICKSEL01`，输入与历史 20 秒格子逐字节一致 |

## 输入与期望哈希

- `PICKSEL01`：沿用原口径，`remap_baseline_cells` 直接复用经 `parallel.load_baseline` 重算校验过的
  历史 20000ms 格子，生成的 `input.json` 逐字节等于历史哈希。
- `PICKSEL02`：`_remap_cell_to_seed` 读取该基线格子的 `input.json`（其存储哈希已由 `load_baseline`
  原始校验），仅把 `seed` 改为 `PICKSEL02`，用与验收 writer 相同的 `json.dumps(..., ensure_ascii=False,
  indent=1)` + 平台换行写出字节并取 sha256，作为该格子的**期望输入哈希**。
  - 先校验基线输入 seed 确为 `PICKSEL01`，且改动前后 `diff_paths == {"seed"}`；否则在任何启动前报错。
  - 历史格子被 `deepcopy`，只改副本的 `raw_hashes.input_sha256`；原 `seed`、结果与哈希来源保留在
    `seed_remap` 元数据中，绝不重标历史记录。
  - 生成的第二 seed 输入哈希必须等于该期望哈希；同一组合两次重复逐字节相同。
  - 其他字段（deck/upgrade/context/character/encounter…）一律不变；源场景仍只改 seed。
- source adapter/manifest/历史 request 与历史格子的原有校验（`validate_baseline_source`、
  scenario sha256、source_seed、实际 resolved/loadout seed 校验）全部保留、未弱化。

## 历史对照的种子标注

当 `seed != PICKSEL01` 时，历史 20 秒格子是**另一个种子**的单一观测。summary 增加 `seed_context`，
并在 `cell_comparison_vs_20000ms` 与 `historical_20000ms_comparison` 每条对照上标
`seed_difference=true`、`baseline_seed=PICKSEL01`，明确 seed / budget / dop 同时不同、纯描述、
**同一 seed 的两次重复不算两个独立种子**。`historical_speed_ratio` 仍恒为 `null`。

## 下游聚合（owner 冻结，未在本工具实现）

owner 已在启动第二 seed 真实批次前冻结试点选牌规则：仅当两个种子各自 4 个动作都有效完整、且每个动作
两次重复精确一致时，才把重复折叠为**每个种子一个观测**；按字典序动作分数（跨 2 个开发种子的胜场、
胜场终局剩余 HP 求和、失败计 0）排序；并列顶部依次优先当前 BuildValue、否则 skip、否则 adapter 候选顺序；
缺失/不一致则弃权/回退 BuildValue，不主张证据。该规则由 owner 在真实批次后用两 seed 原始结果分析，
本任务**不实现**聚合模块。`PICKEVAL01/02` 仍未使用，无训练、无批量标签、无生产改动。

## 实际命令

```powershell
# 第二种子干跑（0 启动，只写 plan/journal/summary）
python scripts/building_pick_node_repeat.py --seed PICKSEL02

# 纯离线测试
python -m unittest discover -s tests -p test_building_pick_node_repeat.py
python -m unittest discover -s tests -p test_building*.py
```

真实 16 场由 owner/Astra 审查后单独显式 `--run` 执行；本任务**未执行 `--run`**。

## 本轮实际验证（DeepSeek）

- `python -m unittest discover -s tests -p test_building_pick_node_repeat.py`：**45 项通过**。
  新增覆盖：第二 seed 的 16 个唯一 run/命令全部保持冻结（budget 120000 / search-timeout 180000 /
  dop 2 / nodes 6000 / max-turns 40）；plan 与 summary 的 seed 为 `PICKSEL02`；生成输入等于第二 seed
  重映射期望哈希且同组合两重复逐字节相同；生成输入相对源场景仅 `seed` 不同；outer-request 仅 seed 不同；
  源目录运行后未被改动；历史对照标注 `seed_difference`；实际错误 seed（loadout 为 PICKSEL01）被拒为
  `mismatch`；期望哈希被篡改时 `prepare_run_input` 报错；基线输入被篡改由 `load_baseline` 拒绝、
  基线输入带外 seed 时 remap 拒绝；留出/任意种子在创建输出目录前被拒；CLI 以退出码 2 拒绝；第一 seed
  默认回归、模块与 ranking/parallel 全局未被改动。
- `python -m unittest discover -s tests -p test_building*.py`：**290 项通过**（含全部既有 building 测试，
  证明 ranking screen / parallel check / input probe 等既有调用者未回归）。
- 全部测试注入假 subprocess、线程安全假时钟、临时 fixture/baseline/runtime，**未启动任何真实进程**。

## 干跑产物

- 第二 seed：`work/pick-node-repeat/20260917T153542.579422Z/`
  `attempts=16 executed=false status_counts=16×dry_run`；`plan.seed=summary.seed=PICKSEL02`；
  `seed_context.seed_difference=true`、`budget_difference=true`、`dop_difference=true`、
  `repeats_are_independent_seeds=false`；16 个 run 的 seed 均为 `PICKSEL02`；
  `runtime.matches_baseline=true`、`baseline.validated=true`；无任何 input/进程产物。
- 第一 seed 回归干跑：`work/pick-node-repeat/20260917T153602.029191Z/`
  `plan.seed=summary.seed=PICKSEL01`、`seed_context.seed_difference=false`。

## 限制（不得省略）

- 两个 fixture 都是 PROXY，不是观察到的奖励画面；每格两次重复仍不能建立跨 seed 方差，也不是独立 seed。
- 历史格子是 20000ms dop1 的 PICKSEL01；第二批是 120000ms dop2 的 PICKSEL02，差异不可控、仅描述。
- 期望第二 seed 输入哈希由历史输入仅改 seed 推导；其可信度取决于 harness 构建与已验收 fixture 目录。
- 批截止为单调墙钟；慢机会把后续 run 记为 `not_started`，如实记录。
- 仅读初始搜索边界，不主张整场纯节点执行；本仪器不做聚合、不训练、不出标签。

## 待办

已完成 owner/Astra 独立审查、真实16场和两种子原始产物复核，结果如下。

## Astra 实际执行与验收（2026-09-17）

独立回归290项通过，日志 `work/pick-fixtures/second-seed-acceptance-tests.log`。owner干跑 `work/pick-node-repeat/20260917T153736.660263Z`。真实PICKSEL02批次：`work/pick-node-repeat/20260917T153844.881455Z`，独立验收在同目录 `owner-acceptance.json`，冻结选择在 `development-selection.json`。

16场全部输入/真实policy校验通过，14胜2负，无超时、异常或未完成。8/8同输入重复组的终局语义完全一致，两状态重复排序一致。并行阶段121.453秒，实际峰值16，原始CPU合计1185.7033秒，平均9.763逻辑核；16逻辑核下估计子进程平均利用率61.02%。首次搜索16/16未观察到时间边界，初始展开1014–6000节点；86次后续重规划没有逐次遥测，仍不能声称整场所有搜索均未触及时限。

| 动作 | 起始PICKSEL02（两次一致） | 后期PICKSEL02（两次一致） |
|---|---|---|
| skip | 胜70HP，5回合 | 负0HP，敌人85HP，9回合 |
| BACKFLIP | 胜70HP，6回合 | 胜11HP，9回合 |
| BLADE_DANCE | 胜57HP，5回合 | 胜33HP，6回合 |
| DAGGER_SPRAY | 胜57HP，5回合 | 胜27HP，9回合 |

跨种子比较使用同预算PICKSEL01重复批次 `work/pick-node-repeat/20260917T152011.472975Z`，而非4秒或20秒历史格子。两批运行二进制哈希一致，全部原始输入差异严格只有seed，原始结局重分类和实际loadout核验通过。起始最优集合从BLADE_DANCE/DAGGER_SPRAY变成skip/BACKFLIP；后期从BACKFLIP变成BLADE_DANCE。局部重复性通过，跨种子排序稳定性没有成立。

## 预先冻结规则的两种子选择

规则在真实PICKSEL02执行前落盘：`work/pick-fixtures/second-seed-selection-rule.json`，其哈希记录在选择产物中。每种子的重复不作为新增独立样本；每动作实际只有2个开发种子。两状态均满足全部动作完整且重复一致的汇总条件。

| 状态 | 动作 | 两种子胜场 | 获胜终局HP合计（负局计0） |
|---|---|---:|---:|
| 起始 | skip | 2 | 133 |
| 起始 | BACKFLIP | 2 | 127 |
| 起始 | BLADE_DANCE | 2 | 126 |
| 起始 | DAGGER_SPRAY | 2 | 126 |
| 后期 | skip | 1 | 41 |
| 后期 | BACKFLIP | 2 | 66 |
| 后期 | BLADE_DANCE | 2 | 56 |
| 后期 | DAGGER_SPRAY | 1 | 27 |

按先胜场再HP合计的冻结规则，起始选择skip（BuildValue为BACKFLIP），后期选择BACKFLIP（BuildValue为DAGGER_SPRAY）；本次没有用到并列破局。它们只是开发集选出的待检验动作，不是留出收益。

下一关冻结上述2个动作，在PICKEVAL01/02上仅比较所选动作与BuildValue，共2状态×2动作×2留出种子×2重复=16场上限。沿用N16×dop2与相同预算/硬限，先审查专用评估入口，避免把留出种子开放给选牌扫描。必须报告4个状态-种子配对的胜负/HP差异和缺失；重复不可充作新增样本，不得看留出结果重选动作。这个极小试验即使方向正面，也只决定是否值得扩大独立评估，不自动进入模型训练、生产上线或多角色推广。
