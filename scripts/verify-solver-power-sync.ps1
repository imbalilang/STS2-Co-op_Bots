#Requires -Version 5.1
<#
    Offline build + focused regression run for the partial 0.41.0 power-strategy
    backport (solver-power-sync).

    No required arguments. Locates the repo from PSScriptRoot, uses the
    workspace dotnet and the known game assembly directory, builds
    tests/PatchSmoke Release with EnableKernelTests=true into a NEW
    work/solver-power-sync/<UTC>/build, then runs PatchSmoke.dll
    --kernel-power-only (KernelPowerRouteScenarios + existing kernel scenario
    regressions, with the presentation shim installed by KernelEngineScenarios).

    No deploy, no package, no battle. Prior work/solver-power-sync/before
    snapshots are never overwritten. Every stage log, exit code, elapsed time
    and SHA256 (source, upstream harvest, game assemblies, output assemblies,
    imported PowerSync files) is recorded in verify-summary.json; the first
    nonzero stage stops the run.
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
$before = Join-Path $repo 'work\solver-power-sync\before'

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss.ffffffZ')
$root = Join-Path $repo ("work\solver-power-sync\" + $stamp)
$build = Join-Path $root 'build'
$logs = Join-Path $root 'logs'

# Files this task may edit / did add: hashed so a report can point at the exact
# revision that produced the run.
$sourceFiles = @(
    'src\CoopBots.Kernel\KernelTeamSearch.cs',
    'src\CoopBots.Kernel\KernelPower.cs',
    'src\CoopBots.Kernel\POWER_SYNC.json',
    'src\CoopBots.Kernel\INTEGRATION.md',
    'tests\PatchSmoke\KernelPowerRouteScenarios.cs',
    'tests\PatchSmoke\KernelEngineScenarios.cs',
    'tests\PatchSmoke\Program.cs',
    'tests\PatchSmoke\PatchSmoke.csproj',
    'scripts\verify-solver-power-sync.ps1',
    'docs\COMBAT_SOLVER_POWER_SYNC.md'
)
$powerSyncImported = @(
    'src\CoopBots.Kernel\Vendor\PowerSync\PowerCardValuationContracts.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\PowerRouteAdmission.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\PowerCommitment.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\PowerCommitmentLifecycle.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\PowerCommitmentSeatPolicy.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\PowerActivationInvestmentPolicy.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\Cards\Ironclad\IroncladPowerRoutePolicy.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\Cards\Silent\SilentPowerRoutePolicy.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\Cards\Defect\DefectPowerRoutePolicy.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\Cards\Regent\RegentPowerRoutePolicy.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\Cards\Necrobinder\NecrobinderPowerRoutePolicy.cs',
    'src\CoopBots.Kernel\Vendor\PowerSync\Cards\Colorless\ColorlessPowerRoutePolicy.cs'
)
$upstreamRoot = Join-Path $repo 'work\combatsolver-sync-latest\src\Search\PowerCardValuation'
$upstreamFiles = @(
    'PowerCardValuationContracts.cs',
    'PowerRouteAdmission.cs',
    'Commitments\PowerCommitment.cs',
    'Commitments\PowerCommitmentLifecycle.cs',
    'Commitments\PowerCommitmentSeatPolicy.cs',
    'Commitments\PowerActivationInvestmentPolicy.cs',
    'Cards\Ironclad\IroncladPowerRoutePolicy.cs',
    'Cards\Silent\SilentPowerRoutePolicy.cs',
    'Cards\Defect\DefectPowerRoutePolicy.cs',
    'Cards\Regent\RegentPowerRoutePolicy.cs',
    'Cards\Necrobinder\NecrobinderPowerRoutePolicy.cs',
    'Cards\Colorless\ColorlessPowerRoutePolicy.cs'
)

$stages = New-Object System.Collections.ArrayList
$exitCodes = [ordered]@{ restore = $null; build = $null; run = $null }
$elapsedMs = [ordered]@{ restore = $null; build = $null; run = $null; total = $null }
$success = $false
$errorMessage = $null
$totalWatch = [System.Diagnostics.Stopwatch]::StartNew()

function Get-Sha256([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    # Use the framework directly: Get-FileHash is missing on some locked-down hosts.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        $hash = $sha.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    }
    catch {
        return $null
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        $sha.Dispose()
    }
}

function Invoke-Stage {
    param(
        [string]$Name,
        [string]$Exe,
        [string[]]$Arguments,
        [string]$LogPath
    )
    $entry = [ordered]@{ stage = $Name; exe = $Exe; arguments = $Arguments; exit = $null; log = $LogPath; elapsed_ms = $null }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $code = -1
    try {
        $output = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    catch {
        $output = @($_.Exception.Message)
        $code = -1
    }
    $watch.Stop()
    if ($null -eq $code) { $code = -1 }
    ($output | Out-String) | Set-Content -LiteralPath $LogPath -Encoding utf8
    $entry.exit = [int]$code
    $entry.elapsed_ms = [int]$watch.ElapsedMilliseconds
    [void]$stages.Add($entry)
    $script:elapsedMs[$Name] = [int]$watch.ElapsedMilliseconds
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
    New-Item -ItemType Directory -Path $build, $logs | Out-Null

    $exitCodes.restore = Invoke-Stage -Name 'restore' -Exe $dotnet `
        -Arguments @('restore', $project, '--ignore-failed-sources', "-p:STS2DataDir=$game", '-p:NuGetAudit=false') `
        -LogPath (Join-Path $logs 'restore.log')
    if ($exitCodes.restore -ne 0) {
        throw "restore failed with exit code $($exitCodes.restore)"
    }

    $exitCodes.build = Invoke-Stage -Name 'build' -Exe $dotnet `
        -Arguments @('build', $project, '-c', 'Release', '--no-restore', "-p:STS2DataDir=$game", "-p:OutputPath=$build\", '-p:EnableKernelTests=true', '-p:NuGetAudit=false') `
        -LogPath (Join-Path $logs 'build.log')
    if ($exitCodes.build -ne 0) {
        throw "PatchSmoke build failed with exit code $($exitCodes.build)"
    }

    $smokeDll = Join-Path $build 'PatchSmoke.dll'
    if (-not (Test-Path -LiteralPath $smokeDll -PathType Leaf)) {
        throw "PatchSmoke.dll was not produced: $smokeDll"
    }

    $exitCodes.run = Invoke-Stage -Name 'run' -Exe $dotnet `
        -Arguments @($smokeDll, '--kernel-power-only') `
        -LogPath (Join-Path $logs 'kernel-power-run.log')
    if ($exitCodes.run -ne 0) {
        throw "kernel-power-only run failed with exit code $($exitCodes.run)"
    }

    $success = $true
}
catch {
    $errorMessage = $_.Exception.Message
}
finally {
    $totalWatch.Stop()
    $elapsedMs.total = [int]$totalWatch.ElapsedMilliseconds
    if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root | Out-Null }
    if (-not (Test-Path -LiteralPath $logs)) { New-Item -ItemType Directory -Path $logs | Out-Null }

    $sourceHashes = [ordered]@{}
    $changedFromSnapshot = New-Object System.Collections.ArrayList
    foreach ($relative in $sourceFiles) {
        $sourceHashes[$relative] = Get-Sha256 (Join-Path $repo $relative)
    }
    foreach ($relative in $powerSyncImported) {
        $sourceHashes[$relative] = Get-Sha256 (Join-Path $repo $relative)
    }

    # A before/after compare against the owner snapshot is reported, never
    # rewritten; a mismatch is evidence, not a failure.
    if (Test-Path -LiteralPath $before) {
        $beforeKernel = Join-Path $before 'CoopBots.Kernel'
        foreach ($pair in @(
            @{ snapshot = (Join-Path $beforeKernel 'KernelTeamSearch.cs'); current = (Join-Path $repo 'src\CoopBots.Kernel\KernelTeamSearch.cs') },
            @{ snapshot = (Join-Path $before 'PatchSmoke\KernelEngineScenarios.cs'); current = (Join-Path $repo 'tests\PatchSmoke\KernelEngineScenarios.cs') },
            @{ snapshot = (Join-Path $before 'PatchSmoke\Program.cs'); current = (Join-Path $repo 'tests\PatchSmoke\Program.cs') },
            @{ snapshot = (Join-Path $before 'PatchSmoke\PatchSmoke.csproj'); current = (Join-Path $repo 'tests\PatchSmoke\PatchSmoke.csproj') }
        )) {
            $beforeHash = Get-Sha256 $pair.snapshot
            $currentHash = Get-Sha256 $pair.current
            if ($beforeHash -ne $currentHash) {
                [void]$changedFromSnapshot.Add(@{ snapshot = $beforeHash; current = $currentHash })
            }
        }
    }

    $powerSyncHashes = [ordered]@{ imported = [ordered]@{}; upstream = [ordered]@{} }
    foreach ($relative in $powerSyncImported) {
        $powerSyncHashes.imported[$relative] = Get-Sha256 (Join-Path $repo $relative)
    }
    foreach ($relative in $upstreamFiles) {
        $powerSyncHashes.upstream[$relative] = Get-Sha256 (Join-Path $upstreamRoot $relative)
    }
    $powerSyncHashesPath = Join-Path $root 'power-sync-hashes.json'
    ($powerSyncHashes | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $powerSyncHashesPath -Encoding utf8

    $summary = [ordered]@{
        schemaVersion        = 1
        tool                 = 'verify-solver-power-sync.ps1'
        task                 = 'solver-power-sync'
        generatedAtUtc       = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')
        success              = $success
        error                = $errorMessage
        repo                 = $repo
        gameDataDir          = $game
        upstreamCommit       = '0e6cc2df342ed300c854013a7ec667eae4f58861'
        paths                = [ordered]@{
            root              = $root
            build             = $build
            logs              = $logs
            buildLog          = (Join-Path $logs 'build.log')
            runLog            = (Join-Path $logs 'kernel-power-run.log')
            powerSyncHashes   = $powerSyncHashesPath
        }
        exitCodes            = $exitCodes
        elapsedMs            = $elapsedMs
        stages               = @($stages)
        changedFromSnapshot  = @($changedFromSnapshot)
        sha256               = [ordered]@{
            csproj            = Get-Sha256 $project
            verifyScript      = Get-Sha256 (Join-Path $PSScriptRoot 'verify-solver-power-sync.ps1')
            patchSmoke        = Get-Sha256 (Join-Path $build 'PatchSmoke.dll')
            coopBots          = Get-Sha256 (Join-Path $build 'CoopBots.dll')
            kernel            = Get-Sha256 (Join-Path $build 'CoopBots.Kernel.dll')
            sts2              = Get-Sha256 (Join-Path $game 'sts2.dll')
            godotSharp        = Get-Sha256 (Join-Path $game 'GodotSharp.dll')
            harmony           = Get-Sha256 (Join-Path $game '0Harmony.dll')
            powerSyncImported = $powerSyncHashes.imported
            powerSyncUpstream = $powerSyncHashes.upstream
        }
        sourceHashes         = $sourceHashes
        limitations          = @(
            'Offline build and synthetic model tests only: no battle, no game deployment, no packaging.',
            'The focused run drives KernelTeamSearch with synthetic and production KernelCombatEvaluation, not a live multiplayer match.',
            'Per-card 0.41.0 projections, full single-player opening-power prefix portfolios and autoplay detection are NOT ported.',
            'Outputs are isolated under work/solver-power-sync/<UTC>; the owner before/ snapshots are only read.'
        )
    }
    $summaryPath = Join-Path $root 'verify-summary.json'
    ($summary | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "verify summary: $summaryPath"
    Write-Host "power-sync hashes: $powerSyncHashesPath"
}

if ($success) { exit 0 } else { exit 1 }
