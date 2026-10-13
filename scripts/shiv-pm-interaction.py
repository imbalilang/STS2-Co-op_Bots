#!/usr/bin/env python3
"""P×M 交互(派单66):加一张「乘数器」的边际价值,是否取决于牌组里已有多少「生成器」?

  P 生成器 BLADE_DANCE / INFINITE_BLADES / CLOAK_AND_DAGGER   ∈ {0, 3}
  M 乘数器 ACCURACY                                            ∈ {0, 1}

底座:**社区缓存 9f0f93ff71** —— SILENT、A10 通关、单人、无小刀包件、无毒。
(批里的 D6 也是"通关+无小刀",但裸测下结构上打不动:0/4 通关、余敌血 183、4.8 回合。
 那批通关牌都是带 14–28 件遗物赢的,裸测把遗物剥了之后大部分打不动这个精英。)

张数恒定:每加一张包件牌就从【同一个普通牌层】拿掉一张。
9f0f93ff71 的普通牌层是 DEFEND_SILENT×5(没有 STRIKE)。

调度:**整张任务表一个 pool**。实测:同一个 pool.map 内部是队列(槽位复用),
但两次 pool.map 之间是硬栅栏(8×5s + 8×10s = 15.0s,不是 10s)。
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
import importlib.util

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("sigma", ROOT / "scripts" / "soulnexus-sigma.py")
sigma = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sigma)

OUT = ROOT / "work" / "oracle" / "shiv-pm"
CARD_META = ROOT / "work" / "spire-codex" / "cards" / "cards.json"
COMMUNITY = ROOT / "work" / "spire-codex" / "decks" / "runs"
BASE_HASH = "9f0f93ff71"

GENERATORS = ["BLADE_DANCE", "INFINITE_BLADES", "CLOAK_AND_DAGGER"]
MULTIPLIER = "ACCURACY"
REMOVAL_POOL = ["DEFEND_SILENT"] * 4

PREREGISTRATION = {
    "乘法成立": "(P3M1 − P3M0) 明显大于 (P0M1 − P0M0)",
    "加法成立": "两个差值大致相等",
    "符号相反": "M 在 P 高时反而更不值钱",
}
WORKING_POINTS = [(1.0, 1.0)]      # 社区底座在【原始 SoulNexus】上就有动态范围

HP_RE = re.compile(r"hp=(\d+)/(\d+)")
TURN_RE = re.compile(r"turn=(\d+)")
ENEMY_RE = re.compile(r":(\d+)/\d+")


def card_colors() -> dict:
    data = json.loads(CARD_META.read_text(encoding="utf-8"))
    return {c["id"]: str(c.get("color") or "").lower() for c in data}


def base_cards() -> list:
    """底座牌表 [(id, upgrade)],不含 ASCENDERS_BANE(那由开关供给)。"""
    for path in COMMUNITY.glob("*.json"):
        d = json.loads(path.read_text(encoding="utf-8"))
        if d.get("run_hash", "").startswith(BASE_HASH):
            return [(c["id"], c.get("upgrades", 0)) for c in d["deck"] if c["id"] != "ASCENDERS_BANE"]
    raise SystemExit(f"社区缓存里找不到 {BASE_HASH}")


def build_input(p: int, m: int, out_dir: Path, seed: str, encounter: str) -> Path:
    """按格子构造牌组:加 P/M 张包件牌,同时从固定移除池里拿掉同样多张 ⇒ 张数恒定。"""
    cards = base_cards()
    ids = [c[0] for c in cards]
    levels = [c[1] for c in cards]
    original = len(ids)

    added = list(GENERATORS[:p]) + ([MULTIPLIER] if m else [])
    for card in REMOVAL_POOL[:len(added)]:
        index = ids.index(card)          # 从前往后拿,可复算
        ids.pop(index)
        levels.pop(index)
    for card in added:
        ids.append(card)
        levels.append(0)

    assert len(ids) == original, f"角色牌张数必须恒定 {original},实际 {len(ids)}"

    colors = card_colors()
    mine = [(i, l) for i, l in zip(ids, levels) if colors.get(i) == "silent"]
    others = [(i, l) for i, l in zip(ids, levels) if colors.get(i) != "silent"]

    options = {
        "schemaVersion": 1, "seed": seed, "characterId": "SILENT", "ascension": 10, "actIndex": 2,
        "encounterId": encounter, "includeStartingDeck": False, "includeStartingRelics": False,
        "includeAscendersBane": True,
        "characterCards": {"count": len(mine), "ids": [c[0] for c in mine],
                           "upgradeLevelsPerCard": [c[1] for c in mine]},
        "colorlessCards": {"count": len(others), "ids": [c[0] for c in others],
                           "upgradeLevelsPerCard": [c[1] for c in others]},
        "relics": {"count": 0}, "potions": {"count": 0},
        "fixedSearchBudget": False, "mode": "Deploy",
    }
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / "input.json"
    path.write_text(json.dumps(options, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def run_one(p: int, m: int, hp: float, dmg: float, seed: str, budget_ms: int = 120_000) -> dict:
    tag = f"b{budget_ms}-hp{hp}-dmg{dmg}-P{p}M{m}-{seed}"
    out_dir = OUT / tag
    scenario = build_input(p, m, out_dir, seed, sigma.ENCOUNTER)

    src = json.loads((ROOT / "work" / "oracle" / "batch" / "D6-AEONGLASS_BOSS" / "request.json")
                     .read_text(encoding="utf-8"))
    for key in ("runCards", "cards", "relics", "combatRelics", "potions", "powers", "modifierIds"):
        src[key] = []
    for key in ("checkpointArchivePath", "runSnapshotPath", "replayStatePath",
                "nativeStatePath", "replayPolicyOverridePath"):
        src[key] = None
    src.update({"holdAfterInitialSearch": False, "generatedScenarioPath": str(scenario),
                "seed": seed, "characterId": "SILENT", "ascension": 10,
                "targetRoomType": "Elite", "targetMapPointType": "Elite",
                "scenarioId": tag, "evidenceDirectory": str(out_dir / "evidence")})
    request = out_dir / "outer-request.json"
    request.write_text(json.dumps(src, ensure_ascii=False, indent=1), encoding="utf-8")

    started = time.time()
    proc = subprocess.run(
        [str(sigma.DOTNET), str(sigma.HARNESS), "--request", str(request),
         "--profile", "Medium", "--nodes", "6000", "--budget-ms", str(budget_ms),
         "--search-timeout-ms", "60000", "--dop", str(sigma.DOP),
         "--no-card-probe", "--deploy-plan", "--play-path", "gameaction",
         "--max-turns", "40", "--milestone", "M2", "--label", tag, "--out", str(out_dir)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        cwd=str(ROOT / "third_party" / "CombatSolver-upstream-main"), timeout=600,
        env={**os.environ, "OFFLINE_HARNESS_SOULNEXUS_HP_SCALE": str(hp),
             "OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE": str(dmg)})

    row = {"p": p, "m": m, "hp": hp, "dmg": dmg, "seed": seed, "tag": tag,
           "exit": proc.returncode, "wall_s": round(time.time() - started, 1)}
    path = out_dir / "harness-result.json"
    if not path.exists():
        row["error"] = "没有产物"
        return row
    payload = json.loads(path.read_text(encoding="utf-8"))
    after = payload.get("afterTurns") or ""
    h = HP_RE.search(after)
    t = TURN_RE.search(after)
    won = "enemies=[]" in after
    row |= {"error": (payload.get("error") or {}).get("message"), "won": won,
            "final_enemy_hp": 0 if won else (
                sum(int(x) for x in ENEMY_RE.findall(after.split("enemies=")[-1]))
                if "enemies=" in after else None),
            "turns": int(t.group(1)) if t else None,
            "hplost": (int(h.group(2)) - int(h.group(1))) if h else None}
    return row


def summarize(rows: list, label: str) -> dict:
    ok = [r for r in rows if r.get("final_enemy_hp") is not None]
    v = [r["final_enemy_hp"] for r in ok]
    turns = [r["turns"] for r in ok if r.get("turns")]
    entry = {
        "label": label, "n": len(ok), "failed": len(rows) - len(ok),
        "mean": round(statistics.mean(v), 1) if v else None,
        "sd": round(statistics.stdev(v), 1) if len(v) > 1 else None,
        "clear": round(sum(1 for r in ok if r["won"]) / len(ok), 3) if ok else None,
        "turns": round(statistics.mean(turns), 1) if turns else None,
        "hplost_mean": round(statistics.mean([r["hplost"] for r in ok if r.get("hplost") is not None]), 1)
                        if any(r.get("hplost") is not None for r in ok) else None,
    }
    print(f"  {label:8s} n={entry['n']:3d} 均值={str(entry['mean']):>7s} σ={str(entry['sd']):>6s} "
          f"通关率={entry['clear']} 回合={entry['turns']} 掉血={entry['hplost_mean']}")
    return entry


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=50)
    ap.add_argument("--jobs", type=int, default=sigma.JOBS)
    ap.add_argument("--points", default="1.0x1.0")
    ap.add_argument("--budget", type=int, default=120_000,
                    help="求解器软预算(ms)。压它=计划降级但仗照打到底;这是成本杠杆,不是 --max-fight-ms")
    args = ap.parse_args()

    seeds = [f"PM{i:03d}" for i in range(1, args.seeds + 1)]
    points = []
    for item in args.points.split(","):
        hp, dmg = item.split("x")
        points.append((float(hp), float(dmg)))

    print("=== 预注册(看数据之前写下的) ===")
    for k, v in PREREGISTRATION.items():
        print(f"  {k}: {v}")
    print(f"\n底座 {BASE_HASH}({len(base_cards()) + 1} 张,张数恒定);"
          f"每格 {len(seeds)} 种子;N{args.jobs}×dop{sigma.DOP}")
    print(f"工作点 {points}\n")

    report = {"preregistration": PREREGISTRATION, "base": BASE_HASH, "budget_ms": args.budget, "points": {}}
    t0 = time.time()
    for hp, dmg in points:
        key = f"hp{hp}_dmg{dmg}"
        # 整张表一个 pool:四个格子一起喂,槽位跑完立刻接下一条,不等整格结束
        tasks = [(p, m, s) for p in (0, 3) for m in (0, 1) for s in seeds]
        with ThreadPoolExecutor(max_workers=args.jobs) as pool:
            rows = list(pool.map(lambda a: run_one(a[0], a[1], hp, dmg, a[2], args.budget), tasks))

        cells = {}
        for p in (0, 3):
            for m in (0, 1):
                got = [r for r in rows if r["p"] == p and r["m"] == m]
                cells[f"P{p}M{m}"] = summarize(got, f"P{p}M{m}")
                cells[f"P{p}M{m}"]["rows"] = got

        a = cells["P3M1"]["mean"] - cells["P3M0"]["mean"]
        b = cells["P0M1"]["mean"] - cells["P0M0"]["mean"]
        n = min(cells[k]["n"] for k in cells)
        sd = statistics.mean([cells[k]["sd"] for k in cells if cells[k]["sd"] is not None])
        se = 2 * sd / (n ** 0.5)          # 交互项 = 差之差 ⇒ SE = 2σ/√n
        interact = a - b
        print(f"  M 在 P3 的边际 Δ(P3M1−P3M0) = {a:+.1f}")
        print(f"  M 在 P0 的边际 Δ(P0M1−P0M0) = {b:+.1f}")
        print(f"  **交互项 = {interact:+.1f}  SE≈{se:.1f}  95% CI ≈ "
              f"[{interact - 1.96 * se:+.1f}, {interact + 1.96 * se:+.1f}]**")
        print(f"     判定: {'不跨零 ⇒ 有交互' if abs(interact) > 1.96 * se else '⚠ 跨零 ⇒ 本精度下不判定'}\n")
        report["points"][key] = {"cells": cells, "delta_P3": a, "delta_P0": b,
                                 "interaction": interact, "se": se, "n_per_cell": n, "sd_avg": sd}

    print(f"总墙钟 {time.time() - t0:.0f}s（{len(points) * 4 * len(seeds)} 场）")
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "pm.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"写出 {OUT / 'pm.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
