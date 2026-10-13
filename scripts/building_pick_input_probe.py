#!/usr/bin/env python3
"""Bounded input-fidelity probe for the two accepted pick fixtures.

The fixture builder (``scripts/building_pick_cases.py``) froze two decision
states and eight actions (two fixtures x skip + three candidate additions) and
the adapter exporter recorded the exact deck, upgrades and context the current
production ``BuildValue`` saw. This instrument answers a deliberately narrow
question: **when the offline harness consumes those eight frozen scenarios, does
it actually resolve the same deck, upgrades, seed, character, act, ascension,
encounter, HP, relics and potion slots?**

It is not a ranking or strength tool. It launches the real offline harness in a
one-turn (``--max-turns 1``) configuration that is intentionally truncated:

* every run uses the fixed production-shaped command
  (``--budget-ms 4000 --dop 1 --nodes 6000 --profile Medium``) but stops after a
  single turn, so no terminal win/loss is claimed and an incomplete one-turn
  fight is never recorded as a loss;
* the shared ``building_eval.classify`` result is kept as a *diagnostic only*;
  acceptance is input fidelity plus a clean process, not a combat outcome.

The accepted source is the verified fixture directory
``work/pick-fixtures/20260917T134019.577341Z`` (adapter output + generated
``cases/manifest.json``). Exact paths and SHA256 values are read from the
manifest, the adapter hash is re-checked, and every scenario file is re-hashed
before any process starts. A mismatch there aborts the batch with no launches.

Evidence comes from the harness-written
``evidence/generated-scenario.loadout.json`` (actual deck with
``currentUpgradeLevel``) and the resolved scenario, never inferred from the
input or the outer request.

Instrument rules:

* the default command is a **dry run** that launches nothing and writes only the
  plan; ``--run`` is required before any process starts;
* exactly eight single-shot launches, one subprocess at a time, no auto-retry;
* each subprocess may run at most 60 s and the whole batch at most 240 s of
  monotonic time; every launch gets ``min(60, remaining)``, recomputed *after*
  input preparation, and a launch after the deadline is recorded
  ``not_started`` (never a battle result);
* each scenario is copied to a unique
  ``work/pick-input-probe/<UTC>/<action>/input.json`` with a new timestamp
  directory that is never overwritten; the accepted/historical assets are only
  read;
* the outer request is cloned from the historical request through the shared
  ``building_repeat_probe.sanitize_request`` helper and then its seed/character
  are overridden to the frozen scenario (the helper hardcodes PM001);
* every attempt is journaled, the summary is written even on failure, and it
  reports ``not_started`` / ``timeout`` / ``error`` / ``missing`` / ``mismatch``
  counts with the exact mismatch reasons;
* the full child environment is passed through but never written to an artifact,
  and no inherited secret is ever logged.

Usage:
    python scripts/building_pick_input_probe.py          # dry run, plan only
    python scripts/building_pick_input_probe.py --run    # execute the eight runs
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import building_eval as be
import building_repeat_probe as brp

ROOT = brp.ROOT

# Fixed repository paths, reused read-only from the repeat probe module. Nothing
# here mutates a module global.
HARNESS = brp.HARNESS
DOTNET = brp.DOTNET
CWD = brp.CWD
RESULT_NAME = brp.RESULT_NAME

# The accepted, independently verified fixture directory (Astra acceptance).
SOURCE_DIR = ROOT / "work" / "pick-fixtures" / "20260917T134019.577341Z"
# Historical outer request used only as the sanitized template.
HISTORICAL_DIR = brp.SOURCE_DIR
OUTPUT_ROOT = ROOT / "work" / "pick-input-probe"

ADAPTER_NAME = "adapter-output.json"
MANIFEST_NAME = "manifest.json"
ADAPTER_TOOL = "BuildingDecisionProbe"
EXPECTED_FIXTURES = 2

CHARACTER = "SILENT"
ASCENSION = 10
PLAYER_HP = 70

# Fixed harness flags shared by every run. This mirrors the successful budget
# probe command, with the deliberate one-turn truncation.
PROFILE = "Medium"
NODES = "6000"
BUDGET_MS = "4000"
SEARCH_TIMEOUT_MS = "60000"
DOP = "1"
PLAY_PATH = "gameaction"
MAX_TURNS = "1"
MILESTONE = "M2"

MAX_LAUNCHES = 8
PER_RUN_TIMEOUT_SECONDS = 60
BATCH_DEADLINE_SECONDS = 240

# Probe-local status vocabulary. ``outcome`` keeps the shared building_eval
# classification as a diagnostic; the probe status is about input fidelity and
# process health only.
STATUS_VERIFIED = "verified"
STATUS_MISMATCH = "mismatch"
STATUS_MISSING = "missing"
STATUS_ERROR = be.STATUS_ERROR
STATUS_TIMEOUT = be.STATUS_TIMEOUT
STATUS_NOT_STARTED = "not_started"
STATUS_DRY_RUN = "dry_run"

STATUS_ORDER = (
    STATUS_VERIFIED,
    STATUS_MISMATCH,
    STATUS_MISSING,
    STATUS_ERROR,
    STATUS_TIMEOUT,
    STATUS_NOT_STARTED,
    STATUS_DRY_RUN,
)

ENV_OVERRIDES = dict(brp.ENV_OVERRIDES)

NOTES = [
    "One-turn (--max-turns 1) combat is intentionally truncated: this probe "
    "verifies input fidelity and process health, never a terminal fight result.",
    "An incomplete one-turn fight is never recorded as a loss; the shared "
    "building_eval classification is a diagnostic only.",
    "Acceptance is input fidelity plus a clean process. This instrument does not "
    "rank candidates, estimate strength, compare budgets or establish repeatability.",
    "One 4000ms/dop1/node6000 run per action cannot separate run-to-run noise; the "
    "probe deliberately sets no study gate.",
]

LIMITATIONS = [
    "The two fixtures are PROXY states, not observed reward screens; the act0 "
    "starter encounter is a fixture proxy.",
    "Relics and potions are empty by design (an ablation); this changes real-run "
    "meaning and is asserted only as fixture fidelity.",
    "A one-turn run cannot establish budget behaviour, repeatability or candidate "
    "ranking; the truncated fight is not evidence about the full fight.",
    "The batch deadline is monotonic wall clock; a slow host can mark later actions "
    "not_started, which is recorded rather than hidden.",
    "Evidence is only as trustworthy as the harness build and the accepted fixture "
    "directory it consumes.",
]


class SourceValidationError(ValueError):
    """Raised when the accepted source, manifest or historical request is invalid."""


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def default_output_root(now=None) -> Path:
    """A fresh UTC timestamp directory with microsecond resolution."""
    moment = now if now is not None else datetime.now(timezone.utc)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    stamp = moment.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    return OUTPUT_ROOT / stamp


def compute_timeout(elapsed_seconds: float) -> float:
    """Per-launch cap: at most 60 s and never past the 240 s batch deadline."""
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


def _resolve_manifest_path(value) -> Path:
    if not isinstance(value, str) or not value:
        raise SourceValidationError(f"manifest path must be a non-empty string, got {value!r}")
    path = Path(value)
    return path if path.is_absolute() else (ROOT / path)


def _within(path: Path, root: Path) -> bool:
    try:
        Path(path).resolve().relative_to(Path(root).resolve())
        return True
    except ValueError:
        return False


def validate_adapter(adapter) -> list:
    """Validate the accepted adapter shape; raise SourceValidationError otherwise."""
    if not isinstance(adapter, dict):
        raise SourceValidationError("adapter output is not a JSON object")
    if adapter.get("tool") != ADAPTER_TOOL:
        raise SourceValidationError(
            f"adapter tool must be {ADAPTER_TOOL}, got {adapter.get('tool')!r}"
        )
    fixtures = adapter.get("fixtures")
    if not isinstance(fixtures, list) or len(fixtures) != EXPECTED_FIXTURES:
        raise SourceValidationError(f"adapter must carry exactly {EXPECTED_FIXTURES} fixtures")
    seen = set()
    for fixture in fixtures:
        if not isinstance(fixture, dict):
            raise SourceValidationError("adapter fixture must be an object")
        fixture_id = fixture.get("id")
        if not isinstance(fixture_id, str) or not fixture_id:
            raise SourceValidationError(f"adapter fixture id invalid: {fixture_id!r}")
        if fixture_id in seen:
            raise SourceValidationError(f"duplicate adapter fixture id {fixture_id!r}")
        seen.add(fixture_id)
        deck = fixture.get("deck")
        if not isinstance(deck, list) or not deck:
            raise SourceValidationError(f"fixture {fixture_id} deck must be a non-empty list")
        for entry in deck:
            if not isinstance(entry, dict) or not isinstance(entry.get("id"), str):
                raise SourceValidationError(f"fixture {fixture_id} deck entry is invalid")
            level = entry.get("upgrade")
            if not isinstance(level, int) or isinstance(level, bool) or level < 0:
                raise SourceValidationError(f"fixture {fixture_id} deck upgrade is invalid")
        candidates = fixture.get("candidates")
        if not isinstance(candidates, list) or not candidates:
            raise SourceValidationError(f"fixture {fixture_id} must offer candidates")
        for candidate in candidates:
            if not isinstance(candidate, dict) or not isinstance(candidate.get("id"), str):
                raise SourceValidationError(f"fixture {fixture_id} candidate is invalid")
    return fixtures


def load_source(source_dir) -> dict:
    """Read and cross-check the accepted adapter output and cases manifest."""
    source_dir = Path(source_dir)
    adapter_path = source_dir / ADAPTER_NAME
    manifest_path = source_dir / "cases" / MANIFEST_NAME
    missing = []
    if not adapter_path.exists():
        missing.append(f"adapter output absent: {be.rel(adapter_path)}")
    if not manifest_path.exists():
        missing.append(f"manifest absent: {be.rel(manifest_path)}")
    if missing:
        raise SourceValidationError("; ".join(missing))

    try:
        adapter = json.loads(adapter_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise SourceValidationError(f"adapter output unreadable: {exc}") from exc
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise SourceValidationError(f"manifest unreadable: {exc}") from exc
    if not isinstance(manifest, dict):
        raise SourceValidationError("manifest is not a JSON object")

    validate_adapter(adapter)

    adapter_sha = be.sha256_file(adapter_path)
    manifest_sha = be.sha256_file(manifest_path)
    source_block = manifest.get("source")
    if not isinstance(source_block, dict):
        raise SourceValidationError("manifest has no source object")
    declared_sha = source_block.get("adapterSha256")
    if declared_sha != adapter_sha:
        raise SourceValidationError(
            f"adapter sha256 {adapter_sha} != manifest.source.adapterSha256 {declared_sha}"
        )
    declared_adapter = _resolve_manifest_path(source_block.get("adapterJson"))
    if declared_adapter.resolve() != adapter_path.resolve():
        raise SourceValidationError(
            f"manifest source.adapterJson {source_block.get('adapterJson')!r} does not "
            f"resolve to {be.rel(adapter_path)}"
        )

    return {
        "source_dir": source_dir,
        "adapter_path": adapter_path,
        "manifest_path": manifest_path,
        "adapter": adapter,
        "manifest": manifest,
        "adapter_sha256": adapter_sha,
        "manifest_sha256": manifest_sha,
    }


def build_specs(source) -> list:
    """Expand the manifest into the fixed eight actions, re-hashing every scenario."""
    adapter = source["adapter"]
    manifest = source["manifest"]
    by_id = {fixture["id"]: fixture for fixture in adapter["fixtures"]}

    manifest_fixtures = manifest.get("fixtures")
    if not isinstance(manifest_fixtures, list) or not manifest_fixtures:
        raise SourceValidationError("manifest has no fixtures list")

    specs = []
    for manifest_fixture in manifest_fixtures:
        if not isinstance(manifest_fixture, dict):
            raise SourceValidationError("manifest fixture is not an object")
        fixture_id = manifest_fixture.get("id")
        adapter_fixture = by_id.get(fixture_id)
        if adapter_fixture is None:
            raise SourceValidationError(f"manifest fixture {fixture_id!r} absent from adapter")
        context = manifest_fixture.get("context")
        if not isinstance(context, dict):
            raise SourceValidationError(f"fixture {fixture_id} has no manifest context")
        for key in ("seed", "characterId", "ascension", "actIndex", "encounterId"):
            if adapter_fixture.get(key) != context.get(key):
                raise SourceValidationError(
                    f"fixture {fixture_id} adapter {key}={adapter_fixture.get(key)!r} "
                    f"!= manifest {context.get(key)!r}"
                )
        if context.get("characterId") != CHARACTER:
            raise SourceValidationError(f"fixture {fixture_id} character must be {CHARACTER}")
        if context.get("ascension") != ASCENSION:
            raise SourceValidationError(f"fixture {fixture_id} ascension must be {ASCENSION}")

        actions = manifest_fixture.get("actions")
        if not isinstance(actions, list) or not actions:
            raise SourceValidationError(f"fixture {fixture_id} has no actions")
        for action in actions:
            if not isinstance(action, dict):
                raise SourceValidationError(f"fixture {fixture_id} action is not an object")
            kind = action.get("kind")
            if kind == "skip":
                candidate = None
                label = "skip"
            elif kind == "add":
                candidate = action.get("candidate")
                if not isinstance(candidate, str) or not candidate:
                    raise SourceValidationError(f"fixture {fixture_id} add action has no candidate")
                label = f"add-{candidate}"
            else:
                raise SourceValidationError(f"fixture {fixture_id} action kind {kind!r} unsupported")

            scenario_path = _resolve_manifest_path(action.get("file"))
            if not _within(scenario_path, source["source_dir"]):
                raise SourceValidationError(
                    f"fixture {fixture_id} action {label} file escapes accepted source: "
                    f"{action.get('file')!r}"
                )
            if not scenario_path.exists():
                raise SourceValidationError(
                    f"fixture {fixture_id} action {label} file absent: {be.rel(scenario_path)}"
                )
            actual_sha = be.sha256_file(scenario_path)
            declared_sha = action.get("sha256")
            if not declared_sha or declared_sha != actual_sha:
                raise SourceValidationError(
                    f"fixture {fixture_id} action {label} sha256 {actual_sha} "
                    f"!= manifest {declared_sha}"
                )
            specs.append(
                {
                    "name": f"{fixture_id}-{label}",
                    "fixture": fixture_id,
                    "action": label,
                    "candidate": candidate,
                    "seed": context["seed"],
                    "character": context["characterId"],
                    "ascension": context["ascension"],
                    "act_index": context["actIndex"],
                    "encounter": context["encounterId"],
                    "scenario_path": scenario_path,
                    "scenario_source": be.rel(scenario_path),
                    "scenario_sha256": actual_sha,
                }
            )

    if len(specs) != MAX_LAUNCHES:
        raise SourceValidationError(f"expected {MAX_LAUNCHES} actions, found {len(specs)}")
    expected_actions = {
        (fixture['id'], candidate)
        for fixture in adapter['fixtures']
        for candidate in [None] + [card['id'] for card in fixture['candidates']]
    }
    if {(spec['fixture'], spec['candidate']) for spec in specs} != expected_actions or len({spec['name'] for spec in specs}) != MAX_LAUNCHES:
        raise SourceValidationError('actions must uniquely cover skip and every candidate for both fixtures')
    return specs


def runtime_dll_checks(adapter) -> dict:
    """Record the adapter-declared runtime hashes and verify present build files."""
    runtime = adapter.get("runtime") if isinstance(adapter, dict) else None
    runtime = runtime if isinstance(runtime, dict) else {}
    checks = {}
    for key in ("probeAssembly", "coopBotsAssembly", "sts2", "godotSharp", "harmony"):
        entry = runtime.get(key)
        entry = entry if isinstance(entry, dict) else {}
        declared = entry.get("sha256")
        path = entry.get("path")
        if path and declared and Path(path).exists():
            actual = be.sha256_file(Path(path))
            checks[key] = {"declared": declared, "actual": actual, "ok": actual == declared}
        else:
            checks[key] = {"declared": declared, "actual": None, "ok": None}
    return checks


def collect_provenance(source, historical_request_path) -> dict:
    adapter = source["adapter"]
    runtime = adapter.get("runtime") if isinstance(adapter, dict) else None
    runtime = runtime if isinstance(runtime, dict) else {}
    dlls = {}
    for key in ("probeAssembly", "coopBotsAssembly", "sts2", "godotSharp", "harmony"):
        entry = runtime.get(key)
        entry = entry if isinstance(entry, dict) else {}
        dlls[key] = {"path": entry.get("path"), "sha256": entry.get("sha256")}
    return {
        "driver": {"path": be.rel(Path(__file__)), "sha256": brp.sha256_file(Path(__file__))},
        "classifier": {"path": be.rel(Path(be.__file__)), "sha256": brp.sha256_file(Path(be.__file__))},
        "repeat_helpers": {
            "path": be.rel(Path(brp.__file__)),
            "sha256": brp.sha256_file(Path(brp.__file__)),
        },
        "harness": {"path": be.rel(HARNESS), "sha256": brp.sha256_file(HARNESS)},
        "runtime": {"path": be.rel(DOTNET), "sha256": brp.sha256_file(DOTNET)},
        "adapter": {
            "path": be.rel(source["adapter_path"]),
            "sha256": source["adapter_sha256"],
        },
        "manifest": {
            "path": be.rel(source["manifest_path"]),
            "sha256": source["manifest_sha256"],
        },
        "historical_request": {
            "path": be.rel(historical_request_path),
            "sha256": brp.sha256_file(historical_request_path),
        },
        "runtime_dlls": dlls,
    }


def build_command(request_path, run_dir, name) -> list:
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
        "--label", name,
        "--out", str(run_dir),
    ]


def build_plan(output_root, source, historical_request_path, specs, provenance) -> dict:
    output_root = Path(output_root)
    runs = []
    for spec in specs:
        run_dir = output_root / spec["name"]
        runs.append(
            {
                "name": spec["name"],
                "fixture": spec["fixture"],
                "action": spec["action"],
                "candidate": spec["candidate"],
                "seed": spec["seed"],
                "character": spec["character"],
                "ascension": spec["ascension"],
                "act_index": spec["act_index"],
                "encounter": spec["encounter"],
                "scenario_source": spec["scenario_source"],
                "scenario_sha256": spec["scenario_sha256"],
                "dir": be.rel(run_dir),
                "input": be.rel(run_dir / "input.json"),
                "request": be.rel(run_dir / "outer-request.json"),
                "command": build_command(run_dir / "outer-request.json", run_dir, spec["name"]),
                "timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
            }
        )
    return {
        "generated_at": utc_now(),
        "tool": "building_pick_input_probe.py",
        "source": {
            "dir": be.rel(source["source_dir"]),
            "adapter": be.rel(source["adapter_path"]),
            "manifest": be.rel(source["manifest_path"]),
            "adapter_sha256": source["adapter_sha256"],
            "manifest_sha256": source["manifest_sha256"],
            "historical_request": be.rel(historical_request_path),
            "historical_request_sha256": brp.sha256_file(historical_request_path),
            "runtime_dll_checks": runtime_dll_checks(source["adapter"]),
        },
        "output_root": be.rel(output_root),
        "cwd": be.rel(CWD),
        "character": CHARACTER,
        "ascension": ASCENSION,
        "player_hp": PLAYER_HP,
        "profile": PROFILE,
        "nodes": NODES,
        "budget_ms": BUDGET_MS,
        "search_timeout_ms": SEARCH_TIMEOUT_MS,
        "dop": DOP,
        "max_turns": MAX_TURNS,
        "play_path": PLAY_PATH,
        "milestone": MILESTONE,
        "max_launches": MAX_LAUNCHES,
        "per_run_timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
        "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
        "env_overrides": dict(ENV_OVERRIDES),
        "runs": runs,
        "provenance": provenance,
    }


def write_run_inputs(run_dir, spec, historical_request_path):
    """Copy the frozen scenario byte-for-byte and write the sanitized outer request."""
    run_dir = Path(run_dir)
    run_dir.mkdir(parents=True, exist_ok=True)

    scenario_path = Path(spec["scenario_path"])
    raw = scenario_path.read_bytes()
    input_path = run_dir / "input.json"
    input_path.write_bytes(raw)

    request = json.loads(Path(historical_request_path).read_text(encoding="utf-8"))
    request = brp.sanitize_request(request, run_dir, spec["name"])
    # The helper hardcodes the PM001 fixture identity; restore the frozen scenario
    # identity so a starter seed is not silently rewritten to PM001.
    request["seed"] = spec["seed"]
    request["characterId"] = spec["character"]
    request_path = run_dir / "outer-request.json"
    request_path.write_text(
        json.dumps(request, ensure_ascii=False, indent=1), encoding="utf-8"
    )
    return input_path, request_path


def expected_deck(adapter_fixture, candidate) -> list:
    """Adapter-visible deck as (id, upgrade), plus one unupgraded candidate on add."""
    pairs = [(entry["id"], int(entry["upgrade"])) for entry in adapter_fixture["deck"]]
    if candidate is not None:
        pairs.append((candidate, 0))
    return pairs


def sha256_pairs(pairs):
    if pairs is None:
        return None
    canonical = sorted([list(pair) for pair in pairs])
    payload = json.dumps(canonical, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def _loadout_pairs(loadout, reasons):
    deck = loadout.get("deck")
    if not isinstance(deck, list):
        reasons.append("loadout deck absent or not a list")
        return None
    pairs = []
    for entry in deck:
        if not isinstance(entry, dict):
            reasons.append(f"loadout deck entry is not an object: {entry!r}")
            return None
        card_id = entry.get("id")
        level = entry.get("currentUpgradeLevel")
        if not isinstance(card_id, str) or not card_id:
            reasons.append(f"loadout deck entry has invalid id: {card_id!r}")
            return None
        if not isinstance(level, int) or isinstance(level, bool) or level < 0:
            reasons.append(
                f"loadout deck entry {card_id} has invalid currentUpgradeLevel: {level!r}"
            )
            return None
        pairs.append((card_id, level))
    return pairs


def verify_run_evidence(spec, adapter_fixture, payload, run_dir) -> dict:
    """Compare the actual loadout/resolved evidence against the accepted fixture."""
    run_dir = Path(run_dir)
    missing = []
    mismatch = []
    checks = {}
    expected_pairs = expected_deck(adapter_fixture, spec["candidate"])
    actual_pairs = None

    def check(name, expected, actual):
        ok = actual == expected
        checks[name] = {"expected": expected, "actual": actual, "ok": ok}
        if not ok:
            mismatch.append(f"{name}: {actual!r} != expected {expected!r}")

    resolved = payload.get("resolvedScenario")
    if not isinstance(resolved, dict):
        mismatch.append("resolvedScenario absent or not an object")
    else:
        check("resolvedScenario.Seed", spec["seed"], resolved.get("Seed"))
        check("resolvedScenario.CharacterId", spec["character"], resolved.get("CharacterId"))
        check("resolvedScenario.Ascension", spec["ascension"], resolved.get("Ascension"))
        check("resolvedScenario.ActIndex", spec["act_index"], resolved.get("ActIndex"))
        check("resolvedScenario.EncounterId", spec["encounter"], resolved.get("EncounterId"))

    resolved_path = run_dir / "evidence" / "generated-scenario.resolved.json"
    resolved_evidence, resolved_error = be.load_payload(resolved_path)
    resolved_sha = be.sha256_file(resolved_path) if resolved_path.exists() else None
    if isinstance(resolved_evidence, dict):
        check("evidenceResolved.seed", spec["seed"], resolved_evidence.get("seed"))
        check("evidenceResolved.characterId", spec["character"], resolved_evidence.get("characterId"))
        check("evidenceResolved.ascension", spec["ascension"], resolved_evidence.get("ascension"))
        check("evidenceResolved.actIndex", spec["act_index"], resolved_evidence.get("actIndex"))
        check("evidenceResolved.encounterId", spec["encounter"], resolved_evidence.get("encounterId"))
    elif resolved_error == "absent":
        missing.append("generated-scenario.resolved.json absent")
    elif resolved_error is not None:
        mismatch.append(f"evidence resolved malformed: {resolved_error}")
    else:
        mismatch.append("evidence resolved is not an object")

    loadout_path = run_dir / "evidence" / "generated-scenario.loadout.json"
    loadout, loadout_error = be.load_payload(loadout_path)
    loadout_sha = be.sha256_file(loadout_path) if loadout_path.exists() else None
    if loadout_error == "absent":
        missing.append("generated-scenario.loadout.json absent")
    elif loadout_error is not None:
        mismatch.append(f"generated-scenario.loadout.json malformed: {loadout_error}")
    elif not isinstance(loadout, dict):
        mismatch.append("generated-scenario.loadout.json is not a JSON object")
    else:
        check("loadout.seed", spec["seed"], loadout.get("seed"))
        check("loadout.characterId", spec["character"], loadout.get("characterId"))
        check("loadout.ascension", spec["ascension"], loadout.get("ascension"))
        check("loadout.actIndex", spec["act_index"], loadout.get("actIndex"))
        check("loadout.encounterId", spec["encounter"], loadout.get("encounterId"))
        check("loadout.playerHp", PLAYER_HP, loadout.get("playerHp"))
        check("loadout.playerMaxHp", PLAYER_HP, loadout.get("playerMaxHp"))
        check("loadout.relics", [], loadout.get("relics"))

        slots = loadout.get("potionSlots")
        if not isinstance(slots, list):
            checks["loadout.potionSlots"] = {"expected": "all null", "actual": slots, "ok": False}
            mismatch.append(f"loadout.potionSlots absent or not a list: {slots!r}")
        elif any(slot is not None for slot in slots):
            checks["loadout.potionSlots"] = {"expected": "all null", "actual": slots, "ok": False}
            mismatch.append(f"loadout.potionSlots has non-null entries: {slots!r}")
        else:
            checks["loadout.potionSlots"] = {"expected": "all null", "actual": slots, "ok": True}

        actual_pairs = _loadout_pairs(loadout, mismatch)
        if actual_pairs is not None:
            expected_counter = Counter(expected_pairs)
            actual_counter = Counter(actual_pairs)
            if expected_counter == actual_counter:
                checks["loadout.deck"] = {
                    "expectedSize": len(expected_pairs),
                    "actualSize": len(actual_pairs),
                    "ok": True,
                }
            else:
                checks["loadout.deck"] = {
                    "expectedSize": len(expected_pairs),
                    "actualSize": len(actual_pairs),
                    "ok": False,
                }
                missing_cards = expected_counter - actual_counter
                extra_cards = actual_counter - expected_counter
                mismatch.append(
                    "loadout deck differs (missing "
                    f"{sorted(missing_cards.items())}, extra {sorted(extra_cards.items())})"
                )

    if missing:
        status = STATUS_MISSING
    elif mismatch:
        status = STATUS_MISMATCH
    else:
        status = STATUS_VERIFIED

    return {
        "status": status,
        "reasons": mismatch + missing,
        "checks": checks,
        "loadout_sha256": loadout_sha,
        "resolved_sha256": resolved_sha,
        "expected_deck_sha256": sha256_pairs(expected_pairs),
        "actual_deck_sha256": sha256_pairs(actual_pairs) if actual_pairs is not None else None,
    }


def _new_record(spec, run_dir, provenance) -> dict:
    run_dir = Path(run_dir)
    return {
        "run": spec["name"],
        "fixture": spec.get("fixture"),
        "action": spec.get("action"),
        "candidate": spec.get("candidate"),
        "seed": spec.get("seed"),
        "character": spec.get("character"),
        "ascension": spec.get("ascension"),
        "act_index": spec.get("act_index"),
        "encounter": spec.get("encounter"),
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
        "checks": {},
        "mismatches": [],
        "expected_deck_sha256": None,
        "actual_deck_sha256": None,
        "loadout_sha256": None,
        "resolved_sha256": None,
        "harness_result_sha256": None,
        "input_sha256": None,
        "request_sha256": None,
        "stdout_path": be.rel(run_dir / "stdout.bin"),
        "stderr_path": be.rel(run_dir / "stderr.bin"),
        "raw_result_path": be.rel(run_dir / RESULT_NAME),
        "args": [],
        "provenance": dict(provenance),
    }


def _missing_record(run, output_root, provenance) -> dict:
    spec = {
        "name": run["name"],
        "fixture": run.get("fixture"),
        "action": run.get("action"),
        "candidate": run.get("candidate"),
        "seed": run.get("seed"),
        "character": run.get("character"),
        "ascension": run.get("ascension"),
        "act_index": run.get("act_index"),
        "encounter": run.get("encounter"),
    }
    record = _new_record(spec, Path(output_root) / run["name"], provenance)
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


def _finish_status(record, spec, adapter_fixture, payload, load_error, run_dir, launch_error) -> None:
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

    evidence = verify_run_evidence(spec, adapter_fixture, payload, run_dir)
    record["checks"] = evidence["checks"]
    record["mismatches"] = evidence["reasons"]
    record["expected_deck_sha256"] = evidence["expected_deck_sha256"]
    record["actual_deck_sha256"] = evidence["actual_deck_sha256"]
    record["loadout_sha256"] = evidence["loadout_sha256"]
    record["resolved_sha256"] = evidence["resolved_sha256"]
    record["status"] = evidence["status"]
    record["reason"] = "; ".join(evidence["reasons"]) if evidence["reasons"] else None


def _execute_case(spec, output_root, *, adapter_fixture, historical_request_path, runner,
                  clock, start, dry_run, provenance) -> dict:
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

    input_path, request_path = write_run_inputs(run_dir, spec, historical_request_path)
    record["input_sha256"] = be.sha256_file(input_path)
    record["request_sha256"] = be.sha256_file(request_path)
    command = build_command(request_path, run_dir, spec["name"])
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
        record["harness_result_sha256"] = be.sha256_file(raw_path)

    record["outcome"] = _outcome_for(exit_code, timed_out, payload, load_error, launch_error)
    _finish_status(record, spec, adapter_fixture, payload, load_error, run_dir, launch_error)
    return record


def execute_case(spec, output_root, **kwargs) -> dict:
    """Run one case; any preparation/decoding failure becomes an error record."""
    try:
        return _execute_case(spec, output_root, **kwargs)
    except Exception as exc:
        record = _new_record(spec, Path(output_root) / spec["name"], kwargs["provenance"])
        record["status"] = STATUS_ERROR
        record["reason"] = f"probe record failed: {type(exc).__name__}: {exc}"
        record["launched"] = None  # failure may have occurred before or after launch
        return record


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


def build_summary(records, *, executed, output_root, plan, error=None) -> dict:
    by_name = {record["run"]: record for record in records}
    ordered = []
    for run in plan["runs"]:
        ordered.append(
            by_name.get(run["name"]) or _missing_record(run, output_root, plan["provenance"])
        )

    status_counts = _status_counts(ordered)
    verified = sum(1 for record in ordered if record.get("status") == STATUS_VERIFIED)
    attempts_accounted = len(ordered) == MAX_LAUNCHES and len(records) == MAX_LAUNCHES
    input_fidelity_verified = bool(executed and attempts_accounted and verified == MAX_LAUNCHES)
    tool_completed = bool(attempts_accounted and error is None)

    return {
        "generated_at": utc_now(),
        "tool": "building_pick_input_probe.py",
        "executed": bool(executed),
        "output_dir": be.rel(Path(output_root)),
        "character": CHARACTER,
        "ascension": ASCENSION,
        "player_hp": PLAYER_HP,
        "batch": {
            "total_attempts": MAX_LAUNCHES,
            "attempts_accounted": attempts_accounted,
            "per_run_timeout_seconds": PER_RUN_TIMEOUT_SECONDS,
            "batch_deadline_seconds": BATCH_DEADLINE_SECONDS,
            "max_turns": MAX_TURNS,
            "budget_ms": BUDGET_MS,
            "dop": DOP,
            "nodes": NODES,
        },
        "status_counts": status_counts,
        "verified": verified,
        "input_fidelity_verified": input_fidelity_verified,
        "tool_completed": tool_completed,
        "outcome_diagnostics": _outcome_counts(ordered),
        "one_turn_truncation": {
            "max_turns": MAX_TURNS,
            "intentional": True,
            "note": "One-turn truncation cannot establish budget behaviour, "
            "repeatability or candidate ranking; an incomplete fight is never a loss.",
        },
        "runs": ordered,
        "plan": plan,
        "error": error,
        "notes": list(NOTES),
        "limitations": list(LIMITATIONS),
    }


def _write_summary(output_root, records, *, executed, plan, provenance, error) -> dict:
    summary = build_summary(
        records, executed=executed, output_root=output_root, plan=plan, error=error
    )
    brp.write_json_exclusive(Path(output_root) / "summary.json", summary)
    return summary


def run_probe(*, run=False, output_root=None, source_dir=None, historical_dir=None,
              runner=None, clock=None, now=None) -> dict:
    """Run the fixed eight-action plan. ``run=False`` (the default) launches nothing."""
    runner = runner if runner is not None else subprocess.run
    clock = clock if clock is not None else time.monotonic
    source_dir = Path(source_dir) if source_dir is not None else SOURCE_DIR
    historical_dir = Path(historical_dir) if historical_dir is not None else HISTORICAL_DIR
    historical_request_path = historical_dir / "outer-request.json"
    output_root = Path(output_root) if output_root is not None else default_output_root(now)

    source = load_source(source_dir)
    specs = build_specs(source)
    if not historical_request_path.exists():
        raise SourceValidationError(
            f"historical outer request absent: {be.rel(historical_request_path)}"
        )
    provenance = collect_provenance(source, historical_request_path)

    # Refuse an existing output directory: the timestamp is expected to be unique.
    output_root.mkdir(parents=True, exist_ok=False)
    plan = build_plan(output_root, source, historical_request_path, specs, provenance)
    brp.write_json_exclusive(output_root / "plan.json", plan)
    journal_path = create_exclusive_file(output_root / "journal.jsonl")

    adapter_fixtures = {fixture["id"]: fixture for fixture in source["adapter"]["fixtures"]}
    dry_run = not run
    start = clock()
    records = []
    error = None
    try:
        for spec in specs:
            record = execute_case(
                spec,
                output_root,
                adapter_fixture=adapter_fixtures[spec["fixture"]],
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

    return _write_summary(
        output_root, records, executed=run, plan=plan, provenance=provenance, error=error
    )


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Bounded one-turn input-fidelity probe for the accepted pick fixtures"
    )
    group = parser.add_mutually_exclusive_group()
    group.add_argument(
        "--run", action="store_true", help="execute the eight fixed runs (default: dry run)"
    )
    group.add_argument(
        "--dry-run", action="store_true", help="write the plan only; no subprocess (default)"
    )
    args = parser.parse_args(argv)

    try:
        summary = run_probe(run=args.run)
    except SourceValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    except FileExistsError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 3

    if args.run:
        print(
            f"attempts={summary['batch']['total_attempts']} "
            f"verified={summary['verified']} "
            f"input_fidelity_verified={summary['input_fidelity_verified']} "
            f"tool_completed={summary['tool_completed']} "
            f"status_counts={summary['status_counts']}",
            flush=True,
        )
    else:
        print(json.dumps(summary["plan"], ensure_ascii=False, indent=1), flush=True)
    print(f"wrote {Path(summary['output_dir']) / 'summary.json'}", flush=True)
    if not args.run:
        return 0
    return 0 if summary["input_fidelity_verified"] else 1


if __name__ == "__main__":
    sys.exit(main())
