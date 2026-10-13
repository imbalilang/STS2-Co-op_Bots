# V30：T4——用诅咒或永久代价换收益

2026-09-29。V28 计划里的 T4：**付出诅咒 / 永久代价换好处**的那类事件。

## 1. 做了什么

`EventTrade` 加三件工具：

| 工具 | 用途 |
|---|---|
| `BestUpgrades(player, count)` | 牌组里最好的 N 次升级之和（营火/光之门同一套 `BuildValue.UpgradeDelta`） |
| `PricesCurse(model, option)` | 这个选项是否**已经**把诅咒计价了（hover tip 或预测里有 Curse/Status 牌） |
| `GrantsWithCurses(player, model, suffix, curses)` | 先按 `Grants` 计价，**只在选项没自己计过诅咒时**再补收 |

`PricesCurse` 是这轮的关键：诅咒的**张数**只有游戏文本知道（本地化，不能解析），
所以 handler 显式传进来；但同一个诅咒**不能被收两次**——多收一次会否掉一副本该拿的遗物，
少收一次就等于白拿诅咒。

三个 handler：

| Handler | 两侧 |
|---|---|
| `LOST_WISP` | CLAIM=遗物 + **1 个诅咒** vs SEARCH=金币 |
| `SPIRIT_GRAFTER` | LET_IT_IN=治疗（按缺的血封顶）+ 蜕变牌 vs REJECTION=掉 10 血 + 最优升级一次 |
| `DROWNING_BEACON` | BOTTLE=药水 vs CLIMB=遗物 **− 永久最大生命 13** |

顺带**删掉了通用打分器里那段内联的 `DrowningBeacon` 特判**（它只减最大生命、从不给遗物计价），
现在两侧由 handler 完整比较。

## 2. 明确**没做**：`TRIAL`

不是漏了，是**不能猜**，两条理由：

1. **选项 id 跨页重复。** `TRIAL` 的 `GUILTY`/`INNOCENT` 同时出现在 `pages.MERCHANT` 与
   `pages.NONDESCRIPT` 上，而两边效果不同（预测器自己就按页分开注册）。按后缀匹配会张冠李戴。
2. **两份来源互相矛盾。** 游戏本地化文本说 `INNOCENT` =「拿 Scham 诅咒 + 强化 2 张牌」，
   而 `TrialPrediction` 对 `NONDESCRIPT.INNOCENT` 预测的是 **Transform 结果包**。
   在没有实机样本裁决之前，按任一份写 handler 都可能把玩家送进错误分支。

按 R1/R4，这种情况下留给通用打分器（至少它不会崩），并在这里登记，而不是编一个看起来完整的实现。

## 3. 回归与 R2

夹具（`TabletEventScenarios`）测的是**三条精确性质**，不是"看起来对"：

```
PASS: T4 charges the curse once (-14.0 vs -14.0), caps the graft's heal at the missing pool (+25),
      and prices the climb's Max HP (-13.0).
```

| 改坏 | 变红原文 |
|---|---|
| 忽略 `PricesCurse`（诅咒收两次） | `A named curse must not be charged a second time: -28.0 vs -14.0.` |
| 治疗不按缺的血封顶 | `The graft's heal must be worth the missing pool, not its face value: 25.0 -> 25.0.` |
| CLIMB 不减最大生命 | `Climbing must be charged its Max HP cost: bottle=0.0 climb=0.0.` |

另有行为断言：`LOST_WISP` 在只给 60 金、没有遗物可拿时必须走 SEARCH（说明诅咒真的被收了）。

## 4. 四道门

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出 **0**。
证据 `work/verification/20260929T135110386Z/`。

| 门 | 实测 |
|---|---|
| Release 构建 | 0 error、**6 warnings（原基线，无新增）** |
| TypeLoadCheck | 退出 0 |
| 普通 PatchSmoke | **265 PASS / 0 FAIL** |
| 完整内核套件 | **426 PASS / 0 FAIL** |

handler 注册表 **22 个**，全部过 id 漂移守卫。

## 5. 优先级提醒与剩余

**T4 的四个事件在已归档的实机日志里一次都没出现过**（V27 扫描的出场次数表：
`NEOW` 16、`OROBAS` 12、`SELF_HELP_BOOK` 8、`WELCOME_TO_WONGOS` 8、`STONE_OF_ALL_TIME` 8 …）。
所以这一轮是"补覆盖"，不是"补出血点"。按同一张表，**仍未被建模、且实际出现过**的优先级是：

| 事件 | 出场 | 属于 |
|---|---:|---|
| `SELF_HELP_BOOK` | 8 | T5（附魔，真正决策在下一页选牌） |
| `WELCOME_TO_WONGOS` | 8 | T2/T6（商品与价格） |
| `STONE_OF_ALL_TIME` | 8 | T3/T5（药水换最大生命 / 附魔） |
| `DOLL_ROOM` / `TRASH_HEAP` / `ROUND_TEA_PARTY` / `LUMINOUS_CHOIR` | — | T6（一侧是没关键词的代价，授予侧已有分） |
| `TRIAL` | 0 | 见 §2，等实机样本 |

**没有任何实机数据**：以上全是夹具结论。
