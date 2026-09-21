#!/usr/bin/env python3
"""Offline tests for scripts/building_pick_node_repeat.py.

The tests reuse the accepted fixture factories from
``test_building_pick_parallel_check`` (temporary runtime, accepted source tree,
raw-verified baseline summary, fake ``subprocess.run``, thread-safe clock and
concurrency tracker). No battle is ever started.

Run with:
    python -m unittest discover -s tests -p test_building_pick_node_repeat.py
"""

import io
import json
import sys
import unittest
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parent.parent
for _path in (str(REPO_ROOT / "scripts"), str(Path(__file__).resolve().parent)):
    if _path not in sys.path:
        sys.path.insert(0, _path)

import building_pick_node_repeat as n  # noqa: E402
import test_building_pick_parallel_check as base  # noqa: E402


class NodeRepeatCase(base.FixtureCase):
    def setUp(self):
        super().setUp()
        self.runs = n.build_repeat_runs(
            self.ordered, self.tmp / "planruns",
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
        )
        self.by_name = {run["name"]: run for run in self.runs}

    def runner(self, **kwargs):
        return base.make_fake_runner(self.runs, self.fixtures, **kwargs)

    def run_nodes(self, *, out=None, run=True, **kwargs):
        kwargs.setdefault("source_dir", self.source_dir)
        kwargs.setdefault("baseline_dir", self.baseline_dir)
        kwargs.setdefault("historical_dir", self.historical)
        kwargs.setdefault("harness_path", self.runtime["harness"])
        kwargs.setdefault("dotnet_path", self.runtime["dotnet"])
        kwargs.setdefault("runtime_config_path", self.runtime["config"])
        kwargs.setdefault("runtime_env", {})
        kwargs.setdefault("output_root", out if out is not None else self._out())
        kwargs.setdefault("clock", base.ThreadSafeFakeClock([0]))
        return n.run_node_repeat(run=run, **kwargs)

    def label(self, base_name, rep):
        return f"{base_name}-b{n.SOFT_BUDGET_MS}-r{rep}"

    def outcome(self, label, **changes):
        run = self.by_name[label]
        spec = dict(base.outcome_for(run["fixture"], base.b._action_key(run), run["budget_ms"]))
        spec.update(changes)
        return spec


class ConfigurationTests(unittest.TestCase):
    def test_fixed_constants(self):
        self.assertEqual(n.MAX_LAUNCHES, 16)
        self.assertEqual(n.BASE_COMBINATIONS, 8)
        self.assertEqual(tuple(n.REPS), (1, 2))
        self.assertEqual(n.SOFT_BUDGET_MS, 120000)
        self.assertEqual(n.SEARCH_TIMEOUT_MS, "180000")
        self.assertEqual(n.BASELINE_BUDGET_MS, 20000)
        self.assertEqual(n.DOP, "2")
        self.assertEqual(n.NODES, "6000")
        self.assertEqual(n.MAX_TURNS, "40")
        self.assertEqual(n.PHASE, "parallel")
        self.assertEqual(n.PARALLELISM, 16)
        self.assertEqual(n.PHASE_TIMEOUT_SECONDS, 300)
        self.assertEqual(n.BATCH_DEADLINE_SECONDS, 900)

    def test_with_flag_replaces_only_that_value(self):
        original = ["dotnet", "harness", "--search-timeout-ms", "60000", "--label", "x"]
        changed = n._with_flag(original, "--search-timeout-ms", "180000")
        self.assertEqual(changed, ["dotnet", "harness", "--search-timeout-ms", "180000", "--label", "x"])
        self.assertEqual(original[3], "60000")
        with self.assertRaises(n.SourceValidationError):
            n._with_flag(["dotnet", "harness"], "--search-timeout-ms", "1")


class PlanTests(NodeRepeatCase):
    def test_sixteen_unique_commands_two_reps_per_input(self):
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
        baseline_cmd = {run["name"]: run["command"] for run in baseline["plan"]["runs"]}
        for run in self.runs:
            reference = baseline_cmd[f"{run['base_name']}-b{n.BASELINE_BUDGET_MS}"]
            diffs = {index for index, (left, right) in enumerate(zip(reference, run["command"]))
                     if left != right}
            allowed = {reference.index("--budget-ms") + 1,
                       reference.index("--search-timeout-ms") + 1,
                       reference.index("--dop") + 1,
                       reference.index("--request") + 1,
                       reference.index("--out") + 1,
                       reference.index("--label") + 1}
            self.assertTrue(diffs <= allowed, msg=f"{run['name']}: {diffs}")

    def test_dry_run_launches_nothing(self):
        out = self._out()
        with mock.patch.object(n.subprocess, "run") as run:
            summary = self.run_nodes(out=out, run=False)
        run.assert_not_called()
        self.assertFalse(summary["executed"])
        self.assertEqual(summary["performance"]["peak_actual_overlap"], 0)
        self.assertEqual(len(summary["runs"]), 16)
        self.assertEqual([r["status"] for r in summary["runs"]], [n.STATUS_DRY_RUN] * 16)
        self.assertTrue((out / "plan.json").exists())
        self.assertTrue((out / "summary.json").exists())
        self.assertTrue((out / "journal.jsonl").exists())
        self.assertFalse(summary["repeat_agreement"]["comparisons_enabled"])
        self.assertEqual(summary["repeat_agreement"]["agreed"], 0)
        self.assertIsNone(summary["performance"]["historical_speed_ratio"])

    def test_plan_lists_eight_combinations_and_phase(self):
        out = self._out()
        self.run_nodes(out=out, run=False)
        plan = json.loads((out / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual(plan["max_launches"], 16)
        self.assertEqual(plan["base_combinations"], 8)
        self.assertEqual(plan["reps"], [1, 2])
        self.assertEqual(plan["dop"], "2")
        self.assertEqual(plan["soft_budget_ms"], 120000)
        self.assertEqual(plan["search_timeout_ms"], "180000")
        self.assertEqual(len(plan["combinations"]), 8)
        self.assertTrue(all(run["phase"] == "parallel" for run in plan["runs"]))

    def test_refuses_existing_output_directory(self):
        out = self._out()
        out.mkdir(parents=True)
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            return self.runner()(command, **kwargs)

        with self.assertRaises(FileExistsError):
            self.run_nodes(out=out, run=True, runner=spy)
        self.assertEqual(calls, [])

    def test_main_dry_run_exits_zero(self):
        out = self._out()
        with mock.patch.object(n.bpi, "SOURCE_DIR", self.source_dir), \
                mock.patch.object(n, "BASELINE_DIR", self.baseline_dir), \
                mock.patch.object(n, "HISTORICAL_DIR", self.historical), \
                mock.patch.object(n, "HARNESS", self.runtime["harness"]), \
                mock.patch.object(n, "DOTNET", self.runtime["dotnet"]), \
                mock.patch.object(n, "default_output_root", return_value=out), \
                mock.patch.object(n.subprocess, "run") as run, \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = n.main([])
        run.assert_not_called()
        self.assertEqual(code, 0)
        self.assertTrue((out / "plan.json").exists())

    def test_main_dryonly_alias_launches_nothing(self):
        out = self._out()
        with mock.patch.object(n.bpi, "SOURCE_DIR", self.source_dir), \
                mock.patch.object(n, "BASELINE_DIR", self.baseline_dir), \
                mock.patch.object(n, "HISTORICAL_DIR", self.historical), \
                mock.patch.object(n, "HARNESS", self.runtime["harness"]), \
                mock.patch.object(n, "DOTNET", self.runtime["dotnet"]), \
                mock.patch.object(n, "default_output_root", return_value=out), \
                mock.patch.object(n.subprocess, "run") as run, \
                mock.patch("sys.stdout", new=io.StringIO()):
            code = n.main(["dryonly"])
        run.assert_not_called()
        self.assertEqual(code, 0)
        summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
        self.assertFalse(summary["executed"])
        self.assertEqual(summary["status_counts"]["dry_run"], 16)


class InputTests(NodeRepeatCase):
    def baseline_cells(self):
        return base.b.baseline_cells(base.b.load_baseline(self.baseline_dir))

    def test_generated_inputs_match_baseline_20s_and_repeats(self):
        out = self._out()
        self.run_nodes(out=out, run=True, runner=self.runner())
        cells = self.baseline_cells()
        by_base = {}
        for run in self.runs:
            path = out / "parallel" / run["name"] / "input.json"
            expected = cells[(run["fixture"], base.b._action_key(run), n.BASELINE_BUDGET_MS)]
            digest = base.sha256_file(path)
            self.assertEqual(digest, expected["raw_hashes"]["input_sha256"], msg=run["name"])
            by_base.setdefault(run["base_name"], set()).add(digest)
        self.assertTrue(all(len(digests) == 1 for digests in by_base.values()))

    def test_remap_requires_present_expected_hash(self):
        baseline = base.b.load_baseline(self.baseline_dir)
        remapped = n.remap_baseline_cells(baseline, self.ordered)
        self.assertEqual(len(remapped), 8)
        self.assertTrue(all(key[2] == n.SOFT_BUDGET_MS for key in remapped))

        corrupted = {"runs": {name: json.loads(json.dumps(run))
                              for name, run in baseline["runs"].items()}}
        for run in corrupted["runs"].values():
            if run.get("budget_ms") == n.BASELINE_BUDGET_MS:
                run["raw_hashes"]["input_sha256"] = None
                break
        with self.assertRaises(n.SourceValidationError):
            n.remap_baseline_cells(corrupted, self.ordered)

        missing_cell = {"runs": {}}
        with self.assertRaises(n.SourceValidationError):
            n.remap_baseline_cells(missing_cell, self.ordered)


class ExecutionTests(NodeRepeatCase):
    def test_all_sixteen_verified_with_policy(self):
        summary = self.run_nodes(run=True, runner=self.runner())
        self.assertEqual(summary["verified"], 16)
        self.assertEqual(summary["policy_verified"], 16)
        self.assertEqual(summary["eligible_completed"], 16)
        self.assertTrue(summary["tool_completed"])
        self.assertEqual(summary["batch"]["attempts_accounted"], True)
        for record in summary["runs"]:
            self.assertTrue(record["policy"]["ok"], msg=record["run"])
            self.assertEqual(record["policy"]["budget_ms"], n.SOFT_BUDGET_MS)
            self.assertEqual(record["policy"]["dop"], 2)

    def test_peak_actual_overlap_is_sixteen(self):
        concurrency = base.Concurrency()
        summary = self.run_nodes(
            run=True,
            runner=self.runner(concurrency=concurrency, barrier=base.threading.Barrier(16)),
            clock=base.ThreadSafeFakeClock([0]),
        )
        self.assertEqual(summary["performance"]["peak_actual_overlap"], 16)
        self.assertEqual(concurrency.state["parallel"]["max"], 16)
        self.assertEqual(len(concurrency.calls), 16)

    def test_journal_has_one_line_per_attempt(self):
        out = self._out()
        self.run_nodes(out=out, run=True, runner=self.runner())
        lines = [line for line in (out / "journal.jsonl").read_text(encoding="utf-8").splitlines()
                 if line.strip()]
        self.assertEqual(len(lines), 16)
        self.assertEqual(len({json.loads(line)["run"] for line in lines}), 16)

    def test_deadline_marks_all_not_started_without_retry(self):
        calls = []

        def spy(command, **kwargs):
            calls.append(command)
            return self.runner()(command, **kwargs)

        summary = self.run_nodes(run=True, runner=spy,
                                 clock=base.ThreadSafeFakeClock([0, 1000]))
        self.assertEqual(calls, [])
        statuses = [record["status"] for record in summary["runs"]]
        self.assertEqual(statuses.count(n.parallel.STATUS_NOT_STARTED), 16)
        self.assertEqual(summary["verified"], 0)


class AgreementTests(NodeRepeatCase):
    def test_identical_repeats_agree(self):
        summary = self.run_nodes(run=True, runner=self.runner())
        agreement = summary["repeat_agreement"]
        self.assertTrue(agreement["comparisons_enabled"])
        self.assertEqual(agreement["total"], 8)
        self.assertEqual(agreement["available"], 8)
        self.assertEqual(agreement["agreed"], 8)
        self.assertEqual(agreement["disagreed"], 0)
        for fixture_id in summary["ranking_agreement"]:
            self.assertTrue(summary["ranking_agreement"][fixture_id]["agreement"], msg=fixture_id)

    def test_repeated_disagreement_is_reported_not_suppressed(self):
        label = self.label("silent-starter-proxy-skip", 2)
        summary = self.run_nodes(
            run=True,
            runner=self.runner(outcomes={label: self.outcome(label, player_hp=10, turns=9)}),
        )
        pair = {(entry["fixture"], entry["action"]): entry
                for entry in summary["repeat_agreement"]["pairs"]}[("silent-starter-proxy", "skip")]
        self.assertTrue(pair["available"])
        self.assertFalse(pair["agreement"])
        self.assertIn("player_hp", pair["changed_fields"])
        self.assertGreaterEqual(summary["repeat_agreement"]["disagreed"], 1)

    def test_incomplete_repeat_suppresses_only_its_pair(self):
        label = self.label("silent-starter-proxy-skip", 2)
        summary = self.run_nodes(
            run=True,
            runner=self.runner(outcomes={label: self.outcome(
                label, status="incomplete", player_hp=50, final_enemy_hp=10, turns=5)}),
        )
        pairs = {(entry["fixture"], entry["action"]): entry
                 for entry in summary["repeat_agreement"]["pairs"]}
        self.assertFalse(pairs[("silent-starter-proxy", "skip")]["available"])
        self.assertTrue(pairs[("silent-starter-proxy", "BACKFLIP")]["available"])
        self.assertFalse(summary["rankings"]["silent-starter-proxy"][2]["available"])

    def test_missing_and_invalid_repeats_suppress_their_pair(self):
        missing = self.label("silent-late-pm001-proxy-add-BACKFLIP", 1)
        invalid = self.label("silent-late-pm001-proxy-add-DAGGER_SPRAY", 1)
        summary = self.run_nodes(
            run=True,
            runner=self.runner(
                drop_result=(missing,),
                mutate=lambda label, loadout: loadout.update(seed="WRONG") if label == invalid else None,
            ),
        )
        pairs = {(entry["fixture"], entry["action"]): entry
                 for entry in summary["repeat_agreement"]["pairs"]}
        self.assertFalse(pairs[("silent-late-pm001-proxy", "BACKFLIP")]["available"])
        self.assertFalse(pairs[("silent-late-pm001-proxy", "DAGGER_SPRAY")]["available"])
        self.assertEqual(summary["repeat_agreement"]["available"], 6)

    def test_runtime_mismatch_disables_comparisons(self):
        baseline_runtime = json.loads(
            (self.baseline_dir / "summary.json").read_text(encoding="utf-8"))["runtime"]
        changed = json.loads(json.dumps(baseline_runtime))
        changed["harness"]["sha256"] = "f" * 64
        with mock.patch.object(
            n.ranking, "collect_runtime_provenance", side_effect=[baseline_runtime, changed]
        ):
            summary = self.run_nodes(run=True, runner=self.runner(),
                                     clock=base.ThreadSafeFakeClock([100000]))
        self.assertFalse(summary["runtime"]["stable"])
        self.assertFalse(summary["repeat_agreement"]["comparisons_enabled"])
        self.assertEqual(summary["repeat_agreement"]["available"], 0)
        self.assertFalse(summary["ranking_agreement"]["silent-starter-proxy"]["available"])
        self.assertFalse(
            summary["historical_20000ms_comparison"]["silent-starter-proxy"]["1"]["available"])

    def test_missing_cpu_suppresses_utilization(self):
        label = self.label("silent-starter-proxy-skip", 1)
        summary = self.run_nodes(run=True, runner=self.runner(omit_cpu=(label,)))
        cpu = summary["performance"]["cpu"]
        self.assertEqual(cpu["runs_missing"], 1)
        self.assertFalse(cpu["cpu_complete"])
        self.assertIsNone(cpu["average_busy_cores"])
        self.assertIsNone(cpu["estimated_child_utilization"])
        self.assertIsNone(summary["performance"]["historical_speed_ratio"])

    def test_cell_and_historical_comparisons_are_descriptive(self):
        summary = self.run_nodes(run=True, runner=self.runner())
        first = summary["cell_comparison_vs_20000ms"][0]
        self.assertTrue(first["descriptive"])
        self.assertIn("baseline_20000ms", first)
        self.assertIn("repeat", first)
        historical = summary["historical_20000ms_comparison"]["silent-starter-proxy"]["1"]
        self.assertTrue(historical["available"])


class TelemetryTests(unittest.TestCase):
    def test_node_telemetry_reads_only_initial_search_fields(self):
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            run_dir = Path(tmp)
            (run_dir / "harness-result.json").write_text(json.dumps({
                "timeBoundaryObserved": True,
                "replans": 3,
                "solverMetrics": {"TotalExpanded": 6000, "SelectedExpanded": 5900, "Boundary": 8},
                "pruneCounters": {"boundary": "NodeLimit"},
            }), encoding="utf-8")
            telemetry = n._node_telemetry(run_dir)
        self.assertTrue(telemetry["time_boundary_observed"])
        self.assertEqual(telemetry["expanded_nodes"], 6000)
        self.assertEqual(telemetry["selected_expanded_nodes"], 5900)
        self.assertEqual(telemetry["search_boundary"], "NodeLimit")
        self.assertEqual(telemetry["replans"], 3)

    def test_initial_search_never_claims_whole_fight_node_only(self):
        ordered = [
            {"run": "a", "node_telemetry": {"time_boundary_observed": False,
                                            "expanded_nodes": 6000, "replans": 2}},
            {"run": "b", "node_telemetry": {"time_boundary_observed": True,
                                            "expanded_nodes": 6000, "replans": 1}},
        ]
        block = n._build_initial_search(ordered)
        self.assertFalse(block["whole_fight_node_only_established"])
        self.assertEqual(block["time_boundary_observed_count"], 1)
        self.assertEqual(block["time_boundary_observed_true_runs"], ["b"])
        self.assertEqual(block["expanded_nodes_min"], 6000)
        self.assertEqual(block["replans_total"], 3)
        self.assertIn("does not establish", block["note"])


class OwnerReviewTests(NodeRepeatCase):
    def test_initial_runtime_mismatch_launches_nothing(self):
        runner = mock.Mock(side_effect=AssertionError("must not launch"))
        with mock.patch.object(n.ranking, "collect_runtime_provenance",
                               return_value={"ok": False, "error": "changed runtime"}):
            summary = self.run_nodes(runner=runner)
        runner.assert_not_called()
        self.assertEqual(len(summary["runs"]), 16)
        self.assertEqual(summary["performance"]["peak_actual_overlap"], 0)
        self.assertFalse(summary["repeat_agreement"]["comparisons_enabled"])
        self.assertIn("no processes launched", summary["error"])

    def test_incomplete_has_no_historical_semantic_comparison(self):
        label = self.runs[0]["name"]
        summary = self.run_nodes(runner=self.runner(outcomes={
            label: self.outcome(label, status="incomplete", combat_in_progress=True)}))
        record = next(r for r in summary["runs"] if r["run"] == label)
        if record["eligible"]:
            self.fail("fake incomplete result unexpectedly eligible")
        cell = next(c for c in summary["cell_comparison_vs_20000ms"]
                    if c["cell"]["fixture"] == self.runs[0]["fixture"]
                    and c["cell"]["action"] == n._action(self.runs[0])
                    and c["cell"]["rep"] == 1)
        self.assertFalse(cell["available"])


class GlobalStateTests(NodeRepeatCase):
    def test_module_globals_are_not_mutated(self):
        before = {"DOP": n.DOP, "SOFT_BUDGET_MS": n.SOFT_BUDGET_MS,
                  "SEARCH_TIMEOUT_MS": n.SEARCH_TIMEOUT_MS, "NODES": n.NODES,
                  "REPS": tuple(n.REPS), "BATCH_DEADLINE_SECONDS": n.BATCH_DEADLINE_SECONDS}
        ranking_dop = n.ranking.DOP
        parallel_dop = n.parallel.DOP
        self.run_nodes(run=True, runner=self.runner())
        self.assertEqual(before["DOP"], n.DOP)
        self.assertEqual(before["SOFT_BUDGET_MS"], n.SOFT_BUDGET_MS)
        self.assertEqual(before["SEARCH_TIMEOUT_MS"], n.SEARCH_TIMEOUT_MS)
        self.assertEqual(before["NODES"], n.NODES)
        self.assertEqual(before["REPS"], tuple(n.REPS))
        self.assertEqual(before["BATCH_DEADLINE_SECONDS"], n.BATCH_DEADLINE_SECONDS)
        self.assertEqual(ranking_dop, n.ranking.DOP)
        self.assertEqual(n.ranking.DOP, "1")
        self.assertEqual(parallel_dop, n.parallel.DOP)
        self.assertEqual(n.parallel.DOP, "2")

    def test_inherited_environment_is_never_logged(self):
        with mock.patch.object(n.brp, "build_env", return_value={"SECRET_SENTINEL": "leak-me"}):
            summary = self.run_nodes(run=True, runner=self.runner())
        encoded = json.dumps(summary)
        self.assertNotIn("leak-me", encoded)
        self.assertNotIn("SECRET_SENTINEL", encoded)


SECOND_SEED = "PICKSEL02"


class SecondSeedCase(base.FixtureCase):
    """Shared second-seed fixtures built from the accepted factories."""

    def setUp(self):
        super().setUp()
        _, self.ordered2 = n.ranking.prepare_source(self.source_dir, seed=SECOND_SEED)
        self.runs2 = n.build_repeat_runs(
            self.ordered2, self.tmp / "planruns2",
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
        )
        self.by_name2 = {run["name"]: run for run in self.runs2}
        self.baseline2 = base.b.load_baseline(self.baseline_dir)

    def runner(self, **kwargs):
        return base.make_fake_runner(self.runs2, self.fixtures, **kwargs)

    def run_nodes(self, *, out=None, run=True, **kwargs):
        kwargs.setdefault("seed", SECOND_SEED)
        kwargs.setdefault("source_dir", self.source_dir)
        kwargs.setdefault("baseline_dir", self.baseline_dir)
        kwargs.setdefault("historical_dir", self.historical)
        kwargs.setdefault("harness_path", self.runtime["harness"])
        kwargs.setdefault("dotnet_path", self.runtime["dotnet"])
        kwargs.setdefault("runtime_config_path", self.runtime["config"])
        kwargs.setdefault("runtime_env", {})
        kwargs.setdefault("output_root", out if out is not None else self._out())
        kwargs.setdefault("clock", base.ThreadSafeFakeClock([0]))
        return n.run_node_repeat(run=run, **kwargs)

    def remapped2(self):
        return n.remap_baseline_cells(self.baseline2, self.ordered2, seed=SECOND_SEED)


class SecondSeedPlanTests(SecondSeedCase):
    def test_sixteen_runs_and_commands_stay_frozen(self):
        self.assertEqual(len(self.runs2), 16)
        self.assertEqual(len({run["name"] for run in self.runs2}), 16)
        self.assertEqual(len({tuple(run["command"]) for run in self.runs2}), 16)
        for run in self.runs2:
            self.assertEqual(run["seed"], SECOND_SEED)
            command = run["command"]
            self.assertEqual(command[command.index("--budget-ms") + 1], "120000")
            self.assertEqual(command[command.index("--search-timeout-ms") + 1], "180000")
            self.assertEqual(command[command.index("--dop") + 1], "2")
            self.assertEqual(command[command.index("--nodes") + 1], "6000")
            self.assertEqual(command[command.index("--max-turns") + 1], "40")

    def test_plan_and_summary_seed_are_accurate(self):
        out = self._out()
        summary = self.run_nodes(out=out, run=False)
        plan = json.loads((out / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual(plan["seed"], SECOND_SEED)
        self.assertEqual(summary["seed"], SECOND_SEED)
        self.assertTrue(summary["seed_context"]["seed_difference"])
        self.assertIsNone(summary["performance"]["historical_speed_ratio"])

    def test_second_seed_dry_run_launches_nothing(self):
        out = self._out()
        with mock.patch.object(n.subprocess, "run") as run:
            summary = self.run_nodes(out=out, run=False)
        run.assert_not_called()
        self.assertEqual([r["status"] for r in summary["runs"]], [n.STATUS_DRY_RUN] * 16)
        self.assertEqual(summary["performance"]["peak_actual_overlap"], 0)
        self.assertFalse(summary["executed"])


class SecondSeedInputTests(SecondSeedCase):
    def test_generated_inputs_match_remap_and_repeats(self):
        out = self._out()
        self.run_nodes(out=out, run=True, runner=self.runner())
        remapped = self.remapped2()
        by_base = {}
        for run in self.runs2:
            path = out / "parallel" / run["name"] / "input.json"
            key = (run["fixture"], base.b._action_key(run), n.SOFT_BUDGET_MS)
            digest = base.sha256_file(path)
            self.assertEqual(digest, remapped[key]["raw_hashes"]["input_sha256"], msg=run["name"])
            by_base.setdefault(run["base_name"], set()).add(digest)
        self.assertTrue(all(len(digests) == 1 for digests in by_base.values()))

    def test_input_differs_from_source_only_by_seed(self):
        out = self._out()
        self.run_nodes(out=out, run=True, runner=self.runner())
        baseline_cells = base.b.baseline_cells(self.baseline2)
        remapped = self.remapped2()
        for run in self.runs2:
            generated = json.loads(
                (out / "parallel" / run["name"] / "input.json").read_text(encoding="utf-8"))
            source = json.loads(Path(run["scenario_path"]).read_text(encoding="utf-8"))
            self.assertEqual(generated["seed"], SECOND_SEED)
            self.assertEqual(n.ranking.diff_paths(source, generated), {"seed"})
            key = (run["fixture"], base.b._action_key(run), n.SOFT_BUDGET_MS)
            historical = baseline_cells[
                (run["fixture"], base.b._action_key(run), n.BASELINE_BUDGET_MS)]
            self.assertNotEqual(remapped[key]["raw_hashes"]["input_sha256"],
                                historical["raw_hashes"]["input_sha256"])
            self.assertEqual(remapped[key]["seed"], n.SEED)
            self.assertEqual(remapped[key]["seed_remap"]["changed_fields"], ["seed"])

    def test_outer_request_changes_only_seed(self):
        out = self._out()
        self.run_nodes(out=out, run=True, runner=self.runner())
        historical = json.loads((self.historical / "outer-request.json").read_text(encoding="utf-8"))
        for run in self.runs2:
            run_dir = out / "parallel" / run["name"]
            request = json.loads((run_dir / "outer-request.json").read_text(encoding="utf-8"))
            self.assertEqual(request["seed"], SECOND_SEED)
            expected = n.brp.sanitize_request(historical, run_dir, run["name"])
            expected["seed"] = SECOND_SEED
            self.assertEqual(n.ranking.diff_paths(expected, request), set())

    def test_source_tree_is_untouched(self):
        paths = [Path(spec["scenario_path"]) for spec in self.ordered2]
        paths += [self.source_dir / "adapter-output.json", self.source_dir / "cases" / "manifest.json"]
        before = {str(path): base.sha256_file(path) for path in paths}
        self.run_nodes(run=True, runner=self.runner())
        after = {str(path): base.sha256_file(path) for path in paths}
        self.assertEqual(before, after)


class SecondSeedHistoricalMetadataTests(SecondSeedCase):
    def test_historical_comparison_flags_seed_difference(self):
        summary = self.run_nodes(run=True, runner=self.runner())
        context = summary["seed_context"]
        self.assertTrue(context["seed_difference"])
        self.assertTrue(context["budget_difference"])
        self.assertTrue(context["dop_difference"])
        self.assertFalse(context["repeats_are_independent_seeds"])
        self.assertTrue(context["descriptive_only"])
        for entry in summary["cell_comparison_vs_20000ms"]:
            self.assertTrue(entry["seed_difference"])
            self.assertEqual(entry["baseline_seed"], n.SEED)
        for fixture in summary["historical_20000ms_comparison"].values():
            for entry in fixture.values():
                self.assertTrue(entry["seed_difference"])


class SecondSeedFailureTests(SecondSeedCase):
    def test_actual_wrong_seed_is_rejected(self):
        label = self.runs2[0]["name"]

        def mutate(name, loadout):
            if name == label:
                loadout["seed"] = n.SEED

        summary = self.run_nodes(run=True, runner=self.runner(mutate=mutate))
        record = next(r for r in summary["runs"] if r["run"] == label)
        self.assertEqual(record["status"], n.parallel.STATUS_MISMATCH)
        self.assertFalse(record["eligible"])

    def test_input_tampering_is_rejected(self):
        out = self._out()
        remapped = self.remapped2()
        run = self.runs2[0]
        key = (run["fixture"], base.b._action_key(run), n.SOFT_BUDGET_MS)
        remapped[key]["raw_hashes"]["input_sha256"] = "0" * 64
        with self.assertRaises(n.SourceValidationError):
            base.b.prepare_run_input(run, out, self.historical / "outer-request.json", remapped)

    def test_tampered_baseline_input_is_rejected_before_remap(self):
        baseline = base.b.load_baseline(self.baseline_dir)
        cell = next(run for run in baseline["runs"].values()
                    if run.get("budget_ms") == n.BASELINE_BUDGET_MS)
        input_path = self.baseline_dir / cell["run"] / "input.json"
        input_path.write_bytes(input_path.read_bytes() + b" ")
        with self.assertRaises(base.b.BaselineValidationError):
            base.b.load_baseline(self.baseline_dir)

    def test_remap_rejects_baseline_input_with_foreign_seed(self):
        baseline = base.b.load_baseline(self.baseline_dir)
        cell = next(run for run in baseline["runs"].values()
                    if run.get("budget_ms") == n.BASELINE_BUDGET_MS)
        input_path = self.baseline_dir / cell["run"] / "input.json"
        data = json.loads(input_path.read_text(encoding="utf-8"))
        data["seed"] = SECOND_SEED
        input_path.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
        summary_path = self.baseline_dir / "summary.json"
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
        for run in summary["runs"]:
            if run["run"] == cell["run"]:
                run["raw_hashes"]["input_sha256"] = base.sha256_file(input_path)
        base.write_json(summary_path, summary)
        baseline = base.b.load_baseline(self.baseline_dir)
        with self.assertRaises(n.SourceValidationError):
            n.remap_baseline_cells(baseline, self.ordered2, seed=SECOND_SEED)


class SeedRejectionTests(base.FixtureCase):
    def _run_with_seed(self, seed, out):
        return n.run_node_repeat(
            run=False, seed=seed, output_root=out, source_dir=self.source_dir,
            baseline_dir=self.baseline_dir, historical_dir=self.historical,
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
            runtime_config_path=self.runtime["config"], runtime_env={},
            clock=base.ThreadSafeFakeClock([0]),
        )

    def test_arbitrary_and_heldout_seeds_rejected_before_output(self):
        for seed in ("PICKEVAL01", "PICKEVAL02", "PICKSEL03", "arbitrary"):
            out = self._out()
            with mock.patch.object(n.subprocess, "run") as run:
                with self.assertRaises(n.SourceValidationError):
                    self._run_with_seed(seed, out)
            run.assert_not_called()
            self.assertFalse(out.exists(), msg=seed)

    def test_cli_rejects_unsupported_seed(self):
        for seed in ("PICKEVAL01", "PICKSEL03"):
            with mock.patch("sys.stderr", new=io.StringIO()):
                with self.assertRaises(SystemExit) as ctx:
                    n.main(["--seed", seed])
            self.assertEqual(ctx.exception.code, 2)


class FirstSeedRegressionTests(base.FixtureCase):
    def test_default_seed_still_picksel01(self):
        self.assertEqual(n.SEED, "PICKSEL01")
        self.assertEqual(n.SUPPORTED_SEEDS, ("PICKSEL01", "PICKSEL02"))
        _, ordered = n.ranking.prepare_source(self.source_dir)
        self.assertEqual({spec["seed"] for spec in ordered}, {"PICKSEL01"})
        summary = n.run_node_repeat(
            run=False, output_root=self._out(), source_dir=self.source_dir,
            baseline_dir=self.baseline_dir, historical_dir=self.historical,
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
            runtime_config_path=self.runtime["config"], runtime_env={},
            clock=base.ThreadSafeFakeClock([0]),
        )
        self.assertEqual(summary["seed"], "PICKSEL01")
        self.assertFalse(summary["seed_context"]["seed_difference"])
        self.assertEqual(summary["plan"]["seed"], "PICKSEL01")


class SecondSeedGlobalTests(base.FixtureCase):
    def test_second_seed_does_not_mutate_globals(self):
        before = {
            "SEED": n.SEED, "SUPPORTED_SEEDS": tuple(n.SUPPORTED_SEEDS),
            "ranking_SEED": n.ranking.SEED,
            "ranking_SUPPORTED_SEEDS": tuple(n.ranking.SUPPORTED_SEEDS),
        }
        _, ordered2 = n.ranking.prepare_source(self.source_dir, seed=SECOND_SEED)
        runs = n.build_repeat_runs(
            ordered2, self.tmp / "globalruns",
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
        )
        n.run_node_repeat(
            run=True, seed=SECOND_SEED, output_root=self._out(), source_dir=self.source_dir,
            baseline_dir=self.baseline_dir, historical_dir=self.historical,
            harness_path=self.runtime["harness"], dotnet_path=self.runtime["dotnet"],
            runtime_config_path=self.runtime["config"], runtime_env={},
            runner=base.make_fake_runner(runs, self.fixtures),
            clock=base.ThreadSafeFakeClock([0]),
        )
        self.assertEqual(before["SEED"], n.SEED)
        self.assertEqual(before["SUPPORTED_SEEDS"], tuple(n.SUPPORTED_SEEDS))
        self.assertEqual(before["ranking_SEED"], n.ranking.SEED)
        self.assertEqual(before["ranking_SUPPORTED_SEEDS"], tuple(n.ranking.SUPPORTED_SEEDS))


if __name__ == "__main__":
    unittest.main()
