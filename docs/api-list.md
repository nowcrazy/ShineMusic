# Shine Music API 接口文档

> 依据后端控制器源码与开发环境运行时 OpenAPI（`/swagger/v1/swagger.json`）核对生成，覆盖全部 **129** 个端点（26 个控制器类）。字段级契约见 [domain-contract.md](domain-contract.md)。

## 全局约定

| 项目 | 约定 |
|---|---|
| 基础路径 | `/api/v1`（健康检查例外：`GET /health`） |
| 传输 | HTTP/HTTPS；请求/响应为 JSON；上传用 `multipart/form-data`；播放/预览为文件流 |
| 命名 | JSON 字段 `camelCase`；状态/枚举值为稳定小写 `snake_case` 字符串 |
| 时间 | UTC ISO 8601，例如 `2026-10-26T08:30:00Z` |
| ID | UUID 字符串 |
| 鉴权 | `Authorization: Bearer <accessToken>`；访问令牌 15 分钟，刷新令牌 30 天（轮换） |
| 分页 | `page` 从 1 开始；`pageSize` 默认 20、范围 1–100（`GET /me/library` 例外：默认 50、上限 200）；列表统一 `PageResult<T>` |
| 错误 | 统一 `{ "error": { "code", "message", "traceId" } }`，见 [错误响应](domain-contract.md#统一错误响应) |
| 限流 | `POST /me/play-events` 每用户/IP 120 次/分钟，超限 429 `rate_limited` |
| 上传限制 | 单文件 250 MB、单批 1 GB、单批最多 10 个；格式白名单见 `GET /system/capabilities` |

### 鉴权级别

- **匿名**：无需令牌。
- **登录**：需有效 Bearer 令牌；缺失/过期 401 `authentication_required`。
- **管理员**：需 `admin`（或未过期的 `tempadmin`）；角色不足 403 `permission_denied`。
- 标注「匿名」的端点若所属开关关闭会返回 404；`GET /tracks/{trackId}/stream` 在 `AnonymousPlayback` 关闭时要求登录（401）。

### 功能开关

端点组受服务端 `Features` 开关控制；开关关闭时整组返回 404（与资源不存在一致，不暴露功能存在性）。`GET /system/capabilities` 返回当前快照，前端仅据此显隐入口，授权仍由 API 执行。`AdminLibraryImport` 为组合开关：需同时开启 `AdminLibraryImport`、`ManualReview`、`ServerTranscoding`。

| 开关 | 影响端点组 | 当前默认 |
|---|---|---|
| `AnonymousPlayback` | `GET /tracks/{trackId}/stream` 的未登录播放 | 开 |
| `UserCollections` | `/me/favorites`、`/me/playlists` | 开 |
| `SongRequests` | `/song-requests` 与 `/admin/song-requests` | 开 |
| `AdminLibraryImport` | `/admin/library/*`（组合开关，另需 `ManualReview` + `ServerTranscoding`） | 开 |
| `ManualReview` | 导入内容必须人工审核（参与组合判定） | 开 |
| `EditorialCollections` | `/collections` 与 `/admin/collections` | 开 |
| `ListeningEvents` | `/me/play-events`、`/me/feedback`、`/me/history` | 开 |
| `PersonalizedRecommendations` | `/recommendations/daily` | 开 |
| `ServerTranscoding` | 服务端转码（参与组合判定） | 开 |
| `UserUploads` | `/me/uploads`、`/me/library` | 开 |
| `SourceDownload` | `/admin/source-downloads/*` | 开 |

## 接口索引

| 分组 | 方法 | 路径 | 鉴权 | 开关 | 说明 |
|---|---|---|---|---|---|
| 曲目 | `GET` | `/api/v1/tracks` | 匿名 | — | 分页查询已发布曲目；`q` 同时匹配曲目标题/专辑名/艺人名，可按艺人、专辑、风格筛选，`sort` 支持 `recent`/`title`/`duration`。 |
| 曲目 | `GET` | `/api/v1/tracks/{trackId}` | 匿名 | — | 曲目详情。未公开的私有曲目仅上传者本人与管理员可见，其他人按 404 处理。 |
| 曲目 | `GET` | `/api/v1/tracks/{trackId}/stream` | 匿名 | — | 播放曲目音频流，支持 HTTP Range 断点续播；匿名播放关闭时要求登录。 |
| 专辑 | `GET` | `/api/v1/albums` | 匿名 | — | 分页查询专辑摘要（含封面与艺人）。 |
| 专辑 | `GET` | `/api/v1/albums/{albumId}` | 匿名 | — | 专辑详情及曲目列表。 |
| 艺人 | `GET` | `/api/v1/artists` | 匿名 | — | 分页查询艺人摘要。 |
| 艺人 | `GET` | `/api/v1/artists/{artistId}` | 匿名 | — | 艺人详情（简介、专辑、曲目）。 |
| 风格 | `GET` | `/api/v1/genres` | 匿名 | — | 返回曲库中实际存在的风格列表（分页）。 |
| 运营精选 | `GET` | `/api/v1/collections` | 匿名 | 运营精选 | 分页查询已发布的运营精选合集。 |
| 运营精选 | `GET` | `/api/v1/collections/{collectionId}` | 匿名 | 运营精选 | 精选合集详情及曲目列表。 |
| 首页 | `GET` | `/api/v1/music/home` | 匿名 | — | 首页聚合数据：焦点曲目、精选合集、最近加入（结果有服务端缓存）。 |
| 封面图 | `GET` | `/api/v1/artwork/{artworkId}` | 匿名 | — | 按封面 Id 读取图片文件；仅当封面被已发布内容引用时可见。 |
| 认证 | `POST` | `/api/v1/auth/invitations/validate` | 匿名 | — | 校验邀请令牌，返回脱敏邮箱与过期时间。 |
| 认证 | `POST` | `/api/v1/auth/register` | 匿名 | — | 使用邀请令牌 + 密码 + 显示名注册；无有效邀请一律拒绝。 |
| 认证 | `POST` | `/api/v1/auth/login` | 匿名 | — | 账号密码登录，返回访问令牌与刷新令牌。 |
| 认证 | `POST` | `/api/v1/auth/refresh` | 匿名 | — | 刷新令牌轮换（凭 refresh token 换发全新令牌对） |
| 认证 | `POST` | `/api/v1/auth/logout` | 匿名 | — | 退出登录并撤销当前刷新令牌。 |
| 认证 | `GET` | `/api/v1/me` | 登录 | — | 读取当前登录用户资料。 |
| 个人资料 | `PATCH` | `/api/v1/me` | 登录 | — | 修改展示昵称（1-100 字符） |
| 个人资料 | `POST` | `/api/v1/me/avatar` | 登录 | — | 上传/更换头像（multipart/form-data，字段名 avatar） |
| 个人资料 | `GET` | `/api/v1/me/avatar` | 登录 | — | 读取当前用户头像（匿名不可见，必须登录且头像存在） |
| 个人资料 | `DELETE` | `/api/v1/me/avatar` | 登录 | — | 删除当前用户头像（幂等：无头像也返回 204） |
| 个人资料 | `POST` | `/api/v1/me/password` | 登录 | — | 修改登录密码（需提供当前密码） |
| 个人资料 | `GET` | `/api/v1/me/theme` | 登录 | — | 读取当前用户自定义主题配置（未配置时返回全空配置） |
| 个人资料 | `POST` | `/api/v1/me/theme/wallpaper` | 登录 | — | 上传/更换壁纸（multipart/form-data，字段名 wallpaper） |
| 个人资料 | `GET` | `/api/v1/me/theme/wallpaper` | 登录 | — | 读取当前用户壁纸（登录用户本人的私人图片） |
| 个人资料 | `PUT` | `/api/v1/me/theme` | 登录 | — | 更新主题配置（色板、遮罩深浅、启用开关） |
| 个人资料 | `DELETE` | `/api/v1/me/theme` | 登录 | — | 删除自定义主题（壁纸文件 + 配置行，幂等：无配置也返回 204） |
| 我的曲库与歌单 | `POST` | `/api/v1/me/playlist-imports/parse` | 登录 | 用户收藏/歌单 | 解析歌单并预览（逐曲标记是否已匹配曲库） |
| 我的曲库与歌单 | `POST` | `/api/v1/me/playlist-imports/import` | 登录 | 用户收藏/歌单 | 导入歌单：立即生成新歌单，未匹配曲目后台静默下载补齐 |
| 我的曲库与歌单 | `GET` | `/api/v1/me/favorites` | 登录 | 用户收藏/歌单 | 分页查询我的收藏曲目。 |
| 我的曲库与歌单 | `PUT` | `/api/v1/me/favorites/{trackId}` | 登录 | 用户收藏/歌单 | 收藏曲目（幂等）。 |
| 我的曲库与歌单 | `DELETE` | `/api/v1/me/favorites/{trackId}` | 登录 | 用户收藏/歌单 | 取消收藏（幂等）。 |
| 我的曲库与歌单 | `GET` | `/api/v1/me/playlists` | 登录 | 用户收藏/歌单 | 分页查询我的歌单。 |
| 我的曲库与歌单 | `POST` | `/api/v1/me/playlists` | 登录 | 用户收藏/歌单 | 创建歌单，可绑定已上传封面。 |
| 我的曲库与歌单 | `GET` | `/api/v1/me/playlists/{playlistId}` | 登录 | 用户收藏/歌单 | 歌单详情及曲目；导入产生的待补齐曲目通过 `pendingTracks` 返回。 |
| 我的曲库与歌单 | `DELETE` | `/api/v1/me/playlists/{playlistId}/pending/{pendingId}` | 登录 | 用户收藏/歌单 | 删除歌单中的一条待补齐（pending）曲目 |
| 我的曲库与歌单 | `PATCH` | `/api/v1/me/playlists/{playlistId}` | 登录 | 用户收藏/歌单 | 修改歌单名称/描述/封面；`clearArtwork` 显式清空封面。 |
| 我的曲库与歌单 | `DELETE` | `/api/v1/me/playlists/{playlistId}` | 登录 | 用户收藏/歌单 | 删除歌单。 |
| 我的曲库与歌单 | `PUT` | `/api/v1/me/playlists/{playlistId}/tracks/{trackId}` | 登录 | 用户收藏/歌单 | 向歌单加入曲目（幂等）。 |
| 我的曲库与歌单 | `DELETE` | `/api/v1/me/playlists/{playlistId}/tracks/{trackId}` | 登录 | 用户收藏/歌单 | 从歌单移出曲目。 |
| 收听记录与反馈 | `POST` | `/api/v1/me/play-events` | 登录 | 播放事件 | 上报一条收听事件（play/complete/skip） |
| 收听记录与反馈 | `GET` | `/api/v1/me/feedback` | 登录 | 播放事件 | 查询我对曲目的反馈列表（like/dislike/not_interested）。 |
| 收听记录与反馈 | `PUT` | `/api/v1/me/feedback/{trackId}` | 登录 | 播放事件 | 设置/更新对某曲目的反馈。 |
| 收听记录与反馈 | `DELETE` | `/api/v1/me/feedback/{trackId}` | 登录 | 播放事件 | 清除对某曲目的反馈。 |
| 收听记录与反馈 | `GET` | `/api/v1/me/history` | 登录 | 播放事件 | 分页查询我的收听历史。 |
| 用户上传与我的曲库 | `POST` | `/api/v1/me/uploads` | 登录 | 用户上传 | 上传本地音频（可同时携带同名 .lrc 旁挂歌词） |
| 用户上传与我的曲库 | `POST` | `/api/v1/me/uploads/precheck` | 登录 | 用户上传 | 上传前批量预检：客户端把本地文件指纹（标题/艺人/时长）发给服务端，返回曲库中已存在的曲目，命中者可跳过上传、直接建曲库引用 |
| 用户上传与我的曲库 | `GET` | `/api/v1/me/library` | 登录 | 用户上传 | 我的曲库：列出当前用户引用的全部曲目（含私有与公开） |
| 用户上传与我的曲库 | `POST` | `/api/v1/me/library` | 登录 | 用户上传 | 批量加入曲库：预检命中公共曲库的歌曲可直接引用（无需上传文件） |
| 用户上传与我的曲库 | `DELETE` | `/api/v1/me/library/{trackId}` | 登录 | 用户上传 | 从我的曲库移除引用（不影响曲目本体与其他用户的引用） |
| 站内信 | `GET` | `/api/v1/me/notifications` | 登录 | — | 分页拉取当前用户的消息（可选只看未读） |
| 站内信 | `GET` | `/api/v1/me/notifications/unread-count` | 登录 | — | 未读站内信数量（供角标轮询）。 |
| 站内信 | `POST` | `/api/v1/me/notifications/{notificationId}/read` | 登录 | — | 把单条消息标记为已读（幂等） |
| 站内信 | `POST` | `/api/v1/me/notifications/read-all` | 登录 | — | 把当前用户全部未读消息标记为已读 |
| 站内信 | `DELETE` | `/api/v1/me/notifications/{notificationId}` | 登录 | — | 删除单条站内信。 |
| 评论 | `GET` | `/api/v1/tracks/{trackId}/comments` | 匿名 | — | 分页查询曲目评论（含嵌套回复与点赞数），匿名可读。 |
| 评论 | `POST` | `/api/v1/tracks/{trackId}/comments` | 登录 | — | 发表评论或回复（登录）。 |
| 评论 | `DELETE` | `/api/v1/comments/{commentId}` | 登录 | — | 删除本人评论；管理员可删除任意评论。 |
| 评论 | `PUT` | `/api/v1/comments/{commentId}/like` | 登录 | — | 点赞评论（幂等）。 |
| 评论 | `DELETE` | `/api/v1/comments/{commentId}/like` | 登录 | — | 取消点赞评论（幂等）。 |
| 关注 | `GET` | `/api/v1/me/following` | 登录 | — | 分页查询我的关注列表。 |
| 关注 | `GET` | `/api/v1/me/followers` | 登录 | — | 分页查询我的粉丝列表。 |
| 关注 | `PUT` | `/api/v1/me/following/{targetUserId}` | 登录 | — | 关注某用户（幂等）。 |
| 关注 | `DELETE` | `/api/v1/me/following/{targetUserId}` | 登录 | — | 取消关注某用户（幂等）。 |
| 关注 | `GET` | `/api/v1/users/{targetUserId}/follow-summary` | 登录 | — | 查询指定用户的关注状态与粉丝/关注数。 |
| 点歌 | `GET` | `/api/v1/song-requests` | 匿名 | 点歌 | 公开点歌列表，支持按状态/排序分页；匿名可读。 |
| 点歌 | `GET` | `/api/v1/song-requests/{requestId}` | 匿名 | 点歌 | 点歌需求详情与当前状态。 |
| 点歌 | `POST` | `/api/v1/song-requests` | 登录 | 点歌 | 登录用户提交点歌需求；服务端检查是否已有匹配曲目或重复需求。 |
| 点歌 | `PUT` | `/api/v1/song-requests/{requestId}/votes/me` | 登录 | 点歌 | 为需求点赞；每用户每需求一票，重复调用幂等（204）。点赞不可取消。 |
| 点歌 | `GET` | `/api/v1/me/song-requests` | 登录 | 点歌 | 查询我创建或投过票的需求。 |
| 点歌 | `DELETE` | `/api/v1/song-requests/{requestId}` | 登录 | 点歌 | 撤回点歌需求（软撤回，仅 open 阶段；管理员可在 open/planned 阶段代为撤回）。 |
| 点歌 | `GET` | `/api/v1/song-requests/{requestId}/timeline` | 匿名 | 点歌 | 需求处理时间线：按时间正序返回状态事件，供卡片展开查看「提交 → 纳入计划 → 处理中 → 已入库」 |
| 每日推荐 | `GET` | `/api/v1/recommendations/daily` | 匿名 | 个性化推荐 | 每日推荐（默认 30 首、互不重复、当日稳定） |
| 歌词 | `GET` | `/api/v1/tracks/{trackId}/lyrics` | 匿名 | — | 读取曲目歌词列表（公开，匿名可用）。 |
| 歌词 | `GET` | `/api/v1/tracks/{trackId}/lyrics/default` | 匿名 | — | 读取曲目的默认歌词。 |
| 管理 · 曲库维护 | `GET` | `/api/v1/admin/tracks` | 管理员 | — | 管理端曲目列表，支持按信息缺失、重复项、专辑、艺人筛选。 |
| 管理 · 曲库维护 | `PATCH` | `/api/v1/admin/tracks/{trackId}` | 管理员 | — | 修正已发布曲目的元数据（艺人/风格按完整集合替换）。 |
| 管理 · 曲库维护 | `PATCH` | `/api/v1/admin/albums/{albumId}` | 管理员 | — | 修正专辑名、艺人、年份与封面关联。 |
| 管理 · 曲库维护 | `PATCH` | `/api/v1/admin/artists/{artistId}` | 管理员 | — | 修正艺人名称与简介。 |
| 管理 · 曲库维护 | `POST` | `/api/v1/admin/tracks/backfill-metadata` | 管理员 | — | 批量回填已发布但缺失元数据（流派/专辑/年份/封面）的曲目 |
| 管理 · 运营精选 | `POST` | `/api/v1/admin/collections` | 管理员 | — | 创建运营精选合集（标题 + 曲目数组 + 可选封面/描述） |
| 管理 · 运营精选 | `PUT` | `/api/v1/admin/collections/{collectionId}` | 管理员 | — | 修改运营精选合集信息（标题/描述/发布态/封面，均为可选的部分更新） |
| 管理 · 运营精选 | `DELETE` | `/api/v1/admin/collections/{collectionId}` | 管理员 | — | 删除运营精选合集（含合集内曲目关联，曲目本体不受影响） |
| 管理 · 运营精选 | `PUT` | `/api/v1/admin/collections/{collectionId}/tracks` | 管理员 | — | 整体替换运营精选合集的曲目（按数组顺序重排） |
| 管理 · 邀请 | `POST` | `/api/v1/admin/invitations` | 管理员 | — | 创建一次性邀请并通过邮件发送注册链接。 |
| 管理 · 邀请 | `POST` | `/api/v1/admin/invitations/code` | 管理员 | — | 创建邀请码（无需邮件，直接返回可分享的邀请码/链接）。 |
| 管理 · 邀请 | `GET` | `/api/v1/admin/invitations` | 管理员 | — | 分页查询邀请及其投递状态。 |
| 管理 · 邀请 | `POST` | `/api/v1/admin/invitations/{invitationId}/resend` | 管理员 | — | 重新发送邀请：旧链接作废并签发新令牌。 |
| 管理 · 邀请 | `DELETE` | `/api/v1/admin/invitations/{invitationId}` | 管理员 | — | 撤销尚未兑换的邀请。 |
| 管理 · 批量导入 | `POST` | `/api/v1/admin/library/imports` | 管理员 | 管理员批量导入 | 管理员批量上传音频到隔离区（multipart，可选关联点歌需求），返回批次与逐文件项。 |
| 管理 · 批量导入 | `GET` | `/api/v1/admin/library/imports` | 管理员 | 管理员批量导入 | 分页查询导入批次及状态。 |
| 管理 · 批量导入 | `GET` | `/api/v1/admin/library/imports/{importId}` | 管理员 | 管理员批量导入 | 查询批次详情及各文件处理状态。 |
| 管理 · 批量导入 | `GET` | `/api/v1/admin/library/import-items/{itemId}` | 管理员 | 管理员批量导入 | 查看单文件解析、转码结果与元数据。 |
| 管理 · 批量导入 | `GET` | `/api/v1/admin/library/import-items/{itemId}/preview` | 管理员 | 管理员批量导入 | 试听隔离区文件或兼容预览版本（支持 Range）。 |
| 管理 · 批量导入 | `PATCH` | `/api/v1/admin/library/import-items/{itemId}` | 管理员 | 管理员批量导入 | 管理员审核修正元数据，或关联/解除点歌需求。 |
| 管理 · 批量导入 | `POST` | `/api/v1/admin/library/import-items/{itemId}/enrich` | 管理员 | 管理员批量导入 | 一键补全：对单个导入项即时调度「歌词链（内嵌→旁挂→LRCLIB→酷狗→咪咕）+ iTunes 元数据」两条外部源，只补空字段、不覆盖人工已填内容，补齐后立即落库并把更新后的条目返回给审核界面刷新表单 |
| 管理 · 批量导入 | `POST` | `/api/v1/admin/library/import-items/{itemId}/lyrics-file` | 管理员 | 管理员批量导入 | 上传歌词文件（.lrc / .txt）并解析内容写入导入条目 |
| 管理 · 批量导入 | `POST` | `/api/v1/admin/library/import-items/{itemId}/publish` | 管理员 | 管理员批量导入 | 发布导入项到曲库；关联点歌需求时校验标题/艺人匹配，不一致按策略阻断或要求强制确认。 |
| 管理 · 批量导入 | `DELETE` | `/api/v1/admin/library/import-items/{itemId}/song-request` | 管理员 | 管理员批量导入 | 解除导入项与点歌需求的关联。 |
| 管理 · 批量导入 | `GET` | `/api/v1/admin/library/import-items` | 管理员 | 管理员批量导入 | 管理端导入条目列表：按状态筛选（如 pending_publication 为待审核转公开的用户上传），附带批次上传者信息，供审核界面展示来源 |
| 管理 · 批量导入 | `DELETE` | `/api/v1/admin/library/import-items/{itemId}` | 管理员 | 管理员批量导入 | 丢弃导入项并安排隔离文件清理。 |
| 管理 · 批量导入 | `POST` | `/api/v1/admin/library/import-items/{itemId}/dismiss` | 管理员 | 管理员批量导入 | 软删除导入条目：仅对终态条目（已拒绝/已发布/处理失败）生效，置 IsDeleted=true 后列表查询自动过滤，记录本身不物理删除以便审计与恢复 |
| 管理 · 歌词 | `GET` | `/api/v1/admin/tracks/{trackId}/lyrics` | 管理员 | — | 查询指定曲目的全部歌词版本（管理端）。 |
| 管理 · 歌词 | `POST` | `/api/v1/admin/tracks/{trackId}/lyrics` | 管理员 | — | 为曲目创建歌词版本，可设为默认。 |
| 管理 · 歌词 | `PUT` | `/api/v1/admin/lyrics/{lyricId}` | 管理员 | — | 修改歌词内容/语言/格式/默认标记。 |
| 管理 · 歌词 | `DELETE` | `/api/v1/admin/lyrics/{lyricId}` | 管理员 | — | 删除歌词版本。 |
| 管理 · 点歌 | `POST` | `/api/v1/admin/song-requests/{requestId}/reopen` | 管理员 | 点歌 | 管理员重新开启一条已结束的需求：declined/withdrawn → open，清空当前处理备注 （拒绝/撤回原因保留在时间线中），写审计并通知提交人 |
| 管理 · 点歌 | `GET` | `/api/v1/admin/song-requests` | 管理员 | 点歌 | 管理员查询点歌队列，默认按有效投票数降序。 |
| 管理 · 点歌 | `PATCH` | `/api/v1/admin/song-requests/{requestId}` | 管理员 | 点歌 | 管理员修改需求状态/备注；状态流转受白名单约束，拒绝时必填原因。 |
| 管理 · 音源抓歌 | `GET` | `/api/v1/admin/source-downloads/search` | 管理员 | 音源抓歌 | 搜索音源平台：provider=all 时并行聚合各启用源，单源失败不拖垮整体（providerErrors 标注） |
| 管理 · 音源抓歌 | `POST` | `/api/v1/admin/source-downloads/tasks` | 管理员 | 音源抓歌 | 创建下载任务：按 (Provider, ProviderTrackId) 去重，重复项进 skippedDuplicates |
| 管理 · 音源抓歌 | `GET` | `/api/v1/admin/source-downloads/tasks` | 管理员 | 音源抓歌 | 任务列表：按状态筛选 + 分页（默认按创建时间倒序） |
| 管理 · 音源抓歌 | `GET` | `/api/v1/admin/source-downloads/tasks/{taskId}` | 管理员 | 音源抓歌 | 查询单个下载任务详情。 |
| 管理 · 音源抓歌 | `POST` | `/api/v1/admin/source-downloads/tasks/{taskId}/retry` | 管理员 | 音源抓歌 | 重试失败任务：仅 failed 状态可重试，重置为 queued 并重新投递 |
| 管理 · 存储治理 | `GET` | `/api/v1/admin/storage/audit` | 管理员 | — | 对账审计：输出孤儿/缺失/空目录明细（只读） |
| 管理 · 存储治理 | `GET` | `/api/v1/admin/storage/file` | 管理员 | — | 按存储键读取库内文件内容（仅管理员） |
| 管理 · 存储治理 | `POST` | `/api/v1/admin/storage/cleanup` | 管理员 | — | 回收审计发现的孤儿文件并清理空目录 |
| 管理 · 存储治理 | `DELETE` | `/api/v1/admin/tracks/{trackId}` | 管理员 | — | 物理删除曲目：数据库行与磁盘文件一并清理 |
| 管理 · 用户 | `GET` | `/api/v1/admin/users` | 管理员 | — | 管理端用户列表，支持关键词/角色/锁定状态筛选。 |
| 管理 · 用户 | `POST` | `/api/v1/admin/users/{id}/role` | 管理员 | — | 修改用户角色（user / tempadmin / admin，互斥替换） |
| 管理 · 用户 | `POST` | `/api/v1/admin/users/{id}/reset-password` | 管理员 | — | 管理员重置用户密码并解除锁定、吊销全部刷新令牌 |
| 管理 · 用户 | `POST` | `/api/v1/admin/users/{id}/lock` | 管理员 | — | 禁用账号（长期锁定 + 吊销刷新令牌） |
| 管理 · 用户 | `POST` | `/api/v1/admin/users/{id}/unlock` | 管理员 | — | 解除禁用/锁定并清零失败计数 |
| 管理 · 审计日志 | `GET` | `/api/v1/admin/logs/login` | 管理员 | — | 查询登录审计日志（成功/失败、客户端画像），支持分页与筛选。 |
| 管理 · 审计日志 | `GET` | `/api/v1/admin/logs/operations` | 管理员 | — | 查询管理员操作审计日志，支持分页与筛选。 |
| 管理 · 封面上传 | `POST` | `/api/v1/admin/artwork` | 管理员 | — | 管理员上传曲目/专辑封面（multipart 字段 `file`；JPEG/PNG/WebP/GIF，≤ 5 MB，魔术字节校验后落盘登记）。 |
| 系统 | `GET` | `/health` | 匿名 | — | 存活/就绪探针，固定返回 `{ "status": "ok" }`。 |
| 系统 | `GET` | `/api/v1/system/capabilities` | 匿名 | — | 读取客户端可见的功能开关、允许格式与上传大小上限。 |

## 分组说明与参数/响应

### 曲目（Tracks）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/tracks` | `q` string、`artistId` uuid、`albumId` uuid、`genre` string、`sort` string、`page` integer、`pageSize` integer | `PageResult<TrackDto>` | 分页查询已发布曲目；`q` 同时匹配曲目标题/专辑名/艺人名，可按艺人、专辑、风格筛选，`sort` 支持 `recent`/`title`/`duration`。 |
| `GET /api/v1/tracks/{trackId}` | `trackId` uuid\*（路径） | `TrackDto` | 曲目详情。未公开的私有曲目仅上传者本人与管理员可见，其他人按 404 处理。 |
| `GET /api/v1/tracks/{trackId}/stream` | `trackId` uuid\*（路径） | 文件流（200/206，支持 Range） | 播放曲目音频流，支持 HTTP Range 断点续播；匿名播放关闭时要求登录。 |

### 专辑（Albums）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/albums` | `q` string、`page` integer、`pageSize` integer | `PageResult<AlbumSummaryDto>` | 分页查询专辑摘要（含封面与艺人）。 |
| `GET /api/v1/albums/{albumId}` | `albumId` uuid\*（路径） | `AlbumDetailDto` | 专辑详情及曲目列表。 |

### 艺人（Artists）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/artists` | `q` string、`page` integer、`pageSize` integer | `PageResult<ArtistSummaryDto>` | 分页查询艺人摘要。 |
| `GET /api/v1/artists/{artistId}` | `artistId` uuid\*（路径） | `ArtistDetailDto` | 艺人详情（简介、专辑、曲目）。 |

### 风格（Genres）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/genres` | `page` integer、`pageSize` integer | `PageResult<string>` | 返回曲库中实际存在的风格列表（分页）。 |

### 运营精选（Collections）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/collections` | `page` integer、`pageSize` integer | `PageResult<CollectionSummaryDto>` | 分页查询已发布的运营精选合集。 |
| `GET /api/v1/collections/{collectionId}` | `collectionId` uuid\*（路径） | `CollectionDetailDto` | 精选合集详情及曲目列表。 |

### 首页（Music）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/music/home` | — | `MusicHomeDto` | 首页聚合数据：焦点曲目、精选合集、最近加入（结果有服务端缓存）。 |

### 封面图（Artwork）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/artwork/{artworkId}` | `artworkId` uuid\*（路径） | 文件流（200/206，支持 Range） | 按封面 Id 读取图片文件；仅当封面被已发布内容引用时可见。 |

### 认证（Authentication）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/auth/invitations/validate` | —<br>请求体：`ValidateInvitationRequest` | `InvitationValidationResponse` | 校验邀请令牌，返回脱敏邮箱与过期时间。 |
| `POST /api/v1/auth/register` | —<br>请求体：`RegisterRequest` | 201 Created（JSON） | 使用邀请令牌 + 密码 + 显示名注册；无有效邀请一律拒绝。 |
| `POST /api/v1/auth/login` | —<br>请求体：`LoginRequest` | 200 JSON | 账号密码登录，返回访问令牌与刷新令牌。 |
| `POST /api/v1/auth/refresh` | —<br>请求体：`RefreshTokenRequest` | `AuthTokenResponse` | 刷新令牌轮换（凭 refresh token 换发全新令牌对） |
| `POST /api/v1/auth/logout` | —<br>请求体：`RefreshTokenRequest` | 204 No Content | 退出登录并撤销当前刷新令牌。 |
| `GET /api/v1/me` | — | 200 JSON（内联字段：`id`、`email`、`displayName`、`roles`、`avatarUrl`） | 读取当前登录用户资料。 |

### 个人资料（Profile）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `PATCH /api/v1/me` | —<br>请求体：`UpdateDisplayNameRequest` | `CurrentUserDto` | 修改展示昵称（1-100 字符） |
| `POST /api/v1/me/avatar` | —<br>请求体：`multipart/form-data`（`avatar`（jpg/png/webp，≤ 2 MB）） | `AvatarUrlDto` | 上传/更换头像（multipart/form-data，字段名 avatar） |
| `GET /api/v1/me/avatar` | — | 文件流（200/206，支持 Range） | 读取当前用户头像（匿名不可见，必须登录且头像存在） |
| `DELETE /api/v1/me/avatar` | — | 204 No Content | 删除当前用户头像（幂等：无头像也返回 204） |
| `POST /api/v1/me/password` | —<br>请求体：`ChangePasswordRequest` | 204 No Content | 修改登录密码（需提供当前密码） |
| `GET /api/v1/me/theme` | — | `UserThemeDto` | 读取当前用户自定义主题配置（未配置时返回全空配置） |
| `POST /api/v1/me/theme/wallpaper` | —<br>请求体：`multipart/form-data`（`wallpaper`（jpg/png/webp，≤ 8 MB）） | `UserThemeDto` | 上传/更换壁纸（multipart/form-data，字段名 wallpaper） |
| `GET /api/v1/me/theme/wallpaper` | — | 文件流（200/206，支持 Range） | 读取当前用户壁纸（登录用户本人的私人图片） |
| `PUT /api/v1/me/theme` | —<br>请求体：`UpdateUserThemeRequest` | `UserThemeDto` | 更新主题配置（色板、遮罩深浅、启用开关） |
| `DELETE /api/v1/me/theme` | — | 204 No Content | 删除自定义主题（壁纸文件 + 配置行，幂等：无配置也返回 204） |

### 我的曲库与歌单（My Library）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/me/playlist-imports/parse` | —<br>请求体：`PlaylistImportParseRequest` | `PlaylistImportParseResponse` | 解析歌单并预览（逐曲标记是否已匹配曲库） |
| `POST /api/v1/me/playlist-imports/import` | —<br>请求体：`PlaylistImportRequest` | `PlaylistImportResponse` | 导入歌单：立即生成新歌单，未匹配曲目后台静默下载补齐 |
| `GET /api/v1/me/favorites` | `page` integer、`pageSize` integer | `PageResult<TrackDto>` | 分页查询我的收藏曲目。 |
| `PUT /api/v1/me/favorites/{trackId}` | `trackId` uuid\*（路径） | 204 No Content | 收藏曲目（幂等）。 |
| `DELETE /api/v1/me/favorites/{trackId}` | `trackId` uuid\*（路径） | 204 No Content | 取消收藏（幂等）。 |
| `GET /api/v1/me/playlists` | `page` integer、`pageSize` integer | `PageResult<PlaylistSummaryDto>` | 分页查询我的歌单。 |
| `POST /api/v1/me/playlists` | —<br>请求体：`CreatePlaylistRequest` | 201 Created `PlaylistSummaryDto` | 创建歌单，可绑定已上传封面。 |
| `GET /api/v1/me/playlists/{playlistId}` | `playlistId` uuid\*（路径） | `PlaylistDetailDto` | 歌单详情及曲目；导入产生的待补齐曲目通过 `pendingTracks` 返回。 |
| `DELETE /api/v1/me/playlists/{playlistId}/pending/{pendingId}` | `playlistId` uuid\*（路径）、`pendingId` uuid\*（路径） | 204 No Content | 删除歌单中的一条待补齐（pending）曲目 |
| `PATCH /api/v1/me/playlists/{playlistId}` | `playlistId` uuid\*（路径）<br>请求体：`UpdatePlaylistRequest` | 204 No Content | 修改歌单名称/描述/封面；`clearArtwork` 显式清空封面。 |
| `DELETE /api/v1/me/playlists/{playlistId}` | `playlistId` uuid\*（路径） | 204 No Content | 删除歌单。 |
| `PUT /api/v1/me/playlists/{playlistId}/tracks/{trackId}` | `playlistId` uuid\*（路径）、`trackId` uuid\*（路径） | 204 No Content | 向歌单加入曲目（幂等）。 |
| `DELETE /api/v1/me/playlists/{playlistId}/tracks/{trackId}` | `playlistId` uuid\*（路径）、`trackId` uuid\*（路径） | 204 No Content | 从歌单移出曲目。 |

### 收听记录与反馈（My Listening）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/me/play-events` | —<br>请求体：`PlayEventRequest` | 204 No Content | 上报一条收听事件（play/complete/skip） |
| `GET /api/v1/me/feedback` | — | `array<UserFeedbackDto>` | 查询我对曲目的反馈列表（like/dislike/not_interested）。 |
| `PUT /api/v1/me/feedback/{trackId}` | `trackId` uuid\*（路径）<br>请求体：`FeedbackRequest` | 204 No Content | 设置/更新对某曲目的反馈。 |
| `DELETE /api/v1/me/feedback/{trackId}` | `trackId` uuid\*（路径） | 204 No Content | 清除对某曲目的反馈。 |
| `GET /api/v1/me/history` | `page` integer、`pageSize` integer | `PageResult<ListeningHistoryItemDto>` | 分页查询我的收听历史。 |

### 用户上传与我的曲库（My Uploads）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/me/uploads` | —<br>请求体：`multipart/form-data`（`files`（可多个音频；同名 `.lrc`/`.txt` 作为旁挂歌词）） | 202 Accepted `UserUploadResponseDto` | 上传本地音频（可同时携带同名 .lrc 旁挂歌词） |
| `POST /api/v1/me/uploads/precheck` | —<br>请求体：`UploadPrecheckRequestDto` | `UploadPrecheckResponseDto` | 上传前批量预检：客户端把本地文件指纹（标题/艺人/时长）发给服务端，返回曲库中已存在的曲目，命中者可跳过上传、直接建曲库引用 |
| `GET /api/v1/me/library` | `page` integer、`pageSize` integer | `PageResult<UserLibraryItemDto>` | 我的曲库：列出当前用户引用的全部曲目（含私有与公开） |
| `POST /api/v1/me/library` | —<br>请求体：`AddToLibraryRequestDto` | 204 No Content | 批量加入曲库：预检命中公共曲库的歌曲可直接引用（无需上传文件） |
| `DELETE /api/v1/me/library/{trackId}` | `trackId` uuid\*（路径） | 204 No Content | 从我的曲库移除引用（不影响曲目本体与其他用户的引用） |

### 站内信（Notifications）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/me/notifications` | `unreadOnly` boolean、`page` integer、`pageSize` integer | `PageResult<NotificationDto>` | 分页拉取当前用户的消息（可选只看未读） |
| `GET /api/v1/me/notifications/unread-count` | — | `UnreadNotificationCountDto` | 未读站内信数量（供角标轮询）。 |
| `POST /api/v1/me/notifications/{notificationId}/read` | `notificationId` uuid\*（路径） | 204 No Content | 把单条消息标记为已读（幂等） |
| `POST /api/v1/me/notifications/read-all` | — | 204 No Content | 把当前用户全部未读消息标记为已读 |
| `DELETE /api/v1/me/notifications/{notificationId}` | `notificationId` uuid\*（路径） | 204 No Content | 删除单条站内信。 |

### 评论（Comments）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/tracks/{trackId}/comments` | `trackId` uuid\*（路径）、`page` integer、`pageSize` integer | `PageResult<TrackCommentDto>` | 分页查询曲目评论（含嵌套回复与点赞数），匿名可读。 |
| `POST /api/v1/tracks/{trackId}/comments` | `trackId` uuid\*（路径）<br>请求体：`CreateCommentRequest` | 201 Created `TrackCommentDto` | 发表评论或回复（登录）。 |
| `DELETE /api/v1/comments/{commentId}` | `commentId` uuid\*（路径） | 204 No Content | 删除本人评论；管理员可删除任意评论。 |
| `PUT /api/v1/comments/{commentId}/like` | `commentId` uuid\*（路径） | `CommentLikeResult` | 点赞评论（幂等）。 |
| `DELETE /api/v1/comments/{commentId}/like` | `commentId` uuid\*（路径） | `CommentLikeResult` | 取消点赞评论（幂等）。 |

### 关注（Follow）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/me/following` | `page` integer、`pageSize` integer | `PageResult<UserSummaryDto>` | 分页查询我的关注列表。 |
| `GET /api/v1/me/followers` | `page` integer、`pageSize` integer | `PageResult<UserSummaryDto>` | 分页查询我的粉丝列表。 |
| `PUT /api/v1/me/following/{targetUserId}` | `targetUserId` uuid\*（路径） | 204 No Content | 关注某用户（幂等）。 |
| `DELETE /api/v1/me/following/{targetUserId}` | `targetUserId` uuid\*（路径） | 204 No Content | 取消关注某用户（幂等）。 |
| `GET /api/v1/users/{targetUserId}/follow-summary` | `targetUserId` uuid\*（路径） | `FollowStatusDto` | 查询指定用户的关注状态与粉丝/关注数。 |

### 点歌（Song Requests）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/song-requests` | `sort` string、`page` integer、`pageSize` integer | `PageResult<SongRequestDto>` | 公开点歌列表，支持按状态/排序分页；匿名可读。 |
| `GET /api/v1/song-requests/{requestId}` | `requestId` uuid\*（路径） | `SongRequestDto` | 点歌需求详情与当前状态。 |
| `POST /api/v1/song-requests` | —<br>请求体：`CreateSongRequestRequest` | 201 Created（JSON） | 登录用户提交点歌需求；服务端检查是否已有匹配曲目或重复需求。 |
| `PUT /api/v1/song-requests/{requestId}/votes/me` | `requestId` uuid\*（路径） | 204 No Content | 为需求点赞；每用户每需求一票，重复调用幂等（204）。点赞不可取消。 |
| `GET /api/v1/me/song-requests` | `page` integer、`pageSize` integer | `PageResult<SongRequestDto>` | 查询我创建或投过票的需求。 |
| `DELETE /api/v1/song-requests/{requestId}` | `requestId` uuid\*（路径） | 204 No Content | 撤回点歌需求（软撤回，仅 open 阶段；管理员可在 open/planned 阶段代为撤回）。 |
| `GET /api/v1/song-requests/{requestId}/timeline` | `requestId` uuid\*（路径） | `array<SongRequestTimelineItemDto>` | 需求处理时间线：按时间正序返回状态事件，供卡片展开查看「提交 → 纳入计划 → 处理中 → 已入库」 |

### 每日推荐（Recommendations）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/recommendations/daily` | `limit` integer | `DailyRecommendationDto` | 每日推荐（默认 30 首、互不重复、当日稳定） |

### 歌词（Lyrics）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/tracks/{trackId}/lyrics` | `trackId` uuid\*（路径） | `array<LyricDto>` | 读取曲目歌词列表（公开，匿名可用）。 |
| `GET /api/v1/tracks/{trackId}/lyrics/default` | `trackId` uuid\*（路径） | `LyricDto` | 读取曲目的默认歌词。 |

### 管理 · 曲库维护（Admin Catalog）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/admin/tracks` | `q` string、`artistId` uuid、`albumId` uuid、`missingMetadata` boolean、`duplicates` boolean、`page` integer、`pageSize` integer | `PageResult<TrackDto>` | 管理端曲目列表，支持按信息缺失、重复项、专辑、艺人筛选。 |
| `PATCH /api/v1/admin/tracks/{trackId}` | `trackId` uuid\*（路径）<br>请求体：`UpdateAdminTrackRequest` | `TrackDto` | 修正已发布曲目的元数据（艺人/风格按完整集合替换）。 |
| `PATCH /api/v1/admin/albums/{albumId}` | `albumId` uuid\*（路径）<br>请求体：`UpdateAdminAlbumRequest` | `AlbumSummaryDto` | 修正专辑名、艺人、年份与封面关联。 |
| `PATCH /api/v1/admin/artists/{artistId}` | `artistId` uuid\*（路径）<br>请求体：`UpdateAdminArtistRequest` | `ArtistSummaryDto` | 修正艺人名称与简介。 |
| `POST /api/v1/admin/tracks/backfill-metadata` | — | 200 JSON（内联字段：`total`、`success`、`skipped`、`failed`、`newArtwork`、`newAlbum`、`newGenre`） | 批量回填已发布但缺失元数据（流派/专辑/年份/封面）的曲目 |

### 管理 · 运营精选（Admin Collections）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/admin/collections` | —<br>请求体：`CreateCollectionRequest` | 201 Created `CollectionSummaryDto` | 创建运营精选合集（标题 + 曲目数组 + 可选封面/描述） |
| `PUT /api/v1/admin/collections/{collectionId}` | `collectionId` uuid\*（路径）<br>请求体：`UpdateCollectionRequest` | `CollectionSummaryDto` | 修改运营精选合集信息（标题/描述/发布态/封面，均为可选的部分更新） |
| `DELETE /api/v1/admin/collections/{collectionId}` | `collectionId` uuid\*（路径） | 204 No Content | 删除运营精选合集（含合集内曲目关联，曲目本体不受影响） |
| `PUT /api/v1/admin/collections/{collectionId}/tracks` | `collectionId` uuid\*（路径）<br>请求体：`ReplaceCollectionTracksRequest` | `CollectionSummaryDto` | 整体替换运营精选合集的曲目（按数组顺序重排） |

### 管理 · 邀请（Admin Invitations）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/admin/invitations` | —<br>请求体：`CreateInvitationRequest` | 201 Created `InvitationDto` | 创建一次性邀请并通过邮件发送注册链接。 |
| `POST /api/v1/admin/invitations/code` | — | 201 Created `InvitationCodeResponse` | 创建邀请码（无需邮件，直接返回可分享的邀请码/链接）。 |
| `GET /api/v1/admin/invitations` | `status` string、`page` integer、`pageSize` integer | `PageResult<InvitationDto>` | 分页查询邀请及其投递状态。 |
| `POST /api/v1/admin/invitations/{invitationId}/resend` | `invitationId` uuid\*（路径） | 201 Created `InvitationDto` | 重新发送邀请：旧链接作废并签发新令牌。 |
| `DELETE /api/v1/admin/invitations/{invitationId}` | `invitationId` uuid\*（路径） | 204 No Content | 撤销尚未兑换的邀请。 |

### 管理 · 批量导入（Admin Library Import）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/admin/library/imports` | —<br>请求体：`multipart/form-data`（`files`（可多个音频）+ 可选 `songRequestId`） | 202 Accepted `LibraryImportBatchDto` | 管理员批量上传音频到隔离区（multipart，可选关联点歌需求），返回批次与逐文件项。 |
| `GET /api/v1/admin/library/imports` | `status` string、`page` integer、`pageSize` integer | `PageResult<LibraryImportBatchDto>` | 分页查询导入批次及状态。 |
| `GET /api/v1/admin/library/imports/{importId}` | `importId` uuid\*（路径） | `LibraryImportBatchDto` | 查询批次详情及各文件处理状态。 |
| `GET /api/v1/admin/library/import-items/{itemId}` | `itemId` uuid\*（路径） | `LibraryImportItemDto` | 查看单文件解析、转码结果与元数据。 |
| `GET /api/v1/admin/library/import-items/{itemId}/preview` | `itemId` uuid\*（路径） | 文件流（200/206，支持 Range） | 试听隔离区文件或兼容预览版本（支持 Range）。 |
| `PATCH /api/v1/admin/library/import-items/{itemId}` | `itemId` uuid\*（路径）<br>请求体：`UpdateImportItemRequest` | `LibraryImportItemDto` | 管理员审核修正元数据，或关联/解除点歌需求。 |
| `POST /api/v1/admin/library/import-items/{itemId}/enrich` | `itemId` uuid\*（路径） | `EnrichImportItemResultDto` | 一键补全：对单个导入项即时调度「歌词链（内嵌→旁挂→LRCLIB→酷狗→咪咕）+ iTunes 元数据」两条外部源，只补空字段、不覆盖人工已填内容，补齐后立即落库并把更新后的条目返回给审核界面刷新表单 |
| `POST /api/v1/admin/library/import-items/{itemId}/lyrics-file` | `itemId` uuid\*（路径）<br>请求体：`multipart/form-data`（`file`（`.lrc`/`.txt`）） | `LibraryImportItemDto` | 上传歌词文件（.lrc / .txt）并解析内容写入导入条目 |
| `POST /api/v1/admin/library/import-items/{itemId}/publish` | `itemId` uuid\*（路径）、`forceFulfill` boolean、`overrideReason` string | `PublishImportItemResultDto` | 发布导入项到曲库；关联点歌需求时校验标题/艺人匹配，不一致按策略阻断或要求强制确认。 |
| `DELETE /api/v1/admin/library/import-items/{itemId}/song-request` | `itemId` uuid\*（路径） | 204 No Content | 解除导入项与点歌需求的关联。 |
| `GET /api/v1/admin/library/import-items` | `status` string、`page` integer、`pageSize` integer | `PageResult<LibraryImportItemWithUploaderDto>` | 管理端导入条目列表：按状态筛选（如 pending_publication 为待审核转公开的用户上传），附带批次上传者信息，供审核界面展示来源 |
| `DELETE /api/v1/admin/library/import-items/{itemId}` | `itemId` uuid\*（路径） | 204 No Content | 丢弃导入项并安排隔离文件清理。 |
| `POST /api/v1/admin/library/import-items/{itemId}/dismiss` | `itemId` uuid\*（路径） | 204 No Content | 软删除导入条目：仅对终态条目（已拒绝/已发布/处理失败）生效，置 IsDeleted=true 后列表查询自动过滤，记录本身不物理删除以便审计与恢复 |

### 管理 · 歌词（Admin Lyrics）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/admin/tracks/{trackId}/lyrics` | `trackId` uuid\*（路径） | `array<LyricDto>` | 查询指定曲目的全部歌词版本（管理端）。 |
| `POST /api/v1/admin/tracks/{trackId}/lyrics` | `trackId` uuid\*（路径）<br>请求体：`CreateLyricRequest` | `LyricDto` | 为曲目创建歌词版本，可设为默认。 |
| `PUT /api/v1/admin/lyrics/{lyricId}` | `lyricId` uuid\*（路径）<br>请求体：`UpdateLyricRequest` | `LyricDto` | 修改歌词内容/语言/格式/默认标记。 |
| `DELETE /api/v1/admin/lyrics/{lyricId}` | `lyricId` uuid\*（路径） | 204 No Content | 删除歌词版本。 |

### 管理 · 点歌（Admin Song Requests）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/admin/song-requests/{requestId}/reopen` | `requestId` uuid\*（路径）<br>请求体：`ReopenSongRequestRequest` | `SongRequestDto` | 管理员重新开启一条已结束的需求：declined/withdrawn → open，清空当前处理备注 （拒绝/撤回原因保留在时间线中），写审计并通知提交人 |
| `GET /api/v1/admin/song-requests` | `status` string、`sort` string、`page` integer、`pageSize` integer | `PageResult<SongRequestDto>` | 管理员查询点歌队列，默认按有效投票数降序。 |
| `PATCH /api/v1/admin/song-requests/{requestId}` | `requestId` uuid\*（路径）<br>请求体：`UpdateSongRequestRequest` | `SongRequestDto` | 管理员修改需求状态/备注；状态流转受白名单约束，拒绝时必填原因。 |

### 管理 · 音源抓歌（Admin Source Download）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/admin/source-downloads/search` | `query` string、`provider` string、`limit` integer | `SourceSearchResponse` | 搜索音源平台：provider=all 时并行聚合各启用源，单源失败不拖垮整体（providerErrors 标注） |
| `POST /api/v1/admin/source-downloads/tasks` | —<br>请求体：`CreateSourceDownloadTasksRequest` | 202 Accepted `CreateSourceDownloadTasksResultDto` | 创建下载任务：按 (Provider, ProviderTrackId) 去重，重复项进 skippedDuplicates |
| `GET /api/v1/admin/source-downloads/tasks` | `status` string、`page` integer、`pageSize` integer | `ExternalDownloadTaskListDto` | 任务列表：按状态筛选 + 分页（默认按创建时间倒序） |
| `GET /api/v1/admin/source-downloads/tasks/{taskId}` | `taskId` uuid\*（路径） | `ExternalDownloadTaskDto` | 查询单个下载任务详情。 |
| `POST /api/v1/admin/source-downloads/tasks/{taskId}/retry` | `taskId` uuid\*（路径） | `ExternalDownloadTaskDto` | 重试失败任务：仅 failed 状态可重试，重置为 queued 并重新投递 |

### 管理 · 存储治理（Admin Storage）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/admin/storage/audit` | — | `StorageAuditDto` | 对账审计：输出孤儿/缺失/空目录明细（只读） |
| `GET /api/v1/admin/storage/file` | `key` string | 文件流（200/206，支持 Range） | 按存储键读取库内文件内容（仅管理员） |
| `POST /api/v1/admin/storage/cleanup` | — | `StorageCleanupDto` | 回收审计发现的孤儿文件并清理空目录 |
| `DELETE /api/v1/admin/tracks/{trackId}` | `trackId` uuid\*（路径） | `TrackDeleteResultDto` | 物理删除曲目：数据库行与磁盘文件一并清理 |

### 管理 · 用户（Admin Users）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/admin/users` | `query` string、`page` integer、`pageSize` integer | `PageResult<AdminUserItem>` | 管理端用户列表，支持关键词/角色/锁定状态筛选。 |
| `POST /api/v1/admin/users/{id}/role` | `id` uuid\*（路径）<br>请求体：`AdminSetRoleRequest` | 200 JSON（内联字段：`role`、`tempAdminUntilUtc`） | 修改用户角色（user / tempadmin / admin，互斥替换） |
| `POST /api/v1/admin/users/{id}/reset-password` | `id` uuid\*（路径）<br>请求体：`AdminResetPasswordRequest` | 200 JSON（内联字段：`id`、`reset`） | 管理员重置用户密码并解除锁定、吊销全部刷新令牌 |
| `POST /api/v1/admin/users/{id}/lock` | `id` uuid\*（路径） | 200 JSON（内联字段：`id`、`locked`） | 禁用账号（长期锁定 + 吊销刷新令牌） |
| `POST /api/v1/admin/users/{id}/unlock` | `id` uuid\*（路径） | 200 JSON（内联字段：`id`、`locked`） | 解除禁用/锁定并清零失败计数 |

### 管理 · 审计日志（Admin Logs）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /api/v1/admin/logs/login` | `result` string、`query` string、`page` integer、`pageSize` integer | `PageResult<LoginAuditItem>` | 查询登录审计日志（成功/失败、客户端画像），支持分页与筛选。 |
| `GET /api/v1/admin/logs/operations` | `query` string、`page` integer、`pageSize` integer | `PageResult<OperationAuditItem>` | 查询管理员操作审计日志，支持分页与筛选。 |

### 管理 · 封面上传（Admin Artwork）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `POST /api/v1/admin/artwork` | —<br>请求体：`multipart/form-data`（`file`（jpeg/png/webp/gif，≤ 5 MB）） | `ArtworkUploadResultDto` | 管理员上传曲目/专辑封面（multipart 字段 `file`；JPEG/PNG/WebP/GIF，≤ 5 MB，魔术字节校验后落盘登记）。 |

### 系统（System）

| 端点 | 参数 / 请求体 | 成功响应 | 说明 |
|---|---|---|---|
| `GET /health` | — | 200 JSON（内联字段：`status`） | 存活/就绪探针，固定返回 `{ "status": "ok" }`。 |
| `GET /api/v1/system/capabilities` | — | `SystemCapabilitiesDto` | 读取客户端可见的功能开关、允许格式与上传大小上限。 |

> `\*` 表示必填参数；未列出的查询参数均为可选。请求体 DTO 字段见附录 A。
> 所有端点都可能返回统一错误响应：`400` 参数错误、`401` 未认证、`403` 无权限、`404` 不存在/不可见、`409` 状态冲突、`429` 限流、`500` 内部错误（按端点实际情况裁剪）。

## 附录 A：请求/响应 DTO

以下为后端 `public sealed record` 契约（`ShineMusic.Model/Contracts` 与控制器内就近定义），JSON 字段为 `camelCase`。类型列中 `?` 表示该字段可为 `null`；`PageResult<T>` 为通用分页外壳 `{ items, page, pageSize, total }`。

#### `ShineMusicApi/ShineMusic.Api/Controllers/AuthController.cs`

**InvitationValidationResponse**

| 字段 | 类型 |
|---|---|
| `email` | `string` |
| `channel` | `string` |
| `expiresAt` | `datetime` |

**LoginRequest**

| 字段 | 类型 |
|---|---|
| `email` | `string?` |
| `username` | `string?` |
| `password` | `string` |

**RefreshTokenRequest**

| 字段 | 类型 |
|---|---|
| `refreshToken` | `string?` |

**RegisterRequest**

| 字段 | 类型 |
|---|---|
| `invitationToken` | `string?` |
| `password` | `string` |
| `displayName` | `string` |
| `email` | `string?` |

**ValidateInvitationRequest**

| 字段 | 类型 |
|---|---|
| `invitationToken` | `string?` |

#### `ShineMusicApi/ShineMusic.Api/Controllers/CatalogManagementController.cs`

**UpdateAdminAlbumRequest**

| 字段 | 类型 |
|---|---|
| `title` | `string?` |
| `artists` | `array<string>?` |
| `releaseYear` | `integer?` |
| `artworkId` | `uuid?` |

**UpdateAdminArtistRequest**

| 字段 | 类型 |
|---|---|
| `name` | `string?` |
| `biography` | `string?` |

**UpdateAdminTrackRequest**

| 字段 | 类型 |
|---|---|
| `title` | `string?` |
| `artists` | `array<string>?` |
| `albumId` | `uuid?` |
| `trackNumber` | `integer?` |
| `discNumber` | `integer?` |
| `releaseYear` | `integer?` |
| `genres` | `array<string>?` |
| `artworkId` | `uuid?` |

#### `ShineMusicApi/ShineMusic.Api/Controllers/CollectionManagementController.cs`

**CreateCollectionRequest**

| 字段 | 类型 |
|---|---|
| `title` | `string` |
| `description` | `string?` |
| `trackIds` | `array<string>` |
| `artworkId` | `string?` |

**ReplaceCollectionTracksRequest**

| 字段 | 类型 |
|---|---|
| `trackIds` | `array<string>` |

**UpdateCollectionRequest**

| 字段 | 类型 |
|---|---|
| `title` | `string?` |
| `description` | `string?` |
| `isPublished` | `boolean?` |
| `artworkId` | `string?` |

#### `ShineMusicApi/ShineMusic.Api/Controllers/InvitationController.cs`

**CreateInvitationRequest**

| 字段 | 类型 |
|---|---|
| `email` | `string?` |

**InvitationCodeResponse**

| 字段 | 类型 |
|---|---|
| `code` | `string` |
| `invitation` | `InvitationDto` |

**InvitationDto**

| 字段 | 类型 |
|---|---|
| `id` | `uuid` |
| `email` | `string?` |
| `status` | `string` |
| `createdAt` | `datetime` |
| `expiresAt` | `datetime` |
| `sentAt` | `datetime?` |
| `redeemedAt` | `datetime?` |

#### `ShineMusicApi/ShineMusic.Api/Controllers/PlaylistImportController.cs`

**PlaylistImportParseRequest** —— 解析预览请求体

| 字段 | 类型 | 说明 |
|---|---|---|
| `url` | `string?` | 外部平台歌单链接。 |

**PlaylistImportParseResponse** —— 解析预览响应

| 字段 | 类型 |
|---|---|
| `platform` | `string` |
| `name` | `string` |
| `coverUrl` | `string?` |
| `total` | `integer` |
| `tracks` | `array<PlaylistImportPreviewTrackDto>` |

**PlaylistImportPreviewTrackDto** —— 解析预览响应中的单条曲目

| 字段 | 类型 |
|---|---|
| `title` | `string` |
| `artists` | `array<string>` |
| `albumTitle` | `string?` |
| `durationMs` | `integer?` |
| `matched` | `boolean` |
| `matchedTrackId` | `uuid?` |

**PlaylistImportRequest** —— 导入请求体

| 字段 | 类型 | 说明 |
|---|---|---|
| `url` | `string?` | 外部平台歌单链接。 |
| `name` | `string?` | 新歌单名称（留空则用原歌单名）。 |

**PlaylistImportResponse** —— 导入响应

| 字段 | 类型 | 说明 |
|---|---|---|
| `playlistId` | `uuid` | 新歌单 Id。 |
| `total` | `integer` | 原歌单曲目总数。 |
| `matched` | `integer` | 已匹配曲库直接入歌单的数量。 |
| `pending` | `integer` | 进入待补齐（静默下载）的数量。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/AdminUserDtos.cs`

**AdminResetPasswordRequest** —— 管理员重置用户密码入参

| 字段 | 类型 |
|---|---|
| `newPassword` | `string` |

**AdminSetRoleRequest** —— 管理员修改用户角色入参：user 普通用户 / tempadmin 临时管理员（3 天）/ admin 管理员

| 字段 | 类型 |
|---|---|
| `role` | `string` |

**AdminUserItem** —— 管理端用户列表条目（含角色与锁定状态；IsLockedOut 表示当前处于锁定/禁用状态）

| 字段 | 类型 |
|---|---|
| `id` | `uuid` |
| `email` | `string?` |
| `userName` | `string?` |
| `displayName` | `string` |
| `roles` | `array<string>` |
| `tempAdminUntilUtc` | `datetime?` |
| `createdAtUtc` | `datetime` |
| `lockoutEnabled` | `boolean` |
| `lockoutEnd` | `datetime?` |
| `accessFailedCount` | `integer` |
| `isLockedOut` | `boolean` |

**LoginAuditItem** —— 登录审计条目（管理端日志页展示）

| 字段 | 类型 |
|---|---|
| `id` | `uuid` |
| `login` | `string` |
| `userId` | `uuid?` |
| `result` | `string` |
| `reason` | `string` |
| `ip` | `string` |
| `clientType` | `string?` |
| `deviceInfo` | `string?` |
| `createdAtUtc` | `datetime` |

**OperationAuditItem** —— 操作审计条目（管理端日志页展示）

| 字段 | 类型 |
|---|---|
| `id` | `uuid` |
| `actorUserId` | `uuid` |
| `actorName` | `string?` |
| `action` | `string` |
| `resourceType` | `string` |
| `resourceId` | `string` |
| `detailsJson` | `string?` |
| `ip` | `string?` |
| `clientType` | `string?` |
| `deviceInfo` | `string?` |
| `createdAtUtc` | `datetime` |

#### `ShineMusicApi/ShineMusic.Model/Contracts/CatalogDtos.cs`

**AlbumDetailDto** —— 专辑详情页数据

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 专辑唯一标识。 |
| `title` | `string` | 专辑标题。 |
| `releaseYear` | `integer?` | 发行年份；未知时为 null。 |
| `artists` | `array<ArtistSummaryDto>` | 专辑艺人列表（合辑可能包含多位艺人）。 |
| `artworkUrl` | `string?` | 专辑封面地址；无封面时为 null。 |
| `tracks` | `array<TrackDto>` | 专辑包含的曲目列表。 |

**AlbumSummaryDto** —— 专辑摘要信息，用于嵌套展示

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 专辑唯一标识。 |
| `title` | `string` | 专辑标题。 |

**ApiErrorDetail** —— 单条错误详情，错误码稳定可被客户端程序化处理，消息面向最终用户

| 字段 | 类型 | 说明 |
|---|---|---|
| `code` | `string` | 机器可读的稳定错误码（如 authentication_required、permission_denied、internal_error）。 |
| `message` | `string` | 面向用户的错误描述文案。 |
| `traceId` | `string` | 请求跟踪标识，与服务端日志关联，便于问题排查。 |

**ApiErrorResponse** —— 所有 API 错误响应的统一外层结构，与成功响应的 JSON 形态区分

| 字段 | 类型 | 说明 |
|---|---|---|
| `error` | `ApiErrorDetail` | 错误详情。 |

**ArtistDetailDto** —— 艺人详情页数据

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 艺人唯一标识。 |
| `name` | `string` | 艺人名称。 |
| `biography` | `string?` | 艺人简介文案；未维护时为 null。 |
| `albums` | `array<AlbumSummaryDto>` | 该艺人关联的专辑摘要列表。 |
| `tracks` | `array<TrackDto>` | 该艺人关联的曲目列表。 |

**ArtistSummaryDto** —— 艺人摘要信息，用于嵌套在曲目、专辑等对象中

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 艺人唯一标识。 |
| `name` | `string` | 艺人名称。 |

**AudioDto** —— 可播放音频流的技术信息，客户端据此选择播放地址与解码方式

| 字段 | 类型 | 说明 |
|---|---|---|
| `container` | `string` | 音频容器格式标识（如 m4a、flac）。 |
| `codec` | `string` | 音频编码格式标识（如 aac、flac）。 |
| `bitrateKbps` | `integer?` | 音频码率，单位 kbps；原始文件信息缺失时可能为 null。 |

**CollectionDetailDto** —— 合集详情，包含完整曲目列表

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 合集唯一标识。 |
| `title` | `string` | 合集标题。 |
| `description` | `string?` | 合集描述；未填写时为 null。 |
| `artworkUrl` | `string?` | 合集封面地址；无封面时为 null。 |
| `tracks` | `array<TrackDto>` | 合集内的曲目列表。 |

**CollectionSummaryDto** —— 合集（运营精选集或歌单）的摘要信息

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 合集唯一标识。 |
| `title` | `string` | 合集标题。 |
| `description` | `string?` | 合集描述；未填写时为 null。 |
| `artworkUrl` | `string?` | 合集封面地址；无封面时为 null。 |
| `trackCount` | `integer` | 合集内曲目数量（冗余计数，避免列表场景再查明细）。 |

**CreateLyricRequest** —— 管理端创建歌词请求体

| 字段 | 类型 | 说明 |
|---|---|---|
| `language` | `string?` | 语言代码；可为空。 |
| `format` | `string?` | 歌词格式：plain 或 lrc，默认 plain。 |
| `content` | `string` | 歌词正文，不能为空。 |
| `isDefault` | `boolean` | 是否设为该曲目默认歌词。 |

**LyricDto** —— 歌词展示信息，前端播放时按需拉取对应语言/格式的歌词

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 歌词唯一标识。 |
| `trackId` | `uuid` | 所属曲目 Id。 |
| `language` | `string?` | 语言代码（如 zh-CN、en）；未知时为 null。 |
| `format` | `string` | 歌词格式：plain（纯文本）或 lrc（带时间轴）。 |
| `content` | `string` | 歌词正文内容。 |
| `isDefault` | `boolean` | 是否为该曲目默认展示的歌词。 |
| `updatedAtUtc` | `datetime` | 最后更新时间（UTC）。 |

**MusicHomeDto** —— 音乐首页的聚合数据，一次请求返回首页各区块内容

| 字段 | 类型 | 说明 |
|---|---|---|
| `featured` | `TrackDto?` | 首页主推/焦点曲目；未配置时为 null。 |
| `collections` | `array<CollectionSummaryDto>` | 精选合集摘要列表。 |
| `recentlyAdded` | `PageResult<TrackDto>` | 最近入库曲目的分页结果。 |

**PageResult**<T> —— 分页查询结果的统一包装

| 字段 | 类型 | 说明 |
|---|---|---|
| `items` | `array<T>` | 当前页的数据列表。 |
| `page` | `integer` | 当前页码，从 1 开始。 |
| `pageSize` | `integer` | 每页条数。 |
| `total` | `integer` | 符合查询条件的记录总数（用于客户端计算总页数）。 |

**SystemCapabilitiesDto** —— 服务端当前功能开关与上传限制的快照，客户端据此显隐功能入口并做上传前校验

| 字段 | 类型 | 说明 |
|---|---|---|
| `anonymousPlayback` | `boolean` | 是否允许匿名试听。 |
| `userCollections` | `boolean` | 是否开放用户个人歌单/收藏。 |
| `songRequests` | `boolean` | 是否开放点歌功能。 |
| `adminLibraryImport` | `boolean` | 是否开放管理员媒体库导入。 |
| `manualReview` | `boolean` | 导入内容是否需人工审核。 |
| `editorialCollections` | `boolean` | 是否开放运营精选合集。 |
| `listeningEvents` | `boolean` | 是否采集播放/聆听事件。 |
| `personalizedRecommendations` | `boolean` | 是否启用个性化推荐。 |
| `serverTranscoding` | `boolean` | 是否启用服务端转码。 |
| `userUploads` | `boolean` | 是否开放用户上传本地音频（扫码导入）：上传的歌曲默认私有、可立即收听，是否转公开由管理员审核决定。 |
| `allowedFormats` | `array<string>` | 允许上传的音频容器格式标识列表。 |
| `maxUploadSizeBytes` | `integer?` | 单文件上传大小上限，单位字节；未配置限制时为 null。 |
| `minimumPasswordStrength` | `integer` | 修改密码要求达到的最低强度等级（0-4），由 PasswordPolicy:MinimumStrength 下发。 |

**TrackDto** —— 曲目（可播放的单条音频）的完整展示信息

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 曲目唯一标识。 |
| `title` | `string` | 曲目标题。 |
| `artists` | `array<ArtistSummaryDto>` | 参与该曲目的艺人列表（支持多位艺人）。 |
| `album` | `AlbumSummaryDto?` | 所属专辑摘要；单曲等无专辑场景为 null。 |
| `trackNumber` | `integer?` | 在专辑碟片内的曲目序号；未知时为 null。 |
| `discNumber` | `integer?` | 碟片号，用于多碟专辑；未知时为 null。 |
| `releaseYear` | `integer?` | 发行年份；未知时为 null。 |
| `genres` | `array<string>` | 流派/风格标签列表。 |
| `durationMs` | `integer?` | 曲目时长，单位毫秒；探测失败时为 null。 |
| `artworkUrl` | `string?` | 封面图片地址；无封面时为 null。 |
| `audio` | `AudioDto?` | 可播放音频信息；未完成转码或当前功能开关不允许播放时为 null。 |

**UpdateLyricRequest** —— 管理端更新歌词请求体（所有字段可选，仅更新提供的字段）

| 字段 | 类型 | 说明 |
|---|---|---|
| `language` | `string?` | 语言代码；传空字符串表示清空。 |
| `format` | `string?` | 歌词格式：plain 或 lrc。 |
| `content` | `string?` | 歌词正文。 |
| `isDefault` | `boolean?` | 是否设为默认歌词。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/LibraryImportDtos.cs`

**ArtworkUploadResultDto** —— 封面上传成功后的结果：封面标识与其读取地址

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 封面唯一标识。 |
| `artworkUrl` | `string` | 封面读取地址。 |

**EnrichImportItemResultDto** —— 元数据/歌词一键补全结果：更新后的导入项与本次实际补齐的字段清单

| 字段 | 类型 | 说明 |
|---|---|---|
| `item` | `LibraryImportItemDto` | 补全后的导入项最新状态（前端直接用它刷新表单）。 |
| `filled` | `array<string>` | 本次补齐的字段标识列表（lyrics/album/genres/releaseYear/cover/trackNumber/discNumber/explicit）；空数组表示外部源均无命中。 |

**ImportMetadataDto** —— 从源文件解析出的音频元数据，管理员可在人工审核环节修正后再正式入库

| 字段 | 类型 | 说明 |
|---|---|---|
| `title` | `string?` | 曲目标题；标签缺失时为 null。 |
| `artists` | `array<string>` | 艺人名列表（原标签可能含多位艺人）。 |
| `albumTitle` | `string?` | 专辑标题；缺失时为 null。 |
| `trackNumber` | `integer?` | 碟内曲目号；缺失时为 null。 |
| `discNumber` | `integer?` | 碟片号；缺失时为 null。 |
| `releaseYear` | `integer?` | 发行年份；缺失时为 null。 |
| `genres` | `array<string>` | 流派标签列表。 |
| `durationMs` | `integer?` | 音频时长，单位毫秒；探测失败时为 null。 |
| `container` | `string?` | 容器格式标识；探测失败时为 null。 |
| `codec` | `string?` | 音频编码标识；探测失败时为 null。 |
| `bitrateKbps` | `integer?` | 音频码率，单位 kbps；探测失败时为 null。 |
| `artworkAvailable` | `boolean` | 源文件中是否包含可提取的内嵌封面。 |
| `lyrics` | `string?` | 当前歌词正文（可能来自内嵌标签、旁挂 .lrc、外部源自动匹配或人工补充）；无歌词时为 null。 |
| `lyricsSource` | `string?` | 歌词来源标识：embedded / sidecar / lrclib / manual；无歌词时为 null。 |
| `lyricsConfidence` | `number?` | 歌词匹配置信度（0~1）；外部源按相似度打分，其余为 1。 |

**ImportSourceFileDto** —— 导入源文件的基础信息

| 字段 | 类型 | 说明 |
|---|---|---|
| `displayName` | `string` | 上传时的原始文件名，仅用于界面展示，不参与服务端存储路径拼装。 |
| `sizeBytes` | `integer` | 源文件大小，单位字节。 |
| `container` | `string?` | ffprobe 探测到的容器格式标识；探测失败时为 null。 |
| `codec` | `string?` | ffprobe 探测到的音频编码标识；探测失败时为 null。 |

**ImportTranscodeDto** —— 单个导入文件的服务端转码状态

| 字段 | 类型 | 说明 |
|---|---|---|
| `status` | `string?` | 转码状态标识（排队中/处理中/完成/失败等）；未进入转码时为 null。 |
| `outputFormat` | `string?` | 实际产出的目标格式标识；未产出时为 null。 |
| `bitrateKbps` | `integer?` | 产出音频码率，单位 kbps；未产出时为 null。 |
| `errorCode` | `string?` | 转码失败时的机器可读错误码；成功或未执行时为 null。 |

**LibraryImportBatchDto** —— 一次批量上传形成的导入批次，包含批次内全部导入项

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 批次唯一标识。 |
| `status` | `string` | 批次整体处理状态标识。 |
| `items` | `array<LibraryImportItemDto>` | 批次内的导入项列表。 |
| `createdAt` | `datetime` | 批次创建时间（UTC）。 |

**LibraryImportItemDto** —— 批量导入中的单个文件项（一个源文件对应一个导入项）

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 导入项唯一标识。 |
| `status` | `string` | 导入项审核/处理状态标识。 |
| `sourceFile` | `ImportSourceFileDto` | 源文件基础信息。 |
| `metadata` | `ImportMetadataDto` | 解析出的音频元数据。 |
| `transcode` | `ImportTranscodeDto` | 服务端转码状态。 |
| `warnings` | `array<string>` | 非阻断性警告信息（如元数据缺失、标签可疑等），不阻止导入但提示管理员核对。 |
| `createdAt` | `datetime` | 导入项创建时间（UTC）。 |
| `updatedAt` | `datetime` | 导入项最近更新时间（UTC）。 |
| `songRequestId` | `uuid?` | 关联的点歌需求标识；未关联时为 null。 |
| `songRequestMatch` | `SongRequestMatchDto?` | 与关联需求的匹配结果；未关联或元数据未解析时为 null。 |

**LibraryImportItemWithUploaderDto** —— 带上传者信息的导入条目（管理端审核列表用）

| 字段 | 类型 | 说明 |
|---|---|---|
| `item` | `LibraryImportItemDto` | 导入条目本体。 |
| `uploadedByUserId` | `uuid?` | 批次上传者用户 Id；用于区分管理员导入与用户上传。 |

**PublishImportItemResultDto** —— 导入项发布成功后的结果：正式库中的曲目标识与该导入项的最新状态

| 字段 | 类型 | 说明 |
|---|---|---|
| `trackId` | `uuid` | 入库曲目标识。 |
| `status` | `string` | 导入项发布后的状态标识（通常为 published）。 |

**SongRequestMatchDto** —— 导入项元数据与目标点歌需求的匹配结果（读取时实时计算，元数据被修正后立即刷新）

| 字段 | 类型 | 说明 |
|---|---|---|
| `kind` | `string` | 结论档位：match（一致）/ fuzzy（微小差异）/ mismatch（不一致）。 |
| `trackDisplay` | `string` | 待上架曲目的展示串（标题 - 艺人）。 |
| `requestDisplay` | `string` | 目标需求的展示串（标题 - 艺人）。 |
| `blocking` | `boolean` | 是否会在发布时被强制阻断（由全局配置决定）。 |
| `message` | `string` | 给管理员看的中文提示。 |

**UpdateImportItemRequest** —— 管理员人工审核导入项时提交的修正后元数据；未传字段保持原值

| 字段 | 类型 | 说明 |
|---|---|---|
| `title` | `string?` | 修正后的曲目标题。 |
| `artists` | `array<string>?` | 修正后的艺人名列表。 |
| `albumTitle` | `string?` | 修正后的专辑标题。 |
| `trackNumber` | `integer?` | 修正后的碟内曲目号。 |
| `discNumber` | `integer?` | 修正后的碟片号。 |
| `releaseYear` | `integer?` | 修正后的发行年份。 |
| `genres` | `array<string>?` | 修正后的流派标签列表。 |
| `lyrics` | `string?` | 修正后的歌词；传空字符串表示清空，不传则保持原值。 |
| `songRequestId` | `uuid?` | 关联的点歌请求标识；当本次导入用于满足某条点歌诉求时传入，否则为 null。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/ListeningDtos.cs`

**DailyRecommendationDto** —— 每日推荐结果

| 字段 | 类型 | 说明 |
|---|---|---|
| `date` | `string` | 推荐日期（yyyy-MM-dd，服务器本地日期）。 |
| `personalized` | `boolean` | 是否为基于该用户行为的个性化结果；false 表示热门/最新兜底。 |
| `tracks` | `array<TrackDto>` | 推荐曲目列表（默认 30 首，互不重复）。 |

**FeedbackRequest** —— 设置曲目反馈

| 字段 | 类型 | 说明 |
|---|---|---|
| `kind` | `string` | 反馈类型：like / dislike / not_interested。 |

**ListeningHistoryItemDto** —— 播放历史中的一条（某曲目最近一次收听 + 累计播放次数）

| 字段 | 类型 | 说明 |
|---|---|---|
| `track` | `TrackDto` | 曲目展示信息。 |
| `lastPlayedAtUtc` | `datetime` | 最近一次收听时间（UTC）。 |
| `playCount` | `integer` | 累计播放次数。 |

**PlayEventRequest** —— 上报一条收听事件

| 字段 | 类型 | 说明 |
|---|---|---|
| `trackId` | `uuid` | 被收听的曲目 ID。 |
| `eventType` | `string` | 事件类型：play / complete / skip。 |
| `positionMs` | `integer?` | 事件发生时的播放进度（毫秒），可选。 |

**UserFeedbackDto** —— 用户对某曲目的反馈记录

| 字段 | 类型 |
|---|---|
| `trackId` | `uuid` |
| `kind` | `string` |
| `updatedAtUtc` | `datetime` |

#### `ShineMusicApi/ShineMusic.Model/Contracts/NotificationDtos.cs`

**NotificationDto** —— 站内信列表项

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 消息唯一标识。 |
| `kind` | `string` | 消息类型（如 song_request.declined）。 |
| `title` | `string` | 标题。 |
| `body` | `string?` | 正文说明；无正文时为 null。 |
| `linkUrl` | `string?` | 点击跳转的前端页面路径；无跳转时为 null。 |
| `resourceType` | `string?` | 关联资源类型；无关联时为 null。 |
| `resourceId` | `string?` | 关联资源 Id；无关联时为 null。 |
| `isRead` | `boolean` | 是否已读。 |
| `createdAt` | `datetime` | 创建时间（UTC）。 |

**UnreadNotificationCountDto** —— 未读消息数，供顶部角标轮询

| 字段 | 类型 | 说明 |
|---|---|---|
| `count` | `integer` | 未读条数。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/ProfileDtos.cs`

**AvatarUrlDto** —— 头像上传/更换的响应，对应

| 字段 | 类型 |
|---|---|
| `avatarUrl` | `string?` |

**ChangePasswordRequest** —— 密码修改请求体，对应

| 字段 | 类型 |
|---|---|
| `currentPassword` | `string` |
| `newPassword` | `string` |

**CurrentUserDto** —— 当前登录用户的资料快照，对应

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 用户唯一标识。 |
| `email` | `string` | 登录邮箱（与邀请绑定的邮箱一致）。 |
| `displayName` | `string` | 展示昵称。 |
| `roles` | `array<string>` | 角色码列表；包含 admin 表示管理员。 |
| `avatarUrl` | `string?` | 头像读取地址（带版本参数用于缓存刷新）；未上传时为 null。 |

**UpdateDisplayNameRequest** —— 昵称修改请求体，对应

| 字段 | 类型 |
|---|---|
| `displayName` | `string` |

**UpdateUserThemeRequest** —— 自定义主题配置更新请求体，对应 null 表示清除该槽位

| 字段 | 类型 |
|---|---|
| `accent` | `string?` |
| `accent2` | `string?` |
| `accent3` | `string?` |
| `maskOpacity` | `number` |
| `enabled` | `boolean` |

**UserThemeDto** —— 用户自定义图片主题配置，对应 GET/ 与 的响应

| 字段 | 类型 | 说明 |
|---|---|---|
| `accent` | `string?` | 主强调色（#RRGGBB）；未配置为 null。 |
| `accent2` | `string?` | 次强调色（#RRGGBB）；可空。 |
| `accent3` | `string?` | 第三强调色（#RRGGBB）；可空。 |
| `maskOpacity` | `number` | 壁纸暗色遮罩不透明度（0-0.85）。 |
| `enabled` | `boolean` | 是否启用自定义主题（多端同步的开关事实源）。 |
| `wallpaperUrl` | `string?` | 壁纸读取地址（带版本参数）；未上传为 null。地址需携带登录态请求取回。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/SocialDtos.cs`

**CommentLikeResult** —— 点赞/取消点赞操作后的最新状态

| 字段 | 类型 | 说明 |
|---|---|---|
| `likeCount` | `integer` | 该评论最新点赞总数。 |
| `likedByMe` | `boolean` | 操作后当前用户是否处于已点赞状态。 |

**CreateCommentRequest** —— 发表评论 / 回复的请求体

| 字段 | 类型 | 说明 |
|---|---|---|
| `content` | `string` | 评论文本，1~500 字符。 |
| `parentId` | `uuid?` | 父评论 Id（回复时传入，指向同一曲目的顶层评论）；顶层评论为 null。 |

**FollowStatusDto** —— 关注关系状态快照

| 字段 | 类型 | 说明 |
|---|---|---|
| `isFollowing` | `boolean` | 当前用户是否已关注目标用户。 |
| `followersCount` | `integer` | 当前用户关注了多少人（关注数）。 |
| `followingCount` | `integer` | 目标用户的粉丝数（被多少人关注）。 |

**TrackCommentDto** —— 曲目评论（或回复）的展示信息

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 评论唯一标识。 |
| `trackId` | `uuid` | 被评论曲目 Id。 |
| `userId` | `uuid` | 发表用户 Id。 |
| `userName` | `string` | 发表用户展示名（昵称）。 |
| `parentId` | `uuid?` | 父评论 Id；顶层评论为 null。 |
| `content` | `string` | 评论文本。 |
| `createdAtUtc` | `datetime` | 发表时间（UTC）。 |
| `replies` | `array<TrackCommentDto>?` | 该评论的回复列表（仅顶层评论填充）。 |
| `likeCount` | `integer` | 该评论的点赞总数。 |
| `likedByMe` | `boolean` | 当前用户是否已点赞（未登录恒为 false）。 |

**UserSummaryDto** —— 用户公开摘要（关注列表 / 评论作者展示用）

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 用户唯一标识。 |
| `displayName` | `string` | 展示名（昵称）。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/SourceDownloadDtos.cs`

**CreateSourceDownloadTasksRequest** —— 创建下载任务请求

| 字段 | 类型 | 说明 |
|---|---|---|
| `provider` | `string` | 音源平台（audius/jamendo）。 |
| `providerTrackIds` | `array<string>` | 平台曲目 Id 列表（一次可批量）。 |

**CreateSourceDownloadTasksResultDto** —— 创建下载任务响应：新建/重置入队的任务与因重复被跳过的平台曲目 Id

| 字段 | 类型 |
|---|---|
| `created` | `array<ExternalDownloadTaskDto>` |
| `skippedDuplicates` | `array<string>` |

**ExternalDownloadTaskDto** —— 外部下载任务视图（含评分与直通标记，供任务列表/详情展示）

| 字段 | 类型 |
|---|---|
| `id` | `uuid` |
| `provider` | `string` |
| `providerTrackId` | `string` |
| `title` | `string?` |
| `artists` | `array<string>` |
| `albumTitle` | `string?` |
| `durationMs` | `integer?` |
| `quality` | `string?` |
| `license` | `string?` |
| `sourcePageUrl` | `string?` |
| `coverUrl` | `string?` |
| `status` | `string` |
| `autoScore` | `integer?` |
| `autoPublished` | `boolean` |
| `errorCode` | `string?` |
| `errorMessage` | `string?` |
| `autoRetryCount` | `integer` |
| `nextRetryAtUtc` | `datetime?` |
| `importItemId` | `uuid?` |
| `createdAtUtc` | `datetime` |
| `updatedAtUtc` | `datetime` |
| `hasLyrics` | `boolean?` |

**ExternalDownloadTaskListDto** —— 任务分页响应

| 字段 | 类型 |
|---|---|
| `items` | `array<ExternalDownloadTaskDto>` |
| `total` | `integer` |
| `page` | `integer` |
| `pageSize` | `integer` |

**SourceProviderErrorDto** —— 单个音源平台的错误说明（搜索聚合时单源失败不拖垮整体）

| 字段 | 类型 |
|---|---|
| `provider` | `string` |
| `message` | `string` |

**SourceSearchItemDto** —— 音源搜索结果条目（跨平台统一模型，与 IMusicSourceProvider.SourceSearchResult 对齐）

| 字段 | 类型 |
|---|---|
| `provider` | `string` |
| `providerTrackId` | `string` |
| `title` | `string` |
| `artists` | `array<string>` |
| `albumTitle` | `string?` |
| `durationMs` | `integer?` |
| `genres` | `array<string>` |
| `coverUrl` | `string?` |
| `sourcePageUrl` | `string?` |
| `license` | `string?` |
| `quality` | `string` |
| `isLosslessAvailable` | `boolean` |

**SourceSearchResponse** —— 搜索响应：合并各平台结果 + 失败平台列表

| 字段 | 类型 |
|---|---|
| `results` | `array<SourceSearchItemDto>` |
| `providerErrors` | `array<SourceProviderErrorDto>` |

#### `ShineMusicApi/ShineMusic.Model/Contracts/StorageDtos.cs`

**StorageAuditDto** —— 存储审计结果：曲库根目录下磁盘文件与数据库引用的对账明细

| 字段 | 类型 | 说明 |
|---|---|---|
| `referencedFileCount` | `integer` | 数据库引用的存储键数量（MediaAsset + Artwork）。 |
| `diskFileCount` | `integer` | 磁盘上实际存在的文件数量（曲库根目录内）。 |
| `missingFileCount` | `integer` | 缺失文件数：数据库有引用但磁盘不存在（通常是误删或磁盘故障，只报告不自动修复）。 |
| `orphanFileCount` | `integer` | 孤儿文件数：磁盘存在但数据库无任何引用（可安全清理）。 |
| `reclaimableBytes` | `integer` | 孤儿文件合计可回收字节数。 |
| `emptyDirectoryCount` | `integer` | 空目录数量（无文件且无子目录，通常为删除残留）。 |
| `orphans` | `array<StorageOrphanFileDto>` | 孤儿文件明细列表（含类型分类，便于前端预览）。 |
| `missing` | `array<StorageMissingFileDto>` | 缺失文件明细列表（含存储键与归属引用说明，便于人工排查）。 |
| `emptyDirectories` | `array<string>` | 空目录相对路径列表（含末尾斜杠的存储键形式）。 |
| `groups` | `array<StorageFileGroupDto>` | 按曲目聚合的库内文件明细（音频 + 封面），末尾可能附带「其他引用」杂项组。 |

**StorageCleanupDto** —— 孤儿文件清理的执行结果

| 字段 | 类型 | 说明 |
|---|---|---|
| `deletedOrphanCount` | `integer` | 实际删除的孤儿文件数量。 |
| `deletedBytes` | `integer` | 实际删除释放的字节数。 |
| `removedEmptyDirectoryCount` | `integer` | 连带移除的空目录数量（孤儿删除后递归发现的）。 |

**StorageFileGroupDto** —— 按曲目聚合的文件分组：一首曲目的全部关联文件（媒体资产 + 封面）放在一起展示

| 字段 | 类型 | 说明 |
|---|---|---|
| `trackId` | `uuid?` | 曲目 Id；null 表示非曲目分组。 |
| `title` | `string` | 分组标题（曲目标题或「其他引用」）。 |
| `artists` | `string?` | 艺术家名（多位的拼接，无则 null）。 |
| `albumTitle` | `string?` | 所属专辑标题（单曲为 null）。 |
| `durationMs` | `integer?` | 曲目时长（毫秒）。 |
| `isPublished` | `boolean` | 曲目是否已发布（上架）。 |
| `totalBytes` | `integer` | 组内在盘文件合计字节数。 |
| `files` | `array<StorageGroupFileDto>` | 组内文件明细。 |

**StorageFileRefDto** —— 单个磁盘文件的引用信息（相对曲库根目录的存储键与大小）

| 字段 | 类型 | 说明 |
|---|---|---|
| `storageKey` | `string` | 相对存储键，形如 original/{trackId}/{assetId}.ext 或 artwork/{artworkId}.jpg。 |
| `sizeBytes` | `integer` | 文件大小（字节）。 |

**StorageGroupFileDto** —— 分组内的单个文件明细

| 字段 | 类型 | 说明 |
|---|---|---|
| `storageKey` | `string` | 相对存储键。 |
| `sizeBytes` | `integer` | 在盘字节数；缺失时为 0。 |
| `kind` | `string` | 文件类别：audio / artwork。 |
| `role` | `string` | 文件角色：primary（主资产）/ variant（转码产物）/ artwork（封面）。 |
| `detail` | `string?` | 人类可读补充：音频为「容器 · 编码 · 码率」，封面为 MIME/共享说明，其他引用组为归属说明。 |
| `onDisk` | `boolean` | 文件是否在磁盘上存在（false 即缺失文件）。 |
| `isPublished` | `boolean?` | 媒体资产发布状态；封面行为 null。 |

**StorageMissingFileDto** —— 缺失文件明细：库有引用但磁盘不存在，附带归属引用说明（哪些曲目/专辑/精选集在引用它）

| 字段 | 类型 | 说明 |
|---|---|---|
| `storageKey` | `string` | 相对存储键。 |
| `kind` | `string` | 文件类别：audio / artwork / other。 |
| `referencedBy` | `array<string>` | 人类可读的归属引用列表，如「曲目《xxx》音频」「专辑《yyy》封面」。 |

**StorageOrphanFileDto** —— 孤儿文件明细：存储键与大小之外附带类型分类（audio/artwork/other），便于前端决定是否可预览

| 字段 | 类型 | 说明 |
|---|---|---|
| `storageKey` | `string` | 相对存储键。 |
| `sizeBytes` | `integer` | 文件大小（字节）。 |
| `kind` | `string` | 文件类别：audio（音频）/ artwork（封面图片）/ other（其他，按扩展名推断）。 |

**TrackDeleteResultDto** —— 曲目物理删除的执行结果：删除数据库行并同步清理磁盘文件

| 字段 | 类型 | 说明 |
|---|---|---|
| `trackId` | `uuid` | 被删除曲目的标识。 |
| `title` | `string` | 被删除曲目的标题（删除前快照，便于审计追溯）。 |
| `deletedFileCount` | `integer` | 实际删除的磁盘文件数量（媒体资产 + 可能连带删除的封面）。 |
| `deletedBytes` | `integer` | 实际删除释放的字节数。 |
| `artworkDeleted` | `boolean` | 封面是否随曲目一并删除（数据库行与文件；被其他曲目/专辑/精选集共享的封面会保留）。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/UserLibraryDtos.cs`

**CreatePlaylistRequest** —— 创建歌单的请求体；字段均可空，实际必填规则由服务端校验

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | `string?` | 歌单名称。 |
| `description` | `string?` | 歌单描述。 |
| `artworkId` | `uuid?` | 预先上传的封面图片资源标识；不使用封面时为 null。 |

**CreateSongRequestRequest** —— 用户提交点歌（求歌）请求的请求体，描述希望曲库收录的歌曲；字段必填规则由服务端校验

| 字段 | 类型 | 说明 |
|---|---|---|
| `title` | `string?` | 期望收录的曲目标题。 |
| `artistName` | `string?` | 艺人名称。 |
| `albumTitle` | `string?` | 专辑名称。 |
| `note` | `string?` | 用户附加说明/备注。 |

**PlaylistDetailDto** —— 用户歌单详情，包含完整曲目列表

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 歌单唯一标识。 |
| `name` | `string` | 歌单名称。 |
| `description` | `string?` | 歌单描述；未填写时为 null。 |
| `tracks` | `array<TrackDto>` | 歌单内的曲目列表。 |
| `updatedAt` | `datetime` | 歌单最近更新时间（UTC）。 |
| `artworkUrl` | `string?` | 歌单封面地址；未设置封面时为 null。 |
| `pendingTracks` | `array<PlaylistPendingTrackDto>?` | 歌单导入产生的待补齐曲目（未匹配到曲库、静默下载进行中或失败的条目）。 |

**PlaylistPendingTrackDto** —— 歌单导入产生的待补齐曲目（前端灰色展示，发布完成后自动转正）

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 待补齐条目唯一标识。 |
| `title` | `string` | 曲目标题。 |
| `artists` | `array<string>` | 艺人名列表。 |
| `albumTitle` | `string?` | 专辑名。 |
| `durationMs` | `integer?` | 时长（毫秒）。 |
| `coverUrl` | `string?` | 封面地址。 |
| `sortOrder` | `integer` | 原歌单内排序序号（0 起）。 |
| `status` | `string` | 状态：pending/downloading/no_source/failed。 |

**PlaylistSummaryDto** —— 用户歌单的摘要信息，用于歌单列表等场景

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 歌单唯一标识。 |
| `name` | `string` | 歌单名称。 |
| `description` | `string?` | 歌单描述；未填写时为 null。 |
| `trackCount` | `integer` | 歌单内曲目数量。 |
| `updatedAt` | `datetime` | 歌单信息（含曲目增删）的最近更新时间（UTC）。 |
| `artworkUrl` | `string?` | 歌单封面地址；未设置封面时为 null。 |

**ReopenSongRequestRequest** —— 管理员重新开启已拒绝需求的请求体

| 字段 | 类型 | 说明 |
|---|---|---|
| `reason` | `string?` | 重新开启原因，写入时间线并通知提交人。 |

**SongRequestDto** —— 点歌请求的展示信息

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 点歌请求唯一标识。 |
| `title` | `string` | 曲目标题。 |
| `artistName` | `string` | 艺人名称。 |
| `albumTitle` | `string?` | 专辑名称；用户未提供时为 null。 |
| `status` | `string` | 当前处理状态标识。 |
| `voteCount` | `integer` | 该请求获得的支持票数，用于排序热门诉求。 |
| `myVote` | `boolean` | 当前查看用户是否已为该请求投过票。 |
| `matchedTrackId` | `uuid?` | 已采纳并匹配的曲库曲目标识；未匹配时为 null。 |
| `createdAt` | `datetime` | 请求创建时间（UTC）。 |
| `updatedAt` | `datetime` | 请求最近更新时间（UTC）。 |
| `createdByMe` | `boolean` | 是否由当前查看用户本人提交。 |

**SongRequestTimelineItemDto** —— 点歌需求时间线上的单个事件，用于在卡片上展示「提交 → 纳入计划 → 处理中 → 已入库」的进度

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | `uuid` | 事件唯一标识。 |
| `kind` | `string` | 事件类型：submitted/planned/in_progress/fulfilled/declined/withdrawn/reopened。 |
| `fromStatus` | `string?` | 变更前状态；提交事件为 null。 |
| `toStatus` | `string?` | 变更后状态。 |
| `actorKind` | `string` | 操作人身份：user/admin/system。 |
| `note` | `string?` | 备注（拒绝原因/重新开启原因）；仅提交人本人与管理员可见，其余情况为 null。 |
| `createdAt` | `datetime` | 事件发生时间（UTC）。 |

**UpdatePlaylistRequest** —— 更新歌单信息的请求体；未传字段表示不修改

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | `string?` | 新的歌单名称。 |
| `description` | `string?` | 新的歌单描述。 |
| `artworkId` | `uuid?` | 新封面图片资源标识。 |
| `clearArtwork` | `boolean` | 是否显式移除现有封面（true 时忽略 ArtworkId 并清空封面）。 |

**UpdateSongRequestRequest** —— 管理员处理点歌请求的请求体

| 字段 | 类型 | 说明 |
|---|---|---|
| `status` | `string?` | 目标状态标识（如待处理、已采纳、已拒绝等，取值由服务端约定）。 |
| `adminNote` | `string?` | 管理员处理备注，对用户可见或留档。 |
| `matchedTrackId` | `uuid?` | 采纳时匹配到的正式曲库曲目标识；未匹配时为 null。 |

#### `ShineMusicApi/ShineMusic.Model/Contracts/UserUploadDtos.cs`

**AddToLibraryRequestDto** —— 批量加入曲库请求：预检命中公共曲库的歌曲可直接引用，无需上传文件

| 字段 | 类型 |
|---|---|
| `trackIds` | `array<uuid>` |

**PrecheckMatchDto** —— 预检命中的曲目摘要

| 字段 | 类型 | 说明 |
|---|---|---|
| `trackId` | `uuid` | 曲目 Id。 |
| `title` | `string` | 曲目标题。 |
| `artists` | `array<string>` | 艺人名列表。 |
| `isPublic` | `boolean` | 是否已公开（公开曲目可被任何用户引用）。 |
| `artworkUrl` | `string?` | 封面图 URL；无封面时为 null。 |

**QueuedUploadDto** —— 上传响应中进入处理队列的新歌：转码 + 歌词自动匹配完成后私有入库

| 字段 | 类型 |
|---|---|
| `itemId` | `uuid` |
| `displayName` | `string` |

**ReferencedTrackDto** —— 上传响应中已引用（去重命中）的曲目：秒级加入曲库，无需等待处理

| 字段 | 类型 |
|---|---|
| `trackId` | `uuid` |
| `title` | `string` |
| `artists` | `array<string>` |

**RejectedUploadDto** —— 上传响应中无法引用/无法处理的文件（如他人私有曲目）

| 字段 | 类型 |
|---|---|
| `displayName` | `string` |
| `reason` | `string` |

**UploadPrecheckFileDto** —— 预检请求中的单个文件指纹，由客户端在上传前读取本地文件标签得到

| 字段 | 类型 | 说明 |
|---|---|---|
| `fileName` | `string` | 本地文件名。 |
| `sizeBytes` | `integer?` | 文件大小（字节）；读取失败时为 null。 |
| `title` | `string?` | 标签中的标题；无标签时为 null。 |
| `artist` | `string?` | 标签中的首位艺人；无标签时为 null。 |
| `durationMs` | `integer?` | 时长（毫秒）；读取失败时为 null。 |

**UploadPrecheckRequestDto** —— 批量预检请求：上传前把本地文件指纹批量发给服务端查重

| 字段 | 类型 |
|---|---|
| `files` | `array<UploadPrecheckFileDto>` |

**UploadPrecheckResponseDto** —— 批量预检响应

| 字段 | 类型 |
|---|---|
| `results` | `array<UploadPrecheckResultDto>` |

**UploadPrecheckResultDto** —— 单个文件的预检结果

| 字段 | 类型 | 说明 |
|---|---|---|
| `fileName` | `string` | 与请求对应的本地文件名。 |
| `match` | `PrecheckMatchDto?` | 命中的曲目；未命中为 null（客户端应上传该文件）。 |

**UserLibraryItemDto** —— 我的曲库条目

| 字段 | 类型 | 说明 |
|---|---|---|
| `trackId` | `uuid` | 曲目 Id。 |
| `title` | `string` | 曲目标题。 |
| `artists` | `array<string>` | 艺人名列表。 |
| `albumTitle` | `string?` | 专辑标题；无专辑时为 null。 |
| `durationMs` | `integer?` | 时长（毫秒）。 |
| `artworkUrl` | `string?` | 封面图 URL；无封面时为 null。 |
| `isPublic` | `boolean` | 是否已公开（私有曲目仅本人可见）。 |
| `addedAtUtc` | `datetime` | 加入曲库时间（UTC）。 |

**UserUploadResponseDto** —— 用户上传响应：区分“已引用”（秒级加入曲库）与“处理中”（新歌走转码与歌词匹配）

| 字段 | 类型 |
|---|---|
| `batchId` | `uuid?` |
| `referenced` | `array<ReferencedTrackDto>` |
| `queued` | `array<QueuedUploadDto>` |
| `rejected` | `array<RejectedUploadDto>` |

#### `ShineMusicApi/ShineMusic.Service/Services/ArtworkFetcher.cs`

**ItunesMetadataResult** —— 元数据获取结果

| 字段 | 类型 |
|---|---|
| `success` | `boolean` |
| `storageKey` | `string?` |
| `contentType` | `string?` |
| `error` | `string?` |
| `albumTitle` | `string?` |
| `releaseYear` | `integer?` |
| `artistName` | `string?` |
| `trackTimeMs` | `integer?` |
| `trackNumber` | `integer?` |
| `discNumber` | `integer?` |
| `itunesTrackId` | `integer?` |
| `country` | `string?` |
| `explicitness` | `string?` |

#### `ShineMusicApi/ShineMusic.Service/Services/ImportPublishService.cs`

**ImportPublishContext** —— 发布动作的执行者上下文

| 字段 | 类型 | 说明 |
|---|---|---|
| `actorUserId` | `uuid` | 操作人用户 Id；系统自动发布时为 <see cref="Guid.Empty"/>。 |
| `traceId` | `string` | 链路追踪 Id（人工发布取 HttpContext.TraceIdentifier，自动发布取合成标识）。 |
| `isAutomatic` | `boolean` | 是否评分直通自动发布（决定审计动作名 track.published / track.auto_published）。 |

**ImportPublishError** —— 发布失败详情

| 字段 | 类型 |
|---|---|
| `kind` | `ImportPublishErrorKind` |
| `message` | `string` |
| `code` | `string?` |
| `field` | `string?` |

**ImportPublishResult** —— 发布结果：成功时携带新曲目 Id 与条目状态；失败时携带错误详情

| 字段 | 类型 |
|---|---|
| `trackId` | `uuid?` |
| `status` | `string?` |
| `error` | `ImportPublishError?` |

#### `ShineMusicApi/ShineMusic.Service/Services/ImportStagingService.cs`

**StagedAudio** —— 暂存结果中的单个音频文件

| 字段 | 类型 | 说明 |
|---|---|---|
| `itemId` | `uuid` | 为本次暂存生成的条目 Id（同时用作隔离区文件名主干）。 |
| `displayName` | `string` | 净化后的原始文件名。 |
| `extension` | `string` | 小写扩展名（不含点）。 |
| `storageKey` | `string` | 隔离区存储键（uploads/{批次}/{条目Id}.{扩展名}）。 |
| `contentHash` | `string` | 文件内容 SHA-256 十六进制串（小写）。 |
| `sizeBytes` | `integer` | 文件字节数。 |

#### `ShineMusicApi/ShineMusic.Service/Services/LyricsResolver.cs`

**LyricsMatch** —— 一次歌词匹配的结果

| 字段 | 类型 | 说明 |
|---|---|---|
| `content` | `string` | 歌词正文（纯文本或带 [mm:ss] 时间轴的 LRC）。 |
| `source` | `string` | 来源标识，取值见 <see cref="LyricsSources"/>。 |
| `confidence` | `number` | 置信度 0~1：内嵌/旁挂为 1，外部源按相似度打分。 |
| `providerId` | `string?` | 外部源记录 ID（如 LRCLIB 的 id / 酷狗歌词 id），非外部来源为 null。 |

**LyricsQuery** —— 一次歌词匹配的输入：来自已解析的音频元数据与隔离区源文件路径

| 字段 | 类型 | 说明 |
|---|---|---|
| `title` | `string?` | 曲目标题（来自标签或文件名）。 |
| `artists` | `array<string>` | 艺人名列表，取第一位作为主艺人参与匹配。 |
| `albumTitle` | `string?` | 专辑名，可为空。 |
| `durationMs` | `integer?` | 音频时长（毫秒），用于精确匹配与时长打分。 |
| `embeddedLyrics` | `string?` | 音频标签中的内嵌歌词（ID3 USLT 等），可为空。 |
| `sourcePath` | `string?` | 隔离区中的音频文件绝对路径，用于查找同名旁挂歌词。 |

#### `ShineMusicApi/ShineMusic.Service/Services/MediaProcessingService.cs`

**ParsedAudioMetadata** —— 解析得到的音频元数据

| 字段 | 类型 | 说明 |
|---|---|---|
| `title` | `string?` | 曲目标题。 |
| `artists` | `array<string>` | 艺术家列表。 |
| `albumTitle` | `string?` | 专辑名称。 |
| `trackNumber` | `integer?` | 曲目序号。 |
| `discNumber` | `integer?` | 碟片序号。 |
| `releaseYear` | `integer?` | 发行年份。 |
| `genres` | `array<string>` | 流派列表。 |
| `durationMs` | `integer?` | 时长（毫秒）。 |
| `artworkStorageKey` | `string?` | 封面在隔离存储中的存储键。 |
| `lyrics` | `string?` | 从标签中读取的内嵌歌词（ID3v2 USLT 等）；无内嵌歌词时为 null。 |
| `warnings` | `array<string>` | 解析过程中收集的非致命告警（如缺少标题/艺术家）。 |

**ProbedMedia** —— 探测得到的媒体信息

| 字段 | 类型 | 说明 |
|---|---|---|
| `container` | `string` | 归一化后的容器格式，例如 flac、mp3、m4a。 |
| `codec` | `string` | 音频编码器名称，例如 aac、flac。 |
| `bitrateKbps` | `integer?` | 比特率（kbps），探测失败时为 null。 |
| `durationMs` | `integer?` | 时长（毫秒），探测失败时为 null。 |
| `formatNames` | `string` | ffprobe 返回的原始 format_name 逗号分隔列表。 |

#### `ShineMusicApi/ShineMusic.Service/Services/RecommendationService.cs`

**DailyRecommendationResult** —— 每日推荐计算结果

| 字段 | 类型 | 说明 |
|---|---|---|
| `tracks` | `array<Track>` | 推荐曲目（已按推荐度排序）。 |
| `personalized` | `boolean` | 是否个性化结果；false 表示热度/最新兜底。 |
| `date` | `date` | 推荐日期（服务器本地日期）。 |

#### `ShineMusicApi/ShineMusic.Service/Services/SongRequestMatchValidator.cs`

**MatchVerdict** —— 匹配校验结果

| 字段 | 类型 | 说明 |
|---|---|---|
| `kind` | `MatchVerdictKind` | 结论档位。 |
| `trackDisplay` | `string` | 待上架曲目的展示串（标题 - 艺人）。 |
| `requestDisplay` | `string` | 点歌需求的展示串（标题 - 艺人）。 |
| `titleDistance` | `integer` | 标题编辑距离；完全一致为 0。 |
| `artistDistance` | `integer` | 艺人编辑距离；完全一致为 0，无艺人信息为 -1。 |

#### `ShineMusicApi/ShineMusic.Service/Services/TokenService.cs`

**AuthTokenResponse** —— 登录/刷新成功后返回给客户端的认证响应载荷：访问令牌 + 刷新令牌 + 令牌类型 + 有效期秒数

| 字段 | 类型 | 说明 |
|---|---|---|
| `accessToken` | `string` | JWT 访问令牌。 |
| `refreshToken` | `string` | 刷新令牌明文（仅此一次随响应下发）。 |
| `tokenType` | `string` | 令牌类型（Bearer）。 |
| `expiresIn` | `integer` | 访问令牌有效期（秒）。 |

