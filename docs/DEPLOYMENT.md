# 部署房间中继（协议 3）

使用 Node.js 22+，HTTPS/WSS 反向代理，允许长连接。无需数据库、持久卷、音频录制或 UDP/TURN。本版本使用 WebSocket 转发独立 Opus 流。

```sh
cd relay
npm ci --ignore-scripts
# 请在部署工具中注入以下变量，不能提交真实建房密钥
export PUBLISH_KEY_HASH='<64位小写SHA256>'
export PUBLIC_ORIGIN='https://your-domain.example'
export PORT=3000
node server.mjs
```

使用加密随机数生成 32 字节（64 位小写十六进制）的建房密钥，将密钥字符串的 UTF-8 SHA256 填入 `PUBLISH_KEY_HASH`。服务器只配置哈希。`PUBLIC_ORIGIN` 必须为真实 HTTPS 根地址，不带结尾斜线；反代需支持 WebSocket 升级和足够的空闲超时。

在主持人的 Windows 用户账户下运行 `scripts/New-RelayProfile.ps1 -Origin https://your-domain.example`，按提示安全输入原始密钥，脚本会生成 `VoiceBridge-sender.vbrelay`。使用 Windows DPAPI CurrentUser，熵为 `VoiceBridge.Relay.v1|` 加规范化 Origin；其他 Windows 用户不能解密。

把配置放 EXE 旁或在客户端导入。朋友只加入房间时无需该文件。不要把配置、原始密钥、私人便携包或房间邀请提交到仓库。

`GET /health` 应返回 protocol=3、ready=true、codec=opus。旧版 0.8 客户端与新版不兼容，服务端升级会结束旧房间，需要所有人升级后重新建房。

服务默认最多 4 个房间、每房间 8 人、房间最长 2 小时。主持人断开即关闭房间。每路音乐约 256 kbps 外加加密/协议开销；多人转发会随发送者和接收者人数增加流量。请在平台控制台关注试用余额与出站流量费用，不要把注册赠送额度理解为永久免费。

国内现有部署使用用户自己的 Sealos 杭州应用，基于原 Node 官方镜像和离线 ConfigMap 包。通用源码不包含该账户的访问凭据或配置信息。回滚需要同时回滚网页、服务协议和客户端版本。
