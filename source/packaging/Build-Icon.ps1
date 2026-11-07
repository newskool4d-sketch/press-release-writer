param(
    [Parameter(Mandatory = $true)][string]$InputPng,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Desktop\Assets')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$inputPath = (Resolve-Path -LiteralPath $InputPng).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$sourceImage = [Drawing.Image]::FromFile($inputPath)
try {
    if ($sourceImage.Width -ne $sourceImage.Height) { throw 'The app icon must be square.' }
    $frames = @(foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $buffer = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, [Drawing.Rectangle]::new(0, 0, $size, $size))
            $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray() }
        }
        finally { $buffer.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    })
    $icoPath = Join-Path $outputPath 'press-release.ico'
    $stream = [IO.File]::Create($icoPath)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    }
    finally { $writer.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $outputPath 'press-release.png'), $frames[-1].Bytes)
    [pscustomobject]@{ Icon = $icoPath; Sizes = @($frames.Size); Source = $inputPath }
}
finally { $sourceImage.Dispose() }
