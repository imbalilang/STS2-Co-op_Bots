#Requires -Version 5.1
<#
    Offline build + run for the default-off decision capture (build-pick-decision-capture).

    No required arguments. It locates the repo from PSScriptRoot, uses the
    workspace dotnet and the known game assembly directory, builds
    tests/PatchSmoke Release into a NEW work/building-decision-capture/<UTC>/build,
    then runs PatchSmoke.dll --building-capture-only with a capture directory
    under the same UTC folder.

    It starts no battle, touches no game install and copies nothing into
    outputs/ or releases/. Every stage log, exit code and SHA256 is recorded in
    verify-summary.json; the first nonzero stage stops the run.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo 'work\dotnet-win\dotnet.exe'
$game = 'B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64'
$project = Join-Path $repo 'tests\PatchSmoke\PatchSmoke.csproj'

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss.ffffffZ')
$root = Join-Path $repo ("work\building-decision-capture\" + $stamp)
$build = Join-Path $root 'build'
$capture = Join-Path $root 'capture'
$logs = Join-Path $root 'logs'

$stages = New-Object System.Collections.ArrayList
$exitCodes = [ordered]@{ restore = $null; build = $null; run = $null }
$success = $false
$errorMessage = $null

function Get-Sha256([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Stage {
    param(
        [string]$Name,
        [string]$Exe,
        [string[]]$Arguments,
        [string]$LogPath
    )
    $entry = [ordered]@{ stage = $Name; exe = $Exe; arguments = $Arguments; exit = $null; log = $LogPath }
    $code = -1
    try {
        $output = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    catch {
        $output = @($_.Exception.Message)
        $code = -1
    }
    if ($null -eq $code) { $code = -1 }
    ($output | Out-String) | Set-Content -LiteralPath $LogPath -Encoding utf8
    $entry.exit = [int]$code
    [void]$stages.Add($entry)
    return [int]$code
}

try {
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw "workspace dotnet not found: $dotnet"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $game 'sts2.dll') -PathType Leaf)) {
        throw "STS2DataDir must contain sts2.dll: $game"
    }
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "PatchSmoke project not found: $project"
    }

    New-Item -ItemType Directory -Path $root | Out-Null
    New-Item -ItemType Directory -Path $build, $capture, $logs | Out-Null

    $exitCodes.restore = Invoke-Stage -Name 'restore' -Exe $dotnet `
        -Arguments @('restore', $project, '--ignore-failed-sources', "-p:STS2DataDir=$game", '-p:NuGetAudit=false') `
        -LogPath (Join-Path $logs 'restore.log')
    if ($exitCodes.restore -ne 0) {
        throw "restore failed with exit code $($exitCodes.restore)"
    }

    $exitCodes.build = Invoke-Stage -Name 'build' -Exe $dotnet `
        -Arguments @('build', $project, '-c', 'Release', '--no-restore', "-p:STS2DataDir=$game", "-p:OutputPath=$build\", '-p:EnableKernelTests=false', '-p:NuGetAudit=false') `
        -LogPath (Join-Path $logs 'build.log')
    if ($exitCodes.build -ne 0) {
        throw "PatchSmoke build failed with exit code $($exitCodes.build)"
    }

    $smokeDll = Join-Path $build 'PatchSmoke.dll'
    if (-not (Test-Path -LiteralPath $smokeDll -PathType Leaf)) {
        throw "PatchSmoke.dll was not produced: $smokeDll"
    }

    $exitCodes.run = Invoke-Stage -Name 'run' -Exe $dotnet `
        -Arguments @($smokeDll, '--building-capture-only', '--out', $capture) `
        -LogPath (Join-Path $logs 'capture-run.log')
    if ($exitCodes.run -ne 0) {
        throw "capture run failed with exit code $($exitCodes.run)"
    }

    $success = $true
}
catch {
    $errorMessage = $_.Exception.Message
}
finally {
    if (-not (Test-Path -LiteralPath $root)) {
        New-Item -ItemType Directory -Path $root | Out-Null
    }

    $captureHashes = [ordered]@{}
    if (Test-Path -LiteralPath $capture) {
        foreach ($file in Get-ChildItem -LiteralPath $capture -Recurse -File -ErrorAction SilentlyContinue) {
            $captureHashes[$file.FullName.Substring($capture.Length).TrimStart('\')] = Get-Sha256 $file.FullName
        }
    }

    $summary = [ordered]@{
        schemaVersion  = 1
        tool           = 'verify-building-decision-capture.ps1'
        task           = 'build-pick-decision-capture'
        generatedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')
        success        = $success
        error          = $errorMessage
        repo           = $repo
        gameDataDir    = $game
        paths          = [ordered]@{
            root        = $root
            build       = $build
            capture     = $capture
            logs        = $logs
            buildLog    = (Join-Path $logs 'build.log')
            runLog      = (Join-Path $logs 'capture-run.log')
        }
        exitCodes      = $exitCodes
        stages         = @($stages)
        sha256         = [ordered]@{
            csproj       = Get-Sha256 $project
            patchSmoke   = Get-Sha256 (Join-Path $build 'PatchSmoke.dll')
            coopBots     = Get-Sha256 (Join-Path $build 'CoopBots.dll')
            kernel       = Get-Sha256 (Join-Path $build 'CoopBots.Kernel.dll')
            sts2         = Get-Sha256 (Join-Path $game 'sts2.dll')
            godotSharp   = Get-Sha256 (Join-Path $game 'GodotSharp.dll')
            harmony      = Get-Sha256 (Join-Path $game '0Harmony.dll')
        }
        captureHashes  = $captureHashes
        limitations    = @(
            'Offline build and synthetic model tests only: no battle, no game deployment.',
            'The capture artifacts are synthetic local records, not live reward-screen captures.',
            'Outputs are isolated under work/building-decision-capture/<UTC>; nothing is written to outputs/ or releases/.'
        )
    }
    $summaryPath = Join-Path $root 'verify-summary.json'
    ($summary | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "verify summary: $summaryPath"
}

if ($success) { exit 0 } else { exit 1 }
