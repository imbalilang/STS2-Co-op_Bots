# Generates the Workshop preview image. Steam requires a preview file
# (jpg/png, at most 1 MB); this is a placeholder that can be replaced with real
# artwork at any time without re-uploading the content.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = 'B:\slay-the-spire-2-mod-mod\work\workshop\preview.png'
$width = 1280; $height = 720
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.TextRenderingHint = 'AntiAliasGridFit'

# Background: the game's dark slate, with a warm accent bar.
$graphics.Clear([System.Drawing.Color]::FromArgb(24, 26, 33))
$accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(214, 168, 74))
$graphics.FillRectangle($accent, 80, 150, 12, 240)

$titleFont = New-Object System.Drawing.Font 'Segoe UI', 76, ([System.Drawing.FontStyle]::Bold)
$subFont = New-Object System.Drawing.Font 'Microsoft YaHei', 40
$smallFont = New-Object System.Drawing.Font 'Segoe UI', 26

$title = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 238, 232))
$muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(176, 180, 190))

$graphics.DrawString('Co-op Bots', $titleFont, $title, 130, 150)
$graphics.DrawString('联机机器人', $subFont, $accent, 136, 268)
$graphics.DrawString('AI teammates for real multiplayer lobbies', $smallFont, $muted, 134, 350)
$graphics.DrawString('Dumb / Normal / Smart / Genius', $smallFont, $muted, 134, 396)

$bitmap.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()
Write-Output ("wrote {0} ({1:N0} bytes)" -f $out, (Get-Item $out).Length)
