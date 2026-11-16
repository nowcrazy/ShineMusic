// 职责：V2.0 统一曲目模型与结构化下载结果（提案 §3 / §5 / §11 的「结果可追溯」）。
//
// 关键约束：曲目身份一律复用 ShineMusic.Service.SourceDownload.TrackIdentity，
// 这里**不**新建第二套标题/艺人模型，也**不**复制归一化逻辑——
// 唯一决策点是 TrackIdentityMatcher.Evaluate，由 SingleDecisionPointTests 反射锁定。
// 本层只做「跨平台结果 → 统一模型」的搬运，不做匹配判定。

using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service.SourceProviders.Abstractions;

/// <summary>音频容器 / 真实编码族。Phase 3 由 ffprobe 回填，Phase 1 仅承载声明值。</summary>
public enum AudioFormat
{
    /// <summary>未知。</summary>
    Unknown,

    /// <summary>FLAC 无损。</summary>
    Flac,

    /// <summary>MP3。</summary>
    Mp3,

    /// <summary>M4A / AAC。</summary>
    M4a,

    /// <summary>Ogg Vorbis。</summary>
    Ogg,

    /// <summary>WAV。</summary>
    Wav,

    /// <summary>ALAC 无损。</summary>
    Alac,
}

/// <summary>
/// 质量等级：S=无损（首选）/ A=320K（可接受）/ 低于 320K（禁止自动入库）。
/// 分级只描述「实际文件」，不能由文件扩展名推断（提案 §5.1）。
/// </summary>
public enum AudioQualityLevel
{
    /// <summary>未探测。</summary>
    Unknown,

    /// <summary>B 级：低于 320K，禁止自动入库。</summary>
    Below320K,

    /// <summary>A 级：320K 高码率有损，可接受。</summary>
    High320K,

    /// <summary>S 级：无损，首选。</summary>
    Lossless,
}

/// <summary>
/// 实际媒体探测结果（提案 §5.2 的 ffprobe 检查项）。
/// Phase 1 只定义字段；真实探测、试听判定与 SHA256 校验属 Phase 3。
/// </summary>
public sealed record AudioQuality(
    AudioFormat Format,
    AudioQualityLevel Level,
    string? Codec = null,
    int? BitrateKbps = null,
    int? SampleRateHz = null,
    int? BitsPerSample = null,
    int? Channels = null,
    long? DurationMs = null,
    long? FileSizeBytes = null,
    string? Sha256 = null);

/// <summary>
/// 跨平台统一曲目模型。身份部分直接持有 <see cref="TrackIdentity"/>，
/// 避免提案目录里的 Core/Models/TrackIdentity.cs 与现有唯一决策点形成第二套身份实现。
/// </summary>
public sealed record MusicTrack(
    string ProviderName,
    string ProviderTrackId,
    TrackIdentity Identity,
    string Quality,
    bool IsLosslessAvailable,
    string? SourcePageUrl = null,
    string? CoverUrl = null,
    string? License = null,
    IReadOnlyList<string>? Genres = null,
    int? ReleaseYear = null)
{
    /// <summary>从现有 SourceSearchResult 转换：纯字段搬运，不做任何身份判定。</summary>
    public static MusicTrack From(SourceSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new MusicTrack(
            result.Provider,
            result.ProviderTrackId,
            new TrackIdentity(result.Title, result.Artists, result.DurationMs, result.AlbumTitle),
            result.Quality,
            result.IsLosslessAvailable,
            result.SourcePageUrl,
            result.CoverUrl,
            result.License,
            result.Genres,
            result.ReleaseYear);
    }

    /// <summary>
    /// 转回既有 SourceSearchResult（Phase 2 收口期下游 DTO / 兜底匹配 / 评分链仍用旧模型）。
    /// 纯字段搬运：Genres 与 ReleaseYear 在 Phase 1 曾被丢弃，补齐后这里不再丢字段。
    /// </summary>
    public SourceSearchResult ToLegacy() => new(
        ProviderName,
        ProviderTrackId,
        Identity.Title,
        Identity.Artists,
        Identity.AlbumTitle,
        Identity.DurationMs,
        Genres ?? Array.Empty<string>(),
        CoverUrl,
        SourcePageUrl,
        License,
        Quality,
        IsLosslessAvailable,
        ReleaseYear);

    /// <summary>按现有 SourceQualities 的语义推断质量等级（仅作声明值，非实测结论）。</summary>
    public AudioQualityLevel DeclaredQualityLevel() =>
        string.Equals(Quality, SourceQualities.Lossless, StringComparison.OrdinalIgnoreCase)
            || IsLosslessAvailable
            ? AudioQualityLevel.Lossless
            : string.Equals(Quality, SourceQualities.High, StringComparison.OrdinalIgnoreCase)
                ? AudioQualityLevel.High320K
                : AudioQualityLevel.Unknown;
}

/// <summary>下载请求。<paramref name="ExpectedDurationMs"/> 供 Phase 3 试听片段检测使用。</summary>
public sealed record DownloadRequest(
    string ProviderTrackId,
    AudioQualityLevel PreferredQuality,
    string OutputDirectory,
    string FileBaseName,
    long? ExpectedDurationMs = null);

/// <summary>
/// 下载阶段。区分「拿到直链」与「拿到真实音频文件」是刻意设计：
/// 搜索成功 ≠ 可以下载，解析成功 ≠ 文件已落地（提案 §11 原则 2）。
/// </summary>
public enum DownloadStage
{
    /// <summary>失败。</summary>
    Failed,

    /// <summary>只解析出直链，文件下载由上层下载器完成。</summary>
    Resolved,

    /// <summary>音频文件已落地。</summary>
    Downloaded,
}

/// <summary>
/// 结构化下载结果（提案 §3.3）。除了成功/失败，还记录来源、阶段、探测参数与耗时，
/// 使每首歌都能追溯下载源与实际质量（提案 §11 原则 8）。
/// </summary>
public sealed class DownloadResult
{
    /// <summary>是否取得了可用资源（直链或文件）；不代表音质已达标，质量闸门属 Phase 3。</summary>
    public bool Success => Stage != DownloadStage.Failed;

    /// <summary>下载阶段。</summary>
    public DownloadStage Stage { get; init; } = DownloadStage.Failed;

    /// <summary>来源平台标识。</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>平台侧曲目 Id。</summary>
    public string ProviderTrackId { get; init; } = string.Empty;

    /// <summary>已落地的音频文件绝对路径；<see cref="Stage"/> 为 Resolved 时为 null。</summary>
    public string? FilePath { get; init; }

    /// <summary>解析出的直链。</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>声明的容器格式。</summary>
    public AudioFormat Format { get; init; } = AudioFormat.Unknown;

    /// <summary>声明的质量等级。</summary>
    public AudioQualityLevel Quality { get; init; } = AudioQualityLevel.Unknown;

    /// <summary>实际媒体探测结果；Phase 3 接入 ffprobe 后回填。</summary>
    public AudioQuality? Probed { get; init; }

    /// <summary>文件体积（字节）。</summary>
    public long FileSize { get; init; }

    /// <summary>耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>机器可读错误码。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>错误详情。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>是否已有真实音频文件（质量闸门前置条件）。</summary>
    public bool HasAudioFile => !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>构造失败结果。</summary>
    public static DownloadResult Failed(
        string providerName,
        string trackId,
        string errorCode,
        string? message,
        long elapsedMs = 0) =>
        new()
        {
            ProviderName = providerName,
            ProviderTrackId = trackId,
            ErrorCode = errorCode,
            ErrorMessage = message,
            ElapsedMs = elapsedMs,
        };
}

/// <summary>
/// 单次 provider 调用的隔离结果。Orchestrator 保证任何异常/超时都收敛成这个对象，
/// 不会冒泡打断其他 provider（提案 §11 原则 6「故障隔离」）。
/// </summary>
public sealed class ProviderCallResult<T>
{
    /// <summary>平台标识。</summary>
    public required string ProviderName { get; init; }

    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>成功时的返回值。</summary>
    public T? Value { get; init; }

    /// <summary>机器可读错误码（provider_timeout / provider_failed / cancelled / search_budget_exhausted ...）。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>错误详情。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>保留耗时，替换错误码（SearchAsync 把笼统的 cancelled 细化为预算耗尽）。</summary>
    public ProviderCallResult<T> WithError(string errorCode, string? message) =>
        new()
        {
            ProviderName = ProviderName,
            ErrorCode = errorCode,
            ErrorMessage = message,
            ElapsedMs = ElapsedMs,
        };
}

/// <summary>
/// <see cref="ProviderCallResult{T}"/> 的构造入口。
/// 放在非泛型静态类里是刻意的：项目把 CA1000「不要在泛型类型上声明静态成员」提升为编译错误。
/// </summary>
public static class ProviderCallResult
{
    /// <summary>构造成功结果。</summary>
    public static ProviderCallResult<T> Ok<T>(string providerName, T value, long elapsedMs) =>
        new() { ProviderName = providerName, Success = true, Value = value, ElapsedMs = elapsedMs };

    /// <summary>构造失败结果。</summary>
    public static ProviderCallResult<T> Fail<T>(string providerName, string errorCode, string? message, long elapsedMs) =>
        new() { ProviderName = providerName, ErrorCode = errorCode, ErrorMessage = message, ElapsedMs = elapsedMs };
}
