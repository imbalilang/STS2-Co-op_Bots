#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Bounded two-parameter fit of the shiv prior model against measured battles.

This module is the PURE fitting layer for task ``shiv-fit-v1``. It owns the
frozen case design, the frozen training utility, the matched-seed rule, the
36-candidate multiplier grid and the deterministic tie-break. It never launches
battle and it never reads holdout data: :func:`fit_parameters` accepts
``split="train"`` only and raises if a holdout split is passed.

Frozen design (declared before any outcome)
-------------------------------------------
Three train cases and one holdout case. All are singleplayer synthetic card-only
PROXY decks from ``scripts/shiv_prior_model.SCENARIOS``, unupgraded, SILENT A10
actIndex 2 HP70, no relics/potions, enemy ``FROG_KNIGHT_NORMAL``:

* ``train-producers``  = ``producers_no_payoffs`` (17), choose ACCURACY / ADRENALINE / skip.
* ``train-payoffs``    = ``payoffs_no_producers`` (16), choose BLADE_DANCE / LEG_SWEEP / skip.
* ``train-defense``    = ``defense_poor`` + CLOAK_AND_DAGGER x2 + BACKFLIP x2 (17),
  choose LEG_SWEEP / ADRENALINE / skip. The support additions are declared here
  before outcomes and are NOT a real-run deck.
* ``holdout-duplicates`` = ``duplicates`` (19), choose KNIFE_TRAP / ADRENALINE / skip.

Train seeds are SHIVFITD0001..0200, shared across the three train cases. Holdout
seeds are SHIVFITH0001..0200, unique from train. Only seeds whose three actions
all qualify (input+policy verified, complete win/loss) are matched examples.

Frozen training utility
-----------------------
``U = 1[win] + 0.5*effective_playerHP/70 - 0.5*min(final_enemyHP/200, 1)`` with
``effective_playerHP = 0`` on a loss and ``final_enemyHP = 0`` on a win. This is
a deliberately specified bounded heuristic target: a win outweighs a loss and
all wins keep a war-of-attrition signal. Raw winrate / player HP / enemy HP are
reported separately so tradeoffs stay visible.

Fitted multipliers
------------------
Two group multipliers on the model's own weights (all other weights, caps and
assumptions unchanged): defense = {w_direct_block, w_weak, w_dex_block,
w_afterimage_block}; engine = {w_draw_support, w_energy_support}. Each is drawn
from the grid [0.5, 0.75, 1, 1.25, 1.5, 2]. Action selection uses
``shiv_prior_model.rank_choices(..., community=False)`` (mechanism-only).

Objective
---------
``mean measured utility of the selected action over the 3 equally weighted train
cases - 0.002*(log(defense)^2 + log(engine)^2)``. Ties break by distance from
the baseline (1, 1) in log space, then by numeric (defense, engine) ascending.
The baseline candidate is always in the grid; if it wins there is no fake
improvement.

CLI
---
``python scripts/fit_shiv_v1.py [--run-root ...] [--out-dir ...]`` aggregates an
existing ``work/shiv-fit-v1/<UTC>/journal.jsonl`` (train cells only), fits and
writes ``params-before.json`` / ``params-trained.json`` / ``grid.json`` /
``train-summary.json``. It never runs the holdout. The execution runner calls
:func:`fit_parameters` directly.

Tests: ``python -m unittest discover -s tests -p test_shiv_fit_v1.py``
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import statistics
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import shiv_prior_model as m  # noqa: E402

WORK = ROOT / "work" / "shiv-fit-v1"

# --------------------------------------------------------------------------- #
# Frozen design constants
# --------------------------------------------------------------------------- #

ENCOUNTER = "FROG_KNIGHT_NORMAL"
CHARACTER = "SILENT"
ASCENSION = 10
ACT_INDEX = 2
PLAYER_HP = 70

DEFENSE_WEIGHTS = ("w_direct_block", "w_weak", "w_dex_block", "w_afterimage_block")
ENGINE_WEIGHTS = ("w_draw_support", "w_energy_support")
MULTIPLIERS = (0.5, 0.75, 1.0, 1.25, 1.5, 2.0)
DEFAULT_GRID = tuple((d, e) for d in MULTIPLIERS for e in MULTIPLIERS)
PENALTY_COEF = 0.002
SPLIT_TRAIN = "train"
SPLIT_HOLDOUT = "holdout"

TRAIN_SEEDS = tuple("SHIVFITD%04d" % i for i in range(1, 201))
HOLDOUT_SEEDS = tuple("SHIVFITH%04d" % i for i in range(1, 201))

TRAIN_CASES = (
    {
        "name": "train-producers",
        "scenario": "producers_no_payoffs",
        "add": (),
        "choices": ("ACCURACY", "ADRENALINE", "skip"),
        "split": SPLIT_TRAIN,
        "expected_length": 17,
    },
    {
        "name": "train-payoffs",
        "scenario": "payoffs_no_producers",
        "add": (),
        "choices": ("BLADE_DANCE", "LEG_SWEEP", "skip"),
        "split": SPLIT_TRAIN,
        "expected_length": 16,
    },
    {
        "name": "train-defense",
        "scenario": "defense_poor",
        "add": (("CLOAK_AND_DAGGER", 2), ("BACKFLIP", 2)),
        "choices": ("LEG_SWEEP", "ADRENALINE", "skip"),
        "split": SPLIT_TRAIN,
        "expected_length": 17,
        "synthetic_support_additions": ["CLOAK_AND_DAGGER x2", "BACKFLIP x2"],
        "real_run": False,
    },
)

HOLDOUT_CASES = (
    {
        "name": "holdout-duplicates",
        "scenario": "duplicates",
        "add": (),
        "choices": ("KNIFE_TRAP", "ADRENALINE", "skip"),
        "split": SPLIT_HOLDOUT,
        "expected_length": 19,
    },
)

ALL_CASES = TRAIN_CASES + HOLDOUT_CASES

Z95 = 1.959963984540054
T_APPROX_LABEL = "cornish_fisher_t_df_ge_24"

UTILITY_FORMULA = (
    "U = 1[win] + 0.5*effective_playerHP/70 - 0.5*min(final_enemyHP/200, 1); "
    "effective_playerHP=0 on loss; final_enemyHP=0 on win"
)


class FitError(Exception):
    """Base class for scoped fitting failures."""


class FitIncompleteError(FitError):
    """Raised when a train case lacks the matched seeds required to fit."""


# --------------------------------------------------------------------------- #
# Small helpers
# --------------------------------------------------------------------------- #

def canonical_sha(value) -> str:
    payload = json.dumps(value, ensure_ascii=False, sort_keys=True,
                         separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def _mean(values):
    values = list(values)
    return statistics.fmean(values) if values else None


def t_critical_cf(df: int) -> float:
    """Cornish-Fisher expansion of the Student-t 95% critical value for df>=24."""
    z = Z95
    return (z + (z ** 3 + z) / (4 * df) + (5 * z ** 5 + 16 * z ** 3 + 3 * z) / (96 * df ** 2)
            + (3 * z ** 7 + 19 * z ** 5 + 17 * z ** 3 - 15 * z) / (384 * df ** 3))


def summary_stats(values) -> dict:
    values = list(values)
    n = len(values)
    out = {"n": n, "mean": None, "sd": None, "se": None, "t_critical": None,
           "t_approx": None, "ci95_low": None, "ci95_high": None, "ci95_halfwidth": None,
           "precision": "insufficient_n_lt_25"}
    if n == 0:
        out["precision"] = "no_valid_values"
        return out
    out["mean"] = statistics.fmean(values)
    if n < 2:
        return out
    out["sd"] = statistics.stdev(values)
    out["se"] = out["sd"] / math.sqrt(n)
    if n >= 25:
        df = n - 1
        tcrit = t_critical_cf(df)
        out["t_critical"] = tcrit
        out["t_approx"] = T_APPROX_LABEL
        out["ci95_halfwidth"] = tcrit * out["se"]
        out["ci95_low"] = out["mean"] - out["ci95_halfwidth"]
        out["ci95_high"] = out["mean"] + out["ci95_halfwidth"]
        out["precision"] = "ok"
    return out


def wilson95(wins: int, n: int) -> dict:
    if n == 0:
        return {"wins": 0, "n": 0, "fraction": None, "low": None, "high": None}
    p = wins / n
    z = Z95
    denom = 1.0 + z * z / n
    centre = (p + z * z / (2.0 * n)) / denom
    half = (z / denom) * math.sqrt(p * (1.0 - p) / n + z * z / (4.0 * n * n))
    return {"wins": wins, "n": n, "fraction": p, "low": max(0.0, centre - half),
            "high": min(1.0, centre + half)}


# --------------------------------------------------------------------------- #
# Frozen cases and utility
# --------------------------------------------------------------------------- #

def case_specs(split=None):
    specs = list(ALL_CASES)
    if split is not None:
        specs = [s for s in specs if s["split"] == split]
    return specs


def seeds_for(case) -> tuple:
    return TRAIN_SEEDS if case["split"] == SPLIT_TRAIN else HOLDOUT_SEEDS


def deck_for_case(case, model=m) -> list:
    """Frozen full deck for a case (base scenario plus declared additions)."""
    scenario = next((s for s in model.SCENARIOS if s["name"] == case["scenario"]), None)
    if scenario is None:
        raise FitError("scenario %r not found in the frozen model" % case["scenario"])
    deck = list(scenario["deck"])
    for card, count in case.get("add", ()):
        deck.extend([card] * int(count))
    return deck


def check_choice_action(action: str) -> None:
    if action not in m.WHITELIST and action != "skip":
        raise FitError("action %r is not a whitelist card or skip" % action)


def utility(record: dict) -> float:
    """Frozen bounded training utility for one complete battle record.

    Unknown / incomplete / errored statuses are rejected (never imputed as a
    loss). ``effective_playerHP`` is 0 on a loss; ``final_enemyHP`` is 0 on a win.
    """
    status = record.get("outcome_status")
    if status not in ("win", "loss"):
        raise FitError("outcome_status %r is not a complete win/loss" % (status,))
    win = status == "win"
    if win:
        effective_player = float(record.get("player_hp") or 0.0)
        final_enemy = 0.0
    else:
        effective_player = 0.0
        final_enemy = min(float(record.get("enemy_hp") or 0.0) / 200.0, 1.0)
    return (1.0 if win else 0.0) + 0.5 * effective_player / 70.0 - 0.5 * final_enemy


def matched_seed_utilities(case, per_seed_records: dict) -> dict:
    """Seed -> {action: U} for seeds where ALL of the case's actions qualify.

    A seed needs every action present, input+policy eligible, and a complete
    win/loss. Errors and incomplete cells are excluded, never treated as losses.
    """
    choices = list(case["choices"])
    matched = {}
    for seed in sorted(per_seed_records):
        per_action = per_seed_records[seed]
        records = {}
        ok = True
        for action in choices:
            record = per_action.get(action)
            if not isinstance(record, dict):
                ok = False
                break
            if not record.get("eligible") or record.get("outcome_status") not in ("win", "loss"):
                ok = False
                break
            records[action] = record
        if not ok:
            continue
        matched[seed] = {action: utility(records[action]) for action in choices}
    return matched


def case_action_means(matched: dict, case) -> dict:
    means = {}
    for action in case["choices"]:
        means[action] = _mean(entry[action] for entry in matched.values())
    return means


def select_action(deck, choices, params) -> str:
    """Mechanism-only (community=False) model choice, mapped to the frozen key."""
    candidates = [c for c in choices if c != "skip"]
    result = m.rank_choices(list(deck), candidates=candidates, params=params, community=False)
    winner = result["winner"]
    return "skip" if winner == "SKIP" else winner


def apply_multipliers(base_params, defense: float, engine: float) -> dict:
    """Scale only the two declared weight groups; reject out-of-grid multipliers."""
    if defense not in MULTIPLIERS or engine not in MULTIPLIERS:
        raise ValueError("multipliers must be in %r, got (%r, %r)"
                         % (MULTIPLIERS, defense, engine))
    params = copy.deepcopy(base_params)
    for key in DEFENSE_WEIGHTS:
        params["weights"][key] = base_params["weights"][key] * defense
    for key in ENGINE_WEIGHTS:
        params["weights"][key] = base_params["weights"][key] * engine
    params["label"] = "shiv_fit_v1_d%s_e%s" % (_tag(defense), _tag(engine))
    params["note"] = (
        "Fitted task shiv-fit-v1: defense group x%s, engine group x%s; all other "
        "weights, caps and assumptions unchanged from the manual baseline."
        % (_tag(defense), _tag(engine))
    )
    return params


def _tag(value: float) -> str:
    return ("%g" % value).replace(".", "p")


# --------------------------------------------------------------------------- #
# Fitting
# --------------------------------------------------------------------------- #

def fit_parameters(train_cases, base_params=None, split: str = SPLIT_TRAIN,
                   grid=DEFAULT_GRID) -> dict:
    """Grid-fit the two group multipliers on TRAIN cases only.

    ``train_cases`` is a list of dicts with ``name``, ``deck``, ``choices`` and
    ``matched`` (seed -> {action: U}). ``split`` must be ``"train"``; passing the
    holdout split raises. The baseline (1, 1) candidate is always evaluated. Ties
    are deterministic: nearest baseline in log space, then numeric pair.
    """
    if split != SPLIT_TRAIN:
        raise ValueError("fit_parameters only accepts split=%r, got %r"
                         % (SPLIT_TRAIN, split))
    if not train_cases:
        raise FitIncompleteError("no train cases supplied")
    base = copy.deepcopy(m.DEFAULT_PARAMS) if base_params is None else base_params

    prepared = []
    for case in train_cases:
        matched = case.get("matched") or {}
        if not matched:
            raise FitIncompleteError(
                "train case %r has no matched valid seeds (training_incomplete)" % case["name"])
        means = case_action_means(matched, case)
        if any(means[a] is None for a in case["choices"]):
            raise FitIncompleteError("train case %r has an empty action mean" % case["name"])
        best_action = max(case["choices"], key=lambda a: means[a])
        prepared.append({
            "case": case,
            "means": means,
            "best_action": best_action,
            "best_u": means[best_action],
        })

    entries = []
    for defense, engine in grid:
        params = apply_multipliers(base, defense, engine)
        per_case = []
        raw_sum = 0.0
        regret_sum = 0.0
        for item in prepared:
            case = item["case"]
            selected = select_action(case["deck"], case["choices"], params)
            measured = item["means"][selected]
            raw_sum += measured
            regret_sum += item["best_u"] - measured
            per_case.append({
                "case": case["name"],
                "selected_action": selected,
                "measured_u": measured,
                "best_action": item["best_action"],
                "best_u": item["best_u"],
                "regret": item["best_u"] - measured,
                "action_means": dict(item["means"]),
            })
        raw_mean = raw_sum / len(prepared)
        penalty = PENALTY_COEF * (math.log(defense) ** 2 + math.log(engine) ** 2)
        objective = raw_mean - penalty
        entries.append({
            "defense": defense,
            "engine": engine,
            "distance_from_baseline": math.hypot(math.log(defense), math.log(engine)),
            "raw_mean_u": raw_mean,
            "penalty": penalty,
            "objective": objective,
            "train_regret": regret_sum / len(prepared),
            "per_case": per_case,
        })

    baseline_entry = next(e for e in entries if e["defense"] == 1.0 and e["engine"] == 1.0)
    ordered = sorted(entries, key=lambda e: (-e["objective"], e["distance_from_baseline"],
                                             e["defense"], e["engine"]))
    best = ordered[0]
    baseline_selected = best["defense"] == 1.0 and best["engine"] == 1.0
    if baseline_selected:
        # Baseline wins: the artifact must equal the frozen baseline exactly
        # (no relabel-only "improvement").
        best_params = copy.deepcopy(base)
    else:
        best_params = apply_multipliers(base, best["defense"], best["engine"])
    improved = best["objective"] > baseline_entry["objective"] + 1e-12

    return {
        "schema": "shiv-fit-v1",
        "split": SPLIT_TRAIN,
        "utility_formula": UTILITY_FORMULA,
        "posterior_penalty_coef": PENALTY_COEF,
        "defense_weights": list(DEFENSE_WEIGHTS),
        "engine_weights": list(ENGINE_WEIGHTS),
        "multipliers": list(MULTIPLIERS),
        "grid_size": len(entries),
        "case_names": [item["case"]["name"] for item in prepared],
        "seed_counts": {item["case"]["name"]: len(item["case"].get("matched") or {})
                        for item in prepared},
        "selected": {
            "defense": best["defense"],
            "engine": best["engine"],
            "objective": best["objective"],
            "raw_mean_u": best["raw_mean_u"],
            "penalty": best["penalty"],
            "train_regret": best["train_regret"],
            "per_case": best["per_case"],
        },
        "baseline": {
            "defense": baseline_entry["defense"],
            "engine": baseline_entry["engine"],
            "objective": baseline_entry["objective"],
            "raw_mean_u": baseline_entry["raw_mean_u"],
            "penalty": baseline_entry["penalty"],
            "train_regret": baseline_entry["train_regret"],
            "per_case": baseline_entry["per_case"],
        },
        "improved_over_baseline": improved,
        "baseline_selected": baseline_selected,
        "ranking": [{"defense": e["defense"], "engine": e["engine"],
                     "objective": e["objective"]} for e in ordered],
        "params": best_params,
        "params_sha256": canonical_sha(best_params),
        "grid": entries,
        "notes": [
            "Mechanism-only action selection (community=False).",
            "Only matched seeds where all three actions qualify enter the fit.",
            "The score is a bounded heuristic target, not win probability or HP.",
            "3 base cases share 200 seeds; seeds are not independent parameters.",
        ],
    }


# --------------------------------------------------------------------------- #
# Journal aggregation (train only) for the CLI
# --------------------------------------------------------------------------- #

def load_journal(path) -> list:
    records = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line:
            records.append(json.loads(line))
    return records


def records_by_case_seed(records) -> dict:
    """{case_name: {seed: {action: record}}} for journal records that carry a case."""
    grouped = {}
    for record in records:
        case_name = record.get("case")
        seed = record.get("seed")
        action = record.get("action")
        if not case_name or not seed or not action:
            continue
        grouped.setdefault(case_name, {}).setdefault(seed, {})[action] = record
    return grouped


def build_train_cases(records) -> list:
    grouped = records_by_case_seed(records)
    cases = []
    for spec in TRAIN_CASES:
        per_seed = grouped.get(spec["name"], {})
        case = dict(spec)
        case["deck"] = deck_for_case(spec)
        case["matched"] = matched_seed_utilities(spec, per_seed)
        cases.append(case)
    return cases


def write_json(path, payload) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def newest_run_root() -> Path:
    if not WORK.exists():
        raise SystemExit("no work/shiv-fit-v1 directory")
    candidates = [child for child in sorted(WORK.iterdir())
                  if child.is_dir() and (child / "journal.jsonl").exists()
                  and (child / "journal.jsonl").stat().st_size > 0]
    if not candidates:
        raise SystemExit("no run root with a non-empty journal.jsonl under work/shiv-fit-v1")
    return candidates[-1]


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--run-root", help="run root holding journal.jsonl (default: newest)")
    parser.add_argument("--out-dir", help="artifact directory (default: run root)")
    args = parser.parse_args(argv)

    run_root = Path(args.run_root) if args.run_root else newest_run_root()
    out_dir = Path(args.out_dir) if args.out_dir else (run_root / "fit-cli")
    records = load_journal(run_root / "journal.jsonl")
    train_cases = build_train_cases(records)

    write_json(out_dir / "params-before.json", m.DEFAULT_PARAMS)
    incomplete = [c["name"] for c in train_cases if len(c["matched"]) != len(TRAIN_SEEDS)]
    if incomplete:
        reason = ("incomplete training pairs (need %d matched seeds): %s"
                  % (len(TRAIN_SEEDS), incomplete))
        summary = {
            "schema": "shiv-fit-v1",
            "status": "training_incomplete",
            "reason": reason,
            "run_root": str(run_root),
            "case_seed_counts": {c["name"]: len(c["matched"]) for c in train_cases},
        }
        write_json(out_dir / "train-summary.json", summary)
        sys.stdout.write(json.dumps({"status": "training_incomplete", "reason": reason},
                                    ensure_ascii=False) + "\n")
        return 3
    try:
        result = fit_parameters(train_cases, m.DEFAULT_PARAMS, split=SPLIT_TRAIN)
    except FitIncompleteError as exc:
        summary = {
            "schema": "shiv-fit-v1",
            "status": "training_incomplete",
            "reason": str(exc),
            "run_root": str(run_root),
            "case_seed_counts": {c["name"]: len(c["matched"]) for c in train_cases},
        }
        write_json(out_dir / "train-summary.json", summary)
        sys.stdout.write(json.dumps({"status": "training_incomplete", "reason": str(exc)},
                                    ensure_ascii=False) + "\n")
        return 3

    write_json(out_dir / "params-trained.json", result["params"])
    write_json(out_dir / "grid.json", {
        "schema": "shiv-fit-v1",
        "split": SPLIT_TRAIN,
        "posterior_penalty_coef": PENALTY_COEF,
        "multipliers": list(MULTIPLIERS),
        "selected": {"defense": result["selected"]["defense"],
                     "engine": result["selected"]["engine"]},
        "baseline": {"defense": 1.0, "engine": 1.0},
        "entries": result["grid"],
        "ranking": result["ranking"],
    })
    write_json(out_dir / "train-summary.json", result)
    sys.stdout.write(json.dumps({
        "status": "ok",
        "selected": {"defense": result["selected"]["defense"],
                     "engine": result["selected"]["engine"]},
        "objective": result["selected"]["objective"],
        "baseline_objective": result["baseline"]["objective"],
        "improved_over_baseline": result["improved_over_baseline"],
        "params_sha256": result["params_sha256"],
    }, ensure_ascii=False) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
