# 选牌动作确认批（72 场实测）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


- 生成时间：2026-09-17T17:57:06Z；工具：`work/pick-confirmation/run_confirmation.py`
- 运行目录：`work/pick-confirmation/20260917T175352.153845Z/`
- 原始证据：`start-manifest.json`、`journal.jsonl`（72 条，含绝对目录）、`summary.json`、每场 `runs/confirm/<name>/{input.json,outer-request.json,harness-result.json,evidence/*,search-policy.json}`
- 协议：`work/pick-confirmation/protocol.json`（owner，未改），sha256 `0bcd9194…`
- 选择：沿用 `work/pick-expansion-batch/run/selected-actions.json` 原始字节 sha256 `ab1c115c…`，与上一批 chosen/baseline 完全一致，未重算 BuildValue
- 设计：3 副已选牌组 × 2 遭遇（SOUL_NEXUS_ELITE / FROG_KNIGHT_NORMAL）× 6 新种子（PICKCONF01..06）× 2 动作 = 72
- 上限：N16×dop2、nodes6000、soft120000、searchTimeout180000、maxturn40、单进程300s、整批1800s、重试0；同种子配对
- 取值：HP70 / A10 / act2 / 无遗物药水 / 无附魔道具；源卡组 SHA 由 prior `load_decks` 复核

## 运行账目

- 启动 72 / 完成 72 / eligible 72 / 超时 0 / 未启动 0（全部 launched=true, exit=0, policy.ok=true）
- 峰值并行 16；整批墙钟 193.84s；子进程 CPU 2785.41s；72/72 记录含 CPU；平均忙核 14.37
- 运行时前后一致：CombatSolverDll 实际解析 sha256 `a8abf08c…`（未用 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 覆盖），sts2.dll sha256 `0861bfa1…` 与 frozen adapter 声明一致，harness/dotnet/runtimeconfig 均未变

## 新增 36 对（本批，先于旧 12 对）

- better/tie/worse/unavailable = **19 / 3 / 14 / 0**
- 主指标：selected 胜 31 vs baseline 胜 29（净 +2）；不一致胜负转移 loss→win **4**、win→loss **2**
- 次级：共同胜场 HP 差（selected−baseline，带符号）n=27，总和 +26，均值 +0.96，正负均有（范围 −33…+25）

| deck | better/tie/worse | selected胜/baseline胜 | loss→win / win→loss |
| --- | --- | --- | --- |
| 071d89030c0fad1f (DAGGER_SPRAY vs BLADE_DANCE) | 6/2/4 | 10/8 | 2/0 |
| 10c4d71b61aeec36 (skip vs DAGGER_SPRAY) | 8/0/4 | 12/11 | 1/0 |
| 14a8539d859aa755 (skip vs BACKFLIP) | 5/1/6 | 9/10 | 1/2 |

| encounter | better/tie/worse | selected胜/baseline胜 | loss→win / win→loss |
| --- | --- | --- | --- |
| FROG_KNIGHT_NORMAL | 10/0/8 | 18/17 | 1/0 |
| SOUL_NEXUS_ELITE | 9/3/6 | 13/12 | 3/2 |

## 旧 12 对（上一批 held-out，未重跑，取自已验收 summary）

- better/tie/worse = 6 / 2 / 4；selected 胜 8 vs baseline 胜 9
- 按组：071d 2/3（better2 tie1 worse1）、10c4 4/3（better3 worse1）、14a8 2/3（better1 tie1 worse2）
- 按遭遇：FROG 6/6（better4 worse2）、SOUL 2/3（better2 tie2 worse2）
- 种子为 EXPANDEVAL01/02，与本批新种子不重叠

## 合并 48 对（仅描述，两组相关设计不独立）

- better/tie/worse/unavailable = 25 / 5 / 18 / 0；selected 39胜 vs baseline 38胜（48对中净+1胜）。

## 结论与下一步决定

- 本批新增 36 对偏正向：better 多于 worse，胜场 +2，并有 4 次由负转胜；但存在 2 次由胜转负、共同胜场 HP 均值仅 +0.96。
- 旧 12 对方向相反（baseline 多 1 胜），合并后接近中性/混合。**不据此声称训练收益或生产增益**；这只是 3 副终端 PROXY 卡组、单负载、相关种子下的离线描述性证据。
- 决定：不改动生产 BuildValue、不训练、不扩批、不重试；本确认批到此结束，交 owner 一次性验收。

## Owner验收

Astra独立核对72场原始输入/请求/结果哈希、实际载入牌组与种子、N16×dop2搜索策略，并重算36对胜负/HP比较，全部一致。冻结选择原始哈希未变，运行时含实际CombatSolverDll的批前/批后哈希一致。验收文件：work/pick-confirmation/owner-acceptance.json。修正初稿中旧批SOUL基线胜场数为3；不追加战斗或无关测试。

后续研究判断：合并仅净多1胜，收益较小且仍有一套牌组持续退步；不建议继续在同3套牌组上大量加种子，更不据此扩产模型标签。若继续，优先用新的可加载牌组检验泛化，复用现有执行器，仍以胜场为首要指标。
