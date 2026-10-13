#!/usr/bin/env python3
"""Prepare local inputs for a10-benchmark.py using only the standard library.

freeze consumes a full-gate verified ZIP and snapshots its payload and routes.
reserve generates disjoint random development/holdout seeds and checks only the
explicit local history roots. Neither command deploys, starts Steam, runs dotnet,
or uploads private artifacts. Keep all generated evidence outside Git.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import uuid
import zipfile

REPO = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("community_a10", REPO / "scripts/a10-benchmark.py")
a10 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(a10)


def checked(path, expected, label):
    if not expected:
        raise ValueError(label + ": missing SHA256")
    a10.checked_file(path, expected, label)


def freeze(package, output):
    package, output = Path(package).resolve(), Path(output).resolve()
    if output.exists():
        raise ValueError("freeze output must be a fresh directory")
    proof_path = package.with_suffix(".verification.json")
    proof = a10.read_json(proof_path)
    if proof.get("source") != "verified-full-gate" or proof.get("build_typeload_regression_smoke_rerun") is not False:
        raise ValueError("package must come from build.ps1 -VerifiedGateBinding")
    checked(package, proof["package_sha256"], "verified ZIP")
    binding_path = Path(proof["gate_binding"])
    checked(binding_path, proof["gate_binding_sha256"], "gate binding")
    binding = a10.read_json(binding_path)
    if (binding.get("status") != "pass" or binding.get("exit_code") != 0
            or binding.get("source_inputs_verified_after_gate") is not True
            or binding.get("test_evidence_verified") is not True):
        raise ValueError("full gate did not pass")
    if Path(binding["repo"]).resolve() != REPO:
        raise ValueError("gate belongs to another checkout")
    gate = Path(binding["full_gate"])
    checked(gate / "summary.json", binding["summary_sha256"], "gate summary")
    summary = a10.read_json(gate / "summary.json")
    if summary.get("status") != "pass":
        raise ValueError("gate summary did not pass")
    for name in ("build", "typeloadcheck", "patchsmoke-kernel"):
        rows = [p for p in summary["phases"] if p["name"] == name]
        if len(rows) != 1 or rows[0]["status"] != "passed" or rows[0]["exit_code"] != 0:
            raise ValueError("missing successful physical gate: " + name)
        checked(gate / rows[0]["log"], binding["phase_log_sha256"][rows[0]["log"]], "gate raw log")
        if name == "patchsmoke-kernel" and ("-p:EnableKernelTests=true" not in rows[0]["args"] or "--" in rows[0]["args"]):
            raise ValueError("filtered test run cannot freeze a delivery candidate")
    input_path = binding_path.parent / "final-inputs.json"
    checked(input_path, binding["input_snapshot_sha256"], "frozen inputs")
    inputs = a10.read_json(input_path)
    license_sources = ["LICENSE"] + [p.relative_to(REPO).as_posix() for p in sorted((REPO / "licenses").rglob("*")) if p.is_file()]
    if len(license_sources) < 2:
        raise ValueError("community candidate requires LICENSE and nonempty licenses/")
    for relative in license_sources:
        rows = [r for r in inputs if r["path"] == relative]
        if len(rows) != 1:
            raise ValueError("distribution license not frozen: " + relative)
    for row in inputs:
        source = Path(row["actual_path"]) if row["path"].startswith("@") else REPO / row["path"]
        if not row["path"].startswith("@") and not source.resolve().is_relative_to(REPO):
            raise ValueError("source escaped repository")
        checked(source, row["sha256"], "current frozen input")
    artifact_rows = {r["name"]: r for r in binding["test_artifacts"]}
    for name in ("CoopBots.dll", "CoopBots.Kernel.dll"):
        row = artifact_rows[name]
        checked(row["physical_path"], row["physical_sha256"], "gate runtime artifact")
        checked(gate / "build" / name, row["physical_sha256"], "gate build artifact")
        if proof["dll_sha256"][name] != row["physical_sha256"]:
            raise ValueError("package/gate dual-DLL identity differs")
    payload = {}
    with zipfile.ZipFile(package) as archive:
        for relative in license_sources:
            member = "CoopBots/" + relative
            if member not in proof["package_member_sha256"]:
                raise ValueError("distribution license missing from package proof: " + member)
            if archive.namelist().count(member) != 1:
                raise ValueError("distribution license missing from package: " + member)
            checked(REPO / relative, proof["package_member_sha256"][member], "distribution license/package identity")
        for member, expected in proof["package_member_sha256"].items():
            if archive.namelist().count(member) != 1 or a10.digest(archive.read(member)) != expected.upper():
                raise ValueError("package member differs: " + member)
        for name in a10.FILES + tuple(license_sources):
            payload[name] = archive.read("CoopBots/" + name)
            if name.endswith(".dll") and a10.digest(payload[name]) != proof["dll_sha256"][name]:
                raise ValueError("package dual-DLL differs")
    routes = []
    for name in a10.ROUTES:
        relative = "src/CoopBots/" + name
        rows = [r for r in inputs if r["path"] == relative]
        if len(rows) != 1:
            raise ValueError("route source not frozen: " + name)
        routes.append((name, REPO / relative, rows[0]["sha256"]))
    game = Path(summary["game_data"]) / "sts2.dll"
    # Validation finishes before creating the immutable output directory.
    output.mkdir(parents=True)
    target_zip = output / package.name
    shutil.copy2(package, target_zip)
    checked(target_zip, proof["package_sha256"], "copied ZIP")
    artifacts = [{"snapshot": str(target_zip), "sha256": a10.file_hash(target_zip)}]
    for name, data in payload.items():
        target = output / "payload" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        artifacts.append({"snapshot": str(target), "sha256": a10.digest(data)})
    sources = []
    for name, source, expected in routes:
        target = output / "routes" / name
        target.parent.mkdir(exist_ok=True)
        shutil.copy2(source, target)
        checked(target, expected, "copied route")
        sources.append({"snapshot": str(target), "sha256": expected})
    receipt = {"schema": "coopbots-community-frozen-candidate-v1", "created_utc": a10.utc(),
               "artifacts": artifacts, "sources": sources,
               "game": {"assembly": str(game.resolve()), "sts2_sha256": a10.file_hash(game)},
               "verification": {"gate_binding": str(binding_path), "sha256": a10.file_hash(binding_path),
                                "package_verification": str(proof_path), "package_verification_sha256": a10.file_hash(proof_path)},
               "boundary": "Local frozen package snapshots, not installed-runtime or native win evidence; no SDK/game DLL/private save copied."}
    a10.write_json(output / "receipt.json", receipt, exclusive=True)
    return {"receipt": str(output / "receipt.json"), "receipt_sha256": a10.file_hash(output / "receipt.json")}


def reserve(output, history_roots, development=20, holdout=30):
    output = Path(output).resolve()
    if output.exists():
        raise ValueError("reservation output must be a fresh directory")
    if development < 1 or holdout < 1 or development > 10000 or holdout > 10000:
        raise ValueError("seed counts must be between 1 and 10000")
    roots = [Path(p).resolve() for p in history_roots]
    if not roots or any(not p.is_dir() for p in roots):
        raise ValueError("pass existing history roots explicitly")
    if any(output.is_relative_to(p) for p in roots):
        raise ValueError("reservation output must be outside the scanned history roots")
    prefix = uuid.uuid4().hex[:16].upper()
    dev = ["CB" + prefix + "D" + str(i).zfill(5) for i in range(1, development + 1)]
    hold = ["CB" + prefix + "H" + str(i).zfill(5) for i in range(1, holdout + 1)]
    a10.validate_seeds(dev, hold)
    needles = [s.encode("ascii") for s in dev + hold]
    rows, seen = [], set()
    for root in roots:
        for source in sorted(root.rglob("*")):
            if not source.is_file() or source.is_symlink():
                continue
            source = source.resolve()
            if not source.is_relative_to(root) or source in seen:
                continue
            seen.add(source)
            h, tail, matched = hashlib.sha256(), b"", []
            before = source.stat()
            with source.open("rb") as stream:
                for data in iter(lambda: stream.read(1024 * 1024), b""):
                    h.update(data)
                    combined = tail + data
                    matched.extend(s.decode("ascii") for s in needles if s in combined)
                    tail = combined[-32:]
            after = source.stat()
            if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                raise ValueError("history file changed during seed scan: " + str(source))
            rows.append({"path": str(source), "sha256": h.hexdigest().upper(), "size": after.st_size})
            if matched:
                raise ValueError("new seed already appears in local history: " + ",".join(sorted(set(matched))))
    output.mkdir(parents=True)
    scan = output / "history-scan.json"
    a10.write_json(scan, {"engine": "Python streaming literal byte search", "roots": [str(p) for p in roots],
                         "source_files": rows, "match_found": False,
                         "boundary": "Only these explicit local roots and literal seed bytes; not a universal never-used or independent sampling proof."}, exclusive=True)
    reservation = {"schema": "coopbots-a10-seed-reservation-v1", "created_utc": a10.utc(),
                   "development": dev, "holdout": hold,
                   "status": "reserved before local execution; random UUID prefix, explicitly scoped literal history scan",
                   "scans": [{"exit": 1, "match_found": False, "log": str(scan), "log_sha256": a10.file_hash(scan),
                              "exit_meaning": "scanner no-match status compatible with benchmark input; not a subprocess exit"}]}
    a10.write_json(output / "reservation.json", reservation, exclusive=True)
    return {"reservation": str(output / "reservation.json"), "development_count": development, "holdout_count": holdout,
            "history_files_scanned": len(rows), "boundary": reservation["status"]}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("freeze", help="snapshot a ZIP produced by verified full-gate packaging")
    p.add_argument("--package", required=True); p.add_argument("--out", required=True)
    p = sub.add_parser("reserve", help="reserve disjoint random seeds before testing")
    p.add_argument("--out", required=True); p.add_argument("--history-root", action="append", required=True)
    p.add_argument("--development", type=int, default=20); p.add_argument("--holdout", type=int, default=30)
    args = parser.parse_args(argv)
    result = freeze(args.package, args.out) if args.command == "freeze" else reserve(args.out, args.history_root, args.development, args.holdout)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, RuntimeError, OSError, KeyError, zipfile.BadZipFile) as error:
        print("Community benchmark: " + str(error), file=sys.stderr)
        raise SystemExit(2)
