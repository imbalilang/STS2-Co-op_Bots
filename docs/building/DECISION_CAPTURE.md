# 选牌决策捕获（build-pick-decision-capture）

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


本文件记录 `src/CoopBots/BuildDecisionCapture.cs` 这组**默认关闭的本地研究记录器**的目的、字段、时点、启用方式、上限与限制。它**不修改选牌策略**，只回答一个问题：`BotBrain.ChooseCardReward` 在打分前看到的真实模型状态是什么，以及这次打分最终选了哪个索引/是否跳过。它**不是**社区历史重建，也**不是**收益或强度结论。

## 状态（2026-09-18 复审修订，DeepSeek 实现，Astra 已完成离线验收）

- 文件：`src/CoopBots/BuildDecisionCapture.cs`。
- 集成：`src/CoopBots/BotBrain.cs` 的 `ChooseCardReward` 在原 `BestReward` 调用前后各加一次捕获调用，其余逻辑、参数、返回值、异常行为不变。
- 测试：`tests/PatchSmoke/BuildDecisionCaptureScenarios.cs`、`tests/PatchSmoke/Program.cs` 的 `--building-capture-only` 分支。
- 校验：`scripts/verify-building-decision-capture.ps1`。
- 证据目录：`work/building-decision-capture/<UTC>/`。
- 复审修订：run 身份改为每 `RunState` 对象弱身份 `Guid`；追加前有紧凑结构校验；同一 `PendingDecision` 只消费一次；药水包装字段由 `card` 改名 `potion`；`EnsureSession` 移入捕获 `try`；新增真实遗物/药水/附魔/Silent 模型与故意拒绝坏记录的测试。

## 启用与默认关闭

只有同时满足下列条件才启用，任一不满足即**不写盘、不序列化、不接触 Player**：

1. 环境变量 `COOPBOTS_CAPTURE_DECISIONS` 精确等于 `1`；
2. 环境变量 `COOPBOTS_CAPTURE_DIR` 是非空的**绝对**路径目录。

不枚举环境变量，不隐式开启。目录为空、空白或相对路径时按失败拒绝。`CapturePreState` 的整个入口（含会话建立与环境检查）都在捕获 `try` 内，目录无法创建/打开、写出失败或结构校验失败时，捕获在本次会话内停用，**选牌行为不受影响**。默认关闭时 `CapturePreState` 在读取环境后立即返回，不触碰 `Player`。

## 时点与语义

`ChooseCardReward` 流程（`src/CoopBots/BotBrain.cs`）：

1. `CapturePreState(player, cards)`：在 `BestReward` **之前**构建不可变快照（JSON），分配全新 `decision` ID，返回持有该快照的 `PendingDecision`。快照只含序列化 JSON 与候选 ID 字符串，不保留任何可变游戏对象。
2. `BestReward(player, cards, allowSkip: true)`：**原样调用**，不额外调用评分器，不吞评分器异常。
3. `CaptureOutcome(pending, chosen)`：把本次 `chosen` 索引/是否 skip 以同一 `decision` ID 追加进该条记录。
4. 原有 `BuildTrace.LogReward` 调用保留。

选择 ≠ 已应用：本钩子只观察 `BestReward` 的返回。记录中 `selection.applied` 恒为 `null`、`applied_observed` 恒为 `false`。`allowSkip=true` 推出的合法替代项**未经验证**，记录为 `legal_alternatives: "unknown"`，不宣称 `solver_eligible`/`ready_for_solver`/`complete`。

## 记录 schema v1（每完成一次决策一行 JSONL）

- 顶层：`schema`（`coopbots.build_decision_capture.v1`）、`source`（`bot_card_reward`）、`captured_utc`、`selection_recorded_utc`。
- 标识：`session`/`run`/`decision` 均为不透明 ID；`run` 用 `ConditionalWeakTable<object,...>` 的每 `RunState` 对象弱身份 `Guid`（同一 run 对象复用同一 ID，不同 run 对象不因哈希碰撞而相同，ID 带会话前缀且随 `ResetForTest` 清空），`player_slot` 用玩家槽位；**不含 NetId、用户名或原始 seed**。
- 构建：`policy`（`id`、程序集名、程序集版本、**MVID**、MOD 版本）；`game`（程序集名、程序集版本、**MVID**、信息版本）。版本/MVID 取自实际程序集，不伪造。
- 位置：`character`、`ascension`、`party_size`、`act`、`floor`、`room`、`room_id`；不可用处为 `null`。
- 状态：`hp`、`max_hp`、`gold`；`potion_slot_capacity`。
- 牌组：`deck` 为**逐实例、不去重**的有序数组，每项经真实 `CardModel.ToSerializable()` 并用游戏序列化器（`MegaCritSerializerContext`/`JsonSerializationUtility`）输出，含 `id`、`current_upgrade_level`、`enchantment`、`props`、`floor_added_to_deck`。
- 候选：`candidates` 为带 `index` 的有序数组（索引即输入顺序），`card` 同上。
- 遗物/药水：`relics` 经 `SerializableRelic`（含 `props` 的 ints/bools/strings 与 `floor_added_to_deck`，真实保存计数如 `PenNib.AttacksPlayed` 随 `[SavedProperty]` 一并输出）；`potions` 为 `{slot_index, potion}`（字段名为 `potion`，不是 `card`），`potion` 经 `SerializablePotion`（仅 id 与 slot_index）。
- 未知与限制：`missing`（`rng_state`、`root_seed`、`teammate_state`、`applied_state`、`legal_alternatives` 等）、`coverage_limits`（含序列化药水只存 id/slot 的已知限制）；`root_seed`/`rng_state` 为 `null`（显式缺失，不补默认）；`teammates_captured: false`。
- 判定位：`schema_valid: true`（已由追加前校验器实际核对）、`solver_eligible: false`、`ready_for_solver: false`、`complete: false`。
- 选择：`selection`：`index`（只允许 `-1` 或 `0..候选数-1`）、`skip`（与 index 一致）、`selected_card_id`（用与候选完全相同的规范化完整 `ModelId` 字符串，不再是 `.Entry`）、`applied: null`、`applied_observed: false`、`legal_alternatives: "unknown"`。

不序列化整个 `Player`/`Run` 或任意对象图；只取上述字段。

## 追加前结构校验

`CaptureOutcome` 在 `Append` 之前调用紧凑的 `ValidateRecord`（`internal static`，测试可直接喂入坏记录）。它只核对 JSON 形状：必填 `schema`/`source`/`decision`/`session`/`run`/`captured_utc`/`selection_recorded_utc`；`policy`/`game` 的程序集名、版本、MVID 非空；`hp`/`max_hp`/`gold`/`potion_slot_capacity` 为整数且非负；`missing`/`coverage_limits`/`deck`/`relics`/`candidates`/`potions` 为数组；候选 `index` 连续且 `card.id` 非空；药水 `slot_index` 满足 `0<=slot<capacity` 且不重复；选择 `index` 只允许 `-1` 或 `0..count-1`，`skip` 一致，`selected_card_id` 与候选规范化 id 相同，`applied` 为 `null`、`applied_observed` 为 `false`、`legal_alternatives` 为 `"unknown"`、`solver_eligible`/`ready_for_solver`/`complete` 为 `false`。校验失败**不写该条**并停用本会话捕获；它**不**验证玩法合法性，也不声称求解器可评测。同一 `PendingDecision` 用 `Interlocked` 只消费一次，重复 `CaptureOutcome` 不再追加。

## 有界写入

- 每个进程/会话一个独占文件：`coopbots-decisions-<UTC>-<session>-p<pid>.jsonl`，`FileMode.CreateNew`（**不覆盖**已有文件），UTF-8，无 BOM，单条同步追加并 flush。
- 上限：每会话最多 **1000 条**、**10 MiB**；达到任一时停用本次会话的捕获，不写不完整记录、无无界队列。
- 序列化失败：整次决策捕获失败并停用，**不以空快照代替**；不影响 `BestReward` 返回值与异常。

## 实测证据

本地执行（2026-09-18）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-building-decision-capture.ps1
```

最新运行：`work/building-decision-capture/20260917T172112.357750Z/`；`verify-summary.json` 记录 restore/build/run 退出码均为 **0**，`success: true`。构建产物 SHA256：`PatchSmoke.dll df938289...`、`CoopBots.dll 4bf6b713...`、`sts2.dll 0861bfa1...`。运行日志 `logs/capture-run.log`：

```
PASS: disabled capture returns before Player and writes nothing
PASS: a blank enabled directory is rejected without touching the Player
PASS: enabled capture serializes real models, preserves copies/upgrades and links the selection
PASS: each invocation gets a distinct decision id within one session
PASS: run identity is stable per run object and distinct between run objects
PASS: the pre-state snapshot is immutable after model mutation
PASS: a pre-state is consumed once; a duplicate outcome is ignored
PASS: the structural validator passes normal records and rejects missing, out-of-range and contradictory ones
PASS: real relic saved props and a holed nonzero potion slot survive capture and stay immutable
PASS: card enchantment survives capture through the real game serializer
PASS: a real Silent player model is captured headlessly with its character and deck
PASS: the per-session record cap stops and disables capture
PASS: the per-session byte cap stops without writing a partial record
PASS: an unwritable target fails safely and does not affect the choice
PASS: enabled and disabled capture return the same choice and leave the same deck
PASS: 15 building decision capture checks.
```

覆盖：默认关闭不触碰 Player/不写盘且不改变评分路径；启用后经真实游戏序列化器往返重复卡与升级、规范化选择 id；`run` 身份按 `RunState` 对象稳定并使用随机UUID区分；坏 index/缺字段/矛盾选择与错误 `chosen` 被校验器拒绝且不落盘；`PendingDecision` 只消费一次；真实 `PenNib` 的 `[SavedProperty]` 计数与带空洞非零槽药水在快照中保留且模型变更后不变；真实卡附魔经序列化器保留；真实 `Silent` 玩家模型可在无头环境创建并记录其 `character`/`deck`；空白目录、记录数/字节上限、不可写目标安全失败；启用与关闭返回同一选择且牌组不变。构建告警仅来自既有 `BuildValueScenarios.cs`/`CoordinatorScenarios.cs`，新文件无告警。

已核对的检查组：默认关闭边界、空白目录、真实模型序列化与选择链接、decision/run 身份、快照不可变、单次消费、结构校验故意拒绝、遗物/药水道具路径、卡附魔路径、真实 Silent 冒烟、记录/字节上限、不可写目标、开关一致性，共 15 项。

## 限制（不得省略）

- 这是**合成、本地**测试，不是实盘奖励画面的采集；未在真实跑局中验证。
- 只覆盖 `BotBrain.ChooseCardReward` 这一个入口；重抽/多选每次调用各自一条记录，不假装覆盖人类或其他事件。
- 药水仅 id/slot；遗物以 `SerializableRelic` 表达的字段为准（已覆盖真实 `PenNib.AttacksPlayed` 保存计数），该结构未表达的运行时字段仍不捕获；RNG/根 seed/队友状态未捕获。
- 卡牌 `props` 的**保存属性**路径仍未构造专门用例（`deck`/`candidates` 的 `id`、`current_upgrade_level`、`enchantment` 已覆盖，卡附魔已用一个真实 `Sharp` 附魔验证；卡上 `[SavedProperty]` 计数仍属未测缺口，不作已测声明）。
- 真实 `Silent` 冒烟只跳过了 `Player.PopulateStartingRelics` 的存档依赖（与 `BuildingDecisionProbe` 相同的适配），不是完整实机跑局；测试玩家仍无起始遗物。
- 候选合法性未独立验证；记录不代表可直接送入求解器。`schema_valid` 仅表示结构校验通过，不构成可评测性结论。
- 未部署、未联网、未复制到 `outputs/`/`releases/`、未运行战斗批。

## 最终验收补充

Astra已完成最终验收：`work/building-decision-capture/20260917T172112.357750Z/verify-summary.json` 的restore/build/run均为0，15组检查通过。补测必要上下文字段缺失、显式unknown与缺键区别、来源类型及药水内外槽位矛盾。独立读取 15 条合成JSONL，选择与候选一致、decision不重复、applied保持null。验收哈希见同目录上层owner-acceptance.json。

运行标识仅在当前进程会话与RunState对象生命周期内稳定；保存后重载可能得到新标识，因此跨会话训练分组不能仅依赖该标识。真实Silent模型测试跳过初始遗物获取的原生路径，非真实奖励UI运行证据。未部署、未启用实机采集、没有新增战斗收益结论。下一关是少量真实记录采集和求解器输入覆盖核验；不得因schema_valid就提升solver_eligible。
