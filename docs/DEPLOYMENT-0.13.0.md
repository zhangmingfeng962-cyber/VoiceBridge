# 0.13.0 部署与账号存储

沿用现有杭州 ECS、HTTPS、TURN 和独立下载进程，不新增付费资源。

## 账号

Node.js 22.23.3 使用内置 SQLite（该版本仍提示实验性 API）。独立 Worker 串行执行 scrypt N=131072、r=8、p=1；密码不保存明文。数据库及头像目录为 `/var/lib/voicebridge/accounts`，属主 voicebridge、目录 0700。WAL 数据库备份必须使用 SQLite 在线备份或在服务停止后复制，不能仅复制正在写入的主文件。

systemd 保留原有隔离，增加 `MemoryMax=512M` 和 `ReadWritePaths=/var/lib/voicebridge/accounts`。Nginx 覆盖设置 `X-Real-IP $remote_addr`，后端仅在回环来源接受该头。服务默认要求账号登录，测试中的 `authRequired:false` 只用于明确的本机合成测试。

浏览器使用 Secure、HttpOnly、SameSite=Lax 的 `__Host-vb_session` Cookie。Windows 使用当前用户 DPAPI 保存不透明会话，不保存密码。屏幕 WebView2 使用 30 秒一次性、绑定账号和成员的控制票据，不能获得可重复使用的账号凭据。

## 邮件

SMTP 专用密码仅保存在 root 可读的 `/etc/voicebridge/relay.env`（0600）。不得上传仓库、进入客户端或打印日志。不要用 shell source 加载该文件；带引号的值由 systemd 或 JSON 解析器读取。

配置 MAIL_ENABLED、MAIL_USER、MAIL_PASSWORD、MAIL_FROM、MAIL_FREE_REMAINING 和 MAIL_DAILY_LIMIT。服务仅使用 smtpdm.aliyun.com:465 且验证 TLS。未配置凭据或有限额度时停止发送；已有用户仍可密码登录。验证码 5 分钟有效、限 5 次尝试；发送频率与总额度记录在数据库。额度需按云平台实际剩余量维护，不自动充值。验证码与密码不记录日志。

`scripts/Verify-MailTransport.mjs` 只验证 SMTP 身份，不发送邮件。上线前还需本人确认真实收件箱能收到注册邮件，并完成注册、登录和找回密码流程。

## 更新与回滚

先备份现有 nginx、systemd、活动版本指针、更新清单和数据库；使用固定源码 SHA、SHA256 和既有签名公钥核对候选。预发布服务只监听回环，不替换活动连麦服务。正式切换需确认没有活跃连接并留出恢复期；失败恢复原指针和配置。

账号数据库在版本目录之外，不能随回滚删除。旧版没有账号入口，回滚只恢复旧服务能力。私钥和备份不属于公开下载目录。

客户端退出登录会立即清理本机凭据并结束本机连麦。离线时不能确认服务端撤销是否成功；服务端仍执行会话到期和密码重设撤销。延迟的自动续期响应不能恢复已退出的本机账号。
