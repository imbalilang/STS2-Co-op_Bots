#!/usr/bin/env python3
"""CoopBots 跨 session 持久化状态。

一个 JSON 状态文件 + 一份只追加的日志。目的是让**下一个 session**（或下一个轮次）
不必重新推导"现在走到哪、上一道门是死是活、哪条 backlog 挂着"。

    python scripts/state.py show                     一屏：阶段、门禁、活跃 backlog、最近实机
    python scripts/state.py get <key>                读一个键（点号取嵌套）
    python scripts/state.py set <key> <value>        写一个键（自动解析 JSON 标量）
    python scripts/state.py gate [--label <label>]   把最新一次 work/verification/*/summary.json 记进来
    python scripts/state.py backlog                  列 backlog
    python scripts/state.py backlog close <ID> [--note "..."]
    python scripts/state.py backlog reopen <ID> [--note "..."]
    python scripts/state.py live <label> [--log <路径>]   记一次实机批次
    python scripts/state.py log "<消息>"             追加到 journal.md 并记一条事件

设计约束（照 AGENT.md）：

* **不许撒谎**：`gate` 只陈述它真读到的东西。读不到 summary.json 就记 `unknown`，
  不沿用上一次的 `pass` —— 那正是 R5b/R5c 说的"恒为常数、看着像证据"。
* **单一事实来源**：backlog 的**定义**在 PLAN.md，**状态**在这里。两边靠 ID 对齐。
* 状态文件在 `work/agent-state/state/`（Git 忽略），不随 clone 交付。
"""

import argparse
import io
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

for _stream in ("stdout", "stderr"):
    try:
        setattr(sys, _stream, io.TextIOWrapper(getattr(sys, _stream).buffer,
                                               encoding="utf-8", errors="replace"))
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent.parent
STATE_DIR = ROOT / "work" / "agent-state" / "state"
STATE_FILE = STATE_DIR / "project-state.json"
JOURNAL_FILE = STATE_DIR / "journal.md"
VERIFICATION_ROOT = ROOT / "work" / "verification"

SCHEMA = 1

DEFAULT = {
    "schema": SCHEMA,
    "updated_utc": None,
    "phase": "P2-multiplayer-strength-and-cost",
    "version": None,
    "gates": {"status": "unknown", "run_dir": None, "finished_utc": None,
              "normal_pass": None, "kernel_pass": None},
    # 默认仅执行无过滤 EnableKernelTests=true；normal 无独立计数。
    # normal 门槛仍用于历史运行或显式 IncludeOrdinary 的独立 false 配置。
    # kernel 门槛只比较完整 true 配置的真实 PASS 行数。
    # 注意：`-- --kernel-power-only` 是一条**聚焦子集**，不是门禁那条。
    # 2026-09-20 之前这里写的是集中在子集口径的 94，于是拿完整的 265 去比 94
    # —— 比较恒真，等于没有门。门槛随 0.38.0 的场景新增重新基线过一次。
    "gate_thresholds": {"normal_pass": 172, "kernel_pass": 294},
    "live": {"label": None, "date": None, "log": None, "verdict": None},
    "backlog": [
        {"id": "B1", "title": "分配/GC（P2 闸门）", "status": "open"},
        {"id": "B2", "title": "occurrence 回退（未复现，先别改）", "status": "open"},
        {"id": "B3", "title": "plan step drift 只打第一个字段", "status": "open"},
        {"id": "B4", "title": "LIVING_FOG_NORMAL missing=7 定性", "status": "open"},
        {"id": "B5", "title": "重放 invalid-target 根因", "status": "open"},
        {"id": "B6", "title": "build.ps1 哈希只覆盖 CoopBots.dll", "status": "open"},
        {"id": "B7", "title": "未建模的多人牌 10 张", "status": "open"},
        {"id": "B8", "title": "搜索跑空仍从头重搜", "status": "open"},
        {"id": "B9", "title": "act3→act4 之后不再投票", "status": "open"},
        {"id": "B10", "title": "药水选择通路（捕获→应用→携带→运行期回答）", "status": "open"},
        {"id": "B11", "title": "两阶段 boss pending choice fail-closed 截断", "status": "open"},
    ],
    "events": [],
}


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def load():
    if not STATE_FILE.exists():
        state = json.loads(json.dumps(DEFAULT))
        state["updated_utc"] = now()
        return state
    try:
        # utf-8-sig: PowerShell 5.1 的 `Set-Content -Encoding UTF8` 会写 BOM，
        # 手工编辑过的状态文件也可能带；用 utf-8 解析会直接抛异常。
        state = json.loads(STATE_FILE.read_text(encoding="utf-8-sig"))
    except Exception as exc:
        # 坏掉的 state 不能让脚本变成"什么都查不到"。明说，然后从默认值重建。
        print(f"warning: {STATE_FILE} 无法解析（{exc}）；用默认值重建", file=sys.stderr)
        state = json.loads(json.dumps(DEFAULT))
    for key, value in DEFAULT.items():
        state.setdefault(key, value)
    return state


def save(state):
    state["updated_utc"] = now()
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(json.dumps(state, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def journal(message):
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%SZ")
    with JOURNAL_FILE.open("a", encoding="utf-8") as handle:
        handle.write(f"- **{stamp}** — {message}\n")


def dig(state, dotted, default=None):
    node = state
    for part in dotted.split("."):
        if not isinstance(node, dict) or part not in node:
            return default
        node = node[part]
    return node


def put(state, dotted, value):
    parts = dotted.split(".")
    node = state
    for part in parts[:-1]:
        node = node.setdefault(part, {})
    node[parts[-1]] = value


# --------------------------------------------------------------------------- gate

def newest_summary():
    if not VERIFICATION_ROOT.is_dir():
        return None
    runs = sorted((p for p in VERIFICATION_ROOT.iterdir() if p.is_dir()), key=lambda p: p.name)
    for run in reversed(runs):
        summary = run / "summary.json"
        if summary.is_file():
            return run, summary
    return None


def cmd_gate(args, state):
    found = newest_summary()
    if found is None:
        state["gates"] = {"status": "unknown", "run_dir": None, "finished_utc": None,
                          "normal_pass": None, "kernel_pass": None,
                          "note": "work/verification 下没有可读的 summary.json"}
        save(state)
        journal("门禁：**未记录** —— work/verification 下没有 summary.json")
        print("gates: unknown (no summary.json under work/verification)")
        return 0

    run, summary_path = found
    try:
        # summary.json 由 verify-project.ps1 用 `Set-Content -Encoding UTF8` 写出，
        # 在 PowerShell 5.1 下带 BOM；utf-8-sig 才能读。
        summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
    except Exception as exc:
        print(f"error: 无法解析 {summary_path}: {exc}", file=sys.stderr)
        return 2

    counts = {}
    for phase in summary.get("phases") or []:
        if isinstance(phase, dict) and phase.get("name"):
            counts[phase["name"]] = phase.get("pass_lines")

    gates = {
        "status": summary.get("status") or "unknown",
        "run_dir": str(Path(summary.get("run_dir") or run)).replace("\\", "/"),
        "finished_utc": summary.get("finished_utc"),
        "normal_pass": counts.get("patchsmoke"),
        "kernel_pass": counts.get("patchsmoke-kernel"),
        "phases": [{"name": p.get("name"), "status": p.get("status"),
                    "exit_code": p.get("exit_code"), "pass_lines": p.get("pass_lines")}
                   for p in (summary.get("phases") or []) if isinstance(p, dict)],
    }
    ordinary = (summary.get("suite_coverage") or {}).get("ordinary") or {}
    gates["protocol"] = summary.get("gate_protocol")
    gates["normal_status"] = ordinary.get("status") or "unconfirmed"
    gates["normal_evidence"] = ordinary
    if gates["protocol"] == "once-full-kernel-v1":
        full = [p for p in (summary.get("phases") or [])
                if isinstance(p, dict) and p.get("name") == "patchsmoke-kernel"]
        covered = (len(full) == 1 and full[0].get("status") == "passed"
                   and full[0].get("exit_code") == 0
                   and "-p:EnableKernelTests=true" in (full[0].get("args") or [])
                   and "--" not in (full[0].get("args") or []))
        # Coverage never manufactures the ordinary count from the full total.
        if ordinary.get("status") == "covered-by-full" and not covered:
            gates["normal_status"] = "unconfirmed"
        if "patchsmoke" not in counts:
            gates["normal_pass"] = None
    if args.label:
        gates["label"] = args.label

    thresholds = state.get("gate_thresholds") or {}
    notes = []
    if gates["status"] != "pass":
        notes.append(f"status={gates['status']}")
    if gates["protocol"] == "once-full-kernel-v1" and gates["normal_status"] == "unconfirmed":
        notes.append("普通入口覆盖未确认")
    for name, key, floor in (("普通套件", "normal_pass", thresholds.get("normal_pass")),
                             ("内核套件", "kernel_pass", thresholds.get("kernel_pass"))):
        actual = gates.get(key)
        if floor is not None and actual is not None and actual < floor:
            # 计数变少 ≠ 一定是回归：也可能是断言被有意撤掉。但两者都必须被人看见。
            notes.append(f"{name} {actual} < 阈值 {floor}")

    # 一次旧的 pass 不等于当前工作树的 pass。不说清楚，状态文件就会变成一个
    # "恒为 pass 的指标" —— 那正是 R5b 说的假证据。
    age_hours = None
    if gates.get("finished_utc"):
        try:
            finished = datetime.fromisoformat(gates["finished_utc"].replace("Z", "+00:00"))
            if finished.tzinfo is None:
                finished = finished.replace(tzinfo=timezone.utc)
            age_hours = (datetime.now(timezone.utc) - finished).total_seconds() / 3600.0
            gates["age_hours"] = round(age_hours, 2)
        except Exception:
            gates["age_hours"] = None
    if age_hours is not None and age_hours > 24:
        notes.append(f"这次运行是 {age_hours / 24:.1f} 天前的，**不代表当前工作树**")

    # notes 与 age_hours 必须在 save() 之前算完，否则写进文件的是半成品
    # （第一次就踩了这个：show 读不到 age_hours，过期警告永远不显示）。
    gates["notes"] = notes
    state["gates"] = gates
    save(state)

    journal("门禁：**%s** (%s) 普通=%s 内核=%s%s"
            % (gates["status"], Path(gates["run_dir"]).name if gates["run_dir"] else "?",
               ("covered-by-full（无独立计数）" if gates["normal_status"] == "covered-by-full"
                else gates["normal_pass"]), gates["kernel_pass"],
               ("；" + "；".join(notes)) if notes else ""))
    print(f"status : {gates['status']}")
    print(f"run    : {gates['run_dir']}")
    if gates["normal_status"] == "covered-by-full":
        print("normal : covered-by-full（无独立计数；证据为同一次完整套件）")
    else:
        print(f"normal : {gates['normal_pass']} PASS (阈值 {thresholds.get('normal_pass')})")
    print(f"kernel : {gates['kernel_pass']} PASS (阈值 {thresholds.get('kernel_pass')})")
    if notes:
        print("注意   : " + "；".join(notes))
    return 0


# ------------------------------------------------------------------------ backlog

def find_item(state, item_id):
    for item in state.get("backlog", []):
        if item.get("id", "").upper() == item_id.upper():
            return item
    return None


def cmd_backlog(args, state):
    if args.action in (None, "list"):
        for item in state.get("backlog", []):
            mark = "x" if item.get("status") == "closed" else " "
            print(f"[{mark}] {item['id']:<4} {item['title']}")
        return 0

    if args.action in ("close", "reopen"):
        if not args.id:
            print("error: backlog close/reopen 需要一个 ID", file=sys.stderr)
            return 2
        item = find_item(state, args.id)
        if item is None:
            print(f"error: 没有 {args.id}", file=sys.stderr)
            return 2
        item["status"] = "closed" if args.action == "close" else "open"
        if args.note:
            item["note"] = args.note
        save(state)
        journal(f"backlog {item['id']} → {item['status']}"
                + (f"（{args.note}）" if args.note else ""))
        print(f"{item['id']} → {item['status']}")
        return 0

    print(f"error: 未知动作 {args.action}", file=sys.stderr)
    return 2


# --------------------------------------------------------------------------- show

def cmd_show(args, state):
    gates = state.get("gates") or {}
    thresholds = state.get("gate_thresholds") or {}
    live = state.get("live") or {}
    open_items = [i for i in state.get("backlog", []) if i.get("status") != "closed"]

    print("CoopBots 状态")
    print(f"  阶段      : {state.get('phase')}")
    print(f"  版本      : {state.get('version') or '(未记录)'}")
    verdict = gates.get("status") or "unknown"
    extra = ""
    if verdict == "pass" and gates.get("normal_pass") is not None:
        extra = (f"  普通={gates.get('normal_pass')}/{thresholds.get('normal_pass')}"
                 f" 内核={gates.get('kernel_pass')}/{thresholds.get('kernel_pass')}")
    elif verdict == "pass" and gates.get("normal_status") == "covered-by-full":
        extra = ("  普通=covered-by-full（无独立计数）"
                 f" 内核={gates.get('kernel_pass')}/{thresholds.get('kernel_pass')}")
    age = gates.get("age_hours")
    if age is not None and age > 24:
        # 醒目的过期标记：旧的 pass 不是当前的 pass。
        verdict += f"  ⚠ {age / 24:.1f} 天前，不代表当前工作树"
    print(f"  最近门禁  : {verdict}{extra}")
    if gates.get("run_dir"):
        print(f"            {gates['run_dir']}")
    if live.get("label"):
        print(f"  最近实机  : {live['label']} ({live.get('date') or '?'}) {live.get('verdict') or ''}")
    print(f"  活跃 backlog ({len(open_items)}):")
    for item in open_items:
        print(f"            {item['id']:<4} {item['title']}")
    print(f"  状态文件  : {STATE_FILE.relative_to(ROOT)}")
    if not STATE_FILE.exists():
        print("            （还没落盘；状态是默认值）")
    return 0


def cmd_live(args, state):
    state["live"] = {"label": args.label, "date": args.date or now()[:10],
                     "log": args.log, "verdict": args.verdict}
    save(state)
    journal(f"实机批次 {args.label}" + (f"：{args.verdict}" if args.verdict else ""))
    print(f"live → {args.label}")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description="CoopBots 跨 session 持久化状态")
    sub = parser.add_subparsers(dest="command")

    sub.add_parser("show", help="一屏状态")

    p_get = sub.add_parser("get", help="读一个键")
    p_get.add_argument("key")

    p_set = sub.add_parser("set", help="写一个键")
    p_set.add_argument("key")
    p_set.add_argument("value")

    p_gate = sub.add_parser("gate", help="记录最新一次四道门结果")
    p_gate.add_argument("--label", default=None)

    p_backlog = sub.add_parser("backlog", help="backlog 状态")
    p_backlog.add_argument("action", nargs="?", default="list",
                           choices=["list", "close", "reopen"])
    p_backlog.add_argument("id", nargs="?", default=None)
    p_backlog.add_argument("--note", default=None)

    p_live = sub.add_parser("live", help="记录一次实机批次")
    p_live.add_argument("label")
    p_live.add_argument("--log", default=None)
    p_live.add_argument("--verdict", default=None)
    p_live.add_argument("--date", default=None)

    p_log = sub.add_parser("log", help="追加一条日志")
    p_log.add_argument("message")

    args = parser.parse_args(argv)
    state = load()

    if args.command in (None, "show"):
        return cmd_show(args, state)
    if args.command == "get":
        value = dig(state, args.key)
        print(json.dumps(value, ensure_ascii=False) if not isinstance(value, str) else value)
        return 0
    if args.command == "set":
        try:
            parsed = json.loads(args.value)
        except Exception:
            parsed = args.value
        put(state, args.key, parsed)
        save(state)
        journal(f"state.{args.key} = {args.value}")
        print(f"{args.key} = {args.value}")
        return 0
    if args.command == "gate":
        return cmd_gate(args, state)
    if args.command == "backlog":
        return cmd_backlog(args, state)
    if args.command == "live":
        return cmd_live(args, state)
    if args.command == "log":
        save(state)
        journal(args.message)
        print("logged")
        return 0

    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
