# Spec：解析外部歌单导入（Web 管理端 + App 端）

## 目标

用户在 Web 管理端（ShineMusicWeb）和 App 端（ShineMusicApp）粘贴外部平台歌单链接，系统解析歌单曲目列表，按「歌名 + 艺人」匹配已有曲库，并生成新歌单；未命中的曲目走后台静默下载（复用音源抓歌流水线），歌单立即生成，未下载完成的曲目在 App 详情页灰色不可播放，下载发布完成后自动变为可播放。

## 支持平台

| 平台                                | 解析方式                                                                                                           | 说明                                                                                       |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| 网易云音乐 (music.163.com)             | 歌单页 HTML 内嵌 `textarea#song-list-pre-data` JSON                                                                 | 兜底 `/api/v6/playlist/detail`                                                             |
| QQ 音乐 (y.qq.com)                  | `c.y.qq.com/v8/fcg-bin/fcg_v8_playlist_cp.fcg?id={disstid}&format=json&newsong=1&platform=h5`                  | Referer: y.qq.com                                                                        |
| 酷我音乐 (kuwo.cn)                    | `nplserver.kuwo.cn/pl.svc?op=getlistinfo&pid={id}&pn=0&rn=500&encode=utf8&keyset=pl2012&identity=kuwo&pcmp4=1` | 伪 JSON 归一化（同 KuwoSourceProvider 做法）                                                      |
| YouTube Music (music.youtube.com) | YtDlpBridge 新增 `GetPlaylistAsync(url)`：`--flat-playlist --dump-single-json`                                    | 国内需配置 `SourceDownload:YtDlp:Proxy`，UI 提示                                                 |
| Spotify (open.spotify.com)        | 不支持                                                                                                            | 匹配时抛 `SourceUnavailableException("Spotify 歌单解析暂不支持（需官方 API 凭据），可复制到 YouTube Music 后导入")` |

## 数据模型

新实体 `UserPlaylistPendingTrack`（加入 `ShineMusicApi/ShineMusic.Model/Entities/IdentityEntities.cs`，`[SugarTable("UserPlaylistPendingTracks")]`，SqlSugarSchemaBootstrap 自动建表，无需注册）：

* `Id` Guid PK

* `PlaylistId` Guid（所属歌单）

* `Title` string、`ArtistsJson` string（JSON 数组）、`AlbumTitle` string?、`DurationMs` int?、`CoverUrl` string?

* `SourcePlatform` string（netease/qqmusic/kuwo/ytmusic/spotify）

* `SourceTrackId` string?、`SourceUrl` string?

* `SortOrder` int（原歌单内序号，0 起）

* `Status` string：`pending` / `downloading` / `no_source` / `failed`

* `DownloadTaskId` Guid?（关联 ExternalDownloadTask）

* `CreatedAtUtc` / `UpdatedAtUtc` DateTime

## 后端设计

### 1. 解析层 `ShineMusicApi/ShineMusic.Service/Services/PlaylistImport/`

```csharp
public interface IPlaylistParser
{
    string Platform { get; }
    bool CanHandle(string url);
    Task<ParsedPlaylist> ParseAsync(string url, CancellationToken ct);
}
public sealed record ParsedPlaylist(string Name, string? CoverUrl, string? Description, IReadOnlyList<ParsedPlaylistTrack> Tracks);
public sealed record ParsedPlaylistTrack(string Title, IReadOnlyList<string> Artists, string? AlbumTitle, int? DurationMs, string? SourceTrackId, string? SourceUrl);
```

实现四个 parser（均单例，注册入 `IEnumerable<IPlaylistParser>`）：

* **NeteasePlaylistParser**（命名 HttpClient `"netease"`，超时 20s）：URL 形态 `/playlist?id=`、`/#/playlist?id=`、`/playlist/{id}`。GET 歌单页（带 UA + Referer），提取 `textarea#song-list-pre-data` 内 JSON（tracks 含 name/artists\[].name/album.name/duration/id）。解析失败兜底调 `/api/v6/playlist/detail`。

* **QQMusicPlaylistParser**（命名 HttpClient `"qqmusic"`，超时 20s）：URL 形态 `y.qq.com/n/ryqq/playlist/{id}`、`playlistid=` 参数。响应 `cdlist[0].songlist`：songname / singer\[].name / albumname / interval（秒） / songmid。

* **KuwoPlaylistParser**（复用现有 kuwo 命名 HttpClient）：URL `kuwo.cn/playlist_detail/{id}`。响应 `musiclist`：name / artist / album / duration（秒） / id（数字，SourceTrackId 存 `"MUSIC_{id}"`）。伪 JSON 用 KuwoSourceProvider 同款归一化。

* **YtDlpPlaylistParser**：匹配 ytmusic 走 YtDlpBridge 新方法 `GetPlaylistAsync`（entries 每项取 title / artist 或 uploader / duration 秒 / id / url）；匹配 spotify 直接抛 `SourceUnavailableException`（上述中文文案）。

### 2. 编排层 `PlaylistImportService`（scoped）

* `ParsePreviewAsync(url, ct)`：选 parser 解析，逐曲 `MatchLibraryAsync` 标 matched，返回预览。

* `MatchLibraryAsync(track, ct)`：归一化（trim + lower + 压缩空白）查 `PublishedAt != null` 的 Track：标题归一化相等 + TrackArtist join Artist 名归一化相等或包含；多候选取 |duration 差| < 5s 且最小者。

* `ImportAsync(url, name?, userId, ct)`：

  1. 解析歌单；
  2. 建 `UserPlaylist`（名截 100，默认用原歌单名）；
  3. 逐曲：matched → 插 `UserPlaylistTrack`（SortOrder = i）；未中 → 插 `UserPlaylistPendingTrack`（Status=pending）+ 用 kuwo provider `SearchAsync($"{title} {artist}", 5)` 取最佳候选（标题归一化相等优先、时长最接近）→ 逐条调 `SourceDownloadService.CreateTasksAsync`（单条失败不影响整体）→ 回填 `DownloadTaskId` / `Status=downloading`；搜不到 → `Status=no_source`；
  4. 返回 `{ playlistId, total, matched, pending }`。

* `ResolvePendingAsync(playlistId, ct)`（懒结算，GetPlaylist 时调用）：

  * pending 有 `DownloadTaskId` → 查 task → `ImportItem.PublishedTrackId` 非空 → 插 `UserPlaylistTrack`（原 SortOrder）+ 删 pending 行；

  * task failed 且 `ErrorCode=source_duplicate` → 用标题+艺人重匹配曲库，命中则同样转正；

  * 其他 failed → `Status=failed`；仍在下载 → `Status=downloading`。

### 3. 控制器 `PlaylistImportController`

`Route("api/v1/me/playlist-imports")`，`[Authorize]` `[FeatureGate(nameof(FeatureOptions.UserCollections))]`：

* `POST parse` 体 `{ url }` → `{ platform, name, coverUrl, total, tracks: [{ title, artists, albumTitle, durationMs, matched, matchedTrackId? }] }`

* `POST import` 体 `{ url, name? }` → `{ playlistId, total, matched, pending }`

* 不支持的平台 / 解析失败 → 400 中文友好消息。

### 4. 歌单详情改造（`UserCollectionController.GetPlaylist`）

* 方法开头调 `ResolvePendingAsync(playlistId, ct)`；

* 响应并入 `pendingTracks`；`PlaylistDetailDto`（UserLibraryDtos.cs:24）加可选参数 `IReadOnlyList<PlaylistPendingTrackDto>? PendingTracks = null`（保持构造兼容）；

* 新 DTO `PlaylistPendingTrackDto(Guid Id, string Title, IReadOnlyList<string> Artists, string? AlbumTitle, int? DurationMs, string? CoverUrl, int SortOrder, string Status)`；

* 新增 `DELETE /api/v1/me/playlists/{playlistId:guid}/pending/{pendingId:guid}`（校验归属后删除 pending 行）。

### 5. DI 注册（Program.cs）

* `services.AddHttpClient("netease", ...)` / `("qqmusic", ...)` 超时 20s（仿 L331-350 现有模式）；

* 四个 parser 注册为单例并汇入 `IEnumerable<IPlaylistParser>`；

* `services.AddScoped<PlaylistImportService>()`；

* YtDlpBridge 加 `GetPlaylistAsync(string url, CancellationToken ct)`：`--flat-playlist --dump-single-json` 输出解析 `{ title, entries[] }`。

## Web 管理端（ShineMusicWeb）

1. `src/services/playlist-import.ts`：仿 admin-source-download.ts 模式（`http` from './http'），类型就地定义（PreviewTrack / ParseResult / ImportResult），方法 `parsePlaylist(url)` / `importPlaylist(url, name?)`。
2. `src/pages/admin/playlist-import.vue`：URL 输入框 + 「解析」按钮 → 预览表格（序号 / 标题 / 艺人 / 专辑 / 时长 / 状态 tag：已匹配(绿) / 待下载(橙) / 无可用源(灰)）→ 可改歌单名 → 「导入」按钮 → 结果摘要（总数 / 已匹配 / 待下载）+ 提示「缺失曲目将在后台自动下载」。ytmusic 时显示代理提示。
3. `src/router/index.ts` 注册 `{ path: 'playlist-import', meta: { title: '歌单解析' } }`。
4. `AdminLayout.vue` `menuItems` 加菜单项「歌单解析」。

## App 端（ShineMusicApp）

1. `src/services/playlist-import.ts`：`parsePlaylistImport(url)` / `importPlaylist(url, name?)`（复用 api request 封装）。
2. 类型：`src/types/music.ts` 加 `ApiPlaylistPendingTrack`；`ApiPlaylistDetail` 加 `pendingTracks?: ApiPlaylistPendingTrack[]`；同步改 `api.generated.ts` 对应定义。
3. 新页 `src/pages/playlist/import.vue`：输入 URL → 解析 → 预览列表（标题/艺人/状态文案）→ 导入 → 成功跳 `pages/playlist/detail?id=...`；`pages.json` 注册页面。
4. `src/pages/home/index.vue`「MY PLAYLISTS」区（L1137-1159）create-playlist 按钮旁加「导入」入口，跳 import 页。
5. `src/pages/playlist/detail.vue`：

   * 合并渲染：slot 0..total-1，`pending.sortOrder` 命中渲染灰色 pending 行，否则取下一 playable track，溢出 playable 追加尾部；

   * pending 行灰色（opacity 降低），状态文案：downloading=下载中 / pending=等待下载 / no\_source=暂无此歌 / failed=获取失败；不可点击播放；

   * 存在 status 为 pending/downloading 的行时 8s 轮询重新 load，`onUnload` 清理定时器。
6. i18n（`src/locale/messages/zh-Hans.ts` / `en.ts` 等）：`homePage.import`、`importPage.*`（title/urlPlaceholder/parse/importing/import/success/failed）、`playlistPage.pendingDownloading/pendingWaiting/pendingNoSource/pendingFailed`。

## 错误与提示

* 不支持的链接：「暂不支持该平台歌单，当前支持：网易云音乐、QQ 音乐、酷我音乐、YouTube Music」。

* 解析失败（链接失效/平台风控）：「歌单解析失败，请确认链接为公开歌单后重试」。

* YT Music 无代理：「YouTube Music 解析需要配置网络代理（SourceDownload:YtDlp:Proxy）」。

* Spotify：「Spotify 歌单解析暂不支持（需官方 API 凭据），可复制到 YouTube Music 后导入」。

## 验证步骤

1. `dotnet build -c Release`（cwd `ShineMusicApi/ShineMusic.Api`）零错误。
2. 重启 5180 后端，curl 实测：

   * `POST /api/v1/me/playlist-imports/parse` 网易云公开歌单链接 → 返回曲目列表且部分 matched=true；

   * `POST /api/v1/me/playlist-imports/import` → 返回 playlistId；`GET /api/v1/me/playlists/{id}` → 含 pendingTracks，下载发布后再调消失、tracks 增加。
3. `ShineMusicWeb` 跑 `yarn vue-tsc --noEmit -p tsconfig.json` 零错误；管理端 UI 走查（admin <3449352732@qq.com> / Admin\@123456）解析 + 导入全流程。
4. App 端页面编译（uni-app 构建/HBuilder 基座运行）确认 import 页与 detail 灰色行渲染正常。

## 不做的事

* 不做 Spotify 官方 API 接入（仅友好提示）。

* 不做 Apple Music / 酷狗 / 咪咕等其他平台。

* 不改已有歌单接口的既有响应字段（仅追加 PendingTracks）。

* 不碰 git commit / push。

