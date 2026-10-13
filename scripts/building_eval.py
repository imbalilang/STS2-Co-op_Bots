#!/usr/bin/env python3
"""Measurement audit for the P×M (shiv) offline oracle batch.

This is an audit instrument, not a result generator. It reads the existing
``work/oracle/shiv-pm/pm.json`` summary plus every ``harness-result.json`` it
references, re-derives each raw outcome from the real
``OfflineCombat.DescribeRoot`` ``afterTurns`` string, and reports:

* independent per-status counts and how they differ from the old inclusion rule
  (``final_enemy_hp is not None``);
* observed wall times (``wall_s`` from pm.json, ``fightWallMs`` from the payload);
* same-seed paired contrasts on the full four-cell P0M0/P0M1/P3M0/P3M1 design,
  restricted to seeds where all four cells are complete (win/loss).

It never starts a battle, never reads secrets, and never recurses the whole
workspace: only the pm file and the per-tag ``harness-result.json`` files below
its directory are opened.

Classification precedence (first match wins):

1. explicit process timeout;
2. nonzero process exit or payload error -> error;
3. missing harness-result payload -> missing;
4. ``fightTruncated`` truthy -> truncated;
5. malformed ``afterTurns``/state -> invalid;
6. player hp<=0 and no living enemies -> ambiguous (not silently a win);
7. living player and empty enemy list or all enemy hp zero -> win;
8. player hp<=0 with living enemies -> loss;
9. both sides alive -> incomplete (a max-turn cap is NOT a completed loss).

Usage:
    python scripts/building_eval.py --audit-pm --output work/measurement-pilot/audit.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import statistics
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PM_PATH = ROOT / "work" / "oracle" / "shiv-pm" / "pm.json"
RESULT_NAME = "harness-result.json"

STATUS_TIMEOUT = "timeout"
STATUS_ERROR = "error"
STATUS_MISSING = "missing"
STATUS_TRUNCATED = "truncated"
STATUS_INVALID = "invalid"
STATUS_AMBIGUOUS = "ambiguous"
STATUS_WIN = "win"
STATUS_LOSS = "loss"
STATUS_INCOMPLETE = "incomplete"

STATUS_ORDER = [
    STATUS_WIN,
    STATUS_LOSS,
    STATUS_INCOMPLETE,
    STATUS_AMBIGUOUS,
    STATUS_TRUNCATED,
    STATUS_MISSING,
    STATUS_INVALID,
    STATUS_ERROR,
    STATUS_TIMEOUT,
]
COMPLETE_STATUSES = frozenset({STATUS_WIN, STATUS_LOSS})

CELL_ORDER = ("P0M0", "P0M1", "P3M0", "P3M1")

NORMAL_Z_95 = 1.96

PLAYER_HP_RE = re.compile(r"\bhp=(-?\d+)/(-?\d+)")
TURN_RE = re.compile(r"\bturn=(-?\d+)")
ENEMIES_RE = re.compile(r"enemies=\[([^\]]*)\]")
ENEMY_ENTRY_RE = re.compile(
    r"^(?P<enemy_id>.+?)#(?P<combat_id>-?\d+):(?P<hp>-?\d+)/(?P<max_hp>-?\d+)@(?P<intent>.*)$"
)

LIMITATIONS = [
    "Classification is a file-level re-read; it does not re-run battles and cannot "
    "recover information the harness did not record.",
    "Only complete win/loss rows count toward the mean enemy hp; win rows are floored "
    "at enemy hp=0, so the metric cannot separate low-loss from high-loss clears.",
    "The paired 95% interval is the approximate normal interval (1.96*SE), not an exact "
    "t interval, and is reported only when n>=2.",
    "fightWallMs is preserved when present but is not the source of the wall_s column in "
    "pm.json, so the two timings are not interchangeable.",
    "No causal synergy or policy-improvement claim is made; the paired contrast is a "
    "descriptive same-seed difference only.",
]


class StateError(Exception):
    """Raised when an afterTurns string cannot be re-derived."""

    def __init__(self, reason: str, missing: bool = False) -> None:
        super().__init__(reason)
        self.reason = reason
        self.missing = missing


class DuplicateSeedError(ValueError):
    """Raised when one cell contains the same seed twice."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def rel(path: Path, base: Path = ROOT) -> str:
    try:
        return path.resolve().relative_to(base.resolve()).as_posix()
    except ValueError:
        return path.name


def validate_tag(tag) -> str:
    """Accept only simple result-directory names; reject anything path-like."""
    if not isinstance(tag, str) or not tag:
        raise ValueError("tag must be a non-empty string")
    if tag in (".", ".."):
        raise ValueError(f"tag must not be {tag!r}")
    if ".." in tag:
        raise ValueError(f"tag must not contain '..': {tag!r}")
    if any(sep in tag for sep in ("/", "\\")):
        raise ValueError(f"tag must be a simple filename, got {tag!r}")
    if ":" in tag:
        raise ValueError(f"tag must not contain a drive or colon: {tag!r}")
    if Path(tag).name != tag:
        raise ValueError(f"tag must be a simple filename, got {tag!r}")
    return tag


def resolve_result_path(base_dir: Path, tag) -> Path:
    tag = validate_tag(tag)
    base = base_dir.resolve()
    path = (base / tag / RESULT_NAME).resolve()
    if base != path.parent.parent:
        raise ValueError(f"resolved result path escapes base directory for tag {tag!r}")
    return path


def parse_state(text: str) -> dict:
    """Parse a DescribeRoot afterTurns line; enemies come only from the bracket."""
    hp_match = PLAYER_HP_RE.search(text)
    if hp_match is None:
        raise StateError("player hp field absent or malformed")
    player_hp = int(hp_match.group(1))
    max_hp = int(hp_match.group(2))
    if player_hp < 0 or max_hp <= 0 or player_hp > max_hp:
        raise StateError(f"invalid player hp/max_hp {player_hp}/{max_hp}")

    enemies_match = ENEMIES_RE.search(text)
    if enemies_match is None:
        if "enemies=" in text:
            raise StateError("enemies field malformed (no bracketed list)")
        raise StateError("enemies field absent", missing=True)

    enemies = []
    content = enemies_match.group(1).strip()
    if content:
        for raw in content.split(","):
            entry = raw.strip()
            if not entry:
                raise StateError("empty enemy entry in enemies list")
            entry_match = ENEMY_ENTRY_RE.match(entry)
            if entry_match is None:
                raise StateError(f"unparsed enemy entry: {entry!r}")
            enemy_hp = int(entry_match.group("hp"))
            enemy_max_hp = int(entry_match.group("max_hp"))
            if enemy_hp < 0 or enemy_max_hp <= 0 or enemy_hp > enemy_max_hp:
                raise StateError(f"invalid enemy hp in entry: {entry!r}")
            enemies.append(
                {
                    "id": entry_match.group("enemy_id"),
                    "combat_id": int(entry_match.group("combat_id")),
                    "hp": enemy_hp,
                    "max_hp": enemy_max_hp,
                    "intent": entry_match.group("intent"),
                }
            )

    turn_match = TURN_RE.search(text)
    return {
        "turn": int(turn_match.group(1)) if turn_match else None,
        "player_hp": player_hp,
        "max_hp": max_hp,
        "enemies": enemies,
    }


def _explicit_timeout(row: dict):
    if not isinstance(row, dict):
        return None
    if row.get("timeout"):
        return "explicit timeout flag on row"
    exit_code = row.get("exit")
    if isinstance(exit_code, str) and "timeout" in exit_code.lower():
        return f"process exit reports timeout: {exit_code}"
    error = row.get("error")
    if isinstance(error, str) and "timeout" in error.lower():
        return f"row error reports timeout: {error}"
    return None


def _result(status: str, reason, record: dict) -> dict:
    out = dict(record)
    out["status"] = status
    out["reason"] = reason
    return out


def classify(row, payload, load_error=None) -> dict:
    """Classify one raw outcome using the fixed precedence order."""
    row = row if isinstance(row, dict) else {}
    record = {
        "turns": None,
        "final_enemy_hp": None,
        "player_hp": None,
        "max_hp": None,
        "fight_wall_ms": None,
    }

    timeout_reason = _explicit_timeout(row)
    if timeout_reason:
        return _result(STATUS_TIMEOUT, timeout_reason, record)

    exit_code = row.get("exit")
    if isinstance(exit_code, bool):
        exit_code = int(exit_code)
    if isinstance(exit_code, int) and exit_code != 0:
        return _result(STATUS_ERROR, f"nonzero process exit {exit_code}", record)

    if load_error == "absent":
        return _result(STATUS_MISSING, "harness-result.json absent", record)
    if load_error is not None:
        return _result(STATUS_INVALID, f"unreadable harness-result.json: {load_error}", record)
    if payload is None:
        return _result(STATUS_MISSING, "harness-result.json absent", record)
    if not isinstance(payload, dict):
        return _result(STATUS_INVALID, "harness-result.json is not a JSON object", record)

    error = payload.get("error")
    error_message = None
    if isinstance(error, dict):
        error_message = error.get("message") or error.get("code") or "payload error"
    elif error:
        error_message = str(error)
    if error_message:
        return _result(STATUS_ERROR, f"payload error: {error_message}", record)

    fight_wall_ms = payload.get("fightWallMs")
    if isinstance(fight_wall_ms, (int, float)) and not isinstance(fight_wall_ms, bool):
        record["fight_wall_ms"] = fight_wall_ms

    if payload.get("fightTruncated"):
        return _result(STATUS_TRUNCATED, "fightTruncated truthy", record)

    after_turns = payload.get("afterTurns")
    if after_turns is None or (isinstance(after_turns, str) and not after_turns.strip()):
        return _result(STATUS_MISSING, "afterTurns field absent", record)
    if not isinstance(after_turns, str):
        return _result(STATUS_INVALID, "afterTurns is not a string", record)

    try:
        state = parse_state(after_turns)
    except StateError as exc:
        status = STATUS_MISSING if exc.missing else STATUS_INVALID
        return _result(status, str(exc), record)

    record["turns"] = state["turn"]
    record["player_hp"] = state["player_hp"]
    record["max_hp"] = state["max_hp"]

    player_alive = state["player_hp"] > 0
    living = [enemy for enemy in state["enemies"] if enemy["hp"] > 0]
    record["final_enemy_hp"] = sum(enemy["hp"] for enemy in living)

    if not player_alive and not living:
        return _result(STATUS_AMBIGUOUS, "player dead with no living enemies", record)
    if player_alive and not living:
        record["final_enemy_hp"] = 0
        return _result(STATUS_WIN, "all enemies dead", record)
    if not player_alive:
        return _result(STATUS_LOSS, "player dead with living enemies", record)
    return _result(STATUS_INCOMPLETE, "player and enemies still alive", record)


def load_payload(path: Path):
    """Return (payload, load_error). load_error is None, 'absent' or a message."""
    if not path.exists():
        return None, "absent"
    try:
        return json.loads(path.read_text(encoding="utf-8")), None
    except (OSError, json.JSONDecodeError) as exc:
        return None, str(exc)


def index_cell(rows) -> dict:
    """Map seed -> row, rejecting duplicate seeds instead of overwriting."""
    by_seed = {}
    for row in rows or []:
        if not isinstance(row, dict):
            raise ValueError("cell row is not a JSON object")
        seed = row.get("seed")
        if seed is None:
            raise ValueError("cell row has no seed")
        if seed in by_seed:
            raise DuplicateSeedError(f"duplicate seed {seed!r} in cell")
        by_seed[seed] = row
    return by_seed


def describe(values) -> dict:
    """Mean/sample-sd/SE and approximate-normal 95% interval; null when n<2."""
    n = len(values)
    if n == 0:
        return {"n": 0, "mean": None, "sd": None, "se": None, "ci95_normal": None}
    mean = statistics.fmean(values)
    if n < 2:
        return {
            "n": n,
            "mean": mean,
            "sd": None,
            "se": None,
            "ci95_normal": None,
            "interval_method": "approximate normal 95% (n<2: uncertainty null)",
        }
    sd = statistics.stdev(values)
    se = sd / (n ** 0.5)
    return {
        "n": n,
        "mean": mean,
        "sd": sd,
        "se": se,
        "ci95_normal": [mean - NORMAL_Z_95 * se, mean + NORMAL_Z_95 * se],
        "interval_method": "approximate normal 95% (1.96*SE), not exact t",
    }


def _time_summary(values) -> dict:
    values = [value for value in values if isinstance(value, (int, float)) and not isinstance(value, bool)]
    if not values:
        return {"n": 0, "mean": None, "median": None, "min": None, "max": None}
    return {
        "n": len(values),
        "mean": statistics.fmean(values),
        "median": statistics.median(values),
        "min": min(values),
        "max": max(values),
    }


def summarize_cell(label: str, old_cell: dict, records: list) -> dict:
    status_counts = {status: 0 for status in STATUS_ORDER}
    for record in records:
        status_counts[record["status"]] += 1

    complete = [record for record in records if record["status"] in COMPLETE_STATUSES]
    complete_hp = [record["final_enemy_hp"] for record in complete]
    exclusions = {
        status: count
        for status, count in status_counts.items()
        if count and status not in COMPLETE_STATUSES
    }

    old_included = [
        record
        for record in records
        if isinstance(record.get("old_final_enemy_hp"), (int, float))
    ]
    old_cell = old_cell if isinstance(old_cell, dict) else {}

    return {
        "label": label,
        "records": len(records),
        "status_counts": {status: count for status, count in status_counts.items() if count},
        "complete": {
            "n": len(complete),
            "mean_enemy_hp": statistics.fmean(complete_hp) if complete_hp else None,
            "sd_enemy_hp": statistics.stdev(complete_hp) if len(complete_hp) > 1 else None,
        },
        "exclusions": exclusions,
        "old_summary": {
            "n": old_cell.get("n"),
            "failed": old_cell.get("failed"),
            "mean": old_cell.get("mean"),
            "sd": old_cell.get("sd"),
            "clear": old_cell.get("clear"),
            "included_rows": len(old_included),
        },
        "wall_s": _time_summary([record.get("wall_s") for record in records]),
        "fight_wall_ms": _time_summary([record.get("fight_wall_ms") for record in records]),
    }


def paired_contrast(seed_maps: dict, cell_keys=CELL_ORDER) -> dict:
    """Same-seed (P3M1-P3M0)-(P0M1-P0M0) over seeds complete in all four cells."""
    missing_cells = [key for key in cell_keys if key not in seed_maps]
    if missing_cells:
        return {
            "n": 0,
            "n_common": 0,
            "n_union": 0,
            "seeds": [],
            "excluded": {},
            "absent": {},
            "interaction": describe([]),
            "reason": f"missing cells: {missing_cells}",
        }

    union = set()
    for key in cell_keys:
        union |= set(seed_maps[key])
    common = set(seed_maps[cell_keys[0]])
    for key in cell_keys[1:]:
        common &= set(seed_maps[key])

    # Seeds that are in the union but not in every cell are reported explicitly:
    # dropping them silently from the intersection would hide coverage gaps.
    absent = {
        seed: [key for key in cell_keys if seed not in seed_maps[key]]
        for seed in sorted(union - common)
    }

    per_seed = []
    excluded = {}
    for seed in sorted(common):
        cells = {key: seed_maps[key][seed] for key in cell_keys}
        reasons = [
            f"{key}:{record['status']}"
            for key, record in cells.items()
            if record["status"] not in COMPLETE_STATUSES
        ]
        if reasons:
            excluded[seed] = reasons
            continue
        hp = {key: cells[key]["final_enemy_hp"] for key in cell_keys}
        delta_p3 = hp["P3M1"] - hp["P3M0"]
        delta_p0 = hp["P0M1"] - hp["P0M0"]
        per_seed.append(
            {
                "seed": seed,
                "hp": hp,
                "delta_p3": delta_p3,
                "delta_p0": delta_p0,
                "interaction": delta_p3 - delta_p0,
            }
        )

    return {
        "n": len(per_seed),
        "n_common": len(common),
        "n_union": len(union),
        "seeds": per_seed,
        "excluded": excluded,
        "absent": absent,
        "interaction": describe([row["interaction"] for row in per_seed]),
        "delta_p3": describe([row["delta_p3"] for row in per_seed]),
        "delta_p0": describe([row["delta_p0"] for row in per_seed]),
        "sign_convention": "raw remaining-enemy-hp direction (lower is better); "
        "reported only as a same-seed descriptive contrast",
    }


def _discrepancy(records: list) -> dict:
    old_included_new_excluded = []
    old_excluded_new_included = []
    won_mismatch = []
    for record in records:
        old_hp = record.get("old_final_enemy_hp")
        old_included = isinstance(old_hp, (int, float))
        new_complete = record["status"] in COMPLETE_STATUSES
        entry = {"seed": record["seed"], "tag": record["tag"], "new_status": record["status"]}
        if old_included and not new_complete:
            entry["old_final_enemy_hp"] = old_hp
            old_included_new_excluded.append(entry)
        elif new_complete and not old_included:
            entry["new_final_enemy_hp"] = record["final_enemy_hp"]
            old_excluded_new_included.append(entry)

        if "old_won" in record and record["status"] in {
            STATUS_WIN,
            STATUS_LOSS,
            STATUS_AMBIGUOUS,
        }:
            new_won = record["status"] == STATUS_WIN
            if bool(record["old_won"]) != new_won:
                won_mismatch.append(
                    {
                        "seed": record["seed"],
                        "tag": record["tag"],
                        "old_won": record["old_won"],
                        "new_status": record["status"],
                    }
                )
    return {
        "old_included_new_excluded": old_included_new_excluded,
        "old_excluded_new_included": old_excluded_new_included,
        "won_mismatch": won_mismatch,
    }


def audit_pm(pm_path: Path, base_dir: Path) -> dict:
    raw = json.loads(pm_path.read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ValueError("pm.json is not a JSON object")

    source_paths = []
    status_totals = {status: 0 for status in STATUS_ORDER}
    total_records = 0
    report = {
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "input": {
            "pm_json": rel(pm_path),
            "pm_sha256": sha256_file(pm_path),
            "base_dir": rel(base_dir),
            "result_filename": RESULT_NAME,
        },
        "source_paths": source_paths,
        "totals": {},
        "points": {},
        "limitations": list(LIMITATIONS),
    }

    points = raw.get("points")
    if not isinstance(points, dict) or not points:
        raise ValueError("pm.json has no points object")

    for point_key, point in points.items():
        if not isinstance(point, dict):
            raise ValueError(f"point {point_key!r} is not an object")
        cells = point.get("cells")
        if not isinstance(cells, dict):
            raise ValueError(f"point {point_key!r} has no cells object")

        point_report = {"cells": {}, "discrepancies": {}, "paired": {}}
        seed_maps = {}
        for cell_key, cell in cells.items():
            if not isinstance(cell, dict):
                raise ValueError(f"cell {cell_key!r} is not an object")
            by_seed = index_cell(cell.get("rows"))
            records = []
            for seed, row in by_seed.items():
                tag = row.get("tag")
                path = resolve_result_path(base_dir, tag)
                payload, load_error = load_payload(path)
                result = classify(row, payload, load_error)
                result["seed"] = seed
                result["tag"] = tag
                result["path"] = rel(path)
                result["wall_s"] = row.get("wall_s")
                result["old_final_enemy_hp"] = row.get("final_enemy_hp")
                result["input_sha256"] = sha256_file(path) if path.exists() else None
                if "won" in row:
                    result["old_won"] = row.get("won")
                records.append(result)
                source_paths.append(rel(path))
                status_totals[result["status"]] += 1
                total_records += 1

            seed_maps[cell_key] = {record["seed"]: record for record in records}
            cell_report = summarize_cell(cell.get("label", cell_key), cell, records)
            # Per-record status/reason/metrics are preserved so a reader can audit
            # every row, not just the aggregate; input_sha256 pins the raw file.
            cell_report["records"] = records
            point_report["cells"][cell_key] = cell_report

        merged = [record for records in seed_maps.values() for record in records.values()]
        point_report["discrepancies"] = _discrepancy(merged)
        point_report["paired"] = paired_contrast(seed_maps)
        point_report["point_old_summary"] = {
            "delta_P3": point.get("delta_P3"),
            "delta_P0": point.get("delta_P0"),
            "interaction": point.get("interaction"),
            "se": point.get("se"),
            "n_per_cell": point.get("n_per_cell"),
            "sd_avg": point.get("sd_avg"),
        }
        report["points"][point_key] = point_report

    report["totals"] = {
        "records": total_records,
        "status_counts": {status: count for status, count in status_totals.items() if count},
    }
    report["source_paths"] = sorted(set(source_paths))
    return report


def write_audit(report: dict, output_path) -> Path:
    """Create the report with O_EXCL; an existing audit is never overwritten."""
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(report, ensure_ascii=False, indent=1).encode("utf-8")
    fd = os.open(str(output_path), os.O_WRONLY | os.O_CREAT | os.O_EXCL)
    with os.fdopen(fd, "wb") as handle:
        handle.write(encoded)
    return output_path


def _print_summary(report: dict) -> None:
    totals = report["totals"]
    print(f"records={totals['records']} status_counts={totals['status_counts']}")
    for point_key, point in report["points"].items():
        paired = point["paired"]
        interaction = paired["interaction"]
        print(
            f"point {point_key}: paired n={paired['n']}/{paired.get('n_common')} "
            f"mean={interaction['mean']} se={interaction['se']} "
            f"ci95={interaction['ci95_normal']}"
        )
        for cell_key, cell in point["cells"].items():
            print(
                f"  {cell_key}: complete n={cell['complete']['n']} "
                f"mean={cell['complete']['mean_enemy_hp']} "
                f"old_mean={cell['old_summary']['mean']} "
                f"exclusions={cell['exclusions']}"
            )


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="P×M measurement audit")
    parser.add_argument("--audit-pm", action="store_true", help="audit the shiv-pm batch")
    parser.add_argument("--pm", default=str(PM_PATH), help="path to pm.json")
    parser.add_argument("--output", required=True, help="audit JSON output path")
    args = parser.parse_args(argv)

    if not args.audit_pm:
        parser.error("--audit-pm is required")

    pm_path = Path(args.pm)
    try:
        report = audit_pm(pm_path, pm_path.resolve().parent)
        output_path = write_audit(report, args.output)
    except FileExistsError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 3
    except (ValueError, DuplicateSeedError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    _print_summary(report)
    print(f"wrote {output_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
