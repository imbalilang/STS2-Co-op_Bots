#!/usr/bin/env python3
"""Bounded 16-way repeatability check for the eight accepted pick actions.

Follows the reviewed ``building_pick_parallel_check.py``: the eight accepted
baseline 20 s fixture/action combinations (same accepted fixtures and PICKSEL01
inputs as ``work/pick-ranking-screen/20260917T141513.123416Z``) are replayed twice
each, concurrently, at nodes 6000, ``--dop 2``, a 120000 ms soft budget and a
180000 ms search timeout. It answers: do the two repeats of one input agree on
terminal fight semantics, and do the per-fixture rankings agree?

Hard boundaries: 16 launches in one ``ThreadPoolExecutor(16)`` with unique repeat
names and byte-identical repeat inputs pinned to the baseline 20 s cell hash; the
accepted command reused with only budget/search-timeout/dop overridden; input
preparation and queue wait charged to the reused 900 s monotonic deadline, each
launch ``min(300, remaining)`` recomputed after preparation, no retries; a launch
without remaining budget is ``not_started``, never a fight; the actual
``search-policy.json`` must show dop/budget/nodes; repeat agreement uses the
reviewed ``semantic_tuple``/``compare_semantics`` only when both repeats are
verified eligible and the runtime is stable, and ranking agreement is reported
separately; the historical 20 s cells are descriptive only (no attribution, no
speed ratio); the initial ``timeBoundaryObserved``/expanded-node diagnostics do
not establish whole-fight node-only execution. No module global mutates.

The default command is a dry run that launches nothing; ``--run`` is required.

Usage:
    python scripts/building_pick_node_repeat.py          # dry run, plan only
    python scripts/building_pick_node_repeat.py dryonly  # explicit dry-run alias
    python scripts/building_pick_node_repeat.py --seed PICKSEL02   # second seed dry run
    python scripts/building_pick_node_repeat.py --run    # execute the 16 launches

``--seed`` accepts only the supported development seeds; ``PICKSEL01`` is the
default and preserves the accepted behavior, ``PICKSEL02`` regenerates the same
inputs with only the seed changed. Held-out or arbitrary seeds are rejected
before any output directory is created or process launched.
"""

from __future__ import annotations

import argparse, json, os, subprocess, sys, threading, time
from concurrent.futures import ThreadPoolExecutor, as_completed
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path

import building_eval as be
import building_pick_input_probe as bpi
import building_pick_parallel_check as parallel
import building_pick_ranking_screen as ranking
import building_repeat_probe as brp

ROOT = brp.ROOT
HARNESS = brp.HARNESS
DOTNET = brp.DOTNET
CWD = brp.CWD
RESULT_NAME = brp.RESULT_NAME
HISTORICAL_DIR = parallel.HISTORICAL_DIR
BASELINE_DIR = parallel.BASELINE_DIR
OUTPUT_ROOT = ROOT / "work" / "pick-node-repeat"

SEED = ranking.SEED
SUPPORTED_SEEDS = tuple(ranking.SUPPORTED_SEEDS)
CHARACTER = bpi.CHARACTER
ASCENSION = bpi.ASCENSION
PLAYER_HP = bpi.PLAYER_HP
ACTIONS = tuple(ranking.ACTIONS)
PROFILE = ranking.PROFILE
NODES = ranking.NODES
PLAY_PATH = ranking.PLAY_PATH
MAX_TURNS = ranking.MAX_TURNS
MILESTONE = ranking.MILESTONE

DOP = "2"
BASELINE_BUDGET_MS = 20000
SOFT_BUDGET_MS = 120000
SEARCH_TIMEOUT_MS = "180000"
BASE_COMBINATIONS = 8
REPS = (1, 2)
MAX_LAUNCHES = BASE_COMBINATIONS * len(REPS)
PHASE = "parallel"
PARALLELISM = 16
PHASE_TIMEOUT_SECONDS = 300
BATCH_DEADLINE_SECONDS = parallel.BATCH_DEADLINE_SECONDS

STATUS_VERIFIED = parallel.STATUS_VERIFIED
STATUS_ERROR = parallel.STATUS_ERROR
STATUS_DRY_RUN = parallel.STATUS_DRY_RUN
SourceValidationError = parallel.SourceValidationError
RuntimeValidationError = parallel.RuntimeValidationError
BaselineValidationError = parallel.BaselineValidationError

NOTES = [
    "The default command is a dry run that launches nothing; --run is required.",
    "Sixteen concurrent --dop 2 replays: eight accepted fixture/action combinations, "
    "each repeated twice, at nodes 6000 and a 120000 ms soft budget.",
    "Repeat agreement is exact terminal semantics only when both repeats are verified "
    "eligible and the runtime was stable; an incomplete cell is never a loss.",
    "Ranking agreement (top set / tie structure / pairwise order) is reported "
    "separately from exact-semantic agreement.",
    "Historical 20 s cells are a descriptive reference only: budget, dop and repetition "
    "all change at once, so no difference is attributed and no speed ratio is claimed.",
]
LIMITATIONS = [
    "Both fixtures are PROXY states, not observed reward screens.",
    "Two repeats per cell cannot establish run-to-run variance across seeds.",
    "The baseline is 20000 ms dop1; this batch is 120000 ms dop2, so any difference is "
    "uncontrolled and descriptive only.",
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


def validate_run_seed(seed=None) -> str:
    """Resolve and validate one development seed before any output or launch."""
    requested = SEED if seed is None else seed
    if requested not in SUPPORTED_SEEDS:
        raise SourceValidationError(
            f"unsupported development seed {requested!r}; "
            f"expected one of {list(SUPPORTED_SEEDS)}"
        )
    return requested


def _action(record_or_spec) -> str:
    item = record_or_spec if isinstance(record_or_spec, dict) else {}
    return "skip" if item.get("candidate") is None else item.get("candidate")


def _with_flag(command, flag, value) -> list:
    """Copy an argv and replace only the value after ``flag`` (never mutate)."""
    tokens = list(command)
    if tokens.count(flag) != 1:
        raise SourceValidationError(f"command must carry exactly one {flag}: {tokens!r}")
    tokens[tokens.index(flag) + 1] = str(value)
    return tokens


def build_command(request_path, run_dir, name, budget, *, harness_path=None,
                  dotnet_path=None) -> list:
    """Compose the reviewed ranking command with the frozen node-repeat overrides."""
    base = ranking.build_command(request_path, run_dir, name, budget,
                                 harness_path=harness_path, dotnet_path=dotnet_path)
    return _with_flag(parallel.with_dop(base, DOP), "--search-timeout-ms", SEARCH_TIMEOUT_MS)


def base_specs(ordered) -> list:
    """The eight accepted baseline 20 s specs from the shared source load."""
    specs = [spec for spec in ordered if spec.get("budget_ms") == BASELINE_BUDGET_MS]
    if len(specs) != BASE_COMBINATIONS:
        raise SourceValidationError(
            f"expected {BASE_COMBINATIONS} accepted 20000 ms specs, found {len(specs)}")
    return specs


def _remap_cell_to_seed(baseline, cell, seed) -> dict:
    """Expected input for another seed: the raw-verified baseline input, seed-only.

    The historical cell is deep-copied so its original seed, result and raw hash
    provenance are preserved; only the copy's expected input hash is changed.
    """
    run_name = cell.get("run")
    base_dir = baseline.get("dir")
    if not isinstance(run_name, str) or not run_name or base_dir is None:
        raise SourceValidationError(f"baseline cell {run_name!r} lacks a readable directory")
    input_path = Path(base_dir) / run_name / "input.json"
    if not input_path.exists():
        raise SourceValidationError(f"baseline input absent: {be.rel(input_path)}")
    try:
        original = json.loads(input_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise SourceValidationError(f"baseline input unreadable: {run_name!r}: {exc}") from exc
    if original.get("seed") != SEED:
        raise SourceValidationError(
            f"baseline input {run_name!r} seed {original.get('seed')!r} != {SEED!r}"
        )
    seeded = dict(original)
    seeded["seed"] = seed
    diff = ranking.diff_paths(original, seeded)
    if diff != {"seed"}:
        raise SourceValidationError(
            f"seed remap for {run_name!r} changed unexpected input fields: {sorted(diff)}"
        )
    text = json.dumps(seeded, ensure_ascii=False, indent=1)
    expected = ranking.sha256_bytes(text.replace("\n", os.linesep).encode("utf-8"))
    record = deepcopy(cell)
    record["raw_hashes"] = dict(cell.get("raw_hashes") or {})
    record["raw_hashes"]["input_sha256"] = expected
    record["seed_remap"] = {
        "run_seed": seed, "baseline_seed": SEED,
        "baseline_input_sha256": cell["raw_hashes"]["input_sha256"],
        "expected_input_sha256": expected, "changed_fields": ["seed"],
        "provenance": "deepcopy of the raw-verified baseline cell; its seed and result are untouched",
    }
    return record


def remap_baseline_cells(baseline, ordered, *, seed=None) -> dict:
    """Map each 120000 ms repeat cell to its accepted 20000 ms baseline cell.

    A missing baseline cell or missing expected input hash is a hard source error.
    For the default seed the historical cell is reused unchanged; for another
    development seed it is the baseline input with only the seed replaced.
    """
    run_seed = validate_run_seed(seed)
    cells = parallel.baseline_cells(baseline)
    remapped = {}
    for spec in base_specs(ordered):
        action = _action(spec)
        source_key = (spec["fixture"], action, BASELINE_BUDGET_MS)
        cell = cells.get(source_key)
        if not isinstance(cell, dict):
            raise SourceValidationError(f"baseline lacks 20000 ms cell {source_key!r}")
        expected = (cell.get("raw_hashes") or {}).get("input_sha256")
        if not isinstance(expected, str) or not expected:
            raise SourceValidationError(f"baseline {source_key!r} lacks expected input_sha256")
        target = (spec["fixture"], action, SOFT_BUDGET_MS)
        if run_seed == SEED:
            remapped[target] = cell
        else:
            remapped[target] = _remap_cell_to_seed(baseline, cell, run_seed)
    return remapped


def build_repeat_runs(ordered, output_root, *, harness_path=None, dotnet_path=None) -> list:
    """Rebuild the frozen 16 runs with unique repeat names and correct output paths."""
    output_root = Path(output_root)
    runs = []
    for spec in base_specs(ordered):
        for rep in REPS:
            name = f"{spec['name']}-b{SOFT_BUDGET_MS}-r{rep}"
            run = dict(spec)
            run.update(budget_ms=SOFT_BUDGET_MS, base_name=spec["name"], rep=rep,
                       phase=PHASE, name=name, scenario_path=str(spec["scenario_path"]))
            run_dir = parallel.run_dir_path(output_root, run)
            run.update(
                dir=be.rel(run_dir), input=be.rel(run_dir / "input.json"),
                request=be.rel(run_dir / "outer-request.json"),
                command=build_command(run_dir / "outer-request.json", run_dir, name,
                                      SOFT_BUDGET_MS, harness_path=harness_path,
                                      dotnet_path=dotnet_path),
                timeout_seconds=PHASE_TIMEOUT_SECONDS,
            )
            runs.append(run)
    if (len(runs) != MAX_LAUNCHES or len({r["name"] for r in runs}) != MAX_LAUNCHES
            or len({tuple(r["command"]) for r in runs}) != MAX_LAUNCHES):
        raise SourceValidationError(f"expected {MAX_LAUNCHES} unique repeat runs")
    return runs


def combinations(ordered) -> list:
    return [{"name": s["name"], "fixture": s["fixture"], "action": s["action"],
             "candidate": s["candidate"]} for s in base_specs(ordered)]


def collect_provenance(source, historical_request_path, baseline, *, harness_path=None,
                       dotnet_path=None) -> dict:
    provenance = ranking.collect_provenance(
        source, historical_request_path, harness_path=harness_path, dotnet_path=dotnet_path)
    provenance["driver"] = {"path": be.rel(Path(__file__)), "sha256": be.sha256_file(Path(__file__))}
    provenance["parallel_check_helper"] = {
        "path": be.rel(Path(parallel.__file__)), "sha256": be.sha256_file(Path(parallel.__file__))}
    provenance["baseline"] = {"dir": be.rel(baseline["dir"]),
                              "summary": be.rel(baseline["summary_path"]),
                              "summary_sha256": baseline["summary_sha256"]}
    return provenance


def build_plan(runs, source, historical_request_path, baseline, runtime_before, provenance,
               combo_list, *, seed=None, harness_path=None, dotnet_path=None) -> dict:
    run_seed = validate_run_seed(seed)
    return {
        "generated_at": utc_now(), "tool": "building_pick_node_repeat.py", "seed": run_seed,
        "character": CHARACTER, "ascension": ASCENSION, "player_hp": PLAYER_HP,
        "profile": PROFILE, "nodes": NODES, "soft_budget_ms": SOFT_BUDGET_MS,
        "search_timeout_ms": SEARCH_TIMEOUT_MS, "dop": DOP, "max_turns": MAX_TURNS,
        "play_path": PLAY_PATH, "milestone": MILESTONE, "phase": PHASE,
        "max_launches": MAX_LAUNCHES, "base_combinations": BASE_COMBINATIONS,
        "reps": list(REPS), "parallelism": PARALLELISM,
        "phase_timeout_seconds": PHASE_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
        "baseline_budget_ms": BASELINE_BUDGET_MS, "cwd": be.rel(CWD),
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
        "baseline": {"dir": be.rel(baseline["dir"]),
                     "summary": be.rel(baseline["summary_path"]),
                     "summary_sha256": baseline["summary_sha256"]},
        "combinations": combo_list, "runtime_before": runtime_before, "runs": runs,
        "provenance": provenance,
    }


def _int_or_none(value):
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _node_telemetry(run_dir) -> dict:
    """Read only the initial search boundary/expanded nodes for diagnosis."""
    path = Path(run_dir) / RESULT_NAME
    payload, error = be.load_payload(path)
    telemetry = {"result_present": path.exists(), "load_error": error,
                 "time_boundary_observed": None, "expanded_nodes": None,
                 "selected_expanded_nodes": None, "search_boundary": None, "replans": None}
    if not isinstance(payload, dict):
        return telemetry
    observed = payload.get("timeBoundaryObserved")
    telemetry["time_boundary_observed"] = observed if isinstance(observed, bool) else None
    metrics = payload.get("solverMetrics") if isinstance(payload.get("solverMetrics"), dict) else {}
    telemetry["expanded_nodes"] = _int_or_none(metrics.get("TotalExpanded"))
    telemetry["selected_expanded_nodes"] = _int_or_none(metrics.get("SelectedExpanded"))
    prune = payload.get("pruneCounters") if isinstance(payload.get("pruneCounters"), dict) else {}
    boundary = prune.get("boundary", metrics.get("Boundary"))
    telemetry["search_boundary"] = boundary if isinstance(boundary, str) else None
    telemetry["replans"] = _int_or_none(payload.get("replans"))
    return telemetry


class _Overlap:
    """Track the actual number of concurrently executing runs."""

    def __init__(self):
        self._lock, self.active, self.peak = threading.Lock(), 0, 0

    def enter(self):
        with self._lock:
            self.active += 1
            self.peak = max(self.peak, self.active)

    def leave(self):
        with self._lock:
            self.active -= 1


def _run_one(run, output_root, *, overlap, **kwargs) -> dict:
    runner = kwargs.pop("runner")
    def measured_runner(*args, **options):
        overlap.enter()
        try:
            return runner(*args, **options)
        finally:
            overlap.leave()
    record = parallel.execute_run(run, output_root, runner=measured_runner, **kwargs)
    record["rep"], record["base_name"] = run.get("rep"), run.get("base_name")
    record["node_telemetry"] = _node_telemetry(parallel.run_dir_path(output_root, run))
    return record


def _eligible(record) -> bool:
    return bool(isinstance(record, dict) and record.get("status") == STATUS_VERIFIED
                and record.get("eligible"))


def compare_repeat_rankings(left, right) -> dict:
    """Compare two reviewed rankings; order/tie structure only, not exact semantics."""
    if not (isinstance(left, dict) and left.get("available")
            and isinstance(right, dict) and right.get("available")):
        return {"available": False, "reason": "one or both repeat rankings are unavailable",
                "left_reasons": (left or {}).get("reasons"),
                "right_reasons": (right or {}).get("reasons")}

    def index(r):
        return {a: g["rank"] for g in r["tie_groups"] for a in g["actions"]}

    left_index, right_index = index(left), index(right)

    def order(a, b, ranks):
        if ranks.get(a) == ranks.get(b):
            return "tie"
        return a if ranks.get(a, 99) < ranks.get(b, 99) else b

    pairwise = [{"pair": [a, b], "rep1_order": order(a, b, left_index),
                 "rep2_order": order(a, b, right_index),
                 "changed": order(a, b, left_index) != order(a, b, right_index)}
                for i, a in enumerate(ACTIONS) for b in ACTIONS[i + 1:]]
    left_tie = [sorted(g["actions"]) for g in left["tie_groups"]]
    right_tie = [sorted(g["actions"]) for g in right["tie_groups"]]
    return {"available": True, "top_set_rep1": left["top_actions"], "top_set_rep2": right["top_actions"],
            "top_set_changed": set(left["top_actions"]) != set(right["top_actions"]),
            "tie_structure_changed": left_tie != right_tie, "pairwise": pairwise,
            "order_changed": [p for p in pairwise if p["changed"]],
            "agreement": bool(set(left["top_actions"]) == set(right["top_actions"]) and left_tie == right_tie),
            "note": "ranking agreement only; exact terminal semantics are compared separately"}


def build_performance(records, *, parallel_wall, peak_overlap, cpu_count) -> dict:
    values, missing = [], []
    for record in records:
        value = record.get("total_cpu_ms")
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            values.append(value)
        else:
            missing.append(f"{record.get('rep')}/{record.get('run')}")
    cpu_complete = bool(records) and not missing
    cpu_seconds = sum(values) / 1000.0 if values else None
    logical = cpu_count if isinstance(cpu_count, int) and cpu_count > 0 else os.cpu_count()
    busy = cpu_seconds / parallel_wall if (cpu_complete and isinstance(parallel_wall, (int, float))
                                           and parallel_wall > 0) else None
    utilization = busy / logical if busy is not None and isinstance(logical, int) and logical > 0 else None
    return {"parallel_wall_seconds": parallel_wall, "peak_actual_overlap": peak_overlap,
            "runtimes_seconds": [r["runtime_seconds"] for r in records
                                 if isinstance(r.get("runtime_seconds"), (int, float))],
            "cpu": {"runs_with_value": len(values), "runs_missing": len(missing),
                    "missing_runs": missing, "total_cpu_seconds": cpu_seconds,
                    "cpu_complete": cpu_complete, "logical_cpus": logical,
                    "average_busy_cores": busy, "estimated_child_utilization": utilization,
                    "note": "parallel-phase CPU only; a missing CPU value suppresses utilization, never zero"},
            "historical_speed_ratio": None,
            "historical_speed_ratio_note": "not reported: budget, dop and repetition change together"}


def _build_initial_search(ordered) -> dict:
    time_true, expanded, replans = [], {}, []
    for record in ordered:
        telemetry = record.get("node_telemetry") if isinstance(record.get("node_telemetry"), dict) else {}
        name = record.get("run")
        if telemetry.get("time_boundary_observed") is True:
            time_true.append(name)
        if telemetry.get("expanded_nodes") is not None:
            expanded[name] = telemetry["expanded_nodes"]
        if telemetry.get("replans") is not None:
            replans.append(telemetry["replans"])
    return {"node_limit": int(NODES), "time_boundary_observed_true_runs": time_true,
            "time_boundary_observed_count": len(time_true), "available_expanded_nodes": expanded,
            "expanded_nodes_min": min(expanded.values()) if expanded else None,
            "expanded_nodes_max": max(expanded.values()) if expanded else None,
            "replans_total": sum(replans) if replans else None,
            "whole_fight_node_only_established": False,
            "note": "only the initial search boundary is read; harness replans is a count, not "
            "per-turn evidence, so a false timeBoundaryObserved does not establish "
            "whole-fight node-only execution"}


def _pair_comparisons(ordered, plan, enabled, gate):
    by_pair = {}
    for record in ordered:
        by_pair.setdefault((record.get("fixture"), _action(record)), {})[record.get("rep")] = record
    pairs, agreed, available = [], 0, 0
    for combo in plan.get("combinations", []):
        key = (combo["fixture"], _action(combo))
        first, second = by_pair.get(key, {}).get(1), by_pair.get(key, {}).get(2)
        entry = {"fixture": combo["fixture"], "action": key[1], "budget_ms": SOFT_BUDGET_MS,
                 "rep_names": [first and first.get("run"), second and second.get("run")]}
        if not enabled:
            entry.update(available=False, reason=gate, agreement=None)
        elif not (_eligible(first) and _eligible(second)):
            entry.update(available=False, agreement=None,
                         reason="one or both repeats are not verified eligible")
        else:
            left, right = parallel.semantic_tuple(first), parallel.semantic_tuple(second)
            compared = parallel.compare_semantics(left, right)
            entry.update(available=True, agreement=compared["exact_match"],
                         changed_fields=compared["changed_fields"], rep1=left, rep2=right)
            available += 1
            agreed += 1 if compared["exact_match"] else 0
        pairs.append(entry)
    return pairs, agreed, available


def _seed_context(run_seed) -> dict:
    """Describe how a second seed changes the historical comparison."""
    return {
        "run_seed": run_seed, "baseline_seed": SEED,
        "seed_difference": run_seed != SEED,
        "baseline_budget_ms": BASELINE_BUDGET_MS, "run_budget_ms": SOFT_BUDGET_MS,
        "budget_difference": BASELINE_BUDGET_MS != SOFT_BUDGET_MS,
        "dop_difference": True,
        "repeats_are_independent_seeds": False,
        "descriptive_only": True,
        "note": "the historical 20000 ms cells are a single PICKSEL01 observation; for "
                "another seed the comparison changes seed, budget and dop together, is "
                "descriptive only, and the two repeats of one seed are not independent seeds",
    }


def _with_seed_flag(comparison, run_seed) -> dict:
    flagged = dict(comparison) if isinstance(comparison, dict) else {}
    flagged["seed_difference"] = run_seed != SEED
    flagged["baseline_seed"] = SEED
    return flagged


def build_summary(records, *, executed, output_root, plan, source, baseline, runtime_before,
                  runtime_after, parallel_wall, peak_overlap, cpu_count, error,
                  seed=None) -> dict:
    run_seed = validate_run_seed(seed)
    by_name = {record.get("run"): record for record in records}
    ordered = []
    for run in plan["runs"]:
        record = by_name.get(run["name"])
        if record is None:
            record = parallel._missing_record(run, output_root, plan["provenance"])
            record["rep"], record["base_name"], record["node_telemetry"] = (
                run.get("rep"), run.get("base_name"), None)
        ordered.append(record)

    runtime_check = parallel.check_runtime_against_baseline(runtime_before, baseline["runtime"])
    runtime_stable = bool(executed and runtime_before is not None and runtime_after is not None
                          and runtime_before == runtime_after)
    enabled = bool(executed and runtime_check["matches"] and runtime_stable)
    gate = "; ".join(filter(None, (
        None if executed else "dry run",
        None if runtime_check["matches"] else "runtime does not match the accepted baseline",
        None if runtime_stable else "runtime changed between before and after the batch",
    ))) or None

    fixture_ids = ranking._fixture_order(source)
    adapter = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    build_value = {fid: ranking.resolve_build_value_action(adapter[fid]) for fid in fixture_ids}
    pairs, agreed, available = _pair_comparisons(ordered, plan, enabled, gate)

    groups = {}
    for record in ordered:
        groups.setdefault((record.get("fixture"), record.get("rep")), {})[_action(record)] = record
    rankings = {fid: {rep: ranking.build_ranking(groups.get((fid, rep), {}), SOFT_BUDGET_MS,
                                                 build_value[fid]) for rep in REPS}
                for fid in fixture_ids}
    if enabled:
        ranking_agreement = {fid: compare_repeat_rankings(rankings[fid][1], rankings[fid][2])
                             for fid in fixture_ids}
        historical = {fid: {str(rep): _with_seed_flag(parallel.compare_rankings(
            rankings[fid][rep], parallel.baseline_ranking_for(baseline, fid, BASELINE_BUDGET_MS)),
            run_seed) for rep in REPS} for fid in fixture_ids}
    else:
        ranking_agreement = {fid: {"available": False, "reason": gate} for fid in fixture_ids}
        historical = {fid: {str(rep): _with_seed_flag({"available": False, "reason": gate},
                                                      run_seed) for rep in REPS}
                      for fid in fixture_ids}

    cells = parallel.baseline_cells(baseline)
    cell_comparisons = []
    for run in plan["runs"]:
        baseline_record = cells.get((run["fixture"], _action(run), BASELINE_BUDGET_MS))
        baseline_tuple = parallel.semantic_tuple(baseline_record)
        repeat_tuple = parallel.semantic_tuple(by_name.get(run["name"]))
        compared = (parallel.compare_semantics(baseline_tuple, repeat_tuple)
                    if enabled and _eligible(baseline_record) and _eligible(by_name.get(run["name"]))
                    else {"available": False, "exact_match": False, "changed_fields": []})
        cell_comparisons.append({
            "cell": {"fixture": run["fixture"], "action": _action(run),
                     "budget_ms": SOFT_BUDGET_MS, "rep": run.get("rep")},
            "baseline_20000ms": baseline_tuple, "repeat": repeat_tuple, "descriptive": True,
            "seed_difference": run_seed != SEED, "baseline_seed": SEED,
            "available": compared["available"], "exact_semantic_match": compared["exact_match"],
            "changed_fields": compared["changed_fields"]})

    performance = build_performance(ordered, parallel_wall=parallel_wall,
                                    peak_overlap=peak_overlap, cpu_count=cpu_count)
    verified = sum(1 for r in ordered if r.get("status") == STATUS_VERIFIED)
    eligible = sum(1 for r in ordered if r.get("eligible"))
    policy_verified = sum(1 for r in ordered if isinstance(r.get("policy"), dict)
                          and r["policy"].get("ok"))
    accounted = len(ordered) == MAX_LAUNCHES and len(records) == MAX_LAUNCHES
    return {
        "generated_at": utc_now(), "tool": "building_pick_node_repeat.py",
        "executed": bool(executed), "output_dir": be.rel(Path(output_root)), "seed": run_seed,
        "seed_context": _seed_context(run_seed),
        "character": CHARACTER, "ascension": ASCENSION, "player_hp": PLAYER_HP,
        "soft_budget_ms": SOFT_BUDGET_MS, "search_timeout_ms": SEARCH_TIMEOUT_MS,
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
        "repeat_agreement": {"available": available, "agreed": agreed,
                             "disagreed": available - agreed, "total": len(pairs),
                             "comparisons_enabled": enabled, "gate_reason": gate, "pairs": pairs},
        "rankings": rankings, "ranking_agreement": ranking_agreement,
        "historical_20000ms_comparison": historical,
        "cell_comparison_vs_20000ms": cell_comparisons,
        "initial_search": _build_initial_search(ordered), "performance": performance,
        "decision": {"production_adoption": "none", "new_labels": False,
                     "concurrency_safety_claim": False, "causality_claim": False,
                     "variance_claim": False, "speedup_claim": False,
                     "whole_fight_node_only_claim": False, "training": False,
                     "heldout_seeds_used": False,
                     "summary": "Descriptive repeatability check only; no production change, "
                     "no labels, no speedup and no whole-fight node-only guarantee."},
        "baseline": {"dir": be.rel(baseline["dir"]), "summary": be.rel(baseline["summary_path"]),
                     "summary_sha256": baseline["summary_sha256"], "validated": True,
                     "budget_ms": BASELINE_BUDGET_MS},
        "runs": ordered, "plan": plan, "error": error,
        "notes": list(NOTES), "limitations": list(LIMITATIONS),
    }


def run_node_repeat(*, run=False, seed=None, output_root=None, source_dir=None, baseline_dir=None,
                    historical_dir=None, harness_path=None, dotnet_path=None,
                    runtime_config_path=None, runtime_env=None, runner=None, clock=None,
                    cpu_count=None, now=None) -> dict:
    """Run the frozen 16-launch node-repeat plan. ``run=False`` launches nothing."""
    run_seed = validate_run_seed(seed)
    runner = runner if runner is not None else subprocess.run
    clock = clock if clock is not None else time.monotonic
    source_dir = Path(source_dir) if source_dir is not None else bpi.SOURCE_DIR
    baseline_dir = Path(baseline_dir) if baseline_dir is not None else BASELINE_DIR
    historical_dir = Path(historical_dir) if historical_dir is not None else HISTORICAL_DIR
    historical_request_path = historical_dir / "outer-request.json"
    harness_path = Path(harness_path) if harness_path is not None else HARNESS
    dotnet_path = Path(dotnet_path) if dotnet_path is not None else DOTNET
    if runtime_config_path is None:
        runtime_config_path = harness_path.parent / "OfflineSearchHarness.runtimeconfig.json"
    runtime_config_path = Path(runtime_config_path)
    output_root = Path(output_root) if output_root is not None else default_output_root(now)

    source, ordered = ranking.prepare_source(source_dir, seed=run_seed)
    baseline = parallel.load_baseline(baseline_dir)
    parallel.validate_baseline_source(baseline, source, historical_request_path)
    remapped_cells = remap_baseline_cells(baseline, ordered, seed=run_seed)

    try:
        runtime_before = ranking.collect_runtime_provenance(
            runtime_config_path, source["adapter"], harness_path=harness_path,
            dotnet_path=dotnet_path, env=runtime_env)
    except RuntimeValidationError as exc:
        runtime_before = {"ok": False, "error": str(exc)}

    output_root.mkdir(parents=True, exist_ok=False)  # existing output is refused
    runs = build_repeat_runs(ordered, output_root, harness_path=harness_path,
                             dotnet_path=dotnet_path)
    provenance = collect_provenance(source, historical_request_path, baseline,
                                    harness_path=harness_path, dotnet_path=dotnet_path)
    plan = build_plan(runs, source, historical_request_path, baseline, runtime_before,
                      provenance, combinations(ordered), seed=run_seed,
                      harness_path=harness_path, dotnet_path=dotnet_path)
    plan["output_root"] = be.rel(output_root)
    brp.write_json_exclusive(output_root / "plan.json", plan)
    journal_path = ranking.create_exclusive_file(output_root / "journal.jsonl")

    adapter_fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    dry_run, start, records, error = not run, clock(), [], None
    parallel_wall, runtime_after, overlap = 0.0, None, _Overlap()
    try:
        prepared = {}
        if not dry_run:
            if not parallel.check_runtime_against_baseline(runtime_before, baseline["runtime"])["matches"]:
                raise RuntimeValidationError("runtime does not match accepted baseline; no processes launched")
            for run_case in runs:
                prepared[(run_case["phase"], run_case["name"])] = parallel.prepare_run_input(
                    run_case, output_root, historical_request_path, remapped_cells)
        phase_start = clock()
        with ThreadPoolExecutor(max_workers=PARALLELISM) as pool:
            futures = {pool.submit(
                _run_one, run_case, output_root, overlap=overlap,
                adapter_fixture=adapter_fixtures[run_case["fixture"]], prepared=prepared,
                runner=runner, clock=clock, start=start, dry_run=dry_run,
                provenance=provenance, phase_cap=PHASE_TIMEOUT_SECONDS): run_case
                for run_case in runs}
            for future in as_completed(futures):  # journal in the main thread
                run_case = futures[future]
                try:
                    record = future.result()
                except BaseException as exc:  # noqa: BLE001
                    record = parallel._missing_record(run_case, output_root, provenance)
                    record.update(rep=run_case.get("rep"), base_name=run_case.get("base_name"),
                                  node_telemetry=None,
                                  reason=f"future failed: {type(exc).__name__}: {exc}")
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
        parallel_wall=parallel_wall, peak_overlap=overlap.peak, cpu_count=cpu_count,
        error=error, seed=run_seed)
    brp.write_json_exclusive(output_root / "summary.json", summary)
    return summary


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Repeatability check of the accepted pick actions at nodes 6000 / dop 2")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--run", action="store_true",
                       help="execute the 16 fixed launches (default: dry run)")
    group.add_argument("--dry-run", action="store_true",
                       help="write the plan only; no subprocess (default)")
    parser.add_argument("mode", nargs="?", choices=["dryonly"],
                        help="'dryonly' is an explicit alias for the default dry run")
    parser.add_argument("--seed", choices=list(SUPPORTED_SEEDS), default=SEED,
                        help="development seed; PICKSEL01 (default) or PICKSEL02")
    args = parser.parse_args(argv)
    execute = bool(args.run and args.mode != "dryonly")
    try:
        summary = run_node_repeat(run=execute, seed=args.seed)
    except SourceValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    except FileExistsError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 3
    except BaselineValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 5
    if execute:
        print(f"attempts={summary['batch']['total_attempts']} verified={summary['verified']} "
              f"eligible={summary['eligible_completed']} "
              f"policy_verified={summary['policy_verified']} "
              f"agreed={summary['repeat_agreement']['agreed']}/"
              f"{summary['repeat_agreement']['available']} "
              f"comparisons_enabled={summary['repeat_agreement']['comparisons_enabled']} "
              f"tool_completed={summary['tool_completed']}", flush=True)
    else:
        print(f"attempts={summary['batch']['total_attempts']} "
              f"combinations={summary['batch']['base_combinations']} "
              f"reps={summary['batch']['reps']} dop={DOP} nodes={NODES} "
              f"budget_ms={SOFT_BUDGET_MS} executed=false "
              f"status_counts={summary['status_counts']}", flush=True)
    print(f"wrote {Path(summary['output_dir']) / 'summary.json'}", flush=True)
    if not execute:
        return 0
    return 0 if summary["tool_completed"] else 1


if __name__ == "__main__":
    sys.exit(main())
