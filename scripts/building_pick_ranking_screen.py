#!/usr/bin/env python3
"""Bounded descriptive pick-ranking screen for the two accepted PROXY states.

This instrument extends the accepted input-fidelity probe into a small,
*predeclared* ranking screen. It answers one deliberately narrow question:
**for the two accepted fixture states and their four frozen actions, does a
4 s and a 20 s single-threaded full fight produce stable, comparable outcomes,
and how do the top actions change between the two search budgets?**

It is not a strength or training tool:

* the fixed design is two fixtures x four actions x two budgets = 16 launches,
  one observation per cell, no repetitions and no retries;
* the only seed is the development seed ``PICKSEL01`` (in both the copied input
  and the outer request); the two fixtures differ from their accepted source
  *only* by that seed, which is asserted structurally before every launch;
* the ranking rule is frozen before any data is collected (see
  ``RANKING_RULE``); a fixture/budget only ranks when all four actions have
  verified input fidelity *and* a complete win/loss outcome;
* the default command is a dry run that launches nothing; ``--run`` is required;
* all child processes are sequential, each capped at 120 s and the whole batch at
  900 s of monotonic time, recomputed *after* input preparation; a launch without
  remaining budget is recorded ``not_started`` and never as a fight;
* source scenario files are re-hashed when copied, and the actual harness runtime
  is captured read-only: the runtimeconfig ``Sts2DataDir``/``sts2.dll`` and the
  effective ``CombatSolverDll`` (honouring ``OFFLINE_HARNESS_COMBATSOLVER_DLL``),
  plus harness/dotnet and source/helper hashes. The game ``sts2.dll`` hash must
  match the adapter-declared game hash before any real launch; missing artifacts
  fail first. The inherited environment is passed to the child but never logged.

Accepted evidence comes only from the harness-written
``evidence/generated-scenario.loadout.json`` and the raw ``harness-result.json``,
never inferred from the input or the outer request.

Usage:
    python scripts/building_pick_ranking_screen.py          # dry run, plan only
    python scripts/building_pick_ranking_screen.py --run    # execute the 16 runs
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path

import building_eval as be
import building_pick_input_probe as bpi
import building_repeat_probe as brp

ROOT = brp.ROOT

# Fixed repository paths, reused read-only from the repeat probe module.
HARNESS = brp.HARNESS
DOTNET = brp.DOTNET
CWD = brp.CWD
RESULT_NAME = brp.RESULT_NAME

# The accepted, independently verified fixture directory and its historical
# outer-request template (both read-only).
SOURCE_DIR = bpi.SOURCE_DIR
HISTORICAL_DIR = bpi.HISTORICAL_DIR
OUTPUT_ROOT = ROOT / "work" / "pick-ranking-screen"

ADAPTER_NAME = bpi.ADAPTER_NAME
MANIFEST_NAME = bpi.MANIFEST_NAME

CHARACTER = bpi.CHARACTER
ASCENSION = bpi.ASCENSION
PLAYER_HP = bpi.PLAYER_HP

# The frozen development seeds and the two fixed search budgets. The default
# seed preserves the accepted screen; PICKSEL02 is the second development seed.
SEED = "PICKSEL01"
SUPPORTED_SEEDS = ("PICKSEL01", "PICKSEL02")
BUDGETS = (4000, 20000)

# Actions in their canonical order (skip plus the three candidate additions).
ACTIONS = ("skip", "BACKFLIP", "BLADE_DANCE", "DAGGER_SPRAY")

# Fixed within-fixture (action, budget-ms) order. The late fixture uses the
# entire reversed sequence, so budget order alternates within each fixture.
WF_ORDER = (
    ("skip", 4000),
    ("skip", 20000),
    ("BACKFLIP", 20000),
    ("BACKFLIP", 4000),
    ("BLADE_DANCE", 4000),
    ("BLADE_DANCE", 20000),
    ("DAGGER_SPRAY", 20000),
    ("DAGGER_SPRAY", 4000),
)

# Fixed harness flags, matching the accepted successful probes except for the
# full-fight turn cap.
PROFILE = "Medium"
NODES = "6000"
SEARCH_TIMEOUT_MS = "60000"
DOP = "1"
PLAY_PATH = "gameaction"
MAX_TURNS = "40"
MILESTONE = "M2"

MAX_LAUNCHES = 16
PER_RUN_TIMEOUT_SECONDS = 120
BATCH_DEADLINE_SECONDS = 900
EXPECTED_FIXTURES = 2
RUNTIME_ENV_OVERRIDE = "OFFLINE_HARNESS_COMBATSOLVER_DLL"

# Input/process status vocabulary is shared with the accepted input probe; the
# fight result lives only in ``outcome``.
STATUS_VERIFIED = bpi.STATUS_VERIFIED
STATUS_MISMATCH = bpi.STATUS_MISMATCH
STATUS_MISSING = bpi.STATUS_MISSING
STATUS_ERROR = bpi.STATUS_ERROR
STATUS_TIMEOUT = bpi.STATUS_TIMEOUT
STATUS_NOT_STARTED = bpi.STATUS_NOT_STARTED
STATUS_DRY_RUN = bpi.STATUS_DRY_RUN
STATUS_ORDER = bpi.STATUS_ORDER

ENV_OVERRIDES = dict(brp.ENV_OVERRIDES)

RANKING_RULE = {
    "declared_before_data": True,
    "eligible_group": "a fixture/budget ranks only when all four actions have "
    "verified input fidelity and a complete win/loss outcome",
    "win_outranks_loss": True,
    "among_wins": "higher remaining player HP ranks higher",
    "losses": "all losses tie; enemy remaining HP is never used as a strength tiebreak",
    "equal_hp": "equal remaining player HP ties",
    "turns": "diagnostic only, never used to order actions",
    "top_set": "the highest-ranked tie group",
    "budget_comparison": "descriptive only: top sets and pairwise order are "
    "compared and changes marked; there is no superiority gate",
    "limits": "one seed and one observation per cell cannot establish gains, "
    "variance or training labels",
}

NOTES = [
    "The default command is a dry run that launches nothing; --run is required.",
    "The only seed is PICKSEL01, placed in both the copied input and the outer "
    "request; the copied input is asserted to differ from the accepted source "
    "only by that seed.",
    "The ranking rule is descriptive and frozen before data; a fixture/budget "
    "with any incomplete action cannot be ranked.",
    "This screen does not train a model, does not use PICKSEL02 or the held-out "
    "PICKEVAL seeds, and makes no strength, variance or superiority claim.",
]

LIMITATIONS = [
    "Both states are PROXY fixtures, not observed reward screens; the act0 "
    "starter encounter is a fixture proxy.",
    "Relics and potions are empty by design (an ablation); this changes real-run "
    "meaning and is asserted only as fixture fidelity.",
    "One seed and one observation per cell cannot separate run-to-run noise or "
    "establish candidate gains, variance or training labels.",
    "A 4000 ms and a 20000 ms single-threaded full fight are two reference "
    "points; the screen never declares one budget superior.",
    "The batch deadline is monotonic wall clock; a slow host can mark later runs "
    "not_started, which is recorded rather than hidden.",
    "Evidence is only as trustworthy as the harness build and the accepted "
    "fixture directory it consumes.",
]


class SourceValidationError(ValueError):
    """Raised when the accepted source, manifest or historical request is invalid."""


class RuntimeValidationError(ValueError):
    """Raised when the actual harness runtime cannot be verified before launch."""


def validate_seed(seed) -> str:
    """Return a supported development seed; reject held-out or arbitrary seeds."""
    if seed not in SUPPORTED_SEEDS:
        raise SourceValidationError(
            f"unsupported development seed {seed!r}; expected one of {list(SUPPORTED_SEEDS)}"
        )
    return seed


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def default_output_root(now=None) -> Path:
    """A fresh UTC timestamp directory with microsecond resolution."""
    moment = now if now is not None else datetime.now(timezone.utc)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    stamp = moment.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    return OUTPUT_ROOT / stamp


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def compute_timeout(elapsed_seconds: float) -> float:
    """Per-launch cap: at most 120 s and never past the 900 s batch deadline."""
    remaining = BATCH_DEADLINE_SECONDS - elapsed_seconds
    if remaining <= 0:
        return 0.0
    return float(min(PER_RUN_TIMEOUT_SECONDS, remaining))


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


def diff_paths(left, right, prefix: str = "") -> set:
    """Return the dotted paths where two JSON-like trees differ."""
    if isinstance(left, dict) and isinstance(right, dict):
        diffs = set()
        for key in set(left) | set(right):
            path = f"{prefix}.{key}" if prefix else str(key)
            if key not in left or key not in right:
                diffs.add(path)
            else:
                diffs |= diff_paths(left[key], right[key], path)
        return diffs
    if isinstance(left, list) and isinstance(right, list):
        if len(left) != len(right):
            return {prefix or "<root>"}
        diffs = set()
        for index, (item_left, item_right) in enumerate(zip(left, right)):
            diffs |= diff_paths(item_left, item_right, f"{prefix}[{index}]")
        return diffs
    if left != right:
        return {prefix or "<root>"}
    return set()


def resolve_build_value_action(adapter_fixture) -> str:
    """Map adapter.bestRewardAllowSkip (-1 skip else candidate index) to an action key."""
    index = adapter_fixture.get("bestRewardAllowSkip")
    if type(index) is not int:
        raise SourceValidationError(
            f"fixture {adapter_fixture.get('id')!r} has no integer bestRewardAllowSkip"
        )
    if index == -1:
        return "skip"
    candidates = adapter_fixture.get("candidates")
    if not isinstance(candidates, list) or not (0 <= index < len(candidates)):
        raise SourceValidationError(
            f"fixture {adapter_fixture.get('id')!r} bestRewardAllowSkip {index} "
            "is outside the candidate list"
        )
    candidate = candidates[index]
    if not isinstance(candidate, dict) or not isinstance(candidate.get("id"), str):
        raise SourceValidationError(
            f"fixture {adapter_fixture.get('id')!r} candidate {index} has no id"
        )
    return candidate["id"]


def _fixture_order(source) -> list:
    """Fixture ids in adapter order, rejecting a design that is not two fixtures."""
    fixtures = source["adapter"]["fixtures"]
    if len(fixtures) != EXPECTED_FIXTURES:
        raise SourceValidationError(f"expected {EXPECTED_FIXTURES} fixtures")
    return [fixture["id"] for fixture in fixtures]


def order_specs(source, specs, seed=SEED) -> list:
    """Order the eight accepted actions into the fixed 16-run sequence.

    The two fixtures use 4 ms/20 ms pairs. The first fixture follows ``WF_ORDER``
    and each later fixture uses the entire reversed within-fixture order. The
    requested development seed is validated before it replaces the source seed.
    """
    seed = validate_seed(seed)
    fixture_ids = _fixture_order(source)
    by_fixture = {fixture_id: {} for fixture_id in fixture_ids}
    for spec in specs:
        if spec["fixture"] not in by_fixture:
            raise SourceValidationError(f"action references unknown fixture {spec['fixture']!r}")
        key = "skip" if spec["candidate"] is None else spec["candidate"]
        if key in by_fixture[spec["fixture"]]:
            raise SourceValidationError(f"duplicate action {key!r} in fixture {spec['fixture']!r}")
        by_fixture[spec["fixture"]][key] = spec

    ordered = []
    for index, fixture_id in enumerate(fixture_ids):
        actions = by_fixture[fixture_id]
        if set(actions) != set(ACTIONS):
            raise SourceValidationError(
                f"fixture {fixture_id!r} must offer {list(ACTIONS)}, got {sorted(actions)}"
            )
        sequence = WF_ORDER if index == 0 else tuple(reversed(WF_ORDER))
        for action, budget in sequence:
            spec = actions[action]
            seeded = dict(spec)
            seeded["source_seed"] = spec["seed"]
            seeded["seed"] = seed
            seeded["budget_ms"] = budget
            ordered.append(seeded)

    if len(ordered) != MAX_LAUNCHES or len({(s["fixture"], s["candidate"], s["budget_ms"]) for s in ordered}) != MAX_LAUNCHES:
        raise SourceValidationError(f"expected {MAX_LAUNCHES} unique fixture/action/budget runs")
    return ordered


def prepare_source(source_dir=None, seed=SEED):
    """Load and validate the accepted source, then build the fixed run sequence."""
    source = bpi.load_source(Path(source_dir) if source_dir is not None else SOURCE_DIR)
    specs = bpi.build_specs(source)
    return source, order_specs(source, specs, seed=seed)


def build_command(request_path, run_dir, name, budget, *, harness_path=None, dotnet_path=None) -> list:
    harness = Path(harness_path) if harness_path is not None else HARNESS
    dotnet = Path(dotnet_path) if dotnet_path is not None else DOTNET
    return [
        str(dotnet),
        str(harness),
        "--request", str(request_path),
        "--profile", PROFILE,
        "--nodes", NODES,
        "--budget-ms", str(budget),
        "--search-timeout-ms", SEARCH_TIMEOUT_MS,
        "--dop", DOP,
        "--no-card-probe",
        "--deploy-plan",
        "--play-path", PLAY_PATH,
        "--max-turns", MAX_TURNS,
        "--milestone", MILESTONE,
        "--label", name,
        "--out", str(run_dir),
    ]


def build_runs(ordered_specs, output_root, *, harness_path=None, dotnet_path=None) -> list:
    """Attach per-run directories and the exact argv for each ordered spec."""
    output_root = Path(output_root)
    runs = []
    for spec in ordered_specs:
        budget = spec["budget_ms"]
        name = f"{spec['name']}-b{budget}"
        run_dir = output_root / name
        run = dict(spec)
        run["scenario_path"] = str(spec["scenario_path"])
        run.update(
            {
                "name": name,
                "dir": be.rel(run_dir),
                "input": be.rel(run_dir / "input.json"),
                "request": be.rel(run_dir / "outer-request.json"),
                "command": build_command(
                    run_dir / "outer-request.json",
                    run_dir,
                    name,
                    budget,
                    harness_path=harness_path,
                    dotnet_path=dotnet_path,
                ),
                "timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
            }
        )
        runs.append(run)
    return runs


def _resolve_config_path(value, base: Path):
    if not isinstance(value, str) or not value:
        return None
    path = Path(value)
    return path if path.is_absolute() else (base / path)


def collect_runtime_provenance(runtime_config_path, adapter, *, harness_path=None,
                               dotnet_path=None, env=None) -> dict:
    """Read the actual harness runtime read-only and verify it before any launch."""
    harness = Path(harness_path) if harness_path is not None else HARNESS
    dotnet = Path(dotnet_path) if dotnet_path is not None else DOTNET
    runtime_config_path = Path(runtime_config_path)
    env = os.environ if env is None else env

    missing = []
    for label, path in (
        ("runtimeconfig", runtime_config_path),
        ("harness", harness),
        ("dotnet", dotnet),
    ):
        if not path.exists():
            missing.append(f"{label} absent: {be.rel(path)}")
    if missing:
        raise RuntimeValidationError("; ".join(missing))

    try:
        config = json.loads(runtime_config_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise RuntimeValidationError(f"runtimeconfig unreadable: {exc}") from exc
    runtime_options = config.get("runtimeOptions") if isinstance(config, dict) else None
    props = runtime_options.get("configProperties") if isinstance(runtime_options, dict) else None
    props = props if isinstance(props, dict) else {}

    base = runtime_config_path.parent
    sts2_dir = _resolve_config_path(props.get("Sts2DataDir"), base)
    config_combat = props.get("CombatSolverDll")
    if sts2_dir is None:
        raise RuntimeValidationError("runtimeconfig configProperties.Sts2DataDir absent")
    sts2_dll = sts2_dir / "sts2.dll"

    override_value = env.get(RUNTIME_ENV_OVERRIDE) if hasattr(env, "get") else None
    override_value = override_value if isinstance(override_value, str) and override_value else None
    effective_combat = override_value or (config_combat if isinstance(config_combat, str) else None)
    effective_combat_path = _resolve_config_path(effective_combat, base)

    missing = []
    if not sts2_dll.exists():
        missing.append(f"sts2.dll absent: {be.rel(sts2_dll)}")
    if effective_combat_path is None:
        missing.append("effective CombatSolverDll absent from runtimeconfig and environment")
    elif not effective_combat_path.exists():
        missing.append(f"CombatSolverDll absent: {be.rel(effective_combat_path)}")
    if missing:
        raise RuntimeValidationError("; ".join(missing))

    adapter_runtime = adapter.get("runtime") if isinstance(adapter, dict) else None
    adapter_sts2 = adapter_runtime.get("sts2") if isinstance(adapter_runtime, dict) else None
    adapter_sts2_sha = adapter_sts2.get("sha256") if isinstance(adapter_sts2, dict) else None
    if not adapter_sts2_sha:
        raise RuntimeValidationError("adapter does not declare runtime.sts2.sha256")

    game_sha = be.sha256_file(sts2_dll)
    if game_sha != adapter_sts2_sha:
        raise RuntimeValidationError(
            f"game sts2.dll sha256 {game_sha} != adapter runtime.sts2.sha256 {adapter_sts2_sha}"
        )

    return {
        "runtime_config": {
            "path": be.rel(runtime_config_path),
            "sha256": be.sha256_file(runtime_config_path),
        },
        "config_properties": {
            "Sts2DataDir": str(sts2_dir),
            "CombatSolverDll": config_combat if isinstance(config_combat, str) else None,
        },
        "combat_solver_dll": {
            "config_path": config_combat if isinstance(config_combat, str) else None,
            "effective_path": str(effective_combat_path),
            "effective_sha256": be.sha256_file(effective_combat_path),
            "override_env": RUNTIME_ENV_OVERRIDE,
            "override": override_value is not None,
        },
        "sts2_dll": {
            "path": be.rel(sts2_dll),
            "sha256": game_sha,
            "adapter_sha256": adapter_sts2_sha,
            "matches_adapter": True,
        },
        "harness": {"path": be.rel(harness), "sha256": be.sha256_file(harness)},
        "dotnet": {"path": be.rel(dotnet), "sha256": be.sha256_file(dotnet)},
    }


def collect_provenance(source, historical_request_path, *, harness_path=None, dotnet_path=None) -> dict:
    harness = Path(harness_path) if harness_path is not None else HARNESS
    dotnet = Path(dotnet_path) if dotnet_path is not None else DOTNET
    adapter = source["adapter"]
    runtime = adapter.get("runtime") if isinstance(adapter, dict) else None
    runtime = runtime if isinstance(runtime, dict) else {}
    dlls = {}
    for key in ("probeAssembly", "coopBotsAssembly", "sts2", "godotSharp", "harmony"):
        entry = runtime.get(key)
        entry = entry if isinstance(entry, dict) else {}
        dlls[key] = {"path": entry.get("path"), "sha256": entry.get("sha256")}
    return {
        "driver": {"path": be.rel(Path(__file__)), "sha256": be.sha256_file(Path(__file__))},
        "input_probe_helper": {
            "path": be.rel(Path(bpi.__file__)),
            "sha256": be.sha256_file(Path(bpi.__file__)),
        },
        "repeat_helpers": {
            "path": be.rel(Path(brp.__file__)),
            "sha256": be.sha256_file(Path(brp.__file__)),
        },
        "classifier": {"path": be.rel(Path(be.__file__)), "sha256": be.sha256_file(Path(be.__file__))},
        "harness": {"path": be.rel(harness), "sha256": be.sha256_file(harness)},
        "dotnet": {"path": be.rel(dotnet), "sha256": be.sha256_file(dotnet)},
        "adapter": {"path": be.rel(source["adapter_path"]), "sha256": source["adapter_sha256"]},
        "manifest": {"path": be.rel(source["manifest_path"]), "sha256": source["manifest_sha256"]},
        "historical_request": {
            "path": be.rel(historical_request_path),
            "sha256": be.sha256_file(historical_request_path),
        },
        "runtime_dlls": dlls,
    }


def build_plan(runs, source, historical_request_path, runtime, provenance, *,
               harness_path=None, dotnet_path=None) -> dict:
    harness = Path(harness_path) if harness_path is not None else HARNESS
    dotnet = Path(dotnet_path) if dotnet_path is not None else DOTNET
    return {
        "generated_at": utc_now(),
        "tool": "building_pick_ranking_screen.py",
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
        "per_run_timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
        "env_overrides": dict(ENV_OVERRIDES),
        "ranking_rule": dict(RANKING_RULE),
        "runtime": runtime,
        "runs": runs,
        "provenance": provenance,
    }


def write_run_inputs(run_dir, run, historical_request_path):
    """Re-hash the accepted scenario, change only the validated run seed, and clone the request.

    The seed defaults to the frozen ``SEED`` so accepted callers are unchanged;
    an explicit per-run seed must be one of the supported development seeds.
    """
    run_dir = Path(run_dir)
    run_dir.mkdir(parents=True, exist_ok=True)

    scenario_path = Path(run["scenario_path"])
    raw = scenario_path.read_bytes()
    actual_sha = sha256_bytes(raw)
    if actual_sha != run["scenario_sha256"]:
        raise SourceValidationError(
            f"scenario changed since source load: {run['scenario_source']} "
            f"sha256 {actual_sha} != {run['scenario_sha256']}"
        )
    original = json.loads(raw.decode("utf-8"))
    if original.get("seed") != run["source_seed"]:
        raise SourceValidationError(
            f"scenario {run['scenario_source']} seed {original.get('seed')!r} "
            f"!= manifest seed {run['source_seed']!r}"
        )

    requested = run.get("seed")
    requested = SEED if requested is None else validate_seed(requested)
    seeded = dict(original)
    seeded["seed"] = requested
    diff = diff_paths(original, seeded)
    if diff != {"seed"}:
        raise SourceValidationError(
            f"seed override changed unexpected input fields: {sorted(diff)}"
        )
    input_path = run_dir / "input.json"
    input_path.write_text(json.dumps(seeded, ensure_ascii=False, indent=1), encoding="utf-8")

    request = json.loads(Path(historical_request_path).read_text(encoding="utf-8"))
    sanitized = brp.sanitize_request(request, run_dir, run["name"])
    baseline = deepcopy(sanitized)
    sanitized["seed"] = requested
    request_diff = diff_paths(baseline, sanitized)
    if request_diff != {"seed"}:
        raise SourceValidationError(
            f"outer request seed override changed unexpected fields: {sorted(request_diff)}"
        )
    if sanitized.get("characterId") != run["character"]:
        raise SourceValidationError("outer request character does not match the fixture")
    request_path = run_dir / "outer-request.json"
    request_path.write_text(
        json.dumps(sanitized, ensure_ascii=False, indent=1), encoding="utf-8"
    )
    return input_path, request_path


def _payload_metrics(payload) -> dict:
    if not isinstance(payload, dict):
        return {
            "cost_ms": None,
            "wall_seconds": None,
            "time_boundary_observed": None,
            "combat_in_progress": None,
            "fight_truncated": None,
        }
    metrics = {}
    solver = payload.get("solverMetrics")
    elapsed = solver.get("ElapsedMilliseconds") if isinstance(solver, dict) else None
    metrics["cost_ms"] = elapsed if isinstance(elapsed, (int, float)) and not isinstance(elapsed, bool) else None
    wall = payload.get("wallSeconds")
    metrics["wall_seconds"] = wall if isinstance(wall, (int, float)) and not isinstance(wall, bool) else None
    metrics["time_boundary_observed"] = payload.get("timeBoundaryObserved")
    metrics["combat_in_progress"] = payload.get("combatInProgress")
    metrics["fight_truncated"] = payload.get("fightTruncated")
    return metrics


def _new_record(run, run_dir, provenance) -> dict:
    run_dir = Path(run_dir)
    return {
        "run": run["name"],
        "fixture": run.get("fixture"),
        "action": run.get("action"),
        "candidate": run.get("candidate"),
        "budget_ms": run.get("budget_ms"),
        "seed": run.get("seed"),
        "character": run.get("character"),
        "ascension": run.get("ascension"),
        "act_index": run.get("act_index"),
        "encounter": run.get("encounter"),
        "source_seed": run.get("source_seed"),
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
        "outcome": None,
        "eligible": False,
        "player_hp": None,
        "max_hp": None,
        "enemy_hp": None,
        "turns": None,
        "fight_wall_ms": None,
        "cost_ms": None,
        "wall_seconds": None,
        "time_boundary_observed": None,
        "checks": {},
        "mismatches": [],
        "raw_hashes": {},
        "stdout_path": be.rel(run_dir / "stdout.bin"),
        "stderr_path": be.rel(run_dir / "stderr.bin"),
        "raw_result_path": be.rel(run_dir / RESULT_NAME),
        "args": [],
        "provenance": dict(provenance),
    }


def _missing_record(run, output_root, provenance) -> dict:
    record = _new_record(run, Path(output_root) / run["name"], provenance)
    record["status"] = STATUS_ERROR
    record["reason"] = "case was not recorded before the batch ended"
    return record


def _outcome_for(exit_code, timed_out, payload, load_error, launch_error) -> dict:
    if launch_error is not None:
        return be.classify(
            {"timeout": False},
            {"error": {"message": f"launch failed: {launch_error}"}},
            None,
        )
    return be.classify({"exit": exit_code, "timeout": timed_out}, payload, load_error)


def _finish_input(record, run, adapter_fixture, payload, load_error, run_dir, launch_error) -> None:
    if launch_error is not None:
        record["status"] = STATUS_ERROR
        record["reason"] = f"launch failed: {launch_error}"
        return
    if record["timed_out"]:
        record["status"] = STATUS_TIMEOUT
        record["reason"] = f"process timeout after {record['timeout_seconds']:.3f}s"
        return
    exit_code = record["exit"]
    if type(exit_code) is not int or exit_code != 0:
        record["status"] = STATUS_ERROR
        record["reason"] = f"nonzero process exit {exit_code}"
        return
    if load_error == "absent":
        record["status"] = STATUS_MISSING
        record["reason"] = "harness-result.json absent"
        return
    if load_error is not None:
        record["status"] = STATUS_MISMATCH
        record["reason"] = f"unreadable harness-result.json: {load_error}"
        return
    if not isinstance(payload, dict):
        record["status"] = STATUS_MISMATCH
        record["reason"] = "harness-result.json is not a JSON object"
        return

    error = payload.get("error")
    if isinstance(error, dict):
        error_message = error.get("message") or error.get("code") or "payload error"
    elif error:
        error_message = str(error)
    else:
        error_message = None
    if error_message:
        record["status"] = STATUS_ERROR
        record["reason"] = f"payload error: {error_message}"
        return

    evidence = bpi.verify_run_evidence(run, adapter_fixture, payload, run_dir)
    record["checks"] = evidence["checks"]
    record["mismatches"] = evidence["reasons"]
    record["raw_hashes"]["expected_deck_sha256"] = evidence["expected_deck_sha256"]
    record["raw_hashes"]["actual_deck_sha256"] = evidence["actual_deck_sha256"]
    record["raw_hashes"]["loadout_sha256"] = evidence["loadout_sha256"]
    record["raw_hashes"]["resolved_sha256"] = evidence["resolved_sha256"]
    record["status"] = evidence["status"]
    record["reason"] = "; ".join(evidence["reasons"]) if evidence["reasons"] else None


def _execute_run(run, output_root, *, adapter_fixture, historical_request_path, runner,
                 clock, start, dry_run, provenance) -> dict:
    run_dir = Path(output_root) / run["name"]
    record = _new_record(run, run_dir, provenance)

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

    input_path, request_path = write_run_inputs(run_dir, run, historical_request_path)
    record["raw_hashes"]["input_sha256"] = be.sha256_file(input_path)
    record["raw_hashes"]["request_sha256"] = be.sha256_file(request_path)
    command = list(run["command"])
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
        timed_out = True
        stdout = exc.stdout or b""
        stderr = exc.stderr or b""
    except OSError as exc:
        launch_error = exc
    record["runtime_seconds"] = clock() - started

    brp.write_bytes(run_dir / "stdout.bin", stdout)
    brp.write_bytes(run_dir / "stderr.bin", stderr)
    record["raw_hashes"]["stdout_sha256"] = sha256_bytes(stdout)
    record["raw_hashes"]["stderr_sha256"] = sha256_bytes(stderr)
    record["timed_out"] = timed_out
    record["exit"] = exit_code

    raw_path = run_dir / RESULT_NAME
    payload, load_error = be.load_payload(raw_path)
    if raw_path.exists():
        record["raw_hashes"]["harness_result_sha256"] = be.sha256_file(raw_path)

    record["outcome"] = _outcome_for(exit_code, timed_out, payload, load_error, launch_error)
    _finish_input(record, run, adapter_fixture, payload, load_error, run_dir, launch_error)

    metrics = _payload_metrics(payload)
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
    return record


def execute_run(run, output_root, **kwargs) -> dict:
    """Run one case; any preparation/decoding failure becomes an error record."""
    try:
        return _execute_run(run, output_root, **kwargs)
    except Exception as exc:  # noqa: BLE001 - the journal already holds attempts
        record = _new_record(run, Path(output_root) / run["name"], kwargs["provenance"])
        record["status"] = STATUS_ERROR
        record["reason"] = f"ranking record failed: {type(exc).__name__}: {exc}"
        record["launched"] = None
        return record


def _action_key(record) -> str:
    return "skip" if record.get("candidate") is None else record["candidate"]


def build_ranking(group_records, budget_ms, build_value_action) -> dict:
    """Rank one fixture/budget group with the frozen descriptive rule."""
    outcomes = {action: (group_records.get(action) or {}).get("outcome") for action in ACTIONS}
    reasons = {}
    for action in ACTIONS:
        record = group_records.get(action)
        if record is None:
            reasons[action] = "record missing"
            continue
        if record.get("status") != STATUS_VERIFIED:
            reasons[action] = f"input {record.get('status')}: {record.get('reason')}"
            continue
        outcome = record.get("outcome") or {}
        if outcome.get("status") not in be.COMPLETE_STATUSES:
            reasons[action] = f"outcome {outcome.get('status')}: {outcome.get('reason')}"

    if reasons:
        return {
            "available": False,
            "budget_ms": budget_ms,
            "reasons": reasons,
            "outcomes": outcomes,
        }

    groups = []
    wins = [
        action
        for action in ACTIONS
        if (group_records[action]["outcome"] or {}).get("status") == be.STATUS_WIN
    ]
    losses = [action for action in ACTIONS if action not in wins]
    for action in sorted(wins, key=lambda a: -int(group_records[a]["outcome"].get("player_hp") or 0)):
        hp = int(group_records[action]["outcome"].get("player_hp"))
        if groups and groups[-1]["result"] == be.STATUS_WIN and groups[-1]["remaining_hp"] == hp:
            groups[-1]["actions"].append(action)
        else:
            groups.append({"result": be.STATUS_WIN, "remaining_hp": hp, "actions": [action]})
    if losses:
        groups.append({"result": be.STATUS_LOSS, "remaining_hp": None, "actions": sorted(losses)})
    for rank, group in enumerate(groups, start=1):
        group["rank"] = rank

    rank_index = {action: group["rank"] for group in groups for action in group["actions"]}
    top_actions = list(groups[0]["actions"])
    build_value_rank = rank_index.get(build_value_action)
    return {
        "available": True,
        "budget_ms": budget_ms,
        "tie_groups": groups,
        "order": [action for group in groups for action in group["actions"]],
        "top_actions": top_actions,
        "top_rank": groups[0]["rank"],
        "build_value_action": build_value_action,
        "build_value_outcome": outcomes.get(build_value_action),
        "build_value_rank": build_value_rank,
        "build_value_in_top": build_value_rank == groups[0]["rank"],
        "outcomes": outcomes,
        "result_counts": _status_counts_from_outcomes(outcomes),
    }


def _status_counts_from_outcomes(outcomes) -> dict:
    counts = {}
    for outcome in outcomes.values():
        status = outcome.get("status") if isinstance(outcome, dict) else None
        if status:
            counts[status] = counts.get(status, 0) + 1
    return counts


def compare_budgets(ranking_4s, ranking_20s) -> dict:
    """Descriptively compare the two budget rankings; never declare a winner."""
    reasons = {}
    if not ranking_4s.get("available"):
        reasons["budget_4000ms"] = ranking_4s.get("reasons")
    if not ranking_20s.get("available"):
        reasons["budget_20000ms"] = ranking_20s.get("reasons")
    if reasons:
        return {"available": False, "reasons": reasons}

    def rank_index(ranking):
        return {action: group["rank"] for group in ranking["tie_groups"] for action in group["actions"]}

    index_4s = rank_index(ranking_4s)
    index_20s = rank_index(ranking_20s)

    def order(a, b, index):
        if index[a] == index[b]:
            return "tie"
        return a if index[a] < index[b] else b

    pairwise = []
    changed = []
    for position, action_a in enumerate(ACTIONS):
        for action_b in ACTIONS[position + 1:]:
            order_4s = order(action_a, action_b, index_4s)
            order_20s = order(action_a, action_b, index_20s)
            entry = {
                "pair": [action_a, action_b],
                "order_4000ms": order_4s,
                "order_20000ms": order_20s,
                "changed": order_4s != order_20s,
            }
            pairwise.append(entry)
            if entry["changed"]:
                changed.append(entry)

    tie_structure_4s = [sorted(group["actions"]) for group in ranking_4s["tie_groups"]]
    tie_structure_20s = [sorted(group["actions"]) for group in ranking_20s["tie_groups"]]
    return {
        "available": True,
        "top_set_4000ms": ranking_4s["top_actions"],
        "top_set_20000ms": ranking_20s["top_actions"],
        "top_set_changed": set(ranking_4s["top_actions"]) != set(ranking_20s["top_actions"]),
        "tie_structure_4000ms": tie_structure_4s,
        "tie_structure_20000ms": tie_structure_20s,
        "tie_structure_changed": tie_structure_4s != tie_structure_20s,
        "pairwise": pairwise,
        "order_changed": changed,
        "note": "descriptive only; no superiority gate is applied",
    }


def _status_counts(records) -> dict:
    counts = {status: 0 for status in STATUS_ORDER}
    for record in records:
        status = record.get("status")
        if status in counts:
            counts[status] += 1
    return counts


def _outcome_counts(records) -> dict:
    counts = {}
    for record in records:
        outcome = record.get("outcome")
        status = outcome.get("status") if isinstance(outcome, dict) else None
        if status:
            counts[status] = counts.get(status, 0) + 1
    return counts


def _ranking_groups(ordered, fixture_ids, build_value_actions) -> dict:
    grouped = {}
    for record in ordered:
        key = (record.get("fixture"), record.get("budget_ms"))
        grouped.setdefault(key, {})[_action_key(record)] = record
    rankings = {}
    for fixture_id in fixture_ids:
        rankings[fixture_id] = {}
        for budget in BUDGETS:
            rankings[fixture_id][budget] = build_ranking(
                grouped.get((fixture_id, budget), {}),
                budget,
                build_value_actions[fixture_id],
            )
    return rankings


def build_summary(records, *, executed, output_root, plan, source, runtime, error=None) -> dict:
    fixture_ids = _fixture_order(source)
    by_name = {record["run"]: record for record in records}
    ordered = [
        by_name.get(run["name"]) or _missing_record(run, output_root, plan["provenance"])
        for run in plan["runs"]
    ]

    adapter_fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    build_value_actions = {
        fixture_id: resolve_build_value_action(adapter_fixtures[fixture_id])
        for fixture_id in fixture_ids
    }

    status_counts = _status_counts(ordered)
    verified = sum(1 for record in ordered if record.get("status") == STATUS_VERIFIED)
    eligible = sum(1 for record in ordered if record.get("eligible"))
    attempts_accounted = len(ordered) == MAX_LAUNCHES and len(records) == MAX_LAUNCHES

    rankings = _ranking_groups(ordered, fixture_ids, build_value_actions)
    budget_comparison = {
        fixture_id: compare_budgets(rankings[fixture_id][4000], rankings[fixture_id][20000])
        for fixture_id in fixture_ids
    }

    return {
        "generated_at": utc_now(),
        "tool": "building_pick_ranking_screen.py",
        "executed": bool(executed),
        "output_dir": be.rel(Path(output_root)),
        "seed": SEED,
        "budgets_ms": list(BUDGETS),
        "character": CHARACTER,
        "ascension": ASCENSION,
        "player_hp": PLAYER_HP,
        "batch": {
            "total_attempts": MAX_LAUNCHES,
            "attempts_accounted": attempts_accounted,
            "per_run_timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
            "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
            "max_turns": MAX_TURNS,
            "budgets_ms": list(BUDGETS),
            "dop": DOP,
            "nodes": NODES,
        },
        "status_counts": status_counts,
        "outcome_counts": _outcome_counts(ordered),
        "verified": verified,
        "eligible_completed": eligible,
        "tool_completed": bool(attempts_accounted and error is None),
        "ranking_rule": dict(RANKING_RULE),
        "ranking": rankings,
        "budget_comparison": budget_comparison,
        "runtime": runtime,
        "runs": ordered,
        "plan": plan,
        "error": error,
        "notes": list(NOTES),
        "limitations": list(LIMITATIONS),
    }


def run_ranking_screen(*, run=False, output_root=None, source_dir=None, historical_dir=None,
                       harness_path=None, dotnet_path=None, runtime_config_path=None,
                       runtime_env=None, runner=None, clock=None, now=None) -> dict:
    """Run the fixed 16-cell screen. ``run=False`` (the default) launches nothing."""
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

    source, ordered = prepare_source(source_dir)
    if not historical_request_path.exists():
        raise SourceValidationError(
            f"historical outer request absent: {be.rel(historical_request_path)}"
        )

    try:
        runtime = collect_runtime_provenance(
            runtime_config_path,
            source["adapter"],
            harness_path=harness_path,
            dotnet_path=dotnet_path,
            env=runtime_env,
        )
    except RuntimeValidationError as exc:
        if run:
            raise
        runtime = {"ok": False, "error": str(exc)}

    # Refuse an existing output directory: the timestamp is expected to be unique.
    output_root.mkdir(parents=True, exist_ok=False)
    runs = build_runs(ordered, output_root, harness_path=harness_path, dotnet_path=dotnet_path)
    provenance = collect_provenance(
        source, historical_request_path, harness_path=harness_path, dotnet_path=dotnet_path
    )
    plan = build_plan(
        runs, source, historical_request_path, runtime, provenance,
        harness_path=harness_path, dotnet_path=dotnet_path,
    )
    plan["output_root"] = be.rel(output_root)
    brp.write_json_exclusive(output_root / "plan.json", plan)
    journal_path = create_exclusive_file(output_root / "journal.jsonl")

    adapter_fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    dry_run = not run
    start = clock()
    records = []
    error = None
    try:
        for run_case in runs:
            record = execute_run(
                run_case,
                output_root,
                adapter_fixture=adapter_fixtures[run_case["fixture"]],
                historical_request_path=historical_request_path,
                runner=runner,
                clock=clock,
                start=start,
                dry_run=dry_run,
                provenance=provenance,
            )
            records.append(record)
            append_journal(journal_path, record)
    except BaseException as exc:  # noqa: BLE001 - the journal already holds attempts
        error = f"{type(exc).__name__}: {exc}"

    summary = build_summary(
        records, executed=run, output_root=output_root, plan=plan,
        source=source, runtime=runtime, error=error,
    )
    brp.write_json_exclusive(output_root / "summary.json", summary)
    return summary


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Bounded descriptive pick-ranking screen for the accepted pick fixtures"
    )
    group = parser.add_mutually_exclusive_group()
    group.add_argument(
        "--run", action="store_true", help="execute the 16 fixed runs (default: dry run)"
    )
    group.add_argument(
        "--dry-run", action="store_true", help="write the plan only; no subprocess (default)"
    )
    args = parser.parse_args(argv)

    try:
        summary = run_ranking_screen(run=args.run)
    except SourceValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    except RuntimeValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 4
    except FileExistsError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 3

    if args.run:
        print(
            f"attempts={summary['batch']['total_attempts']} verified={summary['verified']} "
            f"eligible_completed={summary['eligible_completed']} "
            f"tool_completed={summary['tool_completed']} "
            f"status_counts={summary['status_counts']}",
            flush=True,
        )
    else:
        print(
            f"attempts={summary['batch']['total_attempts']} executed=false "
            f"status_counts={summary['status_counts']}",
            flush=True,
        )
        print(json.dumps(summary["plan"], ensure_ascii=False, indent=1), flush=True)
    print(f"wrote {Path(summary['output_dir']) / 'summary.json'}", flush=True)
    if not args.run:
        return 0
    return 0 if summary["tool_completed"] else 1


if __name__ == "__main__":
    sys.exit(main())
