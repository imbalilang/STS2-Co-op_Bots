#!/usr/bin/env python3
"""Offline tests for scripts/building_history_audit.py.

Every test builds a small synthetic run and manifest in a temporary directory;
no real game file, network or battle is touched. The tests pin the audit's
conservative behaviour:

* a tampered raw SHA256, a wrong version, character, party size, ascension or win
  is rejected before any history is processed;
* a missing key is kept distinct from a present-but-empty list;
* multiple-picked and unpicked-only choice records are flagged as ambiguous,
  not silently grouped;
* gain + remove/transform/upgrade on one floor and duplicate card identities are
  surfaced as order/identity ambiguity;
* a record with no card choices is handled without inventing one;
* a perfectly valid record is still *not* reconstructable: no usable state, no
  complete snapshot, no candidate group;
* the manifest must name exactly six unique runs.

Run with:
    python -m unittest discover -s tests -p test_building_history_audit.py
"""

import json
import sys
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import building_history_audit as b  # noqa: E402

REAL_MANIFEST = b.MANIFEST_PATH


def stat(**overrides):
    value = {
        "player_id": 1,
        "current_hp": 50,
        "max_hp": 70,
        "current_gold": 100,
        "damage_taken": 0,
    }
    value.update(overrides)
    return value


def node(player_stat, map_point_type="monster"):
    return {
        "map_point_type": map_point_type,
        "player_stats": [player_stat],
        "rooms": [{"room_type": "monster", "model_id": "ENCOUNTER.X", "turns_taken": 1}],
    }


def base_run(history):
    return {
        "build_id": b.EXPECTED_BUILD,
        "ascension": 10,
        "win": True,
        "game_mode": "standard",
        "was_abandoned": False,
        "modifiers": [],
        "players": [{"character": b.EXPECTED_CHARACTER, "deck": [], "relics": [], "potions": []}],
        "map_point_history": [history],
    }


def choice(card_id, picked):
    return {"card": {"id": card_id}, "was_picked": picked}


class AuditCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def synth_audit(self, runs, entries=None, manifest_overrides=None):
        """runs: [(hash, run_data)]; entries: per-run manifest field overrides."""
        manifest_runs = []
        for index, (run_hash, data) in enumerate(runs):
            path = self.tmp / "runs" / f"{run_hash}.json"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(data), encoding="utf-8")
            entry = {
                "run_hash": run_hash,
                "build_id": b.EXPECTED_BUILD,
                "character": "SILENT",
                "party_size": 1,
                "ascension": 10,
                "win": True,
                "source": f"https://example.invalid/{run_hash}",
                "path": str(path),
                "sha256": b.sha256_file(path),
            }
            if entries and index < len(entries) and entries[index]:
                entry.update(entries[index])
            manifest_runs.append(entry)
        manifest = {"validated_runs": len(manifest_runs), "wins": 0, "losses": 0,
                    "runs": manifest_runs}
        if manifest_overrides:
            manifest.update(manifest_overrides)
        manifest_path = self.tmp / "source-validation.json"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        return b.audit_manifest(manifest_path, base_dir=self.tmp, generated_at="T",
                                enforce_frozen=False)

    def one(self, data, entry=None):
        return self.synth_audit([("run0001", data)], entries=[entry])["runs"][0]


class VerificationTests(AuditCase):
    def test_manifest_and_payload_cannot_change_fixed_target_together(self):
        for key, value in (("build_id", "v0.999.0"), ("ascension", 0)):
            with self.subTest(key=key):
                data = base_run([])
                data[key] = value
                self.assertFalse(self.one(data, {key: value})["verified"])

    def test_nonstandard_abandoned_modified_and_integer_win_rejected(self):
        for key, value in (("game_mode", "daily"), ("was_abandoned", True),
                           ("modifiers", ["custom"]), ("win", 1)):
            with self.subTest(key=key):
                data = base_run([])
                data[key] = value
                self.assertFalse(self.one(data)["verified"])

    def test_changed_manifest_stops_before_any_run_read(self):
        self.synth_audit([("run0001", base_run([]))])
        with patch.object(b, "audit_run", side_effect=AssertionError("must not read")):
            result = b.audit_manifest(self.tmp / "source-validation.json", self.tmp)
        self.assertEqual(result["runs"], [])
        self.assertFalse(result["conclusion"]["all_six_verified"])

    def test_snapshot_counts_are_observed_not_constants(self):
        data = base_run([node(stat(card_choices=[choice("CARD.A", True)],
                                   deck=[], relics=[], potions=None))])
        result = self.one(data)
        self.assertEqual(result["snapshots"]["direct_deck_snapshot_at_choice_floors"], 1)
        self.assertEqual(result["snapshots"]["direct_relic_snapshot_at_choice_floors"], 1)
        self.assertEqual(result["snapshots"]["direct_potion_snapshot_at_choice_floors"], 0)
        self.assertEqual(b.field_state({"potions": None}, "potions")["entries"], None)
        self.assertIsNone(result["reconstruction"]["reconstructable"])
        self.assertEqual(result["reconstruction"]["usable_pre_reward_states"], 0)

    def test_cli_returns_failure_for_failed_provenance(self):
        self.synth_audit([("run0001", base_run([]))])
        failed = b.audit_manifest(self.tmp / "source-validation.json", self.tmp)
        with patch.object(b, "audit_manifest", return_value=failed), \
             patch.object(b, "write_audit"), patch("builtins.print"):
            self.assertEqual(b.main(), 1)

    def test_tampered_sha_is_rejected_before_processing(self):
        result = self.one(base_run([]), entry={"sha256": "0" * 64})
        self.assertFalse(result["verified"])
        self.assertFalse(result["processed"])
        self.assertNotIn("scan", result)
        self.assertTrue(any("raw sha256" in r for r in result["rejection_reasons"]))

    def test_wrong_version_is_rejected(self):
        data = base_run([])
        data["build_id"] = "v0.999.0"
        result = self.one(data)
        self.assertFalse(result["verified"])
        self.assertTrue(any("build_id" in r for r in result["rejection_reasons"]))

    def test_wrong_character_is_rejected(self):
        data = base_run([])
        data["players"][0]["character"] = "CHARACTER.IRONCLAD"
        result = self.one(data)
        self.assertFalse(result["verified"])
        self.assertTrue(any("character" in r for r in result["rejection_reasons"]))

    def test_wrong_party_size_is_rejected(self):
        data = base_run([])
        data["players"].append({"character": "CHARACTER.SILENT", "deck": [], "relics": [],
                                "potions": []})
        result = self.one(data)
        self.assertFalse(result["verified"])
        self.assertTrue(any("party" in r for r in result["rejection_reasons"]))

    def test_wrong_ascension_is_rejected(self):
        data = base_run([])
        data["ascension"] = 0
        result = self.one(data)
        self.assertFalse(result["verified"])
        self.assertTrue(any("ascension" in r for r in result["rejection_reasons"]))

    def test_wrong_win_is_rejected(self):
        data = base_run([])
        data["win"] = False
        result = self.one(data)
        self.assertFalse(result["verified"])
        self.assertTrue(any("win" in r for r in result["rejection_reasons"]))

    def test_missing_source_file_is_rejected(self):
        result = self.one(base_run([]), entry={"path": str(self.tmp / "nope.json")})
        self.assertFalse(result["verified"])
        self.assertTrue(any("missing" in r for r in result["rejection_reasons"]))


class MissingVersusEmptyTests(AuditCase):
    def test_field_state_distinguishes_missing_empty_and_full(self):
        self.assertEqual(b.field_state({}, "card_choices"),
                         {"present": False, "empty": False, "entries": None})
        self.assertEqual(b.field_state({"card_choices": []}, "card_choices"),
                         {"present": True, "empty": True, "entries": 0})
        self.assertEqual(b.field_state({"card_choices": [1, 2]}, "card_choices"),
                         {"present": True, "empty": False, "entries": 2})

    def test_scan_counts_missing_and_empty_separately(self):
        history = [
            node(stat()),  # card_choices key absent
            node(stat(card_choices=[])),  # present but empty
            node(stat(card_choices=[choice("CARD.A", True), choice("CARD.B", False)])),
        ]
        result = self.one(base_run(history))
        counts = result["scan"]["counts"]
        self.assertEqual(counts["records_missing_card_choices"], 1)
        self.assertEqual(counts["records_present_empty_card_choices"], 1)
        self.assertEqual(counts["records_with_nonempty_card_choices"], 1)
        self.assertEqual(counts["card_choice_entries"], 2)
        self.assertEqual(
            result["scan"]["mutation_counts"]["cards_gained"]["present"], 0)
        self.assertEqual(
            result["scan"]["mutation_counts"]["cards_gained"]["entries"], 0)


class AmbiguityTests(AuditCase):
    def test_multiple_picked_and_unpicked_only_are_ambiguous(self):
        history = [
            node(stat(card_choices=[choice("CARD.A", True), choice("CARD.B", True),
                                    choice("CARD.C", False)])),
            node(stat(card_choices=[choice("CARD.D", False), choice("CARD.E", False)])),
        ]
        result = self.one(base_run(history))
        counts = result["scan"]["counts"]
        self.assertEqual(counts["records_picked_gt1"], 1)
        self.assertEqual(counts["records_unpicked_only"], 1)
        reasons = " ".join(result["reconstruction"]["reasons"])
        self.assertIn("more than one picked", reasons)
        self.assertIn("only unpicked", reasons)
        self.assertFalse(result["reconstruction"]["eligible_for_reconstruction"])

    def test_same_floor_mutations_and_order_not_provable(self):
        history = [
            node(stat(
                cards_gained=[{"id": "CARD.A"}],
                cards_removed=[{"id": "CARD.B"}],
                cards_transformed=[{"original_card": {"id": "CARD.C"},
                                    "final_card": {"id": "CARD.D"}}],
                upgraded_cards=["CARD.A"],
                cards_enchanted=[{"card": {"id": "CARD.E"},
                                  "enchantment": "ENCHANTMENT.X"}],
            )),
        ]
        result = self.one(base_run(history))
        counts = result["scan"]["counts"]
        self.assertEqual(counts["floors_with_same_floor_mutation_overlap"], 1)
        self.assertEqual(result["scan"]["mutation_counts"]["cards_transformed"]["entries"], 1)
        self.assertEqual(result["scan"]["mutation_counts"]["upgraded_cards"]["entries"], 1)
        self.assertIn("not encoded", " ".join(result["reconstruction"]["reasons"]))

    def test_duplicate_card_identity_is_flagged(self):
        history = [
            node(stat(cards_gained=[{"id": "CARD.A"}, {"id": "CARD.A"}])),
        ]
        result = self.one(base_run(history))
        self.assertEqual(result["scan"]["counts"]["floors_with_duplicate_card_identity"], 1)

    def test_no_rewards_is_not_invented(self):
        history = [node(stat()), node(stat())]
        result = self.one(base_run(history))
        counts = result["scan"]["counts"]
        self.assertEqual(counts["records_with_nonempty_card_choices"], 0)
        self.assertEqual(counts["card_choice_entries"], 0)
        self.assertEqual(counts["picked_entries"], 0)
        self.assertTrue(result["snapshots"]["terminal_deck_present"])
        self.assertEqual(result["snapshots"]["terminal_deck_size"], 0)


class ReconstructionTests(AuditCase):
    def test_valid_format_is_not_reconstructable(self):
        history = [
            node(stat(
                card_choices=[choice("CARD.A", True), choice("CARD.B", False)],
                cards_gained=[{"id": "CARD.A"}],
            )),
        ]
        result = self.one(base_run(history))
        self.assertTrue(result["verified"])
        self.assertTrue(result["processed"])
        self.assertFalse(result["reconstruction"]["reconstructable"])
        self.assertEqual(result["reconstruction"]["usable_pre_reward_states"], 0)
        self.assertEqual(result["reconstruction"]["complete_snapshots"], 0)
        self.assertEqual(result["snapshots"]["direct_deck_snapshot_at_choice_floors"], 0)

    def test_terminal_deck_duplicates_are_reported(self):
        run = base_run([node(stat())])
        run["players"][0]["deck"] = [{"id": "CARD.A"}, {"id": "CARD.A"}, {"id": "CARD.B"}]
        result = self.one(run)
        self.assertTrue(result["snapshots"]["terminal_deck_present"])
        self.assertEqual(result["snapshots"]["terminal_deck_size"], 3)
        self.assertEqual(result["snapshots"]["terminal_deck_duplicate_ids"], {"CARD.A": 2})


class ManifestTests(AuditCase):
    def test_manifest_must_name_exactly_six_runs(self):
        runs = [(f"run{i:02d}", base_run([])) for i in range(5)]
        audit = self.synth_audit(runs)
        self.assertTrue(audit["manifest"]["errors"])
        self.assertTrue(any("exactly 6" in e for e in audit["manifest"]["errors"]))

    def test_manifest_duplicate_hashes_are_rejected(self):
        runs = [("dup", base_run([])), ("dup", base_run([]))]
        audit = self.synth_audit(runs)
        self.assertTrue(any("unique" in e for e in audit["manifest"]["errors"]))

    def test_aggregate_keeps_rejected_runs_out_of_totals(self):
        good = base_run([node(stat(card_choices=[choice("CARD.A", True)]))])
        bad = base_run([node(stat(card_choices=[choice("CARD.B", True)]))])
        audit = self.synth_audit(
            [("good0001", good), ("bad00001", bad)],
            entries=[None, {"sha256": "0" * 64}],
        )
        counts = audit["aggregate"]["counts"]
        self.assertEqual(counts["verified_runs"], 1)
        self.assertEqual(counts["rejected_runs"], 1)
        self.assertEqual(counts["card_choice_entries"], 1)

    def test_write_audit_writes_only_named_file(self):
        audit = self.synth_audit([("run0001", base_run([]))])
        output = self.tmp / "out" / "reconstruction-audit.json"
        b.write_audit(audit, output)
        self.assertTrue(output.exists())
        self.assertEqual([p.name for p in output.parent.iterdir()],
                         ["reconstruction-audit.json"])
        self.assertFalse(b.load_json(output)["conclusion"]["usable_for_labels_or_training"])


@unittest.skipUnless(REAL_MANIFEST.exists(), "six frozen runs are not checked out")
class RealManifestTests(unittest.TestCase):
    def test_real_six_runs_are_verified_and_not_reconstructable(self):
        audit = b.audit_manifest(REAL_MANIFEST, generated_at="T")
        counts = audit["aggregate"]["counts"]
        self.assertEqual(counts["runs"], 6)
        self.assertEqual(counts["verified_runs"], 6)
        self.assertEqual(counts["rejected_runs"], 0)
        self.assertEqual(counts["records_with_nonempty_card_choices"], 156)
        self.assertEqual(counts["card_choice_entries"], 564)
        self.assertEqual(audit["aggregate"]["usable_pre_reward_states"], 0)
        self.assertTrue(audit["conclusion"]["all_six_verified"])
        self.assertFalse(audit["conclusion"]["reconstructable_pre_reward_states"])
        self.assertFalse(audit["conclusion"]["production_changes"])


if __name__ == "__main__":
    unittest.main()
