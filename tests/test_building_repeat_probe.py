#!/usr/bin/env python3
"""Offline tests for scripts/building_repeat_probe.py.

Every test mocks ``subprocess.run`` (or injects a runner) and uses temporary
fixture roots, so no battle is ever started. The tests prove the fixed rules
before the instrument is allowed near the game:

* the default command launches nothing and writes only the plan;
* exactly four named runs with the same seed in every copied input;
* the real argv, cwd, byte capture and fixed scale environment;
* the per-run and batch timeout caps, with partial output preserved;
* classification through ``building_eval.classify`` (timeout/error/incomplete
  are never complete win/loss rows);
* semantic equality across all four complete runs, and failure otherwise.

Run with:
    python -m unittest discover -s tests -p test_building_repeat_probe.py
"""

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

import building_repeat_probe as b  # noqa: E402


def win_payload(turn=3, hp=60, max_hp=70, enemy="", wall=1234):
    after = (
        f"turn={turn} round={turn} phase=None side=Enemy energy=2 "
        f"hp={hp}/{max_hp} hand=[] draw=0 discard=0 relics=[] "
        f"enemies=[{enemy}] total_floor=1 act_floor=0"
    )
    return {"fightTruncated": None, "fightWallMs": wall, "afterTurns": after}


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


def fake_runner(payload, returncode=0, timeout_labels=(), calls=None, raises=None):
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
        if isinstance(payload, dict) and "afterTurns" not in payload:
            chosen = payload.get(label)
        if chosen is not None:
            (out_dir / "harness-result.json").write_text(json.dumps(chosen), encoding="utf-8")
        return subprocess.CompletedProcess(
            command, returncode, stdout=b"stdout-bytes", stderr=b"stderr-bytes"
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


class ConfigurationTests(unittest.TestCase):
    def test_fixed_limits_and_names(self):
        self.assertEqual(b.SEED, "PM001")
        self.assertEqual(b.CHARACTER, "SILENT")
        self.assertEqual(b.ENCOUNTER, "SOUL_NEXUS_ELITE")
        self.assertEqual(b.PER_RUN_TIMEOUT_SECONDS, 120)
        self.assertEqual(b.BATCH_DEADLINE_SECONDS, 360)
        self.assertEqual(b.PARALLELISM, 2)
        self.assertEqual(b.RUN_ORDER, ("sequential-1", "sequential-2", "parallel-1", "parallel-2"))
        self.assertEqual(b.PARALLEL_RUNS, ("parallel-1", "parallel-2"))

    def test_compute_timeout_caps_and_decays(self):
        self.assertEqual(b.compute_timeout(0), 120.0)
        self.assertEqual(b.compute_timeout(240), 120.0)
        self.assertEqual(b.compute_timeout(300), 60.0)
        self.assertEqual(b.compute_timeout(359.5), 0.5)
        self.assertEqual(b.compute_timeout(360), 0.0)
        self.assertEqual(b.compute_timeout(400), 0.0)

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
            summary = b.run_probe(source_dir=self.fixture, output_root=self.out)
        run.assert_not_called()
        self.assertFalse(summary["executed"])
        self.assertTrue((self.out / "plan.json").exists())
        self.assertTrue((self.out / "summary.json").exists())
        self.assertEqual(summary["completed"], 0)
        self.assertIsNone(summary["repeat_equal"])
        self.assertFalse(summary["gate_passed"])

    def test_default_main_launches_nothing_and_exits_zero(self):
        with mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run") as run:
            code = b.main([])
        run.assert_not_called()
        self.assertEqual(code, 0)

    def test_explicit_dry_run_flag_is_equivalent(self):
        with mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run") as run:
            code = b.main(["--dry-run"])
        run.assert_not_called()
        self.assertEqual(code, 0)
        self.assertTrue((self.out / "plan.json").exists())

    def test_plan_lists_exactly_the_four_runs(self):
        b.run_probe(source_dir=self.fixture, output_root=self.out)
        plan = json.loads((self.out / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual([r["name"] for r in plan["runs"]], list(b.RUN_ORDER))
        self.assertEqual(
            [r["mode"] for r in plan["runs"]],
            ["sequential", "sequential", "parallel", "parallel"],
        )
        self.assertEqual(plan["seed"], "PM001")

    def test_dry_run_records_are_dry_run(self):
        summary = b.run_probe(source_dir=self.fixture, output_root=self.out)
        self.assertEqual([r["status"] for r in summary["runs"]], [b.STATUS_DRY_RUN] * 4)
        self.assertTrue(all(not r["launched"] for r in summary["runs"]))


class ActualArgumentsTests(FixtureCase):
    def test_runner_receives_spec_argv_cwd_capture_and_scale_env(self):
        calls = []
        b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(win_payload(), calls=calls),
            clock=FakeClock([0]),
        )
        self.assertEqual(len(calls), 4)
        for call in calls:
            command = call["command"]
            name = _label(command)
            self.assertIn(name, b.RUN_ORDER)
            expected = [
                str(b.DOTNET),
                str(b.HARNESS),
                "--request", str(self.out / name / "outer-request.json"),
                "--profile", "Medium",
                "--nodes", "6000",
                "--budget-ms", "4000",
                "--search-timeout-ms", "60000",
                "--dop", "2",
                "--no-card-probe",
                "--deploy-plan",
                "--play-path", "gameaction",
                "--max-turns", "40",
                "--milestone", "M2",
                "--label", name,
                "--out", str(self.out / name),
            ]
            self.assertEqual(command, expected)
            kwargs = call["kwargs"]
            self.assertEqual(kwargs["cwd"], str(b.CWD))
            self.assertIs(kwargs["capture_output"], True)
            self.assertNotIn("shell", kwargs)
            self.assertEqual(kwargs["timeout"], 120.0)
            self.assertEqual(kwargs["env"]["OFFLINE_HARNESS_SOULNEXUS_HP_SCALE"], "1.0")
            self.assertEqual(kwargs["env"]["OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE"], "1.0")

    def test_every_input_is_identical_with_seed_pm001(self):
        b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(win_payload()),
            clock=FakeClock([0]),
        )
        source_bytes = (self.fixture / "input.json").read_bytes()
        for name in b.RUN_ORDER:
            raw = (self.out / name / "input.json").read_bytes()
            self.assertEqual(raw, source_bytes)
            data = json.loads(raw)
            self.assertEqual(data["seed"], "PM001")
            self.assertEqual(data["characterId"], "SILENT")
            self.assertEqual(data["encounterId"], "SOUL_NEXUS_ELITE")

    def test_outer_request_is_rewritten_for_each_run(self):
        b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(win_payload()),
            clock=FakeClock([0]),
        )
        for name in b.RUN_ORDER:
            req = json.loads(
                (self.out / name / "outer-request.json").read_text(encoding="utf-8")
            )
            self.assertEqual(req["generatedScenarioPath"], str(self.out / name / "input.json"))
            self.assertEqual(req["scenarioId"], name)
            self.assertEqual(req["evidenceDirectory"], str(self.out / name / "evidence"))
            self.assertIs(req["holdAfterInitialSearch"], False)
            self.assertEqual(req["seed"], "PM001")
            self.assertEqual(req["characterId"], "SILENT")
            for key in b.SNAPSHOT_KEYS:
                self.assertIsNone(req[key])
            for key in b.LIST_KEYS:
                self.assertEqual(req[key], [])

    def test_each_run_is_launched_at_most_once(self):
        calls = []
        b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(win_payload(), calls=calls),
            clock=FakeClock([0]),
        )
        self.assertEqual(sorted(_label(c["command"]) for c in calls), sorted(b.RUN_ORDER))


class ClassificationTests(FixtureCase):
    def _run(self, payload, **kwargs):
        return b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(payload, **kwargs),
            clock=FakeClock([0]),
        )

    def test_timeout_is_preserved_with_partial_output(self):
        summary = self._run(win_payload(), timeout_labels=("sequential-1",))
        record = {r["run"]: r for r in summary["runs"]}["sequential-1"]
        self.assertEqual(record["status"], b.be.STATUS_TIMEOUT)
        self.assertTrue(record["timed_out"])
        self.assertTrue(record["launched"])
        self.assertEqual((self.out / "sequential-1" / "stdout.bin").read_bytes(), b"partial-out")
        self.assertEqual((self.out / "sequential-1" / "stderr.bin").read_bytes(), b"partial-err")
        self.assertFalse(summary["gate_passed"])

    def test_nonzero_exit_is_an_error(self):
        summary = self._run(win_payload(), returncode=1)
        self.assertTrue(all(r["status"] == b.be.STATUS_ERROR for r in summary["runs"]))
        self.assertFalse(summary["gate_passed"])

    def test_launch_oserror_is_an_error(self):
        summary = b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(None, raises=OSError("cannot spawn")),
            clock=FakeClock([0]),
        )
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.be.STATUS_ERROR)
            self.assertIn("launch failed", record["reason"])
        self.assertFalse(summary["gate_passed"])

    def test_missing_payload_is_missing(self):
        summary = self._run(None)
        self.assertTrue(all(r["status"] == b.be.STATUS_MISSING for r in summary["runs"]))

    def test_invalid_payload_is_invalid(self):
        summary = self._run({"afterTurns": "not a describe-root line"})
        self.assertTrue(all(r["status"] == b.be.STATUS_INVALID for r in summary["runs"]))

    def test_incomplete_fight_is_not_a_complete_result(self):
        summary = self._run(incomplete_payload())
        self.assertTrue(all(r["status"] == b.be.STATUS_INCOMPLETE for r in summary["runs"]))
        self.assertEqual(summary["completed"], 0)
        self.assertIsNone(summary["repeat_equal"])
        self.assertFalse(summary["gate_passed"])


class SemanticTests(FixtureCase):
    def _run(self, payload):
        return b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(payload),
            clock=FakeClock([0]),
        )

    def test_all_four_equal_passes_the_gate(self):
        summary = self._run(win_payload())
        self.assertEqual(summary["completed"], 4)
        self.assertTrue(summary["all_complete"])
        self.assertTrue(summary["repeat_equal"])
        self.assertFalse(summary["repeat_different"])
        self.assertTrue(summary["gate_passed"])

    def test_all_four_losses_can_also_be_equal(self):
        summary = self._run(loss_payload())
        self.assertEqual(summary["completed"], 4)
        self.assertTrue(summary["repeat_equal"])
        self.assertTrue(summary["gate_passed"])

    def test_a_semantic_difference_fails_the_gate(self):
        payloads = {
            "sequential-1": win_payload(hp=60),
            "sequential-2": win_payload(hp=50),
            "parallel-1": win_payload(hp=60),
            "parallel-2": win_payload(hp=60),
        }
        summary = self._run(payloads)
        self.assertEqual(summary["completed"], 4)
        self.assertFalse(summary["repeat_equal"])
        self.assertTrue(summary["repeat_different"])
        self.assertFalse(summary["gate_passed"])
        self.assertEqual(summary["different_runs"][0]["run"], "sequential-2")
        self.assertEqual(summary["different_runs"][0]["fields"], ["player_hp"])

    def test_cli_run_exit_codes(self):
        with mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run", side_effect=fake_runner(win_payload())):
            self.assertEqual(b.main(["--run"]), 0)

        out_two = self.tmp / "out-two"
        payloads = {
            "sequential-1": win_payload(hp=60),
            "sequential-2": win_payload(hp=50),
            "parallel-1": win_payload(hp=60),
            "parallel-2": win_payload(hp=60),
        }
        with mock.patch.object(b, "default_output_root", return_value=out_two), \
                mock.patch.object(b.subprocess, "run", side_effect=fake_runner(payloads)):
            self.assertEqual(b.main(["--run"]), 1)

        out_three = self.tmp / "out-three"
        with mock.patch.object(b, "default_output_root", return_value=out_three), \
                mock.patch.object(b.subprocess, "run",
                                  side_effect=fake_runner(incomplete_payload())):
            self.assertEqual(b.main(["--run"]), 1)


class DeadlineTests(FixtureCase):
    def test_exhausted_deadline_marks_not_started_without_launch(self):
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            raise AssertionError("no subprocess may start after the deadline")

        summary = b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=spy,
            clock=FakeClock([0, 400]),
        )
        self.assertEqual(calls, [])
        self.assertTrue(all(r["status"] == b.STATUS_NOT_STARTED for r in summary["runs"]))
        self.assertEqual(summary["completed"], 0)
        self.assertFalse(summary["gate_passed"])

    def test_deadline_at_exactly_360_is_not_started(self):
        summary = b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(win_payload()),
            clock=FakeClock([0, 360]),
        )
        self.assertTrue(all(r["status"] == b.STATUS_NOT_STARTED for r in summary["runs"]))


class ProvenanceTests(FixtureCase):
    def test_records_keep_provenance_paths_and_runtime(self):
        calls = []
        b.run_probe(
            run=True,
            source_dir=self.fixture,
            output_root=self.out,
            runner=fake_runner(win_payload(), calls=calls),
            clock=FakeClock([0]),
        )
        summary = json.loads((self.out / "summary.json").read_text(encoding="utf-8"))
        command_by_label = {_label(call["command"]): call["command"] for call in calls}
        for record in summary["runs"]:
            provenance = record["provenance"]
            self.assertIsNotNone(provenance["input_sha256"])
            self.assertIsNotNone(provenance["outer_request_sha256"])
            for key in ("harness", "dotnet", "driver"):
                self.assertIn("sha256", provenance[key])
            self.assertIsNotNone(record["harness_result_sha256"])
            self.assertTrue(record["stdout_path"])
            self.assertTrue(record["stderr_path"])
            self.assertTrue(record["raw_result_path"])
            self.assertEqual(record["args"], command_by_label[record["run"]])
            self.assertIsNotNone(record["runtime_seconds"])


class PreparationFailureTests(unittest.TestCase):
    def test_missing_input_preserves_four_error_records_and_summary(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            runner = mock.Mock(side_effect=AssertionError("must not launch"))
            result = b.run_probe(run=True, source_dir=root / "absent",
                                 output_root=root / "out", runner=runner)
            self.assertEqual(len(result["runs"]), 4)
            self.assertFalse(result["gate_passed"])
            self.assertTrue(all(r["status"] == "error" for r in result["runs"]))
            self.assertTrue((root / "out" / "summary.json").exists())
            runner.assert_not_called()

    def test_input_preparation_consumes_batch_budget(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fixture = write_fixture(root / "source")
            runner = mock.Mock(side_effect=AssertionError("deadline expired"))
            record = b.execute_run("sequential-1", root / "run", source_dir=fixture,
                                   runner=runner, clock=FakeClock([350, 361]), start=0,
                                   dry_run=False, provenance={})
            self.assertEqual(record["status"], "not_started")
            runner.assert_not_called()


if __name__ == "__main__":
    unittest.main()
