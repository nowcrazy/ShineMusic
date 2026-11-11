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
    bool IsLosslessAvailable,
    int? ReleaseYear = null);

/// <summary>单个音源平台的错误说明（搜索聚合时单源失败不拖垮整体）。</summary>
public sealed record SourceProviderErrorDto(string Provider, string Message);

/// <summary>搜索响应：合并各平台结果 + 失败平台列表 + 因不可下载被过滤的数量。</summary>
public sealed record SourceSearchResponse(
    IReadOnlyList<SourceSearchItemDto> Results,
    IReadOnlyList<SourceProviderErrorDto> ProviderErrors,
    int FilteredCount = 0);

/// <summary>
/// 创建下载任务请求。
/// 两种模式互斥：
///   1) 单 provider 模式（旧前端兼容）：Provider + ProviderTrackIds + Metadata；
///   2) 多候选换源模式（新前端）：Candidates —— 跨 provider 搜索结果已按音质从好到坏排序，
///      任务创建后自动带 CandidateProvidersJson，下载阶段按顺序依次尝试换源。
/// </summary>
public sealed record CreateSourceDownloadTasksRequest(
    string? Provider = null,
    IReadOnlyList<string>? ProviderTrackIds = null,
    IReadOnlyDictionary<string, SourceSearchItemDto>? Metadata = null,
    IReadOnlyList<SourceSearchItemDto>? Candidates = null);

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
    bool? HasLyrics,
    int? ReleaseYear = null,
    IReadOnlyList<TaskAttemptErrorDto>? AttemptErrors = null);

/// <summary>下载任务单次换源尝试的失败记录（对应 AttemptErrorsJson 的单条）。</summary>
public sealed record TaskAttemptErrorDto(
    string Provider,
    int AttemptIndex,
    string? ErrorCode,
    string? Message);

/// <summary>任务分页响应。</summary>
public sealed record ExternalDownloadTaskListDto(
    IReadOnlyList<ExternalDownloadTaskDto> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// 换源重试请求：对已失败任务换一个音源平台重新创建下载任务。
/// </summary>
/// <param name="Provider">可选：指定目标音源平台。留空则自动在除当前 provider 外的所有已启用平台上搜索匹配。</param>
public sealed record SwitchSourceRequest(string? Provider = null);

/// <summary>
/// 换源重试响应：新创建的任务 + 换源来源信息。
/// </summary>
public sealed record SwitchSourceResponse(
    Guid NewTaskId,
    string NewProvider,
    string NewProviderTrackId,
    string? Title,
    IReadOnlyList<string> Artists,
    bool AutoMatched,
    string? Message);

/// <summary>换源失败原因（service 层返回给 controller 映射为具体 HTTP 状态码）。</summary>
public enum SwitchSourceFailureKind
{
    TaskNotFound,
    NotFailed,
    InsufficientMetadata,
    ProviderUnavailable,
    NoMatchFound,
    DuplicateTask
}

/// <summary>未匹配/无音源曲目：来自「加入下载队列」或「导入歌单」两条链路中未建任务的条目。</summary>
/// <param name="Id">条目 Id。</param>
/// <param name="Origin">来源链路：queue（独立加入下载队列）/ playlist（歌单待补齐）。</param>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">艺人列表。</param>
/// <param name="AlbumTitle">专辑名。</param>
/// <param name="SourcePlatform">来源平台标识（netease/qqmusic/kuwo/kugou/qishui 等）。</param>
/// <param name="PlaylistName">所属歌单名；queue 链路为 null。</param>
/// <param name="Status">状态：no_source（搜索无匹配）/ failed（任务失败）。</param>
/// <param name="Reason">中文原因摘要（由逐源诊断生成）。</param>
/// <param name="CreatedAtUtc">条目创建时间（UTC）。</param>
public sealed record UnmatchedTrackDto(
    Guid Id,
    string Origin,
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    string? SourcePlatform,
    string? PlaylistName,
    string Status,
    string Reason,
    DateTime CreatedAtUtc);

/// <summary>未匹配曲目分页响应；NoSource / TaskFailed 为不受分页影响的总数，用于标签页计数。</summary>
public sealed record UnmatchedTrackListDto(
    IReadOnlyList<UnmatchedTrackDto> Items,
    int Total,
    int Page,
    int PageSize,
    int NoSource,
    int TaskFailed);
