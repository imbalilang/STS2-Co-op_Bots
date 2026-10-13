# V26：构筑选牌估值修复（覆盖率 · 成型度 · 撤销机会）

2026-09-29。起因是实测里一个具体现象：第一幕 Boss 战后机器人没领稀有卡奖励。
本轮把根因拆成两组——**"估值看不见这张牌"**与**"稀释税与牌无关"**——分别修掉，并重写了
一条被测试守住的旧政策。改动只在 `src/CoopBots/**`（构筑侧），未动内核。

## 1. 现场与根因

种子 `9F4Q3R2BV8HQ`，A10，第一幕 Boss `WATERFALL_GIANT_BOSS`(floor 16)。四个席位各有一份
Gold(+Potion)+CardReward，其中两张静默席位（含托管席）三选一全部评负 → `pick=SKIP`。
用项目自己的社区 Elo 尺子（skip = 1573.0）对照：

| 牌 | 社区 Elo | 相对 skip | 结果 |
|---|---:|---:|---|
| BLADE_OF_INK | 1738.2 | +16.5 | 跳过 |
| BURST | 1702.1 | +12.9 | 跳过 |
| FLANKING | 1650.6 | +7.8 | 跳过 |
| IMPERVIOUS | 1646.9 | +7.4 | **拿了** |
| BRAND | 1648.9 | +7.6 | **拿了** |
| WELL_LAID_PLANS | 1559.0 | −1.4 | 跳过（社区同意） |

**被跳掉的比拿掉的评分更高**——所以这不是"金卡强不强"的问题，是"Elo 先行的声明没有兑现"。

四个具名缺陷：

| # | 缺陷 | 证据 |
|---|---|---|
| D1 | 六回合模型对**读不懂文本的牌**照跑照报，`model-share:1.00` | BURST 在 `DraftCardEffects.Of` 无分支，内核却有 `BurstPower` 镜面 / `CardOnPlayCompensationCatalog` |
| D2 | 社区 Elo 被压成 ±2 零头 | BURST +12.9 与 cycle −11.94 相加后剩 −11.6 |
| D3 | 「稀释税」与牌无关，纯按张数收 | `pressure = Oversize(N)×bloat`：19 张 −27.0、21 张 −40.5 |
| D4 | 「成型」只有一条算术：张数 ≥ 15 | 唯一判据 `FormingSize=15`，无引擎/删卡/路线检查 |
| D5 | `route=none` 的牌组被按"已完成"收最高税 | `build[act2]` 快照：21 张、9 基础牌 + 诅咒、`route=none` |
| D6 | 「不可撤销」乘数在幕末误触发 | `ShopAhead` 只走当前幕；Act 1 Boss 处乘 1.35，但第二三幕还有店 |
| D7 | `six-turn:` 打的是**每回合**值 | `SustainedDeckValue.Score` 返回 `total/6`，×6 在 `DeckPoints` 里 |

## 2. 改动

### 2.1 覆盖率：`BuildCoverage`（新文件）

```
coverage(card) = 0  当这张牌的文本不在估值能读的效果族里
               = 1  其余
```

- 判据是 `CardProfile.Facts` 的数值项 / `DraftCardEffects` / `KnownPower` / `Cleanup`。
  BURST、BLADE_OF_INK、FLANKING 这类"技能但效果不在模型里"的牌落为 0。
- **不动** `SustainedDeckValue.Unsupported` 那一类（已知之外的 Power）：它们本来就让 cycle 返回
  null、走 `estimate:structural`，是诚实的"缺证据"，再压一层会连它们**合法携带的路线契合信号**一起抹掉
  （实测 Inferno 的 signature 测试就是靠这个信号）。

### 2.2 缺证据不得变成否决

```csharp
// AddDetailed
var total = eloBase + coverage * rules + development + affinity + multiplayer;
// ContextualAddition
if (coverage <= 0) return ... "estimate:no-coverage" ...;   // 不跑 cycle
var share = Math.Min(experiment.ModelShare, coverage);
```

只让**从牌面读出来的那部分**（结构 `rules`）受覆盖率压制；社区共选 `affinity`、四人修正
`multiplayer`、小额早牌组 `development` 是对"牌"的先验，不是对"这张牌文本"的断言，一律照旧。

### 2.3 成型度取代张数（重写旧政策）

```csharp
basicShare = 未升级基础打击/防御张数 / N
maturity   = clamp(1 - basicShare, 0, 1)          // 0 = 起手堆，1 = 没有基础牌
gate       = max( (maturity - 0.6) / 0.4 , (N - 32) / 8 )   // 末项是防泛洪兜底
pressure   = Oversize(N) × bloat × gate
```

- 托管席 N=21、基础牌 9 → maturity 0.571 < 0.6 → **tax = 0**；席位 731 N=19、基础牌 8 → 0.579 → tax = 0。
- 成型牌组（无基础牌）→ gate = 1，旧的 `size:30.0` / `size:22.5` / `size:6.0` 数值**逐字不变**，
  所以"大路货该被跳过"的性质在成型后仍然成立——这就是被批准的改写：
  **「成型度达到阈值后，与当前路线无关的大路货需要 Skip」**。
- `N > 32` 时无条件恢复收税：防止"未成型不收税"退化成"什么都拿"。

### 2.4 撤销机会按整局算

```csharp
RemovalLeftInRun(shopAhead, actIndex, actCount) = shopAhead || actIndex < actCount - 1
BloatFactor(player) = BloatFactor(Act(player), RemovalLeftInRun(player))
```

Act 1 Boss 处后面还有两幕 → `removalLeft = true` → 乘数 1.0（原先错给 1.35）。

### 2.5 日志诚实

- `six-turn:` → `per-turn:`（×6 发生在 `DeckPoints`，字段名原先误导 6 倍）。
- 理由行前置 `coverage:` 与 `maturity:`，让"模型判它差"与"模型读不懂它"在日志里可分辨。
- 托管席补 `reward not taken`；`took CardReward` 改为 `answered CardReward`（点击只打开嵌套三选一，
  实际可能答 skip——实测那条日志读起来正好与事实相反）。

### 2.6 明确**不做**的事

- **不下调 `RulesPivot`。** 曾试过让它随成型度坍缩到 0，结果让一个 below-skip 的 `Breakthrough`
  靠"补了 AoE 缺口"越过 skip 线，正好违反 Elo 先行政策所守的东西。已回退。
  本轮修复不需要它：读不懂的牌走覆盖率，根本不进 `rules`。

## 3. R2 变红原文（每条断言都先证明会红）

1. 覆盖率加权（还原成 `eloBase + rules + ...`）：
   `System.Exception: An unreadable card must score its community prior with no rules term, got 13.66 (coverage:0.00,...,rules:0.50,...)`
2. 四席不得对读不懂的牌给出 cycle 判决（关掉 `coverage <= 0` 早退）：
   `System.Exception: Four seats must not get a cycle verdict on a card the estimate cannot read: hand-delta:-1.54,...,estimate:cycle:modeled,model-share:0.00,per-turn:-1.54`
3. 成型度闸门（去掉 `× MaturityGate(...)`）：
   `System.Exception: An unformed starter deck must not pay the size tax: coverage:1.00,maturity:0.00,...,size:25.0`
4. 整局撤销机会（把纯函数改成只返回 `shopAhead`）：
   `System.Exception: A later act is still to come, so a removal opportunity remains in the run.`

**被改写的旧断言**（R8，已获用户批准）：`BuildValueScenarios.cs` 原「20 张起手牌组里的大路货
必须低于 skip」改为「**成型**牌组里的大路货必须低于 skip；未成型牌组不收张数税」。
连带把两处依赖"全基础牌牌组也要收税"的夹具改成非基础牌牌组，数值逐字保持不变
（`size:30.0` / `size:22.5` / `size:6.0`）。

## 4. 四道门

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出 **0**。
证据 `work/verification/20260928T175927795Z/`（代码定稿后的最后一次；前一次 `20260928T175744308Z` 读数相同）。

| 门 | 实测 |
|---|---|
| Release 构建 | 0 error、**6 warnings（原基线，无新增）** |
| TypeLoadCheck | 退出 0 |
| 普通 PatchSmoke | **258 PASS / 0 FAIL** |
| 完整内核套件 | **419 PASS / 0 FAIL** |

覆盖扫描未变：`total=598 modeled=553 unmodeled=7 choice=10 broken=0 skipped=28`；`COVERAGE BROKEN:` 为空。

## 5. 尚未验证与下一步

- **本轮无实机数据。** 上面的夹具复现的是**决策**，不是胜率；不据此声称 A10 强度提升。
- 待验：同种子重跑，在 `CoopBots build: reward` 行确认
  ① `coverage:0.00` 的牌不再报 `model-share`；② 未成型牌组不再出现 `size:`；
  ③ BURST/BLADE_OF_INK 这类被取、而 below-skip 的仍能跳。
- 未做的政策旋钮（`docs` 里留档，等用户拍）：`BossRareFloor`（Boss 稀有是否设默认取下限）。
  `θ` 豁免阈值未采用——覆盖率已经覆盖了它要解决的场景。
- 本轮未动内核实战路径、B10/B11 与 `Kernel*`。
