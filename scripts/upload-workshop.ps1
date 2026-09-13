# Uploads the current CoopBots release to the Steam Workshop via steamcmd.
#
# The game has no built-in uploader (it only reads subscribed items through
# SteamUGC), so this is the supported route. Run it from a normal terminal: the
# first run asks for the Steam password and Steam Guard interactively, and
# steamcmd caches the session afterwards.
#
#   powershell -ExecutionPolicy Bypass -File scripts/upload-workshop.ps1 -Account <steam account>
#
# First upload creates the item (publishedfileid 0) and prints the new id; pass
# it back with -PublishedFileId on later runs, or let the script remember it in
# work/workshop/publishedfileid.txt.
param(
    [Parameter(Mandatory = $true)][string]$Account,
    [string]$PublishedFileId = '',
    [switch]$Public,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = 'B:\slay-the-spire-2-mod-mod'
$steamcmd = Join-Path $root 'work\steamcmd\steamcmd.exe'
$workshop = Join-Path $root 'work\workshop'
$vdf = Join-Path $workshop 'workshop_build_item.vdf'
$idFile = Join-Path $workshop 'publishedfileid.txt'

if (-not (Test-Path $steamcmd)) { throw "steamcmd not found: $steamcmd" }

# Rebuild the item folder from the current release package, so the upload can
# never ship a stale build.
& (Join-Path $root 'scripts\build-workshop-item.ps1')

# Remember the item id across runs: uploading with 0 twice would create a
# second Workshop entry instead of updating the first.
if (-not $PublishedFileId -and (Test-Path $idFile)) {
    $PublishedFileId = (Get-Content $idFile -Raw).Trim()
}
$current = (Get-Content $vdf -Raw -Encoding UTF8)

if (-not $PublishedFileId) {
    $match = [regex]::Match($current, '"publishedfileid"\s+"(\d+)"')
    if ($match.Success -and $match.Groups[1].Value -ne '0') { $PublishedFileId = $match.Groups[1].Value }
}
if ($PublishedFileId) {
    $current = [regex]::Replace($current, '"publishedfileid"\s+"\d+"', '"publishedfileid"		"' + $PublishedFileId + '"')
    Write-Output "updating existing Workshop item $PublishedFileId"
} else {
    Write-Output "no item id yet: this run will CREATE a new Workshop item"
}
if ($Public) {
    $current = [regex]::Replace($current, '"visibility"\s+"\d+"', '"visibility"		"0"')
    Write-Output "visibility: public"
} else {
    Write-Output "visibility: private (change it on the Workshop page, or pass -Public)"
}
[System.IO.File]::WriteAllText($vdf, $current, (New-Object System.Text.UTF8Encoding $false))

$arguments = @('+login', $Account, '+workshop_build_item', "`"$vdf`"", '+quit')
if ($DryRun) {
    Write-Output ("dry run, would execute: `"{0}`" {1}" -f $steamcmd, ($arguments -join ' '))
    return
}

$output = & $steamcmd @arguments 2>&1
$output | ForEach-Object { Write-Output $_ }

# steamcmd reports the item it created or updated; keep it for the next run.
$newId = [regex]::Match(($output -join "`n"), 'PublishedFileId[^0-9]*(\d{6,})')
if ($newId.Success) {
    [System.IO.File]::WriteAllText($idFile, $newId.Groups[1].Value)
    Write-Output "item id $($newId.Groups[1].Value) saved to $idFile"
}
