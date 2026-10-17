param()

# Format/size packaging only. Character artwork is kept intact, with alpha.
# Windows PowerShell includes WPF; use -STA when invoking on another host.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\RinkoCamera\Assets'
$sourcePath = Join-Path $assetPath 'tsuki-icon.png'
$source = [System.Windows.Media.Imaging.BitmapFrame]::Create(
    [Uri]$sourcePath, [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
    [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)

function Get-PngBytes([int]$size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $scale = [Math]::Min($size / $source.PixelWidth, $size / $source.PixelHeight)
        $width = $source.PixelWidth * $scale
        $height = $source.PixelHeight * $scale
        $rect = [System.Windows.Rect]::new(($size - $width) / 2, ($size - $height) / 2, $width, $height)
        [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
        $drawing.DrawImage($source, $rect)
    } finally { $drawing.Close() }
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $png = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $png.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $png.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @($sizes | ForEach-Object { ,(Get-PngBytes $_) })
$iconPath = Join-Path $assetPath 'tsuki.ico'
$file = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $assetPath 'installer-logo.png'), (Get-PngBytes 64))
Write-Host "Created multi-resolution ICO and installer logo: $assetPath"
