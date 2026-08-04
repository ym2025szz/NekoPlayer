param(
    [string]$SourcePng
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assetsDirectory = Join-Path $root 'src\NekoPlayer.App\Assets'
$pngTarget = Join-Path $assetsDirectory 'AppIcon.png'
$icoTarget = Join-Path $assetsDirectory 'NekoPlayer.ico'

if ([string]::IsNullOrWhiteSpace($SourcePng)) {
    $SourcePng = Join-Path $root '图标.png'
}

try {
    $SourcePng = [IO.Path]::GetFullPath($SourcePng)
    if (-not (Test-Path -LiteralPath $SourcePng)) { throw "Icon source PNG does not exist: $SourcePng" }
    if ([IO.Path]::GetExtension($SourcePng) -ne '.png') { throw 'Icon source must be a PNG file.' }

    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
    Copy-Item -LiteralPath $SourcePng -Destination $pngTarget -Force

    $source = [Drawing.Image]::FromFile($SourcePng)
    try {
        if ($source.Width -lt 256 -or $source.Height -lt 256) {
            throw "Icon source is too small. Minimum size is 256x256; actual size is $($source.Width)x$($source.Height)."
        }

        $sizes = @(16, 24, 32, 48, 64, 128, 256)
        $frames = @()
        foreach ($size in $sizes) {
            $bitmap = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.Clear([Drawing.Color]::Transparent)
                    $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceOver
                    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.DrawImage($source, 0, 0, $size, $size)
                } finally {
                    $graphics.Dispose()
                }

                $memory = New-Object IO.MemoryStream
                try {
                    $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
                    $frames += ,$memory.ToArray()
                } finally {
                    $memory.Dispose()
                }
            } finally {
                $bitmap.Dispose()
            }
        }

        $stream = [IO.File]::Open($icoTarget, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $writer = New-Object IO.BinaryWriter($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$sizes.Count)
            $offset = 6 + (16 * $sizes.Count)
            for ($index = 0; $index -lt $sizes.Count; $index++) {
                $size = $sizes[$index]
                $frame = $frames[$index]
                $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$frame.Length)
                $writer.Write([uint32]$offset)
                $offset += $frame.Length
            }
            foreach ($frame in $frames) { $writer.Write($frame) }
        } finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    } finally {
        $source.Dispose()
    }

    Write-Host "Source PNG: $SourcePng" -ForegroundColor Cyan
    Write-Host "Avalonia PNG: $pngTarget" -ForegroundColor Green
    Write-Host "Windows ICO: $icoTarget" -ForegroundColor Green
    Write-Host "ICO sizes: 16, 24, 32, 48, 64, 128, 256" -ForegroundColor Green
    exit 0
} catch {
    Write-Error $_
    exit 1
}
