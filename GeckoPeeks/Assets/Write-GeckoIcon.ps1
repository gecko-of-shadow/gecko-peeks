# Builds Assets/gecko-peeks.ico (16/32/48/256) from the original GeckoMark art for ApplicationIcon.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $here "gecko-peeks.ico"

function New-GeckoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, 52, 38))
    $body = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(236, 226, 206))
    $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(232, 168, 56))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(232, 168, 56), [Math]::Max(1.5, 2.0 * $s))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $g.FillEllipse($bg, $s, $s, 30 * $s, 30 * $s)
    $g.DrawBezier($pen, 9 * $s, 22 * $s, 2 * $s, 28 * $s, 3 * $s, 12 * $s, 12 * $s, 16 * $s)
    $g.FillEllipse($body, 10 * $s, 14 * $s, 14 * $s, 9 * $s)
    $g.FillEllipse($body, 16 * $s, 8 * $s, 10 * $s, 9 * $s)
    $g.FillEllipse($amber, 21.2 * $s, 10.6 * $s, 3.1 * $s, 3.1 * $s)
    $g.DrawLine($pen, 14 * $s, 22 * $s, 12 * $s, 26 * $s)
    $g.DrawLine($pen, 20 * $s, 22 * $s, 22 * $s, 26 * $s)

    $pen.Dispose(); $amber.Dispose(); $body.Dispose(); $bg.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

$sizes = @(16, 32, 48, 256)
$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-GeckoBitmap $size
    $pngs += ,(Get-PngBytes $bmp)
    $bmp.Dispose()
}

$count = $sizes.Length
$offset = 6 + (16 * $count)
$stream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $stream
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$count)

$cur = $offset
for ($i = 0; $i -lt $count; $i++) {
    $w = $sizes[$i]
    $h = $sizes[$i]
    $wb = if ($w -ge 256) { [byte]0 } else { [byte]$w }
    $hb = if ($h -ge 256) { [byte]0 } else { [byte]$h }
    $bw.Write($wb)
    $bw.Write($hb)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$cur)
    $cur += $pngs[$i].Length
}

foreach ($png in $pngs) {
    $bw.Write($png)
}

$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
$bw.Dispose()
$stream.Dispose()
Write-Host "Wrote $icoPath"
