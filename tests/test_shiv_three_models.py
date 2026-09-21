#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Scoped tests for scripts/shiv_three_models.py (task shiv-three-models).

Offline, standard-library only. No battles, no network, no writes outside the
scoped module under test. They pin the frozen protocol invariants and the pure
helpers: train-only filtering (holdout is forbidden), train feature scaling, the
B lambda endpoints, an exact small ridge example, same-action zero difference,
the Bonferroni/Cornish-Fisher threshold, and the rule that errors are never
imputed as losses.

Run with:
    python -m unittest discover -s tests -p test_shiv_three_models.py
"""

import json
import math
import statistics
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import fit_shiv_v1 as fit  # noqa: E402
import shiv_prior_model as m  # noqa: E402
import shiv_three_models as t  # noqa: E402

SCORES_A10 = REPO_ROOT / "work" / "shiv-score-audit" / "scores-a10.json"


def _record(status, player_hp=None, enemy_hp=None, eligible=True):
    return {"outcome_status": status, "player_hp": player_hp, "enemy_hp": enemy_hp,
            "eligible": eligible}


class TestFrozenProtocol(unittest.TestCase):
    def test_protocol_shape_and_frozen_values(self):
        protocol = t.build_protocol()
        self.assertFalse(protocol["frozen_before_outcomes"])
        self.assertIn("before any heldout collection", protocol["freeze_scope"])
        self.assertEqual(protocol["holdout_case"]["choices"], list(t.CHOICES))
        self.assertEqual(protocol["train"]["allowed_cases"], list(t.TRAIN_NAMES))
        self.assertTrue(protocol["train"]["holdout_forbidden_in_fit"])
        self.assertEqual(protocol["train"]["required_validated_records"], 1800)
        self.assertEqual(protocol["model_b"]["lambda_grid"], list(t.LAMBDAS))
        self.assertEqual(protocol["model_b"]["missing_elo"], "error")
        self.assertEqual(protocol["model_c"]["ridge_lambda"], 1.0)
        self.assertFalse(protocol["model_c"]["hyperparameter_search"])
        self.assertEqual(protocol["confirmation"]["family_comparisons"], 12)
        self.assertEqual(protocol["confirmation"]["max_new_battles"], 600)
        self.assertEqual(protocol["confirmation"]["deadline_seconds"], 1200)
        expected = {"driver", "fit_module", "model_source", "score_snapshot_a10",
                    "relationship_graph", "prior_runner"}
        self.assertEqual(set(protocol["sources"]), expected)

    def test_frozen_case_and_seed_shape(self):
        self.assertEqual(t.CHOICES, ("KNIFE_TRAP", "ADRENALINE", "skip"))
        self.assertEqual(len(t.H_SEEDS), 200)
        self.assertEqual(len(t.R_SEEDS), 200)
        self.assertEqual(t.H_SEEDS[0], "SHIVFITH0001")
        self.assertEqual(t.H_SEEDS[-1], "SHIVFITH0200")
        self.assertEqual(t.R_SEEDS[0], "SHIVFITR0001")
        self.assertEqual(t.R_SEEDS[-1], "SHIVFITR0200")
        self.assertEqual(t.HOLDOUT_CASE["expected_length"], 19)
        self.assertEqual(len(fit.deck_for_case(t.HOLDOUT_CASE)), 19)
        self.assertEqual(t.LAMBDAS, (0.0, 0.25, 0.5, 0.75, 1.0))

    def test_c_groups_cover_known_component_keys(self):
        for keys in t.C_GROUPS.values():
            for key in keys:
                self.assertIn(key, m._COMPONENT_KEYS)


class TestTrainOnlyFiltering(unittest.TestCase):
    def test_holdout_records_are_filtered_out(self):
        records = [
            {"case": "train-producers", "seed": "S1", "action": "ACCURACY",
             "eligible": True, "outcome_status": "win", "player_hp": 10, "enemy_hp": 0},
            {"case": "holdout-duplicates", "seed": "H1", "action": "KNIFE_TRAP",
             "eligible": True, "outcome_status": "win", "player_hp": 10, "enemy_hp": 0},
        ]
        kept, excluded = t.train_only_records(records)
        self.assertEqual([r["case"] for r in kept], ["train-producers"])
        self.assertEqual(excluded, ["holdout-duplicates"])

        snapshot = t.build_train_snapshot(records)
        self.assertFalse(snapshot["holdout_read"])
        self.assertEqual(snapshot["excluded_case_names"], ["holdout-duplicates"])
        self.assertTrue(all(r["case"] in t.TRAIN_NAMES for r in snapshot["records"]))
        self.assertFalse(snapshot["complete"])

    def test_snapshot_requires_1800_validated_records(self):
        # A complete synthetic train snapshot: 3 cases x 200 seeds x 3 actions.
        records = []
        for spec in fit.TRAIN_CASES:
            for index in range(1, 201):
                for action in spec["choices"]:
                    records.append({
                        "case": spec["name"], "seed": "SHIVFITD%04d" % index,
                        "action": action, "eligible": True,
                        "outcome_status": "win", "player_hp": 35, "enemy_hp": 0,
                    })
        snapshot = t.build_train_snapshot(records)
        self.assertTrue(snapshot["complete"])
        self.assertEqual(snapshot["validated_records"], 1800)
        self.assertEqual(snapshot["case_seed_counts"],
                         {name: 200 for name in t.TRAIN_NAMES})


class TestFitModelsSmoke(unittest.TestCase):
    def test_fit_models_end_to_end_is_holdout_blind(self):
        records = []
        for spec in fit.TRAIN_CASES:
            for index in range(1, 201):
                for action in spec["choices"]:
                    records.append({
                        "case": spec["name"], "seed": "SHIVFITD%04d" % index,
                        "action": action, "eligible": True,
                        "outcome_status": "win", "player_hp": 35, "enemy_hp": 0,
                    })
        snapshot = t.build_train_snapshot(records)
        a1 = {"params": m.DEFAULT_PARAMS, "defense": 1.0, "engine": 1.0,
              "recovered_by": "baseline_exact", "source": "synthetic",
              "source_sha256": None}
        result = t.fit_models(None, snapshot, a1)
        self.assertEqual(set(result["models"]), {"A", "B", "C"})
        self.assertEqual(set(result["baselines"]), {"default_model", "elo_with_skip"})
        for key in ("A", "B", "C"):
            self.assertIn(result["models"][key]["selected"], t.CHOICES)
        for key in ("default_model", "elo_with_skip"):
            self.assertIn(result["baselines"][key]["selected"], t.CHOICES)
        self.assertEqual(set(result["frozen_holdout_selection"]),
                         {"A", "B", "C", "default_model", "elo_with_skip"})


class TestTrainFeatureScaling(unittest.TestCase):
    def test_scales_are_train_rms_of_provided_rows(self):
        rows = [
            {"case": "c1", "action": "X",
             "features": {"offense": 3.0, "defense": 4.0, "engine": 0.0}, "y": 1.0},
            {"case": "c2", "action": "Y",
             "features": {"offense": 0.0, "defense": 0.0, "engine": 5.0}, "y": 2.0},
        ]
        result = t.fit_ridge(rows)
        self.assertAlmostEqual(result["scales"]["offense"], math.sqrt((9.0 + 0.0) / 2.0))
        self.assertAlmostEqual(result["scales"]["defense"], math.sqrt((16.0 + 0.0) / 2.0))
        self.assertAlmostEqual(result["scales"]["engine"], math.sqrt((0.0 + 25.0) / 2.0))

    def test_scales_ignore_extra_rows_not_passed(self):
        base = [{"case": "c", "action": "X",
                 "features": {"offense": 2.0, "defense": 2.0, "engine": 2.0}, "y": 1.0}]
        result = t.fit_ridge(base)
        # Single train row -> RMS is the absolute value, not some heldout scale.
        self.assertAlmostEqual(result["scales"]["offense"], 2.0)


class TestBEndpoints(unittest.TestCase):
    def test_score_endpoints_are_pure_components(self):
        self.assertAlmostEqual(t.b_score(0.37, 1.2, 0.0), 0.37)
        self.assertAlmostEqual(t.b_score(0.37, 1.2, 1.0), 1.2)
        self.assertAlmostEqual(t.b_score(0.37, 1.2, 0.5), 0.5 * 0.37 + 0.5 * 1.2)

    def test_lambda_zero_matches_mechanism_and_one_matches_elo(self):
        deck = next(s["deck"] for s in m.SCENARIOS if s["name"] == "duplicates")
        a10 = json.loads(SCORES_A10.read_text(encoding="utf-8"))
        mechanism = t.rank_mechanism(deck, t.CHOICES, m.DEFAULT_PARAMS)["ranking"]
        elo = t.rank_elo(t.CHOICES, a10)["ranking"]
        b_zero = t.rank_b(deck, t.CHOICES, 1.0, a10, 0.0)["ranking"]
        b_one = t.rank_b(deck, t.CHOICES, 1.0, a10, 1.0)["ranking"]
        self.assertEqual(b_zero, mechanism)
        self.assertEqual(b_one, elo)

    def test_missing_elo_fails_loudly(self):
        with self.assertRaises(t.MissingEloError):
            t.elo_delta("ADRENALINE", {})
        with self.assertRaises(t.MissingEloError):
            t.elo_delta("ADRENALINE", {"SKIP": {"elo": 1500.0}})
        with self.assertRaises(t.MissingEloError):
            t.elo_delta("ADRENALINE", {"ADRENALINE": {"elo": None}, "SKIP": {"elo": 1500.0}})
        self.assertAlmostEqual(t.elo_delta("skip", {"SKIP": {"elo": 1500.0}}), 0.0)


class TestRidgeExample(unittest.TestCase):
    def test_identity_ridge_exact(self):
        identity = [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]]
        solution = t.ridge_solve(identity, [2.0, 4.0, 6.0], 1.0)
        for got, want in zip(solution, [1.0, 2.0, 3.0]):
            self.assertAlmostEqual(got, want)

    def test_gaussian_solve_matches_matrix_product(self):
        matrix = [[2.0, 1.0, 1.0], [1.0, 3.0, 2.0], [1.0, 0.0, 0.0]]
        target = [4.0, 5.0, 6.0]
        solution = t.gauss_solve(matrix, target)
        for row, value in zip(matrix, target):
            self.assertAlmostEqual(sum(a * b for a, b in zip(row, solution)), value)


class TestPairing(unittest.TestCase):
    def _matched(self):
        return {
            "s1": {"KNIFE_TRAP": 1.0, "ADRENALINE": 0.5, "skip": 0.2},
            "s2": {"KNIFE_TRAP": 0.8, "ADRENALINE": 0.4, "skip": 0.1},
        }

    def test_same_action_is_exact_zero_and_indistinguishable(self):
        comparison = t.paired_comparison(self._matched(), "KNIFE_TRAP", "KNIFE_TRAP")
        self.assertTrue(comparison["same_action"])
        self.assertEqual(comparison["status"], "indistinguishable_same_action")
        self.assertFalse(comparison["confirmed"])
        self.assertAlmostEqual(comparison["stats"]["mean"], 0.0)
        self.assertTrue(all(diff == 0.0 for diff in comparison["differences"]))

    def test_different_actions_use_paired_utility(self):
        comparison = t.paired_comparison(self._matched(), "KNIFE_TRAP", "ADRENALINE")
        self.assertEqual(comparison["differences"], [0.5, 0.4])
        self.assertFalse(comparison["same_action"])


class TestAnalyzeConfirmation(unittest.TestCase):
    def _replication(self):
        matched, records = {}, {}
        for index in range(1, 201):
            seed = "SHIVFITX%04d" % index
            matched[seed] = {"KNIFE_TRAP": 1.0, "ADRENALINE": 0.2, "skip": -0.1}
            records[seed] = {
                "KNIFE_TRAP": _record("win", 70, 0),
                "ADRENALINE": _record("loss", 0, 100),
                "skip": _record("loss", 0, 50),
            }
        return {"case": "holdout-duplicates", "matched": matched, "records": records,
                "n": len(matched)}

    def test_confirmation_family_and_same_action_verdict(self):
        replications = {"H": self._replication(), "R": self._replication()}
        selections = {"A": "KNIFE_TRAP", "B": "KNIFE_TRAP", "C": "ADRENALINE",
                      "default_model": "ADRENALINE", "elo_with_skip": "skip"}
        result = t.analyze_confirmation(replications, selections)
        self.assertEqual(result["family_comparisons"], 12)
        self.assertEqual(len(result["comparisons"]), 12)
        self.assertTrue(result["model_verdicts"]["A"]["confirmed"])
        # C picks the same action as the default baseline in both replications ->
        # exact zero -> indistinguishable and not confirmed.
        self.assertEqual(result["model_verdicts"]["C"]["confirmed"], False)
        self.assertEqual(result["model_verdicts"]["C"]["indistinguishable"], 2)
        self.assertEqual(result["model_verdicts"]["C"]["positive"], 2)
        self.assertEqual(len(result["combined_secondary"]), 6)
        self.assertEqual(len(result["combined_secondary"][0]["differences"]), 400)


class TestMultiplicityThreshold(unittest.TestCase):
    def test_bonferroni_quantile_and_cornish_fisher(self):
        expected_z = statistics.NormalDist().inv_cdf(1.0 - t.ALPHA / (2.0 * t.FAMILY_COMPARISONS))
        self.assertAlmostEqual(t.adjusted_normal_z(), expected_z)
        self.assertAlmostEqual(t.adjusted_normal_z(), statistics.NormalDist().inv_cdf(1.0 - 0.05 / 24.0))
        self.assertAlmostEqual(t.adjusted_t_critical(199), t.cornish_fisher_t(expected_z, 199))
        # Adjusted threshold is strictly wider than the ordinary 95% interval.
        self.assertGreater(t.adjusted_t_critical(199), t.ordinary_t_critical(199))
        self.assertGreater(t.adjusted_t_critical(199), 2.8)

    def test_add_adjusted_interval_only_for_n_ge_25(self):
        small = t.add_adjusted_interval(fit.summary_stats([1.0, 2.0, 3.0]))
        self.assertIsNone(small["adjusted_ci_low"])
        values = [float(index % 7 - 3) for index in range(200)]
        big = t.add_adjusted_interval(fit.summary_stats(values))
        self.assertIsNotNone(big["adjusted_ci_low"])
        self.assertGreater(big["adjusted_ci_high"] - big["adjusted_ci_low"],
                           big["ci95_high"] - big["ci95_low"])


class TestNoErrorAsLoss(unittest.TestCase):
    def test_error_seed_is_not_matched(self):
        case = {"name": "holdout-duplicates", "choices": list(t.CHOICES)}
        per_seed = {
            "S1": {"KNIFE_TRAP": _record("win", 10, 0),
                   "ADRENALINE": _record("loss", 0, 50),
                   "skip": _record("error", None, None, eligible=False)},
            "S2": {"KNIFE_TRAP": _record("win", 10, 0),
                   "ADRENALINE": _record("timeout", None, None, eligible=False),
                   "skip": _record("missing", None, None, eligible=False)},
        }
        matched = fit.matched_seed_utilities(case, per_seed)
        self.assertEqual(matched, {})

    def test_utility_rejects_incomplete_status(self):
        for status in (None, "error", "timeout", "missing", "incomplete"):
            with self.assertRaises(fit.FitError):
                fit.utility(_record(status, 10, 10))

    def test_utility_matches_frozen_formula(self):
        self.assertAlmostEqual(fit.utility(_record("win", 35, 9)), 1.25)
        self.assertAlmostEqual(fit.utility(_record("loss", 40, 100)), -0.25)


class _StubPrior:
    def canonical_sha(self, value):
        return fit.canonical_sha(value)

    def make_task(self, deck, encounter, seed, action, role):
        name = "%s-%s-%s-%s" % (deck["id"], encounter, seed, action)
        return {"name": name, "deck": deck["id"], "encounter": encounter, "seed": seed,
                "action": action, "role": role}


class TestConfirmTaskBuilding(unittest.TestCase):
    def test_holdout_deck_and_600_unique_tasks(self):
        cards = fit.deck_for_case(t.HOLDOUT_CASE)
        char_set = set(cards) - {"ASCENDERS_BANE"}
        prior = _StubPrior()
        deck = t.build_holdout_deck(prior, char_set, set())
        self.assertEqual(deck["deck_length"], 19)
        self.assertEqual(deck["bane_count"], 1)
        self.assertEqual(deck["unsupported"], [])
        self.assertEqual(len(deck["fixture"]["deck"]), 19)
        self.assertEqual(deck["fixture"]["candidates"], ["KNIFE_TRAP", "ADRENALINE"])
        tasks = t.make_confirm_tasks(prior, deck, Path("unused-root"))
        self.assertEqual(len(tasks), 600)
        self.assertEqual(len({task["name"] for task in tasks}), 600)
        for task in tasks:
            self.assertIn(task["action"], t.CHOICES)


class TestAParameterValidation(unittest.TestCase):
    def test_multiplier_recovery_round_trip(self):
        tuned = fit.apply_multipliers(m.DEFAULT_PARAMS, 1.5, 0.5)
        defense, engine, how = t.identify_a_multipliers(tuned)
        self.assertEqual((defense, engine), (1.5, 0.5))
        self.assertEqual(how, "grid_match")
        baseline = t.identify_a_multipliers(m.DEFAULT_PARAMS)
        self.assertEqual(baseline[:2], (1.0, 1.0))

    def test_unknown_params_rejected(self):
        bogus = json.loads(json.dumps(m.DEFAULT_PARAMS))
        bogus["weights"]["w_shiv_play"] += 0.123
        with self.assertRaises(t.ParamValidationError):
            t.identify_a_multipliers(bogus)


if __name__ == "__main__":
    unittest.main()
