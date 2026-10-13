# 本轮整理验收（2026-09-17）

> **⚠ 历史文件（2026-09-17 的记录，2026-09-20 标注为历史）** —— 不要依据它行动。
>
> - 它描述的 **DeepSeek 执行 / Astra 验收** 分工协议已作废（见 `AGENT.md` 顶部）；
> - 它服务的**离线卡组评价线**已于 2026-09-19 判死，`work/` 数据 2026-09-20 已删除；
> - 其中的 PASS 数是 2026-09-17 的**历史读数**，不是当前门槛。
>
> 当前规范 [`../AGENT.md`](../AGENT.md)，当前规划 [`../PLAN.md`](../PLAN.md)。


DeepSeek 负责资产文档和两个准备脚本的主要实现，Astra 负责范围、设计、代码审查、修订意见与最终验收。本轮未调整构筑算法，也未运行新一轮大批量实验。

## 交付入口

- [项目资产地图](PROJECT_MAP.md)：生产代码、生成表、测试、参考副本、实验数据与发布物。
- [测试说明](../tests/README.md)：自动测试命令和适用边界。
- [构筑基线](building/BASELINE.md)、[下一步计划](building/NEXT_STEPS.md)：待继续工作的证据与验收条件。
- `scripts/verify-project.ps1`：构建、类型加载、普通回归、内核回归；每次独立日志与 JSON 汇总。
- `scripts/capture-research-baseline.py`：固定白名单的证据副本、SHA256、Git 版本和实验配置。它不是完整可重放的游戏/求解器分发包。

## 本次实际验证

完整验证从仓库外的 `work/preparation-audit/` 工作目录启动，四个阶段退出码均为 0：

| 阶段 | 结果 |
| --- | --- |
| Release 构建 | 通过；产物放入独立运行目录 |
| TypeLoadCheck | 通过 |
| 普通 PatchSmoke | 97 条 PASS 输出 |
| 含内核 PatchSmoke | 173 条 PASS 输出（包含普通场景） |

PASS 输出条数不是独立测试用例数量。上述检查不证明真人多客户端同步、重连、UI 或整局胜率。

原始证据（以下目录被 Git 忽略，交接时应另行保留）：

- `work/verification/20260917T120753845Z/summary.json`：命令、阶段退出码、日志路径和 DLL 哈希。
- `work/research-baselines/20260917T120911Z/manifest.json`：17 份文件，缺失 0 份，逐一复核 SHA256；Git HEAD 为 `b4583889dee3238ec6a8e73cb4015b9c78666d28`，工作树为脏状态。
- `work/preparation-audit/acceptance.json`：本轮工具验收摘要。
- `work/preparation-audit/before-hashes.json` 与 `inherited-changes.patch`：整理前源代码/测试/脚本哈希与接手前已跟踪改动补丁。补丁不包含所有未跟踪文件，不能替代完整工作区备份。

失败路径也已验收：模拟构建退出码 7 时，脚本以非零退出，后续三阶段标记 skipped，并保留失败汇总；快照拒绝目录外路径、已有目录、根目录本身及路径中的 Windows junction，原快照不被覆盖。

第一稿的 PowerShell 集合序列化与 Git 路径问题已修正。早期失败运行保留作诊断证据，不应被当作最新通过结果。

## 后续任务队列

superharness 已建立以下待办，尚未派发：

1. `build-eval-contract`：统一指标、结局分类、配对统计和当前预算的可重复性。
2. `build-factorial-redesign`：依赖上一项，解除删防御牌的混杂，再验证协同形状。
3. `build-value-proxy`：依赖前两项，再评估共享强度函数与选/删/升级有限差分。

继续之前先读计划，不将历史实验中不同预算、不同样本数的结果混成同一个结论。
