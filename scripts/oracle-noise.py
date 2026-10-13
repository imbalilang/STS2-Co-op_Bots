#!/usr/bin/env python3
"""How large is the oracle's own repeat noise?

Every acceptance line in the deck-delta plan is stated as a threshold — symbol
accuracy >= 90%, top-3 >= 80%, regret within 10-15% of the best candidate's gain —
and none of them can be judged without knowing how much the instrument wobbles on
its own. There is already an indirect hint: in factorial v1 the residual sd was
5.2 / 8.3 / 19.5 against outcome spans of 17 / 30 / 66.

Three groups, and the third is the one the conclusion rests on:

  A. same deck, same seed, 5 repeats      -> pure repeat noise (should be zero)
  B. same deck, 5 different seeds         -> shuffle variance
  C. same deck plus or minus ONE card     -> the single-card delta

The question the plan needs answered is whether C is comfortably larger than A and
B. If they are the same size, finite differences cannot be measured at all and the
plan's whole data-collection phase is wasted.

This must not be run while another oracle instance is alive: two instances were
measured to change 5 of 6 readings and cut expanded nodes by a quarter.

Usage:
    python scripts/oracle-noise.py --repeats 5 --seeds 5
"""
import argparse
import importlib.util
import io
import json
import os
import statistics

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "work", "oracle", "noise")



def load_v2():
    spec = importlib.util.spec_from_file_location(
        "v2", os.path.join(REPO, "scripts", "oracle-factorial-v2.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    global BOSSES
    parser = argparse.ArgumentParser()
    parser.add_argument("--repeats", type=int, default=5)
    parser.add_argument("--seeds", type=int, default=5)
    parser.add_argument("--budget-ms", type=int, default=1000)
    parser.add_argument("--timeout-seconds", type=int, default=900)
    parser.add_argument("--bosses", default="TEST_SUBJECT_BOSS,QUEEN_BOSS")
    args = parser.parse_args()
    BOSSES = [b.strip() for b in args.bosses.split(",")]

    v2 = load_v2()
    base = v2.load_base()
    colours = v2.load_colours()
    os.makedirs(OUT, exist_ok=True)

    # The base deck with no package cards, and the same deck with one Accuracy added
    # by swapping a Defend. One card, everything else identical — that difference is
    # the single-card delta the plan is built on.
    control = v2.build_deck(base, 0, 0)
    plus_one = v2.build_deck(base, 0, 1)

    def run(deck, boss, seed, tag):
        spec = v2.spec_for(deck, boss, seed, colours)
        case = os.path.join(OUT, tag, boss)
        os.makedirs(case, exist_ok=True)
        with io.open(os.path.join(case, "input.json"), "w", encoding="utf-8") as handle:
            json.dump(spec, handle, indent=2)
        outcome = v2.run_case(spec, case, tag, args.timeout_seconds, args.budget_ms,
                              fast_mode="Fast")
        row = {"group": tag.split("-")[0], "tag": tag, "boss": boss, "seed": seed,
               "budget_ms": args.budget_ms, "parallelism": 1, **outcome}
        with io.open(os.path.join(OUT, "raw.jsonl"), "a", encoding="utf-8") as handle:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        print(f"  {tag:22} {boss:18} seed={seed:16} hp={outcome['final_enemy_hp']} "
              f"exp={outcome['expanded']} turns={outcome['turns']}", flush=True)
        return outcome

    for boss in BOSSES:
        # A: identical input, repeated. Anything but zero here is pure instrument noise.
        for repeat in range(args.repeats):
            run(control, boss, "NOISE-A", f"A-repeat-{repeat}")
        # B: same deck, different shuffles.
        for index in range(args.seeds):
            run(control, boss, f"NOISE-B{index}", f"B-seed-{index}")
        # C: one card different, same seed as B0 so the comparison is like for like.
        run(plus_one, boss, "NOISE-B0", "C-plusone")

    # Summary
    rows = [json.loads(line) for line in io.open(os.path.join(OUT, "raw.jsonl"), encoding="utf-8")]

    def values(group, boss):
        return [r["final_enemy_hp"] for r in rows
                if r["group"] == group and r["boss"] == boss and r["final_enemy_hp"] is not None]

    print("\n=== 噪声底 ===")
    for boss in BOSSES:
        a, b = values("A", boss), values("B", boss)
        c = [r["final_enemy_hp"] for r in rows if r["tag"] == "C-plusone" and r["boss"] == boss]
        control_hp = b[0] if b else None
        print(f"\n{boss}")
        print(f"  A 重复({len(a)}): {a}  sd={statistics.stdev(a) if len(a)>1 else 0:.2f}")
        print(f"  B 种子({len(b)}): {b}  sd={statistics.stdev(b) if len(b)>1 else 0:.2f}")
        if c and control_hp is not None:
            print(f"  C +1张  : {c}  | 单卡 delta = {c[0] - control_hp:+d} (同一 seed 对照 {control_hp})")


if __name__ == "__main__":
    main()
