#!/usr/bin/env python3
"""Capture a fixed evidence snapshot of the research baseline.

Copies only a hard-coded allowlist of documents and scripts into a new directory
under work/research-baselines/<UTC-id>/ (or --output), then verifies every copy
with SHA256 and records git/version/pm state in manifest.json.

Usage:
    python scripts/capture-research-baseline.py
    python scripts/capture-research-baseline.py --output work/research-baselines/my-snapshot

Limitations:
    This is an evidence snapshot, not a complete replayable game/solver
    distribution. Only the allowlisted sources are copied; binaries, caches,
    run outputs, credentials and environment variables are excluded. Nothing is
    committed, merged or published and no experiment is executed.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BASELINES_ROOT = ROOT / "work" / "research-baselines"

ALLOWLIST = (
    "TAKEOVER_REVIEW.md",
    "DECK_DELTA_MODEL_PLAN.md",
    "outputs/project-handoff.md",
    "outputs/verification-protocol.md",
    "outputs/deck-delta-model-plan.md",
    "scripts/shiv-pm-interaction.py",
    "scripts/soulnexus-sigma.py",
    "scripts/silent-base-screen.py",
    "scripts/combatsolver-oracle-adaptations.json",
    "scripts/kernel-p1-adaptations.json",
    "src/CoopBots/mod_manifest.json",
    "src/CoopBots.Kernel/UPSTREAM.json",
    "work/oracle/shiv-pm/pm.json",
    "work/coord/outbox/decksim/20260917-派单66-回执-PM交互.md",
    "third_party/CombatSolver-upstream-main/tools/OfflineSearchHarness/Program.cs",
    "third_party/CombatSolver-upstream-main/tools/OfflineSearchHarness/ModRuntime.cs",
    "third_party/CombatSolver-upstream-main/tools/OfflineSearchHarness/OfflineSearchHarness.csproj",
)

MOD_MANIFEST_PATH = "src/CoopBots/mod_manifest.json"
UPSTREAM_PATH = "src/CoopBots.Kernel/UPSTREAM.json"
PM_PATH = "work/oracle/shiv-pm/pm.json"

LIMITATIONS = (
    "This is an evidence snapshot, not a complete replayable game/solver distribution.",
    "Only the fixed allowlist is copied; other sources, binaries, caches and run outputs are excluded.",
    "Credentials, environment variables and unrelated personal files are intentionally excluded.",
    "Git state is recorded read-only; nothing is committed, merged or published and no experiment is executed.",
)


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    raise SystemExit(1)


def utc_id() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_reparse_point(path: Path) -> bool:
    try:
        info = os.lstat(path)
    except OSError:
        return False
    if stat.S_ISLNK(info.st_mode):
        return True
    attributes = getattr(info, "st_file_attributes", 0)
    return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))


def lexical_absolute(path: Path) -> Path:
    # abspath collapses "." and ".." without resolving symlinks, so linked
    # components stay visible in the lexical chain checked below.
    return Path(os.path.abspath(str(path)))


def assert_safe_destination(dest: Path, root: Path, repo_root: Path) -> None:
    repo_lex = lexical_absolute(repo_root)
    root_lex = lexical_absolute(root)
    dest_lex = lexical_absolute(dest)

    # The baseline root must itself be a strict descendant of the repository.
    try:
        root_relative = root_lex.relative_to(repo_lex)
    except ValueError:
        fail(f"baselines directory is outside the repository: {root_lex}")
    if not root_relative.parts:
        fail(f"baselines directory must not be the repository root: {root_lex}")

    # The destination must be a strict descendant of the baseline root, checked
    # lexically before resolve() so a linked work/ or research-baselines/ cannot
    # redirect the write outside the intended workspace unnoticed.
    try:
        relative = dest_lex.relative_to(root_lex)
    except ValueError:
        fail(f"destination escapes the baselines directory: {dest_lex}")
    if not relative.parts:
        fail(f"destination must not be the baselines directory itself: {dest_lex}")

    # Reject a symlink/reparse point anywhere in the existing ancestry from the
    # repository root down to the destination, including work/ and the baseline
    # root, before anything is created or resolved.
    current = repo_lex
    ancestors = [current]
    for part in root_relative.parts:
        current = current / part
        ancestors.append(current)
    for part in relative.parts:
        current = current / part
        ancestors.append(current)
    for node in ancestors:
        if (node.exists() or node.is_symlink()) and is_reparse_point(node):
            fail(f"destination crosses a reparse point/symlink: {node}")

    # Confirm the fully resolved path is still strictly below the resolved
    # baseline root, so no link can redirect the final write.
    root_resolved = root_lex.resolve()
    dest_resolved = dest_lex.resolve()
    try:
        resolved_relative = dest_resolved.relative_to(root_resolved)
    except ValueError:
        fail(f"destination resolves outside the baselines directory: {dest_lex}")
    if not resolved_relative.parts:
        fail(f"destination resolves to the baselines directory itself: {dest_lex}")

    if dest_lex.exists() or dest_lex.is_symlink():
        fail(f"destination already exists: {dest_lex}")


def assert_safe_source(source: Path, repo_root: Path) -> None:
    repo_lex = lexical_absolute(repo_root)
    source_lex = lexical_absolute(source)

    try:
        relative = source_lex.relative_to(repo_lex)
    except ValueError:
        fail(f"allowlisted source escapes the repository: {source_lex}")
    if not relative.parts:
        fail("allowlisted source must not be the repository root")

    # Refuse to follow a linked source into credentials or outside the repo.
    current = repo_lex
    for part in relative.parts:
        current = current / part
        if (current.exists() or current.is_symlink()) and is_reparse_point(current):
            fail(f"allowlisted source crosses a reparse point/symlink: {current}")

    repo_resolved = repo_lex.resolve()
    source_resolved = source_lex.resolve()
    try:
        source_resolved.relative_to(repo_resolved)
    except ValueError:
        fail(f"allowlisted source resolves outside the repository: {source_lex}")


def run_git(root: Path, args):
    try:
        completed = subprocess.run(
            # safe.directory is scoped to this invocation so a dubious-ownership
            # checkout still reports HEAD without touching the user's global
            # git config.
            ["git", "-c", f"safe.directory={root.as_posix()}", "-C", str(root), *args],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
    except OSError as error:
        return None, str(error)
    if completed.returncode != 0:
        return None, (completed.stderr or "").strip() or f"git exited {completed.returncode}"
    return completed.stdout, None


def git_state(root: Path) -> dict:
    state = {"head": None, "tracked_dirty": None, "tracked_changes": [], "error": None}
    out, error = run_git(root, ["rev-parse", "HEAD"])
    if error:
        state["error"] = error
        return state
    state["head"] = out.strip()

    out, error = run_git(root, ["status", "--porcelain", "--untracked-files=no"])
    if error:
        state["error"] = error
        return state
    changes = [line for line in out.splitlines() if line.strip()]
    state["tracked_changes"] = changes
    state["tracked_dirty"] = bool(changes)
    return state


def read_mod_manifest(base: Path) -> dict:
    entry = {"path": MOD_MANIFEST_PATH}
    try:
        data = json.loads((base / MOD_MANIFEST_PATH).read_text(encoding="utf-8"))
        entry["id"] = data.get("id")
        entry["version"] = data.get("version")
    except (OSError, ValueError) as error:
        entry["error"] = str(error)
    return entry


def read_upstream(base: Path) -> dict:
    entry = {"path": UPSTREAM_PATH}
    try:
        data = json.loads((base / UPSTREAM_PATH).read_text(encoding="utf-8"))
        entry["source"] = data.get("source")
        entry["namespace"] = data.get("namespace")
        files = data.get("files")
        entry["file_count"] = len(files) if isinstance(files, list) else None
    except (OSError, ValueError) as error:
        entry["error"] = str(error)
    return entry


def read_pm(base: Path) -> dict:
    info = {"path": PM_PATH, "available": False}
    try:
        data = json.loads((base / PM_PATH).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        info["error"] = str(error)
        return info

    info["available"] = True
    info["base"] = data.get("base")
    info["budget_ms"] = data.get("budget_ms")
    info["preregistration"] = data.get("preregistration")

    points = data.get("points")
    if not isinstance(points, dict) or not points:
        return info

    point_key = list(points.keys())[-1]
    point = points.get(point_key) or {}
    if not isinstance(point, dict):
        return info

    info["point"] = point_key
    info["deltas"] = {
        "delta_P3": point.get("delta_P3"),
        "delta_P0": point.get("delta_P0"),
        "interaction": point.get("interaction"),
        "se": point.get("se"),
        "n_per_cell": point.get("n_per_cell"),
        "sd_avg": point.get("sd_avg"),
    }

    cells = {}
    raw_cells = point.get("cells")
    if isinstance(raw_cells, dict):
        for label, cell in raw_cells.items():
            if not isinstance(cell, dict):
                continue
            cells[label] = {
                "label": cell.get("label", label),
                "n": cell.get("n"),
                "failed": cell.get("failed"),
                "mean": cell.get("mean"),
                "sd": cell.get("sd"),
                "clear": cell.get("clear"),
                "turns": cell.get("turns"),
                "hplost_mean": cell.get("hplost_mean"),
            }
    info["cells"] = cells
    return info


def copy_allowlist(dest: Path):
    entries = []
    copied = 0
    missing = 0

    for relative in ALLOWLIST:
        source = ROOT / relative
        entry = {"original_relative_path": relative}

        assert_safe_source(source, ROOT)

        if not source.is_file():
            entry["missing"] = True
            entry["reason"] = "not found" if not source.exists() else "not a regular file"
            entries.append(entry)
            missing += 1
            continue

        target = dest / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            shutil.copy2(source, target)
        except OSError as error:
            fail(f"failed to copy {relative}: {error}")

        source_hash = sha256_file(source)
        copied_hash = sha256_file(target)
        if source_hash != copied_hash:
            fail(f"SHA256 mismatch after copying {relative}")

        entry.update(
            {
                "missing": False,
                "byte_count": source.stat().st_size,
                "sha256": source_hash,
                "copied_relative_path": relative,
                "verified": True,
            }
        )
        entries.append(entry)
        copied += 1

    return entries, copied, missing


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="Capture a fixed research-baseline evidence snapshot.",
        epilog=" ".join(LIMITATIONS),
    )
    parser.add_argument(
        "--output",
        default=None,
        metavar="DIR",
        help="New directory strictly inside work/research-baselines.",
    )
    return parser.parse_args(argv)


def main(argv=None) -> int:
    args = parse_args(argv)

    if args.output:
        dest = Path(args.output)
        if not dest.is_absolute():
            dest = Path.cwd() / dest
    else:
        stamp = utc_id()
        dest = BASELINES_ROOT / stamp
        suffix = 1
        while dest.exists() or dest.is_symlink():
            dest = BASELINES_ROOT / f"{stamp}-{suffix}"
            suffix += 1

    # Use the same normalized lexical path for validation and writing, including
    # inputs containing '..'; never validate one path and mkdir another.
    dest = lexical_absolute(dest)
    # Validate before creating anything, so an unsafe --output never creates
    # work/research-baselines as a side effect.
    assert_safe_destination(dest, BASELINES_ROOT, ROOT)

    captured = datetime.now(timezone.utc).isoformat()
    dest.mkdir(parents=True)

    entries, copied, missing = copy_allowlist(dest)

    manifest = {
        "tool": "capture-research-baseline.py",
        "captured_utc": captured,
        "repo_root": str(ROOT),
        "baselines_root": str(BASELINES_ROOT),
        "output_dir": str(dest),
        "git": git_state(ROOT),
        "versions": {
            # Read from the copied snapshot so the recorded metadata describes
            # the same bytes the archive contains, not the live working tree.
            "mod_manifest": read_mod_manifest(dest),
            "upstream": read_upstream(dest),
        },
        "pm": read_pm(dest),
        "entries": entries,
        "counts": {"copied": copied, "missing": missing},
        "limitations": list(LIMITATIONS),
    }

    try:
        (dest / "manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    except OSError as error:
        fail(f"failed to write manifest: {error}")

    print(str(dest))
    print(f"copied={copied}")
    print(f"missing={missing}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
