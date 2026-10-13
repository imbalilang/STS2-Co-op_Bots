param(
    [string]$GameData = 'D:\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64',
    [string]$Dotnet = '',
    [string]$Python = 'python',
    [string]$RitsuLibDir = '',
    [string]$RitsuLibSharedDir = '',
    [string]$Version = '',
    # Suffixes the release name, for builds that are not the release. A test build
    # of the working tree must never land on outputs\CoopBots-v<version>.zip: that
    # file is the artifact that shipped, and overwriting it with a dirty tree has
    # already happened once (an unreleased build sat there under a release name).
    [string]$Label = '',
    # Reuse an immutable full-gate binding; no build/type-load/smoke rerun.
    [string]$VerifiedGateBinding = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$Dotnet) {
    $Dotnet = Join-Path $repo 'work\dotnet-win\dotnet.exe'
    if (!(Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
}
if (!(Test-Path -LiteralPath (Join-Path $GameData 'sts2.dll'))) { throw 'GameData must contain sts2.dll.' }
$Python = (Get-Command $Python -ErrorAction Stop).Source
$referenceArgs = @()
if ($RitsuLibDir) { $RitsuLibDir = (Resolve-Path -LiteralPath $RitsuLibDir).Path; $referenceArgs += "-p:RitsuLibDir=$RitsuLibDir" }
if ($RitsuLibSharedDir) { $RitsuLibSharedDir = (Resolve-Path -LiteralPath $RitsuLibSharedDir).Path; $referenceArgs += "-p:RitsuLibSharedDir=$RitsuLibSharedDir" }
$manifest = Get-Content -Raw -Encoding utf8 (Join-Path $repo 'src\CoopBots\mod_manifest.json') | ConvertFrom-Json
if (!$Version) { $Version = $manifest.version }
if ($Version -ne $manifest.version -or $Version -notmatch '^\d+\.\d+\.\d+(-[a-z0-9.]+)?$') { throw 'Version must match the manifest.' }
$build = Join-Path $repo "work\build-$Version"
$stem = if ($Label) { "CoopBots-v$Version-$Label" } else { "CoopBots-v$Version" }
$release = Join-Path $repo "outputs\$stem"
$project = Join-Path $repo 'src\CoopBots\CoopBots.csproj'
$distributionLicenses = @([pscustomobject]@{ entry='CoopBots/LICENSE'; source='LICENSE' })
$licenseRoot = Join-Path $repo 'licenses'
if (!(Test-Path -LiteralPath (Join-Path $repo 'LICENSE')) -or !(Test-Path -LiteralPath $licenseRoot)) { throw 'Distribution requires LICENSE and licenses/.' }
$licenseRoot = (Resolve-Path -LiteralPath $licenseRoot).Path
foreach ($file in (Get-ChildItem -LiteralPath $licenseRoot -Recurse -File | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($licenseRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
    $distributionLicenses += [pscustomobject]@{ entry="CoopBots/licenses/$relative"; source="licenses/$relative" }
}
if ($distributionLicenses.Count -lt 2) { throw 'Distribution licenses/ must not be empty.' }
function Copy-DistributionLicenses {
    param([string]$Destination)
    foreach ($item in $distributionLicenses) {
        $target = Join-Path $Destination $item.entry
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo $item.source) -Destination $target
    }
    # The package README sits at the ZIP root and links to this same MIT text.
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $Destination 'LICENSE')
}
if ($VerifiedGateBinding) {
    if (!$Label) { throw 'Verified delivery requires a fresh nonempty -Label.' }
    if ($Label -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Label must be a single safe package suffix.' }
    if (Test-Path -LiteralPath $release) { throw 'Refuse to overwrite an existing labelled package directory.' }
    if (Test-Path -LiteralPath "$release.zip") { throw 'Refuse to overwrite an existing labelled package ZIP.' }
    $bindingPath = (Resolve-Path -LiteralPath $VerifiedGateBinding).Path
    $gateEvidence = Split-Path $bindingPath -Parent
    $binding = Get-Content -LiteralPath $bindingPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($binding.exit_code -ne 0 -or $binding.status -ne 'pass' -or $binding.test_evidence_verified -ne $true -or $binding.source_inputs_verified_after_gate -ne $true) { throw 'Verified gate binding did not pass.' }
    if ([IO.Path]::GetFullPath($binding.repo) -ne [IO.Path]::GetFullPath($repo)) { throw 'Verified gate belongs to another repository.' }
    $summaryPath = Join-Path $binding.full_gate 'summary.json'
    if ((Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash -ne $binding.summary_sha256) { throw 'Verified summary changed.' }
    $summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($summary.status -ne 'pass' -or [IO.Path]::GetFullPath($summary.game_data) -ne [IO.Path]::GetFullPath($GameData)) { throw 'Verified summary/game identity differs.' }
    foreach ($name in @('build', 'typeloadcheck', 'patchsmoke-kernel')) {
        $phase = @($summary.phases | Where-Object { $_.name -eq $name })
        if ($phase.Count -ne 1 -or $phase[0].status -ne 'passed' -or $phase[0].exit_code -ne 0) { throw "Missing successful physical phase: $name" }
    }
    $fullPhase = $summary.phases | Where-Object { $_.name -eq 'patchsmoke-kernel' } | Select-Object -First 1
    if ($fullPhase.args -notcontains '-p:EnableKernelTests=true' -or $fullPhase.args -contains '--') { throw 'Verified suite must be unfiltered full kernel.' }
    if (!(Test-Path -LiteralPath (Join-Path $binding.full_gate $fullPhase.log))) { throw 'Verified full-suite raw log is missing.' }
    if ($binding.protocol -eq 'once-full-kernel-v1' -and !$binding.phase_log_sha256) { throw 'Verified phase log hashes are missing.' }
    if ($binding.phase_log_sha256) {
        foreach ($phase in $summary.phases) {
            if ($phase.status -eq 'passed' -and (!$binding.phase_log_sha256.($phase.log) -or (Get-FileHash -LiteralPath (Join-Path $binding.full_gate $phase.log) -Algorithm SHA256).Hash -ne $binding.phase_log_sha256.($phase.log))) { throw "Verified phase raw log changed: $($phase.name)" }
        }
    }
    $inputPath = Join-Path $gateEvidence 'final-inputs.json'
    if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $binding.input_snapshot_sha256) { throw 'Verified frozen input snapshot changed.' }
    $inputAudit = Join-Path $gateEvidence 'final-source-audit.py'
    $frozenInputs = Get-Content -LiteralPath $inputPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $repoPrefix = [IO.Path]::GetFullPath($repo).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $auditFull = [IO.Path]::GetFullPath($inputAudit)
    if (!$auditFull.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Verified input audit must be inside this repository.' }
    $auditRelative = $auditFull.Substring($repoPrefix.Length).Replace('\', '/')
    $auditInput = @($frozenInputs | Where-Object { $_.path -eq $auditRelative })
    if ($auditInput.Count -ne 1 -or $auditInput[0].sha256 -ne (Get-FileHash -LiteralPath $inputAudit -Algorithm SHA256).Hash) { throw 'Verified input-audit helper changed or is not frozen.' }
    foreach ($name in (@('src/CoopBots/mod_manifest.json', 'THIRD_PARTY_NOTICES.md', 'README.md', 'CHANGELOG.md') + @($distributionLicenses | ForEach-Object { $_.source }))) {
        $frozen = @($frozenInputs | Where-Object { $_.path -eq $name })
        if ($frozen.Count -ne 1 -or $frozen[0].sha256 -ne (Get-FileHash -LiteralPath (Join-Path $repo $name) -Algorithm SHA256).Hash) { throw "Verified package source changed or missing: $name" }
    }
    & $Python $inputAudit
    if ($LASTEXITCODE -ne 0) { throw 'Current sources/references/toolchain differ from the verified frozen inputs.' }
    $build = Join-Path $binding.full_gate 'build'
    foreach ($name in @('CoopBots.dll', 'CoopBots.pdb', 'CoopBots.Kernel.dll', 'CoopBots.Kernel.pdb', 'PatchSmoke.dll', 'PatchSmoke.pdb')) {
        $artifact = @($binding.test_artifacts | Where-Object { $_.name -eq $name })
        if ($artifact.Count -ne 1 -or (Get-FileHash -LiteralPath $artifact[0].physical_path -Algorithm SHA256).Hash -ne $artifact[0].physical_sha256) { throw "Verified test-evidence artifact changed or missing: $name" }
        if ($name -eq 'PatchSmoke.dll' -and $artifact[0].physical_sha256 -ne $binding.patchsmoke_sha256) { throw 'Verified test assembly identity differs.' }
        if ($name -notlike 'PatchSmoke.*' -and (Get-FileHash -LiteralPath (Join-Path $build $name) -Algorithm SHA256).Hash -ne $artifact[0].physical_sha256) { throw "Verified build/test-evidence artifact differs: $name" }
    }
    $verifiedDlls = [ordered]@{}
    foreach ($name in @('CoopBots.dll', 'CoopBots.Kernel.dll')) {
        $digest = (Get-FileHash -LiteralPath (Join-Path $build $name) -Algorithm SHA256).Hash
        $runtime = @($binding.test_artifacts | Where-Object { $_.name -eq $name })
        if ($runtime.Count -ne 1 -or $digest -ne $summary.dll_sha256.$name -or $digest -ne $runtime[0].physical_sha256 -or $digest -ne (Get-FileHash -LiteralPath $runtime[0].physical_path -Algorithm SHA256).Hash) {
            throw "Verified build/runtime physical DLL differs: $name"
        }
        $verifiedDlls[$name] = $digest
    }
    $boundInputHash = $binding.input_snapshot_sha256
    $boundSummaryHash = $binding.summary_sha256
    $boundBindingHash = (Get-FileHash -LiteralPath $bindingPath -Algorithm SHA256).Hash
} else {
& $Dotnet build $project -c Release "-p:STS2DataDir=$GameData" "-p:OutputPath=$build\" -p:NuGetAudit=false @referenceArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# The game enumerates our types before the initializer can load CoopBots.Kernel.
# Guard against a type that needs the kernel at load time (see tests/TypeLoadCheck).
$typeLoadProject = Join-Path $repo 'tests\TypeLoadCheck\TypeLoadCheck.csproj'
$typeLoadOutput = Join-Path $repo 'tests\TypeLoadCheck\bin\Release\net9.0'
& $Dotnet build $typeLoadProject -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Type-load guard failed to build.' }
Push-Location $typeLoadOutput
try {
    & $Dotnet exec (Join-Path $typeLoadOutput 'TypeLoadCheck.dll') $build $GameData
    if ($LASTEXITCODE -ne 0) { throw 'Type-load guard failed: the mod cannot be loaded without CoopBots.Kernel.' }
} finally { Pop-Location }
# Legacy standalone package smoke; this subset is not a full regression receipt.
# Normal verified delivery uses -VerifiedGateBinding and skips this entire branch.
#
# Build once and EXEC the produced assembly instead of `dotnet run`, which does an
# implicit restore+build on every invocation; the second call re-walked a graph
# that was already up to date.
$smokeProject = Join-Path $repo 'tests\PatchSmoke\PatchSmoke.csproj'
$smokeOutput = Join-Path $repo 'tests\PatchSmoke\bin\Release\net9.0'
& $Dotnet build $smokeProject -c Release "-p:STS2DataDir=$GameData" "-p:CoopBotsDll=$build\CoopBots.dll" -p:EnableKernelTests=true -p:NuGetAudit=false @referenceArgs
if ($LASTEXITCODE -ne 0) { throw 'Test project failed to build; package not produced.' }
Push-Location $smokeOutput
try {
    & $Dotnet exec (Join-Path $smokeOutput 'PatchSmoke.dll') --packaging-smoke
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed; package not produced.' }
} finally { Pop-Location }
}
New-Item -ItemType Directory -Force -Path (Join-Path $release 'CoopBots') | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'CoopBots.dll') -Destination (Join-Path $release 'CoopBots')
Copy-Item -LiteralPath (Join-Path $build 'CoopBots.Kernel.dll') -Destination (Join-Path $release 'CoopBots')
Copy-Item -LiteralPath (Join-Path $repo 'src\CoopBots\mod_manifest.json') -Destination (Join-Path $release 'CoopBots')
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $release
Copy-Item -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Destination $release
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $release 'CoopBots')
Copy-DistributionLicenses -Destination $release
Compress-Archive -LiteralPath (Join-Path $release 'CoopBots'), (Join-Path $release 'README.md'), (Join-Path $release 'CHANGELOG.md'), (Join-Path $release 'LICENSE') -DestinationPath "$release.zip" -Force
if ($VerifiedGateBinding) {
    & $Python $inputAudit
    if ($LASTEXITCODE -ne 0) { throw 'Frozen inputs changed while the verified package was produced.' }
    if ((Get-FileHash -LiteralPath $bindingPath -Algorithm SHA256).Hash -ne $boundBindingHash -or (Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash -ne $boundSummaryHash -or (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $boundInputHash) { throw 'Verified receipt/input identity changed during packaging.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageHashes = [ordered]@{}
    foreach ($name in @('CoopBots.dll', 'CoopBots.Kernel.dll')) { $packageHashes["CoopBots/$name"] = $verifiedDlls[$name] }
    foreach ($item in @(
        @{ entry='CoopBots/mod_manifest.json'; source='src/CoopBots/mod_manifest.json' },
        @{ entry='CoopBots/THIRD_PARTY_NOTICES.md'; source='THIRD_PARTY_NOTICES.md' },
        @{ entry='README.md'; source='README.md' },
        @{ entry='CHANGELOG.md'; source='CHANGELOG.md' }
    )) { $packageHashes[$item.entry] = (Get-FileHash -LiteralPath (Join-Path $repo $item.source) -Algorithm SHA256).Hash }
    $packageHashes['LICENSE'] = (Get-FileHash -LiteralPath (Join-Path $repo 'LICENSE') -Algorithm SHA256).Hash
    foreach ($item in $distributionLicenses) { $packageHashes[$item.entry] = (Get-FileHash -LiteralPath (Join-Path $repo $item.source) -Algorithm SHA256).Hash }
    $archive = [IO.Compression.ZipFile]::OpenRead("$release.zip")
    try {
        foreach ($name in $packageHashes.Keys) {
            $entry = @($archive.Entries | Where-Object { $_.FullName -eq $name })
            if ($entry.Count -ne 1) { throw "Package must contain exactly one verified payload: $name" }
            $stream = $entry[0].Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $packageDigest = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $hasher.Dispose() }
            if ($packageDigest -ne $packageHashes[$name]) { throw "Package payload does not match verified/current bytes: $name" }
        }
    } finally { $archive.Dispose() }
    [ordered]@{
        source='verified-full-gate'; gate_binding=$bindingPath
        gate_binding_sha256=(Get-FileHash -LiteralPath $bindingPath -Algorithm SHA256).Hash
        full_gate=$binding.full_gate; summary_sha256=$binding.summary_sha256
        input_snapshot_sha256=$binding.input_snapshot_sha256
        package="$release.zip"; package_sha256=(Get-FileHash -LiteralPath "$release.zip" -Algorithm SHA256).Hash
        dll_sha256=$verifiedDlls; full_pass_lines=$fullPhase.pass_lines
        package_member_sha256=$packageHashes
        ordinary_evidence='Use original summary coverage; do not relabel full total as ordinary.'
        build_typeload_regression_smoke_rerun=$false
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$release.verification.json" -Encoding UTF8
}
Get-FileHash -LiteralPath (Join-Path $release 'CoopBots\CoopBots.dll')
Write-Output "Package: $release.zip"
