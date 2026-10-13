#!/usr/bin/env python3
"""Align the model's per-deck feature table against an independent recomputation.

Dispatch 9. The two AUC tables disagreed on role-dependent features (packages
0.58 vs 0.45, dead cards 0.44 vs 0.59) and the open question was whether that is
a difference of *implementation* or of *sample*. The model exported one row per
deck, so it can now be settled by deck rather than argued by aggregate.

The comparison is staged deliberately, easiest first:

  1. sample alignment — the shared run_hash set
  2. pure counts (upgrades, deck size, curses) — no role tagging involved, so any
     disagreement there is a sample or bookkeeping difference, not a tagging one
  3. role-dependent features — routes, packages>=2, dead cards, convergence
  4. a paired bootstrap of the AUC difference, which is the only way to say
     whether 0.58 vs 0.45 is real or two labels on one sample of 231

Roles are re-derived here by importing the tester's own derivation, not the
mod's, so the two sides stay independent.
"""
import argparse
import collections
import csv
import glob
import importlib.machinery
import importlib.util
import io
import json
import os
import random

HERE = os.path.dirname(os.path.abspath(__file__))


def load_tester_module():
    """Import analyze-feature-auc.py for its Detector, roles and auc."""
    path = os.path.join(HERE, "analyze-feature-auc.py")
    loader = importlib.machinery.SourceFileLoader("tester_auc", path)
    spec = importlib.util.spec_from_loader("tester_auc", loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


def auc(wins, losses):
    if not wins or not losses:
        return None
    greater = ties = 0
    for w in wins:
        for l in losses:
            if w > l:
                greater += 1
            elif abs(w - l) < 1e-9:
                ties += 1
    return (greater + ties / 2) / (len(wins) * len(losses))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model-csv", default="outputs/decksim-analysis/deck-features-4p.csv")
    ap.add_argument("--iterations", type=int, default=2000)
    args = ap.parse_args()

    T = load_tester_module()
    cards = T.load_cards()
    clusters = T.load_clusters()
    det = T.Detector(cards, clusters)

    model = {r["run_hash"]: r for r in csv.DictReader(io.open(args.model_csv, encoding="utf-8"))}
    mine = {}
    for path in glob.glob("work/spire-codex/decks/runs/*.json"):
        d = json.load(io.open(path, encoding="utf-8"))
        if d.get("run_hash"):
            mine[d["run_hash"]] = d

    shared = sorted(set(model) & set(mine))
    print("=" * 78)
    print("1. sample alignment")
    print("=" * 78)
    print(f"  model rows                : {len(model)}")
    print(f"  my cache                  : {len(mine)} runs"
          f" ({sum(1 for d in mine.values() if d.get('picks'))} with picks)")
    print(f"  SHARED run_hash           : {len(shared)}")
    print(f"  model-only                : {len(set(model) - set(mine))}")
    print(f"  mine-only (4-player)      : "
          f"{len([h for h in mine if mine[h].get('players') == 4]) - len(shared)}")
    bad = [h for h in shared if str(1 if mine[h].get("win") else 0) != model[h]["win"]]
    print(f"  win-label mismatches      : {len(bad)}")
    if len(shared) < len(model):
        print("  -> the samples differ; every number below is on the shared subset only")
    else:
        print("  -> identical samples. Any divergence below is implementation, not sample.")

    # ---- 2. pure counts -----------------------------------------------------
    print()
    print("=" * 78)
    print("2. pure counts (no role tagging involved)")
    print("=" * 78)
    pure = {
        "upgrades": lambda d: sum(1 for e in d["deck"] if e.get("upgrades", 0) > 0),
        "deck_size": lambda d: len(d["deck"]),
    }
    for name, fn in pure.items():
        col = {"upgrades": "upgrades", "deck_size": "deck_size"}[name]
        diffs = [abs(fn(mine[h]) - float(model[h][col])) for h in shared]
        exact = sum(1 for d in diffs if d == 0)
        print(f"  {name:28} exact match {exact}/{len(shared)}"
              f"   mean |diff| {sum(diffs)/len(diffs):.3f}")

    # ---- 3. role-dependent --------------------------------------------------
    print()
    print("=" * 78)
    print("3. role-dependent features, per deck")
    print("=" * 78)
    feat = {}
    for h in shared:
        ids = [e["id"] for e in mine[h]["deck"]]
        routes = det.detect(ids, mine[h]["character"], mine[h].get("relics") or [])
        conv = float("inf")
        picks = mine[h].get("picks") or []
        built = []
        for i, cid in enumerate(picks):
            built.append(cid)
            if det.detect(built, mine[h]["character"], []):
                conv = i + 1
                break
        feat[h] = {
            "packages__routes_": float(len(routes)),
            "packages___2": 1.0 if len(routes) >= 2 else 0.0,
            "dead_cards": det.dead_share(ids, routes),
            "convergence__pick_no__": conv,
        }

    for col in ("packages__routes_", "packages___2", "dead_cards"):
        agree = sum(1 for h in shared if abs(feat[h][col] - float(model[h][col])) < 1e-6)
        mae = sum(abs(feat[h][col] - float(model[h][col])) for h in shared) / len(shared)
        bias = sum(feat[h][col] - float(model[h][col]) for h in shared) / len(shared)
        print(f"  {col:26} exact {agree:>3}/{len(shared)}"
              f"   mean |diff| {mae:6.3f}   my mean {bias:+.3f} vs model")

    conv_model = [float(model[h]["convergence__pick_no__"]) for h in shared]
    conv_mine = [feat[h]["convergence__pick_no__"] for h in shared]
    finite_model = [v for v in conv_model if v < 1e6]
    finite_mine = [v for v in conv_mine if v != float("inf")]
    print(f"  {'convergence':26} model finite {len(finite_model)}/{len(shared)}"
          f"   mine finite {len(finite_mine)}/{len(shared)}"
          f"   (model sentinel looks like a large finite number)")

    # ---- 4. paired bootstrap on the AUC difference ---------------------------
    print()
    print("=" * 78)
    print("4. paired bootstrap: is the AUC gap real, or two labels on one sample?")
    print("=" * 78)
    print(f"  resampling the same {len(shared)} decks for both implementations")
    rng = random.Random(20260916)
    n_w = sum(1 for h in shared if mine[h].get("win"))
    n_l = len(shared) - n_w
    print(f"  {len(shared)} decks = {n_w} wins / {n_l} losses")
    print()
    print(f"  {'feature':26}{'mine':>7}{'model':>7}{'diff':>8}{'95% CI of diff':>22}{'p(diff=0)':>11}")
    for col in ("packages__routes_", "packages___2", "dead_cards"):
        mv = [feat[h][col] for h in shared]
        gv = [float(model[h][col]) for h in shared]
        win = [i for i, h in enumerate(shared) if mine[h].get("win")]
        loss = [i for i, h in enumerate(shared) if not mine[h].get("win")]
        a_mine = auc([mv[i] for i in win], [mv[i] for i in loss])
        a_model = auc([gv[i] for i in win], [gv[i] for i in loss])
        diffs = []
        for _ in range(args.iterations):
            ws = [rng.choice(win) for _ in win]
            ls = [rng.choice(loss) for _ in loss]
            d = auc([mv[i] for i in ws], [mv[i] for i in ls]) - auc([gv[i] for i in ws], [gv[i] for i in ls])
            diffs.append(d)
        diffs.sort()
        lo, hi = diffs[int(0.025 * len(diffs))], diffs[int(0.975 * len(diffs)) - 1]
        crosses = sum(1 for d in diffs if d <= 0) / len(diffs)
        p = 2 * min(crosses, 1 - crosses)
        print(f"  {col:26}{a_mine:7.2f}{a_model:7.2f}{a_mine-a_model:+8.2f}"
              f"{f'{lo:+.2f} to {hi:+.2f}':>22}{p:11.3f}")
    print()
    print("  'p(diff=0)' is the two-sided bootstrap share of resamples whose difference")
    print("  changes sign. A CI spanning zero means the two implementations are not")
    print("  distinguishable on this sample, whatever the point estimates say.")


if __name__ == "__main__":
    main()
