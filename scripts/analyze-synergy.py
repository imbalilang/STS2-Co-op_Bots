#!/usr/bin/env python3
"""Do card packages pay off multiplicatively, and does a party stack them?

Marginal card strength (what a pick-rate table measures) cannot see the thing
that decides a build: whether two cards multiply. Accuracy alone is a dead card
and a Shiv generator alone is a 4-damage card; together they are damage =
shivs x (base + accuracy). Knife Trap re-plays every Shiv already exhausted, so
it multiplies by the shivs played so far rather than adding to them. Poison and
Doom are worse than that in a party, because the stack lives on the *enemy*:
four players applying Poison to one target build one shared stack, and each
player's Accelerant multiplies that same stack, so party output grows faster
than the 4 x 1.3 the monster's HP grows by.

This script measures three things:

  1. the package ladder — win rate as producers, multiplier, frequency payoff
     and the exponential payoff are added one at a time (the user's 20/40/80%);
  2. 2x2 interaction — observed win rate against the rate an additive
     log-odds model predicts from the two marginal rates. Positive = the two
     cards are worth more together than apart, which is the claim under test;
  3. party stacking — win rate against how many of the four players ran the
     package, which is where a shared enemy debuff should show up hardest.

Inputs are the caches the sibling fetch scripts write. Nothing here re-fetches.
Usage:
    python scripts/analyze-synergy.py
"""
import collections
import glob
import io
import json
import math
import os

DECKS = "work/spire-codex/decks/runs"
FULL = "work/spire-codex/runs-full"
CARDS = "work/spire-codex/cards/cards.json"

# Package membership, taken from the card text rather than from a tier list:
# PRODUCERS add Shivs to hand or play; MULT is the card that scales with the
# number of Shivs; FREQ turns "cards played" into output; TRAP replays the
# Shivs already exhausted, which is the term that compounds.
SHIV_PRODUCERS = ["BLADE_DANCE", "CLOAK_AND_DAGGER", "UP_MY_SLEEVE", "BLADE_OF_INK",
                  "HIDDEN_DAGGERS", "LEADING_STRIKE", "STORM_OF_STEEL",
                  "INFINITE_BLADES", "FAN_OF_KNIVES"]
SHIV_MULT = ["ACCURACY"]
SHIV_FREQ = ["AFTERIMAGE", "PHANTOM_BLADES", "FINISHER"]
SHIV_TRAP = ["KNIFE_TRAP"]

POISON_APPLY = ["DEADLY_POISON", "POISONED_STAB", "BOUNCING_FLASK", "BUBBLE_BUBBLE",
                "HAZE", "SNAKEBITE", "NOXIOUS_FUMES", "CORROSIVE_WAVE", "OUTBREAK"]
POISON_MULT = ["ACCELERANT", "ENVENOM"]

DOOM = ["BLIGHT_STRIKE", "COUNTDOWN", "DEATHS_DOOR", "DEATHBRINGER", "END_OF_DAYS",
        "NEGATIVE_PULSE", "NEUROSURGE", "NO_ESCAPE", "OBLIVION", "REAPER_FORM",
        "SCOURGE", "SHROUD", "TIMES_UP"]

# "Whenever you play a card / an attack / a power" — the general frequency class.
FREQ_PAYOFF = ["AFTERIMAGE", "SERPENT_FORM", "STRANGLE", "OBLIVION", "MONOLOGUE", "RAGE",
               "FINISHER", "CALAMITY", "SUBROUTINE", "STORM", "DANSE_MACABRE", "ENVENOM",
               "HAUNT", "DEVOUR_LIFE", "RIGHT_HAND_HAND", "THE_SEALED_THRONE", "SPIRIT_OF_ASH"]


def known():
    if not os.path.exists(CARDS):
        raise SystemExit(f"missing {CARDS}; run scripts/fetch-spire-codex-cards.py")
    return {c["id"] for c in json.load(io.open(CARDS, encoding="utf-8"))}


def check_ids(ids):
    missing = [i for i in ids if i not in known()]
    if missing:
        print(f"  WARNING: not in this build's card list, ignored: {missing}")


def counts(ids):
    return collections.Counter(ids)


def deck_ids(deck):
    return [e["id"] for e in deck["deck"]]


def load_decks():
    out = []
    for path in glob.glob(os.path.join(DECKS, "*.json")):
        d = json.load(io.open(path, encoding="utf-8"))
        if d.get("ascension") == 10 and d.get("players") and d.get("deck"):
            out.append(d)
    return out


def load_runs():
    out = []
    for path in glob.glob(os.path.join(FULL, "*.json")):
        p = json.load(io.open(path, encoding="utf-8"))
        if p.get("players"):
            out.append(p)
    return out


def logit(p):
    p = min(max(p, 1e-6), 1 - 1e-6)
    return math.log(p / (1 - p))


def pct(wins, n):
    return f"{100*wins/n:.0f}% ({wins}/{n})" if n else "-"


def odds_ratio(w1, n1, w0, n0):
    """Odds ratio with its log-scale standard error (Woolf).

    The crawler samples a fixed number of wins and losses, so the *intercept* of
    any rate here is an artefact of that choice and the absolute win rate means
    nothing. The odds ratio between two cells is unaffected by that sampling, so
    it is the only quantity worth reading off these tables.
    """
    l1, l0 = n1 - w1, n0 - w0
    if min(w1, l1, w0, l0) == 0:
        return None, None
    ratio = (w1 / l1) / (w0 / l0)
    se = math.sqrt(1 / w1 + 1 / l1 + 1 / w0 + 1 / l0)
    return ratio, se


def or_text(w1, n1, w0, n0):
    ratio, se = odds_ratio(w1, n1, w0, n0)
    if ratio is None:
        return "OR n/a"
    lo, hi = math.exp(math.log(ratio) - se), math.exp(math.log(ratio) + se)
    return f"OR {ratio:5.2f}  (±1SE {lo:.2f}-{hi:.2f})"


def section(title):
    print()
    print("=" * 78)
    print(title)
    print("=" * 78)


# ---- 1. the package ladder --------------------------------------------------

def ladder(decks, character, label, tiers):
    """Win/loss counts per tier, plus the odds ratio against the widest tier.

    Read the odds ratio, not the percentage: the sample holds a fixed number of
    wins and losses per cell, so a rate here is a ratio of two sampling choices.
    """
    section(f"{label}: package components, one tier at a time ({character} decks)")
    rows = [d for d in decks if d["character"] == character]
    if not rows:
        print("  no decks")
        return
    print(f"  {len(rows)} {character} decks across all party sizes "
          f"({sum(1 for d in rows if d['win'])} wins / {sum(1 for d in rows if not d['win'])} losses)")
    base = None
    for name, fn in tiers:
        sel = [d for d in rows if fn(counts(deck_ids(d)))]
        w = sum(1 for d in sel if d["win"])
        n = len(sel)
        if base is None:
            base = (w, n)
            print(f"    {name:50} {pct(w, n):>16}   (reference)")
        else:
            print(f"    {name:50} {pct(w, n):>16}   {or_text(w, n, *base)}")


def interaction(decks, character, label_a, fn_a, label_b, fn_b, restrict=None):
    """2x2 with the additive log-odds prediction, so super-additivity is visible."""
    rows = [d for d in decks if d["character"] == character and (restrict is None or restrict(counts(deck_ids(d))))]
    cell = {}
    for a in (False, True):
        for b in (False, True):
            sel = [d for d in rows if bool(fn_a(counts(deck_ids(d)))) == a and bool(fn_b(counts(deck_ids(d)))) == b]
            cell[(a, b)] = (sum(1 for d in sel if d["win"]), len(sel))
    if any(n < 8 for _, n in cell.values()):
        print(f"  {label_a} x {label_b}: cell too small "
              f"({', '.join(f'{k}={n}' for k, (_, n) in cell.items())})")
        return
    # Only the interaction is free of the case-control sampling; the four
    # percentages are ratios of wins to losses the crawler chose to fetch.
    rate = {k: (w / n) for k, (w, n) in cell.items()}
    observed = rate[(True, True)]
    additive_logit = logit(rate[(False, False)]) + (logit(rate[(True, False)]) - logit(rate[(False, False)])) \
        + (logit(rate[(False, True)]) - logit(rate[(False, False)]))
    term = logit(observed) - additive_logit
    or_a = odds_ratio(*cell[(True, False)], *cell[(False, False)])[0]
    or_b = odds_ratio(*cell[(False, True)], *cell[(False, False)])[0]
    or_both = odds_ratio(*cell[(True, True)], *cell[(False, False)])[0]
    print(f"  {label_a} x {label_b}   (cell percentages are sampling artefacts; read the ratios)")
    print(f"    neither {pct(*cell[(False, False)])}   only {label_a} {pct(*cell[(True, False)])}"
          f"   only {label_b} {pct(*cell[(False, True)])}   both {pct(*cell[(True, True)])}")
    if None not in (or_a, or_b, or_both):
        print(f"    odds ratio vs neither:  {label_a} {or_a:.2f}   {label_b} {or_b:.2f}   "
              f"both {or_both:.2f}   (multiplicative would predict {or_a*or_b:.2f})")
    print(f"    log-odds interaction: {term:+.2f}   "
          f"{'SUPER-additive — the pair compounds' if term > 0.35 else 'sub-additive — the pair overlaps' if term < -0.35 else 'about additive'}")


# ---- 3. party stacking ------------------------------------------------------

def package_in(ids, apply_ids, mult_ids, apply_min):
    # The run payload prefixes every id with "CARD."; the deck cache does not.
    c = counts(i.replace("CARD.", "") for i in ids)
    n_apply = sum(c[i] for i in apply_ids)
    if n_apply < apply_min:
        return False
    # An empty multiplier list means "no multiplier required", not "impossible".
    return not mult_ids or sum(c[i] for i in mult_ids) >= 1


def runner_distribution(runs, label, defs):
    """How often a four-player party even HAS two players on the same package.

    A shared enemy debuff should compound with party size, but that is only
    testable if parties actually stack it. Printing the distribution first keeps
    a null result from being read as evidence against the mechanism when the
    sample simply contains no stacked parties.
    """
    section(f"{label}: distribution of runners per four-player party")
    dist = {}
    for name, apply_ids, mult_ids, apply_min in defs:
        counts_ = collections.Counter()
        for p in runs:
            ps = p.get("players") or []
            if len(ps) != 4:
                continue
            runners = sum(1 for pl in ps
                          if package_in([c.get("id", "") for c in (pl.get("deck") or [])],
                                        apply_ids, mult_ids, apply_min))
            counts_[runners] += 1
        if counts_:
            dist[name] = counts_
    for name, counts_ in dist.items():
        total = sum(counts_.values())
        parts = ", ".join(f"{k}:{v}" for k, v in sorted(counts_.items()))
        stacked = sum(v for k, v in counts_.items() if k >= 2)
        print(f"  {name:34} {parts}   -> {stacked}/{total} parties with 2+ runners")


def zero_loss_p(w, l, wins, losses):
    """How surprising is it that a cell of (w wins, 0 losses) has no losses?

    Computed against the sample's own win/loss totals, not against the cell
    percentages — those are artefacts of how many wins and losses the crawler
    chose to fetch. Under no association the cell's outcomes are a draw without
    replacement from the pooled outcomes, so this is the hypergeometric tail.
    """
    if l != 0:
        return None
    total = wins + losses
    if total == 0 or w > total:
        return None
    # P(no loss) = prod over the cell of (wins - i) / (total - i)
    p = 1.0
    for i in range(w):
        p *= (wins - i) / (total - i)
    return p


def trend_and_zero_loss(counts_by_runners):
    """Two tests that matter more than the cell percentages.

    Cochran-Armitage: does the win/loss ratio move monotonically with the number
    of runners? It is a dose-response test, so it is not fooled by one lucky
    cell the way a per-cell comparison is.

    Zero-loss cells are the other trap: 13 wins and no losses reads as an
    infinite odds ratio and a 100% win rate, when it may only be small. The
    exact binomial tail says how surprising a clean sweep actually is.
    """
    rows = [(k, w, n - w) for k, (w, n) in sorted(counts_by_runners.items())]
    total = sum(w + l for _, w, l in rows)
    wins = sum(w for _, w, _ in rows)
    if total == 0 or wins in (0, total):
        return "trend n/a (no variance in outcome)"
    scores = [k for k, _, _ in rows]
    mean_score = sum(k * (w + l) for k, w, l in rows) / total
    p = wins / total
    num = sum((k - mean_score) * w for k, w, _ in rows)
    var = p * (1 - p) * sum((k - mean_score) ** 2 * (w + l) for k, w, l in rows)
    z = num / math.sqrt(var) if var > 0 else 0.0
    note = f"Cochran-Armitage trend z = {z:+.2f}"
    for k, w, l in rows:
        if l == 0 and w >= 5:
            p_zero = zero_loss_p(w, l, wins, total - wins)
            if p_zero is not None:
                note += (f"; {k}-runner cell has 0 losses in {w} runs "
                         f"(hypergeometric p={p_zero:.2e} against the sample's own win/loss totals)")
    return note


def party_stacking(runs, label, apply_ids, mult_ids, apply_min, note=None):
    """How many of the four players ran the package, against the run's outcome.

    This is where a debuff that lives on the *enemy* should separate from one
    that lives on the player: four players stacking Poison build one shared
    stack, so the party's effective output is closer to (4 x poison) x (4 x
    multiplier) than to 4 x (poison x multiplier).

    The crawler fetches wins first, so an interrupted crawl leaves a win-heavy
    sample. The odds ratios survive that (they are what a case-control design
    estimates without bias) but the percentages and any "no losses" claim do
    not, which is why the composition is printed and a balanced rerun is done.
    """
    section(f"{label}: how many of the four players ran the package"
            + (f"  [{note}]" if note else ""))
    by_count = collections.defaultdict(lambda: [0, 0])
    seen = wins = 0
    for p in runs:
        ps = p.get("players") or []
        if len(ps) != 4:
            continue
        seen += 1
        wins += 1 if p.get("win") else 0
        runners = 0
        for pl in ps:
            ids = [c2.get("id", "") for c2 in (pl.get("deck") or [])]
            if package_in(ids, apply_ids, mult_ids, apply_min):
                runners += 1
        by_count[runners][0] += 1 if p.get("win") else 0
        by_count[runners][1] += 1
    if not seen:
        print("  no 4-player run details cached")
        return
    print(f"  {seen} four-player runs with full detail "
          f"({wins} wins / {seen - wins} losses — the crawler fetches wins first, "
          f"so read the odds ratios, not the percentages)")
    base = by_count.get(0)
    for k in sorted(by_count):
        w, n = by_count[k]
        extra = "" if k == 0 or not base or base[0] == 0 else "   " + or_text(w, n, base[0], base[1])
        tag = "   (reference)" if k == 0 else extra
        print(f"    {k} of 4 players on the package   {pct(w, n):>16}{tag}")
    print(f"    {trend_and_zero_loss(by_count)}")


def main():
    section("package id check")
    for name, ids in (("shiv producers", SHIV_PRODUCERS), ("shiv mult", SHIV_MULT),
                      ("shiv freq", SHIV_FREQ), ("shiv trap", SHIV_TRAP),
                      ("poison apply", POISON_APPLY), ("poison mult", POISON_MULT),
                      ("doom", DOOM), ("frequency payoffs", FREQ_PAYOFF)):
        print(f"  {name}: {len(ids)} ids", end="")
        check_ids(ids)
        print()

    decks = load_decks()
    runs = load_runs()
    print(f"\n  {len(decks)} finished decks, {len(runs)} full run details")

    # --- the shiv ladder, the user's worked example ---
    prod3 = lambda c: sum(c[i] for i in SHIV_PRODUCERS) >= 3
    has_mult = lambda c: sum(c[i] for i in SHIV_MULT) >= 1
    has_freq = lambda c: sum(c[i] for i in SHIV_FREQ) >= 1
    has_trap = lambda c: sum(c[i] for i in SHIV_TRAP) >= 1
    ladder(decks, "SILENT", "shiv ladder", [
        ("any shiv producer", lambda c: sum(c[i] for i in SHIV_PRODUCERS) >= 1),
        ("producers >= 3", prod3),
        ("producers >= 3 + ACCURACY", lambda c: prod3(c) and has_mult(c)),
        ("producers >= 3 + ACCURACY + frequency payoff", lambda c: prod3(c) and has_mult(c) and has_freq(c)),
        ("producers >= 3 + ACCURACY + KNIFE_TRAP", lambda c: prod3(c) and has_mult(c) and has_trap(c)),
        ("producers >= 3 + ACCURACY + frequency + KNIFE_TRAP",
         lambda c: prod3(c) and has_mult(c) and has_freq(c) and has_trap(c)),
        ("producers >= 3, no ACCURACY", lambda c: prod3(c) and not has_mult(c)),
    ])

    section("shiv pairwise interactions (SILENT, all decks)")
    interaction(decks, "SILENT", "producers>=3", prod3, "ACCURACY", has_mult)
    interaction(decks, "SILENT", "producers>=3", prod3, "frequency payoff", has_freq)
    interaction(decks, "SILENT", "ACCURACY", has_mult, "KNIFE_TRAP", has_trap, restrict=prod3)
    interaction(decks, "SILENT", "frequency payoff", has_freq, "KNIFE_TRAP", has_trap, restrict=prod3)

    # --- poison, and whether four players compound ---
    ladder(decks, "SILENT", "poison ladder", [
        ("any poison applier", lambda c: sum(c[i] for i in POISON_APPLY) >= 1),
        ("appliers >= 2", lambda c: sum(c[i] for i in POISON_APPLY) >= 2),
        ("appliers >= 2 + ACCELERANT/ENVENOM",
         lambda c: sum(c[i] for i in POISON_APPLY) >= 2 and sum(c[i] for i in POISON_MULT) >= 1),
    ])
    ladder(decks, "NECROBINDER", "doom ladder", [
        ("any doom card", lambda c: sum(c[i] for i in DOOM) >= 1),
        ("doom cards >= 3", lambda c: sum(c[i] for i in DOOM) >= 3),
    ])

    # Does the party ever stack a package at all? A shared enemy debuff can only
    # compound if two players are actually on it, so this comes before the rates.
    runner_distribution(runs, "runners per party", [
        ("poison, strict (2 appliers + mult)", POISON_APPLY, POISON_MULT, 2),
        ("poison, loose (1 applier)", POISON_APPLY, [], 1),
        ("doom, strict (3 + payoff)", DOOM, ["END_OF_DAYS", "OBLIVION"], 3),
        ("doom, loose (1 card)", DOOM, [], 1),
        ("shiv producers (3+)", SHIV_PRODUCERS, [], 3),
        ("frequency payoffs (2+)", FREQ_PAYOFF, [], 2),
    ])

    for name, apply_ids, apply_min in (("poison, loose (>=1 applier)", POISON_APPLY, 1),
                                       ("doom, loose (>=1 card)", DOOM, 1),
                                       ("shiv producers >=3", SHIV_PRODUCERS, 3)):
        party_stacking(runs, name, apply_ids, [], apply_min)

    # Robustness: winners are downsampled (deterministically, by run hash) to
    # the number of losers so the two sides carry equal weight. A stacking
    # effect that only exists in the full win-heavy sample does not survive.
    four = [p for p in runs if len(p.get("players") or []) == 4]
    wins = sorted((p for p in four if p.get("win")), key=lambda p: p.get("seed") or "")
    losses = [p for p in four if not p.get("win")]
    if len(losses) >= 20 and len(wins) > len(losses):
        balanced = wins[:len(losses)] + losses
        print()
        print(f"  --- robustness: balanced subsample "
              f"({len(losses)} wins + {len(losses)} losses, wins chosen by seed order) ---")
        for name, apply_ids, apply_min in (("poison, loose", POISON_APPLY, 1),
                                           ("doom, loose", DOOM, 1),
                                           ("shiv producers >=3", SHIV_PRODUCERS, 3)):
            party_stacking(balanced, name, apply_ids, [], apply_min, note="balanced")

    section("caveats")
    print("  * win/loss cells come from a crawler with per-cell caps, so these are")
    print("    rates within a cell, not population win rates")
    print("  * a package measured on the FINAL deck is partly the run's outcome: a")
    print("    deck that never drew its payoff may have died before finishing it")
    print("  * no control for relics, potions or pilot; cells under 8 are suppressed")


if __name__ == "__main__":
    main()
