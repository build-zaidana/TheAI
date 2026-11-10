# Makes TheAI's icon (codexbar.ico, all sizes): the notch at the top of a dark screen, with a
# progress ring in it at the larger sizes (16-24 px show the notch alone, so it stays legible).
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"
$outPath = Join-Path (Split-Path -Parent $PSScriptRoot) "src\CodexBar.App\codexbar.ico"
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r, [bool]$topSquare) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    if ($topSquare) {
        $path.AddLine($x, $y, $x + $w, $y)
    } else {
        $path.AddArc($x, $y, $d, $d, 180, 90)
        $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    }
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # The screen: a rounded square, slate fading to near-black.
    $pad = [single][Math]::Max(0.5, $s * 0.04)
    $side = [single]($s - 2 * $pad)
    $screen = New-RoundedRect $pad $pad $side $side ([single]($s * 0.22)) $false
    # Small sizes sit on dark taskbars too: a lighter screen keeps the black notch readable there.
    $from = if ($s -le 24) { [System.Drawing.Color]::FromArgb(255, 158, 165, 182) } else { [System.Drawing.Color]::FromArgb(255, 104, 111, 132) }
    $to = if ($s -le 24) { [System.Drawing.Color]::FromArgb(255, 84, 89, 104) } else { [System.Drawing.Color]::FromArgb(255, 30, 32, 39) }
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), $from, $to
    $g.FillPath($fill, $screen)
    if ($s -le 32) {
        $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), ([single]1)
        $g.DrawPath($rim, $screen)
        $rim.Dispose()
    }

    # The notch: black, flush with the top of the screen, rounded at the bottom.
    $notchW = [single]($s * ($(if ($s -le 24) { 0.70 } else { 0.62 })))
    $notchH = [single]($s * ($(if ($s -le 24) { 0.34 } else { 0.27 })))
    $notchX = [single](($s - $notchW) / 2)
    $notch = New-RoundedRect $notchX $pad $notchW $notchH ([single]($notchH * 0.55)) $true
    $g.SetClip($screen)
    $g.FillPath([System.Drawing.Brushes]::Black, $notch)
    if ($s -ge 32) {
        $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([single][Math]::Max(1, $s / 128))
        $g.DrawPath($edge, $notch)
        $edge.Dispose()
    }
    $g.ResetClip()

    if ($s -ge 32) {
        # Inside: a progress ring and a short bar, what the notch shows while an agent works.
        $ring = [single]($notchH * 0.46)
        $cx = [single]($notchX + $notchH * 0.62)
        $cy = [single]($pad + $notchH * 0.48)
        $stroke = [single][Math]::Max(1.5, $s * 0.028)
        $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), $stroke
        $arc = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
        $arc.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arc.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawEllipse($track, $cx - $ring / 2, $cy - $ring / 2, $ring, $ring)
        $g.DrawArc($arc, $cx - $ring / 2, $cy - $ring / 2, $ring, $ring, -90, 250)
        $bar = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 255, 255, 255)), $stroke
        $bar.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $bar.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $barStart = [single]($cx + $ring * 0.95)
        $barEnd = [single]($notchX + $notchW - $notchH * 0.6)
        $g.DrawLine($bar, $barStart, $cy, $barEnd, $cy)
        $track.Dispose(); $arc.Dispose(); $bar.Dispose()
    }

    $g.Dispose(); $fill.Dispose(); $screen.Dispose(); $notch.Dispose()
    return $bmp
}

# The ICO: 256 px as PNG (what Windows expects for the large size), the rest as classic 32-bit
# bitmaps with alpha, which every reader (the tray, older dialogs, System.Drawing) understands.
function Get-BitmapEntry([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $maskStride = [int][Math]::Ceiling($s / 32) * 4
    $w.Write([UInt32]($s * $s * 4 + $maskStride * $s))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([Byte]$c.B); $w.Write([Byte]$c.G); $w.Write([Byte]$c.R); $w.Write([Byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskStride * $s)))
    $w.Flush()
    $bytes = $ms.ToArray()
    $w.Dispose(); $ms.Dispose()
    return , $bytes
}

$entries = @()
$preview = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\icon"
New-Item -ItemType Directory -Force $preview | Out-Null
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $bmp.Save((Join-Path $preview "theai-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries += , $ms.ToArray()
        $ms.Dispose()
    } else {
        $entries += , (Get-BitmapEntry $bmp)
    }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type = icon
$bw.Write([UInt16]$sizes.Count) # count
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bytes = $entries[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)       # width
    $bw.Write([Byte]$dim)       # height
    $bw.Write([Byte]0)          # palette
    $bw.Write([Byte]0)          # reserved
    $bw.Write([UInt16]1)        # planes
    $bw.Write([UInt16]32)       # bpp
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $entries) { $bw.Write($bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "Icon written: $outPath ($($sizes.Count) sizes; PNG previews in $preview)"
