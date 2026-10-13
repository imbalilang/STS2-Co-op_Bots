# V27：光与暗之门事件建模（用预测数据定价随机强化）

2026-09-29。实机反馈：Bot 在「光与暗之门」选了升级、而不是删牌。查证后确认**该事件从未被建模**，
本轮补上，并第一次把「随机结果」用预测数据算清楚再决定。

## 1. 事件的两个分支（游戏自己的文本）

`.pck` 里的本地化原文（`DOORS_OF_LIGHT_AND_DARK.*`）：

| 选项 | 原文 | 含义 |
|---|---|---|
| `options.LIGHT`（光之门） | `Verbessere {Cards} zufällige Karten.` / 随机[gold]升级[/gold][blue]{Cards}[/blue]张牌 | 升级 **{Cards} 张随机**牌（不可选） |
| `options.DARK`（暗之门） | `Entferne 1 Karte aus deinem Deck.` / 从你的[gold]牌组[/gold]中选择[blue]1[/blue]张牌移除 | **自选移除 1 张** |

实机日志（种子 `9F4Q3R2BV8HQ`，四个席位）：

```
2803  ... options: title: 光之门 description: 随机[gold]升级[/gold][blue]{Cards}[/blue]张牌。 textKey: ...options.LIGHT,
            EventOption title: 暗之门 description: 从你的[gold]牌组[/gold]中选择[blue]1[/blue]张牌移除。 textKey: ...options.DARK
2819-2826  CoopBots event: bot=… chose=...options.LIGHT      ×4
2820  [EventSynchronizer] Option index 0 chosen ... LIGHT
```

选完直接进地图投票（2828），**没有逐张选牌**——因为光之门是随机升级。

## 2. 根因：没有 handler，落进按"名字"打分的通用打分器

`DOORS_OF_LIGHT_AND_DARK` 不在 `EventHandlers.Handlers` 的注册表里，于是走
`EventPatches.ChooseEventOption` 的通用打分器：它读 `option.TextKey` 的小写形式，匹配
`relic/gain/obtain/heal/max_hp/upgrade/gold/damage/lose/curse` 这些词。

两个 key 是

```
doors_of_light_and_dark.pages.initial.options.light
doors_of_light_and_dark.pages.initial.options.dark
```

**一个词都不匹配** → 两边都是基础分 **5.0** → 并列 → 末尾的
`OrderByDescending(Score).ThenBy(index)` 取 **index 0 = 光之门**。

所以那次选择不是"评估后认为升级更好"，而是**被页面上两个选项的排列顺序决定的**。
佐证：`BakedEvents` 里这张表记的顺序是 `["DARK","LIGHT"]`，实机那页却是 LIGHT 在前——
换个排列结果就反过来。

## 3. 修法：把"随机"读出来再定价

新增 `src/CoopBots/DoorsOfLightAndDarkHandler.cs`（`IEventSpecialHandler`）：

- **光之门**：用内核已有的 RandomForeseer 移植（MIT）`OutOfCombatPredictions.EventOption`
  ——它把事件自己的生成器在 **克隆的 RNG** 上重放，于是可以**在选之前**读出这局会被升级的**那两张牌**。
  逐张按 `BuildValue.UpgradeDelta` 计价，**只算它会拿的那两张**，不算牌组里最好的两张。
  （预测返回的是升级后的副本，按 `Id.Entry` 匹配回牌组原件。）
- **暗之门**：自选移除，取牌组里 `BuildValue.Remove` 最优的目标——与商店/营火/删牌界面同一把尺子。
- 两侧比大小，返回**具体选项 index**。
- **没有预测就不猜**：牌组还有可升级牌却拿不到预测时返回 `null`，退回通用打分器（诚实缺证据）。
- 日志：`LogChoice` 追加 `light=<值>[<被升级的两张>] dark=<值>(<被删的牌>) predicted=<bool>`。

用的是同一份日志里它自己的数字：随机升两张基础牌（`STRIKE` 3.6 / `DEFEND` 6.5 量级），
自选删一张基础牌则有 `StarterRemovalBaseline(30) × StarterPressure(≤2.5) = 75`。

## 4. 回归与 R2

夹具（`tests/PatchSmoke/TabletEventScenarios.cs`）：13 张牌（6 打击 / 5 防御 / Bash / Uppercut），
喂入"生成器会拿两张基础牌"的预测。

```
PASS: the light door is priced from the cards it will really upgrade (6.6) and loses to the chosen removal (75.0).
```

断言：光门价值 == 那两张牌各自的 `UpgradeDelta` 之和；夹具必须能区分"预测的两张"与"最好的两张"
（否则断言不成立）；暗门 > 光门；**两种排列下都选暗门**。

**R2 红原文**（把匹配改成"取池子里价值最高的"，即旧式"按最好的两张估"）：

```
System.InvalidOperationException: Tablet regression: The light door must be priced from the cards it will
ACTUALLY take: got 11.4, expected 6.6 for [STRIKE_IRONCLAD,DEFEND_IRONCLAD].
```

`PriceWith` 与 `Decide` 被拆成纯函数，因为**实时预测需要事件自己的 RNG，无头夹具放不出来**；
handler 里剩下的部分（类型判断、`IndexOf`、预测调用）属于接线，不含策略。

## 5. 四道门

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出 **0**。
证据 `work/verification/20260928T181630193Z/`。

| 门 | 实测 |
|---|---|
| Release 构建 | 0 error、**6 warnings（原基线，无新增）** |
| TypeLoadCheck | 退出 0 |
| 普通 PatchSmoke | **259 PASS / 0 FAIL** |
| 完整内核套件 | **420 PASS / 0 FAIL** |

覆盖扫描未变（`broken=0`）。事件 id 漂移守卫（`TabletEventScenarios` 的 registry 检查）
已确认 `DOORS_OF_LIGHT_AND_DARK` 的 `DARK`/`LIGHT` 都在本 build 的事件表里。

## 6. 尚未验证与下一步

- **本轮无实机数据。** 夹具证明的是"给定预测时如何定价"，不是"实机一定会选暗门"。
- 实机要看的：`CoopBots event: … DOORS_OF_LIGHT_AND_DARK chose=…DARK` 且同行出现
  `light=…[...] dark=…(...) predicted=True`；若出现 `predicted=False` 说明预测在实机没读到，
  那就是另一个问题（需要单独查）。
- 同类候选（同样"key 不带词、效果只能靠数据读"的事件）可以按同一模式补：先看
  `BakedEvents` 里哪些事件没有任何 handler，再看它的效果能否从 DynamicVars/预测读出。
