# Builds every HangAll brand asset from one 1024-unit vector definition.
# The artwork is drawn directly with System.Drawing so packaging does not depend
# on an external SVG renderer.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\HangAll\Assets'
$logoDir = Join-Path $assets 'Logo'
$appIconDir = Join-Path $assets 'AppIcon'
$design = Join-Path $root 'design'
$designSources = Join-Path $design 'sources'
$docs = Join-Path $root 'docs'
$installer = Join-Path $root 'installer'
New-Item -ItemType Directory -Force $logoDir, $appIconDir, $design, $designSources, $docs, $installer | Out-Null

$ink = [System.Drawing.Color]::FromArgb(255, 20, 38, 58)
$cord = [System.Drawing.Color]::FromArgb(255, 162, 86, 34)
$clip = [System.Drawing.Color]::FromArgb(255, 184, 139, 84)
$clipLight = [System.Drawing.Color]::FromArgb(255, 213, 159, 99)
$paper = [System.Drawing.Color]::FromArgb(255, 239, 236, 221)
$photo = [System.Drawing.Color]::FromArgb(255, 117, 150, 191)
$highlight = [System.Drawing.Color]::FromArgb(255, 237, 235, 223)
$slate = [System.Drawing.Color]::FromArgb(255, 107, 114, 128)

function NewArgb([int]$w, [int]$h) {
    return New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function NewGraphics([System.Drawing.Bitmap]$bitmap, [bool]$transparent = $true) {
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    if ($transparent) { $g.Clear([System.Drawing.Color]::Transparent) }
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return $g
}

function RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function LeftClipPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddLine(286, 222.3, 371, 222.3)
    $path.AddBezier(371, 222.3, 386, 222.3, 399, 235.5, 399, 252.5)
    $path.AddLine(399, 252.5, 387, 527.5)
    $path.AddBezier(387, 527.5, 386.5, 540.5, 375, 547.5, 359, 547.5)
    $path.AddLine(359, 547.5, 300, 547.5)
    $path.AddBezier(300, 547.5, 285, 547.5, 277, 539.5, 276.5, 526.5)
    $path.AddLine(276.5, 526.5, 258.2, 252.5)
    $path.AddBezier(258.2, 252.5, 258.1, 235.5, 271, 222.3, 286, 222.3)
    $path.CloseFigure()
    return $path
}

function LeftClipLightPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddLine(286, 222.3, 328.5, 222.3)
    $path.AddLine(328.5, 222.3, 328.5, 547.5)
    $path.AddLine(328.5, 547.5, 300, 547.5)
    $path.AddBezier(300, 547.5, 285, 547.5, 277, 539.5, 276.5, 526.5)
    $path.AddLine(276.5, 526.5, 258.2, 252.5)
    $path.AddBezier(258.2, 252.5, 258.1, 235.5, 271, 222.3, 286, 222.3)
    $path.CloseFigure()
    return $path
}

function RightClipPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddLine(675, 257, 763, 257)
    $path.AddBezier(763, 257, 778, 257, 789, 271, 789, 287)
    $path.AddLine(789, 287, 778, 559.5)
    $path.AddBezier(778, 559.5, 777, 572.5, 767, 582.5, 751, 582.5)
    $path.AddLine(751, 582.5, 694, 582.5)
    $path.AddBezier(694, 582.5, 680, 582.5, 669, 572.5, 668, 559.5)
    $path.AddLine(668, 559.5, 650.8, 287)
    $path.AddBezier(650.8, 287, 650.7, 271, 660, 257, 675, 257)
    $path.CloseFigure()
    return $path
}

function RightClipLightPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddLine(675, 257, 720.5, 257)
    $path.AddLine(720.5, 257, 720.5, 582.5)
    $path.AddLine(720.5, 582.5, 694, 582.5)
    $path.AddBezier(694, 582.5, 680, 582.5, 669, 572.5, 668, 559.5)
    $path.AddLine(668, 559.5, 650.8, 287)
    $path.AddBezier(650.8, 287, 650.7, 271, 660, 257, 675, 257)
    $path.CloseFigure()
    return $path
}

function FillPath([System.Drawing.Graphics]$g, [System.Drawing.Drawing2D.GraphicsPath]$path, [System.Drawing.Color]$color) {
    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillPath($brush, $path)
    $brush.Dispose()
}

# The authoritative mark. All coordinates come from design\sources\mark-1024.svg.
function DrawRopeClipMark([System.Drawing.Graphics]$g, [single]$scale, [single]$ox = 0, [single]$oy = 0) {
    $state = $g.Save()
    $matrix = New-Object System.Drawing.Drawing2D.Matrix $scale, 0, 0, $scale, $ox, $oy
    $g.Transform = $matrix

    $ropePen = New-Object System.Drawing.Pen $cord, 78
    $ropePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ropePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawBezier(
        $ropePen,
        [System.Drawing.PointF]::new(79, 272.4),
        [System.Drawing.PointF]::new(337.09, 508.34),
        [System.Drawing.PointF]::new(769.52, 450.97),
        [System.Drawing.PointF]::new(945, 269.2))
    $ropePen.Dispose()

    $rearOuter = RoundedPath 119.5 424 447.5 351.5 77
    $rearInner = RoundedPath 168 473.5 350 253 36
    $frontOuter = RoundedPath 505 503 430 336 77
    $frontInner = RoundedPath 552 552 335 239 47
    FillPath $g $rearOuter $ink
    FillPath $g $rearInner $paper
    FillPath $g $frontOuter $ink
    FillPath $g $frontInner $photo

    $left = LeftClipPath
    $leftLight = LeftClipLightPath
    $right = RightClipPath
    $rightLight = RightClipLightPath
    FillPath $g $left $clip
    FillPath $g $leftLight $clipLight
    FillPath $g $right $clip
    FillPath $g $rightLight $clipLight

    $leftHighlight = RoundedPath 308 322 41 127 20.5
    $rightHighlight = RoundedPath 700 358 40 126 20
    FillPath $g $leftHighlight $highlight
    FillPath $g $rightHighlight $highlight

    $rearOuter.Dispose(); $rearInner.Dispose(); $frontOuter.Dispose(); $frontInner.Dispose()
    $left.Dispose(); $leftLight.Dispose(); $right.Dispose(); $rightLight.Dispose()
    $leftHighlight.Dispose(); $rightHighlight.Dispose(); $matrix.Dispose()
    $g.Restore($state)
}

function DrawMark([System.Drawing.Bitmap]$bitmap) {
    $g = NewGraphics $bitmap $true
    DrawRopeClipMark $g ($bitmap.Width / 1024.0)
    $g.Dispose()
}

function DrawAppIcon([System.Drawing.Bitmap]$bitmap) {
    $g = NewGraphics $bitmap $true
    $size = [single]$bitmap.Width
    $inset = [single]($size * 20.0 / 1024.0)
    $tile = RoundedPath $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) ([single]($size * 0.211))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPath($white, $tile)
    $g.SetClip($tile)
    DrawRopeClipMark $g ($size / 1024.0)
    $white.Dispose(); $tile.Dispose(); $g.Dispose()
}

function SavePng([System.Drawing.Bitmap]$bitmap, [string]$path) {
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function LockupArt([System.Drawing.Bitmap]$mark, [int]$w, [int]$h, [bool]$dark, [System.Drawing.FontFamily]$family) {
    $bmp = NewArgb $w $h
    $g = NewGraphics $bmp $true
    $g.DrawImage($mark, (New-Object System.Drawing.Rectangle 72, 44, 512, 512))
    $font = New-Object System.Drawing.Font $family, 250, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $colour = if ($dark) { [System.Drawing.Color]::White } else { $ink }
    $brush = New-Object System.Drawing.SolidBrush $colour
    $g.DrawString('HangAll', $font, $brush, 575, 105)
    $brush.Dispose(); $font.Dispose(); $g.Dispose()
    return $bmp
}

function DrawHero([System.Drawing.Bitmap]$mark, [System.Drawing.Bitmap]$lockup, [bool]$dark) {
    $bmp = NewArgb 2400 1280
    $g = NewGraphics $bmp $false
    $bg = if ($dark) { [System.Drawing.Color]::FromArgb(255, 15, 24, 37) } else { [System.Drawing.Color]::FromArgb(255, 247, 244, 238) }
    $g.Clear($bg)
    $g.DrawImage($lockup, (New-Object System.Drawing.Rectangle 110, 74, 1580, 395))
    $subColor = if ($dark) { [System.Drawing.Color]::FromArgb(255, 189, 201, 215) } else { [System.Drawing.Color]::FromArgb(255, 92, 105, 119) }
    $subFont = New-Object System.Drawing.Font 'Segoe UI', 52, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $subBrush = New-Object System.Drawing.SolidBrush $subColor
    $g.DrawString('Everything, hung within reach.', $subFont, $subBrush, 122, 510)
    $g.DrawImage($mark, (New-Object System.Drawing.Rectangle 1480, 180, 800, 800))
    $subBrush.Dispose(); $subFont.Dispose(); $g.Dispose()
    return $bmp
}

function Canvas24([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = NewGraphics $bmp $false
    $g.Clear([System.Drawing.Color]::White)
    return @($bmp, $g)
}

function WriteText([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

$svgGeometry = @'
  <path d="M 79 272.4 C 337.09 508.34 769.52 450.97 945 269.2"
        fill="none" stroke="#A25622" stroke-width="78" stroke-linecap="round"/>
  <rect x="119.5" y="424" width="447.5" height="351.5" rx="77" fill="#14263A"/>
  <rect x="168" y="473.5" width="350" height="253" rx="36" fill="#EFECDD"/>
  <rect x="505" y="503" width="430" height="336" rx="77" fill="#14263A"/>
  <rect x="552" y="552" width="335" height="239" rx="47" fill="#7596BF"/>
  <path d="M 286 222.3 H 371 C 386 222.3 399 235.5 399 252.5 L 387 527.5
           C 386.5 540.5 375 547.5 359 547.5 H 300 C 285 547.5 277 539.5 276.5 526.5
           L 258.2 252.5 C 258.1 235.5 271 222.3 286 222.3 Z" fill="#B88B54"/>
  <path d="M 286 222.3 H 328.5 V 547.5 H 300 C 285 547.5 277 539.5 276.5 526.5
           L 258.2 252.5 C 258.1 235.5 271 222.3 286 222.3 Z" fill="#D59F63"/>
  <rect x="308" y="322" width="41" height="127" rx="20.5" fill="#EDEBDF"/>
  <path d="M 675 257 H 763 C 778 257 789 271 789 287 L 778 559.5
           C 777 572.5 767 582.5 751 582.5 H 694 C 680 582.5 669 572.5 668 559.5
           L 650.8 287 C 650.7 271 660 257 675 257 Z" fill="#B88B54"/>
  <path d="M 675 257 H 720.5 V 582.5 H 694 C 680 582.5 669 572.5 668 559.5
           L 650.8 287 C 650.7 271 660 257 675 257 Z" fill="#D59F63"/>
  <rect x="700" y="358" width="40" height="126" rx="20" fill="#EDEBDF"/>
'@

function MarkSvg([string]$transform = '', [string]$width = '1024', [string]$height = '1024') {
    $open = if ($transform) { "  <g transform=`"$transform`">" } else { '  <g>' }
    return @"
<svg xmlns="http://www.w3.org/2000/svg" width="$width" height="$height" viewBox="0 0 $width $height">
$open
$svgGeometry
  </g>
</svg>
"@
}

WriteText (Join-Path $designSources 'mark-1024.svg') (MarkSvg)
$smallSvg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">
  <g transform="scale(0.0625)">
$svgGeometry
  </g>
</svg>
"@
WriteText (Join-Path $designSources 'mark-small-64.svg') $smallSvg
$appSvg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">
  <rect x="20" y="20" width="984" height="984" rx="216" fill="#FFFFFF"/>
  <g>
$svgGeometry
  </g>
</svg>
"@
WriteText (Join-Path $designSources 'app-icon-1024.svg') $appSvg
$lockupSvg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="2400" height="600" viewBox="0 0 2400 600">
  <g transform="translate(72 44) scale(0.5)">
$svgGeometry
  </g>
  <text x="575" y="352" fill="#14263A" font-family="Manrope, Segoe UI, sans-serif" font-size="250" font-weight="800">HangAll</text>
</svg>
"@
WriteText (Join-Path $designSources 'lockup-2400.svg') $lockupSvg
WriteText (Join-Path $designSources 'lockup-dark-2400.svg') ($lockupSvg.Replace('#14263A">HangAll', '#FFFFFF">HangAll'))

# Render a complete bare-mark and application-icon PNG family.
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512, 1024
foreach ($size in $sizes) {
    $logo = NewArgb $size $size
    DrawMark $logo
    SavePng $logo (Join-Path $logoDir ("hangall-logo-{0}.png" -f $size))
    if ($size -eq 64) { SavePng $logo (Join-Path $design 'mark-small-64.png') }
    if ($size -eq 1024) {
        SavePng $logo (Join-Path $assets 'Mark.png')
        SavePng $logo (Join-Path $design 'mark-1024.png')
    }
    $logo.Dispose()

    $appIcon = NewArgb $size $size
    DrawAppIcon $appIcon
    SavePng $appIcon (Join-Path $appIconDir ("hangall-icon-{0}.png" -f $size))
    if ($size -eq 256) { SavePng $appIcon (Join-Path $docs 'icon.png') }
    if ($size -eq 512) { SavePng $appIcon (Join-Path $docs 'logo-512.png') }
    if ($size -eq 1024) { SavePng $appIcon (Join-Path $design 'app-icon-1024.png') }
    $appIcon.Dispose()
}

$mark = NewArgb 1024 1024
DrawMark $mark
$fonts = New-Object System.Drawing.Text.PrivateFontCollection
$fonts.AddFontFile((Join-Path $assets 'Fonts\Manrope-ExtraBold.ttf'))
$family = $fonts.Families[0]
$lockup = LockupArt $mark 2400 600 $false $family
$lockupDark = LockupArt $mark 2400 600 $true $family
SavePng $lockup (Join-Path $assets 'Lockup.png')
SavePng $lockupDark (Join-Path $assets 'LockupDark.png')
SavePng $lockup (Join-Path $design 'lockup-2400.png')
SavePng $lockupDark (Join-Path $design 'lockup-dark-2400.png')

$social = NewArgb 1280 640
$sg = NewGraphics $social $false
$sg.Clear([System.Drawing.Color]::FromArgb(255, 247, 244, 238))
$sg.DrawImage($lockup, (New-Object System.Drawing.Rectangle 70, 130, 1140, 285))
$socialFont = New-Object System.Drawing.Font 'Segoe UI', 28, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$socialBrush = New-Object System.Drawing.SolidBrush $slate
$socialFormat = New-Object System.Drawing.StringFormat
$socialFormat.Alignment = [System.Drawing.StringAlignment]::Center
$sg.DrawString('Everything, hung within reach.', $socialFont, $socialBrush, (New-Object System.Drawing.RectangleF(0, 435, 1280, 64)), $socialFormat)
$socialBrush.Dispose(); $socialFont.Dispose(); $socialFormat.Dispose(); $sg.Dispose()
SavePng $social (Join-Path $docs 'social.png')

$heroLight = DrawHero $mark $lockup $false
$heroDark = DrawHero $mark $lockupDark $true
SavePng $heroLight (Join-Path $docs 'hero-light.png')
SavePng $heroDark (Join-Path $docs 'hero-dark.png')

# Multi-resolution Windows ICO. 512 and 1024 PNGs remain available separately.
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = @()
foreach ($size in $icoSizes) {
    $bitmap = NewArgb $size $size
    DrawAppIcon $bitmap
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@{ size = $size; bytes = $stream.ToArray() }
    $stream.Dispose(); $bitmap.Dispose()
}

$icoOut = Join-Path $assets 'HangAll.ico'
$file = [System.IO.File]::Create($icoOut)
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.size -ge 256) { 0 } else { $image.size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$image.bytes.Length); $writer.Write([uint32]$offset)
    $offset += $image.bytes.Length
}
foreach ($image in $images) { $writer.Write($image.bytes) }
$writer.Flush(); $writer.Dispose(); $file.Dispose()

# Inno Setup artwork.
$side = Canvas24 164 314
$g = $side[1]
$g.DrawImage($mark, (New-Object System.Drawing.Rectangle 32, 34, 100, 100))
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$nameFont = New-Object System.Drawing.Font $family, 18, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Point)
$nameBrush = New-Object System.Drawing.SolidBrush $ink
$descFont = New-Object System.Drawing.Font 'Segoe UI', 8.5
$descBrush = New-Object System.Drawing.SolidBrush $slate
$g.DrawString('HangAll', $nameFont, $nameBrush, (New-Object System.Drawing.RectangleF(0, 150, 164, 36)), $format)
$g.DrawString("Everything,`nhung within reach.", $descFont, $descBrush, (New-Object System.Drawing.RectangleF(0, 190, 164, 46)), $format)
$nameFont.Dispose(); $nameBrush.Dispose(); $descFont.Dispose(); $descBrush.Dispose(); $format.Dispose(); $g.Dispose()
$side[0].Save((Join-Path $installer 'wizard.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $side[0].Dispose()

$small = Canvas24 55 58
$g = $small[1]
$g.DrawImage($mark, (New-Object System.Drawing.Rectangle 4, 5, 47, 47))
$g.Dispose(); $small[0].Save((Join-Path $installer 'wizard-small.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $small[0].Dispose()

$mark.Dispose(); $lockup.Dispose(); $lockupDark.Dispose(); $social.Dispose()
$heroLight.Dispose(); $heroDark.Dispose(); $fonts.Dispose()
Write-Host "Built HangAll brand assets: 12 logo PNG sizes, 12 app-icon PNG sizes, ICO, installer and documentation artwork."
