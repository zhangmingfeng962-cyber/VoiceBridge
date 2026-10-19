# 房间中继部署（协议 3 / 0.9.1）

公开下载版内置国内 HTTPS 服务地址，无需个人配置文件或私人建房密钥。应用自身不在 EXE 中保存管理员密钥。

自部署时使用 Node.js 22+，在 relay 目录运行 npm ci --ignore-scripts，然后 node server.mjs。设置 PORT=3000、PUBLIC_ORIGIN=你的 HTTPS 根地址（不带结尾斜线）、PUBLISH_KEY_HASH=一个私人随机 64 位十六进制密钥字符串的 SHA256。不要把原始管理员密钥提交到仓库。反向代理需要支持 WebSocket。

设置 PUBLIC_ROOM_CREATION=1 开放匿名建房；默认关闭。关闭时只有持有原私人建房密钥的 API 请求可创建房间。公开客户端不含该密钥。要让客户端连接自己的服务，修改 src/VoiceBridge.App/Room/RoomService.cs 中的公共根地址后重新编译。

匿名建房每来源 socket 地址每分钟最多 4 次；服务位于反向代理后时可能共享同一个来源地址，限流更严格。最多 4 个房间、每房间 8 人，匿名空房 30 秒过期，主持人连接后保活，房间最长 2 小时。主持人离开关闭房间。创建房间返回仅属于此房间的临时关闭凭据，不授予其他房间或服务器管理员权限。

GET /health 应显示 release=0.9.1、publicRooms=true、rename=true、protocol=3、ready=true。网页和客户端应一并升级；部署重启会结束当前房间。旧版客户端仍可能在打开控制页时自动关麦，使用自动开麦功能请升级 0.9.1。

音乐 48 kHz 双声道 Opus 约 256 kbps/路；多人转发增加出站流量。国内现有服务沿用用户的 Sealos 杭州实例和当前账户额度，不新增付费实例。请关注试用余额与平台流量费用；预置地址不等于永久免费的服务承诺。

Build-OfflineRelay.mjs 可将服务源码与已安装的 ws 依赖打包为离线 ConfigMap 输入；校验 SHA256 后启动，避免启动时依赖境外仓库。该脚本不读取任何个人配置或密钥。
