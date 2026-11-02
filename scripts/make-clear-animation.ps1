# Draws an original clear-run animation: a soft broom sweeping left, with dust.
# 72x72, transparent background, 16 frames -> PNG sequence for ffmpeg.
param([string]$OutDir = "$env:TEMP\clear-run-frames")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

$size = 72
$frames = 16
$wood = [System.Drawing.Color]::FromArgb(255, 150, 113, 77)
$woodHi = [System.Drawing.Color]::FromArgb(255, 182, 145, 106)
$bristle = [System.Drawing.Color]::FromArgb(255, 210, 167, 112)
$bristleLo = [System.Drawing.Color]::FromArgb(255, 158, 117, 72)
$band = [System.Drawing.Color]::FromArgb(255, 120, 86, 56)
$dust = [System.Drawing.Color]::FromArgb(255, 166, 156, 142)

function Brush($c) { New-Object System.Drawing.SolidBrush $c }
function Pen($c, $w) {
    $p = New-Object System.Drawing.Pen $c, $w
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    return $p
}

for ($i = 0; $i -lt $frames; $i++) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $t = $i / [double]$frames
    $phase = [Math]::Sin($t * 2 * [Math]::PI)
    $drift = 3.0 * $t
    $tilt = 8.0 * $phase

    # Dust to the left of the bristles, drifting away and fading.
    for ($d = 0; $d -lt 3; $d++) {
        $age = (($t + ($d * 0.3)) % 1.0)
        $alpha = [int](170 * (1.0 - $age))
        if ($alpha -le 6) { continue }
        $r = 1.5 + (3.2 * $age)
        $x = 20 - (18 * $age) - ($drift * 0.5)
        $y = 46 - (5 * $age) + ($d * 4)
        $g.FillEllipse((Brush ([System.Drawing.Color]::FromArgb($alpha, $dust.R, $dust.G, $dust.B))),
            [float]($x - $r), [float]($y - $r), [float]($r * 2), [float]($r * 2))
    }

    $state = $g.Save()
    # Build the broom upright, then swing it clockwise so the bristles lead left.
    $g.TranslateTransform([float](35 - $drift), [float]31)
    $g.RotateTransform([float](38 + $tilt))

    # Bristle block.
    $blk = New-Object System.Drawing.Drawing2D.GraphicsPath
    $blk.AddArc(-8, 0, 16, 8, 180, 180)
    $blk.AddArc(-8, 12, 16, 8, 0, 180)
    $blk.CloseFigure()
    $g.FillPath((Brush $bristle), $blk)

    # Bristle strokes.
    $groove = Pen $bristleLo 1.1
    foreach ($x in -5.5, -2.5, 0.5, 3.5) {
        $g.DrawLine($groove, [float]$x, 2, [float]$x, 17)
    }

    # Binding band.
    $g.FillRectangle((Brush $band), -9, -3, 18, 6)
    $g.FillRectangle((Brush $woodHi), -9, -2, 18, 1.8)

    # Handle.
    $g.DrawLine((Pen $wood 5.5), 0, -3, 0, -38)

    $g.Restore($state)
    $g.Dispose()

    $path = Join-Path $OutDir ("frame{0:00}.png" -f $i)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Write-Output "wrote $frames frames to $OutDir"
