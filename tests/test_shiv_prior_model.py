#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Scoped tests for scripts/shiv_prior_model.py.

Offline, standard-library only. No battles, no network, no production edits.
The tests pin the design invariants called out for the task; they never assert
an arbitrary fixture winner.

Run with:
    python -m unittest discover -s tests -p test_shiv_prior_model.py
"""

import copy
import json
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import shiv_prior_model as m  # noqa: E402

STARTER = (
    ["STRIKE_SILENT"] * 4
    + ["DEFEND_SILENT"] * 4
    + ["SURVIVOR", "NEUTRALIZE", "ASCENDERS_BANE"]
)
PARAMS = copy.deepcopy(m.DEFAULT_PARAMS)


class TestValidation(unittest.TestCase):
    def test_unknown_id_rejected(self):
        with self.assertRaises(ValueError):
            m.evaluate_deck(["NOT_A_CARD"], params=PARAMS)

    def test_upgrade_rejected(self):
        with self.assertRaises(ValueError):
            m.evaluate_deck(["BLADE_DANCE+"], params=PARAMS)

    def test_external_reference_rejected(self):
        with self.assertRaises(ValueError):
            m.evaluate_deck(["PANACHE"], params=PARAMS)

    def test_base_card_not_a_candidate(self):
        with self.assertRaises(ValueError):
            m.rank_choices(STARTER, candidates=["STRIKE_SILENT"], params=PARAMS)

    def test_unknown_candidate_rejected(self):
        with self.assertRaises(ValueError):
            m.rank_choices(STARTER, candidates=["NOT_A_CARD"], params=PARAMS)

    def test_base_context_is_allowed_in_deck(self):
        result = m.evaluate_deck(m.BASE_DECK_CONTEXT, params=PARAMS)
        self.assertGreater(result.total, 0.0)

    def test_unknown_context_key_rejected(self):
        with self.assertRaises(ValueError):
            m.evaluate_deck(STARTER, context={"bogus": 1}, params=PARAMS)

    def test_negative_context_rejected(self):
        with self.assertRaises(ValueError):
            m.evaluate_deck(STARTER, context={"turn_horizon": -1}, params=PARAMS)

    def test_non_integer_horizon_rejected(self):
        with self.assertRaises(ValueError):
            m.evaluate_deck(STARTER, context={"turn_horizon": 2.5}, params=PARAMS)

    def test_non_finite_context_rejected(self):
        with self.assertRaises(ValueError):
            m.evaluate_deck(STARTER, context={"energy_per_turn": float("inf")}, params=PARAMS)
        with self.assertRaises(ValueError):
            m.evaluate_deck(STARTER, context={"initial_hand": float("nan")}, params=PARAMS)

    def test_zero_horizon_permitted_and_zero(self):
        result = m.evaluate_deck(STARTER, context={"turn_horizon": 0}, params=PARAMS)
        self.assertEqual(result.total, 0.0)

    def test_params_assumptions_supply_defaults(self):
        custom = copy.deepcopy(PARAMS)
        custom["assumptions"] = {
            "initial_hand": 2,
            "cards_drawn_per_turn": 1,
            "energy_per_turn": 0,
            "turn_horizon": 1,
            "power_setup_delay_turns": 0,
        }
        result = m.evaluate_deck(STARTER, context=None, params=custom)
        self.assertEqual(result.features["base_seen"], 2)
        self.assertEqual(result.features["energy_total"], 0.0)


class TestPurityAndDeterminism(unittest.TestCase):
    def test_inputs_not_mutated(self):
        deck = list(STARTER) + ["BLADE_DANCE", "ACCURACY", "CLOAK_AND_DAGGER"]
        candidates = ["ACCURACY", "BLADE_DANCE", "SKIP"]
        deck_before = copy.deepcopy(deck)
        candidates_before = copy.deepcopy(candidates)
        m.evaluate_deck(deck, params=PARAMS)
        m.rank_choices(deck, candidates=candidates, params=PARAMS)
        self.assertEqual(deck, deck_before)
        self.assertEqual(candidates, candidates_before)

    def test_order_invariant(self):
        deck_a = list(STARTER) + ["BLADE_DANCE", "CLOAK_AND_DAGGER", "ACCURACY"]
        deck_b = list(reversed(deck_a))
        eval_a = m.evaluate_deck(deck_a, params=PARAMS)
        eval_b = m.evaluate_deck(deck_b, params=PARAMS)
        self.assertAlmostEqual(eval_a.total, eval_b.total, places=9)
        self.assertAlmostEqual(
            eval_a.features["shiv_supply"], eval_b.features["shiv_supply"], places=9
        )

    def test_deterministic_json(self):
        first = m.rank_choices(STARTER, params=PARAMS)
        second = m.rank_choices(STARTER, params=PARAMS)
        self.assertEqual(json.dumps(first, sort_keys=True), json.dumps(second, sort_keys=True))

    def test_skip_is_zero_and_present(self):
        result = m.rank_choices(STARTER, params=PARAMS)
        self.assertEqual(result["skip"]["mech_delta"], 0.0)
        self.assertEqual(result["skip"]["final_score"], 0.0)
        self.assertIn("SKIP", result["ranking"])

    def test_duplicates_are_counted(self):
        one = m.evaluate_deck(STARTER + ["BLADE_DANCE"], params=PARAMS)
        two = m.evaluate_deck(STARTER + ["BLADE_DANCE"] * 2, params=PARAMS)
        self.assertGreater(two.features["shiv_supply"], one.features["shiv_supply"])
        self.assertNotAlmostEqual(one.total, two.total, places=6)


class TestDrawAndEnergy(unittest.TestCase):
    def test_horizon_draw_formula(self):
        ctx = m.Context(initial_hand=5, cards_drawn_per_turn=5, turn_horizon=3)
        result = m.evaluate_deck(STARTER, context=ctx, params=PARAMS)
        # initial_hand + (turns - 1) * draw_per_turn == 5 + 2*5 == 15
        self.assertEqual(result.features["base_seen"], 15)

    def test_zero_turns_zero_plays_and_utility(self):
        result = m.evaluate_deck(
            STARTER + ["BLADE_DANCE", "ACCURACY"], context={"turn_horizon": 0}, params=PARAMS
        )
        self.assertEqual(result.total, 0.0)
        for key, value in result.components.items():
            self.assertEqual(value, 0.0, key)
        self.assertEqual(result.features["shiv_supply"], 0.0)
        self.assertEqual(result.features["cards_seen"], 0.0)

    def test_extra_draw_changes_access_and_cycling(self):
        # Small horizon keeps the no-draw deck under one full cycle so both
        # access and cycling can move when a draw card is added.
        ctx = m.Context(turn_horizon=2, energy_per_turn=10)
        without = m.evaluate_deck(STARTER, context=ctx, params=PARAMS)
        with_draw = m.evaluate_deck(STARTER + ["ACROBATICS"], context=ctx, params=PARAMS)
        self.assertEqual(without.features["extra_draw"], 0.0)
        self.assertGreater(with_draw.features["extra_draw"], 0.0)
        self.assertGreater(
            with_draw.features["draw_fraction"], without.features["draw_fraction"]
        )
        self.assertGreater(with_draw.features["cycles"], without.features["cycles"])
        # The effect is not confined to the flat draw_support component.
        self.assertGreater(with_draw.features["cards_seen"], without.features["cards_seen"])

    def test_zero_cost_never_suppressed_by_paid_capacity(self):
        deck = (
            list(STARTER)
            + ["DEFLECT"] * 4
            + ["LEG_SWEEP"] * 4
            + ["KNIFE_TRAP"] * 4
        )
        low = m.evaluate_deck(deck, context=m.Context(energy_per_turn=0), params=PARAMS)
        high = m.evaluate_deck(deck, context=m.Context(energy_per_turn=99), params=PARAMS)
        self.assertGreater(low.features["zero_cost_plays"], 0.0)
        self.assertAlmostEqual(
            low.features["zero_cost_plays"], high.features["zero_cost_plays"], places=12
        )
        self.assertLess(low.features["paid_plays"], high.features["paid_plays"])

    def test_power_delay_at_horizon_gives_no_payoff(self):
        ctx = m.Context(turn_horizon=3, power_setup_delay_turns=3)
        deck = list(STARTER) + [
            "ACCURACY",
            "PHANTOM_BLADES",
            "AFTERIMAGE",
            "FOOTWORK",
            "BLADE_DANCE",
        ]
        result = m.evaluate_deck(deck, context=ctx, params=PARAMS)
        self.assertEqual(result.features["power_active_fraction"], 0.0)
        self.assertEqual(result.features["active_turns"], 0.0)
        for component in ("accuracy", "phantom_first_shiv", "afterimage", "footwork"):
            self.assertEqual(result.components[component], 0.0, component)

    def test_power_active_fraction_scales_payoffs(self):
        deck = list(STARTER) + ["BLADE_DANCE"] * 3 + ["ACCURACY"]
        immediate = m.evaluate_deck(
            deck, context=m.Context(turn_horizon=4, power_setup_delay_turns=0), params=PARAMS
        )
        delayed = m.evaluate_deck(
            deck, context=m.Context(turn_horizon=4, power_setup_delay_turns=1), params=PARAMS
        )
        self.assertGreater(immediate.components["accuracy"], delayed.components["accuracy"])
        self.assertGreater(
            immediate.features["power_active_fraction"],
            delayed.features["power_active_fraction"],
        )


class TestMechanismFeatures(unittest.TestCase):
    def test_no_producer_means_accuracy_synergy_zero(self):
        deck = list(STARTER) + ["ACCURACY"] * 2
        result = m.evaluate_deck(deck, params=PARAMS)
        self.assertEqual(result.features["shiv_supply"], 0.0)
        self.assertEqual(result.components["accuracy"], 0.0)
        self.assertEqual(result.components["phantom_first_shiv"], 0.0)

    def test_phantom_first_shiv_caps_at_active_turns(self):
        ctx = m.Context(turn_horizon=4, power_setup_delay_turns=1)
        deck = list(STARTER) + ["PHANTOM_BLADES"] + ["BLADE_DANCE"] * 20
        result = m.evaluate_deck(deck, context=ctx, params=PARAMS)
        self.assertEqual(result.features["active_turns"], 3.0)
        w = PARAMS["weights"]["w_phantom_first_shiv"]
        self.assertGreaterEqual(result.features["shiv_supply"], ctx.turn_horizon)
        expected = w * result.features["phantom_active"] * min(
            result.features["active_turns"], result.features["shiv_supply"]
        )
        self.assertAlmostEqual(result.components["phantom_first_shiv"], expected, places=9)
        # More shiv supply cannot raise it beyond one trigger per active turn.
        self.assertLessEqual(result.components["phantom_first_shiv"], w * 3.0 + 1e-9)

    def test_afterimage_counts_producer_plays_and_excludes_replays(self):
        ctx = m.Context(energy_per_turn=99, turn_horizon=4)
        deck = list(STARTER) + ["AFTERIMAGE", "BLADE_DANCE"]
        result = m.evaluate_deck(deck, context=ctx, params=PARAMS)
        f = result.features
        self.assertGreater(f["shiv_supply"], 0.0)
        self.assertGreater(f["producer_plays"], 0.0)
        # Producers' own plays are counted in addition to generated shivs.
        self.assertGreaterEqual(
            f["afterimage_triggers"], f["shiv_supply"] + f["producer_plays"] - 1e-9
        )
        self.assertAlmostEqual(
            f["afterimage_non_replay_plays"],
            f["played_cards_total"] - f["afterimage_activation_plays"],
            places=12,
        )
        # Knife Trap replays are tracked but never injected as triggers.
        with_trap = m.evaluate_deck(deck + ["KNIFE_TRAP"], context=ctx, params=PARAMS)
        self.assertGreater(with_trap.features["knife_trap_replays"], 0.0)
        self.assertLessEqual(
            with_trap.features["afterimage_triggers"],
            with_trap.features["shiv_supply"]
            + with_trap.features["played_cards_total"]
            - with_trap.features["afterimage_activation_plays"]
            + 1e-9,
        )

    def test_footwork_does_not_scale_afterimage(self):
        # No normal direct-block card is present, so Footwork contributes zero
        # even though Afterimage triggers exist.
        deck = (
            ["STRIKE_SILENT"] * 4
            + ["NEUTRALIZE", "ASCENDERS_BANE"]
            + ["FOOTWORK", "AFTERIMAGE"]
            + ["BLADE_DANCE"] * 4
        )
        result = m.evaluate_deck(deck, params=PARAMS)
        self.assertGreater(result.features["afterimage_triggers"], 0.0)
        self.assertEqual(result.features["normal_block_plays"], 0.0)
        self.assertEqual(result.components["footwork"], 0.0)

    def test_footwork_applies_to_base_block_cards(self):
        deck = ["DEFEND_SILENT"] * 3 + ["SURVIVOR", "FOOTWORK"]
        result = m.evaluate_deck(deck, params=PARAMS)
        self.assertGreater(result.features["normal_block_plays"], 0.0)
        self.assertGreater(result.components["footwork"], 0.0)

    def test_footwork_uses_only_normal_block_formula(self):
        deck = list(STARTER) + ["FOOTWORK", "CLOAK_AND_DAGGER", "DEFLECT", "AFTERIMAGE"]
        result = m.evaluate_deck(deck, params=PARAMS)
        w = PARAMS["weights"]["w_dex_block"]
        cap = PARAMS["caps"]["dex_block_cap"]
        expected = w * result.features["footwork_active"] * min(
            result.features["normal_block_plays"], cap
        )
        self.assertAlmostEqual(result.components["footwork"], expected, places=9)

    def test_no_recursive_knife_trap_supply(self):
        # Huge energy removes capacity confounds; small deck keeps draw_fraction 1.
        ctx = m.Context(energy_per_turn=99, turn_horizon=4)
        base = list(STARTER) + ["BLADE_DANCE"] * 2
        without = m.evaluate_deck(base, context=ctx, params=PARAMS)
        w = PARAMS["weights"]["w_knife_trap_replay"]
        cap = PARAMS["caps"]["knife_trap_replay_cap"]
        for copies in range(1, 8):
            with_traps = m.evaluate_deck(base + ["KNIFE_TRAP"] * copies, context=ctx, params=PARAMS)
            self.assertAlmostEqual(
                with_traps.features["shiv_supply"], without.features["shiv_supply"], places=9
            )
            self.assertAlmostEqual(
                with_traps.features["exhausted_pool"], with_traps.features["shiv_supply"], places=9
            )
            self.assertLessEqual(with_traps.components["knife_trap"], w * cap + 1e-9)

    def test_accuracy_replay_opportunity_named_feature(self):
        # Huge energy removes capacity confounds; the trap is active and the
        # ordinary Shiv supply is non-zero, so the replay opportunity is positive.
        ctx = m.Context(energy_per_turn=99, turn_horizon=4)
        deck = list(STARTER) + ["ACCURACY", "KNIFE_TRAP", "BLADE_DANCE"]
        result = m.evaluate_deck(deck, context=ctx, params=PARAMS)
        f = result.features
        self.assertIn("knife_trap_replay_opportunity", f)
        self.assertGreater(f["knife_trap_replays"], 0.0)
        self.assertGreater(f["knife_trap_active"], 0.0)
        self.assertGreater(f["knife_trap_replay_opportunity"], 0.0)
        # Named quantity is exactly kt_active * min(exhausted_pool, replay_cap).
        self.assertAlmostEqual(
            f["knife_trap_replay_opportunity"],
            f["knife_trap_active"] * f["knife_trap_replays"],
            places=12,
        )
        self.assertAlmostEqual(
            f["knife_trap_replay_opportunity"],
            f["knife_trap_active"]
            * min(f["exhausted_pool"], PARAMS["caps"]["knife_trap_replay_cap"]),
            places=12,
        )

    def test_accuracy_component_is_ordinary_plus_replay(self):
        ctx = m.Context(energy_per_turn=99, turn_horizon=4)
        deck = list(STARTER) + ["ACCURACY", "KNIFE_TRAP", "BLADE_DANCE"]
        result = m.evaluate_deck(deck, context=ctx, params=PARAMS)
        f = result.features
        w = PARAMS["weights"]["w_accuracy_per_shiv"]
        expected = w * f["accuracy_active"] * (
            f["shiv_supply"] + f["knife_trap_replay_opportunity"]
        )
        self.assertAlmostEqual(result.components["accuracy"], expected, places=9)
        # The replay opportunity strictly improves accuracy over ordinary supply.
        self.assertGreater(
            result.components["accuracy"],
            w * f["accuracy_active"] * f["shiv_supply"],
        )

    def test_accuracy_replay_gain_survives_supply_and_activation_controls(self):
        # Normalize by active * supply so the comparison is not confounded by
        # the deck-size/dilution shift caused by adding Knife Trap.
        ctx = m.Context(energy_per_turn=99, turn_horizon=4)
        without = list(STARTER) + ["ACCURACY", "BLADE_DANCE"]
        with_trap = without + ["KNIFE_TRAP"]
        w = PARAMS["weights"]["w_accuracy_per_shiv"]
        a = m.evaluate_deck(without, context=ctx, params=PARAMS)
        b = m.evaluate_deck(with_trap, context=ctx, params=PARAMS)
        self.assertGreater(a.features["shiv_supply"], 0.0)
        self.assertGreater(b.features["shiv_supply"], 0.0)
        self.assertGreater(a.features["accuracy_active"], 0.0)
        self.assertGreater(b.features["accuracy_active"], 0.0)
        ratio_without = a.components["accuracy"] / (
            a.features["accuracy_active"] * a.features["shiv_supply"]
        )
        ratio_with = b.components["accuracy"] / (
            b.features["accuracy_active"] * b.features["shiv_supply"]
        )
        # Without a trap there is no replay opportunity -> plain per-Shiv weight.
        self.assertAlmostEqual(ratio_without, w, places=9)
        self.assertGreater(ratio_with, ratio_without + 1e-9)

    def test_accuracy_without_trap_matches_legacy_formula(self):
        ctx = m.Context(energy_per_turn=99, turn_horizon=4)
        deck = list(STARTER) + ["ACCURACY", "BLADE_DANCE"]
        result = m.evaluate_deck(deck, context=ctx, params=PARAMS)
        f = result.features
        w = PARAMS["weights"]["w_accuracy_per_shiv"]
        self.assertEqual(f["knife_trap_replay_opportunity"], 0.0)
        self.assertAlmostEqual(
            result.components["accuracy"],
            w * f["accuracy_active"] * f["shiv_supply"],
            places=12,
        )

    def test_replay_opportunity_bounded_and_never_feeds_supply(self):
        # Huge energy removes capacity confounds; horizon 5 keeps base_seen (25)
        # above every probed deck size, so adding copies never changes access.
        ctx = m.Context(energy_per_turn=99, turn_horizon=5)
        base = list(STARTER) + ["BLADE_DANCE"] * 2 + ["ACCURACY"]
        without = m.evaluate_deck(base, context=ctx, params=PARAMS)
        w = PARAMS["weights"]["w_accuracy_per_shiv"]
        cap = PARAMS["caps"]["knife_trap_replay_cap"]
        prev_opportunity = 0.0
        for copies in range(1, 8):
            with_traps = m.evaluate_deck(
                base + ["KNIFE_TRAP"] * copies, context=ctx, params=PARAMS
            )
            f = with_traps.features
            # No recursive growth: replay never changes supply or exhausted pool.
            self.assertAlmostEqual(
                f["shiv_supply"], without.features["shiv_supply"], places=9
            )
            self.assertAlmostEqual(
                f["exhausted_pool"], f["shiv_supply"], places=9
            )
            # Opportunity is monotone non-decreasing and capped by replay_cap.
            self.assertGreaterEqual(
                f["knife_trap_replay_opportunity"], prev_opportunity - 1e-12
            )
            self.assertLessEqual(f["knife_trap_replay_opportunity"], cap + 1e-9)
            # Accuracy stays bounded by active * (supply + cap).
            self.assertLessEqual(
                with_traps.components["accuracy"],
                w * f["accuracy_active"] * (f["shiv_supply"] + cap) + 1e-9,
            )
            prev_opportunity = f["knife_trap_replay_opportunity"]

    def test_replay_opportunity_not_injected_into_afterimage(self):
        ctx = m.Context(energy_per_turn=99, turn_horizon=4)
        deck = list(STARTER) + ["ACCURACY", "KNIFE_TRAP", "AFTERIMAGE", "BLADE_DANCE"]
        result = m.evaluate_deck(deck, context=ctx, params=PARAMS)
        f = result.features
        self.assertGreater(f["knife_trap_replay_opportunity"], 0.0)
        # Afterimage triggers use supply + played cards minus its own activation;
        # the replay opportunity is never an extra trigger source.
        expected = min(
            f["shiv_supply"] + f["afterimage_non_replay_plays"],
            PARAMS["caps"]["afterimage_trigger_cap"],
        )
        self.assertAlmostEqual(f["afterimage_triggers"], expected, places=9)

    def test_new_feature_is_zeroed_at_zero_horizon(self):
        result = m.evaluate_deck(
            STARTER + ["ACCURACY", "KNIFE_TRAP", "BLADE_DANCE"],
            context={"turn_horizon": 0},
            params=PARAMS,
        )
        self.assertEqual(result.features["knife_trap_replay_opportunity"], 0.0)

    def test_large_copy_sweep_eventually_negative(self):
        params = copy.deepcopy(PARAMS)
        params["assumptions"]["energy_per_turn"] = 3
        totals = [
            m.evaluate_deck(STARTER + ["BLADE_DANCE"] * k, params=params).total
            for k in range(0, 41)
        ]
        marginals = [totals[k + 1] - totals[k] for k in range(len(totals) - 1)]
        self.assertTrue(
            any(delta < 0.0 for delta in marginals[5:]),
            "expected diminishing returns to turn a later Blade Dance copy negative",
        )


class TestCommunityPrior(unittest.TestCase):
    def _prior_for(self, deck, candidate, community=True, edges=None):
        result = m.rank_choices(
            deck, candidates=[candidate], params=PARAMS,
            community=community, community_edges=edges,
        )
        return result["candidates"][0]

    def test_direction_accuracy_held_to_blade_dance(self):
        held_accuracy = list(STARTER) + ["ACCURACY"]
        row = self._prior_for(held_accuracy, "BLADE_DANCE")
        self.assertGreater(row["prior_applied"], 0.0)
        self.assertGreater(row["known_edges"], 0)
        self.assertGreater(row["positive_edges"], 0)
        # Reverse direction is not observed in the draft layer.
        held_blade = list(STARTER) + ["BLADE_DANCE"]
        reverse = self._prior_for(held_blade, "ACCURACY")
        self.assertEqual(reverse["prior_applied"], 0.0)
        self.assertGreater(reverse["unknown_edges"], 0)

    def test_held_copies_do_not_multiply_prior(self):
        one = self._prior_for(list(STARTER) + ["ACCURACY"], "BLADE_DANCE")
        three = self._prior_for(list(STARTER) + ["ACCURACY"] * 3, "BLADE_DANCE")
        self.assertAlmostEqual(one["prior_applied"], three["prior_applied"], places=12)
        self.assertAlmostEqual(one["prior_raw"], three["prior_raw"], places=12)

    def test_prior_is_bounded_and_nonnegative(self):
        cap = PARAMS["community"]["prior_cap"]
        row = self._prior_for(list(STARTER) + ["ACCURACY", "INFINITE_BLADES", "KNIFE_TRAP"],
                              "BLADE_DANCE")
        self.assertLessEqual(row["prior_applied"], cap + 1e-12)
        self.assertGreaterEqual(row["prior_applied"], 0.0)

    def test_known_edges_counts_nonpositive_observation(self):
        edges = {("ACCURACY", "BLADE_DANCE"): {"lift": 0.9, "offers": 500}}
        row = self._prior_for(list(STARTER) + ["ACCURACY"], "BLADE_DANCE", edges=edges)
        self.assertEqual(row["known_edges"], 1)
        self.assertEqual(row["positive_edges"], 0)
        self.assertEqual(row["prior_applied"], 0.0)
        self.assertEqual(row["prior_raw"], 0.0)

    def test_prior_never_rescues_nonpositive(self):
        # Held ACCURACY -> offered KNIFE_TRAP is a positive observed direction,
        # but without producers the mechanism delta is non-positive.
        deck = list(STARTER) + ["ACCURACY"]
        result = m.rank_choices(deck, params=PARAMS, community=True)
        for row in result["candidates"]:
            if row["mech_delta"] <= 0.0:
                self.assertFalse(row["prior_used"])
                self.assertEqual(row["final_score"], row["mech_delta"])

    def test_prior_never_overrides_large_gap(self):
        deck = list(STARTER) + ["BLADE_DANCE"] * 6 + ["CLOAK_AND_DAGGER"] * 6 + ["ACCURACY"] * 4
        result = m.rank_choices(deck, params=PARAMS, community=True)
        cap = PARAMS["community"]["prior_cap"]
        positives = [row["mech_delta"] for row in result["candidates"] if row["mech_delta"] > 0]
        if positives:
            best = max(positives)
            for row in result["candidates"]:
                if row["mech_delta"] > 0 and (best - row["mech_delta"]) > cap:
                    self.assertFalse(row["prior_used"])

    def test_community_off_uses_mechanism_only(self):
        deck = list(STARTER) + ["ACCURACY", "BLADE_DANCE"]
        on = m.rank_choices(deck, params=PARAMS, community=True)
        off = m.rank_choices(deck, params=PARAMS, community=False)
        self.assertFalse(off["community_enabled"])
        for row in off["candidates"]:
            self.assertEqual(row["prior_applied"], 0.0)
            self.assertFalse(row["prior_used"])
            self.assertEqual(row["final_score"], row["mech_delta"])
        for row in on["candidates"]:
            expected = row["mech_delta"] + (row["prior_applied"] if row["prior_used"] else 0.0)
            self.assertAlmostEqual(row["final_score"], expected, places=12)

    def test_unknown_edges_are_neutral(self):
        # BLADE_DANCE has no observed outgoing draft edge.
        row = self._prior_for(list(STARTER) + ["BLADE_DANCE"], "ACCURACY")
        self.assertEqual(row["prior_applied"], 0.0)
        self.assertEqual(row["prior_raw"], 0.0)
        self.assertGreaterEqual(row["unknown_edges"], 1)
        self.assertEqual(row["positive_edges"], 0)


class TestRankingInvariants(unittest.TestCase):
    def test_winner_exactly_equals_ranking_zero(self):
        result = m.rank_choices(list(STARTER) + ["ACCURACY", "BLADE_DANCE"], params=PARAMS)
        self.assertEqual(result["winner"], result["ranking"][0])

    def test_candidate_ids_deduped(self):
        result = m.rank_choices(
            STARTER, candidates=["BLADE_DANCE", "BLADE_DANCE", "ACCURACY"], params=PARAMS
        )
        self.assertEqual(len(result["candidates"]), 2)
        self.assertEqual(len(result["ranking"]), 3)  # 2 candidates + SKIP


class TestCli(unittest.TestCase):
    def test_input_output_roundtrip_and_ablation(self):
        import tempfile

        payload = {
            "deck": list(STARTER) + ["ACCURACY", "BLADE_DANCE"],
            "candidates": ["BLADE_DANCE", "ACCURACY"],
        }
        with tempfile.TemporaryDirectory() as tmp:
            in_path = Path(tmp) / "in.json"
            out_path = Path(tmp) / "out.json"
            in_path.write_text(json.dumps(payload), encoding="utf-8")
            rc = m.main(["--input", str(in_path), "--output", str(out_path),
                         "--community", "off"])
            self.assertEqual(rc, 0)
            result = json.loads(out_path.read_text(encoding="utf-8"))
            self.assertFalse(result["community_enabled"])
            self.assertEqual(len(result["ranking"]), 3)  # 2 candidates + SKIP
            for row in result["candidates"]:
                self.assertEqual(row["final_score"], row["mech_delta"])

    def test_input_rejects_unknown(self):
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            in_path = Path(tmp) / "in.json"
            in_path.write_text(json.dumps({"deck": ["NOPE"]}), encoding="utf-8")
            with self.assertRaises(ValueError):
                m.main(["--input", str(in_path)])


class TestFixtures(unittest.TestCase):
    def test_eight_scenarios_generated(self):
        reports = m.build_scenarios(params=PARAMS)
        self.assertEqual(len(reports), 8)
        names = [r["name"] for r in reports]
        self.assertEqual(len(set(names)), 8)

    def test_scenario_rankings_cover_14_plus_skip(self):
        reports = m.build_scenarios(params=PARAMS)
        for report in reports:
            self.assertEqual(len(report["ranking"]), len(m.WHITELIST) + 1)
            self.assertIn("SKIP", report["ranking"])

    def test_scenario_winner_equals_ranking_zero(self):
        reports = m.build_scenarios(params=PARAMS)
        for report in reports:
            self.assertEqual(report["winner"], report["ranking"][0], report["name"])

    def test_scenarios_accept_params_assumptions(self):
        custom = copy.deepcopy(PARAMS)
        custom["assumptions"] = {
            "initial_hand": 1,
            "cards_drawn_per_turn": 0,
            "energy_per_turn": 1,
            "turn_horizon": 1,
            "power_setup_delay_turns": 0,
        }
        reports = m.build_scenarios(params=custom)
        self.assertEqual(len(reports), 8)


if __name__ == "__main__":
    unittest.main()
