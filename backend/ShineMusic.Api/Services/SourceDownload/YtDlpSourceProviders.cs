// 职责：yt-dlp 生态的两个音源 provider —— YouTube Music（ytmusic）与 SoundCloud（soundcloud）。
// 两者共用 YtDlpBridge（DI 单例）完成搜索/元数据/下载，平台差异仅在于：
//   - 搜索目标：ytmusic 用 music.youtube.com/search URL；soundcloud 用 scsearch 前缀；
//   - 曲目 Id 形态：ytmusic 为 YouTube 视频 Id（可复原 watch URL）；soundcloud 为曲目页 URL（无法从数字 Id 复原）。
// 两个 provider 均实现 ISourceFileDownloader：下载走 yt-dlp 子进程而非 HttpClient 直链。
// 无真无损来源，搜索结果统一标 standard 音质（实际定级由入库流水线 ffprobe 探测）。

using Microsoft.Extensions.Options;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Services.SourceDownload;

/// <summary>YouTube Music 音源提供者（yt-dlp 桥接）。</summary>
public sealed class YouTubeMusicSourceProvider(
    YtDlpBridge bridge,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider, ISourceFileDownloader
{
    /// <summary>命名 HttpClient（封面下载使用，Program.cs 注册统一超时）。</summary>
    public const string HttpClientName = "ytmusic";

    public string Provider => "ytmusic";
    public bool IsEnabled => options.Value.YtDlp.Enabled && bridge.BinaryPresent;
    public string? DisabledReason => YtDlpSourceMapping.DisabledReason(options.Value.YtDlp, bridge);

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var target = $"https://music.youtube.com/search?q={Uri.EscapeDataString(query)}";
        var entries = await bridge.SearchAsync(target, limit, cancellationToken);
        return entries.Select(entry => YtDlpSourceMapping.ToSearchResult(Provider, entry.Id, entry)).OfType<SourceSearchResult>().ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var entry = await bridge.GetTrackMetadataAsync(WatchUrl(providerTrackId), cancellationToken);
        return entry is null ? null : YtDlpSourceMapping.ToSearchResult(Provider, providerTrackId, entry);
    }

    /// <inheritdoc />
    public Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken) =>
        // 自下载 provider 不走直链解析，队列经 ISourceFileDownloader 分支处理。
        throw new SourceUnavailableException("This source downloads through the embedded yt-dlp service.");

    /// <inheritdoc />
    public async Task<SourceFileDownload> DownloadToFileAsync(
        string providerTrackId, string outputDirectory, string fileBaseName, CancellationToken cancellationToken)
    {
        var filePath = await bridge.DownloadAudioAsync(WatchUrl(providerTrackId), outputDirectory, fileBaseName, cancellationToken);
        return new SourceFileDownload(filePath, Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(), SourceQualities.Standard);
    }

    /// <summary>视频 Id → YouTube Music watch 页 URL。</summary>
    private static string WatchUrl(string videoId) => $"https://music.youtube.com/watch?v={Uri.EscapeDataString(videoId)}";
}

/// <summary>SoundCloud 音源提供者（yt-dlp 桥接）。</summary>
public sealed class SoundCloudSourceProvider(
    YtDlpBridge bridge,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider, ISourceFileDownloader
{
    /// <summary>命名 HttpClient（封面下载使用，Program.cs 注册统一超时）。</summary>
    public const string HttpClientName = "soundcloud";

    public string Provider => "soundcloud";
    public bool IsEnabled => options.Value.YtDlp.Enabled && bridge.BinaryPresent;
    public string? DisabledReason => YtDlpSourceMapping.DisabledReason(options.Value.YtDlp, bridge);

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        // scsearch 每条约 7s（实测 5 条 44s），上限压到 10 条以留在搜索超时窗口内；
        // N 在桥内由 --playlist-end 控制，前缀仍带 N 做双保险。
        var cappedLimit = Math.Clamp(limit, 1, 10);
        var entries = await bridge.SearchAsync($"scsearch{cappedLimit}:{query}", cappedLimit, cancellationToken);
        return entries.Select(entry => YtDlpSourceMapping.ToSearchResult(Provider, TrackIdOf(entry), entry)).OfType<SourceSearchResult>().ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var entry = await bridge.GetTrackMetadataAsync(providerTrackId, cancellationToken);
        return entry is null ? null : YtDlpSourceMapping.ToSearchResult(Provider, providerTrackId, entry);
    }

    /// <inheritdoc />
    public Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken) =>
        throw new SourceUnavailableException("This source downloads through the embedded yt-dlp service.");

    /// <inheritdoc />
    public async Task<SourceFileDownload> DownloadToFileAsync(
        string providerTrackId, string outputDirectory, string fileBaseName, CancellationToken cancellationToken)
    {
        var filePath = await bridge.DownloadAudioAsync(providerTrackId, outputDirectory, fileBaseName, cancellationToken);
        return new SourceFileDownload(filePath, Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(), SourceQualities.Standard);
    }

    /// <summary>
    /// SoundCloud 的曲目标识使用页面 URL（数字 Id 无法复原页面地址，yt-dlp 下载需要完整 URL）。
    /// 实体列上限 128 字符，超限的极端长 URL 条目在搜索结果中直接丢弃。
    /// </summary>
    private static string TrackIdOf(YtDlpEntry entry)
        => entry.WebpageUrl is { Length: <= 128 } url ? url : entry.Id;
}

/// <summary>yt-dlp 系 provider 的共享映射与降级原因。</summary>
internal static class YtDlpSourceMapping
{
    /// <summary>统一降级原因：配置关闭 &gt; 二进制缺失 &gt; 自检失败。</summary>
    public static string? DisabledReason(YtDlpSourceOptions options, YtDlpBridge bridge)
    {
        if (!options.Enabled)
            return "yt-dlp sources are disabled by configuration.";
        if (!bridge.BinaryPresent)
            return $"yt-dlp executable was not found at {bridge.ResolvedExecutablePath}.";
        return bridge.UnavailableReason;
    }

    /// <summary>yt-dlp 条目 → 统一搜索模型。艺人优先完整元数据的 artist 字段，回退 uploader/channel。</summary>
    public static SourceSearchResult? ToSearchResult(string provider, string providerTrackId, YtDlpEntry entry)
    {
        if (string.IsNullOrWhiteSpace(providerTrackId) || providerTrackId.Length > 128)
            return null;
        var artists = new List<string>();
        var artist = !string.IsNullOrWhiteSpace(entry.Artist) ? entry.Artist : entry.Uploader;
        if (!string.IsNullOrWhiteSpace(artist))
            artists.Add(artist.Trim());
        return new SourceSearchResult(
            provider,
            providerTrackId,
            entry.Title ?? providerTrackId,
            artists,
            entry.Album,
            entry.DurationMs,
            Genres: [],
            entry.ThumbnailUrl,
            entry.WebpageUrl,
            License: null,
            // yt-dlp 系平台无真无损来源；实际定级由入库流水线 ffprobe 探测（wav 提取保留原始码率）。
            SourceQualities.Standard,
            IsLosslessAvailable: false);
    }
}
