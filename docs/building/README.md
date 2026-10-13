# 离线卡组评价线（已废弃）

> **⚠️ 这条线已经判死。本目录下所有文档都是历史记录，不要据它们安排新工作。**

## 它是什么

2026-09-17 到 09-19 期间做过一条**离线**的卡组/选牌质量评价线：

- 一份"血耗尺子"（oracle）：把牌组丢给 CombatSolver 的离线宿主跑，用掉血量当牌组强度；
- 一套 DraftSim / DeckSim 调参与社区牌组校验工具；
- 一批 shiv（飞刀）包的建模、ELO 配对与关系图实验。

## 为什么判死

判据与结论记录在项目记忆 `project_offline_ruler_abandoned`：血耗尺子与 DraftSim 调参线**同时判死**。
主要原因是该尺子无法证明"真能赢"这一档（见 `project_verification_protocol`），
而绑定的评估预算（60–145 秒/次）只够定几十个参数。

**社区数据继续用**：`work/spire-codex/`、`scripts/fetch-spire-codex-*.py`、
`scripts/import-spire-codex-*.py` 与第 4 节生成的 `Baked*.cs` **不在废弃范围内**，
它们是另一条仍在使用的数据来源。

## 对应的文件现在在哪

| 内容 | 状态 |
|---|---|
| 本目录下的实验记录（`SHIV_*.md`、`PICK_*.md`、`BUDGET_CALIBRATION.md` 等） | 保留，只作历史 |
| `work/oracle/`、`work/shiv-*`、`work/building-*`、`work/pick-*`、`work/precision-200/` | **2026-09-20 已删除**（约 3.35GB），见 `work/CLEANUP-2026-09-20.md` |
| `scripts/oracle-*.py`、`scripts/shiv_*.py`、`scripts/building_*.py`、`scripts/analyze-*.py`、`tests/test_building_*.py` | 代码仍在，但它们的输入目录已删除，**直接跑会失败** |
| `third_party/CombatSolver-upstream-main/tools/OfflineSearchHarness/` | 参考副本保留（它同时是内核导入实践的对照物） |

**不要再往这条线里加东西。** 当前主线是战斗内核与实机执行，见 [`../README.md`](../README.md) 与
[`../../AGENT.md`](../../AGENT.md) §5。
