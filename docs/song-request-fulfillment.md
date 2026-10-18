# 需求池联动上传 + 合规校验 + 可跳过检查全局配置

> 适用项目：ShineMusic  
> 关联模块：`LibraryImportEndpoints.cs`（发布流程）、`SongRequestEndpoints.cs`、`FeatureOptions.cs`  
> 关联文档：[song-request-workflow.md](./song-request-workflow.md)  
> 文档版本：v1.0 · 2026-10-18

## 1. 现在已有的基础（无需重造）

| 能力 | 位置 | 说明 |
|------|------|------|
| 上传时关联需求 | `POST /imports` 接受 `songRequestId`（UploadBatch 第 116 行） | 已支持 |
| 发布时自动满足需求 | PublishItem 第 528–537 行：`request.Status=Fulfilled; MatchedTrackId=track.Id` | 已支持 |
| 审核标记 | `MetadataReviewed`、`ManualReview` 开关 | 已支持 |
| 配置机制 | `FeatureOptions` + `appsettings` 的 `"Features"` 节（`IOptions<T>`） | 可复用 |

**结论：联动上传的"管道"已经通了。缺的是两样：**
1. **匹配校验**——上传的曲目必须确实是需求要的那首（防张冠李戴）。
2. **全局配置**——规定哪些校验在管理员上传时可以跳过。

## 2. 核心漏洞（必须堵）

当前 `PublishItem` 第 533–534 行：

```csharp
request.Status = SongRequestStatuses.Fulfilled;
request.MatchedTrackId = track.Id;
```

**没有任何判断 `track.Title` / `track.Artists` 是否与 `request.Title` / `request.ArtistName` 一致。**
→ 管理员把《你是最美的太阳》的音频关联到《天下》的需求，系统照样标记"已满足"。

## 3. 设计一：匹配校验（TitleArtistMatch，核心防错）

### 3.1 校验逻辑

```
match = compare(normalized(track.Title), request.NormalizedTitle)
     && compare(normalized(track.Artists), request.NormalizedArtistName)

compare(a, b):
    if a == b → 完全匹配（PASS）
    else if editDistance(a, b) <= TitleArtistMatch.MaxEditDistance → 模糊匹配（PASS，记 warning）
    else → 不匹配（FAIL）
```

> 归一化复用现有规则：`value.Trim().ToUpperInvariant()`。  
> 艺术家支持多个（取交集/任一匹配即可；更严时可要求首位艺人必须一致）。

### 3.2 三种结果的处理（按全局配置）

| 结果 | 处理 |
|------|------|
| 完全匹配 | 直接关联 + 满足需求，正常发布 |
| 模糊匹配（编辑距离≤阈值） | 正常发布，但 `AdminAudit` 记一条 "title_artist_fuzzy_match" 留痕 |
| **不匹配** | 见下方「硬拒 vs 可强制」 |

**不匹配时的行为取决于配置 `TitleArtistMatch.Mode`：**
- `Enforced` 且 `AllowAdminOverride=false`（**推荐默认**）：**硬拒发布**，返回 409，文案：
  > "曲目《{track.Title} - {artists}》与需求《{request.Title} - {request.ArtistName}》不一致，无法关联。若确为同一首，请修正元数据；若需强制关联，请联系超级管理员或调整配置。"

  → 这正好挡住"张杰-天下 错配 你是最美的太阳"。
- `Enforced` 且 `AllowAdminOverride=true`（或 `Mode=Warning`）：允许发布，但必须 `forceFulfill=true` + `overrideReason`（必填），并强制写 `AdminAudit` 留痕。

> **行为完全由全局配置决定**（默认 `Enforced` + 不可覆盖）。两种结果的错误码不同：硬拒为 `song_request_mismatch`，可覆盖为 `song_request_mismatch_override_required`；前端据此决定是否弹出「强制关联」确认框——配置禁止覆盖时不会弹，避免无效重试。

> **设计取舍**：这是整个需求的"安全网"，强烈建议默认 `AllowAdminOverride=false`（核心校验不可跳过）。其余版权/元数据类校验才放开给管理员跳过。

## 4. 设计二：全局配置（SongRequestFulfillmentOptions）

### 4.1 新增配置节 `"SongRequestFulfillment"`（与 `"Features"` 平级，复用 `IOptions<T>` 模式）

```jsonc
// appsettings.json
{
  "SongRequestFulfillment": {
    "Checks": {
      // 校验项: 模式(Enforced|Warning|Disabled) + 管理员是否可跳过
      "TitleArtistMatch":   { "Mode": "Enforced", "AllowAdminOverride": false }, // 核心，不可跳过
      "DuplicateInLibrary": { "Mode": "Enforced", "AllowAdminOverride": true  }, // 防重复，admin 可跳过
      "CopyrightCheck":     { "Mode": "Warning",  "AllowAdminOverride": true  }, // 版权扫描，admin 可跳
      "MetadataAutoMatch":  { "Mode": "Warning",  "AllowAdminOverride": true  }, // 自动元数据匹配
      "AudioFormat":        { "Mode": "Enforced", "AllowAdminOverride": false }, // 可播放格式
      "LyricsPresent":      { "Mode": "Disabled", "AllowAdminOverride": true  }  // 歌词非必须
    },
    "TitleArtistMatch": { "MaxEditDistance": 1, "Normalize": true },
    "AdminBypassRequiresReason": true   // admin 跳过任何检查都必须填原因并记审计
  }
}
```

### 4.2 模式语义

| Mode | 含义 | 校验失败后果 |
|------|------|--------------|
| `Enforced` | 强制 | 硬拒（除非该检查 `AllowAdminOverride=true` 且 admin 携带 reason 强制） |
| `Warning` | 警告 | 允许通过，但必须记审计 / 前端黄条提示 |
| `Disabled` | 关闭 | 该检查完全不执行 |

`AllowAdminOverride` = 该检查是否允许管理员用 reason 强制通过。

### 4.3 校验项清单（默认建议）

| 校验项 | 默认 Mode | 可否 admin 跳过 | 说明 |
|--------|-----------|----------------|------|
| **TitleArtistMatch** | Enforced | ❌ 不可跳过 | **核心防张冠李戴**，永远不可跳 |
| AudioFormat | Enforced | ❌ | 必须是可转码/播放的格式 |
| DuplicateInLibrary | Enforced | ✅ | 库内已存在相同音频则拒，admin 可有意跳过 |
| CopyrightCheck | Warning | ✅ | 版权自动扫描结果，admin 上传已授权内容可跳 |
| MetadataAutoMatch | Warning | ✅ | 自动元数据匹配失败不阻塞，admin 手填即可 |
| LyricsPresent | Disabled | ✅ | 歌词非必须，本就不校验 |

> 你问的"哪些可以调过检查"——**就是这张表里 `AllowAdminOverride=true` 的那些**（DuplicateInLibrary / Copyright / MetadataAutoMatch / LyricsPresent）。`TitleArtistMatch` 和 `AudioFormat` 是底线，不可跳。

## 5. 校验执行点（Where to run）——均已落地

| 时机 | 位置 | 作用 |
|------|------|------|
| **上传/解析完成后（提示层）** | 所有导入项读取端点（`GetBatch`/`GetItem`/`ListItems`/`ListBatches`）返回 `songRequestMatch` | 元数据一解析出来就能看到「不一致」，列表标红/标黄，**不阻断** |
| **发布时（主防线）** | `PublishItem` 写 `Fulfilled` 之前 | 真正决定能否关联/满足需求 |

> 主防线必须在 `PublishItem` 内、写 `Fulfilled` 之前做。上传时校验只是 UX 友好提示。

### 5.1 上传时提示是怎么做到「即时」的

导入是异步流水线：上传只落文件，元数据由后台 worker 解析。所以匹配结论**不落库、读取时实时计算**：

- `LibraryImportItemDto` 新增 `SongRequestMatch`：`{ kind, trackDisplay, requestDisplay, blocking, message }`；
- 只有 `SongRequestId != null` 且 `Title` 已解析出来才计算，否则为 `null`（前端不展示）；
- 管理员修正元数据后，下一次轮询（导入页每 3s 一次）结论自动刷新，**无需等发布**；
- `blocking = mismatch && !AllowAdminOverride`：为 `true` 表示红字「发布会被拒绝」，为 `false` 表示黄字「需填原因强制关联」。

前端呈现：
- 列表行：不一致显示红色「不匹配」小标，模糊匹配显示黄色「有差异」；
- 详情面板：一行结论条（绿=一致 / 黄=有差异 / 红=不一致），文案直接告诉管理员下一步怎么做（改元数据 / 解绑 / 强制关联）。

## 6. 联动上传 UX（解决"回上传界面麻烦"）——已落地

实现方式：需求池页面管理员点击「上传并满足」→ 跳转导入页并携带需求参数（不弹独立上传框，复用现有导入流水线）。

1. 管理员在需求池卡片点 **「上传并满足」**（仅 `isAdmin` 且状态为 `open/planned/in_progress` 时显示）；管理端「需求管理」页也有同一入口。
2. 跳转 `/pages/admin/import?songRequestId=..&title=..&artistName=..&albumTitle=..`。
3. 导入页顶部显示目标需求横幅；上传时把 `songRequestId` 随 multipart 表单一起提交。
4. 发布时跑匹配校验：
   - 一致 → 自动 fulfill，并写时间线 + 给提交人发「已入库」站内信。
   - 不一致且配置允许覆盖 → 后端返回 409 `song_request_mismatch_override_required`，前端弹「填写原因后强制关联」，确认后带 `forceFulfill=true&overrideReason=..` 重试，写入审计。
   - 不一致且配置禁止覆盖 → 返回 409 `song_request_mismatch`，前端不弹框，直接提示改元数据。
5. 批量上传误挂：详情面板可「解绑」单条，解绑后该条不再触发匹配校验。

## 7. 代码落点建议（伪代码）

### 7.1 新 Options 类

```csharp
public sealed class SongRequestFulfillmentOptions
{
    public const string SectionName = "SongRequestFulfillment";
    public Dictionary<string, CheckConfig> Checks { get; set; } = new();
    public TitleArtistMatchOptions TitleArtistMatch { get; set; } = new();
    public bool AdminBypassRequiresReason { get; set; } = true;
}

public sealed class CheckConfig
{
    public string Mode { get; set; } = "Enforced";      // Enforced | Warning | Disabled
    public bool AllowAdminOverride { get; set; }
}

public sealed class TitleArtistMatchOptions
{
    public int MaxEditDistance { get; set; } = 1;
    public bool Normalize { get; set; } = true;
}
```

### 7.2 校验引擎（插入 `PublishItem` 第 532 行前）

```csharp
if (item.SongRequestId.HasValue)
{
    var request = await db.SongRequests.FirstOrDefaultAsync(c => c.Id == item.SongRequestId.Value, ct);
    if (request is not null)
    {
        var verdict = SongRequestMatchValidator.Validate(
            trackTitle: item.Title!, trackArtists: DeserializeList(item.ArtistsJson),
            request: request, options: fulfillmentOpts);

        if (verdict.Kind == MatchVerdictKind.Mismatch)
        {
            var cfg = fulfillmentOpts.Checks["TitleArtistMatch"];
            if (cfg.Mode == "Enforced" && !cfg.AllowAdminOverride)
                return Conflict(HttpContext,
                    $"曲目《{item.Title} - {artists}》与需求《{request.Title} - {request.ArtistName}》不一致，无法关联。");

            // 允许强制：要求 admin 显式携带 reason
            if (!publishRequest.ForceFulfill || string.IsNullOrWhiteSpace(publishRequest.OverrideReason))
                return Conflict(HttpContext,
                    "曲目与需求不一致，若要强制关联请提供 overrideReason。");
            AdminAudit.Add(HttpContext, db, "song_request.forced_match", "song_request", request.Id,
                new { trackTitle = item.Title, requestTitle = request.Title, reason = publishRequest.OverrideReason });
        }
        // 模糊匹配 / 强制关联均落地审计
    }
}
```

### 7.3 发布端点新增可选 query 参数（已落地）

```csharp
// POST /api/v1/admin/library/import-items/{id}/publish
//   ?forceFulfill=true&overrideReason=...
// 采用 query 参数而非 body，保证既有“无 body 调用”完全不受影响。
public async Task<IResult> PublishItem(
    Guid itemId,
    [FromQuery] bool forceFulfill,
    [FromQuery] string? overrideReason,
    MusicDbContext db,
    IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
    /* 其余依赖不变 */)
```

## 8. 审计与通知

- **强制关联必须记 `AdminAudit`**：动作 `song_request.forced_match`，带 `trackTitle / requestTitle / reason`。
- **模糊匹配记 `title_artist_fuzzy_match`**：留痕但不阻断。
- **通知提交人**：需求状态变为 `fulfilled`（无论正常还是强制）时，按 [workflow 文档](./song-request-workflow.md) 的文案模板通知："你的点歌《天下》已上架 👉 {trackUrl}"。若属强制关联，可在后台标记 `forced=true` 供运营复盘。

## 9. 待办（V 计划）

- [x] V1：新增 `SongRequestFulfillmentOptions` + `appsettings` 配置节
- [x] V1：实现 `SongRequestMatchValidator`（归一化 + 编辑距离 + 三种结果）
- [x] V1：在 `PublishItem` 中插入匹配校验（含强制关联 reason 分支）
- [x] V1：发布端点加 `forceFulfill` / `overrideReason` query 参数
- [x] V1：PATCH 满足需求路径同样接入匹配校验（防绕过）
- [x] V1：前端「上传并满足」入口（需求池按钮 → 导入页横幅 → 上传带 songRequestId → 409 强制关联确认框）
- [x] V1：批量上传误挂时「逐条解绑」（`DELETE /admin/library/import-items/{id}/song-request` + 导入页解绑按钮）
- [x] V1：上传时即时校验提示（导入项 DTO 带 `songRequestMatch`，列表标红/标黄 + 详情结论条）
- [ ] V2：校验项引擎化（`DuplicateInLibrary` / `CopyrightCheck` / `MetadataAutoMatch` / `AudioFormat` / `LyricsPresent` 目前只有配置位，尚未接入执行）

---

### 与 workflow 文档的关系

| 文档 | 覆盖 |
|------|------|
| [song-request-workflow.md](./song-request-workflow.md) | 需求生命周期状态机、权限矩阵、撤回/拒绝/归档/再开启 |
| **本文** | 管理员从需求池联动上传、匹配校验（防错配）、可跳过检查的全局配置 |

两文档共同构成"点歌需求"功能的完整后台运营规范。
