# Membuat ikon aplikasi CodexBar (codexbar.ico) multi-resolusi.
# Ikon: kotak membulat biru aksen + panah "enter" putih (konsisten dgn tombol kirim).
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"
$outPath = Join-Path (Split-Path -Parent $PSScriptRoot) "src\CodexBar.App\codexbar.ico"
$sizes = 16, 24, 32, 48, 64, 128, 256
$accent = [System.Drawing.Color]::FromArgb(47, 123, 246)

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Kotak membulat biru
    $pad = [Math]::Max(1, [int]($s * 0.06))
    $r = [Math]::Max(2, [int]($s * 0.22))
    $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($s - 2*$pad), ($s - 2*$pad)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush $accent
    $g.FillPath($brush, $path)

    # Panah enter putih (return)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.0, $s * 0.075))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    function P([double]$fx, [double]$fy) { New-Object System.Drawing.PointF (($fx*$s), ($fy*$s)) }
    # Batang: turun lalu ke kiri
    $g.DrawLines($pen, @((P 0.66 0.30), (P 0.66 0.56), (P 0.36 0.56)))
    # Kepala panah
    $g.DrawLines($pen, @((P 0.46 0.45), (P 0.36 0.56), (P 0.46 0.67)))

    $g.Dispose(); $brush.Dispose(); $pen.Dispose(); $path.Dispose()
    return $bmp
}

# Susun file ICO (tiap entri PNG-encoded; didukung Windows Vista+).
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose(); $ms.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type = icon
$bw.Write([UInt16]$sizes.Count) # count
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bytes = $pngs[$i]
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
foreach ($bytes in $pngs) { $bw.Write($bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "Ikon dibuat: $outPath ($($sizes.Count) ukuran)"
