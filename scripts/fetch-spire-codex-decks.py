#!/usr/bin/env python3
"""Fetch real winning decks and per-card community scores from Spire Codex.

Source: https://spire-codex.com/api/runs/* and /api/runs/scores/cards
Used under the PolyForm Noncommercial 1.0.0 license and the Spire Codex API
terms: free for community use within the rate limits, attribution encouraged.

Why: the deck-construction pipeline scores decks it drafted itself, which can
only ever say "this build agrees with itself". These are the decks that actually
won Ascension 10, and the community's own per-card score, so the pipeline's
objective can be checked against something outside the repository.

Only the final deck of each run is kept — the API returns whole run histories
and the point here is the finished deck, not the path to it. Responses are
cached per run hash, so an interrupted run resumes instead of re-fetching.

Usage:
    python scripts/fetch-spire-codex-decks.py [--per-character N] [--character IRONCLAD]
"""
import argparse
import io
import json
import os
import time
import urllib.error
import urllib.request

BASE = "https://spire-codex.com/api"
LIST = BASE + "/runs/list"
SHARED = BASE + "/runs/shared/"
SCORES = BASE + "/runs/scores/cards"
CACHE_DIR = "work/spire-codex/decks"
RUN_CACHE = os.path.join(CACHE_DIR, "runs")
SCORES_OUT = "work/spire-codex/card-scores.json"
CHARACTERS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]

# The observed budget is 300/min. This paces far under it and is still polite
# to a community service, which matters more than the wall clock here.
INTERVAL_SECONDS = 0.35
USER_AGENT = "CoopBots-deck-sim/1.0 (local validation; contact: mod repo)"


def get(url, tries=3):
    for attempt in range(tries):
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT,
                                                       "Accept": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
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


def list_runs(character, needed, win, min_floors=0):
    """A10 standard runs for one character, newest first.

    Losing runs are filtered by floors reached: a deck that died in act 1 says
    nothing about deck quality, and the comparison that matters is a finished deck
    against a finished deck.
    """
    hashes, page = [], 1
    while len(hashes) < needed and page <= 30:
        url = (f"{LIST}?win={'true' if win else 'false'}&ascension_min=10&ascension_max=10"
               f"&game_mode=standard&character={character}&limit=50&page={page}")
        payload = get(url)
        runs = payload.get("runs", [])
        if not runs:
            break
        for run in runs:
            if run.get("was_abandoned"):
                continue
            if not win and (run.get("floors_reached") or 0) < min_floors:
                continue
            hashes.append(run["run_hash"])
            if len(hashes) >= needed:
                break
        page += 1
        time.sleep(INTERVAL_SECONDS)
    return hashes


def fetch_deck(run_hash):
    """The finished deck of one run, or None when the API has no detail for it."""
    path = os.path.join(RUN_CACHE, run_hash + ".json")
    if os.path.exists(path):
        with io.open(path, encoding="utf-8") as handle:
            return json.load(handle)
    payload = get(SHARED + run_hash)
    players = payload.get("players") or []
    if not players:
        return None
    player = players[0]
    record = {
        "run_hash": run_hash,
        "character": (player.get("character") or "").replace("CHARACTER.", ""),
        "ascension": payload.get("ascension"),
        "win": bool(payload.get("win")),
        "killed_by": payload.get("killed_by_encounter") or payload.get("killed_by_event"),
        "build_id": payload.get("build_id"),
        "act_count": len(payload.get("acts") or []),
        # The card id is all the pipeline needs to rebuild the card; the upgrade
        # level is what stops a smithed deck from looking like an unsmithed one.
        "deck": [{"id": (card.get("id") or "").replace("CARD.", ""),
                  "upgrades": card.get("current_upgrade_level", 0) or 0}
                 for card in player.get("deck") or []],
        "relics": [r.get("id", "").replace("RELIC.", "") if isinstance(r, dict) else str(r)
                   for r in player.get("relics") or []],
    }
    os.makedirs(RUN_CACHE, exist_ok=True)
    with io.open(path, "w", encoding="utf-8") as handle:
        json.dump(record, handle, ensure_ascii=False)
    time.sleep(INTERVAL_SECONDS)
    return record


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--per-character", type=int, default=30)
    parser.add_argument("--character", action="append", help="limit to one character (repeatable)")
    parser.add_argument("--refresh-scores", action="store_true")
    parser.add_argument("--with-losing", action="store_true",
                        help="also fetch losing runs that reached --min-floors")
    parser.add_argument("--losing-only", action="store_true")
    parser.add_argument("--min-floors", type=int, default=40,
                        help="a losing run must have got this far to count as a finished deck")
    args = parser.parse_args()

    characters = [c.upper() for c in (args.character or CHARACTERS)]

    if args.refresh_scores or not os.path.exists(SCORES_OUT):
        os.makedirs(os.path.dirname(SCORES_OUT), exist_ok=True)
        with io.open(SCORES_OUT, "w", encoding="utf-8") as handle:
            json.dump(get(SCORES), handle, ensure_ascii=False)
        print(f"card scores -> {SCORES_OUT}")

    # Which runs are already cached is read off the cache directory, not off the
    # combined file. The combined file is only written once, at the end, so a run
    # that is interrupted — or a second fetch running alongside — leaves it behind
    # the cache and every already-fetched run gets requested again.
    os.makedirs(RUN_CACHE, exist_ok=True)
    have = {os.path.splitext(name)[0] for name in os.listdir(RUN_CACHE) if name.endswith(".json")}
    print(f"{len(have)} runs already cached")

    for character in characters:
        for win in (True, False):
            if win and args.losing_only:
                continue
            if not win and not args.with_losing:
                continue
            # Losers have to have got somewhere: a deck that died on floor 4 is not
            # a finished deck and cannot be compared against one.
            wanted = [h for h in list_runs(character, args.per_character * 3, win, min_floors=args.min_floors)
                      if h not in have]
            kept = 0
            for run_hash in wanted:
                if kept >= args.per_character:
                    break
                try:
                    record = fetch_deck(run_hash)
                except Exception as error:  # a single dead run must not end the crawl
                    print(f"  {run_hash}: {error}")
                    continue
                if not record or not record["deck"]:
                    continue
                # Ascension is listed as 10 by the query, but a stale cache entry
                # from a different filter would quietly poison the validation set.
                if record.get("ascension") != 10 or record.get("win") is not win:
                    continue
                have.add(run_hash)
                kept += 1
                print(f"  {character} {'win' if win else 'loss'} {run_hash}: {len(record['deck'])} cards")
            print(f"{character} {'wins' if win else 'losses'}: {kept}")

    # No combined file: the per-run cache is what the validation reads, and a
    # summary written at the end is one more thing that can disagree with it.
    print(f"{len(have)} runs cached under {RUN_CACHE}")


if __name__ == "__main__":
    main()
