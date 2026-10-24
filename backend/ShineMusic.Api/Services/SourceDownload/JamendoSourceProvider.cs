// 职责：Jamendo 音源平台实现。
// Jamendo 是开放版权（Creative Commons）音乐平台，公共 API 需免费注册 client_id：
// - 搜索/详情：GET /v3.0/tracks/?client_id=&search=|id=&audioformat=flac&imagesize=600&include=licenses
// - 下载：曲目 JSON 的 audiodownload 字段（请求 audioformat=flac 时指向无损 FLAC，平台无 FLAC 时回退 320k MP3）
// - 仅 audiodownload_allowed=true 的曲目允许下载，其余条目在搜索结果中直接过滤。

using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Services.SourceDownload;

/// <summary>Jamendo 音源提供者。</summary>
public sealed class JamendoSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options,
    ILogger<JamendoSourceProvider> logger) : IMusicSourceProvider
{
    /// <summary>命名 HttpClient（Program.cs 注册，统一超时控制）。</summary>
    public const string HttpClientName = "jamendo";

    public string Provider => "jamendo";
    public bool IsEnabled => options.Value.Jamendo.Enabled && !string.IsNullOrWhiteSpace(options.Value.Jamendo.ClientId);
    public string? DisabledReason => options.Value.Jamendo.Enabled
        ? (string.IsNullOrWhiteSpace(options.Value.Jamendo.ClientId) ? "Jamendo client_id is not configured." : null)
        : "Jamendo source is disabled by configuration.";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{Base()}/tracks/?client_id={ClientId()}&format=json&limit={limit}"
            + $"&search={Uri.EscapeDataString(query)}&audioformat=flac&imagesize=600&include=licenses";
        return await GetResultsWithRetryAsync(url, limit, $"search '{query}'", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var url = $"{Base()}/tracks/?client_id={ClientId()}&format=json&id={Uri.EscapeDataString(providerTrackId)}"
            + "&audioformat=flac&imagesize=600&include=licenses";
        var results = await GetResultsWithRetryAsync(url, 1, $"track {providerTrackId}", cancellationToken);
        return results.FirstOrDefault();
    }

    /// <summary>
    /// GET 并解析结果数组，空结果时有限重试：Jamendo 接口偶发返回 results_count=0 的空成功响应
    /// （平台侧多节点缓存不一致，实测同一 URL 重试即恢复），搜索与详情共用此保护。
    /// </summary>
    private async Task<SourceSearchResult[]> GetResultsWithRetryAsync(string url, int limit, string context, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var doc = await GetJsonAsync(url, cancellationToken);
            var results = ParseResults(doc, limit);
            if (results.Length > 0 || attempt >= 3)
                return results;
            logger.LogInformation("Jamendo {Context} returned empty results (attempt {Attempt}), retrying.", context, attempt);
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        // 详情请求已带 audioformat=flac：audiodownload 指向 FLAC（平台侧有该曲目 FLAC 时），否则回退 320k MP3。
        // 为判定实际音质等级，需要区分两者：Jamendo 不直接暴露实际格式字段，
        // 故无损判定策略为「请求时声明 flac + audiodownload 地址存在」即按无损候选处理，
        // 真实容器格式由下载层按响应头/魔数探测，探测结果回填 Quality。
        var track = await GetTrackAsync(providerTrackId, cancellationToken)
            ?? throw new SourceUnavailableException("The track was not found on Jamendo.");
        var url = $"{Base()}/tracks/?client_id={ClientId()}&format=json&id={Uri.EscapeDataString(providerTrackId)}"
            + "&audioformat=flac&imagesize=600&include=licenses";
        // 下载地址提取同样可能命中平台偶发空响应，复用重试节奏避免误判「不可下载」。
        (string Url, bool IsFlac)? download = null;
        for (var attempt = 1; attempt <= 3 && download is null; attempt++)
        {
            using var doc = await GetJsonAsync(url, cancellationToken);
            download = ExtractDownload(doc);
            if (download is null && attempt < 3)
            {
                logger.LogInformation("Jamendo track {TrackId} download lookup returned empty (attempt {Attempt}), retrying.", providerTrackId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
            }
        }
        if (download is null)
            throw new SourceUnavailableException("The track is not downloadable on Jamendo.");

        return new SourceDownloadInfo(
            download.Value.Url,
            download.Value.IsFlac ? "flac" : "mp3",
            IsLossless: download.Value.IsFlac,
            BitrateKbps: download.Value.IsFlac ? null : 320,
            Quality: download.Value.IsFlac ? SourceQualities.Lossless : SourceQualities.High);
    }

    /// <summary>解析搜索结果数组，过滤禁止下载的条目；关键字段缺失的条目跳过。</summary>
    private SourceSearchResult[] ParseResults(JsonDocument? doc, int limit)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<SourceSearchResult>();
        foreach (var item in results.EnumerateArray())
        {
            var parsed = ParseTrack(item);
            if (parsed is not null) list.Add(parsed);
            if (list.Count >= limit) break;
        }
        return list.ToArray();
    }

    /// <summary>解析 Jamendo 曲目 JSON 为统一模型；禁止下载或关键字段缺失时返回 null。</summary>
    private SourceSearchResult? ParseTrack(JsonElement item)
    {
        // 平台明确禁止下载的曲目直接排除（不允许进候选列表，避免误导管理员）。
        if (!GetBool(item, "audiodownload_allowed"))
            return null;

        var id = GetString(item, "id");
        var title = GetString(item, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var artists = new List<string>();
        var artistName = GetString(item, "artist_name");
        if (!string.IsNullOrWhiteSpace(artistName)) artists.Add(artistName.Trim());

        // 流派：musicinfo.tags.genres 数组（英文标签，入库时经 GenreMoodMapper 归一化）。
        var genres = new List<string>();
        if (item.TryGetProperty("musicinfo", out var musicInfo) && musicInfo.ValueKind == JsonValueKind.Object
            && musicInfo.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            && tags.TryGetProperty("genres", out var genreArray) && genreArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var genre in genreArray.EnumerateArray())
            {
                if (genre.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(genre.GetString()))
                    genres.Add(genre.GetString()!.Trim());
            }
        }

        var durationSeconds = GetLong(item, "duration");
        var isFlac = IsFlacDownload(item);
        return new SourceSearchResult(
            Provider,
            id,
            title.Trim(),
            artists,
            AlbumTitle: GetString(item, "album_name"),
            DurationMs: durationSeconds * 1000,
            Genres: genres,
            CoverUrl: GetString(item, "image"),
            SourcePageUrl: GetString(item, "shareurl") ?? GetString(item, "shorturl"),
            License: GetString(item, "license_ccurl"),
            Quality: isFlac ? SourceQualities.Lossless : SourceQualities.High,
            IsLosslessAvailable: isFlac);
    }

    /// <summary>从详情响应中提取下载地址与实际格式（audioformat=flac 请求下，无 FLAC 时平台回退 mp32）。</summary>
    private static (string Url, bool IsFlac)? ExtractDownload(JsonDocument? doc)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in results.EnumerateArray())
        {
            var url = GetString(item, "audiodownload");
            if (string.IsNullOrWhiteSpace(url)) continue;
            return (url, IsFlacDownload(item));
        }
        return null;
    }

    /// <summary>
    /// 判定该曲目 audiodownload 是否指向 FLAC：Jamendo 在 audioformat=flac 请求下，
    /// 有 FLAC 的曲目 audiodownload 路径含 format=flac（或 .flac 后缀），无则回退 mp32。
    /// </summary>
    private static bool IsFlacDownload(JsonElement item)
    {
        var url = GetString(item, "audiodownload");
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("format=flac", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".flac", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GET JSON 并解析；网络/格式异常返回 null（搜索场景容忍失败，由编排层聚合多源结果）。</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Jamendo request failed: {StatusCode} {Url}", (int)response.StatusCode, url);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);
            // Jamendo 业务错误经 headers.status/code 表达（HTTP 仍 200）。
            if (doc.RootElement.TryGetProperty("headers", out var headers)
                && headers.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                var code = headers.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "?";
                logger.LogWarning("Jamendo API returned status {Status} code {Code}: {Url}", status.GetString(), code, url);
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Jamendo request error: {Url}", url);
            return null;
        }
    }

    private string Base() => options.Value.Jamendo.BaseUrl.TrimEnd('/');
    private string ClientId() => Uri.EscapeDataString(options.Value.Jamendo.ClientId);

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
