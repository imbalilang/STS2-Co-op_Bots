---
name: coopbots-package-deploy
description: Package immutable verified gate artifacts under a fresh label, verify both DLLs and package contents, and deploy to mods/CoopBots when authorized. Covers -VerifiedGateBinding reuse without rebuilding or rerunning smoke, the legacy smoke-only boundary, client-must-be-closed, pre-deploy backup, and post-deploy sha256/symbol verification against stale binaries. Use when 要出包测试、要部署到游戏目录、要打包、改完代码要跑实机、部署后要确认加载的是新 DLL.
---

# 打包与部署（S4）

完整规范在 `AGENT.md` §5 **S4** 与 §3 验证机制。本文件是入口；冲突时以 `AGENT.md` 为准。

> **打包是用户开口才做的事。** 构建 + 回归不算打包，但往 `outputs/` 写工件、
> 覆盖 `mods/CoopBots` 属于需要授权的动作。

## 一条命令

```powershell
& '<bundled pwsh.exe>' -NoProfile -NonInteractive -File scripts/build.ps1 `
  -GameData '<actual game data>' -VerifiedGateBinding '<immutable-epoch-directory>/gate-run-binding.json' `
  -Label '<fresh-label>'
```

先按 `AGENT.md` §3 获取默认完整验证：生产构建、独立类型加载、一次 `EnableKernelTests=true` **无程序过滤参数**的完整 PatchSmoke。独立普通进程只有 `-IncludeOrdinary` 显式诊断才另跑；默认 ordinary 状态为 `covered-by-full`、数值通过数为 `null`，完整总数只记完整内核实际读数，不相加。原四门历史证据保持原样，本轮用户简化授权优先于旧重复执行要求。

`gate-run-binding.json` 由本次 `verify-project.ps1` 冻结输入并验证运行时工件后生成；旧 epoch runner 的绑定保持原样。绑定打包核对实际门禁退出码 0、当前源码/引用 DLL/包内文档与冻结输入、门禁 `build/` 和运行时归档双 DLL 的物理哈希；通过后**直接复制门禁工件，不重新构建或重跑 smoke**。只有双 DLL 相同还不够，不能绕过其它输入绑定；输入变了要获取匹配的新绑定，不能改写旧证据。

未传 `-VerifiedGateBinding` 的 legacy 路径仍自行构建、TypeLoadCheck 和 `--packaging-smoke`；其子集成功不能报成完整回归。已有有效绑定且输入未变时，同次交付无需重复完整运行。

只要求本地包时交付 zip；不自动关闭游戏、安装或写入自动测试哨兵。没有发布请求时保留版本号，不上传工坊。

## `-Label` 是强制的

**不带 `-Label` 会写到 `outputs/CoopBots-v<版本>.zip` —— 那是已发布工件，不得覆盖。**
（已经发生过一次：一个未发布的构建顶着发布名躺在那里。）

带上之后产物是 `outputs/CoopBots-v<版本>-<label>.zip`，测试包与发布包分得开。绑定打包要求 **全新 Label**，已有目录或 ZIP 都拒绝覆盖。

## 哈希的陷阱

旧 legacy 输出曾只打印 `CoopBots.dll` 的 SHA256，不能用它识别内核。绑定打包保存包验证收据，核对 **`CoopBots.dll`、`CoopBots.Kernel.dll` 和 ZIP 自身的 SHA256**；部署时继续按包内双 DLL 对齐，版本号或单壳哈希不够。（见 `PLAN.md` B6。）

## 部署

1. **必须关闭客户端** —— DLL 被占用，热解压会失败或半途而废。
2. 覆盖前先备份 `mods/CoopBots` 到 `work/deploy-backup-<时间>-pre-<label>/`。部署失败或新包异常时，旧 DLL 是唯一回退点。
3. 解到 `mods/CoopBots`（`…/Slay the Spire 2/mods/CoopBots`，完整路径见 `AGENT.md` §1）。
4. **部署后按 sha256 对齐包内与游戏目录的 `CoopBots.dll` / `CoopBots.Kernel.dll`，并扫描关键策略符号**。实测 2026-10-04：源码断言“一层必须打精英”在 `OwnerPolicyScenarios` 全绿，但游戏加载的旧 DLL 里 `requireElite` / `CanFinishWithElite` 字符串都不存在，实机因此逃精英。符号缺失时不是逻辑 bug，是部署了旧包。
5. 要跑实机的话，接着用实机管线：`.claude/skills/coopbots-live-test/SKILL.md`，社区准备与身份合同见 `docs/TESTING_AND_BENCHMARKS.md`。
   `scripts/run-live-loop.ps1` 是历史工具；其关闭客户端与部署行为必须逐项核对授权和进程所有权，不能按旧说明默认杀现有客户端。测试正常关闭失败时保留失败现场与触发文件所有权，按当前授权处理，禁止绕过自动审批拒绝。

## 完成判据

- **按 AGENT.md §3 默认完整验证通过，实际绑定与当前输入/物理双 DLL 匹配**；
- **ZIP 和包内双 DLL 的 sha256、包验证收据已记录**；
- **旧测试包未被覆盖**（且发布包路径没被动过）；
- **包内与部署后的 DLL sha256 一致，关键策略符号都扫到**；
- 部署后**哨兵文件已按需删除** —— 留着会让用户一开游戏就被劫持成一局自动 4BOT A10。

## 发版另说

正式发版走 `docs/RELEASING.md`（版本号 **4 处同步**、工坊上传）。
发版前 README 与工坊描述都要更新，**changenote 不够**（当前用户发布偏好及 `docs/RELEASING.md`；根目录 `AGENTS.md` 是历史文件，不是权威来源）。源码协作快照与 Steam 二进制发版分别说明，不用源码上传冒充新版本发布。
