#!/usr/bin/env python3
"""Offline tests for scripts/building_pick_ranking_screen.py.

Every test injects a fake ``subprocess.run``, a temporary accepted-source tree
and a temporary harness runtime, so no battle is ever started and no real game
file is touched. The tests pin the fixed instrument before it is allowed near
the game:

* the default command launches nothing and records 16 dry runs;
* the 16 runs are unique and follow the frozen budget/action order (the late
  fixture uses the entire reversed order);
* the copied input and the outer request carry only the development seed
  ``PICKSEL01``, and the copied input differs from the accepted source only by
  that seed; source files are re-hashed when copied;
* the actual runtime provenance records the runtimeconfig ``Sts2DataDir`` and
  the effective ``CombatSolverDll`` (honouring the environment override), and
  the game ``sts2.dll`` hash must match the adapter before any launch;
* timeouts, nonzero exits, missing/malformed results and invalid loadouts are
  excluded; input status stays separate from the fight outcome;
* the predeclared descriptive ranking handles wins, ties, all-losses and
  incomplete groups, and the budget comparison marks top-set/order changes;
* the module does not mutate its own globals and never logs the child
  environment.

Run with:
    python -m unittest discover -s tests -p test_building_pick_ranking_screen.py
"""

import hashlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import building_pick_ranking_screen as b  # noqa: E402

CANDIDATES = ["BACKFLIP", "BLADE_DANCE", "DAGGER_SPRAY"]
CHARACTER = "SILENT"
ASCENSION = 10

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

EXPECTED_NAMES = [
    "silent-starter-proxy-skip-b4000",
    "silent-starter-proxy-skip-b20000",
    "silent-starter-proxy-add-BACKFLIP-b20000",
    "silent-starter-proxy-add-BACKFLIP-b4000",
    "silent-starter-proxy-add-BLADE_DANCE-b4000",
    "silent-starter-proxy-add-BLADE_DANCE-b20000",
    "silent-starter-proxy-add-DAGGER_SPRAY-b20000",
    "silent-starter-proxy-add-DAGGER_SPRAY-b4000",
    "silent-late-pm001-proxy-add-DAGGER_SPRAY-b4000",
    "silent-late-pm001-proxy-add-DAGGER_SPRAY-b20000",
    "silent-late-pm001-proxy-add-BLADE_DANCE-b20000",
    "silent-late-pm001-proxy-add-BLADE_DANCE-b4000",
    "silent-late-pm001-proxy-add-BACKFLIP-b4000",
    "silent-late-pm001-proxy-add-BACKFLIP-b20000",
    "silent-late-pm001-proxy-skip-b20000",
    "silent-late-pm001-proxy-skip-b4000",
]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path: Path, data) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=1), encoding="utf-8")
    return path


def build_runtime(root: Path, *, sts2_bytes: bytes = b"fake-sts2", combat_bytes: bytes = b"fake-combat"):
    """A temporary harness runtime with a runtimeconfig and the referenced dlls."""
    runtime = Path(root) / "runtime"
    game_dir = runtime / "gamedata"
    game_dir.mkdir(parents=True, exist_ok=True)
    sts2 = game_dir / "sts2.dll"
    sts2.write_bytes(sts2_bytes)
    alt_combat = runtime / "alt-CombatSolver.dll"
    alt_combat.write_bytes(b"fake-alt-combat")
    combat = runtime / "CombatSolver.dll"
    combat.write_bytes(combat_bytes)
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
        "alt_combat": alt_combat,
        "harness": harness,
        "dotnet": dotnet,
    }


def build_source(root: Path, game_sha: str) -> Path:
    """A miniature accepted-source tree mirroring the real fixture layout."""
    source = Path(root) / "source"
    cases = source / "cases"
    cases.mkdir(parents=True, exist_ok=True)

    adapter_fixtures = []
    manifest_fixtures = []
    for fixture in FIXTURES:
        fixture_id = fixture["id"]
        deck = [{"id": card_id, "upgrade": level} for card_id, level in fixture["deck"]]
        candidates = [
            {
                "id": candidate,
                "canonicalId": candidate,
                "cardPool": "character",
                "upgrade": 0,
                "score": 1.0,
                "finite": True,
                "reason": "fixture",
            }
            for candidate in CANDIDATES
        ]
        adapter_fixtures.append(
            {
                "id": fixture_id,
                "label": "PROXY",
                "proxy": True,
                "seed": fixture["seed"],
                "characterId": CHARACTER,
                "ascension": ASCENSION,
                "actIndex": fixture["act_index"],
                "encounterId": fixture["encounter"],
                "maxHp": 70,
                "currentHp": 70,
                "relics": [],
                "potions": [],
                "deck": deck,
                "candidates": candidates,
                "bestRewardAllowSkip": fixture["best_reward"],
            }
        )

        actions = []
        for label, candidate in [("skip", None)] + [
            (f"add-{candidate}", candidate) for candidate in CANDIDATES
        ]:
            scenario = {
                "schemaVersion": 1,
                "seed": fixture["seed"],
                "characterId": CHARACTER,
                "ascension": ASCENSION,
                "actIndex": fixture["act_index"],
                "encounterId": fixture["encounter"],
                "playerCurrentHp": 70,
                "includeStartingDeck": True,
                "includeStartingRelics": False,
                "includeAscendersBane": True,
                "characterCards": {"count": 0, "ids": [], "upgradeLevelsPerCard": []},
                "colorlessCards": {"count": 0, "ids": [], "upgradeLevelsPerCard": []},
                "relics": {"count": 0},
                "potions": {"count": 0},
                "fixedSearchBudget": False,
                "mode": "Deploy",
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
                "id": fixture_id,
                "label": "PROXY",
                "proxy": True,
                "context": {
                    "seed": fixture["seed"],
                    "characterId": CHARACTER,
                    "ascension": ASCENSION,
                    "actIndex": fixture["act_index"],
                    "encounterId": fixture["encounter"],
                },
                "actions": actions,
            }
        )

    adapter = {
        "schemaVersion": 1,
        "tool": "BuildingDecisionProbe",
        "runtime": {"sts2": {"path": "fake", "sha256": game_sha}},
        "fixtures": adapter_fixtures,
    }
    adapter_path = write_json(source / "adapter-output.json", adapter)
    manifest = {
        "schemaVersion": 1,
        "tool": "building_pick_cases.py",
        "source": {
            "adapterJson": str(adapter_path.resolve()),
            "adapterSha256": sha256_file(adapter_path),
        },
        "fixtures": manifest_fixtures,
    }
    write_json(cases / "manifest.json", manifest)
    return source


def write_historical(root: Path) -> Path:
    historical = Path(root) / "hist"
    write_json(
        historical / "outer-request.json",
        {
            "schemaVersion": 1,
            "runId": "OLD",
            "scenarioId": "OLD-ID",
            "characterId": CHARACTER,
            "encounterId": "AEONGLASS_BOSS",
            "modifierIds": ["x"],
            "seed": "PM001",
            "generatedScenarioPath": "OLD-PATH",
            "evidenceDirectory": "OLD-EVIDENCE",
            "holdAfterInitialSearch": True,
            "checkpointArchivePath": "old",
            "runSnapshotPath": "old",
            "replayStatePath": "old",
            "nativeStatePath": "old",
            "replayPolicyOverridePath": "old",
            "cards": ["x"],
            "runCards": ["x"],
            "relics": ["x"],
            "combatRelics": ["x"],
            "potions": ["x"],
            "powers": ["x"],
            "ascension": ASCENSION,
            "actIndexForTest": 2,
        },
    )
    return historical


class FakeClock:
    """Returns queued values, then repeats the last one."""

    def __init__(self, values):
        self.values = list(values)
        self.last = self.values[-1] if self.values else 0.0

    def __call__(self, *args, **kwargs):
        if self.values:
            self.last = self.values.pop(0)
        return self.last


def _label(command):
    return command[command.index("--label") + 1]


def _out_dir(command):
    return Path(command[command.index("--out") + 1])


def harness_payload(run, *, outcome="incomplete", player_hp=50, enemy_hp=10, turn=5):
    if outcome == "win":
        enemies = "[]"
    else:
        enemies = f"[SOUL_NEXUS#1:{enemy_hp}/30@MOVE]"
    return {
        "resolvedScenario": {
            "SchemaVersion": 1,
            "Seed": run["seed"],
            "CharacterId": run["character"],
            "Ascension": run["ascension"],
            "ActIndex": run["act_index"],
            "EncounterId": run["encounter"],
        },
        "fightTruncated": None,
        "fightWallMs": 1234,
        "wallSeconds": 2.5,
        "timeBoundaryObserved": False,
        "solverMetrics": {"ElapsedMilliseconds": 1200.0},
        "afterTurns": (
            f"turn={turn} round=1 phase=Play side=Player energy=3 hp={player_hp}/70 "
            f"hand=[] draw=1 discard=0 relics=[] enemies={enemies} "
            "total_floor=1 act_floor=0"
        ),
    }


def loadout_payload(run, adapter_fixture):
    deck = [
        {"id": entry["id"], "currentUpgradeLevel": entry["upgrade"]}
        for entry in adapter_fixture["deck"]
    ]
    if run["candidate"] is not None:
        deck.append({"id": run["candidate"], "currentUpgradeLevel": 0})
    return {
        "schemaVersion": 1,
        "seed": run["seed"],
        "characterId": run["character"],
        "ascension": run["ascension"],
        "actIndex": run["act_index"],
        "encounterId": run["encounter"],
        "playerHp": 70,
        "playerMaxHp": 70,
        "relics": [],
        "potionSlots": [None, None],
        "deck": deck,
    }


def make_fake_runner(runs, fixtures, *, mutate=None, returncode=0, timeout_labels=(),
                     calls=None, raises=None, drop_result=(), drop_loadout=(),
                     malformed_result=(), malformed_loadout=(), outcomes=None):
    """A subprocess.run stand-in that writes harness + evidence artifacts."""
    by_name = {run["name"]: run for run in runs}
    outcomes = outcomes or {}

    def run(command, **kwargs):
        if calls is not None:
            calls.append({"command": list(command), "kwargs": dict(kwargs)})
        if raises is not None:
            raise raises
        label = _label(command)
        out_dir = _out_dir(command)
        if label in timeout_labels:
            raise subprocess.TimeoutExpired(
                command, kwargs.get("timeout"), output=b"partial-out", stderr=b"partial-err"
            )
        out_dir.mkdir(parents=True, exist_ok=True)
        case = by_name[label]

        if label in malformed_result:
            (out_dir / "harness-result.json").write_text("not json", encoding="utf-8")
        elif label not in drop_result:
            spec = outcomes.get(label, {})
            write_json(
                out_dir / "harness-result.json",
                harness_payload(
                    case,
                    outcome=spec.get("outcome", "incomplete"),
                    player_hp=spec.get("player_hp", 50),
                    enemy_hp=spec.get("enemy_hp", 10),
                ),
            )

        if label not in drop_loadout:
            loadout = loadout_payload(case, fixtures[case["fixture"]])
            if mutate is not None:
                mutate(label, loadout)
            evidence = out_dir / "evidence"
            evidence.mkdir(parents=True, exist_ok=True)
            if label in malformed_loadout:
                (evidence / "generated-scenario.loadout.json").write_text(
                    "not json", encoding="utf-8"
                )
            else:
                write_json(evidence / "generated-scenario.loadout.json", loadout)
            write_json(
                evidence / "generated-scenario.resolved.json",
                {
                    "seed": case["seed"],
                    "characterId": case["character"],
                    "ascension": case["ascension"],
                    "actIndex": case["act_index"],
                    "encounterId": case["encounter"],
                },
            )

        return subprocess.CompletedProcess(
            command, returncode, stdout=b"stdout-bytes", stderr=b"stderr-bytes"
        )

    return run


class FixtureCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.runtime = build_runtime(self.tmp)
        self.source = build_source(self.tmp, self.runtime["sts2_sha"])
        self.historical = write_historical(self.tmp)
        _, self.ordered = b.prepare_source(self.source)
        self.runs = b.build_runs(
            self.ordered, self.tmp / "out",
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
        )
        self.fixtures = {
            fixture["id"]: fixture for fixture in json.loads(
                (self.source / "adapter-output.json").read_text(encoding="utf-8")
            )["fixtures"]
        }

    def tearDown(self):
        self._tmp.cleanup()

    def runner(self, **kwargs):
        return make_fake_runner(self.runs, self.fixtures, **kwargs)

    def run_screen(self, *, out=None, **kwargs):
        kwargs.setdefault("source_dir", self.source)
        kwargs.setdefault("historical_dir", self.historical)
        kwargs.setdefault("output_root", out if out is not None else self.tmp / "out")
        kwargs.setdefault("harness_path", self.runtime["harness"])
        kwargs.setdefault("dotnet_path", self.runtime["dotnet"])
        kwargs.setdefault("runtime_config_path", self.runtime["config"])
        kwargs.setdefault("runtime_env", {})
        return b.run_ranking_screen(**kwargs)


class ConfigurationTests(unittest.TestCase):
    def test_fixed_limits_and_flags(self):
        self.assertEqual(b.MAX_LAUNCHES, 16)
        self.assertEqual(b.PER_RUN_TIMEOUT_SECONDS, 120)
        self.assertEqual(b.BATCH_DEADLINE_SECONDS, 900)
        self.assertEqual(b.BUDGETS, (4000, 20000))
        self.assertEqual(b.SEED, "PICKSEL01")
        self.assertEqual(b.MAX_TURNS, "40")
        self.assertEqual(b.BUDGETS.count(4000), 1)
        self.assertEqual(b.DOP, "1")
        self.assertEqual(b.NODES, "6000")
        self.assertEqual(b.PROFILE, "Medium")
        self.assertEqual(b.SEARCH_TIMEOUT_MS, "60000")
        self.assertEqual(b.MILESTONE, "M2")
        self.assertEqual(b.PLAY_PATH, "gameaction")
        self.assertEqual(len(b.WF_ORDER), 8)
        self.assertNotIn("PICKSEL02", json.dumps(b.RANKING_RULE))
        self.assertNotIn("PICKEVAL", json.dumps(b.RANKING_RULE))

    def test_compute_timeout_caps_and_decays(self):
        self.assertEqual(b.compute_timeout(0), 120.0)
        self.assertEqual(b.compute_timeout(600), 120.0)
        self.assertEqual(b.compute_timeout(800), 100.0)
        self.assertEqual(b.compute_timeout(899.5), 0.5)
        self.assertEqual(b.compute_timeout(900), 0.0)
        self.assertEqual(b.compute_timeout(1000), 0.0)

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
        with mock.patch.object(b.subprocess, "run") as run:
            summary = self.run_screen()
        run.assert_not_called()
        self.assertFalse(summary["executed"])
        self.assertEqual([r["status"] for r in summary["runs"]], [b.STATUS_DRY_RUN] * 16)
        self.assertTrue(all(not r["launched"] for r in summary["runs"]))
        self.assertTrue((self.tmp / "out" / "plan.json").exists())
        self.assertTrue((self.tmp / "out" / "summary.json").exists())
        self.assertTrue((self.tmp / "out" / "journal.jsonl").exists())
        self.assertEqual(summary["verified"], 0)
        self.assertTrue(summary["tool_completed"])

    def test_plan_has_16_unique_runs_in_frozen_order(self):
        self.run_screen()
        plan = json.loads((self.tmp / "out" / "plan.json").read_text(encoding="utf-8"))
        names = [run["name"] for run in plan["runs"]]
        self.assertEqual(names, EXPECTED_NAMES)
        self.assertEqual(len(set(names)), 16)
        self.assertEqual(plan["max_launches"], 16)
        self.assertEqual(plan["per_run_timeout_seconds"], 120)
        self.assertEqual(plan["batch_deadline_seconds"], 900)
        self.assertEqual(plan["budgets_ms"], [4000, 20000])
        for run in plan["runs"]:
            self.assertEqual(run["timeout_seconds"], 120)
            self.assertEqual(run["command"][run["command"].index("--max-turns") + 1], "40")
            self.assertEqual(run["command"][run["command"].index("--dop") + 1], "1")
            self.assertEqual(run["command"][run["command"].index("--nodes") + 1], "6000")
            self.assertEqual(run["command"][run["command"].index("--profile") + 1], "Medium")
            self.assertIn("--no-card-probe", run["command"])
            self.assertIn("--deploy-plan", run["command"])
        # budget order alternates within the first fixture and is reversed for the late one.
        first_budgets = [run["budget_ms"] for run in plan["runs"][:8]]
        late_budgets = [run["budget_ms"] for run in plan["runs"][8:]]
        self.assertEqual(first_budgets, [4000, 20000, 20000, 4000, 4000, 20000, 20000, 4000])
        self.assertEqual(late_budgets, list(reversed(first_budgets)))

    def test_main_dry_run_launches_nothing_and_exits_zero(self):
        out = self.tmp / "out-main"
        with mock.patch.object(b, "SOURCE_DIR", self.source), \
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
        (self.tmp / "out").mkdir(parents=True)
        with mock.patch.object(b.subprocess, "run") as run:
            with self.assertRaises(FileExistsError):
                self.run_screen(run=True, runner=self.runner())
        run.assert_not_called()


class SeedTests(FixtureCase):
    def test_input_and_request_use_picksel01(self):
        self.run_screen(run=True, runner=self.runner(), clock=FakeClock([0]))
        for name in ("silent-starter-proxy-skip-b4000", "silent-late-pm001-proxy-skip-b4000"):
            run_dir = self.tmp / "out" / name
            copied = json.loads((run_dir / "input.json").read_text(encoding="utf-8"))
            request = json.loads((run_dir / "outer-request.json").read_text(encoding="utf-8"))
            self.assertEqual(copied["seed"], b.SEED)
            self.assertEqual(request["seed"], b.SEED)
            self.assertEqual(request["characterId"], CHARACTER)

    def test_copied_input_differs_from_source_only_by_seed(self):
        self.run_screen(run=True, runner=self.runner(), clock=FakeClock([0]))
        for run in self.runs:
            source_path = Path(run["scenario_path"])
            original = json.loads(source_path.read_text(encoding="utf-8"))
            copied = json.loads(
                (self.tmp / "out" / run["name"] / "input.json").read_text(encoding="utf-8")
            )
            self.assertEqual(b.diff_paths(original, copied), {"seed"})
            self.assertEqual(copied["characterId"], original["characterId"])

    def test_outer_request_seed_override_changes_only_seed(self):
        run = self.runs[0]
        run_dir = self.tmp / "out" / run["name"]
        request = json.loads(
            (self.historical / "outer-request.json").read_text(encoding="utf-8")
        )
        baseline = b.brp.sanitize_request(request, run_dir, run["name"])
        baseline = json.loads(json.dumps(baseline))
        baseline["seed"] = b.SEED
        self.assertEqual(b.diff_paths(baseline, baseline), set())
        # The written request is the sanitized baseline with the development seed.
        b.write_run_inputs(run_dir, run, self.historical / "outer-request.json")
        written = json.loads((run_dir / "outer-request.json").read_text(encoding="utf-8"))
        self.assertEqual(b.diff_paths(baseline, written), set())
        self.assertEqual(written["seed"], b.SEED)

    def test_source_rehash_guard_rejects_changed_scenario(self):
        run = self.runs[0]
        original = Path(run["scenario_path"]).read_bytes()
        try:
            Path(run["scenario_path"]).write_bytes(original + b"\n")
            with self.assertRaises(b.SourceValidationError):
                b.write_run_inputs(
                    self.tmp / "out" / run["name"], run,
                    self.historical / "outer-request.json",
                )
        finally:
            Path(run["scenario_path"]).write_bytes(original)


class RunTests(FixtureCase):
    def test_all_16_verified_with_incomplete_outcomes(self):
        calls = []
        summary = self.run_screen(
            run=True, runner=self.runner(calls=calls), clock=FakeClock([0])
        )
        self.assertEqual(len(calls), 16)
        self.assertEqual(summary["verified"], 16)
        self.assertEqual(summary["eligible_completed"], 0)
        self.assertTrue(summary["tool_completed"])
        self.assertEqual([r["run"] for r in summary["runs"]], EXPECTED_NAMES)
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.STATUS_VERIFIED)
            self.assertEqual(record["outcome"]["status"], b.be.STATUS_INCOMPLETE)
            self.assertFalse(record["eligible"])
            self.assertEqual(record["player_hp"], 50)
            self.assertEqual(record["max_hp"], 70)
            self.assertIsNotNone(record["cost_ms"])
            self.assertEqual(record["time_boundary_observed"], False)
            self.assertIn("input_sha256", record["raw_hashes"])
            self.assertIn("harness_result_sha256", record["raw_hashes"])

    def test_subprocess_is_sequential_and_uses_fixed_flags(self):
        calls = []
        self.run_screen(run=True, runner=self.runner(calls=calls), clock=FakeClock([0]))
        self.assertEqual([_label(call["command"]) for call in calls], EXPECTED_NAMES)
        for call in calls:
            self.assertEqual(call["kwargs"]["cwd"], str(b.CWD))
            self.assertIs(call["kwargs"]["capture_output"], True)
            self.assertEqual(call["kwargs"]["timeout"], 120.0)
            self.assertEqual(
                call["kwargs"]["env"]["OFFLINE_HARNESS_SOULNEXUS_HP_SCALE"], "1.0"
            )

    def test_journal_has_one_line_per_attempt(self):
        self.run_screen(run=True, runner=self.runner(), clock=FakeClock([0]))
        lines = [
            line
            for line in (self.tmp / "out" / "journal.jsonl").read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        self.assertEqual(len(lines), 16)
        self.assertEqual([json.loads(line)["run"] for line in lines], EXPECTED_NAMES)

    def test_inherited_environment_is_never_logged(self):
        with mock.patch.object(
            b.brp, "build_env", return_value={"SECRET_SENTINEL": "leak-me"}
        ):
            summary = self.run_screen(run=True, runner=self.runner(), clock=FakeClock([0]))
        encoded = json.dumps(summary)
        self.assertNotIn("leak-me", encoded)
        self.assertNotIn("SECRET_SENTINEL", encoded)
        self.assertEqual(set(summary["plan"]["env_overrides"]), set(b.brp.ENV_OVERRIDES))

    def test_win_outcomes_produce_available_rankings(self):
        outcomes = {}
        for run in self.runs:
            if run["candidate"] is None:
                hp = 40
            elif run["candidate"] == "BACKFLIP":
                hp = 50
            elif run["candidate"] == "BLADE_DANCE":
                hp = 50
            else:
                hp = 20
            outcomes[run["name"]] = {"outcome": "win", "player_hp": hp}
        summary = self.run_screen(
            run=True, runner=self.runner(outcomes=outcomes), clock=FakeClock([0])
        )
        self.assertEqual(summary["eligible_completed"], 16)
        for fixture_id in ("silent-starter-proxy", "silent-late-pm001-proxy"):
            for budget in b.BUDGETS:
                ranking = summary["ranking"][fixture_id][budget]
                self.assertTrue(ranking["available"])
                self.assertEqual(
                    ranking["tie_groups"][0]["actions"], ["BACKFLIP", "BLADE_DANCE"]
                )
                self.assertEqual(ranking["tie_groups"][1]["actions"], ["skip"])
                self.assertEqual(ranking["tie_groups"][2]["actions"], ["DAGGER_SPRAY"])
        starter = summary["ranking"]["silent-starter-proxy"]
        self.assertEqual(starter[4000]["build_value_action"], "BACKFLIP")
        self.assertTrue(starter[4000]["build_value_in_top"])
        late = summary["ranking"]["silent-late-pm001-proxy"]
        self.assertEqual(late[4000]["build_value_action"], "DAGGER_SPRAY")
        self.assertFalse(late[4000]["build_value_in_top"])


class RuntimeProvenanceTests(FixtureCase):
    def test_records_effective_combat_solver_and_override(self):
        adapter = json.loads(
            (self.source / "adapter-output.json").read_text(encoding="utf-8")
        )
        no_override = b.collect_runtime_provenance(
            self.runtime["config"], adapter,
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"], env={},
        )
        self.assertFalse(no_override["combat_solver_dll"]["override"])
        self.assertEqual(
            no_override["combat_solver_dll"]["effective_path"], str(self.runtime["combat"])
        )
        self.assertTrue(no_override["sts2_dll"]["matches_adapter"])

        override = b.collect_runtime_provenance(
            self.runtime["config"], adapter,
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
            env={"OFFLINE_HARNESS_COMBATSOLVER_DLL": str(self.runtime["alt_combat"])},
        )
        self.assertTrue(override["combat_solver_dll"]["override"])
        self.assertEqual(
            override["combat_solver_dll"]["effective_path"], str(self.runtime["alt_combat"])
        )
        self.assertNotEqual(
            override["combat_solver_dll"]["effective_sha256"],
            no_override["combat_solver_dll"]["effective_sha256"],
        )

    def test_game_hash_mismatch_fails_before_launch(self):
        other = build_runtime(self.tmp, sts2_bytes=b"different-sts2")
        out = self.tmp / "out-mismatch"
        with self.assertRaises(b.RuntimeValidationError):
            self.run_screen(run=True, out=out, runner=self.runner(), runtime_config_path=other["config"])
        self.assertFalse(out.exists())

    def test_missing_runtime_artifact_fails_before_launch(self):
        missing = self.tmp / "missing-config.json"
        write_json(
            missing,
            {"runtimeOptions": {"configProperties": {"Sts2DataDir": str(self.tmp / "nope"),
                                                      "CombatSolverDll": str(self.runtime["combat"])}}},
        )
        out = self.tmp / "out-missing"
        with self.assertRaises(b.RuntimeValidationError):
            self.run_screen(run=True, out=out, runner=self.runner(), runtime_config_path=missing)
        self.assertFalse(out.exists())

    def test_dry_run_records_runtime_error_without_launching(self):
        missing = self.tmp / "missing-config-dry.json"
        write_json(
            missing,
            {"runtimeOptions": {"configProperties": {"Sts2DataDir": str(self.tmp / "nope"),
                                                      "CombatSolverDll": str(self.runtime["combat"])}}},
        )
        with mock.patch.object(b.subprocess, "run") as run:
            summary = self.run_screen(out=self.tmp / "out-dry", runtime_config_path=missing)
        run.assert_not_called()
        self.assertFalse(summary["runtime"]["ok"])


class FailureExclusionTests(FixtureCase):
    def _record(self, summary, name):
        return {record["run"]: record for record in summary["runs"]}[name]

    def test_timeout_is_excluded(self):
        summary = self.run_screen(
            run=True,
            runner=self.runner(timeout_labels=("silent-starter-proxy-skip-b4000",)),
            clock=FakeClock([0]),
        )
        record = self._record(summary, "silent-starter-proxy-skip-b4000")
        self.assertEqual(record["status"], b.STATUS_TIMEOUT)
        self.assertEqual(record["outcome"]["status"], b.be.STATUS_TIMEOUT)
        self.assertFalse(record["eligible"])
        self.assertEqual(
            (self.tmp / "out" / "silent-starter-proxy-skip-b4000" / "stdout.bin").read_bytes(),
            b"partial-out",
        )

    def test_nonzero_exit_is_excluded(self):
        summary = self.run_screen(
            run=True, runner=self.runner(returncode=1), clock=FakeClock([0])
        )
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.STATUS_ERROR)
            self.assertFalse(record["eligible"])

    def test_missing_result_is_missing(self):
        summary = self.run_screen(
            run=True,
            runner=self.runner(drop_result=("silent-starter-proxy-skip-b4000",)),
            clock=FakeClock([0]),
        )
        record = self._record(summary, "silent-starter-proxy-skip-b4000")
        self.assertEqual(record["status"], b.STATUS_MISSING)
        self.assertFalse(record["eligible"])

    def test_malformed_result_is_mismatch(self):
        summary = self.run_screen(
            run=True,
            runner=self.runner(malformed_result=("silent-starter-proxy-skip-b4000",)),
            clock=FakeClock([0]),
        )
        record = self._record(summary, "silent-starter-proxy-skip-b4000")
        self.assertEqual(record["status"], b.STATUS_MISMATCH)

    def test_invalid_loadout_is_mismatch_and_not_eligible(self):
        def mutate(label, loadout):
            if label == "silent-starter-proxy-skip-b4000":
                loadout["seed"] = "WRONG"

        summary = self.run_screen(
            run=True, runner=self.runner(mutate=mutate), clock=FakeClock([0])
        )
        record = self._record(summary, "silent-starter-proxy-skip-b4000")
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertFalse(record["eligible"])
        self.assertIn("loadout.seed", record["reason"])


class DeadlineTests(FixtureCase):
    def test_exhausted_deadline_marks_not_started_without_launch(self):
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            raise AssertionError("no subprocess may start after the deadline")

        summary = self.run_screen(run=True, runner=spy, clock=FakeClock([0, 1000]))
        self.assertEqual(calls, [])
        self.assertTrue(all(r["status"] == b.STATUS_NOT_STARTED for r in summary["runs"]))
        self.assertTrue(summary["batch"]["attempts_accounted"])

    def test_timeout_is_recomputed_after_preparation(self):
        calls = []
        summary = self.run_screen(
            run=True,
            runner=self.runner(calls=calls),
            clock=FakeClock([0, 0, 850]),
        )
        self.assertEqual(len(calls), 16)
        self.assertEqual(calls[0]["kwargs"]["timeout"], 50.0)
        self.assertEqual(summary["runs"][0]["timeout_before_preparation_seconds"], 120.0)
        self.assertEqual(summary["runs"][0]["timeout_seconds"], 50.0)


def make_record(action, status, outcome_status, hp=None):
    return {
        "candidate": None if action == "skip" else action,
        "status": status,
        "outcome": {"status": outcome_status, "player_hp": hp, "reason": "crafted"},
    }


def group(**kwargs):
    return {action: make_record(action, **spec) for action, spec in kwargs.items()}


class RankingTests(unittest.TestCase):
    def test_win_hp_order_and_ties(self):
        records = group(
            skip=dict(status="verified", outcome_status="win", hp=30),
            BACKFLIP=dict(status="verified", outcome_status="win", hp=55),
            BLADE_DANCE=dict(status="verified", outcome_status="win", hp=55),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=12),
        )
        ranking = b.build_ranking(records, 4000, "BACKFLIP")
        self.assertTrue(ranking["available"])
        self.assertEqual(ranking["tie_groups"][0]["actions"], ["BACKFLIP", "BLADE_DANCE"])
        self.assertEqual(ranking["tie_groups"][1]["actions"], ["skip"])
        self.assertEqual(ranking["tie_groups"][2]["actions"], ["DAGGER_SPRAY"])
        self.assertEqual(ranking["top_actions"], ["BACKFLIP", "BLADE_DANCE"])
        self.assertTrue(ranking["build_value_in_top"])

    def test_all_losses_form_one_tie(self):
        records = group(
            skip=dict(status="verified", outcome_status="loss", hp=0),
            BACKFLIP=dict(status="verified", outcome_status="loss", hp=0),
            BLADE_DANCE=dict(status="verified", outcome_status="loss", hp=0),
            DAGGER_SPRAY=dict(status="verified", outcome_status="loss", hp=0),
        )
        ranking = b.build_ranking(records, 4000, "DAGGER_SPRAY")
        self.assertTrue(ranking["available"])
        self.assertEqual(len(ranking["tie_groups"]), 1)
        self.assertEqual(ranking["tie_groups"][0]["result"], b.be.STATUS_LOSS)
        self.assertEqual(sorted(ranking["top_actions"]), sorted(b.ACTIONS))

    def test_win_outranks_loss_even_with_lower_hp(self):
        records = group(
            skip=dict(status="verified", outcome_status="win", hp=1),
            BACKFLIP=dict(status="verified", outcome_status="loss", hp=0),
            BLADE_DANCE=dict(status="verified", outcome_status="loss", hp=0),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=60),
        )
        ranking = b.build_ranking(records, 4000, "skip")
        self.assertEqual(ranking["tie_groups"][0]["actions"], ["DAGGER_SPRAY"])
        self.assertEqual(ranking["tie_groups"][1]["actions"], ["skip"])
        self.assertEqual(ranking["tie_groups"][2]["result"], b.be.STATUS_LOSS)

    def test_incomplete_group_disables_ranking(self):
        records = group(
            skip=dict(status="verified", outcome_status="win", hp=30),
            BACKFLIP=dict(status="verified", outcome_status="win", hp=30),
            BLADE_DANCE=dict(status="verified", outcome_status="incomplete", hp=30),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=30),
        )
        ranking = b.build_ranking(records, 4000, "BACKFLIP")
        self.assertFalse(ranking["available"])
        self.assertIn("BLADE_DANCE", ranking["reasons"])
        self.assertIn("incomplete", ranking["reasons"]["BLADE_DANCE"])

    def test_input_mismatch_disables_ranking(self):
        records = group(
            skip=dict(status="verified", outcome_status="win", hp=30),
            BACKFLIP=dict(status="mismatch", outcome_status="win", hp=30),
            BLADE_DANCE=dict(status="verified", outcome_status="win", hp=30),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=30),
        )
        ranking = b.build_ranking(records, 4000, "BACKFLIP")
        self.assertFalse(ranking["available"])
        self.assertEqual(ranking["reasons"]["BACKFLIP"], "input mismatch: None")

    def test_budget_comparison_detects_tie_changes(self):
        tie_4s = group(
            skip=dict(status="verified", outcome_status="win", hp=10),
            BACKFLIP=dict(status="verified", outcome_status="win", hp=50),
            BLADE_DANCE=dict(status="verified", outcome_status="win", hp=40),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=40),
        )
        split_20s = group(
            skip=dict(status="verified", outcome_status="win", hp=10),
            BACKFLIP=dict(status="verified", outcome_status="win", hp=50),
            BLADE_DANCE=dict(status="verified", outcome_status="win", hp=40),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=30),
        )
        comparison = b.compare_budgets(
            b.build_ranking(tie_4s, 4000, "BACKFLIP"),
            b.build_ranking(split_20s, 20000, "BACKFLIP"),
        )
        self.assertTrue(comparison["available"])
        self.assertFalse(comparison["top_set_changed"])
        self.assertTrue(comparison["tie_structure_changed"])
        changed_pairs = [entry["pair"] for entry in comparison["order_changed"]]
        self.assertIn(["BLADE_DANCE", "DAGGER_SPRAY"], changed_pairs)

    def test_budget_comparison_detects_top_set_flip(self):
        top_backflip = group(
            skip=dict(status="verified", outcome_status="win", hp=10),
            BACKFLIP=dict(status="verified", outcome_status="win", hp=50),
            BLADE_DANCE=dict(status="verified", outcome_status="win", hp=10),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=10),
        )
        top_dagger = group(
            skip=dict(status="verified", outcome_status="win", hp=10),
            BACKFLIP=dict(status="verified", outcome_status="win", hp=10),
            BLADE_DANCE=dict(status="verified", outcome_status="win", hp=10),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=50),
        )
        comparison = b.compare_budgets(
            b.build_ranking(top_backflip, 4000, "BACKFLIP"),
            b.build_ranking(top_dagger, 20000, "BACKFLIP"),
        )
        self.assertTrue(comparison["top_set_changed"])
        changed = [entry["pair"] for entry in comparison["order_changed"]]
        self.assertIn(["BACKFLIP", "DAGGER_SPRAY"], changed)

    def test_budget_comparison_unavailable_when_ranking_unavailable(self):
        incomplete = group(
            skip=dict(status="verified", outcome_status="incomplete", hp=10),
            BACKFLIP=dict(status="verified", outcome_status="win", hp=50),
            BLADE_DANCE=dict(status="verified", outcome_status="win", hp=10),
            DAGGER_SPRAY=dict(status="verified", outcome_status="win", hp=10),
        )
        comparison = b.compare_budgets(
            b.build_ranking(incomplete, 4000, "BACKFLIP"),
            b.build_ranking(incomplete, 20000, "BACKFLIP"),
        )
        self.assertFalse(comparison["available"])
        self.assertIn("budget_4000ms", comparison["reasons"])

    def test_build_value_action_mapping(self):
        self.assertEqual(
            b.resolve_build_value_action({"id": "a", "bestRewardAllowSkip": -1, "candidates": []}),
            "skip",
        )
        self.assertEqual(
            b.resolve_build_value_action(
                {"id": "a", "bestRewardAllowSkip": 1,
                 "candidates": [{"id": "X"}, {"id": "Y"}]}
            ),
            "Y",
        )
        with self.assertRaises(b.SourceValidationError):
            b.resolve_build_value_action({"id": "a", "bestRewardAllowSkip": 9, "candidates": []})


class IntegrationRankingTests(FixtureCase):
    def test_one_incomplete_action_disables_only_its_group(self):
        outcomes = {}
        for run in self.runs:
            if run["name"] == "silent-starter-proxy-skip-b4000":
                outcomes[run["name"]] = {"outcome": "incomplete", "player_hp": 50}
            else:
                outcomes[run["name"]] = {"outcome": "win", "player_hp": 50}
        summary = self.run_screen(
            run=True, runner=self.runner(outcomes=outcomes), clock=FakeClock([0])
        )
        starter_4s = summary["ranking"]["silent-starter-proxy"][4000]
        starter_20s = summary["ranking"]["silent-starter-proxy"][20000]
        self.assertFalse(starter_4s["available"])
        self.assertTrue(starter_20s["available"])
        self.assertFalse(
            summary["budget_comparison"]["silent-starter-proxy"]["available"]
        )
        self.assertTrue(
            summary["budget_comparison"]["silent-late-pm001-proxy"]["available"]
        )


class ModuleGlobalTests(FixtureCase):
    def test_module_globals_are_not_mutated(self):
        before = {
            "SEED": b.SEED,
            "BUDGETS": tuple(b.BUDGETS),
            "WF_ORDER": tuple(tuple(item) for item in b.WF_ORDER),
            "ACTIONS": tuple(b.ACTIONS),
            "MAX_LAUNCHES": b.MAX_LAUNCHES,
            "PER_RUN_TIMEOUT_SECONDS": b.PER_RUN_TIMEOUT_SECONDS,
            "BATCH_DEADLINE_SECONDS": b.BATCH_DEADLINE_SECONDS,
            "ENV_OVERRIDES": dict(b.ENV_OVERRIDES),
            "RANKING_RULE": json.dumps(b.RANKING_RULE, sort_keys=True),
        }
        self.run_screen(run=True, runner=self.runner(), clock=FakeClock([0]))
        self.assertEqual(before["SEED"], b.SEED)
        self.assertEqual(before["BUDGETS"], tuple(b.BUDGETS))
        self.assertEqual(before["WF_ORDER"], tuple(tuple(item) for item in b.WF_ORDER))
        self.assertEqual(before["ACTIONS"], tuple(b.ACTIONS))
        self.assertEqual(before["MAX_LAUNCHES"], b.MAX_LAUNCHES)
        self.assertEqual(before["PER_RUN_TIMEOUT_SECONDS"], b.PER_RUN_TIMEOUT_SECONDS)
        self.assertEqual(before["BATCH_DEADLINE_SECONDS"], b.BATCH_DEADLINE_SECONDS)
        self.assertEqual(before["ENV_OVERRIDES"], dict(b.ENV_OVERRIDES))
        self.assertEqual(before["RANKING_RULE"], json.dumps(b.RANKING_RULE, sort_keys=True))


class AcceptedSourceDryRunTests(FixtureCase):
    def test_real_accepted_source_dry_run(self):
        if not b.SOURCE_DIR.exists():
            self.skipTest("accepted fixture source is not present")
        out = self.tmp / "real-out"
        try:
            summary = b.run_ranking_screen(output_root=out)
        except (b.SourceValidationError, b.RuntimeValidationError) as exc:
            self.skipTest(f"real source/runtime unavailable: {exc}")
        self.assertEqual([run["run"] for run in summary["runs"]], EXPECTED_NAMES)
        self.assertFalse(summary["executed"])
        self.assertEqual(summary["status_counts"][b.STATUS_DRY_RUN], 16)
        self.assertTrue(summary["runtime"]["sts2_dll"]["matches_adapter"])


if __name__ == "__main__":
    unittest.main()
