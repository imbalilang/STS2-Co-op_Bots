#Requires -Version 5.1
<#
    Offline build + entry-regression run for the permanent deck-edit heuristic
    fixes (build-permanent-edits).

    No required arguments. It locates the repo from PSScriptRoot, uses the
    workspace dotnet and the known game assembly directory, builds
    tests/PatchSmoke Release into a NEW work/building-edits/<UTC>/build, then
    runs PatchSmoke.dll --building-edits-only (which routes straight to
    BuildValueScenarios.Run()).

    Before anything else it snapshots the original writable source files into
    work/building-edits/before, but only once: a later run never overwrites the
    pre-edit copy the owner diffs against.

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

# The four source files this task may edit; snapshotted once for the owner diff.
$snapshotFiles = @(
    'src\CoopBots\BotBrain.cs',
    'src\CoopBots\Building\BuildValue.cs',
    'tests\PatchSmoke\BuildValueScenarios.cs',
    'tests\PatchSmoke\Program.cs'
)
$before = Join-Path $repo 'work\building-edits\before'

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss.ffffffZ')
$root = Join-Path $repo ("work\building-edits\" + $stamp)
$build = Join-Path $root 'build'
$logs = Join-Path $root 'logs'

$stages = New-Object System.Collections.ArrayList
$exitCodes = [ordered]@{ restore = $null; build = $null; run = $null }
$success = $false
$errorMessage = $null
$snapshotCreated = $false

function Get-Sha256([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativePath([string]$Base, [string]$Path) {
    $baseFull = [System.IO.Path]::GetFullPath($Base).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    return $pathFull.Substring($baseFull.Length)
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

function Save-BeforeSnapshot {
    if (Test-Path -LiteralPath $before) { return }
    New-Item -ItemType Directory -Path $before | Out-Null
    foreach ($relative in $snapshotFiles) {
        $source = Join-Path $repo $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $target = Join-Path $before $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
    $script:snapshotCreated = $true
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

    Save-BeforeSnapshot

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

    $exitCodes.run = Invoke-Stage -Name 'run' -Exe $dotnet `
        -Arguments @($smokeDll, '--building-edits-only') `
        -LogPath (Join-Path $logs 'building-edits-run.log')
    if ($exitCodes.run -ne 0) {
        throw "building-edits run failed with exit code $($exitCodes.run)"
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
    if (-not (Test-Path -LiteralPath $logs)) {
        New-Item -ItemType Directory -Path $logs | Out-Null
    }

    $beforeHashes = [ordered]@{}
    foreach ($relative in $snapshotFiles) {
        $snapshotPath = Join-Path $before $relative
        $beforeHashes[$relative] = Get-Sha256 $snapshotPath
    }

    $sourceHashes = [ordered]@{}
    foreach ($relative in $snapshotFiles) {
        $sourceHashes[$relative] = Get-Sha256 (Join-Path $repo $relative)
    }

    $summary = [ordered]@{
        schemaVersion   = 1
        tool            = 'verify-building-edits.ps1'
        task            = 'build-permanent-edits'
        generatedAtUtc  = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')
        success         = $success
        error           = $errorMessage
        repo            = $repo
        gameDataDir     = $game
        snapshotCreated = $snapshotCreated
        paths           = [ordered]@{
            root         = $root
            build        = $build
            logs         = $logs
            before       = $before
            buildLog     = (Join-Path $logs 'build.log')
            runLog       = (Join-Path $logs 'building-edits-run.log')
        }
        exitCodes       = $exitCodes
        stages          = @($stages)
        sha256          = [ordered]@{
            csproj       = Get-Sha256 $project
            verifyScript = Get-Sha256 (Join-Path $PSScriptRoot 'verify-building-edits.ps1')
            patchSmoke   = Get-Sha256 (Join-Path $build 'PatchSmoke.dll')
            coopBots     = Get-Sha256 (Join-Path $build 'CoopBots.dll')
            kernel       = Get-Sha256 (Join-Path $build 'CoopBots.Kernel.dll')
            sts2         = Get-Sha256 (Join-Path $game 'sts2.dll')
            godotSharp   = Get-Sha256 (Join-Path $game 'GodotSharp.dll')
            harmony      = Get-Sha256 (Join-Path $game '0Harmony.dll')
        }
        sourceHashes    = $sourceHashes
        beforeHashes    = $beforeHashes
        limitations     = @(
            'Offline build and synthetic model tests only: no battle, no game deployment.',
            'Entry regressions exercise BotBrain.SelectCards against real game models, not a live multiplayer run.',
            'The live real deck is only read; the hypothetical deck is a local copy, so no live deck mutation is possible.',
            'Outputs are isolated under work/building-edits/<UTC>; nothing is written to outputs/ or releases/.'
        )
    }
    $summaryPath = Join-Path $root 'verify-summary.json'
    ($summary | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "verify summary: $summaryPath"
}

if ($success) { exit 0 } else { exit 1 }
