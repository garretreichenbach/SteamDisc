<#
.SYNOPSIS
  Generates assets/SteamDisc.ico — a simple optical-disc mark with the accent ring.
  Run once (or after tweaking the design); the .ico is committed and used by every app.
#>
[CmdletBinding()]
param([string]$OutPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutPath) { $OutPath = Join-Path $repoRoot 'assets\SteamDisc.ico' }
New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null

function New-DiscPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = $size * 0.06
    $d = $size - (2 * $pad)

    $body = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 18, 20, 26))
    $g.FillEllipse($body, $pad, $pad, $d, $d)

    $accentW = [Math]::Max(2, $size * 0.085)
    $accentPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 102, 0), $accentW)
    $inset = $pad + ($accentW / 2)
    $g.DrawEllipse($accentPen, $inset, $inset, $size - (2 * $inset), $size - (2 * $inset))

    $hubD = $size * 0.34
    $hubXY = ($size - $hubD) / 2
    $hubPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 74, 82, 99), [Math]::Max(1, $size * 0.03))
    $g.DrawEllipse($hubPen, $hubXY, $hubXY, $hubD, $hubD)

    # Punch a transparent centre hole.
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $holeD = $size * 0.13
    $holeXY = ($size - $holeD) / 2
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 0, 0, 0))), $holeXY, $holeXY, $holeD, $holeD)

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = New-DiscPng $s }

$fs = [System.IO.File]::Open($OutPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)          # reserved
    $bw.Write([uint16]1)          # type: icon
    $bw.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    foreach ($s in $sizes) {
        $data = $pngs[$s]
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width  (0 => 256)
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height (0 => 256)
        $bw.Write([byte]0)        # palette
        $bw.Write([byte]0)        # reserved
        $bw.Write([uint16]1)      # planes
        $bw.Write([uint16]32)     # bpp
        $bw.Write([uint32]$data.Length)
        $bw.Write([uint32]$offset)
        $offset += $data.Length
    }
    foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
}
finally {
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

Write-Host "Wrote $OutPath ($([math]::Round((Get-Item $OutPath).Length/1KB,1)) KB, $($sizes.Count) sizes)" -ForegroundColor Green
