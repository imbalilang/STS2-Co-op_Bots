# Builds the Steam Workshop preview image from the repository cover.
#
# Steam rejects a preview larger than 1 MB, and cover.png is a photographic
# 1672x941 PNG that exceeds that even after scaling, so the preview is written
# as a JPEG at 1280x720 (around 300-500 KB). The cover stays the single source
# of truth: replace cover.png and re-run this script, nothing else to change.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = 'B:\slay-the-spire-2-mod-mod'
$source = Join-Path $root 'cover.png'
$out = Join-Path $root 'work\workshop\preview.jpg'

if (-not (Test-Path $source)) { throw "cover not found: $source" }

$image = [System.Drawing.Image]::FromFile($source)
try
{
    $width = 1280
    $height = [int][Math]::Round($image.Height * ($width / $image.Width))
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try
    {
        $graphics.InterpolationMode = 'HighQualityBicubic'
        $graphics.DrawImage($image, 0, 0, $width, $height)
    }
    finally { $graphics.Dispose() }

    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object { $_.MimeType -eq 'image/jpeg' }
    $parameters = New-Object System.Drawing.Imaging.EncoderParameters 1
    $parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
        [System.Drawing.Imaging.Encoder]::Quality, 88L)
    $bitmap.Save($out, $codec, $parameters)
    $bitmap.Dispose()
}
finally { $image.Dispose() }

# An older PNG in the same folder would shadow the JPEG if a stale VDF still
# pointed at it, so it is removed rather than left to confuse a later run.
$stalePng = Join-Path $root 'work\workshop\preview.png'
if (Test-Path $stalePng) { Remove-Item $stalePng -Force }

$size = (Get-Item $out).Length
if ($size -gt 1000000) { throw "preview exceeds Steam's 1 MB limit: $size bytes" }
Write-Output ("wrote {0} ({1:N0} bytes)" -f $out, $size)
