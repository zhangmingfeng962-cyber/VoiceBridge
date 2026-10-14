param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
try {
    & $Dotnet publish src\VoiceBridge.App\VoiceBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o artifacts\portable
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    Write-Output "Single executable: $PWD\artifacts\portable\VoiceBridge.exe"
} finally { Pop-Location }
