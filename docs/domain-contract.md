# 全局类型、状态与配置约定

本文是前后端共用的 API 契约基线。字段级 DTO 见 [api-list.md](api-list.md#附录-a请求响应-dto)；机器可读来源为后端 `public sealed record` 与开发环境 `/swagger/v1/swagger.json`。

## 全局数据约定

- API 前缀 `/api/v1`；JSON 属性 `camelCase`；枚举值稳定小写 `snake_case`。
- 实体 ID 为 UUID 字符串；客户端只透传，不据 ID 推断业务状态。
- 时间统一 UTC ISO 8601（如 `2026-10-26T08:30:00Z`）；时长 `durationMs`（毫秒），大小 `sizeBytes`（字节）。
- 可选数据缺失用 `null` 或省略，不用 `""`、`0` 冒充未知值。
- 公开响应不含磁盘路径、隔离区信息、邀请令牌、密码哈希等内部数据。
- 分页统一 `{ "items": [], "page": 1, "pageSize": 20, "total": 0 }`；`page` 从 1 开始，`pageSize` 上限 100（`GET /me/library` 例外：默认 50、上限 200）。

## 认证与授权

- 登录 `POST /api/v1/auth/login` 返回 `accessToken` 与 `refreshToken`；后续请求头 `Authorization: Bearer <accessToken>`。
- 访问令牌 15 分钟；刷新令牌 30 天，刷新时轮换（旧令牌立即失效）。
- 匿名可浏览曲库、播放（`AnonymousPlayback` 开启时）、读评论/点歌/歌词；收藏、歌单、上传、点歌提交、站内信等要求登录。
- 管理端点要求 `admin` 角色；`tempadmin` 为 3 天有效期的临时管理员，到期自动降级。
- 未认证统一 401 `authentication_required`；已认证但无权限 403 `permission_denied`；他人私有资源一律 404 `resource_not_found`（不暴露存在性）。

## 统一错误响应

```json
{
  "error": {
    "code": "validation_error",
    "message": "title: 曲目标题不能为空",
    "traceId": "0HN..."
  }
}
```

`code` 为稳定机器码，客户端应依赖 `code` 而非 `message`；`traceId` 用于关联服务端日志。模型绑定失败同样折叠为该结构，不返回 RFC 7807。

### 错误码

| 错误码 | 含义 |
|---|---|
| `validation_error` | 请求参数缺失、类型错误或超出取值范围（包含 JSON 反序列化失败） |
| `resource_not_found` | 目标资源不存在，或按当前用户权限不可见（统一用 404 避免暴露存在性） |
| `resource_conflict` | 与资源当前状态冲突（重复提交、状态机不允许的迁移、唯一键冲突等） |
| `authentication_required` | 未携带有效凭据或凭据已失效 |
| `permission_denied` | 已登录但角色或归属校验不通过（用户越权访问他人资源） |
| `rate_limited` | 请求过于频繁，被限流中间件拒绝 |
| `unsupported_media_format` | 方法不被该路由支持，或资源已产出但内容类型不可协商 |
| `internal_error` | 未预期的服务端异常 |
| `storage_error` | 下游 I/O（存储、转码、邮件）环节出错 |
| `payload_too_large` | 请求体超过客户端/服务端限制（如上传体积超限） |
| `email_or_password_invalid` | 邮箱或密码不正确 |
| `password_reset_invalid` | 重置密码令牌无效或已被使用 |
| `invitation_required` | 注册时需要邀请令牌 |
| `password_too_weak` | 新密码强度未达到系统配置的最低门槛（见系统能力 minimumPasswordStrength） |
| `invitation_invalid` | 邀请令牌不存在、格式错误，或与请求邮箱不匹配 |
| `invitation_expired` | 邀请已过期 |
| `invitation_redeemed` | 邀请已被兑换（一次性令牌） |
| `upload_too_large` | 上传文件超过单文件体积上限（上限见系统能力快照 maxUploadSizeBytes） |
| `media_parse_failed` | 音频容器/标签无法解析，文件可能损坏或不被 ffprobe 支持 |
| `private_track_of_another_user` | 无法引用他人私有曲目（内容哈希命中但归属他人） |
| `track_not_accessible` | 目标曲目为他人私有，当前用户无权引用或试听 |
| `transcode_failed` | 转码环节失败（ffmpeg 退出码非零或产物缺失） |
| `lyrics_source_failed` | 外部歌词源查询失败或返回不可用内容 |
| `song_request_mismatch` | 曲目与目标需求不一致，需要管理员确认后才可发布 |
| `song_request_mismatch_override_required` | 不一致且策略配置为强制阻断，必须修正元数据或取消关联需求后才能发布 |
| `avatar_too_large` | 头像文件体积超限 |
| `email_delivery_failed` | 邮件投递失败（SMTP 连接或鉴权问题） |
| `semantic_invalid` | 请求体字段格式正确但语义不合法（如自引用、越界索引） |
| `source_unavailable` | 音源平台不可用（未启用、未配置凭据或平台接口调用失败） |
| `source_download_failed` | 音源音频下载失败（网络中断、体积超限、平台拒绝等） |
| `source_duplicate` | 同一音频内容已在曲库或导入队列中，无需重复抓取 |

主要 HTTP 状态：`400` 参数错误、`401` 未认证、`403` 无权限、`404` 不存在/不可见、`409` 状态冲突、`413` 上传超限、`415` 不支持的媒体格式、`429` 限流、`500` 内部错误。创建类成功返回 `201`，异步受理返回 `202`，幂等取消/无响应体返回 `204`。

## 状态与枚举

所有状态在 JSON 中为小写字符串，不使用 C# 枚举整数。

| 枚举 | 取值 |
|---|---|
| 用户角色 | `user`、`tempadmin`、`admin`（互斥，同一时刻只有一个） |
| 邀请状态 | `created`、`sent`、`issued`、`delivery_failed`、`redeemed`、`expired`、`revoked` |
| 点歌状态 | `open`、`planned`、`in_progress`、`fulfilled`、`declined`、`withdrawn` |
| 点歌时间线事件 | `submitted`、`planned`、`in_progress`、`fulfilled`、`declined`、`withdrawn`、`reopened` |
| 点歌时间线操作人 | `user`、`admin`、`system` |
| 导入批次状态 | `queued`、`processing`、`completed`、`completed_with_errors`、`failed`、`cancelled` |
| 导入项状态 | `uploaded`、`validating`、`parsing_metadata`、`transcoding`、`pending_review`、`pending_publication`、`ready_to_publish`、`published`、`rejected`、`processing_failed` |
| 外部下载任务状态 | `queued`、`downloading`、`import_queued`、`completed`、`failed` |
| 歌单待补齐状态 | `pending`、`downloading`、`no_source`、`failed` |
| 收听事件类型 | `play`、`complete`、`skip` |
| 曲目反馈 | `like`、`dislike`、`not_interested` |
| 站内信类型 | `song_request.declined`、`song_request.fulfilled`、`song_request.reopened`、`song_request.withdrawn`、`song_request.progress`、`system` |
| 歌词格式 | `plain`、`lrc` |
| 歌词来源 | `embedded`、`sidecar`、`lrclib`、`kugou`、`migu`、`manual` |

### 点歌状态机

允许向前推进或跳级，终态只有 `fulfilled`（已入库，不可逆）：

| 当前状态 | 可流转到 |
|---|---|
| `open` | `planned`、`in_progress`、`fulfilled`、`declined`、`withdrawn` |
| `planned` | `in_progress`、`fulfilled`、`declined`、`withdrawn` |
| `in_progress` | `fulfilled`、`declined` |
| `declined` | `open`（仅经 reopen 端点） |
| `withdrawn` | `open`（仅经 reopen 端点） |
| `fulfilled` | — |

`declined`/`withdrawn` 回到 `open` 必须调用 `POST /api/v1/admin/song-requests/{requestId}/reopen` 并填写 `reason`（≤ 2000 字符），事件类型记为 `reopened`，原因写入时间线并通知提交人；普通状态更新端点不接受这种回退。撤回仅能在 `open` 阶段由提交人发起（管理员可在 `open`/`planned` 阶段代为撤回）。

## 功能开关

服务端 `appsettings.json` 的 `Features` 节，可用环境变量覆盖；关闭的端点组返回 404。`GET /api/v1/system/capabilities` 返回非敏感快照。

| 开关 | 当前默认 | 说明 |
|---|---|---|
| `AnonymousPlayback` | `true` | `GET /tracks/{trackId}/stream` 的未登录播放 |
| `UserCollections` | `true` | 用户收藏与歌单 |
| `SongRequests` | `true` | 点歌 |
| `AdminLibraryImport` | `true` | 管理员批量导入（组合开关） |
| `UserUploads` | `true` | 用户上传与我的曲库 |
| `ManualReview` | `true` | 导入必须人工审核（参与组合判定） |
| `EditorialCollections` | `true` | 运营精选 |
| `ListeningEvents` | `true` | 播放事件与反馈 |
| `PersonalizedRecommendations` | `true` | 每日个性化推荐 |
| `ServerTranscoding` | `true` | 服务端转码（参与组合判定） |
| `SourceDownload` | `true` | 外部音源抓歌 |

`ManualReview` 与 `ServerTranscoding` 在启动时强制校验为 `true`，否则服务拒绝启动。

## 上传与媒体

- 单文件上限 262144000 字节（250 MB）；单批上限 1 GB；单批最多 10 个文件。
- 允许容器：mp3、flac、wav、aac、m4a、m4b、ogg、oga、wma、aiff、aif、ape、mpc、mpp、wv、dsf。扩展名只是初筛，服务端仍以 ffprobe/魔术字节校验实际内容。
- 转码目标：AAC in M4A（192 kbps）；正式库保留原文件与转码产物。
- 上传文件进入隔离区，隔离文件不能被匿名或其他用户访问；磁盘路径永不返回客户端。
- 头像 jpg/png/webp ≤ 2 MB；主题壁纸 jpg/png/webp ≤ 8 MB；均做魔术字节校验。

## 配置键

| 配置键 | 作用 |
|---|---|
| `Database:ConnectionString` | PostgreSQL 连接串（生产用 `Database__ConnectionString` 注入） |
| `Redis:ConnectionString` | Redis 连接串；留空回退进程内内存缓存 |
| `RabbitMQ:*` | 消息队列（下载/导入任务）；口令用环境变量注入 |
| `Storage:LibraryRoot` / `Storage:QuarantineRoot` | 正式媒体目录 / 隔离目录 |
| `Auth:AccessTokenMinutes` / `Auth:RefreshTokenDays` / `Auth:InvitationExpiryHours` | 令牌与邀请有效期（默认 15 分钟 / 30 天 / 48 小时） |
| `Auth:SigningKey` | JWT 签名密钥（≥ 32 字节，必须用用户机密或环境变量 `Auth__SigningKey` 注入，缺失则拒绝启动） |
| `PasswordPolicy:MinimumStrength` | 密码最低强度（0–4，默认 2） |
| `Uploads:MaxFileSizeBytes` / `MaxBatchSizeBytes` / `MaxFilesPerBatch` / `AllowedFormats` | 上传限制与格式白名单 |
| `Transcoding:TargetFormat` / `BitrateKbps` / `MaxConcurrentJobs` | 转码目标与并发 |
| `LibraryImport:AutoPublishEnabled` / `AutoPublishThreshold` | 管理员导入自动发布阈值（默认 70 分） |
| `Lyrics:AutoFetchEnabled` / `ExternalSources` / `MinScore` | 歌词自动抓取与外部源顺序（lrclib→kugou→migu） |
| `SongRequestFulfillment:Checks:*` | 发布时的点歌匹配校验策略（Enforced/Warning/Disabled + 是否允许管理员覆盖） |
| `SongRequests:ArchiveEnabled` / `ArchiveAfterDays` | 点歌自动归档（默认 30 天） |
| `SourceDownload:*` | 音源平台开关、凭据、超时、自动发布阈值 |
| `Cors:AllowedOrigins` | 允许的 Web 来源；未配置则不放行跨域 |

凭据（SMTP 密码、数据库口令、JWT 签名密钥）只通过环境变量或用户机密注入，不写入仓库与日志。

