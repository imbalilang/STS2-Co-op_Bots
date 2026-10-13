#!/usr/bin/env python3
"""Frozen-action held-out screen for the two accepted PROXY pick states.

A narrow held-out extension of the reviewed ``building_pick_node_repeat.py``
batch: it replays **only** the four approved fixture/action cells from
``work/pick-fixtures/heldout-protocol.json`` on the never-used held-out seeds
``PICKEVAL01`` / ``PICKEVAL02``, twice each, in a single 16-way pool at nodes
6000, ``--dop 2``, a 120000 ms soft budget and a 180000 ms search timeout.

Boundaries: the reviewed selection artifact is SHA256-pinned (injectable only
from Python tests), its mapping and the current adapter ``BuildValue`` action
are re-checked, and no held-out action is ever re-selected; the runtime is
pinned before/after against the accepted baseline, so a mismatch aborts before
launch and any change disables all comparisons; a pair compares only when both
actions have two verified, complete, repeat-agreeing observations (win > loss,
both wins compare terminal player HP, both losses tie, enemy HP/turns never
tiebreak); unavailable pairs are reported, not imputed. Repeats are not extra
samples and the four pairs share two seeds, so no independence, significance or
gain claim is made. The default command is a dry run; ``--run`` is required.

Usage:
    python scripts/building_pick_heldout_screen.py            # dry run, plan only
    python scripts/building_pick_heldout_screen.py dryonly    # explicit dry-run alias
    python scripts/building_pick_heldout_screen.py --run      # execute the 16 launches
"""

from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path

import building_eval as be
import building_pick_node_repeat as node
import building_pick_parallel_check as parallel
import building_pick_ranking_screen as ranking
import building_repeat_probe as brp

ROOT = brp.ROOT
HARNESS = brp.HARNESS
DOTNET = brp.DOTNET
CWD = brp.CWD

SOURCE_DIR = ranking.SOURCE_DIR
HISTORICAL_DIR = parallel.HISTORICAL_DIR
BASELINE_DIR = parallel.BASELINE_DIR
OUTPUT_ROOT = ROOT / "work" / "pick-heldout-screen"
PROTOCOL_PATH = ROOT / "work" / "pick-fixtures" / "heldout-protocol.json"
SELECTION_SHA256 = "b970a7b5eef9fdf9f9b79332b67da36482ee85e1000d538e47824a3b2d9da942"

HELDOUT_SEEDS = ("PICKEVAL01", "PICKEVAL02")
REPS = (1, 2)
APPROVED = {
    "silent-starter-proxy": {"selected": "skip", "baseline": "BACKFLIP"},
    "silent-late-pm001-proxy": {"selected": "BACKFLIP", "baseline": "DAGGER_SPRAY"},
}

DOP = "2"
BASE_BUDGET_MS = 20000
SOFT_BUDGET_MS = 120000
SEARCH_TIMEOUT_MS = "180000"
NODES = ranking.NODES
MAX_TURNS = ranking.MAX_TURNS
CHARACTER = ranking.CHARACTER
PHASE = "parallel"
PARALLELISM = 16
MAX_LAUNCHES = 16
BASE_COMBINATIONS = 4
PHASE_TIMEOUT_SECONDS = node.PHASE_TIMEOUT_SECONDS
BATCH_DEADLINE_SECONDS = parallel.BATCH_DEADLINE_SECONDS

STATUS_VERIFIED = parallel.STATUS_VERIFIED
STATUS_DRY_RUN = parallel.STATUS_DRY_RUN
SourceValidationError = parallel.SourceValidationError
RuntimeValidationError = parallel.RuntimeValidationError
BaselineValidationError = parallel.BaselineValidationError

NOTES = [
    "The default command is a dry run that launches nothing; --run is required.",
    "Only the four approved fixture/action cells are replayed, on the two held-out "
    "seeds, twice each; no held-out action is ever re-selected.",
    "A pair is compared only when both actions have two verified, complete, "
    "repeat-agreeing observations and the runtime is pinned; otherwise unavailable.",
    "Repeats are not extra samples and the four pairs share two seeds; no "
    "independence, significance or gain claim is made.",
    "The historical 20000 ms input is used only as the seed-remap reference and is "
    "never treated as held-out evidence.",
]
LIMITATIONS = [
    "Both fixtures are PROXY states, not observed reward screens.",
    "Two seeds with two repeats each cannot establish variance or significance; the "
    "repeats of one seed are not independent samples.",
    "The baseline is a 20000 ms dop1 development reference; this batch is 120000 ms "
    "dop2, so any difference is descriptive only and is never attributed.",
    "The batch deadline is monotonic wall clock; a slow host can mark later runs "
    "not_started, which is recorded rather than hidden.",
    "Only the initial search boundary is read; per-turn node-only execution is not "
    "established by this instrument.",
    "Evidence is only as trustworthy as the harness build and the accepted fixture "
    "directory it consumes.",
]


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def default_output_root(now=None) -> Path:
    """A fresh UTC timestamp directory with microsecond resolution."""
    moment = now if now is not None else datetime.now(timezone.utc)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return OUTPUT_ROOT / moment.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")


def _action(item) -> str:
    item = item if isinstance(item, dict) else {}
    return "skip" if item.get("candidate") is None else item.get("candidate")


def _read_json(path) -> dict:
    path = Path(path)
    if not path.exists():
        raise SourceValidationError(f"JSON absent: {be.rel(path)}")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise SourceValidationError(f"JSON unreadable {be.rel(path)}: {exc}") from exc
    if not isinstance(data, dict):
        raise SourceValidationError(f"JSON is not an object: {be.rel(path)}")
    return data


def _resolve_path(value) -> Path:
    if not isinstance(value, str) or not value:
        raise SourceValidationError(f"path must be a non-empty string, got {value!r}")
    path = Path(value)
    return path if path.is_absolute() else (ROOT / path)


# ---------------------------------------------------------------------------
# Frozen protocol and selection verification
# ---------------------------------------------------------------------------

def load_protocol(path=None) -> dict:
    """Load the frozen protocol and re-check every frozen limit and mapping."""
    path = Path(path) if path is not None else PROTOCOL_PATH
    data = _read_json(path)
    errors = []
    if tuple(data.get("heldout_seeds") or ()) != HELDOUT_SEEDS:
        errors.append(f"heldout_seeds {data.get('heldout_seeds')!r} != {list(HELDOUT_SEEDS)}")
    if data.get("actions") != APPROVED:
        errors.append(f"actions {data.get('actions')!r} != approved mapping")
    expected_int = {
        "replicates": len(REPS), "max_launches": MAX_LAUNCHES, "parallelism": PARALLELISM,
        "dop": int(DOP), "nodes": int(NODES), "soft_budget_ms": SOFT_BUDGET_MS,
        "search_timeout_ms": int(SEARCH_TIMEOUT_MS), "max_turns": int(MAX_TURNS),
        "process_timeout_seconds": PHASE_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
    }
    for key, value in expected_int.items():
        if data.get(key) != value:
            errors.append(f"{key} {data.get(key)!r} != {value!r}")
    for key in ("reselection", "training", "production_adoption"):
        if data.get(key) is not False:
            errors.append(f"{key} must be false, got {data.get(key)!r}")
    if errors:
        raise SourceValidationError("protocol invalid: " + "; ".join(errors))
    return data


def load_selection(protocol, *, path=None, sha256=None) -> dict:
    """Hash-verify and load the reviewed selection artifact."""
    selection_path = Path(path) if path is not None else _resolve_path(protocol.get("selection_path"))
    expected = SELECTION_SHA256 if sha256 is None else sha256
    if not selection_path.exists():
        raise SourceValidationError(f"selection artifact absent: {be.rel(selection_path)}")
    actual = be.sha256_file(selection_path)
    if actual != expected:
        raise SourceValidationError(
            f"selection artifact sha256 {actual} != pinned {expected}"
        )
    declared = protocol.get("selection_sha256")
    if declared != SELECTION_SHA256 and sha256 is None:
        raise SourceValidationError(
            f"protocol selection_sha256 {declared!r} != pinned {SELECTION_SHA256}"
        )
    data = _read_json(selection_path)
    if data.get("heldout_used") is not False:
        raise SourceValidationError("selection artifact claims held-out data was used")
    if data.get("production_adoption") is not False:
        raise SourceValidationError("selection artifact claims production adoption")
    data["_path"] = str(selection_path)
    data["_sha256"] = actual
    return data


def verify_selection_mapping(selection, source) -> None:
    """Re-check the approved mapping and the current adapter BuildValue action."""
    fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    entries = {
        entry.get("fixture"): entry
        for entry in selection.get("fixtures") or []
        if isinstance(entry, dict)
    }
    errors = []
    for fixture_id, approved in APPROVED.items():
        entry = entries.get(fixture_id)
        if entry is None:
            errors.append(f"selection lacks fixture {fixture_id}")
            continue
        if entry.get("gate_passed") is not True:
            errors.append(f"selection fixture {fixture_id} gate did not pass")
        if entry.get("selected_action") != approved["selected"]:
            errors.append(
                f"selection {fixture_id} selected {entry.get('selected_action')!r} "
                f"!= approved {approved['selected']!r}"
            )
        current = ranking.resolve_build_value_action(fixtures[fixture_id])
        if entry.get("build_value_action") != current:
            errors.append(
                f"selection {fixture_id} build_value_action "
                f"{entry.get('build_value_action')!r} != current adapter {current!r}"
            )
        if current != approved["baseline"]:
            errors.append(
                f"adapter BuildValue {fixture_id} {current!r} != approved baseline "
                f"{approved['baseline']!r}"
            )
    if errors:
        raise SourceValidationError("selection mapping invalid: " + "; ".join(errors))


# ---------------------------------------------------------------------------
# Specs, inputs and baseline remap
# ---------------------------------------------------------------------------

def build_heldout_specs(ordered) -> list:
    """Filter the reviewed 20000 ms specs to the four approved cells and clone them."""
    approved_cells = {
        (fixture, mapping[role])
        for fixture, mapping in APPROVED.items()
        for role in ("selected", "baseline")
    }
    base = [
        spec for spec in ordered
        if spec.get("budget_ms") == BASE_BUDGET_MS and (spec["fixture"], _action(spec)) in approved_cells
    ]
    if len(base) != BASE_COMBINATIONS:
        raise SourceValidationError(
            f"expected {BASE_COMBINATIONS} approved 20000 ms specs, found {len(base)}"
        )
    specs = []
    for spec in base:
        for seed in HELDOUT_SEEDS:
            for rep in REPS:
                run = dict(spec)
                label = _action(spec)
                run.update(
                    scenario_path=str(spec["scenario_path"]),
                    source_seed=spec.get("source_seed", spec["seed"]),
                    seed=seed,
                    heldout_seed=seed,
                    budget_ms=SOFT_BUDGET_MS,
                    rep=rep,
                    phase=PHASE,
                    base_name=f"{spec['fixture']}-{label}-{seed}",
                    name=f"{spec['fixture']}-{label}-{seed}-b{SOFT_BUDGET_MS}-r{rep}",
                )
                specs.append(run)
    return specs


def build_heldout_runs(specs, output_root, *, harness_path=None, dotnet_path=None) -> list:
    """Attach per-run directories and the reused frozen node-repeat command."""
    output_root = Path(output_root)
    runs = []
    for spec in specs:
        run = dict(spec)
        run_dir = parallel.run_dir_path(output_root, run)
        run.update(
            dir=be.rel(run_dir),
            input=be.rel(run_dir / "input.json"),
            request=be.rel(run_dir / "outer-request.json"),
            command=node.build_command(
                run_dir / "outer-request.json", run_dir, run["name"], SOFT_BUDGET_MS,
                harness_path=harness_path, dotnet_path=dotnet_path,
            ),
            timeout_seconds=PHASE_TIMEOUT_SECONDS,
        )
        runs.append(run)
    if (
        len(runs) != MAX_LAUNCHES
        or len({run["name"] for run in runs}) != MAX_LAUNCHES
        or len({tuple(run["command"]) for run in runs}) != MAX_LAUNCHES
    ):
        raise SourceValidationError(f"expected {MAX_LAUNCHES} unique held-out runs")
    return runs


def expected_baseline_inputs(baseline, runs) -> dict:
    """Map each run cell to the seed-remapped accepted 20000 ms input hash."""
    cells = parallel.baseline_cells(baseline)
    expected = {}
    for run in runs:
        key = (run["fixture"], _action(run), run["seed"])
        if key in expected:
            continue
        cell = cells.get((run["fixture"], _action(run), BASE_BUDGET_MS))
        if not isinstance(cell, dict):
            raise SourceValidationError(f"baseline lacks 20000 ms cell for {key!r}")
        remapped = node._remap_cell_to_seed(baseline, cell, run["seed"])
        expected[key] = remapped["raw_hashes"]["input_sha256"]
    return expected


def prepare_heldout_input(run, output_root, historical_request_path, expected_by_cell) -> dict:
    """Write one seed-only input and the sanitized outer request; return their hashes."""
    run_dir = parallel.run_dir_path(output_root, run)
    run_dir.mkdir(parents=True, exist_ok=True)

    scenario_path = Path(run["scenario_path"])
    raw = scenario_path.read_bytes()
    actual = ranking.sha256_bytes(raw)
    if actual != run["scenario_sha256"]:
        raise SourceValidationError(
            f"scenario changed since source load: {run['scenario_source']} sha256 {actual} "
            f"!= {run['scenario_sha256']}"
        )
    original = json.loads(raw.decode("utf-8"))
    if original.get("seed") != run["source_seed"]:
        raise SourceValidationError(
            f"scenario {run['scenario_source']} seed {original.get('seed')!r} "
            f"!= manifest seed {run['source_seed']!r}"
        )
    seeded = dict(original)
    seeded["seed"] = run["seed"]
    if ranking.diff_paths(original, seeded) != {"seed"}:
        raise SourceValidationError("held-out seed override changed unexpected input fields")
    input_path = run_dir / "input.json"
    input_path.write_text(json.dumps(seeded, ensure_ascii=False, indent=1), encoding="utf-8")
    input_sha = be.sha256_file(input_path)
    expected = expected_by_cell.get((run["fixture"], _action(run), run["seed"]))
    if expected is None or input_sha != expected:
        raise SourceValidationError(
            f"{run['name']} generated input sha256 {input_sha} != baseline seed-remap {expected}"
        )

    request = json.loads(Path(historical_request_path).read_text(encoding="utf-8"))
    sanitized = brp.sanitize_request(request, run_dir, run["name"])
    baseline_request = deepcopy(sanitized)
    sanitized["seed"] = run["seed"]
    sanitized["characterId"] = run["character"]
    if ranking.diff_paths(baseline_request, sanitized) != {"seed"}:
        raise SourceValidationError("held-out outer request changed unexpected fields")
    request_path = run_dir / "outer-request.json"
    request_path.write_text(json.dumps(sanitized, ensure_ascii=False, indent=1), encoding="utf-8")
    return {
        "input_sha256": input_sha,
        "request_sha256": be.sha256_file(request_path),
        "baseline_input_sha256": expected,
    }


# ---------------------------------------------------------------------------
# Provenance, plan and summary blocks
# ---------------------------------------------------------------------------

def collect_provenance(source, historical_request_path, baseline, protocol, selection, *,
                       protocol_path=None, harness_path=None, dotnet_path=None) -> dict:
    provenance = ranking.collect_provenance(
        source, historical_request_path, harness_path=harness_path, dotnet_path=dotnet_path
    )
    provenance["driver"] = {"path": be.rel(Path(__file__)), "sha256": be.sha256_file(Path(__file__))}
    provenance["node_repeat_helper"] = {
        "path": be.rel(Path(node.__file__)), "sha256": be.sha256_file(Path(node.__file__))}
    provenance["parallel_check_helper"] = {
        "path": be.rel(Path(parallel.__file__)), "sha256": be.sha256_file(Path(parallel.__file__))}
    provenance["baseline"] = {"dir": be.rel(baseline["dir"]),
                              "summary": be.rel(baseline["summary_path"]),
                              "summary_sha256": baseline["summary_sha256"]}
    provenance["protocol"] = {
        "path": be.rel(Path(protocol_path) if protocol_path is not None else PROTOCOL_PATH),
        "sha256": be.sha256_file(Path(protocol_path) if protocol_path is not None else PROTOCOL_PATH)}
    provenance["selection"] = {
        "path": be.rel(Path(selection["_path"])), "sha256": selection["_sha256"],
        "rule": selection.get("rule"), "rule_sha256": selection.get("rule_sha256")}
    return provenance


def build_plan(runs, source, historical_request_path, baseline, protocol, selection,
               runtime_before, provenance, expected_by_cell, *,
               protocol_path=None, harness_path=None, dotnet_path=None) -> dict:
    protocol_path = Path(protocol_path) if protocol_path is not None else PROTOCOL_PATH
    return {
        "generated_at": utc_now(), "tool": "building_pick_heldout_screen.py",
        "heldout_seeds": list(HELDOUT_SEEDS), "reps": list(REPS),
        "approved_actions": deepcopy(APPROVED), "approved_cells": len(APPROVED) * 2,
        "character": CHARACTER, "profile": ranking.PROFILE, "nodes": NODES,
        "dop": DOP, "soft_budget_ms": SOFT_BUDGET_MS, "search_timeout_ms": SEARCH_TIMEOUT_MS,
        "max_turns": MAX_TURNS, "base_budget_ms": BASE_BUDGET_MS, "phase": PHASE,
        "max_launches": MAX_LAUNCHES, "base_combinations": BASE_COMBINATIONS,
        "parallelism": PARALLELISM, "phase_timeout_seconds": PHASE_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS, "cwd": be.rel(CWD),
        "harness": be.rel(Path(harness_path) if harness_path is not None else HARNESS),
        "dotnet": be.rel(Path(dotnet_path) if dotnet_path is not None else DOTNET),
        "source": {
            "dir": be.rel(source["source_dir"]), "adapter": be.rel(source["adapter_path"]),
            "manifest": be.rel(source["manifest_path"]),
            "adapter_sha256": source["adapter_sha256"],
            "manifest_sha256": source["manifest_sha256"],
            "historical_request": be.rel(historical_request_path),
            "historical_request_sha256": brp.sha256_file(historical_request_path),
        },
        "baseline": {"dir": be.rel(baseline["dir"]), "summary": be.rel(baseline["summary_path"]),
                     "summary_sha256": baseline["summary_sha256"]},
        "selection": {"path": be.rel(Path(selection["_path"])), "sha256": selection["_sha256"],
                      "rule": selection.get("rule"), "rule_sha256": selection.get("rule_sha256")},
        "protocol": {"path": be.rel(protocol_path), "frozen_at": protocol.get("frozen_at"),
                     "pair_gate": protocol.get("pair_gate"), "comparison": protocol.get("comparison"),
                     "aggregation": protocol.get("aggregation")},
        "expected_baseline_inputs": {f"{k[0]}|{k[1]}|{k[2]}": v for k, v in expected_by_cell.items()},
        "runtime_before": runtime_before, "runs": runs, "provenance": provenance,
    }


def _eligible(record) -> bool:
    return bool(
        isinstance(record, dict) and record.get("status") == STATUS_VERIFIED
        and record.get("eligible")
    )


def compare_repeats(group, enabled, gate) -> dict:
    first, second = group.get(1), group.get(2)
    entry = {
        "fixture": group.get("fixture"), "action": group.get("action"),
        "seed": group.get("seed"), "rep_names": [first and first.get("run"), second and second.get("run")],
    }
    if not enabled:
        entry.update(available=False, agreement=None, reason=gate)
    elif not (_eligible(first) and _eligible(second)):
        entry.update(available=False, agreement=None,
                     reason="one or both repeats are not verified eligible")
    else:
        left, right = parallel.semantic_tuple(first), parallel.semantic_tuple(second)
        compared = parallel.compare_semantics(left, right)
        entry.update(available=True, agreement=compared["exact_match"],
                     changed_fields=compared["changed_fields"], rep1=left, rep2=right)
    return entry


def _pair_entry(fixture, seed, repeat_index, enabled, gate) -> dict:
    selected_action = APPROVED[fixture]["selected"]
    baseline_action = APPROVED[fixture]["baseline"]
    entry = {"fixture": fixture, "seed": seed, "selected_action": selected_action,
             "baseline_action": baseline_action, "available": False, "result": None}
    selected_entry = repeat_index.get((fixture, selected_action, seed))
    baseline_entry = repeat_index.get((fixture, baseline_action, seed))
    if not enabled:
        entry["reason"] = gate
        return entry
    if not (
        selected_entry and baseline_entry
        and selected_entry.get("available") and baseline_entry.get("available")
    ):
        entry["reason"] = "one or both actions are not verified eligible"
        return entry
    if not (selected_entry.get("agreement") and baseline_entry.get("agreement")):
        entry["reason"] = "repeat disagreement or incompleteness suppresses the pair"
        return entry

    selected, baseline = selected_entry["rep1"], baseline_entry["rep1"]
    entry.update(available=True, selected=selected, baseline=baseline)
    s_win = selected.get("outcome_status") == be.STATUS_WIN
    b_win = baseline.get("outcome_status") == be.STATUS_WIN
    if s_win and not b_win:
        entry.update(result="selected_better", outcome="selected_win_baseline_loss")
    elif b_win and not s_win:
        entry.update(result="selected_worse", outcome="selected_loss_baseline_win")
    elif s_win and b_win:
        s_hp, b_hp = selected.get("player_hp"), baseline.get("player_hp")
        if any(type(hp) not in (int, float) or not math.isfinite(hp) for hp in (s_hp, b_hp)):
            entry.update(available=False, reason="missing or invalid winning terminal HP")
            return entry
        delta = s_hp - b_hp
        entry.update(
            both_win=True, hp_delta=delta, outcome="both_win",
            result="tie" if delta == 0 else ("selected_better" if delta > 0 else "selected_worse"),
        )
    else:
        entry.update(result="tie", outcome="both_loss")
    return entry


def aggregate_pairs(pairs) -> dict:
    available = [pair for pair in pairs if pair.get("available")]
    co_win = [pair for pair in available if pair.get("both_win")]
    return {
        "total_pairs": len(pairs), "available": len(available),
        "all_planned_pairs_available": len(available) == len(APPROVED) * len(HELDOUT_SEEDS) == len(pairs),
        "unavailable": len(pairs) - len(available),
        "better": sum(1 for pair in available if pair.get("result") == "selected_better"),
        "equal": sum(1 for pair in available if pair.get("result") == "tie"),
        "worse": sum(1 for pair in available if pair.get("result") == "selected_worse"),
        "selected_win_baseline_loss": sum(
            1 for pair in available if pair.get("outcome") == "selected_win_baseline_loss"),
        "selected_loss_baseline_win": sum(
            1 for pair in available if pair.get("outcome") == "selected_loss_baseline_win"),
        "both_win": len(co_win),
        "co_win_hp_deltas": [pair.get("hp_delta") for pair in co_win],
        "co_win_hp_denominator": len(co_win),
        "repeats_are_independent_samples": False, "pairs_share_two_seeds": True,
        "note": "raw paired outcomes only; two seeds and two repeats cannot support an "
                "independence, significance or gain claim",
    }


def build_summary(records, *, executed, output_root, plan, source, baseline, runtime_before,
                  runtime_after, parallel_wall, peak_overlap, cpu_count, error) -> dict:
    by_name = {record.get("run"): record for record in records}
    ordered = []
    for run in plan["runs"]:
        record = by_name.get(run["name"])
        if record is None:
            record = parallel._missing_record(run, output_root, plan["provenance"])
            record.update(rep=run.get("rep"), base_name=run.get("base_name"), node_telemetry=None)
        ordered.append(record)

    runtime_check = parallel.check_runtime_against_baseline(runtime_before, baseline["runtime"])
    runtime_stable = bool(
        executed and runtime_before is not None and runtime_after is not None
        and runtime_before == runtime_after
    )
    enabled = bool(executed and runtime_check["matches"] and runtime_stable)
    gate = "; ".join(filter(None, (
        None if executed else "dry run",
        None if runtime_check["matches"] else "runtime does not match the accepted baseline",
        None if runtime_stable else "runtime changed between before and after the batch",
    ))) or None

    groups = {}
    for record in ordered:
        key = (record.get("fixture"), _action(record), record.get("seed"))
        bucket = groups.setdefault(key, {})
        bucket["fixture"], bucket["action"], bucket["seed"] = key
        bucket[record.get("rep")] = record
    repeat_entries = [compare_repeats(bucket, enabled, gate)
                      for _, bucket in sorted(groups.items(), key=lambda item: str(item[0]))]
    repeat_index = {(entry["fixture"], entry["action"], entry["seed"]): entry
                    for entry in repeat_entries}
    agreed = sum(1 for entry in repeat_entries if entry.get("available") and entry.get("agreement"))
    available_pairs = sum(1 for entry in repeat_entries if entry.get("available"))

    pairs = [_pair_entry(fixture, seed, repeat_index, enabled, gate)
             for fixture in APPROVED for seed in HELDOUT_SEEDS]
    aggregation = aggregate_pairs(pairs)

    performance = node.build_performance(
        ordered, parallel_wall=parallel_wall, peak_overlap=peak_overlap, cpu_count=cpu_count)
    verified = sum(1 for record in ordered if record.get("status") == STATUS_VERIFIED)
    eligible = sum(1 for record in ordered if record.get("eligible"))
    policy_verified = sum(
        1 for record in ordered
        if isinstance(record.get("policy"), dict) and record["policy"].get("ok")
    )
    accounted = len(ordered) == MAX_LAUNCHES and len(records) == MAX_LAUNCHES

    return {
        "generated_at": utc_now(), "tool": "building_pick_heldout_screen.py",
        "executed": bool(executed), "output_dir": be.rel(Path(output_root)),
        "heldout_seeds": list(HELDOUT_SEEDS), "reps": list(REPS),
        "character": CHARACTER, "soft_budget_ms": SOFT_BUDGET_MS,
        "search_timeout_ms": SEARCH_TIMEOUT_MS, "nodes": NODES, "dop": DOP,
        "batch": {"total_attempts": MAX_LAUNCHES, "attempts_accounted": accounted,
                  "base_combinations": BASE_COMBINATIONS, "reps": list(REPS),
                  "parallelism": PARALLELISM, "phase_timeout_seconds": PHASE_TIMEOUT_SECONDS,
                  "batch_deadline_seconds": BATCH_DEADLINE_SECONDS, "budget_ms": SOFT_BUDGET_MS,
                  "search_timeout_ms": SEARCH_TIMEOUT_MS, "dop": DOP, "nodes": NODES,
                  "max_turns": MAX_TURNS},
        "status_counts": ranking._status_counts(ordered),
        "outcome_counts": ranking._outcome_counts(ordered), "verified": verified,
        "eligible_completed": eligible, "policy_verified": policy_verified,
        "tool_completed": bool(accounted and error is None),
        "runtime": {"before": runtime_before, "after": runtime_after, "stable": runtime_stable,
                    "matches_baseline": runtime_check["matches"],
                    "baseline_mismatches": runtime_check["mismatches"]},
        "repeat_agreement": {"available": available_pairs, "agreed": agreed,
                             "disagreed": available_pairs - agreed, "total": len(repeat_entries),
                             "comparisons_enabled": enabled, "gate_reason": gate,
                             "repeats_are_extra_samples": False, "groups": repeat_entries},
        "pairs": pairs, "pair_aggregation": aggregation,
        "initial_search": node._build_initial_search(ordered), "performance": performance,
        "decision": {"production_adoption": "none", "new_labels": False,
                     "heldout_gain_claim": False, "statistical_significance_claim": False,
                     "confidence_claim": False, "causality_claim": False,
                     "training": False, "reselection": False, "variance_claim": False,
                     "whole_fight_node_only_claim": False, "automatic_expansion": False,
                     "summary": "Raw paired held-out outcomes only; no production change, no "
                     "label, no significance and no whole-fight node-only guarantee."},
        "selection": {"path": be.rel(Path(plan["selection"]["path"])),
                      "sha256": plan["selection"]["sha256"], "rule": plan["selection"]["rule"],
                      "rule_sha256": plan["selection"]["rule_sha256"]},
        "baseline": {"dir": be.rel(baseline["dir"]), "summary": be.rel(baseline["summary_path"]),
                     "summary_sha256": baseline["summary_sha256"], "validated": True,
                     "budget_ms": BASE_BUDGET_MS},
        "runs": ordered, "plan": plan, "error": error,
        "notes": list(NOTES), "limitations": list(LIMITATIONS),
    }


# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------

def run_heldout_screen(*, run=False, output_root=None, source_dir=None, baseline_dir=None,
                       historical_dir=None, protocol_path=None, selection_path=None,
                       selection_sha256=None, harness_path=None, dotnet_path=None,
                       runtime_config_path=None, runtime_env=None, runner=None, clock=None,
                       cpu_count=None, now=None) -> dict:
    """Run the frozen 16-launch held-out plan. ``run=False`` (the default) launches nothing."""
    runner = runner if runner is not None else subprocess.run
    clock = clock if clock is not None else time.monotonic
    source_dir = Path(source_dir) if source_dir is not None else SOURCE_DIR
    baseline_dir = Path(baseline_dir) if baseline_dir is not None else BASELINE_DIR
    historical_dir = Path(historical_dir) if historical_dir is not None else HISTORICAL_DIR
    historical_request_path = historical_dir / "outer-request.json"
    harness_path = Path(harness_path) if harness_path is not None else HARNESS
    dotnet_path = Path(dotnet_path) if dotnet_path is not None else DOTNET
    if runtime_config_path is None:
        runtime_config_path = harness_path.parent / "OfflineSearchHarness.runtimeconfig.json"
    runtime_config_path = Path(runtime_config_path)
    output_root = Path(output_root) if output_root is not None else default_output_root(now)

    source, ordered = ranking.prepare_source(source_dir)
    protocol = load_protocol(protocol_path)
    selection = load_selection(protocol, path=selection_path, sha256=selection_sha256)
    verify_selection_mapping(selection, source)
    baseline = parallel.load_baseline(baseline_dir)
    parallel.validate_baseline_source(baseline, source, historical_request_path)
    specs = build_heldout_specs(ordered)
    expected_by_cell = expected_baseline_inputs(baseline, specs)

    try:
        runtime_before = ranking.collect_runtime_provenance(
            runtime_config_path, source["adapter"], harness_path=harness_path,
            dotnet_path=dotnet_path, env=runtime_env)
    except RuntimeValidationError as exc:
        runtime_before = {"ok": False, "error": str(exc)}
    runtime_preflight = parallel.check_runtime_against_baseline(runtime_before, baseline["runtime"])
    if run and not runtime_preflight["matches"]:
        raise RuntimeValidationError(
            "runtime does not match the accepted baseline: "
            + json.dumps(runtime_preflight["mismatches"], ensure_ascii=False)
        )

    output_root.mkdir(parents=True, exist_ok=False)  # existing output is refused
    runs = build_heldout_runs(specs, output_root, harness_path=harness_path, dotnet_path=dotnet_path)
    provenance = collect_provenance(
        source, historical_request_path, baseline, protocol, selection,
        protocol_path=protocol_path, harness_path=harness_path, dotnet_path=dotnet_path)
    plan = build_plan(
        runs, source, historical_request_path, baseline, protocol, selection, runtime_before,
        provenance, expected_by_cell, protocol_path=protocol_path,
        harness_path=harness_path, dotnet_path=dotnet_path)
    plan["output_root"] = be.rel(output_root)
    brp.write_json_exclusive(output_root / "plan.json", plan)
    journal_path = ranking.create_exclusive_file(output_root / "journal.jsonl")

    adapter_fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    dry_run, start, records, error = not run, clock(), [], None
    parallel_wall, runtime_after, overlap = 0.0, None, node._Overlap()
    try:
        prepared = {}
        if not dry_run:
            for run_case in runs:
                prepared[(run_case["phase"], run_case["name"])] = prepare_heldout_input(
                    run_case, output_root, historical_request_path, expected_by_cell)
        phase_start = clock()
        with ThreadPoolExecutor(max_workers=PARALLELISM) as pool:
            futures = {
                pool.submit(
                    node._run_one, run_case, output_root, overlap=overlap,
                    adapter_fixture=adapter_fixtures[run_case["fixture"]], prepared=prepared,
                    runner=runner, clock=clock, start=start, dry_run=dry_run,
                    provenance=provenance, phase_cap=PHASE_TIMEOUT_SECONDS,
                ): run_case
                for run_case in runs
            }
            for future in as_completed(futures):  # journal in the main thread
                run_case = futures[future]
                try:
                    record = future.result()
                except BaseException as exc:  # noqa: BLE001
                    record = parallel._missing_record(run_case, output_root, provenance)
                    record.update(rep=run_case.get("rep"), base_name=run_case.get("base_name"),
                                  node_telemetry=None,
                                  reason=f"future failed: {type(exc).__name__}: {exc}")
                record["heldout_seed"] = run_case.get("heldout_seed")
                records.append(record)
                ranking.append_journal(journal_path, record)
        parallel_wall = clock() - phase_start
        if not dry_run:
            try:
                runtime_after = ranking.collect_runtime_provenance(
                    runtime_config_path, source["adapter"], harness_path=harness_path,
                    dotnet_path=dotnet_path, env=runtime_env)
            except RuntimeValidationError as exc:
                runtime_after = {"ok": False, "error": str(exc)}
    except BaseException as exc:  # noqa: BLE001 - the journal already holds attempts
        error = f"{type(exc).__name__}: {exc}"

    summary = build_summary(
        records, executed=run, output_root=output_root, plan=plan, source=source,
        baseline=baseline, runtime_before=runtime_before, runtime_after=runtime_after,
        parallel_wall=parallel_wall, peak_overlap=overlap.peak, cpu_count=cpu_count, error=error)
    brp.write_json_exclusive(output_root / "summary.json", summary)
    return summary


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Frozen-action held-out screen on PICKEVAL01/PICKEVAL02")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--run", action="store_true",
                       help="execute the 16 fixed launches (default: dry run)")
    group.add_argument("--dry-run", action="store_true",
                       help="write the plan only; no subprocess (default)")
    parser.add_argument("mode", nargs="?", choices=["dryonly"],
                        help="'dryonly' is an explicit alias for the default dry run")
    args = parser.parse_args(argv)
    execute = bool(args.run and args.mode != "dryonly")
    try:
        summary = run_heldout_screen(run=execute)
    except SourceValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    except FileExistsError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 3
    except RuntimeValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 4
    except BaselineValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 5
    if execute:
        print(
            f"attempts={summary['batch']['total_attempts']} verified={summary['verified']} "
            f"eligible={summary['eligible_completed']} policy_verified={summary['policy_verified']} "
            f"repeat_agreement={summary['repeat_agreement']['agreed']}/"
            f"{summary['repeat_agreement']['available']} "
            f"pairs_available={summary['pair_aggregation']['available']}/"
            f"{summary['pair_aggregation']['total_pairs']} "
            f"comparisons_enabled={summary['repeat_agreement']['comparisons_enabled']} "
            f"tool_completed={summary['tool_completed']}", flush=True)
    else:
        print(
            f"attempts={summary['batch']['total_attempts']} "
            f"seeds={summary['heldout_seeds']} dop={DOP} nodes={NODES} "
            f"budget_ms={SOFT_BUDGET_MS} executed=false "
            f"status_counts={summary['status_counts']}", flush=True)
    print(f"wrote {Path(summary['output_dir']) / 'summary.json'}", flush=True)
    if not execute:
        return 0
    return 0 if summary["tool_completed"] else 1


if __name__ == "__main__":
    sys.exit(main())
