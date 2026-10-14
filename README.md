<p align="center"><img src="src/VoiceBridge.App/Assets/voicebridge.png" width="112" alt="VoiceBridge"></p>

# VoiceBridge

把 QQ音乐和真实麦克风混合成一个 Windows 虚拟麦克风，分享给游戏或语音频道。人声、音乐分别开关，本地耳机仍正常播放。

**当前公开测试版：v0.3.1，适用于 Windows 11 x64。普通 Windows 10 19045 不支持本项目采用的进程音频捕获接口。**

## 下载使用

- **[下载 VoiceBridge.exe](https://github.com/zhangmingfeng962-cyber/VoiceBridge/releases/download/v0.3.1/VoiceBridge.exe)**
- [版本说明、源码包和 SHA-256 校验值](https://github.com/zhangmingfeng962-cyber/VoiceBridge/releases/tag/v0.3.1)

EXE 自带 .NET 运行时，可直接放到桌面双击。首次仍需安装虚拟音频设备：应用内附带 **VB-Audio 官方 VB-CABLE 原包**，点击安装入口后，由用户批准管理员授权并按原厂提示完成安装、重启。已安装 VB-CABLE 时无需重装。

VB-CABLE 是原作者的 donationware，保留其独立许可和捐赠方式。VoiceBridge 的 MIT 许可不涵盖该驱动。详情见[第三方声明](third-party/NOTICE.zh-CN.md)。

## 快速开始

1. 使用耳机，在 Windows 中将默认播放设备和默认通信播放设备都设置为正常耳机。**不要设为 CABLE Input。**
2. 打开 QQ音乐，保持它原来的耳机输出，正常播放。
3. 打开 VoiceBridge，选择真实麦克风；发送端选择 `CABLE Input (VB-Audio Virtual Cable)`。
4. 在目标游戏或语音软件中，将输入 / 麦克风选择为 **`CABLE Output (VB-Audio Virtual Cable)`**，输出 / 扬声器选择耳机。
5. 点击“人声”或“音乐”的独立开关。可以只传人声、只传音乐，也可以同时开启。两路全关后停止采集。

找不到设备时先重启并刷新；默认播放误指向线缆时，可点击“修复播放路由”。修复会参照现有的正常默认播放设备，并先保存本地备份。

## 功能

- **仅按 QQMusic 进程树捕获**：使用 Windows Process Loopback，不使用系统立体声混音，不捕获整个耳机输出。
- **独立人声和音乐开关 / 音量**：人声 0–200%，音乐 0–100%，开关使用平滑过渡。
- **固定音乐增益**：人声过载时只对人声削峰，避免把音乐一起压低；混音预留固定余量。
- **系统托盘**：关闭或最小化窗口后继续运行，左键恢复，右键控制声音、音量、全部停止或退出。
- **全局快捷键**：游戏中也可通过已注册的快捷键控制。
- **单文件发布**：运行时、图标、许可和官方组件包都内置于 EXE。
- **本地诊断**：导出设备和路由信息；默认不保存录音，不上传声音。

| 快捷键 | 操作 |
|---|---|
| Ctrl + Alt + F8 | 开关人声 |
| Ctrl + Alt + F9 | 开关音乐 |
| Ctrl + Alt + ↑ / ↓ | 音乐音量 ±5% |

右上角 **×** 会收起到托盘。完全退出请右键托盘图标选择 **“退出程序”**。图标可能位于右下角“显示隐藏的图标”中。每次重新启动软件，两路声音都默认关闭。

## 音量变小、音乐失真或断续

语音软件或浏览器可能对虚拟麦克风再次做自动增益、噪音抑制和回声消除，音乐可能被当作噪声而逐渐变小。请在平台提供相关选项时关闭这些处理，开启原声 / 音乐 / 高音质模式。浏览器麦克风测试页面也需要检查这些开关；修改后重新录制，已有录音不会恢复。

VoiceBridge 的“关闭通话自动压低音量”只修改 Windows 的通话减音偏好，并先备份原值；它不能关闭第三方平台自己的音频处理。详细排查见[音质与路由说明](docs/AUDIO.md)。

## 支持范围与实际限制

| 项目 | 当前状态 |
|---|---|
| 系统 / 架构 | Windows 11 x64；未提供普通 Win10、x86、ARM64 的功能支持保证 |
| 混音格式 | 内部 48 kHz、float32、双声道；语音平台可能另行压缩或降采样 |
| 虚拟设备 | 依赖 VB-CABLE 驱动，需要管理员安装，部分步骤需要重启 |
| 回声 | 避免系统声音的数字回传；真实扬声器串入麦克风的声学回声仍需通过耳机等方式处理 |
| 延迟 | 以低延迟为目标，尚未完成端到端 <100 ms 验收 |
| 音质 | 没有“所有平台和本地完全一致”“绝无底噪”的保证；源音频、硬件及平台处理均会影响结果 |
| 游戏 / 反作弊 | 没有游戏注入、内存修改或检测绕过；仍需按各游戏政策实测，不能保证所有反作弊兼容 |
| 测试范围 | DSP、资源和窗口 / 托盘测试，以及部分合成音虚拟线缆验证；不等同于所有真实通话平台验收 |

## 自行编译

需要 Windows x64 和 .NET 8 SDK；`global.json` 固定为 8.0.426，并允许兼容的同带补丁版本。NuGet 依赖通过 `packages.lock.json` 锁定。

```powershell
git clone https://github.com/zhangmingfeng962-cyber/VoiceBridge.git
cd VoiceBridge
.\scripts\build.ps1
```

发布结果在 `artifacts\publish\VoiceBridge.exe`。如果只需要发布单文件：

```powershell
.\scripts\publish-portable.ps1
```

详见[构建与测试](docs/BUILD.md)。应用编译不需要 WDK，不会自行生成或伪造驱动签名。

## 项目结构

```text
src/VoiceBridge.Core       浮点混音、缓冲与音量处理
src/VoiceBridge.App        WPF 界面、进程捕获、虚拟设备与托盘
tests                     单元测试及显式启用的合成音集成测试
scripts                   构建、单文件发布与图标转换
third-party               原厂驱动 ZIP 与第三方许可
docs                      使用、排查和测试说明
```

## 配置、隐私与卸载

配置、轮转日志和路由 / 通话音量设置备份位于 `%LOCALAPPDATA%\VoiceBridge`。诊断报告包含端点名称、系统版本、路由和 QQMusic 根进程 ID；分享报告前请自行检查。不要提交包含私人设备信息的日志、录音或配置。

退出托盘程序后可删除 EXE。删除 EXE 不会卸载 VB-CABLE；卸载驱动请使用原厂安装器并按提示重启。移除前确认其他软件没有依赖它。

## 许可与反馈

原创代码采用 [MIT](LICENSE)。VB-CABLE、NAudio、WDL、.NET 运行时保留各自许可，见 [third-party](third-party/NOTICE.zh-CN.md)。VB-CABLE 官方来源为 [VB-Audio](https://vb-audio.com/Cable/)，发现有用时请向原作者捐赠或购买适合自己使用场景的许可。

图标源文件、转换脚本和生成记录包含在 [Assets](src/VoiceBridge.App/Assets/README.zh-CN.md) 中。

问题反馈可提交 [Issue](https://github.com/zhangmingfeng962-cyber/VoiceBridge/issues)，请注明软件版本、Windows 版本、语音平台及复现步骤，不要上传隐私录音或未经检查的设备报告。
