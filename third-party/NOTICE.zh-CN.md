# 第三方组件

VB-CABLE 是 VB-Audio / Vincent Burel 提供的 donationware（捐赠软件），并非本项目开发的驱动。

- 官方来源：https://vb-audio.com/Cable/ （亦可访问 https://www.vb-cable.com/）
- 下载：https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip
- 许可与分发条件：https://vb-audio.com/Services/licensing.htm
- 捐赠 / 购买许可：https://shop.vb-audio.com/en/win-apps/11-vb-cable.html

如果发现有用，请支持原作者，按自己的使用场景捐赠或购买许可。企业、机构等专业分发场景需遵守原作者的数量许可要求。本包保留官方 ZIP 原样；应用只解压并启动官方安装器，由用户接受原厂条款、批准 UAC 及完成安装。ZIP 内的 readme.txt 是原厂许可的一部分。正式面向公众分发前应再次核对最新许可，若需要无交互安装或改名，请向原作者取得相应授权。

原包 SHA-256：B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB

NAudio.Wasapi / NAudio.Core 2.2.1：MIT，https://github.com/naudio/NAudio 。许可证见 NAudio-LICENSE.txt。

可选临时互联网链接使用官方 cloudflared 2026.10.1，Apache-2.0。EXE 不包含该组件；仅在用户明确创建临时链接时从 https://github.com/cloudflare/cloudflared/releases/tag/2026.10.1 下载未经修改的 Windows x64 原件并核对 SHA-256。缓存文件每次执行前重新检查。许可：https://github.com/cloudflare/cloudflared/blob/2026.10.1/LICENSE 。临时链接依赖 Cloudflare 服务，无持续可用保证，不是端到端加密；详见 docs/MUSIC-LINK.md。

NAudio 的 WDL 重采样器源自 Cockos WDL，由 Mark Heath 移植；许可见 WDL-LICENSE.txt。随自包含发布包附带的 .NET 运行时保留微软 MIT 许可及上游第三方声明。

微软 Application Loopback 示例用于理解公开 API；本项目按其公开接口独立实现 C# 调用，没有引入示例源文件。https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/
