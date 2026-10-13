"""Attribute the Prepared/Anticipate anchor without changing model weights.

Run PatchSmoke --prepared-anticipate-audit first. The community projection is
the frozen candidate evaluated in the preceding deck-outcome audit; its DLL
hash must match the diagnostic DLL before reusing its rows.
"""
import collections
import hashlib
import itertools
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "work/prepared-anticipate-audit"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    source = ROOT / "work/deck-outcome-optimization/community-after.json"
    source_hashes = {k.replace("\\", "/"): v for k, v in
                     read(ROOT / "work/deck-outcome-optimization/input-sha256.json").items()}
    dll = OUT / "native/CoopBots.dll"
    assert digest(dll) == source_hashes["work/deck-outcome-optimization/finale-native/CoopBots.dll"]
    assert digest(source) == source_hashes[source.relative_to(ROOT).as_posix()]
    native = read(OUT / "native-audit.json")["rows"]
    # These validate the diagnostic interventions, not game-strategy quality.
    assert all(r["controls"]["drawWhileHidden"] == 0
               and r["controls"]["cleanupWhileHidden"] is False
               and r["controls"]["slyCountWhileHidden"] == 0 for r in native)
    assert all(r["controls"]["anticipateDexterityContribution"] > 0 for r in native)
    assert all(r["controls"]["originalSlyCount"] == 2 for r in native if r["scenario"] == "sly")
    runs = {p.stem: read(p) for p in (ROOT / "work/spire-codex/runs-full").glob("*.json")}
    rows = []
    for row in read(source)["rows"]:
        node = list(itertools.chain.from_iterable(runs[row["run"]]["map_point_history"]))[row["floor"] - 1]
        rooms = {r["room_type"] for r in node["rooms"]}
        if rooms and rooms <= {"monster", "elite", "boss"} and row["alliedDecksKnown"] and row["character"] == "SILENT":
            rows.append(row)
    cards = {}
    for card in ["PREPARED", "ANTICIPATE"]:
        offered = [r for r in rows if card in r["cards"]]
        cards[card] = dict(offered=len(offered),
            human=sum(r["picked"] == r["cards"].index(card) for r in offered),
            bot=sum(r["predicted"] == r["cards"].index(card) for r in offered),
            legacy=sum(r["legacy"] == r["cards"].index(card) for r in offered), rows=offered)
    community = dict(source=str(source.relative_to(ROOT)), silent_screens=len(rows),
        simultaneous_offers=sum(all(c in r["cards"] for c in cards) for r in rows), cards=cards)
    (OUT / "community-offers.json").write_text(json.dumps(community, indent=2), encoding="utf-8")
    # Difference of marginal combat values: a prototype of the requested
    # robustness term. These are existing mixed combat scenarios with three
    # Wounds present from the start, not a native Boss insertion replay.
    interference = {}
    for seats in sorted({r["seats"] for r in native}):
        reference = next(r for r in native if r["seats"] == seats and r["scenario"] == "starter")
        disrupted = next(r for r in native if r["seats"] == seats and r["scenario"] == "wounds")
        before = {v["card"]: v["combat"] for v in reference["values"]}
        interference[f"{seats}p"] = {v["card"]: dict(reference_marginal=before[v["card"]],
            three_wounds_marginal=v["combat"], robustness_difference=v["combat"]-before[v["card"]])
            for v in disrupted["values"]}
    report = dict(production_changed=False, diagnostic_controls_valid=True,
        same_price_shop=collections.Counter(r["shop"] for r in native),
        filtering_contribution={f'{r["seats"]}p/{r["scenario"]}': r["mechanism"]["drawContribution"] for r in native},
        sly_contribution={f'{r["seats"]}p/{r["scenario"]}': r["mechanism"]["slyContribution"] for r in native if r["scenario"] == "sly"},
        cleanup_contribution={f'{r["seats"]}p/{r["scenario"]}': r["mechanism"]["cleanupContribution"] for r in native},
        interference_sensitivity=interference,
        community={c: {k: v for k, v in d.items() if k != "rows"} for c, d in cards.items()},
        limitations=["Same-price ranking does not prove discard mechanism coverage.",
            "Native probe uses deterministic constructed decks, not live climbs.",
            "Community data are a selected early A10 cache; no paired offer contains both cards.",
            "Projected plays is one trajectory; combat attribution includes all production scenarios.",
            "Interference sensitivity compares existing mixed scenarios with three starting Wounds; it is not a calibrated robustness coefficient or a native Boss replay.",
            "A diagnostic command exiting zero only confirms measurement completion."])
    (OUT / "audit-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    inputs = [dll, OUT / "native/PatchSmoke.dll", OUT / "native-audit.json", source,
              ROOT / "tests/PatchSmoke/PreparedAnticipateAudit.cs", Path(__file__)]
    inputs += list((ROOT / "src/CoopBots/Building").glob("*.cs"))
    inputs += sorted((ROOT / "work/spire-codex/runs-full").glob("*.json"))
    (OUT / "input-sha256.json").write_text(json.dumps({str(p.relative_to(ROOT)): digest(p) for p in inputs}, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
