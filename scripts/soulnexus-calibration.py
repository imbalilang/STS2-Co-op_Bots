#!/usr/bin/env python3
"""SoulNexus 血量/伤害标定(派单65 + 用户调整版)。

用户的 4 组对照 + 我加的一条基准(1.0×血 1.0×输出)——
没有基准,就分不清"只加伤"与"只加血"各自的贡献。

    (1.0, 1.0) 基准     (1.0, 1.5) 只加伤
    (1.5, 1.0) 只加血   (1.5, 1.5) 都加
    (2.0, 1.0) 更硬的血

三条判据(派单65 §2):通关率 20–80% / 平均回合 ≥7 / σ 最低。
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

OUT = ROOT / "work" / "oracle" / "soulnexus-cal"
HP_RE = re.compile(r"([A-Z_]+)#\d+:(\d+)/(\d+)")

GROUPS = [(1.0, 1.0), (1.0, 1.5), (1.5, 1.0), (1.5, 1.5), (2.0, 1.0)]


def run_group(deck, hp, dmg, seeds, jobs):
    env = {"OFFLINE_HARNESS_SOULNEXUS_HP_SCALE": str(hp),
           "OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE": str(dmg)}

    def one(seed):
        tag = f"hp{hp}x-dmg{dmg}x-{seed}"
        r = sigma.run_one(deck, sigma.ENCOUNTER, seed, tag, extra_env=env)
        r["hp_scale"], r["dmg_scale"] = hp, dmg
        # 实际敌方血量(验证缩放生效)
        path = sigma.OUT / tag / "harness-result.json"   # run_one 写进 sigma 自己的 OUT
        if path.exists():
            payload = json.loads(path.read_text(encoding="utf-8"))
            first = (payload.get("steps") or [{}])[0]
            diag = payload.get("rootDiagnostics") or ""
            m = HP_RE.search(str(payload.get("root") or ""))
            r["max_enemy_hp"] = int(m.group(3)) if m else None
        return r

    with ThreadPoolExecutor(max_workers=jobs) as pool:
        return list(pool.map(one, seeds))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--deck", default="D9")
    ap.add_argument("--seeds", type=int, default=12)
    ap.add_argument("--jobs", type=int, default=sigma.JOBS)
    ap.add_argument("--probe", action="store_true")
    args = ap.parse_args()

    seeds = [f"SOULS{i:02d}" for i in range(1, args.seeds + 1)]

    if args.probe:
        for hp in (1.0, 2.0):
            r = run_group(args.deck, hp, 1.0, seeds[:1], 1)[0]
            print(f"  hp×{hp}: 敌方最大血={r.get('max_enemy_hp')} "
                  f"通关={r.get('won')} 回合={r.get('turns')} err={str(r.get('error'))[:60]}")
        return 0

    rows, summary = [], []
    t0 = time.time()
    print(f"牌 {args.deck} × {len(GROUPS)} 组 × {args.seeds} 种子 【N{args.jobs} × dop{sigma.DOP}】\n")
    for hp, dmg in GROUPS:
        g0 = time.time()
        got = run_group(args.deck, hp, dmg, seeds, args.jobs)
        rows += got
        wall = time.time() - g0
        ok = [r for r in got if r.get("final_enemy_hp") is not None]
        hpv = [r["final_enemy_hp"] for r in ok]
        turns = [r["turns"] for r in ok if r.get("turns")]
        entry = {
            "hp_scale": hp, "dmg_scale": dmg, "n": len(ok), "failed": len(got) - len(ok),
            "sd": round(statistics.stdev(hpv), 1) if len(hpv) > 1 else None,
            "mean": round(statistics.mean(hpv), 1) if hpv else None,
            "span": (max(hpv) - min(hpv)) if hpv else None,
            "clear_rate": round(sum(1 for r in ok if r["won"]) / len(ok), 3) if ok else None,
            "mean_turns": round(statistics.mean(turns), 1) if turns else None,
            "turn_span": f"{min(turns)}–{max(turns)}" if turns else None,
            "max_enemy_hp": ok[0].get("max_enemy_hp") if ok else None,
            "wall_s": round(wall, 1),
            "s_per_run": round(wall / len(got), 1) if got else None,
        }
        summary.append(entry)
        print(f"  血×{hp} 伤×{dmg} (敌血 {entry['max_enemy_hp']}): n={entry['n']} "
              f"σ={entry['sd']} 均值={entry['mean']} 跨度={entry['span']} "
              f"通关率={entry['clear_rate']} 回合={entry['mean_turns']}({entry['turn_span']}) "
              f"｜ {entry['wall_s']}s ({entry['s_per_run']}s/场)")

    print(f"\n总墙钟 {time.time() - t0:.0f}s（{len(rows)} 场）")
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "calibration.json").write_text(
        json.dumps({"summary": summary, "rows": rows}, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"写出 {OUT / 'calibration.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
