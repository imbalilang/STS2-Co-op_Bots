#!/usr/bin/env python3
"""Cache the card compendium from Spire Codex.

Source: https://spire-codex.com/api/cards — same service and terms as the other
fetch scripts here (PolyForm Noncommercial 1.0.0, community use within the rate
limits, attribution encouraged).

Why: the run cache holds card ids and upgrade levels and nothing else. Answering
"is a winning deck more skills than attacks" or "does it cut its starter cards"
needs the type, rarity and cost behind each id, and the per-player party-size
questions need `multiplayer_only`. One request, cached to disk.

Usage:
    python scripts/fetch-spire-codex-cards.py [--refresh]
"""
import argparse
import io
import json
import os
import urllib.request

URL = "https://spire-codex.com/api/cards"
OUT = "work/spire-codex/cards/cards.json"
USER_AGENT = "CoopBots-deck-sim/1.0 (local validation; contact: mod repo)"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--refresh", action="store_true")
    args = ap.parse_args()
    if os.path.exists(OUT) and not args.refresh:
        cards = json.load(io.open(OUT, encoding="utf-8"))
        print(f"{len(cards)} cards already cached at {OUT}")
        return
    request = urllib.request.Request(URL, headers={"User-Agent": USER_AGENT,
                                                   "Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        cards = json.loads(response.read().decode("utf-8"))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with io.open(OUT, "w", encoding="utf-8") as handle:
        json.dump(cards, handle, ensure_ascii=False)
    print(f"{len(cards)} cards -> {OUT}")


if __name__ == "__main__":
    main()
