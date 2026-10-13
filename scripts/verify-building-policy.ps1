#Requires -Version 5.1
<#
    Offline build + focused regression run for the building-policy-v2 core
    phase.

    No required arguments. Locates the repo from PSScriptRoot, uses the
    workspace dotnet and the known game assembly directory, builds
    tests/PatchSmoke Release into a NEW work/building-policy-v2/<UTC>/build,
    then runs PatchSmoke.dll twice:

      --building-policy-only   BuildingPolicyScenarios (mechanism dependencies,
                               continuous relief, structure bottlenecks,
                               contextual upgrades)
      --building-edits-only    the existing BuildValueScenarios entry
                               regressions, so the intended affinity-direction
                               change is checked against the preserved suite

    No deploy, no package, no battle. The owner snapshots the pre-edit source
    under work/building-policy-v2/before; this script never writes there and
    only reports a before/after hash compare. Every stage log, exit code,
    elapsed time and SHA256 (source, game assemblies, output assemblies) is
    recorded in verify-summary.json; the first nonzero stage stops the run.
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
$before = Join-Path $repo 'work\building-policy-v2\before'

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss.ffffffZ')
$root = Join-Path $repo ("work\building-policy-v2\" + $stamp)
$build = Join-Path $root 'build'
$logs = Join-Path $root 'logs'

# Files this task may edit / did add: hashed so a report can point at the exact
# revision that produced the run.
$sourceFiles = @(
    'src\CoopBots\Building\BuildValue.cs',
    'src\CoopBots\Building\CardProfile.cs',
    'src\CoopBots\Building\DeckMechanisms.cs',
    'src\CoopBots\Building\DeckStructure.cs',
    'tests\PatchSmoke\BuildingPolicyScenarios.cs',
    'tests\PatchSmoke\BuildValueScenarios.cs',
    'tests\PatchSmoke\Program.cs',
    'scripts\verify-building-policy.ps1',
    'docs\building\BUILDING_POLICY_V2.md'
)

$stages = New-Object System.Collections.ArrayList
$exitCodes = [ordered]@{ restore = $null; build = $null; policyRun = $null; editsRun = $null }
$elapsedMs = [ordered]@{ restore = $null; build = $null; policyRun = $null; editsRun = $null; total = $null }
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
        -Arguments @('build', $project, '-c', 'Release', '--no-restore', "-p:STS2DataDir=$game", "-p:OutputPath=$build\", '-p:EnableKernelTests=false', '-p:NuGetAudit=false') `
        -LogPath (Join-Path $logs 'build.log')
    if ($exitCodes.build -ne 0) {
        throw "PatchSmoke build failed with exit code $($exitCodes.build)"
    }

    $smokeDll = Join-Path $build 'PatchSmoke.dll'
    if (-not (Test-Path -LiteralPath $smokeDll -PathType Leaf)) {
        throw "PatchSmoke.dll was not produced: $smokeDll"
    }

    $exitCodes.policyRun = Invoke-Stage -Name 'policyRun' -Exe $dotnet `
        -Arguments @($smokeDll, '--building-policy-only') `
        -LogPath (Join-Path $logs 'building-policy-run.log')
    if ($exitCodes.policyRun -ne 0) {
        throw "building-policy-only run failed with exit code $($exitCodes.policyRun)"
    }

    $exitCodes.editsRun = Invoke-Stage -Name 'editsRun' -Exe $dotnet `
        -Arguments @($smokeDll, '--building-edits-only') `
        -LogPath (Join-Path $logs 'building-edits-run.log')
    if ($exitCodes.editsRun -ne 0) {
        throw "building-edits-only run failed with exit code $($exitCodes.editsRun)"
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
    foreach ($relative in $sourceFiles) {
        $sourceHashes[$relative] = Get-Sha256 (Join-Path $repo $relative)
    }

    # A before/after compare against the owner snapshot is reported, never
    # rewritten; a mismatch is evidence, not a failure.
    $changedFromSnapshot = New-Object System.Collections.ArrayList
    if (Test-Path -LiteralPath $before) {
        foreach ($relative in $sourceFiles) {
            $snapshotPath = Join-Path $before $relative
            $beforeHash = Get-Sha256 $snapshotPath
            $currentHash = $sourceHashes[$relative]
            if ($beforeHash -ne $currentHash) {
                [void]$changedFromSnapshot.Add(@{ file = $relative; snapshot = $beforeHash; current = $currentHash })
            }
        }
    }

    $summary = [ordered]@{
        schemaVersion       = 1
        tool                = 'verify-building-policy.ps1'
        task                = 'building-policy-v2'
        generatedAtUtc      = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')
        success             = $success
        error               = $errorMessage
        repo                = $repo
        gameDataDir         = $game
        paths               = [ordered]@{
            root            = $root
            build           = $build
            logs            = $logs
            before          = $before
            buildLog        = (Join-Path $logs 'build.log')
            policyRunLog    = (Join-Path $logs 'building-policy-run.log')
            editsRunLog     = (Join-Path $logs 'building-edits-run.log')
        }
        exitCodes           = $exitCodes
        elapsedMs           = $elapsedMs
        stages              = @($stages)
        changedFromSnapshot = @($changedFromSnapshot)
        sha256              = [ordered]@{
            csproj          = Get-Sha256 $project
            verifyScript    = Get-Sha256 (Join-Path $PSScriptRoot 'verify-building-policy.ps1')
            patchSmoke      = Get-Sha256 (Join-Path $build 'PatchSmoke.dll')
            coopBots        = Get-Sha256 (Join-Path $build 'CoopBots.dll')
            kernel          = Get-Sha256 (Join-Path $build 'CoopBots.Kernel.dll')
            sts2            = Get-Sha256 (Join-Path $game 'sts2.dll')
            godotSharp      = Get-Sha256 (Join-Path $game 'GodotSharp.dll')
            harmony         = Get-Sha256 (Join-Path $game '0Harmony.dll')
        }
        sourceHashes        = $sourceHashes
        limitations         = @(
            'Offline build and synthetic model tests only: no battle, no game deployment, no packaging.',
            'The focused runs drive BuildValue/BotBrain with real game models, not a live multiplayer match.',
            'Full-suite verification is the owner''s step; this script only runs the two focused entry flags.',
            'Outputs are isolated under work/building-policy-v2/<UTC>; the owner before/ snapshot is only read.'
        )
    }
    $summaryPath = Join-Path $root 'verify-summary.json'
    ($summary | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "verify summary: $summaryPath"
}

if ($success) { exit 0 } else { exit 1 }
