#!/usr/bin/env python3
"""Bounded 2x2 budget / internal-dop calibration probe for the building oracle.

The repeatability probe (`scripts/building_repeat_probe.py`) showed that four
runs of the *same* PM001 input do not agree, but it could not separate the soft
time budget, the node ceiling, the internal degree of parallelism and the
process scheduling because it only had one budget and one dop. This instrument
runs a fixed 2x2 design instead:

    budget_ms in {4000, 20000}  x  dop in {1, 2}

Every cell is measured with **two replicates** and every child process runs
**sequentially** (there is no cross-process concurrency). The run order is a
blocked design so drift does not line up with one factor:

* block 1 (replicate 1): (4000,1), (20000,2), (4000,2), (20000,1)
* block 2 (replicate 2): the reverse of block 1

The input is the frozen historical fixture
``work/oracle/shiv-pm/b4000-hp1.0-dmg1.0-P0M0-PM001`` (SILENT, seed PM001,
SOUL_NEXUS_ELITE). Every run copies the same ``input.json`` byte-for-byte,
rewrites only the outer request through the shared
``building_repeat_probe.write_run_inputs`` helper, and re-checks the resolved
scenario's seed / character / encounter before a run is allowed to count.

Why the soft budget matters (see docs/building/BUDGET_CALIBRATION.md for the
line-cited discussion): ``--budget-ms`` is wired into
``ModRuntime.SolveEvaluate`` as ``Profile.SoftTimeBudgetMilliseconds`` and
``CombatBeamSolver`` stops on that wall clock as well as on the node ceiling.
``--dop`` is clamped into ``expansionParallelism`` inside
``CombatBeamSolver.Phases``. So neither a fixed node count nor ``FixedBudget``
turns this into a pure node-deterministic search.

Instrument rules:

* the default command is a **dry run** that launches nothing and writes only the
  plan; ``--run`` is required before any process starts;
* each subprocess may run at most 120 s and the whole batch at most 720 s of
  monotonic time; every launch gets ``min(120, remaining)``, recomputed *after*
  input preparation, and a launch after the deadline is recorded
  ``not_started`` (never a battle result);
* at most 8 launches, all accounted for in the summary even when skipped;
* the plan is written before any launch into a unique microsecond-stamped
  directory; a journal line is appended after every attempt and summary.json is
  written even when the batch fails, so partial evidence survives;
* outcomes are classified by the shared ``building_eval.classify`` API, so a
  timeout, a launch error or an incomplete fight can never be read as a
  completed win or loss.

This instrument answers "did the tool run and what did each configuration
produce", not "is the model repeatable or better". It deliberately does not set
a study gate or approve a model. Two replicates per cell cannot estimate
variance or rank candidates, and the fixed deck has no candidate set at all.

Usage:
    python scripts/building_budget_probe.py          # dry run, writes plan only
    python scripts/building_budget_probe.py --run    # execute the eight runs
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import building_eval as be
import building_repeat_probe as brp

ROOT = brp.ROOT

# Fixed repository paths, reused from the repeat probe module constants. They
# are read, never written: this instrument must not mutate module globals.
HARNESS = brp.HARNESS
DOTNET = brp.DOTNET
CWD = brp.CWD
SOURCE_DIR = brp.SOURCE_DIR
RESULT_NAME = brp.RESULT_NAME

OUTPUT_ROOT = ROOT / "work" / "budget-calibration"

SEED = brp.SEED
CHARACTER = brp.CHARACTER
ENCOUNTER = brp.ENCOUNTER

# Fixed harness flags shared by every one of the eight commands.
PROFILE = "Medium"
NODES = "6000"
SEARCH_TIMEOUT_MS = "60000"
PLAY_PATH = "gameaction"
MAX_TURNS = "40"
MILESTONE = "M2"

BUDGETS_MS = (4000, 20000)
DOPS = (1, 2)
REPLICATES_PER_CELL = 2
CELLS_PER_REPLICATE = 4
MAX_LAUNCHES = REPLICATES_PER_CELL * len(BUDGETS_MS) * len(DOPS)
BATCH_DEADLINE_SECONDS = 720
PER_RUN_TIMEOUT_SECONDS = 120

# Block 1 forward, block 2 reverse. Each entry is (budget_ms, dop).
BLOCK1 = ((4000, 1), (20000, 2), (4000, 2), (20000, 1))
BLOCK2 = tuple(reversed(BLOCK1))
BLOCK_ORDERS = (BLOCK1, BLOCK2)


def cell_key(budget_ms, dop) -> str:
    return f"b{int(budget_ms)}-d{int(dop)}"


CELL_ORDER = tuple(
    cell_key(budget, dop) for budget in BUDGETS_MS for dop in DOPS
)

SEMANTIC_FIELDS = ("status", "player_hp", "max_hp", "final_enemy_hp", "turns")

STATUS_DRY_RUN = brp.STATUS_DRY_RUN
STATUS_NOT_STARTED = brp.STATUS_NOT_STARTED

# The two scale variables are the only environment changes. The full child
# environment is passed through but is never written to any artifact.
ENV_OVERRIDES = dict(brp.ENV_OVERRIDES)

NOTES = [
    "Two replicates per cell are a bounded screen, not a variance estimate; no "
    "significance or causal claim is made about budget, dop or their interaction.",
    "timeBoundaryObserved is the initial-search boundary indicator only. It is not "
    "proof that no replan during deployment also hit a time boundary.",
    "FixedBudget=true does not disable the soft time budget: --budget-ms becomes "
    "Profile.SoftTimeBudgetMilliseconds inside ModRuntime.SolveEvaluate, and "
    "CombatBeamSolver stops on that wall clock as well as on the node ceiling.",
    "Within-cell terminal semantic equality is reported only when both replicates "
    "classified as a complete win or loss; otherwise it stays null.",
    "This instrument does not set a study gate and does not approve a model; "
    "study_gate_passed is intentionally null.",
    "No ranking conclusion is possible here: the fixed deck has no candidate set.",
]

LIMITATIONS = [
    "The probe launches the real offline harness; every result is a real battle "
    "and is only as trustworthy as the harness build it ran.",
    "Two replicates cannot separate run-to-run noise from a budget/dop effect.",
    "Solver metrics beyond boundary and expanded-node counts stay configuration "
    "evidence; they are not compared for improvement.",
    "The batch deadline is monotonic wall clock. A slow host can mark later cases "
    "not_started, which is recorded, not hidden.",
]


def build_specs():
    """The fixed eight-attempt schedule with two replicates per cell."""
    specs = []
    for replicate, order in enumerate(BLOCK_ORDERS, start=1):
        if len(order) != CELLS_PER_REPLICATE:
            raise ValueError(f"block {replicate} must hold {CELLS_PER_REPLICATE} cells")
        for budget_ms, dop in order:
            specs.append(
                {
                    "name": f"r{replicate}-{cell_key(budget_ms, dop)}",
                    "replicate": replicate,
                    "block": replicate,
                    "budget_ms": int(budget_ms),
                    "dop": int(dop),
                    "cell": cell_key(budget_ms, dop),
                }
            )

    if len(specs) != MAX_LAUNCHES:
        raise AssertionError(f"expected {MAX_LAUNCHES} launches, built {len(specs)}")
    counts = {key: 0 for key in CELL_ORDER}
    for spec in specs:
        counts[spec["cell"]] += 1
    for key, count in counts.items():
        if count != REPLICATES_PER_CELL:
            raise AssertionError(f"cell {key} has {count} replicates")
    return specs


def default_output_root(now=None) -> Path:
    """A fresh UTC timestamp directory with microsecond resolution."""
    moment = now if now is not None else datetime.now(timezone.utc)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    stamp = moment.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    return OUTPUT_ROOT / stamp


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def sha256_file(path):
    return brp.sha256_file(path)


def create_exclusive_file(path) -> Path:
    """Create an empty file with O_EXCL; an existing journal is never appended to."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(path), os.O_WRONLY | os.O_CREAT | os.O_EXCL)
    os.close(fd)
    return path


def append_journal(path, record) -> None:
    """Append one JSON line and flush so a crashed batch keeps its attempts."""
    line = json.dumps(record, ensure_ascii=False) + "\n"
    with Path(path).open("a", encoding="utf-8") as handle:
        handle.write(line)
        handle.flush()
        os.fsync(handle.fileno())


def compute_timeout(elapsed_seconds: float) -> float:
    """Per-launch cap: at most 120 s and never past the 720 s batch deadline."""
    remaining = BATCH_DEADLINE_SECONDS - elapsed_seconds
    if remaining <= 0:
        return 0.0
    return float(min(PER_RUN_TIMEOUT_SECONDS, remaining))


def expected_scenario(source_dir) -> dict | None:
    """The seed/character/encounter the copied scenario request must resolve to."""
    try:
        data = json.loads(
            (Path(source_dir) / "input.json").read_text(encoding="utf-8")
        )
    except (OSError, ValueError):
        return None
    if not isinstance(data, dict):
        return None
    return {
        "seed": data.get("seed"),
        "character": data.get("characterId"),
        "encounter": data.get("encounterId"),
    }


def collect_provenance(source_dir) -> dict:
    source_dir = Path(source_dir)
    return {
        "source_input": be.rel(source_dir / "input.json"),
        "source_input_sha256": sha256_file(source_dir / "input.json"),
        "source_request": be.rel(source_dir / "outer-request.json"),
        "source_request_sha256": sha256_file(source_dir / "outer-request.json"),
        "driver": {"path": be.rel(Path(__file__)), "sha256": sha256_file(Path(__file__))},
        "classifier": {
            "path": be.rel(Path(be.__file__)),
            "sha256": sha256_file(Path(be.__file__)),
        },
        "harness": {"path": be.rel(HARNESS), "sha256": sha256_file(HARNESS)},
        "runtime": {"path": be.rel(DOTNET), "sha256": sha256_file(DOTNET)},
    }


def build_command(request_path, run_dir, name, budget_ms, dop) -> list:
    return [
        str(DOTNET),
        str(HARNESS),
        "--request", str(request_path),
        "--profile", PROFILE,
        "--nodes", NODES,
        "--budget-ms", str(int(budget_ms)),
        "--search-timeout-ms", SEARCH_TIMEOUT_MS,
        "--dop", str(int(dop)),
        "--no-card-probe",
        "--deploy-plan",
        "--play-path", PLAY_PATH,
        "--max-turns", MAX_TURNS,
        "--milestone", MILESTONE,
        "--label", name,
        "--out", str(run_dir),
    ]


def build_plan(output_root, source_dir, provenance, specs, expected) -> dict:
    output_root = Path(output_root)
    runs = []
    for spec in specs:
        run_dir = output_root / spec["name"]
        runs.append(
            {
                "name": spec["name"],
                "replicate": spec["replicate"],
                "block": spec["block"],
                "cell": spec["cell"],
                "budget_ms": spec["budget_ms"],
                "dop": spec["dop"],
                "dir": be.rel(run_dir),
                "input": be.rel(run_dir / "input.json"),
                "request": be.rel(run_dir / "outer-request.json"),
                "command": build_command(
                    run_dir / "outer-request.json",
                    run_dir,
                    spec["name"],
                    spec["budget_ms"],
                    spec["dop"],
                ),
                "timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
            }
        )
    return {
        "generated_at": utc_now(),
        "tool": "building_budget_probe.py",
        "seed": SEED,
        "character": CHARACTER,
        "encounter": ENCOUNTER,
        "expected_scenario": expected,
        "source": {
            "dir": be.rel(Path(source_dir)),
            "input": provenance["source_input"],
            "request": provenance["source_request"],
        },
        "output_root": be.rel(output_root),
        "cwd": be.rel(CWD),
        "profile": PROFILE,
        "nodes": NODES,
        "search_timeout_ms": SEARCH_TIMEOUT_MS,
        "play_path": PLAY_PATH,
        "max_turns": MAX_TURNS,
        "milestone": MILESTONE,
        "budgets_ms": list(BUDGETS_MS),
        "dops": list(DOPS),
        "replicates_per_cell": REPLICATES_PER_CELL,
        "max_launches": MAX_LAUNCHES,
        "per_run_timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
        "env_overrides": dict(ENV_OVERRIDES),
        "block_orders": {
            str(replicate): [cell_key(budget, dop) for budget, dop in order]
            for replicate, order in enumerate(BLOCK_ORDERS, start=1)
        },
        "runs": runs,
        "provenance": provenance,
    }


def _new_record(spec, run_dir, provenance) -> dict:
    run_dir = Path(run_dir)
    return {
        "run": spec["name"],
        "replicate": spec["replicate"],
        "block": spec["block"],
        "cell": spec["cell"],
        "budget_ms": spec["budget_ms"],
        "dop": spec["dop"],
        "dir": be.rel(run_dir),
        "status": None,
        "reason": None,
        "launched": False,
        "timed_out": False,
        "exit": None,
        "timeout_seconds": None,
        "timeout_before_preparation_seconds": None,
        "elapsed_before_launch": None,
        "elapsed_before_subprocess": None,
        "runtime_seconds": None,
        "player_hp": None,
        "max_hp": None,
        "final_enemy_hp": None,
        "turns": None,
        "fight_wall_ms": None,
        "time_boundary_observed": None,
        "solver_metrics": None,
        "prune_counters": None,
        "replans": None,
        "search_policy": None,
        "budget_payload": None,
        "stdout_path": be.rel(run_dir / "stdout.bin"),
        "stderr_path": be.rel(run_dir / "stderr.bin"),
        "raw_result_path": be.rel(run_dir / RESULT_NAME),
        "harness_result_sha256": None,
        "args": [],
        "provenance": dict(provenance),
    }


def _missing_record(spec, output_root, provenance) -> dict:
    record = _new_record(spec, Path(output_root) / spec["name"], provenance)
    record["status"] = be.STATUS_ERROR
    record["reason"] = "case was not recorded before the batch ended"
    return record


def check_resolved_scenario(payload, expected) -> str | None:
    """Return a mismatch reason, or None when resolvedScenario matches expected."""
    if expected is None:
        return "expected scenario could not be derived from the source input"
    if not isinstance(payload, dict):
        return "payload is not a JSON object"
    resolved = payload.get("resolvedScenario")
    if not isinstance(resolved, dict):
        return "resolvedScenario absent or not an object"
    for label, key in (("seed", "Seed"), ("character", "CharacterId"), ("encounter", "EncounterId")):
        got = resolved.get(key)
        want = expected.get(label)
        if got != want:
            return f"resolvedScenario {key}={got!r} != expected {want!r}"
    return None


def _payload_error(payload):
    if not isinstance(payload, dict):
        return None
    error = payload.get("error")
    if isinstance(error, dict):
        return error.get("message") or error.get("code") or "payload error"
    return error or None


def _execute_case(spec, output_root, *, source_dir, runner, clock, start, dry_run, provenance, expected) -> dict:
    run_dir = Path(output_root) / spec["name"]
    record = _new_record(spec, run_dir, provenance)

    if dry_run:
        record["status"] = STATUS_DRY_RUN
        record["reason"] = "dry run: subprocess not launched"
        return record

    elapsed = clock() - start
    record["elapsed_before_launch"] = elapsed
    timeout = compute_timeout(elapsed)
    record["timeout_before_preparation_seconds"] = timeout
    if timeout <= 0:
        record["status"] = STATUS_NOT_STARTED
        record["reason"] = (
            f"batch deadline exhausted before launch: elapsed {elapsed:.3f}s "
            f">= {BATCH_DEADLINE_SECONDS}s"
        )
        return record

    input_path, request_path = brp.write_run_inputs(run_dir, source_dir, spec["name"])
    record["provenance"]["input_sha256"] = sha256_file(input_path)
    record["provenance"]["request_sha256"] = sha256_file(request_path)
    command = build_command(request_path, run_dir, spec["name"], spec["budget_ms"], spec["dop"])
    record["args"] = command

    prepared = clock() - start
    record["elapsed_before_subprocess"] = prepared
    # Recompute after preparation so preparation time is charged to the batch.
    timeout = compute_timeout(prepared)
    record["timeout_seconds"] = timeout
    if timeout <= 0:
        record["status"] = STATUS_NOT_STARTED
        record["reason"] = (
            f"batch deadline exhausted during input preparation: elapsed "
            f"{prepared:.3f}s >= {BATCH_DEADLINE_SECONDS}s"
        )
        return record

    record["launched"] = True
    stdout = b""
    stderr = b""
    exit_code = None
    timed_out = False
    launch_error = None
    started = clock()
    try:
        proc = runner(
            command,
            cwd=str(CWD),
            capture_output=True,
            timeout=timeout,
            env=brp.build_env(),
        )
        stdout = proc.stdout or b""
        stderr = proc.stderr or b""
        exit_code = proc.returncode
    except subprocess.TimeoutExpired as exc:
        # subprocess.run has already killed and reaped the child; keep its bytes.
        timed_out = True
        stdout = exc.stdout or b""
        stderr = exc.stderr or b""
    except OSError as exc:
        launch_error = exc
    record["runtime_seconds"] = clock() - started

    brp.write_bytes(run_dir / "stdout.bin", stdout)
    brp.write_bytes(run_dir / "stderr.bin", stderr)
    record["timed_out"] = timed_out
    record["exit"] = exit_code

    raw_path = run_dir / RESULT_NAME
    payload, load_error = be.load_payload(raw_path)
    if raw_path.exists():
        record["harness_result_sha256"] = sha256_file(raw_path)

    if launch_error is not None:
        result = be.classify(
            {"timeout": False},
            {"error": {"message": f"launch failed: {launch_error}"}},
            None,
        )
    else:
        result = be.classify({"exit": exit_code, "timeout": timed_out}, payload, load_error)
        mismatch = None
        if result["status"] in (be.STATUS_WIN, be.STATUS_LOSS, be.STATUS_INCOMPLETE) and isinstance(payload, dict):
            mismatch = check_resolved_scenario(payload, expected)
        if mismatch:
            result["status"] = be.STATUS_INVALID
            result["reason"] = f"resolvedScenario mismatch: {mismatch}"

    record["status"] = result["status"]
    record["reason"] = result["reason"]
    record["player_hp"] = result.get("player_hp")
    record["max_hp"] = result.get("max_hp")
    record["final_enemy_hp"] = result.get("final_enemy_hp")
    record["turns"] = result.get("turns")
    record["fight_wall_ms"] = result.get("fight_wall_ms")

    if isinstance(payload, dict):
        record["time_boundary_observed"] = payload.get("timeBoundaryObserved")
        record["solver_metrics"] = payload.get("solverMetrics")
        record["prune_counters"] = payload.get("pruneCounters")
        record["replans"] = payload.get("replans")
        record["search_policy"] = payload.get("searchPolicy")
        record["budget_payload"] = payload.get("budget")
    return record


def execute_case(spec, output_root, **kwargs) -> dict:
    """Run one case; any preparation/decoding failure becomes an error record."""
    try:
        return _execute_case(spec, output_root, **kwargs)
    except Exception as exc:
        record = _new_record(spec, Path(output_root) / spec["name"], kwargs["provenance"])
        record["status"] = be.STATUS_ERROR
        record["reason"] = f"probe record failed: {type(exc).__name__}: {exc}"
        record["launched"] = None  # failure may have occurred before or after launch
        return record


def semantic_tuple(record: dict) -> dict:
    record = record if isinstance(record, dict) else {}
    return {field: record.get(field) for field in SEMANTIC_FIELDS}


def _range(values) -> dict:
    values = [value for value in values if value is not None]
    if not values:
        return {"n": 0, "min": None, "max": None, "values": []}
    return {"n": len(values), "min": min(values), "max": max(values), "values": values}


def _status_counts(records) -> dict:
    counts = {status: 0 for status in (*be.STATUS_ORDER, STATUS_DRY_RUN, STATUS_NOT_STARTED)}
    for record in records:
        status = record.get("status")
        if status in counts:
            counts[status] += 1
    return counts


def _compact_metrics(record) -> dict:
    prune = record.get("prune_counters")
    boundary = None
    total_expanded = None
    if isinstance(prune, dict):
        boundary = prune.get("boundary")
        total_expanded = prune.get("totalExpanded")
    solver = record.get("solver_metrics")
    if boundary is None and isinstance(solver, dict):
        boundary = solver.get("boundary") or solver.get("Boundary")
    if total_expanded is None and isinstance(solver, dict):
        total_expanded = solver.get("totalExpanded") or solver.get("TotalExpanded")
    return {
        "run": record.get("run"),
        "status": record.get("status"),
        "boundary": boundary,
        "total_expanded": total_expanded,
        "replans": record.get("replans"),
    }


def summarize_cell(key, records) -> dict:
    complete = [record for record in records if record.get("status") in be.COMPLETE_STATUSES]
    both_complete = len(records) == REPLICATES_PER_CELL and len(complete) == len(records)

    semantic_equal = None
    semantic_diff_fields = []
    semantic_reason = "not all replicates classified complete"
    if both_complete:
        baseline = semantic_tuple(complete[0])
        semantic_equal = all(semantic_tuple(record) == baseline for record in complete)
        semantic_diff_fields = [
            field
            for field in SEMANTIC_FIELDS
            if any(semantic_tuple(record)[field] != baseline[field] for record in complete)
        ]
        semantic_reason = "compared" if semantic_equal else "complete but semantically different"

    observed = sum(1 for record in records if record.get("time_boundary_observed") is True)
    known = sum(1 for record in records if record.get("time_boundary_observed") is not None)

    return {
        "cell": key,
        "records": len(records),
        "status_counts": _status_counts(records),
        "complete": len(complete),
        "semantic_equal": semantic_equal,
        "semantic_diff_fields": semantic_diff_fields,
        "semantic_reason": semantic_reason,
        "player_hp": _range([record.get("player_hp") for record in complete]),
        "turns": _range([record.get("turns") for record in complete]),
        "time_boundary": {
            "observed": observed,
            "known": known,
            "indicator": "initial-search time boundary flag; not proof about replans",
            "values": [record.get("time_boundary_observed") for record in records],
        },
        "solver": [_compact_metrics(record) for record in records],
    }


def cross_cell_rows(cells) -> list:
    rows = []
    for key in CELL_ORDER:
        cell = cells.get(key, {})
        rows.append(
            {
                "cell": key,
                "budget_ms": int(key.split("-")[0][1:]),
                "dop": int(key.split("-")[1][1:]),
                "status_counts": cell.get("status_counts"),
                "complete": cell.get("complete"),
                "semantic_equal": cell.get("semantic_equal"),
                "player_hp": cell.get("player_hp"),
                "turns": cell.get("turns"),
                "time_boundary": cell.get("time_boundary"),
            }
        )
    return rows


def build_summary(records, *, executed, output_root, plan, error=None) -> dict:
    by_name = {record["run"]: record for record in records}
    ordered = [by_name[run["name"]] for run in plan["runs"]]

    spec_by_name = {run["name"]: run for run in plan["runs"]}
    for record in ordered:
        spec = spec_by_name.get(record["run"], {})
        if record.get("budget_ms") is None:
            record["budget_ms"] = spec.get("budget_ms")
        if record.get("dop") is None:
            record["dop"] = spec.get("dop")

    cells = {}
    for key in CELL_ORDER:
        cells[key] = summarize_cell(
            key, [record for record in ordered if record.get("cell") == key]
        )
    cross_cell = cross_cell_rows(cells)
    for row in cross_cell:
        key = row["cell"]
        row["budget_ms"] = int(key.split("-")[0][1:])
        row["dop"] = int(key.split("-")[1][1:])

    status_counts = _status_counts(ordered)
    terminal_complete = all(
        record.get("status") in be.COMPLETE_STATUSES for record in ordered
    )
    attempts_accounted = len(ordered) == MAX_LAUNCHES and len(records) == MAX_LAUNCHES
    tool_completed = attempts_accounted and error is None

    if not terminal_complete:
        finding = "not_established: one or more attempts did not classify complete"
    elif all(
        cell["semantic_equal"] is True for cell in cells.values()
    ):
        finding = "within_cell_semantic_equal for all cells"
    elif any(cell["semantic_equal"] is False for cell in cells.values()):
        finding = "within_cell_semantic_different for at least one cell"
    else:
        finding = "terminal_complete but semantic comparison unavailable"

    return {
        "generated_at": utc_now(),
        "tool": "building_budget_probe.py",
        "seed": plan["seed"],
        "character": plan["character"],
        "encounter": plan["encounter"],
        "executed": bool(executed),
        "output_dir": be.rel(Path(output_root)),
        "batch": {
            "total_attempts": MAX_LAUNCHES,
            "attempts_accounted": attempts_accounted,
            "per_run_timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
            "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
            "budgets_ms": list(BUDGETS_MS),
            "dops": list(DOPS),
            "replicates_per_cell": REPLICATES_PER_CELL,
        },
        "status_counts": status_counts,
        "complete": sum(
            1 for record in ordered if record.get("status") in be.COMPLETE_STATUSES
        ),
        "terminal_complete": terminal_complete,
        "tool_completed": tool_completed,
        "repeatability_finding": finding,
        "study_gate_passed": None,
        "cells": cells,
        "cross_cell": cross_cell,
        "runs": ordered,
        "plan": plan,
        "error": error,
        "notes": list(NOTES),
        "limitations": list(LIMITATIONS),
    }


def _write_summary(output_root, records, *, executed, plan, provenance, error) -> dict:
    by_name = {record["run"]: record for record in records}
    ordered = []
    for run in plan["runs"]:
        ordered.append(
            by_name.get(run["name"])
            or _missing_record(
                {
                    "name": run["name"],
                    "replicate": run["replicate"],
                    "block": run["block"],
                    "cell": run["cell"],
                    "budget_ms": run["budget_ms"],
                    "dop": run["dop"],
                },
                output_root,
                provenance,
            )
        )
    summary = build_summary(
        ordered, executed=executed, output_root=output_root, plan=plan, error=error
    )
    brp.write_json_exclusive(Path(output_root) / "summary.json", summary)
    return summary


def run_probe(*, run=False, output_root=None, source_dir=None, runner=None, clock=None, now=None) -> dict:
    """Run the fixed eight-attempt plan. ``run=False`` (the default) launches nothing."""
    runner = runner if runner is not None else subprocess.run
    clock = clock if clock is not None else time.monotonic
    source_dir = Path(source_dir) if source_dir is not None else SOURCE_DIR
    output_root = Path(output_root) if output_root is not None else default_output_root(now)

    specs = build_specs()
    provenance = collect_provenance(source_dir)
    expected = expected_scenario(source_dir)

    # Refuse an existing output directory: the timestamp is expected to be unique.
    output_root.mkdir(parents=True, exist_ok=False)
    plan = build_plan(output_root, source_dir, provenance, specs, expected)
    brp.write_json_exclusive(output_root / "plan.json", plan)
    journal_path = create_exclusive_file(output_root / "journal.jsonl")

    dry_run = not run
    start = clock()
    records = []
    error = None
    try:
        for spec in specs:
            record = execute_case(
                spec,
                output_root,
                source_dir=source_dir,
                runner=runner,
                clock=clock,
                start=start,
                dry_run=dry_run,
                provenance=provenance,
                expected=expected,
            )
            records.append(record)
            append_journal(journal_path, record)
    except BaseException as exc:  # noqa: BLE001 - the journal already holds attempts
        error = f"{type(exc).__name__}: {exc}"

    summary = _write_summary(
        output_root, records, executed=run, plan=plan, provenance=provenance, error=error
    )
    return summary


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Bounded 2x2 building budget/dop probe")
    group = parser.add_mutually_exclusive_group()
    group.add_argument(
        "--run", action="store_true", help="execute the eight fixed runs (default: dry run)"
    )
    group.add_argument(
        "--dry-run", action="store_true", help="write the plan only; no subprocess (default)"
    )
    args = parser.parse_args(argv)

    summary = run_probe(run=args.run)
    if args.run:
        print(
            f"attempts={summary['batch']['total_attempts']} "
            f"complete={summary['complete']} "
            f"terminal_complete={summary['terminal_complete']} "
            f"tool_completed={summary['tool_completed']} "
            f"finding={summary['repeatability_finding']}",
            flush=True,
        )
    else:
        print(json.dumps(summary["plan"], ensure_ascii=False, indent=1), flush=True)
    print(f"wrote {Path(summary['output_dir']) / 'summary.json'}", flush=True)
    if not args.run:
        return 0
    return 0 if summary["terminal_complete"] else 1


if __name__ == "__main__":
    sys.exit(main())
