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
#
# Visibility is deliberately not settable here. The item is public and stays
# public unless the owner asks for a change, so this script never writes the
# `visibility` line -- it only reports the value the VDF already carries
# (RELEASING.md §2 asks for appid / publishedfileid / visibility to be validated
# before a release). To change it, edit work/workshop/workshop_build_item.vdf or
# use the Workshop page; a flag here would make it possible to drift by accident.
param(
    [Parameter(Mandatory = $true)][string]$Account,
    [string]$PublishedFileId = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
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

# A raw ASCII quote inside a KeyValues value ends the string there, and steamcmd
# silently drops the rest: the Workshop page once stopped at "构筑按" because the
# description used straight quotes around a phrase. Full-width quotes are safe.
$description = [regex]::Match($current, '"description"\s+"(.*?)"\s+"changenote"', 'Singleline').Groups[1].Value
$strayQuotes = [regex]::Matches($description, '"').Count
if ($strayQuotes -gt 0) {
    throw "The Workshop description contains $strayQuotes raw quote character(s); steamcmd would truncate the page at the first one. Use full-width quotes."
}

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
# Reported, never written. The previous version had a -Public switch that rewrote this
# line to "0" -- which is what the VDF already says, so the flag was a no-op while the
# message it printed in the default branch claimed "private". Both are gone: the value
# below is what steamcmd will actually apply.
$declared = [regex]::Match($current, '"visibility"\s+"(\d+)"')
if (-not $declared.Success) { throw "no visibility field in the Workshop VDF: $vdf" }
Write-Output "visibility: $($declared.Groups[1].Value) (from the VDF, left unchanged)"
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
