# 核心业务流程

## 1. 听一首已发布的歌

1. 首页 `GET /api/v1/music/home` 或搜索 `GET /api/v1/tracks`。
2. 前端 `mapTrack` 把封面、`/tracks/{id}/stream` 拼成可播地址。
3. `player.ts` 用全局 `InnerAudioContext` 播；队列循环。
4. 已登录时上报 `POST /api/v1/me/play-events`：开始 `play`，切歌时进度 ≥90% 记 `complete`，否则 `skip`。
5. 歌词按需 `GET /tracks/{id}/lyrics/default`。

匿名播放由 `Features:AnonymousPlayback` 控制；关闭时 stream 需要登录。媒体请求允许 query token。

## 2. 邀请注册与登录

1. 库中无用户时，本机 `dotnet run --project backend/ShineMusic.Api -- --create-admin`（或环境变量 `ADMIN_*`）。
2. 管理员 `POST /api/v1/admin/invitations`，SMTP 发一次性链接 `/#/pages/auth/register?token=...`。
3. 前端校验 `POST /auth/invitations/validate`，注册 `POST /auth/register`（邮箱以邀请记录为准，客户端不能改）。
4. 登录拿 access + refresh；`http.ts` 遇 401 单飞刷新。登出吊销 refresh。

邀请过期默认 48 小时。管理接口永不返回原始令牌。

## 3. 管理员导入并发布

1. 管理页上传 `POST /api/v1/admin/library/imports`（可带 `songRequestId`）。
2. 文件进隔离区，item 入 `LibraryImportQueue`。
3. `LibraryImportBackgroundService` 按 `Transcoding:MaxConcurrentJobs` 消费：校验 → 解析 → 转码 → 歌词/iTunes 探测 → `pending_review`。
4. 管理员改元数据、补歌词、试听 `.../preview`，确认后 `POST .../publish`。
5. 生成/更新 Artist、Album、Track、MediaAsset、Artwork、Lyric；隔离文件迁到正式库。
6. 若关联点歌且标题艺人匹配，需求转 `fulfilled`，给提交人发站内信。

转码失败不能发布。误挂的需求可 `DELETE .../song-request` 解绑。

## 4. 用户上传到「我的曲库」

1. 登录用户 `POST /api/v1/me/uploads`（可先 `uploads/precheck`）。
2. 走同一隔离处理流水线，标记 `IsUserUpload`。
3. 成功后创建（或去重引用）Track，`PublishedAt` 为空，写入 `UserLibraryItem`，上传者立即可播。
4. 管理员审核后可转公开，进入公共搜索与精选池。
5. `DELETE /me/library/{trackId}` 只移除引用。

## 5. 点歌 → 入库满足

```
用户搜索曲库 ──已存在──► 提示「已在曲库」，不建需求
       │
       └──未命中──► POST /song-requests（查重标题/艺人/专辑）
                      他人可点赞（不可取消）
                      管理员 PATCH 状态 / 导入时挂 songRequestId
                      发布校验 TitleArtistMatch
                      fulfilled + 通知 + 可选 30 天后归档
```

拒绝必须写原因；重新开启走 `POST /admin/song-requests/{id}/reopen`。

## 6. 每日推荐

1. `GET /api/v1/recommendations/daily`。带 JWT 时按用户行为现算或读当日缓存。
2. 无行为或匿名：热度 + 最新兜底，`Personalized=false`。
3. 前端「为你」Tab 与 `/pages/recommend/daily` 使用同一接口。
4. 显式反馈 `PUT /me/feedback/{trackId}` 影响后续打分。

## 7. 收藏与歌单

全部在 `/api/v1/me` 下，需登录且 `UserCollections` 开启。收藏 PUT 幂等。歌单封面用已发布 `artworkId`，可用 `clearArtwork` 清除。
