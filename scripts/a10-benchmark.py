#!/usr/bin/env python3
"""Frozen-package A10 full-run experiment. Uses S5's Steam launcher, never builds.

plan -> dry-run -> run -> analyze. bind-candidate creates a NEW immutable plan;
its ancestor's baseline attempts remain admissible by exact arm/config identity.
The factorial section is a non-executable short-combat contract, NOT win evidence.
Windows is required only for run. All other commands use the standard library.
"""
from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import statistics
import subprocess
import sys
import time
import zipfile

FILES = ("CoopBots.dll", "CoopBots.Kernel.dll", "mod_manifest.json", "THIRD_PARTY_NOTICES.md")
LICENSE_FILES = ("LICENSE", "licenses/CombatSolver-0.41.0-MIT.txt", "licenses/RandomForeseer-0.13.14-MIT.txt", "licenses/PolyForm-Noncommercial-1.0.0.md")
ROUTES = ("MapRouteOverlay.cs", "RoutePlanner.cs")
PARTY = ["SILENT", "SILENT", "IRONCLAD", "IRONCLAD"]
SCHEMA = "coopbots-a10-benchmark-v1"
REPO = Path(__file__).resolve().parents[1]
_observer_path = Path(__file__).with_name("full_ascent_observer.py")
_observer_spec = importlib.util.spec_from_file_location("coopbots_full_ascent_observer", _observer_path)
_observer = importlib.util.module_from_spec(_observer_spec)
_observer_spec.loader.exec_module(_observer)
observe = _observer.observe
fresh_text = _observer.fresh_text
stopping_control = _observer.stopping_control
require_retained_lock = _observer.require_retained_lock


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def file_hash(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def read_log(path):
    data = Path(path).read_bytes()
    # Windows PowerShell 5.1 Tee-Object writes UTF-16; Godot writes UTF-8.
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        return data.decode("utf-16", errors="replace")
    return data.decode("utf-8-sig", errors="replace")


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()


def write_json(path, value, exclusive=False):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if exclusive:
        with path.open("x", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(value, ensure_ascii=False, indent=2) + "\n")
    else:
        temp = path.with_name(path.name + ".tmp")
        temp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temp, path)


def checked_file(path, expected, label):
    if file_hash(path) != expected.upper():
        raise ValueError(f"{label}: physical SHA256 mismatch: {path}")


def freeze_arm(receipt_path):
    """Only a receipt's SNAPSHOT ZIP, installed copies and route snapshots are used."""
    receipt_path = Path(receipt_path).resolve()
    r = read_json(receipt_path)
    artifacts = r["artifacts"]
    zips = [a for a in artifacts if a["snapshot"].lower().endswith(".zip")]
    if len(zips) != 1:
        raise ValueError("receipt must identify exactly one frozen ZIP")
    z = zips[0]
    checked_file(z["snapshot"], z["sha256"], "package")
    payload = {}
    with zipfile.ZipFile(z["snapshot"]) as archive:
        # Old sealed four-file receipts remain valid. New receipts also bind
        # their optional license members; absent old members are not invented.
        names = FILES + tuple(n for n in LICENSE_FILES if "CoopBots/" + n in archive.namelist())
        for name in names:
            member = "CoopBots/" + name
            if archive.namelist().count(member) != 1:
                raise ValueError(f"package must have one {member}")
            data = archive.read(member)
            copies = [a for a in artifacts if Path(a["snapshot"]).name == Path(name).name]
            if len(copies) != 1 or digest(data) != copies[0]["sha256"].upper():
                raise ValueError(f"frozen installed/package disagreement: {name}")
            checked_file(copies[0]["snapshot"], copies[0]["sha256"], "installed snapshot")
            payload[name] = digest(data)
        version = json.loads(archive.read("CoopBots/mod_manifest.json"))["version"]
    routes = {}
    for name in ROUTES:
        rows = [s for s in r["sources"] if Path(s["snapshot"]).name == name]
        if len(rows) != 1:
            raise ValueError(f"receipt missing route source: {name}")
        checked_file(rows[0]["snapshot"], rows[0]["sha256"], "route snapshot")
        routes[name] = {"path": rows[0]["snapshot"], "sha256": rows[0]["sha256"].upper()}
    return {"receipt": str(receipt_path), "receipt_sha256": file_hash(receipt_path),
            "version": version, "package": str(Path(z["snapshot"]).resolve()),
            "package_sha256": z["sha256"].upper(), "payload": payload,
            "routes": routes, "verification": r.get("verification"),
            "game": r["game"]}


def verify_arm(arm):
    current = freeze_arm(arm["receipt"])
    if current != arm:
        raise ValueError("frozen arm receipt/package identity changed")


def workshop_root(game_root):
    return Path(game_root).resolve().parent.parent / "workshop/content/2868840"


def mod_inventory(game_root, workshop=None):
    """Conservative local and subscribed Workshop inputs, excluding known outputs."""
    mods = Path(game_root) / "mods"
    result = {str(p.relative_to(mods)).replace("\\", "/"): file_hash(p)
            for p in sorted(mods.rglob("*")) if p.is_file()
            and p.name != ".coopbots-a10-benchmark.lock"
            and p.relative_to(mods).parts[:2] != ("CoopBots", "live-recordings")
            and not (p.relative_to(mods).parts[0].lower() == "coopbots"
                     and (p.name in FILES + ("live-test.flag", "LICENSE")
                          or p.relative_to(mods).as_posix().startswith("CoopBots/licenses/")))}
    workshop = Path(workshop) if workshop is not None else workshop_root(game_root)
    for path in sorted(workshop.rglob("*")):
        relative = path.relative_to(workshop)
        if (path.is_file() and not any(part.lower() in ("logs", "recordings", "live-recordings") for part in relative.parts)
                and (path.suffix.lower() in (".dll", ".pck", ".json") or "manifest" in path.name.lower())):
            result["workshop/" + relative.as_posix()] = file_hash(path)
    return result


def mod_configuration(data_root):
    # The observed schema has mod_settings.mods_enabled and mod_settings.mod_list.
    # Freeze that subobject, retaining array order; window/audio updates are unrelated.
    # Do not infer the game's actual loaded order from the configured list alone.
    root = Path(data_root)
    return {p.relative_to(root).as_posix(): digest(canonical(read_json(p).get("mod_settings")))
            for p in sorted(root.rglob("settings.save")) if p.is_file()}


def seal(plan):
    unsigned = {k: v for k, v in plan.items() if k != "manifest_sha256"}
    plan["manifest_sha256"] = digest(canonical(unsigned))
    return plan


def load_plan(path):
    plan = read_json(path)
    if plan.get("schema") != SCHEMA or seal(dict(plan))["manifest_sha256"] != plan.get("manifest_sha256"):
        raise ValueError("manifest schema/hash invalid")
    return plan


def validate_seeds(development, holdout):
    all_seeds = development + holdout
    if not development or not holdout or len(set(all_seeds)) != len(all_seeds):
        raise ValueError("seed lists must be nonempty, unique and disjoint")
    if any(not re.fullmatch(r"[A-Z0-9]{1,32}", s) for s in all_seeds):
        raise ValueError("seed must be a stable uppercase alphanumeric string")


def make_plan(baseline_receipt, reservation_path, game_root, data_root, candidate_receipt=None, power_shell=None):
    reservation = read_json(reservation_path)
    development, holdout = reservation["development"], reservation["holdout"]
    validate_seeds(development, holdout)
    if not reservation.get("scans") or any(s["exit"] != 1 or s["match_found"] for s in reservation["scans"]):
        raise ValueError("seed reservation has no clean no-match history scan")
    for scan in reservation["scans"]:
        checked_file(scan["log"], scan["log_sha256"], "seed history scan")
    baseline = freeze_arm(baseline_receipt)
    launcher = REPO / "scripts/run-live-loop.ps1"
    host = power_shell or shutil.which("pwsh") or shutil.which("powershell.exe") or "powershell.exe"
    host = str(Path(host).resolve()) if Path(host).is_file() else host
    config = {"ascension": 10, "difficulty": "Pro", "perf": "high", "party": PARTY,
              "silence_minutes": 5, "poll_seconds": 15, "max_run_seconds": 7200}
    plan = {"schema": SCHEMA, "created_utc": utc(), "ancestors": [],
            "reservation": {"path": str(Path(reservation_path).resolve()),
                            "sha256": file_hash(reservation_path), "scan_boundary": reservation["status"]},
            "config": config, "seeds": {"development": development, "holdout": holdout},
            "game_root": str(Path(game_root).resolve()), "data_root": str(Path(data_root).resolve()),
            "game": baseline["game"], "other_mods": mod_inventory(game_root),
            "mod_environment": {"workshop_root": str(workshop_root(game_root)),
                                "workshop_exists": workshop_root(game_root).is_dir(),
                                "settings_sources": mod_configuration(data_root),
                                "settings_raw_observation": {p.relative_to(Path(data_root)).as_posix(): file_hash(p)
                                                             for p in sorted(Path(data_root).rglob("settings.save")) if p.is_file()},
                                "boundary": "All subscribed Workshop DLL/PCK/JSON/manifests plus local inputs; settings.save mod_settings subtree canonical hashes retain configured list order. Raw settings SHA is observation only. Actual enabled/load order must also be observed in each run log, not inferred from inventory."},
            "launcher": {"path": str(launcher), "sha256": file_hash(launcher)},
            "execution_helpers": {str(p): file_hash(p) for p in (_observer_path, Path(__file__).with_name("startup-owner-retry.ps1"))},
            "powershell": {"path": host, "sha256": file_hash(host) if Path(host).is_file() else None},
            "tool_sha256": file_hash(__file__),
            "machine": {"platform": platform.platform(), "machine": platform.machine(),
                        "logical_cpus": os.cpu_count(), "python": platform.python_version()},
            "arms": {"baseline": baseline, "candidate": None},
            "schedule": [],
            "factorial_short_combat": {
                "status": "contract-only; NOT executable and NOT full-run evidence",
                "cells": ["old-combat_old-building", "new-combat_old-building",
                          "old-combat_new-building", "new-combat_new-building"],
                "required_before_execution": ["four gated frozen cell packages with separated mechanism provenance",
                    "same native fight-state/RNG and deck/save snapshot per matched four-cell block",
                    "same game/route/party/tier and fixed compute budget", "physical replay/native terminal receipts"],
                "endpoints": ["team HP loss", "combat success", "search cost", "allocation/GC"],
                "boundary": "This tool's run command only performs full ascents; no cell outcome fabricated."},
            "analysis_policy": {"primary": "first attempt per arm/seed; retries remain separate",
                "target": "0.50 operational full-run win probability",
                "uncertainty": "Wilson 95%; paired Hoeffding 95% bound; both require independent seed-sampling assumptions",
                "holdout": "reserved; explicit --allow-holdout to execute, --include-holdout to inspect outcomes",
                "causal_attribution": "failures are dispositions, not automatic combat/building causal labels"}}
    if candidate_receipt:
        attach_arm(plan, freeze_arm(candidate_receipt))
    for split, seeds in plan["seeds"].items():
        for i, seed in enumerate(seeds):
            for arm in (("baseline", "candidate") if i % 2 == 0 else ("candidate", "baseline")):
                plan["schedule"].append({"id": f"{split}-{seed}-{arm}", "split": split, "seed": seed, "arm": arm})
    plan["experiment_id"] = digest(canonical({"baseline": baseline, "seeds": plan["seeds"], "config": config}))[:16]
    return seal(plan)


def attach_arm(plan, candidate):
    baseline = plan["arms"]["baseline"]
    if candidate["game"] != baseline["game"]:
        raise ValueError("candidate must use the identical game assembly/hash")
    if {n: r["sha256"] for n, r in candidate["routes"].items()} != {n: r["sha256"] for n, r in baseline["routes"].items()}:
        raise ValueError("candidate route hashes differ; route is not an experimental variable")
    plan["arms"]["candidate"] = candidate


def bind_candidate(old_path, receipt, output):
    plan = load_plan(old_path)
    if plan["arms"]["candidate"] is not None:
        raise ValueError("candidate already bound; create a new experiment for another candidate")
    plan["ancestors"].append({"path": str(Path(old_path).resolve()), "sha256": plan["manifest_sha256"]})
    attach_arm(plan, freeze_arm(receipt))
    plan["created_utc"] = utc()
    write_json(output, seal(plan), exclusive=True)
    return plan


def command_for(plan, trial, package_dir, launcher=None):
    c = plan["config"]
    return [plan.get("powershell", {}).get("path", "powershell.exe"), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(launcher or plan["launcher"]["path"]),
            "-GameRoot", plan["game_root"], "-PackageDir", str(package_dir), "-Rounds", "1",
            "-Seed", trial["seed"], "-Perf", c["perf"], "-Ascension", str(c["ascension"]),
            "-Party", ",".join(c["party"]), "-SilenceMinutes", str(c["silence_minutes"]),
            "-PollSeconds", str(c["poll_seconds"])]


def verify_environment(plan):
    expected_helpers = {str(p): file_hash(p) for p in (_observer_path, Path(__file__).with_name("startup-owner-retry.ps1"))}
    if plan.get("execution_helpers") != expected_helpers:
        raise ValueError("whole-ascent helper binding missing or changed; create a new immutable plan")
    host = plan.get("powershell", {})
    if os.name == "nt":
        if not host.get("sha256"):
            raise ValueError("PowerShell runtime is not bound; create plan on the test host")
        checked_file(host["path"], host["sha256"], "PowerShell host")
    for arm in plan["arms"].values():
        if arm:
            verify_arm(arm)
    checked_file(plan["launcher"]["path"], plan["launcher"]["sha256"], "launcher")
    checked_file(__file__, plan["tool_sha256"], "benchmark tool")
    checked_file(plan["game"]["assembly"], plan["game"]["sts2_sha256"], "game")
    environment = plan.get("mod_environment")
    if environment is None:
        raise ValueError("old plan lacks complete Workshop/configuration binding; create a new immutable plan")
    if Path(environment["workshop_root"]).is_dir() != environment["workshop_exists"]:
        raise ValueError("Workshop directory existence changed")
    if mod_inventory(plan["game_root"], environment["workshop_root"]) != plan["other_mods"]:
        raise ValueError("other installed mod inventory changed")
    if mod_configuration(plan["data_root"]) != environment["settings_sources"]:
        raise ValueError("mod enable/order configuration sources changed")
    if not (Path(plan["game_root"]) / "SlayTheSpire2.exe").is_file():
        raise ValueError("game executable missing")


def extract_package(arm, destination):
    verify_arm(arm)
    payload = Path(destination) / "CoopBots"
    payload.mkdir(parents=True)
    with zipfile.ZipFile(arm["package"]) as archive:
        # No extractall, traversal, latest outputs or current compiled DLL fallback.
        for name in arm["payload"]:
            data = archive.read("CoopBots/" + name)
            if digest(data) != arm["payload"][name]:
                raise ValueError("package changed during extraction")
            target = payload / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    return Path(destination)


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def normal_close_script(identity_path, receipt_path, game_exe=None):
    """Close only the observed PID/start-time identity; never force termination."""
    return "\n".join([
        "$ErrorActionPreference = 'Stop'",
        f"$a10ProcessIdentityPath = {ps_literal(identity_path)}",
        f"$a10CloseReceiptPath = {ps_literal(receipt_path)}",
        "$a10Close = [ordered]@{requested_utc=(Get-Date).ToUniversalTime().ToString('o'); status='pending'; mode='CloseMainWindow'; forced=$false}",
        "try {",
        "  if (!(Test-Path -LiteralPath $a10ProcessIdentityPath)) {",
        "    if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) { throw 'A10 game identity missing; existing client remains untouched' }",
        "    $a10Close.status = 'absent'; return $false",
        "  }",
        "  $a10Identity = Get-Content -LiteralPath $a10ProcessIdentityPath -Raw | ConvertFrom-Json",
        "  $a10Close.pid = $a10Identity.pid; $a10Close.start_time_ticks = $a10Identity.start_time_ticks",
        "  $a10Client = Get-Process -Id ([int]$a10Identity.pid) -ErrorAction SilentlyContinue",
        "  if (!$a10Client) { $a10Close.status = 'already_exited'; return $false }",
        "  if ($a10Client.ProcessName -ne 'SlayTheSpire2' -or $a10Client.StartTime.ToUniversalTime().Ticks.ToString() -ne [string]$a10Identity.start_time_ticks) { throw 'A10 PID identity changed; client remains untouched' }",
        "  $a10Close.executable_path = $a10Client.Path",
        f"  $a10ExpectedExe = {ps_literal(game_exe)}" if game_exe is not None else "  $a10ExpectedExe = $a10Identity.executable_path",
        "  if (!$a10ExpectedExe -or !$a10Client.Path -or [IO.Path]::GetFullPath($a10Client.Path) -ne [IO.Path]::GetFullPath($a10ExpectedExe)) { throw 'A10 PID executable differs or is missing; client remains untouched' }",
        "  $a10Close.normal_close_requested = $a10Client.CloseMainWindow()",
        "  if (!$a10Close.normal_close_requested) { throw 'A10 CloseMainWindow refused; no force fallback' }",
        "  if (!$a10Client.WaitForExit(20000)) { throw 'A10 client did not close within 20 seconds; no force fallback' }",
        "  $a10Close.status = 'closed'; return $true",
        "} catch { $a10Close.status = 'failed'; $a10Close.error = $_.Exception.Message; throw }",
        "finally { $a10Close.observed_utc=(Get-Date).ToUniversalTime().ToString('o'); $a10Close | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $a10CloseReceiptPath -Encoding UTF8 }",
    ])


def instrument_launcher(plan, arm, directory):
    """Archive a private S5 derivative with pre-Steam binding and owned normal close.

    Shared S5 bytes remain untouched. Each replacement is unique and reversible
    in the derivation receipt. Closing uses a recorded PID and creation time.
    """
    directory = Path(directory)
    source = Path(plan["launcher"]["path"]).read_bytes()
    if digest(source) != plan["launcher"]["sha256"]:
        raise ValueError("launcher source changed")
    text = source.decode("utf-8-sig")
    marker = '        Start-Process "steam://rungameid/2868840" | Out-Null'
    if text.count(marker) != 1:
        raise ValueError("S5 launcher Steam hook is not unique")
    mods = Path(plan["game_root"]) / "mods/CoopBots"
    lines = ["        # A10 benchmark additive pre-Steam physical identity check.",
             "        $a10LaunchStarted = (Get-Date).ToUniversalTime()",
             "        $a10Observed = [ordered]@{}"]
    for name in arm["payload"]:
        lines += [f"        $a10Actual = (Get-FileHash -LiteralPath {ps_literal(mods / name)} -Algorithm SHA256).Hash",
                  f"        if ($a10Actual -ne {ps_literal(arm['payload'][name])}) {{ throw 'A10 installed payload mismatch' }}",
                  f"        $a10Observed[{ps_literal(name)}] = $a10Actual"]
    lines += [f"        $a10GameHash = (Get-FileHash -LiteralPath {ps_literal(plan['game']['assembly'])} -Algorithm SHA256).Hash",
              f"        if ($a10GameHash -ne {ps_literal(plan['game']['sts2_sha256'])}) {{ throw 'A10 game mismatch' }}",
              "        $a10Routes = [ordered]@{}"]
    for name, route in arm["routes"].items():
        lines += [f"        $a10RouteHash = (Get-FileHash -LiteralPath {ps_literal(route['path'])} -Algorithm SHA256).Hash",
                  f"        if ($a10RouteHash -ne {ps_literal(route['sha256'])}) {{ throw 'A10 route snapshot mismatch' }}",
                  f"        $a10Routes[{ps_literal(name)}] = $a10RouteHash"]
    lines += [f"        $a10LauncherHash = (Get-FileHash -LiteralPath {ps_literal(plan['launcher']['path'])} -Algorithm SHA256).Hash",
              f"        if ($a10LauncherHash -ne {ps_literal(plan['launcher']['sha256'])}) {{ throw 'A10 launcher mismatch' }}",
              f"        $a10PackageHash = (Get-FileHash -LiteralPath {ps_literal(arm['package'])} -Algorithm SHA256).Hash",
              f"        if ($a10PackageHash -ne {ps_literal(arm['package_sha256'])}) {{ throw 'A10 package mismatch' }}",
              "        $a10Identity = [ordered]@{installed=$a10Observed; game_sha256=$a10GameHash; routes=$a10Routes; launcher_sha256=$a10LauncherHash; package_sha256=$a10PackageHash; observed_utc=(Get-Date).ToUniversalTime().ToString('o'); boundary='after deployment, before Steam launch'}",
              f"        $a10Identity | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath {ps_literal(directory / 'runtime-identity-before-launch.json')} -Encoding UTF8"]
    (directory / "launcher-original.ps1").write_bytes(source)
    original_stop = text[text.index("function Stop-Client {"):text.index('\nWrite-Host "包      :')]
    safe_stop = "function Stop-Client {\n" + normal_close_script(
        directory / "owned-game-process.json", directory / "owned-game-close-launcher.json", Path(plan["game_root"]) / "SlayTheSpire2.exe") + "\n}\n"
    ownership = "\n".join([
        "        $a10FindDeadline = (Get-Date).AddSeconds(30)",
        "        do {",
        "          $a10NewClients = @(Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object { $_.StartTime.ToUniversalTime() -ge $a10LaunchStarted })",
        "          if ($a10NewClients.Count -gt 1) { throw 'A10 multiple new clients; refusing ownership' }",
        "          if ($a10NewClients.Count -eq 1) { break }",
        "          Start-Sleep -Milliseconds 250",
        "        } while ((Get-Date) -lt $a10FindDeadline)",
        "        if ($a10NewClients.Count -ne 1) { throw 'A10 no unique newly launched client PID observed' }",
        *Path(__file__).with_name("startup-owner-retry.ps1").read_text(encoding="utf-8-sig").replace(
            "'__DIAGNOSTIC_GAME_EXE_LITERAL__'", ps_literal(Path(plan["game_root"]) / "SlayTheSpire2.exe")).rstrip("\n").splitlines(),
        "        $a10OwnedIdentity = [ordered]@{pid=$a10Client.Id; start_time_ticks=$a10Client.StartTime.ToUniversalTime().Ticks.ToString(); process_name=$a10Client.ProcessName; executable_path=$a10ResolvedPath; executable_path_source=$a10PathReadSource; executable_path_read_count=$a10PathReads; executable_path_wait_ms=$a10PathWait.ElapsedMilliseconds; observed_utc=(Get-Date).ToUniversalTime().ToString('o'); boundary='unique newly created client after Steam launch; idle before launch'}",
        f"        $a10OwnedIdentity | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath {ps_literal(directory / 'owned-game-process.json')} -Encoding UTF8",
    ])
    request_marker = "        Start-Sleep -Seconds $PollSeconds"
    request = (f"\n        if (Test-Path -LiteralPath {ps_literal(directory / 'normal-stop-request.json')}) {{ [void](Stop-Client); break }}")
    replacements = [(original_stop, safe_stop),
                    ("$log = Join-Path $env:APPDATA 'SlayTheSpire2\\logs\\godot.log'", "$log = " + ps_literal(Path(plan["data_root"]) / "logs/godot.log")),
                    (marker, "\n".join(lines) + "\n" + marker + "\n" + ownership),
                    (request_marker, request_marker + request),
                    ("→ 判定卡死，杀掉客户端。", "→ 判定卡死，请求本次 PID 正常关窗。")]
    for before, after in replacements:
        if text.count(before) != 1:
            raise ValueError("S5 normal-close derivation anchor is not unique")
        text = text.replace(before, after, 1)
    derivative = directory / "launcher.ps1"
    derivative.write_text(text, encoding="utf-8-sig", newline="")
    write_json(directory / "launcher-derivation.json", {
        "original_sha256": digest(source), "derivative_sha256": file_hash(derivative),
        "unique_marker": marker, "injected_lines": lines,
        "replacements": [{"before": before, "after": after} for before, after in replacements],
        "boundary": "Private pre-Steam identity/PID observation and normal-close replacement; shared S5 unchanged; no force fallback."})
    return derivative


def game_running():
    if os.name != "nt":
        raise RuntimeError("run requires Windows and the S5 Steam installation")
    r = subprocess.run(["powershell.exe", "-NoProfile", "-Command",
                        "if(Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue){exit 1}else{exit 0}"],
                       capture_output=True)
    if r.returncode not in (0, 1):
        raise RuntimeError("could not check game processes")
    return r.returncode == 1


def stop_owned_game(directory, game_exe=None, power_shell="powershell.exe"):
    directory = Path(directory)
    subprocess.run([power_shell, "-NoProfile", "-Command",
                    normal_close_script(directory / "owned-game-process.json", directory / "owned-game-close-python.json", game_exe)],
                   check=True, capture_output=True, timeout=30)
    return read_json(directory / "owned-game-close-python.json")


@contextlib.contextmanager
def game_lock(game_root):
    lock = Path(game_root) / "mods/.coopbots-a10-benchmark.lock"
    lock.parent.mkdir(parents=True, exist_ok=True)
    try:
        fd = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL)
    except FileExistsError:
        raise RuntimeError(f"benchmark/game lock exists: {lock}; inspect owner, never auto-steal")
    token = {"pid": os.getpid(), "created_utc": utc()}
    try:
        os.write(fd, canonical(token))
        os.close(fd)
        yield token
    finally:
        if not token.get("retain") and lock.exists() and read_json(lock) == token:
            lock.unlink()


def archive_sources(data_root, dest, recording_root=None):
    """Full raw log plus known save/replay artifacts. No fabricated replay when shouldSave=false.

    Include before/after snapshots; stable reads checked, missing categories explicit.
    prefs/progress/settings are outside the requested replay/save evidence scope.
    """
    root, dest = Path(data_root), Path(dest)
    dest.mkdir(parents=True, exist_ok=True)
    candidates = []
    for p in root.rglob("*"):
        if p.is_file() and (p.name.lower() in ("godot.log", "damagemeter_combat_state.json", "current_run.save")
                            or p.suffix.lower() in (".run", ".mcr") or "replays" in p.parts):
            candidates.append((p, p.relative_to(root)))
    if recording_root is not None:
        recordings = Path(recording_root)
        candidates += [(p, Path("mod-recordings") / p.relative_to(recordings))
                       for p in recordings.rglob("*") if p.is_file()]
    rows = []
    for p, relative in sorted(candidates):
        target = dest / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            before = p.stat()
            data = p.read_bytes()
            after = p.stat()
            stable = (before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns)
            target.write_bytes(data)
            rows.append({"source": str(p), "archive": str(target), "sha256": digest(data),
                         "size": len(data), "source_mtime_ns": after.st_mtime_ns, "stable_read": stable})
        except OSError as e:
            rows.append({"source": str(p), "error": str(e)})
    present = [Path(r["source"]) for r in rows if "sha256" in r]
    result = {"created_utc": utc(), "files": rows,
              "availability": {"log": any(p.name == "godot.log" for p in present),
                               "save": any(p.name == "current_run.save" for p in present),
                               "history": any(p.suffix == ".run" for p in present),
                               "replay": any(p.suffix in (".mcr", ".replay") or "replays" in p.parts for p in present),
                               "combat_snapshot": any(p.name.endswith(".run.json") for p in present)},
              "boundary": "May include unchanged historical files; compare before/after SHA. Harness shouldSave=false may produce no current save/replay."}
    write_json(dest / "archive-manifest.json", result)
    return result


def installed_identity(plan, arm):
    mods = Path(plan["game_root"]) / "mods/CoopBots"
    hashes = {n: file_hash(mods / n) for n in arm["payload"]}
    if hashes != arm["payload"]:
        raise ValueError("runtime installed dual-DLL/payload hash mismatch")
    checked_file(plan["game"]["assembly"], plan["game"]["sts2_sha256"], "runtime game")
    environment = plan["mod_environment"]
    if mod_inventory(plan["game_root"], environment["workshop_root"]) != plan["other_mods"]:
        raise ValueError("runtime other mods changed")
    if mod_configuration(plan["data_root"]) != environment["settings_sources"]:
        raise ValueError("runtime mod enable/order configuration changed")
    return {"installed": hashes, "game_sha256": file_hash(plan["game"]["assembly"]),
            "other_mods": plan["other_mods"], "mod_settings": environment["settings_sources"], "observed_utc": utc()}


def config_issues(raw, plan, trial):
    issues = []
    starts = list(re.finditer(r"multiplayer run started — seed=(\S+) ascension=(\d+) seats=(\d+)", raw))
    if len(starts) != 1:
        issues.append(f"expected exactly one native run start, found {len(starts)}")
    else:
        m = starts[0]
        if (m[1], int(m[2]), int(m[3])) != (trial["seed"], 10, 4):
            issues.append("native seed/ascension/seats mismatch")
    c = plan["config"]
    flags = list(re.finditer(r"flag seed='([^']+)' multiplayer=(\w+) ascension=(\d+) party='([^']*)'\(resolved=(\d+)\).*?perf='([^']*)'\(active=([^)]*)\)", raw))
    if len(flags) != 1:
        issues.append("missing/ambiguous sentinel configuration log")
    elif (flags[0][1], flags[0][2].lower(), int(flags[0][3]), flags[0][4].split(","), int(flags[0][5]), flags[0][6].lower(), flags[0][7].lower()) != (
            trial["seed"], "true", 10, c["party"], 4, c["perf"], c["perf"]):
        issues.append("sentinel/native perf/party mismatch")
    lobby = re.findall(r"multiplayer lobby ready — seats=(\d+) ascension=(\d+).*?difficulty=(\w+)\); every seat is auto-piloted", raw)
    if lobby != [("4", "10", "Pro")]:
        issues.append("native A10/Pro/all-seat autopilot confirmation missing/mismatched")
    parties = re.findall(r"Preloading 'characters=([^']+)' assets", raw)
    if not parties or parties[-1].split(",") != c["party"]:
        issues.append("native character seat order missing/mismatched")
    arm = plan["arms"][trial["arm"]]
    if f"CoopBots {arm['version']} loaded." not in raw:
        issues.append("loaded version confirmation missing")
    return issues


def search_cost(raw):
    rows, malformed, excluded = [], [], []
    for number, line in enumerate(raw.splitlines(), 1):
        tournament = re.search(r"CoopBots tournament cost:\s*([^,]+)", line)
        segmented = re.search(r"search-complete:", line)
        if tournament is None and segmented is None:
            continue
        marker = tournament or segmented
        if re.search(r"\bshadow\b", line[:marker.start()], re.I):
            excluded.append({"line": number, "raw": line, "reason": "shadow diagnostic is not a submitted decision cost"})
            continue
        source = "tournament-cost" if tournament is not None else "segmented-search-complete"
        outcome = tournament[1].strip().lower() if tournament is not None else "completed"
        row = {"line": number, "raw": line, "source": source, "decision_outcome": outcome,
               "decision_completed": outcome in ("decided", "completed"),
               "native_terminal_proven": False}
        for label, unit in (("wall", "ms"), ("compute", "ms"), ("gcPause", "ms"), ("alloc", "MB")):
            m = re.search(rf"\b{label}=([0-9]+(?:\.[0-9]+)?){unit}\b", line)
            row[label + "_" + unit] = float(m[1]) if m else None
        passes = re.search(r"\bpasses=(\d+)\b", line) if tournament is not None else None
        gen0 = re.search(r"\bgen0=(\d+)\b", line)
        share = re.search(r"\(\s*([0-9]+(?:\.[0-9]+)?)%\s+of\s+compute\)", line)
        row["passes"] = int(passes[1]) if passes else None
        row["gen0_cumulative"] = int(gen0[1]) if gen0 else None
        row["gc_share_percent_logged"] = float(share[1]) if share else None
        row["missing_fields"] = [k for k in ("wall_ms", "compute_ms", "gcPause_ms", "alloc_MB") if row[k] is None]
        if tournament is not None and row["passes"] is None:
            row["missing_fields"].append("passes")
        if tournament is not None and outcome not in ("decided", "declined"):
            row["missing_fields"].append("recognized_decision_outcome")
        if row["missing_fields"]:
            malformed.append(number)
        rows.append(row)
    result = {"search_reports": len(rows), "malformed_lines": malformed, "rows": rows,
              "excluded_telemetry": excluded,
              "by_source": {}, "by_outcome": {},
              "allocation_unit": "log MB is MiB (1048576 bytes), process-global delta during search; not isolated kernel allocations",
              "compute_boundary": "Reported sum of finished pass run.ComputeMs; may exceed wall. Request waits/decision-ms/shadow diagnostics are not substituted for measured search cost.",
              "completion_boundary": "decided/completed means the planning decision emitted an outcome, not a complete native rollout, combat victory or natural run result. Declined scopes remain measured and separately labelled; gen0 is cumulative, not per-search GC count."}
    for row in rows:
        result["by_source"][row["source"]] = result["by_source"].get(row["source"], 0) + 1
        key = row["source"] + "/" + row["decision_outcome"]
        result["by_outcome"][key] = result["by_outcome"].get(key, 0) + 1
    for key in ("wall_ms", "compute_ms", "gcPause_ms", "alloc_MB"):
        values = sorted(r[key] for r in rows if r[key] is not None)
        result[key] = {"observed": len(values), "median": statistics.median(values) if values else None,
                       "p95": values[max(0, math.ceil(.95 * len(values)) - 1)] if values else None,
                       "sum": sum(values) if values else None}
    result["by_source_outcome_metrics"] = {}
    for group in result["by_outcome"]:
        chosen = [r for r in rows if r["source"] + "/" + r["decision_outcome"] == group]
        metrics = {}
        for key in ("wall_ms", "compute_ms", "gcPause_ms", "alloc_MB"):
            values = sorted(r[key] for r in chosen if r[key] is not None)
            metrics[key] = {"observed": len(values), "median": statistics.median(values) if values else None,
                            "p95": values[max(0, math.ceil(.95 * len(values)) - 1)] if values else None,
                            "sum": sum(values) if values else None}
        result["by_source_outcome_metrics"][group] = {"reports": len(chosen), "metrics": metrics}
    return result


def classify(raw, console, exit_code, issues=(), control=None):
    outcomes = list(re.finditer(r"CoopBots live test: run outcome — victory=(\w+) act=(\S+) floor=(\S+)", raw))
    natural = bool(outcomes)
    last = outcomes[-1] if natural else None
    observed = {"victory": last[1].lower() == "true", "act": last[2], "floor": last[3]} if last else None
    # Keep natural evidence even when the operational protocol is invalid.
    if observe(raw.encode())["p0_diagnostics"] or control == "p0_diagnostic":
        status = "p0_diagnostic"
    elif issues:
        status = "protocol_invalid"
    elif natural and (exit_code == 0 or control == "terminal_observed"):
        status = "victory" if observed["victory"] else "defeat"
    elif control:
        status = control
    elif "判定卡死" in console:
        status = "stall"
    elif "NGame.Quit called" in raw:
        status = "external_stop"
    elif exit_code != 0:
        status = "launcher_error"
    elif re.search(r"Unhandled exception|FATAL|could not start the multiplayer run", raw, re.I):
        status = "game_error"
    elif "How did the game end" in raw:
        status = "terminal_unclassified"
    else:
        status = "unknown_exit"
    return {"status": status, "natural_outcome_observed": observed, "issues": list(issues),
            "search_cost": search_cost(raw), "launcher_exit": exit_code,
            "control_stop": control, "boundary": "Disposition is not a causal combat/building attribution."}


def attempts_for(results, plan):
    acceptable = {plan["manifest_sha256"]} | {a["sha256"] for a in plan["ancestors"]}
    for ancestor in plan["ancestors"]:
        old = load_plan(ancestor["path"])
        if old["manifest_sha256"] != ancestor["sha256"] or old["experiment_id"] != plan["experiment_id"]:
            raise ValueError("ancestor plan identity changed")
    trials = {t["id"]: t for t in plan["schedule"]}
    rows = []
    for p in sorted(Path(results).glob("attempts/*/attempt.json")):
        row = read_json(p)
        if row["manifest_sha256"] not in acceptable or row["experiment_id"] != plan["experiment_id"]:
            raise ValueError(f"foreign attempt in results: {p}")
        if row["trial"] != trials.get(row["trial"]["id"]) or row["arm_identity"] != plan["arms"][row["trial"]["arm"]]:
            raise ValueError(f"attempt arm/seed identity changed: {p}")
        if row.get("finished_utc"):
            unsigned = {k: v for k, v in row.items() if k != "record_sha256"}
            if digest(canonical(unsigned)) != row.get("record_sha256"):
                raise ValueError(f"completed attempt record hash invalid: {p}")
            for relative, expected in row.get("evidence_files", {}).items():
                target = (p.parent / relative).resolve()
                if not target.is_relative_to(p.parent.resolve()):
                    raise ValueError("attempt evidence escaped its archive")
                checked_file(target, expected, "attempt archived evidence")
        row["record_path"] = str(p)
        rows.append(row)
    return rows


def choose_trials(plan, rows, arm=None, split="development", limit=None, retry=False):
    if limit is not None and limit < 1:
        raise ValueError("limit must be positive")
    existing = {r["trial"]["id"] for r in rows}
    selected = [t for t in plan["schedule"] if t["split"] == split and (arm is None or t["arm"] == arm)
                and (retry or t["id"] not in existing)]
    return selected[:limit] if limit is not None else selected


def finish_record(directory, row):
    directory = Path(directory)
    row["evidence_files"] = {str(p.relative_to(directory)).replace("\\", "/"): file_hash(p)
                             for p in sorted(directory.rglob("*")) if p.is_file() and p.name not in ("attempt.json", "attempt.json.tmp")}
    row["record_sha256"] = digest(canonical({k: v for k, v in row.items() if k != "record_sha256"}))
    write_json(directory / "attempt.json", row)


def preview(plan, trials, results):
    return {"dry_run": True, "side_effects": "none: no launch, sentinel, copy, lock or result writes",
            "manifest_sha256": plan["manifest_sha256"], "schedule": [
                {"trial": t, "executable": plan["arms"][t["arm"]] is not None,
                 "blocked": "candidate package not frozen" if plan["arms"][t["arm"]] is None else None,
                 "command": command_for(plan, t, Path(results) / "attempts/<attempt>/frozen-package",
                                        Path(results) / "attempts/<attempt>/launcher.ps1")}
                for t in trials]}


def archive_owned_flag(plan, trial, directory, owned):
    """Disable only this attempt's sentinel by reversible same-file move after game exit."""
    flag = Path(plan["game_root"]) / "mods/CoopBots/live-test.flag"
    receipt = {"source": str(flag), "active": flag.exists(), "observed_utc": utc()}
    if not owned:
        return {**receipt, "status": "not_owned"}
    if not flag.exists():
        return {**receipt, "status": "absent"}
    if game_running():
        return {**receipt, "status": "game_still_running"}
    lines = [trial["seed"], "perf=" + plan["config"]["perf"],
             "ascension=" + str(plan["config"]["ascension"]),
             "party=" + ",".join(plan["config"]["party"])]
    expected = "\n".join(lines).encode("ascii")
    original = flag.read_bytes()
    if original != expected:
        return {**receipt, "status": "owner_bytes_mismatch", "sha256": digest(original)}
    destination = Path(directory) / "owned-live-test.flag.disabled"
    if destination.exists():
        raise ValueError("owned sentinel archive already exists; refusing overwrite")
    # Actual execution requires Windows: rename refuses an existing target.
    # A cross-volume failure is recorded and leaves the active source intact.
    flag.rename(destination)
    archived = destination.read_bytes()
    if archived != original or flag.exists():
        raise RuntimeError("owned sentinel archive bytes/active-name verification failed")
    return {**receipt, "status": "archived", "active": False, "archive": str(destination),
            "sha256": digest(archived), "bytes": len(archived)}


def run_attempt(plan, trial, results):
    """Caller holds root-wide lock and checked idle; one attempt never overwrites another."""
    arm = plan["arms"][trial["arm"]]
    if arm is None:
        raise ValueError("candidate package not frozen")
    directory = Path(results) / "attempts" / (dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ") + "-" + trial["id"])
    directory.mkdir(parents=True)
    row = {"schema": SCHEMA, "experiment_id": plan["experiment_id"], "manifest_sha256": plan["manifest_sha256"],
           "trial": trial, "arm_identity": arm, "started_utc": utc(), "finished_utc": None,
           "result": {"status": "interrupted_unrecovered"}}
    record = directory / "attempt.json"
    write_json(record, row, exclusive=True)
    # Persist a complete native-input contract before deployment/start. Never infer it later.
    write_json(directory / "plan.json", plan, exclusive=True)
    backup = directory / "previous-install"
    backup.mkdir()
    mods = Path(plan["game_root"]) / "mods/CoopBots"
    for name in arm["payload"]:
        if (mods / name).is_file():
            (backup / name).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(mods / name, backup / name)
    recordings = mods / "live-recordings"
    row["before_archive"] = archive_sources(plan["data_root"], directory / "before", recordings)
    package = extract_package(arm, directory / "frozen-package")
    launcher = instrument_launcher(plan, arm, directory)
    command = command_for(plan, trial, package, launcher)
    row["command"] = command
    write_json(record, row)
    flag = mods / "live-test.flag"
    console_path = directory / "console.log"
    process, owned, control, exit_code, issues = None, False, None, None, []
    before_path = directory / "before/logs/godot.log"
    before_data = before_path.read_bytes() if before_path.exists() else b""
    start = time.monotonic()
    try:
        # Repeat immediately before the S5 script (which otherwise stops an existing game).
        if game_running() or flag.exists():
            raise RuntimeError("game/sentinel appeared before start; refusing launcher")
        verify_environment(plan)
        row["mod_environment_before_launcher"] = {"other_mods": mod_inventory(plan["game_root"], plan["mod_environment"]["workshop_root"]),
                                                   "mod_settings": mod_configuration(plan["data_root"]), "observed_utc": utc(),
                                                   "boundary": "Observed before starting the private S5 launcher; actual loaded order remains in the native log."}
        write_json(record, row)
        with console_path.open("wb") as console:
            process = subprocess.Popen(command, stdout=console, stderr=subprocess.STDOUT)
            owned = True
            while process.poll() is None:
                if time.monotonic() - start > plan["config"]["max_run_seconds"]:
                    control = "wall_timeout"
                    write_json(directory / "normal-stop-request.json", {"reason": control, "requested_utc": utc()})
                    break
                # Scan all fresh bytes; a first combat win must not hide later P0s.
                log = Path(plan["data_root"]) / "logs/godot.log"
                current = log.read_bytes() if log.exists() else b""
                observation = observe(current, before_data)
                next_control = stopping_control(observation)
                if next_control:
                    row["terminal_observation"] = observation
                    control = next_control
                    write_json(directory / "observed-stop.json", {"reason": control, "observations": observation, "utc": utc()})
                    write_json(directory / "normal-stop-request.json", {"reason": control, "requested_utc": utc()})
                    break
                time.sleep(2)
            try:
                exit_code = process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                issues.append("launcher did not finish after normal stop request; no force fallback")
    except KeyboardInterrupt:
        control = "external_stop"
    except Exception as e:
        issues.append(str(e))
    finally:
        if process is not None and process.poll() is None:
            write_json(directory / "normal-stop-request.json", {"reason": control or "attempt_finally", "requested_utc": utc()})
        if owned:
            try:
                row["game_close"] = stop_owned_game(directory, Path(plan["game_root"]) / "SlayTheSpire2.exe", plan.get("powershell", {}).get("path", "powershell.exe"))
            except Exception as e:
                issues.append(str(e))
                row["game_close"] = {"status": "failed", "error": str(e), "forced": False}
        if process is not None and process.poll() is None:
            try:
                exit_code = process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                issues.append("launcher remains active after normal close; no force fallback")
        row["watcher_still_running"] = process is not None and process.poll() is None
        try:
            may_archive = owned and row.get("game_close", {}).get("status") != "failed" and not row["watcher_still_running"]
            row["sentinel_cleanup"] = archive_owned_flag(plan, trial, directory, may_archive)
            if owned and row["sentinel_cleanup"]["active"]:
                issues.append("owned sentinel remains active: " + row["sentinel_cleanup"]["status"])
        except Exception as e:
            issues.append("owned sentinel archive failed: " + str(e))
            row["sentinel_cleanup"] = {"status": "failed", "active": flag.exists(), "error": str(e)}
        row["after_archive"] = archive_sources(plan["data_root"], directory / "after", recordings)
        before_hashes = {r["source"]: r["sha256"] for r in row["before_archive"]["files"] if "sha256" in r}
        row["new_or_changed_artifacts"] = [r["source"] for r in row["after_archive"]["files"]
                                           if "sha256" in r and before_hashes.get(r["source"]) != r["sha256"]]
        row["elapsed_seconds"] = time.monotonic() - start
        row["finished_utc"] = utc()
        try:
            row["runtime_identity"] = installed_identity(plan, arm)
        except Exception as e:
            issues.append(str(e))
        identity_path = directory / "runtime-identity-before-launch.json"
        if identity_path.exists():
            identity = read_json(identity_path)
            row["runtime_identity_before_launch"] = identity
            if (identity.get("installed") != arm["payload"]
                    or identity.get("game_sha256") != plan["game"]["sts2_sha256"].upper()
                    or identity.get("routes") != {n: r["sha256"] for n, r in arm["routes"].items()}
                    or identity.get("package_sha256") != arm["package_sha256"]
                    or identity.get("launcher_sha256") != plan["launcher"]["sha256"]):
                issues.append("pre-Steam physical input receipt mismatch")
        else:
            issues.append("pre-Steam physical input receipt missing")
        rawpath = directory / "after/logs/godot.log"
        raw = read_log(rawpath) if rawpath.exists() else ""
        beforepath = directory / "before/logs/godot.log"
        if beforepath.exists() and rawpath.exists() and file_hash(beforepath) == file_hash(rawpath):
            issues.append("unchanged/stale godot.log")
        if any(r.get("stable_read") is False for r in row["after_archive"]["files"]):
            issues.append("unstable final artifact read")
        current = rawpath.read_bytes() if rawpath.exists() else b""
        row["final_observation"] = observe(current, before_data)
        raw_fresh = fresh_text(current, before_data)
        issues.extend(config_issues(raw_fresh, plan, trial))
        console = read_log(console_path) if console_path.exists() else ""
        row["result"] = classify(raw_fresh, console, exit_code, issues, control)
        finish_record(directory, row)
    return row


def ingest(plan, external_dir, results, arm="baseline", split="development"):
    """Import a completed S5 wrapper run, retaining missing provenance as invalid.

    No game launch, deployment or retrospective fabricated start-time observation.
    A supplied runtime-identity.json must have the actual recorded timestamp and
    hashes; a batch launch-inputs.json can instead declare its earlier boundary.
    """
    external = Path(external_dir).resolve()
    request = read_json(external / "request.json")
    result = read_json(external / "result.json")
    trials = [t for t in plan["schedule"] if t["seed"] == request["seed"] and t["arm"] == arm and t["split"] == split]
    if len(trials) != 1 or result["seed"] != request["seed"]:
        raise ValueError("import request/result not in fixed schedule")
    trial = trials[0]
    identity = plan["arms"][arm]
    if identity is None:
        raise ValueError("cannot import unbound arm")
    rows = attempts_for(results, plan)
    source = {p.name: file_hash(p) for p in external.iterdir() if p.is_file()}
    if any(r.get("import_source") == str(external) and r.get("import_source_hashes") == source for r in rows):
        raise ValueError("identical external attempt already imported")
    if not result.get("finishedUtc"):
        raise ValueError("external attempt has not finished")
    directory = Path(results) / "attempts" / (dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ") + "-" + trial["id"])
    directory.mkdir(parents=True)
    copied = directory / "external"
    shutil.copytree(external, copied)
    issues = []
    if request.get("ascension") != 10 or request.get("perf") != plan["config"]["perf"] or request.get("party", "").split(",") != plan["config"]["party"] or request.get("difficulty") != "Pro":
        issues.append("external requested A10/Pro/high/party mismatch")
    try:
        verify_arm(identity)
        payload = Path(request["package"]) / "CoopBots"
        if {n: file_hash(payload / n) for n in identity["payload"]} != identity["payload"]:
            issues.append("external launcher package physical hash mismatch")
        requested_receipt = request.get("receipt", request.get(arm + "Receipt", request.get("baselineReceipt")))
        if file_hash(requested_receipt) != identity["receipt_sha256"]:
            issues.append("external requested receipt mismatch")
    except (OSError, ValueError, KeyError) as e:
        issues.append(f"external package/receipt verification: {e}")
    runtime = None
    runtime_path = external / "runtime-identity.json"
    if not runtime_path.exists():
        runtime_path = external.parent / "launch-inputs.json"
    if runtime_path.exists():
        runtime = read_json(runtime_path)
        shutil.copy2(runtime_path, directory / "runtime-identity.json")
        if runtime.get("installed") != identity["payload"] or runtime.get("game_sha256") != plan["game"]["sts2_sha256"].upper():
            issues.append("external runtime game/payload binding mismatch")
        try:
            observed = dt.datetime.fromisoformat(runtime["observed_utc"].replace("Z", "+00:00"))
            started = dt.datetime.fromisoformat(request["startedUtc"].replace("Z", "+00:00"))
            finished = dt.datetime.fromisoformat(result["finishedUtc"].replace("Z", "+00:00"))
            if observed > finished:
                issues.append("runtime identity was observed after run; not launch evidence")
            elif observed > started:
                issues.append("runtime identity observed after launch; not a pre-launch binding")
        except (KeyError, ValueError):
            issues.append("runtime identity missing/invalid observation timestamp")
    else:
        issues.append("external runtime game/installed hashes were not recorded; frozen-package agreement alone is not runtime proof")
    raw = read_log(copied / "godot.log")
    console = read_log(copied / "launcher.log")
    issues.extend(config_issues(raw, plan, trial))
    row = {"schema": SCHEMA, "experiment_id": plan["experiment_id"], "manifest_sha256": plan["manifest_sha256"],
           "trial": trial, "arm_identity": identity, "started_utc": request["startedUtc"],
           "finished_utc": result["finishedUtc"], "import_source": str(external), "import_source_hashes": source,
           "runtime_identity": runtime,
           "result": classify(raw, console, result["launcherExit"], issues),
           "provenance_boundary": "External S5 wrapper; runtime identity timestamp/batch scope retained, never reconstructed from current installation."}
    try:
        row["elapsed_seconds"] = (dt.datetime.fromisoformat(result["finishedUtc"].replace("Z", "+00:00"))
                                  - dt.datetime.fromisoformat(request["startedUtc"].replace("Z", "+00:00"))).total_seconds()
    except ValueError:
        pass
    write_json(directory / "plan.json", plan, exclusive=True)
    finish_record(directory, row)
    return row


def execute(plan, results, arm=None, split="development", limit=None, retry=False, allow_holdout=False):
    if split == "holdout" and (not allow_holdout or plan["arms"]["candidate"] is None):
        raise ValueError("holdout requires explicit release AND a frozen candidate")
    rows = attempts_for(results, plan)
    trials = choose_trials(plan, rows, arm, split, limit, retry)
    if any(plan["arms"][t["arm"]] is None for t in trials):
        raise ValueError("selected schedule contains unbound candidate; select --arm baseline")
    verify_environment(plan)
    with game_lock(plan["game_root"]) as lock_token:
        if game_running():
            raise RuntimeError("game already running; never kill it to start benchmark")
        if (Path(plan["game_root"]) / "mods/CoopBots/live-test.flag").exists():
            raise RuntimeError("pre-existing sentinel; inspect and remove manually before benchmark")
        for trial in trials:
            row = run_attempt(plan, trial, results)
            rows.append(row)
            if require_retained_lock(row):
                lock_token["retain"] = True
                break
            if row["result"]["status"] in ("external_stop", "protocol_invalid", "p0_diagnostic"):
                break
    return rows


def wilson(wins, n):
    if not n:
        return None
    z = 1.959963984540054
    p = wins / n
    d = 1 + z*z/n
    mid = (p + z*z/(2*n)) / d
    half = z * math.sqrt(p*(1-p)/n + z*z/(4*n*n)) / d
    return [max(0., mid-half), min(1., mid+half)]


def summarize(plan, rows, include_holdout=False):
    """First attempts remain the primary units; censoring/retries never improve denominator."""
    primary = {}
    for row in sorted(rows, key=lambda r: r["started_utc"]):
        primary.setdefault(row["trial"]["id"], row)
    report = {"manifest_sha256": plan["manifest_sha256"], "experiment_id": plan["experiment_id"],
              "total_planned": len(plan["schedule"]), "total_attempt_records": len(rows),
              "primary_unit": "first attempted run per fixed seed/arm", "splits": {},
              "factorial_short_combat": plan["factorial_short_combat"],
              "claims": "No full-run 50% claim from pending schedules/short fights; natural-result rates have censoring selection bias."}
    for split, seeds in plan["seeds"].items():
        if split == "holdout" and not include_holdout:
            report["splits"][split] = {"planned_per_arm": len(seeds), "status": "outcomes masked; reserved holdout"}
            continue
        arms = {}
        for arm in plan["arms"]:
            planned = [t for t in plan["schedule"] if t["split"] == split and t["arm"] == arm]
            actual = [primary[t["id"]] for t in planned if t["id"] in primary]
            statuses = {}
            for r in actual:
                status = r["result"]["status"]
                statuses[status] = statuses.get(status, 0) + 1
            wins = statuses.get("victory", 0)
            natural = statuses.get("victory", 0) + statuses.get("defeat", 0)
            pending = len(planned) - len(actual)
            armrows = [r for r in rows if r["trial"]["split"] == split and r["trial"]["arm"] == arm]
            observed_natural = [r["result"]["natural_outcome_observed"] for r in actual if r["result"].get("natural_outcome_observed")]
            observed_wins = sum(bool(r["victory"]) for r in observed_natural)
            arms[arm] = {"planned": len(planned), "attempted_primary": len(actual), "pending": pending,
                         "all_attempts": len(armrows), "retries": len(armrows)-len(actual), "statuses": statuses,
                         "operational_wins": wins, "wins_over_full_plan": wins / len(planned),
                         "completion_bounds": [wins / len(planned), (wins+pending) / len(planned)],
                         "operational_wilson95": wilson(wins, len(planned)) if pending == 0 else None,
                         "natural_results": natural, "natural_win_rate": wins/natural if natural else None,
                         "natural_wilson95": wilson(wins, natural),
                         "raw_natural_observed_results": len(observed_natural),
                         "raw_natural_observed_victories": observed_wins,
                         "raw_natural_observed_rate": observed_wins/len(observed_natural) if observed_natural else None,
                         "raw_natural_boundary": "Includes invalid/unattested runs; retained evidence, NOT the verified A10 win-rate endpoint.",
                         "protocol_invalid_natural_results": sum(bool(r["result"].get("natural_outcome_observed"))
                                                                for r in actual if r["result"]["status"] == "protocol_invalid"),
                         "cost": costs_for(armrows)}
        pairs = []
        for seed in seeds:
            a, b = primary.get(f"{split}-{seed}-baseline"), primary.get(f"{split}-{seed}-candidate")
            if a and b:
                pairs.append({"seed": seed, "baseline": a["result"]["status"], "candidate": b["result"]["status"],
                              "delta": int(b["result"]["status"] == "victory")-int(a["result"]["status"] == "victory")})
        report["splits"][split] = {"arms": arms, "paired_completed": len(pairs), "paired": pairs,
                                  "paired_win_delta": statistics.mean(p["delta"] for p in pairs) if pairs else None,
                                  "paired_hoeffding95": ([max(-1., statistics.mean(p["delta"] for p in pairs)-math.sqrt(2*math.log(40)/len(pairs))),
                                                         min(1., statistics.mean(p["delta"] for p in pairs)+math.sqrt(2*math.log(40)/len(pairs)))] if pairs else None),
                                  "paired_boundary": "Operational failures included; primary first attempts only. Pairing is by seed, not 4 seats as independent samples."}
    return report


def costs_for(rows):
    result = {"attempts": len(rows), "elapsed_seconds": {}, "searches": {}}
    def dist(xs):
        xs = sorted(xs)
        return {"observed": len(xs), "median": statistics.median(xs) if xs else None,
                "p95": xs[max(0, math.ceil(.95*len(xs))-1)] if xs else None}
    result["elapsed_seconds"] = dist([r["elapsed_seconds"] for r in rows if "elapsed_seconds" in r])
    searches = [s for r in rows for s in r["result"].get("search_cost", {}).get("rows", [])]
    for key in ("wall_ms", "compute_ms", "gcPause_ms", "alloc_MB"):
        result["searches"][key] = dist([s[key] for s in searches if s[key] is not None])
    result["by_source"] = {}
    for source in sorted({s.get("source", "historical-unlabelled") for s in searches}):
        chosen = [s for s in searches if s.get("source", "historical-unlabelled") == source]
        outcomes = {}
        for s in chosen:
            outcome = s.get("decision_outcome", "historical-unlabelled")
            outcomes[outcome] = outcomes.get(outcome, 0) + 1
        result["by_source"][source] = {"reports": len(chosen), "decision_outcomes": outcomes,
                                      "metrics": {k: dist([s[k] for s in chosen if s[k] is not None])
                                                  for k in ("wall_ms", "compute_ms", "gcPause_ms", "alloc_MB")},
                                      "by_decision_outcome": {
                                          outcome: {"reports": count,
                                                    "metrics": {k: dist([s[k] for s in chosen if s.get("decision_outcome", "historical-unlabelled") == outcome and s[k] is not None])
                                                                for k in ("wall_ms", "compute_ms", "gcPause_ms", "alloc_MB")}}
                                          for outcome, count in outcomes.items()}}
    result["missing_search_telemetry_attempts"] = sum(not r["result"].get("search_cost", {}).get("rows") for r in rows)
    result["boundary"] = "All attempts/retries included; sources and decided/declined remain distinct. Per-search observations are correlated, not independent full-run samples or proof of native terminal completion. Missing cost is null, never zero. Sealed historical attempt costs are retained as originally parsed; post-hoc reparsing belongs in a separately bound raw-log report."
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="operation", required=True)
    p = sub.add_parser("plan")
    p.add_argument("--baseline-receipt", required=True)
    p.add_argument("--candidate-receipt")
    p.add_argument("--reservation", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--game-root", default=r"B:\SteamLibrary\steamapps\common\Slay the Spire 2")
    p.add_argument("--data-root", default=str(Path(os.environ.get("APPDATA", "")) / "SlayTheSpire2"))
    p.add_argument("--power-shell", help="PowerShell executable on the Windows test host (default: pwsh or powershell.exe)")
    p = sub.add_parser("bind-candidate")
    p.add_argument("manifest"); p.add_argument("--receipt", required=True); p.add_argument("--out", required=True)
    p = sub.add_parser("ingest")
    p.add_argument("manifest"); p.add_argument("--external-dir", required=True); p.add_argument("--results", required=True)
    p.add_argument("--arm", choices=("baseline", "candidate"), default="baseline")
    p.add_argument("--split", choices=("development", "holdout"), default="development")
    for name in ("dry-run", "run", "analyze"):
        p = sub.add_parser(name)
        p.add_argument("manifest"); p.add_argument("--results", required=True)
        if name == "analyze":
            p.add_argument("--include-holdout", action="store_true"); p.add_argument("--out")
        else:
            p.add_argument("--arm", choices=("baseline", "candidate"))
            p.add_argument("--split", choices=("development", "holdout"), default="development")
            p.add_argument("--limit", type=int); p.add_argument("--retry", action="store_true")
            p.add_argument("--allow-holdout", action="store_true")
    args = parser.parse_args(argv)
    if args.operation == "plan":
        result = make_plan(args.baseline_receipt, args.reservation, args.game_root, args.data_root, args.candidate_receipt, args.power_shell)
        write_json(args.out, result, exclusive=True)
    elif args.operation == "bind-candidate":
        result = bind_candidate(args.manifest, args.receipt, args.out)
    else:
        plan = load_plan(args.manifest)
        if args.operation == "ingest":
            result = ingest(plan, args.external_dir, args.results, args.arm, args.split)
        elif args.operation == "dry-run":
            rows = attempts_for(args.results, plan)
            result = preview(plan, choose_trials(plan, rows, args.arm, args.split, args.limit, args.retry), args.results)
        elif args.operation == "run":
            result = execute(plan, args.results, args.arm, args.split, args.limit, args.retry, args.allow_holdout)
        else:
            result = summarize(plan, attempts_for(args.results, plan), args.include_holdout)
            if args.out:
                write_json(args.out, result)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, RuntimeError, OSError) as error:
        print(f"A10 benchmark: {error}", file=sys.stderr)
        sys.exit(2)
