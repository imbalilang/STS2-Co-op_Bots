#!/usr/bin/env python3
"""Bounded serial-vs-parallel replay of the accepted pick-ranking screen.

This instrument answers one deliberately narrow engineering question: **when the
sixteen accepted ranking-screen cells are replayed with ``--dop 2`` through a
single 16-way process pool, do the inputs stay byte-identical, does the actual
harness accept the requested ``dop=2``/budget/nodes, and how do the observed
outcomes and descriptive rankings compare with the accepted ``--dop 1``
baseline?**

It is *not* a strength, speed or safety instrument:

* the accepted baseline is the frozen, independently verified run
  ``work/pick-ranking-screen/20260917T141513.123416Z`` (16 runs, ``--dop 1``,
  seed ``PICKSEL01``, nodes 6000, budgets 4000/20000 ms, ``--max-turns 40``);
  its raw ``input/request/loadout/result`` hashes are re-verified from disk
  before any historical outcome is treated as a reference, and the accepted
  fixture source hashes must still match the baseline plan;
* the design is fixed at 20 launches: four **sequential** ``--dop 2`` reference
  fights on the late fixture (BACKFLIP and DAGGER_SPRAY at both budgets) and
  then **sixteen concurrent** ``--dop 2`` subprocesses that reproduce every
  baseline cell; only ``--dop`` (and the artifact paths/labels) differ from the
  baseline command, and no module global is mutated;
* a launch is only attempted with ``min(phase_cap, remaining)`` recomputed
  *after* input preparation, where preparation and pool queue wait are charged
  to the single 900 s monotonic deadline; a launch after the deadline is
  recorded ``not_started`` (never a fight);
* an input-status (``status``) is kept strictly separate from a fight
  ``outcome``; a timeout, error, mismatch, missing result or incomplete fight
  can never become a loss or ranking evidence, and a fixture/budget with any
  incomplete cell cannot be ranked;
* the fresh four references are single observations compared against both the
  historical ``--dop 1`` cells and the concurrent ``--dop 2`` cells to *show*
  differences; no causality is attributed, because the parallelism and the
  ``dop`` change at the same time;
* the reported historical throughput ratio (450.391 s of historical sequential
  subprocess time / new parallel phase wall) is a descriptive historical ratio,
  **not** a controlled speedup and **not** a single-fight speedup.

The default command is a dry run that launches nothing and writes only the plan
and a dry-run summary; ``--run`` is required before any process starts.

Usage:
    python scripts/building_pick_parallel_check.py          # dry run, plan only
    python scripts/building_pick_parallel_check.py --run    # execute the 20 launches
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path

import building_eval as be
import building_pick_input_probe as bpi
import building_pick_ranking_screen as ranking
import building_repeat_probe as brp

ROOT = brp.ROOT

# Fixed repository paths, reused read-only from the accepted helper modules.
HARNESS = brp.HARNESS
DOTNET = brp.DOTNET
CWD = brp.CWD
RESULT_NAME = brp.RESULT_NAME

SOURCE_DIR = bpi.SOURCE_DIR
HISTORICAL_DIR = bpi.HISTORICAL_DIR
OUTPUT_ROOT = ROOT / "work" / "pick-parallel-check"
BASELINE_DIR = ROOT / "work" / "pick-ranking-screen" / "20260917T141513.123416Z"

# Frozen design constants. None of these is ever assigned at run time.
SEED = ranking.SEED
BUDGETS = tuple(ranking.BUDGETS)
CHARACTER = bpi.CHARACTER
ASCENSION = bpi.ASCENSION
PLAYER_HP = bpi.PLAYER_HP
ACTIONS = tuple(ranking.ACTIONS)

PROFILE = ranking.PROFILE
NODES = ranking.NODES
SEARCH_TIMEOUT_MS = ranking.SEARCH_TIMEOUT_MS
DOP = "2"
PLAY_PATH = ranking.PLAY_PATH
MAX_TURNS = ranking.MAX_TURNS
MILESTONE = ranking.MILESTONE

REFERENCE_FIXTURE = "silent-late-pm001-proxy"
REFERENCE_ACTIONS = ("BACKFLIP", "DAGGER_SPRAY")
REFERENCE_BUDGETS = (4000, 20000)

REFERENCE_LAUNCHES = 4
PARALLEL_LAUNCHES = 16
MAX_LAUNCHES = REFERENCE_LAUNCHES + PARALLEL_LAUNCHES
REFERENCE_TIMEOUT_SECONDS = 120
PARALLEL_TIMEOUT_SECONDS = 300
BATCH_DEADLINE_SECONDS = 900
PARALLELISM = 16

# Historical sum of the accepted baseline's per-run subprocess seconds.
HISTORICAL_SEQUENTIAL_SECONDS = 450.391

BASELINE_TOOL = "building_pick_ranking_screen.py"
BASELINE_ATTEMPTS = 16

# Baseline raw files and the summary hash keys that pin them.
BASELINE_FILES = (
    ("input_sha256", "input.json"),
    ("request_sha256", "outer-request.json"),
    ("stdout_sha256", "stdout.bin"),
    ("stderr_sha256", "stderr.bin"),
    ("harness_result_sha256", "harness-result.json"),
    ("loadout_sha256", "evidence/generated-scenario.loadout.json"),
    ("resolved_sha256", "evidence/generated-scenario.resolved.json"),
)

RUNTIME_COMPARE_FIELDS = (
    ("runtime_config.sha256", ("runtime_config", "sha256")),
    ("harness.sha256", ("harness", "sha256")),
    ("dotnet.sha256", ("dotnet", "sha256")),
    ("sts2_dll.sha256", ("sts2_dll", "sha256")),
    ("combat_solver_dll.effective_sha256", ("combat_solver_dll", "effective_sha256")),
)

# Input/process status vocabulary is shared with the accepted helpers; the fight
# result lives only in ``outcome``.
STATUS_VERIFIED = bpi.STATUS_VERIFIED
STATUS_MISMATCH = bpi.STATUS_MISMATCH
STATUS_MISSING = bpi.STATUS_MISSING
STATUS_ERROR = bpi.STATUS_ERROR
STATUS_TIMEOUT = bpi.STATUS_TIMEOUT
STATUS_NOT_STARTED = bpi.STATUS_NOT_STARTED
STATUS_DRY_RUN = bpi.STATUS_DRY_RUN

# Semantic fields compared cell-by-cell. ``input_status`` is deliberately kept
# beside the outcome fields so the two are reported separately.
SEMANTIC_FIELDS = ("input_status", "outcome_status", "player_hp", "max_hp", "enemy_hp", "turns")

ENV_OVERRIDES = dict(brp.ENV_OVERRIDES)

NOTES = [
    "The default command is a dry run that launches nothing; --run is required.",
    "Four sequential --dop 2 references run first, then sixteen concurrent --dop 2 "
    "replays of the accepted sixteen baseline cells; only --dop and artifact "
    "paths/labels differ from the baseline command.",
    "Only complete, verified outcomes rank; a fixture/budget with any incomplete "
    "cell is suppressed, and a mismatch/error/timeout never becomes a loss.",
    "Matching top sets do not establish concurrency safety, and any difference "
    "cannot be attributed to concurrency because --dop also changed.",
    "No production adoption, new labels, strength, variance or speedup claim is made.",
]

LIMITATIONS = [
    "Both fixtures are PROXY states, not observed reward screens.",
    "One seed and one observation per cell cannot separate run-to-run noise or "
    "concurrency effects from the --dop 1 -> --dop 2 change.",
    "The accepted baseline is historical; its raw hashes are re-verified from disk "
    "but its runtime cannot be re-observed.",
    "The historical 450.391 s throughput ratio is a descriptive historical ratio, "
    "not a controlled or single-fight speedup.",
    "The batch deadline is monotonic wall clock; a slow host can mark later runs "
    "not_started, which is recorded rather than hidden.",
    "Evidence is only as trustworthy as the harness build and the accepted fixture "
    "directory it consumes.",
]


class SourceValidationError(ValueError):
    """Raised when the accepted source, manifest or historical request is invalid."""


class RuntimeValidationError(ValueError):
    """Raised when the actual harness runtime cannot be verified before launch."""


class BaselineValidationError(ValueError):
    """Raised when the accepted baseline summary or its raw files fail verification."""


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def default_output_root(now=None) -> Path:
    """A fresh UTC timestamp directory with microsecond resolution."""
    moment = now if now is not None else datetime.now(timezone.utc)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    stamp = moment.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    return OUTPUT_ROOT / stamp


def compute_timeout(elapsed_seconds: float, phase_cap: float) -> float:
    """Per-launch cap: at most ``phase_cap`` and never past the batch deadline."""
    remaining = BATCH_DEADLINE_SECONDS - elapsed_seconds
    if remaining <= 0:
        return 0.0
    return float(min(phase_cap, remaining))


def with_dop(command, dop=DOP) -> list:
    """Copy an argv and replace only the value after ``--dop`` (never mutate)."""
    tokens = list(command)
    if tokens.count("--dop") != 1:
        raise SourceValidationError(f"command must carry exactly one --dop: {tokens!r}")
    index = tokens.index("--dop")
    if index + 1 >= len(tokens):
        raise SourceValidationError("--dop has no value in command")
    tokens[index + 1] = str(dop)
    return tokens


def _numeric(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def run_dir_path(output_root, run) -> Path:
    """Deterministic run directory: ``<output>/<phase>/<name>``."""
    return Path(output_root) / run["phase"] / run["name"]


def _action_key(run) -> str:
    return "skip" if run.get("candidate") is None else run.get("candidate")


def _cell_key(run):
    return (run.get("fixture"), _action_key(run), run.get("budget_ms"))


def _run_key(run):
    return (run.get("phase"), run.get("run") or run.get("name"))


def _dig(data, path):
    current = data
    for key in path:
        if not isinstance(current, dict):
            return None
        current = current.get(key)
    return current


# ---------------------------------------------------------------------------
# Baseline verification
# ---------------------------------------------------------------------------

def _verify_baseline_run_files(baseline_dir: Path, run: dict, errors: list) -> None:
    name = run.get("run")
    raw = run.get("raw_hashes") if isinstance(run.get("raw_hashes"), dict) else {}
    for hash_key, relative in BASELINE_FILES:
        expected = raw.get(hash_key)
        path = Path(baseline_dir) / name / relative
        if not isinstance(expected, str) or not expected:
            errors.append(f"baseline {name} missing stored {hash_key}")
            continue
        if not path.exists():
            errors.append(f"baseline {name} file absent: {relative}")
            continue
        actual = be.sha256_file(path)
        if actual != expected:
            errors.append(f"baseline {name} {relative} sha256 {actual} != stored {expected}")


def load_baseline(baseline_dir=None) -> dict:
    """Load and raw-verify the accepted ranking-screen baseline."""
    baseline_dir = Path(baseline_dir) if baseline_dir is not None else BASELINE_DIR
    summary_path = baseline_dir / "summary.json"
    if not summary_path.exists():
        raise BaselineValidationError(f"baseline summary absent: {be.rel(summary_path)}")
    try:
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise BaselineValidationError(f"baseline summary unreadable: {exc}") from exc
    if not isinstance(summary, dict):
        raise BaselineValidationError("baseline summary is not a JSON object")

    errors = []
    if summary.get("tool") != BASELINE_TOOL:
        errors.append(f"baseline tool {summary.get('tool')!r} != {BASELINE_TOOL!r}")
    if not summary.get("executed"):
        errors.append("baseline was not marked executed")
    if summary.get("seed") != SEED:
        errors.append(f"baseline seed {summary.get('seed')!r} != {SEED!r}")

    batch = summary.get("batch") if isinstance(summary.get("batch"), dict) else {}
    if batch.get("total_attempts") != BASELINE_ATTEMPTS:
        errors.append(f"baseline total_attempts {batch.get('total_attempts')!r} != {BASELINE_ATTEMPTS}")
    if str(batch.get("dop")) != "1":
        errors.append(f"baseline dop {batch.get('dop')!r} != '1'")
    if str(batch.get("nodes")) != NODES:
        errors.append(f"baseline nodes {batch.get('nodes')!r} != {NODES!r}")
    if str(batch.get("max_turns")) != MAX_TURNS:
        errors.append(f"baseline max_turns {batch.get('max_turns')!r} != {MAX_TURNS!r}")

    runs = summary.get("runs")
    if not isinstance(runs, list) or len(runs) != BASELINE_ATTEMPTS:
        errors.append(f"baseline runs must be a list of {BASELINE_ATTEMPTS}")
        runs = []
    by_name = {}
    for run in runs:
        if not isinstance(run, dict):
            errors.append("baseline run is not an object")
            continue
        name = run.get("run")
        if not isinstance(name, str) or not name:
            errors.append(f"baseline run name invalid: {name!r}")
            continue
        if name in by_name:
            errors.append(f"duplicate baseline run {name!r}")
            continue
        by_name[name] = run
        if run.get("status") != STATUS_VERIFIED:
            errors.append(f"baseline {name} status {run.get('status')!r} != {STATUS_VERIFIED!r}")
        _verify_baseline_run_files(baseline_dir, run, errors)

    if not isinstance(summary.get("runtime"), dict):
        errors.append("baseline runtime absent")
    if not isinstance(summary.get("ranking"), dict):
        errors.append("baseline ranking absent")
    plan = summary.get("plan") if isinstance(summary.get("plan"), dict) else {}
    if not isinstance(plan.get("source"), dict):
        errors.append("baseline plan.source absent")

    if errors:
        raise BaselineValidationError("; ".join(errors))
    return {
        "dir": baseline_dir,
        "summary_path": summary_path,
        "summary_sha256": be.sha256_file(summary_path),
        "summary": summary,
        "runs": by_name,
        "plan": plan,
        "runtime": summary["runtime"],
        "ranking": summary["ranking"],
    }


def validate_baseline_source(baseline: dict, source: dict, historical_request_path) -> None:
    """The accepted fixture/manifest/historical request must be unchanged."""
    src = baseline["plan"].get("source") or {}
    if src.get("adapter_sha256") != source.get("adapter_sha256"):
        raise SourceValidationError(
            f"current adapter sha256 {source.get('adapter_sha256')} != baseline "
            f"{src.get('adapter_sha256')}"
        )
    if src.get("manifest_sha256") != source.get("manifest_sha256"):
        raise SourceValidationError(
            f"current manifest sha256 {source.get('manifest_sha256')} != baseline "
            f"{src.get('manifest_sha256')}"
        )
    if not Path(historical_request_path).exists():
        raise SourceValidationError(
            f"historical outer request absent: {be.rel(Path(historical_request_path))}"
        )
    declared = src.get("historical_request_sha256")
    if declared and declared != be.sha256_file(historical_request_path):
        raise SourceValidationError(
            f"current historical request sha256 != baseline {declared}"
        )


def baseline_cells(baseline: dict) -> dict:
    cells = {}
    for run in baseline["runs"].values():
        cells[_cell_key(run)] = run
    return cells


def baseline_ranking_for(baseline: dict, fixture_id, budget):
    by_budget = baseline["ranking"].get(fixture_id)
    if not isinstance(by_budget, dict):
        return None
    return by_budget.get(str(budget))


def check_runtime_against_baseline(runtime, baseline_runtime) -> dict:
    """Compare the five identity hashes that pin the harness runtime."""
    runtime = runtime if isinstance(runtime, dict) else {}
    baseline_runtime = baseline_runtime if isinstance(baseline_runtime, dict) else {}
    mismatches = {}
    for label, path in RUNTIME_COMPARE_FIELDS:
        current = _dig(runtime, path)
        baseline = _dig(baseline_runtime, path)
        if current != baseline:
            mismatches[label] = {"current": current, "baseline": baseline}
    return {"matches": not mismatches, "mismatches": mismatches}


# ---------------------------------------------------------------------------
# Plan construction
# ---------------------------------------------------------------------------

def build_plan_runs(ordered, output_root, *, harness_path=None, dotnet_path=None):
    """Build the four sequential reference runs and the sixteen parallel replays."""
    reference_specs = [
        spec
        for spec in ordered
        if spec.get("fixture") == REFERENCE_FIXTURE
        and _action_key(spec) in REFERENCE_ACTIONS
        and spec.get("budget_ms") in REFERENCE_BUDGETS
    ]
    if len(reference_specs) != REFERENCE_LAUNCHES:
        raise SourceValidationError(
            f"expected {REFERENCE_LAUNCHES} reference cells for {REFERENCE_FIXTURE}, "
            f"found {len(reference_specs)}"
        )
    if len(ordered) != PARALLEL_LAUNCHES:
        raise SourceValidationError(
            f"expected {PARALLEL_LAUNCHES} replay cells, found {len(ordered)}"
        )

    reference = ranking.build_runs(
        reference_specs, Path(output_root) / "reference",
        harness_path=harness_path, dotnet_path=dotnet_path,
    )
    parallel = ranking.build_runs(
        ordered, Path(output_root) / "parallel",
        harness_path=harness_path, dotnet_path=dotnet_path,
    )
    for run in reference:
        run["phase"] = "reference"
        run["timeout_seconds"] = REFERENCE_TIMEOUT_SECONDS
    for run in parallel:
        run["phase"] = "parallel"
        run["timeout_seconds"] = PARALLEL_TIMEOUT_SECONDS
    for run in reference + parallel:
        run["command"] = with_dop(run["command"], DOP)
    return reference, parallel


def collect_provenance(source, historical_request_path, baseline, *, harness_path=None,
                       dotnet_path=None) -> dict:
    provenance = ranking.collect_provenance(
        source, historical_request_path, harness_path=harness_path, dotnet_path=dotnet_path
    )
    provenance["driver"] = {
        "path": be.rel(Path(__file__)),
        "sha256": be.sha256_file(Path(__file__)),
    }
    provenance["ranking_screen_helper"] = {
        "path": be.rel(Path(ranking.__file__)),
        "sha256": be.sha256_file(Path(ranking.__file__)),
    }
    provenance["baseline"] = {
        "dir": be.rel(baseline["dir"]),
        "summary": be.rel(baseline["summary_path"]),
        "summary_sha256": baseline["summary_sha256"],
    }
    return provenance


def build_plan(runs, source, historical_request_path, baseline, runtime_before, provenance, *,
               harness_path=None, dotnet_path=None) -> dict:
    harness = Path(harness_path) if harness_path is not None else HARNESS
    dotnet = Path(dotnet_path) if dotnet_path is not None else DOTNET
    return {
        "generated_at": utc_now(),
        "tool": "building_pick_parallel_check.py",
        "seed": SEED,
        "budgets_ms": list(BUDGETS),
        "source": {
            "dir": be.rel(source["source_dir"]),
            "adapter": be.rel(source["adapter_path"]),
            "manifest": be.rel(source["manifest_path"]),
            "adapter_sha256": source["adapter_sha256"],
            "manifest_sha256": source["manifest_sha256"],
            "historical_request": be.rel(historical_request_path),
            "historical_request_sha256": brp.sha256_file(historical_request_path),
        },
        "baseline": {
            "dir": be.rel(baseline["dir"]),
            "summary": be.rel(baseline["summary_path"]),
            "summary_sha256": baseline["summary_sha256"],
        },
        "cwd": be.rel(CWD),
        "harness": be.rel(harness),
        "dotnet": be.rel(dotnet),
        "character": CHARACTER,
        "ascension": ASCENSION,
        "player_hp": PLAYER_HP,
        "profile": PROFILE,
        "nodes": NODES,
        "search_timeout_ms": SEARCH_TIMEOUT_MS,
        "dop": DOP,
        "max_turns": MAX_TURNS,
        "play_path": PLAY_PATH,
        "milestone": MILESTONE,
        "max_launches": MAX_LAUNCHES,
        "reference_launches": REFERENCE_LAUNCHES,
        "parallel_launches": PARALLEL_LAUNCHES,
        "reference_timeout_seconds": REFERENCE_TIMEOUT_SECONDS,
        "parallel_timeout_seconds": PARALLEL_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
        "parallelism": PARALLELISM,
        "historical_sequential_seconds": HISTORICAL_SEQUENTIAL_SECONDS,
        "env_overrides": dict(ENV_OVERRIDES),
        "ranking_rule": dict(ranking.RANKING_RULE),
        "runtime_before": runtime_before,
        "runs": runs,
        "provenance": provenance,
    }


# ---------------------------------------------------------------------------
# Execution
# ---------------------------------------------------------------------------

def prepare_run_input(run, output_root, historical_request_path, baseline_by_cell) -> dict:
    """Regenerate one input through the accepted helper and pin it to the baseline."""
    run_dir = run_dir_path(output_root, run)
    input_path, request_path = ranking.write_run_inputs(run_dir, run, historical_request_path)
    input_sha = be.sha256_file(input_path)
    request_sha = be.sha256_file(request_path)
    baseline = baseline_by_cell.get(_cell_key(run))
    expected = None
    if isinstance(baseline, dict):
        expected = (baseline.get("raw_hashes") or {}).get("input_sha256")
        if expected is not None and input_sha != expected:
            raise SourceValidationError(
                f"{run['phase']}/{run['name']} generated input sha256 {input_sha} "
                f"!= baseline input {expected}"
            )
    return {
        "input_sha256": input_sha,
        "request_sha256": request_sha,
        "baseline_input_sha256": expected,
    }


def verify_search_policy(run_dir, run) -> dict:
    """Read the actual search-policy.json and verify dop/budget/nodes."""
    path = Path(run_dir) / "search-policy.json"
    if not path.exists():
        return {"present": False, "ok": False, "reason": "search-policy.json absent"}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        return {"present": True, "ok": False, "reason": f"search-policy.json unreadable: {exc}"}
    if not isinstance(data, dict):
        return {"present": True, "ok": False, "reason": "search-policy.json is not an object"}
    profile = data.get("profile") if isinstance(data.get("profile"), dict) else {}
    dop = data.get("maxDegreeOfParallelism")
    budget = profile.get("softTimeBudgetMilliseconds")
    nodes = profile.get("maxExpandedNodes")
    ok = (
        dop == int(DOP)
        and budget == run.get("budget_ms")
        and nodes == int(NODES)
    )
    return {
        "present": True,
        "ok": ok,
        "sha256": be.sha256_file(path),
        "dop": dop,
        "budget_ms": budget,
        "nodes": nodes,
        "expected": {"dop": int(DOP), "budget_ms": run.get("budget_ms"), "nodes": int(NODES)},
    }


def _execute_run(run, output_root, *, adapter_fixture, prepared, runner, clock, start,
                 dry_run, provenance, phase_cap) -> dict:
    run_dir = run_dir_path(output_root, run)
    record = ranking._new_record(run, run_dir, provenance)
    record["phase"] = run["phase"]

    if dry_run:
        record["status"] = STATUS_DRY_RUN
        record["reason"] = "dry run: subprocess not launched"
        return record

    elapsed = clock() - start
    record["elapsed_before_launch"] = elapsed
    timeout = compute_timeout(elapsed, phase_cap)
    record["timeout_before_preparation_seconds"] = timeout
    if timeout <= 0:
        record["status"] = STATUS_NOT_STARTED
        record["reason"] = (
            f"batch deadline exhausted before launch: elapsed {elapsed:.3f}s "
            f">= {BATCH_DEADLINE_SECONDS}s"
        )
        return record

    info = prepared.get(_run_key(run)) or {}
    record["raw_hashes"]["input_sha256"] = info.get("input_sha256")
    record["raw_hashes"]["request_sha256"] = info.get("request_sha256")
    command = list(run["command"])
    record["args"] = command

    prepared_at = clock() - start
    record["elapsed_before_subprocess"] = prepared_at
    # Recompute after preparation/queue wait so it is charged to the batch.
    timeout = compute_timeout(prepared_at, phase_cap)
    record["timeout_seconds"] = timeout
    if timeout <= 0:
        record["status"] = STATUS_NOT_STARTED
        record["reason"] = (
            f"batch deadline exhausted during preparation/queue wait: elapsed "
            f"{prepared_at:.3f}s >= {BATCH_DEADLINE_SECONDS}s"
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
    record["raw_hashes"]["stdout_sha256"] = be.sha256_file(run_dir / "stdout.bin")
    record["raw_hashes"]["stderr_sha256"] = be.sha256_file(run_dir / "stderr.bin")
    record["timed_out"] = timed_out
    record["exit"] = exit_code

    raw_path = run_dir / RESULT_NAME
    payload, load_error = be.load_payload(raw_path)
    if raw_path.exists():
        record["raw_hashes"]["harness_result_sha256"] = be.sha256_file(raw_path)

    record["outcome"] = ranking._outcome_for(exit_code, timed_out, payload, load_error, launch_error)
    ranking._finish_input(record, run, adapter_fixture, payload, load_error, run_dir, launch_error)

    metrics = ranking._payload_metrics(payload)
    record["cost_ms"] = metrics["cost_ms"]
    record["wall_seconds"] = metrics["wall_seconds"]
    record["time_boundary_observed"] = metrics["time_boundary_observed"]
    outcome = record["outcome"] if isinstance(record["outcome"], dict) else {}
    record["player_hp"] = outcome.get("player_hp")
    record["max_hp"] = outcome.get("max_hp")
    record["enemy_hp"] = outcome.get("final_enemy_hp")
    record["turns"] = outcome.get("turns")
    record["fight_wall_ms"] = outcome.get("fight_wall_ms")
    record["eligible"] = bool(
        record["status"] == STATUS_VERIFIED and outcome.get("status") in be.COMPLETE_STATUSES
    )
    cpu = payload.get("totalCpuMilliseconds") if isinstance(payload, dict) else None
    record["total_cpu_ms"] = _numeric(cpu)
    record["policy"] = verify_search_policy(run_dir, run) if raw_path.exists() else {
        "present": False, "ok": False, "reason": "no harness-result.json"
    }
    if record["status"] == STATUS_VERIFIED and not record["policy"].get("ok"):
        record["status"] = STATUS_MISMATCH
        record["reason"] = "actual search policy missing or mismatched"
        record["eligible"] = False
    return record


def execute_run(run, output_root, **kwargs) -> dict:
    """Run one case; any preparation/decoding failure becomes an error record."""
    try:
        return _execute_run(run, output_root, **kwargs)
    except Exception as exc:  # noqa: BLE001 - the journal already holds attempts
        record = ranking._new_record(run, run_dir_path(output_root, run), kwargs["provenance"])
        record["phase"] = run.get("phase")
        record["status"] = STATUS_ERROR
        record["reason"] = f"parallel-check record failed: {type(exc).__name__}: {exc}"
        record["launched"] = None
        return record


def _missing_record(run, output_root, provenance) -> dict:
    record = ranking._new_record(run, run_dir_path(output_root, run), provenance)
    record["phase"] = run.get("phase")
    record["status"] = STATUS_ERROR
    record["reason"] = "case was not recorded before the batch ended"
    return record


# ---------------------------------------------------------------------------
# Comparison helpers
# ---------------------------------------------------------------------------

def semantic_tuple(record) -> dict:
    """Input status and outcome fields, reported side by side."""
    record = record if isinstance(record, dict) else {}
    outcome = record.get("outcome") if isinstance(record.get("outcome"), dict) else {}
    return {
        "input_status": record.get("status"),
        "outcome_status": outcome.get("status"),
        "player_hp": record.get("player_hp", outcome.get("player_hp")),
        "max_hp": record.get("max_hp", outcome.get("max_hp")),
        "enemy_hp": record.get("enemy_hp", outcome.get("final_enemy_hp")),
        "turns": record.get("turns", outcome.get("turns")),
    }


def compare_semantics(left, right) -> dict:
    left = left if isinstance(left, dict) else None
    right = right if isinstance(right, dict) else None
    if left is None or right is None:
        return {"available": False, "exact_match": False, "changed_fields": [], "left": left, "right": right}
    changed = [field for field in SEMANTIC_FIELDS if left.get(field) != right.get(field)]
    return {
        "available": True,
        "exact_match": not changed,
        "changed_fields": changed,
        "left": left,
        "right": right,
    }


def compare_cell(baseline_record, new_record, run) -> dict:
    base = semantic_tuple(baseline_record) if isinstance(baseline_record, dict) else None
    new = semantic_tuple(new_record) if isinstance(new_record, dict) else None
    comparison = compare_semantics(base, new)
    return {
        "cell": {
            "fixture": run.get("fixture"),
            "action": _action_key(run),
            "budget_ms": run.get("budget_ms"),
        },
        "baseline": base,
        "parallel": new,
        "input_status_match": bool(
            base is not None and new is not None and base["input_status"] == new["input_status"]
        ),
        "exact_semantic_match": comparison["exact_match"],
        "changed_fields": comparison["changed_fields"],
    }


def compare_rankings(parallel_ranking, baseline_ranking) -> dict:
    """Descriptively compare a parallel ranking with its accepted baseline."""
    if not isinstance(parallel_ranking, dict) or not parallel_ranking.get("available"):
        return {
            "available": False,
            "reason": "parallel ranking unavailable",
            "reasons": (parallel_ranking or {}).get("reasons"),
        }
    if not isinstance(baseline_ranking, dict) or not baseline_ranking.get("available"):
        return {"available": False, "reason": "baseline ranking unavailable"}

    def index(ranking):
        return {
            action: group["rank"]
            for group in ranking["tie_groups"]
            for action in group["actions"]
        }

    parallel_index = index(parallel_ranking)
    baseline_index = index(baseline_ranking)

    def order(a, b, rank_index):
        if rank_index.get(a) == rank_index.get(b):
            return "tie"
        return a if rank_index.get(a, 99) < rank_index.get(b, 99) else b

    pairwise = []
    changed = []
    for position, action_a in enumerate(ACTIONS):
        for action_b in ACTIONS[position + 1:]:
            parallel_order = order(action_a, action_b, parallel_index)
            baseline_order = order(action_a, action_b, baseline_index)
            entry = {
                "pair": [action_a, action_b],
                "parallel_order": parallel_order,
                "baseline_order": baseline_order,
                "changed": parallel_order != baseline_order,
            }
            pairwise.append(entry)
            if entry["changed"]:
                changed.append(entry)

    parallel_tie = [sorted(group["actions"]) for group in parallel_ranking["tie_groups"]]
    baseline_tie = [sorted(group["actions"]) for group in baseline_ranking["tie_groups"]]
    return {
        "available": True,
        "top_set_parallel": parallel_ranking["top_actions"],
        "top_set_baseline": baseline_ranking["top_actions"],
        "top_set_changed": set(parallel_ranking["top_actions"]) != set(baseline_ranking["top_actions"]),
        "tie_structure_changed": parallel_tie != baseline_tie,
        "tie_structure_parallel": parallel_tie,
        "tie_structure_baseline": baseline_tie,
        "pairwise": pairwise,
        "order_changed": changed,
        "note": "descriptive only; --dop differs, so changes are not attributable to concurrency",
    }


# ---------------------------------------------------------------------------
# Performance and summary
# ---------------------------------------------------------------------------

def build_performance(reference_records, parallel_records, baseline, *, reference_wall,
                      parallel_wall, cpu_count, runtime_stable, runtime_matches_baseline) -> dict:
    def runtime_values(records):
        return [r["runtime_seconds"] for r in records if _numeric(r.get("runtime_seconds")) is not None]

    reference_launched = sum(1 for r in reference_records if r.get("launched"))
    parallel_launched = sum(1 for r in parallel_records if r.get("launched"))
    reference_process_ok = sum(1 for r in reference_records if r.get("exit") == 0)
    parallel_process_ok = sum(1 for r in parallel_records if r.get("exit") == 0)

    cpu_values = []
    cpu_missing = []
    for record in reference_records + parallel_records:
        value = _numeric(record.get("total_cpu_ms"))
        if value is None:
            cpu_missing.append(f"{record.get('phase')}/{record.get('run')}")
        else:
            cpu_values.append(value)
    cpu_seconds = (sum(cpu_values) / 1000.0) if cpu_values else None
    parallel_cpu_values = [_numeric(r.get("total_cpu_ms")) for r in parallel_records]
    parallel_cpu_complete = bool(parallel_cpu_values) and all(v is not None for v in parallel_cpu_values)
    parallel_cpu_seconds = sum(parallel_cpu_values) / 1000.0 if parallel_cpu_complete else None

    logical_cpus = cpu_count if isinstance(cpu_count, int) and cpu_count > 0 else os.cpu_count()
    average_busy_cores = None
    utilization = None
    if parallel_cpu_seconds is not None and _numeric(parallel_wall) and parallel_wall > 0:
        average_busy_cores = parallel_cpu_seconds / parallel_wall
        if isinstance(logical_cpus, int) and logical_cpus > 0:
            utilization = average_busy_cores / logical_cpus

    all_parallel_complete = (
        len(parallel_records) == PARALLEL_LAUNCHES
        and all(record.get("eligible") for record in parallel_records)
    )
    historical_ratio = None
    historical_ratio_reason = None
    if all_parallel_complete and runtime_stable and runtime_matches_baseline and parallel_wall > 0:
        historical_ratio = HISTORICAL_SEQUENTIAL_SECONDS / parallel_wall
    else:
        reasons = []
        if not all_parallel_complete:
            reasons.append("not all 16 parallel runs are complete valid outcomes")
        if not runtime_stable:
            reasons.append("runtime hash changed between the two phases")
        if not runtime_matches_baseline:
            reasons.append("runtime does not match the accepted baseline")
        if not (_numeric(parallel_wall) and parallel_wall > 0):
            reasons.append("parallel phase wall is unavailable")
        historical_ratio_reason = "; ".join(reasons)

    baseline_runtimes = [
        value
        for value in (
            _numeric(run.get("runtime_seconds")) for run in baseline["runs"].values()
        )
        if value is not None
    ]

    return {
        "reference_wall_seconds": reference_wall,
        "parallel_wall_seconds": parallel_wall,
        "reference_runtimes_seconds": runtime_values(reference_records),
        "parallel_runtimes_seconds": runtime_values(parallel_records),
        "launches": {
            "reference": reference_launched,
            "parallel": parallel_launched,
            "total": reference_launched + parallel_launched,
        },
        "process_completed": {
            "reference": reference_process_ok,
            "parallel": parallel_process_ok,
            "total": reference_process_ok + parallel_process_ok,
        },
        "cpu": {
            "runs_with_value": len(cpu_values),
            "runs_missing": len(cpu_missing),
            "missing_runs": cpu_missing,
            "total_cpu_seconds": cpu_seconds,
            "parallel_cpu_seconds": parallel_cpu_seconds,
            "parallel_cpu_complete": parallel_cpu_complete,
            "logical_cpus": logical_cpus,
            "average_busy_cores": average_busy_cores,
            "estimated_child_utilization": utilization,
            "note": "total CPU covers both phases; utilization uses only parallel-phase CPU/wall. Missing parallel CPU suppresses utilization, never treated as zero. Child-process estimate only.",
        },
        "historical": {
            "sequential_subprocess_seconds": HISTORICAL_SEQUENTIAL_SECONDS,
            "baseline_sum_subprocess_seconds": sum(baseline_runtimes) if baseline_runtimes else None,
            "throughput_ratio": historical_ratio,
            "ratio_available": historical_ratio is not None,
            "ratio_reason": historical_ratio_reason,
            "label": "historical throughput ratio (historical sequential subprocess sum / new "
            "parallel phase wall); NOT a controlled speedup and NOT a single-fight speedup",
        },
    }


def build_decision(*, comparison_available, ranking_comparisons, cell_comparisons, performance) -> dict:
    top_sets_all_match = all(
        (entry.get("available") and not entry.get("top_set_changed"))
        for fixture in ranking_comparisons.values()
        for entry in fixture.values()
    )
    exact_all_match = all(entry.get("exact_semantic_match") for entry in cell_comparisons)
    reasons = [
        "No automatic production adoption and no new labels.",
        "Matching top sets do not establish concurrency safety.",
        "Any outcome change cannot be attributed to concurrency because --dop also changed.",
        "Only one development seed and one observation per cell were collected.",
    ]
    return {
        "production_adoption": "none",
        "automatic_adoption": False,
        "new_labels": False,
        "parallel_safety_claim": False,
        "causality_claim": False,
        "comparison_available": bool(comparison_available),
        "top_sets_all_match": bool(top_sets_all_match),
        "exact_all_match": bool(exact_all_match),
        "historical_throughput_ratio_available": bool(performance["historical"]["ratio_available"]),
        "summary": (
            "Descriptive serial-vs-parallel comparison only; no production change is "
            "implied by a matching top set or by the historical throughput ratio."
        ),
        "reasons": reasons,
    }


def build_summary(records, *, executed, output_root, plan, source, baseline, runtime_before,
                  runtime_after, reference_wall, parallel_wall, cpu_count, error) -> dict:
    fixture_ids = ranking._fixture_order(source)
    by_key = {_run_key(record): record for record in records}
    ordered = [
        by_key.get((run["phase"], run["name"]))
        or _missing_record(run, output_root, plan["provenance"])
        for run in plan["runs"]
    ]
    reference_records = [r for r in ordered if r.get("phase") == "reference"]
    parallel_records = [r for r in ordered if r.get("phase") == "parallel"]

    adapter_fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    build_value_actions = {
        fixture_id: ranking.resolve_build_value_action(adapter_fixtures[fixture_id])
        for fixture_id in fixture_ids
    }

    grouped = {}
    for record in parallel_records:
        key = (record.get("fixture"), record.get("budget_ms"))
        grouped.setdefault(key, {})[ranking._action_key(record)] = record
    parallel_rankings = {}
    for fixture_id in fixture_ids:
        parallel_rankings[fixture_id] = {}
        for budget in BUDGETS:
            parallel_rankings[fixture_id][budget] = ranking.build_ranking(
                grouped.get((fixture_id, budget), {}), budget, build_value_actions[fixture_id]
            )

    ranking_comparisons = {}
    for fixture_id in fixture_ids:
        ranking_comparisons[fixture_id] = {}
        for budget in BUDGETS:
            ranking_comparisons[fixture_id][budget] = compare_rankings(
                parallel_rankings[fixture_id][budget],
                baseline_ranking_for(baseline, fixture_id, budget),
            )

    baseline_by_cell = baseline_cells(baseline)
    cell_comparisons = []
    for run in plan["runs"]:
        if run["phase"] != "parallel":
            continue
        cell_comparisons.append(
            compare_cell(baseline_by_cell.get(_cell_key(run)), by_key.get(("parallel", run["name"])), run)
        )

    fresh_references = []
    for run in plan["runs"]:
        if run["phase"] != "reference":
            continue
        baseline_record = baseline_by_cell.get(_cell_key(run))
        reference_record = by_key.get(("reference", run["name"]))
        parallel_record = by_key.get(("parallel", run["name"]))
        fresh_references.append({
            "cell": {
                "fixture": run.get("fixture"),
                "action": _action_key(run),
                "budget_ms": run.get("budget_ms"),
            },
            "baseline": semantic_tuple(baseline_record) if baseline_record else None,
            "reference": semantic_tuple(reference_record) if reference_record else None,
            "parallel": semantic_tuple(parallel_record) if parallel_record else None,
            "reference_vs_baseline": compare_semantics(
                semantic_tuple(baseline_record) if baseline_record else None,
                semantic_tuple(reference_record) if reference_record else None,
            ),
            "reference_vs_parallel": compare_semantics(
                semantic_tuple(parallel_record) if parallel_record else None,
                semantic_tuple(reference_record) if reference_record else None,
            ),
            "note": "single observations; differences are shown, never attributed",
        })

    runtime_check = check_runtime_against_baseline(runtime_before, baseline["runtime"])
    runtime_stable = bool(executed) and runtime_after is not None and runtime_before == runtime_after
    rankings_all_available = all(
        comparison.get("available")
        for fixture in ranking_comparisons.values()
        for comparison in fixture.values()
    ) and all(
        parallel_rankings[fixture_id][budget].get("available")
        for fixture_id in fixture_ids
        for budget in BUDGETS
    )
    comparison_available = bool(
        executed and runtime_check["matches"] and runtime_stable and rankings_all_available
    )

    performance = build_performance(
        reference_records, parallel_records, baseline,
        reference_wall=reference_wall, parallel_wall=parallel_wall, cpu_count=cpu_count,
        runtime_stable=runtime_stable, runtime_matches_baseline=runtime_check["matches"],
    )

    status_counts = ranking._status_counts(ordered)
    verified = sum(1 for record in ordered if record.get("status") == STATUS_VERIFIED)
    eligible = sum(1 for record in ordered if record.get("eligible"))
    attempts_accounted = len(ordered) == MAX_LAUNCHES and len(records) == MAX_LAUNCHES
    policy_verified = sum(
        1 for record in ordered
        if isinstance(record.get("policy"), dict) and record["policy"].get("ok")
    )

    decision = build_decision(
        comparison_available=comparison_available,
        ranking_comparisons=ranking_comparisons,
        cell_comparisons=cell_comparisons,
        performance=performance,
    )

    return {
        "generated_at": utc_now(),
        "tool": "building_pick_parallel_check.py",
        "executed": bool(executed),
        "output_dir": be.rel(Path(output_root)),
        "seed": SEED,
        "budgets_ms": list(BUDGETS),
        "character": CHARACTER,
        "ascension": ASCENSION,
        "player_hp": PLAYER_HP,
        "batch": {
            "total_attempts": MAX_LAUNCHES,
            "reference_attempts": REFERENCE_LAUNCHES,
            "parallel_attempts": PARALLEL_LAUNCHES,
            "attempts_accounted": attempts_accounted,
            "reference_timeout_seconds": REFERENCE_TIMEOUT_SECONDS,
            "parallel_timeout_seconds": PARALLEL_TIMEOUT_SECONDS,
            "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
            "parallelism": PARALLELISM,
            "max_turns": MAX_TURNS,
            "dop": DOP,
            "nodes": NODES,
        },
        "status_counts": status_counts,
        "outcome_counts": ranking._outcome_counts(ordered),
        "verified": verified,
        "eligible_completed": eligible,
        "policy_verified": policy_verified,
        "tool_completed": bool(attempts_accounted and error is None),
        "runtime": {
            "before": runtime_before,
            "after": runtime_after,
            "stable": runtime_stable,
            "matches_baseline": runtime_check["matches"],
            "baseline_mismatches": runtime_check["mismatches"],
        },
        "reference": {
            "runs": reference_records,
            "comparison": fresh_references,
        },
        "parallel": {
            "runs": parallel_records,
            "rankings": parallel_rankings,
        },
        "baseline": {
            "dir": be.rel(baseline["dir"]),
            "summary": be.rel(baseline["summary_path"]),
            "summary_sha256": baseline["summary_sha256"],
            "validated": True,
            "runtime": baseline["runtime"],
        },
        "ranking_comparison": ranking_comparisons,
        "cell_comparison": cell_comparisons,
        "decision": decision,
        "performance": performance,
        "runs": ordered,
        "plan": plan,
        "error": error,
        "notes": list(NOTES),
        "limitations": list(LIMITATIONS),
    }


# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------

def run_parallel_check(*, run=False, output_root=None, source_dir=None, baseline_dir=None,
                       historical_dir=None, harness_path=None, dotnet_path=None,
                       runtime_config_path=None, runtime_env=None, runner=None, clock=None,
                       cpu_count=None, now=None) -> dict:
    """Run the fixed 20-launch plan. ``run=False`` (the default) launches nothing."""
    runner = runner if runner is not None else subprocess.run
    clock = clock if clock is not None else time.monotonic
    source_dir = Path(source_dir) if source_dir is not None else SOURCE_DIR
    historical_dir = Path(historical_dir) if historical_dir is not None else HISTORICAL_DIR
    historical_request_path = historical_dir / "outer-request.json"
    harness_path = Path(harness_path) if harness_path is not None else HARNESS
    dotnet_path = Path(dotnet_path) if dotnet_path is not None else DOTNET
    if runtime_config_path is None:
        runtime_config_path = harness_path.parent / "OfflineSearchHarness.runtimeconfig.json"
    runtime_config_path = Path(runtime_config_path)
    output_root = Path(output_root) if output_root is not None else default_output_root(now)

    source, ordered = ranking.prepare_source(source_dir)
    baseline = load_baseline(baseline_dir)
    validate_baseline_source(baseline, source, historical_request_path)

    try:
        runtime_before = ranking.collect_runtime_provenance(
            runtime_config_path, source["adapter"],
            harness_path=harness_path, dotnet_path=dotnet_path, env=runtime_env,
        )
    except RuntimeValidationError as exc:
        if run:
            raise
        runtime_before = {"ok": False, "error": str(exc)}

    runtime_check = check_runtime_against_baseline(runtime_before, baseline["runtime"])
    if run and not runtime_check["matches"]:
        raise RuntimeValidationError(
            "runtime does not match the accepted baseline: "
            + json.dumps(runtime_check["mismatches"], ensure_ascii=False)
        )

    # Refuse an existing output directory: the timestamp is expected to be unique.
    output_root.mkdir(parents=True, exist_ok=False)
    reference_runs, parallel_runs = build_plan_runs(
        ordered, output_root, harness_path=harness_path, dotnet_path=dotnet_path
    )
    runs = reference_runs + parallel_runs
    provenance = collect_provenance(
        source, historical_request_path, baseline,
        harness_path=harness_path, dotnet_path=dotnet_path,
    )
    plan = build_plan(
        runs, source, historical_request_path, baseline, runtime_before, provenance,
        harness_path=harness_path, dotnet_path=dotnet_path,
    )
    plan["output_root"] = be.rel(output_root)
    brp.write_json_exclusive(output_root / "plan.json", plan)
    journal_path = ranking.create_exclusive_file(output_root / "journal.jsonl")

    adapter_fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    baseline_by_cell = baseline_cells(baseline)
    dry_run = not run
    start = clock()
    records = []
    error = None
    reference_wall = 0.0
    parallel_wall = 0.0
    runtime_after = None

    try:
        prepared = {}
        if not dry_run:
            for run_case in runs:
                prepared[_run_key(run_case)] = prepare_run_input(
                    run_case, output_root, historical_request_path, baseline_by_cell
                )

        reference_start = clock()
        for run_case in reference_runs:
            record = execute_run(
                run_case, output_root,
                adapter_fixture=adapter_fixtures[run_case["fixture"]],
                prepared=prepared, runner=runner, clock=clock, start=start,
                dry_run=dry_run, provenance=provenance,
                phase_cap=REFERENCE_TIMEOUT_SECONDS,
            )
            records.append(record)
            ranking.append_journal(journal_path, record)
        reference_wall = clock() - reference_start

        parallel_start = clock()
        with ThreadPoolExecutor(max_workers=PARALLELISM) as pool:
            futures = {
                pool.submit(
                    execute_run,
                    run_case, output_root,
                    adapter_fixture=adapter_fixtures[run_case["fixture"]],
                    prepared=prepared, runner=runner, clock=clock, start=start,
                    dry_run=dry_run, provenance=provenance,
                    phase_cap=PARALLEL_TIMEOUT_SECONDS,
                ): run_case
                for run_case in parallel_runs
            }
            # Journal in the main thread as each future completes.
            for future in as_completed(futures):
                run_case = futures[future]
                try:
                    record = future.result()
                except BaseException as exc:  # noqa: BLE001
                    record = _missing_record(run_case, output_root, provenance)
                    record["reason"] = (
                        f"future failed: {type(exc).__name__}: {exc}"
                    )
                records.append(record)
                ranking.append_journal(journal_path, record)
        parallel_wall = clock() - parallel_start

        if not dry_run:
            runtime_after = ranking.collect_runtime_provenance(
                runtime_config_path, source["adapter"],
                harness_path=harness_path, dotnet_path=dotnet_path, env=runtime_env,
            )
    except BaseException as exc:  # noqa: BLE001 - the journal already holds attempts
        error = f"{type(exc).__name__}: {exc}"

    summary = build_summary(
        records, executed=run, output_root=output_root, plan=plan, source=source,
        baseline=baseline, runtime_before=runtime_before, runtime_after=runtime_after,
        reference_wall=reference_wall, parallel_wall=parallel_wall,
        cpu_count=cpu_count, error=error,
    )
    brp.write_json_exclusive(output_root / "summary.json", summary)
    return summary


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Bounded serial-vs-parallel replay of the accepted pick-ranking screen"
    )
    group = parser.add_mutually_exclusive_group()
    group.add_argument(
        "--run", action="store_true", help="execute the 20 fixed launches (default: dry run)"
    )
    group.add_argument(
        "--dry-run", action="store_true", help="write the plan only; no subprocess (default)"
    )
    args = parser.parse_args(argv)

    try:
        summary = run_parallel_check(run=args.run)
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

    if args.run:
        print(
            f"attempts={summary['batch']['total_attempts']} "
            f"reference={summary['batch']['reference_attempts']} "
            f"parallel={summary['batch']['parallel_attempts']} "
            f"verified={summary['verified']} eligible={summary['eligible_completed']} "
            f"policy_verified={summary['policy_verified']} "
            f"comparison_available={summary['decision']['comparison_available']} "
            f"tool_completed={summary['tool_completed']}",
            flush=True,
        )
    else:
        print(
            f"attempts={summary['batch']['total_attempts']} "
            f"reference={summary['batch']['reference_attempts']} "
            f"parallel={summary['batch']['parallel_attempts']} dop={DOP} executed=false "
            f"status_counts={summary['status_counts']}",
            flush=True,
        )
    print(f"wrote {Path(summary['output_dir']) / 'summary.json'}", flush=True)
    if not args.run:
        return 0
    return 0 if summary["tool_completed"] else 1


if __name__ == "__main__":
    sys.exit(main())
