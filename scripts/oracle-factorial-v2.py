#!/usr/bin/env python3
"""P x M factorial, v2: a real winning deck as the base, and seed replication.

v1 answered nothing, and the reason was dynamic range rather than the factors: every
one of its 12 decks died in 3-4 turns, so finalEnemyHp moved inside 4-13% of the
boss's health while the residual was the same size. A package cannot show its shape
on a deck that never gets to play it.

Two changes, both from the leader's ruling:

  A. **The base is a real winning Silent deck** (`9f0f93ff7106a296`, 1-player A10),
     with exactly five cards swapped. Five plain Defends come out and the five
     package slots go in — so the P=0,M=0 cell is the original deck *verbatim*, and
     every other cell differs from it only in those five slots. The base is a
     draw/defence deck: no shiv cards at all, and its only package cards are two
     poison ones, so it cannot cancel or double-count what the factors move.

  C. **Three seeds per cell.** With n=1 the design had 8 degrees of freedom and a
     residual as large as the effect; an interaction of realistic size could not
     have reached significance even if it were there. Cells were kept at 12 and
     seeds added, because what this experiment lacks is power, not grid resolution.

FAN_OF_KNIVES is excluded on purpose: BakedResources lists it as both a shiv producer
and a shiv multiplier, so a cell containing it would move two factors at once. Its
absence here is a control, not an oversight.

Usage:
    python scripts/oracle-factorial-v2.py --budget-ms 1000
"""
import argparse
import io
import itertools
import json
import os
import subprocess
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNS = os.path.join(REPO, "work", "spire-codex", "decks", "runs")
OUT = os.path.join(REPO, "work", "oracle", "factorial-v2")
WRAPPER = os.path.join(REPO, "work", "oracle", "run-oracle.ps1")
PWSH = os.path.join(REPO, "work", "pwsh", "7", "pwsh.exe")
GAME_ROOT = r"B:\SteamLibrary\steamapps\common\Slay the Spire 2"
RITSU_ROOT = r"B:\SteamLibrary\steamapps\workshop\content\2868840\3747602295"
SOLVER_DIR = GAME_ROOT + r"\mods\CombatSolver"
BOSSES = ["TEST_SUBJECT_BOSS", "QUEEN_BOSS", "AEONGLASS_BOSS"]

BASE_RUN = "9f0f93ff7106a296"
GENERATORS = ["BLADE_DANCE", "INFINITE_BLADES", "CLOAK_AND_DAGGER"]
MULTIPLIER = "ACCURACY"
SWAPPED = "DEFEND_SILENT"          # the five cards that come out
SLOTS = 5
SEEDS = ["FACT2-S1", "FACT2-S2", "FACT2-S3"]


def load_colours():
    """Card colour per id, from the codex dump.

    The two card fields draw from different pools: a colourless card listed under
    characterCards fails with "matched 0" exactly like a card that does not exist.
    This base deck holds two of them (FLASH_OF_STEEL, DRAMATIC_ENTRANCE), so the
    split is required rather than defensive.
    """
    path = os.path.join(REPO, "work", "spire-codex", "export-en.zip")
    with zipfile.ZipFile(path) as archive:
        return {c["id"]: (c.get("color") or "").lower()
                for c in json.loads(archive.read("cards.json"))}


def load_base():
    """The base deck, keeping its own upgrade levels.

    Those levels are not decoration: ten of the 32 cards are upgraded, and flattening
    them was exactly the v0 mistake that made a different deck than the player's.
    """
    for name in sorted(os.listdir(RUNS)):
        if not name.endswith(".json"):
            continue
        with io.open(os.path.join(RUNS, name), encoding="utf-8") as handle:
            deck = json.load(handle)
        if deck.get("run_hash") == BASE_RUN:
            return deck
    raise SystemExit(f"base deck {BASE_RUN} is not in the cache")


def build_deck(base, generators, multipliers):
    """Swap SLOTS cards of the base for the package plus filler, at constant size."""
    # Ascender's Bane never goes in a card list: it is not in either card pool and is
    # added by the includeAscendersBane flag instead. Listing it fails the whole run
    # with "matched 0", which is how the first attempt at this batch died on every
    # single cell.
    pairs = [(card["id"], card.get("upgrades", 0) or 0) for card in base["deck"]
             if card["id"] != "ASCENDERS_BANE"]
    removed = [pair for pair in pairs if pair[0] == SWAPPED]
    if len(removed) != SLOTS:
        raise SystemExit(f"base has {len(removed)} {SWAPPED}, expected {SLOTS}")
    kept = [pair for pair in pairs if pair[0] != SWAPPED]
    # Unused slots are refilled with the very card that came out, at its own level, so
    # P=0,M=0 reconstructs the base deck exactly rather than an approximation of it.
    filler_level = removed[0][1]
    slots = ([(g, 0) for g in GENERATORS[:generators]]
             + [(MULTIPLIER, 0)] * multipliers
             + [(SWAPPED, filler_level)] * (SLOTS - generators - multipliers))
    if len(slots) != SLOTS:
        raise SystemExit(f"P={generators} M={multipliers} does not fit {SLOTS} slots")
    deck = kept + slots
    assert len(deck) == len(pairs), (len(deck), len(pairs))
    return deck


def spec_for(deck, boss, seed, colours):
    silent = [(c, u) for c, u in deck if colours.get(c) == "silent"]
    colourless = [(c, u) for c, u in deck if colours.get(c) == "colorless"]
    unknown = [c for c, _ in deck if colours.get(c) not in ("silent", "colorless")]
    if unknown:
        raise SystemExit(f"cards outside the silent/colourless pools: {unknown}")
    return {
        "schemaVersion": 1, "seed": seed, "characterId": "SILENT", "ascension": 10,
        "actIndex": 2, "encounterId": boss,
        "includeStartingDeck": False, "includeStartingRelics": False,
        "includeAscendersBane": True,
        "characterCards": {"count": len(silent), "ids": [c for c, _ in silent],
                           "upgradeLevelsPerCard": [u for _, u in silent]},
        "colorlessCards": ({"count": len(colourless), "ids": [c for c, _ in colourless],
                            "upgradeLevelsPerCard": [u for _, u in colourless]}
                           if colourless else {"count": 0}),
        "relics": {"count": 0}, "potions": {"count": 0},
        "fixedSearchBudget": False, "mode": "Deploy",
    }


def run_case(spec, case_dir, scenario_id, timeout_seconds, budget_ms,
             instance="oracle-factorial", fast_mode="Normal"):
    os.makedirs(case_dir, exist_ok=True)
    input_path = os.path.join(case_dir, "input.json")
    with io.open(input_path, "w", encoding="utf-8") as handle:
        json.dump(spec, handle, indent=2)
    environment = dict(os.environ)
    environment["PATH"] = os.path.dirname(PWSH) + os.pathsep + environment.get("PATH", "")
    command = [PWSH, "-NoProfile", "-File", WRAPPER,
               "-GeneratedScenarioPath", input_path, "-EvidenceDirectory", case_dir,
               "-ScenarioId", scenario_id, "-HeadlessInstance", instance,
               "-TimeoutSeconds", str(timeout_seconds), "-ExitOnComplete",
               "-Sts2GameRoot", GAME_ROOT, "-RitsuWorkshopRoot", RITSU_ROOT,
               "-CombatSolverBuildDir", SOLVER_DIR,
               "-DeploymentFastModeForTest", fast_mode,
               "-ExpectedUnexpectedReplansAtMost", "10",
               "-AcceptAnyCombatOutcome",
               "--search-budget-override-milliseconds", str(budget_ms),
               "--search-max-degree-of-parallelism-for-test", "1"]
    with io.open(os.path.join(case_dir, "launcher.log"), "w", encoding="utf-8") as log:
        subprocess.run(command, cwd=os.path.join(REPO, "CombatSolver-main"),
                       stdout=log, stderr=subprocess.STDOUT, env=environment)
    path = os.path.join(case_dir, "result.json")
    if not os.path.exists(path):
        return {"status": "timeout", "turns": None, "survived": None, "final_enemy_hp": None,
                "hp_lost": None, "expanded": None, "boundary": None, "error": "launcher timeout"}
    with io.open(path, encoding="utf-8-sig") as handle:
        result = json.load(handle)
    outcome = ((result.get("generatedScenario") or {}).get("actualOutcome")) or {}
    return {"status": result.get("status") or "unknown",
            "turns": result.get("finishedTurn"),
            "survived": outcome.get("survived"),
            "final_enemy_hp": outcome.get("finalEnemyHp"),
            "hp_lost": outcome.get("hpLost"),
            "expanded": (result.get("solverMetrics") or {}).get("selectedExpanded"),
            "boundary": (result.get("solverMetrics") or {}).get("boundary"),
            "error": (result.get("error") or "")[:160]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--budget-ms", type=int, default=1000)
    parser.add_argument("--timeout-seconds", type=int, default=900)
    parser.add_argument("--p-levels", default="0,1,2,3",
                        help="generator counts; drop a level here rather than a seed if time is short")
    args = parser.parse_args()
    os.makedirs(OUT, exist_ok=True)
    base = load_base()
    colours = load_colours()
    p_levels = [int(v) for v in args.p_levels.split(",")]

    manifest = []
    for generators, multipliers in itertools.product(p_levels, range(3)):
        deck = build_deck(base, generators, multipliers)
        manifest.append({"label": f"P{generators}M{multipliers}", "P": generators, "M": multipliers,
                         "size": len(deck), "deck": [c for c, _ in deck],
                         "upgrades": [u for _, u in deck]})
    with io.open(os.path.join(OUT, "design.json"), "w", encoding="utf-8") as handle:
        json.dump({"base": base, "cells": manifest}, handle, ensure_ascii=False, indent=1)

    total = len(manifest) * len(BOSSES) * len(SEEDS)
    print(f"base={BASE_RUN} size={len(base['deck'])} | {len(manifest)} cells x "
          f"{len(BOSSES)} bosses x {len(SEEDS)} seeds = {total} runs", flush=True)

    # Resume: rows already in results.jsonl are skipped. A batch that has to be
    # paused for a machine-level experiment should not pay for its finished cells
    # twice, and every row is written the moment it completes precisely so this is
    # possible.
    finished = set()
    results_path = os.path.join(OUT, "results.jsonl")
    if os.path.exists(results_path):
        with io.open(results_path, encoding="utf-8") as handle:
            for line in handle:
                row = json.loads(line)
                finished.add((row["label"], row["seed"], row["boss"]))
    print(f"resuming: {len(finished)} cells already recorded", flush=True)

    done = 1
    for entry in manifest:
        for seed in SEEDS:
            for boss in BOSSES:
                if (entry["label"], seed, boss) in finished:
                    done += 1
                    continue
                case = os.path.join(OUT, entry["label"], seed, boss)
                spec = spec_for(list(zip(entry["deck"], entry["upgrades"])), boss,
                                f"{seed}-{entry['label']}-{boss}", colours)
                outcome = run_case(spec, case, f"{entry['label']}-{seed}-{boss}",
                                   args.timeout_seconds, args.budget_ms)
                row = {"label": entry["label"], "P": entry["P"], "M": entry["M"],
                       "seed": seed, "boss": boss, "size": entry["size"],
                       "budget_ms": args.budget_ms, "parallelism": 1, **outcome}
                with io.open(os.path.join(OUT, "results.jsonl"), "a", encoding="utf-8") as handle:
                    handle.write(json.dumps(row, ensure_ascii=False) + "\n")
                print(f"  {done}/{total} {entry['label']:5} {seed} {boss:18} {row['status']:8} "
                      f"surv={row['survived']} enemyHp={row['final_enemy_hp']} turns={row['turns']}",
                      flush=True)
    print(f"done -> {OUT}", flush=True)


if __name__ == "__main__":
    main()
