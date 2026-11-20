# Regenerates assets\AutoMonitorBrightness.ico (a sun on a night-blue tile) at every size Windows asks for.
# Run with Windows PowerShell:  powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\assets\AutoMonitorBrightness.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded tile
    $r = [Math]::Max(3.0, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 0.5
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), `
        ([System.Drawing.Color]::FromArgb(255, 47, 72, 128)), ([System.Drawing.Color]::FromArgb(255, 20, 28, 56))
    $g.FillPath($bg, $path)

    $c = $s / 2.0
    $sun = [System.Drawing.Color]::FromArgb(255, 255, 196, 46)

    # rays
    $pen = New-Object System.Drawing.Pen $sun, ([Math]::Max(1.3, $s * 0.075))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $r1 = $s * 0.30; $r2 = $s * 0.40
    for ($i = 0; $i -lt 8; $i++) {
        $a = $i * [Math]::PI / 4
        $g.DrawLine($pen, [float]($c + $r1 * [Math]::Cos($a)), [float]($c + $r1 * [Math]::Sin($a)),
                          [float]($c + $r2 * [Math]::Cos($a)), [float]($c + $r2 * [Math]::Sin($a)))
    }

    # disc with a warm centre
    $d = $s * 0.40
    $disc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disc.AddEllipse([float]($c - $d / 2), [float]($c - $d / 2), [float]$d, [float]$d)
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $disc
    $pg.CenterColor = [System.Drawing.Color]::FromArgb(255, 255, 236, 150)
    $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 255, 170, 20))
    $g.FillPath($pg, $disc)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # Large frame as PNG (standard for 256 px).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small frames as classic 32-bit DIBs: every API (tray, Explorer, .NET) reads these.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($s * $s * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $s, $s), 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
        $row = New-Object byte[] ($s * 4)
        for ($y = $s - 1; $y -ge 0; $y--) {
            [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $s * 4)
            $bw.Write($row)
        }
        $bmp.UnlockBits($data)
        $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskStride * $s)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()
}

$frames = foreach ($s in $sizes) { , (New-Frame $s) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close()
Write-Host "Wrote $out"

# Also a 256 px PNG for the README.
$png = Join-Path $PSScriptRoot '..\assets\icon.png'
[System.IO.File]::WriteAllBytes($png, $frames[-1])
