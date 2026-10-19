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
