#!/usr/bin/env python3
"""How much does collapsing duplicate cards change a real deck?

Dispatch 27. The oracle tool requires explicit card ids to be unique, so a real
deck with two ACCURACY cannot be expressed and silently becomes one. The number
looks like "six cards lost" on a 28-card deck; what it actually changes is the
deck's nature, because duplicates are not decoration in this game:

  * 1 vs 2 ACCURACY is a tier apart for the shiv package — the multiplier count
    is what the odds-ratio ladder turns on
  * the starter Strikes and Defends are duplicates by construction, so the share
    of the deck that is starter changes with dedup
  * one player's whole build can be "hold two of the payoff"

So this measures three things per deck: the raw card loss, the package-state
change (using BakedResources, the only authoritative P/M/R table), and whether
the deck is untouched at all. The last one decides whether "evaluate only on
lossless decks" is a viable fallback, so the lossless run_hash list is written
out for the simulator to target directly.

Usage:
    python scripts/analyze-dedup-impact.py --players 4
"""
import argparse
import collections
import glob
import io
import json
import os
import statistics as st

DECKS = "work/spire-codex/decks/runs"
CARDS = "work/spire-codex/cards/cards.json"
BAKED = "src/CoopBots/Building/BakedResources.cs"
OUT_LIST = "outputs/decksim-analysis/dedup-lossless-runs.txt"
CHARS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]


def parse_baked():
    import re
    if not os.path.exists(BAKED):
        return {}
    text = io.open(BAKED, encoding="utf-8").read()
    out = {}
    for m in re.finditer(r'new\("([a-z-]+)",\s*"[^"]*",\s*\[(.*?)\],\s*\[(.*?)\],\s*\[(.*?)\],\s*\[(.*?)\],\s*\[(.*?)\]\)',
                         text, re.S):
        name, _owners, producers, mult, repl, payoffs = m.groups()
        grab = lambda s: [x.strip().strip('"') for x in s.split(",") if x.strip()]
        out[name] = {"P": grab(producers), "M": grab(mult), "R": grab(repl)}
    return out


PMIN = 3


def package_state(counts, table):
    """Which resources are stocked / cashed / complete, given a card multiset.

    The same definitions as the ruled `complete` / `fragmented`: stock is P>=3,
    cash is M or R non-empty, and both must be defined for the resource to count.
    """
    state = {}
    for name, spec in table.items():
        if not (spec["M"] or spec["R"]):
            continue  # stock XOR cash would degenerate; excluded, per ruling 25
        p = sum(counts[i] for i in spec["P"])
        m = sum(counts[i] for i in spec["M"])
        r = sum(counts[i] for i in spec["R"])
        state[name] = (p >= PMIN, (m >= 1) or (r >= 1))
    return state


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--players", type=int, default=4)
    args = ap.parse_args()

    cards = {c["id"]: c for c in json.load(io.open(CARDS, encoding="utf-8"))}
    table = parse_baked()
    decks = []
    for path in glob.glob(os.path.join(DECKS, "*.json")):
        d = json.load(io.open(path, encoding="utf-8"))
        if d.get("ascension") == 10 and d.get("players") == args.players and d.get("deck"):
            decks.append(d)
    winners = [d for d in decks if d["win"]]
    print(f"{len(decks)} A10 decks at {args.players} players "
          f"({len(winners)} winners, {len(decks)-len(winners)} deep losses)")
    print(f"BakedResources: {len(table)} resources, "
          f"{sum(1 for v in table.values() if v['M'] or v['R'])} with M or R defined\n")

    rows, dup_counter, losses_missing = [], collections.Counter(), []
    for d in winners:
        ids = [e["id"] for e in d["deck"]]
        counts = collections.Counter(ids)
        uniq = len(counts)
        lost = len(ids) - uniq
        ups_total = sum(1 for e in d["deck"] if e.get("upgrades", 0) > 0)
        # the deduped representation keeps one copy per id, so upgrades survive
        # only on the copies that survive; report the optimistic reading
        ups_dedup = sum(1 for i in counts if any(
            e.get("upgrades", 0) > 0 for e in d["deck"] if e["id"] == i))
        for i, n in counts.items():
            if n > 1:
                dup_counter[i] += n - 1
        before = package_state(counts, table)
        # every unknown card is dropped by the tool, so count that too
        unknown = [i for i in counts if i not in cards]
        losses_missing += unknown
        after = package_state(collections.Counter(counts), table)  # same; see below
        rows.append({
            "hash": d["run_hash"], "char": d["character"],
            "size": len(ids), "uniq": uniq, "lost": lost,
            "pct_lost": lost / len(ids) if ids else 0,
            "ups_total": ups_total, "ups_dedup": ups_dedup,
            "starter": sum(counts[i] for i in counts if (cards.get(i) or {}).get("rarity") == "Basic"),
            "state_before": before, "unknown": len(unknown),
        })

    # how the package state changes when duplicates collapse to one copy
    # (a card counted twice can satisfy a threshold that one copy cannot)
    def state_after(counts, table):
        collapsed = collections.Counter({i: 1 for i in counts})
        return package_state(collapsed, table)

    changed = 0
    for d, r in zip(winners, rows):
        counts = collections.Counter(e["id"] for e in d["deck"])
        r["state_after"] = state_after(counts, table)
        if r["state_before"] != r["state_after"]:
            changed += 1

    print("=" * 78)
    print("1. raw card loss from dedup, by character (winners only)")
    print("=" * 78)
    print(f"{'character':14}{'n':>4}{'size':>7}{'unique':>8}{'lost':>7}{'%lost':>8}"
          f"{'starter':>9}{'upgTotal':>10}{'upgDedup':>10}{'lossless':>10}")
    for c in CHARS:
        sel = [r for r in rows if r["char"] == c]
        if not sel:
            continue
        n_lossless = sum(1 for r in sel if r["lost"] == 0)
        print(f"{c:14}{len(sel):>4}{st.mean(r['size'] for r in sel):>7.1f}"
              f"{st.mean(r['uniq'] for r in sel):>8.1f}"
              f"{st.mean(r['lost'] for r in sel):>7.2f}"
              f"{100*st.mean(r['pct_lost'] for r in sel):>7.1f}%"
              f"{st.mean(r['starter'] for r in sel):>9.2f}"
              f"{st.mean(r['ups_total'] for r in sel):>10.1f}"
              f"{st.mean(r['ups_dedup'] for r in sel):>10.1f}"
              f"{n_lossless:>7}/{len(sel)}")
    print(f"{'ALL':14}{len(rows):>4}{st.mean(r['size'] for r in rows):>7.1f}"
          f"{st.mean(r['uniq'] for r in rows):>8.1f}"
          f"{st.mean(r['lost'] for r in rows):>7.2f}"
          f"{100*st.mean(r['pct_lost'] for r in rows):>7.1f}%"
          f"{st.mean(r['starter'] for r in rows):>9.2f}"
          f"{st.mean(r['ups_total'] for r in rows):>10.1f}"
          f"{st.mean(r['ups_dedup'] for r in rows):>10.1f}"
          f"{sum(1 for r in rows if r['lost']==0):>7}/{len(rows)}")

    print()
    print("=" * 78)
    print("2. the cards dedup destroys most (winners, copies beyond the first)")
    print("=" * 78)
    for i, n in dup_counter.most_common(15):
        c = cards.get(i, {})
        print(f"  {i:24} {n:>4} copies lost   {c.get('rarity','?')}/{c.get('type','?')}")

    print()
    print("=" * 78)
    print("3. does dedup change what the deck IS? (package state, BakedResources)")
    print("=" * 78)
    print(f"  decks whose stock/cash state changes when duplicates collapse: "
          f"{changed}/{len(rows)} ({100*changed/len(rows):.0f}%)")
    tier_break = []
    for d, r in zip(winners, rows):
        for name in r["state_before"]:
            sb, sa = r["state_before"][name], r["state_after"][name]
            if sb != sa:
                tier_break.append((r["char"], name, sb, sa))
    per = collections.Counter(t[1] for t in tier_break)
    print(f"  packages affected: {dict(per.most_common(8))}")
    for ch, name, sb, sa in tier_break[:8]:
        print(f"    {ch:12} {name:8} stock/cash {sb} -> {sa}")

    print()
    print("=" * 78)
    print("4. is 'evaluate only on lossless decks' viable?")
    print("=" * 78)
    lossless = [r for r in rows if r["lost"] == 0]
    print(f"  lossless winners: {len(lossless)}/{len(rows)} = {100*len(lossless)/len(rows):.1f}%")
    for c in CHARS:
        sel = [r for r in rows if r["char"] == c]
        ll = [r for r in sel if r["lost"] == 0]
        if sel:
            print(f"    {c:14} {len(ll):>3}/{len(sel):<3} = {100*len(ll)/len(sel):>5.1f}%")
    if lossless:
        with io.open(OUT_LIST, "w", encoding="utf-8") as h:
            for r in sorted(lossless, key=lambda x: x["hash"]):
                h.write(r["hash"] + "\n")
        print(f"\n  run_hash list written -> {OUT_LIST} ({len(lossless)} decks)")
    else:
        print("\n  NO lossless decks: plan B (evaluate on lossless only) is not viable.")

    print()
    print("  caveat: this measures the DECK. A deck can lose 6 cards and keep its")
    print("  package state intact, and still play differently, because the cards")
    print("  lost are the second copy of the payoff. Package state is a coarse")
    print("  detector of 'did its nature change', not a proof that it did not.")


if __name__ == "__main__":
    main()
