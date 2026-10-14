# 音质与路由排查

## 正确连接

```text
QQMusic 进程树 → Windows Process Loopback ┐
                                        ├→ 48 kHz 浮点混音 → CABLE Input
真实麦克风 → WASAPI → 格式转换 / 重采样 ─┘                         │
                                                               VB-CABLE
QQ音乐本地输出 + 游戏 + 队友语音 → 耳机               CABLE Output → 语音平台输入
```

Windows 默认播放、默认通信播放及各平台的播放输出都应设为耳机。不要把 QQ音乐、游戏、队友或浏览器播放输出直接分配给 CABLE Input 或 CABLE In 16ch。

程序会检查常见错误路由和虚拟线缆中的其他活动播放会话。检查为定期轮询，不能代替正确的系统和逐应用配置。

## 前几秒正常，之后越来越小

先检查测试网页、语音软件里的 **自动增益、噪音抑制、回声消除**。这些按人声优化的处理可能压低持续音乐。平台支持时关闭，或者选择原声 / 音乐模式，之后重新录制测试。已被处理的旧录音不会因为改变设置恢复。

VoiceBridge 本身对音乐采用固定增益，预留约 6 dB 混音余量，不按讲话活动自动压低音乐。余量会让绝对输出略轻，可用固定音量调整，不能通过提高音量抵消平台主动降噪。

“关闭通话自动压低音量”将 Windows 的通话减音偏好设置为不降低其他声音，原值会保存到本地备份。该设置不会强制关闭平台自己的处理。

## 破音、杂音或回声

- 使用耳机，确认真实麦克风没有拾入扬声器或队友语音。
- 关闭不需要的系统音效、增强和过高的 EQ 增益。源文件已经失真时，混音器无法恢复。
- 真实麦克风及虚拟设备两端尽量使用 48 kHz。设备和平台仍可能重新采样。
- 长时间出现人声削峰提示时降低麦克风增益；不要用整个混音的自动增益反复抬升或压低音乐。
- 设备缓冲过小时可能断音，过大时增加延迟。VB-CABLE 的设置请参考原厂控制面板和手册。

## 平台限制

标准虚拟麦克风使支持 Windows 输入设备选择的平台可以接收混音，但不能强制平台提供无损编码、立体声、关闭降噪或固定增益。有些游戏和通话软件没有这些选项，应以接收端实测为准。

## 技术来源

- [微软按应用音频捕获示例与最低 Build](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)
- [Windows 通话减音](https://learn.microsoft.com/en-us/windows/win32/coreaudio/stream-attenuation)
- [浏览器音频轨设置](https://developer.mozilla.org/en-US/docs/Web/API/MediaTrackSettings)
- [VB-CABLE 官方手册](https://vb-audio.com/Cable/VBCABLE_ReferenceManual.pdf)
