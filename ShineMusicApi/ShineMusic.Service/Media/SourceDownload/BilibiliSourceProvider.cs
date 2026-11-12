// 职责：Bilibili（B站音频）音源 provider（搜索/详情走公开 API，下载走 yt-dlp 子进程自下载）。
// 端点链（公开接口免登录，2026-11 实测需浏览器 UA + Referer + buvid3/buvid4 指纹 Cookie，否则 HTTP 412 风控）：
//   0. 指纹：api.bilibili.com/x/frontend/finger/spi —— 返回 data.b_3/b_4，作为 buvid3/buvid4 Cookie 缓存携带，
//      412 时刷新一次重试；
//   1. 搜索：api.bilibili.com/x/web-interface/search/type?search_type=video —— 返回 data.result[]
//      （bvid/title 含 <em> 高亮标签需剥离/author/duration "mm:ss"/pic/pubdate）；
//   2. 详情：api.bilibili.com/x/web-interface/view?bvid= —— 单曲元数据；
//   3. 下载：不提供直链解析（ResolveDownloadAsync 直接抛出），由 ISourceFileDownloader 走 YtDlpBridge
//      下载 https://www.bilibili.com/video/{bvid}（yt-dlp 原生支持 B 站，-x 提取音频），复用现有代理/cookies/超时配置。
// 覆盖：live 版/综艺现场/翻唱（补主流平台缺失的现场音源）；搜索过滤 <60s 结果（短视频切片非完整曲目）。

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>Bilibili（B站音频）音源提供者（yt-dlp 桥接自下载）。</summary>
public sealed partial class BilibiliSourceProvider(
    IHttpClientFactory httpClientFactory,
    YtDlpBridge bridge,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider, ISourceFileDownloader, IDisposable
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称（仅搜索/详情 API 用）。</summary>
    public const string HttpClientName = "bilibili";

    private const string SearchBase = "https://api.bilibili.com/x/web-interface/search/type";
    private const string ViewBase = "https://api.bilibili.com/x/web-interface/view";
    private const string FingerBase = "https://api.bilibili.com/x/frontend/finger/spi";

    /// <summary>最短收录时长（秒）：过滤短视频切片，只保留完整曲目/现场。</summary>
    private const int MinimumDurationSeconds = 60;

    /// <summary>spi 指纹缓存与并发加载锁（provider 单例，多搜索并发时只取一次指纹）。</summary>
    private readonly SemaphoreSlim _fingerLock = new(1, 1);
    private (string Buvid3, string Buvid4)? _finger;

    /// <inheritdoc />
    public void Dispose() => _fingerLock.Dispose();

    /// <inheritdoc />
    public string Provider => "bilibili";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Bilibili.Enabled && bridge.BinaryPresent;

    /// <inheritdoc />
    public string? DisabledReason
    {
        get
        {
            if (!options.Value.Bilibili.Enabled)
                return "Bilibili 音源未启用（SourceDownload:Bilibili:Enabled=false）。";
            return bridge.BinaryPresent ? null : $"yt-dlp executable was not found at {bridge.ResolvedExecutablePath}.";
        }
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{SearchBase}?search_type=video&keyword={Uri.EscapeDataString(query)}&page=1";
        var raw = await GetStringWithFingerAsync(client, url, cancellationToken);

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("result", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SourceSearchResult>();
        foreach (var entry in list.EnumerateArray())
        {
            var bvid = GetString(entry, "bvid");
            var title = StripHighlight(GetString(entry, "title"));
            if (string.IsNullOrWhiteSpace(bvid) || string.IsNullOrWhiteSpace(title))
                continue;
            // 过滤 <60s 的短视频切片（非完整曲目）。
            var durationSeconds = ParseDurationSeconds(GetString(entry, "duration"));
            if (durationSeconds is null or < MinimumDurationSeconds)
                continue;
            var author = GetString(entry, "author");
            var pic = GetString(entry, "pic");
            results.Add(new SourceSearchResult(
                Provider,
                bvid,
                title,
                string.IsNullOrWhiteSpace(author) ? [] : [author],
                AlbumTitle: null,
                durationSeconds * 1000,
                [],
                NormalizeCoverUrl(pic),
                VideoPageUrl(bvid),
                License: null,
                SourceQualities.Standard,
                IsLosslessAvailable: false,
                ReleaseYear: SourceReleaseYearParser.FromJson(entry, "pubdate")));
            if (results.Count >= limit)
                break;
        }
        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        string json;
        try
        {
            json = await GetStringWithFingerAsync(client,
                $"{ViewBase}?bvid={Uri.EscapeDataString(providerTrackId)}", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
                return null;
            var title = GetString(data, "title");
            if (string.IsNullOrWhiteSpace(title))
                return null;
            string? author = null;
            if (data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                author = GetString(owner, "name");
            long? durationMs = null;
            if (data.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                && duration.TryGetInt64(out var seconds) && seconds > 0)
                durationMs = seconds * 1000;
            return new SourceSearchResult(
                Provider,
                providerTrackId,
                title,
                string.IsNullOrWhiteSpace(author) ? [] : [author],
                AlbumTitle: null,
                durationMs,
                [],
                NormalizeCoverUrl(GetString(data, "pic")),
                VideoPageUrl(providerTrackId),
                null,
                SourceQualities.Standard,
                IsLosslessAvailable: false,
                ReleaseYear: SourceReleaseYearParser.FromJson(data, "pubdate"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken) =>
        // 自下载 provider 不走直链解析，队列经 ISourceFileDownloader 分支处理。
        throw new SourceUnavailableException("This source downloads through the embedded yt-dlp service.");

    /// <inheritdoc />
    public async Task<SourceFileDownload> DownloadToFileAsync(
        string providerTrackId, string outputDirectory, string fileBaseName, CancellationToken cancellationToken)
    {
        var filePath = await bridge.DownloadAudioAsync(VideoPageUrl(providerTrackId), outputDirectory, fileBaseName, cancellationToken);
        return new SourceFileDownload(filePath, Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(), SourceQualities.Standard);
    }

    /// <inheritdoc />
    /// <remarks>B 站公开视频音频流匿名可取：view 接口能取到详情即视为可下载。超时/异常时保守放行。</remarks>
    public async Task<bool> CanDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var document = JsonDocument.Parse(await GetStringWithFingerAsync(client,
                $"{ViewBase}?bvid={Uri.EscapeDataString(providerTrackId)}", cancellationToken));
            // code==0 表示视频存在且公开；非 0（如 -404 已删除）判定不可下载。
            return document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 0;
        }
        catch
        {
            return true; // 超时/异常时保守放行
        }
    }

    /// <summary>bvid → B 站视频页 URL。</summary>
    private static string VideoPageUrl(string bvid) => $"https://www.bilibili.com/video/{Uri.EscapeDataString(bvid)}";

    /// <summary>
    /// 带 buvid3/buvid4 指纹 Cookie 的 GET；命中 HTTP 412 风控时刷新指纹静默重试一次。
    /// 指纹获取失败时不带 Cookie 直接请求（交由上层错误路径处理）。
    /// </summary>
    private async Task<string> GetStringWithFingerAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var finger = await EnsureFingerAsync(client, refresh: false, cancellationToken);
        using var response = await SendWithFingerAsync(client, url, finger, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PreconditionFailed)
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        // 412 风控：指纹失效，强制刷新后重试一次。
        finger = await EnsureFingerAsync(client, refresh: true, cancellationToken);
        using var retry = await SendWithFingerAsync(client, url, finger, cancellationToken);
        retry.EnsureSuccessStatusCode();
        return await retry.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>发送一次带指纹 Cookie 的 GET（无指纹时不带 Cookie）。</summary>
    private static async Task<HttpResponseMessage> SendWithFingerAsync(
        HttpClient client, string url, (string Buvid3, string Buvid4)? finger, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (finger is { } value)
            request.Headers.TryAddWithoutValidation("Cookie", $"buvid3={value.Buvid3}; buvid4={value.Buvid4}");
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    /// <summary>取 spi 指纹（buvid3/buvid4）：有缓存直接用；refresh=true 时强制重取。spi 失败返回 null（静默降级为无 Cookie）。</summary>
    private async Task<(string Buvid3, string Buvid4)?> EnsureFingerAsync(
        HttpClient client, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _finger is not null)
            return _finger;
        await _fingerLock.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _finger is not null)
                return _finger;
            _finger = await FetchFingerAsync(client, cancellationToken);
            return _finger;
        }
        finally
        {
            _fingerLock.Release();
        }
    }

    /// <summary>调 x/frontend/finger/spi 取 buvid3/buvid4；失败返回 null。</summary>
    private static async Task<(string Buvid3, string Buvid4)?> FetchFingerAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(FingerBase, cancellationToken));
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            var buvid3 = GetString(data, "b_3");
            var buvid4 = GetString(data, "b_4");
            return string.IsNullOrWhiteSpace(buvid3) || string.IsNullOrWhiteSpace(buvid4) ? null : (buvid3, buvid4);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>B 站封面 URL 常以 // 开头（协议相对），补全 https: 前缀。</summary>
    private static string? NormalizeCoverUrl(string? pic)
    {
        if (string.IsNullOrWhiteSpace(pic))
            return null;
        return pic.StartsWith("//", StringComparison.Ordinal) ? $"https:{pic}" : pic;
    }

    /// <summary>解析 "mm:ss"（或 "hh:mm:ss"）时长字符串为秒；格式异常返回 null。</summary>
    private static long? ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return null;
        var parts = duration.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long total = 0;
        foreach (var part in parts)
        {
            if (!long.TryParse(part, out var value))
                return null;
            total = total * 60 + value;
        }
        return total > 0 ? total : null;
    }

    /// <summary>剥离搜索标题中的 &lt;em&gt; 高亮标签（B 站搜索返回 HTML 片段），并解码 HTML 实体。</summary>
    private static string? StripHighlight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        var stripped = HtmlTagRegex().Replace(value, "");
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }

    /// <summary>从 JsonElement 上安全取字符串字段。</summary>
    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
