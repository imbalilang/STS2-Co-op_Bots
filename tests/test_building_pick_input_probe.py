#!/usr/bin/env python3
"""Offline tests for scripts/building_pick_input_probe.py.

Every test injects a fake ``subprocess.run`` and uses a temporary accepted-source
tree, so no battle is ever started. The tests pin the fixed instrument before it
is allowed near the game:

* the default command launches nothing and writes only the plan;
* exactly eight single-shot actions (two fixtures x skip + three additions) with
  the fixed one-turn command (``--max-turns 1``, 4000 ms, dop 1, nodes 6000);
* each frozen scenario is copied byte-for-byte and the outer request carries the
  *scenario's* seed/character (not the helper's hardcoded PM001);
* the actual loadout deck multiset (``currentUpgradeLevel``) equals the adapter
  deck plus exactly the candidate on add, and skip is unchanged;
* every context field and the deck fail closed with an explicit reason;
* missing/malformed evidence, timeouts, nonzero exits and launch errors are
  reported distinctly and never as a verified input or a loss;
* the batch deadline prevents launches and is charged for input preparation;
* source hash mismatches abort before any launch, and an existing output
  directory is never overwritten.

Run with:
    python -m unittest discover -s tests -p test_building_pick_input_probe.py
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

import building_pick_input_probe as b  # noqa: E402

CANDIDATES = ["BACKFLIP", "BLADE_DANCE", "DAGGER_SPRAY"]
FIXTURES = [
    {
        "id": "silent-starter-proxy",
        "seed": "PICKPROXY01",
        "act_index": 0,
        "encounter": "FUZZY_WURM_CRAWLER_WEAK",
        "deck": [("STRIKE_SILENT", 0), ("DEFEND_SILENT", 0), ("NEUTRALIZE", 0)],
    },
    {
        "id": "silent-late-pm001-proxy",
        "seed": "PM001",
        "act_index": 2,
        "encounter": "SOUL_NEXUS_ELITE",
        "deck": [("DEFEND_SILENT", 0), ("BACKFLIP", 1), ("ASCENDERS_BANE", 0)],
    },
]

EXPECTED_NAMES = [
    "silent-starter-proxy-skip",
    "silent-starter-proxy-add-BACKFLIP",
    "silent-starter-proxy-add-BLADE_DANCE",
    "silent-starter-proxy-add-DAGGER_SPRAY",
    "silent-late-pm001-proxy-skip",
    "silent-late-pm001-proxy-add-BACKFLIP",
    "silent-late-pm001-proxy-add-BLADE_DANCE",
    "silent-late-pm001-proxy-add-DAGGER_SPRAY",
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


def build_source(root: Path) -> Path:
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
                "characterId": "SILENT",
                "verifiedCharacterId": "SILENT",
                "ascension": 10,
                "verifiedAscension": 10,
                "actIndex": fixture["act_index"],
                "verifiedActIndex": fixture["act_index"],
                "encounterId": fixture["encounter"],
                "maxHp": 70,
                "currentHp": 70,
                "verifiedMaxHp": 70,
                "verifiedCurrentHp": 70,
                "relics": [],
                "potions": [],
                "deckSize": len(deck),
                "deck": deck,
                "candidates": candidates,
            }
        )

        actions = []
        for label, candidate in [("skip", None)] + [
            (f"add-{candidate}", candidate) for candidate in CANDIDATES
        ]:
            scenario = {
                "schemaVersion": 1,
                "seed": fixture["seed"],
                "characterId": "SILENT",
                "ascension": 10,
                "actIndex": fixture["act_index"],
                "encounterId": fixture["encounter"],
                "playerCurrentHp": 70,
                "includeStartingDeck": False,
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
                    "characterId": "SILENT",
                    "ascension": 10,
                    "actIndex": fixture["act_index"],
                    "encounterId": fixture["encounter"],
                },
                "actions": actions,
            }
        )

    adapter = {
        "schemaVersion": 1,
        "tool": "BuildingDecisionProbe",
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
            "characterId": "SILENT",
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
            "ascension": 10,
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


def _runtime(source):
    loaded = b.load_source(source)
    specs = {spec["name"]: spec for spec in b.build_specs(loaded)}
    fixtures = {fixture["id"]: fixture for fixture in loaded["adapter"]["fixtures"]}
    return specs, fixtures


def good_harness(spec):
    return {
        "resolvedScenario": {
            "SchemaVersion": 1,
            "Seed": spec["seed"],
            "CharacterId": "SILENT",
            "Ascension": 10,
            "ActIndex": spec["act_index"],
            "EncounterId": spec["encounter"],
        },
        "fightTruncated": None,
        "fightWallMs": 1234,
        "afterTurns": (
            "turn=1 round=1 phase=Play side=Player energy=3 hp=70/70 hand=[] "
            "draw=29 discard=0 relics=[] "
            "enemies=[SOUL_NEXUS#1:254/254@SOUL_BURN_MOVE] total_floor=1 act_floor=0"
        ),
    }


def good_loadout(spec, adapter_fixture):
    deck = [
        {"id": entry["id"], "currentUpgradeLevel": entry["upgrade"]}
        for entry in adapter_fixture["deck"]
    ]
    if spec["candidate"] is not None:
        deck.append({"id": spec["candidate"], "currentUpgradeLevel": 0})
    return {
        "schemaVersion": 1,
        "seed": spec["seed"],
        "characterId": "SILENT",
        "ascension": 10,
        "actIndex": spec["act_index"],
        "encounterId": spec["encounter"],
        "playerHp": 70,
        "playerMaxHp": 70,
        "relics": [],
        "potionSlots": [None, None],
        "deck": deck,
    }


def fake_runner(source, *, mutate=None, returncode=0, timeout_labels=(), calls=None,
                raises=None, drop_result=(), drop_loadout=(), malformed_result=(),
                malformed_loadout=()):
    """A subprocess.run stand-in that writes harness + evidence artifacts."""

    specs, fixtures = _runtime(source)

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
        spec = specs[label]

        if label in malformed_result:
            (out_dir / "harness-result.json").write_text("not json", encoding="utf-8")
        elif label not in drop_result:
            write_json(out_dir / "harness-result.json", good_harness(spec))

        if label not in drop_loadout:
            loadout = good_loadout(spec, fixtures[spec["fixture"]])
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
                    "seed": spec["seed"],
                    "characterId": "SILENT",
                    "ascension": 10,
                    "actIndex": spec["act_index"],
                    "encounterId": spec["encounter"],
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
        self.source = build_source(self.tmp)
        self.historical = write_historical(self.tmp)
        self.out = self.tmp / "out"

    def tearDown(self):
        self._tmp.cleanup()

    def run_probe(self, **kwargs):
        kwargs.setdefault("source_dir", self.source)
        kwargs.setdefault("historical_dir", self.historical)
        kwargs.setdefault("output_root", self.out)
        return b.run_probe(**kwargs)


class ReviewRegressionTests(FixtureCase):
    def test_missing_resolved_artifact_fails(self):
        original = fake_runner(self.source)
        def runner(command, **kwargs):
            result = original(command, **kwargs)
            (_out_dir(command) / 'evidence/generated-scenario.resolved.json').unlink()
            return result
        summary = self.run_probe(run=True, runner=runner, clock=FakeClock([0]))
        self.assertEqual(summary['status_counts'][b.STATUS_MISSING], 8)
        self.assertFalse(summary['input_fidelity_verified'])

    def test_absent_exit_code_fails(self):
        summary = self.run_probe(run=True, runner=fake_runner(self.source, returncode=None), clock=FakeClock([0]))
        self.assertEqual(summary['status_counts'][b.STATUS_ERROR], 8)

    def test_missing_hash_rejected_before_launch(self):
        path = self.source / 'cases/manifest.json'
        manifest = json.loads(path.read_text(encoding='utf-8'))
        del manifest['fixtures'][0]['actions'][0]['sha256']
        write_json(path, manifest)
        with self.assertRaises(b.SourceValidationError):
            self.run_probe()

    def test_duplicate_action_rejected_before_launch(self):
        path = self.source / 'cases/manifest.json'
        manifest = json.loads(path.read_text(encoding='utf-8'))
        actions = manifest['fixtures'][0]['actions']
        actions[1] = actions[0]
        write_json(path, manifest)
        with self.assertRaises(b.SourceValidationError):
            self.run_probe()


class ConfigurationTests(unittest.TestCase):
    def test_fixed_limits_and_flags(self):
        self.assertEqual(b.MAX_LAUNCHES, 8)
        self.assertEqual(b.PER_RUN_TIMEOUT_SECONDS, 60)
        self.assertEqual(b.BATCH_DEADLINE_SECONDS, 240)
        self.assertEqual(b.MAX_TURNS, "1")
        self.assertEqual(b.BUDGET_MS, "4000")
        self.assertEqual(b.DOP, "1")
        self.assertEqual(b.NODES, "6000")
        self.assertEqual(b.CHARACTER, "SILENT")
        self.assertEqual(b.ASCENSION, 10)
        self.assertEqual(b.PLAYER_HP, 70)

    def test_compute_timeout_caps_and_decays(self):
        self.assertEqual(b.compute_timeout(0), 60.0)
        self.assertEqual(b.compute_timeout(180), 60.0)
        self.assertEqual(b.compute_timeout(200), 40.0)
        self.assertEqual(b.compute_timeout(239.5), 0.5)
        self.assertEqual(b.compute_timeout(240), 0.0)
        self.assertEqual(b.compute_timeout(300), 0.0)

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
        self.assertEqual(summary["verified"], 0)
        self.assertFalse(summary["input_fidelity_verified"])
        self.assertTrue(summary["tool_completed"])

    def test_default_main_launches_nothing_and_exits_zero(self):
        with mock.patch.object(b, "SOURCE_DIR", self.source), \
                mock.patch.object(b, "HISTORICAL_DIR", self.historical), \
                mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run") as run, \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = b.main([])
        run.assert_not_called()
        self.assertEqual(code, 0)
        self.assertTrue((self.out / "plan.json").exists())

    def test_real_accepted_source_dry_run(self):
        if not b.SOURCE_DIR.exists():
            self.skipTest("accepted fixture source is not present")
        output = self.tmp / "real-out"
        summary = b.run_probe(
            source_dir=b.SOURCE_DIR,
            historical_dir=b.HISTORICAL_DIR,
            output_root=output,
        )
        self.assertEqual([run["run"] for run in summary["runs"]], EXPECTED_NAMES)
        self.assertFalse(summary["executed"])
        self.assertTrue(summary["tool_completed"])
        self.assertEqual(summary["verified"], 0)
        self.assertEqual(
            [run["status"] for run in summary["runs"]], [b.STATUS_DRY_RUN] * 8
        )
        plan = json.loads((output / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual([run["name"] for run in plan["runs"]], EXPECTED_NAMES)
        # The plan records the exact accepted manifest hash it verified.
        manifest = json.loads(
            (b.SOURCE_DIR / "cases" / "manifest.json").read_text(encoding="utf-8")
        )
        self.assertEqual(
            plan["source"]["adapter_sha256"], manifest["source"]["adapterSha256"]
        )
        for run in plan["runs"]:
            self.assertTrue(run["scenario_sha256"])

    def test_plan_lists_eight_actions_with_the_fixed_command(self):
        self.run_probe()
        plan = json.loads((self.out / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual([run["name"] for run in plan["runs"]], EXPECTED_NAMES)
        self.assertEqual(plan["max_launches"], 8)
        self.assertEqual(plan["per_run_timeout_seconds"], 60)
        self.assertEqual(plan["batch_deadline_seconds"], 240)
        for run in plan["runs"]:
            command = run["command"]
            self.assertEqual(run["timeout_seconds"], 60)
            self.assertEqual(command[command.index("--max-turns") + 1], "1")
            self.assertEqual(command[command.index("--budget-ms") + 1], "4000")
            self.assertEqual(command[command.index("--dop") + 1], "1")
            self.assertEqual(command[command.index("--nodes") + 1], "6000")
            self.assertEqual(command[command.index("--profile") + 1], "Medium")
            self.assertEqual(command[command.index("--search-timeout-ms") + 1], "60000")
            self.assertEqual(command[command.index("--milestone") + 1], "M2")
            self.assertIn("--no-card-probe", command)
            self.assertIn("--deploy-plan", command)
            self.assertTrue(run["scenario_sha256"])

    def test_refuses_an_existing_output_directory(self):
        self.out.mkdir(parents=True)
        with mock.patch.object(b.subprocess, "run") as run:
            with self.assertRaises(FileExistsError):
                self.run_probe(run=True)
        run.assert_not_called()


class ActualRunTests(FixtureCase):
    def _run(self, **kwargs):
        calls = []
        summary = self.run_probe(
            run=True,
            runner=fake_runner(self.source, calls=calls, **kwargs),
            clock=FakeClock([0]),
        )
        return summary, calls

    def test_all_eight_actions_verified(self):
        summary, calls = self._run()
        self.assertEqual(len(calls), 8)
        self.assertTrue(summary["input_fidelity_verified"])
        self.assertEqual(summary["verified"], 8)
        self.assertTrue(summary["tool_completed"])
        self.assertNotIn("study_gate_passed", summary)
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.STATUS_VERIFIED)
            self.assertIsNone(record["reason"])
            self.assertTrue(record["launched"])
            self.assertIsNotNone(record["loadout_sha256"])
            self.assertIsNotNone(record["expected_deck_sha256"])
            self.assertIsNotNone(record["actual_deck_sha256"])
            # A one-turn fight is incomplete, never a completed result or a loss.
            self.assertEqual(record["outcome"]["status"], b.be.STATUS_INCOMPLETE)
            self.assertNotIn(record["outcome"]["status"], b.be.COMPLETE_STATUSES)
        self.assertEqual(
            summary["outcome_diagnostics"].get(b.be.STATUS_INCOMPLETE), 8
        )

    def test_subprocess_kwargs_and_sequential_order(self):
        summary, calls = self._run()
        self.assertEqual([_label(call["command"]) for call in calls], EXPECTED_NAMES)
        for call in calls:
            self.assertEqual(call["kwargs"]["cwd"], str(b.CWD))
            self.assertIs(call["kwargs"]["capture_output"], True)
            self.assertNotIn("shell", call["kwargs"])
            self.assertEqual(call["kwargs"]["timeout"], 60.0)
            self.assertEqual(
                call["kwargs"]["env"]["OFFLINE_HARNESS_SOULNEXUS_HP_SCALE"], "1.0"
            )
            self.assertEqual(
                call["kwargs"]["env"]["OFFLINE_HARNESS_SOULNEXUS_DAMAGE_SCALE"], "1.0"
            )
        self.assertEqual(len(summary["runs"]), 8)

    def test_input_is_byte_copy_of_the_frozen_scenario(self):
        self._run()
        manifest = json.loads(
            (self.source / "cases" / "manifest.json").read_text(encoding="utf-8")
        )
        for fixture in manifest["fixtures"]:
            for action in fixture["actions"]:
                label = "skip" if action["kind"] == "skip" else f"add-{action['candidate']}"
                name = f"{fixture['id']}-{label}"
                source_bytes = Path(action["file"]).read_bytes()
                copied = (self.out / name / "input.json").read_bytes()
                self.assertEqual(copied, source_bytes)

    def test_outer_request_uses_scenario_seed_not_pm001(self):
        self._run()
        starter = json.loads(
            (self.out / "silent-starter-proxy-skip" / "outer-request.json").read_text(
                encoding="utf-8"
            )
        )
        late = json.loads(
            (self.out / "silent-late-pm001-proxy-skip" / "outer-request.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(starter["seed"], "PICKPROXY01")
        self.assertEqual(starter["characterId"], "SILENT")
        self.assertEqual(late["seed"], "PM001")
        self.assertNotEqual(starter["seed"], late["seed"])
        self.assertEqual(
            starter["generatedScenarioPath"],
            str(self.out / "silent-starter-proxy-skip" / "input.json"),
        )
        self.assertEqual(
            starter["evidenceDirectory"],
            str(self.out / "silent-starter-proxy-skip" / "evidence"),
        )
        self.assertIs(starter["holdAfterInitialSearch"], False)
        for key in b.brp.SNAPSHOT_KEYS:
            self.assertIsNone(starter[key])
        for key in b.brp.LIST_KEYS:
            self.assertEqual(starter[key], [])

    def test_journal_has_one_line_per_attempt(self):
        self._run()
        lines = [
            line
            for line in (self.out / "journal.jsonl").read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        self.assertEqual(len(lines), 8)
        self.assertEqual([json.loads(line)["run"] for line in lines], EXPECTED_NAMES)

    def test_full_environment_is_never_emitted(self):
        summary, _ = self._run()
        plan = summary["plan"]
        self.assertEqual(set(plan["env_overrides"]), set(b.brp.ENV_OVERRIDES))
        self.assertNotIn("env", plan)
        for record in summary["runs"]:
            self.assertNotIn("env", record)


class ContextMismatchTests(FixtureCase):
    def setUp(self):
        super().setUp()
        self._case_index = 0

    def _first_record(self, mutate, name="silent-starter-proxy-skip"):
        self._case_index += 1
        output = self.tmp / f"out-{self._case_index}"
        summary = b.run_probe(
            run=True,
            source_dir=self.source,
            historical_dir=self.historical,
            output_root=output,
            runner=fake_runner(self.source, mutate=mutate),
            clock=FakeClock([0]),
        )
        return {record["run"]: record for record in summary["runs"]}[name]

    def test_every_context_field_fails_explicitly(self):
        cases = [
            ("seed", "WRONG", "loadout.seed"),
            ("characterId", "IRONCLAD", "loadout.characterId"),
            ("ascension", 3, "loadout.ascension"),
            ("actIndex", 7, "loadout.actIndex"),
            ("encounterId", "AEONGLASS_BOSS", "loadout.encounterId"),
            ("playerHp", 10, "loadout.playerHp"),
            ("playerMaxHp", 99, "loadout.playerMaxHp"),
            ("relics", ["RING_OF_THE_SNAKE"], "loadout.relics"),
            ("potionSlots", [None, "FIRE_POTION"], "potionSlots"),
        ]
        for key, value, fragment in cases:
            with self.subTest(field=key):
                def mutate(label, loadout, key=key, value=value):
                    if label == "silent-starter-proxy-skip":
                        loadout[key] = value

                record = self._first_record(mutate)
                self.assertEqual(record["status"], b.STATUS_MISMATCH)
                self.assertIn(fragment, record["reason"])

    def test_resolved_evidence_mismatch_is_caught(self):
        # Mutate the harness resolved scenario via a targeted runner wrapper.
        specs, fixtures = _runtime(self.source)
        base = fake_runner(self.source)

        def runner(command, **kwargs):
            label = _label(command)
            out_dir = _out_dir(command)
            result = base(command, **kwargs)
            if label == "silent-late-pm001-proxy-skip":
                harness = good_harness(specs[label])
                harness["resolvedScenario"]["Seed"] = "PM999"
                write_json(out_dir / "harness-result.json", harness)
            return result

        summary = b.run_probe(
            run=True,
            source_dir=self.source,
            historical_dir=self.historical,
            output_root=self.tmp / "out-resolved",
            runner=runner,
            clock=FakeClock([0]),
        )
        record = {r["run"]: r for r in summary["runs"]}["silent-late-pm001-proxy-skip"]
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("resolvedScenario.Seed", record["reason"])

    def test_deck_id_mutation_fails(self):
        def mutate(label, loadout):
            if label == "silent-starter-proxy-skip":
                loadout["deck"][0]["id"] = "WRONG_CARD"

        record = self._first_record(mutate)
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("loadout deck differs", record["reason"])

    def test_deck_upgrade_mutation_fails(self):
        def mutate(label, loadout):
            if label == "silent-late-pm001-proxy-skip":
                loadout["deck"][0]["currentUpgradeLevel"] = 2

        record = self._first_record(mutate, name="silent-late-pm001-proxy-skip")
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("loadout deck differs", record["reason"])

    def test_deck_count_mutation_fails(self):
        def mutate(label, loadout):
            if label == "silent-starter-proxy-skip":
                loadout["deck"].pop()

        record = self._first_record(mutate)
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("loadout deck differs", record["reason"])

    def test_missing_candidate_on_add_fails(self):
        def mutate(label, loadout):
            if label == "silent-late-pm001-proxy-add-BACKFLIP":
                loadout["deck"] = [
                    entry
                    for entry in loadout["deck"]
                    if not (entry["id"] == "BACKFLIP" and entry["currentUpgradeLevel"] == 0)
                ]

        record = self._first_record(mutate, name="silent-late-pm001-proxy-add-BACKFLIP")
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("loadout deck differs", record["reason"])


class EvidenceFailureTests(FixtureCase):
    def _records(self, **kwargs):
        summary = self.run_probe(
            run=True,
            runner=fake_runner(self.source, **kwargs),
            clock=FakeClock([0]),
        )
        return {record["run"]: record for record in summary["runs"]}, summary

    def test_missing_loadout_is_missing(self):
        records, summary = self._records(drop_loadout=("silent-late-pm001-proxy-skip",))
        record = records["silent-late-pm001-proxy-skip"]
        self.assertEqual(record["status"], b.STATUS_MISSING)
        self.assertIn("loadout.json absent", record["reason"])
        self.assertFalse(summary["input_fidelity_verified"])

    def test_malformed_loadout_is_mismatch(self):
        records, _ = self._records(malformed_loadout=("silent-starter-proxy-add-BACKFLIP",))
        record = records["silent-starter-proxy-add-BACKFLIP"]
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("malformed", record["reason"])

    def test_missing_result_is_missing(self):
        records, _ = self._records(drop_result=("silent-starter-proxy-skip",))
        record = records["silent-starter-proxy-skip"]
        self.assertEqual(record["status"], b.STATUS_MISSING)
        self.assertIn("harness-result.json absent", record["reason"])

    def test_malformed_result_is_mismatch(self):
        records, _ = self._records(malformed_result=("silent-starter-proxy-skip",))
        record = records["silent-starter-proxy-skip"]
        self.assertEqual(record["status"], b.STATUS_MISMATCH)
        self.assertIn("unreadable harness-result.json", record["reason"])


class ProcessFailureTests(FixtureCase):
    def test_nonzero_exit_is_error(self):
        summary = self.run_probe(
            run=True,
            runner=fake_runner(self.source, returncode=1),
            clock=FakeClock([0]),
        )
        self.assertTrue(all(r["status"] == b.STATUS_ERROR for r in summary["runs"]))
        self.assertTrue(all("nonzero process exit 1" in r["reason"] for r in summary["runs"]))
        self.assertFalse(summary["input_fidelity_verified"])

    def test_timeout_is_reported_with_partial_output(self):
        summary = self.run_probe(
            run=True,
            runner=fake_runner(self.source, timeout_labels=("silent-starter-proxy-skip",)),
            clock=FakeClock([0]),
        )
        record = {r["run"]: r for r in summary["runs"]}["silent-starter-proxy-skip"]
        self.assertEqual(record["status"], b.STATUS_TIMEOUT)
        self.assertTrue(record["timed_out"])
        self.assertEqual(
            (self.out / "silent-starter-proxy-skip" / "stdout.bin").read_bytes(),
            b"partial-out",
        )
        self.assertEqual(
            (self.out / "silent-starter-proxy-skip" / "stderr.bin").read_bytes(),
            b"partial-err",
        )

    def test_launch_oserror_is_error(self):
        summary = self.run_probe(
            run=True,
            runner=fake_runner(self.source, raises=OSError("cannot spawn")),
            clock=FakeClock([0]),
        )
        for record in summary["runs"]:
            self.assertEqual(record["status"], b.STATUS_ERROR)
            self.assertIn("launch failed", record["reason"])


class DeadlineTests(FixtureCase):
    def test_exhausted_deadline_marks_not_started_without_launch(self):
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            raise AssertionError("no subprocess may start after the deadline")

        summary = self.run_probe(run=True, runner=spy, clock=FakeClock([0, 300]))
        self.assertEqual(calls, [])
        self.assertTrue(all(r["status"] == b.STATUS_NOT_STARTED for r in summary["runs"]))
        self.assertFalse(summary["input_fidelity_verified"])
        self.assertTrue(summary["batch"]["attempts_accounted"])

    def test_preparation_time_is_charged_to_the_batch(self):
        calls = []
        # run_probe consumes the first clock value for the batch start.
        summary = self.run_probe(
            run=True,
            runner=fake_runner(self.source, calls=calls),
            clock=FakeClock([0, 230, 250]),
        )
        self.assertEqual(calls, [])
        self.assertTrue(all(r["status"] == b.STATUS_NOT_STARTED for r in summary["runs"]))

    def test_timeout_is_recomputed_after_preparation(self):
        calls = []
        summary = self.run_probe(
            run=True,
            runner=fake_runner(self.source, calls=calls),
            clock=FakeClock([0, 0, 200]),
        )
        self.assertEqual(len(calls), 8)
        self.assertEqual(calls[0]["kwargs"]["timeout"], 40.0)
        self.assertEqual(summary["runs"][0]["timeout_before_preparation_seconds"], 60.0)
        self.assertEqual(summary["runs"][0]["timeout_seconds"], 40.0)


class SourceValidationTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.source = build_source(self.tmp)
        self.historical = write_historical(self.tmp)

    def tearDown(self):
        self._tmp.cleanup()

    def test_adapter_hash_mismatch_aborts_before_launch(self):
        manifest_path = self.source / "cases" / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["source"]["adapterSha256"] = "0" * 64
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with mock.patch.object(b.subprocess, "run") as run:
            with self.assertRaises(b.SourceValidationError):
                b.run_probe(
                    run=True,
                    source_dir=self.source,
                    historical_dir=self.historical,
                    output_root=self.tmp / "out",
                )
        run.assert_not_called()
        self.assertFalse((self.tmp / "out").exists())

    def test_scenario_hash_mismatch_aborts_before_launch(self):
        manifest_path = self.source / "cases" / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["fixtures"][0]["actions"][0]["sha256"] = "1" * 64
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with mock.patch.object(b.subprocess, "run") as run:
            with self.assertRaises(b.SourceValidationError):
                b.run_probe(
                    run=True,
                    source_dir=self.source,
                    historical_dir=self.historical,
                    output_root=self.tmp / "out",
                )
        run.assert_not_called()

    def test_missing_adapter_aborts(self):
        with tempfile.TemporaryDirectory() as tmp:
            empty = Path(tmp)
            with self.assertRaises(b.SourceValidationError):
                b.run_probe(
                    source_dir=empty,
                    historical_dir=self.historical,
                    output_root=empty / "out",
                )

    def test_missing_historical_request_aborts(self):
        with tempfile.TemporaryDirectory() as empty_hist:
            with self.assertRaises(b.SourceValidationError):
                b.run_probe(
                    source_dir=self.source,
                    historical_dir=Path(empty_hist),
                    output_root=self.tmp / "out",
                )

    def test_scenario_outside_source_is_rejected(self):
        manifest_path = self.source / "cases" / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["fixtures"][0]["actions"][0]["file"] = str(
            (self.tmp / "outside.json").resolve()
        )
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(b.SourceValidationError):
            b.run_probe(
                source_dir=self.source,
                historical_dir=self.historical,
                output_root=self.tmp / "out",
            )


class MainExitCodeTests(FixtureCase):
    def test_run_exit_zero_when_all_verified(self):
        with mock.patch.object(b, "SOURCE_DIR", self.source), \
                mock.patch.object(b, "HISTORICAL_DIR", self.historical), \
                mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run",
                                  side_effect=fake_runner(self.source)), \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = b.main(["--run"])
        self.assertEqual(code, 0)

    def test_run_exit_one_on_mismatch(self):
        def mutate(label, loadout):
            if label == "silent-starter-proxy-skip":
                loadout["seed"] = "WRONG"

        with mock.patch.object(b, "SOURCE_DIR", self.source), \
                mock.patch.object(b, "HISTORICAL_DIR", self.historical), \
                mock.patch.object(b, "default_output_root", return_value=self.out), \
                mock.patch.object(b.subprocess, "run",
                                  side_effect=fake_runner(self.source, mutate=mutate)), \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = b.main(["--run"])
        self.assertEqual(code, 1)


if __name__ == "__main__":
    unittest.main()
