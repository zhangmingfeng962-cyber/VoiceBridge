# 0.12.0 部署与回滚

使用已有 Ubuntu 24.04 ECS、Node 22.23.3、Nginx、HTTPS 与 coturn；无需新增公网端口或云资源。当前部署仍是 0.11.0，0.12.0 为待验证候选。

候选中继通过 `scripts/Build-OfflineRelay.mjs` 生成包含校验摘要的离线源包。发布环境必须校验固定提交及源包 SHA256，解包到独立版本目录，先在仅回环可访问端口验证，再原子切换 `/opt/voicebridge/current`。保留旧指针及 Nginx、systemd 备份。

主中继设置 `DOWNLOAD_METRICS_FILE=/run/voicebridge/traffic.json`，systemd 的 `RuntimeDirectory=voicebridge` 由 voicebridge 用户拥有。聚合指标每秒原子写入，不包含邀请、密钥、成员 ID 或录音。下载进程同用户运行 `relay/download-server.mjs`，仅监听 127.0.0.1:3101；读取 `/opt/voicebridge/downloads`、`/opt/voicebridge/updates`，并读取同一聚合指标文件。

Nginx 的 `/downloads/` 与 `/updates/` 转发到 3101，关闭响应缓冲，使用至少 600 秒读取超时。移除旧的单文件 512k 独立限速，下载由单独进程统一调度。不要把 Node 3101、更新签名私钥或管理接口暴露到公网。旧 `/downloads/VoiceBridge.exe` 作为回滚下载保留。

更新包先上传固定版本文件并核对 SHA256，再发布 `releases.win-x64.json` 与 `.sig`。签名用 `scripts/Sign-UpdateFeed.ps1` 在受限发布环境执行，公钥已嵌入程序，私钥不提交源码、不传入公开配置。首次安装包的 Windows 商业代码签名未配置。

正式切换必须在无活跃 WebSocket 房间时进行。为避免检查与重启间新增连接，短时暂停新建/加入入口，复查无存量连接后切换；若仍有人在线，取消切换并恢复入口。健康检查失败时立即恢复原指针、Nginx 配置及旧服务。现有房间是内存会话，绝不强制重启活跃房间。

安装包与 GitHub 正式发布依赖国内中继、更新分发的实际验证，不得把候选构建成功当作已上线。
