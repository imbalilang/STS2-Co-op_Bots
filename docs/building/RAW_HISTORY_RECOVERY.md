# 单人 Silent 社区原始历史恢复

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


2026-09-18 使用项目现有 Spire-Codex shared-run 接口及读取工具，成功获取冻结的六份原始历史；未扩大抓取范围。六份均通过 v0.111.0、单人 Silent、A10、standard、无 modifiers、未放弃及胜负与原候选一致性校验，3 胜 3 负。

共含 156 条非空 card_choices 的玩家楼层记录、564 个卡牌选项条目。这些数量不等同于独立奖励页面。源文件保留在 work/spire-codex/runs-full，来源 URL 和 SHA256 见 work/building-raw-history-recovery/source-validation.json，下载结果见同目录 fetch-results.json。

已确认社区接口有项目目标版本的完整序列化跑局历史；此前缺的是本地对应缓存。记录含选牌候选和结果、HP、各类变化及终局状态，但没有逐次选牌前的直接牌组/遗物/药水快照。事件顺序、候选分组及全状态重建仍待 DeepSeek 执行、Astra 验收；尚未导出合格决策状态，也未新增战斗或训练。
