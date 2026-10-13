#!/usr/bin/env python3
"""Offline reconstruction-feasibility audit over the six frozen raw runs.

This is a *feasibility* instrument, not a state extractor. It reads exactly the
six runs named by ``work/building-raw-history-recovery/source-validation.json``,
re-verifies each file's raw SHA256 against the manifest, and checks the
version/character/party/ascension/win fields *before* looking at any history.
It then reports, per run and in aggregate, what the record does and does not
contain.

It deliberately does **not** claim that any player-floor is a usable pre-reward
state. Missing keys are kept distinct from present-but-empty lists; direct
deck/relic/potion snapshots, candidate grouping, within-floor event order and
pre-reward HP are reported as unavailable rather than reconstructed. No network,
no battles, no simulator and no production change are involved.

Usage:
    python scripts/building_history_audit.py
"""

from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = ROOT / "work" / "building-raw-history-recovery" / "source-validation.json"
OUTPUT_PATH = ROOT / "work" / "building-raw-history-recovery" / "reconstruction-audit.json"
FROZEN_MANIFEST_SHA = "27c74e5a4d03577199a3cc10288ea2a168f4b18d47c919fbc1b09af0ae3e2fb0"

EXPECTED_RUNS, MAX_REQUESTS = 6, 12
RETRY_POLICY = "at most one transient retry per run (12 requests for 6 runs)"
EXPECTED_BUILD, EXPECTED_CHARACTER, EXPECTED_PARTY, EXPECTED_ASCENSION = \
    "v0.111.0", "CHARACTER.SILENT", 1, 10

MUTATION_FIELDS = ("cards_gained", "cards_removed", "cards_transformed",
                   "cards_enchanted", "upgraded_cards", "downgraded_cards")
ITEM_FIELDS = ("relic_choices", "potion_choices", "potion_used", "potion_discarded",
               "relics_removed", "bought_relics", "bought_potions", "bought_colorless",
               "ancient_choice", "event_choices", "rest_site_choices", "completed_quests")
DIRECT_SNAPSHOT_FIELDS = ("deck", "relics", "potions")

# Call sites that write the audited lists (work/decompiled-v0.111.0).
WRITE_CALL_SITES = {
    "card_choices": ["Rewards/CardReward.cs:295", "Rewards/CardReward.cs:301",
                     "Rewards/CardReward.cs:316", "Rewards/CardReward.cs:326",
                     "Rooms/MerchantRoom.cs:91", "Rooms/MerchantRoom.cs:98",
                     "Rewards/SpecialCardReward.cs:102", "Models/EventModel.cs:658",
                     "Multiplayer/Game/RewardSynchronizer.cs:224"],
    "cards_gained": ["Commands/CardPileCmd.cs:455"],
    "cards_removed": ["Commands/CardPileCmd.cs:61"],
    "cards_transformed": ["Commands/CardCmd.cs:436"],
    "cards_enchanted": ["Commands/CardCmd.cs:558"],
    "upgraded_cards": ["Commands/CardCmd.cs:282"],
    "downgraded_cards": ["Commands/CardCmd.cs:219"],
    "relic_choices": ["Commands/RelicCmd.cs:39", "Rewards/RelicReward.cs:121",
                      "Rooms/MerchantRoom.cs:105"],
    "potion_choices": ["Commands/PotionCmd.cs:43", "Rewards/PotionReward.cs:99",
                       "Rooms/MerchantRoom.cs:112"],
    "potion_used": ["Models/PotionModel.cs:350"],
    "potion_discarded": ["Commands/PotionCmd.cs:57"],
    "relics_removed": ["Commands/RelicCmd.cs:63"],
    "bought_relics": ["Entities/Merchant/MerchantRelicEntry.cs:61"],
    "bought_potions": ["Entities/Merchant/MerchantPotionEntry.cs:89"],
    "bought_colorless": ["Entities/Merchant/MerchantCardEntry.cs:146"],
}
HP_SAMPLING = {
    "writer": "Runs/RunManager.cs:1713 (UpdatePlayerStatsInMapPointHistory)",
    "triggered_at": ["Runs/RunManager.cs:886 (next map point)",
                     "Runs/RunManager.cs:1625 (run end)"],
    "meaning": "one end-of-floor snapshot; not the HP at reward/choice time",
}
GROUPING_CITATION = ("Runs/History/CardChoiceHistoryEntry.cs:10-16 "
                     "(only card + was_picked; no reward-screen/group id)")
SCHEMA_CITATIONS = {
    "player_history_fields": "Runs/PlayerMapPointHistoryEntry.cs:19-135",
    "serializable_card": "Saves/Runs/SerializableCard.cs:15-32",
    "map_point_rooms": "Runs/History/MapPointHistoryEntry.cs:24-29",
    "empty_omitted": "JsonSerializeCondition(SaveIfNotCollectionEmptyOrNull) omits empties",
}
MINIMUM_CAPTURE = {
    "why": "No state has been verified by this schema audit. It does not prove that "
           "every restricted historical reconstruction is impossible; exact capture "
           "is the proposed next path to remove timing and grouping uncertainty.",
    "fields": ["map_point coord + act/room index", "reward_screen_id",
               "reward_source_type (combat/elite/boss/event/shop/relic/other)",
               "generated_candidates[] with stable index and full "
               "id/upgrade/enchantment/props identity",
               "pre_reward deck multiset (id+upgrade+enchantment+props)",
               "pre_reward relics and potions", "pre_reward current_hp/max_hp and gold",
               "player RNG state or exact seed derivation", "encounter/room model id"],
    "hook_timing": ["Bot path: src/CoopBots/BotBrain.cs:146 ChooseCardReward entry, "
                    "before BestReward, called from RewardPatches.cs:64 ChoiceForWait. "
                    "Capture actual offered cards and player state atomically; record "
                    "chosen action separately with the same decision id.",
                    "Generation/Populate alone is too early: later rewards, rerolls "
                    "or multiple selections may change state. Other choice paths "
                    "need their own verified hook, not assumed coverage."],
}


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def field_state(stat, key):
    """Missing, present-but-empty and present-with-entries are all distinct."""
    if key not in stat:
        return {"present": False, "empty": False, "entries": None}
    value = stat[key]
    if value is None:
        return {"present": True, "empty": False, "entries": None}
    if isinstance(value, list):
        return {"present": True, "empty": len(value) == 0, "entries": len(value)}
    return {"present": True, "empty": False, "entries": 1}


def _walk(data):
    for act_index, act in enumerate(data.get("map_point_history") or []):
        if not isinstance(act, list):
            continue
        for node_index, node in enumerate(act):
            if not isinstance(node, dict):
                continue
            for stat in node.get("player_stats") or []:
                if isinstance(stat, dict):
                    yield act_index, node_index, stat


def _choice_ids(stat):
    return [choice["card"]["id"] for choice in stat.get("card_choices") or []
            if isinstance(choice, dict) and isinstance(choice.get("card"), dict)
            and choice["card"].get("id")]


def _change_ids(stat):
    ids = []
    for key in ("cards_gained", "cards_removed"):
        for entry in stat.get(key) or []:
            if isinstance(entry, dict) and entry.get("id"):
                ids.append(entry["id"])
    for key in ("upgraded_cards", "downgraded_cards"):
        ids.extend(entry for entry in stat.get(key) or [] if isinstance(entry, str))
    for entry in stat.get("cards_transformed") or []:
        if isinstance(entry, dict):
            for side in ("original_card", "final_card"):
                if isinstance(entry.get(side), dict) and entry[side].get("id"):
                    ids.append(entry[side]["id"])
    for entry in stat.get("cards_enchanted") or []:
        if isinstance(entry, dict) and isinstance(entry.get("card"), dict):
            if entry["card"].get("id"):
                ids.append(entry["card"]["id"])
    return ids


def scan_run(data):
    """Per-run factual scan. Never invents a snapshot or a group."""
    c = {"player_floor_records": 0, "records_missing_card_choices": 0,
         "records_present_empty_card_choices": 0, "records_with_nonempty_card_choices": 0,
         "card_choice_entries": 0, "picked_entries": 0, "unpicked_entries": 0,
         "records_with_pick": 0, "records_picked_gt1": 0, "records_unpicked_only": 0,
         "choice_floors_with_hp": 0, "choice_floors_with_direct_snapshot": 0,
         "floors_with_same_floor_mutation_overlap": 0,
         "floors_with_duplicate_card_identity": 0}
    distribution, multi, unpicked_only = {}, [], []
    mutation = {key: {"present": 0, "empty": 0, "entries": 0} for key in MUTATION_FIELDS}
    items = {key: {"present": 0, "empty": 0, "entries": 0} for key in ITEM_FIELDS}
    for act_index, node_index, stat in _walk(data):
        c["player_floor_records"] += 1
        state = field_state(stat, "card_choices")
        if not state["present"]:
            c["records_missing_card_choices"] += 1
        elif state["empty"]:
            c["records_present_empty_card_choices"] += 1
        else:
            c["records_with_nonempty_card_choices"] += 1
            choices = stat["card_choices"]
            distribution[len(choices)] = distribution.get(len(choices), 0) + 1
            c["card_choice_entries"] += len(choices)
            picked = sum(1 for x in choices
                         if isinstance(x, dict) and x.get("was_picked") is True)
            c["picked_entries"] += picked
            c["unpicked_entries"] += len(choices) - picked
            c["records_with_pick"] += int(picked > 0)
            if picked > 1:
                c["records_picked_gt1"] += 1
                multi.append({"act_index": act_index, "node_index": node_index,
                              "choices": len(choices), "picked": picked})
            if picked == 0:
                c["records_unpicked_only"] += 1
                unpicked_only.append({"act_index": act_index, "node_index": node_index,
                                      "choices": len(choices)})
            c["choice_floors_with_hp"] += int("current_hp" in stat)
            c["choice_floors_with_direct_snapshot"] += int(
                any(key in stat for key in DIRECT_SNAPSHOT_FIELDS))
        for key in MUTATION_FIELDS:
            s = field_state(stat, key)
            mutation[key]["present"] += int(s["present"])
            mutation[key]["empty"] += int(s["empty"])
            mutation[key]["entries"] += s["entries"] or 0
        for key in ITEM_FIELDS:
            s = field_state(stat, key)
            items[key]["present"] += int(s["present"])
            items[key]["empty"] += int(s["empty"])
            items[key]["entries"] += s["entries"] or 0
        if stat.get("cards_gained") and any(
                stat.get(key) for key in ("cards_removed", "cards_transformed",
                                          "upgraded_cards", "cards_enchanted",
                                          "downgraded_cards")):
            c["floors_with_same_floor_mutation_overlap"] += 1
        choice_ids = _choice_ids(stat)
        change_ids = _change_ids(stat)
        if len(choice_ids) != len(set(choice_ids)) or \
                len(change_ids) != len(set(change_ids)):
            c["floors_with_duplicate_card_identity"] += 1
    return {"counts": c, "choice_count_distribution": distribution,
            "multi_pick_examples": multi, "unpicked_only_examples": unpicked_only,
            "mutation_counts": mutation, "item_counts": items}


def snapshot_audit(data):
    choice_stats = [stat for _, _, stat in _walk(data) if stat.get("card_choices")]
    observed = {key: sum(isinstance(stat.get(key), list) for stat in choice_stats)
                for key in DIRECT_SNAPSHOT_FIELDS}
    players = data.get("players") or []
    deck = players[0].get("deck") if players and isinstance(players[0], dict) else None
    deck = deck if isinstance(deck, list) else None
    duplicates = {}
    for card in deck or []:
        if isinstance(card, dict) and card.get("id"):
            duplicates[card["id"]] = duplicates.get(card["id"], 0) + 1
    return {"direct_deck_snapshot_at_choice_floors": observed["deck"],
            "direct_relic_snapshot_at_choice_floors": observed["relics"],
            "direct_potion_snapshot_at_choice_floors": observed["potions"],
            "terminal_deck_present": deck is not None,
            "terminal_deck_size": len(deck) if deck is not None else None,
            "terminal_deck_duplicate_ids": {k: v for k, v in duplicates.items() if v > 1},
            "initial_inventory_present": None, "hp_sampling": dict(HP_SAMPLING),
            "missing_cited_schema_fields": [key for key, count in observed.items() if count == 0],
            "note": "Observed list fields are counted, including empty lists; their "
                    "timing is not validated. Terminal state is not pre-reward state."}


def validate_run(data, expected):
    reasons = []
    fixed = {"build_id": EXPECTED_BUILD, "ascension": EXPECTED_ASCENSION,
             "game_mode": "standard", "was_abandoned": False, "modifiers": []}
    for key, value in fixed.items():
        if type(data.get(key)) is not type(value) or data.get(key) != value:
            reasons.append(f"{key} differs from fixed target {value!r}")
    if type(data.get("win")) is not bool or type(expected.get("win")) is not bool:
        reasons.append("win must be a boolean")
    players = data.get("players") if isinstance(data.get("players"), list) else None
    actual = {"build_id": data.get("build_id"),
              "party_size": len(players) if players is not None else None,
              "character": players[0].get("character") if players and
              isinstance(players[0], dict) else None,
              "ascension": data.get("ascension"), "win": data.get("win")}
    if actual["party_size"] != EXPECTED_PARTY:
        reasons.append("party size differs from fixed solo target")
    labels = {"build_id": "build_id", "party_size": "party size", "character": "character",
              "ascension": "ascension", "win": "win"}
    for key in ("build_id", "party_size", "ascension", "win"):
        if actual[key] != expected.get(key):
            reasons.append(f"{labels[key]} {actual[key]!r} != manifest {expected.get(key)!r}")
    if actual["character"] != EXPECTED_CHARACTER:
        reasons.append(f"character {actual['character']!r} != {EXPECTED_CHARACTER!r}")
    return reasons


def audit_run(entry, base_dir):
    path = Path(entry.get("path", ""))
    if not path.is_absolute():
        path = Path(base_dir) / path
    result = {"run_hash": entry.get("run_hash"), "source": entry.get("source"),
              "path": str(path), "manifest_sha256": entry.get("sha256"),
              "raw_sha256": None, "verified": False, "rejection_reasons": [],
              "processed": False}
    if not path.exists():
        result["rejection_reasons"].append(f"source file missing: {path}")
        return result
    result["raw_sha256"] = sha256_file(path)
    if result["raw_sha256"] != entry.get("sha256"):
        result["rejection_reasons"].append(
            f"raw sha256 {result['raw_sha256']} != manifest {entry.get('sha256')}")
        return result
    data = load_json(path)
    result["rejection_reasons"] = validate_run(data, entry)
    if result["rejection_reasons"]:
        return result
    result["verified"] = result["processed"] = True
    players = data.get("players") or [{}]
    result["record"] = {"build_id": data.get("build_id"),
                        "character": players[0].get("character"),
                        "party_size": len(players), "ascension": data.get("ascension"),
                        "win": data.get("win"), "game_mode": data.get("game_mode"),
                        "was_abandoned": data.get("was_abandoned"),
                        "modifiers": data.get("modifiers")}
    result["scan"] = scan_run(data)
    result["snapshots"] = snapshot_audit(data)
    c = result["scan"]["counts"]
    reasons = [
        "no complete timed pre-reward snapshot verified by this schema-only audit; "
        "observed field counts are reported separately",
        "card-choice grouping is not encoded: %s" % GROUPING_CITATION,
        "within-floor event order is not encoded across independent lists "
        "(PlayerMapPointHistoryEntry.cs:62-132); only per-list append order exists",
        "HP is an end-of-floor sample, not reward-time: %s" % HP_SAMPLING["meaning"],
        "terminal players[0].deck is post-run, not pre-reward",
    ]
    extras = [
        (c["records_picked_gt1"], "%d player-floor record(s) show more than one picked "
                                  "entry; screen grouping is ambiguous"),
        (c["records_unpicked_only"], "%d player-floor record(s) show only unpicked "
                                     "entries; skip vs alternate source is ambiguous"),
        (c["floors_with_same_floor_mutation_overlap"],
         "%d player-floor record(s) mix gain with remove/transform/upgrade; operation "
         "order is not encoded"),
        (c["floors_with_duplicate_card_identity"],
         "%d player-floor record(s) contain a duplicated card identity; instance-level "
         "attribution is not encoded"),
    ]
    reasons.extend(template % value for value, template in extras if value)
    result["reconstruction"] = {"eligible_for_reconstruction": False,
                                "usable_pre_reward_states": 0, "complete_snapshots": 0,
                                "reconstructable": None, "status": "not_verified",
                                "reasons": reasons,
                                "assumptions_if_any": []}
    return result


def aggregate(runs):
    totals = {"runs": len(runs), "verified_runs": sum(r["verified"] for r in runs),
              "rejected_runs": sum(not r["verified"] for r in runs),
              "processed_runs": sum(r["processed"] for r in runs)}
    mutation = {key: {"present": 0, "empty": 0, "entries": 0} for key in MUTATION_FIELDS}
    items = {key: {"present": 0, "empty": 0, "entries": 0} for key in ITEM_FIELDS}
    for run in runs:
        if not run["processed"]:
            continue
        for key, value in run["scan"]["counts"].items():
            totals[key] = totals.get(key, 0) + value
        for source, target in ((run["scan"]["mutation_counts"], mutation),
                               (run["scan"]["item_counts"], items)):
            for key in target:
                for state in ("present", "empty", "entries"):
                    target[key][state] += source[key][state]
    return {"counts": totals, "mutation_counts": mutation, "item_counts": items,
            "usable_pre_reward_states": 0, "complete_snapshots": 0, "reconstructable": None}


def audit_manifest(manifest_path=MANIFEST_PATH, base_dir=ROOT, generated_at=None,
                   enforce_frozen=True):
    manifest = load_json(manifest_path)
    errors, entries = [], manifest.get("runs")
    if not isinstance(entries, list) or len(entries) != EXPECTED_RUNS:
        errors.append(f"manifest must name exactly {EXPECTED_RUNS} runs, found "
                      f"{len(entries) if isinstance(entries, list) else 'non-list'}")
        entries = entries if isinstance(entries, list) else []
    ids = [entry.get("run_hash") for entry in entries]
    if len(set(ids)) != len(ids):
        errors.append("manifest run hashes are not unique")
    if enforce_frozen and sha256_file(manifest_path) != FROZEN_MANIFEST_SHA:
        errors.append("manifest differs from the frozen six-run source validation")
    runs = [] if enforce_frozen and errors else [audit_run(entry, base_dir) for entry in entries]
    return {
        "tool": "building_history_audit.py", "generated_at": generated_at or utc_now(),
        "manifest": {"path": str(manifest_path), "sha256": sha256_file(manifest_path),
                     "declared_runs": manifest.get("validated_runs"),
                     "wins": manifest.get("wins"), "losses": manifest.get("losses"),
                     "verified_pre_reward_fixtures":
                         manifest.get("verified_pre_reward_fixtures"), "errors": errors},
        "constants": {"expected_runs": EXPECTED_RUNS, "max_requests": MAX_REQUESTS,
                      "retry_policy": RETRY_POLICY, "build_id": EXPECTED_BUILD,
                      "character": EXPECTED_CHARACTER, "party_size": EXPECTED_PARTY,
                      "ascension": EXPECTED_ASCENSION},
        "grouping": {"provable_from_schema": False, "citation": GROUPING_CITATION,
                     "inline_order_is_per_list_only": True, "candidate_groups": "not_derived"},
        "schema_citations": dict(SCHEMA_CITATIONS), "write_call_sites": dict(WRITE_CALL_SITES),
        "hp_sampling": dict(HP_SAMPLING), "minimum_live_capture": dict(MINIMUM_CAPTURE),
        "runs": runs, "aggregate": aggregate(runs),
        "conclusion": {
            "all_six_verified": all(r["verified"] for r in runs) and not errors,
            "reconstructable_pre_reward_states": None, "verified_pre_reward_states": 0,
            "complete_snapshots": 0,
            "usable_for_labels_or_training": False, "production_changes": False,
            "network_or_battles": False,
            "next_smallest_step": "Keep the six raw runs as immutable provenance. Do not "
            "export states; if pre-reward decisions are still needed, implement the "
            "minimum live structured capture at the cited decision hook. Captured records "
            "require a separate schema validator; this tool stays frozen to these six runs."},
    }


def write_audit(audit, output_path=OUTPUT_PATH):
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(audit, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    return output_path


def main():
    audit = audit_manifest()
    write_audit(audit)
    counts = audit["aggregate"]["counts"]
    print("manifest errors:", audit["manifest"]["errors"])
    print("verified/rejected:", counts["verified_runs"], "/", counts["rejected_runs"])
    print("nonempty card-choice floors:", counts.get("records_with_nonempty_card_choices", 0))
    print("choice entries:", counts.get("card_choice_entries", 0),
          "picked:", counts.get("picked_entries", 0), "unpicked:", counts.get("unpicked_entries", 0))
    print("usable pre-reward states:", audit["aggregate"]["usable_pre_reward_states"])
    print("wrote:", OUTPUT_PATH)
    return 0 if audit["conclusion"]["all_six_verified"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
