#!/usr/bin/env python3
"""Offline tests for scripts/building_pick_cases.py.

All tests use a synthetic adapter document and temporary directories: no battle
is launched, no game is read. They pin the fixture contract before the builder
is used on real adapter output:

* skip copies the base scenario byte-for-byte and records value 0;
* an add action appends exactly one unupgraded card and removes nothing (in
  particular it never changes the number of DEFEND cards);
* every action of a fixture shares the same context and per-copy upgrades;
* the exported actual deck must equal the starting-deck expansion + selections
  + exactly one Ascender's Bane;
* missing/unknown identity, a non-finite score, a duplicate candidate, a bad
  array length, a negative/non-integer upgrade, an Ascender's Bane already
  listed, a path-traversal id, a runtime ascension/act/HP mismatch, and an
  argmax/bestReward mismatch all fail loudly;
* an existing output directory is never overwritten;
* the frozen seed sets are disjoint and the builder uses no mutable defaults.

Run with:
    python -m unittest discover -s tests -p test_building_pick_cases.py
"""

import copy
import inspect
import json
import math
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import building_pick_cases as b  # noqa: E402

CANDIDATE_IDS = ["BACKFLIP", "BLADE_DANCE", "DAGGER_SPRAY"]
CANDIDATE_SCORES = [12.5, -3.0, 4.25]
STARTER_DECK = (
    [("STRIKE_SILENT", 0) for _ in range(5)]
    + [("DEFEND_SILENT", 0) for _ in range(5)]
    + [("NEUTRALIZE", 0), ("SURVIVOR", 0)]
)


def candidate(card_id, score):
    return {
        "id": card_id,
        "canonicalId": card_id,
        "cardPool": "character",
        "cardType": "Skill",
        "rarity": "Common",
        "upgrade": 0,
        "score": score,
        "finite": True,
        "reason": "damage",
    }


def deck_entry(card_id, upgrade=0):
    return {"id": card_id, "upgrade": upgrade}


def starter_fixture():
    deck = [deck_entry(card_id, upgrade) for card_id, upgrade in STARTER_DECK]
    deck.append(deck_entry("ASCENDERS_BANE"))
    starter = [deck_entry(card_id, upgrade) for card_id, upgrade in STARTER_DECK]
    return {
        "id": "silent-starter-proxy",
        "label": "PROXY",
        "stateKind": "synthetic-starter",
        "proxy": True,
        "proxyReason": "test proxy",
        "seed": "PICKPROXY01",
        "characterId": "SILENT",
        "verifiedCharacterId": "SILENT",
        "ascension": 10,
        "verifiedAscension": 10,
        "actIndex": 0,
        "verifiedActIndex": 0,
        "encounterId": "FUZZY_WURM_CRAWLER_WEAK",
        "encounterSuitability": "test",
        "maxHp": 70,
        "currentHp": 70,
        "characterStartingHp": 70,
        "verifiedMaxHp": 70,
        "verifiedCurrentHp": 70,
        "relics": [],
        "potions": [],
        "relicsEmptyByDesign": True,
        "potionsEmptyByDesign": True,
        "ascendersBaneRequested": True,
        "ascendersBaneCount": 1,
        "startingDeck": "character-starting-deck",
        "resolvedStartingDeck": starter,
        "deckSize": len(deck),
        "deck": deck,
        "context": {
            "actIndex": 0,
            "ascension": 10,
            "seed": "PICKPROXY01",
            "encounterId": "FUZZY_WURM_CRAWLER_WEAK",
            "mapContext": "none",
            "sourceInput": None,
            "sourceInputSha256": None,
            "sourceInputValidation": None,
        },
        "selection": {
            "includeStartingDeck": True,
            "includeStartingRelics": False,
            "includeAscendersBane": True,
            "characterCards": {"ids": [], "upgradeLevelsPerCard": []},
            "colorlessCards": {"ids": [], "upgradeLevelsPerCard": []},
        },
        "sourceInputValidation": None,
        "missingContext": ["test"],
        "candidates": [candidate(card_id, score) for card_id, score in zip(CANDIDATE_IDS, CANDIDATE_SCORES)],
        "skip": {"kind": "skip", "index": -1, "value": 0.0, "reason": "skip baseline"},
        "bestRewardAllowSkip": 0,
        "bestRewardNoSkip": 0,
        "argmaxIncludingSkip": 0,
        "argmaxValue": 12.5,
        "argmaxMatchesBestReward": True,
    }


def late_fixture():
    character_ids = ["DEFEND_SILENT", "DEFEND_SILENT", "NEUTRALIZE"]
    character_upgrades = [0, 1, 0]
    colorless_ids = ["FLASH_OF_STEEL"]
    colorless_upgrades = [0]
    deck = [
        deck_entry("DEFEND_SILENT", 0),
        deck_entry("DEFEND_SILENT", 1),
        deck_entry("NEUTRALIZE", 0),
        deck_entry("FLASH_OF_STEEL", 0),
        deck_entry("ASCENDERS_BANE", 0),
    ]
    return {
        "id": "silent-late-pm001-proxy",
        "label": "PROXY",
        "stateKind": "historical-community-late",
        "proxy": True,
        "proxyReason": "test proxy",
        "seed": "PM001",
        "characterId": "SILENT",
        "verifiedCharacterId": "SILENT",
        "ascension": 10,
        "verifiedAscension": 10,
        "actIndex": 2,
        "verifiedActIndex": 2,
        "encounterId": "SOUL_NEXUS_ELITE",
        "encounterSuitability": "test",
        "maxHp": 70,
        "currentHp": 70,
        "characterStartingHp": 70,
        "verifiedMaxHp": 70,
        "verifiedCurrentHp": 70,
        "relics": [],
        "potions": [],
        "relicsEmptyByDesign": True,
        "potionsEmptyByDesign": True,
        "ascendersBaneRequested": True,
        "ascendersBaneCount": 1,
        "startingDeck": "cleared",
        "resolvedStartingDeck": [],
        "deckSize": len(deck),
        "deck": deck,
        "context": {
            "actIndex": 2,
            "ascension": 10,
            "seed": "PM001",
            "encounterId": "SOUL_NEXUS_ELITE",
            "mapContext": "none",
            "sourceInput": None,
            "sourceInputSha256": None,
            "sourceInputValidation": {"matchesSpec": True, "matchesActualDeck": True},
        },
        "selection": {
            "includeStartingDeck": False,
            "includeStartingRelics": False,
            "includeAscendersBane": True,
            "characterCards": {"ids": character_ids, "upgradeLevelsPerCard": character_upgrades},
            "colorlessCards": {"ids": colorless_ids, "upgradeLevelsPerCard": colorless_upgrades},
        },
        "sourceInputValidation": {"matchesSpec": True, "matchesActualDeck": True},
        "missingContext": ["test"],
        "candidates": [candidate(card_id, score) for card_id, score in zip(CANDIDATE_IDS, CANDIDATE_SCORES)],
        "skip": {"kind": "skip", "index": -1, "value": 0.0, "reason": "skip baseline"},
        "bestRewardAllowSkip": 0,
        "bestRewardNoSkip": 0,
        "argmaxIncludingSkip": 0,
        "argmaxValue": 12.5,
        "argmaxMatchesBestReward": True,
    }


def valid_doc():
    return {
        "schemaVersion": 1,
        "tool": "BuildingDecisionProbe",
        "buildValue": {"type": "CoopBots.Building.BuildValue", "access": "reflection"},
        "fixtures": [starter_fixture(), late_fixture()],
    }


class BuildingPickCasesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def build(self, doc=None):
        doc = doc if doc is not None else valid_doc()
        prefix = uuid.uuid4().hex[:10]
        input_path = self.root / f"{prefix}.json"
        input_path.write_text(json.dumps(doc), encoding="utf-8")
        output_dir = self.root / f"cases-{prefix}"
        manifest_path = b.build_cases(input_path, output_dir)
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        return output_dir, manifest

    def test_skip_copies_base_exactly(self):
        output_dir, manifest = self.build()
        for fixture in manifest["fixtures"]:
            fixture_dir = output_dir / fixture["id"]
            base_bytes = (fixture_dir / "base.json").read_bytes()
            skip_bytes = (fixture_dir / "skip.json").read_bytes()
            self.assertEqual(base_bytes, skip_bytes)
            skip_action = next(action for action in fixture["actions"] if action["kind"] == "skip")
            base_action = next(
                action for action in fixture["actions"]
                if action["kind"] == "add"
            )
            self.assertTrue(skip_action["equalsBase"])
            self.assertNotEqual(skip_action["sha256"], base_action["sha256"])

    def test_add_appends_one_unupgraded_card_and_keeps_defends(self):
        output_dir, manifest = self.build()
        for fixture in manifest["fixtures"]:
            fixture_dir = output_dir / fixture["id"]
            base = json.loads((fixture_dir / "base.json").read_text(encoding="utf-8"))
            for action in fixture["actions"]:
                if action["kind"] != "add":
                    continue
                scenario = json.loads((fixture_dir / f"add-{action['candidate']}.json").read_text(encoding="utf-8"))
                for key in ("seed", "characterId", "ascension", "actIndex", "encounterId", "playerCurrentHp",
                            "includeStartingDeck", "includeStartingRelics", "includeAscendersBane"):
                    self.assertEqual(scenario[key], base[key], key)
                self.assertEqual(scenario["colorlessCards"], base["colorlessCards"])
                base_ids = base["characterCards"]["ids"]
                add_ids = scenario["characterCards"]["ids"]
                self.assertEqual(add_ids[:len(base_ids)], base_ids)
                self.assertEqual(add_ids[len(base_ids):], [action["candidate"]])
                self.assertEqual(
                    scenario["characterCards"]["upgradeLevelsPerCard"],
                    base["characterCards"]["upgradeLevelsPerCard"] + [0],
                )
                self.assertEqual(scenario["characterCards"]["count"], len(add_ids))
                self.assertEqual(
                    add_ids.count("DEFEND_SILENT"),
                    base_ids.count("DEFEND_SILENT"),
                )

    def test_actions_share_context_and_upgrades(self):
        output_dir, manifest = self.build()
        for fixture in manifest["fixtures"]:
            fixture_dir = output_dir / fixture["id"]
            base = json.loads((fixture_dir / "base.json").read_text(encoding="utf-8"))
            for candidate_id in CANDIDATE_IDS:
                scenario = json.loads((fixture_dir / f"add-{candidate_id}.json").read_text(encoding="utf-8"))
                for key in ("seed", "characterId", "ascension", "actIndex", "encounterId"):
                    self.assertEqual(scenario[key], base[key], key)
                self.assertEqual(
                    scenario["characterCards"]["upgradeLevelsPerCard"][:len(base["characterCards"]["upgradeLevelsPerCard"])],
                    base["characterCards"]["upgradeLevelsPerCard"],
                )
                self.assertEqual(scenario["colorlessCards"], base["colorlessCards"])
                self.assertEqual(scenario["relics"], base["relics"])
                self.assertEqual(scenario["potions"], base["potions"])

    def test_base_scenario_preserves_seventy_seventy(self):
        output_dir, manifest = self.build()
        for fixture in manifest["fixtures"]:
            base = json.loads((output_dir / fixture["id"] / "base.json").read_text(encoding="utf-8"))
            self.assertEqual(base["playerCurrentHp"], 70)
            self.assertEqual(manifest["scenarioHp"]["field"], "playerCurrentHp")
            self.assertEqual(manifest["scenarioHp"]["value"], 70)

    def test_manifest_records_production_scores_and_runtime_context(self):
        _, manifest = self.build()
        for fixture in manifest["fixtures"]:
            self.assertTrue(fixture["proxy"])
            self.assertEqual(fixture["label"], "PROXY")
            self.assertEqual(fixture["context"]["characterId"], "SILENT")
            self.assertEqual(fixture["context"]["verifiedAscension"], 10)
            self.assertEqual(fixture["context"]["ascension"], 10)
            self.assertEqual(fixture["context"]["verifiedActIndex"], fixture["context"]["actIndex"])
            self.assertEqual(fixture["context"]["verifiedMaxHp"], 70)
            self.assertEqual(fixture["context"]["verifiedCurrentHp"], 70)
            self.assertEqual(fixture["context"]["characterStartingHp"], 70)
            self.assertEqual(fixture["productionScores"]["skip"]["value"], 0.0)
            self.assertEqual(fixture["productionScores"]["argmaxIncludingSkip"], 0)
            self.assertEqual(fixture["productionScores"]["bestRewardAllowSkip"], 0)
            for scored in fixture["productionScores"]["candidates"]:
                self.assertTrue(math.isfinite(scored["score"]))

    def test_seed_sets_are_frozen_and_disjoint(self):
        _, manifest = self.build()
        seeds = manifest["seedSets"]
        self.assertEqual(seeds["selection"], ["PICKSEL01", "PICKSEL02"])
        self.assertEqual(seeds["evaluation"], ["PICKEVAL01", "PICKEVAL02"])
        self.assertTrue(seeds["disjoint"])
        self.assertFalse(set(seeds["selection"]) & set(seeds["evaluation"]))
        self.assertIn("not executed", seeds["status"])

    def test_verified_identity_must_be_silent(self):
        doc = valid_doc()
        doc["fixtures"][0]["verifiedCharacterId"] = "DEPRIVED"
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_non_proxy_label_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["label"] = "OBSERVED"
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_unknown_candidate_pool_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["candidates"][0]["cardPool"] = "colorless"
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_duplicate_candidate_ids_fail(self):
        doc = valid_doc()
        duplicate = copy.deepcopy(doc["fixtures"][0]["candidates"][0])
        doc["fixtures"][0]["candidates"].append(duplicate)
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_non_finite_score_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["candidates"][0]["score"] = float("nan")
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_unknown_canonical_id_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["candidates"][0]["canonicalId"] = "NOT_A_CARD"
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_array_length_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][1]["selection"]["characterCards"]["upgradeLevelsPerCard"] = [0]
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_ascenders_bane_already_listed_fails(self):
        doc = valid_doc()
        block = doc["fixtures"][1]["selection"]["characterCards"]
        block["ids"].append("ASCENDERS_BANE")
        block["upgradeLevelsPerCard"].append(0)
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_argmax_bestreward_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["bestRewardAllowSkip"] = 1
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_wrong_fixture_count_fails(self):
        doc = valid_doc()
        doc["fixtures"] = [doc["fixtures"][0]]
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_skip_value_must_be_exactly_zero(self):
        doc = valid_doc()
        doc["fixtures"][0]["skip"]["value"] = 3.5
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_negative_upgrade_fails(self):
        doc = valid_doc()
        doc["fixtures"][1]["selection"]["characterCards"]["upgradeLevelsPerCard"][0] = -1
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_non_integer_upgrade_fails(self):
        doc = valid_doc()
        doc["fixtures"][1]["deck"][0]["upgrade"] = 0.5
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_actual_deck_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][1]["deck"].pop()
        doc["fixtures"][1]["deckSize"] -= 1
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_starter_expansion_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["resolvedStartingDeck"].pop()
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_starter_exported_without_starting_deck_fails(self):
        doc = valid_doc()
        doc["fixtures"][1]["resolvedStartingDeck"] = [deck_entry("STRIKE_SILENT")]
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_path_traversal_candidate_id_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["candidates"][0]["id"] = "../evil"
        doc["fixtures"][0]["candidates"][0]["canonicalId"] = "../evil"
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_path_traversal_fixture_id_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["id"] = "../evil"
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_runtime_ascension_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["verifiedAscension"] = 0
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_runtime_act_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["verifiedActIndex"] = 1
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_runtime_hp_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["verifiedCurrentHp"] = 50
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_character_starting_hp_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][0]["characterStartingHp"] = 75
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_source_input_validation_mismatch_fails(self):
        doc = valid_doc()
        doc["fixtures"][1]["sourceInputValidation"] = {"matchesSpec": False, "matchesActualDeck": True}
        with self.assertRaises(ValueError):
            self.build(doc)

    def test_output_directory_is_never_overwritten(self):
        doc = valid_doc()
        input_path = self.root / "adapter.json"
        input_path.write_text(json.dumps(doc), encoding="utf-8")
        output_dir = self.root / "cases"
        b.build_cases(input_path, output_dir)
        with self.assertRaises(FileExistsError):
            b.build_cases(input_path, output_dir)

    def test_no_mutable_defaults_and_base_is_not_mutated(self):
        for func in (b.build_base_scenario, b.build_add_scenario, b.semantic_check, b.validate_adapter, b.build_cases):
            for parameter in inspect.signature(func).parameters.values():
                self.assertFalse(
                    isinstance(parameter.default, (list, dict, set)),
                    f"{func.__name__}.{parameter.name} has a mutable default",
                )

        fixture = late_fixture()
        base = b.build_base_scenario(fixture)
        snapshot = copy.deepcopy(base)
        scenario = b.build_add_scenario(base, fixture["candidates"][0])
        self.assertEqual(base, snapshot)
        self.assertIsNot(scenario["characterCards"], base["characterCards"])
        self.assertEqual(len(scenario["characterCards"]["ids"]), len(base["characterCards"]["ids"]) + 1)


if __name__ == "__main__":
    unittest.main()
