"""Regression for stale game results after a launcher failure."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("a5_batch", Path(__file__).parents[1] / "scripts/a5-batch.py")
batch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(batch)

LOG = (
    "CoopBots live test: multiplayer run started — seed=A5BASE201 ascension=5 seats=4 decks=[10,10,10,10]\n"
    "CoopBots live test: run outcome — victory=false act=0 floor=16\n"
    "How did the game end??\n"
)


class BatchValidityTests(unittest.TestCase):
    # R2: disabling validate_round must fail these with "RuntimeError not raised".
    def test_launcher_failure_is_not_a_loss(self):
        with self.assertRaisesRegex(RuntimeError, "launcher failed"):
            batch.validate_round(1, "A5BASE201", "old", LOG)

    def test_unchanged_log_is_not_a_new_game(self):
        with self.assertRaisesRegex(RuntimeError, "did not change"):
            batch.validate_round(0, "A5BASE201", LOG, LOG)

    def test_wrong_seed_is_not_a_paired_run(self):
        with self.assertRaisesRegex(RuntimeError, "seed mismatch"):
            batch.validate_round(0, "A5BASE202", "old", LOG)

    def test_new_completed_run_keeps_its_outcome(self):
        self.assertEqual(batch.validate_round(0, "A5BASE201", "old", LOG),
                         (False, "0", "16", "A5BASE201"))


if __name__ == "__main__":
    unittest.main()
