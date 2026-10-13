"""Retrospective paired audit; native probe outputs remain untouched.

Development-grid calibration uses reward labels plus the owner's shop anchor.
The earlier holdout is disclosed as previously inspected, not independent proof.
"""
import importlib.util
import itertools
import json
import pathlib
import hashlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "work/deck-outcome-optimization"
spec = importlib.util.spec_from_file_location("audit", ROOT / "scripts/audit-energy-commitment.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def main():
    before = audit.read(OUT / "community-before.json")["rows"]
    after_doc = audit.read(OUT / "community-after.json")
    after = after_doc["rows"]
    old_scale = after_doc.get("combatScale", 1)
    old_growth = after_doc.get("forecastWeight", .5)
    assert len(before) == len(after)
    key = lambda r: (r["group"], r["floor"], r["character"], r["cards"], r["picked"], r["size"])
    assert all(key(b) == key(a) for b, a in zip(before, after))
    runs = {p.stem: audit.read(p) for p in (ROOT / "work/spire-codex/runs-full").glob("*.json")}
    def accepted(a):
        node = list(itertools.chain.from_iterable(runs[a["run"]]["map_point_history"]))[a["floor"] - 1]
        rooms = {r["room_type"] for r in node["rooms"]}
        return bool(rooms) and rooms <= {"monster", "elite", "boss"} and a["alliedDecksKnown"]
    pairs = [(b, a) for b, a in zip(before, after) if accepted(a)]
    changed = []
    for old, new in pairs:
        if old["predicted"] == new["predicted"]:
            continue
        def card_name(index):
            return "SKIP" if index < 0 else new["cards"][index]
        changed.append(dict(character=new["character"], run=new["run"], floor=new["floor"],
            human=card_name(new["picked"]), before=card_name(old["predicted"]), after=card_name(new["predicted"]),
            effect="lost" if old["predicted"] == new["picked"] else "gained" if new["predicted"] == new["picked"] else "different_mismatch",
            cards=new["cards"], before_values=old["values"], after_values=new["values"], features=new["features"]))
    (OUT / "changed-choices.json").write_text(json.dumps(changed, indent=2), encoding="utf-8")
    def prediction(a, risk, growth, scale):
        return audit.best([v if a["guards"][i] else v + a["features"][i]["risk"] - risk * a["commitmentLoad"][i]
                           + (scale - old_scale) * (.65 * a["features"][i]["cycle"] + .35 * a["features"][i]["hand"])
                           + (scale / old_scale * growth / old_growth - 1) * a["features"][i]["growth"] for i, v in enumerate(a["values"])])
    dev = [(b, a) for b, a in pairs if a["split"] == "development"]
    grid = [dict(risk=r, growth=g, scale=s, **audit.metrics(dev, lambda b, a: prediction(a, r, g, s)))
            for s in [1, 1.25, 1.5, 2] for g in [.25, .5] for r in [4, 8, 12, 16, 20, 24, 32]]
    groups = {"all": pairs}
    for field in ["split", "seats", "character", "version"]:
        for v in sorted({a[field] for _, a in pairs}):
            groups[f"{field}:{v}"] = [(b, a) for b, a in pairs if a[field] == v]
    shop_source = (ROOT / "src/CoopBots/BotShopPlanner.cs").read_text(encoding="utf-8-sig")
    gold_scale = float(re.search(r"GoldPerDeckValue = ([0-9.]+)", shop_source).group(1))
    anchors = audit.read(OUT / "bully-anchor.json")
    shops = audit.read(OUT / "shop-anchor.json")
    # The fixture logs each pre-purchase purse and final purse. Its only card
    # costs 60; removal starts at 100. Reconstruct executed removals, rather
    # than counting every best-removal candidate printed in the log.
    shop_log = (OUT / "shop-acceptance.log").read_text(encoding="utf-8-sig")
    visits = [dict(seat=s, visit=int(v), gold=int(g), card=c, price=int(p))
              for s, v, g, c, p in re.findall(
                  r"SHOP VISIT seat=(\d+) visit=(\d+) gold=(\d+) removal=([A-Z_]+):[\d.]+ price=(\d+)", shop_log)]
    sequences = []
    for row in shops:
        if row.get("probe") != "six-shop-sensitivity":
            continue
        chain = [v for v in visits if v["seat"] == row["seat"]]
        removed = [v["card"] for i, v in enumerate(chain)
                   if v["gold"] - (chain[i + 1]["gold"] - 150 if i + 1 < len(chain) else row["gold"]) == v["price"]]
        assert len(removed) == row["removals"], "cash reconstruction must agree with executed removal count"
        sequences.append(dict(seat=row["seat"], removed=removed))
    report = dict(projected=len(after), included=len(pairs),
        chosen_parameters=dict(risk=after_doc["riskWeight"], scale=old_scale, growth=old_growth, gold=gold_scale),
        shop_anchor=[dict(seats=r["seats"], withRemoval=r["withRemoval"], withoutBully=r["withoutBully"])
                     for r in anchors if r["energy"] == 3 and r["support"] == "starter"],
        shop_frequency=[r["removals"] for r in shops if r.get("probe") == "six-shop-sensitivity"],
        shop_removals_from_cash=sequences,
        groups={name: audit.metrics(rows, lambda b, a: a["predicted"]) for name, rows in groups.items()},
        development_grid=sorted(grid, key=lambda r: (-r["run_weighted_agreement"], r["risk"], r["growth"])),
        limitations=["Selected early A10 cache, not a random population.",
            "Reward agreement is not causal win rate; no live climb was performed.",
            "The previous holdout was inspected in earlier iterations.",
            "Character pool forecasts use community priors that may overlap this dataset.",
            "Unknown allied histories and non-battle choices excluded.",
            "HP, relics and actual encounters were not reconstructed."])
    (OUT / "regression-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    paths = [OUT / "community-before.json", OUT / "community-after.json", OUT / "bully-anchor.json",
             OUT / "shop-anchor.json", OUT / "shop-acceptance.log", OUT / "r2-results.json",
             OUT / "changed-choices.json", pathlib.Path(__file__)]
    paths += list((ROOT / "src/CoopBots/Building").glob("*.cs"))
    paths += [ROOT / "src/CoopBots/BotShopPlanner.cs", ROOT / "src/CoopBots/RestSitePatches.cs",
              ROOT / "tests/PatchSmoke/UnifiedCommunityRegression.cs",
              ROOT / "tests/PatchSmoke/BullyPriceAnchorScenarios.cs",
              ROOT / "tests/PatchSmoke/ShopRemovalAnchorScenarios.cs",
              ROOT / "tests/PatchSmoke/ConstructionOutcomeScenarios.cs",
              ROOT / "tests/PatchSmoke/Fixtures/unified-shops.json",
              ROOT / "work/unified-deck-strength/latest-godot.log", OUT / "r2.py",
              OUT / "finale-native/CoopBots.dll", OUT / "finale-native/PatchSmoke.dll"]
    paths += sorted((ROOT / "work/spire-codex/runs-full").glob("*.json"))
    (OUT / "input-sha256.json").write_text(json.dumps({str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                                                    for p in paths}, indent=2), encoding="utf-8")
    print(json.dumps({"all": report["groups"]["all"], "development_grid": [
        {k: r[k] for k in ["risk", "growth", "scale", "agreement", "run_weighted_agreement", "skip"]}
        for r in report["development_grid"]]}, indent=2))


if __name__ == "__main__":
    main()
