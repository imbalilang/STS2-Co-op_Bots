# Verifies a CoopBots working tree without touching release artifacts.
#
# Builds the MOD (Release) into a fresh, unique run directory, then runs the
# type-load guard and the two PatchSmoke passes against that exact assembly.
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
    [string]$Dotnet = ''
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

$phaseSpecs = @(
    [pscustomobject]@{
        name = 'build'
        file = 'phase-01-build.log'
        exe = $Dotnet
        args = @('build', $modProject, '-c', 'Release', "-p:STS2DataDir=$GameData", "-p:OutputPath=$buildDir\", '-p:NuGetAudit=false')
    },
    [pscustomobject]@{
        name = 'typeloadcheck'
        file = 'phase-02-typeloadcheck.log'
        exe = $Dotnet
        args = @('run', '--project', $typeLoadProject, '-c', 'Release', '-p:NuGetAudit=false', '--', $buildDir, $GameData)
    },
    [pscustomobject]@{
        name = 'patchsmoke'
        file = 'phase-03-patchsmoke.log'
        exe = $Dotnet
        args = @('run', '--project', $patchSmokeProject, '-c', 'Release', "-p:STS2DataDir=$GameData", "-p:CoopBotsDll=$modDll", '-p:EnableKernelTests=false', '-p:NuGetAudit=false')
    },
    [pscustomobject]@{
        name = 'patchsmoke-kernel'
        file = 'phase-04-patchsmoke-kernel.log'
        exe = $Dotnet
        args = @('run', '--project', $patchSmokeProject, '-c', 'Release', "-p:STS2DataDir=$GameData", "-p:CoopBotsDll=$modDll", '-p:EnableKernelTests=true', '-p:NuGetAudit=false')
    }
)

try {
    foreach ($spec in $phaseSpecs) {
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

    if (!$hadFailure) { $status = 'pass' }
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
        $summary = [ordered]@{
            tool = 'verify-project.ps1'
            task = 'prep-verification-tools'
            started_utc = $startedUtc
            finished_utc = $finishedUtc
            run_dir = $runDir
            game_data = $GameData
            dotnet = $Dotnet
            status = $status
            error = $scriptError
            # Generic List[object] is handed to the serializer as an explicit
            # array; passing the list itself makes ConvertTo-Json/the ordered
            # dictionary throw "Argument types do not match".
            phases = $phases.ToArray()
            dll_sha256 = $dllHashes
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
}

if ($status -ne 'pass') { exit 1 }
