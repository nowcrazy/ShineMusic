# 后端服务

## 当前实现

首版采用 ASP.NET Core Web API、.NET 10、EF Core 和 SQLite，作为单机部署的模块化单体。服务项目位于 `ShineMusic.Api/`。

```powershell
dotnet run --project backend/ShineMusic.Api
```

本地 EF Core 工具清单在仓库根目录，执行迁移命令前先运行 `dotnet tool restore`。

当前已实现：

- `GET /health`、`GET /api/v1/system/capabilities`
- 首页、曲目/艺人/专辑/风格/精选列表与详情查询
- 已发布内容的封面读取与 HTTP Range 播放、分页、搜索和稳定错误响应
- 邀请兑换注册、短期 JWT、刷新令牌轮换、登出与当前用户查询
- 管理员邀请创建/列表/重发/撤销及 SMTP 投递状态
- 登录后的收藏/歌单管理，以及公开歌曲需求、投票和管理员审核
- 管理员批量隔离上传、音频标签解析、AAC/M4A 192 kbps 转码、人工审核与发布
- EF Core 迁移，服务启动时自动应用

开发环境 OpenAPI 文档位于 `/openapi/v1.json`。默认 SQLite 文件与媒体目录位于仓库根目录的 `data/` 下，`data/` 是本地运行数据，不应提交。配置可由 `appsettings.json` 提供默认值，并通过 .NET 配置环境变量覆盖，例如 `Database__ConnectionString`。

邀请邮件的非敏感配置放在 `ShineMusic.Api/appsettings.json` 的 `InvitationEmail` 节：填写前端站点根地址、发件邮箱、SMTP 主机、端口和传输安全方式。端口 587 通常使用 `UseSsl: false`（STARTTLS）；端口 465 通常使用 `UseSsl: true`（SSL on connect）。SMTP 密码不要写进配置文件，使用 `InvitationEmail__Password` 环境变量或服务器密钥管理服务注入；配置 SMTP 用户名时也要同时配置密码。邮件链接会进入 `/#/pages/auth/register?token=...`。

首次在服务器创建管理员（仅数据库还没有任何用户时允许）：

```powershell
dotnet run --project backend/ShineMusic.Api -- --create-admin
```

密码通过无回显终端输入。开发环境 JWT 密钥保存在 User Secrets；生产环境通过 `Auth__SigningKey` 环境变量注入，至少 32 字节，不能提交到仓库。SMTP 通过 `InvitationEmail__PublicBaseUrl`、`InvitationEmail__FromAddress`、`InvitationEmail__SmtpHost`、`InvitationEmail__SmtpPort`、`InvitationEmail__Username`、`InvitationEmail__Password` 和 `InvitationEmail__UseSsl` 配置。邮件未配置或投递失败时，邀请记录为投递失败，原始令牌不会写入 API 响应或数据库。

## 首版边界

- **Catalog / Playback**：曲库元数据与媒体文件分离；已实现已发布曲目文件服务和 HTTP Range 播放。
- **Library Import**：仅管理员上传到隔离区，解析和转码后经人工审核发布；首版不接收普通用户音频投稿。
- **Identity / Invitations**：关闭公开注册，通过管理员邀请创建用户；邮件凭据由服务器环境提供。
- **Collections / Song Requests**：收藏、歌单、需求和投票接口已实现并按 API 契约要求登录；曲库浏览和播放可匿名。
- **Discovery**：首版使用编辑精选和最近加入；播放事件和个性化推荐后置。

首版接口、DTO、配置与安全约束分别见 [api-list.md](api-list.md) 和 [domain-contract.md](domain-contract.md)。播放事件和个性化推荐仍属后续版本；前端尚未提供完整的隔离导入审核工作台。

导入要求服务器可执行 `ffmpeg` 和 `ffprobe`；可通过 `Ffmpeg__ExecutablePath` 与 `Ffmpeg__ProbeExecutablePath` 配置完整路径。单文件默认限制 250 MiB，每批最多 10 个文件、总计 1 GiB。AAC-LC/M4A 192 kbps 是当前兼容优先的候选配置，虽已在本机转码验证，仍需在目标 Android/HarmonyOS 设备确认播放表现。

## 本机运行

手机访问电脑服务时使用电脑的局域网 IP，而不是 `localhost`；电脑与手机需处于同一局域网并允许防火墙放行服务端口。不要将未完成鉴权、HTTPS 与访问控制的开发服务暴露到公网。

当前不引入 Redis、微服务或云对象存储。出现并发、异地访问或独立扩缩容需求后，再评估 PostgreSQL、对象存储和 CDN。