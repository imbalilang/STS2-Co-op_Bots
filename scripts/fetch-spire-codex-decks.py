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


def list_runs(character, needed, win, min_floors=0, players=0):
    """A10 standard runs for one character, newest first.

    Losing runs are filtered by floors reached: a deck that died in act 1 says
    nothing about deck quality, and the comparison that matters is a finished deck
    against a finished deck.
    """
    hashes, page = [], 1
    while len(hashes) < needed and page <= 30:
        # Filtering server-side by party size is the only way to get a usable
        # sample for a given party: the unscaled feed is mostly solo runs.
        party = f"&players={players}" if players else ""
        url = (f"{LIST}?win={'true' if win else 'false'}&ascension_min=10&ascension_max=10"
               f"&game_mode=standard&character={character}&limit=50&page={page}{party}")
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


def picks_for(payload, player_index):
    """Every card this player added, in the order the run recorded them."""
    picks = []
    for act in payload.get("map_point_history") or []:
        for node in act:
            stats = node.get("player_stats") or []
            if not stats:
                continue
            own = stats[min(player_index, len(stats) - 1)]
            for card in own.get("cards_gained") or []:
                picks.append(str(card.get("id", "")).replace("CARD.", ""))
    return picks


def fetch_deck(run_hash):
    """The finished deck of one run, or None when the API has no detail for it."""
    path = os.path.join(RUN_CACHE, run_hash + ".json")
    if os.path.exists(path):
        with io.open(path, encoding="utf-8") as handle:
            cached = json.load(handle)
        # Records written before the player count was captured are re-fetched:
        # party size changes both sides of the comparison (the boss scales with it
        # and so does the damage each player takes), so a sample that mixes sizes
        # cannot be calibrated against anything.
        if "players" in cached and cached.get("picks"):
            return cached
    payload = get(SHARED + run_hash)
    players = payload.get("players") or []
    if not players:
        return None
    player_index = int(payload.get("player_index") or 0)
    player = players[min(player_index, len(players) - 1)]
    record = {
        "run_hash": run_hash,
        "character": (player.get("character") or "").replace("CHARACTER.", ""),
        "players": len(players),
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
        # The order the deck was built in, floor by floor. Without it a run is only
        # a final deck list, and "when did this deck find its plan" — the question a
        # drafting change is judged on — cannot be asked of real runs at all.
        #
        # The card list hangs off the *player's* stats within the node, not off the
        # node: reading node["cards_gained"] finds nothing and yields an empty list
        # for every run, which then reads downstream as "no run ever found a plan".
        "picks": picks_for(payload, player_index),
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
    parser.add_argument("--refresh-meta", action="store_true",
                        help="re-fetch the party size for every cached run that is missing it, "
                             "instead of browsing the feed for new runs")
    parser.add_argument("--players", type=int, default=0,
                        help="only keep runs with this party size (0 = any). Party size is a "
                             "scaling input, so a calibration sample must not mix them.")
    parser.add_argument("--min-floors", type=int, default=40,
                        help="a losing run must have got this far to count as a finished deck")
    args = parser.parse_args()

    characters = [c.upper() for c in (args.character or CHARACTERS)]

    if args.refresh_meta:
        # Walking the cache rather than the feed, because the feed only reaches
        # back so far: a quota-limited sweep leaves the older cached runs without a
        # party size forever, and the validation drops those records silently.
        os.makedirs(RUN_CACHE, exist_ok=True)
        stale = []
        for name in sorted(os.listdir(RUN_CACHE)):
            if not name.endswith(".json"):
                continue
            try:
                with io.open(os.path.join(RUN_CACHE, name), encoding="utf-8") as handle:
                    record = json.load(handle)
            except (ValueError, OSError):
                stale.append(os.path.splitext(name)[0])
                continue
            if ("players" not in record or not record.get("players")
                    or not record.get("picks")):
                stale.append(record.get("run_hash") or os.path.splitext(name)[0])
        print(f"{len(stale)} cached runs need refreshing", flush=True)
        done = 0
        failed = 0
        for run_hash in stale:
            try:
                fetch_deck(run_hash)
            except Exception as error:
                failed += 1
                if failed <= 3:
                    print(f"  {run_hash}: {type(error).__name__}: {error}", flush=True)
                continue
            done += 1
            if done % 50 == 0:
                print(f"  {done}/{len(stale)}", flush=True)
        print(f"refreshed {done}/{len(stale)}, {failed} failed", flush=True)
        # Every run failing is not a data problem, it is this script being broken,
        # and the per-run handler above would otherwise bury it in 800 identical
        # lines that a piped tail never shows.
        if stale and done == 0:
            raise SystemExit("refresh failed for every cached run — the fetch itself is broken")
        return

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
    # Only records carrying the full metadata count as cached. An entry written
    # before the party size was captured is re-fetched rather than trusted, and
    # leaving it out of `have` is what gets it back into the wanted list.
    have = set()
    for name in os.listdir(RUN_CACHE):
        if not name.endswith(".json"):
            continue
        try:
            with io.open(os.path.join(RUN_CACHE, name), encoding="utf-8") as handle:
                record = json.load(handle)
        except (ValueError, OSError):
            continue
        if "players" in record and record.get("picks"):
            have.add(record.get("run_hash") or os.path.splitext(name)[0])
    print(f"{len(have)} runs cached with full metadata")

    for character in characters:
        for win in (True, False):
            if win and args.losing_only:
                continue
            if not win and not args.with_losing:
                continue
            # Losers have to have got somewhere: a deck that died on floor 4 is not
            # a finished deck and cannot be compared against one.
            wanted = [h for h in list_runs(character, args.per_character * 3, win,
                                           min_floors=args.min_floors, players=args.players)
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
                if args.players and record.get("players") != args.players:
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
