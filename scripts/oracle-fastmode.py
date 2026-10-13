#!/usr/bin/env python3
"""Does speeding up the game's animations change what the oracle measures?

`deploymentFastModeForTest` does not name a deployment strategy, which its name
suggests; it sets the game's own animation-speed preference
(UnattendedTestRunner.cs:2333-2337 maps Normal/Fast/Instant onto FastModeType), and
FastModeType shortens the waits the game takes between state transitions:

    CardPileCmd.cs:724-725   Instant 0.01s   Fast 0.2s   default 0.3s
    CardPileCmd.cs:938-939   Instant 0.01s   Fast 0.1s
    CombatManager.cs:1658    Fast 0.4s        default 0.8s

We have been running `Normal`, which is the slowest of the three. If a run spends
~155 seconds of wall clock but only ~22 seconds of CPU, the rest is waiting, and
this is where the waiting comes from.

The reason this needs a controlled comparison rather than a straight switch:
`Instant` makes `Cmd.Wait` a no-op, so the game never rests in an intermediate
state, and the solver's step-by-step replanning may simply not get a chance to
happen. That would change WHAT is measured, not just how fast. So each mode is
compared against the current one on the same deck and seed, and the readings have
to match — a speedup that moves `final_enemy_hp` is not a speedup.

`Fast` is tried first: animations shrink but the game still advances state by
state, so the replanning path is untouched.

Usage:
    python scripts/oracle-fastmode.py --cases 5 --modes Normal,Fast
"""
import argparse
import importlib.util
import io
import json
import os
import statistics
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "work", "oracle", "fastmode")
BOSSES = ["TEST_SUBJECT_BOSS", "QUEEN_BOSS", "AEONGLASS_BOSS"]


def load_v2():
    spec = importlib.util.spec_from_file_location(
        "v2", os.path.join(REPO, "scripts", "oracle-factorial-v2.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_mode(v2, base, colours, mode, cases):
    """Same decks and seeds under one animation mode."""
    rows = []
    for index in range(cases):
        boss = BOSSES[index % len(BOSSES)]
        seed = f"FAST-{index}"
        deck = v2.build_deck(base, 0, 0)
        spec = v2.spec_for(deck, boss, seed, colours)
        case = os.path.join(OUT, mode, seed, boss)
        os.makedirs(case, exist_ok=True)
        with io.open(os.path.join(case, "input.json"), "w", encoding="utf-8") as handle:
            json.dump(spec, handle, indent=2)
        began = time.time()
        outcome = v2.run_case(spec, case, f"{mode}-{seed}-{boss}", 900, 1000,
                              fast_mode=mode)
        wall = time.time() - began
        row = {"mode": mode, "boss": boss, "seed": seed, "wall_seconds": round(wall, 1),
               **outcome}
        rows.append(row)
        with io.open(os.path.join(OUT, "raw.jsonl"), "a", encoding="utf-8") as handle:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        print(f"  {mode:8} {boss:18} {seed:8} {outcome['status']:8} "
              f"hp={outcome['final_enemy_hp']} exp={outcome['expanded']} "
              f"turns={outcome['turns']} wall={wall:.1f}s", flush=True)
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=int, default=5)
    parser.add_argument("--modes", default="Normal,Fast")
    args = parser.parse_args()
    modes = [m.strip() for m in args.modes.split(",")]
    os.makedirs(OUT, exist_ok=True)
    v2 = load_v2()
    base = v2.load_base()
    colours = v2.load_colours()

    results = {}
    for mode in modes:
        print(f"\n=== 动画模式 {mode} ===", flush=True)
        results[mode] = run_mode(v2, base, colours, mode, args.cases)

    print("\n=== 对照(同牌同种子,逐场)===")
    header = "  ".join(f"{m:>22}" for m in modes)
    print(f"{'case':22} {header}")
    for index in range(args.cases):
        boss = BOSSES[index % len(BOSSES)]
        seed = f"FAST-{index}"
        cells = []
        for mode in modes:
            row = next((r for r in results[mode] if r["seed"] == seed), None)
            cells.append("—" if row is None else
                         f"hp={row['final_enemy_hp']} exp={row['expanded']}")
        print(f"{boss[:18]:22} " + "  ".join(f"{c:>22}" for c in cells))

    print("\n=== 汇总 ===")
    baseline = modes[0]
    for mode in modes:
        rows = results[mode]
        walls = [r["wall_seconds"] for r in rows]
        exps = [r["expanded"] for r in rows if r["expanded"] is not None]
        print(f"{mode:8} 墙钟中位 {statistics.median(walls):6.1f}s  "
              f"expanded 中位 {statistics.median(exps) if exps else '—'}  "
              f"提速 {statistics.median(results[baseline][i]['wall_seconds'] for i in range(len(rows))) / statistics.median(walls):.2f}x")

    # Acceptance: does the reading survive the change?
    print("\n=== 验收:读数是否逐位相同 ===")
    for mode in modes[1:]:
        same_hp = same_exp = 0
        for index in range(args.cases):
            seed = f"FAST-{index}"
            a = next(r for r in results[baseline] if r["seed"] == seed)
            b = next(r for r in results[mode] if r["seed"] == seed)
            same_hp += a["final_enemy_hp"] == b["final_enemy_hp"]
            same_exp += a["expanded"] == b["expanded"]
        print(f"  {baseline} vs {mode}: final_enemy_hp 相同 {same_hp}/{args.cases}, "
              f"expanded 相同 {same_exp}/{args.cases}")


if __name__ == "__main__":
    main()
