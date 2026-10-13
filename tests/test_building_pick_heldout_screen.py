#!/usr/bin/env python3
"""Offline tests for scripts/building_pick_heldout_screen.py.

The tests reuse the accepted fixture factories from
``test_building_pick_parallel_check`` (temporary runtime, accepted source tree,
raw-verified baseline summary, fake ``subprocess.run``, thread-safe clock and
concurrency tracker). The frozen protocol and the reviewed selection artifact
are constructed in the test tree and injected with their real SHA256; no battle
is ever started and no held-out scan is performed.

Run with:
    python -m unittest discover -s tests -p test_building_pick_heldout_screen.py
"""

import hashlib
import io
import json
import subprocess
import sys
import threading
import unittest
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parent.parent
for _path in (str(REPO_ROOT / "scripts"), str(Path(__file__).resolve().parent)):
    if _path not in sys.path:
        sys.path.insert(0, _path)

import building_pick_heldout_screen as h  # noqa: E402
import building_pick_node_repeat as n  # noqa: E402
import test_building_pick_parallel_check as base  # noqa: E402

SEED_A, SEED_B = h.HELDOUT_SEEDS


class OwnerPairReviewTests(unittest.TestCase):
    def pair(self, selected_hp, baseline_hp):
        fixture = "silent-starter-proxy"
        entries = {}
        for action, hp in (("skip", selected_hp), ("BACKFLIP", baseline_hp)):
            entries[(fixture, action, SEED_A)] = {
                "available": True, "agreement": True,
                "rep1": {"outcome_status": "win", "player_hp": hp},
            }
        return h._pair_entry(fixture, SEED_A, entries, True, None)

    def test_invalid_winning_hp_is_unavailable_not_tie(self):
        for hp in (None, True, float("nan"), float("inf"), "50"):
            with self.subTest(hp=hp):
                pair = self.pair(hp, 50)
                self.assertFalse(pair["available"])
                agg = h.aggregate_pairs([pair])
                self.assertEqual(agg["equal"], 0)
                self.assertFalse(agg["all_planned_pairs_available"])

    def test_valid_numeric_hp_orders_and_ties(self):
        pairs = [self.pair(50, 50), self.pair(50.5, 50), self.pair(49, 50), self.pair(50, 50)]
        agg = h.aggregate_pairs(pairs)
        self.assertTrue(agg["all_planned_pairs_available"])
        self.assertEqual((agg["better"], agg["equal"], agg["worse"]), (1, 2, 1))
        self.assertEqual(agg["co_win_hp_denominator"], 4)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(Path(path).read_bytes())


def write_json(path: Path, data) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=1), encoding="utf-8")
    return path


def make_selection(root: Path, *, selected=None, build_value=None, gate=True,
                   heldout_used=False, production=False) -> tuple:
    selected = selected or {}
    build_value = build_value or {}
    fixtures = [
        {
            "fixture": fixture_id,
            "gate_passed": gate,
            "selected_action": selected.get(fixture_id, mapping["selected"]),
            "build_value_action": build_value.get(fixture_id, mapping["baseline"]),
        }
        for fixture_id, mapping in h.APPROVED.items()
    ]
    selection = {
        "generated_at": "2026-09-17T00:00:00.000000+00:00", "fixtures": fixtures,
        "heldout_used": heldout_used, "production_adoption": production,
        "rule": "work/pick-fixtures/fake-rule.json", "rule_sha256": "0" * 64,
    }
    path = write_json(Path(root) / "selection.json", selection)
    return path, sha256_file(path)


def make_protocol(root: Path, selection_path: Path, selection_sha: str,
                  **changes) -> Path:
    protocol = {
        "frozen_at": "2026-09-17T15:46:18.217439+00:00",
        "selection_path": str(selection_path), "selection_sha256": selection_sha,
        "heldout_seeds": list(h.HELDOUT_SEEDS), "actions": json.loads(json.dumps(h.APPROVED)),
        "replicates": 2, "max_launches": 16, "parallelism": 16, "dop": 2, "nodes": 6000,
        "soft_budget_ms": 120000, "search_timeout_ms": 180000, "max_turns": 40,
        "process_timeout_seconds": 300, "batch_deadline_seconds": 900,
        "pair_gate": "gate", "comparison": "compare", "aggregation": "aggregate",
        "reselection": False, "training": False, "production_adoption": False,
    }
    protocol.update(changes)
    return write_json(Path(root) / "heldout-protocol.json", protocol)


class HeldoutCase(base.FixtureCase):
    def setUp(self):
        super().setUp()
        self.selection_path, self.selection_sha = make_selection(self.tmp)
        self.protocol_path = make_protocol(self.tmp, self.selection_path, self.selection_sha)
        self.specs = h.build_heldout_specs(self.ordered)
        self.runs = h.build_heldout_runs(
            self.specs, self.tmp / "planruns",
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
        )
        self.by_name = {run["name"]: run for run in self.runs}

    def name_for(self, fixture, action, seed, rep) -> str:
        return f"{fixture}-{action}-{seed}-b{h.SOFT_BUDGET_MS}-r{rep}"

    def runner(self, **kwargs):
        return base.make_fake_runner(self.runs, self.fixtures, **kwargs)

    def run_heldout(self, *, out=None, run=True, **kwargs):
        kwargs.setdefault("source_dir", self.source_dir)
        kwargs.setdefault("baseline_dir", self.baseline_dir)
        kwargs.setdefault("historical_dir", self.historical)
        kwargs.setdefault("protocol_path", self.protocol_path)
        kwargs.setdefault("selection_path", self.selection_path)
        kwargs.setdefault("selection_sha256", self.selection_sha)
        kwargs.setdefault("harness_path", self.runtime["harness"])
        kwargs.setdefault("dotnet_path", self.runtime["dotnet"])
        kwargs.setdefault("runtime_config_path", self.runtime["config"])
        kwargs.setdefault("runtime_env", {})
        kwargs.setdefault("output_root", out if out is not None else self._out())
        kwargs.setdefault("clock", base.ThreadSafeFakeClock([0]))
        return h.run_heldout_screen(run=run, **kwargs)

    def outcome_spec(self, status, hp=50, enemy_hp=0, turns=6):
        return {"status": status, "player_hp": hp, "max_hp": 70,
                "final_enemy_hp": enemy_hp, "turns": turns}

    def pair_outcomes(self, fixture, seed, selected_status, selected_hp,
                      baseline_status, baseline_hp) -> dict:
        mapping = h.APPROVED[fixture]
        overrides = {}
        for action, status, hp in (
            (mapping["selected"], selected_status, selected_hp),
            (mapping["baseline"], baseline_status, baseline_hp),
        ):
            for rep in h.REPS:
                enemy_hp = 0 if status == "win" else 20
                overrides[self.name_for(fixture, action, seed, rep)] = self.outcome_spec(
                    status, hp, enemy_hp)
        return overrides

    def pair(self, summary, fixture, seed):
        return next(p for p in summary["pairs"]
                    if p["fixture"] == fixture and p["seed"] == seed)


class ConfigurationTests(unittest.TestCase):
    def test_fixed_constants(self):
        self.assertEqual(h.MAX_LAUNCHES, 16)
        self.assertEqual(h.BASE_COMBINATIONS, 4)
        self.assertEqual(tuple(h.HELDOUT_SEEDS), ("PICKEVAL01", "PICKEVAL02"))
        self.assertEqual(tuple(h.REPS), (1, 2))
        self.assertEqual(h.SOFT_BUDGET_MS, 120000)
        self.assertEqual(h.SEARCH_TIMEOUT_MS, "180000")
        self.assertEqual(h.BASE_BUDGET_MS, 20000)
        self.assertEqual(h.DOP, "2")
        self.assertEqual(h.NODES, "6000")
        self.assertEqual(h.MAX_TURNS, "40")
        self.assertEqual(h.PARALLELISM, 16)
        self.assertEqual(h.PHASE_TIMEOUT_SECONDS, 300)
        self.assertEqual(h.BATCH_DEADLINE_SECONDS, 900)
        self.assertEqual(h.APPROVED["silent-starter-proxy"],
                         {"selected": "skip", "baseline": "BACKFLIP"})
        self.assertEqual(h.APPROVED["silent-late-pm001-proxy"],
                         {"selected": "BACKFLIP", "baseline": "DAGGER_SPRAY"})

    def test_frozen_selection_sha256_constant(self):
        self.assertEqual(
            h.SELECTION_SHA256,
            "b970a7b5eef9fdf9f9b79332b67da36482ee85e1000d538e47824a3b2d9da942")


class ProtocolTests(HeldoutCase):
    def _run(self, out, **kwargs):
        calls = []
        runner = self.runner()

        def spy(command, **opts):
            calls.append(command)
            return runner(command, **opts)

        with self.assertRaises(h.SourceValidationError):
            self.run_heldout(out=out, run=True, runner=spy, **kwargs)
        self.assertEqual(calls, [])
        self.assertFalse(out.exists())

    def test_tampered_selection_hash_rejected_before_launch(self):
        self.selection_path.write_text(
            self.selection_path.read_text(encoding="utf-8") + " ", encoding="utf-8")
        self._run(self._out())

    def test_selected_action_mapping_mismatch_rejected(self):
        path, sha = make_selection(
            self.tmp / "bad-map", selected={"silent-starter-proxy": "DAGGER_SPRAY"})
        self._run(self._out(), selection_path=path, selection_sha256=sha)

    def test_adapter_build_value_mismatch_rejected(self):
        path, sha = make_selection(
            self.tmp / "bad-bv", build_value={"silent-late-pm001-proxy": "BACKFLIP"})
        self._run(self._out(), selection_path=path, selection_sha256=sha)

    def test_protocol_limit_mismatch_rejected(self):
        bad_protocol = make_protocol(
            self.tmp / "bad-proto", self.selection_path, self.selection_sha, max_launches=8)
        self._run(self._out(), protocol_path=bad_protocol)

    def test_selection_claims_production_adoption_rejected(self):
        path, sha = make_selection(self.tmp / "prod", production=True)
        self._run(self._out(), selection_path=path, selection_sha256=sha)

    def test_approved_specs_filter_and_clone(self):
        self.assertEqual(len(self.specs), 16)
        combos = {h._action(spec) for spec in self.specs}
        self.assertEqual(combos, {"skip", "BACKFLIP", "DAGGER_SPRAY"})
        self.assertEqual({spec["seed"] for spec in self.specs}, set(h.HELDOUT_SEEDS))
        self.assertEqual({spec["budget_ms"] for spec in self.specs}, {120000})
        self.assertEqual(len({(s["fixture"], h._action(s), s["seed"], s["rep"]) for s in self.specs}), 16)


class PlanTests(HeldoutCase):
    def test_sixteen_unique_runs_and_commands(self):
        self.assertEqual(len(self.runs), 16)
        self.assertEqual(len({run["name"] for run in self.runs}), 16)
        self.assertEqual(len({tuple(run["command"]) for run in self.runs}), 16)
        self.assertTrue(all(run["phase"] == "parallel" for run in self.runs))
        counts = {}
        for run in self.runs:
            counts[run["base_name"]] = counts.get(run["base_name"], 0) + 1
        self.assertEqual(len(counts), 8)
        self.assertTrue(all(count == 2 for count in counts.values()))

    def test_commands_use_frozen_flags(self):
        for run in self.runs:
            command = run["command"]
            self.assertEqual(command[command.index("--budget-ms") + 1], "120000")
            self.assertEqual(command[command.index("--search-timeout-ms") + 1], "180000")
            self.assertEqual(command[command.index("--dop") + 1], "2")
            self.assertEqual(command[command.index("--nodes") + 1], "6000")
            self.assertEqual(command[command.index("--max-turns") + 1], "40")

    def test_commands_differ_from_baseline_only_by_frozen_flags_and_paths(self):
        baseline = json.loads((self.baseline_dir / "summary.json").read_text(encoding="utf-8"))
        by_cell = {}
        for run in baseline["plan"]["runs"]:
            by_cell[(run["fixture"], base.b._action_key(run), run["budget_ms"])] = run["command"]
        for run in self.runs:
            reference = by_cell[(run["fixture"], h._action(run), h.BASE_BUDGET_MS)]
            diffs = {index for index, (left, right) in enumerate(zip(reference, run["command"]))
                     if left != right}
            allowed = {reference.index("--budget-ms") + 1,
                       reference.index("--search-timeout-ms") + 1,
                       reference.index("--dop") + 1,
                       reference.index("--request") + 1,
                       reference.index("--out") + 1,
                       reference.index("--label") + 1}
            self.assertTrue(diffs <= allowed, msg=f"{run['name']}: {diffs}")

    def test_default_dry_run_launches_nothing(self):
        out = self._out()
        with mock.patch.object(h.subprocess, "run") as run:
            summary = self.run_heldout(out=out, run=False)
        run.assert_not_called()
        self.assertFalse(summary["executed"])
        self.assertEqual(len(summary["runs"]), 16)
        self.assertEqual([r["status"] for r in summary["runs"]], [h.STATUS_DRY_RUN] * 16)
        self.assertEqual(summary["performance"]["peak_actual_overlap"], 0)
        self.assertTrue((out / "plan.json").exists())
        self.assertTrue((out / "summary.json").exists())
        self.assertTrue((out / "journal.jsonl").exists())
        self.assertFalse(summary["repeat_agreement"]["comparisons_enabled"])
        self.assertEqual(summary["pair_aggregation"]["available"], 0)

    def test_refuses_existing_output_directory(self):
        out = self._out()
        out.mkdir(parents=True)
        with self.assertRaises(FileExistsError):
            self.run_heldout(out=out, run=True, runner=self.runner())
        self.assertFalse(any(out.iterdir()))

    def test_main_dryonly_alias_launches_nothing(self):
        out = self._out()
        with mock.patch.object(h, "SOURCE_DIR", self.source_dir), \
                mock.patch.object(h, "BASELINE_DIR", self.baseline_dir), \
                mock.patch.object(h, "HISTORICAL_DIR", self.historical), \
                mock.patch.object(h, "PROTOCOL_PATH", self.protocol_path), \
                mock.patch.object(h, "SELECTION_SHA256", self.selection_sha), \
                mock.patch.object(h, "HARNESS", self.runtime["harness"]), \
                mock.patch.object(h, "DOTNET", self.runtime["dotnet"]), \
                mock.patch.object(h, "default_output_root", return_value=out), \
                mock.patch.object(h.subprocess, "run") as run, \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = h.main(["dryonly"])
        run.assert_not_called()
        self.assertEqual(code, 0)
        self.assertTrue((out / "plan.json").exists())


class InputTests(HeldoutCase):
    def test_generated_input_matches_seed_remap_and_repeats_byte_equal(self):
        out = self._out()
        self.run_heldout(out=out, run=True, runner=self.runner())
        expected = h.expected_baseline_inputs(
            base.b.load_baseline(self.baseline_dir), self.runs)
        by_base = {}
        for run in self.runs:
            path = out / "parallel" / run["name"] / "input.json"
            digest = sha256_file(path)
            self.assertEqual(digest, expected[(run["fixture"], h._action(run), run["seed"])])
            by_base.setdefault(run["base_name"], set()).add(digest)
        self.assertTrue(all(len(digests) == 1 for digests in by_base.values()))

    def test_generated_input_changes_only_seed_and_request_seed_matches(self):
        out = self._out()
        self.run_heldout(out=out, run=True, runner=self.runner())
        historical = json.loads((self.historical / "outer-request.json").read_text(encoding="utf-8"))
        for run in self.runs:
            run_dir = out / "parallel" / run["name"]
            generated = json.loads((run_dir / "input.json").read_text(encoding="utf-8"))
            source = json.loads(Path(run["scenario_path"]).read_text(encoding="utf-8"))
            self.assertEqual(generated["seed"], run["seed"])
            self.assertEqual(h.ranking.diff_paths(source, generated), {"seed"})
            request = json.loads((run_dir / "outer-request.json").read_text(encoding="utf-8"))
            self.assertEqual(request["seed"], run["seed"])
            expected = h.brp.sanitize_request(historical, run_dir, run["name"])
            expected["seed"] = run["seed"]
            expected["characterId"] = run["character"]
            self.assertEqual(h.ranking.diff_paths(expected, request), set())

    def test_generated_input_rejects_foreign_source_seed(self):
        run = dict(self.runs[0])
        run["source_seed"] = "NOT-THE-SOURCE"
        with self.assertRaises(h.SourceValidationError):
            h.prepare_heldout_input(run, self._out(), self.historical / "outer-request.json", {})


class ExecutionTests(HeldoutCase):
    def test_all_sixteen_verified_and_policy(self):
        summary = self.run_heldout(run=True, runner=self.runner())
        self.assertEqual(summary["verified"], 16)
        self.assertEqual(summary["policy_verified"], 16)
        self.assertEqual(summary["eligible_completed"], 16)
        self.assertTrue(summary["tool_completed"])
        self.assertEqual(summary["batch"]["attempts_accounted"], True)
        for record in summary["runs"]:
            self.assertTrue(record["policy"]["ok"], msg=record["run"])
            self.assertEqual(record["policy"]["budget_ms"], h.SOFT_BUDGET_MS)

    def test_peak_actual_overlap_is_sixteen(self):
        concurrency = base.Concurrency()
        summary = self.run_heldout(
            run=True,
            runner=self.runner(concurrency=concurrency, barrier=threading.Barrier(16)),
        )
        self.assertEqual(summary["performance"]["peak_actual_overlap"], 16)
        self.assertEqual(concurrency.state["parallel"]["max"], 16)
        self.assertEqual(len(concurrency.calls), 16)

    def test_journal_has_one_line_per_attempt(self):
        out = self._out()
        self.run_heldout(out=out, run=True, runner=self.runner())
        lines = [line for line in (out / "journal.jsonl").read_text(encoding="utf-8").splitlines()
                 if line.strip()]
        self.assertEqual(len(lines), 16)
        self.assertEqual(len({json.loads(line)["run"] for line in lines}), 16)

    def test_deadline_marks_all_not_started_without_retry(self):
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            return self.runner()(command, **kwargs)

        summary = self.run_heldout(run=True, runner=spy,
                                   clock=base.ThreadSafeFakeClock([0, 1000]))
        self.assertEqual(calls, [])
        self.assertEqual([r["status"] for r in summary["runs"]],
                         [h.parallel.STATUS_NOT_STARTED] * 16)
        self.assertEqual(summary["verified"], 0)


class FailureTests(HeldoutCase):
    def _assert_suppressed(self, summary, fixture, seed):
        pair = self.pair(summary, fixture, seed)
        self.assertFalse(pair["available"])
        self.assertIsNone(pair["result"])

    def test_wrong_actual_seed_is_mismatch_not_loss(self):
        label = self.runs[0]["name"]

        def mutate(name, loadout):
            if name == label:
                loadout["seed"] = "WRONG"

        summary = self.run_heldout(run=True, runner=self.runner(mutate=mutate))
        record = next(r for r in summary["runs"] if r["run"] == label)
        self.assertEqual(record["status"], h.parallel.STATUS_MISMATCH)
        self.assertNotEqual(record["outcome"]["status"], "loss")
        self.assertFalse(record["eligible"])
        self._assert_suppressed(summary, self.runs[0]["fixture"], self.runs[0]["seed"])

    def test_missing_result_is_not_loss_and_suppresses_pair(self):
        label = self.runs[0]["name"]
        summary = self.run_heldout(run=True, runner=self.runner(drop_result=(label,)))
        record = next(r for r in summary["runs"] if r["run"] == label)
        self.assertEqual(record["status"], h.parallel.STATUS_MISSING)
        self.assertNotEqual(record["outcome"]["status"], "loss")
        self._assert_suppressed(summary, self.runs[0]["fixture"], self.runs[0]["seed"])

    def test_timeout_is_not_loss_and_suppresses_pair(self):
        label = self.runs[0]["name"]
        summary = self.run_heldout(run=True, runner=self.runner(timeout_labels=(label,)))
        record = next(r for r in summary["runs"] if r["run"] == label)
        self.assertEqual(record["status"], h.parallel.STATUS_TIMEOUT)
        self.assertEqual(record["outcome"]["status"], "timeout")
        self._assert_suppressed(summary, self.runs[0]["fixture"], self.runs[0]["seed"])

    def test_incomplete_is_not_loss_and_suppresses_pair(self):
        run = self.runs[0]
        label = run["name"]
        summary = self.run_heldout(run=True, runner=self.runner(outcomes={
            label: self.outcome_spec("incomplete", hp=50, enemy_hp=10, turns=5)}))
        record = next(r for r in summary["runs"] if r["run"] == label)
        self.assertEqual(record["outcome"]["status"], "incomplete")
        self.assertNotEqual(record["outcome"]["status"], "loss")
        self.assertFalse(record["eligible"])
        self._assert_suppressed(summary, run["fixture"], run["seed"])


class AgreementTests(HeldoutCase):
    def test_repeat_disagreement_suppresses_only_its_pair(self):
        run = self.runs[0]
        label = run["name"] if run["rep"] == 2 else self.name_for(
            run["fixture"], h._action(run), run["seed"], 2)
        summary = self.run_heldout(run=True, runner=self.runner(
            outcomes={label: self.outcome_spec("win", hp=10, enemy_hp=0, turns=9)}))
        pair = self.pair(summary, run["fixture"], run["seed"])
        self.assertFalse(pair["available"])
        self.assertIn("repeat", pair["reason"])
        self.assertGreaterEqual(summary["repeat_agreement"]["disagreed"], 1)
        other = next(p for p in summary["pairs"]
                     if not (p["fixture"] == run["fixture"] and p["seed"] == run["seed"]))
        self.assertTrue(other["available"])

    def test_identical_repeats_agree_eight_groups(self):
        summary = self.run_heldout(run=True, runner=self.runner())
        agreement = summary["repeat_agreement"]
        self.assertTrue(agreement["comparisons_enabled"])
        self.assertEqual(agreement["total"], 8)
        self.assertEqual(agreement["available"], 8)
        self.assertEqual(agreement["agreed"], 8)

    def test_pair_aggregation_same_win_both_loss_and_discordant(self):
        outcomes = {}
        starter, late = "silent-starter-proxy", "silent-late-pm001-proxy"
        outcomes.update(self.pair_outcomes(starter, SEED_A, "win", 60, "loss", 0))
        outcomes.update(self.pair_outcomes(starter, SEED_B, "loss", 0, "win", 55))
        outcomes.update(self.pair_outcomes(late, SEED_A, "win", 50, "win", 50))
        outcomes.update(self.pair_outcomes(late, SEED_B, "loss", 0, "loss", 0))
        summary = self.run_heldout(run=True, runner=self.runner(outcomes=outcomes))
        agg = summary["pair_aggregation"]
        self.assertEqual(agg["total_pairs"], 4)
        self.assertEqual(agg["available"], 4)
        self.assertEqual(agg["better"], 1)
        self.assertEqual(agg["worse"], 1)
        self.assertEqual(agg["equal"], 2)
        self.assertEqual(agg["selected_win_baseline_loss"], 1)
        self.assertEqual(agg["selected_loss_baseline_win"], 1)
        self.assertEqual(agg["both_win"], 1)
        self.assertEqual(agg["co_win_hp_deltas"], [0])
        self.assertEqual(agg["co_win_hp_denominator"], 1)
        self.assertFalse(agg["repeats_are_independent_samples"])
        win = self.pair(summary, starter, SEED_A)
        self.assertEqual(win["result"], "selected_better")
        self.assertTrue(self.pair(summary, late, SEED_B)["result"] == "tie")


class RuntimeTests(HeldoutCase):
    def test_runtime_preflight_mismatch_aborts_before_launch(self):
        changed = json.loads(json.dumps(base.b.load_baseline(self.baseline_dir)["runtime"]))
        changed["dotnet"]["sha256"] = "e" * 64
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            return self.runner()(command, **kwargs)

        out = self._out()
        with mock.patch.object(h.ranking, "collect_runtime_provenance", return_value=changed):
            with self.assertRaises(h.RuntimeValidationError):
                self.run_heldout(out=out, run=True, runner=spy)
        self.assertEqual(calls, [])
        self.assertFalse(out.exists())

    def test_runtime_change_between_phases_disables_comparisons(self):
        baseline_runtime = json.loads(
            (self.baseline_dir / "summary.json").read_text(encoding="utf-8"))["runtime"]
        changed = json.loads(json.dumps(baseline_runtime))
        changed["harness"]["sha256"] = "f" * 64
        with mock.patch.object(
            h.ranking, "collect_runtime_provenance", side_effect=[baseline_runtime, changed]):
            summary = self.run_heldout(run=True, runner=self.runner())
        self.assertFalse(summary["runtime"]["stable"])
        self.assertFalse(summary["repeat_agreement"]["comparisons_enabled"])
        self.assertEqual(summary["repeat_agreement"]["available"], 0)
        self.assertEqual(summary["pair_aggregation"]["available"], 0)
        self.assertEqual(summary["status_counts"]["dry_run"], 0)


class PerformanceTests(HeldoutCase):
    def test_missing_cpu_suppresses_utilization(self):
        label = self.runs[0]["name"]
        summary = self.run_heldout(run=True, runner=self.runner(omit_cpu=(label,)))
        cpu = summary["performance"]["cpu"]
        self.assertEqual(cpu["runs_missing"], 1)
        self.assertFalse(cpu["cpu_complete"])
        self.assertIsNone(cpu["average_busy_cores"])
        self.assertIsNone(cpu["estimated_child_utilization"])

    def test_initial_search_never_claims_whole_fight_node_only(self):
        summary = self.run_heldout(run=True, runner=self.runner())
        self.assertFalse(summary["initial_search"]["whole_fight_node_only_established"])
        self.assertIsNotNone(summary["initial_search"])


class DevEntryTests(HeldoutCase):
    def test_dev_entry_still_rejects_heldout_seed(self):
        with self.assertRaises(h.SourceValidationError):
            n.validate_run_seed(SEED_A)
        out = self._out()
        with self.assertRaises(h.SourceValidationError):
            n.run_node_repeat(run=False, seed=SEED_A, output_root=out,
                              source_dir=self.source_dir, baseline_dir=self.baseline_dir,
                              historical_dir=self.historical)
        self.assertFalse(out.exists())


class GlobalStateTests(HeldoutCase):
    def test_module_globals_are_not_mutated(self):
        before = {"DOP": h.DOP, "SOFT_BUDGET_MS": h.SOFT_BUDGET_MS,
                  "SEARCH_TIMEOUT_MS": h.SEARCH_TIMEOUT_MS, "NODES": h.NODES,
                  "REPS": tuple(h.REPS), "HELDOUT_SEEDS": tuple(h.HELDOUT_SEEDS),
                  "APPROVED": json.dumps(h.APPROVED, sort_keys=True)}
        ranking_dop = h.ranking.DOP
        parallel_dop = h.parallel.DOP
        self.run_heldout(run=True, runner=self.runner())
        self.assertEqual(before["DOP"], h.DOP)
        self.assertEqual(before["SOFT_BUDGET_MS"], h.SOFT_BUDGET_MS)
        self.assertEqual(before["SEARCH_TIMEOUT_MS"], h.SEARCH_TIMEOUT_MS)
        self.assertEqual(before["NODES"], h.NODES)
        self.assertEqual(before["REPS"], tuple(h.REPS))
        self.assertEqual(before["HELDOUT_SEEDS"], tuple(h.HELDOUT_SEEDS))
        self.assertEqual(before["APPROVED"], json.dumps(h.APPROVED, sort_keys=True))
        self.assertEqual(ranking_dop, h.ranking.DOP)
        self.assertEqual(h.ranking.DOP, "1")
        self.assertEqual(parallel_dop, h.parallel.DOP)
        self.assertEqual(h.parallel.DOP, "2")

    def test_inherited_environment_is_never_logged(self):
        with mock.patch.object(h.brp, "build_env", return_value={"SECRET_SENTINEL": "leak-me"}):
            summary = self.run_heldout(run=True, runner=self.runner())
        encoded = json.dumps(summary)
        self.assertNotIn("leak-me", encoded)
        self.assertNotIn("SECRET_SENTINEL", encoded)

    def test_cli_rejects_action_and_seed_overrides(self):
        for argv in (["--seed", "PICKEVAL01"], ["--action", "skip"], ["--dop", "4"]):
            with mock.patch("sys.stderr", new=io.StringIO()):
                with self.assertRaises(SystemExit) as ctx:
                    h.main(argv)
            self.assertEqual(ctx.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
