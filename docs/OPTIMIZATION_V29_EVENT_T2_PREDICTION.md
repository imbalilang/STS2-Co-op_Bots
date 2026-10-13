# V29：T2——用预测数据给"随机结果"定价

2026-09-29。V28 的扫描里 T2 是"下一批"，本轮做完。

## 1. 关键发现：能预测的事件比清单上多得多

内核里的 RandomForeseer 移植 `EventOptionPrediction` 注册了 **31 个事件**的预测器
（`src/CoopBots.Kernel/Vendor/OutOfCombat/OutOfCombat/EventOptionPrediction.cs`），
而 V28 时只有 11 个事件有 handler。也就是说**大量"随机结果"其实读得出来，只是没人问**。

其中三个正是 V28 刚写的 T3 handler：`ThisOrThat`（随机遗物）、`UnrestSite`（随机遗物）、
`Wellspring`（随机药水）——它们的预测器一直存在，本轮起会自动被用上
（`EventTrade.GrantedRelic/GrantedPotion` 先问预测，拿不到才用估值常数）。

## 2. T2 核心：一个地方回答"这个选项到底给什么"

新增 `EventTrade.Grants(player, model, option)`，返回**卡组价值点**：

```
= 选项 hover tip 上的具名模型（卡/遗物/药水）
+ RandomForeseer 预测的模型（克隆 RNG 重放生成器，不消耗真实掷骰）
   ↓ 按 Id 去重，具名与预测都给出同一个模型时只算一次
每个卡按 BuildValue.Add 计价，诅咒按 −14×bloat（有界，不用卡牌路径的 −1000 拒绝），
遗物按 HumanCoopAdvisor.RelicValue，药水按 BotPotionInventory.ReserveValue，
卡牌"包"（Choice bundle）按其中最好的一张。
```

**通用打分器和所有 handler 都改用它**，所以"授予具名模型"（T1）与"随机结果的预测"（T2）
只有一份实现。通用打分器现在对任何带预测的选项都能给分，不需要为每个事件写 handler。

## 3. 两个需要 handler 的（代价不在"给什么"里）

| Handler | 两侧 | 为什么通用打分器不够 |
|---|---|---|
| `BATTLEWORN_DUMMY` | 三个木桩（75/150/300 HP）+ 各自的随机奖励（药水 / 强化两张 / 遗物） | 奖励能预测，但**战斗长度**要单独计价 |
| `BRAIN_LEECH` | RIP=掉 5 血换无色卡奖励 vs SHARE=白拿 5 张里 1 张 | 奖励能预测，但 RIP 的**掉血**要单独计价 |

木桩的战斗代价用**一条具名估计**：每 30 点木桩 HP 记 1 点（训练木桩不是威胁，长战斗的代价是时间与零星掉血）。
这条估计写在常数注释里，并每次都进决策日志。

## 4. 回归与 R2

夹具（`TabletEventScenarios`）：

```
PROBE T2 grants: study=30.0(MOCK_POWER_CARD*) touch=0.0()
PASS: T2 predicted grants (study=30.0, touch=0.0); Brain Leech picked …SHARE_KNOWLEDGE.
PASS: the dummy event prices its three fight lengths when no reward can be predicted.
```

- **预测真的跑起来了**：`Owner` 用反射补上（`EventModel.Owner` 是只读属性，实机由游戏在开场时赋值），
  `STUDY` 的随机能力牌被解析成 `MOCK_POWER_CARD` 并计价 30.0。夹具的卡池是 mock，
  所以 `TOUCH_CORE` 的"0 费牌"过滤在该池里为空——**这是夹具的边界，不是实机行为**，
  注释里写明了。
- **木桩夹具刻意把 `SETTING_3`（最长战斗，300 HP）排在第一页**：按页面顺序会选它，
  按战斗代价必须选 `SETTING_1`。这就把"在给战斗长度定价"和"在按顺序取第一个"分开了。

**R2 红原文**：

| 改坏 | 变红原文 |
|---|---|
| `Grants` 不读预测 | `Tablet regression: The RandomForeseer prediction must be priced: study=0.0() touch=0.0()` |
| 木桩不减战斗代价 | `…the dummy handler must price the fight it is committing to, not the page order; got …SETTING_3 (SETTING_1=0.0 SETTING_2=0.0 SETTING_3=0.0).` |

> 第二条第一次**没有变红**：handler 当时按一个固定的 `Settings` 数组遍历，平局时落在数组第一项，
> 于是"拿走顺序"也恰好选中 SETTING_1。已改成**遍历真实页面顺序**（`CurrentOptions`），
> 平局才是真的平局——这才是那个断言想测的东西。

## 5. 四道门

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出 **0**。
证据 `work/verification/20260929T134116218Z/`。

| 门 | 实测 |
|---|---|
| Release 构建 | 0 error、**6 warnings（原基线，无新增）** |
| TypeLoadCheck | 退出 0 |
| 普通 PatchSmoke | **264 PASS / 0 FAIL** |
| 完整内核套件 | **425 PASS / 0 FAIL** |

handler 注册表 **19 个**，全部过 id 漂移守卫。

## 6. 本轮实际覆盖面（避免夸大）

- **完全够用**：两侧都是"授予"的事件——`INFESTED_AUTOMATON`、`COLORFUL_PHILOSOPHERS`、
  `TINKER_TIME`、`WHISPERING_HOLLOW`、`RANWID_THE_ELDER`、`THE_LEGENDS_WERE_TRUE` 等，
  通用打分器现在按预测出的实际模型比较。
- **只有一半**：一侧是授予、另一侧是**没有关键词的代价**（掉血/掉最大生命/耗时）的事件，
  例如 `DOLL_ROOM`（`EXAMINE`/`TAKE_SOME_TIME` 掉血）、`LUMINOUS_CHOIR`（付金币）、
  `ROUND_TEA_PARTY`（`PICK_FIGHT` 掉血）、`TRASH_HEAP`（`DIVE_IN` 掉血）。
  授予侧已经有分，代价侧仍只靠选项名里的关键词——**这些是下一步 T6 的活**。
- 仍未做：T4 剩余（`LOST_WISP`/`SPIRIT_GRAFTER`/`DROWNING_BEACON`/`TRIAL`）、
  T5 全部（附魔/转化，真正的决策在下一页选牌）、T6（战斗代价）。
- **没有任何实机数据**。
