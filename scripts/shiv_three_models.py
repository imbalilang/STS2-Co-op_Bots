#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Three distinct shiv model families plus independent-seed confirmation.

This is the scoped implementation for task ``shiv-three-models``. It only reads
immutable sources and the original ``work/shiv-fit-v1`` journal; it never edits
the prior model/score/runner modules and never mutates their globals.

Frozen design (declared BEFORE any new outcome)
-----------------------------------------------
Three model families all choose among exactly KNIFE_TRAP / ADRENALINE / skip on
the single ``holdout-duplicates`` deck (``duplicates`` scenario, 19 cards),
SILENT A10 actIndex 2 HP70, no relics/potions, enemy FROG_KNIGHT_NORMAL,
unupgraded, one Ascender's Bane:

* **A mechanism** - the original ``scripts/fit_shiv_v1.py`` training-only
  two-group grid fit (defense / engine). We only validate its hashes and
  parameters and reuse them; A is never refit here and never sees heldout data.
* **B community hybrid** - baseline untrained mechanism marginal normalized by
  the TRAIN-only global RMS of non-skip marginal scores (min 1e-9), mixed with
  ``(A10_card_Elo - A10_SKIP_Elo) / 400``:
  ``score = (1-lam)*mechanism_norm + lam*elo_delta``; skip is 0. Lambda grid
  ``[0, .25, .5, .75, 1]``; select by training mean measured utility minus a
  fixed ``0.002*lam^2`` penalty (tie -> lower lambda). Missing Elo fails loudly.
* **C combat ridge** - no-intercept linear ridge predicts
  ``meanU(deck+card) - meanU(deck+skip)`` from three baseline-model component
  delta groups (offense / defense / engine), features scaled by their TRAIN RMS,
  fit with fixed ridge lambda 1 in ``(X'X + I)b = X'y`` over the six training
  (case, non-skip) rows, equal weights, stdlib 3x3 Gaussian elimination.

Fit data is the original ``work/shiv-fit-v1`` journal, filtered by the frozen
train case names only. ``--fit`` never reads the holdout case and fails unless
all 200 matched seeds x 3 actions x 3 cases = 1800 records validate.

Confirmation ``--confirm`` reuses the original heldout SHIVFITH0001..0200 as
replication H (only because ``--fit`` stored ``holdout_read=false``) and runs a
fresh replication R SHIVFITR0001..0200 (max 600 new battles, one 1200 s
deadline, N16 dop2 nodes6000 budget120000 searchTimeout180000 maxturn40
process300, no retries). The frozen criterion is a family of 12 comparisons
(3 models x 2 baselines x 2 replications), two-sided alpha .05/12 each, with a
Cornish-Fisher df correction on the Bonferroni normal quantile.

CLI
---
``python scripts/shiv_three_models.py --prepare``   # freeze protocol.json + doc
``python scripts/shiv_three_models.py --fit``       # train-only fit -> models.json
``python scripts/shiv_three_models.py --confirm``   # H + R -> confirmation doc

Tests: ``python -m unittest discover -s tests -p test_shiv_three_models.py``
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
import os
import statistics
import sys
import threading
import time
from concurrent.futures import CancelledError, ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path

if sys.stdout is not None:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:  # noqa: BLE001 - best effort console encoding only
        pass

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import fit_shiv_v1 as fit  # noqa: E402
import shiv_prior_model as m  # noqa: E402

# --------------------------------------------------------------------------- #
# Paths and frozen constants
# --------------------------------------------------------------------------- #

MODELS_DIR = ROOT / "work" / "shiv-three-models"
PROTOCOL_PATH = MODELS_DIR / "protocol.json"
MODELS_PATH = MODELS_DIR / "models.json"
TRAIN_SNAPSHOT_PATH = MODELS_DIR / "train-snapshot.json"
FIT_SUMMARY_PATH = MODELS_DIR / "fit-summary.json"
CONFIRM_SUMMARY_PATH = MODELS_DIR / "confirm-summary.json"
LOCK_PATH = MODELS_DIR / "batch.lock"
REPORT = ROOT / "docs" / "building" / "SHIV_THREE_MODELS.md"

FIT_WORK = ROOT / "work" / "shiv-fit-v1"
FIT_PARAMS_FALLBACK = FIT_WORK / "params-trained.json"
FIT_SUMMARY_FALLBACK = FIT_WORK / "train-summary.json"
SCORES_A10 = ROOT / "work" / "shiv-score-audit" / "scores-a10.json"
GRAPH_PATH = ROOT / "work" / "shiv-relationship-graph" / "graph.json"
BATCH = ROOT / "work" / "pick-expansion-batch"

ENCOUNTER = fit.ENCOUNTER
CHARACTER = fit.CHARACTER
ASCENSION = fit.ASCENSION
ACT_INDEX = fit.ACT_INDEX
PLAYER_HP = fit.PLAYER_HP

HOLDOUT_CASE = dict(fit.HOLDOUT_CASES[0])
CHOICES = tuple(HOLDOUT_CASE["choices"])
H_SEEDS = tuple(fit.HOLDOUT_SEEDS)
R_SEEDS = tuple("SHIVFITR%04d" % index for index in range(1, 201))

TRAIN_NAMES = tuple(case["name"] for case in fit.TRAIN_CASES)
HOLDOUT_NAMES = tuple(case["name"] for case in fit.HOLDOUT_CASES)

LAMBDAS = (0.0, 0.25, 0.5, 0.75, 1.0)
B_STATUS_PENALTY = 0.002
B_RMS_MIN = 1e-9
C_RIDGE_LAMBDA = 1.0
C_GROUPS = {
    "offense": ("shiv_play", "accuracy", "phantom_first_shiv", "knife_trap", "attack_damage"),
    "defense": ("direct_block", "weak", "footwork", "afterimage"),
    "engine": ("draw_support", "energy_support", "power_setup_penalty", "deck_dilution"),
}

FAMILY_COMPARISONS = 12
ALPHA = 0.05
Z95 = 1.959963984540054
MODEL_IDS = ("A", "B", "C")
BASELINE_IDS = ("default_model", "elo_with_skip")
REPLICATIONS = ("H", "R")

PARALLELISM = 16
CONFIRM_DEADLINE_SECONDS = 1200
MAX_NEW_BATTLES = 600

EXPECTED_PRIOR = {
    "DOP": "2", "NODES": "6000", "BUDGET": 120000, "SEARCH_TIMEOUT": "180000",
    "MAX_TURNS": "40", "PER_PROCESS_SECONDS": 300, "DEADLINE_SECONDS": 1800,
}

COMPONENT_KEYS = tuple(m._COMPONENT_KEYS)


class ThreeModelsError(Exception):
    """Base class for scoped failures."""


class HoldoutAccessError(ThreeModelsError):
    """Raised when a fit path is asked to consume heldout records."""


class MissingEloError(ThreeModelsError):
    """Raised when a required A10 Elo is missing or null."""


class TrainingIncompleteError(ThreeModelsError):
    """Raised when the train snapshot lacks the 1800 validated records."""


class ParamValidationError(ThreeModelsError):
    """Raised when the original fit parameters cannot be validated."""


# --------------------------------------------------------------------------- #
# Small helpers
# --------------------------------------------------------------------------- #

def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def file_sha256(path) -> str | None:
    path = Path(path)
    if not path.exists():
        return None
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def rel_path(path) -> str:
    path = Path(path)
    try:
        return str(path.resolve().relative_to(ROOT.resolve()))
    except ValueError:
        return str(path)


def write_json(path, payload) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def load_module(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def within(path, root) -> bool:
    try:
        Path(path).resolve().relative_to(Path(root).resolve())
        return True
    except ValueError:
        return False


def _mean(values):
    values = list(values)
    return statistics.fmean(values) if values else None


def _rms(values) -> float:
    values = list(values)
    if not values:
        return 0.0
    return math.sqrt(sum(value * value for value in values) / len(values))


# --------------------------------------------------------------------------- #
# Statistics (Bonferroni + Cornish-Fisher)
# --------------------------------------------------------------------------- #

def cornish_fisher_t(z: float, df: int) -> float:
    """Cornish-Fisher expansion of the Student-t quantile for df >= 24."""
    return (z + (z ** 3 + z) / (4 * df)
            + (5 * z ** 5 + 16 * z ** 3 + 3 * z) / (96 * df ** 2)
            + (3 * z ** 7 + 19 * z ** 5 + 17 * z ** 3 - 15 * z) / (384 * df ** 3))


def adjusted_normal_z(family: int = FAMILY_COMPARISONS, alpha: float = ALPHA) -> float:
    """Two-sided Bonferroni normal quantile for ``alpha / family`` per test."""
    return statistics.NormalDist().inv_cdf(1.0 - alpha / (2.0 * family))


def adjusted_t_critical(df: int) -> float:
    return cornish_fisher_t(adjusted_normal_z(), df)


def add_adjusted_interval(stats: dict, family: int = FAMILY_COMPARISONS) -> dict:
    """Augment a ``fit.summary_stats`` result with the Bonferroni-adjusted CI."""
    out = dict(stats)
    out["adjusted_family"] = family
    out["adjusted_alpha_each"] = ALPHA / family
    out["adjusted_t_critical"] = None
    out["adjusted_ci_low"] = None
    out["adjusted_ci_high"] = None
    n = out.get("n")
    se = out.get("se")
    mean = out.get("mean")
    if isinstance(n, int) and n >= 25 and se is not None and mean is not None:
        tcrit = adjusted_t_critical(n - 1)
        out["adjusted_t_critical"] = tcrit
        out["adjusted_ci_low"] = mean - tcrit * se
        out["adjusted_ci_high"] = mean + tcrit * se
    return out


def ordinary_t_critical(df: int) -> float:
    return cornish_fisher_t(Z95, df)


# --------------------------------------------------------------------------- #
# Protocol (frozen before outcomes)
# --------------------------------------------------------------------------- #

def _sources() -> dict:
    files = {
        "driver": Path(__file__),
        "fit_module": SCRIPT_DIR / "fit_shiv_v1.py",
        "model_source": SCRIPT_DIR / "shiv_prior_model.py",
        "score_snapshot_a10": SCORES_A10,
        "relationship_graph": GRAPH_PATH,
        "prior_runner": BATCH / "run_batch.py",
    }
    return {key: {"path": rel_path(path), "sha256": file_sha256(path)}
            for key, path in files.items()}


def build_protocol() -> dict:
    """The complete fixed specification. Pure: no data or outcome is read."""
    return {
        "schema": "shiv-three-models-protocol",
        "schema_version": 1,
        "created_at_utc": utc_now(),
        "frozen_before_outcomes": False,
        "freeze_scope": "Three-model protocol fixed while training collection was in progress, before fitting and before any heldout collection; not claimed before all training outcomes existed.",
        "utility_formula": fit.UTILITY_FORMULA,
        "encounter": ENCOUNTER,
        "character": CHARACTER,
        "ascension": ASCENSION,
        "act_index": ACT_INDEX,
        "player_hp": PLAYER_HP,
        "holdout_case": {
            "name": HOLDOUT_CASE["name"],
            "scenario": HOLDOUT_CASE["scenario"],
            "choices": list(CHOICES),
            "expected_length": HOLDOUT_CASE["expected_length"],
        },
        "model_a": {
            "family": "mechanism_grid",
            "source": "scripts/fit_shiv_v1.py:params-trained.json",
            "defense_weights": list(fit.DEFENSE_WEIGHTS),
            "engine_weights": list(fit.ENGINE_WEIGHTS),
            "multipliers": list(fit.MULTIPLIERS),
            "refit": False,
            "selection": "shiv_prior_model.rank_choices(community=False)",
        },
        "model_b": {
            "family": "community_hybrid",
            "mechanism_params": "shiv_prior_model.DEFAULT_PARAMS (untrained)",
            "mechanism_community": False,
            "normalizer": "TRAIN-only global RMS of non-skip marginal scores",
            "rms_min": B_RMS_MIN,
            "lambda_grid": list(LAMBDAS),
            "status_penalty": B_STATUS_PENALTY,
            "elo_source": "work/shiv-score-audit/scores-a10.json",
            "elo_delta": "(A10_card_Elo - A10_SKIP_Elo) / 400",
            "missing_elo": "error",
            "tie_break": "lower lambda",
        },
        "model_c": {
            "family": "combat_ridge",
            "reference_params": "shiv_prior_model.DEFAULT_PARAMS (untrained)",
            "groups": {key: list(value) for key, value in C_GROUPS.items()},
            "intercept": False,
            "feature_scale": "TRAIN RMS per group (frozen)",
            "ridge_lambda": C_RIDGE_LAMBDA,
            "normal_equations": "(X'X + lambda*I) b = X'y",
            "solver": "stdlib 3x3 Gaussian elimination",
            "hyperparameter_search": False,
            "monotonic_guarantee": False,
            "causal_claim": False,
        },
        "train": {
            "run_root_source": "work/shiv-fit-v1 (newest non-empty journal, or --run-root)",
            "allowed_cases": list(TRAIN_NAMES),
            "holdout_forbidden_in_fit": True,
            "seeds": 200,
            "actions_per_case": 3,
            "cases": 3,
            "required_validated_records": 1800,
            "matched_rule": "all three actions eligible and complete win/loss per seed",
            "utility": fit.UTILITY_FORMULA,
        },
        "confirmation": {
            "replications": list(REPLICATIONS),
            "h_seeds": ["SHIVFITH0001", "SHIVFITH0200"],
            "r_seeds": ["SHIVFITR0001", "SHIVFITR0200"],
            "seeds_per_replication": 200,
            "family_comparisons": FAMILY_COMPARISONS,
            "alpha": ALPHA,
            "adjustment": "bonferroni_alpha_over_family_two_sided",
            "t_critical": "NormalDist.inv_cdf(1 - 0.05/(2*12)) + Cornish-Fisher df correction",
            "criterion": ("positive paired utility difference vs both baselines in both "
                          "replications and adjusted interval lower > 0"),
            "same_action": "exact zero -> indistinguishable (not failure, not confirmed)",
            "interval_crosses_zero": "inconclusive",
            "negative": "regression",
            "optional_stopping": False,
            "max_new_battles": MAX_NEW_BATTLES,
            "deadline_seconds": CONFIRM_DEADLINE_SECONDS,
            "caps": {
                "parallelism": PARALLELISM,
                "dop": 2,
                "nodes": 6000,
                "soft_budget_ms": 120000,
                "search_timeout_ms": 180000,
                "max_turns": 40,
                "per_process_seconds": 300,
                "retries": 0,
            },
        },
        "sources": _sources(),
    }


# --------------------------------------------------------------------------- #
# Train-only snapshot (never reads holdout)
# --------------------------------------------------------------------------- #

def train_only_records(records) -> tuple:
    """Filter to frozen train case names only; return (kept, excluded_names)."""
    kept, excluded = [], set()
    for record in records:
        name = record.get("case")
        if name in TRAIN_NAMES:
            kept.append(record)
        elif name:
            excluded.add(name)
    return kept, sorted(excluded)


def build_train_snapshot(records) -> dict:
    """Validate the train data and build the frozen snapshot.

    Heldout case names are dropped before any matching (mandatory frozen-name
    filter). A completed snapshot requires 3 cases x 200 seeds x 3 actions = 1800
    validated records; errors/timeouts/missing are never imputed as losses.
    """
    if any(record.get("case") in HOLDOUT_NAMES for record in records):
        # The filter below removes them, but a fit path must never consume them.
        kept, excluded = train_only_records(records)
    else:
        kept, excluded = train_only_records(records)
    grouped = fit.records_by_case_seed(kept)
    cases, case_counts, validated = [], {}, 0
    for spec in fit.TRAIN_CASES:
        matched = fit.matched_seed_utilities(spec, grouped.get(spec["name"], {}))
        case_counts[spec["name"]] = len(matched)
        validated += len(matched) * len(spec["choices"])
        cases.append({"spec": spec, "matched": matched})
    complete = all(count == len(fit.TRAIN_SEEDS) for count in case_counts.values())
    snapshot_records = []
    for record in sorted(kept, key=lambda r: (r.get("case") or "", r.get("seed") or "",
                                              CHOICES.index(r.get("action")) if r.get("action") in CHOICES else 9)):
        snapshot_records.append({
            "case": record.get("case"),
            "seed": record.get("seed"),
            "action": record.get("action"),
            "eligible": bool(record.get("eligible")),
            "outcome_status": record.get("outcome_status"),
            "player_hp": record.get("player_hp"),
            "enemy_hp": record.get("enemy_hp"),
        })
    snapshot = {
        "schema": "shiv-three-models-train-snapshot",
        "created_at_utc": utc_now(),
        "allowed_cases": list(TRAIN_NAMES),
        "excluded_case_names": excluded,
        "holdout_read": False,
        "required_records": 1800,
        "validated_records": validated,
        "case_seed_counts": case_counts,
        "complete": complete,
        "records": snapshot_records,
        "cases": cases,
        "snapshot_sha256": None,
    }
    snapshot["snapshot_sha256"] = fit.canonical_sha(
        {key: value for key, value in snapshot.items() if key != "cases"})
    return snapshot


# --------------------------------------------------------------------------- #
# Model A: original fit_shiv_v1 params (validated, reused)
# --------------------------------------------------------------------------- #

def identify_a_multipliers(params) -> tuple:
    """Recover the (defense, engine) multipliers from a validated params dict."""
    digest = fit.canonical_sha(params)
    if digest == fit.canonical_sha(m.DEFAULT_PARAMS):
        return 1.0, 1.0, "baseline_exact"
    for defense, engine in fit.DEFAULT_GRID:
        candidate = fit.apply_multipliers(m.DEFAULT_PARAMS, defense, engine)
        if fit.canonical_sha(candidate) == digest:
            return defense, engine, "grid_match"
    raise ParamValidationError("params do not match any fit_shiv_v1 grid candidate")


def _resolve_root(run_root) -> Path:
    path = Path(run_root)
    return path if path.is_absolute() else (ROOT / path)


def validate_a_params(run_root=None) -> dict:
    candidates = []
    if run_root is not None:
        candidates.append(_resolve_root(run_root) / "params-trained.json")
    candidates.append(FIT_PARAMS_FALLBACK)
    source = next((path for path in candidates if Path(path).exists()), None)
    if source is None:
        raise ParamValidationError(
            "no params-trained.json found under the fit run or work/shiv-fit-v1")
    params = load_json(source)
    defense, engine, how = identify_a_multipliers(params)
    params_sha = fit.canonical_sha(params)
    summary_path = None
    summary_sha = None
    for candidate in ([_resolve_root(run_root) / "train-summary.json"] if run_root else []) + [FIT_SUMMARY_FALLBACK]:
        if candidate is not None and Path(candidate).exists():
            summary = load_json(candidate)
            summary_path = candidate
            summary_sha = summary.get("params_sha256")
            if summary_sha is not None and summary_sha != params_sha:
                raise ParamValidationError(
                    "train-summary params_sha256 %s != params file %s" % (summary_sha, params_sha))
            selected = summary.get("selected") or {}
            if selected.get("defense") is not None:
                if (selected["defense"], selected["engine"]) != (defense, engine):
                    raise ParamValidationError(
                        "train-summary selected %r != recovered %r"
                        % ((selected["defense"], selected["engine"]), (defense, engine)))
            break
    return {
        "params": params,
        "params_sha256": params_sha,
        "defense": defense,
        "engine": engine,
        "recovered_by": how,
        "source": rel_path(source),
        "source_sha256": file_sha256(source),
        "train_summary": rel_path(summary_path) if summary_path else None,
        "train_summary_params_sha256": summary_sha,
    }


# --------------------------------------------------------------------------- #
# Model selection helpers (pure)
# --------------------------------------------------------------------------- #

def rank_mechanism(deck, choices, params) -> dict:
    """Mechanism-only ranking (community off), SKIP implicit at 0."""
    non_skip = [card for card in choices if card != "skip"]
    result = m.rank_choices(list(deck), candidates=non_skip, params=params, community=False)
    entries = [{"id": row["id"], "score": float(row["final_score"])}
               for row in result["candidates"]]
    entries.append({"id": "skip", "score": 0.0, "is_skip": True})
    entries.sort(key=lambda e: (-e["score"], 0 if e.get("is_skip") else 1, e["id"]))
    return {"ranking": [e["id"] for e in entries], "entries": entries,
            "selected": entries[0]["id"]}


def resolve_elo(card, a10) -> float:
    """A10 Elo for one card or SKIP; missing/null fails loudly (never zero)."""
    entry = a10.get(card)
    if not isinstance(entry, dict) or entry.get("elo") is None:
        raise MissingEloError("A10 Elo missing for %r" % card)
    return float(entry["elo"])


def elo_delta(card, a10) -> float:
    if card == "skip":
        return 0.0
    skip_elo = resolve_elo("SKIP", a10)
    return (resolve_elo(card, a10) - skip_elo) / 400.0


def rank_elo(choices, a10) -> dict:
    skip_elo = resolve_elo("SKIP", a10)
    entries = []
    for card in choices:
        if card == "skip":
            entries.append({"id": "skip", "score": skip_elo, "is_skip": True})
        else:
            entries.append({"id": card, "score": resolve_elo(card, a10)})
    entries.sort(key=lambda e: (-e["score"], 0 if e.get("is_skip") else 1, e["id"]))
    return {"ranking": [e["id"] for e in entries], "entries": entries,
            "selected": entries[0]["id"], "skip_elo": skip_elo}


def b_score(mechanism_norm: float, elo_delta_value: float, lam: float) -> float:
    return (1.0 - lam) * mechanism_norm + lam * elo_delta_value


def rank_b(deck, choices, rms, a10, lam) -> dict:
    """B hybrid ranking: normalized baseline mechanism mixed with Elo delta."""
    non_skip = [card for card in choices if card != "skip"]
    result = m.rank_choices(list(deck), candidates=non_skip,
                            params=m.DEFAULT_PARAMS, community=False)
    by_id = {row["id"]: row for row in result["candidates"]}
    entries = []
    for card in non_skip:
        norm = float(by_id[card]["mech_delta"]) / max(rms, B_RMS_MIN)
        score = b_score(norm, elo_delta(card, a10), lam)
        entries.append({"id": card, "score": score,
                        "mechanism_norm": norm, "elo_delta": elo_delta(card, a10)})
    entries.append({"id": "skip", "score": 0.0, "is_skip": True})
    entries.sort(key=lambda e: (-e["score"], 0 if e.get("is_skip") else 1, e["id"]))
    return {"ranking": [e["id"] for e in entries], "entries": entries,
            "selected": entries[0]["id"], "rms": rms, "lambda": lam}


def group_values(delta_components) -> dict:
    return {group: float(sum(delta_components.get(key, 0.0) for key in keys))
            for group, keys in C_GROUPS.items()}


def gauss_solve(matrix, vector) -> list:
    """Solve a small dense linear system with partial-pivot Gaussian elimination."""
    n = len(vector)
    work = [list(matrix[row]) + [float(vector[row])] for row in range(n)]
    for col in range(n):
        pivot = max(range(col, n), key=lambda row: abs(work[row][col]))
        if abs(work[pivot][col]) < 1e-15:
            raise ThreeModelsError("singular linear system")
        work[col], work[pivot] = work[pivot], work[col]
        divisor = work[col][col]
        for index in range(col, n + 1):
            work[col][index] /= divisor
        for row in range(n):
            if row == col:
                continue
            factor = work[row][col]
            if factor:
                for index in range(col, n + 1):
                    work[row][index] -= factor * work[col][index]
    return [work[row][n] for row in range(n)]


def ridge_solve(design, targets, lam=C_RIDGE_LAMBDA) -> list:
    """Fixed-lambda ridge normal equations (X'X + lam*I)b = X'y."""
    k = len(design[0])
    gram = [[sum(row[p] * row[q] for row in design) + (lam if p == q else 0.0)
             for q in range(k)] for p in range(k)]
    rhs = [sum(row[p] * y for row, y in zip(design, targets)) for p in range(k)]
    return gauss_solve(gram, rhs)


def grouped_training_rows(cases_with_means, a10) -> list:
    """Six (case, non-skip) rows with group features and meanU minus skip mean."""
    rows = []
    for item in cases_with_means:
        deck, choices = item["deck"], item["choices"]
        means = item["means"]
        non_skip = [card for card in choices if card != "skip"]
        result = m.rank_choices(list(deck), candidates=non_skip,
                                params=m.DEFAULT_PARAMS, community=False)
        by_id = {row["id"]: row for row in result["candidates"]}
        for card in non_skip:
            features = group_values(by_id[card]["delta_components"])
            rows.append({
                "case": item["name"],
                "action": card,
                "features": features,
                "y": means[card] - means["skip"],
            })
    return rows


def fit_ridge(rows) -> dict:
    """Fit C: scale features by TRAIN RMS, then fixed-lambda ridge."""
    keys = tuple(C_GROUPS)
    scales = {}
    for key in keys:
        values = [row["features"][key] for row in rows]
        rms = _rms(values)
        scales[key] = rms if rms > 1e-15 else 1.0
    design = [[row["features"][key] / scales[key] for key in keys] for row in rows]
    targets = [row["y"] for row in rows]
    coefficients = ridge_solve(design, targets, C_RIDGE_LAMBDA)
    return {
        "coefficients": {key: coefficients[index] for index, key in enumerate(keys)},
        "coefficient_vector": coefficients,
        "scales": scales,
        "ridge_lambda": C_RIDGE_LAMBDA,
        "rows": rows,
        "design": design,
    }


def rank_c(deck, choices, coefficients, scales) -> dict:
    non_skip = [card for card in choices if card != "skip"]
    result = m.rank_choices(list(deck), candidates=non_skip,
                            params=m.DEFAULT_PARAMS, community=False)
    by_id = {row["id"]: row for row in result["candidates"]}
    entries = []
    for card in non_skip:
        features = group_values(by_id[card]["delta_components"])
        score = sum(coefficients[key] * features[key] / scales[key] for key in C_GROUPS)
        entries.append({"id": card, "score": score, "features": features})
    entries.append({"id": "skip", "score": 0.0, "is_skip": True})
    entries.sort(key=lambda e: (-e["score"], 0 if e.get("is_skip") else 1, e["id"]))
    return {"ranking": [e["id"] for e in entries], "entries": entries,
            "selected": entries[0]["id"]}


def mechanism_marginals(deck, choices) -> dict:
    non_skip = [card for card in choices if card != "skip"]
    result = m.rank_choices(list(deck), candidates=non_skip,
                            params=m.DEFAULT_PARAMS, community=False)
    return {row["id"]: float(row["mech_delta"]) for row in result["candidates"]}


# --------------------------------------------------------------------------- #
# Fit (train only)
# --------------------------------------------------------------------------- #

def find_fit_run_root(explicit=None) -> Path:
    if explicit:
        root = Path(explicit)
        if not (root / "journal.jsonl").exists():
            raise ThreeModelsError("no journal.jsonl under %s" % root)
        return root
    if not FIT_WORK.exists():
        raise ThreeModelsError("work/shiv-fit-v1 does not exist")
    candidates = [child for child in sorted(FIT_WORK.iterdir())
                  if child.is_dir() and (child / "journal.jsonl").exists()
                  and (child / "journal.jsonl").stat().st_size > 0]
    if not candidates:
        raise ThreeModelsError("no run root with a non-empty journal.jsonl under work/shiv-fit-v1")
    return candidates[-1]


def fit_models(run_root, snapshot, a1) -> dict:
    """Compute A/B/C from the validated train snapshot; never reads holdout."""
    a10 = load_json(SCORES_A10)
    train_cases = []
    all_marginals = []
    for entry in snapshot["cases"]:
        spec = entry["spec"]
        deck = fit.deck_for_case(spec)
        means = fit.case_action_means(entry["matched"], spec)
        if any(means[action] is None for action in spec["choices"]):
            raise TrainingIncompleteError("case %r has an empty action mean" % spec["name"])
        train_cases.append({"name": spec["name"], "scenario": spec["scenario"],
                            "deck": deck, "choices": spec["choices"], "means": means})
        all_marginals.extend(mechanism_marginals(deck, spec["choices"]).values())

    rms = _rms(all_marginals)
    rms = max(rms, B_RMS_MIN)

    # ---- Model B: lambda grid on training mean measured utility ------------ #
    b_grid = []
    for lam in LAMBDAS:
        per_case = []
        for item in train_cases:
            selected = rank_b(item["deck"], item["choices"], rms, a10, lam)["selected"]
            per_case.append({
                "case": item["name"],
                "selected_action": selected,
                "measured_u": item["means"][selected],
            })
        raw_mean = _mean([entry["measured_u"] for entry in per_case])
        penalty = B_STATUS_PENALTY * lam * lam
        b_grid.append({"lambda": lam, "raw_mean_u": raw_mean, "penalty": penalty,
                       "objective": raw_mean - penalty, "per_case": per_case})
    b_best = min(b_grid, key=lambda entry: (-entry["objective"], entry["lambda"]))
    b_lambda = b_best["lambda"]

    # ---- Model C: ridge on the six training rows --------------------------- #
    c_rows = grouped_training_rows(train_cases, a10)
    c_fit = fit_ridge(c_rows)

    # ---- Frozen heldout-duplicates selections ------------------------------ #
    deck = fit.deck_for_case(HOLDOUT_CASE)
    models = {
        "A": rank_mechanism(deck, CHOICES, a1["params"]),
        "B": rank_b(deck, CHOICES, rms, a10, b_lambda),
        "C": rank_c(deck, CHOICES, c_fit["coefficients"], c_fit["scales"]),
    }
    baselines = {
        "default_model": rank_mechanism(deck, CHOICES, m.DEFAULT_PARAMS),
        "elo_with_skip": rank_elo(CHOICES, a10),
    }

    model_artifact = {
        "A": {
            "family": "mechanism_grid",
            "defense": a1["defense"],
            "engine": a1["engine"],
            "recovered_by": a1.get("recovered_by"),
            "params": a1["params"],
            "params_sha256": a1.get("params_sha256") or fit.canonical_sha(a1["params"]),
            "params_source": a1.get("source"),
            "params_source_sha256": a1.get("source_sha256"),
            "ranking": models["A"]["ranking"],
            "entries": models["A"]["entries"],
            "selected": models["A"]["selected"],
        },
        "B": {
            "family": "community_hybrid",
            "rms": rms,
            "lambda": b_lambda,
            "objective": b_best["objective"],
            "raw_mean_u": b_best["raw_mean_u"],
            "penalty": b_best["penalty"],
            "lambda_grid": b_grid,
            "elo_skip": resolve_elo("SKIP", a10),
            "ranking": models["B"]["ranking"],
            "entries": models["B"]["entries"],
            "selected": models["B"]["selected"],
        },
        "C": {
            "family": "combat_ridge",
            "groups": {key: list(value) for key, value in C_GROUPS.items()},
            "coefficients": c_fit["coefficients"],
            "coefficient_vector": c_fit["coefficient_vector"],
            "scales": c_fit["scales"],
            "ridge_lambda": c_fit["ridge_lambda"],
            "training_rows": c_rows,
            "ranking": models["C"]["ranking"],
            "entries": models["C"]["entries"],
            "selected": models["C"]["selected"],
        },
    }
    frozen = {name: models[name]["selected"] for name in MODEL_IDS}
    frozen.update({name: baselines[name]["selected"] for name in BASELINE_IDS})
    return {
        "models": model_artifact,
        "baselines": baselines,
        "training": {
            "rms": rms,
            "marginal_count": len(all_marginals),
            "case_action_means": {item["name"]: item["means"] for item in train_cases},
            "b_lambda_grid": b_grid,
            "c_rows": c_rows,
        },
        "frozen_holdout_selection": frozen,
    }


def run_fit(run_root=None, out_dir=None) -> int:
    if not PROTOCOL_PATH.exists():
        raise ThreeModelsError("protocol.json is not frozen yet; run --prepare first")
    protocol = load_json(PROTOCOL_PATH)
    resolved_root = find_fit_run_root(run_root)
    records = fit.load_journal(resolved_root / "journal.jsonl")
    snapshot = build_train_snapshot(records)
    out_dir = Path(out_dir) if out_dir else MODELS_DIR
    out_dir.mkdir(parents=True, exist_ok=True)

    train_snapshot_path = write_json(out_dir / "train-snapshot.json", {
        key: value for key, value in snapshot.items() if key != "cases"})
    if not snapshot["complete"]:
        summary = {
            "schema": "shiv-three-models", "status": "training_incomplete",
            "reason": ("train snapshot has %d/%d validated records"
                       % (snapshot["validated_records"], snapshot["required_records"])),
            "run_root": rel_path(resolved_root),
            "case_seed_counts": snapshot["case_seed_counts"],
            "excluded_case_names": snapshot["excluded_case_names"],
        }
        write_json(out_dir / "fit-summary.json", summary)
        print(json.dumps(summary, ensure_ascii=False), flush=True)
        return 3

    a1 = validate_a_params(resolved_root)
    result = fit_models(resolved_root, snapshot, a1)
    a10 = load_json(SCORES_A10)

    models_payload = {
        "schema": "shiv-three-models",
        "schema_version": 1,
        "fitted_at_utc": utc_now(),
        "protocol_sha256": file_sha256(PROTOCOL_PATH),
        "fit_run_root": rel_path(resolved_root),
        "fit_journal_sha256": file_sha256(resolved_root / "journal.jsonl"),
        "train_snapshot_path": rel_path(train_snapshot_path),
        "train_snapshot_sha256": file_sha256(train_snapshot_path),
        "holdout_read": False,
        "utility_formula": fit.UTILITY_FORMULA,
        "train_sources": _sources(),
        "train": {
            "allowed_cases": list(TRAIN_NAMES),
            "validated_records": snapshot["validated_records"],
            "case_seed_counts": snapshot["case_seed_counts"],
            "rms": result["training"]["rms"],
        },
        "A": result["models"]["A"],
        "B": result["models"]["B"],
        "C": result["models"]["C"],
        "baselines": result["baselines"],
        "frozen_holdout_selection": result["frozen_holdout_selection"],
        "notes": [
            "A reuses validated original fit_shiv_v1 params; it is never refit.",
            "B normalizes the untrained mechanism marginal by the TRAIN-only global RMS.",
            "C coefficients may be negative; no monotonicity or causal claim.",
            "Only the frozen train cases enter the fit; holdout_read is false.",
        ],
    }
    write_json(out_dir / "models.json", models_payload)
    write_json(out_dir / "model-a.json", result["models"]["A"])
    write_json(out_dir / "model-b.json", result["models"]["B"])
    write_json(out_dir / "model-c.json", result["models"]["C"])
    write_json(out_dir / "fit-summary.json", {
        "schema": "shiv-three-models", "status": "ok",
        "run_root": rel_path(resolved_root),
        "validated_records": snapshot["validated_records"],
        "rms": result["training"]["rms"],
        "B_lambda": result["models"]["B"]["lambda"],
        "A_defense": result["models"]["A"]["defense"],
        "A_engine": result["models"]["A"]["engine"],
        "frozen_holdout_selection": result["frozen_holdout_selection"],
        "models_path": rel_path(out_dir / "models.json"),
        "frozen_at_utc": models_payload["fitted_at_utc"],
    })
    write_fit_doc(models_payload)
    print(json.dumps({
        "status": "ok",
        "run_root": rel_path(resolved_root),
        "validated_records": snapshot["validated_records"],
        "A": {"defense": result["models"]["A"]["defense"],
              "engine": result["models"]["A"]["engine"],
              "selected": result["models"]["A"]["selected"]},
        "B": {"lambda": result["models"]["B"]["lambda"],
              "selected": result["models"]["B"]["selected"]},
        "C": {"selected": result["models"]["C"]["selected"]},
        "frozen_holdout_selection": result["frozen_holdout_selection"],
        "models_path": rel_path(out_dir / "models.json"),
    }, ensure_ascii=False), flush=True)
    return 0


# --------------------------------------------------------------------------- #
# Confirmation analysis (pure)
# --------------------------------------------------------------------------- #

def build_replication(records, case) -> dict:
    """Matched-seed view of one replication (all three actions eligible/complete)."""
    grouped = fit.records_by_case_seed(records)
    per_seed = grouped.get(case["name"], {})
    matched = fit.matched_seed_utilities(case, per_seed)
    kept = {seed: {action: per_seed[seed][action] for action in case["choices"]}
            for seed in matched}
    return {"case": case["name"], "matched": matched, "records": kept, "n": len(matched)}


def paired_comparison(matched, model_action, baseline_action, family=FAMILY_COMPARISONS) -> dict:
    """Paired mean U(model action) - U(baseline action) over matched seeds."""
    seeds = sorted(matched)
    diffs = [float(matched[seed][model_action]) - float(matched[seed][baseline_action])
             for seed in seeds]
    same = model_action == baseline_action
    stats = add_adjusted_interval(fit.summary_stats(diffs), family)
    if same:
        status, confirmed = "indistinguishable_same_action", False
    elif stats.get("n", 0) < 25 or stats.get("mean") is None:
        status, confirmed = "insufficient_n", False
    elif stats.get("adjusted_ci_low") is not None and stats["adjusted_ci_low"] > 0.0:
        status, confirmed = "positive_adjusted", True
    elif stats.get("adjusted_ci_high") is not None and stats["adjusted_ci_high"] < 0.0:
        status, confirmed = "regression", False
    else:
        status, confirmed = "inconclusive", False
    return {
        "model_action": model_action,
        "baseline_action": baseline_action,
        "same_action": same,
        "n": stats.get("n"),
        "seeds": seeds,
        "differences": diffs,
        "stats": stats,
        "status": status,
        "confirmed": confirmed,
    }


def raw_action_stats(rep, action) -> dict:
    records = [rep["records"][seed][action] for seed in sorted(rep["matched"])]
    wins = sum(1 for record in records if record.get("outcome_status") == "win")
    player = [float(record.get("player_hp") or 0.0) if record.get("outcome_status") == "win"
              else 0.0 for record in records]
    enemy = [float(record.get("enemy_hp") or 0.0) if record.get("outcome_status") == "loss"
             else 0.0 for record in records]
    return {"action": action, "n": len(records), "wins": wins,
            "win_fraction": (wins / len(records)) if records else None,
            "winrate_wilson95": fit.wilson95(wins, len(records)),
            "effective_player_hp": fit.summary_stats(player),
            "enemy_hp": fit.summary_stats(enemy)}


def combined_comparison(replications, model_action, baseline_action) -> dict:
    pooled = []
    for rep_id in REPLICATIONS:
        rep = replications[rep_id]
        pooled.extend(float(rep["matched"][seed][model_action])
                      - float(rep["matched"][seed][baseline_action])
                      for seed in sorted(rep["matched"]))
    stats = add_adjusted_interval(fit.summary_stats(pooled), FAMILY_COMPARISONS)
    return {"model_action": model_action, "baseline_action": baseline_action,
            "n": stats.get("n"), "stats": stats, "differences": pooled}


def analyze_confirmation(replications, selections) -> dict:
    comparisons = []
    for model_id in MODEL_IDS:
        for baseline_id in BASELINE_IDS:
            for rep_id in REPLICATIONS:
                rep = replications[rep_id]
                comparison = paired_comparison(rep["matched"], selections[model_id],
                                               selections[baseline_id])
                comparison.update({
                    "model": model_id,
                    "baseline": baseline_id,
                    "replication": rep_id,
                    "raw": {
                        "model": raw_action_stats(rep, selections[model_id]),
                        "baseline": raw_action_stats(rep, selections[baseline_id]),
                    },
                })
                comparisons.append(comparison)
    verdicts = {}
    for model_id in MODEL_IDS:
        cells = [c for c in comparisons if c["model"] == model_id]
        verdicts[model_id] = {
            "confirmed": len(cells) == 4 and all(c["confirmed"] and c["n"] == 200 for c in cells),
            "comparisons": len(cells),
            "positive": sum(1 for c in cells if c["status"] == "positive_adjusted"),
            "indistinguishable": sum(1 for c in cells
                                     if c["status"] == "indistinguishable_same_action"),
            "inconclusive": sum(1 for c in cells if c["status"] == "inconclusive"),
            "regression": sum(1 for c in cells if c["status"] == "regression"),
            "statuses": {("%s_vs_%s_%s" % (c["baseline"], c["replication"], model_id)): c["status"]
                         for c in cells},
        }
    combined = []
    for model_id in MODEL_IDS:
        for baseline_id in BASELINE_IDS:
            combined.append({"model": model_id, "baseline": baseline_id,
                             **combined_comparison(replications, selections[model_id],
                                                   selections[baseline_id])})
    return {
        "family_comparisons": FAMILY_COMPARISONS,
        "alpha_each": ALPHA / FAMILY_COMPARISONS,
        "adjusted_normal_z": adjusted_normal_z(),
        "comparisons": comparisons,
        "model_verdicts": verdicts,
        "combined_secondary": combined,
        "stable_confirmation": all(verdicts[model_id]["confirmed"] for model_id in MODEL_IDS),
    }


# --------------------------------------------------------------------------- #
# Confirmation execution
# --------------------------------------------------------------------------- #

def build_holdout_deck(prior, char_set, colorless_set) -> dict:
    case = HOLDOUT_CASE
    cards = fit.deck_for_case(case)
    if len(cards) != case["expected_length"]:
        raise ThreeModelsError("holdout deck length %d != %d" % (len(cards), case["expected_length"]))
    char, colorless, unsupported = [], [], []
    bane = 0
    for card_id in cards:
        if card_id == "ASCENDERS_BANE":
            bane += 1
            continue
        if card_id in char_set:
            char.append((card_id, 0))
        elif card_id in colorless_set:
            colorless.append((card_id, 0))
        else:
            unsupported.append(card_id)
    if bane != 1:
        raise ThreeModelsError("holdout deck must contain exactly one ASCENDERS_BANE")
    if unsupported:
        raise ThreeModelsError("holdout deck has unsupported cards: %s" % unsupported)
    fixture = [{"id": card, "upgrade": level} for card, level in char + colorless]
    fixture.append({"id": "ASCENDERS_BANE", "upgrade": 0})
    pairs = [(card, 0) for card in cards]
    return {
        "id": case["name"],
        "source": "scripts/shiv_prior_model.py:SCENARIOS:%s" % case["scenario"],
        "cards": pairs,
        "char": char,
        "colorless": colorless,
        "unsupported": unsupported,
        "deck_sha256": prior.canonical_sha([list(pair) for pair in pairs]),
        "fixture": {"deck": fixture, "candidates": [c for c in CHOICES if c != "skip"]},
        "full_deck": list(cards),
        "deck_length": len(cards),
        "bane_count": bane,
    }


def make_confirm_tasks(prior, deck, run_root) -> list:
    tasks = []
    for seed in R_SEEDS:
        for action in CHOICES:
            task = prior.make_task(deck, ENCOUNTER, seed, action, "confirm")
            task["dir"] = run_root / "runs" / action / task["name"]
            task["action"] = action
            task["case"] = HOLDOUT_CASE["name"]
            tasks.append(task)
    if len(tasks) != MAX_NEW_BATTLES or len({t["name"] for t in tasks}) != MAX_NEW_BATTLES:
        raise ThreeModelsError("confirm task list is not %d unique tasks" % MAX_NEW_BATTLES)
    return tasks


def run_one(prior, task, ctx, run_root) -> dict:
    target = Path(task["dir"])
    if not within(target, run_root) or target.exists():
        record = prior.new_record(task)
        record.update(status="error", launched=False,
                      reason="target outside the fresh confirm root or already exists")
        return record
    return prior.execute(task, ctx)


def write_progress(run_root, payload) -> None:
    payload = dict(payload)
    payload["updated_at_utc"] = utc_now()
    write_json(Path(run_root) / "progress.json", payload)


def run_confirm_battles(prior, tasks, deck, run_root, journal_path) -> dict:
    offset = max(0, int(prior.DEADLINE_SECONDS) - CONFIRM_DEADLINE_SECONDS)
    ctx = {"decks": {deck["id"]: deck}, "records": {}, "done": set(),
           "lock": threading.Lock(), "active": 0, "peak": 0,
           "start": time.monotonic() - offset, "clock": time.monotonic}
    launched = 0
    systemic_abort = False
    abort_reason = None
    completions = 0
    first_records = []
    with ThreadPoolExecutor(max_workers=PARALLELISM) as pool:
        futures = {pool.submit(run_one, prior, task, ctx, run_root): task for task in tasks}
        for future in as_completed(futures):
            task = futures[future]
            try:
                record = future.result()
            except CancelledError:
                record = prior.new_record(task)
                record.update(status="not_started", launched=False,
                              reason="aborted after systematic first-16 failure (unstarted)")
            except BaseException as exc:  # noqa: BLE001 - the journal keeps failed attempts
                record = prior.new_record(task)
                record.update(status="error", launched=None,
                              reason="future failed: %s: %s" % (type(exc).__name__, exc))
            record["action"] = task["action"]
            record["case"] = HOLDOUT_CASE["name"]
            record["dir"] = str(task["dir"])
            ctx["records"][task["name"]] = record
            if record.get("launched"):
                launched += 1
                ctx["done"].add(task["name"])
            fit_parent_append(journal_path, record)
            completions += 1
            if completions <= 16:
                first_records.append(record)
                if completions == 16 and all(not r.get("eligible") for r in first_records):
                    systemic_abort = True
                    abort_reason = "all first 16 completions are non-eligible; aborting unstarted"
                    for pending in list(futures):
                        pending.cancel()
            if completions % 20 == 0 or completions == len(tasks):
                write_progress(run_root, {
                    "completed": completions, "total": len(tasks), "launched": launched,
                    "eligible": sum(1 for r in ctx["records"].values() if r.get("eligible")),
                    "active": ctx["active"], "peak_parallelism": ctx["peak"],
                    "elapsed_seconds": ctx["clock"]() - ctx["start"] - offset,
                    "systemic_abort": systemic_abort, "abort_reason": abort_reason,
                })
                print("[progress] %d/%d completions launched=%d elapsed=%.1fs"
                      % (completions, len(tasks), launched,
                         ctx["clock"]() - ctx["start"] - offset), flush=True)
    return {"records": ctx["records"], "launched": launched, "peak": ctx["peak"],
            "wall_seconds": ctx["clock"]() - ctx["start"] - offset,
            "systemic_abort": systemic_abort, "abort_reason": abort_reason}


def fit_parent_append(path, record) -> None:
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, ensure_ascii=False, sort_keys=True) + "\n")


def find_incomplete_confirm_run() -> Path | None:
    if not MODELS_DIR.exists():
        return None
    candidates = []
    for child in sorted(MODELS_DIR.iterdir()):
        if not child.is_dir():
            continue
        if ((child / "start-manifest.json").exists() and (child / "journal.jsonl").exists()
                and not (child / "summary.json").exists()
                and (child / "journal.jsonl").stat().st_size > 0):
            candidates.append(child)
    return candidates[-1] if candidates else None


def load_h_replication(run_root) -> tuple:
    start_manifest = load_json(Path(run_root) / "start-manifest.json")
    records = fit.load_journal(Path(run_root) / "journal.jsonl")
    train_records, _ = train_only_records(records)
    holdout_records = [r for r in records if r.get("case") == HOLDOUT_CASE["name"]]
    replication = build_replication(holdout_records, HOLDOUT_CASE)
    return replication, start_manifest


def verify_frozen_selection(models, a1, b_result, c_result, a10) -> dict:
    deck = fit.deck_for_case(HOLDOUT_CASE)
    selections = {
        "A": rank_mechanism(deck, CHOICES, a1["params"])["selected"],
        "B": rank_b(deck, CHOICES, b_result["rms"], a10, b_result["lambda"])["selected"],
        "C": rank_c(deck, CHOICES, c_result["coefficients"], c_result["scales"])["selected"],
        "default_model": rank_mechanism(deck, CHOICES, m.DEFAULT_PARAMS)["selected"],
        "elo_with_skip": rank_elo(CHOICES, a10)["selected"],
    }
    frozen = models["frozen_holdout_selection"]
    if selections != {key: frozen[key] for key in selections}:
        raise ThreeModelsError("recomputed selections %r != frozen %r" % (selections, frozen))
    return selections


def finalize_confirm(run_root, r_start_manifest, models, a1, b_result, c_result, a10,
                     r_replication, runtime_before, runtime_after, adapter,
                     report_only=False, h_replication=None) -> int:
    if h_replication is None:
        raise ThreeModelsError("confirmation H replication is required")
    selections = verify_frozen_selection(models, a1, b_result, c_result, a10)
    replications = {"H": h_replication, "R": r_replication}
    analysis = analyze_confirmation(replications, selections)

    summary = {
        "schema": "shiv-three-models-confirm",
        "generated_at_utc": utc_now(),
        "report_only": report_only,
        "models_path": rel_path(MODELS_PATH),
        "protocol_sha256": file_sha256(PROTOCOL_PATH),
        "holdout_read_by_fit": bool(models.get("holdout_read", False)),
        "selections": selections,
        "replications": {
            "H": {"seeds": "SHIVFITH", "n": replications["H"]["n"]},
            "R": {"seeds": "SHIVFITR", "n": replications["R"]["n"]},
        },
        "caps": {
            "parallelism": PARALLELISM, "dop": 2, "nodes": 6000,
            "soft_budget_ms": 120000, "search_timeout_ms": 180000, "max_turns": 40,
            "per_process_seconds": 300, "deadline_seconds": CONFIRM_DEADLINE_SECONDS,
            "max_new_battles": MAX_NEW_BATTLES, "retries": 0,
        },
        "runtime_before": runtime_before,
        "runtime_after": runtime_after,
        "runtime_stable": runtime_before == runtime_after,
        "analysis": analysis,
    }
    write_json(run_root / "summary.json", summary)
    write_json(CONFIRM_SUMMARY_PATH, summary)
    write_confirm_doc(summary)
    print(json.dumps({
        "run_root": rel_path(run_root),
        "report_only": report_only,
        "H_n": replications["H"]["n"],
        "R_n": replications["R"]["n"],
        "stable_confirmation": analysis["stable_confirmation"],
        "model_verdicts": analysis["model_verdicts"],
        "report": rel_path(REPORT),
    }, ensure_ascii=False, default=str), flush=True)
    return 0


def run_confirm(run_root=None) -> int:
    if not MODELS_PATH.exists():
        raise ThreeModelsError("models.json not found; run --fit first")
    models = load_json(MODELS_PATH)
    if models.get("holdout_read"):
        raise HoldoutAccessError("models.json reports holdout_read=true; refusing to confirm")
    a10 = load_json(SCORES_A10)
    a1 = validate_a_params(models.get("fit_run_root"))
    b_result = {"rms": models["B"]["rms"], "lambda": models["B"]["lambda"]}
    c_result = {"coefficients": models["C"]["coefficients"], "scales": models["C"]["scales"]}

    fit_run_root = Path(run_root) if run_root else (ROOT / models["fit_run_root"])
    h_replication, h_start = load_h_replication(fit_run_root)
    if h_replication["n"] != len(H_SEEDS):
        raise ThreeModelsError(
            "replication H has %d/%d matched seeds; run --confirm after the original "
            "holdout is complete" % (h_replication["n"], len(H_SEEDS)))

    prior = load_module(BATCH / "run_batch.py", "three_prior_batch")
    for key, value in EXPECTED_PRIOR.items():
        if getattr(prior, key, "<missing>") != value:
            raise ThreeModelsError("prior %s != frozen %r" % (key, value))
    from importlib import import_module
    bpi = import_module("building_pick_input_probe")
    brp = import_module("building_repeat_probe")
    ranking = import_module("building_pick_ranking_screen")
    adapter = bpi.load_source(bpi.SOURCE_DIR)
    runtime_config = brp.HARNESS.parent / "OfflineSearchHarness.runtimeconfig.json"
    runtime_before = ranking.collect_runtime_provenance(runtime_config, adapter["adapter"],
                                                        env=os.environ)
    if h_start.get("runtime_before") != runtime_before:
        raise ThreeModelsError("runtime provenance differs from the first batch; aborting")

    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    try:
        ranking.create_exclusive_file(LOCK_PATH)
    except FileExistsError:
        raise SystemExit("batch lock already exists at %s; refusing to run" % rel_path(LOCK_PATH))
    try:
        existing = find_incomplete_confirm_run()
        if existing is not None:
            print("[resume] report-only aggregation of %s (no new battles)" % rel_path(existing),
                  flush=True)
            r_records = fit.load_journal(existing / "journal.jsonl")
            return _finalize_from_journal(models, a1, b_result, c_result, a10,
                                          h_replication, r_records, existing,
                                          runtime_before, runtime_before, adapter,
                                          report_only=True)
        if CONFIRM_SUMMARY_PATH.exists():
            print("[skip] confirm-summary.json already exists; no relaunch", flush=True)
            return 0

        char_set, colorless_set, catalog = prior.load_catalog()
        deck = build_holdout_deck(prior, char_set, colorless_set)
        run_dir = MODELS_DIR / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
        run_dir.mkdir(parents=True, exist_ok=False)
        (run_dir / "runs").mkdir()
        for action in CHOICES:
            (run_dir / "runs" / action).mkdir()
        tasks = make_confirm_tasks(prior, deck, run_dir)
        for task in tasks:
            target = Path(task["dir"])
            if not within(target, run_dir) or target.exists():
                raise ThreeModelsError("target %s outside fresh root or already exists" % target)

        start_manifest = {
            "generated_at_utc": utc_now(),
            "tool": "scripts/shiv_three_models.py",
            "replication": "R",
            "seeds": list(R_SEEDS),
            "choices": list(CHOICES),
            "encounter": ENCOUNTER, "character": CHARACTER, "ascension": ASCENSION,
            "act_index": ACT_INDEX, "player_hp": PLAYER_HP,
            "deck": {"full_deck": deck["full_deck"], "deck_length": deck["deck_length"],
                     "bane_count": deck["bane_count"], "deck_sha256": deck["deck_sha256"],
                     "fixture_deck": deck["fixture"]["deck"], "candidates": list(CHOICES[:-1])},
            "catalog": catalog,
            "runtime_before": runtime_before,
            "h_run_root": rel_path(fit_run_root),
            "caps": {"parallelism": PARALLELISM, "dop": 2, "nodes": 6000,
                     "soft_budget_ms": 120000, "search_timeout_ms": 180000, "max_turns": 40,
                     "per_process_seconds": 300, "deadline_seconds": CONFIRM_DEADLINE_SECONDS,
                     "max_new_battles": MAX_NEW_BATTLES, "retries": 0},
            "total_tasks": len(tasks),
            "tasks": [{"name": t["name"], "seed": t["seed"], "action": t["action"],
                       "dir": str(t["dir"])} for t in tasks],
        }
        write_json(run_dir / "start-manifest.json", start_manifest)
        journal_path = ranking.create_exclusive_file(run_dir / "journal.jsonl")
        print("[run] %d battles into %s" % (len(tasks), rel_path(run_dir)), flush=True)
        control = run_confirm_battles(prior, tasks, deck, run_dir, journal_path)

        runtime_after = ranking.collect_runtime_provenance(runtime_config, adapter["adapter"],
                                                           env=os.environ)
        r_records = list(control["records"].values())
        return _finalize_from_journal(models, a1, b_result, c_result, a10,
                                      h_replication, r_records, run_dir,
                                      runtime_before, runtime_after, adapter,
                                      report_only=False)
    finally:
        try:
            LOCK_PATH.unlink()
        except OSError:
            pass


def _finalize_from_journal(models, a1, b_result, c_result, a10, h_replication, r_records,
                           run_root, runtime_before, runtime_after, adapter,
                           report_only=False) -> int:
    r_replication = build_replication(r_records, HOLDOUT_CASE)
    r_start = load_json(run_root / "start-manifest.json")
    return finalize_confirm(run_root, r_start, models, a1, b_result, c_result, a10,
                            r_replication, runtime_before, runtime_after, adapter,
                            report_only=report_only, h_replication=h_replication)


# --------------------------------------------------------------------------- #
# Documentation
# --------------------------------------------------------------------------- #

def _f(value, nd=4):
    if value is None:
        return "N/A"
    if isinstance(value, float):
        return "%.*f" % (nd, value)
    return str(value)


def write_protocol_doc(protocol) -> None:
    lines = [
        "# SHIV_THREE_MODELS — 三种独立模型家族与确认协议", "",
        "生成时间: %s  " % protocol["created_at_utc"],
        "状态: 三模型协议在训练数据收集期间、拟合与留出采集之前固定；不声称早于全部训练结果生成。`--fit` 与 `--confirm` 由 owner 依次执行。", "",
        "## 冻结的三个模型", "",
        "- **A 机制**：复用 `fit_shiv_v1` 训练得出的 defense/engine 两乘子；不重新拟合，不读取 heldout。  ",
        "- **B 社区混合**：未训练机制边际 / 训练集非 skip 边际全局 RMS（下限 1e-9），"
        "与 `(A10卡Elo - A10_SKIP_Elo)/400` 以 λ 混合，λ∈[0,.25,.5,.75,1]，"
        "选择目标为训练均值实测效用减 `0.002*λ²`，同分取较小 λ；Elo 缺失直接报错。  ",
        "- **C 战斗岭回归**：以基线模型的三组组件增量（offense/defense/engine）"
        "无截距预测 `meanU(牌组+卡) - meanU(跳过)`；特征按训练 RMS 缩放冻结，"
        "固定 λ=1 解 `(X'X+I)b=X'y`，纯标准库 3x3 高斯消元；系数可为负，"
        "不声明单调性或因果。  ", "",
        "## 训练数据", "",
        "- 仅读取 `work/shiv-fit-v1` 最新 journal 中的冻结训练场景 "
        "`train-producers` / `train-payoffs` / `train-defense`；heldout 记录在拟合前被强制过滤。  ",
        "- 要求 3 场景 × 200 seed × 3 动作 = 1800 条通过校验的记录；错误/超时/缺失永不算败场。  ",
        "- 效用固定为 `%s`。  " % protocol["utility_formula"], "",
        "## 确认", "",
        "- 复制 H：原始 heldout SHIVFITH0001..0200（仅当 `--fit` 未读 heldout）。  ",
        "- 复制 R：新跑 SHIVFITR0001..0200，同一 heldout 牌组与三个动作，"
        "最多 600 场新战斗，单次 1200s 截止，N16 dop2 nodes6000 budget120000 "
        "searchTimeout180000 maxturn40 process300，无重试。  ",
        "- 12 项比较家族（3 模型 × 2 基线 × 2 复制），每项双侧 α=.05/12，"
        "临界值由 `NormalDist.inv_cdf(1-.05/24)` 加 Cornish-Fisher 自由度修正。  ",
        "- 稳定确认要求：每个模型对两个基线在 H 与 R 均为正，且校正区间下界 > 0；"
        "同动作差恒为 0，标记为不可区分；跨 0 为不确定；为负为回归。  ", "",
        "来源哈希:", "",
    ]
    for key, entry in protocol["sources"].items():
        lines.append("- `%s` `%s` (`%s`)" % (key, entry["sha256"], entry["path"]))
    lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_fit_doc(models) -> None:
    lines = [
        "# SHIV_THREE_MODELS — 拟合结果", "",
        "生成时间: %s  " % models["fitted_at_utc"],
        "heldout 读取: `%s`  " % models["holdout_read"],
        "训练有效记录: %s  " % models["train"]["validated_records"],
        "训练 RMS: %.6f  " % models["train"]["rms"], "",
        "## 冻结的 heldout 选择（KNIFE_TRAP / ADRENALINE / skip）", "",
        "| 模型 | 动作 | 排名 |", "| --- | --- | --- |",
    ]
    for key in MODEL_IDS:
        lines.append("| %s | %s | %s |" % (key, models[key]["selected"],
                                           " > ".join(models[key]["ranking"])))
    for key in BASELINE_IDS:
        lines.append("| %s (基线) | %s | %s |" % (key, models["baselines"][key]["selected"],
                                                   " > ".join(models["baselines"][key]["ranking"])))
    lines += ["", "## B λ 网格", "",
              "| λ | 原始均值 U | 惩罚 | 目标 |", "| --- | --- | --- | --- |"]
    for entry in models["B"]["lambda_grid"]:
        lines.append("| %s | %s | %s | %s |" % (_f(entry["lambda"]), _f(entry["raw_mean_u"]),
                                                 _f(entry["penalty"]), _f(entry["objective"])))
    lines += ["", "## C 系数（已缩放空间）", "",
              "| 组 | 系数 | 训练 RMS |", "| --- | --- | --- |"]
    for key in C_GROUPS:
        lines.append("| %s | %s | %s |" % (key, _f(models["C"]["coefficients"][key]),
                                           _f(models["C"]["scales"][key])))
    lines += ["", "C 系数可为负；无单调性保证，也不代表因果组件权重。  "]
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_confirm_doc(summary) -> None:
    analysis = summary["analysis"]
    lines = [
        "# SHIV_THREE_MODELS — 确认结果", "",
        "生成时间: %s  " % summary["generated_at_utc"],
        "报告模式（无新战斗）: `%s`  " % summary["report_only"],
        "H 匹配 seed 数: %s  " % summary["replications"]["H"]["n"],
        "R 匹配 seed 数: %s  " % summary["replications"]["R"]["n"],
        "家族比较数: %s，每项 α=%.6f，调整临界 z=%.4f。"
        % (analysis["family_comparisons"], analysis["alpha_each"], analysis["adjusted_normal_z"]),
        "",
        "## 每个模型的 12 项比较", "",
        "| 模型 | 基线 | 复制 | 模型动作 | 基线动作 | n | 均值差 | 普通95%CI | 校正CI | 状态 |",
        "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    for comparison in analysis["comparisons"]:
        stats = comparison["stats"]
        lines.append(
            "| %s | %s | %s | %s | %s | %s | %s | [%s, %s] | [%s, %s] | %s |"
            % (comparison["model"], comparison["baseline"], comparison["replication"],
               comparison["model_action"], comparison["baseline_action"], comparison["n"],
               _f(stats.get("mean")), _f(stats.get("ci95_low")), _f(stats.get("ci95_high")),
               _f(stats.get("adjusted_ci_low")), _f(stats.get("adjusted_ci_high")),
               comparison["status"]))
    lines += ["", "## 模型裁决", "",
              "| 模型 | 确认 | 正 | 不可区分 | 不确定 | 回归 |",
              "| --- | --- | --- | --- | --- | --- |"]
    for key in MODEL_IDS:
        verdict = analysis["model_verdicts"][key]
        lines.append("| %s | %s | %s | %s | %s | %s |"
                     % (key, verdict["confirmed"], verdict["positive"],
                        verdict["indistinguishable"], verdict["inconclusive"],
                        verdict["regression"]))
    lines += ["", "总体稳定确认: **%s**。" % analysis["stable_confirmation"],
              "", "## 合并 H+R（次要，400 seed，不单独构成确认）", "",
              "| 模型 | 基线 | n | 均值差 | 校正CI |", "| --- | --- | --- | --- | --- |"]
    for entry in analysis["combined_secondary"]:
        stats = entry["stats"]
        lines.append("| %s | %s | %s | %s | [%s, %s] |"
                     % (entry["model"], entry["baseline"], entry["n"], _f(stats.get("mean")),
                        _f(stats.get("adjusted_ci_low")), _f(stats.get("adjusted_ci_high"))))
    lines += ["", "## 诚实结论与局限", "",
              "- 仅在一个 heldout 牌组、一个遭遇上进行两批 200 seed；只能说明该处随机可重复性，"
              "不代表广泛牌组泛化。  ",
              "- 同动作的配对差恒为 0，记为不可区分，既非失败也非确认；区间跨 0 为不确定，"
              "为负为回归。  ",
              "- 合成 PROXY、单 solver 策略，无真相标签；本确认不决定模型保留，只服务优化与确认。  ",
              "- 无选择性加赛、无结果后再拟合、无第二套超参数。  ", "",
              "原始证据: `%s`, `%s`。" % (rel_path(CONFIRM_SUMMARY_PATH), rel_path(MODELS_PATH))]
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines) + "\n", encoding="utf-8")


# --------------------------------------------------------------------------- #
# Prepare
# --------------------------------------------------------------------------- #

def run_prepare() -> int:
    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    if PROTOCOL_PATH.exists() and MODELS_PATH.exists():
        # Once fitting has consumed the protocol it is immutable.
        existing = load_json(PROTOCOL_PATH)
        print(json.dumps({"status": "already_frozen",
                          "protocol_path": rel_path(PROTOCOL_PATH),
                          "protocol_sha256": file_sha256(PROTOCOL_PATH),
                          "created_at_utc": existing.get("created_at_utc")},
                         ensure_ascii=False), flush=True)
        write_protocol_doc(existing)
        return 0
    protocol = build_protocol()
    write_json(PROTOCOL_PATH, protocol)
    write_protocol_doc(protocol)
    print(json.dumps({"status": "frozen",
                      "protocol_path": rel_path(PROTOCOL_PATH),
                      "protocol_sha256": file_sha256(PROTOCOL_PATH),
                      "report_path": rel_path(REPORT)}, ensure_ascii=False), flush=True)
    return 0


# --------------------------------------------------------------------------- #
# Main
# --------------------------------------------------------------------------- #

def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--prepare", action="store_true",
                       help="freeze protocol.json and the protocol doc")
    group.add_argument("--fit", action="store_true",
                       help="train-only fit from work/shiv-fit-v1 -> models.json")
    group.add_argument("--confirm", action="store_true",
                       help="run/aggregate replication R and confirm against H")
    parser.add_argument("--run-root", help="explicit shiv-fit-v1 run root (fit/H source)")
    parser.add_argument("--out-dir", help="artifact directory (fit only)")
    args = parser.parse_args(argv)
    try:
        if args.prepare:
            return run_prepare()
        if args.fit:
            return run_fit(args.run_root, args.out_dir)
        return run_confirm(args.run_root)
    except ThreeModelsError as exc:
        print(json.dumps({"status": "error", "error": "%s: %s"
                          % (type(exc).__name__, exc)}, ensure_ascii=False), flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
