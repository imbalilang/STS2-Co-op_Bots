#!/usr/bin/env python3
"""Which real decks can the oracle express, and are the rest a different population?

Dispatch 31. The oracle's documented limit (after the dedup limitation was
withdrawn as a misreading) is that a card must belong to the character's own pool
or the colorless pool. A real deck can pick up another character's card from an
event, and such a deck cannot be fed to the tool at all.

The count matters less than the bias. If decks that reached a cross-character
card are systematically bigger, better upgraded or more likely to win, then
"evaluate on the expressible subset" silently selects a different population,
and every reading taken on it inherits that. So this reports the two groups side
by side rather than only the headline fraction.

Two definitions are reported separately, because they are not the same claim:

  foreign      a card from *another character's* pool -- the stated mechanism
  off-pool     any card that is neither the character's nor colorless
               (event / curse / status / token / quest), which is stricter and
               may or may not be what the tool enforces

Usage:
    python scripts/analyze-expressibility.py --players 1
"""
import argparse
import collections
import glob
import io
import json
import statistics as st

DECKS = "work/spire-codex/decks/runs"
CARDS = "work/spire-codex/cards/cards.json"
CHARACTER_COLORS = {"IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"}
CHARS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--players", type=int, default=1)
    args = ap.parse_args()

    cards = {c["id"]: c for c in json.load(io.open(CARDS, encoding="utf-8"))}
    decks = []
    for path in glob.glob(DECKS + "/*.json"):
        d = json.load(io.open(path, encoding="utf-8"))
        if d.get("ascension") == 10 and d.get("players") == args.players and d.get("deck"):
            decks.append(d)
    print(f"{len(decks)} A10 decks at {args.players} players")
    print(f"  {sum(1 for d in decks if d['win'])} winners / "
          f"{sum(1 for d in decks if not d['win'])} losers (all losses, not only deep)")

    rows = []
    for d in decks:
        ids = [e["id"] for e in d["deck"]]
        own = d["character"].lower()
        unknown = [i for i in ids if i not in cards]
        foreign, offpool = set(), set()
        for i in ids:
            c = cards.get(i)
            if c is None:
                continue
            col = (c.get("color") or "").lower()
            if col in {x.lower() for x in CHARACTER_COLORS} and col != own:
                foreign.add(i)
            elif col != own and col != "colorless":
                offpool.add(i)
        rows.append({
            "hash": d["run_hash"], "char": d["character"], "win": bool(d["win"]),
            "size": len(ids),
            "upg": sum(1 for e in d["deck"] if e.get("upgrades", 0) > 0),
            "starter": sum(1 for i in ids if (cards.get(i) or {}).get("rarity") == "Basic"),
            "foreign": sorted(foreign), "offpool": sorted(offpool - foreign),
            "unknown": unknown,
            "killed": (d.get("killed_by") or "").split(".")[-1],
        })

    idx = {r["hash"]: r for r in rows}
    n = len(rows)
    print()
    print("=" * 78)
    print("1. how many decks the oracle can express")
    print("=" * 78)
    for label, key in (("has a card from ANOTHER CHARACTER's pool", "foreign"),
                       ("has any non-character, non-colorless card", None)):
        if key:
            bad = [r for r in rows if r[key]]
        else:
            bad = [r for r in rows if r["offpool"]]
        print(f"  {label:52} {len(bad):>4}/{n} inexpressible "
              f"({100*len(bad)/n:.0f}%), {n-len(bad)} usable")
    strict = [r for r in rows if not r["foreign"] and not r["offpool"]]
    print(f"  {'strict union (foreign OR off-pool)':52} {n-len(strict):>4}/{n} "
          f"({100*(n-len(strict))/n:.0f}%), {len(strict)} usable")
    unk = [r for r in rows if r["unknown"]]
    if unk:
        print(f"  {'has a card this build does not know':52} {len(unk):>4}/{n}")

    print()
    print("=" * 78)
    print("2. bias: are the inexpressible decks a different population?")
    print("=" * 78)
    for label, key in (("foreign-card decks", "foreign"), ("off-pool decks", "offpool")):
        bad = [r for r in rows if r[key]]
        good = [r for r in rows if not r[key]]
        if not bad or not good:
            continue
        print(f"\n  --- {label} ---   n: {len(bad)} inexpressible vs {len(good)} expressible")
        for f, name in (("size", "deck size"), ("upg", "upgrades"),
                        ("starter", "starter cards")):
            print(f"    {name:16} inexpressible {st.mean(r[f] for r in bad):>6.1f}"
                  f"   expressible {st.mean(r[f] for r in good):>6.1f}"
                  f"   diff {st.mean(r[f] for r in bad)-st.mean(r[f] for r in good):>+6.2f}")
        wr_b = sum(1 for r in bad if r["win"]) / len(bad)
        wr_g = sum(1 for r in good if r["win"]) / len(good)
        print(f"    {'win rate':16} inexpressible {100*wr_b:>5.1f}%   expressible {100*wr_g:>5.1f}%"
              f"   diff {100*(wr_b-wr_g):>+5.1f}pt")

    print()
    print("=" * 78)
    print("3. by character")
    print("=" * 78)
    print(f"{'character':14}{'n':>5}{'foreign':>9}{'usable':>8}{'foreign%':>10}{'win%':>7}{'size':>7}{'upg':>6}")
    for c in CHARS:
        sel = [r for r in rows if r["char"] == c]
        if not sel:
            continue
        nb = sum(1 for r in sel if r["foreign"])
        no = sum(1 for r in sel if r["offpool"])
        # usable = foreign-free. The stricter off-pool test excludes every real
        # deck (Ascender's Bane alone is a curse), so it cannot be the rule.
        us = sum(1 for r in sel if not r["foreign"])
        print(f"{c:14}{len(sel):>5}{nb:>9}{us:>8}{100*nb/len(sel):>9.0f}%"
              f"{100*sum(1 for r in sel if r['win'])/len(sel):>6.1f}%"
              f"{st.mean(r['size'] for r in sel):>7.1f}{st.mean(r['upg'] for r in sel):>6.1f}")

    print()
    print("=" * 78)
    print("4. which foreign cards show up, and how often")
    print("=" * 78)
    fc = collections.Counter(i for r in rows for i in r["foreign"])
    for i, k in fc.most_common(12):
        print(f"  {i:22} in {k:>3} decks   (color={cards[i].get('color')})")
    if not fc:
        print("  none")

    print()
    print("  caveat: 'off-pool' folds event / curse / status / token / quest colours")
    print("  together. Ascender's Bane is handled by a separate switch, so counting")
    print("  it as off-pool may overstate the restriction. The foreign count is the")
    print("  one the stated mechanism is about, and is reported separately for that")
    print("  reason.")


if __name__ == "__main__":
    main()
