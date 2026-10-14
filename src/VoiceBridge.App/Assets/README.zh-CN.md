# VoiceBridge 应用图标

生成方式：内置 image_gen 工具，通过 imagegen 技能生成原创图片；没有使用 API / CLI 回退。

用途：Windows EXE、桌面、任务栏、窗口、托盘和应用标题区统一图标。

- 原图：voicebridge.png，1254×1254，透明圆角外边缘。
- Windows 图标：voicebridge.ico，包含 16、20、24、32、40、48、64、96、128、256 像素帧。
- 转换脚本：../../../scripts/convert-app-icon.ps1；只进行 ICO 格式封装与尺寸适配。

最终生成提示词：

```text
Create ONE polished Windows desktop application icon for an audio mixing and music-sharing app called VoiceBridge. No words or letters. The app has an existing dark-navy, blue, and teal UI. The icon should communicate a real microphone and music together: one bold white microphone silhouette combined with a distinct bright mint-teal musical eighth note, arranged as a single balanced emblem. A premium simple Windows 11 / Fluent-inspired rounded-square navy-to-cobalt tile with very restrained soft depth and clean edges; the shapes should be thick and extremely readable when reduced to 16 or 32 pixels. Large centered symbol, generous visual weight, minimal details. Transparent canvas outside the rounded square, tile fills about 90% of the square image with equal margins. Frontal, square composition, no perspective. Crisp professional app icon, not a screenshot, not a mockup. No text, no logos from other brands, no watermark, no surrounding objects, no duplicate icon variations, no fine lines or tiny equalizer bars.
```
