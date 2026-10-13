#!/usr/bin/env python3
"""P x M factorial: does a multiplier's marginal value depend on generator count?

The question is the *shape* of the value function, and shape is an interaction, so
it is settled by a factorial design rather than by argument.

`Archetypes.Bonus` is additive plus a conjunction gate: payoff >= threshold and
enabler >= threshold, then 8 x strength x decay summed linearly. The user's algebra
says a package is multiplicative — shivs are generators x Accuracy. The two rank
decks differently, and the oracle can now be handed an arbitrary deck, so the
disagreement is directly measurable.

    Additive:        y = a + b*P + c*M
    Multiplicative:  y = a + b*P + c*M + d*(P x M)

d is the whole experiment. Significant and positive => the interaction is real and
the shape has to be multiplicative. Not significant => on this package the additive
shape is enough, which is also a result worth having.

Two controls that matter more than the factor levels:

  * **Constant deck size.** deck_size tracks losing (AUC 0.39, winners smaller), so
    letting "add a generator" also enlarge the deck would fold that into the
    estimate. Every one of the 12 decks is 22 cards; a package card always
    displaces a Strike.
  * **No card is both a generator and a multiplier.** FAN_OF_KNIVES is in both
    BakedResources lists and would blur the two factors, so it is not used.

Usage:
    python scripts/oracle-factorial.py --budget-ms 1000
"""
import argparse
import itertools
import io
import json
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "work", "oracle", "factorial")
WRAPPER = os.path.join(REPO, "work", "oracle", "run-oracle.ps1")
PWSH = os.path.join(REPO, "work", "pwsh", "7", "pwsh.exe")
GAME_ROOT = r"B:\SteamLibrary\steamapps\common\Slay the Spire 2"
RITSU_ROOT = r"B:\SteamLibrary\steamapps\workshop\content\2868840\3747602295"
SOLVER_DIR = GAME_ROOT + r"\mods\CombatSolver"
BOSSES = ["TEST_SUBJECT_BOSS", "QUEEN_BOSS", "AEONGLASS_BOSS"]

# Factor levels, in the order they are added. FAN_OF_KNIVES is deliberately absent:
# it is listed as both a producer and a multiplier, so it would be two factors at once.
GENERATORS = ["BLADE_DANCE", "INFINITE_BLADES", "CLOAK_AND_DAGGER"]
MULTIPLIER = "ACCURACY"

# Silent's starting deck, exactly as the character declares it.
STARTING = (["STRIKE_SILENT"] * 5 + ["DEFEND_SILENT"] * 5 + ["NEUTRALIZE", "SURVIVOR"])
# Fixed block/draw base, identical in all 12 decks. None of these is a shiv
# producer or multiplier, so none of them moves either factor.
FILLER = ["BACKFLIP", "BACKFLIP", "DODGE_AND_ROLL", "DODGE_AND_ROLL", "ACROBATICS"]

SLOTS = 5          # must hold the largest package: P=3 plus M=2
TOTAL = len(STARTING) + len(FILLER) + SLOTS   # 22, identical in every cell


def build_deck(generators, multipliers):
    """One cell of the design: the 5 package slots, everything else fixed."""
    slots = GENERATORS[:generators] + [MULTIPLIER] * multipliers
    displaced = SLOTS - len(slots)
    if displaced < 0:
        raise SystemExit(f"P={generators} M={multipliers} exceeds the {SLOTS} slots")
    # The displaced cards are Strikes: one kind of basic, so the swap cannot also
    # shift the attack/defence balance in a way that tracks the factors.
    deck = list(STARTING) + list(FILLER) + slots + ["STRIKE_SILENT"] * displaced
    assert len(deck) == TOTAL, (len(deck), TOTAL)
    return deck


def spec_for(deck, boss, seed):
    from collections import Counter
    levels = [0] * len(deck)          # uniform: this experiment is about counts, not upgrades
    return {
        "schemaVersion": 1,
        "seed": seed,
        "characterId": "SILENT",
        "ascension": 10,
        "actIndex": 2,
        "encounterId": boss,
        "includeStartingDeck": False,
        "includeStartingRelics": False,
        "includeAscendersBane": True,
        "characterCards": {"count": len(deck), "ids": deck, "upgradeLevelsPerCard": levels},
        "colorlessCards": {"count": 0},
        "relics": {"count": 0},
        "potions": {"count": 0},
        "fixedSearchBudget": False,
        "mode": "Deploy",
    }


def run_case(spec, case_dir, scenario_id, timeout_seconds, budget_ms):
    os.makedirs(case_dir, exist_ok=True)
    input_path = os.path.join(case_dir, "input.json")
    with io.open(input_path, "w", encoding="utf-8") as handle:
        json.dump(spec, handle, indent=2)
    environment = dict(os.environ)
    environment["PATH"] = os.path.dirname(PWSH) + os.pathsep + environment.get("PATH", "")
    command = [PWSH, "-NoProfile", "-File", WRAPPER,
               "-GeneratedScenarioPath", input_path, "-EvidenceDirectory", case_dir,
               "-ScenarioId", scenario_id, "-HeadlessInstance", "oracle-factorial",
               "-TimeoutSeconds", str(timeout_seconds), "-ExitOnComplete",
               "-Sts2GameRoot", GAME_ROOT, "-RitsuWorkshopRoot", RITSU_ROOT,
               "-CombatSolverBuildDir", SOLVER_DIR,
               "-DeploymentFastModeForTest", "Normal",
               "-ExpectedUnexpectedReplansAtMost", "10",
               "-AcceptAnyCombatOutcome",
               "--search-budget-override-milliseconds", str(budget_ms),
               "--search-max-degree-of-parallelism-for-test", "1"]
    with io.open(os.path.join(case_dir, "launcher.log"), "w", encoding="utf-8") as log:
        subprocess.run(command, cwd=os.path.join(REPO, "CombatSolver-main"),
                       stdout=log, stderr=subprocess.STDOUT, env=environment)
    path = os.path.join(case_dir, "result.json")
    if not os.path.exists(path):
        return {"status": "timeout", "turns": None, "survived": None,
                "final_enemy_hp": None, "hp_lost": None, "expanded": None, "boundary": None,
                "error": "launcher timeout"}
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
    parser.add_argument("--seed", default="FACTORIAL")
    args = parser.parse_args()
    os.makedirs(OUT, exist_ok=True)

    cells = list(itertools.product(range(4), range(3)))
    print(f"{len(cells)} cells x {len(BOSSES)} bosses = {len(cells) * len(BOSSES)} runs", flush=True)

    manifest = []
    for generators, multipliers in cells:
        deck = build_deck(generators, multipliers)
        label = f"P{generators}M{multipliers}"
        manifest.append({"label": label, "P": generators, "M": multipliers,
                         "size": len(deck), "deck": deck})
    with io.open(os.path.join(OUT, "design.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=1)

    for entry in manifest:
        for boss in BOSSES:
            case = os.path.join(OUT, f"{entry['label']}-{boss}")
            spec = spec_for(entry["deck"], boss, f"{args.seed}-{entry['label']}-{boss}")
            outcome = run_case(spec, case, f"{entry['label']}-{boss}",
                               args.timeout_seconds, args.budget_ms)
            row = {"label": entry["label"], "P": entry["P"], "M": entry["M"],
                   "boss": boss, "seed": spec["seed"], "budget_ms": args.budget_ms,
                   "parallelism": 1, "size": entry["size"], **outcome}
            with io.open(os.path.join(OUT, "results.jsonl"), "a", encoding="utf-8") as handle:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
            print(f"  {entry['label']:5} {boss:18} {row['status']:8} surv={row['survived']} "
                  f"enemyHp={row['final_enemy_hp']} turns={row['turns']} exp={row['expanded']}", flush=True)
    print(f"done -> {OUT}", flush=True)


if __name__ == "__main__":
    main()
