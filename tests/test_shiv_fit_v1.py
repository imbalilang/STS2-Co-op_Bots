#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Scoped tests for scripts/fit_shiv_v1.py (task shiv-fit-v1).

Offline, standard-library only. No battles, no network, no production edits.
They pin the frozen design invariants, never an arbitrary fitted winner.

Run with:
    python -m unittest discover -s tests -p test_shiv_fit_v1.py
"""

import copy
import json
import math
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import fit_shiv_v1 as fit  # noqa: E402
import shiv_prior_model as m  # noqa: E402


def _record(status, player_hp=None, enemy_hp=None, eligible=True):
    return {
        "outcome_status": status,
        "player_hp": player_hp,
        "enemy_hp": enemy_hp,
        "eligible": eligible,
    }


def _tiny_case(name, deck, choices, seed_utils):
    """matched = {seed: {action: U}} built from explicit utility values."""
    return {"name": name, "deck": deck, "choices": choices, "matched": seed_utils}


class TestFrozenDesign(unittest.TestCase):
    def test_case_lengths_and_choices(self):
        lengths = {c["name"]: c["expected_length"] for c in fit.ALL_CASES}
        self.assertEqual(lengths, {
            "train-producers": 17,
            "train-payoffs": 16,
            "train-defense": 17,
            "holdout-duplicates": 19,
        })
        for case in fit.ALL_CASES:
            self.assertEqual(len(fit.deck_for_case(case)), case["expected_length"])
            for action in case["choices"]:
                fit.check_choice_action(action)

    def test_train_and_holdout_seeds_disjoint(self):
        self.assertEqual(len(fit.TRAIN_SEEDS), 200)
        self.assertEqual(len(fit.HOLDOUT_SEEDS), 200)
        self.assertEqual(len(set(fit.TRAIN_SEEDS) & set(fit.HOLDOUT_SEEDS)), 0)

    def test_grid_is_36_and_bounded(self):
        self.assertEqual(len(fit.DEFAULT_GRID), 36)
        self.assertEqual(len(set(fit.DEFAULT_GRID)), 36)
        self.assertEqual(fit.MULTIPLIERS, (0.5, 0.75, 1.0, 1.25, 1.5, 2.0))
        self.assertIn((1.0, 1.0), fit.DEFAULT_GRID)

    def test_baseline_params_unchanged(self):
        before = copy.deepcopy(m.DEFAULT_PARAMS)
        fit.apply_multipliers(m.DEFAULT_PARAMS, 1.0, 1.0)
        self.assertEqual(m.DEFAULT_PARAMS, before)


class TestUtility(unittest.TestCase):
    def test_win_uses_player_hp_and_zeroes_enemy(self):
        # win with full HP and a nonzero enemy_hp still floors enemy to 0
        self.assertAlmostEqual(fit.utility(_record("win", 70, 999)), 1.5)
        self.assertAlmostEqual(fit.utility(_record("win", 35, 12)), 1.25)

    def test_loss_zeroes_player_hp_and_clips_enemy(self):
        self.assertAlmostEqual(fit.utility(_record("loss", 40, 0)), -0.0)
        self.assertAlmostEqual(fit.utility(_record("loss", 40, 100)), -0.25)
        self.assertAlmostEqual(fit.utility(_record("loss", 40, 200)), -0.5)
        self.assertAlmostEqual(fit.utility(_record("loss", 40, 500)), -0.5)

    def test_unknown_or_failed_status_rejected(self):
        for status in (None, "error", "timeout", "missing", "incomplete"):
            with self.assertRaises(fit.FitError):
                fit.utility(_record(status, 10, 10))


class TestMatchedSeeds(unittest.TestCase):
    def test_seed_dropped_when_any_action_ineligible(self):
        case = {"name": "c", "choices": ("ACCURACY", "skip")}
        per_seed = {
            "S1": {"ACCURACY": _record("win", 10, 0), "skip": _record("loss", 0, 50)},
            "S2": {"ACCURACY": _record("win", 10, 0),
                   "skip": _record("error", None, None, eligible=False)},
            "S3": {"ACCURACY": _record("win", 10, 0)},
        }
        matched = fit.matched_seed_utilities(case, per_seed)
        self.assertEqual(sorted(matched), ["S1"])

    def test_error_is_never_imputed_as_loss(self):
        case = {"name": "c", "choices": ("ACCURACY", "skip")}
        per_seed = {"S1": {"ACCURACY": _record("win", 10, 0),
                           "skip": _record("missing", None, None, eligible=False)}}
        self.assertEqual(fit.matched_seed_utilities(case, per_seed), {})

    def test_matched_values_use_utility_formula(self):
        case = {"name": "c", "choices": ("ACCURACY", "skip")}
        per_seed = {"S1": {"ACCURACY": _record("win", 35, 0), "skip": _record("loss", 0, 100)}}
        matched = fit.matched_seed_utilities(case, per_seed)
        self.assertAlmostEqual(matched["S1"]["ACCURACY"], 1.25)
        self.assertAlmostEqual(matched["S1"]["skip"], -0.25)


class TestMultipliers(unittest.TestCase):
    def test_only_declared_weights_scaled(self):
        base = copy.deepcopy(m.DEFAULT_PARAMS)
        tuned = fit.apply_multipliers(base, 0.5, 2.0)
        for key in fit.DEFENSE_WEIGHTS:
            self.assertAlmostEqual(tuned["weights"][key], base["weights"][key] * 0.5)
        for key in fit.ENGINE_WEIGHTS:
            self.assertAlmostEqual(tuned["weights"][key], base["weights"][key] * 2.0)
        for key in base["weights"]:
            if key in fit.DEFENSE_WEIGHTS or key in fit.ENGINE_WEIGHTS:
                continue
            self.assertEqual(tuned["weights"][key], base["weights"][key])
        self.assertEqual(tuned["caps"], base["caps"])
        self.assertEqual(tuned["assumptions"], base["assumptions"])

    def test_out_of_grid_multiplier_rejected(self):
        with self.assertRaises(ValueError):
            fit.apply_multipliers(m.DEFAULT_PARAMS, 3.0, 1.0)
        with self.assertRaises(ValueError):
            fit.apply_multipliers(m.DEFAULT_PARAMS, 1.0, 0.1)


class TestFitFunction(unittest.TestCase):
    def _cases(self):
        deck_a = list(m.SCENARIOS[1]["deck"])   # producers_no_payoffs
        deck_b = list(m.SCENARIOS[2]["deck"])   # payoffs_no_producers
        deck_c = list(m.SCENARIOS[3]["deck"])   # defense_poor
        # Three equally weighted cases; the model may select any action, so the
        # test checks the objective arithmetic rather than a forced winner.
        c1 = _tiny_case("c1", deck_a, ("ACCURACY", "ADRENALINE", "skip"),
                        {"s1": {"ACCURACY": 1.0, "ADRENALINE": 0.4, "skip": 0.2}})
        c2 = _tiny_case("c2", deck_b, ("BLADE_DANCE", "LEG_SWEEP", "skip"),
                        {"s1": {"BLADE_DANCE": 0.5, "LEG_SWEEP": 0.9, "skip": 0.1}})
        c3 = _tiny_case("c3", deck_c, ("LEG_SWEEP", "ADRENALINE", "skip"),
                        {"s1": {"LEG_SWEEP": 0.7, "ADRENALINE": 0.6, "skip": 0.3}})
        return [c1, c2, c3]

    def test_rejects_holdout_split(self):
        with self.assertRaises(ValueError):
            fit.fit_parameters(self._cases(), m.DEFAULT_PARAMS, split=fit.SPLIT_HOLDOUT)
        with self.assertRaises(ValueError):
            fit.fit_parameters(self._cases(), m.DEFAULT_PARAMS, split="holdout")

    def test_baseline_candidate_included(self):
        result = fit.fit_parameters(self._cases(), m.DEFAULT_PARAMS,
                                    grid=((1.0, 1.0), (0.5, 0.5)))
        pairs = {(e["defense"], e["engine"]) for e in result["grid"]}
        self.assertIn((1.0, 1.0), pairs)
        self.assertEqual(result["baseline"]["defense"], 1.0)
        self.assertEqual(result["baseline"]["engine"], 1.0)

    def test_objective_matches_manual_formula(self):
        result = fit.fit_parameters(self._cases(), m.DEFAULT_PARAMS,
                                    grid=((1.0, 1.0), (0.5, 0.5)))
        by_pair = {(e["defense"], e["engine"]): e for e in result["grid"]}
        for (defense, engine), entry in by_pair.items():
            expected_raw = sum(pc["measured_u"] for pc in entry["per_case"]) / 3.0
            expected_penalty = fit.PENALTY_COEF * (math.log(defense) ** 2 + math.log(engine) ** 2)
            self.assertAlmostEqual(entry["raw_mean_u"], expected_raw)
            self.assertAlmostEqual(entry["penalty"], expected_penalty)
            self.assertAlmostEqual(entry["objective"],
                                   entry["raw_mean_u"] - entry["penalty"])

    def test_tie_break_prefers_baseline(self):
        # Constant utility across all actions -> every objective differs only by
        # penalty, so the baseline (1,1) must win with zero penalty.
        cases = []
        for spec in fit.TRAIN_CASES:
            cases.append(_tiny_case(spec["name"], fit.deck_for_case(spec), spec["choices"],
                                    {"s0": {a: 1.0 for a in spec["choices"]}}))
        result = fit.fit_parameters(cases, m.DEFAULT_PARAMS,
                                    grid=((1.0, 1.0), (0.5, 0.5), (2.0, 2.0)))
        self.assertFalse(result["improved_over_baseline"])
        self.assertTrue(result["baseline_selected"])
        self.assertEqual(result["selected"]["defense"], 1.0)
        self.assertEqual(result["selected"]["engine"], 1.0)
        self.assertEqual(result["params"]["weights"]["w_direct_block"],
                         m.DEFAULT_PARAMS["weights"]["w_direct_block"])

    def test_incomplete_case_raises(self):
        cases = self._cases()
        cases[1]["matched"] = {}
        with self.assertRaises(fit.FitIncompleteError):
            fit.fit_parameters(cases, m.DEFAULT_PARAMS, grid=((1.0, 1.0),))

    def test_params_hash_is_stable(self):
        r1 = fit.fit_parameters(self._cases(), m.DEFAULT_PARAMS, grid=((1.0, 1.0),))
        r2 = fit.fit_parameters(self._cases(), m.DEFAULT_PARAMS, grid=((1.0, 1.0),))
        self.assertEqual(r1["params_sha256"], r2["params_sha256"])
        self.assertEqual(r1["params_sha256"], fit.canonical_sha(r1["params"]))

    def test_tuned_artifact_loads_via_model_cli_path(self):
        # The fitted params must be a drop-in for shiv_prior_model's dict params.
        result = fit.fit_parameters(self._cases(), m.DEFAULT_PARAMS, grid=((1.0, 1.0),))
        deck = fit.deck_for_case(fit.TRAIN_CASES[2])
        ranked = m.rank_choices(deck, candidates=["LEG_SWEEP", "ADRENALINE"],
                                params=result["params"], community=False)
        self.assertIn(ranked["winner"], ("LEG_SWEEP", "ADRENALINE", "SKIP"))


class TestCliAggregation(unittest.TestCase):
    def test_records_grouped_by_case_seed(self):
        records = [
            {"case": "train-producers", "seed": "S1", "action": "ACCURACY",
             "outcome_status": "win", "eligible": True, "player_hp": 10, "enemy_hp": 0},
            {"case": "train-producers", "seed": "S1", "action": "ADRENALINE",
             "outcome_status": "loss", "eligible": True, "player_hp": 0, "enemy_hp": 50},
            {"case": "train-producers", "seed": "S1", "action": "skip",
             "outcome_status": "win", "eligible": True, "player_hp": 5, "enemy_hp": 0},
        ]
        grouped = fit.records_by_case_seed(records)
        self.assertEqual(sorted(grouped["train-producers"]["S1"]),
                         ["ACCURACY", "ADRENALINE", "skip"])
        cases = fit.build_train_cases(records)
        by_name = {c["name"]: c for c in cases}
        self.assertEqual(len(by_name["train-producers"]["matched"]), 1)
        self.assertEqual(by_name["train-payoffs"]["matched"], {})


class TestRunnerModule(unittest.TestCase):
    def test_runner_imports_and_constants(self):
        import importlib.util
        path = REPO_ROOT / "work" / "shiv-fit-v1" / "run_training.py"
        spec = importlib.util.spec_from_file_location("shiv_fit_runner_test", path)
        module = importlib.util.module_from_spec(spec)
        sys.modules["shiv_fit_runner_test"] = module
        spec.loader.exec_module(module)
        self.assertEqual(module.MAX_BATTLES, 2400)
        self.assertEqual(module.MAX_PER_CASE, 600)
        self.assertEqual(module.PHASE_DEADLINE_SECONDS, 1800)
        self.assertEqual(module.GLOBAL_BATTLE_CEILING_SECONDS, 3600)
        self.assertEqual([c["name"] for c in module.TRAIN_CASES],
                         ["train-producers", "train-payoffs", "train-defense"])
        self.assertEqual([c["name"] for c in module.HOLDOUT_CASES], ["holdout-duplicates"])
        self.assertTrue(callable(module.main))


class TestOfflineIntegration(unittest.TestCase):
    """Offline deck / Elo wiring checks; no battles, no network."""

    @classmethod
    def setUpClass(cls):
        import importlib.util
        path = REPO_ROOT / "work" / "shiv-fit-v1" / "run_training.py"
        spec = importlib.util.spec_from_file_location("shiv_fit_runner_integration", path)
        cls.runner = importlib.util.module_from_spec(spec)
        sys.modules["shiv_fit_runner_integration"] = cls.runner
        spec.loader.exec_module(cls.runner)
        cls.prior = cls.runner.load_module(cls.runner.BATCH / "run_batch.py", "fit_prior_test")
        cls.audit = cls.runner.load_module(cls.runner.AUDIT_PATH, "fit_audit_test")

    def test_all_case_decks_build_with_expected_shape(self):
        char_set, colorless_set, catalog = self.prior.load_catalog()
        self.assertTrue(catalog)
        for case in self.runner.ALL_CASES:
            deck = self.runner.build_deck(self.prior, char_set, colorless_set, case)
            self.assertEqual(deck["deck_length"], case["expected_length"])
            self.assertEqual(deck["bane_count"], 1)
            self.assertEqual(deck["unsupported"], [])
            self.assertEqual(len(deck["fixture"]["deck"]), case["expected_length"])
            self.assertEqual(deck["fixture"]["candidates"],
                             [c for c in case["choices"] if c != "skip"])
            self.assertNotIn("ASCENDERS_BANE", [c for c, _ in deck["char"]])

    def test_elo_selection_available_for_every_case(self):
        a10 = json.loads((REPO_ROOT / "work" / "shiv-score-audit" / "scores-a10.json")
                         .read_text(encoding="utf-8"))
        for case in self.runner.ALL_CASES:
            selected, detail = self.runner.elo_selection(case, a10, self.audit)
            self.assertIn(selected, case["choices"])
            self.assertTrue(detail["available"])

    def test_baseline_selection_is_valid_action(self):
        for case in self.runner.ALL_CASES:
            selected = fit.select_action(fit.deck_for_case(case), case["choices"],
                                         m.DEFAULT_PARAMS)
            self.assertIn(selected, case["choices"])


if __name__ == "__main__":
    unittest.main()
