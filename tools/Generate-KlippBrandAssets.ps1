param([string]$AssetsDirectory = (Join-Path $PSScriptRoot '../src/Klipp.Desktop/Assets'))

# The SVG polygons are the source artwork. Render every Windows asset from them,
# including all ICO sizes, so the taskbar, installer and capture popups agree.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$AssetsDirectory = [IO.Path]::GetFullPath($AssetsDirectory)
[xml]$taskArtwork = [IO.File]::ReadAllText((Join-Path $AssetsDirectory 'KlippMark.svg'))
$taskPolygons = @($taskArtwork.svg.polygon | ForEach-Object { $_.points })
$taskMarkColor = [Drawing.ColorTranslator]::FromHtml('#8FAFD2')
$taskTileColor = [Drawing.ColorTranslator]::FromHtml('#20262D')

function New-BrandBitmap([int]$Width, [int]$Height, [float]$MarkSize, [bool]$Tile) {
    $taskScale = 4
    $taskHiRes = [Drawing.Bitmap]::new($Width * $taskScale, $Height * $taskScale)
    $taskGraphics = [Drawing.Graphics]::FromImage($taskHiRes)
    $taskGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $taskGraphics.Clear([Drawing.Color]::Transparent)
    $taskGraphics.ScaleTransform($taskScale, $taskScale)
    $taskGraphics.TranslateTransform(($Width - $MarkSize) / 2, ($Height - $MarkSize) / 2)
    $taskGraphics.ScaleTransform($MarkSize / 24, $MarkSize / 24)
    $taskBrush = [Drawing.SolidBrush]::new($taskMarkColor)
    try {
        if ($Tile) {
            $taskTileBrush = [Drawing.SolidBrush]::new($taskTileColor)
            $taskPath = [Drawing.Drawing2D.GraphicsPath]::new()
            try {
                $taskPath.AddArc(0, 0, 10, 10, 180, 90)
                $taskPath.AddArc(14, 0, 10, 10, 270, 90)
                $taskPath.AddArc(14, 14, 10, 10, 0, 90)
                $taskPath.AddArc(0, 14, 10, 10, 90, 90)
                $taskPath.CloseFigure()
                $taskGraphics.FillPath($taskTileBrush, $taskPath)
            } finally { $taskTileBrush.Dispose(); $taskPath.Dispose() }
        }
        foreach ($taskPolygon in $taskPolygons) {
            [Drawing.PointF[]]$taskPoints = @($taskPolygon.Split(' ') | ForEach-Object {
                $taskXY = $_.Split(',')
                [Drawing.PointF]::new([float]$taskXY[0], [float]$taskXY[1])
            })
            $taskGraphics.FillPolygon($taskBrush, $taskPoints)
        }
    } finally { $taskBrush.Dispose(); $taskGraphics.Dispose() }
    $taskBitmap = [Drawing.Bitmap]::new($Width, $Height)
    $taskDownsample = [Drawing.Graphics]::FromImage($taskBitmap)
    try {
        $taskDownsample.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $taskDownsample.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $taskDownsample.DrawImage($taskHiRes, [Drawing.Rectangle]::new(0, 0, $Width, $Height))
    } finally { $taskDownsample.Dispose(); $taskHiRes.Dispose() }
    return ,$taskBitmap
}

function Save-BrandPng([string]$Name, [int]$Width, [int]$Height, [float]$MarkSize, [bool]$Tile) {
    $taskBitmap = New-BrandBitmap $Width $Height $MarkSize $Tile
    try { $taskBitmap.Save((Join-Path $AssetsDirectory $Name), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $taskBitmap.Dispose() }
}

Save-BrandPng 'KlippMark.png' 256 256 256 $false
Save-BrandPng 'KlippAppIcon.png' 256 256 248 $true
Save-BrandPng 'Square44x44Logo.scale-200.png' 88 88 84 $true
Save-BrandPng 'Square150x150Logo.scale-200.png' 300 300 216 $false
Save-BrandPng 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 24 $false
Save-BrandPng 'StoreLogo.png' 50 50 48 $true
Save-BrandPng 'Wide310x150Logo.scale-200.png' 620 300 216 $false
Save-BrandPng 'SplashScreen.scale-200.png' 1240 600 216 $false
Save-BrandPng 'LockScreenLogo.scale-200.png' 48 48 48 $false

$taskIconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$taskFrames = @()
foreach ($taskSize in $taskIconSizes) {
    $taskBitmap = New-BrandBitmap $taskSize $taskSize $taskSize $true
    $taskStream = [IO.MemoryStream]::new()
    try {
        $taskBitmap.Save($taskStream, [Drawing.Imaging.ImageFormat]::Png)
        $taskFrames += ,$taskStream.ToArray()
    } finally { $taskStream.Dispose(); $taskBitmap.Dispose() }
}
$taskIconFile = [IO.File]::Create((Join-Path $AssetsDirectory 'Klipp.ico'))
$taskWriter = [IO.BinaryWriter]::new($taskIconFile)
try {
    $taskWriter.Write([uint16]0)
    $taskWriter.Write([uint16]1)
    $taskWriter.Write([uint16]$taskIconSizes.Count)
    [uint32]$taskOffset = 6 + 16 * $taskIconSizes.Count
    for ($taskIndex = 0; $taskIndex -lt $taskIconSizes.Count; $taskIndex++) {
        $taskSizeByte = if ($taskIconSizes[$taskIndex] -eq 256) { 0 } else { $taskIconSizes[$taskIndex] }
        $taskWriter.Write([byte]$taskSizeByte)
        $taskWriter.Write([byte]$taskSizeByte)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length)
        $taskWriter.Write($taskOffset)
        $taskOffset += $taskFrames[$taskIndex].Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame) }
} finally { $taskWriter.Dispose() }
Write-Output "Generated Klipp artwork and $($taskIconSizes.Count) icon sizes in $AssetsDirectory"
