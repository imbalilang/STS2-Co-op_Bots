#!/usr/bin/env python3
"""Verification skeletons for the hpCost oracle (dispatch 22 section 2).

The oracle does not exist yet, so nothing here can run. Writing it now is the
point: the three checks below are decided in advance, and the input contract is
fixed, so the oracle is built to be verifiable rather than verified afterwards.

Why each check exists, in the order they must be settled:

  direction      the ruler has to put decks that really won in front of decks
                 that really died deep, measured by HP cost rather than by a
                 feature. This is the only legitimate use left for the community
                 sample: qualitative validation of the instrument, not a
                 quantitative gate on a build change.
  reproducibility a ruler whose reading moves with the machine cannot rank
                 anything. Two channels were found in the vendored solver by
                 reading it -- a wall-clock deadline and a parallelism default
                 derived from Environment.ProcessorCount -- so both are tested
                 before any number is believed.
  structure      the tester's own reading of what a deck is (roles, packages)
                 against the oracle's ordering. If they disagree systematically
                 then either the algebra does not describe what sets combat cost,
                 or the ruler is wrong. Both readings are reportable.

Expected input: one CSV, one row per (deck, boss, seed, run), with these columns.
Ask decksim to emit exactly this rather than adapting later.

    run_hash      deck identity, joins to work/spire-codex/decks/runs/*.json
    character     IRONCLAD..REGENT
    win           the real run's outcome, for the direction check only
    killed_by     real death site, used to stratify "died deep"
    boss          which act-3 boss this oracle run fought
    seed          oracle RNG seed
    finalEnemyHp   boss HP remaining -- PRIMARY, 0 = cleared (lower is better)
    finishedTurn   turns survived -- SECONDARY tiebreak (higher is better)
    hp_cost        HP lost; only meaningful when died=false, degenerate otherwise
    died          whether the oracle lost the fight
    budget_ms     the wall-clock budget the oracle was given, if any
    parallelism   the degree of parallelism the oracle used
    turns         fight length

Usage (once the CSV exists):
    python scripts/verify-oracle.py --csv path/to/oracle.csv
    python scripts/verify-oracle.py --csv ... --budget-a 2000 --budget-b 4000
"""
import argparse
import collections
import csv
import glob
import io
import json
import os
import random
import statistics as st

DECKS = "work/spire-codex/decks/runs"
BOSSES = ["TEST_SUBJECT_BOSS", "QUEEN_BOSS", "AEONGLASS_BOSS"]
# finalEnemyHp is the primary reading: hp_cost degenerates when the party dies
# (HP hits zero, so it is a constant equal to max HP and carries no information).
# A boss left at 0 HP means the deck cleared the fight, so lower is better and
# the AUC is taken on the negation. finishedTurn breaks ties: between two decks
# that both fail, the one that lasted longer is closer.
REQUIRED = ["run_hash", "character", "boss", "seed", "finalEnemyHp"]


def load(path):
    if not os.path.exists(path):
        raise SystemExit(f"missing {path} -- the oracle has not produced data yet")
    rows = list(csv.DictReader(io.open(path, encoding="utf-8")))
    missing = [c for c in REQUIRED if rows and c not in rows[0]]
    if missing:
        raise SystemExit(f"{path} is missing required columns: {missing}")
    for r in rows:
        r["finalEnemyHp"] = float(r["finalEnemyHp"])
        if "finishedTurn" in r and r["finishedTurn"] not in (None, ""):
            r["finishedTurn"] = float(r["finishedTurn"])
        r["seed"] = str(r["seed"])
        if "win" in r:
            r["win"] = str(r["win"]) in ("1", "True", "true")
    return rows


def auc(wins, losses):
    if not wins or not losses:
        return None
    g = t = 0
    for a in wins:
        for b in losses:
            if a > b:
                g += 1
            elif abs(a - b) < 1e-9:
                t += 1
    return (g + t / 2) / (len(wins) * len(losses))


def perm_p(wins, losses, obs, iters=2000, seed=3):
    rng = random.Random(seed)
    pool = list(wins) + list(losses)
    n = len(wins)
    hits = 0
    for _ in range(iters):
        rng.shuffle(pool)
        a = auc(pool[:n], pool[n:])
        if a is not None and a >= obs:
            hits += 1
    return (hits + 1) / (iters + 1)


def boot_ci(wins, losses, iters=2000, seed=5):
    rng = random.Random(seed)
    out = []
    for _ in range(iters):
        w = [rng.choice(wins) for _ in wins]
        l = [rng.choice(losses) for _ in losses]
        a = auc(w, l)
        if a is not None:
            out.append(a)
    out.sort()
    return out[int(0.025 * len(out))], out[int(0.975 * len(out)) - 1]


def per_deck(rows):
    """Mean hpCost per deck per boss, which is the unit the vector lives on."""
    out = collections.defaultdict(lambda: collections.defaultdict(list))
    meta = {}
    for r in rows:
        out[r["run_hash"]][r["boss"]].append(r["finalEnemyHp"])
        meta[r["run_hash"]] = (r.get("character"), r.get("win"), r.get("killed_by"))
    return {h: {b: st.mean(v) for b, v in per.items()} for h, per in out.items()}, meta


def survivor_hp_cost(rows):
    """hp_cost restricted to the fights that were actually survived.

    On a death every deck reads the same number, so including those rows would
    add a constant to one side of every comparison and flatten the AUC.
    """
    surv = [r for r in rows if str(r.get("died", "")) in ("0", "False", "false")]
    dead = [r for r in rows if r not in surv]
    return surv, dead


# ---- (a) direction ---------------------------------------------------------

def direction(rows):
    """Winners must leave the boss lower than decks that really died deep.

    finalEnemyHp is "lower is better", so the AUC is taken on the negation: above
    0.5 means the ruler put the real winners in front. hp_cost is reported
    alongside but only where the fight was survived, because on a death it is
    identically max HP.
    """
    per, meta = per_deck(rows)
    print("=" * 78)
    print("(a) DIRECTION — do real winners cost less HP than real deep losses?")
    print("=" * 78)
    for boss in BOSSES:
        w = [-per[h][boss] for h, m in meta.items() if m[1] and boss in per[h]]
        l = [-per[h][boss] for h, m in meta.items() if m[1] is False and boss in per[h]]
        # (negated finalEnemyHp: lower enemy HP is better, so the sign flips once
        #  here and the AUC reads "higher is better" as usual)
        if len(w) < 5 or len(l) < 5:
            print(f"  {boss:22} not enough decks ({len(w)}W/{len(l)}L)")
            continue
        obs = auc(w, l)
        lo, hi = boot_ci(w, l)
        p = perm_p(w, l, obs)
        verdict = "PASS" if lo > 0.5 else ("FAIL" if hi < 0.5 else "INCONCLUSIVE")
        print(f"  {boss:22} n={len(w)}W/{len(l)}L  AUC(negated cost) {obs:.2f}"
              f"  CI {lo:.2f}-{hi:.2f}  perm p={p:.3f}  -> {verdict}")
    # the pair a run actually faces, from the three independent numbers
    print()
    print("  worst pair per deck (what a run must survive), vs character HP:")
    hp = {"IRONCLAD": 80, "SILENT": 70, "DEFECT": 75, "NECROBINDER": 66, "REGENT": 76}
    for h, costs in list(per.items())[:5]:
        if len(costs) < 3:
            continue
        vals = [costs[b] for b in BOSSES if b in costs]
        worst = max(vals[i] + vals[j] for i in range(len(vals)) for j in range(i + 1, len(vals)))
        print(f"    {h[:12]:12} {[round(v,1) for v in vals]}  worst pair {worst:.0f}"
              f"  HP {hp.get(meta[h][0], 75)}  {'passes' if worst <= hp.get(meta[h][0], 75) else 'FAILS'}")


# ---- (b) reproducibility ---------------------------------------------------

def reproducibility(rows, budget_a=None, budget_b=None):
    """Two channels of machine dependence, both found by reading the solver."""
    print()
    print("=" * 78)
    print("(b) REPRODUCIBILITY — is the reading a property of the deck or the box?")
    print("=" * 78)
    by = collections.defaultdict(list)
    for r in rows:
        by[(r["run_hash"], r["boss"], r["seed"])].append(r["finalEnemyHp"])
    repeats = {k: v for k, v in by.items() if len(v) > 1}
    if not repeats:
        print("  same-seed repeats: NONE in this file — cannot test determinism.")
        print("  ASK FOR: the same (deck, boss, seed) run twice, in one invocation.")
    else:
        bad = {k: v for k, v in repeats.items() if len(set(v)) > 1}
        print(f"  same-seed repeats: {len(repeats)}, differing: {len(bad)}")
        if bad:
            k = next(iter(bad))
            print(f"    e.g. {k} -> {bad[k]}  ** the oracle is not deterministic **")

    seeds = collections.defaultdict(set)
    for r in rows:
        seeds[(r["run_hash"], r["boss"])].add(r["seed"])
    multi = [k for k, v in seeds.items() if len(v) > 1]
    if not multi:
        print("  seed spread: no (deck, boss) has more than one seed — cannot compare")
        print("  the seed axis against the boss axis. ASK FOR: >=3 seeds per (deck, boss).")
    else:
        seed_spread, boss_spread = [], []
        per, _ = per_deck(rows)
        for h, costs in per.items():
            if len(costs) > 1:
                boss_spread.append(max(costs.values()) - min(costs.values()))
        for h, b in seeds:
            vals = [r["finalEnemyHp"] for r in rows if r["run_hash"] == h and r["boss"] == b]
            if len(vals) > 1:
                seed_spread.append(max(vals) - min(vals))
        if seed_spread and boss_spread:
            ms, mb = st.mean(seed_spread), st.mean(boss_spread)
            print(f"  within-deck spread: across seeds {ms:.1f} HP, across bosses {mb:.1f} HP"
                  f"  -> spend the budget on the {'BOSS' if mb > ms else 'SEED'} axis")

    if "budget_ms" in (rows[0] if rows else {}):
        budgets = sorted({r.get("budget_ms") for r in rows})
        print(f"  distinct budgets seen: {budgets}")
        if budget_a and budget_b:
            pa = {r["run_hash"]: r["hp_cost"] for r in rows if str(r.get("budget_ms")) == str(budget_a)}
            pb = {r["run_hash"]: r["hp_cost"] for r in rows if str(r.get("budget_ms")) == str(budget_b)}
            common = set(pa) & set(pb)
            if common:
                d = [abs(pa[h] - pb[h]) for h in common]
                print(f"  budget {budget_a} vs {budget_b} on {len(common)} decks:"
                      f" mean |delta| {st.mean(d):.1f} HP, max {max(d):.1f}")
                print("    a large delta means the reading is a function of how long we let it")
                print("    think, not of the deck — it must then be pinned and reported.")
    else:
        print("  no budget_ms column: ASK FOR the wall-clock budget to be recorded per row.")
    if "parallelism" in (rows[0] if rows else {}):
        print(f"  distinct parallelism values seen: {sorted({r.get('parallelism') for r in rows})}")
        print("    more than one value means the reading may depend on core count;")
        print("    pin it explicitly rather than inheriting ProcessorCount.")


# ---- (c) structural agreement ----------------------------------------------

def structural(rows):
    """The tester's read of each deck against the oracle's ordering."""
    print()
    print("=" * 78)
    print("(c) STRUCTURE — does the tester's reading of a deck track the oracle?")
    print("=" * 78)
    path = "scripts/analyze-complete-fragmented.py"
    print(f"  not implemented yet: needs the same deck set read through {path}")
    print("  plan: for each deck compute (size, upgrades, starter count, skill/attack")
    print("  ratio, block-per-card, package counts), rank the decks by each, and")
    print("  Spearman them against the oracle's worst-pair hpCost.")
    print("  A systematic disagreement is the finding: either the algebra does not")
    print("  describe what sets combat cost, or the ruler is wrong. Report both.")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv")
    ap.add_argument("--budget-a")
    ap.add_argument("--budget-b")
    args = ap.parse_args()
    if not args.csv:
        print(__doc__)
        print("No --csv given. This file is a prepared skeleton: the oracle it")
        print("verifies does not exist yet, so there is nothing to run.")
        return
    rows = load(args.csv)
    print(f"{len(rows)} oracle rows from {args.csv}")
    direction(rows)
    reproducibility(rows, args.budget_a, args.budget_b)
    structural(rows)


if __name__ == "__main__":
    main()
