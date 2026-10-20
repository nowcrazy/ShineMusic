# 领域模型

ID 一律 UUID；时间 UTC ISO 8601；时长 `durationMs`；文件大小 `sizeBytes`。公开 DTO 只暴露已发布内容，不含磁盘路径、隔离 key、邀请明文令牌。

## 曲库（Catalog）

```
Artist ──┬── TrackArtist ── Track ── MediaAsset（原文件 / AAC 转码）
         │                    │
         │                    ├── TrackGenre ── Genre
         │                    ├── Lyric（多份，一份 IsDefault）
         │                    └── Artwork?
         └── AlbumArtist ── Album ── Artwork?

EditorialCollection ── CollectionTrack ── Track
UserLibraryItem（用户「拥有」某曲的引用，不复制文件）
```

- **Track**：播放单元。`PublishedAt == null` 表示未上架；用户上传私有期也是这个状态，用 `OwnerUserId` 限制可见性。
- **MediaAsset**：一份具体音频文件。`ContentHash` 唯一。
- **Artwork**：封面元数据，二进制在存储目录。
- **Lyric**：`plain` / `lrc`；来源 `embedded` | `sidecar` | `lrclib` | `manual`。
- **EditorialCollection**：运营精选，与用户歌单分开。未发布前台不可见。
- **UserLibraryItem**：`(UserId, TrackId)` 唯一。移除引用不删曲目本体。

`Region` / `Explicit` / `ItunesTrackId` 在导入时探测，用于推荐区域亲和与内容过滤。

## 身份与互动

- **ApplicationUser**：Identity + `DisplayName`。角色只有 `user`、`admin`。
- **RefreshToken**：哈希存储，支持吊销与轮换链。
- **Invitation**：令牌只存哈希；状态 `created` → `sent`（或 `delivery_failed`）→ `redeemed` / `expired` / `revoked`。
- **Favorite**：`(UserId, TrackId)` 复合主键。
- **UserPlaylist** / **UserPlaylistTrack**：用户歌单，曲目不重复，有 `SortOrder`。
- **UserNotification**：站内信；需求状态变化写给提交人。
- **AdminAuditEntry**：管理员写操作不可变留痕。

## 点歌需求

`SongRequestRecord` 表示「希望曲库里有这首歌」，不是播放队列。

状态：`open` → `planned` → `in_progress` → `fulfilled`（可跳级）。旁路：`declined`、`withdrawn`。

流转规则在 `SongRequestStateMachine`：

- 允许向前推进，禁止回退。
- `fulfilled`、`withdrawn` 无出口。
- `declined → open` 只能走专用 reopen 接口，且必须填原因。
- 提交人仅 `open` 可撤回；管理员可在 `open`/`planned` 代为撤回。
- `declined` 必须有 `adminNote`；`fulfilled` 必须关联已发布曲目并通过标题/艺人匹配（可配置强制/覆盖）。
- 投票：登录用户对同一需求最多一票，重复 PUT 幂等；取消投票接口已下线（405）。
- 公共列表默认不展示 `declined`/`withdrawn`；已满足且超过保留期的由后台任务写入 `ArchivedAtUtc`，从公共池隐藏，记录保留。

时间线 `SongRequestEventRecord` 对匿名可见事件类型；备注仅提交人与管理员可见。

## 导入流水线

批次 `LibraryImportBatch`：`queued` → `processing` → `completed` / `completed_with_errors` / `failed` / `cancelled`。

条目 `LibraryImportItem` 主线：

```
uploaded → validating → parsing_metadata → transcoding
  → pending_review → ready_to_publish → published
```

用户上传完成后可进入 `pending_publication`（已私有入库，等转公开）。失败 `processing_failed`，拒绝 `rejected`。转码子状态与主状态解耦，便于进程崩溃后恢复。条目支持软删除 `IsDeleted`。

关联 `SongRequestId` 时，解析完即可给出 `songRequestMatch` 结论，发布时再强制校验标题/艺人。

## 收听与推荐

- **ListeningEvent**：`play` / `complete` / `skip`，仅登录用户。写接口按用户（匿名按 IP）限流。
- **UserTrackFeedback**：`like` / `dislike` / `not_interested`，一人一曲一条。
- **DailyRecommendationItem**：按用户+日期缓存，当日稳定；近 7 天已推曲目降权。

推荐打分综合：播放/听完次数、歌手亲和、跳过降权、收藏/喜欢加权、不喜欢强降权、不感兴趣排除、区域等。匿名或没有行为时退化为全局热度 + 最新。

## 固定安全不变量

这些不能靠前端参数关掉：

- 注册模式是管理员邀请。
- 未审核/拒绝文件不可被公开搜索、播放、进精选。
- 转码失败不可发布。
- 普通用户不能改角色、邀请、审核结论、正式文件路径。
- 密码、SMTP、邀请明文、磁盘路径不进 API 响应。
