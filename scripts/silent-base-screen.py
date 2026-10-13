#!/usr/bin/env python3
"""猎手底座筛选:在【裸测】下能不能打得动 SoulNexus。

背景:D6 是因为"通关 + 无小刀"被选中的,但它裸测下结构上打不动(15 伤害/回合 vs 需要 60+)。
**通关牌组 ≠ 裸测能打** —— 那批通关牌都带着十几到二十几件遗物。
所以候选池换成社区缓存(832 副,含 relics 字段),筛 "SILENT + 通关 + 单人 + 无小刀",再【实测生存性】。

调度:**整张任务表一个 pool**(不是每组一个 pool)——
实测过:同一个 pool.map 内部是队列(槽位复用),但两次 pool.map 之间是硬栅栏。
"""

import argparse
import json
import re
import statistics
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import importlib.util

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("sigma", ROOT / "scripts" / "soulnexus-sigma.py")
sigma = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sigma)

CACHE = ROOT / "work" / "spire-codex" / "decks" / "runs"
CARD_META = ROOT / "work" / "spire-codex" / "cards" / "cards.json"


def card_colors() -> dict:
    """牌 ID → color（silent / colorless / ironclad / …）。角色池只放 silent。"""
    data = json.loads(CARD_META.read_text(encoding="utf-8"))
    return {c["id"]: str(c.get("color") or "").lower() for c in data}
OUT = ROOT / "work" / "oracle" / "silent-screen"
SHIV = {"BLADE_DANCE", "INFINITE_BLADES", "CLOAK_AND_DAGGER", "ACCURACY",
        "HIDDEN_DAGGERS", "FINISHER", "STORM_OF_STEEL", "ENVENOM", "KNIFE_TRAP"}
HP_RE = re.compile(r"hp=(\d+)/(\d+)")
TURN_RE = re.compile(r"turn=(\d+)")


def candidates() -> list:
    out = []
    for path in sorted(CACHE.glob("*.json")):
        d = json.loads(path.read_text(encoding="utf-8"))
        if d.get("character") != "SILENT" or not d.get("win") or d.get("players") != 1:
            continue
        if d.get("ascension") != 10:
            continue
        ids = [c["id"] for c in d["deck"]]
        if set(ids) & SHIV:
            continue
        out.append({"hash": d["run_hash"], "n": len(ids),
                    "cards": [(c["id"], c.get("upgrades", 0)) for c in d["deck"]]})
    return out


def build_input(deck: dict, out_dir: Path, seed: str, encounter: str) -> Path:
    # ASCENDERS_BANE 不能放进 characterCards —— 它由 includeAscendersBane 开关供给，
    # 放进去会报 "ID必须唯一且属于允许池：ASCENDERS_BANE（匹配0项）"。
    cards = [c for c in deck["cards"] if c[0] != "ASCENDERS_BANE"]
    colors = card_colors()
    mine = [c for c in cards if colors.get(c[0]) == "silent"]
    others = [c for c in cards if colors.get(c[0]) != "silent"]
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


def run_one(deck: dict, seed: str, hp: float, dmg: float) -> dict:
    tag = f"{deck['hash'][:10]}-{seed}"
    out_dir = OUT / tag
    scenario = build_input(deck, out_dir, seed, sigma.ENCOUNTER)
    request = sigma.outer_request_from("D6", scenario, seed, out_dir) \
        if hasattr(sigma, "outer_request_from") else None
    if request is None:
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

    import subprocess, os
    t0 = time.time()
    proc = subprocess.run(
        [str(sigma.DOTNET), str(sigma.HARNESS), "--request", str(request),
         "--profile", "Medium", "--nodes", "6000", "--budget-ms", "120000",
         "--search-timeout-ms", "120000", "--dop", str(sigma.DOP),
         "--no-card-probe", "--deploy-plan", "--play-path", "gameaction",
         "--max-turns", "40", "--milestone", "M2", "--label", tag, "--out", str(out_dir)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        cwd=str(ROOT / "third_party" / "CombatSolver-upstream-main"), timeout=600,
        env={**os.environ, "OFFLINE_HARNESS_SOULNEXUS_HP_SCALE": str(hp),
             "OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE": str(dmg)})
    wall = round(time.time() - t0, 1)
    result = {"hash": deck["hash"][:10], "n": deck["n"], "seed": seed, "wall_s": wall,
              "exit": proc.returncode}
    path = out_dir / "harness-result.json"
    if not path.exists():
        result["error"] = "没有产物"
        return result
    payload = json.loads(path.read_text(encoding="utf-8"))
    after = payload.get("afterTurns") or ""
    h = HP_RE.search(after)
    t = TURN_RE.search(after)
    result |= {"error": (payload.get("error") or {}).get("message"),
               "won": "enemies=[]" in after,
               "enemy_left": None if "enemies=[]" in after else (
                   sum(int(x) for x in re.findall(r":(\d+)/\d+", after.split("enemies=")[-1]))
                   if "enemies=" in after else None),
               "turns": int(t.group(1)) if t else None,
               "hplost": (int(h.group(2)) - int(h.group(1))) if h else None}
    return result


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--hp", type=float, default=1.0)
    ap.add_argument("--dmg", type=float, default=1.0)
    ap.add_argument("--jobs", type=int, default=sigma.JOBS)
    args = ap.parse_args()

    decks = candidates()
    seeds = [f"SCR{i:02d}" for i in range(1, args.seeds + 1)]
    tasks = [(d, s) for d in decks for s in seeds]
    print(f"候选 {len(decks)} 副 × {len(seeds)} 种子 = {len(tasks)} 场 "
          f"【整张表一个 pool，N{args.jobs}×dop{sigma.DOP}】")
    print(f"工作点:血×{args.hp} 伤×{args.dmg}\n")

    t0 = time.time()
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:      # ← 单队列,不分批
        rows = list(pool.map(lambda a: run_one(a[0], a[1], args.hp, args.dmg), tasks))
    wall = time.time() - t0
    print(f"总墙钟 {wall:.1f}s（{len(tasks)} 场；若按每组一个 pool 会明显更久）\n")

    by = {}
    for r in rows:
        by.setdefault(r["hash"], []).append(r)
    print(f"{'牌组':12s} {'牌数':>4s} {'跑通':>4s} {'通关':>4s} {'余敌血均值':>10s} {'回合':>5s}")
    for h, group in sorted(by.items(), key=lambda kv: -sum(1 for r in kv[1] if r.get("won"))):
        ok = [r for r in group if r.get("error") is None]
        left = [r["enemy_left"] for r in ok if r.get("enemy_left") is not None]
        turns = [r["turns"] for r in ok if r.get("turns")]
        print(f"{h:12s} {group[0]['n']:4d} {len(ok):4d} "
              f"{sum(1 for r in ok if r.get('won')):4d} "
              f"{(round(statistics.mean(left),1) if left else '-'):>10} "
              f"{(round(statistics.mean(turns),1) if turns else '-'):>5}")
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "screen.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n写出 {OUT / 'screen.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
