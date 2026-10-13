#!/usr/bin/env python3
"""Offline tests for scripts/building_pick_parallel_check.py.

Every test injects a fake ``subprocess.run``, a thread-safe fake clock, a
temporary accepted-source tree, a temporary harness runtime and a temporary,
raw-verified baseline summary. No battle is ever started and no real game file
is touched. The tests pin the instrument before it is allowed near the game:

* the plan is 4 sequential reference runs plus 16 concurrent replays (20 unique
  ``(phase, name)`` attempts) and only ``--dop``/paths/labels differ from the
  accepted baseline commands;
* the default command launches nothing and records 20 dry runs; an existing
  output directory is refused;
* inputs are regenerated through the accepted helper exactly like the baseline
  and are asserted byte-identical (sha256) to the baseline input; a corrupt or
  changed baseline raw file is rejected before any launch;
* the parallel phase is bounded to 16 workers and actually overlaps (barrier);
  the reference phase runs one at a time; every launch requests ``--dop 2``;
* the two-phase monotonic deadline is recomputed after preparation and pool
  queue wait, and a launch after the deadline is ``not_started``, never a fight;
* timeouts, nonzero exits, missing/malformed results and invalid loadouts are
  excluded and can never become losses or ranking evidence; an incomplete cell
  suppresses only its own fixture/budget comparison;
* the actual ``search-policy.json`` must show ``dop=2``/budget/nodes; missing
  CPU metrics are excluded rather than treated as zero; a runtime hash change
  between phases suppresses the historical throughput ratio;
* the module never mutates its own or the ranking-screen module's globals and
  never logs the child environment.

Run with:
    python -m unittest discover -s tests -p test_building_pick_parallel_check.py
"""

import hashlib
import io
import json
import subprocess
import sys
import tempfile
import threading
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import building_pick_parallel_check as b  # noqa: E402

CANDIDATES = ["BACKFLIP", "BLADE_DANCE", "DAGGER_SPRAY"]
CHARACTER = "SILENT"
ASCENSION = 10
SEED = "PICKSEL01"

FIXTURES = [
    {
        "id": "silent-starter-proxy",
        "seed": "PICKPROXY01",
        "act_index": 0,
        "encounter": "FUZZY_WURM_CRAWLER_WEAK",
        "deck": [("STRIKE_SILENT", 0), ("DEFEND_SILENT", 0), ("NEUTRALIZE", 0)],
        "best_reward": 0,
    },
    {
        "id": "silent-late-pm001-proxy",
        "seed": "PM001",
        "act_index": 2,
        "encounter": "SOUL_NEXUS_ELITE",
        "deck": [("DEFEND_SILENT", 0), ("BACKFLIP", 1), ("ASCENDERS_BANE", 0)],
        "best_reward": 2,
    },
]

REFERENCE_NAMES = [
    "silent-late-pm001-proxy-add-DAGGER_SPRAY-b4000",
    "silent-late-pm001-proxy-add-DAGGER_SPRAY-b20000",
    "silent-late-pm001-proxy-add-BACKFLIP-b4000",
    "silent-late-pm001-proxy-add-BACKFLIP-b20000",
]


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(Path(path).read_bytes())


def write_json(path: Path, data) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=1), encoding="utf-8")
    return path


def outcome_for(fixture, action, budget):
    if fixture == "silent-starter-proxy":
        hp = {"skip": 63, "BACKFLIP": 57, "BLADE_DANCE": 69, "DAGGER_SPRAY": 69}[action]
        return {"status": "win", "player_hp": hp, "max_hp": 70, "final_enemy_hp": 0, "turns": 6}
    if budget == 4000:
        table = {
            "skip": ("loss", 0, 41, 9),
            "BACKFLIP": ("win", 13, 0, 7),
            "BLADE_DANCE": ("loss", 0, 91, 7),
            "DAGGER_SPRAY": ("loss", 0, 168, 9),
        }
    else:
        table = {
            "skip": ("win", 41, 0, 8),
            "BACKFLIP": ("win", 55, 0, 7),
            "BLADE_DANCE": ("win", 23, 0, 5),
            "DAGGER_SPRAY": ("loss", 0, 113, 4),
        }
    status, hp, enemy_hp, turns = table[action]
    return {"status": status, "player_hp": hp, "max_hp": 70, "final_enemy_hp": enemy_hp, "turns": turns}


def build_runtime(root: Path):
    runtime = Path(root) / "runtime"
    game_dir = runtime / "gamedata"
    game_dir.mkdir(parents=True, exist_ok=True)
    sts2 = game_dir / "sts2.dll"
    sts2.write_bytes(b"fake-sts2")
    combat = runtime / "CombatSolver.dll"
    combat.write_bytes(b"fake-combat")
    harness = runtime / "OfflineSearchHarness.dll"
    harness.write_bytes(b"fake-harness")
    dotnet = runtime / "dotnet.exe"
    dotnet.write_bytes(b"fake-dotnet")
    config = runtime / "OfflineSearchHarness.runtimeconfig.json"
    write_json(
        config,
        {
            "runtimeOptions": {
                "tfm": "net9.0",
                "configProperties": {
                    "Sts2DataDir": str(game_dir),
                    "CombatSolverDll": str(combat),
                },
            }
        },
    )
    return {
        "dir": runtime,
        "config": config,
        "game_dir": game_dir,
        "sts2": sts2,
        "sts2_sha": sha256_file(sts2),
        "combat": combat,
        "harness": harness,
        "dotnet": dotnet,
    }


def build_source(root: Path, game_sha: str) -> Path:
    source = Path(root) / "source"
    cases = source / "cases"
    cases.mkdir(parents=True, exist_ok=True)

    adapter_fixtures = []
    manifest_fixtures = []
    for fixture in FIXTURES:
        fixture_id = fixture["id"]
        deck = [{"id": card_id, "upgrade": level} for card_id, level in fixture["deck"]]
        candidates = [
            {"id": candidate, "canonicalId": candidate, "cardPool": "character", "upgrade": 0,
             "score": 1.0, "finite": True, "reason": "fixture"}
            for candidate in CANDIDATES
        ]
        adapter_fixtures.append(
            {
                "id": fixture_id, "label": "PROXY", "proxy": True, "seed": fixture["seed"],
                "characterId": CHARACTER, "ascension": ASCENSION, "actIndex": fixture["act_index"],
                "encounterId": fixture["encounter"], "maxHp": 70, "currentHp": 70, "relics": [],
                "potions": [], "deck": deck, "candidates": candidates,
                "bestRewardAllowSkip": fixture["best_reward"],
            }
        )

        actions = []
        for label, candidate in [("skip", None)] + [(f"add-{c}", c) for c in CANDIDATES]:
            scenario = {
                "schemaVersion": 1, "seed": fixture["seed"], "characterId": CHARACTER,
                "ascension": ASCENSION, "actIndex": fixture["act_index"],
                "encounterId": fixture["encounter"], "playerCurrentHp": 70,
                "includeStartingDeck": True, "includeStartingRelics": False,
                "includeAscendersBane": True,
                "characterCards": {"count": 0, "ids": [], "upgradeLevelsPerCard": []},
                "colorlessCards": {"count": 0, "ids": [], "upgradeLevelsPerCard": []},
                "relics": {"count": 0}, "potions": {"count": 0},
                "fixedSearchBudget": False, "mode": "Deploy",
            }
            scenario_path = write_json(cases / fixture_id / f"{label}.json", scenario)
            action = {
                "kind": "skip" if candidate is None else "add",
                "file": str(scenario_path.resolve()),
                "sha256": sha256_file(scenario_path),
            }
            if candidate is not None:
                action["candidate"] = candidate
            actions.append(action)

        manifest_fixtures.append(
            {
                "id": fixture_id, "label": "PROXY", "proxy": True,
                "context": {
                    "seed": fixture["seed"], "characterId": CHARACTER, "ascension": ASCENSION,
                    "actIndex": fixture["act_index"], "encounterId": fixture["encounter"],
                },
                "actions": actions,
            }
        )

    adapter = {
        "schemaVersion": 1, "tool": "BuildingDecisionProbe",
        "runtime": {"sts2": {"path": "fake", "sha256": game_sha}},
        "fixtures": adapter_fixtures,
    }
    adapter_path = write_json(source / "adapter-output.json", adapter)
    manifest = {
        "schemaVersion": 1, "tool": "building_pick_cases.py",
        "source": {"adapterJson": str(adapter_path.resolve()), "adapterSha256": sha256_file(adapter_path)},
        "fixtures": manifest_fixtures,
    }
    write_json(cases / "manifest.json", manifest)
    return source


def write_historical(root: Path) -> Path:
    historical = Path(root) / "hist"
    write_json(
        historical / "outer-request.json",
        {
            "schemaVersion": 1, "runId": "OLD", "scenarioId": "OLD-ID", "characterId": CHARACTER,
            "encounterId": "AEONGLASS_BOSS", "modifierIds": ["x"], "seed": "PM001",
            "generatedScenarioPath": "OLD-PATH", "evidenceDirectory": "OLD-EVIDENCE",
            "holdAfterInitialSearch": True, "checkpointArchivePath": "old", "runSnapshotPath": "old",
            "replayStatePath": "old", "nativeStatePath": "old", "replayPolicyOverridePath": "old",
            "cards": ["x"], "runCards": ["x"], "relics": ["x"], "combatRelics": ["x"], "potions": ["x"],
            "powers": ["x"], "ascension": ASCENSION, "actIndexForTest": 2,
        },
    )
    return historical


class ThreadSafeFakeClock:
    """Thread-safe queued clock; repeats the last value once exhausted."""

    def __init__(self, values):
        self.values = list(values)
        self.last = self.values[-1] if self.values else 0.0
        self.lock = threading.Lock()

    def __call__(self, *args, **kwargs):
        with self.lock:
            if self.values:
                self.last = self.values.pop(0)
            return self.last


class Concurrency:
    def __init__(self):
        self.lock = threading.Lock()
        self.state = {
            "reference": {"active": 0, "max": 0},
            "parallel": {"active": 0, "max": 0},
        }
        self.calls = []

    def enter(self, phase, label, command):
        with self.lock:
            bucket = self.state[phase]
            bucket["active"] += 1
            bucket["max"] = max(bucket["max"], bucket["active"])
            self.calls.append({"phase": phase, "label": label, "active": bucket["active"]})

    def leave(self, phase):
        with self.lock:
            self.state[phase]["active"] -= 1


def _label(command):
    return command[command.index("--label") + 1]


def _out_dir(command):
    return Path(command[command.index("--out") + 1])


def harness_payload(run, spec, *, omit_cpu=False):
    status = spec["status"]
    hp = spec["player_hp"]
    enemy_hp = spec["final_enemy_hp"]
    turns = spec["turns"]
    if status == "win":
        enemies = "[]"
    else:
        enemies = f"[SOUL_NEXUS#1:{enemy_hp or 10}/254@MOVE]"
    payload = {
        "resolvedScenario": {
            "SchemaVersion": 1, "Seed": run["seed"], "CharacterId": run["character"],
            "Ascension": run["ascension"], "ActIndex": run["act_index"],
            "EncounterId": run["encounter"],
        },
        "fightTruncated": None,
        "fightWallMs": 1234,
        "wallSeconds": 2.5,
        "timeBoundaryObserved": False,
        "solverMetrics": {"ElapsedMilliseconds": 1200.0},
        "afterTurns": (
            f"turn={turns} round=1 phase=Play side=Player energy=3 hp={hp}/70 "
            f"hand=[] draw=1 discard=0 relics=[] enemies={enemies} total_floor=1 act_floor=0"
        ),
    }
    if not omit_cpu:
        payload["totalCpuMilliseconds"] = 1234.5
    return payload


def loadout_payload(run, adapter_fixture):
    deck = [
        {"id": entry["id"], "currentUpgradeLevel": entry["upgrade"]}
        for entry in adapter_fixture["deck"]
    ]
    if run["candidate"] is not None:
        deck.append({"id": run["candidate"], "currentUpgradeLevel": 0})
    return {
        "schemaVersion": 1, "seed": run["seed"], "characterId": run["character"],
        "ascension": run["ascension"], "actIndex": run["act_index"],
        "encounterId": run["encounter"], "playerHp": 70, "playerMaxHp": 70, "relics": [],
        "potionSlots": [None, None], "deck": deck,
    }


def make_fake_runner(runs, fixtures, *, concurrency=None, outcomes=None, returncode=0,
                     timeout_labels=(), drop_result=(), drop_policy=(), drop_loadout=(),
                     malformed_result=(), mutate=None, omit_cpu=(), barrier=None):
    by_name = {run["name"]: run for run in runs}
    outcomes = outcomes or {}

    def run(command, **kwargs):
        label = _label(command)
        out_dir = _out_dir(command)
        phase = "reference" if "reference" in out_dir.parts else "parallel"
        case = by_name[label]
        if concurrency is not None:
            concurrency.enter(phase, label, command)
        try:
            if barrier is not None and phase == "parallel":
                try:
                    barrier.wait(timeout=10)
                except threading.BrokenBarrierError:
                    pass
            if label in timeout_labels:
                raise subprocess.TimeoutExpired(
                    command, kwargs.get("timeout"), output=b"partial-out", stderr=b"partial-err"
                )
            out_dir.mkdir(parents=True, exist_ok=True)
            spec = outcomes.get(label) or outcome_for(
                case["fixture"], b._action_key(case), case["budget_ms"]
            )

            if label in malformed_result:
                (out_dir / "harness-result.json").write_text("not json", encoding="utf-8")
            elif label not in drop_result:
                write_json(
                    out_dir / "harness-result.json",
                    harness_payload(case, spec, omit_cpu=label in omit_cpu),
                )

            if label not in drop_policy:
                write_json(
                    out_dir / "search-policy.json",
                    {
                        "profile": {
                            "maxExpandedNodes": 6000,
                            "softTimeBudgetMilliseconds": case["budget_ms"],
                        },
                        "maxDegreeOfParallelism": 2,
                    },
                )

            if label not in drop_loadout:
                loadout = loadout_payload(case, fixtures[case["fixture"]])
                if mutate is not None:
                    mutate(label, loadout)
                evidence = out_dir / "evidence"
                evidence.mkdir(parents=True, exist_ok=True)
                write_json(evidence / "generated-scenario.loadout.json", loadout)
                write_json(
                    evidence / "generated-scenario.resolved.json",
                    {
                        "seed": case["seed"], "characterId": case["character"],
                        "ascension": case["ascension"], "actIndex": case["act_index"],
                        "encounterId": case["encounter"],
                    },
                )

            return subprocess.CompletedProcess(
                command, returncode, stdout=b"stdout-bytes", stderr=b"stderr-bytes"
            )
        finally:
            if concurrency is not None:
                concurrency.leave(phase)

    return run


class FixtureCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.runtime = build_runtime(self.tmp)
        self.source_dir = build_source(self.tmp, self.runtime["sts2_sha"])
        self.historical = write_historical(self.tmp)
        _, self.ordered = b.ranking.prepare_source(self.source_dir)
        reference, parallel = b.build_plan_runs(
            self.ordered, self.tmp / "planroot",
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
        )
        self.all_runs = reference + parallel
        self.fixtures = {
            fixture["id"]: fixture for fixture in json.loads(
                (self.source_dir / "adapter-output.json").read_text(encoding="utf-8")
            )["fixtures"]
        }
        self.baseline_dir = self._build_baseline()
        self._counter = 0
        self.baseline_names = [f"{spec['name']}-b{spec['budget_ms']}" for spec in self.ordered]

    def tearDown(self):
        self._tmp.cleanup()

    def _out(self):
        self._counter += 1
        return self.tmp / f"out-{self._counter}"

    def _build_baseline(self) -> Path:
        baseline = self.tmp / "baseline"
        baseline.mkdir(parents=True, exist_ok=True)
        historical_request = self.historical / "outer-request.json"
        runs = []
        for spec in self.ordered:
            budget = spec["budget_ms"]
            name = f"{spec['name']}-b{budget}"
            run_dir = baseline / name
            input_path, request_path = b.ranking.write_run_inputs(run_dir, spec, historical_request)
            (run_dir / "stdout.bin").write_bytes(b"baseline-out")
            (run_dir / "stderr.bin").write_bytes(b"baseline-err")
            (run_dir / "harness-result.json").write_bytes(b'{"baseline": true}')
            evidence = run_dir / "evidence"
            evidence.mkdir(parents=True, exist_ok=True)
            (evidence / "generated-scenario.loadout.json").write_bytes(b'{"baseline": "loadout"}')
            (evidence / "generated-scenario.resolved.json").write_bytes(b'{"baseline": "resolved"}')
            outcome = outcome_for(spec["fixture"], b._action_key(spec), budget)
            runs.append({
                "run": name,
                "fixture": spec["fixture"],
                "action": spec["action"],
                "candidate": spec["candidate"],
                "budget_ms": budget,
                "seed": SEED,
                "source_seed": spec["source_seed"],
                "status": b.STATUS_VERIFIED,
                "outcome": outcome,
                "player_hp": outcome["player_hp"],
                "max_hp": outcome["max_hp"],
                "enemy_hp": outcome["final_enemy_hp"],
                "turns": outcome["turns"],
                "runtime_seconds": b.HISTORICAL_SEQUENTIAL_SECONDS / 16,
                "raw_hashes": {
                    "input_sha256": sha256_file(input_path),
                    "request_sha256": sha256_file(request_path),
                    "stdout_sha256": sha256_file(run_dir / "stdout.bin"),
                    "stderr_sha256": sha256_file(run_dir / "stderr.bin"),
                    "harness_result_sha256": sha256_file(run_dir / "harness-result.json"),
                    "loadout_sha256": sha256_file(evidence / "generated-scenario.loadout.json"),
                    "resolved_sha256": sha256_file(evidence / "generated-scenario.resolved.json"),
                },
            })

        loaded = b.bpi.load_source(self.source_dir)
        ranking_block = {}
        for fixture in FIXTURES:
            fixture_id = fixture["id"]
            ranking_block[fixture_id] = {}
            for budget in b.BUDGETS:
                group = {}
                for action in b.ACTIONS:
                    candidate = None if action == "skip" else action
                    outcome = outcome_for(fixture_id, action, budget)
                    group[action] = {
                        "candidate": candidate,
                        "status": b.STATUS_VERIFIED,
                        "outcome": outcome,
                    }
                ranking_block[fixture_id][str(budget)] = b.ranking.build_ranking(
                    group, budget, b.ranking.resolve_build_value_action(
                        next(f for f in loaded["adapter"]["fixtures"] if f["id"] == fixture_id)
                    )
                )

        runtime = b.ranking.collect_runtime_provenance(
            self.runtime["config"], loaded["adapter"],
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"], env={},
        )
        summary = {
            "tool": "building_pick_ranking_screen.py",
            "executed": True,
            "seed": SEED,
            "batch": {"total_attempts": 16, "dop": "1", "nodes": "6000", "max_turns": "40"},
            "runtime": runtime,
            "ranking": ranking_block,
            "runs": runs,
            "plan": {
                "source": {
                    "adapter_sha256": loaded["adapter_sha256"],
                    "manifest_sha256": loaded["manifest_sha256"],
                    "historical_request_sha256": sha256_file(historical_request),
                },
                "runs": [],
            },
        }
        for run in runs:
            spec = next(s for s in self.ordered
                        if f"{s['name']}-b{s['budget_ms']}" == run["run"])
            summary["plan"]["runs"].append({
                "name": run["run"],
                "fixture": spec["fixture"],
                "action": spec["action"],
                "candidate": spec["candidate"],
                "budget_ms": spec["budget_ms"],
                "command": b.ranking.build_command(
                    baseline / run["run"] / "outer-request.json",
                    baseline / run["run"], run["run"], spec["budget_ms"],
                    harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
                ),
            })
        write_json(baseline / "summary.json", summary)
        return baseline

    def runner(self, **kwargs):
        return make_fake_runner(self.all_runs, self.fixtures, **kwargs)

    def run_check(self, *, out=None, run=True, **kwargs):
        kwargs.setdefault("source_dir", self.source_dir)
        kwargs.setdefault("baseline_dir", self.baseline_dir)
        kwargs.setdefault("historical_dir", self.historical)
        kwargs.setdefault("harness_path", self.runtime["harness"])
        kwargs.setdefault("dotnet_path", self.runtime["dotnet"])
        kwargs.setdefault("runtime_config_path", self.runtime["config"])
        kwargs.setdefault("runtime_env", {})
        kwargs.setdefault("output_root", out if out is not None else self._out())
        kwargs.setdefault("clock", ThreadSafeFakeClock([0]))
        return b.run_parallel_check(run=run, **kwargs)


class ConfigurationTests(unittest.TestCase):
    def test_fixed_limits_and_flags(self):
        self.assertEqual(b.MAX_LAUNCHES, 20)
        self.assertEqual(b.REFERENCE_LAUNCHES, 4)
        self.assertEqual(b.PARALLEL_LAUNCHES, 16)
        self.assertEqual(b.REFERENCE_TIMEOUT_SECONDS, 120)
        self.assertEqual(b.PARALLEL_TIMEOUT_SECONDS, 300)
        self.assertEqual(b.BATCH_DEADLINE_SECONDS, 900)
        self.assertEqual(b.PARALLELISM, 16)
        self.assertEqual(b.DOP, "2")
        self.assertEqual(b.NODES, "6000")
        self.assertEqual(b.SEED, "PICKSEL01")
        self.assertEqual(b.MAX_TURNS, "40")
        self.assertEqual(b.HISTORICAL_SEQUENTIAL_SECONDS, 450.391)
        self.assertEqual(b.REFERENCE_FIXTURE, "silent-late-pm001-proxy")
        self.assertEqual(set(b.REFERENCE_ACTIONS), {"BACKFLIP", "DAGGER_SPRAY"})

    def test_compute_timeout_caps_and_deadline(self):
        self.assertEqual(b.compute_timeout(0, 120), 120.0)
        self.assertEqual(b.compute_timeout(0, 300), 300.0)
        self.assertEqual(b.compute_timeout(700, 300), 200.0)
        self.assertEqual(b.compute_timeout(899.5, 300), 0.5)
        self.assertEqual(b.compute_timeout(900, 300), 0.0)
        self.assertEqual(b.compute_timeout(1000, 300), 0.0)

    def test_with_dop_replaces_only_dop_and_never_mutates(self):
        original = ["dotnet", "harness", "--dop", "1", "--label", "x"]
        changed = b.with_dop(original, "2")
        self.assertEqual(changed, ["dotnet", "harness", "--dop", "2", "--label", "x"])
        self.assertEqual(original, ["dotnet", "harness", "--dop", "1", "--label", "x"])
        with self.assertRaises(b.SourceValidationError):
            b.with_dop(["dotnet", "harness"], "2")

    def test_fresh_output_root_uses_utc_microseconds(self):
        first = b.default_output_root(
            now=datetime(2026, 9, 17, 12, 0, 0, 123456, tzinfo=timezone.utc)
        )
        second = b.default_output_root(
            now=datetime(2026, 9, 17, 12, 0, 0, 123457, tzinfo=timezone.utc)
        )
        self.assertNotEqual(first, second)
        self.assertEqual(first.parent, b.OUTPUT_ROOT)
        self.assertIn(".123456", first.name)


class DryRunTests(FixtureCase):
    def test_default_launches_nothing(self):
        out = self._out()
        with mock.patch.object(b.subprocess, "run") as run:
            summary = self.run_check(out=out, run=False)
        run.assert_not_called()
        self.assertFalse(summary["executed"])
        self.assertEqual(len(summary["runs"]), 20)
        self.assertEqual([r["status"] for r in summary["runs"]], [b.STATUS_DRY_RUN] * 20)
        self.assertTrue(all(not r["launched"] for r in summary["runs"]))
        self.assertTrue((out / "plan.json").exists())
        self.assertTrue((out / "summary.json").exists())
        self.assertTrue((out / "journal.jsonl").exists())
        self.assertTrue(summary["tool_completed"])
        self.assertFalse(summary["decision"]["comparison_available"])

    def test_plan_has_20_unique_phases_in_frozen_order(self):
        out = self._out()
        self.run_check(out=out, run=False)
        plan = json.loads((out / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual(plan["max_launches"], 20)
        self.assertEqual(plan["reference_launches"], 4)
        self.assertEqual(plan["parallel_launches"], 16)
        self.assertEqual(plan["dop"], "2")
        phases = [run["phase"] for run in plan["runs"]]
        self.assertEqual(phases, ["reference"] * 4 + ["parallel"] * 16)
        keys = {(run["phase"], run["name"]) for run in plan["runs"]}
        self.assertEqual(len(keys), 20)
        self.assertEqual(len({run["name"] for run in plan["runs"]}), 16)
        self.assertEqual([run["name"] for run in plan["runs"][:4]], REFERENCE_NAMES)
        for run in plan["runs"]:
            self.assertEqual(run["command"][run["command"].index("--dop") + 1], "2")

    def test_plan_command_differs_from_baseline_only_by_dop_and_paths(self):
        out = self._out()
        self.run_check(out=out, run=False)
        plan = json.loads((out / "plan.json").read_text(encoding="utf-8"))
        baseline = json.loads((self.baseline_dir / "summary.json").read_text(encoding="utf-8"))
        baseline_cmd = {
            run["name"]: run["command"] for run in baseline["plan"]["runs"]
        }
        for run in plan["runs"]:
            if run["phase"] != "parallel":
                continue
            reference = baseline_cmd[run["name"]]
            self.assertNotEqual(
                reference[reference.index("--dop") + 1], run["command"][run["command"].index("--dop") + 1]
            )
            diffs = [
                index for index, (left, right) in enumerate(zip(reference, run["command"]))
                if left != right
            ]
            allowed = {
                reference.index("--dop") + 1,
                reference.index("--request") + 1,
                reference.index("--out") + 1,
            }
            self.assertTrue(set(diffs) <= allowed, msg=f"{run['name']}: diffs {diffs}")

    def test_main_dry_run_launches_nothing_and_exits_zero(self):
        out = self._out()
        with mock.patch.object(b, "SOURCE_DIR", self.source_dir), \
                mock.patch.object(b, "BASELINE_DIR", self.baseline_dir), \
                mock.patch.object(b, "HISTORICAL_DIR", self.historical), \
                mock.patch.object(b, "HARNESS", self.runtime["harness"]), \
                mock.patch.object(b, "DOTNET", self.runtime["dotnet"]), \
                mock.patch.object(b, "default_output_root", return_value=out), \
                mock.patch.object(b.subprocess, "run") as run, \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = b.main([])
        run.assert_not_called()
        self.assertEqual(code, 0)
        self.assertTrue((out / "plan.json").exists())

    def test_refuses_an_existing_output_directory(self):
        out = self._out()
        out.mkdir(parents=True)
        with mock.patch.object(b.subprocess, "run") as run:
            with self.assertRaises(FileExistsError):
                self.run_check(out=out, run=True, runner=self.runner())
        run.assert_not_called()


class SeedAndInputTests(FixtureCase):
    def test_generated_input_matches_baseline_input(self):
        out = self._out()
        self.run_check(out=out, run=True, runner=self.runner())
        baseline_cells = b.baseline_cells(b.load_baseline(self.baseline_dir))
        plan = json.loads((out / "plan.json").read_text(encoding="utf-8"))
        for run in plan["runs"]:
            cell = baseline_cells[(run["fixture"], b._action_key(run), run["budget_ms"])]
            generated = out / run["phase"] / run["name"] / "input.json"
            self.assertEqual(
                sha256_file(generated), cell["raw_hashes"]["input_sha256"],
                msg=run["name"],
            )

    def test_prepare_rejects_nonmatching_baseline_input(self):
        run = self.plan_runs()[0]
        bogus = {b._cell_key(run): {"raw_hashes": {"input_sha256": "0" * 64}}}
        with self.assertRaises(b.SourceValidationError):
            b.prepare_run_input(run, self._out(), self.historical / "outer-request.json", bogus)

    def plan_runs(self):
        out = self._out()
        self.run_check(out=out, run=False)
        plan = json.loads((out / "plan.json").read_text(encoding="utf-8"))
        return plan["runs"]


class ExecutionTests(FixtureCase):
    def test_all_20_verified_and_policy_matches(self):
        concurrency = Concurrency()
        summary = self.run_check(
            run=True, runner=self.runner(concurrency=concurrency), clock=ThreadSafeFakeClock([0])
        )
        self.assertEqual(len(concurrency.calls), 20)
        self.assertEqual(summary["verified"], 20)
        self.assertEqual(summary["policy_verified"], 20)
        self.assertEqual(summary["outcome_counts"]["win"], 14)
        self.assertEqual(summary["outcome_counts"]["loss"], 6)
        self.assertEqual(summary["batch"]["attempts_accounted"], True)
        self.assertEqual(concurrency.state["reference"]["max"], 1)
        self.assertLessEqual(concurrency.state["parallel"]["max"], 16)
        first_ref = [c["label"] for c in concurrency.calls[:4]]
        self.assertEqual(first_ref, REFERENCE_NAMES)
        self.assertTrue(all(c["phase"] == "parallel" for c in concurrency.calls[4:]))

    def test_search_policy_dop_budget_nodes_verified(self):
        summary = self.run_check(run=True, runner=self.runner(), clock=ThreadSafeFakeClock([0]))
        for record in summary["runs"]:
            self.assertIsInstance(record["policy"], dict)
            self.assertTrue(record["policy"]["ok"], msg=record["run"])
            self.assertEqual(record["policy"]["dop"], 2)
            self.assertEqual(record["policy"]["nodes"], 6000)
            self.assertEqual(record["policy"]["budget_ms"], record["budget_ms"])

    def test_journal_has_one_line_per_attempt(self):
        out = self._out()
        self.run_check(out=out, run=True, runner=self.runner(), clock=ThreadSafeFakeClock([0]))
        lines = [
            line for line in (out / "journal.jsonl").read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        self.assertEqual(len(lines), 20)
        keys = {(json.loads(line)["phase"], json.loads(line)["run"]) for line in lines}
        self.assertEqual(len(keys), 20)

    def test_parallel_commands_use_dop2(self):
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            return self.runner()(command, **kwargs)

        self.run_check(run=True, runner=spy, clock=ThreadSafeFakeClock([0]))
        self.assertEqual(len(calls), 20)
        for command in calls:
            self.assertEqual(command[command.index("--dop") + 1], "2")
            self.assertEqual(command[command.index("--max-turns") + 1], "40")
            self.assertEqual(command[command.index("--nodes") + 1], "6000")

    def test_inherited_environment_is_never_logged(self):
        with mock.patch.object(
            b.brp, "build_env", return_value={"SECRET_SENTINEL": "leak-me"}
        ):
            summary = self.run_check(
                run=True, runner=self.runner(), clock=ThreadSafeFakeClock([0])
            )
        encoded = json.dumps(summary)
        self.assertNotIn("leak-me", encoded)
        self.assertNotIn("SECRET_SENTINEL", encoded)
        self.assertEqual(set(summary["plan"]["env_overrides"]), set(b.brp.ENV_OVERRIDES))


class OverlapTests(FixtureCase):
    def test_parallel_phase_overlaps_16_way(self):
        concurrency = Concurrency()
        barrier = threading.Barrier(16)
        summary = self.run_check(
            run=True,
            runner=self.runner(concurrency=concurrency, barrier=barrier),
            clock=ThreadSafeFakeClock([0]),
        )
        self.assertEqual(concurrency.state["parallel"]["max"], 16)
        self.assertEqual(summary["verified"], 20)


class DeadlineTests(FixtureCase):
    def test_launch_after_deadline_is_not_started(self):
        concurrency = Concurrency()
        summary = self.run_check(
            run=True,
            runner=self.runner(concurrency=concurrency),
            clock=ThreadSafeFakeClock([0, 1000]),
        )
        self.assertEqual(concurrency.calls, [])
        statuses = [r["status"] for r in summary["runs"]]
        self.assertEqual(statuses.count(b.STATUS_NOT_STARTED), 20)
        self.assertEqual(statuses.count(b.STATUS_VERIFIED), 0)
        self.assertEqual(summary["batch"]["attempts_accounted"], True)

    def test_timeout_is_recomputed_after_preparation(self):
        summary = self.run_check(
            run=True,
            runner=self.runner(),
            clock=ThreadSafeFakeClock([0, 0, 0, 850]),
        )
        first = summary["runs"][0]
        self.assertEqual(first["phase"], "reference")
        self.assertEqual(first["timeout_before_preparation_seconds"], 120.0)
        self.assertEqual(first["timeout_seconds"], 50.0)


class FailureTests(FixtureCase):
    def _record(self, summary, phase, name):
        return {(r["phase"], r["run"]): r for r in summary["runs"]}[(phase, name)]

    def test_timeout_is_excluded_and_never_a_loss(self):
        summary = self.run_check(
            run=True,
            runner=self.runner(timeout_labels=(REFERENCE_NAMES[0],)),
            clock=ThreadSafeFakeClock([0]),
        )
        record = self._record(summary, "reference", REFERENCE_NAMES[0])
        self.assertEqual(record["status"], b.STATUS_TIMEOUT)
        self.assertEqual(record["outcome"]["status"], b.be.STATUS_TIMEOUT)
        self.assertFalse(record["eligible"])

    def test_nonzero_exit_is_excluded(self):
        summary = self.run_check(
            run=True, runner=self.runner(returncode=1), clock=ThreadSafeFakeClock([0])
        )
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.STATUS_ERROR)
            self.assertFalse(record["eligible"])

    def test_missing_result_is_missing(self):
        summary = self.run_check(
            run=True,
            runner=self.runner(drop_result=(REFERENCE_NAMES[0],)),
            clock=ThreadSafeFakeClock([0]),
        )
        record = self._record(summary, "reference", REFERENCE_NAMES[0])
        self.assertEqual(record["status"], b.STATUS_MISSING)

    def test_malformed_result_is_mismatch(self):
        summary = self.run_check(
            run=True,
            runner=self.runner(malformed_result=(REFERENCE_NAMES[0],)),
            clock=ThreadSafeFakeClock([0]),
        )
        record = self._record(summary, "reference", REFERENCE_NAMES[0])
        self.assertEqual(record["status"], b.STATUS_MISMATCH)

    def test_invalid_loadout_is_mismatch(self):
        def mutate(label, loadout):
            if label == REFERENCE_NAMES[0]:
                loadout["seed"] = "WRONG"

        summary = self.run_check(
            run=True, runner=self.runner(mutate=mutate), clock=ThreadSafeFakeClock([0])
        )
        record = self._record(summary, "reference", REFERENCE_NAMES[0])
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("loadout.seed", record["reason"])

    def test_missing_policy_is_reported(self):
        summary = self.run_check(
            run=True,
            runner=self.runner(drop_policy=(REFERENCE_NAMES[0],)),
            clock=ThreadSafeFakeClock([0]),
        )
        record = self._record(summary, "reference", REFERENCE_NAMES[0])
        self.assertFalse(record["policy"]["ok"])
        # The reference label also exists in the parallel phase, so both miss it.
        self.assertEqual(summary["policy_verified"], 18)
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertFalse(record["eligible"])
        self.assertFalse(summary["decision"]["comparison_available"])


class BaselineTests(FixtureCase):
    def test_corrupt_baseline_raw_file_is_rejected_before_launch(self):
        input_path = self.baseline_dir / REFERENCE_NAMES[0] / "input.json"
        input_path.write_bytes(input_path.read_bytes() + b" ")
        with self.assertRaises(b.BaselineValidationError):
            b.load_baseline(self.baseline_dir)
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            return self.runner()(command, **kwargs)

        with self.assertRaises(b.BaselineValidationError):
            self.run_check(run=True, runner=spy)
        self.assertEqual(calls, [])

    def test_baseline_source_mismatch_is_rejected(self):
        loaded = b.bpi.load_source(self.source_dir)
        baseline = json.loads((self.baseline_dir / "summary.json").read_text(encoding="utf-8"))
        baseline["plan"]["source"]["adapter_sha256"] = "0" * 64
        write_json(self.baseline_dir / "summary.json", baseline)
        with self.assertRaises(b.SourceValidationError):
            b.validate_baseline_source(
                b.load_baseline(self.baseline_dir), loaded, self.historical / "outer-request.json"
            )


class ComparisonTests(FixtureCase):
    def _mutate_outcomes(self, changes):
        outcomes = {}
        for spec in self.ordered:
            name = f"{spec['name']}-b{spec['budget_ms']}"
            spec_outcome = outcome_for(spec["fixture"], b._action_key(spec), spec["budget_ms"])
            if name in changes:
                spec_outcome = dict(spec_outcome)
                spec_outcome.update(changes[name])
            outcomes[name] = spec_outcome
        return outcomes

    def test_identical_replay_matches_baseline(self):
        summary = self.run_check(run=True, runner=self.runner(), clock=ThreadSafeFakeClock([0]))
        self.assertTrue(summary["decision"]["comparison_available"])
        self.assertTrue(summary["decision"]["top_sets_all_match"])
        self.assertTrue(summary["decision"]["exact_all_match"])
        for entry in summary["cell_comparison"]:
            self.assertTrue(entry["exact_semantic_match"], msg=entry["cell"])
            self.assertTrue(entry["input_status_match"])

    def test_semantic_difference_hidden_by_stable_top_set(self):
        # Same ordering (BACKFLIP alone on top) but a different winning HP.
        name = "silent-late-pm001-proxy-add-BACKFLIP-b4000"
        outcomes = self._mutate_outcomes({name: {"player_hp": 20, "turns": 9}})
        summary = self.run_check(
            run=True, runner=self.runner(outcomes=outcomes), clock=ThreadSafeFakeClock([0])
        )
        late_4000 = summary["ranking_comparison"]["silent-late-pm001-proxy"][4000]
        self.assertTrue(late_4000["available"])
        self.assertFalse(late_4000["top_set_changed"])
        self.assertFalse(late_4000["tie_structure_changed"])
        changed = [entry for entry in summary["cell_comparison"]
                   if entry["cell"]["fixture"] == "silent-late-pm001-proxy"
                   and entry["cell"]["action"] == "BACKFLIP"
                   and entry["cell"]["budget_ms"] == 4000]
        self.assertEqual(len(changed), 1)
        self.assertFalse(changed[0]["exact_semantic_match"])
        self.assertIn("player_hp", changed[0]["changed_fields"])

    def test_incomplete_cell_suppresses_only_its_comparison(self):
        name = "silent-starter-proxy-skip-b4000"
        outcomes = self._mutate_outcomes(
            {name: {"status": "incomplete", "player_hp": 50, "final_enemy_hp": 10, "turns": 5}}
        )
        summary = self.run_check(
            run=True, runner=self.runner(outcomes=outcomes), clock=ThreadSafeFakeClock([0])
        )
        starter_4000 = summary["ranking_comparison"]["silent-starter-proxy"][4000]
        self.assertFalse(starter_4000["available"])
        self.assertTrue(summary["ranking_comparison"]["silent-late-pm001-proxy"][4000]["available"])
        self.assertFalse(summary["decision"]["comparison_available"])

    def test_fresh_references_report_differences_without_causality(self):
        name = "silent-late-pm001-proxy-add-BACKFLIP-b4000"
        outcomes = self._mutate_outcomes({name: {"player_hp": 20}})
        summary = self.run_check(
            run=True, runner=self.runner(outcomes=outcomes), clock=ThreadSafeFakeClock([0])
        )
        entries = {
            (entry["cell"]["action"], entry["cell"]["budget_ms"]): entry
            for entry in summary["reference"]["comparison"]
        }
        entry = entries[("BACKFLIP", 4000)]
        self.assertTrue(entry["reference_vs_baseline"]["available"])
        self.assertFalse(entry["reference_vs_baseline"]["exact_match"])
        self.assertEqual(entry["reference_vs_baseline"]["right"]["player_hp"], 20)
        self.assertIn("single observations", entry["note"])


class PerformanceTests(FixtureCase):
    def test_missing_cpu_is_excluded_not_zero(self):
        name = "silent-starter-proxy-skip-b4000"  # parallel-only label
        summary = self.run_check(
            run=True, runner=self.runner(omit_cpu=(name,)), clock=ThreadSafeFakeClock([0])
        )
        cpu = summary["performance"]["cpu"]
        self.assertEqual(cpu["runs_missing"], 1)
        self.assertIn(f"parallel/{name}", cpu["missing_runs"])
        self.assertEqual(cpu["runs_with_value"], 19)
        self.assertAlmostEqual(cpu["total_cpu_seconds"], 19 * 1.2345, places=6)
        record = {(r["phase"], r["run"]): r for r in summary["runs"]}[("parallel", name)]
        self.assertIsNone(record["total_cpu_ms"])

    def test_runtime_change_between_phases_suppresses_ratio(self):
        baseline_runtime = json.loads(
            (self.baseline_dir / "summary.json").read_text(encoding="utf-8")
        )["runtime"]
        changed = json.loads(json.dumps(baseline_runtime))
        changed["harness"]["sha256"] = "f" * 64
        with mock.patch.object(
            b.ranking, "collect_runtime_provenance", side_effect=[baseline_runtime, changed]
        ):
            summary = self.run_check(run=True, runner=self.runner(), clock=ThreadSafeFakeClock([100000]))
        self.assertFalse(summary["runtime"]["stable"])
        self.assertFalse(summary["performance"]["historical"]["ratio_available"])
        self.assertFalse(summary["decision"]["comparison_available"])
        self.assertEqual(summary["performance"]["historical"]["throughput_ratio"], None)

    def test_runtime_baseline_mismatch_aborts_run_before_launch(self):
        baseline_runtime = json.loads(
            (self.baseline_dir / "summary.json").read_text(encoding="utf-8")
        )["runtime"]
        changed = json.loads(json.dumps(baseline_runtime))
        changed["dotnet"]["sha256"] = "e" * 64
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            return self.runner()(command, **kwargs)

        with mock.patch.object(
            b.ranking, "collect_runtime_provenance", side_effect=[changed]
        ):
            with self.assertRaises(b.RuntimeValidationError):
                self.run_check(run=True, runner=spy)
        self.assertEqual(calls, [])


class GlobalStateTests(FixtureCase):
    def test_module_globals_are_not_mutated(self):
        before = {
            "DOP": b.DOP,
            "REFERENCE_TIMEOUT_SECONDS": b.REFERENCE_TIMEOUT_SECONDS,
            "PARALLEL_TIMEOUT_SECONDS": b.PARALLEL_TIMEOUT_SECONDS,
            "BATCH_DEADLINE_SECONDS": b.BATCH_DEADLINE_SECONDS,
            "PARALLELISM": b.PARALLELISM,
            "MAX_LAUNCHES": b.MAX_LAUNCHES,
            "SEED": b.SEED,
            "BUDGETS": tuple(b.BUDGETS),
            "REFERENCE_ACTIONS": tuple(b.REFERENCE_ACTIONS),
        }
        ranking_before = {
            "DOP": b.ranking.DOP,
            "PER_RUN_TIMEOUT_SECONDS": b.ranking.PER_RUN_TIMEOUT_SECONDS,
            "BATCH_DEADLINE_SECONDS": b.ranking.BATCH_DEADLINE_SECONDS,
        }
        self.run_check(run=True, runner=self.runner(), clock=ThreadSafeFakeClock([0]))
        self.assertEqual(before["DOP"], b.DOP)
        self.assertEqual(before["REFERENCE_TIMEOUT_SECONDS"], b.REFERENCE_TIMEOUT_SECONDS)
        self.assertEqual(before["PARALLEL_TIMEOUT_SECONDS"], b.PARALLEL_TIMEOUT_SECONDS)
        self.assertEqual(before["BATCH_DEADLINE_SECONDS"], b.BATCH_DEADLINE_SECONDS)
        self.assertEqual(before["PARALLELISM"], b.PARALLELISM)
        self.assertEqual(before["MAX_LAUNCHES"], b.MAX_LAUNCHES)
        self.assertEqual(before["SEED"], b.SEED)
        self.assertEqual(before["BUDGETS"], tuple(b.BUDGETS))
        self.assertEqual(before["REFERENCE_ACTIONS"], tuple(b.REFERENCE_ACTIONS))
        self.assertEqual(ranking_before["DOP"], b.ranking.DOP)
        self.assertEqual(b.ranking.DOP, "1")
        self.assertEqual(ranking_before["PER_RUN_TIMEOUT_SECONDS"], b.ranking.PER_RUN_TIMEOUT_SECONDS)
        self.assertEqual(ranking_before["BATCH_DEADLINE_SECONDS"], b.ranking.BATCH_DEADLINE_SECONDS)


class CpuPhaseRegressionTests(unittest.TestCase):
    def performance(self, parallel):
        return b.build_performance(
            [{"total_cpu_ms": 100000}], parallel, {"runs": {}},
            reference_wall=100, parallel_wall=2, cpu_count=16,
            runtime_stable=True, runtime_matches_baseline=True)

    def test_reference_cpu_excluded_from_parallel_utilization(self):
        result = self.performance([{"total_cpu_ms": 1000, "eligible": True} for _ in range(16)])
        self.assertEqual(result["cpu"]["total_cpu_seconds"], 116)
        self.assertEqual(result["cpu"]["parallel_cpu_seconds"], 16)
        self.assertEqual(result["cpu"]["average_busy_cores"], 8)
        self.assertEqual(result["cpu"]["estimated_child_utilization"], 0.5)

    def test_missing_parallel_cpu_suppresses_utilization(self):
        result = self.performance([{"total_cpu_ms": 1000}, {"total_cpu_ms": None}])
        self.assertIsNone(result["cpu"]["parallel_cpu_seconds"])
        self.assertIsNone(result["cpu"]["average_busy_cores"])
        self.assertIsNone(result["cpu"]["estimated_child_utilization"])


if __name__ == "__main__":
    unittest.main()
