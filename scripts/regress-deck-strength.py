"""Summarize the native --unified-community probe without fitting held-out labels.

Run the C# probe first (see docs/UNIFIED_DECK_STRENGTH.md). No external packages.
Outputs JSON metrics, paired run bootstrap intervals, parameter sensitivity and
SHA256 provenance. Agreement measures preference replication, not win probability.
"""
from __future__ import annotations

import collections
import hashlib
import itertools
import json
import pathlib
import random
import statistics

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "work/unified-deck-strength"
DATA = OUT / "community-decisions.json"


def mean(xs):
    return statistics.mean(xs) if xs else None


def summary(rows):
    by_game = collections.defaultdict(list)
    for row in rows:
        by_game[row["group"]].append(row)
    delta = [mean([int(r["predicted"] == r["picked"]) - int(r["legacy"] == r["picked"])
                   for r in game]) for game in by_game.values()]
    rng = random.Random(930)
    bootstrap = sorted(mean(rng.choices(delta, k=len(delta))) for _ in range(2000)) if delta else []
    return dict(screens=len(rows), games=len(by_game),
                old_agreement=mean([r["legacy"] == r["picked"] for r in rows]),
                new_agreement=mean([r["predicted"] == r["picked"] for r in rows]),
                run_weighted_agreement_delta=mean(delta),
                paired_run_bootstrap_95=[bootstrap[50], bootstrap[1949]] if bootstrap else [],
                human_skip=mean([r["picked"] == -1 for r in rows]),
                new_skip=mean([r["predicted"] == -1 for r in rows]))


def sensitivity(rows):
    # This reports a retrospective development-only grid, it does not rewrite
    # constants or claim an independent win-rate validation. Unknown-effect
    # imputation and starter prior are fixed, not identified by reward labels.
    dev = [r for r in rows if r["split"] == "development"]
    results = []
    for mix, prior, tax in itertools.product([.25, .5, .65, .8, 1], [.35, .5, .75, 1], [0, .06, .18, .35]):
        scores = collections.defaultdict(list)
        for r in dev:
            size = r["size"]
            size_delta = tax * (max(0, size + 1 - 20) ** 2 - max(0, size - 20) ** 2)
            vals = [r["values"][i] if r["guards"][i] else
                    mix * f["cycle"] + (1 - mix) * f["hand"] + prior * f["prior"] + f["unknown"] - size_delta - f.get("risk", 0)
                    for i, f in enumerate(r["features"])]
            pick = vals.index(max(vals)) if max(vals) > 0 else -1
            scores[r["group"]].append(pick == r["picked"])
        results.append(dict(cycle_mix=mix, prior_weight=prior, size_tax=tax,
                            development_run_accuracy=mean([mean(v) for v in scores.values()])))
    return sorted(results, key=lambda r: -r["development_run_accuracy"])[:12]


def community_removals(runs):
    seen = set()
    groups = collections.defaultdict(list)
    for run in runs.values():
        key = (run["seed"], run["start_time"], tuple(sorted(str(p["id"]) for p in run["players"])))
        if key in seen:
            continue
        seen.add(key)
        if run["ascension"] != 10 or run["game_mode"] != "standard" or run["modifiers"]:
            continue
        if any(p["character"].replace("CHARACTER.", "") not in
               {"IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"} for p in run["players"]):
            continue
        for player in run["players"]:
            visits = removed = 0
            for node in itertools.chain.from_iterable(run["map_point_history"]):
                if not any(room["room_type"] == "shop" for room in node["rooms"]):
                    continue
                for stats in node["player_stats"]:
                    if stats["player_id"] == player["id"]:
                        visits += 1
                        removed += len(stats.get("cards_removed", []))
            groups[f"{len(run['players'])}p:{'win' if run['win'] else 'loss'}"].append((visits, removed))
    return {key: dict(seat_runs=len(rows), mean_shop_visits=mean([r[0] for r in rows]),
                      mean_removals=mean([r[1] for r in rows]), median_removals=statistics.median(r[1] for r in rows),
                      removal_histogram=dict(sorted(collections.Counter(r[1] for r in rows).items())))
            for key, rows in sorted(groups.items())}


def main():
    doc = json.loads(DATA.read_text(encoding="utf-8-sig"))
    rows = doc.pop("rows")
    runs = {p.stem: json.loads(p.read_text(encoding="utf-8-sig"))
            for p in sorted((ROOT / "work/spire-codex/runs-full").glob("*.json"))}
    # A short shop inventory or an event choice is not an ordinary reward
    # screen. Preserve raw probe output, but exclude them from the metric.
    def battle_reward(row):
        node = list(itertools.chain.from_iterable(runs[row["run"]]["map_point_history"]))[row["floor"] - 1]
        rooms = {r["room_type"] for r in node["rooms"]}
        return bool(rooms) and rooms <= {"monster", "elite", "boss"}
    doc["projected_screens"] = len(rows)
    rows = [r for r in rows if battle_reward(r)]
    doc["excluded_non_battle_screens"] = doc["projected_screens"] - len(rows)
    groups = {}
    for key in ["split", "seats", "character", "version"]:
        for val in sorted(set(r[key] for r in rows)):
            groups[f"{key}:{val}"] = summary([r for r in rows if r[key] == val])
    for split in ["development", "holdout"]:
        for seats in sorted(set(r["seats"] for r in rows)):
            groups[f"{split}:{seats}p"] = summary([r for r in rows if r["split"] == split and r["seats"] == seats])
    offered = [r for r in rows if "BLUDGEON" in r["cards"]]
    bludgeon = dict(offered=len(offered), **{
        key: sum(r[key] == r["cards"].index("BLUDGEON") for r in offered)
        for key in ["picked", "legacy", "predicted"]})
    stats_path = OUT / "bludgeon-live-stats.json"
    if stats_path.exists():
        stats = json.loads(stats_path.read_text(encoding="utf-8-sig"))
        bludgeon["site_run_denominator"] = {key: {k: stats["brackets"][key][k]
            for k in ["picks", "total_runs", "pick_rate", "elo"]} for key in ["all", "wr30", "4p:a10"]}
    inputs = sorted((ROOT / "work/spire-codex/runs-full").glob("*.json")) + [DATA,
        ROOT / "tests/PatchSmoke/Fixtures/unified-shops.json",
        ROOT / "tests/PatchSmoke/UnifiedCommunityRegression.cs",
        ROOT / "tests/PatchSmoke/HistoricalBuildValue.cs"]
    inputs += sorted((ROOT / "src/CoopBots/Building").glob("*.cs"))
    inputs += [pathlib.Path(__file__).resolve(), ROOT / "scripts/audit-shop-removals.py"]
    if stats_path.exists():
        inputs.append(stats_path)
    manifest = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs}
    report = dict(cohort=doc, all=summary(rows), groups=groups, bludgeon=bludgeon,
                  community_shop_removals=community_removals(runs),
                  development_sensitivity=sensitivity(rows), limitations=[
                      "Early-floor projection with unknown later histories excluded; relics and combat outcomes are not replayed.",
                      "Cached runs were selected by character, party size and outcome; not a random population sample.",
                      "Deduplicated by seed/start_time/players; every physical game stays in one split.",
                      "Community priors may include these records. This is a retrospective preference audit, not independent causal win validation.",
                      "Raw site pick rate divides by tracked runs; offer-conditional reward selection has a different denominator.",
                      "Few early decks exceed 20 cards: size tax is not identified by this grid."])
    (OUT / "regression-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    (OUT / "input-sha256.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"cohort": doc, "groups": {k: v for k, v in groups.items() if k.startswith(("development:", "holdout:"))},
                      "bludgeon": bludgeon, "best_development_grid": report["development_sensitivity"][0]}, indent=2))


if __name__ == "__main__":
    main()
