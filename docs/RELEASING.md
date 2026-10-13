# 发布流程 / Releasing

本文档是每次发布都必须遵守的固定流程。目标：访客可见的版本信息（README、Steam 创意工坊说明）与仓库变更记录始终一致；发布包只含允许进入发布的源码与数据；每一步都可核查。

## 0. 适用范围与权限

- 发布由项目所有者（owner）批准与执行。Worker 只做准备与证据，**不得**自动发布、上传或改写远程版本 / 可见性。
- 任何发布都必须在隔离的发布克隆（`work/release-<版本>/`）中进行；主开发树保持不动。
- 当前用户期望 GitHub Releases 与 Steam 创意工坊同时公开；以 owner 的最终指令为准。

### 社区源码快照

2026-10-13 owner 明确授权公开当前源码及必要测试管线，其他作者也会使用 Agent。因此源码协作快照包含 AGENT/AGENTS/PLAN、项目技能、原生回归和基准工具；旧的一概排除 Agent/测试文件的断言不适用。

源码上传在隔离克隆中完成，明确标注开发身份、历史测试证据和已知问题，并验证远端提交与可见文件。此类上传不自动升级 MOD 版本、不创建二进制 Release、不上传 Steam；安装包发布仍遵守下述流程。测试样本公开前匿名化，原始个人日志、缓存、凭据和游戏程序集不进入源码仓库。

## 1. 版本与三处可见信息（必须同时更新）

每次发布必须更新以下三处，缺一不可；只改 changenote 不算完成：

1. `src/CoopBots/mod_manifest.json` 的 `version`，以及 `src/CoopBots/ModEntry.cs` 的 `FallbackVersion`（两者必须相同）。
2. `README.md` 靠近顶部的最新更新区块（`## 最新更新 / Latest：<版本>（<日期>）`），包含同版本、准确、简洁的能力条目，并链接 `CHANGELOG.md`。
3. Steam 创意工坊说明（`docs/WORKSHOP_DESCRIPTION.txt`）靠近顶部的最新更新区块，含同样的版本与条目。

另外：`CHANGELOG.md` 顶部追加同版本的简洁发布段；`SOURCE_PACKAGE.md` 只更新版本号，除非确有明显错误需要更正。

## 2. Steam 说明校验

- `docs/WORKSHOP_DESCRIPTION.txt` 是公开的 Steam BBCode 说明正文（不是 VDF）：UTF-8，<= 7500 字节，正文中不得出现 ASCII 双引号（`"`），以免 VDF 引号转义出错。
- 保留必需的 RitsuLib 依赖、安装 / 兼容说明、协作玩法说明，以及许可证与第三方署名。
- 由该文件生成 VDF 时，`description` 字段必须正确引用 / 转义，并校验 `appid` / `publishedfileid` / `visibility`。

## 3. 发布包内容

- 使用显式 allowlist（发布所需的源码、数据与文档），并排除在制中的模拟器 / 测试 / 训练产物与实验性工具：
  - 排除：模拟器与训练脚本、测试专用数据、决策采集 instrumentation、未验收的实验。
- 构建后校验产物内容与哈希（DLL 清单、zip 内容、SHA256），确保与 allowlist 一致。

## 4. 构建与回归

- 在隔离克隆中依次运行 Release 构建、独立类型加载守卫和一次未过滤的完整内核回归（见 `scripts/verify-project.ps1`）。普通入口由完整配置覆盖，记录 covered-by-full，独立运行和计数为空；只有诊断明确需要时才额外运行普通配置。
- 记录退出码、日志路径与输出哈希；失败不得发布。

## 5. 发布与验证

- 只有在 owner 批准后才能上传 / 发布。发布后验证远端版本与可见性：
  - GitHub Releases：标签 / 版本与说明。
  - Steam 创意工坊：`publishedfileid` 对应条目的版本说明与可见性。
- 若远端与本地不一致，先修复再宣布完成。

## 6. 禁止事项

- 不自动发布、不自动上传、不自动改写远端可见性。
- 不在主开发树上构建发布包；不把在制实验纳入发布。
