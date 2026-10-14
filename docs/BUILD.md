# 构建与测试

## 依赖

- Windows x64。
- .NET 8 SDK 8.0.426；允许的补丁回退策略见 `global.json`。
- NuGet：NAudio.Wasapi / NAudio.Core 2.2.1。
- 测试依赖：Microsoft.NET.Test.Sdk 17.11.1、xunit 2.9.2、xunit.runner.visualstudio 2.8.2。

不需要 WDK。编译的应用使用现成的、遵循原作者许可的 VB-CABLE；它本身不包含自行开发或签名的内核驱动。

## 本地构建

```powershell
dotnet restore VoiceBridge.sln --locked-mode
dotnet build VoiceBridge.sln -c Release --no-restore
dotnet test tests\VoiceBridge.Tests\VoiceBridge.Tests.csproj -c Release --no-build --filter "Category!=OptInIntegration&Category!=WindowsAudio"
dotnet publish src\VoiceBridge.App\VoiceBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o artifacts\publish
```

或者运行 `scripts\build.ps1`。所有运行时和必要组件资源嵌入单文件；`scripts\publish-portable.ps1` 也可直接发布。

## 验证范围

开发阶段通过了 DSP、格式转换、资源校验、窗口关闭 / 恢复、系统托盘及实例信号测试。进行过 Windows 原生进程捕获、两进程合成音隔离、混音通过已安装虚拟线缆的验证。图标变更后还检查了单文件发布中的图标和托盘资源。

这些短时、合成音和局部实机检查不代表所有用户设备、所有语音平台、长期负载或 <100 ms 已达标。仓库不包含开发者的设备报告、录音和个人路由备份。

## 音频测试

以下命令不向音频设备写入测试音：

```powershell
dotnet test -c Release --filter "Category!=OptInIntegration&Category!=WindowsAudio"
# 在支持按进程捕获的 Windows 上包含原生 API 启停测试
dotnet test -c Release --filter "Category!=OptInIntegration"
```

合成音集成测试会写入虚拟麦克风。请先退出通话、录音和使用该设备的程序，确认虚拟线缆空闲后才显式启用。测试检测到活动会话会拒绝执行。

```powershell
dotnet build VoiceBridge.sln -c Release
$env:VOICEBRIDGE_RUN_AUDIO_INTEGRATION = '1'
$env:VOICEBRIDGE_DOTNET = (Get-Command dotnet).Source
$env:VOICEBRIDGE_FIXTURE = "$PWD\tests\VoiceBridge.AudioFixture\bin\Release\net8.0-windows10.0.19041.0\VoiceBridge.AudioFixture.dll"
dotnet test tests\VoiceBridge.Tests\VoiceBridge.Tests.csproj -c Release --no-build --filter "Category=OptInIntegration"
Remove-Item Env:\VOICEBRIDGE_RUN_AUDIO_INTEGRATION
Remove-Item Env:\VOICEBRIDGE_DOTNET
Remove-Item Env:\VOICEBRIDGE_FIXTURE
```

## 不启动声音的发布检查

```powershell
.\VoiceBridge.exe --verify-bundle .\bundle-check.json
.\VoiceBridge.exe --probe-tray .\tray-check.json
.\VoiceBridge.exe --probe-capture-api .\capture-check.json
```

`--probe-engine` 与上述检查不同：它会短时使用生产音频引擎和真实麦克风，仅适用于用户明确启用、虚拟线缆空闲的本地诊断；不要在通话期间执行。
