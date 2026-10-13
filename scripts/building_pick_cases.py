#!/usr/bin/env python3
"""Build CoopBots pick-candidate scenario JSON from the BuildingDecisionProbe output.

This is an offline fixture builder, not an experiment. It consumes the adapter
JSON produced by ``tools/BuildingDecisionProbe`` (real current production
BuildValue scores for two proxy SILENT states) and writes, into a NEW output
directory, one harness scenario JSON per action per proxy fixture:

    cases/<fixture-id>/base.json
    cases/<fixture-id>/skip.json
    cases/<fixture-id>/add-<CANDIDATE>.json

plus ``manifest.json`` holding per-action SHA256, the frozen (not executed)
selection/evaluation seed sets, the production scores and the semantic checks
that prove only the intended card addition differs between actions.

It never launches a battle, never reads the game, and refuses to overwrite an
existing output directory.

Usage:
    python scripts/building_pick_cases.py --input <adapter-output.json> --output <new-dir>
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import os
import re
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

TOOL = "BuildingDecisionProbe"
SCHEMA_VERSION = 1
CHARACTER = "SILENT"
ASCENSION = 10
HP = 70
PLAYER_CURRENT_HP = 70
REQUIRED_CANDIDATE_POOL = "character"
ASCENDERS_BANE = "ASCENDERS_BANE"
DEFEND_SILENT = "DEFEND_SILENT"

# Candidate ids become file names, so they must be restricted to a safe
# character set; anything else could escape the output directory.
CARD_ID = re.compile(r"^[A-Z0-9_]+$")
# Fixture ids become directory names, so no separators and no ".." segments.
FIXTURE_ID = re.compile(r"^[A-Za-z0-9_-]+$")

# Predeclared before any run. Two seeds per set cannot establish an unbiased
# estimator; they only freeze which seeds belong to which side of a future
# selection/evaluation split.
SEED_SETS = {
    "selection": ["PICKSEL01", "PICKSEL02"],
    "evaluation": ["PICKEVAL01", "PICKEVAL02"],
    "disjoint": True,
    "status": "predeclared; not executed",
    "note": "Two seeds per set cannot establish an unbiased estimator.",
}

LIMITATIONS = [
    "Both states are PROXY fixtures, not observed reward screens.",
    "Relics and potions are empty by design (an ablation); this changes real-run meaning.",
    "No experiment is run here; the seed sets are frozen but never executed.",
    "The act0 starter encounter is a fixture proxy; an actual resolved-scenario check is required before costly sampling.",
    "Scenario JSON is written for later 4s/dop1 and 20s/dop1 comparison; this tool does not decide that budget is superior.",
]


def sha256_bytes(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def rel(path: Path) -> str:
    try:
        return Path(path).resolve().relative_to(ROOT.resolve()).as_posix()
    except ValueError:
        return Path(path).name


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def write_json_exclusive(path: Path, data) -> str:
    """Write JSON with O_EXCL; never clobber an existing file. Returns its SHA256."""
    payload = json.dumps(data, ensure_ascii=False, indent=1).encode("utf-8")
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(path), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
    except BaseException:
        try:
            os.unlink(path)
        except OSError:
            pass
        raise
    return sha256_bytes(payload)


def write_bytes_exclusive(path: Path, payload: bytes) -> str:
    """Write raw bytes with O_EXCL so skip can copy base byte-for-byte."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(path), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
    except BaseException:
        try:
            os.unlink(path)
        except OSError:
            pass
        raise
    return sha256_bytes(payload)


def _finite_number(value, label: str) -> float:
    require(isinstance(value, (int, float)) and not isinstance(value, bool), f"{label} must be a number")
    number = float(value)
    require(math.isfinite(number), f"{label} must be finite, got {value!r}")
    return number


def _upgrade(value, label: str) -> int:
    require(isinstance(value, int) and not isinstance(value, bool), f"{label} must be an integer")
    require(value >= 0, f"{label} must be non-negative, got {value!r}")
    return value


def _card_id(value, label: str) -> str:
    require(isinstance(value, str) and value, f"{label} must be a non-empty string")
    require(CARD_ID.fullmatch(value), f"{label} {value!r} contains characters outside [A-Z0-9_]; refusing it")
    return value


def _fixture_id(value, label: str) -> str:
    require(isinstance(value, str) and value, f"{label} must be a non-empty string")
    require(FIXTURE_ID.fullmatch(value), f"{label} {value!r} is not a safe directory name; refusing it")
    return value


def _string_list(value, label: str) -> list:
    require(isinstance(value, list), f"{label} must be a list")
    require(all(isinstance(item, str) and item for item in value), f"{label} must hold non-empty strings")
    return list(value)


def _int_list(value, label: str) -> list:
    require(isinstance(value, list), f"{label} must be a list")
    require(all(isinstance(item, int) and not isinstance(item, bool) and item >= 0 for item in value),
            f"{label} must hold non-negative integers")
    return list(value)


def _card_entries(ids, upgrades, label: str) -> list:
    entries = []
    for card_id, upgrade in zip(ids, upgrades):
        entries.append((_card_id(card_id, f"{label} id"), _upgrade(upgrade, f"{label} upgrade")))
    return entries


def _block(selection, key: str, label: str) -> tuple:
    block = selection.get(key)
    require(isinstance(block, dict), f"{label}.{key} must be an object")
    ids = _string_list(block.get("ids"), f"{label}.{key}.ids")
    upgrades = _int_list(block.get("upgradeLevelsPerCard"), f"{label}.{key}.upgradeLevelsPerCard")
    require(len(ids) == len(upgrades), f"{label}.{key} ids and upgradeLevelsPerCard differ in length")
    return ids, upgrades


def validate_adapter(doc) -> list:
    """Validate the adapter output and return its fixtures, or raise ValueError."""
    require(isinstance(doc, dict), "adapter output must be a JSON object")
    require(doc.get("tool") == TOOL, f"adapter tool must be {TOOL}, got {doc.get('tool')!r}")
    require(doc.get("schemaVersion") == SCHEMA_VERSION, "adapter schemaVersion must be 1")
    build_value = doc.get("buildValue")
    require(isinstance(build_value, dict) and build_value.get("access") == "reflection",
            "adapter must report BuildValue access by reflection, not a reimplementation")

    fixtures = doc.get("fixtures")
    require(isinstance(fixtures, list) and len(fixtures) == 2, "adapter must carry exactly two fixtures")

    seen_ids = set()
    for fixture in fixtures:
        require(isinstance(fixture, dict), "fixture must be an object")
        fixture_id = _fixture_id(fixture.get("id"), "fixture id")
        require(fixture_id not in seen_ids, f"duplicate fixture id {fixture_id!r}")
        seen_ids.add(fixture_id)

        require(fixture.get("proxy") is True and fixture.get("label") == "PROXY",
                f"fixture {fixture_id} must be an explicit PROXY fixture")
        require(fixture.get("characterId") == CHARACTER and fixture.get("verifiedCharacterId") == CHARACTER,
                f"fixture {fixture_id} must be verified {CHARACTER}")
        require(fixture.get("ascension") == ASCENSION, f"fixture {fixture_id} must be ascension {ASCENSION}")
        require(fixture.get("verifiedAscension") == ASCENSION,
                f"fixture {fixture_id} must report actual runtime ascension {ASCENSION}")
        require(fixture.get("actIndex") == fixture.get("verifiedActIndex"),
                f"fixture {fixture_id} serialized actIndex differs from the actual runtime act")
        require(fixture.get("maxHp") == HP and fixture.get("currentHp") == HP,
                f"fixture {fixture_id} must be {HP}/{HP} HP")
        require(fixture.get("characterStartingHp") == HP,
                f"fixture {fixture_id} must match the character starting HP ({HP}); the scenario HP depends on it")
        require(fixture.get("verifiedMaxHp") == HP and fixture.get("verifiedCurrentHp") == HP,
                f"fixture {fixture_id} must report actual runtime {HP}/{HP} HP")
        require(fixture.get("relics") == [] and fixture.get("potions") == [],
                f"fixture {fixture_id} must have empty relics and potions (an explicit ablation)")
        require(fixture.get("relicsEmptyByDesign") is True and fixture.get("potionsEmptyByDesign") is True,
                f"fixture {fixture_id} must mark relics/potions empty by design")
        require(fixture.get("ascendersBaneRequested") is True and fixture.get("ascendersBaneCount") == 1,
                f"fixture {fixture_id} must include exactly one Ascender's Bane")

        deck = fixture.get("deck")
        require(isinstance(deck, list) and deck, f"fixture {fixture_id} deck must be a non-empty list")
        require(fixture.get("deckSize") == len(deck), f"fixture {fixture_id} deckSize does not match deck length")
        for entry in deck:
            require(isinstance(entry, dict), f"fixture {fixture_id} deck entries must be objects")
            require(isinstance(entry.get("id"), str), f"fixture {fixture_id} deck entries must have an id")
            _upgrade(entry.get("upgrade"), f"fixture {fixture_id} deck upgrade")
        require(sum(1 for entry in deck if entry["id"] == ASCENDERS_BANE) == 1,
                f"fixture {fixture_id} deck must hold exactly one {ASCENDERS_BANE}")

        starter = fixture.get("resolvedStartingDeck")
        require(isinstance(starter, list), f"fixture {fixture_id} must export its resolved starting deck")
        for entry in starter:
            require(isinstance(entry, dict), f"fixture {fixture_id} starter entries must be objects")
            require(isinstance(entry.get("id"), str), f"fixture {fixture_id} starter entries must have an id")
            _upgrade(entry.get("upgrade"), f"fixture {fixture_id} starter upgrade")

        context = fixture.get("context")
        require(isinstance(context, dict), f"fixture {fixture_id} context must be an object")
        require(context.get("actIndex") == fixture.get("actIndex"),
                f"fixture {fixture_id} context actIndex does not match")
        require(context.get("ascension") == fixture.get("ascension"),
                f"fixture {fixture_id} context ascension does not match")
        require(context.get("seed") == fixture.get("seed"), f"fixture {fixture_id} context seed does not match")
        require(context.get("encounterId") == fixture.get("encounterId"),
                f"fixture {fixture_id} context encounter does not match")

        selection = fixture.get("selection")
        require(isinstance(selection, dict), f"fixture {fixture_id} selection must be an object")
        require(selection.get("includeStartingRelics") is False,
                f"fixture {fixture_id} selection must not include starting relics")
        require(selection.get("includeAscendersBane") is True,
                f"fixture {fixture_id} selection must carry the Ascender's Bane flag")
        require(isinstance(selection.get("includeStartingDeck"), bool),
                f"fixture {fixture_id} selection must state includeStartingDeck")
        char_ids, char_upgrades = _block(selection, "characterCards", fixture_id)
        color_ids, color_upgrades = _block(selection, "colorlessCards", fixture_id)
        require(ASCENDERS_BANE not in char_ids,
                f"fixture {fixture_id} must not list {ASCENDERS_BANE} in characterCards; the flag inserts it exactly once")

        _validate_deck_matches_selection(
            fixture_id, deck, starter, char_ids, char_upgrades, color_ids, color_upgrades,
            selection["includeStartingDeck"])

        candidates = fixture.get("candidates")
        require(isinstance(candidates, list) and candidates, f"fixture {fixture_id} must offer candidates")
        candidate_ids = set()
        for candidate in candidates:
            require(isinstance(candidate, dict), f"fixture {fixture_id} candidate must be an object")
            candidate_id = _card_id(candidate.get("id"), f"fixture {fixture_id} candidate id")
            require(candidate_id not in candidate_ids, f"fixture {fixture_id} duplicate candidate id {candidate_id}")
            candidate_ids.add(candidate_id)
            require(candidate.get("canonicalId") == candidate_id,
                    f"fixture {fixture_id} candidate {candidate_id} canonicalId mismatch")
            require(candidate.get("cardPool") == REQUIRED_CANDIDATE_POOL,
                    f"fixture {fixture_id} candidate {candidate_id} is not a {REQUIRED_CANDIDATE_POOL} card")
            require(candidate.get("finite") is True,
                    f"fixture {fixture_id} candidate {candidate_id} must report a finite score")
            _finite_number(candidate.get("score"), f"fixture {fixture_id} candidate {candidate_id} score")
            require(isinstance(candidate.get("reason"), str) and candidate["reason"],
                    f"fixture {fixture_id} candidate {candidate_id} needs a reason")
            require(_upgrade(candidate.get("upgrade"), f"fixture {fixture_id} candidate {candidate_id} upgrade") == 0,
                    f"fixture {fixture_id} candidate {candidate_id} must be offered unupgraded")

        skip = fixture.get("skip")
        require(isinstance(skip, dict) and skip.get("kind") == "skip",
                f"fixture {fixture_id} must record the skip action")
        require(isinstance(skip.get("value"), (int, float)) and not isinstance(skip.get("value"), bool)
                and float(skip["value"]) == 0.0,
                f"fixture {fixture_id} skip value must be exactly 0, got {skip.get('value')!r}")

        scores = [float(candidate["score"]) for candidate in candidates]
        argmax = -1
        best = 0.0
        for index, score in enumerate(scores):
            if score > best:
                best = score
                argmax = index
        require(fixture.get("argmaxIncludingSkip") == argmax,
                f"fixture {fixture_id} argmaxIncludingSkip is not the recomputed argmax")
        require(fixture.get("bestRewardAllowSkip") == argmax,
                f"fixture {fixture_id} bestRewardAllowSkip disagrees with argmax including skip")
        require(fixture.get("argmaxMatchesBestReward") is True,
                f"fixture {fixture_id} must record argmax/bestReward consistency")
        require(isinstance(fixture.get("bestRewardNoSkip"), int),
                f"fixture {fixture_id} bestRewardNoSkip must be an int")

        source_validation = fixture.get("sourceInputValidation")
        require(source_validation is None or isinstance(source_validation, dict),
                f"fixture {fixture_id} sourceInputValidation must be null or an object")
        if isinstance(source_validation, dict):
            require(source_validation.get("matchesSpec") is True
                    and source_validation.get("matchesActualDeck") is True,
                    f"fixture {fixture_id} historical source input did not match the spec and actual deck")
    return fixtures


def _validate_deck_matches_selection(fixture_id, deck, starter, char_ids, char_upgrades,
                                     color_ids, color_upgrades, include_starting_deck) -> None:
    """Reject an exported actual deck that is not exactly the declared selections expanded.

    The adapter exports both the actual deck and the starting-deck expansion it
    applied, so the builder can prove the scenario selections reproduce the deck
    the production scorer actually saw. This is the check that a starter
    expansion cannot silently drift.
    """
    if include_starting_deck:
        require(starter, f"fixture {fixture_id} includes the starting deck but exported none")
    else:
        require(starter == [], f"fixture {fixture_id} does not include the starting deck but exported starter cards")

    expected = Counter()
    for entry in starter:
        expected[(entry["id"], _upgrade(entry["upgrade"], f"fixture {fixture_id} starter upgrade"))] += 1
    for card_id, upgrade in zip(char_ids, char_upgrades):
        expected[(_card_id(card_id, f"fixture {fixture_id} character card"), _upgrade(upgrade, "upgrade"))] += 1
    for card_id, upgrade in zip(color_ids, color_upgrades):
        expected[(_card_id(card_id, f"fixture {fixture_id} colorless card"), _upgrade(upgrade, "upgrade"))] += 1
    expected[(ASCENDERS_BANE, 0)] += 1

    actual = Counter()
    for entry in deck:
        actual[(entry["id"], _upgrade(entry["upgrade"], f"fixture {fixture_id} deck upgrade"))] += 1

    require(actual == expected,
            f"fixture {fixture_id} exported deck does not equal the starting deck + selections + one "
            f"{ASCENDERS_BANE}: expected {sorted(expected.items())}, got {sorted(actual.items())}")


def _scenario_block(ids, upgrades) -> dict:
    return {"count": len(ids), "ids": list(ids), "upgradeLevelsPerCard": list(upgrades)}


def build_base_scenario(fixture: dict) -> dict:
    """The frozen base context shared by every action of one fixture."""
    selection = fixture["selection"]
    character_ids, character_upgrades = _block(selection, "characterCards", fixture["id"])
    colorless_ids, colorless_upgrades = _block(selection, "colorlessCards", fixture["id"])
    return {
        "schemaVersion": 1,
        "seed": fixture["seed"],
        "characterId": fixture["characterId"],
        "ascension": fixture["ascension"],
        "actIndex": fixture["actIndex"],
        "encounterId": fixture["encounterId"],
        # The harness takes max HP from the character (SILENT = 70) and uses full
        # HP unless playerCurrentHp says otherwise, so writing 70 keeps 70/70.
        "playerCurrentHp": PLAYER_CURRENT_HP,
        "includeStartingDeck": bool(selection["includeStartingDeck"]),
        "includeStartingRelics": False,
        "includeAscendersBane": True,
        "characterCards": _scenario_block(character_ids, character_upgrades),
        "colorlessCards": _scenario_block(colorless_ids, colorless_upgrades),
        "relics": {"count": 0},
        "potions": {"count": 0},
        "fixedSearchBudget": False,
        "mode": "Deploy",
    }


def build_add_scenario(base: dict, candidate: dict) -> dict:
    """Append exactly one unupgraded card; never remove or upgrade an existing one."""
    card_pool = candidate["cardPool"]
    require(card_pool in ("character", "colorless"),
            f"candidate {candidate['id']} has unsupported cardPool {card_pool!r}")
    block_key = "characterCards" if card_pool == "character" else "colorlessCards"
    scenario = copy.deepcopy(base)
    block = scenario[block_key]
    block["ids"] = list(base[block_key]["ids"]) + [candidate["id"]]
    block["upgradeLevelsPerCard"] = list(base[block_key]["upgradeLevelsPerCard"]) + [0]
    block["count"] = len(block["ids"])
    return scenario


def semantic_check(base: dict, scenario: dict, candidate: dict, block_key: str) -> dict:
    """Prove the only difference from base is one appended card in one block."""
    for key in ("seed", "characterId", "ascension", "actIndex", "encounterId", "playerCurrentHp",
                "includeStartingDeck", "includeStartingRelics", "includeAscendersBane"):
        require(scenario[key] == base[key], f"add action changed context field {key}")
    other_key = "colorlessCards" if block_key == "characterCards" else "characterCards"
    require(scenario[other_key] == base[other_key], f"add action changed the {other_key} block")
    require(scenario["relics"] == base["relics"] and scenario["potions"] == base["potions"],
            "add action changed relics or potions")

    base_ids = base[block_key]["ids"]
    base_upgrades = base[block_key]["upgradeLevelsPerCard"]
    scenario_ids = scenario[block_key]["ids"]
    scenario_upgrades = scenario[block_key]["upgradeLevelsPerCard"]
    require(scenario_ids[:len(base_ids)] == base_ids, "add action did not preserve the base card order")
    require(scenario_ids[len(base_ids):] == [candidate["id"]], "add action added more than the candidate")
    require(scenario_upgrades[:len(base_upgrades)] == base_upgrades,
            "add action changed an existing card's upgrade level")
    require(scenario_upgrades[len(base_upgrades):] == [0], "added card must be unupgraded")
    require(scenario[block_key]["count"] == len(scenario_ids), "add action count is inconsistent")
    require(scenario_ids.count(DEFEND_SILENT) == base_ids.count(DEFEND_SILENT),
            "add action changed the number of DEFEND cards")
    return {
        "differsOnlyByCandidate": True,
        "block": block_key,
        "countBefore": len(base_ids),
        "countAfter": len(scenario_ids),
        "defendBefore": base_ids.count(DEFEND_SILENT),
        "defendAfter": scenario_ids.count(DEFEND_SILENT),
    }


def build_cases(adapter_path: Path, output_dir: Path) -> Path:
    """Validate the adapter output and write the per-action scenarios + manifest."""
    adapter_path = Path(adapter_path)
    output_dir = Path(output_dir)
    try:
        doc = json.loads(adapter_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"adapter output is unreadable: {exc}") from exc

    fixtures = validate_adapter(doc)

    selection_seeds = list(SEED_SETS["selection"])
    evaluation_seeds = list(SEED_SETS["evaluation"])
    require(not set(selection_seeds) & set(evaluation_seeds), "selection and evaluation seed sets must be disjoint")

    output_dir.mkdir(parents=True, exist_ok=False)

    fixture_reports = []
    for fixture in fixtures:
        base = build_base_scenario(fixture)
        base_bytes = json.dumps(base, ensure_ascii=False, indent=1).encode("utf-8")
        fixture_dir = output_dir / fixture["id"]
        base_hash = write_bytes_exclusive(fixture_dir / "base.json", base_bytes)
        skip_hash = write_bytes_exclusive(fixture_dir / "skip.json", base_bytes)
        require(base_hash == skip_hash, f"fixture {fixture['id']} skip must copy base exactly")

        actions = [{
            "kind": "skip",
            "file": rel(fixture_dir / "skip.json"),
            "sha256": skip_hash,
            "equalsBase": True,
        }]
        for candidate in fixture["candidates"]:
            scenario = build_add_scenario(base, candidate)
            block_key = "characterCards" if candidate["cardPool"] == "character" else "colorlessCards"
            check = semantic_check(base, scenario, candidate, block_key)
            path = fixture_dir / f"add-{candidate['id']}.json"
            digest = write_json_exclusive(path, scenario)
            actions.append({
                "kind": "add",
                "candidate": candidate["id"],
                "file": rel(path),
                "sha256": digest,
                "equalsBase": False,
                **check,
            })

        fixture_reports.append({
            "id": fixture["id"],
            "label": fixture["label"],
            "stateKind": fixture.get("stateKind"),
            "proxy": True,
            "proxyReason": fixture.get("proxyReason"),
            "context": {
                **(fixture.get("context") or {}),
                "characterId": fixture["characterId"],
                "ascension": fixture["ascension"],
                "verifiedAscension": fixture.get("verifiedAscension"),
                "actIndex": fixture["actIndex"],
                "verifiedActIndex": fixture.get("verifiedActIndex"),
                "maxHp": fixture["maxHp"],
                "currentHp": fixture["currentHp"],
                "characterStartingHp": fixture.get("characterStartingHp"),
                "verifiedMaxHp": fixture.get("verifiedMaxHp"),
                "verifiedCurrentHp": fixture.get("verifiedCurrentHp"),
                "relics": fixture.get("relics"),
                "potions": fixture.get("potions"),
                "ascendersBaneCount": fixture["ascendersBaneCount"],
                "deckSize": fixture["deckSize"],
                "resolvedStartingDeck": fixture.get("resolvedStartingDeck"),
                "encounterSuitability": fixture.get("encounterSuitability"),
                "missingContext": fixture.get("missingContext"),
                "sourceInputValidation": fixture.get("sourceInputValidation"),
            },
            "productionScores": {
                "skip": {
                    "index": fixture["skip"].get("index", -1),
                    "value": float(fixture["skip"]["value"]),
                    "reason": fixture["skip"].get("reason"),
                },
                "candidates": [
                    {
                        "id": candidate["id"],
                        "canonicalId": candidate["canonicalId"],
                        "cardPool": candidate["cardPool"],
                        "cardType": candidate.get("cardType"),
                        "rarity": candidate.get("rarity"),
                        "upgrade": candidate["upgrade"],
                        "score": float(candidate["score"]),
                        "reason": candidate["reason"],
                    }
                    for candidate in fixture["candidates"]
                ],
                "bestRewardAllowSkip": fixture["bestRewardAllowSkip"],
                "bestRewardNoSkip": fixture["bestRewardNoSkip"],
                "argmaxIncludingSkip": fixture["argmaxIncludingSkip"],
                "argmaxMatchesBestReward": True,
            },
            "actions": actions,
        })

    manifest = {
        "schemaVersion": 1,
        "tool": "building_pick_cases.py",
        "generatedAtUtc": utc_now(),
        "source": {
            "adapterJson": rel(adapter_path),
            "adapterSha256": sha256_file(adapter_path),
        },
        "candidates": [candidate["id"] for candidate in fixtures[0]["candidates"]],
        "seedSets": {
            "selection": selection_seeds,
            "evaluation": evaluation_seeds,
            "disjoint": True,
            "status": SEED_SETS["status"],
            "note": SEED_SETS["note"],
        },
        "semanticChecks": {
            "skipEqualsBase": True,
            "addsAppendOneUnupgradedCard": True,
            "neverRemovesADefend": True,
            "contextUnchangedAcrossActions": True,
            "scenarioDeckMatchesExportedActualDeck": True,
            "ascendersBaneFlagAvoidsDoubleInsertion": True,
        },
        "scenarioHp": {
            "field": "playerCurrentHp",
            "value": PLAYER_CURRENT_HP,
            "note": "The harness takes max HP from the character (SILENT = 70) and uses full HP unless "
                    "playerCurrentHp says otherwise, so playerCurrentHp=70 preserves 70/70; this is not omitted context.",
        },
        "fixtures": fixture_reports,
        "limitations": list(LIMITATIONS),
    }
    manifest_path = output_dir / "manifest.json"
    write_json_exclusive(manifest_path, manifest)
    return manifest_path


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Build pick-candidate scenario fixtures from adapter JSON")
    parser.add_argument("--input", required=True, help="adapter output JSON")
    parser.add_argument("--output", required=True, help="new output directory")
    args = parser.parse_args(argv)

    try:
        manifest = build_cases(Path(args.input), Path(args.output))
    except FileExistsError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 3
    except (ValueError, KeyError, TypeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    print(f"wrote {manifest}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
