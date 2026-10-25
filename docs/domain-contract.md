# 全局类型、状态与配置约定

本文是后端 API 和前端客户端共用的契约草案。实现时以 ASP.NET Core OpenAPI schema 为机器可读来源，并从 OpenAPI 生成 TypeScript 客户端类型；不要在前后端各自维护互不校验的同名 DTO。

## 全局数据约定

- API 前缀：`/api/v1`；JSON 属性使用 `camelCase`。
- 实体 ID：UUID 字符串；客户端只透传，不根据 ID 内容推断业务状态。
- 时间：UTC ISO 8601 字符串，例如 `2026-10-15T08:30:00Z`。
- 时长：`durationMs`，整数毫秒；文件大小：`sizeBytes`，整数 byte。不要用格式化后的 `4:08` 或 `12 MB` 作为 API 数据。
- 可选数据缺失时使用 `null` 或省略，不能用 `""`、`0` 冒充未知值。
- 公共曲库 DTO 只表示已发布曲目；隔离文件、真实磁盘路径、邀请令牌和内部存储 key 不得出现在公开响应中。

## 核心 DTO

### Playlist

歌单摘要和详情返回 `artworkUrl`（无封面时为 `null`）。创建请求可选传入 `artworkId`；PATCH 通过 `artworkId` 设置公开可读的已发布封面，使用 `clearArtwork: true` 清除封面。歌单列表遵循 `PageResult<T>`。

### Track

```json
{
  "id": "uuid",
  "title": "歌曲名",
  "artists": [{ "id": "uuid", "name": "艺人名" }],
  "album": { "id": "uuid", "title": "专辑名" },
  "trackNumber": 1,
  "discNumber": 1,
  "releaseYear": 2025,
  "genres": ["独立流行"],
  "durationMs": 213000,
  "artworkUrl": "/api/v1/artwork/uuid",
  "audio": { "container": "flac", "codec": "flac", "bitrateKbps": 920 }
}
```

艺人可多对多关联曲目；专辑、曲目与实际媒体文件分开存储。同一首歌可有多个文件版本。API 返回可展示的曲目/媒体信息，不返回服务器物理路径。

### PageResult<T>

```json
{ "items": [], "page": 1, "pageSize": 20, "total": 0 }
```

页码从 1 开始。最大 `pageSize` 由服务器统一配置，客户端不得用超大值绕过限制。

### Invitation

管理员响应中的邀请对象只包含安全字段：`id`、`email`、`status`、`createdAt`、`expiresAt`、`sentAt`、`redeemedAt`。原始邀请令牌只在邮件链接中发送，不在 API 响应中返回；`POST /admin/invitations` 请求体为 `{ "email": "person@example.com" }`。

### RegisterRequest

```json
{
  "invitationToken": "one-time-secret",
  "password": "...",
  "displayName": "听众"
}
```

注册时服务器从已验证的邀请记录取邮箱，不接受客户端提交或覆盖受邀邮箱。密码只通过 HTTPS 提交，不在日志、邀请链接或客户端持久化明文保存。

### SongRequest

```json
{
  "id": "uuid",
  "title": "歌曲名",
  "artistName": "艺人名",
  "albumTitle": null,
  "status": "open",
  "voteCount": 3,
  "myVote": true,
  "matchedTrackId": null,
  "createdAt": "2026-10-15T08:30:00Z",
  "updatedAt": "2026-10-15T08:31:00Z"
}
```

公开曲目搜索命中时直接显示已存在曲目，不重复创建需求；歌曲需求的投票必须登录且同一用户对同一需求只能投一票。`matchedTrackId` 在需求被曲库歌曲满足后填写。

### LibraryImportItem

导入项响应不返回隔离目录路径或内部存储 key：

```json
{
  "id": "uuid",
  "status": "pending_review",
  "sourceFile": {
    "displayName": "upload.flac",
    "sizeBytes": 28500000,
    "container": "flac",
    "codec": "flac"
  },
  "metadata": {
    "title": "歌曲名",
    "artists": ["艺人名"],
    "albumTitle": "专辑名",
    "trackNumber": 1,
    "discNumber": 1,
    "releaseYear": 2025,
    "genres": ["独立流行"],
    "durationMs": 213000,
    "container": "flac",
    "codec": "flac",
    "bitrateKbps": 920,
    "artworkAvailable": true
  },
  "transcode": {
    "status": "succeeded",
    "outputFormat": "待确认的移动端格式",
    "errorCode": null
  },
  "warnings": [],
  "createdAt": "2026-10-15T08:30:00Z",
  "updatedAt": "2026-10-15T08:31:00Z"
}
```

解析不出的元数据使用 `null`；`warnings` 使用稳定错误码，例如 `duplicate_audio`、`missing_title`、`missing_artist`。仅管理员可以创建导入项、修改元数据、预览隔离文件和发布。转码失败时禁止发布。

### ApiError

```json
{
  "error": {
    "code": "invite_expired",
    "message": "邀请链接已过期",
    "traceId": "..."
  }
}
```

`code` 是稳定机器码；`message` 用于展示；详细异常只写服务端日志。主要 HTTP 状态：`400` 参数错误、`401` 未认证、`403` 无权限、`404` 不存在、`409` 状态冲突、`413` 上传超限、`415` 不支持的媒体格式、`422` 文件无法解析、`429` 频率受限。

首版稳定错误码：`validation_error`、`authentication_required`、`permission_denied`、`resource_not_found`、`resource_conflict`、`invitation_invalid`、`invitation_expired`、`invitation_redeemed`、`upload_too_large`、`unsupported_media_format`、`media_parse_failed`、`submission_not_pending`、`rate_limited`、`internal_error`。`traceId` 用于关联服务端日志，不包含用户或文件路径等隐私信息。

## 全局枚举

所有枚举在 JSON 中使用下面的稳定小写 `snake_case` 值，不把 C# 枚举整数值暴露给客户端。

- `UserRole`：`user`、`admin`。
- `InvitationStatus`：`created`、`sent`、`delivery_failed`、`redeemed`、`expired`、`revoked`。
- `SongRequestStatus`：`open`、`planned`、`in_progress`、`fulfilled`、`declined`。
- `ImportBatchStatus`：`queued`、`processing`、`completed`、`completed_with_errors`、`failed`、`cancelled`。
- `ImportItemStatus`：`uploaded`、`validating`、`parsing_metadata`、`transcoding`、`pending_review`、`ready_to_publish`、`published`、`rejected`、`processing_failed`。
- `TranscodeStatus`：`queued`、`processing`、`succeeded`、`failed`。

邀请仅能从未兑换状态变为 `redeemed`、`expired` 或 `revoked`；一次性令牌只存哈希。导入流程为 `uploaded -> validating -> parsing_metadata -> transcoding -> pending_review -> ready_to_publish -> published`；处理错误进入 `processing_failed`，管理员可拒绝待审核/待发布项。歌曲需求通过管理员导入曲目满足后转为 `fulfilled`。

## 固定安全策略

以下约束不是普通功能开关，不能通过客户端参数或普通管理员设置绕过：

- `RegistrationMode = admin_invite_only`：关闭公开注册。
- 只有管理员能上传音频；文件必须人工审核且转码成功后才可进入公开曲库。
- 用户歌曲需求与投票要求登录；用户不能删除或下架曲目。
- 未审核/拒绝的文件不能被公开搜索、公开播放或加入精选。
- 普通用户不能修改角色、邀请状态、审核结论或正式曲库文件路径。
- 邀请令牌绑定邮箱、限时、单次兑换；密码和邮件密钥不得写入日志。

## 可配置功能开关

首版开关由服务端 `appsettings.json` 提供默认值，并可用环境变量覆盖；功能状态由服务端判定，前端只据此显示/隐藏入口，所有权限仍由 API 执行。默认建议：

```json
{
  "Features": {
    "AnonymousPlayback": true,
    "UserCollections": true,
    "SongRequests": true,
    "AdminLibraryImport": true,
    "ManualReview": true,
    "EditorialCollections": true,
    "ListeningEvents": false,
    "PersonalizedRecommendations": false,
    "ServerTranscoding": true
  }
}
```

`ManualReview` 与 `ServerTranscoding` 在首版必须为 `true`；关闭导入时将 `AdminLibraryImport` 设为 `false`。播放行为和个性化推荐已决定后置，默认关闭。

## 服务器配置键

这些是服务器全局配置，不是前端环境变量。凭据只从环境变量或开发机 Secret Store 注入，不提交到仓库。

- `Storage:LibraryRoot`：审核通过后的正式媒体目录。
- `Storage:QuarantineRoot`：待审核/拒绝媒体的隔离目录；必须与 Web 静态文件目录隔离。
- `Uploads:MaxFileSizeBytes`：单文件上限。产品数值尚未确定；上传启用但此值缺失时，服务应拒绝启动或拒绝上传，不能变成无限制接收。
- `Uploads:AllowedFormats`：允许的扩展名/容器白名单，与文件内容探测结果共同校验。
- `Transcoding:TargetFormat`、`Transcoding:QualityProfile`：兼容播放文件的目标格式和质量档位；需用目标 Android/HarmonyOS 设备验证后确定。
- `Transcoding:MaxConcurrentJobs`：单机并发转码数，防止导入时耗尽电脑资源。
- `Auth:InvitationExpiryHours`：邀请有效期，建议默认 48 小时，可配置。
- `Auth:AccessTokenMinutes`、`Auth:RefreshTokenDays`：访问/刷新凭证周期，实现前安全评审后定值。
- `InvitationEmail:PublicBaseUrl`、`InvitationEmail:FromAddress`：注册链接来源地址和发件人。
- `InvitationEmail:SmtpHost`、`InvitationEmail:SmtpPort`、`InvitationEmail:Username`、`InvitationEmail:Password`：邮件投递设置；邮件服务供应方待定。
- `Cors:AllowedOrigins`：允许的 Web 开发/管理端来源；不使用任意来源通配符搭配凭证。
- `Database:ConnectionString`：PostgreSQL 连接串（Npgsql 格式）；口令不得写进仓库，生产环境用 `Database__ConnectionString` 注入。
- `Redis:ConnectionString`：Redis 连接串；留空时回退为进程内内存缓存（仅限单机开发兜底）。

上传大小的产品值暂留空，但安全上限配置是上传开放前的部署必需项。配置键不得用 `VITE_` 前缀暴露给客户端。

在 .NET 实现中将配置绑定为不可变/只读的强类型 Options（例如 `UploadOptions`、`InvitationOptions`、`FeatureOptions`），通过依赖注入提供给业务模块；不要用可变 `static` 字段充当全局变量。启动时校验必需配置与范围，日志中屏蔽密码、SMTP 凭据、连接串密钥和邀请令牌。

## 客户端能力接口

`GET /api/v1/system/capabilities` 返回非敏感的运行时能力，例如匿名播放、收藏、歌曲需求、管理员导入、人工审核、转码和编辑精选是否启用，以及允许的媒体格式和已配置的上传大小。管理入口只对管理员角色显示。不得返回磁盘目录、邮件配置、内部开关管理权限或任何密钥；前端不应把这份响应当作授权凭据。