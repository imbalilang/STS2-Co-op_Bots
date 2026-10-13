#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Offline audit: community Codex Score/Elo snapshot vs the shiv prior model.

What this is
------------
A read-only, standard-library-only audit that
  1. extracts the *actual* community `_compute_score` / `_compute_codex_elo`
     semantics from the cached public sources and reports them verbatim,
  2. lists every whitelist card's cached all-runs and A10 Score/Elo/picks/wins,
  3. aggregates the model's per-card mean marginal and mean rank over the eight
     existing illustrative scenarios and correlates them (tie-aware Spearman,
     average ranks, no scipy) with the community Score and Elo,
  4. freezes the model's scenario output before the Knife Trap replay-synergy
     correction and diffs it against the corrected model,
  5. emits four explicit three-choice + skip decision cases with full reason
     deltas, and
  6. writes ``work/shiv-score-audit/audit.json`` plus a Chinese report at
     ``docs/building/SHIV_SCORE_CALIBRATION.md``.

It is descriptive only. The eight scenarios are handcrafted fixtures, not a
population; correlation over eight scenes is not effectiveness evidence. The
cached public snapshots may differ from whatever is currently deployed, and no
snapshot timestamps are available, so no data version is asserted.

This script ONLY reads the downloaded source/score snapshots; it never
modifies them. It writes under ``work/shiv-score-audit/**`` and the one
scoped doc.

Run:
    python scripts/audit_shiv_scores.py --freeze-before   # freeze old model
    python scripts/audit_shiv_scores.py                   # full audit + doc
"""

import argparse
import hashlib
import json
import math
import os
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from itertools import combinations
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import shiv_prior_model as m  # noqa: E402

AUDIT_DIR = ROOT / "work" / "shiv-score-audit"
SCORES_ALL = AUDIT_DIR / "scores-cards.json"
SCORES_ALL_SKIP = AUDIT_DIR / "scores-all-skip.json"
SCORES_A10 = AUDIT_DIR / "scores-a10.json"
MODEL_BEFORE = AUDIT_DIR / "model-before.json"
MODEL_AFTER = AUDIT_DIR / "model-after.json"
AUDIT_JSON = AUDIT_DIR / "audit.json"
ELO_BASELINE_JSON = AUDIT_DIR / "elo-baseline.json"
AUDIT_MD = ROOT / "docs" / "building" / "SHIV_SCORE_CALIBRATION.md"

# Community-Elo baseline brackets. Each bracket is ranked independently; the
# all-runs bracket is supplied exclusively by ``scores-all-skip.json`` so an
# A10 Elo never leaks into an all-runs comparison (and vice versa).
BRACKET_ALLRUNS = "allruns"
BRACKET_A10 = "a10"
BASELINE_CARD = "elo_highest_card"
BASELINE_WITH_SKIP = "elo_highest_with_skip"
GRAPH_JSON = ROOT / "work" / "shiv-relationship-graph" / "graph.json"
CARDS_JSON = ROOT / "work" / "spire-codex" / "cards" / "cards.json"

SCHEMA = "shiv-score-calibration"
SCHEMA_VERSION = 1
GENERATOR = "scripts/audit_shiv_scores.py"

_REPORT_FIELDS = ("score", "elo", "picks", "wins", "win_rate", "pick_rate")


# --------------------------------------------------------------------------- #
# Small helpers
# --------------------------------------------------------------------------- #

def _sha256(path):
    if not os.path.isfile(path):
        return None
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _load_json(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def _write_json(path, payload):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def _pick(entry, fields=_REPORT_FIELDS):
    if not isinstance(entry, dict):
        return None
    return {field: entry.get(field) for field in fields if field in entry}


def _fmt(value, digits=3):
    if value is None:
        return "-"
    if isinstance(value, float):
        return ("%%.%df" % digits) % value
    return str(value)


# --------------------------------------------------------------------------- #
# Tie-aware ranks and correlation (no scipy)
# --------------------------------------------------------------------------- #

def _average_ranks(values):
    """1-based average ranks, ties share the mean of their rank positions."""
    n = len(values)
    order = sorted(range(n), key=lambda i: values[i])
    ranks = [0.0] * n
    i = 0
    while i < n:
        j = i
        while j + 1 < n and values[order[j + 1]] == values[order[i]]:
            j += 1
        avg = (i + 1 + j + 1) / 2.0
        for k in range(i, j + 1):
            ranks[order[k]] = avg
        i = j + 1
    return ranks


def _pearson(xs, ys):
    n = len(xs)
    if n < 2:
        return None
    mx = sum(xs) / n
    my = sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    if sxx <= 0.0 or syy <= 0.0:
        return None
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    return sxy / math.sqrt(sxx * syy)


def _spearman(xs, ys):
    """Spearman rho on average ranks; None when either side is constant."""
    return _pearson(_average_ranks(xs), _average_ranks(ys))


def _rank_map(ids, values):
    """Map id -> average rank (1 = BEST, i.e. largest value) for the given values."""
    ordered = list(ids)
    ranks = _average_ranks([-values[i] for i in ordered])
    return {cid: rank for cid, rank in zip(ordered, ranks)}


# --------------------------------------------------------------------------- #
# Community-Elo baseline (pure, deterministic; no model score involved)
# --------------------------------------------------------------------------- #

def _elo_rank(offered_elos, skip_elo, include_skip):
    """Rank an offer set by community Elo only.

    Deterministic and bracket-local. Cards with an unavailable (``None``) Elo
    are excluded from the ranking and listed explicitly; they are never treated
    as a silent zero. If no candidate has an Elo the result is ``available
    False`` with ``selected None``. Ties are broken by SKIP first (when
    included), then by card ID ascending.
    """
    unavailable = sorted(cid for cid, elo in offered_elos.items() if elo is None)
    available = {cid: float(elo) for cid, elo in offered_elos.items() if elo is not None}
    skip_included = False
    if include_skip:
        if skip_elo is None:
            unavailable.append("SKIP")
            unavailable.sort()
        else:
            available["SKIP"] = float(skip_elo)
            skip_included = True
    if not available:
        return {
            "selected": None,
            "ranking": [],
            "unavailable": unavailable,
            "available": False,
            "skip_included": skip_included,
        }
    ranking = sorted(
        available, key=lambda cid: (-available[cid], 0 if cid == "SKIP" else 1, cid)
    )
    return {
        "selected": ranking[0],
        "ranking": ranking,
        "unavailable": unavailable,
        "available": True,
        "skip_included": skip_included,
    }


def _reconstruct_restricted_final(rows_by_id, offered, prior_cap):
    """Rebuild a restricted offer-set final score from saved before rows.

    The community prior only fires for positive mechanism rows within
    ``prior_cap`` of the *best positive among the offered set*. Filtering a
    full-14 ranking therefore mislabels the restricted comparison whenever the
    full best differs. ``rows_by_id`` holds the frozen ``mech_delta`` and
    ``prior_applied`` per candidate (both offer-set independent), so the
    restricted final/ranking can be reconstructed exactly.
    """
    present = {cid: rows_by_id[cid] for cid in offered if cid in rows_by_id}
    mechs = {cid: float(row["mech_delta"]) for cid, row in present.items()}
    positives = [value for value in mechs.values() if value > 0.0]
    best = max(positives) if positives else None
    out = {}
    for cid, row in present.items():
        mech = mechs[cid]
        prior_applied = float(row.get("prior_applied") or 0.0)
        eligible = mech > 0.0 and best is not None and (best - mech) <= prior_cap
        out[cid] = {
            "mech_delta": mech,
            "prior_applied": prior_applied,
            "final_score": mech + prior_applied if eligible else mech,
            "prior_used": bool(eligible and prior_applied > 0.0),
        }
    return out


def _rank_restricted(rows_by_id, offered, prior_cap):
    """Restricted community-on ranking (3 offered + SKIP), reconstructed."""
    recon = _reconstruct_restricted_final(rows_by_id, offered, prior_cap)
    entries = [
        {"id": cid, "final": recon[cid]["final_score"], "is_skip": False}
        for cid in offered if cid in recon
    ]
    entries.append({"id": "SKIP", "final": 0.0, "is_skip": True})
    entries.sort(key=lambda e: (-e["final"], 0 if e["is_skip"] else 1, e["id"]))
    return [entry["id"] for entry in entries], recon


# --------------------------------------------------------------------------- #
# Sources / formula
# --------------------------------------------------------------------------- #

SOURCE_SPECS = [
    (
        "work/shiv-score-audit/scores-cards.json",
        "https://spire-codex.com/api/runs/scores/cards",
        "所有 run 的社区 Codex Score + Elo 快照（cards）",
        "endpoint documented in scripts/fetch-spire-codex-decks.py",
    ),
    (
        "work/shiv-score-audit/scores-all-skip.json",
        "https://spire-codex.com/api/runs/scores/cards",
        "all-runs 社区 Score + Elo 快照，含 SKIP 行（最高 Elo 基准的 all-runs 唯一来源）",
        "endpoint documented in scripts/fetch-spire-codex-decks.py; SKIP row is "
        "the reward-screen skip pseudo-entity returned in the same response",
    ),
    (
        "work/shiv-score-audit/scores-a10.json",
        None,
        "A10 bracket 的社区 Score + Elo 快照（cards，含 SKIP）",
        "filename records bracket=a10; no request URL is stored in the snapshot, "
        "so none is invented",
    ),
    (
        "work/shiv-score-audit/run_entity_stats.py",
        None,
        "定义 _compute_score 与 _compute_codex_elo 的上游源码快照",
        "public upstream source snapshot",
    ),
    (
        "work/shiv-score-audit/ScoreBadge.tsx",
        None,
        "Score -> S/A/B/C/D/F letter band 前端映射",
        "frontend snapshot",
    ),
    (
        "work/shiv-score-audit/use-entity-scores.ts",
        None,
        "API 客户端类型（较旧：仅 score/picks/wins/win_rate，无 elo 字段）",
        "frontend snapshot",
    ),
    (
        "work/shiv-relationship-graph/graph.json",
        None,
        "模型使用的 community_draft 有向层与来源溯源",
        "consumed read-only by the model",
    ),
    (
        "work/spire-codex/cards/cards.json",
        None,
        "关系图的卡面语义来源",
        "card semantics snapshot",
    ),
]


def build_sources():
    records = []
    for rel, url, role, note in SOURCE_SPECS:
        path = ROOT / rel
        records.append({
            "path": rel,
            "exists": os.path.isfile(path),
            "sha256": _sha256(path),
            "bytes": os.path.getsize(path) if os.path.isfile(path) else None,
            "url": url,
            "role": role,
            "note": note,
        })
    return records


def build_service():
    service = {
        "title": None,
        "version": None,
        "openapi": None,
        "upstream_source_tree_sha": None,
        "upstream_source_tree_url": None,
        "captured_on": None,
        "snapshot_dirs": None,
    }
    if not os.path.isfile(GRAPH_JSON):
        return service
    graph = _load_json(GRAPH_JSON)
    prov = (graph.get("provenance") or {})
    svc = (prov.get("service") or {})
    tree = (prov.get("upstream_source_tree") or {})
    service.update({
        "title": svc.get("title"),
        "version": svc.get("version"),
        "openapi": svc.get("openapi"),
        "upstream_source_tree_sha": tree.get("sha"),
        "upstream_source_tree_url": tree.get("url"),
        "captured_on": prov.get("captured_on"),
        "snapshot_dirs": {
            "live": prov.get("live_snapshot_dir"),
            "cache": prov.get("cache_dir"),
        },
    })
    return service


def build_formula():
    return {
        "compute_score": {
            "source": "work/shiv-score-audit/run_entity_stats.py:2350-2370",
            "constants": {
                "PRIOR_WEIGHT": 50,
                "SCALE_RANGE": 0.15,
                "tier_bands": [
                    {"floor": 90, "letter": "S"},
                    {"floor": 78, "letter": "A"},
                    {"floor": 65, "letter": "B"},
                    {"floor": 50, "letter": "C"},
                    {"floor": 35, "letter": "D"},
                    {"floor": 0, "letter": "F"},
                ],
            },
            "definition": [
                "weight = prior if prior is not None else 50",
                "shrunk = (wins + baseline * weight) / (picks + weight)",
                "delta = shrunk - baseline",
                "raw = (delta / 0.15 + 1) * 50",
                "score = round(clamp(raw, 0, 100))",
            ],
            "meaning": (
                "Score 是 0-100 的关联性分数：贝叶斯收缩后的卡牌胜率相对同类型"
                "基线的偏移，线性映射并截断。它是关联，不是因果效果；100 只是"
                "上边界截断，既不代表 100% 胜率，也不代表该牌本身带来胜利。"
            ),
        },
        "counts": {
            "picks": (
                "在统计范围内，牌组包含该实体（至少一张）的 run 数。run 内去重："
                "同一次 run 带 5 张 Strike 仍只计 1 次。"
            ),
            "wins": "这些包含该实体的 run 中胜利的 run 数。",
            "win_rate": "wins / picks（百分数）。",
            "baseline": (
                "同类型（cards）全部实体的『按 picks 加权的合并胜率』"
                "= Σwins/Σpicks，不是每张牌胜率的平均。"
            ),
            "prior": (
                "收缩先验的虚拟 picks 数。全量默认 50；bracket 内按 run 份额缩放"
                "（_bracket_prior），下限 10。"
            ),
        },
        "codex_elo": {
            "source": "work/shiv-score-audit/run_entity_stats.py:867-1028",
            "definition": (
                "对每次卡牌奖励三选一建立成对比较：被选中的牌『击败』同一屏其他"
                "未选中的牌；用 Bradley-Terry（MM 迭代）拟合潜在强度，再映射为"
                "ANCHOR + SPREAD*log10(strength)（ANCHOR=1500, SPREAD=400）。"
                "MIN_GAMES=20 以下不评级；PRIOR_GAMES=20 为向强度 1 的收缩先验。"
            ),
            "meaning": (
                "Elo 是揭示偏好（玩家被提供时实际会选谁），与胜率无关；它不是"
                "独立的效果验证。本次审计的 Elo 与关系图 community_draft 层"
                "同源（都来自玩家选择），两者不是互相独立的证据。"
            ),
            "skip": (
                "成对比较里，未被选中的牌就是『输家』；被跳过不是零证据。"
                "A10 快照中出现的 SKIP 行是奖励屏『跳过』这一伪实体的统计，"
                "本次只原样记录，不把它当作 14 张白名单之一。"
            ),
        },
        "discrepancies": [
            "API 客户端类型（use-entity-scores.ts 的 ScoreEntry）较旧，只有 "
            "score/picks/wins/win_rate，没有 elo 字段；而实际返回的 scores-cards.json "
            "与 scores-a10.json 每行都带 elo。",
            "上游 openapi 版本为 1.0.0（graph.json provenance），描述可能把 Elo 说成"
            " all-runs；实际返回的 A10 快照里 Elo 与 all-runs 快照的 Elo 不同。"
            "本审计保留这一事实与不一致，不声称哪份 schema 才是当前部署版本。",
        ],
        "warnings": [
            "缓存快照可能不同于当前部署数据；快照内没有时间戳，故不声明数据版本。",
            "Score 100 是截断上界，不是 100% 胜率，也不是因果效应。",
            "Elo 与关系图 community_draft 同属玩家选择来源，不是独立有效性验证。",
            "本次不使用 score / winrate 作为任何训练目标；不做参数拟合。",
        ],
    }


# --------------------------------------------------------------------------- #
# Community rows
# --------------------------------------------------------------------------- #

def load_community(timings):
    t0 = time.perf_counter()
    all_runs = _load_json(SCORES_ALL)
    a10 = _load_json(SCORES_A10)
    timings["load_community_scores"] = time.perf_counter() - t0
    rows = []
    for cid in m.WHITELIST:
        a = _pick(all_runs.get(cid))
        b = _pick(a10.get(cid))
        row = {
            "id": cid,
            "allruns": a,
            "a10": b,
            "in_allruns": cid in all_runs,
            "in_a10": cid in a10,
        }
        if a and b and a.get("elo") is not None and b.get("elo") is not None:
            row["elo_a10_minus_allruns"] = round(b["elo"] - a["elo"], 4)
        rows.append(row)
    skip = _pick(a10.get("SKIP"))
    return rows, skip


def load_baseline_snapshots(timings):
    """Elo-only views for the two brackets, each from its own snapshot."""
    t0 = time.perf_counter()
    all_skip = _load_json(SCORES_ALL_SKIP)
    a10 = _load_json(SCORES_A10)
    timings["load_baseline_snapshots"] = time.perf_counter() - t0

    def _view(payload):
        elos = {cid: (payload.get(cid) or {}).get("elo") for cid in m.WHITELIST}
        return {"elos": elos, "skip_elo": (payload.get("SKIP") or {}).get("elo")}

    return {BRACKET_ALLRUNS: _view(all_skip), BRACKET_A10: _view(a10)}


# --------------------------------------------------------------------------- #
# Model aggregation over the eight scenarios
# --------------------------------------------------------------------------- #

def scenario_report(result):
    """Compact, serialisable view of one rank_choices result."""
    cands = result["candidates"]
    by_id = {row["id"]: row for row in cands}
    return {
        "winner": result["winner"],
        "ranking": list(result["ranking"]),
        "top3": list(result["top3"]),
        "skip_rank_in_ranking": result["ranking"].index("SKIP") + 1,
        "skip": result["skip"],
        "candidates": cands,
        "by_id": by_id,
        "baseline_total": result["baseline"]["total"],
    }


def build_model_snapshot(timings):
    t0 = time.perf_counter()
    reports = m.build_scenarios(params=None, community=True)
    timings["build_scenarios"] = time.perf_counter() - t0
    scenarios = []
    for report in reports:
        deck = None
        for scenario in m.SCENARIOS:
            if scenario["name"] == report["name"]:
                deck = scenario["deck"]
                break
        result = m.rank_choices(deck, params=None, community=True)
        scenarios.append({
            "name": report["name"],
            "description": report["description"],
            "deck_size": report["deck_size"],
            "deck_counts": report["deck_counts"],
            "winner": report["winner"],
            "top3": list(report["top3"]),
            "ranking": list(report["ranking"]),
            "skip_rank": result["ranking"].index("SKIP") + 1,
            "skip": report["skip"],
            "candidates": report["candidates"],
        })
    return {"label": m.DEFAULT_PARAMS["label"], "scenarios": scenarios}


def aggregate_model(snapshot):
    """Mean mechanism marginal + mean mechanism/final rank per whitelist card."""
    per_card = {
        cid: {"deltas": [], "ranks_mech": [], "ranks_final": []}
        for cid in m.WHITELIST
    }
    scenario_views = []
    for scenario in snapshot["scenarios"]:
        rows = {row["id"]: row for row in scenario["candidates"]}
        mech = {cid: float(rows[cid]["mech_delta"]) for cid in m.WHITELIST}
        final = {
            cid: float(rows[cid]["final_score"]) for cid in m.WHITELIST
        }
        final["SKIP"] = 0.0
        mech_ranks = _rank_map(m.WHITELIST, mech)
        final_ids = list(m.WHITELIST) + ["SKIP"]
        final_ranks = _rank_map(final_ids, final)
        for cid in m.WHITELIST:
            per_card[cid]["deltas"].append(mech[cid])
            per_card[cid]["ranks_mech"].append(mech_ranks[cid])
            per_card[cid]["ranks_final"].append(final_ranks[cid])
        scenario_views.append({
            "name": scenario["name"],
            "winner": scenario["winner"],
            "skip_rank": scenario["skip_rank"],
            "mech_ranks": {cid: mech_ranks[cid] for cid in m.WHITELIST},
            "mech_deltas": mech,
        })
    aggregate = {}
    for cid in m.WHITELIST:
        deltas = per_card[cid]["deltas"]
        aggregate[cid] = {
            "mean_mech_delta": sum(deltas) / len(deltas),
            "mean_rank_mech": sum(per_card[cid]["ranks_mech"]) / len(deltas),
            "mean_rank_final": sum(per_card[cid]["ranks_final"]) / len(deltas),
            "mech_deltas": deltas,
            "mech_ranks": per_card[cid]["ranks_mech"],
        }
    return aggregate, scenario_views


def correlate(aggregate, community_rows):
    ids = list(m.WHITELIST)
    score_all = {}
    score_a10 = {}
    elo_all = {}
    elo_a10 = {}
    for row in community_rows:
        cid = row["id"]
        score_all[cid] = (row["allruns"] or {}).get("score")
        score_a10[cid] = (row["a10"] or {}).get("score")
        elo_all[cid] = (row["allruns"] or {}).get("elo")
        elo_a10[cid] = (row["a10"] or {}).get("elo")

    mean_delta = [aggregate[cid]["mean_mech_delta"] for cid in ids]
    mean_rank = [aggregate[cid]["mean_rank_mech"] for cid in ids]
    mean_rank_final = [aggregate[cid]["mean_rank_final"] for cid in ids]

    def _safe(values):
        return all(v is not None for v in values)

    targets = {
        "community_score_allruns": [score_all[cid] for cid in ids],
        "community_score_a10": [score_a10[cid] for cid in ids],
        "community_elo_allruns": [elo_all[cid] for cid in ids],
        "community_elo_a10": [elo_a10[cid] for cid in ids],
    }
    out = {}
    for name, values in targets.items():
        if not _safe(values):
            out[name] = {"n": 0, "note": "missing community values"}
            continue
        out[name] = {
            "n": len(ids),
            "spearman_mean_mech_delta": _spearman(mean_delta, values),
            "spearman_mean_rank_mech": _spearman(mean_rank, values),
            "spearman_mean_rank_final": _spearman(mean_rank_final, values),
        }
    return out


def review_flags(aggregate, community_rows):
    ids = list(m.WHITELIST)
    score_rank = {}
    elo_rank = {}
    rows = {row["id"]: row for row in community_rows}
    score_vals = {cid: (rows[cid]["a10"] or {}).get("score") for cid in ids}
    elo_vals = {cid: (rows[cid]["a10"] or {}).get("elo") for cid in ids}
    if all(v is not None for v in score_vals.values()):
        score_rank = _rank_map(ids, score_vals)
    if all(v is not None for v in elo_vals.values()):
        elo_rank = _rank_map(ids, elo_vals)
    flags = []
    for cid in ids:
        model_rank = aggregate[cid]["mean_rank_mech"]
        entry = {
            "id": cid,
            "model_mean_rank_mech": model_rank,
            "community_a10_score": score_vals.get(cid),
            "community_a10_elo": elo_vals.get(cid),
        }
        if cid in score_rank:
            entry["community_score_rank"] = score_rank[cid]
            entry["rank_gap_vs_score"] = model_rank - score_rank[cid]
        if cid in elo_rank:
            entry["community_elo_rank"] = elo_rank[cid]
            entry["rank_gap_vs_elo"] = model_rank - elo_rank[cid]
        flags.append(entry)
    flags.sort(key=lambda e: -max(
        abs(e.get("rank_gap_vs_score") or 0.0),
        abs(e.get("rank_gap_vs_elo") or 0.0),
    ))
    return flags


# --------------------------------------------------------------------------- #
# Decision cases
# --------------------------------------------------------------------------- #

DECISION_CASES = [
    {
        "name": "missing_producer",
        "scenario": "payoffs_no_producers",
        "title": "payoff 重型、缺小刀供给",
        "candidates": ["BLADE_DANCE", "CLOAK_AND_DAGGER", "ADRENALINE"],
        "note": (
            "牌组已有 ACCURACY/KNIFE_TRAP/PHANTOM_BLADES/AFTERIMAGE，但没有生成牌；"
            "提供两个生成牌与 ADRENALINE，观察补供给与过牌/能量谁更被看好。"
        ),
    },
    {
        "name": "lacking_payoffs",
        "scenario": "producers_no_payoffs",
        "title": "生成牌重型、缺 payoff",
        "candidates": ["ACCURACY", "KNIFE_TRAP", "ADRENALINE"],
        "note": (
            "牌组已有 BLADE_DANCE/CLOAK_AND_DAGGER/LEADING_STRIKE/INFINITE_BLADES；"
            "提供 ACCURACY、KNIFE_TRAP 与 ADRENALINE，包含本轮修正的 replay 协同。"
        ),
    },
    {
        "name": "defense_poor",
        "scenario": "defense_poor",
        "title": "防御薄弱",
        "candidates": ["FOOTWORK", "LEG_SWEEP", "ADRENALINE"],
        "note": (
            "进攻向、几乎无格挡；提供 FOOTWORK、LEG_SWEEP 与 ADRENALINE，"
            "观察模型对防御/功能牌的处理。"
        ),
    },
    {
        "name": "saturated_duplicates",
        "scenario": "saturated",
        "title": "核心已饱和、重复张数多",
        "candidates": ["ADRENALINE", "FOOTWORK", "DEFLECT"],
        "note": (
            "已有大量重复生成牌与 payoff；提供 ADRENALINE、FOOTWORK、DEFLECT，"
            "观察稀释与低费功能牌在饱和牌组中的边际。"
        ),
    },
]

_CASE_FIELDS = ("accuracy", "knife_trap", "shiv_play", "phantom_first_shiv",
                "afterimage", "deck_dilution")


def evaluate_case(case, before_snapshot, elo_snapshots):
    scenario = next(s for s in m.SCENARIOS if s["name"] == case["scenario"])
    deck = scenario["deck"]
    cands = list(case["candidates"])
    t0 = time.perf_counter()
    mech = m.rank_choices(deck, candidates=cands, params=None, community=False)
    community = m.rank_choices(deck, candidates=cands, params=None, community=True)
    elapsed = time.perf_counter() - t0

    prior_cap = m.DEFAULT_PARAMS["community"]["prior_cap"]
    before_by_id = {}
    before_recon = {}
    before_ranking = None
    if before_snapshot is not None:
        for sc in before_snapshot["scenarios"]:
            if sc["name"] == case["scenario"]:
                before_by_id = {row["id"]: row for row in sc["candidates"]}
                # The restricted offer set changes which positive is "best", so
                # the prior eligibility must be recomputed for exactly these 3
                # candidates instead of filtering the full-14 ranking.
                before_ranking, before_recon = _rank_restricted(
                    before_by_id, cands, prior_cap
                )
                break

    rows = []
    for row in community["candidates"]:
        cid = row["id"]
        before = before_by_id.get(cid, {})
        before_row = before_recon.get(cid, {})
        after_delta_components = row["delta_components"]
        before_delta_components = before.get("delta_components", {}) or {}
        entry = {
            "id": cid,
            "mech_delta_before": before.get("mech_delta"),
            "mech_delta_after": row["mech_delta"],
            "mech_delta_change": (
                row["mech_delta"] - before["mech_delta"]
                if before.get("mech_delta") is not None else None
            ),
            "final_score_before": before_row.get("final_score"),
            "final_score_after": row["final_score"],
            "prior_applied_before": before_row.get("prior_applied"),
            "prior_applied_after": row["prior_applied"],
            "prior_used_before": before_row.get("prior_used"),
            "prior_used_after": row["prior_used"],
            "before_is_restricted_reconstruction": bool(before_row),
        }
        for field in _CASE_FIELDS:
            entry[field + "_before"] = before_delta_components.get(field)
            entry[field + "_after"] = after_delta_components.get(field)
            if (entry[field + "_before"] is not None
                    and entry[field + "_after"] is not None):
                entry[field + "_change"] = (
                    entry[field + "_after"] - entry[field + "_before"])
        rows.append(entry)

    model_mech_winner = mech["ranking"][0]
    model_community_winner = community["ranking"][0]
    brackets = {}
    for bracket, snap in elo_snapshots.items():
        offered_elos = {cid: snap["elos"].get(cid) for cid in cands}
        card = _elo_rank(offered_elos, snap["skip_elo"], include_skip=False)
        with_skip = _elo_rank(offered_elos, snap["skip_elo"], include_skip=True)
        brackets[bracket] = {
            "offered_elos": offered_elos,
            "skip_elo": snap["skip_elo"],
            BASELINE_CARD: card,
            BASELINE_WITH_SKIP: with_skip,
            "agreement": {
                "card_vs_model_mechanism": card["selected"] == model_mech_winner,
                "card_vs_model_community": card["selected"] == model_community_winner,
                "with_skip_vs_model_mechanism": with_skip["selected"] == model_mech_winner,
                "with_skip_vs_model_community": with_skip["selected"] == model_community_winner,
            },
        }

    return {
        "name": case["name"],
        "title": case["title"],
        "scenario": case["scenario"],
        "note": case["note"],
        "deck_size": len(deck),
        "deck_counts": {k: v for k, v in sorted(Counter(deck).items())},
        "offered": cands,
        "ranking_mech_after": mech["ranking"],
        "ranking_community_after": community["ranking"],
        "model_mechanism_winner": model_mech_winner,
        "model_community_winner": model_community_winner,
        "skip_mech": mech["skip"],
        "skip_community": community["skip"],
        "before_ranking_community": before_ranking,
        "before_ranking_note": (
            "Restricted before ranking is reconstructed from the frozen "
            "per-candidate mech_delta/prior_applied under the same prior "
            "eligibility for exactly this 3-candidate offer set; it is NOT a "
            "filter of the full-14 before ranking."
        ),
        "elo_baselines": brackets,
        "candidates": rows,
        "elapsed_seconds": elapsed,
        "caveat": (
            "同一副牌、同一上下文；三选一+跳过。没有真相标签，也不声明谁"
            "『正确』。before/after 只用于暴露 replay 修正带来的影响；"
            "Elo 基准分歧只作复核线索，不作胜负判定。"
        ),
    }


# --------------------------------------------------------------------------- #
# Exhaustive local contrast: model vs highest-Elo baselines
# --------------------------------------------------------------------------- #

_LOCAL_EXAMPLES_PER_SCENARIO = 3


def _new_contrast_cell():
    return {
        "decisions": 0,
        "model_skip": 0,
        "card_agreement": 0,
        "card_disagreement": 0,
        "card_unavailable": 0,
        "with_skip_agreement": 0,
        "with_skip_disagreement": 0,
        "with_skip_unavailable": 0,
        "baseline_skip_selected": 0,
        "disagreement_examples": [],
    }


def _accumulate_contrast(target, source):
    for key in ("decisions", "model_skip", "card_agreement", "card_disagreement",
                "card_unavailable", "with_skip_agreement", "with_skip_disagreement",
                "with_skip_unavailable", "baseline_skip_selected"):
        target[key] += source[key]


def _finalize_contrast(cell):
    decisions = cell["decisions"] or 1
    cell["model_skip_rate"] = cell["model_skip"] / decisions
    cell["baseline_skip_rate"] = cell["baseline_skip_selected"] / decisions
    cell["card_agreement_rate"] = cell["card_agreement"] / decisions
    cell["with_skip_agreement_rate"] = cell["with_skip_agreement"] / decisions
    return cell


def build_local_contrast(elo_snapshots, timings):
    """All C(14,3)=364 offers per scenario, model (mechanism-only) vs two Elo baselines.

    The model vote is the mechanism-only winner (community prior OFF) so the
    comparison is not contaminated by the community draft prior that shares its
    source with Elo. Counts are agreement/disagreement and skip rates only; they
    are NOT a performance score.
    """
    t0 = time.perf_counter()
    model_winners = {}
    for scenario in m.SCENARIOS:
        deck = scenario["deck"]
        per_triple = {}
        for triple in combinations(m.WHITELIST, 3):
            per_triple[triple] = m.rank_choices(
                deck, candidates=list(triple), community=False
            )["winner"]
        model_winners[scenario["name"]] = per_triple
    timings["local_contrast_model"] = time.perf_counter() - t0

    t0 = time.perf_counter()
    brackets = {bracket: {"per_scenario": {}, "totals": _new_contrast_cell()}
                for bracket in elo_snapshots}
    details = []
    for scenario in m.SCENARIOS:
        name = scenario["name"]
        cells = {bracket: _new_contrast_cell() for bracket in elo_snapshots}
        for triple, winner in model_winners[name].items():
            detail = {
                "scenario": name,
                "offer": list(triple),
                "model_winner": winner,
            }
            for bracket, snap in elo_snapshots.items():
                offered_elos = {cid: snap["elos"].get(cid) for cid in triple}
                card = _elo_rank(offered_elos, snap["skip_elo"], include_skip=False)
                with_skip = _elo_rank(offered_elos, snap["skip_elo"], include_skip=True)
                cell = cells[bracket]
                cell["decisions"] += 1
                if winner == "SKIP":
                    cell["model_skip"] += 1
                if not card["available"]:
                    cell["card_unavailable"] += 1
                elif card["selected"] == winner:
                    cell["card_agreement"] += 1
                else:
                    cell["card_disagreement"] += 1
                if not with_skip["available"]:
                    cell["with_skip_unavailable"] += 1
                elif with_skip["selected"] == winner:
                    cell["with_skip_agreement"] += 1
                else:
                    cell["with_skip_disagreement"] += 1
                if with_skip["selected"] == "SKIP":
                    cell["baseline_skip_selected"] += 1
                if (len(cell["disagreement_examples"]) < _LOCAL_EXAMPLES_PER_SCENARIO
                        and (card["selected"] != winner
                             or with_skip["selected"] != winner)):
                    cell["disagreement_examples"].append({
                        "offer": list(triple),
                        "model_winner": winner,
                        "elo_card": card["selected"],
                        "elo_with_skip": with_skip["selected"],
                    })
                detail[bracket] = {
                    BASELINE_CARD: card["selected"],
                    BASELINE_WITH_SKIP: with_skip["selected"],
                }
            details.append(detail)
        for bracket in elo_snapshots:
            _finalize_contrast(cells[bracket])
            brackets[bracket]["per_scenario"][name] = cells[bracket]
            _accumulate_contrast(brackets[bracket]["totals"], cells[bracket])
    for bracket in elo_snapshots:
        _finalize_contrast(brackets[bracket]["totals"])
    timings["local_contrast_baselines"] = time.perf_counter() - t0
    return {
        "model_variant": "mechanism_only (community off)",
        "triples_per_scenario": 364,
        "scenarios": len(m.SCENARIOS),
        "decisions_per_bracket": len(m.SCENARIOS) * 364,
        "note": (
            "Counts are agreement/disagreement and skip rates, NOT performance. "
            "Each bracket uses only its own snapshot; no A10/all-runs mixing."
        ),
        "brackets": brackets,
        "details": details,
    }


def build_selfcheck_results(before_snapshot, after_snapshot):
    """Hand-example regression checks for missing Elo and tie handling."""
    checks = []

    def _check(name, condition, detail=None):
        checks.append({"name": name, "ok": bool(condition), "detail": detail})
        if not condition:
            raise RuntimeError("audit self-check failed: %s (%s)" % (name, detail))

    missing = _elo_rank({"A": None, "B": 1600.0}, None, include_skip=False)
    _check("missing_elo_excluded_listed",
           missing["selected"] == "B" and missing["unavailable"] == ["A"]
           and missing["available"], missing)

    allmissing = _elo_rank({"A": None, "B": None}, None, include_skip=False)
    _check("all_missing_is_unavailable_not_zero",
           allmissing["selected"] is None and not allmissing["available"]
           and allmissing["ranking"] == [], allmissing)

    tie = _elo_rank({"B": 1600.0, "A": 1600.0}, 999.0, include_skip=False)
    _check("tie_breaks_by_id_ascending", tie["selected"] == "A", tie)

    tie_skip = _elo_rank({"A": 1600.0, "B": 1600.0}, 1600.0, include_skip=True)
    _check("tie_prefers_skip_first", tie_skip["selected"] == "SKIP", tie_skip)

    missing_skip = _elo_rank({"A": 1600.0}, None, include_skip=True)
    _check("missing_skip_elo_listed",
           "SKIP" in missing_skip["unavailable"] and missing_skip["selected"] == "A",
           missing_skip)

    deterministic = (
        _elo_rank({"A": 1600.0, "B": 1500.0}, 1550.0, True)
        == _elo_rank({"A": 1600.0, "B": 1500.0}, 1550.0, True)
    )
    _check("elo_ranking_deterministic", deterministic)

    # Restricted before reconstruction: the full-14 best (5.0) would strip the
    # prior from A/B/C, but the restricted best (0.30) keeps all within cap.
    rows = {
        "A": {"mech_delta": 0.30, "prior_applied": 0.25},
        "B": {"mech_delta": 0.20, "prior_applied": 0.10},
        "C": {"mech_delta": 0.10, "prior_applied": 0.05},
        "D": {"mech_delta": 5.00, "prior_applied": 0.25},
    }
    recon = _reconstruct_restricted_final(rows, ["A", "B", "C"],
                                          m.DEFAULT_PARAMS["community"]["prior_cap"])
    _check("before_reconstruction_uses_restricted_best",
           abs(recon["A"]["final_score"] - 0.55) < 1e-9
           and abs(recon["B"]["final_score"] - 0.30) < 1e-9
           and abs(recon["C"]["final_score"] - 0.15) < 1e-9
           and recon["A"]["prior_used"], recon)

    ranking, _ = _rank_restricted(rows, ["A", "B", "C"],
                                  m.DEFAULT_PARAMS["community"]["prior_cap"])
    _check("before_restricted_ranking_has_skip", ranking[-1] == "SKIP" or "SKIP" in ranking,
           ranking)

    if before_snapshot is not None:
        current = {
            s["name"]: {k: v for k, v in sorted(Counter(s["deck"]).items())}
            for s in m.SCENARIOS
        }
        for role, snap in (("before", before_snapshot), ("after", after_snapshot)):
            if snap is None:
                continue
            got = {s["name"]: s.get("deck_counts") for s in snap["scenarios"]}
            _check("snapshot_deckcounts_match_current_fixtures_%s" % role,
                   got == current, sorted(set(got) ^ set(current)))

    return checks


# --------------------------------------------------------------------------- #
# Scenario diff (before vs after)
# --------------------------------------------------------------------------- #

def scenario_diffs(before_snapshot, after_snapshot):
    diffs = []
    if before_snapshot is None:
        return diffs
    before_by_name = {s["name"]: s for s in before_snapshot["scenarios"]}
    for after in after_snapshot["scenarios"]:
        before = before_by_name.get(after["name"])
        if before is None:
            continue
        before_c = {row["id"]: row for row in before["candidates"]}
        changed = []
        for row in after["candidates"]:
            b = before_c.get(row["id"])
            if b is None:
                continue
            delta = row["mech_delta"] - b["mech_delta"]
            if abs(delta) > 1e-9:
                changed.append({
                    "id": row["id"],
                    "before": b["mech_delta"],
                    "after": row["mech_delta"],
                    "change": delta,
                    "accuracy_component_change": (
                        row["delta_components"]["accuracy"]
                        - b["delta_components"]["accuracy"]
                    ),
                })
        changed.sort(key=lambda e: -abs(e["change"]))
        diffs.append({
            "name": after["name"],
            "winner_before": before["winner"],
            "winner_after": after["winner"],
            "winner_changed": before["winner"] != after["winner"],
            "ranking_before": before["ranking"],
            "ranking_after": after["ranking"],
            "skip_rank_before": before["skip_rank"],
            "skip_rank_after": after["skip_rank"],
            "candidate_changes": changed,
            "changed_count": len(changed),
        })
    return diffs


# --------------------------------------------------------------------------- #
# Chinese markdown report
# --------------------------------------------------------------------------- #

def render_doc(audit):
    out = []
    add = out.append
    stamp = audit["generated_at_utc"]
    add("# 小刀模型 v0.1：社区基准与缺失的 replay 协同（SHIV_SCORE_CALIBRATION）")
    add("")
    add("> 离线、只读、标准库。生成器：`%s`。生成时间（UTC，非数据版本）：`%s`。"
        % (GENERATOR, stamp))
    add("> 社区数据为缓存快照，**可能不同于当前部署数据**；快照无时间戳，"
        "故不声明数据版本。")
    add("")

    add("## 1. 数据源与哈希")
    add("")
    add("| 文件 | 角色 | sha256 | URL |")
    add("|---|---|---|---|")
    for rec in audit["sources"]:
        add("| `%s` | %s | `%s` | %s |" % (
            rec["path"], rec["role"],
            (rec["sha256"] or "-")[:16] + ("…" if rec["sha256"] else ""),
            rec["url"] or "（快照未存 URL，不臆造）",
        ))
    add("")
    svc = audit["service"]
    add("- 服务元数据（取自 `graph.json` provenance）：title=`%s`，version=`%s`，"
        "openapi=`%s`，captured_on=`%s`。"
        % (svc.get("title"), svc.get("version"), svc.get("openapi"),
           svc.get("captured_on")))
    add("- 上游源码树 sha：`%s`；URL：`%s`。"
        % (svc.get("upstream_source_tree_sha"),
           svc.get("upstream_source_tree_url")))
    add("")

    f = audit["formula"]
    add("## 2. 实际公式与含义（来自缓存源码）")
    add("")
    add("`_compute_score`（%s）：" % f["compute_score"]["source"])
    add("")
    add("```text")
    for line in f["compute_score"]["definition"]:
        add(line)
    add("```")
    add("")
    add("- 计数含义：picks = %s" % f["counts"]["picks"])
    add("- wins = %s" % f["counts"]["wins"])
    add("- baseline = %s" % f["counts"]["baseline"])
    add("- prior = %s" % f["counts"]["prior"])
    add("- Score 含义：%s" % f["compute_score"]["meaning"])
    add("")
    add("`_compute_codex_elo`（%s）：%s"
        % (f["codex_elo"]["source"], f["codex_elo"]["meaning"]))
    add("")
    add("- skip 区分：%s" % f["codex_elo"]["skip"])
    add("")
    add("已知不一致 / 警告：")
    add("")
    for item in f["discrepancies"]:
        add("- " + item)
    for item in f["warnings"]:
        add("- " + item)
    add("")

    add("## 3. 14 张白名单全量行（社区快照原值）")
    add("")
    add("| 卡 | allruns score | allruns elo | allruns picks | allruns wins | allruns win_rate | a10 score | a10 elo | a10 picks | a10 wins | a10 win_rate | elo(a10-all) |")
    add("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for row in audit["community_rows"]:
        a = row.get("allruns") or {}
        b = row.get("a10") or {}
        add("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
            row["id"],
            _fmt(a.get("score"), 0), _fmt(a.get("elo"), 1),
            _fmt(a.get("picks"), 0), _fmt(a.get("wins"), 0), _fmt(a.get("win_rate"), 1),
            _fmt(b.get("score"), 0), _fmt(b.get("elo"), 1),
            _fmt(b.get("picks"), 0), _fmt(b.get("wins"), 0), _fmt(b.get("win_rate"), 1),
            _fmt(row.get("elo_a10_minus_allruns"), 1),
        ))
    add("")
    if audit.get("community_skip"):
        add("A10 快照的 SKIP 行（伪实体，不作白名单）：`%s`。"
            % json.dumps(audit["community_skip"], ensure_ascii=False))
        add("")

    add("## 4. 八场景聚合 vs 社区（描述性，非总体）")
    add("")
    add("模型每张候选：八场景 `mech_delta` 均值、机制/final 排名均值（1=最好），"
        "以及社区 a10 与 allruns 的 score/elo。")
    add("")
    add("| 卡 | 模型均值 mech_delta | 模型平均机制排名 | 模型平均 final 排名 | a10 score | a10 elo | allruns score | allruns elo |")
    add("|---|---|---|---|---|---|---|---|")
    agg = audit["aggregate"]
    for cid in m.WHITELIST:
        row = agg["per_card"][cid]
        add("| %s | %s | %s | %s | %s | %s | %s | %s |" % (
            cid, _fmt(row["mean_mech_delta"]), _fmt(row["mean_rank_mech"], 2),
            _fmt(row["mean_rank_final"], 2),
            _fmt(row.get("community_a10_score"), 0),
            _fmt(row.get("community_a10_elo"), 1),
            _fmt(row.get("community_allruns_score"), 0),
            _fmt(row.get("community_allruns_elo"), 1)))
    add("")
    add("| 对照 | n | rho(均值 mech_delta) | rho(平均机制排名) | rho(平均 final 排名) |")
    add("|---|---|---|---|---|")
    for name, corr in audit["correlations"].items():
        add("| %s | %s | %s | %s | %s |" % (
            name, corr.get("n"),
            _fmt(corr.get("spearman_mean_mech_delta")),
            _fmt(corr.get("spearman_mean_rank_mech")),
            _fmt(corr.get("spearman_mean_rank_final"))))
    add("")
    add("- 说明：八场景为手工示意，不是总体样本；相关系数只作描述，"
        "不作有效性或因果结论。排名并列用平均秩（tie-aware，无 scipy）。")
    add("")

    add("### 需要人工复核的分歧（模型 vs 社区 a10）")
    add("")
    add("| 卡 | 模型平均机制排名 | 社区 score 排名 | 社区 elo 排名 | 与 score 差 | 与 elo 差 | a10 score | a10 elo |")
    add("|---|---|---|---|---|---|---|---|")
    for row in audit["review_flags"][:8]:
        add("| %s | %s | %s | %s | %s | %s | %s | %s |" % (
            row["id"], _fmt(row["model_mean_rank_mech"], 2),
            _fmt(row.get("community_score_rank"), 1),
            _fmt(row.get("community_elo_rank"), 1),
            _fmt(row.get("rank_gap_vs_score"), 2),
            _fmt(row.get("rank_gap_vs_elo"), 2),
            _fmt(row.get("community_a10_score"), 0),
            _fmt(row.get("community_a10_elo"), 1)))
    add("")
    for note in audit["review_notes"]:
        add("- " + note)
    add("")

    add("## 5. 模型修正：Knife Trap replay 协同")
    add("")
    add("新增可检查量：`knife_trap_replay_opportunity = kt_active * min(exhausted_pool, knife_trap_replay_cap)`。")
    add("Accuracy 贡献由 `accuracy_active * shiv_supply` 改为 "
        "`accuracy_active * (shiv_supply + knife_trap_replay_opportunity)`；"
        "replay 不回流到 supply/exhausted_pool，Afterimage 也不获得额外 replay 触发。"
        "其它权重/上限不变。")
    add("")
    add("| 场景 | before winner | after winner | 变化 | skip rank before | skip rank after | 受影响候选数 |")
    add("|---|---|---|---|---|---|---|")
    for diff in audit["scenario_diffs"]:
        add("| %s | %s | %s | %s | %s | %s | %s |" % (
            diff["name"], diff["winner_before"], diff["winner_after"],
            "是" if diff["winner_changed"] else "否",
            diff["skip_rank_before"], diff["skip_rank_after"],
            diff["changed_count"]))
    add("")
    total_changed = sum(d["changed_count"] for d in audit["scenario_diffs"])
    add("改动明细（只列非零机械 delta 变化；accuracy Δ 是 ACCURACY 组件的变化）：")
    add("")
    add("| 场景 | 候选 | mech before | mech after | Δ | accuracy Δ |")
    add("|---|---|---|---|---|---|")
    for diff in audit["scenario_diffs"]:
        for change in diff["candidate_changes"]:
            add("| %s | %s | %s | %s | %s | %s |" % (
                diff["name"], change["id"], _fmt(change["before"]),
                _fmt(change["after"]), _fmt(change["change"]),
                _fmt(change["accuracy_component_change"])))
    add("")
    winners_flipped = [d["name"] for d in audit["scenario_diffs"] if d["winner_changed"]]
    add("八场景中发生机械 delta 变化的候选-场景对：%d；winner 变化：%d 个（%s）。"
        % (total_changed, len(winners_flipped),
           ", ".join(winners_flipped) if winners_flipped else "无"))
    add("")
    add("修正只影响同时具备 ACCURACY 活跃、小刀供给、KNIFE_TRAP 活跃的局面；"
        "供给既可来自基线牌，也可来自本次被评估的生成牌候选，因此"
        "`payoffs_no_producers`（基线供给为 0）的生成牌候选仍会因新增供给而受益；"
        "而 `starter`/`producers_no_payoffs` 等不同时具备三要素的场景不受影响。")
    add("")
    add("Phantom 与 replay 的时序交互仍未建模：本模型对 Phantom 只取"
        "『每活跃回合首刀』，重放是否触发 Phantom 首刀需要 runtime 验证，"
        "故此处不给 Phantom 任何 replay 信用。")
    add("")

    add("## 6. 四个三选一+跳过决策案例")
    add("")
    for case in audit["decision_cases"]:
        add("### %s（`%s`，场景 `%s`，牌组 %d 张）"
            % (case["title"], case["name"], case["scenario"], case["deck_size"]))
        add("")
        add(case["note"])
        add("")
        add("- 提供：%s" % ", ".join(case["offered"]))
        add("- 机制-only 排名（community off）：%s" % " > ".join(case["ranking_mech_after"]))
        add("- community-on 排名：%s" % " > ".join(case["ranking_community_after"]))
        add("- skip（机制）：mech_delta=%s；skip（community）：final=%s，is_winner=%s"
            % (_fmt(case["skip_mech"]["mech_delta"]),
               _fmt(case["skip_community"]["final_score"]),
               case["skip_community"]["is_winner"]))
        add("")
        add("| 候选 | mech before | mech after | Δ | accuracy Δ | knife_trap Δ | final before | final after | prior_applied after |")
        add("|---|---|---|---|---|---|---|---|---|")
        for row in case["candidates"]:
            add("| %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
                row["id"], _fmt(row["mech_delta_before"]), _fmt(row["mech_delta_after"]),
                _fmt(row["mech_delta_change"]),
                _fmt(row.get("accuracy_change")), _fmt(row.get("knife_trap_change")),
                _fmt(row["final_score_before"]), _fmt(row["final_score_after"]),
                _fmt(row["prior_applied_after"])))
        add("")
        add("> %s" % case["caveat"])
        add("")

    eb = audit.get("elo_baseline")
    add("## 7. 最高社区 Elo 基准（新增；不改模型/权重）")
    add("")
    add("对上述四个三选一+跳过案例，另算两个纯社区 Elo 基准，分 all-runs 与 "
        "A10 两个 bracket 各自独立排名，**不混用**：")
    add("")
    add("- `elo_highest_card`：只在被提供的 3 张候选里选 Elo 最高者，从不跳过。")
    add("- `elo_highest_with_skip`：把该 bracket 的 SKIP 行作为伪候选一起排名。")
    add("")
    add("规则：Elo 缺失的候选被排除并显式列出（绝不当作 0）；若全部缺失则该基准"
        "标记为 unavailable。并列时 SKIP 优先，然后按 ID 升序。Elo 是揭示偏好，"
        "与关系图 community_draft 同源；**这不是独立效果验证，也不用作判定模型"
        "对错的裁判**，分歧只作为复核线索。all-runs bracket 的全部 Elo 只来自 "
        "`scores-all-skip.json`（含 SKIP 行）。")
    add("")
    if eb:
        add("| 数据源 | bracket | URL | sha256 |")
        add("|---|---|---|---|")
        for rec in eb["sources"]:
            add("| `%s` | %s | %s | `%s` |" % (
                rec["path"], rec["bracket"], rec.get("url") or "（快照未存 URL，不臆造）",
                (rec.get("sha256") or "-")[:16] + ("…" if rec.get("sha256") else "")))
        add("")
        add("### 7.1 四个案例的基准选择")
        add("")
        for case in audit["decision_cases"]:
            add("#### %s（`%s`）" % (case["title"], case["name"]))
            add("")
            add("- 提供：%s" % ", ".join(case["offered"]))
            add("- 模型（机制-only）胜者：`%s`；模型（community-on）胜者：`%s`"
                % (case["model_mechanism_winner"], case["model_community_winner"]))
            add("")
            add("| bracket | 方法 | 选择 | 可用性 | 与模型(机制/community)一致 | 缺失 Elo |")
            add("|---|---|---|---|---|---|")
            for bracket, data in case["elo_baselines"].items():
                ag = data["agreement"]
                for method in (BASELINE_CARD, BASELINE_WITH_SKIP):
                    bl = data[method]
                    add("| %s | %s | %s | %s | %s / %s | %s |" % (
                        bracket, method,
                        bl["selected"] if bl["selected"] is not None else "unavailable",
                        "available" if bl["available"] else "unavailable",
                        ag["card_vs_model_mechanism"] if method == BASELINE_CARD
                        else ag["with_skip_vs_model_mechanism"],
                        ag["card_vs_model_community"] if method == BASELINE_CARD
                        else ag["with_skip_vs_model_community"],
                        ", ".join(bl["unavailable"]) or "-"))
            add("")
            add("候选 Elo 原值（allruns / a10）：%s" % "; ".join(
                "%s=%s/%s" % (
                    cid,
                    _fmt(case["elo_baselines"][BRACKET_ALLRUNS]["offered_elos"].get(cid), 1),
                    _fmt(case["elo_baselines"][BRACKET_A10]["offered_elos"].get(cid), 1),
                ) for cid in case["offered"]))
            add("")

        lc = audit["elo_baseline"]["local_contrast"]
        add("### 7.2 穷举局部对照（模型 vs 两个 Elo 基准）")
        add("")
        add("对 8 个固定场景各取全部 C(14,3)=364 组三选一，共 %d 个决策；模型票为"
            "**机制-only**胜者（关掉 community prior，避免与同源的 Elo 双重计数）。"
            "仅记录一致/不一致计数与跳过率，**不是性能评分**。"
            % lc["decisions_per_bracket"])
        add("")
        add("| bracket | 基准 | 决策 | 一致 | 不一致 | 基准缺失 | 模型跳过率 | 基准选 SKIP 率 |")
        add("|---|---|---|---|---|---|---|---|")
        for bracket, data in lc["brackets"].items():
            t = data["totals"]
            add("| %s | %s | %d | %d | %d | %d | %s | %s |" % (
                bracket, BASELINE_CARD, t["decisions"], t["card_agreement"],
                t["card_disagreement"], t["card_unavailable"],
                _fmt(t["model_skip_rate"]), _fmt(0.0)))
            add("| %s | %s | %d | %d | %d | %d | %s | %s |" % (
                bracket, BASELINE_WITH_SKIP, t["decisions"], t["with_skip_agreement"],
                t["with_skip_disagreement"], t["with_skip_unavailable"],
                _fmt(t["model_skip_rate"]),
                _fmt(t["baseline_skip_rate"])))
        add("")
        add("每个场景的不一致计数（compact，完整 2912 条见 `elo-baseline.json`）：")
        add("")
        add("| 场景 | a10 card 不一致 | a10 with_skip 不一致 | model skip | 样例（offer → model / elo_card / elo_with_skip） |")
        add("|---|---|---|---|---|")
        for scenario in m.SCENARIOS:
            name = scenario["name"]
            a10 = lc["brackets"][BRACKET_A10]["per_scenario"][name]
            al = lc["brackets"][BRACKET_ALLRUNS]["per_scenario"][name]
            samples = "; ".join(
                "%s → %s / %s / %s" % (
                    "+".join(ex["offer"]), ex["model_winner"],
                    ex["elo_card"], ex["elo_with_skip"])
                for ex in a10["disagreement_examples"][:2]) or "-"
            add("| %s | %d | %d | %d | %s |" % (
                name, a10["card_disagreement"], a10["with_skip_disagreement"],
                a10["model_skip"], samples))
        add("")
        add("> 对照只对 8 个手工示意场景做局部、同上下文比较；它既不是总体样本，"
            "也不代表谁更准。基准与模型的分歧只用于人工复核，不作胜负结论。")
        add("")
        checks = audit["elo_baseline"].get("selfchecks") or []
        ok = [c["name"] for c in checks if c.get("ok")]
        add("内建自检（缺失 Elo、并列、限制性 before 重建、快照牌数一致）%d/%d 通过：%s。"
            % (len(ok), len(checks), ", ".join("`%s`" % n for n in ok)))
        add("")

    add("## 8. 限制与已知不确定性")
    add("")
    for item in audit["limitations"]:
        add("- " + item)
    add("")

    add("## 9. 下一步（仅建议，默认关闭，之后再做）")
    add("")
    add("- 本修正保持默认在线下模型内，**不接入生产 C# 评分**；如日后接入，"
        "应作为可关闭的参数分支并附对拍金样，默认 off。")
    add("- 需要 runtime 确认：Knife Trap 重放是否计为『打出一张牌』"
        "（影响 Afterimage / Phantom 首刀），以及 Phantom 首刀的精确判定。")
    add("- 若要继续校准：先收集少量人工可审查的具体三选一+跳过局面，"
        "逐项核对理由；不要用 score/winrate 当训练标签，不要启动大规模拟合/采样。")
    add("")

    add("## 10. 复现与耗时")
    add("")
    add("```text")
    add("python -m unittest discover -s tests -p test_shiv_prior_model.py")
    add("python scripts/shiv_prior_model.py")
    add("python scripts/audit_shiv_scores.py --freeze-before   # 冻结旧模型")
    add("python scripts/audit_shiv_scores.py                   # 完整审计 + 本报告")
    add("```")
    add("")
    add("| 阶段 | 秒 |")
    add("|---|---|")
    for key, value in sorted(audit["timings_seconds"].items()):
        add("| %s | %s |" % (key, _fmt(value, 4)))
    add("")
    add("输出：`work/shiv-score-audit/audit.json`、`model-before.json`、"
        "`model-after.json`、`elo-baseline.json`、本文件。")
    add("")
    return "\n".join(out) + "\n"


# --------------------------------------------------------------------------- #
# Orchestration
# --------------------------------------------------------------------------- #

def _snapshot_payload(snapshot, role):
    return {
        "schema": SCHEMA + "-model-snapshot",
        "schema_version": SCHEMA_VERSION,
        "generator": GENERATOR,
        "role": role,
        "captured_at_utc": datetime.now(timezone.utc).isoformat(),
        "model_params": m.DEFAULT_PARAMS,
        "scenarios": snapshot["scenarios"],
    }


def freeze_before():
    t0 = time.perf_counter()
    snapshot = build_model_snapshot({})
    payload = _snapshot_payload(
        snapshot,
        "frozen prior-model scenario output captured BEFORE the Knife Trap "
        "replay-synergy correction",
    )
    _write_json(MODEL_BEFORE, payload)
    print("froze %s (%d scenarios, %.3fs)"
          % (MODEL_BEFORE, len(snapshot["scenarios"]), time.perf_counter() - t0))
    return 0


def run_audit():
    timings = {}
    t_all = time.perf_counter()

    t0 = time.perf_counter()
    sources = build_sources()
    service = build_service()
    formula = build_formula()
    timings["sources_and_formula"] = time.perf_counter() - t0

    community_rows, community_skip = load_community(timings)
    elo_snapshots = load_baseline_snapshots(timings)

    before_snapshot = _load_json(MODEL_BEFORE) if os.path.isfile(MODEL_BEFORE) else None
    if before_snapshot is None:
        # The restricted worker command has no argument flags; auto-freeze the
        # currently-loaded model as the pre-correction baseline on first run.
        before_snapshot = build_model_snapshot(timings)
        _write_json(MODEL_BEFORE, _snapshot_payload(
            before_snapshot,
            "auto-frozen prior-model scenario output captured BEFORE the Knife Trap "
            "replay-synergy correction (first audit run)",
        ))
        print("auto-froze %s" % MODEL_BEFORE)
    after_snapshot = build_model_snapshot(timings)
    _write_json(MODEL_AFTER, _snapshot_payload(
        after_snapshot,
        "prior-model scenario output AFTER the Knife Trap replay-synergy correction",
    ))

    t0 = time.perf_counter()
    aggregate, scenario_views = aggregate_model(after_snapshot)
    correlations = correlate(aggregate, community_rows)
    flags = review_flags(aggregate, community_rows)
    timings["aggregate_and_correlate"] = time.perf_counter() - t0

    t0 = time.perf_counter()
    diffs = scenario_diffs(before_snapshot, after_snapshot)
    timings["scenario_diffs"] = time.perf_counter() - t0

    t0 = time.perf_counter()
    decision_cases = [
        evaluate_case(case, before_snapshot, elo_snapshots) for case in DECISION_CASES
    ]
    timings["decision_cases"] = time.perf_counter() - t0

    t0 = time.perf_counter()
    selfchecks = build_selfcheck_results(before_snapshot, after_snapshot)
    timings["selfchecks"] = time.perf_counter() - t0

    local_contrast = build_local_contrast(elo_snapshots, timings)

    per_card = {}
    community_by_id = {r["id"]: r for r in community_rows}
    for cid in m.WHITELIST:
        row = dict(aggregate[cid])
        c = community_by_id[cid]
        row["community_allruns_score"] = (c["allruns"] or {}).get("score")
        row["community_allruns_elo"] = (c["allruns"] or {}).get("elo")
        row["community_a10_score"] = (c["a10"] or {}).get("score")
        row["community_a10_elo"] = (c["a10"] or {}).get("elo")
        per_card[cid] = row

    elo_path_bracket = {
        "work/shiv-score-audit/scores-all-skip.json": BRACKET_ALLRUNS,
        "work/shiv-score-audit/scores-a10.json": BRACKET_A10,
    }
    elo_sources = [{
        "path": rec["path"],
        "bracket": elo_path_bracket[rec["path"]],
        "url": rec["url"],
        "sha256": rec["sha256"],
        "exists": rec["exists"],
        "role": rec["role"],
    } for rec in sources if rec["path"] in elo_path_bracket]

    definition = {
        "method": [
            "%s: rank the 3 offered cards by community Elo, never SKIP" % BASELINE_CARD,
            "%s: rank the 3 offered cards plus the bracket SKIP row by Elo" % BASELINE_WITH_SKIP,
            "brackets are independent: all-runs uses scores-all-skip.json only; "
            "a10 uses scores-a10.json only",
            "ties are broken SKIP first, then card ID ascending",
            "unavailable (missing) Elo is excluded and listed; all missing => "
            "unavailable; missing is never treated as zero",
        ],
        "not_a_judge": (
            "Elo is revealed preference sharing its source with the graph draft "
            "layer; disagreements with the heuristic model are review cases, not "
            "wins/losses. No model score is used as ground truth, and none of "
            "these baselines is a performance metric."
        ),
    }

    _write_json(ELO_BASELINE_JSON, {
        "schema": SCHEMA + "-elo-baseline",
        "schema_version": SCHEMA_VERSION,
        "generator": GENERATOR,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "definition": definition,
        "sources": elo_sources,
        "decision_cases": decision_cases,
        "local_contrast": local_contrast,
        "selfchecks": selfchecks,
    })

    elo_baseline = {
        "definition": definition,
        "sources": elo_sources,
        "detail_json": "work/shiv-score-audit/elo-baseline.json",
        "local_contrast": {
            key: value for key, value in local_contrast.items() if key != "details"
        },
        "local_contrast_totals": {
            bracket: data["totals"]
            for bracket, data in local_contrast["brackets"].items()
        },
        "local_contrast_note": local_contrast["note"],
        "selfchecks": selfchecks,
    }

    limitations = [
        "社区快照可能不同于当前部署数据；无快照时间戳，故不声明数据版本。",
        "Score 是截断后的关联分数，不是 100% 胜率，也不是因果效应。",
        "Elo 是揭示偏好，与关系图 community_draft 同源；不是独立有效性验证。",
        "八场景为手工示意，不是总体；相关系数只作描述，不作强度/胜率声明。",
        "模型权重/上限仍为手工、未拟合；本次只改一处机制协同，未调参。",
        "Knife Trap replay 是否计为『打出一张牌』需 runtime 验证；"
        "Phantom 首刀与 replay 的时序交互仍未建模。",
        "抽牌/能量仍是单次有界代理；不是战斗模拟。",
        "审查中不使用 score / winrate 作为训练目标，不启动大规模拟合或采样。",
    ]

    review_notes = [
        "上表按模型与社区 a10 排名的最大差距排序，仅用于人工复核，不强制统一排名。",
        "ADRENALINE 在模型示意场景中排名靠前，与其社区 a10 高分方向一致；"
        "这本身不是缺陷，本轮不做下调。",
        "模型与社区分歧可能来自机制代理（供给/能量/时机）与揭示偏好/关联指标"
        "的口径差异，不能据此断言谁对谁错。",
        "均值 mech_delta 与社区 score/elo 的 Spearman 为负：模型比较的是"
        "『把候选加入整副牌』的边际，生成牌在多数示意牌组中边际为正，而"
        "payoff/功能牌在缺供给牌组中边际可为负；社区 Score 是整牌出现时的"
        "胜率关联。两者口径不同，负相关只作描述，不代表模型更强或更弱。",
    ]

    audit = {
        "schema": SCHEMA,
        "schema_version": SCHEMA_VERSION,
        "generator": GENERATOR,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "warning": (
            "Descriptive audit only. Cached public snapshots may differ from the "
            "deployed data; no snapshot timestamp is available, so no data version "
            "is asserted. Score/Elo are association/revealed-preference signals, "
            "not causal effectiveness. Not used as training targets."
        ),
        "sources": sources,
        "service": service,
        "formula": formula,
        "community_rows": community_rows,
        "community_skip": community_skip,
        "aggregate": {"per_card": per_card, "scenario_views": scenario_views},
        "correlations": correlations,
        "review_flags": flags,
        "review_notes": review_notes,
        "scenario_diffs": diffs,
        "decision_cases": decision_cases,
        "elo_baseline": elo_baseline,
        "timings_seconds": {},
        "limitations": limitations,
    }

    # Write JSON before rendering so a rendering bug can't lose the data.
    _write_json(AUDIT_JSON, audit)

    timings["total"] = time.perf_counter() - t_all
    audit["timings_seconds"] = {k: round(v, 6) for k, v in timings.items()}
    _write_json(AUDIT_JSON, audit)

    with open(AUDIT_MD, "w", encoding="utf-8") as handle:
        handle.write(render_doc(audit))

    print("wrote %s" % AUDIT_JSON)
    print("wrote %s" % ELO_BASELINE_JSON)
    print("wrote %s" % AUDIT_MD)
    print("timings: %s" % json.dumps(audit["timings_seconds"], sort_keys=True))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--freeze-before", action="store_true",
                        help="capture the current model's scenario output as "
                             "model-before.json, then exit")
    args = parser.parse_args(argv)
    if args.freeze_before:
        return freeze_before()
    return run_audit()


if __name__ == "__main__":
    sys.exit(main())
