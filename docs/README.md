# CoopBots 文档导航

本目录是仓库内开发文档的入口。历史计划、实验记录和旧说明仍留在 `outputs/`，这些文件被 Git 忽略；
其中的结论需与对应数据和代码版本一起核对，不能因文件较新就视为当前事实。

## 先读这两个

- **[`../AGENT.md`](../AGENT.md)** —— **唯一权威规范**。铁律 R0–R8、四道验证门、Skills 注册表（S1–S9）、
  偏离登记表、当前状态活文档都在这里。与任何其它文件冲突时以它为准。
  **从 §0「首次接手」开始读 —— 那一节读完就能开工，不要顺序通读全文。**
- **[`../PLAN.md`](../PLAN.md)** —— **全局规划**。当前阶段、编号 backlog（B1–B11）、每项的验收判据。

状态用命令拿，不要从文档里抄：

```bash
python scripts/state.py show
```

**通过数**：判据看 `../AGENT.md` §3 的表，证据只看 `work/verification/` 最近一次运行。
**任何文档里写死的 PASS 数都是历史读数**（本仓库已经漂过至少四轮）。

## 项目资产

- [`PROJECT_MAP.md`](PROJECT_MAP.md)：**项目资产地图**。编译产物、生产内核与第三方参考副本、
  测试入口、生成表、被忽略数据、发布产物的精确路径与区分。路径有疑问先查这里。
- [`../README.md`](../README.md)：MOD 简介、构建方式、许可与版本声明。
- [`../CHANGELOG.md`](../CHANGELOG.md)：完整版本变更记录。
- [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)：第三方来源、署名与许可边界，分发时必须保留。
- [`../src/CoopBots.Kernel/INTEGRATION.md`](../src/CoopBots.Kernel/INTEGRATION.md)：生产内核（Vendor）接入范围与适配记录。
- [`../src/CoopBots.Kernel/VENDOR_DEVIATIONS.md`](../src/CoopBots.Kernel/VENDOR_DEVIATIONS.md)：与上游的逐条偏离。
- [`RELEASING.md`](RELEASING.md)：发版与工坊上传流程。
- [`COMBAT_SOLVER_POWER_SYNC.md`](COMBAT_SOLVER_POWER_SYNC.md)：0.41.0 力量策略部分回迁的记录。

## 当前主线（战斗内核与实机执行）

- [`../AGENT.md`](../AGENT.md) §5 **S5**（无人值守实机管线）、**S7**（内核缺陷定位）—— 这是当前的主要工作方式。
- [`COMBAT_SEARCH_LATENCY.md`](COMBAT_SEARCH_LATENCY.md)：搜索延迟与分配的实测读数。
- [`../tests/README.md`](../tests/README.md)：可复现的测试命令（PatchSmoke、`EnableKernelTests`、TypeLoadCheck）与自动化检查的边界。
- [`../scripts/verify-project.ps1`](../scripts/verify-project.ps1)：**四道门的一键验证入口**。产物落在 `work/verification/<时间戳>/`，含 `summary.json`。
- [`../scripts/build.ps1`](../scripts/build.ps1)：跑同样的四道门并出包（**必须带 `-Label`**）。

## 已废弃的线（只作历史，不要据此行动）

- [`building/`](building/)：离线卡组评价 / 选牌模型的实验记录。**该线 2026-09-19 判死**，
  对应的 `work/` 数据已于 2026-09-20 删除（见 [`building/README.md`](building/README.md)）。
  社区数据（`work/spire-codex/`）仍然在用，抓取与烘焙脚本没有废弃。
- [`PREPARATION_CHECK.md`](PREPARATION_CHECK.md)：2026-09-17 那次整理的结果。
- 根目录的 `TAKEOVER_REVIEW.md`、`TEAM.md`、`AGENTS.md`、`SOUL.md`、`CLAUDE.md`、`DECK_DELTA_MODEL_PLAN.md`、
  `SOURCE_PACKAGE.md`：均为**历史文件**，`AGENT.md` 明确声明不要依据它们行动。

## 当前工作区状态（务必先读）

本工作树是一个**脏检出**，包含大量未提交修改和未跟踪文件。

- **所有当前未提交改动都必须保留**：不要 `reset`、`checkout`、`clean`、`stash` 或自动提交、合并、发布。
- **参考副本都在 `third_party/` 下**。它们被脚本以硬编码相对路径引用，**移动前必须先 grep 全仓并改完引用**
  （`PROJECT_MAP.md` §0 第 2 条）。
- **不要读取或打印密钥与凭据**：根目录 `DEEPSEEK_API_KEY`、环境变量全集一律排除。

## 文档维护约定

- 新增或移动文档时同步更新本索引和 `PROJECT_MAP.md`；移动被脚本引用的路径时同步更新引用。
- 以工作区中实际存在的文件和可重跑结果为准；历史结论不要复制，改为链接到原始文档。
- 发现陈旧描述时，优先用证据纠正并在 `PROJECT_MAP.md` 第 9 节记录，而不是静默改写历史。
