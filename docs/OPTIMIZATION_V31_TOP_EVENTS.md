# V31：按出场频率补三个事件（自修手册 / 王戈斯 / 时之石）

2026-09-29。V30 之后按**实机出场次数**（而不是按 T 编号）挑出仍未建模的三个：
`SELF_HELP_BOOK`(8)、`WELCOME_TO_WONGOS`(8)、`STONE_OF_ALL_TIME`(8)。

## 1. 关键数据（都是先探测再写代码）

### 自修手册 `SELF_HELP_BOOK`

三页各附魔一种**牌型**，附魔 id 就写在事件的 DynamicVar 里——只是它是个 **StringVar**：

```
Enchantment1 -> "SHARP.title"   (Attack)
Enchantment2 -> "NIMBLE.title"  (Skill)
Enchantment3 -> "SWIFT.title"   (Power)
Enchantment1Amount/2/3 = 2
```

再从游戏自己的本地化文本读出这四种附魔的**真实效果**：

| 附魔 | 原文 | 含义 |
|---|---|---|
| `SHARP` | Increases damage on this card by {Amount} | 每次打出 +N 伤害 |
| `NIMBLE` | Increases Block gained from this card by {Amount} | 每次打出 +N 格挡 |
| `SWIFT` | 首次打出时抽 {Amount} 张 | **仅首打** |
| `VIGOROUS` | 首次打出时 +{Amount} 伤害 | **仅首打** |

于是可以真的定价：用 `MarginalCore` **自己的权重**（伤害 1.2 / 格挡 1.0 / 抽牌 4.0），
每次打出的加成乘一个**有界的三次出牌**horizon，仅首打的（SWIFT/VIGOROUS）不乘。
**牌组里没有该牌型的一页记 0**——附魔无处可落。

### 时之石 `STONE_OF_ALL_TIME`

```
DrinkMaxHpGain=10   PushHpLoss=6   PushVigorousAmount=8
LIFT  = −随机药水储备值 + 永久最大生命 10
PUSH  = VIGOROUS 8 的定价 − 6 点伤害
```

### 王戈斯 `WELCOME_TO_WONGOS`

它是个**商店**，价格就在事件变量里：`BargainBinCost=100 / FeaturedItemCost=200 / MysteryBoxCost=300`。
`BARGAIN_BIN` 的遗物由预测器给出（T2 的 `Grants` 直接吃下）。

**而 `LEAVE` 不是免费的**：预测器显示离开会把一张**已强化**的牌降级。
所以"走人"这一侧也有价——按牌组里**还能取得的升级平均值**计价（已强化的牌再取 `UpgradeDelta` 是 0，
所以用剩余可升级价值当有界代理）。

**买不到情报的货架不盲买**：`FEATURED_ITEM` / `MYSTERY_BOX` 没有预测器 → 计价为 0 → 不会买。

## 2. R2 红原文

| 改坏 | 变红 |
|---|---|
| 手册忽略牌型 | `An attack-only deck must take the Attack page: READ_THE_BACK=7.2 READ_PASSAGE=6.0 READ_ENTIRE_BOOK=8.0.` |
| 时之石不给 Vigorous 定价 | `A cheap Vigorous attack must beat ten Max HP for a potion: lift=-2.0 push=-6.0.` |
| 王戈斯不收购价 | `An unpriced shelf must not be bought blind: bin=0.0 featured=-66.7 box=-100.0 leave=-3.5.` |

夹具刻意把**错误的那一页排在第一位**（手册把 Power 页排最前、木桩把最长战斗排最前），
所以"按页面顺序取第一个"会稳定地选错。

> 过程记录：这轮改动里我用一次批量正则给新类补 `EventTrade.` 前缀，误伤了字符串字面量 `"Gold"`。
> **是既有断言先红的**（`Sunken Statue must dive…: dive=-7.0 sword=25.0`），不是靠人眼发现的。
> 已修，并把该断言的失败信息补上两侧取值。

## 3. 四道门

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出 **0**。
证据 `work/verification/20260929T140545870Z/`。

| 门 | 实测 |
|---|---|
| Release 构建 | 0 error、**6 warnings（原基线，无新增）** |
| TypeLoadCheck | 退出 0 |
| 普通 PatchSmoke | **267 PASS / 0 FAIL** |
| 完整内核套件 | **428 PASS / 0 FAIL** |

handler 注册表 **25 个**，全部过 id 漂移守卫。

## 4. 明确的估计值（会被写进决策日志）

| 名字 | 值 | 为什么是这个量级 |
|---|---|---|
| 附魔 horizon | 3 次出牌 | 与周期模型用的有界视野一致；仅首打的附魔不乘 |
| `UnnamedPotion` | 12 | 随机药水的储备估值；有预测时以预测为准 |
| `AverageUpgradeValue` | 牌组剩余升级的均值 | 已强化牌再取 delta 为 0，只能用它当有界代理 |

## 5. 仍未做

- **T6 代价侧**：`DOLL_ROOM`、`TRASH_HEAP`、`ROUND_TEA_PARTY`、`LUMINOUS_CHOIR`
  （"授予"侧 T2 已有分，掉血/付金那一侧仍靠关键词）。
- **`TRIAL`**：见 V30 §2，等实机样本（选项 id 跨页重复 + 两份来源对 INNOCENT 的描述矛盾）。
- **T5 其余**：`WOOD_CARVINGS`、`ZEN_WEAVER`、`SYMBIOTE`、`FIELD_OF_MAN_SIZED_HOLES`、
  `SAPPHIRE_SEED`、`SPIRALING_WHIRLPOOL` —— 真正的决策在下一页选牌。
- **没有任何实机数据**：以上全是夹具结论。
