#!/usr/bin/env python3
"""Independent recomputation of the structural features' AUC, plus a gaming test.

Dispatched by the lead (order 3). The simulator owner added process features —
package/route count, convergence pick, dead-card share, burst — and reported
per-character AUC against real A10 win/loss. This script recomputes them from
`work/spire-codex/decks/runs/*.json` **without calling his code**, so the two
numbers are an actual cross-check: agreement makes the features credible, and a
disagreement localises the fault to whichever stage differs.

The detection spec is re-implemented here from a reading of the source, not
imported from it:
  * BakedArchetypes.All is parsed out of the C# array literal
  * roles are re-derived from cards.json metadata (the mod derives them from
    GeniusCombatStrategy.Analyze + card keywords/tags, which Python cannot call)
  * MatchClusters requires 2+ signature cards; MatchRoutes requires the payoff
    count to reach PayoffThreshold and the enabler count EnablerThreshold
  * clusters take precedence over routes, and strength uses
    (hits/4 + 0.2*min(1,relicHits)) * clamp(20/deckSize, 0.7, 1.3)

The role derivation is the one stage that is an approximation, and it is where a
divergence is most likely to originate. That is reported explicitly rather than
smoothed over.

Second half: a degenerate drafter. `power` was already shown to be buyable —
BlockWeight 1->6 took power 30->48 while the kill rate fell 30%->12%. Any feature
that a degenerate drafter can max out while drifting away from real winners is
unusable as a target, because it will steer the whole line the wrong way.

Usage:
    python scripts/analyze-feature-auc.py
    python scripts/analyze-feature-auc.py --players 4
"""
import argparse
import collections
import glob
import io
import json
import os
import re

DECKS = "work/spire-codex/decks/runs"
FULL = "work/spire-codex/runs-full"
CARDS = "work/spire-codex/cards/cards.json"
BAKED = "src/CoopBots/Building/BakedArchetypes.cs"


def load_cards():
    return {c["id"]: c for c in json.load(io.open(CARDS, encoding="utf-8"))}


def load_clusters():
    """Parse the generated C# table rather than re-deriving it from the API."""
    if not os.path.exists(BAKED):
        return []
    text = io.open(BAKED, encoding="utf-8").read()
    clusters = []
    # new("IRONCLAD", "Name", ["A", "B"], ["R"], 123)
    for m in re.finditer(r'new\("([^"]+)",\s*"([^"]*)",\s*\[(.*?)\],\s*\[(.*?)\],\s*(\d+)\)', text):
        character, name, cards, relics, size = m.groups()
        cards = [c.strip().strip('"') for c in cards.split(",") if c.strip()]
        relics = [r.strip().strip('"') for r in relics.split(",") if r.strip()]
        clusters.append((character.upper(), name, cards, relics, int(size)))
    return clusters


def text_of(card):
    return ((card.get("description_raw") or "") + " " + str(card.get("keywords") or "")
            + " " + str(card.get("tags") or "")).lower()


def roles_of(card):
    """Approximate CardProfile.Facts.Roles from the compendium metadata."""
    t = text_of(card)
    r = set()
    dmg = card.get("damage") or 0
    if dmg > 0 or "deal" in t:
        r.add("damage")
        if str(card.get("target") or "").lower() in ("all", "allenemies", "all enemies"):
            r.add("aoe")
    if (card.get("block") or 0) > 0 or "block" in t:
        r.add("block")
    if (card.get("cards_draw") or 0) > 0 or "draw" in t:
        r.add("draw")
    if (card.get("energy_gain") or 0) > 0 or "energy" in t:
        r.add("energy")
    if "vulnerable" in t:
        r.add("vulnerable")
    if re.search(r"\bweak\b", t):
        r.add("weak")
    if "loses" in t and "strength" in t:
        r.add("strengthdown")
    if "poison" in t:
        r.add("poison")
    if "doom" in t:
        r.add("doom")
    gain = re.search(r"gain[^.]{0,30}strength", t) or re.search(r"strength", t)
    if "strength" in t and not r.intersection({"strengthdown"}):
        r.add("strength")
    if "dexterity" in t:
        r.add("dexterity")
    if "focus" in t:
        r.add("focus")
    if (card.get("hit_count") or 0) >= 2 or "times" in t or "twice" in t:
        if "damage" in r:
            r.add("multihit")
    if card.get("cost") == 0 and card.get("type") not in ("Curse", "Status"):
        r.add("zerocost")
    if "shiv" in t:
        r.add("shiv")
    if "osty" in t:
        r.add("osty")
    if "minion" in t or "summon" in t:
        r.add("minion")
    if "exhaust" in t:
        r.add("exhaust")
    if "retain" in t:
        r.add("retain")
    if card.get("type") == "Power":
        r.add("power")
    return r


# Re-implemented from Archetypes.Routes (name, payoff role, enabler roles, threshold)
ROUTES = [
    ("strength-multihit", "multihit", ["strength"], 2),
    ("strength-damage", "damage", ["strength"], 4),
    ("block-dexterity", "block", ["dexterity"], 4),
    ("poison", "poison", ["poison", "weak"], 2),
    ("doom", "doom", ["doom", "strengthdown"], 2),
    ("force-focus", "focus", ["focus"], 2),
    ("orb-block", "block", ["focus", "dexterity"], 4),
    ("shiv", "shiv", ["zerocost", "strength"], 2),
    ("minion", "minion", ["osty"], 2),
    ("exhaust-engine", "exhaust", ["draw", "energy"], 3),
    ("draw-engine", "draw", ["energy", "zerocost"], 4),
]


class Detector:
    def __init__(self, cards, clusters):
        self.cards = cards
        self.clusters = clusters
        self._roles = {}

    def roles(self, cid):
        if cid not in self._roles:
            c = self.cards.get(cid)
            self._roles[cid] = roles_of(c) if c else set()
        return self._roles[cid]

    def detect(self, deck_ids, character, relics=()):
        """(routes, signature_or_roles) — clusters take precedence over routes."""
        if not deck_ids:
            return []
        ids = set(deck_ids)
        density = min(1.3, max(0.7, 20.0 / max(10, len(deck_ids))))
        matches = []
        for ch, name, sig, rel, _ in self.clusters:
            if ch != character.upper():
                continue
            hits = sum(1 for c in sig if c in ids)
            if hits < 2:
                continue
            relic_hits = sum(1 for r in rel if r in relics)
            strength = min(1.0, (hits / 4.0 + 0.2 * min(1, relic_hits)) * density)
            if strength > 0:
                matches.append((name, strength))
        if matches:
            matches.sort(key=lambda m: (-m[1], m[0]))
            return matches
        counts = collections.Counter()
        for cid in deck_ids:
            for role in self.roles(cid):
                counts[role] += 1
        for name, payoff, enablers, threshold in ROUTES:
            payoffs = counts.get(payoff, 0)
            if payoffs < threshold:
                continue
            n_enablers = sum(counts.get(e, 0) for e in enablers)
            if n_enablers < 1:
                continue
            strength = min(1.0, (payoffs / (threshold * 2.0) + 0.2 * min(1, n_enablers)) * density)
            if strength > 0:
                matches.append((name, strength))
        matches.sort(key=lambda m: (-m[1], m[0]))
        return matches

    def dead_share(self, deck_ids, routes):
        if not deck_ids:
            return 0.0
        if not routes:
            return 1.0
        # A card serves a route if its roles include any role that route is built
        # from. Signature names are a subset of the deck, so roles are the proxy.
        serving_roles = set()
        names = {n for n, _ in routes}
        for name, payoff, enablers, _ in ROUTES:
            if name in names:
                serving_roles.add(payoff)
                serving_roles.update(enablers)
        for ch, cname, sig, _, _ in self.clusters:
            if cname in names:
                for cid in sig:
                    serving_roles |= self.roles(cid)
        n = sum(1 for cid in deck_ids if self.roles(cid) & serving_roles)
        return 1.0 - n / len(deck_ids)


def auc(wins, losses):
    if not wins or not losses:
        return None
    greater = ties = 0
    for w in wins:
        for l in losses:
            if w > l:
                greater += 1
            elif abs(w - l) < 1e-9:
                ties += 1
    return (greater + ties / 2) / (len(wins) * len(losses))


def permutation_p(wins, losses, observed, iterations=2000, seed=12345):
    """One-sided p for "AUC is no better than a coin flip".

    The protocol fixed the acceptance rule at a permutation p below 0.05, because
    with ~45 decks a side the standard error of the mean AUC is about 0.04 and a
    point estimate cannot separate 0.58 from 0.61. Labels are shuffled, so the
    null is "the feature knows nothing about the outcome".
    """
    import random
    rng = random.Random(seed)
    pool = list(wins) + list(losses)
    n_w = len(wins)
    hits = 0
    for _ in range(iterations):
        rng.shuffle(pool)
        a = auc(pool[:n_w], pool[n_w:])
        if a is not None and a >= observed:
            hits += 1
    return (hits + 1) / (iterations + 1)


def bootstrap_ci(wins, losses, iterations=2000, seed=999):
    """95% interval for the AUC, resampling decks within each side."""
    import random
    rng = random.Random(seed)
    out = []
    for _ in range(iterations):
        w = [rng.choice(wins) for _ in wins]
        l = [rng.choice(losses) for _ in losses]
        a = auc(w, l)
        if a is not None:
            out.append(a)
    if not out:
        return (None, None)
    out.sort()
    return (out[int(0.025 * len(out))], out[int(0.975 * len(out)) - 1])


def mean(xs):
    xs = [x for x in xs if x is not None]
    return sum(xs) / len(xs) if xs else 0.0


def load_decks(players):
    out = []
    for path in glob.glob(os.path.join(DECKS, "*.json")):
        d = json.load(io.open(path, encoding="utf-8"))
        if d.get("ascension") != 10 or not d.get("deck"):
            continue
        if players and d.get("players") != players:
            continue
        out.append(d)
    return out


def structural(det, deck):
    ids = [e["id"] for e in deck["deck"]]
    routes = det.detect(ids, deck["character"], deck.get("relics") or [])
    # convergence: the pick on which the deck first reads as having a plan
    convergence = float("inf")
    picks = deck.get("picks") or []
    if picks:
        built = []
        for i, cid in enumerate(picks):
            built.append(cid)
            if det.detect(built, deck["character"], []):
                convergence = i + 1
                break
    return (len(routes), 1 if len(routes) >= 2 else 0, convergence, det.dead_share(ids, routes))


def dump_roles(cards, out_path):
    """Every card's derived roles, so the mod's own tagging can be diffed against it.

    Dispatch 4 asks the simulator to localise the role-tagging divergence, and an
    argument about tag semantics goes nowhere. This turns it into a file diff: the
    mod prints CardProfile.Of(card).Roles for the same ids and the two are compared
    mechanically. The uncertainty is real and should be visible in the output --
    the mod reads GeniusCombatStrategy.Analyze (card text executed), this reads the
    compendium metadata (text parsed), and where they disagree neither is obviously
    right.
    """
    lines = ["id,cost,type,rarity,roles"]
    for cid in sorted(cards):
        c = cards[cid]
        roles = "|".join(sorted(roles_of(c)))
        cost = c.get("cost")
        lines.append(f"{cid},{'' if cost is None else cost},{c.get('type','')},{c.get('rarity','')},{roles}")
    sep = chr(10)
    io.open(out_path, "w", encoding="utf-8").write(sep.join(lines) + sep)
    print(f"role table -> {out_path} ({len(cards)} cards)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump-roles", action="store_true",
                    help="write every card's derived roles to a CSV and stop")
    ap.add_argument("--players", type=int, default=0)
    ap.add_argument("--compare", help="path to a simulator --features dump to diff against")
    args = ap.parse_args()

    cards = load_cards()
    if args.dump_roles:
        dump_roles(cards, "outputs/decksim-analysis/role-table-tester.csv")
        return
    clusters = load_clusters()
    det = Detector(cards, clusters)
    decks = load_decks(args.players)
    print(f"{len(decks)} A10 decks"
          + (f" at {args.players} players" if args.players else " (all party sizes)")
          + f"; {len(clusters)} mined clusters parsed from the C# table")

    by_char = collections.defaultdict(list)
    for d in decks:
        r, pkg2, conv, dead = structural(det, d)
        by_char[d["character"]].append((r, pkg2, conv, dead, d["win"], len(d["deck"])))

    features = [("packages (routes)", 0), ("packages >=2", 1),
                ("convergence (pick no.)", 2), ("dead cards", 3)]
    chars = [c for c in ("IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT") if by_char.get(c)]
    print()
    print("INDEPENDENT AUC vs real A10 win/loss (0.50 = no signal)")
    print(f"{'feature':24}" + "".join(f"{c[:6]:>8}" for c in chars) + f"{'mean':>8}{'>=0.5':>7}")
    for name, idx in features:
        row = []
        for c in chars:
            rows = by_char[c]
            # convergence is "lower is better", so negate it to keep the sign
            sign = -1 if name.startswith("convergence") else 1
            wins = [sign * s[idx] for s in rows if s[4]]
            losses = [sign * s[idx] for s in rows if not s[4]]
            if len(wins) < 5 or len(losses) < 5:
                continue
            row.append(auc(wins, losses))
        over = sum(1 for v in row if v is not None and v > 0.5)
        print(f"{name:24}" + "".join(f"{v:8.2f}" if v is not None else f"{'-':>8}" for v in row)
              + f"{mean(row):8.2f}{over:>4}/{len(row)}")

    print()
    print("significance per feature (pooled within character, then combined):")
    print(f"{'feature':24}{'mean AUC':>9}{'95% CI':>16}{'perm p(>0.5)':>14}{'vs 0.61 bar':>22}")
    for name, idx in features:
        allw, alll = [], []
        sign = -1 if name.startswith("convergence") else 1
        for c in chars:
            rows = by_char[c]
            allw += [sign * s[idx] for s in rows if s[4]]
            alll += [sign * s[idx] for s in rows if not s[4]]
        if len(allw) < 5 or len(alll) < 5:
            continue
        obs = auc(allw, alll)
        lo, hi = bootstrap_ci(allw, alll)
        pv = permutation_p(allw, alll, obs)
        verdict = ("below the bar" if hi is not None and hi < 0.61
                   else "indistinguishable from the bar" if lo is not None and hi is not None and hi >= 0.61
                   else "-")
        print(f"{name:24}{obs:9.2f}{(f'{lo:.2f}-{hi:.2f}' if lo is not None else '-'):>16}"
              f"{pv:14.3f}{verdict:>22}")
    print("  the 0.61 bar is block/damage at this party size, the best existing feature;")
    print("  the protocol requires a new feature to beat it, not merely to clear 0.50")

    print()
    print("convergence: how often a plan ever appeared, and at which pick")
    for c in chars:
        rows = by_char[c]
        convs = [s[2] for s in rows]
        never = sum(1 for v in convs if v == float("inf"))
        finite = [v for v in convs if v != float("inf")]
        w = [s for s in rows if s[4]]
        l = [s for s in rows if not s[4]]
        print(f"  {c:12} n={len(rows):4}  never converged {never}/{len(rows)} ({100*never/len(rows):.0f}%)"
              f"  median pick among converged={sorted(finite)[len(finite)//2] if finite else '-'}"
              f"  wins {len(w)} / losses {len(l)}")

    gaming(cards, det, clusters, by_char)


def gaming(cards, det, clusters, by_char):
    """Can a drafter that only maximises the feature drift away from winners?

    A full re-draft is not possible from the cache: a reward screen records the
    offers and which was taken, not the deck that was in front of the player, so
    a greedy re-draft would be scoring a deck it cannot see. What can be settled
    without it is whether the feature rewards a behaviour the data says loses.

    Route count grows with the number of signature cards held, and holding more
    cards means a bigger deck. Deck size is itself a feature here, so the test is
    whether maximising routes drags the deck toward the side of the size
    distribution that loses: compare size and outcome across route-count
    quartiles. The sampling fixes the win/loss ratio, so the size shift is the
    solid number and the win rate is read only as a direction.
    """
    print()
    print("=" * 78)
    print("gaming check - is the feature a proxy for a losing behaviour?")
    print("=" * 78)
    for c in sorted(by_char):
        rows = by_char[c]
        if len(rows) < 40:
            continue
        ordered = sorted(rows, key=lambda s: s[0])
        q = max(1, len(ordered) // 4)
        low, high = ordered[:q], ordered[-q:]
        size_of = lambda chunk: sum(s[5] for s in chunk) / len(chunk)
        print(f"  {c:12} routes: low={low[0][0]} size={size_of(low):5.1f} W{sum(1 for s in low if s[4])}/{len(low)}"
              f"   high={high[-1][0]} size={size_of(high):5.1f} W{sum(1 for s in high if s[4])}/{len(high)}")
    print()
    print("  A feature whose high quartile is systematically LARGER is rewarding deck")
    print("  growth, and deck size reads the wrong way here (winners are smaller), so")
    print("  a degenerate drafter that stacks signature cards raises this feature")
    print("  while moving away from what real winners look like.")


if __name__ == "__main__":
    main()
