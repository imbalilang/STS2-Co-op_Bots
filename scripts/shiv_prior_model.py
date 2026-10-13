#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Offline, explainable marginal model for the fixed 14-card shiv whitelist.

What this is
------------
A small, deterministic, standard-library-only HEURISTIC UTILITY model. It is
NOT a trained strength predictor, NOT a combat simulator, and it makes NO
absolute HP / damage / win-probability claims. Units are arbitrary heuristic
utility intended only for relative, same-context comparison of "add candidate C
to this exact full deck".

Inputs
------
* ``deck``      : full deck as a list of card IDs (duplicates are meaningful).
* ``candidates``: whitelist IDs offered to the deck (defaults to all 14).
* ``context``   : explicit energy / hand-draw / turn-horizon assumptions.
* ``params``    : all weights and caps (see ``DEFAULT_PARAMS``); manual/unfitted.

Allowed deck IDs = 14 whitelist nodes + 5 fixed base-context cards. PANACHE is
whitelist-external and rejected. Unsupported IDs and any upgrade suffix are
rejected loudly instead of being silently ignored.

Core API (pure, non-mutating)
-----------------------------
* ``evaluate_deck(deck, context, params)`` -> ``Evaluation``
* ``rank_choices(deck, candidates, context, params, community, community_edges)``

Candidate score = ``evaluate(deck + [candidate]) - evaluate(deck)``; SKIP is the
same deck with delta 0. The bounded community prior is computed separately from
observed held->offered draft direction and only ever ties-breaks positive
mechanism candidates; it cannot flip pick-vs-skip and cannot overturn a
mechanism gap larger than the prior cap.

Explicit approximations
-----------------------
* Draw is one bounded one-pass estimate (drawn draw-cards are never re-expanded);
  horizon draws are ``initial_hand + (turns-1) * draw_per_turn`` and zero turns
  yield zero plays / zero utility.
* Energy is a single scalar budget for paid plays only; zero-cost cards are never
  suppressed by paid capacity.
* Every payoff power shares one setup-delay / active-fraction gate, so a delay
  at or beyond the horizon gives no payoff at all.
* Afterimage counts the producers' own plays as well as generated shivs, but
  excludes its own activation and unverified Knife Trap replays; no ordering is
  claimed.
* Accuracy's per-Shiv bonus counts ordinary Shiv supply plus the explicit Knife
  Trap replay opportunity ``kt_active * min(exhausted_pool, replay_cap)``. The
  replays add no new supply, are never re-fed into the exhausted pool, and give
  no Afterimage replay credit. Phantom's first-Shiv timing interaction with
  replays is still unmodelled (runtime rule unconfirmed).

Reads (read-only): ``work/shiv-relationship-graph/graph.json`` for the community
draft layer. Writes: ``work/shiv-prior-model/**`` only.
"""

import argparse
import copy
import json
import math
import os
import sys
from collections import Counter
from dataclasses import dataclass

# --------------------------------------------------------------------------- #
# Paths, whitelist and card metadata
# --------------------------------------------------------------------------- #

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SCRIPT_DIR)

GRAPH_PATH = os.path.join(ROOT, "work", "shiv-relationship-graph", "graph.json")
OUT_DIR = os.path.join(ROOT, "work", "shiv-prior-model")
RESULTS_PATH = os.path.join(OUT_DIR, "results.json")
PARAMS_PATH = os.path.join(OUT_DIR, "params.json")
SCENARIO_DIR = os.path.join(OUT_DIR, "scenarios")

GENERATOR = "scripts/shiv_prior_model.py"
SCHEMA = "shiv-prior-model"
SCHEMA_VERSION = 1

# Canonical 14-node whitelist order (same order as the relationship graph).
WHITELIST = [
    "BLADE_DANCE",
    "CLOAK_AND_DAGGER",
    "LEADING_STRIKE",
    "INFINITE_BLADES",
    "ACCURACY",
    "KNIFE_TRAP",
    "PHANTOM_BLADES",
    "BACKFLIP",
    "ACROBATICS",
    "ADRENALINE",
    "AFTERIMAGE",
    "FOOTWORK",
    "LEG_SWEEP",
    "DEFLECT",
]

# Fixed base-deck context: allowed in the deck but NOT candidates.
BASE_DECK_CONTEXT = [
    "ASCENDERS_BANE",
    "DEFEND_SILENT",
    "NEUTRALIZE",
    "STRIKE_SILENT",
    "SURVIVOR",
]

EXTERNAL_REFERENCE = ["PANACHE"]  # explicitly rejected

ALLOWED_DECK = set(WHITELIST) | set(BASE_DECK_CONTEXT)
CANDIDATE_IDS = tuple(WHITELIST)

PRODUCERS = ["BLADE_DANCE", "CLOAK_AND_DAGGER", "LEADING_STRIKE", "INFINITE_BLADES"]
PRODUCER_SET = frozenset(PRODUCERS)
PAYOFFS = ["ACCURACY", "KNIFE_TRAP", "PHANTOM_BLADES", "AFTERIMAGE"]
POWER_CARDS = ["INFINITE_BLADES", "ACCURACY", "PHANTOM_BLADES", "AFTERIMAGE", "FOOTWORK"]
# Footwork scales explicit NORMAL direct-block cards only (graph M-017..M-020
# plus the fixed base-context block cards DEFEND_SILENT / SURVIVOR); triggered
# Afterimage block is deliberately excluded.
FOOTWORK_BLOCK_TARGETS = [
    "CLOAK_AND_DAGGER",
    "BACKFLIP",
    "LEG_SWEEP",
    "DEFLECT",
    "DEFEND_SILENT",
    "SURVIVOR",
]

# Per-card metadata used by the heuristic. Costs come from the graph snapshot.
CARD_STATS = {
    # whitelist
    "BLADE_DANCE": {"cost": 1, "shivs": 3, "exhaust": True},
    "CLOAK_AND_DAGGER": {"cost": 1, "shivs": 1, "block": 6},
    "LEADING_STRIKE": {"cost": 1, "shivs": 2, "damage": 3},
    "INFINITE_BLADES": {"cost": 1, "power": True},
    "ACCURACY": {"cost": 1, "power": True},
    "KNIFE_TRAP": {"cost": 2, "replay": True},
    "PHANTOM_BLADES": {"cost": 1, "power": True},
    "BACKFLIP": {"cost": 1, "block": 5, "draw": 2},
    "ACROBATICS": {"cost": 1, "draw": 3},
    "ADRENALINE": {"cost": 0, "exhaust": True, "draw": 2, "energy": 1},
    "AFTERIMAGE": {"cost": 1, "power": True},
    "FOOTWORK": {"cost": 1, "power": True},
    "LEG_SWEEP": {"cost": 2, "block": 11, "weak": 2},
    "DEFLECT": {"cost": 0, "block": 4},
    # fixed base context
    "STRIKE_SILENT": {"cost": 1, "damage": 6},
    "DEFEND_SILENT": {"cost": 1, "block": 5},
    "SURVIVOR": {"cost": 1, "block": 8},
    "NEUTRALIZE": {"cost": 0, "damage": 3, "weak": 1},
    "ASCENDERS_BANE": {"cost": 0, "unplayable": True},
}

# --------------------------------------------------------------------------- #
# Explicit, manual, unfitted parameters
# --------------------------------------------------------------------------- #

DEFAULT_PARAMS = {
    "label": "manual_unfitted_heuristic",
    "note": (
        "Hand-tuned, NOT fitted to data and NOT validated. Units are heuristic "
        "utility, not HP, damage or win probability. Every weight/cap is here so "
        "the model can be inspected and re-tuned in one place."
    ),
    "assumptions": {
        "initial_hand": 5,
        "cards_drawn_per_turn": 5,
        "energy_per_turn": 3,
        "turn_horizon": 4,
        "power_setup_delay_turns": 1,
    },
    "weights": {
        "w_shiv_play": 1.0,
        "w_accuracy_per_shiv": 0.60,
        "w_phantom_first_shiv": 1.10,
        "w_knife_trap_replay": 0.50,
        "w_afterimage_block": 0.18,
        "w_direct_block": 0.35,
        "w_weak": 0.40,
        "w_dex_block": 0.55,
        "w_draw_support": 0.22,
        "w_energy_support": 0.30,
        "w_attack_damage": 0.12,
        "w_power_setup_penalty": 0.10,
        "w_deck_dilution": 0.02,
    },
    "caps": {
        "shiv_supply_cap": 9.0,
        "active_payoff_cap": 1.0,
        "knife_trap_replay_cap": 6.0,
        "afterimage_trigger_cap": 24.0,
        "dex_block_cap": 3.0,
        "cycle_cap": 2.0,
        "draw_support_cap": 6.0,
        "energy_support_cap": 3.0,
        "block_utility_cap": 36.0,
        "weak_cap": 4.0,
        "attack_cap": 36.0,
        "power_penalty_cap": 5.0,
    },
    "community": {
        "prior_cap": 0.25,
        "edge_log_clamp": 1.0,
        "reliability_denominator": 1000.0,
        "top_n": 2,
        "note": (
            "Bounded tie-break only. reliability=offers/(offers+1000) is an "
            "explicit heuristic, NOT independent-run confidence. Only the "
            "observed held->offered draft direction is used; no winrate/co/NPMI."
        ),
    },
}


# --------------------------------------------------------------------------- #
# Validation
# --------------------------------------------------------------------------- #

def _reject_bad_id(card_id, role):
    if not isinstance(card_id, str) or not card_id:
        raise ValueError("%s must be a non-empty string, got %r" % (role, card_id))
    if "+" in card_id:
        raise ValueError(
            "unsupported upgrade %r in %s: upgrades are not modelled" % (card_id, role)
        )
    if card_id in EXTERNAL_REFERENCE:
        raise ValueError(
            "%r in %s is outside the 14-card whitelist (reference only)" % (card_id, role)
        )
    if card_id not in ALLOWED_DECK:
        raise ValueError("unsupported id %r in %s" % (card_id, role))


def validate_deck(deck):
    if not isinstance(deck, (list, tuple)):
        raise ValueError("deck must be a list of card IDs")
    for card_id in deck:
        _reject_bad_id(card_id, "deck")


def validate_candidates(candidates):
    if not isinstance(candidates, (list, tuple)):
        raise ValueError("candidates must be a list of card IDs")
    for card_id in candidates:
        if card_id == "SKIP":
            continue
        _reject_bad_id(card_id, "candidates")
        if card_id not in WHITELIST:
            raise ValueError(
                "%r in candidates is a fixed base-context card, not a candidate" % card_id
            )


# --------------------------------------------------------------------------- #
# Context and helpers
# --------------------------------------------------------------------------- #

CONTEXT_FIELDS = (
    "initial_hand",
    "cards_drawn_per_turn",
    "energy_per_turn",
    "turn_horizon",
    "power_setup_delay_turns",
)
_INT_CONTEXT_FIELDS = (
    "initial_hand",
    "cards_drawn_per_turn",
    "turn_horizon",
    "power_setup_delay_turns",
)


def _validate_context_value(field, raw):
    if isinstance(raw, bool) or not isinstance(raw, (int, float)):
        raise ValueError("context %s must be a number, got %r" % (field, raw))
    if isinstance(raw, float) and not math.isfinite(raw):
        raise ValueError("context %s must be finite, got %r" % (field, raw))
    if raw < 0:
        raise ValueError("context %s must be >= 0, got %r" % (field, raw))
    if field in _INT_CONTEXT_FIELDS:
        if float(raw) != int(raw):
            raise ValueError("context %s must be an integer, got %r" % (field, raw))
        return int(raw)
    return raw


@dataclass(frozen=True)
class Context:
    initial_hand: int = 5
    cards_drawn_per_turn: int = 5
    energy_per_turn: int = 3
    turn_horizon: int = 4
    power_setup_delay_turns: int = 1

    @staticmethod
    def from_any(value, defaults=None):
        """Build a validated Context.

        ``defaults`` (typically ``params['assumptions']``) supplies the base
        values; an explicit mapping / Context overrides them. Unknown keys are
        rejected, never silently ignored. ``turn_horizon`` may be 0, which the
        evaluator resolves to zero plays / zero utility.
        """
        if isinstance(value, Context):
            value = {field: getattr(value, field) for field in CONTEXT_FIELDS}
        if value is not None and not isinstance(value, dict):
            raise ValueError("context must be a Context or a mapping")
        overrides = dict(value or {})
        unknown = sorted(set(overrides) - set(CONTEXT_FIELDS))
        if unknown:
            raise ValueError("unknown context keys: %s" % ", ".join(unknown))
        merged = {}
        for field in CONTEXT_FIELDS:
            if field in overrides:
                merged[field] = overrides[field]
            elif defaults is not None and field in defaults:
                merged[field] = defaults[field]
            else:
                merged[field] = getattr(Context, field)
        validated = {f: _validate_context_value(f, merged[f]) for f in CONTEXT_FIELDS}
        return Context(**validated)


def _params_from_any(value):
    if value is None:
        return copy.deepcopy(DEFAULT_PARAMS)
    return value


def _saturate(x, cap):
    if x <= 0.0 or cap <= 0.0:
        return 0.0
    return cap * (1.0 - math.exp(-x / cap))


def _clamp(x, lo, hi):
    return max(lo, min(hi, x))


def _card_field(card, field):
    return CARD_STATS.get(card, {}).get(field, 0)


def _is_one_use(card):
    stats = CARD_STATS.get(card, {})
    return bool(stats.get("exhaust") or stats.get("power"))


def _is_playable(card):
    return not CARD_STATS.get(card, {}).get("unplayable")


# --------------------------------------------------------------------------- #
# Core evaluation
# --------------------------------------------------------------------------- #

@dataclass
class Evaluation:
    total: float
    components: dict
    features: dict

    def to_dict(self):
        return {
            "total": self.total,
            "components": {k: self.components[k] for k in sorted(self.components)},
            "features": {k: self.features[k] for k in sorted(self.features)},
        }


def _zero_evaluation(size):
    components = {key: 0.0 for key in _COMPONENT_KEYS}
    features = {key: 0.0 for key in _FEATURE_KEYS}
    features["deck_size"] = float(size)
    return Evaluation(0.0, components, features)


def evaluate_deck(deck, context=None, params=None):
    """Pure heuristic utility of a full deck. Never mutates ``deck``.

    Explicit approximations: draw is one bounded one-pass estimate (drawn
    draw-cards are never re-expanded), energy is a single uniform scalar budget
    for all paid plays (zero-cost plays are exempt), and every power shares one
    setup-delay / active-fraction gate. No ordering or exact-combat claim.
    """
    validate_deck(deck)
    p = _params_from_any(params)
    ctx = Context.from_any(context, p.get("assumptions"))
    w = p["weights"]
    caps = p["caps"]
    copies = Counter(deck)
    size = len(deck)

    if size == 0 or int(ctx.turn_horizon) <= 0:
        return _zero_evaluation(size)

    turns = int(ctx.turn_horizon)
    draw_per_turn = ctx.cards_drawn_per_turn

    # --- Draw access: initial hand + (turns-1) draws, plus ONE bounded pass of
    # draw-card value. Drawn draw-cards are not re-expanded (no self-sustaining
    # draw recursion). Extra draw changes both access and cycling.
    base_seen = ctx.initial_hand + (turns - 1) * draw_per_turn
    access_base = min(1.0, base_seen / size)
    # Gate paid draw by a preliminary budget, before expanding access. This
    # prevents an unplayable Acrobatics from granting free additional draws.
    base_paid_demand = sum(count * access_base * _card_field(card, "cost")
                           for card, count in copies.items() if _is_playable(card))
    base_energy = float(ctx.energy_per_turn) * turns + copies["ADRENALINE"] * access_base
    base_capacity = min(1.0, base_energy / base_paid_demand) if base_paid_demand else 1.0
    extra_draw = 0.0
    for card, count in copies.items():
        bonus = _card_field(card, "draw")
        if bonus:
            draw_capacity = base_capacity if _card_field(card, "cost") > 0 else 1.0
            extra_draw += count * access_base * bonus * draw_capacity
    total_draws = base_seen + extra_draw
    seen = min(float(size), total_draws)
    draw_fraction = seen / size
    effective_dpt = draw_per_turn + extra_draw / turns
    cycles = _clamp(turns / max(1.0, size / max(1e-9, effective_dpt)), 1.0, caps["cycle_cap"])

    # --- Play opportunities (draw-limited, before any energy budget).
    opportunities = {}
    for card, count in copies.items():
        if not _is_playable(card):
            opportunities[card] = 0.0
            continue
        draw_limited = count * draw_fraction
        cycles_for_card = 1.0 if _is_one_use(card) else cycles
        opportunities[card] = draw_limited * cycles_for_card

    # --- Energy: one scalar budget compresses paid play opportunities.
    # Zero-cost cards are never suppressed by paid capacity.
    energy_total = float(ctx.energy_per_turn) * turns
    energy_total += opportunities.get("ADRENALINE", 0.0) * _card_field("ADRENALINE", "energy")
    paid_demand = 0.0
    for card, opp in opportunities.items():
        cost = _card_field(card, "cost")
        if cost > 0:
            paid_demand += opp * cost
    capacity = _clamp(energy_total / paid_demand, 0.0, 1.0) if paid_demand > 0.0 else 1.0

    plays = {}
    zero_cost_plays = 0.0
    paid_plays = 0.0
    for card, opp in opportunities.items():
        cost = _card_field(card, "cost")
        if opp <= 0.0:
            plays[card] = 0.0
        elif cost <= 0:
            plays[card] = opp
            zero_cost_plays += opp
        else:
            plays[card] = opp * capacity
            paid_plays += plays[card]

    # --- Power timing: every power needs ``power_setup_delay_turns`` before it
    # does anything, so it is active for ``active_turns`` of the horizon. A
    # delay >= horizon therefore yields no payoff at all.
    active_turns = max(0.0, turns - int(ctx.power_setup_delay_turns))
    power_active_fraction = active_turns / turns

    # --- Shiv supply (producers only; saturating).
    shivs_raw = 0.0
    for producer in ("BLADE_DANCE", "CLOAK_AND_DAGGER", "LEADING_STRIKE"):
        shivs_raw += _card_field(producer, "shivs") * plays.get(producer, 0.0)
    shivs_raw += plays.get("INFINITE_BLADES", 0.0) * active_turns
    shiv_supply = _saturate(shivs_raw, caps["shiv_supply_cap"])

    # --- Payoff power activity: the active fraction applies to all of them.
    def _power_active(card):
        played = plays.get(card, 0.0)
        return _saturate(played, caps["active_payoff_cap"]) * power_active_fraction

    accuracy_active = _power_active("ACCURACY")
    phantom_active = _power_active("PHANTOM_BLADES")
    afterimage_active = _power_active("AFTERIMAGE")
    footwork_active = _power_active("FOOTWORK")

    # --- Afterimage: every played card triggers it, including the producers'
    # own plays and generated shivs. Its activation play is excluded, and the
    # unverified Knife Trap replay is excluded. No ordering is claimed.
    played_cards_total = sum(plays.values())
    afterimage_activation_plays = plays.get("AFTERIMAGE", 0.0)
    afterimage_non_replay_plays = played_cards_total - afterimage_activation_plays
    afterimage_triggers = min(
        shiv_supply + afterimage_non_replay_plays, caps["afterimage_trigger_cap"]
    )

    # --- Knife Trap: conservative one-shot replay of already-exhausted shivs.
    # Replay adds no new supply and is not re-counted for further replays.
    kt_active = _saturate(plays.get("KNIFE_TRAP", 0.0), caps["active_payoff_cap"])
    exhausted_pool = shiv_supply
    knife_trap_replay = min(exhausted_pool, caps["knife_trap_replay_cap"])
    # --- Accuracy's per-Shiv bonus also applies to the Shivs Knife Trap replays.
    # This named, inspectable opportunity is bounded and one-directional: it adds
    # no new supply and never increases exhausted_pool or Afterimage triggers.
    knife_trap_replay_opportunity = kt_active * knife_trap_replay

    # --- Board / support aggregates.
    direct_block_plays = 0.0
    normal_block_plays = 0.0
    weak_total = 0.0
    draw_total = 0.0
    energy_support_total = 0.0
    attack_damage = 0.0
    for card in copies:
        plays_card = plays.get(card, 0.0)
        block = _card_field(card, "block")
        if block:
            direct_block_plays += plays_card * block
            if card in FOOTWORK_BLOCK_TARGETS:
                normal_block_plays += plays_card
        weak_total += plays_card * _card_field(card, "weak")
        draw_total += plays_card * _card_field(card, "draw")
        energy_support_total += plays_card * _card_field(card, "energy")
        attack_damage += plays_card * _card_field(card, "damage")

    producer_plays = sum(plays.get(card, 0.0) for card in PRODUCER_SET)
    non_shiv_plays = played_cards_total - producer_plays
    power_copies = sum(copies.get(c, 0) for c in POWER_CARDS)

    components = {
        "shiv_play": w["w_shiv_play"] * shiv_supply,
        "accuracy": w["w_accuracy_per_shiv"] * accuracy_active
        * (shiv_supply + knife_trap_replay_opportunity),
        "phantom_first_shiv": w["w_phantom_first_shiv"] * phantom_active
        * min(active_turns, shiv_supply),
        "knife_trap": w["w_knife_trap_replay"] * kt_active * knife_trap_replay,
        "afterimage": w["w_afterimage_block"] * afterimage_active * afterimage_triggers,
        "footwork": w["w_dex_block"] * footwork_active
        * min(normal_block_plays, caps["dex_block_cap"]),
        "direct_block": w["w_direct_block"] * _saturate(direct_block_plays, caps["block_utility_cap"]),
        "weak": w["w_weak"] * _saturate(weak_total, caps["weak_cap"]),
        "attack_damage": w["w_attack_damage"] * _saturate(attack_damage, caps["attack_cap"]),
        "draw_support": w["w_draw_support"] * _saturate(draw_total, caps["draw_support_cap"]),
        "energy_support": w["w_energy_support"] * _saturate(energy_support_total, caps["energy_support_cap"]),
        "power_setup_penalty": -w["w_power_setup_penalty"] * _saturate(power_copies, caps["power_penalty_cap"]),
        "deck_dilution": -w["w_deck_dilution"] * size,
    }
    total = sum(components.values())

    features = {
        "deck_size": size,
        "base_seen": base_seen,
        "extra_draw": extra_draw,
        "total_draws": total_draws,
        "cards_seen": seen,
        "draw_fraction": draw_fraction,
        "effective_draw_per_turn": effective_dpt,
        "cycles": cycles,
        "energy_total": energy_total,
        "energy_demand": paid_demand,
        "energy_capacity": capacity,
        "zero_cost_plays": zero_cost_plays,
        "paid_plays": paid_plays,
        "active_turns": active_turns,
        "power_active_fraction": power_active_fraction,
        "shiv_supply_raw": shivs_raw,
        "shiv_supply": shiv_supply,
        "producer_plays": producer_plays,
        "non_shiv_plays": non_shiv_plays,
        "played_cards_total": played_cards_total,
        "afterimage_activation_plays": afterimage_activation_plays,
        "afterimage_non_replay_plays": afterimage_non_replay_plays,
        "afterimage_triggers": afterimage_triggers,
        "knife_trap_replays": knife_trap_replay,
        "knife_trap_replay_opportunity": knife_trap_replay_opportunity,
        "direct_block_plays": direct_block_plays,
        "normal_block_plays": normal_block_plays,
        "weak_total": weak_total,
        "draw_total": draw_total,
        "energy_support_total": energy_support_total,
        "attack_damage": attack_damage,
        "power_copies": float(power_copies),
        "accuracy_active": accuracy_active,
        "phantom_active": phantom_active,
        "afterimage_active": afterimage_active,
        "footwork_active": footwork_active,
        "knife_trap_active": kt_active,
        "exhausted_pool": exhausted_pool,
    }
    return Evaluation(total, components, features)


_COMPONENT_KEYS = [
    "shiv_play",
    "accuracy",
    "phantom_first_shiv",
    "knife_trap",
    "afterimage",
    "footwork",
    "direct_block",
    "weak",
    "attack_damage",
    "draw_support",
    "energy_support",
    "power_setup_penalty",
    "deck_dilution",
]

_FEATURE_KEYS = [
    "deck_size",
    "base_seen",
    "extra_draw",
    "total_draws",
    "cards_seen",
    "draw_fraction",
    "effective_draw_per_turn",
    "cycles",
    "energy_total",
    "energy_demand",
    "energy_capacity",
    "zero_cost_plays",
    "paid_plays",
    "active_turns",
    "power_active_fraction",
    "shiv_supply_raw",
    "shiv_supply",
    "producer_plays",
    "non_shiv_plays",
    "played_cards_total",
    "afterimage_activation_plays",
    "afterimage_non_replay_plays",
    "afterimage_triggers",
    "knife_trap_replays",
    "knife_trap_replay_opportunity",
    "direct_block_plays",
    "normal_block_plays",
    "weak_total",
    "draw_total",
    "energy_support_total",
    "attack_damage",
    "power_copies",
    "accuracy_active",
    "phantom_active",
    "afterimage_active",
    "footwork_active",
    "knife_trap_active",
    "exhausted_pool",
]


# --------------------------------------------------------------------------- #
# Community prior (observed held -> offered draft direction only)
# --------------------------------------------------------------------------- #

_EDGE_CACHE = {}


def load_community_edges(path=None):
    """Return {(held, offered): {"lift": float, "offers": int}} from graph.json."""
    key = path or GRAPH_PATH
    if key in _EDGE_CACHE:
        return _EDGE_CACHE[key]
    with open(key, "r", encoding="utf-8") as handle:
        graph = json.load(handle)
    edges = {}
    for edge in graph["layers"]["community_draft"]["edges"]:
        edges[(edge["source"], edge["target"])] = {
            "lift": float(edge["lift"]),
            "offers": int(edge["offers"]),
        }
    _EDGE_CACHE[key] = edges
    return edges


def community_prior(deck, candidate, edges, params):
    """Bounded small tie-break prior for one held deck and one candidate.

    Direction is strictly observed ``held -> offered``. Held IDs are deduped so
    extra copies never multiply the prior. Missing edges are neutral (unknown),
    never negative evidence.
    """
    cfg = params["community"]
    held = sorted({card for card in deck if card in WHITELIST})
    values = []
    unknown = 0
    known = 0
    positive = 0
    for held_id in held:
        edge = edges.get((held_id, candidate))
        if edge is None:
            unknown += 1
            continue
        known += 1
        lift = edge["lift"]
        offers = edge["offers"]
        if lift <= 1.0:
            # Observed but not a positive direction: counted as known, never
            # treated as negative evidence.
            continue
        positive += 1
        log_lift = _clamp(math.log(lift), 0.0, cfg["edge_log_clamp"])
        reliability = offers / (offers + cfg["reliability_denominator"])
        values.append(log_lift * reliability)
    values.sort(reverse=True)
    top = values[: cfg["top_n"]]
    raw = sum(top) / len(top) if top else 0.0
    applied = _clamp(raw, 0.0, cfg["prior_cap"])
    return {
        "raw": raw,
        "applied": applied,
        "unknown_edges": unknown,
        "known_edges": known,
        "positive_edges": positive,
    }


# --------------------------------------------------------------------------- #
# Ranking
# --------------------------------------------------------------------------- #

def rank_choices(deck, candidates=None, context=None, params=None, community=True,
                 community_edges=None):
    """Rank candidates (+ SKIP) by marginal heuristic utility.

    Pure: neither ``deck`` nor ``candidates`` is mutated. Returns a plain dict
    (JSON-serialisable) with baseline, per-candidate explanations, ranking and
    winner. Community prior only tie-breaks positive mechanism candidates whose
    gap to the best positive is within the prior cap.
    """
    validate_deck(deck)
    cand_ids = list(CANDIDATE_IDS if candidates is None else candidates)
    validate_candidates(cand_ids)
    # SKIP is implicit and duplicate candidate IDs are collapsed (order-stable).
    cand_ids = [card for card in cand_ids if card != "SKIP"]
    seen_ids = set()
    deduped_ids = []
    for card in cand_ids:
        if card not in seen_ids:
            seen_ids.add(card)
            deduped_ids.append(card)
    cand_ids = deduped_ids
    p = _params_from_any(params)
    ctx = Context.from_any(context, p.get("assumptions"))

    baseline = evaluate_deck(deck, ctx, p)
    edges = None
    if community:
        edges = community_edges if community_edges is not None else load_community_edges()

    prior_cap = p["community"]["prior_cap"]
    rows = []
    for candidate in cand_ids:
        probe_deck = list(deck) + [candidate]
        evaluated = evaluate_deck(probe_deck, ctx, p)
        mech_delta = evaluated.total - baseline.total
        delta_components = {
            name: evaluated.components[name] - baseline.components[name]
            for name in _COMPONENT_KEYS
        }
        prior = (
            community_prior(deck, candidate, edges, p)
            if community
            else {
                "raw": 0.0,
                "applied": 0.0,
                "unknown_edges": 0,
                "known_edges": 0,
                "positive_edges": 0,
            }
        )
        rows.append({
            "id": candidate,
            "mech_delta": mech_delta,
            "prior_raw": prior["raw"],
            "prior_applied": prior["applied"],
            "prior_used": False,
            "unknown_edges": prior["unknown_edges"],
            "known_edges": prior["known_edges"],
            "positive_edges": prior["positive_edges"],
            "final_score": mech_delta,
            "delta_components": delta_components,
            "evaluated": evaluated.to_dict(),
        })

    positives = [row for row in rows if row["mech_delta"] > 0.0]
    best_positive = max((row["mech_delta"] for row in positives), default=None)
    community_changes = []
    if best_positive is not None:
        for row in rows:
            within_cap = (best_positive - row["mech_delta"]) <= prior_cap
            eligible = row["mech_delta"] > 0.0 and within_cap and community
            if eligible:
                if row["prior_applied"] > 0.0:
                    row["prior_used"] = True
                row["final_score"] = row["mech_delta"] + row["prior_applied"]
            community_changes.append({
                "id": row["id"],
                "mech_delta": row["mech_delta"],
                "prior_raw": row["prior_raw"],
                "prior_applied": row["prior_applied"],
                "prior_used": row["prior_used"],
                "known_edges": row["known_edges"],
                "positive_edges": row["positive_edges"],
                "final_score": row["final_score"],
            })

    entries = [{
        "id": row["id"],
        "final_score": row["final_score"],
        "is_skip": False,
    } for row in rows]
    entries.append({"id": "SKIP", "final_score": 0.0, "is_skip": True})
    entries.sort(key=lambda e: (-e["final_score"], 0 if e["is_skip"] else 1, e["id"]))

    # Winner is defined as ranking[0], guaranteed even on exact ties.
    ranking = [entry["id"] for entry in entries]
    winner = ranking[0]

    return {
        "schema": SCHEMA,
        "schema_version": SCHEMA_VERSION,
        "generator": GENERATOR,
        "label": p["label"],
        "assumptions": dict(p["assumptions"]),
        "community_enabled": bool(community),
        "context": {
            "initial_hand": ctx.initial_hand,
            "cards_drawn_per_turn": ctx.cards_drawn_per_turn,
            "energy_per_turn": ctx.energy_per_turn,
            "turn_horizon": ctx.turn_horizon,
            "power_setup_delay_turns": ctx.power_setup_delay_turns,
        },
        "deck_size": len(deck),
        "deck_counts": {k: v for k, v in sorted(Counter(deck).items())},
        "baseline": baseline.to_dict(),
        "candidates": [{k: v for k, v in row.items() if k != "evaluated"} for row in rows],
        "evaluated_candidates": {row["id"]: row["evaluated"] for row in rows},
        "ranking": list(ranking),
        "top3": [entry["id"] for entry in entries if not entry["is_skip"]][:3],
        "skip": {
            "mech_delta": 0.0,
            "final_score": 0.0,
            "is_winner": winner == "SKIP",
        },
        "winner": winner,
        "community_changes": community_changes,
        "approximations": [
            "draw is one bounded one-pass estimate; drawn draw-cards are not re-expanded",
            "energy is one uniform scalar budget for paid plays; zero-cost plays are exempt",
            "all payoff powers share one setup delay / active fraction; no card ordering",
            "afterimage trigger count excludes its own activation and Knife Trap replays",
            "accuracy counts ordinary Shiv supply plus the explicit Knife Trap replay opportunity; replays feed no new supply",
            "Phantom first-Shiv timing with Knife Trap replays is not modelled",
        ],
    }


# --------------------------------------------------------------------------- #
# Fixture scenarios (illustrative only; NOT effectiveness evidence)
# --------------------------------------------------------------------------- #

_STARTER = (
    ["STRIKE_SILENT"] * 4
    + ["DEFEND_SILENT"] * 4
    + ["SURVIVOR", "NEUTRALIZE", "ASCENDERS_BANE"]
)

SCENARIOS = [
    {
        "name": "starter",
        "description": "Base starter context only; no whitelist cards present.",
        "deck": list(_STARTER),
    },
    {
        "name": "producers_no_payoffs",
        "description": "Shiv producers present, no shiv payoff cards.",
        "deck": list(_STARTER) + ["BLADE_DANCE"] * 2 + ["CLOAK_AND_DAGGER"] * 2
        + ["LEADING_STRIKE", "INFINITE_BLADES"],
    },
    {
        "name": "payoffs_no_producers",
        "description": "Shiv payoffs present, no shiv producers.",
        "deck": list(_STARTER) + ["ACCURACY"] * 2 + ["KNIFE_TRAP", "PHANTOM_BLADES", "AFTERIMAGE"],
    },
    {
        "name": "defense_poor",
        "description": "Offense-heavy deck with almost no block.",
        "deck": ["STRIKE_SILENT"] * 4 + ["NEUTRALIZE", "ASCENDERS_BANE"]
        + ["BLADE_DANCE"] * 2 + ["LEADING_STRIKE"] * 2 + ["ACCURACY"] * 2 + ["INFINITE_BLADES"],
    },
    {
        "name": "energy_heavy",
        "description": "Many zero/one-cost enablers plus expensive cards; energy contention.",
        "deck": list(_STARTER) + ["ADRENALINE"] * 3 + ["KNIFE_TRAP"] * 2
        + ["LEG_SWEEP"] * 2 + ["BLADE_DANCE", "CLOAK_AND_DAGGER"],
    },
    {
        "name": "saturated",
        "description": "Already-saturated producer/payoff core; further copies dilute.",
        "deck": list(_STARTER) + ["BLADE_DANCE"] * 6 + ["CLOAK_AND_DAGGER"] * 6
        + ["LEADING_STRIKE"] * 6 + ["INFINITE_BLADES"] * 4 + ["ACCURACY"] * 4
        + ["PHANTOM_BLADES"] * 2,
    },
    {
        "name": "duplicates",
        "description": "Heavy duplicates to exercise copy counting and prior dedup.",
        "deck": list(_STARTER) + ["BLADE_DANCE"] * 3 + ["ACCURACY"] * 3 + ["CLOAK_AND_DAGGER"] * 2,
    },
    {
        "name": "no_supported_community_edges",
        "description": "Held cards have no observed outgoing draft edge (BLADE_DANCE only).",
        "deck": list(_STARTER) + ["BLADE_DANCE"] * 2,
    },
]


def build_scenarios(params=None, context=None, community=True):
    p = _params_from_any(params)
    ctx = Context.from_any(context, p.get("assumptions"))
    reports = []
    for scenario in SCENARIOS:
        result = rank_choices(scenario["deck"], context=ctx, params=p, community=community)
        reports.append({
            "name": scenario["name"],
            "description": scenario["description"],
            "deck_size": len(scenario["deck"]),
            "deck_counts": {k: v for k, v in sorted(Counter(scenario["deck"]).items())},
            "winner": result["winner"],
            "top3": result["top3"],
            "skip": result["skip"],
            "ranking": result["ranking"],
            "candidates": result["candidates"],
            "community_changes": result["community_changes"],
            "baseline_total": result["baseline"]["total"],
        })
    return reports


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def _write_json(path, payload):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def _load_input(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def _run_default(params, community):
    os.makedirs(OUT_DIR, exist_ok=True)
    _write_json(PARAMS_PATH, params)
    reports = build_scenarios(params=params, community=community)
    _write_json(RESULTS_PATH, {
        "schema": SCHEMA,
        "schema_version": SCHEMA_VERSION,
        "generator": GENERATOR,
        "label": params["label"],
        "note": "Illustrative fixtures, NOT effectiveness evidence.",
        "params": params,
        "scenarios": reports,
    })
    for report in reports:
        _write_json(os.path.join(SCENARIO_DIR, report["name"] + ".json"), report)
    return reports


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--input", help="input JSON with deck/candidates/context/community")
    parser.add_argument("--output", help="output JSON path for a single ranking")
    parser.add_argument("--community", choices=["on", "off"], default="on",
                        help="enable/disable the bounded community tie-break prior")
    parser.add_argument("--params", help="optional params JSON path")
    args = parser.parse_args(argv)

    params = _params_from_any(None)
    if args.params:
        params = _load_input(args.params)
    community = args.community == "on"

    if args.input:
        payload = _load_input(args.input)
        deck = payload.get("deck")
        if deck is None:
            raise SystemExit("--input requires a 'deck' list")
        candidates = payload.get("candidates")
        context = payload.get("context")
        if "community" in payload:
            community = bool(payload["community"])
        result = rank_choices(deck, candidates=candidates, context=context,
                              params=params, community=community)
        if args.output:
            _write_json(args.output, result)
        else:
            sys.stdout.write(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n")
        return 0

    reports = _run_default(params, community)
    sys.stdout.write(
        "wrote %s and %s (%d scenarios)\n" % (RESULTS_PATH, PARAMS_PATH, len(reports))
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
