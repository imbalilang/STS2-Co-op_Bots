# Vendor/ 的有意偏离

> **这是两张偏离表里的"源码层"那一张。** 另一张是 [`AGENT.md`](../../AGENT.md) §6
> 「偏离登记表」，登记的是**运行期行为**偏离。二分标准与关键区别写在那里，登记前先读。
>
> 本表的关键约束：**`Vendor/**` 的每一项偏离都必须由导入管线本身产生** ——
> 手改再补登记是不够的，重导入会静默覆盖。

`Vendor/**` 是从 CombatSolver 导入的上游源码。**当前来源是 0.41.0**(`UPSTREAM.json` 记录来源),
由 `scripts/import-combatsolver-kernel-41.py` 从 `work/combatsolver-sync-latest/src`
(该目录是 CombatSolver 仓库 `0e6cc2d release: prepare 0.41.0` 的检出) 生成。

## 先纠正一个常见误读:`upstream_sha256` 不是完整性校验

导入器会重写命名空间:

```python
adapted = text.replace('CombatSolver.', 'CoopBots.Kernel.Vendor.').replace(
    'namespace CombatSolver;', 'namespace CoopBots.Kernel.Vendor;')
```

所以 `UPSTREAM.json` 里那个字段记的是**来源**(这批代码取自上游哪个文件),不是"这个文件没被动过"。

真正的风险是下面这条。

## 真正的风险:重导入会覆盖改动

重跑导入器会从上游重新生成 `Vendor/**`,**覆盖**这里的任何手工编辑。
因此:凡是没有写进导入器(内联钩子)或偏移表(P1 表)的改动,重导入后必然静默消失 ——
表现为"搜索行为突然变了",而没有任何编译错误能解释它。

**所以每一项偏离都必须在下面登记,并且必须由导入管线本身产生。**

## 管线:三层,各自可重放

| 层 | 位置 | 作用 |
|---|---|---|
| 内联钩子 | `scripts/import-combatsolver-kernel-41.py`(函数 `add_*`) | 逐条锚定的小改动;锚点漂移即 `raise`,不会静默丢失 |
| P1 偏移表 | `scripts/kernel-p1-adaptations-41.json` | 按位置记录的多人适配(全体目标、每玩家虚数等) |
| 重锚工具 | `scripts/reanchor-p1-adaptations-41.py` | 把**唯一的作者版**表(0.33.9 的 `kernel-p1-adaptations.json`)重锚到新快照,生成上一行那张表 |

重锚工具的两条不变量,各自都对应一次已经发生过的错误:

1. **顺序是真正的锚点,偏移不是。** 按升序在"上一处结尾之后"搜索,短片段(如 `        };\n`)
   才不会撞到后文里的同名片段。
2. **偏移写在原始文本坐标系里。** 导入器按偏移**倒序**作用于未打补丁的文本,所以每个偏移
   必须在原始文本上成立。曾经按"边打边算"的坐标写过一次,结果把一句局部变量声明插进了参数表中间。

`old == ""` 的纯插入没有可搜索的文本,以它原位置**前一行**为锚。

`base_sha256` 取的是**上游文本**(命名空间重写后、任何 CoopBots 钩子之前)的哈希 ——
否则记录值会依赖我们自己的补丁,重锚工具永远算不出来。

## 偏离清单

### D1 — 多人适配整体移植到 0.41(2026-09-19)

0.41 不只是加常量:它把若干机制换了形状,所以旧补丁不能原样套用。逐条核对后:

**保留**(重锚后照旧生效):`MonsterMoveEffects` 的全体目标(69 处)、`PlayerTurnEndLifecycle` /
`TurnStartRelicSupport` / `ReactiveRelics` / `RelicTurnStart` 的 `etherealByOwner`、
`EndTurn.cs` 的 `participants` / `beforeHandEffects`、`CardChoiceSupport` 的 `displayNames` 可空、
`MonsterMoveSemantics` 改 `partial`。

**改锚(`RETARGET`)**:
- `EndTurn.cs` 的 `playersEndingTurn`:0.33.9 从 `CombatManager.Instance.PlayersTakingExtraTurn`
  取队伍,0.41 直接读 `State.CombatState.Players`。改成
  `participants ?? State.CombatState.Players`,多人补丁仍然要点名"这一回合结束的是谁"。
- `CardEffectSpecRegistry` 的 `case Adrenaline:`:0.41 把 Adrenaline 移到了
  `CardDrawCardMirrors.AdrenalineOnPlay`,那个 case 不复存在。`ENERGY_SURGE` / `BELIEVE_IN_YOU`
  (给队友/目标补能量)改成挂在 `switch (card)` 的第一个 case 前。

**丢弃(0.41 已自己做掉,或改补丁会与上游打架)**:
- `SimulatedCombatState.ApplyTargeted` 的 Instanced Power 插入 —— 0.41 自己按
  `PowerInstanceType` 分支并绕开 `(owner,type)` 合并缓存。
- `EndTurnPowerSupport` / `CorePowerSupport` 的 `etherealByOwner` 管道 —— 0.41 把 JossPaper 的
  虚数逻辑搬到了 `SimulatedCombatState.ReactiveRelics`(我们的补丁已在那里接住),
  `TriggerRegular` 不再读这个计数,那条管道没有下游了。
- `PlayerTurnEndLifecycle` → `CorePowerSupport` 的那一路转发(同一原因;另一路去
  `TurnStartRelicSupport` 的转发保留,它才是 JossPaper 的路径)。

判定依据是**读 0.41 的对应实现**,不是锚点匹配失败。

另:0.41 删掉了 0.33.9 的 history-sensitive 抑制窗口
(`SuppressHistorySensitiveCardModifiers` / `BeginHistorySensitiveCardModifierScope`),
因为它把"本回合已出的牌"折算进了 PhantomBlades / Lethality / Unmovable 三个镜像本身
(`HandleLethalityPower` 减去正在打出的这张、`HandleUnmovablePower` 减去
`GetPoweredBlockEvents(CardPlay)`)。所以 `KernelSession.Play` 不再需要那圈
`try/finally` 包裹 —— 删掉即可,多人语义没有丢失。

### D2 — 从 `Snapshot` 抽出派生量为可调用入口(2026-09-19,仍在)

**动机**:BFWS novelty search 的判据是 `CaptureNoveltyFacts` 读的一批派生量
(`ReachableHandValue` / `StrategicEffects` 五维 / `ReplayPotentialValue` / …)。
这些值上游已经算好,但算在 `CombatBeamSolver.StateEvaluation.cs` 的**私有实例方法**
`Snapshot(...)` 里,没有独立入口。

**改动**:把该方法的若干计算段抽成 `internal static`,参数化其依赖;原处改为调用。
不改变任何计算,只改变可达性。

### D3 — 打分走反射桥,不走 0.41 的装配(仍在)

`CombatSolverEvaluator` 通过反射调用 vendored `CombatBeamSolver` 的私有
`Snapshot(...)`,按席位求和。**不能**改用 `work/combatsolver-sync-latest` 自己构建出的
`CombatSolver.dll`:两个程序集里的 `CombatPredictionSimulator` 是**同名不同类型**,
引擎只认自己那一个,把我们的联合状态递进去会抛 —— 于是分数静默退回降级评估器,
比不接更糟。`CombatSolver41.cs` 是"0.41 端到端能跑"的探针,但它的展开只走一个玩家的手牌。

### D4 — `ContinuationStamp.CaptureLive(CombatState, Player?)`(仍在)

0.41 只有单参数版本,永远盖本地玩家的戳。四人计划要从每个席位自己的视角校验,所以
视角必须可选。由导入器内联钩子施加。

## 当前基线(2026-09-19,0.41.0 引擎)

构建(三个项目都 0 错误):

```
work/dotnet-win/dotnet.exe build src/CoopBots/CoopBots.csproj -c Release \
  "-p:STS2DataDir=<游戏 data 目录>" "-p:OutputPath=<临时目录>" -p:NuGetAudit=false
```

- `PatchSmoke`(默认与 `-p:EnableKernelTests=true` 两种):**当时读数为 PASS 50**,唯一失败
  `LastStandScenarios.cs:36` 的 Beacon 断言
- 该失败**先于** 0.41 导入存在,由同日 `EnemyHpWeight 0.25→0.5` 引入,与本次导入无关
- `TypeLoadCheck` exit 0(`CoopBots.dll enumerates 390 types with CoopBots.Kernel unresolvable`)

> ⚠ **"PASS 50" 是 2026-09-19 当天的读数,不是当前门槛。**
> 当前判据见 [`AGENT.md`](../../AGENT.md) §3 的表,读数只认 `work/verification/` 的最近一次运行
> (`python scripts/state.py show`)。不要拿这里的数字去比对现在的套件。

**导入方法本身不变**(这条才是本节要守的):重导入前后 **PASS 数必须完全相同,且失败集合不变**。
任何差异都说明导入改变了行为 —— 用**同一次运行前后的两个数**比,不要用历史数字比。

## 可证伪的版本标记

`KernelTeamSearch.EngineMarker` 打印在 `search-complete:` 日志行的 `engine=` 字段。
它的两个数字取自 vendored `SolverWeights.MaximumNoVictoryEscalations` /
`DeathSavePremiumPercent` —— 这两个成员只在 0.41.0 存在。所以:回退引擎会**编译失败**,
而旧 DLL 根本打不出这个字段。日志里有 `engine=0.41.0(nve=2,dsp=900)`,才是 0.41 真的在跑。
