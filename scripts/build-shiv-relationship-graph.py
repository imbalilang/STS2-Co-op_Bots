#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic, standard-library generator for the inspectable shiv relationship graph.

Scope
-----
This script only READS existing owner snapshots and WRITES:
  * work/shiv-relationship-graph/graph.json
  * work/shiv-relationship-graph/validation.json
  * docs/building/SHIV_RELATIONSHIP_GRAPH.md

It performs no network access, no battles, no training and no production
scoring changes. All community numbers are copied verbatim from the snapshots in
work/relationship-audit/; mechanism edges are qualitative, hand-curated rules
whose evidence is the cached card text in work/spire-codex/cards/cards.json.

Design
------
* Standard library only, pure function of its inputs -> byte-stable output.
* Four layers are kept strictly separate and are NEVER collapsed into a single
  numeric strength:
    1. mechanism           (qualitative, directed, certainty-labelled)
    2. community_draft     (directed observed preference, exact numbers)
    3. community_pairings  (undirected co-occurrence, deduplicated)
    4. external_reference  (PANACHE, outside whitelist, excluded from counts)
"""

import hashlib
import json
import os
import sys

# --------------------------------------------------------------------------- #
# Paths and constants
# --------------------------------------------------------------------------- #

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SCRIPT_DIR)

AUDIT_DIR = os.path.join(ROOT, "work", "relationship-audit")
CODEX_CARDS = os.path.join(ROOT, "work", "spire-codex", "cards", "cards.json")
CODEX_DRAFT_DIR = os.path.join(ROOT, "work", "spire-codex", "draft-recs")
COVERAGE_SUMMARY = os.path.join(AUDIT_DIR, "coverage-summary.json")
SOURCE_TREE = os.path.join(AUDIT_DIR, "source-tree.json")
OPENAPI = os.path.join(AUDIT_DIR, "openapi.json")

OUT_DIR = os.path.join(ROOT, "work", "shiv-relationship-graph")
GRAPH_PATH = os.path.join(OUT_DIR, "graph.json")
VALIDATION_PATH = os.path.join(OUT_DIR, "validation.json")
MD_PATH = os.path.join(ROOT, "docs", "building", "SHIV_RELATIONSHIP_GRAPH.md")

SCHEMA = "shiv-relationship-graph"
SCHEMA_VERSION = 1
GENERATOR_REVISION = 1

# Fixed 14 selectable nodes (order is the canonical whitelist order).
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

EXTERNAL_NODES = ["PANACHE"]

# Base deck cards are fixed context only; they are NOT candidate nodes.
BASE_DECK_CONTEXT = [
    "ASCENDERS_BANE",
    "DEFEND_SILENT",
    "NEUTRALIZE",
    "STRIKE_SILENT",
    "SURVIVOR",
]

PRODUCERS = ["BLADE_DANCE", "CLOAK_AND_DAGGER", "LEADING_STRIKE", "INFINITE_BLADES"]
PAYOFFS = ["ACCURACY", "KNIFE_TRAP", "PHANTOM_BLADES", "AFTERIMAGE"]

# Draw / energy enablers feeding component availability (conditional enabling).
ENABLERS = {
    "ACROBATICS": "draw",
    "BACKFLIP": "draw",
    "ADRENALINE": "energy_draw",
}

# Footwork -> normal block cards (no Afterimage triggered-block scaling assumed).
FOOTWORK_BLOCK_TARGETS = ["CLOAK_AND_DAGGER", "BACKFLIP", "LEG_SWEEP", "DEFLECT"]

# Draft snapshot timestamp; pairing timestamps are read independently below.
BUILT_AT = "2026-08-18T18:45:19.635000"

NODE_ROLES = {
    "BLADE_DANCE": ["shiv_producer"],
    "CLOAK_AND_DAGGER": ["shiv_producer", "block"],
    "LEADING_STRIKE": ["shiv_producer", "attack"],
    "INFINITE_BLADES": ["shiv_producer", "power", "per_turn"],
    "ACCURACY": ["shiv_payoff", "per_shiv_damage"],
    "KNIFE_TRAP": ["shiv_payoff", "exhaust_replay"],
    "PHANTOM_BLADES": ["shiv_payoff", "retain", "first_shiv_per_turn"],
    "AFTERIMAGE": ["played_card_payoff", "block_trigger"],
    "BACKFLIP": ["block", "draw"],
    "ACROBATICS": ["draw", "discard"],
    "ADRENALINE": ["energy", "draw"],
    "FOOTWORK": ["dexterity", "block_scaling"],
    "LEG_SWEEP": ["block", "debuff"],
    "DEFLECT": ["block"],
}

COMMUNITY_CAVEATS = [
    "观测型数据，无随机化、无玩家/角色/人数/版本/难度分层。",
    "draft 的 lift 条件化在『已持有 context 牌』，context 取自终局牌组，存在生存者偏差与选择偏差。",
    "offers/picks 不是独立观测数；同一局同一 (context, candidate) 可重复贡献。",
    "pairings 为终局牌组一次性共现，带生存者偏差；co 不是独立局数。",
    "Top-K 截断使 lift<1 / NPMI<0 的负关系基本不可见；未出现的边是未知，不是 0 或负关系。",
    "winrate 混杂选择与生存，不得作为强度或评分直接使用。",
]

LIMITATIONS = [
    "卡面文字与 cost 取自 work/spire-codex/cards/cards.json 缓存快照，未声明为当前本地运行时验证版本。",
    "社区各接口的 built_at 分别保留于观测中；种群可能跨版本/模式/人数混合，时间戳只代表池子生成时刻，不代表样本时间跨度。",
    "draft 层方向为『持有 context -> 被提供 candidate』；这是有向抓取观测，不能当作对称图使用。",
    "缺失的 draft 有向边 (162/182) 与 pairings 无向边 (39/91) 均记为 unknown，不能记作 0 或负关系。",
    "机制层为定性规则，effect_units 各自独立（damage / block / availability），绝不合并为单一数值强度。",
    "KNIFE_TRAP -> AFTERIMAGE 的『重放是否计为打出』未由卡面文字确认，标记为需要 runtime 验证的假设。",
    "本图不是可用于评分的 prior/model，也不从原始 winrate 给出任何评分建议。",
    "PANACHE 仅作外部参考示例，明确在白名单之外，且不进入 14 节点任何计数。",
]


# --------------------------------------------------------------------------- #
# Small helpers
# --------------------------------------------------------------------------- #

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def load_json(path):
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def dump_json(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    text = json.dumps(obj, ensure_ascii=False, indent=2, sort_keys=False)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text + "\n")


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def draft_path(card_id):
    if card_id == "ACCURACY":
        return os.path.join(AUDIT_DIR, "draft-accuracy.json")
    return os.path.join(AUDIT_DIR, "draft-recs-%s.json" % card_id)


def pair_path(card_id):
    if card_id == "ACCURACY":
        return os.path.join(AUDIT_DIR, "pair-accuracy.json")
    return os.path.join(AUDIT_DIR, "pairings-%s.json" % card_id)


def card_desc(cards, card_id):
    c = cards.get(card_id)
    return c.get("description") if c else None


# --------------------------------------------------------------------------- #
# Inputs
# --------------------------------------------------------------------------- #

def build_source_records():
    paths = []
    paths.append(CODEX_CARDS)
    for cid in WHITELIST:
        paths.append(draft_path(cid))
    for cid in WHITELIST:
        paths.append(pair_path(cid))
    paths.append(COVERAGE_SUMMARY)
    paths.append(SOURCE_TREE)
    paths.append(OPENAPI)
    records = []
    seen = set()
    for p in paths:
        rp = rel(p)
        if rp in seen:
            continue
        seen.add(rp)
        rec = {
            "path": rp,
            "exists": os.path.isfile(p),
            "sha256": sha256_file(p) if os.path.isfile(p) else None,
            "url": None,
        }
        records.append(rec)
    return records


def build_provenance(source_tree, openapi_doc):
    return {
        "owner_snapshots": True,
        "captured_by": "owner (Astra)",
        "captured_on": "2026-09-18",
        "live_snapshot_dir": "work/relationship-audit/",
        "cache_dir": "work/spire-codex/draft-recs/",
        "description": (
            "本图只消费 owner 已抓取的快照文件；生成器不发起任何网络请求。"
        ),
        "endpoint_urls_note": (
            "快照内未保存完整请求 URL，故不臆造 URL；仅记录服务与源码树元数据。"
        ),
        "service": {
            "title": openapi_doc.get("info", {}).get("title"),
            "version": openapi_doc.get("info", {}).get("version"),
            "openapi": openapi_doc.get("openapi"),
        },
        "upstream_source_tree": {
            "sha": source_tree.get("sha"),
            "url": source_tree.get("url"),
            "note": "当前公开源码树，只作为代理；不保证等于 2026-08-18 出数时版本。",
        },
        "card_semantics_note": (
            "节点 cost/description 来自缓存 cards.json 快照，未声明为当前本地运行时验证。"
        ),
    }


# --------------------------------------------------------------------------- #
# Nodes
# --------------------------------------------------------------------------- #

def build_nodes(cards, cards_sha):
    nodes = []
    for cid in WHITELIST:
        c = cards.get(cid, {})
        nodes.append({
            "id": cid,
            "name": c.get("name"),
            "cost": c.get("cost"),
            "type": c.get("type"),
            "rarity": c.get("rarity"),
            "color": c.get("color"),
            "keywords": c.get("keywords"),
            "roles": NODE_ROLES.get(cid, []),
            "description": c.get("description"),
            "description_raw": c.get("description_raw"),
            "whitelist_fixed": True,
            "external": False,
            "evidence": {
                "path": rel(CODEX_CARDS),
                "sha256": cards_sha,
                "card_id": cid,
            },
        })
    return nodes


def build_external_nodes(cards, cards_sha):
    out = []
    for cid in EXTERNAL_NODES:
        c = cards.get(cid, {})
        out.append({
            "id": cid,
            "name": c.get("name"),
            "cost": c.get("cost"),
            "type": c.get("type"),
            "rarity": c.get("rarity"),
            "color": c.get("color"),
            "roles": ["external_reference_only"],
            "description": c.get("description"),
            "whitelist_fixed": False,
            "outside_whitelist": True,
            "external": True,
            "reference_only": True,
            "excluded_from_14_counts": True,
            "evidence": {
                "path": rel(CODEX_CARDS),
                "sha256": cards_sha,
                "card_id": cid,
            },
        })
    return out


# --------------------------------------------------------------------------- #
# Layer 1: mechanism
# --------------------------------------------------------------------------- #

def _evidence(cards, source_id, target_id=None):
    ev = [{"card_id": source_id, "description": card_desc(cards, source_id)}]
    if target_id:
        ev.append({"card_id": target_id, "description": card_desc(cards, target_id)})
    return ev


def build_mechanism_layer(cards):
    edges = []

    def add(source, target, relation_type, effect, effect_units,
            applicability, certainty, requires_runtime, notes):
        edges.append({
            "edge_id": "M-%03d" % (len(edges) + 1),
            "source": source,
            "target": target,
            "relation_type": relation_type,
            "effect": effect,
            "effect_units": effect_units,
            "applicability": applicability,
            "evidence": _evidence(cards, source, target),
            "certainty": certainty,
            "requires_runtime_verification": requires_runtime,
            "notes": notes,
        })

    producer_supply = {
        "BLADE_DANCE": "每次打出向手牌加入 3 张 Shiv；缓存 keywords 含 Exhaust，普通牌堆循环不能再次抽到这张生成牌；消耗回收等例外未建模。",
        "CLOAK_AND_DAGGER": "每次打出向手牌加入 1 张 Shiv；可重复供给（recurring）。",
        "LEADING_STRIKE": "每次打出向手牌加入 2 张 Shiv；可重复供给（recurring）。",
        "INFINITE_BLADES": "Power；每回合开始向手牌加入 1 张 Shiv（per_turn recurring，需 Power 在生效）。",
    }

    # Producer -> ACCURACY (per-Shiv damage bonus).
    for p in PRODUCERS:
        add(
            p, "ACCURACY",
            "shiv_generation_feeds_per_shiv_bonus",
            "%s 供给的 Shiv 被实际打出时，每一次打出都吃到 ACCURACY 的『Shivs deal 4 additional damage』，因此 per-Shiv 加成随可打出 Shiv 数增加；ACCURACY 本身不生成 Shiv。" % p,
            "count of per-Shiv damage bonuses (damage units); no aggregate number invented",
            ["ACCURACY 需已打出并生效", "生成出的 Shiv 必须被实际打出"],
            "mechanism_supported",
            False,
            producer_supply[p],
        )

    # Producer -> KNIFE_TRAP (exhaust-pile replay).
    for p in PRODUCERS:
        add(
            p, "KNIFE_TRAP",
            "shiv_generation_adds_to_exhaust_pile_for_replay",
            "%s 生成并被打出的 Shiv 进入 Exhaust Pile，成为 KNIFE_TRAP『Play every Shiv in your Exhaust Pile on the enemy』的可重放池。" % p,
            "exhausted Shiv count available for replay (count units)",
            [
                "只累计已 Exhaust 的 Shiv",
                "不得假设 KNIFE_TRAP 重放的 Shiv 可无限重复积累",
                "重放后 Shiv 的去向未由卡面文字确认",
            ],
            "mechanism_supported",
            False,
            producer_supply[p] + " 区分 once-per-fight 与 recurring：若重放的 Shiv 不再回到 Exhaust Pile，则同一批 Shiv 近似一次性收益；可重复打出的生成牌可在后续回合重新累积，但依赖抽牌与能量。",
        )

    # Producer -> PHANTOM_BLADES (retain + first-shiv-per-turn bonus).
    for p in PRODUCERS:
        add(
            p, "PHANTOM_BLADES",
            "shiv_supply_supports_retain_and_first_shiv_bonus",
            "%s 增加 Shiv 供给与稳定性，使 PHANTOM_BLADES 的『Shivs gain Retain』与『first Shiv you play each turn deals 9 additional damage』更容易满足；+9 是每回合首张 Shiv 触发一次。" % p,
            "first-Shiv-per-turn bonus trigger count (damage units); Retain is a retention property",
            ["+9 每回合仅首张 Shiv 生效，不是每 Shiv 叠加", "Retain 减少跨回合弃牌损失"],
            "mechanism_supported",
            False,
            "不得把 PHANTOM_BLADES 当作 flat per-Shiv 倍率；" + producer_supply[p],
        )

    # Producer -> AFTERIMAGE (played-card block trigger).
    for p in PRODUCERS:
        add(
            p, "AFTERIMAGE",
            "shiv_played_counts_as_played_card",
            "%s 生成的 Shiv 被打出时计为一张打出的牌，触发 AFTERIMAGE 的『每当打出一张牌获得 Block』效果（每张一次触发）。" % p,
            "trigger count of triggered Block (block units); one trigger per played Shiv",
            ["AFTERIMAGE 需已生效", "每次打出 Shiv 触发一次触发型 Block"],
            "mechanism_supported",
            False,
            producer_supply[p],
        )

    # FOOTWORK -> normal block cards (Dexterity scaling).
    for t in FOOTWORK_BLOCK_TARGETS:
        add(
            "FOOTWORK", t,
            "dexterity_scales_normal_block",
            "FOOTWORK 提供的 Dexterity 提高 %s 的普通 Block 数值。" % t,
            "block units per Dexterity (normal Block sensitivity)",
            ["目标牌需提供普通 Block"],
            "mechanism_supported",
            False,
            "普通 Block 敏感性；不假设 Dexterity 按相同方式放大 AFTERIMAGE 的触发型 Block，故不建立 FOOTWORK->AFTERIMAGE。",
        )

    # Draw / energy -> component availability (conditional enabling).
    for e, kind in ENABLERS.items():
        for p in PRODUCERS:
            if kind == "draw":
                aspect = "抽牌"
            else:
                aspect = "能量与抽牌"
            add(
                e, p,
                "enables_component_availability",
                "%s 通过%s提高抽到并打出 %s 等组件的条件可用性；这是使能，不是保证的实测收益。" % (e, aspect, p),
                "availability / consistency (NOT damage or block units)",
                [
                    "抽到的是否为组件取决于牌组构成",
                    "能量是否用于组件取决于当回合选择",
                ],
                "mechanism_supported",
                False,
                "conditional enabling：guaranteed_gain=false；未测收益，不给出权重。",
            )

    # KNIFE_TRAP -> AFTERIMAGE (hypothesis requiring runtime verification).
    add(
        "KNIFE_TRAP", "AFTERIMAGE",
        "replay_may_count_as_played_card",
        "若 KNIFE_TRAP 从 Exhaust Pile 重放的 Shiv 被游戏计为『打出』，则一次重放可为 AFTERIMAGE 多次触发 Block。",
        "play-event count (block units if and only if replay counts as played)",
        ["仅当重放计为 played card", "卡面文字未说明，需 runtime 验证"],
        "heuristic_hypothesis",
        True,
        "未由卡面文字确认；不得据此断言必然多次触发。",
    )

    # Generalized rules (avoid dense cliques; state the rule once).
    generalized_rules = [
        {
            "rule_id": "R-001",
            "name": "every_played_card_triggers_afterimage",
            "statement": "通用规则：每打出一张牌（包括 Shiv）都触发 AFTERIMAGE 的『每当打出一张牌获得 Block』。因此无需为全部 14 节点建立稠密团，只保留 4 张生成牌的 representative 边并在此声明通用规则。",
            "applies_to": "any played card, not only the 14 whitelist nodes",
        },
        {
            "rule_id": "R-002",
            "name": "draw_energy_component_availability",
            "statement": "抽牌/能量经由牌组构成影响任意组件的可用性，属于条件使能；本层只对 4 张生成牌枚举 representative 边。",
            "applies_to": "component availability (conditional)",
        },
        {
            "rule_id": "R-003",
            "name": "any_shiv_producer_feeds_payoffs",
            "statement": "任意 Shiv 生成牌都可作为 ACCURACY / KNIFE_TRAP / PHANTOM_BLADES / AFTERIMAGE 的供给；本层只枚举白名单内 4 张生成牌。",
            "applies_to": "shiv producers",
        },
    ]

    layer_notes = [
        "本层为定性机制关系，effect_units 各自独立，绝不与社区数值或 winrate 合并成单一强度。",
        "不存在『保证收益』或『必选』语义；仅描述触发/使能条件。",
        "不引入任何自造量化权重。",
    ]

    certainty_counts = {}
    for e in edges:
        certainty_counts[e["certainty"]] = certainty_counts.get(e["certainty"], 0) + 1

    return {
        "layer": 1,
        "name": "mechanism",
        "description": "定性机制有向边：relation type + effect + applicability + evidence + certainty。",
        "producers": PRODUCERS,
        "payoffs": PAYOFFS,
        "edges": edges,
        "generalized_rules": generalized_rules,
        "certainty_counts": certainty_counts,
        "notes": layer_notes,
    }


# --------------------------------------------------------------------------- #
# Layer 2: community draft
# --------------------------------------------------------------------------- #

def build_draft_layer(source_sha):
    whitelist = set(WHITELIST)
    edges = []
    for cid in WHITELIST:
        p = draft_path(cid)
        doc = load_json(p)
        sha = source_sha[rel(p)]
        for rec in doc.get("recommends", []):
            target = rec.get("id")
            if target not in whitelist:
                continue
            edges.append({
                "edge_id": None,
                "source": cid,
                "target": target,
                "pref": rec.get("pref"),
                "pref_base": rec.get("pref_base"),
                "lift": rec.get("lift"),
                "offers": rec.get("offers"),
                "picks": rec.get("picks"),
                "winrate": rec.get("winrate"),
                "built_at": doc.get("built_at"),
                "source_file": rel(p),
                "source_sha256": sha,
                "candidate_name": rec.get("name"),
                "candidate_desc": rec.get("desc"),
                "caveats": COMMUNITY_CAVEATS,
            })
    edges.sort(key=lambda e: (e["source"], e["target"]))
    for i, e in enumerate(edges, 1):
        e["edge_id"] = "D-%03d" % i

    present = set((e["source"], e["target"]) for e in edges)
    all_pairs = [(a, b) for a in WHITELIST for b in WHITELIST if a != b]
    missing = [{"source": a, "target": b} for (a, b) in all_pairs if (a, b) not in present]

    return {
        "layer": 2,
        "name": "community_draft",
        "description": (
            "社区 draft 推荐的有向观测边：source = 已持有 context 牌，"
            "target = 被提供并选择的 candidate 牌。方向为抓取方向，不可对称化。"
        ),
        "direction_convention": "source = held context; target = offered candidate (held -> offered)",
        "edges": edges,
        "unknown_directed_pairs": missing,
        "counts": {
            "possible_directed_pairs": len(all_pairs),
            "observed_edges": len(edges),
            "unknown_directed_pairs": len(missing),
        },
        "metadata_note": (
            "快照不包含重复张数/升级信息；本层不含 repeat-copy 或 upgrade 元数据。"
        ),
        "notes": [
            "no threshold and no top-4 filtering：保留全部 20 条白名单内观测边。",
            "自环（source==target）已排除。",
            "未返回的边记入 unknown_directed_pairs，不是 0 也不是负关系。",
            "ACCURACY 使用 draft-accuracy.json；其余使用 draft-recs-<ID>.json。",
        ],
    }


# --------------------------------------------------------------------------- #
# Layer 3: community pairings
# --------------------------------------------------------------------------- #

def build_pairing_layer(source_sha):
    whitelist = set(WHITELIST)
    observed = {}  # frozenset({a,b}) -> {"a":..,"b":..,"observations":[...]}
    for cid in WHITELIST:
        p = pair_path(cid)
        doc = load_json(p)
        sha = source_sha[rel(p)]
        for rec in doc.get("partners", {}).get("cards", []):
            other = rec.get("id")
            if other not in whitelist or other == cid:
                continue
            key = tuple(sorted((cid, other)))
            entry = observed.setdefault(key, {"a": key[0], "b": key[1], "observations": []})
            entry["observations"].append({
                "from": cid,
                "to": other,
                "co": rec.get("co"),
                "conf": rec.get("conf"),
                "conf_rev": rec.get("conf_rev"),
                "npmi": rec.get("npmi"),
                "winrate": rec.get("winrate"),
                "built_at": doc.get("built_at"),
                "source_file": rel(p),
                "source_sha256": sha,
            })

    edges = []
    for key in sorted(observed.keys()):
        entry = observed[key]
        obs = sorted(entry["observations"], key=lambda o: o["from"])
        conflict = False
        details = []
        if len(obs) == 2:
            o1, o2 = obs[0], obs[1]
            # o1 = from a -> b ; o2 = from b -> a
            if o1["co"] != o2["co"]:
                conflict = True
                details.append({"field": "co", "a_side": o1["co"], "b_side": o2["co"]})
            if o1["npmi"] != o2["npmi"]:
                conflict = True
                details.append({"field": "npmi", "a_side": o1["npmi"], "b_side": o2["npmi"]})
            if o1["winrate"] != o2["winrate"]:
                conflict = True
                details.append({"field": "winrate", "a_side": o1["winrate"], "b_side": o2["winrate"]})
            if o1["conf"] != o2["conf_rev"]:
                conflict = True
                details.append({"field": "conf_vs_reverse_conf_rev", "a_side": o1["conf"], "b_side": o2["conf_rev"]})
            if o1["conf_rev"] != o2["conf"]:
                conflict = True
                details.append({"field": "conf_rev_vs_reverse_conf", "a_side": o1["conf_rev"], "b_side": o2["conf"]})
        edge = {
            "edge_id": None,
            "a": entry["a"],
            "b": entry["b"],
            "observations": obs,
            "observation_count": len(obs),
            "conflict": conflict,
            "conflict_details": details if details else None,
            "dedup_rule": "undirected key = sorted(a, b); both directional observations preserved, never averaged or overwritten",
            "notes": [
                "co/npmi/winrate 为跨版本、跨模式、跨人数混合池的终局共现，不得推断为独立局数。",
                "不得跨 capped endpoints 反推补集计数。",
                "同一 run 可贡献多对共现，观测不独立。",
            ],
        }
        edges.append(edge)
    edges.sort(key=lambda e: (e["a"], e["b"]))
    for i, e in enumerate(edges, 1):
        e["edge_id"] = "P-%03d" % i

    present = set(tuple(sorted((e["a"], e["b"]))) for e in edges)
    all_pairs = [tuple(sorted((a, b))) for i, a in enumerate(WHITELIST) for b in WHITELIST[i + 1:]]
    missing = [{"a": a, "b": b} for (a, b) in all_pairs if (a, b) not in present]

    conflicts = [e for e in edges if e["conflict"]]

    return {
        "layer": 3,
        "name": "community_pairings",
        "description": (
            "社区 item_pairings 的无向共现边：从两个端点各自快照去重合并，"
            "保留原始双向观测（conf / conf_rev），反向不一致时记录 conflict。"
        ),
        "edges": edges,
        "unknown_pairs": missing,
        "counts": {
            "possible_undirected_pairs": len(all_pairs),
            "observed_edges": len(edges),
            "unknown_pairs": len(missing),
            "conflicts": len(conflicts),
        },
        "notes": [
            "若反向观测不一致，记录 conflict=true 并保留两侧原始值，不静默覆盖、不取平均。",
            "未返回的配对记入 unknown_pairs，不是 0 或负关系。",
        ],
    }


# --------------------------------------------------------------------------- #
# Layer 4: external reference (PANACHE)
# --------------------------------------------------------------------------- #

def build_external_layer(source_sha):
    edges = []
    pairs = [("ACCURACY", draft_path("ACCURACY")), ("BLADE_DANCE", draft_path("BLADE_DANCE"))]
    for source, p in pairs:
        doc = load_json(p)
        sha = source_sha[rel(p)]
        for rec in doc.get("recommends", []):
            if rec.get("id") != "PANACHE":
                continue
            edges.append({
                "edge_id": "X-%03d" % (len(edges) + 1),
                "source": source,
                "target": "PANACHE",
                "pref": rec.get("pref"),
                "pref_base": rec.get("pref_base"),
                "lift": rec.get("lift"),
                "offers": rec.get("offers"),
                "picks": rec.get("picks"),
                "winrate": rec.get("winrate"),
                "built_at": doc.get("built_at"),
                "source_file": rel(p),
                "source_sha256": sha,
                "outside_whitelist": True,
                "context_only": True,
                "excluded_from_counts": True,
                "notes": "PANACHE 仅为白名单外参考示例，不进入 14 节点任何计数。",
            })
    return {
        "layer": 4,
        "name": "external_reference",
        "description": "白名单外参考示例：仅记录 held ACCURACY / held BLADE_DANCE -> PANACHE（若快照存在）。",
        "edges": edges,
        "counts": {"external_edges": len(edges)},
        "notes": [
            "PANACHE 不扩入白名单。",
            "外部边不混入任何 counts。",
        ],
    }


# --------------------------------------------------------------------------- #
# Examples
# --------------------------------------------------------------------------- #

def build_example_accuracy(mechanism_layer, draft_layer, cards):
    mech_in = [e for e in mechanism_layer["edges"]
               if e["target"] == "ACCURACY" and e["source"] in PRODUCERS]
    draft_received = [e for e in draft_layer["edges"]
                      if e["target"] == "ACCURACY"]  # Accuracy as offered candidate
    draft_outgoing = [e for e in draft_layer["edges"]
                      if e["source"] == "ACCURACY"]  # Accuracy as held context
    return {
        "node": "ACCURACY",
        "mechanism_support": {
            "summary": "ACCURACY 是 per-Shiv 加成牌，机制上由 4 张 Shiv 生成牌供给支撑。",
            "edges": [
                {"edge_id": e["edge_id"], "source": e["source"], "target": e["target"],
                 "relation_type": e["relation_type"], "certainty": e["certainty"]}
                for e in mech_in
            ],
        },
        "community_direction_distinction": {
            "received_as_candidate": {
                "meaning": "其他牌作为 held context 时，ACCURACY 被提供（held other -> offered ACCURACY）。",
                "edges": [
                    {"edge_id": e["edge_id"], "source": e["source"], "target": e["target"],
                     "lift": e["lift"], "offers": e["offers"], "picks": e["picks"]}
                    for e in draft_received
                ],
            },
            "outgoing_as_context": {
                "meaning": "ACCURACY 作为 held context 时，其他牌被提供（held ACCURACY -> offered other）。",
                "edges": [
                    {"edge_id": e["edge_id"], "source": e["source"], "target": e["target"],
                     "lift": e["lift"], "offers": e["offers"], "picks": e["picks"]}
                    for e in draft_outgoing
                ],
            },
            "note": "received 与 outgoing 是不同方向，不能合并为对称关系。",
        },
    }


# --------------------------------------------------------------------------- #
# Markdown
# --------------------------------------------------------------------------- #

def fmt(v):
    if v is None:
        return "-"
    if isinstance(v, float):
        return ("%.4f" % v)
    return str(v)


def mermaid_mechanism(layer):
    lines = ["flowchart LR"]
    producers = set(layer["producers"])
    payoffs = set(layer["payoffs"])
    lines.append("  subgraph P[Shiv producers]")
    for p in layer["producers"]:
        lines.append("    %s" % p)
    lines.append("  end")
    lines.append("  subgraph Y[Shiv payoffs]")
    for y in layer["payoffs"]:
        lines.append("    %s" % y)
    lines.append("  end")
    for e in layer["edges"]:
        arrow = "-->" if e["certainty"] == "mechanism_supported" else "-.->"
        lines.append("  %s %s %s" % (e["source"], arrow, e["target"]))
    lines.append("  classDef hypothesis stroke-dasharray: 5 5;")
    return "\n".join(lines)


def build_markdown(graph, validation):
    n = graph
    md = []
    a = md.append
    a("# 小刀白名单关系图（SHIV_RELATIONSHIP_GRAPH）")
    a("")
    a("> 本文件由 `scripts/build-shiv-relationship-graph.py` 确定性生成（标准库，无网络）。")
    a("> 只读 owner 已抓取的快照；不修改生产评分、不跑战斗/训练。")
    a("")
    a("## 0. 结论与范围")
    a("")
    a("- 固定 14 个可选节点（whitelist），PANACHE 仅为**白名单外参考**，不进入任何计数。")
    a("- 基础牌（%s）为固定上下文，不是候选节点。" % ", ".join(n["base_deck_context"]["cards"]))
    a("- 四层严格分离，**绝不**合并为单一数值强度。missing 一律记为 unknown，不是 0 或负关系。")
    a("- 顶层 `built_at` = `%s` 指选牌接口快照；配对接口时间分别保留于每条观测。卡面语义来自缓存快照，未声明为当前本地运行时验证。" % n["built_at"])
    a("")
    a("## 1. 节点（14 whitelist）")
    a("")
    a("| id | name | cost | type | roles |")
    a("|---|---|---|---|---|")
    for node in n["nodes"]:
        a("| %s | %s | %s | %s | %s |" % (
            node["id"], node["name"], fmt(node["cost"]), node["type"],
            ", ".join(node["roles"])))
    a("")
    a("### 外部参考节点")
    a("")
    for node in n["external_nodes"]:
        a("- `%s` (%s): cost=%s, type=%s — outside_whitelist=true, reference_only=true, excluded_from_14_counts=true"
          % (node["id"], node["name"], fmt(node["cost"]), node["type"]))
    a("")
    a("## 2. 机制层（Layer 1, mechanism）")
    a("")
    a("全定性有向关系；`effect_units` 各自独立（damage / block / availability），不与社区数值合并。")
    a("")
    a("```mermaid")
    a(mermaid_mechanism(n["layers"]["mechanism"]))
    a("```")
    a("")
    a("实线 = `mechanism_supported`；虚线 = `heuristic_hypothesis`（需 runtime 验证）。")
    a("")
    a("### 机制边证据表")
    a("")
    a("| # | source | target | relation_type | effect_units | certainty | runtime? | 证据卡牌 |")
    a("|---|---|---|---|---|---|---|---|")
    for e in n["layers"]["mechanism"]["edges"]:
        ev = ", ".join(x["card_id"] for x in e["evidence"])
        a("| %s | %s | %s | %s | %s | %s | %s | %s |" % (
            e["edge_id"], e["source"], e["target"], e["relation_type"],
            e["effect_units"], e["certainty"], "yes" if e["requires_runtime_verification"] else "no", ev))
    a("")
    a("### 通用规则")
    a("")
    for r in n["layers"]["mechanism"]["generalized_rules"]:
        a("- **%s** (`%s`): %s" % (r["name"], r["rule_id"], r["statement"]))
    a("")
    a("## 3. 社区 draft 层（Layer 2, community_draft）")
    a("")
    a("方向约定：`source = 已持有 context`，`target = 被提供 candidate`（有向抓取方向，不可对称化）。")
    a("")
    a("| # | source(held) | target(offered) | pref | pref_base | lift | offers | picks | winrate | built_at |")
    a("|---|---|---|---|---|---|---|---|---|---|")
    for e in n["layers"]["community_draft"]["edges"]:
        a("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
            e["edge_id"], e["source"], e["target"], fmt(e["pref"]), fmt(e["pref_base"]),
            fmt(e["lift"]), e["offers"], e["picks"], fmt(e["winrate"]), e["built_at"]))
    a("")
    dc = n["layers"]["community_draft"]["counts"]
    a("- 可能的有向对：%d；观测边：%d；unknown：%d。" % (
        dc["possible_directed_pairs"], dc["observed_edges"], dc["unknown_directed_pairs"]))
    a("")
    a("## 4. 社区 pairings 层（Layer 3, community_pairings）")
    a("")
    a("无向去重：`key = sorted(a,b)`；保留两个方向的 `conf`/`conf_rev`，反向不一致记 conflict，不覆盖、不平均。")
    a("")
    a("| # | a | b | co | conf(a→b) | conf(b→a) | npmi | winrate | conflict |")
    a("|---|---|---|---|---|---|---|---|---|")
    for e in n["layers"]["community_pairings"]["edges"]:
        o1 = next((o for o in e["observations"] if o["from"] == e["a"]), None)
        o2 = next((o for o in e["observations"] if o["from"] == e["b"]), None)
        base = o1 or o2
        forward = o1["conf"] if o1 else (o2["conf_rev"] if o2 else None)
        backward = o2["conf"] if o2 else (o1["conf_rev"] if o1 else None)
        co = o1["co"] if o1 else (o2["co"] if o2 else None)
        a("| %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
            e["edge_id"], e["a"], e["b"],
            fmt(co), fmt(forward), fmt(backward),
            fmt(base["npmi"]) if base else "-",
            fmt(base["winrate"]) if base else "-",
            "yes" if e["conflict"] else "no"))
    a("")
    pc = n["layers"]["community_pairings"]["counts"]
    a("- 可能的无向对：%d；去重后观测边：%d；unknown：%d；conflict：%d。" % (
        pc["possible_undirected_pairs"], pc["observed_edges"], pc["unknown_pairs"], pc["conflicts"]))
    a("")
    a("## 5. 外部参考层（Layer 4, external_reference）")
    a("")
    a("| # | source(held) | target | lift | offers | picks | pref | built_at |")
    a("|---|---|---|---|---|---|---|---|")
    for e in n["layers"]["external_reference"]["edges"]:
        a("| %s | %s | %s | %s | %s | %s | %s | %s |" % (
            e["edge_id"], e["source"], e["target"], fmt(e["lift"]),
            e["offers"], e["picks"], fmt(e["pref"]), e["built_at"]))
    a("")
    a("PANACHE 明确在白名单外；外部边不进入 14 节点计数。")
    a("")
    a("## 6. 示例：ACCURACY")
    a("")
    ex = n["examples"]["accuracy"]
    a("机制支撑：")
    for e in ex["mechanism_support"]["edges"]:
        a("- %s -> %s (%s, %s)" % (e["source"], e["target"], e["relation_type"], e["certainty"]))
    a("")
    a("社区方向区分（received vs outgoing，不可合并）：")
    a("")
    a("| 方向 | 含义 | edges |")
    a("|---|---|---|")
    rec = ", ".join("%s(lift=%s)" % (e["source"], fmt(e["lift"])) for e in ex["community_direction_distinction"]["received_as_candidate"]["edges"])
    out = ", ".join("%s(lift=%s)" % (e["target"], fmt(e["lift"])) for e in ex["community_direction_distinction"]["outgoing_as_context"]["edges"])
    a("| received (as candidate) | 他牌 held -> offered ACCURACY | %s |" % rec)
    a("| outgoing (as context) | ACCURACY held -> offered 他牌 | %s |" % out)
    a("")
    a("## 7. 来源与溯源")
    a("")
    a("| path | sha256 (前 16) |")
    a("|---|---|")
    for s in n["sources"]:
        a("| %s | %s |" % (s["path"], (s["sha256"] or "-")[:16]))
    a("")
    a("- owner 快照目录：`work/relationship-audit/`；卡表缓存：`work/spire-codex/cards/cards.json`。")
    a("- 上游源码树 sha：`%s`（当前公开源，仅作代理，不一定等于 8/18 出数版本）。" %
      n["provenance"]["upstream_source_tree"]["sha"])
    a("- 快照未保存完整请求 URL，故不臆造 URL。")
    a("")
    a("## 8. 使用限制")
    a("")
    for x in n["limitations"]:
        a("- %s" % x)
    a("")
    a("## 9. 生成与验证摘要")
    a("")
    a("- generator: `%s`（标准库、确定性，revision=%d）。" % (n["generator"]["script"], n["generator"]["revision"]))
    a("- checks_run: %d；errors: %d。" % (validation["checks_run"], len(validation["errors"])))
    a("- 自检覆盖：节点 ID、方向、计数 20/52、原始数值 roundtrip、去重 conflict 标记、数值范围、机制边无悬空、missing 列表互斥且完整、source SHA。")
    if validation["errors"]:
        a("")
        a("**验证错误：**")
        for err in validation["errors"]:
            a("- %s" % err)
    a("")
    return "\n".join(md) + "\n"


# --------------------------------------------------------------------------- #
# Assemble + validate
# --------------------------------------------------------------------------- #

def main():
    errors = []
    warnings = []
    checks = []

    def check(name, passed, detail=""):
        checks.append({"name": name, "passed": bool(passed), "detail": detail})
        if not passed:
            errors.append("%s: %s" % (name, detail))

    source_records = build_source_records()
    source_sha = {s["path"]: s["sha256"] for s in source_records}

    cards = load_json(CODEX_CARDS)
    cards_by_id = {c["id"]: c for c in cards}
    cards_sha = source_sha[rel(CODEX_CARDS)]

    source_tree = load_json(SOURCE_TREE)
    openapi_doc = load_json(OPENAPI)

    nodes = build_nodes(cards_by_id, cards_sha)
    external_nodes = build_external_nodes(cards_by_id, cards_sha)
    mechanism = build_mechanism_layer(cards_by_id)
    draft = build_draft_layer(source_sha)
    pairings = build_pairing_layer(source_sha)
    external = build_external_layer(source_sha)
    example = build_example_accuracy(mechanism, draft, cards_by_id)

    known_ids = set(WHITELIST)
    all_ids = known_ids | set(EXTERNAL_NODES)

    # --- self checks ------------------------------------------------------ #
    check("node_ids_match_fixed14",
          [n["id"] for n in nodes] == WHITELIST,
          "nodes=%s" % [n["id"] for n in nodes])
    check("node_metadata_present",
          all(n["name"] and n["description"] and n["cost"] is not None for n in nodes),
          "every node has name/description/cost")

    dangling = [e["edge_id"] for e in mechanism["edges"]
                if e["source"] not in known_ids or e["target"] not in known_ids]
    check("no_dangling_mechanism_edges", not dangling, "dangling=%s" % dangling)

    check("draft_edge_count_20",
          draft["counts"]["observed_edges"] == 20,
          "observed=%d" % draft["counts"]["observed_edges"])

    # Directions and raw numeric roundtrip against snapshots.
    dir_ok = True
    roundtrip_ok = True
    for e in draft["edges"]:
        p = draft_path(e["source"])
        doc = load_json(p)
        rec = next((r for r in doc.get("recommends", []) if r.get("id") == e["target"]), None)
        if rec is None:
            dir_ok = False
            errors.append("draft edge %s not found in %s" % (e["edge_id"], rel(p)))
            continue
        for field in ("pref", "pref_base", "lift", "offers", "picks", "winrate"):
            if e[field] != rec.get(field):
                roundtrip_ok = False
                errors.append("draft roundtrip %s.%s: %r != %r" % (e["edge_id"], field, e[field], rec.get(field)))
    check("draft_directions_match_snapshots", dir_ok, "source=held context verified against raw files")
    check("draft_numeric_roundtrip", roundtrip_ok, "pref/pref_base/lift/offers/picks/winrate exact")

    # Bounds.
    bounds_ok = True
    for e in draft["edges"]:
        if not (0.0 <= e["pref"] <= 1.0 and 0.0 <= e["pref_base"] <= 1.0 and e["lift"] >= 0.0):
            bounds_ok = False
            errors.append("draft bound violation %s" % e["edge_id"])
        if not (e["offers"] >= 0 and e["picks"] >= 0 and e["picks"] <= e["offers"]):
            bounds_ok = False
            errors.append("draft count violation %s" % e["edge_id"])
        if not (0.0 <= e["winrate"] <= 1.0):
            bounds_ok = False
            errors.append("draft winrate violation %s" % e["edge_id"])
    for e in pairings["edges"]:
        for o in e["observations"]:
            if not (o["co"] >= 0 and 0.0 <= o["conf"] <= 1.0 and 0.0 <= o["conf_rev"] <= 1.0):
                bounds_ok = False
                errors.append("pairing bound violation %s" % e["edge_id"])
            if not (-1.0 <= o["npmi"] <= 1.0 and 0.0 <= o["winrate"] <= 1.0):
                bounds_ok = False
                errors.append("pairing range violation %s" % e["edge_id"])
    check("numeric_bounds", bounds_ok, "pref/conf in [0,1]; npmi in [-1,1]; picks<=offers")

    check("pairing_edge_count_52",
          pairings["counts"]["observed_edges"] == 52,
          "observed=%d" % pairings["counts"]["observed_edges"])

    # Pairing raw roundtrip + conflict flag correctness.
    pair_roundtrip_ok = True
    for e in pairings["edges"]:
        for o in e["observations"]:
            p = pair_path(o["from"])
            doc = load_json(p)
            rec = next((r for r in doc.get("partners", {}).get("cards", []) if r.get("id") == o["to"]), None)
            if rec is None:
                pair_roundtrip_ok = False
                errors.append("pairing obs %s %s->%s missing in raw" % (e["edge_id"], o["from"], o["to"]))
                continue
            for field in ("co", "conf", "conf_rev", "npmi", "winrate"):
                if o[field] != rec.get(field):
                    pair_roundtrip_ok = False
                    errors.append("pairing roundtrip %s %s.%s: %r != %r" % (e["edge_id"], o["from"], field, o[field], rec.get(field)))
    check("pairing_numeric_roundtrip", pair_roundtrip_ok, "co/conf/conf_rev/npmi/winrate exact")

    conflict_ok = True
    for e in pairings["edges"]:
        if e["observation_count"] != 2:
            continue
        obs = sorted(e["observations"], key=lambda o: o["from"])
        o1, o2 = obs[0], obs[1]
        expect = not (o1["conf"] == o2["conf_rev"] and o1["conf_rev"] == o2["conf"]
                      and o1["co"] == o2["co"] and o1["npmi"] == o2["npmi"]
                      and o1["winrate"] == o2["winrate"])
        if bool(e["conflict"]) != bool(expect):
            conflict_ok = False
            errors.append("pairing conflict flag mismatch %s" % e["edge_id"])
    check("pairing_dedup_conflict_flags", conflict_ok, "conflict computed from reverse observations")

    # Missing lists disjoint + complete.
    def disjoint_complete(possible, missing, observed_keys):
        mkeys = set(missing)
        ok = mkeys.isdisjoint(observed_keys) and (mkeys | observed_keys) == set(possible)
        return ok

    draft_possible = [(a, b) for a in WHITELIST for b in WHITELIST if a != b]
    draft_observed = set((e["source"], e["target"]) for e in draft["edges"])
    draft_missing = set((m["source"], m["target"]) for m in draft["unknown_directed_pairs"])
    draft_dc = disjoint_complete(draft_possible, draft_missing, draft_observed)

    pair_possible = [tuple(sorted((a, b))) for i, a in enumerate(WHITELIST) for b in WHITELIST[i + 1:]]
    pair_observed = set(tuple(sorted((e["a"], e["b"]))) for e in pairings["edges"])
    pair_missing = set(tuple(sorted((m["a"], m["b"]))) for m in pairings["unknown_pairs"])
    pair_dc = disjoint_complete(pair_possible, pair_missing, pair_observed)

    check("missing_lists_disjoint_complete", draft_dc and pair_dc,
          "draft=%s pairing=%s" % (draft_dc, pair_dc))

    # External edges excluded from counts.
    ext_ok = external["counts"]["external_edges"] == 2 and all(
        e["target"] == "PANACHE" and e["excluded_from_counts"] for e in external["edges"])
    check("external_panache_excluded_from_counts", ext_ok,
          "external_edges=%d" % external["counts"]["external_edges"])

    # Source SHA match.
    sha_ok = True
    for s in source_records:
        cur = sha256_file(os.path.join(ROOT, s["path"])) if s["exists"] else None
        if cur != s["sha256"]:
            sha_ok = False
            errors.append("source sha mismatch %s" % s["path"])
    check("source_sha256_matches", sha_ok, "all source hashes recomputed")

    # Cross-check against owner coverage-summary (informational, must agree).
    cov = load_json(COVERAGE_SUMMARY)
    cov_draft = len(cov.get("draft_edges", []))
    cov_pair = len(cov.get("pair_edges", []))
    cov_pair_pairs = set(tuple(sorted((e["source"], e["id"]))) for e in cov.get("pair_edges", []))
    check("coverage_summary_draft_agrees", cov_draft == draft["counts"]["observed_edges"],
          "coverage=%d graph=%d" % (cov_draft, draft["counts"]["observed_edges"]))
    check("coverage_summary_pair_agrees", len(cov_pair_pairs) == pairings["counts"]["observed_edges"],
          "coverage directional=%d dedup=%d graph undirected=%d" % (
              cov_pair, len(cov_pair_pairs), pairings["counts"]["observed_edges"]))

    # Draft metadata no repeat-copy info (assert absence is stated).
    check("draft_metadata_no_repeatcopy_claim",
          "重复" in draft["metadata_note"] or "repeat-copy" in draft["metadata_note"],
          draft["metadata_note"])

    # built_at exact on community layers.
    observations = draft["edges"] + [o for e in pairings["edges"] for o in e["observations"]]
    built_ok = all(o["built_at"] is not None and o["built_at"] == load_json(os.path.join(ROOT, o["source_file"])).get("built_at") for o in observations)
    check("community_built_at_exact", built_ok, "timestamps match each original source independently")

    # Mechanism certainty taxonomy valid.
    valid_cert = {"mechanism_supported", "heuristic_hypothesis"}
    cert_ok = all(e["certainty"] in valid_cert for e in mechanism["edges"])
    check("mechanism_certainty_taxonomy", cert_ok, "certainty in %s" % sorted(valid_cert))

    # Determinism: rebuilding every layer must produce identical JSON.
    def _stable(obj):
        return json.dumps(obj, ensure_ascii=False, sort_keys=False)

    determinism_ok = (
        _stable(build_mechanism_layer(cards_by_id)) == _stable(mechanism)
        and _stable(build_draft_layer(source_sha)) == _stable(draft)
        and _stable(build_pairing_layer(source_sha)) == _stable(pairings)
        and _stable(build_external_layer(source_sha)) == _stable(external)
    )
    check("deterministic_rebuild", determinism_ok, "second build of each layer byte-identical")

    # --- assemble graph --------------------------------------------------- #
    graph = {
        "schema": SCHEMA,
        "schema_version": SCHEMA_VERSION,
        "generator": {
            "script": "scripts/build-shiv-relationship-graph.py",
            "std_lib_only": True,
            "deterministic": True,
            "revision": GENERATOR_REVISION,
        },
        "built_at": BUILT_AT,
        "built_at_note": "选牌接口快照时间；不是图生成时间。配对接口快照时间分别见各 observation.built_at；无时区后缀。",
        "scope": {
            "whitelist_fixed_nodes": len(WHITELIST),
            "external_reference_nodes": len(EXTERNAL_NODES),
            "base_deck_context_cards": len(BASE_DECK_CONTEXT),
            "layers_separate": True,
            "no_combined_numeric_strength": True,
        },
        "base_deck_context": {
            "cards": BASE_DECK_CONTEXT,
            "note": "基础牌仅作固定上下文，不是候选节点。",
        },
        "nodes": nodes,
        "external_nodes": external_nodes,
        "layers": {
            "mechanism": mechanism,
            "community_draft": draft,
            "community_pairings": pairings,
            "external_reference": external,
        },
        "sources": source_records,
        "provenance": build_provenance(source_tree, openapi_doc),
        "limitations": LIMITATIONS,
        "examples": {"accuracy": example},
        "counts": {
            "whitelist_nodes": len(WHITELIST),
            "external_nodes": len(EXTERNAL_NODES),
            "base_deck_context_cards": len(BASE_DECK_CONTEXT),
            "mechanism_edges": len(mechanism["edges"]),
            "mechanism_supported": mechanism["certainty_counts"].get("mechanism_supported", 0),
            "heuristic_hypothesis": mechanism["certainty_counts"].get("heuristic_hypothesis", 0),
            "possible_directed_pairs": draft["counts"]["possible_directed_pairs"],
            "community_draft_edges": draft["counts"]["observed_edges"],
            "community_draft_unknown_pairs": draft["counts"]["unknown_directed_pairs"],
            "possible_undirected_pairs": pairings["counts"]["possible_undirected_pairs"],
            "community_pairing_edges": pairings["counts"]["observed_edges"],
            "community_pairing_unknown_pairs": pairings["counts"]["unknown_pairs"],
            "community_pairing_conflicts": pairings["counts"]["conflicts"],
            "external_reference_edges": external["counts"]["external_edges"],
        },
    }

    # --- write graph, then roundtrip-read it ------------------------------ #
    dump_json(GRAPH_PATH, graph)
    graph_readback = load_json(GRAPH_PATH)

    # raw numeric roundtrip through the serialized graph file.
    file_roundtrip_ok = True
    for e in graph_readback["layers"]["community_draft"]["edges"]:
        p = draft_path(e["source"])
        doc = load_json(p)
        rec = next((r for r in doc.get("recommends", []) if r.get("id") == e["target"]), None)
        for field in ("pref", "pref_base", "lift", "offers", "picks", "winrate"):
            if rec is None or e[field] != rec.get(field):
                file_roundtrip_ok = False
                errors.append("file roundtrip draft %s.%s" % (e["edge_id"], field))
    check("file_json_roundtrip_draft", file_roundtrip_ok, "serialized graph matches raw numbers")

    # --- validation ------------------------------------------------------- #
    validation = {
        "schema": "shiv-relationship-graph-validation",
        "schema_version": SCHEMA_VERSION,
        "graph_path": rel(GRAPH_PATH),
        "generator": graph["generator"],
        "built_at": BUILT_AT,
        "checks_run": len(checks),
        "checks": checks,
        "errors": errors,
        "warnings": warnings,
        "counts": graph["counts"],
        "limitations": LIMITATIONS,
        "verification_summary": {
            "nodes": "IDs verified against fixed 14 whitelist",
            "directions": "draft source=held context / target=offered candidate verified against raw snapshots",
            "counts": "draft 20, pairing 52 computed from actual snapshots and cross-checked with coverage-summary",
            "roundtrip": "pref/pref_base/lift/offers/picks/winrate and co/conf/conf_rev/npmi/winrate exact",
            "conflicts": "reverse-observation disagreement computed and flagged, never averaged",
            "missing": "unknown lists disjoint from observed and complete over possible pairs",
            "mechanism": "no dangling edges; certainty taxonomy valid",
            "sources": "sha256 recomputed for every source file",
        },
    }
    dump_json(VALIDATION_PATH, validation)

    # --- markdown --------------------------------------------------------- #
    md = build_markdown(graph_readback, validation)
    os.makedirs(os.path.dirname(MD_PATH), exist_ok=True)
    with open(MD_PATH, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(md)

    # --- console summary -------------------------------------------------- #
    print("shiv-relationship-graph: generated")
    print("  graph      : %s" % rel(GRAPH_PATH))
    print("  validation : %s" % rel(VALIDATION_PATH))
    print("  markdown   : %s" % rel(MD_PATH))
    print("  checks_run : %d" % len(checks))
    print("  errors     : %d" % len(errors))
    for err in errors:
        print("  ERROR: %s" % err)
    print("  counts     : mechanism=%d (supported=%d, hypothesis=%d), draft=%d, unknown=%d, pairing=%d, unknown=%d, external=%d" % (
        graph["counts"]["mechanism_edges"],
        graph["counts"]["mechanism_supported"],
        graph["counts"]["heuristic_hypothesis"],
        graph["counts"]["community_draft_edges"],
        graph["counts"]["community_draft_unknown_pairs"],
        graph["counts"]["community_pairing_edges"],
        graph["counts"]["community_pairing_unknown_pairs"],
        graph["counts"]["external_reference_edges"],
    ))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
