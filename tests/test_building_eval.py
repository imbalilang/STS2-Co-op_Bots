#!/usr/bin/env python3
"""Offline fixture tests for scripts/building_eval.py.

Run with:
    python -m unittest discover -s tests -p test_building_eval.py
"""

import json
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import building_eval as be  # noqa: E402


def after_turns(turn=1, hp=70, max_hp=70, enemies=""):
    return (
        f"turn={turn} round={turn} phase=None side=Enemy energy=2 "
        f"hp={hp}/{max_hp} hand=[] draw=0 discard=0 relics=[] "
        f"enemies=[{enemies}] total_floor=1 act_floor=0"
    )


def base_row(seed="PM001", tag="tag-PM001", **overrides):
    row = {"seed": seed, "tag": tag, "exit": 0, "error": None, "won": False,
           "final_enemy_hp": None, "wall_s": 1.0}
    row.update(overrides)
    return row


def payload(after, **overrides):
    data = {"fightTruncated": None, "fightWallMs": 1234, "afterTurns": after}
    data.update(overrides)
    return data


class ClassifyTests(unittest.TestCase):
    def test_win_when_enemies_empty(self):
        result = be.classify(base_row(), payload(after_turns(hp=16, enemies="")))
        self.assertEqual(result["status"], be.STATUS_WIN)
        self.assertEqual(result["final_enemy_hp"], 0)
        self.assertEqual(result["player_hp"], 16)

    def test_loss_player_dead_with_living_enemy(self):
        result = be.classify(
            base_row(), payload(after_turns(hp=0, enemies="SOUL_NEXUS#1:100/254@X"))
        )
        self.assertEqual(result["status"], be.STATUS_LOSS)
        self.assertEqual(result["final_enemy_hp"], 100)

    def test_zero_enemy_hp_victory(self):
        result = be.classify(
            base_row(), payload(after_turns(hp=70, enemies="SOUL_NEXUS#1:0/254@X"))
        )
        self.assertEqual(result["status"], be.STATUS_WIN)
        self.assertEqual(result["final_enemy_hp"], 0)

    def test_simultaneous_death_is_ambiguous(self):
        result = be.classify(
            base_row(), payload(after_turns(hp=0, enemies="SOUL_NEXUS#1:0/254@X"))
        )
        self.assertEqual(result["status"], be.STATUS_AMBIGUOUS)

    def test_alive_turn_cap_is_incomplete_not_loss(self):
        result = be.classify(
            base_row(), payload(after_turns(turn=40, hp=16, enemies="SOUL_NEXUS#1:201/254@X"))
        )
        self.assertEqual(result["status"], be.STATUS_INCOMPLETE)
        self.assertEqual(result["turns"], 40)
        self.assertEqual(result["final_enemy_hp"], 201)

    def test_nonzero_exit_is_error(self):
        result = be.classify(base_row(exit=1), payload(after_turns(hp=70, enemies="")))
        self.assertEqual(result["status"], be.STATUS_ERROR)

    def test_payload_error_is_error(self):
        result = be.classify(
            base_row(), payload(after_turns(hp=70, enemies=""), error={"message": "boom"})
        )
        self.assertEqual(result["status"], be.STATUS_ERROR)

    def test_explicit_timeout_precedes_nonzero_exit(self):
        result = be.classify(base_row(exit=1, error="process timeout"), payload(None))
        self.assertEqual(result["status"], be.STATUS_TIMEOUT)

    def test_missing_payload_is_missing(self):
        result = be.classify(base_row(), None, load_error="absent")
        self.assertEqual(result["status"], be.STATUS_MISSING)

    def test_truncated_is_truncated(self):
        result = be.classify(
            base_row(),
            payload(after_turns(hp=70, enemies="SOUL_NEXUS#1:100/254@X"), fightTruncated=True),
        )
        self.assertEqual(result["status"], be.STATUS_TRUNCATED)

    def test_malformed_state_is_invalid(self):
        result = be.classify(base_row(), payload("not a describe-root line"))
        self.assertEqual(result["status"], be.STATUS_INVALID)

    def test_missing_enemies_field_is_missing(self):
        text = "turn=3 round=3 phase=None side=Enemy energy=2 hp=10/70 hand=[] draw=0"
        result = be.classify(base_row(), payload(text))
        self.assertEqual(result["status"], be.STATUS_MISSING)

    def test_missing_player_hp_is_invalid(self):
        text = "turn=3 round=3 phase=None side=Enemy enemies=[] total_floor=1"
        result = be.classify(base_row(), payload(text))
        self.assertEqual(result["status"], be.STATUS_INVALID)

    def test_invalid_player_hp_is_invalid(self):
        result = be.classify(base_row(), payload(after_turns(hp=99, max_hp=70, enemies="")))
        self.assertEqual(result["status"], be.STATUS_INVALID)

    def test_enemy_hp_comes_only_from_bracket(self):
        text = (
            "turn=3 round=3 phase=None side=Enemy energy=2 hp=70/70 hand=[] draw=0 "
            "discard=0 relics=[] extra=[:999/999] "
            "enemies=[SOUL_NEXUS#1:10/254@X,SOUL_NEXUS#2:20/254@Y] total_floor=1"
        )
        result = be.classify(base_row(), payload(text))
        self.assertEqual(result["status"], be.STATUS_INCOMPLETE)
        self.assertEqual(result["final_enemy_hp"], 30)

    def test_unparsed_enemy_entry_is_invalid(self):
        result = be.classify(
            base_row(), payload(after_turns(hp=70, enemies="SOUL_NEXUS#1:abc/254@X"))
        )
        self.assertEqual(result["status"], be.STATUS_INVALID)

    def test_negative_enemy_hp_is_invalid(self):
        result = be.classify(
            base_row(), payload(after_turns(hp=70, enemies="SOUL_NEXUS#1:-5/254@X"))
        )
        self.assertEqual(result["status"], be.STATUS_INVALID)

    def test_fight_wall_ms_preserved(self):
        result = be.classify(base_row(), payload(after_turns(hp=70, enemies=""), fightWallMs=987))
        self.assertEqual(result["fight_wall_ms"], 987)


class IndexingTests(unittest.TestCase):
    def test_duplicate_seed_is_rejected(self):
        rows = [base_row(seed="PM001", tag="a"), base_row(seed="PM001", tag="b")]
        with self.assertRaises(be.DuplicateSeedError):
            be.index_cell(rows)

    def test_validate_tag_accepts_simple_name(self):
        self.assertEqual(be.validate_tag("b4000-hp1.0-dmg1.0-P0M0-PM001"), "b4000-hp1.0-dmg1.0-P0M0-PM001")

    def test_validate_tag_rejects_path_like_values(self):
        for bad in ("a/b", "a\\b", "..", ".", "C:foo", "../escape"):
            with self.subTest(tag=bad):
                with self.assertRaises(ValueError):
                    be.validate_tag(bad)


class StatisticsTests(unittest.TestCase):
    def test_single_value_has_null_uncertainty(self):
        stats = be.describe([5.0])
        self.assertEqual(stats["n"], 1)
        self.assertIsNone(stats["se"])
        self.assertIsNone(stats["ci95_normal"])

    def test_normal_interval_uses_1_96_se(self):
        stats = be.describe([0.0, 2.0, 4.0, 6.0])
        self.assertAlmostEqual(stats["mean"], 3.0)
        self.assertAlmostEqual(stats["se"], stats["sd"] / 2.0)
        self.assertAlmostEqual(stats["ci95_normal"][0], 3.0 - 1.96 * stats["se"])
        self.assertAlmostEqual(stats["ci95_normal"][1], 3.0 + 1.96 * stats["se"])

    def test_paired_contrast_excludes_incomplete_seed(self):
        def rec(status, hp):
            return {"status": status, "final_enemy_hp": hp}

        seed_maps = {
            "P0M0": {"PM001": rec(be.STATUS_LOSS, 10), "PM002": rec(be.STATUS_LOSS, 20)},
            "P0M1": {"PM001": rec(be.STATUS_LOSS, 20), "PM002": rec(be.STATUS_INCOMPLETE, 5)},
            "P3M0": {"PM001": rec(be.STATUS_LOSS, 30), "PM002": rec(be.STATUS_LOSS, 40)},
            "P3M1": {"PM001": rec(be.STATUS_LOSS, 10), "PM002": rec(be.STATUS_LOSS, 50)},
        }
        paired = be.paired_contrast(seed_maps)
        self.assertEqual(paired["n"], 1)
        self.assertEqual(paired["n_common"], 2)
        self.assertEqual(paired["seeds"][0]["seed"], "PM001")
        self.assertEqual(paired["seeds"][0]["interaction"], -30)
        self.assertIn("PM002", paired["excluded"])

    def test_paired_contrast_missing_cell(self):
        paired = be.paired_contrast({"P0M0": {}, "P0M1": {}})
        self.assertEqual(paired["n"], 0)
        self.assertIn("reason", paired)

    def test_paired_contrast_lists_seeds_absent_from_any_cell(self):
        def rec(status, hp):
            return {"status": status, "final_enemy_hp": hp}

        seed_maps = {
            "P0M0": {"PM001": rec(be.STATUS_WIN, 0), "PM003": rec(be.STATUS_WIN, 0)},
            "P0M1": {"PM001": rec(be.STATUS_WIN, 0)},
            "P3M0": {"PM001": rec(be.STATUS_WIN, 0)},
            "P3M1": {"PM001": rec(be.STATUS_WIN, 0), "PM003": rec(be.STATUS_WIN, 0)},
        }
        paired = be.paired_contrast(seed_maps)
        self.assertEqual(paired["n"], 1)
        self.assertEqual(paired["n_common"], 1)
        self.assertEqual(paired["n_union"], 2)
        self.assertEqual(paired["absent"]["PM003"], ["P0M1", "P3M0"])
        self.assertEqual([row["seed"] for row in paired["seeds"]], ["PM001"])
        self.assertNotIn("PM003", paired["excluded"])


class AuditTests(unittest.TestCase):
    def _write_result(self, base, tag, data):
        tag_dir = base / tag
        tag_dir.mkdir(parents=True, exist_ok=True)
        (tag_dir / be.RESULT_NAME).write_text(json.dumps(data), encoding="utf-8")

    def _pm(self, rows_by_cell):
        cells = {}
        for cell, rows in rows_by_cell.items():
            cells[cell] = {"label": cell, "n": len(rows), "rows": rows}
        return {"base": "test", "budget_ms": 4000, "points": {"hp1.0_dmg1.0": {"cells": cells}}}

    def test_audit_end_to_end_counts_and_pairing(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            for cell, seeds in (
                ("P0M0", ("PM001", "PM002")),
                ("P0M1", ("PM001", "PM002")),
                ("P3M0", ("PM001", "PM002")),
                ("P3M1", ("PM001", "PM002")),
            ):
                for seed in seeds:
                    tag = f"{cell}-{seed}"
                    self._write_result(
                        base,
                        tag,
                        payload(after_turns(hp=70, enemies="SOUL_NEXUS#1:50/254@X")),
                    )

            # PM002 in P3M1 is left incomplete by reclassifying it through truncation.
            self._write_result(
                base,
                "P3M1-PM002",
                payload(after_turns(hp=70, enemies="SOUL_NEXUS#1:5/254@X"), fightTruncated=True),
            )

            pm = self._pm(
                {
                    "P0M0": [base_row(seed="PM001", tag="P0M0-PM001"), base_row(seed="PM002", tag="P0M0-PM002")],
                    "P0M1": [base_row(seed="PM001", tag="P0M1-PM001"), base_row(seed="PM002", tag="P0M1-PM002")],
                    "P3M0": [base_row(seed="PM001", tag="P3M0-PM001"), base_row(seed="PM002", tag="P3M0-PM002")],
                    "P3M1": [base_row(seed="PM001", tag="P3M1-PM001"), base_row(seed="PM002", tag="P3M1-PM002")],
                }
            )
            pm_path = base / "pm.json"
            pm_path.write_text(json.dumps(pm), encoding="utf-8")

            report = be.audit_pm(pm_path, base)
            self.assertEqual(report["totals"]["records"], 8)
            statuses = report["totals"]["status_counts"]
            self.assertEqual(statuses.get(be.STATUS_INCOMPLETE), 7)
            self.assertEqual(statuses.get(be.STATUS_TRUNCATED), 1)
            paired = report["points"]["hp1.0_dmg1.0"]["paired"]
            self.assertEqual(paired["n"], 0)
            self.assertEqual(paired["n_common"], 2)

    def test_audit_detects_old_inclusion_discrepancy(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            for cell in ("P0M0", "P0M1", "P3M0", "P3M1"):
                self._write_result(
                    base,
                    f"{cell}-PM001",
                    payload(after_turns(turn=40, hp=70, enemies="SOUL_NEXUS#1:100/254@X")),
                )
            old_row = base_row(seed="PM001", tag="P0M0-PM001", final_enemy_hp=100)
            pm = self._pm(
                {
                    "P0M0": [old_row],
                    "P0M1": [base_row(seed="PM001", tag="P0M1-PM001", final_enemy_hp=100)],
                    "P3M0": [base_row(seed="PM001", tag="P3M0-PM001", final_enemy_hp=100)],
                    "P3M1": [base_row(seed="PM001", tag="P3M1-PM001", final_enemy_hp=100)],
                }
            )
            pm_path = base / "pm.json"
            pm_path.write_text(json.dumps(pm), encoding="utf-8")

            report = be.audit_pm(pm_path, base)
            discrepancies = report["points"]["hp1.0_dmg1.0"]["discrepancies"]
            self.assertEqual(len(discrepancies["old_included_new_excluded"]), 4)
            self.assertEqual(discrepancies["old_excluded_new_included"], [])

    def test_write_audit_refuses_existing_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "audit.json"
            output.write_text("{}", encoding="utf-8")
            with self.assertRaises(FileExistsError):
                be.write_audit({"ok": True}, output)
            self.assertEqual(output.read_text(encoding="utf-8"), "{}")

    def test_audit_records_include_status_reason_metrics_and_input_hash(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            self._write_result(base, "P0M0-PM001", payload(after_turns(hp=16, enemies="")))
            pm = self._pm({"P0M0": [base_row(seed="PM001", tag="P0M0-PM001")]})
            pm_path = base / "pm.json"
            pm_path.write_text(json.dumps(pm), encoding="utf-8")

            report = be.audit_pm(pm_path, base)
            records = report["points"]["hp1.0_dmg1.0"]["cells"]["P0M0"]["records"]
            self.assertEqual(len(records), 1)
            record = records[0]
            self.assertEqual(record["status"], be.STATUS_WIN)
            self.assertTrue(record["reason"])
            self.assertEqual(record["final_enemy_hp"], 0)
            self.assertEqual(record["player_hp"], 16)
            self.assertEqual(record["max_hp"], 70)
            self.assertEqual(record["turns"], 1)
            raw_path = base / "P0M0-PM001" / be.RESULT_NAME
            self.assertEqual(record["input_sha256"], be.sha256_file(raw_path))

    def test_audit_missing_record_has_no_input_hash(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            pm = self._pm({"P0M0": [base_row(seed="PM001", tag="P0M0-PM001")]})
            pm_path = base / "pm.json"
            pm_path.write_text(json.dumps(pm), encoding="utf-8")

            report = be.audit_pm(pm_path, base)
            record = report["points"]["hp1.0_dmg1.0"]["cells"]["P0M0"]["records"][0]
            self.assertEqual(record["status"], be.STATUS_MISSING)
            self.assertIsNone(record["input_sha256"])

    def test_missing_result_file_classified_as_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            pm = self._pm({"P0M0": [base_row(seed="PM001", tag="P0M0-PM001")]})
            pm_path = base / "pm.json"
            pm_path.write_text(json.dumps(pm), encoding="utf-8")
            report = be.audit_pm(pm_path, base)
            self.assertEqual(report["totals"]["status_counts"].get(be.STATUS_MISSING), 1)


if __name__ == "__main__":
    unittest.main()
