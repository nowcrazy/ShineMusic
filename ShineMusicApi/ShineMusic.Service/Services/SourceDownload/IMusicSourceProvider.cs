// 职责：外部音源平台的统一抽象。
// 每个开放版权平台（Audius/Jamendo）实现 IMusicSourceProvider，
// 对上提供「搜索曲目」与「解析下载地址」两个能力，下载编排层不感知平台差异。

namespace ShineMusic.Service.SourceDownload;

/// <summary>音质等级：lossless（有无损原文件）/ high（≥256k）/ standard。</summary>
public static class SourceQualities
{
    public const string Lossless = "lossless";
    public const string High = "high";
    public const string Standard = "standard";
}

/// <summary>音源搜索结果条目（跨平台统一模型）。</summary>
/// <param name="Provider">平台标识（audius/jamendo）。</param>
/// <param name="ProviderTrackId">曲目在平台上的 Id。</param>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">艺人列表。</param>
/// <param name="AlbumTitle">专辑标题。</param>
/// <param name="DurationMs">时长（毫秒）。</param>
/// <param name="Genres">流派列表。</param>
/// <param name="CoverUrl">封面图 URL（平台 CDN）。</param>
/// <param name="SourcePageUrl">曲目在平台的页面链接（溯源/试听）。</param>
/// <param name="License">许可信息（如 CC-BY）。</param>
/// <param name="Quality">音质等级（见 <see cref="SourceQualities"/>）。</param>
/// <param name="IsLosslessAvailable">平台侧是否有无损原文件可下载。</param>
public sealed record SourceSearchResult(
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

/// <summary>解析后的下载信息。</summary>
/// <param name="DownloadUrl">音频文件下载地址（可能含重定向）。</param>
/// <param name="Container">声明的容器格式（flac/wav/mp3/ogg 等）；空串表示需按响应头/URL 探测。</param>
/// <param name="IsLossless">是否为无损原文件。</param>
/// <param name="BitrateKbps">声明码率（kbps），未知为 null。</param>
/// <param name="Quality">音质等级（见 <see cref="SourceQualities"/>）。</param>
public sealed record SourceDownloadInfo(
    string DownloadUrl,
    string Container,
    bool IsLossless,
    int? BitrateKbps,
    string Quality);

/// <summary>外部音源平台提供者。</summary>
public interface IMusicSourceProvider
{
    /// <summary>平台标识（audius/jamendo），与配置节、任务行的 Provider 字段一致。</summary>
    string Provider { get; }

    /// <summary>该音源当前是否可用（已启用且凭据齐全）。</summary>
    bool IsEnabled { get; }

    /// <summary>不可用的原因说明（如缺少 client_id），可用时为 null。</summary>
    string? DisabledReason { get; }

    /// <summary>按关键词搜索曲目。</summary>
    Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken);

    /// <summary>按平台曲目 Id 获取单曲详情（创建下载任务时取元数据快照）。未找到返回 null。</summary>
    Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken);

    /// <summary>解析下载地址：优先无损原文件，无则兜底平台最高码率流。</summary>
    /// <exception cref="SourceUnavailableException">平台不可用或曲目不可下载时抛出。</exception>
    Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken);
}

/// <summary>音源平台不可用或目标曲目不可下载。</summary>
public sealed class SourceUnavailableException(string message) : Exception(message);

/// <summary>provider 自下载结果：最终落地的音频文件与容器格式。</summary>
/// <param name="FilePath">音频文件绝对路径（由调用方负责归属到隔离区存储键）。</param>
/// <param name="Container">实际容器格式（如 wav/mp3），用于生成存储键扩展名。</param>
/// <param name="Quality">音质等级（见 <see cref="SourceQualities"/>）。</param>
public sealed record SourceFileDownload(string FilePath, string Container, string Quality);

/// <summary>
/// 自带下载能力的音源 provider（如 yt-dlp 子进程下载）：下载队列优先调用该接口而非 HttpClient 直链下载，
/// 实现方需自行处理平台反爬/格式提取，并把最终音频文件落到指定目录。
/// </summary>
public interface ISourceFileDownloader
{
    /// <summary>下载曲目音频到指定目录（文件名主干由调用方给定），返回最终文件与容器格式。</summary>
    /// <exception cref="SourceUnavailableException">平台不可用或曲目不可下载时抛出。</exception>
    Task<SourceFileDownload> DownloadToFileAsync(
        string providerTrackId, string outputDirectory, string fileBaseName, CancellationToken cancellationToken);
}
