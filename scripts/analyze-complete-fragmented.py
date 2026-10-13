#!/usr/bin/env python3
"""Independent implementation of complete / stranded / fragmented / dead cards.

Dispatch 12. The role vocabulary was just pinned down: a package is now
P (producers) x M (multipliers) x R (replayers), and a deck can hold stock
without monetisation (or the reverse) — the state the old route detector could
not express, because it only fired once the package was already assembled.

Two independence checks are built in, because both were productive last round:

  * P/M/R is read here from the card text by hand and compared against
    BakedResources in the mod. They agree on shiv; if they diverge on another
    resource, the divergence is the finding, not a bug to paper over.
  * `variant-type` (MAD_SCIENCE) is carried as its own flag, and its share is
    reported alongside every AUC. A feature whose signal comes from "did the
    deck happen to hold Mad Science" is a proxy for an event card, not a
    measurement of a build.

Usage:
    python scripts/analyze-complete-fragmented.py --players 4
"""
import argparse
import collections
import csv
import glob
import io
import json
import os
import random
import re

DECKS = "work/spire-codex/decks/runs"
BAKED = "src/CoopBots/Building/BakedResources.cs"
CHARS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]

# ---- the tester's own reading of P/M/R, from card text -------------------
# Producers add Shivs to hand or deck; multipliers make each Shiv worth more;
# replayers re-use Shivs already spent. FAN_OF_KNIVES does both, so it is both.
MINE = {
    "shiv": {
        "P": ["BLADE_DANCE", "BLADE_OF_INK", "CLOAK_AND_DAGGER", "FAN_OF_KNIVES",
              "HIDDEN_DAGGERS", "INFINITE_BLADES", "LEADING_STRIKE", "STORM_OF_STEEL",
              "UP_MY_SLEEVE"],
        "M": ["ACCURACY", "FAN_OF_KNIVES", "PHANTOM_BLADES"],
        "R": ["KNIFE_TRAP"],
    },
    "poison": {
        "P": ["BOUNCING_FLASK", "BUBBLE_BUBBLE", "CORROSIVE_WAVE", "DEADLY_POISON",
              "ENVENOM", "HAZE", "NOXIOUS_FUMES", "OUTBREAK", "POISONED_STAB", "SNAKEBITE"],
        "M": ["ACCELERANT", "OUTBREAK"],
        "R": ["MIRAGE"],
    },
    "doom": {
        "P": ["BLIGHT_STRIKE", "COUNTDOWN", "DEATHBRINGER", "END_OF_DAYS", "NEGATIVE_PULSE",
              "NEUROSURGE", "NO_ESCAPE", "OBLIVION", "REAPER_FORM", "SCOURGE", "SHROUD"],
        "M": ["NO_ESCAPE"],
        "R": ["TIMES_UP", "DEATHS_DOOR"],
    },
    "orb": {
        "P": ["BALL_LIGHTNING", "CHAOS", "CHILL", "COLD_SNAP", "CONSUMING_SHADOW", "COOLHEADED",
              "DARKNESS", "FUSION", "GLACIER", "GLASSWORK", "ICE_LANCE", "LIGHTNING_ROD",
              "METEOR_STRIKE", "NULL", "RAINBOW", "REFRACT", "SHADOW_SHIELD", "SPINNER",
              "STORM", "TRASH_TO_TREASURE", "ZAP"],
        "M": ["BIASED_COGNITION", "DEFRAGMENT", "FOCUSED_STRIKE", "HOTFIX", "SYNCHRONIZE"],
        "R": ["VOLTAIC", "TESLA_COIL", "DUALCAST", "MULTI_CAST", "QUADCAST", "LOOP"],
    },
    "soul": {
        "P": ["CAPTURE_SPIRIT", "DIRGE", "GLIMPSE_BEYOND", "GRAVE_WARDEN", "REAVE", "SEVERANCE"],
        "M": [],
        "R": ["SOUL_STORM", "DEVOUR_LIFE", "HAUNT", "SEANCE"],
    },
    "star": {
        "P": ["BIG_BANG", "BLACK_HOLE", "GATHER_LIGHT", "GENESIS", "GLOW", "HIDDEN_CACHE",
              "KNOCKOUT_BLOW", "ROYAL_GAMBLE", "SHINING_STRIKE", "SOLAR_STRIKE",
              "THE_SEALED_THRONE", "VENERATE"],
        "M": [],
        "R": ["COMET", "CRESCENT_SPEAR", "DYING_STAR", "SEVEN_STARS", "SUPERMASSIVE"],
    },
}

PRODUCER_MIN = 3
# Ruling (dispatch 25): the stock threshold is 3, because 3 is the only value with
# data behind it -- the shiv odds-ratio ladder turns on "generators >= 3"
# (P>=3,M=0 -> OR 0.45; P>=3,M>=1 -> 1.25). It must be the SAME threshold as
# `complete`, or the two features stop measuring the same thing.
TOLERATED = 3

# Converters read an accumulated quantity and turn it into damage or block. They
# are neither producers, multipliers nor replayers, and the P/M/R scheme has no
# slot for them -- which is why MIRAGE had nowhere to live but `Payoffs`.
# MIRAGE is defensive (block = poison); TIMES_UP is the offensive twin (damage =
# Doom). Kept here as judgement, not baked data: BakedResources has no Converter
# column yet.
CONVERTERS = {
    "shiv": [],
    "poison": ["MIRAGE"],
    "doom": ["TIMES_UP", "DEATHS_DOOR", "SHROUD"],
    "orb": [],
    "soul": [],
    "star": ["SUPERMASSIVE"],
    "focus": [],
    "strength": ["BULLY"],
    "hp-loss": [],
}

# A resource can only be judged fragmented when BOTH sides of the XOR are
# defined. With M, R and Converter all empty the expression `stock XOR cash`
# silently degenerates to `stock`, and the count stops being "fragmented
# packages" and becomes "resources on hand". Resources failing this test are
# excluded and counted, never quietly folded in.
def participates(name, table):
    """Whether this resource has enough of a definition to be counted.

    `table` decides, and that matters more than it looks: the same definitions
    over the tester's hand reading give fragmented pooled AUC 0.57 (CI 0.51-0.62),
    and over BakedResources give 0.46 (CI 0.40-0.52). Same code, same sample,
    opposite verdict -- so the project's own table is authoritative and the hand
    reading is only ever a cross-check on it.
    """
    spec = table[name]
    return bool(spec.get("M") or spec.get("R") or CONVERTERS.get(name))


def parse_baked():
    """Parse the generated C# table so the comparison is against the file itself."""
    if not os.path.exists(BAKED):
        return {}
    text = io.open(BAKED, encoding="utf-8").read()
    out = {}
    for m in re.finditer(r'new\("([a-z-]+)",\s*"[^"]*",\s*\[(.*?)\],\s*\[(.*?)\],\s*\[(.*?)\],\s*\[(.*?)\],\s*\[(.*?)\]\)',
                         text, re.S):
        # the record is (name, label, Owners, Producers, Multipliers, Replayers, Payoffs)
        name, _owners, producers, mult, repl, payoffs = m.groups()
        grab = lambda s: [x.strip().strip('"') for x in s.split(",") if x.strip()]
        out[name] = {"P": grab(producers), "M": grab(mult), "R": grab(repl), "Payoff": grab(payoffs)}
    return out


def auc(wins, losses):
    if not wins or not losses:
        return None
    g = t = 0
    for a in wins:
        for b in losses:
            if a > b:
                g += 1
            elif abs(a - b) < 1e-9:
                t += 1
    return (g + t / 2) / (len(wins) * len(losses))


def perm_p(wins, losses, obs, iters=2000, seed=7):
    rng = random.Random(seed)
    pool = list(wins) + list(losses)
    n = len(wins)
    hits = 0
    for _ in range(iters):
        rng.shuffle(pool)
        a = auc(pool[:n], pool[n:])
        if a is not None and a >= obs:
            hits += 1
    return (hits + 1) / (iters + 1)


def boot_ci(wins, losses, iters=2000, seed=11):
    rng = random.Random(seed)
    out = []
    for _ in range(iters):
        w = [rng.choice(wins) for _ in wins]
        l = [rng.choice(losses) for _ in losses]
        a = auc(w, l)
        if a is not None:
            out.append(a)
    out.sort()
    return out[int(0.025 * len(out))], out[int(0.975 * len(out)) - 1]


def deck_features(ids, packages, table=None):
    """Per-resource stock / cash / complete / stranded, under the ruled definitions.

    stock_r   = P_r >= 3                      (same threshold as complete)
    cash_r    = M_r >= 1 or R_r >= 1 or Converter_r >= 1   -- never Payoffs
    complete  = stock and cash
    stranded  = stock XOR cash                (only where both sides are defined)
    fragmented= sum of stranded over participating resources
    dead      = cards sitting in a stranded package / deck size
    """
    table = table or {"M": {}, "R": {}}
    counts = collections.Counter(ids)
    per = {}
    for name in packages:
        src = table.get(name, {})
        p = sum(counts[i] for i in packages[name]["P"])
        m = sum(counts[i] for i in src.get("M", []))
        r = sum(counts[i] for i in src.get("R", []))
        cv = sum(counts[i] for i in CONVERTERS.get(name, []))
        stock = p >= TOLERATED
        from_mr = (m >= 1) or (r >= 1)
        from_cv = cv >= 1
        cash = from_mr or from_cv
        per[name] = {
            "p": p, "m": m, "r": r, "cv": cv,
            "participates": participates(name, table),
            "stock": stock, "cash": cash,
            "cash_from_MR": from_mr, "cash_from_Converter": from_cv,
            "complete": 1.0 if (stock and cash) else 0.0,
            "stranded": 1.0 if participates(name, table) and (stock != cash) else 0.0,
        }
    parts = [v for v in per.values() if v["participates"]]
    complete_any = 1.0 if any(v["complete"] for v in parts) else 0.0
    n_complete = float(sum(v["complete"] for v in parts))
    fragmented = float(sum(v["stranded"] for v in parts))
    stranded_cards = 0
    for name, v in per.items():
        if not v["stranded"]:
            continue
        src = table.get(name, {})
        stranded_cards += sum(counts[i] for i in set(packages[name]["P"]) | set(src.get("M", []))
                              | set(src.get("R", [])) | set(CONVERTERS.get(name, [])))
    return {"complete_any": complete_any, "n_complete": n_complete, "fragmented": fragmented,
            "dead_cards": (stranded_cards / len(ids)) if ids else 0.0,
            "n_participating": float(len(parts)), "per": per}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--players", type=int, default=4)
    args = ap.parse_args()

    baked = parse_baked()
    print("=" * 78)
    print("P/M/R agreement: tester's hand reading vs BakedResources")
    print("=" * 78)
    for name in sorted(MINE):
        b = baked.get(name)
        if not b:
            print(f"  {name:8} not present in BakedResources")
            continue
        for key in ("P", "M", "R"):
            mine, theirs = set(MINE[name][key]), set(b[key])
            if mine == theirs:
                print(f"  {name:8} {key}  identical ({len(mine)})")
            else:
                print(f"  {name:8} {key}  DIFFERS  tester-only={sorted(mine-theirs)}"
                      f"  baked-only={sorted(theirs-mine)}")

    decks = []
    for path in glob.glob(os.path.join(DECKS, "*.json")):
        d = json.load(io.open(path, encoding="utf-8"))
        if d.get("ascension") == 10 and d.get("players") == args.players and d.get("deck"):
            decks.append(d)
    print(f"\n{len(decks)} A10 decks at {args.players} players")

    # BakedResources is authoritative for M and R; MINE is only the cross-check.
    table = {}
    for name in MINE:
        b = baked.get(name)
        table[name] = {"M": b["M"] if b else MINE[name]["M"],
                       "R": b["R"] if b else MINE[name]["R"],
                       "P": b["P"] if b else MINE[name]["P"]}
    missing = [n for n in MINE if n not in baked]
    if missing:
        print(f"NOTE: not in BakedResources, falling back to the hand reading: {missing}")

    rows = []
    for d in decks:
        ids = [e["id"] for e in d["deck"]]
        f = deck_features(ids, MINE, table)
        f["win"] = bool(d["win"])
        f["char"] = d["character"]
        f["mad"] = 1.0 if "MAD_SCIENCE" in set(ids) else 0.0
        f["hash"] = d["run_hash"]
        rows.append(f)

    # Which resources the count actually ran over, and how cash was satisfied.
    # Silence here is what ruling 25 §1(c) forbids: a fragmented count over one
    # resource is not the same feature as one over eight.
    universe = sorted(baked) if baked else sorted(MINE)
    parts = [n for n in universe if n in MINE and participates(n, table)]
    print()
    print(f"fragmented runs over {len(parts)} of {len(universe)} resources: {', '.join(sorted(parts))}")
    skipped = [n for n in universe if n not in parts]
    print(f"  excluded: {', '.join(skipped)}")
    print("  (the rest have M, R and Converter all empty, where `stock XOR cash` would")
    print("   degenerate to `stock` -- excluded, per ruling 25 §1(c))")
    cvm = collections.Counter()
    for r in rows:
        for n, v in r["per"].items():
            if not v["participates"]:
                continue
            if v["cash_from_MR"]:
                cvm["cash from M or R"] += 1
            if v["cash_from_Converter"]:
                cvm["cash from Converter only"] += 1
    for k, v in cvm.items():
        print(f"  deck-resource pairs with {k}: {v}")

    mad = sum(r["mad"] for r in rows)
    print(f"\nvariant-type (MAD_SCIENCE) appears in {int(mad)}/{len(rows)} decks "
          f"({100*mad/len(rows):.0f}%)")

    print()
    print("=" * 78)
    print("independent AUC vs real win/loss (0.50 = no signal)")
    print("=" * 78)
    for feat in ("complete_any", "n_complete", "fragmented", "dead_cards", "n_participating"):
        line = []
        allw, alll = [], []
        for c in CHARS:
            w = [r[feat] for r in rows if r["char"] == c and r["win"]]
            l = [r[feat] for r in rows if r["char"] == c and not r["win"]]
            if len(w) < 5 or len(l) < 5:
                line.append(None)
                continue
            line.append(auc(w, l))
            allw += w
            alll += l
        obs = auc(allw, alll)
        lo, hi = boot_ci(allw, alll)
        p = perm_p(allw, alll, obs)
        print(f"  {feat:16}" + "".join(f"{v:7.2f}" if v is not None else f"{'-':>7}" for v in line)
              + f"  | pooled {obs:.2f} CI {lo:.2f}-{hi:.2f} p={p:.3f}")

    print()
    print("=" * 78)
    print("variant-type risk: is the signal just 'holds Mad Science'?")
    print("=" * 78)
    line = []
    allw, alll = [], []
    for c in CHARS:
        w = [r["mad"] for r in rows if r["char"] == c and r["win"]]
        l = [r["mad"] for r in rows if r["char"] == c and not r["win"]]
        if len(w) < 5 or len(l) < 5:
            line.append(None)
            continue
        line.append(auc(w, l))
        allw += w
        alll += l
    obs = auc(allw, alll)
    p = perm_p(allw, alll, obs)
    print(f"  {'has MAD_SCIENCE':16}" + "".join(f"{v:7.2f}" if v is not None else f"{'-':>7}" for v in line)
          + f"  | pooled {obs:.2f} p={p:.3f}")
    for feat in ("complete_any", "fragmented", "dead_cards"):
        with_mad = [r for r in rows if r["mad"] == 1.0]
        without = [r for r in rows if r["mad"] == 0.0]
        a_with = auc([r[feat] for r in with_mad if r["win"]], [r[feat] for r in with_mad if not r["win"]])
        a_wo = auc([r[feat] for r in without if r["win"]], [r[feat] for r in without if not r["win"]])
        print(f"  {feat:16} AUC with MadScience n={len(with_mad):3} -> {a_with:.2f}"
              f"   without n={len(without):3} -> {a_wo:.2f}")
    print()
    print("  If the two conditioned AUCs sit on the same side of 0.5, the feature is")
    print("  measuring the build. If they split, it is reading the event card.")


if __name__ == "__main__":
    main()
