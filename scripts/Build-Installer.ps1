param([string]$Version='0.12.0',[string]$OutputDirectory='artifacts/installer',[string]$Notes='docs/RELEASE-NOTES-0.12.0.md')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
 if((dotnet --version).Trim() -ne '10.0.403'){throw 'SDK 10.0.403 required'}
 dotnet restore VoiceBridge.sln --locked-mode
 dotnet publish src/VoiceBridge.App/VoiceBridge.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/install-app
 $runtime=Get-Content artifacts/install-app/VoiceBridge.runtimeconfig.json -Raw|ConvertFrom-Json
 if(@($runtime.runtimeOptions.includedFrameworks|Where-Object {$_.version -ne '10.0.14'}).Count){throw 'Runtime must be 10.0.14'}
 dotnet tool install vpk --version 1.2.161 --tool-path artifacts/tools
 & artifacts/tools/vpk.exe pack --packId VoiceBridge --packVersion $Version --packDir artifacts/install-app --mainExe VoiceBridge.exe --channel win-x64 --runtime win-x64 --packTitle VoiceBridge --icon src/VoiceBridge.App/Assets/voicebridge.ico --shortcuts Desktop,StartMenuRoot --delta BestSpeed --releaseNotes $Notes --outputDir $OutputDirectory
} finally {Pop-Location}
