#!/usr/bin/env python3
"""Fetch full run details from Spire Codex for the runs already cached by hash.

Source: https://spire-codex.com/api/runs/shared/* — same service and terms as
scripts/fetch-spire-codex-decks.py (PolyForm Noncommercial 1.0.0, community use
within the rate limits, attribution encouraged).

Why this exists next to fetch-spire-codex-decks.py: that script keeps only the
finished deck, which can say "this card is in winning decks" and no more. The
full payload carries two things the finished deck cannot:

  map_point_history[act][node].player_stats[]  — per-player damage_taken,
      current_hp, hp_healed and gold at every node, so the *pressure* a party
      size actually applies can be measured instead of modelled.
  ...player_stats[].card_choices[]             — every reward screen with the
      card that was picked AND the ones that were passed over. A pick is a
      decision made at the time, so it does not carry the survivorship bias a
      finished deck list carries.

Payloads are ~140 KB each and are cached per run hash, so an interrupted crawl
resumes. Sampling is stratified by party size and win/loss so a caller can ask
for a balanced comparison instead of whatever the feed happened to return.

Usage:
    python scripts/fetch-spire-codex-run-details.py --players 4 --per-cell 40
    python scripts/fetch-spire-codex-run-details.py --players 1 --players 4 --per-cell 40
"""
import argparse
import glob
import io
import json
import os
import time
import urllib.error
import urllib.request

BASE = "https://spire-codex.com/api"
SHARED = BASE + "/runs/shared/"
CACHE_DIR = "work/spire-codex/decks/runs"
DETAIL_DIR = "work/spire-codex/runs-full"
INTERVAL_SECONDS = 0.35
USER_AGENT = "CoopBots-deck-sim/1.0 (local validation; contact: mod repo)"


def get(url, tries=3):
    for attempt in range(tries):
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT,
                                                       "Accept": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            if error.code == 429 and attempt + 1 < tries:
                time.sleep(5 * (attempt + 1))
                continue
            if error.code >= 500 and attempt + 1 < tries:
                time.sleep(2 * (attempt + 1))
                continue
            raise
        except (urllib.error.URLError, TimeoutError):
            if attempt + 1 >= tries:
                raise
            time.sleep(2 * (attempt + 1))
    raise RuntimeError("unreachable")


def cached_runs():
    """(players, win, character, run_hash) for every run the deck crawl cached."""
    out = []
    for path in glob.glob(os.path.join(CACHE_DIR, "*.json")):
        try:
            with io.open(path, encoding="utf-8") as handle:
                record = json.load(handle)
        except (ValueError, OSError):
            continue
        players = record.get("players")
        if not players or record.get("ascension") != 10:
            continue
        out.append((players, bool(record.get("win")), record.get("character") or "", record["run_hash"]))
    return sorted(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--players", type=int, action="append",
                        help="party size to sample (repeatable)")
    parser.add_argument("--per-cell", type=int, default=40,
                        help="runs per (party size, win/loss) cell")
    parser.add_argument("--character", action="append", help="limit to one character (repeatable)")
    args = parser.parse_args()

    players = args.players or [4]
    characters = [c.upper() for c in args.character] if args.character else None
    os.makedirs(DETAIL_DIR, exist_ok=True)

    runs = cached_runs()
    fetched = skipped = 0
    for party in players:
        for win in (True, False):
            pool = [r for r in runs if r[0] == party and r[1] is win
                    and (characters is None or r[2] in characters)]
            wanted = pool[:args.per_cell]
            print(f"{party}p {'win' if win else 'loss'}: {len(pool)} cached, fetching {len(wanted)}")
            for _, _, character, run_hash in wanted:
                path = os.path.join(DETAIL_DIR, run_hash + ".json")
                if os.path.exists(path) and os.path.getsize(path) > 0:
                    skipped += 1
                    continue
                try:
                    payload = get(SHARED + run_hash)
                except Exception as error:
                    print(f"  {run_hash}: {error}")
                    continue
                if not payload or not payload.get("players"):
                    continue
                with io.open(path, "w", encoding="utf-8") as handle:
                    json.dump(payload, handle, ensure_ascii=False)
                fetched += 1
                time.sleep(INTERVAL_SECONDS)
    print(f"fetched {fetched}, already cached {skipped} -> {DETAIL_DIR}")


if __name__ == "__main__":
    main()
