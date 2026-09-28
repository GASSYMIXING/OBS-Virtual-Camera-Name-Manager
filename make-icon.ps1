$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$resourceDir = Join-Path $root 'resources'
New-Item -ItemType Directory -Path $resourceDir -Force | Out-Null
$iconPath = Join-Path $resourceDir 'app.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [single]$size
    $r = [single]($s * 0.19)
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0, 0, 2*$r, 2*$r, 180, 90)
    $bg.AddArc($s-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $bg.AddArc($s-2*$r, $s-2*$r, 2*$r, 2*$r, 0, 90)
    $bg.AddArc(0, $s-2*$r, 2*$r, 2*$r, 90, 90)
    $bg.CloseFigure()
    $navy = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 27, 48, 69))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 246, 250, 252))
    $cyan = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 67, 201, 196))
    $g.FillPath($navy, $bg)
    $g.FillRectangle($white, [single]($s*.22), [single]($s*.35), [single]($s*.43), [single]($s*.31))
    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([single]($s*.66), [single]($s*.43))),
        (New-Object System.Drawing.PointF([single]($s*.81), [single]($s*.34))),
        (New-Object System.Drawing.PointF([single]($s*.81), [single]($s*.67))),
        (New-Object System.Drawing.PointF([single]($s*.66), [single]($s*.58)))
    )
    $g.FillPolygon($cyan, $points)
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@($size, $stream.ToArray())
    $stream.Dispose(); $g.Dispose(); $bmp.Dispose(); $bg.Dispose(); $navy.Dispose(); $white.Dispose(); $cyan.Dispose()
}
$file = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $size = [int]$frame[0]; $bytes = [byte[]]$frame[1]
        $writer.Write([byte]($size % 256)); $writer.Write([byte]($size % 256))
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
} finally { $writer.Dispose() }
