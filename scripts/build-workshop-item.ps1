# Builds the Steam Workshop item folder for CoopBots from the release zip.
# The Workshop item root must contain mod_manifest.json (same layout the game
# reads from the local mods directory, and the same layout RitsuLib uses).
param(
    # Package to stage. Defaults to the release artifact outputs\CoopBots-v<version>.zip --
    # that file is what shipped and is the only thing that may be uploaded. Pass a
    # labeled package (-Zip outputs\CoopBots-v0.36.4-<label>.zip) to rehearse the
    # staging step without touching the release name.
    [string]$Zip = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Read as UTF-8: Windows PowerShell would otherwise decode the manifest as ANSI
# and choke on the Chinese name.
$version = (Get-Content (Join-Path $root 'src\CoopBots\mod_manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version
if (-not $Zip) { $Zip = Join-Path $root "outputs\CoopBots-v$version.zip" }
$zip = $Zip
$staging = Join-Path $root 'work\workshop\CoopBots'

if (-not (Test-Path $zip)) { throw "release package not found: $zip" }
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Extract to a temp folder, then lift the CoopBots/ contents to the item root.
$temp = Join-Path $root 'work\workshop\_extract'
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $temp -Force
Get-ChildItem (Join-Path $temp 'CoopBots') | ForEach-Object {
    Move-Item $_.FullName (Join-Path $staging $_.Name)
}
Remove-Item $temp -Recurse -Force

Get-ChildItem $staging | Select-Object Name, Length
Write-Output "staging: $staging (version $version)"
