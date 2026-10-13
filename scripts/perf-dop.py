#!/usr/bin/env python3
"""DOP 扫描(派单63):求解器原生并行能不能开,判据是**指纹逐位相同**。

关键:用【节点预算】而不是墙钟预算。墙钟下时钟仍会截断,那会把"并行度"和"走多远"混在一起。
节点预算 + DOP 才是真正的"同一份工作、并行做"。

用法:
    python scripts/perf-dop.py --dop 1,2,4,8,16 --scenarios 4
    python scripts/perf-dop.py --wall-clock        # 顺带:墙钟预算下的稳定性
"""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BATCH = ROOT / "work" / "oracle" / "batch"
PERF = ROOT / "work" / "oracle" / "perf-parallel" / "perf.json"
OUT = ROOT / "work" / "oracle" / "perf-dop"
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
    src["scenarioId"] = scenario.name + "-dop"
    src["evidenceDirectory"] = str(out_dir / "evidence")
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / "outer-request.json"
    path.write_text(json.dumps(src, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def pick_scenarios(count: int) -> list:
    """挑搜索能在节点预算内【自己跑完】的场景(boundary=None),这样 DOP 才只影响"怎么算"不影响"算到哪"。"""
    names = []
    if PERF.exists():
        data = json.loads(PERF.read_text(encoding="utf-8"))
        runs = (data.get("N1") or {}).get("runs") or {}
        for name, run in sorted(runs.items()):
            fp = run.get("fingerprint") or {}
            if fp.get("boundary") == "None" and (run.get("wall_s") or 99) <= 12:
                names.append(name)
    return [BATCH / name for name in names[:count]]


def run(scenario: Path, out_dir: Path, dop: int, budget_ms: str) -> dict:
    request = outer_request(scenario, out_dir)
    started = time.time()
    proc = subprocess.run(
        [str(DOTNET), str(HARNESS), "--request", str(request),
         "--profile", "Medium", "--nodes", "24000", "--budget-ms", budget_ms,
         "--dop", str(dop), "--no-card-probe", "--deploy-plan", "--play-path", "gameaction",
         "--max-turns", "40", "--milestone", "M2",
         "--label", f"{scenario.name}-dop{dop}", "--out", str(out_dir)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        cwd=str(ROOT / "third_party" / "CombatSolver-upstream-main"), timeout=1800)
    return {"wall_s": round(time.time() - started, 2), "exit": proc.returncode}


def read(out_dir: Path, timed: dict) -> dict:
    path = out_dir / "harness-result.json"
    if not path.exists():
        return {"error": "没有产物", "exit": timed["exit"], "wall_s": timed["wall_s"]}
    payload = json.loads(path.read_text(encoding="utf-8"))
    metrics = payload.get("pruneCounters") or {}
    steps = payload.get("steps") or []
    search = [s for s in steps if str(s.get("Name", "")).startswith("M2.1")]
    return {
        "exit": timed["exit"],
        "wall_s": timed["wall_s"],
        "search_wall_s": round((search[0]["Milliseconds"] if search else 0) / 1000.0, 2),
        "search_cpu_s": round(((search[0].get("CpuMilliseconds") or 0) / 1000.0) if search else 0.0, 2),
        "total_cpu_s": round((payload.get("totalCpuMilliseconds") or 0) / 1000.0, 2),
        "duty": None,
        "fingerprint": {key: metrics.get(key) for key in FINGERPRINT_KEYS} | {
            "afterTurns": payload.get("afterTurns"),
            "replans": payload.get("replans"),
            "plannedChoices": payload.get("plannedChoiceAnswers"),
        },
        "error": (payload.get("error") or {}).get("message"),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dop", default="1,2,4,8,16")
    parser.add_argument("--scenarios", type=int, default=4)
    parser.add_argument("--budget-ms", default="600000", help="节点的安全网；要大到让节点预算说话")
    args = parser.parse_args()

    scenarios = pick_scenarios(args.scenarios)
    levels = [int(x) for x in args.dop.split(",")]
    print(f"场景 {len(scenarios)} 个；dop {levels}；节点预算 24000，墙钟安全网 {args.budget_ms}ms\n")

    table = {}
    for scenario in scenarios:
        table[scenario.name] = {}
        for dop in levels:
            timed = run(scenario, OUT / scenario.name / f"dop{dop}", dop, args.budget_ms)
            data = read(OUT / scenario.name / f"dop{dop}", timed)
            if data.get("total_cpu_s") and data.get("wall_s"):
                data["duty"] = round(data["total_cpu_s"] / data["wall_s"], 2)
            table[scenario.name][f"dop{dop}"] = data
            fp = data.get("fingerprint") or {}
            print(f"  {scenario.name:26s} dop={dop:2d}  "
                  f"墙钟 {data.get('wall_s')}s  搜索 {data.get('search_wall_s')}s  "
                  f"CPU {data.get('total_cpu_s')}s  占空 {data.get('duty')}  搜索CPU {data.get('search_cpu_s')}s  "
                  f"expanded={fp.get('selectedExpanded')} score={fp.get('score')} "
                  f"boundary={fp.get('boundary')}")
        print()

    print("=== 硬闸:与 dop=1 逐位对照 ===")
    all_same = True
    for name, per_dop in table.items():
        base = (per_dop.get("dop1") or {}).get("fingerprint")
        for label, data in per_dop.items():
            if label == "dop1" or base is None:
                continue
            fp = data.get("fingerprint")
            if fp == base:
                continue
            all_same = False
            diff = {k: (base.get(k), (fp or {}).get(k)) for k in set(base) | set(fp or {})
                    if base.get(k) != (fp or {}).get(k)}
            print(f"  ⚠ {name} {label} 不同: {json.dumps(diff, ensure_ascii=False)[:400]}")
    if all_same:
        print("  全部逐位相同 —— DOP 不改变读数")

    (OUT / "dop.json").write_text(json.dumps(table, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n写出 {OUT / 'dop.json'}")
    return 0 if all_same else 1


if __name__ == "__main__":
    sys.exit(main())
