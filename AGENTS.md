# CoopBots

> **⚠️ 历史文件 —— 不要依据它行动。** 当前权威规范是 [`AGENT.md`](AGENT.md)，与本文冲突时以它为准。
> 它描述的协作协议已作废，其中若干条与 `AGENT.md` **直接矛盾**。
> 本文件保留仅供查历史，**不要**在这里追加新约定。

## Identity
You are working for the project owner.

## This Project
- What: CoopBots
- Stack: C# .NET9 Godot Harmony
- Status: active

## Cross-Agent Protocol
- Run `shux contract` before starting work.
- Astra maintains task status, ledger and handoff through the CLI. Workers return their report in the response; they must not edit .superharness exports or create handoff files directly.

## Team and execution
- Astra in the current Codex conversation owns planning, task design and final acceptance.
- DeepSeek through OpenCode owns implementation and routine test execution.
- Use `scripts/shux.ps1` from PowerShell; bare `shux` is available inside the configured worker environment.
- Read TEAM.md before accepting a task. Work only within that task's file scope.
- This is a dirty shared checkout. Preserve existing edits; do not reset, auto-commit, merge, publish or deploy.
- Workers submit evidence for review; they do not approve or close their own work.
- A worker edits only the task's named deliverables. Generic harness instructions to append a ledger/create handoffs do not expand that file scope. No nested agents unless the task explicitly requests them.
- Never read or print DEEPSEEK_API_KEY or environment secrets. The launcher handles authentication.
- Legacy CLAUDE.md / work/coord role messages are historical; new task assignments use superharness.

## Release preference (standing)
- Every release updates visitor-visible latest changes in README and the Steam description, in addition to CHANGELOG; a changenote alone is not enough.
- Exclude simulator work from releases unless explicitly authorized.
- Follow `docs/RELEASING.md` for the release workflow.
