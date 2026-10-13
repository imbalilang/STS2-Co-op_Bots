"""Observe every fresh room in a complete ascent; combat wins are not run wins.

The named P0 rules are promoted unchanged from the locally validated observer.
Physical-reference/qualification cache misses remain distinct from semantic drift.
This module only reads bytes and never starts or closes any process.
"""
import re

NATIVE_COMBAT_WIN = re.compile(r"^\[INFO\] CHARACTER\.[A-Z_]+ has won against encounter ENCOUNTER\.[A-Z_]+\. That's \d+ wins$")
RUN_ENDING = re.compile(r"CoopBots live test: run outcome — victory=(true|false) act=(\S+) floor=(\S+)")
P0_PATTERNS = (
    ("pending-power-fork", re.compile(r"Cannot fork with pending Power amount changes:")),
    ("recorded-native-drift", re.compile(r"\bplan step drift\b")),
    ("next-turn-native-drift", re.compile(r"next-turn state differs from prediction:")),
    ("targeted-power-native-drift", re.compile(r"targeted power seats differ")),
    ("complete-contract-semantic-drift", re.compile(r"cacheMissAxis=semantics\s+field=\S+\s+expected=.*?\s+actual=")),
    ("named-R9-drift", re.compile(r"\b(?:R9|P0-SIM(?:-\d+)?)\b[^\r\n]*(?:drift|mismatch|diverg|漂移)", re.I)),
    ("capture-projection-native-drift", re.compile(r"\bCoopBots kernel: capture projection differs from live \(")),
    ("stored-victory-replay-native-drift", re.compile(r"\bCoopBots kernel: tournament script dropped: live replay no longer confirms the stored victory:")),
    ("live-enemy-set-native-drift", re.compile(r"\bCoopBots kernel: tournament script dropped: the live enemy set no longer matches the script step \(")),
    ("live-seat-set-native-drift", re.compile(r"\bCoopBots kernel: tournament script dropped: the live living-seat set no longer matches the script step \(")),
    ("plan-boundary-native-drift", re.compile(r"\bCoopBots kernel: plan boundary drift \(logged, not refused\):[^\r\n]*\bexpected=.*?\s+actual=")),
    ("planned-choice-native-drift", re.compile(r"\bCoopBots kernel: (?:tournament script dropped: )?planned choice differs from prediction:")),
    ("planned-choice-native-fallback", re.compile(r"\bCoopBots planned choice diverged: [^\r\n]*; using deterministic fallback\.")),
)


def fresh_text(current, before=b""):
    return current[len(before) if current.startswith(before) else 0:].decode("utf-8-sig", errors="replace")


def observe(current, before=b""):
    appended = current.startswith(before)
    offset = len(before) if appended else 0
    first = current[:offset].count(b"\n") + 1
    result = {"log_relation": "appended" if appended else "replaced_or_truncated",
              "fresh_byte_offset": offset, "combat_summaries": [], "native_combat_wins": [],
              "run_endings": [], "p0_diagnostics": []}
    for number, raw in enumerate(current[offset:].splitlines(), first):
        line = raw.decode("utf-8", errors="replace")
        item = {"line": number, "raw": line}
        if "CoopBots kernel combat summary:" in line:
            result["combat_summaries"].append(item)
        if NATIVE_COMBAT_WIN.fullmatch(line):
            result["native_combat_wins"].append(item)
        if RUN_ENDING.search(line):
            result["run_endings"].append(item)
        for kind, pattern in P0_PATTERNS:
            if pattern.search(line):
                result["p0_diagnostics"].append({**item, "kind": kind})
                break
    return result


def stopping_control(observation):
    if observation["p0_diagnostics"]:
        return "p0_diagnostic"
    if observation["run_endings"]:
        return "terminal_observed"
    return None


def require_retained_lock(row):
    return bool(row.get("watcher_still_running") or row.get("sentinel_cleanup", {}).get("active")
                or row.get("game_close", {}).get("status") == "failed"
                or row.get("sentinel_cleanup", {}).get("status") == "failed")
