# Tasks：解析外部歌单导入

## 后端

- [x] T1 `IdentityEntities.cs` 新增 `UserPlaylistPendingTrack` 实体（SugarTable "UserPlaylistPendingTracks"）
- [x] T2 `ShineMusic.Service/Services/PlaylistImport/` 新建：`IPlaylistParser.cs`（接口 + ParsedPlaylist/ParsedPlaylistTrack）
- [x] T3 `NeteasePlaylistParser.cs`（页面内嵌 JSON + /api/v6 兜底）
- [x] T4 `QQMusicPlaylistParser.cs`（fcg_v8_playlist_cp.fcg）
- [x] T5 `KuwoPlaylistParser.cs`（pl.svc getlistinfo + 伪 JSON 归一化）
- [x] T6 `YtDlpPlaylistParser.cs` + `YtDlpBridge.GetPlaylistAsync`（--flat-playlist --dump-single-json；Spotify 抛 SourceUnavailableException）
- [x] T7 `PlaylistImportService.cs`（ParsePreviewAsync / MatchLibraryAsync / ImportAsync / ResolvePendingAsync）
- [x] T8 `UserLibraryDtos.cs`：`PlaylistDetailDto` 加 `PendingTracks` 可选参数 + 新 `PlaylistPendingTrackDto`
- [x] T9 `PlaylistImportController.cs`（POST parse / POST import，FeatureGate UserCollections）
- [x] T10 `UserCollectionController.GetPlaylist` 开头调 ResolvePendingAsync + 响应并入 pendingTracks；新增 `DELETE .../pending/{pendingId}`
- [x] T11 `Program.cs`：命名 HttpClient（netease/qqmusic 20s）、parsers 单例注册、PlaylistImportService scoped

## Web 管理端

- [x] T12 `services/playlist-import.ts`（parse/import + 就地类型）
- [x] T13 `pages/admin/playlist-import.vue`（URL 输入 → 预览表格 → 导入 → 结果摘要）
- [x] T14 `router/index.ts` 注册路由（meta.title 歌单解析）+ `AdminLayout.vue` menuItems 加项

## App 端

- [x] T15 `services/playlist-import.ts` + `types/music.ts`（ApiPlaylistPendingTrack、ApiPlaylistDetail.pendingTracks）+ 同步 `api.generated.ts`
- [x] T16 `pages/playlist/import.vue` + `pages.json` 注册
- [x] T17 `home/index.vue` MY PLAYLISTS 区加「导入」按钮
- [x] T18 `playlist/detail.vue`：pending 灰色行合并渲染 + 状态文案 + 8s 轮询 + onUnload 清理
- [x] T19 i18n 键（zh-Hans / en）：homePage.import、importPage.*、playlistPage.pending*

## 验证

- [x] T20 `dotnet build -c Release` 零错误，重启 5180 后端
- [x] T21 curl 实测网易云 parse → import → GetPlaylist pendingTracks 懒结算
- [x] T22 `yarn vue-tsc --noEmit -p tsconfig.json`（ShineMusicWeb）零错误 + 管理端 UI 走查
- [x] T23 修复：网易云/QQ/酷我 parser 的 `GetStringAsync` 遇 `charset=utf8`（无连字符）抛 `InvalidOperationException` 导致 500。新增共享 UTF-8 读取辅助（读字节后按 UTF-8 解码），替换 4 处调用点（NeteasePlaylistParser L37/L131、QQMusicPlaylistParser L36、KuwoPlaylistParser L32）；网络异常（HttpRequestException/超时）在辅助内统一包为 PlaylistParseException，避免裸 500。
- [x] T24 修复：SqlSugar CodeFirst 在 PG 下强制小写标识符导致 `UserPlaylistPendingTracks` 表与列全小写、运行时报 relation/column does not exist。改 SqlSugarSchemaBootstrap：对该表走手写 DDL（PascalCase 带引号 + timestamptz + 正确可空性），并清理历史残留小写表。
