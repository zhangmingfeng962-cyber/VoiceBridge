# 0.10.0 — 2026-10-28

- 公共网站迁移到杭州阿里云和已备案域名 voicebridge-room.cn，启用 HTTPS 与自动续期。
- 客户端内置公共地址，下载即可建房；新增网站首页、邀请入房和备案链接。
- 屏幕共享支持窗口/桌面、多人观看、暂停、切换、画质自适应和临时鉴权 TURN 中继。
- 中继采用实际出口总限速，避免按连接预留带宽使多人观看被提前拒绝。

# 0.10.0-preview.1

- 新增单人屏幕共享、多人观看、暂停恢复、来源更换和全屏/放大。
- 新增独立 WebRTC 视频、房间级占用与连接隔离、临时 TURN 凭据和带宽预算。
- 共享声音复用原有程序音乐通道，保留人声和音乐控制。
- 提供本机预览脚本、屏幕中继准备/启用脚本与发布验收说明；公共服务尚未切换。

# Changelog

## 0.9.1 — 2026-10-19

- Keep disabled Windows buttons dark and readable.
- Built-in domestic public room service; remove import UI, private profile reader and profile-generation script. Anonymous creation is opt-in on the server and rate/capacity limited.
- Edit your nickname inside the web room without rejoining.
- Local member join/leave tones follow listening volume and never enter the music stream directly.
- Open the microphone on room entry; first controller attachment preserves it, manual mute remains respected, replacement/closed controllers still mute.


## 0.9.0 — 2026-10-18

- Room-only Windows client and web control page; removes all legacy virtual-device/scenario/tray/startup/tunnel features from this version.
- Per-user microphone 0–400%, total room listening 0–200%, free talk/PTT with configurable key; independent per-member voice/music receive levels.
- Any member can share one application source using the companion; automatic WASAPI session discovery and web source switching. Browser-only participants can share another tab's audio.
- Independent 20 ms Opus voice/stereo music, 60–400 ms adaptive jitter target, bounded queues and late trimming.
- Native receive playback keeps companion-owned room audio outside browser process capture. Private per-device encrypted controls and explicit microphone permission lifecycle.
- Protocol 3 replaces 0.8; every participant and the relay must upgrade together. No claim of zero echo, lossless transport or fixed total latency.
