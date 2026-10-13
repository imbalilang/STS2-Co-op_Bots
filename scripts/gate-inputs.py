#!/usr/bin/env python3
"""Freeze/check the exact inputs of a verification run. No build or test.

verify-project copies this file and writes gate-inputs-config.json next to it.
The archived copy is self-contained and both files are included in the snapshot.
The selected SDK is queried with --version; no restore/build/game is performed.
"""
from pathlib import Path
import hashlib
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET


def sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def collect(config, evidence, sdk_version=None):
    root = Path(config["repo"]).resolve()
    evidence = Path(evidence).resolve()
    game = Path(config["game_data"]).resolve()
    dotnet = Path(config["dotnet"]).resolve()
    ignored = {"bin", "obj", ".nuget", "__pycache__"}
    source = set()
    for folder in ("src", "tests", "scripts", "resources"):
        source.update(p for p in (root / folder).rglob("*")
                      if p.is_file() and not ignored.intersection(p.relative_to(root).parts)
                      and p.suffix.lower() != ".md")
    source.update(p for p in (root / "licenses").rglob("*") if p.is_file())
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
                 "NuGet.Config", "global.json", "version.json", "AGENT.md", "PLAN.md",
                 "README.md", "CHANGELOG.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "docs/WORKSHOP_DESCRIPTION.txt",
                 "src/CoopBots.Kernel/VENDOR_DEVIATIONS.md"):
        if (root / name).exists():
            source.add(root / name)
    source.update((evidence / "final-source-audit.py", evidence / "gate-inputs-config.json"))
    props = {"STS2DataDir": str(game)}
    # These are the exact global properties passed to MSBuild by the verifier.
    # Preserve explicit overrides instead of unconditionally taking defaults.
    for name, key in (("RitsuLibDir", "ritsulib_dir"), ("RitsuLibSharedDir", "ritsulib_shared_dir")):
        if config.get(key):
            props[name] = str(Path(config[key]).resolve())

    def expand(value):
        for _ in range(8):
            updated = re.sub(r"\$\(([A-Za-z0-9_]+)\)", lambda m: props.get(m[1], m[0]), value)
            if updated == value:
                break
            value = updated
        match = re.fullmatch(r"\$\(\[System.IO.Path\]::GetFullPath\('(.+)'\)\)", value)
        if match:
            value = str(Path(match[1]).resolve())
        if "$(" in value:
            raise RuntimeError("Unresolved actual reference: " + value)
        return value

    for group in ET.parse(root / "src/CoopBots.Kernel/KernelDependencies.props").getroot().findall("PropertyGroup"):
        for prop in group:
            if prop.tag in ("RitsuLibDir", "RitsuLibSharedDir") and prop.tag not in props:
                props[prop.tag] = expand(prop.text or "")
    references = {game / "sts2.dll"}
    projects = ("src/CoopBots/CoopBots.csproj", "src/CoopBots.Kernel/CoopBots.Kernel.csproj",
                "tests/PatchSmoke/PatchSmoke.csproj", "tests/TypeLoadCheck/TypeLoadCheck.csproj")
    for name in projects:
        project = root / name
        tree = ET.parse(project).getroot()
        for reference in tree.findall(".//Reference"):
            if reference.get("Include") == "CoopBots":
                continue  # Produced DLL identity is bound separately.
            hint = reference.get("HintPath") or reference.findtext("HintPath")
            if hint:
                path = Path(expand(hint))
                references.add((path if path.is_absolute() else project.parent / path).resolve())
        for item in tree.findall(".//None"):
            name = item.get("Include", "")
            if name.lower().endswith(".dll") and "$(" not in name and (project.parent / name).exists():
                references.add((project.parent / name).resolve())
    if sdk_version is None:
        sdk_version = subprocess.run([str(dotnet), "--version"], cwd=root, check=True,
                                     capture_output=True, text=True, timeout=30).stdout.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+].+)?", sdk_version):
        raise RuntimeError("Invalid selected SDK: " + sdk_version)
    tools = {dotnet}
    for folder in (dotnet.parent / "sdk" / sdk_version, dotnet.parent / "host",
                   dotnet.parent / "shared/Microsoft.NETCore.App", dotnet.parent / "packs"):
        if not folder.is_dir():
            raise RuntimeError("Selected toolchain missing: " + str(folder))
        tools.update(p for p in folder.rglob("*") if p.is_file())
    publicizer = ET.parse(root / "src/CoopBots.Kernel/CoopBots.Kernel.csproj").getroot().find(
        ".//PackageReference[@Include='Krafs.Publicizer']")
    if publicizer is None:
        raise RuntimeError("Actual publicizer reference missing")
    package = root / "src/CoopBots.Kernel/.nuget/packages/krafs.publicizer" / expand(publicizer.get("Version", ""))
    if not package.is_dir():
        raise RuntimeError("Actual publicizer build package missing")
    tools.update(p for p in package.rglob("*") if p.is_file())
    rows = [{"path": p.relative_to(root).as_posix(), "sha256": sha(p)} for p in sorted(source)]
    for role, paths in (("reference", references), ("toolchain", tools)):
        rows.extend({"path": "@" + role + "/" + p.as_posix(), "actual_path": str(p),
                     "sha256": sha(p)} for p in sorted(paths))
    return sorted(rows, key=lambda row: row["path"])


def main():
    evidence = Path(__file__).resolve().parent
    config = json.loads((evidence / "gate-inputs-config.json").read_text(encoding="utf-8-sig"))
    rows = collect(config, evidence)
    target = evidence / "final-inputs.json"
    if "--freeze" in sys.argv:
        if target.exists():
            raise RuntimeError("Refuse to overwrite immutable gate inputs")
        target.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    elif json.loads(target.read_text(encoding="utf-8-sig")) != rows:
        raise RuntimeError("Sources/references/toolchain changed from verified inputs")
    print("PASS:", len(rows), "frozen source/reference/toolchain inputs")


if __name__ == "__main__":
    main()
