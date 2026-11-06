# Generates app.ico (16/24/32/48/256, PNG-compressed entries): three colored faders on a dark tile.
param([string]$Out = (Join-Path $PSScriptRoot "..\app.ico"))
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [Math]::Max(2, $size * 0.22); $d = $r * 2; $w = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x2A, 0x2F, 0x37))), $path)

    $colors = @([System.Drawing.Color]::FromArgb(255, 0x1F, 0xCB, 0x9C), [System.Drawing.Color]::FromArgb(255, 0x3B, 0xA4, 0xF0), [System.Drawing.Color]::FromArgb(255, 0xF2, 0x46, 0x8E))
    $knobs = @(0.38, 0.62, 0.46)
    $trackW = [Math]::Max(1.5, $size * 0.08)
    $knobW = [Math]::Max(4, $size * 0.2); $knobH = [Math]::Max(2.5, $size * 0.11)
    for ($i = 0; $i -lt 3; $i++) {
        $x = $size * (0.27 + 0.23 * $i)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $colors[$i]), [float]($x - $trackW / 2), [float]($size * 0.18), [float]$trackW, [float]($size * 0.64))
        $y = $size * $knobs[$i]
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xF3, 0xF5, 0xF8))), [float]($x - $knobW / 2), [float]($y - $knobH / 2), [float]$knobW, [float]$knobH)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 256)
$frames = @(); foreach ($s in $sizes) { $frames += , (New-Frame $s) }

$buf = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($buf)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Out), $buf.ToArray())
Write-Output "Wrote $Out"

