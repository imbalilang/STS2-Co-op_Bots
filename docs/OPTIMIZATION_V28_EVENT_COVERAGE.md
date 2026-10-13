# V28：事件覆盖率——补 BakedEvents，做 T1（具名模型）与 T3（资源交换）

2026-09-29。V27 修完光与暗之门后做了一次全事件扫描，本轮处理扫描暴露的两类问题：
**表本身不全**，以及**"授予具名模型"和"纯数值交换"这两类事件根本没被定价**。

## 1. 扫描结论（口径与复核方式）

```
已建模 = EventHandlers.Handlers 注册表
零信号 = 该事件所有选项按通用打分器规则同分 → 并列 → 按页面顺序取第一个
打分规则（改动前）= relic/gain/obtain +8 · heal/max_hp/upgrade +6 · gold +4 · damage/lose −9 · curse −9×bloat，base 5.0
```

扫描时（改动前）：**56 个事件、11 个 handler、41 个零信号、4 个只靠选项名里的词**。
零信号里出现频率最高的：`SELF_HELP_BOOK`(8) `WELCOME_TO_WONGOS`(8) `THIS_OR_THAT`(8)
`STONE_OF_ALL_TIME`(8) `SUNKEN_STATUE`(4) `SUNKEN_TREASURY`(4) `COLORFUL_PHILOSOPHERS`(4)。

## 2. BakedEvents：补三处、命名三处

用**本机安装版自己的本地化键**（`SlayTheSpire2.pck`）与表逐项对比，发现表缺 3 个选项 id：

| 事件 | 补入的 id |
|---|---|
| `COLORFUL_PHILOSOPHERS` | `EQUALITY` |
| `LOST_WISP` | `CLAIM_LOCKED` |
| `SPIRALING_WHIRLPOOL` | `REACH_IN` |

**这不是装饰**：漂移守卫（`TabletEventScenarios`）会拒绝任何"handler 命名了表里没有的 id"，
所以缺一个 id 就等于那个事件**永远无法被建模**。已在文件头写明来源与复核方法。

另有一类是**表根本列不出来**的：`NEOW` / `OROBAS` / `PAEL` 的
`pages.INITIAL.options.X` 里 **X 是运行期从池子里抽出来的遗物 id**（例如
`LOST_COFFER`/`SILKEN_TRESS`/`SMALL_CAPSULE` 在 `.pck` 里是**遗物**的键，不是事件的键）。
它们没有静态 id 可查，因此**具名登记**在 `BakedEvents.PoolBasedEvents`，并断言"池基事件不得挂 handler"
——把"没被覆盖"这件事说出来，而不是留一个沉默的空洞（R5c）。

顺带更正 V27 扫描里的一个说法：`NEOW` 并不是"零信号"，它的选项自带遗物模型，
通用打分器的 `GrantedRelics` 分支本来就在区分它们（实机四个席位选了不同选项即是佐证）。

## 3. T1：授予具名模型的事件

通用打分器原来对 hover tip 里的卡牌只按类型给分（诅咒/状态 −14×bloat、Power +6、**其余一律 0**）。
于是 `BUGSLAYER` 的两张具名卡、`BYRDONIS_NEST` 的 `TAKE`、`INFESTED_AUTOMATON` 等
**互相之间、以及与空选项之间都无法区分**。

改为走奖励选牌的同一套模型：

```csharp
if (granted.Type is CardType.Curse or CardType.Status) score += -14 * RunDepth.BloatFactor(player);
else score += CardValueSafe(granted, player) * ScorePerDeckPoint;   // 0.25
```

- 诅咒/状态**保持有界常数**：卡牌路径自己的守卫会直接把诅咒判 `-1000`（对"奖励"是对的），
  但对"用诅咒换遗物"的**交易**是错的——所以这里不调它。
- `ScorePerDeckPoint = 0.25` 沿用遗物分支一直以来的换算，使等值的卡与遗物得分相同。

一次改动覆盖 `BUGSLAYER` `BYRDONIS_NEST` `INFESTED_AUTOMATON` `COLORFUL_PHILOSOPHERS`
`DOLL_ROOM` `TRASH_HEAP` `LUMINOUS_CHOIR` 等所有"给具名卡/遗物"的事件。

## 4. T3：纯数值交换的事件（6 个 handler）

新增 `EventTradeHandlers.cs`，含**一套统一单位** `EventTrade`：

```
单位 = 卡组价值点（BuildValue 的分；商店按 BotShopPlanner.GoldPerDeckValue=3 金/点换算）
金币   = 金额 / 3
HP     = 1:1（CheeseEventPolicy 早已把伤口 HP 直接与遗物分比较；战斗终局比较器也用 HP 当效用单位）
治疗   = min(治疗量, 缺的血)          // 溢出不计
MaxHp  = 与当前 HP 同单位
诅咒   = −14 × BloatFactor
移除   = BuildValue.Remove 的最优目标
```

| Handler | 两侧 | 数据来源 |
|---|---|---|
| `SUNKEN_STATUE` | 潜水=金币−受伤 vs 拿剑=遗物 | `Gold`/`HpLoss` + 选项遗物 |
| `SUNKEN_TREASURY` | 小箱=金币 vs 大箱=更多金币+**贪婪诅咒** | `SmallChestGold`/`LargeChestGold` |
| `JUNGLE_MAZE_ADVENTURE` | 分金 vs 多拿金−受伤 | `JoinForcesGold`/`SoloGold`/`SoloHp` |
| `THIS_OR_THAT` | 诅咒+随机遗物 vs 受伤+金币 | `Gold`/`HpLoss` + 遗物（预测或估值） |
| `UNREST_SITE` | 永久 MaxHp−遗物 vs 回满+诅咒 | `MaxHpLoss` + 当前血量 |
| `WELLSPRING` | 删一张+内疚 vs 随机药水 | `BatheCurses` + 删牌/药水估值 |

**数值全部取自事件模型自己的 `DynamicVars`**（先用探针跑出真实字段名，不是猜的）；
随机遗物/药水先问 `OutOfCombatPredictions`（克隆 RNG），拿不到才用**具名估值常数**
（`UnnamedRelic=25` / `UnnamedPotion=12`），并且**每次使用都会写进决策日志**。

决策日志统一走 `EventHandlers.Explain(player, model)`，新 handler 只需实现 `IEventTradeExplainer`，
不必再改日志调用点。实机将看到形如：

```
CoopBots event: bot=… event=SUNKEN_TREASURY chose=…FIRST_CHEST small=20.0 large=19.0
```

## 5. R2（每条都先证明会红）

| 改坏 | 变红原文 |
|---|---|
| T1 卡牌分支改回 0 分 | `Tablet regression: A named card must be priced by the reward model: got EVENT.pages.INITIAL.options.WEAK (-6.4 vs 22.7).` |
| `SUNKEN_TREASURY` 忽略贪婪诅咒 | `Tablet regression: Sunken Treasury must refuse a large chest that only just covers its curse.` |
| 从表里删掉 `EQUALITY` | `Tablet regression: The option ids verified against the installed build must stay in the table.` |

新增断言：T1 两侧（差卡在前、好卡在后——旧代码因并列会选中差卡）；六个 T3 handler 各自
**跨过自己的盈亏平衡点各测一次**；池基事件具名且不得挂 handler；三个补入 id 被钉住。

## 6. 四道门

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出 **0**。
证据 `work/verification/20260929T125249858Z/`。

| 门 | 实测 |
|---|---|
| Release 构建 | 0 error、**6 warnings（原基线，无新增）** |
| TypeLoadCheck | 退出 0 |
| 普通 PatchSmoke | **262 PASS / 0 FAIL** |
| 完整内核套件 | **423 PASS / 0 FAIL** |

handler 注册表 **17 个**，全部通过 id 漂移守卫。

## 7. 仍未做

- **T2 随机结果预测**：`BATTLEWORN_DUMMY`、`THE_FUTURE_OF_POTIONS`、`INFESTED_AUTOMATON`、
  `COLORFUL_PHILOSOPHERS` —— 按 V27 的模板（`OutOfCombatPredictions`）做。
- **T4 诅咒换收益**：`LOST_WISP`、`SPIRIT_GRAFTER`、`DROWNING_BEACON`、`TRIAL`。
  （`SUNKEN_TREASURY`/`THIS_OR_THAT` 已在本轮覆盖。）
- **T5 附魔/转化**：`SELF_HELP_BOOK`(8)、`WOOD_CARVINGS`、`ZEN_WEAVER`、`SYMBIOTE`、
  `FIELD_OF_MAN_SIZED_HOLES`、`SAPPHIRE_SEED`、`STONE_OF_ALL_TIME`、`SPIRALING_WHIRLPOOL`。
  这些的**真正决策在下一页的选牌**，要和选牌侧一起改（单开一批）。
- **T6 战斗代价**：`DENSE_VEGETATION`、`THE_LANTERN_KEY`、`ROUND_TEA_PARTY`、`TRIAL`(DOUBLE_DOWN)。
- **没有任何实机数据**：以上都是夹具，未在实机验证过任一新 handler 的取舍。

原始扫描产物：`work/v27-doors-event/unhandled-events.txt`（45 个事件各选项的游戏原文）、
`work/v27-doors-event/build-events.json`（从 `.pck` 提取的 build 事件/选项 id 全集）。
