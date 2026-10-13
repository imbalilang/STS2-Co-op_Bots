# Verifies a CoopBots working tree without touching release artifacts.
#
# Builds the MOD (Release) into a fresh, unique run directory, then runs the
# type-load guard and one unfiltered full-kernel PatchSmoke run against that assembly.
# The latter retains the ordinary entrypoints; ordinary coverage is labelled as
# covered-by-full rather than inventing a separate physical run/count/exit code.
# Every phase log and the final summary.json land in the run directory.
#
# Usage:
#   scripts\verify-project.ps1
#   scripts\verify-project.ps1 -GameData 'D:\Steam\...\data_sts2_windows_x86_64'
#   scripts\verify-project.ps1 -Dotnet 'C:\Program Files\dotnet\dotnet.exe'
#
# This script never invokes build.ps1, never packages, never deploys and never
# writes under outputs/. It does not modify prior work/verification runs.
param(
    [string]$GameData = 'B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64',
    [string]$Dotnet = '',
    [string]$Python = 'python',
    [string]$RitsuLibDir = '',
    [string]$RitsuLibSharedDir = '',
    # Optional isolated ordinary configuration for hook/environment diagnostics.
    [switch]$IncludeOrdinary
)

$ErrorActionPreference = 'Stop'

# Keep native stderr from terminating the run on hosts where it is promoted to
# an error. Exit codes are checked explicitly below.
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repo = Split-Path $PSScriptRoot -Parent

if (!(Test-Path -LiteralPath (Join-Path $GameData 'sts2.dll'))) {
    throw "GameData must contain sts2.dll: $GameData"
}

if (!$Dotnet) {
    $Dotnet = Join-Path $repo 'work\dotnet-win\dotnet.exe'
    if (!(Test-Path -LiteralPath $Dotnet)) {
        $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
} elseif (![System.IO.Path]::IsPathRooted($Dotnet)) {
    $Dotnet = Join-Path $repo $Dotnet
}
if (!(Test-Path -LiteralPath $Dotnet)) { throw "dotnet not found: $Dotnet" }
$Python = (Get-Command $Python -ErrorAction Stop).Source
$referenceArgs = @()
if ($RitsuLibDir) { $RitsuLibDir = (Resolve-Path -LiteralPath $RitsuLibDir).Path; $referenceArgs += "-p:RitsuLibDir=$RitsuLibDir" }
if ($RitsuLibSharedDir) { $RitsuLibSharedDir = (Resolve-Path -LiteralPath $RitsuLibSharedDir).Path; $referenceArgs += "-p:RitsuLibSharedDir=$RitsuLibSharedDir" }

$modProject = Join-Path $repo 'src\CoopBots\CoopBots.csproj'
$typeLoadProject = Join-Path $repo 'tests\TypeLoadCheck\TypeLoadCheck.csproj'
$patchSmokeProject = Join-Path $repo 'tests\PatchSmoke\PatchSmoke.csproj'

# A new run directory per invocation, keyed by UTC. Prior runs are never reused.
$verificationRoot = Join-Path $repo 'work\verification'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runDir = Join-Path $verificationRoot $stamp
if (Test-Path -LiteralPath $runDir) {
    $suffix = 1
    do {
        $runDir = Join-Path $verificationRoot ("$stamp-$suffix")
        $suffix++
    } while (Test-Path -LiteralPath $runDir)
}
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$buildDir = Join-Path $runDir 'build'
$modDll = Join-Path $buildDir 'CoopBots.dll'
$kernelDll = Join-Path $buildDir 'CoopBots.Kernel.dll'
$summaryPath = Join-Path $runDir 'summary.json'

function Get-PassLineCount {
    param([string]$Path)
    if (!(Test-Path -LiteralPath $Path)) { return 0 }
    $content = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($content)) { return 0 }
    # A PASS line starts with the token; test-case counts are deliberately not
    # derived from the log.
    return ([regex]::Matches($content, '(?m)^PASS\b')).Count
}

$startedUtc = [DateTime]::UtcNow.ToString('o')
$status = 'error'
$scriptError = $null
$hadFailure = $false
$phases = New-Object System.Collections.Generic.List[object]
$testArtifacts = New-Object System.Collections.Generic.List[object]
$inputsVerified = $false
$testEvidenceVerified = $false
$inputAudit = Join-Path $runDir 'final-source-audit.py'
$inputPath = Join-Path $runDir 'final-inputs.json'
Copy-Item -LiteralPath (Join-Path $repo 'scripts/gate-inputs.py') -Destination $inputAudit
[ordered]@{ repo=$repo; game_data=$GameData; dotnet=$Dotnet; ritsulib_dir=$RitsuLibDir; ritsulib_shared_dir=$RitsuLibSharedDir } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDir 'gate-inputs-config.json') -Encoding UTF8

$phaseSpecs = @(
    [pscustomobject]@{
        name = 'build'
        file = 'phase-01-build.log'
        exe = $Dotnet
        args = @('build', $modProject, '-c', 'Release', "-p:STS2DataDir=$GameData", "-p:OutputPath=$buildDir\", '-p:NuGetAudit=false') + $referenceArgs
    },
    [pscustomobject]@{
        name = 'typeloadcheck'
        file = 'phase-02-typeloadcheck.log'
        exe = $Dotnet
        args = @('run', '--project', $typeLoadProject, '-c', 'Release', '-p:NuGetAudit=false', '--', $buildDir, $GameData)
    },
    $(if ($IncludeOrdinary) {
        [pscustomobject]@{
            name = 'patchsmoke'
            file = 'phase-03-patchsmoke.log'
            exe = $Dotnet
            args = @('run', '--project', $patchSmokeProject, '-c', 'Release', "-p:STS2DataDir=$GameData", "-p:CoopBotsDll=$modDll", '-p:EnableKernelTests=false', '-p:NuGetAudit=false') + $referenceArgs
        }
    }),
    [pscustomobject]@{
        name = 'patchsmoke-kernel'
        file = 'phase-04-patchsmoke-kernel.log'
        exe = $Dotnet
        args = @('run', '--project', $patchSmokeProject, '-c', 'Release', "-p:STS2DataDir=$GameData", "-p:CoopBotsDll=$modDll", '-p:EnableKernelTests=true', '-p:NuGetAudit=false') + $referenceArgs
    }
)

try {
    # A clean clone has no ignored Krafs.Publicizer package cache. Restore it
    # before freezing toolchain inputs; this step does not build or run tests.
    $restoreArgs = @('restore', $patchSmokeProject, "-p:STS2DataDir=$GameData", '-p:NuGetAudit=false') + $referenceArgs
    [ordered]@{ command=$Dotnet; args=$restoreArgs; started_utc=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDir 'bootstrap-restore-command.json') -Encoding UTF8
    & $Dotnet @restoreArgs *> (Join-Path $runDir 'bootstrap-restore.log')
    $restoreExit = $LASTEXITCODE
    [ordered]@{ exit_code=$restoreExit; finished_utc=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDir 'bootstrap-restore-result.json') -Encoding UTF8
    if ($restoreExit -ne 0) { throw 'Dependency restore failed; gate not started.' }
    & $Python $inputAudit --freeze *> (Join-Path $runDir 'pre-gate-input-check.log')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot freeze source/reference/toolchain inputs; gate not started.' }
    foreach ($spec in ($phaseSpecs | Where-Object { $null -ne $_ })) {
        $logPath = Join-Path $runDir $spec.file
        $record = [ordered]@{
            name = $spec.name
            command = $spec.exe
            args = @($spec.args)
            log = $spec.file
            exit_code = $null
            pass_lines = $null
            status = 'pending'
            started_utc = $null
            finished_utc = $null
        }

        if ($hadFailure) {
            $record.status = 'skipped'
            $phases.Add([pscustomobject]$record)
            continue
        }

        $record.started_utc = [DateTime]::UtcNow.ToString('o')
        try {
            & $spec.exe @($spec.args) *> $logPath
            $code = $LASTEXITCODE
            $record.finished_utc = [DateTime]::UtcNow.ToString('o')
            $record.exit_code = $code
            $record.pass_lines = Get-PassLineCount $logPath
            if ($code -eq 0) {
                $record.status = 'passed'
            } else {
                $record.status = 'failed'
                $status = 'failed'
                $hadFailure = $true
            }
        } catch {
            # A phase that throws still leaves a record, so a broken phase never
            # disappears from summary.json.
            $record.finished_utc = [DateTime]::UtcNow.ToString('o')
            $record.status = 'error'
            $record.error = $_.Exception.Message
            $status = 'error'
            $scriptError = $_.Exception.Message
            $hadFailure = $true
        }
        $phases.Add([pscustomobject]$record)
    }

    if (!$hadFailure) {
        & $Python $inputAudit *> (Join-Path $runDir 'post-gate-input-check.log')
        if ($LASTEXITCODE -ne 0) { throw 'Source/reference/toolchain changed during verification.' }
        $inputsVerified = $true
        $testEvidence = Join-Path $runDir 'test-evidence'
        New-Item -ItemType Directory -Path $testEvidence | Out-Null
        $testOutput = Join-Path $repo 'tests/PatchSmoke/bin/Release/net9.0'
        foreach ($name in @('CoopBots.dll', 'CoopBots.pdb', 'CoopBots.Kernel.dll', 'CoopBots.Kernel.pdb', 'PatchSmoke.dll', 'PatchSmoke.pdb')) {
            $runtime = Join-Path $testOutput $name
            $runtimeHash = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
            $gateBuild = $(if ($name -notlike 'PatchSmoke.*') { Join-Path $buildDir $name } else { $null })
            $buildHash = $(if ($gateBuild) { (Get-FileHash -LiteralPath $gateBuild -Algorithm SHA256).Hash } else { $null })
            if ($gateBuild -and $runtimeHash -ne $buildHash) { throw "Actual test/build artifact differs: $name" }
            $physical = Join-Path $testEvidence $name
            Copy-Item -LiteralPath $runtime -Destination $physical
            $physicalHash = (Get-FileHash -LiteralPath $physical -Algorithm SHA256).Hash
            if ($physicalHash -ne $runtimeHash -or (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $runtimeHash) { throw "Actual test artifact changed during archive: $name" }
            $testArtifacts.Add([pscustomobject]@{
                name=$name; runtime_path=$runtime; runtime_sha256=$runtimeHash
                physical_path=$physical; physical_sha256=$physicalHash
                build_path=$gateBuild; build_sha256=$buildHash
            })
        }
        $testEvidenceVerified = $true
        $status = 'pass'
    }
} catch {
    $status = 'error'
    $scriptError = $_.Exception.Message
} finally {
    $finishedUtc = [DateTime]::UtcNow.ToString('o')

    $dllHashes = [ordered]@{}
    foreach ($name in @('CoopBots.dll', 'CoopBots.Kernel.dll')) {
        $path = Join-Path $buildDir $name
        if (Test-Path -LiteralPath $path) {
            $dllHashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        } else {
            $dllHashes[$name] = $null
        }
    }

    try {
        $fullRecord = $phases.ToArray() | Where-Object { $_.name -eq 'patchsmoke-kernel' } | Select-Object -First 1
        $ordinaryRecord = $phases.ToArray() | Where-Object { $_.name -eq 'patchsmoke' } | Select-Object -First 1
        $ordinaryCovered = $fullRecord.status -eq 'passed' -and $fullRecord.exit_code -eq 0
        $summary = [ordered]@{
            tool = 'verify-project.ps1'
            gate_protocol = 'once-full-kernel-v1'
            task = 'prep-verification-tools'
            started_utc = $startedUtc
            finished_utc = $finishedUtc
            run_dir = $runDir
            game_data = $GameData
            dotnet = $Dotnet
            python = $Python
            reference_properties = [ordered]@{ RitsuLibDir=$RitsuLibDir; RitsuLibSharedDir=$RitsuLibSharedDir }
            status = $status
            error = $scriptError
            # Generic List[object] is handed to the serializer as an explicit
            # array; passing the list itself makes ConvertTo-Json/the ordered
            # dictionary throw "Argument types do not match".
            phases = $phases.ToArray()
            dll_sha256 = $dllHashes
            suite_coverage = [ordered]@{
                ordinary = [ordered]@{
                    status = $(if ($ordinaryRecord) { $ordinaryRecord.status } elseif ($ordinaryCovered) { 'covered-by-full' } else { 'unconfirmed' })
                    evidence_phase = $(if ($ordinaryRecord) { 'patchsmoke' } else { 'patchsmoke-kernel' })
                    evidence_log = $(if ($ordinaryRecord) { 'phase-03-patchsmoke.log' } else { 'phase-04-patchsmoke-kernel.log' })
                    physical_run = $null -ne $ordinaryRecord.started_utc
                    pass_lines = $ordinaryRecord.pass_lines
                    evidence_exit_code = $(if ($ordinaryRecord) { $ordinaryRecord.exit_code } else { $fullRecord.exit_code })
                    boundary = 'Default: ordinary entrypoints covered in full-kernel configuration, with no standalone count/exit. IncludeOrdinary records an additional isolated physical run.'
                }
                full = [ordered]@{
                    status = $fullRecord.status
                    evidence_phase = 'patchsmoke-kernel'
                    pass_lines = $fullRecord.pass_lines
                    unfiltered = $true
                }
            }
            suite_sources = [ordered]@{
                program_sha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'tests/PatchSmoke/Program.cs') -Algorithm SHA256).Hash
                cooperative_sha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'tests/PatchSmoke/CooperativeScenarios.cs') -Algorithm SHA256).Hash
                project_sha256 = (Get-FileHash -LiteralPath $patchSmokeProject -Algorithm SHA256).Hash
            }
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    } catch {
        # A missing or invalid summary must fail the run even when every phase
        # passed, otherwise a green run would leave no usable evidence.
        $status = 'error'
        $scriptError = "failed to write summary.json: $($_.Exception.Message)"
        Write-Warning $scriptError
    }

    Write-Output "verify-project: $status"
    Write-Output "run: $runDir"
    Write-Output "summary: $summaryPath"
    # The direct verifier now supplies the same immutable binding accepted by
    # build.ps1; no epoch-specific wrapper is required for verified packaging.
    $bindingPath = Join-Path $runDir 'gate-run-binding.json'
    try {
        $phaseLogHashes = [ordered]@{}
        foreach ($phase in $phases.ToArray()) {
            $phaseLog = Join-Path $runDir $phase.log
            if (Test-Path -LiteralPath $phaseLog) { $phaseLogHashes[$phase.log] = (Get-FileHash -LiteralPath $phaseLog -Algorithm SHA256).Hash }
        }
        [ordered]@{
            repo=$repo; full_gate=$runDir; status=$status
            exit_code=$(if ($status -eq 'pass') { 0 } else { 1 })
            input_snapshot_sha256=$(if (Test-Path -LiteralPath $inputPath) { (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash } else { $null })
            summary_sha256=$(if (Test-Path -LiteralPath $summaryPath) { (Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash } else { $null })
            source_inputs_verified_after_gate=$inputsVerified
            test_evidence_verified=$testEvidenceVerified
            test_artifacts=$testArtifacts.ToArray()
            phase_log_sha256=$phaseLogHashes
            patchsmoke_sha256=($testArtifacts.ToArray() | Where-Object { $_.name -eq 'PatchSmoke.dll' } | Select-Object -First 1).physical_sha256
            protocol='once-full-kernel-v1'; error=$scriptError
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $bindingPath -Encoding UTF8
        Write-Output "binding: $bindingPath"
    } catch {
        $status = 'error'
        Write-Warning "Failed to write gate binding: $($_.Exception.Message)"
    }
}

if ($status -ne 'pass') { exit 1 }
