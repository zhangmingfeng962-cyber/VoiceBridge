param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    & $Dotnet restore VoiceBridge.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    & $Dotnet build VoiceBridge.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $Dotnet test tests\VoiceBridge.Tests\VoiceBridge.Tests.csproj -c Release --no-build --filter 'Category!=OptInIntegration&Category!=WindowsAudio'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $Dotnet publish src\VoiceBridge.App\VoiceBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None --no-restore -o artifacts\publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.zh-CN.md,LICENSE -Destination artifacts\publish
    Copy-Item -LiteralPath docs -Destination artifacts\publish -Recurse -Force
    Write-Output "Ready: $projectRoot\artifacts\publish\VoiceBridge.exe"
} finally { Pop-Location }
