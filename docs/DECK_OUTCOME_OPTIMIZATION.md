# 卡组收益、费用与商店校准（2026-09-30）

> **2026-10-01 最新实现见 §10**：A10 鲁棒性与多人共享易伤已接入统一评价；四人起手保留痛击，100 金商店选择 Bully，其次删防御。社区总体一致率 39.11%→36.38%，尚未通过整体效果验证。下列 §1–9 保留此前各阶段的数据与方案，不代表当前参数。

## 初版做了什么（2026-09-30）

按用户确认的顺序完成四部分：统一商店机会成本、修正战斗收益尺度、加入后续成长、回归社区及删牌行为。加牌、删牌、升级仍然全部使用 `S(修改后) − S(修改前)`。

3 能量、5 打击 + 4 防御 + 痛击、100 金的实际商店夹具，现为 **Bully > 删基础牌 > Bludgeon**。社区回归却没有证明整体改善：同一批 514 个记录上，一致率从 **39.49% 降到 39.11%**。本轮实现解决了定价和机制锚点，不代表胜率提高。

## 1. 统一商店机会成本

移除“买完剩不到 25 金，就额外要求价值达到价格 1.6 倍”的条件；首笔购买和第二笔购买同时修改。旧条件会拒绝花完 100 金的删牌，却放行恰好剩 25 金的 75 金卡。

加牌和删牌共用：

```text
金币估值 = 3.9 × 剩余收益周期 H × ΔS
净值 = 金币估值 − 价格
```

保留实际可支付金额、药水空位、现有的楼层阈值与两次购买重算。金币换算由旧值 3 调为 3.9，是本轮尺度校准；没有新增删牌折扣、删牌加分或删牌次数目标。事件层原本也引用此换算，所以相同金币相对于生命/遗物的价值随之变化。

## 2. 战斗收益

生产 `Strength` 使用有限生命目标。循环估计逐个抽牌轨迹计算可支付动作、战损、敌方死亡，再汇总均值与较差四分位。不能先平均伤害再判断死亡：那会在生命阈值附近产生错误的确定性击杀。

- 整张牌先支付固定费用，打出后的返费才可继续支付后面的牌；不再让 2 能量打出 2/3 张 3 费牌。
- 伤害在敌人剩余生命处封顶；致死后不承受敌人的攻击，也不继续累计后续回合伤害。
- 两回合抽手收益按它自己的时间跨度计分，修正之前把它当成每回合收益、再乘六的放大。
- 出牌选择也读取力量、飞刀、余像、不惧疼痛、黑暗之拥及消耗对象的收益；避免结算层知道机制、选择层却不愿打牌。
- 临时力量和临时敏捷在选择与结算两层使用相同的当回合重叠估计，下回合清零。完整回归发现新选择器漏算 Fade，补齐机制后原有升级断言通过，未放宽断言。
- 新选择结果替换旧曝光权重时，继续携带 Grand Finale 的空抽牌堆条件；没有窗口时该牌仍占抽牌位置、不能打出。保留既有商店与营地断言，修复了覆盖旧权重后条件失效的问题。
- 支付、抽牌和消耗使用同一组选择结果。高费能力牌启动也需要整笔支付。

这仍是有界构筑估计：16 条固定抽牌轨迹、最多 6 回合、代表性压力/生命/敌人数。消耗目标、易伤时序、能力牌曝光、多人集火仍有近似；没有把它当作原生战斗回放或胜率模型。

计算成本也有增加。本轮普通门禁中，三个 25 张牌 Bot 各做一次奖励选择合计 3452.7 ms，16 件货架的双购买比较约 1008.4 ms。这是测试进程的计时，不是实机帧率测量；单次决策缓存减少重复卡组评分，但不能据此声称没有界面卡顿。

## 3. 后续成长也使用同一个 S

```text
Combat = 1.5 × (0.65 × 循环收益 + 0.35 × 开局抽手收益)
w = 0.25 × clamp((剩余战斗数 − 2) / 10) × 队伍最低存活血量比例
Growth = w × (三个成长场景的平均 Combat − 当前 Combat)
S = Combat + Growth + 社区先验 + 未建模效果估计
    − 规模项 + 已知 Boss 项 − 固定费用风险
固定费用风险 = 16 × Σ(固定费用 / max(1, 稳定能量))^4
```

成长场景从角色公开普通/罕见卡池，按已有社区先验挑选攻击、防御、抽牌/能量等四张牌，再使用原生升级副本模拟一次升级。修改前后使用同一公开规则。没有读取未来奖励或消耗游戏随机数，也不修改真实卡组。

末期不计成长；队伍受伤时降低权重。费用风险继续豁免 0 费、X 费与不可打出的牌。权重 16、战斗尺度 1.5、成长权重 0.25 来自开发集粗网格与商店锚点的联合校准，并非卡手概率的测量结果。

基础牌仍沿用上一轮 `−6` 的先验。起手删牌收益有很大一部分来自这一项；不能声称本轮已经仅靠抽牌模拟推导出了删牌价值。删除、拿取和升级都受同一个先验约束，删除本身没有专用奖励。

在单次只读决策中缓存卡组评分；不同费用、升级、附魔、动态数值、关键词的卡组有不同键。商店、营地与多选牌结束时丢弃缓存，避免跨决策读取旧状态。

## 4. 商店测量

价格：Bully 75、Bludgeon 75、删牌 100；HP 80/80、稳定能量 3、金币 100。原生模型确认两张牌稀有度相同。`H = 5.718`。

| 动作 | ΔS | 金币估值 | 扣价后的净值 |
|---|---:|---:|---:|
| 买 Bully | 16.331 | 364.207 | 289.207 |
| 删当前首选基础牌：痛击 | 6.991 | 155.922 | 55.922 |
| 买 Bludgeon | 5.191 | 115.758 | 40.758 |

实际 `BotShopPlanner.Choose`：三项都在时买 Bully；去掉 Bully 后选择删牌。删防御的收益也高于买 Bludgeon 的净值。当前对痛击的删除排名略高于防御，属于需要后续实机关注的模型判断，并未通过卡名规则保护或排除它。

4 能量起手允许选择 Bludgeon：Bully 13.383、Bludgeon 17.969。没有固定让 Bully 永远胜出的规则。

原始记录：`work/deck-outcome-optimization/bully-anchor.json`、`bully-acceptance.log`。

### 删牌次数与历史日志

四个中期卡组、每组六次商店、每次新增 150 金，竞争货架为 60 金的一张固定抽牌卡。早期夹具错误地使用删牌价格 100 起每次加 25；2026-10-01 按原生 A10 Inflation 修正为 100 起每次加 50。下文旧频率数据不能作为正确 A10 价格下的结论，修正结果见 §17。它是敏感性夹具，不是完整爬塔。每次只执行计划的首笔购买，不模拟该店余下购买循环。

本轮删牌次数是 **3、3、2、4**，平均 3.00。第四组依次删 Bludgeon、痛击、Cinder、打击；第四次打击估值 187.04 金，大于 175 金的价格。运行时代码没有“三次后停止”的分支。

按每次现金变化与最终余额核对实际执行，四组包含的基础牌删除数分别为 **1、2、1、2**（防御；打击与防御；防御；痛击与打击）。汇总脚本保存完整删除序列；日志里的最佳删除候选没有自动算成已删除。

旧测试要求四组每一组都恰好删 2–3 次。本轮按用户“绝大多数对局”的表述，明确改为：四组至少三组在 2–3 次、平均数在 2–3、没有零删牌组。**这属于验收口径调整，且这四组参与了校准，不能视为独立验证或全体对局频率。** 原来严格逐组检查得到第四组 4 次的失败日志仍保留。

最新日志的八次历史商店重建仍然没有选删牌：前四次优先购买了模型认为更有价值的卡；后四次只有 61–67 金，付不起 100 金删牌。价格和卡组可以重建，HP、遗物、完整后续购买与反事实路线不能完整还原。**因此没有声称历史零删牌问题已经在实机消失。**

原始记录：`shop-anchor.json`、`shop-acceptance.log`；中间失败记录 `shop-final.log`、`shop-scale-4.5.log`、`shop-calibrated.log`。

## 5. 社区回归

沿用缓存的 327 份 Spire-Codex 记录，按 seed、开始时间与玩家集合去重。原生投影 857 个早期 A10 选择；只对纯战斗奖励、可还原队友卡组的 **514 次选择、99 场对局**做同记录比较。

| 指标 | 上一轮费用风险版本 | 本轮 |
|---|---:|---:|
| 与记录选择相同 | 203/514，39.49% | 201/514，39.11% |
| 只看当前 v0.111.0 | 177/442，40.05% | 180/442，40.72% |
| 每场等权后的变化 | — | −2.77 个百分点 |
| 按整场重采样的 95% 区间 | — | −9.16 至 +3.67 个百分点 |

此样本不足以证明改善。只修机制、尚未重新校准的中间版本为 37.94%；最终参数仅在开发集与锚点上选择。旧留出集在之前的迭代中已被查看过，不能称为新的独立测试集。

当前版本子集按整场等权的变化为 −0.69 个百分点，95% 重采样区间为 −7.87 至 +6.28 个百分点，同样跨过零。

开发集 429 次：39.39% → 39.63%；旧留出集 85 次：40.00% → 36.47%。单人 40.80% → 40.80%，四人 38.26% → 37.50%。战士 39.78% → 41.99%，机器人 37.41% → 32.37%，猎手 44.57% → 38.04%。猎手按场等权变化为 −8.65 个百分点，重采样区间 −18.89 至 −1.18，必须保留这项回退信号；这些分层是事后诊断，未校正多重比较。完整分层均保存在报告中。

逐项核对猎手的改变：10 次原本一致变为不一致，4 次原本不一致变为一致。回退主要落在 Deflect（2 次）、Piercing Wail（3 次）、Leg Sweep（2 次）、Fade 和 Sidestep；另一次是玩家跳过而模型拿 Echoing Slash。它提示防御、减伤和有限战斗收益尺度需要继续验证，并不能由本次社区记录推导出因果结论。所有改变的选择及前后分数见 `work/deck-outcome-optimization/changed-choices.json`。

小样本卡牌明细也不支持直接把站点拿率当奖励屏选择概率：Bludgeon 仅有 10 次被提供，玩家选 6 次，前后 Bot 都选 4 次；Bully 仅 6 次被提供，玩家选 0 次，前后 Bot 都选 5 次。站点总体 3.8%/6.7% 的分母与这里的“被提供后选择”不同；这几条样本无法可靠估计两张牌的总体强弱。

仍有限制：选中的早期 A10 缓存并非随机样本；已有社区先验可能与缓存重叠；HP/遗物/真实遭遇没有还原；历史记录包含多个游戏版本，统一用当前 v0.111.0 原生模型投影；不测反事实胜率；没有新跑实机爬塔。详细分层与参数网格见 `regression-summary.json`，原生结果见 `community-before.json`、`community-after.json`。

## 6. 回归边界与证据

本轮对旧夹具的调整逐项记录如下，避免把政策变化混成代码修复：

- Deprived 默认 100 能量。普通商店、营地和力量收益夹具显式设为 3 能量；费用升级比较保持同一卡组，比较 2 与 4 能量。
- 原“第五张双重打击在力量卡组一定更好”改为比较第一张收益牌。有限遭遇中第五张攻击会挤占启动或防御；流派识别检查仍保留。
- 撤销“有收益的加牌总要压过基础删牌”的旧排序断言，检查删牌等于共同 S 差值。缺防加牌、唯一防御保护继续保留。
- 23 张卡、其中 12 张剑柄打击的夹具不再强制拿血墙。原生血墙为 2 费、掉 2 HP、16 格挡；有限遭遇下其增量可以不足以抵消先验与机会成本。保留缺防诊断；正收益防御牌与缺防/饱和比较由既有抽手夹具守卫。
- Cook 的“全是好牌”夹具原本含负先验的 Anger、IronWave、痛击，改为只有关键防御/抽牌、能量、范围输出的精简卡组。拒绝移除两张关键牌的检查保留。
- 余像升级保持正收益及实际营地比较；不再断言它必定大于一张可加快击杀的攻击升级。
- 普通与流派商店夹具统一现金和能量，都以共同净值决定；没有流派附加价。
- 金币换算改变后，沉没雕像的“明显划算”样本仍给出 40 点购买力，藏宝图用公共换算校验 600 金，保留任务价值与战斗价值分层。
- Bully 无易伤时升级为零的检查限于当前战斗模型；成长场景可能加入新的易伤来源，不能要求整个未来势函数也为零。
- 新增显式成长后，不再要求同一张牌跨幕的总增量完全相同；改为扣除成长分量后相同，继续守卫“不得额外加候选卡年龄税”。

新断言按项目 R2 做生产代码故意改坏、确认失败、恢复。19 个故障均得到目标断言失败（退出 1），逐项命令与失败原文见 `work/deck-outcome-optimization/r2-results.json`；最终构建、类型加载、普通与完整内核门禁以 `work/verification/` 的本轮 `summary.json` 为准。交付是带独立标签的测试包，未安装到游戏目录。

最终四门记录：[`20260930T155559445Z/summary.json`](../work/verification/20260930T155559445Z/summary.json)，四阶段退出码均为 0。生产构建 **6 warnings / 0 errors**；无内核依赖时枚举 **571** 个类型并完成初始化 JIT；普通 **268 PASS**；完整内核 **429 PASS**。`state.py gate` 已记录本次结果。

可重复执行的入口：

```powershell
& work/dotnet-win/dotnet.exe run --project tests/PatchSmoke -c Release '-p:STS2DataDir=B:/SteamLibrary/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64' -p:NuGetAudit=false -- --construction-outcome
# 同一命令的末尾参数分别换成：
# --bully-price-anchor
# --shop-removal-anchor
# --unified-community --community-output=work/deck-outcome-optimization/community-after.json
python scripts/audit-deck-outcomes.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1
```

`building-restored.log`、`temporary-stats.log`、`finale-restored.log` 保存聚焦检查（退出 0）。扩大的审计曾发现 Grand Finale 条件遗漏，失败日志 `unified-restored.log` 保留；最终代码以四门运行证明恢复。`input-sha256.json` 记录原生探针程序集、卡组估值源码、社区原始文件、汇总脚本与输入结果的 SHA256。

## 7. 候选测试包

[CoopBots-v0.39.1-deck-outcomes-20261001T000619.zip](../outputs/CoopBots-v0.39.1-deck-outcomes-20261001T000619.zip)

`scripts/build.ps1 -Label deck-outcomes-20261001T000619` 于北京时间 2026-10-01 完成，再次通过普通 268 / 完整内核 429 PASS，退出 0。包内仅有两份生产 DLL、manifest、第三方说明和 README/CHANGELOG。两份 DLL 均与上述四门版本哈希一致，371 项输入哈希复核一致。没有安装到游戏目录；社区结果不足以把该候选版标记为效果验证通过。

ZIP SHA256：`D3DB3CEC42D9949D6122B83D7810355489E7E82DD33F522B95FE892939E31392`。完整命令、退出码、包内容与哈希见 [`package-evidence.json`](../work/deck-outcome-optimization/package-evidence.json)，构建输出见 `package.log`。

## 8. Prepared / Anticipate 机制审计（2026-10-01）

**当前排序符合用户锚点，但机制计算不完整。** 本轮只增加诊断夹具和汇总脚本，生产程序集与 §7 的候选版相同。

使用原生 Silent 起手牌、A10 的 Ascender's Bane、3 能量、100 金，分别构造起手、三张 Wound、Reflex + Tactician、四张 Deflect、Afterimage + 三张 Wound 五类卡组。每类跑单人和四人，共十例；商店两牌同为 50 金，十例都选择 Prepared。

起手牌组加牌分解如下。单位是模型分数，不是伤害或胜率。

| 加牌 | 战斗增量 | 社区先验增量 | 成长增量 | 总增量 |
|---|---:|---:|---:|---:|
| Prepared | −3.222 | +10.880 | −0.220 | +7.438 |
| Anticipate | +2.857 | −12.000 | −0.258 | −9.401 |

消融保持牌的身份不变，从而保留抽样哈希和先验。原生 Prepared 先抽一张再选弃一张，升级后变成两张；原生弃牌命令在弃牌钩子之后自动打出 Sly 牌。对照本地 `v0.111.0` 的 `Prepared.OnPlay`、`CardCmd.DiscardAndDraw`：

| 原生动作 | 构筑模型现状 | 本轮测量 |
|---|---|---|
| 抽牌揭示新选择 | 两个抽手模型用净手牌增量控制揭牌；Prepared 普通/升级均为 0 | 隐去 Prepared 毛抽牌，十例战斗增量均不变 |
| 实际选择弃掉一张牌 | 周期模型有按负面手牌数量清理的近似，但先由抽手模型决定是否出牌 | 隐去清理，在前四类卡组为 0；Afterimage + Wound 场景贡献 +3.260 |
| Sly 弃牌自动打出 | 构筑抽手模型没有对应状态转换 | 移除持有牌的 Sly 关键词，两个 Sly 场景均不变 |
| Anticipate 临时敏捷 | 已进入当前回合格挡估计 | 阳性对照：隐去效果后评分下降，证明诊断能测出有效效果 |

因此不能用“Prepared 排名更高”证明算法理解了筛牌或 Sly。也不能声称全部弃牌价值都为零：Afterimage 场景已经计算了一部分清理收益。此结论限于构筑模型，不能外推为局内战斗内核没有弃牌机制。

复用 §5 的同程序集社区投影，92 个符合筛选条件的 Silent 奖励屏幕中，Prepared 被提供 8 次，玩家选 3 次、当前 Bot 选 7 次；Anticipate 被提供 6 次，玩家选 0 次、Bot 选 0 次。两张牌从未同时出现在同一奖励屏幕，样本不足以验证两牌的真实相对强度。用户的 21.2% / 5.6% 保留为社区偏好锚点，不能与这里的“被提供后选取次数”直接混算。

可重复命令：

```powershell
& work/dotnet-win/dotnet.exe run --project tests/PatchSmoke -c Release '-p:STS2DataDir=B:/SteamLibrary/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64' -p:NuGetAudit=false -p:OutputPath=B:/slay-the-spire-2-mod-mod/work/prepared-anticipate-audit/native/ -- --prepared-anticipate-audit
python scripts/audit-prepared-anticipate.py
```

两条命令退出 0。探针日志原文为 `AUDIT COMPLETE: measurements saved; completion is not a mechanism acceptance verdict.`；它表示测量完成，不表示缺失机制验收通过。原始数据、消融控制量、输入 SHA256 和社区筛选见 `work/prepared-anticipate-audit/`。四门记录 `work/verification/20260930T162309119Z/summary.json`：各阶段退出 0，普通 268 / 完整内核 429；这些既有回归通过不能替代上述机制缺口。

## 9. 鲁棒性收益的计算建议（历史方案；实现见 §10）

用户希望评估一张牌为卡组增加多少容错，尤其是在 Boss 塞负面牌之后。建议将它定义为**同等干扰下损失的减少**，与选牌、删牌、升级共用同一个卡组函数。

令 `V0(D)` 是无额外干扰时的战斗评价，`Vs(D)` 是场景 s 下的评价；已有诅咒或状态牌始终保留在 D 中。两边用相同战斗单位、压力、抽样序号和聚合规则。`ps` 是固定场景权重、总和为 1：

```text
L(D) = Σ ps × [V0(D) − Vs(D)]
S新(D) = S无额外干扰(D) − λ × L(D)
一次改牌的鲁棒性收益 = λ × [L(修改前) − L(修改后)]
```

`λ` 控制对这些干扰场景的重视程度；若解释为干扰遭遇占比，就限制在 0..1。系数尚未校准，不能由单张牌的 pick rate 直接换算。损失及其差值保留正负；使用绝对效用差，避免一个本来很弱、受干扰后变化也小的卡组仅因“保持率高”获高分。

例如（仅演示公式）：原卡组正常 100、干扰后 70，损失 30；加牌后正常 104、干扰后 84，损失 20。普通收益为 4，鲁棒性收益为 `10λ`；若示例取 λ = 0.3，总收益为 7。

### 接入顺序

1. **补机制**：区分毛抽牌与净手牌增量，实际选择弃牌目标，处理 Sly 自动打出；弃牌只清当前手牌，消耗还减少后续污染。不能简单把 `NetDraw` 全改成毛抽牌而漏付弃牌成本。
2. **统一干扰场景**：先覆盖占抽牌位、回合末伤害、持续塞牌；分别核对放入手牌/抽牌堆/弃牌堆和生成时点。抽样保持配对，未知 Boss 使用固定公开场景，可见 Boss 使用对应机制。动作触发的塞牌允许因出牌数变化，击杀后停止生成。
3. **替换既有重叠项**：当前 `SustainedDeckValue.Strength` 已把污染场景混入均值和最差四分位，`BossPreparation` 又在公开 Aeonglass 时单独计算清理，并关闭通用污染。新鲁棒性项必须统一接管这条分支，不能叠加到当前 `Combat + Boss` 上；成长场景也调用同一规则。
4. **再校准与回归**：先验证机制消融，再用独立对局分组的社区留出样本评估参数。继续报告 Bully / Bludgeon / 基础删牌、Prepared / Anticipate、固定商店夹具、历史商店重建与各角色变化，不强制某张牌永远胜出或每局删固定次数。

### 必须能分辨的情况

- 缺少清理的卡组加入第一张解牌有帮助；清理能力充足时继续堆解牌收益应受真实手牌需求限制。
- 抽牌、定向弃牌、随机弃牌、消耗必须不同；纯抽牌不能凭标签获得“清除伤害状态”加分。
- 输出足以提前击杀时，可以通过减少敌人塞牌次数获得收益。
- Prepared 的筛牌和 Sly 收益应在关闭社区先验后仍能被相应场景及消融测出；无相关条件时允许收益很小或为负。
- 删除基础牌可能加快解牌循环，也可能提高负面牌浓度；两者一起估计。加牌与反向删牌必须互逆，升级等于替换前后的同一 S 差值。

该方案提出时，起手卡组增加三张 Wound 的诊断中，Prepared 的战斗边际值从 −3.222 变成 −3.926，相对变化 −0.704；Anticipate 从 +2.857 变成 +0.294，相对变化 −2.563。这只是旧混合场景对起始污染的敏感度，不是原生 Boss 重放。§8–9 记录的是实现前诊断；后续实现与验证见下文。

## 10. 鲁棒性收益实现（2026-10-01）

### 统一势函数与 A10 权重

`DeckStrength` 的加牌、删牌、升级仍全部使用 `S(修改后) − S(修改前)`。成长预测也使用同一鲁棒性评价；不另给弃牌或消耗标签加固定分。

普通战斗保留六回合、单体/群体、不同攻击压力的评价。额外构造三个配对的耐久场景：相同 240 HP、九回合、18/26 攻击压力、16 个抽样，分别为无干扰、凋萎、灵魂锁链。每个场景使用 70% 均值与 30% 最差四分位，统一为每回合效用。九回合覆盖凋萎第三档伤害，避免短战斗低估后期污染。

```text
V压力 = 0.65 × min(V凋萎, V锁链) + 0.35 × mean(V凋萎, V锁链)
R(D) = λ × (V压力(D) − V耐久无干扰(D))
S(D) = 原普通战斗分量 + 1.5 × 6 × 0.65 × R(D)
       + 成长预测 + 社区先验 + 未建模补偿 − 规模成本 − 费用占用风险
一次改牌的鲁棒性收益 = 1.5 × 6 × 0.65 × [R(修改后) − R(修改前)]
```

A10 三幕的 **λ = 0.80、0.90、1.00**；低于 A10 为 0.40、0.55、0.70。A10 始终评估两种公开机制，较差机制占最终压力评价的 82.5%，避免只防住一种 Boss 掩盖另一种机制下的崩溃。低难度遇到已公开的女王/沙漏时使用对应压力场景。系数表达生存偏好，**不是遭遇概率，也不是由 pick rate 推出的权重**；不读取隐藏第二 Boss。

旧通用污染混合和独立 `BossPreparation.Strength` 已从生产势函数退出，`Boss=0`，避免重复计算。相关旧测试不再要求“公开沙漏后 Prepared 额外加分”，而守卫 A10 在两个公开 Boss 下评价相同、升级与加牌都使用统一项。跨幕不变性检查扣除成长与鲁棒性分量后比较，承认本次明确引入的跨幕生存偏好。

### 真实手牌约束

开手与循环共用 `ConstructionHand`：最多九张牌、32 个候选状态，确定性抽样，不使用局内洗牌结果或 RNG。Prepared、Acrobatics、Dagger Throw 先揭示毛抽牌，再从真实手牌付出弃牌；Burning Pact 先消耗再抽；Calculated Gamble 先全弃、等量抽牌，再处理 Sly。弃牌消除当前手牌伤害，消耗还减少后续污染库存。随机消耗使用固定抽样目标，不替玩家挑最佳目标。

Reflex / Tactician 被弃时按 Sly 免费自动打出一次。补齐 `CardResourceTiming` 的 Tactician 能量映射（原生普通/升级为 1/2）；该事实映射同时供构筑与战斗搜索启发式使用，原生执行和结算规则未改。锁链同样约束自动打出。Second Wind 等按实际目标数结算，Afterimage 的每次出牌格挡只计算一次。

本地 v0.111.0 原生核对：女王首回合施加锁链，随后每回合最先抽到的三张可附魔牌被绑定，只能打出其中一张；无效状态也可以占绑定名额。沙漏每六次出牌后向手牌加入凋萎，生成发生在当前牌的清理之后；每三回合额外向弃牌堆加入两张，凋萎伤害逐档增加。直接击杀停止后续伤害和塞牌；毒在敌方回合开始生效，不能提前取消玩家手牌末尾伤害。

### 校准范围与限制

费用风险统一权重由 16 调为 **31**，金币换算由 3.9 调为 **3.6**，所有永久买牌/删牌共用。它们与新的耐久效用共同按既有商店锚点校准，没有卡名特判或删牌次数配额。中间版本权重 26 仍偏爱 Bludgeon；九回合版本金币换算 3.3 的六店结果为 2、0、1、3，留下失败记录后才调整。这些锚点参与了校准，不能当独立效果验证。

该模型估计机制压力，**没有完整重放 Boss 战**：未覆盖女王各阶段的虚弱/易伤/脆弱、召唤物、所有遗物钩子、同回合重洗与无限循环；能力启动仍部分用期望值。基础牌先验 −6 和社区先验仍存在，不能把总增量全部归因于新增机制。Sly 牌的静态费用风险仍按印刷费用估算；九张窗口及有限抽样也会影响边际排序。抗干扰提高后的实机胜率尚需完整爬塔验证。

### 多人共享易伤（用户追加修正）

原模型存在受益不对称：Bully 会读取队友提供的易伤，但痛击施加易伤时没有得到队友攻击增伤的回报。现在每个压力场景都计算本卡组带来的额外队友收益，因此普通战斗、鲁棒性、成长、加牌、删牌和升级共同生效。

每位**存活**队友按自己的公开卡组与稳定能量贡献攻击供应，人数通过求和自然放大价值；空卡组和纯毒卡组没有虚构的攻击收益。原生 `VulnerablePower.ModifyDamageMultiplicative` 对受影响目标的 powered attack 增伤，标准值为 50%，不限定施加者。估值只记本卡组额外创造的覆盖率，队友已有易伤越多，新增覆盖率收益越低；Bully 对易伤层数的额外伤害也计入。共享收益作为效用计入，不伪造本席击杀来取消自己的凋萎伤害。

这是按能量约束的队友期望输出估计，尚未完整模拟多人行动顺序、遗物改倍率、所有队友在锁链下的可执行手牌及完整 Boss 生命/阶段。它修复共享增益被漏算的问题，不能代替联合战斗模拟。没有加入“痛击不得删除”的卡名保护。

固定同一副 A10 起手卡组，只增加存活攻击队友：痛击删除收益由 **+4.293（1 席）→ −0.321（2 席）→ −9.084（4 席）**。四名战士都带起手痛击时，删除痛击为 **−2.157**，首选删除防御；痛击升级收益 +8.468。纯毒队友对痛击删除价值没有额外影响。故障注入分别切断共享收益、计入死亡队友、把毒伤混为攻击、反转共享收益符号，均能触发断言失败。

### 已测锚点

A10 四名战士原生起手（含进阶之灾）、3 能量、100 金、Bully / Bludgeon 同为 75 金、删牌 100 金：

| 选项 | 统一 S 增量 | 折算金币价值 | 扣价格后净值 |
|---|---:|---:|---:|
| 买 Bully | 5.027 | 103.488 | 28.488 |
| 删本次最优基础牌：防御 | 5.923 | 121.930 | 21.930 |
| 买 Bludgeon | −4.488 | 0（负收益截为 0） | −75 |

真实商店入口有 Bully 时买 Bully，去掉 Bully 后删防御。单人 A10 对照：Bully +6.625、Bludgeon −0.515，删除打击 +5.190，商店同样有 Bully 时买、无 Bully 时删牌。A0 单人/四人也满足买 Bully、删牌优于 Bludgeon；A0 四人删除打击。

早期 A10 商店夹具遗漏了进阶之灾，多人夹具还把三名队友的卡组留空。`bully-final.json` 与旧 `shop-final.log` 保留该中间结果，**最终结论以修正输入后的 `bully-shared.json` 与 `shop-shared.log` 为准**。因此新旧商店分数同时反映机制改动和输入修正，不是单因素对照。上述存活席位对照才保持本席卡组不变。

四组旧日志中期卡组、每组六次固定商店，补全同一快照的队友卡组后仍删 **3、3、1、3 次**，平均 2.5 次。其中三组符合 2–3 次，没有零删牌；一组只删一次，模型认为继续删收益不足。每组只改变被测席位，其余队友保持快照，且每店只执行首笔交易，这是敏感性检查，**不是完整爬塔或人群频率估计**。旧日志八次商店库存重建又回到零次首选删除，具体库存有更高净值购买或现金不足；原先会删除痛击的那一席现在优先买 Stone Armor。不能据此宣称实际零删牌已经消失。

Prepared / Anticipate 的十例同价商店仍全部选 Prepared。A10 猎手起手 Prepared 总增量 **+15.797**，分解为普通战斗 −0.337、鲁棒性 +3.922、成长 +1.332、先验 +10.880；Anticipate 总增量 **−8.286**。鲁棒性在三张 Wound 场景提高为 +4.610，在四张 Deflect 场景为 +7.656，已经响应当前卡组条件。

最终消融直接重算生产用的“普通战斗 + 耐久损失”，排除先验与成长。Prepared 起手的实际弃牌贡献 **+2.850**；Wound 场景毛抽牌贡献 **+2.355**、弃牌 **+1.580**。但起手毛抽牌贡献 **−2.040**，已有 Survivor / Reflex / Tactician 场景的 Sly 对 Prepared *边际价值*贡献 **−2.691**；这些负数保留，不把所有抽牌或 Sly 标签强制加分。关掉 Sly 同时改变了加入 Prepared 前的卡组，且多出牌会增加凋萎，因此差值不能直接解释为“Sly 本身有害”。有限搜索也可能影响这类边际值，仍需实机复核。

固定手牌检查独立证明：Prepared 能弃掉 Wound 后打出新抽攻击；强制弃牌不能保留两个目标同时打出；弃掉 Tactician 可免费获得能量支撑 3 费攻击；Reflex 可抽出两张真实攻击；锁链可拦截这些自动打出。相关生产故障注入均能使断言变红。

`prepared-six-turn-ablation.json` 保留中间诊断，其消融仍调用已退役的六回合混合场景，不能当最终生产消融。共享易伤修正后的最终结果为 `shared-prepared-after.json`，日志 `prepared-shared.log`；`prepared-after.json` 与 `prepared-production-final.log` 是此前阶段的记录。

额外旧测试政策调整：不再断言十九张剑柄打击/两张防御的卡组一定优于十三张剑柄打击/五张防御，保留 150 金基础删牌可负担与唯一防御保护。更多能量可以同时改善加牌前后两副牌组，故不再要求 Blood Wall 的边际值随能量单调增加；能量压力本身的直接单调性检查保留。原实录双删夹具选择 Mangle / Snakebite，保留不能把 Demon Form / One Two Punch 当空白删除的守卫，撤掉“至少删一张基础牌”的附加要求。营地 55% 阈值夹具改为持有可以降费的 Corruption，继续守卫有价值升级在阈值两侧都能竞争；营地生产规则未改。光暗门夹具显式设置 3 能量，消除 Deprived 默认 100 能量的输入错误，原排序和预测/页面顺序守卫均保留。

### 社区回归：机制锚点通过，整体效果未通过验证

沿用 327 份缓存，原生投影 857 次选择，按同一筛选规则比较 **514 个奖励屏幕、99 场对局**。对照是 §5 的有限战斗候选版，所有记录的对局、层数、角色、候选、实际选择与卡组大小均逐项对齐。

| 分层 | 修改前一致次数 / 比率 | 最终候选 |
|---|---:|---:|
| 全部 | 201/514，39.11% | 187/514，36.38% |
| 四人 | 99/264，37.50% | 101/264，38.26% |
| 猎手 | 35/92，38.04% | 44/92，47.83% |
| 战士 | 76/181，41.99% | 64/181，35.36% |
| 机器人 | 45/139，32.37% | 33/139，23.74% |
| 当前 v0.111.0 | 180/442，40.72% | 167/442，37.78% |

全部样本按整场等权的变化为 **−3.73 个百分点**，整场重采样 95% 区间 **−8.58 至 +0.76**。机器人分层为 −5.92 个百分点，区间 −9.80 至 −2.41；猎手为 +8.47，区间 +2.92 至 +15.28。这些是事后分层，未校正多重比较。旧留出集已经被查看过，不是新独立验证集。开发集 39.63%→35.90%，旧留出集 36.47%→38.82%。

逐项有 **35 次由一致变为不一致、21 次由不一致变为一致**。机器人回退包含 Claw 四次、Darkness 两次，及 Turbo、Boost Away 等；Turbo 的一个样本鲁棒性增量为 −10.05，导致由选择变为跳过。Darkness / Fusion 的排序仍依赖未建模补偿与压力残差，提示球机制覆盖不足不能靠较大 λ 自动补好。猎手改善包括 Piercing Wail 三次、Dagger Throw 两次及 Leg Sweep / Fade；没有因检查成功就忽略其他角色回退。

额外保存只加入鲁棒性、尚未加入共享易伤的中间投影：整体 190/514（36.96%），四人 104/264（39.39%）。共享易伤修正后分别变为 187 与 101，单人预测不变。**共享易伤解决了痛击锚点，但这份社区样本没有证明它提高选牌一致率。** 所有阶段来自相同记录和冻结程序集，未用最终标签再调权重。

四张卡的被提供样本很少：Prepared 提供 8 次，玩家选 3 次、前后 Bot 都选 7 次；Anticipate 提供 6 次，玩家与 Bot 都选 0 次；Bully 提供 6 次，玩家 0、Bot 5→4；Bludgeon 提供 10 次，玩家 6、Bot 4→0。降低 Bludgeon 的评分符合指定商店锚点，但不能据此声称已拟合站点 3.8% pick rate，也需要警惕过度惩罚。站点全对局拿率与“被提供后选取率”不是同一分母。

因此本轮交付是**实现和可复现候选**，没有把整体策略标记为效果验证通过，也没有声称胜率提高。需要进一步复核机器人的球与循环机制，以及高费用/多次出牌在干扰场景中的实际收益；应使用新的独立对局或实机对照，避免继续在这 99 场记录上拟合。完整分层、改变的选择、消融与输入 SHA256 见 `regression-summary.json`、`changed-choices.json`、`input-sha256.json`；汇总命令 `python scripts/audit-robustness.py` 退出 0。

### 可重复验证与故障证据

本轮 **23 个生产故障注入**均触发目标失败（退出 1），恢复后对应检查退出 0：基础机制 14 个、权重与重复 Boss 项 2 个、群体消耗计数 1 个、耐久回合数 1 个、共享易伤 4 个、营地阈值 1 个。完整命令、退出码与失败原文见 `work/robustness-optimization/r2-results.json`、`r2-extra-results.json`、`r2-mass-result.json`、`r2-endurance-result.json`、`r2-shared-results.json`、`r2-shared-sign-result.json`、`r2-camp-result.json`。失败原文也写入相应检查注释。

恢复日志包含以下原文：

```text
PASS: robust construction covers actual filtering, discard payment, Sly, Chains, Wither timing and one shared potential.
PASS: shared Vulnerable values living allied attacks, preserves starter Bash and excludes poison damage.
PASS: fixed six-shop cohort centres on 2-3 removals without a runtime removal quota.
```

入口示例（把最后一个参数替换为 `--shared-status-only`、`--bully-price-anchor`、`--shop-removal-anchor` 可分别重跑）：

```powershell
& work/dotnet-win/dotnet.exe run --project tests/PatchSmoke -c Release '-p:STS2DataDir=B:/SteamLibrary/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64' -p:NuGetAudit=false -- --robustness-only
& work/dotnet-win/dotnet.exe work/robustness-optimization/shared-final-native/PatchSmoke.dll --unified-community --community-output=work/robustness-optimization/shared-community-after.json
python scripts/audit-robustness.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1
```

### 最终四道门

上述 `verify-project.ps1` 命令退出 **0**，记录为 `work/verification/20260930T174810875Z/summary.json`；四个阶段均退出 0。构建 **0 错误、6 个警告**，与既有警告基线相同；普通套件 **270 PASS**，完整内核套件 **431 PASS**。类型加载日志原文：

```text
CoopBots.dll enumerates 595 types with CoopBots.Kernel unresolvable.
ModEntry.Initialize JITs without CoopBots.Kernel.
```

内核覆盖日志为 `CARD COVERAGE: total=598 modeled=553 unmodeled=7 choice=10 broken=0 skipped=28`；后面的空 `COVERAGE BROKEN:` 是空清单标题。药水日志仍列出既有未模拟项，不能把通过门禁理解为全机制覆盖。构筑模型的实机单次决策延迟未测。

生产程序集与最终社区投影使用的冻结程序集逐个 SHA256 相同：

```text
CoopBots.dll         357DD79C1920C189E2BEB890F0B3A0125B74D5DB4CA273436D7188B29B7C819A
CoopBots.Kernel.dll  9364E6063D3BC1D36221765FE8981BB9A124B80E22F6C82E38F44DEE58C9CEA1
```

`python scripts/state.py gate --label robustness-shared` 退出 0，已登记本次四门结果。

### 鲁棒性与共享易伤候选包

[CoopBots-v0.39.1-robustness-shared-20261001T0215.zip](../outputs/CoopBots-v0.39.1-robustness-shared-20261001T0215.zip)

`scripts/build.ps1` 使用独立标签与显式游戏目录，退出 **0**，再次完成普通 **270 PASS** / 完整内核 **431 PASS**。为隔离正在运行的独立验证，`-Dotnet work/robustness-optimization/package-dotnet.ps1` 将检查程序输出到单独目录，仍调用工作区 .NET 并执行未过滤的全部检查。精确参数与原始输出见 `work/robustness-optimization/package-evidence.json`、`package.log`。

`python work/robustness-optimization/record-package.py` 退出 **0**，记录 `package-audit.log`。包中仅有两份生产 DLL、manifest、第三方说明、README 与 CHANGELOG；两份 DLL 与四门/社区版本一致，**374 项回归输入 SHA256 全部匹配**，上一候选包 SHA256 保持不变。当前包包含本工作区的生产代码，未安装到游戏目录，未执行新实机爬塔。

ZIP SHA256：`9E6382CCA9215B5418C91094CFD0642858137806C81952A979752ED5B1B024DB`。通过打包门禁表示实现检查通过；社区整体一致率下降的结论仍然成立。

## 11. 奖励界面卡顿诊断（2026-10-01）

本节保留优化前的诊断；实现与验收见 §12。

**确认新版评分引入了明显性能回退，并在奖励回调中同步阻塞。** 游戏安装目录的 `CoopBots.dll` SHA256 与 §10 候选包相同。此前只完成正确性门禁，缺少主线程奖励延迟验收；该候选存在秒级卡顿，性能尚不合格。

日志快照：`work/reward-latency-audit/godot.log`，来自用户指定的最新日志。原日志动作时钟里，最后一次战斗出牌到下一次地图投票为：

| 区间 | 动作时钟 | 间隔 |
|---|---|---:|
| 第一场结束后的奖励阶段 | 19:19:01 → 19:19:44 | 43 秒 |
| 第二场结束后的奖励阶段 | 19:20:17 → 19:21:06 | 49 秒 |

这是整个阶段的间隔，包含动画、交互等，**不能全部算作评分耗时**。区间内四个席位依次记录奖励估值；日志没有每次评分开始/结束的计时。没有 `combat summary` 行，不能据此认定战斗执行计数全为零。快照末尾仍是商店处理，没有运行结束或退出记录，不能判为团灭或死锁。

独立测量使用相同机器、相同原生角色起手卡组、日志中的额外卡与四组三选一候选。固定 80/80 HP、3 能量、A10，未重建全部遗物和现场状态，因此是代表性同输入对照，**不是精确实机重放**。对照程序集为鲁棒性改动前 `5E083BEC...` 与当前安装的 `357DD79C...`。每版先执行冷批次再执行暖批次，依次运行避免两个基准进程竞争；仍可能受到游戏进程活动影响。

| 暖批次，四席各一次三选一 | 单席耗时 | 四席合计 | 累计托管分配 |
|---|---:|---:|---:|
| 改动前 | 1.09–1.52 秒 | 5.206 秒 | 9.53 GiB |
| 当前候选 | 3.84–6.08 秒 | 20.137 秒 | 43.38 GiB |
| 当前候选，诊断程序临时包住现有缓存作用域 | 1.33–2.09 秒 | 6.696 秒 | 14.50 GiB |

当前候选约慢 **3.87 倍**。分配量是计算过程中累计创建的托管对象总量，**不是常驻内存**。临时缓存对照在此夹具中选择相同，约快 3 倍，但仍不够流畅；它只在独立诊断进程生效，未修改或安装生产代码。

根因链路：

1. `BotRemoteChoicePatch.Prefix` 执行 `Task.FromResult(BotRewardDriver.ChoiceForWait(player))`，先同步算出选择，再生成已完成的 Task。托管席位的本地 UI 也由 `NRun._Process → BotRuntime.Tick → ForceClick` 驱动，没有计算分帧或后台隔离。
2. `BotBrain.ChooseCardReward → BuildValue.BestReward` 未启用 `DeckStrength.BeginEvaluation`；三张候选各自计算修改前与修改后的 S，导致基线重复。随后 `BuildTrace.LogReward` 为生成日志再次完整计算三张候选。测得每席 **12 次完整 S、48 次鲁棒性评价**；日志重算占新版暖批次 **9.931 / 20.137 秒**，约 49%。开缓存后完整 S 为 4 次，鲁棒性为 16 次，四席日志合计降为约 3.25 毫秒。
3. 每次鲁棒性评价都包含普通场景及九回合无干扰/凋萎/锁链场景，合计 160 条抽样轨迹；成长预测再评估三副未来卡组，手牌搜索最多九张、32 个保留状态。新版四席暖批次中成长计算的包含子调用耗时为 **17.301 秒**，约 86%。成长和鲁棒性计时互相包含，不能相加。共享队友估计也在这条重复计算链上，本次没有单独消融其耗时。

修复优先级：复用同一奖励决策的评分结果与日志；缓存不变卡牌事实、队友轨迹并减少手牌搜索分配；再通过不可变数据快照与分帧/后台计算避免长时间占用 UI 线程。不能直接把当前访问游戏模型的函数扔进 `Task.Run`。λ 的数值只是最后的加权，调小 λ 本身不会减少当前代码中的场景计算。

证据与复现：`work/reward-latency-audit/Program.cs` / `Bench.csproj`，`new.json` / `old.json` / `summary.json`，原始日志 `new.log` / `old.log`。以下两个最终命令均退出 **0**（初次使用真实角色工厂缺少独立进程的遗物环境，随后按既有原生社区夹具方式设置角色完成测量）：

```powershell
work/dotnet-win/dotnet.exe run --project work/reward-latency-audit/Bench.csproj -c Release '-p:ArtifactRoot=B:/slay-the-spire-2-mod-mod/work/robustness-optimization/shared-final-native' '-p:OutputPath=B:/slay-the-spire-2-mod-mod/work/reward-latency-audit/new-bin/' -p:NuGetAudit=false -- B:/slay-the-spire-2-mod-mod/work/reward-latency-audit/new.json
work/dotnet-win/dotnet.exe run --project work/reward-latency-audit/Bench.csproj -c Release '-p:ArtifactRoot=B:/slay-the-spire-2-mod-mod/work/prepared-anticipate-audit/native' '-p:OutputPath=B:/slay-the-spire-2-mod-mod/work/reward-latency-audit/old-bin/' -p:NuGetAudit=false -- B:/slay-the-spire-2-mod-mod/work/reward-latency-audit/old.json
```

新版日志原文节选：`"mode":"warm","seat":0,"size":13,"selected":2,"elapsedMs":6077.8604`；同一行包含 `"BuildTrace.LogReward":{"calls":1,"ms":3031.6443}`。本轮为诊断，生产代码和安装目录未改，尚未宣称卡顿修复。

## 12. 构筑计算性能优化（2026-10-01）

本轮保留 §10 的评分政策：λ、16 组抽样、九张手牌窗口、32 个搜索状态、三组成长预测及多人共享易伤计算均不变。改变的是重复计算与数据组织。

### 实现

- 奖励选择与诊断共用一次 `DeckStrength.BeginEvaluation`。三张候选只计算四副不同卡组的 S；连同成长，鲁棒性评价从 48 次降到 16 次。独立调用 `BestReward` 也复用基线。
- `ConstructionHand` 复用线程内搜索字典、数组与列表，提前计算一手牌的固定属性，打包九位状态掩码；排序显式保留原来稳定排序的同分次序。
- 弃牌与 Sly 的分支直接写入搜索候选，通过有界的续接栈保持原执行顺序，消除每个分支的列表、迭代器与临时数组。缓存只保留计算缓冲区，不保留跨手牌的决策结果。
- `SustainedDeckValue` 每副场景卡组只准备一次普通牌、能力牌、Grand Finale 可用性等固定数据；回合内用数组保存剩余牌、出牌权重和能力状态，按需生成诊断字典。
- 不随轨迹变化的队友估计移出抽样循环，并在同一次只读决策中复用。作用域退出后丢弃，下一次决策重新读取队友存活、能量与公开卡组。
- 奖励日志新增 `elapsedMs`，从研究采集开始，覆盖选择与诊断评分/格式化，结束于日志写入前。后续实机可以直接量测评分，不再以“战斗结束至地图投票”的整个阶段间隔代替。

生产变动位于 `BotBrain.cs`、`BuildTrace.cs`、`Building/BuildValue.cs`、`DeckStrength.cs`、`ConstructionHand.cs` 和 `SustainedDeckValue.cs`。没有修改本轮的战斗内核策略。

### 结果一致性与可回归性

旧 DLL `357DD79C1920C189E2BEB890F0B3A0125B74D5DB4CA273436D7188B29B7C819A` 生成金标。`ConstructionPerformanceScenarios` 核对：

1. **768 组手牌**：所有结果字段序列化后的 SHA256 完全一致，包括出牌/弃牌/消耗掩码、目标、能量、伤害、状态时机、效用与同分选择。包含全部清理类型、升级、Sly 连锁、凋萎、锁链、腐化、消耗触发、不同敌人数和抽牌量。
2. **22 组完整卡组评分**：每个分项与旧版本差值不超过 `1e-8`，覆盖 Prepared/Anticipate、Bully/Bludgeon、升级、移除、Grand Finale、能量变化和跨决策的队友死亡。
3. **实际奖励入口**：选择加日志必须只有 16 次鲁棒性评价。
4. **分配预算**：固定复杂手牌预热后连续求解 64 次，不超过 2 MiB；当前实测 **114,688 字节**。

金标文件：`tests/PatchSmoke/Fixtures/construction-performance.json`，SHA256 `D9D9717EF5314C2B09C48449EFC435C1B41A8CA651D0BE25FEE7A332D38948BD`。手牌结果摘要为 `CF48D459E73AF1477B9185714C228F9C113B32B240E44FA26FEB8D7D7F2FCDB3`。生成命令与输出见 `work/reward-performance/golden-command.json`、`golden.log`，使用冻结的旧程序集，退出 0。

`python work/reward-performance/run_r2.py` 退出 **0**。五次生产故障注入均退出 **1**，恢复后退出 **0**；精确命令、退出码及错误原文保存在 `work/reward-performance/r2-results.json` 和对应日志，且已写入测试注释。原文包括：

```text
Construction performance: sampled hand results or stable tie order changed
Construction performance: full deck scores or decision-scope invalidation changed
Construction performance: reward plus trace must evaluate four decks once; robustness calls=40, expected=16
Construction performance: repeated hand searches allocated 67874304 bytes, budget=2097152
```

### 同输入耗时对照

`python work/reward-performance/run_final_benchmark.py` 退出 **0**。它依次启动两个独立进程，各测一个冷批次和三个热批次；每批次依次执行日志夹具的四个席位，计时包含实际 `BotBrain.ChooseCardReward` 及诊断。完整回归已结束，两版测量按顺序执行。热批次报告三次中位数，不以最快一次代表结果。

| 项目 | 优化前，§10 候选 | 本次优化 |
|---|---:|---:|
| 冷批次四席合计 | 19.168 秒 | 2.451 秒 |
| 热批次四席合计，中位数 | 18.328 秒 | 1.847 秒 |
| 各席热批次中位数范围 | 3.580–5.692 秒 | 0.341–0.584 秒 |
| 每批次当前线程累计托管分配 | 43.377 GiB | 0.845 GiB |
| 四席诊断评分时间，中位数 | 9204.00 毫秒 | 3.11 毫秒 |

热批次约快 **9.92 倍**，用时减少 **89.92%**，累计分配减少 **98.05%**。分配量是计算期间分配总量，**不是常驻内存**。所有批次四席选择均为候选索引 `[2,2,1,2]`。优化后每席三选一仍有 16 次鲁棒性评价，样本与压力场景未减少。

证据：`work/reward-performance/final-commands.json` 记录两条精确命令、退出码、程序集 SHA256；`final-before.json` / `final-after.json` 为逐席结果，`final-summary.json` 为汇总，原始输出保留在同名 `.log`。代表性原文：

```text
{"mode":"warm-3","seat":0,"size":13,"selected":2,"elapsedMs":584.8729,"cpuMs":593.75,"allocatedMB":219.20157623291016,"methods":{"DeckRobustness.Evaluate":{"calls":16,"ms":544.4154},"DeckStrength.GrowthValue":{"calls":4,"ms":491.383},"DeckStrength.Evaluate":{"calls":12,"ms":584.1545},"BuildTrace.LogReward":{"calls":1,"ms":0.7806}}}
```

完整逐席记录见 `final-after.json`。

完整套件内另有 25 张牌的既有探针，普通套件的历史读数为三 Bot 奖励 **6650.1 → 1264.5 ms**、16 候选商店 **2039.9 → 665.3 ms**。这是两个完整套件各自的一次读数，作为补充，不与上述三轮对照混合。出处为 `work/verification/20260930T174810875Z/phase-03-patchsmoke.log` 和本轮 `20260930T195018210Z/phase-03-patchsmoke.log` 的 `COST:` 行。

### 四道门

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1
python scripts/state.py gate --label reward-performance
```

两条命令均退出 **0**，四个阶段退出码均为 **0**。记录：`work/verification/20260930T195018210Z/summary.json`。构建 **0 错误、6 个既有警告**，普通套件 **273 PASS**，完整内核套件 **434 PASS**。日志原文：

```text
CoopBots.dll enumerates 600 types with CoopBots.Kernel unresolvable.
ModEntry.Initialize JITs without CoopBots.Kernel.
CARD COVERAGE: total=598 modeled=553 unmodeled=7 choice=10 broken=0 skipped=28
```

后面的空 `COVERAGE BROKEN:` 是空清单标题；原有药水未模拟项仍有具名清单。

冻结并用于计时的程序集与四门产物 SHA256 相同：

```text
CoopBots.dll         16047C1602943A9B82187616966073A9DD9667208E6B1F41D28937E8692B932D
CoopBots.Kernel.dll  9364E6063D3BC1D36221765FE8981BB9A124B80E22F6C82E38F44DEE58C9CEA1
```

### 验收边界

计时夹具使用 §11 的四席角色、卡组及奖励候选，固定生命和能量，属于独立进程中的实际奖励回调测量。它不包含游戏动画、其他 MOD 和现场所有遗物，不能当作实机帧率或奖励阶段总时长。该版本评分仍同步执行；本节未实现 UI 分帧或后台快照调度，不能声称零卡顿。后续异步改动见 §13。

## 13. 局外计算分批、并行及性能档位（2026-10-01）

原生卡牌读取、升级副本生成和决策提交留在游戏线程。`ConstructionDecision` 的只读预评估发现缺失卡组，复制不可变事实；专用工作线程只运行纯场景。完成后在游戏线程重放同一公式，完整结果才可提交。每批核对卡组、角色状态、队友、房间和候选；变化时重算。缓存只活在一次决策中，不跨状态复用。

| 档位 | 手牌采样 | 搜索宽度 | 每批最多卡组 | 准备软预算 | 工作线程上限 |
|---|---:|---:|---:|---:|---:|
| 低 | 4 | 8 | 1 | 2 ms | 1 |
| 中 | 8 | 16 | 1 | 3 ms | 2 |
| 高 | 16 | 32 | 2 | 4 ms | 4 |
| 超高 | 24 | 48 | 2 | 6 ms | 4 |

线程上限还受硬件约束。Windows x64、单处理器组且拓扑可读时，保留调用游戏线程所在物理核及 SMT 兄弟核，工作线程排除这些核、使用 BelowNormal 优先级；最后一个并发决策结束后恢复调用线程亲和性。不修改进程整体、音频或渲染线程亲和性。不支持的拓扑使用操作系统调度，不能保证核隔离。主机档位通过可靠房间消息同步，避免各客户端使用不同精度；尚未做多进程实机验证。

### S1 原生次序对照

| 接缝 | 原生行为 | 当前实现及边界 |
|---|---|---|
| `CardReward.OnSelect` / `NCardRewardSelectionScreen.OptionSelected` | 创建屏幕并 await 选择，然后同步结果、加牌、执行奖励钩子 | 保留原生屏幕与 completion source；异步评分完成后回答同一等待。人类先选则取消计算。未复制加牌/钩子次序。无屏幕测试选择器接口仍是同步回退。 |
| `PlayerChoiceSynchronizer.WaitForRemoteChoice` | await 远端选择 | 合成 Bot 的奖励等待异步求值；其它旧分支保持原有行为。 |
| `RestSiteCmd` / `SmithRestSiteOption` | 选择休息选项，再从合法牌中选择升级并执行 | 营火投资排序、选项收益、升级目标均用异步估值，原生升级执行不变。 |
| `CardSelectCmd.FromDeckFor*` / `RelicSelectCmd` | 异步选择，调用方执行效果 | 局外异步估值；战斗内保留原有路径。原生可升级/可移除/可变换筛选保留。 |
| 商店 / 事件驱动 | MOD 原有评分后提交原生购买/事件动作 | 轮询未完成 Task，不阻塞等待；库存或事件状态变化取消旧结果，完整结果才进入原有提交路径。 |

这是用户要求的构筑调度偏离，已登记 AGENT.md §6。不改变费用风险、鲁棒性 λ、成长权重和金币换算。高档保持原有精度；低档可改变选择。

### 验证与计时

`ConstructionParallelScenarios` 检查同步/异步完整分数、主线程原生读取、状态变化重算、取消、四档预算、消息认证/序列化、真实工作线程并发及亲和性恢复。十组生产故障注入日志位于 `work/reward-parallel/r2-*.log`，每组故障退出 1，原文记在测试注释。能力牌快照回归还覆盖 Footwork 升级，防止泛型类型识别失效。

最终计时原始数据：`work/reward-parallel/bench-verified.json`；25 张 Silent 卡组，分别执行营火升级、事件升级三张、事件删两张，各档同步与异步结果必须相同。它是测试 SynchronizationContext 回调耗时，不是 Godot 帧率。旧 `bench-final.json` 在能力牌快照修正前生成，不再作为交付证据。

高档结果（同一 DLL，同步/异步选择一致）：

| 场景 | 同步阻塞 | 异步总耗时 | 最长主线程回调 |
|---|---:|---:|---:|
| 营火升级一张 | 8.114 s | 1.344 s | 5.595 ms |
| 事件升级三张 | 25.105 s | 4.094 s | 15.953 ms |
| 事件删两张 | 15.657 s | 2.632 s | 11.610 ms |

命令 `work/dotnet-win/dotnet.exe work/reward-parallel/final-bench-bin/PatchSmoke.dll --construction-parallel-bench=work/reward-parallel/bench-verified.json`，退出 0。12 组中最长主线程回调 34.495 ms，出现在首次 Low 营火调用；同步基线已先预热评分代码，所以也不能视作完全冷启动上界。

软预算只限制继续加入快照，单次原生分析、首次 JIT 和最终候选遍历可超时；不能声称硬 2–6 ms 上限或零卡顿。未部署到运行中的游戏，未验证实机动画与多人消息全链路。

## 14. 生存者删牌诊断（2026-10-01）

实机日志 `work/reward-parallel/survivor-godot.log:26380` 记录 `item=remove cost=100 ... removed=SURVIVOR+0`，当时还有四张基础防御。

用同一商店之后的存档补回生存者，当前安装 DLL（16047C…）复算：删生存者 11.298594，删防御 9.676938，能复现错误排序。队友已有后续购买，且离线加载移除了 SeaGlass/ArchaicTooth 的 UI 序列化属性，因此这是近似现场，不能声称完整时点回放。原存档未修改。

`ConstructionHand` 已建模生存者的 8 格挡、实际弃牌目标、丢弃凋萎避免回合末伤害、奇巧自动打出以及空手时无需弃牌。弃普通可用牌有机会成本，弃牌不是无条件正收益。

保持搜索宽度 32，仅增加采样：

| 样本数 | 删生存者 | 删防御 |
|---|---:|---:|
| 16 | 11.298594 | 9.676938 |
| 64 | 9.062827 | 17.677198 |
| 128 | 9.543700 | 15.726186 |

证据为 `survivor-old-audit.log` 和 `survivor-sampling.log`。16 样本中删生存者普通战项 −2.64、鲁棒性项却 +1.04；删防御普通战 −1.04、鲁棒性 −2.40。身份哈希产生的有限手牌样本与差分干扰损失足以翻转排序。更高采样支持保留生存者，但单例不能证明全局无偏。

本次诊断没有给生存者卡名保护，也没有增加固定弃牌奖励。后续应在同一预算下改善候选之间的配对采样，并给接近排序提供有上限的复核；必须同时回归 Prepared/Anticipate、Bully/Bludgeon 和基础牌移除锚点。底层评分仍保留这一已知排序风险；用户随后指定的商店筛选规则已规避本例，见 §15，不能将其宣称为全局采样问题已修复。

## 15. 商店删牌调用裁剪（用户 2026-10-01 补充）

1. 删牌服务无库存、价格非法或当前买不起：不执行删牌评分。
2. 服务可购买且有可移除诅咒：直接选择删除第一张合法诅咒，不执行卡组强度算法，也不扫描其它商品来竞争这次操作。保持原生不可删除与藏宝图保护。
3. 其余情况下，只要存在未升级基础打击/防御，删除候选就限制在这些牌；依然以完整卡组为上下文，用统一强度函数比较删除收益。
4. 没有这些基础牌时，恢复全卡组合法候选。连购的假想卡组也使用相同筛选；会员卡对删牌的估价不再扫描每一张牌。

这是用户明确授权的候选和优先级政策。没有设置生存者卡名黑名单；有未升级防御时，生存者不会进入商店删除候选。已升级基础牌也不会混入这一步。

> **2026-10-03 更新（用户要求全入口统一）**：这套候选和优先级已从商店扩到所有删牌入口。`RemovalPlan.PriorityPool/PriorityTier` 成为唯一实现，商店购买、`FromDeckForRemoval` 选牌（事件 / 营火 Cook / Neow / 先古）和事件选项定价共用；保护角色最后一张的兜底保持不变。第 2 条“商店购买不扫描”的短路仍留在商店购买路径（它决定是否买，不决定删谁），其它入口按同一优先级逐张评分。原句“其它场景的通用强度函数不变”由此作废。

`ShopShortcutScenarios` 在诅咒和无法购买的分支对 `DeckStrength.Evaluate` 注入抛错探针，证明根本没有调用；另记录实际删除估值入口，验证只评分未升级打击/防御。六组生产故障的退出码与原文保存在 `work/reward-parallel/shortcut-r2.json`。

### 旧六商店测试为何达标：决策审计

用户要求客观列举后，用已安装基线 16047C… 重跑：`work/dotnet-win/dotnet.exe work/reward-parallel/old-shop-audit/PatchSmoke.dll --shop-removal-audit`，退出 0。完整目标、卡组、候选与分解保存在 `work/reward-parallel/old-shop-decisions.json`。旧完整频率回归退出 0，次数为 3/3/1/3；新硬过滤为 2/0/1/1，频率回归退出 1，未放宽断言。

| 席位/商店序号 | 实际删除 | 金价 | 删除得分 | 同时删打击得分 | 关键分解 |
|---|---|---:|---:|---:|---|
| Silent 1 / 3 | DEFLECT | 150 | 10.617 | 9.874 | 社区先验贡献 8.30；移除该项后 2.317，低于删打击移除先验后的 3.874。 |
| Silent 2 / 1 | ANTICIPATE | 100 | 7.221 | 4.963 | 先验 +12；其余合计 −4.779。不是实际场景显示了明确改善。 |
| Ironclad 2 / 1 | BLUDGEON | 100 | 43.703 | 14.552 | 费用风险解除 +31、先验 +9.62，合计占总收益约 93%；3 能量拥挤确有删牌理由，但 654 金愿付估值主要来自启发式。 |
| Ironclad 2 / 2 | CINDER | 125 | 24.846 | 14.629 | 先验 +12、费用风险 +6.12；2 费 18 伤随机消耗手牌既有代价也有清理收益，不能只凭总分认定胜过删打击。 |
| Ironclad 2 / 3 | SPITE | 150 | 11.132 | 5.715 | 先验 +12；其余 −0.868；普通战 −1.69。仍有 5 打击、3 防御及 Inflame；0 费攻击及受伤后的连击与力量有配合，优先删除很可疑。 |

该次删除 SPITE 时，删打击的 Wither/Chains 绝对场景分分别改善 +2.58/+12.92，鲁棒性差分却扣 −7.15：干净基线改善更多会扩大相对损失。这说明“抗干扰差值”与“抗干扰绝对生存能力”并不等价；有限采样还会进一步扰动它。

测试局限：这些是旧日志牌组的固定六次商店探针，每次补 150 金、反复提供同一张 60 金牌、没有战斗/选牌/幕次推进。`PlayerFor` 从 act2 标记行取牌组，却用默认测试幕次 0（λ=0.8），并固定 64/80 HP。因此 2–3 次只能是敏感性提示，不应据此宣称实际删除更好，更不能为凑次数去删除别的牌。旧模型还把低社区偏好直接累加为移除收益，需与实际卡组价值分开审视。

截至本次审计，硬过滤代码和定向故障测试已完成；频率锚点未通过，完整普通/内核门禁尚未完成，没有发布或安装新包。用户正在要求先分析政策依据，未将失败断言改成通过。

## 16. −18 先验及折扣商品风险核查（2026-10-01，暂缓打包）

用户指定基础打击/防御先验 −18，代码已在 `DeckStrength.CommunityPrior` 实现，保留社区原始数据；升级/附魔实际收益仍由模型计算，痛击、生存者保持原先验。两组故障注入退出 1，证据 `work/reward-parallel/prior-r2.json`。性能冻结向量仅对这一明确的加法政策变化作精确归一化：每张基础打击/防御将 Total、Prior 加回 12，其余字段逐项保持原断言，768 手牌/22 完整向量检查退出 0。

用户随后要求暂停，核对是否挤掉折扣核心牌。独立诊断入口 `--discount-removal-audit=work/reward-parallel/discount-removal.json` 在同一 DLL 中仅覆盖基础先验，分别取 −6/−18；四席 A10、3 能量、满血、100 金、删牌价 100，库存与卡组均保持一致。命令及原始输出为 `work/reward-parallel/discount-removal.log`，退出 0。

| 卡组与测试商品 | 售价 | −6 选择 | −18 选择 |
|---|---:|---|---|
| 战士起手 / Bully | 37 | 买 Bully | 删防御 |
| 战士起手 / Pommel Strike | 25 | 买 Pommel Strike | 删防御 |
| Silent 飞刀：3 Blade Dance、Cloak and Dagger、Infinite Blades / Accuracy | 38 | 买 Accuracy | 买 Accuracy |
| 同一飞刀卡组 / Afterimage | 75 | 买 Afterimage | 买 Afterimage |
| Silent 弃牌：Prepared、Tactician、Reflex、Acrobatics / Calculated Gamble | 38 | 买 Calculated Gamble | 删打击 |

当前商店将整个删除差分乘投资期限再乘 3.6。此处期限 5.718，先验新增 12 分被放大为约 247.03 金；加普通牌不会改变已有基础牌数量，所以购买收益不变。Pommel Strike 净值 256.12 金，删防御净值从 21.93 变成 268.96；Calculated Gamble 净值 100.91，删打击净值从 35.58 变成 282.61。明显协同收益很高的 Accuracy/Afterimage 仍能胜出，问题并非所有核心牌一律被拒。

现有 Bully 同价商店锚点退出 1（`prior-bully-anchor.log`），六商店原始频率锚点退出 1，次数 5/4/3/5（`prior-anchor.log`）。**保留这两组失败检查，不以放宽次数断言来出包**。−18 当前是未验收参数，尚未打包或安装。下一步需统一处理先验与可重复战斗收益的金币折算，再验证全部购买/删除/升级锚点，而不是给单张核心牌加购买保护。

## 17. A10 商店涨价修正（2026-10-01）

原生 MerchantCardRemovalEntry 的 Inflation 分支为 BaseCost=100、PriceIncrease=50，即 100/150/200/250…；此前六商店 Run/Audit 夹具误写 +25，现已同时修正。生产 BotShopPlanner 使用 MerchantEntry.Cost，没有这个硬编码错误。前文以及 prior15-shops.log 的 5/4/3/5 不再作为 A10 结论；单次首次删牌 100 金的 Accuracy/Pommel 比较不受影响。

构建命令：work/dotnet-win/dotnet.exe build tests/PatchSmoke/PatchSmoke.csproj -c Release -p:STS2DataDir=<game-data> -p:NuGetAudit=false，退出 0（work/reward-parallel/a10-price-build.log）。运行 work/dotnet-win/dotnet.exe tests/PatchSmoke/bin/Release/net9.0/PatchSmoke.dll --shop-prior-audit=-15，退出 1；原文：System.Exception: Shop removal anchor: six-shop cohort needs mostly 2-3 removals, mean 2-3 and no zero-removal seat: 3,4,3,3。

正确价格下 −15 四席删除 3/4/3/3 次，买牌 1/2/3/1 次，平均删 3.25 次。原断言保留，因平均超过 3 仍失败；不能沿用旧价格下“平均 4.25 次、明显过度删牌”的判断。证据 work/reward-parallel/prior15-shops-a10-price.json 和 .log。正式先验仍 −18，打包暂停，未声称完整门禁通过。

## 18. 用户指定 −15 实验包（2026-10-01）

用户在核对 A10 正确价格、得知 −15 的六商店删除 3/4/3/3 后，明确要求使用该参数打包。本轮生产只将基础打击/防御先验改为 −15，包含前述后台构筑优化及候选短路，不安装或上传工坊。

测试的两处旧政策依赖已改为验证实际语义：光暗门按真实升级对象的差分比较选项（不能强制两张基础牌升级永远胜过删牌）；空卡组承伤检查只比较 Combat 且必须为负，避免经验先验污染承伤断言。三项故障注入（−15 回退 −18、空卡组 Combat 清零、光暗门反向选择）均退出 1，原文与命令见 `work/reward-parallel/prior15-r2/`；恢复后分别运行 `--shop-shortcuts-only`、`--unified-deck-only`、`--tablet-only`。

原 Bully 购买锚点、六商店频率锚点保持原断言；主套件现在汇总它们的错误后继续检查其他场景，最后仍退出 1。标准门禁保留失败状态，实验包不代表通过验收。常规 build.ps1 的失败即中止规则没有修改。本次根据用户对已知偏差参数的明确测试包指令，归档同一份经过检查的二进制并附完整失败说明，不覆盖正式包。

普通验证：`pwsh -NoProfile -File scripts/verify-project.ps1`，`work/verification/20261001T055317363Z/`；构建退出 0（6 条基线警告）、类型加载退出 0、普通套件 277 PASS / 退出 1（仅两组上述校准锚点）。脚本按设计跳过内核，因此内核另以 `-p:EnableKernelTests=true` 编译并运行完整入口，使用同一验证目录的 CoopBots.dll，日志为 `work/reward-parallel/prior15-kernel.log`，最终结果随包记录。

最终内核完整入口：438 PASS，退出 1；汇总与普通套件相同的两组校准失败，没有其它失败。恢复后的三个定向检查退出 0。最终生产 DLL 与四门验证目录、内核运行目录、故障恢复构建目录 SHA256 完全一致：CoopBots.dll `8DA1B37257D4F256DE842B7D2C6DA1EA6D0A8208B48DF70B2BDAE3AAC903FEE8`，CoopBots.Kernel.dll `9364E6063D3BC1D36221765FE8981BB9A124B80E22F6C82E38F44DEE58C9CEA1`。

用户指定实验包：`outputs/CoopBots-v0.39.1-prior15-experimental-20261001T055942Z.zip`，SHA256 `A83DF6FF204E3D3F00A07C20250F1B02D1DD03EB07008DC2410A4260918645F5`。归档脚本 `work/reward-parallel/package-prior15.py` 检查两套确已跑到末尾、仅两组既知校准失败及三份 DLL 一致，保留退出 1 与常规门禁 failed。ZIP CRC 与两份 DLL 哈希核验通过；包含 TESTING.txt、失败日志与证据 JSON。没有部署、安装或更新工坊。

## 19. prior15-room-wait：异步房间退出保护（2026-10-01）

实机 prior15 在营火 (3,9) 尚有 Bot 未完成时，14:05:58 已集齐下一房间 (3,10) 的地图票。原生 RunManager.ExitCurrentRoom 先 PopCurrentRoom，再 await RestSiteRoom.Exit；后者 await 全部席位的 completionTask。后台构筑检测房间已改变后取消，末席停止选择，导致退出永久等待。证据 `work/prior15-live-stall/godot-snapshot.log:9732`，已安装 DLL 与旧实验包一致。

本轮用户明确要求所有下一房间的 Bot 投票等当前房间完成，覆盖营火、商店、事件。新增 RoomWork 房间身份计数：ConstructionDecision 在任何 await 之前登记、正常/取消/异常退出均释放；营火同时登记整个 entry→排序→逐席选择与原生动画的任务，覆盖单次计算之间的空隙。RoomWork.Waiting 还检查驱动席位的营火选项、商店 Finished（保留已有故障中止语义）、事件 IsFinished 和原生 _pendingOptionTasks，以及奖励界面完成状态。旧房间任务不阻塞新房间。

三个入口共同调用 Waiting：BotRuntime.TryVoteOnMap、BotRoomProceedDriver.TryProceed、BotEventProceedDriver.TryProceed。持有任务时仅返回等待，绝不同步等待工作线程，日志按既有节流说明原因。不拦截人类输入，不自动退出当前游戏，不热替换 DLL。等待尚未完成的 Bot 票足以阻止队伍提前进入下个房间。

新增 RoomWorkScenarios 使用真实 RunState、RestSiteSynchronizer 与 EventSynchronizer、受控异步完成源验证：营火 entry 和 options 已清空但操作还没结束的间隙；嵌套释放；构筑完成和取消释放；旧房间隔离；未结算商店；事件 IsFinished=true 后仍有异步效果；所有三处入口均实际引用共享保护。`--room-work-only` 恢复退出 0。九组故障注入命令、退出 1 与失败原文在 `work/prior15-live-stall/r2/`，不放宽旧校准失败。

普通完整验证目录：`work/verification/20261001T061735984Z/`。含内核完整入口使用该目录同一份 CoopBots.dll，独立输出到 `work/prior15-live-stall/kernel/`、日志 `kernel.log`。最终包与结果待验证结束记录。

商店补充：FaultedIn(state) 仅认可当前 MerchantRoom 的故障停止状态，旧店失败不能让新店提前放行；第十组故障注入证据 `r2-shop/shop-identity.log`，退出 1，恢复后 `room-work-final.log` 退出 0。

最终源版本验证目录为 `work/verification/20261001T062130887Z/`，内核完整入口为 `work/prior15-live-stall/kernel-final/PatchSmoke.dll`，两者使用完全相同的生产 DLL（SHA256 A44000A28760D96B47D54717924CB62065655B3F8526395311A76C5761882A87）。早期 061735984Z 为商店身份补充前版本；062040748Z 因上一测试进程占用输出 DLL 导致测试构建失败，未用于打包验收。常规门禁仍保留校准失败状态。

最终含内核完整套件 439 PASS / 退出 1；普通 278 PASS / 退出 1，两套仅原 Bully 购买、六商店频率两组已知校准失败。新房间等待测试通过，10 组故障注入均退出 1，恢复退出 0。实验补丁包 `outputs/CoopBots-v0.39.1-prior15-room-wait-20261001T062602Z.zip`，ZIP SHA256 `3CF7083F69C027233A90AE94DEDDF1719323E4276C65DC2DBD3E030FE5A697B2`。`work/prior15-live-stall/package.py` 核对完整日志、同一生产 DLL、ZIP CRC 与包内两份 DLL 哈希，退出 0。未安装到正在运行的游戏，未进行跨客户端实机测试。

## 20. 锚点口径更新（2026-10-01，用户明确指示）

六轮商店平均删除次数允许 2–3.5；移除“起手商店必须先买 75 金 Bully”的强制购买断言。保留 Bully 与 Bludgeon 同价排序、无 Bully 时删基础牌优于购买 Bludgeon，以及原生易伤/升级机制检查。生产 −15 参数和决策代码未因此修改，旧 ZIP 没有重打或覆盖。

使用隔离输出 `work/prior15-live-stall/relaxed-anchors-bin/` 构建退出 0（原默认目录被另一测试进程占用）。定向 `--shop-removal-anchor` 退出 0：3/4/3/3，均值 3.25；完整 `--bully-price-anchor` 退出 0。日志 `anchor-relaxed-shops.log`、`anchor-relaxed-bully.log`。这是两项定向通过，不代表重新运行了全部门禁；旧包中的失败报告仍是当时断言口径的真实记录。

## 21. 变牌事件目标修复（2026-10-01）

审计本机 v0.111.0 的 7 个原生变牌事件：WoodCarvings 的 Bird/Torus 使用 FromDeckGeneric，过去未识别 TO_TRANSFORM 提示，回退选贡献最高的牌，因而可能选中强化痛击。AromaOfChaos、EndlessConveyor、MorphicGrove、Symbiote、Trial、WhisperingHollow 均调用 FromDeckForTransformation，没有同一入口漏洞；它们仍按统一删除收益估计随机变牌目标，不预知随机结果。

通用接口现在识别原生变牌提示；在异步计算之前捕获木雕分支，指定 Peck/ToricToughness。固定变牌用整副卡组替换前后的强度差评估，并与木雕选项评分共用。预览使用未强化的新牌，符合原生 TransformTo，不继承被替换牌的强化，不消耗 RNG，不修改实际卡组。保留原生可选范围。

TransformEventScenarios 通过原生 Bird/Torus 异步方法测试实际选择，并审计另外六个事件的原生调用。四人 A10、4 打击/4 防御/强化痛击夹具：PECK 选择 STRIKE_IRONCLAD，delta=17.451；TORIC_TOUGHNESS 选择 DEFEND_IRONCLAD，delta=7.645；均保留强化痛击。定向命令 dotnet run --project tests/PatchSmoke/PatchSmoke.csproj -c Release -- --transform-events-only（本机 STS2DataDir 与隔离 OutputPath 参数见日志目录），退出 0。证据：work/transform-bash-audit/transform-tests.log。

R2：禁用提示分类、禁用原生木雕调用栈识别、给替换收益错误加 10，三次均退出 1，失败原文已写入测试注释；日志 mutant-prompt.log、mutant-caller.log、mutant-delta.log。全部故障已恢复。该验证为真实游戏方法的离线夹具，尚未进行游戏内复跑。

最终四项证据汇总：work/verification/20261001T084449432743Z/summary.json。构建与 TypeLoadCheck 复用 083447313Z 的退出 0；同份生产 DLL 的完整普通、完整内核分别使用隔离 OutputPath 重跑，退出 0，283 / 444 PASS。生产 DLL SHA256 D62CAC5F999007176EAAC254C418529745AA4663FBC0E2C7420BE7C0BCB5E2C0；两套加载副本已核对相同。首次默认目录普通套件退出 1 且无异常堆栈，原因未定，其原始失败 summary 原样保留。汇总工具名明确为 isolated-full-suite-recheck，没有把首次失败改成成功。state.py gate/log 已记录。未打包，未安装。
