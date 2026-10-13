#!/usr/bin/env python3
"""Fixed repeatability probe for the building evaluation oracle.

This is a small, fixed instrument. It launches the offline harness exactly four
times on the *same logical input* and records whether the harness reads that
input the same way every time:

* ``sequential-1`` and ``sequential-2`` run one after another;
* ``parallel-1`` and ``parallel-2`` run at the same time, at most two at once.

The input is the frozen historical fixture
``work/oracle/shiv-pm/b4000-hp1.0-dmg1.0-P0M0-PM001`` (SILENT, seed PM001,
SOUL_NEXUS_ELITE). The seed never changes between runs; only the run directory
and label differ. Every actual input file is copied byte-for-byte and its seed,
character and encounter are re-checked before launch.

The instrument is deliberately strict:

* the default command is a **dry run** that launches nothing and writes only the
  plan; ``--run`` is required before any process starts;
* each subprocess may run at most 120 s and the whole batch at most 360 s of
  monotonic time; every launch gets ``min(120, remaining)`` and a launch after
  the deadline is recorded ``not_started`` (never a battle result);
* outcomes are classified by the shared ``building_eval.classify`` API, so a
  timeout, a launch error or an incomplete/truncated fight can never be read as
  a completed win or loss;
* the report keeps provenance hashes, the exact argv, runtimes, stdout/stderr
  paths and the raw result path for every run.

A pass means the instrument did not visibly move across four repeats of one
seed. It is not a claim about other seeds, about the full execution trace, or
about strength.

Usage:
    python scripts/building_repeat_probe.py            # dry run, writes plan only
    python scripts/building_repeat_probe.py --run      # execute the four runs
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path

import building_eval as be

ROOT = Path(__file__).resolve().parent.parent

# Fixed repository paths. These mirror the DOTNET/HARNESS constants in
# scripts/soulnexus-sigma.py; they are repeated here so importing this module
# never imports another script.
HARNESS = (
    ROOT / "third_party" / "CombatSolver-upstream-main" / "tools" / "OfflineSearchHarness"
    / "bin" / "Release" / "net9.0" / "OfflineSearchHarness.dll"
)
DOTNET = ROOT / "work" / "dotnet-win" / "dotnet.exe"
CWD = ROOT / "third_party" / "CombatSolver-upstream-main"

SOURCE_DIR = ROOT / "work" / "oracle" / "shiv-pm" / "b4000-hp1.0-dmg1.0-P0M0-PM001"
OUTPUT_ROOT = ROOT / "work" / "measurement-pilot" / "repeats"

RESULT_NAME = "harness-result.json"
SEED = "PM001"
CHARACTER = "SILENT"
ENCOUNTER = "SOUL_NEXUS_ELITE"

PROFILE = "Medium"
NODES = "6000"
BUDGET_MS = "4000"
SEARCH_TIMEOUT_MS = "60000"
DOP = "2"
PLAY_PATH = "gameaction"
MAX_TURNS = "40"
MILESTONE = "M2"

PER_RUN_TIMEOUT_SECONDS = 120
BATCH_DEADLINE_SECONDS = 360
PARALLELISM = 2

STATUS_DRY_RUN = "dry_run"
STATUS_NOT_STARTED = "not_started"

RUN_SPEC = (
    ("sequential-1", "sequential"),
    ("sequential-2", "sequential"),
    ("parallel-1", "parallel"),
    ("parallel-2", "parallel"),
)
RUN_ORDER = tuple(name for name, _mode in RUN_SPEC)
PARALLEL_RUNS = tuple(name for name, mode in RUN_SPEC if mode == "parallel")

SEMANTIC_FIELDS = ("status", "player_hp", "max_hp", "final_enemy_hp", "turns")

# Sanitizer parity with scripts/shiv-pm-interaction.py: historical run state is
# dropped so every run starts from the same scenario.
SNAPSHOT_KEYS = (
    "checkpointArchivePath",
    "runSnapshotPath",
    "replayStatePath",
    "nativeStatePath",
    "replayPolicyOverridePath",
)
LIST_KEYS = ("runCards", "cards", "relics", "combatRelics", "potions", "powers", "modifierIds")

ENV_OVERRIDES = {
    "OFFLINE_HARNESS_SOULNEXUS_HP_SCALE": "1.0",
    "OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE": "1.0",
}

NOTES = [
    "Four runs of one seed are a repeatability screen, not a determinism proof; no "
    "claim is made here about other seeds or about the full execution trace.",
    "Only complete win/loss classifications with an identical semantic tuple count as "
    "repeat-equal; every other status fails the measurement gate.",
    "Wall time, file paths, labels and stdout/stderr contents are excluded from the "
    "semantic comparison.",
]


def default_output_root(now=None) -> Path:
    """A fresh UTC timestamp directory with microsecond resolution."""
    moment = now if now is not None else datetime.now(timezone.utc)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    stamp = moment.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    return OUTPUT_ROOT / stamp


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def sha256_file(path) -> str | None:
    """Hash an existing file; absent files are reported as None, not guessed."""
    path = Path(path)
    if not path.exists():
        return None
    return be.sha256_file(path)


def write_json_exclusive(path, data) -> Path:
    """Create a new JSON file with O_EXCL so an existing report is never clobbered."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(data, ensure_ascii=False, indent=1).encode("utf-8")
    fd = os.open(str(path), os.O_WRONLY | os.O_CREAT | os.O_EXCL)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(encoded)
    except BaseException:
        try:
            os.unlink(path)
        except OSError:
            pass
        raise
    return path


def write_bytes(path, data: bytes) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def build_env() -> dict:
    """Fixed SoulNexus scale environment. Returned for the child only; never stored."""
    env = dict(os.environ)
    env.update(ENV_OVERRIDES)
    return env


def build_command(request_path, run_dir, run_name) -> list:
    return [
        str(DOTNET),
        str(HARNESS),
        "--request", str(request_path),
        "--profile", PROFILE,
        "--nodes", NODES,
        "--budget-ms", BUDGET_MS,
        "--search-timeout-ms", SEARCH_TIMEOUT_MS,
        "--dop", DOP,
        "--no-card-probe",
        "--deploy-plan",
        "--play-path", PLAY_PATH,
        "--max-turns", MAX_TURNS,
        "--milestone", MILESTONE,
        "--label", run_name,
        "--out", str(run_dir),
    ]


def compute_timeout(elapsed_seconds: float) -> float:
    """Per-launch cap: at most 120 s and never past the 360 s batch deadline."""
    remaining = BATCH_DEADLINE_SECONDS - elapsed_seconds
    if remaining <= 0:
        return 0.0
    return float(min(PER_RUN_TIMEOUT_SECONDS, remaining))


def collect_provenance(source_dir) -> dict:
    source_dir = Path(source_dir)
    return {
        "source_input": be.rel(source_dir / "input.json"),
        "source_request": be.rel(source_dir / "outer-request.json"),
        "harness": {"path": be.rel(HARNESS), "sha256": sha256_file(HARNESS)},
        "dotnet": {"path": be.rel(DOTNET), "sha256": sha256_file(DOTNET)},
        "driver": {"path": be.rel(Path(__file__)), "sha256": sha256_file(Path(__file__))},
    }


def build_plan(output_root, source_dir, provenance) -> dict:
    output_root = Path(output_root)
    runs = []
    for name, mode in RUN_SPEC:
        run_dir = output_root / name
        runs.append(
            {
                "name": name,
                "mode": mode,
                "dir": be.rel(run_dir),
                "input": be.rel(run_dir / "input.json"),
                "request": be.rel(run_dir / "outer-request.json"),
                "command": build_command(run_dir / "outer-request.json", run_dir, name),
                "timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
            }
        )
    return {
        "generated_at": utc_now(),
        "seed": SEED,
        "character": CHARACTER,
        "encounter": ENCOUNTER,
        "source": {
            "dir": be.rel(Path(source_dir)),
            "input": provenance["source_input"],
            "request": provenance["source_request"],
        },
        "output_root": be.rel(output_root),
        "cwd": be.rel(CWD),
        "per_run_timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
        "parallelism": PARALLELISM,
        "env_overrides": dict(ENV_OVERRIDES),
        "runs": runs,
        "provenance": provenance,
    }


def sanitize_request(request, run_dir, run_name) -> dict:
    """Clone the historical outer request and point it at this run's input."""
    run_dir = Path(run_dir)
    req = dict(request) if isinstance(request, dict) else {}
    for key in SNAPSHOT_KEYS:
        req[key] = None
    for key in LIST_KEYS:
        req[key] = []
    req["generatedScenarioPath"] = str(run_dir / "input.json")
    req["scenarioId"] = run_name
    req["evidenceDirectory"] = str(run_dir / "evidence")
    req["holdAfterInitialSearch"] = False
    req["seed"] = SEED
    req["characterId"] = CHARACTER
    return req


def write_run_inputs(run_dir, source_dir, run_name):
    """Copy the frozen input unchanged and write the sanitized outer request."""
    run_dir = Path(run_dir)
    source_dir = Path(source_dir)
    run_dir.mkdir(parents=True, exist_ok=True)

    raw = (source_dir / "input.json").read_bytes()
    data = json.loads(raw.decode("utf-8"))
    if (
        data.get("seed") != SEED
        or data.get("characterId") != CHARACTER
        or data.get("encounterId") != ENCOUNTER
    ):
        raise ValueError(
            f"source input is not {CHARACTER} {SEED} {ENCOUNTER}: "
            f"{data.get('characterId')!r}/{data.get('seed')!r}/{data.get('encounterId')!r}"
        )
    input_path = run_dir / "input.json"
    input_path.write_bytes(raw)

    request = json.loads((source_dir / "outer-request.json").read_text(encoding="utf-8"))
    request_path = run_dir / "outer-request.json"
    request_path.write_text(
        json.dumps(sanitize_request(request, run_dir, run_name), ensure_ascii=False, indent=1),
        encoding="utf-8",
    )
    return input_path, request_path


def _new_record(name, run_dir, provenance) -> dict:
    run_dir = Path(run_dir)
    return {
        "run": name,
        "dir": be.rel(run_dir),
        "status": None,
        "reason": None,
        "launched": False,
        "timed_out": False,
        "exit": None,
        "timeout_seconds": None,
        "elapsed_before_launch": None,
        "runtime_seconds": None,
        "player_hp": None,
        "max_hp": None,
        "final_enemy_hp": None,
        "turns": None,
        "fight_wall_ms": None,
        "stdout_path": be.rel(run_dir / "stdout.bin"),
        "stderr_path": be.rel(run_dir / "stderr.bin"),
        "raw_result_path": be.rel(run_dir / RESULT_NAME),
        "harness_result_sha256": None,
        "args": [],
        "provenance": dict(provenance),
    }


def _execute_run(name, run_dir, *, source_dir, runner, clock, start, dry_run, provenance) -> dict:
    run_dir = Path(run_dir)
    record = _new_record(name, run_dir, provenance)

    if dry_run:
        record["status"] = STATUS_DRY_RUN
        record["reason"] = "dry run: subprocess not launched"
        return record

    elapsed = clock() - start
    record["elapsed_before_launch"] = elapsed
    timeout = compute_timeout(elapsed)
    if timeout <= 0:
        record["status"] = STATUS_NOT_STARTED
        record["reason"] = (
            f"batch deadline exhausted before launch: elapsed {elapsed:.3f}s "
            f">= {BATCH_DEADLINE_SECONDS}s"
        )
        return record
    record["timeout_seconds"] = timeout
    input_path, request_path = write_run_inputs(run_dir, source_dir, name)
    record["provenance"]["input_sha256"] = sha256_file(input_path)
    record["provenance"]["outer_request_sha256"] = sha256_file(request_path)
    command = build_command(request_path, run_dir, name)
    record["args"] = command

    started = clock()
    timeout = compute_timeout(started - start)
    record["timeout_seconds"] = timeout
    if timeout <= 0:
        record["status"] = STATUS_NOT_STARTED
        record["reason"] = "batch deadline exhausted during input preparation"
        return record
    record["launched"] = True
    stdout = b""
    stderr = b""
    exit_code = None
    timed_out = False
    launch_error = None
    try:
        proc = runner(
            command,
            cwd=str(CWD),
            capture_output=True,
            timeout=timeout,
            env=build_env(),
        )
        stdout = proc.stdout or b""
        stderr = proc.stderr or b""
        exit_code = proc.returncode
    except subprocess.TimeoutExpired as exc:
        # subprocess.run has already killed and reaped the child; keep what it wrote.
        timed_out = True
        stdout = exc.stdout or b""
        stderr = exc.stderr or b""
    except OSError as exc:
        launch_error = exc
    record["runtime_seconds"] = clock() - started

    write_bytes(run_dir / "stdout.bin", stdout)
    write_bytes(run_dir / "stderr.bin", stderr)
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

    record["status"] = result["status"]
    record["reason"] = result["reason"]
    record["player_hp"] = result.get("player_hp")
    record["max_hp"] = result.get("max_hp")
    record["final_enemy_hp"] = result.get("final_enemy_hp")
    record["turns"] = result.get("turns")
    record["fight_wall_ms"] = result.get("fight_wall_ms")
    return record


def execute_run(name, run_dir, **kwargs) -> dict:
    # Input preparation and payload decoding failures must also reach summary.json.
    try:
        return _execute_run(name, run_dir, **kwargs)
    except Exception as exc:
        record = _new_record(name, run_dir, kwargs["provenance"])
        record["status"] = be.STATUS_ERROR
        record["reason"] = f"probe record failed: {type(exc).__name__}: {exc}"
        record["launched"] = None  # failure may have occurred before or after launch
        return record


def semantic_tuple(record: dict) -> dict:
    record = record if isinstance(record, dict) else {}
    return {field: record.get(field) for field in SEMANTIC_FIELDS}


def build_summary(records, *, executed, output_root, plan) -> dict:
    by_name = {record["run"]: record for record in records}
    ordered = [by_name[name] for name in RUN_ORDER if name in by_name]
    complete = [record for record in ordered if record.get("status") in be.COMPLETE_STATUSES]
    completed = len(complete)
    all_complete = completed == len(RUN_ORDER)

    tuples = {record["run"]: semantic_tuple(record) for record in complete}
    repeat_equal = None
    repeat_different = None
    different_runs = []
    if all_complete:
        baseline = tuples[RUN_ORDER[0]]
        repeat_equal = all(tuples[name] == baseline for name in RUN_ORDER)
        repeat_different = not repeat_equal
        for name in RUN_ORDER:
            if tuples[name] != baseline:
                different_runs.append(
                    {
                        "run": name,
                        "fields": [
                            field
                            for field in SEMANTIC_FIELDS
                            if tuples[name][field] != baseline[field]
                        ],
                    }
                )

    return {
        "generated_at": utc_now(),
        "seed": SEED,
        "executed": bool(executed),
        "output_dir": be.rel(output_root),
        "total_runs": len(RUN_ORDER),
        "completed": completed,
        "all_complete": all_complete,
        "repeat_equal": repeat_equal,
        "repeat_different": repeat_different,
        "gate_passed": bool(all_complete and repeat_equal),
        "semantic_tuples": tuples,
        "different_runs": different_runs,
        "runs": ordered,
        "plan": plan,
        "notes": list(NOTES),
    }


def run_probe(*, run=False, output_root=None, source_dir=None, runner=None, clock=None) -> dict:
    """Run the fixed plan. ``run=False`` (the default) launches nothing."""
    runner = runner if runner is not None else subprocess.run
    clock = clock if clock is not None else time.monotonic
    source_dir = Path(source_dir) if source_dir is not None else SOURCE_DIR
    output_root = Path(output_root) if output_root is not None else default_output_root()
    output_root.mkdir(parents=True, exist_ok=True)

    provenance = collect_provenance(source_dir)
    plan = build_plan(output_root, source_dir, provenance)
    write_json_exclusive(output_root / "plan.json", plan)

    dry_run = not run
    start = clock()
    records = []
    for name in ("sequential-1", "sequential-2"):
        records.append(
            execute_run(
                name,
                output_root / name,
                source_dir=source_dir,
                runner=runner,
                clock=clock,
                start=start,
                dry_run=dry_run,
                provenance=provenance,
            )
        )

    with ThreadPoolExecutor(max_workers=PARALLELISM) as pool:
        futures = {
            name: pool.submit(
                execute_run,
                name,
                output_root / name,
                source_dir=source_dir,
                runner=runner,
                clock=clock,
                start=start,
                dry_run=dry_run,
                provenance=provenance,
            )
            for name in PARALLEL_RUNS
        }
        for name in PARALLEL_RUNS:
            records.append(futures[name].result())

    summary = build_summary(records, executed=run, output_root=output_root, plan=plan)
    write_json_exclusive(output_root / "summary.json", summary)
    return summary


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Fixed building repeatability probe")
    group = parser.add_mutually_exclusive_group()
    group.add_argument(
        "--run", action="store_true", help="execute the four fixed runs (default: dry run)"
    )
    group.add_argument(
        "--dry-run", action="store_true", help="write the plan only; no subprocess (default)"
    )
    args = parser.parse_args(argv)

    summary = run_probe(run=args.run)
    if args.run:
        print(
            f"runs={summary['total_runs']} completed={summary['completed']} "
            f"repeat_equal={summary['repeat_equal']} "
            f"repeat_different={summary['repeat_different']} "
            f"gate_passed={summary['gate_passed']}",
            flush=True,
        )
    else:
        print(json.dumps(summary["plan"], ensure_ascii=False, indent=1), flush=True)
    print(f"wrote {Path(summary['output_dir']) / 'summary.json'}", flush=True)
    if not args.run:
        return 0
    return 0 if summary["gate_passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
