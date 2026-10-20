# 运行时架构

系统是**一台 API 进程 + 一份 SQLite + 两块本地磁盘目录 + 一个 uni-app 客户端**。客户端 H5 走 Vite 代理；真机 App 直连局域网或生产域名。

```
┌──────────────── uni-app / Vue 3 ─────────────────┐
│  pages/  页面                                     │
│  services/  HTTP、会话、曲库、播放器、管理接口     │
│  InnerAudioContext 全局单例播放                    │
└──────────────┬───────────────────────────────────┘
               │ /api/v1  JSON + Range 音频/封面
               ▼
┌──────────── ASP.NET Core (.NET 10) ──────────────┐
│  Controllers（Endpoints/）                        │
│  Identity + JWT Bearer                            │
│  FeatureGate / 限流 / 操作日志 / 数据 SQL 日志     │
│  HostedService：导入队列消费、点歌自动归档          │
│  ffmpeg / ffprobe / TagLib / LRCLIB / iTunes 探测  │
└──────┬───────────────┬───────────────────────────┘
       │               │
       ▼               ▼
  SQLite            本地磁盘
  data/*.db         data/library（正式）
                    data/quarantine（隔离）
```

## 后端分层

| 层 | 位置 | 职责 |
|---|---|---|
| 入口组装 | `Program.cs` | 选项校验、CORS、JWT、迁移、播种角色、`--create-admin` |
| HTTP 边界 | `Endpoints/` | MVC Controller，统一 `ApiErrorResponse` |
| 领域规则 | `Domain/` | 目前主要是点歌状态机（纯函数） |
| 应用服务 | `Services/` | 导入队列、转码、歌词、推荐、存储治理、审计 |
| 持久化 | `Data/` | EF Core `MusicDbContext` + 实体 + 迁移 |
| 契约 | `Contracts/` | 对外 DTO 与错误码 |
| 选项 | `Options/` | 强类型配置，`ValidateOnStart` |

启动时自动 `MigrateAsync`，并确保 `user` / `admin` 角色存在。曲库为空时，`EditorialCollectionSeeder` 按现状幂等生成编辑精选（不覆盖已有合集）。

## 媒体与元数据路径

1. 上传写入 **隔离目录** `Storage:QuarantineRoot`，库里只存 `StorageKey`，永不把物理路径返回客户端。
2. 后台 worker 校验扩展名/大小/内容哈希 → TagLib 读标签与内嵌封面 → ffmpeg 转 `aac_m4a` 192 kbps。
3. 歌词优先级：内嵌标签 → 同名旁挂 `.lrc`/`.txt` → 可选 LRCLIB。
4. 导入阶段可用 iTunes Search 回填 `ItunesTrackId`、发行国家、`Region`（华语/欧美/日韩/其他）、`Explicit`。
5. 管理员发布后，原文件与转码文件进入 **正式目录** `Storage:LibraryRoot`，生成 `Track` + `MediaAsset`。公开播放走 `GET /api/v1/tracks/{id}/stream`，支持 HTTP Range。
6. 用户上传走同一套处理流水线，但 `IsUserUpload=true`：处理完先私有入库（`PublishedAt` 为空，仅上传者可见），管理员再决定是否转公开。

内容哈希在 `MediaAsset` 上唯一，用于去重。同一曲目可有多个媒体版本；`IsPrimary` / `IsPublished` 决定默认播放资源。

## 认证与跨域

- 访问令牌：JWT，默认 15 分钟；刷新令牌默认 30 天，库内只存哈希，刷新时轮换。
- `<audio>` 无法带 Authorization 头：仅 `/stream` 与 `/preview` 允许 `?access_token=`。
- CORS 优先读 `Cors:AllowedOrigins`；未配时由 `config/hosts.json` 推导当前环境的前端来源，空来源则不放行。
- 401/403/未处理异常/空 4xx 都收敛成 `{ error: { code, message, traceId } }`，与前端 `http.ts` 对齐。

## 功能开关

`GET /api/v1/system/capabilities` 下发非敏感开关。关闭的功能路由由 `FeatureGate` 返回统一 404。前端只用来显示入口，**权限仍由 API 执行**。

当前 `appsettings.json` 默认：匿名播放、收藏、点歌、管理员导入、用户上传、人工审核、精选、播放事件、个性化推荐、服务端转码均为开启。其中 **人工审核** 与 **服务端转码** 启动时强制为 true。

## 前后端契约同步

不要在前端手写一套与后端平行的 DTO。流程：

1. 改 `backend/ShineMusic.Api/Contracts/`（以及少数 Endpoints 内的 public record）
2. `python backend/tools/generate-ts-contracts.py` 生成 `frontend/src/types/api.generated.ts`
3. `yarn --cwd frontend check:contracts` / CI `--check` 防止漂移
4. 界面扁平模型 `Track` 等仍在 `frontend/src/types/music.ts`，由 `catalog.mapTrack` 从生成类型映射

## 日志

- `logs/operations`：HTTP 操作日志（认证前挂载，能记到 401）
- `logs/data`：EF 实际 SQL
- `logs/app`：未处理异常
- 审计表 `AdminAuditEntries`：管理写操作留痕，带 `traceId`

日志与数据库文件都不应提交。密钥、路径、邀请令牌不得写入响应或普通日志。
