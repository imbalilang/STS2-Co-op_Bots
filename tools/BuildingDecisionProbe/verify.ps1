#Requires -Version 5.1
<#
    Offline build + dry-run entry for the BuildValue pick fixtures.

    No required arguments. It locates the repo from PSScriptRoot, uses the
    workspace dotnet and the known game assembly directory, builds the probe
    into a NEW work/pick-fixtures/<UTC>/build, runs the adapter to produce the
    real BuildValue scores, then runs the Python fixture builder into a NEW
    cases subdirectory.

    It starts no battle, touches no game install and copies nothing into
    outputs/ or releases/. Every stage log, exit code and SHA256 is recorded in
    verify-summary.json; the first nonzero stage stops the run.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dotnet = Join-Path $repo 'work\dotnet-win\dotnet.exe'
$game = 'B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64'
$project = Join-Path $repo 'tools\BuildingDecisionProbe\BuildingDecisionProbe.csproj'
$fixtures = Join-Path $repo 'tools\BuildingDecisionProbe\fixtures.json'
$pyScript = Join-Path $repo 'scripts\building_pick_cases.py'

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss.ffffffZ')
$root = Join-Path $repo ("work\pick-fixtures\" + $stamp)
$build = Join-Path $root 'build'
$cases = Join-Path $root 'cases'
$logs = Join-Path $root 'logs'
$adapterOut = Join-Path $root 'adapter-output.json'

$stages = New-Object System.Collections.ArrayList
$exitCodes = [ordered]@{ build = $null; adapter = $null; builder = $null }
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
        throw "probe project not found: $project"
    }
    if (-not (Test-Path -LiteralPath $fixtures -PathType Leaf)) {
        throw "fixture spec not found: $fixtures"
    }
    if (-not (Test-Path -LiteralPath $pyScript -PathType Leaf)) {
        throw "Python fixture builder not found: $pyScript"
    }

    New-Item -ItemType Directory -Path $root | Out-Null
    New-Item -ItemType Directory -Path $build, $logs | Out-Null

    $exitCodes.build = Invoke-Stage -Name 'build' -Exe $dotnet `
        -Arguments @('build', $project, '-c', 'Release', "-p:STS2DataDir=$game", "-p:OutputPath=$build\", '-p:NuGetAudit=false') `
        -LogPath (Join-Path $logs 'build.log')
    if ($exitCodes.build -ne 0) {
        throw "probe build failed with exit code $($exitCodes.build)"
    }

    $adapterDll = Join-Path $build 'BuildingDecisionProbe.dll'
    $exitCodes.adapter = Invoke-Stage -Name 'adapter' -Exe $dotnet `
        -Arguments @($adapterDll, '--out', $adapterOut, '--fixtures', $fixtures, '--repo-root', $repo) `
        -LogPath (Join-Path $logs 'adapter.log')
    if ($exitCodes.adapter -ne 0) {
        throw "adapter failed with exit code $($exitCodes.adapter)"
    }

    $exitCodes.builder = Invoke-Stage -Name 'builder' -Exe 'python' `
        -Arguments @($pyScript, '--input', $adapterOut, '--output', $cases) `
        -LogPath (Join-Path $logs 'builder.log')
    if ($exitCodes.builder -ne 0) {
        throw "fixture builder failed with exit code $($exitCodes.builder)"
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
    $manifestPath = Join-Path $cases 'manifest.json'
    $summary = [ordered]@{
        schemaVersion = 1
        tool          = 'BuildingDecisionProbe/verify.ps1'
        generatedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')
        success       = $success
        error         = $errorMessage
        repo          = $repo
        gameDataDir   = $game
        paths         = [ordered]@{
            root        = $root
            build       = $build
            cases       = $cases
            logs        = $logs
            adapterJson = $adapterOut
            manifest    = $manifestPath
        }
        exitCodes     = $exitCodes
        stages        = @($stages)
        sha256        = [ordered]@{
            csproj      = Get-Sha256 $project
            fixtures    = Get-Sha256 $fixtures
            probeScript = Get-Sha256 $pyScript
            adapterDll  = Get-Sha256 (Join-Path $build 'BuildingDecisionProbe.dll')
            coopBotsDll = Get-Sha256 (Join-Path $build 'CoopBots.dll')
            sts2        = Get-Sha256 (Join-Path $game 'sts2.dll')
            godotSharp  = Get-Sha256 (Join-Path $game 'GodotSharp.dll')
            harmony     = Get-Sha256 (Join-Path $game '0Harmony.dll')
            adapterJson = Get-Sha256 $adapterOut
            manifest    = Get-Sha256 $manifestPath
        }
        limitations   = @(
            'Offline build and model evaluation only: no battle, no game deployment.',
            'Outputs are isolated under work/pick-fixtures/<UTC>; nothing is written to outputs/ or releases/.',
            'The adapter scores PROXY fixtures, not observed reward screens; relic/potion context is empty by design.'
        )
    }
    $summaryPath = Join-Path $root 'verify-summary.json'
    ($summary | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "verify summary: $summaryPath"
}

if ($success) { exit 0 } else { exit 1 }
