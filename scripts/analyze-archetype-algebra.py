#!/usr/bin/env python3
"""For one archetype, lay out the algebra: which card is the engine, which is the
multiplier, and which one makes the resource grow super-linearly.

The Doom archetype is the worked example that produced this script. Reading the
cards alone gives the wrong answer: END_OF_DAYS and TIME'S UP look like the
payoffs, and they are — but neither makes the Doom stack grow faster than
linearly, so neither is the strongest card in the deck. NO_ESCAPE is, because
its text ("apply 10 Doom, plus 5 more for every 10 Doom already on the target")
is a recurrence: D -> D + 10 + D/2, i.e. about x1.5 per cast. That is only
visible once you (a) read the *power* definition rather than the card text, and
(b) substitute the actual numbers.

So for each archetype this prints:

  1. every defining card with cost, type and its real numbers from `vars`
  2. every power the cards reference, with the power's own description — this is
     where the mechanic actually lives (Doom is "at the end of the enemy turn,
     if this creature has Amount or less HP, it dies", which is a kill
     threshold, not damage)
  3. an automatic role tag per card (engine / multiplier / converter /
     frequency / timing / body)
  4. a RECURRENCE CANDIDATE flag: a card whose effect reads a quantity that
     itself grows, which is the only way a resource compounds

Inputs: work/spire-codex/{cards/cards.json, powers/powers.json, archetypes.json}
Usage:
    python scripts/analyze-archetype-algebra.py                 # every archetype
    python scripts/analyze-archetype-algebra.py --character SILENT
    python scripts/analyze-archetype-algebra.py --cards NO_ESCAPE,TIMES_UP,REAPER_FORM
"""
import argparse
import glob
import io
import json
import os
import re

CARDS = "work/spire-codex/cards/cards.json"
POWERS = "work/spire-codex/powers/powers.json"
ARCHETYPES = "work/spire-codex/archetypes.json"

# Text patterns that decide a card's job. Order matters: the first match wins,
# and the recurrence test comes first because it is the one that changes the
# conclusion.
PATTERNS = [
    # Reads a quantity that itself grows -> the only shape that compounds.
    # Widen the gaps generously: the raw text carries {Placeholder:diff()} tokens
    # and [gold] markup between the words that matter.
    ("RECURRENCE", re.compile(r"for every .{0,60}?already|"
                              r"plus an additional .{0,40}?for every|"
                              r"equal to the .{0,40}?already|"
                              r"for each .{0,60}?already (been )?(channel|play|draw|discard|spent|create)|"
                              r"for (each|every) .{0,40}?(in your (exhaust|discard) pile)|"
                              r"for each .{0,40}?this combat", re.I)),
    ("TIMING", re.compile(r"kill enem|if this kills|dies?\.|at least as much .{0,20}as hp", re.I)),
    ("REPLAY", re.compile(r"play every|trigger all|put all|return (it|them|all)|"
                          r"add (a )?cop(y|ies) of|shuffle all|gain replay", re.I)),
    ("MULTIPLIER", re.compile(r"\bdoubl|\btripl|deal double|takes double|"
                              r"additional (damage|block|time)|gain .{0,20}for each", re.I)),
    ("FREQUENCY", re.compile(r"whenever you (play|apply|create|attack|spend|gain|draw|evoke)|"
                             r"whenever a card|whenever (an? )?(attack|power|skill)|"
                             r"this turn, (deal|apply|gain)|at the (start|end) of (your|each) turn", re.I)),
    ("CONVERTER", re.compile(r"(deal|gain|apply|kill).{0,40}equal to|"
                             r"if .{0,40}has at least|apply that much|"
                             r"deal damage equal to", re.I)),
    ("MULTIPLY_RAMP", re.compile(r"increase the damage of all|"
                                 r"permanently|this combat", re.I)),
]


def text(card):
    return ((card.get("description_raw") or "") + " " + str(card.get("keywords") or "")).lower()


def referenced_powers(card):
    """Names of powers a card touches, from placeholders and powers_applied."""
    names = set()
    for token in re.findall(r"\{(\w*Power)\b", card.get("description_raw") or ""):
        names.add(re.sub(r"Power$", "", token))
    for p in card.get("powers_applied") or []:
        if isinstance(p, dict) and p.get("power"):
            names.add(str(p["power"]))
    return names


def build_power_index():
    if not os.path.exists(POWERS):
        return {}
    data = json.load(io.open(POWERS, encoding="utf-8"))
    items = data if isinstance(data, list) else (data.get("powers") or [])
    index = {}
    for p in items:
        for key in (p.get("id"), p.get("name"), p.get("power_key")):
            if key:
                index[str(key).lower()] = p
    return index


def role_of(card):
    body = text(card)
    for name, pattern in PATTERNS:
        if pattern.search(body):
            return name
    if card.get("type") == "Attack" or (card.get("damage") or 0) > 0:
        return "BODY"
    if card.get("block") or card.get("cards_draw") or card.get("energy_gain"):
        return "SUPPORT"
    return "FILLER"


def values(card):
    v = card.get("vars") or {}
    up = card.get("upgrade") or {}
    if not v:
        return ""
    parts = [f"{k}={v[k]}" for k in sorted(v) if isinstance(v[k], (int, float))]
    if up:
        parts.append("upg " + ",".join(f"{k}{val}" for k, val in sorted(up.items())))
    return " ".join(parts)


def show(name, ids, cards, powers):
    print()
    print("=" * 78)
    print(f"{name}")
    print("=" * 78)
    flagged = []
    for cid in ids:
        c = cards.get(cid)
        if not c:
            print(f"  {cid}: not in this build")
            continue
        role = role_of(c)
        if role == "RECURRENCE":
            flagged.append(cid)
        cost = c.get("cost")
        print(f"  {cid:22} {str(cost):>3}费 {str(c.get('type')):6} {role:10} {values(c)}")
        print(f"      {(c.get('description_raw') or '').replace(chr(10),' / ')[:150]}")
        for pname in sorted(referenced_powers(c)):
            p = powers.get(pname.lower())
            if p:
                print(f"      POWER {pname}: {(p.get('description_raw') or '').replace(chr(10),' ')[:150]}")
    if flagged:
        print(f"  >> RECURRENCE CANDIDATE (the resource grows super-linearly here): {', '.join(flagged)}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--character")
    ap.add_argument("--cards", help="analyse this comma-separated card list instead of an archetype")
    args = ap.parse_args()

    cards = {c["id"]: c for c in json.load(io.open(CARDS, encoding="utf-8"))}
    powers = build_power_index()

    if args.cards:
        ids = [c.strip().upper() for c in args.cards.split(",") if c.strip()]
        show("adhoc: " + ", ".join(ids), ids, cards, powers)
        return

    if not os.path.exists(ARCHETYPES):
        raise SystemExit(f"missing {ARCHETYPES}; run scripts/import-spire-codex-archetypes.py")
    data = json.load(io.open(ARCHETYPES, encoding="utf-8"))
    for ch, lst in data["characters"].items():
        if args.character and ch.upper() != args.character.upper():
            continue
        for arch in lst:
            ids = [(c.get("id") if isinstance(c, dict) else str(c)).replace("CARD.", "")
                   for c in (arch.get("defining_cards") or [])]
            if ids:
                show(f"{ch} — {arch.get('name')}", ids, cards, powers)


if __name__ == "__main__":
    main()
