# Draws the application icon and builds the Windows .ico from it. Windows only.
#
# The icon is the piece map, which is what the program is about and what its
# window is built around: a grid of pieces, most of them held, a couple still
# arriving and a couple still missing. Four cells across rather than the dozens
# the real map has, because at sixteen pixels anything finer turns to mush.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$resources = Join-Path $projectRoot 'src/Bitfield/Resources'
$sourcePath = Join-Path $resources 'app-icon.png'
$outputPath = Join-Path $resources 'app.ico'

New-Item -ItemType Directory -Force -Path $resources | Out-Null

# ----------------------------------------------------------------- the drawing

$canvas = 1024
$background = [System.Drawing.Color]::FromArgb(0x0E, 0x13, 0x1C)
$held = [System.Drawing.Color]::FromArgb(0x5B, 0x9C, 0xFF)
$busy = [System.Drawing.Color]::FromArgb(0x33, 0x52, 0x7E)
$missing = [System.Drawing.Color]::FromArgb(0x1E, 0x27, 0x36)
$edge = [System.Drawing.Color]::FromArgb(0x2C, 0x3A, 0x50)

# 2 held, 1 arriving, 0 missing. The gaps are deliberate: a solid block of blue
# would say nothing about what the program does.
$pattern = @(
    @(2, 2, 1, 2),
    @(2, 2, 2, 0),
    @(2, 1, 2, 2),
    @(0, 2, 2, 2)
)

function New-RoundedPath {
    param([System.Drawing.RectangleF] $bounds, [float] $radius)

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($bounds.X, $bounds.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($bounds.Right - $diameter, $bounds.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($bounds.X, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$bitmap = [System.Drawing.Bitmap]::new($canvas, $canvas, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

# The tile the cells sit on.
$tile = [System.Drawing.RectangleF]::new(0, 0, $canvas, $canvas)
$tilePath = New-RoundedPath -bounds $tile -radius 190
$tileBrush = [System.Drawing.SolidBrush]::new($background)
$graphics.FillPath($tileBrush, $tilePath)

$edgePen = [System.Drawing.Pen]::new($edge, 8)
$graphics.DrawPath($edgePen, $tilePath)

# The grid.
$inset = 150
$gap = 36
$span = $canvas - (2 * $inset)
$cell = ($span - (3 * $gap)) / 4

for ($row = 0; $row -lt 4; $row++) {
    for ($column = 0; $column -lt 4; $column++) {
        $state = $pattern[$row][$column]
        $colour = switch ($state) {
            2 { $held }
            1 { $busy }
            default { $missing }
        }

        $x = $inset + ($column * ($cell + $gap))
        $y = $inset + ($row * ($cell + $gap))
        $bounds = [System.Drawing.RectangleF]::new($x, $y, $cell, $cell)
        $path = New-RoundedPath -bounds $bounds -radius 30
        $brush = [System.Drawing.SolidBrush]::new($colour)

        $graphics.FillPath($brush, $path)

        $brush.Dispose()
        $path.Dispose()
    }
}

$bitmap.Save($sourcePath, [System.Drawing.Imaging.ImageFormat]::Png)

$edgePen.Dispose()
$tileBrush.Dispose()
$tilePath.Dispose()
$graphics.Dispose()

# ------------------------------------------------------------------- the .ico

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()

try {
    foreach ($size in $sizes) {
        $frame = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $frameGraphics = [System.Drawing.Graphics]::FromImage($frame)
        $stream = [IO.MemoryStream]::new()
        try {
            $frameGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $frameGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $frameGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $frameGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $frameGraphics.DrawImage($bitmap, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
            $frameGraphics.Dispose()
            $frame.Dispose()
        }
    }

    $file = [IO.File]::Create($outputPath)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }

        foreach ($frame in $frames) {
            $writer.Write($frame)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}
finally {
    $bitmap.Dispose()
}

Write-Host "wrote $sourcePath and $outputPath"
