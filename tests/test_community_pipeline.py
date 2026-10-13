"""Community pipeline contracts using temporary synthetic files, no game/SDK.

These tests do not certify native gameplay or win rate. R2 physical fault runs
and their raw failures are archived under work/github-open-source-20261013/pipeline.
Each private physical fault produced baseline0 / RED1 / exact-restored0:
  ZIP: AssertionError: ValueError not raised : VERIFIED_ZIP: modified ZIP must never freeze
  Later P0: AssertionError: 'terminal_observed' != 'p0_diagnostic'
    : WHOLE_ASCENT_P0: later rooms must still be inspected
  Ritsu override (actual temp paths retained in raw log):
    AssertionError: '<custom Ritsu DLL>' not found in {'<default DLLs>'}
    : RITSU_OVERRIDE: freeze the supplied runtime, not Workshop defaults
  Seed stream: AssertionError: ValueError not raised : SEED_HISTORY: boundary-spanning matches must prevent reuse
Additional distribution-license controls each produced baseline0 / RED1 / exact-restored0:
  AssertionError: ValueError not raised : LICENSE_DISTRIBUTION: new community packages must include required texts
  AssertionError: False is not true : LICENSE_EXTRACTION: the deployed payload must retain legal notices
  AssertionError: False is not true : LICENSE_GATE_INPUTS: freeze root and third-party license bytes
"""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

REPO = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, REPO / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


community = load("community_pipeline", "community-benchmark.py")
observer = load("full_observer_tests", "full_ascent_observer.py")
gate = load("gate_inputs_tests", "gate-inputs.py")
a10 = community.a10


class CommunityPipelineTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name).resolve()
        self.repo = self.root / "repo"
        self.repo.mkdir()

    def write(self, path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(data, bytes):
            path.write_bytes(data)
        elif isinstance(data, str):
            path.write_text(data, encoding="utf-8")
        else:
            path.write_text(json.dumps(data), encoding="utf-8")
        return path

    def make_package(self):
        run = self.repo / "work/gate"
        payload = {name: ("synthetic-" + name).encode() for name in a10.FILES}
        payload["mod_manifest.json"] = b'{"version":"0.39.1"}'
        license_inputs = []
        for name in a10.LICENSE_FILES:
            payload[name] = ("synthetic-license-" + name).encode()
            path = self.write(self.repo / name, payload[name])
            license_inputs.append({"path": name, "sha256": a10.file_hash(path)})
        package = self.repo / "outputs/candidate.zip"
        package.parent.mkdir()
        with zipfile.ZipFile(package, "w") as archive:
            for name, data in payload.items():
                archive.writestr("CoopBots/" + name, data)
        inputs = list(license_inputs)
        for name in a10.ROUTES:
            path = self.write(self.repo / "src/CoopBots" / name, "synthetic-route")
            inputs.append({"path": path.relative_to(self.repo).as_posix(), "sha256": a10.file_hash(path)})
        game = self.write(self.root / "game/sts2.dll", b"synthetic-game-not-loadable")
        inputs.append({"path": "@reference/" + game.as_posix(), "actual_path": str(game), "sha256": a10.file_hash(game)})
        input_path = self.write(run / "final-inputs.json", inputs)
        phases = []
        logs = {}
        for name in ("build", "typeloadcheck", "patchsmoke-kernel"):
            log = self.write(run / (name + ".log"), "synthetic process evidence for pipeline-only test")
            logs[log.name] = a10.file_hash(log)
            phases.append({"name": name, "status": "passed", "exit_code": 0, "log": log.name,
                           "args": ["-p:EnableKernelTests=true"] if name == "patchsmoke-kernel" else []})
        summary = self.write(run / "summary.json", {"status": "pass", "game_data": str(game.parent), "phases": phases})
        artifacts = []
        dlls = {}
        for name in ("CoopBots.dll", "CoopBots.Kernel.dll"):
            self.write(run / "build" / name, payload[name])
            physical = self.write(run / "test-evidence" / name, payload[name])
            dlls[name] = a10.file_hash(physical)
            artifacts.append({"name": name, "physical_path": str(physical), "physical_sha256": dlls[name]})
        binding = self.write(run / "gate-run-binding.json", {"repo": str(self.repo), "full_gate": str(run),
            "status": "pass", "exit_code": 0, "source_inputs_verified_after_gate": True, "test_evidence_verified": True,
            "summary_sha256": a10.file_hash(summary), "input_snapshot_sha256": a10.file_hash(input_path),
            "test_artifacts": artifacts, "phase_log_sha256": logs})
        proof = self.write(package.with_suffix(".verification.json"), {"source": "verified-full-gate",
            "build_typeload_regression_smoke_rerun": False, "package_sha256": a10.file_hash(package),
            "gate_binding": str(binding), "gate_binding_sha256": a10.file_hash(binding), "dll_sha256": dlls,
            "package_member_sha256": {"CoopBots/" + n: a10.digest(d) for n, d in payload.items()}})
        return package, proof, run

    def test_verified_zip_freezes_physical_payload_and_routes_without_game_copy(self):
        package, _, _ = self.make_package()
        with mock.patch.object(community, "REPO", self.repo):
            result = community.freeze(package, self.root / "frozen")
        frozen = a10.freeze_arm(result["receipt"])
        self.assertEqual(frozen["payload"]["CoopBots.Kernel.dll"], a10.digest(b"synthetic-CoopBots.Kernel.dll"), "FROZEN_DUAL_DLL: kernel must bind physical package bytes")
        self.assertTrue(set(a10.LICENSE_FILES).issubset(frozen["payload"]), "LICENSE_PAYLOAD: freeze must bind all license members")
        extracted = self.root / "extracted"
        a10.extract_package(frozen, extracted)
        self.assertTrue(all((extracted / "CoopBots" / name).is_file() for name in a10.LICENSE_FILES), "LICENSE_EXTRACTION: the deployed payload must retain legal notices")
        self.assertFalse(list((self.root / "frozen").rglob("sts2.dll")), "GAME_BYTES: never copy the game's assembly")
        with self.assertRaisesRegex(ValueError, "fresh directory"):
            community.freeze(package, self.root / "frozen")

    def test_tampered_zip_refused_before_output_creation(self):
        package, _, _ = self.make_package()
        with package.open("ab") as stream:
            stream.write(b"physical tamper")
        with mock.patch.object(community, "REPO", self.repo), self.assertRaisesRegex(ValueError, "verified ZIP.*SHA256", msg="VERIFIED_ZIP: modified ZIP must never freeze"):
            community.freeze(package, self.root / "frozen")
        self.assertFalse((self.root / "frozen").exists())

    def test_filtered_kernel_gate_refused_before_freeze(self):
        package, proof_path, run = self.make_package()
        summary = a10.read_json(run / "summary.json")
        summary["phases"][-1]["args"] += ["--", "--party-setup-pending-only"]
        self.write(run / "summary.json", summary)
        binding_path = run / "gate-run-binding.json"
        binding = a10.read_json(binding_path)
        binding["summary_sha256"] = a10.file_hash(run / "summary.json")
        self.write(binding_path, binding)
        proof = a10.read_json(proof_path)
        proof["gate_binding_sha256"] = a10.file_hash(binding_path)
        self.write(proof_path, proof)
        with mock.patch.object(community, "REPO", self.repo), self.assertRaisesRegex(ValueError, "filtered test"):
            community.freeze(package, self.root / "frozen")

    def test_community_freeze_rejects_license_missing_from_distribution(self):
        package, proof_path, _ = self.make_package()
        member = "CoopBots/licenses/PolyForm-Noncommercial-1.0.0.md"
        with zipfile.ZipFile(package) as archive:
            contents = {n: archive.read(n) for n in archive.namelist() if n != member}
        with zipfile.ZipFile(package, "w") as archive:
            for name, data in contents.items():
                archive.writestr(name, data)
        proof = a10.read_json(proof_path)
        proof["package_sha256"] = a10.file_hash(package)
        del proof["package_member_sha256"][member]
        self.write(proof_path, proof)
        with mock.patch.object(community, "REPO", self.repo), self.assertRaisesRegex(ValueError, "distribution license missing", msg="LICENSE_DISTRIBUTION: new community packages must include required texts"):
            community.freeze(package, self.root / "frozen")
        self.assertFalse((self.root / "frozen").exists())

    def test_community_freeze_rejects_license_omitted_from_gate_inputs(self):
        package, proof_path, run = self.make_package()
        input_path = run / "final-inputs.json"
        self.write(input_path, [r for r in a10.read_json(input_path) if r["path"] != "LICENSE"])
        binding_path = run / "gate-run-binding.json"
        binding = a10.read_json(binding_path)
        binding["input_snapshot_sha256"] = a10.file_hash(input_path)
        self.write(binding_path, binding)
        proof = a10.read_json(proof_path)
        proof["gate_binding_sha256"] = a10.file_hash(binding_path)
        self.write(proof_path, proof)
        with mock.patch.object(community, "REPO", self.repo), self.assertRaisesRegex(ValueError, "distribution license not frozen", msg="LICENSE_FROZEN_INPUT: new community packages must bind license input bytes"):
            community.freeze(package, self.root / "frozen")
        self.assertFalse((self.root / "frozen").exists())

    def test_reservation_is_disjoint_scoped_and_immutable(self):
        history = self.root / "history"
        self.write(history / "old.log", "seed=OLD001")
        result = community.reserve(self.root / "reservation", [history], 3, 4)
        reservation = a10.read_json(result["reservation"])
        a10.validate_seeds(reservation["development"], reservation["holdout"])
        self.assertEqual(result["history_files_scanned"], 1)
        self.assertIn("not a universal", a10.read_json(self.root / "reservation/history-scan.json")["boundary"])
        with self.assertRaisesRegex(ValueError, "fresh directory"):
            community.reserve(self.root / "reservation", [history], 3, 4)
        with self.assertRaisesRegex(ValueError, "outside"):
            community.reserve(history / "reservation", [history], 3, 4)

    def test_reservation_rejects_a_seed_across_stream_chunk_boundary(self):
        history = self.root / "history"
        prefix = "0123456789ABCDEF"
        seed = "CB" + prefix + "D00001"
        self.write(history / "old.run", b"x" * (1024 * 1024 - 7) + seed.encode())
        fixed = mock.Mock(hex=prefix.lower() + "0" * 16)
        with mock.patch.object(community.uuid, "uuid4", return_value=fixed), self.assertRaisesRegex(ValueError, "already appears", msg="SEED_HISTORY: boundary-spanning matches must prevent reuse"):
            community.reserve(self.root / "reservation", [history], 1, 1)
        self.assertFalse((self.root / "reservation").exists())

    def test_explicit_ritsulib_reference_override_is_frozen_and_default_is_not(self):
        evidence = self.repo / "work/gate"
        self.write(evidence / "final-source-audit.py", "synthetic")
        self.write(evidence / "gate-inputs-config.json", "{}")
        self.write(self.repo / "src/CoopBots.Kernel/KernelDependencies.props", (REPO / "src/CoopBots.Kernel/KernelDependencies.props").read_text(encoding="utf-8-sig"))
        self.write(self.repo / "src/CoopBots.Kernel/CoopBots.Kernel.csproj", '<Project><ItemGroup><PackageReference Include="Krafs.Publicizer" Version="2.3.0"/><Reference Include="Ritsu" HintPath="$(RitsuLibDir)/STS2-RitsuLib.dll"/><Reference Include="Shared" HintPath="$(RitsuLibSharedDir)/STS2-RitsuLib.Shared.dll"/></ItemGroup></Project>')
        for path in ("src/CoopBots/CoopBots.csproj", "tests/PatchSmoke/PatchSmoke.csproj", "tests/TypeLoadCheck/TypeLoadCheck.csproj"):
            self.write(self.repo / path, "<Project/>")
        self.write(self.repo / "src/CoopBots.Kernel/.nuget/packages/krafs.publicizer/2.3.0/tool.dll", b"synthetic-publicizer")
        self.write(self.repo / "LICENSE", b"synthetic-MIT")
        self.write(self.repo / "licenses/notice.md", b"synthetic-third-party-notice")
        dotnet = self.write(self.root / "sdk/dotnet.exe", b"synthetic-not-executable")
        for folder in ("sdk/9.0.999", "host", "shared/Microsoft.NETCore.App", "packs"):
            (dotnet.parent / folder).mkdir(parents=True)
        game = self.write(self.root / "steamapps/common/game/data/sts2.dll", b"synthetic-game")
        ritsu = self.write(self.root / "custom-ritsu/compat/0.111.0/STS2-RitsuLib.dll", b"synthetic-ritsu")
        shared = self.write(self.root / "custom-shared/STS2-RitsuLib.Shared.dll", b"synthetic-shared")
        # A valid but different default installation makes a withdrawn override
        # reach the identity assertion, rather than merely fail on missing files.
        default_ritsu = self.write(self.root / "steamapps/workshop/content/2868840/3747602295/compat/0.111.0/STS2-RitsuLib.dll", b"synthetic-default-ritsu")
        self.write(self.root / "steamapps/workshop/content/2868840/3747602295/shared/STS2-RitsuLib.Shared.dll", b"synthetic-default-shared")
        rows = gate.collect({"repo": str(self.repo), "game_data": str(game.parent), "dotnet": str(dotnet),
                             "ritsulib_dir": str(ritsu.parent), "ritsulib_shared_dir": str(shared.parent)}, evidence, sdk_version="9.0.999")
        references = {r["actual_path"] for r in rows if r["path"].startswith("@reference/")}
        self.assertIn(str(ritsu), references, "RITSU_OVERRIDE: freeze the supplied runtime, not Workshop defaults")
        self.assertIn(str(shared), references, "RITSU_SHARED_OVERRIDE: freeze separately supplied shared directory")
        self.assertNotIn(str(default_ritsu), references)
        frozen_sources = {r["path"] for r in rows if not r["path"].startswith("@")}
        self.assertTrue({"LICENSE", "licenses/notice.md"}.issubset(frozen_sources), "LICENSE_GATE_INPUTS: freeze root and third-party license bytes")

    def test_later_p0_after_native_combat_win_prevents_qualified_run_win(self):
        raw = ("[INFO] CHARACTER.SILENT has won against encounter ENCOUNTER.AXEBOTS_NORMAL. That's 1 wins\n"
               "CoopBots kernel: planned choice differs from prediction: hand expected=A actual=B\n"
               "CoopBots live test: run outcome — victory=true act=3 floor=48\n")
        observed = observer.observe(raw.encode())
        self.assertEqual(len(observed["native_combat_wins"]), 1)
        self.assertEqual(observer.stopping_control(observed), "p0_diagnostic", "WHOLE_ASCENT_P0: later rooms must still be inspected")
        self.assertEqual(a10.classify(raw, "", 0)["status"], "p0_diagnostic", "QUALIFIED_WIN: semantic P0 cannot become a win")

    def test_historical_prefix_is_excluded_and_physical_cache_miss_is_not_p0(self):
        before = b"CoopBots kernel: planned choice differs from prediction: OLD\n"
        fresh = b"cacheMissAxis=physical-reference field=hand expected=A actual=B\n"
        observed = observer.observe(before + fresh, before)
        self.assertEqual(observed["p0_diagnostics"], [])
        self.assertIsNone(observer.stopping_control(observed))
        self.assertEqual(observer.fresh_text(before + fresh, before), fresh.decode())

    def test_failed_normal_close_retains_lock_and_checks_executable_before_closing(self):
        self.assertTrue(observer.require_retained_lock({"game_close": {"status": "failed"}}), "NORMAL_CLOSE_LOCK: failed close must retain ownership")
        script = a10.normal_close_script("identity.json", "close.json", "D:/Game/SlayTheSpire2.exe")
        self.assertLess(script.index("PID executable differs"), script.index(".CloseMainWindow()"))
        self.assertNotIn("Stop-Process", script)
        self.assertNotIn(".Kill(", script)


if __name__ == "__main__":
    unittest.main()
