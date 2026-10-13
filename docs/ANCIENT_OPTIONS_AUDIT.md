# 远古之民事件选项审计（2026-10-01）

## 范围与结论

按当前安装的游戏 v0.111.0 原生 `AncientEventModel.AllPossibleOptions` 枚举：
8 位远古之民，102 种选项机制，106 个配置变体（Sea Glass 有 5 个角色配置）。
`DeprecatedAncientEvent` 没有可选项；Neow 自定义模式的单选修饰器页面仅推进流程。
共同的入场治疗不计入某个选项的独有收益。

决策入口是 `BotEventDriver.ChooseEventOption`。原生选项通过 `option.Relic` 携带配置与执行效果，
因此读取其 `AfterObtained` 和后续钩子；这里评估的是完整事件交易，未改动宝箱分配或商店遗物排序。
所有估值均为只读预览，真实事件继续由游戏执行。

## 已有模型审计

此前没有远古专用选项处理器，统一经过关键词基准分与 `EventTrade.Grants`。
这条路径有如下问题：

| 原有路径 | 审计结果 | 本次处理 |
|---|---|---|
| `HoverTips` 中的卡牌／遗物加分 | 提示可能只解释机制；`HoverTipsExcludingRelic` 不包含选项遗物自身。Darv 的蛇眼、贤者之石等会同为 5 分 | 按原生选项配置评估完整效果 |
| `RelicPickupPrediction` 克隆 RNG 预测 | 预览基础设施可复用，真实 RNG 不应被消耗；部分预览受模式／设置限制 | 保留可用预览，缺失时明确采用期望值 |
| Hefty Tablet、Lead Paperweight、Arcane Scroll | 备选牌可能被逐张相加；强制获得与可跳过选择未区分 | 强制随机稀有牌计实际加入；单选计最佳可选增量；Hefty 另扣 Injury |
| Scroll Boxes | 原路径把一组牌当成“取最好一张” | 比较整组加入后的卡组变化，再在组间选择 |
| Glass Eye、Kaleidoscope | 多轮奖励与每轮只取一张容易混计 | 每轮最佳增量求和；未来重复取牌仍为近似 |
| Sea Glass | 按奖励组只计一张，不对应任意数量选择 | 对实际 15 张报价执行与选牌相同的逐张增量策略；保留指定角色及三种稀有度 |
| Sand Castle 等升级 | 升级后的牌被当作额外加入，没扣原牌 | 计算升级差值；随机升级与自主选择升级分开 |
| New Leaf、Astrolabe、Pandora、Leafy Poultice | 变牌奖励可能当作免费牌；数量、升级及血量代价不完整 | 原牌被替换后的整副卡组差值，计强制数量与血量损失；使用原生原牌卡池，诅咒仍变诅咒 |
| Neow's Bones、Calling Bell、Large Capsule、Toy Box | 随机遗物可预测，但其附带代价、临时期限或强制塞牌不完整 | 包含嵌套获得效果、额外诅咒／基础牌和蜡制时限 |
| Pael's Horn、Distinguished Cape、Sere Talon | 按卡牌 ID 去重会丢失副本数 | 按整批牌加入计稀释，另扣诅咒／最大血量 |
| Dowsing Rod | 任务牌的战斗表现不能代表后续收益 | 任务完成概率 × Abundance 收益，扣等待期抽牌占用 |
| 持续能量、抽牌、敌方增益、地图效果 | 无直接发放物的钩子大多未进入事件分数 | 逐选项建模，按剩余战斗、房间和当前卡组估计 |
| Claws 后续选牌 | 原生允许 0..6，旧入口直接取最少的 0 张 | 顺序比较 Maul 替换，收益不再为正时停止，最多 6 张 |
| Instinct、Imbued、Clone 后续选牌 | 普通牌分数不代表附魔带来的增量 | 事件估值与实际选牌共用附魔增量 |

## 逐选项检查清单

“估计”指将原生机制换算为当前局面的效用，不意味着已重放整场或整局。
持续收益采用有限回合与剩余战斗窗口；奖励未知时采用确定性样本，避免改变游戏 RNG。

### Neow（30）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| ArcaneScroll | 强制加入随机稀有牌；计入稀释，允许负收益 |
| BoomingConch | 仅精英首回合能量与抽牌；按剩余精英占比折算 |
| CursedPearl | 333 金及 Greed 诅咒；金币按剩余购买能力折算 |
| DowsingRod | 5 个未知房间任务、完成收益、等待期间死牌 |
| FishingRod | 每 3 场普通战斗一次随机升级；数量受未升级牌限制 |
| GoldenPearl | 150 金；Ectoplasm 禁止所得时为零 |
| HeftyTablet | 稀有牌单选及必得 Injury；跳过奖励不免除代价 |
| Kaleidoscope | 两轮其他职业奖励，每轮单选 |
| LargeCapsule | 两遗物及强制多一张基础打击、防御 |
| LavaRock | 第一幕 Boss 后获得两遗物，包含等待折扣 |
| LeadPaperweight | 两张无色牌选一张 |
| LeafyPoultice | 第一张基础打击、防御转换；减最大 HP 及必要的当前 HP 截断 |
| LostCoffer | 一轮卡牌奖励及一瓶药水；检查空槽／Sozu |
| MassiveScroll | 多人牌候选单选 |
| NeowsBones | 两个合法 Neow 效果及它们自身代价，额外随机诅咒 |
| NeowsSacrifice | Ambergris 储备价值及 Guilty；检查药水容量 |
| NeowsTalisman | 最后一张基础打击、防御升级 |
| NeowsTorment | Neow's Fury 加入后的卡组差值 |
| NewLeaf | 自选一张转换；扣除被替换牌 |
| NutritiousOyster | 增加最大 HP 与同步治疗 |
| PhialHolster | 一药水槽和两药水；溢出不计 |
| Pomander | 自选升级增量 |
| PrecariousShears | 两次强制删牌及 16 伤害；致死代价否决 |
| PreciseScissors | 一次强制删牌；不能只算正收益 |
| ScrollBoxes | 整组三张牌的联合变化，比较两组 |
| SilkenTress | 下一次奖励首次打出重放的收益，失去全部当前金币 |
| SilverCrucible | 前三次升级奖励增量，失去第一个宝箱 |
| SmallCapsule | 一件随机遗物奖励 |
| StoneHumidifier | 只有选择休息才增加最大 HP；按伤势估计实际使用次数 |
| WingedBoots | 三次换路线的期望收益；多人不赋予单人地图功能价值 |

### Darv（12）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| Astrolabe | 三张替换成升级牌；牌数保持不变 |
| BlackStar | 后续精英额外遗物，按地图和剩余幕数估计 |
| CallingBell | 三件遗物，额外不可移除诅咒 |
| DustyTome | 读取已配置的职业远古牌并升级；无配置的目录预览使用合法卡池期望 |
| Ectoplasm | 当前卡组能量需求与未来金币收入损失；保留现有金币 |
| EmptyCage | 顺序删两张，第二次看到第一次删除后的卡组 |
| PandorasBox | 替换全部可移除基础打击／防御；没有目标时收益为零 |
| PhilosophersStone | 持有者额外能量，敌方力量造成的全队代价 |
| RunicPyramid | 留牌选择收益，以及死牌／出牌不足造成的堵手 |
| SneckoEye | 配对抽取 5／7 张手牌，随机 0..3 费用，能量约束下比较可打出价值；扣低尾风险 |
| Sozu | 额外能量与未来药水损失；不把已持有药水算作失去 |
| VelvetChoker | 额外能量与 6 张上限；生成小刀等额外出牌计入需求 |

### Nonupeipe（10）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| BeautifulBracelet | 四张随机可附魔牌获得 Swift 2，按每战首次触发估值 |
| BlessedAntler | 能量与三张 Dazed 的抽牌污染 |
| BrilliantScarf | 第五张手动打出的牌免费；卡组达不到五张时降低收益 |
| DelicateFrond | 每战补空药水槽；Sozu 时为零 |
| DiamondDiadem | 首回合格挡及一回合保留，伤势影响效用 |
| FurCoat | 随机标记战斗跳过的全队收益及剩余场数限制 |
| Glitter | 未来奖励首次出牌重放；按可获得奖励次数折算 |
| JewelryBox | Apotheosis 加入卡组的增量 |
| LoomingFruit | 增加 31 最大 HP 与同步治疗 |
| SignetRing | 888 金；受剩余商店购买空间限制 |

### Orobas（10，14 个变体）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| AlchemicalCoffer | 四个新增槽及填充药水，考虑 Sozu |
| ArchaicTooth | 职业初始牌替换，保留升级／附魔，扣原牌 |
| Driftwood | 奖励重掷的选择收益估计 |
| ElectricShrymp | 自选技能开局免费自动打出，估计目标的费用、格挡、抽牌与自伤 |
| GlassEye | 五轮奖励分别选择；不当作所有候选全部加入 |
| PrismaticGem | 能量及扩大卡池造成的职业协同稀释 |
| RadiantPearl | 每战开局一张保留的 Luminesce，提供一次 2 能量 |
| SandCastle | 六张随机升级，按实际可升级目标计差值 |
| SeaGlass | 指定角色，5 普通＋5 罕见＋5 稀有，任意数量；五种配置独立读取 |
| TouchOfOrobas | 新初始遗物相对被移除旧遗物的增量 |

### Pael（10）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| PaelsBlood | 每回合额外抽牌 |
| PaelsClaw | 所有可附魔 Defend 标签牌的 Goopy；消耗及永久成长，不误用 Cards=3 限制目标数 |
| PaelsEye | 每战一次坏手牌重整／额外回合收益及消耗代价估计 |
| PaelsFlesh | 从第三回合起的能量 |
| PaelsGrowth | 给一张牌 Clone，营火复制一次；扣营火机会成本，Amount=4 不当作复制四张 |
| PaelsHorn | 两张 Relax 同时加入的卡组差值 |
| PaelsLegion | 隔回合首张格挡牌翻倍，依当前格挡供应估计 |
| PaelsTears | 留下未用能量换下回合能量，依出牌需求估计 |
| PaelsTooth | 当前 Bot 选择的五张升级收益，逐战返还的延迟及暂时失去这些牌的影响 |
| PaelsWing | 两次跳过奖励换遗物，按预计可跳过奖励数量计价 |

### Tanx（10）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| Claws | 0..6 张 Maul 替换，保留升级和兼容附魔；选择与估值共用逐次正增量 |
| Crossbow | 每回合随机免费攻击的有限期望 |
| IronClub | 每四次出牌抽牌，依实际出牌供应估计 |
| MeatCleaver | 营火 Cook 的删牌／最大 HP 减休息或升级机会成本 |
| Sai | 每回合 7 格挡，有限战斗长度和利用率 |
| SpikedGauntlets | 能量加成及能力牌加费 |
| TanxsWhistle | Whistle 加入卡组的增量 |
| ThrowingAxe | 第一张牌重放，候选不限于攻击 |
| TriBoomerang | 自选三张 Instinct 双倍伤害附魔 |
| WarHammer | 后续精英后的四次随机升级，按剩余目标／精英限制 |

### Tezcatara（10）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| BiiigHug | 四次删牌，洗牌加入 Soot 的污染 |
| GoldenCompass | 共享地图改写及未知房间仅事件的全队期望 |
| NutritiousSoup | 全部可附魔基础打击变 0 费、Eternal、伤害＋3 |
| PumpkinCandle | 五场能量，营火续燃的机会成本 |
| SealOfGold | 有钱支付时的能量，逐回合金币支出；考虑 Ectoplasm |
| Storybook | Brightest Flame 加入卡组的增量 |
| ToastyMittens | 每回合增长力量，必须消耗手牌的损失 |
| ToyBox | 五个蜡制遗物，每三场失去一个的期限折扣 |
| VeryHotCocoa | 仅开局额外 4 能量 |
| YummyCookie | 四次自选升级，逐次更新卡组 |

### Vakuu（10）

| 选项配置 | 已计入的效果与代价 |
|---|---|
| BloodSoakedRose | 能量与 Enthralled 诅咒 |
| ChoicesParadox | 开局五选一保留牌的选择收益 |
| DistinguishedCape | 三张 Apparition 的卡组变化，加两种诅咒代价 |
| Fiddle | 回合开始多抽两张，失去牌提供的其他抽牌 |
| JeweledMask | 随机能力牌开局免费打出；无能力目标时为零 |
| LordsParasol | 商店免费遗物、药水、删牌及被迫接收商店牌的稀释估计 |
| MusicBox | 首张攻击的虚无复制品需要支付能量；不能当免费重放 |
| PreservedFog | 三次删牌与 Folly |
| SereTalon | 三张 Wish，减 9 最大 HP，只在超过新上限时减少当前 HP |
| WhisperingEarring | 能量加成与开局强制牌序／随机目标风险 |

## 验证和边界

- 新增 `AncientOptionScenarios`：原生枚举覆盖、低费／高费蛇眼、多人与单人代价、强制致死、无目标边界、实际事件入口、选项顺序、选牌入口、异步一致性、牌堆和 RNG 不变。
- `WinningPlanScenarios` 驱动实际剧本采用、药水机会、结束回合发射入口；终局记录是受控夹具，不宣称重放过原来的 Ovicopter 对局。
- 完整内核回归发现 `CombatFeedbackScenarios` 的旧断言要求胜利缓存也因可用攻击而重搜。这与本次用户要求冲突，已将旧场景明确限定为预测败局；胜利场景由新测试独立保护，没有删除败局重搜策略。
- 原生目录测试仅替换本地化和悬浮提示的呈现功能，执行真实选项工厂，避免无 Godot 进程加载贴图。
- 故障注入汇总：`work/ancient-options-20261001/r2-verified.json`，另有最早的两条胜利药水保护日志；命令、退出码及失败原文保留在各 `r2-*.json` / `r2-*.log`。最终四门记录由 `scripts/state.py gate` 指向。
- RNG 只读断言曾因 `SerializableRng` 使用字段、默认 JSON 只序列化属性而漏报。故障注入发现后改成包含字段，并逐选项比较；再次注入真实 RNG 消耗后立即失败。
- 本次没有部署、发布或运行完整爬塔。因此已验证覆盖和决策不变量，未测得新策略的爬塔胜率。
- 猎手弃牌链、特殊复制、稀有组合与未来路线的长期收益依赖现有卡组势函数和显式近似。多轮奖励用每轮增量近似，持续效果采用短期窗口；这些数值需要后续实机日志继续校准。

### 最终门禁证据

命令：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1`，退出码 **0**。
记录：`work/verification/20261001T094004105Z/summary.json`，`status: pass`。

- 构建：0 错误，原有 6 警告。
- TypeLoadCheck：`CoopBots.dll enumerates 700 types with CoopBots.Kernel unresolvable.`
- 普通套件：285 条 PASS，退出码 0。
- 完整内核套件（`EnableKernelTests=true`）：446 条 PASS，退出码 0。
- 定向回归原文：`PASS: verified winning scripts survive emergency/full-belt potion and spare-attack heuristics; losing plans retain rescue spending.`
- 目录回归原文：`PASS: all 102 ancient option mechanisms (106 native variants), costs, deck context, entry routing and read-only async planning.`
- 35 组有效故障注入已恢复并重新通过：汇总中 33 条，加最早两条胜利药水保护日志。覆盖旧行为恢复、漏项、代价、选牌、入口、随机数副作用与异步不一致。

首次完整门禁的旧策略冲突记录保留在 `work/verification/20261001T092950994Z/`。
最终 106 行示例估值及被测 DLL 哈希在 `work/ancient-options-20261001/scores-final.json`；这是测试卡组的估值，不是原爬塔日志的实盘成绩。
