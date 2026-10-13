"""Focused Python-only tests; no game, Steam, dotnet or real mod deployment.

R2: work/architecture-refactor-20261009/benchmark/r2/proof.json.
23 historical physical Python faults: baseline 0, mutation runtime 1, exact byte restoration 0.
Faults1-20 retain the first source epoch; UTF16 fault21 has its own epoch;
recording faults22-23 retain their pre-archive source epoch. All bytes are retained.
Fault24: benchmark/r2/sentinel-archive-v2/proof.json, focused baseline0,
physical archive-ownership mutation runtime1, exact-restored focused/all0.
Actual raw excerpt (one AssertionError, including its following diff/message):
  AssertionError: 'not_owned' != 'archived'
  - not_owned
  + archived
   : FLAG_ARCHIVE: owned flag must move to attempt evidence
The physical R2 test copy precedes this comment-only annotation.
Fault25: benchmark/r2/tournament-cost-v4/proof.json, focused baseline0,
physical main-cost-parser omission runtime1, exact-restored focused/all0:
  AssertionError: None != 2 : TOURNAMENT_COST: actual main-planner telemetry must be observed
The physical fault25 fixture also precedes its comment-only annotation.
Faults26-28: benchmark/r2/normal-close-workshop-v5-utf8/proof.json,
each focused physical baseline0 / named runtime1 / exact-source restored0.
Actual fault26 raw line is long (the entire derived script); exact prefix:
  AssertionError: 'Stop-Process' unexpectedly found in
Exact named tail, with the middle script text preserved in the raw red.log:
  : NORMAL_CLOSE: derived/runtime close must never force clients
Actual fault27/28 raw lines:
  AssertionError: False is not true : NORMAL_CLOSE: refused close must retain active owned flag
  AssertionError: WORKSHOP_BINDING: launch is forbidden
The old CP936-output receipt decoding attempt remains separate and uncounted;
the final three use explicit -X utf8 and retain all physical source/log bytes.
The physical three-fault fixture precedes this comment-only annotation.
These are benchmark-contract controls, not 25 Bot/game bugs. Historical excerpts:
  frozen-physical-sha: AssertionError: ValueError not raised : FROZEN_PAYLOAD: installed bytes must match the frozen ZIP
  zip-extraction-source: AssertionError: {'CoopBots.dll': 'BA2EAE09A9B0A102DA0AF74D6543D7937D019C3DC[294 chars]9E8'} != {'CoopBots.dll': 'A8055045744D174CE7AA3BC1ED5AEC9DD0CCE0D4A[294 chars]DBE'}
  holdout-overlap: AssertionError: ValueError not raised : HOLDOUT_DISJOINT: overlap must be rejected
  manifest-seal: AssertionError: ValueError not raised
  route-frozen: AssertionError: ValueError not raised : ROUTE_FIXED: candidate must not alter the route
  native-config: AssertionError: [] is not true : NATIVE_CONFIG: mismatch cannot become a valid A10 trial
  stall-disposition: AssertionError: 'unknown_exit' != 'stall'
  full-plan-denominator: AssertionError: 1.0 != 0.3333333333333333 within 7 places (0.6666666666666667 difference)
  retry-primary: AssertionError: Tuples differ: (1, 1, 1) != (0, 1, 1)
  seed-pairing: AssertionError: 0 != 1
  holdout-release: AssertionError: "holdout" does not match "selected schedule contains unbound candidate; select --arm baseline" : HOLDOUT_RELEASE: unbound candidate cannot consume reserved seeds
  resume-attempts: AssertionError: Lists differ: ['DEV001', 'DEV002', 'DEV003'] != ['DEV002', 'DEV003']
  exclusive-lock: AssertionError: GAME_LOCK: second lock should never enter
  pre-steam-observation: AssertionError: 6783 not less than 6720 : BEFORE_STEAM: physical identity must be checked before native launch
  owned-sentinel-cleanup: AssertionError: True is not false : SENTINEL_CLEANUP: owned flag must be removed
  missing-telemetry: AssertionError: Lists differ: [] != [1]
  physical-replay-archive: AssertionError: False is not true : ARCHIVE_REPLAY: existing physical replay must be archived
  ingest-no-invented-runtime: AssertionError: 'victory' != 'protocol_invalid'
  existing-game-ownership: AssertionError: RuntimeError not raised
  factorial-evidence-boundary: AssertionError: 'NOT executable' not found in 'executable; full-run evidence'
  utf16-console-ingest: AssertionError: 'unknown_exit' != 'stall'
  recordings-not-input: AssertionError: {'CoopBots/live-recordings/batch/combat-00[195 chars]C50'} != {}
  recordings-real-archive: AssertionError: False is not true : RECORDINGS_ARCHIVE: actual mod-local native replay must be archived
The fake executable/log artifacts validate the benchmark plumbing, not Bot strength.
"""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

spec = importlib.util.spec_from_file_location("a10_benchmark", Path(__file__).resolve().parents[1] / "scripts/a10-benchmark.py")
a10 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(a10)


class BenchmarkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.game = self.root / "steamapps/common/game"
        self.data = self.root / "data"
        self.results = self.root / "results"
        self.game.mkdir(parents=True)
        (self.game / "SlayTheSpire2.exe").write_bytes(b"fake-executable-not-runnable")
        (self.game / "sts2.dll").write_bytes(b"fake-game-assembly")
        (self.data / "logs").mkdir(parents=True)
        (self.data / "logs/godot.log").write_text("old log", encoding="utf-8")
        (self.game / "mods/CoopBots").mkdir(parents=True)
        self.receipt = self.make_receipt("baseline")
        empty = self.root / "history-scan.log"
        empty.write_bytes(b"")
        self.reservation = self.root / "reservation.json"
        a10.write_json(self.reservation, {
            "development": ["DEV001", "DEV002", "DEV003"], "holdout": ["HOLD001", "HOLD002"],
            "status": "test-only synthetic seeds; no real history claim",
            "scans": [{"exit": 1, "match_found": False, "log": str(empty), "log_sha256": a10.file_hash(empty)}]})
        self.plan = a10.make_plan(self.receipt, self.reservation, self.game, self.data)
        self.trial = self.plan["schedule"][0]

    def make_receipt(self, label, route_change=False):
        base = self.root / label
        base.mkdir()
        installed = base / "installed"
        installed.mkdir()
        payload = {n: (label + n).encode() for n in a10.FILES}
        payload["mod_manifest.json"] = json.dumps({"version": "0.39.1"}).encode()
        archive = base / "frozen.zip"
        with zipfile.ZipFile(archive, "w") as z:
            for n, data in payload.items():
                z.writestr("CoopBots/" + n, data)
                (installed / n).write_bytes(data)
        artifacts = [{"snapshot": str(archive), "sha256": a10.file_hash(archive), "path": "UNUSED-current-output.zip"}]
        artifacts += [{"snapshot": str(installed / n), "sha256": a10.file_hash(installed / n), "path": "UNUSED-current.dll"} for n in a10.FILES]
        sources = []
        for n in a10.ROUTES:
            p = base / n
            p.write_bytes((n + ("changed" if route_change else "fixed")).encode())
            sources.append({"snapshot": str(p), "sha256": a10.file_hash(p), "path": "UNUSED-current.cs"})
        receipt = base / "receipt.json"
        a10.write_json(receipt, {"artifacts": artifacts, "sources": sources,
                                "game": {"assembly": str(self.game / "sts2.dll"), "sts2_sha256": a10.file_hash(self.game / "sts2.dll")},
                                "verification": "test-only-no-CSharp-gate-claim"})
        return receipt

    def raw_log(self, victory=True, seed=None):
        seed = seed or self.trial["seed"]
        return ("CoopBots 0.39.1 loaded.\n"
                f"CoopBots live test: flag seed='{seed}' multiplayer=True ascension=10 party='SILENT,SILENT,IRONCLAD,IRONCLAD'(resolved=4) room='' encounter='' search='' tournamentOverride=default perf='high'(active=High).\n"
                "CoopBots live test: multiplayer lobby ready — seats=4 ascension=10 (wanted 10, profile max=10, lobby cap=10, difficulty=Pro); every seat is auto-piloted.\n"
                f"CoopBots live test: multiplayer run started — seed={seed} ascension=10 seats=4 decks=[10,10,10,10]\n"
                "[INFO] Preloading 'characters=SILENT,SILENT,IRONCLAD,IRONCLAD' assets... count=40\n"
                "search-complete: action=Strike, wall=3200ms, compute=3000.5ms, gcPause=120ms (4% of compute), gen0=4, alloc=100MB\n"
                f"CoopBots live test: run outcome — victory={str(victory)} act=3 floor=60\n")

    def row(self, arm="baseline", seed="DEV001", status="defeat", started="2026-10-09T00:00:00+00:00"):
        trial = next(t for t in self.plan["schedule"] if t["arm"] == arm and t["seed"] == seed)
        return {"experiment_id": self.plan["experiment_id"], "manifest_sha256": self.plan["manifest_sha256"],
                "trial": trial, "arm_identity": self.plan["arms"][arm], "started_utc": started,
                "result": {"status": status, "natural_outcome_observed": {"victory": status == "victory"} if status in ("victory", "defeat") else None}}

    def test_frozen_zip_and_installed_copies_must_agree(self):
        r = a10.read_json(self.receipt)
        (Path(r["artifacts"][1]["snapshot"])).write_bytes(b"tampered core")
        with self.assertRaisesRegex(ValueError, "SHA256 mismatch", msg="FROZEN_PAYLOAD: installed bytes must match the frozen ZIP"):
            a10.freeze_arm(self.receipt)

    def test_package_extraction_uses_only_receipt_snapshot(self):
        destination = self.root / "extracted"
        a10.extract_package(self.plan["arms"]["baseline"], destination)
        self.assertEqual({n: a10.file_hash(destination / "CoopBots" / n) for n in a10.FILES},
                         self.plan["arms"]["baseline"]["payload"], "FROZEN_ZIP_SOURCE: extracted bytes must match frozen payload")

    def test_disjoint_holdout_and_scan_errors_rejected(self):
        with self.assertRaisesRegex(ValueError, "disjoint", msg="HOLDOUT_DISJOINT: overlap must be rejected"):
            a10.validate_seeds(["A001"], ["A001"])
        reservation = a10.read_json(self.reservation)
        reservation["scans"][0]["exit"] = 2
        a10.write_json(self.reservation, reservation)
        with self.assertRaisesRegex(ValueError, "history scan"):
            a10.make_plan(self.receipt, self.reservation, self.game, self.data)

    def test_manifest_tampering_rejected(self):
        path = self.root / "plan.json"
        a10.write_json(path, self.plan)
        self.assertEqual(a10.load_plan(path), self.plan)
        modified = dict(self.plan)
        modified["tool_sha256"] = "wrong"
        a10.write_json(path, modified)
        with self.assertRaisesRegex(ValueError, "hash invalid"):
            a10.load_plan(path)

    def test_candidate_preserves_game_and_route_hashes(self):
        candidate = a10.freeze_arm(self.make_receipt("candidate"))
        a10.attach_arm(self.plan, candidate)
        bad = a10.freeze_arm(self.make_receipt("route-change", route_change=True))
        with self.assertRaisesRegex(ValueError, "route hashes", msg="ROUTE_FIXED: candidate must not alter the route"):
            a10.attach_arm(self.plan, bad)
        candidate["game"] = {"assembly": "other", "sts2_sha256": "wrong"}
        with self.assertRaisesRegex(ValueError, "identical game"):
            a10.attach_arm(self.plan, candidate)

    def test_native_config_must_match_every_control(self):
        raw = self.raw_log()
        self.assertEqual(a10.config_issues(raw, self.plan, self.trial), [])
        for old, new in (("seed=DEV001", "seed=OTHER"), ("ascension=10", "ascension=9"),
                         ("difficulty=Pro", "difficulty=Flash"), ("active=High", "active=Low"),
                         ("characters=SILENT,SILENT,IRONCLAD,IRONCLAD", "characters=IRONCLAD,SILENT,SILENT,IRONCLAD"),
                         ("0.39.1 loaded", "0.38.0 loaded")):
            with self.subTest(new=new):
                self.assertTrue(a10.config_issues(raw.replace(old, new), self.plan, self.trial), "NATIVE_CONFIG: mismatch cannot become a valid A10 trial")
        self.assertTrue(a10.config_issues(raw + raw, self.plan, self.trial))

    def test_stall_external_error_and_unknown_are_retained(self):
        for raw, console, code, wanted in (("", "判定卡死", 0, "stall"),
                                           ("NGame.Quit called", "", 0, "external_stop"),
                                           ("", "", 1, "launcher_error"), ("FATAL", "", 0, "game_error"),
                                           ("", "", 0, "unknown_exit"), ("How did the game end??", "", 0, "terminal_unclassified")):
            self.assertEqual(a10.classify(raw, console, code)["status"], wanted, "DISPOSITION: abnormal attempts must be retained")
        result = a10.classify(self.raw_log(), "", 1)
        self.assertEqual(result["status"], "launcher_error")
        self.assertTrue(result["natural_outcome_observed"]["victory"])
        invalid = a10.classify(self.raw_log(), "", 0, ["hash mismatch"])
        self.assertEqual(invalid["status"], "protocol_invalid")
        self.assertTrue(invalid["natural_outcome_observed"]["victory"])

    def test_all_planned_and_natural_denominators_include_censoring(self):
        rows = [self.row(status="victory"), self.row(seed="DEV002", status="stall"), self.row(seed="DEV003", status="external_stop")]
        r = a10.summarize(self.plan, rows)["splits"]["development"]["arms"]["baseline"]
        self.assertEqual((r["planned"], r["attempted_primary"], r["pending"], r["natural_results"]), (3, 3, 0, 1),
                         "PLAN_DENOMINATOR: stalls and external stops remain in the plan")
        self.assertAlmostEqual(r["wins_over_full_plan"], 1/3)
        self.assertEqual(r["natural_win_rate"], 1)
        self.assertIsNotNone(r["operational_wilson95"])
        self.assertGreater(r["operational_wilson95"][1]-r["operational_wilson95"][0], .7)

    def test_retry_cannot_replace_failed_primary(self):
        rows = [self.row(status="stall"), self.row(status="victory", started="2026-10-09T01:00:00+00:00")]
        r = a10.summarize(self.plan, rows)["splits"]["development"]["arms"]["baseline"]
        self.assertEqual((r["operational_wins"], r["attempted_primary"], r["retries"]), (0, 1, 1),
                         "PRIMARY_ATTEMPT: retry victory must not replace first stalled attempt")
        self.assertIsNone(r["operational_wilson95"])
        self.assertEqual(r["completion_bounds"], [0, 2/3])

    def test_pairing_is_seed_level_and_pending_not_zero_filled(self):
        a10.attach_arm(self.plan, a10.freeze_arm(self.make_receipt("candidate")))
        rows = [self.row(status="defeat"), self.row(arm="candidate", status="victory"), self.row(seed="DEV002", status="victory")]
        r = a10.summarize(self.plan, rows)["splits"]["development"]
        self.assertEqual(r["paired_completed"], 1)
        self.assertEqual(r["paired_win_delta"], 1)
        self.assertEqual(r["paired_hoeffding95"], [-1, 1])
        self.assertEqual(r["arms"]["candidate"]["pending"], 2)

    def test_holdout_is_masked_and_release_requires_frozen_candidate(self):
        report = a10.summarize(self.plan, [])
        self.assertNotIn("arms", report["splits"]["holdout"])
        with self.assertRaisesRegex(ValueError, "holdout", msg="HOLDOUT_RELEASE: unbound candidate cannot consume reserved seeds"):
            a10.execute(self.plan, self.results, split="holdout", allow_holdout=True)

    def test_resume_and_dry_run_have_no_execution_side_effects(self):
        row = self.row(status="stall")
        pending = a10.choose_trials(self.plan, [row], arm="baseline")
        self.assertEqual([t["seed"] for t in pending], ["DEV002", "DEV003"],
                         "RESUME: already attempted seeds must not be silently repeated")
        self.assertEqual(len(a10.choose_trials(self.plan, [row], arm="baseline", retry=True)), 3)
        with mock.patch.object(a10.subprocess, "Popen", side_effect=AssertionError("must not launch")), \
                mock.patch.object(a10, "game_lock", side_effect=AssertionError("must not lock")):
            preview = a10.preview(self.plan, self.plan["schedule"], self.results)
        self.assertFalse(self.results.exists())
        self.assertTrue(preview["dry_run"])
        self.assertTrue(any(not t["executable"] for t in preview["schedule"]))
        cmd = preview["schedule"][0]["command"]
        self.assertIn("-PackageDir", cmd)
        self.assertIn("10", cmd)
        self.assertNotIn("outputs", " ".join(cmd))

    def test_concurrent_game_lock_does_not_steal(self):
        with a10.game_lock(self.game):
            with self.assertRaisesRegex(RuntimeError, "lock exists", msg="GAME_LOCK: second runner must not take ownership"):
                with a10.game_lock(self.game):
                    self.fail("GAME_LOCK: second lock should never enter")
        self.assertFalse((self.game / "mods/.coopbots-a10-benchmark.lock").exists())

    def test_existing_game_and_sentinel_are_never_killed(self):
        with mock.patch.object(a10, "game_running", return_value=True), \
                mock.patch.object(a10, "stop_owned_game", side_effect=AssertionError("must not kill")):
            with self.assertRaisesRegex(RuntimeError, "already running"):
                a10.execute(self.plan, self.results, arm="baseline", limit=1)
        flag = self.game / "mods/CoopBots/live-test.flag"
        flag.write_text("someone else's seed")
        with mock.patch.object(a10, "game_running", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "pre-existing sentinel"):
                a10.execute(self.plan, self.results, arm="baseline", limit=1)
        self.assertTrue(flag.exists())

    def test_additive_launcher_hook_runs_after_copy_before_steam(self):
        # R2 2026-10-13, private physical a10 source copy: removing the
        # derivative writer's newline="" gave exit 1 for both LF/CRLF:
        # AssertionError: 0 != 1 : LAUNCHER_REPLACEMENT_BYTES: injected replacements must survive physical newline preservation
        # Exact source restoration returned focused exit 0; full evidence is
        # local work/github-open-source-20261013/pipeline/ci-crlf/proof.json.
        source_path = Path(self.plan["launcher"]["path"])
        original_source = source_path.read_bytes()
        normalized_source = original_source.decode("utf-8-sig").replace("\r\n", "\n")
        for label, newline in (("lf", "\n"), ("crlf", "\r\n")):
            with self.subTest(newline=label):
                launcher = self.root / ("source-launcher-" + label + ".ps1")
                launcher.write_bytes(normalized_source.replace("\n", newline).encode("utf-8-sig"))
                plan = {**self.plan, "launcher": {"path": str(launcher), "sha256": a10.file_hash(launcher)}}
                directory = self.root / ("derivation-" + label)
                directory.mkdir()
                derived = a10.instrument_launcher(plan, plan["arms"]["baseline"], directory)
                text = derived.read_bytes().decode("utf-8-sig")
                copied = text.index("已部署 $($files.Count) 个文件")
                observed = text.index("A10 benchmark additive pre-Steam physical identity check")
                steam = text.index('Start-Process "steam://rungameid/2868840"')
                self.assertLess(copied, observed)
                self.assertLess(observed, steam, "BEFORE_STEAM: physical identity must be checked before native launch")
                d = a10.read_json(directory / "launcher-derivation.json")
                self.assertEqual(d["original_sha256"], plan["launcher"]["sha256"])
                without = text
                for item in reversed(d["replacements"]):
                    self.assertEqual(without.count(item["after"]), 1,
                                     "LAUNCHER_REPLACEMENT_BYTES: injected replacements must survive physical newline preservation")
                    without = without.replace(item["after"], item["before"], 1)
                self.assertEqual(without, launcher.read_bytes().decode("utf-8-sig"),
                                 "LAUNCHER_NEWLINES: reversible replacements must preserve physical LF and CRLF source")
                self.assertEqual(a10.file_hash(launcher), plan["launcher"]["sha256"])
                for name in a10.FILES + a10.ROUTES:
                    self.assertIn(name, "\n".join(d["injected_lines"]))
        self.assertEqual(source_path.read_bytes(), original_source)

    def test_owned_pid_normal_close_without_force_fallback(self):
        directory = self.root / "normal-close"
        directory.mkdir()
        derived = a10.instrument_launcher(self.plan, self.plan["arms"]["baseline"], directory)
        text = derived.read_text(encoding="utf-8-sig")
        script = a10.normal_close_script(directory / "owned-game-process.json", directory / "close.json")
        for raw in (text, script):
            self.assertNotIn("Stop-Process", raw, "NORMAL_CLOSE: derived/runtime close must never force clients")
            self.assertIn("CloseMainWindow()", raw, "NORMAL_CLOSE: recorded PID must use normal window close")
            self.assertIn("WaitForExit(20000)", raw)
            self.assertIn("Get-Process -Id ([int]$a10Identity.pid)", raw)
            self.assertIn("StartTime.ToUniversalTime().Ticks.ToString() -ne", raw)
        self.assertIn("$a10NewClients.Count -ne 1", text)
        self.assertIn("normal-stop-request.json", text)
        self.assertEqual(a10.file_hash(self.plan["launcher"]["path"]), self.plan["launcher"]["sha256"])
        with mock.patch.object(a10.subprocess, "run", side_effect=a10.subprocess.CalledProcessError(1, "normal-close")) as runner:
            with self.assertRaises(a10.subprocess.CalledProcessError, msg="NORMAL_CLOSE: rejected close cannot become successful cleanup"):
                a10.stop_owned_game(directory)
        self.assertEqual(runner.call_count, 1, "NORMAL_CLOSE: refusal must not invoke a force fallback")
        self.assertEqual(runner.call_args.kwargs["timeout"], 30)

    def test_changed_workshop_input_and_settings_refuse_start(self):
        workshop = a10.workshop_root(self.game)
        library = workshop / "3737335127/BaseLib/BaseLib.dll"
        library.parent.mkdir(parents=True)
        library.write_bytes(b"frozen-subscription-library")
        (library.parent / "BaseLib.pck").write_bytes(b"packed-input")
        (library.parent / "BaseLib.json").write_text('{"id":"BaseLib"}')
        output = library.parent / "live-recordings/combat.run.json"
        output.parent.mkdir()
        output.write_bytes(b"output-not-input")
        settings = self.data / "steam/profile/settings.save"
        settings.parent.mkdir(parents=True)
        a10.write_json(settings, {"mod_settings": {"mods_enabled": True, "mod_list": ["BaseLib", "RandomForeseer"]}, "volume_master": .8})
        plan = a10.make_plan(self.receipt, self.reservation, self.game, self.data)
        self.assertEqual(len([key for key in plan["other_mods"] if key.startswith("workshop/")]), 3)
        self.assertIn("steam/profile/settings.save", plan["mod_environment"]["settings_sources"])
        output.write_bytes(b"changed-runtime-output")
        a10.verify_environment(plan)
        a10.write_json(settings, {"mod_settings": {"mods_enabled": True, "mod_list": ["BaseLib", "RandomForeseer"]}, "volume_master": .4})
        a10.verify_environment(plan)
        library.write_bytes(b"changed-workshop-native-hooks")
        with mock.patch.object(a10.subprocess, "Popen", side_effect=AssertionError("WORKSHOP_BINDING: launch is forbidden")):
            with self.assertRaisesRegex(ValueError, "inventory changed", msg="WORKSHOP_BINDING: changed subscribed DLL must refuse start"):
                a10.execute(plan, self.results, arm="baseline", limit=1)
        library.write_bytes(b"frozen-subscription-library")
        a10.write_json(settings, {"mod_settings": {"mods_enabled": True, "mod_list": ["RandomForeseer", "BaseLib"]}, "volume_master": .4})
        with self.assertRaisesRegex(ValueError, "configuration sources changed", msg="WORKSHOP_BINDING: changed enable/order source must refuse start"):
            a10.verify_environment(plan)

    def test_refused_normal_close_retains_flag_and_lock(self):
        outer = self
        class FakeProcess:
            def __init__(self, command, stdout, stderr):
                self.code = 0
                body = "\n".join([outer.trial["seed"], "perf=" + outer.plan["config"]["perf"],
                                  "ascension=" + str(outer.plan["config"]["ascension"]),
                                  "party=" + ",".join(outer.plan["config"]["party"])])
                (outer.game / "mods/CoopBots/live-test.flag").write_bytes(body.encode("ascii"))
            def poll(self): return self.code
            def wait(self, timeout=None): return self.code
        with mock.patch.object(a10, "game_running", return_value=False), \
                mock.patch.object(a10, "stop_owned_game", side_effect=RuntimeError("CloseMainWindow refused")), \
                mock.patch.object(a10.subprocess, "Popen", FakeProcess):
            rows = a10.execute(self.plan, self.results, arm="baseline", limit=1)
        self.assertEqual(rows[0]["game_close"]["status"], "failed")
        self.assertTrue((self.game / "mods/CoopBots/live-test.flag").exists(), "NORMAL_CLOSE: refused close must retain active owned flag")
        self.assertTrue((self.game / "mods/.coopbots-a10-benchmark.lock").exists(), "NORMAL_CLOSE: retained active flag must retain the root-wide lock")
        self.assertEqual(rows[0]["result"]["status"], "protocol_invalid")
        self.assertEqual(len(rows), 1)

    def test_full_execution_plumbing_archives_and_cleans_owned_sentinel(self):
        outer = self
        class FakeProcess:
            def __init__(self, command, stdout, stderr):
                self.code = 0
                directory = Path(command[command.index("-File")+1]).parent
                arm = outer.plan["arms"]["baseline"]
                for name in a10.FILES:
                    p = outer.game / "mods/CoopBots" / name
                    source = Path(arm["receipt"]).parent / "installed" / name
                    p.write_bytes(source.read_bytes())
                body = "\n".join([outer.trial["seed"], "perf=" + outer.plan["config"]["perf"],
                                  "ascension=" + str(outer.plan["config"]["ascension"]),
                                  "party=" + ",".join(outer.plan["config"]["party"])])
                (outer.game / "mods/CoopBots/live-test.flag").write_bytes(body.encode("ascii"))
                (outer.data / "logs/godot.log").write_text(outer.raw_log(), encoding="utf-8")
                a10.write_json(directory / "runtime-identity-before-launch.json", {
                    "installed": arm["payload"], "game_sha256": outer.plan["game"]["sts2_sha256"],
                    "routes": {n: r["sha256"] for n, r in arm["routes"].items()},
                    "launcher_sha256": outer.plan["launcher"]["sha256"], "package_sha256": arm["package_sha256"],
                    "observed_utc": a10.utc()})
                stdout.write(b"test fake process, no Steam launched\n")
            def poll(self): return self.code
            def wait(self, timeout=None): return self.code
            def terminate(self): self.code = -15
            def kill(self): self.code = -9
        with mock.patch.object(a10, "game_running", return_value=False), \
                mock.patch.object(a10, "stop_owned_game", return_value={"status": "already_exited", "forced": False}) as stop, mock.patch.object(a10.subprocess, "Popen", FakeProcess):
            rows = a10.execute(self.plan, self.results, arm="baseline", limit=1)
        self.assertEqual(rows[0]["result"]["status"], "victory", "EXECUTION_WIRING: only complete correctly bound native outcome qualifies")
        stop.assert_called_once()
        self.assertFalse((self.game / "mods/CoopBots/live-test.flag").exists(), "SENTINEL_CLEANUP: owned active flag must be disabled")
        saved_flag = Path(rows[0]["sentinel_cleanup"]["archive"])
        self.assertEqual(rows[0]["sentinel_cleanup"]["sha256"], a10.file_hash(saved_flag))
        self.assertEqual(rows[0]["sentinel_cleanup"]["status"], "archived")
        archived = list(self.results.glob("attempts/*/after/logs/godot.log"))
        self.assertEqual(len(archived), 1)
        self.assertEqual(archived[0].read_text(encoding="utf-8"), self.raw_log())
        self.assertEqual(len(a10.attempts_for(self.results, self.plan)), 1)

    def test_owned_sentinel_archive_is_exact_reversible_and_no_overwrite(self):
        flag = self.game / "mods/CoopBots/live-test.flag"
        target = self.root / "attempt"
        target.mkdir()
        raw = "\n".join([self.trial["seed"], "perf=" + self.plan["config"]["perf"],
                         "ascension=" + str(self.plan["config"]["ascension"]),
                         "party=" + ",".join(self.plan["config"]["party"])]).encode("ascii")
        flag.write_bytes(raw)
        with mock.patch.object(a10, "game_running", return_value=False), \
                mock.patch.object(Path, "unlink", side_effect=AssertionError("FLAG_ARCHIVE: deletion is forbidden")):
            receipt = a10.archive_owned_flag(self.plan, self.trial, target, True)
        self.assertEqual(receipt["status"], "archived", "FLAG_ARCHIVE: owned flag must move to attempt evidence")
        self.assertFalse(flag.exists(), "FLAG_ARCHIVE: original active name must be absent")
        saved = target / "owned-live-test.flag.disabled"
        self.assertEqual(saved.read_bytes(), raw, "FLAG_ARCHIVE: archived raw bytes must remain exact")
        self.assertEqual(receipt["sha256"], a10.digest(raw))
        self.assertFalse(receipt["active"])
        flag.write_bytes(raw)
        with mock.patch.object(a10, "game_running", return_value=False):
            with self.assertRaisesRegex(ValueError, "refusing overwrite", msg="FLAG_ARCHIVE: existing evidence cannot be overwritten"):
                a10.archive_owned_flag(self.plan, self.trial, target, True)
        self.assertEqual(saved.read_bytes(), raw)
        self.assertEqual(flag.read_bytes(), raw)
        with mock.patch.object(a10, "game_running", return_value=True):
            self.assertEqual(a10.archive_owned_flag(self.plan, self.trial, target, True)["status"], "game_still_running")
        self.assertEqual(flag.read_bytes(), raw)
        self.assertEqual(a10.archive_owned_flag(self.plan, self.trial, target, False)["status"], "not_owned")
        flag.write_bytes(b"someone else's actual flag")
        with mock.patch.object(a10, "game_running", return_value=False):
            self.assertEqual(a10.archive_owned_flag(self.plan, self.trial, target, True)["status"], "owner_bytes_mismatch")
        self.assertEqual(flag.read_bytes(), b"someone else's actual flag")

    def test_missing_search_metrics_are_null_not_zero(self):
        result = a10.search_cost("search-complete: action=none, wall=12ms\nplan step: wall=5ms compute=5ms alloc=2MB")
        self.assertEqual(result["search_reports"], 1)
        self.assertEqual(result["malformed_lines"], [1])
        self.assertIsNone(result["alloc_MB"]["median"], "COST_MISSING: absent allocation must not become zero")
        good = a10.search_cost(self.raw_log())
        self.assertEqual(good["compute_ms"]["median"], 3000.5)
        self.assertEqual(good["alloc_MB"]["median"], 100)

    def test_real_tournament_cost_source_outcome_and_wait_boundaries(self):
        actual = "[INFO] CoopBots tournament cost: decided, passes=2, wall=7656ms, compute=15515ms, gcPause=4526ms (29.2% of compute), gen0=19035, alloc=1160MB"
        declined = "[INFO] CoopBots tournament cost: declined, passes=1, wall=20ms, compute=15ms, gcPause=1ms (6.7% of compute), gen0=19036, alloc=9MB"
        irrelevant = ("CoopBots decision request: generation=1 wall=9000ms compute=5ms alloc=4MB\n"
                      "CoopBots tournament: decision-ms=9999 (sliced 6ms/frame)\n"
                      "CoopBots shadow: search-complete: action=Foo, wall=3ms, compute=2ms, gcPause=0ms, alloc=1MB\n")
        parsed = a10.search_cost(actual + "\n" + declined + "\n" + irrelevant + self.raw_log())
        self.assertEqual(parsed["by_source"].get("tournament-cost"), 2,
                         "TOURNAMENT_COST: actual main-planner telemetry must be observed")
        self.assertEqual(parsed["by_source"].get("segmented-search-complete"), 1)
        self.assertEqual(parsed["by_outcome"], {"tournament-cost/decided": 1,
                                               "tournament-cost/declined": 1,
                                               "segmented-search-complete/completed": 1})
        self.assertEqual(parsed["search_reports"], 3, "TOURNAMENT_COST: request wait/shadow are not completed search measurements")
        self.assertEqual(len(parsed["excluded_telemetry"]), 1)
        row = parsed["rows"][0]
        self.assertEqual((row["wall_ms"], row["compute_ms"], row["gcPause_ms"], row["alloc_MB"]),
                         (7656, 15515, 4526, 1160), "TOURNAMENT_COST: actual values must not be clamped to wall time")
        self.assertEqual((row["passes"], row["gen0_cumulative"], row["gc_share_percent_logged"]), (2, 19035, 29.2))
        self.assertTrue(row["decision_completed"])
        self.assertFalse(parsed["rows"][1]["decision_completed"])
        self.assertTrue(all(not r["native_terminal_proven"] for r in parsed["rows"]))
        self.assertEqual(parsed["by_source_outcome_metrics"]["tournament-cost/decided"]["metrics"]["compute_ms"]["median"], 15515)
        totals = a10.costs_for([{"result": {"search_cost": parsed}}])
        self.assertEqual(totals["by_source"]["tournament-cost"]["decision_outcomes"], {"decided": 1, "declined": 1})
        self.assertEqual(totals["by_source"]["tournament-cost"]["by_decision_outcome"]["declined"]["metrics"]["compute_ms"]["median"], 15)
        partial = a10.search_cost("CoopBots tournament cost: declined, passes=2, wall=7ms")
        self.assertEqual(partial["search_reports"], 1)
        self.assertEqual(partial["malformed_lines"], [1])
        self.assertIsNone(partial["compute_ms"]["median"], "TOURNAMENT_COST: missing compute remains unobserved")
        self.assertIsNone(partial["gcPause_ms"]["median"])
        self.assertIsNone(partial["alloc_MB"]["median"])
        self.assertEqual(partial["rows"][0]["source"], "tournament-cost")

    def test_archive_missing_replay_is_explicit_and_existing_artifacts_exact(self):
        (self.data / "current_run.save").write_bytes(b"actual save bytes")
        (self.data / "latest.mcr").write_bytes(b"actual replay bytes")
        result = a10.archive_sources(self.data, self.root / "snapshot")
        self.assertTrue(result["availability"]["save"])
        self.assertTrue(result["availability"]["replay"], "ARCHIVE_REPLAY: existing physical replay must be archived")
        for row in result["files"]:
            self.assertEqual(a10.file_hash(row["archive"]), row["sha256"])
        (self.data / "latest.mcr").unlink()
        result = a10.archive_sources(self.data, self.root / "no-replay")
        self.assertFalse(result["availability"]["replay"])

    def test_external_ingest_preserves_unattested_natural_result(self):
        external = self.root / "external"
        external.mkdir()
        package = self.root / "launch-package"
        a10.extract_package(self.plan["arms"]["baseline"], package)
        a10.write_json(external / "request.json", {"seed": "DEV001", "ascension": 10, "perf": "high", "party": ",".join(a10.PARTY),
                       "difficulty": "Pro", "package": str(package), "baselineReceipt": str(self.receipt), "startedUtc": "2026-10-09T00:00:00+00:00"})
        a10.write_json(external / "result.json", {"seed": "DEV001", "launcherExit": 0, "finishedUtc": "2026-10-09T01:00:00+00:00", "classified": False})
        (external / "godot.log").write_text(self.raw_log(), encoding="utf-8")
        (external / "launcher.log").write_text("actual raw console", encoding="utf-8")
        row = a10.ingest(self.plan, external, self.results)
        self.assertEqual(row["result"]["status"], "protocol_invalid", "INGEST_BOUNDARY: package alone must not fabricate runtime game identity")
        self.assertTrue(row["result"]["natural_outcome_observed"]["victory"])
        with self.assertRaisesRegex(ValueError, "already imported"):
            a10.ingest(self.plan, external, self.results)

    def test_factorial_is_contract_not_fake_full_run_evidence(self):
        f = self.plan["factorial_short_combat"]
        self.assertEqual(len(f["cells"]), 4)
        self.assertIn("NOT executable", f["status"])
        self.assertNotIn("outcomes", f)

    def test_utf16_powershell_console_is_classified_on_real_ingest_path(self):
        external = self.root / "external-utf16"
        external.mkdir()
        package = self.root / "utf16-package"
        arm = self.plan["arms"]["baseline"]
        a10.extract_package(arm, package)
        a10.write_json(external / "request.json", {"seed": "DEV001", "ascension": 10, "perf": "high", "party": ",".join(a10.PARTY),
                       "difficulty": "Pro", "package": str(package), "baselineReceipt": str(self.receipt), "startedUtc": "2026-10-09T00:00:00+00:00"})
        a10.write_json(external / "result.json", {"seed": "DEV001", "launcherExit": 0, "finishedUtc": "2026-10-09T01:00:00+00:00"})
        a10.write_json(external / "runtime-identity.json", {"installed": arm["payload"], "game_sha256": self.plan["game"]["sts2_sha256"],
                       "observed_utc": "2026-10-08T23:59:00+00:00"})
        raw = self.raw_log().split("CoopBots live test: run outcome —")[0]
        (external / "godot.log").write_text(raw, encoding="utf-8")
        (external / "launcher.log").write_bytes("5 分钟没有任何局面变化 → 判定卡死，杀掉客户端。".encode("utf-16"))
        row = a10.ingest(self.plan, external, self.results)
        self.assertEqual(row["result"]["status"], "stall", "UTF16_INGEST: PowerShell Tee-Object stall must not be lost")
        self.assertEqual((Path(row["import_source"]) / "launcher.log").read_bytes(),
                         next(self.results.glob("attempts/*/external/launcher.log")).read_bytes())

    def test_runtime_recordings_are_outputs_and_exactly_archived(self):
        recordings = self.game / "mods/CoopBots/live-recordings/batch"
        recordings.mkdir(parents=True)
        replay = recordings / "combat-001.replay"
        replay.write_bytes(b"native replay output")
        (recordings / "combat-001.run.json").write_bytes(b"native frozen fight snapshot")
        self.assertEqual(a10.mod_inventory(self.game), self.plan["other_mods"],
                         "RECORDINGS_OUTPUT: new replays must not mutate the frozen mod input inventory")
        snapshot = a10.archive_sources(self.data, self.root / "with-recordings", recordings.parent)
        self.assertTrue(snapshot["availability"]["replay"], "RECORDINGS_ARCHIVE: actual mod-local native replay must be archived")
        self.assertTrue(snapshot["availability"]["combat_snapshot"])
        archived = self.root / "with-recordings/mod-recordings/batch/combat-001.replay"
        self.assertEqual(archived.read_bytes(), replay.read_bytes())


if __name__ == "__main__":
    unittest.main()
