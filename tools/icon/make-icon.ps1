# Generates src/LocalAppManager/Assets/AppIcon.ico (16–256 px, PNG-compressed entries).
# The mark: a rounded tile with a "play" triangle and three port dots — start servers, ports handled.
param([string]$Out = "$PSScriptRoot\..\..\src\LocalAppManager\Assets\AppIcon.ico")
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.06))
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $r = $rect.Width * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([System.Drawing.Color]::FromArgb(255, 124, 58, 237)), 45
    $g.FillPath($brush, $path)

    # Play triangle, slightly left of center.
    $s = $rect.Width
    $cx = $rect.X + $s * 0.44; $cy = $rect.Y + $s * 0.44
    $h = $s * 0.40
    $tri = @(
        (New-Object System.Drawing.PointF ($cx - $h * 0.36), ($cy - $h * 0.5)),
        (New-Object System.Drawing.PointF ($cx + $h * 0.52), $cy),
        (New-Object System.Drawing.PointF ($cx - $h * 0.36), ($cy + $h * 0.5))
    )
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($white, $tri)

    # Three "port" dots along the bottom; the last one lit green (running).
    if ($size -ge 20) {
        $d = [Math]::Max(2.0, $s * 0.1)
        $y = $rect.Bottom - $s * 0.2
        $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 74, 222, 128))
        $dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
        for ($i = 0; $i -lt 3; $i++) {
            $x = $rect.X + $s * 0.62 + $i * $d * 1.35 - $d * 1.35
            $b = if ($i -eq 2) { $green } else { $dim }
            $g.FillEllipse($b, $x, $y - $d / 2, $d, $d)
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($sz in $sizes) { ,(New-Frame $sz) }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $frames[$i]
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $w.Write($data) }
$w.Close()
"wrote $Out"
$preview = "$PSScriptRoot\preview.png"
[System.IO.File]::WriteAllBytes($preview, $frames[-1])
