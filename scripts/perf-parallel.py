#!/usr/bin/env python3
"""真实规模并行吞吐测试(派单62 更正版主线)。

三件事:
  1. 分段 CPU 归因 —— 每个阶段报 CPU 秒 / 墙钟(占用率)
  2. 串行基线 vs N=4/8/16 并发的真实墙钟吞吐倍数
  3. **硬闸**:并行下读数必须与串行【逐位相同】,不同就立刻停并报出来

用法:
    python scripts/perf-parallel.py [--runs 16] [--budget-ms 15000] [--max-budget 25]
"""

import argparse
import json
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BATCH = ROOT / "work" / "oracle" / "batch"
COMPARISON = ROOT / "work" / "oracle" / "old-ruler-rerun" / "comparison.jsonl"
OUT = ROOT / "work" / "oracle" / "perf-parallel"
HARNESS = ROOT / "third_party" / "CombatSolver-upstream-main" / "tools" / "OfflineSearchHarness" / "bin" / "Release" / "net9.0" / "OfflineSearchHarness.dll"
DOTNET = ROOT / "work" / "dotnet-win" / "dotnet.exe"

FINGERPRINT_KEYS = ("boundary", "score", "selectedExpanded", "totalExpanded", "finalHp",
                    "finalEnemyHp", "projectedBattleHpLost", "searchedTurns", "combatEndedTurn")


def outer_request(scenario: Path, out_dir: Path) -> Path:
    src = json.loads((scenario / "request.json").read_text(encoding="utf-8"))
    for key in ("runCards", "cards", "relics", "combatRelics", "potions", "powers", "modifierIds"):
        src[key] = []
    for key in ("checkpointArchivePath", "runSnapshotPath", "replayStatePath",
                "nativeStatePath", "replayPolicyOverridePath"):
        src[key] = None
    src["holdAfterInitialSearch"] = False
    src["scenarioId"] = scenario.name + "-perf"
    src["evidenceDirectory"] = str(out_dir / "evidence")
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / "outer-request.json"
    path.write_text(json.dumps(src, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def pick_scenarios(count: int, max_budget: float, all_scenarios: bool = False) -> list:
    if all_scenarios:
        return sorted(d for d in BATCH.iterdir() if d.is_dir())
    """挑【真实整场长度】的场景:优先用上一轮实测的 (搜索+执行) 在 3–max_budget 秒之间的。"""
    cost = {}
    if COMPARISON.exists():
        for line in COMPARISON.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            row = json.loads(line)
            new = row["new"]
            total = (new.get("search_s") or 0) + (new.get("exec_s") or 0)
            if new.get("final_enemy_hp_sum") is not None and 3 <= total <= max_budget:
                cost[row["scenario"]] = total
    ordered = sorted(cost, key=lambda name: cost[name])
    if len(ordered) >= count:
        # 均匀取样，别全挑最便宜的
        step = len(ordered) / count
        return [BATCH / ordered[int(i * step)] for i in range(count)]
    return [BATCH / name for name in ordered] or sorted(d for d in BATCH.iterdir() if d.is_dir())[:count]


def run_one(scenario: Path, out_dir: Path, budget_ms: str, max_turns: str, dop: int = 1):
    request = outer_request(scenario, out_dir)
    started = time.time()
    proc = subprocess.run(
        [str(DOTNET), str(HARNESS), "--request", str(request),
         "--profile", "Medium", "--budget-ms", budget_ms,
         "--no-card-probe", "--deploy-plan", "--play-path", "gameaction",
         "--dop", str(dop),
         "--max-turns", max_turns, "--milestone", "M2",
         "--label", scenario.name, "--out", str(out_dir)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        cwd=str(ROOT / "third_party" / "CombatSolver-upstream-main"), timeout=900)
    return scenario.name, round(time.time() - started, 2), proc.returncode


def read_run(out_dir: Path) -> dict:
    path = out_dir / "harness-result.json"
    if not path.exists():
        return {"error": "没有产物"}
    payload = json.loads(path.read_text(encoding="utf-8"))
    metrics = payload.get("pruneCounters") or {}
    steps = payload.get("steps") or []
    return {
        "fingerprint": {key: metrics.get(key) for key in FINGERPRINT_KEYS} | {
            "afterTurns": payload.get("afterTurns"),
            "plannedChoices": payload.get("plannedChoiceAnswers"),
            "replans": payload.get("replans"),
        },
        "wall_s": round(sum(s.get("Milliseconds", 0) for s in steps) / 1000.0, 2),
        "cpu_s": round(sum(s.get("CpuMilliseconds", 0) for s in steps) / 1000.0, 2),
        "total_cpu_s": round((payload.get("totalCpuMilliseconds") or 0) / 1000.0, 2),
        "steps": [{"name": str(s.get("Name"))[:6], "wall_ms": s.get("Milliseconds"),
                   "cpu_ms": s.get("CpuMilliseconds")} for s in steps],
        "error": (payload.get("error") or {}).get("message"),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", type=int, default=16)
    parser.add_argument("--budget-ms", default="15000")
    parser.add_argument("--max-turns", default="40")
    parser.add_argument("--max-budget", type=float, default=25.0)
    parser.add_argument("--all", action="store_true",
                        help="用全部场景（不按成本过滤）—— 全批的诚实基准")
    parser.add_argument("--levels", default="1,4,8,16")
    parser.add_argument("--dop", type=int, default=1, help="每个进程内部的求解器并行度")
    args = parser.parse_args()

    scenarios = pick_scenarios(args.runs, args.max_budget, args.all)
    if args.all:
        args.runs = len(scenarios)
    print(f"场景数 {len(scenarios)}；并行档位 {args.levels}\n")

    results = {}
    for level in [int(x) for x in args.levels.split(",")]:
        label = f"N{level}" if args.dop == 1 else f"N{level}xD{args.dop}"
        print(f"=== {label} ===")
        started = time.time()
        with ThreadPoolExecutor(max_workers=level) as pool:
            finished = list(pool.map(
                lambda s: run_one(s, OUT / label / s.name, args.budget_ms, args.max_turns, args.dop),
                scenarios))
        total = time.time() - started
        runs = {name: read_run(OUT / label / name) for name, _, _ in finished}
        codes = {name: code for name, _, code in finished}
        results[label] = {"total_wall_s": round(total, 2), "runs": runs, "exit_codes": codes}
        ok = sum(1 for r in runs.values() if r.get("error") is None)
        per_run = total / len(scenarios)
        print(f"  总墙钟 {total:.1f}s / {len(scenarios)} 场 = {per_run:.2f}s/场 "
              f"（跑通 {ok}）；吞吐 {len(scenarios) / total:.2f} 场/秒\n")

    # —— 硬闸:并行 vs 串行的读数必须逐位相同 ——
    print("=== 硬闸:读数逐位对照(基准 = N1 串行) ===")
    base = results.get("N1")
    verdict = True
    if base:
        for label, data in results.items():
            if label == "N1":
                continue
            diffs = [name for name, run in data["runs"].items()
                     if run.get("fingerprint") != base["runs"].get(name, {}).get("fingerprint")]
            status = "逐位相同" if not diffs else f"⚠ 有差异 {len(diffs)} 场: {diffs[:3]}"
            if diffs:
                verdict = False
            print(f"  {label}: {status}")

    (OUT / "perf.json").write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n写出 {OUT / 'perf.json'}")
    print(f"硬闸判定: {'通过 —— 并行不改变读数' if verdict else '⚠ 不通过 —— 并行改变了读数，停并上报'}")
    return 0 if verdict else 1


if __name__ == "__main__":
    sys.exit(main())
