#!/usr/bin/env python3
"""Does the 4-player A10 winner's deck actually look different from a loser's?

Inputs (all produced by the sibling fetch scripts, all local):
  work/spire-codex/decks/runs/*.json   finished decks + party size + win/loss
  work/spire-codex/runs-full/*.json    full run detail: per-player damage_taken,
                                       and every reward screen with what was picked
                                       AND what was passed over
  work/spire-codex/cards/cards.json    card metadata (type, rarity, cost, damage,
                                       block, multiplayer_only); fetch once with
                                       scripts/fetch-spire-codex-cards.py

Why the run detail matters: a finished deck list can only say "this card is in
winning decks", which is survivorship, not evidence — the card may be there
because the run was already going well. A card_choices entry is a decision made
at the time, with the alternatives recorded, so it can say "winners took this
and passed that" instead.

Caveats this script prints rather than hides:
  * Cells are not a random sample. The crawler caps per (character, win/loss),
    so cross-character win RATES are meaningless. Only within-cell deck features
    and pick rates are read.
  * Pick rates are conditional on being offered, which is itself not random
    (reward odds, relics, ascension).
  * Cards are compared without controlling for deck context, so a card that is
    good in one build reads as good in all of them.

Usage:
    python scripts/analyze-mp-decks.py                 # the whole report
    python scripts/analyze-mp-decks.py --players 4
"""
import argparse
import collections
import glob
import io
import json
import math
import os
import statistics as st

DECKS = "work/spire-codex/decks/runs"
FULL = "work/spire-codex/runs-full"
CARDS = "work/spire-codex/cards/cards.json"
CHARS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]


def load_cards():
    if not os.path.exists(CARDS):
        raise SystemExit(f"missing {CARDS} — fetch it with scripts/fetch-spire-codex-cards.py")
    return {c["id"]: c for c in json.load(io.open(CARDS, encoding="utf-8"))}


def load_decks(build=None):
    """Finished decks, restricted to one game build.

    Mixing builds mixes card pools, so a deck feature that moved between patches
    would read as a winner/loser difference. `build=None` keeps only the build
    the cache holds most of, which is the newest one the crawler reached.
    """
    out = []
    for path in glob.glob(os.path.join(DECKS, "*.json")):
        d = json.load(io.open(path, encoding="utf-8"))
        if d.get("ascension") == 10 and d.get("players") and d.get("deck"):
            out.append(d)
    if build is None:
        build = collections.Counter(d.get("build_id") for d in out).most_common(1)[0][0]
    print(f"build filter: {build} ({sum(1 for d in out if d.get('build_id') == build)} of {len(out)} decks)")
    return [d for d in out if d.get("build_id") == build]


def load_runs():
    out = []
    for path in glob.glob(os.path.join(FULL, "*.json")):
        p = json.load(io.open(path, encoding="utf-8"))
        if p.get("players"):
            out.append(p)
    return out


def delta(card, key):
    u = (card.get("upgrade") or {}).get(key)
    try:
        return int(str(u).replace("+", "")) if u else 0
    except ValueError:
        return 0


def features(deck, cards):
    """Composition of one finished deck, from its card list plus metadata."""
    ids = [e["id"] for e in deck["deck"]]
    ups = {e["id"]: e.get("upgrades", 0) for e in deck["deck"]}
    dmg = blk = cost = 0.0
    n_cost = basics = common = rare = powers = skills = attacks = 0
    for cid, n in collections.Counter(ids).items():
        c = cards.get(cid)
        if not c:
            continue
        u = ups.get(cid, 0)
        dmg += ((c.get("damage") or 0) + (delta(c, "damage") if u else 0)) * (c.get("hit_count") or 1) * n
        blk += ((c.get("block") or 0) + (delta(c, "block") if u else 0)) * n
        if isinstance(c.get("cost"), int):
            cost += c["cost"] * n
            n_cost += n
        if c.get("rarity") == "Basic":
            basics += n
        if c.get("rarity") == "Common":
            common += n
        if c.get("rarity") == "Rare":
            rare += n
        if c.get("type") == "Power":
            powers += n
        elif c.get("type") == "Skill":
            skills += n
        elif c.get("type") == "Attack":
            attacks += n
    size = len(ids)
    return {
        "size": size, "basics": basics, "common": common, "rare": rare,
        "powers": powers, "skills": skills, "attacks": attacks,
        "dmgpc": dmg / size, "blkpc": blk / size,
        "avgcost": cost / n_cost if n_cost else 0,
        "skillratio": skills / max(1, attacks),
        "upg": sum(1 for e in deck["deck"] if e.get("upgrades", 0) > 0),
    }


def section(title):
    print()
    print("=" * 78)
    print(title)
    print("=" * 78)


def boss_kills(decks, cards):
    section("which act-3 boss ends a run, by party size")
    by = collections.defaultdict(collections.Counter)
    for d in decks:
        if d["win"]:
            continue
        boss = (d.get("killed_by") or "").split(".")[-1]
        by[d["players"]][boss] += 1
    print(f"{'party':>6}{'n losses':>10}   distribution")
    for party in sorted(by):
        total = sum(by[party].values())
        top = ", ".join(f"{k} {v} ({100*v/total:.0f}%)" for k, v in by[party].most_common(4))
        print(f"{party:>6}{total:>10}   {top}")


def robust_separators(decks, cards, party):
    section(f"{party}-player A10: which deck features separate winners from losers")
    print("median per character, and how many of the characters agree in sign")
    keys = ["size", "skillratio", "blkpc", "dmgpc", "basics", "powers", "upg", "rare", "avgcost"]
    print(f"{'feature':13}" + "".join(f"{c[:6]:>9}" for c in CHARS) + f"{'agree':>8}")
    for key in keys:
        row, diffs = [], []
        for ch in CHARS:
            W = [features(d, cards)[key] for d in decks if d["character"] == ch and d["players"] == party and d["win"]]
            L = [features(d, cards)[key] for d in decks if d["character"] == ch and d["players"] == party and not d["win"]]
            if len(W) < 5 or len(L) < 5:
                row.append(None)
                continue
            mw, ml = st.median(W), st.median(L)
            row.append(mw - ml)
            diffs.append(mw - ml)
        agree = sum(1 for x in diffs if x > 0)
        cells = "".join(f"{v:>+9.2f}" if v is not None else f"{'-':>9}" for v in row)
        print(f"{key:13}{cells}{f'{agree}/{len(diffs)}':>8}")
    print("'agree' = in how many characters winners read higher.")


def choice_lift(runs, cards, party, min_w, min_l):
    section(f"{party}-player reward screens: skip rate and pick lift (decision-level)")
    D = {}

    def cell(k):
        if k not in D:
            D[k] = {"screens": 0, "skips": 0, "offers": collections.Counter(), "picks": collections.Counter()}
        return D[k]

    for p in runs:
        ps = p["players"]
        if len(ps) != party:
            continue
        win = bool(p.get("win"))
        chars = {i: (pl.get("character") or "").replace("CHARACTER.", "") for i, pl in enumerate(ps)}
        by_id = {pl.get("id"): i for i, pl in enumerate(ps)}
        for act in p.get("map_point_history") or []:
            for node in act:
                for s in node.get("player_stats") or []:
                    idx = by_id.get(s.get("player_id"))
                    if idx is None:
                        continue
                    opts = s.get("card_choices") or []
                    if not opts:
                        continue
                    g = cell((chars[idx], win))
                    g["screens"] += 1
                    if not any(o.get("was_picked") for o in opts):
                        g["skips"] += 1
                    for o in opts:
                        cid = ((o.get("card") or {}).get("id") or "").replace("CARD.", "")
                        if not cid:
                            continue
                        g["offers"][cid] += 1
                        if o.get("was_picked"):
                            g["picks"][cid] += 1

    print(f"{'character':14}{'win scr':>9}{'skip%':>8}{'loss scr':>10}{'skip%':>8}")
    for ch in CHARS:
        w, l = D.get((ch, True)), D.get((ch, False))
        if not w or not l:
            continue
        print(f"{ch:14}{w['screens']:>9}{100*w['skips']/max(1,w['screens']):>7.1f}%"
              f"{l['screens']:>10}{100*l['skips']/max(1,l['screens']):>7.1f}%")

    for ch in CHARS:
        w, l = D.get((ch, True)), D.get((ch, False))
        if not w or not l:
            continue
        rows = []
        for cid, off in w["offers"].items():
            if off < min_w:
                continue
            loff = l["offers"].get(cid, 0)
            if loff < min_l:
                continue
            wr, lr = w["picks"][cid] / off, l["picks"][cid] / loff
            rows.append((wr - lr, cid, off, w["picks"][cid], loff, l["picks"][cid], wr, lr))
        if len(rows) < 8:
            print(f"\n--- {ch}: only {len(rows)} cards clear the offer floor; not enough to read")
            continue
        rows.sort(reverse=True)
        print(f"\n--- {ch} ---  offers/picks")
        for label, chunk in (("winners take:", rows[:6]), ("losers take:", rows[-6:])):
            print(f"  {label}")
            for _, cid, o, pi, lo, lp, wr, lr in chunk:
                m = cards.get(cid, {})
                print(f"    {cid:28} W {pi:>3}/{o:<3}({wr:>4.0%})  L {lp:>3}/{lo:<3}({lr:>4.0%})  "
                      f"{m.get('type','?')}/{m.get('rarity','?')}")


def incoming_share(runs, cards):
    section("per-player damage taken over a completed run, by party size")
    print("this is the measurement behind SimConfig.IncomingShare")
    by = collections.defaultdict(list)
    for p in runs:
        per = {}
        for act in p.get("map_point_history") or []:
            for node in act:
                for s in node.get("player_stats") or []:
                    pid = s.get("player_id")
                    per[pid] = per.get(pid, 0) + (s.get("damage_taken") or 0)
        for v in per.values():
            by[(len(p["players"]), bool(p.get("win")))].append(v)
    base = {}
    print(f"{'party':>6}{'win':>7}{'n':>6}{'median':>9}{'mean':>9}{'p25':>8}{'p75':>8}{'vs solo':>9}")
    for key in sorted(by):
        vals = by[key]
        if len(vals) < 8:
            continue
        med = st.median(vals)
        if key[1]:
            base[key[0]] = med
        b = base.get(1)
        print(f"{key[0]:>6}{str(key[1]):>7}{len(vals):>6}{med:>9.0f}{st.mean(vals):>9.0f}"
              f"{st.quantiles(vals, n=4)[0]:>8.0f}{st.quantiles(vals, n=4)[2]:>8.0f}"
              f"{(f'{med/b:.2f}' if b else '-'):>9}")
    print("\nmodels for the per-player share relative to solo:")
    for n in (2, 3, 4, 5, 6):
        print(f"  {n}p:  1/n = {1/n:.3f}   1/sqrt(n) = {1/math.sqrt(n):.3f}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--players", type=int, default=4)
    ap.add_argument("--min-offers-win", type=int, default=12)
    ap.add_argument("--min-offers-loss", type=int, default=8)
    args = ap.parse_args()

    cards = load_cards()
    decks = load_decks()
    runs = load_runs()
    print(f"loaded {len(decks)} finished decks and {len(runs)} full run details")

    boss_kills(decks, cards)
    robust_separators(decks, cards, args.players)
    incoming_share(runs, cards)
    choice_lift(runs, cards, args.players, args.min_offers_win, args.min_offers_loss)

    section("caveats")
    print("  * per (character, win/loss) cells are capped by the crawler, so cross-character")
    print("    win rates are meaningless; only within-cell features and pick rates are read")
    print("  * pick rates condition on being offered, which is not random")
    print("  * no deck-context control: a card good in one build reads as good in all")


if __name__ == "__main__":
    main()
