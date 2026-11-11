// 职责：汽水音乐（Qishui / Luna）音源 provider（HTTP 直链型）。
// 端点（2026-11 实测，均无需 Cookie/签名）：
//   1. 搜索：api.qishui.com/luna/search/track —— 需 Android 客户端 UA + 完整设备参数（aid=386088），
//      返回 result_groups[].data[].entity.track（结构与应用内 track 一致）；
//   2. 详情/元数据：beta-luna.douyin.com/luna/h5/seo_track?track_id=... —— 分享页 SEO 数据源，
//      返回 seo_track.track（曲名/艺人/专辑/时长毫秒/封面）、歌词与 track_player（音频取流信息）；
//   3. 取流：track_player.video_model（内嵌 JSON）或 track_player.url_player_info（VOD GetPlayInfo）。
// 局限：匿名请求对 VIP/付费曲目只下发「试听片段」（实测 30~60s），完整音频需要官方客户端
//      应用级签名（x-helios/x-medusa）或会员鉴权。因此这里对「片段」直接判定不可下载，
//      由歌单导入/下载编排层换源到其他平台，避免把预览片段当成整曲入库。
// 可选 Cookie（SourceDownload:Qishui:Cookie）：登录态请求随附，供后续解锁完整流使用。

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>汽水音乐音源提供者。</summary>
public sealed class QishuiSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider
{
    /// <summary>provider 自身请求（搜索/详情/取流）使用的命名客户端。</summary>
    public const string HttpClientName = "qishui-source";

    /// <summary>搜索接口要求的 Android 客户端 UA（换成浏览器 UA 会被判空搜索）。</summary>
    private const string AndroidUserAgent =
        "com.luna.music/100198030 (Linux; U; Android 15; zh_CN_#Hans; ABR-AL80; Build/V417IR;tt-ok/3.12.13.19)";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private const string PageReferer = "https://www.qishui.com/";

    /// <summary>seo_track 快照缓存（详情与取流共用一次抓取，避免同一任务重复下载 ~180KB 页面）。</summary>
    private readonly ConcurrentDictionary<string, (QishuiTrackSnapshot Snapshot, DateTime ExpiresUtc)> _snapshotCache = new();
    private const int SnapshotCacheSeconds = 30;

    /// <inheritdoc />
    public string Provider => "qishui";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Qishui.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "汽水音乐音源未启用（SourceDownload:Qishui:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var count = Math.Clamp(limit, 1, 30);
        var url = $"{options.Value.Qishui.SearchBaseUrl.TrimEnd('/')}/track?q={Uri.EscapeDataString(query)}"
            + $"&cursor=0&count={count}&{BuildSearchDeviceParams()}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", AndroidUserAgent);
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("result_groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SourceSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("data", out var entries) || entries.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("entity", out var entity)
                    || !entity.TryGetProperty("track", out var track)
                    || track.ValueKind != JsonValueKind.Object)
                    continue;
                var id = GetValueAsString(track, "id");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                    continue;
                var result = ParseTrack(track, id);
                if (result is not null)
                    results.Add(result);
            }
        }
        return results.Count > count ? results.Take(count).ToArray() : results.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var snapshot = await FetchSnapshotAsync(providerTrackId, cancellationToken);
        return snapshot is null ? null : ToSearchResult(snapshot);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 匿名接口对 VIP/付费曲目只返回 30~60 秒试听片段：这里用「音频时长 vs 曲目元数据时长」判定，
    /// 片段直接抛 <see cref="SourceUnavailableException"/>，让上层换源，避免预览片段入库。
    /// </remarks>
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var snapshot = await FetchSnapshotAsync(providerTrackId, cancellationToken)
            ?? throw new SourceUnavailableException("汽水音乐未返回可解析的曲目数据。");

        var audio = PickBestAudio(snapshot.Audios);
        if (audio is null && !string.IsNullOrWhiteSpace(snapshot.PlayerInfoUrl))
            audio = PickBestAudio(await FetchPlayerInfoAsync(snapshot.PlayerInfoUrl, cancellationToken));

        if (audio is null)
            throw new SourceUnavailableException("汽水音乐未返回可下载的音频地址（该曲目可能已下架或无版权）。");

        if (IsPreview(snapshot.DurationMs, audio.DurationSeconds))
        {
            throw new SourceUnavailableException(
                $"汽水音乐匿名接口仅返回试听片段（{audio.DurationSeconds:F0}s / {snapshot.DurationMs / 1000:F0}s），"
                + "VIP/付费曲目完整音频需官方客户端签名，可由其他音源换源下载。");
        }

        return new SourceDownloadInfo(
            audio.Url,
            audio.Container,
            IsLossless: false,
            audio.BitrateKbps,
            SourceQualities.Standard);
    }

    /// <inheritdoc />
    public async Task<bool> CanDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        try
        {
            await ResolveDownloadAsync(providerTrackId, cancellationToken);
            return true;
        }
        catch (SourceUnavailableException)
        {
            return false;
        }
        catch
        {
            return true; // 网络异常时保守放行，不误伤可下载曲目
        }
    }

    /// <summary>
    /// 抓取 seo_track 快照（元数据 + 内嵌 video_model 取流列表）。
    /// 命中 30 秒进程内缓存；请求失败/格式异常返回 null（由调用方降级）。
    /// </summary>
    private async Task<QishuiTrackSnapshot?> FetchSnapshotAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        if (_snapshotCache.TryGetValue(providerTrackId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Snapshot;

        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{options.Value.Qishui.SeoTrackBaseUrl.TrimEnd('/')}?track_id={Uri.EscapeDataString(providerTrackId)}"
            + "&aid=386088&device_platform=web&channel=pc_web";

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Referer", PageReferer);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var track = FindTrackElement(root);
            if (track is null)
                return null;
            var trackElement = track.Value;

            var id = GetValueAsString(trackElement, "id") ?? providerTrackId;
            var title = GetString(trackElement, "name");
            if (string.IsNullOrWhiteSpace(title))
                return null;

            long? durationMs = GetLong(trackElement, "duration") is { } duration && duration > 0 ? duration : null;
            var albumTitle = trackElement.TryGetProperty("album", out var album) ? GetString(album, "name") : null;
            var snapshot = new QishuiTrackSnapshot(
                id,
                title.Trim(),
                albumTitle,
                durationMs,
                BuildCoverUrl(trackElement),
                ReadArtists(trackElement),
                trackElement.TryGetProperty("album", out var albumElement)
                    ? SourceReleaseYearParser.FromJson(albumElement, "release_date")
                    : null,
                ParseVideoModel(root),
                root.TryGetProperty("track_player", out var player) ? GetString(player, "url_player_info") : null);

            _snapshotCache[providerTrackId] = (snapshot, DateTime.UtcNow.AddSeconds(SnapshotCacheSeconds));
            return snapshot;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 seo_track 响应中定位曲目对象（标准为 seo_track.track，兼容 track/track_info 平铺）。</summary>
    private static JsonElement? FindTrackElement(JsonElement root)
    {
        if (root.TryGetProperty("seo_track", out var seo)
            && seo.TryGetProperty("track", out var seoTrack)
            && seoTrack.ValueKind == JsonValueKind.Object)
            return seoTrack;
        if (root.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
            return track;
        if (root.TryGetProperty("track_info", out var trackInfo) && trackInfo.ValueKind == JsonValueKind.Object)
            return trackInfo;
        return null;
    }

    /// <summary>解析 track_player.video_model（可能是内嵌 JSON 字符串）里的取流地址列表。</summary>
    private static IReadOnlyList<QishuiAudioCandidate> ParseVideoModel(JsonElement root)
    {
        if (!root.TryGetProperty("track_player", out var player)
            || !player.TryGetProperty("video_model", out var model))
            return [];

        JsonDocument? parsed = null;
        try
        {
            JsonElement modelElement;
            if (model.ValueKind == JsonValueKind.String)
            {
                var raw = model.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    return [];
                parsed = JsonDocument.Parse(raw);
                modelElement = parsed.RootElement;
            }
            else if (model.ValueKind == JsonValueKind.Object)
            {
                modelElement = model;
            }
            else
            {
                return [];
            }

            var audios = new List<QishuiAudioCandidate>();
            if (modelElement.TryGetProperty("video_list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    var url = GetString(entry, "main_url") ?? GetString(entry, "backup_url");
                    if (string.IsNullOrWhiteSpace(url))
                        continue;
                    audios.Add(new QishuiAudioCandidate(
                        url,
                        GetDouble(modelElement, "video_duration"),
                        ParseBitrateKbps(url),
                        ResolveContainer(url)));
                }
            }
            return audios;
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            parsed?.Dispose();
        }
    }

    /// <summary>请求 VOD GetPlayInfo（url_player_info）并解析 PlayInfoList。</summary>
    private async Task<IReadOnlyList<QishuiAudioCandidate>> FetchPlayerInfoAsync(
        string playerInfoUrl,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, playerInfoUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Referer", PageReferer);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("Result", out var result)
                || !result.TryGetProperty("Data", out var data)
                || !data.TryGetProperty("PlayInfoList", out var list)
                || list.ValueKind != JsonValueKind.Array)
                return [];

            var audios = new List<QishuiAudioCandidate>();
            foreach (var entry in list.EnumerateArray())
            {
                var url = GetString(entry, "MainPlayUrl") ?? GetString(entry, "BackupPlayUrl");
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                var bitrateBps = GetLong(entry, "Bitrate");
                audios.Add(new QishuiAudioCandidate(
                    url,
                    GetDouble(entry, "Duration"),
                    bitrateBps is > 0 ? (int)(bitrateBps.Value / 1000) : null,
                    ResolveContainer(url, GetString(entry, "Format"))));
            }
            return audios;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>在候选里选码率最高的一条（同码率优先 m4a）。</summary>
    private static QishuiAudioCandidate? PickBestAudio(IReadOnlyList<QishuiAudioCandidate> audios)
    {
        if (audios.Count == 0)
            return null;
        return audios
            .OrderByDescending(audio => audio.BitrateKbps ?? 0)
            .ThenBy(audio => string.Equals(audio.Container, "m4a", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();
    }

    /// <summary>片段判定：音频时长明显短于曲目元数据时长（90% 为阈值，留出编码误差余量）。</summary>
    private static bool IsPreview(long? fullDurationMs, double? audioDurationSeconds)
    {
        if (fullDurationMs is not { } full || full <= 0 || audioDurationSeconds is not { } audio || audio <= 0)
            return false;
        return audio * 1000 < full * 0.9;
    }

    /// <summary>从曲目对象构造统一搜索结果。</summary>
    private SourceSearchResult? ParseTrack(JsonElement track, string id)
    {
        var metadata = ToMetadata(track, id);
        if (metadata is null)
            return null;
        return new SourceSearchResult(
            Provider,
            id,
            metadata.Title,
            metadata.Artists,
            metadata.AlbumTitle,
            metadata.DurationMs,
            [],
            metadata.CoverUrl,
            $"https://www.qishui.com/track/{id}",
            License: null,
            SourceQualities.Standard,
            IsLosslessAvailable: false,
            ReleaseYear: metadata.ReleaseYear);
    }

    private SourceSearchResult ToSearchResult(QishuiTrackSnapshot snapshot) =>
        new(
            Provider,
            snapshot.Id,
            snapshot.Title,
            snapshot.Artists,
            snapshot.AlbumTitle,
            snapshot.DurationMs,
            [],
            snapshot.CoverUrl,
            $"https://www.qishui.com/track/{snapshot.Id}",
            License: null,
            SourceQualities.Standard,
            IsLosslessAvailable: false,
            ReleaseYear: snapshot.ReleaseYear);

    private static QishuiTrackMetadata? ToMetadata(JsonElement track, string id)
    {
        var title = GetString(track, "name");
        if (string.IsNullOrWhiteSpace(title))
            return null;
        return new QishuiTrackMetadata(
            title.Trim(),
            ReadArtists(track),
            track.TryGetProperty("album", out var album) ? GetString(album, "name") : null,
            GetLong(track, "duration") is { } duration && duration > 0 ? duration : null,
            BuildCoverUrl(track),
            track.TryGetProperty("album", out var albumElement)
                ? SourceReleaseYearParser.FromJson(albumElement, "release_date")
                : null);
    }

    private static List<string> ReadArtists(JsonElement track)
    {
        var artists = new List<string>();
        if (!track.TryGetProperty("artists", out var values) || values.ValueKind != JsonValueKind.Array)
            return artists;
        foreach (var value in values.EnumerateArray())
        {
            var name = GetString(value, "name");
            if (!string.IsNullOrWhiteSpace(name))
                artists.Add(name.Trim());
        }
        return artists;
    }

    /// <summary>按汽水图片模板拼接封面（urls[0] + uri + 裁剪模板，与歌单解析器一致）。</summary>
    private static string? BuildCoverUrl(JsonElement track)
    {
        if (!track.TryGetProperty("album", out var album)
            || !album.TryGetProperty("url_cover", out var cover)
            || !cover.TryGetProperty("urls", out var urls)
            || urls.ValueKind != JsonValueKind.Array
            || urls.GetArrayLength() == 0)
            return null;
        var baseUrl = urls[0].ValueKind == JsonValueKind.String ? urls[0].GetString() : null;
        var uri = GetString(cover, "uri");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(uri))
            return null;
        var template = GetString(cover, "template_prefix");
        var suffix = string.IsNullOrWhiteSpace(template) ? string.Empty : $"~{template}-crop-center:375:375.jpg";
        return $"{baseUrl.TrimEnd('/')}/{uri}{suffix}";
    }

    /// <summary>容器：优先 VOD 声明的 Format，缺省按 mime_type/URL 推断。</summary>
    private static string ResolveContainer(string url, string? declaredFormat = null)
    {
        var format = declaredFormat?.Trim().TrimStart('.').ToLowerInvariant();
        if (format is "m4a" or "mp3" or "flac" or "aac" or "ogg")
            return format;

        try
        {
            var mime = new Uri(url).Query
                .Split('&')
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2
                    && string.Equals(pair[0], "mime_type", StringComparison.OrdinalIgnoreCase))?[1];
            if (mime is not null && Uri.UnescapeDataString(mime).Contains("audio_mp4", StringComparison.OrdinalIgnoreCase))
                return "m4a";
        }
        catch (UriFormatException)
        {
            // URL 非法时走兜底扩展名。
        }
        return "m4a";
    }

    /// <summary>从抖音 CDN 直链的 br 查询参数解析码率（单位 kbps）。</summary>
    private static int? ParseBitrateKbps(string url)
    {
        try
        {
            var query = new Uri(url).Query;
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2 || !string.Equals(pair[0], "br", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (int.TryParse(pair[1], out var bitrate) && bitrate > 0)
                    return bitrate;
            }
        }
        catch (UriFormatException)
        {
            // 忽略非法 URL。
        }
        return null;
    }

    /// <summary>搜索接口需要的 Android 设备参数（缺 aid/设备号时接口返回 empty_search）。</summary>
    private static string BuildSearchDeviceParams()
    {
        var ticket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return string.Join('&',
        [
            "device_platform=android",
            "os=android",
            "ssmix=a",
            "cdid=46556f98-1720-4248-83da-62b74b60b46a",
            "channel=xiaomi_8478_64",
            "app_name=luna",
            "version_code=100198030",
            "version_name=19.8.0",
            "manifest_version_code=100198030",
            "update_version_code=100198030",
            "resolution=1080*1920",
            "dpi=480",
            "device_type=ABR-AL80",
            "device_brand=HUAWEI",
            "language=zh",
            "os_api=35",
            "os_version=15",
            "ac=wifi",
            "device_model=ABR-AL80",
            "package=com.luna.music",
            "iid=2204957404569386",
            "device_id=2204957404565290",
            $"_rticket={ticket}",
            "tz_name=Asia/Shanghai",
            "tz_offset=28800",
            "aid=386088",
            "battery=0.99",
            "network_speed=10156",
            "hybrid_version_code=100198030",
            "luna_first_launch_apk_type=normal_apk",
            "diversion_channel_name=xiaomi_8478_64",
            "is_car_play=0",
            "charge=0",
            "luna_apk_type=normal_apk",
            "output_device_type=Phone",
            "volume=1.00",
            "brightness=0.08",
            "need_personal_recommend=1",
            "is_teen_mode=0",
            "sim_region=cn",
            "android_device_type=default",
            "save_power=0",
            "font_size=1.00",
        ]);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? GetValueAsString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }

    /// <summary>seo_track 快照：统一元数据 + 音频候选（DownloadUrl 由 video_model/url_player_info 解析）。</summary>
    private sealed record QishuiTrackSnapshot(
        string Id,
        string Title,
        string? AlbumTitle,
        long? DurationMs,
        string? CoverUrl,
        IReadOnlyList<string> Artists,
        int? ReleaseYear,
        IReadOnlyList<QishuiAudioCandidate> Audios,
        string? PlayerInfoUrl);

    private sealed record QishuiAudioCandidate(
        string Url,
        double? DurationSeconds,
        int? BitrateKbps,
        string Container);

    private sealed record QishuiTrackMetadata(
        string Title,
        IReadOnlyList<string> Artists,
        string? AlbumTitle,
        long? DurationMs,
        string? CoverUrl,
        int? ReleaseYear);
}
