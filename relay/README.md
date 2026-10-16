# VoiceBridge HTTPS 音乐中继

Node.js 22+，没有 npm 依赖。接收端不需要账号，发送端需有该服务的独立发送凭据。只提供音乐片段 API 和接收页面，不是通用代理，不开放分享者电脑入站端口。

## 免费托管

可使用支持 Node.js 的 Web Service，例如 Render Free。源码仓库为公开仓库时，可直接填公开 Git URL，不需要赋予平台访问全部私有仓库的权限。

- 根目录：`relay`
- 构建命令：`node --version`
- 启动命令：`node server.mjs`
- 计划：`Free`
- 健康检查：`/health`
- 环境变量：`PUBLISH_KEY_HASH`，为发送密钥 ASCII 文本的 SHA-256 小写十六进制。
- Render 会提供 `PORT`、`RENDER_EXTERNAL_URL`；其他平台请设置 `PUBLIC_ORIGIN=https://实际域名`，并正确终止 HTTPS。

不要把发送密钥放进源码、公共 EXE、邀请链接、聊天记录或日志。朋友的收听链接只含本次收听令牌和加密密钥，不含发送权限。

Render Free 有空闲休眠、冷启动、月度带宽 / 构建限额和随时重启限制。未添加付款方式时，超过免费带宽会暂停；已绑定付款方式的账户可能产生额外费用，应先检查平台用量与支出设置。本项目不会替你开付费实例、添加付款方式，也不做维持在线的空请求。官方限制：https://render.com/docs/free 。

本机原始音频每位收听者约 1.38 GB / 小时，另有协议开销。免费托管适合小范围试用，不能保证长期、无限人数或零中断。默认接收缓冲 1.2 秒，总延迟还包括分段与网络。

## 本地运行和测试

```powershell
# 在 relay 目录中，使用自己的发送密钥的 SHA-256；不要使用文档中的演示值。
$env:PUBLISH_KEY_HASH = '填写64位小写十六进制SHA256'
$env:PORT = '3000'
node server.mjs
```

```powershell
node --test tests/*.test.mjs
```

`/health` 应返回 `ready:true`。本地 HTTP 仅用于测试；跨网必须使用有效 HTTPS，不能禁用证书验证。

## 配置发送者

使用 PowerShell 7 运行 `./relay/configure-sender.ps1 -Origin https://实际中继根地址`，会生成并保存当前用户 DPAPI 保护的发送凭据，只显示应填入服务器环境变量的 `PublishKeyHash`。脚本不会显示原始密钥。默认更改域名时重用现有发送密钥并重新绑定；显式 `-RotateKey` 会生成新密钥，服务器散列也需更新。

Windows 客户端读取 `%LOCALAPPDATA%\VoiceBridge\relay-profile.json`，格式为 `Version:1`、`Origin`、`ProtectedPublishKey`。发送密钥必须通过 Windows 当前用户 DPAPI 加密，熵为 UTF-8 `VoiceBridge.Relay.v1|` 加规范化 HTTPS 根地址。原始密钥不会随 EXE 分发；换电脑 / Windows 用户或更改域名时需重新配置。

只有配置了自己服务发送凭据的人能创建房间和上传；下载公共 EXE 不代表获得此账户的中继发送权限。任何朋友都可以凭有效邀请收听。不要把本机发送配置上传 GitHub。

## 协议与保密范围

1. 发送者通过 HTTPS `POST /api/rooms`，使用发送凭据创建房间，获得独立写入和收听令牌。
2. 发送者本机生成新的 AES-256-GCM 密钥，每 400 ms 将 40 个 VBM1 音频包加密，`PUT /api/rooms/{id}/chunks/{sequence}`。
3. 链接为 `/listen/{id}#收听令牌.解密密钥`；URL 片段不发送给服务。接收者用收听令牌读取加密片段，在浏览器 Web Crypto 中解密。
4. VBE1 头：4 字节 magic，4 字节小端序号，12 字节随机 nonce，随后 154240 字节密文和 16 字节 GCM tag；AAD 为 ASCII `roomId|sequence`。改动密文、序号或房间会导致认证失败。
5. 音频本体为 48 kHz 双声道 float32，没有人声降噪、自动增益或有损语音编码。接收端仍有时钟漂移校正及断流淡入淡出，不代表逐位听音输出。

Node 版本只在内存保留约 8 秒的加密片段，不写音频文件；停止请求会删除房间，断网未能撤销时最迟 120 秒后不可读取，内存清理由服务定时执行。服务重启会清空所有链接。单次房间最多两小时、最多四个并行发送房间；大量听众仍受服务带宽限制。

该端到端加密依赖接收页面代码与分享者可信。服务运营者若恶意替换页面，可以窃取浏览器里的解密密钥；它不能替代对托管方和发送者的信任。持有完整链接的人都能收听，请只发给指定朋友。

另有 Sites / R2 部署验证版本：本次部署成功，但当前网络在平台前置防护处收到 HTTP 403，尚未通过真实外网传输。不能把“部署成功”作为“朋友已能收听”的验收结果。
