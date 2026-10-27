// 职责：外部音源抓歌（Source Download）管理 API 的请求/响应契约。
// 与 Controllers/SourceDownloadController.cs 一一对应，供前端 services/admin-source-download.ts 对齐。

namespace ShineMusic.Model.Contracts;

/// <summary>音源搜索结果条目（跨平台统一模型，与 IMusicSourceProvider.SourceSearchResult 对齐）。</summary>
public sealed record SourceSearchItemDto(
    string Provider,
    string ProviderTrackId,
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    long? DurationMs,
    IReadOnlyList<string> Genres,
    string? CoverUrl,
    string? SourcePageUrl,
    string? License,
    string Quality,
    bool IsLosslessAvailable);

/// <summary>单个音源平台的错误说明（搜索聚合时单源失败不拖垮整体）。</summary>
public sealed record SourceProviderErrorDto(string Provider, string Message);

/// <summary>搜索响应：合并各平台结果 + 失败平台列表。</summary>
public sealed record SourceSearchResponse(
    IReadOnlyList<SourceSearchItemDto> Results,
    IReadOnlyList<SourceProviderErrorDto> ProviderErrors);

/// <summary>创建下载任务请求。</summary>
/// <param name="Provider">音源平台（audius/jamendo/kuwo/netease 等）。</param>
/// <param name="ProviderTrackIds">平台曲目 Id 列表（一次可批量）。</param>
/// <param name="Metadata">可选：按平台曲目 Id 预填的搜索结果元数据。
/// 前端从搜索结果直接带入，避免后端再次调用可能已失效的详情接口（如酷我 getNewMuiseByRid 已返回 HTML）。
/// 缺失的 Id 仍回退到 provider.GetTrackAsync 抓取快照。</param>
public sealed record CreateSourceDownloadTasksRequest(
    string Provider,
    IReadOnlyList<string> ProviderTrackIds,
    IReadOnlyDictionary<string, SourceSearchItemDto>? Metadata = null);

/// <summary>创建下载任务响应：新建/重置入队的任务与因重复被跳过的平台曲目 Id。</summary>
public sealed record CreateSourceDownloadTasksResultDto(
    IReadOnlyList<ExternalDownloadTaskDto> Created,
    IReadOnlyList<string> SkippedDuplicates);

/// <summary>外部下载任务视图（含评分与直通标记，供任务列表/详情展示）。</summary>
public sealed record ExternalDownloadTaskDto(
    Guid Id,
    string Provider,
    string ProviderTrackId,
    string? Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    long? DurationMs,
    string? Quality,
    string? License,
    string? SourcePageUrl,
    string? CoverUrl,
    string Status,
    int? AutoScore,
    bool AutoPublished,
    string? ErrorCode,
    string? ErrorMessage,
    int AutoRetryCount,
    DateTime? NextRetryAtUtc,
    Guid? ImportItemId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool? HasLyrics);

/// <summary>任务分页响应。</summary>
public sealed record ExternalDownloadTaskListDto(
    IReadOnlyList<ExternalDownloadTaskDto> Items,
    int Total,
    int Page,
    int PageSize);
