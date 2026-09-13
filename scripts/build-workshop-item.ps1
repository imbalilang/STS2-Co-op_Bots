# Builds the Steam Workshop item folder for CoopBots from the release zip.
# The Workshop item root must contain mod_manifest.json (same layout the game
# reads from the local mods directory, and the same layout RitsuLib uses).
$ErrorActionPreference = 'Stop'

# Read as UTF-8: Windows PowerShell would otherwise decode the manifest as ANSI
# and choke on the Chinese name.
$version = (Get-Content 'B:\slay-the-spire-2-mod-mod\src\CoopBots\mod_manifest.json' -Raw -Encoding UTF8 | ConvertFrom-Json).version
$zip = "B:\slay-the-spire-2-mod-mod\outputs\CoopBots-v$version.zip"
$staging = 'B:\slay-the-spire-2-mod-mod\work\workshop\CoopBots'

if (-not (Test-Path $zip)) { throw "release package not found: $zip" }
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Extract to a temp folder, then lift the CoopBots/ contents to the item root.
$temp = 'B:\slay-the-spire-2-mod-mod\work\workshop\_extract'
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $temp -Force
Get-ChildItem (Join-Path $temp 'CoopBots') | ForEach-Object {
    Move-Item $_.FullName (Join-Path $staging $_.Name)
}
Remove-Item $temp -Recurse -Force

Get-ChildItem $staging | Select-Object Name, Length
Write-Output "staging: $staging (version $version)"
