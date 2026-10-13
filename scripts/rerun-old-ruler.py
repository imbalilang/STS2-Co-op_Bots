#!/usr/bin/env python3
"""把下午用旧尺(游戏内无人测试)跑过的那批场景,用离线宿主重跑一遍,并排放两份结果。

旧尺读数在 work/oracle/batch/<场景>/result.json(generatedScenario.actualOutcome 是【真打的仗】)。
新尺读数由 OfflineSearchHarness 产出,配置与旧尺同一套(--profile Medium --budget-ms 120000)。

用法:
    python scripts/rerun-old-ruler.py [--only D1] [--jobs 1]
"""

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BATCH = ROOT / "work" / "oracle" / "batch"
OUT = ROOT / "work" / "oracle" / "old-ruler-rerun"
HARNESS = ROOT / "third_party" / "CombatSolver-upstream-main" / "tools" / "OfflineSearchHarness" / "bin" / "Release" / "net9.0" / "OfflineSearchHarness.dll"
DOTNET = ROOT / "work" / "dotnet-win" / "dotnet.exe"

# 与旧尺同一套:PerformancePreset:Medium:120000ms:Beam=60:Nodes=24000:Branches=32
PROFILE = "Medium"
# 单次搜索的墙钟上限。旧尺是 120000ms，但离线宿主每次【重搜】都是一次全新的搜索，
# 而计划漂移会触发多次重搜 —— 120 秒 × N 次能把一场拖到几十分钟（实测有一场卡死 10 分钟）。
# 所以这里给 15 秒封顶，让批量一定跑得完；旧尺那 120 秒的截断效应在对照里单独说明。
BUDGET_MS = "15000"
SCENARIO_TIMEOUT_S = 420
MAX_TURNS = "40"

ENEMY_RE = re.compile(r"([A-Z_0-9#]+):(\d+)/(\d+)")


def outer_request(scenario_dir: Path, out_dir: Path) -> Path:
    """把批目录里【已 Apply 过】的 request.json 剥成外层请求 ——
    GeneratedCombatScenario.Apply 要求生成场景与"注入另一套牌组"互斥。"""
    src = json.loads((scenario_dir / "request.json").read_text(encoding="utf-8"))
    for key in ("runCards", "cards", "relics", "combatRelics", "potions", "powers", "modifierIds"):
        src[key] = []
    for key in ("checkpointArchivePath", "runSnapshotPath", "replayStatePath",
                "nativeStatePath", "replayPolicyOverridePath"):
        src[key] = None
    src["holdAfterInitialSearch"] = False
    src["scenarioId"] = scenario_dir.name + "-offline"
    src["evidenceDirectory"] = str(out_dir / "evidence")
    path = out_dir / "outer-request.json"
    out_dir.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(src, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def old_reading(scenario_dir: Path) -> dict:
    result = json.loads((scenario_dir / "result.json").read_text(encoding="utf-8"))
    outcome = (result.get("generatedScenario") or {}).get("actualOutcome") or {}
    metrics = result.get("solverMetrics") or {}
    return {
        "status": result.get("status"),
        "finished_turn": result.get("finishedTurn"),
        "combat_ended": result.get("combatEnded"),
        # —— 真打的仗 ——
        "actual_survived": outcome.get("survived"),
        "actual_final_hp": outcome.get("finalHp"),
        "actual_hp_lost": outcome.get("hpLost"),
        "actual_final_enemy_hp": outcome.get("finalEnemyHp"),
        # —— 求解器的预测 ——
        "pred_boundary": metrics.get("boundary"),
        "pred_score": metrics.get("score"),
        "pred_final_hp": metrics.get("finalHp"),
        "pred_final_enemy_hp": metrics.get("finalEnemyHp"),
        "pred_projected_hp_lost": metrics.get("projectedBattleHpLost"),
        "pred_expanded": metrics.get("selectedExpanded"),
        "pred_searched_turns": metrics.get("searchedTurns"),
        "wall_ms": result.get("elapsedMilliseconds"),
    }


def new_reading(out_dir: Path, returncode: int, seconds: float) -> dict:
    path = out_dir / "harness-result.json"
    if not path.exists():
        return {"error": f"没有产物（exit={returncode}）"}
    payload = json.loads(path.read_text(encoding="utf-8"))
    after = payload.get("afterTurns") or ""
    enemies = [(m.group(1), int(m.group(2)), int(m.group(3))) for m in ENEMY_RE.finditer(after)]
    # enemies=[] 是【全歼】—— 最好的读数，不是"没有读数"。战斗中列表里至少有一只怪，
    # 所以空列表只可能是打赢了。
    won = bool(after) and "enemies=[]" in after
    metrics = payload.get("pruneCounters") or {}
    steps = payload.get("steps") or []
    return {
        "exit": returncode,
        "reached": payload.get("reachedMilestone"),
        "error": (payload.get("error") or {}).get("message"),
        "combat_in_progress": payload.get("combatInProgress"),
        "final_hp": int(after.split("hp=")[1].split("/")[0]) if "hp=" in after else None,
        "won": won,
        "final_enemy_hp_sum": 0 if won else (sum(hp for _, hp, _ in enemies) if enemies else None),
        "final_enemies": [f"{name}:{hp}/{mx}" for name, hp, mx in enemies],
        "pred_final_enemy_hp": metrics.get("finalEnemyHp"),
        "pred_projected_hp_lost": metrics.get("projectedBattleHpLost"),
        "pred_score": metrics.get("score"),
        "pred_boundary": metrics.get("boundary"),
        "pred_expanded": metrics.get("selectedExpanded"),
        "replans": payload.get("replans"),
        "planned_choices": payload.get("plannedChoiceAnswers"),
        "unplanned_choices": payload.get("unplannedChoices"),
        "search_s": round(sum(s.get("Milliseconds", 0) for s in steps
                              if str(s.get("Name", "")).startswith("M2.1")) / 1000.0, 2),
        "exec_s": round(sum(s.get("Milliseconds", 0) for s in steps
                            if str(s.get("Name", "")).startswith("M3.1")) / 1000.0, 2),
        "wall_s": round(seconds, 2),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default=None, help="只跑名字含该串的场景，例如 D1")
    args = parser.parse_args()

    dirs = sorted(d for d in BATCH.iterdir() if d.is_dir())
    if args.only:
        dirs = [d for d in dirs if args.only in d.name]
    print(f"要重跑 {len(dirs)} 场 -> {OUT}")

    rows = []
    for index, scenario in enumerate(dirs, 1):
        out_dir = OUT / scenario.name
        request = outer_request(scenario, out_dir)
        started = time.time()
        try:
            proc = subprocess.run(
            [str(DOTNET), str(HARNESS),
             "--request", str(request),
             "--profile", PROFILE, "--budget-ms", BUDGET_MS,
             "--no-card-probe", "--deploy-plan", "--play-path", "gameaction",
             "--max-turns", MAX_TURNS, "--milestone", "M2",
             "--label", scenario.name, "--out", str(out_dir)],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            cwd=str(ROOT / "third_party" / "CombatSolver-upstream-main"),
            timeout=SCENARIO_TIMEOUT_S)
        except subprocess.TimeoutExpired:
            proc = None
        seconds = time.time() - started
        row = {"scenario": scenario.name,
               "old": old_reading(scenario),
               "new": new_reading(out_dir, proc.returncode if proc else -1, seconds)}
        rows.append(row)
        old, new = row["old"], row["new"]
        print(f"[{index}/{len(dirs)}] {scenario.name}: "
              f"旧 survived={old['actual_survived']} 敌血={old['actual_final_enemy_hp']} | "
              f"新 exit={new.get('exit')} error={str(new.get('error'))[:60]} "
              f"敌血={new.get('final_enemy_hp_sum')} 未计划选择={len(new.get('unplanned_choices') or [])} "
              f"({seconds:.1f}s)")

    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "comparison.jsonl").write_text(
        "\n".join(json.dumps(r, ensure_ascii=False) for r in rows), encoding="utf-8")
    print(f"\n写出 {OUT / 'comparison.jsonl'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
