# 构建与测试

## 依赖

- Windows x64。
- .NET 8 SDK 8.0.426；允许的补丁回退策略见 `global.json`。
- NuGet：NAudio.Wasapi / NAudio.Core 2.2.1。
- .NET 8 SDK 提供 ASP.NET Core 框架引用，用于仅监听回环的音乐服务；自包含发布会一起打包运行时。
- Node.js 18+ 仅用于运行 `node --test tests/web/receiver-worklet.test.cjs`，运行应用不需要 Node。
- 测试依赖：Microsoft.NET.Test.Sdk 17.11.1、xunit 2.9.2、xunit.runner.visualstudio 2.8.2。

不需要 WDK。编译的应用使用现成的、遵循原作者许可的 VB-CABLE；它本身不包含自行开发或签名的内核驱动。

## 本地构建

```powershell
dotnet restore VoiceBridge.sln --locked-mode
dotnet build VoiceBridge.sln -c Release --no-restore
dotnet test tests\VoiceBridge.Tests\VoiceBridge.Tests.csproj -c Release --no-build --filter "Category!=OptInIntegration&Category!=WindowsAudio"
dotnet publish src\VoiceBridge.App\VoiceBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o artifacts\publish
```

或者运行 `scripts\build.ps1`。运行时、接收页和原虚拟组件资源嵌入单文件；可选的官方 cloudflared 在首次明确创建临时链接时下载并校验。`scripts\publish-portable.ps1` 也可直接发布。

## 验证范围

开发阶段通过了 DSP、格式转换、资源校验、窗口关闭 / 恢复、系统托盘及实例信号测试。进行过 Windows 原生进程捕获、两进程合成音隔离、混音通过已安装虚拟线缆的验证。图标变更后还检查了单文件发布中的图标和托盘资源。

v0.3.2 增加自启动登记 / 关闭、含空格与中文的命令行路径、EXE 移动检测、静默启动到托盘及托盘不可用时显示窗口的测试。登记测试只使用随机命名的 `HKCU\Software\VoiceBridge.Tests` 子键，结束后清理，不会修改当前用户真正的 Windows 自启动项，也不会启动录音。

v0.4.0 增加三个播放器的进程根选择、大小写匹配、嵌套进程去重、相似进程名排除、无效来源拒绝、配置兼容及 WPF 来源选择测试。新来源的筛选测试使用进程快照样本，不等同于真实网易云 / 酷狗客户端播放验证；发布环境没有安装这两个客户端。

v0.5.0 增加场景独立保存 / 迁移、参数签名与手动反馈失效、音乐声道处理、WAV 统计和默认输入恢复计划测试。Windows 默认输入状态在后台只读刷新。基础测试不改变真实默认输入、不录制真实声音、不启动平台通话；虚拟输入录制和耳机试听仍需通过用户明确点击的实机检查，各平台接收效果还需另行验收。

这些短时、合成音和局部实机检查不代表所有用户设备、所有语音平台、长期负载或 <100 ms 已达标。仓库不包含开发者的设备报告、录音和个人路由备份。

v0.6.0 增加 PCM 位值保持、双收听端、密钥拒绝、Origin 检查、8 人上限、资源 / 安全头、停止撤销和默认不分享的测试。接收 worklet 的启动缓冲、独立左右声道、断流补零、序号跳变 / 回绕、非有限值和过期队列由 Node 内置测试覆盖。基础测试启动回环 HTTP / WebSocket 服务，不访问公网或真实音频设备。

v0.6.0 历史验证：67 项 .NET 基础测试、5 项接收缓冲测试通过；桌面 Edge 的本机合成音链路接收约 37 MB，页面显示 0 次缓冲耗尽和序号中断。播放音量为 0，验证链路运行与队列统计，未作主观音质验收。该版临时域名的外网健康检查未通过。

v0.7.0 验证：71 项 .NET 基础测试、10 项中继 / WAN 缓冲测试、5 项原本地接收缓冲测试通过。合成音实际上传到公网 Render 服务，并下载、解密验证了 115200 个双声道样本，最大已知测试源误差约 1.9e-9。延长初测发现抖动后，改用服务器等待新片段，并在过期时保留可播放缓冲；默认接收缓冲提高到 2.4 秒。真实朋友网络、主观音质和长期负载仍需另行验收。

公网合成音检查：配置自己的发送凭据后运行 `VoiceBridge.exe --serve-music-demo demo.json --relay`，再运行 `node relay/tests/public-probe.mjs demo.json sanitized-result.json`。它不会打开音频采集设备，接收数据来自本机合成器。原始 demo 报告含临时密钥，不得上传；仅分享明确不含密钥的统计结果。

开发者可运行 `VoiceBridge.exe --serve-music-demo demo.json`，生成低音量合成测试音和本机收听地址，最多持续 5 分钟；创建 `demo.json.stop` 提前结束。加 `--internet` 会下载 / 启动官方连接组件并尝试临时公网地址。该诊断不采集麦克风或播放器；不要把包含临时密钥的诊断报告提交到仓库。

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
.\VoiceBridge.exe --render-preview .\scenario-preview.png --expand-scenario
```

`--probe-engine` 与上述检查不同：它会短时使用生产音频引擎和真实麦克风，仅适用于用户明确启用、虚拟线缆空闲的本地诊断；不要在通话期间执行。

`--probe-tray` 也会验证首次启动时窗口保持隐藏、托盘可恢复，不会登记自启动。真实登录验证：在固定位置开启自启动，退出程序，注销后重新登录 Windows，确认仅托盘出现，再用托盘或手动双击恢复窗口；这个检查需要真实登录会话，不能用窗口测试代替。Windows 的 Run 启动规则见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)。
