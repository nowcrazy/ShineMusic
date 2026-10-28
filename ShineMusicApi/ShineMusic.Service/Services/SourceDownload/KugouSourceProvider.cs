// 职责：酷狗音乐音源 provider（HTTP 直链型）。
// 端点链（2026-10-28 实测零 Cookie/零签名）：
//   1. 搜索：songsearch.kugou.com/song_search_v2 —— JSON，条目含 FileHash/歌名/艺人/专辑/时长(秒)/各档 hash；
//   2. 详情与下载地址：m.kugou.com/app/i/getSongInfo.php?cmd=playInfo&hash={FileHash} ——
//      返回 url（sharefs.kugou.com 直链，支持 Range）、bitRate、timeLength、fileName。
// 局限：免登录仅 128k mp3；HQFileHash/SQFileHash 经 getSongInfo 仍回 128k 免费档，
//   老 tracker 签名接口 trackercdn.kugou.com/i/v2 已失效（Bad key），wwwapi getdata 需动态签名（30020）。
//   320k/无损签名接入列为后续增强。
// 注：plist 系接口要求移动端 UA（命名 HttpClient "kugou" 已配置），搜索/下载直链无特殊要求。

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>酷狗音乐音源提供者。</summary>
public sealed partial class KugouSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "kugou";

    private const string SearchBase = "https://songsearch.kugou.com/song_search_v2";
    private const string PlayInfoBase = "https://m.kugou.com/app/i/getSongInfo.php";

    /// <summary>getSongInfo.php 的进程内滑动缓存（Singleton 实例跨任务共享），窗口 30s：避免 ResolveDownloadAsync + GetTrackAsync 同任务重复打 2 次端点，
    ///   同时在 RabbitMQ 4 并发下载场景下把连续调用打散，降低触发 errcode:1002 频控的概率。</summary>
    private readonly ConcurrentDictionary<string, (string Json, DateTime ExpiresUtc)> _playInfoCache = new();
    private const int PlayInfoCacheSeconds = 30;

    /// <inheritdoc />
    public string Provider => "kugou";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Kugou.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "酷狗音源未启用（SourceDownload:Kugou:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{SearchBase}?keyword={Uri.EscapeDataString(query)}&page=1&pagesize={Math.Clamp(limit, 1, 50)}";
        var raw = await client.GetStringAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("lists", out var list)
            || list.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SourceSearchResult>();
        foreach (var entry in list.EnumerateArray())
        {
            var fileHash = GetString(entry, "FileHash");
            var title = StripHighlight(GetString(entry, "SongName"));
            if (string.IsNullOrWhiteSpace(fileHash) || string.IsNullOrWhiteSpace(title))
                continue;
            var artists = SplitArtists(StripHighlight(GetString(entry, "SingerName")));
            var album = StripHighlight(GetString(entry, "AlbumName"));
            long? durationMs = GetLong(entry, "Duration") is { } seconds && seconds > 0
                ? seconds * 1000
                : null;
            var cover = GetString(entry, "Image");
            results.Add(new SourceSearchResult(
                Provider,
                fileHash,
                title,
                artists,
                string.IsNullOrWhiteSpace(album) ? null : album,
                durationMs,
                [],
                string.IsNullOrWhiteSpace(cover) ? null : cover,
                SourcePageUrl: $"https://www.kugou.com/song/#hash={fileHash}",
                License: null,
                SourceQualities.Standard,
                IsLosslessAvailable: false));
        }
        return results.ToArray();
    }

    /// <inheritdoc />
    /// <remarks>详情时长（timeLength，整数秒）是下载校验的权威基准；解析失败返回 null 由调用方降级。</remarks>
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await PlayInfoJsonAsync(providerTrackId, cancellationToken);
        }
        catch (SourceUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var (artist, title) = SplitFileName(GetString(root, "fileName"));
            if (string.IsNullOrWhiteSpace(title))
                return null;
            long? durationMs = root.TryGetProperty("timeLength", out var lengthElement)
                && lengthElement.ValueKind == JsonValueKind.Number
                && lengthElement.GetInt64() > 0
                    ? lengthElement.GetInt64() * 1000
                    : null;
            return new SourceSearchResult(
                Provider,
                providerTrackId,
                title,
                string.IsNullOrWhiteSpace(artist) ? [] : [artist],
                AlbumTitle: null,
                durationMs,
                [],
                CoverUrl: null,
                $"https://www.kugou.com/song/#hash={providerTrackId}",
                null,
                SourceQualities.Standard,
                IsLosslessAvailable: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 免登录仅 128k 一档：status==1 且 url 非空才可下载，否则判定 VIP/无版权/频控。
    /// 频控（errcode:1002 / "您操作太频繁了"）单独识别并上抛，让上层任务调度做更短间隔的快速重试。
    /// </remarks>
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var json = await PlayInfoJsonAsync(providerTrackId, cancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            && statusElement.ValueKind == JsonValueKind.Number
                ? statusElement.GetInt32()
                : 0;
        var url = GetString(root, "url")?.Trim();
        if (status != 1 || string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new SourceUnavailableException("酷狗未返回可下载资源（该曲目可能需要 VIP、无版权或已下架）。");

        return new SourceDownloadInfo(url, "mp3", IsLossless: false, BitrateKbps: 128, SourceQualities.Standard);
    }

    /// <summary>
    /// 拉取并缓存 getSongInfo.php 响应；统一识别 errcode:1002（您操作太频繁）并上抛频控异常。
    /// 缓存窗口 30s：Singleton provider 实例跨任务共享，同一 hash 的 ResolveDownloadAsync + GetTrackAsync
    /// 复用一次调用，同时在 RabbitMQ 4 并发下载场景下打散密集请求、降低触发频控的概率。
    /// </summary>
    private async Task<string> PlayInfoJsonAsync(string hash, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (_playInfoCache.TryGetValue(hash, out var cached) && cached.ExpiresUtc > now)
            return cached.Json;

        var client = httpClientFactory.CreateClient(HttpClientName);
        var json = await client.GetStringAsync($"{PlayInfoBase}?cmd=playInfo&hash={Uri.EscapeDataString(hash)}", cancellationToken);

        // 频控信号识别：errcode:1002 或 error 包含「您操作太频繁了」
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var errcode = root.TryGetProperty("errcode", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : 0;
            var errorMsg = GetString(root, "error");
            if (errcode == 1002
                || (!string.IsNullOrWhiteSpace(errorMsg) && errorMsg.Contains("频繁", StringComparison.Ordinal)))
            {
                throw new SourceUnavailableException("酷狗触发频控（errcode 1002 / 操作太频繁），请稍后再试。");
            }
        }
        catch (SourceUnavailableException)
        {
            throw;
        }
        catch (JsonException)
        {
            // 响应非法 JSON（网络层异常包装等）——不缓存、直接回传原始 json 让上层按 status==0 处理
        }

        _playInfoCache.TryAdd(hash, (json, now.AddSeconds(PlayInfoCacheSeconds)));
        return json;
    }

    /// <summary>艺人字段按 、/，/, 拆分为列表。</summary>
    private static IReadOnlyList<string> SplitArtists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return ArtistSeparatorRegex().Split(value)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .ToArray();
    }

    /// <summary>fileName 形态为「艺人 - 歌名」，按首个分隔符拆分；无分隔符时整体作歌名。</summary>
    private static (string? Artist, string? Title) SplitFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return (null, null);
        var index = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (index <= 0)
            return (null, fileName.Trim());
        return (fileName[..index].Trim(), fileName[(index + 3)..].Trim());
    }

    /// <summary>剥离搜索字段中的 &lt;em&gt; 高亮标签，并解码 HTML 实体。</summary>
    private static string? StripHighlight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        var stripped = value.Replace("<em>", "", StringComparison.Ordinal).Replace("</em>", "", StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }

    /// <summary>从 JsonElement 上安全取字符串字段。</summary>
    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>从 JsonElement 上安全取整数字段（兼容数字与字符串两种形态）。</summary>
    private static long? GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    [GeneratedRegex(@"[、，,]")]
    private static partial Regex ArtistSeparatorRegex();
}
