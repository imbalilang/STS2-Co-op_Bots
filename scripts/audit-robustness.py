"""Paired audit of the frozen robustness candidate; no fitting on these labels."""
import hashlib
import importlib.util
import itertools
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "work/robustness-optimization"
spec = importlib.util.spec_from_file_location("prior_audit", ROOT / "scripts/audit-energy-commitment.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    baseline = ROOT / "work/deck-outcome-optimization/community-after.json"
    before = read(baseline)["rows"]
    after_doc = read(OUT / "shared-community-after.json")
    after = after_doc["rows"]
    identity = lambda r: (r["group"], r["floor"], r["character"], r["cards"], r["picked"], r["size"])
    assert len(before) == len(after) and all(identity(b) == identity(a) for b, a in zip(before, after))
    runs = {p.stem: read(p) for p in (ROOT / "work/spire-codex/runs-full").glob("*.json")}

    def accepted(row):
        node = list(itertools.chain.from_iterable(runs[row["run"]]["map_point_history"]))[row["floor"]-1]
        rooms = {r["room_type"] for r in node["rooms"]}
        return bool(rooms) and rooms <= {"monster", "elite", "boss"} and row["alliedDecksKnown"]

    pairs = [(b, a) for b, a in zip(before, after) if accepted(a)]
    groups = {"all": pairs}
    for field in ["split", "character", "seats", "version"]:
        for value in sorted({a[field] for _, a in pairs}):
            groups[f"{field}:{value}"] = [(b, a) for b, a in pairs if a[field] == value]
    changed = [dict(run=a["run"], floor=a["floor"], character=a["character"], cards=a["cards"],
        picked=a["picked"], before=b["predicted"], after=a["predicted"],
        before_values=b["values"], after_values=a["values"], features=a["features"])
        for b, a in pairs if b["predicted"] != a["predicted"]]
    (OUT / "changed-choices.json").write_text(json.dumps(changed, indent=2), encoding="utf-8")
    prepared = read(OUT / "shared-prepared-after.json")["rows"]
    shops = read(OUT / "shop-shared-anchor.json")
    visits = [dict(seat=m[0], visit=int(m[1]), gold=int(m[2]), card=m[3], price=int(m[4]))
        for m in re.findall(r"SHOP VISIT seat=(\d+) visit=(\d+) gold=(\d+) removal=([A-Z_]+):[\d.]+ price=(\d+)",
            (OUT / "shop-shared.log").read_text(encoding="utf-8-sig"))]
    sequences=[]
    for row in shops:
        if row.get("probe") != "six-shop-sensitivity":
            continue
        sequence=[v for v in visits if v["seat"] == row["seat"]]
        removed=[]
        for i, visit in enumerate(sequence):
            next_gold=sequence[i+1]["gold"]-150 if i+1<len(sequence) else row["gold"]
            if visit["gold"]-next_gold == visit["price"]:
                removed.append(visit["card"])
        assert len(removed)==row["removals"]
        sequences.append(dict(seat=row["seat"], removed=removed))
    cards = {}
    for card in ["PREPARED", "ANTICIPATE", "BULLY", "BLUDGEON"]:
        offered = [(b, a) for b, a in pairs if card in a["cards"]]
        cards[card] = dict(offered=len(offered),
            human=sum(a["picked"] == a["cards"].index(card) for _, a in offered),
            before=sum(b["predicted"] == b["cards"].index(card) for b, _ in offered),
            after=sum(a["predicted"] == a["cards"].index(card) for _, a in offered))
    intermediate=read(OUT / "pre-shared-community.json")["rows"]
    assert all(identity(b)==identity(a) for b,a in zip(intermediate,after))
    shared_pairs=[(b,a) for b,a in zip(intermediate,after) if accepted(a)]
    report = dict(projected=len(after), included=len(pairs),
        parameters=dict(risk=after_doc["riskWeight"], combatScale=after_doc["combatScale"],
            growth=after_doc["forecastWeight"], a10_lambda=[.80, .90, 1.0], stress_worst_share=.65),
        groups={k: audit.metrics(v, lambda b, a: a["predicted"]) for k, v in groups.items()},
        shared_status_comparison={str(seats):audit.metrics([(b,a) for b,a in shared_pairs if seats==0 or a["seats"]==seats],lambda b,a:a["predicted"])
            for seats in [0,1,4]},
        card_anchors=cards,
        shop_frequency=[r["removals"] for r in shops if r.get("probe")=="six-shop-sensitivity"],
        shop_removals_from_cash=sequences,
        historical_shop_choices=[r for r in shops if "probe" not in r],
        prepared_diagnostics=[dict(seats=r["seats"], scenario=r["scenario"], values=r["values"],
            mechanism=r["mechanism"], controls=r["controls"], shop=r["shop"]) for r in prepared],
        limitations=["Selected early A10 cache; reward agreement is not survival or win rate.",
            "The prior holdout has already been inspected; it is not a fresh independent test set.",
            "No live climb was performed. Long Boss fights and exact shuffle/trigger chains remain approximations.",
            "Lambda is a survival preference fixed before this comparison, not a fitted encounter probability."])
    (OUT / "regression-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    inputs = [baseline, OUT / "shared-community-after.json", OUT / "shared-prepared-after.json", Path(__file__),
        OUT / "shared-final-native/CoopBots.dll", OUT / "shared-final-native/CoopBots.Kernel.dll", OUT / "shared-final-native/PatchSmoke.dll",
        OUT / "release-native/CoopBots.dll", OUT / "release-native/CoopBots.Kernel.dll", OUT / "release-native/PatchSmoke.dll",
        OUT / "pre-shared-community.json", ROOT / "tests/PatchSmoke/PreparedAnticipateAudit.cs",
        ROOT / "tests/PatchSmoke/UnifiedCommunityRegression.cs", OUT / "shop-shared-anchor.json", OUT / "shop-shared.log",
        OUT / "bully-shared.json", ROOT / "src/CoopBots/BotShopPlanner.cs", ROOT / "tests/PatchSmoke/SharedStatusScenarios.cs"]
    inputs += list((ROOT / "src/CoopBots/Building").glob("*.cs"))
    inputs += [ROOT / "src/CoopBots.Kernel/CardResourceTiming.cs", ROOT / "tests/PatchSmoke/RobustnessScenarios.cs"]
    inputs += sorted((ROOT / "work/spire-codex/runs-full").glob("*.json"))
    (OUT / "input-sha256.json").write_text(json.dumps({p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
        for p in inputs}, indent=2), encoding="utf-8")
    print(json.dumps({"groups": report["groups"], "cards": cards}, indent=2))


if __name__ == "__main__":
    main()
