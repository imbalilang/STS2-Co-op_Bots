"""A5 批量实机测试 + 自动分析 + 通关率汇总。

一次调用跑 N 局多人 4-BOT A5，每局：杀客户端 → 部署 → 经 Steam 启动 → 看护 → 归档日志 →
跑 analyze-round.py 出单轮报告。全部结束后打印通关率。

为什么需要它：用户的目标是"A5 通关率 80%（连续 5 局至少 4 局通关）"，那是**统计量**，
单局日志说明不了任何事。所以判定必须落成可复核的批量读数：每局的胜负从日志的
`run outcome — victory=…` 行读（那一行由 CoopBots 自己在 NGameOverScreen._Ready 里打，
因为 shouldSave:false 让这些局不进 profile 的 run history，没有 run-*.json 可读），
而不是靠人看结尾。

用法:
    python scripts/a5-batch.py --label opt1 --games 5
    python scripts/a5-batch.py --label opt1 --games 5 --package outputs/CoopBots-v0.39.1-xxx
    python scripts/a5-batch.py --label opt1 --games 5 --dry-run     # 只打印会做什么
"""
import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LIVE_LOOP = os.path.join(REPO, "scripts", "run-live-loop.ps1")
ANALYZE = os.path.join(REPO, "work", "live-a5", "analyze-round.py")
APP_DATA = os.environ.get("APPDATA", "")
GODOT_LOG = os.path.join(APP_DATA, "SlayTheSpire2", "logs", "godot.log")
DAMAGE_METER = os.path.join(APP_DATA, "SlayTheSpire2", "DamageMeter_combat_state.json")


def newest_package():
    outputs = os.path.join(REPO, "outputs")
    candidates = []
    for name in os.listdir(outputs):
        if name.endswith(".zip"):
            continue
        path = os.path.join(outputs, name)
        if name.startswith("CoopBots-v") and "-" in name and os.path.isdir(os.path.join(path, "CoopBots")):
            candidates.append((os.path.getmtime(path), path))
    if not candidates:
        raise SystemExit("outputs/ 下没有带标签的测试包目录。先跑 build.ps1 -Label <label>。")
    return max(candidates)[1]


def outcome_of(log_text):
    """从日志读结局。返回 (victory|None, act, floor, run_seed)。"""
    m = None
    for m in re.finditer(r"CoopBots live test: run outcome — victory=(\w+) act=(\S+) floor=(\S+)", log_text):
        pass  # 取最后一条（理论上只有一条）
    victory = m.group(1) == "true" if m else None
    act = m.group(2) if m else None
    floor = m.group(3) if m else None
    started = None
    for s in re.finditer(r"CoopBots live test: multiplayer run started — seed=(\S+) ascension=(\d+) seats=(\d+) decks=\[([^\]]*)\]", log_text):
        started = s
    run_seed = started.group(1) if started else None
    if run_seed is None:
        pinned = None
        for p in re.finditer(r"lobby seed pinned to '([^']*)'", log_text):
            pinned = p
        run_seed = pinned.group(1) if pinned else None
    return victory, act, floor, run_seed


# 本轮开始的标记。**不能按文件大小偏移量切** —— 实测游戏在启动时会把 godot.log
# 截断（`run-live-loop.ps1` 报的"启动前日志长度=179864"是上一局留下的旧文件，新内容从 0
# 开始写），所以一个"启动前记录的字节偏移"在新文件里指向的是**本轮中间**：第一次归档把
# act-1 整段连 `multiplayer run started` 一起切没了，日志被判 INVALID，HP 轨迹也没了。
# 按标记切与截断/轮转无关：找本轮自己的开局行，从那一行起算。
ROUND_MARKERS = (
    "CoopBots live test: multiplayer run started",
    "CoopBots live test: lobby seed pinned to",
    "CoopBots live test: started a run",
    "CoopBots live test: armed",
)


def round_slice(text):
    """本轮的那一段日志：从最后一次开局标记的那一行起。没有标记就返回全部。"""
    best = max((text.rfind(marker) for marker in ROUND_MARKERS), default=-1)
    if best < 0:
        return text
    line_start = text.rfind("\n", 0, best)
    return text[line_start + 1:]


def read_text(path):
    with open(path, encoding="utf-8", errors="replace") as handle:
        return handle.read()


def validate_round(returncode, seed, before, after):
    # Never turn a failed launcher into five copies of the previous run's loss.
    if returncode != 0:
        raise RuntimeError(f"Live launcher failed with exit {returncode}; this attempt is not a game result")
    if before == after:
        raise RuntimeError("Live log did not change; stale results must not be counted")
    victory, act, floor, actual_seed = outcome_of(round_slice(after))
    if actual_seed != seed:
        raise RuntimeError(f"Run seed mismatch: requested {seed}, observed {actual_seed}")
    return victory, act, floor, actual_seed


def run_one(index, seed, package, silence, batch_dir, dry_run, perf, party, powershell):
    console_path = os.path.join(batch_dir, f"round-r{index:02d}.console.log")
    print(f"\n===== 第 {index} 局：seed(旗标)={seed} =====", flush=True)
    if dry_run:
        print(f"[dry] 会跑 run-live-loop.ps1 -Rounds 1 -Seed {seed} -Ascension 5 -Perf {perf} "
              f"-Party {party} -SilenceMinutes {silence} -PackageDir {package}")
        return None
    command = [
        powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", LIVE_LOOP,
        "-Rounds", "1", "-Seed", seed, "-Ascension", "5", "-Perf", perf,
        "-SilenceMinutes", str(silence), "-PackageDir", package,
    ]
    if party:
        command += ["-Party", party]
    before = read_text(GODOT_LOG)
    with open(console_path, "w", encoding="utf-8", errors="replace") as handle:
        process = subprocess.run(command, stdout=handle, stderr=subprocess.STDOUT, cwd=REPO)
    print(f"run-live-loop exit={process.returncode}", flush=True)

    # 只看本轮的那一段：滚动日志里混着上一局，场次归属会判错（S5）。
    after = read_text(GODOT_LOG)
    validate_round(process.returncode, seed, before, after)
    text = round_slice(after)
    log_path = os.path.join(batch_dir, f"godot-a5-r{index:02d}.log")
    with open(log_path, "w", encoding="utf-8", errors="replace") as handle:
        handle.write(text)
    # 逐场承伤留档。DamageMeter 的 JSON 在一次 run 内累积每个战斗段的 dmg_taken/dealt，
    # 但每一场都在覆盖同一个文件 —— 不在这里存下来，跑完一轮就只剩最后一个战斗段的数。
    if os.path.exists(DAMAGE_METER):
        try:
            meter_dir = os.path.join(batch_dir, "damagemeter")
            os.makedirs(meter_dir, exist_ok=True)
            shutil.copyfile(DAMAGE_METER, os.path.join(meter_dir, f"r{index:02d}.json"))
        except Exception as error:                                      # noqa: BLE001
            print(f"damagemeter 归档失败: {error}", flush=True)
    victory, act, floor, run_seed = outcome_of(text)
    report_path = os.path.join(batch_dir, f"analysis-r{index:02d}.md")
    if os.path.exists(ANALYZE):
        analysis = subprocess.run([sys.executable, ANALYZE, log_path, "--out", report_path],
                                  capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=REPO)
        if analysis.returncode != 0:
            print(f"analyze-round 失败: {analysis.stderr.strip()[:300]}", flush=True)
    # 终止方式分类（S5）：有 outcome 行 = 运行结束；有 NGame.Quit 且无 outcome = 外部退出/卡死
    ended = "How did the game end" in text
    return {
        "round": index,
        "flag_seed": seed,
        "run_seed": run_seed,
        "victory": victory,
        "act": act,
        "floor": floor,
        "ended": ended,
        "log": os.path.relpath(log_path, REPO),
        "report": os.path.relpath(report_path, REPO) if os.path.exists(report_path) else None,
        "console": os.path.relpath(console_path, REPO),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--powershell", default=shutil.which("pwsh") or "powershell",
                        help="PowerShell executable; prefer pwsh when available")
    parser.add_argument("--label", required=True, help="批次名，落在 work/live-a5/<日期>-<label>/")
    parser.add_argument("--games", type=int, default=5)
    parser.add_argument("--package", default="", help="测试包目录；缺省取 outputs/ 下最新的带标签包")
    parser.add_argument("--silence", type=int, default=4, help="卡死判定：多少分钟没有局面变化")
    parser.add_argument("--start", type=int, default=1, help="起始局号（续跑用）")
    # THE PREFIX IS THE A/B KEY, NOT THE LABEL. Seeds used to be derived from --label, so a
    # "before" and "after" batch got different maps and the pair compared two draws from the
    # map distribution instead of one change. The prefix is what makes a pair replayable:
    # pass the same one to both sides and every round sees the same map.
    parser.add_argument("--seed-prefix", default="B", help="种子前缀；A/B 两侧必须相同（缺省 B）")
    # USER-CONFIGURED TEST SETUP (2026-09-27): Pro difficulty + `high` spend tier, and a
    # 2 Silent / 2 Ironclad party. The mod's default is already BotDifficulty.Pro; `high`
    # replaces the old `ultra`, and the party default was all-Ironclad. Both travel in the
    # sentinel (`perf=` / `party=`), so a batch can always say which one it measured.
    parser.add_argument("--perf", default="high", help="锦标赛花费档 low|mid|high|ultra（缺省 high）")
    parser.add_argument("--party", default="SILENT,SILENT,IRONCLAD,IRONCLAD",
                        help="四席角色，按座位顺序；缺省 2 静默 2 铁甲")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    package = args.package or newest_package()
    if not args.dry_run and not os.path.exists(os.path.join(package, "CoopBots", "CoopBots.dll")):
        raise SystemExit(f"{package} 里没有 CoopBots/CoopBots.dll")
    stamp = datetime.datetime.now().strftime("%Y-%m-%d")
    batch_dir = os.path.join(REPO, "work", "live-a5", f"{stamp}-{args.label}")
    os.makedirs(batch_dir, exist_ok=True)

    print(f"批次目录: {batch_dir}")
    print(f"测试包  : {package}")
    print(f"局数    : {args.games}  飞升: 5  perf: {args.perf}  party: {args.party}  "
          f"静默阈值: {args.silence} 分钟")

    results = []
    for offset in range(args.games):
        index = args.start + offset
        seed = f"A5{args.seed_prefix[:6].upper()}{index:02d}"
        result = run_one(index, seed, package, args.silence, batch_dir, args.dry_run,
                         args.perf, args.party, args.powershell)
        if result is None:
            continue
        results.append(result)
        print(f"  结局: victory={result['victory']} act={result['act']} floor={result['floor']} "
              f"run_seed={result['run_seed']} ended={result['ended']}", flush=True)

    if args.dry_run:
        return
    valid_results = [r for r in results if r["ended"] and r["victory"] is not None]
    wins = sum(1 for r in valid_results if r["victory"])
    summary = {
        "label": args.label,
        "package": os.path.relpath(package, REPO),
        "ascension": 5,
        "perf": args.perf,
        "party": args.party,
        "games": len(valid_results),
        "attempts": len(results),
        "wins": wins,
        "clear_rate": (wins / len(valid_results)) if valid_results else 0.0,
        "results": results,
    }
    summary_path = os.path.join(batch_dir, "summary.json")
    with open(summary_path, "w", encoding="utf-8") as handle:
        json.dump(summary, handle, ensure_ascii=False, indent=2)
    print(f"\n===== 通关率 {wins}/{len(valid_results)} = "
          f"{(100.0 * wins / len(valid_results)) if valid_results else 0:.0f}% "
          f"（完成 {len(valid_results)}/{len(results)} 次尝试） =====")
    print(f"汇总: {summary_path}")

    # THE BATCH ENDS WITH THE ANALYSIS, NOT WITH "now go read the logs". A five-round batch is
    # ~50 minutes of wall clock; leaving the conclusion as a separate command means the run
    # finishes silently and whoever is waiting has to notice and come back. So the report is
    # generated here, tee'd to `report.txt`, and printed into this process's own stdout — which
    # is exactly what the background task's completion notification hands back.
    report = run_report(batch_dir)
    print(f"\n分析: {os.path.join(batch_dir, 'report.txt')}")
    if report:
        print("\n" + report)


def run_report(batch_dir):
    """Run scripts/a5-report.py over the batch and return its text (empty when unavailable)."""
    reporter = os.path.join(REPO, "scripts", "a5-report.py")
    if not os.path.exists(reporter):
        return ""
    try:
        done = subprocess.run([sys.executable, reporter, batch_dir],
                              capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=REPO)
        text = done.stdout or done.stderr
    except Exception as error:                                          # noqa: BLE001
        return f"（批次报告生成失败：{error}）"
    try:
        with open(os.path.join(batch_dir, "report.txt"), "w", encoding="utf-8") as handle:
            handle.write(text)
    except Exception:                                                   # noqa: BLE001
        pass
    return text


if __name__ == "__main__":
    main()
