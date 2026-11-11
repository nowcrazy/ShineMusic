// 职责：网易云音乐音源 provider（HTTP 直链型）。
// 端点链（公开接口免登录，2026-10 实测）：
//   1. 搜索：music.163.com/api/search/get?s=&type=1&limit= —— 返回 result.songs[]（id/name/artists/album/duration/fee）；
//   2. 详情：music.163.com/api/v3/song/detail?c=[{"id":...}] —— 返回 songs[]（ar/al.name/al.picUrl/dt/fee）；
//   3. 下载地址：music.163.com/api/song/enhance/player/url?ids=[...]&br=320000 —— 返回 data[].url 直链（mp3，320k）。
// 覆盖：主流中文曲库；VIP 曲（fee≠0）搜索可见但下载可能返回空 url → 判定不可下载。
// 注：网易云部分曲目因版权转移（如周杰伦原曲）可能只返回翻唱版本，属平台版权限制非代码缺陷。

using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>网易云音乐音源提供者。</summary>
public sealed class NeteaseSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options,
    ILogger<NeteaseSourceProvider> logger) : IMusicSourceProvider, IQualityLadderProvider
{
    /// <summary>命名 HttpClient（Program.cs 注册，统一超时与 Referer/UA 头）。</summary>
    public const string HttpClientName = "netease";

    /// <inheritdoc />
    public string Provider => "netease";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Netease.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "网易云音源未启用（SourceDownload:Netease:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{SearchBase()}?s={Uri.EscapeDataString(query)}&type=1&limit={Math.Clamp(limit, 1, 50)}&offset=0";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null || !doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<SourceSearchResult>();
        foreach (var song in songs.EnumerateArray())
        {
            var parsed = ParseTrackFromSearch(song);
            if (parsed is not null) list.Add(parsed);
            if (list.Count >= limit) break;
        }
        return list.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var c = JsonSerializer.Serialize(new[] { new { id = long.Parse(providerTrackId) } });
        var url = $"{DetailBase()}?c={Uri.EscapeDataString(c)}";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null || !doc.RootElement.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0)
            return null;
        return ParseTrackFromDetail(songs[0]);
    }

    /// <inheritdoc />
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var candidates = await ResolveDownloadCandidatesAsync(providerTrackId, cancellationToken);
        return candidates[0];
    }

    /// <summary>
    /// 按「官方直链 → 各 VIP 回退端点 × 各音质档」顺序返回全部可用下载地址。
    /// 官方接口无直链时收集回退链里的**所有**成功结果（不再首个即返回）：
    /// haitangw 等端点可能返回 60s 试听，下载阶段时长校验会判无效并自动落到下一档/下一端点，
    /// 而不是把这唯一候选直接判死。
    /// </summary>
    public async Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesAsync(
        string providerTrackId, CancellationToken cancellationToken)
    {
        var candidates = new List<SourceDownloadInfo>();
        var ids = JsonSerializer.Serialize(new[] { long.Parse(providerTrackId) });
        var url = $"{PlayerUrlBase()}?ids={Uri.EscapeDataString(ids)}&br={options.Value.Netease.Bitrate}";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new SourceUnavailableException("网易云未返回播放地址。");

        var item = data[0];
        var downloadUrl = GetString(item, "url");
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            var br = GetInt(item, "br") ?? options.Value.Netease.Bitrate;
            var type = GetString(item, "type") ?? "mp3";
            var isLossless = br >= 800000;
            candidates.Add(new SourceDownloadInfo(
                downloadUrl,
                type,
                IsLossless: isLossless,
                BitrateKbps: br / 1000,
                Quality: isLossless ? SourceQualities.Lossless : SourceQualities.High));
        }
        else
        {
            // VIP 曲或无版权：官方 url 为空 → 收集第三方回退链的全部成功直链。
            candidates.AddRange(await CollectVipFallbackCandidatesAsync(providerTrackId, cancellationToken));
        }

        if (candidates.Count == 0)
            throw new SourceUnavailableException("该曲目为网易云 VIP 专属或无版权，无法下载。");
        return candidates;
    }

    /// <summary>
    /// 轻量预判：调 enhance/player/url 检查官方 API 是否返回有效下载直链。
    /// 不做 VIP 回退探测（第三方端点开销大），仅检查官方接口。
    /// fee!=0 的 VIP 曲目官方 API 通常返回空 url → 判定不可下载（除非 VIP 回退开启）。
    /// 超时/异常时返回 true（保守放行）。
    /// </summary>
    public async Task<bool> CanDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        try
        {
            var ids = JsonSerializer.Serialize(new[] { long.Parse(providerTrackId) });
            var url = $"{PlayerUrlBase()}?ids={Uri.EscapeDataString(ids)}&br={options.Value.Netease.Bitrate}";
            using var doc = await GetJsonAsync(url, cancellationToken);
            if (doc is null || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return true; // 响应异常，保守放行
            var downloadUrl = GetString(data[0], "url");
            // VIP 回退开启时，官方返回空 url 不代表真的不可下载——第三方端点可能解锁，保守放行；
            // 回退关闭时官方空 url 即判定不可下载。
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return options.Value.Netease.VipFallback.Enabled;
            return true;
        }
        catch
        {
            return true; // 超时/异常时保守放行
        }
    }

    /// <summary>
    /// VIP 曲目回退解析：按配置的端点链依次尝试第三方「解锁」服务获取下载直链。
    /// 端点类型：
    ///   - gdstudio：GET 单步，根级 url（2026-11 实测对 VIP 曲返回完整 320k/无损 flac，当前主力）。
    ///   - nextmusic：共享 VIP 账号，POST 两步（/api/ip 取 IP → /api/getSongUrl 取 url），返回完整歌曲（320k/无损）。
    ///   - haitangw：GET 单步，返回 data.url（实测仅 60s 预览，作兜底）。
    ///   - zmusic：兼容官方格式 GET，返回 data[0].url。
    /// 收集所有端点 × 档位的成功直链（按配置顺序），交由下载阶段的时长校验逐档淘汰，
    /// 避免「首个返回的 60s 预览被直接采纳」导致整源被判死。
    /// </summary>
    private async Task<List<SourceDownloadInfo>> CollectVipFallbackCandidatesAsync(
        string providerTrackId, CancellationToken cancellationToken)
    {
        var fb = options.Value.Netease.VipFallback;
        var candidates = new List<SourceDownloadInfo>();
        if (!fb.Enabled || fb.Endpoints is null || fb.Endpoints.Count == 0) return candidates;

        var client = httpClientFactory.CreateClient(HttpClientName);
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in fb.Endpoints)
        {
            foreach (var level in endpoint.Levels ?? [])
            {
                var url = await ResolveViaEndpointAsync(client, endpoint, providerTrackId, level, fb.TimeoutSeconds, cancellationToken);
                if (url is null || !seenUrls.Add(url)) continue;
                var (bitrate, isLossless, container) = InferQuality(level, endpoint.Type);
                logger.LogInformation(
                    "Netease VIP fallback candidate for track {Id} via {Type} (level={Level}, {Bitrate}kbps)",
                    providerTrackId, endpoint.Type, level, bitrate / 1000);
                candidates.Add(new SourceDownloadInfo(
                    url,
                    container,
                    IsLossless: isLossless,
                    BitrateKbps: bitrate / 1000,
                    Quality: isLossless ? SourceQualities.Lossless : SourceQualities.High));
            }
        }
        return candidates;
    }

    /// <summary>按端点类型分发请求，返回下载直链或 null。</summary>
    private async Task<string?> ResolveViaEndpointAsync(
        HttpClient client, NeteaseVipFallbackEndpoint endpoint, string trackId, string level,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            return endpoint.Type switch
            {
                VipFallbackEndpointTypes.NextMusic => await ResolveViaNextMusicAsync(client, endpoint.BaseUrl!, trackId, level, timeoutSeconds, cancellationToken),
                VipFallbackEndpointTypes.HaiTangW => await ResolveViaGetDataUrlAsync(client, endpoint.Url!, trackId, level, timeoutSeconds, cancellationToken),
                VipFallbackEndpointTypes.ZMusic => await ResolveViaZMusicAsync(client, endpoint.Url!, trackId, level, timeoutSeconds, cancellationToken),
                VipFallbackEndpointTypes.GdStudio => await ResolveViaGdStudioAsync(client, endpoint.Url!, trackId, level, timeoutSeconds, cancellationToken),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Netease VIP fallback error via {Type} for track {Id}", endpoint.Type, trackId);
            return null;
        }
    }

    /// <summary>
    /// nextmusic.toubiec.cn：POST 两步。
    /// 1) POST {BaseUrl}/api/ip  body={timestamp} → data.ip
    /// 2) POST {BaseUrl}/api/getSongUrl  body={id,level,timestamp,ip} → data.url
    /// 需 Origin/Referer 头指向 wyapi.toubiec.cn。
    /// </summary>
    private static async Task<string?> ResolveViaNextMusicAsync(
        HttpClient client, string baseUrl, string trackId, string level,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>
        {
            ["Origin"] = "https://wyapi.toubiec.cn",
            ["Referer"] = "https://wyapi.toubiec.cn/",
        };
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Step 1: get IP
        var ip = await PostJsonAsync<string?>(client, $"{baseUrl.TrimEnd('/')}/api/ip",
            new { timestamp = ts }, headers, timeoutSeconds,
            doc => doc.RootElement.TryGetProperty("data", out var d) ? GetString(d, "ip") : null,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(ip)) return null;

        // Step 2: get song url
        return await PostJsonAsync<string?>(client, $"{baseUrl.TrimEnd('/')}/api/getSongUrl",
            new { id = trackId, level, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ip },
            headers, timeoutSeconds,
            doc =>
            {
                if (!doc.RootElement.TryGetProperty("data", out var d)) return null;
                var url = GetString(d, "url");
                return !string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
            },
            cancellationToken);
    }

    /// <summary>haitangw 等 GET 型端点：URL 含 id/level 占位符，响应 data.url。</summary>
    private static async Task<string?> ResolveViaGetDataUrlAsync(
        HttpClient client, string urlTemplate, string trackId, string level,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var url = urlTemplate
            .Replace("{id}", Uri.EscapeDataString(trackId))
            .Replace("{level}", Uri.EscapeDataString(level));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await client.GetAsync(url, cts.Token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
        var fallbackUrl = GetString(dataEl, "url");
        return !string.IsNullOrWhiteSpace(fallbackUrl) && fallbackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? fallbackUrl : null;
    }

    /// <summary>GD Studio GET 型端点：URL 含 {id}/{level}（br）占位符，响应根级 url（br=999 返回无损 flac）。</summary>
    private static async Task<string?> ResolveViaGdStudioAsync(
        HttpClient client, string urlTemplate, string trackId, string level,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var url = urlTemplate
            .Replace("{id}", Uri.EscapeDataString(trackId))
            .Replace("{level}", Uri.EscapeDataString(level));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await client.GetAsync(url, cts.Token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        var fallbackUrl = GetString(doc.RootElement, "url");
        return !string.IsNullOrWhiteSpace(fallbackUrl) && fallbackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? fallbackUrl : null;
    }

    /// <summary>ZMusic 兼容官方格式：GET /song/url，参数 id 与 level，响应 data[0].url。</summary>
    private static async Task<string?> ResolveViaZMusicAsync(
        HttpClient client, string urlTemplate, string trackId, string level,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var url = urlTemplate
            .Replace("{id}", Uri.EscapeDataString(trackId))
            .Replace("{level}", Uri.EscapeDataString(level));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await client.GetAsync(url, cts.Token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array || dataEl.GetArrayLength() == 0)
            return null;
        var fallbackUrl = GetString(dataEl[0], "url");
        return !string.IsNullOrWhiteSpace(fallbackUrl) && fallbackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? fallbackUrl : null;
    }

    /// <summary>带自定义头的 POST JSON 请求，解析函数从 JsonDocument 提取结果。</summary>
    private static async Task<T?> PostJsonAsync<T>(
        HttpClient client, string url, object body, Dictionary<string, string> headers,
        int timeoutSeconds, Func<JsonDocument, T?> parser, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);
        request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode) return default;
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        return parser(doc);
    }

    /// <summary>根据 level 与端点类型推断码率/容器。nextmusic 的 level 是网易云命名（standard/exhigh/lossless）。</summary>
    private static (int Bitrate, bool IsLossless, string Container) InferQuality(string level, string endpointType)
    {
        return level.ToLowerInvariant() switch
        {
            "lossless" or "flac" or "999" => (900000, true, "flac"),
            "exhigh" or "320" or "740" => (320000, false, "mp3"),
            "standard" or "192" => (192000, false, "mp3"),
            _ => (128000, false, "mp3"),
        };
    }

    /// <summary>解析搜索结果条目（artists/album/duration 全字段）。</summary>
    private SourceSearchResult? ParseTrackFromSearch(JsonElement song)
    {
        var id = GetNumberAsString(song, "id");
        var title = GetString(song, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var artists = new List<string>();
        if (song.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistArray.EnumerateArray())
            {
                var name = GetString(artist, "name");
                if (!string.IsNullOrWhiteSpace(name)) artists.Add(name.Trim());
            }
        }

        string? albumName = null;
        string? coverUrl = null;
        if (song.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = GetString(album, "name");
            coverUrl = GetString(album, "picUrl");
        }

        var durationMs = GetLong(song, "duration");
        var fee = GetInt(song, "fee") ?? 0;
        return new SourceSearchResult(
            Provider,
            id,
            title.Trim(),
            artists,
            albumName,
            durationMs,
            Genres: [],
            coverUrl,
            SourcePageUrl: $"https://music.163.com/song?id={id}",
            License: DescribeFee(fee),
            Quality: SourceQualities.High,
            IsLosslessAvailable: false,
            ReleaseYear: SourceReleaseYearParser.FromJson(album, "publishTime", "releaseDate")
                ?? SourceReleaseYearParser.FromJson(song, "publishTime", "releaseDate"));
    }

    /// <summary>解析详情条目（缩写字段 ar/al/dt，封面 picUrl 更完整）。</summary>
    private SourceSearchResult? ParseTrackFromDetail(JsonElement song)
    {
        var id = GetNumberAsString(song, "id");
        var title = GetString(song, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var artists = new List<string>();
        if (song.TryGetProperty("ar", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistArray.EnumerateArray())
            {
                var name = GetString(artist, "name");
                if (!string.IsNullOrWhiteSpace(name)) artists.Add(name.Trim());
            }
        }

        string? albumName = null;
        string? coverUrl = null;
        if (song.TryGetProperty("al", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = GetString(album, "name");
            coverUrl = GetString(album, "picUrl");
        }

        var durationMs = GetLong(song, "dt");
        var fee = GetInt(song, "fee") ?? 0;
        return new SourceSearchResult(
            Provider,
            id,
            title.Trim(),
            artists,
            albumName,
            durationMs,
            Genres: [],
            coverUrl,
            SourcePageUrl: $"https://music.163.com/song?id={id}",
            License: DescribeFee(fee),
            Quality: SourceQualities.High,
            IsLosslessAvailable: false,
            ReleaseYear: SourceReleaseYearParser.FromJson(album, "publishTime", "releaseDate")
                ?? SourceReleaseYearParser.FromJson(song, "publishTime", "releaseDate"));
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
                logger.LogWarning("Netease request failed: {StatusCode} {Url}", (int)response.StatusCode, url);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Netease request error: {Url}", url);
            return null;
        }
    }

    private string SearchBase() => options.Value.Netease.SearchBaseUrl.TrimEnd('/');
    private string DetailBase() => options.Value.Netease.DetailBaseUrl.TrimEnd('/');
    private string PlayerUrlBase() => options.Value.Netease.PlayerUrlBaseUrl.TrimEnd('/');

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// 把网易云的 fee 字段翻译成可读的版权说明（仅用于展示与失败文案，不参与「是否下载」的判定）。
    /// 常见取值：0 免费；1 会员；4 数字专辑需购买；8 低音质免费、高音质需会员。
    /// 注意 fee=8 并非 VIP 限制，直接写成「VIP」会误导。
    /// </summary>
    private static string? DescribeFee(int fee) => fee switch
    {
        0 => null,
        1 => "网易云会员专享（fee=1）",
        4 => "网易云数字专辑，需购买（fee=4）",
        8 => "低音质免费、高音质需会员（fee=8）",
        _ => $"网易云版权受限（fee={fee}）",
    };

    /// <summary>数字或字符串形态的 Id 统一转字符串。</summary>
    private static string? GetNumberAsString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }
}
