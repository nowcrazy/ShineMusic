// 职责：千千音乐（91q，原百度音乐）音源 provider（HTTP 直链型）。
// 端点链（2026-11 实测可用，musicdl qianqian.py 同源实现；旧 music.taihe.com/v1/restserver/ting 已废弃 errno 22001）：
//   1. 搜索：music.91q.com/v1/search?type=1 —— errno==22000 且 state==true 为成功，结果在 data.typeTrack[]
//      （TSID/title/artist[].name/albumTitle/pic/duration/releaseDate/isVip）；
//   2. 详情/下载：music.91q.com/v1/song/tracklink —— 同一签名算法，rate 逐档 3000(flac)→320→128→64(aac)，
//      下载地址取 data.path（空则 data.trail_audio_info.path），data 同时携带单曲完整元数据。
// 签名：所有参数（值不做 URL 编码）按 key 字典序（Ordinal）拼 k=v&k=v，末尾直接追加 secret，UTF-8 MD5 小写 hex；
//      实际 URL query 中的 word 需单独编码。请求头需桌面 Chrome UA + Referer + 自定义头 from:web。
// 局限：匿名态 VIP 曲目（data.isVip==1）不返回下载地址 → 判定不可下载（抛 SourceUnavailableException 换源兜底）。

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>千千音乐（91q）音源提供者。</summary>
public sealed class QianqianSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称（与 Provider 同名：下载管线 CreateClient(task.SourceProvider) 复用同一配置）。</summary>
    public const string HttpClientName = "qianqian";

    private const string SearchBase = "https://music.91q.com/v1/search";
    private const string TracklinkBase = "https://music.91q.com/v1/song/tracklink";
    private const string AppId = "16073360";
    private const string Secret = "0b50b02fd0d73a9c4c8c3a781c30845f";

    /// <summary>码率阶梯：3000=flac 无损 → 320 → 128 → 64=aac；单次解析取最高可用档。</summary>
    private static readonly int[] RateLadder = [3000, 320, 128, 64];

    /// <inheritdoc />
    public string Provider => "qianqian";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Qianqian.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "千千音乐音源未启用（SourceDownload:Qianqian:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var pageSize = Math.Clamp(limit, 1, 50).ToString(CultureInfo.InvariantCulture);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appid"] = AppId,
            ["pageNo"] = "1",
            ["pageSize"] = pageSize,
            ["timestamp"] = timestamp,
            ["type"] = "1",
            ["word"] = query,
        };
        var url = $"{SearchBase}?appid={AppId}&pageNo=1&pageSize={pageSize}&timestamp={timestamp}&type=1"
            + $"&word={Uri.EscapeDataString(query)}&sign={Sign(parameters)}";
        var raw = await client.GetStringAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!IsSuccess(root)
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("typeTrack", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SourceSearchResult>();
        foreach (var entry in list.EnumerateArray())
        {
            var tsid = GetString(entry, "TSID");
            var title = GetString(entry, "title");
            if (string.IsNullOrWhiteSpace(tsid) || string.IsNullOrWhiteSpace(title))
                continue;
            var durationSeconds = GetLong(entry, "duration");
            results.Add(new SourceSearchResult(
                Provider,
                tsid,
                title,
                ParseArtists(entry),
                GetString(entry, "albumTitle"),
                durationSeconds > 0 ? durationSeconds * 1000 : null,
                [],
                GetString(entry, "pic"),
                $"https://music.91q.com/player?songid={Uri.EscapeDataString(tsid)}",
                License: null,
                SourceQualities.Standard,
                IsLosslessAvailable: false,
                ReleaseYear: SourceReleaseYearParser.FromJson(entry, "releaseDate", "pushTime")));
            if (results.Count >= limit)
                break;
        }
        return results.ToArray();
    }

    /// <inheritdoc />
    /// <remarks>tracklink 的 data 段携带单曲完整元数据（title/artist/albumTitle/pic/duration/releaseDate），一次请求即得快照。</remarks>
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var data = await FetchTracklinkAsync(providerTrackId, 128, cancellationToken);
        if (data is null || string.IsNullOrWhiteSpace(data.Value.Title))
            return null;
        return new SourceSearchResult(
            Provider,
            providerTrackId,
            data.Value.Title!,
            data.Value.Artists,
            data.Value.Album,
            data.Value.DurationMs,
            [],
            data.Value.Cover,
            $"https://music.91q.com/player?songid={Uri.EscapeDataString(providerTrackId)}",
            null,
            SourceQualities.Standard,
            IsLosslessAvailable: false,
            ReleaseYear: data.Value.ReleaseYear);
    }

    /// <inheritdoc />
    /// <remarks>按 3000→320→128→64 逐档探测，取首个非 VIP 且 path 非空的档位；isVip==1 各档一致，直接判定不可下载。</remarks>
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var sawVip = false;
        foreach (var rate in RateLadder)
        {
            var data = await FetchTracklinkAsync(providerTrackId, rate, cancellationToken);
            if (data is null)
                continue;
            if (data.Value.IsVip)
            {
                sawVip = true;
                break; // VIP 按曲目生效，各码率档一致，无需继续降档。
            }
            var url = FirstNonEmpty(data.Value.Path, data.Value.TrailPath);
            if (url is null)
                continue;
            var (isLossless, quality) = rate switch
            {
                >= 3000 => (true, SourceQualities.Lossless),
                >= 320 => (false, SourceQualities.High),
                _ => (false, SourceQualities.Standard)
            };
            var container = string.IsNullOrWhiteSpace(data.Value.Format)
                ? (isLossless ? "flac" : "mp3")
                : data.Value.Format!.TrimStart('.').ToLowerInvariant();
            var bitrate = data.Value.BitrateKbps > 0 ? data.Value.BitrateKbps : rate;
            return new SourceDownloadInfo(url, container, isLossless, bitrate, quality);
        }
        if (sawVip)
            throw new SourceUnavailableException("千千音乐该曲目为 VIP，匿名接口不返回下载地址。");
        throw new SourceUnavailableException("千千音乐未返回可下载资源（该曲目可能无版权或已下架）。");
    }

    /// <inheritdoc />
    /// <remarks>轻量预判：探测 rate=128 的 tracklink 是否返回非空 path。超时/异常时保守放行。</remarks>
    public async Task<bool> CanDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        try
        {
            var data = await FetchTracklinkAsync(providerTrackId, 128, cancellationToken);
            return data is not null && !data.Value.IsVip
                && FirstNonEmpty(data.Value.Path, data.Value.TrailPath) is not null;
        }
        catch
        {
            return true; // 超时/异常时保守放行
        }
    }

    /// <summary>调 v1/song/tracklink：返回 data 段快照；HTTP/JSON 失败或 errno 非成功码返回 null。</summary>
    private async Task<TracklinkData?> FetchTracklinkAsync(string tsid, int rate, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TSID"] = tsid,
            ["appid"] = AppId,
            ["rate"] = rate.ToString(CultureInfo.InvariantCulture),
            ["timestamp"] = timestamp,
        };
        var url = $"{TracklinkBase}?TSID={Uri.EscapeDataString(tsid)}&appid={AppId}&rate={rate.ToString(CultureInfo.InvariantCulture)}"
            + $"&timestamp={timestamp}&sign={Sign(parameters)}";
        string raw;
        try
        {
            raw = await client.GetStringAsync(url, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!IsSuccess(root)
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            string? trailPath = null;
            if (data.TryGetProperty("trail_audio_info", out var trail) && trail.ValueKind == JsonValueKind.Object)
                trailPath = GetString(trail, "path");
            var durationSeconds = GetLong(data, "duration");
            return new TracklinkData(
                GetString(data, "path"),
                trailPath,
                GetInt(data, "isVip") == 1,
                GetString(data, "format"),
                GetInt(data, "rate"),
                GetString(data, "title"),
                ParseArtists(data),
                GetString(data, "albumTitle"),
                GetString(data, "pic"),
                durationSeconds > 0 ? durationSeconds * 1000 : null,
                SourceReleaseYearParser.FromJson(data, "releaseDate", "pushTime"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>91q v1 签名：参数（值不做 URL 编码）按 key 字典序（Ordinal）拼 k=v&amp;k=v，末尾追加 secret，UTF-8 MD5 小写 hex。</summary>
#pragma warning disable CA5351 // MD5 为 91q 接口签名规范所要求，非安全用途
    private static string Sign(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var builder = new StringBuilder();
        foreach (var pair in parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 0)
                builder.Append('&');
            builder.Append(pair.Key).Append('=').Append(pair.Value);
        }
        builder.Append(Secret);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
#pragma warning restore CA5351

    /// <summary>成功判定：errno==22000 且 state==true。</summary>
    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("errno", out var errno) && errno.ValueKind == JsonValueKind.Number && errno.GetInt32() == 22000
        && root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.True;

    /// <summary>解析 artist 数组为艺人名列表。</summary>
    private static IReadOnlyList<string> ParseArtists(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("artist", out var artists) || artists.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<string>();
        foreach (var artist in artists.EnumerateArray())
        {
            var name = GetString(artist, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());
        }
        return names;
    }

    /// <summary>取首个非空 URL。</summary>
    private static string? FirstNonEmpty(params string?[] urls) =>
        urls.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

    /// <summary>从 JsonElement 上安全取字符串字段。</summary>
    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>从 JsonElement 上安全取整数字段（兼容字符串数字）。</summary>
    private static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
                _ => 0
            }
            : 0;

    /// <summary>从 JsonElement 上安全取长整数字段（兼容字符串数字）。</summary>
    private static long GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
                _ => 0
            }
            : 0;

    /// <summary>tracklink 响应 data 段快照（播放地址 + 单曲元数据）。</summary>
    private readonly record struct TracklinkData(
        string? Path,
        string? TrailPath,
        bool IsVip,
        string? Format,
        int BitrateKbps,
        string? Title,
        IReadOnlyList<string> Artists,
        string? Album,
        string? Cover,
        long? DurationMs,
        int? ReleaseYear);
}
