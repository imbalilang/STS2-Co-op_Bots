"""Paired retrospective audit of the fixed-cost risk and Bully payoff changes.

Run the native --unified-community probe first; see docs/ENERGY_COMMITMENT_RISK.md.
Uses cached local runs only. No parameters are fitted or written by this script.
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
OUT = ROOT / "work/energy-commitment-risk"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def best(values):
    return values.index(max(values)) if max(values) > 0 else -1


def scores(row, weight):
    # The saved native feature is the actual deck-potential delta. Guards
    # precede the potential and must never be weakened by an ablation.
    return [v if row["guards"][i] else v + row["features"][i]["risk"] - weight * row["commitmentLoad"][i]
            for i, v in enumerate(row["values"])]


def metrics(pairs, predict):
    games = collections.defaultdict(list)
    correct = old_correct = skips = 0
    cards = {c: dict(offered=0, human=0, before=0, after=0) for c in ["BLUDGEON", "BULLY"]}
    for before, after in pairs:
        pred = predict(before, after)
        hit, old = int(pred == after["picked"]), int(before["predicted"] == after["picked"])
        correct += hit
        old_correct += old
        skips += pred == -1
        games[after["group"]].append((hit, old))
        for card, tally in cards.items():
            if card not in after["cards"]:
                continue
            i = after["cards"].index(card)
            tally["offered"] += 1
            tally["human"] += after["picked"] == i
            tally["before"] += before["predicted"] == i
            tally["after"] += pred == i
    delta = [statistics.mean(h - o for h, o in rows) for rows in games.values()]
    rng = random.Random(930)
    boot = sorted(statistics.mean(rng.choices(delta, k=len(delta))) for _ in range(2000))
    return dict(screens=len(pairs), games=len(games), before_agreement=old_correct / len(pairs),
                agreement=correct / len(pairs), skip=skips / len(pairs),
                run_weighted_agreement=statistics.mean(statistics.mean(h for h, _ in rows) for rows in games.values()),
                paired_run_delta=statistics.mean(delta), paired_run_bootstrap_95=[boot[50], boot[1949]], cards=cards)


def main():
    before_doc = read(OUT / "community-before.json")
    after_doc = read(OUT / "community-after.json")
    before, after = before_doc["rows"], after_doc["rows"]
    assert len(before) == len(after), "native projections must retain the same reward rows"
    key = lambda r: (r["group"], r["floor"], r["character"], r["cards"], r["picked"], r["size"])
    assert all(key(b) == key(a) for b, a in zip(before, after)), "paired reward identity mismatch"
    runs = {p.stem: read(p) for p in (ROOT / "work/spire-codex/runs-full").glob("*.json")}
    def battle(r):
        node = list(itertools.chain.from_iterable(runs[r["run"]]["map_point_history"]))[r["floor"] - 1]
        rooms = {x["room_type"] for x in node["rooms"]}
        return bool(rooms) and rooms <= {"monster", "elite", "boss"}
    battle_pairs = [(b, a) for b, a in zip(before, after) if battle(a)]
    # Unknown allied decks cannot validate a new allied synergy estimate.
    pairs = [(b, a) for b, a in battle_pairs if a["alliedDecksKnown"]]
    assert all(best(a["values"]) == a["predicted"] for _, a in pairs)
    ablations = {
        "previous_unified": lambda b, a: b["predicted"],
        "risk_only_4": lambda b, a: best([v if b["guards"][i] else v - 4 * a["commitmentLoad"][i]
                                        for i, v in enumerate(b["values"])]),
        "payoff_and_reward_timing_fix_only": lambda b, a: best(scores(a, 0)),
        "final": lambda b, a: a["predicted"],
    }
    groups = {"all": pairs}
    for field in ["split", "seats", "character"]:
        for val in sorted({a[field] for _, a in pairs}):
            groups[f"{field}:{val}"] = [(b, a) for b, a in pairs if a[field] == val]
    report = dict(projected=len(after), battle_screens=len(battle_pairs),
                  excluded_unknown_allies=len(battle_pairs) - len(pairs),
                  ablations={name: metrics(pairs, fn) for name, fn in ablations.items()},
                  groups={name: metrics(rows, ablations["final"]) for name, rows in groups.items()},
                  retrospective_weight_grid=[dict(weight=w, **metrics(pairs, lambda b, a: best(scores(a, w))))
                                             for w in [0, 2, 4, 6, 8, 12, 16, 20]],
                  limitations=[
                      "Retrospective preference agreement, not causal win-rate validation.",
                      "The previous holdout was already inspected; it is not a fresh validation set.",
                      "Runs are a selected A10 early-floor cache; community priors may overlap this cache.",
                      "Unknown allied decks are excluded. No relic/combat/HP reconstruction is claimed.",
                      "The mechanism ablation includes correcting simultaneous reward timing for allied decks.",
                      "Site pick-rate denominator is tracked runs, not reward screens offering this card."])
    (OUT / "regression-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    inputs = [OUT / "community-before.json", OUT / "community-after.json", pathlib.Path(__file__)]
    inputs += sorted((ROOT / "src/CoopBots/Building").glob("*.cs"))
    inputs += [ROOT / "tests/PatchSmoke/UnifiedCommunityRegression.cs", ROOT / "tests/PatchSmoke/BullyPriceAnchorScenarios.cs"]
    inputs += sorted((ROOT / "work/spire-codex/runs-full").glob("*.json"))
    (OUT / "input-sha256.json").write_text(json.dumps({str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                                                     for p in inputs}, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ["projected", "battle_screens", "excluded_unknown_allies", "ablations"]}, indent=2))


if __name__ == "__main__":
    main()
