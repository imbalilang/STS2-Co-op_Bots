#!/usr/bin/env python3
"""Offline tests for scripts/building_budget_probe.py.

Every test mocks ``subprocess.run`` (or injects a runner) and uses temporary
fixture roots, so no battle is ever started. The tests pin the fixed design
before the instrument is allowed near the game:

* the default command launches nothing and writes only the plan;
* exactly eight launches, two replicates per cell, block 1 forward / block 2
  reverse, and the real argv for every budget/dop combination;
* the same logical PM001 input in every copy, and resolved-scenario checks;
* the per-run and batch timeout caps, including preparation time and partial
  stderr preservation;
* classification through ``building_eval.classify`` (timeout/error/incomplete
  are never complete win/loss rows);
* within-cell semantic equality only when both replicates are complete, and a
  clear separation of tool completion from repeatability.

Run with:
    python -m unittest discover -s tests -p test_building_budget_probe.py
"""

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

import building_budget_probe as b  # noqa: E402

EXPECTED_ORDER = [
    "r1-b4000-d1",
    "r1-b20000-d2",
    "r1-b4000-d2",
    "r1-b20000-d1",
    "r2-b20000-d1",
    "r2-b4000-d2",
    "r2-b20000-d2",
    "r2-b4000-d1",
]
EXPECTED_CONFIG = {
    "r1-b4000-d1": (4000, 1),
    "r1-b20000-d2": (20000, 2),
    "r1-b4000-d2": (4000, 2),
    "r1-b20000-d1": (20000, 1),
    "r2-b20000-d1": (20000, 1),
    "r2-b4000-d2": (4000, 2),
    "r2-b20000-d2": (20000, 2),
    "r2-b4000-d1": (4000, 1),
}
FIXED_FLAGS = [
    "--profile", "Medium",
    "--nodes", "6000",
    "--search-timeout-ms", "60000",
    "--no-card-probe",
    "--deploy-plan",
    "--play-path", "gameaction",
    "--max-turns", "40",
    "--milestone", "M2",
]


def resolved(seed="PM001", character="SILENT", encounter="SOUL_NEXUS_ELITE"):
    return {"Seed": seed, "CharacterId": character, "EncounterId": encounter}


def win_payload(turn=3, hp=60, max_hp=70, enemy="", wall=1234, **overrides):
    after = (
        f"turn={turn} round={turn} phase=None side=Enemy energy=2 "
        f"hp={hp}/{max_hp} hand=[] draw=0 discard=0 relics=[] "
        f"enemies=[{enemy}] total_floor=1 act_floor=0"
    )
    payload = {
        "resolvedScenario": resolved(),
        "fightTruncated": None,
        "fightWallMs": wall,
        "timeBoundaryObserved": True,
        "replans": 3,
        "solverMetrics": {"Boundary": 9, "TotalExpanded": 664},
        "pruneCounters": {"boundary": "TimeLimit", "totalExpanded": 664},
        "searchPolicy": {"FixedBudget": True, "MaxDegreeOfParallelism": 2},
        "budget": {"MaxExpandedNodes": 6000, "BudgetMilliseconds": 4000},
        "afterTurns": after,
    }
    payload.update(overrides)
    return payload


def loss_payload(turn=5, enemy="SOUL_NEXUS#1:50/254@X"):
    return win_payload(turn=turn, hp=0, enemy=enemy)


def incomplete_payload(turn=40, hp=16, enemy="SOUL_NEXUS#1:201/254@X"):
    return win_payload(turn=turn, hp=hp, enemy=enemy)


def write_fixture(root: Path) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    (root / "input.json").write_text(
        json.dumps(
            {
                "schemaVersion": 1,
                "seed": "PM001",
                "characterId": "SILENT",
                "encounterId": "SOUL_NEXUS_ELITE",
                "ascension": 10,
            }
        ),
        encoding="utf-8",
    )
    (root / "outer-request.json").write_text(
        json.dumps(
            {
                "seed": "PM001",
                "characterId": "SILENT",
                "encounterId": "AEONGLASS_BOSS",
                "generatedScenarioPath": "OLD-PATH",
                "scenarioId": "OLD-ID",
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
                "modifierIds": ["x"],
            }
        ),
        encoding="utf-8",
    )
    return root


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


def fake_runner(payload=None, payloads=None, returncode=0, timeout_labels=(),
                calls=None, raises=None, stderr=b"stderr-bytes"):
    """A subprocess.run stand-in that writes a result file into --out."""

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
        chosen = payload
        if payloads is not None and label in payloads:
            chosen = payloads[label]
        if chosen is not None:
            (out_dir / "harness-result.json").write_text(
                json.dumps(chosen), encoding="utf-8"
            )
        return subprocess.CompletedProcess(
            command, returncode, stdout=b"stdout-bytes", stderr=stderr
        )

    return run


class FixtureCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.fixture = write_fixture(self.tmp / "fixture")
        self.out = self.tmp / "out"

    def tearDown(self):
        self._tmp.cleanup()

    def run_probe(self, **kwargs):
        kwargs.setdefault("source_dir", self.fixture)
        kwargs.setdefault("output_root", self.out)
        return b.run_probe(**kwargs)


class ConfigurationTests(unittest.TestCase):
    def test_fixed_limits_and_names(self):
        self.assertEqual(b.SEED, "PM001")
        self.assertEqual(b.CHARACTER, "SILENT")
        self.assertEqual(b.ENCOUNTER, "SOUL_NEXUS_ELITE")
        self.assertEqual(b.PER_RUN_TIMEOUT_SECONDS, 120)
        self.assertEqual(b.BATCH_DEADLINE_SECONDS, 720)
        self.assertEqual(b.MAX_LAUNCHES, 8)
        self.assertEqual(b.REPLICATES_PER_CELL, 2)
        self.assertEqual(b.BUDGETS_MS, (4000, 20000))
        self.assertEqual(b.DOPS, (1, 2))
        self.assertEqual(
            b.CELL_ORDER, ("b4000-d1", "b4000-d2", "b20000-d1", "b20000-d2")
        )

    def test_compute_timeout_caps_and_decays(self):
        self.assertEqual(b.compute_timeout(0), 120.0)
        self.assertEqual(b.compute_timeout(600), 120.0)
        self.assertEqual(b.compute_timeout(660), 60.0)
        self.assertEqual(b.compute_timeout(719.5), 0.5)
        self.assertEqual(b.compute_timeout(720), 0.0)
        self.assertEqual(b.compute_timeout(800), 0.0)

    def test_build_specs_has_two_replicates_per_cell_and_reversed_block(self):
        specs = b.build_specs()
        self.assertEqual([spec["name"] for spec in specs], EXPECTED_ORDER)
        self.assertEqual([spec["replicate"] for spec in specs], [1, 1, 1, 1, 2, 2, 2, 2])
        cells = {}
        for spec in specs:
            cells.setdefault(spec["cell"], []).append(spec["replicate"])
        for key in b.CELL_ORDER:
            self.assertEqual(sorted(cells[key]), [1, 2])
        self.assertEqual(b.BLOCK1, ((4000, 1), (20000, 2), (4000, 2), (20000, 1)))
        self.assertEqual(b.BLOCK2, tuple(reversed(b.BLOCK1)))

    def test_fresh_output_root_uses_utc_microseconds(self):
        first = b.default_output_root(
            now=datetime(2026, 9, 17, 12, 0, 0, 123456, tzinfo=timezone.utc)
        )
        second = b.default_output_root(
            now=datetime(2026, 9, 17, 12, 0, 0, 123457, tzinfo=timezone.utc)
        )
        self.assertNotEqual(first, second)
        self.assertEqual(first.parent, b.OUTPUT_ROOT)
        self.assertTrue(first.name.endswith("Z"))
        self.assertIn(".123456", first.name)


class DryRunTests(FixtureCase):
    def test_default_run_probe_launches_nothing(self):
        with mock.patch.object(b.subprocess, "run") as run:
            summary = self.run_probe()
        run.assert_not_called()
        self.assertFalse(summary["executed"])
        self.assertEqual([r["status"] for r in summary["runs"]], [b.STATUS_DRY_RUN] * 8)
        self.assertTrue(all(not r["launched"] for r in summary["runs"]))
        self.assertTrue((self.out / "plan.json").exists())
        self.assertTrue((self.out / "summary.json").exists())
        self.assertTrue((self.out / "journal.jsonl").exists())
        self.assertEqual(summary["complete"], 0)
        self.assertFalse(summary["terminal_complete"])
        self.assertTrue(summary["tool_completed"])

    def test_default_main_launches_nothing_and_exits_zero(self):
        with mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run") as run, \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = b.main([])
        run.assert_not_called()
        self.assertEqual(code, 0)

    def test_explicit_dry_run_flag_is_equivalent(self):
        with mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run") as run, \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = b.main(["--dry-run"])
        run.assert_not_called()
        self.assertEqual(code, 0)
        self.assertTrue((self.out / "plan.json").exists())

    def test_plan_lists_exactly_eight_runs_with_the_fixed_flags(self):
        self.run_probe()
        plan = json.loads((self.out / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual([r["name"] for r in plan["runs"]], EXPECTED_ORDER)
        for run in plan["runs"]:
            self.assertEqual(
                (run["budget_ms"], run["dop"]), EXPECTED_CONFIG[run["name"]]
            )
            self.assertEqual(run["timeout_seconds"], 120)
            command = run["command"]
            for flag, value in zip(FIXED_FLAGS[::2], FIXED_FLAGS[1::2]):
                self.assertEqual(command[command.index(flag) + 1], value)
            self.assertEqual(command[command.index("--budget-ms") + 1], str(run["budget_ms"]))
            self.assertEqual(command[command.index("--dop") + 1], str(run["dop"]))

    def test_refuses_an_existing_output_directory(self):
        self.out.mkdir(parents=True)
        with mock.patch.object(b.subprocess, "run") as run:
            with self.assertRaises(FileExistsError):
                self.run_probe()
        run.assert_not_called()


class ActualArgumentsTests(FixtureCase):
    def _run(self, **kwargs):
        calls = []
        summary = self.run_probe(
            run=True,
            runner=fake_runner(win_payload(), calls=calls, **kwargs),
            clock=FakeClock([0]),
        )
        return summary, calls

    def test_all_eight_argv_and_subprocess_kwargs(self):
        summary, calls = self._run()
        self.assertEqual(len(calls), 8)
        self.assertEqual([_label(call["command"]) for call in calls], EXPECTED_ORDER)
        for call in calls:
            command = call["command"]
            name = _label(command)
            budget, dop = EXPECTED_CONFIG[name]
            self.assertEqual(command[command.index("--budget-ms") + 1], str(budget))
            self.assertEqual(command[command.index("--dop") + 1], str(dop))
            self.assertEqual(command[command.index("--request") + 1],
                             str(self.out / name / "outer-request.json"))
            self.assertEqual(command[command.index("--out") + 1], str(self.out / name))
            for flag, value in zip(FIXED_FLAGS[::2], FIXED_FLAGS[1::2]):
                self.assertEqual(command[command.index(flag) + 1], value)
            kwargs = call["kwargs"]
            self.assertEqual(kwargs["cwd"], str(b.CWD))
            self.assertIs(kwargs["capture_output"], True)
            self.assertNotIn("shell", kwargs)
            self.assertEqual(kwargs["timeout"], 120.0)
            self.assertEqual(kwargs["env"]["OFFLINE_HARNESS_SOULNEXUS_HP_SCALE"], "1.0")
            self.assertEqual(kwargs["env"]["OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE"], "1.0")
        # The summary keeps the same argv and the eight attempts.
        self.assertEqual(len(summary["runs"]), 8)

    def test_every_copied_input_is_identical(self):
        summary, _ = self._run()
        source_bytes = (self.fixture / "input.json").read_bytes()
        hashes = set()
        for name in EXPECTED_ORDER:
            raw = (self.out / name / "input.json").read_bytes()
            self.assertEqual(raw, source_bytes)
            data = json.loads(raw)
            self.assertEqual(data["seed"], "PM001")
            self.assertEqual(data["characterId"], "SILENT")
            self.assertEqual(data["encounterId"], "SOUL_NEXUS_ELITE")
        for record in summary["runs"]:
            hashes.add(record["provenance"]["input_sha256"])
        self.assertEqual(len(hashes), 1)
        self.assertIsNotNone(next(iter(hashes)))
        self.assertEqual(summary["tool_completed"], True)
        self.assertEqual(summary["terminal_complete"], True)
        self.assertTrue(all(c["semantic_equal"] is True for c in summary["cells"].values()))

    def test_outer_request_is_rewritten_for_each_run(self):
        self._run()
        for name in EXPECTED_ORDER:
            req = json.loads(
                (self.out / name / "outer-request.json").read_text(encoding="utf-8")
            )
            self.assertEqual(req["generatedScenarioPath"], str(self.out / name / "input.json"))
            self.assertEqual(req["scenarioId"], name)
            self.assertEqual(req["evidenceDirectory"], str(self.out / name / "evidence"))
            self.assertIs(req["holdAfterInitialSearch"], False)
            self.assertEqual(req["seed"], "PM001")
            self.assertEqual(req["characterId"], "SILENT")
            for key in b.brp.SNAPSHOT_KEYS:
                self.assertIsNone(req[key])
            for key in b.brp.LIST_KEYS:
                self.assertEqual(req[key], [])

    def test_journal_has_one_line_per_attempt(self):
        self._run()
        lines = [
            line for line in (self.out / "journal.jsonl").read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        self.assertEqual(len(lines), 8)
        self.assertEqual([json.loads(line)["run"] for line in lines], EXPECTED_ORDER)


class ClassificationTests(FixtureCase):
    def _run(self, **kwargs):
        return self.run_probe(
            run=True, runner=fake_runner(**kwargs), clock=FakeClock([0])
        )

    def _record(self, summary, name):
        return {r["run"]: r for r in summary["runs"]}[name]

    def test_timeout_is_preserved_with_partial_output(self):
        summary = self._run(payload=win_payload(), timeout_labels=("r1-b4000-d1",))
        record = self._record(summary, "r1-b4000-d1")
        self.assertEqual(record["status"], b.be.STATUS_TIMEOUT)
        self.assertTrue(record["timed_out"])
        self.assertTrue(record["launched"])
        self.assertEqual(
            (self.out / "r1-b4000-d1" / "stdout.bin").read_bytes(), b"partial-out"
        )
        self.assertEqual(
            (self.out / "r1-b4000-d1" / "stderr.bin").read_bytes(), b"partial-err"
        )
        self.assertFalse(summary["terminal_complete"])

    def test_nonzero_exit_is_an_error(self):
        summary = self._run(payload=win_payload(), returncode=1)
        self.assertTrue(
            all(r["status"] == b.be.STATUS_ERROR for r in summary["runs"])
        )
        self.assertFalse(summary["terminal_complete"])

    def test_launch_oserror_is_an_error(self):
        summary = self._run(payload=win_payload(), raises=OSError("cannot spawn"))
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.be.STATUS_ERROR)
            self.assertIn("launch failed", record["reason"])
        self.assertFalse(summary["terminal_complete"])

    def test_missing_payload_is_missing(self):
        summary = self._run(payload=None)
        self.assertTrue(all(r["status"] == b.be.STATUS_MISSING for r in summary["runs"]))

    def test_invalid_payload_is_invalid(self):
        summary = self._run(payload={"afterTurns": "not a describe-root line"})
        self.assertTrue(all(r["status"] == b.be.STATUS_INVALID for r in summary["runs"]))

    def test_incomplete_fight_is_not_a_complete_result(self):
        summary = self._run(payload=incomplete_payload())
        self.assertTrue(all(r["status"] == b.be.STATUS_INCOMPLETE for r in summary["runs"]))
        self.assertEqual(summary["complete"], 0)
        self.assertFalse(summary["terminal_complete"])

    def test_resolved_scenario_mismatch_is_invalid(self):
        payload = win_payload(resolvedScenario=resolved(encounter="AEONGLASS_BOSS"))
        summary = self._run(payload=payload)
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.be.STATUS_INVALID)
            self.assertIn("resolvedScenario mismatch", record["reason"])

    def test_resolved_scenario_identity_mismatch_is_invalid(self):
        payload = win_payload(resolvedScenario=resolved(seed="PM999"))
        summary = self._run(payload=payload)
        self.assertTrue(all(r["status"] == b.be.STATUS_INVALID for r in summary["runs"]))


class DeadlineTests(FixtureCase):
    def test_exhausted_deadline_marks_not_started_without_launch(self):
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            raise AssertionError("no subprocess may start after the deadline")

        summary = self.run_probe(
            run=True, runner=spy, clock=FakeClock([0, 800])
        )
        self.assertEqual(calls, [])
        self.assertTrue(all(r["status"] == b.STATUS_NOT_STARTED for r in summary["runs"]))
        self.assertEqual(summary["complete"], 0)
        self.assertFalse(summary["terminal_complete"])
        self.assertTrue(summary["batch"]["attempts_accounted"])

    def test_deadline_at_exactly_720_is_not_started(self):
        summary = self.run_probe(
            run=True, runner=fake_runner(win_payload()), clock=FakeClock([0, 720])
        )
        self.assertTrue(all(r["status"] == b.STATUS_NOT_STARTED for r in summary["runs"]))
        self.assertFalse(summary["terminal_complete"])

    def test_preparation_time_is_charged_to_the_batch(self):
        calls = []
        spec = b.build_specs()[0]
        record = b.execute_case(
            spec,
            self.out,
            source_dir=self.fixture,
            runner=fake_runner(win_payload(), calls=calls),
            clock=FakeClock([700, 750]),
            start=0,
            dry_run=False,
            provenance={},
            expected={"seed": "PM001", "character": "SILENT", "encounter": "SOUL_NEXUS_ELITE"},
        )
        self.assertEqual(record["status"], b.STATUS_NOT_STARTED)
        self.assertIn("input preparation", record["reason"])
        self.assertEqual(calls, [])

    def test_timeout_is_recomputed_after_preparation(self):
        calls = []
        spec = b.build_specs()[0]
        record = b.execute_case(
            spec,
            self.out,
            source_dir=self.fixture,
            runner=fake_runner(win_payload(), calls=calls),
            clock=FakeClock([0, 650]),
            start=0,
            dry_run=False,
            provenance={},
            expected={"seed": "PM001", "character": "SILENT", "encounter": "SOUL_NEXUS_ELITE"},
        )
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0]["kwargs"]["timeout"], 70.0)
        self.assertEqual(record["timeout_seconds"], 70.0)
        self.assertEqual(record["timeout_before_preparation_seconds"], 120.0)
        self.assertEqual(record["status"], b.be.STATUS_WIN)


class SummaryTests(FixtureCase):
    def _run(self, payload=None, payloads=None):
        return self.run_probe(
            run=True,
            runner=fake_runner(payload=payload, payloads=payloads),
            clock=FakeClock([0]),
        )

    def test_status_counts_cover_every_status(self):
        summary = self._run(payload=win_payload())
        for cell in summary["cells"].values():
            self.assertEqual(set(cell["status_counts"]), set((*b.be.STATUS_ORDER, b.STATUS_DRY_RUN, b.STATUS_NOT_STARTED)))
            self.assertEqual(sum(cell["status_counts"].values()), 2)
            self.assertEqual(cell["status_counts"][b.be.STATUS_WIN], 2)

    def test_within_cell_equal_on_identical_complete_runs(self):
        summary = self._run(payload=win_payload())
        for cell in summary["cells"].values():
            self.assertEqual(cell["complete"], 2)
            self.assertTrue(cell["semantic_equal"])
            self.assertEqual(cell["semantic_diff_fields"], [])
        self.assertEqual(summary["complete"], 8)
        self.assertEqual(summary["terminal_complete"], True)
        self.assertEqual(summary["tool_completed"], True)

    def test_within_cell_different_is_reported_but_exit_zero(self):
        payloads = {
            "r1-b4000-d1": win_payload(hp=60),
            "r2-b4000-d1": win_payload(hp=50),
        }
        summary = self._run(payload=win_payload(), payloads=payloads)
        cell = summary["cells"]["b4000-d1"]
        self.assertEqual(cell["complete"], 2)
        self.assertFalse(cell["semantic_equal"])
        self.assertEqual(cell["semantic_diff_fields"], ["player_hp"])
        self.assertEqual(summary["terminal_complete"], True)
        with mock.patch.object(b, "default_output_root", return_value=self.tmp / "main-out"), \
                mock.patch.object(b.subprocess, "run",
                                  side_effect=fake_runner(payload=win_payload(), payloads=payloads)):
            self.assertEqual(b.main(["--run"]), 0)

    def test_incomplete_cell_is_not_comparable_and_exit_one(self):
        payloads = {"r1-b4000-d1": incomplete_payload()}
        summary = self._run(payload=win_payload(), payloads=payloads)
        cell = summary["cells"]["b4000-d1"]
        self.assertEqual(cell["complete"], 1)
        self.assertIsNone(cell["semantic_equal"])
        self.assertFalse(summary["terminal_complete"])
        with mock.patch.object(b, "default_output_root", return_value=self.tmp / "main-out"), \
                mock.patch.object(b.subprocess, "run",
                                  side_effect=fake_runner(payload=win_payload(), payloads=payloads)):
            self.assertEqual(b.main(["--run"]), 1)

    def test_player_hp_turn_ranges_and_time_boundary(self):
        summary = self._run(payload=win_payload(hp=42, turn=7))
        cell = summary["cells"]["b4000-d1"]
        self.assertEqual(cell["player_hp"]["min"], 42)
        self.assertEqual(cell["player_hp"]["max"], 42)
        self.assertEqual(cell["turns"]["min"], 7)
        self.assertEqual(cell["turns"]["max"], 7)
        self.assertEqual(cell["time_boundary"]["observed"], 2)
        self.assertEqual(cell["time_boundary"]["known"], 2)

    def test_study_gate_is_not_set_and_tool_completion_is_distinct(self):
        summary = self._run(payload=win_payload())
        self.assertIsNone(summary["study_gate_passed"])
        self.assertNotIn("gate_passed", summary)
        self.assertIn("repeatability_finding", summary)
        dry = self.run_probe(run=False, output_root=self.tmp / "dry-out")
        self.assertTrue(dry["tool_completed"])
        self.assertFalse(dry["terminal_complete"])

    def test_cross_cell_reports_configuration_without_claims(self):
        summary = self._run(payload=win_payload())
        by_cell = {row["cell"]: row for row in summary["cross_cell"]}
        self.assertEqual(by_cell["b4000-d1"]["budget_ms"], 4000)
        self.assertEqual(by_cell["b4000-d1"]["dop"], 1)
        self.assertEqual(by_cell["b20000-d2"]["budget_ms"], 20000)
        self.assertEqual(by_cell["b20000-d2"]["dop"], 2)
        joined = " ".join(summary["notes"])
        self.assertIn("significance", joined)
        self.assertIn("not proof", " ".join(summary["notes"]))


class ProvenanceTests(FixtureCase):
    def test_records_keep_provenance_and_metric_fields(self):
        calls = []
        summary = self.run_probe(
            run=True,
            runner=fake_runner(win_payload(), calls=calls),
            clock=FakeClock([0]),
        )
        self.assertEqual(len(summary["runs"]), 8)
        command_by_label = {_label(call["command"]): call["command"] for call in calls}
        for record in summary["runs"]:
            provenance = record["provenance"]
            self.assertIsNotNone(provenance["source_input_sha256"])
            self.assertIsNotNone(provenance["source_request_sha256"])
            self.assertIsNotNone(provenance["input_sha256"])
            self.assertIsNotNone(provenance["request_sha256"])
            for key in ("driver", "classifier", "harness", "runtime"):
                self.assertIn("path", provenance[key])
                self.assertIn("sha256", provenance[key])
            self.assertEqual(record["args"], command_by_label[record["run"]])
            self.assertTrue(record["stdout_path"])
            self.assertTrue(record["stderr_path"])
            self.assertTrue(record["raw_result_path"])
            self.assertIsNotNone(record["runtime_seconds"])
            self.assertTrue(record["time_boundary_observed"])
            self.assertIsInstance(record["solver_metrics"], dict)
            self.assertIsInstance(record["prune_counters"], dict)
            self.assertEqual(record["replans"], 3)
            self.assertIsInstance(record["search_policy"], dict)
            self.assertIsNotNone(record["harness_result_sha256"])

    def test_full_environment_is_never_emitted(self):
        summary = self.run_probe(run=True, runner=fake_runner(win_payload()),
                                 clock=FakeClock([0]))
        plan = summary["plan"]
        self.assertEqual(plan["env_overrides"], b.ENV_OVERRIDES)
        self.assertEqual(set(plan["env_overrides"]), {
            "OFFLINE_HARNESS_SOULNEXUS_HP_SCALE",
            "OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE",
        })
        self.assertNotIn("env", plan)
        for record in summary["runs"]:
            self.assertNotIn("env", record)


class PreparationFailureTests(unittest.TestCase):
    def test_missing_input_preserves_eight_error_records_and_summary(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            runner = mock.Mock(side_effect=AssertionError("must not launch"))
            result = b.run_probe(
                run=True,
                source_dir=root / "absent",
                output_root=root / "out",
                runner=runner,
            )
            self.assertEqual(len(result["runs"]), 8)
            self.assertTrue(all(r["status"] == "error" for r in result["runs"]))
            self.assertFalse(result["terminal_complete"])
            self.assertTrue((root / "out" / "summary.json").exists())
            self.assertTrue((root / "out" / "plan.json").exists())
            runner.assert_not_called()

    def test_expected_scenario_is_none_when_source_is_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertIsNone(b.expected_scenario(Path(tmp) / "absent"))
            self.assertIn("expected scenario", b.check_resolved_scenario({}, None))


class SummaryReviewTests(unittest.TestCase):
    def test_nonterminal_statuses_are_not_lost(self):
        rows = [{"status": "dry_run"}, {"status": "not_started"}]
        counts = b._status_counts(rows)
        self.assertEqual(sum(counts.values()), 2)
        self.assertEqual(counts["not_started"], 1)

    def test_cross_cell_configuration_values_are_present(self):
        rows = b.cross_cell_rows({})
        self.assertEqual({(r["budget_ms"], r["dop"]) for r in rows},
                         {(4000, 1), (4000, 2), (20000, 1), (20000, 2)})


if __name__ == "__main__":
    unittest.main()
