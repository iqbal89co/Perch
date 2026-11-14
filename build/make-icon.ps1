# Renders the Perch logo (same shapes as src/Perch/Assets/perch.svg) with GDI+ and writes
# perch.png (256 px) and perch.ico (16, 20, 24, 32, 40, 48, 64, 128, 256 px, PNG-encoded entries).
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\Perch\Assets"))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Render-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.InterpolationMode = "HighQualityBicubic"
    $g.PixelOffsetMode = "HighQuality"
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    $tile = New-RoundRect 8 8 240 240 58
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 8, 8), (New-Object System.Drawing.PointF 248, 248), (C "#4F46E5"), (C "#14B8A6")
    $g.FillPath($grad, $tile)

    $bar = New-RoundRect 44 166 168 40 20
    $g.FillPath((New-Object System.Drawing.SolidBrush (C "#0B0B14")), $bar)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C "#2DD4BF")), 184, 180, 12, 12)

    $white = New-Object System.Drawing.SolidBrush (C "#F8FAFC")
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (C "#E2E8F0")), [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 86, 150), (New-Object System.Drawing.PointF 48, 128), (New-Object System.Drawing.PointF 60, 158)))
    $g.FillEllipse($white, 72, 108, 92, 60)
    $g.FillEllipse($white, 134, 78, 52, 52)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (C "#FBBF24")), [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 182, 98), (New-Object System.Drawing.PointF 206, 106), (New-Object System.Drawing.PointF 182, 114)))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C "#0B0B14")), 161.5, 95.5, 9, 9)

    $wing = New-Object System.Drawing.Drawing2D.GraphicsPath
    $wing.AddBezier(96, 128, 113.3, 117.3, 130, 119.3, 146, 134)
    $wing.AddBezier(146, 134, 128.7, 144.7, 112, 142.7, 96, 128)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C "#CBD5E1")), $wing)

    $dark = New-Object System.Drawing.SolidBrush (C "#0B0B14")
    $g.FillPath($dark, (New-RoundRect 106 160 5 10 2))
    $g.FillPath($dark, (New-RoundRect 126 160 5 10 2))

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$big = Render-Logo 256
$big.Save((Join-Path $OutDir "perch.png"), [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (Get-PngBytes (Render-Logo $sz)) }

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "perch.ico"), $ico.ToArray())
"Wrote perch.png and perch.ico ($($sizes -join ', ') px) to $OutDir"
