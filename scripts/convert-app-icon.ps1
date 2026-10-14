param(
    [string]$Png = (Join-Path $PSScriptRoot '..\src\VoiceBridge.App\Assets\voicebridge.png'),
    [string]$Ico = (Join-Path $PSScriptRoot '..\src\VoiceBridge.App\Assets\voicebridge.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskSourceImage = [System.Drawing.Bitmap]::FromFile([System.IO.Path]::GetFullPath($Png))
$taskFrames = [System.Collections.Generic.List[object]]::new()
try {
    foreach ($taskSize in @(16,20,24,32,40,48,64,96,128,256)) {
        $taskFrame = [System.Drawing.Bitmap]::new($taskSize,$taskSize,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [System.Drawing.Graphics]::FromImage($taskFrame)
        $taskAttributes = [System.Drawing.Imaging.ImageAttributes]::new()
        $taskStream = [System.IO.MemoryStream]::new()
        try {
            $taskGraphics.Clear([System.Drawing.Color]::Transparent)
            $taskGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $taskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskAttributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $taskRectangle = [System.Drawing.Rectangle]::new(0,0,$taskSize,$taskSize)
            $taskGraphics.DrawImage($taskSourceImage,$taskRectangle,0,0,$taskSourceImage.Width,$taskSourceImage.Height,[System.Drawing.GraphicsUnit]::Pixel,$taskAttributes)
            $taskFrame.Save($taskStream,[System.Drawing.Imaging.ImageFormat]::Png)
            $taskFrames.Add(@{Size=$taskSize; Data=$taskStream.ToArray()})
        } finally { $taskGraphics.Dispose(); $taskAttributes.Dispose(); $taskStream.Dispose(); $taskFrame.Dispose() }
    }
} finally { $taskSourceImage.Dispose() }
$taskFile = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Ico))
$taskWriter = [System.IO.BinaryWriter]::new($taskFile)
try {
    $taskWriter.Write([UInt16]0); $taskWriter.Write([UInt16]1); $taskWriter.Write([UInt16]$taskFrames.Count)
    $taskOffset = 6 + 16 * $taskFrames.Count
    foreach ($taskFrame in $taskFrames) {
        $taskWriter.Write([byte]($taskFrame.Size -band 255)); $taskWriter.Write([byte]($taskFrame.Size -band 255))
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([UInt16]1); $taskWriter.Write([UInt16]32)
        $taskWriter.Write([UInt32]$taskFrame.Data.Length); $taskWriter.Write([UInt32]$taskOffset)
        $taskOffset += $taskFrame.Data.Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Data) }
} finally { $taskWriter.Dispose(); $taskFile.Dispose() }
Write-Output "Windows icon: $([System.IO.Path]::GetFullPath($Ico))"
