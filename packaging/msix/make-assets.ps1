<#
  Regenerates the MSIX visual assets (tiles / store logo) from the OpenPin logo drawing.
  Usage:  powershell -ExecutionPolicy Bypass -File packaging/msix/make-assets.ps1
  Output: packaging/msix/Assets/*.png  (committed to the repo; re-run only when the logo changes)
#>
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function Draw-Logo([System.Drawing.Graphics]$g, [float]$x, [float]$y, [float]$size) {
    $state = $g.Save()
    $g.TranslateTransform($x, $y)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $r = 56; $bx = 8; $by = 8; $bw = 240; $bh = 240
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($bx, $by, $r, $r, 180, 90)
    $path.AddArc($bx + $bw - $r, $by, $r, $r, 270, 90)
    $path.AddArc($bx + $bw - $r, $by + $bh - $r, $r, $r, 0, 90)
    $path.AddArc($bx, $by + $bh - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(256, 256)),
        [System.Drawing.Color]::FromArgb(255, 64, 150, 255), [System.Drawing.Color]::FromArgb(255, 22, 96, 224))
    $g.FillPath($brush, $path)

    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 22)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $a = 64; $b = 192; $l = 50
    function P($px, $py) { New-Object System.Drawing.PointF($px, $py) }
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P $a ($a + $l)), (P $a $a), (P ($a + $l) $a)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P ($b - $l) $a), (P $b $a), (P $b ($a + $l))))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P $b ($b - $l)), (P $b $b), (P ($b - $l) $b)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P ($a + $l) $b), (P $a $b), (P $a ($b - $l))))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($white, 98, 98, 60, 60)
    $g.Restore($state)
}

function Save-Asset([string]$name, [int]$w, [int]$h, [float]$logoSize) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Logo $g (($w - $logoSize) / 2) (($h - $logoSize) / 2) $logoSize
    $g.Dispose()
    $bmp.Save((Join-Path $outDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "  $name ${w}x$h"
}

Save-Asset 'Square44x44Logo.png' 44 44 44
Save-Asset 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 24
Save-Asset 'Square44x44Logo.targetsize-32_altform-unplated.png' 32 32 32
Save-Asset 'Square44x44Logo.targetsize-48_altform-unplated.png' 48 48 48
Save-Asset 'Square44x44Logo.targetsize-256_altform-unplated.png' 256 256 256
Save-Asset 'Square150x150Logo.png' 150 150 110
Save-Asset 'Wide310x150Logo.png' 310 150 110
Save-Asset 'StoreLogo.png' 50 50 50
