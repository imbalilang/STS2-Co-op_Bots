# AGENT.md — CoopBots 代理运作规范

> **这份文件是唯一权威入口。** 与其它文件冲突时以本文件为准。
>
> **历史文件（不要依据它们行动）**：
> - `CLAUDE.md` —— 四角色派单总线。2026-09-19 起作废（单人全权维护）。
> - `TEAM.md` / `AGENTS.md` / `SOUL.md` —— superharness 时代的跨模型派单协议。当前不适用；
>   其中「未分配任务时不要自行修改 MOD」与本项目现状**直接矛盾**，以本文件为准。
> - 它们保留只为查历史。**不要**在它们里面追加新约定。

---

## 0. 首次接手（读完这一节就能开工）

**不要顺序通读本文件。** 这一节是入口；§1–§7 是按需查的手册。
**状态不要在这里找** —— 用下面两条命令拿。

### 这是什么

《杀戮尖塔 2》联机 Bot MOD。当前主目标（用户 2026-10-09）：**在固定性能预算内，使全 Bot A10 通关率达到 50%，并改善真人与 Bot 协作表现。** 采用分阶段架构重构：保留已验证的原生模拟与多人适配，统一搜索请求、取消和预算，建立完整状态与计划证据合同，并通过完整爬塔对照校准局内、局外收益。整战剧本是全 Bot 模式的一种实现方式；混合桌在真人动作结算后更新计划，不能把执行率当成通关率。路线选择保持现状。
目标、阶段划分与编号 backlog（B1–B11）在 [`PLAN.md`](PLAN.md)。

### 社区作者与 Agent（2026-10-13）

本仓库公开源码开发快照，欢迎作者及其 Agent 直接提出和实现算法改进。项目规范、`PLAN.md`、`.claude/skills/` 与必要测试管线属于可共享内容；用户已明确废弃此前“不上传 Agent 文件”的断言。入门看 [`CONTRIBUTING.md`](CONTRIBUTING.md)、[`docs/ALGORITHM.md`](docs/ALGORITHM.md) 和 [`docs/TESTING_AND_BENCHMARKS.md`](docs/TESTING_AND_BENCHMARKS.md)。公开实测状态看 [`docs/STATUS.md`](docs/STATUS.md)；历史 `work/` 路径表示维护者本机来源，不随克隆提供。

社区作者可在自己的检出中开发和测试，不需要旧角色派单协议。多个 Agent 分清文件范围或使用独立分支/检出；完整门禁、成本基准和实机测试串行占用测试机器。新克隆中的状态应由自己的运行生成，不能继承维护者本机的通过结论。下文固定机器路径、特定任务授权和工作区 SDK 都是本地实例；社区构建通过显式参数提供自己的 .NET 9、Python、游戏及 RitsuLib 路径。

开放 Agent 规范不等于公开个人运行环境：不提交凭据、个人会话/账号、原生存档、原版游戏 DLL/PCK、反编译原版或下载 SDK。实机启动、部署、触发文件及进程控制必须在当前测试机器操作者的授权范围内进行。

### 先跑这两条

```bash
python scripts/state.py show     # 现在走到哪：阶段、最近门禁、活跃 backlog
git status --short               # 这是脏检出，先看清在制品
```

### 三条最常被违反的铁律

（R0–R8 全表在 §2）

| | 规则 |
|---|---|
| **R0** | **一次做完，不要中途回来问。** 只有四类算阻碍性问题：**产品/政策决定、凭据/账号、不可逆操作的许可、信息缺失到无法开始**。其余（实现细节、命名、缺参数、可回滚的风险）自己决定并往下走。 |
| **R1** | **不许声称未验证的事。** 要么给证据（**命令 + 退出码 + 日志原文**），要么明说"未验证"。 |
| **R2** | **新断言先证明它**会**变红**：故意改坏 → 确认红 → 恢复 → **把变红原文记进注释**。全绿的断言比没有断言更坏。 |

### 怎么选 skill（按"手上的东西/现象"选，**不要按目录选**）

| 手上是什么 | 用哪个 |
|---|---|
| 一份 `godot.log`，要判"每场是否严格按剧本" | **S3** |
| 要把一局跑起来 / 复现某个局内现象 / 验证一处执行侧修复 | **S5** |
| 剧本被拒或被丢 / 某个机制从不进剧本 / 战斗莫名拖长 | **S7** |
| 要为一个"只在实机出现过"的机制写断言 | **S8** |
| 要出包测试、要部署 | **S4** |
| 怀疑"我们和上游行为不一致" | **S1** |
| 修完一个"不报错、只悄悄偏离"的 bug | **S2** |
| 引入了上游没有的行为 | **S6** |
| 要说"修好了" / 交付前 / 要给出验证证据 | **S9** |

完整注册表、触发条件与文件路径见 §5。

### 权威边界（重要）

- **规范只有两份**：本文件（铁律、验证、Skills、偏离表）+ [`PLAN.md`](PLAN.md)（目标、backlog）。
- **历史文件不要依据它们行动**：根目录的 `CLAUDE.md` / `TEAM.md` / `AGENTS.md` / `SOUL.md`、
  以及 `TAKEOVER_REVIEW.md` / `DECK_DELTA_MODEL_PLAN.md` / `docs/building/**`。
  它们描述的协作协议与离线卡组评价线都已作废，其中若干条与本文件**直接矛盾**。
- **通过数只认这两个地方**：§3 的门槛，以及 `work/verification/` 里最近一次运行记录
  （`python scripts/state.py show` 会告诉你它是多久以前的）。
  **任何文档里写死的 PASS 数都是历史读数，不是当前保证** —— 它们已经漂过至少四轮。

### 开工前

1. `python scripts/state.py show` 拿到阶段与活跃 backlog；
2. 挑一个 backlog 或用户指定的任务，按上表选 skill；
3. 做完：完整门禁（§3：构建、独立类型加载、一次完整套件）→ `python scripts/state.py gate` → `python scripts/state.py log "…"`。

---

## 1. 项目速览

**是什么**：杀戮尖塔 2 联机机器人 MOD。当前主目标与入口一致：固定预算下的全 Bot A10 50% 通关率及混合桌协作质量。现模拟内核基于 Combat Solver 0.41.0；0.50.1 与 2026-10-07 主分支已经隔离拉取，但仍不支持网络多人，本轮不直接替换 Vendor。借鉴其请求账本、职责边界与有界续搜，逐项保留本地多人语义并用原生差分验证。

| 项 | 值 |
|---|---|
| 技术栈 | C# / .NET 9 / Godot / Harmony |
| 生产程序集 | `src/CoopBots`（壳）+ `src/CoopBots.Kernel`（引擎，`Vendor/` 里是 CombatSolver 源码） |
| 游戏 | `B:/SteamLibrary/steamapps/common/Slay the Spire 2/SlayTheSpire2.exe` |
| 游戏数据 | `…/Slay the Spire 2/data_sts2_windows_x86_64` |
| **mods 目录** | `…/Slay the Spire 2/mods/CoopBots` |
| 日志 | `C:/Users/Administrator/AppData/Roaming/SlayTheSpire2/logs/godot.log` |
| 代码地图 | [`docs/ALGORITHM.md`](docs/ALGORITHM.md) |

**本机维护者构建示例**（这台机器 PATH 上的 dotnet 没有 SDK；社区作者使用自己安装的 .NET 9 SDK，完整参数见 [`docs/TESTING_AND_BENCHMARKS.md`](docs/TESTING_AND_BENCHMARKS.md)）：

```bash
./work/dotnet-win/dotnet.exe build src/CoopBots/CoopBots.csproj -c Release \
  "-p:STS2DataDir=B:/SteamLibrary/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64" \
  -p:NuGetAudit=false
```

---

## 2. 铁律

这些是**用实机调试的学费换来的**。违反任意一条，代价已经量过。

**R0 · 一次做完，不要中途回来问。**
除**阻碍性问题**外，默认**把整个任务执行完**再回到用户面前。中途暂停去请示，是把判断成本转嫁给用户，
而多数时候答案就在本文件、上游代码或最保守的默认值里。

**什么算阻碍性问题**（只有这四类）：

| 类别 | 例子 |
|---|---|
| 产品/政策决定 | "这条偏离要不要保留" —— 两种做法都对，取舍是人的事 |
| 凭据/账号 | 需要密钥、需要登录、需要另一个人的 Steam 会话 |
| 不可逆操作的许可 | 删除、覆盖已发布工件、force push、往游戏目录安装 |
| 信息缺失到**无法开始** | 目标本身没定；注意"缺一部分"不算，先做能做的 |

**不算阻碍性问题**（必须自己决定并往下走）：
- 实现细节、命名、文件放哪、用哪个 API —— 选最贴合上游/现有惯例的那个；
- 缺参数 —— 用默认值或从现场（日志、代码、配置）推断；
- 有风险但可回滚 —— 做完说明风险；
- 用户已明确表达过的偏好 —— 照它执行，不要反复确认。

**例外**：用户**显式要求**交互时以用户为准。例如 `/grill-me` 这类"逐条追问直到共识"的指令、
或用户说"先问我"，就是**被邀请的提问**，不受 R0 约束。

**R0 执行纪律（一句话）**
> **任务不中途请示，一路做到需求被实现为止；单轮装不下就把等待交给后台任务、把状态落盘，让系统而不是用户驱动下一轮；"做完"只看需求自己的验收；探索只为写实现服务（每 3 次只读必须换 1 次写入实现）；预算真的装不下时明说，并交出能不依赖我继续的东西。**

（R0.1–R0.5 是这条规则的展开过程，2026-09-20 合并至此。它们值得留档的原因只有一个：
**五条规则是同一个病的五次描述，而病根是"用规则的文字代替规则的目的"。**）

**R1 · 不许声称未验证的事。** 「应该修好了」「大概是这个原因」一律不算。要么给出证据（命令 + 退出码 + 日志行），要么明说"未验证"。

**R2 · 新断言必须先验证它**会**变红。** 故意改坏 → 确认红 → 恢复 → 再宣布修好。
> 血泪：本 session 有三条断言写错了（分支错配、语义理解错、断言点本来就是对的），**全是靠改坏检查逼出来的**。全绿的断言比没有断言更坏。

**R3 · 修模式，不修实例。** 同一个形状的 bug 通常不止一处。
> 已量过：`FromDeckFor*` 四个入口里三个都破了；`BeginSideTurn` 双侧都漏了；回合切换的「漏抄一步」在四个对照里出现 5 次。**修之前先 grep 同形状的其它实例。**

**R4 · 不许自造"门"。** 上游没有的拒绝逻辑不要加；**只有「可执行性」可以拒绝**（牌不在手里 / 打不出去 / 药水没了 / 席位不能行动）。
> 已量过：自造的 `HasUncertainRisk`、`Boundaries.Count > 0`、逐步漂移拒绝、边界拒绝 —— 每一个都单独让剧本跑不完。

**R5 · 丢剧本必须留日志。** 静默失败优先级最高。
> 已量过：三处 `Reset()` 静默杀剧本，排查了三轮才发现，因为 `planRefusals=0` 而 `noScript` 在涨。

**R5b · 验收计数器自己也会撒谎 —— 先证明它活着。**
> 已量过（2026-09-20）：`noScript` 在**每一轮、每一场**都是 0，读起来像"剧本全程覆盖"。
> 真相是那条分支**根本不可达** —— "剧本走完最后一步"被当成
> `plan step refused: reason=plan-exhausted` 拦截并计入 `planRefusals`：
> ①`planRefusals` 因此分不清"被违反"和"走完了"；
> ②`noScript` 永远是 0。
> **一个恒为常数的指标不是证据，是"它不可达"的证明。** 先让它能变，再拿它下结论。

**R5c · 每一轮都出现的那行诊断，等于没人读的诊断。**
> 已量过（2026-09-20）：`COVERAGE BROKEN: ALL_FOR_ONE:prediction-exception:KeyNotFoundException:
> The given key 'Energy' was not present in the dictionary. @ …CardEffectSpecRegistry.cs:line 221`
> **在每一次内核套件运行里都打印**，连文件与行号都指对了，却被当成背景噪音读过去了一整个 session。
> 真相是 `case AllForOne:` 的标签丢了，`AllForOne` 掉进 `ENERGY_SURGE` 的分支里，
> 对一张只有 `DamageVar` 的牌取 `.DynamicVars.Energy` 而抛异常 —— 这张牌**从来没能进过任何剧本**。
> 结论：**看 "PASS 数" 之前，先读那些不是 PASS 的行**；恒常出现的红/黄字必须要么被修掉，要么被登记成"已知且无害"。

**R6 · 抄就要逐行对照，不能凭记忆。** 对每一个「镜像上游序列」的方法做**并排 diff**；上游没有对照物的，必须**登记为偏离**（见 §6-S6）。
> 已量过：靠回忆审计只能找到你记得要找的东西 —— 而"遗漏"的定义就是你想不到它存在。

**R7 · 别把「测试全绿」当成「被测过」。** 组件全绿 ≠ 管线被测过。功能级的断言要**以功能命名**。
> 已量过：86 条断言没有一条驱动过「搜索→部署→跨回合→继续」。

**R8 · 改完先看有没有把别的行为带走。** 换成"更正确"的实现会翻掉旧断言 —— 那些断言是**旧政策的守卫**，翻转它们是决策，不是修 bug。

**R9 · 实机与模拟的任何不一致 = P0，不许"记录后继续"。**（用户 2026-10-05 指令）
> 搜索的每一个计划都建立在"模拟的盘面 = 实机的盘面"之上。一旦两侧不一致，计划是在**另一个盘面**上算出来的，
> 它多好都不属于这一局。把不一致打印出来、然后照常执行，等于用一句日志换掉正确性。
> 2026-10-05 实机（seed RFTC0WGM4A3P）第 6 回合 refinement 采用 `new=VICTORY enemyHp=347->0 validated=1`，
> 同一份 40 步剧本实机执行到第 16 步全灭——这不是"运气差"，是**被验证的胜利与实机执行盘面/动作分叉了**（具体是哪一层见 S9）。
>
> - 下列通道出现即视为 P0，必须依次：具名 → 最小夹具复现 → 修复 → R2 变红原文 → 完整门禁（§3）：
>   `ProjectionDifferences`（`KernelSession.VerifyProjection`）、refinement 边界漂移
>   （`FirstLiveDifference` 的 `field=… expected=… actual=…`）、
>   `tournament script dropped: live replay no longer confirms the stored victory`、
>   `planned-card-not-found`、`the live enemy set no longer matches the script step`、
>   以及手牌 / 敌 HP / 逐回合计数（`Y`）的任何 expected≠actual。
> - 唯一豁免：**已登记为"已知无害"且附夹具证明影响为零**。默认不是豁免，是待修。
> - P0 清单与现场证据登记在 §5 **S9**；本次事件的具体条目在那里，不要在各处重复开单。

---

## 3. 验证机制

**默认完整验证只跑一次完整 PatchSmoke。** 用户已授权简化重复执行：`scripts/verify-project.ps1` 顺序运行生产构建、独立 TypeLoadCheck 和一次 `EnableKernelTests=true`、**不带程序过滤参数**的完整入口；不再默认先跑独立普通进程。原来的四门运行记录保持原样，不改历史证据。

| 阶段 | 命令要点 | 通过判据 |
|---|---|---|
| 生产构建 | Release，实际游戏/依赖路径 | **0 error**，警告不得超出本次冻结输入已核定的基线 |
| 独立 TypeLoadCheck | 独立进程，故意不解析内核 | `CoopBots.dll` 仍可枚举全部类型，退出码 0；实际类型数记录在日志 |
| 一次完整 PatchSmoke | `-p:EnableKernelTests=true`，无 `--*-only`、case 或 packaging 过滤参数 | 退出码 0、完整入口执行；完整最低计数线仍为 **294 PASS**，实际读数来自本次日志 |
| 独立普通诊断（可选） | `verify-project.ps1 -IncludeOrdinary` | 显式诊断普通进程；不属于默认重复步骤，沿用普通最低计数线 **172 PASS** |

完整入口保留所有普通场景，并在 `CooperativeScenarios.Run` 的 `KERNEL_TESTS` 分支追加六个入口（以及 `KernelEngineScenarios` 内部调用）。`Program.cs` 里的相应条件分支也提供聚焦诊断入口，不能把带过滤参数的运行报成完整套件。普通与完整进程中的 presentation/Harmony 钩子环境可能不同；保留场景调用不等于两种隔离环境逐项等价。

默认记录中，普通阶段为 **`covered-by-full`**，数值通过数为 **`null`**，不能虚报一份独立普通 PASS 数。`full_total` **只取实际完整内核运行的 PASS 行数**，不得把独立普通读数相加。启用 `-IncludeOrdinary` 时才另外记录其实际退出码、日志和读数。聚焦子集、`--packaging-smoke` 都不能替代完整运行。

**交付复用已验证工件，不再构建或重跑 smoke：**

```powershell
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File scripts/build.ps1 `
  -GameData '<actual game data>' -VerifiedGateBinding '<immutable-epoch-directory>/gate-run-binding.json' `
  -Label '<fresh-label>'
```

`gate-run-binding.json` 由本次 `verify-project.ps1` 在冻结输入、完成验证并核对运行时工件后生成；旧 epoch 的 `run-bound-final-gates.ps1` 绑定保持原样。带 `-VerifiedGateBinding` 的打包路径核对实际门禁退出码 0、当前源码/引用 DLL/包内文档与冻结输入、门禁 `build/` 和归档运行时两份 DLL 的物理哈希；通过后直接复制门禁 `build/` 工件，记录包与包内双 DLL 哈希。`-Label` 必须全新，已有目录或 ZIP 不得覆盖。

未传绑定的 legacy 打包路径仍会构建、类型加载并运行 smoke 子集；它只是备用出包路径，**不能证明完整回归通过**。核对包时必须同时识别 `CoopBots.dll` 与 `CoopBots.Kernel.dll`，不能只看壳 DLL 或版本号。

**执行频率**：开发中运行本次改动对应的具名用例及必要的实际 RED/GREEN；同一批候选改动完成、准备部署时才运行一次默认完整验证，聚焦结果不能冒充交付完整验证，也不能以旧绑定覆盖已变化的输入。
`tests/README.md` 提供命令与统计口径细节；本节是当前验证合同。用户本轮简化授权优先于旧 skill 的“必须四次物理执行”表述。

### 3.1 通过数从哪里来（**文档里写死的数字都不算证据**）

当前验证的**判据**是上表；**证据**只有一处。下方日期明确的四门复核表是历史记录，保持原样：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1   # 只验不改
python scripts/state.py gate      # 把最新一次 work/verification/*/summary.json 记进状态
```

`state.py` 会同时报出**这次运行是多久以前的**；超过 24 小时会标 `⚠ 不代表当前工作树`。

**2026-09-20 的复核结论**（跑出来才知道，不是推断）：

| 文档里的数 | 当时写的 | 复核结果 |
|---|---|---|
| 本文件 §3 门 3 | 166 | ✅ **对**（`-p:EnableKernelTests=false` 实测 166） |
| 本文件 §3 门 4 | 94 | ⚠ **对，但记错了行** —— 94 是 `--kernel-power-only` **子集**的数；门禁用**完整**套件 = **265** |
| 本文件 §7 | 166 / 94 | 同上：166 对，94 是子集口径 |
| `tests/README.md` | 转指 `TAKEOVER_REVIEW.md` | ❌ **权威链断了**：被指向的是一份已作废的历史文档 |
| `TAKEOVER_REVIEW.md` | 97 / 173（09-17） | 那天的读数；当时确实是这个状态 |
| `VENDOR_DEVIATIONS.md` | PASS 50（09-19） | 那天的读数；当时是 0.41 导入前的状态 |
| `work/verification/` | 162 / 242（09-18） | 那次运行的真实读数，两天前的工作树 |

**所以真正的问题不是"数字互相矛盾"，而是两个：**

1. **权威链断了一环** —— `AGENT.md` 说"冲突时以 `tests/README.md` 为准"，而它把权威让给了历史文档。
   已断掉该引用。
2. **两个不同的命令共用了"门 4"这个名字** —— 子集（94）与完整（265）。
   于是门槛写成 94 而读数记的是 265，**这个比较恒真，等于没有门**（R5b）。

**现在的规矩：**

- **判据（门槛）**：本节 §3 的当前表；本轮授权移除默认普通重复执行，原普通/完整最低计数线未悄悄降低；其它门槛变化仍须说明；
- **读数（证据）**：`work/verification/` 的最近一次运行 —— 由 `state.py gate` 记录；
- **记录通过数时必须同时记录是哪条命令跑出来的** —— 默认 ordinary 的 covered-by-full/null 不是独立实测读数，full_total 只记录完整运行；
- 其它任何文档里出现的 PASS 数，一律视为**该文档写作当天的历史读数**，不是当前保证。

> 阈值变动是**决策**，不是修 bug。断言被有意撤掉会让计数下降；那必须被人看见，
> 而不是悄悄改低门槛（见 R8）。

---

## 4. 持久化状态

**跨 session 的状态只认这三处**，其余都是派生物：

| 位置 | 内容 | 谁写 |
|---|---|---|
| `PLAN.md` | **全局规划**：目标、阶段、编号 backlog（B1–B11）的**定义**与验收判据 | 人 / 代理，改阶段时 |
| `work/agent-state/state/project-state.json` | 同名 backlog 的**状态**、最近门禁、最近实机批次 | `scripts/state.py` |
| `work/agent-state/state/journal.md` | 只追加的结论流水 | `scripts/state.py log` |
| 本文件 §7 | 当前状态的**叙述**（活文档，每轮更新） | 代理 |

```bash
python scripts/state.py show                       # 一屏：阶段、门禁、活跃 backlog
python scripts/state.py gate                       # 把最新 work/verification/*/summary.json 记进来
python scripts/state.py backlog close B1 --note "…"
python scripts/state.py log "结论 + 证据路径"
```

**`gate` 不会沿用上一次的结果**：读不到 `summary.json` 就记 `unknown`，
运行超过 24 小时会标成"不代表当前工作树"—— 否则这个字段会退化成 R5b 说的假证据。

其余运行期数据：

| 位置 | 内容 | 谁写 |
|---|---|---|
| `work/agent-state/{cache,config,data}` | 运行期缓存、实机日志证据 | 脚本 |
| `work/verification/<UTC 时间戳>/` | 当前完整验证记录（`summary.json` + 阶段日志；旧四门记录保留） | `verify-project.ps1` |
| `work/CLEANUP-2026-09-20.md` | 本轮 `work/` 整理的逐条删除记录 | 整理 |
| `outputs/` | 分析报告、已发布工件 | 报告 / 打包 |
| `outputs/CoopBots-v<版本>-<label>.zip` | 测试包 | `build.ps1 -Label` |
| `CHANGELOG.md` / `mod_manifest.json` | 发版才动（见 `docs/RELEASING.md`，版本号 **4 处同步**） | 发版 |

**诊断的第一现场永远是 `godot.log`**，不是代码。关键行（grep 这些就能还原一场战斗）：

```
opening search #N          search-complete: …        combat summary: …
plan step refused          plan dropped              plan step drift (logged, not refused)
plan boundary drift (logged, not refused)             plan sentinel missing
```

---

## 5. Skills 注册表

每个 skill = **触发条件 → 步骤 → 完成判据**。需要展开时再落成独立文件。

**执行 skill 时同样遵守 R0**：一路做完再回来，中途不请示。

**怎么选：按"手上的东西/现象"选，不要按文件或目录选。**

| 手上是什么 | 用哪个 |
|---|---|
| 一份 `godot.log`，要判"每场是否严格按剧本" | **S3** |
| 要把一局跑起来 / 复现某个局内现象 / 验证一处执行侧修复 | **S5** |
| 剧本被拒或被丢 / 某个机制从不进剧本 / 战斗莫名拖长 | **S7**（先仪器后逻辑） |
| 要为一个"只在实机出现过"的机制写断言 | **S8** |
| 要出包测试、要部署 | **S4** |
| 怀疑"我们和上游行为不一致" | **S1** |
| 修完一个"不报错、只悄悄偏离"的 bug | **S2** |
| 引入了上游没有的行为 | **S6** |
| 要说"修好了" / 交付前 / 要给出验证证据 | **S9**（验证门禁） |
| 想知道现在走到哪、下一步做什么 | `PLAN.md` + `python scripts/state.py show` |

**可调用入口**（`description` 决定何时被触发；本文件是它们的唯一权威来源）：

| Skill | 文件 |
|---|---|
| S1 上游序列审计 | `.claude/skills/coopbots-upstream-sequence-audit/SKILL.md` |
| S2 不变量补测（R2） | `.claude/skills/coopbots-invariant-regression-test/SKILL.md` |
| S3 实机日志现场诊断 | `.claude/skills/coopbots-live-log-triage/SKILL.md` |
| S4 打包与部署 | `.claude/skills/coopbots-package-deploy/SKILL.md` |
| S5 无人值守实机测试管线 | `.claude/skills/coopbots-live-test/SKILL.md` |
| S6 偏离登记 | `.claude/skills/coopbots-deviation-register/SKILL.md` |
| S7 内核缺陷定位 | `.claude/skills/coopbots-kernel-debug/SKILL.md` |
| S8 最小夹具编写 | `.claude/skills/coopbots-kernel-fixture-authoring/SKILL.md` |
| S9 验证门禁（R1/R2/一次完整验证） | `.claude/skills/coopbots-verify-gates/SKILL.md` |

**跨 skill 的两条硬经验**（来自 2026-09-20 的实机调试，花了一整天换来）：
1. **先让系统说话，再改逻辑。** 本 session 每一次真正的突破都来自先加一行仪器；每一次走弯路都来自"读代码猜根因"。
2. **外在表现相同 ≠ 根因相同。** "机器人不打、战斗拖着"既可能是**目标函数缺项**，也可能是**搜索树被截断**；不写探针就分不开（S7 的探针法）。

### S1 · 序列审计（对照上游）
- **触发**：怀疑"我们和上游行为不一致"，或改动涉及镜像上游的序列。
- **步骤**：把「我们的方法」和「上游对应方法」**并排读**（同一次工具调用里读两边）→ 逐行列出差量 → 每处差量判定为 等价 / 有意偏离 / **遗漏**。
- **完成判据**：差量表为空，或每一处差量都有登记（S6）。
- **优先对照对**：回合开始、回合结束、出牌重放、用药重放、所有 prefix 拦截。

### S2 · 不变量补测
- **触发**：修完一个"不报错、只悄悄偏离"的 bug。
- **步骤**：写出断言 → **故意改坏 → 确认红 → 恢复 → 确认绿** → 进 `RunContinuationInvariants`。
- **完成判据**：断言在改坏后确实变红（把变红的原文记进注释）。

### S3 · 现场诊断
- **触发**：日志中的团灭、卡顿、错序、投资/消耗/选牌或构筑估值问题。
- **步骤**：先归档同场日志、可用 meter/replay/save，再跑合并分析器；按执行、模拟、局内策略、预算调度、局外估值与选项分层。详细方法见 `.claude/skills/coopbots-live-log-triage/references/decision-attribution.md`。
- **完成判据**：对所问场次提供原始证据、问题归属、可执行反事实或证据缺口；区分已证实/嫌疑/合理取舍。严格执行剧本不等于决策正确。

### S4 · 打包与部署
- **触发**：要出包测试。
- **步骤**：确认完整门禁和不可变绑定 → `build.ps1 -VerifiedGateBinding <绑定路径> -Label <全新 label>`（直接复用门禁工件，不重构建/重跑 smoke）→ 核对 zip 内容、两份 DLL 与已验证工件哈希。用户只要本地包时交付到此；获准部署后关闭客户端再解到 `mods/CoopBots`。
- **完成判据**：当前完整验证通过且绑定匹配 + zip 与包内双 DLL 的 sha256 已记录 + 旧测试包未被覆盖。

### S5 · 无人值守实机测试管线（2026-09-20 重写，按实际建成的版本）

> **可调用入口**：`.claude/skills/coopbots-live-test/SKILL.md`（`description` 决定何时被触发）。
- **触发**：要跑实机、要复现某个局内现象、要验证一处执行侧修复。
- **一条命令**：
  ```bash
  & '<bundled pwsh.exe>' -NoProfile -NonInteractive -File scripts/build.ps1 -VerifiedGateBinding '<immutable-epoch-directory>/gate-run-binding.json' -Label '<fresh-label>'    # 复用完整验证工件，不另跑 smoke
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-live-loop.ps1 -Rounds 1 -SilenceMinutes 5 -Seed <seed>
  ```
  `run-live-loop.ps1` 自己完成：杀客户端 → 部署到 `mods/CoopBots` → 写哨兵 → 经 Steam 启动 → 看护 → 卡死则杀。
- **必须经 Steam 启动**（`steam://rungameid/2868840`）。直启 exe 会进 editor 模式，在 profile 设置加载**之前**枚举 mod，于是每个 mod 都被 `user has not yet seen the mods warning` 跳过；把标志写进 `settings.save` 也无效（读它的时机更晚）。
- **哨兵文件**：`mods/CoopBots/live-test.flag`。第一行 = 种子；可选一行 `mode=single` 切回旧的"单席直进一场战斗"（`EnterRoomDebug`）模式。**默认模式 = 多人 4 BOT / A10 / 从地图开始爬**（`LiveTestMultiplayerRun`）。
  - 为什么是文件不是环境变量：经 Steam 启动时游戏**不是本进程的子进程**，环境变量传不进去。
  - 为什么路径取**程序集自己的目录**而非 `AppContext.BaseDirectory`：mod 被 `Assembly.LoadFrom` 装进隔离加载上下文，后者指向游戏 exe 目录，哨兵永远找不到。
- **⚠️ 交还用户前必须删掉哨兵。** 留着会让用户**一开游戏就被劫持**成一局自动 4BOT A10。`run-live-loop.ps1` **不会**自动删。
- **看护判据是"局面指纹"，不是日志 mtime。** mod 自己每 5s 一行 `CoopBots live test: where …` 会把 mtime 一路刷新，mtime 规则**永远不触发**（实测卡在商店 4 分钟不被杀）。指纹 = `floor/room` + **最后一条进度行的原文**；**不要用滑动窗口里的计数**（旧行滚出窗口会让计数变小，一次缩小就是一记假"进展"）。
- **判读验收**（`grep "combat summary"`，每场一行）：
  - `plans=n (coverage=x%)` 部署过的剧本数（新段部署时增长，完整剧本复用时可保持不变）；`routes=k/n` 其中搜到战场解的有几个。
  - `planRefusals=0(drift=…,pending=…,missing=…)` **只有可执行性拒绝算数**：`drift` 是"只记录不拒绝"、`pending` 是等上一步落地、`missing` 是搜索没能重放出预测。
  - `noScript=0` 内核已无"停止思考"的正常路径，非 0 是**报警**（见 R5b）。`exception=0`。`seatSkips=` 席位退场被跳过的步数。
  - `maxRound=` 必须是真实回合数。**曾经每场都是 1**，因为采样只发生在搜索起点（见 R5b）。
- **终止方式必须分类**，否则会把外部事件读成我们的 bug：
  | 现场 | 含义 |
  |---|---|
  | 有 `How did the game end??` | **运行结束**（团灭/胜利）→ 才可以谈强度 |
  | 有 `NGame.Quit called` 且无上一行 | **外部退出**（`_Notification(1006)` = `NOTIFICATION_WM_CLOSE_REQUEST`）→ **数据被截断，不能用来判断强度**。实测 live17 被用户手动退出，误当"打到 6 场就停" |
  | 两者都无、被看护器杀 | **卡死** → 去找具名行：`CoopBots map: waiting — …`、`plan step refused: …`、`search slice overran: …` |
- **先归档再分析**：`cp godot.log <批次>/godot-mp-a10-NN.log`。滚动中的 `godot.log` 会让**场次归属**判错（实测把一次拒绝错记到另一场，靠备份日志才纠正）。
- **分析工具**（本 session 写的，放在批次目录里）：
  - `refusal-check.py <log>`：逐场 `refused/dropped/skipped/drift/complete` 计数 + 每次拒绝原文 + 归类 + **"每次丢弃是否都由具名拒绝引起"**（查静默杀剧本）。
  - `analyze-mp-a10.py <log>`：逐场表 + 拒绝原文 + 搜索成本分布（wall/compute/nodes/alloc/gcPause）。
  - `analyze-death.py <log>`：最后一战现场（血线、搜索、结尾 30 行）。
- **完成判据**：给出"每场是否严格按剧本"的表 + 终止方式分类 + 到第几层/是否通关；改了代码则按 §3 完整验证通过。

### S6 · 偏离登记
- **触发**：发现或引入了一处与上游不同的行为。
- **步骤**：在代码注释里写明 **上游怎么做 / 我们怎么做 / 为什么**，并在本文件 §6 追加一行。
- **完成判据**：任何"上游没有"的东西都能在 §6 找到出处（谁要求的 / 为什么）。

### S7 · 内核（模拟器）缺陷定位 —— 剧本对不上 / 某机制从不进剧本

> **可调用入口**：`.claude/skills/coopbots-kernel-debug/SKILL.md`。
- **触发**：实机出现"剧本被拒/被丢"、"局面与预期不符"、"某张牌某个机制从不进剧本"、"战斗莫名拖长"。
- **第一原则：先让系统说话，再改逻辑。** 本 session 每一次真正的突破都来自**先加一行/加一个字段**：
  | 现象 | 加的仪器 | 它立刻给出的答案 |
  |---|---|---|
  | 商店永久卡死 | `shop: entered … stock=[…]` / `idle room=… isMerchant=…` | 驱动**走错分支**：`NetGameType.Host` 漏掉 Singleplayer |
  | 地图开着却不投票 | `map: waiting — <why>; generation=N` | 是 `no-route`，**证伪**了"陈旧票"这个首选嫌疑 |
  | 拒绝只写 `no-card`/`playable` | 给**每个守卫**各自命名 | `round-drift` / `potion-not-held` … 才知道该改哪一道 |
  | 某场"莫名沉默 4 分钟" | `search slice overran: took=… rounds=… nodes=…` | 单次扩展超时（**不是**"没在搜"） |
  | boss 战"机器人不杀、战斗拖" | 指名 `PendingTurnStartChoiceDescription` | 阶段转换后留下的 pending choice |
- **R5b 家族：恒为常数的指标不是证据。** 已量过**五个**同形实例；见到"永远 0 / 永远不变"的字段，先证明它**可达**：
  1. `noScript` 曾不可达（"走完最后一步"被当成拒绝计数），于是每场都是 0；
  2. `plansWithRoute` 漏了一处每场重置 → `routes=` 分子全局累计、分母单场；
  3. `routes=` 漏了两条部署路径的计数 → "以结束回合/以药水开局"的剧本**找到了解却不计**；
  4. `CanModelPotion` 比它管的那道闸**更窄** → 报告说"可建模"、闸却不放行（或反之）；
  5. `COVERAGE BROKEN: ALL_FOR_ONE:…` **每轮都在打印**却没人读 —— 它指的正是"`case` 标签丢了，这张牌从来没能进过任何剧本"。
  → **看 PASS 数之前先读不是 PASS 的行**；恒常出现的红/黄字要么修掉、要么登记为"已知无害"。
- **比对的两侧必须枚举同一个集合。** 实测 `ContinuationStamp`：实盘侧 `Creatures.SelectMany(c => c.Powers)`、模拟侧 `EffectivePowers()`（= hook listener 里的 power）→ 口径不同 → 投影分歧**每次捕获都报**、`drift` 恒亮，把真正的分歧掩盖了。**任何 expected/actual 分歧，先问两侧是不是在数同一件东西。**
- **描述性标签 ≠ 原因。** 若诊断行只回答"这一步是不是牌"（`no-card`）或"牌在手里且能指目标"（`playable`），它**不是**拒绝原因。判据：**每个 `return false` 的守卫都要能被单独指名**，否则同一个词会同时表示多种故障。
- **实机现象 → 最小可判定实验（探针法）**：写一个**只观测、不改行为**的最小实验，让结论只能是你问的那一个。
  - 例：怀疑"评分让机器人不敢赢" → 直接驱动 `KernelTeamSearch`，把**胜利**的评分人为设成灾难级（−1e6），读 `CompletedState.HasWon`。实测 `True` → **该假设被否证**，注意力这才转到"树被截断"。
  - **探针给出"没问题"同样是答案**（本 session 两次靠它纠正了我自己的错误假设）。
  - 探针**不要顺手留成测试**：若同一性质已被既有断言守住，新断言无法按 R2 自己变红 —— 实测把 `Complete` 的终局优先关掉时，既有 `P1-1: A complete nonlethal round must not displace an immediate lethal` 先变红。此时**撤掉冗余断言**，只留下"该性质已有一道门"的结论。
- **读 `search-complete`**：`wall` / `compute` / `gcPause` / `nodes` / `alloc` / `stop` / `turnPrefix` / `crossesTurn` / `route` / `actions`。
  - `stop=frontier-empty` = 当前保留前沿耗尽，不证明已穷举或无解；`stop=time-budget|node-budget` 表示预算触顶，需结合候选、边界和完成终局数判读。
  - 先核对计时采集点、线程及嵌套阶段口径；不固定假设 wall/compute 比例。有效 compute 才能作为 GC 比率分母。
  - 判"还能不能加深/加席"：看 `nodes` 与 `wall` **对预算的占用**。实测单人 A0 只用 3–4%；4 席 A10 已到 `nodes` 90%+ / `wall` 97%（**几乎无法延长**）。
- **"未建模 = boundary" 的代价分两种，先分清再动手**：
  - 跳过**一张牌/一瓶药水**：只损失那件东西的价值（可接受，登记即可）。
  - 截断**时间轴**（回合边界的 pending choice、未建模的敌方招式）：搜索**看不到地平线之后**，会退化成"只求活过这几回合"。实测 `boundaries=end-turn:round-pending-choice×749` 就把一场 25 回合的 boss 战变成了"不杀只防、一直拖"。**时间级优先修。**
  - 注意：**"目标函数缺项"和"树被截断"会产生同一种外在表现**（机器人不打、拖着）。分清的方法就是上面的探针法。
- **修模式不修实例（R3）的现成清单** —— 改一处前先 grep 这些同形点：
  - 每场必须重置的计数器：`plans / plansWithRoute / planRefusals / planTicksWithoutScript / seatStepsSkipped / planDriftDetected / planSentinelPending / planSentinelMissing`；
  - **三条部署路径**（结束回合开局 / 药水开局 / 卡牌开局）都必须计数并设置 `LastExecuted`；
  - `NetGameType.Host` 的**所有**在局内站点（`RunAuthority` 已把"谁是这个局的出牌方"收成一处；"要广播给远端"是**另一个问题**，保持 Host-only）；
  - "需要选择"的药水是**九瓶**（`PotionChoiceSupport.RequiresChoice`），别只挡四瓶；
  - "这个房间当前 act 造得出来吗"（`RoutePlanner.IsBuildable`）。
- **完成判据**：现象被一个**具名守卫**解释（不是"某个地方"）；修复附 R2 变红原文；按 §3 完整验证通过；并且**明确区分哪些是实测、哪些只是构造推断**。

### S8 · 把局内机制做成最小夹具（写断言用）
- **触发**：要为一个"只在实机出现过"的机制写断言。
- **现成夹具模式**（`tests/PatchSmoke/KernelEngineScenarios.cs`）：
  - `ContinuationBoard(seed)` → `(bot, other, combat)`：两席 + 一个 `MockAttackMonster`，手牌/抽牌堆已铺好；
  - 造牌与摆位：`combat.CreateCard<T>(player)`、`p.PlayerCombatState.Hand/DrawPile/DiscardPile.AddInternal(...)`；
  - 单步推演：`KernelSession.Capture(combat).Fork()` → `branch.Play(card, target, out reason)` / `branch.UsePotion(...)` / `branch.EndTurn(...)`，再用 `branch.Hp/Hand/Discard/Power<T>` 读结果；
  - 直接驱动搜索：`new KernelTeamSearch(root, actors, s => <自定评分>, new(MaxRounds:…, IncludeEndTurns:true, MaxNodes:…))` + `Advance(...)`，读 `CompletedResult` / `CompletedState` / `HasRoute`；
  - **扫描式断言**优于单实例：`AuditPotionCoverage` 用反射实例化**每一个** potion 并问 `CanModelPotion` —— 新机制优先加成这种扫描（本 session 正是靠它发现"`BLAZE` 一直不可用""九瓶药水只挡了四瓶"）。
- **夹具会骗你（R2 的价值）**：断言写完必须**改坏 → 确认红 → 恢复**。
  - 本 session 有断言在改坏后**仍然绿**：我把"两个实现都满足"的性质当成了区分点（"脚本 ≥6 动作"——短桩也可能很长；真正的区分点是**是否跨回合**）。
  - 另一条"行为断言"一开始用 `SkillPotion`，退回窄谓词也能通过 → 抓不到目标 bug；换成 `DropletOfPrecognition` 才有区分力。
- **完成判据**：断言在改坏后确实变红，变红原文记进注释；若既有断言已覆盖同一性质，**撤掉冗余断言**而不是留两份。

### S9 · 模拟一致性 P0（实机 ≠ 模拟 = 最高优先；用户 2026-10-05 指令）

> 规则见 §2 **R9**。这里只登记具体条目、证据与修复方向；每一条在修完并拿到 R2 变红原文前都是 P0。

**2026-10-05 伪 VICTORY 事件**（seed RFTC0WGM4A3P，A10 第二幕 Boss `THE_INSATIABLE` 第 6 回合）

- 证据：日志 `work/live-triage-20261005-night/godot.log:46712`（refinement 采用 `old=WIPE new=VICTORY enemyHp=347->0 validated=1`）、`:47116-47169`（实机 16 步后全灭）；诊断探针 `tests/PatchSmoke/SandpitVictoryProbe.cs`（`--sandpit-victory-probe`，`work/live-triage-20261005-night/report.md`）。
- **已证实（探针）**：
  1. 分支捕获逐席沙坑正确（1/2/1/1），`FranticEscape` 只把**自己**的坑 +1（B2 1→2）；
  2. 把实机第 6 回合剧本（S 的逃牌 + 各席格挡）灌进模拟器：敌方回合结束四席全 0、`victory=False`——**同一盘面上模拟器自己也判团灭**；
  3. 生产 `TournamentRun` 从同一盘面能找到"B2 存活"的线，但那要求计划**给 B2 打逃牌**；实机采用的 40 步剧本只给 S 打了逃牌。
- **结论（区分强度）**：伪 VICTORY = **"被验证的胜利"与"实机执行的剧本"在某一层分叉**（已证实：验证通过的剧本在实机盘面上模拟器自判团灭，且实机分配的存活线需要 B2 的逃牌、实机却执行了 S 的逃牌）。分叉的层有两个候选，**尚未区分**：(a) 边界盘面的逐席状态不同而校验没抓到（最可疑：带 `Target` 的 Power 身份不在可见状态文本里、实机/模拟枚举口径不同）；(b) 采纳脚本里是 B2 的逃牌、部署执行成 S 的（跨席同名牌身份/归属错位）。两者都登记为 P0-SIM-1/2，不要挑一个当结论。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-1** | 定向 Power 的逐席身份（`Target`）不在边界校验里：`ContinuationStamp.AppendPowers` 只渲染 `owner:type=amount/vars`；实机侧枚举 `Creatures.SelectMany(c => c.Powers)`，模拟侧 `EffectivePowers()`（hook listener 顺序）——**两侧不是同一枚举口径**。同 owner 多个实例（4 个沙坑分别挂在 4 席）在文本里只是一条有序 amount 序列，顺序口径不一致时逐席交换可以在 `RefinementMatches`/`FirstLiveDifference` 里通过 | 代码事实（`ContinuationStamp.cs:415-430`、`:102` vs `:165`）；探针只在 **root capture** 下证明过能区分交换，重放链的枚举顺序未复现 | **已修复 2026-10-05**：`AppendPowers` 追加 `;PT=` 段，按 `(owner,type,target)` 排序渲染 `owner:type=amount@target`（`ContinuationStamp.cs`）。夹具 `tests/PatchSmoke/KernelConsistencyScenarios.cs`（由 `KernelEngineScenarios.Run` 调用）：两块盘面 `P=` 序列相同、逐席语义不同 → 断言文本必须不同；另断言同语义不同枚举顺序的 `PT` 相等。**R2 变红原文**：删掉 PT 块后 `System.Exception: P0-SIM: targeted powers must be compared by (owner,target) identity, not by position`。四门记录 `work/verification/20261005T144302517Z`（build 0 error/6 原有警告、TypeLoadCheck 通过、普通 343、内核 510=509+1）。**注意**：本修复关闭的是"边界文本表达不了逐席身份"这一条通道，尚未实机复测证明该局伪 VICTORY 的成因就是它 |
| **P0-SIM-2** | `Victory` 记录缺守卫：采用前不校验"同一边界上预测存活席位终局存活 + 定向 Power 逐席一致"，且**不校验实机执行的每一步就是被验证的那一步**（owner+牌+碰数+目标）。探针显示实机分配下唯一能活的线是 **B2 打 FRANTIC_ESCAPE**（`script1: player=793474 FRANTIC_ESCAPE`），而实机日志第 11 步是 **S-jie** 打 FRANTIC_ESCAPE——要么采纳脚本含 B2 的逃牌而部署执行成 S 的（跨席同名牌身份/归属错位），要么生产边界本就与实机不同（见 P0-SIM-1） | 本次 `validated=1` 通过、实机 16 步全灭；探针输出 `work/live-triage-20261005-night/sandpit-probe.txt` | **已修复 2026-10-05**：① `KernelCombatPlanner.Refinement.cs` 加 VICTORY 采用守卫——`Verified+Victory` 必须"复验后至少一名预测存活席位"且"边界与实机的 `TargetedPowerState` 逐席一致"，否则具名拒绝（`victory refused: …`）；采用时输出 `CoopBots victory guard: round=… survivors=… postHp=[…] sandpits-boundary=[…] sandpits-live=[…]`。② `KernelSession.ResolvePlannedCard` 统一四处部署解析（R3）：只在动作**没有记 key** 时才允许实例兜底；记了 key 而实机牌 state key 已漂移（升级/改费/附魔）→ 具名拒绝 `card-state-changed`，不再"对象还在就照打"。③ 夹具 `KernelConsistencyScenarios` 两段：refinement 胜利守卫（无存活席位/定向 Power 不一致/合法胜利三种）与部署身份（状态漂移拒打 + 同名牌不串席）。**R2 变红原文**：关掉胜利守卫 → `System.Exception: P0-SIM: a victory with no surviving seat must not be adopted`；关掉 state-key 检查 → `…a card whose state changed since verification must not be played`；把解析改成 `actors[0]` 错误席位 → `…the planned step must resolve to the acting seat's own card`。四门 `work/verification/20261005T150527684Z` exit 0（build 0 error/6 原有警告、普通 343、内核 **512**=510+2）。仍未实机复测；P0-SIM-3/4/5 未修 |
| **P0-SIM-3** | refinement 边界漂移（本局 4 处：`C[2]` 手牌 R2、`E0.hp 1043 vs 1061` R4、`H[0]` 手牌 R5、`Y` 计数 MYTES R4/R6）只打印不追根 | `work/live-triage-20261005-night/godot.log` 43880/45449/45963/41181/42304 等 | 每处一个具名守卫 + 夹具；满手 DAZED/Ethereal 与敌 HP 差 1–20 是已知高危形态；修完前是 P0 |
| **P0-SIM-4** | `live replay no longer confirms the stored victory` 4 次：B2 `DEFEND` unplayable（16779）、B1 `MOLTEN_FIST` 满手 DAZED（36757）、B2 `BLADE_SYMPHONY` 满手 DAZED（37846）、S `STRIKE` planned-card-not-found 空手（42080） | 与 §5.1/5.4 旧同类；本局 3/4 在第二幕 | `unplayable` 与 `planned-card-not-found` 各自具名到守卫；补 DAZED/Ethereal 手牌夹具 |
| **P0-SIM-5** | 敌阵不匹配 1 次（`step 6/50 …`，脚本被丢弃） | 本局日志；旧例为 REATTACH/召唤类 | 敌阵变化必须给具名原因；与獠牙/重接类夹具绑定 |
| **P0-SIM-6（2026-10-07）** | RLAE8Y8MA4YH 知识恶魔边界玩家 HP `expected=34 actual=36` | 冻结证据 `work/live-triage-20261007-latest/`；旧部署 DLL 的基本瓦解、虚弱攻击、Orichalcum/Plating 组合原生对照正确，未恢复历史分叉前的完整状态 | 本轮共享 Power 写入隔离、回合参与者及缓存失配通道各有原生/R2 证明；**未证明这次历史 2HP 分叉的具体根因，不关闭该条** |
| **P0-SIM-7（2026-10-07）** | 同局两次外骨骼虫敌阵失配（日志 19955 / 20560） | 旧部署 DLL 的 9 点单次伤害上限及 Ringing 原生对照正确；包含 PaperPhrog 的可见伤害不能用漏算易伤的 75 点推导下结论 | 本轮 Ringing 重放发布当前牌身份及实际选择、共享 Power 隔离已有原生/R2 证明；**未恢复两次历史敌阵分叉，不关闭该条** |
| **P0-SIM-8（2026-10-07）** | 5NPNPX62Q6PK 六场预测下一回合少两张手牌、四场先报洗牌 RNG 差异；同一冻结 turn1 根随 live turn1→2 从 draw5→3 | `work/live-triage-post-decision-20261007T120647Z/draw-live-probe.log` 已复现加载 DLL；修复和原生抽牌/洗牌对照在 `work/incombat-repair-20261007/projection/` | 按原生 Hook 监听顺序，以模拟回合和捕获的计数替换八类抽牌、两类能量遗物贡献；原生洗牌对照验证漏抽可跳过洗牌。**历史四次完整分叉盘面未恢复，不能以机制对照宣称逐场历史重放已关闭** |
| **P0-SIM-9（2026-10-08）** | 1JJ0XFWWNLTC 实验体R2 Guarded的`Target`预测各自身、原生null，脚本被丢弃 | `work/live-triage-20261008T023749-local/godot.log:54639`；原生Tank.Apply只传Owner/Applier | **机制已修、待实机复测**：默认Target=null，Owner/Applier独立；原生首次施加、显式Target、父子分支、施加者死亡移除对照通过，未删除PT校验。`LatestRunModelRepairScenarios` / `model/model-r2-results.json`；四门 `work/verification/20261007T215935141Z`。不声称完整历史整战已重放或该字段单独改变伤害 |
| **P0-SIM-10（2026-10-08）** | 同局第二幕Hopper R3 Flutter预测5/原生24 | 同归档22845；首次Apply原生多人倍率 `5×4×1.2=24` | **机制已修、待实机复测**：通用首次能力应用使用原生ShouldScale/override及捕获幕号，叠层不重复缩放；多人数/幕数、Plating、Artifact/Skittish/Slippery、伤害后Flutter23及眩晕对照/R2通过。计数测试同步预测只是修正夹具，未改既有Flutter伤害衰减。证据 `work/latest-run-repair-20261008/model/`；四门 `work/verification/20261007T215935141Z` |
| **P0-SIM-11（2026-10-08）** | 同局五次HiddenDaggers选项失配，原始要求弃2而实际候选不足2 | 同归档15315/35163/39746/49028/56361；少牌有效数量和实机桥元数据口径不同 | **机制已修、待实机复测**：保留原始/有效bounds、完整候选顺序与真实菜单源，独立夹具驱动原生0/1/2、连续嵌套自动打牌及实际CardChoicePatches/PlanSync；泛型真实空菜单/Seeker保留，Dredge/NeowsFury容量条件与Armaments升级过滤对齐原生。真实失配仍清缓存/留日志。14功能夹具（含原生选牌及执行桥对照）、19项故障注入通过，`work/latest-run-repair-20261008/choice/`；四门 `work/verification/20261007T215935141Z`。这些数量失配不被当成任意错弃核心的证明 |

**2026-10-08 新部署后 SZG1RJZ6XVL6 现场（下述机制已修，待新局复测）**：证据 `work/live-triage-20261008T121207/`，实际运行 Core E50B42…2520D / Kernel57037F…0245B；与验收源码1211编译输入SHA一致。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-12（2026-10-08）** | 吞噬者R2生成逃牌后牌堆顺序不一致：`C[1] expected DOMINATE actual FRANTIC_ESCAPE` | 归档godot.log25785；原生Liquify每席连续draw3/discard3，镜像先全席draw3再全席discard3，共享Shuffle随机插入调用顺序不同。源码顺序差已确认，历史全部RNG尚未恢复，不能宣称具体分叉已闭环 | **机制已修、待实机复测**：Liquify、Noisebot、SoulFysh 按逐席逐卡原生创建/插入次序，共享 Shuffle RNG 对齐；原生1/2/4席完整牌堆与RNG及故障注入通过。证据 `work/szg-repair-20261008T124624/model/`；最终四门及安装见本轮报告。历史完整隐藏RNG未恢复，不声称整场历史重放已关闭 |
| **P0-SIM-13（2026-10-08）** | 炮塔R2/R3敌HP固定少3：预测153/134，原生150/131 | 归档31807/32411；Bot2水银沙漏AfterPlayerTurnStart造成3伤，原生所有席位Setup至完成/暂停后一次AfterSideTurnStart城垛；KernelTurns逐席即StartSide，城垛提前于后席沙漏。源码时序差与现场同形，待原生最小夹具闭环 | **机制已修、待实机复测**：按全队Setup完成/真实暂停→一次Side/Orb→逐席恢复处理；真实Bot/接管真人同步任务在Side前结算，普通真人仍延期。原生Hourglass各席/多实例/Rampart/额外回合/嵌套菜单及故障注入通过，保留HP与次数校验。证据 `work/szg-repair-20261008T124624/model/`；四门已验，未恢复历史全局分叉 |

三次独立事件，下一行disposed为重复诊断。本轮最终实验体没有记录到这些漂移，开场已经预测WIPE，不能把这三次早先战斗差异直接写成此次Boss死亡根因；不据“本场没报错”关闭历史P0。

**2026-10-09 架构基线 AR10B7M2X001（旧部署，未自然结算）**：第39层 AXEBOTS_NORMAL R6后无进展，看护终止。现场归档 `work/architecture-refactor-20261009/live/robot-hang-AR10B7M2X001/` 保留日志、线程、最小转储和战斗入口记录；不能把该次卡死计成自然团灭，也未证明后续主线程挂起的完整调用因果。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-14（2026-10-09）** | ANGER+PERFECT_FIT洗牌后抽到的同名牌身份不同，H中的位是`DeckVersion != null` | 旧镜像按注册次序处理原牌、克隆；原生Anger先入克隆再结算原牌到Discard，按当前AllPiles监听顺序处理，PerfectFit最终将原牌放顶。真实付费OnPlayWrapper→Shuffle→Draw对照复现同形差异 | **机制已修，待完整门禁与实机复测**：仅ModifyShuffleOrder重排当前分支所属卡/附着监听，相同牌附件和非卡槽位保留，逐项pending-choice边界保留。7项物理故障证据 `native-card-scope/r2-final/proof.json`；原生5牌堆/DeckVersion/Shuffle RNG/父子隔离对照通过。不声称第三方MOD跨类型监听顺序已覆盖或本项解释后续卡死 |
| **P0-SIM-15（2026-10-09）** | gameplay `ICardScope.CloneCard`错误复用保留DeckVersion/removed状态的捕获帮助方法 | 原生CombatState.CloneCard→CardModel.AfterCloned清除这两字段；独立原生克隆对照发现该接缝。Anger既有factory本来正确，不能将此项当作Anger生成牌的根因 | **机制已修，待完整门禁与实机复测**：只在gameplay入口复原原生两字段重置，捕获/COW仍保留原状态，cloneOf/IsClone不改。证据同上；完整419生成源码重导入差异0，base/seam/target三坏输入均RED1→精确恢复GREEN0，`native-card-scope/import-verification-v2/proof.json` |

**2026-10-09 固定预算 v3 保存入口诊断（UTC；本地次日）**：新 Steam 所有权进程从封存 combat039 入口恢复，完整门禁为 `work/verification/20261009T152747243Z/`（生产/独立类型/一次完整790 PASS），包和双 DLL 冻结于 `candidate/frozen-v3/`。本场在原生终局前发现首个完整合同失配，正常停止、归档、清理触发文件与锁；不是完整 A10 通关率样本。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-16（2026-10-09 UTC）** | Orichalcum / FakeOrichalcum 的惰性镜像 StateStore 条目与原生隐藏触发标志未在根捕获合同对齐 | `fixed-budget-performance-v3/paired-preparation-v4/attempts/combat039-v3-20261009T154700Z/after/logs/godot.log:2471`，`cacheMissAxis=semantics field=captured-state`，预测 Orichalcum/index4 对实际 LizardTail/index8。独立原生旧实现 build0，false 无效果 hook 仍多分配条目、真实 true 标志遗漏均 RED1；真实遗物顺序交换仍被拒绝。证据 `fixed-budget-performance-v3/state-diagnostics/p0-native-orichalcum-v1/old-native-repro-v3/proof.json` | **机制与v4合并完整门禁已通过；整塔仍待验证**：v4保存入口本通路未复现旧山铜具名失配，随后因P0-SIM17停止，不能推断其它分支已通过。捕获原生 `_shouldTrigger`，同一 owned typed 状态贯穿 early/normal/reset、消费与分支隔离，续演文本及搜索指纹包含标志；完整合同三文件与 frozen-v3 字节一致。focused10、既有规划守卫16、9项实际 C# R2（均 RED1→精确恢复 GREEN0）、13项导入守卫 R2 已封存于 `fixed-budget-performance-v3/state-diagnostics/p0-native-orichalcum-v1/sealed/manifest.json`；两份 Vendor 独立重导入 exit0/字节一致。保留两个 R2 driver 分类失败及独立实际命令/断言审计；未弱化合同或遗物顺序。片段不证明遗物被替换，也不解释历史知识恶魔 2HP 分叉。 |

**2026-10-09 固定预算 v4 保存入口诊断（UTC；本地次日）**：门禁 `work/verification/20261009T162709162Z/` 生产/独立类型/一次完整796 PASS 均 exit0；包直接复用双 DLL，1266 个非工具链输入封存于 `candidate/frozen-v4/`。本次原生所有权 Steam PID28776 从同封存入口恢复，至下述首个 P0 正常停止；watcher child 与外层 shell 均实际 exit2，有独立命令/退出/原始输出收据，launcher/正常关闭均 exit0，未强杀、触发文件归档、锁释放。没有原生战斗终局，不是完整 A10 样本。山铜场景及消耗 fallback 的机制和合并门禁已经通过；本次通路未出现旧山铜具名失配，不能据此宣称所有历史分支或整塔一致性通过。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-17（2026-10-09 UTC）** | Vendor 路径相对评分账本被误当作原生未来驱动状态比较，重新捕获根更换评分起点导致失配 | `fixed-budget-performance-v3/paired-preparation-v4/attempts/combat039-v4-20261009T164300Z/after/logs/godot.log:1773`，`cacheMissAxis=semantics field=lifecycle`，预测字典含 Enemy/4=29，实际 null，offset49309；refinement targetRound4/generation61。日志 SHA `BA9C618C0C2F834EE28622C0B6725BB1632D37331EC4A6CE66D37EFFC0A52820`。独立真实 SpendResources/OnPlayWrapper Strike build0/RED1：双方敌 HP94、实付1能量/伤害6/native history一致，旧分支累计6，fresh root累计0；证据 `fixed-budget-performance-v3/state-diagnostics/p0-route-ledger-v1/old-native-repro-v1/proof.json`。 | **角色分离与v5合并完整门禁已通过；保存入口因19停止**：9项路径评分账本保留于具名 SearchMetadata，值、Fork 与 Vendor 评分读取不变；未来 Semantics 仍比较真实 HP/历史/最大生命/金币/药水/遗物已使用状态。真实付费 Strike→Heal4→Strike 的新旧根匹配、真实状态反例及13项实际 C# RED1→精确恢复 GREEN0已封存于 `fixed-budget-performance-v3/state-diagnostics/p0-route-ledger-v1/sealed/manifest.json`（SHA `9471D68354BB643C67576796FF527A312E7F7E2E2C143EE00A1565CB17DF4F31`）；focused事实19、既有规划16与诊断3均 exit0。原 LizardTail payload-only 故障仍 GREEN，已保留为 invalid，不计入13项有效故障。评分起点只读复核没有发现具名 refinement 链裸评分跨起点比较，未改权重或回填过去累计值。旧失配/正常停测记录保留，降低并发性能臂后移。 |
| **P0-SIM-18（2026-10-09 UTC）** | 原生重新捕获根遗漏会驱动后续动作的当前攻击史、VitalSpark 基准及已出技能成员状态；移除历史牌的匹配文本遗漏分支动态伤害 | 17的角色修复后，真实付费动作连续对照仍 RED，分别暴露 current last attack 为空、VitalSpark 基准为空、原生 Smog 生成牌与模拟不一致、HistoryCourse 移除源牌动态伤害差异。真实 HP 相同而牌史不同仍须拒绝，不能把这些字段归为路径评分。 | **批量根捕获与v5合并完整门禁已通过；整塔未验证**：MaterializeRoot 从 owned 原生牌史捕获当前/前回合攻击，保留被移除/消耗牌的冻结模型和动态变量；捕获有效 VitalSpark 总量及原生技能计数的成员状态。HistoryCourse 与 Smog 原生效果、Spark2→3及真实历史反例在上述事实19/13项有效故障内。新增导入适配8项实际 Python RED1→恢复 GREEN0，独立0.41生成419份源码仅两份 Vendor 规范化改变，ContinuationStamp 原字节不变。未扩大排除未来驱动字段，未改模拟评分或路线。 |

**本批并发观测准备**：`TournamentRun.AttachDiagnostics` 仅追加实际运行进程 CPU、request cap、options DOP 与有效 run DOP，High 原 `CPU−2/cap16` 映射及预算不变。生产 NoPotions/Potions 两阶段与正常请求入口 focused 实际 build0/run0，删除遥测读取的唯一物理故障 named RED1→源/六工件精确恢复 GREEN0；收据 `fixed-budget-performance-v3/parallelism-telemetry/final-ready.json`（SHA `9925F8663C2885F3918FEF31CB53A1482F0A0CAA712171B2FB1CBE5C00BC3F9F`）。测试进程 CPU16、并发14；不能当成尚未运行的新候选原生游戏值，也不能从历史 peakActive12 推断配置上限。后续独立降低 High 并发臂须以通过一致性的本批候选为新对照。

**2026-10-09 固定预算 v5 保存入口诊断（UTC；本地次日）**：`work/verification/20261009T174951575Z/` 生产/独立类型/一次完整807 PASS均 exit0，耗时14m35s；绑定出包与冻结 exit0，1268个非工具链输入保留于 `candidate/frozen-v5/`（receipt SHA `A9C8D22C114945672C458D7FFEDFDCC3A55F29940387C1131F629CBD33B6B633`）。本次新原生所有权 Steam PID29592/UTC ticks639271659689789590，从相同封存入口恢复；observer child/外层shell实际2/2，launcher/正常关闭0/0，未强杀，触发归档、锁释放、进程缺席。旧评分账本不再为首失配，未来字段仍按下述新P0停止；没有原生终局，不是完整A10样本。实际游戏发布的112条阶段观测均 CPU16/configured14/effective14/requestCap14，峰值活跃12不是上限，也不是CPU占用率。降低并发性能臂暂缓。

| 编号 | 观察 | 证据 | 状态 |
|---|---|---|---|
| **P0-SIM-19（2026-10-09 UTC）** | 原根的能力退役槽位与fresh根内部记录失配；发生在机器人第一阶段Enemy4被第二阶段Enemy5替换后 | `fixed-budget-performance-v3/paired-preparation-v4/attempts/combat039-v5-high-legacy-20261009T180550Z/after/logs/godot.log:1775`：`cacheMissAxis=semantics field=lifecycle`，预测 `_retiredRootPowerSlots` 含 Enemy/4 条目、实际为 null，offset49314；log SHA `6480B83E9B5090F010B035C2D317E0111AD59A820BB9D403BCADB4BF6244D41C`。round3缓存128步胜利，执行至15/128，round4复用时首次失败。 | **原生机制、v6完整门禁与本保存入口已通过；整塔未验证**：有效能力顺序及完整隐藏 payload 代替 root-origin 槽位表示；Vendor tombstone/Fork/指纹不变。仅永久死亡、离场、HP0且无有效能力的旧实体记录被投影清理；Reviving、pending、真实能力量/顺序/隐藏状态差异仍拒绝。原生 Stock 死亡生成→BOOT_UP→第二次付费 Strike、移除/重获与 Juggling 后继已通过；9项物理 C# 故障 build0/RED1→源码及六工件精确恢复 build0/GREEN0（0 invalid），6项新 Python 导入 guard 0/1/0。Facts26/State16/Tournament3最终强制构建及focused均0，收据 `fixed-budget-performance-v3/state-diagnostics/p0-power-origin-v1/sealed/manifest.json`（SHA `E80886442527CB3C7DE14E0E93A0E8678DEE3DFC981BCFD4EF3DE9ED70DA598F`）。0.41重导入419份，仅State/MonsterAi两份改变且字节一致；旧守卫与重型R2复用。v6保存入口原生AXEBOTS胜利、两次阶段替换后未见具名P0；这是单场诊断，不是整塔结果。 |
| **P0-SIM-20（2026-10-09 UTC）** | 强制改写怪物意图后，模拟攻击意图缓存仍保留旧攻击成员 | 原生付费 Strike 触发 Adaptable 的 `RESPAWN_MOVE`，原生/模拟 HP 均0、保留实体及既有未知 payload 拒绝一致，但后继语义 `_enemiesIntendingAttack` 预测含 Enemy/2、fresh为空。原始 RED 保留于 `fixed-budget-performance-v3/state-diagnostics/p0-power-origin-v1/revive-debug/power-origin-revive.log`，SHA `93BCAD0F369FC9074DDEE8D1CFD4C63DD6DD9EEBA53F652CA6219F67B14D2B39`。 | **最小缓存机制、v6合并完整门禁与本保存入口已通过**：0.41导入器生成 Force/Advance/initial roll/register/pending/离场的 branch-owned 意图成员同步；不读live，不将pending占位发布为真实攻击。原生 GoForTheEyes 后继验证旧 HAMMER 改 BOOT_UP 后不误施虚弱，实际删除同步的故障具名RED1→精确恢复GREEN0，包含上述9项。缓存保留于完整未来状态与指纹；Adaptable 未审计隐藏payload继续拒绝，不新增支持。原始复活RED保留。 |

| **P0-SIM-21（2026-10-09 UTC）** | 双尾鼠永久死亡后 refinement 完整未来生命周期中旧AI payload归属失配，已定位为归档投影（原日志区间未观察到实际召唤） | 首次完整A10开发尝试 `fixed-budget-performance-v3/full-a10-v7-development/results/attempts/20261009T200655775379Z-development-AR10B7M2X001-candidate/after/logs/godot.log:2572`，`cacheMissAxis=semantics field=lifecycle`，expected Enemy/4 `_callForBackupCount`，actual Enemy/5，offset31590；log SHA `79A824978DDC6ED9B2B2D4181FB68A8EF2CCDA31A21D81A7BAEF1F46536C3564`。 | **最小投影修复、定向原生与817项完整门禁已通过；Steam出现新的AI权重P0**：原生付费Strike使旧Rat永久离场，存活Rat AI计数原生/模拟均0，但旧完整Facts合同RED。新投影复用严格IsCompletedDeparted，保留活体、reviving字段；SCREECH及第二付费后继匹配，活计数0与3的CanSummon差别及合同拒绝仍有效。唯一实际R2 build0/RED1→源码/六工件精确恢复GREEN0，最后forcedRebuild/native0；两次fixture初始化invalid保留，750份Vendor C#不变。收据p0-monster-int-origin-v1 SHA 3FAE0B6D4CB566E9F3E62B41AD266E3F348C88209AF327B548A42B8A87B45BE8。floor6自动P0停止，root/launcher0仅为编排退出；无自然塔终局，不将其视为团灭或胜利。尝试保留在分母，正常关闭/noForce、flag归档、锁释放、进程缺席；floor5物理引用与资格失效属于既有保守拒绝，不能混作此P0。不得直接删字段或放宽R9；先原生验证召唤AI与旧实体记录，路线/权重/预算/High6保持。 |

**2026-10-09 固定预算 v8 保存第6层复测（UTC；本地次日）**：完整门禁 `work/verification/20261009T202849898Z/` 构建/独立类型/唯一无过滤完整817 PASS均exit0，13m46s；冻结1271源码、精确双DLL及本地包于 `candidate/frozen-v8/`（receipt SHA `39B212A677F0253CBA4DB84006961E4D7B29E006C4787537A16CB3EA6DCAE457`）。Steam owned PID11292/UTC ticks639271754340631605/exe与已安装双DLL独立匹配。`rat006-preparation-v2/attempts/rat006-v8-20261009T204325Z/after/logs/godot.log:1125` 首报下述P0，1198随后原生Rat获胜；胜利不抵消P0。直接Python与外层实际2/2，launcher/正常关闭0，未强杀、flag归档、锁释放、游戏缺席。94.77s保存战斗诊断不是完整A10样本；诊断尝试累计8，完整塔尝试仍1，留出未用。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-22（2026-10-09 UTC）** | 活双尾鼠动态随机招式权重的旧Static采样与fresh root失配 | v8第6层日志1125–1127，ai-facts SCRATCH_MOVE floatbits1065353216→1034594987（1→1/12），offset33488；log SHA `A6BBF6A8E5ADF14187188B9A83FBFA341473571FF92650C101280796BC9CF06A`。唯一活Enemy5 HP61、下一招CALL_FOR_BACKUP_MOVE；不是退役实体字段。 | **最小Facts修复、818项完整门禁与保存第6层Steam已通过；整塔另有THE_BALL数值P0**：真实付费Strike HP94/94→完整model.PerformMove(SCREECH含OnMovePerformed)→native RollMove与branch Advance均CALL_FOR_BACKUP、countdown0；旧合同仍同float首差RED1，修复后匹配，第二付费Strike HP88/88匹配。Facts只将Rat RAND四项投影为既有GetBranchWeight的当前branch有效值，不调用native lambda/重新捕获live Static；其它权重与真实活计数3反例仍严格拒绝。唯一实际R2构建0/RED1→源码及六DLL/PDB精确恢复0/GREEN0；最终forcedRebuild0/native0，750Vendor不变，28注册但本批只执行新增named。收据 `state-diagnostics/p0-rat-dynamic-weight-v1/sealed/manifest.json` SHA `EEC1D55C133ADE8F39D4F6A148FCC67E627AEF04D7267EA9B8AD0A50A64595AC`。最初fixture遗漏OnMovePerformed的invalid保留；实际CALL召唤动作未执行。路线/High6/权重/预算不变，未放宽R9。 |

**2026-10-09 固定预算 v9 原生与整塔开发（UTC；本地次日）**：唯一无过滤完整门禁 `work/verification/20261009T205451956Z/` 原始818 PASS，生产/独立类型/完整入口均exit0，13m47；ordinary为covered-by-full/null。绑定出包与冻结exit0，1271源码与双DLL封存于 `candidate/frozen-v9/`（receipt SHA `04A824D06027C39F7675369193CB4644DE06C1B3002EBDD5298E08A04ABAA6C2`）。Rat006保存入口原生获胜line1152、无具名P0；直接Python/外层0/0，96.11s。v8/v9单战战损均3/3/3/0，但v9多用1瓶Gigantification，顺序和预算完成数不同，仅属探索比较。完整开发AR10B7M2X001首尝试683.36s、此前7场原生房间胜利，在floor16 SOUL_FYSH按下述真实数值P0正常停止；四席存活、自然终局null。root编排0/launcher0不等于通关；owned PID3336/UTCticks639271771775333568/exe与双DLL一致，正常关闭/noForce、flag归档、锁释放、游戏缺席。只读归档2654项核对exit0，收据 `read-only-a10-v9-firstpilot/sealed.json` SHA `61A1E02039B1831CDE563FD65780322F4C9EC83689E4465BDAA35951E5981F30`。固定v9完整attempts1/P0=1、qualified wins0、未观察自然胜败；与v7不同版本不混算，保存战斗不计完整胜率，留出未使用。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-23（2026-10-09 UTC）** | THE_BALL 打出后遗漏永久伤害累加及ExtraDamageFromPlays，传球后新回合伤害预测10而原生20 | `full-a10-v9-development/results/attempts/20261009T211253352426Z-development-AR10B7M2X001-candidate/after/logs/godot.log:6605–6607`，field=H[1]，双方Increase10/手牌数5一致；log SHA `C1F17D19462D185DABBEE46213AA8C997E7137560E15A76F2D9B4EBD3C09BA2B`。本次fresh Boss入口为combat-016，非更早combat-009。 | **原生/R2、820项完整门禁与保存Boss Steam获胜已通过；完整v10另有死席ready P0**：旧paidplay RED1，原生Damage20/Extra10而模拟10/0，HP90/90与传球owner一致。41导入生成既有generic damage之后的两字段增长，COW MutablePreview，再沿既有传球；first20/10→second30/20、HP90→70、两次draw/队友owner/共享targets RNG/完整planning/Fork及真实降级一致，final forcedRebuild0/native0。唯一物理C# baseline0→撤两growth写RED1→源码/六DLL-PDB精确恢复GREEN0；一个新target guard实际0/1/0，419独立生成仅Registry规范化变化且目标字节相等，750Vendor中749exact原v9。PartySetup已有换行差异未覆盖；不冒称所有生成文件字节相等。sealed-v2/manifest.json SHA `071ECE9B5E933E8D51FD1FC14C7AD6181309083BCF1074FF8AD2FF2180B81BD5`；夹具moving owner/NRE、大小写R2预检、S1路径和首次seal前缀核验失败均保留，未计新增P0或有效R2。四授权文件原子overlay，C# lane closed；完整合同、路线/High6/构筑权重/时间预算不变，胜率未验证。 |

**2026-10-10 v10 闭合实机与新P0（本地日期；UTC10-09）**：一次无过滤完整门禁 `work/verification/20261009T215208493Z/` 原始820 PASS/0 FAIL，生产/独立类型/完整入口均exit0，13m35；ordinary为covered-by-full/null。绑定出包/冻结均0，1271源码、Core23D3A5E6…、Kernel4024D36C…保留于 `candidate/frozen-v10/`（receipt CECD763DB82CC5CFF2F58E0D0C8D430352A872C453EDF683EF176F9C0FC2016B）。Fysh016显式分配探针保存入口171.437s，原生胜利2141/P0空，observer独立child0、launcher0、root99507实际工具0，正常关闭且两个flag精确归档；审核1379项/seal400C3F11…867E33。保存战斗不计完整A10分母，也不当正常性能对照。随后无探针完整开发AR10B7M2X001实际1435.36s、11场原生胜利，floor28仍一席39HP时按下述新P0正常停止，三席已阵亡、自然终局null。root94515编排0/launcher0不等于胜利；owned21320/UTCticks639271810010765321/实际exe及双DLL已核，CloseMainWindow/noForce、flag归档/锁释放、进程缺席。日志SHA `5CA17EB183590D6ED8411DFCCC8FA8D8B0F022D24EA33FE0B4D968280FB8D258`。完整固定v10 attempts1/P0=1、qualified wins0/natural results0，开发余2待定，留出未动用；不与v7/v9混算胜率。闭合审核2822项通过（read-only-full-v10-p0，manifest351D6D5C…173E04B）；第26层净损91HP、25次成本中19次declined，全部19次回退均在此层。全日志185次Tools警告、原生第5回合实际打出一次（11071行）；空cursor存在正常未来菜单捕获路径，警告本身不直接视为unsupported或新P0。Ready P0晚于三席死亡，不解释先前战损；GC/分配为进程窗口观测，非FPS或该牌独占成本。

| ID | 问题 | 证据 / 现状 | 修复方向与验证 |
|---|---|---|---|
| **P0-SIM-24（2026-10-10本地）** | 阵亡席位共享换回合后的ready标记失配：原生true、模拟false | `full-a10-v10-development/results/attempts/20261009T221636720053Z-development-AR10B7M2X001-candidate/after/logs/godot.log:12137–12142`。原生三死席在start turn设ready；首失配12140 seat93217/False/False/7/0/0→True/False/7/0/0。字段为NetId/IsReady/CanAct/playerTurn/cardsPlayed/energySpent，7不是HP，非死亡本身漏记。 | **原生与唯一物理R2已封存并合入；完整门禁825通过；Steam复测旧ready差未再报，出现独立P0-SIM-25，整塔未验收**：KernelTurns按结算后的分支HP恢复死席及extra非参与者ready，保留严格Facts/CanAct。实际Host Damage80死亡→原生phase/StartTurn→完整Facts/Fork/后续paidStrike，normal/extra/LizardTail即时防死三组通过；R2只撤dead谓词，build0/RED1/精确两源六DLL-PDB恢复GREEN0，finalbuild及四组focused0（state-diagnostics/p0-seat-death-v11/sealed，manifest347FCF1C…A549BB3）。独立3465项audit0；两file精确合入收据v11-root-delivery/state-integration-receipt.json。未验证延迟玩家复活或真断线；既有pending敌复活保持。不能解释此前三席死亡或宣称整塔已通过。 |
| **P0-SIM-25（2026-10-10本地）** | ENTOMANCER028第7回合历史攻击事实域失配 | frozen-v11保存入口归档raw2136首差`card-facts`→首Dexterity/applier，不是敏捷数值错误。真实付费攻击/消耗/移除→三席死亡→连续两次普通回合复现死席previous残留；额外回合也复现非参与者previous提前轮替。 | **原生及物理R2已封存、精确合入；交付完整门禁与Steam待执行**：按实际Advance席位推进own-turn pending/previous；effective-current按原生round/side/全席turn快照判断。额外入口捕获待推进牌，纳入完整Semantics/physical/Fork/fingerprint；保留未知SLAM generic-attack资格风险，不忽略字段。详见`v12-root-delivery/history-integration-receipt.json`及所绑定sealed原生/R2。路线、High6及预算保持；旧诊断无终局，不进完整爬塔分母。 |
| **P0-SIM-26（2026-10-11）** | F18岩石碗虫动态POST_HEADBUTT条件选择的旧Static事实与fresh root失配 | frozen-v13完整开发AR10B7M2X001，`full-a10-v13-development-v2/results/attempts/20261011T115559814997Z-development-AR10B7M2X001-candidate/after/logs/godot.log:8447`：ai-facts POST_HEADBUTT DIZZY_MOVE→HEADBUTT_MOVE，targetRound3；10/13为字符串长度，不是权重。看护按P0正常关闭，planned1/natural0/P0fail1/wins0，不能判团灭或通关。 | **当前DLL原生复现与修复/R2已封存**：真实付费四席STUNNED入口→回调→HEADBUTT精确重现10/13，采用既有resolver有效条件投影；未来HP/RNG/条件变化与unknown fallback保持严格。完整证据见 `work/v13-f18-ai-drift-native-20261011-v1/sealed/manifest.json` 及 root v14 integration receipt。原有未覆盖HEADBUTT/environmental/skip控制保持未qualified，不豁免、不删字段。新默认完整门禁及Steam结果以新收据/状态为准，旧834门禁不覆盖新输入。 |

**2026-10-09 固定预算 v6 保存入口复测（UTC；本地次日）**：唯一完整门禁 `work/verification/20261009T185840969Z/` 生产/独立类型/无过滤814 PASS均exit0，14m36s；绑定出包及冻结exit0，1270个非工具链输入封存于 `candidate/frozen-v6/`（receipt SHA `B62F55BEF61A021571055848924340AB4A288714698D83D384838B797CAF2831`）。Steam owned PID32780/UTC ticks639271700974915732/exe独立核对，入口四席A10及High14/6400ms保持；`paired-preparation-v4/attempts/combat039-v6-high-legacy-20261009T191404Z/after/logs/godot.log:2712` 原生AXEBOTS获胜，log SHA `C462FA823745646FC11FAD2C49526F6F9081486CF6B143F88FD28BE3A282FD02`，未记录具名P0。观察器child/外层0/0，launcher/正常关闭0/0，未强杀，触发归档、锁释放、进程缺席；耗时577.36s。单场诊断不进入完整A10胜率分母。下一性能候选仅降低High并发到6，须新完整门禁/同入口成本与实际质量对照，不以此胜利宣称卡顿或50%目标达成。

**2026-10-09 High6 v7验证与首个整塔尝试（UTC）**：唯一门禁 `work/verification/20261009T193839222Z/` 原始816 PASS、三阶段exit0，13m50；绑定包/冻结exit0，receipt SHA `E0510F2A8E7B2F74BE09D6422F4CB5A31AC5E66F0B3D844BAB4318639E7B68BE`、source1271。保存AXEBOTS原生胜利line2719/log SHA `6CC6F6E6D790A49F057E095BF92806409879E483C1415B4250BFD2DFEE5A15E8`，实际CPU16/cap6、四席战后HP54/58/42/18；与v6相比54主动搜索alloc中位1827→1747MiB、GC2818.5→2669ms、wall p956845.095→6480.265ms，新增1瓶Speed使用，不能称资源等价或性能已解决。成本pair SHA `B9A1F5664816A59FD052E76949C13A7741D83A4801A094443D07B8177201F543`。整塔AR10B7M2X001的唯一计划尝试持续217.42s，在floor6因21自动正常停止，attempt SHA `2F77BC1CA6F7D725805D7417AE54C4C31539628EF24ECBA4CAB7A04F88D5E1C6`；固定v7的attempts1/P0=1，natural win/loss均未观察、qualified wins0，不能估计总体通关率。

**P0-SIM-6新增现场（2026-10-08，机制已修、待实机复测）**：1JJ0XFWWNLTC实验体R4日志56063的`hp expected49 actual51`，已用A10四席Tank/Guarded真实同击死亡结算复现形状：原生与修复均51HP，故意逐席即时死亡结算为49HP；改复用既有批量Damage事务。多目标、跨击及荆棘先杀攻击者等对照/R2通过，证据 `work/latest-run-repair-20261008/model/`；四门 `work/verification/20261007T215935141Z`。这是该结算机制的原生证明，未恢复整份历史RNG/全部前序动作；旧RLAE8Y8MA4YH知识恶魔2HP根因仍未恢复，不关闭旧条。

**收尾纪律**：修完前，任何胜利记录在报告里必须标注"未通过逐席一致性校验"（本局即如此）；不得用"多打一张逃牌"之类局部反事实替代模拟一致性的修复。

---

**P0-SIM-27（2026-10-12）跨回合选牌答案未随已结算动作部署**：frozen-v15 全 Bot A10 开发种子 `AR10B7M2X001` 在 F31 知识恶魔 R3 因 `H[3] expected DEFEND_SILENT actual THE_BALL` 正常停止（FULL15 归档日志13581行）；attempt1/anomaly1/qualified wins0，无自然整局结局。随后构造四席真实 Tools 菜单，独立原生与9600ms模拟弃防御，零截止模拟弃刀刃之舞，原生/模拟四类RNG保持；证据 `work/v16-F31-hand-drift-private-20261012-v1/sealed-v1/manifest.json`。**机制分叉已证实，历史R3完整同根尚未恢复，不据此关闭历史手牌失配。** v16 journal/replay/native lease 在具名原生及R2验证中；完整门禁、双DLL部署和Steam回归仍待完成，未豁免Facts或P0观察器。

## 6. 偏离登记表（上游没有的行为）

**2026-10-25 混合局主线程修复（本地候选，用户授权）**：实盘捕获、执行校验与提交仍在游戏线程；冻结根的完整锦标赛、参考/策略/Joint、药水比较和后台后缀重放由单一低优先级控制线程持有，独立于原 rollout 池，队列有界且主线程不等待。Bot 游标前进不再次发出同一真人 revision 的任务；真人结束后可选优化退场。完整搜索早期的真实已结算物理前缀保留为估计提案，输入取消撤销质量而不重新搜同一前缀；必要预算空请求仍可有界恢复，已获资助而无原生候选的失败走既有具名 decline memo，不按计时器重启。全 Bot 原执行/未来优化与全队提交钟不变。新增帧区间/主线程 Tick/捕获成本观测不能当作已完成实机流畅度验收。Vigor/Gigantification 成对攻击状态须清理攻击开始时已参与、随后因反伤死亡退场的监听者；源码层适配另记 Vendor 表。来源：用户2026-10-25卡顿诊断后的修复授权，诊断 `work/mixed-stutter-audit-20261025T045546Z/`。

收尾补充：药水库存、权限、数值及已捕获目标的原生规则必须在捕获时冻结；完成结果的药水比较/无急救结果跨帧单次消费。原生最终可执行性校验仍在游戏线程，新根重新读取真实状态。


**2026-10-25本地混合局活性修复（用户授权，只打包至mods，未发布）**：必要前台搜索与可选后台回合额度分离；请求本身保持硬截止。无候选截止退避后允许必要修复，不得永久Pending饿死可支付动作。混合局无药胜利及时部署，打断的完成无药方案仅保留历史物理提案并撤销质量证明；集火/回合/控制变化隔离。全Bot原优化与1.5秒团队提交钟保持。原因是0.39.4珊瑚精英战实际出现9600ms额度耗尽后的3ms零候选停等。此为调度偏离，非原生规则或新增胜率结论；本轮必须完成对应原生/R2及一次完整门禁后部署，结果记本地收据。

本轮架构登记（用户 2026-10-09，实施与证据进度记录于 `work/architecture-refactor-20261009/`；下表不声明门禁或胜率已达标）：

| 偏离 | 理由 | 来源 |
|---|---|---|
| 混合桌提前计算并保留结束动作，原生结束准备信号不改变材料版本或取消完整搜索；实际侧/回合、真实卡/药/撤销/未知输入仍更新根。仅有明确原生被动准备记账的前缀可以保留收尾，始终撤销旧质量。死亡目标仅对同一实体牌按当前合法目标局部修复，无合法目标的复活阶段仍正常收尾；普通外部输入只退休混合剧本的旧未来回合合同，卡牌菜单仍实际校验，完整回合开始租约本地及时退休，真实选牌分歧仍严格清理。 | 上游没有并发真人席位；结束按钮曾撤销已计算工作，目标死亡与租约回调窗口曾整段弃用合法续打。保留执行并不代表完整比较胜者或新根胜利；全 Bot 严格执行、未来优化及全队提交钟不变。 | 用户2026-10-25明确修复授权；本地完整日志审计 `work/mixed-quality-fix-20261025T083140Z/`，MixedReadinessClosing、MixedChoiceLeaseRetention、MixedStableScript原生/R2回归，验证与部署另记收据。 |
| 混合桌真人结束前后保持完整锦标赛（portfolio、joint、80回合/600步、药水目标与并发不降配）；合法旧剧本稳定续打，真人已结算输入只降级质量并合并后台实际根比较。只在同根严格更好、未占用准备槽时换接；单次消费结果及原生结算游标，前后台共享回合计算账/各请求绝对截止。混合冷开局分帧枚举与完整流水交替；截止保留完整原子前缀/已完成基线，未获候选时当前根具名等待且不重赠预算/不切legacy。全Bot保留未来回合refinement。 | 上游不支持真人多人输入；用户选择0.39.0稳定续打的响应取舍，避免每张真人牌或四步窗口迫使前台重搜。提交仍校验控制席、物理牌、费用/目标及当前选择合同；旧胜利不再冒充新根证明。 | 用户2026-10-24明确V2授权；KernelCombatPlanner.Mixed/MixedReplan/Submission、真实Poll/队列/原生动作回归。 |
| 卡牌按全队最近一次成功原生入队计时：默认Pro/Cheated在真人行动时至少1.5秒，Flash 0.5秒；真人结束后取消人为间隔，优先提交已备卡后再推进可选后台。药水、EndTurn、失败入队不改卡牌时钟。 | 上游部署器等待原生动作；本地多人需稳定跨席穿插节奏、避免思考时间抵扣造成连发，并在真人结束后减少额外等待。初次搜索、原生动画/菜单仍可能延长间距。 | 用户2026-10-24 V2节奏要求；CombatPacing、BotRuntime生产TickCombat/实际ActionEnqueued回归。 |
| 合成席位宝箱使用显式房主授权、房间/幕及原生前后状态绑定的跨端执行和幂等ACK；房主先执行原生奖励再广播，重连载入后态直接ACK；退出注销消息并隔离旧异步回调。本机托管真人非共享事件走原生本地广播；自动Ready握手不视为主动真人输入。 | 原生消息从真实发送者推导玩家，合成Bot没有客户端；所有端必须推进同一奖励RNG/金币且不能重复支付；原生前后态使未携带内存回执的重连快照也能去重。 | 用户2026-10-24混合桌修复授权；BotTreasureRewardSession、BotEventDriver、ExternalCombatInput；双端原生/选择合同回归，物理双客户端验收另记。 |
| 贡献包首张关键牌获取优先：战士 JUGGERNAUT（势不可当），多人 FLANKING/KNOCKDOWN；奖励和永久加牌允许越过普通负收益门槛，商店在可负担删诅咒之后购买最低价首张关键牌。NIGHTMARE 恢复普通估值。 | 忠实合入贡献包实际 ID 与政策；DOMINATE（主宰）和 JUGGERNAUT 分开，原包中文误称不扩展到另一张牌。 | 用户2026-10-23明确要求合入 key-card-acquisition-20261022 包；KeyCardAcquisition、BuildValue、BotShopPlanner；来源及限制见 docs/COMMUNITY_PACKAGE_20261022.md。 |
| 构筑推演增加格挡事件触发伤害与当前血量风险，Add/Remove/Upgrade 加有符号联合队伍边际，房间入口刷新队友快照；保留六回合压力、三回合投资及有限风险成本近似。 | 贡献包的通用构筑增量影响全部永久决策；沿用候选模型，不将有限血量惩罚宣称为完整生存模拟，不扩展原生规则覆盖。 | 用户2026-10-23合入授权；ConstructionHand、SurvivalValue、TeamSynergyEvaluator、TeamSynergyMath/Native；CommunityPackageScenarios。 |
| 复制类技能参与投资与同步受控队伍续演，复制选择允许 step-cap 残值；共享铺垫和未来减益收益扩大，回合末比较固定 root 机会窗口；已完成无完整路线时直接部署现有结果。 | 合入贡献包规划策略，沿用既有共享停止/预算及原生选牌合同；一般搜索仍可 CONTINUING，不增加网络或真人代选范围。 | 用户2026-10-23；KernelChoiceContinuation/KernelInvestmentProbe、KernelTeamCoordination、KernelEndTurnRun、KernelCombatPlanner；验证以本次新收据为准。 |
| 已结算结束回合及隐式换回合卡牌/药水携带实际采用的完整回合开始选牌合同；重放使用同一答案，Core在原生setup前激活独立未来菜单租约，核对席位、回合、来源、完整菜单及物理occurrence。实际动作执行后才生效；原生完成各自退役，失配/取消/真人输入清理旧计划，客户端失败通知主机，ACK等待有界。 | 多人跨回合菜单晚于当前动作；重放或实际执行重新估值会因预算不同产生不同手牌。上游单席自动cursor没有本地原生/网络动作合同；不增加搜索池或预算，不代选未托管真人菜单。 | P0-SIM-27；KernelTurnStartChoices、全部Action生产者及Core BotTurnStartChoiceSync/BotRuntime/CardChoicePatches；四席Tools原生机制与实际R2记录于 work/v16-turn-start-contract-native-20261012-v1/。完整门禁、Steam与真实混合网络另验。 |
| 精确回放短期合同保留其已结算敌方终点；Joint及药水续算显式执行共享的下一玩家setup序列，将新增答案附回最后实际动作。额外回合按owner turn区分，终局和既有轮数上限保持。 | 上游没有本地已部署未来菜单合同；用较大caller horizon隐式推进会拒绝短期合同。精确验证与继续预测分开，沿用同一原生序列、通知隔离及预算，不新增搜索池。 | 原始JointFunding40步/2轮→80轮末步拒绝具名复现；四文件有限逆差异审查 work/v16-horizon-replay-fixed-source-review-20261012-v1/audit.json，原生与实际两项R2 work/v16-horizon-contract-native-20261012-v1/。完整及Steam验收另记。 |
| 当前回合药水救援使用具名ForecastCausal重新生成自身结算合同；与严格ReplayPlanned共享卡牌身份、目标及即时选牌处理，覆盖End与卡牌/药水隐式推进。严格回放默认及其全部守卫保持，其他审计/缓存消费者不迁移。 | 已发布计划的未来合同要求下一玩家setup，有限救援只需要当前敌方结算；混用使救命药在试药前被拒绝。新预测重新记录实际采用的合同，沿用原max1、64步、切片及共享截止，不增加搜索池。 | 原CachedDex/6HP/Dex/Defend用例具名失败574 PASS门禁、原断言14/TurnStart9/Joint80与真实撤调用build0/RED1、1154源码/10工件精确恢复build0/GREEN0，证据 work/v16-cached-dex-contract-native-20261012-v1/；新完整及Steam结果另核，任意长预测/隐式推进的原生覆盖不据此扩大。 |
| 多人搜索按实际根请求绑定绝对墙钟截止时间，Tournament/Joint/药水/重放与可选选牌续演共用请求；异步尾继承该请求，复用工作线程逐任务安装上下文，原生事务结算后停止新增试算。已完成的胜利保留既有数值，未知或未完成分支不因此变成终局。 | 上游请求账本面向单人；四席任务不能因重捕获遗留旧工人、按并行任务重新获得预算或合计CPU替代用户等待时间。既有无Request夹具兼容预算不改变，线程不能沿用创建者或上一任务的旧截止时间。 | 用户2026-10-09分阶段架构计划；KernelDecisionRequest/KernelSearchBudgetScope/KernelSearchWorkPool及对应run，KernelCombatPlanner；本轮证据 work/architecture-refactor-20261009/fixed-budget-performance-v3/。 |
| 混合桌只观察真人已结算动作；混合续用及跨回合 refinement 同时绑定战斗、控制/同步选牌合同、完整已支持状态、原实体顺序及捕获的隐藏机制事实。完整合同失配后清除旧终局和检查点所有权；旧路线从新实际根有限原生重放后才能成为候选，沿用当前前台请求余额。 | 合法性不等于新盘面的路线质量；排队/取消动作不能作为已结算输入；未知事实只停止缓存复用，不拒绝合法原生动作。现有R9原生/实机差分责任不变。 | 用户2026-10-09；KernelInputObserver/KernelPlanningState/KernelPlanContinuation及KernelCombatPlanner.Mixed、Refinement。 |
| 强制消耗的普通牌廉价回退保留既有后续战斗移除价值顺序；当回合不可支付不再把核心牌排在基础牌前面。弃牌仍保留当回合的支付及格挡政策，状态/诅咒/无害虚无的既有规则保留。 | 共享截止只允许首个原生分支时，不能依赖后续完整续演纠正错误排序；消耗与弃牌的机会成本不同。不新增 Fork、候选数或独立预算。 | 用户关于低价值牌仍在却烧核心的观察；KernelRelicChoices/CleanupPriority。独立原生 BurningPact、TrueGrit+ 与 Survivor 对照，9组focused、两项实际RED/GREEN，`fixed-budget-performance-v3/choice-budget/exhaust-fallback-final-evidence.json`；合并完整门禁与实机待完成。 |
| 构筑机制事实与有限原生采证：真实类型/动态变量/生命周期来源，逐轴并排留存原生结果与廉价proxy结果。 | 只显式离线有界调用，不随每次奖励跑整战；未有采证结果前不自动改默认权重，不改变路线。 | 用户2026-10-09分阶段架构计划；ConstructionMechanismFacts/ConstructionNativeCalibration。 |
| A10预先固定开发/留出种子、包/双DLL/游戏/路线/配置身份，按计划与自然结算分别报告分母。 | 卡死、崩溃、中断和未启动不能被自然结算分母隐藏；旧批次缺少启动前实物绑定不能事后伪造。 | 用户2026-10-09；scripts/a10-benchmark.py及benchmark清单。 |
| gameplay克隆遵循原生AfterCloned身份重置；Shuffle按当前分支所属牌堆重排卡与附着监听，保留非卡槽位和pending-choice边界。 | 捕获/COW克隆与游戏生成克隆是不同生命周期；多人动态生成牌不能使用冻结注册顺序。未知第三方监听交错没有被本轮证人证明。 | 当前游戏0.111.0原生逐行对照，2026-10-09 P0-SIM-14/15；Vendor两处精确适配及SzgNativeCardScopeScenarios。 |

> **偏离分两张表，别写错地方。** 判断标准是**偏离发生在哪一层**：
>
> | 偏离是什么 | 登记在哪 | 由谁施加 |
> |---|---|---|
> | **运行期行为**：上游没有的动作、拒绝、边界处理、多人语义 | **本表**（下一行起） | 手写代码 / 内联钩子 |
> | **`Vendor/**` 源码**：导入上游时对源码本身的改动 | [`src/CoopBots.Kernel/VENDOR_DEVIATIONS.md`](src/CoopBots.Kernel/VENDOR_DEVIATIONS.md)（D1–D4） | **导入管线**（`import-combatsolver-kernel-41.py` 内联钩子 + P1 偏移表） |
>
> 关键区别：**Vendor 的改动会被重导入覆盖**，所以那一边的每一项都必须由导入管线本身产生，
> 手改再补登记是**不够**的。本表的偏离是手写代码，重导入碰不到。
> 两张表都要求"任何上游没有的东西都能找到出处"（S6）。



| 偏离 | 理由 | 来源 |
|---|---|---|
| **商店删牌索引保留完整卡组身份**：合法列表只用于筛选，BestRemoval将ordinal解引用为原deck index，评分/Choice/SecondValue及实际RemoveFromDeck沿用同一对象。 | 不可移除卡造成合法列表缺口，旧ordinal被错误当作完整卡组位置。真实前置Bane、唯一末位未升Defend及前一Bash的原生菜单/支付100/删除/Used/两RNG和删后边际证明，保持原估值与优先tier。v9四次现场删除在gap前，不能归因该bug。 | 用户持续局内/局外修复授权；shop-gap-native-v1 sealed-report-manifest SHA488349E95F318F836EBC89D3398F93821E464F086C434C30155CEF67A6C121D0。唯一actualC# RED1→源码/六工件精确恢复GREEN0；final-focused forcedRebuild/native0，humanAutoPilot在夹具内保存恢复。同批完整门禁/Steam待执行。 |
| **THE_BALL 多人永久增长补偿**：既有generic伤害之后，分支MutablePreview同时累加Damage与ExtraDamageFromPlays，保留传球、owner与共享RNG路径。 | 上游0.41的Rampage处理同形两字段，TheBall却只有已有result-location镜面，漏原生增长。当前DLL两次真实实付/draw/降级/Fork证明数值与账本皆未来驱动，不能忽略手牌伤害失配。 | 用户持续原生正确性授权；P0-SIM-23具名native/唯一C#R2/单target guard，S1逐行差量；经41导入生成，源码适配登记在VENDOR_DEVIATIONS末行。sealed-v2 SHA071ECE9B5E933E8D51FD1FC14C7AD6181309083BCF1074FF8AD2FF2180B81BD5；820项完整门禁及保存Boss Steam WIN/P0空通过，完整v10在第28层发现独立死席ready P0。 |
| **双尾鼠动态招式权重的有效Facts投影**：仅Rat RAND四项复用既有分支GetBranchWeight计算，用于完整未来比较，原Static/cache/Fork/指纹保留。 | 上游根捕获将native weightLambda的当时值存Static；本地运行期已按branch计数、空槽和队友意图重算四项，旧采样并不驱动这四项未来。真实SCREECH/RollMove证明不同捕获来源有效状态相同；其它Static和活计数差别严格拒绝，未知权重不调用native fallback。 | 用户持续实机修复授权；P0-SIM-22原生RED→GREEN/唯一物理R2，`state-diagnostics/p0-rat-dynamic-weight-v1/sealed/manifest.json`；完整门禁818 PASS与Rat006 Steam WIN/P0=0已通过；整塔floor16发现P0-SIM-23，选中召援不代表实际spawn已验证。 |
| **已永久离场怪物私有整数状态的完整Facts投影**：复用严格离场资格，仅在逻辑未来视图中排除旧owner的 `_monsterIntStates` 归档项，保留运行存储、Fork和指纹。 | 上游分支缓存留存旧owner，原生重新捕获只收当前实体；真实Rat付费死亡证人显示活计数一致但归档owner起点不同。活体备份计数驱动召唤，仍严格比较，pending/reviving保留；不是修改怪物行为或任意忽略状态差异。 | 用户持续实机修复授权；P0-SIM-21原生RED→GREEN/1物理R2，`state-diagnostics/p0-monster-int-origin-v1/sealed/manifest.json`；完整门禁817 PASS已通过；floor6 Steam旧归档首差未复现，但另有P0-SIM-22，不能推断其它分支通过。 |
| **High 并发上限6的独立性能试验**：大厅High映射仅将请求DOP上限设6，普通/药水阶段继承同一请求；其它档位、全局池、搜索时间及策略不变。 | v6已原生赢下封存AXEBOTS且无具名P0，首次预测胜利前仍有高分配/GC与未完成参考。此为固定预算的成本探索，不是原生游戏规则、胜率证据或已证明性能收益；真实后果需新候选同入口与完整塔检验。 | 用户2026-10-09持续实机优化授权；KernelCombatPlanner本地大厅适配；work/architecture-refactor-20261009/fixed-budget-performance-v3/high-dop6-preparation。初次冷Restore输入集guard失败保留；新冻结输入的唯一实际映射R2已RED1→精确恢复GREEN0，最终定向与独立fixture集成构建exit0，收据fixture-integration-v2 SHA D1276C3AE08B23C35E6CC4E74421EFCF3FE3410E8C1B71080DA995AE6031B886；v7唯一无过滤816 PASS、绑定出包/冻结exit0（source1271）；保存入口原生WIN/P0=0且同战后HP，GC中位2818.5→2669ms但新增1瓶Speed消耗，仅准探索性开发pilot。首个整塔floor6生命周期P0停止并保留尝试，不称性能解决或50%达标。 |
| **牌序探测后态来源统一校验**：轻量带选牌候选不 Seed 或直接作为 proposedFirst；正式重开菜单比较，无选牌候选仍复用；三个入口采用同一规则。 | 轻量弃核心牌后的状态进入正式依赖比较，即使后续正式选牌能保留核心，也会影响首动作；来源混用必须在缓存入口修复。配额不扩张，有限续打不保证最优。 | 用户2026-10-08；KernelRollout；旧LatestRunSearch夹具字节未改，真实RED首SHIV→精确恢复GREEN首Survivor/弃Strike。work/szg-repair-20261008T124624/combat/cheap-choice-fix。 |
| **资源机会成本与合法牌序保留**：原生AB/BA第二张不可支付时各侧保留首张或合法两牌较优结果；资源返还使用行动前价值并以本回合实际支付限制，结束/跨回合不返还，消耗通路不重复返还。 | X费不可支付后续曾丢掉另一合法次序；普通与消耗路径对同一实付能量估价不一致导致Cascade误烧诅咒。保留原始Bane守卫及支付、结束席位、跨回合对照。 | 用户2026-10-08；KernelSequenceOrder/KernelTacticalPolicy/KernelExhaustPolicy/KernelSession.Evaluation；work/szg-repair-20261008T124624/combat/cascade-fix；6故障及2同故障管线witness，不合并夸大独立数量。 |
| **原生依赖优先与生成牌可支付收益**：有限配对优先实际敌方PowerVar和跨席位依赖，非印刷熔岩之拳仍保留候选；新生成的可支付手牌作为真实当前回合机会。单目标致命提示不跳过AB/BA，完整原生胜利仍保留快速返回。 | 旧非基础攻击占尽有限setup探测，刀刃之舞被保留策略错误过滤；单目标击杀短路消耗了第二敌人所需的痛击。旧战略、五小刀及双目标胜利守卫保留。 | 用户2026-10-08；KernelSequenceOrder/KernelExhaustPolicy；work/szg-repair-20261008T124624/combat/tail-fix-v2；3实际故障与1印刷PowerVar防回归对照，各自真实RED/恢复GREEN。 |
| **完成参考后原生重放药水前缀与失效尾部重算**：保留已完成参考，在同一剩余deadline内验证不同瓶子开场；每步在私有attempt上执行并核对完整实际菜单，旧目标/菜单失效从动作前owned状态重新续演，仅真实terminal加入比较。 | 正式菜单正确续演后整条重搜成本变高；药水导致Boss提前换阶段不能继续用消失目标，也不能丢掉真实前缀或把截断算完成。复用现有原生script replay而非假定旧脚本有效。 | 用户2026-10-08；KernelPotionContinuationRun/KernelTournament；本轮model/late-diagnostic；原3200旧Quality两轮至少2真实结局/2不同开场及全script重放，固定预算允许原子动作结束处的小幅墙钟超出。 |
| **同次搜索完成参考的联合复用**：仅同一Tournament的真实终局Kill与完全相同的有序行动席位域可透传；外部参考、截断和药水不推断来源，独立策略仍从初始盘面重放。 | 原生终局已在外层完成后，联合搜索在余下1ms重复整条Kill，使有效参考诊断变成continuation-budget；复用证据而不增加rollout或prefix计数。 | 用户2026-10-08；KernelTournament/KernelJointTurnRun；本轮model/owned-card-reference-proposal，具名原生及来源/域故障注入；最终原版四门。 |
| **原生采样顺序不变的评分数组与风险引用索引**：AttackGain的局部数字用索引数组；不可变历史前缀缓存RiskEntry对象引用，每次照常分类，可变尾表继续原始版本检查。 | 原3200ms长战比较被分配与重复全历史扫描挤占；不缓存native getter结果，不改变调用次序、分支数、PairLimit或预算，Fork与实时分类保持原生语义。 | 用户2026-10-08；KernelTeamCoordination及Vendor History/PredictionCoverage；本轮perf-audit/attack-gain-arrays与combat/risk-prefix-coverage-proposal，原生序列/异常/尾表可见性R2及419源码导入复现；最终四门。 |
| **药水当前回合正式修复后严格续演、先单瓶后组合**：明确原生失效目标、完整实际菜单变化或出牌事务前unplayable时，每个真实round/side segment最多修复一次；新回合setup后全party原生checkpoint一致才恢复参考后缀。缺元数据、额外回合、其他失败及无法匹配仍保留完整正式尾部。保留全部合法单瓶与最多四瓶组合，先完成单瓶对照。 | 失配后重复重算长尾和组合优先占满原3200ms预算；unplayable独立识别手牌/支付/行动可执行性，不假定目标消失。修复前缀仍cutoff，仅真实终局计为完成。原80回合及每个药水候选累计600正式续演动作约束不变；初始参考、重放和选牌探针不计入600步，整个搜索受既有deadline限制。 | 用户2026-10-08；KernelRollout/KernelTournament/KernelPotionContinuationRun；本轮model/potion-round-repair-v1与v2，全party终态/逐动作/9RNG及调度原生证人，具名物理R2，旧Quality字节未改；最终四门。 |
| **空消耗机会成本无分配捷径**：先用原生Original身份确认没有新消耗卡，直接返回精确+0；真实新增消耗的旧估值尾部原字节与getter顺序保留。 | Survivor/Prepared等不新增消耗的选牌路径反复构建HashSet和整队临时属性投影；仅消除无用计算，不缓存原生getter结果。 | 用户2026-10-08；KernelChoiceContinuation；本轮perf-audit/empty-exhaust-opportunity，16原生全值/状态/菜单/9RNG对照、2物理故障3验证，真实子错到父套件；最终四门。 |
| **多人开始回合按原生共享屏障执行**：冻结每席控制；Bot和被接管真人的同步选牌任务在Side前完成，普通真人暂停后跨Side恢复；一次Side/Orb后逐席完整native帧与AutoPrePlay。实际后续菜单失配时重捕获比较，不沿用采样手牌token；比较额度耗尽仍用实际菜单合法选择完成。 | CombatSolver以单席为主，本局后席水银沙漏晚于提前城垛，产生敌HP差3；共享Shuffle的生成也必须逐席、逐卡创建插入。保留pending菜单/历史/死亡校验，不因菜单数量拒绝合法动作。 | 用户2026-10-08修复并重新部署；KernelCombatCapture/KernelTurns/KernelPartySetup及Vendor.PartySetup；SzgModel/Combat/ChoiceRepairScenarios，work/szg-repair-20261008T124624；最终门禁/部署读数以本轮报告为准。 |
| **赌博筹码比较完成的原生手牌**：0到全手数量均留代表，最多12候选，共384续演步骤，继承依赖排序而关闭嵌套整手搜索；实际选择和主分支RNG保持原生。 | 独立单牌DP不能兑现增益→攻击、弃牌触发/抽牌等组合，旧清理策略遗漏变量替换的完整比较；隐藏牌堆仅使用共同已知multiset。 | 用户2026-10-08；KernelRelicChoices/KernelChoiceContinuation，SzgChoiceRepairScenarios；5隔离生产故障红/恢复绿原文在work/szg-repair-20261008T124624/choice/r2-results.json。 |
| **虚拟阳伞席位免费购物与构筑机制通路**：owner自己的库存经既有同步消息和native购买wrapper逐项免费购买，真人沿用native本地回调；Tools/Juggling/Phantom/BladeInk/Burst进入有限功能续演，加入公共三形态长期压力与完整候选日志。 | 本局阳伞估值53.44但虚拟owner无客户端回调；五项核心效果缺失与短boss场景压低构筑防御。宏观压力不是隐藏boss预测/胜率；路线不改。 | 用户2026-10-08；BotShopDriver/BotShopPlanner/Building，SzgBuildRepairScenarios；work/szg-repair-20261008T124624/build；四门及R2待最终本轮报告核对。 |
| **失败参考后的策略续演与全队反制代价**：调度分额截断保留确切状态，在同一总deadline内轮转续演；只有实际结束才比较，普通和联合开局继承有限native依赖排序；永久敌力量按全队下一phase额外hit估价并消除临时减力掩盖。 | 开局WIPE后另两独立策略被切断而无有效比较，Skill激怒代价跨全队；长期力量估价是显式启发式，完整终局仍由实际原生续演决定，不延长总预算或伪造胜利。 | 用户2026-10-08；KernelRollout/SequenceOrder/TacticalPolicy/Tournament/JointTurnRun，SzgCombatRepairScenarios；work/szg-repair-20261008T124624/combat。 |
| **轻量候选评分与正式选择隔离**：关闭整手lookahead的Tactical候选只取首条真实原生选择分支作排名估计；排序后选中的动作重新完整解析CardBranches，正式state/choices/tokens/ordinals均来自完整比较。宽搜索不变，1600ms总预算、3:1:1分配、16对牌序额度不变。 | 完整选牌比较在逐候选排名重复续算，导致旧四席压力测试Tactical无完成结果。浅候选可能低估其它选择分支，不能把评分首支部署；真实手牌证明首支弃PreciseCut但正式保留它改弃Strike并精确重放。 | 用户2026-10-08修复复盘问题；KernelRollout、LatestRunSearchRepairScenarios；work/latest-run-repair-20261008/stress-repair/handoff.zh-CN.md及两项真实R2；完整四门 work/verification/20261007T215935141Z。 |
| **多人牌序与联合比较的有限预算**：完整参考、联合续演和轻量首波均接原生AB/BA牌序比较；每玩家侧共16对按活托管席分配，保留增益和不可逆手牌消费的前置比较份额。一次Order中共享完整原生分支及后续手牌收益，实际动作后失效，缓存命中仍扣配额。联合比较先按既有FirstActions规则选确定合法短prefix，续演至真实终局或显式截止，再扩beam，后续Tactical/Defend/Growth按3:1:1分余量；无verified参考时保留原完整比较机会。总预算不变，启动、完成与显式截止分别记录。 | 本局晚打跟踪/残暴/狂怒，前席吃完后席的牌序预算，早期联合比较集中Kill。移除小手重复补额；保留双易伤→层数延长→Strength兑现及消费手牌前落实防御。墙钟beam首prefix不同导致固定份额不稳定，不以改诊断标签伪装完成。 | 用户2026-10-07；KernelRollout、KernelSequenceOrder、KernelTournament、KernelJointTurnRun；LatestCombatRepairScenarios、LongValue/StrategicLookahead/CombatQuality旧守卫及work/decision-repair-20261007。 |
| **龙涎香的后续治疗保留价值**：保留价值为原生HealPercent乘队伍最佳MaxHp的75%，超过普通瓶成本的部分写入终局记录；已验证团灭不为囤瓶加分，真正获胜仍优先。额外回合仅按原生续演兑现，Neow报价共用同一保留值。 | 原先每瓶固定6HP，使本局只补16HP即花掉强治疗并浪费额外回合；75%为显式不确定性先验，非实测胜率。 | 用户2026-10-07；KernelPotionPolicy、KernelTerminal、AncientOptionValue.Models；LatestCombatRepairScenarios/LatestBuildRepairScenarios。 |
| **局外原生收益和删牌后持续煤灰**：可跳过永久单选按奖励同源加牌边际收益决策；腐蚀波/狂怒按生效后的抽牌/攻击触发；石板用当前随机升级池实际收益与即时治疗、永久MaxHp比较；BiiigHug先选四删，再按剩余整副卡组和每次洗牌煤灰比较。 | 修复跳过前没估值、触发效应缺失、升级固定常数及一次性煤灰成本；构筑轨迹仍为有界宏观估计，不作为完整原生战斗胜率。 | 用户2026-10-07；Building、BotBrain、CardChoicePatches、EventPatches、AncientOptionValue；局外故障注入日志。 |
| **交易工具共用战斗弃牌策略**：实盘ToolsOfTheTradePower来源路由与自动回合开始预测共用清理排序，保留能力牌及当下有防守价值的牌，优先弃即将伤手的状态，允许无附加伤害的虚无牌自然消耗。 | 旧预测按通用CardValue，实盘按另一套弃牌评分，造成同数量但不同手牌的边界漂移；原生抽牌/弃牌效果和RNG不变。 | 用户2026-10-07；BotRelicChoices、CardChoicePatches、KernelRelicChoices、LatestProjectionRepairScenarios。 |
| **局内选牌比较实际后态**（替代上行的纯清理排序）：必选消耗/弃牌、手牌卡牌选择与回合开始选择先完整结算原生效果，再比较有限的可支付手续演；只在稳定交易点 fork，实际部署保留原始后态/RNG及物理token，评分副本不读取隐藏顶牌。 | 本局 ToM 消耗狂怒/Cascade、交易工具弃精准切割，静态牌分看不到触发、抽牌、能量和最后一张牌的计算伤害；不能改成无条件烧基础牌。 | 用户2026-10-07“先不动路线的选择，修复局内打牌的算法”；KernelChoiceContinuation、KernelRelicChoices、KernelChoices、KernelTurns、PostDecisionChoiceScenarios；work/incombat-repair-20261007/choice。 |
| **纯数值评分避免递归扩展选牌**：HandValue、Investment后续步、Defend的Threat估计及AB/BA/ABC的Then后缀复用既有轻量原生选牌，不再次展开三策略比较。Then只供数值排序，实际提交始终用Open的完整后态和选择token，落牌后新盘面重新比较；同一Tactical决策已完成的候选分支仅在该物理root的Order中复用。没有可支付合法卡时只做一次真实EndTurn见证，零费可打牌仍保留完整策略比较。 | 完整原生菜单比较在外层重复调用导致预算内无完整参考；调用栈确认纯估计占用大量重复续演。只限定评分工作，不拒绝合法动作，不延长总预算，不把截止当完成；缓存不跨实际动作。 | 用户2026-10-07局内算法修复；KernelSequenceOrder、KernelInvestmentProbe、KernelRollout、KernelChoiceContinuation、DiscardCleanupScenarios；work/incombat-repair-20261007/kernel-review/profile-second及choice/discard-native-review。 |
| **联合候选展开前兑现精细参考**：diverse模式从真实原生首动作重放得到Tactical续演，先完成或明确截止，再展开beam并继续原策略队列。早期比较与参考一样关闭嵌套投资，后续候选保留原投资工作；单策略及无首动作走原流。 | 先展开候选消耗剩余预算，会让精细续演连完整结果都拿不到；总预算与各job额度不加大，未资助的候选不伪装完成。 | 用户2026-10-07局内算法修复；KernelJointTurnRun、JointDiversityScenarios；work/incombat-repair-20261007/choice/discard-native-review。 |
| **一次选牌比较复用相同原生动作前缀**：三种评分策略从同一采样及准备后的seed各自Fork；同一完整前缀下，以物理玩家、牌、目标、状态/出现序号、实际选择token及EndTurn参数保存完整原生后态。null与显式空选择/序号数组的presence分别保留；Commit必须匹配实际解析后的完整元数据。命中返回独立Fork，结束回合只结算该策略自有副本；未完整/不可Fork后态不入缓存，未缓存的动作使cursor停用。单策略无牌边界保持原快路径。 | Survivor在不同策略中反复执行相同动作和敌人阶段，挤占整个精细续演预算。缓存只在一次Evaluate存活，没有跨盘面或跨实盘动作复用；三策略、384总步骤与真实终局要求不减少。 | 用户2026-10-07局内算法修复；KernelChoicePrefixCache、KernelChoiceContinuation、KernelRollout、DiscardCleanupScenarios；work/incombat-repair-20261007/choice/discard-native-review。 |
| **扩展搜索前完成独立防御/成长参考**：Defend/Growth 各自从相同根选择首牌，在剩余总预算的35%内比较完整原生续演，再进入既有联合搜索；投资预览关闭，原生牌序比较保留。预算截止具名，未资助任务不伪装完成，显式单策略和药水首牌约束保持。 | 将Defend续演接在Kill已消费的攻击首牌之后，可能已经无法支付格挡；大量prefix只完成进攻终局。本次只改变局内预算顺序和真实比较覆盖，不延长预算或改地图选择。 | 用户2026-10-07；KernelTournament、PostDecisionSearchScenarios；work/incombat-repair-20261007/search/r2-results.json。 |
| **缓存失败路线仍检查当前回合救命药水**：非胜局且有即将死亡压力时，在缓存快返前封顶重放当前回合，对实际瓶子/目标及相同续牌作原生生存对照；喝瓶后保留经过验证的救援续牌和选择，下一回合重搜。相同完整盘面缓存无救援结果，未完成试验不算保命证据。 | 本局 Bot3 6HP 持敏捷/能量药，却被“已比较/已缓存且非满栏”的快返跳过；防止仅喝药后继续执行旧致死牌序。不重复开启整场锦标赛。 | 用户2026-10-07；KernelCombatPlanner.EmergencyPotion、KernelPotionOpportunity、PostDecisionPotionScenarios；work/incombat-repair-20261007/potion。 |
| **未来属性按原生临时效果失效后残值估算**：长期能力、后续易伤/虚弱与 Shiv 收益使用私有分支的原生临时力量/敏捷/集中移除顺序，人工制品留下的真实永久属性保留。当前手牌和即时威胁仍用当前属性；永久消耗机会成本共用同一投影。构筑按牌序兑现当回合临时增益，保留既有0.7同手重叠先验。 | 原总属性把预备打击计入未来成长，又回溯放大自身及已打攻击；不能简单减去所有临时层数，否则破坏人工制品的合法永久收益。模拟生命周期源码适配另记 Vendor 表。 | 用户2026-10-07追加单回合属性审计；KernelSession.TemporaryStats/PowerValue/Evaluation、KernelTacticalPolicy、Building.ConstructionHand/SustainedDeckValue；TemporaryStatsCombat/BuildScenarios。 |
| **基础牌占多数且缺净手牌优势时优先升级真实净抽牌**：公共永久升级排序以显式政策优先级、再真实收益排序；不改拿牌/删牌/随机升级的势函数。新增净抽必须留有可支付输出，成本/扣血下降及受伤防御优先需求保持模型排序。营火按实际首选卡的真实收益定价。 | 用户指定12张卡组应剑柄打击优先于痛击/预备打击；抽后等量弃牌不增加净手牌、抽牌数量不变的升级没有新增抽牌收益，不能统一给“有抽牌”牌加分。这条优先级取代下面旧行“未给抽牌升级硬加分”的绝对表述。 | 用户2026-10-07；Building.UpgradePreference/BuildValue.RankUpgrades、BotBrain、RestSitePatches/RestSiteTeamPlan；TemporaryStatsBuildScenarios。 |
| **知识恶魔固定选择瓦解（替代 2026-10-04 的 Debuff 估值）**：实盘、自动预测和搜索候选都只取 Disintegration；删除卡组/血量惩罚估计。原生瓦解效果不变，显式原生效果夹具仍能模拟其他合法选项。 | 用户明确要求统一选瓦解，包括低血量情境。旧昂贵卡组/2HP选择Sloth或MindRot断言按本次策略授权替换。 | 用户2026-10-07；KernelKnowledgeChoice、KnowledgeDemonChoiceSupport、DecisionConsistencyScenarios；work/decision-repair-20261007/root-r2-results.json。 |
| **预测边界与实际选牌分叉后撤销缓存续演**：同回合/游标的后台边界与实盘不同则丢弃同一错误边界生成的原剧本，包括候选未完成时；真正的计划选牌回退也使依赖旧选择的续演失效，动作落地后从实际盘面重搜。 | R9要求修复预测/实盘分叉；保留旧续演也会继续依赖错误手牌/HP/RNG。此为缓存生命周期纠正，不为单个合法动作增设拒绝条件；原生Brand合法空选与一致选择继续执行。 | 用户2026-10-07修复复盘问题；KernelCombatPlanner.Refinement/ChoiceDivergence、BotChoicePlanSync、NightDecisionScenarios.Refinement、LatestChoiceDivergenceScenarios；work/decision-repair-20261007。 |
| **预测胜利按实盘回合复验**：新回合先撤销旧终局保证，6ms切片/120ms软预算重放剩余路线；确证仍胜才恢复豁免，完成但不胜则具名重新规划，超时保留可执行动作并允许回合末比较。知识恶魔的旧三回合选择后果估计已由2026-10-07固定瓦解策略替代（见上行）。 | 实盘崩解13而预测Sloth限三张，旧胜利让免费输出绕过补打；不因文本差异拒绝动作，不每张牌重开锦标赛。 | 用户2026-10-04；KernelCombatPlanner.VictoryReview、DecisionConsistencyScenarios；工作证据 decision-consistency-20261004；选择策略按2026-10-07更新。 |
| **未来减益使用同一起点的收益窗口**：回合末短序列及两张牌顺序比较固定参考HP和出牌机会，按参考输出决定连续窗口，各分支的新力量仍放大未来易伤伤害；最后一费互斥续演保留结束后仍生效的减益价值。 | 防止多6伤害跨过ceil台阶反扣整轮易伤收益；实际层数、死亡和层数兑现仍从各分支读取。 | 用户2026-10-04；KernelSession.PowerValue、KernelEndTurnRun、KernelSequenceOrder。 |
| **构筑普通/长战/干扰表现采用非负权重组合**；补自动打牌及能力启动、抽牌禁止时机、同手消耗降费和全队历史消耗预测，Tank计本人额外承伤与队友未被格挡吸收的减伤。 | 消除改善受干扰结果仍扣分；三张Boss金卡不能因缺关键机制被全判负。保持真实支付、有限窗口与稀释，未给抽牌升级或稀有度硬加分；未建模牌仍有先验与占位成本，不强制总分为正。 | 用户2026-10-04；DeckRobustness、ConstructionHand、ConditionalCosts、SustainedDeckValue；DecisionConsistencyScenarios。 |
| **正社区先验在同名牌首张计入，重复副本不再获得该项**；真实战斗、资源协同和亲和增量继续比较，负面质量成本仍逐张计算。 | 实际奖励重建中第二/第三张Prepared靠重复正先验掩盖低战斗收益；不设置挑衅/准备张数上限，不否决有实际收益的重复牌。保持拿牌/删牌/升级同一势函数。 | 用户2026-10-04；DeckStrength、DecisionConsistencyScenarios；work/decision-consistency-20261004/rewards-current.log、duplicate-r2.json。 |
| **战斗遗物弃牌/消耗估值读取当前攻击修正**：力量、虚弱、易伤通过分支伤害钩子，多段逐击计数，X费按手牌序列剩余能量支付和计伤；未来保留价值也使用攻击能力。 | 烘焙手套静态卡面分忽略23力量下的旋风斩终端；原有24种选择、10张手牌上限与替换采样保留。 | 用户2026-10-04；KernelRelicChoices、DecisionConsistencyScenarios.RelicExhaust；此为有界提示而非完整战斗搜索。 |
| 第一幕尚未经历精英时，整条到 Boss 的路线须包含一个精英；已过精英或当前已无此类可达路径时解除 | 用户明确要求第一幕至少挑战一次精英；低血量/低成熟度只影响含精英路线之间的评分 | 用户 2026-10-04；RoutePlanner |
| 未建模遗物采用 15 分低正收益先验，保持未建模标识；商店购买门槛随金币连续降低，最低 0.35，每次购买及第二件预览重新计算 | 避免 445 金币仍不删牌、不买遗物；已建模负收益和可负担性规则保留 | 用户 2026-10-04；HumanCoopAdvisor、BotShopPlanner |
| **附魔目标静态优先级**：按 `EnchantmentsTable.csv` 的 22 行建立冻结规则表；排斥先否决，再比较极高/高/略微/普通，同档沿用原评分。Corrupted/Instinct 的非 X 候选按一次打出总伤害排序；Clone 事件固定 −100。事件可避时不进入排斥目标选择；已进入强制选牌时仅按原评分补足原生最低数量。 | 用户提供人工优先级，明确要求同档用旧算法、排斥一票否决，并补充“单次”为多段合计与不可跳过时的例外。随机/自动附魔不套选牌否决。 | 用户 2026-10-04；`EnchantmentPriorities.cs`、`EnchantmentEventPolicy.cs`；`EnchantmentPriorityScenarios.cs`。 |
| **非胜利路线的回合末补打与续搜回退**：结束回合按同一敌方阶段结算后的全队存活/血量、敌方剩余血量和持续收益比较，6ms 切片、80ms 软预算、最多 64 个候选与 4 步；更优短牌序直接接入当前回合，下一回合重新比较。后台候选经过冻结边界重放才发布，非胜利基线采用不关闭后续回退，所有任务最终处置均记录。 | 知识恶魔 R6 多席位留费漏防御、R7 邪眼漏打；只检查攻击并奖励将失效的能量会误判。保留已验证获胜剧本与消耗机会成本，不修改卡牌可执行性；隐式群攻目标按当前牌型规范化，不臆造单体目标。 | 用户 2026-10-04 执行 P0/P1；`KernelEndTurnRun`、`KernelScriptReplayRun`、`KernelCombatPlanner.EndTurn/Refinement`；`ActTwoDecisionScenarios`。 |
| **团队防御与比较预算**：防御续演以原生分支补充无格挡字段的团队减伤；最后能量的互斥动作比较合法剩余手牌；投资基线胜出只取消强制能力开局，不覆盖精细策略。联合搜索在轻量参考后先比较 Tactical；Boss 首回合锦标赛总预算翻倍。 | 园丁三个队友各损失 8 HP，而简化策略忽略尖啸；68 次联合搜索有 33 次没有精细策略结果。未改牌的可执行性。 | 用户 2026-10-03；`NightDecisionScenarios`；`work/decision-followup-20261003/`。 |
| **变牌交易与营火协作**：变牌按原生卡池和克隆随机数比较替换后的全卡组，多张依次推进，包含 BingBong 复制；未知来源使用明确的采样估计。营火比较尚未行动的受控席位组合，计入确定的缩放仪入场治疗，MEND 的患者保留到执行。 | 变诅咒被当成删诅咒；先行动的席位过量治疗本可自己休息的队友。原生事件选项、资源成本与选牌数量保留。 | 用户 2026-10-03；`TransformPlan`、`RestSiteTeamPlan`。 |
| **执行期间优化下一回合剧本**：上游没有多人剧本空闲优化调度；本项目在出牌间隔/动画等待时以 2ms 协作切片预算回放冻结的当前回合，并比较下一回合的完整续演。只发布已完成候选；相同边界状态下严格更优才替换后续，平局保留原路线；不等待未完成搜索。 | 用户要求执行已确定动作时继续利用算力优化后续。本次预测不匹配仅废弃新计算，绝不因此丢掉正在执行的旧剧本；仅在所有存活席位受 Bot 控制时启用。2ms 是调度预算，原子原生步骤可能超时。 | 用户 2026-10-03 追加要求；`KernelScriptRefinementRun`、`KernelCombatPlanner.Refinement`、`BotRuntime`。 |

| **第二幕目标、投资与执行纠偏**：战斗进度只统计原生主要敌人，爪牙减伤仍由原生 Threat 体现；同宽保留生存/持续成长候选，Defend 也比较能力投资。执行中短斩杀仅在原生重放成功、终局优于旧线时替换；召唤目标按唯一 CombatId+模型映射；有效下一回合边界的失效后缀可重新搜索，仍严格匹配实盘。 | 用户要求修复 Y4VXRK7BEPMM 团灭问题；原生多人开回合跳过死亡席位，上游单人序列不能直接对旧存活列表补能量。构筑新增抽牌堆伤害/固有排序与单一队友的增量能量收益，脑蛭强制五选一独立于可跳过奖励。 | 用户 2026-10-04；`KernelShortVictoryRun`、`KernelCombatPlanner.ShortVictory`、`KernelScriptRefinementRun`、`KernelTurns`、`OutOfCombatPredictions`、`Building/AlliedEnergy`；`ActTwoPriorityScenarios`。 |
| **持续减益与易伤层数兑现**：原生分支实际增加易伤或多回合虚弱时，不套用等待防御时机的消耗保留偏好；持续减益估值接入回合末补打，保留原有牌序比较。Bully 的计算伤害排序复用原生分支变量，已有易伤时仍比较按层数增加的伤害。 | 战栗被误当成尖啸保留；易伤既是持续时间，也是主宰/Bully 可兑现资源。持续时间预测边界不能限制原生层数兑现或形成保留上限。 | 用户 2026-10-04；`KernelExhaustPolicy`、`KernelEndTurnRun`、`KernelTeamCoordination`、`KernelSession.Evaluation/PowerValue`；`DecisionFixScenarios.LastingExhaust`。 |
| **消耗牌保留与先建立完整参考**：对消耗技能比较原生实际收益与剩余使用机会；首牌排序与续打共用保留偏好，所有合法动作仍可搜索。联合搜索前先跑有限预算的轻量完整策略，首轮候选关闭嵌套预览，后续比较继续使用投资与牌序搜索。 | 知识恶魔无攻击回合先打尖啸；23 次决策中 33 个联合阶段没有完成一条路线。低威胁时不为少量防御浪费消耗，致死压力例外；持久卡牌强化和消耗触发抽牌不会被误当空收益。仅修改评分和预算安排。 | 用户 2026-10-03“根据上述问题，优化算法”；`KernelExhaustPolicy`、`KernelTournament`、`DecisionFixScenarios`；证据 `work/decision-fixes-20261003/`。 |
| **战斗遗物按结果手牌选牌**：赌博筹码比较零张、部分与全换；烤热手套按本场消耗损失选牌；工具箱、选择悖论按可支付手牌选择。实机与回合开始续演使用同一有界策略，抽取替代牌按剩余卡池采样，不读取牌堆顶序。 | 通用选择器用 MinSelect=0，导致赌博筹码永远不换；永久构筑分不适合本场手牌选择。保持原生选牌数量与真人控制。 | 用户 2026-10-03 战斗遗物反馈；`BotRelicChoices`、`KernelRelicChoices`；原生 29 个遗物选牌调用审计记录在本轮证据。 |
| **生成牌与资源升级估值修正**：手牌搜索实现生成小刀的伤害，熔刃交响曲计入自身小刀及单列队友收益；升级理由显示即时抽牌/能量差值。原生抽牌/产能支付由回归验证，保留同一长战斗基线的抗干扰差量。 | 生成小刀在开局手牌模型中没有伤害，导致减费/多生成牌的收益漏算。队友小刀按 80% 可用率的明确启发式估计，不充当自身触发次数或本人的斩杀来免除伤害。直接混合短/长情境的试验违反既有商店策略约束，未采用；额外抽牌在污染压力下仍可低分。 | 用户 2026-10-03 升级估值反馈；`DraftCardEffects`、`ConstructionHand`；旧性能快照与新数值差异存档于 `work/decision-fixes-20261003/`。 |
| **缺少专属资源启动条件，每张牌扣 1000 构筑分**：星星来源按原生效果和启动星费判断；奥提斯与充能球检查实际生成牌/遗物。惩罚进入统一卡组势函数，加删、变牌和整组选牌共用；独立生成资源的异色牌不因角色不同被拒绝。 | 用户明确要求“当卡组里没有专属启动资源时，给予巨大负面修正”。这是资源可达性的构筑策略，不能表示逐回合供应充足，也不限制实机出牌。 | 用户 2026-10-01；`ResourceStartup.cs` / `DeckStrength.cs`；`ResourceStartupScenarios`；证据 `work/crossclass-audit-20261001/`。 |
| **长期价值与构筑衔接修正**：薪火之源按后续回合实际能量建模，移除未知牌的虚构基础收益；主宰前置易伤通过原生两步分支排序；锦标赛为能力牌保留候选。 | 用户最新日志反馈：投资牌被忽略，主宰错序，构筑高估未知效果。只改变评分和候选覆盖，不新增执行拒绝。 | 用户 2026-10-03；`LongValueScenarios`。 |
| **P0/P1 局内续演与药水复用**：终局比较及持续能力估值读取原生多形态剩余血量；弃牌/消耗导致另一动作消失时，比较原生合法续打。中途抽到新能力或新增可支付资源时重新评估，限制每回合预览工作。串行/并行续演逐步检查预算，保留完整参考路线，开局轮转不同策略。药水先复用无药打牌路线比较单瓶和组合，再把结果带入搜索；紧急建模药水保留剧本。 | 最新团灭暴露换形进展倒退、提前弃掉有效牌、能力预览只做一次、单条续演超预算，以及每瓶药水之后重复搜索。用户限定只改局内；未知药水仍按既有满栏规则原生使用后捕获。 | 用户 2026-10-03；`CombatQualityScenarios`；证据 `work/combat-quality-20261003/`。 |
| **通用投资与牌序比较**：所有可模拟的手持能力牌进入同一套两回合原生预览，比较进攻/防守续演；同组动作比较 AB/BA，必要时预览第三张收益牌，取代主宰专用排序。保留不同能力牌及已结束当前回合的投资路线；锦标赛先轮流比较不同开局，再追加策略。 | 用户要求修复整类短视问题，追加 Panache 回归。预览按步骤推进；每回合限制牌序比较次数，小手牌保留收尾比较。联合搜索使用原预算的 40% 展开前缀，参考路线的投资工作预算由后续 P0/P1 修正限定。完整模拟终局仍决定剧本，未完成预览不冒充终局，不新增执行拒绝。 | 用户 2026-10-03；`KernelInvestmentProbe`、`KernelSequenceOrder`、`StrategicLookaheadScenarios`。 |
| **满栏未知药水例外**：对缺少效果模型或满栏收益规则的可主动使用药水，满栏时原生使用并清空旧剧本、重新规划，允许覆盖已有胜利剧本；保留原生可用性/目标检查及自动触发药水。 | 避免未知估值永久占满药水栏；明确接受使用后重新规划。 | 用户 2026-10-02；`KernelPotionOpportunity`；`WinningPlanScenarios`。 |
| **已验证胜利剧本优先于应急药水、满栏药水和空余攻击启发式**：存在剩余胜利动作时不插入启发式用药，也不因可打攻击牌而改掉计划结束回合；失效、耗尽仍走原有可执行性检查。 | Ovicopter 日志中 148 步胜利线被“预测某席死亡”的药水建议清空；团队胜利允许个别席位死亡，启发式没有提供更好的完整终局。 | 用户 2026-10-01；`KernelPotionOpportunity` / `KernelCombatPlanner`；`WinningPlanScenarios`。 |
| **远古之民按完整事件选项估值**：覆盖 8 位远古、102 种机制／106 个原生配置变体；分开计算即时交换、卡组变化、持续收益、代价和延迟。蛇眼使用当前卡组的随机费用可支付性；多人代价按作用范围计算；利爪及三种选牌附魔与执行目标共用估值。 | 上游战斗求解器不负责局外选择。用户明确要求所有远古事件选项建模并审计已有模型。未来房间、奖励、战斗和随机无法预览部分仍为有界期望，不是全局最优或胜率证明。 | 用户 2026-10-01；`AncientOptionValue*`；审计 `docs/ANCIENT_OPTIONS_AUDIT.md`；证据 `work/ancient-options-20261001/`。 |
| **战士／猎手能力牌按机制估计局内投资收益**：22 张原本主要按增益层数评分的牌，改为至多三个未来回合的能量、抽牌、留牌、格挡、毒伤或触发收益；读取分支牌堆和原生参数，扣除自伤／幽魂形态敏捷代价。依赖投影的能力路线使用同一份机制增量，避免投影恒零或重复计分。Tank／Guarded 当前承伤只由原生倍率进入 Threat。 | 用户指出薪火之源拿到后几乎不打，并要求检查、补齐两职业所有能力牌。此为搜索剪枝的有界启发式，不是精确战斗胜率；原生效果继续实际模拟，完整终局继续裁决。 | 用户 2026-10-01；`KernelSession.PowerValue.cs` / `KernelPower.cs`；`ClassPowerScenarios`，38 张、76 个版本；证据 `work/power-coverage-20261001/`。 |
| **商店删牌先筛选再估值**：可负担且有库存的删牌服务优先直接删可移除诅咒；否则有未升级基础打击/防御时只比较这些牌，没有时恢复全卡组候选；服务不可购买不扫描删牌。 | 用户 2026-10-01 明确要求减少明显无必要的卡组算法调用，并排除有未升级基础牌时误删生存者。此为明确的候选政策，不是全局强度函数已证明基础牌一定更差。 | `BotShopPlanner.ChooseDetailed/RemovalCandidates`；`ShopShortcutScenarios`；`work/reward-parallel/shortcut-r2.json`。 |
| **删牌优先级全入口统一**：`RemovalPlan.PriorityPool/PriorityTier` 是唯一的删牌候选优先级——可移除诅咒优先，其次未升级基础打击/防御，最后全卡组；保护角色最后一张的兜底不变。商店购买、`FromDeckForRemoval` 选牌（事件、营火 Cook、Neow/先古）和事件选项定价（`EventTrade.BestRemoval`）全部走这一条。 | 用户 2026-10-03 要求事件删牌与商店删牌用同一套优先级；此前商店独有候选裁剪、事件走统一 S，实测同一牌组两条路会删不同的牌（act-3 探针曾删 Mangle/Snakebite）。 | 用户 2026-10-03；`src/CoopBots/Building/RemovalPlan.cs`、`BotShopPlanner.cs`、`EventTradeHandlers.cs`；`--packaging-smoke` / `--unified-audit` 全绿。 |
| **局外构筑异步预算**：游戏线程分批复制原生事实；专用低优先级线程计算纯场景，主线程重放分数后提交。Windows x64 单处理器组上保留游戏线程所在物理核及 SMT，结束恢复亲和性。选牌、营火、商店、事件和局外卡牌/遗物选择共用 Low/Mid/High/Ultra；主机同步精度，状态变化丢弃旧结果。 | 用户 2026-10-01 要求多核避让主核、覆盖事件与锻造，并让开局性能配置控制局外预算。时间片为软预算；低档降低精度，不能承诺相同选择或硬帧时限。 | `ConstructionDecision` / `ConstructionWorkers` / `ConstructionBudget` / `ConstructionPerfSync`；审计与限制见 `docs/DECK_OUTCOME_OPTIMIZATION.md` §13。 |
| **多人共享易伤收益回到施加者卡组**：各普通/干扰场景按每位存活队友的能量约束攻击供应估计易伤额外收益，已有覆盖率递减，毒伤不计入，Bully 层数收益单列。加删升级和成长都通过场景势函数共用，没有痛击卡名保护。 | 用户 2026-10-01 指出删除痛击是坏决定，要求按人数计入共享易伤。此前只算受益端 Bully 读取队友易伤，漏算施加端的团队增伤。队友输出仍是期望近似，非完整多人重放。 | `SustainedDeckValue.AlliedStatus`；原生 `VulnerablePower.ModifyDamageMultiplicative`；`SharedStatusScenarios` 及 `work/robustness-optimization/r2-shared*.json`。夹具补齐同期队友卡组和 A10 进阶之灾，详见 `docs/DECK_OUTCOME_OPTIMIZATION.md` §10。 |
| **A10 鲁棒性进入统一构筑势函数**：九回合 240 HP 配对耐久场景估计凋萎与锁链损失，65% 最差机制 + 35% 平均机制；A10 三幕 λ=0.80/0.90/1.00，低难度 0.40/0.55/0.70。普通开手与周期共用九张窗口/32 状态手牌搜索，毛抽牌、真实弃牌/消耗、Sly 按原生次序；补齐 Tactician 原生返能映射。取代旧通用污染与独立可见 Boss 加分，成长也使用同一项。公共费用风险权重 31，金币换算 3.6；无删牌次数配额。 | 用户 2026-10-01 明确要求把抗干扰作为 A10 生存需求并提高 λ。这是构筑压力估计，不是完整 Boss 重放或胜率模型；不读取隐藏第二 Boss。新的金价和费用风险按既有商店锚点校准。 | `DeckRobustness.cs` / `ConstructionHand.cs` / `DeckStrength.cs` / `CardResourceTiming.cs`；机制红绿证据 `work/robustness-optimization/r2*.json`，实现、政策变化及限制见 `docs/DECK_OUTCOME_OPTIMIZATION.md` §10。替代下行历史参数。 |
| **有限战斗收益、后续成长与统一商店校准**：加删升级继续取同一 S 差值；整张牌支付、逐轨迹有限生命/击杀/战损、公开卡池四次奖励与一次升级的三组成长场景。当前战斗尺度 1.5、成长权重 0.25、四次方费用风险权重 16，公共金币换算 3.9。移除首笔/次笔购买的 25 金余额加门槛，单次只读决策缓存 S。 | 用户要求按顺序优化并复核 Bully、Bludgeon、基础删牌和通常删 2–3 次的锚点。都是构筑策略近似，非上游战斗规则。四组商店夹具删 3/3/2/4，允许划算的第四次；样本用于校准。社区一致率 39.49%→39.11%，未证明整体改善，历史八次商店重建仍零删牌。 | 用户 2026-09-30；`docs/DECK_OUTCOME_OPTIMIZATION.md`；证据 `work/deck-outcome-optimization/`。取代下行历史参数，旧测试政策调整见文档 §6。 |
| **固定费用风险与 Bully 易伤收益**：统一 S(D) 减去每张牌 `4×(费用/稳定能量)^4`；0 费、X 费与不可打出牌豁免。Bully 从原生 ExtraDamage 读取升级和重放收益，抽手/周期模型估计易伤供应，周期还读取存活队友的公开卡组。 | 用户提出卡手风险，并给出 Bully/Bludgeon 同稀有度、同价锚点。风险是策略近似，不是卡手概率；权重 4 尚无独立数据支持。3 能量夹具选 Bully，4 能量允许选择 Bludgeon。社区一致率未提高，历史商店零删牌仍未解决。 | 用户 2026-09-30；`docs/ENERGY_COMMITMENT_RISK.md`；13 组 R2 与社区证据 `work/energy-commitment-risk/`。 |
| **统一卡组强度势函数**：选牌、删牌、升级均按修改前后 S(D) 差值；商店加删牌共用剩余收益周期。固定周期/抽手混合、社区先验、未知效果估计和规模项，批量升级按最终卡组计分。 | 用户要求三个入口统一，并以 Spire-Codex 和商店删 2–3 次的经验锚点审计；替代旧的获取 Elo 独立公式、候选决定模型、额外删牌折扣与商店流派溢价。2–3 次只属于固定回归夹具，不是运行时配额。 | 用户 2026-09-30；`DeckStrength.cs`；`docs/UNIFIED_DECK_STRENGTH.md`；R2 与社区/日志证据 `work/unified-deck-strength/`。 |
| **V41 佩尔之牙按"强化 5 张"处理（PAEL / PaelsTooth）**：① `BotCardChoiceDispatcher` 新增纯函数 `PurposeFor(methodName, paelsToothOnStack)`——`FromDeckForRemoval` 且调用者是 `PaelsTooth.AfterObtained` 时改标签为 `BotBrain.PaelsToothPurpose`（**刻意不含 "ForRemoval" 子串**，否则会掉进移除分支）；② `BotBrain.Permanent` 让该 purpose 走**与营地锻造完全同一条**贪心路径（按 `BuildValue.UpgradeDelta` 降序，每取一张就把强化后的牌换进上下文再算下一张）；③ `HumanCoopAdvisor.RelicValue` 新增 `PaelsTooth` 分支：价值 = 牌组里 `UpgradeDelta` 最高的 5 张之和（此前拾取预测为空 → 读成"尚无可靠的专属估值"的 0）。 | 用户 2026-09-30：「佩尔之牙…本质上是升级 5 张牌，而不是移除 5 张牌，现在只按移除算，导致只有 5 张基础打击和防御牌被选中，真正有价值的牌没被选中」。机制（`PaelsTooth.cs`）：`AfterObtained` 用 `FromDeckForRemoval(filter: c => c.IsUpgradable)` 拿走 5 张，`AfterCombatEnd` 每场战斗结束把其中随机一张**强化后**放回牌组。实测该遗物 `RelicValue = 0.00`（拾取预测为空），而 PAEL 也没有 handler、没有排名日志。变异复现：把该 purpose 改回移除路径，测试原文为 `it took STRIKE_IRONCLAD×4,DEFEND_IRONCLAD`（正是用户描述的那一手）。 | 用户 2026-09-30；`src/CoopBots/CardChoicePatches.cs`（`PurposeFor` / `PaelsToothIsOnStack`）；`src/CoopBots/BotBrain.cs`（`PaelsToothPurpose`）；`src/CoopBots/HumanCoopAdvisor.cs`（`PaelsTooth` 分支）；`tests/PatchSmoke/BuildValueScenarios.cs` |
| **V40 毒液玻璃按"任意数量"定价 + 池基事件排名可见（OROBAS）**：① `HumanCoopAdvisor.PredictedEffectValue` 新增 `SeaGlass` 分支——把预测出的三档稀有度**合起来当作一张网格**，用新的共享原语 `BuildValue.GreedyPick` 逐张贪心求和，取代通用规则的"每档只算最好的那一张"。选牌器（V39）与这里的定价从此**共用同一段代码**，报价与实际会拿到的牌不会再分叉。② `EventPatches.ChooseEventOption` 走通用打分器时新增 `CoopBots event ranking:` 日志，打印每个可选项的 `KEY#index=分数`（池基事件此前 `Explain()` 为空，一条痕迹都没有）。 | 实测 2026-09-30（starter Ironclad）：通用规则给毒液玻璃 **58.24**（三档各自最优），按"任意数量"贪心是 **113.49**，几乎翻倍。更要紧的是同池 `Driftwood` / `PrismaticGem` / `ElectricShrymp` / `AlchemicalCoffer` / `RadiantPearl` 全部 **0.00**：实数日志里 OROBAS 给的三个选项是毒液玻璃 58 / 浮木 0 / 古老牙齿 0 —— **两个 0 之间就是并列，由页面顺序决定**，而日志不说任何理由。 | 用户 2026-09-30「执行B1」；`src/CoopBots/HumanCoopAdvisor.cs`（`SeaGlass` 分支）；`src/CoopBots/Building/BuildValue.cs`（`GreedyPick`）；`src/CoopBots/EventPatches.cs`（`LogRanking`）；`tests/PatchSmoke/BuildValueScenarios.cs` |
| **V39 `0..N` 取牌屏幕按价值逐张回答（OROBAS / 毒液玻璃）**：`BotBrain.SelectCards` 新增 `TakesAnyNumberOf(purpose)`（目前只认 `FromSimpleGridForRewards`）与 `WorthTaking(player, list, min, max)`。命中时不再返回 `min`（这个形状恒为 0），而是**贪心逐张**：每轮对剩余牌用 `BuildValue.Add(card, player, workingDeck)` 打分取最高的一张，>0 才收，收下后并进 workingDeck 再重算，直到到 `max` 或最好的一张 ≤0；`min>0` 的强制下限照旧兑现。并补 `BuildTrace.LogDeckEdit` —— 这条路径此前**一条日志都没有**，所以"15 张一张没要"是无迹可查的。刻意只覆盖"加进牌组"的 0..N 屏幕；弃牌/消耗类的 0..N 是代价，declining 常常正确，各自取证前不动。 | 实机 2026-09-30（`OROBAS`）：B2 选了「毒液玻璃」，规则是"查看 15 张来自其他角色的牌，**选择任意数量**加入牌组"（`CardSelectorPrefs(prompt, 0, 15)`），结果**一张没要**、牌组 size 不变，奖励全废；日志里没有任何解释。根因是 `count = clamp(min, …)` 把 `min` 当成了答案，`.Take(0)` 把已经排好序的 15 张全削掉 —— 不是评估后觉得不值，是**根本没评估**。对照同场 `GLASS_EYE`（固定 5 组）正常入账；`RoomFullOfCheese`(定选1)/`BrainLeech`(定选1)/`SealedDeck`(定选10) 都是 `min==max`，不经此分支。 | 用户 2026-09-30「检查当前Bot对先古之民·欧巴洛斯的事件判定建模算法……一张牌都没要，完全浪费了本次奖励」+「修复选择器」；`src/CoopBots/BotBrain.cs:WorthTaking`；`tests/PatchSmoke/BuildValueScenarios.cs` |
| **V38 藏宝图事件按任务收益定价（THE_LEGENDS_WERE_TRUE）**：新增 `LegendsWereTrueHandler` 与 `EventTrade.Card` 的任务牌分支。`SPOILS_MAP`（Quest）在战斗估值器里必然不可打出，`BuildValue.Add` 返回 `-1000` 守卫哨兵；此前该事件无 handler、走通用打分器，藏宝图选项因此被定成 **-237**（选项名 `NAB_THE_MAP` 的 "nab" 还误匹配 "gain" 白拿 +8），bot 在从未读过 600 金的情况下拒图。现在 `EventTrade.Card` 对 `SPOILS_MAP` 按 `OnQuestComplete` 的 600 金（`EventTrade.Gold`，3 金/点）定价、减去 `DeadCard`（1/4 个诅咒）的死牌占用，handler 与「8 点不可格挡伤害 + 预测药水」比大小；`BuildValue.Add` 本身不变（战斗层仍视其为死牌），页面顺序不参与决策。 | 用户 2026-09-30「检查一下当前项目对藏宝图事件的建模，Bot 是如何评估不拿藏宝图而选另一个选项的？这个模型算法是否合理？」。探针实测：修正前 `Grants(NAB_THE_MAP)=-1000`、通用分 `-237` vs `-4`，必然拒图；修正后 `map=196.5` vs `exit=4.0`，按真实收益拿图。 | 用户 2026-09-30；`src/CoopBots/EventTradeHandlers.cs`（`LegendsWereTrueHandler` / `QuestGold` / `DeadCard`）；`src/CoopBots/EventHandlers.cs`；`tests/PatchSmoke/TabletEventScenarios.cs`；门 `--tablet-only`（含 R2 断红证据） |
| **拿牌 / 升级 / 删牌统一接线（V35，第 1+2 步）**：三条路都接 `SustainedDeckValue.Compare(deck, added, removed)` 这台共享引擎，本次只统一"接线"、不动引擎与先验常数——① **份额统一**：`share = Math.Min(实验 ModelShare, BuildCoverage.Of(card))`（`Add` 原本就有，`UpgradeDelta` / `Remove` 补齐），读不懂的牌拿不到实验的判决权；② **单位统一**：升级也走 `CounterfactualDraft.DeckPoints(experiment, deck.Count)`（cycle 覆盖下与原 `Δ×6` 数值等价，实测已建模牌的升级值 1.98 / 9.77 **逐位不变**）；`Remove` 的 `RepeatablePerFight` 也改用同一份额。 | 用户 2026-09-30：「拿牌=牌组变强多少、删牌=反过来、升级=强化后变强多少，三套逻辑都会用到卡组估值」；三处各说各话的实机后果就是 2026-09-29 的 BULLET_TIME：Elo 买进来、cycle 升级，抽手实验却按"读不懂=占位符"删掉。读不懂的牌在升级/删牌两条路上原本会被实验按满额判死（`model-share:1.00`）。 | 用户 2026-09-30「执行 1 和 2 的改动」；`src/CoopBots/Building/BuildValue.cs`（`Add`/`UpgradeDelta`/`Remove`）；`tests/PatchSmoke/RemovalProbeScenarios.cs`（含 R2 变红原文） |
| **没建模的牌在删牌定价里不低于基础牌（V34）**：`BuildValue.Remove` 对 `BuildCoverage.Of(card)==None` 的牌（效果模型完全读不懂）把删除价值**封顶到本副牌组里普通打击/防御删除价值中更小的那个**，并在 reason 里留 `unmodeled-floor:<值>`；没有基础牌可比时不封顶。已建模的牌不受影响。 | 用户 2026-09-30：「对删牌计算里没有建模的牌，其估值不应低于基础牌（打击和防御）」。动机是实机 2026-09-29 那一手：BULLET_TIME 靠社区 Elo（1630.7 vs skip 1573）买入、营地锻造、事件附魔 PerfectFit，下一个商店却被删——`Remove` 把抽手实验的 `model-share:1.00` 直接采信，而实验对读不懂的牌只会当占位符（`coverage:0.00`），于是它的删除价值 +11.15 反超普通打击 +2.59。这正是 `BuildCoverage` 自己的原则「absence of evidence is not evidence of absence」在删除路径上的缺口。 | 用户 2026-09-30；`src/CoopBots/Building/BuildValue.cs`（`UnmodeledRemovalFloor`）；`tests/PatchSmoke/RemovalProbeScenarios.cs`；探针 `--removal-probe-only` |
| **V37 营地极低血量与"最后一个篝火"加价**：`BotRestSitePatch` 新增两个系数——① `HealValue` 在**治疗前**血量比例低于 `CriticalHpRatio=0.35` 时，按 `CriticalHpBonus=2.0` 沿缺量线性再加一档 need（满血/中血完全不受影响）；② `CampHealWeight` 在 `RunDepth.LastCampBeforeBoss` 为真时把治疗权重乘 `LastCampHealBonus=1.5`。HEAL 与 MEND 共用同一权重，两者相对定价不变。 | 实机 2026-09-30 四人 A10 团灭于第三层 Boss `AEONGLASS_BOSS`。Boss 前最后一个篝火，B3 席位 `hp=7/62`（11%）、`lastCamp=True`、`scores=[HEAL:33.1, SMITH:47.3, MEND:42.2]` —— 它在"下一层就是终局 Boss、再挨一下必死"的状态下去升级了一张牌，进 Boss 时 9 血、第一回合末即被打死；全队以 80/36/42/9 进场。旧曲线里"再挨一下必死"与"掉了三分之一的血"同价。 | 用户 2026-09-30「修复 2……」「为极低血量和"最后一个篝火"赋予更高的系数」；`work/triage-20260930-wipe/godot-latest.log`；`src/CoopBots/RestSitePatches.cs:CriticalHpRatio`/`CampHealWeight`；`tests/PatchSmoke/RestSiteScenarios.cs` |
| **V37b Music Box 不再让整轮决策离线**：`Vendor/.../BeforeCardPlayedMirrors.cs` 的 `MusicBoxPredictionState` 改为可 fork（`Fork()` 浅拷贝、`AssertForkable()` 空实现），**保留** `IPredictionForkBoundary` 以维持"不参与手牌选择续演"这条独立限制。源码层登记见 `src/CoopBots.Kernel/VENDOR_DEVIATIONS.md` **D7**。 | 同一场战斗：`Cannot fork Music Box during card-play resolution` 连续 6 次，全部落在持有 Music Box（VAKUU 遗物）的两个席位上，`KernelCombatPlanner.Choose` 的 catch 把整轮决策判成 `Declined` 并退回 legacy 规划器——最强规划器在唯一打不过的那场里被自己关掉。 | 用户 2026-09-30「……修复 3」；`work/triage-20260930-wipe/godot-latest.log`；`scripts/import-combatsolver-kernel-41.py:make_music_box_forkable`；D7 |
| **V36 THE_GAMBIT 获取黑名单**：`BuildValue.AcquisitionBlacklist` 在 `AddDetailed` 首行短路，命中返回固定 `-100`（`Guard("blacklist:the-gambit", …)`，理由行进日志），压在 Elo / 结构 / 情境 / hand 实验之前，因此**低于 skip 的 0、也低于任何真实候选**。这是**定价**不是执行拒绝（R4 不受影响，不加任何新闸）：reward / 商店 / 事件 / `HumanCoopAdvisor` 共用 `BuildValue.Add` 全部自动生效，商店 `CardGold = max(0,value)×3` 因此报价 0、永不购买。 | THE_GAMBIT 打出即挂 `THE_GAMBIT_POWER`：吃到**任意一次未被格挡的 powered attack 伤害**就 `CreatureCmd.Kill`，**无视剩余血量**——这是"构筑里带一颗地雷"而不是"少一点数值"，且估值器从牌面读不出这个代价。实机 2026-09-29 二层第一战 `THIEVING_HOPPER_WEAK`（floor 18）：B2 自己的剧本第 25/26 步连打 THE_GAMBIT + PANIC_BUTTON（后者 `NoBlockPower` 让格挡全变 0），turn3 被 `HAT_TRICK_MOVE` 触发处决，52/80 血满着死；同场模拟器因漏掉敌人 Power 的多人缩放（Flutter 实机 24 层 vs 内核 5 层）仍判 4 人全活 `VICTORY`、0 拒绝。 | 用户 2026-09-29「直接把 THE_GAMBIT 拉入黑名单……-100 获取意向分数（赌狗不得好死）」；`work/triage-20260929-act2wipe/godot-latest.log`；`src/CoopBots/Building/BuildValue.cs:AcquisitionBlacklist`；`tests/PatchSmoke/BuildValueScenarios.cs` |
| **弃牌清理排序（V33）**：手牌弃牌（`Discard` / `DiscardAndDraw` / `Exhaust`）的候选按"这回合真的想扔哪张"重排：**不虚无**的状态/诅咒（贪婪）与带回合末伤害的（WITHER）仍然最优；其次是**本回合确定打不出去的牌**（直接问模拟器 `Combat.CanPlayCard`，含能量/目标/禁止）；然后是还能打的牌；**虚无且没有别的负面效果的（进阶之灾、眩晕）排到最后**——它们回合末会自己烧掉，丢掉等于把它留在牌组里等下次再抽。 | 用户 2026-09-29：主动丢掉这类虚无牌不如丢掉本回合确定不会打的牌，让虚无牌烧掉。原实现把所有状态/诅咒一律排第一，于是进阶之灾/眩晕被优先扔掉、留在牌组里反复卡手；WITHER 不是虚无，改它就会推翻 V14–V18"先扔掉伤手牌"的政策，故不动。 | 用户 2026-09-29「弃牌时不要优先丢虚无状态/诅咒」；`src/CoopBots.Kernel/KernelChoices.cs:CleanupValue`；`tests/PatchSmoke/DiscardCleanupScenarios.cs` |
| **夹击/击倒的三处语义修正（V32）**：① **生命周期**——两者都必须在**持有者所在阵营结束回合时**摘除（实机 `AfterSideTurnEnd` → Remove）；内核原先只镜像了增伤那一半，一次夹击会跟到整场模拟结束。② **单位与施加者**——实机一次施法写 `Amount 2` 且把 `Applier` 渲染成平台名，内核自己写"一次施法 = 1 层"和 NetId；捕获来的实例先折算成内核层数再喂给 `2^stacks`，applier 判据改用**施加者 Creature**（实机自己就是这么判的）。③ **复利规则不动**：3 张夹击仍是 8×（两种单位下都成立，`KernelEngineScenarios.AuditFlankingKnockdown` 继续钉住 1 张 20 / 3 张 80）。 | 实机 A10 四人第 25 层 `ENTOMANCER_ELITE`：缓存剧本判 `ending=VICTORY`（VerifiedTerminal），81 步一步不差全部执行，实机却团灭、精英还剩 **218/789**；R2→R6 出牌序列与剧本逐步一致（16/14/13/13/6 张），唯一差别就是 R2 打出的夹击在仿真里没摘（之后每回合翻倍，≈补上那 ~211 点）。顺带量到捕获路径的两个单位错误：施法者自己被加成、单张夹击读成 ×4、三张读成 ×64。 | 用户 2026-09-29「核对 VICTORY 高估实际伤害的原因并修复」+「一回合 3 张确实是 8 倍，只需修回合末的闸门，不是 `2^Amount` 本身」；`tests/PatchSmoke/FlankingLifetimeScenarios.cs`；源码层登记见 `src/CoopBots.Kernel/VENDOR_DEVIATIONS.md` **D6** |
| **V31 按出场频率补三个事件**：① `SELF_HELP_BOOK`——三页各附魔一种牌型，附魔 id 写在 **StringVar** 里（`Enchantment1/2/3` = `SHARP`/`NIMBLE`/`SWIFT`），效果从游戏本地化文本读出（+N 伤害 / +N 格挡 / 首次出牌抽 N），按 `MarginalCore` 自己的权重（伤害 1.2 / 格挡 1.0 / 抽牌 4.0）× 三次出牌 horizon 定价，仅首打的附魔不乘；**牌组没有该牌型的一页记 0**。② `STONE_OF_ALL_TIME`——LIFT = 随机药水储备值换永久最大生命，PUSH = `VIGOROUS` 首打伤害 − 掉血。③ `WELCOME_TO_WONGOS`——价格取自事件变量（100/200/300），`BARGAIN_BIN` 的遗物由预测器给出，**`LEAVE` 不是免费的**（游戏会把一张已强化的牌降级，按牌组剩余升级均值计价）；没有预测器的货架计价为 0，**不盲买**。 | 按实机出场次数（SELF_HELP_BOOK/WELCOME_TO_WONGOS/STONE_OF_ALL_TIME 各 8 次）而不是按 T 编号排优先级；三者此前都落在通用打分器的关键词规则上（王戈斯的 `BARGAIN_BIN` 更是因为拼写里含 "gain" 白拿 +8）。 | 用户 2026-09-29「先处理出场次数高的这三个事件」；`docs/OPTIMIZATION_V31_TOP_EVENTS.md`；`tests/PatchSmoke/TabletEventScenarios.cs` |
| **V30 事件 T4（诅咒/永久代价换收益）**：`EventTrade` 加 `BestUpgrades`、`PricesCurse`、`GrantsWithCurses`——诅咒张数由 handler 从事件文本显式传入（本地化不可解析），但**只在选项自己没计过诅咒时才补收**，避免同一个诅咒收两次。新增三个 handler：`LOST_WISP`（遗物+诅咒 vs 金币）、`SPIRIT_GRAFTER`（治疗按缺的血封顶 + 蜕变牌 vs 掉血换升级）、`DROWNING_BEACON`（药水 vs 遗物−永久最大生命），并**删除通用打分器里那段只减最大生命、从不给遗物计价的内联 DROWNING_BEACON 特判**。`TRIAL` **明确不做**：其 `GUILTY`/`INNOCENT` 跨页重复且两页效果不同，且本地化文本说 INNOCENT 是"诅咒+强化 2 张"而预测器给的是 Transform 结果包——两份来源矛盾，等实机样本裁决。 | 一类事件用诅咒或永久代价换好处，此前只有通用打分器按名字猜，代价侧完全没计价。 | 用户 2026-09-29「接下来执行 T4」；`docs/OPTIMIZATION_V30_EVENT_T4.md`；`tests/PatchSmoke/TabletEventScenarios.cs` |
| **V29 事件 T2（随机结果按预测定价）**：新增 `EventTrade.Grants(player, model, option)`——把选项 hover tip 上的**具名模型**与 RandomForeseer **预测**的模型（克隆 RNG 重放生成器）合并计价，按 Id 去重，单位是卡组价值点；诅咒走有界 −14×bloat。**通用打分器与全部 handler 共用这一份实现**，于是"随机遗物/药水/卡牌"的选项不再按"未知"计价。三个 T3 handler（`THIS_OR_THAT`/`UNREST_SITE`/`WELLSPRING`）的预测器本来就在，本轮起自动用上，估值常数只在预测失败时兜底。新增两个需要单独计代价的 handler：`BATTLEWORN_DUMMY`（木桩 75/150/300 HP，奖励可预测，战斗长度按每 30 HP 记 1 点的具名估计）、`BRAIN_LEECH`（RIP 掉血换无色卡奖励 vs 白拿随机卡）。 | 内核注册了 **31 个**事件预测器，而此前只有 11 个 handler——大量"随机结果"读得出来却没人问。 | 用户 2026-09-29「接下来执行 T2」；`docs/OPTIMIZATION_V29_EVENT_T2_PREDICTION.md`；`tests/PatchSmoke/TabletEventScenarios.cs` |
| **V28 事件覆盖率（BakedEvents + T1 + T3）**：① `BakedEvents` 按**本机安装版**的本地化键补齐 3 个缺失选项 id（`COLORFUL_PHILOSOPHERS/EQUALITY`、`LOST_WISP/CLAIM_LOCKED`、`SPIRALING_WHIRLPOOL/REACH_IN`）——缺 id 会让漂移守卫永久挡住该事件的 handler；并把 `NEOW`/`OROBAS`/`PAEL` 具名登记为**池基事件**（选项 id 是运行期抽出的遗物 id，静态表列不出来），断言其不得挂 handler。② **T1**：通用事件打分器对 hover tip 里的**具名卡牌**改用 `BuildValue.Add`（×0.25，与遗物分支同换算），取代"非 Power 一律 0"；诅咒仍走有界常数。③ **T3**：新增 `EventTradeHandlers.cs`（统一单位 `EventTrade` = 卡组价值点，HP 1:1、金币 ÷3、诅咒 −14×bloat）与 6 个 handler——`SUNKEN_STATUE`/`SUNKEN_TREASURY`/`JUNGLE_MAZE_ADVENTURE`/`THIS_OR_THAT`/`UNREST_SITE`/`WELLSPRING`，数值取自事件自己的 `DynamicVars`；决策日志统一走 `EventHandlers.Explain`。 | 全事件扫描（56 事件 / 11 handler / 41 个零信号）显示：一类事件授予具名卡牌却被打 0 分，另一类纯资源交换完全没有信号、由页面顺序决定。缺 id 与池基事件是"表看起来全、实际不全"。 | 用户 2026-09-29「扫一下还有哪些事件根本没有建模」「对 BakedEvents、T1+T3 进行建模」；`docs/OPTIMIZATION_V28_EVENT_COVERAGE.md`；`tests/PatchSmoke/TabletEventScenarios.cs` |
| **V27 光与暗之门事件建模**：新增 `DoorsOfLightAndDarkHandler`。光之门（随机升级 `{Cards}` 张）用内核 RandomForeseer 移植的 `OutOfCombatPredictions.EventOption` 读出**这局实际会被升级的那两张**，逐张按 `BuildValue.UpgradeDelta` 计价；暗之门（自选移除 1 张）取 `BuildValue.Remove` 最优目标；两者比大小并返回具体 index。预测不可得时不猜，退回通用打分器。日志追加 `light=…[…] dark=…(…) predicted=…`。 | 实测四个席位在 `DOORS_OF_LIGHT_AND_DARK` 都选了光之门升级、放弃自选删牌。该事件原本没有 handler，两个选项 id 不含任何关键词，通用打分器双双给基础分 5.0，**并列后按页面顺序取 index 0**——结果由排列决定而非评估。同一日志里它自己的单位：随机升两张基础牌 ≈6.6，自选删一张基础牌 ≈75。 | 用户 2026-09-29「这个事件是怎么建模的」「使用数据来进行建模，让 Bot 看到选光之门实际强化了哪两张牌」；`docs/OPTIMIZATION_V27_DOORS_EVENT.md`；`tests/PatchSmoke/TabletEventScenarios.cs` |
| **V26 构筑选牌估值修复**：新增 `BuildCoverage`——牌的文本不在估值可读的效果族内时 `coverage=0`；结构项 `rules` 按覆盖率加权（`eloBase + coverage*rules + development + affinity + multiplayer`），覆盖率 0 时**不跑** cycle 估计，理由行标 `estimate:no-coverage`，社区 Elo 独自决定。**「成型度」取代张数**：`maturity = 1 − 未升级基础牌占比`，`gate = clamp((maturity−0.6)/0.4, 0, 1)`，`pressure = Oversize(N)×bloat×gate`；`N>32` 无条件恢复收税作防泛洪兜底。`RemovalLeftInRun = 本幕有店 ∨ 还有后续幕`，取代只看当前幕的 `ShopAhead`。日志：`six-turn:`→`per-turn:`、理由前置 `coverage:`/`maturity:`、托管席补 `reward not taken`。 | 实测第一幕 Boss 后跳过稀有卡：被跳的 BLADE_OF_INK(1738.2)/BURST(1702.1) 社区 Elo 高于实际拿下的 IMPERVIOUS(1646.9)/BRAND(1648.9)；根因是读不懂文本的 cycle 估计覆盖了 Elo，且张数税与牌无关。只让"从牌面读出的部分"受覆盖率压制，先验不压制；未成型牌组不收张数税，成型后行为逐字不变。 | 用户 2026-09-29「这个稀释算法明显有问题…金卡…在拉高你卡组的平均水平」与「成型度达到某个值时才需要 Skip」；`docs/OPTIMIZATION_V26_BUILD_DECISION.md`；`tests/PatchSmoke/BuildValueScenarios.cs` |
| **V25 联合搜索参照路线优先**：`KernelJointTurnRun` 的第 0 个续演（最佳前缀 × Tactical）不受单任务时间片限制，只用总预算封顶；其余任务按"打算比较的打法数"（五种策略）而非 `paths × policies` 的四十份摊分预算。截断仍是具名 cutoff，不当终局。诊断新增 `reference=<stop\|pending>`。 | V21 指出联合续演预算按 `paths.Count` 摊分。实测 defeat-v24 日志：42 次联合搜索里 38 次 `Tactical:0`——最贵也最贴位置的 Tactical 续演被切掉，只剩便宜的 Kill/Growth/Cleanup/Defend。先保证一条完整参照路线，再用剩余预算比较其他打法，且不许把未完成路线当终局。 | 用户 2026-09-29「执行上述遗留内容」；`docs/OPTIMIZATION_V25_JOINT_BUDGET.md`；`tests/PatchSmoke/JointDiversityScenarios.cs` 压力夹具 |
| **V24 局内决策修复**：上游镜像仍负责合法性与结算；本地排序统一群攻/单体伤害修正，格挡按未覆盖伤害计分，保留已建模的储存/全身撞击用途；临时增益先结算，再续演最多三次合法且可支付的后续动作。满栏鱼油以两次实际增益主动使用；固化剂按实际防伤并扣除已计划的吹哨防护；两瓶加入奖励库存比较。手动局也记录 Bot 自身手牌与公开战场状态。 | 用户报告过防、弱攻击优先、昏眩空增益及丢失瓶中精灵；修正排序与策略覆盖，不增加执行拒绝条件。 | 用户 2026-09-28；`docs/OPTIMIZATION_V24_DECISION_QUALITY.md`；`work/decision-feedback-20260928-233427/` |
| **V23 一层团灭反馈**：华丽打击构筑按公开牌组的首轮抽牌循环采样可打窗口，处理抽空与过抽、能量、手牌容量、已知开局抽牌遗物；不改变局内合法性。双机器人事件在初始页以全队最低血量 80% 储备判断挑战，已承诺页继续原生战斗。满栏药水奖励比较库存储备价值，合成 Bot 在全端确定性领奖序列调用原生弃药，接管席位等待同步弃药动作完成再点击。锻造祝福及永久属性药水在两次可支付的实际增益达 4 点时可主动腾位；临时属性仍遵守原来的三触发标准。 | 用户报告发光水因满栏丢失、华丽打击误购误强化、双机器人事件重伤后团灭。构筑条件与奖励流程修复，事件血量阈值为明确的策略估计，不声称已模拟该战斗。 | 用户 2026-09-28；`docs/OPTIMIZATION_V23_ACT1_REVIEW.md`；`work/act1-wipe-20260928/` 日志、回归与故障注入 |
| **V22 能力牌、铺垫牌序与生成卡药水**：为无尽刀刃、刀扇、蛇形、鬼祟补充有限期持续收益；战术候选保留能力与最高排名动作，生成的免费攻击按原生分支探测后续收益。精准/力量与暴露按支付费用后、共享能量的实际攻击增益排序，暴露比较移除前后的护甲。鬼祟构筑比较统计其他席位的六回合攻击牌次数。攻击/技能/能力/无色药水用克隆 RNG 的实际候选、零费效果估值，满药水栏有即时收益即可使用；满手时包括濒死应急在内均不赋予本回合生成收益，出牌腾位后重评。选择仍实盘完成再重建剧本；未把预览宣称为完整药水重放模型。 | 用户实机报告能力迟打、刀扇输给打击、精准插在小刀中间、鬼祟未升级、暴露晚于攻击、选择药水长期占栏且濒死满手浪费。修正机制与候选覆盖，保持原生结算和完整终局裁决；满手限制是用户要求的用药收益策略。 | 用户 2026-09-28 连续反馈；`docs/OPTIMIZATION_V22_SELF_CHECK.md`；`work/power-investigation-20260928/` 的日志、原生夹具与故障注入 |
| **V20 机制分项构筑与多策略续演**：已支持的持续机制保留六回合比较，未知组件保留结构评分份额；未知候选和可能破坏未知引擎的删牌仍回退。加入扇刀、蛇形、刀阵、腐化、临时属性、重放和消耗阈值的有限期望；免费与付费动作分开分配，消耗数量跨回合扣除。奖励/升级/商店使用共同机制估值和明确单位，商店可重复收益采用营火的剩余战斗折扣。联合搜索在原预算内保留铺垫候选，交错比较五种续演；预算截断只作诊断，增加策略/边界/重放日志。 | 用户要求按 0.39.1 后续计划进行结构改动与自检，解决整组回退和单一续演。模型是启发式，不改原生结算，不增加实盘拒绝条件；隐藏 Boss 不参与构筑。 | 用户 2026-09-28；`docs/OPTIMIZATION_PLAN_0.39.1.md`；`work/strength-20260928-v20/semantic-audit.md` 与自检记录 |
| **V14–V18 消耗战与长期构筑**：战术排名计入先于敌人攻击的手牌回合末伤害；首步保留团队减伤和清理手牌候选；弃牌/消耗的默认分支优先有害状态。V18 按用户最新指示取消额外的未来污染税，以原生后续推演计算凋萎生成、洗牌、抽回与伤害；保留当前手牌伤害。单步无收益时比较最多十步的三种连续出牌方案，盈利组合完整执行，开局候选也保留组合起手。已兑现的手牌和易伤潜力不重复扣减，持续能力按同一参照卡池比较，允许结束回合作为基准。未来团灭与当前濒死分别处理药水。营火按剩余战斗和启动收益比较升级，低升级收益席位先行动；受支持卡组使用六回合能量、能力启动、状态压力和消耗触发的配对估计，未支持机制回退结构评分。 | 沙漏复盘出现 40 格挡仍被三张 12 伤凋萎耗尽、尖啸漏选、解牌药水开场耗光，以及余像未升级、错过重振精神。V16 又实测在无护盾时留下 6 张小刀和 5 能量，定位为兑现收益被重复扣减。估计仅排序，不改原生规则或完整终局裁决；六回合构筑估值不是胜率。 | 用户 2026-09-28“按上述结论优化算法并再次测试”及逐张凋萎成本、漏打小刀反馈；`work/strength-20260927-v14/` 至 `work/strength-20260927-v17/`，以及 `work/strength-20260928-v18/` 的证据；V18 基于用户“取消污染估值，抢杀或弃牌/烧牌应对”的最新指示 |
| **测试快照恢复与共享事件等待（V12）**：共享投票清空后仍按当前选项 WasChosen 等待；新选项对象恢复投票。仅测试哨兵的 snapshot 参数可按原生保存局初始化序列恢复四个既有席位，跳过写正常档案的重载计数且 shouldSave=false。 | 训练假人连续选择三档导致各席位 encounter 冲突；从第36层原始快照恢复诊断，避免重复爬塔。恢复续局单独标记，不计为单版本完整强度样本。 | 用户 2026-09-27 报告训练假人卡死；`EventPatches.cs` / `LiveTestSnapshotResume.cs`；`work/strength-20260927-v12/sequence-audit.md` 与两次故障注入 |
| **构筑语义修正与实机战斗归档（V9）**：引擎、临时属性、条件伤害、生成小刀和消耗触发使用共享描述；缺失机制的抽手实验整体退回完整结构评价，不把未知已持有牌当零收益。升级比较同一效果的前后值，基础删牌下限仅用于普通打击/防御。测试哨兵下保存原生多人战斗包和完整开战存档。 | 原 A10 失败卡组删除恶魔形态/连环拳、契约终结忽略消耗条件、消影升级被视为永久敏捷。生命周期估值仍是显式启发式；原生回放保存不改战斗规则，也不宣称回放确定性已验证。 | 用户 2026-09-27“执行优化和第一批测试”；`DraftCardEffects.cs` / `LiveCombatCapture.cs`；`work/strength-20260927-v9/semantic-audit.md` 与故障注入证据 |
| **A10 双首领路线终点补全**：存在 SecondBossMapPoint 时，整幕路线以它为终点，路径保留第一首领及其原生子节点；机器人投票和真人路线提示共用修正。 | V6 在第 47 层击败第三幕第一首领后停住；原规划以 BossMapPoint 为终点，返回单点路径。这是对原生 StandardActMap 双首领连边的遗漏修复，不绕过首领或改动胜负。RunDepth 的“下一个首领前商店/营地”仍采用原有语义。 | 用户 2026-09-27 指出未前往第二首领；`RoutePlanner.cs`；`work/strength-20260927-v7/r2-old-route.log`，旧生产路线长度 2/1/0，修复后 3/2/1 |
| **资源效果按时机估值**：Shared CardResourceTiming 分开当前能量、下回合能量/抽牌；ShouldHold 不再要求延迟资源解锁当前手牌。持续效果估值单独计算一次性预约资源；构筑采用相同样本连续两手的可支付组合，含队友下回合抽牌收益，升级读取延迟资源差值。 | 用户指出侧步几乎不打。原代码对所有 EnergyVar 应用当前手牌能量浪费规则；Scavenge 被误列入即时能量；构筑单手实验遗漏跨回合收益。两手仍是有界估计，非完整战斗胜率。 | 用户 2026-09-27；V8 实现及 20 项故障注入证据 `work/strength-20260927-v8/`；V8e 全量 207/368 |
| **事件成长和营火有效收益**：天选芝士按可见地图剩余路径与后续幕估计战斗数，扣除即时生命成本，与实际候选两张卡边际收益比较。遗物提示与直接奖励去重。营火取消 25/55/70/85% 血线压低另一选择到 22% 的政策，直接比较有效治疗与实际升级；等待 RestSiteRoom.EnterInternal 完成进房回血，再逐席等待结算后重新读生命；援助也取消仅限 ≤25% 患者的旧门，与自疗和升级直接比较；本机使用 ChooseLocalOption 广播，并等待原生 AfterSelectingOptionAsync 完成 UI 和地图推进。 | 用户指出早期芝士被忽略、营火多次溢出。永久成长不应为零；溢出为零且回血紧迫度随血量恢复递减。取消旧血线测试属于用户授权的策略变更（R8），非单纯调低阈值。 | 用户 2026-09-27；`CheeseEventPolicy.cs`、`RestSitePatches.cs`；V8–V8e 证据，39 项故障注入；V8c 9 个营火实机推进正常；V8d 补齐援助血线遗漏，V8e 统一所有目标收益、实际执行及遗物触发时机；实机 9 营火 21 升级/5 自疗/10 援助，自疗零溢出、援助溢出 3，A10 第 47 层自然失败 |

| **营火遗物触发与援助目标统一**：两种茶具的入房收益不再额外计给 HEAL；选牌/药水奖励仅计自疗且相加，StoneHumidifier 成长计入自疗和接受援助。评分与远端回答共用 BestMendTarget，按有效恢复/成长选择患者。 | 原生全部营火治疗及入房遗物逐项审计；最低生命比例遗漏治疗量、溢出和遗物，旧估值还把无条件入房能量计成休息奖励。 | 用户持续优化营火与延迟资源授权；V8e 9 项故障注入和 207/368 全量；`work/strength-20260927-v8e/implementation.md` |
| **集火与败局进度**：Tactical 对存活敌人计后续威胁、对伤势用凹函数计价，鼓励连续打完击杀；非胜利路线按剩余敌人数、敌方 HP、存活回合继续比较，胜利排序保持不变。 | 原总 HP 线性项使伤害分散与集中打平；所有团灭路线原先都以零血打平，保留枚举首项。 | 用户 2026-09-27 明确反馈输出分散；`KernelTacticalPolicy.cs` / `KernelTerminal.cs`；`work/strength-20260927-v5/` |
| **留牌不空过整手**：存在不该保留的可支付牌时，不因另一张牌建议保留而生成优先 Pass；缓存 EndTurn 若会放弃实盘中的有收益攻击则具名重搜，仍避免致死荆棘攻击。 | 用户明确指出有打击和能量却不打。末项是用户要求的出牌策略检查，放宽原“只因不可执行而拒绝”规范的对应场景，不作为其他拒绝门的授权。 | 用户 2026-09-27；`KernelTournament.cs` / `KernelSession.HasBeneficialAttack` / `KernelCombatPlanner.cs`；旧行为回归均红 |
| **团队组合搜索接入终局比较**：主锦标赛先用约一半预算做跨席位、多目标、选牌和连续药水的多步束搜索，保留前缀后续演到终局，与原固定策略比较。每瓶均计资源成本，截断不报胜。 | 用户要求结构性、更加激进的强度优化；此处复用已有团队树搜索，避免把局部估分当通关证明。 | 用户 2026-09-27；`KernelJointTurnRun.cs` / `KernelTournament.cs`；V6 新旧对照与重放回归 |
| **败局按回合重新规划**：经历团队搜索但未找到可验证胜利时，只复用当前回合，下一实盘回合具名重搜；已找到胜利的可执行剧本继续复用。 | 对旧 R4 / 开局只搜一次政策的结构性调整，由本次用户强度优先要求授权。允许依据新的抽牌、怪物行动和药水组合寻找转机。 | 用户 2026-09-27；`KernelCombatPlanner.cs`；V6 执行回归 |
| **多人加牌与删牌按抽手增益决策**：至少 8 张牌、至少 2 席时，以同样的 16 组抽牌样本、4 类压力、实际可支付能量比较加牌前后；枚举手牌及抽牌展开的可行子集，兼顾均值和最差四分之一。全局牌分与流派作为有界先验；未知机制明示。 | 用户要求不再靠小幅系数修正。此为结构近似，不是真实战斗模拟，不消费实盘 RNG，不宣称胜率。加牌与删牌换算为一轮卡组的共同尺度；多人删牌取消固定基础牌高价，商店不重复加流派溢价。统一进入奖励、删牌和商店入口，单人保留旧基线。 | 用户 2026-09-27；`CounterfactualDraft.cs` / `BuildValue.cs`；V6 构筑对照 |
| **败局用药争取机会**：基线明确预测团灭时，采用改善击杀、输出或存活的药水；两线均团灭时，实际即时收益为正且团灭不提前也可采用，不因剩余敌方 HP 更高而囤药。团灭线不为保留药水加分；非胜利用药后重新捕获。 | 用户明确指出团灭也不用药；替代此前“只有预测必胜才用药”的政策。未知截断仍标未知，胜利线不会为败局药水被替换。 | 用户 2026-09-27；`KernelPotionPolicy.cs` / `KernelTerminal.cs` / `KernelCombatPlanner.cs`；V5 故障注入及实机记录 |
| **动作牌态取分支当前预览**：三类推演策略与 Tactical、锦标赛首步及团队搜索统一从 PredictedCard 构造语义键；原始引用仍用于定位实例。 | 战斗内升级、费用与修饰变化后，原始引用的键过期；最小升级夹具在四处旧入口分别失败。 | 2026-09-27；`KernelSession.CardStateKey`；`work/strength-20260927-v4/r2-card-keys.json` |
| **最后行动者阵亡后的自动回合推进**：所有存活席位已经结束回合时复用同一回合结算，推演不再截断为 all-ready，也不添加虚假的出牌动作。 | A10 花园精英搜索出现 `no-actor:all-ready`；荆棘夹具复现，原生 CreatureCmd.Kill 会结束阵亡者回合。 | 2026-09-27；`KernelTurns.cs` / `KernelRollout.cs`；`work/strength-20260927-v4/r2-thorns.log` |
| **暴怒按当前牌态复制**：向仍存活的每个玩家（含施法者）复制当前实例，保留升级与修饰，并转移副本所有者；两版导入器持久化。 | V2b A5BASE201 出现两次手中有 OUTRAGE 却匹配失败；原多人补全生成基础牌，与游戏 `Outrage.OnPlay` 不符。 | 用户 2026-09-27 优化；`CardEffectSpecRegistry.cs`；`work/strength-20260927-v3/implementation.md`；三项故障注入 |
| **构筑质量缺口贯穿最终选牌**：净抽牌缺口和防御质量改善同时进入适配判断与卡组规模惩罚减免，减免仍限制在 0..1；基础牌数量及 Prepared 的毛抽牌不能代替质量。 | strengthv1 第二幕多次以 size:42–50 跳过补缺卡；新增中间评分此前未完整接到 Add 的 Fit/FunctionalDeficit。 | 用户 2026-09-27 优化构筑；`BuildValue.cs` / `DeckStructure.cs`；三项故障注入 `work/strength-20260927-v2/r2-building-add.json` |
| **锦标赛剧本接入运行时待执行判断**：空手时仍先执行剧本自己的结束回合，防止运行时抢先结束、下一轮再执行旧 EndTurn。 | A5BASE202 首领战日志出现 11 次剧本外自动结束回合；原 HasPendingStep 只认分段搜索剧本，漏认 tournamentScript。 | 2026-09-27；`KernelCombatPlanner.cs`；`work/strength-20260927-v2/r2-idle*.log` |
| **跨回合驱动席位保持完整**：首步仅允许当前可行动席位；后续推演控制全部已接管机器人，当前已结束回合者下一回合重新行动；真人仍保持被动假设。 | 原实现把当前 eligible 列表永久当作 driven 列表，导致已结束回合机器人后续整场只跳过回合。 | 2026-09-27；`KernelCombatPlanner.cs` / `KernelTournament.cs`；两层故障注入证据 `work/strength-20260927-v2/r2-continuation.json` |
| **历史政策，已被 V8 有效收益比较替代**：前两幕最后营地恢复优先线提高到 70%，最终幕仍为 85%；普通前两幕营地仍为 25%。记录每个选项分数及实际选择。 | strengthv1 的 A5BASE201 在最终营地四席都升级，以 37/70、40/70、61/80、48/80 进入首领后团灭；补偿当前过度贪升级。 | 用户授权继续优化与实机验证，2026-09-27；`RestSitePatches.cs`；`work/strength-20260927-v2/` |
| **有界战术续演**：增加 Tactical 策略，每步至多模拟 8 个不同牌态/目标，按实际 HP、有效格挡、后续能力和手牌资源比较；首步枚举目标并合并同席重复牌态，保留 Kill/Defend/Growth 作为组合策略。 | 卡面数值和最低血目标不能处理护盾与过量格挡；系数是显式启发式，终局比较仍决定路线。 | 用户 2026-09-27 接管并优化；`KernelTacticalPolicy.cs` / `KernelTournament.cs`；证据 `work/strength-20260927-v1/` |
| **满槽高收益与阵亡前连续用药**：格挡按实际承伤、群伤按有效伤害、临时属性按共享能量下可打出的三次增益；预测席位阵亡时逐瓶尝试有效药水，选牌药水实盘使用后重新捕获。药水专用比较不得返回无药水路线。 | 用户要求满槽提前花药，并明确预测阵亡时尽量用完；保留具名弃剧本记录，每次药水后重新判断。 | 用户 2026-09-27；`KernelPotionOpportunity.cs` / `KernelCombatPlanner.cs`；`work/strength-20260927-v10/` |
| **每步跨席选牌与团队易伤收益**：所有续演策略从可行动且已接管的席位共同选下一张牌；搜索首步、战术状态和跨回合缓存续演计入剩余可付费攻击的易伤收益，去重包含牌主。 | 后席战士的易伤被固定席位遍历延迟到前席猎手出完牌后；铺垫先实际结算，收益是受能量、星星、格挡和目标血量限制的伤害提示，完整终局仍裁决，防御策略保留自身优先级。 | 用户 2026-09-27；`KernelTeamCoordination.cs` / `KernelRollout.cs`；`work/strength-20260927-v13/` |
| **中毒前置与顺序价值**：所有策略及首步覆盖计入两次毒跳的有界估值；咕咕冒泡优先有毒目标，构筑区分独立毒源和条件收益。 | 原模拟器条件正确，但四种策略均先花费打击和无效冒泡，漏掉蛇咬；估值为我方启发式，真实结算不变。 | 用户 2026-09-27；`CardPoisonEffects.cs`；现场同手牌探针与回归 |
| **药水按战后资源比较**：可验证胜利也允许花药保血，每瓶暂按 6 HP 计价；第二遍只评药水首步，平局和未验证截断不花药。 | 旧策略只在无药失败时尝试，无法比较有损胜利的补给收益；6 HP 是待实机校准的初值。 | 用户 2026-09-27；`KernelPotionPolicy.cs`；故障回退覆盖准入、计价、平局和截断 |
| **构筑质量补偿**：按五张手牌和固定能量估算防御供应；薄弱防御中的效率升级最多加 4 分，净增手牌不足时加 2 分；Prepared/Acrobatics/DaggerThrow/BurningPact 的弃牌或消耗同计。 | 基础防御牌数量不能代表防御质量，抽后弃牌不能充当净增手牌；保留原有协同角色。 | 用户 2026-09-27；`DeckStructure.cs` / `CardProfile.NetDraw`；上游 OnPlay 核对与构筑回归 |

| ~~丢剧本后交给旧版规划器（而非重搜）~~ **已被下一条取代** | 用户指定 | 用户 2026-09-19 |
| **限步前瞻：`LookaheadRounds=5` + 段间重搜** | 用户规格 2026-09-20：算 5 回合、按剧本打完、再从实盘算下一段，一局是**几个连续剧本**。预算 5/10 分钟 → **3/8 分钟**。首次尝试（只改 `MaxRounds`）已证伪并回滚，见 §7 | 用户 2026-09-20 |
| `ContinuationSource` 独立对象 | 照搬 CombatSolver 的结构 | 用户 2026-09-20 |
| 投影差异只报告不抛异常 | 上游单人可 throw，联机不能失败关闭 | 设计 |
| 开局搜索阶梯（5min + ≤5×1min + 报错） | 用户指定；**扩展已改为真续跑** | 用户 |
| 血量按血池比例计价（`referenceMaxHp / maxHp[i]`） | 上游单人无此问题；战士卖血流被低估 | 用户 2026-09-20 |
| `MembershipCard` 去掉 `LocalContext.IsMe` | 折扣属于遗物持有者，持有者不一定是本机 | 多人修正 |
| 未建模的 `OnPlay` = boundary（跳过该牌） | 上游行为；估算过的牌不能进剧本 | 用户 2026-09-20 |
| 逐步漂移 / 边界漂移**只记录不拒绝** | 上游不逐动**比对** | 用户 2026-09-20 |
| 搜索心跳日志（S5 前置） | 纯日志静默判死的必要前提 | 已做 |
| 哨兵文件 `mods/CoopBots/live-test.flag` 触发无人值守开局 | 经 Steam 启动时游戏不是本进程子进程，环境变量传不进去 | 实机管线 |
| 实机开局**默认**走多人 4 BOT / A10 / 从地图开始爬（`LiveTestMultiplayerRun`）；`live-test.flag` 里写 `mode=single` 才回到"单席直进怪物房"（`EnterRoomDebug`） | 单席那条路测不到任何"Host 才亮"的门（地图投票多人分支、商店 ack、药水按席归属、多人牌团队目标）；保留它只为复现"进不了局/进得了局"的二分 | 实机管线 2026-09-20 |
| 实机开局用 ENet 房主 + `LobbyBotService.Add` 填满 4 席（不是 Steam 大厅） | 纯 UDP，不需要 Steam 大厅/第二个账号，也不留会话残留；顺序照抄游戏的 `NMultiplayerTest.StartHost` | 实机管线 2026-09-20 |
| 寻路回避「当前 act 造不出来的房间」（`RoutePlanner.IsBuildable`） | 原版 `TreasureRoom` 只支持 act 0-2 而 `CreateRoom` 不检查 act 索引，Glory（act4）的宝箱节点必抛异常并卡死整局；这属于 R4 允许的**可执行性**拒绝，且每次跳过都写日志 | 用户 2026-09-20 |
| 锦标赛剧本缓存：首张牌找到的胜利线保留，后续每次重搜若没有任何候选的终局**严格优于**缓存剧本，就继续走剧本的下一步 | 上游 CombatSolver 只在分段搜索里保留 `ContinuationSource`；锦标赛路径原来只取首动作、逐张重搜。2026-09-23 实盘终局：第一张牌 `predicted=VICTORY`，之后 80 次决策全是 `WIPE hp=0/0/0/0`，胜利线被丢掉后团灭 | 用户 2026-09-23 |
| **锦标赛接管人机混合局**：`TournamentDrives` 只要有至少一个驱动席位就为真；非驱动真人席位在 roll-out 中按“结束它的回合/空过”模拟。`humansFinished=false` 时 `IncludeEndTurns=false`，避免执行层拒收 EndTurn 后每帧重跑。 | 用户 2026-09-23 产品决定：不再维护全 Bot 与人机混合两套打牌算法。真人的正贡献不计入 Bot 的获胜计划，真人负贡献由真人负责；真人已经发生的动作仍由实盘 capture 精确吸收，每步重规划不依赖长剧本。 | 用户 2026-09-23 |

---


| V19 控制药水去重与可见 Boss 构筑 | 固定同一剧本比较去掉首瓶的原生结果；即时防护按后续控制后的边际收益计入；远期团灭不再丢药水剧本。第三层公开沙漏时共享清理估值给拿牌、商店、删牌、升级，区分随机/定向/一次性，饱和递减，不读隐藏第二 Boss、不加局内污染税。 | 用户 2026-09-28；`work/strength-20260928-v19/THIRD_ACT_REVIEW.md` 与回归、R2 证据 |

| 对象池原生信号包装采用上游 7129c6f 的作用域释放，由 CoopBots 壳层 PatchAll 安装；原生 Variant 键延迟至真实遍历时创建 | 内核排除了上游 Entry，原生命周期补丁从未被注册；2026-10-04 实机 Array.Finalize 访问冲突后补齐接线。保留解绑条件、递归及对象池账本，只释放本次取得的包装，不释放借用节点。此缺口已确认，是否为本次 AV 的唯一来源未有转储证实。 | 用户 2026-10-04；NodePoolLifetimePatch；work/array-crash-20261004/report.txt |
| **已易伤 + 长战优先部署 CRUELTY**：敌方当前带有易伤、且战斗预期剩余回合数 > 3（即没有找到三回合内胜利的剧本）时，把 CRUELTY 的持续收益按**整场剩余回合**计价并乘以一个明确的优先级常数，使其压过当回合的普通出牌。 | 上游只按 setup 视野给持续型 Power 估值，`KernelSession.ExtendedPowerValue` 把回合数截在 3（`turns+1`），所以超过 3 回合的战斗里 CRUELTY 的 +25% 永远只值 4 回合——实机 2026-10-05 A10 终局 Boss 起手手里有 CRUELTY 却一次没打，全程少约 25% 输出。该常数是**策略**（用户要求"极高优先级"），不是测量值；两个门槛（已易伤 / 超过视野）均可由夹具单独证伪。 | 用户 2026-10-05；`KernelSession.PowerValue.cs`（`CrueltyLongFightTurns` / `CrueltyLongFightPriority`）；`PowerPriorityScenarios` 三条比较，R2 去掉分支后 long+vuln 486.4→15.2 变红 |
| **Boss 开局清空药水**：Boss 遭遇的**第 1 回合**把所有可用药水全部喝掉，不评估收益；走与"腰带已满"相同的"先执行、再重新捕获重规划"路径。**2026-10-05 追加浪费闸门**：加牌/抽牌药在目标手牌满 10 时不给（能指定队友就给手牌有空的队友，给不了就留瓶）；能量药只给"手牌正费牌总额 > 现有能量"的席位（自己花不完找队友，都花不完留瓶）；其余瓶子行为不变。 | 上游按战斗终局比较决定是否喝药，所以我们见过"团灭时身上还有 3 瓶"。用户 2026-10-05 要求直接把这种情况消掉；同日追加"倒瓶也不许浪费"（10 张手牌还喝抽卡药、能量花不完还喝能量药）。生效范围仅 Boss 第 1 回合，且每瓶一次（喝下即清剧本重捕获），所以未建模的 CHOOSER 药水（SKILL/COLORLESS/POWER）也会被喝掉而不是被估值挡住。这是**消费政策**，不是"这瓶值"的判断；跳过不等于放弃——瓶还在腰带上，后续常规判定仍可花掉。 | 用户 2026-10-05；`KernelPotionOpportunity.Choose(bossOpening:)`、`BossOpeningTarget`/`EnergyNeed` + `KernelCombatPlanner.TryPotionOpportunity`；`PotionOverlapScenarios` 两条比较 + `KernelEngineScenarios.AuditBossOpeningPotionWaste`（满手加牌/抽牌不吃、能量给缺的队友、真缺仍吃），R2 把闸门改回"自己优先"后红在 "must go to a teammate with hand space" |
| **女王爪牙标注为「可先打」的保留线**：`KernelPriorityAdds` 把 TorchHeadAmalgam 标成优先爪牙；`KernelTeamSearch` 每次裁剪保留一条对它推进最深的线（爪牙已击杀优先），`RolloutPolicyKind.KillAdds` 先打它、但"这张牌能击杀主要敌人"仍最高优先；`KernelCombatEvaluation.Growth` 与 `TeamFocus.Work` 把女王在场时的 +1 力量/回合计入成长。默认其它政策的目标优先级不变，胜利条件仍只认主要敌人（`RemainingOpposition` 不动）。**同日范围校验**：只允许这一个标注；未标注的次要敌人不再计入 `KernelCombatEvaluation` 的进度项（标注爪牙按半价），会复活的次要敌人（`Illusion/Adaptable/Reattach/Infested`）在 `TeamFocus` 里排到最后——胧光怪房曾把火力倒在会复活的幻影爪牙上导致主怪过慢、战损过大。 | 上游的通用目标选择只看主要敌人/最低血量，非主要敌人血量对局部估值不可见：2026-10-05 A10 女王战 11 回合无人以聚合体为目标，四席死亡全部来自它（聚合体只吃到 AoE/毒）。用户要求"给 Queen 的爪牙标注一下，允许 Bot 先打爪牙"——是**允许**不是强制：保留一条策略线交给终局比较，默认仍对拼主要敌人。 | 用户 2026-10-05；`KernelPriorityAdds`、`KernelTeamSearch.ProtectPriorityAddLine`、`KernelRollout.KillAdds`、`KernelCombatEvaluation.Growth`、`TeamFocus.Work`/`RevivesWithoutProgress`；`KernelEngineScenarios.AuditQueenPriorityAdd` 3 条 + 胧光怪幻影范围断言，R2 三处改坏分别红在 "KillAdds must open on the claw"、"the search must retain a claw-targeting continuation"、"the plan must attack the main enemy, got PARAFRIGHT"；实机证据 `outputs/bot-wipe-triage-20261005.md` |
| **高价值战斗允许延长搜索时间**：`HighValueTournamentBudget` —— 终幕 Boss（0-based act≥2）全回合 ×3，其它 Boss 开幕 ×2/后续 ×1.5，精英 ×1.5，普通战斗不变（High 档基数 3200ms/趟，两趟共享 2×，终幕 Boss 单次决策上限约 19.2s）。 | 用户 2026-10-05：高价值场景允许更长的搜索时间（P0-2）。实机 2026-10-05 女王战开局 8 条候选 0 条评估完、参考续演吃光预算。延长的是**允许思考的时间**，不是断言"更久=更准"；搜索按 6ms slice 分帧，不阻塞渲染。 | 用户 2026-10-05；`KernelCombatPlanner.HighValueTournamentBudget`/`TournamentBudget`；`NightDecisionScenarios.Combat` 断言普通/精英/普通 Boss/终幕 Boss 四档，R2 把终幕系数改回 1 后红在 "high-value rooms get more thinking time" |
| **易伤轴「变现端」团队通道 + 决策层接线**：`TeamCoordinator.PayoffBonus`（`DebuffChannel` 的镜像：全队易伤供应 ≥2 时 +14、1 时 +8，按全队读者数与本席副本衰减，供应/读者读快照的 `vulnerable`/`vulnerable-payoff` 角色）。读者名单在 `VulnerablePin`（手工表：DOMINATE、MOLTEN_FIST）；**BULLY 故意不入表**——牌组模型的 allyVulnerable 已按全队供应给 Bully 计价（夹具 22.5→39.0），再入通道就是同一份供应计两次价。新增 `BuildValue.AddForDecision / RemoveForDecision / UpgradeForDecision` 决策包装（取牌再乘 0.5 份额上限，删除与获取互逆），取牌（BestReward/GreedyPick）、商店买卖（CardGold/RemovalValue）、删牌（RemovalPlan/DoorsOfLightAndDark）、升级（营地 smith/事件/古代/PaelsTooth）全部改走包装；纯 `Add/Remove/UpgradeDelta` 仍是卡组模型的精确差（edit 恒等式由 `UnifiedDeckStrengthScenarios` 钉死）。 | 实机 2026-10-05 A10 四人（seed 2B1D65QTZ1F2）：全队维持 8-27 层易伤，B1 商店 74 金（现金 113）Bully 未买，S-jie 两次 Molten Fist 拾取仅 0.7/−1.9 分；供应侧 TAUNT/BASH 拿满 team-fit，变现端没有通道。此前团队值只出现在日志 traits 里，Bot 的取牌/删牌/升级读的都是纯卡组差，等于没接线。两个通道都是有界启发式（≤18 且取牌再乘 0.5），不是胜率模型。 | 用户 2026-10-06「按 A 执行……除了 Bully 之外，主宰也要同步加入回归……升级、删牌时都要考虑」；`VulnerablePin.cs`、`TeamCoordinator.PayoffBonus`、`BuildValue.*ForDecision`；回归 `TeamEconomyScenarios.VulnerablePayoffFollowsPartySupply`（Bully 三条编辑路径 + 通道不重复计价）与 `PayoffValueReachesRemovalAndUpgrade`；R2 `work/triage-20261006-wipe/r2-mutation.log`（关通道 → "0.00 -> 0.00" 红）与 `r2-mutationB.log`（包装去掉通道 → 互逆 3.34/−2.23 红）；门禁 `work/verification/20261005T180950737Z/summary.json`（verify-project.ps1，普通 345/内核 514） |

| 偏离 | 理由 | 来源 |
|---|---|---|
| **本轮联机原生一致性与完整导入保留**：能力 Target/Applier 独立、首次敌方多人倍率、每击全目标先扣血后死亡、有效菜单数量/源/完整候选匹配；导入保留既存源码支持并全生成集合对照。 | 1JJ0XFWWNLTC 出现真实字段、Flutter 和少牌菜单分叉；上游仅支持单人。我方联机适配必须复用已有原生批量事务，并证明对应原生结算。没有放宽漂移判断。 | 用户 2026-10-08「修复本轮查出的问题」；`LatestRunModelRepairScenarios` / `LatestRunChoiceRepairScenarios`；证据 `work/latest-run-repair-20261008/`，四门与 R2 以最终记录为准。 |
| **本轮搜索与构筑决策修复**：先建立不嵌套整手搜索的完整参考，保留原生两步牌序；Tactical 未完成也共享预算，Defend/Growth 均先于 beam；当回合重复按真实可付后续和剩余计数，能量按 X 费及组合支出兑现；遗物交换按净损益、Sly 承诺费按可达免费支付、消耗选择按未来保留价值。 | 最新局出现末点能量浪费、零效果 X 牌、投资错序、独立防御不足和局外把成本算作收益。策略只提议动作、比较真实可达收益，路线及知识恶魔固定瓦解保持原要求。Prepared 没有机制错误证据，保留既有先验。 | 用户 2026-10-08；`LatestRunSearchRepairScenarios` / `LatestRunBuildRepairScenarios`；既有预备打击临时属性与剑柄升级政策继续回归。 |

| **Boss 开局格挡消费的保守补充（2026-10-10）**：先让其它现有可执行开局瓶完成并重捕获，保留原格挡目标；只有有限已知牌、无相关 hook/附加牌效果且固定结算尾证实零收益时跳过格挡。未知状态沿用原消费政策，不限制模拟合法性。 | 延续用户已授权的开局用药及避免浪费要求；无出牌结算尾不能推断 Calamity 等生成攻击收益，不能把未知当成零。 | KernelPotionOpportunity/PostDecisionPotionScenarios；private-block-expiry-v2 封存原生与物理 R2，旧 v1 两项实际 R2 保留，Steam收益待验证。 |
| **可选 B1 调用与预览复制采证（2026-10-10）**：CardBranches 原 Core 逻辑不变，启用时仅包裹各次 MoveNext 并在 yield 前关闭作用域；以编译调用位置分类，不用对象指针推断重复根。PredictedCard 仅测原有 initial/shared 的两个 Clone 与 PreviewStorage 赋值。 | 固定预算实机高分配/GC需要区分轻量排名、正式选择与复制阶段；上游无 CoopBots 诊断，关闭时直接原 Core，观测本身不声明性能收益。 | b1-caller-preview-execution-v1 sealed；0.41实际生成raw目标一致、3个measurement-only物理R2及完整状态off/on对照。 |

| **多人历史攻击窗口（2026-10-10）**：own-turn pending独立于effective共享current，按实际玩家Advance推进previous；原生entry round/side/全席turn快照捕获为不可变origin，Fork只用owned映射。待推进历史牌的字段、引用和origin进入完整合同与指纹。 | 单席上游在end-turn提前Commit不足以表达死席及额外回合非参与者的窗口；修复真实P0-SIM-25，保持完整资格规则。 | KernelTurns/AutoPlay/Fork/PlanningState；精确历史与retained导入登记，sealed原生/物理R2见v12 integration receipt；完整门禁和Steam交付另记。 |

| **有界已结算策略前缀（2026-10-11）**：暂停参考在预算截止时保存原生已结算动作、逐动作检查点及席位/敌数组；与完整终局分开保留，仅无已接受结果时提供前缀，取消仍撤回。 | 单席上游无本地暂停参考调度；修复多人固定预算下参考丢失，不改变合法性、终局排序、路线及总预算。 | KernelTournament；实际原生/R2及精确源码交付见 v13 integration receipt，完整门禁/实机另记。 |
| **Afterimage完整状态（2026-10-11）**：读取原生实际空内部字典，捕获独立空 CardPlayPair；非空仍无可复用证据。 | 上游已有 Before/After 镜像，漏根状态捕获；不制造 CardPlay 身份、不忽略字段、不放宽未知状态资格。 | PowerPredictionStateSupport/Planning Facts；既有 retained 适配登记，真实空/非空原生及R2封存。 |

| **怪物条件有效事实（2026-10-11）**：完整Facts调用既有ResolveConditional，对已覆盖动态条件使用分支当前有效值；未知fallback及其他AI字段严格保留。 | 单席上游没有本地完整Facts合同；用户R9要求消除跨根静态lambda影子值失配，P0-SIM-26当前游戏原生证据支持。 | KernelPlanningState.Facts；2026-10-11 F18现场、具名原生/R2封存与root v14 integration receipt；不改变怪物预测/资格/预算/路线。 |

| **疯狂之触完整药水选择链（2026-10-11）**：本地KernelPotionChoices在既有Prepare/Complete之间按原生目标手牌费用资格生成菜单，原始序号、来源与完整token进入已结算动作；六处PotionPlan生产者及网络ACK/UsePotionAction验证同一瓶槽和菜单。Kernel捕获映射与Core全部药水优先保留仍持有的原瓶身份（仅普通药水保留既有ID回退，Touch不回退）；仅含Touch的PotionsOnly阶段先覆盖每个物理瓶，再在既有TopK内补选。 | 当前上游单席胶水没有本地多人确认/控制合同；F28开发局未用Touch及真实双瓶生产链支持。其余八类chooser仍拒绝，未托管真人不代选；Vendor改动0，不属于导入适配。 | B10；KernelSession/KernelPotionChoices/KernelChoices/KernelTeamSearch/KernelTournament/KernelPotionPolicy、KernelCombatPlanner/EmergencyPotion、BotRuntime/BotChoicePlanSync；具名原生及有限R2、root独审接受/完整门禁收据在 `work/v15-touch-root-delivery-20261011-v1/`；无界面46步非终局，Steam收益另验。 |

## 7. 当前状态（活文档，每轮更新）

- **2026-10-12 v19强制Touch生产通路原生/R2通过，待合并门禁和实机**：v18完整门禁869 PASS、冻结D783BD45并部署，保存F31知识恶魔原生WIN；完整开发001在1540.984秒后F36 DEVOTED_SCULPTOR_WEAK触发300次planned-choice P0，无整塔自然终局，异常保留尝试分母。满带生产者发布Touch但Choices为空，执行守卫正确拒绝；Boss开场同形路径也修复。两处改用现有Touch-only PotionActions(maximumChoices:1, controlledActors)，同请求停止检查、无新增预算/裸回退，守卫及其余8类chooser不变。四项原生默认检查实际覆盖Choose/Core/同步/喝药，不能冒称BotRuntime.Tick或历史F36完整同根；旧Touch12/跨回合9项保持，撤唯一生产修复实际RED、1281源码/31工件精确恢复GREEN。seal E29D11DF在 work/v19-touch-opportunity-private-20261012-v1/sealed-v1/，root工作在 work/v19-touch-opportunity-root-delivery-20261012-v1/，最终门禁/冻结及实机以收据为准。路线/构筑权重/High6/预算/固定瓦解维持，留出未用、50%及混合桌未达成。

- **2026-10-12 v18知识恶魔条件记录修复准备交付**：原生复现v17保存F31 P0，第三次CURSE后完整语义仅旧条件记录CURSE→SLAP不同，实际下一招与模拟Advance均已正确。ResolveConditional补齐owned counter<3规则，PONDER与有效Facts共用；未知fallback、全部Facts及资格守卫保留。原生新12阶段/3PASS，旧原生0–8阶段；实际仅撤规则RED、1280源码/31工件精确恢复GREEN及独审通过，证据 work/v18-knowledge-conditional-private-20261012-v1/ 与 work/v18-knowledge-conditional-source-review-20261012-v1/。仅生产1源、新夹具和默认接线合入；root唯一完整门禁、直接复用双DLL、保存F31及全A10实机按 work/v18-knowledge-conditional-root-delivery-20261012-v1/ 新收据顺序推进。路线/构筑权重/High6/预算/瓦解选择不改；完整v17未启动，50%与混合桌尚未达成。

- **2026-10-12 v17保存F31严格AI Facts异常已关闭，v18私有原生验证**：v17合并门禁866 PASS/0 FAIL，直接复用工件冻结FC327A95并部署。保存入口217.875秒后第10回合P0正常关闭，watcher2/launcher0，无费用空引用/原生终局；封存 work/v17-root-integration-20261012-v1/saved-F31-P0-closure.json。ai-facts的23/9是字符串长度编码：旧Static条件保留CURSE，fresh原生是SLAP；Advance已经按分支counter推进，EffectiveAiStatic调用ResolveConditional未覆盖KnowledgeDemon所以回退旧根值。实际动作是否一致仍需第三次CURSE及后续PONDER同根原生Facts/RNG证明，不能先认无害或修改P0守卫。完整v17开发epoch仅准备未启动；当前唯一私有SDK v15基于frozen17验证共享条件规则/R2，Main生产仍17、Steam关闭、无竞争游戏。路线、权重、High6预算及固定瓦解保持；保存异常不计全塔，留出未用、目标未达成。

- **2026-10-12 v17合并候选待完整门禁**：v16完整开发AR10B7M2X001在1538.984秒后F31自然团灭，1尝试/0胜、无观察到P0，正常关闭/清理封存。真实付费THE_BALL转交、抽回复现GeneratedFollowupValue费用空引用，Original保留旧席、Preview.Owner已转交；7次历史异常逐个实参未捕获。v17续搜资格/过期终局/药水顺序3生产源及对应测试、当前分支持有者7生产源和4项默认原生回归已整合，收据 work/v17-root-integration-20261012-v1/integration.json 与 owner-integration.json；私有原生、High6、5组真实RED/精确恢复GREEN及独审通过，尚未完整门禁或部署。Ponder构造防御证人原生Facts/RNG/结算匹配，痛击死亡对照防御存活2血，正式固定预算搜索自主选防御；此前2HP偏差为私有A10初始化顺序，生产未改，不代表历史同根已复现。私有SDK全部释放，root独占一次合并完整门禁；直接复用门禁工件出包，再保存入口及完整开发Steam测试。构造成本收益不冒称整局收益，路线、权重、预算与固定瓦解保持；留出未用、50%及混合桌目标未完成。

- **2026-10-12 v16本地候选源码**：基于 frozen-v15 F31手牌P0，保留已结算回合开始选牌答案并交给原生菜单。跨回合完整语义对齐但预测资格仍environmental-risk，不能称qualified完整Facts。371 PASS后的2轮→80轮合同终点失配保留于 `work/verification/20261011T192313067Z/`；精确回放现保留原终点，Joint与药水续算显式预测setup并回贴最后实际动作，原Joint断言80及两项实际R2通过。383 PASS后的Hidden(0)即时空选牌序号问题保留于 `work/verification/20261011T195319724Z/`；空手牌只跳过未来租约，即时PlanSync仍消费原记录。原14个选牌断言未改，实际撤修复RED1、1154源码/10工件精确恢复GREEN0，证据 `work/v16-hidden-empty-menu-native-20261012-v1/`。
- **v16后续救援回归（同日）**：完整门禁 `work/verification/20261011T202226975Z/` 构建/类型加载0、574 PASS后原CachedDex断言失败，空选牌14均已通过。实际诊断：原末End From1→To2/Owners4，有限救援max1只到From1→To1/Owners0，严格回放拒绝使基准在喝药前为空。新增ForecastCausal与严格回放共享动作处理，但重新记录自身未来合同，仅Emergency采用；原max1/64步/共享截止保持。私有原救援14、跨回合9及原Joint80通过；仅撤回调用接线已真实build0/RED1，恢复及最终封存以 `work/v16-cached-dex-contract-native-20261012-v1/` 收据为准。新交付目录 `work/v16-turn-start-root-delivery-20261012-v7/`；完整门禁、包、双DLL部署及Steam只认新收据，当前仍未出v16包。历史R3完整同根未复现；路线、构筑权重、High6和总预算保持，50%及真实混合网络目标未达成。

- **2026-10-12 v15本地测试候选**：`v15-touch-choice-20261012-local`，Touch选择、确认和选牌/普通药水同名双瓶执行链按独审及原生/R2证据精确合入。一次新完整门禁、双DLL冻结和Steam终局由状态/收据记录。v14 F28自然团灭保留；隔离正式搜索总剧本99=Touch1+续演98，预测胜利不视作原生胜利。路线及既有预算保持，目标未达成。


- **2026-10-11 v14本地候选**：`effective-monster-ai-20261011-local`，P0-SIM-26具名原生/R2封存后精确合入有效条件Facts；一次新默认完整门禁与双DLL冻结/Steam由状态和收据记录。v13 F18异常保留，构筑权重未改变，目标未完成。


- **2026-10-11 v13本地候选**：`bounded-prefix-20261011-local`精确合入，完整门禁一次834 PASS/0 FAIL，包/双DLL冻结并部署。感染棱柱受控保存战斗胜利，6次求解/99张kernel牌、无观察到P0；保存局不进全塔分母。首完整开发局第18层按P0-SIM-26正常停止，planned1/natural0/P0fail1/wins0；关闭/旗帜/锁已验证，原始失败保留。原生复现修复与新门禁优先，质量/分配优化暂缓，50%及混合桌目标未完成。证据见 `work/architecture-refactor-20261009/fixed-budget-performance-v3/full-a10-v13-development-v2/root-full-a10-closure.json`。


- **2026-10-10 v12 候选**：`history-domain-20261010-local`仅合入已复现的历史窗口修复，原生与实际故障恢复封存。随后执行一次完整门禁、复用工件出包，先正常保存入口复测再固定开发种子整塔；B1采证已完成，性能优化和50%通关率未获证明。


- **2026-10-10 v11 合并候选待完整门禁/实机**：阵亡席位ready修复、保守开局格挡政策及B1可选成本采证均以封存源码精确合入，独立审核与各自原生/R2完成；交付标签`dead-seat-ready-opening-cost-20261010-local`。随后只执行一次完整门禁并直接复用工件出包，保存第28层再测及正常完整A10分开归档。原v10第28层三席死亡发生在ready失配前，ready修复不证明减少战损；B1目前仅采证，不宣称成本或胜率改善。



- **2026-10-08 SZG复盘问题修复并重新部署（准备手动测试）**：用户授权修复并安装。选牌原生完整续演、未来实际菜单重捕获、牌序/实际支付估值、永久敌力量全队代价、失败参考在既有deadline内续算、多人生成RNG与开始回合屏障、虚拟阳伞免费购物及五项构筑效果已修；路线输入字节不变，知识恶魔固定瓦解、临时属性及剑柄升级守卫通过。四门 `work/verification/20261008T170109408Z/summary.json` pass/exit0，普通 **546**、内核 **718** PASS；1229最终输入及实际测试DLL与门禁匹配。独立R2审计 `work/szg-repair-20261008T124624/final-r2-audit.json`；旧守卫保留。独立标签 `szg-run-repair-20261008T124624-local` 打包smoke通过，ZIP SHA `329C752B2E6E8D17744D3BD18740CA6F306079C92148A0BC7A757107ADD6C433`；Core `3B2F82A1A5CF69AE173D770951DF9F19DF51ECEF45D166CCF324F4D9F777F820`，Kernel `7A4DCB4AAD5CAFF149085F93E10E0968FFC08CC9BE89F45CD216EB6F01B18D3F`，四安装文件与包/门禁完全一致，旧目录完整备份 `B:\slay-the-spire-2-mod-mod\work\deploy-backup-20261008T173936255Z-pre-szg-run-repair-20261008T124624-local\CoopBots`。版本仍0.39.1，未启动游戏、无live-test.flag、未发布。最终报告 `work/szg-repair-20261008T124624/report.zh-CN.md`。本轮原生机制证明不等于历史整场隐藏RNG重放，有限策略与构筑估值不保证最优或该种子必胜；新局网络与通关效果待实机。下面“仅分析/未修”为对应历史时点记录。

- **2026-10-08 新部署后实机复盘 SZG1RJZ6XVL6（仅分析）**：核对实际0.39.1 CoreE50B42…2520D/Kernel57037F…0245B与最新测试包一致，sts2.dll同原生门禁，1211编译输入SHA不变。A10第三幕45层实验体R9团灭、终敌1220HP；营火后309/316HP，开场药水后首牌315/316，沿途残血不是本次主要解释。1207出牌、16场完整遭遇及数值seed6414142435591804501对齐；旧工具带数字事件名/空stop正则误报已纠正，真实38次joint。终战开局172步WIPE剧本，joint仅Kill完成、三策略截断、nodes0；R1激怒11技能加33力量并损106HP，二形态再损84+54。现场局部错序含代打熔岩之拳→欺凌+23伤、Bot1精准→两小刀+18攻击量/步法→生存者+3格挡、Bot2步法→防御+6/+3格挡；Bot3先战斗专注封掉耸肩抽牌属强嫌疑，同RNG反事实待做。14次赌博筹码仍旧HandValue不兑现状态组合；未复现可指名主动烧核心、无新临时属性永久化证据。Bot2领主阳伞+53.44估值但非本地虚拟席位没有原生免费购物通路，F36实花227，局外引擎续演缺项确认。三次早先战斗漂移登记S9新P0-SIM-12/13（逃牌多席生成RNG序、沙漏/城垛side钩子时序），未闭环或修复。报告`work/live-triage-20261008T121207/report.zh-CN.md`及三子报告。未改生产/测试/安装目录、未跑新门禁或原生全战历史重放，不宣称必胜；路线、固定瓦解及既有升级偏好保持。

- **2026-10-08 最新团灭复盘修复测试包已部署到 mods（准备手动测试）**：用户明确授权部署。完整四门475/647通过，独立标签`latest-run-repair-20261008T093435-local`打包exit0、smoke348；包内和安装后的两DLL均与`work/verification/20261007T215935141Z`工件SHA全等。Core `E50B42DC8AC0AC9DC5110A8D9410E116075EDF57CD80B420F4F5E7119802520D`，Kernel `57037F17FE02E165DE13E6D75B858606290B46D04F4375722B35C76F5270245B`，ZIP SHA `55D590860CB26F4BACA02AC6B9EA2FBA3A3C6EB693292009E22CFDF9B05BB4C2`。已安装`B:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\CoopBots`，四文件SHA和十个策略符号通过，1211个编译输入不变；旧目录完整备份`B:\slay-the-spire-2-mod-mod\work\deploy-backup-20261008T014222280Z-pre-latest-run-repair-20261008T093435-local\CoopBots`。首次部署校验误用了源文件名KernelEnergyConsumption作类型名，已自动回退并核对旧DLL原SHA；改用实际EnergySpentThisTurn后重新安装，原无效记录保留。版本0.39.1，路线/固定瓦解/临时属性与剑柄升级偏好保持，无live-test.flag，未启动游戏或发布。证据`work/package-deploy-latest-run-repair-20261008T093435-local/report.zh-CN.md`、`deployment-verification.json`及`package-verification.json`。下面源码阶段“未部署/已安装DLL未改”是部署前历史状态。

- **2026-10-08 最新团灭复盘源码修复验收完成，未部署**：修复联机能力身份/倍率、同击批量死亡事务、少牌及嵌套选牌桥/升级过滤/事件初始化，补独立防御成长预算、有限原生牌序、供能真实支付、临时重复次数与构筑净成本/保留价值。完整四门 `work/verification/20261007T215935141Z/summary.json` **pass/exit0**：6既有警告/0错误，811类型与无Kernel JIT，普通无过滤 **475** / 完整内核无过滤 **647** PASS。96组有效R2各自编译成功、具名RED1、精确恢复GREEN0；37个聚焦机制/集成功能PASS（牌效、支付与倍率等含原生对照）及最终干净导入独立编译/原生验证通过（星代费原生支付子项在energy-limits内实际调用）。0.41入口418生成源规范化全等，331既存支持源任务前SHA全等，8既存差量显式保留，旧0.33入口原字节不变。首轮四门因旧Hands快照失败，独立三次768×18字段对照隔离为5个含TrueGrit+窗口；只改Hands哈希，22向量/排序/16求值/64次2MiB守卫保留，再两项故障注入证明守卫有效。Core `E50B42DC8AC0AC9DC5110A8D9410E116075EDF57CD80B420F4F5E7119802520D`、Kernel `57037F17FE02E165DE13E6D75B858606290B46D04F4375722B35C76F5270245B`。报告 `work/latest-run-repair-20261008/repair-review.txt` 与 `final-verification.json`；路线、固定瓦解、临时属性/剑柄升级政策及0.39.1保持，已安装两DLL任务前SHA不变。未出包/部署/新实机，未证明整场必胜或所有历史P0关闭。 最终压力修复：cheap Tactical候选排名仅取首条原生选择分支，正式提交重新完成全部选牌比较与tokens/ordinals；浅排名可能低估其它选择收益，宽搜索不变。1600ms总预算、3:1:1分配、16对排序配额与旧完成断言均保留；stress-repair两项真实故障注入通过。

- **2026-10-08 最新实机复盘（仅分析）**：已核对刚部署Core290A…2C68/Kernel1806…28F0的A10种子1JJ0XFWWNLTC；45层第三幕实验体真团灭，入场312/315（Bot1为67/70，其余满血），代打R3/Bot2R8/余两席R10死亡，终敌剩665。日志/同场1791395728.run/meter以XxHash64、四席及17场完整ID独立对齐；通用工具meter及joint摘要误报已排除，实际44次joint/15次nodes0/9次paths0。3个边界+5个HiddenDaggers选择分叉共8次丢脚本，S9登记待修；终战Defend/Growth多次预算截断。明确局部线索含代打R3同费防御替撕咬可存2HP、Bot3随机坚毅烧Turbo后0X Cascade、代打燃烧/残酷晚于攻击局部损31伤、Bot1爆发无后续技能、Bot2余像漏投资；局外遗物交换失去项被加正值。详见`work/live-triage-20261008T023749-local/report.zh-CN.md`及子报告，区分局部原生公式推导/强嫌疑/未做同根整战反事实。未修改生产代码、测试或已安装MOD，不运行新的门禁或部署，不宣称这些改进可保证整场胜利；路线及固定瓦解维持既有要求。

- **2026-10-08 局内/临时属性/抽牌升级测试包已部署**：用户明确要求部署到`/mods`准备测试。独立标签`incombat-temp-draw-20261008T014010-local`打包exit0、smoke347，包内两DLL与完整四门438/609工件SHA完全一致（Core290A…2C68、Kernel1806…28F0）。ZIP SHA`A9B4BE8EC53DB613C4C2879D7C09B69F37BCC3E68BCFE5D46BD59D25D0D32847`；已安装`B:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/CoopBots`，四文件SHA及八个策略符号通过。覆盖前旧目录完整备份`work/deploy-backup-20261007T174637936Z-pre-incombat-temp-draw-20261008T014010-local/CoopBots`，旧DLL12AB…EB18/5190…3D6D。版本仍0.39.1，路线不变、知识恶魔统一瓦解；无`live-test.flag`，未启动新局或发布，待用户手动实机。证据`work/package-deploy-incombat-temp-draw-20261008T014010-local/report.zh-CN.md`及部署/打包验证JSON。以下源码阶段的“未部署”是部署前状态。

- **2026-10-08 局内打牌、临时属性及抽牌升级源码修复验收完成，未部署**：用户限定先不改路线，并追加预备打击单回合力量/敏捷及12张基础牌组的剑柄打击升级优先级。已实现原生选牌后续演、防守/成长开局参考、失败缓存当前回合保命、冻结回合抽牌/能量修复；临时属性未来残余/参与者/监听顺序、构筑实际牌序、独立净抽牌升级政策已补。精确原生前缀复用及Joint前置比较已通过原预算检查，null与显式空选择元数据分别保留。完整四门 `work/verification/20261007T170328682Z/summary.json` **pass/exit0**：生产6既有警告，801类型及ModEntry无Kernel JIT通过，普通无过滤438/完整内核无过滤609 PASS。127组有效R2（106既有+Joint3+前缀18）均独立逐原始日志审计；错误expected的初次ordinal故障记录单列保留排除，组数不等于独立根因数。Core `290A2628EC41B280010725ACDACB47E8180357FE8AE28626E3E610E91AFA2C68`、Kernel `1806E58A5DBBD07A19D8B3CB6EE3D04E1DFE6DF1E723EE65567C0DEB62AF28F0`。门禁后的58项输入中56字节不变，两测试文件仅追加真实RED注释，可执行逻辑不变；前四次失败完整记录及旧政策/反射守卫保留。报告 `work/incombat-repair-20261007/review.zh-CN.md`，工件/命令见`final-verification.json`。路线、固定瓦解、版本0.39.1和已安装两DLL保持任务开始状态，未出包或新实机；历史完整Shuffle/HP/敌阵P0及构筑Artifact组合未由本批机制夹具关闭。

- **2026-10-07 决策修复测试包已部署到 mods（准备手动测试）**：用户明确要求后执行S4。包 `outputs/CoopBots-v0.39.1-decision-repair-20261007T113451911Z-local.zip`，ZIP SHA256 `BBD5712A99D8768EECC5104BD1F94E2EDF075F1C255A9054899E77CFE7B6D736`；`build.ps1 -GameData <本机数据目录> -Label decision-repair-20261007T113451911Z-local` 退出0，打包smoke **347**。包内及安装到游戏 `mods/CoopBots` 的两份DLL均与完整四门 `work/verification/20261007T094145007Z` 的 **366/537** 工件SHA256一致（Core `12AB2F6C…C118EB18`、Kernel `51904626…DDBA3D6D`）；安装后固定瓦解、选择失配清缓存、NativeReferenceOpening、ProbeCache、EnsureOwnedPower关键符号均存在。旧MOD完整备份 `work/deploy-backup-20261007T114102482Z-pre-decision-repair-20261007T113451911Z-local/CoopBots/`。版本保持0.39.1，无 `live-test.flag`，游戏未启动；未声称实机验证通过。部署证据 `work/package-deploy-decision-repair-20261007T113451911Z-local/deployment-verification.json`、`package-verification.json`；此前源码验收的“已安装DLL不变”是部署前历史记录。

- **2026-10-07 知识恶魔团灭复盘后的源码修复完成（未安装）**：按用户要求，实盘、自动续演和待决搜索全部固定选瓦解，取消Debuff择优。修复有限原生牌序比较、队友前置衔接及消费前完整手续演，同次决策复用完整分支后立即失效；联合搜索先完成确定合法短prefix参考，再扩beam，总预算保持。补永久可跳单选、腐蚀波/暴怒/毒及重复触发、四删煤灰与真相碑估值；补交易工具原生弃牌、额外回合参与者、死亡历史、选择/边界失配清缓存、Ringing当前牌身份及七类Power父子写入隔离。四门 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-project.ps1` 退出0：构建0错误/6原有警告，类型加载780，普通 **366** / 完整内核 **537**，CARD COVERAGE broken=0。门禁 `work/verification/20261007T094145007Z/summary.json`；90组有效R2（88生产原生、1独立导入原生、1导入哈希守卫）已逐份审计。旧Long/Strategic/消耗防御阈值保留；性能快照只在独立旧新DLL逐字段证明后重建毒语义导致的Hands摘要，其他样本/Decks/资源守卫不变；旧Ambergris unsupported断言替换为原生支持、有限地平线及未解决准备事务保护。证据 `work/decision-repair-20261007/review.txt`、`final-verification.json`。已安装DLL与原有发布文案不变，版本仍0.39.1；未打包、部署或新实机复测，未证明整局必胜/多种子胜率改善；**P0-SIM-6/7的历史2HP和两次敌阵分叉具体原因仍未恢复，不关闭**。

- **2026-10-05 P0-SIM-1/2 修复已打包并部署到 mods（准备实机测试）**：用户开口后执行。包 `outputs/CoopBots-v0.39.1-p0sim-pt-victory-guard-20261005.zip`，zip SHA256 `2dd8d1f274802bdec48617a93d183a59706c12a3bed1bf3554ba4ca49a24d74d`；包内两 DLL 与完整四门工件 `work/verification/20261005T150527684Z` 哈希一致（`283e573a…1d4939` / `eb6cdacb…507f6b`），打包 smoke 通过。已备份旧 DLL 到 `work/deploy-backup-20261005T233010-pre-p0sim-pt-victory-guard/`，部署到 `mods/CoopBots` 后哈希对齐并扫到关键符号（kernel `ResolvePlannedCard`/`TargetedPowerState`/`SandpitCountdowns`/`card-state-changed`/`;PT=`；mod `victory refused`/`CoopBots victory guard`/`sandpits-live`），无哨兵文件。**未启动游戏**；实机验证走 live-test 管线，P0-SIM-3/4/5 仍未修。部署细节 `work/deploy-p0sim-pt-victory-guard/deploy-record.md`。

- **2026-10-05 模拟一致性 P0-SIM-2 修复（VICTORY 采用守卫 + 逐步部署身份）**：① 胜利记录不再是"校验过就照打"：`KernelCombatPlanner.TryAdoptRefinement` 在采用 `Verified+Victory` 前断言"至少一名预测存活席位"与"边界/实机 `TargetedPowerState` 逐席一致"，并按名字拒绝；采用日志新增 `CoopBots victory guard: … postHp=[…] sandpits-boundary=[…] sandpits-live=[…]`，下次伪 VICTORY 不用再从 order 快照反推。② 部署解析收成一处 `KernelSession.ResolvePlannedCard`（原四处同形实例兜底，R3）：动作记了 state key 时，实例兜底必须先通过 key 复查，升级/改费/附魔后的同对象牌以 `card-state-changed` 具名拒打；`TryEmitFromTournamentScript` 的拒绝日志现在直说 `reason=card-left-hand|card-state-changed`。③ 夹具 `KernelConsistencyScenarios` 新增两段：refinement 胜利守卫（无存活席位 / 定向 Power 不一致 / 合法胜利三例）和部署身份（状态漂移拒打 + 四席同名牌按 owner+key 解析不串席）。**R2 变红原文（三处）**：关掉胜利守卫 → `System.Exception: P0-SIM: a victory with no surviving seat must not be adopted`；关掉 state-key 复查 → `…a card whose state changed since verification must not be played`；把解析换成错误席位 → `…the planned step must resolve to the acting seat's own card`；三处恢复后全绿。四门 `scripts/verify-project.ps1` 退出 0：build 0 error（6 原有警告）、TypeLoadCheck 通过、普通 **343**、内核 **512**（510+2）；门禁 `work/verification/20261005T150527684Z/summary.json`。**未验证**：无实机复测；P0-SIM-3（4 处边界漂移）、P0-SIM-4（4 次 replay 不确认）、P0-SIM-5（敌阵不匹配）仍未修。未打包、未部署、未启动游戏。

- **2026-10-05 模拟一致性 P0-SIM-1 修复（定向 Power 逐席身份进边界校验）**：核对结论：`ContinuationStamp` 的边界文本对 Power 用有序 `P=` 段，实机侧 `Creatures.Powers` 与模拟侧 `EffectivePowers()`（hook listener 顺序）枚举口径不同且都不含 `Target`；而本局的 `SandpitPower` 语义完全在 Target 上（逐席交换翻转"谁被倒计时处死"）——这正是第 6 回合 `new=VICTORY` 与实机全灭之间唯一被确认"文本表达不了"的通道。修复：`AppendPowers` 追加 `;PT=` 段，按 `(owner,type,target)` 排序渲染 `owner:type=amount@target`，使逐席交换与枚举顺序都无法再被掩盖。夹具 `tests/PatchSmoke/KernelConsistencyScenarios.cs`：两块 `P=` 序列相同但逐席语义不同的盘面必须得到不同文本；同语义不同枚举顺序的 PT 必须相等。R2 变红原文：删 PT 块 → `System.Exception: P0-SIM: targeted powers must be compared by (owner,target) identity, not by position`。四门 `scripts/verify-project.ps1` 退出 0：build 0 error（6 个原有警告不变）、TypeLoadCheck 通过、普通 **343**、内核 **510**（509+1）；门禁 `work/verification/20261005T144302517Z/summary.json`。探针补充测量：同一盘面 `THRASH_MOVE` 模拟伤害 2×8 与实机一致（S/B1 均 16 raw），未发现敌人伤害模型分歧。**未验证项**：没有实机复测证明该伪 VICTORY 的直接成因就是这个通道；P0-SIM-2..5（VICTORY 逐步守卫、4 处边界漂移、4 次 replay 不确认、1 次敌阵不匹配）仍未修。未打包、未部署、未启动游戏。

- **2026-10-05 伪 VICTORY 归因完成 + 模拟一致性 P0 登记（只查、只登记，未修代码）**：seed RFTC0WGM4A3P（A10，第二幕 Boss THE_INSATIABLE，floor 31）第 6 回合 refinement 采用 `new=VICTORY enemyHp=347->0 validated=1`，同一份 40 步剧本实机走到第 16 步四席全灭（沙坑处决 B2/B3 + THRASH 击杀 S/B1）。诊断探针 `tests/PatchSmoke/SandpitVictoryProbe.cs`（`--sandpit-victory-probe`）证明：① 分支逐席沙坑正确（捕获 1/2/1/1）；② `FranticEscape` 只加自己（B2 1→2）；③ 把该剧本灌回模拟器、在同一实机盘面上模拟器自判团灭；④ 生产 `TournamentRun` 从该盘面能找到的存活线都要求给对应席位打逃牌，而实机剧本只给 S 打了逃牌。④ 生产 tournament 在实机分配下唯一能活的线是 **B2 打 FRANTIC_ESCAPE**（`script1: player=793474 FRANTIC_ESCAPE`），而实机第 11 步是 **S-jie** 打 FRANTIC_ESCAPE。结论：**"被验证的胜利"与"实机执行的剧本"在某一层分叉**（已证实到分叉存在）；两个候选根因——(a) 边界盘面的逐席状态不同而校验没抓到（嫌疑：带 `Target` 的 Power 身份不在可见状态文本里、实机/模拟枚举口径不同，`ContinuationStamp.AppendPowers` 只渲染 owner+amount，实机 `Creatures.Powers` vs 模拟 `EffectivePowers()`）；(b) 采纳脚本里是 B2 的逃牌、部署执行成 S 的（跨席同名牌身份/归属错位）。两者都已按用户指令登记为 P0（S9 P0-SIM-1/2），未定论前不挑一个当结论。按用户指令新增 §2 **R9**（实机/模拟任何不一致 = P0）与 §5 **S9**（P0-SIM-1…5：定向 Power 身份、VICTORY 守卫、4 处边界漂移、4 次 replay 不确认、1 次敌阵不匹配），每条附证据、修复方向与夹具验证要求。归档 `work/live-triage-20261005-night/`（日志+sha256、探针输出、`report.md`、`report-deck-rootcause.md`）。本次未改生产代码、未打包、未部署；P0-SIM-1/2 修完前，该局任何胜利记录都必须标注"未通过逐席一致性校验"。

- **2026-10-04 原生数组终结器崩溃风险修复**：Windows19:00:33记录Array.Finalize→GC.RunFinalizers；微软匹配PDB将coreclr+0x2e9302解析至ProcessCLRExceptionNew的损坏状态FailFast。确认上游NodePool信号包装释放补丁随Entry排除而未注册，现由壳层PatchAll安装，三个清理方法与上游7129c6f逐项一致；四处Bot UI遍历不再产生GetChildren原生数组。三项R2退出1后恢复，完整四门退出0：普通340/内核501，构建6原有警告0错误，类型766，broken为空。证据work/array-crash-20261004/report.txt；门禁work/verification/20261004T124553428Z/summary.json。没有保留崩溃dump，未证明本次AV唯一来源或实机不复发；未打包、部署或启动游戏。

- **2026-10-04 决策一致性本地测试包**：用户要求打包；`scripts/build.ps1 -Label decision-consistency-20261004-local` 退出0，打包smoke335 PASS。产物 `outputs/CoopBots-v0.39.1-decision-consistency-20261004-local.zip`（1,759,383字节），zip SHA256 `6A52B7061D0A22F44A470D36A1BED5E5542C2AF5FE55A9F8265FE931CF2907C0`。包内两个DLL均与完整门禁 `20261004T074309418Z` 的331/492工件哈希一致；6项内容及zip完整性校验通过。证据 `work/decision-consistency-20261004/package-verification.json`。未安装、未启动实机、未上传。

- **2026-10-04 决策一致性修复（完整门禁通过）**：复盘方法写入S3 reference；知识恶魔预测/实盘共用惩罚估计，旧胜利逐实盘回合复验，未来减益固定比较窗口且保留新力量收益，最后一费续演保留长期减益；遗物选牌读取力量/多段/X费。构筑改非负场景权重，补自动出牌、能力启动、禁止抽牌、全队消耗降费与Tank团队承伤；正社区先验只计首张，重复副本靠实际增量，现场第二/三/四张准备变负。快照136补齐四步三小刀、多18伤害；三张Boss稀有牌转正。剑柄升级+3.5386仍低于熔岩+5.3013，第三张挑衅+仍被模型高估值，未声明整体升级排序/防御偏好彻底解决。33次故障注入恢复后四门退出0，普通331/完整内核492，构建6原有警告0错误，类型761，broken=0；门禁 work/verification/20261004T074309418Z/summary.json。旧断言与快照变化及失败记录均具名审查；证据 work/decision-consistency-20261004/review.txt。未打包、部署或实机重跑。

- **2026-10-04 午间复盘深化及路线/消费政策落实**：第一幕尚未打过精英且仍可达时强制路线包含精英；未建模遗物采用15分正先验但保留已建模零收益；购买门槛随富余金币下降并逐次重算，实际卡组/商店重建445金先删防御150，再以295金买BREAD185。旧1万金囤币断言按用户新政策改为买；固定六商店删牌样本均值3.5→3.75，接受上界3.5→4，保留下界和无零删牌要求。9组R2故障均退出1后恢复。完整四门退出0，普通324/完整内核485，构建6警告0错误，类型加载757；门禁 `work/verification/20261004T052816477Z/summary.json`。另已定位但未修：剑柄升级受干扰表现改善仍被相对差值罚分；三张Boss稀有牌缺团队承伤/自动打牌/全队消耗降费；知识恶魔预测Sloth每回合限3张、实盘选崩解，导致三名队友R6各打3张就结束，6→13边界差异只丢后台候选、旧VICTORY仍豁免补打；未来易伤价值的ceil台阶使多6伤害反降约49分。局部替代动作Bot1多15伤害/11格挡但少3易伤，Bot2多6或18伤害，Bot3多18伤害/5格挡但仍可能被敌方攻击击杀；未证明整战必胜。证据 `work/live-triage-20261004-noon/followup-report.txt`。本轮未改构筑强度公式/局内内核，未打包部署。

- **2026-10-04 午间最新日志诊断（仅分析）**：种子6PN9S2QAVZKR，A10，第31层知识恶魔R8团灭，Boss最后快照401HP；安装DLL与act2-priority本地包一致。确证烘焙手套按静态基础数值消耗旋风斩（同可见状态原生局部对照186 vs130伤害）、Midnight基础12费遭7936能耗罚分忽略全队消耗减费、商店3遗物全0估值、445金零购、拳->痛击5易伤而反序8、非攻击回合挑衅被防御替代；重建升级比较显示剑柄正即时收益被抗干扰项压负。第一幕0精英的可选时点最低HP均不足70%，第二幕唯一精英强制路径；不能盲目提高精英数量。Boss双预算生效，6/6联合搜索有Tactical，后台4启/1保留旧线/0更优采用/3边界差异（额外手牌、HP、解体6->13）；全局19边界差异。证据work/live-triage-20261004-noon/report.txt、probe2.log、metrics.json；未修改生产代码、未打包部署，未证明整场反事实可赢。

- **2026-10-04 第二幕决策修复与本地包**：主怪进度/短斩杀原生验证、Defend能力投资及同宽生存候选、死亡席位开回合/召唤目标身份修正、有效边界后参考失效恢复搜索；脑蛭强制奖励集合、心灵震摄与队友能量估值补齐。16个故障注入退出1后恢复，含缓存分片轮询与固有排序快照。`scripts/verify-project.ps1` 退出0，普通322/完整内核483，生产6警告0错误，类型加载756；门禁 `work/verification/20261003T201948771Z/summary.json`。`scripts/build.ps1 -Label act2-priority-20261004-local` 退出0，打包smoke335；包内两DLL与门禁哈希一致，ZIP SHA256 `81C501AE4DCA5DF01DBF65F0FDC29DD24BBC102F7BACD49AFEAE733FB405787D`。包 `outputs/CoopBots-v0.39.1-act2-priority-20261004-local.zip` 包含此前战栗修正，未部署/启动/上传。证据 `work/act2-priority-20261004/review.txt`、`package-verification.json`。旧日志额外手牌与Boss血差11尚未完整复现，仍需实机验证，未宣称整战能赢。

- **2026-10-04 战栗与持续减益消耗保留修正**：按原生分支实际易伤/多回合虚弱收益解除等待防御时机的保留偏好，既有易伤不设层数上限；回合末补打计入结算后持续减益，Bully层数伤害提示与已有易伤的协同估值补齐，主宰完整层数兑现由原生验证。普通/升级战栗、Shockwave、9/30层易伤、Artifact及牌序覆盖；4个R2故障退出1后恢复。两次完整门禁发现Bash伤害重复计入/计算伤害泛化影响防御排序，已收窄修正，旧断言未放宽。最终 scripts/verify-project.ps1 退出0，普通319/完整内核480，生产6警告0错误，TypeLoadCheck751类型；证据 work/exhaust-duration-20261004/review.txt、work/verification/20261003T191135600Z/summary.json。未打包/部署，本次日志仍是旧包。

- **2026-10-04 最新爬塔诊断（Y4VXRK7BEPMM，只分析）**：A10 第31层知识恶魔 R7 团灭，最终 Boss 1094 HP；胧光怪前战承伤260、战后四人共46 HP，休息后 Boss 入场共163/300 HP。胧光怪 R8 日志可见手牌/状态的原生局部对照，痛击+→愤怒→愤怒→打击集中主怪可当场结束，实际分火爪牙延长到 R9；原生支持主次敌人，估值与执行中短斩杀纠偏仍不足。Boss R4 Bot2 后空翻后丢灵动步法，局部改为后空翻→灵动步法→残影，下一回合2HP/0敏捷/0格挡→7HP/2敏捷/7格挡，少34伤害；未证明整战获胜。Boss 双预算 compute11710ms 生效，8/8 joint 有 Tactical，后台7次启动/2次采用、更优仍WIPE；手牌、参考可打性、敌HP与能量存在分歧。脑蛭SHARE强制五选一被按五张总和估值；MindBlast无抽牌堆时漏伤害，BelieveInYou把队友能量加给自身。证据 work/live-triage-20261004-latest/report.txt、probe.log（退出0）；隐藏历史/RNG未完整还原，新诊断问题未实施修复。

- **2026-10-04 战斗修复与附魔优先级本地测试包**：`outputs/CoopBots-v0.39.1-act2-enchantments-20261004-local.zip` 已生成，包含知识恶魔 P0/P1 修复及 22 种附魔静态优先级。`scripts/build.ps1 -GameData <本机游戏数据目录> -Label act2-enchantments-20261004-local` 退出 0，打包 smoke **335** 项。包内两份 DLL 与完整四门 **318/479** 的 `work/verification/20261003T181145217Z` 工件 SHA256 一致；内容校验 `python work/package-20261004-act2-enchantments/verify-package.py` 退出 0。ZIP SHA256 `6159267F0460D718BB7527D36F4B149EB86DDCD271BE1EC52214CA91E219C968`，版本保持 0.39.1，旧包未覆盖。证据 `work/package-20261004-act2-enchantments/package-verification.json`。未部署或启动游戏。

- **2026-10-04 附魔目标优先级接入完成**：将用户 `EnchantmentsTable.csv` 的 22 行译为 `EnchantmentPriorities.Table` 冻结静态表，原 CSV 保持不变。原生合法范围后先排斥、再比偏好档位，同档保留原评分；按用户澄清，Corrupted/Instinct 非 X 费候选按一次打出的多段总伤害排序。Clone 对应 PaelsGrowth 事件固定 −100。可选附魔取允许目标，已进入强制选择时仅按原算法补足原生最低数量；有替代事件选项时提前避开排斥目标。三个原生重载及异步入口均携带附魔类型；远古选项估值与实际目标共用策略。14 组 R2 故障均退出 1 后恢复，未删除旧断言或下调门槛；完整四门 `scripts/verify-project.ps1` 退出 0，普通 **318** / 完整内核 **479**，构建 **6** 个原有警告、0 错误，类型加载 **751** 类型。证据 `work/enchantment-policy-20261004/review.txt`、`work/verification/20261003T181145217Z/summary.json`。条件伤害沿用现有静态模型（3 能量/1 敌人），未跑实机、未打包或部署。

- **2026-10-04 知识恶魔 P0/P1 修复完成**：非胜利剧本结束回合前以同一敌方结算时点比较补打短牌序，覆盖技能/抽牌/防御，移除即将失效能量的虚假收益；更优结果直接拼接当前回合，避免为简单补打重开完整锦标赛。Threat 补上可格挡崩解；动态群攻统一去除旧单体目标。后台 WIPE 后续采用后保留下一回合前台重搜，候选必须独立原生重放通过才发布；ready 任务最终弃用也输出原因。15 组 R2 故障均退出 1 后恢复；旧补打攻击断言从强制丢剧本改为验证实际发出攻击，保留行为保障且已故意破坏验证。完整四门 `scripts/verify-project.ps1` 退出 0，普通 **314** / 完整内核 **475**，构建 **6** 个原有警告、0 错误，TypeLoadCheck **738** 类型。现场局部重建邪眼使下一回合 HP **24→48**，R6 耸肩/双防御分别 +9/+16 HP、死亡前补打 +33 伤害；未证明整战获胜。短比较默认 80ms 为软预算，冷 JIT 实测约 142.9ms，已确保不会因基线耗时而零比较。证据 `work/act2-fixes-20261004/review.txt`、`work/verification/20261003T173008215Z/summary.json`。只改局内，未打包、部署或启动游戏。

- **2026-10-04 第二幕知识恶魔团灭诊断（只分析）**：A10 种子 DKQNYYE3NCMV，31 层 R11 团灭，Boss 剩 127 HP；安装 DLL 与 idle-refinement 本地包一致。原生重放 231 快照确认 R7 托管席位坚毅后留邪眼/2 费结束，局部 A10 对照补打可使 R8 HP **24→48**，五种现场续打均使用邪眼。R6 Bot1 留耸肩/1 费、Bot3 留两防御/2 费，对照下回合 HP **22→31、5→21**；Bot2 留 4 费和攻击/小刀生成，局部可多打 33 伤害但仍死亡。EndTurn 补救只检查攻击，且前后状态评分可能奖励未花能量；Threat 漏算可格挡的崩解，完整回合结算已支持。上轮续搜采用 WIPE 基线后无条件清除 `tournamentReplanNextRound`，R5 接受旧线→R6 参考 invalid-target→R6/R7 未重搜，已定位回退缺口。Boss 双预算实际 compute **12582ms** 生效；整局 joint **46/48** 有 Tactical，后台 **44** 次启动/**20** 次采用/**12** 次更优；本场 Boss **11/4/2**，更优仍 WIPE。存在两次小刀动态目标拒绝，后台 11 次 unplayable + 1 次 invalid-target；完整成因未全部复现。构筑 Expose 覆盖 0 仍靠 +13.02 先验入选，另有过牌先验偏高迹象。最终诊断探针退出 0，报告 `work/live-triage-20261004-act2/report.txt`；局部重建未完整恢复隐藏历史/RNG，未证明整战能赢。未改生产、未打包/部署、未重跑生产门禁。

- **2026-10-04 本地测试包**：`outputs/CoopBots-v0.39.1-idle-refinement-20261004-local.zip` 已生成，包含全队减伤/变牌/营火协作修正、Boss 首回合双预算和执行期间下一回合优化。`scripts/build.ps1 -GameData <本机游戏数据目录> -Label idle-refinement-20261004-local` 退出 0，打包 smoke **335** 项；ZIP 中两份 DLL 与完整四门 **310/471** 工件 SHA256 一致。ZIP SHA256 `335BFB25C3E258E44C69065E657D3C3F0DB015361865EF5E4E40116F28650B50`，版本保持 0.39.1；内容校验脚本退出 0。证据 `work/package-20261004-idle-refinement/package-verification.json`。仅交付本地包，未部署或启动游戏。

- **2026-10-03 23:59 女王诊断后修正与执行期间续演优化完成**：防御续打计入非格挡技能的全队实际减伤，消耗牌小收益保留按全队合计承伤判断；互斥用能牌比较合法后续，能力投资对照不再用 Kill 开局覆盖 Tactical。联合搜索优先完整比较同一前缀的 Kill/Tactical；Boss 首回合预算翻倍，药水共享预算同步扩大。随机变牌按原生卡池、克隆 RNG、全卡组变化及 BingBong 复制估值，多选依次推进；营火联合比较剩余受控席位的动作与治疗目标，计入自休和确定的缩放仪入场治疗，并将 MEND 患者传到执行。按用户追加要求，所有存活席位受 Bot 控制时，出牌/动画间隔用 2ms 协作切片预算优化下一回合；当前回合冻结，新旧后续从同一起点比较，完整候选严格更优且实盘状态匹配才替换，不等待未完成搜索，平局保留原路线。20 组 R2 故障均退出 1 后恢复；旧尖啸保留测试修正为全队合计 2 点暴露（原来实际为三队友各 2 点），原断言保留并重新证明会变红。完整四门 `scripts/verify-project.ps1` 退出 0，普通 **310** / 完整内核 **471**，生产构建 **6** 个原有警告，类型加载 **737** 类型。证据 `work/decision-followup-20261003/review.txt`、`work/verification/20261003T154834899Z/summary.json`。2ms 为软预算；未跑新实机，未证明胜率或实机帧耗改善，未打包/部署。

- **2026-10-03 22:32 女王团灭诊断（只分析）**：A10 种子 EPA0PVNNSQKP，第 45 层女王 R8 团灭；安装 DLL 与 decision-fixes 测试包一致。园丁 R3 三队友各掉 8 血已定位：局部原生探针 `ShouldHold=False`，尖啸可免掉队友 24 点承伤，廉价续打偏好防御；能力投资预览会把同一差基线带回 Tactical，关闭嵌套预览后选尖啸。TRIAL 普通变牌仍按删牌值选两张疑虑，原生 RNG 精确复现变成扭动/内疚，BING_BONG 又各复制一张；局部整组估值从虚假的两次 +44.6 变为 −69.16。第二幕 Boss 前 MEND 漏算目标自休和缩放仪，实际 37 点协助只有 13 点入场收益，24 点被后续治疗挤成溢出（患者仅自休+缩放仪为 63/76，非满血）。最终女王前四席均自休，入场 237/420 HP，R5 末三席死，最后原生状态女王仍 1191 HP；未证明有获胜路线。68 次 joint 中 33 次无被接受的 Tactical，完整廉价参考不代表精细比较充分。证据与建议 `work/live-triage-20261003-night/report.md`，最终探针退出 0。未改生产、未打包/部署，未重跑生产门禁。

- **2026-10-03 日志归因 skill 与本地测试包**：S3 现在按执行/模拟覆盖/局内策略/搜索预算/局外估值与选项分层，详细方法在 `coopbots-live-log-triage/references/decision-attribution.md`；加入同场证据归档、反事实重放、能力与消耗/遗物/资源升级案例和结论强度，修正用低伤害或低能量浪费直接归因构筑的旧说法。S7 已链接该方法，打包及验证文档明确区分 packaging-smoke 与完整四门。本轮仅改文档；4 个 skill 格式校验通过。测试包 `outputs/CoopBots-v0.39.1-decision-fixes-20261003-local.zip` 已生成，打包命令退出 0、smoke **334** 项；包内两份 DLL 与上一轮 **307/468** 完整门禁工件 SHA256 一致。zip SHA256 `B314E18DC2145E2924AD002502F52A0936843746A657E0AAA2A47CBE2565DEFA`。未部署、未启动实机；证据 `work/skills-package-20261003/package-verification.json`。

- **2026-10-03 最新团灭的决策修正完成**：消耗技能共用原生收益/使用机会判断，根节点排序及预算回退不再绕过保留偏好；低威胁保留、致死压力、消耗抽牌与永久升级均有回归。联合搜索先建立轻量完整参考，首轮关闭嵌套探测后再细比；赌博筹码、ToastyMittens、Toolbox、ChoicesParadox 按战斗结果手牌选择，实机与续演共用。补齐已建模小刀生成的伤害及熔刃交响曲队友收益，队友收益不能制造本人斩杀免伤；升级日志显示抽牌/能量变化。17 项有效 R2 故障变红并恢复；四门退出 0，普通 **307** / 完整内核 **468**，生产构建 **6** 个原有警告，TypeLoadCheck **723** 类型。受控四人场景约 3.2 秒完成 **22** 条终局比较（上一版同场景 10–11），不代表实机胜率。**验收调整明确记录**：固定商店样例从 3,4,3,3 变为 4,4,3,3，取消“至少三席位删 2～3 次”的分布要求，保留平均 2～3.5 及无零删牌；性能数值快照因生成牌伤害修正而重建；神化旧空战斗牌堆测试补上真实升级目标。短/长战斗直接混分试验未采用。知识恶魔惩罚取舍未改，未打包、未部署、未跑新实机。证据 `work/decision-fixes-20261003/review.txt`、`work/verification/20261003T120250536Z/summary.json`。

- **2026-10-03 P0/P1 局内修正及药水复用完成**：多形态 Boss 的终局进展/持续价值读取原生剩余形态血量；弃牌/消耗比较合法续打，不再把已离手的牌当作可打动作。新抽能力及能量花掉后补回均触发有限重评估；串行/并行续演按步限时并保存完整参考，轮转开局策略。药水复用已有打牌路线比较并继承结果，紧急建模药水保留剧本，完整剧本阻止重复用药搜索；未知满栏药水保留原生处理。十组 R2 故障变红后恢复；四门退出 0，普通 **304** / 完整内核 **465**，构建 **6** 个原有警告。四人后期牌组受控夹具约 3.2 秒完成 10–11 条终局比较（旧 DLL 同夹具 9 条）；不宣称分配量稳定下降或实机胜率提升。36 个局外构筑文件哈希不变。证据 `work/combat-quality-20261003/review.txt`、`work/verification/20261003T072953223Z/summary.json`。未打包、未部署、未跑新实机。


- **2026-10-03 通用投资与牌序回归完成**：所有合法能力牌参加两回合原生投资比较；五种续打策略比较 AB/BA 及后续兑现，删除主宰专用排序。保留不同能力及结束当前回合后的投资路线，锦标赛轮流比较开局。Panache 普通/升级版在生产联合锦标赛优先启动，五连打实际触发 10/14 伤害，保留立即斩杀和保命反例；撕裂有/无触发对照与胜利重放通过，战士/猎手 76 个版本完成投资试算。七组改坏变红；四门退出 0，普通 301、完整内核 462，构建维持 6 个警告。证据 `work/strategic-lookahead-20261003/findings.txt`、`work/verification/20261003T051317860Z/summary.json`。本轮未打包、未部署、未跑新的实机爬塔。

- **2026-10-01 prior15 营火卡死补丁**：实机快照 `work/prior15-live-stall/godot-snapshot.log:9732` 记录营火尚有 Bot 计算时离场，原生 PopCurrentRoom 触发异步取消，而房间 Exit 永久等待该席完成。新增 `RoomWork`，营火整队任务与所有构筑任务按房间计数；继续按钮与地图投票统一等待营火选项、商店结算、事件结束及原生选项任务。九组故障注入变红、恢复定向通过；完整回归结果看状态工具，不声称已实机验证。

- **2026-10-01 prior15 测试候选**：用户在得知正确 A10 六商店结果为 3/4/3/3 后指定 −15 并要求打包。生产基础打击/防御先验已改为 −15；先验原始社区表不改。完整回归保留旧 Bully 购买与 2–3 次频率失败，不能声称四门全绿。正常包门禁没有放宽；本次用户指定参数的实验包需随包记录失败和验证证据。详见 `docs/DECK_OUTCOME_OPTIMIZATION.md` §18。

- **2026-10-01 奖励界面性能回退已确认，尚未修复**：用户最新日志奖励阶段间隔 43/49 秒；独立同输入四席暖批次旧版 5.206 秒、新版 20.137 秒，约慢 3.87 倍。奖励入口漏用单次决策缓存，日志重复算分约占 49%；新增压力手牌搜索与成长预测放大成本。诊断缓存降至 6.696 秒，仍不够流畅。累计托管分配 43.38 GiB 不是常驻内存。已安装 DLL 与下述候选相同；本轮仅诊断，生产未改。证据 `work/reward-latency-audit/`、`docs/DECK_OUTCOME_OPTIMIZATION.md` §11。

- **2026-10-01 鲁棒性与共享易伤候选实现**：统一 S 接入九回合凋萎/锁链压力，A10 三幕 λ=0.80/0.90/1.00；开手/循环共用实际抽弃与 Sly。多人易伤按存活队友的攻击供应回报施加者；四战士 A10 起手删除痛击 −2.157，优先删防御，100 金商店买 Bully、无 Bully 则删防御。六店探针删 3/3/1/3，历史八店重建仍零首选删除。23 个 R2 故障；四门普通 270 / 完整内核 431，记录 `work/verification/20260930T174810875Z/summary.json`。社区 514 屏整体 39.11%→36.38%，四人 37.50%→38.26%，猎手 38.04%→47.83%，战士/机器人回退；**整体效果未验证通过，未跑新实机**。详见 `docs/DECK_OUTCOME_OPTIMIZATION.md` §10；取代下行“生产未改”状态。

- **2026-10-01 Prepared / Anticipate 审计与鲁棒性定义，生产未改**：十个同价商店构造场景均选 Prepared，但起手增量 +7.438 中先验 +10.880、战斗 −3.222；隐藏抽牌 / Sly 后战斗评分不变。Afterimage + Wound 中清理贡献 +3.260，不能说弃牌价值全部为零。新鲁棒性建议按同等干扰损失差计入统一 S，先补毛抽牌/实际弃牌/Sly，再替换既有污染与可见 Boss 清理项，尚未接入系数。诊断 `--prepared-anticipate-audit`、`scripts/audit-prepared-anticipate.py` 均退出 0；四门普通 268 / 完整内核 429，生产 DLL 与上一包相同。证据 `work/prepared-anticipate-audit/`、`work/verification/20260930T162309119Z/summary.json`；详见 `docs/DECK_OUTCOME_OPTIMIZATION.md` §8–9。

- **2026-10-01 卡组收益算法候选版完成，未安装**：统一商店机会成本、整张牌支付与有限战斗收益、后续成长已落地；修复新选择器漏算临时属性和 Grand Finale 条件。100 金商店选 Bully，无 Bully 时删基础牌优于 Bludgeon；六商店四组删 3/3/2/4，含基础牌 1/2/1/2。社区 514 次一致率 39.49%→39.11%，猎手 44.57%→38.04%；历史八次商店仍零删除，**没有效果验证通过的结论**。19 项 R2；四门普通 268 / 完整内核 429，记录 `work/verification/20260930T155559445Z/summary.json`。独立测试包 `outputs/CoopBots-v0.39.1-deck-outcomes-20261001T000619.zip`；详见 `docs/DECK_OUTCOME_OPTIMIZATION.md`。

- **2026-09-30 统一构筑评分**：以 `docs/UNIFIED_DECK_STRENGTH.md` 为本轮说明。三个入口已共享卡组差值；社区记录按实际对局去重，日志锚点与受控六店探针分开报告。旧 Elo-first、候选选择模型与删牌独立保底相关条目为历史政策。未用新算法跑实机，不声称胜率或真实整局删牌频率提升。门禁读数看最新 `state.py show`。

- **2026-09-29 V31 按出场频率补三个事件，未上实机**：上一轮指出 T4 那四个事件实机 0 次出现，真正高频的是 `SELF_HELP_BOOK`(8)/`WELCOME_TO_WONGOS`(8)/`STONE_OF_ALL_TIME`(8)，本轮做这三个。**自修手册**的附魔 id 藏在 **StringVar** 里（`SHARP`/`NIMBLE`/`SWIFT`），效果从游戏本地化文本读出后用 `MarginalCore` 自己的权重定价，**牌组没有该牌型的一页记 0**；**时之石**两侧都定价（药水储备值 vs 永久最大生命；`VIGOROUS` 首打伤害 vs 掉血）；**王戈斯**是商店，价格取自事件变量，`LEAVE` 会把一张已强化的牌降级所以**走人也有成本**，没有预测器的货架**不盲买**。**R2 三组红原文**与门禁 **267 / 428** 见 `work/verification/20260929T140545870Z` 与 `docs/OPTIMIZATION_V31_TOP_EVENTS.md`；handler 注册表 **25 个**。过程记录：本轮一次批量正则误伤了字符串字面量 `"Gold"`，**是既有断言先红的**（`Sunken Statue must dive…: dive=-7.0`），已修。**无实机数据**；未做 T6 代价侧 / `TRIAL` / T5 其余。

- **2026-09-29 V30 事件 T4 完成（诅咒/永久代价换收益），未上实机**：`EventTrade` 加 `BestUpgrades`/`PricesCurse`/`GrantsWithCurses`（诅咒张数由 handler 显式传入，但只在选项没自己计价时才补收，**同一个诅咒不能收两次**）。新增 `LOST_WISP`、`SPIRIT_GRAFTER`、`DROWNING_BEACON` 三个 handler，并删掉通用打分器里那段只减最大生命、从不给遗物计价的内联特判。**`TRIAL` 明确不做**：`GUILTY`/`INNOCENT` 跨页重复且效果不同，且本地化文本与预测器对 INNOCENT 的描述互相矛盾（"诅咒+强化 2 张" vs Transform 结果包），等实机样本裁决而不是猜。**R2 三组红原文**与门禁 **265 / 426** 见 `work/verification/20260929T135110386Z` 与 `docs/OPTIMIZATION_V30_EVENT_T4.md`。handler 注册表 **22 个**。**注意优先级**：T4 这四个事件在已归档实机日志里**一次都没出现过**；实际出现过且仍未建模的是 `SELF_HELP_BOOK`(8)、`WELCOME_TO_WONGOS`(8)、`STONE_OF_ALL_TIME`(8)。**无实机数据**。

- **2026-09-29 V29 事件 T2 完成（随机结果按预测定价），未上实机**：发现内核注册了 **31 个**事件预测器而当时只有 11 个 handler——"随机结果"一直读得出来。新增 `EventTrade.Grants`（具名 hover tip + 预测模型合并、按 Id 去重、单位卡组价值点），**通用打分器与全部 handler 共用**：两侧都是"授予"的事件（`INFESTED_AUTOMATON`/`COLORFUL_PHILOSOPHERS`/`TINKER_TIME` 等）现在按实际会给的模型比较。`THIS_OR_THAT`/`UNREST_SITE`/`WELLSPRING` 的预测器本来就在，自动用上。另加两个代价侧 handler：`BATTLEWORN_DUMMY`（木桩长度按每 30 HP 记 1 点）、`BRAIN_LEECH`。**R2 两组红原文**（其中木桩那条第一次没变红——handler 按固定数组遍历导致平局恰好选中目标，已改成遍历真实页面顺序）与门禁 **264 / 425** 见 `work/verification/20260929T134116218Z` 与 `docs/OPTIMIZATION_V29_EVENT_T2_PREDICTION.md`。**无实机数据**；仍未做 T4 剩余 / T5 / T6。

- **2026-09-29 V28 事件覆盖率：补表 + T1 + T3，未上实机**：全事件扫描口径下共 **56 事件 / 11 handler / 41 个零信号**（选项 id 不含通用打分器任何关键词 → 并列 → 按页面顺序取第一个）。本轮做三件事：① **BakedEvents 补表**——按本机安装版 `.pck` 的本地化键补 3 个缺失 id（缺 id 会让漂移守卫永久挡住 handler），并把 `NEOW`/`OROBAS`/`PAEL` 具名登记为**池基事件**（其选项是运行期抽出的遗物 id，静态表列不出；同时更正：NEOW 靠遗物模型本来就有分差，不是零信号）。② **T1**——通用打分器对具名卡牌改用 `BuildValue.Add`（×0.25），不再"非 Power 一律 0"，一次覆盖 `BUGSLAYER`/`BYRDONIS_NEST`/`INFESTED_AUTOMATON` 等。③ **T3**——新增统一单位 `EventTrade`（卡组价值点；HP 1:1、金币 ÷3、诅咒 −14×bloat）与 6 个 handler（`SUNKEN_STATUE`/`SUNKEN_TREASURY`/`JUNGLE_MAZE_ADVENTURE`/`THIS_OR_THAT`/`UNREST_SITE`/`WELLSPRING`），数值全部取自事件 `DynamicVars`（用探针跑出真实字段名）。注册表 **17 个 handler**，**R2 三组红原文**与门禁 **262 / 423** 见 `work/verification/20260929T125249858Z` 与 `docs/OPTIMIZATION_V28_EVENT_COVERAGE.md`。**无实机数据**；未做 T2/T4/T5/T6。

- **2026-09-29 V27 光与暗之门事件已建模，未上实机**：实机看到 Bot 在该事件选升级、放弃删牌。查证：该事件**从未被建模**——不在 handler 注册表里，两个选项 id（`options.LIGHT`/`options.DARK`）不含通用打分器的任何关键词，双双得基础分 5.0，**并列后按页面顺序取 index 0**（`BakedEvents` 记的顺序恰好相反，换个排列结果就反过来）。修法：新 `DoorsOfLightAndDarkHandler`——光之门用 RandomForeseer 预测（克隆 RNG 重放生成器）读出**这局实际会被升级的两张**逐张计 `UpgradeDelta`，暗之门取 `Remove` 最优目标，比大小；预测不可得则退回通用打分器。夹具实测 **6.6 vs 75.0**（随机升两张基础牌 vs 自选删一张基础牌），R2 红原文与门禁 **259 / 420** 见 `work/verification/20260928T181630193Z` 与 `docs/OPTIMIZATION_V27_DOORS_EVENT.md`。**无实机数据**；实机要看 `chose=…DARK` 与同行 `predicted=True`。

- **2026-09-29 V26 构筑选牌估值已修，未上实机**：起因是实测"第一幕 Boss 后不领稀有卡"。用社区 Elo 尺子核对：**被跳掉的 BLADE_OF_INK(1738.2)/BURST(1702.1)/FLANKING(1650.6) 评分高于实际拿下的 IMPERVIOUS(1646.9)/BRAND(1648.9)**——不是"金卡弱"，是"Elo 先行"没兑现。三条根因：① 读不懂文本的牌被 cycle 估计当空白牌（BURST 在 `DraftCardEffects` 无分支，内核却有镜面），其 −11.9 覆盖了 +12.9；② 稀释税按**张数**收且与牌无关（19 张 −27、21 张 −40.5），连模型自己标 `route=none` 的 21 张牌组也收满；③ `BloatFactor` 的 ×1.35「再也删不掉」在幕末误触发（`ShopAhead` 只走当前幕）。修法：新增 `BuildCoverage`、只按覆盖率压制**结构项**、**成型度取代张数**（未成型不收税，`N>32` 兜底）、撤销机会按**整局**算、日志 `six-turn:`→`per-turn:` 与托管席补 `reward not taken`。**R2 四组红原文**与四道门（**258 / 419**，6 warnings）见 `work/verification/20260928T175744308Z` 与 `docs/OPTIMIZATION_V26_BUILD_DECISION.md`。**本轮无实机数据，不声称强度提升**；被改写的旧断言「20 张起手牌组里的大路货必须低于 skip」已按批准改为「**成型**后的大路货需要 Skip」。`BossRareFloor` 等政策旋钮未动。

- **2026-09-29 V25 联合搜索预算调度已修，未上实机**：V21 定的前置条件 #1 本轮落地——`KernelJointTurnRun` 的第 0 个续演（最佳前缀 × Tactical）改为只用总预算封顶，其余任务按"打算比较的打法数"（五种策略）而非 `paths×policies` 的四十份分摊。触发证据是 **defeat-v24 实机**：种子 `9F4Q3R2BV8HQ`、A10、第三幕 `DECIMILLIPEDE_ELITE` 团灭（13 场 0 refusal/drift/exception，营火 24/24 与 V24 一致），42 次联合搜索里 **`Tactical:` 只出现 4 次**——最贵也最贴位置的续演被单任务片切掉。新增四席压力夹具，R2 红原文（旧摊分下 `Tactical:0`、`reference=continuation-budget`）记在夹具注释与 `docs/OPTIMIZATION_V25_JOINT_BUDGET.md`。门禁 **256 / 417**，`work/verification/20260928T165831112Z`。**实机已确认调度生效**：同种子 `9F4Q3R2BV8HQ`、high 档跑完全局（正常 `How did the game end??` 收尾，`victory=false act=1 floor=29`，第二幕精英团灭），**47/47 次联合搜索含 `Tactical:`、0 次 `reference=continuation-budget`**（旧版 4/42）。**强度不作结论**——同种子 v24 打到第三幕、v25 死在第二幕，决策分叉使路线全变，非受控对照。证据 `work/v25-joint-budget-20260929/`。下一步按 V21 四场最小复测。V21 的 #3/#4 与 B10/B11 未动。详见 `docs/OPTIMIZATION_V25_JOINT_BUDGET.md`。

- **2026-09-28 V21 实机对照已完成**：8 场沙漏 + 20 层营火 / 39 层商店。原卡组基线 0/2、V20 1/2；第二席补入真实曾提供的准备与投掷匕首后，两版均 2/2，第 11 回合击杀。全部是固定局面的诊断，未证明完整 A10；获胜样本均推进第二 Boss 后按计划停止。营火余像升级与商店 148→14 金购买精准/投掷匕首/迅捷药水由原生存档确认。**下一优先是联合续演预算调度**：候选版沙漏阶段 44 次联合搜索仅 1 次产出完整候选，297 次续演超时，当前多策略简单夹具没有覆盖该复杂度。先修复并跑四场目标复测，暂不启动六局完整 A10。另修复测试工具“关闭主菜单却等待主菜单”的启动死锁，两组使用同一工具。最终门禁 248/409，`work/verification/20260928T131213184Z`；详见 `docs/OPTIMIZATION_V21_TEST_RESULTS.md` 和 `work/strength-20260928-v21/`。本批测试结束，哨兵已移除。

- **已做（2026-09-27 第二轮）：按独立评审 [`outputs/strength-review-20260927.md`](outputs/strength-review-20260927.md) 的 P0-2 动手 + 用户指定的测试配置。走查见 `work/live-a5/2026-09-27-iter2.md`。**
  - **测试配置（用户指定，本轮起生效）**：Bot 难度 **Pro**（本来就是）、花费档 **high**（原 ultra）、
    队伍 **2 静默 + 2 铁甲**。落点是 `a5-batch.py --perf/--party`（缺省即此），进哨兵 `perf=` / `party=`，
    两值都写进 `summary.json`。
  - **① 抽牌能力识别（评审 §4.1）**：`StrongDrawFirstCards` 是手写白名单，51 个含
    `CardPileCmd.Draw` 的类里 **29 个不在其中** —— 真抽牌在**每个构筑入口**都被当成"没有功能"。
    按机械规则（`OnPlay` 无条件立即抽、张数可读）加了 22 个 + 新增 `FixedSingleDrawCards`
    （`DaggerThrow`/`EscapePlan` 固定抽 1）。**明确排除的 6 类各写了具名理由**（`Impatience` 手牌空才抽、
    `Pillage` 循环、`Restlessness` 唯一手牌才抽、`MadScience` 在 rider 里、状态牌、mod 占位）。
    R2 验红 `r2draw.log`；反面 `Restlessness.Draw==0` 也钉住。
  - **② MEND 执行链（评审 §4.3）**：先补上评审没定位到的根因 —— `MendRestSiteOption.OnSelect`
    的本地分支**驱动目标选择 UI**，远端分支走 `WaitForRemoteChoice`（本仓库早就有
    `BotRemoteChoicePatch`→`ChoiceForWait` 的 MEND 分支）。所以坏的只有**被托管的本地席位**，
    那行 `rest-site option MEND refused for 1: Object reference not set` 就是它。改法：
    `MendRunnable` = 本地被托管席位**不提供** MEND（R4 可执行性拒绝，具名日志），
    并把 `PlannedHeal` 预留从"选择时"改成"`ChooseOption` 返回 true 之后"（失败不再留脏预留）。
    R2 验红 `r2mend.log`。**完整目标链（Harmony 前缀自己算目标）留给下一轮**，配方写在走查里。
  - **③ 启用新配置时撞出并修掉的两个 harness 缺陷**（第一次按新配置跑**一局都开不起来**）：
    - **混合职业触发「建局 vs 主菜单加载」竞态**：`scene=NMainMenu state=set` 永不进图；
      同包 + 默认全铁甲则整局跑完 → 是预载时长的问题。`LiveTestAutoStart.EnsureRunScene()`
      在 ticker 上重设 `NRun`（20 秒后放弃并报警）。
    - **房主席位的角色被静默忽略（R5b 家族）**：`StartRunLobbyPlayer` 是 **struct**，
      `SetCharacter` 反射改的是装箱副本、没写回列表（游戏自己的换角色路径是写回的）。
      老代码只判断"找没找到"就返回 true；加上 harness 一直跑全铁甲默认 →
      "设了没生效"和"设了生效"**长得一模一样**。修：按索引写回 + 检查返回值 + 失败 `Log.Error`。
      **修好后实测 `hp=[38/70,35/70,33/80,43/80]` = 2 静默 + 2 铁甲 ✓**。
  - **门禁**：`build.ps1 -Label a5-20260927-review-p0c` exit 0（`work/live-a5/logs/review-p0c.log`）。
  - **实机（Pro+high+2静默2铁甲，种子 `A5BASE201..205`）：0/5，全部死在一层**（层 16/16/12/16/16；
    3 局一层 Boss、1 局 FOGMOG、1 局 12 层）。队伍血池只有 **300**（70/70/80/80），进 Boss 时常常只剩三到四成。
  - **上一轮的修复仍然成立**：敌人集合丢弃本批**每局仅 1 次**（修复前 20–34）。
  - **头号失败模式换人了**：`计划卡已不可打`（5/18/4/12）、`计划席位不能行动`（8/12/2/3）、
    `锦标赛降级交旧规划器`（r03 8 次）、`EYE_WITH_TEETH/REVIVE_MOVE` 边界（r03 **74** 次）。
    **这正是评审 §3.4/P0-1 说的读数缺口**：没有每步的 HP/格挡/牌堆/费用差分，
    `不可打` 分不清是抽牌错、费用变化还是选择不一致 —— **拿到具名原因之前不要改它**。
  - **`a5-batch.py` 现在批次结束就出分析**：`main()` 自己调 `scripts/a5-report.py`，
    tee 到 `<批次>/report.txt` 并打进自己的 stdout，后台任务的完成回执里直接就是逐局表 + 失败模式计数。

- **已做（2026-09-27）：A5 实机闭环 + 第一处按社区四人局数据修正的局外策略。走查见 `work/live-a5/2026-09-27-iter1.md`。**
  - **用户目标**：A5 通关率 80%（连续 5 局至少 4 局）。**当前 0/5 = 0%**，没有接近，不声称接近。
  - **新建的测量能力**（这是本轮真正的产物）：
    - `scripts/a5-batch.py`：跑 N 局 4-BOT A5/ultra，自动归档日志、跑单轮分析、汇总通关率。
    - `scripts/a5-report.py`：批次表 —— 每局胜负/到达层 + **每个房间的 hp 首末读数** + 失败模式计数。
    - mod 侧三个仪器（都是零行为改动）：`run outcome — victory=… act=… floor=…`（`shouldSave:false`
      让这些局不进 profile 的 run history，胜负只能由 mod 自己报，读游戏自己的 `IsVictoryRoom`）；
      `where … hp=[…] deck=[…]`（每 5 秒一行，全run 血量轨迹）；`lobby seed pinned to '<seed>'`。
    - **哨兵的种子此前在联机模式下被静默忽略**（`LiveTestMultiplayerRun.Start` 收了 `seed` 从没用）→
      每局随机地图 → A/B 无法配对。现在钉住、打印，并由 `a5-batch.py --seed-prefix` 决定（两侧必须相同）。
    - 两个已修的**方法**错误：① 按文件大小偏移量切日志是错的（游戏启动会**截断** `godot.log`，
      偏移指向本轮中间，第一次归档把 act-1 整段切没了）→ 改为**按开局标记切**；
      ② 种子取自 `--label` → A/B 两侧跑的是两组不同地图 → 改为 `--seed-prefix`。
  - **基线（`work/live-a5/2026-09-27-base2/`，包 `a5-20260927-baseline2`，种子 `A5BASE201..205`）：0/5**，
    到达层 27/16/12/16/33（3 局死在一层 Boss）。血是这么掉的：**一层 Boss 一场 −166~−202；
    act2 普通/精英每场 −30~−100**；队伍总血池 320。全批失败模式：`enemy set 不符` 114 次、
    `card no longer playable` 59 次、`EYE_WITH_TEETH/REVIVE_MOVE` 边界 55 次。
  - **改了什么**：`MultiplayerPickLift`（16 张牌，**只在席位数 ≥ 2 时生效**）接进
    `BuildValue.AddDetailed`，单位同 `elo-base`，作为独立项 `multiplayer:` 打进日志。
    依据是 `outputs/mp-a10-deck-findings.md` 的逐卡 W/L 选中率（铁甲四人局赢家要高效输出+高单卡格挡，
    输家才是慢速力量成长；`INFLAME` 赢家 0/31、`MOLTEN_FIST` 11% vs 61%）。实测这确实是现行行为：
    `r01` 里 `POMMEL_STRIKE` 被提供 5 次 0 次拿，`INFLAME`/`MOLTEN_FIST` 拿了，`SHRUG_IT_OFF` 拿了 5 次。
  - **R2 已验红**：`MultiplayerPickLift.Term` 强制返回 0 →
    `System.Exception: Community draft regression: a four-seat run must steer away from the slow strength profile: MOLTEN_FIST multiplayer=0.00`
    （`work/live-a5/logs/r2break.log`，构建在门 3 正确拒绝出包）。
  - **门禁**：`build.ps1 -Label a5-20260927-mplift` exit 0（源项目 6 warnings = 基线）。
  - **A/B（同种子）：0/5 → 0/5，层数 27/16/12/16/33 → 28/16/12/16/33。中性。**
    **不许把它读成"有效"**；它改对了选牌方向，但没能把任何一局从"死在 act1/act2"变成通关。
  - **已修（本轮第二处，也是本轮真正重要的那处）：`KernelRollout` 的状态数组「晚一步记录」。**
    - **怎么找到的**：先加仪器 `KernelCombatPlanner.EnemySetDiff` / `SeatSetDiff`，把丢弃原因从
      "敌人集合不符"扩成"哪几个怪 missing / extra"。真机第一轮 13 处差异**全部**是
      `missing=[] extra=[X]` —— `missing` 一次都没非空、`extra` 每次恰好一个敌人，
      **实盘永远比剧本"多"一个**。这个方向性把嫌疑锁死在"比较错位"，不是"伤害模型不准"。
    - **根因**：`KernelRollout` 的 `SnapshotAlive()/SnapshotEnemies()` 在
      `session = resolved.State;`（应用行动）**之后**才调用，而 `RolloutOutcome` 自己的注释与
      消费者 `TryEmitFromTournamentScript` 都按"**行动之前**"用。`tournamentScriptIndex` 指向
      下一个要执行的步骤，此刻实盘是"执行完第 `Index-1` 步"的状态，而 `enemyStates[Index]` 是
      "执行完第 `Index` 步"——**比较永远领先实盘一步**。上一步只要杀掉一个敌人就误判 → 丢剧本重搜。
    - **改法**：快照移到行动之前（卡片路径的 `Add` 提到 `session = resolved.State` 之上；
      EndTurn 路径提到 `session.EndTurn(...)` 之上）。**生产者照它自己的契约走。**
    - **R2 已验红**：把两个 `Add` 挪回去 →
      `System.Exception: the step that wins the fight is the killing blow, so the board recorded for it must still show a living enemy, got 0.`
      （`work/live-a5/logs/r2rollout.log`，内核套件在门 4 拒绝出包）。门禁
      `build.ps1 -Label a5-20260927-statediff` exit 0，`work/live-a5/logs/statediff.log`。
    - **实机读数（本轮最有说服力的一条）**：`the live enemy set no longer matches the script step`
      **114 次（基线 5 局）→ 16 次（修复后同 5 局），−86%，且 5/5 局都下降**
      （每局 28/29/17/13/27 → 3/5/5/0/3）。`r02` 那种"出牌→丢→重搜→同一张牌→再丢"的循环没了。
    - **但它没换来通关**：同种子 A/B 仍是 **0/5**，层数 27/16/12/16/33 → 28/16/16/29/16（均值 20.8 → 21.0）。
      **这是一个真的执行缺陷、真的修掉了、用方向明确的读数证明了；它没能把任何一局变成通关。**
  - **下一轮的头号目标已经换了：战斗强度，不是执行。** 血是在**一层 Boss（一场 −166~−202）**
    与 **act2 普通/精英（每场 −30~−100）** 上掉的，队伍总血池 320。执行侧再修不会改变这一条。

- **已做（2026-09-25）：`SLIPPERY_BRIDGE` 不再白送卡组里最好的那张牌。**
  - **现象**：4 Bot A5 实机（`work/live-20260925/a5-run-death.json`，seed `ZTUZ50VH3J1J`）第 8 层 `SLIPPERY_BRIDGE` 上四个席位**全部**答 `OVERCOME`，各丢一张牌，而且都是各自**唯一的非基础牌**：`TACTICIAN` / `MOLTEN_FIST` / `FLAME_BARRIER` / `BULLET_TIME`（分别在第 5/2/2/6 层刚选的）。进一层 Boss 时卡组 11/12/13/15 张、全队只剩 3 个升级，13 回合团灭于 `ENCOUNTER.VANTOM_BOSS`。
  - **根因**：两个选项 id（`OVERCOME` / `HOLD_ON_n`）都不含通用打分器认得的任何关键词，**同为 5.0**，按索引取小 → 恰好落在索引 0 的 `OVERCOME`。反编译 0.111.0 的 `SlipperyBridge.GetNewRandomCard` 证实首次抽取池就是"非 Basic 牌"，所以"免费"送掉的必然是玩家自己选的牌。
  - **改法**：`EventHandlers` 新增 `SlipperyBridgeHandler`。待丢的牌**就写在 `OVERCOME` 自己的 hover tip 上**（游戏源码 `HoverTipFactory.FromCard(RandomCardToLose)`），因此不需要碰任何私有字段；待丢牌是 Basic / 诅咒 / 状态就接受，否则 `HOLD_ON` 重掷。两条守卫：`WillKillPlayer` 为真、或本次重掷会让血量跌破 `maxHp * 0.35`（与 `AbyssalBathsHandler` 同一门槛）则不重掷 —— `HOLD_ON` 的代价是 3、4、5… 递增的不可格挡/不可强化伤害，而血量有限，这两条守卫同时保证循环终止。**后者是用户指令（"直到 Basic 或诅咒"）之外我自己加的，要否决只删这一个条件。**
  - **R2 已验红**：把 `Choose` 改成 `return null`（即修复前的行为，交回通用打分器）→
    `System.InvalidOperationException: Slippery Bridge regression: A picked card must not be handed over while a 3 HP reroll is on the table.`
  - **门禁**（命令同 `build.ps1:19/24/28/33`，`-p:STS2DataDir=…/data_sts2_windows_x86_64`）：build exit 0（**6 warnings**，与基线一致）/ TypeLoadCheck exit 0 / 普通 **188 PASS** / `-p:EnableKernelTests=true` **327 PASS**。本次改动只贡献 1 条 PASS（`SlipperyBridgeScenarios`）。
  - **R3 扫描（已 grep，未修，待裁定）**：用同一套关键词规则扫 `BakedEvents`，**56 个事件里 44 个的全部选项都落在同一个 5.0 平局上** —— 通用打分器对它们等价于"永远选索引 0"。同局第 14 层的 `WOOD_CARVINGS` 就在其中（把 3 张**已升级**的基础牌变成 `PECK`）。本次只修用户指定的那一个。
  - **未做**：未实机验证（需打包/部署）；`WOOD_CARVINGS` 里"选牌时选中已升级基础牌"是另一条代码路径（`BotCardSelector`），不在本次范围。
- **已做（2026-09-23）：人机混合局改由锦标赛统一决策。** 用户要求不再维护全 Bot / 人机混合两套打牌算法。
  - `TournamentDrives(combat, actors)` 现在只要 **`actors.Count > 0`** 就为真：全 Bot 和混合局都走锦标赛；`TournamentOverride=false` 仍强制分段搜索。
  - 非驱动真人席位在 roll-out 里继续按 `KernelRollout` 的既有语义**结束回合/空过**；真人的正贡献不计入 Bot 的获胜计划，已经发生的真人动作由实盘 capture 精确吸收。
  - `TournamentOptions.IncludeEndTurns` 新增；`BeginTournamentPhase` 在 `humansFinished=false` 且并非全驱动时关掉 EndTurn 候选，`TryTournamentDecision` 与 `TryEmitFromTournamentScript` 都有防御性拒绝，避免 BotRuntime 拒收 EndTurn 后每帧重跑。
  - `AllSeatsDriven` 仍是全 Bot 专属功能（3 分钟开局阶梯、CombatSolver 评分、探针）的闸门，没有被放宽。
  - 测试：`ChoicePolicyScenarios` 改为断言混合局默认也走锦标赛；`KernelRolloutScenarios` 新增 `IncludeEndTurns=false` 删除 EndTurn 候选的断言。R2 已验红：
    `System.Exception: IncludeEndTurns=false must remove the hold rule's end-turn candidate; a mixed table with a human still acting depends on it.`
  - 门禁：`work/verification/20260922T175317124Z/summary.json` —— build exit 0、TypeLoadCheck exit 0、普通 **179 PASS**、内核 **315 PASS**。
  - **未做**：尚未实机验证混合局；也尚未把 Perf/人类贡献估计做混合局专属调参。
- **已做（2026-09-23）：商店消费让当前流派核心牌高于起手移牌。**
  - 现象：非诅咒起手移牌价值 100 deck points = 300 gold，几乎必选，目标流派核心牌（商店里只按通用 Add 计价）永远输。
  - 改法：`Archetypes.CorePremium` 给当前路线添加“缺失核心”溢价（签名牌 150 / 兑现角色 100 deck points，按路线强度和前两个兼容匹配计），`BotShopPlanner.CardGold` 把溢价加到**卡牌商品**上（重复副本折减）；非诅咒起手移除基线 `40 -> 30`，使 150 gold 的移除仍可负担但不再无条件压过核心牌。
  - R2 已验红：把 `SignatureCorePremium` 置 0 → `ShopScenarios` 报
    `a missing signature of the recognised route must get a core premium, got 0.0.`；恢复后四门 186/323。
  - 门禁：`work/verification/20260922T184037511Z/summary.json`。
- **已做（2026-09-21）：锦标赛多核并行。** 用户问"能不能用多个 CPU" —— 能，而且**上游引擎早就把最难的部分做完了**。
  - **现状为什么是单核**：三个原因叠加 —— 锦标赛是串行循环；它跑在 Godot 主线程；且 `NeutralSearchPolicy.cs:45`
    把 `MaxDegreeOfParallelism` 显式写成了 **1**。不是算法只能单线程。
  - **上游证据**：`CombatBeamSolver.ParallelExpansion.cs`（`ExpansionLane` = 真线程 + `BelowNormal` 优先级、
    `ReplayForkSeed`）、`AdmittedExpansion.cs`、`RetentionJobs.cs` 都在用 `DegreeOfParallelism`；
    `SolverWeights.MaximumSearchMaxDegreeOfParallelism = 16`。
  - **唯一的硬约束（引擎原话）**：
    > "A parent simulator cannot be forked concurrently: prediction history seals its mutable tail
    > and several COW containers publish a shared bit during Fork. Lanes serialize seed creation
    > through the parent's gate; each worker then consumes only its private fork."
  - **所以形状是**：**fork 全部在调用者线程串行做**，worker 只碰自己那个私有 fork。
    `Parallel.For` 套在 rollout 循环上会并发 Fork 共享父 → 静默损坏。先例注释就写在
    `KernelTournament` 的类注释里。
  - **另外两条设计点**：
    1. **按启动顺序消费，不按完成顺序** —— `TerminalComparer` 保留**第一个**并列最优，乱序会让平局
       取决于哪个 worker 先跑完。这条已被 R2 验红（见下）。
    2. **worker 用 `BelowNormal` 后台线程**，照抄 `ExpansionLane`：饱和的一批 workers 不该跟渲染线程抢。
  - **并行度**：`Math.Clamp(ProcessorCount - 2, 1, 4)` —— 给游戏留两个核；本机 16 核 → **4**。
    上限 4 是因为再往上**串行的 fork 阶段**会成为瓶颈。
  - **探针（S7，先做后改）**：`ParallelRolloutsMatchSequentialOnes` —— 大预算下 4 路并发与 1 路串行
    必须给出**完全相同**的 `Rollouts` / `Cutoffs` / `StopReason` / 选中动作 / 终局记录。
    **它给出了"没问题"这个答案**（`rollouts=12, action=STRIKE_IRONCLAD@…->0`）。
  - **R2 已验红**：把消费顺序改成逆序，断言立刻红 —— 原文记在该测试注释里。
    注意它的报错信息：两边**卡牌名相同**、变的是**席位**，所以断言比的是
    `玩家 + 卡 + 目标` 三者，不是卡牌 id。
  - **探针救了一次**：打开并行前它先炸出 `rollouts=0 / no-playable-first-action` ——
    我重构时把 `if (rollout is null)` 守卫弄丢了，每轮都新建 rollout 把上一步的丢掉。
    没有这条断言，这个 bug 会直接进实机。
  - **仍未做**：**尚未实机复跑**。实机要看的是 `decision-ms` 明显回落（并行 + 不走分片路径），
    且 `rollouts=` / `stop=` 与之前同量级。注意：**并行路径不再受 `TournamentSliceMs` 限制** ——
    它每帧只发一批、收一批，主线程完全不阻塞，所以顺滑度不受影响。
- **已修（2026-09-21）：锦标赛阻塞主线程 0.3–1.9 秒，把出牌动画冻在半空。**
  - **症状**：Bot 每出一张牌，画面明显卡顿。用户的原话把它定性了：**"我可以接受多思考一会，
    但不想看到打牌的动画明显卡顿（这看起来会像是 BUG）"** —— 要的是**平滑**，不是**快**。
  - **实测证据**（`godot.log:10821-10830`）：`[ActionExecutor] Completed execution of action`
    **紧接着**就是 `CoopBots tournament: decision-ms=1003.1`，然后才提交下一张牌。动作在**队列上**
    完成了，但那张牌的**视觉尾巴**还在播；Godot 的动画是主线程上的 tween/timer，于是被冻住。
  - **根因**：`KernelTournament.Run` 是一次性同步调用，一次决策跑 ~18 个 rollout（每个 ~27-31ms）
    = 500–1200ms 不间断的主线程工作，全都发生在 `NRun._Process`（每帧）里。
  - **修法：分片（不是后台线程）。** 后台线程会踩内核的 LocString/Godot 访问，而项目里**搜索早就是
    分片架构**，这条路风险最低：
    | 新增 | 作用 |
    |---|---|
    | `RolloutRun`（`KernelRollout.cs`） | 一次 `Step()` = 一次行动（出牌 / 结束席位回合），循环体从 `while(true)` 抽出 |
    | `TournamentRun`（`KernelTournament.cs`） | 候选×策略游标 + 在飞的 rollout；`Advance(slice)` 每次最多花一个 slice |
    | `KernelCombatPlanner.TournamentSliceMs = 6` | 每帧最多 6ms 计算（帧预算 16.7ms），`Poll` 在飞时返回 `Status.Pending` |
  - **ⓘ 预算必须是 compute，不能是 wall**：分片后墙钟还包含帧间空闲，用 wall 做预算会在只跑了一部分
    rollout 时就到期，**答案真的会变差**。所以 `TournamentRun` 累加的是**实际计算时间**。
    且 `computeMs` 只在 `Advance` **返回时**折叠，所以预算检查必须**加上本次 Advance 正在跑的秒表** ——
    漏掉这一项会让一次性 `Run`（大 slice）的预算**永不触发**，悄悄改变所有非分片调用方的行为。
    这条由 `ABudgetBindsInsideOneAdvance` 钉住（R2 已验红）。
  - **代价（用户已同意）**：墙钟变长 —— 400ms 计算摊到 6ms/帧约 1.1s 思考。**rollouts 数、候选顺序、
    选中动作、stop 原因全部不变**，由 `SlicingDoesNotChangeTheAnswer` 断言（R2 已验红）。
  - **R2 三条新断言**（都在 `KernelRolloutScenarios`，只在内核套件跑）：
    `SlicingDoesNotChangeTheAnswer`、`ABudgetBindsInsideOneAdvance`，以及
    `ChoicePolicyScenarios.Decide` 改成按帧驱动（原来假设一次 `Poll` 出结果）。
  - **仍未做**：**尚未实机复跑**。实机要看的是 `decision-ms` 变成 1–3s（这是**预期**，不是回归）
    而动画不再冻结；`rollouts=` 与 `stop=` 应与修复前同量级。`TournamentSliceMs` 是唯一的调参旋钮：
    调小更顺、墙钟更长。
- **已修（2026-09-21）：全 Bot 局进 Act2 吃掉 Ancient 事件。** 症状是幕切换后**直接进下一场战斗**，
  既没拿到本幕起始事件（NPC，回 80% 已损失生命）也没拿到剩余的 Boss 奖励。
  - **根因是 `Tick` 里的驱动顺序 + 闸门读错**：`BotRoomProceedDriver.TryProceed` 排在
    `BotRewardsScreenDriver.TryDrive` **前面**（`BotRuntime.cs:131-135`），而它的唯一闸门是
    `button.IsEnabled && IsVisibleInTree()`。结算奖励屏的 continue 按钮**从第一帧就是启用的** ——
    和商店同一个陷阱，但商店有特判，奖励屏没有。于是 Bot 在奖励集合还没 drain 完时就按了它。
  - **实测证据**（`godot.log:12851-12857`）：`pressed the continue (Boss)` → `Skipping remaining
    rewards ... because we're exiting the room` → 奖励集 `GoldReward,PotionReward,CardReward`
    只拿了金币 → `Run location changed to act 1`。全场 `Creating NEventRoom` = **0 次**。
  - **为什么既有那道 2 秒门没堵住**：`_nextActionAt`（`SceneSettleMs`）的检查在
    `BotRuntime.cs:156`，而 non-combat 驱动块在 **`:127`** —— **在它前面**，从来没被挡过；
    `_nextNonCombatAt` 则是**限流器**（"2 秒最多一次"），不是"新屏幕出现后等 2 秒"，屏幕切换时不重置。
  - **修法**：① `BotRewardsScreenDriver.Finished(state)` 与 `TryDrive` **共用同一个**
    `NextRewardButton` 谓词（两者不可能不一致），满腰带时未取的药水算"不可取"而非"未完成"，
    所以不会把房间永久卡住；② `BotRoomProceedDriver` 像商店一样特判奖励屏，并把策略抽成纯谓词
    `MustWaitForDriver`；③ 场景切换时一并重置 `_nextNonCombatAt`。
  - **R2 已验红**：把 `MustWaitForDriver` 写成 `shopPending` 一条，
    `RoomProceedScenarios` 立刻变红 —— 变红原文记在该文件注释里。
  - **延迟随之缩短（2026-09-21，用户指令）**：正确性既然由奖励屏闸门承担，那两个 2 秒就只剩
    "等 UI 挂好"的作用，于是分开成两个语义不同的常量：
    | 常量 | 旧 | 新 | 语义 |
    |---|---|---|---|
    | `SceneSettleMs` | 2000 | **1000** | **进入新场景**后等多久；`_nextActionAt` 与 `_nextNonCombatAt` **两个 gate 都用它** |
    | `NonCombatSettleMs` | 2000 | **250** | **同一房间内非战斗动作的轮询间隔**（奖励/事件/商店/宝箱循环） |
    | `ActionSettleMs` | 2000 | **2000（未动）** | **战斗内每出一张牌**的地板；用户明确确认不改 |
    - `NonCombatSettleMs` 是**轮询间隔而非正确性闸门**：这个块里的每个驱动都以游戏**自己的实时 UI 状态**
      为闸门（按钮必须 enabled 且可见、商店必须 settle、奖励屏必须无可取），所以扫得更勤只是**更早发现**，
      不可能比屏幕允许的更早行动。
    - 场景切换处改用 `SceneSettleMs` 而不是 `NonCombatSettleMs`：**进房间要等 UI，房间内重复动作不用**。
  - **仍未做**：这次是**代码级修复 + 门禁验证**，**尚未实机复跑**。确认点是日志出现
    `CoopBots room: holding the continue — the reward screen still has rewards`
    且幕切换后跟一个事件房（`Creating NEventRoom`）。另外 `NonCombatSettleMs = 250` 是本次最大胆的值：
    若游戏的奖励按钮存在"已 enabled 但尚未能吃下点击"的窗口，`BotRewardsScreenDriver` 的
    `Attempted` 集合会把该奖励永久抑制 —— 这是既有风险，但轮询变快后更容易撞上，实机日志要盯
    `CoopBots rewards: took …` 与房间推进是否对得上。
- **最近一次门禁（2026-09-21 17:34 实跑）**：普通 **172 PASS** / 内核 **294 PASS**（完整套件），
  四道门 exit 0，`work/verification/20260921T173431413Z/summary.json`。
  - 门槛随 0.38.0 新增场景重新基线过（0.36.4 时是 166 / 265；完整套件的聚焦子集
    `--kernel-power-only` 在 0.36.4 时读数是 94）。口径见 §3.1。
  - 构建警告基线同步为 **6**（0.38.0；日志里每个警告会打两遍，去重后才是真数）。
- **限步前瞻已实现（用户规格 2026-09-20）**：`LookaheadRounds = 5`，预算 **3 分钟 + 5×1 分钟 = 8 分钟上限**，
  节点 200k → 120k。一局 = **一串连续的剧本**：算 5 回合 → 严格打完这段 → 从实盘再算下一段。
  - **必须先理解它为什么第一次失败**（只改 `MaxRounds` 99→3，已回滚）。三件事**必须同时动**，否则更坏：
    1. **地平线必须值钱**：`KernelTeamSearch.Score` 给到达地平线的节点加 `HorizonBonus = 2_000_000`。
       取值依据是评估器自己的层级——胜利 `+10_000_000`、团灭 `-100_000_000`、战术项钳在 `±1_000_000`：
       所以它**高于钳位**（任何局面差异都盖不过它）而**远低于胜利**（真能打完的线仍然赢）。
       没有它，深线要为吃掉的敌方回合付出代价而**没有任何东西为到达地平线付钱** → 实测 `actions=2` 的短桩胜出。
    2. **`HasRoute` 必须认地平线**：它同时被**部署、开局阶梯、阶梯的上限分支**读着。
       保持"只有找到结束线才算成功"→ 阶梯每场都触发 → 上限分支**把内核整场禁用**（实测每个动作都变成 `via=legacy`）。
    3. **走完一段要重搜**：`CompleteContinuation` 清 `planCommitted`，让下一 tick 算下一段，
       而不是把剩下的战斗交给旧规划器。
  - 另外把阶梯的上限分支从"禁用内核"改成"**部署这条线并记录**"：有界搜索**按构造**打不完战斗，
    所以"没找到结束线"不再能作为放弃内核的理由；短剧本是**更差的剧本**，不是停止思考的理由。
  - **验证边界（不夸大）**：新断言 `(4c)` 覆盖"剧本必须跨回合 + `HasRoute=true`"，
    其中 `HasRoute` 那一半**已用变异验证会变红**；`HorizonBonus` 那一半**在该合成棋局上无法区分**
    （5000 HP 的敌人让跨回合在战术上反而更吸引人，去掉 bonus 仍产出 54 动作的线）。
    所以 bonus 的必要性**以实机证据为准**（`MaxRounds=3` 时 `best partial: actions=2`），
    断言只守住"结果"（脚本跨回合且报告成功）。这一点写在测试注释里，不假装它更强。
- **限步前瞻实机结果（live12，22 场到 act3/floor48）**：
  - **22/22 场 `refus=0 / drift=0 / pending=0 / missing=0 / noScript=0 / exc=0`** —— 执行率 100%，
    且**包含此前拒绝过的那两类**（`LOUSE_PROGENITOR` 的 round-drift、`HUNTER_KILLER` 的选牌药水）。
  - `plans` 每场 2–9 → **段链在跑**（一局多个剧本）。`routes` 每场 ≥1。
  - 单次搜索 `alloc` 最大 **17,632 → 3,882 MB**，中位 1,526 MB；`search slice overran` 0 次。
  - `KNOWLEDGE_DEMON_BOSS` 那场：`openingAttempts=5, openingNoRoute=1` —— 阶梯**撞到上限后按新逻辑部署**
    而不是禁用内核（旧逻辑就是在这里把内核整场关掉的），战斗继续打下去（party 12/80）。
- **发现并修掉一个"实现没跟上规格"**：预算是按 `openingSearch` 开闸的，所以**只有每场第一个剧本是真搜索**，
  第 2 段之后掉回交互预算（768 节点/450ms，实测 `alloc=21–55MB`）——那不是"算 5 回合"，是猜。
  闸门已改为"每个席位都被接管"，即每个段都拿到 3 分钟基础搜索；
  **续跑阶梯仍是整场共享**的 8 分钟上限（每段 3 分钟基础 + 整场共享的续跑额度），
  否则一场长战斗会变成"每段都能想 3 分钟、永无上限"。
  配套把 `AuditBoundedSearchPolicy` 拆成两个问题（相位不变性：两种桌都断言；"预算小不小"：只对有真人的桌断言）。
- **多人 4 BOT A10 首轮（live15）**：6 场杂兵 → act-1 BOSS 团灭；`via=kernel` 317 / `via=legacy` 1。
  - 计算量比单席涨一个量级：`wall` p50 **94.5s**（单席 10.3s）、`compute` 47.5s、`nodes` 38.6k/120k、
    `alloc` **22.4GB**、`gcPause` 8.9s。**wall p90 已到 180s 预算的 66%** → 4 席下延长地平线的空间
    比单席（当时只用 3–4%）小一个量级。长脚本 47–69 动作，说明 5 回合地平线在 4 席下确实走满。
  - 两次拒绝，都是**剧本对不上**，已修（见下）；死亡本身是**强度**问题（进 boss 时 34/33/27/23）。
- **已修①：一个席位退场不再拖垮整个剧本。** 剧本是按"搜索时活着的席位"定价的；4 席下一个席位中途阵亡，
  它剩下的步骤全部不可执行（死席位没有手牌 → `card-left-hand`），而旧逻辑**拒绝整个剧本**，
  连幸存席位的可执行步骤一起丢掉（实测 boss 战：3 席被 STOMP 打死后，幸存者的剧本被
  `index=19/32 card=EXPECT_A_FIGHT card-left-hand seat_eligible=False` 拒掉）。
  现在**跳过**这些步骤并计数（`seatSkips=`），R4：只有可执行性可以拒绝，而幸存席位的步骤是可执行的。
  - 配套：L1 哨兵改为按 **`LastExecuted`**（最后真正执行的步骤）比对，而不是 `Index-1`；
    跳过会让后续预测失去可比性，所以**一次性**置为失效（`SentinelInvalidated`），
    而不是留下每个剩余步骤一条漂移（那正是把 7 个字段报成 72 条的成因）。
  - **R2 已验红**：`skips=1, refusals=0`；去掉跳过循环 → `a script step belonging to a seat that can no
    longer act was not skipped`。另有一条**既有**断言抓到了我引入的回归（`CommitContinuation` 必须让
    `LastExecuted=0`，否则第一步的哨兵被整个关掉）——已修。
- **已修②：回合跨越检查不再只对卡牌步生效。** `ValidatedRoundDelta` **只**在卡牌分支里写入，
  所以边界索引一旦落在 EndTurn/药水步上，这次跨越就永远不会被标记为已消费。
  **4 席每轮有 4 个 EndTurn**，边界索引经常落在 EndTurn 上 → 实测
  `index=19/36 reason=round-drift round=2/plan_round=1 validated_delta=0 boundary_here=no
  boundaries=[15,16,17,18,29,31,33,34]`：计划标了 15–18 四个跨越点，运行时一个个走过却**一个都没记录**。
  单席每轮只有 1 个 EndTurn，所以 34 场单席从没暴露。
  现在该检查**提升到每步都跑**（仍在 L1 哨兵之后，否则会与"上一步还没落地"的等待打架）。
- - **live16（两处修复后）**：**11 场、act1 → act2、floor 20**（live15 是 floor 16 团灭）。
  - `round-drift` 拒绝 **1 → 0** ✅（边界提升生效）；`seatSkips=5` ✅（席位跳过生效）；
    **同一个 act-1 boss 从"团灭"变成"38 个剧本、0 拒绝、打赢"**。
  - 成本已**打满预算**：`wall` p90 **174s**（基础预算 180s）、`nodes` max **110k/120k**、
    `alloc` max **29GB**、`gcPause` p90 31.6s → 4 席 A10 下**延长地平线不可行**，除非先降成本。
  - 剩 2 次拒绝，都是 `card-left-hand` 且 `seat_eligible=True`：
    ① `INFLAME`（第 17/42 步）—— 根因是**剧本中途独立药水规划器灌了 `POWER_POTION`**
    （日志原文 `CoopBots potion: POWER_POTION; card-generation-tempo`，紧接着 `field=R.card_generation` 漂移；
    `INFLAME` 正是 Power 牌 = Power Potion 会加进手牌的东西）。**已修**：见下。
    ② `POMMEL_STRIKE`（第 36/52 步）—— 计划与实盘**对这张牌在哪个牌堆上意见不同**（`D[2]`/`H[2]` 期望它），
    现场没有 legacy 动作；该场用了 5 瓶药水其中含 **`DROPLET_OF_PRECOGNITION`**（会移动/重排卡牌），
    所以嫌疑是**"计划记录的选择没有用来回答实盘的选择"**那一族（与选牌药水同根）。**未修，已收窄。**
- **已修③：剧本在飞时，旧规划器一律沉默。** `legacyPlan` 原来是 `kernelStatus != Ready || joint is null`，
    而 `kernelStatus != Ready` 在**脚本等待上一步落地时**也为真（那时脚本返回 Pending）——
    于是**三个决策源**都能在剧本中途插话：`BotPotionPlanner` 灌药、`BotBrain` 提卡、
    `TeamCombatPlanner` 产出的 `joint` 还能被 `TeamCoordinator.Select` 选中。
    任意一个都会改变剧本后续步骤所依据的手牌。现在改为 `!KernelPlanner.HasPendingStep && …`。
    拒绝时 `DropContinuation` 会让 `HasPendingStep` 转假，所以"拒绝后交旧规划器"这条登记行为不变。
- **已修④：`BLAZE` 建模**（与 `CONCOCT` 同形：Skill / AnyAlly / 单个 `PowerVar<StrengthPower>`）。
  实测它在一场 boss 战里贡献 **1272 次边界**，是全场最频繁的边界 —— 也就是机器人最想打却打不出的牌。
  覆盖率：**unmodeled 10 → 9、broken 2 → 1**，`COVERAGE UNMODELED` 里已无 `BLAZE`。
  该卡片原来有一条断言**要求它被拒**（"An unmirrored ally buff must be SKIPPED now"），已按它自己的注释
  翻成正面断言（可打出 + 给**目标队友**+5 Strength）；**R2 已验红**（去掉注册表条目 →
  `BLAZE must be playable now, not a boundary: prediction-risk:…MethodNotMirrored`）。
- **剧本拒绝的完整核对（备份日志 `work/agent-state/data/mp-a10-2026-09-20/`，脚本 `refusal-check.py`）**：

  | | live15 | live16 | live17 |
  |---|---|---|---|
  | 场数 / 严格场（`refusals=0` 且 `noScript=0`） | 6 / **5** | 11 / **9** | 6 / **5** |
  | `plan step refused` | 2 | 2 | **1** |
  | `plan dropped` | 2 | 2 | 1 |
  | `plan step skipped`（席位退场，**非拒绝**） | 0 | 5 | 0 |
  | `plan step/boundary drift`（**只记录不拒绝**） | 90 | 210 | **0** |
  | 丢弃是否都由具名拒绝引起 | ✅ | ✅ | ✅ |

  **没有静默杀剧本**：每一次 `plan dropped` 都能对上一条具名 `plan step refused`。
  剩下的拒绝**只有一个族**：`card-left-hand` 且 `seat_eligible=True`（席位活着、在出牌阶段，牌不在手里）。
- **那个族的根因已坐实**（同一场内取证，不是推断）：
  live16 的 `POMMEL_STRIKE` 拒绝在 **`SLIMES_NORMAL` 第 36/52 步**；同一场第 16/52 步用了
  **`DROPLET_OF_PRECOGNITION`**，而它的效果是
  `CardSelectCmd.FromCombatPile(Draw, …, 1)` → **从抽牌堆里选 1 张放进手牌**（一次玩家选择）。
  漂移字段正是 `D[2]`/`H[2]` **期望 `POMMEL_STRIKE`** —— 即这瓶药水本该搬进手的那张牌。
  所以：**实盘那次"选哪张"不是计划记录的那张**，计划的手牌模型就此分叉，20 步后要打它时它不在手里。
  这与选牌药水是**同一条四段通路**：① 捕获选择 → ② 选择的应用 → ③ 部署侧携带 → ④ **运行期用它回答实盘选择**（④ 是主要工作量）。
  **未验证的张力**：覆盖率扫描把 `DropletOfPrecognition` 列为**不可建模的药水**（`POTION COVERAGE` 的 missing 列表里有它），
  但日志里它是**剧本站的一步**（`kernel-plan:reuse,potion:DROPLET_OF_PRECOGNITION,step:16/52`）。两者至少有一个是错的，需查。
- **修正记错**：`POWER_POTION`/`INFLAME` 那次拒绝发生在 **`BYGONE_EFFIGY_ELITE`**，我先前写成 `THIEVING_HOPPER_WEAK`（
  `BotRuntime` 的注释已改）。这是靠**备份日志**核对出来的 —— 单看当时滚动中的 `godot.log` 很容易串场。
- **已修⑤：九瓶"需要选择"的药水一律判 boundary（此前只挡了四瓶）。**
  - 引擎自己的谓词就是 `PotionChoiceSupport.RequiresChoice`：**四瓶抽卡药水 + 五瓶取牌药水**
    （`Ashwater / DropletOfPrecognition / GamblersBrew / LiquidMemories / TouchOfInsanity`）。
    我上次只用了更窄的 `GeneratesCardChoice`（四瓶），于是 `DROPLET_OF_PRECOGNITION` 照样进了剧本。
  - **为什么它们在剧本里不可重放**：搜索建药水动作时是 `new Action(actor, null, target, potion)` —— **`Choices` 恒为 null**；
    `KernelSession.UsePotion` 又恒传 `choice: null`。所以计划**无法说明该拿哪张牌**，
    实盘那个选择画面由通用 `BotCardSelector`（`BotBrain` 评分）回答，选得不一样时剧本的手牌模型就分叉。
  - 实证（同一场取证）：`SLIMES_NORMAL` 第 16/52 步用 `DROPLET_OF_PRECOGNITION`
    （效果 = `CardSelectCmd.FromCombatPile(Draw,1)` 从抽牌堆选 1 张进手），
    紧接着漂移读到 `D[2]`/`H[2] expected={POMMEL_STRIKE}`，第 36/52 步就 `card-left-hand` 拒在 `POMMEL_STRIKE` 上。
  - 这也是我先前记下的那个"矛盾"的答案：覆盖率扫描把这几瓶列为**不可建模**是**对的**（它们确实不可重放），
    错的是**那道闸比报告更宽**。现在两者用**同一个谓词**，矛盾消失。
  - **R2 已验红**：行为断言从 `SkillPotion` 改用 `DropletOfPrecognition`（用前者的话，退回窄谓词也能通过，抓不到这个 bug）；
    退回 `GeneratesCardChoice` → `the search must refuse a chooser potion by name, got refused=False boundary=''`。
  - **仍未做**：真正的修法是把选择打通（搜索侧捕获 → `UsePotion` 应用 → 部署侧携带 →
    `BotChoicePlanSync` 认 `UsePotionAction` 并回答实盘选择）。**这条通路现在缺的是搜索侧捕获**，
    因为 `BotChoicePlanSync.Select` 的守卫只认 `PlayCardAction`，而药水的选择发生在 `UsePotionAction` 期间。
- **"机器人拒绝击杀、战斗无限拖" 的根因（已端到端验证，2026-09-20）**：
  **不是评分偏好，而是搜索树被截断。** 两次误读先纠正（R1）：
  - 误读一：我说"卡牌击杀不算终局" —— **错**，`KernelTeamSearch` 第 236-268 行就是卡牌分支，它**有** `bestTerminal` 记录。
  - 误读二：我以为评分让机器人不敢赢 —— **探针否证**：把"胜利"的评分设成 **−1,000,000（灾难级）**，
    搜索**仍然选那条 12 步的胜利线**（`CompletedState.HasWon=True`）。
  - 而且"非终局线不得压过眼前的致命线"**本来就已被守护**：把 `Complete` 的终局优先关掉，既有断言
    `P1-1: A complete nonlethal round must not displace an immediate lethal` 立刻变红（我实测过）。
- **真因链（`WATERFALL_GIANT_BOSS`，4 BOT A10）**：
  1. `WaterfallGiant` 是**两阶段 boss**：打到 0 血不会赢。`SteamEruptionPower.AfterDeath` →
     `TriggerAboutToBlowState()` → **回满到 `999,999,999` 血 + 强制切 `ABOUT_TO_BLOW_MOVE`**
     （外加 `ShouldStopCombatFromEnding()=>true`、死后不移除、power 不消失）。
  2. 内核**建模了**这一转换（`SimulatedCombatState:1354 TryTriggerSteamEruptionDeath` 逐行镜像游戏）。
  3. **但转换之后那句 fail-closed 把整条线截断**（`CorePowerSupport.cs:661`）：
     ```csharp
     bool steamEruptionTriggered = combat.TryTriggerSteamEruptionDeath(simulator, enemy);
     if (simulator.HasPendingChoice) return false;   // ← 死亡结算失败 → 该分支变成 boundary
     ```
     实测这一场 `boundaries=end-turn:round-pending-choice×749`。
  4. 于是**任何"打死这个 boss"的线在搜索里都走不到底**，搜索能看到的最好线只剩
     "活过接下来 5 回合" → 它不打、猛防守（51 张 DEFEND vs 36 张 STRIKE）、拖 25 回合 64 个剧本 → **每段重搜都得出同一结论 → 战斗无限拖**。
  - **这与用户的观察完全对应**："击杀了敌方之后自己会死" 正是 `SteamEruptionPower` 的效果。
- **一族（R3）**：`KNOWLEDGE_DEMON_BOSS` 在更早一轮打出 `boundaries=end-turn:round-pending-choice×213`，
  同一个 `HasPendingChoice` fail-closed 路径。`HasPendingChoice = PendingTurnStartChoice != null || PendingKnowledgeDemonChoice != null`
  —— 两个来源都要覆盖。
- **已加仪器（未修）**：`SimulatedCombatState.PendingTurnStartChoiceDescription`（复用已有的 `Describe`，
  给出 `SourceId/Effect/SourcePile/Count/ContextId`）+ `CorePowerSupport` 里那条 fail-closed 前打一行，
  指名是哪一个 pending choice。**下一次实机就能知道要解析什么**，然后才动那一行 ——
  因为改的是"回合边界的结算"，本 session 已在同类地方被罚过两次。
- **实机管线已通**：`scripts/build.ps1 -Label …` → `scripts/run-live-loop.ps1` 一键
  杀进程→部署→经 Steam 启动→看护。开局由 `LiveTestAutoStart` 自己建局并直接进
  `CULTISTS_NORMAL`。**判据：日志出现 `combat summary`**。
  - 已量：一轮连打 6 场，全部 `plans=n(100%) / planRefusals=0 / noScript=0 / exception=0`。
  - 看护判据是**局面指纹**（floor/room + 最后一条进度行），不是日志 mtime ——
    mod 自己每 5s 一行 `where` 心跳会把 mtime 一路刷新，mtime 规则永远不触发。
- **最近一次实机**：
  - live4 轮：**19 场跨 act1→act3（含 2 个 boss，floor 34）**；18/19 严格按剧本。
    唯一不严格的 FOGMOG 是**已登记的边界行为**（搜索按设计停在未建模的敌方 `REVIVE_MOVE` 上，
    实机手牌随之漂移 → `CINDER` 不在手里 → 可执行性拒绝 → 交旧规划器打完），不是 occurrence bug。
  - live5 轮：**25 场到 act4 / floor 54**，`routes=` 已可单场判读。
  - **`planRefusals` 之前不是一个可信的验收量**：见 R5b。live5 首次出现 `plan-exhausted`，
    才暴露出"剧本走完最后一步"被当成拒绝计数，而 `noScript` 那条分支不可达。
    已修：走完 = `plan complete`（不计拒绝、不计弃用、放行到 STRICT EXECUTION 让 `noScript` 开始计数）。
- **头号问题已经是分配/GC，不是管线**（以下都是实机量到的数字，不是推测）：
  - `search-complete … compute=31122ms, gcPause=4657ms (15.0%), gen0=824, alloc=13074MB`
    —— **单次搜索分配 13 GB**；另一场 `gcPause` 占到 `compute` 的 **56.6%**。
  - live6 第 5 轮在 floor 6 **只打了 4 场**就卡死：日志停在一条 `team: plays` 之后
    **4 分钟无任何输出**，连 45s 一次的 `search heartbeat` 都没出来 —— 主线程被一次
    超长的 GC/切片卡住时，心跳自己也发不出来。看护器正确判死并杀掉。
  - 所以"静默"和"没在搜"是两件事：**先压分配量**，否则局越长、搜得越深，卡死越频繁。
- **原版 bug（不是本 MOD 的，已核对）**：`RunManager.cs:954` 用
  `new TreasureRoom(State.CurrentActIndex)` 建宝箱房且**不做范围检查**，而
  `TreasureRoom.cs:25` 只接受 act 0..2；`Glory` 是第 4 个 act（索引 3）。
  → **Glory 地图上的 Treasure 节点，点进去必抛 `ArgumentOutOfRangeException`**，
  真人和 Bot 走的是同一条 `NMapScreen.TravelToMapCoord` 链，**手点一样崩**。
  撞上后 `room=null roomStack=0`，本轮卡死（看护器已正确杀掉）。
  **是否要在寻路时回避"当前 act 造不出来的房间"是产品决策，待用户拍板。**
- **已修（R4：未建模 = boundary）**：**选牌类药水不再进剧本。**
  - 真相：`CardGenerationPotionMirrors` 对 Attack/Skill/Power/Colorless 四类返回
    `AddsToHand: false`，自己的注释写着生成卡"without … applying combat piles and hooks" ——
    **引擎明说只建模了"抽 3 张"，没建模"选中的那张进手牌"**。搜索于是把药水之后整场都按
    "这瓶没加牌"规划，实盘却加了一张 → 一次错位 → `card-left-hand` → 丢剧本
    （live9 `SLIMES_WEAK` 的 `d=10`、`expected_count=3 actual_count=4`、第 4 步从未重放、第 12 步拒绝）。
  - 修法：`CanModelPotion` 改成"镜像存在**且**不是选牌类"（R5b：镜像 ≠ 建模），
    `UsePotion` 对选牌类**软拒绝**（`boundary = "potion-choice-unmodeled"`，不置 `failedBoundary`，
    免得污染同一个 fork 上的其它展开）。这样搜索不会跨过它，而独立药水规划器仍可在剧本外使用它们。
  - 三条断言 + R2 全部改坏验证过（报告、拒绝、边界名）。
  - **仍未做**：把选择真正打通（捕获 → `UsePotion` 应用 → `PotionPlan` 携带 →
    运行期用它回答实盘三选一）。第 ④ 步目前没有通路，是主要工作量。在那之前这四类药水不进剧本。
- **已修**：**`ALL_FOR_ONE` 的 `case` 标签丢了**（见 R5c）。修好后：
  编译器 `CS0162` 消失、每次运行都打的 `COVERAGE BROKEN: ALL_FOR_ONE` 消失、
  新断言（0 费牌回到手牌且不涨能量）通过且 R2 改坏变红。
  与选牌药水同族：**一个"悄悄不对"的模型比一个"不存在"的模型更坏**，因为只有后者会变成 boundary。
- **已命名（诊断；不是行为改动）**：`planRefusals=1(drift=0,pending=0,missing=0)` 那种"计数器都说没事"的拒绝。
  - 现场（live9 第 8 轮，LOUSE_PROGENITOR_NORMAL）：
    `plan step refused: index=4/20 card=WHIRLWIND playable seat_eligible=True end_turn=False
    round=2/plan_round=1 boundary=none sentinel_states=20 risk=True`
  - **`playable` 不是原因，是描述**：`ReportPlanStepRefusal` 的 `cardState` 只说明"牌在手里且能指定目标"。
    真正的守卫是 **round-drift**（`AllowsRoundDrift(2, 1, boundaryHere:false, validatedDelta:0)` → `1 <= 0` 为假），
    而它不属于那三个 sentinel 计数器，所以从来没被命名过 —— 这正是该函数自己声明的存在理由。
  - 已加分支：这种拒绝现在打 `reason=round-drift round=…/plan_round=… validated_delta=… boundary_here=…`。
  - **验证边界（不夸大）**：该分支的**分类正确性由构造保证**（`AllowsRoundDrift` 本身已有断言覆盖），
    门禁 166/92 保持；但"实盘确实会打这一行"**尚未被实测验证** —— 本套件没有日志捕获机制。
    下一次实机跑到这种拒绝时看那一行即可确认。
- **已加仪器（未修，等实机分辨）**：**act3→act4 之后不再投票**，整局卡在打开的地图上。
  - 现场（live9 第 8 轮收尾）：`room=Map roomStack=1 floor=48 mapOpen=True mapTravel=True`，
    守卫全过却 4 分钟不投票；**`generation 4` 在整份日志里出现 0 次**，最后一次投票是 `generation 3`。
  - 首嫌疑：`TryVoteOnMap` 的 `if (GetVote(player).HasValue) continue;` **只看"有没有票"、不比对 generation**，
    而同一循环下方三行的兄弟检查是比对的。act 切换若不清票，gen 3 的旧票会让席位永远被跳过。
  - 次嫌疑：`RoutePlanner.Plan` 返回 null。**我加的 act4 宝箱排除会造成这个**，
    但我那行 `refusing to travel to …` 只枚举**起点节点的直接子节点**，所以"没打"**不能**给它脱罪。
  - 已加节流仪器：每个等待分支打 `CoopBots map: waiting — <stale-own-vote(gen=N) | no-route | …>; generation=N`。
    **行为零改动**，只用一轮实机即可分辨，然后按结论修（两者修法都很小）。
- **已修**：**`routes=N/M` 漏报**。`plansWithRoute++` 只在三条部署路径的一条里（牌开局那条），
  所以「以结束回合开局」或「以药水开局」的剧本**找到了战场解却不被计数**。
  实测（live9 第 8 轮）SOUL_FYSH_BOSS：开局搜索打印 `action=potion, route=True, actions=30`，
  摘要却写 `routes=0/3` —— 这让我把求解率误报成 14/19，**真实是 19/19**。
  修法：把两个计数器收进一个 `CountDeployedPlan()`（三条部署路径各调一次；
  **结束回合的复用路径故意不调**，那是重报旧剧本、没有自己的 route 可记）。
- **已加仪器**：**搜索切片超时**必须出声。`sliceMs` 是**软预算**（在两次扩展之间检查），
  所以单次扩展可以无限超时 —— 那时主线程回不到 `Poll`，**45 秒的心跳也发不出来**，
  留下的只有"莫名沉默"，看护器会杀掉一个其实在正常跑的搜索。
  实测 live10 的 act-1 boss：搜索跑 ~366 秒只发过 **2 次**心跳（`rounds=14` 两次相同），
  之后 ~276 秒全静默，内核整场没出解 → 整场 boss 交给旧规划器 → **败**。
  现在超过 `20 × sliceMs` 会打 `search slice overran: took=… budget=… rounds=… nodes=…`。
- **未完成项已迁到 [`PLAN.md`](PLAN.md) §3 的编号 backlog（B1–B11）**，每条带验收判据。
  本节只保留**叙述性背景**（为什么会这样、当时怎么查出来的），
  **不要把这里的散文当成待办清单** —— 状态用 `python scripts/state.py show` 拿。
