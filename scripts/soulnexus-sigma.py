#!/usr/bin/env python3
"""SoulNexusElite 的数据质量测试(派单64 修正版)。

要报三个数(缺一条数就没用):
  σ         —— 直接决定后面所有实验要多少场
  通关率     —— 太高 = 天花板效应,方差小但没信息
  平均回合数 —— ≤4 回合 ⇒ 测的是"能不能快速打掉",不是续航

外加配对 ρ:两副牌用同一批种子,报 sd(A−B) 反推相关系数。

用法:
    python scripts/soulnexus-sigma.py --probe          # 只跑一场确认遭遇 ID
    python scripts/soulnexus-sigma.py                  # 跑全套
"""

import argparse
import json
import os
import re
import statistics
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BATCH = ROOT / "work" / "oracle" / "batch"
OUT = ROOT / "work" / "oracle" / "soulnexus"
HARNESS = ROOT / "third_party" / "CombatSolver-upstream-main" / "tools" / "OfflineSearchHarness" / "bin" / "Release" / "net9.0" / "OfflineSearchHarness.dll"
DOTNET = ROOT / "work" / "dotnet-win" / "dotnet.exe"

ENCOUNTER = "SOUL_NEXUS_ELITE"
CONTROL = "AEONGLASS_BOSS"
# 用【节点预算】而不是墙钟：节点预算才是确定性的那个，而且两边必须同配置才可比。
# 6000 是实测值：SoulNexus 的树比 Boss 深得多，24000 会让搜索跑过任何合理墙钟。
NODES = "6000"
SEARCH_TIMEOUT_MS = "120000"    # 只是安全网，真正的界是 NODES
BUDGET_MS = "120000"
# 基准并行配置：16 进程 × 每进程 dop=2（实测最快，1.42 秒/场，且读数逐位不变）。
# 从此所有测试都走这个，不再串行。
JOBS = 16
DOP = 2
TURN_RE = re.compile(r"turn=(\d+)")
ENEMY_RE = re.compile(r"([A-Z_0-9#]+):(\d+)/(\d+)")


def scenario_inputs(deck: str, encounter: str, seed: str, out_dir: Path) -> Path:
    """以该牌组在批里的 input.json 为底,换遭遇与种子。"""
    base = BATCH / f"{deck}-AEONGLASS_BOSS" / "input.json"
    options = json.loads(base.read_text(encoding="utf-8"))
    options["encounterId"] = encounter
    options["seed"] = seed
    options["actIndex"] = 2            # Glory（第三幕），0 基
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / "input.json"
    path.write_text(json.dumps(options, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def outer_request(deck: str, scenario_path: Path, seed: str, out_dir: Path) -> Path:
    src = json.loads((BATCH / f"{deck}-AEONGLASS_BOSS" / "request.json").read_text(encoding="utf-8"))
    for key in ("runCards", "cards", "relics", "combatRelics", "potions", "powers", "modifierIds"):
        src[key] = []
    for key in ("checkpointArchivePath", "runSnapshotPath", "replayStatePath",
                "nativeStatePath", "replayPolicyOverridePath"):
        src[key] = None
    src["holdAfterInitialSearch"] = False
    src["generatedScenarioPath"] = str(scenario_path)
    src["seed"] = seed
    src["targetRoomType"] = "Elite"
    src["targetMapPointType"] = "Elite"
    src["scenarioId"] = f"{deck}-{scenario_path.parent.name}"
    src["evidenceDirectory"] = str(out_dir / "evidence")
    path = out_dir / "outer-request.json"
    path.write_text(json.dumps(src, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def run_one(deck: str, encounter: str, seed: str, tag: str, extra_env: dict | None = None,
            scenario_override=None) -> dict:
    out_dir = OUT / tag
    # scenario_override：调用方自己构造了牌组（例如加了包件牌的实验），别让这里把它覆盖掉。
    scenario = scenario_override if scenario_override is not None else scenario_inputs(deck, encounter, seed, out_dir)
    request = outer_request(deck, scenario, seed, out_dir)
    started = time.time()
    proc = subprocess.run(
        [str(DOTNET), str(HARNESS), "--request", str(request),
         "--character", "IRONCLAD", "--ascension", "10",
         "--profile", "Medium", "--nodes", NODES, "--budget-ms", BUDGET_MS,
         "--search-timeout-ms", SEARCH_TIMEOUT_MS,
         "--dop", str(DOP),
         "--no-card-probe", "--deploy-plan", "--play-path", "gameaction",
         "--max-turns", "40", "--milestone", "M2",
         "--label", tag, "--out", str(out_dir)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        cwd=str(ROOT / "third_party" / "CombatSolver-upstream-main"), timeout=600,
        env={**os.environ, **(extra_env or {})})
    wall = time.time() - started
    result = {"tag": tag, "deck": deck, "encounter": encounter, "seed": seed,
              "exit": proc.returncode, "wall_s": round(wall, 2)}
    path = out_dir / "harness-result.json"
    if not path.exists():
        result["error"] = "没有产物"
        return result
    payload = json.loads(path.read_text(encoding="utf-8"))
    after = payload.get("afterTurns") or ""
    enemies = [(m.group(1), int(m.group(2))) for m in ENEMY_RE.finditer(after)]
    won = "enemies=[]" in after
    turn = TURN_RE.search(after)
    result |= {
        "error": (payload.get("error") or {}).get("message"),
        "reached": payload.get("reachedMilestone"),
        "won": won,
        "final_enemy_hp": 0 if won else (sum(hp for _, hp in enemies) if enemies else None),
        "turns": int(turn.group(1)) if turn else None,
        "replans": payload.get("replans"),
        "unplanned": len(payload.get("unplannedChoices") or []),
    }
    return result


def describe(rows: list, label: str) -> dict:
    ok = [r for r in rows if r.get("final_enemy_hp") is not None]
    hp = [r["final_enemy_hp"] for r in ok]
    turns = [r["turns"] for r in ok if r.get("turns")]
    failed = [r for r in rows if r.get("final_enemy_hp") is None]
    stats = {
        "label": label, "n": len(ok), "failed": len(failed),
        "mean": round(statistics.mean(hp), 1) if hp else None,
        "sd": round(statistics.stdev(hp), 1) if len(hp) > 1 else None,
        "min": min(hp) if hp else None, "max": max(hp) if hp else None,
        "span": (max(hp) - min(hp)) if hp else None,
        "clear_rate": round(sum(1 for r in ok if r["won"]) / len(ok), 3) if ok else None,
        "mean_turns": round(statistics.mean(turns), 1) if turns else None,
        "turn_span": f"{min(turns)}–{max(turns)}" if turns else None,
        "wall_s_per_run": round(statistics.mean([r["wall_s"] for r in rows]), 1) if rows else None,
    }
    print(f"  {label:34s} n={stats['n']:2d} 均值{str(stats['mean']):>7s} sd={str(stats['sd']):>6s} "
          f"跨度{str(stats['span']):>6s} 通关率={stats['clear_rate']} "
          f"回合{stats['mean_turns']}({stats['turn_span']}) {stats['wall_s_per_run']}s/场")
    if failed:
        print(f"      ⚠ {len(failed)} 场无读数: {[r['tag'] for r in failed][:4]}")
    return stats


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--median", default="D9")
    parser.add_argument("--strong", default="D2")
    parser.add_argument("--weak", default="D5")
    parser.add_argument("--seeds", type=int, default=12)
    parser.add_argument("--control-seeds", type=int, default=8)
    args = parser.parse_args()

    if args.probe:
        r = run_one("D9", ENCOUNTER, "SOULPROBE1", "probe")
        print(json.dumps(r, ensure_ascii=False, indent=1))
        return 0 if r.get("final_enemy_hp") is not None else 1

    t0 = time.time()
    seeds = [f"SOULS{i:02d}" for i in range(1, args.seeds + 1)]
    ctl_seeds = seeds[: args.control_seeds]
    all_rows = []

    def batch(jobs):
        """N 路并行（每个进程内部再 dop=2）。"""
        with ThreadPoolExecutor(max_workers=JOBS) as pool:
            return list(pool.map(lambda a: run_one(*a), jobs))

    print(f"=== SoulNexusElite（{ENCOUNTER}）× 中位牌 {args.median} × {len(seeds)} 种子 "
          f"【N{JOBS} × dop{DOP}】===")
    rows = batch([(args.median, ENCOUNTER, s, f"{args.median}-soul-{s}") for s in seeds])
    all_rows += rows
    soul = describe(rows, f"{args.median}@SoulNexus")

    print(f"\n=== 对照 AEONGLASS_BOSS × {args.median} × {len(ctl_seeds)} 种子 ===")
    rows = batch([(args.median, CONTROL, s, f"{args.median}-aeon-{s}") for s in ctl_seeds])
    all_rows += rows
    aeon = describe(rows, f"{args.median}@Aeonglass")

    print(f"\n=== 配对 ρ:最强 {args.strong} vs 最弱 {args.weak}，同一批 {len(seeds)} 个种子 ===")
    both = batch([(args.strong, ENCOUNTER, s, f"{args.strong}-soul-{s}") for s in seeds]
                 + [(args.weak, ENCOUNTER, s, f"{args.weak}-soul-{s}") for s in seeds])
    half = len(seeds)
    strong_rows, weak_rows = both[:half], both[half:]
    all_rows += strong_rows + weak_rows
    describe(strong_rows, f"{args.strong}@SoulNexus(最强)")
    describe(weak_rows, f"{args.weak}@SoulNexus(最弱)")

    paired = {}
    sa = [r["final_enemy_hp"] for r in strong_rows]
    sb = [r["final_enemy_hp"] for r in weak_rows]
    if all(v is not None for v in sa + sb) and len(sa) == len(sb) and len(sa) > 2:
        diff = [a - b for a, b in zip(sa, sb)]
        sd_a, sd_b, sd_d = statistics.stdev(sa), statistics.stdev(sb), statistics.stdev(diff)
        # sd(A−B)² = sd(A)² + sd(B)² − 2ρ·sd(A)·sd(B)  ⇒  ρ = (sdA²+sdB²−sdD²) / (2·sdA·sdB)
        rho = (sd_a ** 2 + sd_b ** 2 - sd_d ** 2) / (2 * sd_a * sd_b) if sd_a and sd_b else None
        paired = {"sd_strong": round(sd_a, 1), "sd_weak": round(sd_b, 1),
                  "sd_diff": round(sd_d, 1), "rho": round(rho, 3) if rho is not None else None,
                  "mean_diff": round(statistics.mean(diff), 1)}
        print(f"\n  配对:sd(强)={paired['sd_strong']} sd(弱)={paired['sd_weak']} "
              f"sd(差)={paired['sd_diff']} ⇒ **ρ = {paired['rho']}**  (均值差 {paired['mean_diff']})")

    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "sigma.json").write_text(json.dumps(
        {"soul": soul, "aeon": aeon, "paired": paired, "rows": all_rows},
        ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n写出 {OUT / 'sigma.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
