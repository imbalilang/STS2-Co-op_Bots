#!/usr/bin/env python3
"""Run a handful of real decks through the CombatSolver oracle and collect outcomes.

Why a small batch: the leader's ruling is that human judgement comes before scale.
Eight decks across three bosses is enough for a person to say whether the ordering
is sensible, and small enough that they can actually read every card list.

The ruler holds relics at zero on purpose. That is the controlled variable: a deck
that clears with no relics is strong on its cards alone, and relics would only
make it easier. Losing is therefore a normal result, not an error — the launcher
is run with -AcceptAnyCombatOutcome so a wipe is recorded rather than thrown.

Duplicates need no special handling: listing the same id twice injects two copies.
(An earlier note in the hand-off said explicit ids had to be unique. That was a
misreading of a garbled error string — the real complaint was a card missing from
the pool, not a repeated one. Verified by loading a 28-card deck with 3xD defend.)

Usage:
    python scripts/oracle-small-batch.py --decks 8 --bosses 3
"""
import argparse
import io
import json
import os
import re
import subprocess
import sys
import zipfile
from collections import Counter

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNS = os.path.join(REPO, "work", "spire-codex", "decks", "runs")
CARDS_ZIP = os.path.join(REPO, "work", "spire-codex", "export-en.zip")
OUT = os.path.join(REPO, "work", "oracle", "batch")
WRAPPER = os.path.join(REPO, "work", "oracle", "run-oracle.ps1")
PWSH_DIR = os.path.join(REPO, "work", "pwsh", "7")
# Absolute path, not a bare name: CreateProcess resolves the executable against the
# PARENT process environment, so putting the portable pwsh on the child PATH is not
# enough and the spawn fails with "system cannot find the file".
PWSH = os.path.join(PWSH_DIR, "pwsh.exe")
LAUNCHER = os.path.join(REPO, "CombatSolver-main", "tools", "run-unattended-test.ps1")
GAME_ROOT = r"B:\SteamLibrary\steamapps\common\Slay the Spire 2"
RITSU_ROOT = r"B:\SteamLibrary\steamapps\workshop\content\2868840\3747602295"
SOLVER_DIR = GAME_ROOT + r"\mods\CombatSolver"

# Three bosses, never merged: A10 draws a second act-3 boss at random, so pass/fail
# is `max over pairs (cost_i + cost_j) <= player HP`, which three numbers decide.
BOSSES = ["TEST_SUBJECT_BOSS", "QUEEN_BOSS", "AEONGLASS_BOSS"]
CHARACTER_COLOR = {"IRONCLAD": "ironclad", "SILENT": "silent", "DEFECT": "defect",
                   "NECROBINDER": "necrobinder", "REGENT": "regent"}


def load_cards():
    with zipfile.ZipFile(CARDS_ZIP) as archive:
        return {c["id"]: c for c in json.loads(archive.read("cards.json"))}


def expressible(deck_record, cards):
    """True when every card can be injected: character pool or colourless."""
    own = CHARACTER_COLOR[deck_record["character"]]
    for card in deck_record["deck"]:
        card_id = card["id"]
        if card_id == "ASCENDERS_BANE":
            continue
        color = (cards.get(card_id, {}).get("color") or "").lower()
        if color not in (own, "colorless"):
            return False
    return True


def build_spec(deck_record, boss, cards, seed):
    """One cached deck as a generated-combat scenario.

    Ascender's Bane is never listed: it is not in either card pool and is added by
    the includeAscendersBane flag instead. Cards are split by colour because the
    two fields draw from different pools — a colourless card in `characterCards`
    is rejected outright.
    """
    own = CHARACTER_COLOR[deck_record["character"]]
    character_ids, character_levels = [], []
    colorless_ids, colorless_levels = [], []
    for card in deck_record["deck"]:
        card_id = card["id"]
        if card_id == "ASCENDERS_BANE":
            continue
        level = 1 if card.get("upgrades") else 0
        color = (cards.get(card_id, {}).get("color") or "").lower()
        if color == own:
            character_ids.append(card_id); character_levels.append(level)
        elif color == "colorless":
            colorless_ids.append(card_id); colorless_levels.append(level)
        else:
            raise SystemExit(f"{card_id} is neither {own} nor colorless ({color})")
    return {
        "schemaVersion": 1,
        "seed": seed,
        "characterId": deck_record["character"],
        "ascension": 10,
        "actIndex": 2,
        "encounterId": boss,
        "includeStartingDeck": False,
        "includeStartingRelics": False,
        "includeAscendersBane": True,
        # Per-card levels, not a scalar: the real deck's upgrade fraction is what
        # makes one deck better than another, and flattening it to all-upgraded
        # would both feed the solver a deck nobody played and delete the strongest
        # card-quality signal from the experiment.
        "characterCards": {"count": len(character_ids), "ids": character_ids,
                           "upgradeLevelsPerCard": character_levels},
        "colorlessCards": ({"count": len(colorless_ids), "ids": colorless_ids,
                            "upgradeLevelsPerCard": colorless_levels}
                           if colorless_ids else {"count": 0}),
        "relics": {"count": 0},
        "potions": {"count": 0},
        "fixedSearchBudget": False,
        "mode": "Deploy",
    }


def select_decks(wanted_per_bucket, cards):
    """Winners and deep losses, spread over characters, picked deterministically.

    Sorted by file name rather than taken in file order so the sample is the same
    on every machine and every re-run.

    A deck is only usable if every card is in the character's pool or the colourless
    one: real decks pick up other characters' cards from events, and the generator
    draws each field from a single pool. 169 of 212 candidates fail this, so the
    filter matters — without it the batch dies on the first cross-class card.
    """
    buckets = {}
    for name in sorted(os.listdir(RUNS)):
        if not name.endswith(".json"):
            continue
        with io.open(os.path.join(RUNS, name), encoding="utf-8") as handle:
            record = json.load(handle)
        if record.get("players") != 1 or record.get("ascension") != 10:
            continue
        if not expressible(record, cards):
            continue
        record["_file"] = name
        buckets.setdefault((record["character"], record["win"]), []).append(record)

    chosen = []
    for character in ("IRONCLAD", "SILENT", "DEFECT"):
        for win in (True, False):
            pool = buckets.get((character, win), [])
            chosen.extend(pool[:wanted_per_bucket])
    return chosen


def run_case(spec, case_dir, scenario_id, timeout_seconds=900, budget_ms=1000):
    os.makedirs(case_dir, exist_ok=True)
    input_path = os.path.join(case_dir, "input.json")
    with io.open(input_path, "w", encoding="utf-8") as handle:
        json.dump(spec, handle, indent=2)

    environment = dict(os.environ)
    environment["PATH"] = PWSH_DIR + os.pathsep + environment.get("PATH", "")
    command = [PWSH, "-NoProfile", "-File", WRAPPER,
               "-GeneratedScenarioPath", input_path,
               "-EvidenceDirectory", case_dir,
               "-ScenarioId", scenario_id,
               "-HeadlessInstance", "oracle-batch",
               "-TimeoutSeconds", str(timeout_seconds),
               "-ExitOnComplete",
               "-Sts2GameRoot", GAME_ROOT,
               "-RitsuWorkshopRoot", RITSU_ROOT,
               "-CombatSolverBuildDir", SOLVER_DIR,
               # Optics, pinned and reported: a real search (not Deploy's Instant
               # default), a fixed wall-clock budget, and explicit parallelism so
               # the reading does not depend on this machine's core count.
               "-DeploymentFastModeForTest", "Normal",
               "-ExpectedUnexpectedReplansAtMost", "10",
               "-AcceptAnyCombatOutcome",
               # The budget that is recorded must be the budget that is used.
               # An earlier version hardcoded 2000 here while writing --budget-ms
               # (default 1000) into the results, so every row named a budget the
               # run never had. Both now come from the one argument.
               "--search-budget-override-milliseconds", str(budget_ms),
               "--search-max-degree-of-parallelism-for-test", "1"]
    with io.open(os.path.join(case_dir, "launcher.log"), "w", encoding="utf-8") as log:
        subprocess.run(command, cwd=os.path.join(REPO, "CombatSolver-main"),
                       stdout=log, stderr=subprocess.STDOUT, env=environment)

    result_path = os.path.join(case_dir, "result.json")
    if not os.path.exists(result_path):
        # The launcher stops the game and writes nothing when it hits its own wall
        # clock. That is a result about the budget, not a crash, so it is recorded
        # as such rather than aborting the batch.
        return {"status": "timeout", "turns": None, "survived": None, "hp_lost": None,
                "final_enemy_hp": None, "expanded": None, "boundary": None, "error": "launcher timeout"}
    with io.open(result_path, encoding="utf-8-sig") as handle:
        result = json.load(handle)
    outcome = ((result.get("generatedScenario") or {}).get("actualOutcome")) or {}
    return {
        "status": result.get("status") or "unknown",
        "turns": result.get("finishedTurn"),
        "survived": outcome.get("survived"),
        "hp_lost": outcome.get("hpLost"),
        "final_enemy_hp": outcome.get("finalEnemyHp"),
        "expanded": (result.get("solverMetrics") or {}).get("selectedExpanded"),
        "boundary": (result.get("solverMetrics") or {}).get("boundary"),
        "error": (result.get("error") or "")[:160],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--per-bucket", type=int, default=1,
                        help="decks per (character, outcome); 3 characters x 2 outcomes")
    parser.add_argument("--seed", default="ORACLE-SMALL")
    parser.add_argument("--budget-ms", type=int, default=1000,
                        help="wall-clock search budget per decision; the launcher's own "
                             "timeout has to be large enough to fit every turn of the fight")
    # The launcher kills the game process when this expires, and writes only
    # {reason, runId, status} — no outcome at all. That is not "the solver could
    # not decide": each decision is already capped by --budget-ms, so a fight's
    # total length is bounded (turns x decisions x budget). The cap only has to be
    # generous enough that the fight always reaches its end; a fight that dies
    # should be recorded as a defeat, not thrown away. At 1000ms a run costs
    # ~60-120s, so this leaves 7-15x headroom.
    parser.add_argument("--timeout-seconds", type=int, default=900)
    args = parser.parse_args()

    cards = load_cards()
    decks = select_decks(args.per_bucket, cards)
    if not decks:
        raise SystemExit("no decks selected")
    os.makedirs(OUT, exist_ok=True)
    print(f"{len(decks)} decks x {len(BOSSES)} bosses = {len(decks) * len(BOSSES)} runs", flush=True)

    rows = []
    for index, deck in enumerate(decks):
        label = f"D{index + 1}"
        for boss in BOSSES:
            case = os.path.join(OUT, f"{label}-{boss}")
            spec = build_spec(deck, boss, cards, f"{args.seed}-{label}-{boss}")
            outcome = run_case(spec, case, f"{label}-{boss}", args.timeout_seconds, args.budget_ms)
            row = {"label": label, "character": deck["character"], "win": deck["win"],
                   "run_hash": deck["run_hash"], "boss": boss, "seed": spec["seed"],
                   "budget_ms": args.budget_ms, "parallelism": 1,
                   "timeout_s": args.timeout_seconds, **outcome}
            rows.append(row)
            print(f"  {label} {deck['character']:11} {'W' if deck['win'] else 'L'} {boss:18} "
                  f"{row['status']:8} turns={row['turns']} surv={row['survived']} "
                  f"enemyHp={row.get('final_enemy_hp')} expanded={row.get('expanded')}", flush=True)
            with io.open(os.path.join(OUT, "results.jsonl"), "a", encoding="utf-8") as handle:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")

    # The deck compositions travel with the results: the report a human reads needs
    # the cards next to the numbers, and rebuilding them later from a hash is work
    # nobody should have to repeat.
    with io.open(os.path.join(OUT, "decks.json"), "w", encoding="utf-8") as handle:
        json.dump([{k: v for k, v in deck.items() if k != "_file"} for deck in decks], handle,
                  ensure_ascii=False, indent=1)
    print(f"done -> {OUT}", flush=True)


if __name__ == "__main__":
    main()
