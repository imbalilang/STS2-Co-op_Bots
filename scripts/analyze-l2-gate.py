#!/usr/bin/env python3
"""Is an L2 build a prerequisite for clearing a four-player A10 run?

The claim under test: "to be confident of clearing, at least one completed L2
build — one player's or the team's — has to come online." That is a sharp
prediction, so it can be falsified: if runs that clear have no L2 build and runs
that die do, the claim is wrong.

An L2 build is defined as a *payoff* card plus enough *fuel* for it, because the
payoff alone does nothing:

  quadratic because it replays an accumulated count  KNIFE_TRAP, VOLTAIC,
      TESLA_COIL, SOUL_STORM, ALL_FOR_ONE, PULL_FROM_BELOW
  quadratic because it grows itself               CLAW, MAUL
  a converter that turns an event into output     FEEL_NO_PAIN, DARK_EMBRACE,
      MEMENTO_MORI, BLACK_HOLE, SUPERMASSIVE, ...

Poison and Doom are evaluated across the whole party rather than per player,
because the stack lives on the enemy: see outputs/archetype-amplification-map.md.

Read odds ratios, never the percentages — the crawler fixes how many wins and
losses it fetches, so the rate in the sample is a sampling artefact. The
balanced rerun at the end is the check that matters, because that sampling also
left the sample win-heavy.

Usage:
    python scripts/analyze-l2-gate.py
"""
import collections
import glob
import io
import json
import math
import os

FULL = "work/spire-codex/runs-full"
CARDS = "work/spire-codex/cards/cards.json"


def load():
    cards = {c["id"]: c for c in json.load(io.open(CARDS, encoding="utf-8"))}
    runs = []
    for path in glob.glob(os.path.join(FULL, "*.json")):
        p = json.load(io.open(path, encoding="utf-8"))
        if len(p.get("players") or []) == 4:
            runs.append(p)
    return cards, runs


def text(card):
    return ((card.get("description_raw") or "") + " " + str(card.get("keywords") or "")).lower()


def builds(cards):
    """(name, payoff-card set, fuel predicate over a Counter of card ids)."""
    def spawns(what):
        return {k for k, c in cards.items() if what in [str(s).upper() for s in (c.get("spawns_cards") or [])]}

    def mentions(what, exclude=()):
        return {k for k, c in cards.items()
                if what in text(c) and k not in exclude and c.get("type") not in ("Curse", "Status")}

    shiv = spawns("SHIV") - {"ACCURACY", "KNIFE_TRAP", "PHANTOM_BLADES", "FAN_OF_KNIVES"}
    soul = spawns("SOUL")
    channel = mentions("channel")
    ethereal = mentions("ethereal")
    exhaust = mentions("exhaust")
    discard = mentions("discard")
    stars = mentions("star")
    creators = mentions("create") | spawns("SHIV") | spawns("SOUL") | spawns("DEBRIS")
    cheap = {k for k, c in cards.items() if c.get("cost") == 0}

    def n(cnt, ids):
        return sum(cnt[i] for i in ids)

    return [
        # per-player quadratic builds
        ("shiv_replay", {"KNIFE_TRAP"}, lambda c: n(c, shiv) >= 3),
        ("claw_self", {"CLAW"}, lambda c: c["CLAW"] >= 2),
        ("maul_self", {"MAUL"}, lambda c: c["MAUL"] >= 2),
        ("lightning_replay", {"VOLTAIC", "TESLA_COIL"}, lambda c: n(c, channel) >= 2),
        ("soul_replay", {"SOUL_STORM"}, lambda c: n(c, soul) >= 2),
        ("zero_cost_replay", {"ALL_FOR_ONE"}, lambda c: n(c, cheap) >= 4),
        ("ethereal_freq", {"PULL_FROM_BELOW", "BANSHEES_CRY"}, lambda c: n(c, ethereal) >= 3),
        ("exhaust_freq", {"FEEL_NO_PAIN", "DARK_EMBRACE"}, lambda c: n(c, exhaust) >= 3),
        ("discard_freq", {"MEMENTO_MORI"}, lambda c: n(c, discard) >= 2),
        ("star_freq", {"BLACK_HOLE", "CHILD_OF_THE_STARS", "THE_SEALED_THRONE"}, lambda c: n(c, stars) >= 2),
        ("create_freq", {"SUPERMASSIVE", "ARSENAL", "PILLAR_OF_CREATION"}, lambda c: n(c, creators) >= 2),
        ("status_freq", {"SMOKESTACK", "ROCKET_PUNCH"},
         lambda c: sum(c[i] for i in ("TURBO", "GUNK_UP", "REFRACT", "OVERCLOCK")) >= 1),
        # team-level: the stack is on the enemy, so it is summed over the party
        ("poison_stack", {"ACCELERANT", "MIRAGE", "OUTBREAK"},
         lambda c: sum(c[i] for i in ("DEADLY_POISON", "POISONED_STAB", "BOUNCING_FLASK",
                                      "BUBBLE_BUBBLE", "HAZE", "SNAKEBITE", "NOXIOUS_FUMES",
                                      "CORROSIVE_WAVE", "OUTBREAK")) >= 2),
        ("doom_stack", {"TIME'S_UP", "TIMES_UP", "SHROUD", "DEATHS_DOOR", "END_OF_DAYS"},
         lambda c: sum(c[i] for i in ("BLIGHT_STRIKE", "COUNTDOWN", "DEATHBRINGER", "NEGATIVE_PULSE",
                                      "SCOURGE", "OBLIVION", "NO_ESCAPE")) >= 2),
    ]


L3 = [
    ("nightmare_loop", lambda c: c["NIGHTMARE"] >= 1),
    ("poison_accel_mirage", lambda c: c["ACCELERANT"] >= 1 and c["MIRAGE"] >= 1),
    ("thunder_voltaic_storm", lambda c: c["VOLTAIC"] >= 1 and c["THUNDER"] >= 1 and c["STORM"] >= 1),
    ("claw_all_for_one", lambda c: c["CLAW"] >= 2 and c["ALL_FOR_ONE"] >= 1),
    ("regent_create_arsenal", lambda c: c["SUPERMASSIVE"] >= 1 and c["ARSENAL"] >= 1),
    ("arsenal_heirloom", lambda c: c["ARSENAL"] >= 1 and c["HEIRLOOM_HAMMER"] >= 1),
]

L1_ONLY = [
    ("shiv_accuracy", lambda c: c["ACCURACY"] >= 1 and sum(
        c[i] for i in ("BLADE_DANCE", "CLOAK_AND_DAGGER", "UP_MY_SLEEVE", "BLADE_OF_INK",
                       "HIDDEN_DAGGERS", "LEADING_STRIKE", "STORM_OF_STEEL",
                       "INFINITE_BLADES", "FAN_OF_KNIVES")) >= 3),
    ("tracking_weak", lambda c: c["TRACKING"] >= 1 and sum(
        c[i] for i in ("SUPPRESS", "DEATHBRINGER", "LEG_SWEEP", "PIERCING_WAIL", "MALAISE")) >= 2),
]


def deck_counts(cards, player):
    ids = [(c.get("id") or "").replace("CARD.", "") for c in (player.get("deck") or [])]
    return collections.Counter(i for i in ids if i in cards)


def classify(cards, run, packages, vector=None):
    """('l2'|'l3'|'l1'|'none', names) for one four-player run."""
    per_player = [deck_counts(cards, pl) for pl in run["players"]]
    team = collections.Counter()
    for c in per_player:
        team.update(c)
    if vector is not None:
        return [("yes" if fn(team) or any(fn(c) for c in per_player) else "no") for _, fn in vector]
    # L3 first: a closed loop is the strongest form
    for name, fn in L3:
        if fn(team) or any(fn(c) for c in per_player):
            return "l3", [name]
    hits = []
    for name, payoffs, fuel in packages:
        if not any(team[p] for p in payoffs):
            continue
        if not fuel(team):
            continue
        # a shared-stack package counts once for the party; a per-player one needs
        # the payoff and the fuel in the SAME deck
        if name.endswith("_stack"):
            hits.append(name)
        else:
            if any(any(c[p] for p in payoffs) and fuel(c) for c in per_player):
                hits.append(name)
    if hits:
        return "l2", hits
    for name, fn in L1_ONLY:
        if fn(team) or any(fn(c) for c in per_player):
            return "l1", [name]
    return "none", []


def or_text(w1, n1, w0, n0):
    l1, l0 = n1 - w1, n0 - w0
    if min(w1, l1, w0, l0) == 0:
        return "OR n/a"
    r = (w1 / l1) / (w0 / l0)
    se = math.sqrt(1 / w1 + 1 / l1 + 1 / w0 + 1 / l0)
    return f"OR {r:5.2f} (1SE {math.exp(math.log(r)-se):.2f}-{math.exp(math.log(r)+se):.2f})"


def table(runs, cards, packages, title, note=""):
    print()
    print("=" * 78)
    print(f"{title}{note}")
    print("=" * 78)
    groups = collections.defaultdict(lambda: [0, 0])
    for run in runs:
        tier, _ = classify(cards, run, packages)
        groups[tier][0] += 1 if run.get("win") else 0
        groups[tier][1] += 1
    wins = sum(w for w, _ in groups.values())
    total = sum(n for _, n in groups.values())
    print(f"  {total} runs ({wins} wins / {total-wins} losses; read the odds ratios, not the %)")
    base = groups.get("none")
    for tier in ("none", "l1", "l2", "l3"):
        if tier not in groups:
            continue
        w, n = groups[tier]
        extra = ""
        if tier != "none" and base and base[0] and base[1]:
            extra = "   " + or_text(w, n, base[0], base[1]) + "  vs none"
        print(f"    {tier:5} {100*w/n:5.0f}% ({w}/{n}){extra}")
    return groups


def main():
    cards, runs = load()
    packages = builds(cards)
    print(f"{len(runs)} four-player runs with full detail; {len(packages)} L2 packages defined")
    for name, payoffs, _ in packages:
        print(f"    {name:20} payoff: {', '.join(sorted(payoffs))}")

    all_runs = runs
    table(all_runs, cards, packages, "all available four-player runs")

    # balance: the crawler fetches wins first, so the raw sample is win-heavy
    wins = sorted((r for r in all_runs if r.get("win")), key=lambda r: str(r.get("seed")))
    losses = [r for r in all_runs if not r.get("win")]
    balanced = wins[:len(losses)] + losses
    table(balanced, cards, packages, f"balanced subsample ({len(losses)} wins + {len(losses)} losses)",
          note="  <- the one to read")

    print()
    print("=" * 78)
    print("which L2 packages appear in clearing vs dying runs (balanced sample)")
    print("=" * 78)
    B = collections.Counter()
    for run in balanced:
        _, hits = classify(cards, run, packages)
        for h in hits:
            B[(h, bool(run.get("win")))] += 1
    print(f"  {'package':22}{'in wins':>9}{'in losses':>11}")
    for name, _, _ in packages:
        print(f"  {name:22}{B[(name, True)]:>9}{B[(name, False)]:>11}")

    print()
    print("=" * 78)
    print("caveats")
    print("=" * 78)
    print("  * fuel thresholds are judgement calls; a build can be online in play")
    print("    without meeting the deck-count bar, which biases toward 'none'")
    print("  * the final deck is partly the outcome: a run that died early in act 3")
    print("    may have been assembling a build it never finished")
    print("  * crawler cells are capped, so only odds ratios are comparable")


if __name__ == "__main__":
    main()
