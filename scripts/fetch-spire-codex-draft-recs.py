#!/usr/bin/env python3
"""Fetch per-card draft partners from Spire Codex into a local cache.

Source: https://spire-codex.com/api/draft-recs/cards/{id}
Used under the PolyForm Noncommercial 1.0.0 license and the Spire Codex API
terms: free for community use within the rate limits, attribution encouraged.

Rate limiting: the endpoint reports its own budget in the x-ratelimit-* headers
(observed limit 300/min). This script paces well under that, watches the
remaining count and slows down when it gets low, and honours Retry-After on a
429. Responses are cached per card, so an interrupted run resumes instead of
re-fetching.

Usage:
    python scripts/fetch-spire-codex-draft-recs.py [--limit N]
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = "https://spire-codex.com/api/draft-recs/cards/"
CARD_DUMP = "work/spire-codex/export-en.zip"
CACHE_DIR = "work/spire-codex/draft-recs"
# 540 cards at ~110/min is a few minutes; the endpoint allows 300/min, so this
# leaves a wide margin and is still polite to a community service.
INTERVAL_SECONDS = 0.55
SLOW_INTERVAL_SECONDS = 1.4
LOW_REMAINING = 40


def draftable_ids():
    import zipfile
    with zipfile.ZipFile(CARD_DUMP) as archive:
        cards = json.loads(archive.read("cards.json"))
    # Only cards that can actually be offered as a reward or a shop card.
    return sorted(
        card["id"] for card in cards
        if card.get("type") in ("Attack", "Skill", "Power") and card.get("rarity")
    )


def fetch(card_id: str):
    request = urllib.request.Request(BASE + card_id, headers={"User-Agent": "CoopBots-archetype-import/1.0"})
    with urllib.request.urlopen(request, timeout=45) as response:
        payload = json.load(response)
        limits = {k.lower(): v for k, v in response.headers.items()}
        return payload, limits


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, default=0, help="only fetch the first N ids (for a trial run)")
    args = parser.parse_args()

    if not os.path.exists(CARD_DUMP):
        print(f"error: {CARD_DUMP} not found", file=sys.stderr)
        return 1
    os.makedirs(CACHE_DIR, exist_ok=True)

    ids = draftable_ids()
    if args.limit:
        ids = ids[: args.limit]
    fetched = skipped = failed = empty = 0
    interval = INTERVAL_SECONDS
    started = time.monotonic()

    for index, card_id in enumerate(ids, 1):
        path = os.path.join(CACHE_DIR, card_id + ".json")
        if os.path.exists(path):
            skipped += 1
            continue
        remaining = None
        for attempt in range(4):
            wait = interval - (time.monotonic() - started) % interval
            if wait > 0:
                time.sleep(wait)
            try:
                payload, limits = fetch(card_id)
                try:
                    remaining = int(float(limits.get("x-ratelimit-remaining", "1000")))
                except ValueError:
                    remaining = None
                with open(path, "w", encoding="utf-8") as handle:
                    json.dump(payload, handle, ensure_ascii=False)
                fetched += 1
                if not payload.get("recommends"):
                    empty += 1
                break
            except urllib.error.HTTPError as error:
                if error.code == 429 and attempt < 3:
                    delay = float(error.headers.get("Retry-After") or 30)
                    print(f"  429 on {card_id}; waiting {delay:.0f}s", flush=True)
                    time.sleep(delay)
                    continue
                failed += 1
                print(f"  {card_id}: HTTP {error.code}", flush=True)
                break
            except Exception as error:  # noqa: BLE001 - report and keep going
                failed += 1
                print(f"  {card_id}: {type(error).__name__} {error}", flush=True)
                break
        # Stay well clear of the budget: slow down before it runs low.
        interval = SLOW_INTERVAL_SECONDS if (remaining is not None and remaining < LOW_REMAINING) else INTERVAL_SECONDS
        if index % 25 == 0 or index == len(ids):
            elapsed = time.monotonic() - started
            print(f"  {index}/{len(ids)} fetched={fetched} skipped={skipped} empty={empty} "
                  f"failed={failed} remaining={remaining} {elapsed:.0f}s", flush=True)

    print(f"done: fetched={fetched} skipped={skipped} empty={empty} failed={failed}")
    return 0 if failed == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
