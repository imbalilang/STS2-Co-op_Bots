param(
    [string]$GameData = 'B:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64',
    [string]$Dotnet = '',
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$Dotnet) {
    $Dotnet = Join-Path $repo 'work\dotnet-win\dotnet.exe'
    if (!(Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
}
if (!(Test-Path -LiteralPath (Join-Path $GameData 'sts2.dll'))) { throw 'GameData must contain sts2.dll.' }
$manifest = Get-Content -Raw -Encoding utf8 (Join-Path $repo 'src\CoopBots\mod_manifest.json') | ConvertFrom-Json
if (!$Version) { $Version = $manifest.version }
if ($Version -ne $manifest.version -or $Version -notmatch '^\d+\.\d+\.\d+(-[a-z0-9.]+)?$') { throw 'Version must match the manifest.' }
$build = Join-Path $repo "work\build-$Version"
$release = Join-Path $repo "outputs\CoopBots-v$Version"
$project = Join-Path $repo 'src\CoopBots\CoopBots.csproj'
& $Dotnet build $project -c Release "-p:STS2DataDir=$GameData" "-p:OutputPath=$build\" -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# The game enumerates our types before the initializer can load CoopBots.Kernel.
# Guard against a type that needs the kernel at load time (see tests/TypeLoadCheck).
& $Dotnet run --project (Join-Path $repo 'tests\TypeLoadCheck\TypeLoadCheck.csproj') -c Release -- $build $GameData
if ($LASTEXITCODE -ne 0) { throw 'Type-load guard failed: the mod cannot be loaded without CoopBots.Kernel.' }
& $Dotnet run --project (Join-Path $repo 'tests\PatchSmoke\PatchSmoke.csproj') -c Release "-p:STS2DataDir=$GameData" "-p:CoopBotsDll=$build\CoopBots.dll" -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed; package not produced.' }
& $Dotnet run --project (Join-Path $repo 'tests\PatchSmoke\PatchSmoke.csproj') -c Release "-p:STS2DataDir=$GameData" "-p:CoopBotsDll=$build\CoopBots.dll" -p:EnableKernelTests=true -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Kernel regression tests failed; package not produced.' }
New-Item -ItemType Directory -Force -Path (Join-Path $release 'CoopBots') | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'CoopBots.dll') -Destination (Join-Path $release 'CoopBots')
Copy-Item -LiteralPath (Join-Path $build 'CoopBots.Kernel.dll') -Destination (Join-Path $release 'CoopBots')
Copy-Item -LiteralPath (Join-Path $repo 'src\CoopBots\mod_manifest.json') -Destination (Join-Path $release 'CoopBots')
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $release
Copy-Item -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Destination $release
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $release 'CoopBots')
Compress-Archive -LiteralPath (Join-Path $release 'CoopBots'), (Join-Path $release 'README.md'), (Join-Path $release 'CHANGELOG.md') -DestinationPath "$release.zip" -Force
Get-FileHash -LiteralPath (Join-Path $release 'CoopBots\CoopBots.dll')
Write-Output "Package: $release.zip"
