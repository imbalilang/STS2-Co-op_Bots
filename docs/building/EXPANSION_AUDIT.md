# 选牌扩展审计：数据与生产策略（2026-09-18）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


> **路径提示（2026-09-20）**：本文提到的 `CombatSolver-main/`、`CombatSolver-upstream-main/`、
> `StS2.RandomForeseer-0.13.14/` 已在本次目录整理中移入 `third_party/` 下。
> 原文保留不改写；引用路径请自行加 `third_party/` 前缀。相关脚本已同步更新。


DeepSeek 完成源码/样本审计；Astra 复核关键调用路径、全量解析321份跑局记录并纠正初稿。结论：**路线值得继续，但下一步应恢复真实状态来源并验证重建，暂不扩产标签或训练。** 本轮未联网、未新增战斗、未构建测试、未改生产源码。

## 1. 本地资产与可用性

| 目录 | JSON文件数 | 含义 |
|---|---:|---|
| `work/spire-codex/decks/runs` | 832 | 压缩终局牌组、遗物和获得牌序列，不是选牌前快照 |
| `work/spire-codex/runs-full` | 321 | 含楼层历史的完整跑局记录，需验证状态重建 |
| `work/spire-codex/runs` | 1 | 额外完整记录，为当前版本单人IRONCLAD |
| `work/spire-codex/draft-recs` | 540 | 聚合推荐/共现统计，不是决策状态 |
| `outputs` | 119 | 抽查到覆盖率/发布manifest等；不据抽样宣称目录绝无状态 |

枚举证据：`work/building-expansion-audit/asset-index.json`。全量字段普查：同目录 `owner-schema-census.json`。计数针对文件或玩家-楼层记录，不等于独立奖励界面数，更不等于可用测试状态数。

321份完整记录全部可解析；90份单人、231份多人，247份为v0.111.0。共23,613条玩家-楼层记录含非空`card_choices`，这些记录均有`current_hp/max_hp`，但`deck/relics/potions`逐节点完整快照字段覆盖数均为0。6份顶层`has_replay=true`只代表标记存在，不代表回放文件已取得。

这321份记录中**当前版本单人SILENT为0份**；额外`runs/run1.json`也不是SILENT。另一方面，压缩终局牌组库有**45份v0.111.0、单人SILENT、A10**记录（21胜24负），是可恢复原始历史的明确入口。不能把已有多人Silent记录去掉队友就称为真实单人状态。

## 2. 重建缺口：信息有一部分，直接快照没有

- 完整历史的`map_point_history[act][node].player_stats[]`含候选`card_choices`及`was_picked`、获得/移除/变形/升级/降级/附魔等事件；并非只有`cards_gained`。终局`players[].deck`还可能含`current_upgrade_level`、`enchantment`、`props`。
- 药水使用/丢弃/购买和遗物变动也有对应历史字段，不能说信息完全缺失。字段定义见 `work/decompiled-v0.111.0/MegaCrit/sts2/Core/Runs/PlayerMapPointHistoryEntry.cs:64`（至购买/变动字段）；实际例子见 `work/spire-codex/runs-full/007a678e17f52b04.json`。完整字段覆盖计数见普查JSON。
- **同房间内的事件先后、多个奖励界面的分组、HP记录相对选牌的时点、重复卡的身份及升级/附魔归属，尚未验证。** 不能把整个楼层的`card_choices`直接当作一次奖励，也不能把全false直接解释为一个完整可跳过界面。
- 顶层seed不等于该决策点的完整RNG状态；有角色/队伍元数据不等于保留了队友当时状态。社区来源也不能证明操作者一定是真人而非Bot。
- 当前没有经过验证并导出的真实pre-reward fixture；**可重建数量仍未知**，不是断言真实状态不存在。

压缩过程见 `scripts/fetch-spire-codex-decks.py:106`（获得牌序列）和`:128`（终局摘要）；原始payload缓存入口见 `scripts/fetch-spire-codex-run-details.py:105`。恢复时必须保存原始历史，不能再次经过终局压缩器丢失事件。

生产日志也不能直接补齐：`src/CoopBots/BuildTrace.cs:58`记录候选、评分、pick和size，没有同条完整牌组/HP/遗物/药水/seed/遭遇；`LogDeck`在其他时间记录紧凑牌组。调用在`src/CoopBots/BotBrain.cs:146`，奖励入口在`src/CoopBots/RewardPatches.cs:64`。这支持后续必要时做结构化捕获，但本轮不修改生产。

## 3. 生产与研究并非同一战斗策略

| 维度 | 当前生产默认Pro | 已验收研究批次 |
|---|---|---|
| 搜索 | `KernelTeamSearch` + `KernelCombatEvaluation` | `CombatBeamSolver`，Evaluate模式 |
| 节点/预算 | 1152节点、300ms计算/450ms墙钟检查阈值 | 6000节点、120000ms软限、180000ms搜索硬限 |
| 形状/调度 | 深度9、宽8、2轮；4ms分片，捕获线程执行 | Medium；dop2；N16是离线批调度 |
| 执行 | 提交第一动作，落子后按真实局面再算 | 部署计划，到不可执行或用完时再算 |
| 上下文 | eligible Bot、队友/人类、药水、焦点和回退 | 当前fixture为单人、无遗物/药水的代理状态 |
| 源码版本 | vendor源记录0.33.9，外加自有团队搜索 | `CombatSolver-upstream-main`源版本0.40.0；实际运行DLL哈希已冻结 |

生产基础元组是(9,8,768,200,300,2)，Pro乘1.5得到上表；Flash乘0.5。依据 `src/CoopBots/KernelCombatPlanner.cs:237`、`src/CoopBots/BotDifficulty.cs:59`。这不是每次调用绝不超时的硬保证，捕获/展开并非全部可抢占。主调用在`src/CoopBots/BotRuntime.cs:176`，药水优先与回退在`:195`和`:226`；每动作重算在`KernelCombatPlanner.cs:372`；线程要求在`src/CoopBots.Kernel/KernelTeamSearch.cs:53`。

研究调用见 `CombatSolver-upstream-main/tools/OfflineSearchHarness/Program.cs:212`及`ModRuntime.cs:282`。宿主的Coordinator选项是CombatSolver自身协调器，**不等于**CoopBots生产路径。版本依据 `CombatSolver-upstream-main/CombatSolver.csproj:25`；另一个`CombatSolver-main`才是0.39.0，初稿误混了两份源码，已纠正。生产vendor来源见`src/CoopBots.Kernel/UPSTREAM.json:2`。

因此，当前结果可作为独立oracle条件价值的局部证据，尚不能叫作生产Bot收益。不同教师/执行器不意味着蒸馏一定不可行，也不要求两边代码或预算完全相同；需要明确标签依赖的状态与执行策略，并用生产策略另做迁移验收。**只降低离线节点预算不能复现生产行为。**

## 4. 已冻结的最小下一步

恢复清单：`work/building-expansion-audit/raw-recovery-candidates.json`，共6个已有run hash，来源文件SHA已记录。规则为当前版本、单人Silent、A10；按文件名固定顺序，各取3胜3负，排除相同终局牌组签名：

- 胜：`071d89030c0fad1f`、`1ea2817c7dacccfc`、`2926074ecfa9f90c`。
- 负：`0ee0ce474be7f8cc`、`10c4d71b61aeec36`、`14a8539d859aa755`。

终局签名去重只是初筛，不等于不同牌组家族；恢复后仍需按整局/近亲牌组分组。**六个跑局不是六个合格状态。**

1. 最多获取这6份原始历史，最多12次请求（每份仅对临时失败多重试一次），保存来源、哈希及失败，不额外扫描或扩抓。本轮尚未联网。
2. 检查逐房间的候选分组、事件顺序、升级/附魔/移除/变形及遗物/药水状态；能证明一致的才导出候选pre-reward状态。无法判定的字段必须保留未知，不能用0/空表冒充真实值。
3. 按来源整局/牌组家族分割，最多6个新状态；是否足够、如何选遭遇及计算预算待重建验收后冻结。复用现有运行/输入校验工具，不再复制批运行器。
4. 另做生产策略保真/迁移方案；研究oracle收益和生产执行收益分别报告。PICKEVAL01/02已使用，不能再作为未见最终确认集；不根据本次留出结果改选原批动作。
5. 只有原始历史验证后仍不足，才单独立项最小结构化pre-reward捕获；不先改生产、不自动加跑、不扩产标签或训练。

## 5. 验收记录

DeepSeek原始证据已保存在 `work/building-expansion-audit/worker-evidence-original.json`；经Astra纠正的证据在`worker-evidence.json`，纠正点和原始哈希可追溯。全量普查、六个恢复候选及源码核对是本轮新增成果。未运行测试，因为没有实现代码变更；源码/研究脚本完整性以同目录`owner-acceptance.json`为准。
